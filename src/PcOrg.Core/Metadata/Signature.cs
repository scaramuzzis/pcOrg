using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace PcOrg.Core.Metadata;

/// <summary>Firma digitale Authenticode: chi ha firmato il file e se la firma è valida.</summary>
public static class Signature
{
    /// <summary>Nome dell'editore (es. "Microsoft Corporation") dalla firma PKCS#7 incorporata in un PE.</summary>
    public static string? SignerFromPkcs7(byte[]? pkcs7)
    {
        if (pkcs7 is null || pkcs7.Length == 0)
            return null;
        try
        {
            var cms = new SignedCms();
            cms.Decode(pkcs7);
            var certificate = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0].Certificate : null;
            return certificate is null ? null : NameOf(certificate);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Editore di un file firmato (PE o MSI) tramite le API del sistema.</summary>
    public static string? SignerFromFile(string path) => Platform.PlatformServices.Current.SignerFromFile(path);

    /// <summary>Organizzazione (O=) del certificato se presente, altrimenti il nome comune (CN=).</summary>
    public static string? NameOf(X509Certificate2 certificate)
    {
        const string OrganizationOid = "2.5.4.10";
        foreach (var rdn in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
            if (rdn.GetSingleElementType().Value == OrganizationOid && rdn.GetSingleElementValue() is { Length: > 0 } org)
                return org.Trim();
        var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public const int TrustENoSignature = unchecked((int)0x800B0100);
    public const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    public const int TrustEProviderUnknown = unchecked((int)0x800B0001);

    /// <summary>
    /// Verifica la firma come Esplora risorse (WinVerifyTrust su Windows), senza accedere a internet
    /// per la revoca dei certificati. Dove non è disponibile restituisce <see cref="SignatureStatus.NonVerificata"/>.
    /// </summary>
    public static SignatureStatus Verify(string path, bool hasSignature)
    {
        var result = Platform.PlatformServices.Current.VerifyAuthenticode(path);
        return result switch
        {
            null => hasSignature ? SignatureStatus.NonVerificata : SignatureStatus.NonFirmato,
            0 => SignatureStatus.Valida,
            TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown
                => hasSignature ? SignatureStatus.NonValida : SignatureStatus.NonFirmato,
            _ => SignatureStatus.NonValida,
        };
    }
}
