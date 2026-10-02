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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MangaLibraryApp;

// ════════════════════════════════════════════════════════════════════════
//  Hauptfenster: Navigation · Tag-Suche · gallery-dl-Steuerung
// ════════════════════════════════════════════════════════════════════════

public partial class MainWindow : Window
{
    // ── Einstellungs-Schlüssel (Tabelle Settings) ──
    private const string SettingLibraryPath = "LibraryPath";
    private const string SettingGalleryDlPath = "GalleryDlPath";
    private const string SettingExtraArgs = "ExtraArgs";
    private const string SettingMaxParallel = "MaxParallel";
    private const string SettingSort = "Sort";
    private const string SettingSources = "EnabledSources"; // fehlt = alle Quellen
    private const string SettingFlareSolverr = "FlareSolverrUrl";

    /// <summary>Kacheln werden seitenweise nachgeladen, damit große Bibliotheken flüssig bleiben.</summary>
    private const int TilePageSize = 72;

    private const int MaxTagChips = 150;

    private static readonly Regex AnsiRegex = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private enum AppPage { Library, Favorites, Downloads, WebSearch, Settings }

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
    private bool _favoritesOnly;
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
    private IReadOnlyList<TagInfo> _webCatalog = Array.Empty<TagInfo>();
    private readonly ObservableCollection<TagGroup> _webTagGroups = new();
    private readonly ObservableCollection<WebSearchTile> _webHits = new();
    private string _webSource = "nhentai";
    private int _webPage = 1;
    private int _webRangeStart = 1;
    private int _webPageSize = 25;
    private bool _webScrollLoad;
    private bool _webSearching;
    private List<string> _webSearchTags = new();
    private string _webSearchExtra = string.Empty;
    private CancellationTokenSource? _webSearchCts;
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
            SourceBar.ItemsSource = _sourceFilters;
            SuggestList.ItemsSource = _suggestions;
            WebActiveTagList.ItemsSource = _webTags;
            SearchResultsControl.ItemsSource = _webHits;
            _webHits.CollectionChanged += (_, _) => ApplyWebDiscoveryVisibility();
            WebCatalogGroups.ItemsSource = _webTagGroups;

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
        var library = _db.GetSetting(SettingLibraryPath);
        _libraryPath = string.IsNullOrWhiteSpace(library) ? DefaultLibraryPath : library;
        _galleryDlPath = _db.GetSetting(SettingGalleryDlPath) ?? string.Empty;
        _extraArgs = _db.GetSetting(SettingExtraArgs) ?? string.Empty;
        _maxParallel = int.TryParse(_db.GetSetting(SettingMaxParallel), out var parallel) ? Math.Clamp(parallel, 1, 4) : 2;
        if (Enum.TryParse<MangaSort>(_db.GetSetting(SettingSort), out var sort) && Enum.IsDefined(sort))
            _sort = sort;

        // Gewählte Quellen: fehlt die Einstellung, sind alle angehakt.
        var enabled = _db.GetSetting(SettingSources)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        InitSourceFilters(enabled);
        SourceHintText.Visibility = SourcesRestricted ? Visibility.Visible : Visibility.Collapsed;

        LibraryPathBox.Text = _libraryPath;
        GalleryDlPathBox.Text = _galleryDlPath;
        ExtraArgsBox.Text = _extraArgs;
        DatabasePathText.Text = $"Datenbank: {_db.DatabasePath}";
        SelectComboItem(ParallelBox, _maxParallel.ToString());
        SelectComboItem(SortBox, _sort.ToString());

        _flareSolverrUrl = _db.GetSetting(SettingFlareSolverr) ?? string.Empty;
        if (FlareSolverrBox is not null)
            FlareSolverrBox.Text = _flareSolverrUrl;
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
        var isLibrary = page is AppPage.Library or AppPage.Favorites;
        var isWeb = page == AppPage.WebSearch;

