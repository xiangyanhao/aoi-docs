using Aoi.Interpreter.Ast;
using Aoi.Interpreter.Runtime;
using Environment = Aoi.Interpreter.Runtime.Environment;

namespace Aoi.Interpreter.Semantics;

public sealed class ResolvedOperator
{
    public OperatorDefinition? Definition { get; init; }
    public ImplBinding? Impl { get; init; }
    public bool IsBuiltin { get; init; }
    public string? BuiltinKey { get; init; }
    public required string Name { get; init; }
    public bool IsBinary { get; init; }
    public TypeSubstitution Substitution { get; init; } = new();
    public Dictionary<string, AoiType> TypeBindings { get; init; } = new();
    public List<ImplBinding> Dependencies { get; init; } = new();
    public AoiType? LhsType { get; init; }
    public AoiType? RhsType { get; init; }
    public AoiType? ResultType { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }

    public override string ToString()
        => IsBuiltin ? $"builtin {Name}" : $"{Definition} ({Impl})";
}

/// <summary>
/// Type-directed overload resolution shared by the evaluator. Candidates are
/// matched by unifying their declared signatures with the runtime argument
/// types; generic parameters and trait requirements are solved along the way.
/// </summary>
public sealed class OperatorResolver
{
    private readonly SemanticModel _model;
    private readonly TraitEngine _traits;

    public OperatorResolver(SemanticModel model)
    {
        _model = model;
        _traits = model.TraitEngine;
    }

    public ResolvedOperator Resolve(
        string name,
        bool binary,
        IReadOnlyList<AoiType> arguments,
        Environment environment,
        IReadOnlyList<TypeArgumentSyntax>? explicitArguments = null,
        string? qualifier = null,
        int line = 0,
        int column = 0)
    {
        if (qualifier != null)
        {
            var qualified = ResolveQualified(name, binary, arguments, environment, explicitArguments, qualifier, line, column);
            if (qualified != null) return qualified;
            throw new AoiException(
                $"No operator '{name}' in '{qualifier}' for {Format(arguments)}", line, column);
        }

        // 1. The current generic frame's selected impls have priority.
        var fromFrame = ResolveFromFrame(name, binary, arguments, environment, line, column);
        if (fromFrame != null) return fromFrame;

        // 2. Program-defined operators / impl members.
        var candidates = new List<ResolvedOperator>();
        var explicitImpls = CollectExplicitImpls(explicitArguments);

        foreach (var op in _model.Operators)
        {
            if (op.Name != name || op.IsBinary != binary) continue;
            if (op.Kind != OperatorKind.Normal) continue;
            var matched = TryMatch(op, null, arguments, environment, new TypeSubstitution(), explicitArguments, explicitImpls);
            if (matched != null) candidates.Add(matched);
        }

        foreach (var impl in _model.Impls.Where(i => !i.IsTraitImpl || i.IsDefault))
        {
            foreach (var member in impl.Members)
            {
                if (member.Name != name || member.IsBinary != binary || member.Kind != OperatorKind.Normal
                    || member.Qualifier != null)
                    continue;
                var binding = _traits.CreateProvisionalBinding(impl);
                var matched = TryMatch(member, binding, arguments, environment, binding.Substitution.Clone(),
                    explicitArguments, explicitImpls);
                if (matched != null) candidates.Add(matched);
            }
        }

        // Members of concrete impls that are aliased as defaults. For
        //   impl of Ord[V] = ByX;
        // ByX's members participate in unqualified resolution.
        foreach (var impl in EffectiveDefaultImpls().Where(i => i.Name != null))
        {
            var binding = _traits.CreateProvisionalBinding(impl);
            foreach (var member in impl.Members)
            {
                if (member.Name != name || member.IsBinary != binary || member.Kind != OperatorKind.Normal
                    || member.Qualifier != null)
                    continue;
                var matched = TryMatch(member, binding, arguments, environment, binding.Substitution.Clone(),
                    explicitArguments, explicitImpls);
                if (matched != null) candidates.Add(matched);
            }
        }

        // Default trait methods (providing a body) for default impls.
        foreach (var impl in EffectiveDefaultImpls().Where(i => i.Trait != null))
        {
            var binding = _traits.CreateProvisionalBinding(impl);
            foreach (var member in impl.Trait!.Members)
            {
                if (member.Name != name || member.IsBinary != binary || !member.HasBody || member.Qualifier != null) continue;
                var matched = TryMatch(member, binding, arguments, environment, binding.Substitution.Clone(),
                    explicitArguments, explicitImpls);
                if (matched != null) candidates.Add(matched);
            }
        }

        var distinct = Deduplicate(candidates);
        if (distinct.Count == 1) return distinct[0];
        if (distinct.Count > 1)
        {
            // Prefer a candidate with no unresolved type variables; if several
            // remain, the program is ambiguous.
            var concrete = distinct.Where(d => d.ResultType?.IsConcrete == true).ToList();
            if (concrete.Count == 1) return concrete[0];
            throw new AoiException(
                $"Ambiguous operator '{name}' for {Format(arguments)}: " +
                string.Join(", ", distinct.Select(d => d.ToString())), line, column);
        }

        // 3. Built-in numeric / comparison operators.
        var builtin = TryBuiltin(name, binary, arguments, line, column);
        if (builtin != null) return builtin;

        throw new AoiException($"No operator '{name}' for {Format(arguments)}", line, column);
    }

