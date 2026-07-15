# ExcelDB

ExcelDB 用 `.proto` 定义表结构、用 `.xlsx` 填写数据，并生成 C# 与按目标划分的 runtime bytes。Windows x64 版本只有一个自包含的 `exceldb.exe`；使用机器不需要安装 .NET、SDK、protoc、Node、Python 或 Unity。

## 下载后直接开始

从 [Releases](https://github.com/764s/ExcelDB/releases/latest) 下载 `exceldb-win-x64.zip`，解压得到 `exceldb.exe`。

最简单的方式是双击 `exceldb.exe`。你可以先把它复制到目标空目录，也可以把它安装在固定位置；Project Hub 都会让你选择任意项目目录，然后提供“初始化”“创建／编辑表结构”“重新生成”“检查数据”“生成 bytes”等可见操作。它会记住最近使用的项目。你无需先寻找配置文件。

也可以在空目录中打开终端，完整使用 CLI 或 BAT：

```bat
exceldb.exe init .
exceldb.exe table create Hero --workbook Excel\game.xlsx --field name:string --field hp:int32
```

初始化后，普通使用者只需要认识四类工件：

```text
GameConfig\
├─ Schema\                         表结构
├─ Excel\                          Excel 数据
└─ Generated\
   ├─ CSharp\                      生成的 C#
   └─ Bytes\                       生成的 runtime bytes
```

默认生成目录是 `Generated\CSharp`；默认 client bytes 是 `Generated\Bytes\client\config.bytes`。

工具配置、报告、缓存和恢复信息位于隐藏的 `.exceldb` 中。发生错误时从 Project Hub 的“查看报告”进入即可，不需要日常维护它。

## 填数据并生成 bytes

打开 `Excel\game.xlsx`，在 `Hero` sheet 第 4 行起填写业务列。不要手工填写或修改 `__guid`、`__rev` 系统列。保存后执行：

```bat
exceldb.exe data prepare
exceldb.exe check
exceldb.exe convert
```

默认 client 输出为：

```text
Generated\Bytes\client\config.bytes
Generated\Bytes\client\config.bytes.manifest.json
```

服务端和未来目标同样自动派生目录，不需要手填输出路径：

```bat
exceldb.exe convert --target server
```

其输出位于 `Generated\Bytes\server\config.bytes`。`--out` 仅用于高级的一次性覆盖。

## 日常操作

- 只改数值：编辑 `Excel` 中的 `.xlsx`，然后运行 `check` 和 `convert`。
- 新增 Excel 行：填写后先运行一次 `data prepare`，再检查和转换。
- 改表结构：使用 `table edit`，随后运行 `generate`；不要直接编辑生成的 C#。
- 外部 IDE 报 `import "exceldb/options.proto"` 不存在：在 Project Hub 点“修复 Proto 依赖”，或运行 `exceldb.exe project repair-imports`。
- 查看四类工件状态：运行 `exceldb.exe project inspect`。

常用命令示例：

```bat
exceldb.exe table edit game.configs.Hero --add speed:float
exceldb.exe generate
exceldb.exe data prepare
exceldb.exe check
exceldb.exe convert --target client
exceldb.exe convert --target server
```

`generate` 会重新编译当前业务 proto、修复 IDE 使用的系统 proto 镜像、更新 manifest 拥有的 C#，并同步全部 Excel 结构；它保留业务单元格，不修改业务 proto、RowGuid 或 bytes。

## 放入目录或安装后调用

- 便携方式：把 EXE 和 [`samples/init-with-portable-exceldb.bat`](samples/init-with-portable-exceldb.bat) 放在一起。
- PATH 方式：把 `exceldb.exe` 所在目录加入 `PATH`，使用 [`samples/init-with-installed-exceldb.bat`](samples/init-with-installed-exceldb.bat)。
- 两个 BAT 都接受项目目录参数，路径可以包含空格；不传参数时使用当前目录。
- 可选的第二个参数是 Generated C# 真实目录。

例如：

```bat
init-with-installed-exceldb.bat "D:\Game Config"
init-with-installed-exceldb.bat "D:\Game Config" "E:\Unity Project\Assets\Generated\ExcelDB"
```

初始化时可把生成 C# 直接放到宿主工程目录，而不创建无效的本地副本：

```bat
exceldb.exe init "D:\Game Config" --generated-csharp-dir "E:\Unity Project\Assets\Generated\ExcelDB"
```

## 项目设置与诊断

Project Hub 的“高级／诊断”区域提供项目设置、打开项目配置、打开报告、打开内部目录和清理缓存。CLI 对应入口为：

```bat
exceldb.exe project inspect
exceldb.exe project configure --generated-csharp-dir "E:\Unity Project\Assets\Generated\ExcelDB"
exceldb.exe project repair-imports
exceldb.exe project clean-cache
```

项目配置是 `.exceldb\project.json`。旧的根目录 `ExcelDb.Project.json` 只会被识别为 legacy v1，并提示重新初始化；工具不会自动迁移它。

命令退出码固定为：`0` 成功、`1` 数据错误、`2` blocker 或 stale、`3` 用法或环境错误。重定向输入／输出时，无参数启动会打印用法并返回 `3`，不会尝试打开窗口。

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

生成的便携程序是 `artifacts\exceldb-win-x64\exceldb.exe`。

## 更多资料

- [完整使用流程](docs/usage.md)
- [CLI、CI 与 Git 样例](samples/)
- [M1–M8 权威规范](docs/spec/README.md)
- [源码](src/)
- [自动化测试](tests/)

生成物保持宿主无关；Unity 等宿主按自己的工程结构引用项目配置指向的 Generated C# 真实目录和目标 bytes 即可。
