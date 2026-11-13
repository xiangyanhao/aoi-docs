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

// --- Generic syntax ---

/// <summary>
/// A type parameter declaration, e.g. <c>T</c> or <c>T: Ord[T], ToStr[T]</c>.
/// </summary>
public sealed class TypeParameterSyntax : AstNode
{
    public required string Name { get; set; }
    public List<TraitRequirementSyntax> Requirements { get; set; } = new();
}

/// <summary>
/// A trait requirement such as <c>Ord[T]</c>. A bare trait name with no
/// arguments (<c>T: Ord</c>) is also allowed; arguments are inferred later.
/// </summary>
public sealed class TraitRequirementSyntax : AstNode
{
    public required string TraitName { get; set; }
    public List<TypeRef> Args { get; set; } = new();
}

/// <summary>
/// A reference to a trait implementation, used in impl headers and in
/// explicitly supplied generic arguments: <c>OrdByX</c> or
/// <c>PartialOrdByX[Vector]</c>.
/// </summary>
public sealed class ImplRefSyntax : AstNode
{
    public required string Name { get; set; }
    public List<TypeRef> Args { get; set; } = new();
    /// <summary>True when <see cref="Args"/> was written; used to disambiguate trait refs.</summary>
    public bool HasExplicitArgs { get; set; }
}

/// <summary>
/// One item inside an explicit generic argument list, e.g. <c>int</c>,
/// <c>int: Ord[int]</c>, <c>Vector: MaxByX, MinByX</c>.
/// An item with no <see cref="Type"/> contributes additional implementation
/// requirements to the preceding item.
/// </summary>
public sealed class TypeArgumentSyntax : AstNode
{
    public TypeRef? Type { get; set; }
    public List<ImplRefSyntax> Impls { get; set; } = new();
}

/// <summary>
/// A type argument in an impl header, e.g. <c>Vector: PartialOrdByX[Vector]</c>.
/// </summary>
public sealed class TraitArgSyntax : AstNode
{
    public required TypeRef Type { get; set; }
    public List<ImplRefSyntax> Impls { get; set; } = new();
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
    public List<TypeParameterSyntax> TypeParams { get; set; } = new();
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
    public List<TypeParameterSyntax> TypeParams { get; set; } = new();
    public List<(string Name, TypeRef Type)> Cases { get; set; } = new();
}

public sealed class OpDecl : Stmt
{
    public DeclPattern? Lhs { get; set; }
    public required string Name { get; set; }
    public DeclPattern? Rhs { get; set; }
    public List<TypeParameterSyntax> TypeParams { get; set; } = new();
    public TypeRef? LhsType { get; set; }
    public TypeRef? RhsType { get; set; }
    public TypeRef? ResultType { get; set; }
    public required Expr Body { get; set; }
    /// <summary>True for trait member signatures ending in <c>;</c> (no default body).</summary>
    public bool IsDeclarationOnly { get; set; }
    /// <summary>Optional <c>Trait::</c> / <c>Impl::</c> prefix on a member declaration.</summary>
    public string? Qualifier { get; set; }
    public bool IsBinary => Lhs != null;
}

public sealed class TraitDecl : Stmt
{
    public required string Name { get; set; }
    public List<TypeParameterSyntax> TypeParams { get; set; } = new();
    public List<OpDecl> Members { get; set; } = new();
}

public sealed class ImplDecl : Stmt
{
    public string? Name { get; set; }
    public string? TraitName { get; set; }
    public List<TraitArgSyntax> TraitArgs { get; set; } = new();
    public List<TypeParameterSyntax> TypeParams { get; set; } = new();
    public List<OpDecl> Members { get; set; } = new();
    public string? AliasOf { get; set; }
}

public sealed class TypeAliasDecl : Stmt
{
    public required string Name { get; set; }
    public List<TypeParameterSyntax> TypeParams { get; set; } = new();
    public required TypeRef Definition { get; set; }
}

public sealed class ConvertDecl : Stmt
{
    public required bool Implicit { get; set; }
    public required DeclPattern Pattern { get; set; }
    public List<TypeParameterSyntax> TypeParams { get; set; } = new();
    public required TypeRef FromType { get; set; }
    public required TypeRef ToType { get; set; }
    public required Expr Body { get; set; }
}

public sealed class GetDecl : Stmt
{
    public required DeclPattern Self { get; set; }
    public string? PropertyName { get; set; }
    public DeclPattern? Index { get; set; }
    public List<TypeParameterSyntax> TypeParams { get; set; } = new();
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
    public List<TypeParameterSyntax> TypeParams { get; set; } = new();
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
    public List<TypeParameterSyntax> TypeParams { get; set; } = new();
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
    /// <summary>Explicit generic arguments, e.g. <c>max[int]</c> or <c>sum[Vector: OrdByX]</c>.</summary>
    public List<TypeArgumentSyntax> TypeArguments { get; set; } = new();
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
    /// <summary>Generic arguments on the qualifier, e.g. <c>VectorSpace[float, Vector]::*</c>.</summary>
    public List<TypeArgumentSyntax> TypeArguments { get; set; } = new();
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

/// <summary>Literal with glued suffix, e.g. <c>2.0i</c> / <c>42suf</c>.</summary>
public sealed class LiteralSuffixExpr : Expr
{
    public required Expr Value { get; set; }
    public required string Suffix { get; set; }
}

/// <summary>
/// Explicit construction of a dynamic value: <c>value[Impl0, Impl1]</c>.
/// When the implementation list is empty this is an implicit dyn conversion.
/// </summary>
public sealed class DynWrapExpr : Expr
{
    public required Expr Value { get; set; }
    public List<ImplRefSyntax> Impls { get; set; } = new();
}

/// <summary>Projection of a type parameter from a dynamic value: <c>value::T</c>.</summary>
public sealed class TypeProjectionExpr : Expr
{
    public required Expr Value { get; set; }
    public required string Member { get; set; }
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
    public List<TypeParameterSyntax> TypeParams { get; set; } = new();
    public List<TraitRequirementSyntax> Requirements { get; set; } = new();
    public required TypeRef Inner { get; set; }
}
