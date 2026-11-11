namespace PcOrg.Core;

/// <summary>
/// File "solo online" (OneDrive e simili con File su richiesta): compaiono nelle cartelle ma il contenuto
/// non è sul disco. Leggerli ne provocherebbe lo scaricamento, quindi pcOrg li elenca senza aprirli.
/// </summary>
public static class CloudFiles
{
    // Attributi di Windows non presenti nell'enum di .NET.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    public static bool IsOnlineOnly(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    public static bool IsOnlineOnly(string path)
    {
        try
        {
            return IsOnlineOnly(File.GetAttributes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
