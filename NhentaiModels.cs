using System.Text.Json;

namespace MangaLibraryApp;

/// <summary>
/// nhentai-Tag-Typen wie in NClientV3 (<c>TagType</c>): parody, character, tag, artist, group, language, category.
/// </summary>
internal static class NhentaiTagTypes
{
    public const string Parody = "parody";
    public const string Character = "character";
    public const string Tag = "tag";
    public const string Artist = "artist";
    public const string Group = "group";
    public const string Language = "language";
    public const string Category = "category";

    public static readonly string[] All =
    {
        Parody, Character, Tag, Artist, Group, Language, Category,
    };

    public static string Canonical(string? type)
    {
        var key = (type ?? string.Empty).Trim().ToLowerInvariant();
        return key switch
        {
            "parody" or "parodies" => Parody,
            "character" or "characters" => Character,
            "artist" or "artists" => Artist,
            "group" or "groups" or "circle" or "circles" => Group,
            "language" or "languages" or "lang" => Language,
            "category" or "categories" => Category,
            "tag" or "tags" or "" => Tag,
            _ => TagNames.CanonicalCategory(key) is { Length: > 0 } known ? known : Tag,
        };
    }

    /// <summary>Leere Kategorie für den Typ <c>tag</c> (wie in der Bibliothek), sonst der NClientV3-Einzelname.</summary>
    public static string DatabaseCategory(string type)
    {
        var canonical = Canonical(type);
        return canonical == Tag ? string.Empty : canonical;
    }
}

/// <summary>Titel-Objekt der nhentai-API (v1) bzw. <c>english_title</c>/<c>japanese_title</c> (v2).</summary>
internal sealed record NhentaiTitle(string? English, string? Japanese, string? Pretty)
{
    public string Display => First(Pretty, English, Japanese) ?? string.Empty;

    private static string? First(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }
}

/// <summary>Bildmetadaten: <c>t</c>/<c>w</c>/<c>h</c> wie in NHApp und NClientV3 <c>Image</c>.</summary>
internal sealed record NhentaiImage(string TypeCode, int Width, int Height)
{
    public string Extension => TypeCode switch
    {
        "p" => "png",
        "g" => "gif",
        "w" => "webp",
        _ => "jpg",
    };
}

/// <summary>Ein Tag der Galerie inkl. API-Häufigkeit – wird in <c>online_site_tags</c> gespiegelt.</summary>
internal sealed record NhentaiTag(int Id, string Type, string Name, int Count)
{
    public string Category => NhentaiTagTypes.DatabaseCategory(Type);

    public string Formatted => TagNames.Format(Category, TagNames.CleanName(Name));
}

/// <summary>
/// Galerie nach Vorbild von NClientV3 <c>Gallery</c>/<c>SimpleGallery</c> und NHApp:
/// id, media_id, title, images, tags, num_pages.
/// Versteht API v1 (<c>/api/galleries/search</c>) und v2 (<c>/api/v2/search</c>).
/// </summary>
internal sealed class NhentaiGallery
{
    public int Id { get; init; }
    public string MediaId { get; init; } = string.Empty;
    public NhentaiTitle Title { get; init; } = new(null, null, null);
    public NhentaiImage? Cover { get; init; }
    public IReadOnlyList<NhentaiImage> Pages { get; init; } = Array.Empty<NhentaiImage>();
    public IReadOnlyList<NhentaiTag> Tags { get; init; } = Array.Empty<NhentaiTag>();
    public int NumPages { get; init; }
    public string? ThumbnailPath { get; init; }

    public string GalleryUrl => "https://nhentai.net/g/" + Id + "/";

