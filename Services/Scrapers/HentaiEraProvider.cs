using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NekoDex.Core.Enums;

namespace NekoDex.Services.Scrapers
{
    /// <summary>Suche auf hentaiera.com. gallery-dl bekommt <c>https://hentaiera.com/view/…</c>.</summary>
    public sealed class HentaiEraProvider : BrowserListingProvider
    {
        private static readonly Regex Link = new(
            """href="(?:https?://(?:www\.)?hentaiera\.com)?/(?:view|gallery)/(?<id>\d+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex IdInUrl = new(
            """/(?:view|gallery)/(?<id>\d+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public override SourceProvider Provider => SourceProvider.HentaiEra;

        public override string BaseUrl => "https://hentaiera.com/";

        protected override string SiteKey => "hentaiera";

        protected override string SiteTitle => "HentaiEra";

        protected override Regex CardLink => Link;

        protected override Regex IdPattern => IdInUrl;

        protected override bool WarmSession => true;

        protected override string GalleryUrl(string id, string? slug) =>
            "https://hentaiera.com/view/" + id.Trim('/') + "/";

        public override string BuildSearchUrl(string query, int page)
        {
            var text = SearchText.Prepare(query, TagBlacklist);
            return BaseUrl + "search/?key=" + Uri.EscapeDataString(text) + "&page=" + Math.Max(1, page).ToString();
        }

        protected override IReadOnlyList<(string Url, string Kind)> TagRoots(bool fullIndex) =>
            FoxFamilyRoots(BaseUrl, fullIndex);

        protected override IReadOnlyList<(string Url, string Kind)>? LetterRoots(bool fullIndex) =>
            fullIndex ? LetterTagRoots(BaseUrl + "tags/") : null;
    }
}
