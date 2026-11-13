namespace Aoi.Interpreter.Ast;

public abstract class AstNode
{
    public int Line { get; set; }
    public int Column { get; set; }
}

public abstract class Stmt : AstNode { }
public abstract class Expr : AstNode { }

public sealed class ProgramNode : AstNode
{
    public List<Stmt> Statements { get; set; } = new();
}

// --- Statements ---

public sealed class ExprStmt : Stmt
{
    public required Expr Expression { get; set; }
}

public sealed class LetStmt : Stmt
{
    public required DeclPattern Pattern { get; set; }
    public TypeRef? Type { get; set; }
    public required Expr Value { get; set; }
}

public sealed class BlockStmt : Stmt
{
    public List<Stmt> Statements { get; set; } = new();
}

public sealed class IfStmt : Stmt
{
    public Expr? Condition { get; set; }
    public Expr? MatchValue { get; set; }
    public MatchPattern? MatchPattern { get; set; }
    public required Stmt ThenBranch { get; set; }
    public Stmt? ElseBranch { get; set; }
}

public sealed class LoopStmt : Stmt
{
    public required Stmt Body { get; set; }
}

public sealed class WhileStmt : Stmt
{
    public required Expr Condition { get; set; }
    public required Stmt Body { get; set; }
}

public sealed class ForStmt : Stmt
{
    public required DeclPattern Pattern { get; set; }
    public required Expr Iterable { get; set; }
    public required Stmt Body { get; set; }
}

public sealed class WhenStmt : Stmt
{
    public required Expr Value { get; set; }
    public List<(MatchPattern Pattern, Stmt Branch)> Arms { get; set; } = new();
    public Stmt? ElseBranch { get; set; }
}

public sealed class TryStmt : Stmt
{
    public required Stmt Body { get; set; }
    public List<(MatchPattern Pattern, Stmt Branch)> Catches { get; set; } = new();
    public Stmt? Finally { get; set; }
}

public sealed class StructDecl : Stmt
{
    public required string Name { get; set; }
    public List<string> TypeParams { get; set; } = new();
    public List<(string Name, TypeRef Type)> Fields { get; set; } = new();
    public bool IsClass { get; set; }
}

public sealed class EnumDecl : Stmt
{
    public required string Name { get; set; }
    public List<(string Name, long? Value)> Members { get; set; } = new();
}

public sealed class UnionDecl : Stmt
{
    public required string Name { get; set; }
    public List<string> TypeParams { get; set; } = new();
    public List<(string Name, TypeRef Type)> Cases { get; set; } = new();
}

public sealed class OpDecl : Stmt
{
    public DeclPattern? Lhs { get; set; }
    public required string Name { get; set; }
    public DeclPattern? Rhs { get; set; }
    public List<string> TypeParams { get; set; } = new();
    public TypeRef? LhsType { get; set; }
    public TypeRef? RhsType { get; set; }
    public TypeRef? ResultType { get; set; }
    public required Expr Body { get; set; }
    public bool IsBinary => Lhs != null;
}

public sealed class TraitDecl : Stmt
{
    public required string Name { get; set; }
    public List<string> TypeParams { get; set; } = new();
    public List<OpDecl> Members { get; set; } = new();
}

public sealed class ImplDecl : Stmt
{
    public string? Name { get; set; }
    public string? TraitName { get; set; }
    public List<TypeRef> TraitArgs { get; set; } = new();
    public List<string> TypeParams { get; set; } = new();
    public List<OpDecl> Members { get; set; } = new();
    public string? AliasOf { get; set; }
}

public sealed class TypeAliasDecl : Stmt
{
    public required string Name { get; set; }
    public List<string> TypeParams { get; set; } = new();
    public required TypeRef Definition { get; set; }
}

public sealed class ConvertDecl : Stmt
{
    public required bool Implicit { get; set; }
    public required DeclPattern Pattern { get; set; }
    public required TypeRef FromType { get; set; }
    public required TypeRef ToType { get; set; }
    public required Expr Body { get; set; }
}

public sealed class GetDecl : Stmt
{
    public required DeclPattern Self { get; set; }
    public string? PropertyName { get; set; }
    public DeclPattern? Index { get; set; }
    public TypeRef? SelfType { get; set; }
    public TypeRef? IndexType { get; set; }
    public TypeRef? ResultType { get; set; }
    public required Expr Body { get; set; }
    public bool IsIndexer => Index != null;
}

