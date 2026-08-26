using CodeNav.OutOfProc.Constants;

namespace CodeNav.OutOfProc.Languages.TypeScript.Mappers;

public static class TooltipMapper
{
    public static string Map(CodeItemAccessEnum access, string type, string name, string extra)
    {
        var accessString = access == CodeItemAccessEnum.Unknown
            ? string.Empty
            : access.ToString();

        return string.Join(" ", new[] { accessString, type, name, extra }
            .Where(tooltipPart => !string.IsNullOrEmpty(tooltipPart)));
    }
}
