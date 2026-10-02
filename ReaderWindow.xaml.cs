using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace MangaLibraryApp;

/// <summary>Fortschritt beim Laden einer Seite: empfangene Bytes und – falls der Server sie nennt – die Gesamtgröße.</summary>
internal readonly record struct ReadProgress(long Received, long? Total);

/// <summary>Eine Seite des Readers: liefert die (noch undekodierten) Bildbytes – aus dem Netz, einem Ordner oder einem Archiv.</summary>
internal sealed class ReaderPage
{
    public ReaderPage(Func<IProgress<ReadProgress>, CancellationToken, Task<byte[]>> fetch) => Fetch = fetch;

    /// <summary>Holt die Bytes. Der Fortschritt wird nur beim Laden aus dem Netz gemeldet (lokale Dateien sind sofort da).</summary>
    public Func<IProgress<ReadProgress>, CancellationToken, Task<byte[]>> Fetch { get; }
}

internal enum FitMode
{
    /// <summary>Ganze Seite im Fenster.</summary>
    Page,

    /// <summary>Fensterbreite, nach unten scrollen.</summary>
    Width,

    /// <summary>Originalgröße.</summary>
    Original,
}

/// <summary>Gemeinsame Fenster-Hilfen für Fenster mit eigener Titelleiste (<see cref="WindowChrome"/>).</summary>
internal static class ChromeHelper
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;

    /// <summary>
    /// Maximiert ragt ein Fenster mit WindowChrome um die unsichtbare Größenänderungs-Kante über den Bildschirm hinaus;
    /// der Inhalt wird um genau diese Kante (Rahmen + Zusatzrand, passend zur Skalierung) eingerückt.
    /// </summary>
    public static Thickness MaximizedInset(Window window)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var padded = GetSystemMetrics(SM_CXPADDEDBORDER);
        var x = (GetSystemMetrics(SM_CXSIZEFRAME) + padded) / dpi.DpiScaleX;
        var y = (GetSystemMetrics(SM_CYSIZEFRAME) + padded) / dpi.DpiScaleY;
        return new Thickness(x, y, x, y);
    }
}

/// <summary>
/// Lesefenster. <b>Online</b> (<c>new ReaderWindow(url, isOnlineMode: true)</c>): gallery-dl liefert im Hintergrund mit <c>-g</c> die direkten
/// Bild-Adressen der Galerie (<see cref="ImageUrls"/>); der Reader lädt die Seiten asynchron per <see cref="HttpClient"/> als Byte-Array in
/// den Arbeitsspeicher und dekodiert sie aus einem <see cref="MemoryStream"/> zu einem <see cref="BitmapImage"/> – nichts wird vorher auf die
/// Festplatte geschrieben. Die nächsten Seiten werden schon vorab geladen; braucht eine Seite doch noch Zeit, zeigt ein Ladeindikator den
/// Fortschritt. <b>Lokal</b> (<c>isOnlineMode: false</c>): dieselbe Oberfläche liest Ordner und CBZ/ZIP-Archive.
/// </summary>
public partial class ReaderWindow : Window
{
    private const int PrefetchAhead = 3;
    private const int KeepBehind = 3;
    private const int KeepAhead = 8;
    private const int MaxDecodeWidth = 2600;
    private const int MaxDecodeHeight = 3600;
    private const long MaxImageBytes = 90L * 1024 * 1024;
    private const double ScrollBarWidth = 12;

    /// <summary>Erst wenn eine Seite länger als das braucht, erscheint der Ladeindikator (kein Flackern bei fertigen oder lokalen Seiten).</summary>
    private static readonly TimeSpan LoadingIndicatorDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>Solange die nächste Seite lädt, bleibt die alte sichtbar – gedämpft, damit klar ist, dass sie nicht mehr aktuell ist.</summary>
    private const double StalePageOpacity = 0.35;

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _downloadGate = new(3);
    private readonly List<ReaderPage> _pages = new();
    private readonly List<string> _imageUrls = new();
    private readonly Dictionary<int, BitmapImage> _decoded = new();
    private readonly Dictionary<int, Task<BitmapImage>> _loading = new();

