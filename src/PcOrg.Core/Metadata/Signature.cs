using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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

    /// <summary>Editore di un file firmato (PE o MSI) tramite le API di Windows.</summary>
    [SupportedOSPlatform("windows")]
    public static string? SignerFromFile(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // nessuna alternativa per leggere il certificato da file firmati non PE (MSI)
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return NameOf(certificate);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Organizzazione (O=) del certificato se presente, altrimenti il nome comune (CN=).</summary>
    private static string? NameOf(X509Certificate2 certificate)
    {
        const string OrganizationOid = "2.5.4.10";
        foreach (var rdn in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
            if (rdn.GetSingleElementType().Value == OrganizationOid && rdn.GetSingleElementValue() is { Length: > 0 } org)
                return org.Trim();
        var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Verifica la firma con WinVerifyTrust (stessa verifica di Esplora risorse), senza accedere a internet
    /// per la revoca dei certificati. Fuori da Windows restituisce <see cref="SignatureStatus.NonVerificata"/>.
    /// </summary>
    public static SignatureStatus Verify(string path, bool hasSignature)
    {
        if (!OperatingSystem.IsWindows())
            return hasSignature ? SignatureStatus.NonVerificata : SignatureStatus.NonFirmato;

        var result = WinTrust.Verify(path);
        return result switch
        {
            0 => SignatureStatus.Valida,
            WinTrust.TrustENoSignature or WinTrust.TrustESubjectFormUnknown or WinTrust.TrustEProviderUnknown
                => hasSignature ? SignatureStatus.NonValida : SignatureStatus.NonFirmato,
            _ => SignatureStatus.NonValida,
        };
    }

    [SupportedOSPlatform("windows")]
    private static class WinTrust
    {
        public const int TrustENoSignature = unchecked((int)0x800B0100);
        public const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
        public const int TrustEProviderUnknown = unchecked((int)0x800B0001);

        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

        public static int Verify(string path)
        {
            const uint WtdUiNone = 2, WtdRevokeNone = 0, WtdChoiceFile = 1, WtdStateActionIgnore = 0;
            const uint WtdCacheOnlyUrlRetrieval = 0x1000, WtdDisableMd2Md4 = 0x2000;

            var pathPtr = Marshal.StringToCoTaskMemUni(path);
            var filePtr = IntPtr.Zero;
            try
            {
                var file = new WinTrustFileInfo
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    pcwszFilePath = pathPtr,
                };
                filePtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
                Marshal.StructureToPtr(file, filePtr, false);

                var data = new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                    dwUIChoice = WtdUiNone,
                    fdwRevocationChecks = WtdRevokeNone,
                    dwUnionChoice = WtdChoiceFile,
                    pFile = filePtr,
                    dwStateAction = WtdStateActionIgnore,
                    dwProvFlags = WtdCacheOnlyUrlRetrieval | WtdDisableMd2Md4,
                };
                var action = GenericVerifyV2;
                return WinVerifyTrust(new IntPtr(-1), ref action, ref data); // INVALID_HANDLE_VALUE: nessuna UI
            }
            finally
            {
                if (filePtr != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(filePtr);
                Marshal.FreeCoTaskMem(pathPtr);
            }
        }
    }
}
