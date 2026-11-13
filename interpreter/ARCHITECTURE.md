# Aoi 解释器架构

本文说明 `refactor/generic-core` 分支对解释器核心的重构。目标不是给旧解释器打补丁，而是把「类型」「声明」「特征求解」「运算符分派」拆成独立层，使第 4 章的泛型、特征与动态类型可以在同一套模型上实现。

## 1. 旧架构的问题

重构前的 `Interpreter` 是一个纯动态求值器：

- 值只带一个 `TypeName` 字符串，`StructValue` 不保存类型实参，泛型在运行时被擦除。
- 运算符按名字存放在 `List<UserOperator>` 中，调用时取「最后一个同名运算符」，没有按操作数类型分派。
- `TraitDecl` 在解释时被直接忽略；`ImplDecl` 被摊平成全局运算符，丢失了 trait、trait 实参和 impl 泛型参数。
- 泛型参数、约束、`dyn`、显式类型实参在语法分析阶段被跳过。
- 索引器通过 `try { ... } catch { }` 逐个试探，错误会被吞掉。

因此旧架构无法表达「`T: Add[T]` 时使用调用方传入的 Add 实现」这类语义，也无法区分 `OrdByX` 与 `OrdByY`。

## 2. 新分层

```
Aoi.Core/
  Ast/Nodes.cs                语法树（补全泛型参数、约束、dyn、显式实参）
  Lexer/                      词法
  Parser/Parser.cs            语法（泛型头、impl 头、限定运算符等）
  Semantics/
    AoiType.cs                结构化类型、类型变量、替换、合一
    Declarations.cs           类型/特征/实现/运算符声明表 + 静态声明检查
    TypeResolver.cs           TypeRef -> AoiType，类型别名展开
    TraitEngine.cs            实现匹配、约束求解、默认/具名实现、依赖表
    OperatorResolver.cs       按运行时类型做重载解析
  Runtime/
    Values.cs                 带类型的值、运行时环境、泛型/实现上下文
    Interpreter.cs            树遍历求值器，调用 Semantics 完成分派
```

### 2.1 类型模型（`AoiType`）

类型是结构化值，而不是字符串：

- `PrimitiveType` / `UnitType` / `UnknownType` / `NeverType`
- `NamedType(name, args, definition)`：保留 `Vector[int]` 的类型实参
- `TupleType` / `NullableType` / `OpType`
- `TypeVar`：声明中的泛型参数和推理变量共用，按唯一 id 区分
- `DynType(self, requirements, inner)`：`dyn[T: ToStr[T]] T`

`TypeSubstitution` 保存 `TypeVar -> AoiType`；`TypeUnifier.TryUnify(pattern, actual, subst)` 完成签名匹配和类型实参推导。这样 `op sum[T: Add[T]] array: arr[T] -> T` 在调用时可以从 `arr[Vector]` 推出 `T = Vector`。

### 2.2 声明表（`SemanticModel`）

执行前先收集全部顶层声明，因此支持前向引用：

- `TypeDefinition`：struct/class/union/enum，含类型参数、字段、case。
- `TraitDefinition`：类型参数、成员签名、默认实现体。
- `ImplDefinition`：可选实现名、trait 与 trait 实参、impl 自身泛型参数、成员、别名。
- `OperatorDefinition`：统一普通运算符、getter/setter、索引器、convert、字面量后缀。
- `TypeAliasDefinition`：包括 `type DShow = dyn[...] ...`。

`SemanticModel.ValidateDeclarations()` 进行与求值无关的静态检查：字段/签名类型是否存在、类型实参数量、trait 是否存在、impl 成员是否属于该 trait、别名目标是否存在。重复默认实现由 `ValidateCoherence()` 检查。

### 2.3 特征求解（`TraitEngine`）

`TraitEngine` 负责「为一个 trait 应用找到实现」：

- 具名实现：`impl ByX of Ord2[Vector]`，只能通过 `ByX::<` 或显式实参选择。
- 默认实现：`impl of Ord2[Vector]`，无歧义时自动使用。
- 默认实现别名：`impl of Ord2[Vector] = ByX;` 让 `ByX` 成为默认。
- 泛型实现：`impl[T] of Add[Vector[T]]`，用合一匹配 `Add[Vector[int]]` 并解出 `T = int`。
- trait 自身的泛型要求：`trait VectorSpace[TNum, TVec: Add[TVec]]`；选择实现时递归求解 `Add[TVec]`。
- impl 自身的泛型要求：`impl[T: Vector[T]] of Add[Vector[T]]`；支持 `T: Vector[T]` 这种「类型作为要求简写」，展开为该类型参数的全部要求。
- 显式依赖：`impl OrdByX of Ord[Vector: PartialOrdByX[Vector]]`；以及调用处 `max[Vector: ByX]` / `max[Ord[int]]`。
- 依赖表：`ImplBinding.Dependencies` 把 trait 要求对应的实现一起传给泛型函数体，等价于字典传递（dictionary passing）。菱形要求会检查同一个 trait 应用是否被绑定到不同实现。

