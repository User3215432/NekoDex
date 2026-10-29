namespace NekoDex.Core.Models
{
    /// <summary>Ein Galerie-Treffer einer Quellen-Suche, unabhängig vom bestehenden Online-Raster.</summary>
    public sealed class GalleryResult
    {
        public string Title { get; init; } = string.Empty;

        public string? CoverUrl { get; init; }

        public string GalleryUrl { get; init; } = string.Empty;

        public string? GalleryId { get; init; }

        public int? PageCount { get; init; }

        public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

        public string SourceSite { get; init; } = string.Empty;
    }
}
