using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using NekoDex.Services.Scrapers;
using Microsoft.Win32;

namespace MangaLibraryApp;

// ════════════════════════════════════════════════════════════════════════
//  Hauptfenster: Navigation · Tag-Suche · gallery-dl-Steuerung
// ════════════════════════════════════════════════════════════════════════

public partial class MainWindow : Window
{
    // ── Einstellungs-Schlüssel, die außerhalb von AppSettings.Save geschrieben werden ──
    private const string SettingSort = "Sort";
    private const string SettingSources = "EnabledSources"; // fehlt = alle Quellen

    private AppSettings _settings = new();

    /// <summary>Kacheln werden seitenweise nachgeladen, damit große Bibliotheken flüssig bleiben.</summary>
    private const int TilePageSize = 72;

    private const int MaxTagChips = 150;

    private static readonly Regex AnsiRegex = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private enum AppPage { Library, Downloads, WebSearch, Settings }

    private enum StatusKind { Info, Success, Warning, Error }

    // ── Zustand ──
    private Database _db = null!; // wird in Window_Loaded angelegt
    private readonly ObservableCollection<MangaTile> _tiles = new();
    private readonly ObservableCollection<TagChip> _tagChips = new();
    private readonly ObservableCollection<TagGroup> _tagGroups = new();
    private readonly ObservableCollection<DownloadJob> _jobs = new();
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _tagFilterTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private List<Manga> _results = new();
    private int _loadedCount;
    private int _searchVersion;
    private int _totalCount;
    private bool _ready;
    private ReadingStatus? _readingFilter;
    private string? _libraryCategory;
    private int? _minPages;
    private int? _maxPages;
    private bool _webHeaderAutoHide;
    private bool _webHeaderCollapsed;
    private bool _webHeaderHover;
    private IReadOnlyList<UpdateNotice> _updates = Array.Empty<UpdateNotice>();
    private MangaSort _sort = MangaSort.DateAddedDesc;
    private AppPage _page = AppPage.Library;

    private string _libraryPath = DefaultLibraryPath;
    private string _galleryDlPath = string.Empty;
    private string _extraArgs = string.Empty;
    private int _maxParallel = 2;
    private GalleryDlCommand? _galleryDl;

    private DownloadJob? _logJob;
    private bool _logDirty;
    private MangaTile? _editingTile;
    private readonly StringBuilder _sessionLog = new();
    private string _sessionLogTitle = "LOG";

    private TagService _tags = null!;
    private ApiSearchService _api = null!;
    private readonly ObservableCollection<ActiveTag> _webTags = new();
    private IReadOnlyList<TagSuggestion> _webSuggestionItems = Array.Empty<TagSuggestion>();
    private int _webTagLoad;
    private readonly ObservableCollection<WebSearchTile> _webHits = new();
    private string _webSource = "nhentai";
    private WebSearchViewModel? _webSearchModel;
    private bool _languageReady;
    private IScraperProvider? _activeScraper;
    private int _webPage = 1;
    private int _webRangeStart = 1;
    private const int WebPageLimit = 30;
    private bool _webSearching;
    private List<string> _webSearchTags = new();
    private string _webSearchExtra = string.Empty;
    private CancellationTokenSource? _webSearchCts;
    private bool _webHasMore;
    private bool _webApiTotals;
    private int? _webTotal;
    private int? _webTotalPages;
    private int _pageGeneration;
    private readonly object _pageGate = new();
    private readonly Dictionary<int, WebPageBatch> _pageCache = new();
    private CancellationTokenSource? _webTagDownloadCts;
    private int _webSuggestVersion;
    private bool _webTagDownloading;
    private string _flareSolverrUrl = string.Empty;
    private bool _cookieUiReady;

    private static string DefaultLibraryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MangaLibrary");

    public MainWindow()
    {
        try
        {
            App.Log("MainWindow.InitializeComponent …");
            InitializeComponent();
            App.Log("MainWindow: XAML geladen, binde Listen.");

            TileGrid.ItemsSource = _tiles;
            TagList.ItemsSource = _tagGroups;
            JobList.ItemsSource = _jobs;
            ActiveTagList.ItemsSource = _activeTags;
            SourcePopoutList.ItemsSource = _sourceFilters;
            SuggestList.ItemsSource = _suggestions;
            WebActiveTagList.ItemsSource = _webTags;
            SearchResultsControl.ItemsSource = _webHits;
            _webHits.CollectionChanged += (_, _) => ApplyWebDiscoveryVisibility();

            _activeTags.CollectionChanged += (_, e) =>
            {
                if (e.NewItems is not null)
                {
                    foreach (ActiveTag added in e.NewItems)
                        added.Changed += OnActiveTagsChanged;
                }

                OnActiveTagsChanged();
            };

            _jobs.CollectionChanged += (_, _) =>
            {
                JobsEmptyText.Visibility = _jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            };

            _searchTimer.Tick += async (_, _) =>
            {
                _searchTimer.Stop();
                await RefreshLibraryAsync();
            };

            _tagFilterTimer.Tick += async (_, _) =>
            {
                _tagFilterTimer.Stop();
                await RefreshTagsAsync();
            };

            _logTimer.Tick += (_, _) =>
            {
                try
                {
                    if (_logDirty)
                        FlushLog();
                }
                catch (Exception)
                {
                    // Oberfläche wird gerade abgebaut
                }
            };
            _logTimer.Start();
            LibraryBrowser.OpenRequested += LibraryBrowser_OpenRequested;
            LibraryBrowser.FavoriteToggled += LibraryBrowser_FavoriteToggled;
            LibraryBrowser.ReadingFilterChanged += LibraryBrowser_ReadingFilterChanged;
            LibraryBrowser.ReadingStatusChanged += LibraryBrowser_ReadingStatusChanged;
            LibraryBrowser.DeleteRequested += LibraryBrowser_DeleteRequested;
            LibraryBrowser.CategoryAssignRequested += LibraryBrowser_CategoryAssignRequested;
            LibraryBrowser.CategoryFilterChanged += LibraryBrowser_CategoryFilterChanged;
            LibraryBrowser.CategoryCreated += LibraryBrowser_CategoryCreated;
            LibraryBrowser.CategoryRenamed += LibraryBrowser_CategoryRenamed;
            LibraryBrowser.CategoryDeleted += LibraryBrowser_CategoryDeleted;
            LibraryBrowser.PageRangeChanged += LibraryBrowser_PageRangeChanged;
            App.Log("MainWindow: Konstruktor fertig.");
        }
        catch (Exception ex)
        {
            App.ReportFatal("Hauptfenster (XAML/InitializeComponent) fehlgeschlagen", ex);
            throw;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Fenster-Lebenszyklus
    // ════════════════════════════════════════════════════════════════════

    // ── Eigene Titelleiste (WindowChrome) ──

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922"; // Wiederherstellen / Maximieren
        MaximizeButton.ToolTip = maximized ? "Verkleinern" : "Maximieren";

        // Maximiert ragt ein Fenster mit WindowChrome um die unsichtbare Größenänderungs-Kante über den Bildschirm hinaus;
        // der Inhalt wird um genau diese Kante eingerückt (gemeinsame Hilfe mit dem Reader).
        RootGrid.Margin = maximized ? ChromeHelper.MaximizedInset(this) : new Thickness(0);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Log("Window_Loaded: öffne Datenbank …");
            _db = new Database();
            App.Log("Datenbank: " + _db.DatabasePath);
            _tags = new TagService(_db);
            _api = new ApiSearchService
            {
                Log = message => Dispatcher.BeginInvoke(() => AppendSessionLog("! " + FlattenLogLine(message))),
                Notice = message => Dispatcher.BeginInvoke(() =>
                {
                    AppendSessionLog("↻  " + FlattenLogLine(message));
                    SetStatus(message, StatusKind.Info);
                    if (_webSearching)
                        SetWebSearchBusy(true, message);
                }),
                CookiesCaptured = cookies =>
                {
                    try { _db.UpsertHttpCookies(cookies); }
                    catch { /* Persistenz ist optional */ }
                    Dispatcher.BeginInvoke(RefreshCookieBox);
                },
            };
            LibraryScanner.CoverCacheDirectory = Path.Combine(Path.GetDirectoryName(_db.DatabasePath) ?? string.Empty, "covers");
            LoadSettings();
            App.Log("Einstellungen geladen.");
        }
        catch (Exception ex)
        {
            App.ReportFatal("Die Datenbank konnte nicht geöffnet werden.\nDatei: " + Database.DefaultPath, ex);
            Close();
            return;
        }

        _ready = true;
        BindWebSources();
        ShowPage(AppPage.Library);
        UpdateHud();

        try
        {
            await RefreshAllAsync();

            if (await Task.Run(() => _db.TakeSourceBackfillFlag()) && await Task.Run(RepairSources) > 0)
                await RefreshAllAsync();

            if (_totalCount == 0)
                await ScanLibraryAsync(automatic: true);

            await CheckGalleryDlAsync(silent: true);
            _ = CheckForUpdatesAsync();
            App.Log("Window_Loaded fertig.");
        }
        catch (Exception ex)
        {
            App.Log("Start nach UI: " + App.Flatten(ex));
            SetStatus($"Start fehlgeschlagen: {ex.Message}", StatusKind.Error);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        var active = _jobs.Count(j => j.Status is JobStatus.Running or JobStatus.Queued);
        if (active > 0)
        {
            var answer = MessageBox.Show(this,
                $"Es {(active == 1 ? "läuft noch 1 Download" : $"laufen noch {active} Downloads")}.\nBeenden und alle Downloads abbrechen?",
                "NekoDex", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        // Beendet laufende gallery-dl-Prozesse samt Kindprozessen.
        foreach (var job in _jobs)
            job.Cts.Cancel();

        try { _webSearchCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        try { _searchTimer.Stop(); } catch { /* Start abgebrochen */ }
        try { _tagFilterTimer.Stop(); } catch { /* Start abgebrochen */ }
        try { _logTimer.Stop(); } catch { /* Start abgebrochen */ }

        foreach (var job in _jobs)
        {
            try { job.Cts.Cancel(); }
            catch (ObjectDisposedException) { }
            try { job.Cts.Dispose(); }
            catch (ObjectDisposedException) { }
        }

        try { _webSearchCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        try { _webSearchCts?.Dispose(); }
        catch (ObjectDisposedException) { }
        try { _webTagDownloadCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        try { _webTagDownloadCts?.Dispose(); }
        catch (ObjectDisposedException) { }

        try { _api?.Dispose(); }
        catch { /* Start abgebrochen */ }

        try { Database.ReleaseConnections(); }
        catch { /* Pool bereits leer */ }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void LoadSettings()
    {
        _settings.Load(_db);
        var library = _settings.LibraryPath;
        _libraryPath = string.IsNullOrWhiteSpace(library) ? DefaultLibraryPath : library;
        _galleryDlPath = _settings.GalleryDlPath;
        _extraArgs = _settings.ExtraArgs;
        _maxParallel = _settings.MaxParallel;
        if (Enum.TryParse<MangaSort>(_settings.Sort, out var sort) && Enum.IsDefined(sort))
            _sort = sort;

        // Gewählte Quellen: fehlt die Einstellung, sind alle angehakt.
        var enabled = string.IsNullOrEmpty(_settings.EnabledSources)
            ? null
            : _settings.EnabledSources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        InitSourceFilters(enabled);
        UpdateSourceHint();

        LibraryPathBox.Text = _libraryPath;
        GalleryDlPathBox.Text = _galleryDlPath;
        HitomiTagBankToggle.IsChecked = _settings.UseGlobalTagFallback;
        UpdateHitomiTagBankStatus();
        ExtraArgsBox.Text = _extraArgs;
        DatabasePathText.Text = $"Datenbank: {_db.DatabasePath}";
        SelectComboItem(ParallelBox, _maxParallel.ToString());
        SelectComboItem(SortBox, _sort.ToString());
        SelectComboItem(PreferredBrowserBox, string.IsNullOrWhiteSpace(_settings.PreferredBrowser) ? "auto" : _settings.PreferredBrowser);
        SelectComboItem(LanguageBox, string.IsNullOrWhiteSpace(_settings.UiLanguage) ? "auto" : _settings.UiLanguage);
        LanguageManager.Instance.Apply(_settings.UiLanguage);
        _languageReady = true;
        BlacklistSettings.Attach(new LibraryRepository(_db));

        _flareSolverrUrl = _settings.FlareSolverrUrl;
        if (FlareSolverrBox is not null)
            FlareSolverrBox.Text = _flareSolverrUrl;
        LoadEhSessionFields();
        try
        {
            FillCookieDomainBox();
            ApplySessionCookiesToApi();
        }
        catch (Exception ex)
        {
            App.Log("Cookie-Einstellungen: " + App.Flatten(ex));
        }
    }

    private static void SelectComboItem(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Navigation
    // ════════════════════════════════════════════════════════════════════

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag })
            return;

        if (Enum.TryParse<AppPage>(tag, out var page))
            ShowPage(page);
    }

    private void ShowPage(AppPage page)
    {
        _page = page;
        var isLibrary = page == AppPage.Library;
        var isWeb = page == AppPage.WebSearch;

        PageLibrary.Visibility = isLibrary ? Visibility.Visible : Visibility.Collapsed;
        PageDownloads.Visibility = page == AppPage.Downloads ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = page == AppPage.Settings ? Visibility.Visible : Visibility.Collapsed;
        WebSearchView.Visibility = isWeb ? Visibility.Visible : Visibility.Collapsed;
        SortPanel.Visibility = isLibrary ? Visibility.Visible : Visibility.Collapsed;
        LibrarySearchPanel.Visibility = isLibrary ? Visibility.Visible : Visibility.Collapsed;
        SourcePopoutButton.Visibility = isLibrary ? Visibility.Visible : Visibility.Collapsed;
        if (!isLibrary)
            SourcePopout.IsOpen = false;
        LibrarySearchPanel.Margin = new Thickness(12, 0, 8, 0);
        SearchBox.Height = isLibrary ? 36 : 46;
        SourcePopoutButton.Height = isLibrary ? 36 : 46;
        if (PageHeader.ColumnDefinitions.Count > 0)
            PageHeader.ColumnDefinitions[0].MinWidth = isLibrary ? 108 : 180;
        CloseSuggestions();
        CloseWebSuggestions();

        PageTitleText.Text = page switch
        {
            AppPage.Downloads => "Downloads",
            AppPage.WebSearch => "Web Search",
            AppPage.Settings => "Einstellungen",
            _ => "Bibliothek",
        };
        BreadcrumbText.Text = "/  " + PageTitleText.Text.ToUpperInvariant();
        PageHeader.Margin = isWeb
            ? new Thickness(16, 0, 16, 2)
            : isLibrary ? new Thickness(16, 4, 16, 2) : new Thickness(28, 10, 28, 10);
        PageTitleText.FontSize = isWeb ? 15 : isLibrary ? 18 : 24;
        PageSubtitleText.FontSize = isWeb ? 11 : 12.5;
        PageSubtitleText.Margin = isWeb ? new Thickness(0) : new Thickness(0, 2, 0, 0);
        if (isWeb)
        {
            if (_webHeaderAutoHide && !_webHeaderHover)
                CollapseWebSearchHeader(false);
            else if (!_webHeaderAutoHide)
                ExpandWebSearchHeader(false);
            ApplyWebHeaderBudget();
        }

        if (isWeb)
        {
            TagList.ItemTemplate = (DataTemplate)FindResource("WebTagCategoryTemplate");
            if (_webSearchModel is not null)
                TagList.ItemsSource = _webSearchModel.TagGroups;
            _ = RefreshWebPopularTagsAsync();
        }
        else
        {
            TagList.ItemTemplate = (DataTemplate)FindResource("TagCategoryTemplate");
            TagList.ItemsSource = _tagGroups;
            NoTagsText.Text = "Noch keine Tags.\nSie erscheinen nach dem ersten Download.";
            NoTagsText.Visibility = _tagGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TagCountText.Text = _tagChips.Count > 0 ? _tagChips.Count.ToString() : string.Empty;
        }

        if (isLibrary && _activeTags.Count > 0)
            ActiveTagBar.Visibility = Visibility.Visible;
        else
            ActiveTagBar.Visibility = Visibility.Collapsed;

        UpdateSubtitle();
    }

    private void GoDownloads_Click(object sender, RoutedEventArgs e)
    {
        NavDownloads.IsChecked = true;
        UrlBox.Focus();
    }

    private void UpdateSubtitle()
    {
        if (!_ready)
            return;

        PageSubtitleText.Text = _page switch
        {
            AppPage.Library => HasActiveSearch
                ? $"{_results.Count} von {_totalCount} Titeln"
                : $"{_totalCount} {(_totalCount == 1 ? "Titel" : "Titel")}",
            AppPage.Downloads => DownloadSummary(),
            AppPage.WebSearch => WebSearchSummary(),
            _ => "gallery-dl, Ordner und Datenbank",
        };
    }

    private string DownloadSummary()
    {
        var running = _jobs.Count(j => j.Status == JobStatus.Running);
        var queued = _jobs.Count(j => j.Status == JobStatus.Queued);
        if (running + queued == 0)
            return "Keine aktiven Downloads";
        return queued > 0 ? $"{running} aktiv · {queued} wartend" : $"{running} aktiv";
    }

    private string WebSearchSummary()
    {
        if (_webSearching)
            return "Suche auf " + SourceCatalog.DisplayName(_webSource) + " …";
        return _webHits.Count == 0
            ? "Eine Website, Tags wählen, online suchen"
            : $"{_webHits.Count} Treffer auf {SourceCatalog.DisplayName(_webSource)}";
    }

    // ════════════════════════════════════════════════════════════════════
    //  Suche: Freitext · Tag-Chips (UND) · Quellen-Filter (ODER) · Vorschläge
    // ════════════════════════════════════════════════════════════════════

    private readonly ObservableCollection<ActiveTag> _activeTags = new();
    private readonly ObservableCollection<SourceFilter> _sourceFilters = new();
    private readonly ObservableCollection<TagSuggestion> _suggestions = new();
    private bool _updatingSearchText;
    private int _suggestVersion;

    /// <summary>Alle Quellen angehakt = kein Filter (dann erscheinen auch Einträge unbekannter Herkunft).</summary>
    private bool SourcesRestricted => _sourceFilters.Any(s => !s.IsSelected);

    /// <summary>Greift irgendein Filter – Text, Tag-Chips oder eingeschränkte Quellen? (für Untertitel und Leer-Hinweise)</summary>
    private bool HasActiveSearch =>
        _activeTags.Count > 0 || SourcesRestricted || SearchSyntax.Tokenize(SearchBox.Text).Any(t => t.Kind != SearchTokenKind.Ignored);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateClearButton();
        if (!_ready || _updatingSearchText)
            return;

        // Fertig getippte Tags („artist:foo “) wandern als Chip unter die Suchleiste, der Rest bleibt Freitext.
        var (tags, remaining) = SearchSyntax.Commit(SearchBox.Text, commitLast: false);
        if (tags.Count > 0)
        {
            foreach (var tag in tags)
                SetActiveTag(tag.Tag, tag.Exclude);
            SetSearchText(remaining);
        }

        SyncTagChips();
        _ = UpdateSuggestionsAsync();

        _searchTimer.Stop();
        _searchTimer.Start();

        // Die Suchleiste gilt global: Wer tippt, landet in der Bibliothek.
        if (_page is AppPage.Downloads or AppPage.Settings)
            NavLibrary.IsChecked = true;
    }

    private void UpdateClearButton() =>
        ClearSearchButton.Visibility = SearchBox.Text.Length > 0 || _activeTags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Setzt den Suchtext, ohne die Eingabe-Logik (Chips übernehmen, Vorschläge) erneut auszulösen.</summary>
    private void SetSearchText(string text)
    {
        _updatingSearchText = true;
        try
        {
            SearchBox.Text = text;
            SearchBox.CaretIndex = text.Length;
        }
        finally
        {
            _updatingSearchText = false;
        }

        UpdateClearButton();
    }

    private async void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SuggestPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down:
                    MoveSuggestion(+1);
                    e.Handled = true;
                    return;
                case Key.Up:
                    MoveSuggestion(-1);
                    e.Handled = true;
                    return;
                case Key.Enter or Key.Tab when SuggestList.SelectedItem is TagSuggestion selected:
                    e.Handled = true;
                    await AcceptSuggestionAsync(selected);
                    return;
                case Key.Tab when _suggestions.Count > 0:
                    e.Handled = true;
                    await AcceptSuggestionAsync(_suggestions[0]);
                    return;
                case Key.Escape:
                    CloseSuggestions();
                    e.Handled = true;
                    return;
            }
        }

        // Rücktaste im leeren Feld nimmt den zuletzt gewählten Tag-Chip zurück.
        if (e.Key == Key.Back && SearchBox.Text.Length == 0 && _activeTags.Count > 0)
        {
            e.Handled = true;
            _activeTags.RemoveAt(_activeTags.Count - 1);
            await RefreshNowAsync();
        }
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (SearchBox.Text.Length > 0 || _activeTags.Count > 0))
        {
            // Erst Esc leert den Text, ein weiteres Esc entfernt auch die Tag-Chips.
            if (SearchBox.Text.Length > 0)
                SearchBox.Clear();
            else
                _activeTags.Clear();

            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CloseSuggestions();

            // Enter übernimmt auch das gerade getippte Tag („artist:foo“ ohne Leerzeichen dahinter).
            var (tags, remaining) = SearchSyntax.Commit(SearchBox.Text, commitLast: true);
            if (tags.Count > 0)
            {
                foreach (var tag in tags)
                    SetActiveTag(tag.Tag, tag.Exclude);
                SetSearchText(remaining);
            }

            await RefreshNowAsync();
        }
    }

