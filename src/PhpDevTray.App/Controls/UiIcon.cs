using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PhpDevTray.App;

public enum AppIcon
{
    App, Server, Terminal, Php, Code, Sites, Globe, Mail, Logs, Download, Settings, Play, Stop,
    Restart, Plus, Folder, Edit, More, Close, Refresh, Check, Search, ChevronDown, ChevronRight,
    Error, Copy, ExternalLink, Info, Sun, Moon, Monitor
}

public static class UiIcon
{
    public static FrameworkElement Create(AppIcon icon, double size = 18, string? brushKey = null)
    {
        if (size <= 0 || !double.IsFinite(size)) throw new ArgumentOutOfRangeException(nameof(size));
        var element = new IconElement(icon) { Width = size, Height = size, Focusable = false, IsHitTestVisible = false };
        if (brushKey is not null) element.SetResourceReference(IconElement.ForegroundProperty, brushKey);
        return element;
    }

    private sealed class IconElement : FrameworkElement
    {
        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(IconElement),
            new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));
        private readonly Geometry _geometry;

        public IconElement(AppIcon icon)
        {
            _geometry = Geometries[icon];
            SnapsToDevicePixels = false;
            UseLayoutRounding = true;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            var brush = (Brush)GetValue(ForegroundProperty);
            var pen = new Pen(brush, 1.65) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var scale = Math.Min(ActualWidth, ActualHeight) / 24;
            drawingContext.PushTransform(new TranslateTransform((ActualWidth - 24 * scale) / 2, (ActualHeight - 24 * scale) / 2));
            drawingContext.PushTransform(new ScaleTransform(scale, scale));
            drawingContext.DrawGeometry(null, pen, _geometry);
            drawingContext.Pop();
            drawingContext.Pop();
        }
    }

    private static readonly IReadOnlyDictionary<AppIcon, Geometry> Geometries = BuildGeometries();

    private static IReadOnlyDictionary<AppIcon, Geometry> BuildGeometries()
    {
        var paths = new Dictionary<AppIcon, string>
        {
            [AppIcon.Server] = "M5,3.5 H19 A1.5,1.5 0 0 1 20.5,5 V9 A1.5,1.5 0 0 1 19,10.5 H5 A1.5,1.5 0 0 1 3.5,9 V5 A1.5,1.5 0 0 1 5,3.5 Z M5,13.5 H19 A1.5,1.5 0 0 1 20.5,15 V19 A1.5,1.5 0 0 1 19,20.5 H5 A1.5,1.5 0 0 1 3.5,19 V15 A1.5,1.5 0 0 1 5,13.5 Z M7,7 H7.1 M7,17 H7.1 M11,7 H17 M11,17 H17",
            [AppIcon.Terminal] = "M4.5,4.5 H19.5 A1.5,1.5 0 0 1 21,6 V18 A1.5,1.5 0 0 1 19.5,19.5 H4.5 A1.5,1.5 0 0 1 3,18 V6 A1.5,1.5 0 0 1 4.5,4.5 Z M7,9 L10,12 L7,15 M13,15 H17",
            [AppIcon.Code] = "M8,7 L3,12 L8,17 M16,7 L21,12 L16,17 M13.5,4.5 L10.5,19.5",
            [AppIcon.Globe] = "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 Z M3,12 H21 M12,3 C6.5,8 6.5,16 12,21 C17.5,16 17.5,8 12,3 Z",
            [AppIcon.Mail] = "M4.5,5 H19.5 A1.5,1.5 0 0 1 21,6.5 V17.5 A1.5,1.5 0 0 1 19.5,19 H4.5 A1.5,1.5 0 0 1 3,17.5 V6.5 A1.5,1.5 0 0 1 4.5,5 Z M3.5,6 L12,12.5 L20.5,6",
            [AppIcon.Logs] = "M6,3.5 H15 L19.5,8 V20.5 H6 Z M14.5,3.5 V8.5 H19.5 M9,12 H16 M9,15.5 H16 M9,19 H13",
            [AppIcon.Download] = "M12,3.5 V15 M7.5,10.5 L12,15 L16.5,10.5 M4,15 V19 A1.5,1.5 0 0 0 5.5,20.5 H18.5 A1.5,1.5 0 0 0 20,19 V15",
            [AppIcon.Settings] = "M9.7,3 H14.3 L14.8,5.5 L17,6.8 L19.4,6 L21.5,9.6 L19.6,11.2 V13.8 L21.5,15.4 L19.4,19 L17,18.2 L14.8,19.5 L14.3,21.5 H9.7 L9.2,19.5 L7,18.2 L4.6,19 L2.5,15.4 L4.4,13.8 V11.2 L2.5,9.6 L4.6,6 L7,6.8 L9.2,5.5 Z M15.5,12.3 A3.5,3.5 0 1 1 8.5,12.3 A3.5,3.5 0 1 1 15.5,12.3 Z",
            [AppIcon.Play] = "M8,4.5 L20,12 L8,19.5 Z",
            [AppIcon.Stop] = "M6,5.5 H18 A.5,.5 0 0 1 18.5,6 V18 A.5,.5 0 0 1 18,18.5 H6 A.5,.5 0 0 1 5.5,18 V6 A.5,.5 0 0 1 6,5.5 Z",
            [AppIcon.Refresh] = "M20,9 A8,8 0 0 0 6.2,5.8 L3,9 M3,4 V9 H8 M4,15 A8,8 0 0 0 17.8,18.2 L21,15 M16,15 H21 V20",
            [AppIcon.Plus] = "M12,5 V19 M5,12 H19",
            [AppIcon.Folder] = "M3.5,7 V5.5 A1,1 0 0 1 4.5,4.5 H9 L11,7 H19.5 A1,1 0 0 1 20.5,8 V18.5 A1,1 0 0 1 19.5,19.5 H4.5 A1,1 0 0 1 3.5,18.5 Z M3.5,9.5 H20.5",
            [AppIcon.Edit] = "M14.5,5 L19,9.5 M4.5,19.5 L6,14 L16.5,3.5 A1.5,1.5 0 0 1 18.6,3.5 L20.5,5.4 A1.5,1.5 0 0 1 20.5,7.5 L10,18 Z",
            [AppIcon.More] = "M5,12 A1,1 0 1 1 3,12 A1,1 0 1 1 5,12 Z M13,12 A1,1 0 1 1 11,12 A1,1 0 1 1 13,12 Z M21,12 A1,1 0 1 1 19,12 A1,1 0 1 1 21,12 Z",
            [AppIcon.Close] = "M6,6 L18,18 M18,6 L6,18",
            [AppIcon.Check] = "M4.5,12.5 L9.5,17.5 L20,6.5",
            [AppIcon.Search] = "M16.5,10 A6.5,6.5 0 1 1 3.5,10 A6.5,6.5 0 1 1 16.5,10 Z M15,15 L20.5,20.5",
            [AppIcon.ChevronDown] = "M6,9 L12,15 L18,9",
            [AppIcon.ChevronRight] = "M9,6 L15,12 L9,18",
            [AppIcon.Error] = "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 Z M12,7 V13 M12,16.5 V16.7",
            [AppIcon.Info] = "M21,12 A9,9 0 1 1 3,12 A9,9 0 1 1 21,12 Z M12,11 V17 M12,7.2 V7.4",
            [AppIcon.Copy] = "M8.5,8.5 H20.5 V20.5 H8.5 Z M15.5,8.5 V3.5 H3.5 V15.5 H8.5",
            [AppIcon.ExternalLink] = "M14,3.5 H20.5 V10 M20.5,3.5 L11,13 M10,5 H5 A1.5,1.5 0 0 0 3.5,6.5 V19 A1.5,1.5 0 0 0 5,20.5 H17.5 A1.5,1.5 0 0 0 19,19 V14",
            [AppIcon.Sun] = "M16.5,12 A4.5,4.5 0 1 1 7.5,12 A4.5,4.5 0 1 1 16.5,12 Z M12,2 V4 M12,20 V22 M2,12 H4 M20,12 H22 M5,5 L6.5,6.5 M17.5,17.5 L19,19 M19,5 L17.5,6.5 M6.5,17.5 L5,19",
            [AppIcon.Moon] = "M20.5,14.5 A9,9 0 0 1 9.5,3.5 A9,9 0 1 0 20.5,14.5 Z",
            [AppIcon.Monitor] = "M4.5,3.5 H19.5 A1.5,1.5 0 0 1 21,5 V15 A1.5,1.5 0 0 1 19.5,16.5 H4.5 A1.5,1.5 0 0 1 3,15 V5 A1.5,1.5 0 0 1 4.5,3.5 Z M12,16.5 V21 M7.5,21 H16.5"
        };
        paths[AppIcon.Php] = paths[AppIcon.Code];
        paths[AppIcon.Sites] = paths[AppIcon.Globe];
        paths[AppIcon.Restart] = paths[AppIcon.Refresh];
        var geometries = paths.ToDictionary(pair => pair.Key, pair => { var geometry = Geometry.Parse(pair.Value); geometry.Freeze(); return geometry; });
        geometries[AppIcon.App] = AppLogo.Glyph;
        return geometries;
    }
}
