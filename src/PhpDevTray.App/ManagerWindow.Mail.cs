using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using PhpDevTray.Core;
namespace PhpDevTray.App;
internal sealed partial class ManagerWindow
{
    private void BuildMail()
    {
        Heading("Mail Trap", "A safe inbox for development mail.");
        if (MailIsRunning) AddUpdateControl();
        if (MailIsRunning) _pageActions.Children.Add(ActionButton("", CopySmtpAsync, "IconButtonStyle", AppIcon.Copy, "Copy SMTP settings", independent: true));
        if (!HasRuntime(ServiceKind.Mailpit))
        {
            _contentHost.Children.Add(Empty(AppIcon.Mail, "Keep test email close", "Capture messages from your apps in a local inbox, without sending them to real recipients.", "Install Mailpit", () => OpenInstallAsync(ServiceKind.Mailpit), "Import Mailpit", () => BrowseRuntimeAsync(ServiceKind.Mailpit)));
            return;
        }
        var outer = new Grid(); outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); outer.RowDefinitions.Add(new RowDefinition());
        outer.Children.Add(ServiceControl(ServiceKind.Mailpit, $"SMTP 127.0.0.1:{_snapshot.SmtpPort}"));
        if (!MailIsRunning)
        {
            var stopped = Empty(AppIcon.Mail, "Mail Trap is stopped", "Start Mail Trap to capture and read development email. Previously captured messages stay stored.", "Start Mail Trap", StartMailAsync, "Copy SMTP settings", CopySmtpAsync, primaryIcon: AppIcon.Play);
            Grid.SetRow(stopped, 1); outer.Children.Add(stopped);
        }
        else
        {
            var inbox = new Grid { Margin = new Thickness(0, 1, 0, 0) }; inbox.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) }); inbox.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) }); inbox.ColumnDefinitions.Add(new ColumnDefinition());
            inbox.RowDefinitions.Add(new RowDefinition()); inbox.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
            var listArea = new Grid { Margin = new Thickness(0, 0, 13, 0) }; listArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); listArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); listArea.RowDefinitions.Add(new RowDefinition());
            var searchInput = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            _mailSearch = new TextBox { ToolTip = "Search subject or sender", Padding = new Thickness(33, 8, 10, 8), MinHeight = 40 };
            AutomationProperties.SetName(_mailSearch, "Search captured messages"); searchInput.Children.Add(_mailSearch);
            AutomationProperties.SetHelpText(_mailSearch, "Search subject or sender. Press Control F to focus search.");
            var searchIcon = UiIcon.Create(AppIcon.Search, 15, "MutedBrush"); searchIcon.HorizontalAlignment = HorizontalAlignment.Left; searchIcon.VerticalAlignment = VerticalAlignment.Center; searchIcon.Margin = new Thickness(11, 0, 0, 0); searchIcon.IsHitTestVisible = false; searchInput.Children.Add(searchIcon);
            var placeholder = T("Search mail", 12, muted: true, margin: new Thickness(33, 0, 0, 0)); placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.IsHitTestVisible = false; searchInput.Children.Add(placeholder);
            _mailSearch.TextChanged += (_, _) => { placeholder.Visibility = _mailSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; FillMailList(); };
            listArea.Children.Add(searchInput);
            _mailResults = T(string.Empty, 11, muted: true, margin: new Thickness(0, 0, 0, 7));
            AutomationProperties.SetLiveSetting(_mailResults, AutomationLiveSetting.Polite);
            Grid.SetRow(_mailResults, 1); listArea.Children.Add(_mailResults);
            _mailList = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ScrollViewer.SetHorizontalScrollBarVisibility(_mailList, ScrollBarVisibility.Disabled);
            AutomationProperties.SetName(_mailList, "Captured messages"); _mailList.SelectionChanged += async (_, _) =>
            {
                if (_updatingInputs || _mailList.SelectedItem is not ListBoxItem { Tag: MailMessageSummary selected }) return;
                await ReadMessageAsync(selected);
            };
            Grid.SetRow(_mailList, 2); listArea.Children.Add(_mailList);
            var noResults = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            noResults.Children.Add(T("No matching messages", 13, true));
            noResults.Children.Add(T("Try another subject or sender, or clear your search.", 12, muted: true, margin: new Thickness(0, 6, 0, 12)));
            var clearSearch = ActionButton("Clear search", () => { if (_mailSearch is not null) { _mailSearch.Clear(); _mailSearch.Focus(); } return Task.CompletedTask; }, "SecondaryButtonStyle", independent: true);
            clearSearch.HorizontalAlignment = HorizontalAlignment.Left; noResults.Children.Add(clearSearch);
            _mailNoResults = new Border { Child = noResults, Padding = new Thickness(6, 12, 6, 0), Visibility = Visibility.Collapsed };
            Grid.SetRow(_mailNoResults, 2); listArea.Children.Add(_mailNoResults); inbox.Children.Add(listArea);
            var separator = new Border(); separator.SetResourceReference(Border.BackgroundProperty, "DividerBrush"); Grid.SetColumn(separator, 1); inbox.Children.Add(separator);
            var detail = new Grid { Margin = new Thickness(17, 0, 0, 0) }; detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); detail.RowDefinitions.Add(new RowDefinition());
            _mailDetail = detail;
            var messageHeader = new StackPanel { Margin = new Thickness(0, 0, 0, 13) }; _mailSubject = T("Select a message", 16, true); _mailMetadata = T(string.Empty, 11, muted: true, margin: new Thickness(0, 7, 0, 0)); messageHeader.Children.Add(_mailSubject); messageHeader.Children.Add(_mailMetadata); detail.Children.Add(messageHeader);
            _mailPreview = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = Brushes.Transparent, Text = _mailText };
            AutomationProperties.SetName(_mailPreview, "Message plain-text content"); Grid.SetRow(_mailPreview, 1); detail.Children.Add(_mailPreview); Grid.SetColumn(detail, 2); inbox.Children.Add(detail); Grid.SetRow(inbox, 1); outer.Children.Add(inbox);
            inbox.SizeChanged += (_, _) =>
            {
                var narrow = inbox.ActualWidth < 600;
                // Preserve the desktop master/detail layout at the supported minimum width.
                inbox.ColumnDefinitions[0].Width = new GridLength(narrow ? 160 : 220);
                listArea.Margin = new Thickness(0, 0, narrow ? 8 : 13, 0);
                detail.Margin = new Thickness(narrow ? 10 : 17, 0, 0, 0);
            };
            FillMailList();
        }
        _contentHost.Children.Add(outer);
    }

    private void FillMailList()
    {
        if (_mailList is null) return;
        var scroller = FindScrollViewer(_mailList);
        var offset = scroller?.VerticalOffset ?? 0;
        _updatingInputs = true;
        _mailList.Items.Clear(); var query = _mailSearch?.Text.Trim() ?? string.Empty;
        foreach (var message in _messages.Where(m => m.Subject.Contains(query, StringComparison.OrdinalIgnoreCase) || m.From.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            var content = new StackPanel { Margin = new Thickness(7, 9, 7, 9) };
            var subject = T(message.Subject, 12, true); subject.TextWrapping = TextWrapping.NoWrap; subject.TextTrimming = TextTrimming.CharacterEllipsis; subject.ToolTip = message.Subject; content.Children.Add(subject);
            var sender = T(message.From, 11, muted: true, margin: new Thickness(0, 4, 0, 0)); sender.TextWrapping = TextWrapping.NoWrap; sender.TextTrimming = TextTrimming.CharacterEllipsis; sender.ToolTip = message.From; content.Children.Add(sender);
            content.Children.Add(T(message.Created.LocalDateTime.ToString("g"), 11, muted: true, margin: new Thickness(0, 4, 0, 0)));
            var item = new ListBoxItem { Content = content, Tag = message, HorizontalContentAlignment = HorizontalAlignment.Stretch }; AutomationProperties.SetName(item, $"{message.Subject}, from {message.From}"); _mailList.Items.Add(item);
            if (message.Id == _selectedMessageId) _mailList.SelectedItem = item;
        }
        _updatingInputs = false;
        if (_mailResults is not null) _mailResults.Text = query.Length == 0 ? $"{_messages.Count} {(_messages.Count == 1 ? "message" : "messages")}" : $"{_mailList.Items.Count} of {_messages.Count} messages";
        var noMatches = _mailList.Items.Count == 0;
        if (_mailNoResults is not null) _mailNoResults.Visibility = noMatches ? Visibility.Visible : Visibility.Collapsed;
        if (_mailDetail is not null) _mailDetail.Visibility = noMatches ? Visibility.Collapsed : Visibility.Visible;
        _mailList.Visibility = noMatches ? Visibility.Collapsed : Visibility.Visible;
        _renderedMessages = _messages;
        scroller?.ScrollToVerticalOffset(offset);
        if (_selectedMessageId is { } id && _messages.FirstOrDefault(m => m.Id == id) is { } selected) SetMessageHeader(selected);
    }

    private async Task ReadMessageAsync(MailMessageSummary message, CancellationToken refreshToken = default)
    {
        _selectedMessageId = message.Id; SetMessageHeader(message);
        if (_loadedMessageId != message.Id) { _mailText = string.Empty; _loadedMessageId = null; }
        _messageCancellation?.Cancel(); _messageCancellation?.Dispose();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, refreshToken); _messageCancellation = cancellation;
        if (_mailPreview is not null) _mailPreview.Text = "Loading message…";
        try
        {
            var text = await _manager.GetMessageTextAsync(message.Id, cancellation.Token);
            if (!cancellation.IsCancellationRequested && _selectedMessageId == message.Id) { _mailText = text; _loadedMessageId = message.Id; if (_mailPreview is not null) _mailPreview.Text = text; }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (_selectedMessageId == message.Id && _mailPreview is not null) _mailPreview.Text = "This message could not be loaded. Select another message, then select this message to try again. Check Activity if the problem continues.";
            if (refreshToken.CanBeCanceled) throw;
            ShowError(exception.Message);
        }
        finally { if (ReferenceEquals(_messageCancellation, cancellation)) _messageCancellation = null; }
    }

    private void SetMessageHeader(MailMessageSummary message)
    {
        if (_mailSubject is not null) _mailSubject.Text = message.Subject;
        if (_mailMetadata is not null) _mailMetadata.Text = $"From  {message.From}\nTo  {message.To}\n{message.Created.LocalDateTime:g}";
    }

    private IEnumerable<MenuItem> MailMenu()
    {
        if (_snapshot.Services.FirstOrDefault(s => s.Kind == ServiceKind.Mailpit && s.Status != ServiceStatus.NotInstalled) is { } mail)
        {
            yield return Menu(mail.Status == ServiceStatus.Running ? "Stop Mail Trap" : "Start Mail Trap", () => ToggleServiceAsync(ServiceKind.Mailpit), !Busy);
            yield return Menu("Restart Mail Trap", () => RunAsync("Restarting Mail Trap…", async () => { await _manager.RestartServiceAsync(mail.Id, _lifetime.Token); await ReadInboxCoreAsync(); }), !Busy);
        }
        else yield return Menu("Install Mailpit…", () => OpenInstallAsync(ServiceKind.Mailpit), !Busy);
        yield return Menu("Copy SMTP settings", CopySmtpAsync);
        yield return Menu("Import Mailpit…", () => BrowseRuntimeAsync(ServiceKind.Mailpit), !Busy);
    }

    private Task CopySmtpAsync() => CopyTextAsync($"Host: 127.0.0.1\nPort: {_snapshot.SmtpPort}\nEncryption: None\nAuthentication: None", "SMTP settings copied to clipboard.");

    private Task CopyTextAsync(string text, string confirmation)
    {
        Clipboard.SetText(text); _notifications.Publish("Copied", confirmation, NotificationSeverity.Success); return Task.CompletedTask;
    }


}
