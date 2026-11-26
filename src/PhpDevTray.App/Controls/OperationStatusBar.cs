using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using PhpDevTray.Core;
using Button = Wpf.Ui.Controls.Button;

namespace PhpDevTray.App;

/// <summary>Compact ongoing progress; an empty idle state preserves the page layout.</summary>
internal sealed class OperationStatusBar : Grid, IDisposable
{
    private readonly OperationTracker _operations;
    private readonly ProgressBar _progress = new() { Width = 64, Height = 3, Margin = new Thickness(16, 0, 12, 0), Minimum = 0, Maximum = 1 };
    private readonly Button _cancel = new() { Content = "Cancel", Padding = new Thickness(8, 2, 8, 2), FontSize = 12, Margin = new Thickness(12, 0, 8, 0) };
    private readonly DispatcherTimer _timer;
    private Guid? _current;
    public TextBlock Message { get; } = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    public event EventHandler? Tick;

    public OperationStatusBar(OperationTracker operations, NotificationCenter notifications)
    {
        _operations = operations;
        Height = 36; SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Children.Add(_progress); SetColumn(Message, 1); Children.Add(Message); SetColumn(_cancel, 2); Children.Add(_cancel);
        AutomationProperties.SetName(_progress, "Operation progress"); AutomationProperties.SetName(_cancel, "Cancel current operation");
        _cancel.Click += (_, _) =>
        {
            try { if (_current is { } id) _operations.Cancel(id); }
            catch (Exception error) { notifications.Publish("Cancellation failed", error.Message, NotificationSeverity.Error); }
        };
        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => { Refresh(); Tick?.Invoke(this, EventArgs.Empty); };
        IsVisibleChanged += (_, _) => { _timer.IsEnabled = IsVisible; if (IsVisible) Refresh(); };
        Refresh();
    }
    private void Refresh()
    {
        var active = _operations.Snapshot.Where(item => item.State == OperationState.Running && DateTimeOffset.UtcNow - item.Started > TimeSpan.FromMilliseconds(120)).ToArray();
        var current = active.FirstOrDefault(); _current = current?.Id;
        Message.Text = current?.Detail ?? current?.Name ?? "";
        if (active.Length > 1) Message.Text += $" ({active.Length} running)";
        Message.ToolTip = Message.Text;
        _progress.Visibility = current is null ? Visibility.Hidden : Visibility.Visible;
        _progress.IsIndeterminate = current is not null && current.Progress is null && TryFindResource("MotionEnabled") is true;
        _progress.Value = current?.Progress ?? 1;
        _cancel.Visibility = current?.CanCancel is true ? Visibility.Visible : Visibility.Hidden;
    }
    public void Dispose() => _timer.Stop();
}