    /// <summary>
    /// Default impls plus the concrete impls they alias. An
    /// <c>impl of Trait[T] = Named;</c> declaration makes Named the default,
    /// so its members and default methods participate in resolution.
    /// </summary>
    private IEnumerable<ImplDefinition> EffectiveDefaultImpls()
    {
        foreach (var impl in _model.Impls.Where(i => i.IsTraitImpl && i.Name == null))
        {
            if (impl.AliasOf == null)
            {
                yield return impl;
                continue;
            }
            var target = _model.Impls.FirstOrDefault(i => i.Name == impl.AliasOf);
            if (target != null) yield return target;
        }
    }

    private ResolvedOperator? ResolveQualified(
        string name,
        bool binary,
        IReadOnlyList<AoiType> arguments,
        Environment environment,
        IReadOnlyList<TypeArgumentSyntax>? explicitArguments,
        string qualifier,
        int line,
        int column)
    {
        // Trait[args]::op: use the selected impl from the frame when possible,
        // otherwise the default impl.
        var trait = _model.GetTrait(qualifier);
        if (trait != null)
        {
            IReadOnlyList<AoiType> traitArgs;
            if (explicitArguments is { Count: > 0 })
            {
                traitArgs = explicitArguments
                    .Where(a => a.Type != null)
                    .Select(a => ResolveType(a.Type!, environment, new Dictionary<string, AoiType>()))
                    .ToList();
            }
            else
            {
                traitArgs = InferTraitArguments(trait, name, binary, arguments, environment);
            }

            var binding = _traits.FindImpl(trait.Name, traitArgs, null, new TypeSubstitution(), environment.Impls, line, column);
            return ResolveFromBinding(binding, name, binary, arguments, environment, line, column, allowQualified: true);
        }

        // Impl::op.
        var impl = _model.Impls.FirstOrDefault(i => i.Name == qualifier);
        if (impl == null) return null;
        var implBinding = _traits.CreateProvisionalBinding(impl);
        return ResolveFromBinding(implBinding, name, binary, arguments, environment, line, column, allowQualified: true);
    }

    private IReadOnlyList<AoiType> InferTraitArguments(
        TraitDefinition trait,
        string name,
        bool binary,
        IReadOnlyList<AoiType> arguments,
        Environment environment)
    {
        var member = trait.FindMembers(name, binary).FirstOrDefault();
        if (member == null)
            return trait.TypeParams.Select(p => (AoiType)TypeVar.Fresh(p.Name)).ToList();

        var substitution = new TypeSubstitution();
        var scope = new Dictionary<string, AoiType>();
        foreach (var parameter in trait.TypeParams)
        {
            var fresh = TypeVar.Fresh(parameter.Name);
            substitution.Bind(parameter.Variable, fresh);
            scope[parameter.Name] = fresh;
        }
        var signature = SignatureTypes(member, scope, scope, scope);
        MatchArguments(signature, arguments, substitution);
        return trait.TypeParams.Select(p => p.Variable.Substitute(substitution)).ToList();
    }