    private Func<Task>? _startListing;
    private int _index = -1;
    private int _showVersion;
    private int _loadingIndex = -1;
    private bool _starting;
    private bool _listingDone;
    private bool _waitingForNext;
    private bool _updatingSlider;
    private bool _rightToLeft;
    private bool _fullscreen;
    private FitMode _fit = FitMode.Page;
    private WindowState _stateBeforeFullscreen;
    private string? _galleryUrl;
    private string? _warning;
    private string? _displayTitle;

    /// <summary>
    /// Öffnet den Reader.
    /// <para><b>Online</b> (<paramref name="isOnlineMode"/> = <c>true</c>): <paramref name="url"/> ist die Adresse der Galerie. Nach <see cref="Window.Show"/>
    /// fragt der Reader im Hintergrund per <c>gallery-dl -g &lt;url&gt;</c> die direkten Bild-URLs ab und streamt die Seiten über
    /// <see cref="HttpClient"/> direkt in den Speicher.</para>
    /// <para><b>Lokal</b> (<c>false</c>): <paramref name="url"/> ist der Pfad eines Bilderordners oder eines CBZ/ZIP-Archivs.</para>
    /// Optionale Einstellungen (<see cref="GalleryDlPath"/>, <see cref="ExtraArguments"/>, <see cref="WorkingDirectory"/>,
    /// <see cref="DisplayTitle"/>) vor dem <see cref="Window.Show"/> setzen.
    /// </summary>
    /// <exception cref="ArgumentException">Leere Angabe – oder im Online-Modus keine Adresse, die mit http:// oder https:// beginnt.</exception>
    public ReaderWindow(string url, bool isOnlineMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        url = url.Trim();

        if (isOnlineMode && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
            throw new ArgumentException("Der Online-Modus braucht eine Adresse, die mit http:// oder https:// beginnt.", nameof(url));

        InitializeComponent();

        Url = url;
        IsOnlineMode = isOnlineMode;

        if (isOnlineMode)
            Prepare(url, SourceCatalog.FromUrl(url), url);
        else
            Prepare(LibraryScanner.FolderTitle(url), SourceCatalog.Other, null);
    }

    /// <summary>Adresse der Galerie (Online-Modus) bzw. Pfad des Ordners oder Archivs (lokal).</summary>
    public string Url { get; }

    /// <summary><c>true</c>: Seiten kommen aus dem Netz (gallery-dl + HttpClient); <c>false</c>: aus einem Ordner oder Archiv.</summary>
    public bool IsOnlineMode { get; }

    /// <summary>Anzeigename in der Titelleiste; Standard ist die Adresse bzw. der Ordnername.</summary>
    public string? DisplayTitle
    {
        get => _displayTitle;
        set
        {
            _displayTitle = value;
            if (!string.IsNullOrWhiteSpace(value))
                ApplyTitle(value.Trim());
        }
    }

    /// <summary>Pfad zu gallery-dl; leer = automatisch suchen (App-Ordner, PATH, „python -m gallery_dl“).</summary>
    public string? GalleryDlPath { get; set; }

    /// <summary>Zusätzliche gallery-dl-Argumente (z. B. Cookies), die vor der Adresse übergeben werden.</summary>
    public string? ExtraArguments { get; set; }

    /// <summary>Arbeitsordner für gallery-dl (relevant für relative Pfade in dessen Konfiguration).</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Die direkten Bild-Adressen, die <c>gallery-dl -g</c> geliefert hat. Die Liste wächst, während gallery-dl noch sucht
    /// (die erste Seite wird schon angezeigt); nur im Oberflächen-Thread lesen.
    /// </summary>
    public IReadOnlyList<string> ImageUrls => _imageUrls;

    /// <summary>Bereits gefundenes gallery-dl, damit der Reader nicht erneut suchen muss (z. B. vom Hauptfenster); sonst sucht er selbst.</summary>
    internal GalleryDlCommand? ResolvedCommand { get; set; }

    /// <summary>
    /// Wird ausgelöst, wenn der Reader einen Fehler anzeigt (ungültige URL, keine Verbindung, gallery-dl-Fehler, Bild nicht ladbar).
    /// Das Hauptfenster schreibt die Meldung ins Download-Protokoll.
    /// </summary>
    public event Action<string>? Failed;

    // ════════════════════════════════════════════════════════════════════
    //  Öffnen
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Liest einen Eintrag der Bibliothek (Ordner oder CBZ/ZIP). Liefert <c>null</c>, wenn nichts Lesbares gefunden wurde.</summary>
    internal static ReaderWindow? OpenLocal(Window? owner, Manga manga)
    {
        var path = manga.FolderPath;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        List<ReaderPage>? pages;
        try
        {
            pages = ListLocalPages(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }

        if (pages is null || pages.Count == 0)
            return null;

        var window = new ReaderWindow(path, isOnlineMode: false) { Owner = owner };
        window.Prepare(manga.Title, manga.Source, manga.SourceUrl);
        window._startListing = () =>
        {
            window.AddPages(pages); // schon aufgelistet – kein zweites Mal suchen
            window.FinishListing();
            return Task.CompletedTask;
        };
        window.Show();
        return window;
    }

    /// <summary>Seiten eines Bilderordners oder CBZ/ZIP-Archivs; <c>null</c>, wenn der Pfad keins von beiden ist.</summary>
    private static List<ReaderPage>? ListLocalPages(string path)
    {
        if (File.Exists(path) && LibraryScanner.IsArchivePath(path))
        {
            return LibraryScanner.ListArchivePages(path)
                .Select(name => new ReaderPage((_, ct) => Task.Run(() => LibraryScanner.ReadArchiveEntry(path, name), ct)))
                .ToList();
        }

        if (Directory.Exists(path))
        {
            return LibraryScanner.ListFolderPages(path)
                .Select(file => new ReaderPage((_, ct) => ReadLocalFileAsync(file, ct)))
                .ToList();
        }

        return null;
    }

    /// <summary>Lokale Bilddatei mit Größenlimit – dieselbe Obergrenze wie beim Netzabruf, damit eine einzelne Datei den Speicher nicht füllt.</summary>
    private static async Task<byte[]> ReadLocalFileAsync(string file, CancellationToken ct)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(file);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new IOException("Die Datei konnte nicht gelesen werden: " + file, ex);
        }

        if (!info.Exists)
            throw new FileNotFoundException("Datei nicht gefunden: " + file);
        if (info.Length > MaxImageBytes)
            throw new IOException("Die Datei ist ungewöhnlich groß und wird nicht geladen.");

        return await File.ReadAllBytesAsync(file, ct);
    }

