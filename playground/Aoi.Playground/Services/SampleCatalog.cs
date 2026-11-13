namespace Aoi.Playground.Services;

public sealed record Sample(string Id, string Title, string Blurb, string Source);

public static class SampleCatalog
{
    public static IReadOnlyList<Sample> All { get; } =
    [
        new(
            "hello",
            "Hello",
            "最短的 Aoi 程序。",
            """
            printLine "Hello, World!";
            """),
        new(
            "factorial",
            "Factorial",
            "一元运算符与递归。",
            """
            op fac x: int -> int {
                if x == 0 then {
                    1
                } else {
                    x * fac (x - 1)
                }
            }
            printLine fac 10;
            """),
        new(
            "control",
            "Control flow",
            "when / for / 可空模式匹配。",
            """
            let n = 5;
            when n {
            is > 0 then {
                printLine "positive";
            }
            is < 0 then {
                printLine "negative";
            }
            else {
                printLine "zero";
            }
            }

            for i in 0..3 do {
                printLine i;
            }

            let opt = some 42;
            if opt is some as x then {
                printLine x;
            }
            """),
        new(
            "vector",
            "Vector sum",
            "结构体、自定义加法与数组。",
            """
            struct Vector {
                x: float;
                y: float;
            }
            op a + b: Vector <> Vector -> Vector {
                new Vector {
                    x: a.x + b.x,
                    y: a.y + b.y
                }
            }
            op sum array: arr[Vector] -> Vector {
                let res = array.[0];
                for i in 1..array.length do {
                    res = res + array.[i];
                }
                res
            }
            let vectors = new arr[Vector] {
                new Vector { x: 1.0, y: 2.0 },
                new Vector { x: 3.0, y: 4.0 },
                new Vector { x: 5.0, y: 6.0 }
            };
            printLine sum vectors;
            """),
        new(
            "pipe",
            "Pipe & closure",
            "管道、闭包与柯里化。",
            """
            op array map fn: arr[int] <> (int -> int) -> arr[int] {
                for i in 0..array.length do {
                    array.[i] = fn array.[i];
                }
                array
            }

            let nums = new arr[int] { 1, 2, 3, 4, 5 };
            nums
                |> map ((+) 10)
                |> map (a => a * 2)
                |> printLine;
            """),
    ];

    public static Sample Default => All[0];
}