    private ResolvedOperator? ResolveFromFrame(
        string name,
        bool binary,
        IReadOnlyList<AoiType> arguments,
        Environment environment,
        int line,
        int column)
    {
        var bindings = environment.Impls.All.GroupBy(b => b.Key).Select(g => g.First()).ToList();
        var matches = new List<ResolvedOperator>();
        foreach (var binding in bindings)
        {
            var matched = ResolveFromBinding(binding, name, binary, arguments, environment, line, column, throwIfMissing: false);
            if (matched != null) matches.Add(matched);
        }
        if (matches.Count == 0) return null;
        if (matches.Count == 1) return matches[0];

        // Different trait requirements may expose the same operator name. A
        // concrete result type disambiguates; otherwise report ambiguity.
        var concrete = matches.Where(m => m.ResultType?.IsConcrete == true).ToList();
        if (concrete.Count == 1) return concrete[0];
        throw new AoiException(
            $"Ambiguous operator '{name}' from generic requirements: " +
            string.Join(", ", matches.Select(m => m.Impl?.ToString() ?? m.ToString())), line, column);
    }

    private ResolvedOperator? ResolveFromBinding(
        ImplBinding binding,
        string name,
        bool binary,
        IReadOnlyList<AoiType> arguments,
        Environment environment,
        int line,
        int column,
        bool throwIfMissing = true,
        bool allowQualified = false)
    {
        if (binding.IsBuiltin)
        {
            var trait = _model.GetTrait(binding.TraitName);
            if (trait != null && !trait.FindMembers(name, binary).Any())
                return null;
            return TryBuiltinForTrait(binding, name, binary, arguments, line, column);
        }

        var member = _traits.FindMember(binding, name, binary, allowQualified);
        if (member == null) return null;

        var initial = binding.Substitution.Clone();
        return TryMatch(member, binding, arguments, environment, initial, null, Array.Empty<ImplRefSyntax>(), line, column);
    }

    private ResolvedOperator? TryBuiltinForTrait(
        ImplBinding binding,
        string name,
        bool binary,
        IReadOnlyList<AoiType> arguments,
        int line,
        int column)
    {
        var result = BuiltinResult(name, binary, arguments);
        if (result == null) return null;
        return new ResolvedOperator
        {
            Name = name,
            IsBinary = binary,
            IsBuiltin = true,
            BuiltinKey = name,
            ResultType = result,
            Impl = binding,
            Dependencies = binding.Dependencies.Values.ToList(),
            LhsType = binary && arguments.Count > 0 ? arguments[0] : null,
            RhsType = arguments.Count > 0 ? arguments[^1] : null,
            Line = line,
            Column = column,
        };
    }

