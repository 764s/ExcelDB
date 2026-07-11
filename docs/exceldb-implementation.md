# ExcelDB 实施文档

> 模块化重推演进行中:schema 声明方式已改为 proto(`docs/modules/01-schema.md`),本文 §1 的 CodeGen 工程形态、§2.5、§3-M1 与 §8.1 示例将随模块文档逐步修订,修订前以模块文档为准;AssetDatabase 门面已定稿(`docs/modules/02-assetdatabase.md`),§3-M3 3.1 所引 P§6.7 与 §8.3 样例照此修订;生成类型为普通 C# 类,不派生 `ScriptableObject`(M2 D4/Δ11),§8 各样例的基类声明、`CreateInstance` 与 `name` 用法照此修订;工作流已定稿(`docs/modules/03-workflow.md`,M3):§6 CI 序列追加生成代码新鲜度与评审工件(check + diff 报告)两道门禁、CI check 下 schema.drift 判 error(M3§9),CLI 增 `diff` 子命令(M3§8),§8.5 操作序列以 M3 WF3-WF5 为准;集成工具已定稿(`docs/modules/04-integration-tools.md`,M4,场景 × 使用者矩阵驱动):§6 读作 M4§5 流水线配方,§3-M5 5.2/5.3 与 §3-M6 6.3 工作项以 M4 为规格;CLI 为工作空间管理工具(选项式门面 W1 + `ExcelDb.Workspace.json`,M4§3),增 `normalize` 与 `--project`/`--workspace`(M4§3.3/§3.4)。

## 0. 口径

- 契约唯一来源是 `exceldb-lean-plan.md`,本文引用其章节记作 P§x.y;本文只回答"怎么建",不重定义任何契约;与计划冲突时修计划,不在本文放宽。
- 荷载口径同计划:工作项、结构决策、算法要点、命令、验收、基准数字为荷载行;本节与附录为非荷载。

## 1. 工程布局

### 1.1 solution 树

```text
ExcelDb.sln
Directory.Build.props                  统一 LangVersion=latest、Nullable=enable、net 版本变量
src/
  ExcelDb.Core/                        netstandard2.1,零 NuGet 依赖(删除现有 protobuf 引用)
    Attributes/  Objects/  Identity/  Schema/  Families/  Bytes/  Runtime/  Diagnostics/
  ExcelDb.CodeGen/                     netstandard2.0,Microsoft.CodeAnalysis.CSharp 4.1.0(Unity 2022.3 Roslyn 上限)
  ExcelDb.Editor/                      netstandard2.1,仅引用 Core
    Facade/  Import/  Merge/  Snapshot/  Undo/  Generate/  Watch/  Xlsx/
  ExcelDb.Cli/                         net10.0 console,引用 Editor;手写参数解析,不引第三方
unity/
  com.exceldb/                         UPM 包
    Runtime/ExcelDb.Unity.Runtime.asmdef      引用 Core.dll
    Editor/ExcelDb.Unity.Editor.asmdef        引用 Core/Editor.dll,UnityEditor only
    Plugins/ExcelDb.Core.dll  ExcelDb.Editor.dll  ExcelDb.CodeGen.dll(RoslynAnalyzer 标签)
  TestProject/                         Unity 2022.3 LTS 工程,playmode 测试宿主
tests/
  ExcelDb.Core.Tests/                  net10.0 + xunit(沿用现有栈)
  ExcelDb.Editor.Tests/                net10.0 + xunit
  fixtures/                            golden workbook 与 bytes 样本(版本控制内)
samples/
  Game.Configs/                        重写为特性 schema(现 proto 版废弃)
```

### 1.2 依赖与旧代码处置

- 全线零第三方运行库:xlsx 读写沿用手写 ZipArchive + XmlReader/XmlWriter 路线(现 `XlsxWorkbook.cs` 是参考实现,按 §2.3/§2.4 重写);xxHash64 手写进 Core(约 100 行,Unity 可移植)。
- CodeGen 是唯一带 NuGet 依赖的工程;`Microsoft.CodeAnalysis.CSharp` 锁 4.1.0,生成器代码限 C# 10 语法。
- 移植件:`Editing/UndoStack.cs`(改造为 canonical 行快照栈)、`Graph/DependencyGraph.cs`(改造为 CSR 增量结构);`ConfigDatabase`/proto/`SkillEditor.*` 不入新 sln,M4 完成后删除。
- 测试框架 xunit 2.5.x + `Microsoft.NET.Test.Sdk`,沿用现有 tests 工程配置。