    private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CloseSuggestions();

    private async void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        _activeTags.Clear();
        SetSearchText(string.Empty);
        SyncTagChips();
        await RefreshNowAsync();
        SearchBox.Focus();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();

    private async void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SortBox.SelectedItem is not ComboBoxItem { Tag: string tag })
            return;

        if (!Enum.TryParse<MangaSort>(tag, out var sort) || sort == _sort)
            return;

        _sort = sort;
        _settings.Sort = sort.ToString();
        _ = Task.Run(() => _db.SetSetting(SettingSort, _settings.Sort));
        await RefreshLibraryAsync();
    }

    /// <summary>Lädt die Bibliothek sofort neu (ohne die 250-ms-Verzögerung des Tippens).</summary>
    private async Task RefreshNowAsync()
    {
        _searchTimer.Stop();
        await RefreshLibraryAsync();
    }

    private MangaQuery BuildQuery()
    {
        var query = new MangaQuery
        {
            Sort = _sort,
            Sources = SelectedSources(),
            ReadingStatus = _readingFilter,
            Category = _libraryCategory,
            MinPages = _minPages,
            MaxPages = _maxPages,
        };

        // Gewählte Tag-Chips: alle müssen vorkommen (UND); ausgeschlossene dürfen nicht vorkommen.
        foreach (var active in _activeTags)
            (active.Exclude ? query.ExcludeTags : query.IncludeTags).Add(active.Tag);

        // Was noch im Suchfeld steht: Freitext und das Tag, das gerade getippt wird.
        foreach (var token in SearchSyntax.Tokenize(SearchBox.Text))
        {
            switch (token.Kind)
            {
                case SearchTokenKind.Text:
                    query.Terms.Add(token.Value);
                    break;
                case SearchTokenKind.IncludeTag:
                    query.IncludeTags.Add(token.Value);
                    break;
                case SearchTokenKind.ExcludeTag:
                    query.ExcludeTags.Add(token.Value);
                    break;
            }
        }

        return query;
    }

    // ── Aktive Tag-Chips (unter der Suchleiste) ──

    /// <summary>Fügt einen Tag hinzu oder wechselt seine Art (einschließen ⇄ ausschließen).</summary>
    private void SetActiveTag(string tag, bool exclude)
    {
        var existing = _activeTags.FirstOrDefault(a => a.Tag == tag);
        if (existing is null)
            _activeTags.Add(new ActiveTag(tag, exclude));
        else
            existing.Exclude = exclude;
    }

    /// <summary>Linksklick auf einen Tag der Seitenleiste: hinzufügen bzw. – wenn schon gewählt – wieder entfernen.</summary>
    private void ToggleActiveTag(string tag, bool exclude)
    {
        var existing = _activeTags.FirstOrDefault(a => a.Tag == tag);
        if (existing is null)
            _activeTags.Add(new ActiveTag(tag, exclude));
        else if (existing.Exclude == exclude)
            _activeTags.Remove(existing);
        else
            existing.Exclude = exclude;
    }

    private void OnActiveTagsChanged()
    {
        ActiveTagBar.Visibility = _page == AppPage.Library && _activeTags.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateClearButton();
        SyncTagChips();

        if (_ready)
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }
    }

    private async void ActiveTag_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActiveTag active)
        {
            _activeTags.Remove(active);
            await RefreshNowAsync();
        }
    }

    // ── Tags der Seitenleiste ──

    private async void TagChip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TagChip chip)
        {
            if (_page == AppPage.WebSearch)
            {
                AddWebTag(chip.Name);
                SyncWebTagSelection();
                return;
            }

            ToggleActiveTag(chip.Name, exclude: false);
            await RefreshNowAsync();
        }
    }

    private async void TagChip_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is TagChip chip)
        {
            if (_page == AppPage.WebSearch)
                return;

            ToggleActiveTag(chip.Name, exclude: true);
            await RefreshNowAsync();
        }
    }

    /// <summary>Hebt die Tags der Seitenleiste hervor, die gerade gewählt sind (Akzentfarbe = enthalten, rot = ausgeschlossen).</summary>
    private void SyncTagChips()
    {
        var included = _activeTags.Where(a => !a.Exclude).Select(a => a.Tag).ToHashSet(StringComparer.Ordinal);
        var excluded = _activeTags.Where(a => a.Exclude).Select(a => a.Tag).ToHashSet(StringComparer.Ordinal);

        // Tags, die noch im Suchfeld stehen (gerade getippt), zählen ebenfalls.
        foreach (var token in SearchSyntax.Tokenize(SearchBox.Text))
        {
            if (token.Kind == SearchTokenKind.IncludeTag)
                included.Add(token.Value);
            else if (token.Kind == SearchTokenKind.ExcludeTag)
                excluded.Add(token.Value);
        }

        foreach (var chip in _tagChips)
        {
            chip.IsIncluded = included.Contains(chip.Name);
            chip.IsExcluded = excluded.Contains(chip.Name);
        }
    }

    // ── Vorschlagsliste beim Tippen ──

    /// <summary>Zeigt Tags aus der Datenbank, die zum gerade getippten Wort passen (Namensteil, „artist:ab…“, „-tag:…“).</summary>
    private async Task UpdateSuggestionsAsync()
    {
        var version = ++_suggestVersion;
        try
        {
            if (SearchSyntax.CurrentWord(SearchBox.Text) is not { } word || (word.Query.Length < 2 && !word.Query.Contains(':')))
            {
                CloseSuggestions();
                return;
            }

            var skip = _activeTags.Select(a => a.Tag).ToList();
            var found = await Task.Run(() => _db.SuggestTags(word.Query, 8, skip));
            if (version != _suggestVersion)
                return; // inzwischen wurde weitergetippt

            _suggestions.Clear();
            foreach (var tag in found)
                _suggestions.Add(new TagSuggestion(tag, word.Exclude, word.Raw));

            if (_suggestions.Count == 0 || !SearchBox.IsKeyboardFocusWithin)
            {
                CloseSuggestions();
                return;
            }

            SuggestList.SelectedIndex = -1;
            SuggestPopup.IsOpen = true;
        }
        catch (Exception)
        {
            CloseSuggestions(); // Vorschläge sind ein Komfort – Fehler dürfen das Tippen nie stören
        }
    }

    private void CloseSuggestions()
    {
        _suggestVersion++;
        SuggestPopup.IsOpen = false;
        SuggestList.SelectedIndex = -1;
    }

    private void MoveSuggestion(int delta)
    {
        if (_suggestions.Count == 0)
            return;

        var index = SuggestList.SelectedIndex + delta;
        if (index < 0)
            index = _suggestions.Count - 1;
        else if (index >= _suggestions.Count)
            index = 0;

        SuggestList.SelectedIndex = index;
        SuggestList.ScrollIntoView(SuggestList.SelectedItem);
    }

    private async Task AcceptSuggestionAsync(TagSuggestion suggestion)
    {
        var rest = SearchSyntax.RemoveLastWord(SearchBox.Text, suggestion.Raw);
        CloseSuggestions();
        SetActiveTag(suggestion.Tag, suggestion.Exclude);
        SetSearchText(rest);
        SearchBox.Focus();
        await RefreshNowAsync();
    }

    private async void SuggestList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(SuggestList, source) is ListBoxItem { DataContext: TagSuggestion suggestion })
        {
            e.Handled = true;
            await AcceptSuggestionAsync(suggestion);
        }
    }

    // ── Quellen-Filter (Leiste über dem Raster) ──

    /// <summary>
    /// Die gewählten Quellen für die Datenbank-Abfrage (ODER). <c>null</c>, wenn alle angehakt sind – dann wird nicht gefiltert,
    /// und es erscheinen auch Einträge, die zu keiner der Seiten gehören.
    /// </summary>
    private List<string>? SelectedSources() =>
        SourcesRestricted ? _sourceFilters.Where(s => s.IsSelected).Select(s => s.Id).ToList() : null;

    private bool IsSourceEnabled(string source) =>
        _sourceFilters.FirstOrDefault(s => s.Id == source) is not { } filter || filter.IsSelected;

    private void InitSourceFilters(ISet<string>? enabled)
    {
        foreach (var filter in _sourceFilters)
            filter.SelectionChanged -= OnSourceSelectionChanged;
        _sourceFilters.Clear();

        void Add(string id, string name)
        {
            var filter = new SourceFilter(id, name);
            filter.SetSelectedSilently(enabled is null || enabled.Contains(id));
            filter.SelectionChanged += OnSourceSelectionChanged;
            _sourceFilters.Add(filter);
        }

        foreach (var site in SourceCatalog.Sites)
            Add(site.Id, site.DisplayName);
        Add(SourceCatalog.Other, "Andere");
    }

    private async void OnSourceSelectionChanged(SourceFilter changed)
    {
        await ApplySourceChangeAsync();
    }

    private async Task ApplySourceChangeAsync()
    {
        var value = SourcesRestricted ? string.Join(",", _sourceFilters.Where(s => s.IsSelected).Select(s => s.Id)) : null;
        _settings.EnabledSources = value ?? string.Empty;
        _ = Task.Run(() => _db.SetSetting(SettingSources, value));

        UpdateSourceHint();
        UpdateSubtitle();
        await RefreshNowAsync();
    }

    private async void SourceAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var filter in _sourceFilters)
            filter.SetSelectedSilently(true);
        await ApplySourceChangeAsync();
    }

    /// <summary>Rechtsklick auf eine Quelle: nur diese anzeigen.</summary>
    private async void SourceCheck_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not SourceFilter only)
            return;

        foreach (var filter in _sourceFilters)
            filter.SetSelectedSilently(filter == only);
        await ApplySourceChangeAsync();
    }

    // ════════════════════════════════════════════════════════════════════
    //  Bibliothek laden · Kachel-Raster
    // ════════════════════════════════════════════════════════════════════

    private async Task RefreshAllAsync()
    {
        await RefreshLibraryAsync();
        await RefreshTagsAsync();
        await RefreshStatsAsync();
    }

    private async Task RefreshLibraryAsync()
    {
        if (!_ready)
            return;

        var query = BuildQuery();
        var version = ++_searchVersion;

        try
        {
            List<Manga> results = new();
            List<string> categories = new();
            await Task.Run(() =>
            {
                results = _db.SearchMangas(query);
                categories = _db.ListCategories().ToList();
            });
            if (version != _searchVersion)
                return; // inzwischen wurde eine neuere Suche gestartet

            _results = results;
            _tiles.Clear();
            _loadedCount = 0;
            LoadNextTilePage();
            LibraryBrowser.Show(_results.Select(LocalManga.From));
            LibraryBrowser.SetCategories(categories, _libraryCategory);
            UpdateEmptyState();
            UpdateSubtitle();
        }
        catch (Exception ex)
        {
            if (version == _searchVersion)
                SetStatus($"Bibliothek konnte nicht geladen werden: {ex.Message}", StatusKind.Error);
        }
    }

    private void LoadNextTilePage()
    {
        var end = Math.Min(_loadedCount + TilePageSize, _results.Count);
        for (var i = _loadedCount; i < end; i++)
        {
            var tile = new MangaTile(_results[i]);
            tile.FavoriteChanged += OnTileFavoriteChanged;
            _tiles.Add(tile);
        }

        _loadedCount = end;
    }

    /// <summary>Endloses Scrollen: Kurz vor dem Ende werden die nächsten Kacheln angehängt.</summary>
    private void TileGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_loadedCount >= _results.Count || e.OriginalSource is not ScrollViewer viewer)
            return;

        var remaining = viewer.ExtentHeight - (viewer.VerticalOffset + viewer.ViewportHeight);
        if (remaining < 700)
            LoadNextTilePage();
    }

    private void TagFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
            return;

        _tagFilterTimer.Stop();
        _tagFilterTimer.Start();
    }

    private async Task RefreshTagsAsync()
    {
        var filter = TagFilterBox.Text;

        try
        {
            var tags = await Task.Run(() => _db.GetTags(MaxTagChips, filter));
            if (!string.Equals(filter, TagFilterBox.Text, StringComparison.Ordinal))
                return; // der Filter wurde inzwischen weiter getippt – das neuere Ergebnis kommt gleich

            _tagChips.Clear();
            _tagGroups.Clear();
            var chips = tags.Select(tag => new TagChip(tag.Name, tag.MangaCount)).ToList();
            foreach (var chip in chips)
                _tagChips.Add(chip);
            foreach (var group in TagGroup.FromChips(chips, expandAll: filter.Trim().Length > 0))
                _tagGroups.Add(group);

            // Welche Kategorien (artist, parody, …) es gibt, braucht die Such-Eingabe, um „präfix:wort“ als Tag zu erkennen.
            TagNames.RegisterCategories(await Task.Run(() => _db.GetCategories()));

            NoTagsText.Text = filter.Trim().Length > 0
                ? "Keine passenden Tags."
                : "Noch keine Tags.\nSie erscheinen nach dem ersten Download.";
            NoTagsText.Visibility = _tagChips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SyncTagChips();
        }
        catch (Exception ex)
        {
            SetStatus($"Tags konnten nicht geladen werden: {ex.Message}", StatusKind.Error);
        }
    }

    private async Task RefreshStatsAsync()
    {
        try
        {
            var (total, tags, sourceCounts) = await Task.Run(() =>
                (_db.CountMangas(), _db.CountTags(), _db.GetSourceCounts()));

            foreach (var filter in _sourceFilters)
                filter.Count = sourceCounts.GetValueOrDefault(filter.Id);

            _totalCount = total;
            NavLibraryCount.Text = total > 0 ? total.ToString() : string.Empty;
            TagCountText.Text = tags > 0 ? tags.ToString() : string.Empty;
            FooterStats.Text = $"{total} TITEL · {tags} TAGS";
            UpdateSubtitle();
        }
        catch (Exception ex)
        {
            SetStatus($"Statistik konnte nicht geladen werden: {ex.Message}", StatusKind.Error);
        }
    }

    private void UpdateEmptyState()
    {
        var empty = _tiles.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty)
            return;

        EmptyActionButton.Visibility = Visibility.Collapsed;
        EmptyScanButton.Visibility = Visibility.Collapsed;

        if (_sourceFilters.Count > 0 && _sourceFilters.All(s => !s.IsSelected))
        {
            EmptyIcon.Text = "\uE71C";
            EmptyTitleText.Text = "Keine Quelle ausgewählt";
            EmptyHintText.Text = "Öffne „Quellen“ neben der Suchleiste und hake mindestens eine Seite an – oder wähle „Alle“.";
        }
        else if (HasActiveSearch)
        {
            EmptyIcon.Text = "\uE721";
            EmptyTitleText.Text = "Keine Treffer";
            EmptyHintText.Text = "Es müssen alle gewählten Tags zutreffen. Entferne einen Tag-Chip unter der Suchleiste oder lockere die Quellen-Auswahl.";
        }
        else
        {
            EmptyIcon.Text = "\uE8F1";
            EmptyTitleText.Text = "Deine Bibliothek ist leer";
            EmptyHintText.Text = "Füge unter „Downloads“ eine URL hinzu – oder lies bereits vorhandene Galerien aus dem Bibliotheksordner ein.";
            EmptyActionButton.Visibility = Visibility.Visible;
            EmptyScanButton.Visibility = Visibility.Visible;
        }
    }

    // ── Kacheln: Favorit, Doppelklick, Kontextmenü ──

    private static MangaTile? TileFromSender(object sender)
    {
        if (sender is not FrameworkElement element)
            return null;

        if (element.DataContext is MangaTile tile)
            return tile;

        // MenuItem im ContextMenu: die Kachel hängt am Element, auf dem das Menü geöffnet wurde.
        if (element is MenuItem item
            && ItemsControl.ItemsControlFromItemContainer(item) is ContextMenu { PlacementTarget: FrameworkElement target }
            && target.DataContext is MangaTile fromMenu)
            return fromMenu;

        return null;
    }

    private void MenuFavorite_Click(object sender, RoutedEventArgs e)
    {
        // Das Umschalten genügt: Die Kachel meldet die Änderung (FavoriteChanged) und löst das Speichern aus.
        if (TileFromSender(sender) is { } tile)
            tile.IsFavorite = !tile.IsFavorite;
    }

    /// <summary>
    /// Wird bei jeder Änderung des Favoriten ausgelöst – egal ob per Stern, Kontextmenü oder Bedienungshilfe.
    /// Das Speichern hängt bewusst am Modell und nicht an einem Mausklick-Ereignis.
    /// </summary>
    private async void OnTileFavoriteChanged(MangaTile tile)
    {
        var value = tile.IsFavorite;
        try
        {
            await Task.Run(() => _db.SetFavorite(tile.Id, value));
            await RefreshStatsAsync();
        }
        catch (Exception ex)
        {
            tile.SetFavoriteSilently(!value); // zurücksetzen, ohne erneut zu speichern
            SetStatus($"Favorit konnte nicht gespeichert werden: {ex.Message}", StatusKind.Error);
        }
    }

    // ── Lesen: lokal im Reader oder online per gallery-dl -g ──

    private async void Tile_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TileFromSender(sender) is { } tile)
            await ReadTileAsync(tile);
    }

    /// <summary>Enter öffnet die gewählte Kachel im Reader – auch ohne Maus bedienbar.</summary>
    private async void TileGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && TileGrid.SelectedItem is MangaTile tile)
        {
            e.Handled = true;
            await ReadTileAsync(tile);
        }
    }

    private async void MenuRead_Click(object sender, RoutedEventArgs e)
    {
        if (TileFromSender(sender) is { } tile)
            await ReadTileAsync(tile);
    }

    private async void MenuReadOnline_Click(object sender, RoutedEventArgs e)
    {
        if (TileFromSender(sender) is not { } tile)
            return;

        if (string.IsNullOrWhiteSpace(tile.Manga.SourceUrl))
        {
            SetStatus("Für diesen Eintrag ist keine Quell-URL gespeichert – online lesen ist nicht möglich.", StatusKind.Warning);
            return;
        }

        await ReadOnlineAsync(tile.Manga.SourceUrl, tile.Title);
    }

    /// <summary>Doppelklick bzw. „Lesen“: zuerst aus den lokalen Dateien; fehlen sie, online über die gespeicherte Adresse.</summary>
    private async Task ReadTileAsync(MangaTile tile)
    {
        if (ReaderWindow.OpenLocal(this, tile.Manga, tile.Manga.LastReadPageIndex) is { } reader)
        {
            TrackReading(reader, tile.Id, tile.Manga, card: null);
            return;
        }

        if (!string.IsNullOrWhiteSpace(tile.Manga.SourceUrl))
        {
            SetStatus("Lokale Dateien nicht gefunden – lese online …");
            await ReadOnlineAsync(tile.Manga.SourceUrl, tile.Title);
        }
        else
        {
            SetStatus("Die Dateien dieses Eintrags wurden nicht gefunden (Ordner verschoben oder gelöscht?).", StatusKind.Warning);
        }
    }

    private void TrackReading(ReaderWindow reader, long id, Manga manga, LocalManga? card)
    {
        var status = manga.ReadingStatus;
        reader.PageCommitted += page =>
        {
            if (card is not null)
                card.LastReadPageIndex = page;
            RememberProgress(id, page, manga);
            var next = status;
            if (reader.ListingComplete && reader.PageCount > 0 && page >= reader.PageCount - 1)
                next = ReadingStatus.Completed;
            else if (status == ReadingStatus.Unread)
                next = ReadingStatus.Reading;
            if (next == status)
                return;
            status = next;
            manga.ReadingStatus = next;
            PersistReadingStatus(id, next);
        };
    }

    private void PersistReadingStatus(long id, ReadingStatus status)
    {
        try
        {
            new LibraryRepository(_db).SetReadingStatus(id, status);
            LibraryBrowser.UpdateStatus(id, status);
        }
        catch (Exception ex)
        {
            App.Log("Lesestatus: " + ex.Message);
        }
    }

    private void RememberProgress(long id, int page, Manga manga)
    {
        manga.LastReadPageIndex = page;
        try
        {
            new LibraryRepository(_db).SetLastReadPage(id, page);
        }
        catch (Exception ex)
        {
            App.Log("Lesefortschritt: " + ex.Message);
        }
    }

    private void LibraryBrowser_ReadingFilterChanged(object? sender, ReadingStatus? status)
    {
        if (!_ready || _readingFilter == status)
            return;
        _readingFilter = status;
        _ = RefreshLibraryAsync();
    }

    private void LibraryBrowser_CategoryFilterChanged(object? sender, string? category)
    {
        if (!_ready || string.Equals(_libraryCategory, category, StringComparison.Ordinal))
            return;
        _libraryCategory = string.IsNullOrWhiteSpace(category) ? null : category;
        _ = RefreshLibraryAsync();
    }

    private void LibraryBrowser_PageRangeChanged(object? sender, LibraryPageRange range)
    {
        if (_minPages == range.Min && _maxPages == range.Max)
            return;
        _minPages = range.Min;
        _maxPages = range.Max;
        if (_ready)
            _ = RefreshLibraryAsync();
    }

    private async void LibraryBrowser_CategoryCreated(object? sender, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        try
        {
            await Task.Run(() => new LibraryRepository(_db).CreateCategory(name));
            await RefreshLibraryAsync();
            SetStatus($"Kategorie „{name}“ wurde angelegt.", StatusKind.Info);
        }
        catch (Exception ex)
        {
            SetStatus($"Kategorie konnte nicht angelegt werden: {ex.Message}", StatusKind.Error);
        }
    }

    private async void LibraryBrowser_CategoryRenamed(object? sender, LibraryCategoryRename rename)
    {
        try
        {
            await Task.Run(() => new LibraryRepository(_db).RenameCategory(rename.OldName, rename.NewName));
            if (string.Equals(_libraryCategory, rename.OldName, StringComparison.OrdinalIgnoreCase))
                _libraryCategory = rename.NewName;
            await RefreshLibraryAsync();
            SetStatus($"Kategorie „{rename.NewName}“ wurde gespeichert.", StatusKind.Info);
        }
        catch (Exception ex)
        {
            SetStatus($"Kategorie konnte nicht umbenannt werden: {ex.Message}", StatusKind.Error);
        }
    }

    private async void LibraryBrowser_CategoryDeleted(object? sender, string name)
    {
        try
        {
            await Task.Run(() => new LibraryRepository(_db).DeleteCategory(name));
            if (string.Equals(_libraryCategory, name, StringComparison.OrdinalIgnoreCase))
                _libraryCategory = null;
            await RefreshLibraryAsync();
            SetStatus($"Kategorie „{name}“ wurde gelöscht.", StatusKind.Info);
        }
        catch (Exception ex)
        {
            SetStatus($"Kategorie konnte nicht gelöscht werden: {ex.Message}", StatusKind.Error);
        }
    }

    private void SourcePopout_Click(object sender, RoutedEventArgs e) =>
        SourcePopout.IsOpen = !SourcePopout.IsOpen;

    private void UpdateSourceHint()
    {
        SourcePopoutHint.Visibility = SourcesRestricted ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void LibraryBrowser_DeleteRequested(object? sender, LibraryDeleteRequest request)
    {
        if (request.Items.Count == 0)
            return;

        try
        {
            var warnings = new List<string>();
            await Task.Run(() =>
            {
                var repository = new LibraryRepository(_db);
                foreach (var item in request.Items)
                {
                    var warning = repository.Delete(item, request.DeleteFiles, _libraryPath);
                    if (!string.IsNullOrWhiteSpace(warning))
                        warnings.Add(warning);
                }
            });
            await RefreshAllAsync();
            var text = request.Items.Count == 1
                ? $"„{request.Items[0].Title}“ wurde gelöscht."
                : $"{request.Items.Count} Titel wurden gelöscht.";
            if (warnings.Count > 0)
                text += " " + warnings[0];
            SetStatus(text, warnings.Count > 0 ? StatusKind.Warning : StatusKind.Info);
        }
        catch (Exception ex)
        {
            SetStatus($"Löschen fehlgeschlagen: {ex.Message}", StatusKind.Error);
        }
    }

    private async void LibraryBrowser_CategoryAssignRequested(object? sender, LibraryCategoryRequest request)
    {
        if (request.Ids.Count == 0 || string.IsNullOrWhiteSpace(request.Name))
            return;

        try
        {
            await Task.Run(() => new LibraryRepository(_db).AssignCategory(request.Ids, request.Name));
            await RefreshLibraryAsync();
            SetStatus($"Kategorie „{request.Name}“ wurde zugewiesen.", StatusKind.Info);
        }
        catch (Exception ex)
        {
            SetStatus($"Kategorie konnte nicht gespeichert werden: {ex.Message}", StatusKind.Error);
        }
    }

    private void LibraryBrowser_ReadingStatusChanged(object? sender, LocalManga item)
    {
        var tile = _tiles.FirstOrDefault(entry => entry.Id == item.Id);
        if (tile is not null)
            tile.Manga.ReadingStatus = item.ReadingStatus;
        PersistReadingStatus(item.Id, item.ReadingStatus);
    }

    private async void LibraryBrowser_OpenRequested(object? sender, LocalManga item)
    {
        var manga = item.ToManga();
        if (ReaderWindow.OpenLocal(this, manga, item.LastReadPageIndex) is { } reader)
        {
            TrackReading(reader, item.Id, manga, item);
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.SourceUrl))
        {
            SetStatus("Lokale Dateien nicht gefunden – lese online …");
            await ReadOnlineAsync(item.SourceUrl, item.Title);
        }
        else
        {
            SetStatus("Die Dateien dieses Eintrags wurden nicht gefunden (Ordner verschoben oder gelöscht?).", StatusKind.Warning);
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var (command, _) = await GalleryDl.ResolveAsync(_galleryDlPath);
            var notices = await UpdateService.CheckAsync(command?.Description);
            if (notices.Count == 0 || Dispatcher.HasShutdownStarted)
                return;
            await Dispatcher.InvokeAsync(() =>
            {
                _updates = notices;
                UpdateButton.Content = string.Join(" · ", notices.Select(notice => notice.Product + " " + notice.LatestVersion));
                UpdateButton.Visibility = Visibility.Visible;
            });
        }
        catch (Exception ex)
        {
            App.Log("Update-Prüfung: " + ex.Message);
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var notice in _updates)
        {
            if (notice.IsGalleryDl)
            {
                SetStatus("gallery-dl wird aktualisiert …", StatusKind.Info);
                try
                {
                    var path = await GalleryDlService.DownloadAsync();
                    SetStatus(path is null
                        ? "gallery-dl konnte nicht geladen werden."
                        : "gallery-dl gespeichert: " + path,
                        path is null ? StatusKind.Warning : StatusKind.Success);
                }
                catch (Exception ex)
                {
                    SetStatus("gallery-dl-Update fehlgeschlagen: " + ex.Message, StatusKind.Error);
                }
            }
            else if (Uri.TryCreate(notice.Url, UriKind.Absolute, out var uri))
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
        }
    }

    private async void LibraryBrowser_FavoriteToggled(object? sender, LocalManga item)
    {
        var value = !item.IsFavorite;
        item.IsFavorite = value;
        try
        {
            await Task.Run(() => new LibraryRepository(_db).SetFavorite(item.Id, value));
            _tiles.FirstOrDefault(tile => tile.Id == item.Id)?.SetFavoriteSilently(value);
            await RefreshStatsAsync();
        }
        catch (Exception ex)
        {
            item.IsFavorite = !value;
            SetStatus($"Favorit konnte nicht gespeichert werden: {ex.Message}", StatusKind.Error);
        }
    }

    /// <summary>„Direkt online lesen“ auf der Downloads-Seite: die erste Adresse aus dem Eingabefeld, ohne sie herunterzuladen.</summary>
    private async void ReadOnline_Click(object sender, RoutedEventArgs e)
    {
        var (urls, invalid) = ParseUrls(UrlBox.Text);
        if (urls.Count == 0)
        {
            var message = invalid > 0
                ? "Keine gültige URL gefunden – sie muss mit http:// oder https:// beginnen."
                : "Bitte zuerst die URL einer Galerie einfügen.";
            LogOnlineRead("Direkt online lesen", "! " + message);
            SetStatus(message, StatusKind.Warning);
            UrlBox.Focus();
            return;
        }

        var title = TitleBox.Text.Trim();
        await ReadOnlineAsync(urls[0], title.Length > 0 ? title : urls[0], logToDownloads: true);
    }

    /// <summary>Öffnet den Reader für eine Galerie-URL. gallery-dl läuft dabei nur für angehakte Quellen.</summary>
    /// <param name="logToDownloads">
    /// <c>true</c> (Button auf der Downloads-Seite): Ablauf und Fehler ins Log-Fenster schreiben.
    /// <c>false</c> (Kontextmenü der Kachel): nur Statuszeile, das Log bleibt beim markierten Download.
    /// </param>
    private async Task ReadOnlineAsync(string url, string title, bool logToDownloads = false, bool skipSourceFilter = false)
    {
        void Fail(string message, StatusKind kind)
        {
            if (logToDownloads)
                LogOnlineRead(title, "$ " + url, "! " + message);
            SetStatus(message, kind);
        }

        var source = SourceCatalog.FromUrl(url);
        if (!skipSourceFilter && !IsSourceEnabled(source))
        {
            Fail($"Online lesen nicht gestartet – Quelle „{SourceCatalog.DisplayName(source)}“ ist in der Quellen-Leiste abgewählt.", StatusKind.Warning);
            return;
        }

        SetStatus("Suche gallery-dl …");
        var (command, problem) = await ResolveGalleryDlAsync();
        if (command is null)
        {
            Fail("gallery-dl wurde nicht gefunden. " + (problem ?? string.Empty).Trim(), StatusKind.Error);
            return;
        }

        try
        {
            // Online-Modus: Der Reader fragt per „gallery-dl -g“ die Bild-URLs ab und streamt die Seiten direkt in den Speicher.
            var reader = new ReaderWindow(url, isOnlineMode: true)
            {
                Owner = this,
                DisplayTitle = title,
                ResolvedCommand = command, // schon gefunden – der Reader muss nicht noch einmal suchen
                ExtraArguments = JoinArguments(CookieArgumentsFor(url), _extraArgs),
                WorkingDirectory = Directory.Exists(_libraryPath) ? _libraryPath : null,
            };

            if (logToDownloads)
            {
                LogOnlineRead(title, "$ " + url, "gallery-dl -g  ·  " + command.Description);
                reader.Failed += message =>
                {
                    if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                        return;
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        AppendSessionLog("! " + FlattenLogLine(message));
                        SetStatus(Truncate(message, 120), StatusKind.Error);
                    });
                };
            }

            reader.Show();
            SetStatus($"Reader geöffnet: {Truncate(title, 70)}");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or IOException)
        {
            Fail(ex.Message, StatusKind.Error);
        }
        catch (Exception ex)
        {
            Fail("Reader konnte nicht geöffnet werden: " + ex.Message, StatusKind.Error);
        }
    }

    private async void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (TileFromSender(sender) is { } tile)
            await OpenEntryAsync(tile, reveal: false);
    }

    private async void MenuShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (TileFromSender(sender) is { } tile)
            await OpenEntryAsync(tile, reveal: true);
    }

    /// <summary>
    /// Öffnet den Eintrag: Ordner im Explorer, Archive (.cbz/.zip) im Standardprogramm.
    /// Mit <paramref name="reveal"/> wird stattdessen der Ort im Explorer gezeigt (Archive markiert).
    /// </summary>
    private async Task OpenEntryAsync(MangaTile tile, bool reveal)
    {
        var path = tile.Manga.FolderPath;
        var isFile = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        var isFolder = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        if (!isFile && !isFolder)
        {
            SetStatus("Der Ordner bzw. das Archiv dieses Eintrags existiert nicht mehr.", StatusKind.Warning);
            return;
        }

        if (isFile && reveal)
            RevealInExplorer(path!);
        else
            OpenWithShell(path!);

        try
        {
            await Task.Run(() => _db.TouchLastOpened(tile.Id));
        }
        catch
        {
            // "Zuletzt geöffnet" ist nicht kritisch
        }
    }

    private void MenuOpenSource_Click(object sender, RoutedEventArgs e)
    {
        if (TileFromSender(sender) is not { } tile)
            return;

        var url = tile.Manga.SourceUrl;
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            OpenWithShell(uri.AbsoluteUri);
        else
            SetStatus("Für diesen Eintrag ist keine Quell-URL gespeichert.", StatusKind.Warning);
    }

    private async void MenuRemove_Click(object sender, RoutedEventArgs e)
    {
        if (TileFromSender(sender) is not { } tile)
            return;

        var answer = MessageBox.Show(this,
            $"„{tile.Title}“ aus der Bibliothek entfernen?\n\nDie heruntergeladenen Dateien bleiben auf der Festplatte erhalten.",
            "Eintrag entfernen", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            await Task.Run(() => _db.DeleteManga(tile.Id));
            await RefreshAllAsync();
            SetStatus($"„{tile.Title}“ wurde aus der Bibliothek entfernt.", StatusKind.Info);
        }
        catch (Exception ex)
        {
            SetStatus($"Entfernen fehlgeschlagen: {ex.Message}", StatusKind.Error);
        }
    }

    // ── Bearbeiten (Overlay) ──

    private void MenuEdit_Click(object sender, RoutedEventArgs e)
    {
        if (TileFromSender(sender) is not { } tile)
            return;

        _editingTile = tile;
        EditTitleBox.Text = tile.Manga.Title;
        EditAuthorBox.Text = tile.Manga.Author ?? string.Empty;
        EditTagsBox.Text = string.Join(", ", tile.Manga.Tags);
        EditFolderText.Text = tile.Manga.FolderPath ?? string.Empty;
        EditOverlay.Visibility = Visibility.Visible;
        EditTitleBox.Focus();
        EditTitleBox.SelectAll();
    }

    private void EditCancel_Click(object sender, RoutedEventArgs e) => CloseEditOverlay();

    private void EditOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseEditOverlay();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            EditSave_Click(sender, e);
        }
    }

    private void CloseEditOverlay()
    {
        EditOverlay.Visibility = Visibility.Collapsed;
        _editingTile = null;
    }

    private async void EditSave_Click(object sender, RoutedEventArgs e)
    {
        if (_editingTile is not { } tile)
            return;

        var title = EditTitleBox.Text.Trim();
        if (title.Length == 0)
        {
            EditTitleBox.Focus();
            return;
        }

        var source = tile.Manga;
        var updated = new Manga
        {
            Id = source.Id,
            Title = title,
            Author = EditAuthorBox.Text.Trim(),
            Description = source.Description,
            SourceUrl = source.SourceUrl,
            FolderPath = source.FolderPath,
            CoverPath = source.CoverPath,
            PageCount = source.PageCount,
            IsFavorite = source.IsFavorite,
            DateAdded = source.DateAdded,
            LastOpened = source.LastOpened,
            Tags = SearchSyntax.SplitList(EditTagsBox.Text),
        };

        try
        {
            await Task.Run(() => _db.UpdateManga(updated));
            CloseEditOverlay();
            await RefreshAllAsync();
            SetStatus($"„{title}“ wurde gespeichert.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"Speichern fehlgeschlagen: {ex.Message}", StatusKind.Error);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  gallery-dl: Programm finden · Warteschlange · Prozesssteuerung
    // ════════════════════════════════════════════════════════════════════

    private void UrlBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            StartDownloads();
            e.Handled = true;
        }
    }

    private void PasteUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                SetStatus("Die Zwischenablage enthält keinen Text.", StatusKind.Warning);
                return;
            }

            var text = Clipboard.GetText().Trim();
            UrlBox.Text = UrlBox.Text.Length == 0 ? text : UrlBox.Text.TrimEnd() + Environment.NewLine + text;
            UrlBox.CaretIndex = UrlBox.Text.Length;
            UrlBox.Focus();
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            SetStatus("Zugriff auf die Zwischenablage nicht möglich – bitte erneut versuchen.", StatusKind.Warning);
        }
    }

    private void StartDownload_Click(object sender, RoutedEventArgs e) => StartDownloads();

    private void StartDownloads()
    {
        var (urls, invalid) = ParseUrls(UrlBox.Text);
        if (urls.Count == 0)
        {
            SetStatus(
                invalid > 0
                    ? "Keine gültige URL gefunden – sie muss mit http:// oder https:// beginnen."
                    : "Bitte zuerst eine URL einfügen.",
                StatusKind.Warning);
            UrlBox.Focus();
            return;
        }

        // gallery-dl läuft nur für die in der Quellen-Leiste gewählten Seiten; gesperrte Adressen bleiben im Feld stehen.
        var blocked = urls.Where(u => !IsSourceEnabled(SourceCatalog.FromUrl(u))).ToList();
        urls = urls.Except(blocked).ToList();
        if (urls.Count == 0)
        {
            SetStatus(BlockedMessage(blocked), StatusKind.Warning);
            return;
        }

        var tags = SearchSyntax.SplitList(TagsBox.Text);
        var title = urls.Count == 1 ? TitleBox.Text : null; // ein Titel ergibt nur für genau eine URL Sinn

        // IDs steigen in Eingabe-Reihenfolge (bestimmt die Startreihenfolge); die Liste zeigt die Eingabe oben.
        DownloadJob? first = null;
        for (var i = 0; i < urls.Count; i++)
        {
            var job = new DownloadJob(urls[i], title, tags);
            _jobs.Insert(i, job);
            first ??= job;
        }

        UrlBox.Text = string.Join(Environment.NewLine, blocked);
        TitleBox.Clear();
        TagsBox.Clear();
        JobList.SelectedItem = first;

        var message = urls.Count == 1
            ? LanguageManager.Instance.Get("Download_Enqueued")
            : LanguageManager.Instance.Get("Download_EnqueuedMany", urls.Count);
        if (invalid > 0)
            message += $" {invalid} ungültige Einträge ignoriert.";
        if (blocked.Count > 0)
            message += " " + BlockedMessage(blocked);
        SetStatus(message, invalid > 0 || blocked.Count > 0 ? StatusKind.Warning : StatusKind.Info);

        PumpQueue();
    }

    private DownloadJob QueueGalleryDownload(string url, string? title, IReadOnlyList<string> tags, bool skipSourceFilter)
    {
        var job = new DownloadJob(url, title, tags) { SkipSourceFilter = skipSourceFilter };
        _jobs.Insert(0, job);
        JobList.SelectedItem = job;
        PumpQueue();
        return job;
    }

    private static string BlockedMessage(IReadOnlyCollection<string> urls)
    {
        var names = urls.Select(u => SourceCatalog.DisplayName(SourceCatalog.FromUrl(u))).Distinct().ToList();
        return $"{urls.Count} {(urls.Count == 1 ? "Adresse" : "Adressen")} nicht gestartet – Quelle „{string.Join("“, „", names)}“ ist in der Quellen-Leiste abgewählt.";
    }

    private static (List<string> Urls, int Invalid) ParseUrls(string? text)
    {
        var urls = new List<string>();
        var invalid = 0;

        foreach (var raw in (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = raw.Trim().Trim('"', '\'', '<', '>', ',', ';');
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                if (!urls.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    urls.Add(candidate);
            }
            else
            {
                invalid++;
            }
        }

        return (urls, invalid);
    }

    /// <summary>Startet wartende Jobs, solange noch Plätze frei sind (Einstellung „Gleichzeitige Downloads“).</summary>
    private void PumpQueue()
    {
        if (!_ready)
            return;

        while (_jobs.Count(j => j.Status == JobStatus.Running) < _maxParallel)
        {
            var next = _jobs.Where(j => j.Status == JobStatus.Queued).OrderBy(j => j.Id).FirstOrDefault();
            if (next is null)
                break;

            // Wurde die Quelle nach dem Einreihen abgewählt, läuft gallery-dl dafür nicht mehr.
            if (!next.SkipSourceFilter && !IsSourceEnabled(next.Source))
            {
                next.Message = $"Nicht gestartet – Quelle „{SourceCatalog.DisplayName(next.Source)}“ ist abgewählt";
                next.Status = JobStatus.Cancelled;
                continue;
            }

            next.Status = JobStatus.Running; // sofort markieren, damit der Job nicht doppelt gestartet wird
            _ = RunJobAsync(next);
        }

        UpdateHud();
    }

    private async Task RunJobAsync(DownloadJob job)
    {
        var exitCode = -1;
        string? failure = null;

        try
        {
            job.Message = "Suche gallery-dl …";
            var (command, problem) = await ResolveGalleryDlAsync();
            job.Cts.Token.ThrowIfCancellationRequested();

            if (command is null)
                throw new InvalidOperationException(
                    "gallery-dl wurde nicht gefunden. " + (problem ?? string.Empty) +
                    " Pfad unter „Einstellungen“ angeben oder installieren: pip install -U gallery-dl");

            job.Message = null;
            Directory.CreateDirectory(_libraryPath);

            var startInfo = GalleryDl.CreateStartInfo(command, _libraryPath);
            startInfo.ArgumentList.Add("-d");                // Zielordner der Bibliothek
            startInfo.ArgumentList.Add(_libraryPath);
            startInfo.ArgumentList.Add("--write-metadata");  // neben jede Datei eine JSON mit Künstlern, Parodien, Charakteren, Tags, Sprache …
            startInfo.ArgumentList.Add("--write-info-json"); // zusätzlich eine info.json für die ganze Galerie – beides liest der MetadataParser
            foreach (var argument in GalleryDl.SplitArguments(_extraArgs))
                startInfo.ArgumentList.Add(argument);
            var fetchUrl = GalleryDl.ExtractorUrl(job.Url);
            var jobSite = SourceCatalog.FromUrl(fetchUrl);
            var jobDomain = SourceCatalog.Find(jobSite)?.Domains[0];
            if (!string.IsNullOrEmpty(jobDomain))
            {
                ApplySessionCookiesToApi();
                var (cookieFile, namedCookies) = _api.ExportSessionCookies(jobDomain);
                GalleryDl.AddCookieArguments(startInfo, cookieFile, namedCookies);
            }

            GalleryDl.PreferredBrowser = _settings.PreferredBrowser;
            GalleryDl.AddBrowserSession(startInfo);
            startInfo.ArgumentList.Add(fetchUrl);

            job.AppendLog("$ " + GalleryDl.DescribeCommand(startInfo));
            MarkLogDirty(job);

            using var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("gallery-dl konnte nicht gestartet werden.");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                throw new InvalidOperationException("gallery-dl konnte nicht gestartet werden: " + ex.Message, ex);
            }

            process.StandardInput.Close(); // gallery-dl soll nie auf Eingaben warten

            // Abbrechen = Prozess samt Kindprozessen beenden (die gallery-dl.exe startet unter Umständen einen Unterprozess).
            using var cancelRegistration = job.Cts.Token.Register(() => GalleryDl.KillProcess(process));

            await Task.WhenAll(
                PumpAsync(process.StandardOutput, job, isError: false, job.Cts.Token),
                PumpAsync(process.StandardError, job, isError: true, job.Cts.Token));

            try
            {
                await process.WaitForExitAsync(job.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                GalleryDl.KillProcess(process);
                try
                {
                    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await process.WaitForExitAsync(wait.Token);
                }
                catch (OperationCanceledException)
                {
                    // Prozess reagiert nicht – der using-Block gibt das Handle trotzdem frei
                }
            }

            if (!process.HasExited)
                throw new InvalidOperationException("gallery-dl konnte nicht beendet werden.");

            exitCode = process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            // Abbruch durch den Nutzer – wird in FinishJobAsync ausgewertet
        }
        catch (ObjectDisposedException)
        {
            // Fenster zu, Token bereits freigegeben
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            job.AppendLog("! " + ex.Message);
            MarkLogDirty(job);
        }

        await FinishJobAsync(job, exitCode, failure);
        PumpQueue();
    }

    private async Task PumpAsync(StreamReader reader, DownloadJob job, bool isError, CancellationToken cancellationToken)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
                HandleOutputLine(job, line, isError);
        }
        catch (IOException)
        {
            // Pipe wurde beim Beenden des Prozesses geschlossen
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// gallery-dl schreibt jede fertige Datei als Pfad auf stdout (übersprungene mit "# " davor);
    /// Meldungen und Fehler stehen auf stderr.
    /// </summary>
    private void HandleOutputLine(DownloadJob job, string rawLine, bool isError)
    {
        var line = AnsiRegex.Replace(rawLine, string.Empty).TrimEnd();
        if (line.Length == 0)
            return;

        job.AppendLog(line);
        MarkLogDirty(job);
        DownloadsViewModel.Apply(job, line);

        if (!isError && LibraryScanner.TryParseDownloadedPath(line, out var path, out var skipped))
        {
            job.RegisterFile(path, skipped);
            UpdateHud();
        }
        else if (line.Contains("database is locked", StringComparison.OrdinalIgnoreCase))
        {
            job.LastError = GalleryDl.BrowserCookieLockedMessage;
        }
        else if (line.Contains("[error]", StringComparison.OrdinalIgnoreCase)
                 || line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
        {
            job.LastError = line;
        }
    }

    private async Task FinishJobAsync(DownloadJob job, int exitCode, string? failure)
    {
        if (job.Cts.IsCancellationRequested)
        {
            job.AppendLog("— abgebrochen —");
            job.Message = job.TotalFiles > 0
                ? LanguageManager.Instance.Get("Download_Cancelled") + " · " + job.TotalFiles
                : LanguageManager.Instance.Get("Download_Cancelled");
            job.Status = JobStatus.Cancelled;
            MarkLogDirty(job);
            UpdateHud();
            SetStatus($"Download abgebrochen: {Truncate(job.DisplayTitle, 80)}", StatusKind.Warning);
            return;
        }

        if (failure is not null || (exitCode != 0 && job.Files.Count == 0))
        {
            var locked = GalleryDl.IsCookieDatabaseLocked(exitCode, job.LastError) || GalleryDl.IsCookieDatabaseLocked(exitCode, failure);
            job.Message = locked
                ? LanguageManager.Instance.Get("Web_CookieLocked")
                : failure ?? job.LastError ?? LanguageManager.Instance.Get("Download_ExitCode", exitCode);
            job.Status = JobStatus.Failed;
            SetStatus($"Download fehlgeschlagen: {Truncate(job.Message, 120)}", StatusKind.Error);
            UpdateHud();
            return;
        }

        if (job.Files.Count == 0)
        {
            job.Message = LanguageManager.Instance.Get("Download_NoFiles");
            job.Status = JobStatus.Failed;
            SetStatus(job.Message, StatusKind.Warning);
            UpdateHud();
            return;
        }

        try
        {
            job.Message = "Importiere in die Bibliothek …";
            var result = await ImportJobAsync(job);

            job.ResultTitle = result.Count > 1 ? $"{result.Title} (+{result.Count - 1})" : result.Title;
            job.Message = (result.IsNew ? "Hinzugefügt" : "Aktualisiert")
                          + (result.Count > 1 ? $" · {result.Count} Einträge" : string.Empty)
                          + $" · {result.PageCount} Seiten"
                          + (exitCode != 0 ? $" · mit Warnungen (Code {exitCode})" : string.Empty);
            job.Status = JobStatus.Completed;
            SetStatus(
                result.Count > 1
                    ? $"{result.Count} Einträge ({result.PageCount} Seiten) wurden {(result.IsNew ? "zur Bibliothek hinzugefügt" : "aktualisiert")}."
                    : $"„{Truncate(result.Title, 60)}“ {(result.IsNew ? "wurde zur Bibliothek hinzugefügt" : "wurde aktualisiert")}.",
                exitCode != 0 ? StatusKind.Warning : StatusKind.Success);

            await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            job.Message = "Import fehlgeschlagen: " + ex.Message;
            job.Status = JobStatus.Failed;
            SetStatus(job.Message, StatusKind.Error);
        }

        UpdateHud();
    }

    /// <summary>
    /// Liest die Metadaten-Dateien des Downloads (JSON von gallery-dl, ComicInfo.xml …) und schreibt Eintrag und Tags in die Datenbank –
    /// die eigentliche Arbeit steckt im <see cref="MetadataParser"/>. Läuft im Hintergrund-Thread.
    /// </summary>
    private Task<ImportOutcome> ImportJobAsync(DownloadJob job)
    {
        var files = job.Files.ToArray();
        var url = job.Url;
        var customTitle = job.CustomTitle;
        var customTags = job.CustomTags.ToArray();

        return Task.Run(() => MetadataParser.ImportDownload(_db, files, url, customTitle, customTags));
    }

    // ── Ordner-Scan: vorhandene Ordner und Archive im Bibliotheksordner aufnehmen ──

    private bool _scanning;

    private async void ScanLibrary_Click(object sender, RoutedEventArgs e) => await ScanLibraryAsync(automatic: false);

    /// <summary>
    /// Nimmt alle Ordner und Archive im Bibliotheksordner auf, die noch nicht in der Datenbank stehen, und bestimmt die Website von
    /// Einträgen ohne erkannte Quelle neu. Bestehende Einträge, Favoriten und Tags bleiben unberührt; Dateien werden nur gelesen.
    /// </summary>
    private async Task ScanLibraryAsync(bool automatic)
    {
        if (_scanning)
            return;

        _scanning = true;
        var root = _libraryPath;

        try
        {
            SetStatus("Scanne den Bibliotheksordner …");

            // Meldet den Fortschritt im UI-Thread zurück (Progress<T> bindet sich an den aktuellen Kontext).
            var progress = new Progress<(int Done, int Total)>(p =>
                SetStatus($"Scanne den Bibliotheksordner … {p.Done} / {p.Total}"));

            var (added, known, skipped) = await Task.Run(() => ImportFolders(root, progress));
            var repaired = automatic ? 0 : await Task.Run(RepairSources);

            if (added > 0 || repaired > 0)
            {
                await RefreshAllAsync();
                SetStatus(
                    (added > 0 ? $"{added} {(added == 1 ? "Eintrag" : "Einträge")} aus dem Bibliotheksordner importiert." : "Keine neuen Einträge.")
                    + (repaired > 0 ? $" Quelle von {repaired} Einträgen erkannt." : string.Empty)
                    + (skipped > 0 ? $" {skipped} nicht lesbar/ohne Bilder übersprungen." : string.Empty),
                    StatusKind.Success);
            }
            else if (!automatic)
            {
                SetStatus(
                    known > 0
                        ? "Keine neuen Einträge gefunden – alles ist bereits in der Bibliothek."
                        : "Keine Galerien gefunden. Im Bibliotheksordner liegen weder Ordner mit Bildern noch .cbz/.zip-Archive.",
                    StatusKind.Info);
            }
            else
            {
                SetStatus("Bereit");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Ordner-Scan fehlgeschlagen: {ex.Message}", StatusKind.Error);
        }
        finally
        {
            _scanning = false;
        }
    }

    /// <summary>
    /// Nimmt neue Ordner und Archive auf. Läuft im Hintergrund-Thread. Der aufwendige Teil (Archive öffnen, Cover-Vorschaubild
    /// erzeugen) läuft parallel; eingetragen wird danach in <b>einer</b> Transaktion und in Pfad-Reihenfolge.
    /// </summary>
    private (int Added, int Known, int Skipped) ImportFolders(string root, IProgress<(int Done, int Total)> progress)
    {
        var knownPaths = _db.GetFolderPaths();
        var all = LibraryScanner.FindEntries(root);
        var pending = all.Where(path => !knownPaths.Contains(path.TrimEnd('\\', '/'))).ToList();

        var built = new Manga?[pending.Count];
        var done = 0;

        Parallel.For(0, pending.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            try
            {
                built[i] = LibraryScanner.BuildManga(pending[i]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                built[i] = null; // unlesbar: überspringen, der Rest wird trotzdem aufgenommen
            }
            finally
            {
                progress.Report((Interlocked.Increment(ref done), pending.Count));
            }
        });

        // Einträge ohne Bilder (z. B. ein ZIP mit Programmdateien) werden nicht aufgenommen.
        var accepted = built.Where(m => m is { PageCount: > 0 }).Select(m => m!).ToList();
        try
        {
            new LibraryRepository(_db).AddRange(accepted.Select(LocalManga.From));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            throw new InvalidOperationException("Die Datenbank konnte die neuen Einträge nicht speichern: " + ex.Message, ex);
        }

        return (accepted.Count, all.Count - pending.Count, pending.Count - accepted.Count);
    }

    /// <summary>Bestimmt die Website von Einträgen mit der Quelle „Andere“ aus URL und Metadaten-Dateien neu. Läuft im Hintergrund-Thread.</summary>
    private int RepairSources()
    {
        var repaired = 0;
        foreach (var (id, path, url) in _db.GetEntriesWithoutSource())
        {
            var source = LibraryScanner.DetectSource(path, url);
            if (source != SourceCatalog.Other && _db.UpdateSource(id, source))
                repaired++;
        }

        return repaired;
    }

    // ── gallery-dl finden (gemeinsame Logik in GalleryDl.cs) ──

    /// <summary>Der gefundene Befehl wird gemerkt, bis die Einstellungen geändert werden.</summary>
    private async Task<(GalleryDlCommand? Command, string? Problem)> ResolveGalleryDlAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _galleryDl is not null)
            return (_galleryDl, null);

        var result = await GalleryDl.ResolveAsync(_galleryDlPath);
        _galleryDl = result.Command;
        return result;
    }

    private async Task CheckGalleryDlAsync(bool silent)
    {
        try
        {
            GalleryDlStatusText.Foreground = (Brush)FindResource("MutedBrush");
            GalleryDlStatusText.Text = "Prüfe gallery-dl …";

            var (command, problem) = await ResolveGalleryDlAsync(forceRefresh: true);
            if (command is null)
            {
                GalleryDlStatusText.Foreground = (Brush)FindResource("DangerBrush");
                GalleryDlStatusText.Text = "✖  " + problem + "  Installation: pip install -U gallery-dl  oder gallery-dl.exe herunterladen und hier auswählen.";
                if (silent)
                    SetStatus("gallery-dl nicht gefunden – bitte unter „Einstellungen“ einrichten.", StatusKind.Warning);
            }
            else
            {
                GalleryDlStatusText.Foreground = (Brush)FindResource("SuccessBrush");
                GalleryDlStatusText.Text = "✔  " + command.Description;
                if (!silent)
                    SetStatus("gallery-dl ist einsatzbereit.", StatusKind.Success);
            }
        }
        catch (Exception ex)
        {
            GalleryDlStatusText.Foreground = (Brush)FindResource("DangerBrush");
            GalleryDlStatusText.Text = "✖  gallery-dl konnte nicht geprüft werden: " + ex.Message;
            if (!silent)
                SetStatus("gallery-dl-Prüfung fehlgeschlagen: " + ex.Message, StatusKind.Error);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Web Discovery: eine Website · Online-Tags · HTTP-APIs · Reader per gallery-dl
    // ════════════════════════════════════════════════════════════════════

    private void BindWebSources()
    {
        if (App.Services is null)
            return;

        _webSearchModel = new WebSearchViewModel(App.Services.GetRequiredService<IScraperFactory>());
        _webSearchModel.CategoryLoader = (site, category) =>
            _db.GetTagsBySiteCategory(site, category, UseHitomiTagFallback(site))
                .Select(tag => new TagChip(tag.Name, tag.MangaCount))
                .ToList();
        _webSearchModel.CategoryLoaded += SyncWebTagSelection;
        WebSourceBar.DataContext = _webSearchModel;
        _webSearchModel.SelectionChanged += OnWebSourceSelectionChanged;
        WebCatalogGroups.ItemsSource = _webSearchModel.TagGroups;
        ApplyWebSource(_webSearchModel.Selected, resetResults: false);
    }

    private void OnWebSourceSelectionChanged() => ApplyWebSource(_webSearchModel?.Selected, resetResults: true);

    private void ApplyWebSource(WebSourceOption? option, bool resetResults)
    {
        if (!_webSearching)
            WebSearchButton.IsEnabled = _webSearchModel?.CanSearch ?? true;

        if (option is not { IsImplemented: true, SiteId: { Length: > 0 } siteId, Scraper: { } scraper })
        {
            _activeScraper = null;
            _webSearchModel?.ReplaceTags(Array.Empty<TagInfo>());
            ShowWebTagSidebar(0);
            if (option is { IsPlaceholder: false })
            {
                var message = LanguageManager.Instance.Get("Web_Roadmap", option.Title);
                WebDiscoveryPanelTitle.Text = "Noch nicht verfügbar";
                WebDiscoveryPanelHint.Text = message;
                if (_webHits.Count == 0)
                    ApplyWebDiscoveryVisibility();
                SetStatus(message, StatusKind.Info);
            }

            return;
        }

        _activeScraper = scraper;
        var showHitomiBank = siteId is not ("hitomi" or "simplyhentai");
        if (HitomiTagBankToggle is not null)
            HitomiTagBankToggle.Visibility = showHitomiBank ? Visibility.Visible : Visibility.Collapsed;
        if (HitomiTagBankStatus is not null)
            HitomiTagBankStatus.Visibility = showHitomiBank ? Visibility.Visible : Visibility.Collapsed;

        var changed = siteId != _webSource;
        if (!resetResults && !changed)
            return;

        if (changed)
        {
            _webSearchModel?.ReplaceTags(Array.Empty<TagInfo>());
            ShowWebTagSidebar(0);
        }

        if (resetResults && changed)
        {
            _webSource = siteId;
            _webTags.Clear();
            _webHits.Clear();
            CloseWebSuggestions();
            WebDiscoveryPanelTitle.Text = "Web Discovery";
            WebDiscoveryPanelHint.Text = "Wähle ein Tag oder mehrere aus um die Suche zu starten";
            ApplyWebDiscoveryVisibility();
            UpdateSubtitle();
            SetStatus("Ziel: " + SourceCatalog.DisplayName(_webSource) + " – nur Online-Tags dieser Website.");
        }

        _ = RefreshWebPopularTagsAsync();
    }

    private void HitomiTagBank_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;
        _settings.UseGlobalTagFallback = HitomiTagBankToggle.IsChecked == true;
        UpdateHitomiTagBankStatus();
        try
        {
            _db.SetSetting(nameof(AppSettings.UseGlobalTagFallback), _settings.UseGlobalTagFallback ? "1" : "0");
        }
        catch (Exception ex)
        {
            SetStatus("Hitomi-Tagbank konnte nicht gespeichert werden: " + ex.Message, StatusKind.Error);
            return;
        }

        SetStatus(
            _settings.UseGlobalTagFallback ? "Hitomi-Tagbank eingeschaltet." : "Hitomi-Tagbank ausgeschaltet.",
            _settings.UseGlobalTagFallback ? StatusKind.Info : StatusKind.Success);
        _ = RefreshWebPopularTagsAsync();
    }

    private void UpdateHitomiTagBankStatus()
    {
        if (HitomiTagBankToggle is null)
            return;

        var on = HitomiTagBankToggle.IsChecked == true;
        HitomiTagBankToggle.Content = on ? "Hitomi-Tagbank an" : "Hitomi-Tagbank aus";
        if (HitomiTagBankStatus is null)
            return;

        HitomiTagBankStatus.Text = on
            ? "Hitomi-Tagbank ist an. Autovervollständigung und Katalog ergänzen Hitomi."
            : "Hitomi-Tagbank ist aus. Es gelten nur die Tags dieser Quelle.";
        HitomiTagBankStatus.Foreground = (Brush)FindResource(on ? "AccentBrush" : "MutedBrush");
    }

    private async void DownloadGalleryDl_Click(object sender, RoutedEventArgs e)
    {
        DownloadGalleryDlButton.IsEnabled = false;
        GalleryDlDownloadProgress.Visibility = Visibility.Visible;
        GalleryDlDownloadProgress.IsIndeterminate = true;
        GalleryDlDownloadProgress.Value = 0;
        GalleryDlStatusText.Foreground = (Brush)FindResource("MutedBrush");
        GalleryDlStatusText.Text = "Lade gallery-dl …";

        var progress = new Progress<GalleryDlDownloadUpdate>(update =>
        {
            GalleryDlStatusText.Text = update.Total > 0
                ? update.Status + "  " + (update.Received * 100 / update.Total) + " %"
                : update.Status;
            if (update.Total > 0)
            {
                GalleryDlDownloadProgress.IsIndeterminate = false;
                GalleryDlDownloadProgress.Value = Math.Clamp(update.Received * 100.0 / update.Total, 0, 100);
            }
        });

        try
        {
            var path = await GalleryDlService.DownloadGalleryDlAsync(progress);
            GalleryDlPathBox.Text = path;
            _galleryDlPath = path;
            _settings.GalleryDlPath = path;
            _galleryDl = null;
            await Task.Run(() => _db.SetSetting("GalleryDlPath", path));
            await CheckGalleryDlAsync(silent: false);
            SetStatus("gallery-dl gespeichert: " + path, StatusKind.Success);
        }
        catch (Exception ex)
        {
            GalleryDlStatusText.Foreground = (Brush)FindResource("DangerBrush");
            GalleryDlStatusText.Text = "✖  " + ex.Message;
            SetStatus("gallery-dl-Download fehlgeschlagen: " + ex.Message, StatusKind.Error);
        }
        finally
        {
            DownloadGalleryDlButton.IsEnabled = true;
            GalleryDlDownloadProgress.Visibility = Visibility.Collapsed;
            GalleryDlDownloadProgress.IsIndeterminate = false;
        }
    }

    private void WebCatalogExpander_Changed(object sender, RoutedEventArgs e)
    {
        if (WebCatalogExpanderText is null || sender is not Expander expander)
            return;
        WebCatalogExpanderText.Text = expander.IsExpanded
            ? "🏷️  Tag-Katalog einklappen"
            : "🏷️  Tag-Katalog anzeigen";
        ApplyWebHeaderBudget();
    }

    private void WebTagsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (WebTagsToggle is null || TagCatalogContainer is null)
            return;

        var show = WebTagsToggle.IsChecked == true;
        TagCatalogContainer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        WebTagsToggle.Content = show ? "🏷️  Tags ausblenden" : "🏷️  Tags anzeigen";
        if (!show)
            CloseWebSuggestions();
        ApplyWebHeaderBudget();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyWebHeaderBudget();

    private void WebSearchHeader_MouseEnter(object sender, MouseEventArgs e)
    {
        _webHeaderHover = true;
        if (_webHeaderAutoHide)
            ExpandWebSearchHeader(true);
    }

    private void WebSearchHeader_MouseLeave(object sender, MouseEventArgs e)
    {
        _webHeaderHover = false;
        if (!_webHeaderAutoHide || WebSuggestPopup.IsOpen)
            return;
        CollapseWebSearchHeader(true);
    }

    /// <summary>Nach dem Start einer Suche fährt die Kopfzeile ein. Der obere Rand klappt sie wieder auf.</summary>
    private void EngageWebHeaderAutoHide()
    {
        _webHeaderAutoHide = true;
        CollapseWebSearchHeader(true);
    }

    private void CollapseWebSearchHeader(bool animate)
    {
        if (WebSearchHeader is null)
            return;
        var from = CurrentWebHeaderHeight();
        _webHeaderCollapsed = true;
        WebSearchHeader.Margin = new Thickness(0);
        if (WebSearchHeaderGripMark is not null)
            WebSearchHeaderGripMark.Opacity = 0.9;
        AnimateWebHeader(from, 0, animate);
    }

    private void ExpandWebSearchHeader(bool animate)
    {
        if (WebSearchHeader is null)
            return;
        var from = CurrentWebHeaderHeight();
        WebSearchHeader.Margin = new Thickness(0, 0, 0, 4);
        if (WebSearchHeaderGripMark is not null)
            WebSearchHeaderGripMark.Opacity = 0;
        var target = MeasureWebHeaderHeight(from);
        _webHeaderCollapsed = false;
        AnimateWebHeader(from, target, animate);
    }

    private double CurrentWebHeaderHeight()
    {
        var height = WebSearchHeader.MaxHeight;
        if (double.IsNaN(height) || double.IsInfinity(height))
            height = WebSearchHeader.ActualHeight;
        return Math.Max(0, height);
    }

    private double MeasureWebHeaderHeight(double restore)
    {
        WebSearchHeader.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
        WebSearchHeader.ClearValue(FrameworkElement.MaxHeightProperty);
        var width = WebSearchView?.ActualWidth > 1 ? WebSearchView.ActualWidth : Math.Max(1, ActualWidth);
        WebSearchHeader.Measure(new Size(Math.Max(1, width), double.PositiveInfinity));
        var height = WebSearchHeader.DesiredSize.Height;
        WebSearchHeader.MaxHeight = restore;
        return height < 48 ? 160 : height;
    }

    private void AnimateWebHeader(double from, double to, bool animate)
    {
        if (WebSearchHeader is null)
            return;
        if (!animate || Math.Abs(from - to) < 1)
        {
            WebSearchHeader.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            if (to <= 0)
                WebSearchHeader.MaxHeight = 0;
            else
            {
                WebSearchHeader.ClearValue(FrameworkElement.MaxHeightProperty);
                ApplyWebHeaderBudget();
            }
            return;
        }

        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        if (to > 0)
        {
            animation.Completed += (_, _) =>
            {
                if (_webHeaderCollapsed || WebSearchHeader is null)
                    return;
                WebSearchHeader.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
                WebSearchHeader.ClearValue(FrameworkElement.MaxHeightProperty);
                ApplyWebHeaderBudget();
            };
        }

        WebSearchHeader.BeginAnimation(FrameworkElement.MaxHeightProperty, animation);
    }

    /// <summary>
    /// Zugeklappt bleibt der Kopf (Website + Suchleiste) bei höchstens 22 % der Fensterhöhe.
    /// Offener Tag-Katalog darf bis zur vollen Suchseite nach unten wachsen.
    /// </summary>
    private void ApplyWebHeaderBudget()
    {
        if (WebSearchHeader is null || WebSearchView is null || !WebSearchView.IsVisible || ActualHeight <= 0)
            return;
        if (_webHeaderCollapsed)
            return;
        if (PresentationSource.FromVisual(WebSearchHeader) is null)
            return;

        WebSearchHeader.ClearValue(FrameworkElement.MaxHeightProperty);
        var catalogOpen = WebTagsToggle?.IsChecked == true
                          && WebCatalogExpander?.IsExpanded == true
                          && TagCatalogContainer?.Visibility == Visibility.Visible;

        if (!catalogOpen)
        {
            WebCatalogScroll?.ClearValue(FrameworkElement.MaxHeightProperty);
            return;
        }

        var page = WebSearchView.ActualHeight > 1 ? WebSearchView.ActualHeight : ActualHeight * 0.75;
        if (WebCatalogScroll is not null)
            WebCatalogScroll.MaxHeight = Math.Max(160, page - 84);
    }

    private async Task RefreshWebPopularTagsAsync()
    {
        if (_tags is null || _webSearchModel is null)
            return;

        var source = _webSource;
        var generation = ++_webTagLoad;
        try
        {
            string? accessNote = _activeScraper is BrowserListingProvider open && open.SiteId == source
                ? open.BlockStatus
                : null;
            var storedTags = await Task.Run(() => _db.CountTagsBySite(source));
            var refillHentaiRun = source == HentaiRunProvider.Site && storedTags < 500;
            if ((storedTags == 0 || refillHentaiRun)
                && string.IsNullOrWhiteSpace(accessNote)
                && _activeScraper is ITagCatalogSource catalog
                && catalog.SiteId == source)
            {
                ApplyCatalogCookies(catalog);
                try
                {
                    IProgress<TagDownloadProgress>? progress = null;
                    if (refillHentaiRun)
                    {
                        SetStatus("HentaiRun-Tagkatalog wird vollständig geladen …", StatusKind.Info);
                        progress = new Progress<TagDownloadProgress>(p =>
                        {
                            if (p.Collected > 0)
                                SetStatus(p.Status + " · " + p.Collected.ToString("N0") + " Tags", StatusKind.Info);
                        });
                    }

                    var fetched = await catalog.GetOnlineTagsAsync(fullIndex: refillHentaiRun, progress);
                    if (source != _webSource || generation != _webTagLoad)
                        return;
                    if (fetched.Count > 0)
                    {
                        var rows = ToCatalogRows(fetched);
                        if (refillHentaiRun)
                            await Task.Run(() => _db.ReplaceOnlineTags(source, rows));
                        else
                            await Task.Run(() => _db.UpsertOnlineTags(source, rows, OnlineTagMerge.Max));
                    }
                }
                catch (InvalidOperationException ex)
                {
                    accessNote = ex.Message;
                }

                if (catalog is BrowserListingProvider listed && !string.IsNullOrWhiteSpace(listed.BlockStatus))
                    accessNote = listed.BlockStatus;
                if (catalog is SimplyHentaiProvider simply && !string.IsNullOrWhiteSpace(simply.BlockStatus))
                    accessNote = simply.BlockStatus;
            }
            else if (source == HentaiFoxScraperProvider.SiteId
                && await Task.Run(() => _db.CountTagsBySite(source)) == 0
                && _activeScraper is HentaiFoxScraperProvider fox)
            {
                var fetched = await fox.GetOnlineTagsAsync();
                if (source != _webSource || generation != _webTagLoad)
                    return;
                var rows = fetched
                    .Select(tag => (Tag: TagNames.Format(tag.Category, tag.Name), Count: tag.Count))
                    .ToList();
                await Task.Run(() => _db.UpsertOnlineTags(source, rows, OnlineTagMerge.Max));
            }

            var ownCount = await Task.Run(() => _db.CountTagsBySite(source));
            var fallback = UseHitomiTagFallback(source, ownCount);
            var summaries = await Task.Run(() => _db.GetTagCategoriesBySite(source, fallback));
            if (source != _webSource || generation != _webTagLoad)
                return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (source != _webSource || generation != _webTagLoad || _webSearchModel is null)
                    return;

                _webSearchModel.ShowCategories(source, summaries);
                TagNames.RegisterCategories(summaries.Select(item => item.Category).Where(category => category.Length > 0));
                var count = summaries.Sum(item => item.Count);
                ShowWebTagSidebar(count);
                string hint;
                if (source == "simplyhentai" && !string.IsNullOrWhiteSpace(accessNote))
                {
                    hint = accessNote;
                    SetStatus(hint, StatusKind.Error);
                }
                else if (ownCount == 0)
                    hint = "Noch keine Online-Tags für " + SourceCatalog.DisplayName(source) +
                           " – „Tags dieser Quelle aktualisieren“ lädt den vollständigen Index.";
                else
                    hint = ownCount.ToString("N0") + " Tags von " + SourceCatalog.DisplayName(source) +
                           " in online_site_tags. Kategorien sind eingeklappt; Aufklappen lädt nur diese Kategorie.";
                if (fallback)
                    hint += " Die Hitomi-Tagbank ist manuell eingeschaltet.";
                WebPopularHint.Text = hint;
            });
        }
        catch (Exception)
        {
            // Katalog ist optional
        }
    }

    private void WebSearchTagBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
            return;

        var (tags, remaining) = SearchSyntax.Commit(WebSearchTagBox.Text, commitLast: false);
        if (tags.Count > 0)
        {
            foreach (var tag in tags.Where(t => !t.Exclude))
                AddWebTag(tag.Tag);
            WebSearchTagBox.Text = remaining;
            WebSearchTagBox.CaretIndex = remaining.Length;
        }

        _ = UpdateWebSuggestionsAsync();
    }

    private void AddWebTag(string tag)
    {
        tag = TagNames.Normalize(tag);
        if (tag.Length == 0 || _webTags.Any(t => t.Tag == tag))
            return;
        _webTags.Add(new ActiveTag(tag, exclude: false));
        SyncWebTagSelection();
    }

    private void ApplyCatalogCookies(object scraper)
    {
        switch (scraper)
        {
            case AsmhentaiProvider asm:
                asm.ApplySessionCookies(_db.GetHttpCookies("asmhentai.com"));
                break;
            case BrowserListingProvider site:
                site.ApplyGalleryDlSession(_db.GetHttpCookies(site.CookieHost));
                break;
            case SimplyHentaiProvider simply:
                simply.ApplyGalleryDlSession(SimplyHentaiCookies());
                break;
        }
    }

    private IEnumerable<StoredHttpCookie> SimplyHentaiCookies()
    {
        foreach (var host in new[] { "simplyhentai.org", "www.simplyhentai.org" })
        {
            foreach (var cookie in _db.GetHttpCookies(host))
                yield return cookie;
        }
    }

    private bool UseHitomiTagFallback(string site, int? ownCount = null)
    {
        _ = ownCount;
        if (site is "hitomi" or "simplyhentai")
            return false;
        return _settings.UseGlobalTagFallback;
    }

    private void ShowWebTagSidebar(int count)
    {
        if (_page != AppPage.WebSearch)
            return;

        TagList.ItemTemplate = (DataTemplate)FindResource("WebTagCategoryTemplate");
        if (_webSearchModel is not null)
            TagList.ItemsSource = _webSearchModel.TagGroups;
        NoTagsText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoTagsText.Text = count == 0
            ? "Keine Online-Tags für diese Quelle."
            : string.Empty;
        TagCountText.Text = count > 0 ? count.ToString("N0") : string.Empty;
    }

    private void SyncWebTagSelection()
    {
        if (_webSearchModel is null)
            return;

        var selected = _webTags.Select(tag => tag.Tag).ToHashSet(StringComparer.Ordinal);
        foreach (var chip in _webSearchModel.TagsCollection)
            chip.IsIncluded = selected.Contains(chip.Name);
    }

    private void WebActiveTag_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActiveTag tag)
        {
            _webTags.Remove(tag);
            SyncWebTagSelection();
        }
    }

    private void WebCatalogList_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list)
            return;
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(list, source) is ListBoxItem { DataContext: TagChip chip })
        {
            AddWebTag(chip.Name);
            e.Handled = true;
        }
        else if (e.OriginalSource is DependencyObject src
                 && ItemsControl.ContainerFromElement(list, src) is ListBoxItem { DataContext: TagInfo info })
        {
            AddWebTag(info.Name);
            e.Handled = true;
        }
    }

    private static List<(string Tag, int Count)> ToCatalogRows(IEnumerable<CatalogTag> tags) =>
        tags.Select(tag => (Tag: TagNames.Format(tag.Category, tag.Name), Count: tag.Count))
            .Where(row => row.Tag.Length > 0 && row.Count > 0)
            .ToList();

    private async void WebTagDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_webTagDownloading || _tags is null)
            return;

        _webTagDownloadCts?.Dispose();
        _webTagDownloadCts = new CancellationTokenSource();
        var ct = _webTagDownloadCts.Token;
        var site = _webSource;
        _webTagDownloading = true;
        WebTagDownloadButton.IsEnabled = false;
        WebTagDownloadProgress.Visibility = Visibility.Visible;
        WebTagDownloadProgress.IsIndeterminate = true;
        WebTagDownloadStatus.Visibility = Visibility.Visible;
        WebTagDownloadStatus.Text = "Lade Tag-Index von " + SourceCatalog.DisplayName(site) + " …";

        var progress = new Progress<TagDownloadProgress>(p =>
        {
            if (p.Total > 0)
            {
                WebTagDownloadProgress.IsIndeterminate = false;
                WebTagDownloadProgress.Maximum = Math.Max(1, p.Total);
                WebTagDownloadProgress.Value = Math.Clamp(p.Completed, 0, p.Total);
            }
            else
                WebTagDownloadProgress.IsIndeterminate = true;
            WebTagDownloadStatus.Text = p.Status + (p.Collected > 0 ? "  ·  " + p.Collected.ToString("N0") + " Tags" : string.Empty);
        });

        try
        {
            int count;
            if (_activeScraper is ITagCatalogSource catalog && catalog.SiteId == site)
            {
                ApplyCatalogCookies(catalog);
                var fetched = await catalog.GetOnlineTagsAsync(fullIndex: true, progress, ct);
                var rows = ToCatalogRows(fetched);
                if (rows.Count == 0)
                    throw new InvalidOperationException(SourceCatalog.DisplayName(site) + " lieferte keinen bereinigten Tagkatalog.");
                count = await Task.Run(() => _db.ReplaceOnlineTags(site, rows), ct);
            }
            else if (site == HentaiFoxScraperProvider.SiteId)
            {
                if (_activeScraper is not HentaiFoxScraperProvider fox)
                    throw new InvalidOperationException("HentaiFox ist nicht als Suchquelle registriert.");
                var fetched = await fox.GetOnlineTagsAsync(fullIndex: true, progress, ct);
                var rows = fetched
                    .Select(tag => (Tag: TagNames.Format(tag.Category, tag.Name), Count: tag.Count))
                    .ToList();
                count = await Task.Run(() => _db.DownloadAndSeedSiteTags(site, rows), ct);
            }
            else
            {
                ApplySessionCookiesToApi();
                count = await _tags.DownloadAndSeedSiteTags(site, _api, progress, ct);
            }
            await RefreshWebPopularTagsAsync();
            SetStatus(count.ToString("N0") + " Online-Tags für " + SourceCatalog.DisplayName(site) + " gespeichert.", StatusKind.Success);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Tag-Download abgebrochen.", StatusKind.Warning);
        }
        catch (Exception ex)
        {
            AppendSessionLog("! Tag-Index " + site + ": " + FlattenLogLine(ex.Message));
            SetStatus("Tag-Index: " + ex.Message, StatusKind.Error);
            if (UseHitomiTagFallback(site))
                await RefreshWebPopularTagsAsync();
        }
        finally
        {
            _webTagDownloading = false;
            WebTagDownloadButton.IsEnabled = true;
            WebTagDownloadProgress.Visibility = Visibility.Collapsed;
            WebTagDownloadProgress.IsIndeterminate = false;
        }
    }

    private void WebTagExport_Click(object sender, RoutedEventArgs e)
    {
        if (_tags is null)
            return;
        try
        {
            var json = _tags.ExportJson(_webSource);
            var dialog = new SaveFileDialog
            {
                Filter = "JSON (*.json)|*.json",
                FileName = "online-tags-" + _webSource + ".json",
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog(this) != true)
                return;
            File.WriteAllText(dialog.FileName, json);
            SetStatus("Tag-Katalog exportiert: " + dialog.FileName, StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus("Export fehlgeschlagen: " + ex.Message, StatusKind.Error);
        }
    }

    private void WebTagImport_Click(object sender, RoutedEventArgs e)
    {
        if (_tags is null)
            return;
        try
        {
            var dialog = new OpenFileDialog
            {
                Filter = "JSON (*.json)|*.json|Alle Dateien (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(this) != true)
                return;
            var json = File.ReadAllText(dialog.FileName);
            var count = _tags.ImportJson(_webSource, json);
            _ = RefreshWebPopularTagsAsync();
            SetStatus(count.ToString("N0") + " Tags importiert für " + SourceCatalog.DisplayName(_webSource) + ".", StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus("Import fehlgeschlagen: " + ex.Message, StatusKind.Error);
        }
    }

    private void WebPopularTag_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TagInfo info)
            AddWebTag(info.Name);
    }

    private void WebSearchTagBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!WebSuggestPopup.IsOpen)
            return;

        if (e.Key is Key.Down or Key.Up)
        {
            MoveWebSuggestion(e.Key == Key.Down ? 1 : -1);
            e.Handled = true;
        }
        else if (e.Key is Key.Tab or Key.Enter && WebSuggestList.SelectedItem is TagSuggestion selected)
        {
            e.Handled = true;
            AcceptWebSuggestion(selected);
        }
        else if (e.Key == Key.Escape)
        {
            CloseWebSuggestions();
            e.Handled = true;
        }
    }

    private async void WebSearchTagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !WebSuggestPopup.IsOpen)
        {
            e.Handled = true;
            CommitWebSearchBox(commitLast: true);
            await RunWebSearchAsync(reset: true);
        }
        else if (e.Key == Key.Back && WebSearchTagBox.Text.Length == 0 && _webTags.Count > 0)
        {
            _webTags.RemoveAt(_webTags.Count - 1);
            e.Handled = true;
        }
    }

    private void WebSearchTagBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CloseWebSuggestions();

    private void CommitWebSearchBox(bool commitLast)
    {
        var (tags, remaining) = SearchSyntax.Commit(WebSearchTagBox.Text, commitLast);
        foreach (var tag in tags.Where(t => !t.Exclude))
            AddWebTag(tag.Tag);
        if (WebSearchTagBox.Text != remaining)
        {
            WebSearchTagBox.Text = remaining;
            WebSearchTagBox.CaretIndex = remaining.Length;
        }
    }

    private async Task UpdateWebSuggestionsAsync()
    {
        var version = ++_webSuggestVersion;
        try
        {
            if (_tags is null
                || SearchSyntax.CurrentWord(WebSearchTagBox.Text) is not { } word
                || (word.Query.Length < 2 && !word.Query.Contains(':')))
            {
                CloseWebSuggestions();
                return;
            }

            var skip = _webTags.Select(a => a.Tag).ToList();
            var source = _webSource;
            var fallback = UseHitomiTagFallback(source);
            var found = await Task.Run(() => _tags.Suggest(source, word.Query, limit: null, skip, fallback));
            if (version != _webSuggestVersion)
                return;

            var items = found.Select(tag => new TagSuggestion(tag, word.Exclude, word.Raw)).ToList();
            if (version != _webSuggestVersion)
                return;

            _webSuggestionItems = items;
            WebSuggestList.ItemsSource = items;

            if (items.Count == 0 || !WebSearchTagBox.IsKeyboardFocusWithin)
            {
                CloseWebSuggestions();
                return;
            }

            WebSuggestList.SelectedIndex = -1;
            WebSuggestPopup.IsOpen = true;
        }
        catch (Exception)
        {
            CloseWebSuggestions();
        }
    }

    private void CloseWebSuggestions()
    {
        var wasOpen = WebSuggestPopup.IsOpen;
        _webSuggestVersion++;
        WebSuggestPopup.IsOpen = false;
        WebSuggestList.SelectedIndex = -1;
        if (wasOpen && _webHeaderAutoHide && !_webHeaderHover)
            CollapseWebSearchHeader(true);
    }

    private void MoveWebSuggestion(int delta)
    {
        if (_webSuggestionItems.Count == 0)
            return;

        var index = WebSuggestList.SelectedIndex + delta;
        if (index < 0)
            index = _webSuggestionItems.Count - 1;
        else if (index >= _webSuggestionItems.Count)
            index = 0;

        WebSuggestList.SelectedIndex = index;
        WebSuggestList.ScrollIntoView(WebSuggestList.SelectedItem);
    }

    private void AcceptWebSuggestion(TagSuggestion suggestion)
    {
        var rest = SearchSyntax.RemoveLastWord(WebSearchTagBox.Text, suggestion.Raw);
        CloseWebSuggestions();
        if (!suggestion.Exclude)
            AddWebTag(suggestion.Tag);
        WebSearchTagBox.Text = rest;
        WebSearchTagBox.CaretIndex = rest.Length;
        WebSearchTagBox.Focus();
    }

    private void WebSuggestList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(WebSuggestList, source) is ListBoxItem { DataContext: TagSuggestion suggestion })
        {
            e.Handled = true;
            AcceptWebSuggestion(suggestion);
        }
    }

    private async void WebSearch_Click(object sender, RoutedEventArgs e) => await RunWebSearchAsync(reset: true);

    private async void WebSearchNext_Click(object sender, RoutedEventArgs e) =>
        await RunWebSearchAsync(reset: false, page: _webPage + 1, append: false);

    private async void WebSearchPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_webPage <= 1)
            return;
        await RunWebSearchAsync(reset: false, page: _webPage - 1, append: false);
    }

    private void WebSearchCancel_Click(object sender, RoutedEventArgs e)
    {
        try { _webSearchCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RunWebSearchAsync(bool reset, int? page = null, bool append = false)
    {
        if (_webSearching)
        {
            try { _webSearchCts?.Cancel(); }
            catch (ObjectDisposedException) { }
            return;
        }

        if (_webSearchModel?.Selected is { IsImplemented: false, IsPlaceholder: false } pending)
        {
            var message = LanguageManager.Instance.Get("Web_Roadmap", pending.Title);
            SetStatus(message, StatusKind.Info);
            return;
        }

        if (_activeScraper is null && _webSearchModel?.Selected?.Scraper is { } scraper)
            _activeScraper = scraper;

        if (reset)
        {
            CommitWebSearchBox(commitLast: true);
            CloseWebSuggestions();
            page = 1;
            append = false;
        }

        var tags = reset
            ? _webTags.Select(t => t.Tag).ToList()
            : _webSearchTags;
        var extra = reset ? WebSearchTagBox.Text.Trim() : _webSearchExtra;
        if (reset)
        {
            var provider = TagQuery.FromSite(_webSource);
            if (provider == SourceProvider.Ehentai || _activeScraper is EHentaiStyleProvider)
            {
                var pieces = new List<string>(tags);
                if (extra.Length > 0)
                    pieces.Add(extra);
                tags = TagQuery.BuildEhentaiQuery(pieces).ToList();
                extra = string.Empty;
            }
            else
            {
                tags = tags.Select(t => TagQuery.NormalizeTag(t, provider)).Where(t => t.Length > 0).ToList();
                if (extra.Length > 0)
                    extra = TagQuery.NormalizeTag(extra, provider);
            }
        }
        if (tags.Count == 0 && extra.Length == 0)
        {
            SetStatus("Bitte mindestens einen Tag oder ein Suchwort angeben.", StatusKind.Warning);
            WebSearchTagBox.Focus();
            return;
        }

        if (reset && _webSearchModel is not null)
        {
            _webSearchModel.SetBlacklist(new LibraryRepository(_db).GetBlacklistedTags());
            foreach (var excluded in _webSearchModel.ExclusionQuery(_webSource))
                tags.Add(excluded);
        }
        else
            _webSearchModel?.SetBlacklist(new LibraryRepository(_db).GetBlacklistedTags());

        if (_webSource == "exhentai")
        {
            SetStatus("ExHentai ist vorübergehend nicht auswählbar. Bitte E-Hentai verwenden.", StatusKind.Warning);
            return;
        }

        if (WebSearchCookieButton is not null)
            WebSearchCookieButton.Visibility = Visibility.Collapsed;
        _webSearching = true;
        EngageWebHeaderAutoHide();
        if (reset)
        {
            ResetPageCache();
            _webHasMore = false;
            _webApiTotals = false;
            _webSearchTags = tags;
            _webSearchExtra = extra;
            _webHits.Clear();
            _webRangeStart = 1;
            if (WebCatalogExpander.IsExpanded)
                WebCatalogExpander.IsExpanded = false;
        }

        var pageGeneration = _pageGeneration;
        var requestedPage = Math.Max(1, page ?? _webPage);
        var instantPage = ReadyPage(requestedPage) is not null;
        _webSearchCts?.Dispose();
        _webSearchCts = new CancellationTokenSource();
        var ct = _webSearchCts.Token;
        SetWebSearchBusy(true, $"Seite {requestedPage} auf {SourceCatalog.DisplayName(_webSource)} …", overlay: !append && !instantPage);
        UpdateSubtitle();
        if (reset)
        {
            LogOnlineRead("Web Search  //  " + SourceCatalog.DisplayName(_webSource),
                "$ " + _webSource + "  ·  " + string.Join(" ", tags) + (extra.Length > 0 ? " " + extra : string.Empty));
        }

        var harvested = new List<(string Tag, int Count)>();
        var added = 0;
        var endOfResults = false;
        WebPageBatch? loaded = null;
        try
        {
            var (command, _) = await ResolveGalleryDlAsync();
            _api.GalleryDl = command;
            _api.ExtraArgs = _extraArgs;
            _api.WorkingDirectory = Directory.Exists(_libraryPath) ? _libraryPath : null;
            _api.FlareSolverrUrl = string.IsNullOrWhiteSpace(_flareSolverrUrl) ? null : _flareSolverrUrl;
            ApplySessionCookiesToApi();

            if (_activeScraper is IScraperProvider direct && direct is HentaiFoxScraperProvider or ITagCatalogSource)
            {
                switch (direct)
                {
                    case HentaiFoxScraperProvider fox:
                        fox.TagBlacklist = _webSearchModel;
                        break;
                    case AsmhentaiProvider asm:
                        asm.TagBlacklist = _webSearchModel;
                        asm.ApplySessionCookies(_db.GetHttpCookies("asmhentai.com"));
                        break;
                    case BrowserListingProvider site:
                        site.TagBlacklist = _webSearchModel;
                        site.ApplyGalleryDlSession(_db.GetHttpCookies(site.CookieHost));
                        break;
                    case SimplyHentaiProvider simply:
                        simply.TagBlacklist = _webSearchModel;
                        simply.ApplyGalleryDlSession(SimplyHentaiCookies());
                        break;
                    case ITagCatalogSource catalog:
                        catalog.TagBlacklist = _webSearchModel;
                        break;
                }
            }

            loaded = await ObtainWebPageAsync(requestedPage, pageGeneration, tags, extra, ct);
            if (!append && requestedPage > 1 && !PageHasNewHits(_webHits, loaded))
            {
                endOfResults = true;
                _webHasMore = false;
            }
            else
            {
                var known = new HashSet<string>(StringComparer.Ordinal);
                if (append)
                {
                    foreach (var tile in _webHits)
                        known.Add(tile.Url);
                }
                else
                    _webHits.Clear();

                foreach (var hit in loaded.Hits)
                {
                    if (!known.Add(hit.GalleryUrl))
                        continue;
                    if (_webSearchModel is not null && !_webSearchModel.Allows(hit.Tags))
                        continue;
                    _webHits.Add(new WebSearchTile(hit));
                    added++;
                    if (_webHits.Count == 1)
                        ApplyWebDiscoveryVisibility();
                    if (hit.TagHarvest is { Count: > 0 } harvest)
                        harvested.AddRange(harvest);
                    else
                    {
                        foreach (var t in hit.Tags)
                            harvested.Add((t, 1));
                    }
                }

                if (added > 0)
                {
                    _webPage = requestedPage;
                    if (!append)
                        _webRangeStart = (_webPage - 1) * WebPageLimit + 1;
                }

                _webHasMore = loaded.HasMore
                    && requestedPage < GalleryPages.SafetyCap
                    && added > 0
                    && _webHits.Count > 0;
                if (added == 0 && _webHits.Count > 0)
                    endOfResults = true;
            }

            _webApiTotals = loaded.FromApi;
            _webTotal = loaded.Total;
            _webTotalPages = loaded.TotalPages;

            try
            {
                if (harvested.Count > 0)
                {
                    var merge = _webSource == "nhentai" ? OnlineTagMerge.Max : OnlineTagMerge.Add;
                    var snapshot = harvested.ToList();
                    var site = _webSource;
                    await Task.Run(() => _db.UpsertOnlineTags(site, snapshot, merge), ct);
                }
            }
            catch (Exception ex)
            {
                AppendSessionLog("! Online-Tags konnten nicht gespeichert werden: " + FlattenLogLine(ex.Message));
            }

            if (reset)
                await RefreshWebPopularTagsAsync();

            ApplyWebDiscoveryVisibility();
            UpdateWebSearchCount();
            if (endOfResults)
            {
                AppendSessionLog("— keine weiteren Treffer —");
                SetStatus("Keine weiteren Treffer.", StatusKind.Info);
            }
            else if (_webHits.Count == 0 && !string.IsNullOrWhiteSpace(loaded?.BlockStatus))
            {
                WebDiscoveryPanelTitle.Text = "Kein Online-Zugriff";
                WebDiscoveryPanelHint.Text = loaded.Diagnostic ?? loaded.BlockStatus;
                AppendSessionLog("! " + FlattenLogLine(loaded.Diagnostic ?? loaded.BlockStatus));
                SetStatus(loaded.BlockStatus, StatusKind.Error);
            }
            else if (_webHits.Count == 0)
            {
                WebDiscoveryPanelTitle.Text = "Keine Treffer";
                var diagnostic = loaded?.Diagnostic;
                WebDiscoveryPanelHint.Text = string.IsNullOrWhiteSpace(diagnostic)
                    ? "Die API hat zu dieser Suche keine Galerien geliefert. Andere Tags oder eine andere Website versuchen."
                    : diagnostic;
                AppendSessionLog(string.IsNullOrWhiteSpace(diagnostic) ? "— keine Treffer —" : "! " + FlattenLogLine(diagnostic));
                SetStatus("Keine Online-Treffer für diese Suche.", StatusKind.Warning);
            }
            else
            {
                AppendSessionLog("✔  " + WebSearchCountText.Text);
                SetStatus(WebSearchCountText.Text, StatusKind.Success);
            }
        }
        catch (OperationCanceledException)
        {
            AppendSessionLog("— abgebrochen —");
            SetStatus("Online-Suche abgebrochen.", StatusKind.Warning);
        }
        catch (Exception ex) when (ex is ApiSearchException or ArgumentException or IOException or HttpRequestException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested)
            {
                AppendSessionLog("— abgebrochen —");
                SetStatus("Online-Suche abgebrochen.", StatusKind.Warning);
            }
            else
            {
                AppendSessionLog("! " + FlattenLogLine(ex.Message));
                SetStatus("Online-Suche fehlgeschlagen: " + ex.Message, StatusKind.Error);
                if (_webHits.Count == 0)
                {
                    var locked = IsBrowserCookieLock(ex.Message);
                    var cookies = locked || NeedsCookieSetup(ex.Message);
                    WebDiscoveryPanelTitle.Text = "Suche fehlgeschlagen";
                    WebDiscoveryPanelHint.Text = locked
                        ? GalleryDl.BrowserCookieLockedMessage
                        : cookies
                            ? LanguageManager.Instance.Get("Web_Cloudflare")
                            : ex.Message;
                    if (WebSearchCookieButton is not null)
                        WebSearchCookieButton.Visibility = cookies ? Visibility.Visible : Visibility.Collapsed;
                    ApplyWebDiscoveryVisibility();
                }
            }
        }
        catch (Exception ex)
        {
            AppendSessionLog("! " + FlattenLogLine(ex.Message));
            SetStatus("Online-Suche fehlgeschlagen: " + ex.Message, StatusKind.Error);
            if (_webHits.Count == 0 && (IsBrowserCookieLock(ex.Message) || NeedsCookieSetup(ex.Message)))
            {
                WebDiscoveryPanelTitle.Text = "Suche fehlgeschlagen";
                WebDiscoveryPanelHint.Text = IsBrowserCookieLock(ex.Message)
                    ? GalleryDl.BrowserCookieLockedMessage
                    : LanguageManager.Instance.Get("Web_Cloudflare");
                if (WebSearchCookieButton is not null)
                    WebSearchCookieButton.Visibility = Visibility.Visible;
                ApplyWebDiscoveryVisibility();
            }
        }
        finally
        {
            _webSearching = false;
            SetWebSearchBusy(false, null);
            ApplyWebDiscoveryVisibility();
            UpdateSubtitle();
        }
    }

    private void ResetPageCache()
    {
        lock (_pageGate)
        {
            _pageGeneration++;
            _pageCache.Clear();
        }
    }

    private WebPageBatch? ReadyPage(int page)
    {
        lock (_pageGate)
            return _pageCache.TryGetValue(page, out var found) ? found : null;
    }

    private async Task<WebPageBatch> ObtainWebPageAsync(
        int page,
        int generation,
        IReadOnlyList<string> tags,
        string extra,
        CancellationToken cancellationToken)
    {
        if (ReadyPage(page) is { } ready)
            return ready;

        var batch = await FetchWebPageAsync(_webSource, _activeScraper, tags, extra, page, _settings, cancellationToken);
        lock (_pageGate)
        {
            if (generation == _pageGeneration)
                _pageCache[page] = batch;
        }

        return batch;
    }

    private async Task<WebPageBatch> FetchWebPageAsync(
        string site,
        IScraperProvider? scraper,
        IReadOnlyList<string> tags,
        string extra,
        int page,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (scraper is IScraperProvider direct && direct is HentaiFoxScraperProvider or ITagCatalogSource)
        {
            var siteId = direct switch
            {
                ITagCatalogSource catalog => catalog.SiteId,
                _ => HentaiFoxScraperProvider.SiteId,
            };
            string query;
            if (direct is SimplyHentaiProvider or HentaiRunProvider or HentaiClapProvider or HentaiEnvyProvider)
            {
                var parts = new List<string>(tags);
                if (extra.Length > 0)
                    parts.Add(extra);
                query = string.Join('\u001f', parts);
            }
            else
            {
                query = string.Join(' ', tags);
                if (extra.Length > 0)
                    query = (query + " " + extra).Trim();
            }

            var hits = new List<OnlineSearchResult>();
            foreach (var item in await direct.SearchAsync(query, page, settings, cancellationToken).ConfigureAwait(false))
            {
                hits.Add(new OnlineSearchResult(
                    item.Title,
                    item.CoverUrl,
                    item.GalleryUrl,
                    item.Tags,
                    siteId,
                    item.GalleryId,
                    item.PageCount,
                    item.Tags.Select(tag => (tag, 1)).ToList()));
            }

            var hasMore = direct is ISearchPageHint hint && hint.HasFurtherPages && page < GalleryPages.SafetyCap;
            CapResults(hits, ref hasMore);
            var diagnostic = direct is SimplyHentaiProvider simply ? simply.SearchDiagnostic : null;
            var blocked = direct is SimplyHentaiProvider blockedSite ? blockedSite.BlockStatus : null;
            return new WebPageBatch(hits, hasMore, diagnostic, blocked, false, null, null);
        }

        var apiHits = new List<OnlineSearchResult>();
        await foreach (var hit in _api.SearchStreamAsync(site, tags, extra.Length > 0 ? extra : null, page, cancellationToken).ConfigureAwait(false))
            apiHits.Add(hit);
        var apiHasMore = _api.LastHasMore && page < GalleryPages.SafetyCap;
        CapResults(apiHits, ref apiHasMore);
        return new WebPageBatch(
            apiHits,
            apiHasMore,
            null,
            null,
            true,
            _api.LastTotal,
            _api.LastTotalPages);
    }

    private static void CapResults(List<OnlineSearchResult> hits, ref bool hasMore)
    {
        if (hits.Count <= WebPageLimit)
            return;
        hits.RemoveRange(WebPageLimit, hits.Count - WebPageLimit);
        hasMore = true;
    }

    private static bool PageHasNewHits(IEnumerable<WebSearchTile> previous, WebPageBatch batch)
    {
        if (batch.Hits.Count == 0)
            return false;
        var known = new HashSet<string>(previous.Select(tile => tile.Url), StringComparer.Ordinal);
        foreach (var hit in batch.Hits)
        {
            if (!known.Contains(hit.GalleryUrl))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Platzhalter und Kachel-Grid schließen sich aus: Treffer blenden den Globus aus,
    /// keine Treffer blenden das Kachel-Grid aus.
    /// </summary>
    private void ApplyWebDiscoveryVisibility()
    {
        if (_webHits.Count > 0)
        {
            WebDiscoveryPanel.Visibility = Visibility.Collapsed;
            SearchResultsControl.Visibility = Visibility.Visible;
            return;
        }

        SearchResultsControl.Visibility = Visibility.Collapsed;
        WebDiscoveryPanel.Visibility = _webSearching ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetWebSearchBusy(bool busy, string? text, bool overlay = true)
    {
        WebSearchLoading.Visibility = busy && overlay ? Visibility.Visible : Visibility.Collapsed;
        WebSearchCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        WebSearchButton.IsEnabled = !busy && (_webSearchModel?.CanSearch ?? true);
        WebSearchNextButton.IsEnabled = !busy && _webHasMore;
        WebSearchPrevButton.IsEnabled = !busy && _webPage > 1;
        if (busy && text is not null)
            WebSearchLoadingText.Text = text;
        ApplyWebDiscoveryVisibility();
    }

    private void UpdateWebSearchCount()
    {
        if (_webHits.Count == 0)
        {
            WebSearchCountText.Visibility = Visibility.Collapsed;
            WebSearchPager.Visibility = Visibility.Collapsed;
            return;
        }

        var end = _webRangeStart + _webHits.Count - 1;
        var total = _webApiTotals ? _webTotal : null;
        WebSearchCountText.Text = total is int count
            ? count.ToString("N0") + " Ergebnisse gefunden – Zeige " + _webRangeStart.ToString("N0") + "–" + end.ToString("N0")
            : _webHits.Count.ToString("N0") + " Ergebnisse – Zeige " + _webRangeStart.ToString("N0") + "–" + end.ToString("N0");
        WebSearchPageText.Text = _webApiTotals && _webTotalPages is int pages
            ? "Seite " + _webPage + " von " + pages
            : "Seite " + _webPage;
        WebSearchCountText.Visibility = Visibility.Visible;
        WebSearchPager.Visibility = Visibility.Visible;
        WebSearchPrevButton.IsEnabled = _webPage > 1;
        WebSearchNextButton.IsEnabled = _webHasMore;
    }

    private async void WebSearchRead_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is WebSearchTile tile)
            await ReadWebHitAsync(tile);
    }

    private void WebSearchDownload_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not WebSearchTile tile)
            return;

        QueueGalleryDownload(tile.Url, tile.Title, tile.Tags, skipSourceFilter: true);
        SetStatus("Download eingereiht: " + Truncate(tile.Title, 70), StatusKind.Info);
        AppendSessionLog("↓  " + tile.Title + "  ·  " + tile.Url);
    }

    private async void WebSearchTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1)
            return;
        if (e.OriginalSource is DependencyObject source && FindVisualParent<Button>(source) is not null)
            return;
        if ((sender as FrameworkElement)?.DataContext is WebSearchTile tile)
            await ReadWebHitAsync(tile);
    }

    private async void WebSearchGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SearchResultsControl.SelectedItem is WebSearchTile tile)
        {
            e.Handled = true;
            await ReadWebHitAsync(tile);
        }
    }

    private async Task ReadWebHitAsync(WebSearchTile tile) =>
        await ReadOnlineAsync(tile.Url, tile.Title, logToDownloads: true, skipSourceFilter: true);

    // ── Warteschlange bedienen ──

    private void CancelJob_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadJob job)
            CancelJob(job);
    }

    private void CancelAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var job in _jobs.Where(j => j.CanCancel).ToList())
            CancelJob(job);
    }

    private void CancelJob(DownloadJob job)
    {
        switch (job.Status)
        {
            case JobStatus.Queued:
                job.Cts.Cancel();
                job.Message = LanguageManager.Instance.Get("Download_Cancelled");
                job.Status = JobStatus.Cancelled;
                break;
            case JobStatus.Running:
                job.Message = LanguageManager.Instance.Get("Download_CancelPending");
                job.Cts.Cancel(); // beendet den Prozess; FinishJobAsync setzt den Endstatus
                break;
        }

        UpdateHud();
    }

    private void RetryJob_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadJob { CanRetry: true } job)
            return;

        var retry = new DownloadJob(job.Url, job.CustomTitle, job.CustomTags) { SkipSourceFilter = job.SkipSourceFilter };
        _jobs.Insert(0, retry);
        JobList.SelectedItem = retry;
        SetStatus(LanguageManager.Instance.Get("Download_Enqueued"), StatusKind.Info);
        PumpQueue();
    }

    private void RemoveJob_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadJob { CanRemove: true } job)
        {
            _jobs.Remove(job);
            DisposeJobToken(job);
        }
    }

    private void ClearFinished_Click(object sender, RoutedEventArgs e)
    {
        foreach (var job in _jobs.Where(j => j.CanRemove).ToList())
        {
            _jobs.Remove(job);
            DisposeJobToken(job);
        }
        UpdateHud();
    }

    private static void DisposeJobToken(DownloadJob job)
    {
        try { job.Cts.Dispose(); }
        catch (ObjectDisposedException) { }
    }

    private void JobList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _logJob = JobList.SelectedItem as DownloadJob;
        LogTitleText.Text = _logJob is null ? _sessionLogTitle : "LOG  //  " + Truncate(_logJob.DisplayTitle, 70);
        FlushLog(forceScroll: true);
    }

    private void MarkLogDirty(DownloadJob job)
    {
        if (job == _logJob)
            _logDirty = true;
    }

    private void FlushLog(bool forceScroll = false)
    {
        _logDirty = false;

        var atEnd = forceScroll || LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 4;

        if (_logJob is null)
        {
            // Kein Download markiert: Protokoll des letzten „Direkt online lesen“ (oder leer).
            LogBox.Text = _sessionLog.ToString();
            if (atEnd)
                LogBox.ScrollToEnd();
            return;
        }

        // Nur mitscrollen, wenn der Nutzer am Ende war – sonst bleibt seine Leseposition erhalten.
        LogBox.Text = _logJob.GetLogText();
        if (atEnd)
            LogBox.ScrollToEnd();
    }

    /// <summary>Schreibt den Ablauf von „Direkt online lesen“ ins Log-Fenster (kein Download-Job, daher eigenes Protokoll).</summary>
    private void LogOnlineRead(string title, params string[] lines)
    {
        JobList.SelectedItem = null;
        _logJob = null;
        _sessionLog.Clear();
        _sessionLogTitle = "LOG  //  " + Truncate(title, 70);
        LogTitleText.Text = _sessionLogTitle;
        foreach (var line in lines)
            _sessionLog.AppendLine(line);
        FlushLog(forceScroll: true);
    }

    private void AppendSessionLog(string line)
    {
        if (_sessionLog.Length > 0 && _sessionLog[^1] != '\n')
            _sessionLog.AppendLine();
        _sessionLog.AppendLine(line);
        if (_logJob is null)
        {
            LogTitleText.Text = _sessionLogTitle;
            FlushLog(forceScroll: true);
        }
    }

    private static string FlattenLogLine(string message)
    {
        return message.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Trim();
    }

    /// <summary>Aktualisiert die HUD-Leiste unten und die Zähler in der Navigation.</summary>
    private void UpdateHud()
    {
        if (!_ready)
            return;

        var running = _jobs.Count(j => j.Status == JobStatus.Running);
        var queued = _jobs.Count(j => j.Status == JobStatus.Queued);
        var files = _jobs.Where(j => j.Status == JobStatus.Running).Sum(j => j.TotalFiles);
        var active = running + queued > 0;

        DownloadHud.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        NavDownloadsCount.Text = active ? (running + queued).ToString() : string.Empty;

        if (active)
        {
            DownloadHudText.Text = $"{running} aktiv" + (queued > 0 ? $" · {queued} wartend" : string.Empty) +
                                   (files > 0 ? $" · {files} Dateien" : string.Empty);
        }

        if (_page == AppPage.Downloads)
            UpdateSubtitle();
    }

    // ════════════════════════════════════════════════════════════════════
    //  Einstellungen
    // ════════════════════════════════════════════════════════════════════

    private void BrowseGalleryDl_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "gallery-dl auswählen",
            Filter = "gallery-dl (gallery-dl*.exe)|gallery-dl*.exe|Programme (*.exe)|*.exe|Alle Dateien|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
            GalleryDlPathBox.Text = dialog.FileName;
    }

    private async void CheckGalleryDl_Click(object sender, RoutedEventArgs e)
    {
        // Prüft den Wert aus dem Eingabefeld, ohne ihn schon zu speichern.
        var previous = _galleryDlPath;
        _galleryDlPath = GalleryDlPathBox.Text;
        try
        {
            await CheckGalleryDlAsync(silent: false);
        }
        finally
        {
            _galleryDlPath = previous;
            _galleryDl = null; // erst „Speichern“ übernimmt den Pfad endgültig
        }
    }

    private void BrowseLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Download-Ordner der Bibliothek wählen",
            InitialDirectory = Directory.Exists(LibraryPathBox.Text) ? LibraryPathBox.Text : null,
        };

        if (dialog.ShowDialog(this) == true)
            LibraryPathBox.Text = dialog.FolderName;
    }

    private void OpenLibraryFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Path.GetFullPath(string.IsNullOrWhiteSpace(LibraryPathBox.Text) ? DefaultLibraryPath : LibraryPathBox.Text.Trim());
            Directory.CreateDirectory(folder);
            OpenWithShell(folder);
        }
        catch (Exception ex)
        {
            SetStatus($"Ordner konnte nicht geöffnet werden: {ex.Message}", StatusKind.Error);
        }
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        string library;
        try
        {
            var typed = LibraryPathBox.Text.Trim().Trim('"');
            library = Path.GetFullPath(typed.Length == 0 ? DefaultLibraryPath : typed);
        }
        catch (Exception)
        {
            SetStatus("Der Download-Ordner ist kein gültiger Pfad.", StatusKind.Error);
            return;
        }

        _libraryPath = library;
        _galleryDlPath = GalleryDlPathBox.Text.Trim().Trim('"');
        _extraArgs = ExtraArgsBox.Text.Trim();
        _flareSolverrUrl = FlareSolverrBox.Text.Trim();
        _settings.LibraryPath = library;
        _settings.GalleryDlPath = _galleryDlPath;
        _settings.ExtraArgs = _extraArgs;
        _settings.FlareSolverrUrl = _flareSolverrUrl;
        _settings.EHentaiIpbMemberId = EhMemberIdBox.Text.Trim();
        _settings.EHentaiIpbPassHash = EhPassHashBox.Text.Trim();
        _settings.EHentaiCfClearance = EhClearanceBox.Text.Trim();
        _settings.PreferredBrowser = PreferredBrowserBox.SelectedItem is ComboBoxItem { Tag: string browser } && browser.Length > 0
            ? browser
            : "auto";
        _settings.UiLanguage = LanguageBox.SelectedItem is ComboBoxItem { Tag: string language } && language.Length > 0
            ? language
            : "auto";
        _settings.UseGlobalTagFallback = HitomiTagBankToggle.IsChecked == true;
        GalleryDl.PreferredBrowser = _settings.PreferredBrowser;
        SaveEhSessionFields();
        _maxParallel = ParallelBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var parallel)
            ? Math.Clamp(parallel, 1, 4)
            : 2;
        _settings.MaxParallel = _maxParallel;
        _galleryDl = null;

        LibraryPathBox.Text = _libraryPath;
        GalleryDlPathBox.Text = _galleryDlPath;

        try
        {
            var snapshot = _settings;
            await Task.Run(() => snapshot.Save(_db));
        }
        catch (Exception ex)
        {
            SetStatus($"Einstellungen konnten nicht gespeichert werden: {ex.Message}", StatusKind.Error);
            return;
        }

        SetStatus("Einstellungen gespeichert.", StatusKind.Success);
        ApplySessionCookiesToApi();
        await CheckGalleryDlAsync(silent: false);
        PumpQueue(); // ein höheres Limit kann wartende Jobs freigeben
    }

    private void FillCookieDomainBox()
    {
        _cookieUiReady = false;
        CookieDomainBox.Items.Clear();
        foreach (var site in SourceCatalog.Sites)
        {
            CookieDomainBox.Items.Add(new ComboBoxItem
            {
                Content = site.DisplayName + "  (" + site.Domains[0] + ")",
                Tag = site.Domains[0],
            });
        }

        CookieDomainBox.SelectedIndex = 0;
        _cookieUiReady = true;
        RefreshCookieBox();
    }

    private string SelectedCookieDomain()
    {
        if (CookieDomainBox.SelectedItem is ComboBoxItem { Tag: string domain } && domain.Length > 0)
            return domain;
        return SourceCatalog.Find(_webSource)?.Domains[0] ?? "nhentai.net";
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_languageReady || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string language } || language.Length == 0)
            return;

        _settings.UiLanguage = language;
        LanguageManager.Instance.Apply(language);
        try
        {
            _db.SetSetting(nameof(AppSettings.UiLanguage), language);
        }
        catch (Exception ex)
        {
            App.Log("Sprache: " + App.Flatten(ex));
        }
    }

    private void CookieDomainBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_cookieUiReady)
            RefreshCookieBox();
    }

    private void RefreshCookieBox()
    {
        if (CookieDomainBox is null || SessionCookieBox is null)
            return;

        var domain = SelectedCookieDomain();
        var cookies = _db.GetHttpCookies(domain);
        SessionCookieBox.Text = HttpCookieImport.ToHeader(cookies);
        CookieStatusText.Text = cookies.Count == 0
            ? "Keine Cookies für " + domain + "."
            : cookies.Count + " Cookies für " + domain + " gespeichert (" +
              string.Join(", ", cookies.Select(c => c.Name).Take(6)) +
              (cookies.Count > 6 ? " …" : string.Empty) + ").";
    }

    private void SaveCookies_Click(object sender, RoutedEventArgs e)
    {
        var domain = SelectedCookieDomain();
        var parsed = HttpCookieImport.Parse(SessionCookieBox.Text, domain);
        try
        {
            _db.ReplaceHttpCookiesForDomain(domain, parsed);
            ApplySessionCookiesToApi();
            RefreshCookieBox();
            LoadEhSessionFields();
            SetStatus(parsed.Count == 0
                ? "Cookies für " + domain + " gelöscht."
                : parsed.Count + " Cookies für " + domain + " gespeichert.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus("Cookies konnten nicht gespeichert werden: " + ex.Message, StatusKind.Error);
        }
    }

    private void ClearCookies_Click(object sender, RoutedEventArgs e)
    {
        var domain = SelectedCookieDomain();
        try
        {
            _db.ClearHttpCookies(domain);
            SessionCookieBox.Clear();
            ApplySessionCookiesToApi();
            RefreshCookieBox();
            SetStatus("Cookies für " + domain + " entfernt.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus("Cookies konnten nicht gelöscht werden: " + ex.Message, StatusKind.Error);
        }
    }

    private void LoadEhSessionFields()
    {
        if (EhMemberIdBox is null)
            return;

        var stored = _db.GetHttpCookies("e-hentai.org");
        _settings.EHentaiIpbMemberId = CookieValue(stored, "ipb_member_id") ?? _settings.EHentaiIpbMemberId;
        _settings.EHentaiIpbPassHash = CookieValue(stored, "ipb_pass_hash") ?? _settings.EHentaiIpbPassHash;
        _settings.EHentaiCfClearance = CookieValue(stored, "cf_clearance") ?? _settings.EHentaiCfClearance;
        EhMemberIdBox.Text = _settings.EHentaiIpbMemberId;
        EhPassHashBox.Text = _settings.EHentaiIpbPassHash;
        EhClearanceBox.Text = _settings.EHentaiCfClearance;
    }

    private static string? CookieValue(IReadOnlyList<StoredHttpCookie> cookies, string name) =>
        cookies.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private void SaveEhSessionFields()
    {
        var member = _settings.EHentaiIpbMemberId;
        var pass = _settings.EHentaiIpbPassHash;
        var clearance = _settings.EHentaiCfClearance;
        foreach (var domain in new[] { "e-hentai.org", "exhentai.org" })
        {
            StoreEhCookie(domain, "ipb_member_id", member);
            StoreEhCookie(domain, "ipb_pass_hash", pass);
            StoreEhCookie(domain, "cf_clearance", clearance);
        }
    }

    private void StoreEhCookie(string domain, string name, string value)
    {
        if (value.Length == 0)
            _db.DeleteHttpCookie(domain, name);
        else
            _db.UpsertHttpCookies(new[] { new StoredHttpCookie(domain, name, value, "/") });
    }

    private void OpenCookieSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(AppPage.Settings);
        EhMemberIdBox?.Focus();
    }

    private static string JoinArguments(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
            return right;
        if (string.IsNullOrWhiteSpace(right))
            return left;
        return left + " " + right;
    }

    private static bool IsBrowserCookieLock(string message) =>
        message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
        || message.Contains(GalleryDl.BrowserCookieLockedMessage, StringComparison.Ordinal);

    private static bool NeedsCookieSetup(string message) =>
        message.Contains("403", StringComparison.Ordinal)
        || message.Contains("Code 2", StringComparison.OrdinalIgnoreCase)
        || message.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase);

    private string CookieArgumentsFor(string? url)
    {
        if (_api is null || string.IsNullOrWhiteSpace(url))
            return string.Empty;

        var site = SourceCatalog.FromUrl(url);
        var domain = SourceCatalog.Find(site)?.Domains[0];
        if (string.IsNullOrEmpty(domain))
            return string.Empty;

        ApplySessionCookiesToApi();
        var (file, named) = _api.ExportSessionCookies(domain);
        var info = new System.Diagnostics.ProcessStartInfo();
        GalleryDl.AddCookieArguments(info, file, named);
        return string.Join(' ', info.ArgumentList.Select(argument =>
            argument.Any(char.IsWhiteSpace) ? "\"" + argument + "\"" : argument));
    }

    private void ApplySessionCookiesToApi()
    {
        if (_api is null)
            return;
        try
        {
            _api.LoadCookies(_db.GetHttpCookies());
            _api.FlareSolverrUrl = string.IsNullOrWhiteSpace(_flareSolverrUrl) ? null : _flareSolverrUrl;
            GalleryDl.PreferredBrowser = string.IsNullOrWhiteSpace(_settings.PreferredBrowser) ? "auto" : _settings.PreferredBrowser;
        }
        catch (Exception ex)
        {
            AppendSessionLog("! Cookies konnten nicht geladen werden: " + FlattenLogLine(ex.Message));
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
                return match;
            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Hilfsfunktionen
    // ════════════════════════════════════════════════════════════════════

    private void SetStatus(string message, StatusKind kind = StatusKind.Info)
    {
        StatusText.Text = message;
        StatusDot.Fill = (Brush)FindResource(kind switch
        {
            StatusKind.Success => "SuccessBrush",
            StatusKind.Warning => "WarnBrush",
            StatusKind.Error => "DangerBrush",
            _ => "AccentBrush",
        });
    }

    /// <summary>Zeigt eine Datei im Explorer an (im Ordner markiert).</summary>
    private void RevealInExplorer(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", file }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            SetStatus($"Konnte nicht geöffnet werden: {ex.Message}", StatusKind.Error);
        }
    }

    /// <summary>Öffnet einen Ordner im Explorer, eine Datei im Standardprogramm bzw. eine URL im Standardbrowser.</summary>
    private void OpenWithShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Konnte nicht geöffnet werden: {ex.Message}", StatusKind.Error);
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}

