using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NekoDex.Core.Enums;
using NekoDex.Core.Models;
using AppSettings = MangaLibraryApp.AppSettings;

namespace NekoDex.Services.Scrapers
{
    /// <summary>
    /// Suche über <c>https://hentaihand.com/api/comics</c>. Der HTML-Pfad <c>/search/</c> antwortet mit HTTP 404.
    /// gallery-dl und der Reader bekommen <c>https://hentaihand.com/en/comic/…</c>.
    /// </summary>
    public sealed class HentaiHandProvider : BrowserListingProvider
    {
        private static readonly string[] LookupUris =
        {
            "tags", "artists", "characters", "parodies", "groups", "categories",
        };

        private static readonly Regex Link = new(
            """href="(?:https?://(?:www\.)?hentaihand\.com)?(?:/en)?/(?:comic|hentai)/(?<id>[^"#?/]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex IdInUrl = new(
            """/(?:en/)?(?:comic|hentai)/(?<id>[^"#?/]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public override SourceProvider Provider => SourceProvider.HentaiHand;

        public override string BaseUrl => "https://hentaihand.com/";

        protected override string SiteKey => "hentaihand";

        protected override string SiteTitle => "HentaiHand";

        protected override Regex CardLink => Link;

        protected override Regex IdPattern => IdInUrl;

        protected override string GalleryUrl(string id, string? slug) =>
            "https://hentaihand.com/en/comic/" + (slug is { Length: > 0 } ? slug : id).Trim('/');

        public override string BuildSearchUrl(string query, int page)
        {
            var text = SearchText.Prepare(query, TagBlacklist);
            return "https://hentaihand.com/api/comics?page=" + Math.Max(1, page).ToString()
                + "&q=" + Uri.EscapeDataString(text);
        }

        protected override IReadOnlyList<(string Url, string Kind)> TagRoots(bool fullIndex) =>
            FoxFamilyRoots(BaseUrl, fullIndex);

        protected override IReadOnlyList<(string Url, string Kind)>? LetterRoots(bool fullIndex) =>
            fullIndex ? LetterTagRoots(BaseUrl + "tags/") : null;

        protected override bool AcceptId(string id) =>
            id.Length > 1 && !id.Equals("search", StringComparison.OrdinalIgnoreCase);

        public override async Task<IEnumerable<GalleryResult>> SearchAsync(
            string query,
            int page,
            AppSettings settings,
            CancellationToken cancellationToken = default)
        {
            _ = settings;
            var words = Words(query);
            var filters = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var free = new List<string>();
            var index = 0;
            while (index < words.Count)
            {
                var matched = false;
                var window = Math.Min(4, words.Count - index);
                for (var length = window; length >= 1; length--)
                {
                    var phrase = string.Join(' ', words.GetRange(index, length));
                    var hit = await LookupAsync(phrase, cancellationToken).ConfigureAwait(false);
                    if (hit is not { } found)
                        continue;
                    if (!filters.TryGetValue(found.Uri, out var ids))
                    {
                        ids = new List<int>();
                        filters[found.Uri] = ids;
                    }

                    if (!ids.Contains(found.Id))
                        ids.Add(found.Id);
                    index += length;
                    matched = true;
                    break;
                }

                if (!matched)
                {
                    free.Add(words[index]);
                    index++;
                }
            }

            var text = free.Count == 0 ? null : string.Join(' ', free);
            var current = Math.Max(1, page);
            if (current > GalleryPages.SafetyCap)
            {
                HasFurtherPages = false;
                return Array.Empty<GalleryResult>();
            }

            List<GalleryResult> batch;
            try
            {
                batch = ReadComics(await FetchAsync(ComicUrl(current, filters, text), cancellationToken).ConfigureAwait(false));
            }
            catch (InvalidOperationException ex) when (free.Count > 0 && filters.Count > 0 && ex.Message.Contains("HTTP 500", StringComparison.Ordinal))
            {
                batch = ReadComics(await FetchAsync(ComicUrl(current, filters, null), cancellationToken).ConfigureAwait(false));
            }

            HasFurtherPages = GalleryPages.Continues(batch.Count, current);
            return batch;
        }

        public override async Task<GalleryResult> GetGalleryDetailsAsync(string galleryIdOrUrl, CancellationToken cancellationToken = default)
        {
            var text = (galleryIdOrUrl ?? string.Empty).Trim();
            var match = IdInUrl.Match(text);
            var id = match.Success ? match.Groups["id"].Value : text.Trim('/');
            if (!AcceptId(id))
                throw new ArgumentException(SiteTitle + ": keine Galerie-ID in \"" + text + "\".");
            return await base.GetGalleryDetailsAsync(GalleryUrl(id, null), cancellationToken).ConfigureAwait(false);
        }

        private static string ComicUrl(int page, Dictionary<string, List<int>> filters, string? text)
        {
            var builder = new StringBuilder("https://hentaihand.com/api/comics?page=");
            builder.Append(Math.Max(1, page).ToString());
            builder.Append("&sort=uploaded_at&order=desc&duration=all");
            foreach (var pair in filters)
            {
                for (var i = 0; i < pair.Value.Count; i++)
                {
                    builder.Append('&').Append(pair.Key).Append('[').Append(i.ToString()).Append("]=").Append(pair.Value[i].ToString());
                }
            }

            if (!string.IsNullOrWhiteSpace(text))
                builder.Append("&q=").Append(Uri.EscapeDataString(text));
            return builder.ToString();
        }

        private async Task<(string Uri, int Id)?> LookupAsync(string phrase, CancellationToken cancellationToken)
        {
            foreach (var uri in LookupUris)
            {
                string body;
                try
                {
                    body = await FetchAsync("https://hentaihand.com/api/" + uri + "?q=" + Uri.EscapeDataString(phrase), cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var item in data.EnumerateArray())
                    {
                        var name = JsonValues.String(item, "name", "Name");
                        var id = JsonValues.OptionalInt(item, "id", "Id");
                        if (id is null || !name.Equals(phrase, StringComparison.OrdinalIgnoreCase))
                            continue;
                        return (uri, id.Value);
                    }
                }
                catch (JsonException)
                {
                    // Diese Nachschlageliste ist kein Tag-JSON.
                }
            }

            return null;
        }

        private List<GalleryResult> ReadComics(string body)
        {
            var results = new List<GalleryResult>();
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return results;
                foreach (var item in data.EnumerateArray())
                {
                    var slug = JsonValues.String(item, "slug", "Slug");
                    var id = slug.Length > 0 ? slug : JsonValues.String(item, "id", "Id");
                    if (!AcceptId(id))
                        continue;
                    var title = JsonValues.String(item, "title", "Title");
                    if (title.Length == 0)
                        title = "Galerie " + id;
                    var tags = new List<string>();
                    if (item.TryGetProperty("tags", out var tagElement) || item.TryGetProperty("Tags", out tagElement))
                        CollectNames(tagElement, tags);
                    if (TagBlacklist is { } filter && !filter.Allows(tags))
                        continue;
                    var cover = JsonValues.String(item, "thumb_url", "image_url", "cover");
                    results.Add(new GalleryResult
                    {
                        Title = title,
                        CoverUrl = cover.Length == 0 ? null : SiteHttp.Absolute(cover, BaseUrl),
                        GalleryUrl = GalleryUrl(id, slug),
                        GalleryId = id,
                        PageCount = JsonValues.OptionalInt(item, "pages", "Pages"),
                        Tags = tags,
                        SourceSite = SiteId,
                    });
                }
            }
            catch (JsonException)
            {
                return ParseCards(body);
            }

            return results;
        }

        private static void CollectNames(JsonElement element, List<string> tags)
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
                        CollectNames(item, tags);
                    break;
                case JsonValueKind.Object:
                    var name = JsonValues.String(element, "name", "Name", "slug");
                    if (name.Length > 0)
                        tags.Add(name);
                    break;
            }
        }

        private List<string> Words(string? query)
        {
            var words = new List<string>();
            foreach (var part in (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith('-'))
                    continue;
                var name = part;
                var colon = part.IndexOf(':');
                if (colon > 0 && colon < part.Length - 1 && part[..colon].All(char.IsLetter))
                    name = part[(colon + 1)..];
                name = name.Trim().Trim('"').Replace('_', ' ');
                if (name.Length == 0)
                    continue;
                if (TagBlacklist is { } filter && !filter.Allows(new[] { name }))
                    continue;
                words.Add(name);
            }

            return words;
        }
    }
}
