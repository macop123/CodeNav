using CodeNav.OutOfProc.Constants;
using Microsoft.CodeAnalysis.Text;
using System.Text.RegularExpressions;

namespace CodeNav.OutOfProc.Languages.Sql.Parsing;

/// <summary>
/// Parses T-SQL/Sybase ASE source into a flat list of <see cref="SqlNode"/>s.
/// </summary>
/// <remarks>
/// There is no compiler API available in-process for SQL, so - like the TypeScript mapper -
/// this is a lightweight structural (regex + paren matching) scan rather than a full parser,
/// and it deliberately never interprets the body of an object. It only needs to find where a
/// CREATE/ALTER PROCEDURE, FUNCTION, VIEW or TABLE declaration starts and ends. This is what
/// lets it tolerate Sybase ASE syntax that would not parse as valid MSSQL: the body is always
/// skipped, never validated.
///
/// Two SQL-specific rules replace bracket/BEGIN-END matching for locating the end of a
/// declaration:
/// - CREATE/ALTER PROCEDURE, FUNCTION and VIEW must be the only statement in their batch (a
///   rule enforced by both MSSQL and ASE), so their span simply runs to the end of the
///   current batch. This deliberately avoids counting BEGIN/END pairs, because "BEGIN
///   TRAN[SACTION]" is closed by COMMIT/ROLLBACK, not END, which would otherwise throw off a
///   naive BEGIN/END counter.
/// - CREATE TABLE's span runs to the matching closing paren of its column list (multiple
///   CREATE TABLE statements can share a batch), and ALTER TABLE's span runs to its own
///   statement-terminating semicolon (or batch end).
/// </remarks>
public static class SqlParser
{
    private const string IdentifierPart = @"(?:\[[^\]]*\]|""[^""]*""|[A-Za-z_#][\w#$]*)";

    private static readonly Regex GoRegex = new(
        @"^[ \t]*GO(?:[ \t]+\d+)?[ \t]*\r?$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex HeaderRegex = new(
        @"\G\b(?<action>CREATE|ALTER)\b\s+(?:\bOR\s+ALTER\b\s+)?\b(?<kind>PROC(?:EDURE)?|FUNCTION|VIEW|TABLE)\b\s+" +
        @"(?:(?<schema>" + IdentifierPart + @")\s*\.\s*)?" +
        @"(?<name>" + IdentifierPart + @")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public static List<SqlNode> Parse(string text)
    {
        var masked = SqlTextMasker.Mask(text);
        var nodes = new List<SqlNode>();

        foreach (var (batchStart, batchEnd) in SplitBatches(masked))
        {
            ParseBatch(masked, batchStart, batchEnd, nodes);
        }

        return nodes;
    }

    private static IEnumerable<(int Start, int End)> SplitBatches(string masked)
    {
        var start = 0;

        foreach (Match match in GoRegex.Matches(masked))
        {
            yield return (start, match.Index);
            start = match.Index + match.Length;
        }

        yield return (start, masked.Length);
    }

    private static void ParseBatch(string masked, int batchStart, int batchEnd, List<SqlNode> nodes)
    {
        var pos = batchStart;
        var depth = 0;

        while (pos < batchEnd)
        {
            var c = masked[pos];

            if (c == '(')
            {
                depth++;
                pos++;
                continue;
            }

            if (c == ')')
            {
                if (depth > 0)
                {
                    depth--;
                }

                pos++;
                continue;
            }

            if (depth == 0)
            {
                var match = HeaderRegex.Match(masked, pos, batchEnd - pos);

                if (match.Success && match.Index == pos)
                {
                    pos = ParseHeader(masked, batchEnd, match, nodes);
                    continue;
                }
            }

            pos++;
        }
    }

    private static int ParseHeader(string masked, int batchEnd, Match match, List<SqlNode> nodes)
    {
        var headerStart = match.Index;
        var kind = ResolveKind(match.Groups["kind"].Value);

        if (kind == CodeItemKindEnum.Unknown)
        {
            return headerStart + 1;
        }

        var isAlter = string.Equals(match.Groups["action"].Value, "ALTER", StringComparison.OrdinalIgnoreCase);
        var nameGroup = match.Groups["name"];
        var schemaGroup = match.Groups["schema"];
        var name = CleanIdentifier(nameGroup.Value);
        var schema = schemaGroup.Success ? CleanIdentifier(schemaGroup.Value) : null;
        var identifierSpan = new TextSpan(nameGroup.Index, nameGroup.Length);
        var afterHeader = match.Index + match.Length;

        var span = kind == CodeItemKindEnum.Table
            ? ParseTableSpan(masked, headerStart, afterHeader, batchEnd, isAlter)
            : new TextSpan(headerStart, batchEnd - headerStart);

        var parameters = string.Empty;
        var returnType = string.Empty;

        if (kind is CodeItemKindEnum.Procedure or CodeItemKindEnum.Function)
        {
            (parameters, returnType) = ParseParametersAndReturnType(masked, kind, afterHeader, batchEnd);
        }

        nodes.Add(new SqlNode
        {
            Kind = kind,
            Name = name,
            Schema = schema,
            Parameters = parameters,
            ReturnType = returnType,
            Span = span,
            IdentifierSpan = identifierSpan,
        });

        return Math.Max(span.End, headerStart + 1);
    }

    private static TextSpan ParseTableSpan(string masked, int headerStart, int afterHeader, int batchEnd, bool isAlter)
    {
        if (isAlter)
        {
            var statementEnd = FindStatementEnd(masked, afterHeader, batchEnd);
            return TextSpan.FromBounds(headerStart, StatementSpanEnd(masked, statementEnd, batchEnd));
        }

        var openParen = FindChar(masked, afterHeader, batchEnd, '(');

        if (openParen == -1)
        {
            var statementEnd = FindStatementEnd(masked, afterHeader, batchEnd);
            return TextSpan.FromBounds(headerStart, StatementSpanEnd(masked, statementEnd, batchEnd));
        }

        var close = FindMatchingBracket(masked, openParen, batchEnd);
        var columnListEnd = Math.Min(close + 1, batchEnd);

        return TextSpan.FromBounds(headerStart, IncludeTrailingSemicolon(masked, columnListEnd, batchEnd));
    }

    private static (string Parameters, string ReturnType) ParseParametersAndReturnType(
        string masked, CodeItemKindEnum kind, int afterHeader, int batchEnd)
    {
        var asIndex = FindKeywordAtDepthZero(masked, afterHeader, batchEnd, "AS");
        var tailEnd = asIndex >= 0 ? asIndex : batchEnd;

        if (kind == CodeItemKindEnum.Procedure)
        {
            return (CollapseWhitespace(masked[afterHeader..tailEnd]), string.Empty);
        }

        // Function: parameters are always parenthesized; whatever remains up to AS is the
        // return type (typically "RETURNS int").
        var openParen = FindChar(masked, afterHeader, tailEnd, '(');

        if (openParen == -1)
        {
            return (string.Empty, CollapseWhitespace(masked[afterHeader..tailEnd]));
        }

        var close = FindMatchingBracket(masked, openParen, tailEnd);
        var closeClamped = Math.Min(close + 1, tailEnd);

        return (
            CollapseWhitespace(masked[openParen..closeClamped]),
            CollapseWhitespace(masked[closeClamped..tailEnd]));
    }

    private static CodeItemKindEnum ResolveKind(string kindText)
        => kindText.ToUpperInvariant() switch
        {
            "PROC" or "PROCEDURE" => CodeItemKindEnum.Procedure,
            "FUNCTION" => CodeItemKindEnum.Function,
            "VIEW" => CodeItemKindEnum.View,
            "TABLE" => CodeItemKindEnum.Table,
            _ => CodeItemKindEnum.Unknown,
        };

    private static string CleanIdentifier(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '[' && raw[^1] == ']')
        {
            return raw[1..^1].Replace("]]", "]");
        }

        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            return raw[1..^1].Replace("\"\"", "\"");
        }

