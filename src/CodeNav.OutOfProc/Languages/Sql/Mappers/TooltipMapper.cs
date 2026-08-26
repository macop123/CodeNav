using CodeNav.OutOfProc.Constants;

namespace CodeNav.OutOfProc.Languages.Sql.Mappers;

public static class TooltipMapper
{
    public static string Map(CodeItemKindEnum kind, string fullName, string parameters)
        => string.Join(" ", new[] { kind.ToString(), fullName, parameters }
            .Where(part => !string.IsNullOrEmpty(part)));
}
