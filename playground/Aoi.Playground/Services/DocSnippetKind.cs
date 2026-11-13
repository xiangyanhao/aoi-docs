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

        // Infix convert *uses* without a convert definition are incomplete demos.
        if (Regex.IsMatch(code, @"\bconvert\b")
            && !Regex.IsMatch(code, @"\bconvert\s+(implicit|explicit)\b")
            && !Regex.IsMatch(code, @"\bop\b[\s\S]*\bconvert\b"))
            return false;

        // Forms the interpreter still cannot parse reliably — keep as syntax.
        // Generic type decls: class Dictionary[TKey, TValue], struct Vector[T: …], …
        if (Regex.IsMatch(code, @"\b(class|struct|union|enum)\s+\w+\s*\["))
            return false;
        // Constrained generic ops: op max[T: Ord[T]] … (not arr[dyn[T: …]] in a type position)
        if (Regex.IsMatch(code, @"\bop\b(?:\s+\w+){0,3}\s*\[[^\]]*:\s*"))
            return false;
        // Comparison as operator name: op a < b
        if (Regex.IsMatch(code, @"\bop\s+\w+\s*<\s*"))
            return false;
        // Multiply as operator name without a type signature: op a * u { / ;
        if (Regex.IsMatch(code, @"\bop\s+\w+\s*\*\s+\w+\s*[\{\;]"))
            return false;
        // Static-style ops only fail inside trait declarations today.
        if (Regex.IsMatch(code, @"\btrait\b[\s\S]*\bop\s+\w+::"))
            return false;
        // Multi-constraint type params: [TNum, TVec: Add[TNum], Sub[TNum], …]
        if (Regex.IsMatch(code, @"\[[^\]]*:\s*\w+\s*\[[^\]]*\]\s*,"))
            return false;
        // type aliases to dyn[…]
        if (Regex.IsMatch(code, @"\btype\b[^=]*=\s*dyn\b"))
            return false;

        // Declaration-only snippets (get/set/struct/impl/…) are runnable even with no output.
        return DeclOrOutput().IsMatch(code);
    }
}
