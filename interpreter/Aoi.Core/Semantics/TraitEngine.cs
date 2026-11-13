using Aoi.Interpreter.Ast;

namespace Aoi.Interpreter.Semantics;

public readonly struct TraitKey : IEquatable<TraitKey>
{
    public string Trait { get; }
    public string Arguments { get; }

    public TraitKey(string trait, IEnumerable<AoiType> arguments)
    {
        Trait = trait;
        Arguments = string.Join("|", arguments.Select(a => a.ToString()));
    }

    public static TraitKey From(string trait, IEnumerable<AoiType> arguments) => new(trait, arguments);

    public bool Equals(TraitKey other) => Trait == other.Trait && Arguments == other.Arguments;
    public override bool Equals(object? obj) => obj is TraitKey key && Equals(key);
    public override int GetHashCode() => HashCode.Combine(Trait, Arguments);
    public override string ToString() => Arguments.Length == 0 ? Trait : $"{Trait}[{Arguments}]";
}

/// <summary>
/// A concrete implementation selected for a trait requirement, together with
/// the implementations its own generic requirements depend on.
/// </summary>
public sealed class ImplBinding
{
    public required string TraitName { get; init; }
    public required IReadOnlyList<AoiType> TraitArgs { get; init; }
    public ImplDefinition? Impl { get; init; }
    public TypeSubstitution Substitution { get; init; } = new();
    public Dictionary<TraitKey, ImplBinding> Dependencies { get; } = new();
    public bool IsBuiltin { get; init; }

    public TraitKey Key => TraitKey.From(TraitName, TraitArgs);

    public void AddDependency(ImplBinding binding)
    {
        if (Dependencies.TryGetValue(binding.Key, out var existing))
        {
            if (!SameImplementation(existing, binding))
                throw new AoiException(
                    $"Conflicting implementations for {binding.Key}: " +
                    $"{existing.Impl?.Name ?? "<default>"} and {binding.Impl?.Name ?? "<default>"}");
            return;
        }
        Dependencies[binding.Key] = binding;
    }

    private static bool SameImplementation(ImplBinding a, ImplBinding b)
        => a.IsBuiltin == b.IsBuiltin
           && a.Impl?.Name == b.Impl?.Name
           && a.TraitArgs.SequenceEqual(b.TraitArgs);

    public static ImplBinding Builtin(string trait, IReadOnlyList<AoiType> args)
        => new() { TraitName = trait, TraitArgs = args, IsBuiltin = true };

    public override string ToString()
        => IsBuiltin ? $"builtin {Key}" : $"{Impl?.Name ?? "<default>"} : {Key}";
}

public sealed class ImplContext
{
    private readonly Dictionary<TraitKey, ImplBinding> _bindings = new();
    public ImplContext? Parent { get; }

    public ImplContext(ImplContext? parent = null) => Parent = parent;

    public void Add(ImplBinding binding) => _bindings[binding.Key] = binding;

    public void AddRange(IEnumerable<ImplBinding> bindings)
    {
        foreach (var binding in bindings) Add(binding);
    }

    public bool TryGet(TraitKey key, out ImplBinding binding)
    {
        if (_bindings.TryGetValue(key, out binding!)) return true;
        if (Parent != null) return Parent.TryGet(key, out binding);
        binding = null!;
        return false;
    }

    public IEnumerable<ImplBinding> All
        => _bindings.Values.Concat(Parent?.All ?? Enumerable.Empty<ImplBinding>());
}

public sealed class TraitEngine
{
    private readonly SemanticModel _model;

    public TraitEngine(SemanticModel model) => _model = model;

    public bool IsBuiltinTrait(string name) => _model.GetTrait(name)?.IsBuiltin == true;

    /// <summary>
    /// Resolves a trait requirement such as <c>Ord[Vector]</c>. The current
    /// frame's implementation dictionaries are consulted first, which is how
    /// generic bodies keep using the impl selected by their caller.
    /// </summary>
    public ImplBinding ResolveRequirement(
        TraitRequirement requirement,
        TypeSubstitution substitution,
        ImplContext context,
        int line = 0,
        int column = 0)
    {
        var resolved = requirement.Substitute(substitution);
        if (context.TryGet(resolved.Key, out var existing))
            return existing;
        return FindImpl(resolved.TraitName, resolved.Args, null, substitution, context, line, column);
    }

