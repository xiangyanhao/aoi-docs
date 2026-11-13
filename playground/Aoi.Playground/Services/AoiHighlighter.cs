using System.Net;
using System.Text;

namespace Aoi.Playground.Services;

public static class AoiHighlighter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "let", "op", "if", "then", "else", "loop", "while", "for", "in", "do",
        "break", "continue", "return", "throw", "try", "catch", "finally",
        "when", "is", "as", "and", "or", "xor", "not", "new", "set", "unset",
        "some", "none", "struct", "class", "enum", "union", "trait", "impl",
        "of", "convert", "implicit", "explicit", "get", "literal", "type", "dyn",
        "true", "false",
    };

    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "int", "float", "bool", "char", "str", "arr", "Range", "Span", "never",
    };

    public static string ToHtml(string source)
    {
        if (string.IsNullOrEmpty(source))
            return "\n";

        var sb = new StringBuilder(source.Length * 2);
        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                int start = i;
                i += 2;
                while (i < source.Length && source[i] != '\n') i++;
                AppendSpan(sb, "tok-comment", source[start..i]);
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                int start = i;
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                if (i + 1 < source.Length) i += 2;
                AppendSpan(sb, "tok-comment", source[start..Math.Min(i, source.Length)]);
                continue;
            }

            if (c == '"')
            {
                int start = i++;
                while (i < source.Length)
                {
                    if (source[i] == '\\' && i + 1 < source.Length) { i += 2; continue; }
                    if (source[i] == '"') { i++; break; }
                    i++;
                }
                AppendSpan(sb, "tok-string", source[start..i]);
                continue;
            }

            if (c == '`')
            {
                int start = i++;
                while (i < source.Length)
                {
                    if (source[i] == '\\' && i + 1 < source.Length) { i += 2; continue; }
                    if (source[i] == '`') { i++; break; }
                    i++;
                }
                AppendSpan(sb, "tok-string", source[start..i]);
                continue;
            }

            if (c == '\'')
            {
                int start = i++;
                if (i < source.Length && source[i] == '\\' && i + 1 < source.Length) i += 2;
                else if (i < source.Length) i++;
                if (i < source.Length && source[i] == '\'') i++;
                AppendSpan(sb, "tok-char", source[start..i]);
                continue;
            }

            if (char.IsDigit(c))
            {
                int start = i++;
                if (c == '0' && i < source.Length && source[i] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O')
                {
                    i++;
                    while (i < source.Length && IsHex(source[i])) i++;
                }
                else
                {
                    while (i < source.Length && char.IsDigit(source[i])) i++;
                    if (i < source.Length && source[i] == '.' && i + 1 < source.Length
                        && source[i + 1] != '.' && char.IsDigit(source[i + 1]))
                    {
                        i++;
                        while (i < source.Length && char.IsDigit(source[i])) i++;
                    }
                }
                AppendSpan(sb, "tok-number", source[start..i]);
                continue;
            }

            if (IsIdentStart(c))
            {
                int start = i++;
                while (i < source.Length && IsIdentPart(source[i])) i++;
                var word = source[start..i];
                string cls = Keywords.Contains(word) ? "tok-keyword"
                    : Types.Contains(word) ? "tok-type"
                    : "tok-ident";
                AppendSpan(sb, cls, word);
                continue;
            }

            if (TryMatchOp(source, i, out int opLen))
            {
                AppendSpan(sb, "tok-operator", source[i..(i + opLen)]);
                i += opLen;
                continue;
            }

            if (c is '(' or ')' or '{' or '}' or '[' or ']' or ',' or ';' or '.')
            {
                AppendSpan(sb, "tok-punct", c.ToString());
                i++;
                continue;
            }

            if (c is '\n' or '\r' or '\t' or ' ')
            {
                sb.Append(c == ' ' ? ' ' : c);
                i++;
                continue;
            }

            AppendSpan(sb, "tok-plain", c.ToString());
            i++;
        }

        // Trailing newline keeps overlay height aligned with textarea.
        if (!source.EndsWith('\n'))
            sb.Append('\n');
        return sb.ToString();
    }

    private static void AppendSpan(StringBuilder sb, string css, string text)
    {
        sb.Append("<span class=\"").Append(css).Append("\">")
          .Append(WebUtility.HtmlEncode(text))
          .Append("</span>");
    }

    private static bool TryMatchOp(string src, int i, out int len)
    {
        // Longest-first multi-char operators
        ReadOnlySpan<string> ops =
        [
            "??=", "||", "&&", "==", "!=", "<=", ">=", "<<", ">>",
            "??", "|>", "->", "=>", "::", "..", "<>", "<-",
        ];
        foreach (var op in ops)
        {
            if (i + op.Length <= src.Length && src.AsSpan(i, op.Length).SequenceEqual(op))
            {
                len = op.Length;
                return true;
            }
        }

        if ("+-*/%<>!&|^~?:@=".Contains(src[i]))
        {
            len = 1;
            return true;
        }

        len = 0;
        return false;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';
    private static bool IsHex(char c) => char.IsDigit(c) || c is >= 'a' and <= 'f' or >= 'A' and <= 'F';
}