## 2. 横切实现决策

### 2.1 内存与索引

- 编辑期行模型:resident object(用户类实例)为主体;合并/回写所需的"上次导入 canonical 值"不驻留内存,从快照文件按需载入(快照即 base)。
- 运行时表存储:columnar——每表定长 slot 数组(标量就地)+ 变长区引用(sid / offset,count),与 bytes 布局(P§7.4)同构,访问器两态共用偏移表。
- 字符串:全局 intern 池(开放寻址,xxHash64 → 池 id);bytes 打开时 sid 直接映射池 id,懒物化。
- key 索引:手写开放寻址 `u64 hash → u32 行序`,负载因子 0.7,扩容即水位线事件;guid 索引同构。
- AssetKey.gen:每表单调递增 u16,行槽复用时 +1,防悬空 handle 命中。
- DiagnosticSink:预分配 `Diagnostic[]` 环,溢出走水位线;code 全部为 `static readonly string` 常量,热路径不拼接。

### 2.2 线程模型

- 全部状态变更在主线程(editor tick / 宿主 publish point)执行;watcher 线程、CLI 并行解析只做只读工作并投递结果。
- `RuntimeDatabase.changed` 在 publish point 同步派发;订阅方重入调用写 API 一律抛 `InvalidOperationException`(P§14.26 旧文的排队策略不做,直接禁止)。

### 2.3 xlsx 读(workspace)

- `ZipArchive` 读模式 + `XmlReader`(`XmlReaderSettings` 池化复用);每 sheet 解析为 `CellGrid`(行索引 → 列稀疏数组,cell 存 UTF8 原文 span 引用 + 类型标记)。
- 变更检测先行:cell 原文先算 xxHash64 与上次 workspace 比对,不等才物化 string 并走 canonical 解析——保证重复 refresh 分配 O(变更)。
- sharedStrings 表一次载入 workspace 池;数字/日期按不变文化规范化(P§4.3),Excel serial date 转 ISO8601 在此层完成。

### 2.4 xlsx 写(补丁)

- 写回不整包重建:逐 zip entry 复制原字节,仅计划涉及的 sheet part 走流式补丁(XmlReader → XmlWriter,原样转发所有节点,命中计划 cell 的 `<c>` 元素才重写)。
- 补丁 cell 的字符串一律写 `t="inlineStr"`:不触碰 sharedStrings part(原字节拷贝,孤儿条目容忍),避免索引重排;数值/bool/日期写原生类型。
- 新行追加、行删除、伴随列、metadata sheet、`__exceldb_keys` sheet 都走同一补丁器;`__exceldb_keys` 与 metadata 是系统 part,允许整 part 重写。
- 复读校验(P§6.5 步骤 4):对临时文件重跑 §2.3 读,仅比对计划内 cell 的 canonical 值;非计划 part 断言字节不变(测试用,运行期免)。

### 2.5 schema_hash 与 codegen

- schema_hash 在 CodeGen 编译期算定,作为 `SchemaDescriptor.SchemaHash` 常量发射;运行期零计算。
- 生成器为 incremental:语法阶段只筛 `[ExcelTable]`;语义模型缓存按类型符号;六类产物(P§4.4)各自独立 partial 文件,便于 diff。
- 生成器诊断 code 前缀 `XDB0xx`(如 XDB001 嵌套结构体未展开、XDB002 Key 缺失、XDB003 数字 id 重复……),与运行期 `Diagnostic` 命名空间分开。

## 3. 里程碑工作项

每项:编号 | 内容与产出 | 验收(P§12 测试项或明示)。里程碑内按序,跨里程碑仅 M5 依赖 M4。

### M1 Core 只读竖切

