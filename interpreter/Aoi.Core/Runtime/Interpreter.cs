using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Aoi.Interpreter.Ast;
using Aoi.Interpreter.Semantics;
using Environment = Aoi.Interpreter.Runtime.Environment;

namespace Aoi.Interpreter.Runtime;

public sealed class InterpreterOptions
{
    public int MaxCallDepth { get; init; } = 1_024;
    public long MaxSteps { get; init; } = 50_000_000;
}

public sealed class Interpreter
{
    private Environment _env = new();
    private readonly SemanticModel _model = new();
    private readonly OperatorResolver _resolver;
    private readonly TextWriter _output;
    private readonly int _maxCallDepth;
    private readonly long _maxSteps;
    private int _callDepth;
    private long _steps;

    public SemanticModel Model => _model;

    public Interpreter(TextWriter? output = null, InterpreterOptions? options = null)
    {
        _output = output ?? Console.Out;
        options ??= new InterpreterOptions();
        _maxCallDepth = Math.Max(1, options.MaxCallDepth);
        _maxSteps = Math.Max(1, options.MaxSteps);
        _resolver = new OperatorResolver(_model);
    }

    public void Execute(ProgramNode program)
    {
        _callDepth = 0;
        _steps = 0;
        _model.AddProgram(program);
        foreach (var stmt in program.Statements)
        {
            if (IsDeclaration(stmt)) continue;
            ExecStmt(stmt);
        }
    }

    private static bool IsDeclaration(Stmt stmt) => stmt is
        StructDecl or EnumDecl or UnionDecl or OpDecl or TraitDecl or ImplDecl
        or TypeAliasDecl or ConvertDecl or GetDecl or SetDecl or LiteralSuffixDecl;

    private void Tick()
    {
        if (++_steps > _maxSteps)
            throw new AoiException($"Execution limit exceeded ({_maxSteps} steps). Possible infinite loop.");
        if ((_steps & 0x3F) == 0) EnsureStack();
    }

    private void EnterCall()
    {
        if (_callDepth >= _maxCallDepth)
            throw new AoiException($"Stack overflow: call depth exceeded {_maxCallDepth}.");
        EnsureStack();
        _callDepth++;
    }

    private void LeaveCall() => _callDepth--;

    private static void EnsureStack()
    {
        try { RuntimeHelpers.EnsureSufficientExecutionStack(); }
        catch (InsufficientExecutionStackException)
        {
            throw new AoiException("Stack overflow: insufficient execution stack.");
        }
    }

    private void ExecStmt(Stmt stmt)
    {
        Tick();
        switch (stmt)
        {
            case ExprStmt e:
                Eval(e.Expression);
                break;
            case LetStmt let:
            {
                var value = Eval(let.Value);
                if (let.Type != null)
                    value = CoerceValue(value, ResolveType(let.Type));
                BindDecl(let.Pattern, value, _env);
                break;
            }
            case BlockStmt block:
            {
                var prev = _env;
                _env = prev.CreateChild();
                try
                {
                    foreach (var s in block.Statements) ExecStmt(s);
                }
                finally { _env = prev; }
                break;
            }
            case IfStmt ifs:
            {
                if (ifs.MatchValue != null && ifs.MatchPattern != null)
                {
                    var val = Eval(ifs.MatchValue);
                    var child = _env.CreateChild();
                    if (MatchPattern(val, ifs.MatchPattern, child))
                    {
                        var prev = _env; _env = child;
                        try { ExecStmt(ifs.ThenBranch); }
                        finally { _env = prev; }
                    }
                    else if (ifs.ElseBranch != null)
                        ExecStmt(ifs.ElseBranch);
                }
                else
                {
                    var cond = Eval(ifs.Condition!);
                    if (IsTruthy(cond)) ExecStmt(ifs.ThenBranch);
                    else if (ifs.ElseBranch != null) ExecStmt(ifs.ElseBranch);
                }
                break;
            }
            case LoopStmt loop:
                while (true)
                {
                    try { ExecStmt(loop.Body); }
                    catch (AoiBreakException) { break; }
                    catch (AoiContinueException) { }
                }
                break;
            case WhileStmt wh:
                while (IsTruthy(Eval(wh.Condition)))
                {
                    try { ExecStmt(wh.Body); }
                    catch (AoiBreakException) { break; }
                    catch (AoiContinueException) { }
                }
                break;
            case ForStmt fr:
            {
                foreach (var item in Iterate(Eval(fr.Iterable)))
                {
                    var child = _env.CreateChild();
                    BindDecl(fr.Pattern, item, child);
                    var prev = _env; _env = child;
                    try { ExecStmt(fr.Body); }
                    catch (AoiBreakException) { _env = prev; break; }
                    catch (AoiContinueException) { _env = prev; continue; }
                    finally { _env = prev; }
                }
                break;
            }
            case WhenStmt whenStmt:
            {
                var val = Eval(whenStmt.Value);
                bool matched = false;
                foreach (var (pattern, branch) in whenStmt.Arms)
                {
                    var child = _env.CreateChild();
                    if (MatchPattern(val, pattern, child))
                    {
                        var prev = _env; _env = child;
                        try { ExecStmt(branch); }
                        finally { _env = prev; }
                        matched = true;
                        break;
                    }
                }
                if (!matched && whenStmt.ElseBranch != null)
                    ExecStmt(whenStmt.ElseBranch);
                break;
            }
            case TryStmt tryStmt:
            {
                try { ExecStmt(tryStmt.Body); }
                catch (AoiThrowException ex)
                {
                    bool caught = false;
                    foreach (var (pattern, branch) in tryStmt.Catches)
                    {
                        var child = _env.CreateChild();
                        if (MatchPattern(ex.Value, pattern, child))
                        {
                            var prev = _env; _env = child;
                            try { ExecStmt(branch); }
                            finally { _env = prev; }
                            caught = true;
                            break;
                        }
                    }
                    if (!caught) throw;
                }
                finally
                {
                    if (tryStmt.Finally != null) ExecStmt(tryStmt.Finally);
                }
                break;
            }
            case StructDecl or EnumDecl or UnionDecl or OpDecl or TraitDecl or ImplDecl
                or TypeAliasDecl or ConvertDecl or GetDecl or SetDecl or LiteralSuffixDecl:
                break; // declarations were collected by SemanticModel
            default:
                throw new AoiException($"Unsupported statement: {stmt.GetType().Name}", stmt.Line, stmt.Column);
        }
    }

