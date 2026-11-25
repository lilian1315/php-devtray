using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhpDevTray.App;

namespace PhpDevTray.UiChecks;

internal static class AppIconChecks
{
    public static void Run()
    {
        var assembly = typeof(PhpDevTray.App.App).Assembly.Location;
        var count = ExtractIconEx(assembly, -1, null, null, 0);
        if (count == 0)
            throw new InvalidOperationException("The application has no native icon resource: Windows falls back to its generic window/taskbar icon.");

        var decoder = AppLogo.WindowIcon.Decoder;
        // The original control rounds its 34-DIP layout to physical device pixels.
        RequireSimilar(AppLogo.Render(256), RenderSidebarLogo(256), "The shared drawing no longer matches the original sidebar logo", tolerance: 12);
        foreach (var size in new[] { 16, 20, 24, 28, 32, 40, 48, 64, 96, 128, 256 })
        {
            var frame = decoder.Frames.SingleOrDefault(f => f.PixelWidth == size && f.PixelHeight == size)
                ?? throw new InvalidOperationException($"The window/taskbar icon is missing its {size}px frame.");
            RequireSimilar(frame, AppLogo.Render(size), $"{size}px ICO frame no longer matches the shared application logo");
        }
        var large = new nint[1];
        var small = new nint[1];
        ExtractIconEx(assembly, 0, large, small, 1);
        try
        {
            foreach (var handle in large.Concat(small))
            {
                if (handle == 0) throw new InvalidOperationException("Windows could not extract a small/large application icon.");
                var native = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                RequireSimilar(native, AppLogo.Render(native.PixelWidth), "Windows extracted the wrong application icon");
            }
        }
        finally { foreach (var handle in large.Concat(small)) if (handle != 0) AppLogo.DestroyNativeIcon(handle); }

        foreach (var size in new[] { 16, 20, 24, 28, 32, 40, 48, 64 })
        {
            var handle = AppLogo.CreateNativeIcon(size);
            try
            {
                var native = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                if (native.PixelWidth != size || native.PixelHeight != size)
                    throw new InvalidOperationException("The notification icon does not use the requested DPI size.");
                RequireSimilar(native, AppLogo.Render(size), "The notification icon no longer matches the shared application logo");
                var pixels = Pixels(native);
                if (!Enumerable.Range(0, size * size).Any(p => pixels[p * 4 + 3] is > 0 and < 255))
                    throw new InvalidOperationException("The notification icon lost its antialiased transparent edges.");
            }
            finally { AppLogo.DestroyNativeIcon(handle); }
            foreach (var color in new[] { Color.FromRgb(32,159,112), Color.FromRgb(216,152,41), Color.FromRgb(204,62,78) })
            {
                var status = AppLogo.Render(size, color);
                RequireSimilar(status, AppLogo.Render(size), "A tray status replaced the application logo", excludeBadge: true);
            }
        }
        var busyStart = Pixels(AppLogo.Render(32, Colors.White, 0));
        var busyEnd = Pixels(AppLogo.Render(32, Colors.White, 5));
        if (busyStart.SequenceEqual(busyEnd)) throw new InvalidOperationException("The busy status does not animate.");
        RequireSimilar(AppLogo.Render(32, Colors.White, 5), AppLogo.Render(32), "The busy status replaced the application logo", excludeBadge: true);
        Console.WriteLine("PASS: native EXE/DLL icons and all ICO sizes match the existing sidebar logo; DPI-sized notification icons retain antialiased alpha and status badges.");
    }

    private static BitmapSource RenderSidebarLogo(int size)
    {
        var tile = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(39,93,215)), Padding = new Thickness(6),
            Child = UiIcon.Create(AppIcon.App, 22, "AccentTextBrush")
        };
        tile.Measure(new Size(34,34));
        tile.Arrange(new Rect(0,0,34,34));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawRectangle(new VisualBrush(tile), null, new Rect(0,0,size,size));
        var bitmap = new RenderTargetBitmap(size,size,96,96,PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
    }

    private static void RequireSimilar(BitmapSource actual, BitmapSource expected, string message, bool excludeBadge = false, double tolerance = 8)
    {
        if (actual.PixelWidth != expected.PixelWidth || actual.PixelHeight != expected.PixelHeight)
            throw new InvalidOperationException(message + ": different sizes.");
        var a = Pixels(actual);
        var b = Pixels(expected);
        long difference = 0;
        var samples = 0;
        for (var y = 0; y < actual.PixelHeight; y++)
        for (var x = 0; x < actual.PixelWidth; x++)
        {
            if (excludeBadge && x >= actual.PixelWidth * .65 && y >= actual.PixelHeight * .65) continue;
            for (var c = 0; c < 4; c++) { var index = (y * actual.PixelWidth + x) * 4 + c; difference += Math.Abs(a[index] - b[index]); samples++; }
        }
        if (difference / (double)samples > tolerance)
        {
            Directory.CreateDirectory("TestResults/icons");
            foreach (var (name, bitmap) in new[] { ("actual", actual), ("expected", expected) })
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create($"TestResults/icons/{name}-{actual.PixelWidth}.png");
                encoder.Save(output);
            }
            throw new InvalidOperationException($"{message}: mean pixel difference {difference / (double)samples:0.00}.");
        }
    }

    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, nint[]? large, nint[]? small, uint count);
}
