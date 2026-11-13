using System.Collections.Immutable;
using Aoi.Interpreter.Semantics;

namespace Aoi.Interpreter.Runtime;

public abstract class AoiValue
{
    public abstract AoiType Type { get; }
    public virtual string TypeName => Type.ToString();
    public virtual AoiValue Copy() => this;
    public abstract override string ToString();
}

public sealed class UnitValue : AoiValue
{
    public static readonly UnitValue Instance = new();
    private UnitValue() { }
    public override AoiType Type => UnitType.Instance;
    public override string ToString() => "()";
}

public sealed class IntValue : AoiValue
{
    public long Value { get; }
    public IntValue(long value) => Value = value;
    public override AoiType Type => PrimitiveType.Int;
    public override string ToString() => Value.ToString();
}

public sealed class FloatValue : AoiValue
{
    public double Value { get; }
    public FloatValue(double value) => Value = value;
    public override AoiType Type => PrimitiveType.Float;
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class BoolValue : AoiValue
{
    public bool Value { get; }
    public BoolValue(bool value) => Value = value;
    public override AoiType Type => PrimitiveType.Bool;
    public override string ToString() => Value ? "true" : "false";
}

public sealed class CharValue : AoiValue
{
    public char Value { get; }
    public CharValue(char value) => Value = value;
    public override AoiType Type => PrimitiveType.Char;
    public override string ToString() => Value.ToString();
}

public sealed class StrValue : AoiValue
{
    public string Value { get; }
    public StrValue(string value) => Value = value;
    public override AoiType Type => PrimitiveType.Str;
    public override string ToString() => Value;
}

public sealed class TupleValue : AoiValue
{
    public List<AoiValue> Elements { get; }
    public TupleValue(List<AoiValue> elements) => Elements = elements;
    public override AoiType Type => new TupleType(Elements.Select(e => e.Type));
    public override AoiValue Copy() => new TupleValue(Elements.Select(e => e.Copy()).ToList());
    public override string ToString() => $"({string.Join(", ", Elements)})";
}

public sealed class ArrayValue : AoiValue
{
    public List<AoiValue> Elements { get; }
    public AoiType ElementType { get; }
    public ArrayValue(AoiType elementType, List<AoiValue> elements)
    {
        ElementType = elementType;
        Elements = elements;
    }
    public override AoiType Type => new NamedType("arr", new[] { ElementType });
    public override string ToString() => $"[{string.Join(", ", Elements)}]";
}

public sealed class SpanValue : AoiValue
{
    public ArrayValue Array { get; }
    public int Start { get; }
    public int Length { get; }
    public SpanValue(ArrayValue array, int start, int length)
    {
        Array = array;
        Start = start;
        Length = length;
    }
    public override AoiType Type => new NamedType("Span", new[] { Array.ElementType });
    public override string ToString() => $"Span[{Length}]";
}

public sealed class RangeValue : AoiValue
{
    public long From { get; }
    public long To { get; }
    public RangeValue(long from, long to) { From = from; To = to; }
    public override AoiType Type => new NamedType("Range");
    public override string ToString() => $"{From}..{To}";
    public IEnumerable<long> Iterate()
    {
        for (long i = From; i < To; i++) yield return i;
    }
}

public sealed class StructValue : AoiValue
{
    public NamedType StructType { get; }
    public Dictionary<string, AoiValue> Fields { get; }
    public bool IsClass { get; }
    public string StructName => StructType.Name;

    public StructValue(NamedType type, Dictionary<string, AoiValue> fields, bool isClass)
    {
        StructType = type;
        Fields = fields;
        IsClass = isClass;
    }
    public override AoiType Type => StructType;

    [ThreadStatic] private static HashSet<object>? _copySeen;
    public override AoiValue Copy()
    {
        if (IsClass) return this;
        _copySeen ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (!_copySeen.Add(this))
            return this;
        try
        {
            return new StructValue(StructType,
                Fields.ToDictionary(kv => kv.Key, kv => kv.Value.Copy()), false);
        }
        finally
        {
            _copySeen.Remove(this);
        }
    }

