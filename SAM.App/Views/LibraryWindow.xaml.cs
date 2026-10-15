using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SAM.App.Helpers;
using SAM.App.Library;
using SAM.App.Localization;
using SAM.App.Steam;
using Windows.ApplicationModel.DataTransfer;

namespace SAM.App.Views;

/// <summary>Game library - the WinUI 3 version of SAM.Picker.</summary>
public sealed partial class LibraryWindow : Window, INotifier
{
    private const double CardMinWidth = 236;
    private const double CardMargin = 12;
    private const double CardTextHeight = 58;

    private readonly WindowChrome _chrome;
    private readonly DispatcherQueueTimer _notifyTimer;
    private readonly List<GameEntry> _games = [];
    private readonly Dictionary<uint, GameEntry> _gamesById = [];

    private SteamSession? _session;
    private ListsWindow? _listsWindow;
    private CancellationTokenSource? _loadCancellation;
    private bool _isLoading;

    public LibraryWindow()
    {
        InitializeComponent();

        Root.FlowDirection = AppLanguage.FlowDirection;
        SearchBox.FlowDirection = AppLanguage.FlowDirection;
        LanguageButtonText.Text = AppLanguage.ShortName;

        _chrome = WindowChrome.Apply(this, Root, AppTitleBar, RightInsetColumn, SearchBox);
        _chrome.CenterOnScreen(1320, 880, 820, 560);

        _notifyTimer = DispatcherQueue.CreateTimer();
        _notifyTimer.Interval = TimeSpan.FromSeconds(4);
        _notifyTimer.IsRepeating = false;
        _notifyTimer.Tick += (_, _) => NotifyBar.IsOpen = false;

        Root.Loaded += async (_, _) =>
        {
            if (StartupMessage is not null)
            {
                Notify(StartupMessage, InfoBarSeverity.Warning);
            }

            await ConnectAndLoadAsync();
        };

        Closed += (_, _) =>
        {
            _listsWindow?.Close();
            _loadCancellation?.Cancel();
            _session?.Dispose();
        };
    }

    /// <summary>Notice at startup, e.g. for an invalid app ID on the command line.</summary>
    public string? StartupMessage { get; set; }

    // ---------------------------------------------------------------- Steam & game list

    private async Task ConnectAndLoadAsync()
    {
        ShowLoading(Strings.ConnectingToSteam, null);
        SetHeaderEnabled(false);
        RefreshButton.IsEnabled = false;

        _session?.Dispose();
        _session = null;

        try
        {
            _session = SteamSession.Connect(0, DispatcherQueue);
        }
        catch (SteamConnectException e)
        {
            ShowError(e.Title, e.Message, e.CanRetry);
            return;
        }

        var appDataChanged = _session.Client.CreateAndRegisterCallback<API.Callbacks.AppDataChanged>();
        appDataChanged.OnRun += OnAppDataChanged;

        SetHeaderEnabled(true);
        await LoadGamesAsync();
    }

    private void SetHeaderEnabled(bool enabled)
    {
        SearchBox.IsEnabled = enabled;
        FilterButton.IsEnabled = enabled;
        ListsButton.IsEnabled = enabled;
        SortButton.IsEnabled = enabled;
        OpenByIdButton.IsEnabled = enabled;
    }

