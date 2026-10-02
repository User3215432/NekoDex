using System.Text;

namespace MangaLibraryApp;

// ════════════════════════════════════════════════════════════════════════
//  Such-Syntax
//     wort                    Freitext (Titel, Autor, Tags)
//     "mehrere wörter"        Freitext-Phrase
//     artist:name             Tag mit Kategorie   (auch parody:… character:… group:… language:… female:… male:… category:…)
//     tag:name  oder  #name   Tag ohne bekannte Kategorie
//     -artist:name  -tag:x    Tag ausschließen
//  Leerzeichen in Tags als _ schreiben (artist:foo_bar) oder in Anführungszeichen setzen.
// ════════════════════════════════════════════════════════════════════════

internal enum SearchTokenKind
{
    Ignored,
    Text,
    IncludeTag,
    ExcludeTag,
}

/// <param name="Raw">Das Token, wie es im Suchfeld steht (inklusive Anführungszeichen).</param>
/// <param name="Value">Bei Tags die normalisierte Textform („artist:foo bar“), bei Freitext der Suchbegriff.</param>
internal sealed record SearchToken(string Raw, SearchTokenKind Kind, string Value);

/// <summary>Ein Tag, das aus dem Suchfeld in die Chips-Leiste übernommen werden soll.</summary>
internal sealed record CommittedTag(string Tag, bool Exclude);

internal static class SearchSyntax
{
    /// <summary>Zerlegt den Suchtext in Tokens (Leerzeichen trennen, Anführungszeichen gruppieren).</summary>
    public static List<SearchToken> Tokenize(string? text)
    {
        var tokens = new List<SearchToken>();
        if (string.IsNullOrWhiteSpace(text))
            return tokens;

        var current = new StringBuilder();
        var inQuotes = false;

        void Flush()
        {
            if (current.Length == 0)
                return;

            tokens.Add(Classify(current.ToString()));
            current.Clear();
        }

        foreach (var ch in text)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                current.Append(ch);
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                Flush();
            }
            else
            {
                current.Append(ch);
            }
        }

        Flush();
        return tokens;
    }

    private static SearchToken Classify(string raw)
    {
        var body = raw;
        var negate = false;
        if (body.Length > 1 && body[0] == '-')
        {
            negate = true;
            body = body[1..];
        }

        // Ausdrückliche Form: tag:… bzw. #…  (auch „tag:artist:foo“)
        string? tag = null;
        if (body.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
            tag = body[4..];
        else if (body[0] == '#')
            tag = body[1..];
        else
        {
            // Kurzform: kategorie:name – nur für bekannte Kategorien (artist, parody, language …) bzw. solche, die in der Datenbank vorkommen.
            // So bleibt „Re:Zero“ ein gewöhnliches Suchwort.
            var colon = body.IndexOf(':');
            if (colon > 0 && TagNames.IsSearchNamespace(body[..colon]))
                tag = body;
        }

        if (tag is not null)
        {
            var normalized = TagNames.Normalize(tag);
            return normalized.Length == 0
                ? new SearchToken(raw, SearchTokenKind.Ignored, string.Empty) // z. B. „tag:“ oder „artist:“ während des Tippens
                : new SearchToken(raw, negate ? SearchTokenKind.ExcludeTag : SearchTokenKind.IncludeTag, normalized);
        }

        var text = raw.Replace("\"", string.Empty).Trim();
        return text.Length == 0 || text == "-"
            ? new SearchToken(raw, SearchTokenKind.Ignored, string.Empty)
            : new SearchToken(raw, SearchTokenKind.Text, text);
    }

    /// <summary>
    /// Trennt fertig getippte Tags vom Rest des Suchtextes. Fertig ist ein Tag, sobald dahinter ein Leerzeichen steht
    /// (oder er nicht das letzte Token ist) – mit <paramref name="commitLast"/> (Enter) auch das letzte.
    /// Übrig bleibt der Freitext samt dem Tag, das gerade noch getippt wird.
    /// </summary>
    public static (List<CommittedTag> Tags, string Remaining) Commit(string? text, bool commitLast)
    {
        var tags = new List<CommittedTag>();
        if (string.IsNullOrEmpty(text))
            return (tags, string.Empty);

        var tokens = Tokenize(text);
        var endsWithSpace = char.IsWhiteSpace(text[^1]);
        var keep = new List<SearchToken>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var isTag = token.Kind is SearchTokenKind.IncludeTag or SearchTokenKind.ExcludeTag;
            var complete = i < tokens.Count - 1 || endsWithSpace || commitLast;

            if (isTag && complete)
                tags.Add(new CommittedTag(token.Value, token.Kind == SearchTokenKind.ExcludeTag));
            else
                keep.Add(token);
        }

        if (tags.Count == 0)
            return (tags, text); // nichts zu übernehmen – Eingabe unverändert lassen

        var remaining = string.Join(' ', keep.Select(t => t.Raw));
        if (remaining.Length > 0 && endsWithSpace)
            remaining += " "; // der Cursor soll hinter einem Leerzeichen weitertippen können
        return (tags, remaining);
    }

    /// <summary>
    /// Das Wort, das gerade getippt wird (letztes Token, falls der Text nicht mit Leerzeichen endet) – Grundlage der Vorschlagsliste.
    /// <c>Query</c> ist der Suchtext für die Tags, <c>Exclude</c> ob ein „-“ davorstand, <c>Raw</c> das Token im Suchfeld.
    /// </summary>
    public static (string Query, bool Exclude, string Raw)? CurrentWord(string? text)
    {
        if (string.IsNullOrEmpty(text) || char.IsWhiteSpace(text[^1]))
            return null;

        var tokens = Tokenize(text);
        if (tokens.Count == 0)
            return null;

        var raw = tokens[^1].Raw;
        var body = raw;
        var exclude = false;
        if (body.Length > 1 && body[0] == '-')
        {
            exclude = true;
            body = body[1..];
        }

        if (body.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
            body = body[4..];
        else if (body.Length > 0 && body[0] == '#')
            body = body[1..];

        body = body.Replace("\"", string.Empty).Trim();
        return body.Length == 0 ? null : (body, exclude, raw);
    }

    /// <summary>Entfernt das letzte Token (<paramref name="raw"/>) aus dem Suchtext – nach dem Übernehmen eines Vorschlags.</summary>
    public static string RemoveLastWord(string? text, string raw)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var index = text.LastIndexOf(raw, StringComparison.Ordinal);
        if (index < 0)
            return text;

        var rest = text[..index].TrimEnd();
        return rest.Length > 0 ? rest + " " : string.Empty;
    }

    /// <summary>Trennt eine Liste an Komma, Semikolon und Zeilenumbruch („romance, school life“).</summary>
    public static List<string> SplitList(string? text) =>
        (text ?? string.Empty)
            .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}