| # | 工作项 | 验收 |
| --- | --- | --- |
| 1.1 | 建 `ExcelDb.sln`、`Directory.Build.props`、新 csproj 骨架;Core 删 protobuf;旧 samples 移出编译 | sln 全量 build 过 |
| 1.2 | Core 基础件:XxHash64、`GUID`/`UnityGuid`、`AssetKey`/`AssetIdentity`、`Diagnostic`/`DiagnosticSink`、`RuntimeMode` | 单元测试 + hash 已知向量比对 |
| 1.3 | 17 特性类 + `Object`/`ScriptableObject`/`Ref<T>`/`UnityRef`/`LocalizedTextRef`/`Curve`/`WeightedList<T>`/`Expression<T>` 类型骨架 | 编译期可声明 P§4.1 示例类 |
| 1.4 | CodeGen:符号抽取 → `SchemaDescriptor` 发射 + 编译期 schema_hash;六类产物;XDB0xx 诊断 | P§12-1 |
| 1.5 | bytes writer(两遍测量/发射,pooled)+ reader(单块载入 + 目录 + 校验)| P§12-7(坏例三种)|
| 1.6 | columnar TableStore + intern 池 + key/guid 索引 + `RuntimeDatabase` 读 API + `Prewarm` 物化 | P§12-7(查找)|
| 1.7 | 家族 runtime:map 二分、`Curve.Evaluate`、`WeightedList.Select`、expression 字节码 VM | P§12-10(runtime 半)|
| 1.8 | GC harness(§5.3)+ 读取路径门禁接入 CI | P§12-8(读取)|

### M2 Excel 导入

| # | 工作项 | 验收 |
| --- | --- | --- |
| 2.1 | xlsx 读 workspace(§2.3):CellGrid、sharedStrings、日期/数字规范化 | fixtures 解析比对 |
| 2.2 | 列绑定:行 2 匹配 + FormerName 映射;metadata sheet 读(P§5.2);未知/缺失列诊断 | P§12-1(FormerName 链)|
| 2.3 | 身份扫描:5 规则单遍实现 + pending guid;快照 read/write(临时文件 + 原子替换)| P§12-3 |
| 2.4 | 校验管线:内建约束 → 引用两阶段解析(表内先行,跨表收尾)→ `IRowValidator` | P§12-2(全部)|
| 2.5 | 物化 resident objects + 索引 + 依赖图构建 + `ImportReport` | 导入报告 golden 比对 |
| 2.6 | `samples/Game.Configs` 重写为特性 schema;golden `game.xlsx` fixtures 制作(§5.1)| fixtures 入库 |

### M3 编辑闭环

| # | 工作项 | 验收 |
| --- | --- | --- |
| 3.1 | `AssetDatabase` facade 全签名(P§6.7)+ label 索引与 API | facade 单测 |
| 3.2 | SO/SP:codegen 属性树、路径 handle 预解析、迭代器、数组编辑、多目标混合值 | SP 路径往返测试 |
| 3.3 | Undo/dirty:canonical 行快照栈(移植 UndoStack)、状态机(P§6.5)| 状态机全转移测试 |
| 3.4 | 写回补丁器(§2.4)+ 写回计划构造 + 临时文件/复读/原子替换 + 文件锁重试 | P§12-5 |
| 3.5 | Create/Delete/RenameAsset 落盘 + rename 引用改写(依赖图驱动)| rename 场景测试 |

### M4 并发与热度

| # | 工作项 | 验收 |
| --- | --- | --- |
| 4.1 | 三方合并核心(§4.1)+ 冲突索引与 `ResolveConflict` + 状态机接入 | P§12-4(12 场景)|
| 4.2 | watcher:防抖 300ms + 内容 hash 去重 + 主线程队列 + Excel 锁重试 | 抖动模拟测试 |
| 4.3 | `ExcelDataSource`:workspace 复用 + 先 hash 后物化(§2.3)| 重复 refresh 分配曲线平 |
| 4.4 | hot reload 事务(P§7.5):diff → publish point → patcher → 索引增量 → ChangeSet | P§12-7(5 事件)|
| 4.5 | 切源事务(P§7.6)+ 失败保留旧图 | P§12-7(切源)|
| 4.6 | 依赖图 CSR 增量更新 + DependencyChanged 传递闭包 | 闭包正确性测试 |

