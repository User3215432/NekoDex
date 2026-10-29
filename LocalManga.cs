using System.IO;
using System.Windows.Media;

namespace MangaLibraryApp;

/// <summary>Ein lokal gespeicherter Titel inklusive Favorit und Lesefortschritt.</summary>
public sealed class LocalManga : ObservableObject
{
    private bool _isFavorite;
    private bool _isSelected;
    private bool _showSelection;
    private ReadingStatus _readingStatus;
    private ImageSource? _cover;
    private bool _coverRequested;

    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Artist { get; set; }
    public string? CoverPath { get; set; }

    public ImageSource? Cover
    {
        get
        {
            if (_cover is not null)
                return _cover;
            if (ImageCacheService.TryGet(CoverPath, out var cached))
            {
                _cover = cached;
                return cached;
            }

            if (!_coverRequested && !string.IsNullOrWhiteSpace(CoverPath))
            {
                _coverRequested = true;
                _ = LoadCoverAsync();
            }

            return null;
        }
    }

    private async Task LoadCoverAsync()
    {
        var path = CoverPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var image = await ImageCacheService.GetFileAsync(path);
            if (image is null)
                return;
            _cover = image;
            OnPropertyChanged(nameof(Cover));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }
    }
    public string? FolderPath { get; set; }
    public string? SourceUrl { get; set; }
    public string SourceProvider { get; set; } = SourceCatalog.Other;
    public int PageCount { get; set; }
    public bool IsFavorite
    {
        get => _isFavorite;
        set => SetField(ref _isFavorite, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    /// <summary>Checkbox auf der Karte, solange der Auswahl-Modus aktiv ist oder etwas markiert wurde.</summary>
    public bool ShowSelection
    {
        get => _showSelection;
        set => SetField(ref _showSelection, value);
    }
    public int LastReadPageIndex { get; set; }

    public ReadingStatus ReadingStatus
    {
        get => _readingStatus;
        set
        {
            if (SetField(ref _readingStatus, value))
            {
                OnPropertyChanged(nameof(FooterText));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusText => ReadingStatus switch
    {
        ReadingStatus.Reading => "Am Lesen",
        ReadingStatus.Completed => "Abgeschlossen",
        _ => string.Empty,
    };

    public string FooterText
    {
        get
        {
            var pages = PageCountText;
            var status = ReadingStatus switch
            {
                ReadingStatus.Reading => "Am Lesen",
                ReadingStatus.Completed => "Abgeschlossen",
                _ => string.Empty,
            };
            if (pages.Length == 0)
                return status;
            return status.Length == 0 ? pages : pages + " · " + status;
        }
    }
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;
    public DateTime? LastOpened { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<string> Categories { get; set; } = new();

    public string CategoryLine => Categories.Count == 0 ? string.Empty : string.Join(" · ", Categories);

    public bool HasCategories => Categories.Count > 0;

    public string ArtistLine => string.IsNullOrWhiteSpace(Artist) ? SourceBadge : Artist;

    public string SourceBadge => SourceProvider switch
    {
        "nhentai" => "nhentai",
        "hitomi" => "Hitomi.la",
        "ehentai" => "E-Hentai",
        "exhentai" => "ExHentai",
        _ => SourceCatalog.DisplayName(SourceProvider),
    };

    public string PageCountText => PageCount switch
    {
        <= 0 => string.Empty,
        1 => "1 Seite",
        _ => PageCount + " Seiten",
    };

    public static LocalManga From(Manga manga) => new()
    {
        Id = manga.Id,
        Title = manga.Title,
        Artist = manga.Author,
        CoverPath = manga.CoverPath,
        FolderPath = manga.FolderPath,
        SourceUrl = manga.SourceUrl,
        SourceProvider = string.IsNullOrWhiteSpace(manga.Source) ? SourceCatalog.Other : manga.Source,
        PageCount = manga.PageCount,
        IsFavorite = manga.IsFavorite,
        LastReadPageIndex = manga.LastReadPageIndex,
        ReadingStatus = manga.ReadingStatus,
        DateAdded = manga.DateAdded,
        LastOpened = manga.LastOpened,
        Tags = manga.Tags.ToList(),
        Categories = manga.Categories.ToList(),
    };

    public Manga ToManga() => new()
    {
        Id = Id,
        Title = Title,
        Author = Artist,
        CoverPath = CoverPath,
        FolderPath = FolderPath,
        SourceUrl = SourceUrl,
        Source = SourceProvider,
        PageCount = PageCount,
        IsFavorite = IsFavorite,
        LastReadPageIndex = LastReadPageIndex,
        ReadingStatus = ReadingStatus,
        DateAdded = DateAdded,
        LastOpened = LastOpened,
        Tags = Tags.ToList(),
        Categories = Categories.ToList(),
    };
}
