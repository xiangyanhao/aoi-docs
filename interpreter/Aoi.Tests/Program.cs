using System.Text;
using Aoi.Interpreter;
using Aoi.Interpreter.Lexer;
using Aoi.Interpreter.Parser;
using Aoi.Interpreter.Runtime;
using AoiRuntime = Aoi.Interpreter.Runtime.Interpreter;

namespace Aoi.Tests;

internal sealed record Case(string Name, string Source, string Expected, string? ErrorContains = null);

internal static class Program
{
    private static int Main()
    {
        var cases = BuildCases();
        int failed = 0;
        foreach (var test in cases)
        {
            var buffer = new StringBuilder();
            using var writer = new StringWriter(buffer);
            string? error = null;
            try
            {
                var tokens = new Lexer(test.Source).Tokenize();
                var program = new Parser(tokens).ParseProgram();
                new AoiRuntime(writer, new InterpreterOptions
                {
                    MaxSteps = 200_000,
                    MaxCallDepth = 48,
                    MaxExecutionMilliseconds = 1_500,
                }).Execute(program);
            }
            catch (AoiThrowException ex)
            {
                error = $"Unhandled exception: {ex.Value}";
            }
            catch (AoiException ex)
            {
                error = ex.Message;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
            }

            string actual = error ?? Normalize(buffer.ToString());
            bool ok;
            if (test.ErrorContains != null)
                ok = error?.Contains(test.ErrorContains, StringComparison.Ordinal) == true;
            else
                ok = error == null && actual == Normalize(test.Expected);

            if (ok)
            {
                Console.WriteLine($"PASS {test.Name}");
            }
            else
            {
                failed++;
                Console.WriteLine($"FAIL {test.Name}");
                if (test.ErrorContains != null)
                    Console.WriteLine($"  expected error containing: {test.ErrorContains}");
                else
                    Console.WriteLine($"  expected: {Escape(test.Expected)}");
                Console.WriteLine($"  actual:   {Escape(actual)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{cases.Count - failed}/{cases.Count} passed");
        return failed == 0 ? 0 : 1;
    }

    private static string Normalize(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');

    private static string Escape(string value)
        => value.Replace("\n", "\\n", StringComparison.Ordinal);

    private static List<Case> BuildCases()
    {
        return new List<Case>
        {
            new("hello", """
                printLine "Hello, World!";
                """, "Hello, World!"),

            new("generic_struct_inference", """
                struct Pair[A, B] {
                    first: A;
                    second: B;
                }
                let p = new Pair { first: 1, second: "hi" };
                printLine p.first;
                printLine p.second;
                """, "1\nhi"),

            new("generic_operator", """
                op id[T] x: T -> T { x }
                printLine id 5;
                printLine id[str] "x";
                """, "5\nx"),

            new("trait_add_and_builtin", """
                struct Vector {
                    x: float;
                    y: float;
                }
                impl of Add[Vector] {
                    op a + b {
                        new Vector { x: a.x + b.x, y: a.y + b.y }
                    }
                }
                op sum[T: Add[T]] array: arr[T] -> T {
                    let res = array.[0];
                    for i in 1..array.length do {
                        res += array.[i];
                    }
                    res
                }
                let vectors = new arr[Vector] {
                    new Vector { x: 1.0, y: 2.0 },
                    new Vector { x: 3.0, y: 4.0 }
                };
                printLine sum vectors;
                printLine sum (new arr[int] { 1, 2, 3 });
                """, "Vector { x: 4, y: 6 }\n6"),

            new("named_impl_and_default_method", """
                trait Ord2[T] {
                    op a < b: T <> T -> bool;
                    op a <= b: T <> T -> bool { a < b || a == b }
                    op a > b: T <> T -> bool { !(a <= b) }
                    op a >= b: T <> T -> bool { !(a < b) }
                }
                struct V {
                    x: float;
                    y: float;
                }
                impl ByX of Ord2[V] { op a < b { a.x < b.x } }
                impl ByY of Ord2[V] { op a < b { a.y < b.y } }
                op maxV[T: Ord2[T]] (a, b): (T, T) -> T {
                    if a >= b then a else b
                }
                let a = new V { x: 1.0, y: 9.0 };
                let b = new V { x: 5.0, y: 2.0 };
                printLine (maxV[V: ByX] (a, b)).x;
                printLine (maxV[V: ByY] (a, b)).x;
                printLine (a ByX::< b);
                printLine (a ByY::< b);
                """, "5\n1\ntrue\nfalse"),

            new("generic_impl", """
                struct Vector[T: Add[T], Sub[T]] {
                    x: T;
                    y: T;
                }
                impl[T: Vector[T]] of Add[Vector[T]] {
                    op a + b {
                        new Vector { x: a.x + b.x, y: a.y + b.y }
                    }
                }
                let v = new Vector[int] { x: 1, y: 2 };
                let w = new Vector[int] { x: 3, y: 4 };
                printLine (v + w).x;
                printLine (v + w).y;
                """, "4\n6"),

            new("impl_alias_default", """
                trait Ord2[T] {
                    op a < b: T <> T -> bool;
                    op a <= b: T <> T -> bool { a < b || a == b }
                    op a > b: T <> T -> bool { !(a <= b) }
                    op a >= b: T <> T -> bool { !(a < b) }
                }
                struct V { x: float; y: float; }
                impl ByX of Ord2[V] { op a < b { a.x < b.x } }
                impl ByY of Ord2[V] { op a < b { a.y < b.y } }
                impl of Ord2[V] = ByX;
                let a = new V { x: 1.0, y: 9.0 };
                let b = new V { x: 5.0, y: 2.0 };
                printLine (a < b);
                printLine (a Ord2[V]::< b);
                printLine (a ByY::< b);
                printLine (a Ord2[V]::> b);
                """, "true\ntrue\nfalse\nfalse"),

            new("dyn_array", """
                type DShow = dyn[T: ToStr[T]] T;
                op printAll array: arr[DShow] -> () {
                    for e in array do {
                        printLine toStr e;
                    }
                }
                let items = new arr[DShow] { 1, "hi", true };
                printAll items;
                """, "1\nhi\ntrue"),

            new("dyn_explicit_impl", """
                struct A { x: int; }
                trait Show[T] {
                    op show x: T -> str;
                }
                impl ShowA of Show[A] { op show x { "A" } }
                let a = new A { x: 1 };
                let d = a[ShowA];
                printLine (show d);
                """, "A"),

            new("generic_class_indexer", """
                class Dictionary[TKey, TValue] {
                    keyValuePairs: arr[(TKey, TValue)];
                }
                get[TKey, TValue] self.length: Dictionary[TKey, TValue] -> int {
                    self.keyValuePairs.length
                }
                get[TKey, TValue] self.[key]: Dictionary[TKey, TValue] <> TKey -> TValue {
                    for (k, v) in self.keyValuePairs do {
                        if k == key then {
                            return v;
                        }
                    }
                    throw new KeyNotFoundException {};
                }
                set[TKey, TValue] self.[key] = value: Dictionary[TKey, TValue] <> TKey <- TValue {
                    for i in 0..self.length do {
                        if self.keyValuePairs.[i].0 == key then {
                            self.keyValuePairs.[i].1 = value;
                            return self;
                        }
                    }
                    self
                }
                let d = new Dictionary[str, int] {
                    keyValuePairs: new arr[(str, int)] { ("a", 1), ("b", 2) }
                };
                printLine d.length;
                printLine d.["b"];
                d.["b"] = 3;
                printLine d.length;
                printLine d.["b"];
                """, "2\n2\n2\n3"),

            new("trait_generic_requirements", """
                trait VectorSpace[TNum, TVec: Add[TVec]] {
                    op a * u: TNum <> TVec -> TVec;
                }
                struct Vector { x: float; y: float; }
                impl of Add[Vector] {
                    op a + b { new Vector { x: a.x + b.x, y: a.y + b.y } }
                }
                impl of VectorSpace[float, Vector] {
                    op a * u: float <> Vector -> Vector {
                        new Vector { x: a * u.x, y: a * u.y }
                    }
                }
                let v = new Vector { x: 1.5, y: 2.5 };
                let u = 2.0 VectorSpace[float, Vector]::* v;
                printLine u.x;
                printLine u.y;
                printLine (2.0 * v).x;
                """, "3\n5\n3"),

            new("impl_only_argument", """
                trait Ord2[T] {
                    op a < b: T <> T -> bool;
                    op a >= b: T <> T -> bool { !(a < b) }
                }
                struct V { x: float; }
                impl ByX of Ord2[V] { op a < b { a.x < b.x } }
                impl of Ord2[V] = ByX;
                op maxV[T: Ord2[T]] (a, b): (T, T) -> T {
                    if a >= b then a else b
                }
                let a = new V { x: 1.0 };
                let b = new V { x: 9.0 };
                printLine (maxV[Ord2[V]] (a, b)).x;
                """, "9"),

            new("dyn_custom_vtable", """
                struct A { x: int; }
                trait Show[T] {
                    op show x: T -> str;
                }
                impl ShowA of Show[A] { op show x { `A({x.x})` } }
                impl of Show[A] = ShowA;
                type DShow = dyn[T: Show[T]] T;
                op showAll array: arr[DShow] -> () {
                    for e in array do {
                        printLine (show e);
                    }
                }
                let items = new arr[DShow] {
                    new A { x: 1 },
                    new A { x: 2 }
                };
                showAll items;
                """, "A(1)\nA(2)"),

            new("constraint_failure", """
                struct Box[T: Ord[T]] { v: T; }
                let b = new Box[int] { v: 1 };
                printLine b.v;
                let bad = new Box[bool] { v: true };
                printLine bad.v;
                """, "", "does not satisfy Ord[bool]"),

            new("factorial_ok", """
                op fac x: int -> int {
                    if x <= 1 then 1 else x * fac (x - 1)
                }
                printLine fac 10;
                """, "3628800"),

            new("factorial_overflow", """
                op fac x: int -> int {
                    if x <= 1 then 1 else x * fac (x - 1)
                }
                printLine fac 21;
                """, "", "Integer overflow"),

            new("factorial_deep_recursion", """
                op fac x: int -> int {
                    if x <= 1 then 1 else x * fac (x - 1)
                }
                printLine fac 10000;
                """, "", "Stack overflow"),

            new("factorial_huge_input", """
                op fac x: int -> int {
                    if x <= 1 then 1 else x * fac (x - 1)
                }
                printLine fac 1000000;
                """, "", "Stack overflow"),
        };
    }
}
