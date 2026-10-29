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

namespace NekoDex.Services.Scrapers
{
    /// <summary>Ein Tag oder eine Kategorie aus dem HentaiFox-Katalog.</summary>
    public sealed record HentaiFoxTag(string Name, string Category, int Count);

    /// <summary>Detailseite einer Galerie, inklusive Vorschaubilder für den Reader.</summary>
    public sealed class HentaiFoxGalleryDetails
    {
        public GalleryResult Gallery { get; init; } = new();

        public IReadOnlyList<string> PreviewUrls { get; init; } = Array.Empty<string>();
    }

    /// <summary>HTML-Suche auf hentaifox.com. nhentai, E-Hentai und Hitomi bleiben auf dem bisherigen Suchpfad.</summary>
    public sealed class HentaiFoxScraperProvider : IScraperProvider, ISearchPageHint
    {
        public const string SiteId = "hentaifox";

        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

        private static readonly HttpClient Http = CreateClient();

        private static readonly Regex GalleryCard = new(
            """<div class="thumb"[^>]*>.*?<a class="t_cat"[^>]*>(?<category>[^<]*)</a>.*?<a href="/gallery/(?<id>\d+)/"><img\b[^>]*\bdata-src="(?<cover>https?://[^"]+)"[^>]*>.*?<h2 class="g_title"><a href="/gallery/\k<id>/">(?<title>[^<]*)</a>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex CatalogTag = new(
            """<a class="tag_btn[^"]*" href="/(?<kind>tag|category)/[^"]+">\s*<h3 class="list_tag">(?<name>[^<]+)</h3>\s*<span class="badge">(?<count>\d+)</span>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex DetailTag = new(
            """href=['"]/(?<kind>tag|category|artist|language|parody|character|group)/[^'"]+['"]>(?<name>[^<]+?)\s*<span class=['"]t_badge['"]>(?<count>\d+)</span>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex DetailTitle = new(
            """<div class="info">\s*<h1>(?<title>[^<]+)</h1>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex DetailCover = new(
            """<div class="cover">\s*<img src="(?<cover>https?://[^"]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex DetailPages = new(
            """<span class="i_text pages">Pages:\s*(?<pages>\d+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PreviewImage = new(
            """data-src="(?<url>https?://[^"]+/\d+t\.jpg)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Dieselbe Blacklist wie <see cref="MangaLibraryApp.WebSearchViewModel"/>. Die Suche filtert darüber, bevor Treffer zurückgehen.</summary>
        public MangaLibraryApp.WebSearchViewModel? TagBlacklist { get; set; }

        public SourceProvider Provider => SourceProvider.HentaiFox;

        public string BaseUrl => "https://hentaifox.com/";

        public bool SupportsTagSearch => true;

        public bool HasFurtherPages { get; private set; }

        public string BuildSearchUrl(string query, int page)
        {
            var text = PrepareQuery(query);
            var n = Math.Max(1, page);
            return BaseUrl + "search/?q=" + Uri.EscapeDataString(text) + "&page=" + n.ToString();
        }

        public async Task<IEnumerable<GalleryResult>> SearchAsync(
            string query,
            int page,
            AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            _ = settings;
            var text = PrepareQuery(query);
            var current = Math.Max(1, page);
            if (text.Length == 0 || current > GalleryPages.SafetyCap)
            {
                HasFurtherPages = false;
                return Array.Empty<GalleryResult>();
            }

            var url = BuildSearchUrl(text, current);
            var html = await GetHtmlAsync(url, cancellationToken).ConfigureAwait(false);
            var hits = new List<GalleryResult>();
            foreach (Match match in GalleryCard.Matches(html))
            {
                var id = match.Groups["id"].Value;
                if (id.Length == 0)
                    continue;

                var category = Decode(match.Groups["category"].Value);
                var tags = category.Length == 0
                    ? Array.Empty<string>()
                    : new[] { "category:" + category };
                if (TagBlacklist is { } filter && !filter.Allows(tags))
                    continue;

                hits.Add(new GalleryResult
                {
                    Title = Decode(match.Groups["title"].Value),
                    CoverUrl = match.Groups["cover"].Value,
                    GalleryUrl = BaseUrl + "gallery/" + id + "/",
                    GalleryId = id,
                    Tags = tags,
                    SourceSite = SiteId,
                });
            }

            HasFurtherPages = GalleryPages.Continues(hits.Count, current, html, url);
            return hits;
        }

        public Task<IReadOnlyList<HentaiFoxTag>> GetOnlineTagsAsync(CancellationToken cancellationToken = default) =>
            GetOnlineTagsAsync(fullIndex: false, progress: null, cancellationToken);

        public async Task<IReadOnlyList<HentaiFoxTag>> GetOnlineTagsAsync(
            bool fullIndex,
            IProgress<MangaLibraryApp.TagDownloadProgress>? progress,
            CancellationToken cancellationToken = default)
        {
            var urls = new List<string> { BaseUrl + "categories/" };
            if (fullIndex)
            {
                urls.Add(BaseUrl + "tags/num/");
                for (var letter = 'a'; letter <= 'z'; letter++)
                    urls.Add(BaseUrl + "tags/" + letter + "/");
            }
            else
                urls.Add(BaseUrl + "tags/");

            var tags = new Dictionary<string, HentaiFoxTag>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < urls.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new MangaLibraryApp.TagDownloadProgress(
                    i, urls.Count, "Lade " + urls[i], tags.Count));
                string html;
                try
                {
                    html = await GetHtmlAsync(urls[i], cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException) when (fullIndex)
                {
                    continue;
                }

                foreach (Match match in CatalogTag.Matches(html))
                {
                    var name = Decode(match.Groups["name"].Value);
                    if (name.Length == 0 || !int.TryParse(match.Groups["count"].Value, out var count) || count <= 0)
                        continue;

                    var category = string.Equals(match.Groups["kind"].Value, "category", StringComparison.OrdinalIgnoreCase)
                        ? "category"
                        : string.Empty;
                    var key = category + "\0" + name;
                    if (!tags.TryGetValue(key, out var existing) || count > existing.Count)
                        tags[key] = new HentaiFoxTag(name, category, count);
                }
            }

            progress?.Report(new MangaLibraryApp.TagDownloadProgress(urls.Count, urls.Count, "Tags geladen", tags.Count));
            return tags.Values
                .OrderByDescending(tag => tag.Count)
                .ThenBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<HentaiFoxGalleryDetails> GetGalleryDetailsAsync(
            string galleryUrl,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(galleryUrl))
                throw new ArgumentException("Galerie-URL fehlt.", nameof(galleryUrl));

            var url = galleryUrl.Trim();
            if (url.StartsWith('/'))
                url = BaseUrl.TrimEnd('/') + url;
            var html = await GetHtmlAsync(url, cancellationToken).ConfigureAwait(false);
            var body = html;
            var related = html.IndexOf("related_galleries", StringComparison.OrdinalIgnoreCase);
            if (related > 0)
                body = html[..related];

            var idMatch = Regex.Match(url, @"/gallery/(\d+)/?", RegexOptions.IgnoreCase);
            var id = idMatch.Success ? idMatch.Groups[1].Value : null;
            var title = DetailTitle.Match(body);
            var cover = DetailCover.Match(body);
            var pages = DetailPages.Match(body);
            int? pageCount = pages.Success && int.TryParse(pages.Groups["pages"].Value, out var parsed) ? parsed : null;

            var tags = new List<string>();
            foreach (Match match in DetailTag.Matches(body))
            {
                var name = Decode(match.Groups["name"].Value);
                if (name.Length == 0)
                    continue;
                var kind = match.Groups["kind"].Value.ToLowerInvariant();
                tags.Add(kind == "tag" ? name : kind + ":" + name);
            }

            var previews = new List<string>();
            foreach (Match match in PreviewImage.Matches(body))
            {
                var preview = match.Groups["url"].Value;
                if (!previews.Contains(preview, StringComparer.OrdinalIgnoreCase))
                    previews.Add(preview);
            }

            return new HentaiFoxGalleryDetails
            {
                Gallery = new GalleryResult
                {
                    Title = title.Success ? Decode(title.Groups["title"].Value) : string.Empty,
                    CoverUrl = cover.Success ? cover.Groups["cover"].Value : null,
                    GalleryUrl = url,
                    GalleryId = id,
                    PageCount = pageCount,
                    Tags = tags,
                    SourceSite = SiteId,
                },
                PreviewUrls = previews,
            };
        }

        private string PrepareQuery(string? query)
        {
            var kept = new List<string>();
            foreach (var part in (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith('-'))
                    continue;

                var name = part;
                var colon = part.IndexOf(':');
                if (colon >= 0 && colon < part.Length - 1)
                    name = part[(colon + 1)..];
                name = name.Replace('_', ' ').Trim();
                if (name.Length == 0)
                    continue;
                if (TagBlacklist is { } filter && !filter.Allows(new[] { name }))
                    continue;
                kept.Add(name);
            }

            return string.Join(' ', kept);
        }

        private async Task<string> GetHtmlAsync(string url, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
            request.Headers.Referrer = new Uri(BaseUrl);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            return client;
        }

        private static string Decode(string value) =>
            WebUtility.HtmlDecode(value).Trim();
    }
}