    private ResolvedOperator? TryMatch(
        OperatorDefinition op,
        ImplBinding? impl,
        IReadOnlyList<AoiType> arguments,
        Environment environment,
        TypeSubstitution initial,
        IReadOnlyList<TypeArgumentSyntax>? explicitArguments,
        IReadOnlyList<ImplRefSyntax> explicitImpls,
        int line = 0,
        int column = 0)
    {
        var local = initial.Clone();

        // Keep the three namespaces separate: a trait parameter and an impl
        // parameter may both be called T, but a member signature inherited
        // from the trait must use the trait binding.
        var traitScope = new Dictionary<string, AoiType>(StringComparer.Ordinal);
        var implScope = new Dictionary<string, AoiType>(StringComparer.Ordinal);
        var ownScope = new Dictionary<string, AoiType>(StringComparer.Ordinal);

        if (op.Trait != null)
        {
            if (impl != null)
            {
                for (int i = 0; i < op.Trait.TypeParams.Count && i < impl.TraitArgs.Count; i++)
                    traitScope[op.Trait.TypeParams[i].Name] = impl.TraitArgs[i].Substitute(local);
            }
            else
            {
                foreach (var parameter in op.Trait.TypeParams)
                {
                    var fresh = TypeVar.Fresh(parameter.Name);
                    local.Bind(parameter.Variable, fresh);
                    traitScope[parameter.Name] = fresh;
                }
            }
        }

        if (op.Impl != null)
        {
            foreach (var parameter in op.Impl.TypeParams)
            {
                if (local.TryGet(parameter.Variable, out var bound))
                    implScope[parameter.Name] = bound;
                else
                    implScope[parameter.Name] = parameter.Variable;
            }
        }

        foreach (var parameter in op.TypeParams)
        {
            var fresh = TypeVar.Fresh(parameter.Name);
            local.Bind(parameter.Variable, fresh);
            ownScope[parameter.Name] = fresh;
        }

        // Explicit type arguments: max[int] / max[Vector: ByX] / sum[Pair[A, B]].
        if (explicitArguments != null)
        {
            int index = 0;
            foreach (var argument in explicitArguments)
            {
                var typeName = argument.Type != null ? TypeNameOf(argument.Type) : null;
                bool implOnly = argument.Type != null
                    && argument.Impls.Count == 0
                    && typeName != null
                    && _model.GetTrait(typeName) != null;
                if (argument.Type != null && !implOnly && index < op.TypeParams.Count)
                {
                    var explicitType = ResolveType(argument.Type, environment, ownScope);
                    local.Bind(op.TypeParams[index].Variable, explicitType);
                    ownScope[op.TypeParams[index].Name] = explicitType;
                }
                index++;
            }
        }

        var signature = SignatureTypes(op, traitScope, implScope, ownScope);
        if (!MatchArguments(signature, arguments, local))
            return null;

        var dependencies = new List<ImplBinding>();
        var effectiveImpl = impl;
        if (impl != null && op.Impl != null)
        {
            // Re-instantiate after operand unification has bound the impl's
            // generic parameters, so its requirements become concrete.
            try { effectiveImpl = _traits.InstantiateImpl(op.Impl, local, environment.Impls); }
            catch (AoiException) { effectiveImpl = impl; }
        }
        else if (impl?.Impl != null)
        {
            // Trait default method: keep the concrete impl selected for the
            // receiver, with its generic arguments substituted.
            try { effectiveImpl = _traits.InstantiateImpl(impl.Impl, local, environment.Impls); }
            catch (AoiException) { effectiveImpl = impl; }
        }
        if (effectiveImpl != null)
        {
            dependencies.Add(effectiveImpl);
            dependencies.AddRange(effectiveImpl.Dependencies.Values);
        }

        var effectiveImpls = explicitImpls
            .Concat(CollectExplicitImpls(explicitArguments))
            .GroupBy(r => (r.Name, string.Join(",", r.Args.Select(a => a.ToString()))))
            .Select(g => g.First())
            .ToList();

        // Requirements on the operator's own parameters. Impl/trait names are
        // available as dependency types, but own parameters take precedence.
        var constraintScope = new Dictionary<string, AoiType>(implScope, StringComparer.Ordinal);
        foreach (var (name, type) in traitScope) constraintScope.TryAdd(name, type);
        foreach (var (name, type) in ownScope) constraintScope[name] = type;

        foreach (var parameter in op.TypeParams)
        {
            foreach (var reqSyntax in parameter.RequirementSyntax)
            {
                var requirement = new TraitRequirement(
                    reqSyntax.TraitName,
                    reqSyntax.Args.Select(a => ResolveType(a, environment, constraintScope).Substitute(local)));
                if (!requirement.Args.All(a => a.IsConcrete)) continue;
                dependencies.AddRange(_traits.ResolveDependencies(requirement, effectiveImpls, local, environment.Impls));
            }
        }

        var resultType = signature.Result?.Substitute(local);
        var typeBindings = new Dictionary<string, AoiType>(StringComparer.Ordinal);
        foreach (var parameter in op.TypeParams)
        {
            if (local.TryGet(parameter.Variable, out var bound))
                typeBindings[parameter.Name] = local.Apply(bound);
        }

        return new ResolvedOperator
        {
            Definition = op,
            Impl = effectiveImpl,
            Name = op.Name,
            IsBinary = op.IsBinary,
            Substitution = local,
            TypeBindings = typeBindings,
            Dependencies = dependencies.GroupBy(d => d.Key).Select(g => g.First()).ToList(),
            LhsType = signature.Lhs?.Substitute(local),
            RhsType = signature.Rhs?.Substitute(local),
            ResultType = resultType,
            Line = line != 0 ? line : op.Line,
            Column = column != 0 ? column : op.Column,
        };
    }