public sealed class SetDecl : Stmt
{
    public required DeclPattern Self { get; set; }
    public string? PropertyName { get; set; }
    public DeclPattern? Index { get; set; }
    public required DeclPattern Value { get; set; }
    public TypeRef? SelfType { get; set; }
    public TypeRef? IndexType { get; set; }
    public TypeRef? ValueType { get; set; }
    public required Expr Body { get; set; }
    public bool IsIndexer => Index != null;
}

public sealed class LiteralSuffixDecl : Stmt
{
    public required DeclPattern Pattern { get; set; }
    public required string Suffix { get; set; }
    public required TypeRef ValueType { get; set; }
    public required TypeRef ResultType { get; set; }
    public required Expr Body { get; set; }
}

// --- Expressions ---

public sealed class IntExpr : Expr
{
    public required long Value { get; set; }
}

public sealed class FloatExpr : Expr
{
    public required double Value { get; set; }
}

public sealed class BoolExpr : Expr
{
    public required bool Value { get; set; }
}

public sealed class CharExpr : Expr
{
    public required char Value { get; set; }
}

public sealed class StringExpr : Expr
{
    public required string Value { get; set; }
}

public sealed class TemplateExpr : Expr
{
    public required string Raw { get; set; }
}

public sealed class IdentExpr : Expr
{
    public required string Name { get; set; }
}

public sealed class UnaryExpr : Expr
{
    public required string Op { get; set; }
    public required Expr Operand { get; set; }
}

public sealed class BinaryExpr : Expr
{
    public required Expr Left { get; set; }
    public required string Op { get; set; }
    public required Expr Right { get; set; }
}

public sealed class AssignExpr : Expr
{
    public required AssignPattern Pattern { get; set; }
    public required Expr Value { get; set; }
}

public sealed class CompoundAssignExpr : Expr
{
    public required Expr Target { get; set; }
    public required string Op { get; set; }
    public required Expr Value { get; set; }
}

public sealed class BlockExpr : Expr
{
    public List<Stmt> Statements { get; set; } = new();
    public required Expr Result { get; set; }
}

public sealed class IfExpr : Expr
{
    public Expr? Condition { get; set; }
    public Expr? MatchValue { get; set; }
    public MatchPattern? MatchPattern { get; set; }
    public required Expr ThenBranch { get; set; }
    public required Expr ElseBranch { get; set; }
}

public sealed class WhenExpr : Expr
{
    public required Expr Value { get; set; }
    public List<(MatchPattern Pattern, Expr Branch)> Arms { get; set; } = new();
    public Expr? ElseBranch { get; set; }
}

public sealed class TryExpr : Expr
{
    public required Expr Body { get; set; }
    public List<(MatchPattern Pattern, Expr Branch)> Catches { get; set; } = new();
}

public sealed class TupleExpr : Expr
{
    public List<Expr> Elements { get; set; } = new();
}

public sealed class MemberExpr : Expr
{
    public required Expr Object { get; set; }
    public required string Member { get; set; }
}

public sealed class IndexExpr : Expr
{
    public required Expr Object { get; set; }
    public required Expr Index { get; set; }
}

public sealed class CallLikeExpr : Expr
{
    /// <summary>Sequence of juxtaposed primary expressions (odd length for binary ops).</summary>
    public List<Expr> Parts { get; set; } = new();
}

public sealed class NewExpr : Expr
{
    public required TypeRef Type { get; set; }
    public List<(string? Name, Expr Value, Expr? Repeat, Expr? Index)> Elements { get; set; } = new();
    public bool IsArray { get; set; }
}

public sealed class SetExpr : Expr
{
    public required Expr Target { get; set; }
    public List<(string? Name, Expr Value, Expr? Repeat, Expr? Index)> Elements { get; set; } = new();
}

public sealed class RangeExpr : Expr
{
    public required Expr From { get; set; }
    public required Expr To { get; set; }
}

public sealed class SomeExpr : Expr
{
    public required Expr Value { get; set; }
}

public sealed class NoneExpr : Expr { }

public sealed class UnsetExpr : Expr { }

public sealed class UnitExpr : Expr { }

public sealed class BreakExpr : Expr { }
public sealed class ContinueExpr : Expr { }

public sealed class ReturnExpr : Expr
{
    public Expr? Value { get; set; }
}

public sealed class ThrowExpr : Expr
{
    public required Expr Value { get; set; }
}

public sealed class IsExpr : Expr
{
    public required Expr Value { get; set; }
    public required MatchPattern Pattern { get; set; }
}

public sealed class ClosureExpr : Expr
{
    public DeclPattern? Lhs { get; set; }
    public TypeRef? LhsType { get; set; }
    public required DeclPattern Rhs { get; set; }
    public TypeRef? RhsType { get; set; }
    public required Expr Body { get; set; }
    public bool IsBinary => Lhs != null;
}

