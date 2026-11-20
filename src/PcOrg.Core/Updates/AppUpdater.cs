using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using PcOrg.Core.Diagnostics;

namespace PcOrg.Core.Updates;

/// <summary>Come è stato distribuito Gestione File: determina quale pacchetto scaricare per aggiornarlo.</summary>
public enum InstallKind
{
    /// <summary>Cartella con .NET incluso (GestioneFile-portatile-win-x64.zip).</summary>
    Portatile,
    /// <summary>Cartella che usa il .NET Desktop Runtime installato (GestioneFile-leggero-win-x64.zip).</summary>
    Leggero,
    /// <summary>Un solo GestioneFile.exe compresso.</summary>
    FileUnico,
    /// <summary>Installato con il pacchetto MSI (in Programmi).</summary>
    Installato,
}

/// <summary>Aggiornamento scaricato e pronto per essere applicato alla chiusura dell'app.</summary>
/// <param name="Kind">Tipo di installazione da aggiornare.</param>
/// <param name="Source">Cartella estratta (Portatile/Leggero), exe (FileUnico) o msi (Installato).</param>
/// <param name="Version">Versione scaricata.</param>
public sealed record PreparedUpdate(InstallKind Kind, string Source, string Version);

/// <summary>
/// Aggiornamento dell'app: sceglie il pacchetto giusto nell'ultima Release, lo scarica verificandone
/// dimensione e impronta SHA-256, lo prepara (estrazione degli zip) e scrive lo script che, dopo la
/// chiusura dell'app, sostituisce i file e la riavvia. I dati (indice, impostazioni, cartella
/// pcOrg-dati) non vengono mai toccati: lo script copia sopra, non cancella nulla.
/// </summary>
public static class AppUpdater
{
    public const string ExeName = "GestioneFile.exe";

    /// <summary>File che il pacchetto MSI mette accanto all'eseguibile per farsi riconoscere.</summary>
    public const string InstalledMarker = "GestioneFile.installato";

    public static string AssetNameFor(InstallKind kind) => kind switch
    {
        InstallKind.Portatile => "GestioneFile-portatile-win-x64.zip",
        InstallKind.Leggero => "GestioneFile-leggero-win-x64.zip",
        InstallKind.FileUnico => "GestioneFile.exe",
        InstallKind.Installato => "GestioneFile-win-x64.msi",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Descrizione per l'utente del tipo di installazione.</summary>
    public static string Describe(InstallKind kind) => kind switch
    {
        InstallKind.Portatile => "versione portatile",
        InstallKind.Leggero => "versione leggera",
        InstallKind.FileUnico => "file unico",
        InstallKind.Installato => "versione installata",
        _ => kind.ToString(),
    };

    /// <summary>
    /// Riconosce il tipo di installazione dalla cartella dell'eseguibile.
    /// </summary>
    /// <param name="appFolder">Cartella dell'app (AppContext.BaseDirectory).</param>
    /// <param name="isSingleFile">Vero se l'app gira come file unico (assembly senza percorso su disco).</param>
    public static InstallKind DetectKind(string appFolder, bool isSingleFile)
    {
        if (isSingleFile)
            return InstallKind.FileUnico;
        if (File.Exists(Path.Combine(appFolder, InstalledMarker)))
            return InstallKind.Installato;
        // Il pacchetto portatile contiene il runtime .NET.
        return File.Exists(Path.Combine(appFolder, "coreclr.dll")) ? InstallKind.Portatile : InstallKind.Leggero;
    }

    /// <summary>Il pacchetto dell'ultima Release adatto a questa installazione, se pubblicato.</summary>
    public static ReleaseAsset? SelectAsset(UpdateInfo info, InstallKind kind)
    {
        var name = AssetNameFor(kind);
        return info.Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scarica il pacchetto in <paramref name="folder"/> e ne verifica dimensione e SHA-256 (se GitHub la
    /// dichiara). Un file incompleto o alterato viene cancellato e segnalato con InvalidDataException.
    /// </summary>
    public static async Task<string> DownloadAsync(ReleaseAsset asset, string folder, string currentVersion,
        IProgress<double>? progress = null, HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(asset.Name));
        var own = client is null;
        client ??= new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("GestioneFile", currentVersion));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? asset.Size;

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    sha.AppendData(buffer, 0, read);
                    written += read;
                    if (total > 0)
                        progress?.Report(Math.Min(1.0, (double)written / total));
                }
            }