    public string CoverUrl
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ThumbnailPath))
            {
                var path = ThumbnailPath.Trim();
                if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return path;
                if (path.StartsWith("//", StringComparison.Ordinal))
                    return "https:" + path;
                return "https://t1.nhentai.net/" + path.TrimStart('/');
            }

            var media = MediaId.Length > 0 ? MediaId : Id.ToString();
            var ext = Cover?.Extension ?? "jpg";
            return $"https://t.nhentai.net/galleries/{media}/cover.{ext}";
        }
    }

    public static NhentaiGallery? TryParse(JsonElement item)
    {
        var idText = ReadNumber(item, "id");
        if (idText is null || !int.TryParse(idText, out var id) || id <= 0)
            return null;

        var media = ReadNumber(item, "media_id") ?? id.ToString();
        var title = ReadTitle(item);
        var cover = ReadImage(item, "images", "cover");
        var pages = ReadPages(item);
        var tags = ReadTags(item);
        var numPages = item.TryGetProperty("num_pages", out var np) && np.TryGetInt32(out var n)
            ? n
            : pages.Count;
        var thumb = ReadString(item, "thumbnail");

        return new NhentaiGallery
        {
            Id = id,
            MediaId = media,
            Title = title,
            Cover = cover,
            Pages = pages,
            Tags = tags,
            NumPages = numPages,
            ThumbnailPath = string.IsNullOrWhiteSpace(thumb) ? null : thumb,
        };
    }

    public OnlineSearchResult ToResult()
    {
        var display = Title.Display;
        if (display.Length == 0)
            display = "#" + Id;

        var names = new List<string>();
        var harvest = new List<(string Tag, int Count)>();
        foreach (var tag in Tags)
        {
            var formatted = tag.Formatted;
            if (formatted.Length == 0)
                continue;
            names.Add(formatted);
            harvest.Add((formatted, tag.Count > 0 ? tag.Count : 1));
        }

        return new OnlineSearchResult(
            display,
            CoverUrl,
            GalleryUrl,
            names,
            "nhentai",
            Id.ToString(),
            NumPages > 0 ? NumPages : null,
            harvest);
    }

    private static NhentaiTitle ReadTitle(JsonElement item)
    {
        if (item.TryGetProperty("title", out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            return new NhentaiTitle(
                ReadString(obj, "english"),
                ReadString(obj, "japanese"),
                ReadString(obj, "pretty"));
        }

        return new NhentaiTitle(
            ReadString(item, "english_title"),
            ReadString(item, "japanese_title"),
            ReadString(item, "pretty_title") ?? ReadString(item, "title"));
    }

    private static NhentaiImage? ReadImage(JsonElement root, string parent, string child)
    {
        if (!root.TryGetProperty(parent, out var images) || images.ValueKind != JsonValueKind.Object)
            return null;
        if (!images.TryGetProperty(child, out var img) || img.ValueKind != JsonValueKind.Object)
            return null;
        return ReadImage(img);
    }

    private static NhentaiImage ReadImage(JsonElement img)
    {
        var type = img.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? "j"
            : "j";
        var w = img.TryGetProperty("w", out var wEl) && wEl.TryGetInt32(out var width) ? width : 0;
        var h = img.TryGetProperty("h", out var hEl) && hEl.TryGetInt32(out var height) ? height : 0;
        return new NhentaiImage(type, w, h);
    }

    private static List<NhentaiImage> ReadPages(JsonElement item)
    {
        var list = new List<NhentaiImage>();
        if (!item.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Object)
            return list;
        if (!images.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var page in pages.EnumerateArray())
        {
            if (page.ValueKind == JsonValueKind.Object)
                list.Add(ReadImage(page));
        }

        return list;
    }

    private static List<NhentaiTag> ReadTags(JsonElement item)
    {
        var list = new List<NhentaiTag>();
        if (!item.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var t in tags.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object)
                continue;
            var name = ReadString(t, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var idText = ReadNumber(t, "id");
            int.TryParse(idText, out var id);
            var count = 0;
            if (t.TryGetProperty("count", out var c))
            {
                if (c.TryGetInt32(out var n))
                    count = n;
                else if (c.ValueKind == JsonValueKind.String && int.TryParse(c.GetString(), out var parsed))
                    count = parsed;
            }

            list.Add(new NhentaiTag(id, NhentaiTagTypes.Canonical(ReadString(t, "type")), name, count));
        }

        return list;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string? ReadNumber(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.String => el.GetString(),
            _ => null,
        };
    }
}
