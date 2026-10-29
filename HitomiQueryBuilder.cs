namespace MangaLibraryApp;

/// <summary>
/// Hitomi-Suchbegriffe: ohne Bereich wird <c>tag:</c> ergänzt, Namen mit Leerzeichen stehen in Anführungszeichen.
/// </summary>
public static class HitomiQueryBuilder
{
    /// <summary><c>full color</c> → <c>tag:"full color"</c>, <c>female:big breasts</c> → <c>female:"big breasts"</c>.</summary>
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var text = raw.Trim();
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

        name = TagNames.CleanName(name);
        if (name.Length == 0)
            return string.Empty;

        var area = MapNamespace(ns);
        if (area.Length == 0)
            area = "tag";

        var value = name.Contains(' ', StringComparison.Ordinal) ? "\"" + name + "\"" : name;
        return area + ":" + value;
    }

    private static string MapNamespace(string ns)
    {
        if (ns.Length == 0)
            return string.Empty;
        return ns.ToLowerInvariant() switch
        {
            "language" or "languages" or "lang" => "language",
            "artist" or "artists" => "artist",
            "series" or "parody" or "parodies" => "series",
            "character" or "characters" => "character",
            "group" or "groups" or "circle" or "circles" => "group",
            "female" => "female",
            "male" => "male",
            "tag" or "tags" or "genre" or "genres" => "tag",
            "type" or "types" or "category" or "categories" => "type",
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
