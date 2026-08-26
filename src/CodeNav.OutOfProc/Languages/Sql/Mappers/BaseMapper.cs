using CodeNav.OutOfProc.Constants;
using CodeNav.OutOfProc.Languages.Sql.Parsing;
using CodeNav.OutOfProc.ViewModels;
using Microsoft.CodeAnalysis.Text;

namespace CodeNav.OutOfProc.Languages.Sql.Mappers;

public static class BaseMapper
{
    /// <summary>
    /// Map commonly shared code item properties based on the parsed SQL node.
    /// </summary>
    /// <typeparam name="T">CodeItem or a type derived from it</typeparam>
    /// <param name="node">Parsed SQL node</param>
    /// <param name="codeDocumentViewModel">Code document view model used in the CodeNav tool window</param>
    public static T MapBase<T>(SqlNode node, CodeDocumentViewModel codeDocumentViewModel) where T : CodeItem, new()
    {
        var codeItem = new T();

        var fullName = string.IsNullOrEmpty(node.Schema) ? node.Name : $"{node.Schema}.{node.Name}";

        codeItem.Name = node.Name;
        codeItem.FullName = fullName;
        // Prefixed with Kind so e.g. a table and a view sharing a name can't collide.
        codeItem.Id = $"{node.Kind}:{fullName}";
        codeItem.Tooltip = TooltipMapper.Map(node.Kind, fullName, node.Parameters);
        // SQL has no public/private concept - every object is mapped as Public. See
        // ARCHITECTURE.md / design notes for the resulting (lack of) effect on access filters.
        codeItem.Access = CodeItemAccessEnum.Public;
        codeItem.CodeDocumentViewModel = codeDocumentViewModel;

        codeItem.Span = node.Span;
        codeItem.IdentifierSpan = node.IdentifierSpan;
        codeItem.OutlineSpan = MapOutlineSpan(node.Span, node.IdentifierSpan);

        return codeItem;
    }

    private static TextSpan MapOutlineSpan(TextSpan span, TextSpan identifierSpan)
    {
        var outlineStart = identifierSpan.End > 0 ? identifierSpan.End : span.Start;
        return new TextSpan(outlineStart, Math.Max(0, span.End - outlineStart));
    }
}
