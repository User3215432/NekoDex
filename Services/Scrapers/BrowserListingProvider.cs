using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NekoDex.Core.Enums;
using NekoDex.Core.Models;
using AppSettings = MangaLibraryApp.AppSettings;
using TagDownloadProgress = MangaLibraryApp.TagDownloadProgress;
using WebSearchViewModel = MangaLibraryApp.WebSearchViewModel;

namespace NekoDex.Services.Scrapers
{
    /// <summary>
    /// HTML-Katalog mit Browser-Headern, Sitzungs-Cookies und Anti-Bot-Meldung statt Absturz.
    /// gallery-dl bekommt die kanonische Galerie-URL der Quelle.
    /// </summary>
    public abstract class BrowserListingProvider : IScraperProvider, ITagCatalogSource, ISearchPageHint
    {
        private readonly CookieContainer _cookies = new();
        private readonly HttpClient _http;
        private int _warmed;
        private bool _useGalleryDlAgent;
        private bool _hasClearance;
        private bool _cookieRetried;
        private string? _blockMessage;

        public bool HasFurtherPages { get; protected set; }

        protected BrowserListingProvider()
        {
            _http = SiteHttp.CreateBrowser(_cookies);
        }

        public abstract SourceProvider Provider { get; }

        public abstract string BaseUrl { get; }

        public string SiteId => SiteKey;

        public virtual bool SupportsTagSearch => true;

        public WebSearchViewModel? TagBlacklist { get; set; }

        public string CookieHost => new Uri(BaseUrl).Host;

        protected abstract string SiteKey { get; }

        protected abstract string SiteTitle { get; }

        protected abstract Regex CardLink { get; }

        protected abstract Regex IdPattern { get; }

        protected virtual int TagPageCap => 40;

        protected abstract string GalleryUrl(string id, string? slug);

        public abstract string BuildSearchUrl(string query, int page);

        protected abstract IReadOnlyList<(string Url, string Kind)> TagRoots(bool fullIndex);

        protected virtual IReadOnlyList<(string Url, string Kind)>? LetterRoots(bool fullIndex) => null;

        protected virtual bool AcceptId(string id) => id.Length > 0;

        /// <summary>Ein erster Abruf der Startseite setzt Sitzungs-Cookies, bevor Suche und Tag-Index laufen.</summary>
        protected virtual bool WarmSession => false;

        /// <summary>Kürzeres Limit, damit eine hängende Cloudflare-Antwort den Tag-Lauf nicht offen hält.</summary>
        protected virtual TimeSpan? RequestTimeout => null;

        /// <summary>Letzte Anti-Bot- oder Erreichbarkeitsmeldung, sonst leer.</summary>
        public string? BlockStatus => _blockMessage;

        /// <summary>
        /// Übernimmt gespeicherte Sitzungs-Cookies und, falls vorhanden, die Netscape-Datei von gallery-dl.
        /// Liegt ein Cookie (zum Beispiel <c>cf_clearance</c>), gehen die nächsten Abrufe mit dem User-Agent von gallery-dl.
        /// </summary>
        public void ApplyGalleryDlSession(IEnumerable<MangaLibraryApp.StoredHttpCookie>? stored)
        {
            _warmed = 0;
            _blockMessage = null;
            _cookieRetried = false;
            _hasClearance = false;
            var added = ApplySessionCookies(stored);
            foreach (var path in GalleryDlCookieFiles(CookieHost))
            {
                if (!File.Exists(path))
                    continue;
                try
                {
                    added += ApplySessionCookies(MangaLibraryApp.HttpCookieImport.Parse(File.ReadAllText(path), CookieHost));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Die Cookie-Datei bleibt optional.
                }
            }

            _useGalleryDlAgent = added > 0;
        }

        public int ApplySessionCookies(IEnumerable<MangaLibraryApp.StoredHttpCookie>? cookies)
        {
            if (cookies is null)
                return 0;

            var added = 0;
            var host = CookieHost;
            foreach (var cookie in cookies)
            {
                var domain = (cookie.Domain ?? string.Empty).Trim().TrimStart('.');
                if (!domain.Equals(host, StringComparison.OrdinalIgnoreCase)
                    && !domain.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrWhiteSpace(cookie.Name) || string.IsNullOrWhiteSpace(cookie.Value))
                    continue;
                if (cookie.Name.Equals("cf_clearance", StringComparison.OrdinalIgnoreCase))
                    _hasClearance = true;

                try
                {
                    _cookies.Add(new Uri(BaseUrl), new Cookie(
                        cookie.Name.Trim(),
                        cookie.Value,
                        string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path,
                        domain)
                    {
                        Secure = true,
                    });
                    added++;
                }
                catch (CookieException)
                {
                    // Dasselbe Cookie liegt schon in der Sitzung.
                }
            }

            return added;
        }