    public AoiValue Eval(Expr expr)
    {
        Tick();
        switch (expr)
        {
            case IntExpr i: return new IntValue(i.Value);
            case FloatExpr f: return new FloatValue(f.Value);
            case BoolExpr b: return new BoolValue(b.Value);
            case CharExpr c: return new CharValue(c.Value);
            case StringExpr s: return new StrValue(s.Value);
            case TemplateExpr t: return EvalTemplate(t.Raw);
            case IdentExpr id: return LookupIdent(id);
            case UnitExpr: return UnitValue.Instance;
            case UnsetExpr: return UnsetValue.Instance;
            case NoneExpr: return new NullableValue(null);
            case SomeExpr some: return new NullableValue(Eval(some.Value));
            case BreakExpr: throw new AoiBreakException();
            case ContinueExpr: throw new AoiContinueException();
            case ReturnExpr ret: throw new AoiReturnException(ret.Value != null ? Eval(ret.Value) : UnitValue.Instance);
            case ThrowExpr th: throw new AoiThrowException(Eval(th.Value));
            case UnaryExpr u: return EvalUnary(u);
            case BinaryExpr b: return EvalBinary(b);
            case RangeExpr r:
                return new RangeValue(((IntValue)Eval(r.From)).Value, ((IntValue)Eval(r.To)).Value);
            case PipeExpr p:
            {
                var val = Eval(p.Value);
                var func = Eval(p.Func);
                return ApplyUnary(func, val);
            }
            case AssignExpr a:
            {
                var val = Eval(a.Value);
                AssignPattern(a.Pattern, val);
                return val;
            }
            case CompoundAssignExpr ca:
            {
                if (ca.Op == "??=")
                {
                    var cur = Eval(ca.Target);
                    if (cur is NullableValue nv && nv.IsNone)
                    {
                        var v = Eval(ca.Value);
                        AssignToExpr(ca.Target, v is NullableValue ? v : new NullableValue(v));
                        return v;
                    }
                    return cur is NullableValue n2 && n2.Value != null ? n2.Value : cur;
                }
                var left = Eval(ca.Target);
                var right = Eval(ca.Value);
                var result = EvalBinaryOp(ca.Op, left, right);
                AssignToExpr(ca.Target, result);
                return result;
            }
            case BlockExpr be:
            {
                var prev = _env;
                _env = prev.CreateChild();
                try
                {
                    foreach (var s in be.Statements) ExecStmt(s);
                    return Eval(be.Result);
                }
                finally { _env = prev; }
            }
            case IfExpr ie:
            {
                if (ie.MatchValue != null && ie.MatchPattern != null)
                {
                    var val = Eval(ie.MatchValue);
                    var child = _env.CreateChild();
                    if (MatchPattern(val, ie.MatchPattern, child))
                    {
                        var prev = _env; _env = child;
                        try { return Eval(ie.ThenBranch); }
                        finally { _env = prev; }
                    }
                    return Eval(ie.ElseBranch);
                }
                return IsTruthy(Eval(ie.Condition!)) ? Eval(ie.ThenBranch) : Eval(ie.ElseBranch);
            }
            case WhenExpr we:
            {
                var val = Eval(we.Value);
                foreach (var (pattern, branch) in we.Arms)
                {
                    var child = _env.CreateChild();
                    if (MatchPattern(val, pattern, child))
                    {
                        var prev = _env; _env = child;
                        try { return Eval(branch); }
                        finally { _env = prev; }
                    }
                }
                if (we.ElseBranch != null) return Eval(we.ElseBranch);
                throw new AoiException("Non-exhaustive when", we.Line, we.Column);
            }
            case TryExpr te:
            {
                try { return Eval(te.Body); }
                catch (AoiThrowException ex)
                {
                    foreach (var (pattern, branch) in te.Catches)
                    {
                        var child = _env.CreateChild();
                        if (MatchPattern(ex.Value, pattern, child))
                        {
                            var prev = _env; _env = child;
                            try { return Eval(branch); }
                            finally { _env = prev; }
                        }
                    }
                    throw;
                }
            }
            case TupleExpr te:
                return new TupleValue(te.Elements.Select(Eval).ToList());
            case MemberExpr me:
                return GetMember(Eval(me.Object), me.Member, me.Line, me.Column);
            case IndexExpr ix:
                return GetIndex(Eval(ix.Object), Eval(ix.Index), ix.Line, ix.Column);
            case SafeMemberExpr sm:
            {
                var obj = Eval(sm.Object);
                if (obj is NullableValue nv)
                {
                    if (nv.IsNone) return new NullableValue(null, nv.InnerType);
                    return new NullableValue(GetMember(nv.Value!, sm.Member, sm.Line, sm.Column), nv.InnerType);
                }
                return new NullableValue(GetMember(obj, sm.Member, sm.Line, sm.Column));
            }
            case SafeIndexExpr si:
            {
                var obj = Eval(si.Object);
                if (obj is NullableValue nv)
                {
                    if (nv.IsNone) return new NullableValue(null, nv.InnerType);
                    return new NullableValue(GetIndex(nv.Value!, Eval(si.Index), si.Line, si.Column), nv.InnerType);
                }
                return new NullableValue(GetIndex(obj, Eval(si.Index), si.Line, si.Column));
            }
            case NullAssertExpr na:
            {
                var v = Eval(na.Operand);
                if (v is NullableValue nv)
                {
                    if (nv.IsNone) throw new AoiThrowException(new ExceptionValue("NullAssertException", "Value is none"));
                    return nv.Value!;
                }
                return v;
            }
            case NewExpr ne: return EvalNew(ne);
            case SetExpr se: return EvalSet(se);
            case IsExpr ise:
                return new BoolValue(MatchPattern(Eval(ise.Value), ise.Pattern, _env.CreateChild()));
            case ClosureExpr ce: return MakeClosure(ce);
            case CastExpr cast:
            {
                var target = ResolveType(cast.Type);
                return DoCast(target, Eval(cast.Value));
            }
            case QualifiedExpr qe: return EvalQualified(qe);
            case GroupedOpExpr go: return MakeCurriedBinary(go.Op);
            case CallLikeExpr call: return EvalCallLike(call);
            case LiteralSuffixExpr ls:
                return EvalLiteralSuffix(ls);
            case DynWrapExpr dw:
                return WrapDyn(Eval(dw.Value), dw.Impls, dynType: null);
            case TypeProjectionExpr tp:
                return EvalTypeProjection(tp);
            default:
                throw new AoiException($"Unsupported expression: {expr.GetType().Name}", expr.Line, expr.Column);
        }
    }

    // --- generic operator invocation ---

    private AoiValue EvalUnary(UnaryExpr u)
    {
        var value = Eval(u.Operand);
        var context = EnvironmentForDyn(value);
        var resolved = _resolver.Resolve(u.Op, false, new[] { value.Type }, context, null, null, u.Line, u.Column);
        return InvokeResolved(resolved, null, value);
    }

    private AoiValue EvalBinary(BinaryExpr b)
    {
        if (b.Op is "&&" or "||" or "??")
        {
            var left = Eval(b.Left);
            if (b.Op == "&&") return new BoolValue(IsTruthy(left) && IsTruthy(Eval(b.Right)));
            if (b.Op == "||") return new BoolValue(IsTruthy(left) || IsTruthy(Eval(b.Right)));
            if (left is NullableValue nv)
                return nv.IsNone ? Eval(b.Right) : nv.Value!;
            return left;
        }

        var l = Eval(b.Left);
        var r = Eval(b.Right);
        return EvalBinaryOp(b.Op, l, r, b.Line, b.Column);
    }

    private AoiValue EvalBinaryOp(string op, AoiValue left, AoiValue right, int line = 0, int column = 0)
    {
        var context = EnvironmentForDyn(left, right);
        var resolved = _resolver.Resolve(op, true, new[] { left.Type, right.Type }, context, null, null, line, column);
        return InvokeResolved(resolved, left, right);
    }

    private AoiValue EvalCallLike(CallLikeExpr call)
    {
        var parts = call.Parts;
        if (parts.Count == 0) return UnitValue.Instance;
        if (parts.Count == 1) return Eval(parts[0]);
        if (parts.Count == 2) return ApplyJuxtaposed(parts[0], parts[1]);

        // Odd length: binary operators in the middle, folded left-to-right.
        if (parts.Count % 2 == 1 && IsBinaryOpExpr(parts[1]))
        {
            var result = Eval(parts[0]);
            for (int i = 1; i < parts.Count; i += 2)
            {
                var right = Eval(parts[i + 1]);
                result = ApplyBinaryOpExpr(parts[i], result, right);
            }
            return result;
        }

        // Even length ending with `lhs binOp rhs`: unary apps around a binary call.
        if (parts.Count >= 4 && parts.Count % 2 == 0 && IsBinaryOpExpr(parts[^2]))
        {
            var accBin = ApplyBinaryOpExpr(parts[^2], Eval(parts[^3]), Eval(parts[^1]));
            for (int i = parts.Count - 4; i >= 0; i--)
                accBin = ApplyJuxtaposedValue(parts[i], accBin);
            return accBin;
        }

        // Right-associative unary application: a b c => a (b c).
        AoiValue acc = Eval(parts[^1]);
        for (int i = parts.Count - 2; i >= 0; i--)
            acc = ApplyJuxtaposedValue(parts[i], acc);
        return acc;
    }

    private bool IsBinaryOpExpr(Expr expr)
    {
        string? name = expr switch
        {
            IdentExpr id => id.Name,
            GroupedOpExpr g => g.Op,
            QualifiedExpr q => q.Member,
            _ => null,
        };
        if (name == null) return false;
        if (name is "+" or "-" or "*" or "/" or "%" or "==" or "!=" or "<" or "<=" or ">" or ">="
            or "<<" or ">>" or "&" or "|" or "^" or "&&" or "||" or "??" or "|>" or ".." or "@")
            return true;
        if (_model.Operators.Any(o => o.Name == name && o.IsBinary)) return true;
        if (_model.Impls.SelectMany(i => i.Members).Any(o => o.Name == name && o.IsBinary)) return true;
        if (_model.Traits.Values.SelectMany(t => t.Members).Any(o => o.Name == name && o.IsBinary)) return true;
        if (_env.TryGet(name, out var bound) && bound is OperatorValue ov && ov.IsBinary) return true;
        return false;
    }

    private AoiValue ApplyBinaryOpExpr(Expr opExpr, AoiValue left, AoiValue right)
    {
        var context = EnvironmentForDyn(left, right);
        ResolvedOperator resolved;
        if (opExpr is IdentExpr id)
        {
            if (_env.TryGet(id.Name, out var binding) && binding is OperatorValue localOp)
                return localOp.Invoke(this, left, right);
            resolved = _resolver.Resolve(id.Name, true, new[] { left.Type, right.Type }, context,
                id.TypeArguments.Count > 0 ? id.TypeArguments : null, null, opExpr.Line, opExpr.Column);
        }
        else if (opExpr is QualifiedExpr q)
        {
            resolved = _resolver.Resolve(q.Member, true, new[] { left.Type, right.Type }, context,
                q.TypeArguments.Count > 0 ? q.TypeArguments : null, q.TypeName, opExpr.Line, opExpr.Column);
        }
        else if (opExpr is GroupedOpExpr g)
        {
            resolved = _resolver.Resolve(g.Op, true, new[] { left.Type, right.Type }, context, null, null, opExpr.Line, opExpr.Column);
        }
        else
        {
            var opVal = Eval(opExpr);
            if (opVal is OperatorValue ov) return ov.Invoke(this, left, right);
            throw new AoiException("Expected operator", opExpr.Line, opExpr.Column);
        }
        return InvokeResolved(resolved, left, right);
    }

