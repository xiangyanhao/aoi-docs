# Aoi 解释器

基于 .NET 8 的 Aoi 语言树遍历解释器，实现语言设计文档中的核心语法。

项目结构：

- `Aoi.Core`：共享的词法 / 语法 / 运行时库（供 CLI 与 playground 使用）
- `Aoi.Interpreter`：命令行入口

浏览器 playground 见 [`../playground/`](../playground/)。

## 依赖

- [.NET 8 SDK](https://dotnet.microsoft.com/download)

## 构建

```bash
cd interpreter
dotnet build
```

可执行文件位于 `Aoi.Interpreter/bin/Debug/net8.0/aoi.dll`。

## 运行

```bash
# 运行源文件
dotnet run --project Aoi.Interpreter -- ../examples/hello.aoi

# 或直接调用
dotnet Aoi.Interpreter/bin/Debug/net8.0/aoi.dll ../examples/hello.aoi

# 执行片段
dotnet run --project Aoi.Interpreter -- -e 'printLine "hi";'

# 交互式 REPL
dotnet run --project Aoi.Interpreter -- -i
```

## 示例

仓库根目录 `examples/` 下提供了若干示例：

| 文件 | 说明 |
|------|------|
| `hello.aoi` | Hello, World |
| `factorial.aoi` | 自定义一元运算符与递归 |
| `control_flow.aoi` | `when` / `for` / 可空模式匹配 |
| `vector_sum.aoi` | 结构体、自定义 `+`、数组求和 |
| `properties_literals.aoi` | 属性 / 字面量后缀 / `set` / `convert` 并列 / 元组交换 |

## 当前支持范围

**已实现：**

- 基础类型：`int` `float` `bool` `char` `str`、元组、数组、`Range`、可空类型
- 自定义 `struct` / `class` / `enum` / `union`
- 变量、赋值、元组解构赋值（如 `(a, b) = (b, a)`）
- 控制流：`if` / `loop` / `while` / `for` / `when` / `break` / `continue`
- 一元 / 二元自定义运算符、`return`
- 属性 `get` / `set`、索引属性 `get/set self.[i]`（含值类型属性嵌套写回）
- 字面量后缀（`literal`，如 `2.0i`）
- `set` 表达式（`set arr { … }` / `instance set { … }`）
- 基础模式匹配（`is`、`if is`、`when`、`some`/`none`）
- 闭包、管道 `|>`、运算符柯里化（如 `(+) 42`）、环境中的二元闭包并列调用（`1 add 2`）
- `convert` 可作为运算符名用于并列 / 管道（非泛型）
- `?.0` 等安全元组成员、`str.[i]` 字符索引
- `try` / `catch` / `throw`（基础）
- `impl` 中的运算符定义（特征系统为简化实现）
- 内置 `printLine` / `print` / `toStr`
- 运行时防护：调用深度上限（默认 1 024）、求值步数上限（默认 5 000 万），并用 `EnsureSufficientExecutionStack` 把递归栈溢出转成可捕获错误；playground 使用更紧的限额（深度 512 / 步数 200 万）
- 复合赋值 `+=`/`-=`/`*=`/`/=`/`%=`、顶层表达式块、`for () in …` 丢弃绑定、以及 `printLine x op y` 形式的并列调用

**尚未完整实现或仅部分支持：**

- 完整的特征约束求解、`dyn` 动态类型与虚表分派、泛型参数（第 4 章）
- 对象字面量形式的赋值解构（`{ a, [2]: b } = …`）
- 静态类型检查（当前为动态求值）

语言设计仍以仓库中的章节文档为准；解释器会逐步向文档靠拢。