### M5 Unity adapter(依赖 M4)

| # | 工作项 | 验收 |
| --- | --- | --- |
| 5.1 | UPM 包骨架 + DLL 同步脚本(build 后拷贝 Plugins)+ CodeGen 打 RoslynAnalyzer 标签 | TestProject 编译过,生成器生效 |
| 5.2 | Browser 窗口:UIToolkit TreeView(workbook/表/行)+ inspector 面板 + generic drawer 集 + `[CustomDrawer]` | 手测清单 + UI 冒烟 |
| 5.3 | picker(FindAssets 驱动)+ UnityRef ObjectField 桥 + 冲突对话框 | 手测清单 |
| 5.4 | Play Mode 接线:publish point 挂 `EditorApplication.update`/PlayerLoop;domain reload 后按 `ExcelDb.Project.json` 重挂载 | playmode 测试 |
| 5.5 | 构建钩子 `IPreprocessBuildWithReport` → convert → `StreamingAssets/ExcelDb/`;Release 读取样例场景 | 出包冒烟 |
| 5.6 | playmode 测试集:读取等价 + ProfilerRecorder GC 门禁(§5.3)| P§12-8(Unity 侧)|

### M6 收口

| # | 工作项 | 验收 |
| --- | --- | --- |
| 6.1 | 结构生成完整分级(P§6.6 表逐行)+ `--purge`/`--rekey` 门禁 + dry-run | P§12-6 |
| 6.2 | 枚举/引用下拉 + `__exceldb_keys` 维护 + 表头批注写入 | 生成后 Excel 手测 |
| 6.3 | CLI 四命令 + `--json` 报告 + 退出码(P§10)| CLI 集成测试 |
| 6.4 | 家族 bake 收口:expression convert 期字节码、curve bake、weighted 前缀和、`ILocalizedTextProvider` | P§12-10(全部)|
| 6.5 | golden 端到端 + CI 脚本(§6)+ 基准测试(§5.4)| P§12-9 |

## 4. 关键算法要点

### 4.1 三方合并(P§6.3)

按 guid 做 base/theirs/mine 三集合并集迭代:行存在性先裁(增删对 = 行级结果),存活行内按字段序走 cell 三比较(canonical 值);冲突写入冲突索引(`ConflictRecord` + 三值快照,供 `GetConflicts`/`ResolveConflict` 消费)。theirs 来自 workspace 重导入,mine 来自 resident 对象经写出器 canonical 化,base 按需从快照流式读——三路都是有序 guid 序列,单遍归并,零随机查找。

### 4.2 身份扫描(P§5.3)

单遍行扫描收集 (guid, key, 内容 hash),快照载入为 guid → (key, hash, rev) 字典;五规则按序判定,重复 guid 组内先按快照匹配再按行序;pending 区(上次分配未落盘的 guid)以 (key, hash) 为键参与规则 1 匹配。输出:行 → 身份决议 + 诊断,供物化与合并使用。

### 4.3 hot reload diff(P§7.5)

workspace 给出变更 cell 集(§2.3 hash 预筛)→ 聚合为变更行集 → 对 resident 逐行走生成的 patcher(字段级比较 + 就地覆写,返回是否实变)→ 实变行进 ChangeSet;新增/消失行走物化/Missing 路径;key 变更行同步 key 索引增量(删旧插新)。

### 4.4 expression 编译与 VM

Pratt parser → 类型化 AST(符号表字段与函数注册表在编译期解析为槽位)→ 常量折叠 → 字节码(操作码 ≤ 32:载常量/载符号槽/算术/比较/逻辑/跳转/调用);VM 为 `Span<Value>` 栈机,`Value` 为 8 字节 union(double/long/bool),`Eval(in TSymbols)` 通过生成的符号读取器免装箱;字节码与常量池进 bytes 变长区。

### 4.5 curve bake 与 weighted

curve:点列排序校验 → hermite 段系数预计算(a,b,c,d)→ `Evaluate` 二分定段 + 多项式求值;域外按端点钳制。weighted:convert/导入期算前缀和数组,`Select` 生成 [0,total) 随机数二分;带条件者先用 expression VM 过滤到池化候选区再二分。

