using System.Globalization;
using Microsoft.Data.Sqlite;
using PcOrg.Core.Content;

namespace PcOrg.Core.Storage;

/// <summary>File il cui contenuto va (ri)indicizzato.</summary>
public sealed record PendingContent(long Id, string Path, long Size, long ModifiedUnix);

/// <summary>
/// Ricerca nel contenuto (testo dei documenti, elenco dei file di ZIP e ISO) ed etichette/note dei file.
/// </summary>
/// <remarks>
/// <para>Il contenuto sta in una tabella full-text separata (<c>content_fts</c>, una riga per file con lo
/// stesso id) con suddivisione in parole, che ignora maiuscole e accenti: si cerca per parola o inizio di parola.</para>
/// <para>Etichette e note stanno in <c>file_notes</c>, legate al <b>percorso</b> e non all'id: così
/// sopravvivono alla ricostruzione dell'indice. Sono copiate anche nell'indice di ricerca dei nomi
/// (colonna <c>notes</c> di <c>files_fts</c>), quindi la ricerca normale le trova.</para>
/// </remarks>
public sealed partial class FileIndex
{
    private const long MaxDocumentBytes = 50L * 1024 * 1024;

    /// <summary>Schema v8: contenuto, etichette/note, indice dei nomi esteso alle note.</summary>
    private static void CreateContentTables(SqliteConnection conn, SqliteTransaction tx) => Exec(conn, """
        CREATE TABLE file_content (
            file_id INTEGER PRIMARY KEY REFERENCES files(id) ON DELETE CASCADE,
            size    INTEGER NOT NULL,
            mtime   INTEGER NOT NULL,
            chars   INTEGER NOT NULL
        );
        CREATE VIRTUAL TABLE content_fts USING fts5(text, tokenize='unicode61 remove_diacritics 2');
        CREATE TRIGGER files_ad_content AFTER DELETE ON files BEGIN
            DELETE FROM content_fts WHERE rowid = old.id;
        END;

        CREATE TABLE file_notes (
            path TEXT PRIMARY KEY COLLATE NOCASE,
            tags TEXT NOT NULL DEFAULT '',
            note TEXT NOT NULL DEFAULT ''
        );

        DROP TABLE files_fts;
        CREATE VIRTUAL TABLE files_fts USING fts5(name, folder, product, company, notes, tokenize='trigram');
        -- (l'indice dei nomi viene ripopolato dal passo v9, che segue sempre questo)
        DROP TRIGGER files_ai;
        CREATE TRIGGER files_ai AFTER INSERT ON files BEGIN
            INSERT INTO files_fts(rowid, name, folder, product, company, notes)
            VALUES (new.id, new.name, new.folder, '', '',
                    COALESCE((SELECT tags || ' ' || note FROM file_notes WHERE path = new.path), ''));
        END;
        """, tx);

    // ------------------------------------------------------------------ Contenuto

    private string ContentFilter()
    {
        var extensions = string.Join(", ", ContentExtractor.SupportedExtensions.Select(e => $"'{e}'"));
        var containers = string.Join(", ", ContentExtractor.ContainerExtensions.Select(e => $"'{e}'"));
        // Solo cartelle locali: leggere ogni documento attraverso la rete sarebbe troppo lento.
        var roots = GetRoots().Where(r => !r.IsNetwork).Select(r => r.Id.ToString(CultureInfo.InvariantCulture)).ToList();
        return $"""
            FROM files f LEFT JOIN file_content c ON c.file_id = f.id
            WHERE f.ext IN ({extensions}) AND f.size > 0
              AND (f.ext IN ({containers}) OR f.size <= {MaxDocumentBytes})
              AND f.root_id IN ({(roots.Count == 0 ? "-1" : string.Join(",", roots))})
              AND (c.file_id IS NULL OR c.size <> f.size OR c.mtime <> f.modified_utc)
            """;
    }

