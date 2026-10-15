using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SAM.App.Helpers;
using SAM.App.Lists;
using SAM.App.Localization;
using SAM.App.Steam;
using Windows.ApplicationModel.DataTransfer;

namespace SAM.App.Views;

/// <summary>
/// Achievement lists following the Steam collections "🎖️ 1.0.0 All" to "🎖️ 1.2.4 👀✖ Can't Finish",
/// with export as guide text. Only reads Steam's cache - no game is launched and nothing is written to Steam.
/// </summary>
public sealed partial class ListsWindow : Window, INotifier
{
    private readonly SteamSession _session;
    private readonly WindowChrome _chrome;
    private readonly DispatcherQueueTimer _notifyTimer;

    private uint _accountId;
    private ListState _state = new();
    private List<(CachedProgress Progress, string Name)> _raw = [];
    private List<ListCategory> _categories = [];
    private DateTime _cacheTime;
    private bool _loading;
    private bool _dialogOpen;

    internal ListsWindow(SteamSession session)
    {
        _session = session;
        InitializeComponent();

        Root.FlowDirection = AppLanguage.FlowDirection;
        SearchBox.FlowDirection = AppLanguage.FlowDirection;
        Title = Strings.ListsTitle + " – Steam Achievement Manager";
        TitleText.Text = "Steam Achievement Manager  ·  " + Strings.ListsTitle;

        _chrome = WindowChrome.Apply(this, Root, AppTitleBar, RightInsetColumn);
        _chrome.CenterOnScreen(1280, 900, 900, 600);

        _notifyTimer = DispatcherQueue.CreateTimer();
        _notifyTimer.Interval = TimeSpan.FromSeconds(4);
        _notifyTimer.IsRepeating = false;
        _notifyTimer.Tick += (_, _) => NotifyBar.IsOpen = false;

        Root.Loaded += async (_, _) => await LoadAsync();
    }

    // ---------------------------------------------------------------- Loading

    private async Task LoadAsync()
    {
        if (_loading || _session.IsDisposed)
        {
            return;
        }

        _loading = true;
        SetCommandsEnabled(false);
        ShowPanel(LoadingPanel);
        LoadingText.Text = Strings.ListsLoading;

        try
        {
            ulong steamId;

            lock (_session.Gate)
            {
                steamId = _session.Client.SteamUser.GetSteamId();
            }

            _accountId = (uint)(steamId & 0xFFFFFFFF);
            var path = AchievementCache.PathFor(API.Steam.GetInstallPath() ?? "", _accountId);

            if (!File.Exists(path))
            {
                ShowError(Strings.ListsCacheMissingTitle, Strings.ListsCacheMissing(path));
                return;
            }

            _state = ListState.Load(_accountId);
            _cacheTime = File.GetLastWriteTime(path);
            var session = _session;
            var state = _state;
            LoadingText.Text = Strings.ListsMatching;

            _raw = await Task.Run(() => ReadOwned(session, state, path));
            Rebuild(updateHistory: true);
            ShowPanel(ContentGrid);
        }
        catch (OperationCanceledException)
        {
            // The library was closed, the Steam session is gone.
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShowError(Strings.ListsCacheMissingTitle, Strings.ListsReadError(e.Message));
        }
        finally
        {
            _loading = false;
            SetCommandsEnabled(true);
        }
    }

    /// <summary>Only games with achievements that the account owns - with names from Steam's app data.</summary>
    private static List<(CachedProgress, string)> ReadOwned(SteamSession session, ListState state, string path)
    {
        var result = new List<(CachedProgress, string)>();

        foreach (var entry in AchievementCache.Read(path))
        {
            if (entry.Total <= 0 && !state.CantFinish.ContainsKey(entry.AppId) && !state.NotAnymoreManual.Contains(entry.AppId))
            {
                continue;
            }

            lock (session.Gate)
            {
                if (session.IsDisposed)
                {
                    throw new OperationCanceledException();
                }

                if (!session.Client.SteamApps008.IsSubscribedApp(entry.AppId))
                {
                    continue;
                }

                var name = session.Client.SteamApps001.GetAppData(entry.AppId, "name");
                result.Add((entry, string.IsNullOrWhiteSpace(name) ? Strings.AppFallbackName(entry.AppId.ToString(CultureInfo.InvariantCulture)) : name.Trim()));
            }
        }

        return result;
    }