// ════════════════════════════════════════════════════════════════════════
//  Oberflächen-Modelle (Bindings)
// ════════════════════════════════════════════════════════════════════════

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>Kachel im Raster. Das Cover wird erst bei Bedarf und im Hintergrund geladen.</summary>
public sealed class MangaTile : ObservableObject
{
    private ImageSource? _cover;
    private bool _coverRequested;
    private bool _isFavorite;

    public MangaTile(Manga manga)
    {
        Manga = manga;
        _isFavorite = manga.IsFavorite;
    }

    public Manga Manga { get; }
    public long Id => Manga.Id;
    public string Title => Manga.Title;

    public string Meta
    {
        get
        {
            var parts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(Manga.Author))
                parts.Add(Manga.Author);
            if (Manga.PageCount > 0)
                parts.Add(Manga.PageCount == 1 ? "1 Seite" : $"{Manga.PageCount} Seiten");
            return parts.Count > 0 ? string.Join(" · ", parts) : "—";
        }
    }

    public bool IsNew => DateTime.UtcNow - Manga.DateAdded < TimeSpan.FromDays(2);

    public string ToolTipText
    {
        get
        {
            var text = new StringBuilder(Manga.Title);
            if (!string.IsNullOrWhiteSpace(Manga.Author))
                text.AppendLine().Append("von ").Append(Manga.Author);
            if (Manga.Tags.Count > 0)
            {
                text.AppendLine().AppendLine().Append(string.Join(", ", Manga.Tags.Take(14)));
                if (Manga.Tags.Count > 14)
                    text.Append(" …");
            }

            if (!string.IsNullOrWhiteSpace(Manga.Description))
            {
                var description = Manga.Description.Trim();
                text.AppendLine().AppendLine().Append(description.Length > 220 ? description[..220].TrimEnd() + " …" : description);
            }

            return text.ToString();
        }
    }

    /// <summary>Wird ausgelöst, wenn der Favoriten-Status geändert wurde (nicht bei <see cref="SetFavoriteSilently"/>).</summary>
    public event Action<MangaTile>? FavoriteChanged;

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (!SetField(ref _isFavorite, value))
                return;

            Manga.IsFavorite = value;
            FavoriteChanged?.Invoke(this);
        }
    }

    /// <summary>Setzt den Status, ohne <see cref="FavoriteChanged"/> auszulösen (z. B. um einen Speicherfehler rückgängig zu machen).</summary>
    public void SetFavoriteSilently(bool value)
    {
        if (SetField(ref _isFavorite, value, nameof(IsFavorite)))
            Manga.IsFavorite = value;
    }

    public ImageSource? Cover
    {
        get
        {
            if (_cover is not null)
                return _cover;
            if (ImageCacheService.TryGet(Manga.CoverPath, out var cached))
            {
                _cover = cached;
                return cached;
            }

            if (!_coverRequested)
            {
                _coverRequested = true;
                _ = LoadCoverAsync();
            }

            return _cover;
        }
    }

    private async Task LoadCoverAsync()
    {
        var coverPath = Manga.CoverPath;
        var folder = Manga.FolderPath;

        try
        {
            var path = coverPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                path = string.IsNullOrEmpty(folder) ? null : await Task.Run(() => LibraryScanner.FindFirstImage(folder));
            if (path is null)
                return;

            var bitmap = await ImageCacheService.GetFileAsync(path);
            if (bitmap is null)
                return;

            _cover = bitmap;
            OnPropertyChanged(nameof(Cover));
        }
        catch
        {
            // Ein fehlendes Cover ist kein Fehler – die Kachel zeigt dann den Platzhalter.
        }
    }
}

