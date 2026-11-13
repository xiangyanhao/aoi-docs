using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Aoi.Interpreter.Ast;

namespace Aoi.Interpreter.Runtime;

public sealed class InterpreterOptions
{
    /// <summary>
    /// Maximum nested operator/closure calls before reporting stack overflow.
    /// Kept modest because each Aoi call consumes several CLR frames.
    /// </summary>
    public int MaxCallDepth { get; init; } = 1_024;

    /// <summary>Maximum expression/statement evaluation steps before aborting runaway programs.</summary>
    public long MaxSteps { get; init; } = 50_000_000;
}

public sealed class Interpreter
{
    private Environment _env = new();
    private readonly Dictionary<string, TypeInfo> _types = new();
    private readonly List<UserOperator> _operators = new();
    private readonly Dictionary<string, UserOperator> _getters = new();
    private readonly Dictionary<string, UserOperator> _setters = new();
    private readonly Dictionary<string, UserOperator> _indexGetters = new();
    private readonly Dictionary<string, UserOperator> _indexSetters = new();
    private readonly Dictionary<(string From, string To), UserOperator> _converts = new();
    private readonly Dictionary<string, UserOperator> _literals = new();
    private readonly TextWriter _output;
    private readonly int _maxCallDepth;
    private readonly long _maxSteps;
    private int _callDepth;
    private long _steps;

    public Interpreter(TextWriter? output = null, InterpreterOptions? options = null)
    {
        _output = output ?? Console.Out;
        options ??= new InterpreterOptions();
        _maxCallDepth = Math.Max(1, options.MaxCallDepth);
        _maxSteps = Math.Max(1, options.MaxSteps);
        RegisterBuiltins();
    }

    public void Execute(ProgramNode program)
    {
        _callDepth = 0;
        _steps = 0;
        foreach (var stmt in program.Statements)
            ExecStmt(stmt);
    }

