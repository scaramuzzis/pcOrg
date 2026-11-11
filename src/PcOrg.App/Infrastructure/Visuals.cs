using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PcOrg.Core.Classification;

namespace PcOrg.App.Infrastructure;

/// <summary>Icone (font Segoe Fluent Icons / MDL2) e colori usati nell'interfaccia.</summary>
public static class Glyphs
{
    public const string All = "\uE71D";
    public const string Folder = "\uE8B7";
    public const string NetworkFolder = "\uE8CE";
    public const string FolderOpen = "\uE838";
    public const string Add = "\uE710";
    public const string Delete = "\uE74D";
    public const string Refresh = "\uE72C";
    public const string Search = "\uE721";
    public const string Open = "\uE8E5";
    public const string Copy = "\uE8C8";
    public const string Move = "\uE8DE";
    public const string History = "\uE81C";
    public const string Cancel = "\uE711";
    public const string Undo = "\uE7A7";
    public const string Success = "\uE73E";
    public const string Info = "\uE946";
    public const string Warning = "\uE7BA";
    public const string Error = "\uEA39";
    public const string Question = "\uE897";
    public const string SelectAll = "\uE8B3";
    public const string Package = "\uE7B8";
    public const string Shield = "\uE83D";
    public const string Photo = "\uEB9F";
    public const string Export = "\uEDE1";
    public const string Exit = "\uE7E8";
    public const string Help = "\uE897";
    public const string Theme = "\uE790";
    public const string Keyboard = "\uE765";
    public const string Settings = "\uE713";
    public const string Columns = "\uE8A9";
    public const string Log = "\uE9F9";
    public const string Up = "\uE70E";
    public const string Down = "\uE70D";
    public const string Database = "\uE7F1";
    public const string Duplicate = "\uE8F1";
    public const string Rules = "\uE71C";
    public const string Device = "\uE772";
    public const string Home = "\uE80F";
    public const string Tag = "\uE8EC";
    public const string Document = "\uE8A5";
    public const string Download = "\uE896";
    public const string Edit = "\uE70F";
    public const string Lightbulb = "\uEA80";

    public static string ForCategory(FileCategory? category) => category switch
    {
        null => All,
        FileCategory.Installer => Package,
        FileCategory.Driver => "\uE772",
        FileCategory.Programma => "\uECAA",
        FileCategory.Archivio => "\uF012",
        FileCategory.ImmagineDisco => "\uEDA2",
        FileCategory.Documento => "\uE8A5",
        FileCategory.Immagine => Photo,
        FileCategory.Audio => "\uE8D6",
        FileCategory.Video => "\uE714",
        FileCategory.Script => "\uE943",
        FileCategory.Pdf => "\uEA90",
        FileCategory.Word => "\uE8D2",
        FileCategory.Testo => "\uE7C3",
        FileCategory.FoglioDiCalcolo => "\uE80A",
        FileCategory.Presentazione => "\uE786",
        FileCategory.AccessoRemoto => "\uE8AF",
        _ => "\uE9CE",
    };
}

/// <summary>Colore distintivo di ogni categoria (usato per icone e badge).</summary>
public static class CategoryColors
{
    private static readonly Dictionary<FileCategory, string> Hex = new()
    {
        [FileCategory.Installer] = "#2563EB",
        [FileCategory.Driver] = "#7C3AED",
        [FileCategory.Programma] = "#0891B2",
        [FileCategory.Archivio] = "#CA8A04",
        [FileCategory.ImmagineDisco] = "#475569",
        [FileCategory.Documento] = "#0D9488",
        [FileCategory.Immagine] = "#DB2777",
        [FileCategory.Audio] = "#EA580C",
        [FileCategory.Video] = "#DC2626",
        [FileCategory.Script] = "#16A34A",
        [FileCategory.Pdf] = "#E11D48",
        [FileCategory.Word] = "#2B579A",
        [FileCategory.Testo] = "#6B7280",
        [FileCategory.FoglioDiCalcolo] = "#217346",
        [FileCategory.Presentazione] = "#D24726",
        [FileCategory.AccessoRemoto] = "#0EA5E9",
        [FileCategory.Altro] = "#94A3B8",
    };

    private static readonly Dictionary<string, SolidColorBrush> Cache = [];

    public static SolidColorBrush For(FileCategory? category) =>
        Brush(category is { } c && Hex.TryGetValue(c, out var hex) ? hex : "#64748B");

    public static SolidColorBrush Brush(string hex)
    {
        if (!Cache.TryGetValue(hex, out var brush))
        {
            brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Cache[hex] = brush;
        }
        return brush;
    }
}

/// <summary>FileCategory → icona.</summary>
public sealed class CategoryGlyphConverter : IValueConverter
{
    public static CategoryGlyphConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Glyphs.ForCategory(value as FileCategory?);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>FileCategory → colore.</summary>
public sealed class CategoryBrushConverter : IValueConverter
{
    public static CategoryBrushConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        CategoryColors.For(value as FileCategory?);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Vero → Collapsed (l'opposto di BooleanToVisibilityConverter).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public static InverseBoolToVisibilityConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Testo vuoto o null → Collapsed.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public static EmptyToCollapsedConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Percorso completo → nome dell'ultima cartella (es. "C:\Users\Io\Download" → "Download").</summary>
public sealed class FolderNameConverter : IValueConverter
{
    public static FolderNameConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var path = value as string ?? "";
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? path : name;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