/// <summary>Eine bereits geladene Suchseite, inklusive der vorgeladenen Folgeseite.</summary>
internal sealed class WebPageBatch
{
    public WebPageBatch(
        List<OnlineSearchResult> hits,
        bool hasMore,
        string? diagnostic,
        string? blockStatus,
        bool fromApi,
        int? total,
        int? totalPages)
    {
        Hits = hits;
        HasMore = hasMore;
        Diagnostic = diagnostic;
        BlockStatus = blockStatus;
        FromApi = fromApi;
        Total = total;
        TotalPages = totalPages;
    }

    public List<OnlineSearchResult> Hits { get; }
    public bool HasMore { get; }
    public string? Diagnostic { get; }
    public string? BlockStatus { get; }
    public bool FromApi { get; }
    public int? Total { get; }
    public int? TotalPages { get; }
}

/// <summary>Kachel eines Online-Suchtreffers. Das Cover wird per HTTP im Hintergrund geladen.</summary>
internal sealed class WebSearchTile : ObservableObject
{
    private ImageSource? _cover;
    private bool _coverRequested;

    public WebSearchTile(OnlineSearchResult hit)
    {
        Title = hit.Title;
        Url = hit.GalleryUrl;
        Source = hit.SourceSite;
        GalleryId = hit.GalleryId;
        CoverUrl = hit.CoverUrl;
        Tags = hit.Tags;
        PageCount = hit.PageCount;
        SourceName = SourceCatalog.DisplayName(hit.SourceSite);
        Meta = (hit.GalleryId is { Length: > 0 } id ? "#" + id + "  ·  " : string.Empty)
               + SourceName
               + (hit.PageCount is > 0 ? "  ·  " + hit.PageCount + " S." : string.Empty);
        TagsText = hit.Tags.Count == 0 ? string.Empty : string.Join("  ·  ", hit.Tags.Take(8));
        ToolTipText = hit.Title + "\n" + hit.GalleryUrl + (TagsText.Length == 0 ? string.Empty : "\n" + TagsText);
    }

