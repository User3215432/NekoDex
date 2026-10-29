namespace MangaLibraryApp;

/// <summary>
/// E-Hentai-Suchbegriffe. Leerzeichen werden Unterstriche.
/// <c>full color</c> und <c>uncensored</c> liegen auf e-hentai.org im Namespace <c>other:</c>
/// (<c>other:full_color$</c>). <c>misc:full_color$</c> liefert keine Treffer.
/// </summary>
public static class EHentaiQueryBuilder
{
    private static readonly HashSet<string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        "english", "japanese", "chinese", "korean", "spanish", "french", "german",
        "portuguese", "russian", "italian", "thai", "vietnamese", "speechless",
        "translated", "rewrite", "text cleaned",
    };

    private static readonly HashSet<string> Misc = new(StringComparer.OrdinalIgnoreCase)
    {
        "full color", "uncensored", "mosaic censorship", "incomplete", "ongoing",
        "anthology", "artbook", "tankoubon", "multi-work series", "sample",
        "webtoon", "western", "rewritten", "already uploaded", "colored",
    };

    private static readonly HashSet<string> Female = new(StringComparer.OrdinalIgnoreCase)
    {
        "big breasts", "sole female", "nakadashi", "pantyhose", "stockings",
        "schoolgirl uniform", "blowjob", "paizuri", "ahegao", "femdom",
    };

    /// <summary><c>female:dickgirl on female</c> → <c>female:dickgirl_on_female$</c>, <c>other:full color</c> → <c>other:full_color$</c>.</summary>
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var text = raw.Trim();
        if (text.EndsWith('$'))
            text = text[..^1].TrimEnd();

        var colon = text.IndexOf(':');
        string ns;
        string name;
        if (colon > 0 && IsNamespace(text[..colon]))
        {
            ns = text[..colon].Trim().Trim('"');
            name = Unwrap(text[(colon + 1)..]);
        }
        else
        {
            ns = string.Empty;
            name = Unwrap(text);
        }

        name = TagNames.CleanName(name).Replace(' ', '_');
        if (name.Length == 0)
            return string.Empty;

        var area = MapNamespace(ns);
        if (area.Length == 0)
            area = BareNamespace(name.Replace('_', ' '));

        return area + ":" + name + "$";
    }

    private static string BareNamespace(string spacedName)
    {
        if (Languages.Contains(spacedName))
            return "language";
        if (Female.Contains(spacedName))
            return "female";
        if (Misc.Contains(spacedName))
            return "other";
        return "other";
    }

    private static string MapNamespace(string ns)
    {
        if (ns.Length == 0)
            return string.Empty;
        return ns.ToLowerInvariant() switch
        {
            "language" or "languages" or "lang" => "language",
            "artist" or "artists" => "artist",
            "character" or "characters" => "character",
            "group" or "groups" or "circle" or "circles" => "group",
            "parody" or "parodies" or "series" => "parody",
            "female" => "female",
            "male" => "male",
            "mixed" => "mixed",
            "other" or "misc" => "other",
            "reclass" => "reclass",
            "tag" or "tags" or "genre" or "genres" => "other",
            "temp" => "temp",
            "cosplayer" or "cosplayers" => "cosplayer",
            "location" or "locations" => "location",
            _ => string.Empty,
        };
    }

    private static bool IsNamespace(string prefix)
    {
        var text = prefix.Trim().Trim('"');
        return text.Length is >= 2 and <= 20 && text.All(char.IsAsciiLetter);
    }

    private static string Unwrap(string value)
    {
        var text = value.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1];
        return text.Trim();
    }
}
