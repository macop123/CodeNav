using CodeNav.OutOfProc.Constants;
using Microsoft.CodeAnalysis.Text;

namespace CodeNav.OutOfProc.Languages.TypeScript.Parsing;

/// <summary>
/// Structural representation of a single TypeScript declaration (class, interface, enum,
/// function, property, region, etc.), as produced by <see cref="TypeScriptParser"/>.
/// </summary>
public sealed class TypeScriptNode
{
    public CodeItemKindEnum Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Raw modifier keywords found before the declaration (export, public, private, static, etc.)
    /// </summary>
    public List<string> Modifiers { get; set; } = [];

    /// <summary>
    /// Parameter list text, e.g. "(id: number, name: string)"
    /// </summary>
    public string Parameters { get; set; } = string.Empty;

    /// <summary>
    /// Return type or property/field type, e.g. "string" or "Promise&lt;void&gt;"
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Extends/implements clause text for classes and interfaces, e.g. " extends Base implements IFoo"
    /// </summary>
    public string Heritage { get; set; } = string.Empty;

    /// <summary>
    /// Full span of the declaration, including its body.
    /// </summary>
    public TextSpan Span { get; set; }

    /// <summary>
    /// Span of just the identifier/name token, used to position the caret on navigation.
    /// </summary>
    public TextSpan IdentifierSpan { get; set; }

    public List<TypeScriptNode> Members { get; set; } = [];

    public bool HasModifier(string modifier)
        => Modifiers.Contains(modifier, StringComparer.Ordinal);
}
