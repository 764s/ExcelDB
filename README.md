# ExcelDB

ExcelDB 用 `.proto` 定义数据结构，用 `.xlsx` 让策划填写数据，然后生成 C# 类型和可直接加载的 runtime bytes。

如果你只是想先跑起来，不需要先理解内部架构、descriptor 或代码生成器。Windows x64 版本是一个自包含的 `exceldb.exe`，目标机器不需要安装 .NET、SDK、protoc、Node 或 Python。

## 五分钟上手

### 1. 下载 EXE

打开 [Releases](https://github.com/764s/ExcelDB/releases/latest)，下载 `exceldb-win-x64.zip`，解压后会得到一个 `exceldb.exe`。

新建一个空目录，例如 `D:\GameConfig`，把 `exceldb.exe` 放进去，然后在该目录打开 PowerShell 或命令提示符。

> 如果 Releases 暂时没有可下载版本，请使用文末的“从源码构建”。正式 Release 由版本 tag 自动构建，不需要手工收集 DLL。

### 2. 初始化并创建第一张表

执行：

```bat
exceldb.exe init .
exceldb.exe table create Hero --workbook Data\game.xlsx --field name:string --field hp:int32
```

第二条命令会自动添加 `id` 主键，并创建：

```text
GameConfig\
├─ ExcelDb.Project.json       项目配置
├─ Schema\Hero.proto          表结构事实源
├─ Data\game.xlsx             使用者填写的数据
├─ Generated\                 生成的 C# 类型和 runtime registry
├─ Build\                     转换后的运行时数据
└─ .exceldb\                  工具缓存与报告
```

不带参数执行 `exceldb.exe` 也可以进入交互引导。

### 3. 在 Excel 中填写数据

打开 `Data\game.xlsx`，找到 `Hero` sheet，从第 4 行开始填写。例如：

| id | name | hp |
| --- | --- | --- |
| hero_001 | Knight | 100 |
| hero_002 | Mage | 60 |

不要手工填写或修改末尾的 `__guid`、`__rev` 系统列。复杂消息会展开为普通列；重复消息和 message-map 会生成独立子表，不需要在单元格里手写大段 JSON。

### 4. 固化身份、检查并转换

保存 Excel 后执行：

```bat
exceldb.exe data prepare
exceldb.exe check
exceldb.exe convert
```

成功后会得到：

```text
Build\config.bytes
Build\config.bytes.manifest.json
```

`data prepare` 只为新增行写入稳定 GUID；`check` 不会偷偷修改数据；`convert` 默认生成 `client` 目标。

至此，最小工作流已经完成：

```text
定义表结构 → 填写 Excel → data prepare → check → convert
```

## 日常使用者需要做什么

- 日常改数值：只编辑 `.xlsx`，然后运行 `check` 和 `convert`。
- 新增 Excel 行：填写后先运行一次 `data prepare`。
- 新增简单字段：运行 `table edit`，不要直接编辑 Generated 目录。
- 查看失败原因：先看命令行中的诊断位置和消息；退出码 `1` 是数据错误，`2` 是阻止写入的 blocker，`3` 是命令或环境用法错误。

常用命令：

```bat
exceldb.exe table edit game.configs.Hero --add speed:float
exceldb.exe generate
exceldb.exe normalize
exceldb.exe check
exceldb.exe convert
```

服务端等非默认目标必须给出独立输出：

```bat
exceldb.exe convert --target server --out Build\server\config.bytes
```

## 便携使用与 PATH 安装

- 便携方式：把 EXE 和 [`samples/init-with-portable-exceldb.bat`](samples/init-with-portable-exceldb.bat) 放在一起。
- PATH 方式：把 `exceldb.exe` 所在目录加入 `PATH`，然后使用 [`samples/init-with-installed-exceldb.bat`](samples/init-with-installed-exceldb.bat)。
- 两种 BAT 都接受目标目录参数，目录名可以包含空格。

完整命令、复杂字段、引用、冻结计划和定制 C# 策略见 [使用流程](docs/usage.md)。

## 从源码构建

源码构建需要 .NET 10 SDK：

```powershell
git clone https://github.com/764s/ExcelDB.git
cd ExcelDB
dotnet test ExcelDB.slnx -c Release
dotnet publish src/ExcelDB.Cli/ExcelDB.Cli.csproj `
  -c Release -p:PublishProfile=win-x64 `
  -o artifacts/exceldb-win-x64
```

生成的便携程序位于：

```text
artifacts\exceldb-win-x64\exceldb.exe
```

## Unity 与开发文档

- [完整使用流程](docs/usage.md)
- [CLI、CI 与 Git 样例](samples/)
- [M1–M8 规范](docs/spec/README.md)
- [源码](src/)
- [自动化测试](tests/)

Unity Runtime 与 Editor 均提供 UPM 投影；接入具体 Unity 2022.3 项目时，应在宿主工程中执行一次 Play Mode 和导入 smoke test。
