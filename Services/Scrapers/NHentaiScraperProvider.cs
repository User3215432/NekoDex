using NekoDex.Core.Enums;

namespace NekoDex.Services.Scrapers
{
    public sealed class NHentaiScraperProvider : CatalogScraperProvider
    {
        protected override string SiteId => "nhentai";

        public override SourceProvider Provider => SourceProvider.NHentai;

        public override string BaseUrl => "https://nhentai.net/";
    }
}