    private void Tick()
    {
        if (++_steps > _maxSteps)
            throw new AoiException($"Execution limit exceeded ({_maxSteps} steps). Possible infinite loop.");

        // Periodically probe CLR stack so deep Eval chains fail as AoiException, not process abort.
        if ((_steps & 0x3F) == 0)
            EnsureStack();
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
        try
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
        }
        catch (InsufficientExecutionStackException)
        {
            throw new AoiException("Stack overflow: insufficient execution stack.");
        }
    }

    private void RegisterBuiltins()
    {
        _operators.Add(new UserOperator
        {
            Name = "printLine",
            IsBinary = false,
            Rhs = new NameDeclPattern { Name = "x" },
            Body = new UnitExpr(), // handled specially
        });
        _operators.Add(new UserOperator
        {
            Name = "print",
            IsBinary = false,
            Rhs = new NameDeclPattern { Name = "x" },
            Body = new UnitExpr(),
        });
        _operators.Add(new UserOperator
        {
            Name = "toStr",
            IsBinary = false,
            Rhs = new NameDeclPattern { Name = "x" },
            Body = new UnitExpr(),
        });
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
                BindDecl(let.Pattern, Eval(let.Value), _env);
                break;
            case BlockStmt block:
            {
                var prev = _env;
                _env = new Environment(prev);
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
                    var child = new Environment(_env);
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
                    var child = new Environment(_env);
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
                    var child = new Environment(_env);
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
                try
                {
                    ExecStmt(tryStmt.Body);
                }
                catch (AoiThrowException ex)
                {
                    bool caught = false;
                    foreach (var (pattern, branch) in tryStmt.Catches)
                    {
                        var child = new Environment(_env);
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
                    if (tryStmt.Finally != null)
                        ExecStmt(tryStmt.Finally);
                }
                break;
            }
            case StructDecl sd:
            {
                var info = new TypeInfo { Name = sd.Name, IsClass = sd.IsClass };
                info.Fields.AddRange(sd.Fields);
                _types[sd.Name] = info;
                break;
            }
            case EnumDecl ed:
            {
                var info = new TypeInfo { Name = ed.Name, IsEnum = true };
                long next = 0;
                foreach (var (name, value) in ed.Members)
                {
                    if (value.HasValue) next = value.Value;
                    info.EnumMembers[name] = next;
                    next++;
                }
                _types[ed.Name] = info;
                break;
            }
            case UnionDecl ud:
            {
                var info = new TypeInfo { Name = ud.Name, IsUnion = true };
                info.UnionCases.AddRange(ud.Cases);
                _types[ud.Name] = info;
                break;
            }
            case OpDecl op:
                _operators.Add(new UserOperator
                {
                    Name = op.Name,
                    IsBinary = op.IsBinary,
                    Lhs = op.Lhs,
                    Rhs = op.Rhs!,
                    Body = op.Body,
                });
                break;
            case TraitDecl:
                break;
            case ImplDecl implDecl:
                foreach (var m in implDecl.Members)
                {
                    _operators.Add(new UserOperator
                    {
                        Name = m.Name,
                        IsBinary = m.IsBinary,
                        Lhs = m.Lhs,
                        Rhs = m.Rhs!,
                        Body = m.Body,
                        ImplName = implDecl.Name ?? implDecl.TraitName,
                    });
                }
                break;
            case TypeAliasDecl:
                break;
            case ConvertDecl conv:
                _converts[(TypeNameOf(conv.FromType), TypeNameOf(conv.ToType))] = new UserOperator
                {
                    Name = "convert",
                    IsBinary = false,
                    Rhs = conv.Pattern,
                    Body = conv.Body,
                };
                break;
            case GetDecl get:
            {
                var uo = new UserOperator
                {
                    Name = get.PropertyName ?? "[]",
                    IsBinary = get.IsIndexer,
                    Lhs = get.Self,
                    Rhs = get.Index ?? new NameDeclPattern { Name = "_" },
                    Body = get.Body,
                };
                if (get.IsIndexer) _indexGetters[get.PropertyName ?? ""] = uo;
                else if (get.PropertyName != null) _getters[get.PropertyName] = uo;
                // Also store under self type name if available
                if (get.SelfType is NamedTypeRef nt)
                {
                    if (get.IsIndexer) _indexGetters[nt.Name] = uo;
                    else _getters[$"{nt.Name}.{get.PropertyName}"] = uo;
                }
                break;
            }
            case SetDecl set:
            {
                var uo = new UserOperator
                {
                    Name = set.PropertyName ?? "[]",
                    IsBinary = true,
                    Lhs = set.Self,
                    Rhs = set.Value,
                    Body = set.Body,
                };
                if (set.IsIndexer) _indexSetters[set.PropertyName ?? ""] = uo;
                else if (set.PropertyName != null) _setters[set.PropertyName] = uo;
                break;
            }
            case LiteralSuffixDecl lit:
                _literals[lit.Suffix] = new UserOperator
                {
                    Name = lit.Suffix,
                    IsBinary = false,
                    Rhs = lit.Pattern,
                    Body = lit.Body,
                };
                break;
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
            case IdentExpr id: return LookupIdent(id.Name);
            case UnitExpr: return UnitValue.Instance;
            case UnsetExpr: return UnsetValue.Instance;
            case NoneExpr: return NullableValue.None;
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
                _env = new Environment(prev);
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
                    var child = new Environment(_env);
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
                    var child = new Environment(_env);
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
                        var child = new Environment(_env);
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
                return GetMember(Eval(me.Object), me.Member);
            case IndexExpr ix:
                return GetIndex(Eval(ix.Object), Eval(ix.Index));
            case SafeMemberExpr sm:
            {
                var obj = Eval(sm.Object);
                if (obj is NullableValue nv)
                {
                    if (nv.IsNone) return NullableValue.None;
                    return new NullableValue(GetMember(nv.Value!, sm.Member));
                }
                return new NullableValue(GetMember(obj, sm.Member));
            }
            case SafeIndexExpr si:
            {
                var obj = Eval(si.Object);
                if (obj is NullableValue nv)
                {
                    if (nv.IsNone) return NullableValue.None;
                    return new NullableValue(GetIndex(nv.Value!, Eval(si.Index)));
                }
                return new NullableValue(GetIndex(obj, Eval(si.Index)));
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
                return new BoolValue(MatchPattern(Eval(ise.Value), ise.Pattern, new Environment(_env)));
            case ClosureExpr ce:
                return MakeClosure(ce);
            case CastExpr cast:
                return DoCast(TypeNameOf(cast.Type), Eval(cast.Value));
            case QualifiedExpr qe:
                return EvalQualified(qe);
            case GroupedOpExpr go:
                return MakeCurriedBinary(go.Op);
            case CallLikeExpr call:
                return EvalCallLike(call);
            default:
                throw new AoiException($"Unsupported expression: {expr.GetType().Name}", expr.Line, expr.Column);
        }
    }

    private AoiValue EvalCallLike(CallLikeExpr call)
    {
        var parts = call.Parts;
        if (parts.Count == 0) return UnitValue.Instance;
        if (parts.Count == 1) return Eval(parts[0]);
        if (parts.Count == 2)
            return ApplyJuxtaposed(parts[0], parts[1]);

        // Odd length: prefer binary ops when middle parts are known binary operator names.
        if (parts.Count % 2 == 1 && IsBinaryOpName(parts[1]))
        {
            var result = Eval(parts[0]);
            for (int i = 1; i < parts.Count; i += 2)
            {
                var opName = OpNameOf(parts[i]);
                var right = Eval(parts[i + 1]);
                result = EvalBinaryOp(opName, result, right);
            }
            return result;
        }

        // Otherwise right-associative unary application: a b c => a (b c)
        // so `printLine fac 10` => printLine (fac 10)
        AoiValue acc = Eval(parts[^1]);
        for (int i = parts.Count - 2; i >= 0; i--)
            acc = ApplyJuxtaposedValue(parts[i], acc);
        return acc;
    }

    private bool IsBinaryOpName(Expr expr)
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
        return _operators.Any(o => o.Name == name && o.IsBinary);
    }

    private static string OpNameOf(Expr expr) => expr switch
    {
        IdentExpr id => id.Name,
        GroupedOpExpr g => g.Op,
        QualifiedExpr q => q.Value == null ? q.Member : throw new AoiException("Invalid operator"),
        _ => throw new AoiException("Expected operator name", expr.Line, expr.Column),
    };

    private AoiValue ApplyJuxtaposed(Expr opExpr, Expr argExpr)
    {
        // Type cast: Type value
        if (opExpr is IdentExpr id && _types.ContainsKey(id.Name))
            return DoCast(id.Name, Eval(argExpr));
        if (opExpr is IdentExpr id2 && id2.Name is "int" or "float" or "str" or "bool" or "char")
            return DoCast(id2.Name, Eval(argExpr));

        return ApplyJuxtaposedValue(opExpr, Eval(argExpr));
    }

    private AoiValue ApplyJuxtaposedValue(Expr opExpr, AoiValue arg)
    {
        if (opExpr is IdentExpr id)
        {
            // Variable bound to a first-class operator (closure / curried op).
            if (_env.TryGet(id.Name, out var bound) && bound is OperatorValue ov)
                return ApplyUnary(ov, arg);

            // Binary op with only the right operand: curry to unary `x => x op arg`
            // (e.g. `map fn` used in `array |> map fn`).
            var binary = _operators.LastOrDefault(o => o.Name == id.Name && o.IsBinary);
            if (binary != null)
            {
                return new OperatorValue(id.Name, false, (interp, _, left) =>
                    interp.InvokeUserOp(binary, left, arg));
            }
            return CallUnaryOp(id.Name, arg);
        }
        if (opExpr is GroupedOpExpr g)
            return ApplyUnary(MakeCurriedBinary(g.Op), arg);
        if (opExpr is QualifiedExpr q && q.Value == null)
        {
            var binary = _operators.LastOrDefault(o => o.Name == q.Member && o.IsBinary);
            if (binary != null)
            {
                return new OperatorValue(q.Member, false, (interp, _, left) =>
                    interp.InvokeUserOp(binary, left, arg));
            }
            return CallUnaryOp(q.Member, arg);
        }

        var opVal = Eval(opExpr);
        return ApplyUnary(opVal, arg);
    }

    private AoiValue LookupIdent(string name)
    {
        if (_env.TryGet(name, out var v) && v != null) return v;

        // Builtins keep their special behavior when used as first-class ops (e.g. `|> printLine`).
        if (name is "printLine" or "print" or "toStr")
            return new OperatorValue(name, false, (interp, _, arg) => interp.CallUnaryOp(name, arg));

        // Operator as value
        var op = _operators.LastOrDefault(o => o.Name == name);
        if (op != null)
            return WrapOperator(op);

        throw new AoiException($"Undefined '{name}'");
    }

    private AoiValue EvalUnary(UnaryExpr u)
    {
        // Custom / named unary via juxtaposition already creates UnaryExpr with op name
        if (u.Op is "printLine" or "print" or "toStr" || _operators.Any(o => o.Name == u.Op && !o.IsBinary))
        {
            var operand = Eval(u.Operand);
            return CallUnaryOp(u.Op, operand);
        }

        var val = Eval(u.Operand);
        return u.Op switch
        {
            "+" when val is IntValue i => i,
            "+" when val is FloatValue f => f,
            "-" when val is IntValue i => new IntValue(-i.Value),
            "-" when val is FloatValue f => new FloatValue(-f.Value),
            "!" when val is BoolValue b => new BoolValue(!b.Value),
            "~" when val is IntValue i => new IntValue(~i.Value),
            "~" when val is BoolValue b => new BoolValue(!b.Value),
            _ => CallUnaryOp(u.Op, val),
        };
    }

    private AoiValue CallUnaryOp(string name, AoiValue operand)
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
            return new StrValue(operand.ToString() ?? "");

        var op = _operators.LastOrDefault(o => o.Name == name && !o.IsBinary)
                 ?? throw new AoiException($"Unknown unary operator '{name}'");
        return InvokeUserOp(op, null, operand);
    }

    private AoiValue EvalBinary(BinaryExpr b)
    {
        if (b.Op is "&&" or "||" or "??")
        {
            var left = Eval(b.Left);
            if (b.Op == "&&")
                return new BoolValue(IsTruthy(left) && IsTruthy(Eval(b.Right)));
            if (b.Op == "||")
                return new BoolValue(IsTruthy(left) || IsTruthy(Eval(b.Right)));
            // ??
            if (left is NullableValue nv)
            {
                if (!nv.IsNone) return nv.Value!;
                return Eval(b.Right);
            }
            return left;
        }

        var l = Eval(b.Left);
        var r = Eval(b.Right);
        return EvalBinaryOp(b.Op, l, r);
    }

    private AoiValue EvalBinaryOp(string op, AoiValue left, AoiValue right)
    {
        // Built-in arithmetic / comparison
        switch (op)
        {
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
                if (ri.Value == 0) throw new AoiException("Division by zero");
                return new IntValue(li.Value / ri.Value);
            case "/" when left is FloatValue lf && right is FloatValue rf: return new FloatValue(lf.Value / rf.Value);
            case "/" when left is IntValue li2 && right is FloatValue rf2: return new FloatValue(li2.Value / rf2.Value);
            case "/" when left is FloatValue lf2 && right is IntValue ri2: return new FloatValue(lf2.Value / ri2.Value);
            case "%" when left is IntValue li && right is IntValue ri:
                if (ri.Value == 0) throw new AoiException("Division by zero");
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
            case "==": return new BoolValue(EqualsValue(left, right));
            case "!=": return new BoolValue(!EqualsValue(left, right));
            case "<" when left is IntValue li && right is IntValue ri: return new BoolValue(li.Value < ri.Value);
            case "<" when left is FloatValue lf && right is FloatValue rf: return new BoolValue(lf.Value < rf.Value);
            case "<" when left is CharValue lc && right is CharValue rc: return new BoolValue(lc.Value < rc.Value);
            case "<=" when left is IntValue li && right is IntValue ri: return new BoolValue(li.Value <= ri.Value);
            case "<=" when left is FloatValue lf && right is FloatValue rf: return new BoolValue(lf.Value <= rf.Value);
            case "<=" when left is CharValue lc && right is CharValue rc: return new BoolValue(lc.Value <= rc.Value);
            case ">" when left is IntValue li && right is IntValue ri: return new BoolValue(li.Value > ri.Value);
            case ">" when left is FloatValue lf && right is FloatValue rf: return new BoolValue(lf.Value > rf.Value);
            case ">" when left is CharValue lc && right is CharValue rc: return new BoolValue(lc.Value > rc.Value);
            case ">=" when left is IntValue li && right is IntValue ri: return new BoolValue(li.Value >= ri.Value);
            case ">=" when left is FloatValue lf && right is FloatValue rf: return new BoolValue(lf.Value >= rf.Value);
            case ">=" when left is CharValue lc && right is CharValue rc: return new BoolValue(lc.Value >= rc.Value);
        }

        // User-defined binary
        var userOp = _operators.LastOrDefault(o => o.Name == op && o.IsBinary);
        if (userOp != null)
            return InvokeUserOp(userOp, left, right);

        throw new AoiException($"No operator '{op}' for {left.TypeName} and {right.TypeName}");
    }

    private AoiValue InvokeUserOp(UserOperator op, AoiValue? left, AoiValue right)
    {
        EnterCall();
        var prev = _env;
        _env = new Environment(prev);
        try
        {
            if (op.IsBinary && op.Lhs != null && left != null)
                BindDecl(op.Lhs, left, _env);
            BindDecl(op.Rhs, right, _env);
            try { return Eval(op.Body); }
            catch (AoiReturnException ret) { return ret.Value; }
        }
        finally
        {
            _env = prev;
            LeaveCall();
        }
    }

    private OperatorValue WrapOperator(UserOperator op)
    {
        if (op.IsBinary)
        {
            return new OperatorValue(op.Name, true, (interp, l, r) =>
                interp.InvokeUserOp(op, l, r));
        }
        return new OperatorValue(op.Name, false, (interp, _, r) =>
            interp.InvokeUserOp(op, null, r));
    }

    private AoiValue MakeCurriedBinary(string op)
    {
        return new OperatorValue(op, false, (interp, _, right) =>
        {
            // (+) 42 => x => x + 42
            return new OperatorValue(op, false, (interp2, _, left) =>
                interp2.EvalBinaryOp(op, left, right));
        });
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

    private AoiValue ApplyUnary(AoiValue func, AoiValue arg)
    {
        if (func is OperatorValue op)
            return op.Invoke(this, null, arg);
        // Ident that is unary op name already handled
        throw new AoiException($"Cannot apply non-operator {func.TypeName}");
    }

    private AoiValue EvalNew(NewExpr ne)
    {
        if (ne.Type is NamedTypeRef nt && nt.Name == "arr")
        {
            string elemType = nt.Args.Count > 0 ? TypeNameOf(nt.Args[0]) : "any";
            var list = new List<AoiValue>();
            foreach (var (name, value, repeat, index) in ne.Elements)
            {
                var v = Eval(value);
                long times = repeat != null ? ((IntValue)Eval(repeat)).Value : 1;
                for (long i = 0; i < times; i++)
                    list.Add(v.Copy());
            }
            return new ArrayValue(elemType, list);
        }

        string typeName = TypeNameOf(ne.Type);
        if (!_types.TryGetValue(typeName, out var info))
        {
            // Allow undeclared struct-like for flexibility
            info = new TypeInfo { Name = typeName, IsClass = false };
        }

        var fields = new Dictionary<string, AoiValue>();
        foreach (var (name, value, _, _) in ne.Elements)
        {
            if (name == null) throw new AoiException("Field name required in new", ne.Line, ne.Column);
            fields[name] = Eval(value);
        }
        return new StructValue(typeName, fields, info.IsClass);
    }

    private AoiValue EvalSet(SetExpr se)
    {
        var target = Eval(se.Target);
        if (target is ArrayValue arr)
        {
            int pos = 0;
            foreach (var (name, value, repeat, index) in se.Elements)
            {
                var v = Eval(value);
                long times = repeat != null ? ((IntValue)Eval(repeat)).Value : 1;
                if (index != null)
                    pos = (int)((IntValue)Eval(index)).Value;
                for (long i = 0; i < times; i++)
                {
                    if (pos < arr.Elements.Count)
                        arr.Elements[pos] = v.Copy();
                    pos++;
                }
            }
            return arr;
        }
        if (target is StructValue sv)
        {
            foreach (var (name, value, _, _) in se.Elements)
            {
                if (name == null) continue;
                sv.Fields[name] = Eval(value);
            }
            return sv;
        }
        throw new AoiException("set target must be array or struct");
    }

    private AoiValue GetMember(AoiValue obj, string member)
    {
        if (obj is TupleValue tv && int.TryParse(member, out int idx))
        {
            if (idx < 0 || idx >= tv.Elements.Count)
                throw new AoiThrowException(new ExceptionValue("IndexOutOfRangeException", idx.ToString()));
            return tv.Elements[idx];
        }
        if (obj is ArrayValue arr && member == "length")
            return new IntValue(arr.Elements.Count);
        if (obj is SpanValue span && member == "length")
            return new IntValue(span.Length);
        if (obj is StrValue str && member == "length")
            return new IntValue(str.Value.Length);
        if (obj is StructValue sv)
        {
            if (sv.Fields.TryGetValue(member, out var f))
            {
                if (f is UnsetValue)
                    throw new AoiException($"Field '{member}' is unset");
                return f;
            }
        }
        // Getter
        if (_getters.TryGetValue(member, out var getter))
            return InvokeUserOp(getter, obj, UnitValue.Instance);
        if (obj is StructValue sv2 && _getters.TryGetValue($"{sv2.StructName}.{member}", out var g2))
            return InvokeUserOp(g2, obj, UnitValue.Instance);

        throw new AoiException($"No member '{member}' on {obj.TypeName}");
    }

    private AoiValue GetIndex(AoiValue obj, AoiValue index)
    {
        if (obj is ArrayValue arr)
        {
            if (index is RangeValue range)
            {
                int start = (int)range.From;
                int len = (int)(range.To - range.From);
                return new SpanValue(arr, start, len);
            }
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
        // Custom indexer
        foreach (var kv in _indexGetters)
        {
            try { return InvokeUserOp(kv.Value, obj, index); }
            catch { }
        }
        throw new AoiException($"Cannot index {obj.TypeName}");
    }

    private void SetMember(AoiValue obj, string member, AoiValue value)
    {
        if (obj is TupleValue tv && int.TryParse(member, out int idx))
        {
            tv.Elements[idx] = value;
            return;
        }
        if (obj is StructValue sv)
        {
            sv.Fields[member] = value;
            return;
        }
        if (_setters.TryGetValue(member, out var setter))
        {
            InvokeUserOp(setter, obj, value);
            return;
        }
        throw new AoiException($"Cannot set member '{member}' on {obj.TypeName}");
    }

    private void SetIndex(AoiValue obj, AoiValue index, AoiValue value)
    {
        if (obj is ArrayValue arr)
        {
            int i = (int)((IntValue)index).Value;
            if (i < 0 || i >= arr.Elements.Count)
                throw new AoiThrowException(new ExceptionValue("IndexOutOfRangeException", i.ToString()));
            arr.Elements[i] = value;
            return;
        }
        if (obj is SpanValue span)
        {
            int i = (int)((IntValue)index).Value;
            span.Array.Elements[span.Start + i] = value;
            return;
        }
        foreach (var kv in _indexSetters)
        {
            // set self.[index] = value — need both index and value; simplified
            var prev = _env;
            _env = new Environment(prev);
            try
            {
                BindDecl(kv.Value.Lhs!, obj, _env);
                // Rhs is value pattern in our storage; index not separately stored well
                BindDecl(kv.Value.Rhs, value, _env);
                Eval(kv.Value.Body);
                return;
            }
            catch { }
            finally { _env = prev; }
        }
        throw new AoiException($"Cannot set index on {obj.TypeName}");
    }

    private AoiValue EvalQualified(QualifiedExpr qe)
    {
        if (_types.TryGetValue(qe.TypeName, out var info))
        {
            if (info.IsEnum && info.EnumMembers.TryGetValue(qe.Member, out var ev))
                return new EnumValue(qe.TypeName, qe.Member, ev);
            if (info.IsUnion)
            {
                var payload = qe.Value != null ? Eval(qe.Value) : UnitValue.Instance;
                return new UnionValue(qe.TypeName, qe.Member, payload);
            }
        }
        // ImplName::op as operator value
        var op = _operators.LastOrDefault(o => o.Name == qe.Member && (o.ImplName == qe.TypeName || true));
        if (op != null && qe.Value == null)
            return WrapOperator(op);

        // Trait::op or just Type::member with value for union-like
        if (qe.Value != null)
            return new UnionValue(qe.TypeName, qe.Member, Eval(qe.Value));

        throw new AoiException($"Unknown qualified name {qe.TypeName}::{qe.Member}");
    }

    private AoiValue DoCast(string typeName, AoiValue value)
    {
        if (typeName == "int" && value is FloatValue f) return new IntValue((long)f.Value);
        if (typeName == "float" && value is IntValue i) return new FloatValue(i.Value);
        if (typeName == "str") return new StrValue(value.ToString() ?? "");
        if (_converts.TryGetValue((value.TypeName, typeName), out var conv))
            return InvokeUserOp(conv, null, value);
        if (value.TypeName == typeName) return value;
        // Implicit int->float already
        throw new AoiException($"Cannot cast {value.TypeName} to {typeName}");
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
            // Simple identifier interpolation
            try
            {
                var lexer = new Lexer.Lexer(name);
                var tokens = lexer.Tokenize();
                var parser = new Parser.Parser(tokens);
                // Parse as expression via program? Use reflection on private - instead eval ident or binary simply
                sb.Append(EvalSimpleInterp(name));
            }
            catch
            {
                sb.Append(_env.Get(name));
            }
            last = m.Index + m.Length;
        }
        sb.Append(raw, last, raw.Length - last);
        return new StrValue(sb.ToString());
    }

    private string EvalSimpleInterp(string code)
    {
        var lexer = new Lexer.Lexer(code + ";");
        // Hack: parse expression by wrapping
        var tokens = new Lexer.Lexer(code).Tokenize();
        // Remove EOF and parse expression
        var parser = new Parser.Parser(tokens);
        // Use public ParseProgram won't work for bare expr. Evaluate as ident or via mini.
        if (tokens.Count >= 1 && tokens[0].Kind == Lexer.TokenKind.Identifier && tokens[1].Kind == Lexer.TokenKind.Eof)
            return _env.Get(tokens[0].Text).ToString() ?? "";
        // Fall back: run as expression by creating temp parser method via program
        // ParseExpression is private - use let trick
        var prog = new Parser.Parser(new Lexer.Lexer($"let __t = {code};").Tokenize()).ParseProgram();
        var prev = _env;
        _env = new Environment(prev);
        try
        {
            Execute(prog);
            return _env.Get("__t").ToString() ?? "";
        }
        finally { _env = prev; }
    }

    private void BindDecl(DeclPattern pattern, AoiValue value, Environment env)
    {
        switch (pattern)
        {
            case NameDeclPattern n:
                env.Define(n.Name, value.Copy());
                break;
            case TupleDeclPattern t:
            {
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
                    if (index != null)
                        v = GetIndex(value, Eval(index));
                    else if (name != null)
                        v = GetMember(value, name);
                    else
                        v = GetIndex(value, new IntValue(auto++));
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
                var obj = Eval(me.Object);
                // Value-type property update semantics simplified: mutate if class/ref
                if (obj is StructValue sv && !sv.IsClass)
                {
                    // If object is a member of something, need reassignment — simplified mutate copy back via ident
                    SetMember(obj, me.Member, value);
                    if (me.Object is IdentExpr oid)
                        _env.Assign(oid.Name, obj);
                }
                else
                    SetMember(obj, me.Member, value);
                break;
            }
            case IndexExpr ix:
            {
                var obj = Eval(ix.Object);
                SetIndex(obj, Eval(ix.Index), value);
                break;
            }
            default:
                throw new AoiException("Invalid assignment target", target.Line, target.Column);
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
                var result = CallUnaryOp(u.Op, value);
                return result is BoolValue b && b.Value;
            }
            case BinaryOpMatchPattern b:
                return IsTruthy(EvalBinaryOp(b.Op, value, Eval(b.Value)));
            case AndMatchPattern a:
                return MatchPattern(value, a.Left, env) && MatchPattern(value, a.Right, env);
            case OrMatchPattern o:
                return MatchPattern(value, o.Left, new Environment(env)) || MatchPattern(value, o.Right, new Environment(env));
            case XorMatchPattern x:
                return MatchPattern(value, x.Left, new Environment(env)) ^ MatchPattern(value, x.Right, new Environment(env));
            case NotMatchPattern n:
                return !MatchPattern(value, n.Pattern, new Environment(env));
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
                // Type::Case
                string caseName = u.Condition.Contains("::") ? u.Condition.Split("::")[^1] : u.Condition;
                if (value is UnionValue uv)
                {
                    if (uv.CaseName != caseName) return false;
                    if (u.Inner != null && !MatchPattern(uv.Payload, u.Inner, env)) return false;
                    if (u.Binding != null) BindDecl(u.Binding, uv.Payload, env);
                    return true;
                }
                // Exception name match
                if (value is ExceptionValue ev && (ev.ExceptionName == u.Condition || ev.ExceptionName == caseName))
                {
                    if (u.Binding != null) BindDecl(u.Binding, value, env);
                    return true;
                }
                // Type name as exception/class
                if (value.TypeName == u.Condition || value.TypeName == caseName)
                {
                    if (u.Binding != null) BindDecl(u.Binding, value, env);
                    return true;
                }
                return false;
            }
            case TypeMatchPattern t:
            {
                if (value.TypeName != TypeNameOf(t.Type)) return false;
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

    private IEnumerable<AoiValue> Iterate(AoiValue value)
    {
        if (value is RangeValue range)
        {
            foreach (var i in range.Iterate())
                yield return new IntValue(i);
            yield break;
        }
        if (value is ArrayValue arr)
        {
            foreach (var e in arr.Elements) yield return e;
            yield break;
        }
        if (value is SpanValue span)
        {
            for (int i = 0; i < span.Length; i++)
                yield return span.Array.Elements[span.Start + i];
            yield break;
        }
        // Custom iterator: op iterate
        var iterate = _operators.LastOrDefault(o => o.Name == "iterate" && !o.IsBinary);
        if (iterate != null)
        {
            while (true)
            {
                var next = InvokeUserOp(iterate, null, value);
                if (next is NullableValue nv)
                {
                    if (nv.IsNone) yield break;
                    yield return nv.Value!;
                }
                else yield break;
            }
        }
        throw new AoiException($"Value of type {value.TypeName} is not iterable");
    }

    private static bool IsTruthy(AoiValue v) => v is BoolValue b && b.Value;

    private static bool EqualsValue(AoiValue a, AoiValue b)
    {
        return (a, b) switch
        {
            (IntValue x, IntValue y) => x.Value == y.Value,
            (FloatValue x, FloatValue y) => x.Value == y.Value,
            (BoolValue x, BoolValue y) => x.Value == y.Value,
            (CharValue x, CharValue y) => x.Value == y.Value,
            (StrValue x, StrValue y) => x.Value == y.Value,
            (UnitValue, UnitValue) => true,
            (EnumValue x, EnumValue y) => x.EnumName == y.EnumName && x.Value == y.Value,
            (NullableValue x, NullableValue y) => (x.IsNone && y.IsNone) || (!x.IsNone && !y.IsNone && EqualsValue(x.Value!, y.Value!)),
            (TupleValue x, TupleValue y) => x.Elements.Count == y.Elements.Count && x.Elements.Zip(y.Elements).All(p => EqualsValue(p.First, p.Second)),
            (StructValue x, StructValue y) => ReferenceEquals(x, y) || (!x.IsClass && !y.IsClass && x.StructName == y.StructName && x.Fields.Count == y.Fields.Count && x.Fields.All(kv => y.Fields.TryGetValue(kv.Key, out var ov) && EqualsValue(kv.Value, ov))),
            _ => ReferenceEquals(a, b),
        };
    }

    private static string TypeNameOf(TypeRef type) => type switch
    {
        NamedTypeRef n => n.Args.Count == 0 ? n.Name : $"{n.Name}[{string.Join(", ", n.Args.Select(TypeNameOf))}]",
        TupleTypeRef t => t.Elements.Count == 0 ? "()" : $"({string.Join(", ", t.Elements.Select(TypeNameOf))})",
        OpTypeRef o => o.Lhs != null ? $"{TypeNameOf(o.Lhs)} <> {TypeNameOf(o.Rhs)} -> {TypeNameOf(o.Result)}" : $"{TypeNameOf(o.Rhs)} -> {TypeNameOf(o.Result)}",
        DynTypeRef d => $"dyn {TypeNameOf(d.Inner)}",
        _ => "any",
    };
}