            if (asset.Size > 0 && written != asset.Size)
                Fail(target, $"Download incompleto: {written:N0} byte su {asset.Size:N0}.");
            var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            if (asset.Sha256 is not null && !string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                Fail(target, "Il file scaricato non corrisponde all'impronta pubblicata (SHA-256).");
            Log.Info($"Aggiornamento scaricato: {asset.Name} ({written:N0} byte, SHA-256 {hash}).");
            return target;
        }
        catch
        {
            TryDelete(target);
            throw;
        }
        finally
        {
            if (own)
                client.Dispose();
        }
    }

    /// <summary>
    /// Prepara il pacchetto scaricato: gli zip vengono estratti e controllati (devono contenere
    /// GestioneFile.exe). Restituisce l'aggiornamento pronto da applicare.
    /// </summary>
    public static PreparedUpdate Prepare(InstallKind kind, string downloadedFile, string version)
    {
        switch (kind)
        {
            case InstallKind.Portatile:
            case InstallKind.Leggero:
                var extracted = Path.Combine(Path.GetDirectoryName(downloadedFile)!, "estratto");
                if (Directory.Exists(extracted))
                    Directory.Delete(extracted, recursive: true);
                ZipFile.ExtractToDirectory(downloadedFile, extracted);
                // Lo zip contiene la cartella GestioneFile; si accetta anche lo zip "piatto".
                var root = File.Exists(Path.Combine(extracted, ExeName)) ? extracted : Path.Combine(extracted, "GestioneFile");
                if (!File.Exists(Path.Combine(root, ExeName)))
                    throw new InvalidDataException($"Il pacchetto scaricato non contiene {ExeName}.");
                return new PreparedUpdate(kind, root, version);
            case InstallKind.FileUnico:
            case InstallKind.Installato:
                return new PreparedUpdate(kind, downloadedFile, version);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>
    /// Script PowerShell che attende la chiusura dell'app (processo <paramref name="processId"/>),
    /// sostituisce i file e riavvia Gestione File. Ogni passo viene scritto nel registro attività.
    /// Se la sostituzione non riesce riavvia comunque la versione precedente.
    /// </summary>
    public static string BuildScript(PreparedUpdate update, string appFolder, string exePath, int processId, string logFile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Aggiornamento di Gestione File: generato automaticamente, si può cancellare.");
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine($"$log = {Quote(logFile)}");
        sb.AppendLine("function Scrivi([string]$testo) {");
        sb.AppendLine("  try { Add-Content -LiteralPath $log -Encoding UTF8 -Value ('{0:yyyy-MM-dd HH:mm:ss.fff} INFO  [aggiornamento] {1}' -f (Get-Date), $testo) } catch { }");
        sb.AppendLine("}");
        sb.AppendLine($"Scrivi {Quote($"Attendo la chiusura di Gestione File per installare la versione {update.Version}.")}");
        sb.AppendLine($"try {{ Wait-Process -Id {processId} -Timeout 180 -ErrorAction Stop }} catch {{ }}");
        sb.AppendLine("Start-Sleep -Milliseconds 700");
        sb.AppendLine("$ok = $false");
        switch (update.Kind)
        {
            case InstallKind.Portatile:
            case InstallKind.Leggero:
                // robocopy: codici < 8 indicano successo. Copia sopra, non elimina nulla (i dati restano).
                sb.AppendLine("for ($i = 0; $i -lt 10 -and -not $ok; $i++) {");
                sb.AppendLine($"  robocopy {Quote(update.Source)} {Quote(appFolder)} /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null");
                sb.AppendLine("  if ($LASTEXITCODE -lt 8) { $ok = $true } else { Scrivi ('Copia non riuscita (codice ' + $LASTEXITCODE + '), nuovo tentativo.'); Start-Sleep -Seconds 2 }");
                sb.AppendLine("}");
                break;
            case InstallKind.FileUnico:
                sb.AppendLine("for ($i = 0; $i -lt 15 -and -not $ok; $i++) {");
                sb.AppendLine($"  try {{ Copy-Item -LiteralPath {Quote(update.Source)} -Destination {Quote(exePath)} -Force -ErrorAction Stop; $ok = $true }}");
                sb.AppendLine("  catch { Scrivi ('Sostituzione non riuscita: ' + $_.Exception.Message); Start-Sleep -Seconds 2 }");
                sb.AppendLine("}");
                break;
            case InstallKind.Installato:
                sb.AppendLine($"$p = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i', {Quote("\"" + update.Source + "\"")}, '/passive', '/norestart') -Wait -PassThru");
                sb.AppendLine("if ($p.ExitCode -eq 0 -or $p.ExitCode -eq 3010) { $ok = $true } else { Scrivi ('Installazione non riuscita (codice ' + $p.ExitCode + ').') }");
                break;
        }
        sb.AppendLine($"if ($ok) {{ Scrivi {Quote($"Aggiornamento alla versione {update.Version} completato.")} }}");
        sb.AppendLine("else { Scrivi 'Aggiornamento non riuscito: riavvio la versione precedente.' }");
        sb.AppendLine($"Start-Process -FilePath {Quote(exePath)}");
        return sb.ToString();
    }

    /// <summary>Stringa PowerShell tra apici singoli (nessuna espansione di variabili).</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static void Fail(string target, string message)
    {
        TryDelete(target);
        throw new InvalidDataException(message);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