        private static IEnumerable<string> GalleryDlCookieFiles(string host)
        {
            var file = "cookies." + host.Replace('.', '_') + ".txt";
            yield return Path.Combine(Path.GetTempPath(), "MangaLibraryApp", file);
            var tools = Path.GetDirectoryName(MangaLibraryApp.GalleryDlService.ToolsPath);
            if (!string.IsNullOrEmpty(tools))
            {
                yield return Path.Combine(tools, file);
                yield return Path.Combine(tools, "cookies.txt");
            }

            var bundled = Path.GetDirectoryName(MangaLibraryApp.GalleryDlService.BundledPath);
            if (!string.IsNullOrEmpty(bundled))
            {
                yield return Path.Combine(bundled, file);
                yield return Path.Combine(bundled, "cookies.txt");
            }
        }

        public virtual async Task<IEnumerable<GalleryResult>> SearchAsync(
            string query,
            int page,
            AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            _ = settings;
            var current = Math.Max(1, page);
            if (current > GalleryPages.SafetyCap)
            {
                HasFurtherPages = false;
                return Array.Empty<GalleryResult>();
            }

            var url = BuildSearchUrl(query, current);
            var html = await FetchAsync(url, cancellationToken).ConfigureAwait(false);
            var cards = ParseCards(html);
            if (cards.Count == 0 && SiteHttp.LooksBlocked(html))
                throw new InvalidOperationException(SiteTitle + " hat die Suche mit einer Anti-Bot-Seite beantwortet.");
            HasFurtherPages = GalleryPages.Continues(cards.Count, current, html, url);
            return cards;
        }

        public virtual async Task<GalleryResult> GetGalleryDetailsAsync(string galleryIdOrUrl, CancellationToken cancellationToken = default)
        {
            var id = GalleryIds.Require(galleryIdOrUrl, IdPattern, SiteTitle);
            var galleryUrl = GalleryUrl(id, null);
            var html = await FetchAsync(galleryUrl, cancellationToken).ConfigureAwait(false);
            if (SiteHttp.LooksBlocked(html))
                throw new InvalidOperationException(SiteTitle + " hat die Galerie mit einer Anti-Bot-Seite beantwortet.");

            var details = GalleryPage.Read(html, BaseUrl, "tag");
            return new GalleryResult
            {
                Title = details.Title.Length > 0 ? details.Title : "Galerie " + id,
                CoverUrl = details.Cover,
                GalleryUrl = galleryUrl,
                GalleryId = id,
                PageCount = details.Pages,
                Tags = details.Tags,
                SourceSite = SiteId,
            };
        }

        public virtual async Task<IReadOnlyList<CatalogTag>> GetOnlineTagsAsync(
            bool fullIndex = false,
            IProgress<TagDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var bag = new Dictionary<string, CatalogTag>(StringComparer.Ordinal);
            await CollectHtmlTagsAsync(bag, fullIndex, progress, cancellationToken).ConfigureAwait(false);
            if (bag.Count == 0 && fullIndex)
                throw new InvalidOperationException(EmptyCatalogMessage());
            return Order(bag);
        }

