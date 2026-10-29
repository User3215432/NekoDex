using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using NekoDex.Core.Enums;
using NekoDex.Core.Models;
using TagDownloadProgress = MangaLibraryApp.TagDownloadProgress;
using TagNames = MangaLibraryApp.TagNames;
using TagQuery = MangaLibraryApp.TagQuery;
using WebSearchViewModel = MangaLibraryApp.WebSearchViewModel;

namespace NekoDex.Services.Scrapers
{
    /// <summary>
    /// Gemeinsame E-Hentai-Abfrage (<c>artist:</c>, <c>parody:</c>, <c>character:</c>, <c>tag:</c> über
    /// <see cref="MangaLibraryApp.EHentaiQueryBuilder"/>). Jede Quelle sucht auf ihrer eigenen Domain.
    /// </summary>
    public abstract class EHentaiStyleProvider : CatalogScraperProvider, ITagCatalogSource, ISearchPageHint
    {
        protected readonly HttpClient Http = SiteHttp.Create();

        public WebSearchViewModel? TagBlacklist { get; set; }

        public bool HasFurtherPages { get; protected set; }

        string ITagCatalogSource.SiteId => SiteId;

        protected abstract string DisplayName { get; }

        public abstract override Task<IEnumerable<GalleryResult>> SearchAsync(
            string query,
            int page,
            MangaLibraryApp.AppSettings settings,
            CancellationToken cancellationToken = default);

        public abstract Task<IReadOnlyList<CatalogTag>> GetOnlineTagsAsync(
            bool fullIndex = false,
            IProgress<TagDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default);

        protected async Task<string> GetHtmlAsync(string url, CancellationToken cancellationToken)
        {
            try
            {
                return await SiteHttp.GetString(Http, url, BaseUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                throw new InvalidOperationException(DisplayName + " ist nicht erreichbar.", ex);
            }
        }

        protected static IReadOnlyList<EhTerm> ReadTerms(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Array.Empty<EhTerm>();

            var tokens = query.Contains(' ', StringComparison.Ordinal) || query.Contains(':', StringComparison.Ordinal)
                ? TagQuery.BuildEhentaiQuery(query)
                : new[] { query.Trim() };

            var terms = new List<EhTerm>();
            foreach (var token in tokens)
            {
                var normalized = MangaLibraryApp.EHentaiQueryBuilder.Normalize(token);
                if (normalized.Length == 0)
                    continue;
                var body = normalized.EndsWith('$') ? normalized[..^1] : normalized;
                var colon = body.IndexOf(':');
                if (colon <= 0 || colon >= body.Length - 1)
                    continue;
                var name = body[(colon + 1)..].Replace('_', ' ').Trim();
                if (name.Length == 0)
                    continue;
                terms.Add(new EhTerm(body[..colon], name));
            }

            return terms;
        }

        protected static string FSearch(string? query) =>
            string.Join(' ', ReadTerms(query).Select(term => term.Namespace + ":" + term.Name.Replace(' ', '_') + "$"));

        protected readonly record struct EhTerm(string Namespace, string Name);
    }

    /// <summary>
    /// Lo-Fi-Oberfläche von E-Hentai: <c>https://e-hentai.org/lofi/?f_search=</c>.
    /// gallery-dl bekommt die umgeschriebene Adresse <c>https://e-hentai.org/g/{id}/{token}/</c>.
    /// </summary>
    public sealed class LoFiEHentaiProvider : EHentaiStyleProvider
    {
        public const string Site = "lofiehentai";

        private const string Root = "https://e-hentai.org/lofi/";
        private const int FullPageCap = 30;

        private readonly object _cursorGate = new();
        private string _cursorSearch = string.Empty;
        private readonly Dictionary<int, string?> _nextAfterPage = new();

        protected override string SiteId => Site;

        public override SourceProvider Provider => SourceProvider.LoFiEHentai;

        public override string BaseUrl => Root;