    private AoiValue ApplyJuxtaposed(Expr opExpr, Expr argExpr)
    {
        if (opExpr is IdentExpr id)
        {
            if (_model.GetType(id.Name) != null || id.Name is "int" or "float" or "str" or "bool" or "char")
            {
                AoiType target = id.Name is "int" or "float" or "str" or "bool" or "char"
                    ? PrimitiveType.ByName(id.Name)!
                    : new NamedType(id.Name, Array.Empty<AoiType>(), _model.GetType(id.Name));
                return DoCast(target, Eval(argExpr));
            }
        }
        return ApplyJuxtaposedValue(opExpr, Eval(argExpr));
    }

    private AoiValue ApplyJuxtaposedValue(Expr opExpr, AoiValue arg)
    {
        if (opExpr is IdentExpr id)
        {
            if (_env.TryGet(id.Name, out var bound) && bound is OperatorValue ov)
                return ApplyUnary(ov, arg);

            // `value[Impl]` parses as an identifier with type arguments; when
            // the identifier is a variable this is an explicit dyn wrap.
            if (id.TypeArguments.Count > 0 && _env.TryGet(id.Name, out var existing))
                return WrapDyn(existing!, ImplRefsFromTypeArguments(id.TypeArguments), null);

            if (id.Name is "printLine" or "print" or "toStr")
                return InvokeBuiltinUnary(id.Name, arg);

            var context = EnvironmentForDyn(arg);
            try
            {
                var resolved = _resolver.Resolve(id.Name, false, new[] { arg.Type }, context,
                    id.TypeArguments.Count > 0 ? id.TypeArguments : null, null, opExpr.Line, opExpr.Column);
                return InvokeResolved(resolved, null, arg);
            }
            catch (AoiException)
            {
                // A binary-only operator used with one operand: curry it.
                var binary = _model.Operators.LastOrDefault(o => o.Name == id.Name && o.IsBinary);
                if (binary != null)
                {
                    return new OperatorValue(id.Name, false, (interp, _, left) =>
                        interp.EvalBinaryOp(id.Name, left, arg, opExpr.Line, opExpr.Column));
                }
                throw;
            }
        }
        if (opExpr is GroupedOpExpr g)
            return ApplyUnary(MakeCurriedBinary(g.Op), arg);
        if (opExpr is QualifiedExpr q)
        {
            var opVal = EvalQualified(q);
            if (opVal is OperatorValue qv) return ApplyUnary(qv, arg);
            return opVal;
        }

        var value = Eval(opExpr);
        return ApplyUnary(value, arg);
    }

    private static List<ImplRefSyntax> ImplRefsFromTypeArguments(IReadOnlyList<TypeArgumentSyntax> arguments)
    {
        var refs = new List<ImplRefSyntax>();
        foreach (var argument in arguments)
        {
            refs.AddRange(argument.Impls);
            if (argument.Type is NamedTypeRef named && argument.Impls.Count == 0)
                refs.Add(new ImplRefSyntax { Name = named.Name });
        }
        return refs;
    }

    private AoiValue LookupIdent(IdentExpr id)
    {
        if (_env.TryGet(id.Name, out var value))
        {
            if (id.TypeArguments.Count > 0)
                return WrapDyn(value!, ImplRefsFromTypeArguments(id.TypeArguments), null);
            return value!;
        }

        if (id.Name is "printLine" or "print" or "toStr")
            return new OperatorValue(id.Name, false, (interp, _, arg) => interp.InvokeBuiltinUnary(id.Name, arg));

        // An operator used as a value. Determine its arity from the
        // declarations; default to unary when both exist so juxtaposition works.
        bool hasUnary = _model.Operators.Any(o => o.Name == id.Name && !o.IsBinary)
            || _model.Impls.SelectMany(i => i.Members).Any(o => o.Name == id.Name && !o.IsBinary)
            || _model.Traits.Values.SelectMany(t => t.Members).Any(o => o.Name == id.Name && !o.IsBinary);
        bool hasBinary = _model.Operators.Any(o => o.Name == id.Name && o.IsBinary)
            || _model.Impls.SelectMany(i => i.Members).Any(o => o.Name == id.Name && o.IsBinary)
            || _model.Traits.Values.SelectMany(t => t.Members).Any(o => o.Name == id.Name && o.IsBinary);
        if (hasUnary || hasBinary)
        {
            var binary = !hasUnary && hasBinary;
            return new OperatorValue(id.Name, binary, (interp, left, right) =>
            {
                if (binary) return interp.EvalBinaryOp(id.Name, left!, right);
                return interp.EvalUnaryOp(id.Name, right, id.TypeArguments);
            });
        }

        throw new AoiException($"Undefined '{id.Name}'", id.Line, id.Column);
    }

    private AoiValue EvalUnaryOp(string name, AoiValue operand, IReadOnlyList<TypeArgumentSyntax>? typeArgs)
    {
        var context = EnvironmentForDyn(operand);
        var resolved = _resolver.Resolve(name, false, new[] { operand.Type }, context,
            typeArgs is { Count: > 0 } ? typeArgs : null, null, 0, 0);
        return InvokeResolved(resolved, null, operand);
    }

    private AoiValue InvokeResolved(ResolvedOperator resolved, AoiValue? left, AoiValue right, AoiValue? index = null)
    {
        if (resolved.IsBuiltin)
            return InvokeBuiltin(resolved.BuiltinKey ?? resolved.Name, resolved.IsBinary, left, right, index, resolved.Line, resolved.Column);
        return InvokeOperator(resolved, left, right, index);
    }

    private AoiValue InvokeOperator(ResolvedOperator resolved, AoiValue? left, AoiValue right, AoiValue? index = null)
    {
        var op = resolved.Definition
            ?? throw new AoiException($"Missing operator definition for '{resolved.Name}'", resolved.Line, resolved.Column);

        EnterCall();
        var prev = _env;
        _env = prev.CreateChild();
        try
        {
            // Type parameters of the operator itself.
            foreach (var (name, type) in resolved.TypeBindings)
            {
                _env.DefineTypeName(name, type);
                if (op.TypeParams.FirstOrDefault(p => p.Name == name) is { } parameter)
                    _env.DefineType(parameter.Variable, type);
            }

            // Impl parameters and trait parameters, when present.
            if (resolved.Impl is { } binding && binding.Impl != null)
            {
                foreach (var parameter in binding.Impl.TypeParams)
                {
                    if (binding.Substitution.TryGet(parameter.Variable, out var type))
                        _env.DefineTypeName(parameter.Name, binding.Substitution.Apply(type));
                }
                _env.AddImpl(binding);
            }
            foreach (var dependency in resolved.Dependencies)
                _env.AddImpl(dependency);
            if (op.Trait != null && resolved.Impl is { } impl)
            {
                for (int i = 0; i < op.Trait.TypeParams.Count && i < impl.TraitArgs.Count; i++)
                    _env.DefineTypeName(op.Trait.TypeParams[i].Name, impl.TraitArgs[i]);
            }

            switch (op.Kind)
            {
                case OperatorKind.Getter:
                    if (op.Rhs != null) BindDecl(op.Rhs, right, _env);
                    break;
                case OperatorKind.Setter:
                    if (op.Lhs != null && left != null) BindDecl(op.Lhs, left, _env);
                    if (op.Index != null && index != null) BindDecl(op.Index, index, _env);
                    if (op.Rhs != null) BindDecl(op.Rhs, right, _env);
                    break;
                case OperatorKind.IndexGetter:
                    // Invocation convention: left = self, right = index.
                    if (op.Rhs != null) BindDecl(op.Rhs, left ?? right, _env);
                    if (op.Index != null) BindDecl(op.Index, index ?? right, _env);
                    break;
                case OperatorKind.IndexSetter:
                    if (op.Lhs != null && left != null) BindDecl(op.Lhs, left, _env);
                    if (op.Index != null && index != null) BindDecl(op.Index, index, _env);
                    if (op.Rhs != null) BindDecl(op.Rhs, right, _env);
                    break;
                case OperatorKind.Convert:
                case OperatorKind.Literal:
                    if (op.Rhs != null) BindDecl(op.Rhs, right, _env);
                    break;
                default:
                    if (op.Lhs != null && left != null) BindDecl(op.Lhs, left, _env);
                    if (op.Rhs != null) BindDecl(op.Rhs, right, _env);
                    break;
            }

            try { return Eval(op.Body); }
            catch (AoiReturnException ret) { return ret.Value; }
        }
        finally
        {
            _env = prev;
            LeaveCall();
        }
    }

