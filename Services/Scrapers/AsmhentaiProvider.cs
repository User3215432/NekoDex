using System;
using System.Collections.Generic;
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
    /// Suche und Galerie auf asmhentai.com. Namespace-Tags (female, male, artist, character, parody)
    /// kommen aus den Listenlinks. gallery-dl bekommt <c>https://asmhentai.com/g/{id}/</c>.
    /// </summary>
    public sealed class AsmhentaiProvider : IScraperProvider, ITagCatalogSource, ISearchPageHint
    {
        public const string SiteId = "asmhentai";

        private static readonly CookieContainer Cookies = new();

        private static readonly HttpClient Http = SiteHttp.CreateBrowser(Cookies);

        private static readonly Regex Link = new(
            """href="(?:https?://(?:www\.)?asmhentai\.com)?/(?:g|gallery)/(?<id>\d+)/?(?<slug>[^"#?]*)?""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex IdInUrl = new(
            """/(?:g|gallery)/(?<id>\d+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        string ITagCatalogSource.SiteId => SiteId;

        public WebSearchViewModel? TagBlacklist { get; set; }

        /// <summary>Übernimmt gespeicherte Sitzungs-Cookies für asmhentai.com, falls welche vorliegen.</summary>
        public void ApplySessionCookies(IEnumerable<MangaLibraryApp.StoredHttpCookie>? cookies)
        {
            if (cookies is null)
                return;

            foreach (var cookie in cookies)
            {
                var domain = (cookie.Domain ?? string.Empty).Trim().TrimStart('.');
                if (!domain.Equals("asmhentai.com", StringComparison.OrdinalIgnoreCase)
                    && !domain.EndsWith(".asmhentai.com", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrWhiteSpace(cookie.Name) || string.IsNullOrWhiteSpace(cookie.Value))
                    continue;

                try
                {
                    Cookies.Add(new Uri(BaseUrl), new Cookie(
                        cookie.Name.Trim(),
                        cookie.Value,
                        string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path,
                        domain)
                    {
                        Secure = true,
                    });
                }
                catch (CookieException)
                {
                    // Dasselbe Cookie liegt schon in der Sitzung.
                }
            }
        }

        public SourceProvider Provider => SourceProvider.Asmhentai;

        public string BaseUrl => "https://asmhentai.com/";

        public bool SupportsTagSearch => true;

        public bool HasFurtherPages { get; private set; }

        public static string GalleryUrlFor(string id) => "https://asmhentai.com/g/" + id.Trim() + "/";

        public string BuildSearchUrl(string query, int page)
        {
            var text = SearchText.Prepare(query, TagBlacklist);
            return BaseUrl + "search/?q=" + Uri.EscapeDataString(text) + "&page=" + Math.Max(1, page).ToString();
        }

        public async Task<IEnumerable<GalleryResult>> SearchAsync(
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
            try
            {
                var html = await SiteHttp.GetString(Http, url, BaseUrl, cancellationToken).ConfigureAwait(false);
                var cards = ListingCards.Read(html, Link, BaseUrl);
                if (cards.Count == 0 && SiteHttp.LooksBlocked(html))
                    throw new InvalidOperationException("AsmHentai hat die Suche mit einer Anti-Bot-Seite beantwortet.");
                var hits = ToResults(cards);
                HasFurtherPages = GalleryPages.Continues(hits.Count, current, html, url);
                return hits;
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException("AsmHentai ist nicht erreichbar (" + ex.Message + ").", ex);
            }
        }

        public async Task<GalleryResult> GetGalleryDetailsAsync(string galleryIdOrUrl, CancellationToken cancellationToken = default)
        {
            var id = GalleryIds.Require(galleryIdOrUrl, IdInUrl, "AsmHentai");
            var galleryUrl = GalleryUrlFor(id);
            var html = await SiteHttp.GetString(Http, galleryUrl, BaseUrl, cancellationToken).ConfigureAwait(false);
            if (SiteHttp.LooksBlocked(html))
                throw new InvalidOperationException("AsmHentai hat die Galerie mit einer Anti-Bot-Seite beantwortet.");

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

        public async Task<IReadOnlyList<CatalogTag>> GetOnlineTagsAsync(
            bool fullIndex = false,
            IProgress<TagDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var bag = new Dictionary<string, CatalogTag>(StringComparer.Ordinal);
            await TagIndexWalker.WalkAsync(
                Http,
                BaseUrl,
                CategoryRoots(),
                fullIndex ? 80 : 1,
                bag,
                progress,
                "AsmHentai",
                cancellationToken).ConfigureAwait(false);

            if (fullIndex)
            {
                await TagIndexWalker.WalkAsync(
                    Http,
                    BaseUrl,
                    LetterRoots(),
                    fullIndex ? 8 : 1,
                    bag,
                    progress,
                    "AsmHentai",
                    cancellationToken).ConfigureAwait(false);
            }

            if (bag.Count == 0 && fullIndex)
                throw new InvalidOperationException("AsmHentai lieferte keinen bereinigten Tagkatalog.");

            return bag.Values
                .OrderBy(tag => tag.Category, StringComparer.Ordinal)
                .ThenByDescending(tag => tag.Count)
                .ThenBy(tag => tag.Name, StringComparer.Ordinal)
                .ToList();
        }

        private static List<(string Url, string Kind)> CategoryRoots() => new()
        {
            ("https://asmhentai.com/tags/", "tag"),
            ("https://asmhentai.com/artists/", "artist"),
            ("https://asmhentai.com/characters/", "character"),
            ("https://asmhentai.com/parodies/", "parody"),
            ("https://asmhentai.com/groups/", "group"),
            ("https://asmhentai.com/categories/", "category"),
        };

        private static List<(string Url, string Kind)> LetterRoots()
        {
            var roots = new List<(string Url, string Kind)> { ("https://asmhentai.com/tags/num/", "tag") };
            for (var letter = 'a'; letter <= 'z'; letter++)
                roots.Add(("https://asmhentai.com/tags/" + letter + "/", "tag"));
            return roots;
        }

        private List<GalleryResult> ToResults(IEnumerable<ListingCards.Card> cards)
        {
            var results = new List<GalleryResult>();
            foreach (var card in cards)
            {
                if (TagBlacklist is { } filter && !filter.Allows(card.Tags))
                    continue;
                results.Add(new GalleryResult
                {
                    Title = card.Title,
                    CoverUrl = card.Cover,
                    GalleryUrl = GalleryUrlFor(card.Id),
                    GalleryId = card.Id,
                    PageCount = card.Pages,
                    Tags = card.Tags,
                    SourceSite = SiteId,
                });
            }

            return results;
        }
    }
}
