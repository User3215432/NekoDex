using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NekoDex.Core.Enums;
using NekoDex.Core.Models;
using AppSettings = MangaLibraryApp.AppSettings;
using TagDownloadProgress = MangaLibraryApp.TagDownloadProgress;

namespace NekoDex.Services.Scrapers
{
    /// <summary>Suche auf kemono.cr. gallery-dl bekommt <c>https://kemono.cr/{service}/user/{user}/post/{id}</c>.</summary>
    public sealed class KemonoProvider : BrowserListingProvider
    {
        private static readonly Regex Link = new(
            """href="(?:https?://(?:www\.)?kemono\.(?:cr|su|party|st))?/(?<service>[a-z0-9]+)/user/(?<user>[^"#?/]+)/post/(?<id>\d+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex IdInUrl = new(
            """/post/(?<id>\d+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public override SourceProvider Provider => SourceProvider.KemonoSu;

        public override string BaseUrl => "https://kemono.cr/";

        protected override string SiteKey => "kemono";

        protected override string SiteTitle => "Kemono";

        protected override Regex CardLink => Link;

        protected override Regex IdPattern => IdInUrl;

        protected override int TagPageCap => 40;

        protected override string GalleryUrl(string id, string? slug) =>
            "https://kemono.cr/post/" + id;

        public static string GalleryUrlFor(string service, string user, string id) =>
            "https://kemono.cr/" + service.Trim('/') + "/user/" + user.Trim('/') + "/post/" + id.Trim();

        public override string BuildSearchUrl(string query, int page)
        {
            var text = SearchText.Prepare(query, TagBlacklist);
            var offset = Math.Max(0, page - 1) * 50;
            return "https://kemono.cr/posts?q=" + Uri.EscapeDataString(text) + "&o=" + offset.ToString();
        }

        protected override IReadOnlyList<(string Url, string Kind)> TagRoots(bool fullIndex) =>
            new[] { ("https://kemono.cr/posts?q=", "tag") };

        public override async Task<IEnumerable<GalleryResult>> SearchAsync(
            string query,
            int page,
            AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            _ = settings;
            var text = SearchText.Prepare(query, TagBlacklist);
            var current = Math.Max(1, page);
            if (current > GalleryPages.SafetyCap)
            {
                HasFurtherPages = false;
                return Array.Empty<GalleryResult>();
            }

            List<GalleryResult> batch;
            var useHtml = false;
            try
            {
                batch = await ReadApiWindowAsync(text, current, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (!ex.Message.Contains("Anti-Bot", StringComparison.Ordinal))
            {
                useHtml = true;
                batch = new List<GalleryResult>();
            }

            if (!useHtml && batch.Count == 0 && current == 1)
                useHtml = true;
            if (useHtml)
            {
                var pageUrl = BuildSearchUrl(query, current);
                var html = await FetchAsync(pageUrl, cancellationToken).ConfigureAwait(false);
                batch = ReadHtmlPosts(html);
                if (batch.Count == 0 && SiteHttp.LooksBlocked(html))
                    throw new InvalidOperationException("Kemono hat die Suche mit einer Anti-Bot-Seite beantwortet.");
                if (batch.Count > ShownPage)
                    batch = batch.Take(ShownPage).ToList();
                HasFurtherPages = batch.Count == ShownPage && current < GalleryPages.SafetyCap;
                return batch;
            }

            HasFurtherPages = batch.Count == ShownPage && current < GalleryPages.SafetyCap;
            return batch;
        }

        private const int ShownPage = 30;
        private const int ApiPage = 50;

        private async Task<List<GalleryResult>> ReadApiWindowAsync(string text, int page, CancellationToken cancellationToken)
        {
            var start = (page - 1) * ShownPage;
            var apiIndex = start / ApiPage;
            var skip = start % ApiPage;
            var first = ReadPosts(await FetchApiAsync(ApiUrl(text, apiIndex * ApiPage), cancellationToken).ConfigureAwait(false));
            var window = first.Skip(skip).Take(ShownPage).ToList();
            if (window.Count >= ShownPage || first.Count < ApiPage)
                return window;

            List<GalleryResult> second;
            try
            {
                second = ReadPosts(await FetchApiAsync(ApiUrl(text, (apiIndex + 1) * ApiPage), cancellationToken).ConfigureAwait(false));
            }
            catch (InvalidOperationException ex) when (!ex.Message.Contains("Anti-Bot", StringComparison.Ordinal))
            {
                return window;
            }

            var seen = new HashSet<string>(window.Select(item => string.IsNullOrEmpty(item.GalleryId) ? item.GalleryUrl : item.GalleryId), StringComparer.Ordinal);
            foreach (var item in second)
            {
                if (window.Count >= ShownPage)
                    break;
                var key = string.IsNullOrEmpty(item.GalleryId) ? item.GalleryUrl : item.GalleryId;
                if (seen.Add(key))
                    window.Add(item);
            }

            return window;
        }

        private static string ApiUrl(string text, int offset) =>
            "https://kemono.cr/api/v1/posts?q=" + Uri.EscapeDataString(text) + "&o=" + offset.ToString();

        public override async Task<IReadOnlyList<CatalogTag>> GetOnlineTagsAsync(
            bool fullIndex = false,
            IProgress<TagDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var bag = new Dictionary<string, CatalogTag>(StringComparer.Ordinal);
            var pages = fullIndex ? 40 : 1;
            string? previous = null;
            for (var page = 0; page < pages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string body;
                try
                {
                    body = await FetchApiAsync("https://kemono.cr/api/v1/posts?o=" + (page * 50).ToString(), cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException) when (page > 0)
                {
                    break;
                }

                var posts = ReadPosts(body);
                if (posts.Count == 0)
                    break;
                var fingerprint = string.Join("\n", posts.Take(20).Select(item => item.GalleryId));
                if (fingerprint == previous)
                    break;
                previous = fingerprint;
                foreach (var post in posts)
                {
                    foreach (var tag in post.Tags)
                        TagSanitizer.Add(bag, "tag", tag, 0, hasCount: false);
                }

                progress?.Report(new TagDownloadProgress(page, pages, "Kemono: Beiträge " + (page + 1), bag.Count));
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            if (fullIndex)
            {
                try
                {
                    var body = await FetchApiAsync("https://kemono.cr/api/v1/posts/tags", cancellationToken).ConfigureAwait(false);
                    RememberJsonTags(bag, body);
                }
                catch (InvalidOperationException ex)
                {
                    RememberBlock(ex.Message);
                }

                progress?.Report(new TagDownloadProgress(1, 1, "Kemono: Tags", bag.Count));
            }

            if (bag.Count == 0 && fullIndex)
                throw new InvalidOperationException(EmptyCatalogMessage());
            return Order(bag);
        }

        private Task<string> FetchApiAsync(string url, CancellationToken cancellationToken) =>
            FetchAsync(url, cancellationToken, "text/css");

        private List<GalleryResult> ReadPosts(string body)
        {
            var trimmed = body.TrimStart();
            if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
                return ParseCards(body);

            try
            {
                using var doc = JsonDocument.Parse(body);
                var list = new List<GalleryResult>();
                Walk(doc.RootElement, list);
                return list;
            }
            catch (JsonException)
            {
                return ReadHtmlPosts(body);
            }
        }

        private List<GalleryResult> ReadHtmlPosts(string html)
        {
            var results = new List<GalleryResult>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in Link.Matches(html))
            {
                var id = match.Groups["id"].Value;
                var service = match.Groups["service"].Value;
                var user = match.Groups["user"].Value;
                if (id.Length == 0 || service.Length == 0 || user.Length == 0 || !seen.Add(id))
                    continue;
                results.Add(new GalleryResult
                {
                    Title = "Kemono " + id,
                    GalleryUrl = GalleryUrlFor(service, user, id),
                    GalleryId = id,
                    SourceSite = SiteId,
                });
            }

            return results;
        }

        private void Walk(JsonElement element, List<GalleryResult> list)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Walk(item, list);
                    break;
                case JsonValueKind.Object:
                    if (TryPost(element, out var post))
                        list.Add(post);
                    else
                    {
                        foreach (var property in element.EnumerateObject())
                            Walk(property.Value, list);
                    }
                    break;
            }
        }

        private bool TryPost(JsonElement item, out GalleryResult post)
        {
            post = new GalleryResult();
            var id = JsonValues.String(item, "id", "Id");
            var title = JsonValues.String(item, "title", "Title", "content");
            var service = JsonValues.String(item, "service", "Service");
            var user = JsonValues.String(item, "user", "User", "creator");
            if (id.Length == 0 || !id.All(char.IsDigit) || service.Length == 0 || user.Length == 0)
                return false;
            if (title.Length == 0)
                title = service + " " + id;

            var tags = new List<string>();
            if (item.TryGetProperty("tags", out var tagElement) || item.TryGetProperty("Tags", out tagElement))
                CollectTagNames(tagElement, tags);
            if (TagBlacklist is { } filter && !filter.Allows(tags))
                return false;

            var cover = CoverFrom(item);
            post = new GalleryResult
            {
                Title = title,
                CoverUrl = cover,
                GalleryUrl = GalleryUrlFor(service, user, id),
                GalleryId = id,
                Tags = tags,
                SourceSite = SiteId,
            };
            return true;
        }

        private static void CollectTagNames(JsonElement element, List<string> tags)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    var text = element.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        tags.Add(text.Trim());
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        CollectTagNames(item, tags);
                    break;
                case JsonValueKind.Object:
                    var name = JsonValues.String(element, "name", "Name", "tag", "Tag", "text", "Text");
                    if (name.Length > 0)
                        tags.Add(name);
                    break;
            }
        }

        private static string? CoverFrom(JsonElement item)
        {
            if (item.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object)
            {
                var path = JsonValues.String(file, "path", "name");
                if (path.Length > 0)
                    return path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? path
                        : "https://img.kemono.cr/thumbnail/data" + (path.StartsWith('/') ? path : "/" + path);
            }

            var thumb = JsonValues.String(item, "thumbnail", "cover");
            return thumb.Length == 0 ? null : SiteHttp.Absolute(thumb, "https://kemono.cr/");
        }

        private static void RememberJsonTags(Dictionary<string, CatalogTag> bag, string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                RememberElement(bag, doc.RootElement);
            }
            catch (JsonException)
            {
                // Kein Tag-JSON.
            }
        }

        private static void RememberElement(Dictionary<string, CatalogTag> bag, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        RememberElement(bag, item);
                    break;
                case JsonValueKind.Object:
                    var name = JsonValues.String(element, "name", "Name", "tag", "Tag", "text");
                    var count = JsonValues.OptionalInt(element, "count", "Count", "post_count");
                    if (name.Length > 0)
                        TagSanitizer.Add(bag, "tag", name, count ?? 0, count is not null);
                    break;
                case JsonValueKind.String:
                    var text = element.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        TagSanitizer.Add(bag, "tag", text, 0, hasCount: false);
                    break;
            }
        }
    }
}