public sealed class PipeExpr : Expr
{
    public required Expr Value { get; set; }
    public required Expr Func { get; set; }
}

public sealed class CastExpr : Expr
{
    public required TypeRef Type { get; set; }
    public required Expr Value { get; set; }
}

public sealed class QualifiedExpr : Expr
{
    public required string TypeName { get; set; }
    public required string Member { get; set; }
    public Expr? Value { get; set; }
}

public sealed class NullAssertExpr : Expr
{
    public required Expr Operand { get; set; }
}

public sealed class SafeMemberExpr : Expr
{
    public required Expr Object { get; set; }
    public required string Member { get; set; }
}

public sealed class SafeIndexExpr : Expr
{
    public required Expr Object { get; set; }
    public required Expr Index { get; set; }
}

public sealed class GroupedOpExpr : Expr
{
    /// <summary>Parenthesized operator used for currying, e.g. (+).</summary>
    public required string Op { get; set; }
}

// --- Patterns ---

public abstract class DeclPattern : AstNode { }

public sealed class NameDeclPattern : DeclPattern
{
    public required string Name { get; set; }
}

public sealed class TupleDeclPattern : DeclPattern
{
    public List<DeclPattern?> Elements { get; set; } = new();
}

public sealed class ObjectDeclPattern : DeclPattern
{
    public List<(string? Name, Expr? Index, DeclPattern Pattern)> Fields { get; set; } = new();
}

public abstract class AssignPattern : AstNode { }

public sealed class ExprAssignPattern : AssignPattern
{
    public required Expr Target { get; set; }
}

public sealed class TupleAssignPattern : AssignPattern
{
    public List<AssignPattern?> Elements { get; set; } = new();
}

public sealed class ObjectAssignPattern : AssignPattern
{
    public List<(string? Name, Expr? Index, AssignPattern Pattern)> Fields { get; set; } = new();
}

public abstract class MatchPattern : AstNode { }

public sealed class ValueMatchPattern : MatchPattern
{
    public required Expr Value { get; set; }
}

public sealed class UnaryOpMatchPattern : MatchPattern
{
    public required string Op { get; set; }
}

public sealed class BinaryOpMatchPattern : MatchPattern
{
    public required string Op { get; set; }
    public required Expr Value { get; set; }
}

public sealed class AndMatchPattern : MatchPattern
{
    public required MatchPattern Left { get; set; }
    public required MatchPattern Right { get; set; }
}

public sealed class OrMatchPattern : MatchPattern
{
    public required MatchPattern Left { get; set; }
    public required MatchPattern Right { get; set; }
}

public sealed class XorMatchPattern : MatchPattern
{
    public required MatchPattern Left { get; set; }
    public required MatchPattern Right { get; set; }
}

public sealed class NotMatchPattern : MatchPattern
{
    public required MatchPattern Pattern { get; set; }
}

public sealed class TupleMatchPattern : MatchPattern
{
    public List<MatchPattern?> Elements { get; set; } = new();
}

public sealed class ObjectMatchPattern : MatchPattern
{
    public List<(string? Name, Expr? Index, MatchPattern Pattern)> Fields { get; set; } = new();
}

public sealed class UnionMatchPattern : MatchPattern
{
    public required string Condition { get; set; }
    public MatchPattern? Inner { get; set; }
    public DeclPattern? Binding { get; set; }
}

public sealed class TypeMatchPattern : MatchPattern
{
    public required TypeRef Type { get; set; }
    public MatchPattern? Inner { get; set; }
    public DeclPattern? Binding { get; set; }
}

public sealed class AsMatchPattern : MatchPattern
{
    public required MatchPattern Inner { get; set; }
    public required DeclPattern Binding { get; set; }
}

// --- Types ---

public abstract class TypeRef : AstNode { }

public sealed class NamedTypeRef : TypeRef
{
    public required string Name { get; set; }
    public List<TypeRef> Args { get; set; } = new();
    public bool Nullable { get; set; }
}

public sealed class TupleTypeRef : TypeRef
{
    public List<TypeRef> Elements { get; set; } = new();
}

public sealed class OpTypeRef : TypeRef
{
    public TypeRef? Lhs { get; set; }
    public required TypeRef Rhs { get; set; }
    public required TypeRef Result { get; set; }
}

public sealed class DynTypeRef : TypeRef
{
    public List<string> TypeParams { get; set; } = new();
    public List<TypeRef> Requirements { get; set; } = new();
    public required TypeRef Inner { get; set; }
}
