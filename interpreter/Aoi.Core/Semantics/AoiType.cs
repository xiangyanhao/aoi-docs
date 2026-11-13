using System.Collections.Immutable;
using System.Text;

namespace Aoi.Interpreter.Semantics;

/// <summary>
/// The type model shared by the declaration collector, the trait solver and
/// the runtime. Types are structural values: generic arguments are kept
/// explicitly instead of being erased.
/// </summary>
public abstract class AoiType
{
    public virtual bool IsConcrete => true;
    public abstract AoiType Substitute(TypeSubstitution substitution);
    public abstract override string ToString();
    public abstract override bool Equals(object? obj);
    public abstract override int GetHashCode();
}

public sealed class PrimitiveType : AoiType
{
    public static readonly PrimitiveType Int = new("int");
    public static readonly PrimitiveType Float = new("float");
    public static readonly PrimitiveType Bool = new("bool");
    public static readonly PrimitiveType Char = new("char");
    public static readonly PrimitiveType Str = new("str");

    public static readonly IReadOnlyList<PrimitiveType> All = new[] { Int, Float, Bool, Char, Str };

    public string Name { get; }
    private PrimitiveType(string name) => Name = name;

    public static PrimitiveType? ByName(string name) => name switch
    {
        "int" => Int,
        "float" => Float,
        "bool" => Bool,
        "char" => Char,
        "str" => Str,
        _ => null,
    };

    public override AoiType Substitute(TypeSubstitution substitution) => this;
    public override string ToString() => Name;
    public override bool Equals(object? obj) => obj is PrimitiveType p && p.Name == Name;
    public override int GetHashCode() => Name.GetHashCode();
}

public sealed class UnitType : AoiType
{
    public static readonly UnitType Instance = new();
    private UnitType() { }
    public override AoiType Substitute(TypeSubstitution substitution) => this;
    public override string ToString() => "()";
    public override bool Equals(object? obj) => obj is UnitType;
    public override int GetHashCode() => 0x51ed;
}

/// <summary>Bottom type of expressions that never return (throw / break).</summary>
public sealed class NeverType : AoiType
{
    public static readonly NeverType Instance = new();
    private NeverType() { }
    public override AoiType Substitute(TypeSubstitution substitution) => this;
    public override string ToString() => "!";
    public override bool Equals(object? obj) => obj is NeverType;
    public override int GetHashCode() => 0x4e57;
}

/// <summary>Unknown type, produced only by explicitly written <c>any</c> or <c>_</c>.</summary>
public sealed class UnknownType : AoiType
{
    public static readonly UnknownType Instance = new();
    private UnknownType() { }
    public override bool IsConcrete => false;
    public override AoiType Substitute(TypeSubstitution substitution) => this;
    public override string ToString() => "any";
    public override bool Equals(object? obj) => obj is UnknownType;
    public override int GetHashCode() => 0x0a11;
}

/// <summary>
/// A type variable. The same class is used for declared generic parameters and
/// for fresh inference variables; identity is the numeric id, not the name.
/// </summary>
public sealed class TypeVar : AoiType
{
    private static int _nextId;
    public int Id { get; }
    public string Name { get; }
    public bool IsInference { get; }

    public TypeVar(string name, bool inference = false)
    {
        Id = Interlocked.Increment(ref _nextId);
        Name = name;
        IsInference = inference;
    }

    public static TypeVar Fresh(string hint = "T") => new(hint, inference: true);

    public override bool IsConcrete => false;
    public override AoiType Substitute(TypeSubstitution substitution)
    {
        if (!substitution.TryGet(this, out var replacement))
            return this;
        if (replacement is TypeVar v && v.Id == Id)
            return this;
        return replacement.Substitute(substitution);
    }
    public override string ToString() => Name;
    public override bool Equals(object? obj) => obj is TypeVar v && v.Id == Id;
    public override int GetHashCode() => Id;
}

public sealed class NamedType : AoiType
{
    public string Name { get; }
    public ImmutableArray<AoiType> Args { get; }
    /// <summary>Linked declaration when the type comes from this program.</summary>
    public TypeDefinition? Definition { get; }

    public NamedType(string name, IEnumerable<AoiType>? args = null, TypeDefinition? definition = null)
    {
        Name = name;
        Args = (args ?? Array.Empty<AoiType>()).ToImmutableArray();
        Definition = definition;
    }

    public override bool IsConcrete => Args.All(a => a.IsConcrete);
    public override AoiType Substitute(TypeSubstitution substitution)
    {
        if (Args.Length == 0) return this;
        var args = new AoiType[Args.Length];
        bool changed = false;
        for (int i = 0; i < Args.Length; i++)
        {
            args[i] = Args[i].Substitute(substitution);
            changed |= !ReferenceEquals(args[i], Args[i]);
        }
        return changed ? new NamedType(Name, args, Definition) : this;
    }
    public override string ToString() => Args.Length == 0 ? Name : $"{Name}[{string.Join(", ", Args)}]";
    public override bool Equals(object? obj)
        => obj is NamedType n && n.Name == Name && n.Args.SequenceEqual(Args);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        foreach (var a in Args) hash.Add(a);
        return hash.ToHashCode();
    }
}

