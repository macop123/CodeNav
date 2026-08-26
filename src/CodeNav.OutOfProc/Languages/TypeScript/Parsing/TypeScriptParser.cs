using CodeNav.OutOfProc.Constants;
using Microsoft.CodeAnalysis.Text;
using System.Text.RegularExpressions;

namespace CodeNav.OutOfProc.Languages.TypeScript.Parsing;

/// <summary>
/// Parses TypeScript source into a tree of <see cref="TypeScriptNode"/>s.
/// </summary>
/// <remarks>
/// There is no Roslyn-equivalent compiler API available in-process for TypeScript, so this is a
/// lightweight structural (regex + bracket matching) parser rather than a full language parser.
/// It recognizes classes, interfaces, enums, namespaces/modules, functions, arrow-function
/// consts, class/interface members, type aliases, top-level variables and "// #region" blocks.
/// It intentionally does not resolve types or perform any semantic analysis - it only needs to
/// find where declarations start and end.
/// </remarks>
public static class TypeScriptParser
{
    private const string ModifiersPattern =
        @"(?<mods>(?:\b(?:export|declare|default|public|private|protected|readonly|static|abstract|async)\b\s+)*)";

    private const string GenericsPattern = @"(?:<(?:[^<>]|<[^<>]*>)*>)?";

    private static readonly Regex NamespaceRegex = new(
        @"\G" + ModifiersPattern + @"(?:namespace|module)\s+(?<name>[\w$.]+|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*')\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex ClassRegex = new(
        @"\G" + ModifiersPattern + @"class\s+(?<name>[A-Za-z_$][\w$]*)\s*" + GenericsPattern +
        @"\s*(?<heritage>(?:extends\s+[A-Za-z_$][\w$.<>,\s\[\]]*)?(?:\s*implements\s+[A-Za-z_$][\w$.<>,\s\[\]]*)?)\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex InterfaceRegex = new(
        @"\G" + ModifiersPattern + @"interface\s+(?<name>[A-Za-z_$][\w$]*)\s*" + GenericsPattern +
        @"\s*(?<heritage>(?:extends\s+[A-Za-z_$][\w$.<>,\s\[\]]*)?)\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex EnumRegex = new(
        @"\G" + ModifiersPattern + @"(?:const\s+)?enum\s+(?<name>[A-Za-z_$][\w$]*)\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex FunctionRegex = new(
        @"\G" + ModifiersPattern + @"function\s*(?<generator>\*)?\s*(?<name>[A-Za-z_$][\w$]*)\s*" + GenericsPattern,
        RegexOptions.Compiled);

    private static readonly Regex TypeAliasRegex = new(
        @"\G" + ModifiersPattern + @"type\s+(?<name>[A-Za-z_$][\w$]*)\s*" + GenericsPattern,
        RegexOptions.Compiled);

    private static readonly Regex VariableRegex = new(
        @"\G" + ModifiersPattern + @"(?<kind>const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)\s*",
        RegexOptions.Compiled);

    private static readonly Regex ConstructorRegex = new(
        @"\G" + ModifiersPattern + @"(?<kw>constructor)\s*",
        RegexOptions.Compiled);

    private static readonly Regex AccessorRegex = new(
        @"\G" + ModifiersPattern + @"(?<accessor>get|set)\s+(?<name>[A-Za-z_$][\w$]*)\s*",
        RegexOptions.Compiled);

    private static readonly Regex MemberHeaderRegex = new(
        @"\G" + ModifiersPattern + @"(?<generator>\*)?\s*(?<name>\[[^\]]*\]|\#?[A-Za-z_$][\w$]*)\s*(?<optional>[?!])?\s*" + GenericsPattern,
        RegexOptions.Compiled);

