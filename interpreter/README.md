# Aoi 解释器

基于 .NET 8 的 Aoi 语言树遍历解释器。当前版本（`refactor/generic-core` 分支）重构了类型、声明、特征求解与运算符分派，已支持文档第 4 章的泛型与特征核心语义。

项目结构：

- `Aoi.Core`：共享的词法 / 语法 / 语义 / 运行时库（供 CLI 与 playground 使用）
- `Aoi.Interpreter`：命令行入口
- `Aoi.Tests`：无外部依赖的回归测试运行器

架构说明见 [`ARCHITECTURE.md`](ARCHITECTURE.md)。

浏览器 playground 见 [`../playground/`](../playground/)。

## 依赖

- [.NET 8 SDK](https://dotnet.microsoft.com/download)

## 构建

```bash
cd interpreter
dotnet build Aoi.Interpreter/Aoi.Interpreter.csproj
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

## 测试

```bash
dotnet run --project Aoi.Tests/Aoi.Tests.csproj
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
| `generics.aoi` | 泛型结构体、泛型运算符、泛型 impl |
| `traits.aoi` | 特征、具名实现、默认实现别名、特征要求 |
| `dyn.aoi` | `dyn` 动态类型与显式虚表包装 |

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
- `convert` 可作为运算符名用于并列 / 管道
- `?.0` 等安全元组成员、`str.[i]` 字符索引
- `try` / `catch` / `throw`（基础）
- 内置 `printLine` / `print` / `toStr`
- 运行时防护：`int` 加减乘（及部分除）溢出报错；调用深度上限（默认 1 024）、求值步数上限（默认 5 000 万）、可选墙钟超时（`MaxExecutionMilliseconds`），并用 `EnsureSufficientExecutionStack` 把递归栈溢出转成可捕获错误；playground 使用更紧的限额（浅调用深度 + 超时），避免 Blazor WASM 原生栈溢出导致页面崩溃
- 复合赋值 `+=`/`-=`/`*=`/`/=`/`%=`、顶层表达式块、`for () in …` 丢弃绑定、以及 `printLine x op y` 形式的并列调用

**第 4 章（已支持核心语义）：**

- 泛型运算符、泛型 `struct` / `class` / `union`，显式或自动推导类型实参
- 特征声明、默认成员实现、默认 / 具名实现、`Impl::op`、`Trait[..]::op`
- `impl of Trait[T] = Named;` 默认实现别名
- 泛型要求 `[T: Trait[T], ...]`，trait / impl 自身的要求，`T: Foo[T]` 简写
- 带泛型的 impl（如 `impl[T] of Add[Vector[T]]`）与默认实现唯一性检查
- `dyn[...]`、`value[Impl]`、`value::T` 的基本用法
- `type` 类型别名（含 dyn 别名）

**仍待完善：**

- 完整的静态表达式类型推断（目前是声明静态检查 + 使用点/运行时类型分派）
- 多参数 `dyn[T0, T1]` 的完整投影，以及 `value::T` 出现在类型语法中的写法
- 将 `for` 完全改为 `Iter` trait 分派
- 泛型 impl 的完整特化重叠诊断
- 对象字面量形式的赋值解构（`{ a, [2]: b } = …`）

语言设计仍以仓库中的章节文档为准。
