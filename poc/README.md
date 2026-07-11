# SchemaPoc:模块 1 冒烟验证

目的:验证 `docs/modules/01-schema.md` 的 schema 契约可执行——同一份 proto 声明能同时生成 C# 与 Excel。

```text
Protos/game/game.proto(§4 示例全家桶)
  → protoc --descriptor_set_out(Grpc.Tools 自带 protoc)
  → SchemaCompiler:options v2 解析 + 值形分类(§3)+ lint(XDB 子集)+ schema_hash(xxHash64)
  → CSharpEmitter:强类型类 / 枚举 / 注册引导 / SchemaHash 常量
  → ExcelEmitter + XlsxWriter:三行表头、隐藏伴随列(__guid/__rev)、子表 sheet、
    __exceldb 元数据、__exceldb_keys 键清单、枚举/union/引用下拉、示例数据(join/curve/expr/map 文法)
```

运行:

```text
dotnet build poc/SchemaPoc/SchemaPoc.csproj -c Release
dotnet run --project poc/SchemaPoc -c Release --no-build       # 生成 + 自检,退出码 0 = 全部通过
dotnet build poc/SchemaPoc.GeneratedCheck -c Release           # 证明生成的 C# 可编译
```

产物:`poc/out/game.xlsx`、`poc/SchemaPoc.GeneratedCheck/Generated/GameConfigs.g.cs`。

已验证:§3 全部值形分类、EXPAND_AUTO 物化、文件级 SchemaDefaults、字段级 format(join 2 层)、
weighted/expression/curve/map/union/ref 声明、XDB001/002/004/005/007/009/013/015 lint、
schema_hash 跨次运行确定性、xlsx 结构自检(6 项)。

未覆盖(归属后续模块):cell 解析回读与校验(M6)、表头批注与 RowRef token 语义(M5)、
bytes 与运行时访问器(M7)。union 的 C# 成员暂为 object,类型化 getter 留给正式 codegen。当前权威规范入口见 [`docs/spec/README.md`](../docs/spec/README.md)。
