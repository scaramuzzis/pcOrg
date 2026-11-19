using System.Runtime.CompilerServices;

namespace PcOrg.Core.Tests;

/// <summary>Su Windows i test usano le funzioni di sistema vere (registro, periferiche, firme, MSI).</summary>
internal static class PlatformSetup
{
    [ModuleInitializer]
    internal static void Register() => PcOrg.Windows.WindowsPlatformSetup.Register();
}
