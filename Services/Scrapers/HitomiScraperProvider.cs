using NekoDex.Core.Enums;

namespace NekoDex.Services.Scrapers
{
    public sealed class HitomiScraperProvider : CatalogScraperProvider
    {
        protected override string SiteId => "hitomi";

        public override SourceProvider Provider => SourceProvider.Hitomi;

        public override string BaseUrl => "https://hitomi.la/";
    }
}