    private void Prepare(string title, string source, string? galleryUrl)
    {
        _galleryUrl = galleryUrl;
        ApplyTitle(title);
        SourceBadge.Text = source == SourceCatalog.Other ? string.Empty : SourceCatalog.DisplayName(source).ToUpperInvariant();
        BrowserButton.Visibility = galleryUrl is null ? Visibility.Collapsed : Visibility.Visible;
        UpdatePageUi();
    }

    private void ApplyTitle(string title)
    {
        Title = title + " – Reader";
        TitleText.Text = title;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Focus();
        await RunStartAsync();
    }

    /// <summary>Startet die Seitenliste – beim Öffnen und bei „Erneut versuchen“, wenn die Liste selbst scheiterte.</summary>
    private async Task RunStartAsync()
    {
        if (_starting)
            return;

        _starting = true;
        try
        {
            await (_startListing is not null ? _startListing() : IsOnlineMode ? StartOnlineAsync() : StartLocalAsync());
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Fenster wurde geschlossen
        }
        catch (Exception ex)
        {
            if (_pages.Count == 0)
                ShowError(ex.Message, canRetry: _startListing is null);
            else
                SetStatus("Liste unvollständig: " + ex.Message);
        }
        finally
        {
            _starting = false;
        }
    }

    /// <summary>Online: gallery-dl suchen (falls noch nicht bekannt), dann <c>gallery-dl -g &lt;url&gt;</c> im Hintergrund ausführen.</summary>
    private async Task StartOnlineAsync()
    {
        var command = ResolvedCommand;
        if (command is null)
        {
            ShowLoading("Suche gallery-dl …");
            var (resolved, problem) = await GalleryDl.ResolveAsync(GalleryDlPath);
            _cts.Token.ThrowIfCancellationRequested(); // das Fenster wurde währenddessen geschlossen

            if (resolved is null)
                throw new InvalidOperationException(
                    "gallery-dl wurde nicht gefunden. " + (problem ?? string.Empty) +
                    " Pfad unter „Einstellungen“ angeben oder installieren: pip install -U gallery-dl");

            command = ResolvedCommand = resolved;
        }

        await ListOnlineAsync(command, Url, ExtraArguments, WorkingDirectory, RefererFor(Url));
    }