    [ThreadStatic] private static HashSet<object>? _toStringSeen;
    public override string ToString()
    {
        _toStringSeen ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (!_toStringSeen.Add(this))
            return $"{StructName} {{ ... }}";
        try
        {
            return $"{StructName} {{ {string.Join(", ", Fields.Select(kv => $"{kv.Key}: {kv.Value}"))} }}";
        }
        finally
        {
            _toStringSeen.Remove(this);
        }
    }
}

public sealed class EnumValue : AoiValue
{
    public NamedType EnumType { get; }
    public string MemberName { get; }
    public long Value { get; }
    public string EnumName => EnumType.Name;
    public EnumValue(NamedType type, string memberName, long value)
    {
        EnumType = type;
        MemberName = memberName;
        Value = value;
    }
    public override AoiType Type => EnumType;
    public override string ToString() => $"{EnumName}::{MemberName}";
}

public sealed class UnionValue : AoiValue
{
    public NamedType UnionType { get; }
    public string CaseName { get; }
    public AoiValue Payload { get; }
    public string UnionName => UnionType.Name;
    public UnionValue(NamedType type, string caseName, AoiValue payload)
    {
        UnionType = type;
        CaseName = caseName;
        Payload = payload;
    }
    public override AoiType Type => UnionType;
    public override AoiValue Copy() => new UnionValue(UnionType, CaseName, Payload.Copy());
    public override string ToString() => $"{UnionName}::{CaseName}({Payload})";
}

public sealed class NullableValue : AoiValue
{
    public AoiValue? Value { get; }
    public bool IsNone => Value == null;
    public AoiType InnerType { get; }
    public NullableValue(AoiValue? value, AoiType? innerType = null)
    {
        Value = value;
        InnerType = innerType ?? value?.Type ?? UnknownType.Instance;
    }
    public static NullableValue NoneFor(AoiType innerType) => new(null, innerType);
    public override AoiType Type => new NullableType(InnerType);
    public override AoiValue Copy() => new NullableValue(Value?.Copy(), InnerType);
    public override string ToString() => Value == null ? "none" : $"some({Value})";
}

public sealed class UnsetValue : AoiValue
{
    public static readonly UnsetValue Instance = new();
    private UnsetValue() { }
    public override AoiType Type => new NamedType("unset");
    public override string ToString() => "unset";
}

public sealed class DynValue : AoiValue
{
    public AoiValue Inner { get; }
    public DynType DynType { get; }
    public ImplContext Implementations { get; }

    public DynValue(AoiValue inner, DynType dynType, ImplContext implementations)
    {
        Inner = inner;
        DynType = dynType;
        Implementations = implementations;
    }

    // Runtime dispatch uses the concrete underlying type; DynType remains
    // available for conversions and member lookup.
    public override AoiType Type => Inner.Type;
    public override AoiValue Copy() => new DynValue(Inner.Copy(), DynType, Implementations);
    public override string ToString() => Inner.ToString();

    public static DynValue Wrap(AoiValue value, DynType dynType, TraitEngine engine, ImplContext context)
    {
        var implementations = new ImplContext(context);
        foreach (var requirement in dynType.Requirements)
        {
            var resolved = requirement.Substitute(new TypeSubstitution());
            // The dyn requirement contains the dyn self variable; replace it
            // with the concrete underlying type.
            var subst = new TypeSubstitution();
            subst.Bind(dynType.Self, value.Type);
            var concrete = resolved.Substitute(subst);
            implementations.Add(engine.FindImpl(concrete.TraitName, concrete.Args, null, subst, context));
        }
        return new DynValue(value, dynType, implementations);
    }
}

public sealed class OperatorValue : AoiValue
{
    public string Name { get; }
    public bool IsBinary { get; }
    public Func<Interpreter, AoiValue?, AoiValue, AoiValue> Invoke { get; }
    public AoiType? LhsType { get; }
    public AoiType? RhsType { get; }
    public AoiType? ResultType { get; }

