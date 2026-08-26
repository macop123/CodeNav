using CodeNav.OutOfProc.Constants;
using CodeNav.OutOfProc.Languages.Sql.Parsing;
using CodeNav.OutOfProc.Mappers;
using CodeNav.OutOfProc.ViewModels;

namespace CodeNav.OutOfProc.Languages.Sql.Mappers;

public static class CodeItemMapper
{
    public static List<CodeItem> MapNodes(IEnumerable<SqlNode> nodes, CodeDocumentViewModel codeDocumentViewModel)
        => [.. nodes.Select(node => MapNode(node, codeDocumentViewModel))];

    private static CodeItem MapNode(SqlNode node, CodeDocumentViewModel codeDocumentViewModel)
        => node.Kind switch
        {
            CodeItemKindEnum.Procedure or CodeItemKindEnum.Function => MapFunction(node, codeDocumentViewModel),
            _ => MapPlainItem(node, codeDocumentViewModel),
        };

    private static CodeFunctionItem MapFunction(SqlNode node, CodeDocumentViewModel codeDocumentViewModel)
    {
        var codeItem = BaseMapper.MapBase<CodeFunctionItem>(node, codeDocumentViewModel);
        codeItem.Kind = node.Kind;
        codeItem.Parameters = node.Parameters;
        codeItem.ReturnType = node.ReturnType;
        codeItem.Moniker = IconMapper.MapMoniker(codeItem.Kind, codeItem.Access);

        return codeItem;
    }

    // View and Table: no members, no parameters/return type - plain leaf CodeItem.
    private static CodeItem MapPlainItem(SqlNode node, CodeDocumentViewModel codeDocumentViewModel)
    {
        var codeItem = BaseMapper.MapBase<CodeItem>(node, codeDocumentViewModel);
        codeItem.Kind = node.Kind;
        codeItem.Moniker = IconMapper.MapMoniker(codeItem.Kind, codeItem.Access);

        return codeItem;
    }
}
