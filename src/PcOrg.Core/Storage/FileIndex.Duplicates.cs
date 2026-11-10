using Microsoft.Data.Sqlite;

namespace PcOrg.Core.Storage;

/// <summary>File candidato alla ricerca dei cloni, con le impronte già calcolate (se ancora valide).</summary>
public sealed record HashCandidate(long Id, string Path, long Size, long ModifiedUnix, string? PartialHash, string? FullHash);

/// <summary>
/// Supporto alla ricerca dei cloni: candidati per dimensione e memoria delle impronte (hash).
/// Le impronte valgono finché dimensione e data del file non cambiano, così la seconda ricerca
/// non rilegge i file già confrontati.
/// </summary>
public sealed partial class FileIndex
{
    /// <summary>Tabella delle impronte (schema v6) e indice per dimensione.</summary>
    private static void CreateHashTable(SqliteConnection conn, SqliteTransaction tx) => Exec(conn, """
        CREATE TABLE file_hash (
            file_id INTEGER PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE,
            size    INTEGER NOT NULL,
            mtime   INTEGER NOT NULL,
            partial TEXT,
            full    TEXT
        );
        CREATE INDEX ix_files_size ON files(size);
        """, tx);

    /// <summary>
    /// File che hanno almeno un altro file con la stessa dimensione (gli unici che possono essere cloni),
    /// con le impronte memorizzate se il file non è cambiato nel frattempo.
    /// </summary>
    /// <param name="minSize">Dimensione minima in byte (i file più piccoli vengono ignorati).</param>
    /// <param name="includeRoot">Filtro sulle cartelle monitorate (es. escludere quelle di rete).</param>
    public IReadOnlyList<HashCandidate> GetDuplicateCandidates(long minSize, Func<RootFolder, bool>? includeRoot = null)
    {
        var roots = GetRoots().Where(r => includeRoot?.Invoke(r) ?? true).Select(r => r.Id).ToList();
        if (roots.Count == 0)
            return [];
        using var conn = Connect();
        using var cmd = Command(conn, $"""
            SELECT f.id, f.path, f.size, f.modified_utc,
                   CASE WHEN h.size = f.size AND h.mtime = f.modified_utc THEN h.partial END,
                   CASE WHEN h.size = f.size AND h.mtime = f.modified_utc THEN h.full END
            FROM files f
            LEFT JOIN file_hash h ON h.file_id = f.id
            WHERE f.size >= $min AND f.root_id IN ({string.Join(",", roots)})
              AND f.size IN (SELECT size FROM files WHERE size >= $min AND root_id IN ({string.Join(",", roots)})
                             GROUP BY size HAVING COUNT(*) > 1)
            ORDER BY f.size DESC;
            """);
        cmd.Parameters.AddWithValue("$min", Math.Max(1, minSize));
        using var r = cmd.ExecuteReader();
        var list = new List<HashCandidate>();
        while (r.Read())
            list.Add(new HashCandidate(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    /// <summary>Memorizza le impronte calcolate (si mantengono quelle già note se il nuovo valore è null).</summary>
    public void SaveHashes(IEnumerable<HashCandidate> items)
    {
        using var conn = Connect();
        using var tx = conn.BeginTransaction();
        using var cmd = Command(conn, """
            INSERT INTO file_hash(file_id, size, mtime, partial, full) VALUES ($id, $size, $mtime, $partial, $full)
            ON CONFLICT(file_id) DO UPDATE SET
                size = excluded.size, mtime = excluded.mtime,
                partial = COALESCE(excluded.partial, CASE WHEN file_hash.size = excluded.size AND file_hash.mtime = excluded.mtime THEN file_hash.partial END),
                full = COALESCE(excluded.full, CASE WHEN file_hash.size = excluded.size AND file_hash.mtime = excluded.mtime THEN file_hash.full END);
            """, tx);
        foreach (var item in items)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", item.Id);
            cmd.Parameters.AddWithValue("$size", item.Size);
            cmd.Parameters.AddWithValue("$mtime", item.ModifiedUnix);
            cmd.Parameters.AddWithValue("$partial", (object?)item.PartialHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$full", (object?)item.FullHash ?? DBNull.Value);
            try
            {
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // Il file è stato tolto dall'indice nel frattempo (vincolo di chiave esterna): si ignora.
            }
        }
        tx.Commit();
    }

    /// <summary>Record completi dei file indicati (per mostrare i gruppi di cloni).</summary>
    public IReadOnlyList<FileRecord> GetFiles(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0)
            return [];
        var list = new List<FileRecord>();
        using var conn = Connect();
        foreach (var chunk in ids.Chunk(500))
        {
            using var cmd = Command(conn, SelectFiles + $" WHERE f.id IN ({string.Join(",", chunk)});");
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(ReadFile(r));
        }
        return list;
    }

    /// <summary>Toglie dall'indice i file indicati (es. dopo averli spostati nel Cestino).</summary>
    public void RemoveFiles(IEnumerable<string> paths)
    {
        using var conn = Connect();
        using var tx = conn.BeginTransaction();
        using var cmd = Command(conn, "DELETE FROM files WHERE path = $p;", tx);
        var p = cmd.Parameters.Add("$p", SqliteType.Text);
        foreach (var path in paths)
        {
            p.Value = path;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