    private async Task LoadGamesAsync()
    {
        if (_session is not { } session)
        {
            return;
        }

        _loadCancellation?.Cancel();
        var cancellation = _loadCancellation = new CancellationTokenSource();
        var token = cancellation.Token;

        _isLoading = true;
        RefreshButton.IsEnabled = false;
        ListInfoBar.IsOpen = false;
        ShowLoading(Strings.LoadingGameList, null);

        var list = await GameListSource.LoadAsync(token);

        if (token.IsCancellationRequested)
        {
            return;
        }

        if (list.Error is not null)
        {
            ListInfoBar.Title = Strings.GameListUnavailable;
            ListInfoBar.Message = list.FromCache ? Strings.GameListFromCache : Strings.GameListFallback;
            ListInfoBar.IsOpen = true;
        }

        ShowLoading(Strings.MatchingLibrary, 0);
        LoadingDetail.Text = Strings.KnownGames(list.Games.Count.ToString("N0", CultureInfo.CurrentCulture));
        var progress = new Progress<double>(value => LoadingProgress.Value = value);

        List<GameEntry> owned;

        try
        {
            owned = await Task.Run(() => ScanOwnership(session, list.Games, progress, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _games.Clear();
        _gamesById.Clear();

        foreach (var game in owned)
        {
            _games.Add(game);
            _gamesById[game.Id] = game;
        }

        _isLoading = false;
        RefreshButton.IsEnabled = true;
        ApplyFilter();
    }

    /// <summary>Like AddGame in the original: only games owned by the account, with name and capsule image.</summary>
    private static List<GameEntry> ScanOwnership(SteamSession session, IReadOnlyList<(uint Id, string Type)> games, IProgress<double> progress, CancellationToken token)
    {
        var result = new List<GameEntry>();
        var seen = new HashSet<uint>();
        string language;

        lock (session.Gate)
        {
            language = session.Client.SteamApps008.GetCurrentGameLanguage() ?? "english";
        }

        for (var i = 0; i < games.Count; i++)
        {
            if (i % 512 == 0)
            {
                token.ThrowIfCancellationRequested();
                progress.Report((double)i / games.Count);
            }

            var (id, type) = games[i];

            if (!seen.Add(id))
            {
                continue;
            }

            lock (session.Gate)
            {
                if (session.IsDisposed)
                {
                    throw new OperationCanceledException();
                }

                if (!session.Client.SteamApps008.IsSubscribedApp(id))
                {
                    continue;
                }

                var name = session.Client.SteamApps001.GetAppData(id, "name");
                var image = SteamImages.Header(session.Client, id, language);
                result.Add(new GameEntry(id, type, name, image));
            }
        }

        progress.Report(1);
        return result;
    }

    private void OnAppDataChanged(API.Types.AppDataChanged param)
    {
        // Runs in the callback timer, so the Steam lock is already held.
        if (!param.Result || _session is null || !_gamesById.TryGetValue(param.Id, out var game))
        {
            return;
        }

        game.Name = _session.Client.SteamApps001.GetAppData(game.Id, "name") ?? game.Name;
        var language = _session.Client.SteamApps008.GetCurrentGameLanguage() ?? "english";
        var image = SteamImages.Header(_session.Client, game.Id, language);

        if (image != game.ImageUrl)
        {
            game.ImageUrl = image;
            game.ReleaseImage();
            game.EnsureImage();
        }
    }

    // ---------------------------------------------------------------- Filter & view

    private void ApplyFilter()
    {
        if (_session is null || _isLoading)
        {
            return;
        }

        var wantNormals = FilterGamesItem.IsChecked;
        var wantDemos = FilterDemosItem.IsChecked;
        var wantMods = FilterModsItem.IsChecked;
        var wantJunk = FilterJunkItem.IsChecked;
        var search = SearchBox.Text.Trim();

        IEnumerable<GameEntry> query = _games.Where(game => game.Type switch
        {
            "normal" => wantNormals,
            "demo" => wantDemos,
            "mod" => wantMods,
            "junk" => wantJunk,
            _ => true,
        });

        if (search.Length > 0)
        {
            query = query.Where(game =>
                game.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                game.Id.ToString(CultureInfo.InvariantCulture) == search);
        }

        query = SortIdItem.IsChecked
            ? query.OrderBy(game => game.Id)
            : query.OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase);

        var filtered = query.ToList();
        GamesView.ItemsSource = filtered;

        var active = new[] { wantNormals, wantDemos, wantMods, wantJunk }.Count(on => on);
        FilterButtonText.Text = active == 1 && wantNormals ? Strings.Filter : Strings.FilterActive(active);
        SortButtonText.Text = SortIdItem.IsChecked ? Strings.SortAppId : Strings.SortName;

        SummaryText.Text = Strings.LibrarySummary(Number(_games.Count))
            + (filtered.Count == _games.Count ? "" : "  ·  " + Strings.LibraryShown(Number(filtered.Count)));

        if (filtered.Count > 0)
        {
            ShowPanel(GamesView);
            return;
        }

        if (_games.Count == 0)
        {
            EmptyTitle.Text = Strings.NoGamesWithAchievements;
            EmptyMessage.Text = Strings.NoGamesWithAchievementsHelp;
        }
        else
        {
            EmptyTitle.Text = Strings.NoGamesFound;
            EmptyMessage.Text = Strings.AdjustSearchOrFilter;
        }

        ShowPanel(EmptyPanel);
    }

    private static string Number(int count) => count.ToString("N0", CultureInfo.CurrentCulture);

    private void ShowLoading(string text, double? progress)
    {
        LoadingText.Text = text;
        LoadingDetail.Text = "";
        LoadingProgress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        LoadingProgress.Value = progress ?? 0;
        ShowPanel(LoadingPanel);
    }

    private void ShowError(string title, string message, bool canRetry)
    {
        ErrorTitle.Text = title;
        ErrorMessage.Text = message;
        RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = " ";
        ShowPanel(ErrorPanel);
    }

    private void ShowPanel(UIElement panel)
    {
        GamesView.Visibility = panel == GamesView ? Visibility.Visible : Visibility.Collapsed;
        LoadingPanel.Visibility = panel == LoadingPanel ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = panel == EmptyPanel ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = panel == ErrorPanel ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Notify(string message, InfoBarSeverity severity)
    {
        NotifyBar.Severity = severity;
        NotifyBar.Message = message;
        NotifyBar.IsOpen = true;
        _notifyTimer.Stop();
        _notifyTimer.Start();
    }

    // ---------------------------------------------------------------- Tiles

    private void OnGamesViewSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCardSize();

    /// <summary>Size the tiles so the rows fill the window width.</summary>
    private void UpdateCardSize()
    {
        if (GamesView.ItemsPanelRoot is not ItemsWrapGrid panel)
        {
            return;
        }

        var available = GamesView.ActualWidth - GamesView.Padding.Left - GamesView.Padding.Right - 2;

        if (available <= 0)
        {
            return;
        }

        var columns = Math.Max(1, (int)(available / (CardMinWidth + CardMargin)));
        var width = Math.Floor(available / columns);
        panel.ItemWidth = width;
        panel.ItemHeight = Math.Round((width - CardMargin) * 215 / 460) + CardTextHeight + CardMargin;
    }

    private void OnGameContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not GameEntry game)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            game.ReleaseImage();
        }
        else
        {
            game.EnsureImage();

            if (args.ItemIndex == 0)
            {
                UpdateCardSize();
            }
        }
    }

