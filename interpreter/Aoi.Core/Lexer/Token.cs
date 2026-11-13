namespace Aoi.Interpreter.Lexer;

public enum TokenKind
{
    Eof,
    Identifier,
    IntLiteral,
    FloatLiteral,
    CharLiteral,
    StringLiteral,
    TemplateString,

    // Keywords
    Let, Op, If, Then, Else, Loop, While, For, In, Do, Break, Continue,
    Return, Throw, Try, Catch, Finally, When, Is, As, And, Or, Xor, Not,
    New, Set, Unset, Some, None, Struct, Class, Enum, Union, Trait, Impl,
    Of, Convert, Implicit, Explicit, Get, Literal, Type, Dyn,
    True, False,

    // Symbols
    Plus, Minus, Star, Slash, Percent, Amp, Pipe, Caret, Tilde, Bang,
    Eq, EqEq, BangEq, Lt, LtEq, Gt, GtEq, LtLt, GtGt,
    AmpAmp, PipePipe, QuestionQuestion, QuestionQuestionEq,
    PipeGt, DotDot, Arrow, FatArrow, ColonColon, Colon,
    Semi, Comma, Dot, Question, At,
    LParen, RParen, LBrace, RBrace, LBracket, RBracket,
    LtGt, LeftArrow, // <> and <-
}

public sealed class Token
{
    public TokenKind Kind { get; }
    public string Text { get; }
    public int Line { get; }
    public int Column { get; }

    public Token(TokenKind kind, string text, int line, int column)
    {
        Kind = kind;
        Text = text;
        Line = line;
        Column = column;
    }

    public override string ToString() => $"{Kind}('{Text}') @{Line}:{Column}";
}