    public ImplBinding FindImpl(
        string traitName,
        IReadOnlyList<AoiType> traitArgs,
        string? requestedName,
        TypeSubstitution substitution,
        ImplContext context,
        int line = 0,
        int column = 0)
    {
        var args = traitArgs.Select(a => a.Substitute(substitution)).ToList();

        // A named implementation can be requested explicitly.
        if (requestedName != null)
        {
            var named = _model.Impls.FirstOrDefault(i =>
                i.Name == requestedName && (i.TraitName == traitName || i.AliasOf != null));
            if (named == null)
                throw new AoiException($"Unknown implementation '{requestedName}'", line, column);

            if (named.AliasOf != null)
            {
                var aliased = _model.Impls.FirstOrDefault(i => i.Name == named.AliasOf)
                    ?? throw new AoiException(
                        $"Implementation alias '{named.Name}' refers to unknown '{named.AliasOf}'", named.Line, named.Column);
                return FindImpl(traitName, args, aliased.Name, substitution, context, line, column);
            }

            var matched = TryMatchImpl(named, traitName, args, substitution, context);
            if (matched == null)
                throw new AoiException(
                    $"Implementation '{requestedName}' does not implement {FormatTrait(traitName, args)}",
                    line, column);
            return matched;
        }

        // Default implementations.
        var defaults = _model.Impls
            .Where(i => i.IsTraitImpl && i.Name == null && i.TraitName == traitName && i.AliasOf == null)
            .ToList();

        ImplBinding? selected = null;
        foreach (var candidate in defaults)
        {
            var binding = TryMatchImpl(candidate, traitName, args, substitution, context);
            if (binding == null) continue;
            if (selected != null)
                throw new AoiException(
                    $"Ambiguous default implementation for {FormatTrait(traitName, args)}", line, column);
            selected = binding;
        }

        // Aliases act as defaults as well.
        foreach (var alias in _model.Impls.Where(i =>
                     i.IsTraitImpl && i.Name == null && i.TraitName == traitName && i.AliasOf != null))
        {
            var binding = ResolveAliasImpl(alias, traitName, args, substitution, context);
            if (binding == null) continue;
            if (selected != null)
                throw new AoiException(
                    $"Ambiguous default implementation for {FormatTrait(traitName, args)}", line, column);
            selected = binding;
        }

        if (selected != null) return selected;

        if (IsBuiltinTrait(traitName) && BuiltinSupports(traitName, args))
            return ImplBinding.Builtin(traitName, args);

        throw new AoiException(
            $"No implementation of {FormatTrait(traitName, args)}", line, column);
    }

    private ImplBinding? ResolveAliasImpl(
        ImplDefinition alias,
        string traitName,
        IReadOnlyList<AoiType> args,
        TypeSubstitution substitution,
        ImplContext context)
    {
        if (alias.AliasOf == null) return null;
        var target = _model.Impls.FirstOrDefault(i => i.Name == alias.AliasOf);
        if (target == null)
            throw new AoiException(
                $"Implementation alias '{alias.Name ?? alias.TraitName}' refers to unknown '{alias.AliasOf}'",
                alias.Line, alias.Column);
        return FindImpl(traitName, args, target.Name, substitution, context);
    }