    private sealed record SignatureTypesResult(AoiType? Lhs, AoiType? Rhs, AoiType? Index, AoiType? Result);

    private SignatureTypesResult SignatureTypes(
        OperatorDefinition op,
        Dictionary<string, AoiType> traitScope,
        Dictionary<string, AoiType> implScope,
        Dictionary<string, AoiType> ownScope)
    {
        var source = op;
        bool inherited = false;
        if (op.Trait != null && op.LhsType == null && op.RhsType == null && op.ResultType == null)
        {
            var traitMember = op.Trait.FindMembers(op.Name, op.IsBinary).FirstOrDefault();
            if (traitMember != null)
            {
                source = traitMember;
                inherited = true;
            }
        }

        var scope = new Dictionary<string, AoiType>(StringComparer.Ordinal);
        if (inherited)
        {
            // A signature inherited from the trait is written in the trait's
            // parameter namespace, even when an impl parameter has the same name.
            foreach (var (name, type) in traitScope) scope[name] = type;
        }
        else
        {
            foreach (var (name, type) in traitScope) scope[name] = type;
            foreach (var (name, type) in implScope) scope[name] = type;
            foreach (var (name, type) in ownScope) scope[name] = type;
        }

        AoiType? ResolveOrNull(TypeRef? type)
            => type == null ? null : _model.Resolver.Resolve(type, scope);

        return new SignatureTypesResult(
            ResolveOrNull(source.LhsType),
            ResolveOrNull(source.RhsType),
            ResolveOrNull(source.IndexType),
            ResolveOrNull(source.ResultType));
    }

    private static bool MatchArguments(SignatureTypesResult signature, IReadOnlyList<AoiType> arguments, TypeSubstitution substitution)
    {
        // Getter: one argument (self); index getter: two; setter: two.
        var expected = new List<AoiType?>();
        if (signature.Lhs != null) expected.Add(signature.Lhs);
        if (signature.Rhs != null) expected.Add(signature.Rhs);
        if (signature.Index != null) expected.Add(signature.Index);

        // If no types were written at all, accept arity only.
        if (expected.Count == 0) return true;
        if (expected.Count != arguments.Count) return false;

        for (int i = 0; i < expected.Count; i++)
        {
            if (expected[i] == null) continue;
            if (!TypeUnifier.TryUnify(expected[i]!, arguments[i], substitution)) return false;
        }
        return true;
    }

    private AoiType ResolveType(TypeRef type, Environment environment, Dictionary<string, AoiType> scope)
    {
        var combined = environment.TypeNameScope();
        foreach (var (name, value) in scope) combined[name] = value;
        return _model.Resolver.Resolve(type, combined);
    }

    private static string? TypeNameOf(TypeRef type) => type switch
    {
        NamedTypeRef n => n.Name,
        _ => null,
    };

    private List<ImplRefSyntax> CollectExplicitImpls(IReadOnlyList<TypeArgumentSyntax>? arguments)
    {
        var list = new List<ImplRefSyntax>();
        if (arguments == null) return list;
        foreach (var argument in arguments)
        {
            if (argument.Impls.Count > 0)
            {
                list.AddRange(argument.Impls);
                continue;
            }
            // max[Ord[int]] is an impl-only argument: the trait name in type
            // position denotes its default implementation.
            if (argument.Type is NamedTypeRef named && _model.GetTrait(named.Name) != null)
            {
                list.Add(new ImplRefSyntax
                {
                    Name = named.Name,
                    Args = named.Args.ToList(),
                    HasExplicitArgs = named.Args.Count > 0,
                });
            }
        }
        return list;
    }

