using Aoi.Interpreter.Ast;
using Aoi.Interpreter.Lexer;

namespace Aoi.Interpreter.Parser;

public sealed class Parser
{
    private readonly List<Token> _tokens;
    private int _pos;

    private static readonly HashSet<string> BinaryOps = new()
    {
        "+", "-", "*", "/", "%", "<<", ">>", "<", "<=", ">", ">=", "==", "!=",
        "&", "^", "|", "&&", "||", "??", "|>", "..", "@",
    };

    private static readonly Dictionary<string, int> Precedence = new()
    {
        ["|>"] = 1,
        ["??"] = 2,
        ["||"] = 3,
        ["&&"] = 4,
        ["|"] = 5,
        ["^"] = 6,
        ["&"] = 7,
        ["<"] = 8, ["<="] = 8, [">"] = 8, [">="] = 8, ["=="] = 8, ["!="] = 8,
        ["<<"] = 9, [">>"] = 9,
        ["+"] = 10, ["-"] = 10,
        ["*"] = 11, ["/"] = 11, ["%"] = 11, ["@"] = 11,
        [".."] = 12,
    };

    public Parser(List<Token> tokens) => _tokens = tokens;

    public ProgramNode ParseProgram()
    {
        var program = new ProgramNode();
        while (!Check(TokenKind.Eof))
            program.Statements.Add(ParseDeclarationOrStatement());
        return program;
    }

    private Stmt ParseDeclarationOrStatement()
    {
        if (Match(TokenKind.Struct) || Match(TokenKind.Class))
        {
            bool isClass = Previous().Kind == TokenKind.Class;
            return ParseStructOrClass(isClass);
        }
        if (Match(TokenKind.Enum)) return ParseEnum();
        if (Match(TokenKind.Union)) return ParseUnion();
        if (Match(TokenKind.Op)) return ParseOpDecl();
        if (Match(TokenKind.Trait)) return ParseTrait();
        if (Match(TokenKind.Impl)) return ParseImpl();
        if (Match(TokenKind.Type)) return ParseTypeAlias();
        if (Match(TokenKind.Convert)) return ParseConvert();
        if (Match(TokenKind.Get)) return ParseGet();
        if (Check(TokenKind.Set) && LookAheadIsDeclSet()) return ParseSetDecl();
        if (Match(TokenKind.Literal)) return ParseLiteralSuffix();
        return ParseStatement();
    }

    private bool LookAheadIsDeclSet()
    {
        // Decl: set self.prop = … / set self.[i] = …
        // Expr: set instance { … }  — brace without a preceding '.' is an expression.
        if (!Check(TokenKind.Set)) return false;
        int i = _pos + 1;
        int depth = 0;
        while (i < _tokens.Count)
        {
            var k = _tokens[i].Kind;
            if (k == TokenKind.Eof || k == TokenKind.Semi) return false;
            if (depth == 0 && k == TokenKind.LBrace) return false;
            if (depth == 0 && k == TokenKind.Dot) return true;
            if (k is TokenKind.LParen or TokenKind.LBrace or TokenKind.LBracket) depth++;
            else if (k is TokenKind.RParen or TokenKind.RBrace or TokenKind.RBracket) depth--;
            i++;
        }
        return false;
    }

    private Stmt ParseStatement()
    {
        if (Match(TokenKind.Let)) return ParseLet();
        if (Match(TokenKind.If)) return ParseIfStmt();
        if (Match(TokenKind.Loop)) return ParseLoop();
        if (Match(TokenKind.While)) return ParseWhile();
        if (Match(TokenKind.For)) return ParseFor();
        if (Match(TokenKind.When)) return ParseWhenStmt();
        if (Match(TokenKind.Try)) return ParseTryStmt();
        if (Check(TokenKind.LBrace)) return ParseBlockStmtOrExpr();

        var expr = ParseExpression();
        Consume(TokenKind.Semi, "Expected ';' after expression");
        return Loc(new ExprStmt { Expression = expr }, expr);
    }

    /// <summary>
    /// Top-level `{ ... }` may be a statement block or an expression block
    /// (trailing expression without `;`), matching the language docs.
    /// </summary>
    private Stmt ParseBlockStmtOrExpr()
    {
        var tok = Consume(TokenKind.LBrace, "Expected '{'");
        var stmts = new List<Stmt>();
        Expr? result = null;
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            if (Check(TokenKind.Let) || Check(TokenKind.If) || Check(TokenKind.Loop)
                || Check(TokenKind.While) || Check(TokenKind.For) || Check(TokenKind.When)
                || Check(TokenKind.Try) || Check(TokenKind.LBrace))
            {
                stmts.Add(ParseStatement());
                continue;
            }

            var e = ParseExpression();
            if (Match(TokenKind.Semi))
                stmts.Add(new ExprStmt { Expression = e });
            else
            {
                result = e;
                break;
            }
        }
        Consume(TokenKind.RBrace, "Expected '}'");

        if (result != null)
        {
            var be = new BlockExpr { Result = result };
            be.Statements.AddRange(stmts);
            return Loc(new ExprStmt { Expression = Loc(be, tok) }, tok);
        }

