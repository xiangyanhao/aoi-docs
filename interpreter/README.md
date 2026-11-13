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

## 当前支持范围

**已实现：**

- 基础类型：`int` `float` `bool` `char` `str`、元组、数组、`Range`、可空类型
- 自定义 `struct` / `class` / `enum` / `union`
- 变量、赋值、基础解构
- 控制流：`if` / `loop` / `while` / `for` / `when` / `break` / `continue`
- 一元 / 二元自定义运算符、`return`
- 基础模式匹配（`is`、`if is`、`when`、`some`/`none`）
- 闭包、管道 `|>`、运算符柯里化（如 `(+) 42`）
- `try` / `catch` / `throw`（基础）
- `impl` 中的运算符定义（特征系统为简化实现）
- 内置 `printLine` / `print` / `toStr`

**尚未完整实现或仅部分支持：**

- 完整的特征约束求解、`dyn` 动态类型与虚表分派
- 属性 getter/setter、索引属性的完整语义
- 字面量后缀、全部类型转换规则
- 静态类型检查（当前为动态求值）

语言设计仍以仓库中的章节文档为准；解释器会逐步向文档靠拢。