    private static List<ResolvedOperator> Deduplicate(List<ResolvedOperator> candidates)
    {
        var result = new List<ResolvedOperator>();
        foreach (var candidate in candidates)
        {
            if (result.Any(r => r.Definition == candidate.Definition && r.Impl?.Key.Equals(candidate.Impl?.Key) != false))
                continue;
            result.Add(candidate);
        }
        return result;
    }

    private ResolvedOperator? TryBuiltin(string name, bool binary, IReadOnlyList<AoiType> arguments, int line, int column)
    {
        var result = BuiltinResult(name, binary, arguments);
        if (result == null) return null;
        return new ResolvedOperator
        {
            Name = name,
            IsBinary = binary,
            IsBuiltin = true,
            BuiltinKey = name,
            ResultType = result,
            LhsType = binary && arguments.Count > 0 ? arguments[0] : null,
            RhsType = arguments.Count > 0 ? arguments[^1] : null,
            Line = line,
            Column = column,
        };
    }

    /// <summary>Type of a built-in operator, or null when the operands are unsupported.</summary>
    public static AoiType? BuiltinResult(string name, bool binary, IReadOnlyList<AoiType> arguments)
    {
        if (arguments.Count == 0) return null;
        if (!arguments.All(a => a.IsConcrete))
        {
            // Some operators (==, !=, ToStr) work for any type, including
            // unresolved generic parameters.
            return name switch
            {
                "==" or "!=" => PrimitiveType.Bool,
                "toStr" => PrimitiveType.Str,
                "printLine" or "print" => UnitType.Instance,
                _ => null,
            };
        }

        var first = arguments[0];
        AoiType? result = null;
        if (name == "==" || name == "!=") result = PrimitiveType.Bool;
        else if (name == "toStr") result = PrimitiveType.Str;
        else if (name is "printLine" or "print") result = UnitType.Instance;
        else if (name == "!" && !binary && first is PrimitiveType { Name: "bool" }) result = PrimitiveType.Bool;
        else if (name == "~" && !binary && first is PrimitiveType { Name: "int" }) result = PrimitiveType.Int;
        else if (name is "+" or "-" && !binary && first is PrimitiveType { Name: "int" or "float" }) result = first;
        else if (binary)
        {
            var second = arguments[1];
            result = (name, first, second) switch
            {
                ("+", PrimitiveType { Name: "int" }, PrimitiveType { Name: "int" }) => PrimitiveType.Int,
                ("+", PrimitiveType { Name: "float" }, PrimitiveType { Name: "float" }) => PrimitiveType.Float,
                ("+", PrimitiveType { Name: "int" }, PrimitiveType { Name: "float" }) => PrimitiveType.Float,
                ("+", PrimitiveType { Name: "float" }, PrimitiveType { Name: "int" }) => PrimitiveType.Float,
                ("+", PrimitiveType { Name: "str" }, PrimitiveType { Name: "str" }) => PrimitiveType.Str,
                ("+", PrimitiveType { Name: "str" }, PrimitiveType { Name: "char" }) => PrimitiveType.Str,
                ("+", PrimitiveType { Name: "char" }, PrimitiveType { Name: "int" }) => PrimitiveType.Char,
                ("-", PrimitiveType { Name: "int" }, PrimitiveType { Name: "int" }) => PrimitiveType.Int,
                ("-", PrimitiveType { Name: "float" }, PrimitiveType { Name: "float" }) => PrimitiveType.Float,
                ("-", PrimitiveType { Name: "int" }, PrimitiveType { Name: "float" }) => PrimitiveType.Float,
                ("-", PrimitiveType { Name: "float" }, PrimitiveType { Name: "int" }) => PrimitiveType.Float,
                ("-", PrimitiveType { Name: "char" }, PrimitiveType { Name: "int" }) => PrimitiveType.Char,
                ("*", PrimitiveType { Name: "int" }, PrimitiveType { Name: "int" }) => PrimitiveType.Int,
                ("*", PrimitiveType { Name: "float" }, PrimitiveType { Name: "float" }) => PrimitiveType.Float,
                ("*", PrimitiveType { Name: "int" }, PrimitiveType { Name: "float" }) => PrimitiveType.Float,
                ("*", PrimitiveType { Name: "float" }, PrimitiveType { Name: "int" }) => PrimitiveType.Float,
                ("/", PrimitiveType { Name: "int" }, PrimitiveType { Name: "int" }) => PrimitiveType.Int,
                ("/", PrimitiveType { Name: "float" }, PrimitiveType { Name: "float" }) => PrimitiveType.Float,
                ("/", PrimitiveType { Name: "int" }, PrimitiveType { Name: "float" }) => PrimitiveType.Float,
                ("/", PrimitiveType { Name: "float" }, PrimitiveType { Name: "int" }) => PrimitiveType.Float,
                ("%", PrimitiveType { Name: "int" }, PrimitiveType { Name: "int" }) => PrimitiveType.Int,
                ("%", PrimitiveType { Name: "float" }, PrimitiveType { Name: "float" }) => PrimitiveType.Float,
                ("<<", PrimitiveType { Name: "int" }, PrimitiveType { Name: "int" }) => PrimitiveType.Int,
                (">>", PrimitiveType { Name: "int" }, PrimitiveType { Name: "int" }) => PrimitiveType.Int,
                ("&", PrimitiveType { Name: "int" }, PrimitiveType { Name: "int" }) => PrimitiveType.Int,
                ("&", PrimitiveType { Name: "bool" }, PrimitiveType { Name: "bool" }) => PrimitiveType.Bool,
                ("|", PrimitiveType { Name: "int" }, PrimitiveType { Name: "int" }) => PrimitiveType.Int,
                ("|", PrimitiveType { Name: "bool" }, PrimitiveType { Name: "bool" }) => PrimitiveType.Bool,
                ("^", PrimitiveType { Name: "int" }, PrimitiveType { Name: "int" }) => PrimitiveType.Int,
                ("^", PrimitiveType { Name: "bool" }, PrimitiveType { Name: "bool" }) => PrimitiveType.Bool,
                ("<" or "<=" or ">" or ">=", PrimitiveType p, PrimitiveType q)
                    when p.Name == q.Name && p.Name is "int" or "float" or "char" or "str" => PrimitiveType.Bool,
                _ => null,
            };
        }
        return result;
    }