    public int CountFilesNeedingContent()
    {
        var filter = ContentFilter();
        using var conn = Connect();
        return Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) " + filter + ";"), CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<PendingContent> GetFilesNeedingContent(int limit)
    {
        var filter = ContentFilter();
        using var conn = Connect();
        using var cmd = Command(conn, "SELECT f.id, f.path, f.size, f.modified_utc " + filter + " ORDER BY f.id LIMIT $n;");
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<PendingContent>();
        while (r.Read())
            list.Add(new PendingContent(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3)));
        return list;
    }

    /// <summary>Salva il testo estratto (null = nessun testo: si annota comunque che il file è stato letto).</summary>
    public void SaveContent(IEnumerable<(PendingContent File, string? Text)> items)
    {
        using var conn = Connect();
        using var tx = conn.BeginTransaction();
        foreach (var (file, text) in items)
        {
            using (var state = Command(conn, """
                INSERT INTO file_content(file_id, size, mtime, chars) VALUES ($id, $size, $mtime, $chars)
                ON CONFLICT(file_id) DO UPDATE SET size = excluded.size, mtime = excluded.mtime, chars = excluded.chars;
                """, tx))
            {
                state.Parameters.AddWithValue("$id", file.Id);
                state.Parameters.AddWithValue("$size", file.Size);
                state.Parameters.AddWithValue("$mtime", file.ModifiedUnix);
                state.Parameters.AddWithValue("$chars", text?.Length ?? 0);
                try
                {
                    state.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    continue; // il file è stato tolto dall'indice nel frattempo
                }
            }
            using (var del = Command(conn, "DELETE FROM content_fts WHERE rowid = $id;", tx))
            {
                del.Parameters.AddWithValue("$id", file.Id);
                del.ExecuteNonQuery();
            }
            if (string.IsNullOrEmpty(text))
                continue;
            using var insert = Command(conn, "INSERT INTO content_fts(rowid, text) VALUES ($id, $text);", tx);
            insert.Parameters.AddWithValue("$id", file.Id);
            insert.Parameters.AddWithValue("$text", text);
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Numero di file con contenuto indicizzato (per le Impostazioni).</summary>
    public long CountIndexedContent()
    {
        using var conn = Connect();
        return Convert.ToInt64(Scalar(conn, "SELECT COUNT(*) FROM file_content WHERE chars > 0;"), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Query per il contenuto: ogni parola deve comparire (anche come inizio di parola).
    /// Si tengono solo lettere e cifre, così nessun carattere digitato può alterare la sintassi della query.
    /// </summary>
    internal static string? BuildContentMatch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var terms = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in text + " ")
        {
            if (char.IsLetterOrDigit(ch))
                current.Append(ch);
            else if (current.Length > 0)
            {
                if (current.Length >= 2)
                    terms.Add($"\"{current}\"*");
                current.Clear();
            }
        }
        return terms.Count == 0 ? null : string.Join(" AND ", terms);
    }

    /// <summary>File il cui contenuto corrisponde al testo, con un estratto del punto trovato.</summary>
    private List<FileRecord> SearchContent(SqliteConnection conn, SearchQuery query)
    {
        var list = new List<FileRecord>();
        if (BuildContentMatch(query.Text) is not { } match)
            return list;
        using var cmd = conn.CreateCommand();
        var sql = new System.Text.StringBuilder(SelectFiles.Replace("FROM files f", ", snippet(content_fts, 0, '«', '»', ' … ', 16) FROM files f"));
        sql.Append(" JOIN content_fts ON content_fts.rowid = f.id WHERE content_fts MATCH $match");
        cmd.Parameters.AddWithValue("$match", match);
        AppendCategoryFilters(cmd, sql, query);
        sql.Append(" ORDER BY bm25(content_fts) LIMIT $limit;");
        cmd.Parameters.AddWithValue("$limit", query.Limit);
        cmd.CommandText = sql.ToString();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(ReadFile(r));
        return list;
    }

    private static void AppendCategoryFilters(SqliteCommand cmd, System.Text.StringBuilder sql, SearchQuery query)
    {
        if (query.Extensions is { Count: > 0 } extensions)
        {
            var names = extensions.Select((_, i) => $"$cext{i}").ToList();
            foreach (var (ext, i) in extensions.Select((e, i) => (e, i)))
                cmd.Parameters.AddWithValue($"$cext{i}", ext.ToLowerInvariant());
            sql.Append($" AND f.ext IN ({string.Join(", ", names)})");
        }
        if (query.Category is { } category)
        {
            sql.Append(" AND f.category = $ccat");
            cmd.Parameters.AddWithValue("$ccat", (int)category);
        }
    }

    // ------------------------------------------------------------------ Etichette e note

    /// <summary>
    /// Imposta etichette e nota dei file indicati (null = lascia com'è). Etichette e nota vuote
    /// eliminano la riga. Le etichette sono separate da virgola.
    /// </summary>
    public void SetNotes(IEnumerable<string> paths, string? tags, string? note)
    {
        using var conn = Connect();
        using var tx = conn.BeginTransaction();
        foreach (var path in paths)
        {
            string currentTags = "", currentNote = "";
            using (var read = Command(conn, "SELECT tags, note FROM file_notes WHERE path = $p;", tx))
            {
                read.Parameters.AddWithValue("$p", path);
                using var r = read.ExecuteReader();
                if (r.Read())
                    (currentTags, currentNote) = (r.GetString(0), r.GetString(1));
            }
            var newTags = tags is null ? currentTags : NormalizeTags(tags);
            var newNote = note is null ? currentNote : note.Trim();

            using (var write = Command(conn, newTags.Length == 0 && newNote.Length == 0
                       ? "DELETE FROM file_notes WHERE path = $p;"
                       : """
                         INSERT INTO file_notes(path, tags, note) VALUES ($p, $t, $n)
                         ON CONFLICT(path) DO UPDATE SET tags = excluded.tags, note = excluded.note;
                         """, tx))
            {
                write.Parameters.AddWithValue("$p", path);
                write.Parameters.AddWithValue("$t", newTags);
                write.Parameters.AddWithValue("$n", newNote);
                write.ExecuteNonQuery();
            }
            using var fts = Command(conn, "UPDATE files_fts SET notes = $v WHERE rowid = (SELECT id FROM files WHERE path = $p);", tx);
            fts.Parameters.AddWithValue("$v", (newTags + " " + newNote).Trim());
            fts.Parameters.AddWithValue("$p", path);
            fts.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>"Ufficio ,  stampante,ufficio" → "Ufficio, stampante" (senza vuoti né doppioni).</summary>
    public static string NormalizeTags(string tags) =>
        string.Join(", ", tags.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase));

    /// <summary>Tutte le etichette in uso con il numero di file, dalla più usata.</summary>
    public IReadOnlyList<(string Tag, int Count)> GetTags()
    {
        using var conn = Connect();
        using var cmd = Command(conn, "SELECT n.tags FROM file_notes n JOIN files f ON f.path = n.path WHERE n.tags <> '';");
        using var r = cmd.ExecuteReader();
        var counts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        while (r.Read())
            foreach (var tag in r.GetString(0).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
        return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)).ToList();
    }
}
