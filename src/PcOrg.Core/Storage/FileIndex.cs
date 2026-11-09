using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PcOrg.Core.Classification;
using PcOrg.Core.Indexing;

namespace PcOrg.Core.Storage;

/// <summary>
/// Indice dei file su SQLite, con ricerca full-text (FTS5, tokenizer trigram) su nome e cartella.
/// Ogni metodo apre una propria connessione: la classe si può usare da più thread.
/// </summary>
public sealed class FileIndex
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;

    private FileIndex(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    public static FileIndex Open(string databasePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var index = new FileIndex(databasePath);
        index.Migrate();
        return index;
    }

    private SqliteConnection Connect()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        Exec(conn, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return conn;
    }

    private void Migrate()
    {
        using var conn = Connect();
        Exec(conn, "PRAGMA journal_mode = WAL;");
        var version = Convert.ToInt32(Scalar(conn, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version >= SchemaVersion)
            return;

        using var tx = conn.BeginTransaction();
        Exec(conn, """
            CREATE TABLE roots (
                id            INTEGER PRIMARY KEY,
                path          TEXT NOT NULL UNIQUE COLLATE NOCASE,
                last_scan_utc TEXT
            );
            CREATE TABLE files (
                id           INTEGER PRIMARY KEY,
                root_id      INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
                path         TEXT NOT NULL UNIQUE COLLATE NOCASE,
                name         TEXT NOT NULL,
                ext          TEXT NOT NULL,
                folder       TEXT NOT NULL,
                size         INTEGER NOT NULL,
                modified_utc INTEGER NOT NULL,
                category     INTEGER NOT NULL,
                seen         INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX ix_files_root ON files(root_id, seen);
            CREATE INDEX ix_files_category ON files(category);

            CREATE VIRTUAL TABLE files_fts USING fts5(
                name, folder, content='files', content_rowid='id', tokenize='trigram');
            CREATE TRIGGER files_ai AFTER INSERT ON files BEGIN
                INSERT INTO files_fts(rowid, name, folder) VALUES (new.id, new.name, new.folder);
            END;
            CREATE TRIGGER files_ad AFTER DELETE ON files BEGIN
                INSERT INTO files_fts(files_fts, rowid, name, folder) VALUES ('delete', old.id, old.name, old.folder);
            END;
            CREATE TRIGGER files_au AFTER UPDATE OF name, folder ON files BEGIN
                INSERT INTO files_fts(files_fts, rowid, name, folder) VALUES ('delete', old.id, old.name, old.folder);
                INSERT INTO files_fts(rowid, name, folder) VALUES (new.id, new.name, new.folder);
            END;

            CREATE TABLE move_batches (
                id          INTEGER PRIMARY KEY,
                created_utc TEXT NOT NULL,
                description TEXT NOT NULL,
                undone      INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE move_log (
                id        INTEGER PRIMARY KEY,
                batch_id  INTEGER NOT NULL REFERENCES move_batches(id) ON DELETE CASCADE,
                from_path TEXT NOT NULL,
                to_path   TEXT NOT NULL
            );
            """, tx);
        Exec(conn, $"PRAGMA user_version = {SchemaVersion};", tx);
        tx.Commit();
    }

    // ------------------------------------------------------------------ Cartelle monitorate

    public IReadOnlyList<RootFolder> GetRoots()
    {
        using var conn = Connect();
        using var cmd = Command(conn, "SELECT id, path, last_scan_utc FROM roots ORDER BY path;");
        using var r = cmd.ExecuteReader();
        var list = new List<RootFolder>();
        while (r.Read())
            list.Add(new RootFolder(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : ParseUtc(r.GetString(2))));
        return list;
    }

    /// <summary>Cartella monitorata che contiene il percorso indicato, se esiste.</summary>
    public RootFolder? FindRootFor(string path) =>
        GetRoots().Where(root => PathUtil.IsSameOrUnder(path, root.Path))
                  .MaxBy(root => root.Path.Length);

    /// <summary>
    /// Aggiunge una cartella da monitorare. Se è già contenuta in una cartella monitorata viene rifiutata;
    /// se invece contiene cartelle già monitorate, queste vengono assorbite nella nuova.
    /// </summary>
    public RootFolder AddRoot(string path)
    {
        var normalized = PathUtil.Normalize(path);
        if (!Directory.Exists(normalized))
            throw new DirectoryNotFoundException($"La cartella \"{normalized}\" non esiste.");

        var roots = GetRoots();
        var parent = roots.FirstOrDefault(root => PathUtil.IsSameOrUnder(normalized, root.Path));
        if (parent is not null)
            throw new InvalidOperationException(
                string.Equals(parent.Path, normalized, PathUtil.Comparison)
                    ? $"La cartella \"{normalized}\" è già monitorata."
                    : $"La cartella \"{normalized}\" è già inclusa in \"{parent.Path}\".");

        using var conn = Connect();
        using var tx = conn.BeginTransaction();
        using (var insert = Command(conn, "INSERT INTO roots(path) VALUES ($p) RETURNING id;", tx))
        {
            insert.Parameters.AddWithValue("$p", normalized);
            var id = (long)insert.ExecuteScalar()!;

            // Le cartelle figlie confluiscono nella nuova: i loro file passano sotto la nuova cartella.
            foreach (var child in roots.Where(root => PathUtil.IsSameOrUnder(root.Path, normalized)))
            {
                Exec(conn, $"UPDATE files SET root_id = {id} WHERE root_id = {child.Id};", tx);
                Exec(conn, $"DELETE FROM roots WHERE id = {child.Id};", tx);
            }
            tx.Commit();
            return new RootFolder(id, normalized, null);
        }
    }

    /// <summary>Smette di monitorare la cartella e rimuove i suoi file dall'indice (i file su disco non vengono toccati).</summary>
    public void RemoveRoot(long rootId)
    {
        using var conn = Connect();
        using var cmd = Command(conn, "DELETE FROM roots WHERE id = $id;");
        cmd.Parameters.AddWithValue("$id", rootId);
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ Scansione

    /// <summary>
    /// Allinea l'indice di una cartella monitorata con i file trovati su disco:
    /// aggiunge i nuovi, aggiorna i modificati, rimuove quelli spariti.
    /// </summary>
    /// <returns>Numero di file presenti nella cartella.</returns>
    public int SyncRoot(long rootId, IEnumerable<ScannedFile> files, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var conn = Connect();
        using var tx = conn.BeginTransaction();
        var generation = Convert.ToInt64(
            Scalar(conn, "SELECT COALESCE(MAX(seen), 0) + 1 FROM files;", tx), CultureInfo.InvariantCulture);

        using var upsert = Command(conn, """
            INSERT INTO files(root_id, path, name, ext, folder, size, modified_utc, category, seen)
            VALUES ($root, $path, $name, $ext, $folder, $size, $mod, $cat, $seen)
            ON CONFLICT(path) DO UPDATE SET
                root_id = excluded.root_id, size = excluded.size, modified_utc = excluded.modified_utc,
                category = excluded.category, seen = excluded.seen;
            """, tx);
        var pRoot = upsert.Parameters.Add("$root", SqliteType.Integer);
        var pPath = upsert.Parameters.Add("$path", SqliteType.Text);
        var pName = upsert.Parameters.Add("$name", SqliteType.Text);
        var pExt = upsert.Parameters.Add("$ext", SqliteType.Text);
        var pFolder = upsert.Parameters.Add("$folder", SqliteType.Text);
        var pSize = upsert.Parameters.Add("$size", SqliteType.Integer);
        var pMod = upsert.Parameters.Add("$mod", SqliteType.Integer);
        var pCat = upsert.Parameters.Add("$cat", SqliteType.Integer);
        var pSeen = upsert.Parameters.Add("$seen", SqliteType.Integer);
        upsert.Prepare();

        pRoot.Value = rootId;
        pSeen.Value = generation;
        var count = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pPath.Value = file.Path;
            pName.Value = Path.GetFileName(file.Path);
            pExt.Value = Path.GetExtension(file.Path).ToLowerInvariant();
            pFolder.Value = Path.GetDirectoryName(file.Path) ?? "";
            pSize.Value = file.Size;
            pMod.Value = ToUnixSeconds(file.ModifiedUtc);
            pCat.Value = (int)FileClassifier.Classify(file.Path);
            upsert.ExecuteNonQuery();

            if (++count % 500 == 0)
                progress?.Report(count);
        }
        progress?.Report(count);

        Exec(conn, $"DELETE FROM files WHERE root_id = {rootId} AND seen <> {generation};", tx);
        using (var stamp = Command(conn, "UPDATE roots SET last_scan_utc = $t WHERE id = $id;", tx))
        {
            stamp.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            stamp.Parameters.AddWithValue("$id", rootId);
            stamp.ExecuteNonQuery();
        }
        tx.Commit();
        return count;
    }

    // ------------------------------------------------------------------ Ricerca

    public IReadOnlyList<FileRecord> Search(SearchQuery query)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        var sql = new StringBuilder(
            "SELECT f.id, f.root_id, f.path, f.name, f.ext, f.folder, f.size, f.modified_utc, f.category FROM files f");
        var where = new List<string>();

        var (match, likeTerms) = BuildTextFilter(query.Text);
        if (match is not null)
        {
            sql.Append(" JOIN files_fts ON files_fts.rowid = f.id");
            where.Add("files_fts MATCH $match");
            cmd.Parameters.AddWithValue("$match", match);
        }
        for (var i = 0; i < likeTerms.Count; i++)
        {
            // Termini di 1-2 caratteri: il trigram non li indicizza, si filtra con LIKE.
            where.Add($"(f.name LIKE $like{i} ESCAPE '\\' OR f.folder LIKE $like{i} ESCAPE '\\')");
            cmd.Parameters.AddWithValue($"$like{i}", "%" + EscapeLike(likeTerms[i]) + "%");
        }
        if (query.Category is { } category)
        {
            where.Add("f.category = $cat");
            cmd.Parameters.AddWithValue("$cat", (int)category);
        }

        if (where.Count > 0)
            sql.Append(" WHERE ").Append(string.Join(" AND ", where));
        sql.Append(match is not null ? " ORDER BY bm25(files_fts, 10.0, 1.0), f.name" : " ORDER BY f.name COLLATE NOCASE");
        sql.Append(" LIMIT $limit;");
        cmd.Parameters.AddWithValue("$limit", query.Limit);
        cmd.CommandText = sql.ToString();

        using var r = cmd.ExecuteReader();
        var list = new List<FileRecord>();
        while (r.Read())
            list.Add(ReadFile(r));
        return list;
    }

    /// <summary>
    /// Trasforma il testo digitato in una query FTS5: ogni parola di almeno 3 caratteri deve comparire
    /// (in qualsiasi punto) nel nome o nella cartella. Le parole più corte vanno filtrate con LIKE.
    /// </summary>
    internal static (string? Match, List<string> LikeTerms) BuildTextFilter(string? text)
    {
        var likeTerms = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return (null, likeTerms);

        var phrases = new List<string>();
        foreach (var term in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (term.Length < 3)
                likeTerms.Add(term);
            else
                phrases.Add("\"" + term.Replace("\"", "\"\"") + "\"");
        }
        return (phrases.Count > 0 ? string.Join(" AND ", phrases) : null, likeTerms);
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public IReadOnlyList<CategoryStat> GetCategoryStats()
    {
        using var conn = Connect();
        using var cmd = Command(conn, "SELECT category, COUNT(*), COALESCE(SUM(size), 0) FROM files GROUP BY category;");
        using var r = cmd.ExecuteReader();
        var list = new List<CategoryStat>();
        while (r.Read())
            list.Add(new CategoryStat((FileCategory)r.GetInt32(0), r.GetInt64(1), r.GetInt64(2)));
        return list;
    }

    public FileRecord? GetFile(long id)
    {
        using var conn = Connect();
        using var cmd = Command(conn,
            "SELECT id, root_id, path, name, ext, folder, size, modified_utc, category FROM files WHERE id = $id;");
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadFile(r) : null;
    }

    // ------------------------------------------------------------------ Spostamenti

    /// <summary>
    /// Registra nell'indice che un file è stato spostato. Se la destinazione è fuori dalle cartelle
    /// monitorate il file esce dall'indice.
    /// </summary>
    public void ApplyMove(string fromPath, string toPath)
    {
        var root = FindRootFor(toPath);
        using var conn = Connect();
        using var tx = conn.BeginTransaction();

        using (var clear = Command(conn, "DELETE FROM files WHERE path = $to;", tx))
        {
            clear.Parameters.AddWithValue("$to", toPath);
            clear.ExecuteNonQuery();
        }

        using var cmd = root is null
            ? Command(conn, "DELETE FROM files WHERE path = $from;", tx)
            : Command(conn, """
                UPDATE files SET path = $to, name = $name, ext = $ext, folder = $folder, root_id = $root, category = $cat
                WHERE path = $from;
                """, tx);
        cmd.Parameters.AddWithValue("$from", fromPath);
        if (root is not null)
        {
            cmd.Parameters.AddWithValue("$to", toPath);
            cmd.Parameters.AddWithValue("$name", Path.GetFileName(toPath));
            cmd.Parameters.AddWithValue("$ext", Path.GetExtension(toPath).ToLowerInvariant());
            cmd.Parameters.AddWithValue("$folder", Path.GetDirectoryName(toPath) ?? "");
            cmd.Parameters.AddWithValue("$root", root.Id);
            cmd.Parameters.AddWithValue("$cat", (int)FileClassifier.Classify(toPath));
        }
        var updated = cmd.ExecuteNonQuery();

        // Il file non era nell'indice (es. torna da una cartella non monitorata): lo si aggiunge.
        if (root is not null && updated == 0 && File.Exists(toPath))
        {
            var info = new FileInfo(toPath);
            using var insert = Command(conn, """
                INSERT INTO files(root_id, path, name, ext, folder, size, modified_utc, category, seen)
                VALUES ($root, $to, $name, $ext, $folder, $size, $mod, $cat,
                        (SELECT COALESCE(MAX(seen), 0) FROM files WHERE root_id = $root));
                """, tx);
            insert.Parameters.AddWithValue("$root", root.Id);
            insert.Parameters.AddWithValue("$to", toPath);
            insert.Parameters.AddWithValue("$name", info.Name);
            insert.Parameters.AddWithValue("$ext", info.Extension.ToLowerInvariant());
            insert.Parameters.AddWithValue("$folder", info.DirectoryName ?? "");
            insert.Parameters.AddWithValue("$size", info.Length);
            insert.Parameters.AddWithValue("$mod", ToUnixSeconds(info.LastWriteTimeUtc));
            insert.Parameters.AddWithValue("$cat", (int)FileClassifier.Classify(toPath));
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public long CreateMoveBatch(string description)
    {
        using var conn = Connect();
        using var cmd = Command(conn, "INSERT INTO move_batches(created_utc, description) VALUES ($t, $d) RETURNING id;");
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$d", description);
        return (long)cmd.ExecuteScalar()!;
    }

    public void LogMove(long batchId, string fromPath, string toPath)
    {
        using var conn = Connect();
        using var cmd = Command(conn, "INSERT INTO move_log(batch_id, from_path, to_path) VALUES ($b, $f, $t);");
        cmd.Parameters.AddWithValue("$b", batchId);
        cmd.Parameters.AddWithValue("$f", fromPath);
        cmd.Parameters.AddWithValue("$t", toPath);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<MoveBatch> GetMoveBatches(int limit = 50)
    {
        using var conn = Connect();
        using var cmd = Command(conn, """
            SELECT b.id, b.created_utc, b.description, b.undone, (SELECT COUNT(*) FROM move_log l WHERE l.batch_id = b.id)
            FROM move_batches b ORDER BY b.id DESC LIMIT $n;
            """);
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<MoveBatch>();
        while (r.Read())
            list.Add(new MoveBatch(r.GetInt64(0), ParseUtc(r.GetString(1)), r.GetString(2), r.GetInt32(4), r.GetInt64(3) != 0));
        return list;
    }

    public IReadOnlyList<MoveLogEntry> GetMoveLog(long batchId)
    {
        using var conn = Connect();
        using var cmd = Command(conn, "SELECT id, batch_id, from_path, to_path FROM move_log WHERE batch_id = $b ORDER BY id;");
        cmd.Parameters.AddWithValue("$b", batchId);
        using var r = cmd.ExecuteReader();
        var list = new List<MoveLogEntry>();
        while (r.Read())
            list.Add(new MoveLogEntry(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    public void MarkBatchUndone(long batchId)
    {
        using var conn = Connect();
        Exec(conn, $"UPDATE move_batches SET undone = 1 WHERE id = {batchId};");
    }

    // ------------------------------------------------------------------ Helper

    private static FileRecord ReadFile(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.GetInt64(6), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(7)).UtcDateTime, (FileCategory)r.GetInt32(8));

    private static long ToUnixSeconds(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static DateTime ParseUtc(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static SqliteCommand Command(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private static void Exec(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = Command(conn, sql, tx);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = Command(conn, sql, tx);
        return cmd.ExecuteScalar();
    }
}
