using System;
using System.Collections.Generic;
using System.Linq;
using NekoDex.Core.Enums;

namespace NekoDex.Services.Scrapers
{
    public interface IScraperFactory
    {
        IScraperProvider GetProvider(SourceProvider provider);
        IEnumerable<IScraperProvider> GetAllProviders();
    }

    public class ScraperFactory : IScraperFactory
    {
        private readonly Dictionary<SourceProvider, IScraperProvider> _providers;

        public ScraperFactory(IEnumerable<IScraperProvider> providers)
        {
            _providers = providers.ToDictionary(p => p.Provider, p => p);
        }

        public IScraperProvider GetProvider(SourceProvider provider)
        {
            if (_providers.TryGetValue(provider, out var scraper))
            {
                return scraper;
            }

            throw new NotSupportedException($"Der Provider '{provider}' ist aktuell noch nicht implementiert.");
        }

        public IEnumerable<IScraperProvider> GetAllProviders() => _providers.Values;
    }
}
