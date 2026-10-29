using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NekoDex.Core.Enums;
using NekoDex.Core.Models;
using AppSettings = MangaLibraryApp.AppSettings;

namespace NekoDex.Services.Scrapers
{
    public interface IScraperProvider
    {
        SourceProvider Provider { get; }
        string BaseUrl { get; }
        bool SupportsTagSearch { get; }

        Task<IEnumerable<GalleryResult>> SearchAsync(string query, int page, AppSettings settings, CancellationToken cancellationToken = default);
        string BuildSearchUrl(string query, int page);
    }

    /// <summary>Die letzte <see cref="IScraperProvider.SearchAsync"/>-Seite hat weitere Treffer.</summary>
    public interface ISearchPageHint
    {
        bool HasFurtherPages { get; }
    }
}