        protected override string DisplayName => "LoFi E-Hentai";

        public override string BuildSearchUrl(string query, int page)
        {
            var search = FSearch(query);
            _ = page;
            return search.Length == 0 ? Root : Root + "?f_search=" + Uri.EscapeDataString(search);
        }

        public override async Task<IEnumerable<GalleryResult>> SearchAsync(
            string query,
            int page,
            MangaLibraryApp.AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            _ = settings;
            var search = FSearch(query);
            var target = Math.Max(1, page);
            if (target > GalleryPages.SafetyCap)
            {
                HasFurtherPages = false;
                return Array.Empty<GalleryResult>();
            }

            var cursor = await CursorForPageAsync(search, target, cancellationToken).ConfigureAwait(false);
            if (target > 1 && cursor is null)
            {
                HasFurtherPages = false;
                return Array.Empty<GalleryResult>();
            }

            var html = await GetHtmlAsync(ListingUrl(search, cursor), cancellationToken).ConfigureAwait(false);
            var hits = ParseGalleries(html);
            var following = NextCursor(html);
            lock (_cursorGate)
                _nextAfterPage[target] = following;
            HasFurtherPages = hits.Count > 0 && following is not null && target < GalleryPages.SafetyCap;
            return hits;
        }

        private async Task<string?> CursorForPageAsync(string search, int target, CancellationToken cancellationToken)
        {
            if (target <= 1)
            {
                lock (_cursorGate)
                {
                    if (!string.Equals(_cursorSearch, search, StringComparison.Ordinal))
                    {
                        _cursorSearch = search;
                        _nextAfterPage.Clear();
                    }
                }

                return null;
            }

            string? step = null;
            for (var index = 1; index < target; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? stored;
                var known = false;
                lock (_cursorGate)
                {
                    if (!string.Equals(_cursorSearch, search, StringComparison.Ordinal))
                    {
                        _cursorSearch = search;
                        _nextAfterPage.Clear();
                    }

                    if (_nextAfterPage.TryGetValue(index, out stored))
                        known = true;
                }

                if (known)
                {
                    if (stored is null)
                        return null;
                    step = stored;
                    continue;
                }

                var html = await GetHtmlAsync(ListingUrl(search, step), cancellationToken).ConfigureAwait(false);
                var next = NextCursor(html);
                lock (_cursorGate)
                    _nextAfterPage[index] = next;
                if (next is null)
                    return null;
                step = next;
            }

            return step;
        }

        public override async Task<IReadOnlyList<CatalogTag>> GetOnlineTagsAsync(
            bool fullIndex = false,
            IProgress<TagDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var pages = fullIndex ? FullPageCap : 1;
            var bag = new Dictionary<string, CatalogTag>(StringComparer.Ordinal);
            string? cursor = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 1; index <= pages; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new TagDownloadProgress(index - 1, pages, "LoFi E-Hentai Seite " + index, bag.Count));
                var html = await GetHtmlAsync(ListingUrl(string.Empty, cursor), cancellationToken).ConfigureAwait(false);
                foreach (var gallery in ParseGalleries(html))
                {
                    foreach (var tag in gallery.Tags)
                        RememberTag(bag, tag);
                }

                cursor = NextCursor(html);
                if (cursor is null || !seen.Add(cursor))
                    break;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            if (fullIndex && bag.Count == 0)
                throw new InvalidOperationException("LoFi E-Hentai hat keinen Tag-Index geliefert.");
            progress?.Report(new TagDownloadProgress(pages, pages, "LoFi E-Hentai", bag.Count));
            return bag.Values.ToList();
        }

        private static string ListingUrl(string search, string? cursor)
        {
            var url = search.Length == 0 ? Root : Root + "?f_search=" + Uri.EscapeDataString(search);
            if (cursor is null)
                return url;
            return url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "next=" + cursor;
        }