    /// <summary>Lokal: Seiten des Ordners bzw. Archivs im Hintergrund auflisten.</summary>
    private async Task StartLocalAsync()
    {
        ShowLoading("Lese Seiten …");
        var path = Url;
        var pages = await Task.Run(() => ListLocalPages(path), _cts.Token);

        if (pages is null || pages.Count == 0)
            throw new FileNotFoundException(pages is null
                ? "Der Pfad ist weder ein Ordner noch ein CBZ/ZIP-Archiv: " + path
                : "In diesem Ordner bzw. Archiv wurden keine Bilder gefunden: " + path);

        AddPages(pages);
        FinishListing();
    }

    /// <summary>Die Seiten prüfen den Referer – bekannte Websites bekommen ihre Startseite, sonst die Wurzel der Galerie-Adresse.</summary>
    private static string RefererFor(string galleryUrl) =>
        SourceCatalog.Find(SourceCatalog.FromUrl(galleryUrl))?.Root
        ?? (Uri.TryCreate(galleryUrl, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) + "/" : galleryUrl);

    private void Window_Closed(object? sender, EventArgs e)
    {
        try
        {
            _cts.Cancel(); // beendet auch einen noch laufenden gallery-dl-Prozess
        }
        catch (ObjectDisposedException)
        {
        }

        _decoded.Clear();
        _loading.Clear();
        PageImage.Source = null;

        try { _cts.Dispose(); }
        catch (ObjectDisposedException) { }

        try { _downloadGate.Dispose(); }
        catch (ObjectDisposedException) { }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Seitenliste
    // ════════════════════════════════════════════════════════════════════

    private async Task ListOnlineAsync(GalleryDlCommand command, string url, string? extraArguments, string? workingDirectory, string referer)
    {
        ShowLoading("gallery-dl sucht die Seiten …");
        SetStatus("Liste wird geladen …");

        await foreach (var page in GalleryDl.ListPagesAsync(command, url, extraArguments, workingDirectory,
                           warning => _warning = warning, _cts.Token))
        {
            _imageUrls.Add(page.Url);
            AddPages(new[] { new ReaderPage((progress, ct) => FetchRemoteAsync(page, referer, progress, ct)) });
        }

        FinishListing();
    }

    private void AddPages(IEnumerable<ReaderPage> pages)
    {
        _pages.AddRange(pages);
        UpdatePageUi();

        if (_index < 0)
            _ = ShowPageAsync(0);
        else if (_waitingForNext && _index + 1 < _pages.Count)
            _ = ShowPageAsync(_index + 1);
    }

    private void FinishListing()
    {
        _listingDone = true;
        UpdatePageUi();

        if (_warning is not null)
            SetStatus("Liste unvollständig: " + _warning);
        else
            SetStatus($"{_pages.Count} Seiten");

        // Wurde auf eine Seite gewartet, die es nicht gibt: zurück auf die letzte.
        if (_waitingForNext && _index + 1 >= _pages.Count)
        {
            _waitingForNext = false;
            HideLoading();
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Seite anzeigen · Laden · Vorladen
    // ════════════════════════════════════════════════════════════════════

    private async Task ShowPageAsync(int index)
    {
        if (index < 0 || index >= _pages.Count)
            return;

        _index = index;
        _waitingForNext = false;
        var version = ++_showVersion;
        UpdatePageUi();
        ErrorPanel.Visibility = Visibility.Collapsed;

        // Ist die Seite schon dekodiert, ist die Aufgabe sofort fertig. Sonst wird sie gerade aus dem Netz gestreamt: Ladeindikator.
        var pending = GetPageAsync(index);
        if (!pending.IsCompleted)
        {
            if (LoadingPanel.Visibility == Visibility.Visible)
                ShowLoading(PageLoadingText(index, null), index); // läuft schon (vorherige Seite): Text sofort anpassen
            else
                _ = ShowLoadingWhenSlowAsync(index, version, pending);
        }

        try
        {
            var bitmap = await pending;
            if (version != _showVersion)
                return; // der Nutzer ist inzwischen weitergeblättert

            PageImage.Source = bitmap;
            HideLoading();
            ApplyFit();
            PageScroller.ScrollToTop();
            PageScroller.ScrollToLeftEnd();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (version == _showVersion)
                ShowError($"Seite {index + 1} konnte nicht geladen werden:\n{ex.Message}", canRetry: true);
            return;
        }

        Prefetch(index);
        Evict(index);
    }

    /// <summary>
    /// Zeigt den Ladeindikator erst, wenn die Seite nach <see cref="LoadingIndicatorDelay"/> noch nicht da ist – so flackert nichts,
    /// wenn die Seite vorgeladen wurde oder aus einer lokalen Datei kommt.
    /// </summary>
    private async Task ShowLoadingWhenSlowAsync(int index, int version, Task pending)
    {
        try
        {
            await Task.WhenAny(pending, Task.Delay(LoadingIndicatorDelay, _cts.Token));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        if (!_cts.IsCancellationRequested && !pending.IsCompleted && version == _showVersion)
        {
            try
            {
                ShowLoading(PageLoadingText(index, null), index);
            }
            catch (InvalidOperationException)
            {
                // Fenster wird gerade geschlossen
            }
        }
    }

    private Task<BitmapImage> GetPageAsync(int index)
    {
        if (_decoded.TryGetValue(index, out var ready))
            return Task.FromResult(ready);

        if (_loading.TryGetValue(index, out var running))
            return running;

        var task = LoadPageAsync(index);
        _loading[index] = task;
        return task;
    }

    private async Task<BitmapImage> LoadPageAsync(int index)
    {
        try
        {
            try
            {
                await _downloadGate.WaitAsync(_cts.Token);
            }
            catch (ObjectDisposedException)
            {
                throw new OperationCanceledException();
            }

            byte[] data;
            try
            {
                // Der Fortschritt kommt im Oberflächen-Thread an (Progress<T> merkt sich den aktuellen Kontext).
                var progress = new Progress<ReadProgress>(p =>
                {
                    try { OnPageProgress(index, p); }
                    catch (InvalidOperationException) { }
                });
                data = await _pages[index].Fetch(progress, _cts.Token);
            }
            finally
            {
                try { _downloadGate.Release(); }
                catch (ObjectDisposedException) { }
            }

            var bitmap = await Task.Run(() => Decode(data), _cts.Token);
            _decoded[index] = bitmap;
            return bitmap;
        }
        catch (ObjectDisposedException)
        {
            throw new OperationCanceledException();
        }
        finally
        {
            _loading.Remove(index); // fehlgeschlagene Seiten werden beim nächsten Versuch neu geladen
        }
    }

    /// <summary>Aktualisiert den Ladeindikator, wenn der Fortschritt zur gerade angezeigten (noch ladenden) Seite gehört.</summary>
    private void OnPageProgress(int index, ReadProgress progress)
    {
        if (index == _loadingIndex)
            LoadingText.Text = PageLoadingText(index, progress);
    }

    private static string PageLoadingText(int index, ReadProgress? progress)
    {
        var text = $"Seite {index + 1} wird geladen …";

        if (progress is { Received: > 0 } p)
        {
            text += p.Total is > 0
                ? $"  {Math.Min(100, p.Received * 100 / p.Total.Value)} %"
                : "  " + FormatSize(p.Received);
        }

        return text;
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1_048_576 ? $"{bytes / 1_048_576d:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    private void Prefetch(int index)
    {
        for (var i = index + 1; i <= index + PrefetchAhead && i < _pages.Count; i++)
            ObservePrefetch(GetPageAsync(i));

        if (index > 0)
            ObservePrefetch(GetPageAsync(index - 1));
    }

    /// <summary>Vorladen ist reiner Komfort – Fehler zeigt erst das tatsächliche Anzeigen der Seite.</summary>
    private static async void ObservePrefetch(Task<BitmapImage> task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Hält den Speicher klein: nur ein Fenster um die aktuelle Seite bleibt dekodiert.</summary>
    private void Evict(int index)
    {
        foreach (var key in _decoded.Keys.Where(k => k < index - KeepBehind || k > index + KeepAhead).ToList())
            _decoded.Remove(key);
    }

    /// <summary>
    /// Macht aus den Bildbytes ein <see cref="BitmapImage"/> – direkt aus einem <see cref="MemoryStream"/>, ohne Umweg über die Festplatte.
    /// Sehr große Scans werden schon beim Dekodieren verkleinert (bei JPEG skaliert Windows dabei direkt in der Dekodier-Stufe): spart
    /// Arbeitsspeicher und Zeit, auf dem Bildschirm sieht man keinen Unterschied. Das Ergebnis ist eingefroren und darf daher vom
    /// Hintergrund-Thread an die Oberfläche übergeben werden.
    /// </summary>
    private static BitmapImage Decode(byte[] data)
    {
        try
        {
            // Die Größe zuerst nur aus dem Bild-Kopf lesen, um zu entscheiden, ob verkleinert werden muss.
            int width, height;
            using (var probe = new MemoryStream(data, writable: false))
            {
                var header = BitmapFrame.Create(probe, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                (width, height) = (header.PixelWidth, header.PixelHeight);
            }

            using var stream = new MemoryStream(data, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad; // beim Laden komplett einlesen – der Stream darf danach geschlossen werden
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

            if (width > MaxDecodeWidth || height > MaxDecodeHeight)
            {
                var scale = Math.Min(MaxDecodeWidth / (double)width, MaxDecodeHeight / (double)height);
                image.DecodePixelWidth = Math.Max(1, (int)Math.Round(width * scale)); // die Höhe folgt im selben Seitenverhältnis
            }

            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException
                                       or COMException or ArgumentException or InvalidDataException)
        {
            throw new NotSupportedException(
                "Dieses Bildformat kann Windows nicht darstellen (z. B. AVIF oder WebP ohne installierten Windows-Codec).", ex);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Netzwerk
    // ════════════════════════════════════════════════════════════════════

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 6,
        };

        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
        return client;
    }

    /// <summary>Lädt ein Bild in den Speicher. Die Seiten prüfen den Referer, daher wird die Startseite der Website mitgeschickt.</summary>
    private static async Task<byte[]> FetchRemoteAsync(GalleryPage page, string referer, IProgress<ReadProgress> progress, CancellationToken ct)
    {
        string? lastError = null;

        foreach (var url in new[] { page.Url }.Concat(page.Fallbacks))
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    return await DownloadAsync(url, referer, progress, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                               or UriFormatException or InvalidOperationException)
                {
                    lastError = ex is HttpRequestException { StatusCode: { } code }
                        ? $"Der Server antwortete mit HTTP {(int)code}."
                        : ex is TaskCanceledException ? "Zeitüberschreitung beim Laden."
                        : ex is UriFormatException ? "Ungültige Bild-Adresse."
                        : ex.Message;
                    await Task.Delay(400 * (attempt + 1), ct);
                }
            }
        }

        throw new IOException(lastError ?? "Das Bild konnte nicht geladen werden.");
    }

    /// <summary>
    /// Streamt ein Bild in ein Byte-Array im Arbeitsspeicher und meldet den Fortschritt. Das Größenlimit gilt auch dann, wenn der Server keine
    /// <c>Content-Length</c> nennt (sonst könnte ein endloser Datenstrom den Speicher füllen).
    /// </summary>
    private static async Task<byte[]> DownloadAsync(string url, string referer, IProgress<ReadProgress> progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new IOException("Ungültige Bild-Adresse.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
            request.Headers.Referrer = refererUri;

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new IOException(ex.StatusCode is { } code
                ? $"Der Server antwortete mit HTTP {(int)code}."
                : "Die Adresse ist nicht erreichbar: " + ex.Message, ex);
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            if (total > MaxImageBytes)
                throw new IOException("Das Bild ist ungewöhnlich groß und wird nicht geladen.");

            progress.Report(new ReadProgress(0, total)); // neuer Versuch: Anzeige zurücksetzen

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream(total is > 0 and <= int.MaxValue ? (int)total.Value : 128 * 1024);
            var chunk = new byte[64 * 1024];

            int read;
            while ((read = await body.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxImageBytes)
                    throw new IOException("Das Bild ist ungewöhnlich groß und wird nicht geladen.");

                progress.Report(new ReadProgress(buffer.Length, total));
            }

            // Bei bekannter Größe ist der Puffer genau gefüllt – dann ohne Kopie weitergeben.
            return buffer.Length == buffer.Capacity ? buffer.GetBuffer() : buffer.ToArray();
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Navigation
    // ════════════════════════════════════════════════════════════════════

    private void Step(int direction)
    {
        var target = _index + direction;
        if (target < 0)
            return;

        if (target < _pages.Count)
        {
            _ = ShowPageAsync(target);
        }
        else if (!_listingDone && direction > 0)
        {
            // Die nächste Seite ist noch nicht in der Liste (gallery-dl sucht noch) – sobald sie eintrifft, wird sie angezeigt.
            _waitingForNext = true;
            ShowLoading("Warte auf die nächste Seite …");
        }
    }

    private void GoTo(int index) => _ = ShowPageAsync(Math.Clamp(index, 0, Math.Max(0, _pages.Count - 1)));

    /// <summary>Blättern in Leserichtung: bei „rechts → links“ (Manga) bedeutet die linke Taste „weiter“.</summary>
    private void StepByKey(int leftToRight) => Step(_rightToLeft ? -leftToRight : leftToRight);

    private void Prev_Click(object sender, RoutedEventArgs e) => Step(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Step(+1);

    private void PageSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider || !IsLoaded)
            return;

        var target = (int)Math.Round(e.NewValue) - 1;
        if (target != _index)
            GoTo(target);
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_index >= 0)
        {
            _ = ShowPageAsync(_index);
        }
        else if (_pages.Count == 0)
        {
            // Die Seitenliste selbst ist gescheitert (kein Netz, gallery-dl-Fehler …): noch einmal von vorn.
            ErrorPanel.Visibility = Visibility.Collapsed;
            _imageUrls.Clear();
            _warning = null;
            _ = RunStartAsync();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var handled = true;

        switch (e.Key)
        {
            case Key.Left or Key.A:
                StepByKey(-1);
                break;
            case Key.Right or Key.D:
                StepByKey(+1);
                break;
            case Key.PageUp:
                Step(-1);
                break;
            case Key.PageDown:
                Step(+1);
                break;
            case Key.Space:
                Step(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : +1);
                break;
            case Key.Up when _fit == FitMode.Page:
                Step(-1);
                break;
            case Key.Down when _fit == FitMode.Page:
                Step(+1);
                break;
            case Key.Home:
                GoTo(0);
                break;
            case Key.End:
                GoTo(_pages.Count - 1);
                break;
            case Key.F:
                CycleFit();
                break;
            case Key.R:
                ToggleDirection();
                break;
            case Key.F11:
                ToggleFullscreen();
                break;
            case Key.Escape:
                if (_fullscreen)
                    ToggleFullscreen();
                else
                    Close();
                break;
            default:
                handled = false; // Pfeile hoch/runter scrollen sonst die Seite
                break;
        }

        e.Handled = handled;
    }

    /// <summary>Klick auf das linke bzw. rechte Drittel blättert; die Mitte tut nichts (damit man die Seite in Ruhe ansehen kann).</summary>
    private void ViewerArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ErrorPanel.IsVisible || _pages.Count == 0)
            return;

        var x = e.GetPosition(ViewerArea).X;
        var width = ViewerArea.ActualWidth;
        if (x < width / 3)
            StepByKey(-1);
        else if (x > width * 2 / 3)
            StepByKey(+1);
    }

    private void ViewerArea_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_fit == FitMode.Page)
        {
            Step(e.Delta < 0 ? +1 : -1);
            e.Handled = true;
            return;
        }

        // Beim Scrollen am Seitenende geht es auf der nächsten Seite oben weiter (und umgekehrt).
        var atBottom = PageScroller.VerticalOffset >= PageScroller.ScrollableHeight - 1;
        var atTop = PageScroller.VerticalOffset <= 0.5;
        if (e.Delta < 0 && atBottom)
        {
            Step(+1);
            e.Handled = true;
        }
        else if (e.Delta > 0 && atTop && _index > 0)
        {
            Step(-1);
            e.Handled = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Ansicht
    // ════════════════════════════════════════════════════════════════════

    private void Fit_Click(object sender, RoutedEventArgs e) => CycleFit();

    private void CycleFit()
    {
        _fit = _fit switch
        {
            FitMode.Page => FitMode.Width,
            FitMode.Width => FitMode.Original,
            _ => FitMode.Page,
        };

        FitButton.ToolTip = _fit switch
        {
            FitMode.Page => "Ansicht: ganze Seite (F)",
            FitMode.Width => "Ansicht: Fensterbreite, nach unten scrollen (F)",
            _ => "Ansicht: Originalgröße (F)",
        };
        FitButton.Content = _fit switch
        {
            FitMode.Page => "\uE9A6",
            FitMode.Width => "\uE7C3",
            _ => "\uE71E",
        };

        ApplyFit();
        PageScroller.ScrollToTop();
    }

    private void ApplyFit()
    {
        var width = ViewerArea.ActualWidth;
        var height = ViewerArea.ActualHeight;
        if (width < 2 || height < 2)
            return;

        switch (_fit)
        {
            case FitMode.Page:
                PageScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                PageScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                PageImage.Stretch = Stretch.Uniform;
                PageImage.Width = width;
                PageImage.Height = height;
                break;

            case FitMode.Width:
                PageScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                PageScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Visible; // Platz immer reservieren: stabile Breite
                PageImage.Stretch = Stretch.Uniform;
                PageImage.Width = Math.Max(1, width - ScrollBarWidth);
                PageImage.Height = double.NaN;
                break;

            default:
                PageScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                PageScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                PageImage.Stretch = Stretch.None;
                PageImage.Width = double.NaN;
                PageImage.Height = double.NaN;
                break;
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyFit();

    private void Direction_Click(object sender, RoutedEventArgs e) => ToggleDirection();

    private void ToggleDirection()
    {
        _rightToLeft = !_rightToLeft;
        DirectionButton.ToolTip = _rightToLeft ? "Leserichtung: rechts → links, Manga (R)" : "Leserichtung: links → rechts (R)";
        DirectionButton.Content = _rightToLeft ? "\uE7B2" : "\uE7B3";
        SetStatus(_rightToLeft ? "Leserichtung: rechts → links" : "Leserichtung: links → rechts");
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        var chrome = WindowChrome.GetWindowChrome(this);

        if (_fullscreen)
        {
            _stateBeforeFullscreen = WindowState;
            TitleStrip.Visibility = Visibility.Collapsed;
            BottomBar.Visibility = Visibility.Collapsed;
            chrome.CaptionHeight = 0; // sonst würde der obere Rand als Titelleiste Klicks verschlucken
            WindowState = WindowState.Maximized;
        }
        else
        {
            TitleStrip.Visibility = Visibility.Visible;
            BottomBar.Visibility = Visibility.Visible;
            chrome.CaptionHeight = 34;
            WindowState = _stateBeforeFullscreen;
        }

        ApplyFit();
    }

    private void Browser_Click(object sender, RoutedEventArgs e)
    {
        if (_galleryUrl is null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = _galleryUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("Browser konnte nicht geöffnet werden: " + ex.Message);
        }
    }

    // ── Titelleiste ──

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Verkleinern" : "Maximieren";
        RootGrid.Margin = maximized ? ChromeHelper.MaximizedInset(this) : new Thickness(0);
    }

    // ════════════════════════════════════════════════════════════════════
    //  Anzeige-Zustände
    // ════════════════════════════════════════════════════════════════════

    private void UpdatePageUi()
    {
        var total = _pages.Count;
        PageText.Text = total == 0 ? "–" : $"{_index + 1} / {total}{(_listingDone ? string.Empty : "+")}";

        _updatingSlider = true;
        PageSlider.Maximum = Math.Max(1, total);
        PageSlider.Value = Math.Clamp(_index + 1, 1, Math.Max(1, total));
        _updatingSlider = false;

        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index + 1 < total || !_listingDone;
    }

    /// <param name="pageIndex">Seite, deren Fortschritt angezeigt wird; -1 für allgemeine Meldungen (Suche, Liste).</param>
    private void ShowLoading(string text, int pageIndex = -1)
    {
        _loadingIndex = pageIndex;
        LoadingText.Text = text;
        LoadingPanel.Visibility = Visibility.Visible;

        if (PageImage.Source is not null)
            PageImage.Opacity = StalePageOpacity; // die alte Seite bleibt sichtbar, aber gedämpft
    }

    private void HideLoading()
    {
        _loadingIndex = -1;
        LoadingPanel.Visibility = Visibility.Collapsed;
        PageImage.Opacity = 1;
    }

    private void ShowError(string message, bool canRetry)
    {
        HideLoading();
        ErrorText.Text = message;
        RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        try
        {
            Failed?.Invoke(message);
        }
        catch
        {
            // ein fehlerhafter Abonnent darf den Reader nicht abstürzen lassen
        }
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
