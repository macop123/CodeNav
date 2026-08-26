namespace CodeNav.OutOfProc.Languages.Sql.Parsing;

/// <summary>
/// T-SQL/ASE has no compiler API available in-process, so CodeNav parses it structurally
/// instead of semantically. Before scanning for CREATE/ALTER headers, every character that
/// lives inside a string literal or a comment is replaced with a space so that paren-depth
/// counting, statement-terminator detection and GO-batch splitting never trip over a stray
/// '(', ')', ';' or "GO" that only appears as text (e.g. inside dynamic SQL).
/// </summary>
/// <remarks>
/// Known limitations: bracketed ([Name]) and double-quoted ("Name") identifiers are left
/// untouched - their content is never masked - so a literal '(' or ')' inside such an
/// identifier can still confuse paren-depth counting. This is deliberately accepted, since
/// masking their interior would also erase the name text the mapper needs to read back out.
/// Double-quoted text is never treated as a string literal (the default QUOTED_IDENTIFIER ON
/// behavior), and block comments do not nest.
/// </remarks>
public static class SqlTextMasker
{
    public static string Mask(string text)
    {
        var mask = text.ToCharArray();
        var length = text.Length;
        var i = 0;

        while (i < length)
        {
            var current = text[i];

            if (current == '-' && i + 1 < length && text[i + 1] == '-')
            {
                i = BlankLineComment(mask, i, length);
                continue;
            }

            if (current == '/' && i + 1 < length && text[i + 1] == '*')
            {
                i = BlankBlockComment(mask, i, length);
                continue;
            }

            if (current == '\'')
            {
                i = BlankQuoted(mask, i, length);
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

    /// <summary>
    /// Blanks a single-quoted string literal. A doubled quote ('') is the T-SQL/ASE escape
    /// for an embedded quote and does not end the literal.
    /// </summary>
    private static int BlankQuoted(char[] mask, int start, int length)
    {
        var i = start;
        mask[i] = ' ';
        i++;

        while (i < length)
        {
            if (mask[i] == '\'')
            {
                if (i + 1 < length && mask[i + 1] == '\'')
                {
                    mask[i] = ' ';
                    mask[i + 1] = ' ';
                    i += 2;
                    continue;
                }

                mask[i] = ' ';
                i++;
                break;
            }

            if (mask[i] != '\n')
            {
                mask[i] = ' ';
            }

            i++;
        }

        return i;
    }
}
