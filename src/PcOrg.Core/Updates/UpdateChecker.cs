using System.Net.Http.Headers;
using System.Text.Json;
using PcOrg.Core.Diagnostics;
using PcOrg.Core.Metadata;

namespace PcOrg.Core.Updates;

/// <summary>Esito del controllo di una nuova versione.</summary>
public enum UpdateStatus
{
    /// <summary>Stai usando la versione più recente.</summary>
    Aggiornato,
    /// <summary>È disponibile una versione più recente.</summary>
    NuovaVersione,
    /// <summary>Non è stato possibile controllare (nessuna connessione, pagina non accessibile).</summary>
    NonDisponibile,
}

public sealed record UpdateInfo(UpdateStatus Status, string? LatestVersion, string? Url, string? Error = null);

/// <summary>
/// Controlla sulla pagina Releases del progetto se esiste una versione più recente.
/// È l'unica funzione di Gestione File che usa internet: parte solo su richiesta, o all'avvio se l'utente
/// la attiva nelle Impostazioni. Non invia alcun dato: legge soltanto l'ultima versione pubblicata.
/// </summary>
public static class UpdateChecker
{
    public const string LatestReleaseApi = "https://api.github.com/repos/scaramuzzis/pcOrg/releases/latest";
    public const string ReleasesPage = "https://github.com/scaramuzzis/pcOrg/releases";

    public static async Task<UpdateInfo> CheckAsync(string currentVersion, HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        var own = client is null;
        client ??= new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("GestioneFile", currentVersion));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"Controllo aggiornamenti: risposta {(int)response.StatusCode}.");
                return new UpdateInfo(UpdateStatus.NonDisponibile, null, ReleasesPage,
                    $"La pagina delle versioni non è accessibile (codice {(int)response.StatusCode}).");
            }
            return Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), currentVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Warn("Controllo aggiornamenti non riuscito.", ex);
            return new UpdateInfo(UpdateStatus.NonDisponibile, null, ReleasesPage, "Nessuna connessione o servizio non raggiungibile.");
        }
        finally
        {
            if (own)
                client.Dispose();
        }
    }

    /// <summary>Interpreta la risposta (JSON con tag_name e html_url) e la confronta con la versione in uso.</summary>
    public static UpdateInfo Parse(string json, string currentVersion)
    {
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : ReleasesPage;
        if (string.IsNullOrWhiteSpace(tag))
            return new UpdateInfo(UpdateStatus.NonDisponibile, null, ReleasesPage, "Risposta non riconosciuta.");
        var latest = tag.TrimStart('v', 'V');
        return VersionComparer.Instance.Compare(latest, currentVersion) > 0
            ? new UpdateInfo(UpdateStatus.NuovaVersione, latest, url)
            : new UpdateInfo(UpdateStatus.Aggiornato, latest, url);
    }
}