    private AoiValue InvokeBuiltinUnary(string name, AoiValue operand)
    {
        if (name == "printLine")
        {
            _output.WriteLine(operand.ToString());
            return UnitValue.Instance;
        }
        if (name == "print")
        {
            _output.Write(operand.ToString());
            return UnitValue.Instance;
        }
        if (name == "toStr")
            return new StrValue(UnwrapDyn(operand).ToString() ?? "");
        switch (name)
        {
            case "+" when operand is IntValue or FloatValue: return operand;
            case "-" when operand is IntValue i: return new IntValue(-i.Value);
            case "-" when operand is FloatValue f: return new FloatValue(-f.Value);
            case "!" when operand is BoolValue b: return new BoolValue(!b.Value);
            case "~" when operand is IntValue i: return new IntValue(~i.Value);
            case "~" when operand is BoolValue b: return new BoolValue(!b.Value);
        }
        throw new AoiException($"Unknown builtin unary '{name}'");
    }

    private AoiValue InvokeBuiltin(string name, bool binary, AoiValue? left, AoiValue right, AoiValue? index, int line, int column)
    {
        if (!binary)
            return InvokeBuiltinUnary(name, right);

        if (left == null) throw new AoiException($"Binary operator '{name}' requires two operands", line, column);

        switch (name)
        {
            case "==": return new BoolValue(EqualsValue(left, right));
            case "!=": return new BoolValue(!EqualsValue(left, right));
            case "+" when left is IntValue li && right is IntValue ri: return new IntValue(li.Value + ri.Value);
            case "+" when left is FloatValue lf && right is FloatValue rf: return new FloatValue(lf.Value + rf.Value);
            case "+" when left is IntValue li2 && right is FloatValue rf2: return new FloatValue(li2.Value + rf2.Value);
            case "+" when left is FloatValue lf2 && right is IntValue ri2: return new FloatValue(lf2.Value + ri2.Value);
            case "+" when left is StrValue ls && right is StrValue rs: return new StrValue(ls.Value + rs.Value);
            case "+" when left is StrValue ls2 && right is CharValue rc: return new StrValue(ls2.Value + rc.Value);
            case "+" when left is CharValue lc && right is IntValue ri3: return new CharValue((char)(lc.Value + ri3.Value));
            case "-" when left is IntValue li && right is IntValue ri: return new IntValue(li.Value - ri.Value);
            case "-" when left is FloatValue lf && right is FloatValue rf: return new FloatValue(lf.Value - rf.Value);
            case "-" when left is IntValue li2 && right is FloatValue rf2: return new FloatValue(li2.Value - rf2.Value);
            case "-" when left is FloatValue lf2 && right is IntValue ri2: return new FloatValue(lf2.Value - ri2.Value);
            case "-" when left is CharValue lc && right is IntValue ri3: return new CharValue((char)(lc.Value - ri3.Value));
            case "*" when left is IntValue li && right is IntValue ri: return new IntValue(li.Value * ri.Value);
            case "*" when left is FloatValue lf && right is FloatValue rf: return new FloatValue(lf.Value * rf.Value);
            case "*" when left is IntValue li2 && right is FloatValue rf2: return new FloatValue(li2.Value * rf2.Value);
            case "*" when left is FloatValue lf2 && right is IntValue ri2: return new FloatValue(lf2.Value * ri2.Value);
            case "/" when left is IntValue li && right is IntValue ri:
                if (ri.Value == 0) throw new AoiException("Division by zero", line, column);
                return new IntValue(li.Value / ri.Value);
            case "/" when left is FloatValue lf && right is FloatValue rf: return new FloatValue(lf.Value / rf.Value);
            case "/" when left is IntValue li2 && right is FloatValue rf2: return new FloatValue(li2.Value / rf2.Value);
            case "/" when left is FloatValue lf2 && right is IntValue ri2: return new FloatValue(lf2.Value / ri2.Value);
            case "%" when left is IntValue li && right is IntValue ri:
                if (ri.Value == 0) throw new AoiException("Division by zero", line, column);
                return new IntValue(li.Value % ri.Value);
            case "%" when left is FloatValue lf && right is FloatValue rf: return new FloatValue(lf.Value % rf.Value);
            case "<<" when left is IntValue li && right is IntValue ri: return new IntValue(li.Value << (int)ri.Value);
            case ">>" when left is IntValue li && right is IntValue ri: return new IntValue(li.Value >> (int)ri.Value);
            case "&" when left is IntValue li && right is IntValue ri: return new IntValue(li.Value & ri.Value);
            case "&" when left is BoolValue lb && right is BoolValue rb: return new BoolValue(lb.Value & rb.Value);
            case "|" when left is IntValue li && right is IntValue ri: return new IntValue(li.Value | ri.Value);
            case "|" when left is BoolValue lb && right is BoolValue rb: return new BoolValue(lb.Value | rb.Value);
            case "^" when left is IntValue li && right is IntValue ri: return new IntValue(li.Value ^ ri.Value);
            case "^" when left is BoolValue lb && right is BoolValue rb: return new BoolValue(lb.Value ^ rb.Value);
            case "<" when left is IntValue li && right is IntValue ri: return new BoolValue(li.Value < ri.Value);
            case "<" when left is FloatValue lf && right is FloatValue rf: return new BoolValue(lf.Value < rf.Value);
            case "<" when left is CharValue lc && right is CharValue rc: return new BoolValue(lc.Value < rc.Value);
            case "<" when left is StrValue ls && right is StrValue rs: return new BoolValue(string.CompareOrdinal(ls.Value, rs.Value) < 0);
            case "<=" when left is IntValue li && right is IntValue ri: return new BoolValue(li.Value <= ri.Value);
            case "<=" when left is FloatValue lf && right is FloatValue rf: return new BoolValue(lf.Value <= rf.Value);
            case "<=" when left is CharValue lc && right is CharValue rc: return new BoolValue(lc.Value <= rc.Value);
            case "<=" when left is StrValue ls && right is StrValue rs: return new BoolValue(string.CompareOrdinal(ls.Value, rs.Value) <= 0);
            case ">" when left is IntValue li && right is IntValue ri: return new BoolValue(li.Value > ri.Value);
            case ">" when left is FloatValue lf && right is FloatValue rf: return new BoolValue(lf.Value > rf.Value);
            case ">" when left is CharValue lc && right is CharValue rc: return new BoolValue(lc.Value > rc.Value);
            case ">" when left is StrValue ls && right is StrValue rs: return new BoolValue(string.CompareOrdinal(ls.Value, rs.Value) > 0);
            case ">=" when left is IntValue li && right is IntValue ri: return new BoolValue(li.Value >= ri.Value);
            case ">=" when left is FloatValue lf && right is FloatValue rf: return new BoolValue(lf.Value >= rf.Value);
            case ">=" when left is CharValue lc && right is CharValue rc: return new BoolValue(lc.Value >= rc.Value);
            case ">=" when left is StrValue ls && right is StrValue rs: return new BoolValue(string.CompareOrdinal(ls.Value, rs.Value) >= 0);
            case "+" or "-" or "!" or "~":
                break;
        }

        // Unary names accidentally routed here.
        if (name is "!" or "~" or "toStr" or "printLine" or "print")
            return InvokeBuiltinUnary(name, right);

        throw new AoiException($"No built-in operator '{name}' for {left.Type} and {right.Type}", line, column);
    }

    private AoiValue ApplyUnary(AoiValue func, AoiValue arg)
    {
        if (func is OperatorValue op) return op.Invoke(this, null, arg);
        if (func is TypeValue tv) return DoCast(tv.Value, arg);
        throw new AoiException($"Cannot apply non-operator {func.Type}");
    }

    private AoiValue MakeCurriedBinary(string op)
    {
        return new OperatorValue(op, false, (interp, _, right) =>
            new OperatorValue(op, false, (interp2, _, left) =>
                interp2.EvalBinaryOp(op, left, right)));
    }

    private AoiValue MakeClosure(ClosureExpr ce)
    {
        var captured = _env;
        if (ce.IsBinary)
        {
            return new OperatorValue("closure", true, (interp, left, right) =>
            {
                interp.EnterCall();
                var prev = interp._env;
                interp._env = new Environment(captured);
                try
                {
                    interp.BindDecl(ce.Lhs!, left!, interp._env);
                    interp.BindDecl(ce.Rhs, right, interp._env);
                    try { return interp.Eval(ce.Body); }
                    catch (AoiReturnException ret) { return ret.Value; }
                }
                finally
                {
                    interp._env = prev;
                    interp.LeaveCall();
                }
            });
        }
        return new OperatorValue("closure", false, (interp, _, right) =>
        {
            interp.EnterCall();
            var prev = interp._env;
            interp._env = new Environment(captured);
            try
            {
                interp.BindDecl(ce.Rhs, right, interp._env);
                try { return interp.Eval(ce.Body); }
                catch (AoiReturnException ret) { return ret.Value; }
            }
            finally
            {
                interp._env = prev;
                interp.LeaveCall();
            }
        });
    }

