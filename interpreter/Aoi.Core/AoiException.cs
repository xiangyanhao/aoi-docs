namespace Aoi.Interpreter;

public class AoiException : Exception
{
    public int Line { get; }
    public int Column { get; }

    public AoiException(string message, int line = 0, int column = 0)
        : base(line > 0 ? $"{message} (at {line}:{column})" : message)
    {
        Line = line;
        Column = column;
    }
}

public sealed class AoiReturnException : Exception
{
    public Runtime.AoiValue Value { get; }
    public AoiReturnException(Runtime.AoiValue value) => Value = value;
}

public sealed class AoiBreakException : Exception { }
public sealed class AoiContinueException : Exception { }

public sealed class AoiThrowException : Exception
{
    public Runtime.AoiValue Value { get; }
    public AoiThrowException(Runtime.AoiValue value) : base(value.ToString())
        => Value = value;
}
