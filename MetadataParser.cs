using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace MangaLibraryApp;

/// <summary>Was ein Metadaten-Parser aus JSON-/XML-Dateien herausliest.</summary>
/// <param name="Source">Kennung aus <see cref="SourceCatalog"/> („nhentai“ …) oder „other“.</param>
/// <param name="Tags">Alle Tags in Textform: „artist:name“, „parody:serie“, „character:name“, „language:english“, „female:glasses“, „romance“ …</param>
public sealed record ParsedMetadata(
    string? Title,
    string? Author,
    string? Description,
    string Source,
    string? SourceUrl,
    IReadOnlyList<string> Tags,
    string? Language = null,
    int? PageCount = null,
    bool IsFavorite = false,
    DateTime? DateAdded = null);

/// <summary>Ergebnis eines Imports in die Bibliothek (bei mehreren Archiven die Summe).</summary>
public sealed record ImportOutcome(string Title, bool IsNew, int PageCount, int Count = 1);

/// <summary>
/// Liest nach einem Download (oder beim Einlesen eines Ordners) die Metadaten-Dateien und schreibt Titel, Quelle und
/// <b>alle</b> Tags – Künstler, Gruppen, Parodien, Charaktere, Sprache, Genres – sauber in die Datenbank.
/// <para>
/// Unterstützte Dateien: die JSON-Dateien von gallery-dl (<c>--write-metadata</c> legt neben jede Datei eine
/// <c>&lt;datei&gt;.json</c>, <c>--write-info-json</c> eine <c>info.json</c> für die Galerie), die eigene <c>metadata.json</c>
/// und die <c>ComicInfo.xml</c> in CBZ-Archiven. Die Felder der Seiten (nhentai, Hitomi, E-Hentai/ExHentai) unterscheiden
/// sich; der Parser ordnet sie über Schlüssel-Tabellen einheitlich den Kategorien zu.
/// </para>
/// </summary>
public static class MetadataParser
{
    private const int MaxTags = 120;
    private const long MaxJsonBytes = 4L * 1024 * 1024;

    /// <summary>JSON-Schlüssel → Tag-Kategorie. Mehrere Schreibweisen je Kategorie, weil jede Seite eigene Namen verwendet.</summary>
    private static readonly (string Category, string[] Keys)[] CategoryKeys =
    {
        ("artist", new[] { "artist", "artists", "author", "authors", "creator", "creators", "penciller", "writer" }),
        ("group", new[] { "group", "groups", "circle", "circles" }),
        ("parody", new[] { "parody", "parodies", "series", "copyright", "source_series" }),
        ("character", new[] { "character", "characters" }),
        ("language", new[] { "language", "languages" }),
        ("category", new[] { "type", "types", "eh_category" }),
        ("convention", new[] { "convention", "conventions", "event" }),
        ("origin", new[] { "origin" }),
    };

    /// <summary>Schlüssel mit „gewöhnlichen“ Tags (Genres, Inhalte); Werte können selbst „namespace:tag“ enthalten (E-Hentai).</summary>
    private static readonly string[] PlainTagKeys = { "tags", "tag", "genre", "genres", "content", "contents", "keywords" };

    /// <summary>Booru-artige Zeichenketten mit durch Leerzeichen getrennten Tags.</summary>
    private static readonly string[] SpaceSeparatedTagKeys = { "tag_string", "tag_string_general" };

    private static readonly string[] TitleKeys = { "title", "title_en", "title_pretty", "japanese_title", "title_ja", "title_jpn" };
    private static readonly string[] AuthorKeys = { "artist", "artists", "author", "authors", "creator", "uploader" };
    private static readonly string[] UrlKeys = { "sourceUrl", "source_url", "gallery_url", "galleryUrl", "web" };
    private static readonly string[] PageKeys = { "count", "total", "num_pages", "pages", "totalPages", "filecount" };