    // --- values / construction ---

    private AoiType ResolveType(TypeRef type)
        => _model.Resolver.Resolve(type, _env.TypeNameScope());

    private AoiValue EvalNew(NewExpr ne)
    {
        if (ne.Type is NamedTypeRef nt && nt.Name == "arr")
            return EvalNewArray(ne, nt);

        var typeRef = ne.Type as NamedTypeRef
            ?? throw new AoiException("new requires a named type", ne.Line, ne.Column);

        var fields = new Dictionary<string, AoiValue>(StringComparer.Ordinal);
        foreach (var (name, valueExpr, _, _) in ne.Elements)
        {
            if (name == null) throw new AoiException("Field name required in new", ne.Line, ne.Column);
            fields[name] = Eval(valueExpr);
        }

        var definition = _model.GetType(typeRef.Name);
        if (definition == null)
        {
            var fallback = new NamedType(typeRef.Name,
                typeRef.Args.Select(a => ResolveType(a)));
            return new StructValue(fallback, fields, isClass: false);
        }

        var scope = new Dictionary<string, AoiType>(StringComparer.Ordinal);
        var substitution = new TypeSubstitution();
        var args = new List<AoiType>();

        if (typeRef.Args.Count > 0)
        {
            if (typeRef.Args.Count != definition.TypeParams.Count)
                throw new AoiException(
                    $"Type '{definition.Name}' expects {definition.TypeParams.Count} argument(s), got {typeRef.Args.Count}",
                    ne.Line, ne.Column);
            for (int i = 0; i < definition.TypeParams.Count; i++)
            {
                var arg = ResolveType(typeRef.Args[i]);
                args.Add(arg);
                scope[definition.TypeParams[i].Name] = arg;
                substitution.Bind(definition.TypeParams[i].Variable, arg);
            }
        }
        else
        {
            for (int i = 0; i < definition.TypeParams.Count; i++)
            {
                var fresh = TypeVar.Fresh(definition.TypeParams[i].Name);
                args.Add(fresh);
                scope[definition.TypeParams[i].Name] = fresh;
                substitution.Bind(definition.TypeParams[i].Variable, fresh);
            }
        }

        // Infer generic arguments from field initializers.
        foreach (var (fieldName, fieldTypeRef) in definition.Fields)
        {
            if (!fields.TryGetValue(fieldName, out var fieldValue)) continue;
            var declared = _model.Resolver.Resolve(fieldTypeRef, scope).Substitute(substitution);
            TypeUnifier.TryUnify(declared, fieldValue.Type, substitution);
        }

        var concreteArgs = args.Select(a => a.Substitute(substitution)).ToList();
        var finalType = new NamedType(definition.Name, concreteArgs, definition);
        var concreteScope = new Dictionary<string, AoiType>(StringComparer.Ordinal);
        for (int i = 0; i < definition.TypeParams.Count; i++)
            concreteScope[definition.TypeParams[i].Name] = concreteArgs[i];

        ValidateTypeArguments(definition, concreteScope, ne);

        var coerced = new Dictionary<string, AoiValue>(StringComparer.Ordinal);
        foreach (var (fieldName, fieldValue) in fields)
        {
            var fieldDecl = definition.Fields.FirstOrDefault(f => f.Name == fieldName);
            coerced[fieldName] = fieldDecl.Type == null
                ? fieldValue
                : CoerceValue(fieldValue, _model.Resolver.Resolve(fieldDecl.Type, concreteScope));
        }

        return new StructValue(finalType, coerced, definition.IsClass);
    }

    private void ValidateTypeArguments(TypeDefinition definition, Dictionary<string, AoiType> scope, NewExpr site)
    {
        foreach (var parameter in definition.TypeParams)
        {
            foreach (var requirementSyntax in parameter.RequirementSyntax)
            {
                var requirement = new TraitRequirement(
                    requirementSyntax.TraitName,
                    requirementSyntax.Args.Select(a => _model.Resolver.Resolve(a, scope)));
                if (!requirement.Args.All(a => a.IsConcrete)) continue;
                try
                {
                    _model.TraitEngine.FindImpl(
                        requirement.TraitName, requirement.Args, null,
                        new TypeSubstitution(), _env.Impls, site.Line, site.Column);
                }
                catch (AoiException ex)
                {
                    throw new AoiException(
                        $"Type argument for '{parameter.Name}' does not satisfy {requirement}: {ex.Message}",
                        site.Line, site.Column);
                }
            }
        }
    }

    private AoiValue EvalNewArray(NewExpr ne, NamedTypeRef nt)
    {
        AoiType? elementType = null;
        if (nt.Args.Count > 0)
            elementType = ResolveType(nt.Args[0]);

        var values = new List<AoiValue>();
        foreach (var (_, valueExpr, repeatExpr, _) in ne.Elements)
        {
            var value = Eval(valueExpr);
            long times = repeatExpr != null ? ((IntValue)Eval(repeatExpr)).Value : 1;
            for (long i = 0; i < times; i++) values.Add(value.Copy());
        }

        if (elementType == null || elementType is TypeVar || elementType is UnknownType)
        {
            var subst = new TypeSubstitution();
            if (elementType != null)
            {
                foreach (var value in values) TypeUnifier.TryUnify(elementType, value.Type, subst);
                elementType = elementType.Substitute(subst);
            }
            if (elementType == null || !elementType.IsConcrete)
            {
                elementType = values.Count > 0
                    ? values.Select(v => v.Type).Aggregate((a, b) => TypeUnifier.CanUnify(a, b) ? a : UnknownType.Instance)
                    : UnknownType.Instance;
            }
        }

        var coerced = values.Select(v => CoerceValue(v, elementType)).ToList();
        return new ArrayValue(elementType, coerced);
    }

    private AoiValue EvalSet(SetExpr se)
    {
        var target = Eval(se.Target);
        if (target is ArrayValue arr)
        {
            int pos = 0;
            foreach (var (_, valueExpr, repeatExpr, indexExpr) in se.Elements)
            {
                var v = Eval(valueExpr);
                long times = repeatExpr != null ? ((IntValue)Eval(repeatExpr)).Value : 1;
                if (indexExpr != null) pos = (int)((IntValue)Eval(indexExpr)).Value;
                for (long i = 0; i < times; i++)
                {
                    if (pos < arr.Elements.Count)
                        arr.Elements[pos] = CoerceValue(v.Copy(), arr.ElementType);
                    pos++;
                }
            }
            return arr;
        }
        if (target is StructValue sv)
        {
            foreach (var (name, valueExpr, _, _) in se.Elements)
            {
                if (name == null) continue;
                sv.Fields[name] = CoerceMember(sv, name, Eval(valueExpr));
            }
            return sv;
        }
        throw new AoiException("set target must be array or struct", se.Line, se.Column);
    }

    private AoiValue EvalLiteralSuffix(LiteralSuffixExpr ls)
    {
        var value = Eval(ls.Value);
        var resolved = _resolver.ResolveLiteral(ls.Suffix, value.Type, _env, ls.Line, ls.Column)
            ?? throw new AoiException($"Unknown literal suffix '{ls.Suffix}'", ls.Line, ls.Column);
        return InvokeOperator(resolved, null, value);
    }

    // --- members / indexing ---

    private AoiValue GetMember(AoiValue obj, string member, int line = 0, int column = 0)
    {
        if (obj is TupleValue tv && int.TryParse(member, out int idx))
        {
            if (idx < 0 || idx >= tv.Elements.Count)
                throw new AoiThrowException(new ExceptionValue("IndexOutOfRangeException", idx.ToString()));
            return tv.Elements[idx];
        }
        if (obj is ArrayValue arr && member == "length") return new IntValue(arr.Elements.Count);
        if (obj is SpanValue span && member == "length") return new IntValue(span.Length);
        if (obj is StrValue str && member == "length") return new IntValue(str.Value.Length);
        if (obj is StructValue sv && sv.Fields.TryGetValue(member, out var field))
        {
            if (field is UnsetValue) throw new AoiException($"Field '{member}' is unset", line, column);
            return field;
        }
        if (obj is DynValue dyn)
        {
            // First try a trait getter through the dyn vtable, then fall back to
            // the underlying value's members.
            var context = EnvironmentForDyn(obj);
            var dynGetter = _resolver.ResolveGetter(member, dyn.DynType, context, line, column);
            if (dynGetter != null) return InvokeResolved(dynGetter, null, obj);
            return GetMember(dyn.Inner, member, line, column);
        }

        var getter = _resolver.ResolveGetter(member, obj.Type, _env, line, column);
        if (getter != null) return InvokeResolved(getter, null, obj);

        throw new AoiException($"No member '{member}' on {obj.Type}", line, column);
    }

