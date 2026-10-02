using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace MangaLibraryApp;

// ════════════════════════════════════════════════════════════════════════
//  Quellen (Websites)
// ════════════════════════════════════════════════════════════════════════

/// <param name="Id">Kennung in der Datenbank (Spalte <c>mangas.source</c>).</param>
/// <param name="Root">Startseite – dient dem Reader als Referer für Bildabrufe.</param>
public sealed record SourceSite(string Id, string DisplayName, string[] Domains, string Root);

/// <summary>Die unterstützten Seiten und die Zuordnung von URLs bzw. Namen (z. B. gallery-dl-„category“) zu einer Quelle.</summary>
public static class SourceCatalog
{
    /// <summary>Alles, was keiner der gelisteten Seiten zugeordnet werden kann (lokale Dateien, andere Websites).</summary>
    public const string Other = "other";

    public static IReadOnlyList<SourceSite> Sites { get; } = new SourceSite[]
    {
        new("nhentai", "nhentai", new[] { "nhentai.net", "nhentai.xxx", "nhentai.to" }, "https://nhentai.net/"),
        new("hitomi", "Hitomi", new[] { "hitomi.la" }, "https://hitomi.la/"),
        new("ehentai", "E-Hentai", new[] { "e-hentai.org" }, "https://e-hentai.org/"),
        new("exhentai", "ExHentai", new[] { "exhentai.org" }, "https://exhentai.org/"),
    };

    /// <summary>Alle Kennungen inklusive <see cref="Other"/>.</summary>
    public static IReadOnlyList<string> AllIds { get; } = Sites.Select(s => s.Id).Append(Other).ToArray();

    public static SourceSite? Find(string? id) =>
        Sites.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Gültige Kennung zu <paramref name="id"/>; alles Unbekannte wird zu <see cref="Other"/>.</summary>
    public static string Normalize(string? id) => Find(id)?.Id ?? Other;

    public static string DisplayName(string? id) => Find(id)?.DisplayName ?? "Andere";

    /// <summary>Quelle anhand der Domain einer URL (auch Subdomains wie <c>g.e-hentai.org</c>).</summary>
    public static string FromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return Other;

        var host = uri.Host.ToLowerInvariant();
        foreach (var site in Sites)
        {
            foreach (var domain in site.Domains)
            {
                if (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal))
                    return site.Id;
            }
        }

        return Other;
    }

    /// <summary>Quelle anhand eines Namens wie „e-hentai“, „Hitomi.la“ oder der gallery-dl-<c>category</c>.</summary>
    public static string FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Other;

        var key = new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (key.Contains("exhentai", StringComparison.Ordinal)) return "exhentai";
        if (key.Contains("ehentai", StringComparison.Ordinal)) return "ehentai";
        if (key.Contains("nhentai", StringComparison.Ordinal)) return "nhentai";
        if (key.Contains("hitomi", StringComparison.Ordinal)) return "hitomi";
        return Other;
    }
}

// ════════════════════════════════════════════════════════════════════════
//  Tag-Namen:  "artist:name"  ⇄  Kategorie + Name
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// Ein Tag besteht aus Kategorie und Name (Spalten <c>tags.category</c> / <c>tags.name</c>). Als Text schreibt man ihn
/// <c>kategorie:name</c>; Tags ohne Kategorie stehen ohne Präfix. Alle Tags liegen klein geschrieben und normalisiert vor.
/// </summary>
public static class TagNames
{
    private const int MaxNameLength = 64;

    /// <summary>Bekannte Schreibweisen → einheitliche Kategorie. Ein leerer Wert steht für Tags ohne Kategorie.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["artist"] = "artist", ["artists"] = "artist", ["author"] = "artist", ["authors"] = "artist",
        ["group"] = "group", ["groups"] = "group", ["circle"] = "group", ["circles"] = "group",
        ["parody"] = "parody", ["parodies"] = "parody", ["series"] = "parody", ["copyright"] = "parody",
        ["character"] = "character", ["characters"] = "character",
        ["language"] = "language", ["languages"] = "language", ["lang"] = "language",
        ["category"] = "category", ["categories"] = "category", ["type"] = "category",
        ["convention"] = "convention", ["event"] = "convention",
        ["female"] = "female", ["male"] = "male", ["mixed"] = "mixed", ["other"] = "other",
        ["cosplayer"] = "cosplayer", ["reclass"] = "reclass",
        ["tag"] = string.Empty, ["tags"] = string.Empty, ["genre"] = string.Empty, ["genres"] = string.Empty,
        ["content"] = string.Empty, ["contents"] = string.Empty,
    };

    private static volatile HashSet<string> _searchNamespaces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Einheitliche Kategorie zu einem Präfix („artists“ → „artist“); <c>null</c>, wenn es kein bekannter Alias ist.</summary>
    public static string? CanonicalCategory(string? prefix) =>
        prefix is not null && Aliases.TryGetValue(prefix.Trim(), out var category) ? category : null;

    /// <summary>
    /// Merkt sich die Kategorien, die in der Datenbank vorkommen. Für die Such-Eingabe zählt „präfix:wort“ nur dann als Tag,
    /// wenn das Präfix ein bekannter Alias ist oder in der Datenbank vorkommt – „Re:Zero“ bleibt so ein gewöhnliches Suchwort.
    /// </summary>
    public static void RegisterCategories(IEnumerable<string> categories) =>
        _searchNamespaces = new HashSet<string>(categories.Where(c => c.Length > 0), StringComparer.OrdinalIgnoreCase);

    /// <summary>Ist <paramref name="prefix"/> in der Such-Eingabe (ohne „tag:“) als Tag-Kategorie zu verstehen?</summary>
    public static bool IsSearchNamespace(string? prefix) =>
        prefix is not null && (Aliases.TryGetValue(prefix, out var c) && c.Length > 0 || _searchNamespaces.Contains(prefix));

    /// <summary>Zerlegt „artist:name“ in Kategorie und Name; ohne gültiges Präfix ist die Kategorie leer.</summary>
    public static (string Category, string Name) Split(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (string.Empty, string.Empty);

        var text = raw.Trim();
        var colon = text.IndexOf(':');
        if (colon > 0)
        {
            var prefix = text[..colon].Trim();
            if (LooksLikeNamespace(prefix))
            {
                // „artist:“ ohne Namen ist kein Tag.
                var rest = CleanName(text[(colon + 1)..]);
                return rest.Length == 0
                    ? (string.Empty, string.Empty)
                    : (CanonicalCategory(prefix) ?? prefix.ToLowerInvariant(), rest);
            }
        }

        return (string.Empty, CleanName(text));
    }

    public static string Format(string category, string name) =>
        name.Length == 0 ? string.Empty : category.Length == 0 ? name : category + ":" + name;

    /// <summary>Einheitliche Textform eines Tags („Artist:Foo_Bar“ → „artist:foo bar“); leer, wenn nichts übrig bleibt.</summary>
    public static string Normalize(string? raw)
    {
        var (category, name) = Split(raw);
        return Format(category, name);
    }

    /// <summary>Unterstriche und Leerraum werden zu einem Leerzeichen; klein geschrieben, ohne Anführungszeichen und Steuerzeichen.</summary>
    public static string CleanName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var sb = new StringBuilder(raw.Length);
        var lastWasSpace = true; // führende Leerzeichen unterdrücken

        foreach (var ch in raw)
        {
            if (ch == '"' || (char.IsControl(ch) && !char.IsWhiteSpace(ch)))
                continue;

            if (ch == '_' || char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                    sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
        }

        var result = sb.ToString().TrimEnd().ToLowerInvariant();
        return result.Length > MaxNameLength ? result[..MaxNameLength].TrimEnd() : result;
    }

    /// <summary>Ein Präfix gilt als Kategorie, wenn es nur aus 2–20 Buchstaben besteht („female“, „misc“ – nicht „chapter 1“).</summary>
    private static bool LooksLikeNamespace(string prefix) =>
        prefix.Length is >= 2 and <= 20 && prefix.All(char.IsAsciiLetter);
}

/// <summary>
/// Reihenfolge und Beschriftung der Tag-Kategorien in der UI.
/// Artist, Parody, Character, Tag/Genre und Language sind standardmäßig geöffnet.
/// </summary>
public static class TagCategoryUi
{
    public static string Title(string? category) => (category ?? string.Empty) switch
    {
        "artist" => "Artist",
        "parody" => "Parody",
        "character" => "Character",
        "language" => "Language",
        "group" => "Group / Circle",
        "category" => "Category",
        "female" => "Female",
        "male" => "Male",
        "mixed" => "Mixed",
        "convention" => "Convention",
        "" => "Tag / Genre",
        _ => category is { Length: > 0 }
            ? char.ToUpperInvariant(category[0]) + category[1..]
            : "Tag / Genre",
    };

