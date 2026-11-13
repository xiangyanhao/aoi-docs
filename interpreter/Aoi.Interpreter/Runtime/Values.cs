using Aoi.Interpreter.Ast;

namespace Aoi.Interpreter.Runtime;

public abstract class AoiValue
{
    public abstract string TypeName { get; }
    public virtual AoiValue Copy() => this;
    public abstract override string ToString();
}

public sealed class UnitValue : AoiValue
{
    public static readonly UnitValue Instance = new();
    public override string TypeName => "()";
    public override string ToString() => "()";
}

public sealed class IntValue : AoiValue
{
    public long Value { get; }
    public IntValue(long value) => Value = value;
    public override string TypeName => "int";
    public override string ToString() => Value.ToString();
}

public sealed class FloatValue : AoiValue
{
    public double Value { get; }
    public FloatValue(double value) => Value = value;
    public override string TypeName => "float";
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class BoolValue : AoiValue
{
    public bool Value { get; }
    public BoolValue(bool value) => Value = value;
    public override string TypeName => "bool";
    public override string ToString() => Value ? "true" : "false";
}

public sealed class CharValue : AoiValue
{
    public char Value { get; }
    public CharValue(char value) => Value = value;
    public override string TypeName => "char";
    public override string ToString() => Value.ToString();
}

public sealed class StrValue : AoiValue
{
    public string Value { get; }
    public StrValue(string value) => Value = value;
    public override string TypeName => "str";
    public override string ToString() => Value;
}

public sealed class TupleValue : AoiValue
{
    public List<AoiValue> Elements { get; }
    public TupleValue(List<AoiValue> elements) => Elements = elements;
    public override string TypeName => $"({string.Join(", ", Elements.Select(e => e.TypeName))})";
    public override AoiValue Copy() => new TupleValue(Elements.Select(e => e.Copy()).ToList());
    public override string ToString() => $"({string.Join(", ", Elements)})";
}

public sealed class ArrayValue : AoiValue
{
    public List<AoiValue> Elements { get; }
    public string ElementType { get; }
    public ArrayValue(string elementType, List<AoiValue> elements)
    {
        ElementType = elementType;
        Elements = elements;
    }
    public override string TypeName => $"arr[{ElementType}]";
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
    public override string TypeName => $"Span[{Array.ElementType}]";
    public override string ToString() => $"Span[{Length}]";
}

public sealed class RangeValue : AoiValue
{
    public long From { get; }
    public long To { get; }
    public RangeValue(long from, long to) { From = from; To = to; }
    public override string TypeName => "Range";
    public override string ToString() => $"{From}..{To}";
    public IEnumerable<long> Iterate()
    {
        for (long i = From; i < To; i++) yield return i;
    }
}

public sealed class StructValue : AoiValue
{
    public string StructName { get; }
    public Dictionary<string, AoiValue> Fields { get; }
    public bool IsClass { get; }
    public StructValue(string name, Dictionary<string, AoiValue> fields, bool isClass)
    {
        StructName = name;
        Fields = fields;
        IsClass = isClass;
    }
    public override string TypeName => StructName;
    public override AoiValue Copy()
    {
        if (IsClass) return this;
        return new StructValue(StructName,
            Fields.ToDictionary(kv => kv.Key, kv => kv.Value.Copy()), false);
    }
    public override string ToString()
        => $"{StructName} {{ {string.Join(", ", Fields.Select(kv => $"{kv.Key}: {kv.Value}"))} }}";
}

public sealed class EnumValue : AoiValue
{
    public string EnumName { get; }
    public string MemberName { get; }
    public long Value { get; }
    public EnumValue(string enumName, string memberName, long value)
    {
        EnumName = enumName;
        MemberName = memberName;
        Value = value;
    }
    public override string TypeName => EnumName;
    public override string ToString() => $"{EnumName}::{MemberName}";
}

public sealed class UnionValue : AoiValue
{
    public string UnionName { get; }
    public string CaseName { get; }
    public AoiValue Payload { get; }
    public UnionValue(string unionName, string caseName, AoiValue payload)
    {
        UnionName = unionName;
        CaseName = caseName;
        Payload = payload;
    }
    public override string TypeName => UnionName;
    public override AoiValue Copy() => new UnionValue(UnionName, CaseName, Payload.Copy());
    public override string ToString() => $"{UnionName}::{CaseName}({Payload})";
}

public sealed class NullableValue : AoiValue
{
    public AoiValue? Value { get; }
    public bool IsNone => Value == null;
    public NullableValue(AoiValue? value) => Value = value;
    public static NullableValue None { get; } = new(null);
    public override string TypeName => Value == null ? "?" : $"{Value.TypeName}?";
    public override AoiValue Copy() => new NullableValue(Value?.Copy());
    public override string ToString() => Value == null ? "none" : $"some({Value})";
}

public sealed class UnsetValue : AoiValue
{
    public static readonly UnsetValue Instance = new();
    public override string TypeName => "unset";
    public override string ToString() => "unset";
}

public sealed class OperatorValue : AoiValue
{
    public string Name { get; }
    public bool IsBinary { get; }
    public Func<Interpreter, AoiValue?, AoiValue, AoiValue> Invoke { get; }
    public OperatorValue(string name, bool isBinary, Func<Interpreter, AoiValue?, AoiValue, AoiValue> invoke)
    {
        Name = name;
        IsBinary = isBinary;
        Invoke = invoke;
    }
    public override string TypeName => IsBinary ? "op<>" : "op->";
    public override string ToString() => $"<op {Name}>";
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
    public override string TypeName => ExceptionName;
    public override string ToString() => $"{ExceptionName}: {Message}";
}

public sealed class Environment
{
    private readonly Dictionary<string, AoiValue> _values = new();
    private readonly Environment? _parent;

    public Environment(Environment? parent = null) => _parent = parent;

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
        if (_values.TryGetValue(name, out var v)) return v;
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
}

public sealed class TypeInfo
{
    public required string Name { get; init; }
    public bool IsClass { get; init; }
    public bool IsEnum { get; init; }
    public bool IsUnion { get; init; }
    public List<(string Name, TypeRef Type)> Fields { get; } = new();
    public Dictionary<string, long> EnumMembers { get; } = new();
    public List<(string Name, TypeRef Type)> UnionCases { get; } = new();
}

public sealed class UserOperator
{
    public required string Name { get; init; }
    public required bool IsBinary { get; init; }
    public DeclPattern? Lhs { get; init; }
    public required DeclPattern Rhs { get; init; }
    public required Expr Body { get; init; }
    public string? ImplName { get; init; }
}