### 4.6 依赖图增量

CSR 双向(出边/入边)+ 行级脏重建:行变更时旧出边批量摘除、新出边插入(边槽池化,空洞复用);`DependencyChanged` 闭包 = 入边 BFS,visited 位图池化,深度不限但环安全。

### 4.7 bytes 两遍写

第一遍遍历 resident 按访问器测量:定长 stride、变长区尺寸、字符串收集入 intern 序;第二遍按 P§7.4 布局顺序发射到单 pooled buffer,列目录偏移在测量期算定;guid/rev/key 索引在发射期就地构建,写完整体 xxHash64 收尾。

## 5. 测试基建

### 5.1 fixtures

- `tests/fixtures/golden/`:入库 xlsx 样本。制作方式:代码生成基础结构(builder 工具 `ExcelDb.Cli fixture` 隐藏子命令)→ 人工用 Excel 打开注入样式/公式/批注/辅助列/自由 sheet → 入库并记录清单 `fixtures.md`。
- 每个 fixture 配对期望物:`*.import.json`(期望 ImportReport)、`*.rows.json`(期望 canonical 行值),测试做结构化比对。
- 身份/合并场景用"操作重放"fixture:基础 xlsx + 脚本化变换(排序/剪切/复制/改 cell,直接操作 zip part 的工具类),避免入库数十个近似文件。

### 5.2 保真断言

- part-diff 助手:解包两 xlsx,非计划 part 断言字节相等;计划 sheet part 内,断言仅计划 cell 元素变化(XmlReader 双流同步走读)。
- 计划内 cell 允许存储形态变化(sharedStrings → inlineStr,§2.4),断言 canonical 值而非原文。

### 5.3 GC harness

- CoreCLR:预热 → `GC.GetAllocatedBytesForCurrentThread` 包夹 10^4 次目标操作 → 断言增量为 0;水位线增长用 `AllocationStats` 白名单显式豁免并断言只发生在首轮。
- Unity playmode:`ProfilerRecorder(ProfilerCategory.Memory, "GC.Alloc")` 包夹同款循环,断言样本计数 0;Mono 与 CoreCLR 各跑一遍(TestProject 双后端配置)。
- 覆盖清单即 P§7.9 覆盖列表,每 API 一个 `[Theory]` 数据行,新增热路径 API 必须登记。

### 5.4 性能基准(阈值即 CI 断言,BenchmarkDotNet 不引入,手写计时三次取中位)

| 场景 | 阈值 |
| --- | --- |
| 导入 10 万行 × 20 列 xlsx | ≤ 3 s |
| convert 同上 | ≤ 5 s |
| bytes 打开(50 MB) | ≤ 150 ms |
| `TryGetAsset`(预热后) | ≤ 100 ns |
| hot reload 单 cell(解析到事件,除防抖) | ≤ 100 ms |
| 切源 10 万行(publish 段) | ≤ 200 ms |

## 6. CI 命令序列

```text
dotnet build ExcelDb.sln -c Release
dotnet test tests/ExcelDb.Core.Tests -c Release
dotnet test tests/ExcelDb.Editor.Tests -c Release          # 含 GC 门禁与基准断言
dotnet run --project src/ExcelDb.Cli -- lint    --schema samples/Game.Configs --workbooks "tests/fixtures/golden/*.xlsx" --json out/lint.json
dotnet run --project src/ExcelDb.Cli -- check   --schema samples/Game.Configs --workbooks "tests/fixtures/golden/*.xlsx" --json out/check.json
dotnet run --project src/ExcelDb.Cli -- convert --schema samples/Game.Configs --workbooks "tests/fixtures/golden/*.xlsx" --out out/config.bytes
Unity TestProject:-runTests -testPlatform PlayMode(M5 起入流水线;Unity License 不可用的环境降级为本地必跑项)
门禁:任一命令退出码 ≥ 1 即失败;convert 产物 + manifest + 各 json 报告上传为构建工件
```

## 7. 风险与预案

