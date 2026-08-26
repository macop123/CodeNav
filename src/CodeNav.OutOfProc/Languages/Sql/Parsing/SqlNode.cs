using CodeNav.OutOfProc.Constants;
using Microsoft.CodeAnalysis.Text;

namespace CodeNav.OutOfProc.Languages.Sql.Parsing;

/// <summary>
/// Structural representation of a single top-level SQL object declaration (procedure,
/// function, view or table), as produced by <see cref="SqlParser"/>.
/// </summary>
public sealed class SqlNode
{
    public CodeItemKindEnum Kind { get; set; }

    /// <summary>
    /// Bare object name, without its owner/schema qualifier.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Owner/schema the object is qualified with (e.g. "dbo" in "dbo.GetUser"), if any.
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>
    /// Parameter list text for procedures/functions, e.g. "@id int, @name varchar(50)".
    /// </summary>
    public string Parameters { get; set; } = string.Empty;

    /// <summary>
    /// Return type text for functions, e.g. "RETURNS int".
    /// </summary>
    public string ReturnType { get; set; } = string.Empty;

    /// <summary>
    /// Full span of the declaration/statement.
    /// </summary>
    public TextSpan Span { get; set; }

    /// <summary>
    /// Span of just the object name, used to position the caret on navigation.
    /// </summary>
    public TextSpan IdentifierSpan { get; set; }
}
