using Microsoft.Data.Sqlite;
using PcOrg.Core.Drivers;

namespace PcOrg.Core.Storage;

/// <summary>Identificativi hardware dei pacchetti driver (.inf) presenti nelle cartelle monitorate.</summary>
public sealed partial class FileIndex
{
    /// <summary>Tabella degli identificativi hardware dei driver (schema v7).</summary>
    private static void CreateDriverTable(SqliteConnection conn, SqliteTransaction tx) => Exec(conn, """
        CREATE TABLE driver_hwids (
            file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
            hwid        TEXT NOT NULL COLLATE NOCASE,
            description TEXT
        );
        CREATE INDEX ix_driver_hwids_file ON driver_hwids(file_id);
        CREATE INDEX ix_driver_hwids_id ON driver_hwids(hwid);
        """, tx);

    private static void SaveHardwareIds(SqliteConnection conn, SqliteTransaction tx, long fileId, IReadOnlyList<DriverHardwareId>? ids)
    {
        using (var del = Command(conn, "DELETE FROM driver_hwids WHERE file_id = $id;", tx))
        {
            del.Parameters.AddWithValue("$id", fileId);
            del.ExecuteNonQuery();
        }
        if (ids is null || ids.Count == 0)
            return;
        using var insert = Command(conn, "INSERT INTO driver_hwids(file_id, hwid, description) VALUES ($id, $hw, $desc);", tx);
        var pId = insert.Parameters.Add("$id", SqliteType.Integer);
        var pHw = insert.Parameters.Add("$hw", SqliteType.Text);
        var pDesc = insert.Parameters.Add("$desc", SqliteType.Text);
        pId.Value = fileId;
        foreach (var id in ids)
        {
            pHw.Value = id.Id;
            pDesc.Value = (object?)id.Description ?? DBNull.Value;
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>Pacchetti driver (.inf) con i loro identificativi hardware.</summary>
    public IReadOnlyList<(FileRecord File, IReadOnlyList<DriverHardwareId> Ids)> GetDriverPackages()
    {
        var ids = new Dictionary<long, List<DriverHardwareId>>();
        using (var conn = Connect())
        using (var cmd = Command(conn, "SELECT file_id, hwid, description FROM driver_hwids;"))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                if (!ids.TryGetValue(r.GetInt64(0), out var list))
                    ids[r.GetInt64(0)] = list = [];
                list.Add(new DriverHardwareId(r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
            }
        return GetFiles(ids.Keys.ToList())
            .Select(f => (f, (IReadOnlyList<DriverHardwareId>)ids[f.Id]))
            .ToList();
    }
}
