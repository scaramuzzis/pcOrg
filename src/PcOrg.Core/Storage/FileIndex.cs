using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using PcOrg.Core.Classification;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Indexing;
using PcOrg.Core.Metadata;

namespace PcOrg.Core.Storage;

/// <summary>
/// Indice dei file su SQLite, con ricerca full-text (FTS5, tokenizer trigram) su nome e cartella.
/// Ogni metodo apre una propria connessione: la classe si può usare da più thread.
/// </summary>
public sealed partial class FileIndex
{
    private const int SchemaVersion = 6;
    private readonly string _connectionString;

    /// <summary>Percorso completo del file dell'indice (indice.db).</summary>
    public string DatabasePath { get; }

    private FileIndex(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
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

        Log.Info($"Aggiornamento dell'indice dalla versione {version} alla {SchemaVersion} ({DatabasePath}).");
        using var tx = conn.BeginTransaction();
        if (version < 1)
            CreateSchemaV1(conn, tx);
        if (version < 2)
            MigrateToV2(conn, tx);
        if (version < 3)
            Reclassify(conn, tx, FileCategory.Documento);
        if (version < 4)
            Reclassify(conn, tx, FileCategory.Altro); // nuovi formati: Ghost, Acronis, Macrium, dischi virtuali...
        if (version < 5)
        {
            CreateDirectoryTable(conn, tx);
            Reclassify(conn, tx, FileCategory.Altro); // nuova categoria Accesso remoto (.vnc, .rdp)
        }
        if (version < 6)
            CreateHashTable(conn, tx);
        Exec(conn, $"PRAGMA user_version = {SchemaVersion};", tx);
        tx.Commit();
    }

    private static void CreateSchemaV1(SqliteConnection conn, SqliteTransaction tx)
    {
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
    }

