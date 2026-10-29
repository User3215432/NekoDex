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
    /// <summary>Suche auf 3hentai.net. gallery-dl bekommt <c>https://3hentai.net/d/…</c>.</summary>
    public sealed class ThreeHentaiProvider : BrowserListingProvider
    {
        private static readonly Regex Link = new(
            """href="(?:https?://(?:www\.)?3hentai\.net)?/d/(?<id>\d+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex IdInUrl = new(
            """/d/(?<id>\d+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public override SourceProvider Provider => SourceProvider.ThreeHentai;

        public override string BaseUrl => "https://3hentai.net/";

        protected override string SiteKey => "3hentai";

        protected override string SiteTitle => "3Hentai";

        protected override Regex CardLink => Link;

        protected override Regex IdPattern => IdInUrl;

        protected override string GalleryUrl(string id, string? slug) =>
            "https://3hentai.net/d/" + id.Trim();

        public override string BuildSearchUrl(string query, int page)
        {
            var text = SearchText.Prepare(query, TagBlacklist);
            return BaseUrl + "search?q=" + Uri.EscapeDataString(text) + "&page=" + Math.Max(1, page).ToString();
        }

        protected override IReadOnlyList<(string Url, string Kind)> TagRoots(bool fullIndex) =>
            FoxFamilyRoots(BaseUrl, fullIndex);

        public override async Task<IEnumerable<GalleryResult>> SearchAsync(
            string query,
            int page,
            AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            var text = SearchText.Prepare(query, TagBlacklist);
            var current = Math.Max(1, page);
            if (current > GalleryPages.SafetyCap)
            {
                HasFurtherPages = false;
                return Array.Empty<GalleryResult>();
            }

            try
            {
                var api = BaseUrl + "api/search?query=" + Uri.EscapeDataString(text) + "&page=" + current.ToString();
                var batch = ReadApi(await FetchAsync(api, cancellationToken).ConfigureAwait(false));
                if (batch.Count == 0)
                    return await base.SearchAsync(query, current, settings, cancellationToken).ConfigureAwait(false);
                HasFurtherPages = GalleryPages.Continues(batch.Count, current);
                return batch;
            }
            catch (InvalidOperationException)
            {
                return await base.SearchAsync(query, current, settings, cancellationToken).ConfigureAwait(false);
            }
        }

        public override async Task<IReadOnlyList<CatalogTag>> GetOnlineTagsAsync(
            bool fullIndex = false,
            IProgress<TagDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var bag = new Dictionary<string, CatalogTag>(StringComparer.Ordinal);
            await CollectHtmlTagsAsync(bag, fullIndex, progress, cancellationToken).ConfigureAwait(false);

            var pages = fullIndex ? 40 : 1;
            for (var page = 1; page <= pages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string body;
                try
                {
                    body = await FetchAsync(BaseUrl + "api/tags?page=" + page.ToString(), cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                var before = bag.Count;
                RememberApiTags(bag, body);
                progress?.Report(new TagDownloadProgress(page, pages, "3Hentai: Tags Seite " + page, bag.Count));
                if (bag.Count == before)
                    break;
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            if (bag.Count == 0 && fullIndex)
                throw new InvalidOperationException("3Hentai lieferte keinen bereinigten Tagkatalog.");
            return Order(bag);
        }

        private List<GalleryResult> ReadApi(string body)
        {
            var trimmed = body.TrimStart();
            if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
                return new List<GalleryResult>();

            try
            {
                using var doc = JsonDocument.Parse(body);
                var list = new List<GalleryResult>();
                Walk(doc.RootElement, list);
                return list;
            }
            catch (JsonException)
            {
                return new List<GalleryResult>();
            }
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
                    if (TryBook(element, out var book))
                        list.Add(book);
                    else
                    {
                        foreach (var property in element.EnumerateObject())
                            Walk(property.Value, list);
                    }
                    break;
            }
        }

        private bool TryBook(JsonElement item, out GalleryResult book)
        {
            book = new GalleryResult();
            var id = JsonValues.String(item, "id", "Id");
            var title = JsonValues.String(item, "title", "Title", "name");
            if (id.Length == 0 || !id.All(char.IsDigit) || title.Length == 0)
                return false;
            if (JsonValues.Has(item, "type", "Type") && !JsonValues.Has(item, "thumbnail", "thumb", "cover"))
                return false;

            var tags = new List<string>();
            if (item.TryGetProperty("tags", out var tagElement) || item.TryGetProperty("Tags", out tagElement))
                CollectTags(tagElement, tags);
            if (TagBlacklist is { } filter && !filter.Allows(tags))
                return false;

            var cover = JsonValues.String(item, "thumbnail", "thumb", "cover");
            book = new GalleryResult
            {
                Title = title,
                CoverUrl = cover.Length == 0 ? "https://t.3hentai.net/d/" + id + "/cover.jpg" : SiteHttp.Absolute(cover, BaseUrl),
                GalleryUrl = GalleryUrl(id, null),
                GalleryId = id,
                Tags = tags,
                SourceSite = SiteId,
            };
            return true;
        }

        private static void CollectTags(JsonElement element, List<string> tags)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    var text = element.GetString();
                    var formatted = TagSanitizer.FormatTag("tag", text);
                    if (formatted is not null)
                        tags.Add(formatted);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        CollectTags(item, tags);
                    break;
                case JsonValueKind.Object:
                    var name = JsonValues.String(element, "name", "Name", "tag", "text");
                    var kind = JsonValues.String(element, "type", "Type", "namespace", "category");
                    var tag = TagSanitizer.FormatTag(kind.Length == 0 ? "tag" : kind, name);
                    if (tag is not null)
                        tags.Add(tag);
                    break;
            }
        }

        private static void RememberApiTags(Dictionary<string, CatalogTag> bag, string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                Remember(bag, doc.RootElement);
            }
            catch (JsonException)
            {
                // Kein Tag-JSON.
            }
        }

        private static void Remember(Dictionary<string, CatalogTag> bag, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Remember(bag, item);
                    break;
                case JsonValueKind.Object:
                    var name = JsonValues.String(element, "name", "Name", "tag", "text");
                    var kind = JsonValues.String(element, "type", "Type", "namespace", "category");
                    var count = JsonValues.OptionalInt(element, "count", "Count");
                    if (name.Length > 0 && !JsonValues.Has(element, "thumbnail", "thumb"))
                        TagSanitizer.Add(bag, kind.Length == 0 ? "tag" : kind, name, count ?? 0, count is not null);
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                            Remember(bag, property.Value);
                    }
                    break;
            }
        }
    }
}