    public string Title { get; }
    public string Url { get; }
    public string Source { get; }
    public string SourceName { get; }
    public string? GalleryId { get; }
    public string? CoverUrl { get; }
    public IReadOnlyList<string> Tags { get; }
    public int? PageCount { get; }
    public string Meta { get; }
    public string TagsText { get; }
    public string ToolTipText { get; }

    public ImageSource? Cover
    {
        get
        {
            if (_cover is not null)
                return _cover;
            if (ImageCacheService.TryGet(CoverUrl, out var cached))
            {
                _cover = cached;
                return cached;
            }

            if (!_coverRequested)
            {
                _coverRequested = true;
                _ = LoadCoverAsync();
            }

            return _cover;
        }
    }

    private async Task LoadCoverAsync()
    {
        if (string.IsNullOrWhiteSpace(CoverUrl))
            return;

        try
        {
            var bitmap = await ImageCacheService.GetRemoteAsync(CoverUrl, Source);
            if (bitmap is null)
                return;
            _cover = bitmap;
            OnPropertyChanged(nameof(Cover));
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Cover ist optional
        }
    }
}

/// <summary>Ein gewählter Tag-Filter unter der Suchleiste. Alle gewählten Tags müssen zutreffen (UND); ausgeschlossene dürfen nicht vorkommen.</summary>
public sealed class ActiveTag : ObservableObject
{
    private bool _exclude;