    public static int SortKey(string? category) => (category ?? string.Empty) switch
    {
        "artist" => 0,
        "parody" => 1,
        "character" => 2,
        "" => 3,
        "language" => 4,
        "group" => 5,
        "category" => 6,
        "female" => 7,
        "male" => 8,
        _ => 20,
    };

    public static bool DefaultExpanded(string? category) =>
        category is "artist" or "parody" or "character" or "" or "language";
}

// ════════════════════════════════════════════════════════════════════════
//  Datenmodelle
// ════════════════════════════════════════════════════════════════════════

/// <summary>Ein Eintrag der Bibliothek (Tabelle <c>mangas</c>) inklusive seiner Tags.</summary>
public sealed class Manga
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? Description { get; set; }

    /// <summary>Kennung der Website (<see cref="SourceCatalog"/>), von der das Werk stammt; sonst „other“.</summary>
    public string Source { get; set; } = SourceCatalog.Other;

    /// <summary>Adresse der Galerie auf der Website (kann bei mehreren Bänden einer Serie gleich sein).</summary>
    public string? SourceUrl { get; set; }

    /// <summary>Ort auf der Festplatte: Ordner mit Bildern oder Archivdatei (.cbz/.zip) – Spalte <c>mangas.filepath</c>.</summary>
    public string? FolderPath { get; set; }

    public string? CoverPath { get; set; }
    public int PageCount { get; set; }
    public bool IsFavorite { get; set; }

    /// <summary>Zeitpunkt des Hinzufügens (UTC).</summary>
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    /// <summary>Zeitpunkt, zu dem der Eintrag zuletzt geöffnet wurde (UTC).</summary>
    public DateTime? LastOpened { get; set; }

    /// <summary>Normalisierte Tags als Text („artist:name“, „romance“), alphabetisch sortiert.</summary>
    public List<string> Tags { get; set; } = new();
}

/// <summary>Ein Tag mit der Anzahl der Mangas, die ihn tragen. <paramref name="Name"/> ist die Textform („artist:name“).</summary>
public sealed record TagInfo(long Id, string Name, int MangaCount, string Category = "");

public enum MangaSort
{
    DateAddedDesc,
    TitleAsc,
    TitleDesc,
    PagesDesc,
    LastOpenedDesc,
}

/// <summary>Suchanfrage an die Bibliothek. Alle Bedingungen werden mit UND verknüpft; die Quellen untereinander mit ODER.</summary>
public sealed class MangaQuery
{
    /// <summary>Freitext-Begriffe; jeder muss in Titel, Autor oder einem Tag vorkommen (Teilstring).</summary>
    public List<string> Terms { get; } = new();

    /// <summary>Tags (Textform), die der Eintrag <b>alle</b> tragen muss.</summary>
    public List<string> IncludeTags { get; } = new();

    /// <summary>Tags, die der Eintrag keinesfalls tragen darf.</summary>
    public List<string> ExcludeTags { get; } = new();

    /// <summary>Gewählte Quellen: der Eintrag muss zu <b>mindestens einer</b> gehören. <c>null</c> = keine Einschränkung, leer = nichts.</summary>
    public List<string>? Sources { get; set; }

    public bool FavoritesOnly { get; set; }
    public MangaSort Sort { get; set; } = MangaSort.DateAddedDesc;
}

// ════════════════════════════════════════════════════════════════════════
//  SQLite-Datenbank
// ════════════════════════════════════════════════════════════════════════

/// <summary>Ein persistiertes Session-Cookie (z. B. <c>cf_clearance</c>) für eine Domain.</summary>
public sealed record StoredHttpCookie(string Domain, string Name, string Value, string Path = "/");

/// <summary>
/// Import von Browser-Cookies: Netscape-Cookie-Datei (Tabs) oder Header-Zeile
/// <c>cf_clearance=…; csrftoken=…</c>.
/// </summary>
internal static class HttpCookieImport
{
    private static readonly HashSet<string> AttributeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "path", "domain", "expires", "max-age", "secure", "httponly", "samesite", "priority", "sameparty",
    };

    public static string NormalizeDomain(string? domain)
    {
        var host = (domain ?? string.Empty).Trim().ToLowerInvariant();
        if (host.StartsWith("http://", StringComparison.Ordinal) || host.StartsWith("https://", StringComparison.Ordinal))
        {
            if (Uri.TryCreate(host, UriKind.Absolute, out var uri))
                host = uri.Host;
        }

        return host.TrimStart('.');
    }

    public static IReadOnlyList<StoredHttpCookie> Parse(string text, string fallbackDomain)
    {
        var fallback = NormalizeDomain(fallbackDomain);
        var result = new List<StoredHttpCookie>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
            return result;

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var netscape = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('#') && !line.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase))
                continue;

            var httpOnly = line.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase);
            if (httpOnly)
                line = line["#HttpOnly_".Length..];

            if (line.Contains('\t', StringComparison.Ordinal))
            {
                var parts = line.Split('\t');
                if (parts.Length >= 7)
                {
                    netscape = true;
                    Add(result, seen, parts[0], parts[5], parts[6], parts[2]);
                }
            }
        }

        if (netscape)
            return result;

        var header = string.Join(';', lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            var name = part[..eq].Trim();
            if (AttributeNames.Contains(name))
                continue;
            Add(result, seen, fallback, name, part[(eq + 1)..].Trim(), "/");
        }

        return result;
    }

    public static string ToHeader(IEnumerable<StoredHttpCookie> cookies) =>
        string.Join("; ", cookies.Select(c => c.Name + "=" + c.Value));

    private static void Add(
        List<StoredHttpCookie> result,
        HashSet<string> seen,
        string domain,
        string name,
        string value,
        string path)
    {
        var host = NormalizeDomain(domain);
        name = name.Trim();
        path = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        if (host.Length == 0 || name.Length == 0)
            return;
        if (!seen.Add(host + "\n" + name + "\n" + path))
            return;
        result.Add(new StoredHttpCookie(host, name, value, path));
    }
}

/// <summary>Wie Online-Tag-Häufigkeiten zusammengeführt werden.</summary>
public enum OnlineTagMerge
{
    /// <summary>Zählt Sichtungen (HTML-Scraper ohne API-Count).</summary>
    Add,
    /// <summary>Übernimmt den höheren Wert (nhentai API <c>count</c>).</summary>
    Max,
}

/// <summary>
/// SQLite-Persistenz der Bibliothek (Datei <c>manga_library.db</c>).
/// <para>
/// Tabellen: <c>mangas</c> (id, title, filepath, source …), <c>tags</c> (id, name, category) und die Verknüpfung
/// <c>manga_tags</c> (manga_id, tag_id) sowie <c>settings</c>, der unabhängige Online-Katalog
/// <c>online_site_tags</c> (Websuche, nicht mit <c>tags</c> verknüpft) und <c>http_cookies</c>
/// (Session-Cookies für Cloudflare-geschützte Seiten). Jede Methode öffnet eine eigene (gepoolte) Verbindung
/// und ist aus beliebigen Threads aufrufbar.
/// </para>
/// </summary>
public sealed class Database
{
    public const string FileName = "manga_library.db";
    private const string LegacyFileName = "library.db"; // Dateiname der Vorgängerversion (Schema 1)

    private const int SchemaVersion = 2;

    /// <summary>Trennzeichen für GROUP_CONCAT der Tags (ASCII "Unit Separator" – kommt in Tag-Namen nicht vor).</summary>
    private const char TagSeparator = '\u001F';

    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private const string TagText = "CASE WHEN t.category = '' THEN t.name ELSE t.category || ':' || t.name END";

    private const string MangaColumns =
        "m.id, m.title, m.author, m.description, m.source_url, m.filepath, m.cover_path, " +
        "m.page_count, m.is_favorite, m.date_added, m.last_opened, m.source, " +
        "(SELECT GROUP_CONCAT(" + TagText + ", char(31)) FROM manga_tags mt JOIN tags t ON t.id = mt.tag_id " +
        "WHERE mt.manga_id = m.id) AS tag_list";

