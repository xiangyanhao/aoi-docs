using Aoi.Interpreter.Ast;

namespace Aoi.Interpreter.Semantics;

public sealed class TypeResolver
{
    private readonly SemanticModel _model;

    public TypeResolver(SemanticModel model) => _model = model;

    private static readonly IReadOnlyDictionary<string, AoiType> EmptyScope
        = new Dictionary<string, AoiType>();

    public AoiType Resolve(TypeRef type, IReadOnlyDictionary<string, AoiType>? scope = null)
    {
        scope ??= EmptyScope;

        switch (type)
        {
            case NamedTypeRef named:
            {
                var args = named.Args.Select(a => Resolve(a, scope)).ToList();
                AoiType result;

                if (named.Name == "()" && args.Count == 0)
                    result = UnitType.Instance;
                else if (named.Name == "_" || named.Name == "any")
                    result = UnknownType.Instance;
                else if (PrimitiveType.ByName(named.Name) is { } primitive)
                    result = primitive;
                else if (scope.TryGetValue(named.Name, out var variable))
                {
                    if (args.Count > 0)
                        throw new AoiException($"Type parameter '{named.Name}' cannot take type arguments", named.Line, named.Column);
                    result = variable;
                }
                else if (_model.Aliases.TryGetValue(named.Name, out var alias))
                {
                    result = ResolveAlias(alias, args, named);
                }
                else
                {
                    _model.Types.TryGetValue(named.Name, out var definition);
                    if (definition != null && definition.TypeParams.Count != args.Count)
                        throw new AoiException(
                            $"Type '{definition.Name}' expects {definition.TypeParams.Count} argument(s), got {args.Count}",
                            named.Line, named.Column);
                    result = new NamedType(named.Name, args, definition);
                }

                return named.Nullable ? new NullableType(result) : result;
            }
            case TupleTypeRef tuple:
                if (tuple.Elements.Count == 0) return UnitType.Instance;
                return new TupleType(tuple.Elements.Select(e => Resolve(e, scope)));
            case OpTypeRef op:
                return new OpType(
                    op.Lhs != null ? Resolve(op.Lhs, scope) : null,
                    Resolve(op.Rhs, scope),
                    Resolve(op.Result, scope));
            case DynTypeRef dyn:
            {
                var innerScope = new Dictionary<string, AoiType>(scope);
                var variables = new List<(string Name, TypeVar Variable)>();
                foreach (var parameter in dyn.TypeParams)
                {
                    var variable = TypeVar.Fresh(parameter.Name);
                    variables.Add((parameter.Name, variable));
                    innerScope[parameter.Name] = variable;
                }

                var requirements = new List<TraitRequirement>();
                foreach (var req in dyn.Requirements)
                {
                    requirements.Add(new TraitRequirement(
                        req.TraitName,
                        req.Args.Select(a => Resolve(a, innerScope))));
                }

                // Also resolve requirements written on the parameters (parser
                // mirrors them into Requirements, so deduplicate by value).
                foreach (var parameter in dyn.TypeParams)
                {
                    foreach (var req in parameter.Requirements)
                    {
                        var requirement = new TraitRequirement(
                            req.TraitName,
                            req.Args.Select(a => Resolve(a, innerScope)));
                        if (!requirements.Contains(requirement)) requirements.Add(requirement);
                    }
                }

                var self = variables.Count > 0 ? variables[0].Variable : TypeVar.Fresh("T");
                var inner = Resolve(dyn.Inner, innerScope);
                return new DynType(self, requirements, inner);
            }
            default:
                throw new AoiException($"Unsupported type reference {type.GetType().Name}", type.Line, type.Column);
        }
    }

    private AoiType ResolveAlias(TypeAliasDefinition alias, List<AoiType> args, NamedTypeRef site)
    {
        if (args.Count != alias.TypeParams.Count)
        {
            if (args.Count == 0)
            {
                args = alias.TypeParams.Select(p => (AoiType)p.Variable).ToList();
            }
            else
            {
                throw new AoiException(
                    $"Type alias '{alias.Name}' expects {alias.TypeParams.Count} argument(s), got {args.Count}",
                    site.Line, site.Column);
            }
        }

        var scope = new Dictionary<string, AoiType>();
        for (int i = 0; i < alias.TypeParams.Count; i++)
            scope[alias.TypeParams[i].Name] = args[i];
        return Resolve(alias.Definition, scope);
    }

    /// <summary>Instantiates a declaration with fresh type variables.</summary>
    public (NamedType Type, TypeSubstitution Substitution, Dictionary<string, AoiType> Scope)
        Instantiate(TypeDefinition definition)
    {
        var substitution = new TypeSubstitution();
        var args = new List<AoiType>();
        var scope = new Dictionary<string, AoiType>();
        foreach (var parameter in definition.TypeParams)
        {
            var variable = TypeVar.Fresh(parameter.Name);
            substitution.Bind(parameter.Variable, variable);
            args.Add(variable);
            scope[parameter.Name] = variable;
        }
        return (new NamedType(definition.Name, args, definition), substitution, scope);
    }

    public (NamedType Type, Dictionary<string, AoiType> Scope) InstantiateWithArguments(
        TypeDefinition definition, IReadOnlyList<AoiType> args)
    {
        if (definition.TypeParams.Count != args.Count)
            throw new AoiException(
                $"Type '{definition.Name}' expects {definition.TypeParams.Count} argument(s), got {args.Count}");
        var scope = new Dictionary<string, AoiType>();
        for (int i = 0; i < args.Count; i++)
            scope[definition.TypeParams[i].Name] = args[i];
        return (new NamedType(definition.Name, args, definition), scope);
    }

    public AoiType FieldType(TypeDefinition definition, string field, IReadOnlyList<AoiType> args)
    {
        var fieldDecl = definition.Fields.FirstOrDefault(f => f.Name == field);
        if (fieldDecl.Type == null)
            throw new AoiException($"Type '{definition.Name}' has no field '{field}'");
        var (_, scope) = InstantiateWithArguments(definition, args);
        return Resolve(fieldDecl.Type, scope);
    }

    public AoiType UnionCaseType(TypeDefinition definition, string caseName, IReadOnlyList<AoiType> args)
    {
        var caseDecl = definition.UnionCases.FirstOrDefault(c => c.Name == caseName);
        if (caseDecl.Type == null)
            throw new AoiException($"Union '{definition.Name}' has no case '{caseName}'");
        var (_, scope) = InstantiateWithArguments(definition, args);
        return Resolve(caseDecl.Type, scope);
    }
}
