using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace PcOrg.App.Controls;

/// <summary>Pulsante con icona (Segoe Fluent Icons) e testo opzionale. Usa lo stile standard dei pulsanti.</summary>
public class IconButton : Button
{
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    static IconButton()
    {
        // Senza questo WPF cercherebbe uno stile per "IconButton" e userebbe quello classico:
        // così invece eredita lo stile Fluent dei normali pulsanti.
        DefaultStyleKeyProperty.OverrideMetadata(typeof(IconButton), new FrameworkPropertyMetadata(typeof(Button)));
    }

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(IconButton), new PropertyMetadata(null, (d, _) => ((IconButton)d).Rebuild()));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(IconButton), new PropertyMetadata(null, (d, _) => ((IconButton)d).Rebuild()));

    public static readonly DependencyProperty GlyphBrushProperty = DependencyProperty.Register(
        nameof(GlyphBrush), typeof(Brush), typeof(IconButton), new PropertyMetadata(null, (d, _) => ((IconButton)d).Rebuild()));

    public string? Glyph
    {
        get => (string?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Colore dell'icona (facoltativo; di default quello del testo).</summary>
    public Brush? GlyphBrush
    {
        get => (Brush?)GetValue(GlyphBrushProperty);
        set => SetValue(GlyphBrushProperty, value);
    }

    private void Rebuild()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (!string.IsNullOrEmpty(Glyph))
        {
            var icon = new TextBlock
            {
                Text = Glyph,
                FontFamily = IconFont,
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = string.IsNullOrEmpty(Label) ? new Thickness(0) : new Thickness(0, 0, 8, 0),
            };
            if (GlyphBrush is not null)
                icon.Foreground = GlyphBrush;
            panel.Children.Add(icon);
        }
        if (!string.IsNullOrEmpty(Label))
            panel.Children.Add(new TextBlock { Text = Label, VerticalAlignment = VerticalAlignment.Center });
        Content = panel;
        if (string.IsNullOrEmpty(Label) && ToolTip is null && !string.IsNullOrEmpty(AutomationName))
            ToolTip = AutomationName;
    }

    private string? AutomationName => System.Windows.Automation.AutomationProperties.GetName(this);

    /// <summary>
    /// Nome letto dagli screen reader: quello esplicito, altrimenti il testo del pulsante, altrimenti il
    /// suggerimento (per i pulsanti con la sola icona). Senza, verrebbe letto il carattere dell'icona.
    /// </summary>
    public string AccessibleName =>
        !string.IsNullOrWhiteSpace(AutomationName) ? AutomationName!
        : !string.IsNullOrWhiteSpace(Label) ? Label!
        : ToolTip as string ?? (ToolTip as ToolTip)?.Content as string ?? "";

    protected override AutomationPeer OnCreateAutomationPeer() => new IconButtonAutomationPeer(this);

    private sealed class IconButtonAutomationPeer(IconButton owner) : ButtonAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.AccessibleName;

        // Il contenuto (icona e testo) è già riassunto nel nome: non va letto una seconda volta.
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