    public ActiveTag(string tag, bool exclude)
    {
        Tag = tag;
        _exclude = exclude;
        Category = TagNames.Split(tag).Category;
    }

    /// <summary>Textform wie „artist:foo bar“.</summary>
    public string Tag { get; }

    public string Category { get; }

    /// <summary>Wird ausgelöst, wenn der Tag zwischen einschließen und ausschließen wechselt.</summary>
    public event Action? Changed;

    public bool Exclude
    {
        get => _exclude;
        set
        {
            if (!SetField(ref _exclude, value))
                return;

            Changed?.Invoke();
        }
    }
}

/// <summary>Eine Quelle (Website) in der Filterleiste über dem Raster.</summary>
public sealed class SourceFilter : ObservableObject
{
    private bool _isSelected = true;
    private int _count;

    public SourceFilter(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }

    /// <summary>Wird ausgelöst, wenn der Nutzer die Quelle an- oder abwählt (nicht bei <see cref="SetSelectedSilently"/>).</summary>
    public event Action<SourceFilter>? SelectionChanged;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetField(ref _isSelected, value))
                return;

            SelectionChanged?.Invoke(this);
        }
    }

    /// <summary>Setzt die Auswahl, ohne <see cref="SelectionChanged"/> auszulösen (Sammel-Aktionen laden danach einmal neu).</summary>
    public void SetSelectedSilently(bool value) => SetField(ref _isSelected, value, nameof(IsSelected));

    /// <summary>Anzahl der Werke in der Bibliothek.</summary>
    public int Count
    {
        get => _count;
        set
        {
            if (SetField(ref _count, value))
                OnPropertyChanged(nameof(CountText));
        }
    }

    public string CountText => _count > 0 ? _count.ToString() : string.Empty;
}

