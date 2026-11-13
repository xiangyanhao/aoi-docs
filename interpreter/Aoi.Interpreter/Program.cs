using Aoi.Interpreter;
using Aoi.Interpreter.Lexer;
using Aoi.Interpreter.Parser;
using Aoi.Interpreter.Runtime;

if (args.Length == 0)
{
    PrintHelp();
    return 1;
}

if (args[0] is "-h" or "--help")
{
    PrintHelp();
    return 0;
}

if (args[0] is "-e" or "--eval")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: aoi -e \"<code>\"");
        return 1;
    }
    return RunSource(string.Join(" ", args.Skip(1)), "<eval>");
}

if (args[0] is "-i" or "--repl")
{
    return RunRepl();
}

var path = args[0];
if (!File.Exists(path))
{
    Console.Error.WriteLine($"File not found: {path}");
    return 1;
}

return RunSource(File.ReadAllText(path), path);

static int RunSource(string source, string name)
{
    try
    {
        var tokens = new Lexer(source).Tokenize();
        var program = new Parser(tokens).ParseProgram();
        new Interpreter().Execute(program);
        return 0;
    }
    catch (AoiException ex)
    {
        Console.Error.WriteLine($"Error in {name}: {ex.Message}");
        return 1;
    }
    catch (AoiThrowException ex)
    {
        Console.Error.WriteLine($"Unhandled exception: {ex.Value}");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Internal error in {name}: {ex.Message}");
        return 1;
    }
}

static int RunRepl()
{
    Console.WriteLine("Aoi REPL (empty line or :q to quit)");
    var interpreter = new Interpreter();
    var buffer = "";
    while (true)
    {
        Console.Write(buffer.Length == 0 ? "aoi> " : "...> ");
        var line = Console.ReadLine();
        if (line == null || line == ":q") break;
        if (line.Length == 0 && buffer.Length == 0) continue;
        buffer += line + "\n";
        if (!LooksComplete(buffer)) continue;
        try
        {
            var tokens = new Lexer(buffer).Tokenize();
            var program = new Parser(tokens).ParseProgram();
            interpreter.Execute(program);
        }
        catch (AoiException ex)
        {
            Console.Error.WriteLine(ex.Message);
        }
        catch (AoiThrowException ex)
        {
            Console.Error.WriteLine($"Unhandled exception: {ex.Value}");
        }
        buffer = "";
    }
    return 0;
}

static bool LooksComplete(string src)
{
    int braces = 0, parens = 0, brackets = 0;
    foreach (var c in src)
    {
        switch (c)
        {
            case '{': braces++; break;
            case '}': braces--; break;
            case '(': parens++; break;
            case ')': parens--; break;
            case '[': brackets++; break;
            case ']': brackets--; break;
        }
    }
    return braces <= 0 && parens <= 0 && brackets <= 0;
}

static void PrintHelp()
{
    Console.WriteLine("""
        Aoi interpreter — run Aoi programs on .NET

        Usage:
          aoi <file.aoi>       Run a source file
          aoi -e "<code>"      Evaluate a snippet
          aoi -i               Interactive REPL
          aoi -h               Show this help
        """);
}
