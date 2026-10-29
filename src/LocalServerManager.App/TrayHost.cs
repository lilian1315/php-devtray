using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LocalServerManager.Core;

namespace LocalServerManager.App;

internal sealed class TrayHost : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const uint IconId = 1;
    private readonly HwndSource _source;
    private readonly DispatcherTimer _animation;
    private readonly IntPtr[] _frames;
    private readonly Dictionary<TrayState, IntPtr> _icons;
    private readonly uint _taskbarCreated;
    private EnvironmentSnapshot? _snapshot;
    private int _frame;
    private bool _disposed;
    private TrayState _state = TrayState.Stopped;

    public event EventHandler? OpenRequested;
    public event EventHandler? ToggleRequested;
    public event EventHandler? ExitRequested;

    public TrayHost()
    {
        _source = new HwndSource(new HwndSourceParameters("LocalServerManager.TrayHost")
        {
            Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000),
        });
        _source.AddHook(WindowProcedure);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _icons = new Dictionary<TrayState, IntPtr>
        {
            [TrayState.Stopped] = CreateStatusIcon(115, 128, 148),
            [TrayState.Healthy] = CreateStatusIcon(32, 159, 112),
            [TrayState.Partial] = CreateStatusIcon(216, 152, 41),
            [TrayState.Failed] = CreateStatusIcon(204, 62, 78),
        };
        _frames = Enumerable.Range(0, 10).Select(i => CreateStatusIcon(60, 112, 228, i)).ToArray();
        _animation = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _animation.Tick += (_, _) => { _frame = (_frame + 1) % _frames.Length; Modify(); };
        Add();
    }

    public void Update(EnvironmentSnapshot snapshot)
    {
        _snapshot = snapshot;
        var statuses = snapshot.Services.Select(s => s.Status).ToArray();
        var busy = snapshot.IsBusy || statuses.Any(IsTransition);
        _state = busy ? TrayState.Busy
            : statuses.Any(s => s == ServiceStatus.Failed)
                ? statuses.Any(s => s == ServiceStatus.Running) ? TrayState.Partial : TrayState.Failed
            : statuses.Any(s => s == ServiceStatus.Running)
                ? statuses.Where(s => s != ServiceStatus.NotInstalled).All(s => s == ServiceStatus.Running) ? TrayState.Healthy : TrayState.Partial
            : TrayState.Stopped;
        if (busy && SystemParameters.ClientAreaAnimation) _animation.Start();
        else _animation.Stop();
        Modify();
    }

    private static bool IsTransition(ServiceStatus status) => status is
        ServiceStatus.Starting or ServiceStatus.Stopping or ServiceStatus.Restarting or ServiceStatus.Updating;

    private NotifyIconData IconData()
    {
        var text = _snapshot is null ? "Local Server Manager · starting" :
            $"Nginx: {Describe(ServiceKind.Nginx)} · PHP: {DescribePhp()} · Mail Trap: {Describe(ServiceKind.Mailpit)}";
        if (text.Length > 127) text = text[..127];
        return new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _source.Handle, Id = IconId,
            Flags = 1 | 2 | 4 | 0x80, CallbackMessage = CallbackMessage,
            Icon = _state == TrayState.Busy ? _frames[_frame] : _icons[_state], Tip = text,
            Info = string.Empty, InfoTitle = string.Empty,
        };
    }

    private string Describe(ServiceKind kind) => _snapshot?.Services.FirstOrDefault(s => s.Kind == kind)?.Status is { } status
        ? status == ServiceStatus.NotInstalled ? "not installed" : status == ServiceStatus.Failed ? "needs attention" : status.ToString().ToLowerInvariant() : "not installed";
    private string DescribePhp()
    {
        var services = _snapshot?.Services.Where(s => s.Kind == ServiceKind.Php && s.Status != ServiceStatus.NotInstalled).ToArray() ?? [];
        if (services.Length == 0) return "not installed";
        if (services.Any(s => s.Status == ServiceStatus.Failed)) return "needs attention";
        if (services.Any(s => IsTransition(s.Status))) return "working";
        return $"{services.Count(s => s.Status == ServiceStatus.Running)}/{services.Length} running";
    }
    private void Add()
    {
        var data = IconData();
        ShellNotifyIcon(0, ref data);
        data.VersionOrTimeout = 4;
        ShellNotifyIcon(4, ref data);
    }
    private void Modify()
    {
        if (_disposed) return;
        var data = IconData();
        ShellNotifyIcon(1, ref data);
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == _taskbarCreated) { Add(); handled = true; }
        if ((uint)message == CallbackMessage)
        {
            var notification = unchecked((uint)lParam.ToInt64()) & 0xffff;
            if (notification is 0x400 or 0x401) OpenRequested?.Invoke(this, EventArgs.Empty);
            else if (notification == 0x7b) OpenContextMenu();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void OpenContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            var running = _snapshot?.Services.Any(s => s.Status is ServiceStatus.Running or ServiceStatus.Starting) == true;
            var disabled = _snapshot is null || _snapshot.IsBusy || _snapshot.Services.Any(s => IsTransition(s.Status));
            AppendMenu(menu, 0, 1, "Open manager");
            AppendMenu(menu, disabled ? 3u : 0u, 2, running ? "Stop all" : "Start all");
            AppendMenu(menu, 0, 3, "Exit and stop services");
            GetCursorPos(out var point);
            SetForegroundWindow(_source.Handle);
            var choice = TrackPopupMenuEx(menu, 0x0100 | 0x0002, point.X, point.Y, _source.Handle, IntPtr.Zero);
            PostMessage(_source.Handle, 0, IntPtr.Zero, IntPtr.Zero);
            switch (choice)
            {
                case 1: OpenRequested?.Invoke(this, EventArgs.Empty); break;
                case 2: ToggleRequested?.Invoke(this, EventArgs.Empty); break;
                case 3: ExitRequested?.Invoke(this, EventArgs.Empty); break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    public void Position(Window window)
    {
        var identifier = new NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = _source.Handle, Id = IconId };
        if (ShellNotifyIconGetRect(ref identifier, out var iconRect) != 0)
        {
            GetCursorPos(out var cursor);
            iconRect = new RectNative { Left = cursor.X, Right = cursor.X + 1, Top = cursor.Y, Bottom = cursor.Y + 1 };
        }
        var monitor = MonitorFromRect(ref iconRect, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        uint dpiX = 96, dpiY = 96;
        try { GetDpiForMonitor(monitor, 0, out dpiX, out dpiY); } catch (DllNotFoundException) { }
        var scale = dpiX / 96d;
        var work = info.WorkArea;
        var desiredWidth = double.IsFinite(window.Width) ? window.Width : window.ActualWidth;
        var desiredHeight = double.IsFinite(window.Height) ? window.Height : window.ActualHeight;
        var width = Math.Min((int)Math.Ceiling(desiredWidth * scale), work.Right - work.Left - 24);
        var height = Math.Min((int)Math.Ceiling(desiredHeight * scale), work.Bottom - work.Top - 24);
        var x = Math.Clamp(iconRect.Right - width, work.Left + 12, Math.Max(work.Left + 12, work.Right - width - 12));
        var y = iconRect.Top - height - 8;
        if (y < work.Top + 12) y = iconRect.Bottom + 8;
        y = Math.Clamp(y, work.Top + 12, Math.Max(work.Top + 12, work.Bottom - height - 12));
        SetWindowPos(new WindowInteropHelper(window).Handle, new IntPtr(-1), x, y, width, height, 0x0040);
    }

    private static IntPtr CreateStatusIcon(byte red, byte green, byte blue, int frame = -1)
    {
        const int size = 32;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = x - 15.5; var dy = y - 15.5;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var inside = distance <= 13.8;
            if (!inside) continue;
            var r = red; var g = green; var b = blue;
            if (frame >= 0)
            {
                if (distance < 8.7) continue;
                var angle = (Math.Atan2(dy, dx) + Math.PI * 2) % (Math.PI * 2);
                var phase = (angle / (Math.PI * 2) - frame / 10d + 1) % 1;
                var opacity = .25 + .75 * phase;
                r = (byte)(r * opacity); g = (byte)(g * opacity); b = (byte)(b * opacity);
            }
            else if ((x is >= 9 and <= 22 && y is >= 9 and <= 12) ||
                     (x is >= 9 and <= 22 && y is >= 15 and <= 18) ||
                     (x is >= 9 and <= 22 && y is >= 21 and <= 23))
            { r = 255; g = 255; b = 255; }
            var offset = (y * size + x) * 4;
            pixels[offset] = b; pixels[offset + 1] = g; pixels[offset + 2] = r; pixels[offset + 3] = 255;
        }

        var bitmap = new BitmapInfo
        {
            Header = new BitmapInfoHeader { Size = 40, Width = size, Height = -size, Planes = 1, BitCount = 32, SizeImage = size * size * 4 },
        };
        var color = CreateDIBSection(IntPtr.Zero, ref bitmap, 0, out var bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        try
        {
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var iconInfo = new IconInfo { IsIcon = true, Mask = mask, Color = color };
            var icon = CreateIconIndirect(ref iconInfo);
            if (icon == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return icon;
        }
        finally { DeleteObject(color); if (mask != IntPtr.Zero) DeleteObject(mask); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _animation.Stop();
        var data = IconData();
        ShellNotifyIcon(2, ref data);
        _disposed = true;
        foreach (var icon in _icons.Values.Concat(_frames)) DestroyIcon(icon);
        _source.RemoveHook(WindowProcedure);
        _source.Dispose();
    }

    private enum TrayState { Stopped, Healthy, Partial, Failed, Busy }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NotifyIconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RectNative { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public RectNative Monitor, WorkArea; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool IsIcon; public uint HotspotX, HotspotY; public IntPtr Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Color; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")] private static extern int ShellNotifyIconGetRect(ref NotifyIconIdentifier identifier, out RectNative rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RectNative rect, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo bitmapInfo, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, IntPtr bits);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateIconIndirect(ref IconInfo iconInfo);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
}
