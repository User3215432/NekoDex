using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace MangaLibraryApp;

// ════════════════════════════════════════════════════════════════════════
//  Dateisystem: gallery-dl-Ausgabe auswerten, Ordner und Archive scannen
//  (Metadaten wie Titel und Tags liest der MetadataParser)
// ════════════════════════════════════════════════════════════════════════

internal static class LibraryScanner
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif", ".tif", ".tiff", ".jxl", ".heic", ".heif",
    };

    /// <summary>Formate, die WPF ohne zusätzliche Windows-Codecs darstellen kann – bevorzugt für das Cover.</summary>
    private static readonly HashSet<string> NativeCoverExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff",
    };

    /// <summary>Archive, die wie Ordner mit Bildern behandelt werden (CBZ = ZIP mit Bildern).</summary>
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cbz", ".zip",
    };

    private static readonly Regex ChapterFolderRegex =
        new(@"^(c|ch|chap|chapter|vol|volume|v)[\s._-]*\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Dateinamen, die nur ein Kapitel benennen („Chapter“, „Übersetzer_Chapter“, „oneshot“) – der Titel steht im Ordner darüber.</summary>
    private static readonly Regex GenericNameRegex =
        new(@"(^|[\s._-])(chapter|chap|one[\s-]?shot)([\s._-]*\d+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Ablage der Cover-Vorschaubilder, die aus Archiven gezogen werden (setzt das Hauptfenster beim Start).</summary>
    public static string? CoverCacheDirectory { get; set; }

    public static bool IsArchivePath(string path) => ArchiveExtensions.Contains(Path.GetExtension(path));

    public sealed record FolderScan(int PageCount, string? CoverPath);

    /// <summary>Ist der Dateiname ein Bild (nach der Endung)?</summary>
    public static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string x, string y);

    /// <summary>Natürliche Sortierung wie im Explorer („2.jpg“ vor „10.jpg“).</summary>
    public static int NaturalCompare(string? x, string? y) =>
        x is null || y is null ? string.CompareOrdinal(x, y) : StrCmpLogicalW(x, y);

    /// <summary>
    /// Erkennt in einer Ausgabezeile von gallery-dl einen Dateipfad.
    /// Format: "C:\...\datei.jpg" (neu geladen) oder "# C:\...\datei.jpg" (bereits vorhanden, übersprungen).
    /// </summary>
    public static bool TryParseDownloadedPath(string line, out string path, out bool skipped)
    {
        path = string.Empty;
        skipped = false;

        var text = line.Trim();
        if (text.StartsWith("# ", StringComparison.Ordinal))
        {
            skipped = true;
            text = text[2..].TrimStart();
        }
        else if (text.StartsWith("* ", StringComparison.Ordinal))
        {
            text = text[2..].TrimStart();
        }

        // Erweitertes Pfadpräfix für lange Pfade: \\?\C:\...  bzw.  \\?\UNC\server\share\...
        if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            text = @"\\" + text[8..];
        else if (text.StartsWith(@"\\?\", StringComparison.Ordinal))
            text = text[4..];

        var isDrivePath = text.Length > 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && (text[2] == '\\' || text[2] == '/');
        var isUncPath = text.Length > 2 && text.StartsWith(@"\\", StringComparison.Ordinal);
        if (!isDrivePath && !isUncPath)
            return false;

        if (!Path.HasExtension(text))
            return false;

        try
        {
            path = Path.GetFullPath(text);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Gemeinsamer Elternordner aller Dateien – der Ordner des Bibliothekseintrags.</summary>
    public static string? FindCommonDirectory(IEnumerable<string> files)
    {
        string? common = null;

        foreach (var file in files)
        {
            var directory = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(directory))
                continue;

            if (common is null)
            {
                common = directory;
                continue;
            }

            while (common is not null && !IsSameOrChildPath(directory, common))
                common = Path.GetDirectoryName(common);

            if (common is null)
                return null;
        }

        return common;
    }

    public static bool IsSameOrChildPath(string path, string parent)
    {
        var p = path.TrimEnd('\\', '/');
        var r = parent.TrimEnd('\\', '/');
        return string.Equals(p, r, StringComparison.OrdinalIgnoreCase)
               || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Titel-Vorschlag aus dem Ordner- bzw. Archivnamen (ohne Endung);
    /// Kapitelnamen („c001“, „Chapter 3“) nehmen den Namen des Elternordners.
    /// </summary>
    public static string FolderTitle(string folder)
    {
        var trimmed = folder.TrimEnd('\\', '/');
        var name = IsArchivePath(trimmed) ? Path.GetFileNameWithoutExtension(trimmed) : Path.GetFileName(trimmed);

        if (ChapterFolderRegex.IsMatch(name) || GenericNameRegex.IsMatch(name))
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(trimmed)?.TrimEnd('\\', '/'));
            if (!string.IsNullOrWhiteSpace(parent))
                name = parent;
        }

        return string.IsNullOrWhiteSpace(name) ? "Unbenannt" : name;
    }

    // ── Bilder ──

    public static FolderScan ScanFolder(string folder)
    {
        var (pages, explicitCover) = EnumerateImages(folder);
        return new FolderScan(pages.Count, explicitCover ?? PickCover(pages));
    }

    /// <summary>Ein Cover für den Eintrag: bei Ordnern die erste Seite, bei Archiven ein (neu erzeugtes) Vorschaubild.</summary>
    public static string? FindFirstImage(string path)
    {
        if (File.Exists(path))
            return IsArchivePath(path) ? ScanArchive(path).Scan.CoverPath : null;

        var (pages, explicitCover) = EnumerateImages(path);
        return explicitCover ?? PickCover(pages);
    }

    /// <summary>
    /// Trennt die Seiten von einer ausdrücklichen Titelbild-Datei („cover.jpg“): Das Titelbild zählt nicht als Seite.
    /// Die Seiten sind natürlich sortiert (001, 002 … 010).
    /// </summary>
    private static (List<string> Pages, string? Cover) EnumerateImages(string folder)
    {
        var pages = new List<string>();
        var covers = new List<string>();
        if (!Directory.Exists(folder))
            return (pages, null);

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(folder, "*", options))
        {
            if (!ImageExtensions.Contains(Path.GetExtension(file)))
                continue;

            if (string.Equals(Path.GetFileNameWithoutExtension(file), "cover", StringComparison.OrdinalIgnoreCase))
                covers.Add(file);
            else
                pages.Add(file);
        }

        pages.Sort(NaturalCompare);
        covers.Sort(NaturalCompare);

        // Besteht der Ordner nur aus einer Datei namens „cover“, ist das die einzige Seite.
        if (pages.Count == 0 && covers.Count > 0)
        {
            pages.AddRange(covers);
            covers.Clear();
        }

        var cover = covers.FirstOrDefault(f => NativeCoverExtensions.Contains(Path.GetExtension(f))) ?? covers.FirstOrDefault();
        return (pages, cover);
    }

    private static string? PickCover(List<string> pages) =>
        pages.FirstOrDefault(f => NativeCoverExtensions.Contains(Path.GetExtension(f))) ?? pages.FirstOrDefault();

    // ── Einträge finden (Ordner-Scan) ──

    /// <summary>
    /// Alle Einträge unterhalb von <paramref name="root"/>: Ordner mit Bildern <b>und</b> Archive (.cbz/.zip).
    /// Jede Archivdatei ist ein eigener Eintrag (ein „Buch“); ob sie wirklich Bilder enthält, zeigt erst <see cref="BuildManga"/>.
    /// </summary>
    public static List<string> FindEntries(string root)
    {
        var entries = FindImageFolders(root);
        if (!Directory.Exists(root))
            return entries;

        var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true };
        foreach (var extension in ArchiveExtensions)
        {
            foreach (var file in Directory.EnumerateFiles(rootFull, "*" + extension, options))
            {
                if (ArchiveExtensions.Contains(Path.GetExtension(file)))
                    entries.Add(file);
            }
        }

        entries.Sort(NaturalCompare);
        return entries;
    }

    /// <summary>
    /// Findet alle Ordner unterhalb von <paramref name="root"/>, die Bilder enthalten und je einen Eintrag ergeben.
    /// Kapitelordner („c001“, „Chapter 3“) werden dem übergeordneten Titelordner zugeschlagen;
    /// liegen Bilder auch in Unterordnern, zählt nur der oberste Ordner. Der Wurzelordner selbst ist nie ein Eintrag.
    /// </summary>
    private static List<string> FindImageFolders(string root)
    {
        var folders = new List<string>();
        if (!Directory.Exists(root))
            return folders;

        var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true };

        foreach (var directory in Directory.EnumerateDirectories(rootFull, "*", options))
        {
            if (!ContainsImageDirectly(directory))
                continue;

            var gallery = directory;
            var name = Path.GetFileName(gallery);
            var parent = Path.GetDirectoryName(gallery);

            if (ChapterFolderRegex.IsMatch(name) && parent is not null && !IsSameOrChildPath(rootFull, parent))
                gallery = parent; // parent liegt unterhalb des Wurzelordners → Titelordner statt Kapitel

            candidates.Add(gallery.TrimEnd('\\', '/'));
        }

        // Oberste Ordner zuerst; alles, was in einem bereits gewählten Ordner liegt, entfällt.
        foreach (var candidate in candidates.OrderBy(c => c.Length).ThenBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            if (!folders.Any(chosen => IsSameOrChildPath(candidate, chosen)))
                folders.Add(candidate);
        }

        folders.Sort(NaturalCompare);
        return folders;
    }

    private static bool ContainsImageDirectly(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory).Any(f => ImageExtensions.Contains(Path.GetExtension(f)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Baut aus einem Ordner (Bilder + Metadaten) oder einer Archivdatei (CBZ/ZIP + ComicInfo.xml) einen Bibliothekseintrag.
    /// Der Pfad steht anschließend in <see cref="Manga.FolderPath"/>. Bei einem unlesbaren oder bildlosen Archiv ist <c>PageCount</c> 0.
    /// </summary>
    public static Manga BuildManga(string path, string? sourceUrl = null, string? customTitle = null, IEnumerable<string>? extraTags = null)
    {
        FolderScan scan;
        ParsedMetadata? info;
        if (File.Exists(path))
            (scan, info) = ScanArchive(path);
        else
            (scan, info) = (ScanFolder(path), MetadataParser.ParseFolder(path));

        var tags = new List<string>();
        tags.AddRange(info?.Tags ?? Array.Empty<string>());
        tags.AddRange(extraTags ?? Array.Empty<string>());

        var url = FirstNonBlank(sourceUrl, info?.SourceUrl);

        // Die Website nennt zuerst die Adresse (E-Hentai und ExHentai teilen sich bei gallery-dl dieselbe „category“), dann die Metadaten.
        var source = SourceCatalog.FromUrl(url);
        if (source == SourceCatalog.Other)
            source = info?.Source ?? SourceCatalog.Other;

        return new Manga
        {
            Title = FirstNonBlank(customTitle, info?.Title) ?? FolderTitle(path),
            Author = info?.Author,
            Description = info?.Description,
            Source = source,
            SourceUrl = url,
            FolderPath = path,
            CoverPath = scan.CoverPath,
            PageCount = scan.PageCount,
            IsFavorite = info?.IsFavorite ?? false,
            DateAdded = info?.DateAdded ?? SafeCreationTimeUtc(path),
            Tags = tags,
        };
    }

    /// <summary>Bestimmt nur die Website eines vorhandenen Eintrags neu (aus Adresse und Metadaten-Dateien) – ohne Cover zu erzeugen.</summary>
    public static string DetectSource(string? path, string? url)
    {
        var source = SourceCatalog.FromUrl(url);
        if (source != SourceCatalog.Other || string.IsNullOrWhiteSpace(path))
            return source;

        if (File.Exists(path))
        {
            if (!IsArchivePath(path))
                return SourceCatalog.Other;

            try
            {
                using var zip = ZipFile.OpenRead(path);
                return (MetadataParser.ParseComicInfo(zip) ?? ReadSiblingInfo(path))?.Source ?? SourceCatalog.Other;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return SourceCatalog.Other;
            }
        }

        return Directory.Exists(path) ? MetadataParser.ParseFolder(path)?.Source ?? SourceCatalog.Other : SourceCatalog.Other;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static DateTime SafeCreationTimeUtc(string path)
    {
        try
        {
            var created = File.Exists(path) ? File.GetCreationTimeUtc(path) : Directory.GetCreationTimeUtc(path);
            return created.Year > 1990 ? created : DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Bestimmt, welche Einträge ein Download ergeben hat.
    /// Normalfall: ein Ordner mit den geladenen Bildern. Mit <c>--cbz</c>/<c>--zip</c> meldet gallery-dl zwar weiterhin
    /// die Pfade der einzelnen Bilder, schreibt sie aber nur in „&lt;Ordner&gt;.cbz“ neben den (dann fast leeren) Ordner –
    /// dann sind die Archive die Einträge.
    /// </summary>
    public static List<string> ResolveImportTargets(IReadOnlyCollection<string> reportedFiles)
    {
        var existing = reportedFiles.Where(File.Exists).ToList();
        if (existing.Count > 0)
        {
            var folder = FindCommonDirectory(existing);
            return folder is null ? new List<string>() : new List<string> { folder };
        }

        var archives = new List<string>();
        var directories = reportedFiles
            .Select(Path.GetDirectoryName)
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => d!.TrimEnd('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            foreach (var extension in ArchiveExtensions)
            {
                var candidate = directory + extension;
                if (File.Exists(candidate))
                {
                    archives.Add(candidate);
                    break;
                }
            }
        }

        return archives;
    }

    // ── Archive (CBZ/ZIP) ──

    private const int MaxCoverCandidates = 8;
    private const long MaxCoverEntryBytes = 60L * 1024 * 1024;
    private const long MaxArchivePageBytes = 90L * 1024 * 1024;
    private const int CoverThumbnailWidth = 400;

    private static bool IsImageEntry(string fullName)
    {
        if (fullName.EndsWith('/') || fullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase))
            return false;

        var name = Path.GetFileName(fullName);
        return !name.StartsWith("._", StringComparison.Ordinal) && ImageExtensions.Contains(Path.GetExtension(name));
    }

    /// <summary>
    /// Liest ein Archiv: Seitenzahl, Cover-Vorschaubild (im Cache) und Metadaten.
    /// Metadaten kommen aus der <c>ComicInfo.xml</c> im Archiv, sonst aus der <c>info.json</c> im gleichnamigen Ordner
    /// neben dem Archiv (so legt gallery-dl sie bei <c>--cbz</c> ab).
    /// </summary>
    public static (FolderScan Scan, ParsedMetadata? Info) ScanArchive(string archivePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);

            var (pages, explicitCover) = SplitArchiveEntries(zip);

            var candidates = new List<ZipArchiveEntry>();
            if (explicitCover is not null)
                candidates.Add(explicitCover);
            candidates.AddRange(pages.Take(MaxCoverCandidates));

            var cover = pages.Count == 0 ? null : CreateArchiveCover(archivePath, candidates);
            var info = MetadataParser.ParseComicInfo(zip) ?? ReadSiblingInfo(archivePath);
            return (new FolderScan(pages.Count, cover), info);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            return (new FolderScan(0, null), null); // kein gültiges ZIP / nicht lesbar
        }
    }

    /// <summary>
    /// Trennt die Seiten eines Archivs (natürlich sortiert) von einer ausdrücklichen Titelbild-Datei („cover.jpg“).
    /// Besteht das Archiv nur aus einer solchen Datei, ist sie die einzige Seite.
    /// </summary>
    private static (List<ZipArchiveEntry> Pages, ZipArchiveEntry? Cover) SplitArchiveEntries(ZipArchive zip)
    {
        var pages = new List<ZipArchiveEntry>();
        ZipArchiveEntry? cover = null;

        foreach (var entry in zip.Entries)
        {
            if (entry.Length == 0 || !IsImageEntry(entry.FullName))
                continue;

            if (string.Equals(Path.GetFileNameWithoutExtension(entry.Name), "cover", StringComparison.OrdinalIgnoreCase))
                cover ??= entry;
            else
                pages.Add(entry);
        }

        pages.Sort((a, b) => NaturalCompare(a.FullName, b.FullName));
        if (pages.Count == 0 && cover is not null)
        {
            pages.Add(cover);
            cover = null;
        }

        return (pages, cover);
    }

    // ── Seiten zum Lesen (Reader) ──

    /// <summary>Die Seiten eines Ordners in Lesereihenfolge (Dateipfade).</summary>
    public static List<string> ListFolderPages(string folder) => EnumerateImages(folder).Pages;

    /// <summary>Die Seiten eines Archivs in Lesereihenfolge (Namen der Einträge).</summary>
    public static List<string> ListArchivePages(string archivePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            return SplitArchiveEntries(zip).Pages.Select(e => e.FullName).ToList();
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            throw new IOException("Das Archiv ist beschädigt oder kein gültiges ZIP/CBZ.", ex);
        }
    }

    /// <summary>Liest den Inhalt eines Archiv-Eintrags in den Speicher.</summary>
    public static byte[] ReadArchiveEntry(string archivePath, string entryName)
    {
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var entry = zip.GetEntry(entryName) ?? throw new FileNotFoundException("Seite nicht im Archiv gefunden: " + entryName);
            using var buffer = CopyZipEntry(entry, MaxArchivePageBytes);
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or InvalidOperationException)
        {
            throw new IOException("Das Archiv ist beschädigt oder die Seite konnte nicht gelesen werden.", ex);
        }
    }

    /// <summary>
    /// Kopiert einen ZIP-Eintrag in den Speicher. <see cref="ZipArchiveEntry.Length"/> kann <c>-1</c> sein (Größe unbekannt) –
    /// dann wächst der Puffer; eine bekannte oder laufende Größe über <paramref name="maxBytes"/> wird abgelehnt.
    /// </summary>
    private static MemoryStream CopyZipEntry(ZipArchiveEntry entry, long maxBytes)
    {
        if (entry.Length > maxBytes)
            throw new IOException("Die Datei im Archiv ist ungewöhnlich groß.");

        var capacity = entry.Length is >= 0 and <= int.MaxValue ? (int)entry.Length : 128 * 1024;
        var buffer = new MemoryStream(capacity);
        try
        {
            using var source = entry.Open();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes)
                    throw new IOException("Die Datei im Archiv ist ungewöhnlich groß.");
            }

            buffer.Position = 0;
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private static ParsedMetadata? ReadSiblingInfo(string archivePath)
    {
        var directory = Path.ChangeExtension(archivePath, null);
        return Directory.Exists(directory) ? MetadataParser.ParseFolder(directory) : null;
    }

    /// <summary>
    /// Zieht die erste darstellbare Seite aus dem Archiv und legt sie als JPEG-Vorschaubild im Cache ab.
    /// Seiten in Formaten, die Windows nicht dekodieren kann (z. B. AVIF ohne Codec), werden übersprungen.
    /// </summary>
    private static string? CreateArchiveCover(string archivePath, List<ZipArchiveEntry> candidates)
    {
        var cacheDirectory = CoverCacheDirectory;
        if (string.IsNullOrEmpty(cacheDirectory))
            return null;

        try
        {
            var info = new FileInfo(archivePath);
            var key = $"{archivePath.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            var target = Path.Combine(cacheDirectory, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..24] + ".jpg");
            if (File.Exists(target))
                return target;

            Directory.CreateDirectory(cacheDirectory);

            foreach (var entry in candidates)
            {
                if (entry.Length > MaxCoverEntryBytes)
                    continue;

                try
                {
                    using var buffer = CopyZipEntry(entry, MaxCoverEntryBytes);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bitmap.DecodePixelWidth = CoverThumbnailWidth;
                    bitmap.StreamSource = buffer;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));

                    // Erst in eine temporäre Datei schreiben: parallele Scans sollen nie ein halbes Bild sehen.
                    var temp = target + "." + Environment.CurrentManagedThreadId + ".tmp";
                    using (var output = File.Create(temp))
                        encoder.Save(output);
                    File.Move(temp, target, overwrite: true);
                    return target;
                }
                catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException
                                               or InvalidDataException or IOException or COMException or ArgumentException)
                {
                    // diese Seite ist nicht darstellbar → nächste versuchen
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache nicht beschreibbar: Eintrag bleibt ohne Cover
        }

        return null;
    }
}
