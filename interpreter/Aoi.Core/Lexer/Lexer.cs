namespace Aoi.Interpreter.Lexer;

public sealed class Lexer
{
    private readonly string _src;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    private static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        ["let"] = TokenKind.Let,
        ["op"] = TokenKind.Op,
        ["if"] = TokenKind.If,
        ["then"] = TokenKind.Then,
        ["else"] = TokenKind.Else,
        ["loop"] = TokenKind.Loop,
        ["while"] = TokenKind.While,
        ["for"] = TokenKind.For,
        ["in"] = TokenKind.In,
        ["do"] = TokenKind.Do,
        ["break"] = TokenKind.Break,
        ["continue"] = TokenKind.Continue,
        ["return"] = TokenKind.Return,
        ["throw"] = TokenKind.Throw,
        ["try"] = TokenKind.Try,
        ["catch"] = TokenKind.Catch,
        ["finally"] = TokenKind.Finally,
        ["when"] = TokenKind.When,
        ["is"] = TokenKind.Is,
        ["as"] = TokenKind.As,
        ["and"] = TokenKind.And,
        ["or"] = TokenKind.Or,
        ["xor"] = TokenKind.Xor,
        ["not"] = TokenKind.Not,
        ["new"] = TokenKind.New,
        ["set"] = TokenKind.Set,
        ["unset"] = TokenKind.Unset,
        ["some"] = TokenKind.Some,
        ["none"] = TokenKind.None,
        ["struct"] = TokenKind.Struct,
        ["class"] = TokenKind.Class,
        ["enum"] = TokenKind.Enum,
        ["union"] = TokenKind.Union,
        ["trait"] = TokenKind.Trait,
        ["impl"] = TokenKind.Impl,
        ["of"] = TokenKind.Of,
        ["convert"] = TokenKind.Convert,
        ["implicit"] = TokenKind.Implicit,
        ["explicit"] = TokenKind.Explicit,
        ["get"] = TokenKind.Get,
        ["literal"] = TokenKind.Literal,
        ["type"] = TokenKind.Type,
        ["dyn"] = TokenKind.Dyn,
        ["true"] = TokenKind.True,
        ["false"] = TokenKind.False,
    };

    public Lexer(string source) => _src = source;

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (IsAtEnd())
            {
                tokens.Add(new Token(TokenKind.Eof, "", _line, _col));
                break;
            }

            tokens.Add(NextToken());
        }
        return tokens;
    }

    private Token NextToken()
    {
        int startLine = _line, startCol = _col;
        char c = Advance();

        if (IsIdentStart(c))
            return IdentifierOrKeyword(c, startLine, startCol);

        if (char.IsDigit(c))
            return Number(c, startLine, startCol);

        return c switch
        {
            '"' => StringLiteral(startLine, startCol),
            '`' => TemplateString(startLine, startCol),
            '\'' => CharLiteral(startLine, startCol),
            '+' => Make(TokenKind.Plus, "+", startLine, startCol),
            '-' => Peek() == '>'
                ? MakeAdvance(TokenKind.Arrow, "->", startLine, startCol)
                : Make(TokenKind.Minus, "-", startLine, startCol),
            '*' => Make(TokenKind.Star, "*", startLine, startCol),
            '/' => Make(TokenKind.Slash, "/", startLine, startCol),
            '%' => Make(TokenKind.Percent, "%", startLine, startCol),
            '^' => Make(TokenKind.Caret, "^", startLine, startCol),
            '~' => Make(TokenKind.Tilde, "~", startLine, startCol),
            '@' => Make(TokenKind.At, "@", startLine, startCol),
            ';' => Make(TokenKind.Semi, ";", startLine, startCol),
            ',' => Make(TokenKind.Comma, ",", startLine, startCol),
            '(' => Make(TokenKind.LParen, "(", startLine, startCol),
            ')' => Make(TokenKind.RParen, ")", startLine, startCol),
            '{' => Make(TokenKind.LBrace, "{", startLine, startCol),
            '}' => Make(TokenKind.RBrace, "}", startLine, startCol),
            '[' => Make(TokenKind.LBracket, "[", startLine, startCol),
            ']' => Make(TokenKind.RBracket, "]", startLine, startCol),
            '=' => Peek() switch
            {
                '=' => MakeAdvance(TokenKind.EqEq, "==", startLine, startCol),
                '>' => MakeAdvance(TokenKind.FatArrow, "=>", startLine, startCol),
                _ => Make(TokenKind.Eq, "=", startLine, startCol),
            },
            '!' => Peek() == '='
                ? MakeAdvance(TokenKind.BangEq, "!=", startLine, startCol)
                : Make(TokenKind.Bang, "!", startLine, startCol),
            '<' => Peek() switch
            {
                '=' => MakeAdvance(TokenKind.LtEq, "<=", startLine, startCol),
                '<' => MakeAdvance(TokenKind.LtLt, "<<", startLine, startCol),
                '>' => MakeAdvance(TokenKind.LtGt, "<>", startLine, startCol),
                '-' => MakeAdvance(TokenKind.LeftArrow, "<-", startLine, startCol),
                _ => Make(TokenKind.Lt, "<", startLine, startCol),
            },
            '>' => Peek() switch
            {
                '=' => MakeAdvance(TokenKind.GtEq, ">=", startLine, startCol),
                '>' => MakeAdvance(TokenKind.GtGt, ">>", startLine, startCol),
                _ => Make(TokenKind.Gt, ">", startLine, startCol),
            },
            '&' => Peek() == '&'
                ? MakeAdvance(TokenKind.AmpAmp, "&&", startLine, startCol)
                : Make(TokenKind.Amp, "&", startLine, startCol),
            '|' => Peek() switch
            {
                '|' => MakeAdvance(TokenKind.PipePipe, "||", startLine, startCol),
                '>' => MakeAdvance(TokenKind.PipeGt, "|>", startLine, startCol),
                _ => Make(TokenKind.Pipe, "|", startLine, startCol),
            },
            '?' => Peek() == '?'
                ? (Peek(1) == '='
                    ? MakeAdvance2(TokenKind.QuestionQuestionEq, "??=", startLine, startCol)
                    : MakeAdvance(TokenKind.QuestionQuestion, "??", startLine, startCol))
                : Make(TokenKind.Question, "?", startLine, startCol),
            '.' => Peek() == '.'
                ? MakeAdvance(TokenKind.DotDot, "..", startLine, startCol)
                : Make(TokenKind.Dot, ".", startLine, startCol),
            ':' => Peek() == ':'
                ? MakeAdvance(TokenKind.ColonColon, "::", startLine, startCol)
                : Make(TokenKind.Colon, ":", startLine, startCol),
            _ when IsOperatorPunct(c) => Make(TokenKind.Identifier, c.ToString(), startLine, startCol),
            _ => throw new AoiException($"Unexpected character '{c}'", startLine, startCol),
        };
    }

    private Token IdentifierOrKeyword(char first, int line, int col)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(first);
        while (!IsAtEnd() && IsIdentPart(Peek()))
            sb.Append(Advance());
        var text = sb.ToString();
        if (Keywords.TryGetValue(text, out var kind))
            return new Token(kind, text, line, col);
        return new Token(TokenKind.Identifier, text, line, col);
    }

    private Token Number(char first, int line, int col)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(first);

        // hex / bin / oct
        if (first == '0' && !IsAtEnd())
        {
            char p = Peek();
            if (p is 'x' or 'X' or 'b' or 'B' or 'o' or 'O')
            {
                sb.Append(Advance());
                while (!IsAtEnd() && IsHexDigit(Peek()))
                    sb.Append(Advance());
                return new Token(TokenKind.IntLiteral, sb.ToString(), line, col);
            }
        }

        while (!IsAtEnd() && char.IsDigit(Peek()))
            sb.Append(Advance());

        if (!IsAtEnd() && Peek() == '.' && Peek(1) != '.' && char.IsDigit(Peek(1)))
        {
            sb.Append(Advance()); // .
            while (!IsAtEnd() && char.IsDigit(Peek()))
                sb.Append(Advance());
            return new Token(TokenKind.FloatLiteral, sb.ToString(), line, col);
        }

        return new Token(TokenKind.IntLiteral, sb.ToString(), line, col);
    }

    private Token StringLiteral(int line, int col)
    {
        var sb = new System.Text.StringBuilder();
        while (!IsAtEnd() && Peek() != '"')
        {
            if (Peek() == '\\')
            {
                Advance();
                sb.Append(ParseEscape());
            }
            else
            {
                sb.Append(Advance());
            }
        }
        if (IsAtEnd()) throw new AoiException("Unterminated string", line, col);
        Advance(); // closing "
        return new Token(TokenKind.StringLiteral, sb.ToString(), line, col);
    }

    private Token TemplateString(int line, int col)
    {
        var sb = new System.Text.StringBuilder();
        while (!IsAtEnd() && Peek() != '`')
        {
            if (Peek() == '\\')
            {
                Advance();
                sb.Append(ParseEscape());
            }
            else
            {
                sb.Append(Advance());
            }
        }
        if (IsAtEnd()) throw new AoiException("Unterminated template string", line, col);
        Advance();
        return new Token(TokenKind.TemplateString, sb.ToString(), line, col);
    }

    private Token CharLiteral(int line, int col)
    {
        if (IsAtEnd()) throw new AoiException("Unterminated char", line, col);
        char ch;
        if (Peek() == '\\')
        {
            Advance();
            ch = ParseEscape();
        }
        else
        {
            ch = Advance();
        }
        if (IsAtEnd() || Advance() != '\'')
            throw new AoiException("Unterminated char literal", line, col);
        return new Token(TokenKind.CharLiteral, ch.ToString(), line, col);
    }

    private char ParseEscape()
    {
        if (IsAtEnd()) throw new AoiException("Incomplete escape", _line, _col);
        char c = Advance();
        return c switch
        {
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            '\\' => '\\',
            '"' => '"',
            '\'' => '\'',
            '`' => '`',
            '0' => '\0',
            _ => c,
        };
    }

    private void SkipWhitespaceAndComments()
    {
        while (!IsAtEnd())
        {
            char c = Peek();
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                Advance();
                continue;
            }
            if (c == '/' && Peek(1) == '/')
            {
                while (!IsAtEnd() && Peek() != '\n') Advance();
                continue;
            }
            if (c == '/' && Peek(1) == '*')
            {
                Advance(); Advance();
                while (!IsAtEnd() && !(Peek() == '*' && Peek(1) == '/'))
                    Advance();
                if (!IsAtEnd()) { Advance(); Advance(); }
                continue;
            }
            break;
        }
    }

    private Token Make(TokenKind kind, string text, int line, int col)
        => new(kind, text, line, col);

    private Token MakeAdvance(TokenKind kind, string text, int line, int col)
    {
        Advance();
        return new Token(kind, text, line, col);
    }

    private Token MakeAdvance2(TokenKind kind, string text, int line, int col)
    {
        Advance(); Advance();
        return new Token(kind, text, line, col);
    }

    private bool IsAtEnd() => _pos >= _src.Length;
    private char Peek(int offset = 0)
        => _pos + offset >= _src.Length ? '\0' : _src[_pos + offset];

    private char Advance()
    {
        char c = _src[_pos++];
        if (c == '\n') { _line++; _col = 1; }
        else _col++;
        return c;
    }

    private static bool IsIdentStart(char c)
        => char.IsLetter(c) || c == '_' || (c > 127 && IsOperatorPunct(c));

    private static bool IsIdentPart(char c)
        => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsHexDigit(char c)
        => char.IsDigit(c) || c is >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static bool IsOperatorPunct(char c)
    {
        // Non-ASCII punctuation/symbols may be custom operators.
        // ASCII punctuation is handled by dedicated token rules.
        if (c <= 127) return false;
        var cat = char.GetUnicodeCategory(c);
        return cat is System.Globalization.UnicodeCategory.OtherPunctuation
            or System.Globalization.UnicodeCategory.MathSymbol
            or System.Globalization.UnicodeCategory.CurrencySymbol
            or System.Globalization.UnicodeCategory.ModifierSymbol;
    }
}