public sealed class TupleType : AoiType
{
    public ImmutableArray<AoiType> Elements { get; }
    public TupleType(IEnumerable<AoiType> elements) => Elements = elements.ToImmutableArray();
    public override bool IsConcrete => Elements.All(e => e.IsConcrete);
    public override AoiType Substitute(TypeSubstitution substitution)
        => new TupleType(Elements.Select(e => e.Substitute(substitution)));
    public override string ToString() => $"({string.Join(", ", Elements)})";
    public override bool Equals(object? obj) => obj is TupleType t && t.Elements.SequenceEqual(Elements);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var e in Elements) hash.Add(e);
        return hash.ToHashCode();
    }
}

public sealed class NullableType : AoiType
{
    public AoiType Element { get; }
    public NullableType(AoiType element) => Element = element;
    public override bool IsConcrete => Element.IsConcrete;
    public override AoiType Substitute(TypeSubstitution substitution)
        => new NullableType(Element.Substitute(substitution));
    public override string ToString() => $"{Element}?";
    public override bool Equals(object? obj) => obj is NullableType n && n.Element.Equals(Element);
    public override int GetHashCode() => HashCode.Combine(0x6e75, Element);
}

public sealed class OpType : AoiType
{
    public AoiType? Lhs { get; }
    public AoiType Rhs { get; }
    public AoiType Result { get; }
    public OpType(AoiType? lhs, AoiType rhs, AoiType result)
    {
        Lhs = lhs;
        Rhs = rhs;
        Result = result;
    }
    public override bool IsConcrete => (Lhs?.IsConcrete ?? true) && Rhs.IsConcrete && Result.IsConcrete;
    public override AoiType Substitute(TypeSubstitution substitution)
        => new OpType(Lhs?.Substitute(substitution), Rhs.Substitute(substitution), Result.Substitute(substitution));
    public override string ToString()
        => Lhs == null ? $"({Rhs} -> {Result})" : $"({Lhs} <> {Rhs} -> {Result})";
    public override bool Equals(object? obj)
        => obj is OpType o && Equals(o.Lhs, Lhs) && o.Rhs.Equals(Rhs) && o.Result.Equals(Result);
    public override int GetHashCode() => HashCode.Combine(Lhs, Rhs, Result);
}

public sealed class TraitRequirement : IEquatable<TraitRequirement>
{
    public string TraitName { get; }
    public ImmutableArray<AoiType> Args { get; }

    public TraitRequirement(string traitName, IEnumerable<AoiType> args)
    {
        TraitName = traitName;
        Args = args.ToImmutableArray();
    }

    public TraitRequirement Substitute(TypeSubstitution substitution)
        => new(TraitName, Args.Select(a => a.Substitute(substitution)));

    public TraitKey Key => TraitKey.From(TraitName, Args);

    public bool Equals(TraitRequirement? other)
        => other != null && other.TraitName == TraitName && other.Args.SequenceEqual(Args);
    public override bool Equals(object? obj) => Equals(obj as TraitRequirement);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(TraitName);
        foreach (var a in Args) hash.Add(a);
        return hash.ToHashCode();
    }
    public override string ToString()
        => Args.Length == 0 ? TraitName : $"{TraitName}[{string.Join(", ", Args)}]";
}

/// <summary>
/// Dynamic type <c>dyn[T: Trait[T]] T</c>: a value plus a table of trait
/// implementations. The self type variable is available in <see cref="Inner"/>
/// and <see cref="Requirements"/>.
/// </summary>
public sealed class DynType : AoiType
{
    public TypeVar Self { get; }
    public ImmutableArray<TraitRequirement> Requirements { get; }
    public AoiType Inner { get; }

    public DynType(TypeVar self, IEnumerable<TraitRequirement> requirements, AoiType inner)
    {
        Self = self;
        Requirements = requirements.ToImmutableArray();
        Inner = inner;
    }

    public override bool IsConcrete => false;
    public override AoiType Substitute(TypeSubstitution substitution)
    {
        var self = substitution.TryGet(Self, out var s) && s is TypeVar sv ? sv : Self;
        return new DynType(self, Requirements.Select(r => r.Substitute(substitution)), Inner.Substitute(substitution));
    }
    public override string ToString()
    {
        var reqs = string.Join(", ", Requirements.Select(r => r.ToString()));
        var body = Inner is TypeVar v && v.Id == Self.Id ? Self.Name : Inner.ToString();
        return $"dyn[{Self.Name}: {reqs}] {body}";
    }
    public override bool Equals(object? obj)
        => obj is DynType d && d.Requirements.SequenceEqual(Requirements) && d.Inner.Equals(Inner);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(0x64796e);
        foreach (var r in Requirements) hash.Add(r);
        hash.Add(Inner);
        return hash.ToHashCode();
    }
}

