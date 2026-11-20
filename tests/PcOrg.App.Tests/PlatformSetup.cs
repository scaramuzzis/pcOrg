using System.Runtime.CompilerServices;

namespace PcOrg.App.Tests;

/// <summary>I test usano le funzioni di Windows vere fin dall'inizio (anche prima di creare l'App).</summary>
internal static class PlatformSetup
{
    [ModuleInitializer]
    internal static void Register() => PcOrg.Windows.WindowsPlatformSetup.Register();
}