        var block = new BlockStmt();
        block.Statements.AddRange(stmts);
        return Loc(block, tok);
    }

    private LetStmt ParseLet()
    {
        var tok = Previous();
        var pattern = ParseDeclPattern();
        TypeRef? type = null;
        if (Match(TokenKind.Colon))
            type = ParseType();
        Consume(TokenKind.Eq, "Expected '=' in let");
        var value = ParseExpression();
        // Allow omitting `;` when the initializer ends with `}` (common after `new` / blocks).
        if (!Match(TokenKind.Semi) && Previous().Kind != TokenKind.RBrace)
            Consume(TokenKind.Semi, "Expected ';' after let");
        return Loc(new LetStmt { Pattern = pattern, Type = type, Value = value }, tok);
    }

    private BlockStmt ParseBlockStmt()
    {
        // Used where only a statement is allowed (if/loop bodies, etc.).
        var stmt = ParseBlockStmtOrExpr();
        if (stmt is BlockStmt block) return block;
        // Expression block used as a statement: evaluate and discard.
        if (stmt is ExprStmt es)
        {
            var b = new BlockStmt();
            b.Statements.Add(es);
            return Loc(b, es);
        }
        throw new AoiException("Expected block statement", stmt.Line, stmt.Column);
    }

    private IfStmt ParseIfStmt()
    {
        var tok = Previous();
        // if value is pattern then ...  OR  if condition then ...
        // Use ParseBinary so trailing `is` is not consumed as IsExpr.
        var first = ParseBinary();
        if (Match(TokenKind.Is))
        {
            var pattern = ParseMatchPattern();
            Consume(TokenKind.Then, "Expected 'then'");
            var thenB = ParseStatement();
            Stmt? elseB = null;
            if (Match(TokenKind.Else)) elseB = ParseStatement();
            return Loc(new IfStmt { MatchValue = first, MatchPattern = pattern, ThenBranch = thenB, ElseBranch = elseB }, tok);
        }
        Consume(TokenKind.Then, "Expected 'then'");
        var thenBranch = ParseStatement();
        Stmt? elseBranch = null;
        if (Match(TokenKind.Else)) elseBranch = ParseStatement();
        return Loc(new IfStmt { Condition = first, ThenBranch = thenBranch, ElseBranch = elseBranch }, tok);
    }

    private LoopStmt ParseLoop()
    {
        var tok = Previous();
        var body = ParseStatement();
        return Loc(new LoopStmt { Body = body }, tok);
    }

    private WhileStmt ParseWhile()
    {
        var tok = Previous();
        var cond = ParseExpression();
        Consume(TokenKind.Do, "Expected 'do'");
        var body = ParseStatement();
        return Loc(new WhileStmt { Condition = cond, Body = body }, tok);
    }

    private ForStmt ParseFor()
    {
        var tok = Previous();
        var pattern = ParseDeclPattern();
        Consume(TokenKind.In, "Expected 'in'");
        var iter = ParseExpression();
        Consume(TokenKind.Do, "Expected 'do'");
        var body = ParseStatement();
        return Loc(new ForStmt { Pattern = pattern, Iterable = iter, Body = body }, tok);
    }

    private WhenStmt ParseWhenStmt()
    {
        var tok = Previous();
        var value = ParseExpression();
        Consume(TokenKind.LBrace, "Expected '{'");
        var stmt = new WhenStmt { Value = value };
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            if (Match(TokenKind.Is))
            {
                var pattern = ParseMatchPattern();
                Consume(TokenKind.Then, "Expected 'then'");
                var branch = ParseStatement();
                stmt.Arms.Add((pattern, branch));
            }
            else if (Match(TokenKind.Else))
            {
                stmt.ElseBranch = ParseStatement();
                break;
            }
            else break;
        }
        Consume(TokenKind.RBrace, "Expected '}'");
        return Loc(stmt, tok);
    }

    private TryStmt ParseTryStmt()
    {
        var tok = Previous();
        var body = ParseStatement();
        var stmt = new TryStmt { Body = body };
        while (Match(TokenKind.Catch))
        {
            var pattern = ParseMatchPattern();
            Consume(TokenKind.Then, "Expected 'then'");
            var branch = ParseStatement();
            stmt.Catches.Add((pattern, branch));
        }
        if (Match(TokenKind.Finally))
            stmt.Finally = ParseStatement();
        return Loc(stmt, tok);
    }

    private StructDecl ParseStructOrClass(bool isClass)
    {
        var name = Consume(TokenKind.Identifier, "Expected type name").Text;
        var decl = new StructDecl { Name = name, IsClass = isClass };
        if (Match(TokenKind.LBracket))
        {
            do
            {
                if (Check(TokenKind.RBracket)) break;
                decl.TypeParams.Add(Consume(TokenKind.Identifier, "Expected type param").Text);
                // skip : requirements for now
                if (Match(TokenKind.Colon))
                {
                    while (!Check(TokenKind.Comma) && !Check(TokenKind.RBracket) && !Check(TokenKind.Eof))
                        Advance();
                }
            } while (Match(TokenKind.Comma));
            Consume(TokenKind.RBracket, "Expected ']'");
        }
        Consume(TokenKind.LBrace, "Expected '{'");
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            var field = Consume(TokenKind.Identifier, "Expected field name").Text;
            Consume(TokenKind.Colon, "Expected ':'");
            var type = ParseType();
            Match(TokenKind.Semi);
            decl.Fields.Add((field, type));
        }
        Consume(TokenKind.RBrace, "Expected '}'");
        return decl;
    }

    private EnumDecl ParseEnum()
    {
        var name = Consume(TokenKind.Identifier, "Expected enum name").Text;
        var decl = new EnumDecl { Name = name };
        Consume(TokenKind.LBrace, "Expected '{'");
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            var member = Consume(TokenKind.Identifier, "Expected member").Text;
            long? value = null;
            if (Match(TokenKind.Eq))
                value = ParseIntLiteral(Consume(TokenKind.IntLiteral, "Expected int"));
            Match(TokenKind.Semi);
            decl.Members.Add((member, value));
        }
        Consume(TokenKind.RBrace, "Expected '}'");
        return decl;
    }

    private UnionDecl ParseUnion()
    {
        var name = Consume(TokenKind.Identifier, "Expected union name").Text;
        var decl = new UnionDecl { Name = name };
        if (Match(TokenKind.LBracket))
        {
            do
            {
                if (Check(TokenKind.RBracket)) break;
                decl.TypeParams.Add(Consume(TokenKind.Identifier, "Expected type param").Text);
            } while (Match(TokenKind.Comma));
            Consume(TokenKind.RBracket, "Expected ']'");
        }
        Consume(TokenKind.LBrace, "Expected '{'");
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            var caseName = Consume(TokenKind.Identifier, "Expected case").Text;
            Consume(TokenKind.Colon, "Expected ':'");
            var type = ParseType();
            Match(TokenKind.Semi);
            decl.Cases.Add((caseName, type));
        }
        Consume(TokenKind.RBrace, "Expected '}'");
        return decl;
    }

    private OpDecl ParseOpDecl()
    {
        // Forms:
        // op name rhs: Type -> Result { }
        // op lhs name rhs: L <> R -> Result { }
        // Distinguish by looking ahead for '<>' before the body.
        if (HasDiamondBeforeBody())
            return ParseBinaryOpDecl();
        return ParseUnaryOpDecl();
    }

    private bool HasDiamondBeforeBody()
    {
        int depth = 0;
        for (int i = _pos; i < _tokens.Count; i++)
        {
            var k = _tokens[i].Kind;
            if (k == TokenKind.LBrace && depth == 0) return false;
            if (k == TokenKind.Eof) return false;
            if (k == TokenKind.LtGt) return true;
            if (k is TokenKind.LParen or TokenKind.LBracket or TokenKind.LBrace) depth++;
            if (k is TokenKind.RParen or TokenKind.RBracket or TokenKind.RBrace) depth--;
        }
        return false;
    }

    private OpDecl ParseUnaryOpDecl()
    {
        var name = ConsumeOpName();
        // optional Trait:: prefix already consumed by caller when needed
        var tparams = ParseOptionalTypeParams();
        var rhs = ParseDeclPattern();
        TypeRef? rType = null, resType = null;
        if (Match(TokenKind.Colon))
        {
            rType = ParseType();
            Consume(TokenKind.Arrow, "Expected '->'");
            resType = ParseType();
        }
        var body = ParseBlockExprOrStmt();
        return new OpDecl
        {
            Name = name,
            Rhs = rhs,
            TypeParams = tparams,
            RhsType = rType,
            ResultType = resType,
            Body = body,
        };
    }

    private OpDecl ParseBinaryOpDecl()
    {
        var lhs = ParseDeclPattern();
        var name = ConsumeOpName();
        var typeParams = ParseOptionalTypeParams();
        var rhs = ParseDeclPattern();
        TypeRef? lhsType = null, rhsType = null, resultType = null;
        if (Match(TokenKind.Colon))
        {
            lhsType = ParseType();
            Consume(TokenKind.LtGt, "Expected '<>'");
            rhsType = ParseType();
            Consume(TokenKind.Arrow, "Expected '->'");
            resultType = ParseType();
        }
        var body = ParseBlockExprOrStmt();
        return new OpDecl
        {
            Lhs = lhs,
            Name = name,
            Rhs = rhs,
            TypeParams = typeParams,
            LhsType = lhsType,
            RhsType = rhsType,
            ResultType = resultType,
            Body = body,
        };
    }

    private TraitDecl ParseTrait()
    {
        var name = Consume(TokenKind.Identifier, "Expected trait name").Text;
        var decl = new TraitDecl { Name = name };
        if (Match(TokenKind.LBracket))
        {
            do
            {
                if (Check(TokenKind.RBracket)) break;
                decl.TypeParams.Add(Consume(TokenKind.Identifier, "Expected type param").Text);
                if (Match(TokenKind.Colon))
                {
                    while (!Check(TokenKind.Comma) && !Check(TokenKind.RBracket) && !Check(TokenKind.Eof))
                        Advance();
                }
            } while (Match(TokenKind.Comma));
            Consume(TokenKind.RBracket, "Expected ']'");
        }
        Consume(TokenKind.LBrace, "Expected '{'");
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            Consume(TokenKind.Op, "Expected 'op'");
            // Parse op header ending with ; or body
            var saved = _pos;
            var op = ParseOpDeclLoose();
            decl.Members.Add(op);
        }
        Consume(TokenKind.RBrace, "Expected '}'");
        return decl;
    }

    private OpDecl ParseOpDeclLoose()
    {
        // Like ParseOpDecl but body may be ';'
        if (HasDiamondBeforeBody())
        {
            var lhs = ParseDeclPattern();
            var name = ConsumeOpName();
            var tparams = ParseOptionalTypeParams();
            var rhs = ParseDeclPattern();
            TypeRef? lhsType = null, rhsType = null, resultType = null;
            if (Match(TokenKind.Colon))
            {
                lhsType = ParseType();
                if (Match(TokenKind.LtGt))
                    rhsType = ParseType();
                Consume(TokenKind.Arrow, "Expected '->'");
                resultType = ParseType();
            }
            Expr body = Match(TokenKind.Semi) ? new UnitExpr() : ParseBlockExprOrStmt();
            return new OpDecl
            {
                Lhs = lhs, Name = name, Rhs = rhs,
                TypeParams = tparams, LhsType = lhsType, RhsType = rhsType,
                ResultType = resultType, Body = body,
            };
        }

        var uname = ConsumeOpName();
        var tp = ParseOptionalTypeParams();
        var urhs = ParseDeclPattern();
        TypeRef? rType = null, resType = null;
        if (Match(TokenKind.Colon))
        {
            rType = ParseType();
            Consume(TokenKind.Arrow, "Expected '->'");
            resType = ParseType();
        }
        Expr ubody = Match(TokenKind.Semi) ? new UnitExpr() : ParseBlockExprOrStmt();
        return new OpDecl { Name = uname, Rhs = urhs, TypeParams = tp, RhsType = rType, ResultType = resType, Body = ubody };
    }

    private ImplDecl ParseImpl()
    {
        var decl = new ImplDecl();
        List<string> tparams = new();
        if (Match(TokenKind.LBracket))
        {
            do
            {
                if (Check(TokenKind.RBracket)) break;
                tparams.Add(Consume(TokenKind.Identifier, "Expected type param").Text);
                if (Match(TokenKind.Colon))
                {
                    while (!Check(TokenKind.Comma) && !Check(TokenKind.RBracket) && !Check(TokenKind.Eof))
                        Advance();
                }
            } while (Match(TokenKind.Comma));
            Consume(TokenKind.RBracket, "Expected ']'");
        }
        decl.TypeParams.AddRange(tparams);

        if (Match(TokenKind.Of))
        {
            decl.TraitName = Consume(TokenKind.Identifier, "Expected trait").Text;
            if (Match(TokenKind.LBracket))
            {
                do
                {
                    if (Check(TokenKind.RBracket)) break;
                    decl.TraitArgs.Add(ParseType());
                } while (Match(TokenKind.Comma));
                Consume(TokenKind.RBracket, "Expected ']'");
            }
        }
        else if (Check(TokenKind.Identifier))
        {
            decl.Name = Advance().Text;
            if (Match(TokenKind.Of))
            {
                decl.TraitName = Consume(TokenKind.Identifier, "Expected trait").Text;
                if (Match(TokenKind.LBracket))
                {
                    do
                    {
                        if (Check(TokenKind.RBracket)) break;
                        decl.TraitArgs.Add(ParseType());
                    } while (Match(TokenKind.Comma));
                    Consume(TokenKind.RBracket, "Expected ']'");
                }
            }
        }

        if (Match(TokenKind.Eq))
        {
            decl.AliasOf = Consume(TokenKind.Identifier, "Expected impl name").Text;
            Consume(TokenKind.Semi, "Expected ';'");
            return decl;
        }

        Consume(TokenKind.LBrace, "Expected '{'");
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            Consume(TokenKind.Op, "Expected 'op'");
            // Skip optional Trait:: prefix
            if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.ColonColon)
            {
                Advance(); Advance();
            }
            decl.Members.Add(ParseOpDeclLoose());
        }
        Consume(TokenKind.RBrace, "Expected '}'");
        return decl;
    }

    private TypeAliasDecl ParseTypeAlias()
    {
        var name = Consume(TokenKind.Identifier, "Expected type name").Text;
        var decl = new TypeAliasDecl { Name = name, Definition = new NamedTypeRef { Name = "()" } };
        if (Match(TokenKind.LBracket))
        {
            do
            {
                if (Check(TokenKind.RBracket)) break;
                decl.TypeParams.Add(Consume(TokenKind.Identifier, "Expected type param").Text);
            } while (Match(TokenKind.Comma));
            Consume(TokenKind.RBracket, "Expected ']'");
        }
        Consume(TokenKind.Eq, "Expected '='");
        decl.Definition = ParseType();
        Match(TokenKind.Semi);
        return decl;
    }

    private ConvertDecl ParseConvert()
    {
        bool implicit_ = Match(TokenKind.Implicit);
        if (!implicit_) Consume(TokenKind.Explicit, "Expected implicit/explicit");
        var pattern = ParseDeclPattern();
        Consume(TokenKind.Colon, "Expected ':'");
        var from = ParseType();
        Consume(TokenKind.Arrow, "Expected '->'");
        var to = ParseType();
        var body = ParseBlockExprOrStmt();
        return new ConvertDecl { Implicit = implicit_, Pattern = pattern, FromType = from, ToType = to, Body = body };
    }

    private GetDecl ParseGet()
    {
        var self = ParseDeclPattern();
        Consume(TokenKind.Dot, "Expected '.'");
        string? prop = null;
        DeclPattern? index = null;
        if (Match(TokenKind.LBracket))
        {
            index = ParseDeclPattern();
            Consume(TokenKind.RBracket, "Expected ']'");
        }
        else
        {
            prop = Consume(TokenKind.Identifier, "Expected property").Text;
        }
        TypeRef? selfType = null, indexType = null, resultType = null;
        if (Match(TokenKind.Colon))
        {
            selfType = ParseType();
            if (index != null)
            {
                Consume(TokenKind.LtGt, "Expected '<>'");
                indexType = ParseType();
            }
            Consume(TokenKind.Arrow, "Expected '->'");
            resultType = ParseType();
        }
        var body = ParseBlockExprOrStmt();
        return new GetDecl
        {
            Self = self, PropertyName = prop, Index = index,
            SelfType = selfType, IndexType = indexType, ResultType = resultType, Body = body,
        };
    }

    private SetDecl ParseSetDecl()
    {
        Consume(TokenKind.Set, "Expected 'set'");
        var self = ParseDeclPattern();
        Consume(TokenKind.Dot, "Expected '.'");
        string? prop = null;
        DeclPattern? index = null;
        if (Match(TokenKind.LBracket))
        {
            index = ParseDeclPattern();
            Consume(TokenKind.RBracket, "Expected ']'");
        }
        else
        {
            prop = Consume(TokenKind.Identifier, "Expected property").Text;
        }
        Consume(TokenKind.Eq, "Expected '='");
        var value = ParseDeclPattern();
        TypeRef? selfType = null, indexType = null, valueType = null;
        if (Match(TokenKind.Colon))
        {
            selfType = ParseType();
            if (index != null)
            {
                Consume(TokenKind.LtGt, "Expected '<>'");
                indexType = ParseType();
            }
            Consume(TokenKind.LeftArrow, "Expected '<-'");
            valueType = ParseType();
        }
        var body = ParseBlockExprOrStmt();
        return new SetDecl
        {
            Self = self, PropertyName = prop, Index = index, Value = value,
            SelfType = selfType, IndexType = indexType, ValueType = valueType, Body = body,
        };
    }

    private LiteralSuffixDecl ParseLiteralSuffix()
    {
        var pattern = ParseDeclPattern();
        var suffix = Consume(TokenKind.Identifier, "Expected suffix").Text;
        Consume(TokenKind.Colon, "Expected ':'");
        var vt = ParseType();
        Consume(TokenKind.Arrow, "Expected '->'");
        var rt = ParseType();
        var body = ParseBlockExprOrStmt();
        return new LiteralSuffixDecl { Pattern = pattern, Suffix = suffix, ValueType = vt, ResultType = rt, Body = body };
    }

    private Expr ParseBlockExprOrStmt()
    {
        var tok = Consume(TokenKind.LBrace, "Expected '{'");
        var stmts = new List<Stmt>();
        Expr? result = null;
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            // Pure statements that cannot be the block result expression.
            if (Check(TokenKind.Let) || Check(TokenKind.Loop) || Check(TokenKind.While) || Check(TokenKind.For))
            {
                stmts.Add(ParseStatement());
                continue;
            }

            // Prefer expressions so `if`/`when`/`try` can be the result value.
            var expr = ParseExpression();
            if (Match(TokenKind.Semi))
            {
                stmts.Add(new ExprStmt { Expression = expr });
            }
            else
            {
                result = expr;
                break;
            }
        }
        Consume(TokenKind.RBrace, "Expected '}'");
        if (result != null)
        {
            var be = new BlockExpr { Result = result };
            be.Statements.AddRange(stmts);
            return Loc(be, tok);
        }
        // statement block used as ()-returning body
        var block = new BlockExpr { Result = new UnitExpr() };
        block.Statements.AddRange(stmts);
        return Loc(block, tok);
    }

    // --- Expressions ---

    private Expr ParseExpression() => ParseAssignment();

    private Expr ParseAssignment()
    {
        var expr = ParseIsExpr();

        // Infix set: instance set { field: value, ... }
        if (Check(TokenKind.Set) && Peek(1).Kind == TokenKind.LBrace)
        {
            Advance(); // set
            return ParseSetExprBody(expr);
        }

        if (Match(TokenKind.Eq))
        {
            var value = ParseAssignment();
            return new AssignExpr { Pattern = ExprToAssignPattern(expr), Value = value };
        }
        if (Match(TokenKind.QuestionQuestionEq))
        {
            var value = ParseAssignment();
            return new CompoundAssignExpr { Target = expr, Op = "??=", Value = value };
        }
        // compound += -= etc.
        if (Check(TokenKind.Plus) || Check(TokenKind.Minus) || Check(TokenKind.Star)
            || Check(TokenKind.Slash) || Check(TokenKind.Percent))
        {
            if (Peek(1).Kind == TokenKind.Eq)
            {
                var op = Advance().Text;
                Advance(); // =
                var value = ParseAssignment();
                return new CompoundAssignExpr { Target = expr, Op = op, Value = value };
            }
        }
        return expr;
    }

    private static AssignPattern ExprToAssignPattern(Expr expr) => expr switch
    {
        TupleExpr t => new TupleAssignPattern
        {
            Elements = t.Elements.Select(e => (AssignPattern?)ExprToAssignPattern(e)).ToList(),
            Line = t.Line,
            Column = t.Column,
        },
        _ => new ExprAssignPattern { Target = expr, Line = expr.Line, Column = expr.Column },
    };

    private Expr ParseSetExprBody(Expr target)
    {
        Consume(TokenKind.LBrace, "Expected '{'");
        var elements = ParseInitializerElements();
        Consume(TokenKind.RBrace, "Expected '}'");
        return new SetExpr
        {
            Target = target,
            Elements = elements,
            Line = target.Line,
            Column = target.Column,
        };
    }

    private List<(string? Name, Expr Value, Expr? Repeat, Expr? Index)> ParseInitializerElements()
    {
        var elements = new List<(string? Name, Expr Value, Expr? Repeat, Expr? Index)>();
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            string? fieldName = null;
            Expr? index = null;
            if (Match(TokenKind.LBracket))
            {
                index = ParseExpression();
                Consume(TokenKind.RBracket, "Expected ']'");
                Consume(TokenKind.Colon, "Expected ':'");
            }
            else if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Colon)
            {
                fieldName = Advance().Text;
                Advance(); // :
            }
            var value = ParseExpression();
            Expr? repeat = null;
            if (Match(TokenKind.Semi) && !Check(TokenKind.RBrace) && !Check(TokenKind.Comma))
                repeat = ParseExpression();
            elements.Add((fieldName, value, repeat, index));
            Match(TokenKind.Comma);
        }
        return elements;
    }

    private Expr ParseIsExpr()
    {
        var expr = ParseBinary();
        if (Match(TokenKind.Is))
        {
            var pattern = ParseMatchPattern();
            return new IsExpr { Value = expr, Pattern = pattern, Line = expr.Line };
        }
        return expr;
    }

    private Expr ParseBinary(int minPrec = 1)
    {
        var left = ParseUnary();

        // Juxtaposition: collect primary-like sequence then fold
        // After unary, if next is a primary start (not binary op, not then/do/etc.), collect
        var parts = new List<Expr> { left };
        while (CanStartJuxtaposed())
        {
            // If a `new`/`block` just closed with `}` and the next tokens look like a new
            // statement (`ident.` / `ident =`), stop — a missing ';' must not swallow it.
            if (Previous().Kind == TokenKind.RBrace
                && Check(TokenKind.Identifier)
                && Peek(1).Kind is TokenKind.Dot or TokenKind.Eq or TokenKind.LBrace)
                break;
            parts.Add(ParseUnary());
        }

        Expr result;
        if (parts.Count == 1)
            result = parts[0];
        else
        {
            // Keep as CallLikeExpr; runtime decides unary nesting vs binary ops.
            // (e.g. `printLine fac 10` vs `list insert a`)
            var call = new CallLikeExpr { Line = parts[0].Line };
            call.Parts.AddRange(parts);
            result = call;
        }

        while (true)
        {
            string? op = CurrentBinaryOp();
            if (op == null) break;
            int prec = Precedence.GetValueOrDefault(op, 13);
            if (prec < minPrec) break;
            Advance(); // consume op
            var right = ParseBinary(prec + 1);
            if (op == "..")
                result = new RangeExpr { From = result, To = right, Line = result.Line };
            else if (op == "|>")
                result = new PipeExpr { Value = result, Func = right, Line = result.Line };
            else
                result = new BinaryExpr { Left = result, Op = op, Right = right, Line = result.Line };
        }
        return result;
    }

    private bool CanStartJuxtaposed()
    {
        if (Check(TokenKind.Eof) || Check(TokenKind.Semi) || Check(TokenKind.Comma)
            || Check(TokenKind.RParen) || Check(TokenKind.RBrace) || Check(TokenKind.RBracket)
            || Check(TokenKind.LBrace) // avoid `when n {` / `if a {` ambiguity
            || Check(TokenKind.Then) || Check(TokenKind.Do) || Check(TokenKind.Else)
            || Check(TokenKind.In) || Check(TokenKind.Is) || Check(TokenKind.As)
            || Check(TokenKind.Catch) || Check(TokenKind.Finally)
            || Check(TokenKind.Eq) || Check(TokenKind.Colon)
            || Check(TokenKind.Arrow) || Check(TokenKind.FatArrow)
            || Check(TokenKind.LtGt) || Check(TokenKind.LeftArrow)
            || Check(TokenKind.QuestionQuestionEq))
            return false;

        // binary operators stop juxtaposition
        if (CurrentBinaryOp() != null) return false;

        // postfix handled in unary/postfix already; don't restart on . 
        if (Check(TokenKind.Dot)) return false;

        // Juxtaposition operands are primaries, but not bare blocks/if/when/try
        // (those need then/do or parentheses to avoid grammar ambiguity).
        return Check(TokenKind.Identifier) || Check(TokenKind.IntLiteral) || Check(TokenKind.FloatLiteral)
           || Check(TokenKind.StringLiteral) || Check(TokenKind.TemplateString) || Check(TokenKind.CharLiteral)
           || Check(TokenKind.True) || Check(TokenKind.False)
           || Check(TokenKind.LParen)
           || Check(TokenKind.New) || Check(TokenKind.Some) || Check(TokenKind.None)
           || Check(TokenKind.Unset) || Check(TokenKind.Break) || Check(TokenKind.Continue)
           || Check(TokenKind.Return) || Check(TokenKind.Throw)
           || Check(TokenKind.Convert);
    }

    private bool CanStartPrimary()
        => Check(TokenKind.Identifier) || Check(TokenKind.IntLiteral) || Check(TokenKind.FloatLiteral)
           || Check(TokenKind.StringLiteral) || Check(TokenKind.TemplateString) || Check(TokenKind.CharLiteral)
           || Check(TokenKind.True) || Check(TokenKind.False)
           || Check(TokenKind.LParen) || Check(TokenKind.LBrace)
           || Check(TokenKind.New) || Check(TokenKind.Some) || Check(TokenKind.None)
           || Check(TokenKind.Unset) || Check(TokenKind.If) || Check(TokenKind.When)
           || Check(TokenKind.Try) || Check(TokenKind.Break) || Check(TokenKind.Continue)
           || Check(TokenKind.Return) || Check(TokenKind.Throw)
           || Check(TokenKind.Convert) || Check(TokenKind.Set);

    private static string ExtractOpName(Expr e) => e switch
    {
        IdentExpr id => id.Name,
        GroupedOpExpr g => g.Op,
        QualifiedExpr q => $"{q.TypeName}::{q.Member}",
        _ => throw new AoiException("Expected operator name in juxtaposition", e.Line, e.Column),
    };

    private string? CurrentBinaryOp()
    {
        var t = Peek();
        // `+=` `-=` `*=` `/=` `%=` are compound assignments, not binary ops.
        if (t.Kind is TokenKind.Plus or TokenKind.Minus or TokenKind.Star
            or TokenKind.Slash or TokenKind.Percent
            && Peek(1).Kind == TokenKind.Eq)
            return null;

        return t.Kind switch
        {
            TokenKind.Plus => "+",
            TokenKind.Minus => "-",
            TokenKind.Star => "*",
            TokenKind.Slash => "/",
            TokenKind.Percent => "%",
            TokenKind.LtLt => "<<",
            TokenKind.GtGt => ">>",
            TokenKind.Lt => "<",
            TokenKind.LtEq => "<=",
            TokenKind.Gt => ">",
            TokenKind.GtEq => ">=",
            TokenKind.EqEq => "==",
            TokenKind.BangEq => "!=",
            TokenKind.Amp => "&",
            TokenKind.Caret => "^",
            TokenKind.Pipe => "|",
            TokenKind.AmpAmp => "&&",
            TokenKind.PipePipe => "||",
            TokenKind.QuestionQuestion => "??",
            TokenKind.PipeGt => "|>",
            TokenKind.DotDot => "..",
            TokenKind.At => "@",
            _ => null,
        };
    }

    private Expr ParseUnary()
    {
        if (Match(TokenKind.Plus) || Match(TokenKind.Minus) || Match(TokenKind.Bang) || Match(TokenKind.Tilde) || Match(TokenKind.At))
        {
            var op = Previous().Text;
            var operand = ParseUnary();
            return new UnaryExpr { Op = op, Operand = operand, Line = Previous().Line };
        }
        if (Match(TokenKind.Some))
        {
            var val = ParseUnary();
            return new SomeExpr { Value = val };
        }
        return ParsePostfix();
    }

    private Expr ParsePostfix()
    {
        var expr = ParsePrimary();
        while (true)
        {
            if (Match(TokenKind.Dot))
            {
                if (Match(TokenKind.LBracket))
                {
                    var index = ParseExpression();
                    Consume(TokenKind.RBracket, "Expected ']'");
                    expr = new IndexExpr { Object = expr, Index = index, Line = expr.Line };
                }
                else if (Check(TokenKind.IntLiteral))
                {
                    var n = Advance().Text;
                    expr = new MemberExpr { Object = expr, Member = n, Line = expr.Line };
                }
                else
                {
                    var name = Consume(TokenKind.Identifier, "Expected member").Text;
                    expr = new MemberExpr { Object = expr, Member = name, Line = expr.Line };
                }
            }
            else if (Match(TokenKind.Question))
            {
                if (Match(TokenKind.Dot))
                {
                    if (Match(TokenKind.LBracket))
                    {
                        var index = ParseExpression();
                        Consume(TokenKind.RBracket, "Expected ']'");
                        expr = new SafeIndexExpr { Object = expr, Index = index };
                    }
                    else if (Check(TokenKind.IntLiteral))
                    {
                        var n = Advance().Text;
                        expr = new SafeMemberExpr { Object = expr, Member = n };
                    }
                    else
                    {
                        var name = Consume(TokenKind.Identifier, "Expected member").Text;
                        expr = new SafeMemberExpr { Object = expr, Member = name };
                    }
                }
                else if (Check(TokenKind.Bang) == false && !CanStartJuxtaposed() && CurrentBinaryOp() == null)
                {
                    // trailing ? for nullable op propagation is hard; skip alone
                    break;
                }
                else break;
            }
            else if (Match(TokenKind.Bang) && !Check(TokenKind.Eq))
            {
                // a! null assert — only if not part of !=
                expr = new NullAssertExpr { Operand = expr };
            }
            else break;
        }
        return expr;
    }

    private Expr ParsePrimary()
    {
        if (Match(TokenKind.IntLiteral))
        {
            var tok = Previous();
            return MaybeLiteralSuffix(Loc(new IntExpr { Value = ParseIntLiteral(tok) }, tok), tok);
        }
        if (Match(TokenKind.FloatLiteral))
        {
            var tok = Previous();
            return MaybeLiteralSuffix(
                Loc(new FloatExpr { Value = double.Parse(tok.Text, System.Globalization.CultureInfo.InvariantCulture) }, tok),
                tok);
        }
        if (Match(TokenKind.StringLiteral))
        {
            var tok = Previous();
            return MaybeLiteralSuffix(Loc(new StringExpr { Value = tok.Text }, tok), tok);
        }
        if (Match(TokenKind.TemplateString))
            return Loc(new TemplateExpr { Raw = Previous().Text }, Previous());
        if (Match(TokenKind.CharLiteral))
        {
            var tok = Previous();
            return MaybeLiteralSuffix(Loc(new CharExpr { Value = tok.Text[0] }, tok), tok);
        }
        if (Match(TokenKind.True)) return Loc(new BoolExpr { Value = true }, Previous());
        if (Match(TokenKind.False)) return Loc(new BoolExpr { Value = false }, Previous());
        if (Match(TokenKind.Unset)) return Loc(new UnsetExpr(), Previous());
        if (Match(TokenKind.Convert))
            return Loc(new IdentExpr { Name = "convert" }, Previous());
        // Prefix set: set instance { ... }
        if (Match(TokenKind.Set))
        {
            var target = ParseUnary();
            if (!Check(TokenKind.LBrace))
                throw new AoiException("Expected '{' after set target", Peek().Line, Peek().Column);
            return ParseSetExprBody(target);
        }
        if (Match(TokenKind.None))
        {
            // none () or just none
            if (Match(TokenKind.LParen))
            {
                Consume(TokenKind.RParen, "Expected ')'");
            }
            return Loc(new NoneExpr(), Previous());
        }
        if (Match(TokenKind.Break)) return Loc(new BreakExpr(), Previous());
        if (Match(TokenKind.Continue)) return Loc(new ContinueExpr(), Previous());
        if (Match(TokenKind.Return))
        {
            var tok = Previous();
            Expr? val = null;
            if (CanStartPrimary() || Check(TokenKind.Plus) || Check(TokenKind.Minus) || Check(TokenKind.Bang))
                val = ParseExpression();
            return Loc(new ReturnExpr { Value = val }, tok);
        }
        if (Match(TokenKind.Throw))
        {
            var tok = Previous();
            return Loc(new ThrowExpr { Value = ParseExpression() }, tok);
        }
        if (Match(TokenKind.New)) return ParseNew();
        if (Match(TokenKind.If)) return ParseIfExpr();
        if (Match(TokenKind.When)) return ParseWhenExpr();
        if (Match(TokenKind.Try)) return ParseTryExpr();
        if (Match(TokenKind.LBrace)) return ParseBlockExprFromOpen();

        if (Match(TokenKind.LParen))
        {
            var tok = Previous();
            // () unit, (expr), (a, b) tuple, (+) grouped op, (a: T => body) closure start
            if (Match(TokenKind.RParen))
                return Loc(new UnitExpr(), tok);

            // grouped operator (+) (-) etc.
            if (CurrentBinaryOp() != null && Peek(1).Kind == TokenKind.RParen)
            {
                var op = CurrentBinaryOp()!;
                Advance();
                Consume(TokenKind.RParen, "Expected ')'");
                return Loc(new GroupedOpExpr { Op = op }, tok);
            }

            // Could be closure: pattern : Type <> pattern => 
            // or tuple/grouped expr
            int saved = _pos;
            try
            {
                var closure = TryParseClosureAfterParen();
                if (closure != null) return Loc(closure, tok);
            }
            catch { /* fall through */ }
            _pos = saved;

            var first = ParseExpression();
            if (Match(TokenKind.Comma))
            {
                var tuple = new TupleExpr();
                tuple.Elements.Add(first);
                do
                {
                    tuple.Elements.Add(ParseExpression());
                } while (Match(TokenKind.Comma));
                Consume(TokenKind.RParen, "Expected ')'");
                return Loc(tuple, tok);
            }

            // (pattern: Type => expr) unary closure without leading pattern parse
            if (Match(TokenKind.Colon))
            {
                var type = ParseType();
                if (Match(TokenKind.LtGt))
                {
                    var rhs = ParseDeclPattern();
                    TypeRef? rhsType = null;
                    if (Match(TokenKind.Colon)) rhsType = ParseType();
                    Consume(TokenKind.FatArrow, "Expected '=>'");
                    var body = ParseExpression();
                    Consume(TokenKind.RParen, "Expected ')'");
                    // first was used as expression but we need pattern - reparse
                    // This path is messy; handle simpler closures elsewhere
                }
                else if (Match(TokenKind.FatArrow))
                {
                    // Can't easily convert expr to pattern; skip
                }
            }

            Consume(TokenKind.RParen, "Expected ')'");
            return first;
        }

        if (Match(TokenKind.Identifier))
        {
            var tok = Previous();
            string name = tok.Text;

            // TypeName::Member [value]
            if (Match(TokenKind.ColonColon))
            {
                var member = ConsumeOpNameOrIdent();
                Expr? value = null;
                if (CanStartPrimary() || Check(TokenKind.Plus) || Check(TokenKind.Minus))
                {
                    // Qualified with value: Union::Case value
                    // Only if juxtaposition-like; for enums no value
                    // Heuristic: if next can start primary and isn't statement end
                    if (CanStartJuxtaposed() || CanStartPrimary())
                    {
                        // For Enum::Value typically next is not a value
                        // For Union::Case value, value follows
                        // Ambiguous - if next is identifier/literal/paren, take it when looking for union
                        if (!Check(TokenKind.Semi) && !Check(TokenKind.Comma) && !Check(TokenKind.RParen)
                            && !Check(TokenKind.RBrace) && CurrentBinaryOp() == null
                            && !Check(TokenKind.Then) && !Check(TokenKind.Do) && !Check(TokenKind.Else)
                            && !Check(TokenKind.Eof) && !Check(TokenKind.Is)
                            && (Check(TokenKind.IntLiteral) || Check(TokenKind.FloatLiteral)
                                || Check(TokenKind.StringLiteral) || Check(TokenKind.CharLiteral)
                                || Check(TokenKind.True) || Check(TokenKind.False)
                                || Check(TokenKind.LParen) || Check(TokenKind.New)
                                || Check(TokenKind.Identifier) || Check(TokenKind.LBrace)))
                        {
                            // Prefer not consuming another identifier as value for Enum::Member in juxtaposition
                            // Only take value for literals and structured exprs
                            if (!Check(TokenKind.Identifier) || Peek(1).Kind is TokenKind.LBrace or TokenKind.LParen)
                                value = ParseUnary();
                            else if (Check(TokenKind.IntLiteral) || Check(TokenKind.FloatLiteral)
                                     || Check(TokenKind.StringLiteral) || Check(TokenKind.LParen)
                                     || Check(TokenKind.New) || Check(TokenKind.True) || Check(TokenKind.False))
                                value = ParseUnary();
                        }
                    }
                }
                return Loc(new QualifiedExpr { TypeName = name, Member = member, Value = value }, tok);
            }

            // Cast: Type value  — handled via juxtaposition as unary
            // Closure: name: Type => body  or name => body
            if (Match(TokenKind.Colon))
            {
                var type = ParseType();
                if (Match(TokenKind.FatArrow))
                {
                    var body = ParseExpression();
                    return Loc(new ClosureExpr
                    {
                        Rhs = new NameDeclPattern { Name = name },
                        RhsType = type,
                        Body = body,
                    }, tok);
                }
                if (Match(TokenKind.LtGt))
                {
                    var rhsPat = ParseDeclPattern();
                    TypeRef? rhsType = null;
                    if (Match(TokenKind.Colon)) rhsType = ParseType();
                    Consume(TokenKind.FatArrow, "Expected '=>'");
                    var body = ParseExpression();
                    return Loc(new ClosureExpr
                    {
                        Lhs = new NameDeclPattern { Name = name },
                        LhsType = type,
                        Rhs = rhsPat,
                        RhsType = rhsType,
                        Body = body,
                    }, tok);
                }
                // Type cast: Type already consumed after name — name was type
                var castVal = ParseUnary();
                return Loc(new CastExpr { Type = new NamedTypeRef { Name = name }, Value = castVal }, tok);
            }

            if (Match(TokenKind.FatArrow))
            {
                var body = ParseExpression();
                return Loc(new ClosureExpr
                {
                    Rhs = new NameDeclPattern { Name = name },
                    Body = body,
                }, tok);
            }

            // Optional generic args for op: name[T]
            if (Match(TokenKind.LBracket))
            {
                // Could be generic op call name[Type] — keep as Ident for now and skip types
                while (!Check(TokenKind.RBracket) && !Check(TokenKind.Eof))
                    Advance();
                Consume(TokenKind.RBracket, "Expected ']'");
            }

            return Loc(new IdentExpr { Name = name }, tok);
        }

        throw new AoiException($"Unexpected token {Peek()}", Peek().Line, Peek().Column);
    }

    private ClosureExpr? TryParseClosureAfterParen()
    {
        // (a => body) or (a: T => body) or (a: T <> b: U => body)
        if (!Check(TokenKind.Identifier) && !Check(TokenKind.LParen) && !Check(TokenKind.LBrace))
            return null;
        var lhsOrRhs = ParseDeclPattern();
        TypeRef? t1 = null;
        if (Match(TokenKind.Colon))
            t1 = ParseType();

        if (Match(TokenKind.LtGt))
        {
            var rhs = ParseDeclPattern();
            TypeRef? t2 = null;
            if (Match(TokenKind.Colon)) t2 = ParseType();
            Consume(TokenKind.FatArrow, "Expected '=>'");
            var body = ParseExpression();
            Consume(TokenKind.RParen, "Expected ')'");
            return new ClosureExpr { Lhs = lhsOrRhs, LhsType = t1, Rhs = rhs, RhsType = t2, Body = body };
        }

        if (Match(TokenKind.FatArrow))
        {
            var body = ParseExpression();
            Consume(TokenKind.RParen, "Expected ')'");
            return new ClosureExpr { Rhs = lhsOrRhs, RhsType = t1, Body = body };
        }
        return null;
    }

    private Expr ParseNew()
    {
        var tok = Previous();
        var type = ParseType();
        Consume(TokenKind.LBrace, "Expected '{'");
        var expr = new NewExpr { Type = type, IsArray = type is NamedTypeRef n && n.Name == "arr" };
        expr.Elements.AddRange(ParseInitializerElements());
        Consume(TokenKind.RBrace, "Expected '}'");
        return Loc(expr, tok);
    }

    private Expr MaybeLiteralSuffix(Expr lit, Token litTok)
    {
        if (!Check(TokenKind.Identifier)) return lit;
        var suf = Peek();
        if (suf.Line != litTok.Line) return lit;
        if (litTok.Column + litTok.Text.Length != suf.Column) return lit;
        Advance();
        return Loc(new LiteralSuffixExpr { Value = lit, Suffix = suf.Text }, litTok);
    }

    private Expr ParseIfExpr()
    {
        var tok = Previous();
        var first = ParseBinary();
        if (Match(TokenKind.Is))
        {
            var pattern = ParseMatchPattern();
            Consume(TokenKind.Then, "Expected 'then'");
            var thenB = ParseExpression();
            Consume(TokenKind.Else, "Expected 'else'");
            var elseB = ParseExpression();
            return Loc(new IfExpr { MatchValue = first, MatchPattern = pattern, ThenBranch = thenB, ElseBranch = elseB }, tok);
        }
        Consume(TokenKind.Then, "Expected 'then'");
        var thenBranch = ParseExpression();
        Consume(TokenKind.Else, "Expected 'else'");
        var elseBranch = ParseExpression();
        return Loc(new IfExpr { Condition = first, ThenBranch = thenBranch, ElseBranch = elseBranch }, tok);
    }

    private Expr ParseWhenExpr()
    {
        var tok = Previous();
        var value = ParseExpression();
        Consume(TokenKind.LBrace, "Expected '{'");
        var expr = new WhenExpr { Value = value };
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            if (Match(TokenKind.Is))
            {
                var pattern = ParseMatchPattern();
                Consume(TokenKind.Then, "Expected 'then'");
                var branch = ParseExpression();
                Match(TokenKind.Semi);
                expr.Arms.Add((pattern, branch));
            }
            else if (Match(TokenKind.Else))
            {
                expr.ElseBranch = ParseExpression();
                Match(TokenKind.Semi);
                break;
            }
            else break;
        }
        Consume(TokenKind.RBrace, "Expected '}'");
        return Loc(expr, tok);
    }

    private Expr ParseTryExpr()
    {
        var tok = Previous();
        var body = ParseExpression();
        var expr = new TryExpr { Body = body };
        while (Match(TokenKind.Catch))
        {
            var pattern = ParseMatchPattern();
            Consume(TokenKind.Then, "Expected 'then'");
            var branch = ParseExpression();
            expr.Catches.Add((pattern, branch));
        }
        return Loc(expr, tok);
    }

    private Expr ParseBlockExprFromOpen()
    {
        var tok = Previous(); // {
        var stmts = new List<Stmt>();
        Expr? result = null;
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            int saved = _pos;
            if (Check(TokenKind.Let) || Check(TokenKind.If) || Check(TokenKind.Loop)
                || Check(TokenKind.While) || Check(TokenKind.For) || Check(TokenKind.When)
                || Check(TokenKind.Try))
            {
                stmts.Add(ParseStatement());
                continue;
            }
            var e = ParseExpression();
            if (Match(TokenKind.Semi))
                stmts.Add(new ExprStmt { Expression = e });
            else
            {
                result = e;
                break;
            }
        }
        Consume(TokenKind.RBrace, "Expected '}'");
        if (result == null)
            throw new AoiException("Expression block must end with an expression", tok.Line, tok.Column);
        var be = new BlockExpr { Result = result };
        be.Statements.AddRange(stmts);
        return Loc(be, tok);
    }

    // --- Patterns ---

    private DeclPattern ParseDeclPattern()
    {
        if (Match(TokenKind.LParen))
        {
            var tuple = new TupleDeclPattern();
            if (!Check(TokenKind.RParen))
            {
                do
                {
                    if (Check(TokenKind.Comma) || Check(TokenKind.RParen))
                        tuple.Elements.Add(null);
                    else
                        tuple.Elements.Add(ParseDeclPattern());
                } while (Match(TokenKind.Comma));
            }
            Consume(TokenKind.RParen, "Expected ')'");
            return tuple;
        }
        if (Match(TokenKind.LBrace))
        {
            var obj = new ObjectDeclPattern();
            while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
            {
                string? name = null;
                Expr? index = null;
                DeclPattern pattern;
                if (Match(TokenKind.LBracket))
                {
                    index = ParseExpression();
                    Consume(TokenKind.RBracket, "Expected ']'");
                    Consume(TokenKind.Colon, "Expected ':'");
                    pattern = ParseDeclPattern();
                }
                else if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Colon)
                {
                    name = Advance().Text;
                    Advance();
                    pattern = ParseDeclPattern();
                }
                else
                {
                    pattern = ParseDeclPattern();
                }
                obj.Fields.Add((name, index, pattern));
                Match(TokenKind.Comma);
            }
            Consume(TokenKind.RBrace, "Expected '}'");
            return obj;
        }
        var id = Consume(TokenKind.Identifier, "Expected pattern name");
        return Loc(new NameDeclPattern { Name = id.Text }, id);
    }

    private MatchPattern ParseMatchPattern() => ParseOrMatch();

    private MatchPattern ParseOrMatch()
    {
        var left = ParseXorMatch();
        while (Match(TokenKind.Or))
            left = new OrMatchPattern { Left = left, Right = ParseXorMatch() };
        return left;
    }

    private MatchPattern ParseXorMatch()
    {
        var left = ParseAndMatch();
        while (Match(TokenKind.Xor))
            left = new XorMatchPattern { Left = left, Right = ParseAndMatch() };
        return left;
    }

    private MatchPattern ParseAndMatch()
    {
        var left = ParseUnaryMatch();
        while (Match(TokenKind.And))
            left = new AndMatchPattern { Left = left, Right = ParseUnaryMatch() };
        return left;
    }

    private MatchPattern ParseUnaryMatch()
    {
        if (Match(TokenKind.Not))
            return new NotMatchPattern { Pattern = ParseUnaryMatch() };

        if (Match(TokenKind.LParen))
        {
            var tuple = new TupleMatchPattern();
            if (!Check(TokenKind.RParen))
            {
                do
                {
                    if (Check(TokenKind.Comma) || Check(TokenKind.RParen))
                        tuple.Elements.Add(null);
                    else
                        tuple.Elements.Add(ParseMatchPattern());
                } while (Match(TokenKind.Comma));
            }
            Consume(TokenKind.RParen, "Expected ')'");
            return FinishAs(tuple);
        }

        if (Match(TokenKind.LBrace))
        {
            var obj = new ObjectMatchPattern();
            while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
            {
                string? name = null;
                Expr? index = null;
                MatchPattern pattern;
                if (Match(TokenKind.LBracket))
                {
                    index = ParseExpression();
                    Consume(TokenKind.RBracket, "Expected ']'");
                    Consume(TokenKind.Colon, "Expected ':'");
                    pattern = ParseMatchPattern();
                }
                else if (Check(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Colon)
                {
                    name = Advance().Text;
                    Advance();
                    pattern = ParseMatchPattern();
                }
                else
                {
                    pattern = ParseMatchPattern();
                }
                obj.Fields.Add((name, index, pattern));
                Match(TokenKind.Comma);
            }
            Consume(TokenKind.RBrace, "Expected '}'");
            return FinishAs(obj);
        }

        // some as b  /  Condition pattern as decl  /  Type pattern as decl
        if (Match(TokenKind.Some))
        {
            MatchPattern? inner = null;
            if (!Check(TokenKind.As) && !Check(TokenKind.Then) && !Check(TokenKind.And)
                && !Check(TokenKind.Or) && !Check(TokenKind.Xor) && !Check(TokenKind.RBrace)
                && !Check(TokenKind.Comma) && !Check(TokenKind.RParen) && CurrentBinaryOp() == null)
            {
                // optional inner pattern - if next looks like pattern start that's not 'as'
                if (!Check(TokenKind.As))
                {
                    // try optional value pattern - skip for simplicity unless obvious
                }
            }
            DeclPattern? binding = null;
            if (Match(TokenKind.As))
                binding = ParseDeclPattern();
            return new UnionMatchPattern { Condition = "some", Inner = inner, Binding = binding };
        }

        if (Match(TokenKind.None))
        {
            if (Match(TokenKind.LParen)) Consume(TokenKind.RParen, "Expected ')'");
            DeclPattern? binding = null;
            if (Match(TokenKind.As)) binding = ParseDeclPattern();
            return new UnionMatchPattern { Condition = "none", Binding = binding };
        }

        // Binary op pattern: >= 0, < 0, == x
        if (CurrentBinaryOp() != null)
        {
            var op = CurrentBinaryOp()!;
            Advance();
            var value = ParseUnary();
            return FinishAs(new BinaryOpMatchPattern { Op = op, Value = value });
        }

        // Identifier: could be unary op match, type match, union condition, or value
        if (Check(TokenKind.Identifier))
        {
            var name = Advance().Text;
            if (Match(TokenKind.ColonColon))
            {
                var member = Consume(TokenKind.Identifier, "Expected member").Text;
                MatchPattern? inner = null;
                DeclPattern? binding = null;
                // optional inner pattern before as
                if (!Check(TokenKind.As) && !Check(TokenKind.Then) && !Check(TokenKind.And)
                    && !Check(TokenKind.Or) && !Check(TokenKind.RBrace) && CurrentBinaryOp() != null)
                {
                    inner = ParseUnaryMatch();
                }
                else if (!Check(TokenKind.As) && !Check(TokenKind.Then) && CanStartPrimary()
                         && CurrentBinaryOp() == null && !Check(TokenKind.And) && !Check(TokenKind.Or))
                {
                    // value after condition - treat as value match on payload? skip
                }
                if (Match(TokenKind.As))
                    binding = ParseDeclPattern();
                return new UnionMatchPattern { Condition = $"{name}::{member}", Inner = inner, Binding = binding };
            }

            // Type pattern: Type ... as decl
            if (Match(TokenKind.As))
            {
                var binding = ParseDeclPattern();
                return new TypeMatchPattern { Type = new NamedTypeRef { Name = name }, Binding = binding };
            }

            // Could be unary op name standing alone, or a type/exception name (PascalCase).
            if (Check(TokenKind.Then) || Check(TokenKind.And) || Check(TokenKind.Or)
                || Check(TokenKind.Xor) || Check(TokenKind.RBrace) || Check(TokenKind.Comma)
                || Check(TokenKind.RParen) || Check(TokenKind.Eof) || Check(TokenKind.Semi))
            {
                if (name.Length > 0 && char.IsUpper(name[0]))
                    return new UnionMatchPattern { Condition = name };
                return new UnaryOpMatchPattern { Op = name };
            }

            // value pattern
            return FinishAs(new ValueMatchPattern { Value = new IdentExpr { Name = name } });
        }

        // Literal value pattern
        var expr = ParseUnary();
        return FinishAs(new ValueMatchPattern { Value = expr });
    }

    private MatchPattern FinishAs(MatchPattern pattern)
    {
        if (Match(TokenKind.As))
            return new AsMatchPattern { Inner = pattern, Binding = ParseDeclPattern() };
        return pattern;
    }

    // --- Types ---

    private TypeRef ParseType()
    {
        TypeRef type;
        if (Match(TokenKind.LParen))
        {
            if (Match(TokenKind.RParen))
            {
                type = new TupleTypeRef();
            }
            else
            {
                var first = ParseType();
                if (Match(TokenKind.Comma))
                {
                    var tuple = new TupleTypeRef();
                    tuple.Elements.Add(first);
                    do { tuple.Elements.Add(ParseType()); } while (Match(TokenKind.Comma));
                    Consume(TokenKind.RParen, "Expected ')'");
                    type = tuple;
                }
                else if (Match(TokenKind.LtGt))
                {
                    var rhs = ParseType();
                    Consume(TokenKind.Arrow, "Expected '->'");
                    var result = ParseType();
                    Consume(TokenKind.RParen, "Expected ')'");
                    type = new OpTypeRef { Lhs = first, Rhs = rhs, Result = result };
                }
                else if (Match(TokenKind.Arrow))
                {
                    var result = ParseType();
                    Consume(TokenKind.RParen, "Expected ')'");
                    type = new OpTypeRef { Rhs = first, Result = result };
                }
                else
                {
                    Consume(TokenKind.RParen, "Expected ')'");
                    type = first;
                }
            }
        }
        else if (Match(TokenKind.Dyn))
        {
            var dyn = new DynTypeRef { Inner = new NamedTypeRef { Name = "_" } };
            if (Match(TokenKind.LBracket))
            {
                while (!Check(TokenKind.RBracket) && !Check(TokenKind.Eof))
                    Advance();
                Consume(TokenKind.RBracket, "Expected ']'");
            }
            dyn.Inner = ParseType();
            type = dyn;
        }
        else
        {
            var name = Consume(TokenKind.Identifier, "Expected type name").Text;
            var named = new NamedTypeRef { Name = name };
            if (Match(TokenKind.LBracket))
            {
                do
                {
                    if (Check(TokenKind.RBracket)) break;
                    named.Args.Add(ParseType());
                } while (Match(TokenKind.Comma));
                Consume(TokenKind.RBracket, "Expected ']'");
            }
            type = named;
        }

        // Operator type without parens: Type -> Type  or after seeing <> 
        // Already handled in paren form mostly.

        while (Match(TokenKind.Question))
        {
            if (type is NamedTypeRef n) n.Nullable = true;
            else type = new NamedTypeRef { Name = type.ToString() ?? "?", Nullable = true };
        }
        return type;
    }

    private List<string> ParseOptionalTypeParams()
    {
        var list = new List<string>();
        if (!Match(TokenKind.LBracket)) return list;
        do
        {
            if (Check(TokenKind.RBracket)) break;
            list.Add(Consume(TokenKind.Identifier, "Expected type param").Text);
            if (Match(TokenKind.Colon))
            {
                while (!Check(TokenKind.Comma) && !Check(TokenKind.RBracket) && !Check(TokenKind.Eof))
                    Advance();
            }
        } while (Match(TokenKind.Comma));
        Consume(TokenKind.RBracket, "Expected ']'");
        return list;
    }

    private bool CheckOpName()
    {
        if (Check(TokenKind.Identifier)) return true;
        if (CurrentBinaryOp() != null) return true;
        if (Check(TokenKind.Plus) || Check(TokenKind.Minus) || Check(TokenKind.Bang) || Check(TokenKind.Tilde) || Check(TokenKind.At))
            return true;
        return false;
    }

    private string ConsumeOpName()
    {
        if (Check(TokenKind.Identifier)) return Advance().Text;
        // Contextual: `convert` may be an operator name (ch.3.5).
        if (Check(TokenKind.Convert)) { Advance(); return "convert"; }
        if (CurrentBinaryOp() != null) { var op = CurrentBinaryOp()!; Advance(); return op; }
        if (Check(TokenKind.Plus) || Check(TokenKind.Minus) || Check(TokenKind.Bang) || Check(TokenKind.Tilde) || Check(TokenKind.At))
            return Advance().Text;
        throw new AoiException("Expected operator name", Peek().Line, Peek().Column);
    }

    private string ConsumeOpNameOrIdent()
    {
        if (Check(TokenKind.Identifier)) return Advance().Text;
        if (Check(TokenKind.Convert)) { Advance(); return "convert"; }
        if (CurrentBinaryOp() != null) { var op = CurrentBinaryOp()!; Advance(); return op; }
        if (Check(TokenKind.Star) || Check(TokenKind.Plus) || Check(TokenKind.Minus) || Check(TokenKind.Lt))
            return Advance().Text;
        throw new AoiException("Expected name", Peek().Line, Peek().Column);
    }

    private static long ParseIntLiteral(Token tok)
    {
        var t = tok.Text;
        try
        {
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToInt64(t[2..], 16);
            if (t.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                return Convert.ToInt64(t[2..], 2);
            if (t.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
                return Convert.ToInt64(t[2..], 8);
            return long.Parse(t);
        }
        catch
        {
            throw new AoiException($"Invalid integer literal '{t}'", tok.Line, tok.Column);
        }
    }

    // --- helpers ---

    private bool Match(TokenKind kind)
    {
        if (Check(kind)) { Advance(); return true; }
        return false;
    }

    private bool Check(TokenKind kind) => Peek().Kind == kind;

    private Token Advance()
    {
        if (!Check(TokenKind.Eof)) _pos++;
        return Previous();
    }

    private Token Peek(int offset = 0)
        => _pos + offset >= _tokens.Count ? _tokens[^1] : _tokens[_pos + offset];

    private Token Previous() => _tokens[_pos - 1];

    private Token Consume(TokenKind kind, string message)
    {
        if (Check(kind)) return Advance();
        throw new AoiException($"{message}, got {Peek()}", Peek().Line, Peek().Column);
    }

    private static T Loc<T>(T node, Token tok) where T : AstNode
    {
        node.Line = tok.Line;
        node.Column = tok.Column;
        return node;
    }

    private static T Loc<T>(T node, AstNode other) where T : AstNode
    {
        node.Line = other.Line;
        node.Column = other.Column;
        return node;
    }
}
