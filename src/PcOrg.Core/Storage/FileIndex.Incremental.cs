using System.Globalization;
using Microsoft.Data.Sqlite;
using PcOrg.Core.Classification;
using PcOrg.Core.Diagnostics;

namespace PcOrg.Core.Storage;

/// <summary>Riepilogo di un aggiornamento (rapido o completo) o dell'applicazione di modifiche rilevate.</summary>
public sealed record ScanResult(
    int DirectoriesVisited,
    int DirectoriesChanged,
    int FilesAdded,
    int FilesUpdated,
    int FilesRemoved)
{
    public int FilesChanged => FilesAdded + FilesUpdated + FilesRemoved;

    public static ScanResult operator +(ScanResult a, ScanResult b) => new(
        a.DirectoriesVisited + b.DirectoriesVisited, a.DirectoriesChanged + b.DirectoriesChanged,
        a.FilesAdded + b.FilesAdded, a.FilesUpdated + b.FilesUpdated, a.FilesRemoved + b.FilesRemoved);

    public static ScanResult Empty { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>
/// Aggiornamento incrementale dell'indice: invece di rileggere ogni file, si rilegge solo il contenuto
/// delle cartelle che sono cambiate.
/// </summary>
/// <remarks>
/// <para><b>Come funziona.</b> Per ogni cartella l'indice ricorda la data di ultima modifica (tabella
/// <c>dirs</c>). Windows aggiorna questa data quando nella cartella viene <i>aggiunto, eliminato o
/// rinominato</i> un file o una sottocartella. All'aggiornamento:</para>
/// <list type="number">
/// <item>si legge la data di ogni cartella conosciuta (un'operazione leggera per cartella, non per file);</item>
/// <item>se la data non è cambiata, il suo elenco di file è lo stesso: non la si apre e si prosegue con
/// le sottocartelle già note;</item>
/// <item>se è cambiata (o è nuova), se ne legge il contenuto e si confronta con l'indice, scrivendo solo le
/// differenze (file nuovi, modificati, spariti);</item>
/// <item>le cartelle conosciute che non esistono più vengono tolte, con i loro file.</item>
/// </list>
/// <para>Con milioni di file distribuiti in migliaia di cartelle, un aggiornamento senza modifiche
/// richiede solo qualche secondo. <b>Limite:</b> la modifica del <i>contenuto</i> di un file esistente
/// non cambia la data della cartella; viene rilevata dall'osservatore delle cartelle mentre l'app è
/// aperta (<see cref="Indexing.FolderWatcher"/>) oppure con la scansione completa
/// (<paramref name="full"/> = true).</para>
/// </remarks>
public sealed partial class FileIndex
{
    /// <summary>Cartelle elaborate fra un salvataggio intermedio e il successivo.</summary>
    private const int DirectoryBatchSize = 200;

    private static readonly EnumerationOptions ListOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Tabella delle cartelle conosciute con la loro data di modifica (schema v5).</summary>
    private static void CreateDirectoryTable(SqliteConnection conn, SqliteTransaction tx) => Exec(conn, """
        CREATE TABLE dirs (
            id      INTEGER PRIMARY KEY,
            root_id INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
            path    TEXT NOT NULL UNIQUE COLLATE NOCASE,
            mtime   INTEGER NOT NULL
        );
        CREATE INDEX ix_dirs_root ON dirs(root_id);
        CREATE INDEX ix_files_folder ON files(folder COLLATE NOCASE);
        """, tx);

    /// <summary>
    /// Aggiorna una cartella monitorata (o una sua sottocartella <paramref name="startPath"/>).
    /// </summary>
    /// <param name="full">Rilegge tutte le cartelle anche se la loro data non è cambiata.</param>
    /// <param name="isStillAvailable">Controllato prima di eliminare le cartelle sparite (es. rete caduta).</param>
    public ScanResult UpdateRoot(long rootId, string startPath, bool full = false, IProgress<ScanResult>? progress = null,
        CancellationToken cancellationToken = default, Func<bool>? isStillAvailable = null)
    {
        startPath = PathUtil.Normalize(startPath);
        using var conn = Connect();

        // Cartelle già conosciute sotto il punto di partenza, con le relative sottocartelle.
        var known = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = Command(conn, "SELECT path, mtime FROM dirs WHERE root_id = $r;"))
        {
            cmd.Parameters.AddWithValue("$r", rootId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var path = r.GetString(0);
                if (PathUtil.IsSameOrUnder(path, startPath))
                    known[path] = r.GetInt64(1);
            }
        }
        var children = known.Keys
            .Where(p => !string.Equals(p, startPath, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => Path.GetDirectoryName(p) ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        stack.Push(startPath);
        int dirsVisited = 0, dirsChanged = 0, added = 0, updated = 0, removed = 0;
        ScanResult Current() => new(dirsVisited, dirsChanged, added, updated, removed);

        var tx = conn.BeginTransaction();
        var pending = 0;
        try
        {
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                if (!visited.Add(dir))
                    continue;
                dirsVisited++;

                void PushKnownChildren()
                {
                    if (children.TryGetValue(dir, out var list))
                        foreach (var child in list)
                            stack.Push(child);
                }

                long mtime;
                try
                {
                    if (!Directory.Exists(dir))
                    {
                        visited.Remove(dir); // sparita: verrà tolta come "non visitata"
                        continue;
                    }
                    mtime = ToUnixMilliseconds(Directory.GetLastWriteTimeUtc(dir));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    PushKnownChildren(); // non leggibile ora: si tiene quello che si sapeva
                    continue;
                }

                if (!full && known.TryGetValue(dir, out var oldTime) && oldTime == mtime)
                {
                    PushKnownChildren(); // contenuto invariato: niente da leggere
                    continue;
                }

                // Cartella nuova o cambiata: se ne legge il contenuto e lo si confronta con l'indice.
                List<FileInfo> files;
                List<string> subdirs;
                try
                {
                    var info = new DirectoryInfo(dir);
                    files = [];
                    subdirs = [];
                    foreach (var entry in info.EnumerateFileSystemInfos("*", ListOptions))
                    {
                        if (entry is FileInfo f)
                            files.Add(f);
                        else
                            subdirs.Add(entry.FullName);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Cartella non leggibile, saltata: {dir}", ex);
                    PushKnownChildren();
                    continue;
                }

                dirsChanged++;
                var delta = SyncFolder(conn, tx, rootId, dir, files);
                added += delta.Added;
                updated += delta.Updated;
                removed += delta.Removed;
                SaveDirectory(conn, tx, rootId, dir, mtime);
                foreach (var sub in subdirs)
                    stack.Push(sub);
                if (Log.IsEnabled(LogLevel.Debug) && delta.Added + delta.Updated + delta.Removed > 0)
                    Log.Debug($"Cartella aggiornata: {dir} (+{delta.Added} ~{delta.Updated} -{delta.Removed})");

                if (++pending >= DirectoryBatchSize)
                {
                    // Salvataggio intermedio: un'interruzione non fa perdere il lavoro fatto.
                    tx.Commit();
                    tx.Dispose();
                    tx = conn.BeginTransaction();
                    pending = 0;
                    progress?.Report(Current());
                }
                else if (dirsVisited % 500 == 0)
                {
                    progress?.Report(Current());
                }
            }

            if (isStillAvailable is not null && !isStillAvailable())
            {
                tx.Commit();
                throw new IOException("La cartella non è più raggiungibile: le cartelle non controllate restano nell'indice.");
            }

            // Cartelle conosciute non più presenti: si tolgono con i loro file.
            foreach (var stale in known.Keys.Where(k => !visited.Contains(k)))
            {
                removed += DeleteFolderFiles(conn, tx, rootId, stale);
                using var del = Command(conn, "DELETE FROM dirs WHERE path = $p;", tx);
                del.Parameters.AddWithValue("$p", stale);
                del.ExecuteNonQuery();
            }
            if (string.Equals(startPath, GetRootPath(conn, tx, rootId), StringComparison.OrdinalIgnoreCase))
            {
                using var stamp = Command(conn, "UPDATE roots SET last_scan_utc = $t WHERE id = $id;", tx);
                stamp.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                stamp.Parameters.AddWithValue("$id", rootId);
                stamp.ExecuteNonQuery();
            }
            tx.Commit();
            progress?.Report(Current());
            return Current();
        }
        catch (OperationCanceledException)
        {
            tx.Commit(); // si tiene quanto aggiornato finora
            throw;
        }
        finally
        {
            tx.Dispose();
        }
    }

    /// <summary>
    /// Applica le modifiche segnalate dall'osservatore delle cartelle: per ogni percorso si controlla
    /// cosa c'è ora su disco e si aggiorna solo quella voce (o quella sottocartella).
    /// </summary>
    public ScanResult ApplyChanges(IEnumerable<(long RootId, string Path)> changes, CancellationToken cancellationToken = default)
    {
        var total = ScanResult.Empty;
        // Si elabora ogni percorso una sola volta, partendo da quelli più in alto.
        foreach (var (rootId, path) in changes.DistinctBy(c => c.Path.ToUpperInvariant()).OrderBy(c => c.Path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (Directory.Exists(path))
                {
                    total += UpdateRoot(rootId, path, cancellationToken: cancellationToken);
                }
                else if (File.Exists(path))
                {
                    total += UpsertSingle(rootId, new FileInfo(path));
                }
                else
                {
                    total += RemovePath(rootId, path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Modifica non applicata a {path}.", ex);
            }
        }
        return total;
    }

    // ------------------------------------------------------------------ Operazioni elementari

    private readonly record struct FolderDelta(int Added, int Updated, int Removed);

    /// <summary>Allinea i file di una sola cartella con quanto trovato su disco, scrivendo solo le differenze.</summary>
    private static FolderDelta SyncFolder(SqliteConnection conn, SqliteTransaction tx, long rootId, string dir, List<FileInfo> files)
    {
        var existing = new Dictionary<string, (long Size, long Mtime)>(StringComparer.OrdinalIgnoreCase);
        using (var select = Command(conn, "SELECT path, size, modified_utc FROM files WHERE folder = $f COLLATE NOCASE;", tx))
        {
            select.Parameters.AddWithValue("$f", dir);
            using var r = select.ExecuteReader();
            while (r.Read())
                existing[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2));
        }

        int added = 0, updated = 0, removed = 0;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var upsert = UpsertCommand(conn, tx);
        foreach (var file in files)
        {
            present.Add(file.FullName);
            long size, mtime;
            try
            {
                size = file.Length;
                mtime = ToUnixSeconds(file.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (existing.TryGetValue(file.FullName, out var old))
            {
                if (old.Size == size && old.Mtime == mtime)
                    continue; // invariato: nessuna scrittura
                updated++;
            }
            else
            {
                added++;
            }
            BindAndRun(upsert, rootId, file.FullName, size, mtime);
        }

        foreach (var gone in existing.Keys.Where(p => !present.Contains(p)))
        {
            using var del = Command(conn, "DELETE FROM files WHERE path = $p;", tx);
            del.Parameters.AddWithValue("$p", gone);
            removed += del.ExecuteNonQuery();
        }
        return new FolderDelta(added, updated, removed);
    }

    private ScanResult UpsertSingle(long rootId, FileInfo file)
    {
        using var conn = Connect();
        using var tx = conn.BeginTransaction();
        bool exists;
        using (var check = Command(conn, "SELECT size, modified_utc FROM files WHERE path = $p;", tx))
        {
            check.Parameters.AddWithValue("$p", file.FullName);
            using var r = check.ExecuteReader();
            exists = r.Read();
            if (exists && r.GetInt64(0) == file.Length && r.GetInt64(1) == ToUnixSeconds(file.LastWriteTimeUtc))
                return ScanResult.Empty;
        }
        using var upsert = UpsertCommand(conn, tx);
        BindAndRun(upsert, rootId, file.FullName, file.Length, ToUnixSeconds(file.LastWriteTimeUtc));
        tx.Commit();
        return exists ? new ScanResult(0, 0, 0, 1, 0) : new ScanResult(0, 0, 1, 0, 0);
    }

    /// <summary>Toglie un file o un'intera cartella (con le sottocartelle) che non esiste più.</summary>
    private ScanResult RemovePath(long rootId, string path)
    {
        using var conn = Connect();
        using var tx = conn.BeginTransaction();
        var removed = 0;
        using (var file = Command(conn, "DELETE FROM files WHERE path = $p;", tx))
        {
            file.Parameters.AddWithValue("$p", path);
            removed += file.ExecuteNonQuery();
        }
        if (removed == 0)
        {
            removed += DeleteFolderFiles(conn, tx, rootId, path, includeSubfolders: true);
            using var dirs = Command(conn, "DELETE FROM dirs WHERE path = $p OR substr(path, 1, $n) = $prefix COLLATE NOCASE;", tx);
            dirs.Parameters.AddWithValue("$p", path);
            dirs.Parameters.AddWithValue("$n", path.Length + 1);
            dirs.Parameters.AddWithValue("$prefix", path + Path.DirectorySeparatorChar);
            dirs.ExecuteNonQuery();
        }
        tx.Commit();
        return new ScanResult(0, 0, 0, 0, removed);
    }

    private static int DeleteFolderFiles(SqliteConnection conn, SqliteTransaction tx, long rootId, string folder, bool includeSubfolders = false)
    {
        using var del = Command(conn, includeSubfolders
            ? "DELETE FROM files WHERE root_id = $r AND (folder = $f COLLATE NOCASE OR substr(folder, 1, $n) = $prefix COLLATE NOCASE);"
            : "DELETE FROM files WHERE root_id = $r AND folder = $f COLLATE NOCASE;", tx);
        del.Parameters.AddWithValue("$r", rootId);
        del.Parameters.AddWithValue("$f", folder);
        if (includeSubfolders)
        {
            del.Parameters.AddWithValue("$n", folder.Length + 1);
            del.Parameters.AddWithValue("$prefix", folder + Path.DirectorySeparatorChar);
        }
        return del.ExecuteNonQuery();
    }

    private static void SaveDirectory(SqliteConnection conn, SqliteTransaction tx, long rootId, string dir, long mtime)
    {
        using var cmd = Command(conn, """
            INSERT INTO dirs(root_id, path, mtime) VALUES ($r, $p, $m)
            ON CONFLICT(path) DO UPDATE SET root_id = excluded.root_id, mtime = excluded.mtime;
            """, tx);
        cmd.Parameters.AddWithValue("$r", rootId);
        cmd.Parameters.AddWithValue("$p", dir);
        cmd.Parameters.AddWithValue("$m", mtime);
        cmd.ExecuteNonQuery();
    }

    private static string? GetRootPath(SqliteConnection conn, SqliteTransaction tx, long rootId)
    {
        using var cmd = Command(conn, "SELECT path FROM roots WHERE id = $id;", tx);
        cmd.Parameters.AddWithValue("$id", rootId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Inserisce o aggiorna un file; se non è cambiato mantiene la categoria affinata dai metadati.</summary>
    private static SqliteCommand UpsertCommand(SqliteConnection conn, SqliteTransaction tx) => Command(conn, """
        INSERT INTO files(root_id, path, name, ext, folder, size, modified_utc, category, seen)
        VALUES ($root, $path, $name, $ext, $folder, $size, $mod, $cat, 0)
        ON CONFLICT(path) DO UPDATE SET
            root_id = excluded.root_id, size = excluded.size, modified_utc = excluded.modified_utc,
            category = CASE WHEN files.size = excluded.size AND files.modified_utc = excluded.modified_utc
                            THEN files.category ELSE excluded.category END;
        """, tx);

    private static void BindAndRun(SqliteCommand upsert, long rootId, string path, long size, long mtime)
    {
        upsert.Parameters.Clear();
        upsert.Parameters.AddWithValue("$root", rootId);
        upsert.Parameters.AddWithValue("$path", path);
        upsert.Parameters.AddWithValue("$name", Path.GetFileName(path));
        upsert.Parameters.AddWithValue("$ext", Path.GetExtension(path).ToLowerInvariant());
        upsert.Parameters.AddWithValue("$folder", Path.GetDirectoryName(path) ?? "");
        upsert.Parameters.AddWithValue("$size", size);
        upsert.Parameters.AddWithValue("$mod", mtime);
        upsert.Parameters.AddWithValue("$cat", (int)FileClassifier.Classify(path));
        upsert.ExecuteNonQuery();
    }

    /// <summary>Data delle cartelle in millisecondi: più precisa dei secondi usati per i file.</summary>
    private static long ToUnixMilliseconds(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
