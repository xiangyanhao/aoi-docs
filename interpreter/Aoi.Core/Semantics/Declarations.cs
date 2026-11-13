using Aoi.Interpreter.Ast;

namespace Aoi.Interpreter.Semantics;

public enum TypeKind
{
    Struct,
    Class,
    Union,
    Enum,
    Builtin,
}

public sealed class TypeParameter
{
    public required string Name { get; init; }
    public List<TraitRequirementSyntax> RequirementSyntax { get; } = new();
    /// <summary>The stable type variable used when the declaration is referenced.</summary>
    public TypeVar Variable { get; } = new TypeVar("T");

    public TraitRequirement? ResolveRequirement(TypeResolver resolver, IReadOnlyDictionary<string, AoiType> scope, int index)
    {
        if (index >= RequirementSyntax.Count) return null;
        var syntax = RequirementSyntax[index];
        return new TraitRequirement(syntax.TraitName, syntax.Args.Select(a => resolver.Resolve(a, scope)));
    }
}

public sealed class TypeDefinition
{
    public required string Name { get; init; }
    public TypeKind Kind { get; init; }
    public List<TypeParameter> TypeParams { get; } = new();
    public List<(string Name, TypeRef Type)> Fields { get; } = new();
    public Dictionary<string, long> EnumMembers { get; } = new();
    public List<(string Name, TypeRef Type)> UnionCases { get; } = new();

    public bool IsClass => Kind == TypeKind.Class;
    public bool IsUnion => Kind == TypeKind.Union;
    public bool IsEnum => Kind == TypeKind.Enum;

    /// <summary>Scope mapping the declaration's parameter names to their type variables.</summary>
    public Dictionary<string, AoiType> ParameterScope()
    {
        var scope = new Dictionary<string, AoiType>();
        foreach (var p in TypeParams) scope[p.Name] = p.Variable;
        return scope;
    }
}

public sealed class TypeAliasDefinition
{
    public required string Name { get; init; }
    public List<TypeParameter> TypeParams { get; } = new();
    public required TypeRef Definition { get; init; }

    public Dictionary<string, AoiType> ParameterScope()
    {
        var scope = new Dictionary<string, AoiType>();
        foreach (var p in TypeParams) scope[p.Name] = p.Variable;
        return scope;
    }
}

public enum OperatorKind
{
    Normal,
    Getter,
    Setter,
    IndexGetter,
    IndexSetter,
    Convert,
    Literal,
}

/// <summary>
/// One callable operator. This unifies ordinary operators, getters/setters,
/// conversions and literal suffixes so that the resolver has a single
/// signature-matching path for all of them.
/// </summary>
public sealed class OperatorDefinition
{
    public required string Name { get; init; }
    public bool IsBinary { get; init; }
    public OperatorKind Kind { get; init; } = OperatorKind.Normal;

    public DeclPattern? Lhs { get; init; }
    public DeclPattern? Rhs { get; init; }
    public DeclPattern? Index { get; init; }
    public DeclPattern? Value { get; init; }

    public List<TypeParameter> TypeParams { get; } = new();
    public TypeRef? LhsType { get; init; }
    public TypeRef? RhsType { get; init; }
    public TypeRef? IndexType { get; init; }
    public TypeRef? ResultType { get; init; }

    public required Expr Body { get; init; }
    public bool IsDeclarationOnly { get; init; }
    public bool IsImplicit { get; init; }
    public string? Suffix { get; init; }
    public string? Qualifier { get; init; }

    public TraitDefinition? Trait { get; init; }
    public ImplDefinition? Impl { get; init; }

    public int Line { get; init; }
    public int Column { get; init; }

    public bool HasBody => !IsDeclarationOnly;

    public override string ToString()
        => $"{(IsBinary ? "binary" : "unary")} op {Name}";
}

public sealed class TraitDefinition
{
    public required string Name { get; init; }
    public List<TypeParameter> TypeParams { get; } = new();
    public List<OperatorDefinition> Members { get; } = new();
    public bool IsBuiltin { get; init; }

    public Dictionary<string, AoiType> ParameterScope()
    {
        var scope = new Dictionary<string, AoiType>();
        foreach (var p in TypeParams) scope[p.Name] = p.Variable;
        return scope;
    }

    public IEnumerable<OperatorDefinition> FindMembers(string name, bool binary)
        => Members.Where(m => m.Name == name && m.IsBinary == binary);
}