        return raw;
    }

    private static int FindChar(string masked, int start, int end, char target)
    {
        for (var i = start; i < end; i++)
        {
            if (masked[i] == target)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindMatchingBracket(string masked, int openIndex, int end)
    {
        var depth = 0;

        for (var i = openIndex; i < end; i++)
        {
            if (masked[i] == '(')
            {
                depth++;
            }
            else if (masked[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return end;
    }

    /// <summary>
    /// Scans forward tracking paren depth, returning the index of the first occurrence of
    /// <paramref name="keyword"/> found as a whole word at depth 0, or -1 if not found.
    /// </summary>
    private static int FindKeywordAtDepthZero(string masked, int start, int end, string keyword)
    {
        var depth = 0;
        var i = start;

        while (i < end)
        {
            var c = masked[i];

            if (c == '(')
            {
                depth++;
                i++;
                continue;
            }

            if (c == ')')
            {
                if (depth > 0)
                {
                    depth--;
                }

                i++;
                continue;
            }

            if (depth == 0 && MatchesWordAt(masked, i, end, keyword))
            {
                return i;
            }

            i++;
        }

        return -1;
    }

    private static bool MatchesWordAt(string text, int pos, int end, string word)
    {
        if (pos + word.Length > end ||
            string.Compare(text, pos, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return false;
        }

        var before = pos - 1;
        if (before >= 0 && IsIdentifierChar(text[before]))
        {
            return false;
        }

        var after = pos + word.Length;
        return after >= end || !IsIdentifierChar(text[after]);
    }

    private static bool IsIdentifierChar(char c)
        => char.IsLetterOrDigit(c) || c is '_' or '#' or '$';

    private static int FindStatementEnd(string masked, int start, int end)
    {
        var depth = 0;

        for (var i = start; i < end; i++)
        {
            var c = masked[i];

            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c == ')')
            {
                if (depth > 0)
                {
                    depth--;
                }

                continue;
            }

            if (c == ';' && depth == 0)
            {
                return i;
            }
        }

        return end;
    }

    private static int StatementSpanEnd(string masked, int terminatorIndex, int scopeEnd)
    {
        if (terminatorIndex >= scopeEnd)
        {
            return scopeEnd;
        }

        return masked[terminatorIndex] == ';' ? terminatorIndex + 1 : terminatorIndex;
    }

    /// <summary>
    /// Extends <paramref name="pos"/> to include a semicolon that directly follows (only
    /// whitespace/blanked-comment in between) - but never searches further, so it can never
    /// swallow a later, unrelated statement's own terminator.
    /// </summary>
    private static int IncludeTrailingSemicolon(string masked, int pos, int end)
    {
        var i = SkipWhitespace(masked, pos, end);
        return i < end && masked[i] == ';' ? i + 1 : pos;
    }

    private static int SkipWhitespace(string masked, int pos, int end)
    {
        while (pos < end && char.IsWhiteSpace(masked[pos]))
        {
            pos++;
        }

        return pos;
    }

    private static string CollapseWhitespace(string s)
        => WhitespaceRegex.Replace(s, " ").Trim();
}
