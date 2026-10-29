using NekoDex.Core.Enums;

namespace NekoDex.Services.Scrapers
{
    public sealed class EHentaiScraperProvider : CatalogScraperProvider
    {
        protected override string SiteId => "ehentai";

        public override SourceProvider Provider => SourceProvider.EHentai;

        public override string BaseUrl => "https://e-hentai.org/";
    }
}