/// <summary>Ein Eintrag der Vorschlagsliste in der Suchleiste.</summary>
/// <param name="Raw">Das getippte Wort im Suchfeld, das durch den gewählten Tag ersetzt wird.</param>
public sealed record TagSuggestion(string Tag, string Category, string Name, int Count, bool Exclude, string Raw)
{
    public TagSuggestion(TagInfo info, bool exclude, string raw)
        : this(info.Name, info.Category, info.Name[(info.Category.Length == 0 ? 0 : info.Category.Length + 1)..], info.MangaCount, exclude, raw)
    {
    }

    /// <summary>Kurzer Text statt der Record-Standardausgabe – den lesen Screenreader und die UI-Automation vor.</summary>
    public override string ToString() => Tag;
}

public sealed class TagChip : ObservableObject
{
    private bool _isIncluded;
    private bool _isExcluded;

    public TagChip(string name, int count)
    {
        Name = name;
        Count = count;
        Category = TagNames.Split(name).Category;
    }

    public string Name { get; }
    public int Count { get; }
    public int MangaCount => Count;

    /// <summary>Kategorie des Tags („artist“, „female“ …), leer bei gewöhnlichen Tags – bestimmt die Farbe des Chips.</summary>
    public string Category { get; }

    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetField(ref _isIncluded, value);
    }

    public bool IsExcluded
    {
        get => _isExcluded;
        set => SetField(ref _isExcluded, value);
    }
}