public sealed class ImplDefinition
{
    public string? Name { get; init; }
    public string? TraitName { get; init; }
    public List<TraitArgSyntax> TraitArgSyntax { get; } = new();
    public List<TypeParameter> TypeParams { get; } = new();
    public List<OperatorDefinition> Members { get; } = new();
    public string? AliasOf { get; init; }
    public TraitDefinition? Trait { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }

    public bool IsTraitImpl => TraitName != null;
    public bool IsDefault => IsTraitImpl && Name == null;

    public Dictionary<string, AoiType> ParameterScope()
    {
        var scope = new Dictionary<string, AoiType>();
        foreach (var p in TypeParams) scope[p.Name] = p.Variable;
        return scope;
    }

    public override string ToString()
        => Name ?? (TraitName ?? "<impl>");
}

/// <summary>
/// The declaration table. It is populated before execution, so forward
/// references between types, traits and impls work as in the language docs.
/// </summary>
public sealed class SemanticModel
{
    private static readonly string[] BuiltinTraitNames =
    {
        "Add", "Sub", "Mul", "Div", "Rem", "Neg",
        "Eq", "PartialOrd", "Ord", "ToStr", "Iter",
    };

    public Dictionary<string, TypeDefinition> Types { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, TypeAliasDefinition> Aliases { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, TraitDefinition> Traits { get; } = new(StringComparer.Ordinal);
    public List<ImplDefinition> Impls { get; } = new();
    public List<OperatorDefinition> Operators { get; } = new();
    public List<Stmt> Declarations { get; } = new();
    public TypeResolver Resolver { get; }
    public TraitEngine TraitEngine { get; }

    public SemanticModel()
    {
        Resolver = new TypeResolver(this);
        TraitEngine = new TraitEngine(this);
        RegisterBuiltinTraits();
    }

    private void RegisterBuiltinTraits()
    {
        foreach (var name in BuiltinTraitNames)
            Traits[name] = CreateBuiltinTrait(name);
    }

    private static TraitDefinition CreateBuiltinTrait(string name)
    {
        var trait = new TraitDefinition { Name = name, IsBuiltin = true };
        trait.TypeParams.Add(new TypeParameter { Name = "T" });
        if (name == "Iter")
            trait.TypeParams.Add(new TypeParameter { Name = "TVal" });

        TypeRef T() => new NamedTypeRef { Name = "T" };
        TypeRef Bool() => new NamedTypeRef { Name = "bool" };

        void Member(string opName, bool binary, TypeRef? lhs, TypeRef rhs, TypeRef result)
        {
            var member = new OperatorDefinition
            {
                Name = opName,
                IsBinary = binary,
                Kind = OperatorKind.Normal,
                LhsType = lhs,
                RhsType = rhs,
                ResultType = result,
                Body = new UnitExpr(),
                IsDeclarationOnly = true,
                Trait = trait,
            };
            trait.Members.Add(member);
        }

        switch (name)
        {
            case "Add": Member("+", true, T(), T(), T()); break;
            case "Sub": Member("-", true, T(), T(), T()); break;
            case "Mul": Member("*", true, T(), T(), T()); break;
            case "Div": Member("/", true, T(), T(), T()); break;
            case "Rem": Member("%", true, T(), T(), T()); break;
            case "Neg": Member("-", false, null, T(), T()); break;
            case "Eq":
                Member("==", true, T(), T(), Bool());
                Member("!=", true, T(), T(), Bool());
                break;
            case "PartialOrd":
            case "Ord":
                Member("<", true, T(), T(), Bool());
                Member("<=", true, T(), T(), Bool());
                Member(">", true, T(), T(), Bool());
                Member(">=", true, T(), T(), Bool());
                break;
            case "ToStr": Member("toStr", false, null, T(), new NamedTypeRef { Name = "str" }); break;
            case "Iter":
                Member("iterate", false, null, T(),
                    new NamedTypeRef { Name = "TVal", Nullable = true });
                break;
        }
        return trait;
    }

    public TraitDefinition? GetTrait(string name)
        => Traits.TryGetValue(name, out var trait) ? trait : null;

    public TypeDefinition? GetType(string name)
        => Types.TryGetValue(name, out var type) ? type : null;

    public void AddProgram(ProgramNode program)
    {
        // Shell pass: make names visible to one another.
        foreach (var stmt in program.Statements)
        {
            switch (stmt)
            {
                case StructDecl sd:
                    if (!Types.ContainsKey(sd.Name))
                        Types[sd.Name] = new TypeDefinition
                        {
                            Name = sd.Name,
                            Kind = sd.IsClass ? TypeKind.Class : TypeKind.Struct,
                        };
                    break;
                case UnionDecl ud:
                    if (!Types.ContainsKey(ud.Name))
                        Types[ud.Name] = new TypeDefinition { Name = ud.Name, Kind = TypeKind.Union };
                    break;
                case EnumDecl ed:
                    if (!Types.ContainsKey(ed.Name))
                        Types[ed.Name] = new TypeDefinition { Name = ed.Name, Kind = TypeKind.Enum };
                    break;
                case TraitDecl td:
                    if (!Traits.ContainsKey(td.Name))
                        Traits[td.Name] = new TraitDefinition { Name = td.Name };
                    break;
                case TypeAliasDecl ad:
                    if (!Aliases.ContainsKey(ad.Name))
                        Aliases[ad.Name] = new TypeAliasDefinition { Name = ad.Name, Definition = ad.Definition };
                    break;
            }
        }

        // Populate declarations in source order.
        foreach (var stmt in program.Statements)
        {
            Declarations.Add(stmt);
            switch (stmt)
            {
                case StructDecl sd: PopulateStruct(sd); break;
                case UnionDecl ud: PopulateUnion(ud); break;
                case EnumDecl ed: PopulateEnum(ed); break;
                case TraitDecl td: PopulateTrait(td); break;
                case ImplDecl id: PopulateImpl(id); break;
                case TypeAliasDecl ad: PopulateAlias(ad); break;
                case OpDecl op: Operators.Add(FromOp(op, null, null, OperatorKind.Normal)); break;
                case ConvertDecl c: Operators.Add(FromConvert(c)); break;
                case GetDecl g: Operators.Add(FromGet(g)); break;
                case SetDecl s: Operators.Add(FromSet(s)); break;
                case LiteralSuffixDecl l: Operators.Add(FromLiteral(l)); break;
            }
        }

        ValidateDeclarations();
        ValidateCoherence();
    }

    private static List<TypeParameter> BuildTypeParameters(IEnumerable<TypeParameterSyntax> syntax)
    {
        var list = new List<TypeParameter>();
        foreach (var p in syntax)
        {
            var parameter = new TypeParameter { Name = p.Name };
            parameter.RequirementSyntax.AddRange(p.Requirements);
            list.Add(parameter);
        }
        return list;
    }

    private void PopulateStruct(StructDecl decl)
    {
        var type = Types[decl.Name];
        if (type.TypeParams.Count > 0 || type.Fields.Count > 0) return;
        type.TypeParams.AddRange(BuildTypeParameters(decl.TypeParams));
        type.Fields.AddRange(decl.Fields);
    }

    private void PopulateUnion(UnionDecl decl)
    {
        var type = Types[decl.Name];
        if (type.TypeParams.Count > 0 || type.UnionCases.Count > 0) return;
        type.TypeParams.AddRange(BuildTypeParameters(decl.TypeParams));
        type.UnionCases.AddRange(decl.Cases);
    }

    private void PopulateEnum(EnumDecl decl)
    {
        var type = Types[decl.Name];
        if (type.EnumMembers.Count > 0) return;
        long next = 0;
        foreach (var (name, value) in decl.Members)
        {
            if (value.HasValue) next = value.Value;
            type.EnumMembers[name] = next++;
        }
    }

    private void PopulateTrait(TraitDecl decl)
    {
        var trait = Traits[decl.Name];
        if (trait.TypeParams.Count > 0 || trait.Members.Count > 0) return;
        trait.TypeParams.AddRange(BuildTypeParameters(decl.TypeParams));
        foreach (var member in decl.Members)
            trait.Members.Add(FromOp(member, trait, null, OperatorKind.Normal));
    }

    private void PopulateImpl(ImplDecl decl)
    {
        var impl = new ImplDefinition
        {
            Name = decl.Name,
            TraitName = decl.TraitName,
            AliasOf = decl.AliasOf,
            Trait = decl.TraitName != null ? GetTrait(decl.TraitName) : null,
            Line = decl.Line,
            Column = decl.Column,
        };
        impl.TypeParams.AddRange(BuildTypeParameters(decl.TypeParams));
        impl.TraitArgSyntax.AddRange(decl.TraitArgs);

        foreach (var member in decl.Members)
            impl.Members.Add(FromOp(member, impl.Trait, impl, OperatorKind.Normal));

        Impls.Add(impl);
    }

    private void PopulateAlias(TypeAliasDecl decl)
    {
        var alias = Aliases[decl.Name];
        if (alias.TypeParams.Count > 0) return;
        alias.TypeParams.AddRange(BuildTypeParameters(decl.TypeParams));
        // Definition is already set in the shell pass.
    }

    private static OperatorDefinition FromOp(OpDecl op, TraitDefinition? trait, ImplDefinition? impl, OperatorKind kind)
    {
        var def = new OperatorDefinition
        {
            Name = op.Name,
            IsBinary = op.IsBinary,
            Kind = kind,
            Lhs = op.Lhs,
            Rhs = op.Rhs,
            Body = op.Body,
            IsDeclarationOnly = op.IsDeclarationOnly,
            Qualifier = op.Qualifier,
            Trait = trait,
            Impl = impl,
            Line = op.Line,
            Column = op.Column,
            LhsType = op.LhsType,
            RhsType = op.RhsType,
            ResultType = op.ResultType,
        };
        def.TypeParams.AddRange(BuildTypeParameters(op.TypeParams));
        return def;
    }

    private static OperatorDefinition FromConvert(ConvertDecl c)
    {
        var def = new OperatorDefinition
        {
            Name = "convert",
            IsBinary = false,
            Kind = OperatorKind.Convert,
            Rhs = c.Pattern,
            RhsType = c.FromType,
            ResultType = c.ToType,
            Body = c.Body,
            IsImplicit = c.Implicit,
            Line = c.Line,
            Column = c.Column,
        };
        def.TypeParams.AddRange(BuildTypeParameters(c.TypeParams));
        return def;
    }

    private static OperatorDefinition FromGet(GetDecl g)
    {
        var def = new OperatorDefinition
        {
            Name = g.PropertyName ?? "[]",
            IsBinary = g.IsIndexer,
            Kind = g.IsIndexer ? OperatorKind.IndexGetter : OperatorKind.Getter,
            Rhs = g.Self,
            Index = g.Index,
            RhsType = g.SelfType,
            IndexType = g.IndexType,
            ResultType = g.ResultType,
            Body = g.Body,
            Line = g.Line,
            Column = g.Column,
        };
        def.TypeParams.AddRange(BuildTypeParameters(g.TypeParams));
        return def;
    }

    private static OperatorDefinition FromSet(SetDecl s)
    {
        var def = new OperatorDefinition
        {
            Name = s.PropertyName ?? "[]",
            IsBinary = true,
            Kind = s.IsIndexer ? OperatorKind.IndexSetter : OperatorKind.Setter,
            Lhs = s.Self,
            Rhs = s.Value,
            Index = s.Index,
            LhsType = s.SelfType,
            RhsType = s.ValueType,
            IndexType = s.IndexType,
            ResultType = s.ValueType,
            Body = s.Body,
            Line = s.Line,
            Column = s.Column,
        };
        def.TypeParams.AddRange(BuildTypeParameters(s.TypeParams));
        return def;
    }

    private static OperatorDefinition FromLiteral(LiteralSuffixDecl l)
    {
        var def = new OperatorDefinition
        {
            Name = l.Suffix,
            IsBinary = false,
            Kind = OperatorKind.Literal,
            Rhs = l.Pattern,
            RhsType = l.ValueType,
            ResultType = l.ResultType,
            Body = l.Body,
            Suffix = l.Suffix,
            Line = l.Line,
            Column = l.Column,
        };
        def.TypeParams.AddRange(BuildTypeParameters(l.TypeParams));
        return def;
    }

    /// <summary>
    /// Static declaration checks shared by the CLI and playground. These are
    /// deliberately independent of the evaluator: they validate names, arity
    /// and signature types before any code runs. Full expression inference is
    /// performed by the resolver at use sites.
    /// </summary>
    public void ValidateDeclarations()
    {
        foreach (var type in Types.Values)
        {
            var scope = type.ParameterScope();
            foreach (var parameter in type.TypeParams)
                ValidateRequirements(parameter, scope);
            foreach (var (name, fieldType) in type.Fields)
            {
                try { Resolver.Resolve(fieldType, scope); }
                catch (AoiException ex)
                {
                    throw new AoiException(
                        $"Invalid field '{type.Name}.{name}': {ex.Message}", fieldType.Line, fieldType.Column);
                }
            }
            foreach (var (name, caseType) in type.UnionCases)
            {
                try { Resolver.Resolve(caseType, scope); }
                catch (AoiException ex)
                {
                    throw new AoiException(
                        $"Invalid union case '{type.Name}::{name}': {ex.Message}", caseType.Line, caseType.Column);
                }
            }
        }

        foreach (var alias in Aliases.Values)
        {
            var scope = alias.ParameterScope();
            try { Resolver.Resolve(alias.Definition, scope); }
            catch (AoiException ex)
            {
                throw new AoiException(
                    $"Invalid type alias '{alias.Name}': {ex.Message}", alias.Definition.Line, alias.Definition.Column);
            }
        }

        foreach (var trait in Traits.Values)
        {
            var scope = trait.ParameterScope();
            foreach (var parameter in trait.TypeParams)
                ValidateRequirements(parameter, scope);
            foreach (var member in trait.Members)
                ValidateSignature(member, scope);
        }

        foreach (var impl in Impls)
        {
            if (impl.TraitName != null && GetTrait(impl.TraitName) == null)
                throw new AoiException($"Unknown trait '{impl.TraitName}'", impl.Line, impl.Column);
            if (impl.TraitName != null && impl.Trait != null
                && impl.Trait.TypeParams.Count != impl.TraitArgSyntax.Count)
                throw new AoiException(
                    $"Trait '{impl.TraitName}' expects {impl.Trait.TypeParams.Count} argument(s), got {impl.TraitArgSyntax.Count}",
                    impl.Line, impl.Column);

            if (impl.AliasOf != null && !Impls.Any(i => i.Name == impl.AliasOf))
                throw new AoiException(
                    $"Implementation alias refers to unknown '{impl.AliasOf}'", impl.Line, impl.Column);

            var scope = impl.ParameterScope();
            if (impl.Trait != null)
            {
                for (int i = 0; i < impl.Trait.TypeParams.Count && i < impl.TraitArgSyntax.Count; i++)
                    scope[impl.Trait.TypeParams[i].Name] =
                        Resolver.Resolve(impl.TraitArgSyntax[i].Type, impl.ParameterScope());
            }
            foreach (var parameter in impl.TypeParams)
                ValidateRequirements(parameter, scope);

            foreach (var member in impl.Members)
            {
                if (impl.Trait != null
                    && !impl.Trait.FindMembers(member.Name, member.IsBinary).Any())
                    throw new AoiException(
                        $"Trait '{impl.Trait.Name}' has no operator '{member.Name}' with this arity",
                        member.Line, member.Column);
                ValidateSignature(member, scope);
            }
        }
    }

    private void ValidateRequirements(TypeParameter parameter, Dictionary<string, AoiType> scope)
    {
        foreach (var requirement in parameter.RequirementSyntax)
        {
            if (GetTrait(requirement.TraitName) == null && GetType(requirement.TraitName) == null)
                throw new AoiException(
                    $"Unknown trait or type '{requirement.TraitName}' in generic requirement");
            foreach (var argument in requirement.Args)
            {
                try { Resolver.Resolve(argument, scope); }
                catch (AoiException ex)
                {
                    throw new AoiException(
                        $"Invalid requirement {requirement.TraitName}: {ex.Message}", argument.Line, argument.Column);
                }
            }
        }
    }

    private void ValidateSignature(OperatorDefinition op, Dictionary<string, AoiType> scope)
    {
        foreach (var type in new[] { op.LhsType, op.RhsType, op.IndexType, op.ResultType })
        {
            if (type == null) continue;
            try { Resolver.Resolve(type, scope); }
            catch (AoiException ex)
            {
                throw new AoiException(
                    $"Invalid signature for operator '{op.Name}': {ex.Message}", op.Line, op.Column);
            }
        }
    }

    /// <summary>
    /// Enforces the "one default impl per trait argument tuple" rule from
    /// docs 4.5. Named impls may overlap freely.
    /// </summary>
    public void ValidateCoherence()
    {
        var defaults = Impls.Where(i => i.IsTraitImpl && i.IsDefault).ToList();
        for (int i = 0; i < defaults.Count; i++)
        {
            for (int j = i + 1; j < defaults.Count; j++)
            {
                var a = defaults[i];
                var b = defaults[j];
                if (a.TraitName != b.TraitName || a.TraitArgSyntax.Count != b.TraitArgSyntax.Count)
                    continue;
                var subst = new TypeSubstitution();
                bool overlaps = true;
                for (int k = 0; k < a.TraitArgSyntax.Count; k++)
                {
                    var ta = Resolver.Resolve(a.TraitArgSyntax[k].Type, a.ParameterScope());
                    var tb = Resolver.Resolve(b.TraitArgSyntax[k].Type, b.ParameterScope());
                    if (!TypeUnifier.TryUnify(ta, tb, subst)) { overlaps = false; break; }
                }
                if (overlaps)
                    throw new AoiException(
                        $"Conflicting default implementations for trait '{a.TraitName}'", b.Line, b.Column);
            }
        }
    }
}