        protected async Task<string> FetchAsync(string url, CancellationToken cancellationToken, string? accept = null)
        {
            await EnsureWarmAsync(cancellationToken).ConfigureAwait(false);
            // Die Startseite darf den Abruf der eigentlichen URL nicht dauerhaft sperren.
            _blockMessage = null;
            try
            {
                return await DownloadOnceAsync(url, cancellationToken, accept).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (IsAntiBotMessage(ex.Message) && _useGalleryDlAgent && !_cookieRetried)
            {
                _cookieRetried = true;
                _blockMessage = null;
                return await DownloadOnceAsync(url, cancellationToken, accept).ConfigureAwait(false);
            }
        }

        private async Task<string> DownloadOnceAsync(string url, CancellationToken cancellationToken, string? accept)
        {
            try
            {
                return await DownloadAsync(url, BaseUrl, cancellationToken, accept).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(SiteTitle + " hat die Anfrage mit einer Anti-Bot-Seite beantwortet.");
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(Unreachable(ex), ex);
            }
        }

        private async Task EnsureWarmAsync(CancellationToken cancellationToken)
        {
            if (!WarmSession || _hasClearance || Interlocked.Exchange(ref _warmed, 1) == 1)
                return;

            try
            {
                await DownloadAsync(BaseUrl, string.Empty, cancellationToken, null).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Der Zielabruf, bei Cookies inklusive cf_clearance, folgt danach.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (HttpRequestException)
            {
            }
        }

        private static bool IsAntiBotMessage(string message) =>
            message.Contains("Anti-Bot", StringComparison.Ordinal);

        private async Task<string> DownloadAsync(string url, string referer, CancellationToken cancellationToken, string? accept)
        {
            if (RequestTimeout is not { } limit)
                return await SiteHttp.GetString(_http, url, referer, cancellationToken, accept, RequestAgent()).ConfigureAwait(false);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(limit);
            return await SiteHttp.GetString(_http, url, referer, linked.Token, accept, RequestAgent()).ConfigureAwait(false);
        }

        private string? RequestAgent() =>
            _useGalleryDlAgent ? MangaLibraryApp.GalleryDl.ChromeUserAgent : null;

        private string Unreachable(HttpRequestException ex)
        {
            var text = (ex.InnerException?.Message ?? string.Empty) + " " + ex.Message;
            if (text.Contains("host", StringComparison.OrdinalIgnoreCase)
                || text.Contains("nicht aufgelöst", StringComparison.OrdinalIgnoreCase)
                || text.Contains("No such host", StringComparison.OrdinalIgnoreCase))
                return SiteTitle + " ist nicht erreichbar (Host unbekannt).";
            return SiteTitle + " ist nicht erreichbar (" + ex.Message + ").";
        }

        protected async Task CollectHtmlTagsAsync(
            Dictionary<string, CatalogTag> bag,
            bool fullIndex,
            IProgress<TagDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            _blockMessage = await TagIndexWalker.WalkAsync(
                _http,
                BaseUrl,
                TagRoots(fullIndex),
                fullIndex ? TagPageCap : 1,
                bag,
                progress,
                SiteTitle,
                cancellationToken).ConfigureAwait(false) ?? _blockMessage;

            var letters = LetterRoots(fullIndex);
            if (fullIndex && letters is { Count: > 0 })
            {
                _blockMessage = await TagIndexWalker.WalkAsync(
                    _http,
                    BaseUrl,
                    letters,
                    8,
                    bag,
                    progress,
                    SiteTitle,
                    cancellationToken).ConfigureAwait(false) ?? _blockMessage;
            }
        }

        protected void ClearBlock() => _blockMessage = null;

        protected void RememberBlock(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                _blockMessage = message;
        }

        protected string EmptyCatalogMessage() =>
            _blockMessage ?? SiteTitle + " lieferte keinen bereinigten Tagkatalog.";

        protected List<GalleryResult> ParseCards(string html)
        {
            var results = new List<GalleryResult>();
            foreach (var card in ListingCards.Read(html, CardLink, BaseUrl))
            {
                if (!AcceptId(card.Id))
                    continue;
                if (TagBlacklist is { } filter && !filter.Allows(card.Tags))
                    continue;
                results.Add(new GalleryResult
                {
                    Title = card.Title,
                    CoverUrl = card.Cover,
                    GalleryUrl = GalleryUrl(card.Id, card.Slug),
                    GalleryId = card.Id,
                    PageCount = card.Pages,
                    Tags = card.Tags,
                    SourceSite = SiteId,
                });
            }

            return results;
        }

        protected static List<CatalogTag> Order(Dictionary<string, CatalogTag> bag) =>
            bag.Values
                .OrderBy(tag => tag.Category, StringComparer.Ordinal)
                .ThenByDescending(tag => tag.Count)
                .ThenBy(tag => tag.Name, StringComparer.Ordinal)
                .ToList();

        protected static List<(string Url, string Kind)> FoxFamilyRoots(string origin, bool fullIndex)
        {
            var roots = new List<(string Url, string Kind)>
            {
                (origin + "tags/", "tag"),
                (origin + "artists/", "artist"),
                (origin + "characters/", "character"),
                (origin + "parodies/", "parody"),
                (origin + "groups/", "group"),
                (origin + "categories/", "category"),
            };
            if (!fullIndex)
                return roots.Take(4).ToList();
            return roots;
        }

        protected static List<(string Url, string Kind)> LetterTagRoots(string tagsRoot)
        {
            var roots = new List<(string Url, string Kind)> { (tagsRoot + "num/", "tag") };
            for (var letter = 'a'; letter <= 'z'; letter++)
                roots.Add((tagsRoot + letter + "/", "tag"));
            return roots;
        }
    }
}
