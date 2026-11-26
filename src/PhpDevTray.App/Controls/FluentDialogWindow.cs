using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Ui = Wpf.Ui.Controls;

namespace PhpDevTray.App;

/// <summary>Shared Fluent chrome for owned task windows. Closing a task never exits the tray app.</summary>
internal class FluentDialogWindow : Ui.FluentWindow
{
    private bool _wrappingContent;

    public FluentDialogWindow()
    {
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = Ui.WindowBackdropType.Mica;
        Icon = AppLogo.WindowIcon;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (_wrappingContent || newContent is not UIElement body) return;
        _wrappingContent = true;
        try
        {
            SetCurrentValue(ContentProperty, null);
            var shell = new Grid(); shell.SetResourceReference(Panel.BackgroundProperty, "ApplicationBackgroundBrush");
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            shell.RowDefinitions.Add(new RowDefinition());
            var titleBar = new Ui.TitleBar
            {
                ShowMinimize = false, ShowMaximize = false, ForceShutdown = false,
                Icon = new Ui.ImageIcon { Source = AppLogo.WindowIcon }
            };
            titleBar.SetBinding(Ui.TitleBar.TitleProperty, new Binding(nameof(Title)) { Source = this });
            shell.Children.Add(titleBar);
            Grid.SetRow(body, 1); shell.Children.Add(body);
            SetCurrentValue(ContentProperty, shell);
        }
        finally { _wrappingContent = false; }
    }
}