    /// <summary>Rebuild the categories, e.g. after a mark. The selection is kept.</summary>
    private void Rebuild(bool updateHistory)
    {
        var selected = CategoryList.SelectedItem as ListCategory;
        var games = _raw.Select(r => (Game: new ListGame(r.Progress.AppId, r.Name, r.Progress.Unlocked, r.Progress.Total, r.Progress.Vetted),
                                      Complete: r.Progress.Total > 0 && (r.Progress.AllUnlocked || r.Progress.Unlocked >= r.Progress.Total)))
                        .ToList();

        if (updateHistory && ListBuilder.UpdateHistory(_state, games, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
        {
            SaveState();
        }

        foreach (var (game, complete) in games)
        {
            ListBuilder.Classify(_state, game, complete);
        }

        _categories = ListBuilder.Build(games.Select(g => g.Game).ToList());
        CategoryList.ItemsSource = _categories;
        CategoryList.SelectedItem = _categories.FirstOrDefault(c => selected is not null && c.Scope == selected.Scope && c.Kind == selected.Kind) ?? _categories[0];

        var withAchievements = _categories.Sum(c => c.Scope == ListScope.All && c.Kind is ListKind.All or ListKind.Untouched ? c.Games.Count : 0);
        SummaryText.Text = Strings.ListsSummary(_cacheTime.ToString("g", CultureInfo.CurrentCulture), withAchievements.ToString("N0", CultureInfo.CurrentCulture));
    }

    private void SaveState()
    {
        try
        {
            _state.Save(_accountId);
        }
        catch (Exception e)
        {
            Notify(Strings.ListsSaveError(e.Message), InfoBarSeverity.Error);
        }
    }

    // ---------------------------------------------------------------- View

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e) => ShowGames();

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ShowGames();
        }
    }

    private List<ListGame> VisibleGames()
    {
        if (CategoryList.SelectedItem is not ListCategory category)
        {
            return [];
        }

        var search = SearchBox.Text.Trim();
        return search.Length == 0
            ? category.Games
            : category.Games.Where(g => g.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                                        g.AppId.ToString(CultureInfo.InvariantCulture) == search).ToList();
    }

    private void ShowGames()
    {
        if (CategoryList.SelectedItem is not ListCategory category)
        {
            return;
        }

        var games = VisibleGames();
        CategoryTitle.Text = category.Title + "  (" + category.CountText + ")";
        CategoryDescription.Text = category.Description;
        GamesList.ItemsSource = games;
        EmptyText.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string title, string message)
    {
        ErrorTitle.Text = title;
        ErrorMessage.Text = message;
        ShowPanel(ErrorPanel);
    }

    private void ShowPanel(UIElement panel)
    {
        ContentGrid.Visibility = panel == ContentGrid ? Visibility.Visible : Visibility.Collapsed;
        LoadingPanel.Visibility = panel == LoadingPanel ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = panel == ErrorPanel ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetCommandsEnabled(bool enabled)
    {
        RefreshButton.IsEnabled = enabled;
        ExportButton.IsEnabled = enabled && _categories.Count > 0;
    }

    public void Notify(string message, InfoBarSeverity severity)
    {
        NotifyBar.Severity = severity;
        NotifyBar.Message = message;
        NotifyBar.IsOpen = true;
        _notifyTimer.Stop();
        _notifyTimer.Start();
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen)
        {
            return ContentDialogResult.None;
        }

        _dialogOpen = true;

        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // ---------------------------------------------------------------- Copy

    private void OnCopyIdsClick(object sender, RoutedEventArgs e)
        => Copy(VisibleGames().Select(g => g.AppId.ToString(CultureInfo.InvariantCulture)).ToList());

    private void OnCopyNamesClick(object sender, RoutedEventArgs e)
        => Copy(VisibleGames().Select(g => g.GuideLine).ToList());

    private void Copy(List<string> lines)
    {
        CopyText(string.Join("\r\n", lines));
        Notify(Strings.CopiedCount(lines.Count.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Success);
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    // ---------------------------------------------------------------- Marks

    private static uint MenuAppId(object sender) => (sender as FrameworkElement)?.Tag is uint id ? id : 0;

    private ListGame? FindGame(uint appId) => _categories.SelectMany(c => c.Games).FirstOrDefault(g => g.AppId == appId);

    private async void OnMarkCantFinishClick(object sender, RoutedEventArgs e)
    {
        if (FindGame(MenuAppId(sender)) is not { } game)
        {
            return;
        }

        var reason = new TextBox
        {
            PlaceholderText = Strings.CantFinishReasonPlaceholder,
            Text = game.CantFinishReason ?? "",
            FlowDirection = AppLanguage.FlowDirection,
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(new TextBlock { Text = Strings.CantFinishReasonHint, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["SecondaryTextStyle"] });
        panel.Children.Add(reason);

        var dialog = Dialogs.Create(Root.XamlRoot, Strings.CantFinishDialogTitle(game.Name), panel, Strings.Mark);

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        _state.CantFinish[game.AppId] = reason.Text.Trim();
        SaveState();
        Rebuild(updateHistory: false);
    }

    private void OnUnmarkCantFinishClick(object sender, RoutedEventArgs e)
    {
        if (_state.CantFinish.Remove(MenuAppId(sender)))
        {
            SaveState();
            Rebuild(updateHistory: false);
        }
    }

    private void OnMarkNotAnymoreClick(object sender, RoutedEventArgs e)
    {
        var appId = MenuAppId(sender);
        _state.NotAnymoreManual.Add(appId);
        _state.NotAnymoreDismissed.Remove(appId);
        SaveState();
        Rebuild(updateHistory: false);
    }

    private void OnUnmarkNotAnymoreClick(object sender, RoutedEventArgs e)
    {
        var appId = MenuAppId(sender);
        _state.NotAnymoreManual.Remove(appId);

        // Detected automatically: remember that it should not be shown any more.
        if (_state.Perfect.ContainsKey(appId))
        {
            _state.NotAnymoreDismissed.Add(appId);
        }

        SaveState();
        Rebuild(updateHistory: false);
    }

    private void OnOpenGameClick(object sender, RoutedEventArgs e)
    {
        if (GameLauncher.OpenGame(MenuAppId(sender)) is { } error)
        {
            Notify(Strings.GameWindowFailed(error), InfoBarSeverity.Error);
        }
    }

    private void OnStorePageClick(object sender, RoutedEventArgs e)
    {
        if (GameLauncher.OpenUri("https://store.steampowered.com/app/" + MenuAppId(sender).ToString(CultureInfo.InvariantCulture)) is { } error)
        {
            Notify(Strings.CouldNotOpen(error), InfoBarSeverity.Error);
        }
    }

    // ---------------------------------------------------------------- Guide text

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_categories.Count == 0)
        {
            return;
        }

        var includeUntouched = new CheckBox { Content = Strings.ExportIncludeUntouched, IsChecked = true };
        var limit = new NumberBox
        {
            Header = Strings.ExportLimit,
            Value = GuideExport.DefaultLimit,
            Minimum = 500,
            Maximum = 100000,
            SmallChange = 500,
            LargeChange = 1000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 220,
        };
        var sectionCount = new TextBlock { VerticalAlignment = VerticalAlignment.Bottom, Style = (Style)Application.Current.Resources["SecondaryTextStyle"] };
        var sectionList = new ListView { SelectionMode = ListViewSelectionMode.Single, Width = 340, Height = 420 };
        var preview = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 420,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(preview, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(preview, ScrollBarVisibility.Auto);

        var copyTitle = new Button { Content = Strings.ExportCopyTitle };
        var copyText = new Button { Content = Strings.ExportCopyText, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var saveAll = new Button { Content = Strings.ExportSaveAll };

        List<GuideSection> sections = [];

        void Generate()
        {
            var value = double.IsNaN(limit.Value) ? GuideExport.DefaultLimit : (int)limit.Value;
            sections = GuideExport.Build(_categories, includeUntouched.IsChecked == true, value);
            sectionList.ItemsSource = sections.Select(s => s.Title + "\n" + s.LengthText).ToList();
            sectionList.SelectedIndex = 0;
            sectionCount.Text = Strings.ExportSections(sections.Count.ToString(CultureInfo.CurrentCulture));
        }

        GuideSection? Current() => sectionList.SelectedIndex >= 0 && sectionList.SelectedIndex < sections.Count ? sections[sectionList.SelectedIndex] : null;

        sectionList.SelectionChanged += (_, _) => preview.Text = Current()?.Body ?? "";
        includeUntouched.Click += (_, _) => Generate();
        limit.ValueChanged += (_, _) => Generate();
        copyTitle.Click += (_, _) =>
        {
            if (Current() is { } s)
            {
                CopyText(s.Title);
            }
        };
        copyText.Click += (_, _) =>
        {
            if (Current() is { } s)
            {
                CopyText(s.Body.Replace("\n", "\r\n"));
            }
        };

        var savedInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["SecondaryCaptionStyle"], IsTextSelectionEnabled = true };
        saveAll.Click += (_, _) =>
        {
            try
            {
                var folder = SaveSections(sections);
                savedInfo.Text = Strings.ExportSaved(folder);
                GameLauncher.OpenUri(folder);
            }
            catch (Exception ex)
            {
                savedInfo.Text = Strings.CouldNotOpen(ex.Message);
            }
        };

        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        options.Children.Add(limit);
        options.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { includeUntouched } });
        options.Children.Add(sectionCount);

        var body = new Grid { ColumnSpacing = 12 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(sectionList);
        Grid.SetColumn(preview, 1);
        body.Children.Add(preview);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(copyTitle);
        buttons.Children.Add(copyText);
        buttons.Children.Add(saveAll);

        var content = new StackPanel { Spacing = 12, Width = 940 };
        content.Children.Add(new TextBlock { Text = Strings.ExportHint, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["SecondaryTextStyle"] });
        content.Children.Add(options);
        content.Children.Add(body);
        content.Children.Add(buttons);
        content.Children.Add(savedInfo);

        var dialog = Dialogs.Create(Root.XamlRoot, Strings.ExportTitle, content, Strings.Close, showClose: false);
        dialog.Resources["ContentDialogMaxWidth"] = 1040.0;
        dialog.Resources["ContentDialogMaxHeight"] = 860.0;
        Generate();
        await ShowDialogAsync(dialog);
    }

    /// <summary>Each section as a text file of its own (heading in the first line) plus one file with everything.</summary>
    private static string SaveSections(List<GuideSection> sections)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Steam Achievement Manager",
            "Guide " + DateTime.Now.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        var invalid = Path.GetInvalidFileNameChars();
        var all = new List<string>();

        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var safe = new string(section.Title.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            var text = section.Title + "\r\n\r\n" + section.Body.Replace("\n", "\r\n") + "\r\n";
            File.WriteAllText(Path.Combine(folder, (i + 1).ToString("00", CultureInfo.InvariantCulture) + " " + safe + ".txt"), text);
            all.Add(text);
        }

        File.WriteAllText(Path.Combine(folder, "00 All sections.txt"), string.Join("\r\n\r\n", all));
        return folder;
    }

    // ---------------------------------------------------------------- Keyboard

    private void OnSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
    }

    private async void OnRefreshAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await LoadAsync();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await LoadAsync();
}
