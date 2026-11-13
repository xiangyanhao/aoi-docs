# Aoi 示例

在仓库根目录执行：

```bash
cd interpreter
dotnet run --project Aoi.Interpreter -- ../examples/hello.aoi
dotnet run --project Aoi.Interpreter -- ../examples/factorial.aoi
dotnet run --project Aoi.Interpreter -- ../examples/control_flow.aoi
dotnet run --project Aoi.Interpreter -- ../examples/vector_sum.aoi
dotnet run --project Aoi.Interpreter -- ../examples/generics.aoi
dotnet run --project Aoi.Interpreter -- ../examples/traits.aoi
dotnet run --project Aoi.Interpreter -- ../examples/dyn.aoi
```

| 文件 | 说明 |
|------|------|
| `hello.aoi` | Hello, World |
| `factorial.aoi` | 自定义一元运算符与递归 |
| `control_flow.aoi` | `when` / `for` / 可空模式匹配 |
| `vector_sum.aoi` | 结构体、自定义 `+`、数组求和 |
| `properties_literals.aoi` | 属性 / 字面量后缀 / `set` / `convert` 并列 / 元组交换 |
| `generics.aoi` | 泛型结构体、泛型运算符、带泛型的 impl |
| `traits.aoi` | 特征、具名实现、默认实现别名、特征要求 |
| `dyn.aoi` | `dyn` 动态类型与显式虚表包装 |