    private static string Format(IReadOnlyList<AoiType> arguments)
        => string.Join(", ", arguments);

    // --- Getters / setters / indexers / conversions / literals ---

    public ResolvedOperator? ResolveGetter(string name, AoiType selfType, Environment environment, int line = 0, int column = 0)
        => ResolveSpecial(OperatorKind.Getter, name, new[] { selfType }, environment, line, column);

    public ResolvedOperator? ResolveSetter(string name, AoiType selfType, AoiType valueType, Environment environment, int line = 0, int column = 0)
        => ResolveSpecial(OperatorKind.Setter, name, new[] { selfType, valueType }, environment, line, column);

    public ResolvedOperator? ResolveIndexGetter(AoiType selfType, AoiType indexType, Environment environment, int line = 0, int column = 0)
        => ResolveSpecial(OperatorKind.IndexGetter, "[]", new[] { selfType, indexType }, environment, line, column);

    public ResolvedOperator? ResolveIndexSetter(AoiType selfType, AoiType indexType, AoiType valueType, Environment environment, int line = 0, int column = 0)
        => ResolveSpecial(OperatorKind.IndexSetter, "[]", new[] { selfType, valueType, indexType }, environment, line, column);

    private ResolvedOperator? ResolveSpecial(
        OperatorKind kind,
        string name,
        IReadOnlyList<AoiType> arguments,
        Environment environment,
        int line,
        int column)
    {
        var candidates = new List<ResolvedOperator>();

        // Current generic frame first (trait getters / methods).
        foreach (var binding in environment.Impls.All.GroupBy(b => b.Key).Select(g => g.First()))
        {
            if (binding.IsBuiltin) continue;
            var member = binding.Impl?.Members.FirstOrDefault(m =>
                m.Kind == kind && m.Name == name && m.IsBinary == (kind is OperatorKind.IndexGetter or OperatorKind.IndexSetter or OperatorKind.Setter));
            if (member == null) continue;
            var matched = TryMatch(member, binding, arguments, environment, binding.Substitution.Clone(), null, Array.Empty<ImplRefSyntax>(), line, column);
            if (matched != null) candidates.Add(matched);
        }

        void AddDefinition(OperatorDefinition op, ImplBinding? binding)
        {
            if (op.Kind != kind) return;
            if (op.Name != name) return;
            var matched = binding == null
                ? TryMatch(op, null, arguments, environment, new TypeSubstitution(), null, Array.Empty<ImplRefSyntax>(), line, column)
                : TryMatch(op, binding, arguments, environment, binding.Substitution.Clone(), null, Array.Empty<ImplRefSyntax>(), line, column);
            if (matched != null) candidates.Add(matched);
        }

        foreach (var op in _model.Operators) AddDefinition(op, null);

        foreach (var impl in _model.Impls)
        {
            if (impl.IsTraitImpl && impl.Name != null) continue;
            if (impl.AliasOf != null) continue;
            var binding = impl.IsTraitImpl
                ? _traits.CreateProvisionalBinding(impl)
                : null;
            foreach (var member in impl.Members) AddDefinition(member, binding);
        }

        var distinct = Deduplicate(candidates);
        if (distinct.Count == 0) return null;
        if (distinct.Count == 1) return distinct[0];
        var concrete = distinct.Where(d => d.ResultType?.IsConcrete == true).ToList();
        if (concrete.Count == 1) return concrete[0];
        throw new AoiException($"Ambiguous {kind} '{name}' for {Format(arguments)}", line, column);
    }