    public OperatorValue(
        string name,
        bool isBinary,
        Func<Interpreter, AoiValue?, AoiValue, AoiValue> invoke,
        AoiType? lhsType = null,
        AoiType? rhsType = null,
        AoiType? resultType = null)
    {
        Name = name;
        IsBinary = isBinary;
        Invoke = invoke;
        LhsType = lhsType;
        RhsType = rhsType;
        ResultType = resultType;
    }

    public override AoiType Type => new OpType(LhsType, RhsType ?? UnknownType.Instance, ResultType ?? UnknownType.Instance);
    public override string ToString() => $"<op {Name}>";
}

public sealed class TypeValue : AoiValue
{
    public AoiType Value { get; }
    public TypeValue(AoiType value) => Value = value;
    public override AoiType Type => new NamedType("Type");
    public override string ToString() => Value.ToString();
}

public sealed class ExceptionValue : AoiValue
{
    public string ExceptionName { get; }
    public string Message { get; }
    public ExceptionValue(string name, string message)
    {
        ExceptionName = name;
        Message = message;
    }
    public override AoiType Type => new NamedType(ExceptionName);
    public override string ToString() => $"{ExceptionName}: {Message}";
}

/// <summary>
/// Lexical environment plus the runtime type and implementation dictionaries
/// of the current generic invocation.
/// </summary>
public sealed class Environment
{
    private readonly Dictionary<string, AoiValue> _values = new();
    private readonly Dictionary<int, AoiType> _typeBindings = new();
    private readonly Dictionary<string, AoiType> _namedTypeBindings = new(StringComparer.Ordinal);
    private readonly Environment? _parent;

    public ImplContext Impls { get; }

    public Environment(Environment? parent = null)
    {
        _parent = parent;
        Impls = new ImplContext(parent?.Impls);
    }

    public Environment CreateChild() => new(this);

    public void Define(string name, AoiValue value) => _values[name] = value;

    public bool Assign(string name, AoiValue value)
    {
        if (_values.ContainsKey(name))
        {
            _values[name] = value;
            return true;
        }
        return _parent?.Assign(name, value) ?? false;
    }

    public AoiValue Get(string name)
    {
        if (_values.TryGetValue(name, out var value)) return value;
        if (_parent != null) return _parent.Get(name);
        throw new AoiException($"Undefined variable '{name}'");
    }

    public bool TryGet(string name, out AoiValue? value)
    {
        if (_values.TryGetValue(name, out value)) return true;
        if (_parent != null) return _parent.TryGet(name, out value);
        value = null;
        return false;
    }

    public void DefineType(TypeVar variable, AoiType type) => _typeBindings[variable.Id] = type;

    public void DefineTypeName(string name, AoiType type) => _namedTypeBindings[name] = type;

    public bool TryGetTypeName(string name, out AoiType type)
    {
        if (_namedTypeBindings.TryGetValue(name, out type!)) return true;
        if (_parent != null) return _parent.TryGetTypeName(name, out type);
        type = null!;
        return false;
    }

    public Dictionary<string, AoiType> TypeNameScope()
    {
        var scope = _parent?.TypeNameScope() ?? new Dictionary<string, AoiType>(StringComparer.Ordinal);
        foreach (var (name, type) in _namedTypeBindings) scope[name] = type;
        return scope;
    }

    public bool TryGetType(TypeVar variable, out AoiType type)
    {
        if (_typeBindings.TryGetValue(variable.Id, out type!)) return true;
        if (_parent != null) return _parent.TryGetType(variable, out type);
        type = null!;
        return false;
    }

    public void AddImpl(ImplBinding binding) => Impls.Add(binding);

    public void AddImpls(IEnumerable<ImplBinding> bindings) => Impls.AddRange(bindings);
}