        internal static IReadOnlyList<GalleryResult> ParseGalleries(string html)
        {
            var results = new List<GalleryResult>();
            foreach (Match match in GalleryPattern.Matches(html))
            {
                var id = match.Groups["id"].Value;
                var token = match.Groups["token"].Value;
                var tags = ReadTags(match.Groups["tags"].Value);
                var pages = PagePattern.Match(match.Groups["meta"].Value);
                results.Add(new GalleryResult
                {
                    Title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim(),
                    CoverUrl = WebUtility.HtmlDecode(match.Groups["cover"].Value),
                    GalleryUrl = "https://e-hentai.org/lofi/g/" + id + "/" + token + "/",
                    GalleryId = id,
                    PageCount = pages.Success && int.TryParse(pages.Groups[1].Value, out var count) ? count : null,
                    Tags = tags,
                    SourceSite = Site,
                });
            }

            return results;
        }

        private static List<string> ReadTags(string table)
        {
            var tags = new List<string>();
            foreach (Match row in TagRowPattern.Matches(table))
            {
                var kind = row.Groups["ns"].Value.Trim();
                foreach (var raw in row.Groups["names"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var formatted = TagSanitizer.FormatTag(kind, raw);
                    if (formatted is not null)
                        tags.Add(formatted);
                }
            }

            return tags;
        }

        private static void RememberTag(Dictionary<string, CatalogTag> bag, string formatted)
        {
            var (category, name) = TagNames.Split(formatted);
            if (name.Length == 0)
                return;
            TagSanitizer.Add(bag, category.Length == 0 ? "tag" : category, name, 1, hasCount: false);
        }

        private static string? NextCursor(string html)
        {
            var matches = NextPattern.Matches(html);
            return matches.Count == 0 ? null : matches[^1].Groups["id"].Value;
        }

        private static readonly Regex GalleryPattern = new(
            """<img[^>]*\bsrc="(?<cover>https://ehgt\.org/[^"]+)"[^>]*>.{0,500}?<h2>\s*<a href="https://e-hentai\.org/(?:lofi/)?g/(?<id>\d+)/(?<token>[0-9a-f]{10})/">(?<title>.*?)</a>\s*</h2>\s*<p>(?<meta>.*?)</p>\s*<table class="tl">(?<tags>.*?)</table>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex TagRowPattern = new(
            """<tr>\s*<td>\s*(?<ns>[^<:]+):\s*</td>\s*<td>(?<names>.*?)</td>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex PagePattern = new(@"(\d+)\s*p\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NextPattern = new(
            """<a href="[^"]*(?:\?|&(?:amp;)?)next=(?<id>\d+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>
    /// HentaiDB auf <c>https://hentaidb.xyz/</c>. Die Suche läuft über den eingebetteten Katalog
    /// (<c>tag:</c>, <c>studio:</c>); artist/group werden auf das Studio gelegt.
    /// </summary>
    public sealed class HentaiDbProvider : EHentaiStyleProvider
    {
        public const string Site = "hentaidb";

        private const string Root = "https://hentaidb.xyz/";
        private const int PageSize = 30;

        private readonly SemaphoreSlim _gate = new(1, 1);
        private DbCatalog? _catalog;

        protected override string SiteId => Site;

        public override SourceProvider Provider => SourceProvider.HentaiDB;

        public override string BaseUrl => Root;

        protected override string DisplayName => "HentaiDB";

        public override string BuildSearchUrl(string query, int page)
        {
            var text = string.Join(' ', ReadTerms(query).Select(term =>
                term.Namespace is "artist" or "group"
                    ? "studio:\"" + term.Name + "\""
                    : "tag:\"" + term.Name + "\""));
            return Root + "?q=" + Uri.EscapeDataString(text);
        }

        public override async Task<IEnumerable<GalleryResult>> SearchAsync(
            string query,
            int page,
            MangaLibraryApp.AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            _ = settings;
            var start = Math.Max(1, page);
            if (start > GalleryPages.SafetyCap)
            {
                HasFurtherPages = false;
                return Array.Empty<GalleryResult>();
            }

            var catalog = await LoadAsync(null, cancellationToken).ConfigureAwait(false);
            var terms = ReadTerms(query);
            var matched = catalog.Rows
                .Where(row => Matches(catalog, row, terms))
                .OrderBy(row => row.Popularity)
                .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var hits = matched
                .Skip((start - 1) * PageSize)
                .Take(PageSize)
                .Select(row => ToResult(catalog, row))
                .ToList();
            HasFurtherPages = hits.Count > 0 && matched.Count > start * PageSize && start < GalleryPages.SafetyCap;
            return hits;
        }

        public override async Task<IReadOnlyList<CatalogTag>> GetOnlineTagsAsync(
            bool fullIndex = false,
            IProgress<TagDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new TagDownloadProgress(0, 1, "HentaiDB-Katalog", 0));
            var catalog = await LoadAsync(progress, cancellationToken).ConfigureAwait(false);
            var bag = new Dictionary<string, CatalogTag>(StringComparer.Ordinal);
            var uses = new int[catalog.Tags.Length];
            var studios = new int[catalog.Studios.Length];
            foreach (var row in catalog.Rows)
            {
                foreach (var index in row.TagIndexes)
                {
                    if ((uint)index < (uint)uses.Length)
                        uses[index]++;
                }

                if ((uint)row.StudioIndex < (uint)studios.Length)
                    studios[row.StudioIndex]++;
            }

            for (var i = 0; i < catalog.Tags.Length; i++)
                TagSanitizer.Add(bag, "tag", catalog.Tags[i], uses[i], hasCount: true);
            for (var i = 0; i < catalog.Studios.Length; i++)
                TagSanitizer.Add(bag, "artist", catalog.Studios[i], studios[i], hasCount: true);

            if (fullIndex && bag.Count == 0)
                throw new InvalidOperationException("HentaiDB hat keinen Tag-Index geliefert.");
            progress?.Report(new TagDownloadProgress(1, 1, "HentaiDB", bag.Count));
            return bag.Values.ToList();
        }

        private async Task<DbCatalog> LoadAsync(IProgress<TagDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            if (_catalog is not null)
                return _catalog;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_catalog is not null)
                    return _catalog;

                var home = await GetHtmlAsync(Root, cancellationToken).ConfigureAwait(false);
                var scriptUrl = CatalogScriptUrl(home);
                progress?.Report(new TagDownloadProgress(0, 1, "HentaiDB-Katalog wird gelesen", 0));
                var body = await GetHtmlAsync(scriptUrl, cancellationToken).ConfigureAwait(false);
                try
                {
                    using var document = ParseCatalogJson(body);
                    var catalog = DbCatalog.Read(document.RootElement);
                    if (catalog.Rows.Length == 0 || catalog.Tags.Length == 0)
                        throw new InvalidOperationException("HentaiDB-Katalog von https://hentaidb.xyz/ enthält keine Serien oder Tags.");
                    _catalog = catalog;
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException("HentaiDB-Katalog konnte nicht gelesen werden.", ex);
                }

                return _catalog;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static bool Matches(DbCatalog catalog, DbRow row, IReadOnlyList<EhTerm> terms)
        {
            foreach (var term in terms)
            {
                if (term.Namespace is "artist" or "group")
                {
                    if (!Contains(row.Studio, term.Name) && !Contains(row.Title, term.Name))
                        return false;
                    continue;
                }

                if (HasTag(catalog, row, term.Name) || Contains(row.Title, term.Name))
                    continue;
                return false;
            }

            return true;
        }

        private static bool HasTag(DbCatalog catalog, DbRow row, string name)
        {
            var folded = Fold(name);
            foreach (var index in row.TagIndexes)
            {
                if ((uint)index >= (uint)catalog.Tags.Length)
                    continue;
                var tag = catalog.Tags[index];
                if (tag.Equals(name, StringComparison.OrdinalIgnoreCase) || Fold(tag) == folded)
                    return true;
            }

            return false;
        }

        private static bool Contains(string haystack, string needle) =>
            haystack.Contains(needle, StringComparison.OrdinalIgnoreCase) || Fold(haystack).Contains(Fold(needle), StringComparison.Ordinal);

        private static string Fold(string value)
        {
            var chars = value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
            return new string(chars);
        }

        private static GalleryResult ToResult(DbCatalog catalog, DbRow row)
        {
            var tags = new List<string>();
            foreach (var index in row.TagIndexes)
            {
                if ((uint)index >= (uint)catalog.Tags.Length)
                    continue;
                var formatted = TagNames.Format(string.Empty, TagNames.CleanName(catalog.Tags[index]));
                if (formatted.Length > 0)
                    tags.Add(formatted);
            }

            if (row.Studio.Length > 0)
            {
                var artist = TagNames.Format("artist", TagNames.CleanName(row.Studio));
                if (artist.Length > 0)
                    tags.Add(artist);
            }

            var cover = row.Cover;
            if (cover.StartsWith("//", StringComparison.Ordinal))
                cover = "https:" + cover;
            else if (cover.StartsWith('/'))
                cover = Root.TrimEnd('/') + cover;
            return new GalleryResult
            {
                Title = row.Title,
                CoverUrl = cover.Length == 0 ? null : cover,
                GalleryUrl = Root + "s/" + row.Slug,
                GalleryId = row.Slug,
                PageCount = row.Episodes > 0 ? row.Episodes : null,
                Tags = tags,
                SourceSite = Site,
            };
        }

        private static string CatalogScriptUrl(string home)
        {
            var script = ScriptPattern.Match(home);
            if (!script.Success)
                throw new InvalidOperationException("HentaiDB-Katalog (cat.js) wurde auf https://hentaidb.xyz/ nicht gefunden.");

            var src = WebUtility.HtmlDecode(script.Groups["src"].Value);
            if (src.StartsWith("https://hentaidb.xyz/", StringComparison.OrdinalIgnoreCase))
                return src;
            if (src.StartsWith('/'))
                return Root.TrimEnd('/') + src;
            throw new InvalidOperationException("HentaiDB-Katalog liegt nicht auf https://hentaidb.xyz/.");
        }

        private static JsonDocument ParseCatalogJson(string body)
        {
            var start = body.IndexOf('{');
            if (start < 0)
                throw new JsonException("HentaiDB-Katalog enthält kein JSON-Objekt.");

            var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(body[start..]));
            if (!JsonDocument.TryParseValue(ref reader, out var document) || document is null)
                throw new JsonException("HentaiDB-Katalog ist kein JSON-Objekt.");
            return document;
        }

        private static readonly Regex ScriptPattern = new(
            """src="(?<src>(?:https://hentaidb\.xyz)?/cat-[^"]+\.js)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private sealed class DbCatalog
        {
            public string[] Tags { get; init; } = Array.Empty<string>();
            public string[] Studios { get; init; } = Array.Empty<string>();
            public DbRow[] Rows { get; init; } = Array.Empty<DbRow>();

            public static DbCatalog Read(JsonElement root)
            {
                var tags = ReadStrings(root, "tags");
                var studios = ReadStrings(root, "studios");
                var rows = new List<DbRow>();
                if (root.TryGetProperty("rows", out var list))
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        var slug = item.TryGetProperty("s", out var slugValue) ? slugValue.GetString() ?? string.Empty : string.Empty;
                        if (slug.Length == 0)
                            continue;
                        var title = item.TryGetProperty("t", out var titleValue) ? titleValue.GetString() ?? slug : slug;
                        if (title.StartsWith('#'))
                            title = title[1..].Trim();
                        var studioIndex = item.TryGetProperty("st", out var studioValue) && studioValue.TryGetInt32(out var studio) ? studio : -1;
                        var indexes = new List<int>();
                        if (item.TryGetProperty("g", out var tagsValue))
                        {
                            foreach (var tag in tagsValue.EnumerateArray())
                            {
                                if (tag.TryGetInt32(out var index))
                                    indexes.Add(index);
                            }
                        }

                        rows.Add(new DbRow
                        {
                            Slug = slug,
                            Title = title,
                            Cover = item.TryGetProperty("a", out var cover) ? cover.GetString() ?? string.Empty : string.Empty,
                            Studio = (uint)studioIndex < (uint)studios.Length ? studios[studioIndex] : string.Empty,
                            StudioIndex = studioIndex,
                            Episodes = item.TryGetProperty("e", out var episodes) && episodes.TryGetInt32(out var count) ? count : 0,
                            Popularity = item.TryGetProperty("p", out var pop) && pop.TryGetInt32(out var rank) ? rank : int.MaxValue,
                            TagIndexes = indexes.ToArray(),
                        });
                    }
                }

                return new DbCatalog { Tags = tags, Studios = studios, Rows = rows.ToArray() };
            }

