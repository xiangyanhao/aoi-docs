using System.Text;
using Aoi.Interpreter;
using Aoi.Interpreter.Lexer;
using Aoi.Interpreter.Parser;
using Aoi.Interpreter.Runtime;
using AoiRuntime = Aoi.Interpreter.Runtime.Interpreter;

namespace Aoi.Playground.Services;

public sealed record RunResult(string Output, string? Error, long ElapsedMs);

public static class AoiRunner
{
    /// <summary>Playground-oriented limits so runaway code cannot freeze the browser tab.</summary>
    public static InterpreterOptions PlaygroundOptions { get; } = new()
    {
        MaxCallDepth = 512,
        MaxSteps = 2_000_000,
    };

    public static RunResult Run(string source)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var buffer = new StringBuilder();
        using var writer = new StringWriter(buffer);
        try
        {
            var tokens = new Lexer(source).Tokenize();
            var program = new Parser(tokens).ParseProgram();
            new AoiRuntime(writer, PlaygroundOptions).Execute(program);
            sw.Stop();
            return new RunResult(buffer.ToString(), null, sw.ElapsedMilliseconds);
        }
        catch (AoiException ex)
        {
            sw.Stop();
            return new RunResult(buffer.ToString(), ex.Message, sw.ElapsedMilliseconds);
        }
        catch (AoiThrowException ex)
        {
            sw.Stop();
            return new RunResult(buffer.ToString(), $"Unhandled exception: {ex.Value}", sw.ElapsedMilliseconds);
        }
        catch (DivideByZeroException)
        {
            sw.Stop();
            return new RunResult(buffer.ToString(), "Division by zero.", sw.ElapsedMilliseconds);
        }
        catch (OverflowException ex)
        {
            sw.Stop();
            return new RunResult(buffer.ToString(), $"Numeric overflow: {ex.Message}", sw.ElapsedMilliseconds);
        }
        catch (OutOfMemoryException)
        {
            sw.Stop();
            return new RunResult(buffer.ToString(), "Out of memory while evaluating the program.", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // StackOverflowException usually cannot be caught on .NET; depth limits prevent it.
            return new RunResult(buffer.ToString(), $"Internal error: {ex.GetType().Name}: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }
}
