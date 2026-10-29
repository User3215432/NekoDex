using System.IO;

namespace MangaLibraryApp;

/// <summary>Liest und schreibt Bibliotheks-Metadaten in <c>manga_library.db</c>.</summary>
public sealed class LibraryRepository
{
    private readonly Database _database;

    public LibraryRepository(Database database) => _database = database;

    public long Upsert(LocalManga item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var manga = item.ToManga();
        if (manga.Id <= 0)
            _database.AddManga(manga);
        else
            _database.UpdateManga(manga);
        item.Id = manga.Id;
        return manga.Id;
    }

    public int AddRange(IEnumerable<LocalManga> items)
    {
        var mangas = items.Select(item => item.ToManga()).ToList();
        return _database.AddMangas(mangas);
    }

    public IReadOnlyList<LocalManga> Search(MangaQuery query) =>
        _database.SearchMangas(query).Select(LocalManga.From).ToList();

    public void SetFavorite(long id, bool isFavorite) => _database.SetFavorite(id, isFavorite);

    public void SetLastReadPage(long id, int pageIndex) => _database.SetReadingProgress(id, pageIndex);

    public void SetReadingStatus(long id, ReadingStatus status) => _database.SetReadingStatus(id, status);

    public IReadOnlyList<string> GetBlacklistedTags() => _database.GetBlacklistedTags();

    public void AddBlacklistedTag(string tag) => _database.AddBlacklistedTag(tag);

    public void RemoveBlacklistedTag(string tag) => _database.RemoveBlacklistedTag(tag);

    public IReadOnlyList<string> ListCategories() => _database.ListCategories();

    public void AssignCategory(IEnumerable<long> mangaIds, string name) =>
        _database.AssignCategory(mangaIds, name);

    public void CreateCategory(string name) => _database.CreateCategory(name);

    public void RenameCategory(string oldName, string newName) => _database.RenameCategory(oldName, newName);

    public void DeleteCategory(string name) => _database.DeleteCategory(name);

    /// <summary>
    /// Entfernt den Titel aus der Datenbank. Mit <paramref name="deleteFiles"/> werden der gespeicherte Ordner
    /// bzw. die Archivdatei und ein separates Cover gelöscht, sofern der Pfad sicher ist.
    /// </summary>
    public string? Delete(LocalManga item, bool deleteFiles, string? libraryRoot)
    {
        ArgumentNullException.ThrowIfNull(item);
        string? warning = null;
        if (deleteFiles)
            warning = TryDeleteStoredFiles(item.FolderPath, item.CoverPath, libraryRoot);
        if (!_database.DeleteManga(item.Id) && warning is null)
            warning = "Der Eintrag war nicht mehr in der Datenbank.";
        return warning;
    }

    private static string? TryDeleteStoredFiles(string? folderPath, string? coverPath, string? libraryRoot)
    {
        var notes = new List<string>();
        TryDeleteOne(folderPath, libraryRoot, notes);
        if (!string.IsNullOrWhiteSpace(coverPath) && !ContainedBy(coverPath, folderPath))
            TryDeleteOne(coverPath, libraryRoot, notes);
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    private static void TryDeleteOne(string? path, string? libraryRoot, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            notes.Add("Ein Dateipfad war ungültig und wurde nicht gelöscht.");
            return;
        }

        if (!IsSafeToDelete(full, libraryRoot))
        {
            notes.Add("Dateien außerhalb der Bibliothek wurden nicht gelöscht.");
            return;
        }

        try
        {
            if (File.Exists(full))
                File.Delete(full);
            else if (Directory.Exists(full))
                Directory.Delete(full, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add(ex.Message);
        }
    }

    private static bool ContainedBy(string cover, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return false;

        try
        {
            var folderFull = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var coverFull = Path.GetFullPath(cover);
            return coverFull.StartsWith(folderFull, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSafeToDelete(string full, string? libraryRoot)
    {
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            return false;

        var candidate = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var drive = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (candidate.Length == 0 || string.Equals(candidate, drive, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(libraryRoot))
        {
            try
            {
                var library = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (candidate.StartsWith(library + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        if (File.Exists(full))
            return true;

        var rest = full.Substring(root.Length).Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var depth = rest.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries).Length;
        return depth >= 3;
    }
}
