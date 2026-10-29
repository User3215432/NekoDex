using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TagDownloadProgress = MangaLibraryApp.TagDownloadProgress;
using TagNames = MangaLibraryApp.TagNames;
using WebSearchViewModel = MangaLibraryApp.WebSearchViewModel;

namespace NekoDex.Services.Scrapers
{
    /// <summary>Ein bereinigter Katalogeintrag für <c>online_site_tags</c>.</summary>
    public sealed record CatalogTag(string Name, string Category, int Count);

    /// <summary>Quellen, die Suche und Tagkatalog selbst laden, statt über die bestehende API-Suche.</summary>
    public interface ITagCatalogSource
    {
        string SiteId { get; }

        WebSearchViewModel? TagBlacklist { get; set; }

        Task<IReadOnlyList<CatalogTag>> GetOnlineTagsAsync(
            bool fullIndex = false,
            IProgress<TagDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default);
    }

    /// <summary>Dieselbe Bereinigung wie beim Hitomi-Katalog: Mindestzahl, keine reinen Zahlen, keine kryptischen Zeichen.</summary>
    internal static class TagSanitizer
    {
        public const int MinCount = 3;

        public static string? FormatTag(string? kind, string? raw, string? slug = null)
        {
            var decoded = WebUtility.HtmlDecode(raw ?? string.Empty).Trim();
            var (category, name) = TagNames.Split(decoded);
            if (category.Length == 0)
                category = CategoryFromSlug(slug);
            if (category.Length == 0)
                category = MapKind(kind);
            if (name.Length == 0)
                name = TagNames.CleanName(decoded);
            if (name.Length == 0 || (name.Length < 2 && name.All(ch => ch <= 127)))
                return null;
            if (!IsClean(name))
                return null;
            return TagNames.Format(category, name);
        }

        public static void RememberMany(Dictionary<string, CatalogTag> bag, IEnumerable<CatalogPage.Found> found)
        {
            foreach (var item in found)
                Remember(bag, item.Kind, item.Name, item.Count, item.HasCount, item.Slug);
        }

        public static void Add(Dictionary<string, CatalogTag> bag, string kind, string raw, int count, bool hasCount, string? slug = null) =>
            Remember(bag, kind, raw, count, hasCount, slug);

        public static string MapKind(string? kind)
        {
            if (string.IsNullOrWhiteSpace(kind))
                return string.Empty;
            if (TagNames.CanonicalCategory(kind) is { } known)
                return known;
            return kind.Trim().ToLowerInvariant() switch
            {
                "collection" => "collection",
                "scanlator" or "scanlators" => "group",
                "translator" or "translators" => "translator",
                "magazine" => "parody",
                _ => string.Empty,
            };
        }

        public static bool IsClean(string name)
        {
            if (name.Length == 0)
                return false;

            var digitsOnly = true;
            var hasLetter = false;
            foreach (var ch in name)
            {
                if (ch is ' ' or '-' or '\'' or '.')
                {
                    digitsOnly = false;
                    continue;
                }

                if (char.IsDigit(ch))
                    continue;
                if (char.IsLetter(ch))
                {
                    hasLetter = true;
                    digitsOnly = false;
                    continue;
                }

                return false;
            }

            return hasLetter && !digitsOnly;
        }

        private static void Remember(Dictionary<string, CatalogTag> bag, string kind, string raw, int count, bool hasCount, string? slug)
        {
            // Eine Zahl im Quelltext unter 3 bleibt draußen. Fehlt die Zahl, wird der Tag trotzdem gespeichert.
            if (hasCount && count < MinCount)
                return;

            var formatted = FormatTag(kind, raw, slug);
            if (formatted is null)
                return;

            var (category, name) = TagNames.Split(formatted);
            if (name.Length == 0)
                return;

            var stored = hasCount ? count : 1;
            var key = category + "\0" + name;
            if (!bag.TryGetValue(key, out var existing) || stored > existing.Count)
                bag[key] = new CatalogTag(name, category, stored);
        }