    private AoiValue CoerceMember(StructValue sv, string member, AoiValue value)
    {
        if (sv.StructType.Definition == null) return value;
        var field = sv.StructType.Definition.Fields.FirstOrDefault(f => f.Name == member);
        if (field.Type == null) return value;
        var scope = new Dictionary<string, AoiType>(StringComparer.Ordinal);
        for (int i = 0; i < sv.StructType.Definition.TypeParams.Count && i < sv.StructType.Args.Length; i++)
            scope[sv.StructType.Definition.TypeParams[i].Name] = sv.StructType.Args[i];
        var expected = _model.Resolver.Resolve(field.Type, scope);
        return CoerceValue(value, expected);
    }

    private AoiValue? SetMember(AoiValue obj, string member, AoiValue value, int line = 0, int column = 0)
    {
        if (obj is TupleValue tv && int.TryParse(member, out int idx))
        {
            tv.Elements[idx] = value;
            return obj;
        }
        if (obj is StructValue sv && sv.Fields.ContainsKey(member))
        {
            sv.Fields[member] = CoerceMember(sv, member, value);
            return obj;
        }
        if (obj is DynValue dyn)
        {
            var context = EnvironmentForDyn(dyn);
            var dynSetter = _resolver.ResolveSetter(member, dyn.DynType, value.Type, context, line, column);
            if (dynSetter != null) return InvokeResolved(dynSetter, obj, value);
            return SetMember(dyn.Inner, member, value, line, column);
        }

        var setter = _resolver.ResolveSetter(member, obj.Type, value.Type, _env, line, column);
        if (setter != null) return InvokeResolved(setter, obj, value);

        if (obj is StructValue sv3)
        {
            sv3.Fields[member] = value;
            return obj;
        }
        throw new AoiException($"Cannot set member '{member}' on {obj.Type}", line, column);
    }

    private AoiValue GetIndex(AoiValue obj, AoiValue index, int line = 0, int column = 0)
    {
        if (obj is ArrayValue arr)
        {
            if (index is RangeValue range)
                return new SpanValue(arr, (int)range.From, (int)(range.To - range.From));
            int i = (int)((IntValue)index).Value;
            if (i < 0 || i >= arr.Elements.Count)
                throw new AoiThrowException(new ExceptionValue("IndexOutOfRangeException", i.ToString()));
            return arr.Elements[i];
        }
        if (obj is SpanValue span)
        {
            int i = (int)((IntValue)index).Value;
            if (i < 0 || i >= span.Length)
                throw new AoiThrowException(new ExceptionValue("IndexOutOfRangeException", i.ToString()));
            return span.Array.Elements[span.Start + i];
        }
        if (obj is StrValue str)
        {
            int i = (int)((IntValue)index).Value;
            if (i < 0 || i >= str.Value.Length)
                throw new AoiThrowException(new ExceptionValue("IndexOutOfRangeException", i.ToString()));
            return new CharValue(str.Value[i]);
        }
        if (obj is DynValue dyn)
            return GetIndex(dyn.Inner, index, line, column);

        var getter = _resolver.ResolveIndexGetter(obj.Type, index.Type, _env, line, column);
        if (getter != null) return InvokeResolved(getter, obj, index);
        throw new AoiException($"Cannot index {obj.Type}", line, column);
    }

    private AoiValue? SetIndex(AoiValue obj, AoiValue index, AoiValue value, int line = 0, int column = 0)
    {
        if (obj is ArrayValue arr)
        {
            int i = (int)((IntValue)index).Value;
            if (i < 0 || i >= arr.Elements.Count)
                throw new AoiThrowException(new ExceptionValue("IndexOutOfRangeException", i.ToString()));
            arr.Elements[i] = CoerceValue(value, arr.ElementType);
            return obj;
        }
        if (obj is SpanValue span)
        {
            int i = (int)((IntValue)index).Value;
            span.Array.Elements[span.Start + i] = CoerceValue(value, span.Array.ElementType);
            return obj;
        }
        if (obj is DynValue dyn)
            return SetIndex(dyn.Inner, index, value, line, column);

        var setter = _resolver.ResolveIndexSetter(obj.Type, index.Type, value.Type, _env, line, column);
        if (setter != null) return InvokeResolved(setter, obj, value, index);
        throw new AoiException($"Cannot set index on {obj.Type}", line, column);
    }

    // --- qualified names ---

    private AoiValue EvalQualified(QualifiedExpr qe)
    {
        if (_model.GetType(qe.TypeName) is { } type)
        {
            var args = qe.TypeArguments
                .Where(a => a.Type != null)
                .Select(a => ResolveType(a.Type!))
                .ToList();
            var named = new NamedType(type.Name, args, type);

            if (type.IsEnum)
            {
                if (type.EnumMembers.TryGetValue(qe.Member, out var ev))
                    return new EnumValue(named, qe.Member, ev);
            }
            if (type.IsUnion)
            {
                var caseDecl = type.UnionCases.FirstOrDefault(c => c.Name == qe.Member);
                if (caseDecl.Type == null)
                    throw new AoiException($"Union '{type.Name}' has no case '{qe.Member}'", qe.Line, qe.Column);
                var payloadType = type.UnionCases.Count == 0
                    ? UnitType.Instance
                    : _model.Resolver.Resolve(caseDecl.Type, TypeScopeFor(named));
                if (payloadType is UnitType)
                {
                    var payload = qe.Value != null ? Eval(qe.Value) : UnitValue.Instance;
                    return new UnionValue(named, qe.Member, payload);
                }
                if (qe.Value != null)
                    return new UnionValue(named, qe.Member, CoerceValue(Eval(qe.Value), payloadType));
                return new OperatorValue(qe.Member, false, (interp, _, value) =>
                    new UnionValue(named, qe.Member, interp.CoerceValue(value, payloadType)));
            }
        }

        if (_model.GetTrait(qe.TypeName) != null || _model.Impls.Any(i => i.Name == qe.TypeName))
        {
            bool binary = IsQualifiedBinary(qe);
            return new OperatorValue(qe.Member, binary, (interp, left, right) =>
            {
                var context = interp.EnvironmentForDyn(left, right);
                var args = binary ? new[] { left!.Type, right.Type } : new[] { right.Type };
                var resolved = interp._resolver.Resolve(qe.Member, binary, args, context,
                    qe.TypeArguments.Count > 0 ? qe.TypeArguments : null, qe.TypeName, qe.Line, qe.Column);
                return interp.InvokeResolved(resolved, left, right);
            });
        }

        // Dynamic type projection: value::T.
        if (_env.TryGet(qe.TypeName, out var dynValue))
            return EvalTypeProjectionValue(dynValue!, qe.Member);

        throw new AoiException($"Unknown qualified name {qe.TypeName}::{qe.Member}", qe.Line, qe.Column);
    }

    private bool IsQualifiedBinary(QualifiedExpr qe)
    {
        if (qe.Member is "+" or "-" or "*" or "/" or "%" or "==" or "!=" or "<" or "<=" or ">" or ">="
            or "<<" or ">>" or "&" or "|" or "^" or "&&" or "||" or "??" or "|>" or ".." or "@")
            return true;
        if (_model.GetTrait(qe.TypeName) is { } trait)
            return trait.Members.Any(m => m.Name == qe.Member && m.IsBinary);
        var impl = _model.Impls.FirstOrDefault(i => i.Name == qe.TypeName);
        if (impl != null)
        {
            if (impl.Members.Any(m => m.Name == qe.Member && m.IsBinary)) return true;
            return impl.Trait?.Members.Any(m => m.Name == qe.Member && m.IsBinary) == true;
        }
        return false;
    }

    private Dictionary<string, AoiType> TypeScopeFor(NamedType type)
    {
        var scope = new Dictionary<string, AoiType>(StringComparer.Ordinal);
        if (type.Definition == null) return scope;
        for (int i = 0; i < type.Definition.TypeParams.Count && i < type.Args.Length; i++)
            scope[type.Definition.TypeParams[i].Name] = type.Args[i];
        return scope;
    }

    private AoiValue EvalTypeProjection(TypeProjectionExpr tp)
    {
        var value = Eval(tp.Value);
        return EvalTypeProjectionValue(value, tp.Member);
    }

