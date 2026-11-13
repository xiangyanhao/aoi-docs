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
    public static RunResult Run(string source)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var buffer = new StringBuilder();
        using var writer = new StringWriter(buffer);
        try
        {
            var tokens = new Lexer(source).Tokenize();
            var program = new Parser(tokens).ParseProgram();
            new AoiRuntime(writer).Execute(program);
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
        catch (Exception ex)
        {
            sw.Stop();
            return new RunResult(buffer.ToString(), $"Internal error: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }
}
