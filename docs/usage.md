# ExcelDB 使用流程

ExcelDB 的首要交付物是对应平台的自包含单文件。Windows x64 使用 `exceldb.exe`；目标目录不需要安装 .NET、SDK、protoc、Node 或 Python。

## 1. 初始化空目录

便携方式：把 `exceldb.exe` 复制到任意合法空目录，在该目录双击运行进入交互引导，或明确执行：

```bat
exceldb.exe init .
```

也可以把 EXE 与 [`init-with-portable-exceldb.bat`](../samples/init-with-portable-exceldb.bat) 放在一起，从其他位置初始化目标目录：

```bat
init-with-portable-exceldb.bat "D:\Game Config"
```

BAT 不带参数时以当前目录为目标；便携 BAT 始终调用与自身同目录的 `exceldb.exe`，因此可从任意工作目录执行。

安装或加入 `PATH` 后，可使用 [`init-with-installed-exceldb.bat`](../samples/init-with-installed-exceldb.bat)：

```bat
init-with-installed-exceldb.bat "D:\Game Config"
```

已安装 BAT 不带参数时同样以当前目录为目标，并通过 `PATH` 调用 `exceldb`。

初始化只创建唯一配置 `ExcelDb.Project.json` 及其五个目录。重复执行是幂等校验，不覆盖已有业务文件。

## 2. 创建第一张表

日常入口只填写简单字段；默认初始化器自动补 `id` key，标准导出策略自动物化 `client`/`server`：

```bat
exceldb.exe table create Hero --workbook Data\game.xlsx ^
  --field name:string --field hp:int32
```

结果是同一事务中的 `.proto`、生成 C#、descriptor cache 和 `.xlsx`。复杂结构继续以 `.proto` 为唯一事实源；不需要维护第二份模板或声明文件。

增加、改名或删除字段时使用同一计划管线：

```bat
exceldb.exe table edit game.configs.Hero --add speed:float
exceldb.exe table edit game.configs.Hero --rename 2:display_name
exceldb.exe table edit game.configs.Hero --remove 3
```

定制发行版可用纯 C# 实现 `ITableInitializer` 或 `IExportTargetStrategy`，在 `ExcelDbToolHost.RunAsync` 的 composition root 注册。策略只填充尚未冻结的简单建表/编辑意图；确认结果仍写入 `.proto`，运行期不会再次执行策略。

`ICellFormat` 使用同一注册方式；注册表会贯穿 `generate`、`normalize`、`check`、`convert` 与可选 Excel source。下面是定制启动项目的最小 composition 形态：

```csharp
using ExcelDb.Cli;
using ExcelDb.Schema.Authoring;
using ExcelDb.Workbooks.Formatting;

return await ExcelDbToolHost.RunAsync(
    args,
    static builder => builder
        .UseTableInitializer(new GameTableInitializer())
        .UseExportTargetStrategy(new GameExportTargets())
        .UseCellFormat(new AngleCellFormat()));

sealed class GameTableInitializer : ITableInitializer
{
    public string Id => "game.table:v1";

    public void Initialize(in TableInitializationContext context, ITableDraft draft)
    {
        new DefaultTableInitializer().Initialize(in context, draft);
        draft.AddField(new SimpleFieldDefinition("source", SimpleFieldType.String));
    }
}

sealed class GameExportTargets : IExportTargetStrategy
{
    private static readonly string[] Targets = ["client", "server", "lite-client"];
    public string Id => "game.targets:v1";

    public void Apply(in ExportTargetStrategyContext context, IExportTargetDraft draft)
    {
        draft.TryFillTableExportTargets(Targets);
        var inherited = draft.TableExportTargets!.Targets;
        foreach (var field in draft.Fields)
            draft.TryFillFieldExportTargets(field.Name, inherited);
    }
}

sealed class AngleCellFormat : ICellFormat
{
    public string Identity => "angle:v1";
    public string Describe(CellFormatContext context) => "<value>";

    public bool TryParse(string text, CellFormatContext context,
        out string canonical, out string? error)
    {
        if (text.Length >= 2 && text[0] == '<' && text[^1] == '>')
        {
            canonical = text[1..^1];
            error = null;
            return true;
        }

        canonical = string.Empty;
        error = "Expected <value>.";
        return false;
    }

    public bool TryWrite(string canonical, CellFormatContext context,
        out string text, out string? error)
    {
        text = $"<{canonical}>";
        error = null;
        return true;
    }
}
```

自定义 `ICellFormat.Identity`（例如 `angle:v1`）必须与 proto 的 `format: { codec: "angle:v1" }` 一致。三个实现都应确定、无 I/O；它们编译进受信任的定制 EXE。标准 EXE 不扫描项目 `.cs`、外部程序集或声明式 profile。

## 3. 在 Excel 填数据

打开生成的 workbook，在第 4 行起填写业务列。新行的 `__guid` 保持空白；它表示 `pending-new`，不会在只读检查时被暗改。

复杂消息不要求在一个单元格里手写大段 JSON。展开消息显示为普通简单列；重复消息和 message-map 显示为独立子表，使用者只填写 `__parent_guid`、`__ordinal`（或 `__map_key`）以及子消息的简单字段。导入器会按数字字段路径自动聚合，子表行不会变成可引用资产。

内部引用填写稳定的人读 token：

```text
<table-id>:<UTF-8 percent-encoded key components>
```

例如 `101:sword`；复合 key 用未转义 `|` 分隔，key 内部的 `|` 写成 `%7C`。

填完后显式固化新行身份，再检查：

```bat
exceldb.exe data prepare
exceldb.exe check
```

`data prepare` 的冻结计划只允许写目标行的 `__guid`。计划形成后 workbook、schema 或行内容发生变化时，应用会以 stale blocker 拒绝并保持原文件。

## 4. 生成运行时数据

客户端使用 Project 中的默认输出：

```bat
exceldb.exe convert
```

服务端及未来目标必须显式给出单一 target 和输出路径：

```bat
exceldb.exe convert --target server --out Build\ExcelDB\server.bytes
```

每个输出都有同行 manifest，运行时以 `(SchemaHash, ExportTargetId)` 同 generated registry 做硬门禁。一次失败只保留该 target 的旧工件。

## 5. 安全计划与自动化

危险写操作可先冻结计划：

```bat
exceldb.exe generate --dry-run --plan .exceldb\generate.plan
exceldb.exe generate --apply-plan .exceldb\generate.plan
```

计划包含 source fingerprints、确定序 mutations、风险和 `planHash`；body 被修改或输入变化时零写入拒绝。

退出码固定为：

- `0`：成功；
- `1`：数据/校验错误；
- `2`：blocker 或 stale，零危险写入；
- `3`：用法或环境错误。

CI 与 Git 配方位于 [`samples/ci`](../samples/ci) 和 [`samples/git`](../samples/git)。