    /// <summary>
    /// Schema 2. Der Primärschlüssel von <c>manga_tags</c> ist (tag_id, manga_id): Alle Werke eines Tags liegen als sortierter
    /// Bereich im Index, das UND über mehrere Tags wird damit zu einem schnellen Bereichs-Join; der zweite Index
    /// (manga_id, tag_id) liefert umgekehrt die Tags eines Werks.
    /// </summary>
    private const string SchemaSql = """
        CREATE TABLE mangas (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            title       TEXT    NOT NULL COLLATE NOCASE,
            filepath    TEXT,
            source      TEXT    NOT NULL DEFAULT 'other',
            author      TEXT,
            description TEXT,
            source_url  TEXT,
            cover_path  TEXT,
            page_count  INTEGER NOT NULL DEFAULT 0,
            is_favorite INTEGER NOT NULL DEFAULT 0,
            date_added  TEXT    NOT NULL,
            last_opened TEXT
        );
        CREATE INDEX ix_mangas_filepath   ON mangas (filepath COLLATE NOCASE);
        CREATE INDEX ix_mangas_source_url ON mangas (source_url);
        CREATE INDEX ix_mangas_source     ON mangas (source, date_added);
        CREATE INDEX ix_mangas_title      ON mangas (title);
        CREATE INDEX ix_mangas_date_added ON mangas (date_added);
        CREATE INDEX ix_mangas_favorite   ON mangas (is_favorite);

        CREATE TABLE tags (
            id       INTEGER PRIMARY KEY AUTOINCREMENT,
            name     TEXT NOT NULL,
            category TEXT NOT NULL DEFAULT '',
            UNIQUE (category, name)
        );
        CREATE INDEX ix_tags_name ON tags (name);

        CREATE TABLE manga_tags (
            manga_id INTEGER NOT NULL REFERENCES mangas (id) ON DELETE CASCADE,
            tag_id   INTEGER NOT NULL REFERENCES tags   (id) ON DELETE CASCADE,
            PRIMARY KEY (tag_id, manga_id)
        ) WITHOUT ROWID;
        CREATE INDEX ix_manga_tags_manga ON manga_tags (manga_id, tag_id);

        CREATE TABLE settings (
            name  TEXT PRIMARY KEY,
            value TEXT
        );

        CREATE TABLE online_site_tags (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            source_site TEXT    NOT NULL,
            tag_name    TEXT    NOT NULL,
            category    TEXT    NOT NULL DEFAULT '',
            count       INTEGER NOT NULL DEFAULT 0,
            UNIQUE (source_site, category, tag_name)
        );
        CREATE INDEX ix_online_site_tags_site ON online_site_tags (source_site, count DESC);
        CREATE INDEX ix_online_site_tags_name ON online_site_tags (source_site, tag_name);

        CREATE TABLE http_cookies (
            id     INTEGER PRIMARY KEY AUTOINCREMENT,
            domain TEXT    NOT NULL,
            name   TEXT    NOT NULL,
            value  TEXT    NOT NULL,
            path   TEXT    NOT NULL DEFAULT '/',
            UNIQUE (domain, name, path)
        );
        CREATE INDEX ix_http_cookies_domain ON http_cookies (domain);
        """;

    private readonly string _connectionString;