    public ResolvedOperator? ResolveConvert(AoiType from, AoiType to, Environment environment, bool implicitOnly, int line = 0, int column = 0)
    {
        var candidates = new List<ResolvedOperator>();
        foreach (var op in _model.Operators.Where(o => o.Kind == OperatorKind.Convert))
        {
            if (implicitOnly && !op.IsImplicit) continue;
            var matched = TryMatch(op, null, new[] { from, to }, environment, new TypeSubstitution(), null, Array.Empty<ImplRefSyntax>(), line, column);
            if (matched != null) candidates.Add(matched);
        }
        foreach (var impl in _model.Impls)
        {
            if (impl.IsTraitImpl && impl.Name != null) continue;
            var binding = impl.IsTraitImpl ? _traits.CreateProvisionalBinding(impl) : null;
            foreach (var op in impl.Members.Where(o => o.Kind == OperatorKind.Convert))
            {
                if (implicitOnly && !op.IsImplicit) continue;
                var matched = binding == null
                    ? TryMatch(op, null, new[] { from, to }, environment, new TypeSubstitution(), null, Array.Empty<ImplRefSyntax>(), line, column)
                    : TryMatch(op, binding, new[] { from, to }, environment, binding.Substitution.Clone(), null, Array.Empty<ImplRefSyntax>(), line, column);
                if (matched != null) candidates.Add(matched);
            }
        }
        if (candidates.Count == 0) return null;
        return candidates[0];
    }

    public ResolvedOperator? ResolveLiteral(string suffix, AoiType valueType, Environment environment, int line = 0, int column = 0)
    {
        foreach (var op in _model.Operators.Where(o => o.Kind == OperatorKind.Literal && o.Suffix == suffix))
        {
            var matched = TryMatch(op, null, new[] { valueType }, environment, new TypeSubstitution(), null, Array.Empty<ImplRefSyntax>(), line, column);
            if (matched != null) return matched;
        }
        return null;
    }
}