        PageLibrary.Visibility = isLibrary ? Visibility.Visible : Visibility.Collapsed;
        PageDownloads.Visibility = page == AppPage.Downloads ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = page == AppPage.Settings ? Visibility.Visible : Visibility.Collapsed;
        WebSearchView.Visibility = isWeb ? Visibility.Visible : Visibility.Collapsed;
        SortPanel.Visibility = isLibrary ? Visibility.Visible : Visibility.Collapsed;
        LibrarySearchPanel.Visibility = isLibrary ? Visibility.Visible : Visibility.Collapsed;
        SourceBarPanel.Visibility = page is AppPage.Settings or AppPage.WebSearch ? Visibility.Collapsed : Visibility.Visible;
        CloseSuggestions();
        CloseWebSuggestions();

        PageTitleText.Text = page switch
        {
            AppPage.Favorites => "Favoriten",
            AppPage.Downloads => "Downloads",
            AppPage.WebSearch => "Web Search",
            AppPage.Settings => "Einstellungen",
            _ => "Bibliothek",
        };
        BreadcrumbText.Text = "/  " + PageTitleText.Text.ToUpperInvariant();
        PageHeader.Margin = isWeb ? new Thickness(16, 0, 16, 2) : new Thickness(28, 10, 28, 10);
        PageTitleText.FontSize = isWeb ? 15 : 24;
        PageSubtitleText.FontSize = isWeb ? 11 : 12.5;
        PageSubtitleText.Margin = isWeb ? new Thickness(0) : new Thickness(0, 2, 0, 0);
        if (isWeb)
            ApplyWebHeaderBudget();

        if (isLibrary)
        {
            var favorites = page == AppPage.Favorites;
            if (favorites != _favoritesOnly)
            {
                _favoritesOnly = favorites;
                _ = RefreshLibraryAsync();
            }
        }