内置 trait（`Add` `Sub` `Mul` `Div` `Rem` `Neg` `Eq` `PartialOrd` `Ord` `ToStr` `Iter`）在声明表中也有签名；`int`/`float`/`str` 等由 `BuiltinSupports` 提供原生实现。因此 `op sum[T: Add[T]]` 对内置数值类型和自定义 `impl of Add[Vector]` 使用同一条解析路径。

### 2.4 运算符解析（`OperatorResolver`）

一次运算符调用按以下顺序解析：

1. 当前泛型调用帧中已绑定的 trait 实现（保证具名实现选择能传入被调函数体）。
2. 程序定义的运算符、默认 impl 成员、非 trait impl 成员、别名默认 impl 的目标成员。
3. 内置数值/比较运算符。

候选签名通过 `TypeUnifier` 与实参类型合一，并求解候选自身的泛型约束。显式实参支持：

- `max[int]`
- `max[int: Ord]`
- `max[int: Ord[int]]`
- `max[Vector: MaxByX, MinByX]`
- `v OrdByX::< u`
- `2.0 VectorSpace[float, Vector]::* v`

getter/setter/索引器/convert/literal 使用同一套签名匹配，只是参数形状不同。

### 2.5 运行时（`Values` + `Interpreter`）

- 每个 `AoiValue` 都带 `AoiType Type`；`StructValue` 保存 `NamedType`（含类型实参），`ArrayValue` 保存元素类型。
- `Environment` 除变量外，还保存 `TypeVar -> AoiType`、类型名 -> 类型、以及 `ImplContext`（当前调用可用的实现表）。
- 泛型运算符调用时：
  1. 从实参推导类型实参；
  2. 求解约束，收集 `ImplBinding` 依赖；
  3. 在新的 `Environment` 中绑定类型名与实现；
  4. 执行函数体。
- `DynValue` 保存底层值、`DynType` 和该值的虚表（实现集合）。对动态值调用 trait 运算符时，会把虚表加入当前环境，再按 trait 成员匹配；底层值仍保留具体类型，因此 `toStr`/`show` 等可以正确分派。
- 构造泛型结构体时，如果显式类型实参缺失，会用字段初始化器的类型反推；`new arr[DShow] { 1, "hi", true }` 会按 `DynType` 隐式包装每个元素。

## 3. 第 4 章覆盖情况

| 文档内容 | 状态 |
|---|---|
| 4.1 泛型运算符、泛型类型 | 已实现（含显式/推导类型实参） |
| 4.2 特征、默认成员实现 | 已实现 |
| 4.3 默认/具名实现、`Impl::op`、`Trait[..]::op`、别名默认 | 已实现 |
| 4.4 泛型要求、trait 要求、impl 时指定依赖、简写 | 已实现 |
| 4.5 带泛型的 impl、默认实现唯一性 | 已实现 |
| 4.6 `dyn`、`value[Impl]`、`value::T` | 基本实现（`dyn` 单 self 参数的常见用法） |
| 4.7 `type` 别名（含 dyn 别名） | 已实现 |

## 4. 仍可继续完善的地方

- 目前类型检查主要在「声明 + 使用点」完成，尚未实现完整的静态表达式类型推断；`SemanticModel`、`TypeResolver`、`TraitEngine`、`OperatorResolver` 已为后续的 `TypeChecker` 提供了可直接复用的接口。
- `dyn[T0, T1, ...]` 的多个类型参数目前只把第一个作为 `Self`；多参数投影和 `value::T` 出现在类型语法中的写法还需要扩展。
- `Iter`/`iterate` 的内置实现只覆盖数组、Span 和 Range；把 `for` 完全改为 trait 分派是下一步。
- 泛型 impl 的相干性检查目前只检查默认实现的「头是否能合一」，还没有实现完整的特化重叠诊断。
- 闭包尚未携带静态运算符类型；目前按 `OpType(Unknown, Unknown)` 宽松匹配。

## 5. 测试

```bash
cd interpreter
dotnet run --project Aoi.Tests/Aoi.Tests.csproj
```

测试（14 个用例）覆盖：泛型结构体推导、泛型运算符、内置/自定义 Add、具名实现与默认方法、泛型 impl、impl 别名、`dyn` 数组与显式 `a[ShowA]`、泛型类索引器、trait 泛型要求、约束失败诊断，以及仓库原有示例。
