using System.Text.RegularExpressions;

namespace Aoi.Playground.Services;

public static partial class DocSnippetKind
{
    // Placeholder-heavy / ellipsis snippets are grammar illustrations, not programs.
    [GeneratedRegex(
        @"\.\.\.|\b(statement\d*|expression\d*|condition|pattern|branch\d*|Type\d*|fieldName\d*|ValueName\d*|ConditionName\d*|EnumName|StructName|ClassName|UnionName|element\d*|repeat\d*|iter|TraitName|GenericName|param\d*|LhsType|RhsType|ResultType|opName|implName)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex TemplateMarker();

    public static bool LooksRunnable(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        if (TemplateMarker().IsMatch(code)) return false;

        // Generics / traits / dyn are only partially implemented — show as syntax for now.
        if (Regex.IsMatch(code, @"\b(trait|impl|dyn)\b")
            || Regex.IsMatch(code, @"\b(op|get|set|class|struct|union|enum)\s+\w+\s*\[")
            || Regex.IsMatch(code, @"\bconvert\b")
            || code.Contains("::"))
            return false;

        // Require at least one declaration/call keyword or a concrete literal.
        if (Regex.IsMatch(code, @"\b(let|op|struct|class|enum|union|printLine|print)\b"))
            return true;
        if (Regex.IsMatch(code, "[0-9\"'`]"))
            return true;
        return false;
    }
}