        if (isWeb)
            _ = RefreshWebPopularTagsAsync();

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
            AppPage.Favorites => HasActiveSearch
                ? $"{_results.Count} Favoriten gefunden"
                : $"{_results.Count} {(_results.Count == 1 ? "Favorit" : "Favoriten")}",
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
        _ = Task.Run(() => _db.SetSetting(SettingSort, sort.ToString()));
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
        var query = new MangaQuery { FavoritesOnly = _favoritesOnly, Sort = _sort, Sources = SelectedSources() };

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
        ActiveTagBar.Visibility = (_page is AppPage.Library or AppPage.Favorites) && _activeTags.Count > 0
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
            ToggleActiveTag(chip.Name, exclude: false);
            await RefreshNowAsync();
        }
    }

    private async void TagChip_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is TagChip chip)
        {
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
        _ = Task.Run(() => _db.SetSetting(SettingSources, value));

        SourceHintText.Visibility = SourcesRestricted ? Visibility.Visible : Visibility.Collapsed;
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
            var results = await Task.Run(() => _db.SearchMangas(query));
            if (version != _searchVersion)
                return; // inzwischen wurde eine neuere Suche gestartet

            _results = results;
            _tiles.Clear();
            _loadedCount = 0;
            LoadNextTilePage();
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
            var (total, favorites, tags, sourceCounts) = await Task.Run(() =>
                (_db.CountMangas(), _db.CountMangas(true), _db.CountTags(), _db.GetSourceCounts()));

            foreach (var filter in _sourceFilters)
                filter.Count = sourceCounts.GetValueOrDefault(filter.Id);

            _totalCount = total;
            NavLibraryCount.Text = total > 0 ? total.ToString() : string.Empty;
            NavFavoritesCount.Text = favorites > 0 ? favorites.ToString() : string.Empty;
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
            EmptyHintText.Text = "Hake oben in der Quellen-Leiste mindestens eine Seite an – oder wähle „Alle“.";
        }
        else if (HasActiveSearch)
        {
            EmptyIcon.Text = "\uE721";
            EmptyTitleText.Text = "Keine Treffer";
            EmptyHintText.Text = "Es müssen alle gewählten Tags zutreffen. Entferne einen Tag-Chip unter der Suchleiste oder lockere die Quellen-Auswahl.";
        }
        else if (_favoritesOnly)
        {
            EmptyIcon.Text = "\uE734";
            EmptyTitleText.Text = "Noch keine Favoriten";
            EmptyHintText.Text = "Markiere Titel in der Bibliothek mit dem Stern, dann erscheinen sie hier.";
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

            // In der Favoriten-Ansicht verschwindet der Eintrag sofort, ohne die Liste neu aufzubauen.
            if (!value && _favoritesOnly && _tiles.Remove(tile))
            {
                _results.Remove(tile.Manga);
                _loadedCount = _tiles.Count;
                UpdateEmptyState();
            }

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
        if (ReaderWindow.OpenLocal(this, tile.Manga) is not null)
        {
            try
            {
                await Task.Run(() => _db.TouchLastOpened(tile.Id));
            }
            catch
            {
                // "Zuletzt geöffnet" ist nicht kritisch
            }

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
                ExtraArguments = _extraArgs,
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

        var message = urls.Count == 1 ? "Download eingereiht." : $"{urls.Count} Downloads eingereiht.";
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
            startInfo.ArgumentList.Add(job.Url);

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

        if (!isError && LibraryScanner.TryParseDownloadedPath(line, out var path, out var skipped))
        {
            job.RegisterFile(path, skipped);
            UpdateHud();
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
            job.Message = job.TotalFiles > 0 ? $"Abgebrochen · {job.TotalFiles} Dateien bereits geladen" : "Abgebrochen";
            job.Status = JobStatus.Cancelled;
            MarkLogDirty(job);
            UpdateHud();
            SetStatus($"Download abgebrochen: {Truncate(job.DisplayTitle, 80)}", StatusKind.Warning);
            return;
        }

        if (failure is not null || (exitCode != 0 && job.Files.Count == 0))
        {
            job.Message = failure ?? job.LastError ?? $"gallery-dl wurde mit Code {exitCode} beendet.";
            job.Status = JobStatus.Failed;
            SetStatus($"Download fehlgeschlagen: {Truncate(job.Message, 120)}", StatusKind.Error);
            UpdateHud();
            return;
        }

        if (job.Files.Count == 0)
        {
            job.Message = "gallery-dl hat keine Dateien geliefert – wird diese URL unterstützt?";
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
            _db.AddMangas(accepted);
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

    private void WebSite_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag } || tag == _webSource)
            return;

        if (tag == "exhentai")
            return;

        _webSource = tag;
        _webTags.Clear();
        _webHits.Clear();
        CloseWebSuggestions();
        WebDiscoveryPanelTitle.Text = "Web Discovery";
        WebDiscoveryPanelHint.Text = "Tags dieser Quelle wählen (AND). Autocomplete kommt nur aus online_site_tags von "
                                  + SourceCatalog.DisplayName(_webSource) + ".";
        ApplyWebDiscoveryVisibility();
        _ = RefreshWebPopularTagsAsync();
        UpdateSubtitle();
        SetStatus("Ziel: " + SourceCatalog.DisplayName(_webSource) + " – nur Online-Tags dieser Website.");
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
        if (WebTagsToggle is null || WebTagArea is null)
            return;

        var show = WebTagsToggle.IsChecked == true;
        WebTagArea.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        WebTagsToggle.Content = show ? "🏷️  Tags ausblenden" : "🏷️  Tags anzeigen";
        if (!show)
            CloseWebSuggestions();
        ApplyWebHeaderBudget();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyWebHeaderBudget();

    /// <summary>
    /// Zugeklappt bleibt der Kopf (Website + Suchleiste) bei höchstens 22 % der Fensterhöhe.
    /// Offener Tag-Katalog darf bis zur vollen Suchseite nach unten wachsen.
    /// </summary>
    private void ApplyWebHeaderBudget()
    {
        if (WebSearchHeader is null || WebSearchView is null || !WebSearchView.IsVisible || ActualHeight <= 0)
            return;
        if (PresentationSource.FromVisual(WebSearchHeader) is null)
            return;

        var catalogOpen = WebTagsToggle?.IsChecked == true
                          && WebCatalogExpander?.IsExpanded == true
                          && WebTagArea?.Visibility == Visibility.Visible;

        if (!catalogOpen)
        {
            var top = Math.Max(0, WebSearchHeader.TranslatePoint(new Point(0, 0), this).Y);
            WebSearchHeader.MaxHeight = Math.Max(68, ActualHeight * 0.22 - top);
            WebCatalogScroll?.ClearValue(FrameworkElement.MaxHeightProperty);
            return;
        }

        var page = WebSearchView.ActualHeight > 1 ? WebSearchView.ActualHeight : ActualHeight * 0.75;
        WebSearchHeader.MaxHeight = page;
        if (WebCatalogScroll is not null)
            WebCatalogScroll.MaxHeight = Math.Max(160, page - 84);
    }

    private async Task RefreshWebPopularTagsAsync()
    {
        if (_tags is null)
            return;

        var source = _webSource;
        try
        {
            var catalog = await Task.Run(() => _tags.GetAll(source));
            if (source != _webSource)
                return;

            _webCatalog = catalog;
            _webTagGroups.Clear();
            foreach (var group in TagGroup.FromChips(catalog.Select(t => new TagChip(t.Name, t.MangaCount)), expandAll: false))
                _webTagGroups.Add(group);
            TagNames.RegisterCategories(catalog.Select(t => t.Category).Where(c => c.Length > 0));
            var count = catalog.Count;
            WebPopularHint.Text = count == 0
                ? "Noch keine Online-Tags für " + SourceCatalog.DisplayName(source) +
                  " – „Tags dieser Quelle aktualisieren“ lädt den vollständigen Index."
                : count.ToString("N0") + " Tags von " + SourceCatalog.DisplayName(source) +
                  " in online_site_tags. Liste ist virtualisiert; Klick fügt den Tag der AND-Suche hinzu.";
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
    }

    private void WebActiveTag_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActiveTag tag)
            _webTags.Remove(tag);
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
            ApplySessionCookiesToApi();
            var count = await _tags.DownloadAndSeedSiteTags(site, _api, progress, ct);
            await RefreshWebPopularTagsAsync();
            SetStatus(count.ToString("N0") + " Online-Tags für " + SourceCatalog.DisplayName(site) + " gespeichert.", StatusKind.Success);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Tag-Download abgebrochen.", StatusKind.Warning);
        }
        catch (Exception ex)
        {
            SetStatus("Tag-Index: " + ex.Message, StatusKind.Error);
            AppendSessionLog("! Tag-Index " + site + ": " + FlattenLogLine(ex.Message));
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
            var found = await Task.Run(() => _tags.Suggest(source, word.Query, limit: null, skip));
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
        _webSuggestVersion++;
        WebSuggestPopup.IsOpen = false;
        WebSuggestList.SelectedIndex = -1;
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

    private async void WebSearchMore_Click(object sender, RoutedEventArgs e) => await LoadMoreWebResultsAsync();

    private async void WebSearchNext_Click(object sender, RoutedEventArgs e) =>
        await RunWebSearchAsync(reset: false, page: _webPage + 1, append: false);

    private async void WebSearchPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_webPage <= 1)
            return;
        await RunWebSearchAsync(reset: false, page: _webPage - 1, append: false);
    }

    private async void WebSearchGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_ready || _webSearching || !_api.LastHasMore || e.OriginalSource is not ScrollViewer viewer)
            return;
        if (_webScrollLoad)
            return;
        var remaining = viewer.ExtentHeight - (viewer.VerticalOffset + viewer.ViewportHeight);
        if (remaining > 500 || viewer.ExtentHeight <= viewer.ViewportHeight)
            return;
        _webScrollLoad = true;
        try
        {
            await LoadMoreWebResultsAsync();
        }
        finally
        {
            _webScrollLoad = false;
        }
    }

    private async Task LoadMoreWebResultsAsync()
    {
        if (_webSearching || !_api.LastHasMore || _webHits.Count == 0)
            return;
        await RunWebSearchAsync(reset: false, page: _webPage + 1, append: true);
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
        if (tags.Count == 0 && extra.Length == 0)
        {
            SetStatus("Bitte mindestens einen Tag oder ein Suchwort angeben.", StatusKind.Warning);
            WebSearchTagBox.Focus();
            return;
        }

        if (_webSource == "exhentai")
        {
            SetStatus("ExHentai ist vorübergehend nicht auswählbar. Bitte E-Hentai verwenden.", StatusKind.Warning);
            return;
        }

        _webSearching = true;
        if (reset)
        {
            _webSearchTags = tags;
            _webSearchExtra = extra;
            _webHits.Clear();
            _webRangeStart = 1;
            _webPageSize = 25;
            if (WebCatalogExpander.IsExpanded)
                WebCatalogExpander.IsExpanded = false;
        }

        var requestedPage = Math.Max(1, page ?? _webPage);
        _webSearchCts?.Dispose();
        _webSearchCts = new CancellationTokenSource();
        var ct = _webSearchCts.Token;
        SetWebSearchBusy(true, $"Seite {requestedPage} auf {SourceCatalog.DisplayName(_webSource)} …", overlay: !append);
        UpdateSubtitle();
        if (reset)
        {
            LogOnlineRead("Web Search  //  " + SourceCatalog.DisplayName(_webSource),
                "$ " + _webSource + "  ·  " + string.Join(" ", tags) + (extra.Length > 0 ? " " + extra : string.Empty));
        }

        var harvested = new List<(string Tag, int Count)>();
        var added = 0;
        try
        {
            var (command, _) = await ResolveGalleryDlAsync();
            _api.GalleryDl = command;
            _api.ExtraArgs = _extraArgs;
            _api.WorkingDirectory = Directory.Exists(_libraryPath) ? _libraryPath : null;
            _api.FlareSolverrUrl = string.IsNullOrWhiteSpace(_flareSolverrUrl) ? null : _flareSolverrUrl;
            ApplySessionCookiesToApi();

            if (!append)
                _webHits.Clear();

            await foreach (var hit in _api.SearchStreamAsync(_webSource, tags, extra.Length > 0 ? extra : null, requestedPage, ct))
            {
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
                if (added >= 15)
                    _webPageSize = added;
                if (!append)
                    _webRangeStart = (_webPage - 1) * Math.Max(1, _webPageSize) + 1;
            }

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
            if (_webHits.Count == 0)
            {
                WebDiscoveryPanelTitle.Text = "Keine Treffer";
                WebDiscoveryPanelHint.Text = "Die API hat zu dieser Suche keine Galerien geliefert. Andere Tags oder eine andere Website versuchen.";
                AppendSessionLog("— keine Treffer —");
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
                    WebDiscoveryPanelTitle.Text = "Suche fehlgeschlagen";
                    WebDiscoveryPanelHint.Text = ex.Message;
                    ApplyWebDiscoveryVisibility();
                }
            }
        }
        catch (Exception ex)
        {
            AppendSessionLog("! " + FlattenLogLine(ex.Message));
            SetStatus("Online-Suche fehlgeschlagen: " + ex.Message, StatusKind.Error);
        }
        finally
        {
            _webSearching = false;
            SetWebSearchBusy(false, null);
            ApplyWebDiscoveryVisibility();
            UpdateSubtitle();
        }
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
        WebSearchButton.IsEnabled = !busy;
        WebSearchMoreButton.IsEnabled = !busy && _api is not null && _api.LastHasMore;
        WebSearchNextButton.IsEnabled = !busy && _api is not null && _api.LastHasMore;
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
        var total = _api.LastTotal;
        WebSearchCountText.Text = total is int count
            ? count.ToString("N0") + " Ergebnisse gefunden – Zeige " + _webRangeStart.ToString("N0") + "–" + end.ToString("N0")
            : _webHits.Count.ToString("N0") + " Ergebnisse – Zeige " + _webRangeStart.ToString("N0") + "–" + end.ToString("N0");
        WebSearchPageText.Text = _api.LastTotalPages is int pages
            ? "Seite " + _webPage + " von " + pages
            : "Seite " + _webPage;
        WebSearchCountText.Visibility = Visibility.Visible;
        WebSearchPager.Visibility = Visibility.Visible;
        WebSearchPrevButton.IsEnabled = _webPage > 1;
        WebSearchNextButton.IsEnabled = _api.LastHasMore;
        WebSearchMoreButton.IsEnabled = _api.LastHasMore;
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
                job.Message = "Abgebrochen";
                job.Status = JobStatus.Cancelled;
                break;
            case JobStatus.Running:
                job.Message = "Wird abgebrochen …";
                job.Cts.Cancel(); // beendet den Prozess; FinishJobAsync setzt den Endstatus
                break;
        }

        UpdateHud();
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
        _maxParallel = ParallelBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var parallel)
            ? Math.Clamp(parallel, 1, 4)
            : 2;
        _galleryDl = null;

        LibraryPathBox.Text = _libraryPath;
        GalleryDlPathBox.Text = _galleryDlPath;

        try
        {
            await Task.Run(() =>
            {
                _db.SetSetting(SettingLibraryPath, library);
                _db.SetSetting(SettingGalleryDlPath, _galleryDlPath);
                _db.SetSetting(SettingExtraArgs, _extraArgs);
                _db.SetSetting(SettingMaxParallel, _maxParallel.ToString());
                _db.SetSetting(SettingFlareSolverr, string.IsNullOrWhiteSpace(_flareSolverrUrl) ? null : _flareSolverrUrl);
            });
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

    private void ApplySessionCookiesToApi()
    {
        if (_api is null)
            return;
        try
        {
            _api.LoadCookies(_db.GetHttpCookies());
            _api.FlareSolverrUrl = string.IsNullOrWhiteSpace(_flareSolverrUrl) ? null : _flareSolverrUrl;
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
    private const int CoverDecodeWidth = 360; // etwa Kachelbreite × 2 für hochauflösende Monitore
    private static readonly SemaphoreSlim CoverGate = new(3);

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
            await CoverGate.WaitAsync();
            try
            {
                var bitmap = await Task.Run(() =>
                {
                    var path = coverPath;
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                        path = string.IsNullOrEmpty(folder) ? null : LibraryScanner.FindFirstImage(folder);

                    return path is null ? null : LoadBitmap(path);
                });

                if (bitmap is not null)
                {
                    _cover = bitmap;
                    OnPropertyChanged(nameof(Cover));
                }
            }
            finally
            {
                CoverGate.Release();
            }
        }
        catch
        {
            // Ein fehlendes Cover ist kein Fehler – die Kachel zeigt dann den Platzhalter.
        }
    }

    private static BitmapImage? LoadBitmap(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // liest komplett ein, die Datei bleibt nicht gesperrt
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = CoverDecodeWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze(); // einfrieren: darf vom Hintergrund-Thread an die Oberfläche übergeben werden
            return bitmap;
        }
        catch
        {
            return null; // z. B. WebP/AVIF ohne installierten Windows-Codec
        }
    }
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
            var bitmap = await CoverImageLoader.LoadAsync(CoverUrl, Source);
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

    public TagGroup(string category, IEnumerable<TagChip> tags, bool expanded)
    {
        Category = category ?? string.Empty;
        Title = TagCategoryUi.Title(Category);
        Tags = new ObservableCollection<TagChip>(tags);
        _isExpanded = expanded;
    }

    public string Category { get; }
    public string Title { get; }
    public ObservableCollection<TagChip> Tags { get; }
    public int Count => Tags.Count;
    public string Header => Title + "  ·  " + Count;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
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
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsRunning => Status == JobStatus.Running;
    public bool CanCancel => Status is JobStatus.Queued or JobStatus.Running;
    public bool CanRemove => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;

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
                    return "In der Warteschlange";

                case JobStatus.Running:
                    if (!string.IsNullOrEmpty(Message))
                        return Message;
                    if (TotalFiles == 0)
                        return "Verbinde und lese Metadaten …";

                    var text = $"{FileCount} geladen";
                    if (SkippedCount > 0)
                        text += $" · {SkippedCount} übersprungen";
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

    public void AppendLog(string line)
    {
        _log.Add(line);
        if (_log.Count > MaxLogLines)
            _log.RemoveRange(0, 500);
    }

    public string GetLogText(int maxLines = 600) =>
        string.Join(Environment.NewLine, _log.Skip(Math.Max(0, _log.Count - maxLines)));
}