    /// <summary>
    /// Beschriftungen in der „Summary“ von ComicInfo.xml (<c>Parodies: touhou project</c>, <c>Characters: a, b</c>, <c>Languages: english, translated</c> …),
    /// wie sie doujins.com-, HentaiNexus- und E-Hentai-Exporte schreiben → Tag-Kategorie. Alles andere (Pages, Rating, Uploader …) wird ignoriert.
    /// </summary>
    private static readonly Dictionary<string, string> SummaryLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Parody"] = "parody", ["Parodies"] = "parody",
        ["Character"] = "character", ["Characters"] = "character",
        ["Language"] = "language", ["Languages"] = "language",
        ["Category"] = "category",
        ["Circle"] = "group", ["Group"] = "group", ["Groups"] = "group",
        ["Artist"] = "artist", ["Artists"] = "artist",
        ["Publisher"] = "publisher",
        ["Convention"] = "convention",
    };

    /// <summary>„Chapter“, „Chapter 3“, „Ch. 12“, „Vol. 2“, „Oneshot“ oder nur eine Zahl – kein Werktitel (Mihon/Tachiyomi-Exporte).</summary>
    private static readonly Regex GenericTitleRegex = new(
        @"^(chapter|chap\.?|ch\.?|episode|ep\.?|volume|vol\.?|book|part|extra|special|one[\s-]?shot)?\s*#?\s*\d*([.,]\d+)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>E-Hentai schreibt übersetzte Werke als „English TR“.</summary>
    private static readonly Regex TranslatedLanguageRegex = new(@"^([\p{L}]+)\s+TR$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Doujin-Titel enthalten die Sprache in eckigen Klammern: „… (Touhou Project) [English] {doujins.com}“.</summary>
    private static readonly Regex TitleLanguageRegex = new(
        @"\[\s*(english|japanese|chinese|korean|spanish|german|french|italian|russian|portuguese|polish|thai|vietnamese|indonesian|turkish|arabic|dutch|swedish|czech|hungarian|ukrainian|filipino|tagalog|hebrew|greek|finnish|romanian|bulgarian)\s*\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, string> LanguageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "english", ["ja"] = "japanese", ["jp"] = "japanese", ["zh"] = "chinese", ["ko"] = "korean", ["de"] = "german",
        ["fr"] = "french", ["es"] = "spanish", ["it"] = "italian", ["ru"] = "russian", ["pt"] = "portuguese", ["nl"] = "dutch",
        ["pl"] = "polish", ["th"] = "thai", ["vi"] = "vietnamese", ["id"] = "indonesian", ["tr"] = "turkish", ["ar"] = "arabic",
    };

    // ════════════════════════════════════════════════════════════════════
    //  Einlesen
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Sucht in einem Ordner nach Metadaten, in dieser Reihenfolge: <c>metadata.json</c> → <c>info.json</c> → die erste JSON-Datei neben einem Bild
    /// (<c>001.jpg.json</c> von <c>--write-metadata</c>). Liefert <c>null</c>, wenn nichts Lesbares vorhanden ist.
    /// </summary>
    public static ParsedMetadata? ParseFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return null;

            var own = Path.Combine(folder, "metadata.json");
            if (File.Exists(own))
                return ParseFile(own, atEntryRoot: true);

            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };

            var infos = Directory.EnumerateFiles(folder, "info.json", options).ToList();
            if (infos.Count > 0)
            {
                infos.Sort(LibraryScanner.NaturalCompare);
                return ParseFile(infos[0], IsDirectlyIn(infos[0], folder));
            }

            // Eine JSON-Datei neben jedem Bild: die Galerie-Felder stehen in allen gleich – die erste genügt.
            var sidecars = Directory.EnumerateFiles(folder, "*.json", options)
                .Where(f => LibraryScanner.IsImageFile(Path.GetFileNameWithoutExtension(f)))
                .ToList();
            if (sidecars.Count > 0)
            {
                sidecars.Sort(LibraryScanner.NaturalCompare);
                var first = sidecars.OrderBy(f => IsDirectlyIn(f, folder) ? 0 : 1).First();
                return ParseFile(first, IsDirectlyIn(first, folder));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Metadaten sind optional
        }

        return null;
    }

    private static bool IsDirectlyIn(string file, string folder) =>
        string.Equals(Path.GetDirectoryName(file)?.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <param name="atEntryRoot">
    /// <c>true</c>, wenn die Datei direkt im Eintragsordner liegt. Nur dann ist „title“ der Titel des Ganzen (nhentai, Hitomi, E-Hentai …);
    /// in Kapitelordnern ist „title“ der Kapitelname, dort liefert „manga“ die Serie.
    /// </param>
    public static ParsedMetadata? ParseFile(string path, bool atEntryRoot)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxJsonBytes)
                return null;

            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            return ParseJson(document.RootElement, atEntryRoot);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Wertet ein JSON-Objekt aus (siehe <see cref="ParseFile"/>).</summary>
    public static ParsedMetadata? ParseJson(JsonElement root, bool atEntryRoot)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var title = First(root, "manga") ?? (atEntryRoot ? First(root, TitleKeys) : null);

        var tags = new List<string>();

        // Kategorisierte Felder: artist → „artist:name“ usw.
        foreach (var (category, keys) in CategoryKeys)
        {
            foreach (var key in keys)
            {
                if (root.TryGetProperty(key, out var element))
                    foreach (var value in Strings(element))
                        AddTag(tags, category, value);
            }
        }

        // „category“ ist bei gallery-dl der Name des Extraktors („nhentai“), nicht der Inhaltstyp – daher nur ohne „subcategory“.
        if (!root.TryGetProperty("subcategory", out _) && root.TryGetProperty("category", out var categoryElement))
        {
            foreach (var value in Strings(categoryElement))
                AddTag(tags, "category", value);
        }

        // Gewöhnliche Tags; E-Hentai liefert sie bereits als „namespace:tag“.
        foreach (var key in PlainTagKeys)
        {
            if (!root.TryGetProperty(key, out var element))
                continue;

            if (element.ValueKind == JsonValueKind.String)
            {
                // Eine Zeichenkette: durch Komma getrennt, sonst ein einzelnes Tag.
                var text = element.GetString() ?? string.Empty;
                foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    AddTag(tags, string.Empty, part);
            }
            else
            {
                foreach (var value in Strings(element))
                    AddTag(tags, string.Empty, value);
            }
        }

        foreach (var key in SpaceSeparatedTagKeys)
        {
            if (root.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String)
            {
                foreach (var part in (element.GetString() ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    AddTag(tags, string.Empty, part);
            }
        }

        // Sprache: Name bevorzugt, sonst aus dem Kürzel („en“ → english).
        var language = tags.FirstOrDefault(t => t.StartsWith("language:", StringComparison.Ordinal))?[9..];
        if (language is null && First(root, "lang") is { } code)
        {
            language = LanguageCodes.TryGetValue(code, out var name) ? name : TagNames.CleanName(code);
            if (language.Length > 0)
                AddTag(tags, "language", language);
        }

        var authorNames = new List<string>();
        foreach (var key in AuthorKeys)
        {
            if (root.TryGetProperty(key, out var element))
                authorNames.AddRange(Strings(element));
        }

        var author = authorNames.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();

        // Website: Adresse (falls vorhanden) → eigener „source“-Eintrag → gallery-dl-„category“.
        string? sourceUrl = null;
        foreach (var key in UrlKeys)
        {
            if (First(root, key) is { } candidate && IsHttpUrl(candidate))
            {
                sourceUrl = candidate;
                break;
            }
        }

        var source = SourceCatalog.FromUrl(sourceUrl);
        if (source == SourceCatalog.Other)
            source = SourceCatalog.FromName(First(root, "source", "site"));
        if (source == SourceCatalog.Other)
            source = SourceCatalog.FromName(First(root, "category"));

        // Fehlt die Adresse, lässt sie sich für nhentai/Hitomi aus der Galerie-Nummer bilden – damit gehen „Quelle öffnen“ und „Online lesen“.
        sourceUrl ??= SynthesizeUrl(source, Scalar(root, "gallery_id", "id", "gid"));

        int? pages = null;
        foreach (var key in PageKeys)
        {
            if (root.TryGetProperty(key, out var element) && TryGetInt(element, out var count) && count > 0)
            {
                pages = count;
                break;
            }
        }

        var favorite = root.TryGetProperty("favorite", out var fav) && fav.ValueKind == JsonValueKind.True;

        DateTime? added = null;
        if (First(root, "downloadDate", "dateAdded") is { } dateText
            && DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            added = parsed.UtcDateTime;

        return new ParsedMetadata(
            title,
            author.Count > 0 ? string.Join(", ", author) : null,
            First(root, "description", "summary"),
            source,
            sourceUrl,
            Distinct(tags),
            language,
            pages,
            favorite,
            added);
    }

    /// <summary>Liest die ComicRack-/Mihon-<c>ComicInfo.xml</c> aus einem Archiv (Title, Series, Writer, Penciller, Genre, Tags, Summary, Web …).</summary>
    public static ParsedMetadata? ParseComicInfo(ZipArchive zip)
    {
        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("ComicInfo.xml", StringComparison.OrdinalIgnoreCase)
            || e.FullName.EndsWith("/ComicInfo.xml", StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Length == 0 || (entry.Length >= 0 && entry.Length > 2_000_000))
            return null;

        try
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, // keine externen Entitäten
                XmlResolver = null,
            });

            var root = XDocument.Load(reader).Root;
            if (root is null)
                return null;

            string? Field(string name)
            {
                var value = root.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }

            var tags = new List<string>();

            // Writer und Penciller sind bei Doujin-Archiven Autor/Zeichner → Tags „artist:…“.
            var people = new[] { Field("Writer"), Field("Penciller") }
                .Where(p => p is not null)
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var person in people)
                AddTag(tags, "artist", person);

            foreach (var key in new[] { "Genre", "Tags" })
            {
                if (Field(key) is { } value)
                {
                    foreach (var part in value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        AddTag(tags, string.Empty, part);
                }
            }

            string? language = null;
            if (Field("LanguageISO") is { } iso)
            {
                language = LanguageCodes.TryGetValue(iso, out var name) ? name : TagNames.CleanName(iso);
                AddTag(tags, "language", language);
            }

            // Mihon-/Tachiyomi-Exporte schreiben als <Title> nur „Chapter“ – der Werktitel steht dann in <Series>.
            var title = Field("Title");
            var series = Field("Series");
            if (series is not null && (title is null || IsGenericTitle(title)))
                title = series;
            title ??= series;

            var summary = Field("Summary");
            if (summary is not null)
                ReadSummaryLabels(summary, tags, ref language);

            // Ohne Sprachangabe verrät oft der Titel sie: „… [English] {doujins.com}“.
            if (language is null && title is not null && TitleLanguageRegex.Match(title) is { Success: true } inTitle)
            {
                language = inTitle.Groups[1].Value.ToLowerInvariant();
                AddTag(tags, "language", language);
            }

            string? sourceUrl = null;
            if (Field("Web") is { } web)
            {
                // Mehrere Adressen sind möglich – die erste gültige http(s)-Adresse zählt.
                sourceUrl = web.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(u => u.Trim().TrimEnd(',', ';'))
                    .FirstOrDefault(IsHttpUrl);
            }

            return new ParsedMetadata(
                title,
                people.Count > 0 ? string.Join(", ", people) : null,
                summary,
                SourceCatalog.FromUrl(sourceUrl),
                sourceUrl,
                Distinct(tags),
                language,
                int.TryParse(Field("PageCount"), out var pageCount) && pageCount > 0 ? pageCount : null);
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException
                                       or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return null; // kaputte ComicInfo.xml: Archiv trotzdem aufnehmen
        }
    }

    /// <summary>Ein Titel wie „Chapter“, „Ch. 3“ oder „7“ sagt nichts über das Werk.</summary>
    internal static bool IsGenericTitle(string title) => GenericTitleRegex.IsMatch(title.Trim());

    /// <summary>Liest Zeilen der Form „Parodies: a, b“ aus der Summary und legt die passenden Tags an.</summary>
    private static void ReadSummaryLabels(string summary, List<string> tags, ref string? language)
    {
        foreach (var line in summary.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || colon > 20 || !SummaryLabels.TryGetValue(line[..colon].Trim(), out var category))
                continue;

            foreach (var part in line[(colon + 1)..].Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var value = part;

                if (category == "language")
                {
                    if (TranslatedLanguageRegex.Match(value) is { Success: true } translated)
                    {
                        value = translated.Groups[1].Value;
                        AddTag(tags, "language", "translated");
                    }

                    if (!value.Equals("translated", StringComparison.OrdinalIgnoreCase))
                        language ??= TagNames.CleanName(value);
                }

                AddTag(tags, category, value);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Übernehmen in die Datenbank
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Nimmt das Ergebnis eines Downloads in die Datenbank auf. Ziele sind meist ein Ordner mit Bildern, bei <c>--cbz</c>/<c>--zip</c>
    /// die Archive neben den Ordnern (siehe <see cref="LibraryScanner.ResolveImportTargets"/>); je Ziel entsteht ein Eintrag.
    /// </summary>
    public static ImportOutcome ImportDownload(Database db, IReadOnlyCollection<string> reportedFiles, string url, string? customTitle,
        IEnumerable<string> customTags)
    {
        var targets = LibraryScanner.ResolveImportTargets(reportedFiles);
        if (targets.Count == 0)
            throw new InvalidOperationException("Die geladenen Dateien wurden auf der Festplatte nicht gefunden.");

        var tagList = customTags.ToList();
        ImportOutcome? first = null;
        var anyNew = false;
        var pages = 0;

        for (var i = 0; i < targets.Count; i++)
        {
            // Die URL und der Wunschtitel gehören zum ersten Eintrag (ein Titel gilt nur einmal).
            var result = Store(db, targets[i], i == 0 ? url : null, i == 0 ? customTitle : null, tagList);
            first ??= result;
            anyNew |= result.IsNew;
            pages += result.PageCount;
        }

        return new ImportOutcome(first!.Title, anyNew, pages, targets.Count);
    }

    /// <summary>
    /// Liest Ordner bzw. Archiv samt Metadaten und legt den Eintrag an – oder aktualisiert den vorhandenen (erneuter Download):
    /// Titel, Favorit und eigene Tags des Nutzers bleiben dabei erhalten, neue Tags kommen hinzu.
    /// </summary>
    public static ImportOutcome Store(Database db, string target, string? url, string? customTitle, IEnumerable<string> customTags)
    {
        var fresh = LibraryScanner.BuildManga(target, url, customTitle, customTags);
        if (fresh.PageCount == 0)
            throw new InvalidOperationException("Im Ergebnis wurden keine Bilder gefunden.");

        try
        {
            // Bekannt ist ein Eintrag zuerst über seinen Ort (auch wenn ein Ordner-Scan ihn ohne URL angelegt hat).
            // Über die URL zählt er nur, wenn er nicht zu einer anderen, noch vorhandenen Datei gehört – mehrere Bände
            // teilen sich oft die Adresse ihrer Serie, und einer davon darf nicht versehentlich überschrieben werden.
            var current = db.GetMangaByFolder(target);
            if (current is null && url is not null)
            {
                var byUrl = db.GetMangaBySourceUrl(url);
                if (byUrl is not null
                    && (string.IsNullOrWhiteSpace(byUrl.FolderPath)
                        || !(File.Exists(byUrl.FolderPath) || Directory.Exists(byUrl.FolderPath))))
                    current = byUrl;
            }

            if (current is null)
            {
                fresh.DateAdded = DateTime.UtcNow;
                db.AddManga(fresh);
                return new ImportOutcome(fresh.Title, true, fresh.PageCount);
            }

            if (!string.IsNullOrWhiteSpace(customTitle))
                current.Title = fresh.Title;
            if (string.IsNullOrWhiteSpace(current.Author))
                current.Author = fresh.Author;
            if (string.IsNullOrWhiteSpace(current.Description))
                current.Description = fresh.Description;

            current.SourceUrl ??= fresh.SourceUrl;
            if (current.Source == SourceCatalog.Other)
                current.Source = fresh.Source;
            current.FolderPath = target;
            current.PageCount = fresh.PageCount;
            current.CoverPath = fresh.CoverPath ?? current.CoverPath;
            current.Tags = current.Tags.Union(fresh.Tags, StringComparer.OrdinalIgnoreCase).ToList();

            db.UpdateManga(current);
            return new ImportOutcome(current.Title, false, current.PageCount);
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException("Die Datenbank konnte den Eintrag nicht speichern: " + ex.Message, ex);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Hilfsfunktionen
    // ════════════════════════════════════════════════════════════════════

    private static void AddTag(List<string> tags, string category, string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0 || tags.Count >= MaxTags * 2)
            return;

        // Hitomi kennzeichnet Geschlechts-Tags mit Symbolen („Glasses ♀“) – daraus wird female:glasses wie bei E-Hentai.
        if (category.Length == 0 && value.Length > 1)
        {
            if (value[^1] == '♀')
            {
                category = "female";
                value = value[..^1].Trim();
            }
            else if (value[^1] == '♂')
            {
                category = "male";
                value = value[..^1].Trim();
            }
        }

        var tag = TagNames.Normalize(category.Length == 0 ? value : category + ":" + value);
        if (tag.Length > 0)
            tags.Add(tag);
    }

    private static List<string> Distinct(List<string> tags) =>
        tags.Distinct(StringComparer.Ordinal).Take(MaxTags).ToList();

    /// <summary>Alle Texte eines JSON-Werts: Zeichenkette, Liste, Objekt mit „name“ bzw. Objekt aus Listen ({"general": [...]}).</summary>
    private static IEnumerable<string> Strings(JsonElement element, int depth = 0)
    {
        if (depth > 3)
            yield break;

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    yield return text.Trim();
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    foreach (var value in Strings(item, depth + 1))
                        yield return value;
                break;

            case JsonValueKind.Object:
                if (element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    foreach (var value in Strings(name, depth + 1))
                        yield return value;
                }
                else
                {
                    foreach (var property in element.EnumerateObject())
                        foreach (var value in Strings(property.Value, depth + 1))
                            yield return value;
                }

                break;
        }
    }

    private static string? First(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (root.TryGetProperty(key, out var element) && Strings(element).FirstOrDefault() is { Length: > 0 } text)
                return text;
        }

        return null;
    }

    /// <summary>Wert als Text, auch wenn er als Zahl gespeichert ist (gallery-dl schreibt IDs wie <c>"gallery_id": 123456</c> als Zahl).</summary>
    private static string? Scalar(JsonElement root, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!root.TryGetProperty(key, out var element))
                continue;

            var text = element.ValueKind switch
            {
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.String => element.GetString()?.Trim(),
                _ => null,
            };

            if (!string.IsNullOrEmpty(text))
                return text;
        }

        return null;
    }

    private static bool TryGetInt(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static bool IsHttpUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string? SynthesizeUrl(string source, string? id)
    {
        if (id is null || id.Length == 0 || !id.All(char.IsAsciiDigit))
            return null;

        return source switch
        {
            "nhentai" => $"https://nhentai.net/g/{id}/",
            "hitomi" => $"https://hitomi.la/galleries/{id}.html",
            _ => null,
        };
    }
}