- Unity 2022.3 Roslyn 上限 4.1:CodeGen 语法限 C# 10,CI 加 TestProject 编译冒烟防回归;若项目后续升 Unity,仅放宽 pin。
- 流式 XML 补丁保真:XmlWriter 属性顺序/自闭合差异可能触碰非计划节点——补丁器对非命中节点走原始字节透传(记录 reader 偏移,直接拷贝区间),只有命中 `<c>` 走重建;做不到透传的 part 触发 P§6.5 步骤 3 的整 part 回拷校验路径。
- Excel serial date 与 1904 纪元 workbook:workspace 读取按 workbook 属性识别纪元;fixtures 各留一例。
- Mono GC 计量不可靠:playmode 门禁以 ProfilerRecorder 为准,CoreCLR 精确计量为主门禁。
- 大表内存峰值:导入与 convert 流式化(逐表处理,CellGrid 复用);10 万行基准同时记录峰值 RSS,回归即查。

## 8. 使用样例

样例只演示 P 契约与本文实现约定,不新增 API;8.1 的类型即 `samples/Game.Configs` 与 golden fixtures 的基底。

### 8.1 定义配置(程序,一次性)

```csharp
using ExcelDbEngine;

// preset = 基类字段组(P§4.7):所有配置表共享 key/显示名/标签/开关
public abstract class GameAssetBase : ScriptableObject
{
    [Key] public string id = "";        // 单字段 string key;Object.name 即其投影(P§7.1)
    [Display("显示名")] public LocalizedTextRef displayName;
    [Labels, Display("标签")] public List<string> tags = new();
    [Display("启用")] public bool enabled = true;
}

public enum DamageType { Physical, Fire, Frost }

[Union] public abstract class SkillEffect { }
[Variant("damage")] public sealed class DamageEffect : SkillEffect { public int amount; }
[Variant("dot")]    public sealed class DotEffect    : SkillEffect { public int amountPerTick; public float duration; }

public struct Cost { public int mp; public int hp; }                  // 展开列元素
public struct CritSymbols { public float baseDamage; public float crit; }  // expression 符号表

public class DropEntry                                                // 子表元素,无身份声明
{
    [Display("物品")] public Ref<ItemConfig> item;
    [Display("数量")] public int count = 1;
}

[ExcelTable] public class ItemConfig : GameAssetBase { [Display("堆叠上限")] public int maxStack = 99; }

[ExcelTable, Validator(typeof(SkillRules))]
public class SkillConfig : GameAssetBase
{
    [Display("伤害", Comment = "基础伤害值"), Range(0, 9999)] public int damage = 10;
    [Display("伤害类型")] public DamageType damageType;
    [ExpandColumns, Display("消耗")] public Cost cost;
    [Display("跳字节奏")] public List<float> tickTimes = new();
    [Display("等级成长")] public Curve damageByLevel;
    [Display("暴击公式"), Expression(Symbols = typeof(CritSymbols))] public Expression<float> critFormula;
    [Display("附加效果")] public SkillEffect effect;                   // union → 类型列 + 前缀展开列
    [Display("下一级"), FormerName("upgradeTo")] public Ref<SkillConfig> nextRank;
    [Display("图标")] public UnityRef icon;
    [Display("掉落")] public WeightedList<DropEntry> drops;            // 自动子表 + __weight
    [Display("抗性衰减")] public Dictionary<DamageType, float> resistDecay = new();  // map 单 cell
}

public sealed class SkillRules : IRowValidator      // 行级业务校验
{
    public void Validate(object row, ref DiagnosticSink sink)
    {
        var s = (SkillConfig)row;
        if (s.damageType == DamageType.Frost && s.damage > 500)
            sink.Error("skill.frost_damage_cap", s);   // Error/Warning 为 sink 便捷方法,落 P§9 Diagnostic
    }
}
```

### 8.2 生成结构与策划填表

```text
dotnet run --project src/ExcelDb.Cli -- generate --schema samples/Game.Configs --workbooks "Assets/Configs/*.xlsx"
dotnet run --project src/ExcelDb.Cli -- convert  --schema samples/Game.Configs --workbooks "Assets/Configs/*.xlsx" --out ConfigData/config.bytes
```

生成后 `SkillConfig` sheet(节选;`__guid`/`__rev` 为隐藏伴随列):