    private ImplBinding? TryMatchImpl(
        ImplDefinition impl,
        string requestedTrait,
        IReadOnlyList<AoiType> requestedArgs,
        TypeSubstitution callerSubstitution,
        ImplContext context)
    {
        if (impl.TraitName == null) return null;
        if (impl.TraitName != requestedTrait) return null;
        if (impl.TraitArgSyntax.Count != requestedArgs.Count) return null;

        // Fresh copy of the impl's own parameters for this use site.
        var local = new TypeSubstitution();
        var scope = new Dictionary<string, AoiType>();
        foreach (var parameter in impl.TypeParams)
        {
            var fresh = TypeVar.Fresh(parameter.Name);
            local.Bind(parameter.Variable, fresh);
            scope[parameter.Name] = fresh;
        }

        // Match the head: impl Trait[A, B] against the requested arguments.
        for (int i = 0; i < impl.TraitArgSyntax.Count; i++)
        {
            var pattern = _model.Resolver.Resolve(impl.TraitArgSyntax[i].Type, scope).Substitute(local);
            var actual = requestedArgs[i].Substitute(callerSubstitution);
            if (!TypeUnifier.TryUnify(pattern, actual, local))
                return null;
        }

        var binding = new ImplBinding
        {
            TraitName = requestedTrait,
            TraitArgs = requestedArgs.Select(a => a.Substitute(callerSubstitution)).ToList(),
            Impl = impl,
            Substitution = local,
        };

        // Solve requirements declared on the trait's own type parameters.
        var trait = _model.GetTrait(requestedTrait);
        var traitParamScope = new Dictionary<string, AoiType>(scope);
        if (trait != null)
        {
            for (int i = 0; i < trait.TypeParams.Count && i < impl.TraitArgSyntax.Count; i++)
            {
                var traitParam = trait.TypeParams[i];
                traitParamScope[traitParam.Name] =
                    _model.Resolver.Resolve(impl.TraitArgSyntax[i].Type, scope).Substitute(local);
                var explicitRefs = impl.TraitArgSyntax[i].Impls;
                foreach (var reqSyntax in traitParam.RequirementSyntax)
                {
                    var requirement = new TraitRequirement(
                        reqSyntax.TraitName,
                        reqSyntax.Args.Select(a => _model.Resolver.Resolve(a, traitParamScope).Substitute(local)));
                    foreach (var resolved in ResolveDependencies(requirement, explicitRefs, local, context))
                        binding.AddDependency(resolved);
                }
            }
        }

        // Solve requirements declared on the impl's own generic parameters.
        foreach (var parameter in impl.TypeParams)
        {
            foreach (var reqSyntax in parameter.RequirementSyntax)
            {
                var requirement = new TraitRequirement(
                    reqSyntax.TraitName,
                    reqSyntax.Args.Select(a => _model.Resolver.Resolve(a, scope).Substitute(local)));
                if (!requirement.Args.All(a => a.IsConcrete)) continue;
                foreach (var resolved in ResolveDependencies(requirement, Array.Empty<ImplRefSyntax>(), local, context))
                    binding.AddDependency(resolved);
            }
        }

        return binding;
    }

    /// <summary>
    /// Creates a binding with fresh type variables for a candidate impl,
    /// without solving its requirements yet. Overload resolution binds those
    /// variables against the actual operand types first.
    /// </summary>
    public ImplBinding CreateProvisionalBinding(ImplDefinition impl)
    {
        var local = new TypeSubstitution();
        var scope = new Dictionary<string, AoiType>();
        foreach (var parameter in impl.TypeParams)
        {
            var fresh = TypeVar.Fresh(parameter.Name);
            local.Bind(parameter.Variable, fresh);
            scope[parameter.Name] = fresh;
        }
        var traitArgs = impl.TraitArgSyntax
            .Select(arg => _model.Resolver.Resolve(arg.Type, scope).Substitute(local))
            .ToList();
        return new ImplBinding
        {
            TraitName = impl.TraitName ?? "",
            TraitArgs = traitArgs,
            Impl = impl,
            Substitution = local,
        };
    }