        private static string CategoryFromSlug(string? slug)
        {
            if (string.IsNullOrWhiteSpace(slug))
                return string.Empty;
            foreach (var prefix in new[] { "female", "male", "mixed", "other" })
            {
                if (slug.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)
                    || slug.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase))
                    return prefix;
            }

            return string.Empty;
        }
    }

    internal static class SearchText
    {
        public static string Prepare(string? query, WebSearchViewModel? blacklist)
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
                if (blacklist is { } filter && !filter.Allows(new[] { name }))
                    continue;
                kept.Add(name);
            }

            return string.Join(' ', kept);
        }
    }

    internal static class SiteHttp
    {
        public const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        private const string SecChUa = "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"";

        public static HttpClient Create()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9,de;q=0.8");
            return client;
        }

        /// <summary>Browser-Client mit Cookie-Container, damit eine Sitzung über mehrere Abrufe erhalten bleibt.</summary>
        public static HttpClient CreateBrowser(CookieContainer cookies)
        {
            var handler = new SocketsHttpHandler
            {
                CookieContainer = cookies,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9,de;q=0.8");
            return client;
        }

        public static void ApplyBrowserHeaders(HttpRequestMessage request, string referer, string? accept = null, string? userAgent = null)
        {
            var agent = string.IsNullOrWhiteSpace(userAgent) ? UserAgent : userAgent;
            var chrome124 = agent.Contains("Chrome/124", StringComparison.Ordinal);
            request.Headers.TryAddWithoutValidation("User-Agent", agent);
            request.Headers.TryAddWithoutValidation("Accept", accept ?? "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9,de;q=0.8");
            request.Headers.TryAddWithoutValidation("Sec-Ch-Ua", chrome124
                ? "\"Chromium\";v=\"124\", \"Google Chrome\";v=\"124\", \"Not-A.Brand\";v=\"99\""
                : SecChUa);
            request.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Mobile", "?0");
            request.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Platform", "\"Windows\"");
            request.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Platform-Version", "\"15.0.0\"");
            request.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Arch", "\"x86\"");
            request.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Bitness", "\"64\"");
            request.Headers.TryAddWithoutValidation("Sec-Ch-Ua-Full-Version-List", chrome124
                ? "\"Chromium\";v=\"124.0.6367.119\", \"Google Chrome\";v=\"124.0.6367.119\", \"Not-A.Brand\";v=\"99.0.0.0\""
                : "\"Google Chrome\";v=\"131.0.6778.205\", \"Chromium\";v=\"131.0.6778.205\", \"Not_A Brand\";v=\"24.0.0.0\"");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
            request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
            request.Headers.TryAddWithoutValidation("Cache-Control", "max-age=0");
            request.Headers.TryAddWithoutValidation("Priority", "u=0, i");
            if (!Uri.TryCreate(referer, UriKind.Absolute, out var uri))
            {
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
                return;
            }

            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
            request.Headers.Referrer = uri;
            request.Headers.TryAddWithoutValidation("Origin", uri.GetLeftPart(UriPartial.Authority));
        }

        public static async Task<string> GetString(HttpClient http, string url, string referer, CancellationToken cancellationToken, string? accept = null, string? userAgent = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyBrowserHeaders(request, referer, accept, userAgent);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (HasCatalogMarkup(body))
                return body;
            var denied = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests;
            if (IsAntiBotPage(body) || denied)
                throw new InvalidOperationException(Host(url) + " hat die Anfrage mit einer Anti-Bot-Seite beantwortet.");
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("HTTP " + (int)response.StatusCode + " von " + Host(url));
            return body;
        }

        public static async Task<string?> TryGet(HttpClient http, string url, string referer, CancellationToken cancellationToken)
        {
            try
            {
                return await GetString(http, url, referer, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        public static bool LooksBlocked(string html) => IsAntiBotPage(html);

        /// <summary>
        /// Echte Cloudflare-Sperre. Ein nachgeladenes <c>challenge-platform</c>-Skript auf einer Katalogseite zählt nicht.
        /// </summary>
        public static bool IsAntiBotPage(string? html)
        {
            if (string.IsNullOrEmpty(html) || HasCatalogMarkup(html))
                return false;
            if (html.Contains("parklogic.com", StringComparison.OrdinalIgnoreCase))
                return true;
            if (html.Contains("Attention Required! | Cloudflare", StringComparison.OrdinalIgnoreCase))
                return true;
            if (html.Contains("<title>Just a moment", StringComparison.OrdinalIgnoreCase))
                return true;
            if (html.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase) && html.Length < 12_000)
                return true;
            return html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
                && html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase)
                && html.Length < 30_000;
        }

        /// <summary>Inertia-Katalog, Galerie-Links oder eine normale Seitentitelzeile. Kein reines Challenge-HTML.</summary>
        public static bool HasCatalogMarkup(string html)
        {
            if (html.Contains("data-page=", StringComparison.OrdinalIgnoreCase))
                return true;
            if (html.Contains("/doujinshi/", StringComparison.OrdinalIgnoreCase))
                return true;
            if (html.Contains("/gallery/", StringComparison.OrdinalIgnoreCase) && html.Length > 8_000)
                return true;
            if (html.Contains("/view/", StringComparison.OrdinalIgnoreCase) && html.Length > 8_000)
                return true;
            return false;
        }

        private static string Host(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "Die Website";

        public static string? Absolute(string? url, string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(url) || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return null;
            if (url.StartsWith("//", StringComparison.Ordinal))
                return "https:" + url;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return url;
            return Uri.TryCreate(new Uri(baseUrl), url, out var absolute) ? absolute.ToString() : null;
        }
    }

    internal static class CatalogPage
    {
        public readonly record struct Found(string Kind, string Name, string? Slug, int Count, bool HasCount);

        private static readonly Regex Link = new(
            """href=(?<q>["'])(?<href>.*?)\k<q>[^>]*>(?<inner>.*?)</a>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex Markup = new("<[^>]+>", RegexOptions.Compiled);

        private static readonly Regex KindSlug = new(
            """/(?<kind>tag|tags|artist|artists|character|characters|parody|parodies|group|groups|category|categories|language|languages|circle|circles|content|contents|convention|conventions|collection|collections)/(?<slug>[^"'#?/]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex QueryName = new(
            """[?&](?<kind>Tag|Artist|Character|Parody|Group|Category|Collection|Female|Male)=(?<name>[^&"'#]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PrefixedQuery = new(
            """[?&]q=(?<kind>[A-Za-z]{2,20}):(?<name>[^&#]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex UsedCount = new(
            """^(?<name>.+?)\s*\(\s*used\s+(?<count>\d[\d,\.\s]*)\s+times\s*\)\s*$""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TrailingCount = new(
            """^(?<name>.+?)\s*\((?<count>\d[\d,\.\s]*)\)\s*$""",
            RegexOptions.Compiled);

        private static readonly Regex NearbyCount = new(
            """<(?:span|small|em)\b[^>]*class=["'][^"']*(?:badge|count|qty|num)[^"']*["'][^>]*>\s*(?<count>\d{1,3}(?:[,\s]\d{3})+|\d+)\s*<""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

        private static readonly HashSet<string> Navigation = new(StringComparer.OrdinalIgnoreCase)
        {
            "home", "random", "login", "register", "signup", "tags", "tag", "artists", "artist",
            "characters", "character", "parodies", "parody", "groups", "group", "categories",
            "category", "next", "prev", "previous", "search", "popular", "latest", "more",
            "languages", "language", "conventions", "convention",
        };

        public static List<Found> Read(string html, string defaultKind)
        {
            var list = new List<Found>();
            foreach (Match match in Link.Matches(html))
            {
                var inner = match.Groups["inner"].Value;
                if (inner.Length > 400)
                    continue;

                var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
                if (href.Length == 0 || href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (href.Contains("/gallery/", StringComparison.OrdinalIgnoreCase)
                    || href.Contains("/entry/", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (href.Contains("/g/", StringComparison.OrdinalIgnoreCase)
                    && !href.Contains("/tag", StringComparison.OrdinalIgnoreCase)
                    && !href.Contains("/tags", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = Whitespace.Replace(WebUtility.HtmlDecode(Markup.Replace(inner, " ")), " ").Trim();
                var kind = defaultKind;
                string? slug = null;
                var path = KindSlug.Match(href);
                if (path.Success)
                {
                    kind = path.Groups["kind"].Value;
                    slug = Uri.UnescapeDataString(path.Groups["slug"].Value);
                }
                else
                {
                    var query = QueryName.Match(href);
                    if (query.Success)
                    {
                        kind = query.Groups["kind"].Value;
                        slug = Uri.UnescapeDataString(query.Groups["name"].Value.Replace('+', ' '));
                    }
                    else
                    {
                        var decodedHref = href.Replace('+', ' ');
                        try
                        {
                            decodedHref = Uri.UnescapeDataString(decodedHref);
                        }
                        catch (UriFormatException)
                        {
                            // Der Link bleibt im Rohtext.
                        }

                        var prefixed = PrefixedQuery.Match(decodedHref);
                        if (!prefixed.Success)
                            continue;
                        kind = prefixed.Groups["kind"].Value;
                        slug = prefixed.Groups["name"].Value.Trim().Trim('"').Replace('+', ' ');
                    }

                    if (name.Length == 0)
                        name = slug ?? string.Empty;
                }

                var hasCount = false;
                var count = 0;
                var used = UsedCount.Match(name);
                if (used.Success && TryCount(used.Groups["count"].Value, out count))
                {
                    name = used.Groups["name"].Value.Trim();
                    hasCount = true;
                }

                var trailing = TrailingCount.Match(name);
                if (!hasCount && trailing.Success && TryCount(trailing.Groups["count"].Value, out count))
                {
                    name = trailing.Groups["name"].Value.Trim();
                    hasCount = true;
                }
                else if (!hasCount)
                {
                    var badge = NearbyCount.Match(inner);
                    if (!badge.Success)
                    {
                        var tailStart = match.Index + match.Length;
                        if (tailStart < html.Length)
                            badge = NearbyCount.Match(html.Substring(tailStart, Math.Min(80, html.Length - tailStart)));
                    }

                    if (badge.Success && TryCount(badge.Groups["count"].Value, out count))
                        hasCount = true;
                }

                if (name.Length == 0 || Navigation.Contains(name))
                    continue;
                list.Add(new Found(kind, name, slug, count, hasCount));
            }

            return list;
        }

        public static List<Found> ReadJson(string body, string defaultKind)
        {
            var trimmed = body.TrimStart();
            if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
                return new List<Found>();

            try
            {
                using var doc = JsonDocument.Parse(body);
                var list = new List<Found>();
                Walk(doc.RootElement, defaultKind, list);
                return list;
            }
            catch (JsonException)
            {
                return new List<Found>();
            }
        }

        private static void Walk(JsonElement element, string defaultKind, List<Found> list)
        {
            if (list.Count >= 8000)
                return;

            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Walk(item, defaultKind, list);
                    break;
                case JsonValueKind.Object:
                    if (TryTag(element, defaultKind, out var found))
                        list.Add(found);
                    foreach (var property in element.EnumerateObject())
                        Walk(property.Value, defaultKind, list);
                    break;
            }
        }

        private static bool TryTag(JsonElement element, string defaultKind, out Found found)
        {
            found = default;
            if (JsonValues.Has(element, "Thumbnail", "ThumbnailUrl", "Pages", "PageCount", "GalleryUrl"))
                return false;

            var name = JsonValues.String(element, "Text", "Name", "Tag", "Value", "Label");
            if (name.Length == 0)
                return false;

            var kind = JsonValues.String(element, "Type", "type", "Category", "Namespace");
            if (kind.Length == 0)
                kind = defaultKind;
            var count = JsonValues.OptionalInt(element, "Count", "count", "Uses", "GalleryCount", "Weight");
            found = new Found(kind, name, null, count ?? 0, count is not null);
            return true;
        }

        private static bool TryCount(string raw, out int count)
        {
            var digits = raw.Replace(",", string.Empty).Replace(".", string.Empty).Replace(" ", string.Empty);
            return int.TryParse(digits, out count) && count > 0;
        }
    }

    internal static class ListingCards
    {
        public readonly record struct Card(string Id, string? Slug, string Title, string? Cover, int? Pages, IReadOnlyList<string> Tags);

        private static readonly Regex Image = new("""<img\b[^>]*>""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Attribute = new(
            """(?<key>data-src|data-original|data-lazy-src|data-lazy|src|alt)=["'](?<value>[^"']*)["']""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Title = new(
            """<(?:div|h2|h3|span|a)\b[^>]*class=["'][^"']*(?:title|caption|name)[^"']*["'][^>]*>\s*(?<title>[^<]{2,200})""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Pages = new(
            """(?i)(?<pages>\d{1,4})\s*pages?|pages?\s*:?\s*(?<count>\d{1,4})""",
            RegexOptions.Compiled);

        public static List<Card> Read(string html, Regex link, string baseUrl)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cards = new List<Card>();
            foreach (Match match in link.Matches(html))
            {
                var id = match.Groups["id"].Value;
                if (id.Length == 0 || id.Length > 96 || seen.Contains(id))
                    continue;

                var cursor = match.Index + match.Length;
                var next = link.Match(html, cursor);
                while (next.Success && next.Groups["id"].Value == id)
                {
                    cursor = next.Index + next.Length;
                    next = link.Match(html, cursor);
                }

                var end = next.Success
                    ? Math.Min(next.Index, match.Index + 2500)
                    : Math.Min(html.Length, match.Index + 2500);
                if (end <= match.Index)
                    continue;
                var window = html.Substring(match.Index, end - match.Index);
                var cover = CoverFrom(window, baseUrl);
                var titleMatch = Title.Match(window);
                var title = titleMatch.Success
                    ? WebUtility.HtmlDecode(titleMatch.Groups["title"].Value).Trim()
                    : AltFrom(window);
                var hasTitle = title.Length >= 2
                    && !title.Equals("thumb", StringComparison.OrdinalIgnoreCase)
                    && !title.Equals("cover", StringComparison.OrdinalIgnoreCase);
                if (cover is null && !hasTitle)
                    continue;

                seen.Add(id);
                if (!hasTitle)
                    title = "Galerie " + id;

                var slug = match.Groups["slug"].Success ? match.Groups["slug"].Value.Trim().Trim('/') : null;
                if (slug is { Length: 0 })
                    slug = null;

                var tags = new List<string>();
                var tagSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var found in CatalogPage.Read(window, "tag"))
                {
                    var formatted = TagSanitizer.FormatTag(found.Kind, found.Name, found.Slug);
                    if (formatted is null || !tagSeen.Add(formatted))
                        continue;
                    tags.Add(formatted);
                    if (tags.Count == 16)
                        break;
                }

                int? pages = null;
                var pageMatch = Pages.Match(window);
                if (pageMatch.Success)
                {
                    var raw = pageMatch.Groups["pages"].Success ? pageMatch.Groups["pages"].Value : pageMatch.Groups["count"].Value;
                    if (int.TryParse(raw, out var parsed) && parsed > 0)
                        pages = parsed;
                }

                cards.Add(new Card(id, slug, title, cover, pages, tags));
            }

            return cards;
        }

        private static string? CoverFrom(string window, string baseUrl)
        {
            foreach (Match image in Image.Matches(window))
            {
                string? dataSrc = null;
                string? src = null;
                foreach (Match attr in Attribute.Matches(image.Value))
                {
                    var key = attr.Groups["key"].Value;
                    var value = WebUtility.HtmlDecode(attr.Groups["value"].Value).Trim();
                    if (value.Length == 0)
                        continue;
                    if (key.Equals("src", StringComparison.OrdinalIgnoreCase))
                        src = value;
                    else if (!key.Equals("alt", StringComparison.OrdinalIgnoreCase))
                        dataSrc ??= value;
                }

                var cover = SiteHttp.Absolute(dataSrc ?? src, baseUrl);
                if (cover is null)
                    continue;
                if (cover.Contains("logo", StringComparison.OrdinalIgnoreCase)
                    || cover.Contains("favicon", StringComparison.OrdinalIgnoreCase)
                    || cover.Contains("blank.", StringComparison.OrdinalIgnoreCase)
                    || cover.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                    continue;
                return cover;
            }

            return null;
        }

        private static string AltFrom(string window)
        {
            var image = Image.Match(window);
            if (!image.Success)
                return string.Empty;
            foreach (Match attr in Attribute.Matches(image.Value))
            {
                if (!attr.Groups["key"].Value.Equals("alt", StringComparison.OrdinalIgnoreCase))
                    continue;
                return WebUtility.HtmlDecode(attr.Groups["value"].Value).Trim();
            }

            return string.Empty;
        }
    }

    internal static class GalleryPage
    {
        public readonly record struct Details(string Title, string? Cover, int? Pages, IReadOnlyList<string> Tags);

        private static readonly Regex Heading = new(
            """<h1\b[^>]*>(?<inner>.*?)</h1>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex Markup = new("<[^>]+>", RegexOptions.Compiled);

        private static readonly Regex MetaImage = new(
            """<meta\b[^>]*(?:property|name)=["'](?:og:image|twitter:image)["'][^>]*content=["'](?<url>[^"']+)["']""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MetaImageReverse = new(
            """<meta\b[^>]*content=["'](?<url>[^"']+)["'][^>]*(?:property|name)=["'](?:og:image|twitter:image)["']""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PagesLabel = new(
            """(?i)pages?\s*:?\s*(?<pages>\d{1,4})""",
            RegexOptions.Compiled);

        private static readonly Regex PagesWord = new(
            """(?i)(?<pages>\d{1,4})\s*pages?""",
            RegexOptions.Compiled);

        public static Details Read(string html, string baseUrl, string defaultKind)
        {
            var heading = Heading.Match(html);
            var title = heading.Success
                ? WebUtility.HtmlDecode(Markup.Replace(heading.Groups["inner"].Value, string.Empty)).Trim()
                : string.Empty;

            string? cover = null;
            var meta = MetaImage.Match(html);
            if (!meta.Success)
                meta = MetaImageReverse.Match(html);
            if (meta.Success)
                cover = SiteHttp.Absolute(meta.Groups["url"].Value, baseUrl);

            int? pages = null;
            var pageMatch = PagesLabel.Match(html);
            if (!pageMatch.Success)
                pageMatch = PagesWord.Match(html);
            if (pageMatch.Success && int.TryParse(pageMatch.Groups["pages"].Value, out var parsed) && parsed > 0)
                pages = parsed;

            var tags = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var found in CatalogPage.Read(html, defaultKind))
            {
                var formatted = TagSanitizer.FormatTag(found.Kind, found.Name, found.Slug);
                if (formatted is null || !seen.Add(formatted))
                    continue;
                tags.Add(formatted);
            }

            foreach (var found in CatalogPage.ReadJson(html, defaultKind))
            {
                var formatted = TagSanitizer.FormatTag(found.Kind, found.Name, found.Slug);
                if (formatted is null || !seen.Add(formatted))
                    continue;
                tags.Add(formatted);
            }

            return new Details(title, cover, pages, tags);
        }
    }

    internal static class JsonValues
    {
        public static string String(JsonElement element, params string[] names)
        {
            foreach (var name in names)
            {
                if (!element.TryGetProperty(name, out var value))
                    continue;
                if (value.ValueKind == JsonValueKind.String)
                    return value.GetString()?.Trim() ?? string.Empty;
                if (value.ValueKind == JsonValueKind.Number)
                    return value.ToString();
            }

            return string.Empty;
        }

        public static int? OptionalInt(JsonElement element, params string[] names)
        {
            foreach (var name in names)
            {
                if (!element.TryGetProperty(name, out var value))
                    continue;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                    return number;
                if (value.ValueKind == JsonValueKind.String
                    && int.TryParse((value.GetString() ?? string.Empty).Replace(",", string.Empty), out var parsed))
                    return parsed;
            }

            return null;
        }

        public static bool Has(JsonElement element, params string[] names)
        {
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out _))
                    return true;
            }

            return false;
        }
    }

    internal static class TagIndexWalker
    {
        public static async Task<string?> WalkAsync(
            HttpClient http,
            string referer,
            IReadOnlyList<(string Url, string Kind)> roots,
            int maxPages,
            Dictionary<string, CatalogTag> bag,
            IProgress<TagDownloadProgress>? progress,
            string siteTitle,
            CancellationToken cancellationToken)
        {
            var total = Math.Max(1, roots.Count);
            var completed = 0;
            string? blockedMessage = null;
            foreach (var (url, kind) in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? previousPage = null;
                for (var page = 1; page <= Math.Max(1, maxPages); page++)
                {
                    List<CatalogPage.Found>? found = null;
                    string? fingerprint = null;
                    foreach (var candidate in PageUrls(url, page))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string? html;
                        try
                        {
                            html = await SiteHttp.GetString(http, candidate, referer, cancellationToken).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException ex)
                        {
                            blockedMessage = ex.Message;
                            progress?.Report(new TagDownloadProgress(completed, total, ex.Message, bag.Count));
                            continue;
                        }
                        catch (HttpRequestException)
                        {
                            continue;
                        }

                        if (SiteHttp.LooksBlocked(html))
                        {
                            blockedMessage ??= siteTitle + " hat die Anfrage mit einer Anti-Bot-Seite beantwortet.";
                            progress?.Report(new TagDownloadProgress(completed, total, blockedMessage, bag.Count));
                            continue;
                        }

                        var pageTags = CatalogPage.Read(html, kind);
                        pageTags.AddRange(CatalogPage.ReadJson(html, kind));
                        if (pageTags.Count == 0)
                            continue;

                        var candidateFingerprint = PageFingerprint(pageTags);
                        if (candidateFingerprint == previousPage)
                            continue;
                        if (found is null || pageTags.Count > found.Count)
                        {
                            found = pageTags;
                            fingerprint = candidateFingerprint;
                        }
                    }

                    progress?.Report(new TagDownloadProgress(completed, total, siteTitle + ": " + kind + " Seite " + page, bag.Count));
                    if (found is null || fingerprint is null)
                        break;
                    previousPage = fingerprint;
                    TagSanitizer.RememberMany(bag, found);
                    if (page < maxPages)
                        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }

                completed++;
                progress?.Report(new TagDownloadProgress(completed, total, siteTitle + ": " + kind, bag.Count));
            }

            return blockedMessage;
        }

        private static string PageFingerprint(List<CatalogPage.Found> found)
        {
            var take = Math.Min(40, found.Count);
            var parts = new string[take];
            for (var i = 0; i < take; i++)
                parts[i] = found[i].Kind + "\0" + found[i].Name;
            return string.Join("\n", parts);
        }

        private static IEnumerable<string> PageUrls(string root, int page)
        {
            if (root.Contains("{page}", StringComparison.Ordinal))
            {
                yield return root.Replace("{page}", page.ToString(), StringComparison.Ordinal);
                yield break;
            }

            var joiner = root.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            yield return root + joiner + "page=" + page.ToString();
            if (page == 1)
                yield return root;
            else if (root.EndsWith('/'))
                yield return root + "page/" + page.ToString() + "/";
        }
    }

    internal static class GalleryIds
    {
        public static string Require(string? value, Regex pattern, string site)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length > 0 && text.All(char.IsDigit))
                return text;

            var match = pattern.Match(text);
            if (!match.Success)
                throw new ArgumentException(site + ": keine Galerie-ID in \"" + text + "\".");
            return match.Groups["id"].Value;
        }
    }
}