```text
      A          B                    C            D                E        …  __guid                            __rev
行1   技能ID     显示名                伤害          伤害类型          法力消耗
行2   id         displayName          damage       damageType       cost.mp
行3   string     loc                  int [0..9999] enum DamageType  int
行4   fireball   skill.fireball.name  120          Fire             30          7c9e6679f4b04d3ab2c0e8d5f1a2b3c4  7
```

策划直接填行 4+:新行 `__guid` 留空(导入自动分配);`damageType`、引用列有下拉;改 `id` 后其他表引用该 key 的 cell 由导入自动改写(P§8.1)。

### 8.3 编辑器工具代码

```csharp
using ExcelDbEngine;
using ExcelDbEditor;

// 挂载与刷新
AssetDatabase.MountWorkbook("Assets/Configs/game.xlsx");
AssetDatabase.Refresh();

// 按路径加载
var fireball = AssetDatabase.LoadAssetAtPath<SkillConfig>(
    "Assets/Configs/game.xlsx/SkillConfig/fireball");

Console.WriteLine(fireball.name);
Console.WriteLine(fireball.GetInstanceID());

// SerializedObject 编辑与数组、引用赋值
var so = new SerializedObject(fireball);

Undo.RecordObject(fireball, "Buff Fireball");

so.FindProperty("damage").intValue = 150;
so.FindProperty("cost.mp").intValue = 35;

var ticks = so.FindProperty("tickTimes");
ticks.arraySize++;
ticks.GetArrayElementAtIndex(ticks.arraySize - 1).floatValue = 2.0f;

var next = so.FindProperty("nextRank");
next.objectReferenceValue = AssetDatabase.LoadAssetAtPath<SkillConfig>(
    "Assets/Configs/game.xlsx/SkillConfig/fireball_2");

so.ApplyModifiedProperties();

EditorUtility.SetDirty(fireball);
AssetDatabase.SaveAssets();               // 合并 → 补丁写回 → 复读 → 原子替换(P§6.5)

// 查找资产:类型过滤与 label 过滤
foreach (var guid in AssetDatabase.FindAssets("t:SkillConfig"))
{
    var path = AssetDatabase.GUIDToAssetPath(guid);
    var skill = AssetDatabase.LoadAssetAtPath<SkillConfig>(path);
    Console.WriteLine(skill.name);
}
foreach (var guid in AssetDatabase.FindAssets("l:boss"))
    Console.WriteLine(AssetDatabase.GUIDToAssetPath(guid));

// 创建和删除资产;name 即单字段 string key 的投影(P§7.1)
var skill2 = ScriptableObject.CreateInstance<SkillConfig>();
skill2.name = "ice_nova";
skill2.damage = 80;

AssetDatabase.CreateAsset(
    skill2,
    "Assets/Configs/game.xlsx/SkillConfig/ice_nova");
AssetDatabase.SaveAssets();

AssetDatabase.DeleteAsset(
    "Assets/Configs/game.xlsx/SkillConfig/ice_nova");

// 冲突处理:外部修改撞上本地 dirty 时(P§6.3)
var conflicts = new ConflictRecord[64];
AssetDatabase.GetConflicts(conflicts, out var conflictCount);
var written = Math.Min(conflicts.Length, conflictCount);
for (var i = 0; i < written; i++)
    AssetDatabase.ResolveConflict(conflicts[i].id, ConflictResolutionAction.ReloadFromExcel);
AssetDatabase.SaveAssets();
```

### 8.4 运行时代码(游戏侧)