    private AoiValue EvalTypeProjectionValue(AoiValue value, string member)
    {
        if (value is DynValue dyn)
        {
            if (member == dyn.DynType.Self.Name)
                return new TypeValue(dyn.Inner.Type);
            if (dyn.Inner.Type is NamedType nt && nt.Definition != null)
            {
                for (int i = 0; i < nt.Definition.TypeParams.Count && i < nt.Args.Length; i++)
                    if (nt.Definition.TypeParams[i].Name == member) return new TypeValue(nt.Args[i]);
            }
            return new TypeValue(dyn.Inner.Type);
        }
        if (_env.TryGetTypeName(member, out var type)) return new TypeValue(type);
        return new TypeValue(value.Type);
    }

    // --- casts / coercion ---

    private AoiValue DoCast(AoiType target, AoiValue value)
    {
        if (target is DynType dyn)
        {
            if (value is DynValue) return value;
            return DynValue.Wrap(value, dyn, _model.TraitEngine, _env.Impls);
        }

        if (target is NamedType { Name: "int" } && value is FloatValue f)
            return new IntValue((long)f.Value);
        if (target is NamedType { Name: "float" } && value is IntValue i)
            return new FloatValue(i.Value);
        if (target is NamedType { Name: "str" })
            return new StrValue(UnwrapDyn(value).ToString() ?? "");

        if (value.Type.Equals(target)) return value;

        var conversion = _resolver.ResolveConvert(value.Type, target, _env, implicitOnly: false);
        if (conversion != null) return InvokeResolved(conversion, null, value);

        // int -> float is available implicitly in arithmetic; allow the cast too.
        if (target is PrimitiveType { Name: "float" } && value is IntValue iv)
            return new FloatValue(iv.Value);

        throw new AoiException($"Cannot cast {value.Type} to {target}");
    }

    private AoiValue CoerceValue(AoiValue value, AoiType expected)
    {
        if (expected is UnknownType or TypeVar) return value;
        if (expected is DynType dyn)
            return value is DynValue ? value : DynValue.Wrap(value, dyn, _model.TraitEngine, _env.Impls);
        if (expected is NullableType nullable)
        {
            if (value is NullableValue) return value;
            return new NullableValue(CoerceValue(value, nullable.Element), nullable.Element);
        }
        if (value.Type.Equals(expected)) return value;

        // Structural coercion for arrays/tuples is useful when the container's
        // static element type is more precise than the literal's inferred type.
        if (expected is NamedType { Name: "arr" } expectedArray && value is ArrayValue array)
        {
            var element = expectedArray.Args.FirstOrDefault() ?? array.ElementType;
            return new ArrayValue(element, array.Elements.Select(e => CoerceValue(e, element)).ToList());
        }
        if (expected is TupleType tuple && value is TupleValue tv && tuple.Elements.Length == tv.Elements.Count)
            return new TupleValue(tv.Elements.Select((e, i) => CoerceValue(e, tuple.Elements[i])).ToList());

        var conversion = _resolver.ResolveConvert(value.Type, expected, _env, implicitOnly: true);
        if (conversion != null) return InvokeResolved(conversion, null, value);

        // Numeric widening.
        if (expected is PrimitiveType { Name: "float" } && value is IntValue iv)
            return new FloatValue(iv.Value);

        return value;
    }

    // --- dynamic dispatch helpers ---

    private Environment EnvironmentForDyn(params AoiValue?[] values)
    {
        var dynValues = values.OfType<DynValue>().ToList();
        if (dynValues.Count == 0) return _env;
        var child = _env.CreateChild();
        foreach (var dyn in dynValues)
            child.AddImpls(dyn.Implementations.All);
        return child;
    }

    private static AoiValue UnwrapDyn(AoiValue value) => value is DynValue dyn ? UnwrapDyn(dyn.Inner) : value;

    private AoiValue WrapDyn(AoiValue value, IReadOnlyList<ImplRefSyntax> implRefs, DynType? dynType)
    {
        if (dynType != null)
            return value is DynValue ? value : DynValue.Wrap(value, dynType, _model.TraitEngine, _env.Impls);

        if (implRefs.Count == 0) return value;
        if (value is DynValue existing) return existing;

        var requirements = new List<TraitRequirement>();
        var bindings = new List<ImplBinding>();
        foreach (var reference in implRefs)
        {
            var impl = _model.Impls.FirstOrDefault(i => i.Name == reference.Name);
            if (impl == null) continue;
            var binding = _model.TraitEngine.InstantiateImpl(impl, new TypeSubstitution(), _env.Impls);
            bindings.Add(binding);
            requirements.Add(new TraitRequirement(binding.TraitName, binding.TraitArgs));
        }
        if (requirements.Count == 0) return value;

        var self = TypeVar.Fresh("T");
        var dyn = new DynType(self, requirements, self);
        var context = new ImplContext(_env.Impls);
        context.AddRange(bindings);
        return new DynValue(value, dyn, context);
    }

    // --- patterns / binding / assignment ---

    private void BindDecl(DeclPattern pattern, AoiValue value, Environment env)
    {
        switch (pattern)
        {
            case NameDeclPattern n:
                if (n.Name != "_") env.Define(n.Name, value.Copy());
                break;
            case TupleDeclPattern t:
            {
                if (t.Elements.Count == 0) break;
                if (value is not TupleValue tv)
                    throw new AoiException("Expected tuple for destructuring");
                for (int i = 0; i < t.Elements.Count; i++)
                {
                    if (t.Elements[i] != null)
                        BindDecl(t.Elements[i]!, tv.Elements[i], env);
                }
                break;
            }
            case ObjectDeclPattern o:
            {
                int auto = 0;
                foreach (var (name, index, pat) in o.Fields)
                {
                    AoiValue v;
                    if (index != null) v = GetIndex(value, Eval(index));
                    else if (name != null) v = GetMember(value, name);
                    else v = GetIndex(value, new IntValue(auto++));
                    BindDecl(pat, v, env);
                }
                break;
            }
            default:
                throw new AoiException("Unsupported decl pattern");
        }
    }

    private void AssignPattern(AssignPattern pattern, AoiValue value)
    {
        switch (pattern)
        {
            case ExprAssignPattern e:
                AssignToExpr(e.Target, value);
                break;
            case TupleAssignPattern t:
            {
                var tv = (TupleValue)value;
                for (int i = 0; i < t.Elements.Count; i++)
                {
                    if (t.Elements[i] != null)
                        AssignPattern(t.Elements[i]!, tv.Elements[i]);
                }
                break;
            }
            case ObjectAssignPattern o:
            {
                int auto = 0;
                foreach (var (name, index, pat) in o.Fields)
                {
                    AoiValue v;
                    if (index != null) v = GetIndex(value, Eval(index));
                    else if (name != null) v = GetMember(value, name);
                    else v = GetIndex(value, new IntValue(auto++));
                    AssignPattern(pat, v);
                }
                break;
            }
        }
    }

    private void AssignToExpr(Expr target, AoiValue value)
    {
        switch (target)
        {
            case IdentExpr id:
                if (!_env.Assign(id.Name, value.Copy()))
                    throw new AoiException($"Undefined variable '{id.Name}'");
                break;
            case MemberExpr me:
            {
                if (me.Object is MemberExpr parentMe)
                {
                    var parentObj = Eval(parentMe.Object);
                    var parentField = parentObj is StructValue ps && ps.Fields.ContainsKey(parentMe.Member);
                    var parentGetter = !parentField && _resolver.ResolveGetter(parentMe.Member, parentObj.Type, _env) != null;
                    if (parentGetter)
                    {
                        var temp = GetMember(parentObj, parentMe.Member).Copy();
                        SetMember(temp, me.Member, value);
                        var written = SetMember(parentObj, parentMe.Member, temp) ?? parentObj;
                        WriteBackExpr(parentMe.Object, written);
                        return;
                    }
                }

                var obj = Eval(me.Object);
                var updated = SetMember(obj, me.Member, value) ?? obj;
                if (obj is StructValue sv && !sv.IsClass)
                    WriteBackExpr(me.Object, updated);
                else if (!ReferenceEquals(updated, obj))
                    WriteBackExpr(me.Object, updated);
                break;
            }
            case IndexExpr ix:
            {
                var obj = Eval(ix.Object);
                var updated = SetIndex(obj, Eval(ix.Index), value) ?? obj;
                if (!ReferenceEquals(updated, obj))
                    WriteBackExpr(ix.Object, updated);
                break;
            }
            default:
                throw new AoiException("Invalid assignment target", target.Line, target.Column);
        }
    }

    private void WriteBackExpr(Expr target, AoiValue value)
    {
        switch (target)
        {
            case IdentExpr id:
                if (_env.TryGet(id.Name, out var cur) && ReferenceEquals(cur, value)) return;
                _env.Assign(id.Name, value.Copy());
                break;
            case MemberExpr or IndexExpr:
                AssignToExpr(target, value);
                break;
        }
    }