    private void OnGameClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GameEntry game)
        {
            OpenGame(game.Id, game.Name);
        }
    }

    /// <summary>Like OnActivateGame in the original: open the game in a process of its own.</summary>
    private void OpenGame(uint appId, string? name)
    {
        if (GameLauncher.OpenGame(appId) is { } error)
        {
            Notify(Strings.GameWindowFailed(error), InfoBarSeverity.Error);
        }
        else
        {
            Notify(Strings.GameOpening(name ?? Strings.AppFallbackName(appId)), InfoBarSeverity.Informational);
        }
    }

    private static uint MenuAppId(object sender) => (sender as FrameworkElement)?.Tag is uint id ? id : 0;

    private void OnOpenGameMenuClick(object sender, RoutedEventArgs e)
    {
        var id = MenuAppId(sender);

        if (id != 0)
        {
            OpenGame(id, _gamesById.GetValueOrDefault(id)?.Name);
        }
    }

    private void OnStorePageMenuClick(object sender, RoutedEventArgs e)
        => OpenUri("https://store.steampowered.com/app/" + MenuAppId(sender).ToString(CultureInfo.InvariantCulture));

    private void OnSteamLibraryMenuClick(object sender, RoutedEventArgs e)
        => OpenUri("steam://nav/games/details/" + MenuAppId(sender).ToString(CultureInfo.InvariantCulture));

    private void OnCopyIdMenuClick(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(MenuAppId(sender).ToString(CultureInfo.InvariantCulture));
        Clipboard.SetContent(package);
        Notify(Strings.AppIdCopied, InfoBarSeverity.Success);
    }

    private void OpenUri(string uri)
    {
        if (GameLauncher.OpenUri(uri) is { } error)
        {
            Notify(Strings.CouldNotOpen(error), InfoBarSeverity.Error);
        }
    }

    /// <summary>Achievement lists in a window of their own; it uses the library's Steam session.</summary>
    private void OnListsClick(object sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        if (_listsWindow is null)
        {
            _listsWindow = new ListsWindow(_session);
            _listsWindow.Closed += (_, _) => _listsWindow = null;
        }

        _listsWindow.Activate();
    }

    // ---------------------------------------------------------------- Header

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ApplyFilter();
        }
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void OnSortChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await LoadGamesAsync();

    private async void OnRetryClick(object sender, RoutedEventArgs e) => await ConnectAndLoadAsync();

    private void OnSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
    }

    private async void OnRefreshAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;

        if (RefreshButton.IsEnabled)
        {
            await LoadGamesAsync();
        }
    }

    private void OnOpenByIdFlyoutOpened(object sender, object e)
    {
        AppIdError.Visibility = Visibility.Collapsed;
        AppIdBox.SelectAll();
        AppIdBox.Focus(FocusState.Programmatic);
    }

    private void OnAppIdKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            OpenById();
        }
    }

    private void OnOpenByIdClick(object sender, RoutedEventArgs e) => OpenById();

    /// <summary>Successor of OnAddGame: checks ownership and opens the game directly.</summary>
    private void OpenById()
    {
        if (_session is null)
        {
            return;
        }

        if (!uint.TryParse(AppIdBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
        {
            ShowAppIdError(Strings.AppIdInvalid);
            return;
        }

        bool owns;
        string? name;

        lock (_session.Gate)
        {
            owns = _session.Client.SteamApps008.IsSubscribedApp(id);
            name = owns ? _session.Client.SteamApps001.GetAppData(id, "name") : null;
        }

        if (!owns)
        {
            ShowAppIdError(Strings.AppIdNotOwned);
            return;
        }

        OpenByIdFlyout.Hide();
        AppIdBox.Text = "";
        OpenGame(id, name);
    }

    private void ShowAppIdError(string message)
    {
        AppIdError.Text = message;
        AppIdError.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- Language

    private IReadOnlyList<LanguageOption>? _languages;

    private void OnLanguageFlyoutOpened(object sender, object e)
    {
        _languages ??= AppLanguage.Options();
        LanguageSearchBox.Text = "";
        LanguageList.ItemsSource = _languages;
        LanguageList.SelectedItem = _languages.FirstOrDefault(o => o.Locale == AppLanguage.Selected);

        if (LanguageList.SelectedItem is not null)
        {
            LanguageList.ScrollIntoView(LanguageList.SelectedItem, ScrollIntoViewAlignment.Leading);
        }

        LanguageSearchBox.Focus(FocusState.Programmatic);
    }

    private void OnLanguageSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_languages is null)
        {
            return;
        }

        var search = LanguageSearchBox.Text.Trim();
        LanguageList.ItemsSource = search.Length == 0
            ? _languages
            : _languages.Where(o => o.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                                    o.Subtitle.Contains(search, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    /// <summary>Save the language and restart the app so all texts and formats switch.</summary>
    private void OnLanguageClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LanguageOption option)
        {
            return;
        }

        LanguageFlyout.Hide();

        if (option.Locale == AppLanguage.Selected)
        {
            return;
        }

        AppLanguage.Save(option.Locale);

        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
            Close();
        }
        catch (Exception ex)
        {
            Notify(Strings.CouldNotOpen(ex.Message), InfoBarSeverity.Error);
        }
    }
}