    private static readonly Regex RegionStartRegex = new(@"//\s*#region\b[ \t]*(?<name>[^\r\n]*)", RegexOptions.Compiled);
    private static readonly Regex RegionEndRegex = new(@"//\s*#endregion\b", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private enum ScopeKind
    {
        TopLevel,
        Namespace,
        ClassBody,
        InterfaceBody,
    }

    private readonly record struct ArrowScanResult(int ParamsStart, int ParamsEnd, string ReturnType, int BodyEnd, bool IsBlockBody);

    public static List<TypeScriptNode> Parse(string text)
    {
        var masked = TypeScriptTextMasker.Mask(text);
        return ParseScope(masked, text, 0, masked.Length, ScopeKind.TopLevel);
    }

    private static List<TypeScriptNode> ParseScope(string masked, string original, int start, int end, ScopeKind kind)
    {
        var rawMembers = new List<TypeScriptNode>();
        var pos = start;

        while (pos < end)
        {
            pos = SkipWhitespace(masked, pos, end);
            if (pos >= end)
            {
                break;
            }

            if (masked[pos] is ';' or '}')
            {
                pos++;
                continue;
            }

            var (node, nextPos) = kind is ScopeKind.ClassBody or ScopeKind.InterfaceBody
                ? TryParseClassMember(masked, pos, end, kind == ScopeKind.InterfaceBody)
                : TryParseTopLevelDeclaration(masked, original, pos, end);

            if (node != null)
            {
                rawMembers.Add(node);
                pos = Math.Max(nextPos, pos + 1);
                continue;
            }

            pos = SkipUnknownToken(masked, pos, end);
        }

        // Namespaces/classes/interfaces recursively ran their own region scan over their own
        // body. Exclude their spans here so a "// #region" comment that lives inside one of
        // them isn't also picked up (as an empty duplicate) by every enclosing scope.
        var excludedSpans = rawMembers
            .Where(member => member.Kind is CodeItemKindEnum.Namespace or CodeItemKindEnum.Class or CodeItemKindEnum.Interface)
            .Select(member => member.Span)
            .ToList();

        var regions = ParseRegions(original, start, end, excludedSpans);
        return MergeRegions(rawMembers, regions);
    }

    private static (TypeScriptNode? Node, int NextPos) TryParseTopLevelDeclaration(string masked, string original, int pos, int end)
    {
        var namespaceMatch = NamespaceRegex.Match(masked, pos, end - pos);
        if (namespaceMatch.Success && namespaceMatch.Index == pos)
        {
            return ParseContainer(masked, original, pos, end, namespaceMatch, CodeItemKindEnum.Namespace, ScopeKind.Namespace, string.Empty);
        }

        var classMatch = ClassRegex.Match(masked, pos, end - pos);
        if (classMatch.Success && classMatch.Index == pos)
        {
            return ParseContainer(masked, original, pos, end, classMatch, CodeItemKindEnum.Class, ScopeKind.ClassBody, "heritage");
        }

        var interfaceMatch = InterfaceRegex.Match(masked, pos, end - pos);
        if (interfaceMatch.Success && interfaceMatch.Index == pos)
        {
            return ParseContainer(masked, original, pos, end, interfaceMatch, CodeItemKindEnum.Interface, ScopeKind.InterfaceBody, "heritage");
        }

        var enumMatch = EnumRegex.Match(masked, pos, end - pos);
        if (enumMatch.Success && enumMatch.Index == pos)
        {
            return ParseEnum(masked, pos, end, enumMatch);
        }

        var functionMatch = FunctionRegex.Match(masked, pos, end - pos);
        if (functionMatch.Success && functionMatch.Index == pos)
        {
            var afterHeader = SkipWhitespace(masked, functionMatch.Index + functionMatch.Length, end);
            if (afterHeader < end && masked[afterHeader] == '(')
            {
                return ParseMethodLike(masked, pos, end, ParseModifiers(functionMatch.Groups["mods"].Value),
                    functionMatch.Groups["name"].Value, afterHeader, CodeItemKindEnum.Method,
                    new TextSpan(functionMatch.Groups["name"].Index, functionMatch.Groups["name"].Length));
            }
        }

        var typeAliasMatch = TypeAliasRegex.Match(masked, pos, end - pos);
        if (typeAliasMatch.Success && typeAliasMatch.Index == pos)
        {
            var afterHeader = SkipWhitespace(masked, typeAliasMatch.Index + typeAliasMatch.Length, end);
            if (afterHeader < end && masked[afterHeader] == '=')
            {
                return ParseTypeAlias(masked, pos, end, typeAliasMatch, afterHeader);
            }
        }

        var variableMatch = VariableRegex.Match(masked, pos, end - pos);
        if (variableMatch.Success && variableMatch.Index == pos)
        {
            return ParseVariableOrArrow(masked, pos, end, variableMatch);
        }

        return (null, pos);
    }

    private static (TypeScriptNode?, int) TryParseClassMember(string masked, int pos, int end, bool isInterface)
    {
        if (!isInterface)
        {
            var ctorMatch = ConstructorRegex.Match(masked, pos, end - pos);
            if (ctorMatch.Success && ctorMatch.Index == pos)
            {
                var afterHeader = SkipWhitespace(masked, ctorMatch.Index + ctorMatch.Length, end);
                if (afterHeader < end && masked[afterHeader] == '(')
                {
                    return ParseMethodLike(masked, pos, end, ParseModifiers(ctorMatch.Groups["mods"].Value),
                        "constructor", afterHeader, CodeItemKindEnum.Constructor,
                        new TextSpan(ctorMatch.Groups["kw"].Index, ctorMatch.Groups["kw"].Length));
                }
            }
        }

        var accessorMatch = AccessorRegex.Match(masked, pos, end - pos);
        if (accessorMatch.Success && accessorMatch.Index == pos)
        {
            var afterHeader = SkipWhitespace(masked, accessorMatch.Index + accessorMatch.Length, end);
            if (afterHeader < end && masked[afterHeader] == '(')
            {
                return ParseMethodLike(masked, pos, end, ParseModifiers(accessorMatch.Groups["mods"].Value),
                    accessorMatch.Groups["name"].Value, afterHeader, CodeItemKindEnum.Property,
                    new TextSpan(accessorMatch.Groups["name"].Index, accessorMatch.Groups["name"].Length));
            }
        }

        var memberMatch = MemberHeaderRegex.Match(masked, pos, end - pos);
        if (memberMatch.Success && memberMatch.Index == pos && memberMatch.Groups["name"].Success)
        {
            var afterHeader = SkipWhitespace(masked, memberMatch.Index + memberMatch.Length, end);

            if (afterHeader < end && masked[afterHeader] == '(')
            {
                return ParseMethodLike(masked, pos, end, ParseModifiers(memberMatch.Groups["mods"].Value),
                    memberMatch.Groups["name"].Value, afterHeader, CodeItemKindEnum.Method,
                    new TextSpan(memberMatch.Groups["name"].Index, memberMatch.Groups["name"].Length));
            }

            return ParseClassProperty(masked, pos, end, memberMatch);
        }

        return (null, pos);
    }

    private static (TypeScriptNode?, int) ParseContainer(
        string masked, string original, int pos, int end, Match match,
        CodeItemKindEnum kind, ScopeKind scopeKind, string heritageGroup)
    {
        var bodyOpen = match.Index + match.Length - 1; // The pattern always ends with the opening brace.
        var bodyClose = FindMatchingBracket(masked, bodyOpen, '{', '}');

        if (bodyClose >= end)
        {
            return (null, pos);
        }

        var node = new TypeScriptNode
        {
            Kind = kind,
            Name = CleanContainerName(match.Groups["name"].Value),
            Modifiers = ParseModifiers(match.Groups["mods"].Value),
            Heritage = heritageGroup.Length > 0 ? CollapseWhitespace(match.Groups[heritageGroup].Value) : string.Empty,
            Span = new TextSpan(pos, bodyClose + 1 - pos),
            IdentifierSpan = new TextSpan(match.Groups["name"].Index, match.Groups["name"].Length),
        };

        node.Members = ParseScope(masked, original, bodyOpen + 1, bodyClose, scopeKind);

        return (node, bodyClose + 1);
    }

    private static (TypeScriptNode?, int) ParseEnum(string masked, int pos, int end, Match match)
    {
        var bodyOpen = match.Index + match.Length - 1;
        var bodyClose = FindMatchingBracket(masked, bodyOpen, '{', '}');

        if (bodyClose >= end)
        {
            return (null, pos);
        }

        var members = ParseEnumMembers(masked, bodyOpen + 1, bodyClose);

        var node = new TypeScriptNode
        {
            Kind = CodeItemKindEnum.Enum,
            Name = match.Groups["name"].Value,
            Modifiers = ParseModifiers(match.Groups["mods"].Value),
            Type = string.Join(", ", members.Select(member => member.Name)),
            Span = new TextSpan(pos, bodyClose + 1 - pos),
            IdentifierSpan = new TextSpan(match.Groups["name"].Index, match.Groups["name"].Length),
            Members = members,
        };

        return (node, bodyClose + 1);
    }

    private static List<TypeScriptNode> ParseEnumMembers(string masked, int start, int end)
    {
        var members = new List<TypeScriptNode>();
        var pos = start;

        while (pos < end)
        {
            pos = SkipWhitespace(masked, pos, end);
            if (pos >= end)
            {
                break;
            }

            if (masked[pos] == ',')
            {
                pos++;
                continue;
            }

            var nameStart = pos;
            while (pos < end && (char.IsLetterOrDigit(masked[pos]) || masked[pos] is '_' or '$'))
            {
                pos++;
            }

            if (pos == nameStart)
            {
                // Not a plain identifier (e.g. a quoted enum key) - skip defensively to the next comma.
                pos = FindBalancedEnd(masked, pos, end, [',']);
                continue;
            }

            var name = masked[nameStart..pos];
            var memberEnd = FindBalancedEnd(masked, pos, end, [',']);

            members.Add(new TypeScriptNode
            {
                Kind = CodeItemKindEnum.EnumMember,
                Name = name,
                Span = new TextSpan(nameStart, memberEnd - nameStart),
                IdentifierSpan = new TextSpan(nameStart, pos - nameStart),
            });

            pos = memberEnd;
        }

        return members;
    }

    private static (TypeScriptNode?, int) ParseTypeAlias(string masked, int pos, int end, Match match, int equalsPos)
    {
        var statementEnd = FindStatementEnd(masked, equalsPos + 1, end);
        var spanEnd = StatementSpanEnd(masked, statementEnd, end);

        var node = new TypeScriptNode
        {
            Kind = CodeItemKindEnum.Variable,
            Name = match.Groups["name"].Value,
            Modifiers = ParseModifiers(match.Groups["mods"].Value),
            Type = CollapseWhitespace(masked[(equalsPos + 1)..statementEnd]),
            Span = new TextSpan(pos, spanEnd - pos),
            IdentifierSpan = new TextSpan(match.Groups["name"].Index, match.Groups["name"].Length),
        };

        return (node, spanEnd);
    }

    private static (TypeScriptNode?, int) ParseVariableOrArrow(string masked, int pos, int end, Match match)
    {
        var kindKeyword = match.Groups["kind"].Value;
        var headerEnd = match.Index + match.Length;
        var afterHeader = SkipWhitespace(masked, headerEnd, end);

        if (afterHeader < end && masked[afterHeader] == ':')
        {
            afterHeader = FindBalancedEnd(masked, afterHeader + 1, end, ['=', ';']);
            afterHeader = SkipWhitespace(masked, afterHeader, end);
        }

        if (afterHeader < end && masked[afterHeader] == '=')
        {
            var arrow = TryScanArrowFunction(masked, afterHeader + 1, end);

            if (arrow != null)
            {
                var parametersRaw = masked[arrow.Value.ParamsStart..arrow.Value.ParamsEnd];
                var parametersText = parametersRaw.Length > 0 && parametersRaw[0] == '('
                    ? parametersRaw
                    : $"({parametersRaw})";

                var bodyEndExclusive = arrow.Value.IsBlockBody
                    ? arrow.Value.BodyEnd + 1
                    : StatementSpanEnd(masked, arrow.Value.BodyEnd, end);

                var arrowNode = new TypeScriptNode
                {
                    Kind = CodeItemKindEnum.Method,
                    Name = match.Groups["name"].Value,
                    Modifiers = ParseModifiers(match.Groups["mods"].Value),
                    Parameters = CollapseWhitespace(parametersText),
                    Type = CollapseWhitespace(arrow.Value.ReturnType),
                    Span = new TextSpan(pos, bodyEndExclusive - pos),
                    IdentifierSpan = new TextSpan(match.Groups["name"].Index, match.Groups["name"].Length),
                };

                return (arrowNode, bodyEndExclusive);
            }
        }

        var statementEnd = FindStatementEnd(masked, headerEnd, end);
        var spanEnd = StatementSpanEnd(masked, statementEnd, end);
        var typeText = ExtractTypeAnnotation(masked, headerEnd, statementEnd);

        var variableNode = new TypeScriptNode
        {
            Kind = kindKeyword == "const" ? CodeItemKindEnum.Constant : CodeItemKindEnum.Variable,
            Name = match.Groups["name"].Value,
            Modifiers = ParseModifiers(match.Groups["mods"].Value),
            Type = CollapseWhitespace(typeText),
            Span = new TextSpan(pos, spanEnd - pos),
            IdentifierSpan = new TextSpan(match.Groups["name"].Index, match.Groups["name"].Length),
        };

        return (variableNode, spanEnd);
    }

    private static (TypeScriptNode?, int) ParseClassProperty(string masked, int pos, int end, Match match)
    {
        var headerEnd = match.Index + match.Length;
        var afterHeader = SkipWhitespace(masked, headerEnd, end);
        var typeText = string.Empty;

        if (afterHeader < end && masked[afterHeader] == ':')
        {
            afterHeader++;
            var typeStart = afterHeader;
            afterHeader = FindBalancedEnd(masked, afterHeader, end, ['=', ';']);
            typeText = masked[typeStart..afterHeader];
        }

        var statementEnd = FindStatementEnd(masked, afterHeader, end);
        var spanEnd = StatementSpanEnd(masked, statementEnd, end);

        var node = new TypeScriptNode
        {
            Kind = CodeItemKindEnum.Property,
            Name = match.Groups["name"].Value,
            Modifiers = ParseModifiers(match.Groups["mods"].Value),
            Type = CollapseWhitespace(typeText),
            Span = new TextSpan(pos, spanEnd - pos),
            IdentifierSpan = new TextSpan(match.Groups["name"].Index, match.Groups["name"].Length),
        };

        return (node, spanEnd);
    }

    private static (TypeScriptNode?, int) ParseMethodLike(
        string masked, int pos, int end, List<string> modifiers, string name,
        int parenStart, CodeItemKindEnum kind, TextSpan identifierSpan)
    {
        var parenEnd = FindMatchingBracket(masked, parenStart, '(', ')');
        if (parenEnd >= end)
        {
            return (null, pos);
        }

        var afterParams = SkipWhitespace(masked, parenEnd + 1, end);
        var returnType = string.Empty;

        if (afterParams < end && masked[afterParams] == ':')
        {
            afterParams++;
            var typeStart = afterParams;
            afterParams = FindBalancedEnd(masked, afterParams, end, ['{', ';']);
            returnType = masked[typeStart..afterParams];
        }

        afterParams = SkipWhitespace(masked, afterParams, end);

        if (afterParams >= end)
        {
            return (null, pos);
        }

        int nextPos;
        int spanEndExclusive;

        if (masked[afterParams] == '{')
        {
            var bodyClose = FindMatchingBracket(masked, afterParams, '{', '}');
            spanEndExclusive = bodyClose + 1;
            nextPos = bodyClose + 1;
        }
        else if (masked[afterParams] == ';')
        {
            // Ambient declaration or interface/overload signature - no body.
            spanEndExclusive = afterParams + 1;
            nextPos = afterParams + 1;
        }
        else
        {
            return (null, pos);
        }

        var node = new TypeScriptNode
        {
            Kind = kind,
            Name = name,
            Modifiers = modifiers,
            Parameters = CollapseWhitespace(masked[parenStart..(parenEnd + 1)]),
            Type = CollapseWhitespace(returnType),
            Span = new TextSpan(pos, spanEndExclusive - pos),
            IdentifierSpan = identifierSpan,
        };

        return (node, nextPos);
    }

    private static ArrowScanResult? TryScanArrowFunction(string masked, int start, int end)
    {
        var pos = SkipWhitespace(masked, start, end);

        if (MatchesWord(masked, pos, end, "async"))
        {
            pos = SkipWhitespace(masked, pos + "async".Length, end);
        }

        int paramsStart;
        int paramsEnd;

        if (pos < end && masked[pos] == '(')
        {
            paramsStart = pos;
            var close = FindMatchingBracket(masked, pos, '(', ')');
            if (close >= end)
            {
                return null;
            }

            paramsEnd = close + 1;
            pos = paramsEnd;
        }
        else if (pos < end && (char.IsLetter(masked[pos]) || masked[pos] is '_' or '$'))
        {
            paramsStart = pos;
            while (pos < end && (char.IsLetterOrDigit(masked[pos]) || masked[pos] is '_' or '$'))
            {
                pos++;
            }

            paramsEnd = pos;
        }
        else
        {
            return null;
        }

        pos = SkipWhitespace(masked, pos, end);
        var returnType = string.Empty;

        if (pos < end && masked[pos] == ':')
        {
            pos++;
            var typeStart = pos;
            pos = FindBalancedEnd(masked, pos, end, ['=']);
            returnType = masked[typeStart..pos];
        }

        pos = SkipWhitespace(masked, pos, end);

        if (pos + 1 >= end || masked[pos] != '=' || masked[pos + 1] != '>')
        {
            return null;
        }

        pos = SkipWhitespace(masked, pos + 2, end);

        if (pos >= end)
        {
            return null;
        }

        if (masked[pos] == '{')
        {
            var bodyClose = FindMatchingBracket(masked, pos, '{', '}');
            return new ArrowScanResult(paramsStart, paramsEnd, returnType, bodyClose, true);
        }

        var expressionEnd = FindStatementEnd(masked, pos, end);
        return new ArrowScanResult(paramsStart, paramsEnd, returnType, expressionEnd, false);
    }

    private static List<TypeScriptNode> ParseRegions(string original, int start, int end, List<TextSpan> excludedSpans)
    {
        var markers = new List<(int Index, int LineEnd, bool IsStart, string Name)>();

        foreach (Match match in RegionStartRegex.Matches(original))
        {
            if (match.Index < start || match.Index >= end || IsExcluded(match.Index, excludedSpans))
            {
                continue;
            }

            var lineEnd = FindLineEnd(original, match.Index);
            var name = match.Groups["name"].Value.Trim();
            markers.Add((match.Index, lineEnd, true, string.IsNullOrEmpty(name) ? "Region" : name));
        }

        foreach (Match match in RegionEndRegex.Matches(original))
        {
            if (match.Index < start || match.Index >= end || IsExcluded(match.Index, excludedSpans))
            {
                continue;
            }

            markers.Add((match.Index, FindLineEnd(original, match.Index), false, string.Empty));
        }

        markers.Sort((a, b) => a.Index.CompareTo(b.Index));

        var stack = new Stack<TypeScriptNode>();
        var flatRegions = new List<TypeScriptNode>();

        foreach (var marker in markers)
        {
            if (marker.IsStart)
            {
                var region = new TypeScriptNode
                {
                    Kind = CodeItemKindEnum.Region,
                    Name = marker.Name,
                    IdentifierSpan = new TextSpan(marker.Index, marker.LineEnd - marker.Index),
                    Span = new TextSpan(marker.Index, marker.LineEnd - marker.Index),
                };
                stack.Push(region);
                flatRegions.Add(region);
            }
            else if (stack.Count > 0)
            {
                var region = stack.Pop();
                region.Span = new TextSpan(region.Span.Start, marker.LineEnd - region.Span.Start);
            }
        }

        return BuildRegionHierarchy(flatRegions);
    }

    private static List<TypeScriptNode> BuildRegionHierarchy(List<TypeScriptNode> flatRegions)
    {
        var roots = new List<TypeScriptNode>();

        foreach (var region in flatRegions.OrderByDescending(r => r.Span.Length))
        {
            var parent = flatRegions
                .Where(candidate => candidate != region && candidate.Span.Contains(region.Span))
                .OrderBy(candidate => candidate.Span.Length)
                .FirstOrDefault();

            if (parent != null)
            {
                parent.Members.Add(region);
            }
            else
            {
                roots.Add(region);
            }
        }

        return [.. roots.OrderBy(r => r.Span.Start)];
    }

    private static List<TypeScriptNode> MergeRegions(List<TypeScriptNode> members, List<TypeScriptNode> topLevelRegions)
    {
        var result = new List<TypeScriptNode>(topLevelRegions);

        foreach (var member in members)
        {
            if (!TryAddToRegion(topLevelRegions, member))
            {
                result.Add(member);
            }
        }

        return [.. result.OrderBy(node => node.Span.Start)];
    }

    private static bool TryAddToRegion(List<TypeScriptNode> regions, TypeScriptNode member)
    {
        foreach (var region in regions)
        {
            if (region.Kind != CodeItemKindEnum.Region)
            {
                continue;
            }

            if (TryAddToRegion(region.Members, member))
            {
                return true;
            }

            if (member.Span.Start >= region.Span.Start && member.Span.Start <= region.Span.End)
            {
                region.Members.Add(member);
                return true;
            }
        }

        return false;
    }

    private static int SkipUnknownToken(string masked, int pos, int end)
    {
        var close = masked[pos] switch
        {
            '{' => FindMatchingBracket(masked, pos, '{', '}'),
            '(' => FindMatchingBracket(masked, pos, '(', ')'),
            '[' => FindMatchingBracket(masked, pos, '[', ']'),
            _ => -1,
        };

        return close >= 0 ? Math.Min(close + 1, end) : pos + 1;
    }

    private static int FindMatchingBracket(string masked, int openIndex, char open, char close)
    {
        var depth = 0;

        for (var i = openIndex; i < masked.Length; i++)
        {
            if (masked[i] == open)
            {
                depth++;
            }
            else if (masked[i] == close)
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return masked.Length;
    }

    /// <summary>
    /// Scans forward tracking bracket depth (treating '(', '[', '{' and '&lt;' as nesting), stopping
    /// at the first of <paramref name="stopChars"/> found at depth 0, or at an unmatched closing
    /// bracket that must belong to an enclosing scope.
    /// </summary>
    private static int FindBalancedEnd(string masked, int start, int end, char[] stopChars)
    {
        var depth = 0;

        for (var i = start; i < end; i++)
        {
            var c = masked[i];

            if (depth == 0 && Array.IndexOf(stopChars, c) >= 0)
            {
                return i;
            }

            if (c is '(' or '[' or '{' or '<')
            {
                depth++;
                continue;
            }

            if (c is ')' or ']' or '}' or '>')
            {
                if (depth == 0)
                {
                    return i;
                }

                depth--;
            }
        }

        return end;
    }

    private static int FindStatementEnd(string masked, int start, int end)
    {
        var depth = 0;

        for (var i = start; i < end; i++)
        {
            var c = masked[i];

            if (c is '(' or '[' or '{')
            {
                depth++;
                continue;
            }

            if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    return i;
                }

                depth--;
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

    private static string ExtractTypeAnnotation(string masked, int headerEnd, int statementEnd)
    {
        var pos = SkipWhitespace(masked, headerEnd, statementEnd);

        if (pos >= statementEnd || masked[pos] != ':')
        {
            return string.Empty;
        }

        pos++;
        var typeStart = pos;
        var typeEnd = FindBalancedEnd(masked, pos, statementEnd, ['=']);
        return masked[typeStart..typeEnd];
    }

    private static int SkipWhitespace(string masked, int pos, int end)
    {
        while (pos < end && char.IsWhiteSpace(masked[pos]))
        {
            pos++;
        }

        return pos;
    }

    private static bool MatchesWord(string masked, int pos, int end, string word)
    {
        if (pos + word.Length > end || string.CompareOrdinal(masked, pos, word, 0, word.Length) != 0)
        {
            return false;
        }

        var after = pos + word.Length;
        return after >= end || !(char.IsLetterOrDigit(masked[after]) || masked[after] is '_' or '$');
    }

    private static List<string> ParseModifiers(string raw)
        => [.. raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)];

    private static string CollapseWhitespace(string s)
        => WhitespaceRegex.Replace(s, " ").Trim();

    private static bool IsExcluded(int index, List<TextSpan> excludedSpans)
    {
        foreach (var span in excludedSpans)
        {
            if (index >= span.Start && index < span.End)
            {
                return true;
            }
        }

        return false;
    }

    private static int FindLineEnd(string text, int index)
    {
        var lineEnd = text.IndexOf('\n', index);
        return lineEnd == -1 ? text.Length : lineEnd;
    }

    private static string CleanContainerName(string name)
        => name.Length >= 2 && (name[0] == '"' || name[0] == '\'') && name[^1] == name[0]
            ? name[1..^1]
            : name;
}