/// <summary>Auf- und zuklappbare Tag-Gruppe (Artist, Parody, Character, Tag/Genre, Language, …).</summary>
public sealed class TagGroup : ObservableObject
{
    private bool _isExpanded;
    private int _count;
    private ObservableCollection<TagChip> _tags;

    public TagGroup(string category, IEnumerable<TagChip> tags, bool expanded)
    {
        Category = category ?? string.Empty;
        Title = TagCategoryUi.Title(Category);
        var list = tags as IList<TagChip> ?? tags.ToList();
        _tags = new ObservableCollection<TagChip>(list);
        _count = list.Count;
        _isExpanded = expanded;
    }

    public string Category { get; }
    public string Title { get; }
    public ObservableCollection<TagChip> Tags
    {
        get => _tags;
        private set => SetField(ref _tags, value);
    }

    public int Count => _count;
    public string Header => Title + "  ·  " + Count;
    public bool TagsLoaded { get; private set; }
    public bool IsLoading { get; set; }

    public event Action<TagGroup>? ExpandRequested;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetField(ref _isExpanded, value))
                return;
            if (value)
                ExpandRequested?.Invoke(this);
        }
    }

    public void SetCount(int count)
    {
        if (_count == count)
            return;
        _count = count;
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(Header));
    }

    /// <summary>Ersetzt die Tag-Liste in einem Schritt, damit nicht jeder Chip ein eigenes UI-Ereignis auslöst.</summary>
    public void Fill(IReadOnlyList<TagChip> chips)
    {
        Tags = new ObservableCollection<TagChip>(chips);
        SetCount(chips.Count);
        TagsLoaded = true;
        IsLoading = false;
    }

    public static IReadOnlyList<TagGroup> FromChips(IEnumerable<TagChip> chips, bool expandAll = false) =>
        chips
            .GroupBy(c => c.Category ?? string.Empty, StringComparer.Ordinal)
            .OrderBy(g => TagCategoryUi.SortKey(g.Key))
            .ThenBy(g => TagCategoryUi.Title(g.Key), StringComparer.OrdinalIgnoreCase)
            .Select(g => new TagGroup(
                g.Key,
                g.OrderByDescending(c => c.Count).ThenBy(c => c.Name, StringComparer.Ordinal),
                expandAll || TagCategoryUi.DefaultExpanded(g.Key)))
            .ToList();
}

public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Ein Eintrag der Download-Warteschlange samt Protokoll.</summary>
public sealed class DownloadJob : ObservableObject
{
    private const int MaxLogLines = 3000;
    private static int _nextId;

    private readonly List<string> _log = new();
    private JobStatus _status = JobStatus.Queued;
    private int _fileCount;
    private int _skippedCount;
    private string? _lastFile;
    private string? _message;
    private string? _resultTitle;

    public DownloadJob(string url, string? customTitle, IReadOnlyList<string> customTags)
    {
        Id = Interlocked.Increment(ref _nextId);
        Url = url;
        Source = SourceCatalog.FromUrl(url);
        CustomTitle = string.IsNullOrWhiteSpace(customTitle) ? null : customTitle.Trim();
        CustomTags = customTags;
        LanguageManager.Instance.Changed += () =>
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ErrorBadge));
        };
    }

    public int Id { get; }
    public string Url { get; }

    /// <summary>Website der Adresse (Kennung aus <see cref="SourceCatalog"/>).</summary>
    public string Source { get; }

    public string SourceName => SourceCatalog.DisplayName(Source);
    public string? CustomTitle { get; }
    public IReadOnlyList<string> CustomTags { get; }

    /// <summary>Websuche-Downloads umgehen die Quellen-Leiste – der Nutzer hat den Treffer ausdrücklich gewählt.</summary>
    public bool SkipSourceFilter { get; init; }

    /// <summary>Abbruch-Token; beim Abbrechen wird der gallery-dl-Prozess beendet.</summary>
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>Alle von gallery-dl gemeldeten Dateien (neu geladene und übersprungene).</summary>
    public List<string> Files { get; } = new();

    public string? LastError { get; set; }

    public JobStatus Status
    {
        get => _status;
        set
        {
            if (!SetField(ref _status, value))
                return;

            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanRemove));
            OnPropertyChanged(nameof(CanRetry));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(HasDeterminateProgress));
            OnPropertyChanged(nameof(ErrorBadge));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsRunning => Status == JobStatus.Running;
    public bool CanCancel => Status is JobStatus.Queued or JobStatus.Running;
    public bool CanRemove => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;
    public bool CanRetry => Status == JobStatus.Failed;
    public bool HasError => Status == JobStatus.Failed || _earlyFault;
    public bool HasDeterminateProgress => Status == JobStatus.Running && ProgressTotal > 0;
    public string ErrorBadge => HasError ? LanguageManager.Instance.Get("Download_FailedBadge") : string.Empty;

    private int _progressCurrent;
    private int _progressTotal;
    private double _progressPercent;
    private string _speedText = string.Empty;

    public int ProgressCurrent
    {
        get => _progressCurrent;
        private set => SetField(ref _progressCurrent, value);
    }

    public int ProgressTotal
    {
        get => _progressTotal;
        private set => SetField(ref _progressTotal, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetField(ref _progressPercent, value);
    }

    public string SpeedText
    {
        get => _speedText;
        private set => SetField(ref _speedText, value);
    }

    public int FileCount
    {
        get => _fileCount;
        private set => SetField(ref _fileCount, value);
    }

    public int SkippedCount
    {
        get => _skippedCount;
        private set => SetField(ref _skippedCount, value);
    }

    public int TotalFiles => FileCount + SkippedCount;

    public string? LastFile
    {
        get => _lastFile;
        private set => SetField(ref _lastFile, value);
    }

    /// <summary>Freitext zum aktuellen Zustand (z. B. Fehlermeldung); überschreibt die Standardanzeige.</summary>
    public string? Message
    {
        get => _message;
        set
        {
            if (SetField(ref _message, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>Titel des Bibliothekseintrags, sobald der Import abgeschlossen ist.</summary>
    public string? ResultTitle
    {
        get => _resultTitle;
        set
        {
            if (SetField(ref _resultTitle, value))
                OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    public string DisplayTitle => ResultTitle ?? CustomTitle ?? Url;

    public string StatusText
    {
        get
        {
            switch (Status)
            {
                case JobStatus.Queued:
                    return LanguageManager.Instance.Get("Download_Queued");

                case JobStatus.Running:
                    if (ProgressTotal > 0)
                        return LanguageManager.Instance.Get("Download_Progress", ProgressCurrent, ProgressTotal, SpeedSuffix());
                    if (!string.IsNullOrEmpty(Message))
                        return Message;
                    if (TotalFiles == 0)
                        return LanguageManager.Instance.Get("Download_Connecting");

                    var text = LanguageManager.Instance.Get("Download_Loaded", FileCount);
                    if (SkippedCount > 0)
                        text += " · " + LanguageManager.Instance.Get("Download_Skipped", SkippedCount);
                    return LastFile is null ? text : $"{text} · {LastFile}";

                default:
                    return Message ?? Status.ToString();
            }
        }
    }

    public void RegisterFile(string path, bool skipped)
    {
        Files.Add(path);
        if (skipped)
            SkippedCount++;
        else
            FileCount++;

        LastFile = Path.GetFileName(path);
        OnPropertyChanged(nameof(TotalFiles));
        OnPropertyChanged(nameof(StatusText));
    }

    public void SetProgress(int current, int total, string? speed)
    {
        ProgressCurrent = current;
        ProgressTotal = total;
        ProgressPercent = total <= 0 ? 0 : Math.Clamp(current * 100d / total, 0, 100);
        if (!string.IsNullOrWhiteSpace(speed))
            SpeedText = speed;
        OnPropertyChanged(nameof(HasDeterminateProgress));
        OnPropertyChanged(nameof(StatusText));
    }

    public void SetPercent(double percent, string? speed)
    {
        ProgressPercent = Math.Clamp(percent, 0, 100);
        if (ProgressTotal <= 0)
            ProgressTotal = 100;
        ProgressCurrent = (int)Math.Round(ProgressPercent);
        if (!string.IsNullOrWhiteSpace(speed))
            SpeedText = speed;
        OnPropertyChanged(nameof(HasDeterminateProgress));
        OnPropertyChanged(nameof(StatusText));
    }

    public void MarkEarlyFault(string message)
    {
        _earlyFault = true;
        Message = message;
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorBadge));
    }

    private string SpeedSuffix() => string.IsNullOrEmpty(SpeedText) ? string.Empty : " · " + SpeedText;

    private bool _earlyFault;

    public void AppendLog(string line)
    {
        _log.Add(line);
        if (_log.Count > MaxLogLines)
            _log.RemoveRange(0, 500);
    }

    public string GetLogText(int maxLines = 600) =>
        string.Join(Environment.NewLine, _log.Skip(Math.Max(0, _log.Count - maxLines)));
}


