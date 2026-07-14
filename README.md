# ExcelDB

ExcelDB 正在以纯 C# 实现 M1–M8：`.proto` 定义结构，`.xlsx` 保存策划数据，工具生成 C# 与按目标划分的 runtime bytes；authoring、运行时、Unity adapter、兼容分析和迁移共享同一套身份、诊断与事务门禁。

当前 M1–M8 模块均为 **Dependency-Complete / Verified**：完整自动化矩阵、真实 XLSX 迁移、崩溃恢复和 Windows 单文件离线冒烟均已通过。权威契约见 [规范总纲](docs/spec/README.md)，实际操作见 [使用流程](docs/usage.md)。

## 最短使用流程

Windows x64 的交付物是自包含单文件 `exceldb.exe`。目标机器不需要安装 .NET、SDK、protoc、Node 或 Python。

把 EXE 复制到任意合法空目录后执行：

```bat
exceldb.exe init .
exceldb.exe table create Hero --workbook Data\game.xlsx --field name:string --field hp:int32
```

在生成的 Excel workbook 第 4 行起填写数据，然后执行：

```bat
exceldb.exe data prepare
exceldb.exe check
exceldb.exe convert
```

不带参数运行 `exceldb.exe` 会进入三阶段交互引导。也可以把 EXE 与 [便携 BAT](samples/init-with-portable-exceldb.bat) 放在一起，或安装到 `PATH` 后使用 [已安装 BAT](samples/init-with-installed-exceldb.bat) 初始化其他目录；路径中可以包含空格。

首次建表只需要简单字段。内置 `DefaultTableInitializer` 可自动补 `id` key，`StandardClientServerExportTargetStrategy` 可自动写入 `client`/`server` 目标。定制发行版可直接用 C# 注册 `ITableInitializer`、`IExportTargetStrategy` 与 `ICellFormat`，不需要模板 DSL、JSON/YAML profile 或运行时程序集扫描。

## 工程入口

- [解决方案](ExcelDB.slnx)
- [源码](src/)
- [自动化验收](tests/)
- [CLI、CI 与 Git 样例](samples/)
- [M1–M8 规范模块](docs/modules/)

本地验证与 Windows 单文件发布：

```powershell
dotnet test ExcelDB.slnx -c Release
dotnet publish src/ExcelDB.Cli/ExcelDB.Cli.csproj -c Release -p:PublishProfile=win-x64 -o artifacts/exceldb-win-x64
```

Unity 包由纯 C# 工程生成并通过 C# 9、UPM 结构和 Unity API stub 自动门禁；正式接入具体 Unity 2022.3 项目时仍应执行宿主 smoke test。
