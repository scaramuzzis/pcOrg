using System.Runtime;
using Microsoft.Data.Sqlite;

namespace PcOrg.Core.Diagnostics;

/// <summary>
/// Restituisce al sistema la memoria non più necessaria. Dopo un'operazione pesante (scansione, lettura
/// dei documenti, ricerca dei cloni) .NET e SQLite tendono a tenersi la memoria usata "per la prossima
/// volta": qui la si libera, così l'occupazione a riposo torna bassa.
/// </summary>
public static class MemoryTrim
{
    /// <summary>Libera la memoria gestita (compattando anche gli oggetti grandi) e le cache delle connessioni inattive.</summary>
    public static void Run()
    {
        try
        {
            SqliteConnection.ClearAllPools(); // chiude solo le connessioni non in uso, con le loro cache
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Liberazione della memoria non riuscita.", ex);
        }
    }
}
