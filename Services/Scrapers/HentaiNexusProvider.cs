using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using NekoDex.Core.Enums;

namespace NekoDex.Services.Scrapers
{
    /// <summary>Suche auf hentainexus.com. gallery-dl bekommt <c>https://hentainexus.com/view/…</c>.</summary>
    public sealed class HentaiNexusProvider : BrowserListingProvider
    {
        private static readonly Regex Link = new(
            """href="(?:https?://(?:www\.)?hentainexus\.com)?/view/(?<id>\d+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex IdInUrl = new(
            """/view/(?<id>\d+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public override SourceProvider Provider => SourceProvider.HentaiNexus;

        public override string BaseUrl => "https://hentainexus.com/";

        protected override string SiteKey => "hentainexus";

        protected override string SiteTitle => "HentaiNexus";

        protected override Regex CardLink => Link;

        protected override Regex IdPattern => IdInUrl;

        protected override string GalleryUrl(string id, string? slug) =>
            "https://hentainexus.com/view/" + id.Trim();

        public override string BuildSearchUrl(string query, int page)
        {
            var q = Uri.EscapeDataString(NexusQuery(query));
            var number = Math.Max(1, page);
            return number <= 1
                ? BaseUrl + "?q=" + q
                : BaseUrl + "page/" + number.ToString() + "?q=" + q;
        }

        protected override IReadOnlyList<(string Url, string Kind)> TagRoots(bool fullIndex)
        {
            var roots = new List<(string Url, string Kind)>
            {
                ("https://hentainexus.com/explore/categories/tag", "tag"),
                ("https://hentainexus.com/explore/categories/artist", "artist"),
                ("https://hentainexus.com/explore/categories/author", "artist"),
                ("https://hentainexus.com/explore/categories/parody", "parody"),
                ("https://hentainexus.com/explore/categories/circle", "group"),
                ("https://hentainexus.com/explore/categories/magazine", "parody"),
                ("https://hentainexus.com/explore/categories/event", "convention"),
                ("https://hentainexus.com/explore/categories/publisher", "group"),
            };
            return fullIndex ? roots : roots.GetRange(0, 4);
        }

        /// <summary>
        /// HentaiNexus filtert mehrere Tags nur mit Präfix (<c>tag:anal tag:"sole male"</c>).
        /// Ein einzelnes Wort bleibt ohne Präfix, die Seite sucht den Tag dann selbst.
        /// </summary>
        private static string NexusQuery(string? query)
        {
            var terms = new List<string>();
            string? kind = null;
            var name = new StringBuilder();
            foreach (var part in (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith('-'))
                    continue;
                var colon = part.IndexOf(':');
                var namespaced = colon > 0 && colon < part.Length - 1 && part[..colon].All(char.IsLetter);
                if (namespaced)
                {
                    Flush(terms, kind, name);
                    kind = part[..colon];
                    name.Append(part[(colon + 1)..].Trim('"'));
                    continue;
                }

                if (kind is null && name.Length == 0)
                {
                    name.Append(part.Trim('"'));
                    continue;
                }

                if (kind is not null)
                {
                    name.Append(' ').Append(part.Trim('"'));
                    continue;
                }

                Flush(terms, null, name);
                name.Append(part.Trim('"'));
            }

            Flush(terms, kind, name);
            if (terms.Count > 1)
            {
                for (var i = 0; i < terms.Count; i++)
                {
                    if (!terms[i].Contains(':'))
                        terms[i] = "tag:" + terms[i];
                }
            }

            return string.Join(' ', terms);
        }

        private static void Flush(List<string> terms, string? kind, StringBuilder name)
        {
            var text = name.ToString().Trim().Replace('_', ' ');
            name.Clear();
            if (text.Length == 0)
                return;
            if (kind is null && !text.Contains(' '))
            {
                terms.Add(text);
                return;
            }

            var prefix = string.IsNullOrEmpty(kind) ? "tag" : kind;
            terms.Add(text.Contains(' ') ? prefix + ":\"" + text + "\"" : prefix + ":" + text);
        }
    }
}