    /// <summary>Standardpfad: <c>%LocalAppData%\MangaLibraryApp\manga_library.db</c>.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MangaLibraryApp",
        FileName);

    public string DatabasePath { get; }

    public Database(string? databasePath = null)
    {
        try
        {
            SQLitePCL.Batteries_V2.Init();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Die SQLite-Laufzeitbibliothek (e_sqlite3) konnte nicht geladen werden: " + ex.Message, ex);
        }

        DatabasePath = Path.GetFullPath(string.IsNullOrWhiteSpace(databasePath) ? DefaultPath : databasePath);

        var directory = Path.GetDirectoryName(DatabasePath);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("Ungültiger Datenbankpfad: " + DatabasePath);

        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Keine Schreibrechte für die Datenbank in \"" + directory + "\": " + ex.Message, ex);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();

        ImportLegacyDatabase();
        Initialize();
    }

    /// <summary>Gibt gepoolte Verbindungen frei (Dateihandles, WAL-Checkpoint). Beim Beenden der App aufrufen.</summary>
    public static void ReleaseConnections() => SqliteConnection.ClearAllPools();

    // ────────────────────────────────────────────────────────────────────
    //  Schema, Migration
    // ────────────────────────────────────────────────────────────────────

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();

            // SQLites LIKE ignoriert Groß-/Kleinschreibung nur für ASCII ("Ärzte" ≠ "ärzte").
            // fold() setzt beide Seiten per .NET auf Kleinbuchstaben und macht die Suche damit Unicode-tauglich.
            connection.CreateFunction("fold", (string? s) => s?.ToLowerInvariant(), isDeterministic: true);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Übernimmt die Datenbank der Vorgängerversion (<c>library.db</c>, Schema 1), falls die neue Datei noch nicht existiert.
    /// <c>VACUUM INTO</c> erzeugt eine konsistente Kopie (auch mit noch nicht übernommenen WAL-Daten); das Original bleibt unberührt.
    /// </summary>
    private void ImportLegacyDatabase()
    {
        if (File.Exists(DatabasePath) && new FileInfo(DatabasePath).Length > 0)
            return;
        if (!string.Equals(Path.GetFileName(DatabasePath), FileName, StringComparison.OrdinalIgnoreCase))
            return;

        var legacy = Path.Combine(Path.GetDirectoryName(DatabasePath) ?? string.Empty, LegacyFileName);
        if (!File.Exists(legacy))
            return;

        try
        {
            var legacyConnection = new SqliteConnectionStringBuilder
            {
                DataSource = legacy,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString();

            using var connection = new SqliteConnection(legacyConnection);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "VACUUM INTO $target;";
            cmd.Parameters.AddWithValue("$target", DatabasePath);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            // Die alte Datei ist unbrauchbar: mit einer frischen Datenbank starten (die alte bleibt zur manuellen Rettung liegen).
            TryDelete(DatabasePath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Initialize()
    {
        try
        {
            using var connection = Open();

            Scalar(connection, null, "PRAGMA journal_mode = WAL;");

            var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
            if (version > SchemaVersion)
                throw new InvalidOperationException($"Die Datenbank hat eine unbekannte Version ({version}).");

            var hasLegacyTables = TableExists(connection, "Mangas") && TableExists(connection, "MangaTags");

            if (version == 0 && !hasLegacyTables)
            {
                using var tx = connection.BeginTransaction();
                ExecuteScript(connection, tx, SchemaSql);
                Execute(connection, tx, $"PRAGMA user_version = {SchemaVersion};");
                tx.Commit();
            }
            else if (version <= 1 && hasLegacyTables)
            {
                MigrateFromSchema1(connection);
            }
            else if (version < SchemaVersion)
            {
                throw new InvalidOperationException($"Die Datenbank hat eine unbekannte Version ({version}).");
            }

            EnsureOnlineSiteTags(connection);
            EnsureHttpCookies(connection);
            if (version < SchemaVersion)
                Execute(connection, null, "ANALYZE;");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Schema-Initialisierung von \"" + DatabasePath + "\" fehlgeschlagen: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Eigener Katalog für die Websuche – <b>ohne</b> Fremdschlüssel zur Bibliotheks-Tabelle <c>tags</c>.
    /// Additive Migration: bestehende Bibliotheken (Schema 2) bekommen die Tabelle nachträglich.
    /// </summary>
    private static void EnsureOnlineSiteTags(SqliteConnection connection)
    {
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS online_site_tags (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                source_site TEXT    NOT NULL,
                tag_name    TEXT    NOT NULL,
                category    TEXT    NOT NULL DEFAULT '',
                count       INTEGER NOT NULL DEFAULT 0,
                UNIQUE (source_site, category, tag_name)
            );
            """);
        EnsureColumn(connection, "online_site_tags", "source_site", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "online_site_tags", "tag_name", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "online_site_tags", "category", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "online_site_tags", "count", "INTEGER NOT NULL DEFAULT 0");
        Execute(connection, null, "CREATE INDEX IF NOT EXISTS ix_online_site_tags_site ON online_site_tags (source_site, count DESC);");
        Execute(connection, null, "CREATE INDEX IF NOT EXISTS ix_online_site_tags_name ON online_site_tags (source_site, tag_name);");
        SeedNhentaiOnlineTags(connection);
    }

    /// <summary>
    /// Startkatalog für nhentai (NClientV3-Kategorien), damit Autocomplete vor der ersten Suche funktioniert.
    /// Bestehende Einträge bleiben unangetastet.
    /// </summary>
    private static void SeedNhentaiOnlineTags(SqliteConnection connection)
    {
        try
        {
            var existing = Convert.ToInt32(Scalar(connection, null,
                "SELECT COUNT(*) FROM online_site_tags WHERE source_site = 'nhentai';") ?? 0);
            if (existing > 0)
                return;

            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO online_site_tags (source_site, tag_name, category, count)
                VALUES ('nhentai', $name, $category, $count);
                """;
            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pCategory = cmd.Parameters.Add("$category", SqliteType.Text);
            var pCount = cmd.Parameters.Add("$count", SqliteType.Integer);

            foreach (var (category, name, count) in NhentaiSeedTags)
            {
                pName.Value = name;
                pCategory.Value = category;
                pCount.Value = count;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        catch (SqliteException)
        {
            // Seed ist optional – die Tabelle existiert, der Start darf nicht daran scheitern.
        }
    }

    /// <summary>Sprache und Kategorie wie in NClientV3 <c>SpecialTagIds</c> / Tag-Sitemaps.</summary>
    private static readonly (string Category, string Name, int Count)[] NhentaiSeedTags =
    {
        ("language", "english", 50),
        ("language", "japanese", 50),
        ("language", "chinese", 40),
        ("language", "translated", 40),
        ("language", "rewrite", 10),
        ("category", "doujinshi", 45),
        ("category", "manga", 45),
        ("category", "artistcg", 20),
        ("category", "gamecg", 15),
        ("category", "western", 15),
        ("category", "non-h", 10),
        ("category", "imageset", 15),
        ("category", "cosplay", 12),
        ("category", "asianporn", 8),
        ("category", "misc", 8),
    };

    /// <summary>
    /// Session-Cookies pro Domain (Cloudflare <c>cf_clearance</c>, E-Hentai <c>ipb_member_id</c>, <c>xres</c> …).
    /// Additive Migration für bestehende Bibliotheken.
    /// </summary>
    private static void EnsureHttpCookies(SqliteConnection connection)
    {
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS http_cookies (
                id     INTEGER PRIMARY KEY AUTOINCREMENT,
                domain TEXT    NOT NULL,
                name   TEXT    NOT NULL,
                value  TEXT    NOT NULL,
                path   TEXT    NOT NULL DEFAULT '/',
                UNIQUE (domain, name, path)
            );
            """);
        Execute(connection, null, "CREATE INDEX IF NOT EXISTS ix_http_cookies_domain ON http_cookies (domain);");
    }

    /// <summary>
    /// Schema 1 (Tabellen Mangas/Tags/MangaTags, Tag-Namen mit eingebautem Präfix) → Schema 2.
    /// Alles läuft in einer Transaktion: Schlägt etwas fehl, bleibt die Datenbank unverändert.
    /// IDs, Favoriten, Einstellungen und Tag-Zuordnungen bleiben erhalten; „female:glasses“ wird zu Kategorie „female“ + Name „glasses“,
    /// die Quelle ergibt sich aus der gespeicherten URL.
    /// </summary>
    private static void MigrateFromSchema1(SqliteConnection connection)
    {
        // Fremdschlüssel dürfen beim Umbau nicht kaskadieren; der Schalter wirkt nur außerhalb einer Transaktion.
        Execute(connection, null, "PRAGMA foreign_keys = OFF;");
        try
        {
            using var tx = connection.BeginTransaction();

            // Alte Tabellen und Indizes beiseitestellen (Namen sind in SQLite unabhängig von der Groß-/Kleinschreibung).
            ExecuteScript(connection, tx, """
                DROP INDEX IF EXISTS IX_Mangas_Title;
                DROP INDEX IF EXISTS IX_Mangas_DateAdded;
                DROP INDEX IF EXISTS IX_Mangas_Favorite;
                DROP INDEX IF EXISTS IX_MangaTags_TagId;
                ALTER TABLE MangaTags RENAME TO manga_tags_v1;
                ALTER TABLE Mangas    RENAME TO mangas_v1;
                ALTER TABLE Tags      RENAME TO tags_v1;
                ALTER TABLE Settings  RENAME TO settings_v1;
                """);

            ExecuteScript(connection, tx, SchemaSql);

            Execute(connection, tx, """
                INSERT INTO mangas (id, title, filepath, source, author, description, source_url, cover_path,
                                    page_count, is_favorite, date_added, last_opened)
                SELECT Id, Title, FolderPath, 'other', Author, Description, SourceUrl, CoverPath,
                       PageCount, IsFavorite, DateAdded, LastOpened
                  FROM mangas_v1;
                INSERT INTO settings (name, value) SELECT Name, Value FROM settings_v1;
                """);

            // Quelle aus der gespeicherten URL ableiten.
            foreach (var (id, url) in ReadRows(connection, tx, "SELECT id, source_url FROM mangas WHERE source_url IS NOT NULL;",
                         r => (r.GetInt64(0), r.GetString(1))))
            {
                var source = SourceCatalog.FromUrl(url);
                if (source != SourceCatalog.Other)
                    Execute(connection, tx, "UPDATE mangas SET source = $source WHERE id = $id;", ("$source", source), ("$id", id));
            }

            // Tags aufteilen: alter Name → (Kategorie, Name); Zuordnung alte ID → neue ID.
            var tagMap = new Dictionary<long, long>();
            foreach (var (oldId, oldName) in ReadRows(connection, tx, "SELECT Id, Name FROM tags_v1;", r => (r.GetInt64(0), r.GetString(1))))
            {
                var (category, name) = TagNames.Split(oldName);
                if (name.Length == 0)
                    continue;

                tagMap[oldId] = GetOrCreateTagId(connection, tx, category, name);
            }

            foreach (var (mangaId, oldTagId) in ReadRows(connection, tx, "SELECT MangaId, TagId FROM manga_tags_v1;", r => (r.GetInt64(0), r.GetInt64(1))))
            {
                if (tagMap.TryGetValue(oldTagId, out var newTagId))
                {
                    Execute(connection, tx, "INSERT OR IGNORE INTO manga_tags (manga_id, tag_id) VALUES ($m, $t);",
                        ("$m", mangaId), ("$t", newTagId));
                }
            }

            ExecuteScript(connection, tx, """
                DROP TABLE manga_tags_v1;
                DROP TABLE mangas_v1;
                DROP TABLE tags_v1;
                DROP TABLE settings_v1;
                """);

            if (ReadRows(connection, tx, "PRAGMA foreign_key_check;", r => r.GetString(0)).Count > 0)
                throw new InvalidOperationException("Die Migration wurde abgebrochen: Fremdschlüssel-Prüfung fehlgeschlagen.");

            // Die Websites ließen sich bisher nur an gespeicherten URLs erkennen; die App ergänzt den Rest einmalig aus den Metadaten-Dateien.
            Execute(connection, tx, "INSERT OR REPLACE INTO settings (name, value) VALUES ('SourceBackfillPending', '1');");

            Execute(connection, tx, $"PRAGMA user_version = {SchemaVersion};");
            tx.Commit();
        }
        finally
        {
            Execute(connection, null, "PRAGMA foreign_keys = ON;");
        }
    }

    /// <summary>
    /// <c>true</c> genau einmal nach einer Migration: Einträge ohne erkannte Website sollen aus ihren Metadaten-Dateien nachbestimmt werden.
    /// Die Markierung wird dabei gelöscht.
    /// </summary>
    public bool TakeSourceBackfillFlag()
    {
        if (GetSetting("SourceBackfillPending") is null)
            return false;

        SetSetting("SourceBackfillPending", null);
        return true;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Mangas
    // ────────────────────────────────────────────────────────────────────

    /// <summary>Legt einen Eintrag samt Tags an und setzt <see cref="Manga.Id"/>.</summary>
    public long AddManga(Manga manga)
    {
        ArgumentNullException.ThrowIfNull(manga);

        using var connection = Open();
        using var tx = connection.BeginTransaction();

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO mangas (title, filepath, source, author, description, source_url, cover_path,
                                    page_count, is_favorite, date_added, last_opened)
                VALUES ($title, $filepath, $source, $author, $description, $sourceUrl, $cover,
                        $pages, $favorite, $added, $opened)
                RETURNING id;
                """;
            BindManga(cmd, manga);
            cmd.Parameters.AddWithValue("$added", FormatDate(manga.DateAdded));
            manga.Id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        AttachTags(connection, tx, manga.Id, manga.Tags);
        tx.Commit();
        return manga.Id;
    }

    /// <summary>
    /// Legt viele Einträge in <b>einer</b> Transaktion an (bei großen Importen etwa zehnmal schneller als einzelne <see cref="AddManga"/>-Aufrufe)
    /// und aktualisiert danach die Statistik für die Tag-Abfragen.
    /// </summary>
    public int AddMangas(IReadOnlyCollection<Manga> mangas)
    {
        ArgumentNullException.ThrowIfNull(mangas);
        if (mangas.Count == 0)
            return 0;

        using (var connection = Open())
        using (var tx = connection.BeginTransaction())
        {
            foreach (var manga in mangas)
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO mangas (title, filepath, source, author, description, source_url, cover_path,
                                        page_count, is_favorite, date_added, last_opened)
                    VALUES ($title, $filepath, $source, $author, $description, $sourceUrl, $cover,
                            $pages, $favorite, $added, $opened)
                    RETURNING id;
                    """;
                BindManga(cmd, manga);
                cmd.Parameters.AddWithValue("$added", FormatDate(manga.DateAdded));
                manga.Id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                AttachTags(connection, tx, manga.Id, manga.Tags);
            }

            tx.Commit();
        }

        Analyze();
        return mangas.Count;
    }

    /// <summary>Schreibt alle Felder (außer <c>date_added</c>) und ersetzt die Tags durch <see cref="Manga.Tags"/>.</summary>
    public bool UpdateManga(Manga manga)
    {
        ArgumentNullException.ThrowIfNull(manga);

        using var connection = Open();
        using var tx = connection.BeginTransaction();

        int changed;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE mangas
                   SET title = $title, filepath = $filepath, source = $source, author = $author, description = $description,
                       source_url = $sourceUrl, cover_path = $cover, page_count = $pages,
                       is_favorite = $favorite, last_opened = $opened
                 WHERE id = $id;
                """;
            BindManga(cmd, manga);
            cmd.Parameters.AddWithValue("$id", manga.Id);
            changed = cmd.ExecuteNonQuery();
        }

        if (changed > 0)
        {
            Execute(connection, tx, "DELETE FROM manga_tags WHERE manga_id = $id;", ("$id", manga.Id));
            AttachTags(connection, tx, manga.Id, manga.Tags);
            RemoveOrphanTags(connection, tx);
        }

        tx.Commit();
        return changed > 0;
    }

    /// <summary>Entfernt den Eintrag aus der Datenbank (Dateien auf der Festplatte bleiben unberührt).</summary>
    public bool DeleteManga(long id)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();

        var changed = Execute(connection, tx, "DELETE FROM mangas WHERE id = $id;", ("$id", id));
        RemoveOrphanTags(connection, tx);

        tx.Commit();
        return changed > 0;
    }

    public Manga? GetManga(long id) =>
        QueryOne($"SELECT {MangaColumns} FROM mangas m WHERE m.id = $id;", ("$id", id));

    /// <summary>Der älteste Eintrag zu dieser Adresse (mehrere Bände einer Serie können sich eine Adresse teilen).</summary>
    public Manga? GetMangaBySourceUrl(string sourceUrl) =>
        string.IsNullOrWhiteSpace(sourceUrl)
            ? null
            : QueryOne($"SELECT {MangaColumns} FROM mangas m WHERE m.source_url = $url ORDER BY m.id LIMIT 1;", ("$url", sourceUrl.Trim()));

    /// <summary>Der Eintrag zu einem Ordner bzw. Archiv (ohne Beachtung der Groß-/Kleinschreibung).</summary>
    public Manga? GetMangaByFolder(string folderPath) =>
        string.IsNullOrWhiteSpace(folderPath)
            ? null
            : QueryOne($"SELECT {MangaColumns} FROM mangas m WHERE m.filepath = $path COLLATE NOCASE LIMIT 1;",
                ("$path", folderPath.Trim().TrimEnd('\\', '/')));

    private Manga? QueryOne(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadManga(reader) : null;
    }

    /// <summary>
    /// Mehrfach-Tag-Suche: Gibt nur Mangas zurück, die <b>alle</b> <paramref name="selectedTags"/> tragen (UND) und zu
    /// <b>mindestens einer</b> der <paramref name="selectedSources"/> gehören (ODER). <c>null</c> bei den Quellen heißt „keine Einschränkung“,
    /// eine leere Liste „keine Quelle gewählt“ – also kein Ergebnis.
    /// </summary>
    public List<Manga> SearchMangas(List<string> selectedTags, List<string>? selectedSources)
    {
        var query = new MangaQuery { Sources = selectedSources };
        if (selectedTags is not null)
            query.IncludeTags.AddRange(selectedTags);
        return SearchMangas(query);
    }

    /// <summary>Durchsucht die Bibliothek (Freitext, Tags, Quellen, Favoriten) und sortiert das Ergebnis.</summary>
    public List<Manga> SearchMangas(MangaQuery? query = null)
    {
        query ??= new MangaQuery();

        if (query.Sources is { Count: 0 })
            return new List<Manga>(); // keine Quelle gewählt

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        var n = 0;

        // 1) Tags in IDs auflösen. Existiert einer der gewünschten Tags gar nicht, kann kein Werk alle tragen.
        var includeTags = query.IncludeTags.Select(TagNames.Normalize).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var includeIds = includeTags.Count == 0 ? new List<long>() : ResolveTagIds(connection, includeTags);
        if (includeIds.Count != includeTags.Count)
            return new List<Manga>();

        var excludeTags = query.ExcludeTags.Select(TagNames.Normalize).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var excludeIds = excludeTags.Count == 0 ? new List<long>() : ResolveTagIds(connection, excludeTags);

        // 2) Abfrage: JOIN über manga_tags; GROUP BY/HAVING lässt nur Werke übrig, die in allen gewählten Tags vorkommen.
        var sql = new StringBuilder("SELECT ").Append(MangaColumns).Append(" FROM mangas m");
        if (includeIds.Count > 0)
            sql.Append($" JOIN manga_tags jt ON jt.manga_id = m.id AND jt.tag_id IN ({string.Join(",", includeIds)})");
        sql.Append(" WHERE 1 = 1");

        if (query.FavoritesOnly)
            sql.Append(" AND m.is_favorite = 1");

        if (query.Sources is { } sources)
        {
            var parameters = new List<string>();
            foreach (var source in sources.Select(SourceCatalog.Normalize).Distinct())
            {
                var p = $"$src{n++}";
                parameters.Add(p);
                cmd.Parameters.AddWithValue(p, source);
            }

            sql.Append($" AND m.source IN ({string.Join(",", parameters)})");
        }

        foreach (var term in query.Terms.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            // Titel/Autor werden per fold() angeglichen; Tags sind beim Speichern bereits klein geschrieben.
            var p = $"$term{n++}";
            sql.Append(
                $" AND (fold(m.title) LIKE {p} ESCAPE '\\' OR fold(m.author) LIKE {p} ESCAPE '\\' OR EXISTS (" +
                "SELECT 1 FROM manga_tags x JOIN tags t ON t.id = x.tag_id " +
                $"WHERE x.manga_id = m.id AND (t.name LIKE {p} ESCAPE '\\' OR t.category LIKE {p} ESCAPE '\\')))");
            cmd.Parameters.AddWithValue(p, "%" + EscapeLike(term.Trim().ToLowerInvariant()) + "%");
        }

        if (excludeIds.Count > 0)
        {
            sql.Append(" AND NOT EXISTS (SELECT 1 FROM manga_tags xt WHERE xt.manga_id = m.id AND xt.tag_id IN (")
               .Append(string.Join(",", excludeIds)).Append("))");
        }

        if (includeIds.Count > 0)
            sql.Append($" GROUP BY m.id HAVING COUNT(*) = {includeIds.Count}");

        sql.Append(query.Sort switch
        {
            MangaSort.TitleAsc => " ORDER BY m.title COLLATE NOCASE ASC, m.id ASC",
            MangaSort.TitleDesc => " ORDER BY m.title COLLATE NOCASE DESC, m.id DESC",
            MangaSort.PagesDesc => " ORDER BY m.page_count DESC, m.title COLLATE NOCASE ASC",
            MangaSort.LastOpenedDesc => " ORDER BY m.last_opened IS NULL, m.last_opened DESC, m.title COLLATE NOCASE ASC",
            _ => " ORDER BY m.date_added DESC, m.id DESC",
        });

        cmd.CommandText = sql.ToString();

        var result = new List<Manga>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(ReadManga(reader));
        return result;
    }

    /// <summary>IDs der Tags (Textform) – über den eindeutigen Index (category, name). Unbekannte Tags fehlen im Ergebnis.</summary>
    private static List<long> ResolveTagIds(SqliteConnection connection, IReadOnlyList<string> tags)
    {
        using var cmd = connection.CreateCommand();
        var sql = new StringBuilder("SELECT id FROM tags WHERE ");

        for (var i = 0; i < tags.Count; i++)
        {
            var (category, name) = TagNames.Split(tags[i]);
            if (i > 0)
                sql.Append(" OR ");
            sql.Append($"(category = $c{i} AND name = $n{i})");
            cmd.Parameters.AddWithValue($"$c{i}", category);
            cmd.Parameters.AddWithValue($"$n{i}", name);
        }

        cmd.CommandText = sql.ToString();

        var ids = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    public int CountMangas(bool favoritesOnly = false)
    {
        using var connection = Open();
        var sql = favoritesOnly ? "SELECT COUNT(*) FROM mangas WHERE is_favorite = 1;" : "SELECT COUNT(*) FROM mangas;";
        return Convert.ToInt32(Scalar(connection, null, sql), CultureInfo.InvariantCulture);
    }

    /// <summary>Anzahl der Mangas je Quelle (Kennung → Anzahl); Quellen ohne Werke fehlen.</summary>
    public Dictionary<string, int> GetSourceCounts()
    {
        using var connection = Open();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (source, count) in ReadRows(connection, null, "SELECT source, COUNT(*) FROM mangas GROUP BY source;",
                     r => (r.GetString(0), r.GetInt32(1))))
            result[source] = count;
        return result;
    }

    /// <summary>Alle bereits eingetragenen Orte (ohne Groß-/Kleinschreibung vergleichbar) – für den Ordner-Scan.</summary>
    public HashSet<string> GetFolderPaths()
    {
        using var connection = Open();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ReadRows(connection, null, "SELECT filepath FROM mangas WHERE filepath IS NOT NULL;", r => r.GetString(0)))
            result.Add(path.TrimEnd('\\', '/'));
        return result;
    }

    /// <summary>Alle Einträge mit unbekannter Quelle (Kennung, Ort, URL) – zum nachträglichen Erkennen der Website.</summary>
    public List<(long Id, string? Path, string? Url)> GetEntriesWithoutSource()
    {
        using var connection = Open();
        return ReadRows(connection, null, "SELECT id, filepath, source_url FROM mangas WHERE source = 'other';",
            r => (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
    }

    public bool UpdateSource(long id, string source) =>
        ExecuteOnce("UPDATE mangas SET source = $source WHERE id = $id;", ("$source", SourceCatalog.Normalize(source)), ("$id", id)) > 0;

    public bool SetFavorite(long id, bool isFavorite) =>
        ExecuteOnce("UPDATE mangas SET is_favorite = $fav WHERE id = $id;", ("$fav", isFavorite ? 1 : 0), ("$id", id)) > 0;

    public bool TouchLastOpened(long id) =>
        ExecuteOnce("UPDATE mangas SET last_opened = $now WHERE id = $id;", ("$now", FormatDate(DateTime.UtcNow)), ("$id", id)) > 0;

    /// <summary>Aktualisiert die Statistik des Abfrageplaners – nach großen Importen aufrufen (hält Multi-Tag-Abfragen schnell).</summary>
    public void Analyze()
    {
        using var connection = Open();
        Execute(connection, null, "ANALYZE;");
    }

    // ────────────────────────────────────────────────────────────────────
    //  Tags
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Alle verwendeten Tags, häufigste zuerst. <paramref name="limit"/> = <c>null</c> liefert alle;
    /// <paramref name="contains"/> beschränkt auf Tags, deren Text den Suchtext enthält (ohne Groß-/Kleinschreibung).
    /// <paramref name="source"/> beschränkt auf Tags, die bei Werken dieser Website vorkommen (<c>mangas.source</c>).
    /// </summary>
    public List<TagInfo> GetTags(int? limit = null, string? contains = null, string? source = null)
    {
        var filter = TagNames.CleanName(contains);
        var site = string.IsNullOrWhiteSpace(source) ? null : SourceCatalog.Normalize(source);

        using var connection = Open();
        using var cmd = connection.CreateCommand();

        var where = new List<string>();
        if (filter.Length > 0)
            where.Add("(t.name LIKE $contains ESCAPE '\\' OR t.category LIKE $contains ESCAPE '\\')");
        if (site is not null)
            where.Add("m.source = $source");

        var from = site is null
            ? "FROM tags t JOIN manga_tags mt ON mt.tag_id = t.id"
            : "FROM tags t JOIN manga_tags mt ON mt.tag_id = t.id JOIN mangas m ON m.id = mt.manga_id";

        cmd.CommandText = $"""
            SELECT t.id, t.name, t.category, COUNT(mt.manga_id) AS cnt
              {from}
             {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty)}
             GROUP BY t.id
             ORDER BY cnt DESC, t.category, t.name
             LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit ?? -1); // negatives LIMIT = unbegrenzt
        if (filter.Length > 0)
            cmd.Parameters.AddWithValue("$contains", "%" + EscapeLike(filter) + "%");
        if (site is not null)
            cmd.Parameters.AddWithValue("$source", site);

        var result = new List<TagInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var category = reader.GetString(2);
            result.Add(new TagInfo(reader.GetInt64(0), TagNames.Format(category, reader.GetString(1)), reader.GetInt32(3), category));
        }

        return result;
    }

    /// <summary>
    /// Tags der Websuche für <b>eine</b> Website aus <c>online_site_tags</c> – unabhängig von der Bibliotheks-Tabelle <c>tags</c>.
    /// <paramref name="filter"/> schränkt auf Namen/Kategorie ein (ohne Groß-/Kleinschreibung); leer = häufigste zuerst.
    /// <paramref name="limit"/> = <c>null</c> liefert alle Einträge (SQLite <c>LIMIT -1</c>).
    /// </summary>
    public List<TagInfo> GetTagsBySite(string siteName, string? filter = null, int? limit = null)
    {
        var site = SourceCatalog.Normalize(siteName);
        if (site == SourceCatalog.Other)
            return new List<TagInfo>();

        var needle = TagNames.CleanName(filter);
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = needle.Length == 0
            ? """
                SELECT id, tag_name, category, count
                  FROM online_site_tags
                 WHERE source_site = $site
                 ORDER BY count DESC, category, tag_name
                 LIMIT $limit;
                """
            : """
                SELECT id, tag_name, category, count
                  FROM online_site_tags
                 WHERE source_site = $site
                   AND (tag_name LIKE $filter ESCAPE '\' OR category LIKE $filter ESCAPE '\'
                        OR (category || ':' || tag_name) LIKE $filter ESCAPE '\')
                 ORDER BY CASE WHEN tag_name LIKE $prefix ESCAPE '\' THEN 0 ELSE 1 END,
                          count DESC, tag_name
                 LIMIT $limit;
                """;
        cmd.Parameters.AddWithValue("$site", site);
        cmd.Parameters.AddWithValue("$limit", limit ?? -1);
        if (needle.Length > 0)
        {
            var escaped = EscapeLike(needle);
            cmd.Parameters.AddWithValue("$filter", "%" + escaped + "%");
            cmd.Parameters.AddWithValue("$prefix", escaped + "%");
        }

        var result = new List<TagInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var category = reader.GetString(2);
            result.Add(new TagInfo(reader.GetInt64(0), TagNames.Format(category, reader.GetString(1)), reader.GetInt32(3), category));
        }

        return result;
    }

    public int CountTagsBySite(string siteName)
    {
        var site = SourceCatalog.Normalize(siteName);
        if (site == SourceCatalog.Other)
            return 0;

        using var connection = Open();
        return Convert.ToInt32(Scalar(connection, null,
            "SELECT COUNT(*) FROM online_site_tags WHERE source_site = $site;",
            ("$site", site)) ?? 0);
    }

    /// <summary>Übernimmt einen heruntergeladenen Tag-Index in <c>online_site_tags</c> (höhere Counts gewinnen).</summary>
    public int DownloadAndSeedSiteTags(string siteName, IEnumerable<(string Tag, int Count)> tags)
    {
        UpsertOnlineTags(siteName, tags, OnlineTagMerge.Max);
        return CountTagsBySite(siteName);
    }

    /// <summary>
    /// Schreibt Online-Tags einer Website. <see cref="OnlineTagMerge.Max"/> übernimmt die höhere API-Häufigkeit
    /// (nhentai <c>Tag.count</c>); <see cref="OnlineTagMerge.Add"/> zählt Sichtungen.
    /// <paramref name="siteName"/> darf der Anzeigename sein („E-Hentai“, „Hitomi“);
    /// gespeichert wird die Katalog-Kennung (<c>ehentai</c>, <c>hitomi</c>, <c>nhentai</c>) in <c>source_site</c>.
    /// Kategorie und Name kommen aus „artist:name“, „female:name“ usw.
    /// </summary>
    public void UpsertOnlineTags(string siteName, IEnumerable<(string Tag, int Count)> tags, OnlineTagMerge merge = OnlineTagMerge.Add)
    {
        var site = SourceCatalog.Normalize(siteName);
        if (site == SourceCatalog.Other)
            return;

        using var connection = Open();
        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = merge == OnlineTagMerge.Max
            ? """
                INSERT INTO online_site_tags (source_site, tag_name, category, count)
                VALUES ($site, $name, $category, $count)
                ON CONFLICT (source_site, category, tag_name) DO UPDATE SET
                    count = MAX(online_site_tags.count, excluded.count);
                """
            : """
                INSERT INTO online_site_tags (source_site, tag_name, category, count)
                VALUES ($site, $name, $category, $count)
                ON CONFLICT (source_site, category, tag_name) DO UPDATE SET
                    count = online_site_tags.count + excluded.count;
                """;
        var pSite = cmd.Parameters.Add("$site", SqliteType.Text);
        var pName = cmd.Parameters.Add("$name", SqliteType.Text);
        var pCategory = cmd.Parameters.Add("$category", SqliteType.Text);
        var pCount = cmd.Parameters.Add("$count", SqliteType.Integer);
        pSite.Value = site;

        var any = false;
        foreach (var (tag, count) in tags)
        {
            var (category, name) = TagNames.Split(tag);
            if (name.Length == 0 || count <= 0)
                continue;

            pName.Value = name;
            pCategory.Value = category;
            pCount.Value = count;
            cmd.ExecuteNonQuery();
            any = true;
        }

        if (any)
            tx.Commit();
    }

    /// <summary>
    /// Vorschläge zum Tippen: Tags, deren Name den Text enthält (Treffer am Anfang zuerst, dann nach Häufigkeit).
    /// „artist:ab“ beschränkt auf die Kategorie; bereits gewählte Tags (<paramref name="exclude"/>) werden nicht vorgeschlagen.
    /// <paramref name="source"/> beschränkt auf Tags, die bei Werken dieser Website vorkommen.
    /// </summary>
    public List<TagInfo> SuggestTags(string? text, int limit = 8, IEnumerable<string>? exclude = null, string? source = null)
    {
        var raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0)
            return new List<TagInfo>();

        string? category = null;
        var part = raw;
        var colon = raw.IndexOf(':');
        if (colon > 0 && TagNames.CanonicalCategory(raw[..colon]) is { Length: > 0 } known)
        {
            category = known;
            part = raw[(colon + 1)..];
        }

        part = TagNames.CleanName(part);
        var skip = new HashSet<string>((exclude ?? Enumerable.Empty<string>()).Select(TagNames.Normalize), StringComparer.Ordinal);

        using var connection = Open();
        using var cmd = connection.CreateCommand();
        var site = string.IsNullOrWhiteSpace(source) ? null : SourceCatalog.Normalize(source);
        var siteFilter = site is null
            ? string.Empty
            : "AND EXISTS (SELECT 1 FROM manga_tags mt JOIN mangas m ON m.id = mt.manga_id WHERE mt.tag_id = t.id AND m.source = $source)";

        cmd.CommandText = $"""
            SELECT c.id, c.name, c.category, (SELECT COUNT(*) FROM manga_tags x WHERE x.tag_id = c.id) AS cnt
              FROM (SELECT t.id, t.name, t.category,
                           CASE WHEN t.name LIKE $prefix ESCAPE '\' THEN 0
                                WHEN t.name LIKE $word   ESCAPE '\' THEN 1 ELSE 2 END AS rnk
                      FROM tags t
                     WHERE t.name LIKE $contains ESCAPE '\' {(category is null ? string.Empty : "AND t.category = $category")}
                       {siteFilter}
                     ORDER BY rnk, length(t.name), t.name
                     LIMIT $pool) c
             ORDER BY c.rnk, cnt DESC, c.name
             LIMIT $limit;
            """;
        var escaped = EscapeLike(part);
        cmd.Parameters.AddWithValue("$prefix", escaped + "%");
        cmd.Parameters.AddWithValue("$word", "% " + escaped + "%");
        cmd.Parameters.AddWithValue("$contains", "%" + escaped + "%");
        cmd.Parameters.AddWithValue("$pool", 60);
        cmd.Parameters.AddWithValue("$limit", limit + skip.Count);
        if (category is not null)
            cmd.Parameters.AddWithValue("$category", category);
        if (site is not null)
            cmd.Parameters.AddWithValue("$source", site);

        var result = new List<TagInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read() && result.Count < limit)
        {
            var cat = reader.GetString(2);
            var full = TagNames.Format(cat, reader.GetString(1));
            if (!skip.Contains(full))
                result.Add(new TagInfo(reader.GetInt64(0), full, reader.GetInt32(3), cat));
        }

        return result;
    }

    /// <summary>Alle vorkommenden Tag-Kategorien („artist“, „language“ …).</summary>
    public List<string> GetCategories()
    {
        using var connection = Open();
        return ReadRows(connection, null, "SELECT DISTINCT category FROM tags WHERE category <> '' ORDER BY category;", r => r.GetString(0));
    }

    public int CountTags()
    {
        using var connection = Open();
        return Convert.ToInt32(Scalar(connection, null, "SELECT COUNT(*) FROM tags;"), CultureInfo.InvariantCulture);
    }

    public List<string> GetTagsForManga(long mangaId)
    {
        using var connection = Open();
        var tags = ReadRows(connection, null, $"""
            SELECT {TagText} FROM manga_tags mt JOIN tags t ON t.id = mt.tag_id WHERE mt.manga_id = {mangaId};
            """, r => r.GetString(0));
        tags.Sort(StringComparer.CurrentCultureIgnoreCase);
        return tags;
    }

    /// <summary>Ersetzt die Tags eines Eintrags.</summary>
    public void SetTags(long mangaId, IEnumerable<string> tags)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();

        Execute(connection, tx, "DELETE FROM manga_tags WHERE manga_id = $id;", ("$id", mangaId));
        AttachTags(connection, tx, mangaId, tags);
        RemoveOrphanTags(connection, tx);

        tx.Commit();
    }

    /// <summary>Fügt Tags hinzu; bereits vorhandene bleiben erhalten.</summary>
    public void AddTags(long mangaId, IEnumerable<string> tags)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();

        AttachTags(connection, tx, mangaId, tags);

        tx.Commit();
    }

    /// <summary>Einheitliche Textform eines Tags („Artist:Foo_Bar“ → „artist:foo bar“); leer = ungültig. Siehe <see cref="TagNames"/>.</summary>
    public static string NormalizeTag(string? tag) => TagNames.Normalize(tag);

    // ────────────────────────────────────────────────────────────────────
    //  Einstellungen (Schlüssel/Wert)
    // ────────────────────────────────────────────────────────────────────

    public string? GetSetting(string name)
    {
        using var connection = Open();
        return Scalar(connection, null, "SELECT value FROM settings WHERE name = $name;", ("$name", name)) is string value ? value : null;
    }

    /// <summary>Speichert einen Wert; <c>null</c> oder leer löscht die Einstellung.</summary>
    public void SetSetting(string name, string? value)
    {
        using var connection = Open();

        if (string.IsNullOrEmpty(value))
        {
            Execute(connection, null, "DELETE FROM settings WHERE name = $name;", ("$name", name));
            return;
        }

        Execute(connection, null, """
            INSERT INTO settings (name, value) VALUES ($name, $value)
            ON CONFLICT (name) DO UPDATE SET value = excluded.value;
            """, ("$name", name), ("$value", value));
    }

    // ────────────────────────────────────────────────────────────────────
    //  Session-Cookies (Cloudflare / Login)
    // ────────────────────────────────────────────────────────────────────

    public IReadOnlyList<StoredHttpCookie> GetHttpCookies(string? domain = null)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        var host = HttpCookieImport.NormalizeDomain(domain);
        if (host.Length == 0)
        {
            cmd.CommandText = "SELECT domain, name, value, path FROM http_cookies ORDER BY domain, name;";
        }
        else
        {
            cmd.CommandText = """
                SELECT domain, name, value, path FROM http_cookies
                 WHERE domain = $domain OR domain = $dot
                 ORDER BY name;
                """;
            cmd.Parameters.AddWithValue("$domain", host);
            cmd.Parameters.AddWithValue("$dot", "." + host);
        }

        var list = new List<StoredHttpCookie>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new StoredHttpCookie(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? "/" : reader.GetString(3)));
        }

        return list;
    }

    public int CountHttpCookies(string? domain = null) => GetHttpCookies(domain).Count;

    /// <summary>Ersetzt alle Cookies einer Domain (Browser-Import).</summary>
    public void ReplaceHttpCookiesForDomain(string domain, IReadOnlyList<StoredHttpCookie> cookies)
    {
        var host = HttpCookieImport.NormalizeDomain(domain);
        if (host.Length == 0)
            throw new ArgumentException("Domain fehlt.");

        using var connection = Open();
        using var tx = connection.BeginTransaction();
        Execute(connection, tx, "DELETE FROM http_cookies WHERE domain = $domain OR domain = $dot;",
            ("$domain", host), ("$dot", "." + host));
        foreach (var cookie in cookies)
        {
            var name = cookie.Name.Trim();
            if (name.Length == 0)
                continue;
            Execute(connection, tx, """
                INSERT INTO http_cookies (domain, name, value, path)
                VALUES ($domain, $name, $value, $path)
                ON CONFLICT (domain, name, path) DO UPDATE SET value = excluded.value;
                """,
                ("$domain", host),
                ("$name", name),
                ("$value", cookie.Value ?? string.Empty),
                ("$path", string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path.Trim()));
        }

        tx.Commit();
    }

    /// <summary>Fügt Cookies hinzu oder aktualisiert sie (z. B. nach FlareSolverr), ohne andere Namen zu löschen.</summary>
    public void UpsertHttpCookies(IReadOnlyList<StoredHttpCookie> cookies)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        foreach (var cookie in cookies)
        {
            var host = HttpCookieImport.NormalizeDomain(cookie.Domain);
            var name = cookie.Name.Trim();
            if (host.Length == 0 || name.Length == 0)
                continue;
            Execute(connection, tx, """
                INSERT INTO http_cookies (domain, name, value, path)
                VALUES ($domain, $name, $value, $path)
                ON CONFLICT (domain, name, path) DO UPDATE SET value = excluded.value;
                """,
                ("$domain", host),
                ("$name", name),
                ("$value", cookie.Value ?? string.Empty),
                ("$path", string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path.Trim()));
        }

        tx.Commit();
    }

    public void ClearHttpCookies(string? domain = null)
    {
        using var connection = Open();
        var host = HttpCookieImport.NormalizeDomain(domain);
        if (host.Length == 0)
            Execute(connection, null, "DELETE FROM http_cookies;");
        else
            Execute(connection, null, "DELETE FROM http_cookies WHERE domain = $domain OR domain = $dot;",
                ("$domain", host), ("$dot", "." + host));
    }

    // ────────────────────────────────────────────────────────────────────
    //  Hilfsmethoden
    // ────────────────────────────────────────────────────────────────────

    private static IEnumerable<string> NormalizeTags(IEnumerable<string>? tags) =>
        (tags ?? Enumerable.Empty<string>())
            .Select(TagNames.Normalize)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal);

    private static long GetOrCreateTagId(SqliteConnection connection, SqliteTransaction tx, string category, string name)
    {
        Execute(connection, tx, "INSERT OR IGNORE INTO tags (name, category) VALUES ($name, $category);",
            ("$name", name), ("$category", category));
        return Convert.ToInt64(
            Scalar(connection, tx, "SELECT id FROM tags WHERE category = $category AND name = $name;", ("$category", category), ("$name", name)),
            CultureInfo.InvariantCulture);
    }

    private static void AttachTags(SqliteConnection connection, SqliteTransaction tx, long mangaId, IEnumerable<string>? tags)
    {
        // Ein vorbereiteter Befehl für alle Tags: spart bei großen Importen viel Zeit.
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO tags (name, category) VALUES ($name, $category);
            INSERT OR IGNORE INTO manga_tags (manga_id, tag_id)
            SELECT $mangaId, id FROM tags WHERE category = $category AND name = $name;
            """;
        var pName = cmd.Parameters.Add("$name", SqliteType.Text);
        var pCategory = cmd.Parameters.Add("$category", SqliteType.Text);
        cmd.Parameters.AddWithValue("$mangaId", mangaId);

        foreach (var tag in NormalizeTags(tags))
        {
            var (category, name) = TagNames.Split(tag);
            pName.Value = name;
            pCategory.Value = category;
            cmd.ExecuteNonQuery();
        }
    }

    private static void RemoveOrphanTags(SqliteConnection connection, SqliteTransaction tx) =>
        Execute(connection, tx, "DELETE FROM tags WHERE NOT EXISTS (SELECT 1 FROM manga_tags mt WHERE mt.tag_id = tags.id);");

    private static bool TableExists(SqliteConnection connection, string name) =>
        Scalar(connection, null, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name COLLATE NOCASE;", ("$name", name)) is not null;

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(" + table + ");";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string typeSql)
    {
        if (ColumnExists(connection, table, column))
            return;
        Execute(connection, null, "ALTER TABLE " + table + " ADD COLUMN " + column + " " + typeSql + ";");
    }

    private int ExecuteOnce(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open();
        return Execute(connection, null, sql, parameters);
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Mehrere durch Semikolon getrennte Anweisungen ausführen.</summary>
    private static void ExecuteScript(SqliteConnection connection, SqliteTransaction tx, string script) =>
        Execute(connection, tx, script);

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteScalar();
    }

    private static List<T> ReadRows<T>(SqliteConnection connection, SqliteTransaction? tx, string sql, Func<SqliteDataReader, T> map)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;

        var rows = new List<T>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(map(reader));
        return rows;
    }

    private static void BindManga(SqliteCommand cmd, Manga m)
    {
        var title = string.IsNullOrWhiteSpace(m.Title) ? "Unbenannt" : m.Title.Trim();

        // Ist keine Quelle gesetzt, ergibt sie sich aus der Adresse.
        var source = SourceCatalog.Normalize(m.Source);
        if (source == SourceCatalog.Other)
            source = SourceCatalog.FromUrl(m.SourceUrl);

        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$filepath", OrNull(m.FolderPath is null ? null : m.FolderPath.Trim().TrimEnd('\\', '/')));
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$author", OrNull(m.Author));
        cmd.Parameters.AddWithValue("$description", OrNull(m.Description));
        cmd.Parameters.AddWithValue("$sourceUrl", OrNull(m.SourceUrl));
        cmd.Parameters.AddWithValue("$cover", OrNull(m.CoverPath));
        cmd.Parameters.AddWithValue("$pages", Math.Max(0, m.PageCount));
        cmd.Parameters.AddWithValue("$favorite", m.IsFavorite ? 1 : 0);
        cmd.Parameters.AddWithValue("$opened", m.LastOpened is { } opened ? FormatDate(opened) : DBNull.Value);
    }

    private static object OrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string FormatDate(DateTime value) =>
        value.ToUniversalTime().ToString(DateFormat, CultureInfo.InvariantCulture);

    private static DateTime ParseDate(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static Manga ReadManga(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Title = r.GetString(1),
        Author = r.IsDBNull(2) ? null : r.GetString(2),
        Description = r.IsDBNull(3) ? null : r.GetString(3),
        SourceUrl = r.IsDBNull(4) ? null : r.GetString(4),
        FolderPath = r.IsDBNull(5) ? null : r.GetString(5),
        CoverPath = r.IsDBNull(6) ? null : r.GetString(6),
        PageCount = r.GetInt32(7),
        IsFavorite = r.GetInt64(8) != 0,
        DateAdded = ParseDate(r.GetString(9)),
        LastOpened = r.IsDBNull(10) ? null : ParseDate(r.GetString(10)),
        Source = r.GetString(11),
        Tags = r.IsDBNull(12)
            ? new List<string>()
            : r.GetString(12)
                .Split(TagSeparator, StringSplitOptions.RemoveEmptyEntries)
                .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
    };
}