            private static string[] ReadStrings(JsonElement root, string name)
            {
                if (!root.TryGetProperty(name, out var list))
                    return Array.Empty<string>();
                return list.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
            }
        }

        private sealed class DbRow
        {
            public string Slug { get; init; } = string.Empty;
            public string Title { get; init; } = string.Empty;
            public string Cover { get; init; } = string.Empty;
            public string Studio { get; init; } = string.Empty;
            public int StudioIndex { get; init; }
            public int Episodes { get; init; }
            public int Popularity { get; init; }
            public int[] TagIndexes { get; init; } = Array.Empty<int>();
        }
    }

    /// <summary>
    /// Doujins.com: Suche über <c>/searches?words=</c>. Tags, Künstler und Serien (Parodien) füllen <c>online_site_tags</c>.
    /// gallery-dl hat keinen Extraktor für diese Domain; die Galerie-URL bleibt die kanonische Seite.
    /// </summary>
    public sealed class DoujinsComProvider : EHentaiStyleProvider
    {
        public const string Site = "doujins";

        private const string Root = "https://doujins.com/";
        private const int ArtistPageCap = 60;

        protected override string SiteId => Site;

        public override SourceProvider Provider => SourceProvider.DoujinsCom;

        public override string BaseUrl => Root;

        protected override string DisplayName => "Doujins.com";

        public override string BuildSearchUrl(string query, int page)
        {
            var words = string.Join(' ', ReadTerms(query).Select(term => term.Name));
            var url = Root + "searches?words=" + Uri.EscapeDataString(words);
            return page > 1 ? url + "&page=" + page : url;
        }

        public override async Task<IEnumerable<GalleryResult>> SearchAsync(
            string query,
            int page,
            MangaLibraryApp.AppSettings settings,
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
            var html = await GetHtmlAsync(url, cancellationToken).ConfigureAwait(false);
            var hits = ParseGalleries(html);
            HasFurtherPages = GalleryPages.Continues(hits.Count, current, html, url);
            return hits;
        }

        public override async Task<IReadOnlyList<CatalogTag>> GetOnlineTagsAsync(
            bool fullIndex = false,
            IProgress<TagDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var bag = new Dictionary<string, CatalogTag>(StringComparer.Ordinal);
            var letters = fullIndex ? "ABCDEFGHIJKLMNOPQRSTUVWXYZ" : "A";
            var steps = 1 + 1 + letters.Length;
            var done = 0;

            progress?.Report(new TagDownloadProgress(done, steps, "Doujins.com Tags", bag.Count));
            await HarvestAsync(bag, Root + "tags", "tag", "/tags/", cancellationToken).ConfigureAwait(false);
            done++;

            var artists = await GetHtmlAsync(Root + "artists", cancellationToken).ConfigureAwait(false);
            Harvest(bag, artists, "artist", "/artists/");
            var last = 1;
            foreach (Match match in PagePattern.Matches(artists))
            {
                if (int.TryParse(match.Groups[1].Value, out var number))
                    last = Math.Max(last, number);
            }

            if (!fullIndex)
                last = 1;
            last = Math.Min(last, ArtistPageCap);
            for (var page = 2; page <= last; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new TagDownloadProgress(done, steps + (last - 1), "Doujins.com Künstler Seite " + page, bag.Count));
                var html = await GetHtmlAsync(Root + "artists?page=" + page, cancellationToken).ConfigureAwait(false);
                Harvest(bag, html, "artist", "/artists/");
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            }

            done++;
            foreach (var letter in letters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new TagDownloadProgress(done, steps + Math.Max(0, last - 1), "Doujins.com Serien " + letter, bag.Count));
                try
                {
                    var html = await GetHtmlAsync(Root + "series/" + letter, cancellationToken).ConfigureAwait(false);
                    Harvest(bag, html, "parody", "/");
                }
                catch (InvalidOperationException ex) when (ex.InnerException is HttpRequestException http && http.Message.Contains("HTTP 404", StringComparison.Ordinal))
                {
                    // Ein fehlender Buchstabe beendet nicht den restlichen Index.
                }

                done++;
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
            }

            if (fullIndex && bag.Count == 0)
                throw new InvalidOperationException("Doujins.com hat keinen Tag-Index geliefert.");
            progress?.Report(new TagDownloadProgress(done, Math.Max(done, 1), "Doujins.com", bag.Count));
            return bag.Values.ToList();
        }

        private async Task HarvestAsync(
            Dictionary<string, CatalogTag> bag,
            string url,
            string kind,
            string prefix,
            CancellationToken cancellationToken)
        {
            var html = await GetHtmlAsync(url, cancellationToken).ConfigureAwait(false);
            Harvest(bag, html, kind, prefix);
        }

        private static void Harvest(Dictionary<string, CatalogTag> bag, string html, string kind, string prefix)
        {
            foreach (Match match in CardPattern.Matches(html))
            {
                var href = match.Groups["href"].Value;
                if (kind == "parody")
                {
                    if (!SeriesPath.IsMatch(href))
                        continue;
                }
                else if (!href.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = WebUtility.HtmlDecode(match.Groups["name"].Value).Trim();
                if (name.Length < 2)
                    continue;
                TagSanitizer.Add(bag, kind, name, 1, hasCount: false);
            }
        }

        private static IReadOnlyList<GalleryResult> ParseGalleries(string html)
        {
            var results = new List<GalleryResult>();
            foreach (Match match in GalleryPattern.Matches(html))
            {
                var path = match.Groups["path"].Value;
                var id = match.Groups["id"].Value;
                results.Add(new GalleryResult
                {
                    Title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim(),
                    CoverUrl = WebUtility.HtmlDecode(match.Groups["cover"].Value),
                    GalleryUrl = Root.TrimEnd('/') + path,
                    GalleryId = id,
                    Tags = Array.Empty<string>(),
                    SourceSite = Site,
                });
            }

            return results;
        }

        private static readonly Regex GalleryPattern = new(
            """<a href="(?<path>/(?<series>[^"?]+)/(?<slug>[^"?]+)-(?<id>\d+))" class="gallery-visited[^"]*">\s*<img src="(?<cover>https://static\.doujins\.com/[^"?]+)".{0,800}?<div class="text">(?<title>[^<]*)</div>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex CardPattern = new(
            """<a href="(?<href>/[^"]+)"[^>]*>.{0,700}?<div class="text">(?<name>[^<]+)</div>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex SeriesPath = new(
            @"^/[^/]+-\d+(?:\?|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PagePattern = new(@"[?&]page=(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