    /// <summary>
    /// Ricalcola la categoria dei file di una categoria quando cambiano le regole
    /// (v3: Documenti divisi in PDF, Word, Testo...; v4: nuovi formati di immagini disco).
    /// </summary>
    private static void Reclassify(SqliteConnection conn, SqliteTransaction tx, FileCategory from)
    {
        var changes = new List<(long Id, FileCategory Category)>();
        using (var select = Command(conn, $"SELECT id, path FROM files WHERE category = {(int)from};", tx))
        using (var r = select.ExecuteReader())
            while (r.Read())
                changes.Add((r.GetInt64(0), FileClassifier.Classify(r.GetString(1))));

        using var update = Command(conn, "UPDATE files SET category = $cat WHERE id = $id;", tx);
        var pCat = update.Parameters.Add("$cat", SqliteType.Integer);
        var pId = update.Parameters.Add("$id", SqliteType.Integer);
        foreach (var (id, category) in changes.Where(c => c.Category != from))
        {
            pCat.Value = (int)category;
            pId.Value = id;
            update.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// v2: metadati (versione, produttore, firma...) e indice di ricerca esteso a prodotto e produttore.
    /// L'indice full-text diventa una tabella autonoma, aggiornata dai trigger di files e file_meta.
    /// </summary>
    private static void MigrateToV2(SqliteConnection conn, SqliteTransaction tx)
    {
        Exec(conn, """
            CREATE TABLE file_meta (
                file_id          INTEGER PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE,
                scanned_size     INTEGER NOT NULL,
                scanned_mtime    INTEGER NOT NULL,
                product_name     TEXT,
                company          TEXT,
                description      TEXT,
                file_version     TEXT,
                product_version  TEXT,
                original_name    TEXT,
                arch             INTEGER NOT NULL DEFAULT 0,
                signer           TEXT,
                signature        INTEGER NOT NULL DEFAULT 0,
                installer_type   TEXT,
                product_key      TEXT
            );
            CREATE INDEX ix_meta_product ON file_meta(product_key);

            DROP TRIGGER files_ai;
            DROP TRIGGER files_ad;
            DROP TRIGGER files_au;
            DROP TABLE files_fts;

            CREATE VIRTUAL TABLE files_fts USING fts5(name, folder, product, company, tokenize='trigram');
            INSERT INTO files_fts(rowid, name, folder, product, company) SELECT id, name, folder, '', '' FROM files;

            CREATE TRIGGER files_ai AFTER INSERT ON files BEGIN
                INSERT INTO files_fts(rowid, name, folder, product, company) VALUES (new.id, new.name, new.folder, '', '');
            END;
            CREATE TRIGGER files_ad AFTER DELETE ON files BEGIN
                DELETE FROM files_fts WHERE rowid = old.id;
            END;
            CREATE TRIGGER files_au AFTER UPDATE OF name, folder ON files BEGIN
                UPDATE files_fts SET name = new.name, folder = new.folder WHERE rowid = new.id;
            END;
            CREATE TRIGGER meta_ai AFTER INSERT ON file_meta BEGIN
                UPDATE files_fts SET product = COALESCE(new.product_name, ''), company = COALESCE(new.company, '')
                WHERE rowid = new.file_id;
            END;
            CREATE TRIGGER meta_au AFTER UPDATE ON file_meta BEGIN
                UPDATE files_fts SET product = COALESCE(new.product_name, ''), company = COALESCE(new.company, '')
                WHERE rowid = new.file_id;
            END;
            CREATE TRIGGER meta_ad AFTER DELETE ON file_meta BEGIN
                UPDATE files_fts SET product = '', company = '' WHERE rowid = old.file_id;
            END;
            """, tx);
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
        var normalized = PathUtil.Normalize(NetworkPaths.ToUnc(path));
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
                Exec(conn, $"UPDATE dirs SET root_id = {id} WHERE root_id = {child.Id};", tx);
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

    /// <summary>Numero di file salvati per volta durante una scansione.</summary>
    public const int ScanBatchSize = 2000;

    /// <summary>
    /// Allinea l'indice di una cartella monitorata con i file trovati su disco:
    /// aggiunge i nuovi, aggiorna i modificati, rimuove quelli spariti.
    /// </summary>
    /// <remarks>
    /// I file trovati vengono salvati a blocchi di <see cref="ScanBatchSize"/>: se la scansione viene
    /// interrotta (pulsante Interrompi, chiusura dell'app, errore) quello che è già stato trovato resta
    /// nell'indice e la scansione successiva riparte da lì. I file spariti vengono eliminati solo alla
    /// fine di una scansione <b>completa</b>, così una scansione parziale non cancella mai nulla.
    /// Ogni scansione usa un nuovo numero di "generazione" (colonna <c>seen</c>): alla fine si eliminano
    /// i file della cartella che non sono stati visti in questa generazione.
    /// </remarks>
    /// <param name="isStillAvailable">
    /// Controllato prima di eliminare i file non trovati: se la cartella (es. di rete) è sparita durante la
    /// scansione, l'elenco potrebbe essere incompleto e i file non visti restano nell'indice.
    /// </param>
    /// <returns>Numero di file presenti nella cartella.</returns>
    public int SyncRoot(long rootId, IEnumerable<ScannedFile> files, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default, Func<bool>? isStillAvailable = null)
    {
        using var conn = Connect();
        var generation = Convert.ToInt64(
            Scalar(conn, "SELECT COALESCE(MAX(seen), 0) + 1 FROM files;"), CultureInfo.InvariantCulture);

        var tx = conn.BeginTransaction();
        var upsert = CreateUpsert(conn, tx, rootId, generation);
        var count = 0;
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                upsert.Parameters["$path"].Value = file.Path;
                upsert.Parameters["$name"].Value = Path.GetFileName(file.Path);
                upsert.Parameters["$ext"].Value = Path.GetExtension(file.Path).ToLowerInvariant();
                upsert.Parameters["$folder"].Value = Path.GetDirectoryName(file.Path) ?? "";
                upsert.Parameters["$size"].Value = file.Size;
                upsert.Parameters["$mod"].Value = ToUnixSeconds(file.ModifiedUtc);
                upsert.Parameters["$cat"].Value = (int)FileClassifier.Classify(file.Path);
                upsert.ExecuteNonQuery();

                if (++count % ScanBatchSize == 0)
                {
                    // Salvataggio intermedio: i file trovati finora sopravvivono a un'interruzione.
                    tx.Commit();
                    upsert.Dispose();
                    tx.Dispose();
                    tx = conn.BeginTransaction();
                    upsert = CreateUpsert(conn, tx, rootId, generation);
                }
                if (count % 500 == 0)
                    progress?.Report(count);
            }
            progress?.Report(count);

            if (isStillAvailable is not null && !isStillAvailable())
            {
                tx.Commit(); // si tengono i file trovati, ma non si elimina nulla
                throw new IOException("La cartella non è più raggiungibile: i file non trovati restano nell'indice.");
            }

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
        catch (OperationCanceledException)
        {
            // Interruzione richiesta: si salva il blocco in corso e si rilancia.
            tx.Commit();
            throw;
        }
        finally
        {
            upsert.Dispose();
            tx.Dispose();
        }
    }

    /// <summary>Comando preparato per inserire o aggiornare un file durante la scansione.</summary>
    private static SqliteCommand CreateUpsert(SqliteConnection conn, SqliteTransaction tx, long rootId, long generation)
    {
        var upsert = Command(conn, """
            INSERT INTO files(root_id, path, name, ext, folder, size, modified_utc, category, seen)
            VALUES ($root, $path, $name, $ext, $folder, $size, $mod, $cat, $seen)
            ON CONFLICT(path) DO UPDATE SET
                root_id = excluded.root_id, size = excluded.size, modified_utc = excluded.modified_utc,
                -- se il file non è cambiato si mantiene la categoria (eventualmente affinata dai metadati)
                category = CASE WHEN files.size = excluded.size AND files.modified_utc = excluded.modified_utc
                                THEN files.category ELSE excluded.category END,
                seen = excluded.seen;
            """, tx);
        upsert.Parameters.Add("$root", SqliteType.Integer).Value = rootId;
        upsert.Parameters.Add("$seen", SqliteType.Integer).Value = generation;
        foreach (var name in new[] { "$path", "$name", "$ext", "$folder" })
            upsert.Parameters.Add(name, SqliteType.Text);
        foreach (var name in new[] { "$size", "$mod", "$cat" })
            upsert.Parameters.Add(name, SqliteType.Integer);
        upsert.Prepare();
        return upsert;
    }

    // ------------------------------------------------------------------ Manutenzione

    /// <summary>Riepilogo dell'indice per la finestra Impostazioni e il registro.</summary>
    public IndexStats GetStats()
    {
        using var conn = Connect();
        long Count(string sql) => Convert.ToInt64(Scalar(conn, sql), CultureInfo.InvariantCulture);
        var size = 0L;
        foreach (var suffix in new[] { "", "-wal" })
            if (File.Exists(DatabasePath + suffix))
                size += new FileInfo(DatabasePath + suffix).Length;
        return new IndexStats(Count("SELECT COUNT(*) FROM files;"), Count("SELECT COUNT(*) FROM roots;"),
            Count("SELECT COUNT(*) FROM move_batches;"), DatabasePath, size);
    }

    /// <summary>Riduce lo spazio occupato dal file dell'indice (utile dopo aver rimosso molte cartelle).</summary>
    public void Compact()
    {
        using var conn = Connect();
        Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE);");
        Exec(conn, "VACUUM;");
    }

    /// <summary>
    /// Svuota l'elenco dei file (e i relativi metadati) mantenendo cartelle monitorate e cronologia:
    /// alla scansione successiva l'indice viene ricostruito da zero. I file su disco non vengono toccati.
    /// </summary>
    public void ClearFiles()
    {
        using var conn = Connect();
        using var tx = conn.BeginTransaction();
        Exec(conn, "DELETE FROM files; DELETE FROM dirs; UPDATE roots SET last_scan_utc = NULL;", tx);
        tx.Commit();
    }

    // ------------------------------------------------------------------ Ricerca

    public IReadOnlyList<FileRecord> Search(SearchQuery query)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        var sql = new StringBuilder(SelectFiles);
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
            where.Add($"(f.name LIKE $like{i} ESCAPE '\\' OR f.folder LIKE $like{i} ESCAPE '\\' OR m.product_name LIKE $like{i} ESCAPE '\\')");
            cmd.Parameters.AddWithValue($"$like{i}", "%" + EscapeLike(likeTerms[i]) + "%");
        }
        if (query.Extensions is { Count: > 0 } extensions)
        {
            var names = new List<string>();
            foreach (var (ext, i) in extensions.Select((e, i) => (e, i)))
            {
                names.Add($"$ext{i}");
                cmd.Parameters.AddWithValue($"$ext{i}", ext.ToLowerInvariant());
            }
            where.Add($"f.ext IN ({string.Join(", ", names)})");
        }
        if (query.Category is { } category)
        {
            where.Add("f.category = $cat");
            cmd.Parameters.AddWithValue("$cat", (int)category);
        }