```csharp
using ExcelDbEngine;

// 初始化(允许分配):打开 converted bytes,预热,预解析 key,预留帧内 buffer
RuntimeDatabase.Open(new ConvertedBytesDataSource("ConfigData/config.bytes"));

RuntimeDatabase.Prewarm();

RuntimeDatabase.TryGetAssetKey<SkillConfig>("fireball", out var fireballKey);

var skillBuffer = new SkillConfig[512];   // 初始化期分配;稳定帧内复用
var rng = new Rng(seed: 12345);

// 便捷读取(允许分配的路径)
var hero = RuntimeDatabase.LoadAsset<SkillConfig>("fireball");

// 订阅变化,重建派生缓存(与原计划 4.10 同形)
RuntimeDatabase.changed += static changeSet =>
{
    var events = changeSet.events;
    for (var i = 0; i < events.Count; i++)
    {
        ref readonly var evt = ref events[i];
        if (evt.runtimeType == typeof(SkillConfig))
            SkillRuntimeCache.Invalidate(evt.assetIdentity);
    }
};

// 稳定帧内(零分配,P§7.9 门禁覆盖)
if (RuntimeDatabase.TryGetAsset(fireballKey, out SkillConfig fireball))
{
    var dmg  = fireball.damageByLevel.Evaluate(level);                      // curve
    var crit = fireball.critFormula.Eval(
        new CritSymbols { baseDamage = dmg, crit = 0.35f });                // expression
    if (fireball.effect is DotEffect dot) ApplyDot(dot);                    // union
    fireball.resistDecay.TryGetValue(DamageType.Fire, out var decay);       // map
    var drop = fireball.drops.Select(ref rng);                              // weighted
    if (fireball.nextRank.TryGet(out var nextRank)) ShowHint(nextRank);     // 内部引用
    hud.Title = fireball.displayName.Resolve();                             // 本地化(interned)
}

var skillStatus = RuntimeDatabase.GetAssets<SkillConfig>(skillBuffer, out var skillCount);
var skillWritten = Math.Min(skillBuffer.Length, skillCount);
for (var i = 0; i < skillWritten; i++)
    SkillRuntime.Tick(skillBuffer[i]);
if (skillStatus == RuntimeQueryStatus.Truncated)
    SkillRuntimeCache.MarkCapacityInsufficient(skillCount);

// 开发期现场调数:切 Excel 源 → 开热载 → 改表即生效 → 切回 bytes 验证
RuntimeDatabase.SwitchDataSource(new ExcelDataSource("Assets/Configs/game.xlsx"));
RuntimeDatabase.EnableHotReload();
RuntimeDatabase.Refresh();
RuntimeDatabase.SwitchDataSource(new ConvertedBytesDataSource("ConfigData/config.bytes"));
```

### 8.5 Unity 日常操作序列

1. 菜单 `ExcelDB → Mount Workbook` 选中 xlsx;首次执行 `Generate` 生成表结构。
2. 策划改 Excel 保存 → watcher 自动 Refresh,Browser/inspector 即时更新;有本地 dirty 则弹冲突对话框。
3. Play Mode(项目 `developmentExcelSource: true`):运行中改 Excel 保存 → 热载,Game 视图即时生效。
4. 调数完成 → 菜单 `Convert` → `SwitchDataSource` 到 bytes 复验出包数据。
5. 出包:构建钩子自动 convert 进 `StreamingAssets/ExcelDb/`,Release 只读 bytes。

### 8.6 扩展点

```csharp
public sealed class ColorCodec : ICellCodec         // "#RRGGBB" ↔ Color32,cell 级自定义文法
{
    public string Id => "game.color"; public string Version => "1";
    public bool TryParse(ReadOnlySpan<char> cell, ref object value, ref DiagnosticSink sink)
    { /* 解析 #RRGGBB;失败 sink.Error("color.bad_format", …) 返回 false */ return true; }
    public string Write(object value) => $"#{((Color32)value).ToHex()}";
}

public sealed class FmodEventFamily : IReferenceFamily   // 自定义外部引用族
{
    public string Id => "fmod";
    public bool TryResolve(in RefToken token, out object target)
    { target = FmodBanks.FindEvent(token.Value); return target != null; }
    public Diagnostic? Validate(in RefToken token)
        => FmodBanks.Contains(token.Value) ? null : Diagnostics.Error("fmod.event_missing", token);
}

// 注册即代码(P§4.6),通常放宿主启动引导
SchemaRegistry.AddReferenceFamily(new FmodEventFamily());
SchemaRegistry.SetLocalizedTextProvider(new I2LocalizationProvider());
// 使用:[Codec(typeof(ColorCodec))] public Color32 tint;
```

## 附录 A. 荷载预算

计数规则同 P 附录 A:非荷载 = 标题、第 0 节与本附录;荷载 = 第 1-8 节。实测:荷载 408 行 / 总计 414 行 = 98.6%。