public sealed class TypeSubstitution
{
    private readonly Dictionary<int, AoiType> _bindings = new();

    public bool TryGet(TypeVar variable, out AoiType type)
        => _bindings.TryGetValue(variable.Id, out type!);

    public void Bind(TypeVar variable, AoiType type) => _bindings[variable.Id] = type;

    public IReadOnlyDictionary<int, AoiType> Bindings => _bindings;

    public TypeSubstitution Clone()
    {
        var clone = new TypeSubstitution();
        foreach (var (id, type) in _bindings)
            clone._bindings[id] = type;
        return clone;
    }

    public AoiType Apply(AoiType type) => type.Substitute(this);

    public bool IsFullyResolved(AoiType type) => type.IsConcrete;

    public override string ToString()
        => string.Join(", ", _bindings.Select(kv => $"{kv.Key}->{kv.Value}"));
}

public static class TypeUnifier
{
    /// <summary>
    /// Unifies a pattern type (typically a signature containing generic
    /// variables) with an actual type, extending <paramref name="substitution"/>.
    /// </summary>
    public static bool TryUnify(AoiType pattern, AoiType actual, TypeSubstitution substitution)
    {
        pattern = pattern.Substitute(substitution);
        actual = actual.Substitute(substitution);

        if (pattern is TypeVar pv)
            return Bind(pv, actual, substitution);
        if (actual is TypeVar av)
            return Bind(av, pattern, substitution);

        if (pattern is UnknownType || actual is UnknownType)
            return true;

        switch (pattern)
        {
            case UnitType:
                return actual is UnitType;
            case NeverType:
                return actual is NeverType;
            case PrimitiveType p:
                return actual is PrimitiveType a && p.Name == a.Name;
            case NamedType n:
                if (actual is not NamedType m || n.Name != m.Name || n.Args.Length != m.Args.Length)
                    return false;
                for (int i = 0; i < n.Args.Length; i++)
                    if (!TryUnify(n.Args[i], m.Args[i], substitution)) return false;
                return true;
            case TupleType t:
                if (actual is not TupleType u || t.Elements.Length != u.Elements.Length)
                    return false;
                for (int i = 0; i < t.Elements.Length; i++)
                    if (!TryUnify(t.Elements[i], u.Elements[i], substitution)) return false;
                return true;
            case NullableType nt:
                return actual is NullableType na && TryUnify(nt.Element, na.Element, substitution);
            case OpType o:
                return actual is OpType oa
                    && (o.Lhs == null ? oa.Lhs == null : oa.Lhs != null && TryUnify(o.Lhs, oa.Lhs, substitution))
                    && TryUnify(o.Rhs, oa.Rhs, substitution)
                    && TryUnify(o.Result, oa.Result, substitution);
            case DynType d:
                if (actual is not DynType da || d.Requirements.Length != da.Requirements.Length)
                    return false;
                for (int i = 0; i < d.Requirements.Length; i++)
                {
                    if (d.Requirements[i].TraitName != da.Requirements[i].TraitName
                        || d.Requirements[i].Args.Length != da.Requirements[i].Args.Length)
                        return false;
                    for (int j = 0; j < d.Requirements[i].Args.Length; j++)
                        if (!TryUnify(d.Requirements[i].Args[j], da.Requirements[i].Args[j], substitution))
                            return false;
                }
                return TryUnify(d.Inner, da.Inner, substitution);
            default:
                return pattern.Equals(actual);
        }
    }

    private static bool Bind(TypeVar variable, AoiType type, TypeSubstitution substitution)
    {
        if (type is TypeVar other && other.Id == variable.Id) return true;
        if (Occurs(variable, type, substitution)) return false;
        substitution.Bind(variable, type);
        return true;
    }

    private static bool Occurs(TypeVar variable, AoiType type, TypeSubstitution substitution)
    {
        type = type.Substitute(substitution);
        return type switch
        {
            TypeVar v => v.Id == variable.Id,
            NamedType n => n.Args.Any(a => Occurs(variable, a, substitution)),
            TupleType t => t.Elements.Any(e => Occurs(variable, e, substitution)),
            NullableType nt => Occurs(variable, nt.Element, substitution),
            OpType o => (o.Lhs != null && Occurs(variable, o.Lhs, substitution))
                         || Occurs(variable, o.Rhs, substitution)
                         || Occurs(variable, o.Result, substitution),
            DynType d => d.Requirements.Any(r => r.Args.Any(a => Occurs(variable, a, substitution)))
                         || Occurs(variable, d.Inner, substitution),
            _ => false,
        };
    }

    /// <summary>
    /// Checks whether two types can be made equal without mutating either.
    /// Used by coherence checking and overload comparison.
    /// </summary>
    public static bool CanUnify(AoiType a, AoiType b)
        => TryUnify(a, b, new TypeSubstitution());
}