    /// <summary>
    /// Instantiates a specific impl selected by overload resolution. The
    /// caller may already have bound its generic parameters while matching a
    /// member signature; those bindings are carried in <paramref name="initial"/>.
    /// </summary>
    public ImplBinding InstantiateImpl(
        ImplDefinition impl,
        TypeSubstitution initial,
        ImplContext context)
    {
        var local = initial.Clone();
        var scope = new Dictionary<string, AoiType>();
        foreach (var parameter in impl.TypeParams)
        {
            if (local.TryGet(parameter.Variable, out var bound))
            {
                scope[parameter.Name] = bound;
            }
            else
            {
                var fresh = TypeVar.Fresh(parameter.Name);
                local.Bind(parameter.Variable, fresh);
                scope[parameter.Name] = fresh;
            }
        }

        var traitArgs = new List<AoiType>();
        foreach (var arg in impl.TraitArgSyntax)
            traitArgs.Add(_model.Resolver.Resolve(arg.Type, scope).Substitute(local));

        var binding = new ImplBinding
        {
            TraitName = impl.TraitName ?? "",
            TraitArgs = traitArgs,
            Impl = impl,
            Substitution = local,
        };

        var trait = _model.GetTrait(impl.TraitName ?? "");
        var traitParamScope = new Dictionary<string, AoiType>(scope);
        if (trait != null)
        {
            for (int i = 0; i < trait.TypeParams.Count && i < impl.TraitArgSyntax.Count; i++)
            {
                traitParamScope[trait.TypeParams[i].Name] =
                    _model.Resolver.Resolve(impl.TraitArgSyntax[i].Type, scope).Substitute(local);
                var explicitRefs = impl.TraitArgSyntax[i].Impls;
                foreach (var reqSyntax in trait.TypeParams[i].RequirementSyntax)
                {
                    var requirement = new TraitRequirement(
                        reqSyntax.TraitName,
                        reqSyntax.Args.Select(a => _model.Resolver.Resolve(a, traitParamScope).Substitute(local)));
                    if (!requirement.Args.All(a => a.IsConcrete)) continue;
                    foreach (var resolved in ResolveDependencies(requirement, explicitRefs, local, context))
                        binding.AddDependency(resolved);
                }
            }
        }

        foreach (var parameter in impl.TypeParams)
        {
            foreach (var reqSyntax in parameter.RequirementSyntax)
            {
                var requirement = new TraitRequirement(
                    reqSyntax.TraitName,
                    reqSyntax.Args.Select(a => _model.Resolver.Resolve(a, scope).Substitute(local)));
                foreach (var resolved in ResolveDependencies(requirement, Array.Empty<ImplRefSyntax>(), local, context))
                    binding.AddDependency(resolved);
            }
        }

        return binding;
    }

    /// <summary>
    /// Solves one requirement, optionally preferring an explicitly written
    /// impl (docs 4.4: <c>Ord[Vector: PartialOrdByX[Vector]]</c>).
    /// </summary>
    public ImplBinding ResolveDependency(
        TraitRequirement requirement,
        IReadOnlyList<ImplRefSyntax> explicitRefs,
        TypeSubstitution substitution,
        ImplContext context)
    {
        var resolved = requirement.Substitute(substitution);

        foreach (var reference in explicitRefs)
        {
            var binding = TryResolveImplRef(reference, resolved, substitution, context);
            if (binding != null) return binding;
        }

        if (context.TryGet(resolved.Key, out var existing)) return existing;

        // Shorthand: T: Vector[T] means all requirements of Vector's parameters.
        if (_model.GetTrait(resolved.TraitName) == null
            && _model.GetType(resolved.TraitName) is { } type)
        {
            var expanded = ExpandTypeRequirement(type, resolved.Args);
            if (expanded.Count == 0)
                throw new AoiException($"Type '{type.Name}' has no generic requirements to satisfy");
            // A shorthand requirement is really a conjunction; the caller only
            // receives the first binding, but all dependencies are installed in
            // its dependency table when the enclosing impl is selected.
            return ResolveRequirement(expanded[0], substitution, context);
        }

        return FindImpl(resolved.TraitName, resolved.Args, null, substitution, context);
    }

    /// <summary>
    /// Resolves a requirement that may be a type shorthand such as
    /// <c>T: Vector[T]</c> (docs 4.4). A type shorthand expands to all
    /// requirements of that type's parameters.
    /// </summary>
    public List<ImplBinding> ResolveDependencies(
        TraitRequirement requirement,
        IReadOnlyList<ImplRefSyntax> explicitRefs,
        TypeSubstitution substitution,
        ImplContext context)
    {
        var resolved = requirement.Substitute(substitution);
        if (_model.GetTrait(resolved.TraitName) == null
            && _model.GetType(resolved.TraitName) is { } type)
        {
            var expanded = ExpandTypeRequirement(type, resolved.Args);
            var list = new List<ImplBinding>();
            foreach (var item in expanded)
            {
                if (!item.Args.All(a => a.IsConcrete)) continue;
                list.Add(ResolveDependency(item, explicitRefs, new TypeSubstitution(), context));
            }
            return list;
        }
        return new List<ImplBinding> { ResolveDependency(requirement, explicitRefs, substitution, context) };
    }

