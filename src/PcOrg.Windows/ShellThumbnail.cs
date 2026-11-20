using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PcOrg.Core.Platform;

namespace PcOrg.Windows;

/// <summary>
/// Miniature di Esplora risorse (IShellItemImageFactory): fotogramma dei video, copertina dei brani,
/// prima pagina di PDF e documenti quando c'è un gestore installato, immagini in formati particolari.
/// Si chiede solo la miniatura vera (niente icona generica del tipo di file).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ShellThumbnail
{
    private const int SiigbfBiggerSizeOk = 0x1, SiigbfThumbnailOnly = 0x8;

    public static ThumbnailImage? Get(string path, int size)
    {
        // Alcuni gestori di miniature funzionano solo in un thread STA: se ne usa uno dedicato.
        ThumbnailImage? result = null;
        var thread = new Thread(() => result = GetCore(path, size)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(TimeSpan.FromSeconds(15)) ? result : null;
    }

    private static ThumbnailImage? GetCore(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null)
            return null;
        try
        {
            if (factory.GetImage(new NativeSize { Width = size, Height = size }, SiigbfBiggerSizeOk | SiigbfThumbnailOnly, out var hbitmap) != 0
                || hbitmap == IntPtr.Zero)
                return null;
            try
            {
                return ToImage(hbitmap);
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Converte la bitmap di Windows in pixel BGRA (righe dall'alto in basso).</summary>
    private static ThumbnailImage? ToImage(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<NativeBitmap>(), out var bm) == 0 || bm.Width <= 0 || bm.Height <= 0)
            return null;
        var info = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = bm.Width,
            Height = -bm.Height, // negativo: dall'alto in basso
            Planes = 1,
            BitCount = 32,
        };
        var pixels = new byte[bm.Width * bm.Height * 4];
        var screen = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(screen, hbitmap, 0, (uint)bm.Height, pixels, ref info, 0) == 0)
                return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
        // Alcuni gestori restituiscono un canale alfa tutto a zero: l'immagine sarebbe invisibile.
        var anyAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha)
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
        return new ThumbnailImage(bm.Width, bm.Height, pixels);
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr hbitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
}
