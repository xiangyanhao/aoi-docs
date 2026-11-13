using System.Text.RegularExpressions;

namespace Aoi.Playground.Services;

public static partial class DocSnippetKind
{
    // Placeholder-heavy / ellipsis snippets are grammar illustrations, not programs.
    // Numbered placeholders require digits (repeat0 / element0) so real ops like `repeat` stay runnable.
    [GeneratedRegex(
        @"\.\.\.|\b(statement\d*|expression\d*|condition|pattern|branch\d*|Type\d*|fieldName\d*|ValueName\d*|ConditionName\d*|EnumName|StructName|ClassName|UnionName|element\d+|repeat\d+|TraitName|GenericName|param\d*|LhsType|RhsType|ResultType|opName|implName|ImplName|FromType|ToType|SelfType|PropertyType|IndexType|ValueType|TypeName|TypeDef|propertyName|proertyName)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex TemplateMarker();

    [GeneratedRegex(
        @"\b(let|op|struct|class|enum|union|trait|impl|get|set|convert|literal|type|printLine|print)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeclOrOutput();

    public static bool LooksRunnable(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        if (TemplateMarker().IsMatch(code)) return false;

        // Generics / traits / dyn are only partially reliable — keep as syntax for now.
        if (Regex.IsMatch(code, @"\b(trait|impl|dyn)\b"))
            return false;

        // Generic type parameters on operator declarations are not supported yet
        // (e.g. `op array convert[T, TRes] ...`).
        if (Regex.IsMatch(code, @"\bop\b(?:\s+\w+){1,3}\s*\["))
            return false;

        // Other generic decls that still use placeholder-y type params.
        if (Regex.IsMatch(code, @"\b(get|set|class|struct|union|enum|convert|literal|type)\s+\w+\s*\["))
            return false;

        // Infix convert *uses* without a convert definition are incomplete demos.
        if (Regex.IsMatch(code, @"\bconvert\b")
            && !Regex.IsMatch(code, @"\bconvert\s+(implicit|explicit)\b")
            && !Regex.IsMatch(code, @"\bop\b[\s\S]*\bconvert\b"))
            return false;

        // Require a declaration or print — bare expression fragments are usually incomplete demos.
        return DeclOrOutput().IsMatch(code);
    }
}
