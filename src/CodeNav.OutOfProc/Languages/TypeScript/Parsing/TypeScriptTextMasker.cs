namespace CodeNav.OutOfProc.Languages.TypeScript.Parsing;

/// <summary>
/// TypeScript has no Roslyn-style compiler API available in-process, so CodeNav parses it
/// structurally instead of semantically. Before scanning for declarations, every character
/// that lives inside a string, template literal or comment is replaced with a space so that
/// brace/parenthesis counting and statement-terminator detection never trips over a stray
/// '{', '}' or ';' that only appears as text.
/// </summary>
/// <remarks>
/// Known limitation: regex literals (e.g. <c>/[{};]/</c>) are not special-cased, so a brace or
/// semicolon inside a regex literal can confuse the structural scan. This is a deliberate
/// trade-off to keep the mapper lightweight; it does not attempt to be a full TypeScript parser.
/// </remarks>
public static class TypeScriptTextMasker
{
    public static string Mask(string text)
    {
        var mask = text.ToCharArray();
        var length = text.Length;
        var i = 0;

        while (i < length)
        {
            var current = text[i];

            if (current == '/' && i + 1 < length && text[i + 1] == '/')
            {
                i = BlankLineComment(mask, i, length);
                continue;
            }

            if (current == '/' && i + 1 < length && text[i + 1] == '*')
            {
                i = BlankBlockComment(mask, i, length);
                continue;
            }

            if (current is '\'' or '"' or '`')
            {
                i = BlankQuoted(mask, i, length, current);
                continue;
            }

            i++;
        }

        return new string(mask);
    }

    private static int BlankLineComment(char[] mask, int start, int length)
    {
        var i = start;

        while (i < length && mask[i] != '\n')
        {
            mask[i] = ' ';
            i++;
        }

        return i;
    }

    private static int BlankBlockComment(char[] mask, int start, int length)
    {
        var i = start;
        mask[i] = ' ';
        mask[i + 1] = ' ';
        i += 2;

        while (i < length && !(mask[i] == '*' && i + 1 < length && mask[i + 1] == '/'))
        {
            if (mask[i] != '\n')
            {
                mask[i] = ' ';
            }

            i++;
        }

        if (i < length)
        {
            mask[i] = ' ';
            i++;

            if (i < length)
            {
                mask[i] = ' ';
                i++;
            }
        }

        return i;
    }

    private static int BlankQuoted(char[] mask, int start, int length, char quote)
    {
        var i = start;
        mask[i] = ' ';
        i++;

        while (i < length && mask[i] != quote)
        {
            // Preserve escaped characters (e.g. \` inside a template literal) but still blank them.
            if (mask[i] == '\\' && i + 1 < length)
            {
                mask[i] = ' ';
                i++;
                if (mask[i] != '\n')
                {
                    mask[i] = ' ';
                }
                i++;
                continue;
            }

            if (mask[i] != '\n')
            {
                mask[i] = ' ';
            }

            i++;
        }

        if (i < length)
        {
            mask[i] = ' ';
            i++;
        }

        return i;
    }
}