    private ImplBinding? TryResolveImplRef(
        ImplRefSyntax reference,
        TraitRequirement requirement,
        TypeSubstitution substitution,
        ImplContext context)
    {
        var impl = _model.Impls.FirstOrDefault(i => i.Name == reference.Name);
        if (impl != null)
        {
            // If the ref carries its own trait arguments use them; otherwise use
            // the arguments from the requirement.
            IReadOnlyList<AoiType> args;
            if (reference.Args.Count > 0)
            {
                var scope = new Dictionary<string, AoiType>();
                args = reference.Args.Select(a => _model.Resolver.Resolve(a, scope).Substitute(substitution)).ToList();
            }
            else
            {
                args = requirement.Args;
            }

            if (impl.TraitName != requirement.TraitName && impl.TraitName != null)
                return null;

            return FindImpl(requirement.TraitName, args, impl.Name, substitution, context,
                impl.Line, impl.Column);
        }

        // A trait name in an impl-ref position means "the default impl".
        if (_model.GetTrait(reference.Name) != null)
        {
            var args = reference.Args.Count > 0
                ? reference.Args.Select(a => _model.Resolver.Resolve(a)).ToList()
                : requirement.Args.ToList();
            return FindImpl(reference.Name, args, null, substitution, context);
        }

        return null;
    }

    private List<TraitRequirement> ExpandTypeRequirement(TypeDefinition type, IReadOnlyList<AoiType> args)
    {
        var result = new List<TraitRequirement>();
        if (args.Count != type.TypeParams.Count) return result;
        var scope = new Dictionary<string, AoiType>();
        for (int i = 0; i < type.TypeParams.Count; i++) scope[type.TypeParams[i].Name] = args[i];
        foreach (var parameter in type.TypeParams)
        {
            foreach (var req in parameter.RequirementSyntax)
            {
                result.Add(new TraitRequirement(req.TraitName,
                    req.Args.Select(a => _model.Resolver.Resolve(a, scope))));
            }
        }
        return result;
    }

    private static bool BuiltinSupports(string trait, IReadOnlyList<AoiType> args)
    {
        if (args.Count == 0) return false;
        var type = args[0];
        if (type is NullableType) return false;

        return trait switch
        {
            "Eq" => type.IsConcrete,
            "ToStr" => type.IsConcrete,
            "Add" => type is PrimitiveType p && p.Name is "int" or "float" or "str" or "char",
            "Sub" or "Mul" or "Div" or "Rem" =>
                type is PrimitiveType p && p.Name is "int" or "float",
            "Neg" => type is PrimitiveType p && p.Name is "int" or "float",
            "PartialOrd" or "Ord" =>
                type is PrimitiveType p && p.Name is "int" or "float" or "char" or "str",
            "Iter" when args.Count >= 2 =>
                type is NamedType n && n.Name is "arr" or "Span",
            _ => false,
        };
    }

    private static string FormatTrait(string name, IReadOnlyList<AoiType> args)
        => args.Count == 0 ? name : $"{name}[{string.Join(", ", args)}]";

    /// <summary>
    /// Returns the operator definition to invoke for a trait member in a
    /// concrete impl. Builtin impls return a synthetic definition handled by
    /// the evaluator.
    /// </summary>
    public OperatorDefinition? FindMember(ImplBinding binding, string name, bool binary, bool allowQualified = false)
    {
        if (binding.IsBuiltin) return null;
        var implMember = binding.Impl?.Members.FirstOrDefault(m =>
            m.Name == name && m.IsBinary == binary && (allowQualified || m.Qualifier == null));
        if (implMember != null && implMember.HasBody) return implMember;
        return binding.Impl?.Trait?.FindMembers(name, binary)
            .FirstOrDefault(m => m.HasBody && (allowQualified || m.Qualifier == null));
    }

    public TypeSubstitution BindingSubstitution(ImplBinding binding) => binding.Substitution;
}
