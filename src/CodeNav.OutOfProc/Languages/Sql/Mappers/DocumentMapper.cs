using CodeNav.OutOfProc.Interfaces;
using CodeNav.OutOfProc.Languages.Sql.Parsing;
using CodeNav.OutOfProc.Models;
using CodeNav.OutOfProc.ViewModels;
using Microsoft.VisualStudio.Extensibility;

namespace CodeNav.OutOfProc.Languages.Sql.Mappers;

public class DocumentMapper : IDocumentMapper
{
    /// <summary>
    /// Map text document to list of code items.
    /// </summary>
    /// <remarks>
    /// Like the TypeScript mapper, this does not need to look at any other files in the
    /// solution: there is no semantic model being built, just a structural scan of the
    /// current document.
    /// </remarks>
    public Task<List<CodeItem>> MapDocument(
        string text,
        string? excludeFilePath,
        CodeDocumentViewModel codeDocumentViewModel,
        VisualStudioExtensibility extensibility,
        CancellationToken cancellationToken)
        => MapDocument(text, codeDocumentViewModel, cancellationToken);

    /// <summary>
    /// Map text document to list of code items.
    /// </summary>
    public static Task<List<CodeItem>> MapDocument(
        string text,
        CodeDocumentViewModel codeDocumentViewModel,
        CancellationToken cancellationToken)
    {
        var nodes = SqlParser.Parse(text);
        var codeItems = CodeItemMapper.MapNodes(nodes, codeDocumentViewModel);

        return Task.FromResult(codeItems);
    }

    public bool CanMapDocument(
        string filePath,
        GlobalSettings settings)
        => settings.EnableSql &&
           filePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);
}
