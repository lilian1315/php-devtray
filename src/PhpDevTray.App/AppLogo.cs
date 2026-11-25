using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhpDevTray.App;

internal static class AppLogo
{
    private static readonly DrawingGroup Drawing = LoadDrawing();

    internal static Geometry Glyph => ((GeometryDrawing)((DrawingGroup)Drawing.Children[1]).Children[0]).Geometry;

    internal static BitmapFrame WindowIcon { get; } = LoadWindowIcon();

    internal static BitmapSource Render(int size, Color? statusColor = null, int frame = -1)
    {
        if (size is < 16 or > 1024) throw new ArgumentOutOfRangeException(nameof(size));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(size / 34d, size / 34d));
            context.DrawDrawing(Drawing);
            if (statusColor is { } color)
            {
                var center = new Point(28.5, 28.5);
                context.DrawEllipse(new SolidColorBrush(Color.FromRgb(39, 93, 215)), null, center, 5.5, 5.5);
                var brush = new SolidColorBrush(color);
                if (frame >= 0) brush.Opacity = .55 + .45 * Math.Cos(frame * Math.PI / 5);
                context.DrawEllipse(brush, null, center, 3.7, 3.7);
            }
            context.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    internal static nint CreateNativeIcon(int size, Color? statusColor = null, int frame = -1)
    {
        var pixels = new byte[size * size * 4];
        Render(size, statusColor, frame).CopyPixels(pixels, size * 4, 0);
        var bitmap = new BitmapInfo
        {
            Header = new BitmapInfoHeader { Size = 40, Width = size, Height = -size, Planes = 1, BitCount = 32, SizeImage = (uint)pixels.Length }
        };
        var color = CreateDIBSection(0, ref bitmap, 0, out var bits, 0, 0);
        if (color == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint mask = 0;
        try
        {
            // Explicitly initialize the AND mask; alpha carries the antialiased edges.
            mask = CreateBitmap(size, size, 1, 1, new byte[((size + 15) / 16 * 2) * size]);
            if (mask == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var info = new IconInfo { IsIcon = true, Mask = mask, Color = color };
            var icon = CreateIconIndirect(ref info);
            if (icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            return icon;
        }
        finally
        {
            DeleteObject(color);
            if (mask != 0) DeleteObject(mask);
        }
    }

    internal static void DestroyNativeIcon(nint icon) => DestroyIcon(icon);

    private static DrawingGroup LoadDrawing()
    {
        using var stream = typeof(AppLogo).Assembly.GetManifestResourceStream("PhpDevTray.App.Assets.AppLogo.xaml")
            ?? throw new InvalidOperationException("The application logo resource is missing.");
        var drawing = (DrawingGroup)XamlReader.Load(stream);
        drawing.Freeze();
        return drawing;
    }

    private static BitmapFrame LoadWindowIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/PhpDevTray;component/Assets/App.ico"))
            ?? throw new InvalidOperationException("The application icon resource is missing.");
        using var stream = resource.Stream;
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame; // WPF uses the decoder's frames to select the appropriate window/taskbar size.
    }

    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool IsIcon; public uint HotspotX, HotspotY; public nint Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Color; }
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo bitmapInfo, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreateIconIndirect(ref IconInfo iconInfo);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
}