    private bool MatchPattern(AoiValue value, MatchPattern pattern, Environment env)
    {
        switch (pattern)
        {
            case ValueMatchPattern v:
                return EqualsValue(value, Eval(v.Value));
            case UnaryOpMatchPattern u:
            {
                var result = EvalUnaryOp(u.Op, value, null);
                return result is BoolValue b && b.Value;
            }
            case BinaryOpMatchPattern b:
                return IsTruthy(EvalBinaryOp(b.Op, value, Eval(b.Value)));
            case AndMatchPattern a:
                return MatchPattern(value, a.Left, env) && MatchPattern(value, a.Right, env);
            case OrMatchPattern o:
                return MatchPattern(value, o.Left, env.CreateChild()) || MatchPattern(value, o.Right, env.CreateChild());
            case XorMatchPattern x:
                return MatchPattern(value, x.Left, env.CreateChild()) ^ MatchPattern(value, x.Right, env.CreateChild());
            case NotMatchPattern n:
                return !MatchPattern(value, n.Pattern, env.CreateChild());
            case TupleMatchPattern t:
            {
                if (value is not TupleValue tv) return false;
                for (int i = 0; i < t.Elements.Count; i++)
                {
                    if (t.Elements[i] == null) continue;
                    if (!MatchPattern(tv.Elements[i], t.Elements[i]!, env)) return false;
                }
                return true;
            }
            case ObjectMatchPattern o:
            {
                int auto = 0;
                foreach (var (name, index, pat) in o.Fields)
                {
                    AoiValue v;
                    try
                    {
                        if (index != null) v = GetIndex(value, Eval(index));
                        else if (name != null) v = GetMember(value, name);
                        else v = GetIndex(value, new IntValue(auto++));
                    }
                    catch { return false; }
                    if (!MatchPattern(v, pat, env)) return false;
                }
                return true;
            }
            case UnionMatchPattern u:
            {
                if (u.Condition == "some")
                {
                    if (value is not NullableValue nv || nv.IsNone) return false;
                    if (u.Inner != null && !MatchPattern(nv.Value!, u.Inner, env)) return false;
                    if (u.Binding != null) BindDecl(u.Binding, nv.Value!, env);
                    return true;
                }
                if (u.Condition == "none")
                {
                    if (value is not NullableValue nv || !nv.IsNone) return false;
                    if (u.Binding != null) BindDecl(u.Binding, UnitValue.Instance, env);
                    return true;
                }
                string caseName = u.Condition.Contains("::") ? u.Condition.Split("::")[^1] : u.Condition;
                if (value is UnionValue uv)
                {
                    if (uv.CaseName != caseName) return false;
                    if (u.Inner != null && !MatchPattern(uv.Payload, u.Inner, env)) return false;
                    if (u.Binding != null) BindDecl(u.Binding, uv.Payload, env);
                    return true;
                }
                if (value is ExceptionValue ev && (ev.ExceptionName == u.Condition || ev.ExceptionName == caseName))
                {
                    if (u.Binding != null) BindDecl(u.Binding, value, env);
                    return true;
                }
                if (value.Type is NamedType nt && (nt.Name == u.Condition || nt.Name == caseName))
                {
                    if (u.Binding != null) BindDecl(u.Binding, value, env);
                    return true;
                }
                return false;
            }
            case TypeMatchPattern t:
            {
                var expected = ResolveType(t.Type);
                if (!ValueMatchesType(value, expected)) return false;
                if (t.Inner != null && !MatchPattern(value, t.Inner, env)) return false;
                if (t.Binding != null) BindDecl(t.Binding, value, env);
                return true;
            }
            case AsMatchPattern a:
            {
                if (!MatchPattern(value, a.Inner, env)) return false;
                BindDecl(a.Binding, value, env);
                return true;
            }
            default:
                return false;
        }
    }

    private static bool ValueMatchesType(AoiValue value, AoiType expected)
    {
        if (expected is UnknownType or TypeVar) return true;
        if (expected is DynType dyn)
        {
            var inner = value is DynValue d ? d.Inner : value;
            return ValueMatchesType(inner, dyn.Inner is TypeVar ? UnknownType.Instance : dyn.Inner);
        }
        if (value is DynValue dynValue) return ValueMatchesType(dynValue.Inner, expected);
        if (expected is NamedType n && value.Type is NamedType v)
        {
            if (n.Name != v.Name) return false;
            if (n.Args.Length == 0 || n.Args.All(a => a is TypeVar or UnknownType)) return true;
            return n.Args.Length == v.Args.Length && n.Args.Zip(v.Args).All(p => ValueMatchesTypeType(p.First, p.Second));
        }
        if (expected is PrimitiveType p) return value.Type is PrimitiveType q && p.Name == q.Name;
        if (expected is NullableType nt) return value is NullableValue nv && (nv.IsNone || ValueMatchesType(nv.Value!, nt.Element));
        return value.Type.Equals(expected);
    }

    private static bool ValueMatchesTypeType(AoiType expected, AoiType actual)
        => expected is TypeVar or UnknownType || expected.Equals(actual);

    // --- iteration / equality / helpers ---

    private IEnumerable<AoiValue> Iterate(AoiValue value)
    {
        if (value is DynValue dyn) value = dyn.Inner;
        if (value is RangeValue range)
        {
            foreach (var i in range.Iterate()) yield return new IntValue(i);
            yield break;
        }
        if (value is ArrayValue arr)
        {
            foreach (var e in arr.Elements) yield return e;
            yield break;
        }
        if (value is SpanValue span)
        {
            for (int i = 0; i < span.Length; i++) yield return span.Array.Elements[span.Start + i];
            yield break;
        }

        var context = EnvironmentForDyn(value);
        var iterate = _resolver.Resolve("iterate", false, new[] { value.Type }, context);
        while (true)
        {
            var next = InvokeResolved(iterate, null, value);
            if (next is NullableValue nv)
            {
                if (nv.IsNone) yield break;
                yield return nv.Value!;
            }
            else yield break;
        }
    }

    private static bool IsTruthy(AoiValue v) => v is BoolValue b && b.Value;

    private static bool EqualsValue(AoiValue a, AoiValue b)
    {
        a = UnwrapDyn(a);
        b = UnwrapDyn(b);
        return (a, b) switch
        {
            (IntValue x, IntValue y) => x.Value == y.Value,
            (FloatValue x, FloatValue y) => x.Value == y.Value,
            (IntValue x, FloatValue y) => x.Value == y.Value,
            (FloatValue x, IntValue y) => x.Value == y.Value,
            (BoolValue x, BoolValue y) => x.Value == y.Value,
            (CharValue x, CharValue y) => x.Value == y.Value,
            (StrValue x, StrValue y) => x.Value == y.Value,
            (UnitValue, UnitValue) => true,
            (EnumValue x, EnumValue y) => x.EnumName == y.EnumName && x.Value == y.Value,
            (NullableValue x, NullableValue y) => (x.IsNone && y.IsNone)
                || (!x.IsNone && !y.IsNone && EqualsValue(x.Value!, y.Value!)),
            (TupleValue x, TupleValue y) => x.Elements.Count == y.Elements.Count
                && x.Elements.Zip(y.Elements).All(p => EqualsValue(p.First, p.Second)),
            (StructValue x, StructValue y) => ReferenceEquals(x, y)
                || (!x.IsClass && !y.IsClass && x.StructType.Equals(y.StructType)
                    && x.Fields.Count == y.Fields.Count
                    && x.Fields.All(kv => y.Fields.TryGetValue(kv.Key, out var ov) && EqualsValue(kv.Value, ov))),
            _ => ReferenceEquals(a, b),
        };
    }

    private AoiValue EvalTemplate(string raw)
    {
        var sb = new StringBuilder();
        var regex = new Regex(@"\{([^{}]+)\}");
        int last = 0;
        foreach (Match m in regex.Matches(raw))
        {
            sb.Append(raw, last, m.Index - last);
            var name = m.Groups[1].Value.Trim();
            sb.Append(EvalInterpolation(name));
            last = m.Index + m.Length;
        }
        sb.Append(raw, last, raw.Length - last);
        return new StrValue(sb.ToString());
    }

    private string EvalInterpolation(string code)
    {
        try
        {
            var tokens = new Lexer.Lexer(code).Tokenize();
            var parser = new Parser.Parser(tokens);
            var program = new Parser.Parser(new Lexer.Lexer($"let __t = {code};").Tokenize()).ParseProgram();
            var prev = _env;
            _env = prev.CreateChild();
            try
            {
                _model.AddProgram(program);
                foreach (var stmt in program.Statements) ExecStmt(stmt);
                return _env.Get("__t").ToString() ?? "";
            }
            finally { _env = prev; }
        }
        catch
        {
            return _env.Get(code).ToString() ?? "";
        }
    }
}