        if (where.Count > 0)
            sql.Append(" WHERE ").Append(string.Join(" AND ", where));
        sql.Append(match is not null ? " ORDER BY bm25(files_fts, 10.0, 1.0, 5.0, 3.0), f.name" : " ORDER BY f.name COLLATE NOCASE");
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

    /// <summary>Numero di file e spazio per estensione all'interno di una categoria.</summary>
    public IReadOnlyList<ExtensionStat> GetExtensionStats(FileCategory category)
    {
        using var conn = Connect();
        using var cmd = Command(conn, "SELECT ext, COUNT(*), COALESCE(SUM(size), 0) FROM files WHERE category = $cat GROUP BY ext;");
        cmd.Parameters.AddWithValue("$cat", (int)category);
        using var r = cmd.ExecuteReader();
        var list = new List<ExtensionStat>();
        while (r.Read())
            list.Add(new ExtensionStat(r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        return list;
    }

    public FileRecord? GetFile(long id)
    {
        using var conn = Connect();
        using var cmd = Command(conn, SelectFiles + " WHERE f.id = $id;");
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadFile(r) : null;
    }

    // ------------------------------------------------------------------ Metadati

    /// <summary>File da cui leggere (o rileggere, se modificati) versione, produttore e firma.</summary>
    public IReadOnlyList<PendingMetadata> GetFilesNeedingMetadata(int limit = 500)
    {
        var extensions = string.Join(", ", MetadataExtractor.SupportedExtensions.Select(e => $"'{e}'"));
        using var conn = Connect();
        using var cmd = Command(conn, $"""
            SELECT f.id, f.path, f.size, f.modified_utc FROM files f
            LEFT JOIN file_meta m ON m.file_id = f.id
            WHERE f.ext IN ({extensions})
              AND (m.file_id IS NULL OR m.scanned_size <> f.size OR m.scanned_mtime <> f.modified_utc)
            ORDER BY f.id LIMIT $n;
            """);
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<PendingMetadata>();
        while (r.Read())
            list.Add(new PendingMetadata(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3)));
        return list;
    }

    public int CountFilesNeedingMetadata()
    {
        var extensions = string.Join(", ", MetadataExtractor.SupportedExtensions.Select(e => $"'{e}'"));
        using var conn = Connect();
        return Convert.ToInt32(Scalar(conn, $"""
            SELECT COUNT(*) FROM files f LEFT JOIN file_meta m ON m.file_id = f.id
            WHERE f.ext IN ({extensions})
              AND (m.file_id IS NULL OR m.scanned_size <> f.size OR m.scanned_mtime <> f.modified_utc);
            """), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Salva i metadati letti. Un file illeggibile viene salvato senza dati, così non lo si rilegge
    /// finché non cambia. La categoria viene affinata in base ai metadati.
    /// </summary>
    public void SaveMetadata(IEnumerable<(PendingMetadata File, FileMeta? Meta)> items)
    {
        using var conn = Connect();
        using var tx = conn.BeginTransaction();
        using var upsert = Command(conn, """
            INSERT INTO file_meta(file_id, scanned_size, scanned_mtime, product_name, company, description, file_version,
                                  product_version, original_name, arch, signer, signature, installer_type, product_key)
            VALUES ($id, $size, $mtime, $product, $company, $desc, $fver, $pver, $orig, $arch, $signer, $sig, $inst, $key)
            ON CONFLICT(file_id) DO UPDATE SET
                scanned_size = excluded.scanned_size, scanned_mtime = excluded.scanned_mtime,
                product_name = excluded.product_name, company = excluded.company, description = excluded.description,
                file_version = excluded.file_version, product_version = excluded.product_version,
                original_name = excluded.original_name, arch = excluded.arch, signer = excluded.signer,
                signature = excluded.signature, installer_type = excluded.installer_type, product_key = excluded.product_key;
            """, tx);
        using var category = Command(conn, "UPDATE files SET category = $cat WHERE id = $id AND category <> $cat;", tx);
        using var current = Command(conn, "SELECT category FROM files WHERE id = $id;", tx);

        foreach (var (file, meta) in items)
        {
            upsert.Parameters.Clear();
            upsert.Parameters.AddWithValue("$id", file.Id);
            upsert.Parameters.AddWithValue("$size", file.Size);
            upsert.Parameters.AddWithValue("$mtime", file.ModifiedUnix);
            upsert.Parameters.AddWithValue("$product", (object?)meta?.ProductName ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$company", (object?)meta?.CompanyName ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$desc", (object?)meta?.FileDescription ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$fver", (object?)meta?.FileVersion ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$pver", (object?)meta?.ProductVersion ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$orig", (object?)meta?.OriginalFilename ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$arch", (int)(meta?.Architecture ?? Architecture.Sconosciuta));
            upsert.Parameters.AddWithValue("$signer", (object?)meta?.Signer ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$sig", (int)(meta?.SignatureStatus ?? SignatureStatus.NonFirmato));
            upsert.Parameters.AddWithValue("$inst", (object?)meta?.InstallerType ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$key", (object?)meta?.ProductKey ?? DBNull.Value);
            upsert.ExecuteNonQuery();

            if (meta is null)
                continue;
            current.Parameters.Clear();
            current.Parameters.AddWithValue("$id", file.Id);
            if (current.ExecuteScalar() is not long existing)
                continue;
            var refined = FileClassifier.Refine((FileCategory)existing, meta);
            category.Parameters.Clear();
            category.Parameters.AddWithValue("$id", file.Id);
            category.Parameters.AddWithValue("$cat", (int)refined);
            category.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>File con una chiave prodotto (exe, msi, sys), per costruire l'elenco dei prodotti.</summary>
    public IReadOnlyList<FileRecord> GetProductFiles()
    {
        using var conn = Connect();
        using var cmd = Command(conn, SelectFiles + " WHERE m.product_key IS NOT NULL;");
        using var r = cmd.ExecuteReader();
        var list = new List<FileRecord>();
        while (r.Read())
            list.Add(ReadFile(r));
        return list;
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
                UPDATE files SET path = $to, name = $name, ext = $ext, folder = $folder, root_id = $root
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

    private const string SelectFiles = """
        SELECT f.id, f.root_id, f.path, f.name, f.ext, f.folder, f.size, f.modified_utc, f.category,
               m.file_id, m.product_name, m.company, m.description, m.file_version, m.product_version,
               m.original_name, m.arch, m.signer, m.signature, m.installer_type, m.product_key
        FROM files f LEFT JOIN file_meta m ON m.file_id = f.id
        """;

    private static FileRecord ReadFile(SqliteDataReader r)
    {
        string? Str(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        var meta = r.IsDBNull(9) ? null : new FileMeta
        {
            ProductName = Str(10),
            CompanyName = Str(11),
            FileDescription = Str(12),
            FileVersion = Str(13),
            ProductVersion = Str(14),
            OriginalFilename = Str(15),
            Architecture = (Architecture)r.GetInt32(16),
            Signer = Str(17),
            SignatureStatus = (SignatureStatus)r.GetInt32(18),
            InstallerType = Str(19),
            ProductKey = Str(20),
        };
        return new FileRecord(
            r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
            r.GetInt64(6), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(7)).UtcDateTime, (FileCategory)r.GetInt32(8),
            meta);
    }

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
