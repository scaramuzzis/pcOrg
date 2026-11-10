using CommunityToolkit.Mvvm.ComponentModel;
using PcOrg.App.Infrastructure;
using PcOrg.Core.Storage;

namespace PcOrg.App.ViewModels;

/// <summary>Cartella monitorata nel pannello laterale, con lo stato di raggiungibilità (utile per quelle di rete).</summary>
public sealed partial class RootItem(RootFolder root) : ObservableObject
{
    public RootFolder Root { get; } = root;
    public long Id => Root.Id;
    public string Path => Root.Path;
    public DateTime? LastScanUtc => Root.LastScanUtc;
    public bool IsNetwork => Root.IsNetwork;
    public string Glyph => IsNetwork ? Glyphs.NetworkFolder : Glyphs.Folder;

    /// <summary>null = verifica in corso o non ancora fatta.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffline))]
    private bool? _isAvailable;

    public bool IsOffline => IsAvailable == false;
}
