# Unity-like ExcelDB 计划文档

## 0. 文档规范层级

本文仍是计划阶段文档，但计划阶段也必须像规格一样维护单一事实源。后续实现、评审和 AI 续写按以下层级理解本文：

1. `1.1 最终敲定需求索引` 定义不可裁剪的产品目标和硬边界。
2. `14.x 执行细化` 定义默认协议和冲突决策；任何 API、workflow、测试或任务清单都不得放宽这些默认协议。
3. `15. 完整目标版实现任务与验收` 定义完整目标版完成定义、conformance 归属和验收入口。
4. `13.2/13.3` 是原生需求的收束索引，只能索引已经落到 1.1/8/14/15 的规则；如果索引文字与权威章节冲突，以 1.1/8/14/15 为准并修正索引。
5. `2.x-12.x` 和 `13.1` 的工作流推演、现状差距、缺口审查和补丁痕迹是需求发现过程；其中出现“缺口”“最小闭环”“建议”“fallback”“旧实现”等语气时，只用于解释为什么需要 14.x/15.x 的原生协议，不构成完整目标版裁剪依据。

如果本文其他段落与上述层级冲突，应修正文档本身，而不是让实现者靠“以后文为准”临时解释。历史缺口可以保留为背景，但 active spec 必须只表达统一后的原生需求。

本文不再使用 product phase / release phase 表示对外能力裁剪；`phase` 仍可作为 operation transaction、validation pipeline、extension execution、report lifecycle 的流程字段。流程 phase 不能被解释为产品阶段，也不能用来裁剪完整目标版能力。

## 1. 宏观目标

ExcelDB 的目标不是做一个普通的“Excel 表格读取库”，而是把游戏配置当作 Unity-like assets 管起来：策划仍然用 Excel 高速迭代表格，程序和工具用 Unity-like API 读写对象，运行中的游戏可以在 Excel 源和 Excel converted bytes 源之间切换，并在不重启的情况下看到配置变化。首要接入目标是 Unity 2022.3，但 Unity 不是唯一接入目标；核心 schema、source、object/runtime 模型必须能被其他宿主复用。

宏观目标：

- Unity 2022.3 adapter 的 API 像素级贴近 Unity，类名、方法名、属性名尽量保持一致，仅通过 namespace 区分。
- 首要接入目标是 Unity 2022.3，并提供完整 Unity adapter。
- Unity 不是唯一接入目标，核心库不能依赖 UnityEngine / UnityEditor。
- Excel 是真正的存盘对象，而不是临时导入源或一次性中间文件。
- 一行配置是一个可加载、可引用、可编辑、可保存的 asset object。
- 支持 `Object`、`ScriptableObject`、`AssetDatabase`、`SerializedObject`、`SerializedProperty` 这一套编辑模型。
- 支持直接修改 Excel。
- 直接修改 Excel 的目的，是让策划和程序在日常调数时可以直接改表、保存、立刻看到效果，而不是被迫进入自定义编辑器。
- 直接修改 Excel 后，系统要能刷新、合并、验证、诊断，并把变化同步回已加载对象。
- 运行时支持 Excel 数据源，用于编辑器 Play Mode、开发包、调试包里的快速迭代。
- 运行时支持 Excel converted bytes 数据源，用于验证导出结果、性能测试和正式包。
- 支持 Excel 与 Excel converted bytes 在运行中热切换。
- 支持 Excel 修改热更新到运行中的数据，尽量保持已加载 object identity，让持有配置引用的系统看到新值。
- 运行时和编辑期尽量共用同一套读 API，差异集中在 data source、mutability policy 和 editor-only 操作。
- 高性能追求是宏观目标：core no-GC contract 覆盖 runtime hot path、source switch / hot reload commit、publish、editor core preflight 和 machine-readable report append；除初始化阶段和水位线增长以外，核心窗口默认不接受额外 GC allocation。xlsx/xml/zip 等 external backend、human report、Excel comment 和 UI projection 必须单独分账，不能冒充 core no-GC。

### 1.1 最终敲定需求索引

本节是完整目标版的目标索引，不是讨论清单。后文的推演、缺口和内部施工顺序必须围绕这些目标展开，不能把分析过程中的旧语气解释为降级或延期。

1. 平台接入目标。
   - 首要落地目标是 Unity 2022.3。
   - Unity adapter 必须完整覆盖编辑器接入、Play Mode 调数、Unity 资源引用、converted bytes 验证和正式包读取。
   - Unity 不是唯一接入目标；核心 schema、source、object/runtime、convert pipeline 不依赖 UnityEngine / UnityEditor。
   - 非 Unity 宿主可以复用同一套 schema、workbook metadata、converted bytes、runtime data source 和 reference family 扩展机制。

2. Unity-like API 是 Unity 接入的对外形态。
   - Unity 已有同构概念使用同名 API，仅 namespace 不同。
   - Unity 没有的 ExcelDB 概念使用统一的 ExcelDB-native 命名，例如 `RuntimeDatabase`、`ExcelDataSource`、`ConvertedBytesDataSource`。
   - 非 Unity 接入可以保留核心概念，也可以提供宿主自己的 facade；不能反向污染核心 contract。

3. Schema 是有效表结构唯一来源。
   - 字段名、字段类型、枚举、简单结构体、导出端、默认值、必填性、引用规则、校验、布局、editor capability 都由 schema 声明。
   - 简单结构体由 schema 决定写入单 cell，还是自动展开为多列。
   - Excel 表头中的结构信息、字段注释、枚举可选值和结构体展开说明是 schema 的落盘展示和诊断入口，不是另一份事实源。

4. Excel 是一等存盘对象和主 authoring surface。
   - 策划可以直接修改 Excel 来调数、加行、改备注。
   - 自定义 inspector 是效率工具，不是唯一入口。
   - Excel 修改必须能进入验证、合并、诊断、保存和运行中热更新流程。

5. Workbook 必须有原生分区和所有权。
   - schema-owned structure region 由 schema 生成和修正。
   - data region 保存正式数据行，生成结构时不得清空。
   - helper/freeform region 保存策划辅助信息，只要不影响导入导出就保留。
   - metadata region 保存身份、schema hash、field mapping、row revision 等系统信息。

6. 稳定身份必须来自 metadata。
   - workbook guid、table id、field id、row guid/local id 是身份基础。
   - key、sheet name、header text、asset path 是可变展示或定位信息，不作为最终身份。
   - 术语必须区分 `WorkbookGuid`、`RowGuid`、`AssetGuid` 和 `UnityGuid`：ExcelDB asset guid 由 workbook/table/row identity 派生；Unity `.meta` guid 只出现在 `UnityResourceRef` / Unity adapter 语境中。

7. 引用系统必须按 reference family 设计。
   - 内部资产引用、Unity asset 引用、插件外部引用分别有身份、校验、导出和诊断规则。
   - Unity asset 原生使用 `UnityResourceRef { guid, main_asset_path }`，其中 `guid` 是 Unity `.meta` guid / `UnityGuid`。
   - UnityGuid 是 Unity 资源引用身份，main asset path 是给人看的主资源路径；它们不得和 ExcelDB `AssetGuid` 混用。

8. Excel 与 converted bytes 是一等 data source。
   - 两者共享 schema hash、identity、reference graph 和读取 API。
   - `RuntimeDatabase` 必须支持 Excel 与 converted bytes 的事务式 open/switch/refresh。
   - 切源失败时保留旧 source 和旧 object graph。

9. 热更新必须保持运行中对象图的一致性。
   - 能 patch 的对象优先保持 object identity。
   - 无法 patch 时必须发出 added、removed、moved、renamed、recreated、property changed、dependency changed 等事件。
   - 业务缓存失效不能靠调用方猜。

10. 高性能和 GC 边界是核心需求。
   - 除初始化阶段和水位线增长以外，core runtime/editor/CLI operation 默认不接受额外 GC allocation；任何无法满足的后端、API 或流程必须显式进入 allocation scope report，并受 mode / CI / benchmark 策略约束。
   - 初始化阶段的 source open、首次批量导入、convert、schema generation 可以分配内存，但必须归因到 initialization 或 watermark growth。
   - 水位线增长可以分配内存；水位线指对象池、索引、事件缓冲、diff buffer、candidate snapshot、Excel/bytes parser workspace、source read buffer、临时工作区等达到历史最大容量时的扩容。
   - machine-readable report append 是 core operation 的一部分，达到水位线后不得靠拼接字符串、创建异常对象或临时集合记录诊断；human-readable report / UI 文本只是显式投影输出。
   - 达到水位线后的运行时热路径默认不得产生 GC allocation，包括 `LoadAsset`、引用解析、key/path 查找、遍历依赖、读取字段、source switch commit、hot reload patch 和 change event 分发。
   - 达到水位线后的编辑器核心 operation 默认不得产生额外 GC allocation，包括 workbook refresh no-op、layout preflight、dirty diff、conflict classify、machine-readable report append 和 save preflight；真正写 xlsx/package 的外部库分配必须进入 `external_backend` allocation scope，并被 backend capability 和 report 暴露。
   - `ExcelDataSource` 的重复 refresh/hot reload 必须复用 source parser workspace；如果某个 xlsx 后端无法保证，应限制在 Editor/Development debug 路径，并在 report 中明确标记 allocation，不作为正式 runtime backend。
   - 性能设计优先使用预分配、对象池、stable buffers、batch/coalesce 事件、增量索引和 generated accessor，避免 LINQ、闭包、装箱、临时字符串、反射热路径和 per-row/per-property 临时对象。

11. 所有危险操作都走 operation transaction/report。
   - 生成表结构、metadata flush、import、save、convert、source switch、hot reload 都必须先 analyze/diff/classify/report，再 commit。
   - safe、warning、error、blocker 的语义跨流程一致。
   - blocker 不写 workbook、不清空数据、不替换旧 runtime source。

12. 旧 `ConfigDatabase` 只能作为内部实现资产。
    - 可以复用其 transaction、undo、loader、dependency graph 能力。
    - 对外模型必须由 Object、SerializedObject、AssetDatabase、schema、workbook metadata 统一定义。
    - 新 API 不能泄漏旧 TableId/RowId 表模型作为常规使用前提。

## 2. 游戏开发视角的核心工作流

### 2.1 直接改 Excel 加速调数

策划或程序打开 `game.xlsx`，直接修改技能伤害、怪物血量、AI 参数，保存 Excel。运行中的 Editor Play Mode 或开发包收到文件变化，重新导入受影响的 workbook/sheet/row，验证数据，然后把变化 patch 到 resident objects。

关键点：

- 这是主工作流，不是 fallback。
- 不要求所有配置都通过自定义 inspector 修改。
- 修改成功后，游戏里已经持有 `SkillConfig`、`CharacterConfig` 引用的系统应尽量看到同一个对象实例的新字段值。
- 修改失败时保留旧数据，给出 workbook、sheet、row、property/cell 级诊断。

### 2.2 自定义编辑器改对象

复杂资产，比如行为树、掉落池、关卡波次，可以通过自定义编辑器编辑。编辑器层使用 `SerializedObject` / `SerializedProperty`，最终仍然写回 Excel。

关键点：

- 自定义编辑器是效率工具，不是唯一入口。
- Excel 和 editor inspector 修改同一个 source of truth。
- Undo、dirty、validation、SaveAssets 走统一生命周期。

### 2.3 运行中切换数据源

同一场运行可以先从 Excel converted bytes 启动，确认导出包表现；发现问题后切到 Excel 源进行现场调数；调好后重新 convert bytes，再切回 converted bytes 验证正式包数据。

关键点：

- Excel 源和 converted bytes 源必须产生同样的 asset identity 和引用关系。
- 切换数据源时尽量 patch 已加载对象，而不是要求业务系统全部重新 Load。
- 对新增、删除、key 变化、引用变化要有明确事件，让战斗、AI、UI、资源预加载等系统能重建自己的缓存。

### 2.4 正式运行时只读

正式包默认使用 Excel converted bytes。运行时 API 可以读对象、解析引用、遍历依赖，但不能把修改写回 source。热切换和 Excel 源可以保留给 Editor、开发包或受控 debug 环境。

## 3. 命名原则

采用方案：Unity 2022.3 adapter 和默认 C# facade 尽量使用完全一样的命名，仅 namespace 不一样。核心 schema/source/runtime contract 保持宿主无关，非 Unity 接入可以在不改变核心 contract 的前提下提供自己的 facade。

目标 namespace：

```csharp
using ExcelDbEngine;
using ExcelDbEditor;
```

目标类型名：

```csharp
Object
ScriptableObject

AssetDatabase
SerializedObject
SerializedProperty
Undo
EditorUtility
```

Unity adapter 不采用 `ExcelAssetDatabase`、`ExcelSerializedObject`、`ExcelUndo` 这种前缀风格。原因是宏观目标要求 Unity-like API，前缀式命名会让调用层一直暴露“这是另一个系统”的味道。

但 Unity 本身没有“运行时在 Excel 和 converted bytes 之间切换数据源”的现成 API。这里不强行伪装成 Unity 已有类型，而是作为 ExcelDB 的必要扩展处理：

- Unity 已有概念：保持同名，例如 `Object`、`ScriptableObject`、`AssetDatabase`、`SerializedObject`、`SerializedProperty`、`Undo`。
- Unity 没有的概念：保持统一风格，例如 `RuntimeDatabase`、`ExcelDataSource`、`ConvertedBytesDataSource`。
- 不混用半截前缀风格，例如不用 `ExcelAssetDatabase`，也不用把数据源切换硬塞进不合适的 Unity 类型里。
- 非 Unity 宿主不能要求核心库引入 Unity 类型；它们通过 adapter 暴露宿主习惯的 API，但继续共享 schema、metadata、source、convert 和 runtime identity。

## 4. 目标调用示范

### 4.1 挂载和刷新 Excel

```csharp
using ExcelDbEngine;
using ExcelDbEditor;

AssetDatabase.MountWorkbook("Assets/Configs/game.xlsx");
AssetDatabase.Refresh();
```

### 4.2 按路径加载资产

```csharp
var hero = AssetDatabase.LoadAssetAtPath<CharacterConfig>(
    "Assets/Configs/game.xlsx/CharacterConfig/hero");

Console.WriteLine(hero.name);
Console.WriteLine(hero.GetInstanceID());
```

### 4.3 查找资产

```csharp
var skillGuids = AssetDatabase.FindAssets("t:SkillConfig");

foreach (var guid in skillGuids)
{
    var path = AssetDatabase.GUIDToAssetPath(guid);
    var skill = AssetDatabase.LoadAssetAtPath<SkillConfig>(path);
    Console.WriteLine(skill.name);
}
```

### 4.4 SerializedObject 编辑

```csharp
var hero = AssetDatabase.LoadAssetAtPath<CharacterConfig>(
    "Assets/Configs/game.xlsx/CharacterConfig/hero");

var so = new SerializedObject(hero);

Undo.RecordObject(hero, "Change Hero HP");

var hp = so.FindProperty("hp");
hp.intValue = 150;

so.ApplyModifiedProperties();

EditorUtility.SetDirty(hero);
AssetDatabase.SaveAssets();
```

### 4.5 SerializedProperty 数组编辑

```csharp
var so = new SerializedObject(hero);

var skills = so.FindProperty("skills");
skills.arraySize++;

var item = skills.GetArrayElementAtIndex(skills.arraySize - 1);
item.objectReferenceValue = AssetDatabase.LoadAssetAtPath<SkillConfig>(
    "Assets/Configs/game.xlsx/SkillConfig/fireball");

so.ApplyModifiedProperties();
AssetDatabase.SaveAssets();
```

### 4.6 SerializedProperty 遍历

```csharp
var so = new SerializedObject(hero);
var iterator = so.GetIterator();

while (iterator.NextVisible(enterChildren: true))
{
    Console.WriteLine($"{iterator.propertyPath} : {iterator.propertyType}");
}
```

### 4.7 创建和删除资产

```csharp
var skill = ScriptableObject.CreateInstance<SkillConfig>();
skill.name = "ice_nova";
skill.Cost = 20;

AssetDatabase.CreateAsset(
    skill,
    "Assets/Configs/game.xlsx/SkillConfig/ice_nova");

AssetDatabase.SaveAssets();

AssetDatabase.DeleteAsset(
    "Assets/Configs/game.xlsx/SkillConfig/ice_nova");
```

### 4.8 Excel 外部修改后刷新

```csharp
AssetDatabase.Refresh();

var hero = AssetDatabase.LoadAssetAtPath<CharacterConfig>(
    "Assets/Configs/game.xlsx/CharacterConfig/hero");

Console.WriteLine(hero.Hp);
```

### 4.9 运行时数据源

```csharp
using ExcelDbEngine;

RuntimeDatabase.Open(new ConvertedBytesDataSource("ConfigData/config.bytes"));

var hero = RuntimeDatabase.LoadAsset<CharacterConfig>("hero");
var skill = RuntimeDatabase.LoadAsset<SkillConfig>("fireball");
```

调试场景可切换为 Excel 数据源：

```csharp
RuntimeDatabase.Open(new ExcelDataSource("Assets/Configs/game.xlsx"));
```

业务层读取 API 保持一致，数据源可替换。

运行中可以在 Excel 和 converted bytes 之间热切换：

```csharp
RuntimeDatabase.SwitchDataSource(new ExcelDataSource("Assets/Configs/game.xlsx"));
RuntimeDatabase.SwitchDataSource(new ConvertedBytesDataSource("ConfigData/config.bytes"));
```

Excel 文件被直接修改后，热更新运行中的 resident objects：

```csharp
RuntimeDatabase.EnableHotReload();
RuntimeDatabase.Refresh();
```

运行系统可以订阅变化，重建派生缓存：

```csharp
RuntimeDatabase.changed += changeSet =>
{
    foreach (var evt in changeSet.events)
    {
        if (evt.runtimeType == typeof(SkillConfig))
            SkillRuntimeCache.Invalidate(evt.assetIdentity);
    }
};
```

### 4.10 预热后的无 GC 读取

战斗、AI、UI 刷新这类高频路径不应每帧拼 key/path 字符串。默认做法是在场景或玩法初始化阶段解析 key、预留容量、预热索引，稳定帧内只使用 handle / caller-owned buffer：

```csharp
using ExcelDbEngine;

RuntimeDatabase.Open(new ConvertedBytesDataSource("ConfigData/config.bytes"));

RuntimeDatabase.Reserve(RuntimeCapacity.FromDescriptor("ConfigData/capacity.json"));

RuntimeDatabase.Prewarm();

RuntimeDatabase.TryGetAssetKey<SkillConfig>("fireball", out var fireballKey);

// 这是初始化期分配；稳定帧内复用这个数组。
var skillBuffer = new SkillConfig[512];
```

稳定帧内：

```csharp
if (RuntimeDatabase.TryGetAsset(fireballKey, out SkillConfig fireball))
{
    SkillRuntime.Use(fireball);
}

var skillStatus = RuntimeDatabase.GetAssets<SkillConfig>(skillBuffer, out var skillCount);
var skillWritten = Math.Min(skillBuffer.Length, skillCount);
for (var i = 0; i < skillWritten; i++)
{
    SkillRuntime.Tick(skillBuffer[i]);
}

if (skillStatus == RuntimeQueryStatus.Truncated)
{
    SkillRuntimeCache.MarkCapacityInsufficient(skillCount);
}
```

事件订阅也避免捕获闭包和分配式枚举：

```csharp
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
```

## 5. 现有实现与目标差距

当前实现已经具备一些基础能力：

- `ConfigDatabase.LoadAsset` 可读取 resident row。
- `IEditTransaction` 支持 draft、commit、undo、redo。
- `ITableLoader` 抽象了不同表数据源。
- `ExcelTableLoader` 支持 xlsx 读写。
- 有 RowRef、ExternalRef、依赖图、校验和 hot reload 基础。

但它当前更像“protobuf 行表数据库 + Excel loader”，还不是 Unity-like asset database。

历史缺口审查输入：

- 缺 `Object` / `ScriptableObject` 对象模型。
- 缺 `AssetDatabase` 级路径、guid、local id、dirty、refresh、import/save 生命周期。
- 缺 `SerializedObject` / `SerializedProperty` 的属性树、property path、数组编辑、property 级 diff。
- Excel 保存目前偏整 sheet 重写，不是保真 cell/property 级写回。
- Excel 外部修改没有完整 watcher、merge、conflict、diagnostic 流程。
- 运行时 converted bytes data source 和 export pipeline 还没有成型。
- 缺少 Excel 与 converted bytes 在运行中的热切换语义。
- 缺少 Excel 修改热更新到运行中 resident objects 的完整路径。
- 缺少对象变化事件和缓存失效协议，业务系统无法安全响应配置热更新。
- 缺少 Unity 专用外部资源引用结构；当前 `ExternalRef scheme:id` 不能同时表达可读主资源路径和稳定 guid。
- proto `layout` / `meta` 还没有进入真正的 inspector、Excel layout、validator 体系。

## 6. 目标架构

### 6.1 Engine 层

`ExcelDbEngine` 负责宿主无关的运行时对象和只读数据访问：

- `Object`
- `ScriptableObject`
- `RuntimeDatabase`
- `RowRef` / object reference bridge
- reference family registry
- converted bytes data source
- first-class ExcelDataSource，按 mode / OperationProfile 限制 authoring、debug、Development hot reload 和 Release 禁用边界

核心原则：

- 运行时默认只读。
- 不依赖 UnityEngine / UnityEditor。
- 除初始化阶段的 source open / 首次批量导入和水位线增长以外，运行时热路径默认不得产生 GC allocation。
- asset object 有稳定 identity。
- 热切换时尽量保留 object identity。
- 对象内容变化要能通知持有者重建派生缓存。
- 外部资源引用通过 reference family 扩展，不把某个宿主的资源系统写死进核心。
- 不让业务层直接依赖 Excel workbook 细节。

### 6.2 Editor 层

`ExcelDbEditor` 负责编辑期资产数据库和序列化编辑：

- `AssetDatabase`
- `SerializedObject`
- `SerializedProperty`
- `Undo`
- `EditorUtility`
- import/export/refresh/save
- validation diagnostic

核心原则：

- API 尽量贴 Unity。
- 所有编辑通过 `SerializedObject` 或 AssetDatabase 生命周期进入 dirty/undo/save。
- Excel 是一等存盘对象。
- 直接改 Excel 与 inspector 编辑都要回到同一套 import/validation/dirty 机制。

### 6.3 Source 层

数据源抽象分为：

- `ExcelDataSource`：编辑期、Play Mode 或受控 debug 环境中直接读取 / refresh Excel workbook 的 source implementation；是否允许写 Excel 由 mode、`OperationProfile`、xlsx backend capability、workbook write lease 和 operation transaction 共同决定，不能只因为 source kind 是 Excel 就默认可写。
- `ConvertedBytesDataSource`：运行时导出的 Excel converted bytes。
- `CompositeDataSource`：一等 layered source，支持基础包 + patch 包 + override / hotfix / development override。

Source 层只负责数据读写和变更通知，不负责高层编辑语义。

Source 层默认口径：

- source implementation 可以声明 read、refresh、watch、write backend、lease、no-GC boundary 等 capability。
- 高层写入仍必须通过 `AssetDatabase` / `SerializedObject` / schema generation / metadata flush / migration / auto-fix 等 operation transaction 触发。
- Development / runtime 中的 `ExcelDataSource` 默认是 read + refresh + hot reload source；Excel writeback 只有进入 editor-authoring bridge 或显式 debug authoring profile 时才允许。
- `ExcelDataSource` 具备写 backend capability，不等于当前 mode/profile 允许写 workbook。

Source 层需要支持运行中热切换：

- `ExcelDataSource` 与 `ConvertedBytesDataSource` 可以在同一个 `RuntimeDatabase` 中切换。
- 切换时保持已加载 object identity，尽量只更新对象内容。
- Excel 文件被外部修改后，可以将变更 hot reload 到运行中的 resident objects。
- 热切换失败时保留旧数据，并发出可诊断错误。

Source 层还要定义热更新结果：

- 修改：patch 已有 object，触发 object changed。
- 新增：创建新的 resident object，触发 asset added。
- 删除：把 object 标记为 missing 或 unloaded，触发 asset removed；不能让旧引用静默指向错误数据。
- key 变化：identity 不变，key index 更新，触发 asset moved/renamed 类事件。
- 引用变化：依赖图更新，触发相关缓存失效。

### 6.4 Platform adapter 层

Platform adapter 负责把宿主引擎/工具链接入核心 contract。第一目标是 Unity 2022.3 adapter，但 adapter 机制不能只服务 Unity。

Unity 2022.3 adapter 至少负责：

- 绑定 Unity Editor 菜单、import/refresh/save 生命周期和 Play Mode 调数入口。
- 提供 Unity-like `AssetDatabase`、`SerializedObject`、`SerializedProperty`、`Undo` 使用体验。
- 提供 `UnityResourceRef` reference family：guid 为身份，main asset path 为展示。
- 处理 Unity asset picker、guid/path 同步、asset move/rename 诊断。
- 将 converted bytes 集成进 Unity 构建、开发包和正式包读取流程。

非 Unity adapter 可以负责：

- 提供宿主自己的资源引用结构和 resource resolver。
- 提供宿主自己的编辑入口、命令行、Web 工具或服务器热更新入口。
- 复用 workbook metadata、schema compatibility、converted bytes、runtime source switch 和 hot reload 事务。

边界约束：

- Engine/schema/source 不直接引用 UnityEngine / UnityEditor。
- Unity 资源相关规则放在 Unity adapter 和 `UnityResourceRef` reference family 中。
- 新宿主不能修改核心语义来适配自己；应通过 reference family、source、editor facade、validator、converter extension 扩展。

## 7. Schema 契约能力审查

Schema 不是前置临时任务，而是全系统契约本体。`Object`、`AssetDatabase`、`SerializedObject`、Excel 生成、converted bytes、hot reload 和 no-GC runtime 都必须从同一个 schema descriptor 派生。Schema 决定：

- Excel 如何生成和升级。
- 一行数据如何成为 asset object。
- `SerializedObject` 如何生成 property tree。
- Excel cell 如何映射到 property。
- 引用如何校验、显示、热更新。
- converted bytes 如何导出和兼容。
- 编辑器结构与表结构不一致时如何迁移或降级。

如果 schema 能力不足，后面的 API 可以做得很像 Unity，但底层会在 rename、merge、热更新、出包时破。

### 7.1 Schema 控制有效表结构，Excel 保留非语义信息

游戏行业常见 Excel 里，经常会让策划主动填写多行表头：

- 中文名。
- 字段名。
- 字段类型。
- 导出端。
- 默认值。
- 是否必填。
- 引用目标。
- 校验规则。
- 枚举可选值。
- 简单结构体的写入/展开方式。
- 字段说明。
- 下拉选项。
- 表头注释。

在 ExcelDB 的目标模型里，**会影响导入、导出、运行时解释的表结构，总是由 schema 控制**。Excel 里的表头、类型、导出端、规则、引用目标等，可以展示给策划看，但它们不是第二份 schema。

同时，Excel 是策划日常工作的地方，不应该把所有手工信息都清掉。对于不会影响导入和导出的策划辅助信息，应给予宽松度并保留。

原则：

- Schema 是有效表结构的事实源。
- Excel 是 schema 的可编辑投影。
- 策划维护正式数据行，以及不会改变导入/导出语义的辅助信息。
- 程序维护 schema，不要求策划同步维护字段类型、导出端、约束、引用目标。
- 如果 Excel 的结构信息和 schema 不一致，先判断是否影响导入/导出。
- 影响导入/导出的不一致，报错或警告，并进入结构兼容/迁移流程。
- 不影响导入/导出的手工信息，保留，不因为重新生成表结构而删除。

Excel 中可以展示这些 schema 生成的信息：

```text
第1行：中文名      技能ID    名称      消耗      伤害      图标
第2行：字段名      key       name      cost      damage    icon
第3行：类型        string    string    int       int       unity_resource_ref
第4行：导出端      all       client    all       server    client
第5行：规则        required  required  >=0       >=0       UnityGuidRequired
第6行：说明        唯一ID    显示名    魔法消耗  基础伤害  UI图标
第7行：注释        不可重复  -         >=0      >=0      path 显示，guid 为身份
第8行以后：数据
```

其中第 1-7 行属于 schema 管辖的结构区域。生成表时只生成或修正这些结构区域中 schema 管辖的内容；正式数据行必须保留：

```text
fireball  火球术  10  100  Assets/UI/Icons/fire.png|8f3a...
ice_nova  冰环    20  80   Assets/UI/Icons/ice.png|2ab1...
```

有些列看起来像“策划信息”，但仍应来自 schema：

- 中文名来自 `display_name`。
- 类型来自 field type / value shape。
- 导出端来自 export policy。
- 必填、范围、正则来自 validators。
- 引用目标来自 ref schema。
- 下拉来自 enum/ref/object picker schema。
- 说明和表头注释来自 schema doc comment、field description 或 header comment。
- 枚举可选值来自 schema enum 定义，并写入表头注释和 data validation。
- 简单结构体的单 cell / 多列展开方式来自 schema value shape。

允许策划维护并保留的信息应明确分层：

- 数据值：例如 `cost = 10`、`damage = 100`。
- 业务备注：例如平衡性说明、临时 TODO，可以放在 schema 声明的备注列，也可以放在 schema 明确忽略的辅助区域。
- 状态标记：例如 disabled、review_state；这些必须是 schema 声明过的普通字段，而不是游离在结构外的隐式规则。
- 辅助展示：例如额外说明行、分组空列、冻结窗格、筛选、颜色、批注；只要不改变字段映射和导入/导出语义，应保留。

生成表时的约定：

- 只生成或修正 schema 管辖的表结构。
- 由 schema 生成表头注释；枚举字段必须能在表头注释中看到可选值。
- 不重建整张 sheet。
- 不清空正式数据行。
- 不删除 schema 不认识但不影响导入/导出的辅助行/列/样式/批注。
- 对会影响导入/导出的未知列、缺失列、类型冲突、导出端冲突、字段 id 冲突，报错或警告。
- 对不影响导入/导出的手工信息，保持宽松，原样保留。

示例：

- 策划在表头上方加一行“本周调整重点”，如果可由 layout metadata / table descriptor 识别为 pre-header helper/freeform row，且不影响字段映射，应保留。
- 策划加一列“临时备注”，如果 schema 标记该区域为 ignored helper column，应保留且不导出。
- 策划把 `cost` 字段的类型行从 `int` 改成 `string`，这会影响导入/导出，应按 schema 报错或修正。
- 策划删除 `damage` 列，影响导入，应报错或进入 migration。
- 策划调整列颜色、批注、筛选，不影响导入，应保留。

这条原则能避免“双重事实源”：一份事实在 `.proto/schema`，另一份事实在 Excel 表头。只要允许两边都手改类型、导出端、约束，最终一定会出现编辑器按一种结构读、策划按另一种结构填、converted bytes 又按第三种结构出的情况。

### 7.2 表结构生成的错误分级

由 schema 生成或修正 Excel 表结构时，系统必须先分类风险，再决定自动处理、警告还是报错。不能一律覆盖，也不能一律失败。

#### 7.2.1 安全：可自动处理，不需要打断流程

这些情况不改变导入/导出语义，可以自动保留或修正：

- 表头上方或表格外的策划说明行。
- schema 明确标记为 ignored/helper 的辅助列。
- 单元格样式、颜色、筛选、冻结窗格、批注。
- 不参与字段映射的空列、分组列。
- schema 生成行里的展示文本过期，例如中文名或说明旧了，但 field id 能匹配。
- 可默认的新 optional 字段缺列。
- 列顺序和 schema 不一致，但所有 field id/field name 都能唯一匹配。

预期处理：

- 保留辅助信息。
- 修正 schema 管辖的展示信息。
- 不清空正式数据行。
- 记录 info 级 generation report，方便审计。

#### 7.2.2 警告：可继续打开，但需要提示

这些情况可能影响团队理解或其他导出视图，但在当前 import 语义下仍可安全导入：

- unknown column 不在 ignored/helper 区域，但也不参与当前 schema 字段映射。
- 新增 optional 字段缺列，已使用 default value 补齐。
- 字段 header 文本改名，但可通过 field id 或 alias 匹配。
- sheet name 改了，但 metadata table id 可匹配。
- schema 行中的导出端/类型/规则被手改，但 metadata field id 和实际 schema 仍可恢复。
- helper 区域位置变化，但不影响数据区域识别。
- 旧 schema 的 deprecated 字段仍在 Excel 中，并且被 preservation policy 保留。

预期处理：

- workbook 可以打开。
- import 可以继续。
- convert bytes 是否允许继续，由 warning 的 mode/severity 决定。
- 在 generation report 中给出 workbook/sheet/row/column 定位和建议修复动作。

#### 7.2.3 阻断：必须报错，不能静默生成

这些情况会破坏导入、导出、identity 或数据安全：

- 缺少必须字段列，且没有 default/migration。
- key 字段缺失或 key 类型不兼容。
- field id 冲突：两个列声称是同一个字段。
- 同一个字段无法唯一匹配：header、alias、metadata 指向多个候选列。
- table id 冲突：多个 sheet 声称是同一个 table。
- row guid/local id 冲突，且无法判断哪行是复制行。
- 数据区域边界无法识别，生成操作可能覆盖正式数据行。
- 目标 sheet 被保护或文件锁定，无法写入必要 metadata。
- schema hash 不兼容，且无迁移路径。
- 字段类型发生不兼容变化，例如 string -> int 且现有数据无法转换。
- 引用目标策略变化导致现有引用无法校验，例如 fixed table 改成不兼容 group。
- converted bytes 生成所需字段有 blocker validation error。

预期处理：

- 不写回 workbook，或只写入安全的临时诊断，不修改结构。
- 不清空数据。
- 保留上一次成功 import snapshot。
- Editor/Runtime 继续使用旧数据。
- 报 error 或 convert blocker，要求用户修复或执行明确 migration。

#### 7.2.4 生成过程的基本策略

生成表结构时采用 dry-run + commit：

1. 读取 workbook 和 metadata。
2. 根据 schema 计算目标结构。
3. 生成 diff：要新增/修正哪些 schema 管辖 cell，要保留哪些辅助区域。
4. 对 diff 做风险分级。
5. 如果只有安全项，自动 commit。
6. 如果有 warning，commit 安全部分，并输出 report。
7. 如果有 blocker，不 commit 结构变更。

生成 report 至少包含：

- severity：info / warning / error / blocker。
- scope：workbook / sheet / column / row / cell / metadata。
- 是否影响 import。
- 是否影响 convert。
- 建议动作：保留、自动修正、手动修复、运行 migration。

### 7.3 既有 schema 实现能力（历史输入）

既有实现已经有一个很好的雏形，但它只作为历史输入和复用候选，不定义完整目标版边界：

- `TableOptions.kind`：区分 `ASSET` 和 `EMBEDDED`。
- `TableOptions.id`：稳定 table number。
- `TableOptions.implements`：声明 reference group。
- `FieldOptions.key`：声明业务 key。
- `FieldOptions.ref_table` / `ref_group`：约束 `RowRef` 目标。
- `FieldOptions.layout`：预留 Excel layout hint。
- `FieldOptions.meta`：预留扩展 metadata。
- `SchemaRegistry`：能注册 table、检查 table id/name 冲突、收集 key fields、校验 ref constraint。

这些能力是旧“表格行数据库”的基础，但还不足以支撑 Unity-like asset workflow；不能再把它们当作可交付边界。

### 7.4 Schema 必须表达的核心语义

本节描述 schema 必须原生表达的语义。下文的“旧实现差距”只解释现有实现离目标模型的距离，不参与完整目标版范围裁剪；如果发现本节与 14.x 默认协议不一致，应修正文档，而不是让实现者临时选择。

#### 7.4.1 Table identity 与生命周期

需要表达：

- stable table id。
- schema name。
- display name。
- sheet name override。
- table kind：asset / embedded / singleton / abstract group。
- owner module 或 package。
- table version。
- deprecated / hidden / editor-only / runtime-only。
- default asset path pattern。

原因：

- table id 是真正身份，sheet name 只是 Excel 展示。
- 游戏项目会 rename sheet、rename message、拆分配置模块。
- 有些表只用于编辑器，有些只进运行时 bytes。

旧实现差距（不得作为完整目标版裁剪依据）：

- 只有 table id 和 table name。
- sheet name 与 table name 强绑定。
- 没有 table version、display name、deprecation、runtime export policy。

#### 7.4.2 Field identity 与演化

需要表达：

- stable field id。
- current field name。
- old aliases。
- display name。
- field version。
- deprecated / reserved。
- rename migration。
- type migration。
- default value。
- required-like policy。
- nullable / allow empty。
- editor-only / runtime-only / export-only。

原因：

- Excel header 是人可改的，不应作为唯一字段身份。
- proto field number 可以作为基础 field id，但 Excel metadata 也要记录 field id。
- rename 是日常需求，不能每次都变成 missing column + unknown column。

旧实现差距（不得作为完整目标版裁剪依据）：

- 代码依赖 `FieldDescriptor.Name` / `JsonName` 找列。
- 没有 alias。
- 没有 required/default/nullability policy。
- 没有 field rename/type migration。
- 没有 old editor 对 unknown field 的 preservation 策略。

#### 7.4.3 Value shape 与 Excel 表达方式

需要表达每种字段如何在 Excel 中展开：

- scalar：单 cell。
- enum：由 schema 定义名称、数字、显示名、别名、下拉选项、导出表示。
- simple struct：由 schema 定义字段集合，并声明单 cell 写入或多 cell 自动展开。
- repeated scalar：单 cell 分隔、横向多列、纵向子表三种策略。
- repeated object/ref：顺序是否重要、是否允许重复、最大数量。
- map：是否支持，如何展开。
- oneof/union：类型列 + value columns。
- embedded message：JSON cell、展开列、独立子表。
- polymorphic payload：group ref、oneof、sub-asset 三种策略。

enum contract：

- enum 本身可以由 schema 声明，不要求依赖运行时代码里的 C# enum。
- 每个 enum value 至少有 stable value id / name，可选 display name、aliases、deprecated、description。
- Excel 显示值、导入值、导出值的关系必须明确，例如按 name、number 或 explicit export token。
- 表头注释必须列出可选值；当选项较多时可列出摘要，并指向 schema 生成的枚举说明区域。
- Excel data validation dropdown 应从 enum schema 生成，不由策划手工维护。

simple struct contract：

- simple struct 是可被 schema 展开的轻量 value object，例如 `Vector2`、`RangeInt`、`CurvePoint`、`WeightedItem`。
- schema 必须声明 `cell_mode`：`single_cell` 或 `expanded_columns`。
- `single_cell` 模式必须声明或使用默认稳定文本格式、分隔符/转义规则、示例值和 parse error 诊断；默认 codec 为 `exceldb.key_value_pairs.v1`。
- `expanded_columns` 模式必须声明子字段列名、顺序、display name、field id、默认值和校验。
- 展开后的子列仍属于同一个 logical field，metadata 需要能从任一子列追踪回父字段。
- 表头注释必须说明结构体格式；多列展开时父字段和子字段都要有注释。

原因：

- 游戏配置里数组、嵌套结构、行为树、掉落表非常常见。
- 统一用 JSON cell 虽然省事，但不利于策划直接编辑、校验和 diff。
- enum 和简单结构体是策划最常接触的非标量值，必须让可选值、填写格式和展开列在 Excel 里直接可见。

旧实现差距（不得作为完整目标版裁剪依据）：

- repeated 目前是分号拼接。
- enum 没有独立 schema contract，也没有表头注释/下拉生成规则。
- simple struct 没有声明式 contract，单 cell 与展开列策略没有统一入口。
- embedded message 目前偏 JSON。
- map 不支持。
- oneof/union、嵌套展开、子表策略未定义。

原生化落点：

- enum、simple struct、single-cell codec、child table、map、oneof/union、polymorphic payload、gameplay expression、weighted selection 和 table archetype preset 都必须进入 canonical `SchemaDescriptor`；不能由 importer、editor drawer 或 converter 各自解释。
- repeated simple struct、owned child object、行为树节点、掉落项和复杂条件组默认使用 child table + stable element identity；JSON cell 只能作为 schema 显式 opt-in 的 legacy/custom codec，并且不享受 property-level merge。
- 14.16 的 value shape 协议是本节缺口的完整目标版默认解法。

#### 7.4.4 Asset identity、key 与 path

需要表达：

- row guid / local id 是否由 schema 要求。
- key fields。
- key 是否允许 rename。
- key rename 是否触发 asset moved/renamed。
- path pattern，例如 `{workbook}/{table}/{key}`。
- display name field。
- sort/order field。
- duplicate key policy。

原因：

- Unity-like object identity 不能只依赖 key。
- key 是给人用的，row guid/local id 是给系统用的。
- 策划改 key 不应该让所有引用断掉。

旧实现差距（不得作为完整目标版裁剪依据）：

- `RowId` 是 table number + row int id，适合内存和当前表模型，但不够覆盖 Excel 复制行、跨 workbook、merge、key rename。
- 没有 row guid/local id metadata。
- key rename 与 path 变化没有事件模型。

#### 7.4.5 Reference schema

引用分两类：ExcelDB 内部资产引用，以及 Unity 外部资源引用。二者都要由 schema 声明，但身份规则不同。

内部资产引用需要表达：

- hard reference / soft reference。
- fixed table ref。
- group/interface ref。
- nullable ref。
- list ref 是否允许重复。
- ownership ref，例如 behavior tree owns nodes。
- delete policy：restrict / cascade / set null / leave missing。
- dependency kind：runtime preload / validation-only / editor-only。
- display strategy：显示 key、name、path、table+key。

Unity 外部资源引用不应再只用泛化 `ExternalRef { scheme, id }` 表达，而应定义 Unity 专用结构：

```proto
message UnityResourceRef {
  string guid = 1;
  string main_asset_path = 2;
}
```

约定：

- `guid` 是真实身份，用于导入、导出、依赖、运行时解析。
- `main_asset_path` 是给人看的主资源路径，用于 Excel 观察、搜索、诊断。
- 当 path 与 guid 指向的 Unity asset 不一致时，以 guid 为准。
- path 变化但 guid 不变，是安全 rename/move。
- guid 为空但 path 有值，可以尝试 editor-only 解析，并写回 guid；无法解析则报 warning/error。
- guid 指向的 Unity asset 不存在，是 missing external asset。
- 同一 guid 当前路径变化时，应刷新 path 展示，但不改变配置引用身份。

Excel 默认表达采用拆列：

```text
icon.path                     icon.guid
Assets/UI/Icons/fire.png      8f3a7c...
```

拆列更利于排序、筛选、肉眼检查和批量修复，也能避免 path 中出现分隔符时产生解析歧义。schema 必须声明哪部分是 display path，哪部分是 identity guid。

单 cell 表达只作为紧凑布局或 legacy adapter 的 schema opt-in，不是 UnityResourceRef 的默认形态：

```text
Assets/UI/Icons/fire.png|8f3a7c...
```

原因：

- 游戏配置引用不只是“指向另一行”。
- 删除技能、移动行为树节点、复制子树时，引用策略决定工具行为。
- Unity 资源路径容易因为移动/重命名变化，不能作为稳定身份。
- 策划需要看到路径才知道引用了什么，程序和运行时需要 guid 才能稳定解析。

旧实现差距（不得作为完整目标版裁剪依据）：

- 已有 fixed table 和 group ref。
- 没有 nullable、soft/hard、ownership、delete policy、dependency kind。
- 复制/删除/热更新时无法从 schema 里判断应 cascade 还是报错。
- 当前 `ExternalRef` 是 `scheme:id`，对 Unity 来说不够：缺少主资源路径作为可读展示，也没有明确 guid 优先规则。

原生化落点：

- `UnityResourceRef` 是 Unity 项目外部资源引用的一等结构，不作为后补兼容字段处理。
- Unity asset 字段必须由 schema 声明为 `UnityResourceRef`，并声明允许的 asset type、guid required policy、path display policy。
- 泛化 `ExternalRef` 只作为非 Unity 平台、插件扩展或 legacy adapter 使用，不是 Unity asset 的默认表达。
- 当前样例中的 `icon`、`vfx` 等资源字段应按原生模型声明为 `UnityResourceRef`。
- ResourceManager 的 external dependency key 对 Unity 应从 guid 生成，而不是从 path 生成。
- import、export、diagnostic、hot reload 都按 reference family 处理 Unity 资源引用，不在各流程里分别写特殊分支。

#### 7.4.6 Validation schema

需要表达：

- required。
- min/max。
- regex。
- string length。
- enum allow list。
- ref existence。
- ref group/table constraint。
- uniqueness。
- cross-field validation。
- cross-table validation。
- severity：info / warning / error / convert blocker。
- mode：editor / play mode / convert / release。
- custom validator id。

原因：

- 游戏配置允许编辑期 warning，但出包必须 blocker。
- 直接改 Excel 后要给策划明确 cell 级错误。

旧实现差距（不得作为完整目标版裁剪依据）：

- 目前主要校验 RowRef dangling 和 ref constraint。
- 没有通用 field validation schema。
- 没有 severity/mode 分级。
- 没有 custom validator contract。

#### 7.4.7 Excel layout schema

需要表达：

- sheet name。
- header row。
- header comment / note。
- enum options comment。
- metadata row/sheet。
- column order。
- hidden columns。
- frozen panes / filter policy。
- nested field 展开策略。
- simple struct single-cell / expanded-columns layout。
- unknown column preservation。
- comments/diagnostics 写回策略。
- data validation dropdown。
- protected range。

原因：

- Excel 是策划的日常编辑界面。
- layout 是产品体验，不是单纯 IO。

旧实现差距（不得作为完整目标版裁剪依据）：

- `layout` 只是字符串 hint，未形成 contract。
- 保存是整 sheet 重建，和 layout schema 目标相反。
- 没有 unknown column、样式、公式保留策略。
- 没有 schema-driven header comment 生成策略，枚举可选值和结构体填写格式无法在表头直接查看。

#### 7.4.8 SerializedProperty schema

需要表达：

- property path 生成规则。
- field display order。
- visible / hidden。
- readonly。
- tooltip。
- drawer type。
- group/category。
- array element label。
- array edit policy：append/delete/reorder 是否允许、reorder 是 stable identity 还是 whole-array rewrite。
- object picker target。
- multi-object edit policy。

原因：

- `SerializedObject` 不是简单反射字段；它要稳定地产生 inspector 和自定义 editor 可用的 property tree。
- Excel cell、property path、runtime field 三者需要可追踪映射。

旧实现差距（不得作为完整目标版裁剪依据）：

- 当前没有 property tree。
- 没有 drawer/visibility/readonly metadata。
- 没有 property path 到 Excel cell 的 mapping。

#### 7.4.9 Runtime export schema

需要表达：

- 是否导出到 converted bytes。
- runtime field strip。
- editor-only field strip。
- binary tag / schema hash。
- compatibility rule。
- default value materialization。
- index generation。
- preload dependency。

原因：

- converted bytes 不是把 Excel 原样换格式，它是运行时数据产品。
- 正式包要剔除 editor-only 字段，并校验 schema 兼容。

旧实现差距（不得作为完整目标版裁剪依据）：

- 没有 converted bytes schema contract。
- 没有 runtime-only/editor-only/export policy。
- 没有 schema compatibility rule。

#### 7.4.10 Custom editor capability schema

需要表达：

- editor id。
- supported schema/layout version。
- required fields。
- optional fields。
- migration id。
- fallback mode：read-only / table view / reject open。

原因：

- 行为树、掉落池、关卡波次这类自定义 editor 对表结构有强假设。
- 表结构变化时，editor 必须知道自己能不能打开，而不是运行到一半崩。

旧实现差距（不得作为完整目标版裁剪依据）：

- 自定义 editor 和 schema 没有 capability handshake。
- migration 目前靠业务代码临时处理，没有注册到 schema contract。

### 7.5 Schema 完整目标版闭环

完整目标版就是目标效果，不再把 schema 能力拆成“先定义 contract、以后再实现”的公开阶段。下面条目只要属于本文默认能力，就必须有 descriptor、hash、diagnostic、compatibility、migration gate、report、import/export/editor/runtime 行为和测试闭环。项目可以用 profile / extension permission 禁用某类能力，但那是项目策略，不是实现尚未完成。

1. Table contract：
   - table id。
   - schema name。
   - sheet name。
   - kind。
   - version。
   - export policy。
2. Field contract：
   - field id。
   - name。
   - display name。
   - header comment。
   - aliases。
   - type shape。
   - enum definition。
   - simple struct definition。
   - simple struct cell mode。
   - repeated/list layout、array edit policy 和 reorder semantics。
   - single-cell codec id/version、canonical writer、parser 和 merge granularity。
   - child table descriptor、element identity、order policy 和 owner field mapping。
   - map descriptor、oneof/union variant descriptor、polymorphic payload strategy。
   - common game table archetype / preset expansion；preset 必须展开为普通 field/reference/value shape。
   - default/required/nullability。
   - deprecated/reserved。
3. Asset identity contract：
   - row guid/local id。
   - key fields。
   - display name field。
   - key rename policy。
4. Reference contract：
   - fixed/group target。
   - nullable。
   - ownership/delete policy。
   - dependency kind。
   - UnityResourceRef：guid 为身份，main asset path 为展示。
5. Excel contract：
   - sheet mapping by table id。
   - column mapping by field id。
   - unknown column preservation。
   - metadata sheet format。
6. Validation contract：
   - built-in constraints。
   - severity。
   - mode。
   - custom validator id。
7. Runtime export contract：
   - include/strip policy。
   - schema hash。
   - compatibility rule。
8. Editor capability contract：
   - required schema/layout version。
   - migration/fallback policy。

### 7.6 Schema 声明方向

仍然可以以 proto option 为基础，但需要把 option 从“零散 hint”提升为“契约”：

```proto
message TableOptions {
  TableKind kind = 1;
  int32 id = 2;
  repeated string implements = 3;
  string display_name = 4;
  string sheet_name = 5;
  int32 version = 6;
  ExportPolicy export = 7;
}

message FieldOptions {
  int32 key = 1;
  string display_name = 2;
  repeated string aliases = 3;
  bool required = 4;
  string default_value = 5;
  string ref_table = 6;
  string ref_group = 7;
  RefDeletePolicy delete_policy = 8;
  FieldLayout layout = 9;
  repeated Validator validators = 10;
  ExportPolicy export = 11;
  string header_comment = 12;
  ValueShape value_shape = 13;
  EnumSchema enum_schema = 14;
  SimpleStructSchema simple_struct = 15;
}
```

真实实现不一定照这个 proto 字段命名，但需要覆盖这些语义，并统一生成 canonical `SchemaDescriptor`。后续 editor/importer/converter/runtime 只消费 descriptor 和 generated registry，不直接各自解释 proto option。

`ValueShape` 不是一个字符串 hint，只表达字段值的物理布局、canonical value、property path、merge granularity 和 runtime representation；enum、simple struct、single-cell codec、repeated/list、child table、map、oneof/union、polymorphic payload、expression、weighted selection 和 curve 必须有稳定 value shape descriptor。

reference family、label/search 和 preset expansion 是并列的 descriptor family，不能塞进 `ValueShape` enum。

### 7.7 Schema 设计的验收测试

第一步 schema 能力至少要用这些测试验收：

- 新建 workbook，metadata sheet 包含 table id、field id、schema hash。
- rename sheet 后仍能识别同一 table。
- rename field header 后能通过 field id 或 alias 迁移。
- enum 字段生成表头注释和 data validation dropdown，注释能看到可选值。
- simple struct 字段按 schema 指定写入单 cell 或展开为多列，并且表头注释说明填写格式。
- repeated simple struct / owned child object 默认生成 child table，child row metadata 含 owner identity、parent field id、element guid/local id 和 order index。
- map 以 child table key/value rows 表达，duplicate canonical key 产生稳定 validation diagnostic。
- oneof/union 以 stable variant id 作为 discriminator，未知 variant 或当前 variant 外 payload 非空产生稳定 diagnostic。
- `SkillConfig` 这类常见配置表 preset 展开后，canonical descriptor 中只能看到普通 field/reference/value shape，importer 不按 `enabled/tags/review_state` 列名猜语义。
- 旧编辑器保存含 unknown column 的 workbook，不删除 unknown column。
- 新 schema 增加 optional field，旧 workbook 可打开并补默认值。
- 新 schema 增加 required field，旧 workbook 可打开但 convert bytes 被 blocker 阻止。
- key rename 不改变 row guid。
- 删除被引用资产时，根据 delete policy 报错或 cascade。
- UnityResourceRef 的 path 变化但 guid 不变时引用保持有效，并刷新展示路径。
- UnityResourceRef 的 guid 缺失或找不到时给出 missing external asset 诊断。
- behavior tree editor 遇到缺字段时走 migration 或 read-only fallback。
- converted bytes schema hash 不兼容时，runtime open/switch 失败且保留旧 source。

## 8. 完整目标版交付计划与内部施工顺序

### 8.0 完整目标版边界

本文不再使用阶段编号表示对外裁剪后的产品范围。完整目标版就是最终目标效果：本文 14.x 默认协议、Unity-like API、Excel authoring、converted bytes、热切换、schema/value shape、report、compatibility 和 no-GC 边界如果被写为默认能力，就必须在完整目标版内完成实现和验收。

- `完整目标版`：本文描述的最终工作流效果。不能存在“只 lint/report、不开放 editor/runtime”的半成品公开能力。
- `内部里程碑`：工程施工顺序，只用于组织实现，不改变完整目标版对外能力边界。任何内部里程碑产物都不能被当作可交付版本，除非它已经满足完整目标版验收。
- `CompleteConformance`：用于证明完整目标版是否闭合的默认验收 profile，必须启用所有完整目标版默认能力；项目 profile 可以基于项目策略收窄可用性，但不能替代 conformance profile。
- `profile / extension opt-in`：项目策略或宿主能力选择。禁用能力时必须有稳定 diagnostic / read-only / build gate，但实现本身仍必须完整存在，不能把未实现伪装成 profile 禁用。
- `legacy/custom codec`：兼容或扩展入口，需要显式 schema / extension permission / report。它不能成为默认能力缺失时的临时兜底。

CompleteConformance 按宿主能力拆分验收，但不能拆分产品承诺：

| conformance profile | 必须证明的能力 |
| --- | --- |
| `CoreConformance` | schema descriptor、workbook metadata、canonical value、import/export/convert、converted bytes reader、RuntimeDatabase、source switch / hot reload transaction、diagnostic/report、no-GC core contract |
| `UnityEditorConformance` | Unity 2022.3 adapter、AssetDatabase、SerializedObject / SerializedProperty、Undo/dirty/SaveAssets、UnityResourceRef picker、Project/Inspector projection、Excel 非破坏写回 |
| `DevelopmentRuntimeConformance` | ExcelDataSource、ConvertedBytesDataSource、Excel/bytes 热切换、Development hot reload、debug source policy、old source preservation、runtime no-GC gate；必须至少有一个可通过 no-GC 验收的 ExcelDataSource backend / wrapper / pooled workspace |
| `ReleaseRuntimeConformance` | trusted converted bytes / runtime artifact package、Release read-only、manifest/bytes verify、runtime provider binding、禁止 Excel source/writeback/dynamic migration |

完整目标版能力必须落到 conformance profile，不能只停留在“任务清单提到过”：

| capability family | Core | UnityEditor | DevelopmentRuntime | ReleaseRuntime |
| --- | --- | --- | --- | --- |
| schema/value shape：enum、simple struct、child table、map、union、polymorphic payload、Expression、weighted selection、curve、label、preset | descriptor、hash、lint、import/export、compatibility、diagnostic | drawer/editor projection、表头注释、data validation、non-destructive writeback | source switch / hot reload patch、ChangeSet、no-GC read view | converted bytes verify/open、strip/export view、no-GC runtime accessor |
| reference family：internal ref、UnityResourceRef、LocalizedTextRef | identity、resolver contract、dependency graph、manifest/hash | internal picker、Unity ObjectField bridge、localized text editor/debug projection | provider binding、dependency_changed、hot reload old source preservation | runtime provider key、manifest verify、missing provider/build gate |
| editor model：AssetDatabase、SerializedObject、SerializedProperty、Undo、multi-object edit | core identity/status/report/query | Unity-like API、generic/custom inspector、multi-target all-or-nothing、conflict resolver | runtime read-only failure/report when editor APIs are unavailable | prohibited editor/writeback APIs produce stable failure |
| operation/report/profile/no-GC | transaction、machine-readable report、profile hash、allocation budget | SaveAssets/layout/migration/diagnostic projection, external backend allocation分账 | ExcelDataSource refresh/source switch/hot reload commit no-GC | signed artifact activation、Release read-only、no Excel source/writeback |

完整目标版默认支持的 first-class schema capability 分为不同 descriptor family；不能把 label、preset、reference family 都塞进同一个 value shape enum：

value shape：

```text
scalar
enum
simple struct single_cell
simple struct expanded_columns
repeated scalar single_cell
repeated scalar horizontal columns
child table for repeated complex / owned child
map via child table
oneof / union with stable variant id
schema-discriminated polymorphic payload
gameplay expression / condition
weighted selection
curve
```

reference family / localization contract：

```text
internal object reference
UnityResourceRef
LocalizedTextRef
```

AssetDatabase search / label capability：

```text
label descriptor
```

schema authoring preset：

```text
game table archetype / preset expansion
```

完整目标版默认不把这些作为隐式兜底；需要时必须通过明确 schema / extension opt-in，并拥有完整 diagnostic / migration / report：

```text
arbitrary JSON / custom codec
multi-dimensional array
external localization provider / advanced formatter grammar
```

没有 schema / extension opt-in 的高级表达不能退化成普通字符串、JSON、Excel formula 或业务代码临时解释；必须按 `schema.value_shape_invalid`、extension/profile diagnostic 或 build gate 处理。

### 内部里程碑 1：Schema contract 与原生协议闭环

目标：

- 扩展 `exceldb/options.proto`，把 table/field/reference/layout/validation/export/editor capability 从 hint 升级为 contract。
- 定义 canonical `SchemaDescriptor`、generated C# type binding 和 generated registry bootstrap，避免 editor/importer/converter/runtime 各自解释 schema。
- 定义 canonical descriptor bytes、descriptor_hash/schema_hash/layout_hash/codegen_hash 和 binding manifest 的 deterministic 生成规则。
- 定义 workbook region ownership：schema-owned structure、data、helper/freeform、metadata 的边界和写入规则。
- 建立 schema hash、table id、field id、row guid/local id 的 metadata 规范。
- 定义 missing column、unknown column、field rename、table rename、type change 的兼容策略。
- 定义 schema migration registry、MigrationPlan、dry-run/apply/verify/history 协议。
- 定义 required/default/nullability、validation severity、convert blocker。
- 定义 enum 与 simple struct contract：enum 可选值、显示/导出值、simple struct 单 cell / 多列展开。
- 定义 reference family：内部资产引用、Unity asset 引用、插件外部引用分别有身份和诊断规则。
- 定义 Unity 2022.3 adapter 的 `UnityResourceRef` contract：guid 为身份，main_asset_path 为展示，并明确 resolver、sub-asset opt-in、runtime provider、build dependency manifest。
- 定义 platform adapter boundary：Unity 2022.3 adapter 是首个实现，核心 schema/source/runtime 不依赖 UnityEngine / UnityEditor。
- 定义 Excel layout 与 unknown preservation 的最低协议。
- 定义由 schema 生成的 Excel 结构行和表头注释：中文名、字段名、类型、导出端、规则、说明、枚举可选值、结构体填写格式。
- 定义 schema 管辖区域和策划辅助区域：前者由 schema 生成/校验，后者在不影响导入导出时保留。
- 定义 operation transaction/report：表结构生成、metadata flush、import、save、convert、source switch、hot reload 都要能 dry-run、风险分级和原子提交。
- 定义 Excel raw cell 到 canonical value 的 parse / normalize 协议，覆盖 blank、string、number、bool、enum、reference、date/time、formula、error cell。
- 定义 converted bytes schema compatibility rule。
- 定义 editor capability / drawer registry contract：复杂编辑器必须先能力握手，再通过 SerializedObject 写入。
- 实现 map、oneof/union、polymorphic payload、expression、weighted selection、curve、label 和 preset 的 descriptor、lint、diagnostic、compatibility、editor/import/export/runtime gate；这些能力属于完整目标版目标效果，不能只停在 descriptor 描述层。

验收：

- 新建 workbook 时生成 metadata sheet。
- 新建 workbook 时生成 schema 驱动的表头结构行和表头注释，策划不需要手填字段名/类型/导出端/规则/枚举可选值。
- enum 字段的表头注释和 data validation dropdown 由 schema 生成。
- simple struct 字段根据 schema 以单 cell 或多列展开生成表头和 metadata。
- 重新生成表结构时只修正 schema 管辖结构，不清空正式数据行。
- 不影响导入/导出的策划辅助行/列/样式/批注被保留。
- 影响导入/导出的结构不一致会报错或警告。
- blocker 级结构问题不会写回 workbook，也不会清空数据。
- generation report 能区分 safe / warning / error / blocker。
- Excel raw cell parse 对 blank、string、number、bool、enum、reference、date/time、formula、error cell 有 deterministic normalized value 和 diagnostic。
- rename sheet 不丢 table。
- rename field 可通过 field id/alias 迁移。
- Unity resource path rename 不改变引用身份，guid 缺失/无效会产生诊断。
- UnityResourceRef guid/path mismatch 以 guid 为准，Unity build dependency manifest 能列出 guid、main_asset_path、asset type、runtime provider/key 和 referrer。
- 核心 Engine/schema/source 能在没有 Unity assemblies 的环境下成立；Unity 2022.3 相关依赖只出现在 Unity adapter。
- 同一 schema source 在不同机器生成相同 descriptor_hash/schema_hash/layout_hash；reserved id 复用会被 schema-lint 阻止。
- 旧编辑器保存 unknown column 不丢数据。
- 新 schema 加 optional field 可打开旧表并补默认。
- 新 schema 加 required field 可打开旧表，但 convert bytes 被 blocker 阻止。
- converted bytes schema hash 不兼容时 runtime open/switch 失败且保留旧 source。
- migration registry 可 lint；migration dry-run 不写 workbook；migration apply 成功后写 history 并可幂等 skip。

### 内部里程碑 2：补齐 Object 和 Asset identity

目标：

- 引入 `ExcelDbEngine.Object`。
- 引入 `ExcelDbEngine.ScriptableObject`。
- 给 row asset 建立稳定 `guid/path/localId`。
- 让现有 row resident 实例被 object wrapper 或 object base class 承载。
- 统一 object equality、instance id、name。

验收：

- 可以通过 `AssetDatabase.LoadAssetAtPath<T>()` 加载 row asset。
- 同一个 asset 重复加载返回稳定 resident object。
- asset path 和 guid 可互相转换。

### 内部里程碑 3：建立 AssetDatabase API 边界

目标：

- 新增 `ExcelDbEditor.AssetDatabase`。
- 支持 `MountWorkbook`、`Refresh`、`SaveAssets`。
- 支持 `FindAssets`、`LoadAssetAtPath`、`LoadAllAssetsAtPath`。
- 支持 `CreateAsset`、`DeleteAsset`。
- 将当前 `ConfigDatabase` 的 load/edit/save 能力放入内部适配层。

验收：

- API 调用形态接近 Unity。
- 当前 CLI/sample 可以逐步迁移到 `AssetDatabase` API。
- 不再要求上层直接操作 `TableId`、`RowId` 才能完成常规资产操作。

### 内部里程碑 4：SerializedObject / SerializedProperty

目标：

- 新增 `SerializedObject`。
- 新增 `SerializedProperty`。
- 支持 `FindProperty`、`GetIterator`、`NextVisible`。
- 支持 primitive、enum、string、simple struct、object reference。
- 支持 repeated/list 的 `arraySize`、`GetArrayElementAtIndex`、insert/delete/move。
- `ApplyModifiedProperties` 接入 transaction、undo、dirty。

验收：

- 能用 property path 修改任意支持字段。
- 数组引用编辑可工作。
- undo/redo 可以按一次 `ApplyModifiedProperties` 为一个编辑单元。
- 现有 direct draft edit 只作为底层事务能力保留；常规对外编辑 API 统一走 `SerializedObject`。

### 内部里程碑 5：Excel 一等存盘对象

目标：

- 将 workbook 视为 asset source。
- sheet/table/row 有稳定映射。
- 增加隐藏 metadata sheet，保存 schema hash、asset guid、local id、import state。
- 从整 sheet 重写升级为尽量保真写回。
- 支持 column layout、field layout、嵌套对象展开策略。

验收：

- 手工修改 Excel 后 `AssetDatabase.Refresh()` 能稳定更新对象。
- 保存时尽量保留样式、公式、冻结窗格、筛选等非数据内容。
- property 能定位到对应 cell/range，错误能回写到 Excel 诊断位置。

### 内部里程碑 6：外部 Excel 修改同步

目标：

- 文件 watcher + debounce。
- 外部修改检测。
- 内存 dirty 与外部修改冲突处理。
- reload merge 保持 object identity。
- validation diagnostic 可指向 asset/property/cell。
- 把“直接修改 Excel 加速日常调数”作为主工作流支持。
- 支持 Excel 修改热更新到 Editor Play Mode 或开发包中的 resident objects。
- 提供 object/table/asset 级变化事件，支持业务系统重建缓存。
- 定义 watcher/background import 与 owner thread publish point，避免运行中对象图在帧内被半更新。

验收：

- 外部 Excel 编辑并保存后，已加载对象刷新为新值。
- dirty object 冲突时不静默覆盖。
- 诊断信息能指出 workbook、sheet、row、property。
- 运行中的 `ExcelDataSource` 可以接收 Excel 修改并热更新 resident objects。
- 持有旧 `SkillConfig` 引用的运行系统能在不重新 `LoadAsset` 的情况下读到新字段值。

### 内部里程碑 7：运行时导出数据源

目标：

- 新增 export/bake pipeline。
- 新增 `ConvertedBytesDataSource`。
- 定义 converted bytes section layout、schema manifest、runtime index 和 reader verify/open 协议。
- 支持 schema hash/version 校验。
- 支持按类型和 key 加载。
- 支持引用解析和依赖预加载。
- 支持 `ExcelDataSource` 与 `ConvertedBytesDataSource` 热切换。
- 支持 converted bytes 更新后热切换到运行中的数据。
- 支持命令行 `schema-lint`、`workbook-check`、`convert`、`verify-bytes` 和 artifact manifest。
- 支持 Unity 2022.3 build 前 convert gate，Release build 默认只打包 converted bytes。
- 定义新增、删除、修改、key 变化、引用变化的热切换事件。
- 定义 runtime GC budget：稳定水位线后的读取、查找、引用解析、source switch commit、hot reload patch、事件分发默认不得产生 GC allocation。
- 定义 runtime operation queue / publish point，source switch 与 hot reload commit 不与读取遍历交错。

验收：

- 编辑期 Excel 数据和运行时 converted bytes 数据读取结果一致。
- 业务层可从 `ExcelDataSource` 切到 `ConvertedBytesDataSource`，读取 API 基本不变。
- runtime 默认只读，修改 API 不可用或明确失败。
- 热切换后已加载对象尽量保持 identity，不要求业务层重新获取所有引用。
- 切回 converted bytes 后能验证正式包数据与 Excel 调试数据是否一致。
- CI/Build 能通过 manifest 追踪 schema_hash、export_view_hash、source_set_hash、preload_plan_hash、bytes_hash、input_workbooks、dependency digest 和 Unity resource dependencies。
- 在已完成初始化和水位线预热后，常规 `LoadAsset`、handle lookup、引用解析、key lookup、依赖遍历、ExcelDataSource refresh commit、converted bytes read 和 hot reload patch 的测试不产生 GC allocation。

### 内部里程碑 8：扩展体系

目标：

- validator 注册。
- property drawer 注册。
- Excel layout planner。
- schema migration。
- custom metadata 使用规范。
- extension permission / external tool sandbox。

验收：

- 项目可为字段定义自定义校验和编辑展示。
- schema 变更后可迁移已有 Excel。
- `layout` / `meta` 从 proto option 真正参与编辑和导入导出。
- 自定义 extension 的文件、Unity、网络、环境变量和外部工具权限能被 schema-lint、CI、report 和 profile 一致治理。

## 9. 关键设计约束

- 公开 API 优先 Unity-like，不让底层表结构污染常规调用。
- 不破坏已有 `ConfigDatabase` 的事务、undo、依赖图和 loader 基础能力。
- 编辑期允许 mutable，运行时默认 read-only。
- Excel 是 source of truth，但保存必须避免无意义破坏用户工作簿内容。
- 直接修改 Excel 是一等工作流，用于加速游戏配置调数，并且必须能同步回数据库对象和运行中对象。
- Excel 与 Excel converted bytes 都是一等数据源，运行中需要支持热切换。
- 热切换优先保持 object identity；无法保持时必须显式发出删除/重建事件。
- 所有引用最终都要有稳定 identity，不依赖易变行号或显示名。
- 所有写操作都要经过 dirty/undo/save 生命周期。
- 除初始化阶段的 source open / 首次批量导入和水位线增长外，运行时稳定热路径默认不得产生 GC allocation；machine-readable report append 属于 core operation，human-readable 导入报告属于 projection，必须分账。

## 10. 工作流模拟审查

这一节假设目标功能已经完成，用游戏开发中的真实使用方式做桌面推演。推演重点不是 API 是否好看，而是状态变化、冲突处理和热更新边界是否闭合。

### 10.1 推演约定

参与对象：

- `game.xlsx`：配置 workbook，也是 source asset。
- `CharacterConfig`、`SkillConfig`、`BehaviorTreeConfig`：普通配置资产。
- `BehaviorNodeConfig`：行为树节点资产，常由自定义编辑器管理。
- `ConvertedBytesDataSource`：由 Excel convert 出来的运行时 bytes。
- `RuntimeDatabase`：运行时 resident objects、key index、依赖图、热更新事件的持有者。

每个 workbook 至少需要隐藏 metadata：

- workbook guid。
- schema hash / schema version。
- table id 到 sheet 的映射。
- row guid / local id。
- row revision 或 content hash。
- 上次成功 import 的 source fingerprint。
- converted bytes 对应的 source hash。

没有这些 metadata，后面的 rename、copy row、热切换和三方 merge 都会很脆。

### 10.2 从 0 创建配置

操作：

1. 程序定义配置 schema，生成 C# 类型和描述信息。
2. 编辑器调用 `AssetDatabase.MountWorkbook("Assets/Configs/game.xlsx")`，文件不存在。
3. 用户创建 workbook。

预期变化：

- `game.xlsx` 被创建。
- 每个 asset table 生成对应 sheet。
- 每个 sheet 有稳定 header。
- 隐藏 metadata sheet 写入 workbook guid、schema hash、table id。
- `AssetDatabase` 产生 source added / imported 事件。
- 此时没有 row assets。

审查点：

- 创建 workbook 不能只生成可读表格，还必须生成 merge 和 identity 需要的隐藏 metadata。
- sheet 名可以改，但 table id 不能丢；否则重命名 sheet 会导致资产丢失。

### 10.3 从编辑器创建资产

操作：

```csharp
var skill = ScriptableObject.CreateInstance<SkillConfig>();
skill.name = "fireball";
skill.Cost = 10;

AssetDatabase.CreateAsset(
    skill,
    "Assets/Configs/game.xlsx/SkillConfig/fireball");

AssetDatabase.SaveAssets();
```

预期变化：

- 创建 resident `SkillConfig` object。
- 分配 stable row guid / local id。
- 写入 `SkillConfig` sheet 的数据行。
- 更新 key index：`SkillConfig/fireball -> row guid`。
- 更新 dependency graph。
- 标记 workbook dirty。
- `SaveAssets` 后 Excel 文件落盘，metadata 同步保存。

审查点：

- `name`、业务 key、asset path 之间要有明确关系。
- 如果 key 后续改变，object identity 不变，asset path 可以变化。
- `CreateAsset` 必须经过 undo/dirty/save 生命周期，而不是直接插行。

### 10.4 从 Excel 直接创建资产

操作：

1. 策划打开 `game.xlsx`。
2. 在 `SkillConfig` sheet 新增一行：`key = ice_nova`，`cost = 20`。
3. 保存 Excel。
4. 编辑器或运行中的开发包触发 refresh/hot reload。

预期变化：

- importer 发现新行。
- 如果 row guid/local id 缺失，系统生成 session-local `temporary_imported` identity，并把 workbook 标记为 metadata dirty。
- 在 editor table/status/diagnostics 中显示该行；如果 Excel 当前可写，metadata 会在 `FlushMetadata()`、`SaveAssets()` 或 profile 允许的 auto metadata flush 时写回 workbook。
- metadata flush 成功后创建有效 resident `SkillConfig` asset，触发 asset added / object changed，更新 key index、guid index 和 dependency graph。
- metadata flush 失败时不把该行加入常规 `FindAssets` / guid / runtime asset set，只保留 workbook diagnostic 和 temporary preview。

审查点：

- 直接加行是主工作流，不能要求用户先去自定义编辑器创建对象。
- 新行没有 metadata 是正常情况，不应该失败；但系统必须尽快把 identity 写回 Excel。
- 如果 Excel 被锁定导致 metadata 写不回，允许本轮 session 使用临时 identity，但要给出诊断，避免下次 reload 变成另一个对象。

### 10.5 运行时从 converted bytes 启动

操作：

```csharp
RuntimeDatabase.Open(new ConvertedBytesDataSource("ConfigData/config.bytes"));

var fireball = RuntimeDatabase.LoadAsset<SkillConfig>("fireball");
```

预期变化：

- runtime 校验 converted bytes 的 schema hash。
- 加载 resident objects。
- 建立 key index、row guid index、依赖图。
- runtime 默认 read-only。

审查点：

- converted bytes 不是另一套身份系统，它必须保留 Excel 源里的 row guid/local id。
- 正式包里路径、key、引用解析结果要和 Excel 源一致。

### 10.6 运行中切到 Excel 调数

操作：

```csharp
RuntimeDatabase.SwitchDataSource(new ExcelDataSource("Assets/Configs/game.xlsx"));
RuntimeDatabase.EnableHotReload();
```

预期变化：

- runtime 读取 Excel snapshot。
- 对比当前 converted bytes snapshot。
- row guid 相同的对象尽量 patch 原 resident object。
- 新增行触发 asset added。
- 删除行触发 asset removed。
- 字段变化触发 object changed。
- key 变化更新 key index，并触发 asset moved/renamed 类事件。

审查点：

- 切源是一个事务：要么整批切换成功，要么保留旧数据源。
- 不能切到一半导致一部分系统读新数据、一部分系统读旧数据。
- 热切换后，持有 `fireball` 引用的战斗系统应该读到同一个 object 的新值。

### 10.7 直接改 Excel 热更新运行中对象

操作：

1. 战斗正在运行，`fireball.Cost == 10`。
2. 策划把 Excel 里的 `fireball.cost` 改成 `12`。
3. 保存 Excel。

预期变化：

- watcher debounce 后触发 import。
- importer 只解析受影响 workbook/sheet，生成 candidate snapshot。
- validation 通过。
- `RuntimeDatabase` patch 原 `fireball` object。
- `fireball.Cost == 12`。
- 触发 object changed。
- 战斗、AI、UI、资源预加载等系统收到事件后重建派生缓存。

审查点：

- 热更新不是只改内存字段，还要更新索引、依赖图和运行系统缓存。
- 如果字段格式错误，例如 `cost = abc`，本次 import 失败，旧 `fireball.Cost == 10` 必须保留。
- 错误要定位到 workbook/sheet/row/property/cell。

### 10.8 编辑器 dirty 与 Excel 外部修改冲突

场景：

1. 自定义 inspector 打开 `fireball`。
2. inspector 里把 `cost` 从 `10` 改成 `11`，尚未保存。
3. 策划同时在 Excel 里把 `cost` 改成 `12` 并保存。
4. 编辑器收到 Excel refresh。

预期变化：

- 系统发现同一个 object、同一个 property 同时被两边修改。
- 使用三方信息判断冲突：
  - base snapshot：`cost = 10`
  - editor draft：`cost = 11`
  - excel snapshot：`cost = 12`
- 这是同字段冲突，不能自动覆盖。
- resident object 保持当前安全状态。
- inspector 显示 conflict diagnostic。
- 用户可以选择：
  - reload from Excel：丢弃 editor draft，使用 `12`。
  - keep editor value：保留 `11`，下次 SaveAssets 写回 Excel。
  - manual resolve：打开 diff，手动决定。

非冲突场景：

- inspector 改 `cost`，Excel 改 `description`。
- 系统可以自动 merge，最终两边修改都保留。

审查点：

- 需要三方 merge，不是简单比文件时间戳。
- `SaveAssets` 必须检查 workbook source revision；如果 Excel 已外部修改且未 resolve，拒绝静默覆盖。
- `SerializedObject.ApplyModifiedProperties()` 也要检查 target revision；如果 target 已被外部刷新，旧 property snapshot 不能无脑 apply。

### 10.9 编辑器结构与 Excel 表结构不一致

这是最容易出事的地方。这里的“编辑器结构”包括 generated C# 类型、SerializedProperty path、自定义 editor 对字段/布局的假设；“表结构”包括 Excel header、metadata、schema hash、隐藏 layout 信息。

#### 10.9.1 Excel 少了编辑器需要的字段

例子：

- 新版 `SkillConfig` 增加字段 `cooldown`。
- 编辑器已更新，Excel 仍然是旧表，没有 `cooldown` 列。

预期处理：

- importer 根据 schema metadata 判断这是新增字段。
- 如果字段可默认，自动使用 default value，并把 workbook 标记为 structure dirty。
- 下一次保存或 migration 时补列。
- 如果字段是 key、required-like、或被 validator 标记为必须显式填写，则 import 成功但 validation error，相关资产不可 convert。

审查点：

- 缺列不应该直接让整个 workbook 无法打开。
- 但正式 convert bytes 必须更严格，不能把缺必填字段的数据带进包。

#### 10.9.2 Excel 有编辑器不认识的字段

例子：

- 分支 A 加了 `mana_cost` 列。
- 当前编辑器仍是旧代码，不认识这个字段。

预期处理：

- importer 保留 unknown column。
- 旧编辑器可以读已知字段，但保存时不能删除 unknown column。
- 如果用户只改已知字段，写回必须保持 unknown column 原样。
- convert bytes 时如果 schema hash 不匹配，默认失败，提示需要更新代码或执行兼容导出策略。

审查点：

- 不能因为旧编辑器保存一次就把 schema 新字段删掉。
- Excel writer 必须支持保真写回和 unknown column preservation。

#### 10.9.3 字段 rename

例子：

- `cost` 改名为 `mana_cost`。
- proto field number 或稳定 field id 没变。

预期处理：

- importer 通过 field id / metadata 识别这是 rename。
- 自动迁移 header 或提示迁移。
- `SerializedProperty.FindProperty("mana_cost")` 能找到新字段。
- 旧 editor / drawer 如果还调用 `FindProperty("cost")`，必须依赖 schema 声明的 `property_aliases=["cost"]`；没有 alias 时返回 null，不按相似名字或旧 header 猜测。
- 旧 header 不应被当作 unknown column 和 missing column 的双重错误。

审查点：

- 只靠 header 文本不够，必须有 field id 级 metadata。

#### 10.9.4 table/sheet rename

例子：

- `SkillConfig` sheet 被用户改名为 `Skills`。

预期处理：

- 通过 metadata table id 找回 sheet。
- AssetDatabase path 可以继续使用 schema name，也可以记录 display sheet name。
- 如果 metadata 丢失，只能退化为启发式匹配，并产生诊断。

审查点：

- table id 是身份，sheet name 是显示和编辑便利。

#### 10.9.5 自定义编辑器结构变化

例子：

- 行为树编辑器新版要求 `BehaviorNodeConfig.owner_tree` 字段。
- 老 Excel 没有该列。

预期处理：

- 打开行为树 editor 前先运行 migration check。
- 可自动迁移时，生成 owner_tree 并标记 workbook dirty。
- 不可迁移时，行为树 editor 进入 read-only 或拒绝打开，但普通 table view 仍可打开 workbook。
- diagnostic 指向缺失字段和建议迁移动作。

审查点：

- 自定义 editor 不能假设表结构永远匹配。
- 每个复杂 editor 需要声明 schema/layout capability 和 migration。

### 10.10 引用冲突和身份冲突

#### 10.10.1 直接复制 Excel 行导致 duplicate key

操作：

- 用户复制 `fireball` 行，忘记改 key。

预期处理：

- importer 发现 duplicate key。
- 新行可以作为 raw row 存在，但不能成为有效 asset。
- 原有 `fireball` object 保持不变。
- diagnostic 指向 duplicate key 的两行。

#### 10.10.2 直接复制 Excel 行导致 duplicate row guid

操作：

- 用户复制了包括隐藏 metadata 关联的整行，造成两个 row guid 一样。

预期处理：

- row guid 冲突比 duplicate key 更严重。
- importer 不能随便猜谁是真的。
- 如果能识别一行是新复制行，例如数据行位置新、revision 缺失，可建议 regenerate identity。
- 未 resolve 前，冲突行不进入有效 asset set。

#### 10.10.3 删除被运行中系统持有的资产

操作：

- Excel 中删除 `fireball` 行。
- 运行中战斗系统仍持有 `fireball` object。

预期处理：

- hot reload 后触发 asset removed。
- object 进入 missing/unloaded 状态，类似 Unity destroyed object 语义。
- `LoadAsset("fireball")` 失败。
- 旧引用不能静默保留旧数值继续参与新逻辑。

审查点：

- 删除不是普通 patch，需要明确生命周期事件。

### 10.11 converted bytes 与代码 schema 冲突

场景：

- 游戏代码已更新到 schema hash B。
- 包里的 `config.bytes` 是 schema hash A。

预期处理：

- `RuntimeDatabase.Open` 检查 schema hash。
- 完全不兼容时打开失败。
- 可兼容时允许读取，并记录 compatibility warning。
- 热切换到不兼容 bytes 时，整个 switch 失败，继续使用旧 source。

审查点：

- 运行时不能读一半旧 bytes 再崩在某个字段。
- 热切换也是事务。

### 10.12 推演后的审查结论

为了让工作流闭合，计划里必须明确补上这些机制：

- workbook metadata sheet：schema hash、table id、field id、row guid/local id、row revision。
- import snapshot：每次成功 import 都要有可用于三方 merge 的 base snapshot。
- structure compatibility：把缺列、未知列、rename、table rename、field type change 分级处理。
- unknown preservation：旧编辑器保存时不能破坏新字段、新列、样式和公式。
- conflict resolver：处理 editor dirty 与 Excel 外部修改的冲突。
- runtime source switch transaction：切源要整批成功或整批回滚。
- runtime change events：object changed、asset added、asset removed、asset moved/renamed、dependency changed。
- missing object 语义：删除资产后旧引用不能静默继续使用旧数据。
- custom editor capability：复杂编辑器声明自己依赖的 schema/layout version，并提供 migration 或 read-only fallback。

这次推演说明：只说“支持直接改 Excel”和“支持热切换”不够，真正需要设计的是 identity、snapshot、merge、structure migration 和 runtime change propagation。

## 11. 操作人员视角端到端模拟

这一节从“真正坐在电脑前操作的人”的视角模拟：从建表到进游戏使用。重点不是系统内部怎么实现，而是操作人员能不能知道下一步做什么、当前是否安全、失败后如何恢复。

### 11.1 角色与入口

常见参与者：

- 配置程序：定义 schema、写 validator、维护 migration。
- 策划：填表、改数值、看诊断、修数据。
- 工具程序：维护 ExcelDB 工具、编辑器、自定义 inspector。
- 构建/发布人员：执行 convert bytes、看 CI 报告、确认能否出包。
- 客户端/服务器运行时：读取 converted bytes 或 Excel debug source。

每个角色需要不同入口：

- 配置程序入口：schema 文件、migration 工具、schema compatibility report。
- 策划入口：Excel、validation report、可跳转到 cell 的诊断。
- 工具入口：AssetDatabase/SerializedObject、generation report。
- 构建入口：命令行 convert、CI report、artifact hash。
- 运行时入口：RuntimeDatabase、source switch、hot reload report。

缺陷检查：

- 如果只有 API，没有面向角色的报告和入口，团队会不知道失败该找谁。
- 每条错误必须能回答：谁负责修、在哪里修、修完按哪个按钮重新检查。

### 11.2 第一步：配置程序定义 schema

操作：

1. 配置程序新增 `SkillConfig` schema。
2. 声明 table id、sheet display、field id、display name、type、export side、required/default、validator、reference policy。
3. 对 Unity 资源字段，声明 `UnityResourceRef`、允许的 Unity asset type、guid required policy、path display policy。
4. 提交 schema。

操作人员期望看到：

- schema 编译是否通过。
- table id / field id 是否冲突。
- 是否破坏旧 workbook。
- 是否需要 migration。
- 是否影响 converted bytes 兼容性。
- Unity 资源字段是否能生成 object picker / path+guid Excel layout。

预期系统变化：

- 生成 schema hash。
- 生成 schema compatibility report。
- 如果新增 required 字段，标记旧 workbook 需要补数据或 migration。
- 如果只是新增 optional 字段，允许旧 workbook 自动补默认。
- Unity 资源字段进入 dependency schema，后续导入/导出以 guid 校验。

缺陷检查：

- 需要 schema lint：id 冲突、field alias 冲突、validator 引用不存在、export policy 不合法。
- 需要 Unity resource lint：asset type 约束、guid required、path display layout 是否完整。
- 需要 schema diff：告诉操作人员这是新增字段、rename、类型变化还是删除字段。
- 需要 migration requirement：哪些变化必须写 migration，哪些可自动处理。

### 11.3 第二步：生成或升级 Excel 表结构

操作：

1. 操作人员执行 `Generate/Upgrade Workbook`。
2. 选择目标 workbook，例如 `Assets/Configs/game.xlsx`。
3. 工具先 dry-run，显示结构 diff 和风险分级。
4. 操作人员确认后 commit。

操作人员期望看到：

- 会新增哪些 sheet。
- 会新增/修正哪些字段列。
- 哪些策划辅助信息会保留。
- 哪些正式数据行会保留。
- 哪些问题是 warning，哪些是 blocker。

预期系统变化：

- schema 管辖结构被生成或修正。
- hidden metadata sheet 更新。
- 正式数据行不被清空。
- 不影响导入/导出的辅助行/列/样式/批注保留。
- blocker 时不写回 workbook。

缺陷检查：

- 必须有 preview/dry-run，否则操作人员不敢点“生成”。
- 必须有备份或可回滚。
- 必须明确“本次不会改数据行”。
- 需要处理 Excel 文件被打开/锁定的情况：不能写就报可理解错误，不应生成半成品。

### 11.4 第三步：策划填写和修改数据

操作：

1. 策划打开 Excel。
2. 在正式数据行填写 `fireball`、`ice_nova` 等配置。
3. 可添加辅助备注、颜色、批注、筛选。
4. 保存 Excel。

操作人员期望看到：

- 哪些区域能填，哪些区域不该改。
- 填错引用、类型、必填项时能被指出。
- 额外备注不会被工具清掉。

预期系统变化：

- watcher 或手动 refresh 触发 import。
- 新行没有 row guid 时生成 identity。
- validation report 指向具体 sheet/row/cell。
- safe/warning/error/blocker 分级。

缺陷检查：

- 需要明确数据区域边界；否则策划插行/加说明会让 importer 误判。
- 需要 cell 级诊断；只报 “SkillConfig invalid” 不够。
- 需要新行 identity 写回策略；Excel 被锁定时要提示 metadata 未落盘的风险。

### 11.5 第四步：导入到编辑器并作为 asset 使用

操作：

1. 操作人员回到编辑器。
2. `AssetDatabase.Refresh()` 或自动刷新。
3. 在 asset browser 中搜索 `t:SkillConfig fireball`。
4. 用 inspector 或自定义 editor 查看配置。

操作人员期望看到：

- 新增资产出现在搜索结果。
- key、display name、引用都正确。
- validation 状态可见。
- 有错误的资产不能假装正常。

预期系统变化：

- Excel rows 成为 resident objects。
- key index、guid index、dependency graph 更新。
- dirty/conflict 状态显示在编辑器里。

缺陷检查：

- asset browser / table view 必须通过 `TryGetAssetStatus`、`TryGetWorkbookStatus` 和 diagnostics query 显示 clean / warning / error / conflict。
- 需要 object picker 和引用显示，否则配置之间互引很难用。
- missing reference 必须通过 asset/workbook diagnostics 可视化，而不是只到运行时报空。

### 11.6 第五步：进入 Play Mode 使用 Excel 源调数

操作：

1. 开发者进入 Play Mode。
2. RuntimeDatabase 使用 `ExcelDataSource`。
3. 战斗中读取 `fireball` 配置。
4. 策划直接改 Excel 的 `damage`，保存。

操作人员期望看到：

- 修改后游戏内效果自动更新或有明确刷新按钮。
- 成功热更新有提示。
- 失败时游戏继续使用旧数据。
- 哪些系统收到变化事件可观察。

预期系统变化：

- import candidate snapshot。
- validation 通过后 atomic patch resident objects。
- `object changed` / `dependency changed` 事件发出。
- 派生缓存重建。

缺陷检查：

- 需要 hot reload 开关和模式显示，避免正式模式误开。
- 需要 reload report，说明更新了哪些 assets。
- 需要 batch/coalesce，避免保存一次 Excel 触发多次 reload。
- 需要旧数据保底；热更新失败不能污染运行态。

### 11.7 第六步：从 Excel 源切到 converted bytes 验证出包数据

操作：

1. 操作人员执行 `Convert To Bytes`。
2. 生成 `config.bytes`。
3. Play Mode 或开发包调用 `RuntimeDatabase.SwitchDataSource(new ConvertedBytesDataSource(...))`。
4. 继续运行同一场战斗验证。

操作人员期望看到：

- convert 是否成功。
- converted bytes 与当前 Excel 是否同源。
- 切换是否成功。
- 切换后对象 identity 是否保持。

预期系统变化：

- convert 输出 deterministic bytes 和 hash。
- runtime 校验 schema hash/source hash。
- source switch dry-run 通过后 atomic commit。
- 不兼容则保留 Excel source。

缺陷检查：

- 需要显示 Excel source hash 和 bytes source hash 是否匹配。
- 需要 source switch report。
- 需要切回 Excel 的操作入口。
- converted bytes 不应悄悄使用旧 Excel 数据。

### 11.8 第七步：提交、CI、构建

操作：

1. 提交 schema、Excel、generated metadata、converted bytes 或由 CI 生成 bytes。
2. CI 执行 schema lint、workbook import、validation、convert。
3. 构建人员查看报告。

操作人员期望看到：

- 哪个文件失败。
- 哪张表哪一行哪一列失败。
- 是 warning 还是 blocker。
- 是否可以出包。

预期系统变化：

- CI 生成 deterministic convert report。
- blocker 阻止构建。
- warning 可配置是否阻止。
- artifact hash 可追踪。

缺陷检查：

- 需要命令行工具，不只是编辑器按钮；默认命令为 `schema-lint`、`workbook-check`、`convert`、`verify-bytes`。
- 需要 deterministic output，否则版本库会噪音很大；bytes 和 machine-readable report 的逻辑字段必须稳定。
- 需要 CI-friendly report，例如 json + human readable；CI 逻辑只读 machine-readable report。
- 需要 artifact manifest 追踪 schema_hash、export_view_hash、source_set_hash、preload_plan_hash、bytes_hash、input_workbooks、dependency digest 和 Unity resource dependencies。
- 需要区分 client/server/editor export，否则会把 editor-only 字段带进包。

### 11.9 第八步：游戏运行时加载

操作：

1. 正式包启动。
2. RuntimeDatabase 打开 `ConvertedBytesDataSource`。
3. 游戏系统按 key/path/object reference 读取配置。

操作人员期望看到：

- 启动失败能明确指出 bytes/schema 不兼容。
- 运行时不允许写回。
- 缺失引用有明确错误。
- Unity 资源 guid 缺失或无效时能明确报错，path 仅用于提示。

预期系统变化：

- schema hash 校验通过。
- 建立 runtime index 和 dependency graph。
- Unity resource refs 以 guid 建立依赖或运行时加载 key。
- editor-only 字段不可见或已 strip。
- runtime 默认 read-only。

缺陷检查：

- 需要 runtime startup report。
- 需要 release/development/editor mode policy。
- 需要 graceful fail：正式包遇到关键配置错误应尽早失败，而不是战斗中崩。

### 11.10 操作人员视角暴露出的缺陷

从这个端到端流程看，还需要补充这些面向操作者的能力：

- Dry-run preview：生成表结构、source switch、convert 都要先预览风险。
- Report 体系：schema lint report、generation report、import report、validation report、convert report、hot reload report、source switch report。
- 可定位诊断：所有错误要能定位到 workbook/sheet/row/cell/schema field。
- 责任归属：错误应能看出是程序改 schema、策划改数据、工具迁移、还是构建环境问题。
- 可恢复性：blocker 不写回、不清空数据、保留旧 snapshot，可回滚。
- 模式显示：Editor/Play Mode/Development/Release 下哪些能力可用必须可见。
- 文件锁处理：Excel 打开占用、只读文件、权限不足时要有明确提示。
- 备份策略：结构生成、metadata 写回、migration 前自动备份或可撤销。
- 版本协作：多人分支合并后要有 duplicate guid/key 修复工具。
- 人类入口：不能只有 API，还要有菜单、命令行和报告文件。

结论：从操作人员视角看，流程缺陷不只在数据模型，也在“看不见风险、失败不知道怎么恢复”。完整目标版除了 schema contract，还必须同时设计 report、dry-run、定位和回滚策略。

## 12. 实操视角落地风险分类

这一节从项目真正落地使用的角度分类风险。这里的分类不是产品阶段、不是交付裁剪，也不是能力可选性；它只说明某类能力缺失时会怎样伤害真实团队工作流。所有被列入完整目标版默认能力的条目，都必须回到 14.x 的权威协议和 15 的 conformance 验收。

### 12.1 主工作流硬依赖

#### 12.1.1 Workbook metadata 与稳定身份

必须闭合的能力：

- workbook guid。
- table id / field id / row guid / local id。
- row revision / content hash。
- source fingerprint。
- schema hash / layout version。

为什么会阻断主工作流：

- 没有 row guid，Excel 复制行、改 key、改顺序、rename sheet 后，系统无法判断是同一个资产还是新资产。
- 没有 field id，字段 rename 会被误判成“删旧列 + 加新列”。
- 没有 revision，无法做 editor dirty 与 Excel 外部修改的三方 merge。

验收标准：

- 改 sheet 名不丢资产。
- 改 key 不改变 object identity。
- 复制行能检测 duplicate row guid。
- 字段 rename 能通过 metadata 识别。

#### 12.1.2 非破坏性 Excel 写回

必须闭合的能力：

- 保留 unknown columns。
- 保留样式、公式、筛选、冻结窗格、批注、数据验证。
- 只写受影响 cell/range，而不是整 sheet 重建。
- 写 metadata 时不破坏用户可见内容。

为什么会阻断主工作流：

- 策划会把 Excel 当工作台，不只是数据容器。
- 如果保存一次就丢样式、公式和未知列，团队不会敢用编辑器写回。

验收标准：

- 旧编辑器打开含新列的 Excel，修改已知字段并保存，新列仍保留。
- 有公式和筛选的表保存后仍可用。
- 自定义 editor 保存行为树后，不破坏同 sheet 上其它人工维护内容。

#### 12.1.3 Import snapshot 与三方 merge

必须闭合的能力：

- 上次成功 import 的 base snapshot。
- editor draft snapshot。
- Excel latest snapshot。
- property-level conflict detection。
- conflict resolver。

为什么会阻断主工作流：

- 游戏项目里 Excel 和 editor inspector 同时改同一份配置很常见。
- 只靠文件时间戳会导致误覆盖或反复要求 reload。

验收标准：

- 两边改不同字段可自动合并。
- 两边改同字段必须报冲突。
- 未 resolve 冲突时 `SaveAssets` 拒绝静默覆盖 Excel。

#### 12.1.4 结构兼容与迁移

必须闭合的能力：

- missing column policy。
- unknown column preservation。
- field rename migration。
- field type change migration。
- table/sheet rename resolution。
- custom editor schema/layout capability check。

为什么会阻断主工作流：

- 游戏配置 schema 会持续变动。
- 编辑器结构和 Excel 表结构不一致不是异常，是日常分支协作和版本迭代的必然。

验收标准：

- 新字段缺列时 workbook 仍可打开，并给出补列/迁移动作。
- unknown column 不被旧编辑器删除。
- 行为树 editor 发现结构不匹配时，可以自动迁移或 read-only fallback。

#### 12.1.5 Runtime hot reload 事务

必须闭合的能力：

- runtime source switch transaction。
- candidate snapshot validation。
- patch resident objects。
- failed reload rollback。
- object identity preservation。

为什么会阻断主工作流：

- 运行中调数要求“成功就整批生效，失败就保持旧数据”。
- 半更新会让战斗、AI、UI 缓存进入无法解释的状态。

验收标准：

- Excel 格式错误时，运行中对象保持旧值。
- 切源失败时继续使用旧 source。
- 热更新成功后，同一个 object 实例字段变为新值。

#### 12.1.6 Runtime change events 与缓存失效

必须闭合的能力：

- object changed。
- asset added。
- asset removed。
- asset moved/renamed。
- dependency changed。
- table reloaded。

为什么会阻断主工作流：

- 游戏运行系统经常会对配置做派生缓存，例如技能公式、AI 黑板、掉落权重、UI 展示数据。
- 只改 object 字段，不通知缓存重建，看到的效果仍然可能是旧数据。

验收标准：

- 修改技能伤害触发 `SkillConfig` changed，战斗缓存可重建。
- 删除资产触发 removed，旧引用进入 missing/unloaded 语义。
- 引用变化触发 dependency changed，资源预加载可重算。

### 12.2 团队效率硬依赖

#### 12.2.1 Validation 与诊断 UX

必须闭合的能力：

- workbook/sheet/row/property/cell 级定位。
- error / warning / info 分级。
- convert blocker 与 editor warning 分离。
- 可跳转 Excel cell。
- 可写回批注或诊断 sheet。

实操影响：

- 策划看到“RowRef dangling”不够，需要知道是哪张表、哪一行、哪个单元格。
- 有些问题可以继续调试，有些问题必须阻止出包。

#### 12.2.2 Convert pipeline 与 CI 集成

必须闭合的能力：

- 命令行 convert。
- schema hash 校验。
- deterministic output。
- 增量 convert。
- convert report。
- CI fail policy。

实操影响：

- 没有 CI convert，配置错误会在运行时暴露。
- 输出不 deterministic，会导致版本控制里 bytes 经常无意义变化。

#### 12.2.3 分支协作与合并策略

必须闭合的能力：

- Excel 文件冲突的推荐工作流。
- xlsx 对应的 canonical snapshot / diff / review artifact。
- CI 检查 snapshot 是否和 workbook 同步。
- 三方 workbook merge 命令和 conflict artifact。
- metadata 冲突修复工具。
- duplicate row guid 修复工具。
- key rename / row move / copied row 的辅助诊断。

实操影响：

- 多人改 Excel 是必然。
- 只依赖 Git 二进制冲突处理不够，需要工具层修复身份冲突。
- review 不能只看二进制 xlsx；需要能看到 asset/property/reference 级变化。

#### 12.2.4 权限与数据源模式

必须闭合的能力：

- Editor mode。
- Play Mode debug mode。
- Development build hot reload mode。
- Release read-only mode。
- 对每种模式明确哪些 API 可用。

实操影响：

- 如果运行时也能随便写 Excel，正式包风险很高。
- 如果开发包不能 hot reload，调试效率会大打折扣。

#### 12.2.5 Asset reference 体验

必须闭合的能力：

- object picker。
- 按 key/name 搜索引用。
- 引用约束提示。
- rename key 后引用稳定。
- missing reference 展示。
- Unity resource picker。
- UnityResourceRef path/guid mismatch 展示。
- Unity asset move/rename 后刷新 main asset path。

实操影响：

- 配置之间大量互相引用，纯手填 `table:id` 不适合日常使用。
- 直接改 Excel 时也需要友好的引用输入和校验。
- Unity 资源路径会变，Excel 里显示路径是为了人看；真正稳定性必须来自 guid。

### 12.3 规模化硬依赖

#### 12.3.1 性能与增量

必须闭合的能力：

- runtime allocation budget：除初始化阶段的 source open / 首次批量导入和水位线增长外，稳定热路径不产生 GC allocation。
- 水位线预热机制：对象池、事件缓冲、diff buffer、candidate snapshot、索引、Excel/bytes parser workspace、临时解析工作区可以主动 reserve。
- workbook/sheet/row 级增量 import。
- property-level diff。
- 大表索引。
- 大量 object changed event 的 batch/coalesce。
- converted bytes segment / load set / lazy residency 协议。
- zero-allocation read path：`LoadAsset`、引用解析、key lookup、依赖遍历、字段读取不分配。
- zero-allocation hot reload commit path：patch、事件分发、索引更新必须复用 buffer；容量不足只能登记水位线增长或失败。
- zero-allocation ExcelDataSource refresh workspace：重复 hot reload 不得每次创建 ZIP/XML/cell parse 临时对象；第三方 backend 分配必须进入 external backend allocation。
- GC allocation profiler test：在 Unity 2022.3 和核心 .NET 测试中都要能验证。

实操影响：

- 小样例全量 reload 没问题，大项目几万行配置会拖慢 Play Mode 反馈。
- 运行时配置读取和热更新如果持续产生 GC，会把调数便利性转化成帧时间抖动，尤其在战斗、AI、UI 刷新和资源预加载高频路径里很明显。

边界说明：

- 允许初始化阶段的 source open、首次批量导入、convert 和 schema generation 分配内存；machine-readable report append 属于 core operation，达到水位线后不得分配。
- 允许 human-readable report、Excel 批注、UI 展示和 debug log 在显式 projection 阶段分配；这些分配必须和 core operation 分账，不能作为 no-GC 通过证据。
- 允许超过历史容量时水位线增长并产生分配，但必须可观测、可预热、可通过 report 暴露；相同规模第二次运行不得再次分配。
- 不接受稳定容量后的 per-row、per-property、per-event 临时分配。

#### 12.3.2 可观察性

必须闭合的能力：

- import 耗时统计。
- hot reload 成功/失败日志。
- source switch report。
- changed asset summary。
- validation dashboard。

实操影响：

- 热更新失败时，程序需要知道是 watcher 没触发、import 失败、validation 失败还是 patch 失败。

#### 12.3.3 扩展点治理

必须闭合的能力：

- custom validator 注册生命周期。
- property drawer 注册生命周期。
- custom importer/migration 注册。
- 不同团队模块的扩展隔离。

实操影响：

- 没有治理时，项目后期会出现 validator 顺序依赖、drawer 冲突、迁移脚本互相踩的问题。

### 12.4 内部试运行 smoke path

如果团队要在完整目标版完全落地前做内部试运行，smoke path 不是先做完整 UI，而是先验证这些高风险能力。它只能用于发现风险和驱动实现顺序，不能作为对外完整目标版、CompleteConformance 或默认能力裁剪依据：

1. 稳定身份：workbook/table/field/row metadata。
2. 非破坏写回：至少 unknown column preservation 和 cell-level data write。
3. 三方 merge：base/editor/excel property diff。
4. 热更新事务：candidate snapshot validation + atomic patch/rollback。
5. 变化事件：object changed / added / removed / dependency changed。
6. 结构兼容：missing/unknown/rename 的明确 policy。
7. convert bytes：deterministic export + schema hash + runtime read-only load。
8. 诊断：能定位到 Excel cell，并区分 warning 和 blocker。

没有这八项，Unity-like API 再像，也只能是表层像；有了这八项，团队可以开始内部试用主工作流并收集风险。正式可交付目标仍必须满足 14.x 默认协议和 15.x conformance。

## 13. 历史补丁痕迹审计与权威索引

当前文档已经经过多轮推进，部分规则是在推演中暴露风险后补充进来的。计划阶段不应该保留“后来补上”的实现心智；这些条目只用于审计历史痕迹，并把对应决策指向 1.1、8、14 和 15 的原生需求。13 章本身不新增独立事实源。

### 13.1 已识别的历史补丁痕迹

1. `ExternalRef` 之后再追加 `UnityResourceRef`。
   - 补丁痕迹：先有泛化外部引用，再发现 Unity 资源需要 path + guid。
   - 原生化落点：引用系统从第一天就是 reference family。Unity asset 使用 `UnityResourceRef`，guid 是身份，main asset path 是展示；`ExternalRef` 只用于非 Unity 或扩展场景。

2. “schema 控制表结构”之后再补“保留策划辅助信息”。
   - 补丁痕迹：像是在 schema 强控制和 Excel 自由编辑之间临时找平衡。
   - 原生化落点：workbook 原生分区。schema-owned structure region、data region、helper/freeform region、metadata region 各自有所有权和写入规则。生成器只写自己拥有的区域，正式数据和无语义辅助信息默认保留。

3. 表结构生成再补 dry-run、generation report、warning/blocker。
   - 补丁痕迹：先讨论生成，再发现误写 Excel 的风险。
   - 原生化落点：所有会改变 workbook、source、converted bytes 或 resident objects 的操作都走 operation transaction 协议：analyze、diff、classify、report、commit、rollback。没有 report 的结构写入不允许进入实现。

4. 隐藏 metadata sheet 在后文多处被补充。
   - 补丁痕迹：rename、copy row、merge、hot reload 各自都要求 metadata。
   - 原生化落点：workbook metadata 是 ExcelDB asset identity 的原生组成部分，不是导入缓存。workbook guid、table id、field id、row guid/local id、row revision、schema hash 必须在完整目标版定义并落地。

5. “支持直接修改 Excel”从目标补成工作流细节。
   - 补丁痕迹：像是额外支持 Excel 外部编辑。
   - 原生化落点：Excel 是主 authoring surface 之一。直接改 Excel、保存、验证、导入、热更新、冲突处理，是主链路，不是旁路能力。

6. Excel 与 converted bytes 热切换在 runtime 段落里后补。
   - 补丁痕迹：converted bytes 像是 Excel 的导出副本，后来才要求热切换。
   - 原生化落点：`ExcelDataSource` 与 `ConvertedBytesDataSource` 是同一个 `RuntimeDatabase` 的两个一等 source implementation，共用 identity、schema hash、reference graph 和 change event 协议。

7. 热更新后再补 object identity、change event、cache invalidation。
   - 补丁痕迹：先说刷新 resident objects，再补业务系统如何知道变化。
   - 原生化落点：runtime object graph 原生支持 change propagation。added、removed、moved、renamed、property changed、dependency changed、recreated 都是明确事件，热切换必须在事务内生成这些事件。

8. 编辑器结构与 Excel 表结构冲突在模拟阶段才展开。
   - 补丁痕迹：schema 变更、generated C#、custom editor、Excel header 各自讨论。
   - 原生化落点：schema compatibility 是完整目标版的核心协议。字段 rename、type change、required/optional change、deprecated field、editor capability version 都必须有兼容矩阵和诊断规则。

9. “保留旧 `ConfigDatabase` 能力”容易变成 facade 补丁。
   - 补丁痕迹：先换 Unity-like API，再让旧实现继续兜底。
   - 原生化落点：旧 `ConfigDatabase` 可以作为内部 storage/transaction/undo/dependency engine，但对外模型必须由 Object、SerializedObject、AssetDatabase、schema、workbook metadata 统一定义。facade 不能暴露旧表模型语义。

10. Unity-like 命名与新增概念命名混在一起。
    - 补丁痕迹：既想像素级 like，又出现 Unity 没有的 `RuntimeDatabase`、DataSource。
    - 原生化落点：Unity 已有同构概念采用同名 API，仅 namespace 不同；Unity 没有的概念统一采用 ExcelDB-native 风格命名。这个命名策略是设计原则，不是个别例外。

11. ExcelDB asset guid 与 Unity asset guid 在文本里共用 `guid`。
    - 补丁痕迹：AssetDatabase facade 要暴露 Unity-like GUID API，同时 UnityResourceRef 也使用 Unity `.meta` guid。
    - 原生化落点：术语和 schema/report/API 文档必须区分 `AssetGuid`、`WorkbookGuid`、`RowGuid`、`UnityGuid`。Unity-like `GUIDToAssetPath` 默认处理 ExcelDB `AssetGuid`；`UnityResourceRef.guid` 明确是 `UnityGuid`，二者不得互相兜底或隐式转换。

12. metadata sheet 与 hidden companion columns 像两套身份源。
    - 补丁痕迹：早期只说“metadata 是唯一稳定身份源”，后文又要求 import 以 companion identity 匹配 row。
    - 原生化落点：metadata sheet 是 identity ledger，hidden companion cells 是随 Excel 行移动的 row-local identity anchor。二者冲突时进入 reconciliation/repair analyze；不能把任一方当成可以静默覆盖另一方的第二事实源。

13. “表头上方说明行应保留”与固定第 1-7 行 schema-owned layout 冲突。
    - 补丁痕迹：为了支持策划自由备注，临时放宽了表头区域。
    - 原生化落点：pre-header helper/freeform rows 是 workbook layout 的原生能力，必须由 table descriptor、layout metadata 或明确 repair proposal 识别。无法证明的插行不被当作 schema facts，也不静默删除；会进入 layout warning/blocker 并提示登记为 helper/freeform region。

14. LocalizedTextRef、expression、weighted selection、curve、label、preset 是迭代中补入完整目标版的高级能力。
    - 补丁痕迹：早期草案只强调 schema/options/metadata/reference，后文才把游戏配置常见高级表达补全。
    - 原生化落点：这些能力属于完整目标版默认 capability family。项目 profile 可以禁用使用，但 conformance 必须证明实现存在；禁用只能产生 stable diagnostic/read-only/build gate，不能让业务代码或 importer 临时解释普通 string/JSON/formula。

15. multi-object edit 从 Unity-like API 表面后补成默认能力。
    - 补丁痕迹：早期调用示范只覆盖单对象，后文才补多选编辑的 common property、mixed value 和 all-or-nothing。
    - 原生化落点：multi-object edit 是 UnityEditorConformance 的原生验收项。generic inspector 必须支持可写 multi-target surface；custom editor 只有 capability handshake 通过才可写，否则 read-only/table_view/reject_open。

16. `boxedValue` / `managedReferenceValue` 既要像 Unity，又不能破坏 no-GC 主路径。
    - 补丁痕迹：API 为了像素级贴近 Unity 后补了 allocating projection。
    - 原生化落点：它们属于 Unity-like editor projection conformance，不是 core no-GC 主表面。必须同时存在 descriptor/generated accessor/Span 或 handle 型非分配主路径；自定义 editor 不应依赖 allocating projection 完成核心编辑。

17. xlsx backend allocation 与 Development ExcelDataSource no-GC 之间存在后补豁免。
    - 补丁痕迹：先要求 ExcelDataSource hot reload，再发现第三方 xlsx/xml/zip backend 可能分配。
    - 原生化落点：CompleteConformance 必须提供至少一个满足 DevelopmentRuntimeConformance 的 ExcelDataSource backend / wrapper / pooled workspace。分配式 backend 可以作为 editor authoring/debug projection，但不能冒充 runtime hot reload no-GC 证明。

18. Object / ScriptableObject / GUID / Hash128 等 API 在多个章节重复定义。
    - 补丁痕迹：后补 RuntimeDatabase、AssetDatabase、SerializedObject 章节时复制了同一批基础类型。
    - 原生化落点：每个 public API 只能有一个权威定义章节；其他章节只引用该定义并声明附加语义，不能再复制出第二套签名。

### 13.2 原生协议收束索引

本节只索引已经上升到 1.1、8、14 和 15 的原生协议，不新增独立规则。实现规划应围绕这些协议展开，而不是在各内部里程碑各自补规则：

1. Schema contract protocol。
   - 定义表、字段、枚举、简单结构体、引用、校验、布局、表头注释、导出端、兼容性、editor capability。
   - schema 是有效表结构唯一来源。

2. Workbook region ownership protocol。
   - 定义 schema-owned、data、helper/freeform、metadata 的边界。
   - 定义生成、升级、保存、保留、清理和冲突判断。

3. Asset identity protocol。
   - 定义 workbook guid、table id、field id、row guid/local id、row revision。
   - key、path、sheet name、header text 都是可变展示或定位信息，不作为最终身份。

4. Reference family protocol。
   - 内部资产引用、Unity asset 引用、本地化文本引用、插件外部引用分别有身份规则。
   - Unity asset 原生使用 `UnityResourceRef { guid, main_asset_path }`。
   - 本地化文本原生使用 `LocalizedTextRef`，不是普通 string 或高级 value shape 占位。
   - Unity asset reference family 属于 Unity adapter 首发能力，不让核心库依赖 UnityEngine / UnityEditor。

5. Platform adapter protocol。
   - Unity 2022.3 是第一接入目标。
   - 非 Unity 宿主通过 adapter 复用同一套 schema、metadata、source、convert、runtime identity 和 reference family extension。
   - 核心库只能依赖宿主无关 contract，不能反向引用具体宿主 API。

6. Operation transaction/report protocol。
   - 表结构生成、metadata flush、import、save、convert、source switch、hot reload 都必须先分析风险，再原子提交。
   - safe、warning、error、blocker 的判定标准跨流程一致。

7. Data source protocol。
   - Excel 与 converted bytes 是同一数据模型的不同 source。
   - open、switch、refresh、convert 都必须校验 schema hash、identity、reference graph。

8. Performance and GC budget protocol。
   - 初始化阶段的 source open、首次批量导入、convert、schema generation 和水位线增长允许分配。
   - 水位线包括 object/index/event/diff buffer，也包括 ExcelDataSource parser workspace、ConvertedBytesDataSource reader workspace 和 candidate snapshot。
   - 达到水位线后的运行时读取、handle/key 查找、引用解析、依赖遍历、source switch commit、hot reload patch、事件分发、editor core preflight 和 machine-readable report append 默认不得产生 GC allocation。
   - human-readable report、Excel 批注、UI 展示和 debug log 属于 report projection；可以按 profile 分配，但不能进入 core no-GC 证明或 report_hash。
   - 高频路径使用 `AssetKey` / `AssetIdentity` / caller-owned buffer，不依赖每帧字符串拼接或分配式枚举。
   - runtime 热路径、editor core operation、report append 和 external backend allocation 都要有 allocation budget 测试，Unity adapter 和宿主无关 runtime/editor 都要能验证。

9. Runtime change propagation protocol。
   - resident objects 的 patch、recreate、delete、dependency changed、index changed 都必须有事件。
   - 切源或热更新失败时保留旧 source 和旧 object graph。

10. Editor capability and migration protocol。
   - generated code、custom editor、drawer、validator 与 workbook schema 都要有版本和能力检查。
   - 当编辑器结构落后于表结构时，必须能区分可编辑、只读、可保留、必须升级。

### 13.3 完整目标版影响索引

本节只索引完整目标版的交付影响，权威规则仍在 1.1、8、14 和 15。完整目标版不能被缩成 “schema options + workbook metadata”，而应该交付完整原生协议闭环：

- schema contract。
- value shape and header annotation，包含 enum、simple struct、表头注释和 dropdown。
- workbook region ownership。
- workbook metadata and identity。
- reference family，包含 internal ref、UnityResourceRef 与 LocalizedTextRef。
- platform adapter boundary，至少包含 Unity 2022.3 adapter 和核心无 Unity 依赖约束。
- first-class schema capability family，包含 map、union、schema-discriminated polymorphic payload、Expression、weighted selection、curve、label 和 preset expansion。
- runtime performance and GC budget。
- operation transaction/report。
- compatibility matrix。
- schema generation dry-run。
- 完整 import/convert/editor/runtime diagnostic。
- CompleteConformance capability matrix，用来证明每个默认能力落到 Core、UnityEditor、DevelopmentRuntime、ReleaseRuntime 中的哪类验收。

内部里程碑可以先后实现 Object、AssetDatabase、SerializedObject、Excel source、runtime source switch 和 hot reload，但这些都属于完整目标版的交付范围。这样不会出现“API 已经像 Unity，但 Excel 工作流靠补丁兜底”的结构性风险。

## 14. 执行细化：确定性默认决策

本节用于消除实现时的自由发挥。若后文没有更具体的规则，默认按本节执行；若两个目标冲突，按优先级选择，并把被牺牲的目标写入 report。

### 14.1 决策优先级

1. 不丢数据，不破坏用户 workbook。
2. schema 是唯一结构事实源，metadata 是唯一稳定身份源。
3. 所有结构写入、import、save、convert、source switch、hot reload 都必须事务化。
4. runtime object identity 和 change event 必须一致，业务缓存不能靠猜。
5. 达到水位线后的 runtime 热路径默认不得产生 GC allocation。
6. 核心库保持宿主无关，Unity 2022.3 只在 adapter 中出现。
7. Unity adapter 的对外 API 像素级贴近 Unity。
8. Excel authoring 体验优先保留策划手工辅助信息。

解释：

- 数据安全高于 API 好看；如果一次保存会丢未知列、样式、公式或正式数据行，必须拒绝或进入手动修复流程。
- 身份稳定高于显示文本；key、sheet name、header text、asset path 都可以变，metadata identity 不能被静默重建。
- 性能 contract 高于调试便利；稳定窗口只写 machine-readable code/payload，诊断字符串必须在 report projection、editor/debug 或显式低频路径生成。

### 14.2 默认 assembly / adapter 边界

默认至少拆成三类边界：

- `ExcelDbEngine`：宿主无关 runtime、object identity、source、converted bytes reader、reference family registry、GC budget 基础设施。
- `ExcelDbEditor`：宿主无关的编辑抽象、schema generation、workbook import/export、diagnostic/report、transaction。
- Unity 2022.3 adapter：UnityEditor 菜单、import hook、asset picker、Play Mode hot reload、Unity build integration、`UnityResourceRef` resolver。

硬约束：

- `ExcelDbEngine`、schema、source、convert pipeline 不引用 `UnityEngine` / `UnityEditor`。
- `UnityResourceRef` 是 Unity adapter 首发的 reference family；核心只认识 reference family contract，不认识 Unity asset database。
- 非 Unity adapter 不能修改 schema/workbook/runtime 的核心语义，只能通过 reference family、source、editor facade、validator、converter extension 扩展。

#### 14.2.1 默认 database context / static facade 绑定协议

ExcelDB 的真实状态必须归属于显式 database context；static `AssetDatabase` / `RuntimeDatabase` 只是 Unity-like 默认入口，不能把系统实现成不可替换的全局单例。这样同一进程内的编辑器 authoring、Play Mode 调数、工具批处理、测试用例、client/server 双数据源和后台校验可以相互隔离，同时保留常规调用的 Unity-like 手感。

context 默认种类：

```text
EditorAuthoringContext: workbook mount set、dirty draft、Undo、SerializedObject、SaveAssets
EditorPlayModeRuntimeContext: Play Mode runtime source、hot reload、ChangeSet
DevelopmentRuntimeContext: development build runtime source、controlled hot reload
ReleaseRuntimeContext: release runtime source、read-only converted bytes/composite source
ToolContext: CLI/import/convert/migrate/verify 的一次性上下文
TestContext: 单元测试/集成测试隔离上下文
```

context 默认拥有：

- context id、context kind、mode、operation profile、allocation policy、owner thread / scheduler。
- mounted workbook/source set descriptor、current source、source revision、schema/registry binding。
- resident object pool、instance id allocator、key/path index、identity index、reference/dependency graph。
- editor dirty draft、undo stack、conflict state、metadata repair state；这些只存在于 editor authoring context。
- runtime load set residency、snapshot handle pool、snapshot pin table、ChangeSet/event buffer 和 operation queue。
- watcher registration、last operation report、diagnostic sink、allocation budget/watermark。

static facade 默认规则：

- `ExcelDbEditor.AssetDatabase.*` 调用当前 editor authoring context；Unity adapter 默认把它绑定到当前 Unity project 的 default editor context。
- `ExcelDbEngine.RuntimeDatabase.*` 调用当前 runtime context；Unity Play Mode 默认使用 play mode runtime context，Release 默认使用 release runtime context。
- 如果没有显式绑定 current context，static facade 使用 default context；default context 必须由 host/bootstrap 创建，不能在第一次业务读取时隐式读取 source 或分配大量状态。
- static facade 不把 context id 写入 asset identity；同一个 `AssetIdentity` 可以在不同 context 中指向同一存盘资产，但 resident object instance、`AssetKey`、`SerializedObject`、`SerializedProperty`、`RuntimeSnapshot` 都是 context-bound。
- context 绑定 scope 只能改变当前线程/当前 async flow 的 facade 目标；不能改变已有 object/key/property/snapshot 的所属 context。
- context bind/unbind 是初始化/工具/测试路径；稳定 runtime 热路径不得每帧反复切换 context。

默认 context binding API surface：

本节只权威定义 context 类型、context 绑定入口和 instance/static facade 的绑定关系。`Object` / `ScriptableObject` 的权威 API surface 在 14.19.1；`AssetDatabase`、`GUID`、`Hash128`、Undo 和 EditorUtility 相关类型在 14.20；`SerializedObject` / `SerializedProperty` 在 14.21；`RuntimeDatabase`、`DataSource`、`ExcelDataSource` 和 `ConvertedBytesDataSource` 在 14.22。14.2 不再复制这些业务 API 签名。

```csharp
namespace ExcelDbEngine
{
    public enum DatabaseContextKind
    {
        EditorAuthoring,
        EditorPlayModeRuntime,
        DevelopmentRuntime,
        ReleaseRuntime,
        Tool,
        Test
    }

    public readonly struct DatabaseContextId
    {
        // Opaque session-local context id. Not serialized into workbook/bytes.
    }

    public sealed class RuntimeDatabaseContext : IDisposable
    {
        public static RuntimeDatabaseContext Create(RuntimeMode mode);

        public DatabaseContextId contextId { get; }
        public DatabaseContextKind contextKind { get; }
        public RuntimeMode mode { get; }
        public bool isOpen { get; }

        // Instance methods mirror RuntimeDatabase static methods, except context accessors.
        public bool Open(DataSource source);
        public bool SwitchDataSource(DataSource source);
        public bool Refresh();
        public void Close();

        public T LoadAsset<T>(string keyOrPath) where T : Object;
        public bool TryGetAsset<T>(string keyOrPath, out T asset) where T : Object;
        public bool TryGetAssetKey<T>(string keyOrPath, out AssetKey key) where T : Object;
        public bool TryGetAsset<T>(AssetKey key, out T asset) where T : Object;
        public bool TryGetAsset<T>(AssetIdentity identity, out T asset) where T : Object;
        public RuntimeQueryStatus GetAssets<T>(Span<T> buffer, out int count) where T : Object;
        public RuntimeQueryStatus GetDependencies(
            AssetIdentity identity,
            Span<DependencyTarget> buffer,
            out int count);
        public RuntimeQueryStatus GetDependencies(
            AssetIdentity identity,
            in DependencyQuery query,
            Span<DependencyTarget> buffer,
            out int count);
        public RuntimeSnapshot AcquireSnapshot();
        public RuntimeSnapshot AcquireSnapshot(in RuntimeSnapshotOptions options);
        public bool TryAcquireSnapshot(out RuntimeSnapshot snapshot);
        public bool TryAcquireSnapshot(in RuntimeSnapshotOptions options, out RuntimeSnapshot snapshot);

        public void EnableHotReload();
        public void DisableHotReload();
        public void Reserve(RuntimeCapacity capacity);
        public void Prewarm();
        public bool TryGetLoadSetId(string loadSetId, out RuntimeLoadSetId id);
        public bool LoadSet(RuntimeLoadSetId id);
        public bool PrewarmLoadSet(RuntimeLoadSetId id);
        public bool UnloadSet(RuntimeLoadSetId id);
        public RuntimeLoadSetStatus GetLoadSetStatus(RuntimeLoadSetId id);
        public void SetAllocationPolicy(RuntimeAllocationPolicy policy);

        public RuntimeAllocationPolicy allocationPolicy { get; }
        public DataSource currentSource { get; }
        public OperationReport GetLastOperationReport();

        public event Action<ChangeSet> changed;
        public event Action<Object> objectChanged;
        public void Dispose();
    }

    public readonly struct RuntimeDatabaseContextScope : IDisposable
    {
        public void Dispose();
    }
}

namespace ExcelDbEditor
{
    // GUID, Hash128, options and processor types are defined in 14.20.
    // This block is authoritative only for explicit editor context binding.
    public sealed class AssetDatabaseContext : IDisposable
    {
        public static AssetDatabaseContext CreateEditorAuthoring();

        public ExcelDbEngine.DatabaseContextId contextId { get; }
        public ExcelDbEngine.DatabaseContextKind contextKind { get; }

        // Instance methods mirror AssetDatabase static methods, except context accessors.
        public void MountWorkbook(string workbookPath);
        public bool UnmountWorkbook(string workbookPath);
        public void Refresh();
        public void StartAssetEditing();
        public void StopAssetEditing();
        public void DisallowAutoRefresh();
        public void AllowAutoRefresh();
        public void ImportAsset(string path);
        public void ImportAsset(string path, ImportAssetOptions options);
        public bool FlushMetadata();
        public bool FlushMetadata(string workbookPath);
        public void SaveAssets();
        public void SaveAssetIfDirty(ExcelDbEngine.Object obj);
        public void SaveAssetIfDirty(GUID guid);
        public void ForceReserializeAssets();
        public void ForceReserializeAssets(IEnumerable<string> assetPaths, ForceReserializeAssetsOptions options);

        public bool IsValidFolder(string path);
        public string CreateFolder(string parentFolder, string newFolderName);
        public string[] GetSubFolders(string path);
        public string[] FindAssets(string filter);
        public string[] FindAssets(string filter, string[] searchInFolders);
        public string[] GetDependencies(string pathName);
        public string[] GetDependencies(string pathName, bool recursive);
        public string[] GetDependencies(string[] pathNames);
        public string[] GetDependencies(string[] pathNames, bool recursive);
        public Hash128 GetAssetDependencyHash(string path);
        public Hash128 GetAssetDependencyHash(GUID guid);
        public string[] GetLabels(ExcelDbEngine.Object obj);
        public void SetLabels(ExcelDbEngine.Object obj, string[] labels);
        public void ClearLabels(ExcelDbEngine.Object obj);
        public string GUIDToAssetPath(string guid);
        public string AssetPathToGUID(string assetPath);
        public string AssetPathToGUID(string assetPath, AssetPathToGUIDOptions options);
        public GUID GUIDFromAssetPath(string assetPath);
        public string GetAssetPath(ExcelDbEngine.Object assetObject);
        public ExcelDbEngine.Object LoadMainAssetAtPath(string assetPath);
        public ExcelDbEngine.Object LoadAssetAtPath(string assetPath, Type type);
        public T LoadAssetAtPath<T>(string assetPath) where T : ExcelDbEngine.Object;
        public ExcelDbEngine.Object[] LoadAllAssetsAtPath(string assetPath);
        public ExcelDbEngine.Object[] LoadAllAssetRepresentationsAtPath(string assetPath);
        public Type GetMainAssetTypeAtPath(string assetPath);
        public bool Contains(ExcelDbEngine.Object obj);
        public bool IsMainAsset(ExcelDbEngine.Object obj);
        public bool IsMainAsset(int instanceID);
        public bool IsSubAsset(ExcelDbEngine.Object obj);
        public bool IsSubAsset(int instanceID);
        public bool IsForeignAsset(ExcelDbEngine.Object obj);
        public bool IsForeignAsset(int instanceID);
        public bool IsNativeAsset(ExcelDbEngine.Object obj);
        public bool IsNativeAsset(int instanceID);
        public bool OpenAsset(int instanceID, int lineNumber = -1);
        public bool OpenAsset(int instanceID, int lineNumber, int columnNumber);
        public bool OpenAsset(ExcelDbEngine.Object target, int lineNumber = -1);
        public bool OpenAsset(ExcelDbEngine.Object target, int lineNumber, int columnNumber);
        public bool OpenAsset(ExcelDbEngine.Object[] objects);

        public void CreateAsset(ExcelDbEngine.Object asset, string assetPath);
        public void AddObjectToAsset(ExcelDbEngine.Object objectToAdd, string path);
        public void AddObjectToAsset(ExcelDbEngine.Object objectToAdd, ExcelDbEngine.Object assetObject);
        public void RemoveObjectFromAsset(ExcelDbEngine.Object objectToRemove);
        public void SetMainObject(ExcelDbEngine.Object mainObject, string assetPath);
        public bool CopyAsset(string path, string newPath);
        public bool DeleteAsset(string assetPath);
        public string MoveAsset(string oldPath, string newPath);
        public string RenameAsset(string pathName, string newName);
        public string GenerateUniqueAssetPath(string path);

        public bool TryGetGUIDAndLocalFileIdentifier(
            ExcelDbEngine.Object obj,
            out string guid,
            out long localId);
        public bool TryGetGUIDAndLocalFileIdentifier(
            int instanceID,
            out string guid,
            out long localId);

        public bool TryGetAssetStatus(
            ExcelDbEngine.Object obj,
            out AssetStatusRecord status);
        public bool TryGetAssetStatus(
            string assetPath,
            out AssetStatusRecord status);
        public bool TryGetWorkbookStatus(
            string workbookPath,
            out WorkbookStatusRecord status);
        public ExcelDbEngine.RuntimeQueryStatus GetAssetDiagnostics(
            ExcelDbEngine.AssetIdentity assetIdentity,
            Span<AssetDiagnosticRecord> buffer,
            out int count);
        public ExcelDbEngine.RuntimeQueryStatus GetWorkbookDiagnostics(
            string workbookPath,
            Span<AssetDiagnosticRecord> buffer,
            out int count);

        public ExcelDbEngine.RuntimeQueryStatus GetConflicts(
            Span<ConflictRecord> buffer,
            out int count);
        public bool TryGetConflict(ConflictId conflictId, out ConflictRecord conflict);
        public bool ResolveConflict(ConflictId conflictId, ConflictResolutionAction action);
        public bool TryOpenConflictResolver(
            ConflictId conflictId,
            ConflictResolutionSeed seed,
            out ConflictResolver resolver);

        public OperationReport GetLastOperationReport();
        public void Dispose();
    }

    public readonly struct AssetDatabaseContextScope : IDisposable
    {
        public void Dispose();
    }
}
```

static facade context access：

```csharp
namespace ExcelDbEngine
{
    public static class RuntimeDatabase
    {
        public static RuntimeDatabaseContext defaultContext { get; }
        public static RuntimeDatabaseContext currentContext { get; }
        public static RuntimeDatabaseContextScope BindContext(RuntimeDatabaseContext context);
    }
}

namespace ExcelDbEditor
{
    public static class AssetDatabase
    {
        public static AssetDatabaseContext defaultContext { get; }
        public static AssetDatabaseContext currentContext { get; }
        public static AssetDatabaseContextScope BindContext(AssetDatabaseContext context);
    }
}
```

显式 context API 默认规则：

- `RuntimeDatabaseContext` 的实例方法集合等价于 `RuntimeDatabase` static API 去掉 `defaultContext/currentContext/BindContext` 后的操作集合；返回值、report、allocation policy、threading 和 event 语义必须一致。
- `AssetDatabaseContext` 的实例方法集合等价于 `AssetDatabase` static API 去掉 context 访问器后的操作集合；不能因为走 instance API 就绕过 Unity-like 返回值、Undo/dirty/save 或 operation transaction。
- static facade 和 instance context API 产生的 report 结构一致；差异只在 static facade 多了一步 current/default context resolution。
- `RuntimeDatabaseContext.changed/objectChanged` 只发布该 context 的 ChangeSet；`RuntimeDatabase.changed/objectChanged` 是 current/default context 的 facade，不跨 context 广播。
- static `RuntimeDatabase.changed += handler` / `-= handler` 按订阅或退订当时的 `currentContext` 绑定；后续 `BindContext` 变化不迁移已有订阅。
- context instance API 返回的 `AssetKey`、`RuntimeLoadSetId`、`RuntimeSnapshot`、resident `Object`、`SerializedObject` 和 `SerializedProperty` 都绑定该 context；不能传给 static facade 在另一个 current context 下继续使用。
- 项目可以只把 instance context API 暴露给工具/测试 assembly，但核心实现内部必须以 context 为状态根，不能以 static class 字段作为唯一事实源。

context 边界默认规则：

- `Object.contextId`、`AssetKey.contextId`、`RuntimeSnapshot.contextId`、`SerializedObject.contextId` 必须用于 debug 校验；Release 可以只保留 generation/token，但错误不能静默通过。
- 把其他 context 的 object 传给 `AssetDatabase.GetAssetPath`、`SerializedObject`、`EditorUtility.SetDirty`、reference setter 或 `RuntimeDatabase.TryGetAsset` 必须失败，并记录对应 `assetdb.context_mismatch`、`serialized.context_mismatch` 或 `runtime.context_mismatch`。
- source `Close()` 后 resident objects 进入 `unloaded`，context 仍可重新 `Open`；context `Dispose()` 后 objects/keys/handles 进入 `context_closed`，旧 `AssetKey`、`SerializedProperty` 和未 disposed snapshot 不能被新 context 复用。
- `AssetIdentity`、Unity asset guid/local file id、workbook guid、row guid/local id 是跨 context 可比较的稳定值；`GetInstanceID()`、object reference equality、array element handle、snapshot handle 只在 context 内有效。
- editor context 与 runtime context 默认不共享 resident object instance 和 dirty draft；Play Mode 打开同一个 Excel source 只共享文件/source identity，不共享可写草稿。
- editor SaveAssets 写回 Excel 后，runtime context 是否看到变化仍通过 watcher/Refresh/source switch 事务，不允许直接偷读 editor draft。
- runtime context hot reload 不修改 editor dirty draft；如果它读取的是同一 Excel 文件，editor context 之后 Refresh 时按外部变化进入 import/merge/conflict 流程。
- operation report 必须写 `context_id`、`context_kind`、`mode`、`source_set_id` 和 `source_revision`；跨 context 错误必须能定位 source context 和 target context。

#### 14.2.2 默认 Unity editor projection / proxy object 协议

Unity adapter 的目标是让 Unity 用户以熟悉的 Project/Object Picker/Inspector 节奏编辑 ExcelDB asset，但 Excel row asset 的 source of truth 仍然是 workbook + metadata。默认不把每一行配置另存为 `.asset` 文件，也不把 Unity cache/proxy 的 guid 当作 ExcelDB identity。

Unity editor projection 默认组件：

```text
ExcelDbUnityProjectState: project-level default editor context binding
ExcelDbAssetIndex: virtual asset path/guid/type/name/label/status index
ExcelDbBrowserWindow: table/project-like browser and status surface
ExcelDbInspectorBridge: generic inspector + custom editor host
ExcelDbObjectPicker: internal asset reference picker
ExcelDbUnityObjectProxy optional: UnityEngine.Object wrapper for ObjectField/Selection/Ping integration
ExcelDbUnityImportHook: workbook/schema/project setting change hook
```

proxy descriptor 默认字段：

```text
proxy_id
proxy_kind: asset | sub_asset | workbook | diagnostic
context_id
source_set_id
workbook_guid
table_id
row_guid
row_local_id
asset_guid
asset_local_file_id
asset_path
runtime_type
display_name
object_state
source_revision
proxy_generation
```

默认规则：

- Unity projection 只读取当前 `AssetDatabaseContext` 的 virtual asset index；它不直接扫描 Excel cell、metadata sheet 或 converted bytes。
- ExcelDB asset guid/local file id 是 workbook guid + table id + row identity 派生的稳定值；Unity proxy asset 的 `.meta` guid、Unity instance id、Unity local file id 都不是 ExcelDB identity。
- 默认不为每个 row 生成持久 `.asset` mirror。若项目启用 proxy cache，cache 只保存 proxy descriptor / display state / Unity integration state；丢失 cache 后必须能从 workbook 和 metadata 完整重建。
- proxy cache 不进入 `source_hash`、`bytes_hash`、`schema_hash`、`layout_hash` 或 build cache key；只可进入 editor projection report / UI cache hash。
- `Selection.activeObject`、`EditorGUIUtility.PingObject`、Project-like browser selection 如果需要 `UnityEngine.Object`，使用 `ExcelDbUnityObjectProxy`；对该 proxy 执行 inspector 编辑时必须转回 `SerializedObject` / `AssetDatabase` facade，不允许直接序列化 proxy 字段作为 source。
- Unity proxy 的 `hideFlags`、icon、preview、foldout、selection state 和 inspector UI state 是 session/editor presentation，不写 workbook。
- proxy 生命周期绑定 source revision 和 context generation。Refresh/source switch/schema layout refresh 后，旧 proxy 必须能标记 stale 并 rebind 到同一 `AssetIdentity`，或在 identity removed 时进入 invalid/missing presentation。
- proxy stale 不等于 asset stale：业务 API 只能以 ExcelDB `Object` / `AssetIdentity` / `AssetKey` 判断数据状态，不能读取 Unity proxy 状态当作数据状态。
- proxy creation/materialization 是 editor projection 或 initialization 路径，可以分配；稳定 Project/browser repaint 和 object picker filter 必须复用 proxy/index buffer，不为每个 row 创建临时对象。

Selection / Ping / ObjectField 默认语义：

- ExcelDB `Selection` 是当前 `AssetDatabaseContext` 的 editor projection state，不是 workbook 数据；设置 selection 不创建 dirty、不写 workbook、不改变 source_hash、不触发 runtime ChangeSet。
- `Selection.activeObject` / `Selection.objects` 返回 ExcelDB `Object` view；Unity adapter 暴露给 `UnityEditor.Selection` 时使用 `ExcelDbUnityObjectProxy`。proxy 被选中后必须能通过 proxy registry 映射回 ExcelDB object / asset identity。
- selection update 默认 all-or-nothing：传入 null 表示清空；传入其他 context、runtime context、closed context、removed/stale object 或 `temporary_imported` preview 时，本次 selection 不改变，并写 context/state report。
- `Selection.activeInstanceID` / `Selection.instanceIDs` 返回当前 context-local ExcelDB instance id，不返回 Unity proxy instance id。Unity adapter 可以另外维护 proxy instance id 映射，但不得把 proxy id 写入 workbook 或 ExcelDB identity。
- `Selection.assetGUIDs` 返回 selection 中可映射到 current main asset guid 的项，按 selection order 去重；sub-asset selection 返回 owning main asset guid。需要 sub-asset 精确身份时使用 selected object + `TryGetGUIDAndLocalFileIdentifier`。
- `Selection.selectionChanged` 在 editor projection state commit 后发布，并在 `StartAssetEditing` batch / refresh drain 中 coalesce；它不表示数据源发生变化，数据变化仍以 operation report / ChangeSet / status index 为准。
- `EditorGUIUtility.PingObject(Object/int)` 只请求 UI 定位：优先定位 ExcelDB browser row/sub-asset entry；Unity adapter 可同时 ping proxy。Ping 不改变 selection、不打开 workbook、不刷新 source、不 repair metadata。
- Unity `ObjectField` 用于 ExcelDB internal reference 字段时，显示和拖拽对象可以是 proxy，但写回必须转成 ExcelDB stable identity / row guid/local id；不能把 `UnityEngine.Object`、proxy instance id、proxy `.meta` guid 或 Unity local file id 保存进 workbook。
- 对 `UnityResourceRef` 字段，ObjectField 使用真实 Unity object；写回的是 `{guid, main_asset_path, optional sub_asset_local_file_id/name/type}`，仍不把 Unity object reference 作为 ExcelDB object identity。
- selection/ping/object-field projection 可以在 editor UI 层分配；稳定 repaint、picker filter、selection membership test 和 proxy registry lookup 必须复用 buffer，达到水位线后 core projection/report append 不产生 GC allocation。

Project / browser 默认语义：

- Unity Project window 无法原生显示虚拟 row asset 时，Unity adapter 必须提供 `ExcelDbBrowserWindow` 或等价 Project-like surface；是否额外生成 Project window cache asset 是 adapter feature，不改变 core contract。
- browser tree 默认按 mounted workbook root -> table display/schema name -> escaped key path 展示；排序与 `FindAssets` current asset path 排序一致。
- 双击 row asset 默认打开 generic/custom inspector；可选命令可以打开 Excel 到对应 workbook/sheet/row，但这只是编辑便利，不改变 selection identity。
- browser 必须显示 status flags：dirty、conflict、validation error、missing reference、temporary identity、convert blocker；这些来自 status index，不从 human-readable report 文本解析。
- invalid raw row、duplicate key loser、`temporary_imported` row 不进入普通 `FindAssets`，但 browser/table diagnostic view 必须能显示它们并定位 workbook/sheet/row/cell。

Object picker 默认语义：

- ExcelDB internal reference field 使用 `ExcelDbObjectPicker`，候选来自 target scope 的 virtual asset index，而不是 UnityEditor.AssetDatabase project-wide scan。
- picker filter 语法与 `FindAssets` 一致，并额外应用 reference descriptor 的 target table/group/runtime type/scope/nullability/list duplicate policy。
- picker 选择结果写入 target `AssetIdentity` / row guid/local id metadata；显示 token/key/path 只是 projection。
- picker 允许 `None` 只在 schema `nullable=true` 或 reference policy 允许时出现；否则清空选择产生 validation error。
- `UnityResourceRef` 字段使用 Unity `ObjectField` / Unity asset picker，但写回仍是 `{guid, main_asset_path, optional sub_asset}`；选择 Unity object 时 adapter 解析 guid/local file id/path，不能保存 `UnityEngine.Object` 引用到 workbook。

Inspector / custom editor 默认语义：

- Generic inspector 使用 `ExcelDbEditor.SerializedObject` / `SerializedProperty`，不是 UnityEngine.SerializedObject 直接反射 proxy。
- Unity adapter 可以提供桥接 drawer，让 Unity IMGUI/UI Toolkit 控件读写 ExcelDB `SerializedProperty`；所有写入仍进入 pending property buffer、Undo、dirty、validation 和 SaveAssets 生命周期。
- 自定义 Unity editor 必须声明 editor capability / drawer registry descriptor；不能在 `OnInspectorGUI` 中直接写 workbook cell、proxy serialized field 或 runtime resident storage。
- Inspector repaint 稳定路径不得执行 import、Refresh、SaveAssets、source switch、Unity project scan 或全表 validation；这些必须是显式 command 或 scheduled operation。
- Multi-object edit 是完整目标版默认支持的 Unity-like editor surface；只有目标集合、field policy、drawer capability 或项目 profile 明确不允许写入时才降级为 read-only / table_view，并且必须按 14.24 产出稳定 diagnostic。

Unity import hook 默认语义：

- `.xlsx/.xlsm` 变化、schema source 变化、project policy 变化和 Unity asset dependency 变化可以触发 Unity adapter hook，但 hook 只能 enqueue refresh/check/convert request，不在 AssetPostprocessor callback 中 patch resident object 或写 workbook。
- Unity adapter 的 import hook 与 14.14 watcher 共用 transaction/read-verify/classifier；不能形成第二套 Excel import path。
- Unity asset rename/move 只触发 UnityResourceRef display auto-fix proposal；guid 不变时不改变 reference identity。
- Unity domain reload 后必须从 workbook metadata、project policy、descriptor registry 和 optional proxy cache 重建 editor context；不能依赖静态字段保留事实状态。

### 14.3 默认 workbook 布局

默认 workbook 由三类 sheet 组成：

- table sheet：策划日常编辑的可见表。
- `__ExcelDB_Metadata`：系统 metadata sheet，默认 hidden / very hidden。
- 可选 generated helper sheet：例如 enum 大量可选值说明、`__ExcelDB_Diagnostics`；这些 sheet 必须有 metadata 标记，不能被当作 table sheet。

table sheet 默认结构：

```text
第1行：display name / 中文名
第2行：field path / 字段路径
第3行：value shape / 类型与展开方式
第4行：export policy / 导出端
第5行：validation / 规则
第6行：description / 说明
第7行：header comment / 注释
第8行以后：正式数据行
```

默认规则：

- 第 1-7 行是 schema-owned structure region，由 schema 生成和修正。
- field id 不依赖 visible header text，默认记录在 `__ExcelDB_Metadata`；可额外生成 hidden row/column 方便调试，但 importer 不能只依赖它。
- data region 从第 8 行开始，生成结构时不得清空、重排或隐式改写正式数据。
- 不依赖 merged cells 进行导入；merged cells 只能作为显示样式，语义以 metadata 和 column mapping 为准。
- simple struct 的 `expanded_columns` 默认使用 `parent.child` 字段路径，例如 `range.min`、`range.max`。
- simple struct 的 `single_cell` 必须在 header comment 中给出格式和示例。
- enum 字段必须生成 Excel data validation dropdown；可选值过多时，header comment 给摘要，并指向 generated enum helper sheet。

#### 14.3.1 默认 schema structure generation / layout refresh 协议

schema generation 不是“按当前 schema 重建整张 sheet”，而是在现有 workbook 上构建一个结构 patch。它只写 schema-owned structure、ExcelDB-owned helper artifact、metadata 和必要 system companion columns；正式数据行、未知/helper/freeform 信息默认保留。

默认操作入口：

```text
GenerateStructure
LayoutRefresh
MetadataFlush
MigrationApply
SaveAssets preflight auto-fix
CleanupDeprecatedLayout
```

默认流程：

1. 读取 current `SchemaDescriptor`、generated layout descriptor、workbook metadata、table sheet、system companion columns。
2. 识别 table：优先 metadata table id，其次 hidden/generated table id marker，再其次 schema name / alias；不能唯一匹配时 blocker。
3. 识别 field column group：优先 metadata field id，其次 ExcelDB-owned hidden field id marker，其次 field path / alias；不能唯一匹配时 blocker。
4. 生成 structure diff：sheet create/rename、header rows、field column group、data validation、helper sheet、system companion columns、metadata mapping。
5. 分类 safe / warning / error / blocker。
6. 只对 safe 和被 policy 允许的 warning 生成 patch；error/blocker 不写 workbook。
7. 通过 operation transaction + workbook backup / atomic save / recovery 协议提交。
8. 提交后重新打开 workbook，复读 table/field/row mapping、metadata checksum、system columns 和 affected header/dropdown。

table 匹配默认规则：

- table identity 是 schema table id，不是 sheet name。
- sheet rename 只更新 metadata current sheet name 和 report，不改变 table id。
- 同一 table id 匹配多个 sheet 是 `structure.table_mapping_ambiguous` blocker。
- sheet 名与 schema name 不一致但 table id 唯一时，默认保留当前 sheet name；如果 schema 声明 `enforce_sheet_name=true`，作为 layout refresh rename sheet。
- 新 table 缺 sheet 时可以创建新 sheet；创建位置按 schema table order，已有 sheet 顺序默认不强制重排。

field column group 默认规则：

- scalar / enum / reference single-cell 字段占一个 schema field column group。
- simple struct `expanded_columns` 占一个父 field group，内部按 subfield id/order 生成多个 child columns。
- repeated horizontal columns 占一个 field group，宽度来自 schema fixed/max size；可变复杂 repeated 默认 child table。
- `UnityResourceRef` 默认占 `field.main_asset_path` + `field.guid` 两列，属于同一 logical field group。
- system companion columns 不属于 field group，不进入 import/export/runtime object data。
- deprecated/reserved field column 可以保留为 ignored/deprecated group；除非 migration 明确删除，否则 generation 不直接丢弃其数据。

column matching 默认规则：

```text
metadata field id
-> hidden/generated field id marker
-> exact schema field path
-> declared alias
-> no match
```

- field id 匹配成功时，header 文本、display name、类型行、导出端行可以被 schema 修正。
- field path / alias fallback 只允许在 metadata 缺失或待 repair 时使用；成功后必须写回 metadata field id。
- 同一个 field id 匹配多个 column group 是 `structure.field_mapping_ambiguous` blocker。
- 一个 column group 匹配多个 current field 是 `structure.field_mapping_ambiguous` blocker。
- 不按中文名、列位置、Excel 样式或相似字符串猜字段身份。

header row patch 默认规则：

```text
row 1 display name
row 2 field path
row 3 value shape
row 4 export policy
row 5 validation summary
row 6 description
row 7 header comment
```

- 第 1-7 行对应 schema field group 的 cell 是 schema-owned；generation 可以覆盖这些 cell。
- 正式数据区域第 8 行以后不因 header refresh 被清空、批量默认填充或重排。
- header comment 行写 schema 说明、枚举可选值摘要、simple struct 格式示例和 reference 填写提示。
- 过长 enum / reference 选项写入 generated helper sheet；header comment 写摘要和 helper sheet 名。
- Excel data validation dropdown 属于 schema-owned artifact；仅更新 schema 管辖字段的 validation，不覆盖用户普通数据 cell 的其他 validation。
- 用户要写自由说明时默认写到 helper/freeform 区域或 schema 声明的备注列，不写进第 1-7 行的 schema-owned cells。

column order 默认规则：

- 默认 policy 为 `preserve_existing_known_columns`：已能用 field id 匹配的字段列保持当前物理顺序，只刷新 schema-owned header/dropdown/metadata。
- 新增字段默认插入到 schema 相邻字段之间；如果相邻字段因手工调整不再形成稳定插入点，则追加到最后一个 schema field group 之后、system companion columns 之前，并报告 `layout.column_appended_due_to_manual_order`。
- schema 可以声明 `enforce_column_order=true`；此时 layout refresh 可以按 schema order 移动完整 field column group，但必须连同 data cells、style、formula、comment、data validation 一起移动，不得只移动表头。
- 如果 column move 会覆盖 unknown/helper/freeform column、merged range、formula dependency 或无法被 xlsx backend 保真执行，默认不移动并产生 `layout.column_move_skipped` warning；field mapping 仍按 metadata/field id 保持有效。
- 如果 descriptor 声明 `enforce_column_order=true` 且某个 editor capability / export adapter 声明 `requires_physical_column_order=true`，任何无法保真移动的 field group 都使本次 layout refresh 失败；operation profile 只能把 warning 升级为 error/blocker，不能把默认 patch 从“跳过移动”改成“覆盖 helper/freeform”。
- 当新增字段和手工列顺序冲突时，默认选择安全追加而不是重排；只有所有受影响 field group、helper/freeform range、drawing/table anchor、formula dependency 都可保真移动时，才执行列移动。
- column order 变化不改变 field identity、row identity、source hash；只影响 layout hash 和 authoring display。

helper/freeform 保留默认规则：

- schema 未声明但不参与 field mapping 的 column 默认保留；如果它位于 data region 且含数据，产生 `layout.unknown_column_preserved` warning。
- schema 声明 ignored/helper column 后，该列可以带样式、批注、公式和策划备注；import/export/runtime 忽略其语义。
- helper column 与 schema field insertion/move 目标重叠时，不静默覆盖；尝试寻找下一个安全插入点，找不到时 `structure.helper_region_overlap` blocker。
- pre-header helper/freeform rows 默认不是 schema-owned structure。它们只有在 table descriptor、layout metadata、generated marker 或 repair proposal 能证明其范围时才作为 helper/freeform region 保留；否则 generation 不把这些行当作 schema facts，也不静默删除，而是产生 layout warning/blocker 并提示登记 `pre_header_row_count` 或执行 layout repair。
- images/charts/pivot/table objects 默认保留；如果它们锚定到要移动的 schema field group，move 前必须确认后端能保真移动，否则降级为不移动列并报告。

missing / deleted field 默认规则：

- 新增 nullable / optional default 字段可以生成列，但不批量写第 8 行以后的默认值；读取时 materialize default/null。
- 新增 required 且无 default 字段可以生成列，但正式数据缺值产生 validation error，convert/build blocker。
- 删除字段且 descriptor 中 reserved/deprecated 时，旧列默认保留为 ignored/deprecated group，不导出。
- 删除字段且未 reserved、旧列含数据时，需要 migration 或 data-loss confirmation；generation 不直接清空或删除列。
- 删除字段且旧列全空，也必须产生 report；`GenerateStructure` / `LayoutRefresh` 默认仍保留物理列，只把它标记为 cleanup candidate。

cleanup 默认规则：

- `CleanupDeprecatedLayout` 是显式 operation，不由 `GenerateStructure`、`LayoutRefresh`、`SaveAssets` 或 `MigrationApply` 顺手触发；默认 profile 不自动删除任何物理列、sheet、row、comment、style、validation 或 helper/freeform 内容。
- cleanup 只允许处理 ExcelDB-owned generated artifact、已 reserved/deprecated 且 canonical data 全空的 field group、metadata 已确认不再引用的 named range/data validation，以及显式 migration plan 声明可删除的 schema-owned layout artifact。
- cleanup 不删除正式数据行，不删除 unknown/helper/freeform column，不删除含公式/comment/style/validation 的用户区域；即使单元格值为空，只要区域 ownership 不是 ExcelDB-owned，也只能保留并报告。
- cleanup 必须先 dry-run 输出 cleanup candidates、ownership proof、emptiness proof、metadata reference proof 和 data_loss_risk；没有 dry-run report hash 或 confirmation hash 时 apply 拒绝执行。
- cleanup apply 必须验证 dry-run report hash、source revision、schema/layout hash 和 package fingerprint 仍匹配；不匹配产生 `transaction.plan_stale` / `transaction.source_revision_changed`，并不写 workbook。
- cleanup 成功只影响 layout hash、metadata checksum 和 package fingerprint；不改变 schema hash、source hash、converted bytes data hash 或 runtime object identity。
- operation profile 可以禁止 cleanup 或把 cleanup warning 升级为 blocker，但不能允许 cleanup 覆盖未知/用户区域；需要丢弃正式数据时必须走 migration data-loss confirmation，不属于 cleanup。

生成 report 默认字段：

```text
structure_diff[]
column_mapping_before[]
column_mapping_after[]
preserved_helper_ranges[]
created_columns[]
updated_header_cells[]
updated_data_validations[]
updated_helper_artifacts[]
updated_named_ranges[]
skipped_column_moves[]
cleanup_candidates[]
cleanup_actions[]
data_loss_risk
```

默认 diagnostic code：

- `structure.table_mapping_ambiguous`：table id / sheet mapping 无法唯一确定。
- `structure.field_mapping_ambiguous`：field id / column group mapping 无法唯一确定。
- `structure.required_column_missing`：required field 缺列且不能安全生成或没有 default/migration。
- `structure.data_region_ambiguous`：无法确定第 8 行以后哪些是正式数据。
- `structure.helper_region_overlap`：helper/freeform 区域与必要 schema patch 冲突。
- `layout.column_appended_due_to_manual_order`：因保留手工列顺序，新字段追加到安全位置。
- `layout.column_move_skipped`：schema 想移动列，但当前 workbook 无法保真移动。
- `layout.helper_artifact_mismatch`：schema-owned helper sheet / named range 与 layout descriptor 不一致。
- `layout.data_validation_mismatch`：schema-owned Excel data validation 与 layout descriptor 不一致。

#### 14.3.2 默认 schema-owned header comment / data validation / helper artifact 协议

表头注释、枚举下拉和辅助说明不是策划手工维护的“美化内容”，而是 schema 的可见投影。它们默认只影响 authoring layout，不改变正式数据语义；但它们必须有稳定物理身份，否则 layout refresh、热切换和冲突判断都会分叉。

schema-owned artifact 默认集合：

```text
header row cells: row 1-7 中 schema field group 对应 cell
cell note/comment: 可选，仅用于 richer tooltip，必须带 ExcelDB marker
data validation: schema field data region 的 dropdown / input rule / error rule
generated helper sheet: enum/reference/codec 说明和长选项列表
defined names: data validation 使用的稳定 named range
metadata artifact record: artifact id、kind、owner table/field、layout hash、checksum
```

generated helper sheet 默认命名：

- `__ExcelDB_Helpers`：默认承载 enum value、reference picker 摘要、single-cell codec 示例、长注释。
- `__ExcelDB_Diagnostics`：只承载 diagnostic 投影，继续按 diagnostic helper sheet 协议处理。
- 项目可以声明拆分策略，例如 per workbook / per table helper sheet；但 sheet 必须是 hidden 或 very hidden，且必须在 metadata 中登记为 generated helper sheet。
- 任何以 `__ExcelDB_` 开头但没有合法 metadata marker 的 sheet，不能被当作可信 generated helper sheet；layout refresh 必须报 `layout.helper_artifact_mismatch`，并按普通未知 sheet 保护，除非用户显式 repair。

helper sheet 物理格式默认规则：

```text
cell A1: EXCELDB_HELPER
cell B1: format_version
cell C1: helper_schema_version
cell D1: descriptor_hash
cell E1: layout_hash
cell F1: helper_artifact_checksum

blocks:
  enum_values
  reference_options
  single_cell_codecs
  field_comments
  defined_ranges
```

- helper block 使用固定列名、canonical text、稳定排序；不依赖 Excel 显示格式。
- `enum_values` 至少包含 enum id、value id、export token、display name、aliases、deprecated、description、sort order。
- `reference_options` 至少包含 owner field id、target family、target table/group/runtime type、target identity、canonical key token、display token、deprecated/missing flag、runtime provider key summary、sort order 和 source hash。
- `single_cell_codecs` 至少包含 codec id/version、format summary、examples、escaping/null/missing 规则。
- `field_comments` 保存过长 header comment 的完整内容；table sheet 第 7 行只写摘要和 helper anchor。
- `defined_ranges` 保存 generated named range 的 logical id、Excel name、target range、owner field id、checksum。
- helper sheet 中 ExcelDB-owned block/range 可以被 layout refresh 覆盖；非 ExcelDB-owned 区域如果被项目允许存在，必须位于 metadata 登记的 helper/freeform range。

defined name / data validation 默认规则：

- enum dropdown 默认使用 helper sheet 上的 named range，不使用逗号拼接的 inline list；这样可选值变多、含逗号或本地化文本时仍稳定。
- generated named range 使用稳定 logical id 派生，例如 `_xdb_enum_<enum_id_hash>`、`_xdb_field_<field_id_hash>_options`；Excel name 只是物理名，metadata 中的 logical id 才是身份。
- data validation 只附加到 schema field 的 data region；不会覆盖 unknown/helper/freeform column 的用户 validation。
- schema field data region 已有非 ExcelDB-owned validation 时，layout refresh 不静默覆盖；如果该 validation 与 schema validation 等价，写回 ExcelDB marker；不等价则报 `layout.data_validation_mismatch`，默认保留用户 validation 并要求用户确认或执行 repair。
- column move 必须连同 data validation 和 named range 引用一起移动；如果后端不能保真移动，按 `layout.column_move_skipped` 处理。

reference helper / dropdown 默认规则：

- internal reference 字段可以生成 reference options helper range，但它是 authoring suggestion，不是引用身份事实源。
- reference options 的候选来自当前 workbook source set 的 identity/key/reference index 和 reference descriptor target scope；不能从 visible token 列、sheet 当前排序或 Excel dropdown 内容反推候选集合。
- dropdown token 默认使用 target canonical key / asset path display；target identity 通过 companion metadata、hidden reference identity cell 或 explicit picker result 保存。visible token 与 hidden identity 不一致时按 `reference.display_mismatch` 处理，不自动重绑定。
- 对 target 数量超过 data validation 实用上限、需要跨 workbook/filter 条件、或者候选依赖当前行字段的 reference，默认不生成完整 dropdown；row 7/header comment 写填写规则和 picker/search 入口，helper sheet 写摘要和候选查询 descriptor。
- nullable reference 可以在 dropdown 中显示 schema-defined empty/null token；non-null reference 清空或选择 empty token 产生 validation error，不把空 token 当 missing target。
- soft reference / deprecated target 可以显示在 helper sheet 中并带 deprecated/missing flag；hard reference 指向 missing target 仍由 import/validation 产生 `reference.missing_target`。
- data validation 不能替代 validator：Excel 允许用户粘贴任意文本或禁用 validation，import 仍必须按 schema resolver 解析并报告 ambiguity/missing/type mismatch。
- reference options source hash 只影响 layout hash、helper checksum 和 data validation repair；target identity/key/reference canonical value 的变化才影响 source_hash/runtime diff。
- UnityResourceRef 不使用 Excel dropdown 作为主选择方式；helper 可以显示 guid/path/provider key 摘要，真正选择由 Unity adapter picker 或显式 guid/path 输入完成。

header comment 默认内容：

- row 7 是最小稳定载体，必须能在不支持 Excel note/comment 的工具链中被读取和 diff。
- 可选 Excel note/comment 只能作为 row 7 的 richer presentation；它必须包含 ExcelDB marker、artifact id 和 checksum，不能成为唯一事实源。
- comment 内容默认来自 field display、description、required/default/nullability、validation summary、export policy、enum 可选值、simple struct 格式、reference 填写规则和 UnityResourceRef guid/path 规则。
- enum value 数量少时，row 7 可以列出全部可选值；数量多或文本过长时，row 7 写摘要、deprecated 提示和 helper anchor，完整列表在 helper sheet。
- simple struct `single_cell` 必须写 canonical 格式、示例和 escaping/null/missing 规则；`expanded_columns` 必须给父字段摘要和每个 child column 注释。
- schema-owned row 1-7 cell 的文本可被生成器覆盖；策划自由注释默认写到 helper/freeform column、schema 声明的备注列或项目允许的 pre-header helper rows。

刷新和冲突分类默认规则：

- helper sheet 丢失但 metadata、layout descriptor、table/field mapping 可确认时，可以重新生成，产生 `layout.helper_artifact_mismatch` warning/info；正式数据不受影响。
- helper sheet checksum 不一致，但差异只在 ExcelDB-owned block，layout refresh 可以覆盖并记录 updated helper artifact。
- helper sheet 中出现非 ExcelDB-owned 内容且位于将被覆盖的 generated range，是 `structure.helper_region_overlap` blocker；不能用“重新生成”吞掉策划手写信息。
- row 7 / note/comment 与 schema 不一致时，优先以 schema 重写 schema-owned row 7，并报 `layout.schema_owned_header_mismatch`；如果检测到非 ExcelDB-owned Excel comment，保留 comment，schema 内容回写 row 7 / helper sheet，并报 `diagnostic.user_comment_preserved`。
- named range 指向错误 helper range、data validation 引用旧 range、dropdown 缺失或过期时，报 `layout.data_validation_mismatch`；可保真修复时作为 layout refresh patch，不能修复时保留 workbook 并输出 report。
- helper artifact、named range、data validation 的修复只更新 layout hash，不改变 schema hash/source hash；但如果 schema enum value id/export token/codec 语义改变，必须按 schema compatibility/migration 规则处理。
- reference options helper range 增删候选、排序或 display token 刷新只更新 layout hash/helper checksum；已填写数据 cell 的 hidden identity/canonical reference value 不变时，不产生 runtime property_changed，也不要求 converted bytes cache miss。

layout refresh 后必须复验：

- 重新打开 workbook，读取 helper sheet marker、metadata artifact record、checksum、defined names、data validation range。
- 抽查或全量校验 affected schema field 的 row 1-7 header cell 与 descriptor 一致。
- 校验 enum dropdown 对应的 named range 内容与 enum descriptor 一致，包括 deprecated value 的显示策略。
- 校验 helper/freeform range 没有被 schema-owned patch 覆盖。

### 14.4 默认 metadata 格式

`__ExcelDB_Metadata` 默认保存七类记录，格式可以是多块 table 或同 sheet 分区，但字段语义必须稳定：

1. workbook record：
   - workbook guid。
   - schema hash。
   - layout hash。
   - generator version。
   - last successful import revision。
   - next row local id。
   - identity allocator revision。

2. table record：
   - table id。
   - schema name。
   - current sheet name。
   - table version。
   - data start row。
   - table kind / export policy。

3. field record：
   - table id。
   - field id。
   - field path。
   - column index。
   - value shape。
   - parent field id，适用于 simple struct / nested field。
   - deprecated / reserved 状态。

4. row record：
   - table id。
   - row guid / local id。
   - current row number。
   - key snapshot。
   - row revision。
   - source hash 或 cell range hash。
   - identity state：stable / temporary / deleted / repaired。

5. migration history record：
   - migration id。
   - migration version。
   - migration descriptor hash。
   - from schema hash / layout hash。
   - to schema hash / layout hash。
   - operation id。
   - plan hash。
   - dry-run report hash。
   - applied tool version。
   - affected table ids / field ids。
   - data_loss_risk。
   - result。
   - report hash。

6. system column record：
   - table id。
   - sheet name。
   - column kind / index。
   - header text。
   - hidden / protected。
   - purpose。

7. generated artifact record：
   - artifact id。
   - artifact kind：helper_sheet / named_range / data_validation / cell_comment / diagnostic_projection。
   - owner table id / field id。
   - sheet name。
   - cell range / named range。
   - descriptor hash / layout hash。
   - artifact checksum。
   - generated by tool version。
   - protected / repair policy。

hash 规则：

- `schema hash` 只覆盖会影响 import、export、runtime 解释、reference、validation blocker 的语义。
- `layout hash` 覆盖 display name、description、header comment、列顺序、冻结窗格、筛选、Excel authoring layout。
- 只改注释、中文名或表头说明不应导致 converted bytes schema hash 不兼容，但可以触发布局刷新。

#### 14.4.1 默认 metadata sheet 物理格式 / companion identity 协议

`__ExcelDB_Metadata` 不是给策划手填的表，而是 workbook identity ledger。v1 默认使用 plain worksheet ranges，不依赖 Excel structured table 对象、命名区域或公式，避免不同 xlsx 后端支持程度不同。

metadata sheet 与 table sheet hidden companion columns 的关系必须固定：metadata sheet 是完整 identity ledger，hidden companion cells 是随 Excel 数据行移动的 row-local identity anchor。import 先读取 companion anchor 来定位现场行，再和 metadata ledger reconcile；二者任何不一致都不是“择一覆盖”，而是进入 repair analyze / blocker / explicit metadata flush。这样可以同时支持策划排序、复制、剪切行，又不让隐藏列被手改后静默制造新资产。

metadata sheet header 默认格式：

```text
A1 = EXCELDB_METADATA
B1 = format_version
C1 = 1
D1 = metadata_schema_version
E1 = 1
F1 = metadata_checksum
G1 = <sha256 lowercase hex over canonical metadata rows, excluding G1>
```

block 默认顺序固定：

```text
[workbook]
[tables]
[fields]
[rows]
[migrations]
[generated_artifacts]
[system_columns]
```

每个 block 使用一行 marker、一行 header、N 行 record、一个空行分隔。marker 写在 A 列，格式为 `[block:<name>]`；header 使用稳定英文列名；record cell 均使用 canonical text，不依赖 Excel number/date formatting。

`[block:workbook]` 默认列：

```text
workbook_guid
descriptor_hash
schema_hash
layout_hash
generator_version
metadata_revision
source_revision
last_successful_import_revision
next_row_local_id
identity_allocator_revision
created_tool_version
last_write_operation_id
```

`[block:tables]` 默认列：

```text
table_id
schema_name
sheet_name
table_version
table_kind
export_policy
data_start_row
schema_owned_column_count
companion_column_start
row_count
table_flags
```

`[block:fields]` 默认列：

```text
table_id
field_id
field_path
column_index
column_span
value_shape
codec_id
parent_field_id
export_policy
deprecated_state
reserved_reason
```

`[block:rows]` 默认列：

```text
table_id
row_guid
row_local_id
identity_state
current_sheet_name
current_row_number
key_snapshot
row_revision
source_hash
cell_range_hash
last_import_operation_id
deleted_at_revision
```

`[block:migrations]` 默认列：

```text
migration_id
migration_version
migration_descriptor_hash
from_schema_hash
from_layout_hash
to_schema_hash
to_layout_hash
operation_id
plan_hash
dry_run_report_hash
applied_tool_version
affected_table_ids
affected_field_ids
data_loss_risk
result
report_hash
```

`[block:generated_artifacts]` 默认列：

```text
artifact_id
artifact_kind
owner_table_id
owner_field_id
sheet_name
cell_range
defined_name
descriptor_hash
layout_hash
artifact_checksum
generated_tool_version
protected
repair_policy
```

`[block:system_columns]` 默认列：

```text
table_id
sheet_name
column_kind
column_index
header_text
hidden
protected
purpose
```

排序默认规则：

- block 顺序固定，不能按写入时碰到的顺序输出。
- workbook block 只有一行。
- tables 按 `table_id` 升序。
- fields 按 `table_id`、`field_id` 升序。
- rows 按 `table_id`、`row_local_id` 升序；没有 stable local id 的 `temporary_imported` row 排在该 table 末尾，并按 session-local identity token 排序。
- migrations 按 apply revision、operation id、migration id/version 排序。
- generated artifacts 按 `artifact_kind`、`owner_table_id`、`owner_field_id`、`artifact_id` 排序。
- system columns 按 `table_id`、`column_index` 排序。

canonical text 默认规则：

- guid / hash 使用 lowercase hex。
- uint64 / int32 使用 invariant decimal text。
- bool 使用 `true` / `false`。
- list 字段使用 `|` 分隔的 sorted canonical tokens；token 内的 `|`、`\` 必须反斜杠转义。
- 空值使用空 cell；显式空字符串使用 `""`；两者不能混淆。
- 不写入当前时间、本机绝对路径、用户名、Excel 计算环境等非 deterministic 内容。

table sheet hidden companion columns 默认规则：

- 每个 table sheet 默认在 schema-owned 字段列之后生成 ExcelDB-owned hidden companion columns。
- v1 必需 companion columns 为 `__xdb_row_guid`、`__xdb_row_local_id`、`__xdb_row_revision`、`__xdb_identity_state`。
- companion columns 属于 system-owned structure，不是 schema field，不进入 `FindProperty`、convert bytes 或 runtime object data。
- companion column 的位置记录在 `[block:system_columns]`；生成结构时可以移动它们，但必须同步 metadata。
- data row 的 companion cells 是现场行匹配的第一读取来源；`[block:rows].current_row_number` 只是加速和诊断定位，不是身份事实源。
- 策划排序、筛选、剪切、复制数据行时，companion cells 应随行移动；import 以 companion anchor 匹配 metadata row record，并验证 ledger / anchor / row content fingerprint 一致。

metadata 读取默认规则：

- 先验证 header、format_version、metadata_schema_version、metadata_checksum。
- format_version 大于当前支持版本时，默认 read-only open；任何写回、metadata repair、convert 都进入 blocker，除非有显式 metadata migration。
- metadata_checksum mismatch 是 blocker；只允许在用户确认的 repair operation 中重建。
- companion identity 与 `[block:rows]` 冲突时，不自动选择任一方覆盖另一方；先进入 repair analyze。能通过 row guid/local id、key snapshot、source_hash 和 row content fingerprint 唯一证明只是 row number stale 时，只更新 current row number；否则 blocker。
- companion columns 缺失但 `[block:rows]`、key snapshot、source_hash 能唯一匹配数据行时，可以生成 repair proposal；不能唯一匹配时 `metadata.row_identity_ambiguous` blocker。
- duplicate row_guid / row_local_id 不自动改正式数据；只能按 14.8.1 的复制行识别规则 repair。

metadata 写回默认规则：

- metadata sheet 是 system-owned，可以由 schema generation、metadata flush、SaveAssets、migration、repair operation 重写整个 metadata sheet。
- 重写 metadata sheet 不能改正式数据 cell；table sheet companion cells 只在 identity flush/repair 时写入。
- 每次写回递增 `metadata_revision`；改变导入语义的数据写回递增 `source_revision`。
- 写回后必须重新读取 header、block count、record count、checksum、companion column mapping 和本次 affected row identity。
- 如果 metadata 写回失败，本次 operation 不能标记成功；涉及新行或 repaired identity 时必须保留 dirty/repair state。

### 14.5 默认 operation transaction 流程

所有危险操作默认走同一流程：

1. Analyze：只读 workbook/source/runtime state，读取 schema 和 metadata，并执行 schema compatibility analyze、mode/mutability check、source revision check。
2. Build candidate：构建 candidate workbook/source/object graph，不修改当前状态。
3. Diff：生成 structure/data/metadata/runtime diff。
4. Classify：把每个 diff 标成 safe、warning、error、blocker。
5. Preflight report：输出 dry-run / preview 可用的 machine-readable `PreflightReport` 和可选 human-readable projection，绑定 `operation_plan_hash`，但不声称 side effect 已提交。
6. Pre-commit：准备 backup、临时文件、buffer reserve、identity map、event buffer。
7. Commit：原子替换 workbook/source/object graph 或 patch resident objects。
8. Verify：复读关键 metadata、schema hash、identity map、reference graph。
9. Publish：提交 dirty state、change event、source/cache/status index，并完成 `FinalOperationReport`。
10. Rollback：任一步失败时保留旧 workbook/source/object graph。

operation report 生命周期默认分类：

- `OperationPlan`：Analyze / Build candidate / Diff / Classify 的可执行计划，描述将要写什么、需要哪些 precondition、如何 verify 和 rollback。
- `PreflightReport`：dry-run / preview / CI preflight 的事实记录，包含 diagnostic、diff summary、proposal、capacity requirement、data loss confirmation 和 `operation_plan_hash`；它不能包含 committed revision 或 publish 结果。
- `FinalOperationReport`：commit / verify / publish 后的事实记录，包含实际 saved/skipped/failed/restored subject、committed revision、source_hash_after、metadata_checksum_after、ChangeSet digest、reimport_report_hash 和 final result。
- `GateResultReport`：用某个 gate policy 对 `PreflightReport` 或 `FinalOperationReport` 的重判定，引用原 `report_hash` 和新的 `gate_policy_hash`，不得改写原 report。

`report_hash` 默认冻结点：

- dry-run / preview 只冻结 `PreflightReport.report_hash`。
- commit 成功或失败后冻结 `FinalOperationReport.report_hash`；失败 report 也必须记录失败 phase、rollback/recovery 状态和未提交 side effect。
- human-readable 文本、Excel diagnostic projection、UI tree、debug trace 和 timing 不进入任何 core report hash。

默认提交规则：

- safe 自动提交。
- warning 可以继续 import/open；结构写回在 editor UI 中需要明确确认，在 CLI/CI 中由参数或 policy 控制。
- error 允许 workbook 打开和 report 展示，但相关 asset/source 不可 convert，不可作为成功 hot reload/source switch 结果发布。
- blocker 不写 workbook，不替换 runtime source，不清空数据，不发布 change event。

#### 14.5.0 默认 OperationPlan / patch proposal 协议

operation transaction 的核心可执行产物是 machine-readable `OperationPlan`。Analyze / Build candidate / Diff / Classify 只生成 plan、`PreflightReport` 和 proposal；Commit 阶段只允许执行 plan 中声明的 patch proposal，不能重新扫描后临时决定额外写入。Verify / Publish 完成后必须生成或完成 `FinalOperationReport`，记录实际 side effect 和最终状态。

`OperationPlan` 默认字段：

```text
operation_plan_format_version
operation_kind
operation_profile_id
operation_profile_hash
gate_policy_hash
context_id
base_revisions[]
input_hashes[]
candidate_hashes[]
affected_subjects[]
auto_fix_proposals[] optional
patch_proposals[]
expected_side_effects[]
preflight_requirements[]
data_loss_confirmations[]
capacity_requirements[]
verification_expectations[]
operation_plan_hash
```

`operation_plan_hash` 默认规则：

- `operation_plan_hash = sha256("exceldb-operation-plan-v1" + canonical OperationPlan bytes without operation_plan_hash)`。
- `operation_id`、当前时间、用户名、临时文件实际随机后缀、human-readable report 文本不进入 plan hash。
- 相同 base revision、schema/profile、candidate diff 和 patch proposal 在不同机器上必须生成相同 plan hash。
- dry-run report、migration plan、SaveAssets preview、source switch preview 都必须引用 plan hash；commit/apply 必须验证 plan hash 未变化。

`base_revisions[]` 默认字段：

```text
subject_kind: workbook | source_set | runtime_source | generated_artifact | unity_project
subject_identity
source_revision optional
fingerprint_hash optional
metadata_checksum optional
schema_hash optional
layout_hash optional
source_hash optional
registry_hash optional
binding_manifest_hash optional
```

`affected_subjects[]` 默认规则：

- affected set 必须在 plan 中闭包展开；不能在 commit 时发现跨 workbook referrer、owned child、cascade/set_null、metadata repair、auto-fix 后临时追加未计划写入目标。
- SaveAssets 的 affected workbook set 默认包含 dirty draft、dirty metadata、create/delete markers、resolved conflict draft、metadata repair、auto-fix proposal、cross-workbook hard reference、cascade/set_null delete 和 owned child table。
- Runtime source switch 的 affected subject 默认包含 current source、candidate source、resident object slots、identity/key/reference/dependency indexes、loaded load sets、pending ChangeSet/event buffer。
- generated artifact / Unity project mutation 只有在 operation profile 和 extension permission policy 允许时才能进入 affected set。

`patch_proposals[]` 默认字段：

```text
patch_id
origin_proposal_id optional
patch_kind
target_subject
target_location
owner_phase
old_value_hash optional
new_value_hash optional
old_bytes_hash optional
new_bytes_hash optional
preconditions[]
data_loss_risk
can_auto_fix
requires_confirmation
verification_expectation_id
rollback_boundary
```

patch kind 默认集合：

```text
workbook_cell_patch
workbook_range_patch
metadata_record_patch
companion_identity_patch
layout_artifact_patch
diagnostic_projection_patch
xlsx_package_part_patch
generated_artifact_patch
runtime_object_patch
runtime_index_patch
runtime_source_swap
unity_project_patch
external_tool_output_import
```

patch proposal 默认规则：

- workbook data cell patch 必须包含 workbook guid、sheet/table id、row guid/local id、field id、cell/range、old raw/canonical hash、new canonical hash。
- metadata patch 必须包含 metadata block、record stable key、old/new canonical metadata row hash 和 metadata checksum expectation。
- xlsx package part patch 必须声明 OPC part/relationship、old/new content hash、owner range 或 XML node selector；未声明 part 的内容 hash 必须保持不变。
- runtime object patch 必须包含 object slot、asset identity、field id、old/new canonical value hash、storage slot/range 和 generated accessor compatibility。
- runtime index patch 必须包含 index kind、old/new digest 和 affected slot/range；不能在 commit 时按 live dictionary 重建后跳过 digest 校验。
- `data_loss_risk=true` 的 proposal 必须有 confirmation id/hash；否则 plan classify 至少是 blocker 或 `data_loss_confirmation_required`。
- `can_auto_fix=true` 只表示可以自动生成 patch；是否自动 commit 仍由 operation profile 的 auto-fix policy 决定。

auto-fix / repair proposal 默认协议：

`AutoFixProposal` 是 Analyze / Normalize / Validate / Repair 阶段产生的机器可读建议，不是 apply 权限。它统一承载 normalizer canonical display 修正、schema-owned layout refresh、metadata repair、UnityResourceRef display refresh、diagnostic projection 清理、copy-row repair、workbook guid clone/repair 和 migration quick fix；各流程不能定义自己的隐式“安全自动修”语义。

`auto_fix_proposals[]` 默认字段：

```text
proposal_id
proposal_kind
origin_diagnostic_ids[]
origin_validator_id optional
operation_kind
target_subject
target_location
owner_region
safety_class
old_raw_fingerprint optional
new_raw_fingerprint optional
old_canonical_value_hash optional
new_canonical_value_hash optional
old_source_hash_contribution optional
new_source_hash_contribution optional
patch_kind
patch_preview_hash
preconditions[]
affected_workbook_guids[]
affected_asset_identities[]
affected_field_ids[]
backend_required_features[]
allocation_scope
data_loss_risk
can_auto_fix
requires_confirmation
confirmation_binding_hash optional
verification_expectations[]
rollback_boundary
suggested_action
```

`proposal_id` 默认规则：

- `proposal_id = sha256("exceldb-auto-fix-proposal-v1" + operation kind + proposal kind + stable target identity + origin diagnostic ids + old/new hashes + schema/layout/profile hash)`。
- stable target identity 必须使用 workbook guid、table id、field id、row guid/local id、metadata record key、generated artifact id、Unity guid 或 source layer id；row number、cell address、sheet name、header text 只能作为定位补充。
- 同一输入、同一 schema/profile/source revision 在不同机器上必须生成相同 proposal id；proposal id 不包含发现顺序、当前时间、用户名、UI 语言、human-readable suggested action 或临时文件路径。
- source revision、raw fingerprint、schema/layout/profile hash 任一变化后，旧 proposal 默认 stale；apply 必须重新 analyze 或产生 `transaction.plan_stale` / `transaction.source_revision_changed`。

`safety_class` 默认集合：

```text
safe_metadata_only
safe_layout_display
canonical_display_only
authoring_value_change
identity_repair
external_side_effect
data_loss_risk
```

safety class 默认语义：

- `safe_metadata_only` 只写 ExcelDB-owned metadata sheet、hidden companion identity cell、metadata checksum 或 repair history；不改正式数据 cell，不改变 canonical authoring value。metadata auto flush 只能使用这一类。
- `safe_layout_display` 只写 schema-owned header、schema comment、dropdown/data validation、UnityResourceRef display path、diagnostic marker/helper projection 或 generated layout artifact；不改变 `source_hash`、converted bytes、runtime dependency hash 或 canonical value。
- `canonical_display_only` 会改 raw/display token，但 parse 后 canonical value hash、canonical state、source hash contribution 和 runtime value 全部不变，例如换行归一化、enum canonical display token、大小写规范化展示。它可以触发 workbook source text 变化，但不能发布 runtime `property_changed`。
- `authoring_value_change` 会改变 canonical value、canonical state、source hash contribution、converted bytes 或 runtime 行为，例如 materialize default、path-to-guid rebind、reference target 改变、数值 normalizer 改值；不得由 Refresh/import/SaveAssets 隐式 apply，必须是显式 command、SerializedProperty edit、picker/rebind 或用户确认的 apply。
- `identity_repair` 会改 workbook guid、row guid/local id、asset guid/local file id、copy-row identity 或 reference metadata；只能由显式 metadata repair / clone / copy repair transaction 提交。即使能证明修复安全，也不能在 runtime hot reload 中静默提交。
- `external_side_effect` 会访问或修改 Unity project、文件系统、外部工具输出、provider manifest 或 generated artifact；必须有 extension permission、side-effect ledger、backend capability 和 verification expectation。
- `data_loss_risk` 表示会删除、覆盖、迁移不可逆内容，或可能丢失用户 helper/freeform/unknown package feature；必须绑定 confirmation id/hash，默认不自动 apply。

自动应用默认 gate：

- `can_auto_fix=true` 只允许出现在 ExcelDB-owned region、schema-owned generated/display region，或 schema 明确声明可覆盖且 precondition 能证明未覆盖用户内容的 target。
- `safe_metadata_only` 在 `metadata_auto_flush=editor_safe|required_for_runtime_publish` 且 old fingerprint/source revision 匹配时可以自动进入 `patch_proposals[]`；失败只保留 dirty metadata / proposal，不把 temporary identity 发布成 stable runtime asset。
- `safe_layout_display` 和 `canonical_display_only` 只有在 `auto_fix_policy=apply_safe_only|apply_safe_and_warnings` 且 backend capability 支持非破坏性 patch 时才能自动进入 `patch_proposals[]`；否则只写 report/status。
- `authoring_value_change`、`identity_repair`、`external_side_effect` 和 `data_loss_risk` 默认 `requires_confirmation=true`，不能被 ordinary Refresh、ImportAsset、SaveAssets 的附带流程静默应用。
- warning 级 proposal 在 `apply_safe_only` 下不自动 apply；`apply_safe_and_warnings` 仍必须满足 owner region、old hash、backend capability、data loss、side effect、source revision 和 confirmation gate。
- CI / CLI apply 不能依赖交互确认；confirmation 必须是 signed confirmation，绑定 operation kind、proposal id、plan hash、affected workbook guid、cell/range、old/new hash、schema/layout/profile hash 和 source revision。
- 任何 proposal 如果会让 affected workbook set 增加跨 workbook referrer、owned child、cascade/set_null、metadata repair 或 generated artifact，必须先闭包展开到 `affected_subjects[]`；commit 阶段不能临时追加目标。

proposal apply 默认规则：

- report-only proposal 不写 workbook、不改 dirty state、不改 runtime object graph；UI 按 proposal id 显示按钮或 quick fix。
- apply 前必须把选中的 proposal 转成 `patch_proposals[]`，并让 patch 的 `origin_proposal_id` 指向原 proposal；commit 只执行 patch proposal，不执行游离 proposal。
- apply 必须重新验证 old raw/canonical/package hash、source revision、schema/layout/profile hash、owner region、backend capability、confirmation hash 和 affected set；不匹配时拒绝写入。
- 同一 target 的多个 proposal 必须在 plan 阶段合并成一个 patch 或报 conflict；不能按 UI 点击顺序覆盖。
- proposal batch 默认 all-or-nothing per affected set；metadata-only 与 diagnostic projection 可以按 workbook 独立成功，但 report 必须列出 saved/skipped/failed/restored。
- proposal apply 后必须走 backup/temp/verify/replace/reimport；不能因为是“自动修复”而跳过非破坏性写回和 recovery 协议。

典型分类默认规则：

- UnityResourceRef `guid` 有效但 `main_asset_path` 缺失或 stale：`safe_layout_display`，只刷新 display path，不改 canonical reference，不触发 runtime dependency change。
- UnityResourceRef `guid` 为空但 `main_asset_path` 可解析：`authoring_value_change`，写 guid 会改变 canonical identity，必须由 picker/resolver/approved apply 提交。
- UnityResourceRef `guid` 与 `main_asset_path` 指向不同 asset：默认 `safe_layout_display` 可建议把 path 改成 guid 当前路径；按 path 反向改 guid 是 `authoring_value_change`。
- normalizer 只改变展示 token且 canonical value hash 不变：`canonical_display_only`；normalizer 改变 canonical value：`authoring_value_change`。
- schema 生成/布局刷新补 header comment、enum dropdown、struct 展开说明：`safe_layout_display`，但覆盖非 ExcelDB-owned comment/validation 时升级为需要确认或 blocker。
- duplicate copied row identity 且 copy closure 可证明：`identity_repair`；无法证明复制候选时不生成 safe proposal，保持 blocker。

preflight token 默认规则：

- Pre-commit 成功后生成 `preflight_token`，绑定 operation_plan_hash、operation_profile_hash、base revisions、file lock/write permission、backup/temp/recovery path、capacity reservation 和 confirmation hash。
- commit 必须持有未过期 preflight token；任一 base revision/fingerprint/source hash/metadata checksum/profile hash 变化时产生 `transaction.plan_stale` 或 `transaction.source_revision_changed`。
- preflight token 不允许跨 process/session 长期复用；CLI apply 可以复用 dry-run plan，但必须重新 preflight。
- preflight 失败产生 `transaction.preflight_failed` 或更具体的 `save.file_locked` / `vcs.lock_required` / `extension.permission_denied` 等 diagnostic。

side effect ledger 默认规则：

- `expected_side_effects[]` 记录所有计划中的文件写入、temp/backup/recovery manifest、generated artifact、Unity project mutation、external tool input/output、runtime source/object/index mutation。
- commit 后必须生成 `actual_side_effects[]`，并与 expected ledger 对比。
- 出现未声明文件写入、未声明 Unity mutation、未声明 external output、额外 workbook part 改动或少写计划输出时产生 `transaction.side_effect_mismatch` 或 `extension.undeclared_side_effect`，本次 artifact/cache 不得记为成功。
- side effect ledger 中的本机临时路径可以不进入 plan hash，但 logical output id、target subject、content hash 和 owner phase 必须进入 plan hash。

commit / verify 默认规则：

- commit 顺序按 target subject kind、workbook guid、patch kind、patch id deterministic 排序；同一 cell/range/slot 被多个 proposal 写入必须在 plan 阶段合并或报 conflict，不能靠顺序覆盖。
- commit 前重新验证所有 patch precondition；失败产生 `transaction.plan_stale`，不执行后续 patch。
- commit 只允许在 rollback boundary 内修改当前状态；workbook 文件进入 replace 阶段后由 recovery manifest 负责恢复。
- verify 必须校验 `verification_expectations[]`：metadata checksum、source hash、patched cell/range hash、package part hash、runtime index digest、object state、ChangeSet digest。
- verify 失败产生 `transaction.verify_failed` 或更具体的 `save.verify_failed` / `runtime.patch_failed`；未 publish 前不得发布成功事件。
- Publish 是最后一步；只有 verify 通过后才能清 dirty state、切换 current source、发布 ChangeSet、写 success manifest 或更新 cache。
- retry 同一个 plan 时，如果 success manifest / migration history / generated artifact 证明该 plan 已完成且 verification expectations 仍匹配，可以 idempotent skip；否则必须重新 analyze/preflight。

#### 14.5.1 默认 workbook backup / atomic save / recovery 协议

任何会写 workbook 文件的操作都必须有可恢复边界。包括 schema generation、metadata flush、`SaveAssets()`、migration、auto-fix apply 和 diagnostic writeback。

默认保存阶段：

```text
preflight
backup original
write temp workbook
verify temp workbook
replace original
verify replaced workbook
reimport committed workbook
publish editor state
write success manifest
cleanup old temp
```

默认文件策略：

- temp workbook 必须写在目标 workbook 同目录，避免跨磁盘 rename/copy 造成非原子替换。
- temp 文件名默认包含 operation id，例如 `game.xlsx.__exceldb_tmp_{operation_id}`。
- backup 文件名默认包含 operation id 和 source revision，例如 `game.xlsx.__exceldb_backup_{source_revision}_{operation_id}`。
- recovery manifest 默认写在同目录，例如 `game.xlsx.__exceldb_recovery_{operation_id}.json`。
- backup / temp / manifest 都必须加入 `.gitignore` 推荐规则，除非项目显式选择保留 recovery artifact。

默认 preflight：

- 目标 workbook 当前 fingerprint/source revision 必须等于 transaction analyze 时记录的 base revision。
- 目标文件可读、可写，且不处于 Excel lock 状态。
- patch list 不覆盖 unknown/helper/freeform region，不把公式 cell 静默改 literal。
- 本次操作没有 blocker / unresolved conflict / unresolved data loss confirmation。

默认 verify：

- temp 写完后必须重新打开并读取 workbook guid、schema hash、layout hash、table mapping、field mapping、row identity。
- verify replaced workbook 时必须确认关键 metadata、patched cell/range、source hash 与 expected candidate 一致。
- verify 失败时保留 original，不发布 success event；如果 original 已被替换但 verify failed，必须从 backup restore 或把 recovery manifest 标成 manual recovery required。

reimport committed workbook 默认规则：

- verify replaced workbook 通过后，仍必须从磁盘上的 replaced workbook 重新 import，生成 committed snapshot；不能直接把内存 candidate 当作成功后的 base snapshot。
- committed snapshot 必须重算 metadata checksum、source revision、source_hash、row revision、identity/key/reference/dependency graph、status index 和 workbook fingerprint。
- editor dirty draft 只有在 committed snapshot 的 canonical value / metadata / identity 与 verification_expectations 完全匹配时才能清除。
- resolved_pending_save conflict 只有在 committed snapshot 与 resolved draft 复读一致时进入 `resolved_saved`；否则保留 stale conflict / dirty draft，并报告 verify/reimport mismatch。
- diagnostic projection writeback 成功后也必须复读 projection checksum；但 projection 复读失败只影响 projection report，不得把业务数据 draft 标成保存失败，除非本次 operation profile 把 diagnostic writeback 设为 required side effect。
- 如果 reimport 发现 workbook 被外部进程在 replace 和 reimport 之间再次修改，产生 `transaction.source_revision_changed` 或 `transaction.verify_failed`，不得清 dirty；保留 draft 并要求重新 Refresh/merge。

publish editor state 默认规则：

- 清 dirty、更新 base snapshot、row revision、source revision、status index、path index、conflict state 和 undo save marker 只能发生在 reimport committed workbook 之后。
- `SaveAssets()` 成功 report 中必须列出每个 workbook 的 `pre_save_revision`、`committed_revision`、`source_hash_before/after`、`metadata_checksum_after`、`reimport_report_hash`。
- 多 workbook 保存中，某 workbook 已替换并 reimport 成功、另一个 workbook 失败时，成功 workbook 可以进入 clean，但 report 必须标记 multi_workbook_atomic=false，并列出失败 workbook 的 dirty draft 仍保留。
- 如果跨 workbook reference/set_null/cascade/resolved conflict 要求同一 affected set 一起成功，任一 workbook reimport mismatch 时，所有相关 workbook draft 默认保持 unresolved/dirty，直到用户执行 recovery/merge；不能只按内存 candidate 清一半状态。

默认 replace：

- 单 workbook 保存默认追求 per-file atomic replace。
- 多 workbook 保存默认不承诺跨文件原子；report 必须列出每个 workbook 的 saved / skipped / failed / restored 状态。
- 如果平台不支持 atomic replace，必须在 report 中标记 `atomic_replace=false`，并保留 backup 到用户确认清理。

默认恢复：

- 启动或 mount workbook 时发现 `__exceldb_tmp_*` / `__exceldb_recovery_*`，必须生成 recovery diagnostic。
- 如果 manifest 显示 replace 未开始，删除 temp 或提示用户删除，original 继续使用。
- 如果 manifest 显示 replace 已开始但 success 未完成，优先验证 original；original invalid 且 backup valid 时建议 restore backup。
- 自动 restore 只允许在 original 缺失或 checksum/metadata 明确损坏且 backup verify 通过时执行；否则要求用户确认。
- recovery 操作本身也要写 report，不能静默删除 backup。

backup retention 默认规则：

- 成功保存后，最近一次 backup 默认保留到下一次成功 save/generation/migration。
- CI/command line 可以配置立即清理，但必须在 report 中记录 backup cleanup。
- 任何 data_loss_risk=true 的 migration / auto-fix，默认保留 backup，直到用户或 CI policy 明确确认清理。

#### 14.5.2 默认 workbook write lease / 并发写入协议

Excel workbook 是真正的 authoring source of truth，因此任何写入都必须先取得本地 write lease。write lease 解决的是同一机器或同一共享工作区内多个 ExcelDB context / Unity Editor / CLI tool 同时写同一个 workbook 的问题；它不替代 VCS/Perforce lock，也不能阻止 Excel 应用或外部工具直接改文件。VCS lock、Excel 文件锁和 write lease 是三层不同 gate，必须分别记录在 report。

需要 write lease 的 operation：

- `SaveAssets`、`SaveAssetIfDirty`、`ForceReserializeAssets`。
- schema generation / layout refresh / cleanup deprecated layout。
- metadata flush、metadata repair、workbook clone/repair。
- migration apply、materialize defaults apply、auto-fix apply。
- diagnostic projection writeback。
- merge apply、recovery restore、backup/temp/recovery cleanup。

不需要 write lease 的 operation：

- read-only import、workbook-check、schema-lint、snapshot、diff、merge dry-run、migration dry-run、convert 只读输入阶段。
- watcher debounce、fingerprint、read-verify、status query。
- runtime `ConvertedBytesDataSource` open/read 和 immutable source switch。

这些 read-only operation 即使不持有 write lease，也必须遵守 read-verify / fingerprint / source revision 规则；读到半写入、锁定、temp/recovery 状态时只产生 report 并保留旧 source，不能清空状态。

write lease sidecar 默认命名：

```text
sidecar_path = target_workbook_path + ".__exceldb_write_lease.json"
```

例如：

```text
game.xlsx.__exceldb_write_lease.json
```

`WorkbookWriteLease` 默认字段：

```text
lease_format_version
lease_id
operation_id
operation_kind
operation_plan_hash optional
context_id
context_kind
process_id optional
host_id optional
tool_id
tool_version
normalized_workbook_path
workbook_guid optional
source_revision_before optional
fingerprint_before
affected_subjects_hash optional
write_intent_kind
acquired_at_utc optional
heartbeat_sequence
expires_at_utc optional
lease_state: active | releasing | abandoned
lease_hash
```

lease hash 默认规则：

- `lease_hash = sha256("exceldb-workbook-write-lease-v1" + canonical lease fields excluding lease_hash, acquired_at_utc, expires_at_utc, heartbeat_sequence)`。
- lease 文件可以包含 process id、host id、时间和 user display 信息用于恢复提示；这些字段不进入 source hash、bytes hash、schema hash、layout hash、operation plan hash 或 build cache key。
- 同一 operation 的 lease 必须写入 report 的 preflight summary 和 side effect ledger；lease sidecar 本身是工具临时文件，不是 workbook source。

lease acquisition 默认规则：

- acquire 必须使用原子创建 sidecar 文件或等价 OS lock；不能先检查不存在再普通写入。
- 多 workbook operation 必须按 normalized workbook path / workbook guid deterministic 排序 acquire lease，避免两个进程按相反顺序死锁。
- 任一 workbook lease acquire 失败时，释放已取得 lease，operation 进入 `transaction.preflight_failed` 或更具体的 `save.file_locked` / `vcs.lock_required` / `vcs.lock_owner_mismatch`，不写任何 workbook。
- 同一 context owner operation queue 内的嵌套写入默认拒绝或排到后续 safe point；不能复用外层 lease 同步重入写 workbook。
- 取得 lease 后仍必须验证 workbook fingerprint/source revision 等于 Analyze 阶段记录；不匹配产生 `transaction.source_revision_changed`，释放 lease，不写 workbook。
- 如果 project policy 启用 VCS lock provider，write lease acquire 之后还必须验证 VCS lock / MakeEditable；VCS lock 失败释放 write lease，不写 workbook。

lease 持有边界默认规则：

- lease 生命周期从 Pre-commit 开始，到 rollback / recovery manifest 完成 / publish editor state 后结束。
- 写 temp workbook、verify temp、replace original、verify replaced、reimport committed workbook、success manifest 和 cleanup 都在 lease 持有期间完成。
- lease 持有期间 watcher 必须忽略 lease sidecar 自身；如果 watcher 看到同 workbook 的 temp/backup/recovery 文件，只能进入 refresh_required/status，不启动导入半成品。
- lease 不允许跨 process/session 长期复用；CLI `--apply --plan` 每次执行都必须重新 acquire lease 和重新 preflight。
- lease 不能作为“数据已保存”的证明；只有 verify + reimport committed workbook + success manifest 才能清 dirty。

stale lease / recovery 默认规则：

- mount / Refresh / write preflight 发现遗留 write lease 时，先读取 sidecar、recovery manifest、temp/backup 文件和 workbook fingerprint，生成 recovery diagnostic summary。
- 如果 lease owner 仍存活或 heartbeat 未过期，新的写 operation 默认失败并报告 `transaction.preflight_failed`；read-only operation 可以继续读取上一次 verified workbook，但必须显示 write-in-progress 状态。
- 如果 owner 不存活或 lease 过期，且没有 replace-in-progress recovery manifest，工具可以在新的 preflight 中把 lease 标记 abandoned 并清理；清理动作必须进入 side effect ledger。
- 如果存在 replace-in-progress 或 verify-incomplete recovery manifest，不能只删除 lease；必须按 14.5.1 recovery 流程先验证 original/backup/temp，再决定 restore、manual recovery 或继续阻止写入。
- stale lease cleanup 不能改变 workbook source hash；若 cleanup 后 workbook fingerprint 与 lease 记录不一致，下一次写入必须重新 Analyze。

并发读写默认规则：

- 一个 context 持有 write lease 时，其他 context 的 read-only import 可以读取旧文件或等待 read-verify 稳定；读到 lease 期间的 temp/replaced 中间态必须丢弃 candidate。
- 两个写 operation 针对不同 workbook 可以并行；针对同一 workbook 或同一 affected all-or-nothing set 必须串行。
- 多 workbook all-or-nothing affected set 中任一 workbook lease 不可取得，整个 set 不写；不能先写取得 lease 的 workbook 再等待另一个。
- write lease 只保护 ExcelDB 写入流程。外部 Excel 在 lease 期间强行保存导致 fingerprint/source revision 改变时，本次 operation verify/reimport 必须失败并保留 dirty/recovery state。

### 14.6 默认 report / diagnostic 格式

所有 report 默认同时有 human-readable 和 machine-readable 两种输出。逻辑判断只依赖 machine-readable report，不解析人类文本。

machine-readable report 默认字段：

```text
operation_id
operation_kind
report_format_version
tool_version
producer_id
producer_version
started_at_utc optional
duration_ms optional
context_id
context_kind
phase
source_kind
source_identity
source_revision
schema_hash
layout_hash
operation_profile_id
operation_profile_hash
gate_policy_hash
convert_profile_hash optional
operation_plan_hash optional
operation_profile_sources[] optional
profile_override_provenance[] optional
result_severity
result_code
report_hash
diagnostics[]
auto_fix_proposals[] optional
diff_summary
allocation_summary
side_effect_summary optional
subreports[]
```

diagnostic 默认字段：

```text
diagnostic_id
code
severity
effective_severity
phase
scope
location
affects_import
affects_convert
affects_runtime
affects_writeback
can_auto_fix
suggested_action
owner_hint
data_loss_risk
is_suppressed
suppression_id optional
details
```

location 默认包含可用的最精确定位：

```text
workbook_path
workbook_guid
sheet_name
table_id
table_schema_name
row_guid
row_local_id
row_number
field_id
field_path
column_index
cell_address
range_address
metadata_record
asset_identity
asset_path
source_layer_id
bytes_section_kind
segment_id
unity_guid
unity_main_asset_path
extension_id
```

`operation_profile_sources[]` 默认字段：

```text
source_kind
source_id
source_revision optional
source_hash
profile_layer_kind
priority
```

`profile_override_provenance[]` 默认字段：

```text
field_path
source_kind
source_id
override_kind
affects_gate
affects_output
```

`side_effect_summary` 默认字段：

```text
expected_side_effect_count
actual_side_effect_count
unexpected_side_effect_count
missing_side_effect_count
affected_workbook_guids[]
affected_generated_artifacts[]
affected_unity_assets[]
external_tool_output_count
side_effect_result
```

allocation_summary 默认字段：

```text
policy
measurement_window
allocation_contract_scope
runtime_capacity_hash optional
requested_runtime_capacity optional
effective_runtime_capacity optional
runtime_capacity_observed optional
runtime_capacity_recommendation optional
capacity_recommendation_hash optional
gc_bytes
gc_alloc_count
watermark_growth_count
watermark_growth_entries[]
hot_path_allocation_entries[]
truncation_entries[]
external_backend_allocation_entries[] optional
report_projection_allocation_entries[] optional
profiler_backend
sample_reliable
budget_result
```

`watermark_growth_entries[]` 默认字段：

```text
buffer_kind
old_capacity
new_capacity
requested_capacity
operation_kind
source_identity
allowed_by_policy
diagnostic_code
```

`hot_path_allocation_entries[]` 默认字段：

```text
api_name
phase
gc_bytes
allocation_count
call_count
stack_sample optional
diagnostic_code
```

`external_backend_allocation_entries[]` 默认字段：

```text
backend_id
backend_version
operation_kind
phase
allocation_class
gc_bytes optional
allocation_count optional
declared_capability
allowed_by_policy
diagnostic_code
```

`report_projection_allocation_entries[]` 默认字段：

```text
projection_kind: human_text | localized_excel_comment | ui_view | debug_log
phase
gc_bytes optional
allocation_count optional
allowed_by_policy
diagnostic_code
```

默认规则：

- `code` 必须稳定，例如 `schema.missing_required_column`、`metadata.duplicate_row_guid`。
- `severity` 只能是 `info`、`warning`、`error`、`blocker`。
- `result_severity` 是 diagnostics 中最高严重级别。
- report 排序必须 deterministic：severity、scope、table id、row guid/row number、field id/column、code。
- CI 和 convert 默认只读取 machine-readable report。
- human-readable report 可以本地化，但不得改变 code、severity、location 和 suggested_action。
- runtime 正式包默认不生成大段字符串；只保留 code、severity、source identity、最小 location 和错误计数。
- `allocation_summary.sample_reliable=false` 时，不能把该次运行当作 no-GC 验收证据；CI/benchmark 必须使用可靠 profiler backend 或宿主无关 allocation counter。
- `allocation_contract_scope` 默认取 `runtime_hot_path`、`editor_core_operation`、`cli_core_operation`、`report_projection`、`external_backend` 之一；同一 operation 覆盖多个 scope 时使用 subreport 拆分，不能把 report projection 的分配混入 core operation 结果。
- machine-readable report append 属于 core operation scope；它只能写稳定 code、numeric id、hash、range、small fixed payload 或预分配 string table id，不在达到水位线后创建 message string、临时 list、异常对象或 stack trace。
- human-readable report、Excel 诊断批注、UI tree 和 debug log 属于 report projection scope；这些投影可以由 profile 允许分配，但不能参与 gate 判断、source hash、report_hash 或 no-GC core operation 证明。
- 外部 xlsx/zip/xml/backend 调用如果无法证明 no-GC，必须写 `external_backend_allocation_entries[]` 和 backend capability；Release runtime source、benchmark-no-gc 和 strict CI profile 默认禁止把这类 backend 放进测量窗口。

#### 14.6.1 默认 machine-readable report 物理格式 / 稳定性协议

machine-readable report 是 operation 的事实记录，也是 CI gate、Excel diagnostic projection、build cache、review artifact 和自动修复输入的共同协议。它必须 deterministic、可 hash、可合并、可裁剪；human-readable 文本只是投影。

物理格式默认规则：

- 默认格式是 canonical JSON，UTF-8 without BOM，LF 换行。
- object key 按 ASCII ordinal 升序输出；数组排序按各数组的专用规则，不能保留字典枚举顺序或发现顺序。
- 字符串使用 JSON 标准转义；hash/guid 使用 lowercase hex；枚举值使用 lowercase snake_case；整数用 invariant decimal。
- `null` 只用于字段显式允许的 optional 值；未知字段缺省表示 absent，不能用空字符串代替 absent。
- report 不写本机绝对路径、用户名、当前文化、本地化后的 message、随机 id 或当前时区时间。需要展示路径时使用 normalized project-relative path。
- `started_at_utc` / `duration_ms` 属于观测字段，不进入 `report_hash`；CI 判断不能依赖它们。
- `operation_profile_sources[]` 按 priority、profile_layer_kind、source_kind、source_id 排序；`profile_override_provenance[]` 按 field_path、source_kind、source_id、override_kind 排序。

`report_id` / `operation_id` / `report_hash` 默认规则：

- `operation_id` 是本次操作的稳定会话 id，可用于 backup/temp/recovery 文件名和日志关联；不进入 source hash、schema hash 或 bytes hash。
- `report_id` 默认由 `operation_kind + operation_id + context_id + phase + source_identity + report_format_version` 派生；同一 operation 的 retry 可以产生不同 `report_id`，但必须保留同一 `operation_id`。
- `report_hash` 是 report canonical JSON 的 SHA-256 lowercase hex，但必须排除 `report_hash` 自身、观测字段、human-readable 文本和 host transient timing。
- `diff_summary_hash`、`diagnostic_summary_hash`、`allocation_summary_hash` 可作为子 hash 写入 report；如果存在，必须由对应 canonical 子对象计算。
- report 被 gate policy 重新判定时，不改写原 report；生成一个 gate result report，引用原 `report_hash` 和新的 `gate_policy_hash`。

diagnostic 排序 / 去重默认规则：

- diagnostic 按 `severity_rank desc`、`phase_order`、`scope_kind`、`workbook_guid`、`table_id`、`row_local_id`、`field_id`、`cell_address/range_address`、`code`、`diagnostic_id` 排序。
- `diagnostic_id` 默认由 `code + phase + scope + canonical location + stable subject identity + canonical args hash` 派生；不能用递增序号。
- 同一 operation 内相同 `diagnostic_id` 只能出现一次；重复发现时合并 `occurrence_count` 和 `related_locations[]`，不能输出多条等价诊断。
- 如果多个 diagnostic 指向同一根因和多个受影响资产，保留根 diagnostic，并在 `related_subjects[]` 写受影响 identity；不要为每个下游 asset 复制 blocker，除非它们需要独立修复。
- `details` 可以包含结构化参数；CI/editor 逻辑只读 `code`、`severity/effective_severity`、`affects_*`、`location`、`suggested_action`。
- suppression 只能降低 effective severity，不能删除原 diagnostic；`is_suppressed=true` 时必须写 `suppression_id`、来源 profile 和原 severity。

location 默认规则：

- location 必须尽量提供 stable identity：workbook guid、table id、field id、row guid/local id 优先；row number、sheet name、cell address 只是现场定位。
- Excel 行号/列号可能因排序、筛选、插列而变化，不能作为 merge、auto-fix 或 suppression 的唯一 key。
- bytes/runtime report 如果没有 workbook/cell 信息，可以只写 asset identity、section/range、source hash；debug symbols 存在时再补 cell location。
- Unity resource 诊断必须同时写 `unity_guid` 和 display-only `unity_main_asset_path`；逻辑判断仍以 guid/provider key 为准。
- command/env/internal diagnostic 可以没有 workbook location，但必须写 command、phase 和 operation profile。

diff_summary 默认字段：

```text
diff_summary_hash
workbook_count
table_count
row_added_count
row_removed_count
row_moved_count
property_changed_count
reference_changed_count
dependency_changed_count
schema_structure_changed_count
metadata_changed_count
source_hash_before optional
source_hash_after optional
source_set_hash_before optional
source_set_hash_after optional
affected_workbook_guids[]
affected_table_ids[]
affected_asset_identities[]
```

subreport 默认规则：

- 一个顶层 operation report 可以包含 `subreports[]`，例如 `SaveAssets` 包含 metadata flush、validation、atomic save、reimport；`convert` 包含 workbook-check、source-set verify、bytes verify。
- subreport 必须有自己的 `report_id`、`operation_kind`、`phase`、`result_severity` 和 `report_hash`，并继承顶层 `operation_id`。
- 顶层 `result_severity` 是所有未 suppressed diagnostic 和 subreport result 的最高 effective severity。
- subreport 的 diagnostic 可以在顶层只保留 summary；如果裁剪，顶层必须写 `diagnostic_summary_hash` 和被裁剪数量，完整 report artifact 必须可追溯。

report detail level 默认集合：

```text
minimal_runtime
normal
editor_full
ci_full
debug_trace
```

detail level 默认规则：

- `minimal_runtime` 只保留 code、severity、source identity、revision、计数、最小 location，不生成大段 message string。
- `normal` 保留完整 diagnostic、diff_summary 和 allocation_summary，但不保留 per-cell trace。
- `editor_full` 可以保留 cell preview、related locations、suggested action 文本和 UI grouping。
- `ci_full` 必须保留足够字段重现 gate 判定、cache key、artifact hash 和 exit code。
- `debug_trace` 可包含排序后的 step trace，但 trace 不进入 `report_hash`，也不能作为 CI 通过条件。

`result_code` 默认集合：

```text
success
success_with_info
warning_policy
error
blocker
data_loss_confirmation_required
command_or_environment_error
internal_error
```

`result_code` 默认规则：

- `result_code` 是 `result_severity`、data loss confirmation、command/env/internal 状态和 gate policy 的归一化结果；CLI exit code 由它映射，不由 human-readable 文本决定。
- operation 成功但有 info diagnostic 时使用 `success_with_info`；warning 是否映射为 `warning_policy` 由 gate policy 决定。
- `blocker` 和 `data_loss_confirmation_required` 都不能 commit destructive change；后者用于用户确认后可重试的 data-loss step。

#### 14.6.2 默认 diagnostic code taxonomy / compatibility 协议

diagnostic code 是工具链、CI、编辑器 UI、Excel diagnostic sheet、自动修复和测试断言之间的稳定协议。message 可以本地化，code 不能随文案改变。

code 命名默认规则：

- code 使用 lowercase ASCII、点分 namespace 和 snake_case leaf，例如 `metadata.row_identity_ambiguous`。
- code 不包含 workbook 名、字段名、table 名、schema version、行号等动态内容。
- code 不包含本地化文本，不用空格、短横线或大小写区分含义。
- 内置 code 由 ExcelDB 保留；项目自定义 code 必须使用 stable package prefix，例如 `gameplay.drop_table.weight_sum_invalid`。
- 同一个 code 的含义一旦出现在 machine-readable report 中，就不能改成另一种语义；需要新语义时新增 code，旧 code 标记 deprecated/reserved。
- severity 是本次 operation 的 effective severity；默认 severity 来自 code catalog，mode / CI policy 可以升级，但不能改变 code。

内置 namespace 默认集合：

```text
schema
layout
metadata
structure
cell
validation
key
reference
dependency
unity_ref
localization
expression
migration
transaction
source
vcs
save
recovery
import
convert
bytes
runtime
assetdb
serialized
undo
watcher
build
diagnostic
performance
extension
command
internal
```

v1 必备 core diagnostic code：

```text
schema.descriptor_hash_mismatch
schema.codegen_hash_mismatch
schema.unsupported_version
schema.source_set_invalid
schema.option_invalid
schema.missing_required_column
schema.id_missing
schema.id_conflict
schema.id_range_invalid
schema.id_allocation_stale
schema.table_id_conflict
schema.field_id_conflict
schema.enum_value_id_conflict
schema.reserved_id_reuse
schema.alias_ambiguous
schema.property_path_conflict
schema.runtime_type_binding_conflict
schema.export_policy_invalid
schema.value_shape_invalid
schema.migration_required
schema.migration_ambiguous
layout.schema_owned_header_mismatch
layout.unknown_column_preserved
layout.column_appended_due_to_manual_order
layout.column_move_skipped
layout.helper_artifact_mismatch
layout.data_validation_mismatch
structure.table_mapping_ambiguous
structure.field_mapping_ambiguous
structure.required_column_missing
structure.data_region_ambiguous
structure.helper_region_overlap
metadata.missing_sheet
metadata.unsupported_format_version
metadata.checksum_mismatch
metadata.duplicate_workbook_guid
metadata.duplicate_row_guid
metadata.duplicate_row_local_id
metadata.asset_guid_collision
metadata.identity_not_flushed
metadata.companion_columns_missing
metadata.companion_identity_mismatch
metadata.row_identity_ambiguous
cell.parse_failed
cell.blank_required
cell.number_out_of_range
cell.integer_fraction
cell.formula_not_allowed
cell.formula_cache_invalid
cell.formula_dependency_undeclared
cell.formula_nondeterministic
cell.error_value
cell.merged_data_cell
validation.required_missing
validation.null_not_allowed
validation.enum_unknown
validation.enum_deprecated
validation.range_out_of_bounds
validation.regex_mismatch
validation.unique_key_duplicate
validation.map_key_duplicate
validation.variant_unknown
validation.variant_payload_invalid
validation.weight_invalid
validation.weight_sum_invalid
validation.probability_sum_invalid
validation.random_table_empty
key.missing_required
key.normalizer_missing
key.pattern_invalid
key.path_not_reversible
key.segment_invalid
key.asset_path_collision
key.lookup_ambiguous
reference.missing_target
reference.ambiguous_target
reference.display_mismatch
reference.type_mismatch
reference.deleted_target
reference.restrict_delete
reference.duplicate_not_allowed
dependency.edge_kind_invalid
dependency.ownership_cycle
dependency.preload_plan_invalid
dependency.preload_plan_cycle
unity_ref.guid_missing
unity_ref.guid_invalid
unity_ref.missing_asset
unity_ref.path_mismatch
unity_ref.asset_type_mismatch
unity_ref.runtime_provider_missing
unity_ref.sub_asset_not_allowed
localization.key_missing
localization.locale_missing
localization.fallback_cycle
localization.preview_mismatch
localization.token_invalid
localization.provider_missing
expression.parse_failed
expression.symbol_unknown
expression.type_mismatch
expression.forbidden_call
expression.nondeterministic
expression.budget_exceeded
migration.plan_stale
migration.host_unavailable
migration.data_loss_confirmation_required
migration.history_inconsistent
transaction.conflict_unresolved
transaction.conflict_not_found
transaction.conflict_resolution_stale
transaction.plan_stale
transaction.preflight_failed
transaction.source_revision_changed
transaction.verify_failed
transaction.side_effect_mismatch
source.source_set_mismatch
source.equivalence_mismatch
source.unmount_dirty_workbook
source.mount_path_conflict
source.workbook_alias_conflict
source.layer_order_invalid
source.layer_base_mismatch
source.layer_stack_mismatch
source.overlay_conflict
source.overlay_forbidden
source.patch_signature_invalid
vcs.snapshot_missing
vcs.snapshot_stale
vcs.merge_base_missing
vcs.merge_conflict
vcs.lock_required
vcs.lock_owner_mismatch
save.file_locked
save.verify_failed
save.atomic_replace_unavailable
save.package_preservation_failed
save.unsupported_xlsx_feature
save.signature_would_be_invalidated
recovery.manifest_found
convert.export_view_empty
convert.reference_stripped_target
convert.export_view_mismatch
bytes.header_invalid
bytes.checksum_mismatch
bytes.schema_hash_mismatch
bytes.section_missing
bytes.index_corrupt
bytes.source_mutated
bytes.load_set_invalid
bytes.segment_missing
bytes.segment_mismatch
bytes.manifest_missing
bytes.manifest_mismatch
runtime.mode_forbidden
runtime.capacity_invalid
runtime.switch_failed
runtime.hot_reload_disabled
runtime.hot_reload_candidate_invalid
runtime.publish_reentrant
runtime.subscriber_failed
runtime.allocation_budget_exceeded
runtime.operation_queue_overflow
runtime.patch_plan_stale
runtime.patch_failed
runtime.event_buffer_overflow
runtime.dependency_buffer_too_small
runtime.load_set_not_loaded
runtime.load_set_unload_blocked
runtime.thread_violation
runtime.context_mismatch
runtime.context_closed
runtime.source_not_open
runtime.snapshot_pool_exhausted
runtime.snapshot_invalid
runtime.snapshot_capability_unsupported
runtime.snapshot_pin_leak
assetdb.find_filter_invalid
assetdb.search_folder_not_mounted
assetdb.path_stale
assetdb.asset_not_found
assetdb.type_mismatch
assetdb.move_forbidden
assetdb.copy_forbidden
assetdb.sub_asset_not_supported
assetdb.sub_asset_mapping_ambiguous
assetdb.sub_asset_parent_invalid
assetdb.main_object_fixed
assetdb.labels_not_supported
assetdb.label_invalid
assetdb.label_mapping_ambiguous
assetdb.asset_editing_scope_unbalanced
assetdb.auto_refresh_scope_unbalanced
assetdb.virtual_folder_create_forbidden
assetdb.import_path_not_mounted
assetdb.context_mismatch
assetdb.context_closed
assetdb.object_state_invalid
assetdb.object_mutation_not_mappable
assetdb.modification_processor_rejected
assetdb.modification_processor_failed
assetdb.modification_processor_reentrant
serialized.property_not_found
serialized.handle_stale
serialized.mapping_changed
serialized.type_mismatch
serialized.read_only
serialized.array_edit_invalid
serialized.multi_object_not_supported
serialized.multi_object_incompatible
serialized.mixed_value_unsupported
serialized.context_mismatch
undo.no_record
undo.group_invalid
undo.stack_empty
undo.processing_reentrant
undo.record_scope_invalid
watcher.file_locked
watcher.file_unstable
watcher.read_verify_failed
watcher.fingerprint_noop
watcher.ignored_temp_file
watcher.queue_overflow
build.release_excel_source_forbidden
build.unity_dependency_missing_runtime_key
diagnostic.writeback_failed
diagnostic.user_comment_preserved
performance.watermark_grew
extension.host_requirement_unavailable
extension.nondeterministic
extension.registry_conflict
extension.dependency_missing
extension.dependency_cycle
extension.version_conflict
extension.permission_denied
extension.external_tool_failed
extension.external_tool_timeout
extension.undeclared_side_effect
command.invalid_args
command.profile_invalid
command.profile_override_forbidden
command.profile_hash_mismatch
command.environment_error
internal.unexpected
```

默认 severity / affects 约定：

- `*.missing_sheet`、`*.checksum_mismatch`、`*.duplicate_*`、`*.hash_mismatch`、`*.index_corrupt`、`validation.unique_key_duplicate` 默认 blocker。
- `schema.codegen_hash_mismatch` 默认 blocker；generated C#、binding manifest 或 registry bootstrap 与当前 descriptor 不一致时，不能继续用旧 generated accessor 解释 workbook/bytes。
- `schema.source_set_invalid`、`schema.option_invalid`、`schema.id_missing`、`schema.table_id_conflict`、`schema.field_id_conflict`、`schema.enum_value_id_conflict`、`schema.reserved_id_reuse`、`schema.alias_ambiguous`、`schema.property_path_conflict`、`schema.runtime_type_binding_conflict` 默认 schema-lint blocker；schema source 到 canonical descriptor 的输入、ID、alias、property path 和 runtime type binding 必须唯一、稳定、可复现。
- `metadata.asset_guid_collision` 默认 blocker；asset guid / local file id 派生碰撞不能通过改 key/path 自动规避。
- `structure.table_mapping_ambiguous`、`structure.field_mapping_ambiguous`、`structure.required_column_missing`、`structure.data_region_ambiguous`、`structure.helper_region_overlap` 默认 blocker。
- `schema.value_shape_invalid` 默认 schema-lint blocker；高级 value shape 必须有稳定 descriptor、layout 和 runtime 表达，不能由 importer 临时猜。
- `key.missing_required`、`key.normalizer_missing`、`key.pattern_invalid`、`key.asset_path_collision` 默认 blocker；`key.path_not_reversible` 在 schema 允许 `CreateAsset(asset, path)` 或 path 反解时是 blocker。
- `key.segment_invalid` 默认 validation error + `affects_convert=true`；`key.lookup_ambiguous` 默认是 lookup operation error，不修改当前 source。
- `schema.export_policy_invalid` 默认 schema-lint blocker；导出视图不能靠 converter 临时猜字段/表/目标端。
- `layout.unknown_column_preserved`、`diagnostic.user_comment_preserved` 默认 warning 或 info，不影响 import/export/runtime。
- `layout.column_appended_due_to_manual_order` 默认 info，`layout.column_move_skipped` 默认 warning；两者不影响 import/export/runtime。只有 editor capability / export adapter descriptor 明确声明 `requires_physical_column_order=true` 时，无法保真移动才升级为 layout refresh failure；project policy 只能升级 severity，不能改变 patch plan。
- `layout.schema_owned_header_mismatch`、`layout.helper_artifact_mismatch`、`layout.data_validation_mismatch` 默认 warning/info 且只影响 authoring layout；如果修复会覆盖非 ExcelDB-owned 内容，则升级为 `structure.helper_region_overlap` blocker 或等待显式 repair confirmation。
- `cell.number_out_of_range`、`cell.integer_fraction`、`cell.formula_not_allowed`、`cell.formula_cache_invalid`、`cell.formula_dependency_undeclared`、`cell.formula_nondeterministic`、`cell.error_value` 对 runtime/export 字段默认 `affects_convert=true`。
- `cell.merged_data_cell` 在 schema-owned header/display region 默认 warning；在正式数据区命中 schema 字段时默认 error + `affects_convert=true`，除非 schema 显式声明该 merged range 只是 ignored/helper display。
- `validation.map_key_duplicate` 默认 error + `affects_convert=true`；map child rows 的 key identity 必须唯一。
- `validation.variant_unknown`、`validation.variant_payload_invalid` 默认 error + `affects_convert=true`；oneof/union/polymorphic payload 不能按未知 variant 或不匹配字段静默导入。
- `validation.weight_invalid`、`validation.weight_sum_invalid`、`validation.probability_sum_invalid`、`validation.random_table_empty` 默认 error + `affects_convert=true`；随机选择表必须在 convert 前能确定有效候选集合和稳定权重。
- `reference.missing_target` 对 hard reference 默认 error + `affects_convert=true`，对 soft reference 默认 warning。
- `reference.display_mismatch` 默认 warning，不改变引用 identity；如果调用方显式选择“按显示 token 重绑定”，必须通过 edit transaction 写入新的 target identity。
- `dependency.edge_kind_invalid`、`dependency.preload_plan_invalid` 默认 schema-lint / convert blocker；依赖种类、导出视图和预加载计划必须在 convert 前归一化，不能由 runtime 临时猜。
- `dependency.ownership_cycle` 默认 blocker；owned child / cascade delete 关系不能形成环。
- `dependency.preload_plan_cycle` 默认 warning；普通 runtime dependency graph 允许环，但 recursive preload plan 必须用 visited set 截断并在 report 中记录 cycle，只有 schema/profile 声明 `cycle_policy=error` 时升级为 error。
- `unity_ref.runtime_provider_missing` 在 Release/build/convert 下默认 error + `affects_convert=true`。
- `localization.key_missing` 对 exported `LocalizedTextRef` 默认 error + `affects_convert=true`；editor-only 文本可按 policy 降为 warning。
- `localization.locale_missing` 默认按 locale policy 判定；required locale 缺失是 error + `affects_convert=true`，optional locale 缺失是 warning。
- `localization.fallback_cycle` 默认 blocker；fallback chain 不能在 runtime 解析时才发现循环。
- `localization.preview_mismatch` 默认 info/warning，只影响 Excel authoring display，不改变文本 key identity。
- `localization.token_invalid` 默认 validation error；占位符/token 集合不匹配会导致运行时格式化失败时 `affects_convert=true`。
- `localization.provider_missing` 默认 runtime open/build error；Release bytes 声明了 localization runtime provider 但宿主未注册、版本不匹配或 manifest 缺 provider binding 时，不能回退到 Excel 文本表或 preview_text。
- `expression.parse_failed`、`expression.symbol_unknown`、`expression.type_mismatch` 默认 validation error + `affects_convert=true`；表达式必须在 convert 前解析、绑定并类型检查。
- `expression.forbidden_call`、`expression.nondeterministic` 默认 schema/convert blocker；运行时表达式不能调用未声明、非确定性或有副作用的函数。
- `expression.budget_exceeded` 默认 convert error；表达式 AST/bytecode/eval step 超过 schema budget 时不能进入 Release bytes。
- `convert.export_view_empty` 默认 convert error；除非 schema/profile 显式声明允许空 artifact，否则不输出 bytes。
- `convert.reference_stripped_target` 默认 error + `affects_convert=true`；当前导出视图中的 hard reference 不能指向被 strip 的目标。
- `convert.export_view_mismatch` 默认 blocker；bytes/manifest/header 声称的导出视图与当前 runtime/profile 不一致时 open/switch 失败并保留旧 source。
- `extension.registry_conflict`、`extension.dependency_missing`、`extension.dependency_cycle`、`extension.version_conflict` 默认 schema-lint blocker；`extension.nondeterministic` 在 convert/build/runtime open 下默认 blocker。
- `extension.permission_denied` 对 required extension 默认 blocker；对 optional editor display/debug extension 默认 warning 并进入 fallback/read-only。
- `extension.external_tool_failed`、`extension.external_tool_timeout` 默认按调用 phase 继承 affects flags；发生在 convert/build/migration required step 时是 blocker。
- `extension.undeclared_side_effect` 默认 blocker；扩展产生未声明文件写入、网络访问、Unity project mutation 或输出文件时不能继续复用结果。
- `transaction.conflict_not_found` 默认 editor operation error；传入的 conflict id 不属于当前 context、已 resolved/cleared、或 revision lifecycle 已结束时，resolver 不创建 draft。
- `transaction.conflict_resolution_stale` 默认 editor operation error；resolver 打开后 source/row/mapping/schema/layout revision 改变时，旧 resolution 不能写入 draft，必须重新查询 conflict。
- `transaction.plan_stale`、`transaction.preflight_failed`、`transaction.source_revision_changed`、`transaction.verify_failed`、`transaction.side_effect_mismatch` 默认阻止 commit/publish；workbook/source/object graph 保持旧状态，除非对应 recovery manifest 已明确进入 manual recovery。
- `undo.no_record` 默认 warning；direct mutation 未先 `Undo.RecordObject` 时仍可由 `SetDirty` 形成 diff，但 Undo 菜单不能承诺回到修改前状态。
- `undo.group_invalid`、`undo.stack_empty`、`undo.processing_reentrant`、`undo.record_scope_invalid` 默认是 editor operation error；它们不写 workbook、不清 dirty、不修改外部 Excel。
- `watcher.file_locked`、`watcher.file_unstable`、`watcher.fingerprint_noop`、`watcher.ignored_temp_file`、`watcher.queue_overflow` 默认不影响旧数据继续服务；`watcher.read_verify_failed` 默认 warning，连续失败可按 project policy 升级。
- `runtime.operation_queue_overflow` 默认 warning；如果丢弃了 explicit open/switch/close 请求则升级为 blocker，但默认实现禁止丢弃这些请求。
- `runtime.subscriber_failed` 默认 warning；subscriber 回调异常不回滚已提交 source/object/index，但必须记录 subscriber id、event kind、revision 和 exception category 的 machine-readable 摘要。Release 可以只保留 code/count，不保存异常字符串。
- `runtime.capacity_invalid` 默认 API/command error；负数、超过宿主限制、dimension 与 buffer_kind 不匹配或 capacity descriptor 格式不兼容时，Reserve/Prewarm 不做部分提交。
- `runtime.patch_plan_stale`、`runtime.patch_failed` 默认阻止本次 source switch / hot reload commit 并保留旧 source；`runtime.event_buffer_overflow` 默认按 allocation policy 处理，不能静默丢事件。
- `runtime.dependency_buffer_too_small` 默认只在 diagnostic/reporting mode 下产生 warning；no-GC 热路径查询必须优先通过返回 status / required count 表达截断，不能为了记录诊断而分配。
- `runtime.load_set_not_loaded` 默认是 runtime lookup miss；Release 下返回 null/false 并写最小 report，不触发隐式加载。
- `runtime.load_set_unload_blocked` 默认 operation error；load set 仍被 pinned object、active RuntimeSnapshot、active dependency、pending ChangeSet 或 policy 持有时不能卸载。
- `runtime.thread_violation` 默认 API 使用错误；debug/development 可以断言，Release 必须返回失败或最小 report，不能跨线程读取 owner mutable object graph。
- `runtime.context_mismatch`、`runtime.context_closed` 默认 API 使用错误；跨 context 使用 object/key/snapshot 或对已关闭 context 读写时返回失败，不尝试按 identity 在当前 context 自动重绑定。
- `runtime.source_not_open` 默认 lookup/snapshot/load-set operation miss；context 仍可用但没有 current source 时返回 null/false/invalid handle，不隐式重开上一次 source，也不把该状态误报为 `runtime.context_closed`。
- `runtime.snapshot_pool_exhausted` 默认按 allocation policy 处理；允许水位线增长时记录增长，不允许时 snapshot acquire 失败且不分配。
- `runtime.snapshot_invalid` 默认 API 使用错误；Dispose 后继续使用、generation 不匹配或 snapshot handle 损坏时返回失败，不读底层内存。
- `runtime.snapshot_capability_unsupported` 默认 operation error；请求 ThreadSafe/JobSafe/BurstSafe 能力而 source/schema/provider/accessor 不支持时，不能降级成 resident object 读取。
- `runtime.snapshot_pin_leak` 默认 development/CI warning；如果 close/build/test teardown 后仍有 active snapshot pin，报告 owner stack/id/revision，是否升级由 project policy 决定。
- `bytes.header_invalid`、`bytes.checksum_mismatch`、`bytes.schema_hash_mismatch`、`bytes.section_missing`、`bytes.index_corrupt`、`bytes.manifest_missing`、`bytes.manifest_mismatch` 默认 open/verify blocker；reader 不能跳过损坏 section 或按猜测 layout 继续读取。
- `bytes.source_mutated` 默认 open/switch/load-set/refresh blocker；memory bytes 的 ownership/lifetime contract 被破坏时，不能继续从该 buffer 发布新的 source revision。
- `bytes.load_set_invalid` 默认 convert/open/verify blocker；load set root selector、dependency closure、segment membership、load-set-to-segment 双向索引或 empty policy 不合法时不能按运行时猜测继续。
- `bytes.segment_missing`、`bytes.segment_mismatch` 默认 open/verify blocker；segmented bytes 的 segment directory、checksum、load_set_index 与 manifest 必须一致。
- `source.source_set_mismatch` 默认阻止 Excel/converted bytes 等价 switch 的 no-op 判定；目标 source 仍可作为普通 candidate 继续完整 diff/validate，失败时保留旧 source。
- `source.equivalence_mismatch` 默认 blocker；source_set_hash 声称一致但 identity/key/reference/dependency index 不一致时，视为 artifact/reader bug 或损坏，保留旧 source。
- `source.unmount_dirty_workbook` 默认 operation error；unmount 返回 false，mounted context、dirty draft 和 watcher registration 保持不变。
- `source.mount_path_conflict` 默认 operation error；同一 mount root 归一化冲突或同一 physical workbook 多路径 mount 时，不创建第二个 source context。
- `source.workbook_alias_conflict` 默认 operation error；workbook alias 在 source set 内不能唯一解析时，引用 resolver 不能按 alias 继续。
- `source.layer_order_invalid` 默认 profile/schema-lint error；layer stack 必须有唯一 base 和 deterministic order。
- `source.layer_base_mismatch` 默认 open/switch blocker；patch/override 声明的 base hash、schema hash、export view 或 layer target 与当前 base 不匹配时不能应用。
- `source.layer_stack_mismatch` 默认等价 switch blocker；manifest/bytes/runtime 声称的 materialized layer stack hash 与当前 layer stack 不一致时保留旧 source。
- `source.overlay_conflict` 默认 blocker；同一 layer priority 下两个 active overlay 改同一 diff key 且无显式 merge policy 时不能猜优先级。
- `source.overlay_forbidden` 在 Release 或当前 mode/profile 禁止对应 overlay kind 时默认 blocker；Editor/Development 可按 policy 降为 warning 并忽略该层。
- `source.patch_signature_invalid` 默认 Release blocker；Development 可以按 profile 降为 warning，但不能把该 patch 当作可信正式 hotfix。
- `vcs.snapshot_missing` 默认 warning；如果 project policy 要求 workbook 必须有 review/merge snapshot，则在 CI 中升级为 error。
- `vcs.snapshot_stale` 表示 derived snapshot/diff/review artifact 与当前 workbook source_hash、metadata_checksum、schema_hash 或 layout_hash 不一致；普通 editor 是 warning，CI review gate 默认 error。
- `vcs.merge_base_missing` 默认 blocker；没有 base snapshot/xlsx 时不能自动三方合并二进制 workbook，只能显式 choose ours/theirs 或人工 resolve。
- `vcs.merge_conflict` 默认 blocker；workbook merge 产生未解决 property/row/identity/helper conflict 时不写 merged workbook。
- `vcs.lock_required`、`vcs.lock_owner_mismatch` 只在 project policy 启用 lock provider 时生效；默认 editor warning，CI/pre-submit 可升级为 blocker。
- `save.package_preservation_failed` 默认 blocker；非目标 xlsx part、relationship、style/comment/drawing/table/pivot/VBA 等保真校验失败时不替换 workbook。
- `save.unsupported_xlsx_feature` 默认 blocker；当前写回会触碰后端不能保真 patch 的 Excel feature 时，必须拒绝而不是整包重写。
- `save.signature_would_be_invalidated` 默认 blocker；带数字签名的 workbook/xlsm 在没有显式 policy 允许破坏签名时不能写回。
- `serialized.property_not_found`、`serialized.handle_stale`、`serialized.mapping_changed`、`serialized.type_mismatch`、`serialized.read_only`、`serialized.array_edit_invalid`、`serialized.multi_object_not_supported`、`serialized.multi_object_incompatible`、`serialized.mixed_value_unsupported`、`serialized.context_mismatch` 默认是 editor operation error，不写 workbook；如果发生在 SaveAssets preflight，阻止 affected workbook 保存。
- `assetdb.asset_not_found`、`assetdb.type_mismatch`、`assetdb.path_stale`、`assetdb.move_forbidden`、`assetdb.copy_forbidden`、`assetdb.sub_asset_not_supported`、`assetdb.sub_asset_mapping_ambiguous`、`assetdb.sub_asset_parent_invalid`、`assetdb.main_object_fixed`、`assetdb.labels_not_supported`、`assetdb.label_invalid`、`assetdb.label_mapping_ambiguous`、`assetdb.asset_editing_scope_unbalanced`、`assetdb.auto_refresh_scope_unbalanced`、`assetdb.virtual_folder_create_forbidden`、`assetdb.import_path_not_mounted`、`assetdb.context_mismatch`、`assetdb.context_closed`、`assetdb.object_state_invalid`、`assetdb.object_mutation_not_mappable`、`assetdb.modification_processor_rejected`、`assetdb.modification_processor_failed`、`assetdb.modification_processor_reentrant` 默认是 editor operation error；它们不修改 workbook，除非调用方随后通过 resolver/rename/move/copy/sub-asset/label/import operation 成功产生 editor draft 或 source refresh。
- `performance.watermark_grew` 默认 info；项目可在 benchmark/CI policy 中升级为 warning/error。
- `command.invalid_args`、`command.profile_invalid`、`command.profile_override_forbidden`、`command.profile_hash_mismatch` 默认是 command/environment error；如果发生在 convert/build/runtime open 的 profile/manifest 校验阶段，本次 operation 失败且不能写输出或替换 source。
- `internal.unexpected` 默认 internal tool error，对应 exit code 5。

自定义 diagnostic code 默认规则：

- custom validator / codec / migration / drawer 声明的 code 必须在 generated registry 中登记。
- 自定义 code prefix 必须全局唯一；prefix 冲突是 `extension.registry_conflict`。
- 未登记 code 默认是 schema-lint error；CI 不能把 unknown custom code 当 warning 放过。
- 自定义 code 必须声明 default severity、affects flags、suggested action 模板和是否允许 auto-fix。
- custom code 不能复用内置 namespace，除非由 ExcelDB core package 提供。

compatibility 默认规则：

- report schema 新增字段必须向后兼容；删除或改名 machine-readable 字段需要 report format migration。
- code catalog 可以新增 code；不能删除已发布 code，只能 deprecated/reserved。
- deprecated code 如果仍由旧 workbook/report 读到，tool 必须能展示，并建议迁移到 replacement code。
- machine-readable report 可以包含 `tool_version` / `report_format_version`，但逻辑判断优先按 code、severity、affects flags。

#### 14.6.3 默认 diagnostic presentation / Excel writeback 协议

machine-readable report 是诊断事实源。Excel 中的诊断展示只是 report 的投影，不能参与 import/export/runtime 解释，也不能改变 schema hash / source hash。

默认展示通道：

```text
editor console / inspector status
machine-readable report json
human-readable report
generated diagnostic helper sheet
optional cell marker/comment
```

默认 Excel diagnostic helper sheet：

- sheet 名默认 `__ExcelDB_Diagnostics`。
- 该 sheet 属于 generated helper sheet，必须有 metadata 标记。
- 内容由 latest report 生成，可以随下次 report 全量重建。
- 不允许策划把它当作业务数据源；import/export 忽略该 sheet。
- 默认列：severity、code、message、workbook、sheet、cell、table id、row guid、field id、suggested action、operation id。
- 排序沿用 report deterministic sorting。

diagnostic projection record 默认字段：

```text
projection_id
projection_format_version
source_report_id
source_report_hash
operation_id
operation_kind
context_id
workbook_guid
source_revision
schema_hash
layout_hash
projection_scope: workbook | sheet | row | cell | asset | command
target_location optional
diagnostic_ids[]
projection_checksum
created_by_tool_version
```

helper sheet 生命周期默认规则：

- `__ExcelDB_Diagnostics` 是 latest projection，不是历史日志；默认每次 diagnostic writeback 以同 workbook 的 current report projection 全量重建 ExcelDB-owned block。
- 需要保留历史时，历史 report 作为外部 artifact 或 machine-readable report 文件保存；不在 workbook 中累积多轮旧诊断行。
- helper sheet 必须写 `source_report_hash`、`source_revision`、`schema_hash`、`layout_hash` 和 `projection_checksum`；下次 refresh 发现 projection stale 时可以安全覆盖 ExcelDB-owned block。
- projection stale 不影响 import/convert/runtime 判定；它只表示 Excel 展示过期。status index 和 machine-readable report 仍是当前事实。
- 如果 workbook 当前 source revision 与 diagnostic projection 的 source revision 不一致，UI 必须显示 stale projection 状态，不能把旧诊断当作当前错误。
- helper sheet 中非 ExcelDB-owned 区域若被项目允许存在，必须在 metadata registered helper/freeform range 内；否则 diagnostic writeback 不覆盖并报告 helper region overlap / writeback failed。

cell marker/comment 默认规则：

- 默认不覆盖用户已有 comment / note。
- 如果 cell 已有用户 comment，diagnostic marker 只写到 diagnostic helper sheet，并在 report 中记录 `diagnostic.user_comment_preserved`。
- 如果 schema/metadata 标记某个 comment 是 ExcelDB-owned diagnostic comment，可以由新 report 更新或清除。
- cell marker 默认只使用非语义样式，例如边框/填充/批注入口；导入导出不得依赖这些样式。
- 清除诊断只能清除 ExcelDB-owned marker/comment，不能清除用户手写批注、颜色和备注。
- cell marker/comment 必须包含 projection_id、diagnostic_id 或 source_report_hash 的 ExcelDB marker；没有 marker 的 comment 一律视为用户内容。
- 同一 cell 的多个 diagnostic 默认在 helper sheet 展开为多行；cell marker 只显示最高 effective severity 和 helper sheet anchor，避免反复重写长文本批注。
- 当某 diagnostic 在新 report 中消失时，只清除对应 ExcelDB-owned marker/comment；如果同 cell 仍有其他 active diagnostic，则更新 marker severity/anchor，不清空整格样式。

写回策略：

- diagnostic writeback 默认只在 Editor authoring 中允许，并且必须走 operation transaction。
- CI/convert 默认只输出 report，不写 workbook。
- runtime 默认不写 diagnostic 到 source。
- diagnostic writeback 失败不能改变 import/convert 结论；只在 report 中增加 `diagnostic.writeback_failed`。
- diagnostic writeback 可以和 schema generation/save 分开执行；默认不因为刷新诊断而改正式数据 cell。
- diagnostic writeback patch 只能修改 `__ExcelDB_Diagnostics` ExcelDB-owned block、registered diagnostic marker/comment 和必要 metadata generated_artifact checksum；不能修改正式数据 cell、schema-owned header、user comment、style 或 helper/freeform content。
- diagnostic writeback 的 OperationPlan 必须列出会清除的 stale marker/comment；不能靠打开 workbook 后扫描到什么就临时删除。
- projection 生成 human-readable message 可以本地化和分配；projection record、helper row identity、diagnostic id、source location、severity/code 必须来自 machine-readable report。

hash / dirty 规则：

- diagnostic helper sheet、diagnostic marker、diagnostic comment 不参与 schema hash、layout hash、source hash。
- diagnostic writeback 不产生 asset dirty，不触发 runtime hot reload。
- 如果 workbook 只有 diagnostic 投影变化，`AssetDatabase.SaveAssets()` 不应把它当作业务数据修改。
- diagnostic projection metadata checksum 只用于证明投影可安全覆盖；不能参与 workbook data source equivalence、bytes cache key 或 merge semantic conflict。
- workbook merge 遇到双方只改 diagnostic projection 时默认丢弃双方 projection，并要求 merge 后重新生成；不把 diagnostic helper sheet 当作业务冲突。

#### 14.6.4 默认 validation pipeline / validator 协议

validation 默认是 operation transaction 的 Analyze / Build candidate / Verify 阶段的一部分。它只产生 normalized value、diagnostic 和 auto-fix proposal；真正写 workbook 或 patch object 必须回到 transaction commit。

validation phases：

```text
schema_lint
workbook_structure
cell_parse_normalize
row_validate
table_validate
reference_validate
cross_table_validate
convert_validate
runtime_open_validate
```

validator 默认输入：

```text
SchemaDescriptor
ValidationMode
OperationKind
source identity
workbook/table/row/field identity
normalized candidate value
base snapshot optional
dependency/reference indexes
```

validator 默认输出：

```text
normalized value optional
diagnostics[]
auto_fix_proposals[]
```

`severity` 默认仍只使用 `info`、`warning`、`error`、`blocker`。`convert blocker` 不是第五种 severity，而是 diagnostic 的 `affects_convert=true` 且在 convert/CI/build policy 下会阻止输出。

ValidationMode 默认集合：

```text
EditorImport
EditorSave
PlayModeHotReload
DevelopmentBuild
Convert
ReleaseRuntimeOpen
CI
```

默认内置 validators：

- `required`：必填字段为空。
- `nullability`：non-null 字段为空。
- `type_parse`：Excel cell 无法按 schema value shape 解析。
- `default_materialization`：缺值是否可用 default 读取或写回。
- `enum_defined`：enum token/value id 是否存在。
- `enum_deprecated`：使用 deprecated enum value。
- `number_range`：min/max/inclusive/exclusive。
- `string_length`：min/max length，默认按 Unicode scalar count。
- `regex`：默认 culture-invariant；禁止依赖当前系统区域设置。
- `unique_key`：同 table key 唯一。
- `row_identity_unique`：row guid/local id 唯一。
- `reference_exists`：hard/soft reference 目标存在性。
- `reference_target`：ref table/group/type 约束。
- `reference_list_duplicate`：list reference duplicate policy。
- `ownership_delete_policy`：cascade/set_null/restrict 合法性。
- `UnityResourceRef_guid`：guid required、guid 格式、asset type、path display mismatch。
- `cross_field`：同 row 多字段关系。
- `cross_table`：跨表一致性和索引约束。

默认 severity / mode 策略：

- EditorImport：warning/error 都可显示 workbook；error asset 标记 invalid，不进入 successful hot reload。
- EditorSave：error/blocker 阻止保存 affected workbook；warning 是否阻止由 project policy 决定，默认不阻止但必须报告。
- PlayModeHotReload：error/blocker 阻止 candidate commit，旧 object graph 保留。
- Convert / CI / build：任何 `affects_convert=true` 的 error/blocker 阻止 converted bytes 输出。
- ReleaseRuntimeOpen：只接受已经通过 convert 的 bytes；出现 validation error 表示 artifact 损坏或版本不匹配，open 失败。

normalization 默认规则：

- type parse 和 normalizer 可以生成 canonical value，用于 hash、diff、merge、converted bytes。
- normalizer 不能静默改写 Excel cell；写回 canonical display value 必须作为 auto-fix proposal 进入 transaction/report。
- default value 可以在读取 candidate 时 materialize；默认不把 materialized default 批量写回 Excel。把 missing/default_materialized 物化为显式 cell value 必须是显式 `MaterializeDefaultValues` / migration operation，并进入 diff/report。
- custom normalizer 必须有 stable id 和 version；normalizer 变化影响 schema hash，除非 schema 标记为 editor-display-only。

custom validator contract：

- custom validator 必须声明 stable id、version、phase、supported modes、affected table/field ids、deterministic flag。
- validator 不能直接写 workbook、修改 object graph、发事件或读取非声明依赖的外部状态。
- 需要 Unity AssetDatabase、文件系统或网络状态的 validator 必须属于 adapter validator，并在 descriptor 中标注 host requirement；核心 runtime 不依赖它。
- validator 输出 diagnostics 和 auto-fix proposal；auto-fix apply 必须走 operation transaction。
- validator 排序 deterministic：phase、table id、field id、validator id。

#### 14.6.5 默认 Excel cell value parse / normalize 协议

ExcelDataSource 的第一步不是把 cell 文本直接塞进字段，而是先构建 raw cell view，再按 schema value shape 解析成 canonical value。source hash、diff、merge、converted bytes 都只使用 canonical value 和必要的身份 metadata。

RawCell 默认字段：

```text
workbook_path
sheet_name
cell_address
raw_kind: absent | blank | string | number | boolean | formula | error
raw_value
formula_text
formula_cached_kind
formula_cached_value
number_format_id
style_id
merged_range
comment_owner
```

raw cell 默认规则：

- `absent` 表示 xlsx 中没有该 cell record；`blank` 表示存在空 cell；两者在数据解析中默认都作为 missing value。
- rich text、style、comment、hyperlink、filter、hidden row/column 不参与 value 语义，但写回时必须按非破坏性策略保留。
- merged cell 不提供数据语义；只有左上角 cell 可作为真实值，其余 merged cell 作为 blank 处理并给 `cell.merged_data_cell` diagnostic，除非该区域是 schema-owned display header。
- hidden row / filtered row 仍属于 data region，默认参与 import/export；如果项目要“隐藏即忽略”，必须由 schema/import profile 显式声明。
- Excel error cell，例如 `#VALUE!`、`#REF!`、`#DIV/0!`，在 schema 字段数据区默认是 `cell.error_value` error，影响 convert。

missing / null / default 默认规则：

- 对所有字段，absent/blank 默认先进入 missing，而不是自动变成类型零值。
- nullable 字段的 missing 读为 null。
- optional 且有 default 的字段读为 default；这只是 canonical state `default_materialized`，默认不写回 Excel cell。
- required 且无 default 的字段 missing 产生 `validation.required_missing`；schema 声明必须有实际单元格但 cell absent/blank 时产生 `cell.blank_required`。
- string 字段也遵守 missing 规则；若需要把 blank 当空字符串，schema 必须声明 `blank_string_policy=empty_string`，否则 blank 不是 `""`。
- 非 blank 字符串 cell 的内容默认按 exact value 保留；trim、case fold、全半角转换只能由 schema normalizer 显式声明。

authoring value state 物理表达默认规则：

- `missing` / `default_materialized` 在普通 scalar/reference cell 中默认表达为 no value payload：xlsx cell absent 或 blank cell 都可读为 missing；写回清空时保留 style/comment/hyperlink/data validation，但清除 value payload，不写 schema default token。
- `explicit_null` 默认写 bare token `null`，只允许 nullable 字段；非 nullable 字段读到 bare `null` 产生 `validation.null_not_allowed`。如果字符串值本身是四个字符 `null`，必须在 cell 中写带引号的 JSON string literal `"null"`，不能用 bare token。
- `explicit empty string` 默认写 JSON string literal `""`；空 cell 不表示空字符串，除非字段显式 opt-in `blank_string_policy=empty_string`。
- `explicit_value` 即使等于 schema default，也必须写 canonical display token；它和 blank/default_materialized 在 authoring diff、merge、snapshot、SaveAssets 中不同。
- reserved authoring tokens 包括 bare `null`、single-cell codec 中的 omitted pair、list codec 中的 omitted element；string literal 如果想表达这些文本，必须使用 JSON string literal escaping。
- `blank_string_policy=empty_string` 只能用于非 nullable、无 default 的 string 字段；否则 schema-lint 产生 `schema.value_shape_invalid`。该 opt-in 下 absent 仍是 missing，blank 是 explicit empty string；SaveAssets 若需要写 missing，必须删除 value payload 而不是写 blank cell。
- `MaterializeDefaultValues` 是显式 operation：它把当前 `default_materialized` 写成 `explicit_value`，必须输出 affected cell list、old/new canonical state、data_loss_risk=false/true 判定和 report hash；`GenerateStructure`、`LayoutRefresh`、普通 `SaveAssets` 默认不执行它。
- explicit clear operation 对 nullable 字段写 `explicit_null`，对 optional/default 字段写 `missing/default_materialized`；普通 `stringValue = null` 仍按 Unity-like 兼容写 explicit empty string，不等价于 clear。

scalar parse 默认规则：

- number cell 解析使用 Excel 存储的数值，不使用显示格式文本；thousands separator、百分号、货币符号只在 schema normalizer 显式声明时接受。
- int/long 字段要求数值是有限值、无小数部分、在目标类型范围内；否则 `cell.number_out_of_range` 或 `cell.integer_fraction`。
- float/double 字段要求数值是有限值；NaN/Infinity 不允许从 Excel 输入。
- decimal/fixed-point 字段默认从 number cell 或 invariant string token 解析，并按 schema scale/rounding policy 验证；没有 policy 时不静默四舍五入。
- bool 字段接受 Excel boolean cell，以及 string token `true` / `false` / `TRUE` / `FALSE`；`1` / `0` 只有 schema 声明 `bool_numeric_alias=true` 时接受。canonical bool token 是 `true` / `false`。
- enum 字段接受 schema 声明的 stable name、export token、alias 或 value id；normalized value 是 enum value id，写回展示 token 由 schema 决定。
- reference 字段接受可见 key/path/display token，但 normalized value 必须是 target workbook guid + table id + row guid/local id；解析歧义产生 `reference.ambiguous_target`。

NumericDescriptor 默认字段：

```text
numeric_kind: signed_integer | unsigned_integer | float | fixed_decimal | ratio | percent
bit_width optional
scale optional
min_value optional
max_value optional
inclusive_min: true
inclusive_max: true
rounding_policy: reject | truncate | floor | ceiling | half_even | half_away_from_zero
physical_cell_kind: excel_number | invariant_string | auto_exact
allow_percent_token: false
allow_thousands_separator: false
allow_currency_symbol: false
unit optional
display_format optional
runtime_storage_kind
```

numeric descriptor 默认规则：

- `numeric_kind`、`bit_width`、`scale`、range、rounding、physical cell kind 和 runtime storage kind 都属于 value shape 语义；会影响 parse、canonical bytes、source_hash、converted bytes 和 generated accessor。
- `display_format`、Excel number format、列宽、颜色、千分位显示和货币符号默认只属于 layout/display；不参与 source_hash，不能改变 parse 结果。
- `physical_cell_kind=auto_exact` 默认规则：`i32/u32/f32/f64` 可以使用 Excel number cell；`i64/u64` 只有在值可被 IEEE-754 double 精确表示时才接受 Excel number cell，否则必须使用 invariant string token；`fixed_decimal/ratio/percent` 默认优先 invariant string token，除非 schema 明确允许 exact number cell。
- integer string token 使用 invariant decimal，不允许 `_`、空格、千分位或本地化数字；Excel number cell 必须是有限值、无小数部分、在 bit width 范围内。
- `fixed_decimal` canonical value 是 `{unscaled_int, scale}`；输入小数位超过 scale 时，`rounding_policy=reject` 默认 parse error，其他 rounding mode 必须由 schema 显式声明并进入 schema hash。
- `ratio` 默认表示 0..1 的 fixed decimal 或 float ratio；`percent` 是 authoring/display 语义，不等于运行时自动乘 100。是否接受 `25%`、`25` 表示 25%，或 `0.25` 表示 25%，必须由 `allow_percent_token` / normalizer 明确声明。
- Excel percent formatted number cell 存储值仍按 Excel raw number 读取，例如显示 `25%` 的 raw value 是 `0.25`；没有 schema normalizer 时不能因为 number format 把 `25` 或 `0.25` 互相转换。
- `allow_thousands_separator` / `allow_currency_symbol` 只影响 string token normalizer；Excel number cell 不读取显示文本，因此 `$1,000` 这类显示格式不能改变 raw numeric value。
- float/double 字段保存 IEEE-754 bit pattern；`-0.0` canonicalize 为 `+0.0`，NaN/Infinity 禁止输入。需要跨平台确定性 gameplay 计算的字段默认应使用 integer/fixed_decimal，或在 convert 阶段 bake 成固定 runtime 表。
- `min_value` / `max_value` 按 canonical numeric value 比较，不按 Excel 显示文本比较；range validator 在 parse 后执行，失败产生 `validation.range_out_of_bounds` 或更具体 numeric diagnostic。
- numeric normalizer 如果改变 canonical value，例如百分比 token 转 ratio、货币字符串去符号、rounding，必须输出 auto-fix proposal；不能在 import 中直接改 workbook。

numeric writeback 默认规则：

- `SaveAssets` 写 numeric cell 时使用 descriptor 的 canonical display writer；不能使用 C# `ToString()`、当前 culture、Excel 当前显示格式或 Unity inspector 文本。
- `i64/u64/fixed_decimal` 如果写入 Excel number cell 会丢精度，必须写 invariant string token，或因 backend/descriptor 不支持而拒绝保存；不能写一个 Excel 会四舍五入的 number cell。
- 写回 numeric value 默认保留 cell style/number format；style 不匹配 descriptor display format 只产生 layout/display proposal，不改变 canonical value。
- 如果 schema 要把 display format 作为强约束，例如百分比列必须显示 `%`，它进入 layout hash 和 layout refresh；仍不能作为 parse 事实源。

string / culture 默认规则：

- 所有 culture-sensitive parse 默认使用 invariant culture；禁止依赖当前 OS culture、Excel UI language 或 Unity editor language。
- string canonical value 使用 UTF-8 exact bytes；默认不 trim、不大小写折叠、不 Unicode normalize。
- schema 可以声明 string normalizer，例如 trim、case-insensitive key、Unicode normalization form；normalizer id/version 进入 schema hash。
- 换行在 canonical string 中保留为 `\n`；如果 workbook 存储 `\r\n`，import normalizes to `\n` 并把写回 canonical value 作为 auto-fix proposal。

date / time 默认规则：

- 完整目标版不做隐式日期类型猜测；Excel number format 看起来像日期不改变 raw_kind。
- date/time 字段默认推荐 ISO-8601 string，例如 `2026-07-05`、`2026-07-05T12:30:00Z`。
- 如果 schema 声明 `excel_serial_date=true`，必须同时声明 date system `1900` / `1904`、timezone policy 和 precision；否则 schema-lint blocker。
- Excel 1900 leap-year bug 必须按 Excel serial compatibility 处理，并在 descriptor 中固定；不能由 host library 自己决定。
- runtime converted bytes 中 date/time 必须已经是 canonical ticks/day/seconds 或 canonical string id，不保留 Excel number format 语义。

formula 默认规则：

- 核心库默认不计算 Excel formula。
- helper/freeform region 的 formula 只保留，不参与 import/export。
- schema 字段数据区的 formula 默认不允许作为 runtime/export value；EditorImport 可显示 cached result preview，但 Convert/CI 默认 `cell.formula_not_allowed` blocker。
- 如果 schema 声明 `formula_policy=allow_cached_value`，import 使用 cached result 作为 raw value 继续 parse，但 source hash 必须同时包含 formula text、cached value、calculation chain/version 标记。
- cached result 缺失、类型不匹配、workbook 标记需要重新计算时产生 `cell.formula_cache_invalid`；公式引用了 import 范围外未声明依赖时产生 `cell.formula_dependency_undeclared`。这些对 runtime/export 字段都是 convert blocker。
- 需要真正计算公式的项目必须注册 deterministic formula evaluator，声明 evaluator id/version/host requirement；evaluator id/version 进入 schema hash。

`FormulaPolicyDescriptor` 默认字段：

```text
formula_policy: forbid | allow_cached_value | evaluate_deterministic
allowed_function_set_id optional
allowed_function_set_version optional
declared_dependency_ranges[] optional
allow_cross_sheet_reference
allow_cross_workbook_reference
allow_external_link
allow_volatile_function
cache_freshness_policy: require_clean_calc | allow_dirty_preview_only
evaluator_id optional
evaluator_version optional
evaluator_host_requirement optional
```

workbook formula state 默认读取：

- importer 必须读取 workbook 计算元数据：`calcMode`、`fullCalcOnLoad`、`forceFullCalc`、`calcId`、calc chain presence/digest，以及 formula cell 的 cached value kind。
- `formula_policy=allow_cached_value` 要求 workbook 不处于需要全量重算状态；`fullCalcOnLoad=true`、`forceFullCalc=true`、calc chain 缺失且 backend 无法验证依赖时，runtime/export 字段产生 `cell.formula_cache_invalid`。
- cached value parse 后仍必须走普通 value shape parser；cached number 不能绕过 int range、enum、reference、date/time 或 validator。
- formula text 与 cached canonical value 同时进入 `source_cell_hash`；只改 formula text 即使 cached value 相同，也会改变 workbook source semantic hash，避免公式逻辑变化被漏掉。
- helper/freeform formula 不进入 source hash，但写回必须保留 formula text、shared formula group、array formula range 和 cached value，除非 operation 明确拥有该 cell。

公式依赖默认规则：

- schema 字段数据区的 formula 只能引用当前 SourceSet 中声明的 workbook/sheet/range，且这些 range 必须能映射到 schema field、metadata/helper artifact 或显式 declared dependency range。
- 引用未知 sheet、外部 workbook、DDE/WEBSERVICE/PowerQuery、未声明 named range、volatile dynamic range 或 import 范围外 cell，默认 `cell.formula_dependency_undeclared`。
- cross-sheet reference 只有在 `allow_cross_sheet_reference=true` 且目标 sheet/table/range 可纳入 source set dependency graph 时允许。
- cross-workbook formula reference 默认禁止；允许时必须把目标 workbook 纳入 SourceSetDescriptor，并让 source_set_hash 覆盖 referenced workbook 的相关 canonical input。
- shared formula 和 array formula 必须按 expanded formula token 或 group descriptor 进入 raw_cell_fingerprint；部分覆盖 array formula range 的写回默认 blocker。

非确定性公式默认规则：

- `NOW`、`TODAY`、`RAND`、`RANDBETWEEN`、`OFFSET`、`INDIRECT`、external link、宏/UDF、当前用户/环境/网络相关函数，以及 evaluator 标记为 nondeterministic 的函数，默认产生 `cell.formula_nondeterministic`。
- `allow_volatile_function=true` 只允许 EditorImport 预览；runtime/export/CI 仍默认 blocker，除非项目提供 deterministic evaluator 且把所有外部输入 snapshot 写入 dependency manifest。
- Excel UI 语言、本机 locale、时区、当前时间、计算线程数不能影响公式 import 结果；如果 evaluator 需要它们，必须先归一化为 descriptor 中的显式输入，否则 `extension.nondeterministic` 或 `cell.formula_nondeterministic`。

deterministic evaluator 默认规则：

- evaluator 是 extension descriptor 的一种，必须声明 function set、函数语义版本、浮点/日期/文本比较规则、错误值传播规则、支持的引用范围和 host requirement。
- evaluator 只允许读取 declared dependency ranges、current workbook/source set snapshot 和 descriptor；不能调用 Excel 应用实例、UnityEditor、网络、当前时间或未声明文件系统状态。
- evaluator 输出 canonical cached value、diagnostic 和 dependency digest；不能直接写 workbook、改 formula text 或清除 calc flags。
- evaluator id/version/function set 改变进入 schema_hash 或 converter_registry_hash；如果改变 output semantic，必须触发 cache miss 和 runtime compatibility report。
- evaluator 无法支持某函数或引用形态时产生 `cell.formula_dependency_undeclared`、`cell.formula_nondeterministic` 或 `extension.host_requirement_unavailable`，不能退回读取旧 cached value。

保存 / 写回公式默认规则：

- `SaveAssets` 修改被公式引用的 cell 时，必须保留公式 cell 不变，并按后端能力更新 workbook calc metadata，使 Excel 下次打开能重算；推荐设置 `fullCalcOnLoad=true` 或等价安全标记。
- 如果 backend 无法安全维护 calc chain / shared formula / array formula / workbook calc metadata，本次写回产生 `save.unsupported_xlsx_feature` 或 `cell.formula_dependency_undeclared`，不能静默保存一个缓存可能错误的 workbook。
- 写回 schema-owned formula field 只有在 operation 明确编辑该 field 且 schema 允许覆盖公式时才可把 formula cell 改为 literal；否则 `cell.formula_not_allowed` 或 save blocker。
- Convert/CI 不依赖刚保存后的 Excel 自动重算；若 formula runtime/export 字段需要 cached value，必须在当前 workbook snapshot 已证明 cache fresh，或使用 deterministic evaluator 重新计算。

parse / normalize output 默认字段：

```text
canonical_kind
canonical_value
canonical_value_bytes
canonical_value_hash
canonical_display_token optional
is_missing
is_null
is_explicit_value
is_default_materialized
source_cell_hash
raw_cell_fingerprint
display_text
diagnostics[]
auto_fix_proposals[]
```

canonical value 默认规则：

- `canonical_value_bytes` 是 import、diff、merge、source_hash、converted bytes writer 和 runtime patch plan 的共同事实；human-readable `canonical_value` / `canonical_display_token` 只是调试和写回展示。
- `canonical_value_hash = sha256("exceldb-canonical-value-v1" + canonical_value_bytes)`，用于 report、auto-fix proposal 和 diff summary；同一 value shape 下相同 canonical bytes 必须得到相同 hash。
- canonical bytes 使用 ExcelDB-defined value binary format，字段使用 stable type tag + value payload；不能直接使用 JSON、protobuf 默认编码、C# `ToString()`、Excel displayed text 或当前语言序列化。
- 所有 scalar canonical bytes 必须带 value shape id / field id context 中可验证的 kind tag，避免字符串 `"1"`、数字 `1`、enum id `1`、bool `true` 在 hash 中碰撞。
- integer 使用 fixed signed/unsigned little-endian two's complement payload，并按 schema declared width 校验范围。
- float/double 使用 IEEE-754 little-endian bit pattern；`-0.0` canonicalize 为 `+0.0`，NaN/Infinity 禁止从 Excel 输入。
- decimal/fixed-point 使用 canonical `{unscaled_int, scale}`；scale 来自 schema，输入多余精度按 rounding policy 处理，没有 policy 时 parse error。
- string 使用 UTF-8 bytes，前置 varint length；换行已统一为 `\n`，是否 Unicode normalize 只由 normalizer 决定。
- bool 使用单 byte `0/1`。
- enum 使用 enum descriptor id + enum value id；display token/export token 不进入 canonical value，除非 schema 显式声明该 token 是 exported runtime value。
- internal reference 使用 target workbook guid + table id + row guid/local id；UnityResourceRef 使用 guid + optional local file id/runtime provider key，不使用 main_asset_path 作为 identity。
- date/time 如果采用 ISO string，则按 canonical string；如果采用 serial/ticks，则按 schema date representation 写 UTC ticks/day/seconds + precision tag。
- repeated value 使用 element count + element canonical bytes，元素顺序默认有语义；无 stable element id 的 reorder 是 value change。
- expanded simple struct 使用 subfield count + 按 subfield id 升序的 subfield canonical bytes；single-cell struct parse 后也必须归一化到同一 struct canonical bytes。
- child table / sub-asset 不把子对象塞进 parent cell canonical bytes；parent 保存 child range/digest，child row 自己进入 source hash。
- map 使用 canonical key bytes 升序排序后写 key/value pair；duplicate key 在写 canonical bytes 前产生 `validation.map_key_duplicate`。
- union/oneof 使用 variant id + variant payload canonical bytes；当前 variant 外 payload 非空按 `validation.variant_payload_invalid` 处理，不进入“忽略字段”。

missing / null / default canonical state 默认规则：

- missing、explicit null、explicit value、default materialized 是四种不同状态；diff、merge、runtime patch 和 generated accessor 必须能区分。
- missing 不写类型零值；canonical state tag 为 `missing`，是否在 runtime 读成 null/default 由 schema default/nullability 决定。
- explicit null 只允许 nullable 字段；canonical state tag 为 `null`，不同于 missing。
- default materialized 表示源中 missing，但 runtime/export 值来自 schema default；source_hash 默认包含 `missing + default descriptor hash`，不伪装成用户显式填写 default。
- 用户显式填写与 default 相同的值，canonical state 是 `explicit_value`；它和 `default_materialized` 在 authoring diff/merge 中不同，但 export view 可以按 profile 选择是否把两者折叠为相同 runtime bytes。
- 折叠 default state 如果会改变 writeback/merge/diagnostic 语义，只能发生在 converted bytes export view，不能改 workbook source canonical state。

normalizer 默认规则：

- normalizer 输入 RawCell + schema value shape，输出 canonical value、diagnostic 和 auto-fix proposal；不能直接写 workbook、改 metadata 或访问未声明外部状态。
- normalizer id/version、mode、affected value shape、deterministic flag 进入 schema descriptor；会改变 canonical value 时进入 `schema_hash`。
- normalizer 只能把多个可接受输入 token 映射到一个 canonical value；如果映射不是单射且会影响写回展示，必须提供 canonical display token。
- normalizer 失败默认产生 `cell.parse_failed` 或对应 validation diagnostic；不能吞掉错误后返回 schema default。
- auto-fix proposal 必须包含 old raw token、new canonical display token、canonical_value_hash、cell/range 和 data_loss_risk 标记。

source cell hash 默认规则：

- `raw_cell_fingerprint` 覆盖 raw kind、raw value、formula text/cached value、calc state、merged state 和 import-relevant workbook state；用于 watcher/import no-op 和诊断，不等同 runtime value。
- `source_cell_hash` 覆盖 canonical state tag、canonical_value_bytes、field id、export policy、normalizer id/version、formula policy contribution 和 reference identity contribution。
- style、number format、column width、comment、hyperlink、filter、普通 data validation 不进入 `source_cell_hash`，除非 schema 声明它们影响 runtime/export 语义。
- 公式 cached value 被允许时，`source_cell_hash` 同时覆盖 formula text、cached canonical value、calculation chain/version 标记和 evaluator/cache policy。
- `source_cell_hash` 不包含 row number、column index、sheet name 或 display header text；这些只进入 layout/report。

hash / diff 默认规则：

- `source_hash` 使用 canonical value bytes、canonical state、row identity、field id、reference identity、normalizer/codec contribution 和 export policy，不使用 Excel 显示格式、列宽、颜色、普通 comment。
- 如果 schema 允许 formula cached value，`source_hash` 同时包含 formula text 和 cached value，避免公式不变但缓存变更或缓存过期被漏掉。
- canonical value 相同但 Excel 显示文本不同，默认只产生 layout/display auto-fix，不触发 runtime property_changed。
- canonical value 改变才进入 property diff、merge、hot reload patch 和 converted bytes 输出。
- authoring diff 默认区分 missing/null/default materialized/explicit value；runtime export view 可以按 profile 折叠等价 runtime value，但必须在 export view hash 中体现。

### 14.7 默认非破坏性 Excel 写回算法

Excel 写回默认是 cell/range patch，不是整 sheet 重建。

写回流程：

1. 打开现有 workbook，并读取 workbook region ownership。
2. 根据 property/cell mapping 构建 patch list。
3. 对 patch list 做 data-loss risk scan。
4. 只写 schema-owned structure cell、metadata cell、被明确修改的数据 cell/range。
5. 保存前复查 unknown columns、helper/freeform region、style、formula、comment、filter、freeze panes、data validation。
6. 保存后可选复读关键 cell 和 metadata，确认写回目标准确。

默认保留：

- unknown columns。
- helper/freeform rows。
- cell styles / number format / width / height。
- formulas。
- comments / notes。
- filters / frozen panes。
- data validation，除非该 validation 属于 schema-owned header/dropdown 并且 schema 要更新它。
- images/charts/pivot/table objects，除非明确属于 generated helper artifact。

默认阻断：

- patch 会覆盖 unknown column。
- patch 会把 formula cell 改成 literal value，且 schema 没声明允许覆盖公式。
- patch 目标无法唯一映射到 cell/range。
- metadata 写回失败且本次操作会产生新的 stable identity。
- workbook lock 导致无法完成必要 metadata flush。

默认允许：

- 更新 schema-owned header row。
- 更新 schema-owned data validation dropdown。
- 更新 metadata sheet。
- 更新被 SerializedObject / AssetDatabase 明确修改的数据 cell。
- 在不移动正式数据行的前提下新增 schema 字段列。

#### 14.7.1 默认 xlsx package 级保真 patch 协议

`.xlsx` / `.xlsm` 是 Open Packaging Convention ZIP 容器。ExcelDB 的保存实现不能把 workbook 当成“重新导出一个表格文件”；默认必须以原 workbook package 为 base，生成 package patch plan，只修改被 operation 明确拥有的 part / XML node / cell range，并证明其他内容保真。

package fingerprint 默认字段：

```text
package_format
file_extension: xlsx | xlsm
zip_entry_count
zip_entries[]
content_types_hash
workbook_rels_hash
workbook_xml_hash
worksheet_part_hashes[]
shared_strings_hash optional
styles_hash optional
calc_chain_hash optional
vba_project_hash optional
digital_signature_hash optional
custom_xml_hashes[]
```

`zip_entries[]` 默认字段：

```text
entry_name
compression_method
compressed_size
uncompressed_size
crc32
content_hash
is_exceldb_owned
part_kind
```

package patch plan 默认字段：

```text
operation_id
base_package_fingerprint_hash
target_workbook_path
required_backend_features[]
xlsx_backend_capability_hash
patched_parts[]
copied_parts[]
created_parts[]
removed_parts[]
relationship_patches[]
content_type_patches[]
shared_string_plan optional
style_plan optional
calc_plan optional
verify_plan
```

xlsx backend capability 默认字段：

```text
backend_id
backend_version
capability_descriptor_hash
supported_file_extensions[]: xlsx | xlsm
zip_copy_through: byte_for_byte | content_hash_preserving | rewrites_all_entries
xml_patch_model: node_level | worksheet_streaming | whole_part_rewrite
preserve_unknown_parts
preserve_vba_project
preserve_digital_signature: preserve | detect_and_block | unsupported
shared_strings: append_only | rewrite_all | unsupported
inline_string_write
style_append
comment_patch
threaded_comment_patch
data_validation_patch
defined_name_patch
calc_chain_patch
calc_pr_patch
worksheet_dimension_patch
row_col_insert_delete
row_col_move_with_style_formula_comment
list_object_range_patch
auto_filter_patch
drawing_anchor_patch
pivot_cache_patch
formula_cell_overwrite
merged_cell_patch
external_link_preservation
custom_xml_preservation
max_supported_workbook_size optional
allocation_class: no_gc | watermark_only | allocating_projection | allocating_core
```

backend capability gate 默认规则：

- `XlsxBackendCapabilityDescriptor` 是保存能力事实源，必须在 editor/CLI bootstrap 阶段登记；operation 不能通过试写 workbook 来发现能力。
- descriptor canonical bytes 和 `capability_descriptor_hash` 必须进入 package patch plan、operation report、allocation summary 和 CI artifact；同一 backend 版本在不同机器输出相同 hash。
- SaveAssets / LayoutRefresh / MigrationApply / ForceReserializeAssets preflight 必须从 patch plan 推导 `required_backend_features[]`，例如 append shared string、patch comments、delete calcChain、move column with drawing anchor、preserve VBA、preserve digital signature。
- 任一 required feature 未声明支持时，本次 operation 产生 `save.unsupported_xlsx_feature`，不写 temp workbook；不能降级为整 sheet/整 package rewrite，除非 profile 显式选择另一个已登记 backend 并重新 dry-run。
- `zip_copy_through=rewrites_all_entries` 的 backend 默认不能用于含 unknown part、VBA、digital signature、custom XML、drawing/chart/pivot 或非 ExcelDB-owned comment/style 的 workbook 保存；只能用于新建 workbook、generated-only helper artifact 或 project policy 明确允许的 debug/export projection。
- `content_hash_preserving` 可以接受 ZIP central directory metadata 变化，但 verify 必须证明每个未声明 changed entry 的 uncompressed bytes、CRC/content hash、relationship target 和 content type 不变。
- `xml_patch_model=whole_part_rewrite` 只有在该 worksheet part 完全由 ExcelDB-owned region 组成，或 verify 能证明非目标 nodes byte/canonical-equivalent 时才可用；普通策划 sheet 默认要求 node/range 级保真 patch。
- backend 声明支持某 feature 不等于跳过 verify；写 temp 后仍必须复读 package fingerprint、patched cell、metadata checksum、relationship/content type 和所有 preserved part hash。
- backend 运行中发现实际 workbook feature 超出 descriptor，例如存在 shared formula、array formula、threaded comments、external links 或 macro sheet 而 descriptor 未声明 preserve/patch 能力时，必须在 preflight 产生 `save.unsupported_xlsx_feature`。
- backend 分配行为按 `allocation_class` 分账；`allocating_core` backend 不能进入 Release runtime source、strict CI、benchmark-no-gc 或 editor core no-GC measurement window。
- 同一 operation 不允许先用一个 backend dry-run，再用另一个 backend apply；更换 backend 会改变 `xlsx_backend_capability_hash`，必须重新 analyze/dry-run，旧 plan 变 stale。

copy-through 默认规则：

- 默认 writer 是 ZIP part copy-through writer：未在 `patched_parts/created_parts/removed_parts` 中声明的 entry 必须从原 package 原样复制或保持相同 content hash。
- 如果 ZIP 库无法 byte-for-byte 保留 central directory metadata，至少必须保证 entry name、uncompressed bytes、CRC/content hash、relationship target 和 content type 不变；report 中标记 `zip_metadata_rewritten=true`。
- `[Content_Types].xml`、`_rels/.rels`、`xl/_rels/workbook.xml.rels` 只有在新增/删除 part 时允许 patch；排序必须 deterministic。
- `.xlsm` 的 `xl/vbaProject.bin` 默认原样复制；任何会移除、重写或重新压缩导致 content hash 变化的保存都是 `save.package_preservation_failed`。
- workbook/package digital signature 存在时，任何内容修改都会使签名失效；默认产生 `save.signature_would_be_invalidated` blocker，除非 operation profile 显式允许 break signature 并要求 report 记录。

允许 patch 的 part / node：

- 目标 worksheet 的明确 cell records、row records 和 dimension/ref，只限 schema-owned structure、metadata、companion columns 或明确 dirty data cell。
- `xl/sharedStrings.xml`：只允许按 shared string plan append 新字符串；不允许 compact、reorder、删除或重写既有 string entry/index，即使旧 entry 当前只被 ExcelDB-owned cell 引用。
- `xl/styles.xml`：v1 默认不新增/重写 style；只有 schema-owned generated style descriptor 明确声明时可 append ExcelDB-owned style，并记录 artifact id/checksum。
- worksheet `dataValidations`：只允许更新带 ExcelDB marker 或由 metadata artifact record 声明的 schema-owned validation。
- comments/notes/threaded comments：只允许更新 ExcelDB-owned marker/comment；非 ExcelDB-owned 用户 comment/note 保留。
- generated helper sheet、defined names、named ranges：只允许更新 metadata 声明的 ExcelDB-owned artifact。
- `xl/calcChain.xml` 和 workbook `calcPr`：只允许按 calc plan 请求 Excel 重算，不计算或重写公式结果。

禁止隐式 patch 的内容：

- 非 ExcelDB-owned drawing、image、chart、pivot table/cache、slicer、timeline、external link、connection、custom XML、VBA、macro sheet、digital signature。
- 非目标 worksheet part。
- unknown/helper/freeform region 的 cell、formula、style、comment、hyperlink、data validation。
- Excel structured table/ListObject 的 range/tableColumns，除非 patch plan 明确声明并且后端能保真调整。

cell patch 默认规则：

- 修改现有 cell value 时保留原 `style_id`、number format、comment、hyperlink、formula state 和 cell storage kind，除非本次 patch 显式拥有对应 artifact。
- patch 目标是 formula cell 时默认 blocker；schema 显式允许覆盖公式时必须删除 formula node、写 literal value，并记录 data_loss_risk。
- 写字符串时，已是 shared string cell 则复用既有 shared string 或 append 到 sharedStrings；已是 inlineStr 则更新 inline string；新建 string cell v1 默认写 inlineStr，避免全局 sharedStrings churn。
- 新建 data cell 默认复制同字段列最近上方 data cell 的 style；没有可用样式时使用 column/default style，不创建新 style。
- 删除/清空 cell 不删除 row/style/comment/hyperlink；默认清除 value payload，写入 blank/missing 表达，并保留现场格式。只有显式 cleanup / migration 且 ownership proof 成立时才可删除物理 cell/row。
- 写 `default_materialized` 不写 schema default display token；如果旧 cell 有显式 value，clear-to-default patch 只清除 value payload，使下次 import 得到 missing/default_materialized。
- 写 `explicit_null` 必须写 bare `null` token 并校验 nullable；写 explicit empty string 必须写 `""`；写 literal string `null` 或 `""` 文本必须使用 JSON string literal escaping。
- 对 `blank_string_policy=empty_string` 字段，clear-to-missing 不能用 blank cell 表达；如果后端不能安全删除 value payload 并保持 style/comment，SaveAssets 失败并报告 `save.unsupported_xlsx_feature` 或 `transaction.preflight_failed`。
- merged range 内只允许 patch 左上角 cell，且 schema 必须声明该 merged range 是 schema-owned display/helper；正式数据字段命中 merged range 默认 blocker。

shared string 默认规则：

- shared string table 不 compact、不排序、不删除 orphan entry；这避免重写全 workbook string indexes。
- append 新 shared string 时，append order 按 patch list 的 deterministic order；更新 `count/uniqueCount` 时必须与实际 table 一致。
- rich text shared string 默认保留；如果 schema field 写入 plain text 到 rich text cell，默认作为 data_loss_risk，需要 explicit confirmation 或写入 inlineStr 新 cell。

formula / calc 默认规则：

- ExcelDB 不计算 Excel formula，也不更新非目标 formula cached value。
- 如果 patch 改变了可能被公式引用的 cell，且 workbook 存在 formula/calcChain，默认设置 workbook `calcPr` 为 open 时重算，并可删除 `calcChain` part/relationship，让 Excel 重建。
- 如果后端不能安全更新 calcPr / calcChain relationship，产生 `save.unsupported_xlsx_feature`，不写 workbook。
- 如果 schema 使用 `formula_policy=allow_cached_value` 的字段被保存，保存前必须确认 formula text/cached value/calc state 与 import snapshot 仍匹配；不匹配时拒绝保存并要求 Refresh/merge。

structured table / pivot / drawing 默认规则：

- patch 普通 cell 但不改变 ListObject range 时，table object 原样保留。
- 新增/删除行列会改变 ListObject、autoFilter、pivot cache、drawing anchor 或 chart source range 时，只有后端声明支持对应保真 patch 才允许；否则 `save.unsupported_xlsx_feature` 或 `layout.column_move_skipped`。
- pivot cache 默认不刷新、不重写；如果 patched range 是 pivot source，report 标记 `pivot_cache_stale_possible=true`，需要用户/Excel 刷新，除非项目提供 deterministic pivot cache writer。
- drawing/image/chart anchor 如果落在要移动的 row/column range 内，column/row move 默认跳过；不能只移动 cell 而留下锚点错位。

verify 默认规则：

- temp workbook 写完后，必须重新打开 ZIP package，计算 package fingerprint，并验证所有未声明 changed part 的 content hash 未变。
- 对 patched worksheet，必须复读 patched cells、metadata checksum、row identity、field mapping、defined names/data validation marker 和 source hash。
- 对 `.xlsm`，必须验证 `vbaProject.bin` content hash 未变；对 signed package，若 policy 允许破坏签名，report 必须标记 signature state changed。
- 如果任一保真校验失败，产生 `save.package_preservation_failed`，不替换 original；若 original 已替换，按 recovery manifest restore。

#### 14.7.2 默认 MaterializeDefaultValues / default 显式物化协议

`MaterializeDefaultValues` 是 authoring 数据整理操作，不是 schema generation、普通 SaveAssets 或 convert 的隐式副作用。它唯一的语义是把当前 workbook source 中的 `default_materialized` 状态显式写成 `explicit_value`，让策划在 Excel 中看到并提交默认值。

默认 scope：

- 只处理 schema 字段数据区中当前 canonical state 为 `default_materialized` 的 cell；不处理 `missing` 且无 default、`explicit_null`、`explicit_value`、parse error cell、formula blocker cell、unknown/helper/freeform region。
- 默认只处理 editor-writable、schema-visible、非 runtime-only/generated、非 deprecated/reserved 的字段；需要包含 deprecated/reserved 字段时必须走 migration。
- 新建 draft row/element 的默认写入由 `materialize_defaults=on_create` 控制；既有 workbook 行的批量物化只能由本 operation 或 migration 控制。
- selection 可以按 workbook、table id、field id、row identity、filter predicate 或 explicit cell list 限定；selection 本身必须进入 OperationPlan hash。
- filter predicate 只能读取 canonical state/value、row identity、table/field id、export policy 和 schema-declared tags；不能读取 Excel display text、row number、current selection、隐藏/筛选状态、当前时间、随机数或外部文件。predicate descriptor、normalizer id/version 和 captured constants 必须进入 plan hash。

dry-run 默认输出：

```text
materialize_plan_id
target_workbooks[]
target_table_ids[]
target_field_ids[]
affected_cells[]
old_canonical_state: default_materialized
old_canonical_value_hash
default_descriptor_hash
new_canonical_state: explicit_value
new_canonical_value_hash
new_display_token
source_hash_before
source_hash_after
converted_bytes_effect: none | export_view_dependent | changes_runtime_bytes
data_loss_risk
operation_plan_hash
dry_run_report_hash
```

dry-run 默认规则：

- dry-run 不写 workbook；它必须完整 parse/import 当前 workbook，证明每个 target cell 仍是 `default_materialized`。
- dry-run 必须计算本操作后的 authoring `source_hash_after`；因为 canonical state 从 `default_materialized` 变为 `explicit_value`，authoring source hash 和 authoring snapshot hash 默认会改变。
- 如果当前 export view 会折叠 default state 与显式 default 值，则 `converted_bytes_effect=none`。
- 如果 target field 不在当前 export view 但可能影响其他 export view，则 `converted_bytes_effect=export_view_dependent`，并在 report 中列出 affected export_view ids 或 unknown profile reason。
- 如果 target field 在当前 export view 中且该 export view 保留 authoring state difference，则 `converted_bytes_effect=changes_runtime_bytes`，apply 后需要重新 convert，并按普通 source diff 发布 runtime patch。
- `data_loss_risk` 默认 false；如果写入 explicit default 会覆盖 formula、非空 physical value、非 ExcelDB-owned validation/comment 所需 payload、或需要改变 shared/rich text representation，则必须转为 blocker 或 data-loss confirmation。
- default display token 必须由 schema value shape writer 产生；不能用 C# `ToString()`、Excel 当前显示格式或本机 culture。

apply 默认规则：

- apply 必须验证 operation_plan_hash、dry_run_report_hash、source revision、metadata checksum、schema hash、layout hash、default_descriptor_hash、target cell raw fingerprint 和 package fingerprint。
- 任一 target cell 在 dry-run 后被用户显式填写、清空成 non-default missing、改为 explicit null、出现 parse error 或 formula state 变化时，产生 `transaction.plan_stale` / `transaction.source_revision_changed`，不写 workbook。
- 已经由上一次相同 plan 成功物化的 cell 如果 old/new canonical hash、display token 和 metadata revision 均匹配，可以 idempotent skip；否则必须重新 dry-run。
- apply 使用 14.7.1 的 cell patch 规则写 visible default token，保留 style/comment/hyperlink/data validation；不能 compact shared strings、删除 rows 或移动 columns。
- apply 成功后必须复读 affected cells，确认 canonical state 为 `explicit_value`，new_canonical_value_hash 与 dry-run 一致，并写 operation report。

hash /事件默认规则：

- authoring source_hash、WorkbookSnapshot、diff-workbook 和 review artifact 必须体现 state 变化；不能因为 runtime value 相同而忽略。
- Runtime hot reload 如果 export view 折叠 default state，source switch 可以是 source_summary/no-op；如果 export view 不折叠，则按普通 property_changed patch plan 发布。
- `MaterializeDefaultValues` 不改变 schema_hash、layout_hash、row identity、asset identity、reference identity 或 key identity，除非 default 字段本身是 schema 允许的 key field；key default 参与路径时必须先执行 duplicate/path collision preflight。

### 14.8 默认 identity 生成与修复规则

identity 默认来源：

- workbook guid 在 workbook 创建时生成，一旦提交到版本库不得自动更换。
- table id 来自 schema。
- field id 来自 schema，默认优先使用显式 field id；没有显式 field id 时才允许使用 proto field number 作为初始值。
- row guid/local id 来自 metadata；key 不是 identity。

新行默认处理：

- Excel 里出现没有 row guid 的新数据行时，import 可以创建临时 identity。
- workbook 可写只是 metadata flush 的必要条件，不是自动执行条件。本次 import 默认只标记 dirty_metadata；只有显式 `FlushMetadata()` / `SaveAssets()`、`metadata_auto_flush` 允许，或 runtime publish 需要 stable identity 且存在 paired editor authoring context 时，才生成独立 metadata-only OperationPlan 并尝试 flush stable row guid。
- 如果 workbook 不可写，临时 identity 只在当前 session 有效，并产生 `metadata.identity_not_flushed` warning。
- 未 flush stable identity 的新行不能进入 converted bytes。

duplicate row guid 默认处理：

- 如果两行 row guid 相同，且只有一行有 row revision / source hash 匹配历史 snapshot，另一行视为复制行候选。
- 复制行候选不能在 import / hot reload 中静默 regenerate row guid；只能生成 copy-row repair plan，并由显式 metadata repair / SaveAssets 事务提交。
- 即使项目开启 editor auto-repair，也必须表现为独立 operation transaction：有 plan hash、report、undo/recovery 记录，并且只写 metadata / companion identity / checksum，不改正式数据 cell。
- 如果两行都能匹配历史 snapshot，或者都无法判断来源，报 blocker，不自动改 identity。
- duplicate key 是 validation error；duplicate row guid 是 identity blocker。

metadata 丢失默认处理：

- table metadata 丢失但 sheet 有唯一 schema/table name 匹配时，可以打开为 warning，并要求 metadata repair。
- field metadata 丢失但 header/alias 唯一匹配时，可以打开为 warning，并要求 metadata repair。
- row metadata 丢失时可以导入为临时 identity，但不能 convert bytes，除非成功 flush stable identity。
- metadata repair 必须走 operation transaction/report。

#### 14.8.1 默认 row identity / asset guid / metadata repair 协议

row identity 是 workbook metadata 的持久内容，不从 key、row number、Excel 顺序或 cell value 推导。

row identity 默认字段：

```text
row_guid: 128-bit random id, lowercase 32 hex
row_local_id: uint64, workbook-wide monotonic allocation, never reused
row_revision: uint64
row_source_hash
identity_state: stable | temporary_draft | temporary_imported | deleted | repaired
```

默认生成规则：

- 新 workbook 创建时生成 `workbook_guid`，默认 128-bit random id，lowercase 32 hex。
- workbook record 保存 `next row local id`，默认从 1 开始；每次分配 row / child element local id 后递增。
- `row_guid` 默认使用随机 128-bit id；禁止从 key、row number、display name、source hash 派生。
- `row_local_id` 在同一 workbook 内全局唯一，不按 table 单独分配；删除行后不得复用。
- `row_revision` 初次 stable flush 为 1；每次 ExcelDB 成功写入该 row 的 data cell、metadata identity、key snapshot 或 source hash 时递增。
- 用户直接改 Excel data cell 不会自动递增 row_revision；import 时通过 row_source_hash 发现内容变化，下一次 ExcelDB 写回/metadata flush 时更新 revision。

`temporary_draft` / `temporary_imported` identity 默认规则：

- `temporary_draft` 只来自 `CreateAsset` / `SerializedProperty` child insert 这类 editor draft operation；它在创建时已经预分配最终 row_guid / row_local_id 和派生 asset guid/local file id，只是尚未 flush 到 workbook。
- `temporary_imported` 只来自 Excel 直接新增行或 metadata 丢失行；它使用 session-local token 定位本次 editor view / report / conflict，不是稳定 `AssetIdentity`。
- `temporary_draft` 可以进入 Editor authoring 的常规 asset set、`FindAssets`、`GUIDToAssetPath`、`AssetPathToGUID`、`LoadAssetAtPath` 和 `TryGetGUIDAndLocalFileIdentifier`；SaveAssets/FlushMetadata 成功前后 guid/local id 不得改变。
- `temporary_imported` 不能进入常规 `FindAssets` / guid-path API / object reference metadata / converted bytes / runtime hot reload `added` event；它只能通过 table view、status index 和 workbook diagnostics 暴露。
- metadata flush 被显式调用或被 profile/runtime publish gate 允许时，必须把 `temporary_imported` 转为 stable identity，并写入 row_guid / row_local_id / row_revision / row_source_hash；转换后才进入常规 asset set 并发布 editor added/moved/status change。仅 workbook 可写但未触发 flush 时，仍保留 `temporary_imported` 和 dirty_metadata/status diagnostic。
- 如果 workbook 不可写或被锁，保留 `temporary_imported` 并产生 `metadata.identity_not_flushed`；本次 session 可浏览，但 convert/build blocker。
- editor authoring 可以为 `temporary_imported` 创建 preview object 以便 generic table/inspector 显示；该 object 的 `AssetStatusFlags.TemporaryIdentity` 必须为 true，`TryGetGUIDAndLocalFileIdentifier` 返回 false，`GetAssetPath` 返回空字符串或 preview-only path，并写 report。
- `temporary_imported` 不能被其他 row 引用为 stable target；用户在 Excel 中手填对 `temporary_imported` row 的 key/path 引用时，resolver 可以在 editor session 内显示候选，但保存/convert 前必须先 flush stable identity。

asset guid / local file id 默认派生：

```text
asset_guid = first_128_bits_sha256("ExcelDB.AssetGuid.v1" + workbook_guid + table_id + row_guid)
asset_local_file_id = positive_63_bits_sha256("ExcelDB.LocalFileId.v1" + workbook_guid + row_local_id)
```

默认规则：

- `asset_guid` 和 `asset_local_file_id` 是 facade/adapter 暴露值；真正存盘身份仍是 workbook guid + table id + row guid/local id。
- `AssetDatabase.GUIDToAssetPath(asset_guid)` 必须通过当前 mount set 的 identity map 找到 current path；不能从 guid 反推 key。
- `TryGetGUIDAndLocalFileIdentifier` 返回上述 `asset_guid` 和 `asset_local_file_id`；`GetInstanceID()` 仍是 context-local，不进入 workbook/bytes。
- 派生 guid/local file id 发生碰撞时是 `metadata.asset_guid_collision` blocker；不得通过改 key/path 自动规避。

deleted / tombstone 默认规则：

- 删除 row 默认把 row record 标记为 deleted tombstone，至少保留到下一次成功 save/convert 后的 cleanup policy。
- deleted tombstone 记录 table id、row guid/local id、last key snapshot、deleted revision、operation id。
- 新行不得复用 deleted row_guid 或 row_local_id。
- reference restrict 检查必须能看到 deleted tombstone，避免同一 save transaction 中删除又新建同 key 时误判。

duplicate / copied row repair 默认规则：

- duplicate row guid/local id 是 identity blocker，除非可以证明其中一行是复制行候选。
- 复制行候选默认需要满足：只有一行能匹配 previous snapshot 的 row_source_hash / row_revision / row number trace，另一行 source hash 不匹配或 row record 是新出现的重复 metadata。
- 对复制行候选，repair operation 使用与 `CopyAsset` 相同的 identity remap engine：生成新的 row_guid 和 row_local_id，row_revision 重置为 1，key snapshot 按当前行计算，并写 `identity_state=repaired`。
- 如果策划复制了正式数据行但没有复制 hidden companion identity / metadata record，该行按 `row_added_temporary` 处理；后续 `FlushMetadata` 分配稳定身份。
- 如果策划连同 hidden companion identity / metadata record 一起复制，该重复 metadata 只用于识别复制候选，不能成为新 asset 身份；repair 只重写 metadata / companion identity / checksum，不重写正式数据 cell。
- 复制行 repair 的 owned child rows 必须和父行组成同一个 copy closure：父/子都生成新 row/element identity，owner edge 改到新父行，child order index 和策划填写的数据状态保持。
- repair 过程中的引用处理与 `CopyAsset` 一致：copy closure 内部引用 remap 到新副本，外部引用保留原 target identity；违反 schema reference policy 时 repair plan 是 blocker。
- 如果两行都像已发布历史行，或都无法匹配 previous snapshot，禁止自动 repair，要求用户选择保留哪一行身份。
- repair 必须输出 old/new row guid/local id、row number、key snapshot、source hash，不能静默改 identity。

workbook clone / move 默认规则：

- workbook move：同一 workbook_guid 出现在新路径，且旧路径不存在或显式 unmount/move，视为同一 workbook；不改 workbook_guid，不改 row identity。
- workbook clone：把一个 workbook 当作新的配置源使用时，必须执行 clone/repair operation 生成新的 workbook_guid。
- clone operation 默认保留 table id、field id、row_guid、row_local_id；因为 workbook_guid 已变，asset identity 与 asset_guid 都变成新 source 的身份。
- 如果项目需要 clone 后保留跨 workbook 引用身份，必须显式声明 source alias / migration；默认不把 cloned workbook 当同一 source。

#### 14.8.2 默认 workbook clone / duplicate workbook guid repair 协议

复制 Excel 文件是日常配置工作流，但复制出来的 workbook 不能靠路径不同自动成为新 source。workbook guid 是 source identity 的一部分；任何改变 workbook guid 的操作都必须是显式 clone/repair transaction。

duplicate workbook guid 分类：

- 同一 canonical physical file 被重复 mount：按 idempotent mount 处理，保留一个 workbook source context，report 记录 duplicate mount info，不创建第二套 asset。
- 不同 physical file 拥有同一 workbook_guid：默认 `metadata.duplicate_workbook_guid` blocker，即使当前内容 hash 相同也不能自动当作同一 source。
- 同一 workbook_guid 出现在新路径，旧路径不存在、被显式 unmount，或通过 explicit move operation 关联：视为 workbook move，不改 workbook_guid，不改 row identity。
- 不同 physical file 内容相同但 workbook_guid 相同，通常是文件系统复制；必须执行 clone/repair operation 后才能同时 mount。

CloneWorkbook 默认操作入口：

```text
CloneWorkbook(source_workbook_path, target_workbook_path, clone_policy)
RepairWorkbookGuid(workbook_path, repair_policy)
```

clone_policy 默认字段：

```text
new_workbook_guid
preserve_row_identity: true
remap_self_references: true
preserve_external_references: true
new_workbook_alias optional
key_collision_policy
reference_rebind_policy
```

默认 clone 流程：

1. 读取 source workbook、metadata、system companion columns、row identity、reference graph 和 source fingerprint。
2. 生成新的 workbook_guid，写入 workbook record，并重算 source identity。
3. 保留 table id、field id、row_guid、row_local_id、row_revision、key snapshot 和 data cell；这些 row identity 在新 workbook_guid 下形成新的 asset identity。
4. 重写所有派生字段和 metadata checksum，包括 workbook record、row record、source hash、asset guid/local file id 相关缓存。
5. 对 self reference 做 remap：target workbook guid 等于旧 workbook_guid 且 target row 在 cloned workbook 中存在时，改为 new_workbook_guid。
6. 对 external reference 默认 preserve：target workbook guid 不是旧 workbook_guid 的引用保持不变。
7. 如果某个 reference 目标旧 workbook_guid 但 cloned workbook 中不存在对应 row，按 reference policy 产生 missing/ambiguous diagnostic，不猜测目标。
8. 重新构建 key index、identity map、reference graph、reverse dependency index。
9. 执行 duplicate key、duplicate row identity、hard reference、UnityResourceRef 和 validation 检查。
10. 通过 operation transaction、backup/temp/verify/replace/reimport 提交。

clone report 默认字段：

```text
old_workbook_guid
new_workbook_guid
source_workbook_path
target_workbook_path
row_count
self_reference_remap_count
external_reference_preserve_count
key_collision_count
identity_repair_count
source_hash_before
source_hash_after
metadata_checksum_after
```

clone 默认边界：

- clone 不修改 source workbook。
- clone 不改变 schema table id / field id；schema identity 属于项目结构，不属于 workbook 实例。
- clone 不因为 key 相同而自动合并原 workbook 与 cloned workbook 的 row；两个 workbook 是两个 source，asset identity 不同。
- clone 后如果同一 source set 不允许两个 workbook 内出现相同 key scope，按 key descriptor 产生 duplicate key validation；不要为通过校验自动改 key。
- clone 生成的新 workbook_guid 和 self reference remap 必须写入 metadata / companion data；只改可见 key/path display 不算 clone 成功。
- clone 成功后，旧 asset guid 与新 asset guid 不同；引用旧 workbook 的外部配置不会自动指向 clone，除非执行显式 reference migration。

duplicate guid repair 默认规则：

- repair 只允许在用户明确选择“把当前 workbook 作为新 source”时生成新 workbook_guid。
- repair 必须展示旧/new workbook guid、受影响 asset guid 数量、self reference remap 数量、external reference 保留数量和 key collision 风险。
- repair 不能在 import/hot reload 中静默执行；EditorImport 只能给出 proposal，SaveAssets/migration/clone command 才能提交。
- repair 成功写入 `[block:workbook]`、受影响 `[block:rows]` reference metadata、metadata checksum 和 migration/repair report。
- repair 失败时保持 duplicate workbook blocker，不把该 workbook 加入有效 source set。

metadata repair 写入边界：

- metadata repair 只允许写 `__ExcelDB_Metadata`、必要 hidden companion identity cell 和 ExcelDB-owned diagnostic；不改正式数据 cell。
- repair 前必须校验 workbook fingerprint 未变化；repair 后复读 metadata 并重建 identity map。
- repair 失败时保留旧 metadata/source，不发布成功 ChangeSet。
- metadata repair 在 Release runtime 不可用；Development build 默认也不可用，除非显式 debug authoring mode。

### 14.9 默认 converted bytes 与 runtime index 格式

converted bytes 默认是 runtime 数据产品，不是 workbook 的压缩副本。

默认 header：

```text
magic
format_version
artifact_kind: full_source | patch_layer
schema_hash
source_hash
build_target
endianness
flags
file_size
section_directory_offset
section_directory_count
content_checksum
```

默认 section directory：

```text
schema_manifest
segment_directory optional
string_table
blob_table
table_directory
object_data
identity_index
key_index
reference_index
dependency_index
reverse_dependency_index
load_set_index optional
layer_manifest optional
patch_operations optional
debug_symbols optional
```

binary encoding 默认规则：

- `magic` 默认固定为 `EXDB`。
- `artifact_kind=full_source` 表示完整 materialized runtime source；`patch_layer` 表示只能作为 SourceLayer 应用的 patch bytes，不能单独作为完整 RuntimeDatabase source 打开。
- v1 默认 little-endian；`endianness` 用于 reader 校验，reader 不支持时必须拒绝，不做猜测读取。
- 所有 offset / length 使用从文件起点开始的 byte offset，section 默认 8-byte alignment。
- `format_version` 不兼容时 reader/open/switch 失败并保留旧 source。
- `content_checksum` 覆盖除 checksum 字段自身以外的 bytes，用于发现截断或错误拷贝；checksum 不参与 source hash。
- section directory 按固定 section kind 顺序写入；缺少必需 section 是 blocker。

默认 table data：

- table id 按 numeric ascending 排序。
- row/object slot 按 table id、row guid/local id 的稳定顺序写入；需要保留 Excel 显示顺序时单独写 display order index。
- object data 只写 runtime exported fields；editor-only/helper 字段不进入 runtime object data。
- 字符串进入 string table，object data 只保存 string id / offset，runtime 读取时不重复分配。
- enum 使用 schema 定义的 runtime representation。
- simple struct 在 bake 阶段展平为 runtime layout，读取时不解析 Excel 文本。
- UnityResourceRef 在 Unity adapter bake 阶段写 guid-derived runtime key / provider id；不使用 main_asset_path 作为身份或 Release runtime 兜底路径。

string table 默认规则：

- 使用 UTF-8。
- 按 exact UTF-8 bytes 去重。
- string id 分配按 bytes ordinal deterministic 排序，不按发现顺序、不按本机 culture。
- 空字符串保留为 string id 0。
- runtime 可以返回 interned/view string；稳定热路径不得为同一个 string 重复分配。

默认 runtime index：

- row guid/local id -> object slot。
- key scope + table id + canonical key hash -> object slot；命中后仍验证 canonical key bytes。
- table id -> contiguous object range。
- dependency edge list。
- reverse dependency index，供 hot reload / asset removed 事件使用。

deterministic 要求：

- 同一 schema descriptor、同一 workbook metadata、同一 exported data 在不同机器上输出 byte-for-byte 一致。
- 输出不依赖字典遍历顺序。
- 输出不依赖当前时间。
- 输出不依赖本机路径分隔符。
- report 可以记录生成时间，但生成时间不进入 source hash/schema hash。
- debug_symbols section 可以包含人类可读 path/cell location；是否生成由 build profile 控制，且不参与 source hash。

hash 默认规则：

- `schema_hash` 来自 canonical `SchemaDescriptor` 的 runtime semantic fields。
- `source_hash` 来自当前 export view 下的 workbook guid、table id、field id、row guid/local id、exported normalized cell values、必要 validation input、reference identity 和 export policy。
- `source_hash` 不包含 Excel 样式、批注、列宽、筛选、冻结窗格、helper/freeform region、生成时间、本机路径。
- key/path rename 会改变 key index 和 source hash，但不改变 row identity。
- UnityResourceRef path display 变化但 guid 不变时，不改变 runtime reference identity；是否改变 source hash 取决于 path display 是否被 schema 标记为 exported runtime field，默认不改变。

convert blocker 默认规则：

- schema hash 与 converter 当前 descriptor 不匹配且无 migration。
- row metadata 未 flush stable identity。
- duplicate row guid/local id。
- asset guid / local file id derivation collision。
- duplicate key。
- hard reference missing。
- required runtime field missing 且无 default。
- exported field validation blocker。

#### 14.9.1 默认 converted bytes section / reader 协议

所有语义 hash 默认使用 SHA-256 over canonical bytes，report 中使用 lowercase hex。包括 `schema_hash`、`layout_hash`、`export_view_hash`、`source_hash`、`source_set_hash`、`descriptor_hash`、`bytes_hash`、`report_hash`。runtime key index 可以额外使用稳定 64-bit hash 做查找加速，但 hash 不是身份，必须保留碰撞校验。

section directory entry 默认字段：

```text
section_kind
section_version
flags
offset
length
element_count
element_size
section_checksum
```

v1 必需 section：

```text
schema_manifest
string_table
table_directory
object_data
identity_index
key_index
reference_index
dependency_index
reverse_dependency_index
```

v1 条件 section：

```text
blob_table: 只有存在 blob/large string/custom binary field 时必需
segment_directory: 只有启用 segmented bytes / external payload segment 时必需
load_set_index: 只有 build/runtime profile 声明 load set 或非全量 eager resident 时必需
preload_plan_index: 只有 build/export profile 声明预计算 preload plan 时必需
layer_manifest: artifact_kind=patch_layer 或 full_source 保留 layer stack debug 时必需
patch_operations: artifact_kind=patch_layer 时必需
debug_symbols: optional
```

section validate 默认规则：

- header 的 `file_size` 必须等于实际 bytes length。
- `section_directory_offset + section_directory_count` 必须落在文件范围内。
- 每个 section 的 `offset + length` 必须落在文件范围内，且默认 8-byte aligned。
- section 不允许重叠；同一 `section_kind` 不允许重复，除非该 kind 的 descriptor 显式声明可重复。
- unknown required section 是 blocker；unknown optional section 可以跳过，但必须保留在 `bytes_hash` 中。
- 每个 section 的 `section_checksum` 默认是该 section bytes 的 SHA-256；header 的 `content_checksum` 默认是除 checksum 字段自身外整个文件的 SHA-256。
- converter 必须按固定 section kind 顺序写 directory；reader 必须校验顺序，但不能依赖 host dictionary order。

primitive encoding 默认规则：

- v1 所有 runtime-required section 使用固定 little-endian binary record；不使用 protobuf/json/messagepack 作为 runtime 读取格式。
- 整数类型使用 `u8/u16/u32/u64/i8/i16/i32/i64` 固定宽度；bool 使用 `u8 0/1`。
- 浮点使用 IEEE-754 `f32/f64` little-endian；converter 阶段已禁止 NaN/Infinity。
- hash 使用 32 bytes lowercase hex 的 binary digest bytes，而不是 ASCII hex；report/manifest 才使用 hex string。
- workbook guid / row guid / Unity guid 在 bytes 内默认使用 16 bytes canonical guid；显示字符串只在 debug_symbols/manifest。
- variable-length payload 不内嵌到 fixed record；使用 `(offset:u64, length:u32)` 或 `(start:u32, count:u32)` range 指向 string/blob/range section。
- optional index/slot 默认使用 `u32.MaxValue` 作为 invalid sentinel；`count=0` 表示 empty range，不表示 missing。
- runtime hot path section 禁止 varint、对象图递归、长度前缀嵌套对象和需要分配的解析结构；这些只允许出现在 debug/manifest/report artifact。
- 所有 record array 的 element size 必须写入 section directory；reader 通过 `length % element_size == 0` 校验。

schema_manifest 默认内容：

```text
descriptor_hash
schema_hash
layout_hash
source_hash
source_set_hash
export_view_id
export_view_hash
layer_stack_hash optional
materialized_source_hash optional
preload_plan_hash optional
load_set_manifest_hash optional
included_export_targets[]
build_target
build_profile
tool_version
table_layouts[]
field_layouts[]
source_workbooks[]
```

`table_layouts[]` 默认字段：

```text
table_id
runtime_type_id
asset_kind
object_slot_start
object_slot_count
object_record_size
field_layout_start
field_layout_count
child_range_start optional
flags
```

`field_layouts[]` 默认字段：

```text
table_id
field_id
value_shape_kind
storage_kind
state_bit_index optional
fixed_offset optional
slot_index optional
range_table_kind optional
reference_family optional
element_layout_id optional
flags
```

table / object data 默认布局：

- table_directory 保存 table id、runtime type id、object slot range、object data range、field layout range、child range index。
- object_data 按 table_directory 的稳定顺序写入；同一 table 内按 row guid/local id 排序。
- table-specific object record 由 generated descriptor 定义：fixed-width scalar area、bitset/null/default area、variable range area。
- string/reference/blob/repeated/child data 不在 object record 中复制大对象；record 只保存 string id、blob range、reference slot、child range、repeated range。
- optional/default 字段必须可区分 missing、explicit null、explicit value、default materialized；否则 diff/hot reload 会误判。
- child table 和 repeated range 保存 `start + count`，遍历时不创建 list。

object record 默认结构：

```text
object_header
state_bitset[]
fixed_scalar_area
slot_area
range_area
```

`object_header` 默认字段：

```text
object_slot
table_id
row_guid
row_local_id
object_state
key_index_range
reference_range
dependency_range
```

object record 默认规则：

- `object_record_size` 固定；同一 table 的 object record 可通过 `object_data_base + table.object_slot_start * object_record_size + local_index * object_record_size` 直接寻址。
- `object_slot` 是当前 bytes 内部 slot，不是长期身份；长期身份仍是 workbook guid + table id + row guid/local id。
- `state_bitset` 每个 exported field 至少有 2-bit state：`00 missing`、`01 explicit_null`、`10 explicit_value`、`11 default_materialized`；非 nullable 字段如果出现 `explicit_null` 是 verify blocker。
- fixed scalar area 只保存 fixed-width runtime value，例如 integer、float、bool、enum value id、small fixed struct、date ticks。
- string field 保存 string id；reference field 保存 reference slot；repeated/child/map/union/blob 保存 range slot。
- `default_materialized` 字段可以不复制 default payload；getter 按 field layout 指向 schema default table / generated default constant。若 converter选择 materialize payload，必须仍保留 state bit。
- editor-only/debug-only display payload 不进入 Release object record；debug_symbols 可反查 workbook/cell。
- object record 中所有 offset/slot/range 必须由 verify-bytes 校验落在对应 section/table 范围内。

field storage kind 默认集合：

```text
fixed_i32
fixed_i64
fixed_u32
fixed_u64
fixed_f32
fixed_f64
fixed_bool
fixed_enum
fixed_date_time
string_id
blob_range
reference_slot
value_range
child_range
map_range
union_payload
custom_fixed
custom_range
```

range table 默认规则：

- value_range 保存 `(element_layout_id, start, count)`，元素位于对应 value payload section，按 schema order 或 canonical key order。
- child_range 保存 `(child_table_id, object_slot_start, count)`，child object 仍有自己的 identity/object record。
- map_range 的 entry 按 canonical key bytes 升序；entry record 保存 key hash、key bytes/string id 或 key payload range、value payload range。
- union_payload 保存 variant id、payload storage kind、payload range/slot；未知 variant id 是 verify/import blocker。
- range table section 使用 fixed-size records；payload section 使用 offset/length 指向 blob/string/value bytes。

string / blob section 默认规则：

- string_table 保存 UTF-8 bytes blob 和 string entry table；string id 0 固定为空字符串。
- string entry 默认字段是 offset、length、hash、flags；offset/length 必须指向 string bytes blob 范围。
- UTF-8 必须验证；非法 UTF-8 是 blocker。
- Runtime 热路径默认返回 string view / interned stable string；如果 API materialize 新 string，必须是显式低频 API。
- blob_table 不参与字符串语义；它只保存 schema 声明的 binary/blob/custom codec bytes，并由 field layout 指向。

index section 默认规则：

- identity_index 按 `table id + row guid/local id` 排序，value 是 object slot；duplicate identity 是 blocker。
- key_index 按 `key scope + table id + canonical key hash + key string id + identity` 排序；key hash collision 必须通过 key string id / canonical key bytes 二次确认。
- reference_index 保存 source slot、field id、target slot、reference strength、dependency kind；target slot 越界或 hard missing 是 blocker。
- dependency_index / reverse_dependency_index 都以 contiguous range 表示，edge 按 source slot、target slot、dependency kind 排序。
- table range、child range、dependency range 的 `start + count` 必须落在对应 section 范围内。

#### 14.9.2 默认 runtime export view / strip 协议

converted bytes 不是“当前 workbook 的全量镜像”，而是某个 build/runtime profile 下的导出视图。client、server、editor、development、release、platform variant 都必须先归一化为 `ExportViewDescriptor`，再参与 import validation、source hash、bytes 输出、manifest、runtime open 和 hot reload equivalence。

`ExportViewDescriptor` 默认字段：

```text
export_view_id
export_view_version
build_target
runtime_mode
included_export_targets[]
excluded_export_targets[]
table_rules[]
field_rules[]
row_filter_rules[]
reference_closure_policy
debug_symbol_policy
strip_policy
empty_view_policy
```

table / field export policy 默认字段：

```text
export_targets[]
exclude_targets[]
editor_only
runtime_only
debug_only
server_only
client_only
required_for_key
required_for_reference_identity
required_for_validation
```

row filter rule 默认字段：

```text
table_id
filter_field_ids[]
predicate_kind
allowed_values[]
default_when_missing
affects_identity
```

默认导出视图生成流程：

1. 从 schema descriptor、`OperationProfile.convert_profile`、build target 和 runtime mode 生成 canonical `ExportViewDescriptor`。
2. schema-lint 校验 table / field / row filter policy：目标端 id 必须稳定，required key/reference 字段不能被当前视图 strip，row filter 字段必须可解析且 deterministic。
3. 对每个 table 计算 exported table set；helper/editor-only table 默认不进入 Release runtime view。
4. 对每个 exported table 计算 exported field set；editor-only/debug-only 字段默认不进入 Release，runtime-only/generated 字段只允许由 converter/materializer 产生。
5. 对每行计算 row inclusion；row filter 只允许使用 schema 声明的 normalized scalar/enum/bool 字段，不执行脚本、不读取本机环境、不依赖当前时间。
6. 构建 reference closure：当前视图中的 hard reference 目标也必须在当前视图中存在；否则产生 `convert.reference_stripped_target`。
7. 生成 export view canonical bytes、`export_view_hash`、per-workbook exported source hash 和 `source_set_hash`。
8. converter 只写 exported object/field/index/dependency/debug symbol；被 strip 的内容不进入 runtime object data、runtime dependency hash 或 Release bytes。

strip 默认规则：

- `editor_only=true` 字段可在 Excel、editor inspector、diagnostic 和 migration 中使用，但不进入 Release converted bytes。
- `debug_only=true` 字段只在 development/debug export view 中进入 bytes；Release 视图 strip。
- `runtime_only=true` / generated 字段默认不允许策划直接填写；如果需要进入 bytes，必须声明 materializer/validator，并把 materializer id/version 纳入 converter registry hash。
- key 字段默认是 runtime lookup 的一部分；除非 table 声明 `runtime_lookup=false`，否则 key field 被 strip 是 `schema.export_policy_invalid`。
- reference identity 字段、UnityResourceRef guid、sub-asset local id、hard reference target identity 默认不能被 strip；display path/comment 可以 strip。
- validation-only 字段可以不进入 bytes，但如果它影响 exported value 是否有效，它仍参与 convert validation 和 `source_hash` 的 validation input digest。

row filter 默认规则：

- 行过滤只能减少当前 export view 的 runtime object set，不能改变 workbook row identity。
- 被 filter 掉的行不进入 identity/key/reference/dependency index；debug report 可以列出 stripped row count。
- 如果 exported 行 hard reference 到 filtered-out 行，默认 blocker；soft reference 可保留 missing 状态，但必须按 reference policy 声明。
- row filter 字段自身默认不进入 bytes，除非也被普通 field export policy 选中。
- `default_when_missing` 默认为 exclude；缺失 filter 字段不会默默导出到 Release。

hash / manifest 默认规则：

- `export_view_hash` 是 canonical `ExportViewDescriptor` 的 SHA-256，不包含本机路径、输出路径、当前时间和 human-readable 文本。
- `schema_hash` 覆盖可导出的语义结构；`export_view_hash` 覆盖当前视图选择；`convert_profile_hash` 覆盖压缩、debug symbol、string table、runtime provider 等转换策略。
- `source_hash` 必须以当前 export view 为上下文，只覆盖 exported rows/fields、必要 validation input、reference identity 和 row identity。
- 同一 workbook 在 client/server/editor 不同 export view 下可以有不同 `source_hash`、`source_set_hash` 和 bytes；这些 artifact 不能互相复用。
- artifact manifest 和 bytes schema manifest 必须记录 `export_view_id`、`export_view_hash`、`included_export_targets[]` 和 stripped summary。

runtime open / hot reload 默认规则：

- runtime source 必须声明当前 `export_view_id/hash`；打开 bytes 时，如果 header/manifest 与 current runtime profile 不匹配，产生 `convert.export_view_mismatch` 并拒绝 open/switch。
- ExcelDataSource 用于 Development hot reload 时，也必须按同一个 `ExportViewDescriptor` 构建 candidate snapshot；不能用全量 Excel 去 patch 一个 stripped bytes runtime。
- source equivalence 比较的是同一 export view 下的 source/hash/digest；不同 export view 只能做普通 switch，不能当 no-op equivalence。
- 如果当前视图没有导出任何 table/row，默认产生 `convert.export_view_empty`；只有工具型 profile 显式声明 `empty_view_policy=allow` 才能输出空 bytes。

#### 14.9.3 默认 converted bytes segment / runtime load set 协议

大表和正式包内存不能只靠“全量 open 后全部 resident”。ExcelDB 默认支持把 converted bytes 分成 core index 与 payload segment，并用 load set 描述运行时驻留策略。分段是运行时包布局，不改变 asset identity、source_hash 的数据语义；会改变 bytes_hash、load_set_manifest_hash 和 cache key。

LoadSetDescriptor 默认字段：

```text
load_set_id
display_name
root_selector: all | table | schema_group | asset_path_prefix | explicit_assets | dependency_closure
root_table_ids[] optional
root_asset_identities[] optional
include_dependency_kinds[]
include_target_families[]
dependency_traversal: direct | recursive
max_dependency_depth optional
cycle_policy: truncate_with_report | error
segment_ids[]
residency_mode: eager_resident | explicit_load | metadata_only | external_segment
object_materialization: prewarm_instances | lazy_instances | no_wrappers
unload_policy: never | explicit
pin_policy: none | while_referenced | manual
allow_empty
capacity_hint
export_view_id
```

SegmentDescriptor 默认字段：

```text
segment_id
segment_kind: core_index | table_payload | string_payload | blob_payload | debug_payload | external_payload
load_set_id optional
source_hash
offset
length
compressed_length optional
compression optional
segment_checksum
required_for_open
```

core index 默认规则：

- `schema_manifest`、`table_directory`、`identity_index`、`key_index`、`reference_index`、`dependency_index`、`reverse_dependency_index` 默认属于 core index。
- core index 必须能在 open 阶段验证所有 identity、key、reference 和 dependency range；它可以只保存 unloaded payload 的 segment/range handle。
- object payload、large string/blob、debug symbols 可以放在非 core segment；未加载时不能读取字段值。
- key/path lookup 可以在 payload 未加载时解析到 AssetIdentity；`TryGetAsset` 是否成功取决于目标 load set residency。

load set 默认规则：

- 默认 profile 是 `eager_resident` 全量 load set，等价于不分段；项目显式声明 load set 后才启用 partial residency。
- `explicit_load` load set 只能通过 `LoadSet` / `PrewarmLoadSet` operation 进入 resident；`LoadAsset` 不隐式加载。
- `metadata_only` 只允许 identity/key/path/find/dependency summary 查询，不允许 generated field getter；读取对象返回 not loaded 状态。
- `external_segment` 表示 payload 在外部包或平台资源系统中；open 只验证 manifest，`LoadSet` 阶段由 provider 绑定 immutable bytes view。
- `unload_policy=explicit` 表示只在显式 `UnloadSet` operation 卸载；默认不做 LRU/时间驱动自动 eviction，避免帧中不可预测分配和引用失效。
- `pin_policy=while_referenced` 时，只要存在 materialized object wrapper、active `RuntimeSnapshot`、active ChangeSet view、pending dependency traversal 或 provider handle，卸载产生 `runtime.load_set_unload_blocked`。

load set materialization 默认规则：

- converter / verify 必须把每个 `LoadSetDescriptor` 展开为 canonical `load_set_object_index`、`load_set_segment_index` 和 optional `load_set_dependency_closure`；runtime 不在首次 `LoadSet` 时重新解释字符串 selector。
- 展开输入只能来自当前 export view 的 identity/key/path/reference/dependency/preload index；不能读取 Excel header、workbook 行号、文件枚举顺序、本机目录或运行时当前对象状态。
- `root_selector=all` 选择当前 export view 内所有 exported root assets，按 table id、row guid/local id、asset identity 排序。
- `root_selector=table` 只按 table id 选择 exported rows；table display name、C# type name 或 sheet name 不能作为 root 身份。
- `root_selector=schema_group` 只使用 schema descriptor 中稳定 group id / label descriptor；不能从 `category/tags` 列名临时猜 group。
- `root_selector=asset_path_prefix` 使用 14.15 的 canonical asset path bytes 做 prefix match；path display、workbook alias display 或未 canonical percent-encoding 的文本不能参与匹配。
- `root_selector=explicit_assets` 必须列出 stable `AssetIdentity` 或可在 convert 阶段唯一解析的 canonical key；解析不到、重复或歧义产生 `bytes.load_set_invalid`。
- `root_selector=dependency_closure` 先解析 root set，再按 descriptor 声明的 `include_dependency_kinds[]`、`include_target_families[]`、traversal/depth/cycle policy 展开；cycle 用 visited set 截断并按 stable edge order 输出，不依赖 DFS/BFS 发现顺序。
- ownership child 默认随 owner root 进入同一 load set，除非 child descriptor 声明 `metadata_only` 或独立 load set boundary；parent payload resident 但 owned child payload 未 resident 时，generated child getter 必须返回 not loaded，而不是 missing。
- root selector 展开为空时默认产生 `bytes.load_set_invalid`；只有 descriptor 显式 `allow_empty=true` 的工具/debug load set 可以为空，并且 manifest 必须记录 empty reason。
- load set id 在同一 artifact 内全局唯一；重复 id、非法 id、引用不存在 segment、segment 不反向引用 load set、required segment 不属于任何 reachable load set 都产生 `bytes.load_set_invalid`。

load set overlap / sharing 默认规则：

- 同一 asset identity 可以被多个 load set 覆盖；runtime 仍只允许一个 canonical resident wrapper / payload binding，不为每个 load set 复制对象。
- segment payload 是 immutable slice，可以被多个 load set 共享；loaded residency 使用 per-session load set bitset + per-object/segment reference count 计算。
- 加载第二个覆盖同一 object 的 load set 时，如果该 object payload hash/layout 与已 resident binding 一致，只增加 residency ref；如果不一致，artifact 是无效的，产生 `bytes.load_set_invalid` 或 `bytes.segment_mismatch`。
- `UnloadSet(A)` 只清除 A 的 residency bit；仍被其他 loaded load set、snapshot、ChangeSet、dependency traversal 或 provider handle 引用的 object/segment 保持 loaded。
- object 的 `ObjectState.Unloaded` 只在没有任何 loaded load set 覆盖其 payload 且没有 pin 保持 wrapper resident 时进入；不因卸载其中一个重叠 load set 就抖动状态。
- `GetLoadSetStatus(id)` 基于当前 session residency bitset 返回状态；它不触发 provider IO、不检查文件时间、不重新校验 checksum。

runtime residency 默认规则：

- object slot identity 可以先存在；object payload 和 wrapper 可以未 resident。
- `LoadAsset<T>` 命中未加载 load set 时返回 null；`TryGetAsset` 返回 false，并在 operation report 中记录 `runtime.load_set_not_loaded`。
- 引用指向未加载 target 时，hard reference 不变成 missing；getter 必须能区分 `unloaded` 与 `missing/deleted`，debug/report 给出 target load_set_id。
- `FindAssets`、`GUIDToAssetPath`、dependency direct query 可以基于 core index 工作，不强制加载 payload。
- generated field getter 只能在 object payload resident 时调用；debug build 可断言，Release 默认返回 safe default/false 或最小 error state，具体由 generated accessor policy 声明。
- object wrapper 如果 lazy materialize，第一次 materialize 属于 explicit load/prewarm 或低频 `LoadAsset`；它不能被算作 no-GC hot path，除非已经通过 `PrewarmLoadSet` 完成水位线预热。

segment loading 默认规则：

- `LoadSet` / `PrewarmLoadSet` 是 operation，不是普通 getter；可以读取文件、映射外部 segment、解压、扩容水位线并生成 report。
- `LoadSet` 必须 all-or-nothing：先验证所有 required segments / provider leases / capacity / schema/export view/source revision，再一次性发布 residency change；任一 blocker 时旧 residency 不变。
- 同一 load set 第二次加载必须是 no-op；同一 source revision + capacity 下重复 `PrewarmLoadSet` 不得再次产生 GC allocation。
- segment checksum、source hash、load_set_id、export_view_hash、schema_hash 不匹配时产生 `bytes.segment_mismatch`，保留旧 resident state。
- 必需 segment 缺失产生 `bytes.segment_missing`；optional debug segment 缺失只影响 debug report。
- unload 只能释放 payload/object wrapper/provider handle；core identity/key/dependency index 默认保留到 `Close`。
- async/Addressables/provider 场景必须先由 host 完成显式 preload / provider lease acquire，再让 `LoadSet` 消费 immutable lease；默认 `LoadSet` 不启动不确定完成时间的后台加载，也不在 getter 中轮询。
- `PrewarmLoadSet` 在 `LoadSet` 基础上 materialize declared object wrappers、generated accessor binding、string/blob/range views 和 dependency/preload ranges；`object_materialization=no_wrappers` 时只预热 read views，不创建 `Object` wrapper。
- `UnloadSet` 不能释放 core index、identity/key/reference/dependency range；只释放该 load set 独占且未被 pin 的 payload segment、wrapper、string/blob view 和 provider lease。
- load/unload 成功默认发布 `source_summary` / `load_set_summary`，不发布 property_changed；只有对象由 unloaded 变为 readable 或反向变为 unloaded 且 profile 要求 object lifetime notification 时才发布对应 lifetime summary，不伪装为 added/removed。

manifest / cache 默认规则：

- artifact manifest 记录 `load_set_manifest_hash`、`segment_index_digest`、`load_sets[]` 和 `segments[]`。
- `load_set_manifest_hash` 覆盖 LoadSetDescriptor、root selector、dependency closure policy、residency/object materialization/unload policy 和 segment membership。
- `segment_index_digest` 覆盖 segment id/kind/load_set_id/source hash/offset/length/checksum/compression。
- 改变 load set 切分但不改变 data semantic 时，`source_hash` 不变，但 `bytes_hash`、`load_set_manifest_hash`、`segment_index_digest` 和 cache key 改变。
- `verify-bytes` 必须校验 segment range 不重叠、segment checksum、load_set_index range、load_set -> segment 双向一致性。

hot reload / source switch 默认规则：

- source switch candidate 先构建目标 load set manifest 和 segment index；当前已 resident 的 load set 默认尝试在新 source 上保持 resident。
- 如果 resident load set 的 payload 形状兼容，按普通 patch plan patch object；不兼容时相关 object recreated 或 unloaded，并发布 ChangeSet。
- 被卸载的 load set 中的 object 发布 `unloaded` / source_summary，不发布 `removed`，除非 materialized source 中该 row 被 tombstone/delete。
- load set manifest 变化但当前 resident object value 不变时，只发布 source_summary/load_set_summary。

reader open 默认流程：

1. 建立 immutable bytes view。path-based source 可以 memory-map 或 read-only file buffer；memory source 必须先按 `ConvertedBytesBufferOwnership` 固定 copy / take ownership / borrow immutable 语义。
2. 校验 header：magic、format_version、artifact_kind、endianness、file_size、content_checksum、schema_hash、export_view_hash、build_target/profile policy。
3. 读取并校验 section directory：范围、alignment、顺序、重复、checksum、required/optional。
4. 建立 section views：schema manifest、string table、object data、indexes、debug symbols。此阶段允许初始化分配。
5. 交叉验证 counts 和 ranges：table range、object slot、field layout、state bitset、identity/key/reference/dependency index、string/blob/value range offsets。
6. 校验 schema descriptor hash 与当前 generated registry 匹配；不匹配时 open/switch 失败并保留旧 source。
7. 构建或绑定 runtime indexes；完成 `Reserve/Prewarm` 后字段读取、引用解析、依赖遍历不得产生 GC allocation。

`ConvertedBytesDataSource(byte[] bytes)` 默认规则：

- `ConvertedBytesDataSource(byte[] bytes)` 是安全易用入口，等价于 `ownership=Copy`；copy 发生在 source initialization/open 前，归入 initialization allocation，不进入稳定 hot path。
- `CopyFrom(ReadOnlySpan<byte>)` 显式复制输入；适合来自临时 buffer、网络包、解压 workspace、会复用的下载缓存。
- `TakeOwnership(byte[])` / `ownership=TakeOwnership` 不复制；调用后数组所有权转移给 DataSource，调用方不得再写入、池化、归还或复用该数组，直到该 DataSource 已关闭且没有 active snapshot/load set/provider pin。
- `BorrowImmutable(ReadOnlyMemory<byte>)` / `ownership=BorrowImmutable` 不复制；调用方或宿主必须保证底层内存在 DataSource 生命周期内 immutable 且不会被释放，典型来源是 read-only memory mapped file、Unity immutable asset blob、asset bundle immutable view 或自定义 provider lease。
- `TakeOwnership` / `BorrowImmutable` 在 open/switch/load-set 阶段必须记录 `bytes_hash`、`content_checksum`、buffer owner kind、lifetime token/generation 和 source revision；不允许把裸数组引用直接散落到 reader 之外。
- 如果在 open/switch/load-set/refresh/debug safety check 中发现 borrowed/owned memory 的 `content_checksum` 或 sampled guard range 与已发布 source revision 不一致，产生 `bytes.source_mutated`，本次 operation 失败；若旧 source 仍可用则保留旧 source，若当前 source 已被污染则标记 current source invalidated，后续读取按 `unloaded/invalid source` 失败路径处理，直到 `Close()` 或成功 `SwitchDataSource()`，且不得从污染 buffer 发布新的 ChangeSet。
- 稳定字段 getter 不重新计算全文件 checksum；需要防止外部写入的项目必须使用 `Copy`、只读 OS mapping、immutable provider lease，或在 Development/CI 开启 safety check。no-GC 证明不能依赖 per-read checksum。
- memory bytes 没有稳定 path；report 使用 bytes_hash / source_hash / source revision / buffer owner kind 定位。

`verify-bytes` 默认检查：

- 重新计算 `content_checksum`、`bytes_hash`，并与 manifest 对比。
- 校验 header、format_version、endianness、file_size、section directory、required section、section checksum。
- 校验 schema_manifest 中 descriptor_hash / schema_hash / export_view_hash / source_hash / source_set_hash 与命令输入 schema、profile 和 manifest 一致。
- 校验 primitive encoding、section element_size、record array length、8-byte alignment、invalid sentinel 和 offset/range 边界。
- 校验 table_layouts / field_layouts 与 generated binding manifest 一致，包括 object_record_size、field storage kind、state bit index、fixed offset、slot/range kind。
- 校验 object record 的 state bitset：missing/null/default/explicit state 合法，non-null 字段没有 explicit_null，default_materialized 字段能找到 schema default。
- 校验 string table UTF-8、string id 顺序、object record range、identity index、key index、reference index、dependency/reverse dependency index。
- 校验 value_range、child_range、map_range、union_payload 的 start/count、variant id、element layout、canonical key order 和 payload range。
- 校验所有 object slot 可达性：table_directory range 覆盖 object_data 中的 object slot，identity/key/reference/dependency 不指向不存在 slot。
- 启用 segmented bytes 时校验 segment_directory、load_set_index、segment checksum、load set 到 segment 的双向映射和 external segment manifest。
- 有 debug_symbols 时校验其 location 只用于 report，不参与 source_hash，不允许影响 runtime 读取结果。

### 14.10 默认 runtime no-GC 实现规则

达到水位线后的 runtime 热路径默认不得产生 GC allocation。热路径包括：

- `RuntimeDatabase.LoadAsset`。
- key/path lookup。
- object reference resolve。
- dependency traversal。
- converted bytes read。
- source switch commit。
- hot reload patch。
- change event dispatch。

实现默认选择：

- 热路径不用 LINQ。
- 热路径不用反射查字段；反射只能出现在 schema/import/bake/init 阶段。
- 热路径不用闭包捕获、装箱、临时字符串拼接、`yield return`、分配式 `IEnumerator`、异常作为控制流、按字段创建临时对象。
- 对外遍历 API 提供 struct enumerator 或 caller-provided buffer 版本。
- change event 默认 batch/coalesce 到预分配 `ChangeSet`。
- key/path 在 import/bake 阶段完成 hash/intern/cache；runtime lookup 不构造新字符串。
- 超过水位线时可以扩容，但必须记录 allocation report，并允许用户预热或 reserve。

分配边界默认定义：

- 允许分配：schema load/codegen、source open、首次批量导入、convert、`Reserve` / `Prewarm`、operation workspace 初始化、超过历史最大容量的水位线增长。
- 显式低频投影允许按 profile 分配：human-readable report、diagnostic 字符串、Excel 批注、Unity console/UI 展示和 debug log。
- 不允许分配：已达到水位线后的常规读取、key/identity lookup、snapshot acquire/read/release、引用解析、依赖遍历、converted bytes field read、source switch commit、hot reload patch、ChangeSet dispatch、editor core preflight、machine-readable report append。
- `ExcelDataSource` refresh 的 workbook parse / XML/ZIP read 也必须使用可复用 parser workspace；只有 workspace 容量不足时才允许水位线增长。
- 第三方 xlsx 后端如果每次 refresh 都产生不可控 GC，只能作为 Editor authoring/debug fallback，不能作为 Development runtime hot reload 的默认实现。
- 调用方自己在热路径拼接字符串、创建 delegate、创建数组造成的分配不计入库内部 GC budget，但 API 必须提供不用这样做的入口。

水位线默认覆盖：

- resident object pool / object slot。
- key/path hash index。
- row guid/local id index。
- reference index / reverse dependency index。
- dependency edge buffer。
- stable string pool：string entry、UTF-8/string bytes、materialized string reference table。
- repeated / child range view table。
- loaded object / load set residency table。
- loaded segment table 和 segment provider workspace。
- runtime snapshot handle pool、snapshot pin table 和 snapshot read context view table。
- candidate snapshot / diff buffer。
- ChangeSet / event buffer。
- ExcelDataSource parser workspace，包括 ZIP/XML/shared string/cell value 暂存区。
- ConvertedBytesDataSource reader workspace，包括 section cache、string table view、blob view。
- validation/runtime patch 临时工作区。

API 形态默认要求：

- 字符串 API 是易用入口，可以用于 editor、初始化和低频路径。
- 高频路径默认先把 key/path 解析成 `AssetKey` 或 `AssetIdentity`，之后用 struct key/identity 查询。
- 批量枚举默认提供 caller-owned `Span<T>` / array buffer 版本。
- dependency traversal、reference enumeration、changed event enumeration 默认提供不分配的 indexed view 或 struct enumerator。
- `GetLastOperationReport()` 返回 operation 已经构建好的 report；它不是稳定帧热路径的一部分。

数据布局默认选择：

- converted bytes 中 runtime string 使用 string table / string id；读取字段不创建新 string，除非调用方显式请求 materialize。
- generated runtime type 使用 generated accessor 或预绑定 delegate table；不在热路径反射查字段。
- repeated scalar / child table runtime 数据使用 contiguous range + count，遍历不创建临时 list。
- reference 字段保存 identity / slot index，解析时走 index，不解析显示 key/path。
- ChangeSet event 使用 struct entry buffer，`field_ids` / `property_paths` 默认是切片视图，不为每个 event 创建数组。

观测默认要求：

- 每次水位线增长写入 `allocation_summary`，包括 buffer kind、old capacity、new capacity、operation kind、source identity。
- 项目可以把水位线增长设置为 warning 或 CI/benchmark failure。
- Unity 2022.3 adapter 用 ProfilerRecorder / GC allocation sample 验证；核心 runtime 用 allocation counter / benchmark 验证。
- 性能测试至少覆盖 converted bytes 读取、ExcelDataSource refresh commit、source switch commit、hot reload patch、ChangeSet dispatch。

#### 14.10.1 默认 generated runtime accessor / read view 协议

generated C# type 是 runtime 热路径的主读取表面。它必须把 converted bytes / resident object layout 的 no-GC 约束体现在 API 形态上，而不是靠调用方猜哪些 getter 安全。

scalar getter 默认规则：

- generated getter 只能读预绑定 object slot、resident field 或 converted bytes field layout；不反射、不查字典、不装箱。
- integer、bool、float、enum getter 返回 primitive value；enum getter 使用 schema value id / generated enum mapping，不按显示 token 解析。
- optional/default 字段的普通 getter 返回 schema materialized value；需要区分 missing / explicit null / default materialized 时，generated type 必须提供 no-GC state accessor，例如 `HasX`、`IsXNull` 或 `TryGetX(out T value)`。
- runtime read-only mode 下 generated setter 默认不生成；Editor authoring 下如生成 public setter，也只写 direct mutation overlay，必须再由 `SetDirty` / `SerializedObject` 进入 draft，不能在 resident runtime object 上静默制造 dirty。

string getter 默认规则：

- converted bytes 中字符串身份是 string id；热路径不从 UTF-8 bytes 每次创建 `string`。
- public `string` getter 只有在字符串已经于 open / prewarm / candidate build 阶段进入 stable string pool 时才是 hot-path safe。
- 如果 build profile 选择 lazy string materialization，第一次 materialize 属于初始化/水位线增长或显式低频 API；完成 `Prewarm()` 后同一 getter 不能再分配。
- source switch / hot reload 的 candidate build 必须先准备新 string pool entry；commit 只交换 string id / stable string reference，不在发布事件或 patch resident object 时构造字符串。
- 需要避免 string materialization 的低层 API 可以暴露 `StringId` / `Utf8StringView` / generated debug view，但这些 view 生命周期必须绑定当前 source revision。

repeated / child view 默认规则：

- runtime generated type 不默认暴露可变 `List<T>` 或会分配枚举器的 `IEnumerable<T>` 作为热路径入口。
- repeated scalar 默认暴露 count + indexer / `ReadOnlySpan<T>` / generated `ValueRange<T>` struct view；是否能用 `ReadOnlySpan<T>` 取决于该字段在 runtime layout 中是否 contiguous 且元素表示固定。
- repeated reference / child object 默认暴露 count + indexer / generated `ObjectRange<T>` struct view；indexer 通过 slot range 返回 canonical resident instance。
- child table backed array 使用 parent slot 中保存的 child range index；遍历按 child range 的 `start + count`，struct enumerator 不分配。
- 如果为了 Unity inspector 或调试提供 `IEnumerable<T>` / `IReadOnlyList<T>` convenience API，必须标为 editor/low-frequency，不作为 no-GC hot path 验收入口。
- 任何 `ToArray()`、`ToList()`、copy-out API 都必须是显式调用，并把分配归属调用方；默认读取不做 defensive copy。

reference getter 默认规则：

- internal asset reference getter 保存 target slot / identity handle；读取时走 slot index，返回 canonical resident instance 或 missing/null 状态，不解析显示 key/path。
- hard reference missing 不应出现在成功 runtime open 的有效 object graph；如果 debug mode 允许 invalid graph，getter 必须返回 null/false 并保留 diagnostic state，不能抛异常作为常规控制流。
- soft reference 和 `leave_missing` reference 必须提供 no-GC missing state 查询；不能把 missing 伪装成新对象或空 identity。
- `UnityResourceRef` runtime getter 返回 provider id/runtime key/guid-derived handle；不访问 UnityEditor.AssetDatabase，不读取 `main_asset_path` 兜底。

patch / source switch 默认规则：

- candidate snapshot build 阶段完成 object slot、string pool、range view、reference slot 和 child range 的准备；此阶段允许初始化/水位线分配。
- commit 阶段只 patch primitive field、string reference/id、range start/count、reference slot 和 object state flag；达到水位线后不得创建 list、array、delegate、closure、string 或 reflection artifact。
- 若字段 layout 改变导致 generated accessor 不能复用旧 resident field，必须走 `recreated` event；不能在旧 object 上挂一份临时 adapter map。
- generated accessor manifest 必须记录 field id -> runtime field offset / string id slot / range slot / reference slot；reader open 校验 manifest 与 schema descriptor hash 匹配。

#### 14.10.2 默认 allocation budget / verification 协议

no-GC 不是“实现尽量优化”，而是核心能力声明。任何 source backend、runtime API、editor core operation、adapter 或 extension 只要进入稳定读取、稳定刷新、commit/publish、machine-readable report append 或 benchmark 窗口，就必须能被同一套 allocation budget policy 和 report 验证。

全局 allocation contract 默认规则：

- `ExcelDbEngine` 与 `ExcelDbEditor` 的 core operation 在完成 context init / `Reserve` / `Prewarm` / workspace warmup 后，默认只能复用既有对象、池、writer、span、range view 和 string table。
- operation 拆分为 `prepare/build`、`preflight`、`commit`、`publish`、`core_report_append`、`projection` 六类 phase；只有 `prepare/build` 中的初始化和水位线增长可以分配。
- `preflight` 可以构建 OperationPlan、diff、diagnostic 和 backup/side-effect ledger，但必须使用 operation workspace；容量不足只能登记 watermark growth 或按 policy 失败。
- `commit` 和 `publish` 不能分配；如果 commit 前发现容量不足，必须在切换 visible state 之前增长或失败，不能发布一半后再扩容。
- `core_report_append` 不能分配；diagnostic 必须先写 code、severity、stable ids、source location token、hash 和 fixed-size payload，message 文本延迟到 projection 生成。
- `projection` 是显式低频输出，例如 human-readable report、Unity console message、Excel comment、UI tree；它可以按 profile 使用分配，但不能被拿来证明 core operation no-GC，也不能进入 report_hash。
- 外部 backend 必须声明 `allocation_class: no_gc | watermark_only | allocating_projection | allocating_core`；`allocating_core` 只能用于 editor/tool debug 路径，不能进入 Release runtime source、benchmark-no-gc 或 strict CI gate。
- source backend 如果只能通过会分配的第三方 xlsx/xml/zip API 读取文件，可以作为 authoring import backend；要作为运行时 ExcelDataSource hot reload backend，必须提供 no-GC wrapper、池化 backend 或把分配全部归因到 prepare/build 的水位线增长。

allocation policy 默认集合：

```text
allow_watermark_growth
report_watermark_growth
fail_on_watermark_growth
fail_on_any_hot_path_allocation
```

- 协议文本使用 snake_case；C# API 使用 PascalCase enum；CLI 参数使用 kebab-case，三者必须一一映射，不能出现同义别名。
- `allow_watermark_growth`：水位线增长合法，只写 `allocation_summary`；真实 hot path allocation 仍产生 `runtime.allocation_budget_exceeded`。
- `report_watermark_growth`：水位线增长产生 `performance.watermark_grew` info/warning，允许本次 operation 成功。
- `fail_on_watermark_growth`：任何水位线增长都使本次 benchmark/CI gate 失败；普通 editor operation 可按 policy 降级为 warning。
- `fail_on_any_hot_path_allocation`：热路径出现 GC allocation 或未登记水位线增长，直接产生 `runtime.allocation_budget_exceeded`，source switch/hot reload commit 默认失败并保留旧 source。

测量窗口默认规则：

- 初始化窗口：schema registry init、source open、首次导入、`Reserve`、`Prewarm`、candidate snapshot build；允许分配，但必须能归因到 initialization 或 watermark growth。
- 稳定读取窗口：`LoadAsset`、`TryGetAsset(AssetKey/AssetIdentity)`、generated getter、reference resolve、dependency traversal、`GetAssets(Span<T>)`；默认零分配。
- 稳定 editor preflight 窗口：workbook fingerprint no-op、schema layout dry-run、dirty diff classify、conflict classify、save preflight；达到水位线后默认零分配。
- 稳定 refresh commit 窗口：fingerprint no-op、candidate 已构建后的 patch plan commit、index swap、ChangeSet dispatch；默认零分配。
- Excel parser 窗口：重复 `ExcelDataSource.Refresh()` 使用 parser workspace；只有 workspace 容量不足时登记水位线增长。
- core report append 窗口：diagnostic、diff entry、allocation entry、side-effect ledger entry 写入 machine-readable report builder；达到水位线后默认零分配。
- report projection 窗口：human-readable 文本、Excel 批注、Unity console/UI 展示；默认不参与 no-GC core proof，若 profile 要求 strict projection，则必须写入 caller-provided writer/buffer 并单独测量。
- external backend 窗口：xlsx/zip/xml/Unity/文件系统 backend 的不可控分配；必须按 backend capability 单独记录，不能和 core operation 的 `gc_bytes=0` 混在一起。
- 订阅回调窗口：库只保证 ChangeSet view 不分配；subscriber 自己分配不计入库内部 budget，但如果库提供的 convenience event 诱导分配，该 API 不能声明 hot-path safe。

watermark growth 默认规则：

- 每一种可增长 buffer 都必须有 stable `buffer_kind`，例如 `object_slots`、`key_index`、`dependency_edges`、`change_events`、`diff_entries`、`parser_workspace_bytes`、`string_entries`、`string_bytes`、`range_views`。
- 扩容前必须知道 requested capacity；扩容后写 old/new/requested capacity 和 operation kind。
- 同一输入规模、同一 `RuntimeCapacity`、同一 source revision 的第二次运行不得再次增长；否则是 budget bug，而不是正常水位线增长。
- 水位线增长不能发生在 object graph 已经对外切换之后；commit 前增长可以回滚，publish 后增长默认是 hot path violation。
- 因 event buffer 不足导致扩容或被 policy 阻止时，必须额外产生 `runtime.event_buffer_overflow`，不能只写 performance summary。

hot path allocation 默认判定：

- core operation measurement window 内没有登记为 watermark growth 的 GC allocation，一律按 allocation budget violation 处理，即使发生在 editor preflight、report append 或 external backend adapter 里。
- 装箱、分配式 enumerator、`ToArray/ToList`、LINQ iterator、闭包、字符串拼接、反射 artifact、异常对象、per-event/per-property array 都属于违规来源。
- Debug-only assertion、Profiler marker、human-readable message 构建不在 Release/runtime hot path 执行；Editor/Development 若执行，也必须在 report 中标明 measurement window，不得污染 Release 验收。
- `objectChanged` 等 convenience API 默认不作为 no-GC 验收入口；如果实现声明它 no-GC，必须和 `changed` 一样用预分配 buffer 证明。
- 如果 API 失败路径通过抛异常表达常规数据错误，该失败路径不算 no-GC；core API 必须用 bool/status/report 表达 source 内容错误，异常只保留给调用方编程错误或 internal unexpected。

验证默认流程：

1. 使用固定测试 source，先执行 open + reserve + prewarm，丢弃初始化期测量。
2. 清空 allocation counters / ProfilerRecorder sample baseline。
3. 执行稳定读取窗口 N 次，N 默认至少 1000，覆盖 scalar/string/reference/repeated/child getter。
4. 执行 no-op refresh / source switch equivalence / patch refresh / ChangeSet dispatch。
5. 执行 editor core preflight 和 core report append 窗口，覆盖 dry-run、conflict classify、machine-readable report entry append。
6. 如需 human-readable report 或 Excel comment projection，单独开启 projection measurement，不能覆盖 core window 结果。
7. 读取 allocation counter，生成 `allocation_summary`。
8. 再次以同一 capacity 和 source revision 重跑；如果第一次只有 watermark growth，第二次必须为 0 GC bytes / 0 growth。
9. 根据 policy 将 `performance.watermark_grew` 或 `runtime.allocation_budget_exceeded` 映射到 warning/error/blocker 和 exit code。

profiler backend 默认规则：

- 核心 .NET 测试使用宿主无关 allocation counter 或 benchmark harness；不能只靠 wall time。
- Unity 2022.3 adapter 使用 ProfilerRecorder / GC allocation sample；必须记录采样是否可靠。
- IL2CPP / ReleasePlayer 若无法得到逐调用 allocation sample，必须使用场景级 before/after GC allocated bytes + 重复运行证明；`sample_reliable=false` 不能作为 CI 通过证据。
- benchmark 不能在测量窗口内构建 human-readable report、拼接日志或调用会分配的 assertion message。

report / gate 默认规则：

- `performance.watermark_grew` 只表示允许的水位线增长；它必须有对应 `watermark_growth_entries[]`。
- `runtime.allocation_budget_exceeded` 表示违反 policy 或出现未允许 hot path allocation；它必须有 `hot_path_allocation_entries[]` 或 profiler backend 的最小定位。
- CI 中 `--allocation-policy fail-on-any-hot-path-allocation` 是 runtime benchmark 默认策略。
- 普通 editor authoring 可以使用 `report_watermark_growth`，但 Release build gate 必须至少使用 `fail_on_watermark_growth` 验证 converted bytes runtime 读取，strict CI / benchmark-no-gc 默认使用 `fail_on_any_hot_path_allocation` 验证 runtime、editor core preflight 和 core report append。
- `external_backend_allocation_entries[]` 或 `report_projection_allocation_entries[]` 可以被 editor profile 接受；它们不能抵消 core window 的 violation，也不能作为 no-GC 通过证据。

#### 14.10.3 默认 RuntimeCapacity / Reserve / Prewarm 协议

`RuntimeCapacity` 是 no-GC 承诺的输入，不是性能调优备注。所有水位线 buffer 都必须有对应 capacity dimension、buffer_kind、增长记录和推荐值；否则 `Reserve/Prewarm` 无法把一次运行中发现的峰值转成下一次稳定运行的零分配前置条件。

`RuntimeCapacityDescriptor` 物理格式默认规则：

- CLI / CI / benchmark 使用 canonical JSON，默认文件名可为 `capacity.json`；C# API 使用 `RuntimeCapacity` struct，必须能和 descriptor 无损互转，简化构造器只能作为常用快捷入口。
- JSON 使用 UTF-8 without BOM、LF 换行、object key 升序；不写本机路径、用户名、当前时间。
- `runtime_capacity_hash = sha256("exceldb-runtime-capacity-v1" + canonical RuntimeCapacityDescriptor bytes)`。
- capacity 只影响初始化分配、watermark/gate 和 report，不影响 `source_hash`、`source_set_hash`、`bytes_hash` 或 converted bytes cache key。
- 如果 capacity policy 影响 benchmark pass/fail，它进入 `gate_policy_hash` 或 operation report；如果只改变预留数量，不进入 bytes cache key。

`RuntimeCapacityDescriptor` 默认字段：

```text
capacity_format_version
runtime_capacity_hash
source_scope optional
operation_profile_id optional
build_target optional
global_capacity
load_set_capacities[] optional
parser_workspace_capacity
reader_workspace_capacity
candidate_workspace_capacity
headroom_policy optional
```

`global_capacity` 默认字段：

```text
object_count
key_count
reference_count
dependency_edge_count
reverse_dependency_edge_count
string_entry_count
string_byte_count
range_count
change_event_count
diff_entry_count
snapshot_handle_count
snapshot_pin_count
operation_queue_count
pending_report_bytes
```

`load_set_capacities[]` 默认字段：

```text
load_set_id
loaded_object_count
loaded_segment_count
segment_workspace_bytes
object_wrapper_count
string_entry_count
string_byte_count
blob_bytes
range_count
```

workspace capacity 默认字段：

```text
parser_workspace_capacity:
  zip_entry_count
  shared_string_count
  shared_string_bytes
  worksheet_cell_count
  xml_buffer_bytes
  cell_value_buffer_count

reader_workspace_capacity:
  section_view_count
  string_view_count
  blob_view_count
  segment_view_count

candidate_workspace_capacity:
  candidate_object_count
  patch_entry_count
  dependency_edge_count
  validation_entry_count
  change_event_count
```

capacity dimension / buffer_kind 默认映射：

```text
object_count -> object_slots
key_count -> key_index
reference_count -> reference_index
dependency_edge_count -> dependency_edges
reverse_dependency_edge_count -> reverse_dependency_edges
string_entry_count -> string_entries
string_byte_count -> string_bytes
range_count -> range_views
change_event_count -> change_events
diff_entry_count -> diff_entries
snapshot_handle_count -> snapshot_handles
snapshot_pin_count -> snapshot_pins
operation_queue_count -> operation_queue
pending_report_bytes -> pending_report_bytes
zip_entry_count -> parser_zip_entries
shared_string_count -> parser_shared_strings
shared_string_bytes -> parser_shared_string_bytes
worksheet_cell_count -> parser_cells
xml_buffer_bytes -> parser_xml_bytes
section_view_count -> bytes_section_views
segment_workspace_bytes -> segment_workspace_bytes
```

Reserve 默认规则：

- `Reserve(RuntimeCapacity)` 只扩容，不收缩；收缩只能在 `Close` 或显式 debug/reset API 中发生，不能在热路径自动释放。
- capacity 中任一负数或超过 host 最大限制时产生 `runtime.capacity_invalid` 或 command invalid args；不进行部分 reserve。
- 无 current source 时，Reserve 只预留 context-global buffer；source/load-set-specific capacity 在下一次 Open/Prewarm/LoadSet 时应用。
- 有 current source 时，Reserve 可以立即扩容 object/index/string/range/snapshot/event/parser/reader/candidate workspace，并写 operation report。
- Reserve 成功后必须记录 effective capacity；同一或更小 capacity 再次调用不得分配。
- Reserve 不能清空 dirty draft、current source、loaded load set、snapshot pin 或 pending event；需要重建 context 必须 Close/Open。

Prewarm 默认规则：

- `Prewarm()` 是 operation，不是热路径 getter；允许分配并生成 report。
- Prewarm 至少完成：runtime index binding、stable string pool materialization、key/path handle cache、reference slot table、dependency range table、range view table、snapshot handle pool、ChangeSet buffer、candidate diff workspace、parser/reader workspace reserve。
- 对 eager resident load set，Prewarm 默认 materialize object wrapper 和 generated accessor binding；对 explicit load set，只预留 capacity，不读取 payload，除非 profile 声明 prewarm load sets。
- `PrewarmLoadSet(id)` materialize 该 load set 的 segment view、object payload、string/blob view、object wrapper 和 dependency/preload range；重复执行同一 source revision + same/effective capacity 不应再次分配。
- Prewarm 不执行 schema migration、不修复 metadata、不写 workbook、不改变 source data；它只准备运行时读取所需结构。
- 如果 Prewarm 发现 capacity 不足且 policy 允许增长，记录 `performance.watermark_grew`；如果 policy 禁止增长，operation 失败并保持旧 resident state。

capacity recommendation 默认规则：

- 每次 operation report 可以输出 `runtime_capacity_observed` 和 `runtime_capacity_recommendation`。
- 推荐值默认取 observed peak + headroom；headroom 默认 `max(ceil(peak * 1.25), peak + 8)`，bytes 类默认 `max(ceil(peak * 1.25), peak + 4096)`，项目可在 descriptor 中覆盖。
- recommendation 必须按 capacity dimension 输出，不只写 human-readable “建议增大 buffer”。
- benchmark-no-gc 默认要求提供 capacity descriptor；如果没有提供，可以先运行 discovery mode 生成 recommendation，但 discovery mode 不能算 pass。
- 同一 source revision、same/effective capacity、same load set residency 的第二次 benchmark 若仍有 watermark growth，默认视为 capacity/implementation bug。

allocation report 补充字段：

```text
runtime_capacity_hash optional
requested_runtime_capacity optional
effective_runtime_capacity optional
runtime_capacity_observed optional
runtime_capacity_recommendation optional
capacity_recommendation_hash optional
```

### 14.11 默认 source switch / hot reload 语义

source switch 和 hot reload 默认都按 candidate snapshot 处理：

- 先完整导入 candidate source。
- 校验 schema hash、source hash、identity map、reference graph、validation blocker。
- 在 commit 前完成必要 buffer reserve；如果触发水位线增长，本次 allocation 合法，但必须可观测。
- commit 时优先 patch 已有 object，不重建可保持 identity 的对象。
- 删除的 object 进入 `removed` / `missing` 语义，不让旧引用静默指向新对象。
- 新增 object 分配 stable identity 后加入 index。
- key/path 变化触发 moved/renamed 事件，不改变 row guid/local id。
- 事件发布顺序固定为 removed、added、moved、renamed、recreated、property_changed、dependency_changed、source_summary。

如果 candidate 失败：

- 旧 source 继续服务。
- 旧 object graph 不变。
- 不发布成功事件，只发布失败 report。
- editor/development build 可以显示失败诊断；正式 runtime 默认只保留机器可读错误码和最小日志。

### 14.12 默认 mode / mutability 矩阵

系统默认有四种运行模式。所有 API 都必须能知道当前 mode，禁止靠调用方猜。

```text
Editor authoring：允许 Excel 写回，允许 SerializedObject 编辑，允许 metadata repair。
Editor Play Mode debug：允许 ExcelDataSource，允许 hot reload，默认不写回运行时修改。
Development build：允许受控 ExcelDataSource/hot reload/source switch，默认 read-only；Excel 写回只能通过显式 debug authoring bridge 或配对的 Editor authoring context，不在 player runtime 中默认开放。
Release：只允许 ConvertedBytesDataSource 或可信 CompositeDataSource，read-only，不允许 Excel 写回，不允许动态 schema migration。
```

默认能力矩阵：

```text
能力                                  Editor authoring  Play Mode debug  Development build  Release
ExcelDataSource                       yes               yes              opt-in             no
ConvertedBytesDataSource              yes               yes              yes                yes
CompositeDataSource                   yes               yes              opt-in             signed converted layers only
AssetDatabase writeback               yes               editor-only      debug-authoring-bridge only  no
SerializedObject edit                  yes               editor-only      debug-authoring-bridge only  no
Hot reload (watcher/dev refresh)       yes               yes              opt-in             no
Signed source/layer activation         yes               yes              opt-in             signed patch/layer switch only
Metadata repair                       yes               no               no                 no
Large human report                     yes               yes              limited            no
Machine-readable report                yes               yes              yes                minimal
```

默认规则：

- Release 模式遇到需要 Excel/source 写回的 API 必须明确失败，不能静默 no-op。
- Release 模式中的 CompositeDataSource 只能包含 converted base 和可信 patch/DLC/hotfix layer；Excel layer、development override 和 unsigned patch 默认失败。
- `Hot reload` 专指 watcher/path/debug source refresh；Release 禁止该能力。Release 允许运行中更新时只能走显式 `Open` / `Refresh` / `SwitchDataSource` 的 signed source/layer activation，输入必须是可信 converted bytes / patch layer。
- Development build 的 hot reload/source switch 必须有显式开关和可见 mode 标识。
- Development build 的 writeback 不属于普通 runtime 能力；启用时必须声明 `debug_authoring_bridge`、目标 Editor authoring context / authorized workbook backend、write lease、report detail 和不可进入 Release 的 hard invariant。
- debug authoring bridge 只把请求转交给 authoring operation transaction；player runtime 不能绕过 `OperationProfile`、schema compatibility、metadata flush、SaveAssets preflight 或 workbook write lease 直接写 xlsx。
- Editor Play Mode 中的运行时对象修改不默认写回 Excel；要写回必须走 editor authoring API。
- mode 变化不能改变 schema/metadata 语义，只改变可用能力和 report 详细程度。

#### 14.12.1 默认 project policy / operation profile 协议

`policy` 不能散落在按钮、CLI 参数、Unity adapter 分支和 runtime if 里。项目长期规则必须先进入 `ProjectPolicyDescriptor`，一次操作的所有最终规则必须归一化为 `OperationProfile`；operation、report、cache、bytes、runtime open 和 CI gate 只读取归一化后的 profile，不回头解释原始命令行或 UI 状态。

物理格式默认规则：

- `ProjectPolicyDescriptor` 默认是 schema root 下的 canonical JSON artifact，默认文件名为 `ExcelDB.ProjectPolicy.json`；它和 schema descriptor 一起进入 schema-lint。
- `ProjectPolicyDescriptor` 只保存项目可配置规则、named profiles、host/build overlays、override allowlist、extension policy namespace 和 command default profile 映射；不保存本机绝对路径、当前用户、当前时间或 Unity Library path。
- 外部 `--profile <path>` 使用同一个 canonical JSON schema；它可以作为临时 profile input，但仍必须通过 project policy 的 override allowlist 和 hard invariant 校验。
- `OperationProfile` 是 normalize 后的完整值对象，不允许只保存 diff；report/manifest 中记录 `profile_id`、`profile_version`、`operation_profile_hash`、`gate_policy_hash`、可选 `convert_profile_hash`，以及 profile source 摘要。
- profile source 摘要只记录 source kind/id/revision/hash 和 override provenance，不把 secret、local path 或 human-readable UI 文案写入 hash。
- profile 文件格式过新时默认 read-only / report-only；convert、writeback、migration apply 和 build 必须失败并产生 `command.profile_invalid`。

`ProjectPolicyDescriptor` 默认字段：

```text
policy_format_version
project_policy_id
project_policy_version
default_profile_by_operation{}
named_profiles{}
host_overlays{}
build_target_overlays{}
override_allowlist{}
hard_invariants{}
diagnostic_policy{}
extension_policy_namespaces{}
confirmation_policy{}
cache_policy{}
```

`OperationProfile` 默认字段：

```text
profile_id
profile_version
operation_kind
runtime_mode
host_kind
build_target optional
source_profile
source_layer_profile
mutability_profile
schema_operation_profile
metadata_profile
diagnostic_gate_profile
data_loss_confirmation_profile
auto_fix_profile
structure_write_profile
save_profile
backup_retention_profile
recovery_profile
manifest_profile
allocation_profile
report_detail_profile
convert_profile optional
build_profile optional
runtime_startup_profile optional
extension_permission_profile
extension_policy_overrides{}
override_provenance[]
hard_invariants[]
```

内置 profile id 默认集合：

```text
editor_authoring
editor_play_mode_debug
development_build
release_build
ci_check
ci_convert
benchmark_no_gc
```

内置 profile 默认语义：

- `editor_authoring`：允许 ExcelDataSource、Excel 写回、SerializedObject 编辑、metadata repair、safe layout refresh、hot reload；metadata_auto_flush 默认 `editor_safe`；report 默认 `editor_full`；allocation 默认 `report_watermark_growth`。
- `editor_play_mode_debug`：允许 ExcelDataSource 和 hot reload；runtime 修改默认不写回 Excel；metadata repair 默认关闭；如果绑定 editor authoring context，可按项目 profile 使用 `required_for_runtime_publish`；report 默认 `normal` 或 `editor_full`。
- `development_build`：默认只读；ConvertedBytesDataSource 必开，ExcelDataSource / hot reload / source switch 必须项目显式 opt-in；allocation 默认 `fail_on_watermark_growth`。
- `release_build`：只允许 ConvertedBytesDataSource 或可信 signed converted layer stack；禁止 Excel source、UnityEditor AssetDatabase、dynamic schema migration、Excel writeback 和 metadata repair；allocation 不低于 `fail_on_watermark_growth`。
- `ci_check`：不写 workbook，执行 schema/workbook/source-set/validation gate；human report 可裁剪，但 machine-readable report 必须完整到足够复现 gate。
- `ci_convert`：以 build/release 语义做 convert；不弹交互确认，不接受未登记 extension permission，不把 warning-as-error 等 gate-only 字段写入 bytes cache key。
- `benchmark_no_gc`：默认 `fail_on_any_hot_path_allocation`；测量窗口、capacity、source revision 和 profiler backend 必须进入 report。

profile normalize 默认顺序：

```text
built-in default profile
project policy descriptor global defaults
project policy named profile
host overlay
build target overlay
explicit command/API override
hard invariant validation
canonicalize + hash
```

合并规则默认要求：

- 每个 profile 字段必须在 schema 中声明 merge kind：`scalar`、`set`、`ordered_list`、`map`、`struct`、`replace_only`；未声明 merge kind 的字段不能被覆盖。
- `scalar` / `replace_only` 后一层替换前一层，但只能替换 allowlist 中允许的字段。
- `set` 字段按 canonical value 去重并 ASCII ordinal 排序，例如 allowed source kinds、allowed layer kinds、host requirements。
- `ordered_list` 只允许字段 schema 声明稳定 ordering key；没有 ordering key 的 ordered list 覆盖必须整体 replace，不能 append。
- `map` 按 key 合并；同一优先级重复设置不同值产生 `command.profile_invalid`。
- `struct` 递归应用字段 merge kind；不能把整个 struct 当自由 JSON 合并。
- named profile 可以声明 `base_profile_id`，但继承链必须无环，normalize 后必须展开成完整 `OperationProfile`；继承冲突产生 `command.profile_invalid`。
- 未识别字段默认是 `command.profile_invalid`；只有 extension descriptor 声明并被 project policy 授权的 policy namespace 可以被对应 extension 消费。

override 默认规则：

- 每个字段默认有 mutability：`fixed`、`project_configurable`、`profile_configurable`、`host_overlay_only`、`explicit_override_allowed`。
- CLI/API override 只能修改 `explicit_override_allowed` 字段；试图修改 `fixed`、schema semantic descriptor、field id、value shape、reference family、validator id/version、codec id/version 或 migration identity 时产生 `command.profile_override_forbidden`。
- 降低安全性的 override，例如允许 Excel source、允许 writeback、放宽 allocation policy、允许 data loss confirmation、启用 extension permission，必须同时被 project policy allowlist 允许；否则即使 CLI 显式传参也失败。
- output-affecting override 必须进入 `convert_profile_hash` / cache key；gate-only override 只进入 `gate_policy_hash`；两者都影响时必须同时进入两个 hash。
- API 设置例如 `RuntimeDatabase.SetAllocationPolicy` 只生成后续 operation 的 explicit override，不 retroactively 修改已完成 report 或已打开 source 的 profile hash。
- 相同 effective `OperationProfile` 必须得到相同 hash；不同输入来源只要 normalize 结果等价，cache/gate 视为等价。

hard invariant 默认规则：

- Release profile 不能被任何 override 改成允许 Excel source、Excel writeback、metadata repair、dynamic schema migration、UnityEditor AssetDatabase、unsigned patch 或 editor unsaved draft。
- CI profile 不能依赖交互确认；未提前提供 signed confirmation 或 project allowlist 的 data-loss step 必须失败。
- benchmark_no_gc 不能把 allocation policy 降到低于 `fail_on_any_hot_path_allocation`，除非命令显式切到非 benchmark profile。
- extension permission 不能越过 extension descriptor 的声明；profile 只能授予已声明权限，不能给 extension 增加它没声明的能力。
- host overlay 不能改变 materialized schema/data 语义；它只能表达 host capability、build target、Unity version capability、runtime source availability 和 report/detail 策略。

profile hash 默认规则：

- `operation_profile_hash = sha256("exceldb-operation-profile-v1" + canonical OperationProfile bytes)`，覆盖完整 effective profile 和 override provenance 的稳定部分。
- `gate_policy_hash` 覆盖 warning/error/blocker 升级、suppression、data loss confirmation gate、allocation gate、manifest requirement、permission grant gate、hard invariant 选择等只影响 pass/fail 的字段。
- `convert_profile_hash` 覆盖会影响 converted bytes 内容、section layout、debug symbol、compression、alignment、runtime provider selection、string table policy、strip policy 和 output-affecting warning policy 的字段。
- `build_profile` 是 profile 中的事实字段；会影响包内容、Unity dependency runtime hash、bytes selection 或 generated artifact 时进入 cache key。
- source layer activation 改变 materialized output 时进入 `convert_profile_hash` / cache key；只改变 trust/gate 要求而不改变 materialized output 时进入 `gate_policy_hash`。
- 本机路径、Unity Library path、当前用户名、当前时间、UI 语言、watcher event 顺序、human-readable 文案不得进入任何 profile hash。
- report/manifest/bytes header 记录的 profile hash 重新 canonicalize 后不一致时产生 `command.profile_hash_mismatch` 或更具体的 manifest/bytes mismatch diagnostic。

warning / severity / suppression policy 默认规则：

```text
warning_policy: allow | warnings_as_errors | selected_codes_as_errors
diagnostic_severity_overrides[]: code_or_prefix, min_severity, target_modes[]
suppression_policy[]: code_or_prefix, max_effective_severity, reason, scope, expires optional
```

- code catalog 的默认 severity 是事实；profile 只能计算 `effective_severity`，不能改变 code 的定义。
- 升级 warning/error 允许；降级 error/blocker 默认不允许。
- 只有 code catalog 标记为 suppressible、且不影响 import/convert/runtime/writeback 正确性的 diagnostic，才允许 suppression 降低 effective severity；原 diagnostic 仍必须写入 report。
- selected code prefix 必须匹配已登记内置 code 或 custom code registry；未知 prefix 是 `command.profile_invalid`。
- gate result 只看 effective severity、data loss gate、allocation gate、hard invariants 和 subreport result；不能解析 human-readable message。

source / layer policy 默认规则：

```text
allowed_source_kinds[]: excel_workbook | converted_bytes | composite | snapshot
allowed_source_layer_kinds[]: base | platform_variant | dlc | hotfix_patch | experiment | development_override | editor_unsaved_draft
source_layer_trust_policy: none | report_only | require_signature | require_allowlisted_certificate
source_layer_activation_policy: fixed_profile | declared_assignment | host_provided_assignment | forbidden
```

- Release profile 默认只允许 `converted_bytes` / `composite`，且 composite 只能由 signed converted layers 组成。
- `host_provided_assignment` 只能使用启动前写入的 deterministic assignment；运行中随机切换实验层必须走显式 `SwitchDataSource`，并发布 layer summary。
- trust policy 只控制 layer 能否采用；不能改变 patch operation 的 canonical value。
- Editor/Development 允许 Excel source 是调试能力，不是 Release fallback；对应 opt-in 必须在 startup report 中可见。

data loss / auto-fix policy 默认规则：

```text
data_loss_confirmation_policy: reject | require_explicit_confirmation | allow_with_signed_confirmation
auto_fix_policy: report_only | apply_safe_only | apply_safe_and_warnings | require_confirmation
```

- `data_loss_risk=true` 的 diff/migration/auto-fix 默认不能由 profile 静默允许；必须绑定 confirmation id/hash。
- signed confirmation 必须绑定 operation kind、plan hash、affected workbook guid、cell/range、canonical value hash、schema/layout hash 和 profile hash。
- confirmation 的 user/timestamp 可以写 report，但不参与 convert/source/cache hash。
- auto-fix 只能应用 report 中 `can_auto_fix=true` 且 patch 不覆盖用户数据的 proposal；warning 级 auto-fix 默认需要 confirmation。

metadata policy 默认规则：

```text
metadata_auto_flush: off | editor_safe | required_for_runtime_publish
metadata_flush_scope: explicit_only | mounted_workbook | affected_source_set
```

- `off`：Refresh 对缺 metadata 的 Excel 新行只生成 `temporary_imported` identity 和 dirty_metadata/status diagnostic；用户或工具必须显式调用 `FlushMetadata()` / `SaveAssets()`。
- `editor_safe`：Editor authoring Refresh 可在安全 preflight 后自动执行 metadata-only flush；只允许写 system-owned metadata/companion cells，不写正式数据。
- `required_for_runtime_publish`：runtime ExcelDataSource hot reload 如果候选新增 row 缺 stable identity，必须先通过 paired editor authoring context 完成 metadata flush；失败则保留旧 runtime source。
- `metadata_flush_scope=affected_source_set` 用于跨 workbook identity/reference metadata 需要同时稳定的场景；任一 workbook blocker 时不写任何 workbook。
- metadata_auto_flush 是 writeback 能力，Release hard invariant 默认禁止；Development runtime 需要项目显式 opt-in、写入 startup report，并且只能通过 `debug_authoring_bridge` 或 paired editor authoring context 执行实际 workbook 写入。

structure / save policy 默认规则：

```text
structure_write_policy:
  refresh_schema_owned_only
  allow_safe_layout_refresh
  enforce_schema_order
  report_only

save_policy:
  materialize_defaults: never | on_create | explicit_operation
  cleanup_deprecated: never | if_empty | explicit_only
  write_diagnostic_projection: never | explicit | on_save
  multi_workbook_atomic: best_effort | require_all_or_nothing
```

- 默认 `materialize_defaults=on_create`：`CreateAsset`、child insert、array append 等新建 editor draft row/element 可以把 schema default 写成 `explicit_value`；旧行缺值读取时仍是 `default_materialized`，不批量改写旧正式数据。
- `materialize_defaults=never`：新建 row/element 也默认保留 missing/default_materialized，除非调用方显式写入字段。
- `materialize_defaults=explicit_operation`：只有 `MaterializeDefaultValues` operation 可以把既有 missing/default_materialized 写成 explicit_value；普通 `SaveAssets` 即使看到 default 也不能顺手写 cell。
- 默认 `cleanup_deprecated=explicit_only`：deprecated/reserved 列不因普通 SaveAssets 删除。
- 默认 `write_diagnostic_projection=explicit`：诊断投影不让普通 SaveAssets 产生业务 dirty。
- `multi_workbook_atomic=require_all_or_nothing` 只有在 host 提供事务文件系统或项目实现 batch recovery 时可用；否则产生 `command.profile_invalid`。

manifest / runtime source policy 默认规则：

```text
manifest_policy:
  require_manifest_for_release: true
  allow_manifest_missing_in_development: true
  release_allow_excel_source: false
  development_allow_excel_source: opt_in
```

- Release profile 默认只允许 `ConvertedBytesDataSource` 或可信 signed `CompositeDataSource`，manifest 缺失是 gate failure。
- Development profile 可允许 Excel source/hot reload，但必须在 report 中标记 opt-in。
- Profile 不能允许 Release runtime 访问 UnityEditor AssetDatabase、Excel source 或 dynamic migration。

extension permission policy 默认规则：

```text
extension_permission_profile:
  default_permission_mode: deny_by_default
  require_external_tool_allowlist: true
  ci_unapproved_permission: fail
  editor_optional_permission: fallback_or_readonly
```

- extension descriptor 声明的是权限需求；profile 声明的是本次 operation 是否授予。二者必须先归一化再执行 extension。
- 未声明的文件系统、网络、环境变量、Unity 项目写入、外部进程权限默认拒绝。
- required permission 被拒绝时产生 `extension.permission_denied`；optional permission 被拒绝时只能进入 fallback/read-only/debug-disabled，不能改输出语义。
- permission policy 只影响 gate 时进入 `gate_policy_hash`；如果它改变 convert 输出、runtime provider selection 或 generated artifact，必须同时进入 `convert_profile_hash`。

allocation policy 默认映射：

- `editor_authoring`：`report_watermark_growth`。
- `editor_play_mode_debug`：`report_watermark_growth`。
- `development_build`：默认 `fail_on_watermark_growth`，可由 project policy 显式降级为 report-only。
- `release_build`：至少 `fail_on_watermark_growth`。
- `benchmark_no_gc`：`fail_on_any_hot_path_allocation`。

gate 判定默认流程：

1. operation 先按 effective `OperationProfile` 执行，生成原始 machine-readable report。
2. 按 code catalog 得到 default severity 和 affects flags。
3. 应用 profile 的 severity upgrade / suppression，生成 effective severity；原 severity 不改写。
4. 合并 subreport、allocation summary、data loss confirmation、manifest/permission/hard invariant 结果。
5. 由 `gate_policy_hash` 对应的 gate policy 得到 result severity、result code 和 CLI exit code。
6. 同一 report 使用不同 gate policy 重判定时，生成新的 gate result report，引用原 `report_hash`；不得改写原 report。

CLI / API override 默认规则：

- CLI 显式参数例如 `--warnings-as-errors`、`--allocation-policy`、`--allow-data-loss-confirmation`、`--allow-excel-source` 必须编译进 canonical profile，并在 report 的 `operation_profile_hash` 中体现。
- `--profile` 解析失败、profile schema 版本不兼容、继承环、未知字段、未知 diagnostic prefix、非法 merge 或 hard invariant 失败都产生 `command.profile_invalid`。
- CLI/API 试图覆盖未授权字段产生 `command.profile_override_forbidden`；命令不得降级为 warning 后继续。
- 如果 CLI 参数和 profile 文件产生相同 canonical profile，hash 必须相同。

### 14.13 默认 Dirty / Undo / Save / Conflict 状态机

每个 workbook、asset object、SerializedObject 默认有独立状态：

```text
clean
dirty_editor
dirty_metadata
external_changed
conflict
saving
save_failed
import_error
```

状态转移默认规则：

- `SerializedObject.ApplyModifiedProperties()` 成功后，对应 asset 进入 `dirty_editor`。
- 新行缺 stable identity 并生成临时 identity 后，workbook 进入 `dirty_metadata`。
- watcher/import 发现 workbook source revision 变化后，进入 `external_changed`。
- editor dirty 和 external changed 修改同一 property 时，进入 `conflict`。
- editor dirty 和 external changed 修改不同 property 时，按三方 merge 自动合并，并保留双方修改。
- `SaveAssets()` 只能保存 `dirty_editor` / `dirty_metadata` 且无 unresolved conflict 的 workbook。
- `SaveAssets()` 必须检查 base source revision；如果 workbook 已外部变化且未 refresh/merge，拒绝保存。
- `Undo` 只回退 editor draft；不能撤销策划已经在 Excel 外部保存的内容。
- `SaveAssets()` 成功后刷新 base snapshot、source revision、row revision，并清除 dirty 状态。

冲突解决默认动作：

- `reload from Excel`：丢弃 editor draft，使用 Excel latest，清除 editor dirty。
- `keep editor value`：保留 editor draft，标记待写回，下一次 `SaveAssets()` 必须重新检查 source revision。
- `manual resolve`：生成 property diff，用户选择最终值；完成后写入 editor draft。

默认禁止：

- unresolved conflict 时静默保存，必须拒绝 `SaveAssets()`。
- external changed 时用旧 SerializedProperty snapshot 直接 apply，必须先 refresh/merge。
- metadata dirty 未 flush 时 convert bytes，必须先写入 stable metadata。

#### 14.13.1 默认 property-level 三方 merge / conflict resolver

三方 merge 默认只在 editor authoring context 中发生。runtime hot reload/source switch 只使用 candidate snapshot patch，不把 runtime 修改当作可写 draft。

默认三方输入：

```text
base snapshot: 上次成功 import/save 后的 property snapshot
editor draft: SerializedObject / SetDirty / CreateAsset / DeleteAsset 产生的本地修改
external snapshot: 最新 Excel import candidate
```

snapshot 默认单位：

```text
workbook guid
table id
row guid/local id
field id
property path
array element identity or index
cell/range mapping revision
normalized value
value hash
source revision
row revision
```

normalized value 规则：

- scalar 按 schema runtime representation 比较，不按 Excel 显示文本比较。
- enum 按 value id 比较，不按 display name / token 比较。
- object reference 按 target row guid/local id 比较，不按可见 key/path 比较。
- UnityResourceRef 按 guid 比较；main_asset_path 默认只作为 display，不参与引用身份冲突。
- simple struct `expanded_columns` 按子 field id 比较。
- simple struct `single_cell` 默认按父 cell serialized value 比较；除非 schema codec 声明可做 child-level lossless merge。
- string 默认按 exact value 比较；trim/case-insensitive 只能由 schema validator/normalizer 显式声明。

默认 diff key：

```text
workbook guid + table id + row guid/local id + field id + property path segment + element identity/index
```

禁止使用 row number、column index、header text 作为 merge identity。它们只能作为 diagnostic location。

diff key 默认规则：

- scalar field 的 diff key 到 field id 为止。
- expanded struct 的 diff key 到 child field id / property path segment。
- single-cell struct 默认 diff key 是父 field id；只有 `merge_granularity=child` 且 codec lossless roundtrip 时才细化到 child path。
- repeated scalar 没有 stable element identity 时，diff key 包含 array index；出现 insert/delete/reorder 后，同 field 的 index-based diff 默认全部重新判定。
- child table / owned element 使用 element guid/local id；order index 是 display/order，不是 identity。
- reference 使用 reference field id + target identity；dependency graph 变化另生成 dependency diff，不复用 display token。

默认 merge 矩阵：

| base -> editor | base -> external | editor vs external | 默认结果 |
| --- | --- | --- | --- |
| unchanged | unchanged | same | no-op |
| changed | unchanged | different | accept editor draft |
| unchanged | changed | different | accept external |
| changed | changed | same normalized value | accept value, clear conflict |
| changed | changed | different same diff key | conflict |
| changed property A | changed property B | independent | auto merge both |

独立性默认判断：

- 不同 row 且没有 ownership/reference cascade 关系：independent。
- 同一 row 不同 scalar field：independent。
- 同一 expanded struct 不同 child field：independent。
- 同一 single-cell struct 的不同 child property：默认不独立，因为写回同一 cell。
- 同一 repeated/list field 的元素值修改：有 stable element identity 时按 element identity 判断；没有 stable element identity 时按 array index 判断。
- 同一 repeated/list field 出现 insert/delete/reorder：默认与该 array 内其他未基于 stable element identity 的修改冲突。
- reference field 修改会影响 dependency graph；与同 row 其他 scalar 修改可 merge，但必须同时产生 dependency_changed。
- schema/layout mapping revision 改变导致 property 无法映射时，不自动 merge，进入 mapping conflict。

row-level 默认处理：

- editor 修改 row，external 删除同 row：conflict。
- editor 删除 row，external 修改同 row：conflict。
- editor 删除 row，external 也删除同 row：accept delete。
- editor 新建 row 且 external 无同 key/identity：accept editor create。
- external 新建 row 且 editor 无同 key/identity：accept external create。
- editor 新建 row 与 external 新建 row key 相同但 identity 不同：duplicate key conflict，不自动合并 identity。
- duplicate row guid/local id 按 identity 修复规则处理；不能由 merge resolver 静默改身份。

冲突记录默认字段：

```text
conflict_id
conflict_state
conflict_kind
diff_key
asset_identity
field_id
property_path
element_identity optional
base_value
editor_value
external_value
base_source_revision
editor_draft_revision
external_source_revision
base_row_revision
external_row_revision
schema_hash
layout_hash
mapping_revision
base_location
editor_location
external_location
reason_code
suggested_actions
resolution_action optional
resolution_value optional
resolution_operation_id optional
```

`conflict_id` 默认生成规则：

```text
conflict_id = sha256(
  "exceldb-conflict-v1" +
  workbook_guid +
  table_id +
  row_guid_or_local_id +
  diff_key +
  base_source_revision +
  editor_draft_revision +
  external_source_revision +
  reason_code)
```

- conflict id 只用于本次 conflict lifecycle 的稳定定位；resolver 提交、base rebase 或 external revision 再变化后可以生成新的 conflict id。
- report 排序使用 workbook guid、table id、row identity、diff key、reason_code、conflict_id，不能按发现顺序。

`conflict_state` 默认集合：

```text
unresolved
resolved_pending_save
resolved_saved
stale
dismissed_noop
```

`conflict_kind` 默认集合：

```text
property_value
row_deleted_vs_modified
delete_vs_delete
create_duplicate_key
identity_collision
mapping_changed
schema_incompatible
reference_policy
array_structure
single_cell_codec
```

`reason_code` 默认集合：

```text
same_property_changed_differently
editor_modified_external_deleted
editor_deleted_external_modified
editor_create_external_create_same_key
duplicate_row_identity
property_mapping_changed
schema_or_layout_rebased
single_cell_child_conflict
array_index_unstable
reference_restrict_or_cascade
manual_resolution_stale
```

resolver 默认动作：

- `reload from Excel`：使用 external value，丢弃该 diff key 上的 editor draft；base snapshot rebase 到 external snapshot；conflict 进入 `dismissed_noop` 或 `resolved_pending_save`，取决于是否还有 metadata/diagnostic writeback。
- `keep editor value`：以 external snapshot 作为新的 base，把 editor value 重新写成待保存 diff；conflict 进入 `resolved_pending_save`；下一次 `SaveAssets()` 必须再次检查 source revision。
- `manual resolve`：用户提供 final value；系统先按 schema parse/normalize/validate，再以 external snapshot 为 base 写入 editor draft，并记录 resolution source。
- `keep both` 只在 schema 明确支持 duplicated/owned child asset 或 list duplicate 时出现；默认不提供。

resolver stale 默认规则：

- resolver UI 打开后，如果 workbook source revision、row revision、mapping revision、schema hash 或 layout hash 改变，提交旧 resolution 产生 `manual_resolution_stale`，不能写入 draft。
- stale conflict 必须重新从当前 base/editor/external snapshot 计算；不能把旧 `external_location` 的 row number/cell address 当作事实源。
- 如果旧 conflict 的 editor/external normalized value 在新 snapshot 中已经相同，进入 `dismissed_noop`，并清理该 conflict。
- 如果 resolver 选择会触发 reference restrict/cascade、duplicate key、required missing 或 validation blocker，resolution 只能形成 draft proposal，不能跳过 SaveAssets preflight。

conflict resolver API 默认语义：

- `AssetDatabase.GetConflicts(Span<ConflictRecord>, out count)` 查询当前 editor authoring context 的 conflict index；buffer 不足时返回 `RuntimeQueryStatus.Truncated`，`count` 是所需总数，不为结果创建临时数组。
- `ConflictRecord` 是轻量索引记录，只包含 conflict id、state、asset identity、field id 和 property path；base/editor/external value、cell location、preview 文本从 `OperationReport` 或显式低频 projection 查询。
- `TryGetConflict` 只查询当前 context 的 active conflict lifecycle；找不到、已清理、跨 context 或 context closed 时返回 false，并记录 `transaction.conflict_not_found` / `assetdb.context_mismatch` / `assetdb.context_closed`。
- `ResolveConflict(id, ReloadFromExcel)` 丢弃该 diff key 上的 editor draft，以 external snapshot rebase；如果没有剩余待保存 diff，conflict 进入 `dismissed_noop`。
- `ResolveConflict(id, KeepEditorValue)` 以 external snapshot 为新 base，把 editor value 重新写入 editor draft，conflict 进入 `resolved_pending_save`。
- `ResolveConflict(id, KeepBoth)` 只有 conflict record 的 `suggested_actions` 和 schema policy 都允许时可用；否则返回 false，并保持 unresolved。
- `ResolveConflict(id, ManualResolve)` 不直接提交，因为缺少 final value；必须通过 `TryOpenConflictResolver` 创建 manual resolver draft。
- `TryOpenConflictResolver(id, seed, out resolver)` 创建 conflict-local resolver draft；`seed` 决定 resolver `SerializedObject` 初始值来自 external/editor/base，默认 UI 应使用 `ExternalValue`。
- `ConflictResolver.serializedObject` 只能表示该 conflict 的可解析 property 子树；setter 仍走 `SerializedProperty` pending buffer、schema parse/normalize/validate，不允许直接写 workbook cell。
- `ConflictResolver.Apply()` 把 resolver draft 的最终 normalized value 写入 editor draft，记录 `resolution_action=manual resolve` 和 resolver operation id；它不保存 Excel，不发布 runtime ChangeSet。
- `ConflictResolver.Apply()` 前重新校验 conflict id、context id、source revision、row revision、mapping revision、schema hash、layout hash 和 target object state；任一变化产生 `transaction.conflict_resolution_stale`，不改变 editor draft。
- resolver dispose 未 apply 时丢弃 resolver pending buffer，不影响原 conflict。
- conflict resolver query / classify / state transition 属于 editor core operation；达到水位线后不得分配。打开 human-readable diff UI、构造 value preview、Excel 批注和本地化文本属于 report projection / editor UI，可以按 profile 分账。

提交规则：

- 自动 merge 后必须更新 editor draft 的 base snapshot，使后续 `SaveAssets()` 基于 merged snapshot preflight。
- resolved conflict 不等于已保存；只有 `SaveAssets()` 成功并重新 import 后，才能进入 `resolved_saved` 并清理 draft。
- unresolved / stale conflict 存在时，`SaveAssets()` 拒绝写回整个 affected workbook set，并产生 `transaction.conflict_unresolved`。
- conflict resolver 完成后，保存仍走 operation transaction；如果 Excel 又发生外部变化，必须重新 merge。
- conflict report 必须定位到 workbook/sheet/row/property/cell，并包含 diff key；UI 可以本地化显示，但机器逻辑只看 code/key。
- conflict report 中的 `base_value`、`editor_value`、`external_value` 默认使用 canonical debug representation；大文本/blob 可以写 value hash + preview，不能为了 report 改变 merge 判定。

#### 14.13.2 默认 editor draft / Undo group / SaveAssets 协议

editor draft 是唯一可保存的本地编辑缓冲。`SerializedObject.ApplyModifiedProperties()`、`EditorUtility.SetDirty()`、`CreateAsset()`、`DeleteAsset()`、Undo/Redo 都只修改 editor draft，不直接写 workbook。

editor draft 默认记录：

```text
target asset identity
base source revision
base row revision
base schema hash / layout hash
base mapping revision
property diffs[]
metadata diffs[]
create/delete markers
undo group id
draft revision
dirty reason
conflict ids[]
```

Undo group 默认规则：

- undo group id 是 context-local monotonic id；`IncrementCurrentGroup` 开启新 logical edit group，`SetCurrentGroupName` 只影响 UI/display name。
- 一次 `SerializedObject.ApplyModifiedProperties()` 默认形成一个 undoable edit unit；若当前 group 已被工具显式设置，则加入当前 group。
- `Undo.RecordObject(obj, name)` 必须在修改前捕获 schema-defined serialized snapshot；只记录 schema 字段、object state token、source/row/mapping revision 和 editor draft state，不记录 unmanaged runtime cache、proxy state 或 human report。
- `Undo.RecordObjects` / `RegisterCompleteObjectUndo(Object[])` 必须 all-or-nothing 进入同一 group；任一 target invalid/cross-context/stale/temporary_imported 时整个 record 失败。
- `Undo.RegisterCompleteObjectUndo` 保存完整 schema-defined object snapshot，适合 bulk rewrite、variant 切换、whole-array rewrite 和 data-loss-risk edit；它不绕过后续 validation/conflict/save preflight。
- `Undo.RegisterCreatedObjectUndo` 记录 create marker 和 `temporary_draft` identity；undo create 必须移除 draft row，并且未 flush 到 workbook 的 metadata 不得残留；redo 恢复同一 identity。
- `Undo.DestroyObjectImmediate` 默认等价于 editor draft delete marker；真正删除 Excel row / metadata tombstone 发生在 `SaveAssets()` commit。
- `CollapseUndoOperations` 只合并 Undo 菜单步骤，不合并 SaveAssets transaction，也不改变 draft diff 顺序。
- `PerformUndo` / `PerformRedo` 只改变 editor draft、dirty/status/projection index 和 redo/undo stack，不写 Excel、不触发 runtime source switch、不撤销外部 Excel 保存。
- `RevertAllInCurrentGroup` / `RevertAllDownToGroup` 只做 draft revert 且不创建 redo entry；用于取消 UI gesture，不得清无关 dirty。
- `ClearUndo` / `ClearAll` 只清 undo/redo stack，不清 dirty draft、conflict、status 或 workbook。
- undo/redo 后如果 base source revision 已外部变化，相关 asset 进入 needs refresh/merge；下一次 save 必须三方 merge。
- undo/redo commit 期间 `Undo.isProcessing=true`；重入修改、保存、刷新或再次 undo/redo 产生 `undo.processing_reentrant` 或进入 owner queue safe point。

`SetDirty` diff 默认规则：

- `SetDirty` 兼容直接改 C# property 的工作流，但保存前必须从 base snapshot 与 current object state 计算 schema property diff。
- diff 只允许映射到 schema field id / property path / cell range；无法映射的 runtime cache 字段、computed 字段、editor-only 临时字段不得写回。
- direct mutation 修改 repeated/list 时，如果缺 stable element identity，只能按 array index diff；与外部 insert/delete/reorder 同 field 冲突。
- `SetDirty` 不自动绕过 validator；save preflight 仍执行 parse/normalize/validation/reference/conflict。

`SaveAssets` 默认流程：

1. 收集 dirty workbook set，包括 dirty_editor、dirty_metadata、create/delete markers、auto-fix proposals。
2. 扩展 affected workbook set：跨 workbook hard reference、cascade/set_null delete、owned child table、resolved conflict draft、metadata repair、auto-fix proposal 都必须纳入。
3. 对所有 affected workbook 做只读 preflight：source revision、file lock、schema compatibility、unresolved/stale conflict、identity flush、reference restrict、validation blocker、data_loss confirmation。
4. 如果 source revision 已变化，先 import external snapshot 并执行三方 merge；能自动 merge 的更新 editor draft/base snapshot，不能 merge 的进入 conflict 并停止保存。
5. 如果任一 affected workbook 有 blocker，默认不写任何 workbook；report 按 workbook 列出 blocked reason。
6. 为每个 workbook 构建 patch list：metadata patch、data cell patch、row create/delete/tombstone、structure/layout safe refresh、diagnostic projection。
7. 执行 backup/temp/verify/replace；多 workbook 保存不承诺跨文件原子，但必须按 workbook 输出 saved/skipped/failed/restored 状态。
8. 保存成功后重新 import affected workbook/source set，刷新 base snapshot、row revision、source hash、identity map、reference graph。
9. 把已写入且复读验证通过的 `resolved_pending_save` 标记为 `resolved_saved`，清除已保存 draft；保留未保存或失败 workbook 的 dirty/conflict 状态。

SaveAssets preflight 默认 blocker：

- unresolved 或 stale conflict。
- conflict resolution 的 base/source/mapping revision 过期。
- editor draft diff 无法映射到 current schema field/cell range。
- draft 中的 create/delete 与 external snapshot 产生 duplicate key、reference restrict、identity collision 或 required validation blocker。
- 任何 affected workbook source revision 变化但当前 operation mode 不允许 auto merge。
- 多 workbook affected set 中任一 workbook 文件锁定、backup/temp/recovery preflight 失败或 data_loss confirmation 缺失。

save partial failure 默认规则：

- 如果某 workbook 写入失败，旧 workbook 和 dirty draft 必须保留；不能把内存状态当作已保存。
- 已成功保存的 workbook 可以进入 clean；失败 workbook 保持 save_failed + dirty。
- 如果跨 workbook hard reference、cascade delete 或 resolved conflict 需要一起更新，默认把它们视为同一 affected set；任一 blocker 时不写任何 workbook。
- 如果平台无法保证多 workbook 原子，report 必须提示 `multi_workbook_atomic=false`，并列出每个 workbook backup 路径。
- 如果部分 workbook 已替换后后续 verify 失败，按 recovery manifest restore 或进入 manual recovery；成功 workbook 的 draft 只有在 source set 复读一致后才能清理。

create / delete 默认规则：

- `CreateAsset` 创建 editor draft row 和 `temporary_draft` identity，立即进入 dirty_editor/dirty_metadata；创建时已经拥有最终 row guid/local id 和 asset guid/local file id，SaveAssets 成功只把该 identity_state 落盘为 stable，不改变 guid/local id。
- create 的 key 与外部新增 row 冲突时进入 duplicate key conflict，不自动合并。
- `DeleteAsset` 默认先检查 reference restrict；有 referrer 时返回 false + report，不创建 delete marker。
- delete marker 保存 row identity，不保存 row number；SaveAssets 时按 identity 删除 data row / child rows / metadata tombstone。
- undo delete 恢复 editor draft state；如果外部 Excel 已删除或修改同 row，恢复后仍需 conflict resolver。

#### 14.13.3 默认 workbook version-control / review / merge artifact 协议

xlsx 是二进制容器，不能指望 Git/Perforce 自己理解 row identity、field id、reference identity 或 schema-owned structure。ExcelDB 默认把版本协作当作 authoring 协议的一部分处理，而不是后期脚本。

权威性默认规则：

- `.xlsx` workbook + `__ExcelDB_Metadata` + hidden companion identity cell 是唯一 authoring source of truth。
- text snapshot、diff、review markdown、merge plan 都是 derived artifact；它们可以提交到版本库帮助 review/merge，但不能在 workbook 存在且可读时替代 workbook 作为 source。
- derived artifact 必须记录 `workbook_guid`、`schema_hash`、`layout_hash`、`metadata_checksum`、`source_hash`、`source_revision`、`snapshot_format_version` 和 `snapshot_hash`。
- tool 读取 derived artifact 前必须重新读取 workbook 或 base snapshot 并验证 hash；不一致产生 `vcs.snapshot_stale`。
- 删除 workbook 但留下 snapshot 不能让 asset 继续存在；snapshot 只能用于 report、review 和三方 merge base。

默认 artifact 路径：

```text
<workbook>.exceldb.snapshot.json
<workbook>.exceldb.diff.json
<workbook>.exceldb.review.md optional
```

项目可以通过 policy 改到集中目录，例如 `Assets/ConfigSnapshots/...`，但 artifact 内必须保存 normalized workbook path、workbook guid 和 path policy id；逻辑判断不能只靠文件名。

WorkbookSnapshot 默认格式：

```text
snapshot_format_version
tool_version
operation_profile_hash
operation_plan_hash optional
snapshot_scope: authoring_full | export_view
export_view_id optional
export_view_hash optional
schema_hash
layout_hash
descriptor_hash
workbook_guid
normalized_workbook_path
metadata_checksum
source_hash
source_revision
tables[]
rows[]
values[]
references[]
dependencies_digest optional
helper_freeform_regions[]
debug_locations optional
snapshot_hash
```

`rows[]` 默认字段：

```text
table_id
row_guid_or_local_id
asset_guid
canonical_key
asset_path
row_revision
row_state: active | deleted_tombstone | invalid
display_order
key_hash
value_digest
reference_digest
```

`values[]` 默认字段：

```text
table_id
row_guid_or_local_id
field_id
property_path
element_identity_or_index optional
value_shape
canonical_state: missing | explicit_null | explicit_value | default_materialized
canonical_value_hash
canonical_value_debug optional
canonical_value_bytes_base64 optional
source_cell_hash
raw_cell_fingerprint optional
cell_location optional
export_policy
```

snapshot canonical 规则：

- 使用 canonical JSON，UTF-8 without BOM，LF 换行，object 字段按字典序输出。
- array record 按 workbook guid、table id、row identity、field id、property path、element identity/index 排序。
- 不写 Excel 样式、列宽、筛选状态、当前时间、用户名、本机绝对路径、临时文件路径。
- `authoring_full` snapshot 默认包含 workbook 中所有 schema-owned authoring data，包括 editor-only、validation-only 和 runtime-exported field；`export_view` snapshot 只覆盖当前 export view，用于 bytes equivalence/review，不作为普通 workbook merge base。
- value record 必须区分 missing、explicit null、explicit value、default materialized；merge 判定不能把 missing default 和显式填写 default 当同一个 authoring 改动。
- `canonical_value_hash` 使用 14.6.5 的 canonical value hash；merge 判定使用 canonical state + canonical value hash，不使用 debug text。
- `canonical_value_debug` 是人类可读投影；大文本/blob 可以只写 hash + preview；如果 preview 被截断，必须显式标记，merge 判定只使用 canonical state/hash/bytes。
- `canonical_value_bytes_base64` 可按 profile 裁剪；缺失时工具仍可通过 xlsx/base snapshot 重建，不能从 debug text 反推 canonical value。
- `source_cell_hash` 用于 stale 检查和 cell-level review；只改样式/批注/列宽不改变 value record 的 canonical state/hash。
- helper/freeform region 只写 registered range 的 content hash、owner、cell range 和 optional preview；不能把未登记区域当作 schema 数据。
- `debug_locations`、preview text 和 source map 进入 snapshot hash 由 `snapshot_detail_policy` 决定；默认 CI/merge snapshot 不让 preview/debug location 影响 `snapshot_hash`。
- `snapshot_hash` 覆盖除自身字段和 policy 排除字段外整个 canonical snapshot；同一 workbook/schema/source 在不同机器输出相同 hash。

WorkbookDiff 默认字段：

```text
base_snapshot_hash optional
before_source_hash
after_source_hash
workbook_guid
schema_hash
layout_hash
row_changes[]
property_changes[]
reference_changes[]
dependency_changes[]
structure_changes[]
helper_freeform_changes[]
diagnostics[]
diff_hash
```

diff/review 默认规则：

- review artifact 面向人；merge/CI 只读取 WorkbookSnapshot、WorkbookDiff 和 OperationReport。
- row add/delete/rename/move、key/path change、property change、reference target change、Unity guid change、本地化 key change、dependency changed 必须分别输出稳定 change kind。
- Excel row order、sheet order、列宽、样式只在 schema/profile 声明进入 layout/review 时输出；默认不影响 source diff。
- key/path rename 使用同一 row identity 表达 moved/renamed；不能拆成 delete + add。
- reference diff 按 target identity / Unity guid / provider key 比较；显示 token/path 只作为 debug。

Workbook merge conflict artifact 默认格式：

```text
conflict_artifact_format_version
operation_id
operation_plan_hash optional
base_snapshot_hash
ours_snapshot_hash
theirs_snapshot_hash
workbook_guid
schema_hash
layout_hash
conflicts[]
conflict_artifact_hash
```

`conflicts[]` 默认字段：

```text
conflict_id
conflict_kind
diff_key
asset_identity optional
table_id optional
row_guid_or_local_id optional
field_id optional
property_path optional
element_identity_or_index optional
base_value_state optional
ours_value_state optional
theirs_value_state optional
base_value_hash optional
ours_value_hash optional
theirs_value_hash optional
base_location optional
ours_location optional
theirs_location optional
reason_code
suggested_actions[]
requires_manual_resolution
```

conflict artifact 默认规则：

- `conflict_artifact_hash` 覆盖除自身字段外的 canonical JSON；同一冲突集合在不同机器输出相同 hash。
- conflict 排序按 table id、row identity、field id、property path、element identity/index、reason_code、conflict_id。
- value 比较使用 canonical state + canonical value hash；debug preview 不参与 conflict 判定。
- `--conflicts-out` 写的是该 artifact，不是 human markdown；human review 可以从 artifact/report 投影生成。
- `merge-workbook` 有 unresolved conflict 时不写 merged workbook；只输出 report、conflict artifact 和可选 human review。

三方 workbook merge 默认输入：

```text
base workbook or base WorkbookSnapshot
ours workbook
theirs workbook
current SchemaDescriptor / generated registry
OperationProfile
optional lock/provider state
```

三方 workbook merge 默认流程：

1. 对 base/ours/theirs 分别执行 read-only import，生成 canonical WorkbookSnapshot；base 只能是 xlsx 或可信 snapshot。
2. 验证三者 workbook guid、schema compatibility、metadata format、row identity map 和 source revision lineage。
3. 计算 base->ours、base->theirs 的 WorkbookDiff。
4. 复用 14.13.1 的 diff key 和 merge matrix 做 property-level merge。
5. 对 row create/delete/tombstone、key/path rename、reference policy、helper/freeform、schema-owned structure 分别分类。
6. 无 unresolved conflict 时生成 merged workbook patch；有 conflict 时只输出 merge report/conflict artifact，不写 merged workbook。
7. 写入时走 backup/temp/verify/reimport；成功后输出 merged snapshot、diff 和 report。

merge base 默认规则：

- 有共同 base snapshot/xlsx 时才能自动三方合并。
- base snapshot 的 schema/layout 可旧于 current schema，但必须能通过 compatibility analyze 建立 field/table/row identity 映射。
- 缺 base、base hash 不可信、或 base 无法映射到当前 schema 时产生 `vcs.merge_base_missing` 或 schema compatibility blocker。
- 二方 merge 只能用于显式 `choose-ours` / `choose-theirs` / manual resolve，不默认自动猜。

row / identity merge 默认规则：

- 同一 row identity 在 ours/theirs 都存在：按 property-level merge。
- 只有一边新增 row 且 key 不冲突：接受新增。
- 两边新增不同 row identity 但 canonical key/path 相同：`vcs.merge_conflict` + duplicate key conflict；不自动合并身份。
- 两边新增同 row identity 但 base 不存在，默认视为复制/手工 guid 冲突，产生 `metadata.duplicate_row_guid` 或 `vcs.merge_conflict`，除非 clone/repair report 能证明来源。
- 一边删除 row，另一边未改：接受删除并保留 tombstone policy。
- 一边删除 row，另一边修改 row：conflict。
- 两边都删除同 row：接受删除。
- row order/display_order 两边都变且 schema 声明 order 是 runtime semantic：按 same property conflict；如果 order 只是 display，默认采用 ours order 并在 review 中记录 theirs order changed。

structure / layout merge 默认规则：

- schema-owned header、row 7 注释、data validation、helper generated block 不从分支手工合并；以 current schema/layout refresh 结果为准。
- table/field mapping 通过 table id / field id / metadata，不通过 sheet name、header text 或 column index。
- 两边只改 sheet/field display name 且 field id 不变：layout refresh 解决，不是 data conflict。
- 两边对同一 helper/freeform registered region 都有内容变化：如果 region 声明 `merge_policy=text_line` 可按 canonical line merge；否则 `vcs.merge_conflict`。
- 未登记的自由区域不参与自动 merge；如果会被 schema patch 覆盖，仍按 `structure.helper_region_overlap` blocker。
- Excel comment/note 中非 ExcelDB-owned 内容默认保留 ours；theirs 也修改同一 comment/note 时进入 helper/freeform conflict。

reference / dependency merge 默认规则：

- 引用冲突按 target identity 判断，不按显示 key/path。
- UnityResourceRef 冲突按 guid 判断；`main_asset_path` 两边不同但 guid 相同，默认只刷新 path display，不产生 data conflict。
- LocalizedTextRef 冲突按 text entry identity / text key policy 判断；preview_text 不作为 identity conflict。
- reference merge 后必须重建 dependency graph、reverse index 和 preload plan digest；digest 改变时 review artifact 要列出 affected referrer。
- hard reference 在 merged snapshot 中 missing 时，merge 可以产出 report，但写 merged workbook/convert 必须被 reference policy blocker 阻止。

lock / edit lease 默认规则：

- 核心库不依赖 Git LFS、Perforce 或某个 VCS；lock provider 是 adapter/extension。
- Project policy 可以声明 workbook/table/schema group 的 `edit_lock_policy: none | warn | require`。
- `require` 下，Editor authoring 的 `SaveAssets`、merge apply、migration apply 和 structure generation 必须验证当前 user/process 持有 lock；否则产生 `vcs.lock_required` 或 `vcs.lock_owner_mismatch`。
- 直接在 Excel 外部编辑无法被核心库阻止；下次 import/report 必须展示 lock mismatch，并可由 pre-submit/CI 拦截。
- lock state 不进入 source_hash；它只影响 operation permission/gate。

CI / pre-submit 默认规则：

- 如果项目启用 snapshot artifact，CI 必须检查每个 mounted workbook 的 snapshot 存在且不 stale；失败产生 `vcs.snapshot_missing` / `vcs.snapshot_stale`。
- CI review gate 可以要求 `WorkbookDiff` 与当前 workbook pair 匹配；不能只检查 diff 文件存在。
- workbook-check 仍以 xlsx 为准；snapshot 只用于 stale 检查、review summary 和 merge base。
- binary xlsx conflict 未解决时，merge command 必须先生成 merged xlsx，再允许 convert/build。
- merge report 的 conflict id、diff key、row identity、field id、cell location 必须 deterministic，不能按 Git conflict marker 顺序。

### 14.14 默认 watcher / refresh / import 调度

Excel 文件 watcher 默认只负责发出“可能变化”信号，不直接修改 runtime object graph。

默认调度：

1. 文件系统事件进入 debounce queue。
2. 等待文件稳定窗口，默认至少连续两次 stat 的 size/write-time 不变。
3. 尝试以只读方式打开 workbook。
4. 打不开时进入 retry，并产生 `watcher.file_locked` warning；不清空旧数据。
5. 读取 workbook fingerprint/source revision。
6. 如果 fingerprint 未变化，丢弃事件。
7. 如果变化，启动 import transaction。
8. import 成功后再进入 hot reload/source switch candidate 流程。

默认 debounce / coalesce：

- 同一 workbook 的连续 watcher 事件合并为一次 refresh。
- 同一保存动作触发多次 changed/renamed/temp-file 事件时，只产生一个 import report。
- 多个 workbook 同时变化时，按 workbook path deterministic 排序处理。
- Play Mode hot reload 可以 batch 多个 workbook 的 change event，再统一 publish。

watcher queue / overflow 默认规则：

- watcher queue 是 context-local operation queue 的输入 buffer，不是 source state；queue 中保存的是 refresh request summary、coalescing key、event sequence range 和 fingerprint hint，不保存可发布 candidate。
- queue key 默认是 normalized workbook path + source kind；同 key 的自动 watcher event 只保留 oldest sequence、newest sequence、latest fingerprint hint、event count 和 event kind bitset。
- 自动 watcher event 可以 coalesce，但不能把 manual `Refresh()`、explicit `ImportAsset()`、`SwitchDataSource()`、`Open()`、`Close()`、`SaveAssets()` 请求合并掉或丢弃。
- queue capacity 属于 RuntimeCapacity/editor capacity 的 `watcher_events` / `operation_requests` 水位线；容量不足时，如果 policy 允许水位线增长，登记 `performance.watermark_grew` 并继续；如果 policy 禁止增长，自动 watcher event 可降级为 full mounted-workbook rescan request，并产生 `watcher.queue_overflow`。
- `watcher.queue_overflow` 发生后不得发布部分 candidate；旧 source/object graph 继续服务。下一次 owner safe point 必须按 deterministic workbook path 顺序对 mounted workbook 做 fingerprint scan，重建缺失的 event summary。
- 如果连 full rescan request 都无法入队，context 进入 `refresh_required` status；后续任何 `FindAssets` / status query 可以显示 stale warning，但 lookup 仍使用旧 index。用户显式 `Refresh()` 必须优先处理该 status。
- overflow report 必须包含 dropped/coalesced event count、oldest/newest sequence、affected normalized paths、capacity before/after、是否执行 full rescan fallback 和是否仍需 manual refresh。
- watcher queue append、coalesce、overflow summary 和 report append 属于 editor core operation；达到水位线后不得为每个文件事件分配对象或字符串。

默认手动 `AssetDatabase.Refresh()`：

- 立即 flush 当前 watcher queue。
- 对所有 mounted workbook 检查 fingerprint。
- 不绕过 transaction/report。
- 如果存在 editor dirty，refresh 可以 import candidate，但不能覆盖 dirty draft；必须走 merge/conflict。
- 如果当前 context 处于 `StartAssetEditing` batch，manual refresh request 进入 batch deferred queue，outermost `StopAssetEditing()` 后执行；如果只是 `DisallowAutoRefresh`，manual refresh 仍立即执行。
- manual refresh 不受自动 watcher refresh suppression 影响，但仍受 owner operation queue、file lock retry、read-verify 和 conflict/preflight 约束。

#### 14.14.1 默认 file watcher / fingerprint / retry 协议

文件系统事件不等于 workbook 已经可读。Excel 保存时可能产生临时文件、rename、多次 write、文件锁、半写入 zip。默认 watcher 只把这些事件归并成 refresh request，真正是否导入由 fingerprint 和 read-verify 决定。

watcher event 默认字段：

```text
event_id
sequence
raw_path
normalized_path
event_kind
observed_time
source_hint
```

默认忽略的路径：

- 不在 mounted workbook roots / source set 内的路径。
- Excel lock/temp 文件，例如以 `~$` 开头的文件。
- ExcelDB backup/temp/recovery/write-lease 文件，例如 `.__exceldb_tmp_`、`.__exceldb_backup_`、`.__exceldb_recovery_`、`.__exceldb_write_lease.json`。
- diagnostic/report/manifest 输出，除非它本身被 mount 为 source。
- 非 `.xlsx` / `.xlsm` / schema 声明支持的 source extension。

路径归一化默认规则：

- 使用项目相对路径，统一 `/`。
- Windows 下路径比较默认 ordinal ignore-case；manifest/report 中仍保存 normalized display path。
- symlink/junction 默认解析到 canonical physical path 做重复检测；display path 保留 mount path。
- 同一 physical workbook 通过两个 mount path 出现时，按 duplicate mount/workbook guid 规则处理。

debounce 默认规则：

- 同一 normalized workbook path 的 watcher event 默认 coalesce。
- 默认 debounce window 是 configurable；未配置时 editor 使用 250ms，Development runtime 使用 500ms。
- debounce 结束后必须执行 stability probe：至少连续两次 stat 的 size、last write time、file id/inode 可用时一致。
- 如果 stat 不稳定，继续延后；超过 max settle time 仍不稳定，产生 `watcher.file_unstable` warning，保留旧数据。
- manual `AssetDatabase.Refresh()` 会立即 flush queue，但仍执行 read-verify；不会因为手动触发就读半写文件。

read-verify 默认流程：

1. 尝试以 read-sharing 模式打开 workbook。
2. 如果打开失败且错误像 lock/permission/sharing violation，进入 retry，不清空旧 snapshot。
3. 如果打开成功，先验证 xlsx zip central directory / package 结构，再读取 metadata header/checksum。
4. 计算 workbook fingerprint。
5. 读取结束后再次 stat；如果 size/write-time 改变，本次读取视为 stale，重新 debounce/retry。
6. 只有 read-verify 通过且 fingerprint 变化，才进入 import transaction。

fingerprint 默认字段：

```text
normalized_path
file_size
last_write_time_utc_ticks
file_id_or_inode optional
quick_content_hash optional
metadata_revision
source_revision
source_hash
metadata_checksum
workbook_guid
schema_hash
layout_hash
```

fingerprint 默认规则：

- `last_write_time` 只用于快速判断，不作为 source identity 的唯一依据。
- 如果 size/write-time 变化但 metadata/source revision、metadata_checksum、exported source_hash 都不变，refresh 是 no-op，并报告 `watcher.fingerprint_noop` info。
- 如果 workbook 内容变化但 source_hash 不变，例如只改样式/列宽/普通批注，默认不触发 runtime property_changed；可以触发布局/diagnostic report。
- 如果 metadata_checksum 变化但 metadata_revision 未递增，产生 `metadata.checksum_mismatch` 或 repair diagnostic，不静默相信文件时间。
- 读取到 workbook_guid 变化时，不按同路径覆盖旧 source；按 clone/move/duplicate workbook 规则处理。

retry 默认规则：

- file locked 默认 diagnostic code 为 `watcher.file_locked`，severity warning。
- retry 使用 bounded backoff；默认 editor 尝试 5 次，间隔 100ms、200ms、400ms、800ms、1600ms；Development runtime 默认最多 3 次。
- retry 期间旧 snapshot / resident object graph 继续服务。
- 超过 retry 上限后，本次 refresh 失败但 watcher 可以保留 dirty pending flag；下一次事件或手动 Refresh 再尝试。
- `SaveAssets` 遇到 lock 使用 save preflight 的 `save.file_locked`，不要和 watcher read lock 混用。

event source priority 默认规则：

- explicit `SwitchDataSource/Open/Close` 高于 watcher refresh。
- manual `Refresh()` 高于自动 watcher refresh，但不能跳过 transaction。
- asset editing batch drain 高于自动 watcher refresh；batch 内记录的 manual refresh request 高于同期间自动 watcher event。
- `DisallowAutoRefresh` 只压制自动 watcher refresh，不压制 explicit operation；`AllowAutoRefresh` outermost 后的 queued watcher refresh 按原 event sequence coalesce。
- 同一 workbook 的多个自动 refresh request 只保留最新 sequence。
- schema/layout change request 与 workbook file change 同时存在时，先完成 schema compatibility analyze，再决定 import/layout refresh/migration。

report 默认字段：

```text
watcher_events[]
coalesced_workbooks[]
ignored_paths[]
retry_summary[]
fingerprint_before
fingerprint_after
read_verify_result
settle_time_ms
```

默认 diagnostic code：

- `watcher.file_locked`：文件当前被 Excel 或其他进程占用，等待 retry。
- `watcher.file_unstable`：稳定窗口内文件 size/write-time 一直变化。
- `watcher.read_verify_failed`：zip/package/metadata 读取校验失败，可能是半写入或损坏。
- `watcher.fingerprint_noop`：文件事件存在，但语义 fingerprint/source hash 未变化。
- `watcher.ignored_temp_file`：忽略 Excel/ExcelDB 临时文件事件。
- `watcher.queue_overflow`：自动 watcher event 超出 queue/watermark；系统已 coalesce、fallback 到 full rescan 或标记需要 manual refresh。

#### 14.14.1.1 默认 Excel direct edit classifier / import action 协议

直接修改 Excel 是主工作流，因此 import transaction 不能只得到“workbook 变了”这个粗粒度结论。默认必须先把当前 workbook snapshot 与上次 successful import snapshot 做确定性分类，生成 import actions，再交给 dirty/merge/conflict、metadata flush、runtime patch plan 和 report。classifier 只读 workbook，不写 workbook、不 patch resident object。

import action 默认字段：

```text
action_id
action_kind
table_id
row_identity optional
previous_row_identity optional
field_id optional
property_path optional
source_cell_range optional
previous_canonical_value_hash optional
current_canonical_value_hash optional
previous_source_cell_hash optional
current_source_cell_hash optional
row_match_confidence: identity | source_hash | key_snapshot | ambiguous | none
requires_metadata_flush
requires_merge
affects_runtime_source
affects_layout_only
diagnostic_refs[]
copy_repair_plan_ref optional
identity_remap_scope optional
```

action_kind 默认集合：

```text
no_semantic_change
layout_only_change
row_added_temporary
row_added_stable
row_deleted
row_moved
row_order_changed
key_changed
property_changed
reference_changed
unity_resource_display_changed
metadata_only_changed
identity_repair_candidate
identity_blocker
duplicate_key_candidate
invalid_raw_row
```

classifier 默认流程：

1. 按 table id / field id / system companion columns 读取 current layout；layout 不能稳定映射时停止 data import，产生 structure/layout diagnostic。
2. 按 companion row guid/local id 优先匹配 previous row；metadata row number 只作为定位和 report。
3. 对没有 stable identity 的正式数据行，创建 `row_added_temporary` action，并分配 session-local `temporary_imported` identity；不进入常规 asset index。
4. 对有 stable identity 但 previous snapshot 不存在的行，先检查 workbook guid、table id、row guid/local id、allocator/tombstone、duplicate identity 和 key scope；全部通过才是 `row_added_stable`，否则是 repair candidate 或 blocker。
5. 对 previous snapshot 中存在、current snapshot 中缺失的 stable row，分类为 `row_deleted`；如果只是被 filter/hidden/排序移动，不算删除。
6. 对同 identity 的 current row，逐字段比较 canonical state/value/source_cell_hash；canonical value 改变才产生 `property_changed` / `reference_changed`，只有 display/style/comment/width 改变则是 `layout_only_change` 或 `no_semantic_change`。
7. key 字段 canonical value 改变时，同时产生 `key_changed` 和对应 property diff；identity 不变，后续 AssetDatabase path/moved/renamed event 由 patch plan 生成。
8. row number 变化但 identity 和 canonical value 不变，默认 `row_moved` 仅用于 report / current row number refresh；只有 schema 声明 order policy 或 child table order index 有语义时，才产生 `row_order_changed` 并影响 source_hash/runtime diff。
9. UnityResourceRef 只改 `main_asset_path` 且 guid/local file id/runtime provider key 不变时，分类为 `unity_resource_display_changed`；不产生 runtime data conflict，不改变 runtime_dependency_hash。
10. hidden companion identity、metadata row record 或 checksum 被手动改动时，先进入 `metadata_only_changed` / `identity_repair_candidate`；不能因为用户改了隐藏 guid/local id 就把已有 row 当作新 asset。无法证明映射时是 `identity_blocker`。
11. 同一 identity 出现多行时，按 14.8.1 duplicate row identity 规则判断是否是复制行候选；只能生成 copy-row repair proposal，不在 import/hot reload 中静默重写正式数据。
12. copy-row repair proposal 必须记录 source stable row、candidate copied row、owned child closure、需要新分配的 identity 数量、内部引用 remap 列表和外部引用保留列表；proposal 不进入 runtime added event。
13. 同一 key scope 出现多个 live row 时，分类为 `duplicate_key_candidate`；duplicate key loser 不进入常规 key lookup / converted runtime index。
14. parse/normalize 失败的行或字段保留为 `invalid_raw_row` action；Editor table/status/diagnostics 可显示，runtime hot reload/source switch 不能发布该 invalid value。

direct Excel edit 默认语义矩阵：

| Excel 现场变化 | 默认 action | Editor authoring 可见性 | Runtime hot reload |
| --- | --- | --- | --- |
| 改普通数据 cell，canonical value 变化 | `property_changed` | resident/dirty/conflict state 更新 | 可 patch 则 property_changed |
| 只改样式、列宽、普通批注 | `layout_only_change` | report/layout refresh | 不发布 runtime event |
| 改 key cell | `key_changed` + `property_changed` | identity 不变，path 可 moved/renamed | moved/renamed + property_changed |
| 新增数据行且缺 metadata | `row_added_temporary` | table/status preview，dirty_metadata | 不发布 added，需先 FlushMetadata |
| 新增数据行且 stable metadata 有效 | `row_added_stable` | 进入 asset set | 可发布 added |
| 复制正式数据 cell 但不复制 hidden identity | `row_added_temporary` | 按 Excel 新行处理，可 FlushMetadata | 不发布 added，需先 FlushMetadata |
| 删除 stable row | `row_deleted` | 进入 delete/import conflict 或 removed | 可发布 removed |
| 复制 row 连同 hidden identity | `identity_repair_candidate` 或 `identity_blocker` | copy-row repair proposal / blocker | 不发布，直到 identity 稳定 |
| 拖动普通数据行 | `row_moved` | 更新 current row number/report | 默认无 runtime event |
| 拖动有 order policy 的 child row | `row_order_changed` | order index diff | property_changed/dependency_changed |
| 改 UnityResourceRef path 但 guid 不变 | `unity_resource_display_changed` | auto-fix/report | 不改变 runtime dependency |
| 手改 hidden companion identity | `metadata_only_changed` / `identity_blocker` | repair/report | 不发布 unstable identity |

merge / conflict 接入默认规则：

- classifier action 不是最终结果；如果 editor draft 对同一 diff key 也有修改，必须进入 14.13 的三方 merge/conflict。
- `row_added_temporary` 与 editor draft create 的 key 冲突时是 duplicate key conflict；不能自动把 Excel 新行和 editor draft row 合并。
- `row_deleted` 与 editor draft property edit / reference edit / child edit 冲突时进入 row delete vs editor edit conflict。
- `layout_only_change` 不和 editor data diff 冲突；但如果它破坏 schema-owned layout mapping，则升级为 structure/layout blocker。
- classifier 输出排序必须 deterministic：table id、row identity、action kind、field id、cell range；report 不依赖 Excel 文件中的物理发现顺序。
- classifier 达到水位线后不得分配；action buffer 不足按 allocation policy 登记 watermark growth 或失败。

#### 14.14.2 默认 workbook mount set / cross-workbook 协议

一个 `AssetDatabase` / `RuntimeDatabase` context 默认管理的是 workbook/source set，不是假设只有单个 Excel 文件。单 workbook 只是 source set 的特例。

mount set 默认身份：

```text
context id
mounted workbook roots[]
workbook guid -> workbook source identity
workbook guid + table id + row guid/local id -> asset identity
```

SourceSetDescriptor 默认字段：

```text
source_set_descriptor_format_version
source_set_id
source_set_descriptor_hash
schema_hash
layout_hash
export_view_id
export_view_hash
operation_profile_hash
workbook_entries[]
source_layers[] optional
layer_stack_hash optional
materialized_source_hash optional
source_set_options
```

`workbook_entries[]` 默认字段：

```text
normalized_mount_path
canonical_physical_id optional
workbook_guid
workbook_alias
workbook_role: primary | dependency | generated | debug_only
required
schema_hash
layout_hash
source_hash
metadata_checksum
source_revision
mount_state
```

SourceSetDescriptor 默认规则：

- `SourceSetDescriptor` 是上下文 / authoring / report 的挂载描述，不是 asset identity。asset identity 仍只来自 workbook guid + table id + row guid/local id。
- `source_set_id` 默认由 sorted runtime workbook guid set + schema hash + required/debug role membership 生成；alias 和 mount path 不进入 `source_set_id`。
- descriptor canonical bytes 使用 workbook guid、normalized mount path、alias、role、required flag、schema/layout/export view/source hash、profile hash 的稳定字段；不写当前时间、绝对路径、用户名或 watcher event 顺序。
- `source_set_descriptor_hash` 改变不必然表示 runtime data 改变；它可能只是 alias、mount path 或 role 变化。
- `source_set_hash` 仍只覆盖当前 export view 下的 exported data、identity、reference 和 schema/runtime 解释所需字段；alias/path/display-only role 变化默认不改变 `source_set_hash`。
- 如果 profile 允许 debug-only workbook，`debug_only` workbook 不进入 Release convert/runtime source set；它必须在 report 中列出，不能静默混入 Release bytes。

#### 14.14.2.1 默认 layered source / patch / override 协议

游戏运行时常见 source 不是单一全量配置，而是 base 包、平台差异、DLC、热修补丁、开发覆盖、灰度实验等组合。ExcelDB 默认把这类组合定义为 `CompositeDataSource` / layered source，而不是让业务层自己叠加配置。

SourceLayerDescriptor 默认字段：

```text
layer_id
layer_kind: base | platform_variant | dlc | hotfix_patch | experiment | development_override | editor_unsaved_draft
source_kind: excel | converted_bytes | patch_bytes
source_identity
source_hash
source_set_hash optional
schema_hash
export_view_id
export_view_hash
layer_order
priority_group
target_base_source_hash optional
target_layer_stack_hash optional
patch_operation_granularity: property | row | table
allowed_patch_kinds[]
activation_condition optional
activation_assignment_id optional
activation_inputs_hash optional
trust_policy optional
signature optional
debug_name optional
```

内置 layer kind 默认语义：

- `base`：完整 source，layer stack 必须有且只有一个 base；base 提供完整 identity/key/reference/dependency 初始图。
- `platform_variant`：随 build target/profile 固定启用的覆盖层，例如 iOS/Android/console 平台差异；Release 可用，但必须进入 layer_stack_hash 和 cache key。
- `dlc`：可新增 asset 或替换允许覆盖的 asset；必须声明 package id/version 和依赖的 base/source hash。
- `hotfix_patch`：正式包运行后下发的补丁；Release 中必须是 converted patch bytes，不能是 Excel workbook。
- `experiment`：灰度/AB 实验层；必须有 deterministic activation id，不能在同一 session 内随机改变 layer stack。
- `development_override`：Editor/Development 调试覆盖；Release 默认禁止。
- `editor_unsaved_draft`：仅 Editor authoring 可见，用于 inspector dirty draft 预览；不进入 convert/runtime artifact。

layer ordering 默认规则：

- 先按 `layer_order` 升序，再按 `layer_id` ordinal 排序；同一 priority group 中两个 active layer 改同一 diff key 且无 merge policy 时产生 `source.overlay_conflict`。
- `base` 必须是最低 layer_order；多个 base 或没有 base 是 `source.layer_order_invalid`。
- activation condition 必须只依赖 OperationProfile、build target、platform id、declared experiment assignment、package manifest 和 deterministic user segment；不能读取当前时间、随机数、网络返回或本机环境。
- activation assignment 是 source switch 输入，不是 runtime hot path 中可变状态；assignment id、输入摘要和 evaluation result 必须记录到 manifest/startup report。改变 activation assignment 必须重新构建 source switch candidate，不能在同一 frame 内直接切换 active layer。
- layer order、activation result、source hash、schema/export view hash 共同生成 `layer_stack_hash`。
- `materialized_source_hash` 是应用所有 active layer 后的有效 source hash；它才是 runtime object graph 的 data identity。

patch operation 默认字段：

```text
operation_id
operation_kind: set_property | replace_row | restore_row | add_row | delete_row | move_key | set_reference | patch_child | delete_child
table_id
row_guid_or_local_id
field_id optional
property_path optional
element_identity_or_index optional
canonical_value optional
base_value_hash optional
patch_value_hash
diff_key
required
patch_source_location optional
data_loss_risk
```

patch / override 默认规则：

- patch operation canonical bytes 必须包含 operation_id、operation_kind、target identity、diff_key、base/patch hash、required、data_loss_risk 和 value payload；`patch_operation_digest` 按 operation_id / diff_key stable sort 后计算，不依赖 patch 文件中的物理顺序。
- 同一 layer 内两个 operation 命中同一 diff_key 必须在生成 patch manifest 时合并；不能合并则产生 `source.overlay_conflict`。运行时应用 patch 时不按“后出现的赢”。
- 默认 granularity 是 property-level canonical patch，复用 14.13.1 的 diff key；row replace/table replace 必须由 layer descriptor 显式允许。
- patch 必须声明 base value hash 或 target base source hash；base 不匹配时产生 `source.layer_base_mismatch`，不能按当前值强行覆盖。默认 `base_value_hash` 指应用低层 layer 后、本 operation 之前的 expected materialized value hash；如果 patch 要求直接绑定原始 base source，必须显式使用 `target_base_source_hash`。
- patch 不允许改变 workbook guid、table id、row guid/local id、field id 或 schema value shape；需要改变身份/结构时必须走 migration 或完整 base source。
- key/path 变化使用 `move_key`，保持 row identity；不能通过 delete + add 伪装 rename。
- delete 使用 tombstone operation；被删除 row 不进入 materialized key/reference/runtime index，旧 resident object 在 hot reload 时发布 `removed`。
- tombstone 会遮蔽低层同 identity row；更高层如果需要恢复同一 row，必须使用显式 `restore_row` / `replace_row` policy，且声明 base tombstone hash。默认不允许 delete 后同层 add 同 identity。
- add_row 必须携带 stable row identity、key fields、required exported fields、reference payload 和 row_source_hash；不能在 Release patch 应用时现场分配 identity。
- child table patch 默认按 child element identity；没有 stable element identity 的 repeated/list 只能按 row/field replace，或产生 overlay conflict。
- single-cell struct 默认按父 cell patch；只有 codec 声明 `merge_granularity=child` 时才能 patch 子字段。
- `required=false` 的 optional patch 只允许在 current export view strip 了目标 field/table、或 target feature 在 profile 中未启用时被忽略；忽略必须写 report 和 patch result summary。base mismatch、identity mismatch、checksum mismatch、signature failure 不能因 required=false 被忽略。
- patch operation 的 source location 只用于 report/debug，不参与 target identity；同一 patch 在不同机器或不同 package path 中 target 不变。

patch operation kind 默认语义：

```text
set_property: 修改一个 field / child field canonical value
set_reference: 修改 reference identity / external runtime key
move_key: 修改 key/path 字段但保持 row identity
replace_row: 替换整行 canonical field set；必须声明 allowed_patch_kinds
restore_row: 从低层 tombstone 或 delete marker 恢复同一 row identity；必须声明 base tombstone hash 和 allowed_patch_kinds
add_row: 新增 stable row；必须携带完整 identity/key/required field payload
delete_row: 写 tombstone，遮蔽低层 row
patch_child: 按 child element identity 修改 child field / order
delete_child: 写 child tombstone，遮蔽低层 child element
```

patch operation apply 默认流程：

1. 按 layer order 得到当前 materialized candidate。
2. 校验 operation target 的 table/row/field/child identity 和 schema/export view。
3. 校验 base_value_hash / target_base_source_hash / target_layer_stack_hash。
4. 应用 operation 到 staged materialized candidate，不立即暴露给 runtime index。
5. 更新 staged identity/key/reference/dependency/preload indexes。
6. 对 staged candidate 执行 duplicate key、hard reference、required field、export policy 和 dependency validation。
7. 所有 active layer 成功后，发布 materialized_source_hash 和 runtime patch plan；任一步失败保留旧 source/layer stack。

materialization 默认规则：

- RuntimeDatabase 打开或切换 layered source 时，先在 candidate build 阶段应用所有 active layer，生成 materialized object slots、key index、reference index、dependency graph 和 preload plan。
- getter、reference resolver、key lookup 和 dependency traversal 只读取 materialized view；稳定热路径不沿 layer 链查找，不分配。
- materialized view 中同一 asset identity 只对应一个 resident object；被高层 patch 的 object 仍保持原 identity 和 `GetInstanceID()`，除非 patch plan 判定必须 recreated。
- layer 只改变 debug/source location，不改变 canonical value 时不得发布 property_changed；可发布 source_summary/layer_summary。
- materialized debug symbol 可以记录每个 property 的 winning layer id 和 overridden layer ids；debug symbol 不参与 source_hash。

validation 默认规则：

- layer 的 schema_hash/export_view_hash 必须与 base materialization context 兼容；不兼容时 open/switch 失败。
- hard reference 在 materialized view 中缺失，仍按 reference policy blocker；不能因为目标在低层存在但被高层 tombstone 删除而继续解析。
- overlay 改 key 后导致 duplicate key，产生 `validation.unique_key_duplicate`；不按 layer 优先级选择赢家。
- overlay patch 的 field 在当前 export view 被 strip 时，产生 `schema.export_policy_invalid` 或忽略该 patch，具体由 patch descriptor 的 required flag 决定。
- patch operation 指向不存在的 row/field/child 且不是允许的 add/create，产生 `source.overlay_conflict` 或 schema/reference diagnostic。

Release / trust 默认规则：

- Release 默认只允许 `ConvertedBytesDataSource` base + signed converted `patch_bytes` hotfix/DLC layer；Excel layer 和 development override 默认产生 `source.overlay_forbidden`。
- hotfix patch 必须声明 target base source hash 或 target layer stack hash；不匹配产生 `source.layer_base_mismatch`。
- 如果 project policy 要求签名，patch manifest signature、certificate/key id、signature algorithm 和 signed payload hash 必须验证；失败产生 `source.patch_signature_invalid`。
- 签名 payload 默认覆盖 canonical layer descriptor（排除 signature 字段自身）、`patch_operation_digest`、target base/layer hash、provider id/version、trust policy id/hash 和 activation result；debug name、source location、report 文本和 display path 不参与签名。
- certificate/key id 必须通过 project policy allowlist 或 platform trust store；trust result 只影响 gate/report/cache 信任状态，不改变 canonical data 值或 `materialized_source_hash`。
- trust/signature 不进入 materialized_source_hash 的 canonical data 值，但进入 startup report、gate policy 和 cache/trust verification result。

manifest / cache 默认规则：

- artifact manifest 记录 `source_layers[]`、`layer_stack_hash`、`materialized_source_hash`、per-layer source_hash、per-layer patch digest、activation assignment/result summary 和 trust result。
- build cache key 包含会影响 materialized bytes 的 layer descriptors、activation condition、source hashes、patch operation bytes 和 trust-required provider version。
- 如果 convert 选择输出单一 materialized bytes，bytes header/source_hash 使用 `materialized_source_hash`，debug manifest 仍保留 layer stack。
- 如果 convert 选择输出 base bytes + patch bytes，patch bytes 必须有独立 header、target base/layer hash、patch operation section 和 signature/trust metadata。

hot reload / source switch 默认规则：

- layer activation、layer order、patch bytes、base source 任一变化都构成 source switch candidate。
- layer activation 的 assignment id / input hash / result 改变时，先按完整 source switch preflight 重建 candidate；commit 成功前旧 layer stack 继续服务。
- 如果 materialized_source_hash 和 all runtime digests 一致，只发布 source_summary/layer_summary，不发布 property_changed。
- layer stack 变化导致 materialized value 改变时，按普通 patch plan 发布 property_changed/dependency_changed/added/removed/recreated。
- source switch 失败时旧 base、旧 layer stack、旧 materialized indexes 继续服务。

mount normalization 默认规则：

- `normalized_mount_path` 使用项目相对路径，统一 `/`，去掉重复分隔符和尾部 `/`。
- Windows 路径比较使用 ordinal ignore-case；manifest/report 中保留 normalized display path。
- `canonical_physical_id` 可用时来自 file id/inode/realpath；不可用时使用 normalized path + workbook guid + metadata checksum 做弱检测。
- 同一路径重复 `MountWorkbook` 幂等，返回已有 source context。
- 同一 physical workbook 多路径 mount 默认只保留一个 source context，并报告 `source.mount_path_conflict` warning/error；调用方必须显式 unmount/move 后再改变 mount root。
- 不同 physical workbook 使用同一 normalized mount path 是 `source.mount_path_conflict` blocker。
- 不同 physical workbook 拥有同一 workbook guid 仍是 `metadata.duplicate_workbook_guid` blocker，不由 mount path policy 解决。

workbook alias 默认规则：

- alias 是人类输入和 Excel 可见 reference token 的短名；不参与 workbook identity、source_set_hash、asset guid 或 local file id。
- alias 默认从 workbook descriptor 或 mount policy 读取；如果未声明，可以从文件名 basename 派生，但只有在 source set 内唯一时才可用。
- alias 规范化使用 case-insensitive ordinal compare；显示保留原大小写。
- alias 为空或冲突时，resolver 不使用 alias；需要跨 workbook disambiguation 的 visible token 必须使用 full asset path 或用户显式配置 alias。
- 同一 source set 内两个 workbook alias 归一化后相同，产生 `source.workbook_alias_conflict`；如果任何 schema reference target scope 使用该 alias，operation blocker。
- workbook move 不改变 alias，除非 alias 是 auto-derived 且项目 profile 声明 `auto_alias_tracks_filename=true`；即便 alias 变化，也只影响 display/layout，不改变 identity。
- converted bytes manifest 必须记录 alias/display 信息用于 debug/report，但 runtime resolver 默认不依赖 alias。

默认规则：

- workbook guid 必须在同一 context 内唯一。
- 同一路径重复 `MountWorkbook` 幂等。
- 同一 workbook guid 出现在新路径，且旧路径不存在或被显式 unmount/move 时，视为 workbook move；更新 path mapping，不改变 asset identity。
- 同一 workbook guid 同时出现在两个不同 physical workbook，默认 blocker：`metadata.duplicate_workbook_guid`；即使当前内容 hash 相同也不能自动当作同一 source。
- 同一 physical workbook 通过多个 mount path 出现时按 duplicate mount 处理，只保留一个 source context。
- 复制 workbook 作为新配置源时，必须执行 clone/repair operation 生成新 workbook guid；不能靠路径不同自动当作新身份。
- workbook display name / alias 只是人类可读定位，不参与身份。

跨 workbook asset path：

- 默认 asset path 仍包含 workbook path：`{workbook_asset_path}/{table_schema_name}/{escaped_key_path}`。
- `GUIDToAssetPath` 在 mount set 内返回当前 workbook path 下的 current asset path。
- workbook move 后，guid 不变，path 变化触发 moved report/event。
- `LoadAssetAtPath` 只按当前 path 解析；旧 workbook path 不猜测身份，只通过 moved report 指向新 path。

内部引用默认身份：

```text
target workbook guid
target table id
target row guid/local id
```

Excel 表达：

- 可见 cell 默认显示目标 key/path；跨 workbook 时显示 workbook alias 或 asset path 前缀，方便策划区分。
- metadata/隐藏 companion data 必须保存 target workbook guid + table id + row guid/local id。
- 如果用户只输入 key/path 且没有 workbook 前缀，resolver 在 schema 声明的 target scope 内查找。
- target scope 默认是 mounted workbook set；如果找到多个匹配目标，报 `reference.ambiguous_target` error，不按路径或加载顺序猜。
- schema 可以把 target scope 收紧为 same table、same workbook、specific workbook alias、specific table/group。
- metadata identity 与可见 key/path 冲突时，仍按 14.17 的 identity 优先规则处理。

批量 refresh / hot reload：

- watcher 可以同时收集多个 workbook 变化；同一 debounce 窗口内的变化默认组成一个 source set candidate。
- candidate 必须重建全局 key index、identity map、reference graph、reverse dependency index。
- 如果变化 workbook 之间存在跨 workbook hard reference，commit 默认按 source set 原子提交：任一 blocker 使整个 candidate 不替换当前 object graph。
- 如果 workbook 之间没有依赖关系，editor import 可以分别产生 report；runtime hot reload 仍默认按 source set 一次发布 ChangeSet，避免业务看到中间状态。
- ChangeSet 的 `source_summary` 必须列出 affected workbook guids、paths、source hash before/after。

converted bytes source set：

- converted bytes 可以是单文件包含整个 source set，也可以是 manifest + 多 bytes 文件；完整目标版默认优先单文件 source set，同时保留 manifest + 多 bytes 文件的确定性协议。
- converted bytes manifest 必须保存 `source_set_descriptor_hash` 和 `workbook_entries[]` 的 runtime/report 子集，用于验证 Excel source set 与 bytes source set 是否是同一组 workbook。
- source set hash 由 workbook guid + per-workbook source hash + schema hash + exported data 按 workbook guid/path deterministic 排序组合。
- Excel source set 和 converted bytes source set 的 identity map、key index、reference graph 必须一致，才能认为热切换等价。

#### 14.14.3 默认 Excel / converted bytes source equivalence 协议

ExcelDataSource 与 ConvertedBytesDataSource 是同一数据模型的两个 source implementation。热切换时不能只比较文件时间或 bytes path；必须用 machine-readable equivalence preflight 判断“同源 no-op”、“同身份可 patch”还是“不兼容失败”。

SourceEquivalence 默认输入：

```text
source_kind_before
source_kind_after
descriptor_hash
schema_hash
export_view_id
export_view_hash
layout_hash
source_set_descriptor_hash
source_set_hash
layer_stack_hash optional
materialized_source_hash optional
workbook_sources[]
identity_index_digest
key_index_digest
reference_index_digest
dependency_index_digest
preload_plan_hash optional
preload_plan_digest optional
runtime_provider_registry_hash
unity_dependency_runtime_hash
convert_profile_hash
```

`workbook_sources[]` 默认字段：

```text
workbook_guid
normalized_workbook_path optional
workbook_alias optional
workbook_role optional
source_hash
row_count_by_table
exported_row_count_by_table
metadata_checksum optional
source_revision optional
```

digest 默认规则：

- `identity_index_digest` 覆盖 workbook guid、table id、row guid/local id、asset kind、runtime type id、object slot sort key。
- `key_index_digest` 覆盖 table id、key scope、canonical key hash/string id、asset identity，不覆盖可变 display text。
- `reference_index_digest` 覆盖 source identity、field id、target identity / Unity runtime key、reference strength、dependency kind。
- `dependency_index_digest` 覆盖 dependency edge 的 source slot、target slot、dependency kind，以及 reverse dependency index 的等价内容。
- `preload_plan_digest` 覆盖命名 preload plan、root set、dependency kind filter、target family filter、recursive/depth policy、预计算 closure range 和排序结果；未输出 `preload_plan_index` 时为空。
- digest 使用 canonical bytes + SHA-256 lowercase hex；排序固定，不依赖字典顺序、workbook mount 顺序或本机路径。

switch preflight 默认判定：

- descriptor_hash / schema_hash 不一致：open/switch 失败，保留旧 source；除非 schema descriptor 声明显式 runtime compatibility adapter。
- `export_view_hash` 不一致：不能作为等价 no-op；如果 runtime profile 不允许切换目标视图，产生 `convert.export_view_mismatch` 并保留旧 source。
- `source_set_descriptor_hash` 不一致但 workbook guid set、identity/key/reference/dependency/preload_plan digest 一致：不是 data mismatch，可以作为 ordinary switch 更新 source summary/path/alias display；不得发布 property_changed。
- layer_stack_hash 不一致但 materialized_source_hash 和所有 runtime digest 一致：这是 layer source summary 变化，不发布 property_changed；可以发布 source_summary/layer_summary。
- layer_stack_hash 一致但 materialized_source_hash 或任一 runtime digest 不一致：产生 `source.layer_stack_mismatch` 或 `source.equivalence_mismatch` blocker，保留旧 source。
- materialized_source_hash 一致且所有 digest 一致：这是同源 no-op switch；可以只更新 `source_summary`，不得发布 property_changed。
- materialized_source_hash 不一致但 identity index 可建立稳定对应：进入普通 source switch diff/patch；added/removed/moved/property/dependency event 由 patch plan 决定。
- source_set_hash / dependency_index_digest 一致但 preload_plan_digest 不一致：不得发布 property_changed；如果当前 runtime 暴露命名 preload plan 查询，则发布 source_summary + dependency_plan_changed，或按 profile 重新 open/switch。
- Excel source 与 converted bytes manifest 的 workbook set 不一致：产生 `source.source_set_mismatch`，不能当作等价 no-op；若作为普通 switch 仍有 missing hard reference 或 identity blocker，则失败并保留旧 source。
- Unity runtime dependency hash 不一致但 source_set_hash 一致：不能宣称 Release bytes 等价；Development 可继续 editor/debug switch，但 build/Release gate 必须重新 convert 或失败。

SourceEquivalence report 默认字段：

```text
equivalence_result: equivalent_noop | layer_summary_only | patchable | source_set_mismatch | layer_stack_mismatch | schema_mismatch | index_mismatch | dependency_mismatch
source_kind_before
source_kind_after
source_set_hash_before
source_set_hash_after
export_view_hash_before
export_view_hash_after
layer_stack_hash_before
layer_stack_hash_after
materialized_source_hash_before
materialized_source_hash_after
descriptor_hash_before
descriptor_hash_after
schema_hash_before
schema_hash_after
mismatched_digest_kinds[]
affected_workbook_guids[]
candidate_patch_required
```

默认边界：

- equivalence preflight 不 materialize runtime objects，不执行 migration，不修复 metadata。
- equivalence report 是 source switch / hot reload report 的子报告；业务逻辑不能解析 human-readable 文本。
- no-op switch 可以更新 current source kind/path/display summary，但不能改变 resident object identity、instance id、key index 或 dependency graph。
- 如果 converted bytes 缺 manifest，仍可从 bytes header/index 计算 equivalence digest；report 必须标记 `manifest_missing=true`，Release policy 可以禁止。

#### 14.14.4 默认 runtime scheduler / publish point / threading 协议

RuntimeDatabase 的 resident object graph、key index、reference graph、dependency graph 和 ChangeSet buffer 默认由一个 database context owner 线程拥有。核心库不假设 Unity 主线程，但 Unity adapter 默认把 owner 线程绑定到 Unity main thread。

默认线程边界：

- `LoadAsset`、`TryGetAsset`、`GetAssets`、引用解析、依赖遍历、`Open`、`SwitchDataSource`、`Refresh`、`Close` 默认只能在 owner 线程调用。
- 文件 watcher 线程、后台 import 线程、CI worker 线程不能直接 patch resident objects，不能发布 ChangeSet，不能调用 UnityEditor / UnityEngine API。
- 后台任务只允许读取 immutable source snapshot、构建 candidate snapshot、生成 diff/report、准备 validation 结果和预估 buffer capacity。
- 所有会改变 current source、resident object、runtime index、dirty state、event buffer 的 commit 都必须回到 owner 线程。
- 跨线程读取 resident `Object` graph 默认禁止；需要跨线程读配置、AI worker、Job/ECS 或后台缓存构建时，必须使用 14.14.5 的 immutable `RuntimeSnapshot` / generated read view。

默认发布点：

- core runtime 默认 manual publish：只有显式 `RuntimeDatabase.Refresh()` / `SwitchDataSource()` / `Open()` / `Close()` 调用会执行 commit 和发布事件。
- `EnableHotReload()` 只允许 watcher 入队和 host scheduler 触发 refresh，不代表 watcher 线程可以立即 commit。
- Unity Editor authoring 默认在 editor update 中处理 watcher queue；Play Mode 默认在 Unity main thread 的稳定 PlayerLoop 发布点处理，不能在 inspector repaint、property drawer `OnGUI`、文件 watcher callback 或事件回调中直接 patch。
- Development build 如果启用 hot reload，必须声明 host scheduler 的 publish point；默认建议在一帧 gameplay update 前或后固定位置执行，不能在同一帧任意系统中途插入。
- Release mode 不启用 watcher/path/debug hot reload；path-based converted bytes refresh 或 hotfix layer 更新必须作为 signed source/layer activation，由显式 `Refresh` / `SwitchDataSource` / 受控 scheduler 触发，并通过 trust/signature/profile gate。

commit / read 一致性默认规则：

- candidate build 可以和上一版 runtime 读取并行，但 commit 不能和运行时读取/遍历交错。
- commit 默认是短暂停顿的 atomic publish：先完成 object/index patch，再切换 current source，再发布 ChangeSet。
- 一个 gameplay frame 内如果没有到达 publish point，读者看到同一 source revision；不会半帧读到旧对象、半帧读到新索引。
- `GetAssets(Span<T>)`、dependency traversal、ChangeSet event view 在一次调用内看到同一 revision；如果 publish 排队中，等待当前调用结束后再 commit。
- 订阅者回调期间禁止重入修改当前 ChangeSet；新的 `Refresh/Switch/Open/Close` 进入 owner queue，在当前 publish 完成后按 deterministic 顺序处理。
- `changed` 回调期间，owner-thread read API 默认读取 `revisionAfter`；如果业务需要 `revisionBefore` 的完整数据，必须在 publish 前已持有旧 `RuntimeSnapshot`，不能从 ChangeSet 借用旧对象图。
- 回调中 `TryAcquireSnapshot` 默认返回 `revisionAfter` 的 immutable snapshot；不会返回半更新 revision，也不会为了 before/after 对比临时复制对象。

owner queue 默认规则：

- watcher event、manual refresh request、source switch request、close/open request 都进入 owner operation queue。
- operation queue 按 request sequence deterministic 排序；同一 workbook/source 的 refresh request 可以 coalesce。
- queue / pending ChangeSet / pending report buffer 属于水位线；超过容量可以扩容但必须写 allocation report。
- 如果 queue overflow 且不能扩容，默认丢弃低优先级重复 refresh request，不丢 explicit `SwitchDataSource/Open/Close` request，并写 `runtime.operation_queue_overflow` diagnostic。

ChangeSet view 生命周期：

- non-alloc `ChangeSet.events` view 默认只保证在 `changed` 回调栈内有效。
- 业务如果要跨帧保存事件，需要复制需要的 identity/code/path；这部分分配属于业务选择。
- `AssetIdentity`、row guid/local id、field id 是可长期保存的稳定值；event view 的 slices、property path view、debug string view 不保证跨 publish 稳定。
- ChangeSet view 不能跨线程转发；如果 host adapter 想把事件异步派发给其他线程，必须先生成自己的 persistent event artifact 或 stable identity 列表，并在 report 中标记这是 adapter projection。
- `objectChanged` 只是从同一个 ChangeSet 派生的便利投影，不拥有独立事实源；它不能延长 ChangeSet view 生命周期，也不能补发完整 removed/dependency/source_summary 语义。

subscriber dispatch 默认规则：

- subscriber list 在每次 publish 开始时冻结为 dispatch snapshot；回调中新增/移除 subscriber 只影响下一次 ChangeSet。
- dispatch 顺序按 subscription sequence 稳定排序；static facade 订阅先绑定到当时 current context，再在该 context 的 sequence 中排序，不受后续 `BindContext` 影响。
- subscriber registration / unregistration 是低频 authoring/runtime setup 操作，可以使用锁或扩容；ChangeSet dispatch 本身必须使用预分配 subscriber slot/view，不在稳定 publish 窗口分配。
- subscriber 回调同步运行在 owner thread；默认不并行、不切线程、不在 watcher/background 线程调用。
- subscriber 回调抛异常时记录 `runtime.subscriber_failed`，继续通知后续 subscriber；项目 profile 可以声明 fail-fast，但仍不能回滚已经 commit 的 source/object/index。
- subscriber 回调中要求立即嵌套 publish 的 API 产生 `runtime.publish_reentrant` 或被排入 owner queue；默认不允许当前 ChangeSet 被二次修改。

Unity adapter 默认规则：

- 任何需要 UnityEditor.AssetDatabase、UnityEngine.Object、Unity main asset path 查询、inspector/drawer 的操作只能在 Unity main thread 执行。
- UnityResourceRef 的 guid/path validation 如果需要 Unity AssetDatabase，必须作为 main-thread adapter validation step；核心后台 import 只能验证格式和已有 manifest。
- PlayerLoop hook 必须可开关并在 report 中标记 publish mode；开发包里自动 hot reload 必须有明显模式标识。

#### 14.14.5 默认 immutable RuntimeSnapshot / 跨线程读取协议

`RuntimeDatabase` 的 canonical resident `Object` instance 是 owner-thread 可变视图，用于 Unity-like `LoadAsset`、引用解析、hot reload patch 和 ChangeSet 发布。跨线程读取不能通过给这套对象图加锁实现；默认一等读取面是 immutable `RuntimeSnapshot`，它在 publish revision 上提供只读、无分配、可 pin 生命周期的 generated read view。

默认边界：

- `RuntimeSnapshot` 捕获一个已发布 revision：`source_revision`、`schema_hash`、`export_view_hash`、`source_hash`、`source_set_hash`、`layer_stack_hash`、`materialized_source_hash`、`load_set_manifest_hash`、`segment_index_digest` 和 loaded load set 状态。
- snapshot 只暴露 generated read view / table view / dependency view，不暴露 mutable `Object` instance、`SerializedObject`、`SerializedProperty`、UnityEditor API 或可写 dirty state。
- owner-thread API `LoadAsset<T>` / `TryGetAsset<T>` 返回 resident `Object`；snapshot API `TryGetAsset<TView>` / `GetAssets<TView>` 返回 generated read view。两者不能共用同一个可变对象引用。
- snapshot read view 的字符串、数组、repeated、child range 和 dependency range 默认是对 immutable string table / range table 的 slice；读取不创建 `string`、`List<T>`、数组或分配式 enumerator。需要 materialize managed string/list 的 API 必须标记为 low-frequency / allocating。
- ExcelDataSource 当前 source 也必须先归一化成 canonical materialized source snapshot；后台线程不能直接读取 workbook ZIP/XML/cell 对象作为 runtime snapshot。

生命周期默认规则：

- `TryAcquireSnapshot` 从预留的 snapshot handle pool 获取句柄并 pin 当前 revision 需要的 immutable source view、segment、string table、range table、index 和 provider handle。
- `RuntimeSnapshot.Dispose()` 释放 pin；Dispose 本身不得分配，不写 workbook，不发布 ChangeSet。
- source switch / hot reload publish 后，新 acquire 的 snapshot 看到新 revision；旧 snapshot 继续看到旧 revision，直到 Dispose。旧 snapshot 不是 stale error，除非它被 Dispose 后继续使用或底层校验失败。
- `Close` 不强制立即破坏已 acquire 的 snapshot；它清空 current source session，但被 pin 的 immutable sections 只能在最后一个 snapshot / provider handle 释放后回收。
- `UnloadSet` 遇到 active snapshot pin 时产生 `runtime.load_set_unload_blocked`，不半卸载；如果 unload 成功，之后 acquire 的 snapshot 不包含该 load set payload。
- 超过 snapshot handle / pin 水位线时按 allocation policy 处理；不能扩容或 policy 禁止时 `TryAcquireSnapshot` 返回 false，并记录 `runtime.snapshot_pool_exhausted`。

线程安全默认规则：

- `RuntimeSnapshot` 可以在非 owner 线程读取；实现必须使用 immutable memory、generation-checked handle 和预绑定 accessor，热路径不加全局锁、不访问 owner mutable queue。
- acquire/release 允许使用原子引用计数或 host-provided safety handle；它们属于水位线管理，不得在稳定帧产生 GC allocation。
- 请求 `ThreadSafe` / `JobSafe` / `BurstSafe` 能力时，source、schema value shape、codec、provider 和 generated accessor 必须声明支持；不支持时 acquire 失败并记录 `runtime.snapshot_capability_unsupported`，不能退化成读 resident object。
- Burst/ECS 子集只允许 blittable/fixed-layout scalar、opaque identity/key、fixed slice/range 和 provider key；managed string、managed object wrapper、UnityEngine.Object、polymorphic managed payload 不属于默认 BurstSafe read view。
- owner-only API 如果从非 owner 线程调用，默认失败并记录 `runtime.thread_violation`；debug build 可以断言，Release 不允许静默读取可变对象图。

publish / snapshot 一致性：

- publish point 先完成 resident object/index patch，再构建或切换 current immutable snapshot root，最后发布 ChangeSet。
- snapshot acquisition 与 publish 竞争时，要么拿到 publish 前完整 revision，要么拿到 publish 后完整 revision；不能拿到半更新 root。
- ChangeSet 中的 `source_revision_before/after` 必须能和 snapshot revision 对齐；业务可以在事件里只保存 `AssetIdentity`，然后在下一帧用新 snapshot 重查。
- layered source / patch / load set 切换后，snapshot root 必须记录 materialized layer stack 和 loaded load set bitmap；后台缓存不能只按 base source hash 判断是否可复用。

默认 API 调用示范：

```csharp
if (!RuntimeDatabase.TryAcquireSnapshot(out var snapshot))
    return;

try
{
    Span<CharacterConfigView> heroes = stackalloc CharacterConfigView[128];
    var status = snapshot.GetAssets(heroes, out var total);
    var written = Math.Min(heroes.Length, total);

    for (var i = 0; i < written; i++)
    {
        ref readonly var hero = ref heroes[i];
        CombatPrecompute.Write(hero.identity, hero.maxHp, hero.attack);
    }

    if (status == RuntimeQueryStatus.Truncated)
        CombatPrecompute.MarkCapacityInsufficient(total);
}
finally
{
    snapshot.Dispose();
}
```

Job/ECS 示例只传递 snapshot job view 和 caller-owned output buffer：

```csharp
using var snapshot = RuntimeDatabase.AcquireSnapshot(new RuntimeSnapshotOptions(
    RuntimeSnapshotCapability.ThreadSafe | RuntimeSnapshotCapability.JobSafe));

var job = new BuildSkillCacheJob
{
    snapshot = snapshot.AsJobView(),
    output = skillCacheWriter
};
job.Schedule(skillCount, batchSize).Complete();
```

默认实现要求：

- `RuntimeSnapshot` API 必须能在 `Reserve/Prewarm` 后无 GC acquisition/read/release；如果项目选择 snapshot acquire 也算低频 operation，必须提供一个 `TryRetainCurrentSnapshot` 或等价 no-GC 热路径入口。
- snapshot view 不能通过 LINQ、`IEnumerable<T>`、反射、字典装箱、闭包捕获或临时字符串实现。
- generated view 类型由 schema/codegen 产生，例如 `CharacterConfigView`；它不是 `CharacterConfig : Object`，也不允许写回 Excel。
- snapshot pin leak detection 是 debug/development/CI 功能；长时间未释放或 context close 后仍持有的 snapshot 记录 `runtime.snapshot_pin_leak`，但不强制破坏仍在使用的读视图。

### 14.15 默认 asset path / guid / lookup 协议

Unity adapter 默认暴露 Unity-like asset path，但 identity 不依赖 path。

默认 asset path：

```text
{workbook_asset_path}/{table_schema_name}/{escaped_key_path}
```

例子：

```text
Assets/Configs/game.xlsx/SkillConfig/fireball
Assets/Configs/game.xlsx/CharacterConfig/hero
```

默认规则：

- `workbook_asset_path` 使用 mount 时传入的项目相对路径，统一使用 `/`。
- `table_schema_name` 默认使用 schema name，不使用可变 sheet display name。
- `escaped_key_path` 来自 schema 声明的 key/path pattern；其中 `/`、`%`、`?`、`#` 必须 percent-encode。
- key/path 变化只改变 asset path，不改变 object identity。
- sheet rename 不改变 asset path，除非 schema name 明确迁移。
- `Object.name` 默认来自 display name field；没有 display name 时使用 key；再没有时使用 `{table_schema_name}/{row_guid}`。

默认 guid/local id：

- asset guid 由 workbook guid + table id + row guid 派生，必须稳定、deterministic。
- local file id 由 workbook guid + row local id 派生，必须在同 workbook 内稳定；row local id 删除后不复用。
- `AssetDatabase.GUIDToAssetPath(guid)` 返回当前 path；key rename 后返回新 path。
- `AssetDatabase.LoadAssetAtPath<T>(oldPath)` 在 path 已迁移后默认返回 null，并通过 moved/renamed event/report 指向新 path；不要用旧 path 猜测 identity。
- runtime 可按 key lookup，但 key lookup 只是索引入口，最终返回 row guid/local id 对应的 object。

`FindAssets` 默认行为：

- 支持 `t:TypeName`、key/name 文本搜索、table scope。
- 返回 guid 列表，排序按 current asset path deterministic。
- warning/error 状态的 asset 可以被查到，但 blocker/invalid row 不进入有效 asset set，除非调用 debug/report API。

#### 14.15.1 默认 key schema / canonical key / asset path 协议

key 是策划、工具和运行时代码可读的定位入口；identity 仍然只来自 workbook guid + table id + row guid/local id。schema 必须显式声明 key descriptor，不能让 importer 从列名、第一列或 `name` 约定中猜测。

table key descriptor 默认字段：

```text
key field ids[]
key scope: table | workbook_table | source_set
key normalizer id/version
key compare policy
key path pattern
path segment fields[]
display name field id
alias key field ids[]
create_from_path policy
duplicate policy
rename policy
```

默认 schema 规则：

- `key field ids` 必须引用稳定 field id；字段 rename、移动、表头改名不改变 key descriptor。
- 默认 key 字段只能是 scalar、string、integer 或 enum；simple struct 只有在 codec 声明 deterministic key canonical writer 时才能作为 key；repeated、blob、float、formula-only、non-deterministic custom codec 默认不能作为 key。
- key 字段默认 required；缺失、空白或无法 parse 时产生 `key.missing_required` / `cell.parse_failed`，该 row 不进入有效 key index。
- `key scope=table` 是默认值：同一 source set 内相同 table id 的 key 唯一；`workbook_table` 允许不同 workbook 的同 table 重复 key；`source_set` 要求所有 asset table 的 key 全局唯一。
- `key normalizer id/version` 是 schema 的一部分；缺失 normalizer、normalizer 版本不匹配或 non-deterministic normalizer 都是 schema/import blocker。
- default value 不会静默填充缺失 key；只有 schema 显式 `allow_default_key=true` 时，materialized default 才能参与 canonical key，并必须写入 report。
- display name 只是 `Object.name` 和搜索展示；display name 变化不改变 key，除非它同时是 key field。

canonical key 默认规则：

- canonical key 在字段 parse、enum value id 解析、string normalizer 执行之后生成。
- canonical key bytes 使用 deterministic UTF-8 record：`format_version + key_scope + table_id + key_field_ids + field_canonical_values`。
- string 比较默认 ordinal case-sensitive；如果 schema 选择 case-folding 或 trim，必须通过 normalizer 明确声明，不能依赖 Excel/系统区域设置。
- enum key 的 canonical identity 使用 enum value id；用于 path/display 的文本使用 schema 当前 export token，因此 enum token rename 可以导致 asset path moved，但不改变 row identity。
- key hash 只是索引加速；命中后必须比较 canonical key bytes 或 key string id，hash collision 不能返回错误 asset。
- duplicate key 不选择“第一行获胜”；冲突范围内的所有 row 都产生 `validation.unique_key_duplicate`，不进入常规 key lookup / converted runtime index。

asset path 派生默认规则：

- `escaped_key_path` 只由 canonical key 和 `key path pattern` 派生；不从中文名、sheet name、row number 或 Excel display formatting 派生。
- key path pattern 默认由 literal segment 和 field placeholder 组成；每个 placeholder 先格式化成 canonical display token，再作为单个 segment payload 进行 escaping。placeholder 输出不能跨 segment；要表达层级必须由 schema 显式声明多个 segment。
- path segment 按 UTF-8 percent-encode：`%`、`/`、`\`、`?`、`#`、ASCII control chars、Windows 保留文件名字符、空 segment、`.` / `..` segment、前后空格都必须编码或报 `key.segment_invalid`。
- path 规范化统一使用 `/`；不做隐式 trim、大小写折叠或 Unicode normalization，除非 normalizer 明确声明。
- 默认要做 case-insensitive path collision 检查；两个不同 canonical key 如果生成只差大小写的 asset path，产生 `key.asset_path_collision`，避免 Unity/文件系统/人工观察层歧义。
- `key path pattern` 必须能从 path 反解 key fields，才能支持 `CreateAsset(asset, path)`；不可逆 pattern 必须声明 `create_from_path=false`，否则产生 `key.path_not_reversible`。
- key pattern 改变只导致 asset path moved/renamed；key field 集合、normalizer 或 key scope 改变属于 migration_required，必须 dry-run duplicate/path collision/reference display 影响。

canonical asset path grammar：

```text
asset_path        := workbook_asset_path '/' table_schema_name '/' escaped_key_path
escaped_key_path  := escaped_segment ('/' escaped_segment)*
escaped_segment   := 1*escaped_char
escaped_char      := unreserved | pct_encoded
unreserved        := ALPHA | DIGIT | '-' | '_' | '.' | '~'
pct_encoded       := '%' HEXDIG HEXDIG
```

percent-encoding 默认规则：

- writer 使用 UTF-8 bytes 后逐 byte 编码；所有 percent hex 使用 uppercase，例如 space 写 `%20`、`/` 写 `%2F`、`%` 写 `%25`。
- writer 仅允许 unreserved byte 原样输出：`A-Z a-z 0-9 - _ . ~`。其他 byte 必须 percent-encode，包括空格、中文/Unicode 多字节、Windows reserved `< > : " / \ | ? *`、`#`、`%` 和 ASCII control。
- 反解 parser 接受 uppercase/lowercase hex，但 canonical report/path 输出必须 uppercase。
- `+` 没有 URL form 含义，literal plus 必须原样 `+` 或按项目 normalizer 决定；parser 不能把 `+` 解码为空格。
- `%` 后不足两位 hex、非法 hex、解码后不是合法 UTF-8、解码后包含 NUL/control 或 schema 禁止字符，产生 `key.segment_invalid`。
- 解码后的 segment 如果为空、`.`、`..`、前后有空白且 normalizer 未声明 trim/canonicalize，产生 `key.segment_invalid`；writer 不输出这类 segment。
- literal schema path segment 与 field-derived segment 使用同一 escaping/collision 规则；literal segment 不得是空、`.`、`..`，也不得在同一 pattern 中制造不可逆歧义。
- canonical path comparison 先确保 writer 输出 uppercase percent encoding，再按 ordinal 比较；collision check 额外使用 ordinal ignore-case 的 normalized path key。

path pattern 反解默认规则：

- `create_from_path=true` 要求 pattern 是无歧义的 segment-level grammar：每个 key field placeholder 必须占据完整 segment，或被 descriptor 声明的 fixed literal delimiter 包围，并且 delimiter 不会出现在该 field 的 canonical display token 中。
- 默认推荐每个 key field 一个 segment；多个 key field 合并到一个 segment 时必须声明 delimiter、escaping 和 split grammar，且 schema-lint 能证明可逆。
- 反解时先按 `/` 切 asset path segment，再 percent-decode，再按 pattern 匹配 literal/field；literal 不匹配、segment 数量不符、field parse/normalize 后无法 roundtrip，都产生 `key.path_not_reversible` 或 `key.segment_invalid`。
- 反解得到的 key field value 必须经过同一 schema parse/normalize/canonical key pipeline；不能把 path display token 直接写入 workbook。
- `CreateAsset(asset, path)`、`MoveAsset(oldPath, newPath)`、`RenameAsset(pathName, newName)` 使用同一反解流程；反解失败不创建 draft、不分配 identity、不修改 key field。
- pattern 中包含 optional segment、wildcard、regex capture、case-insensitive literal、locale-sensitive transform 或 lookup table 时，默认不可用于 `create_from_path`，除非 descriptor 提供 deterministic inverse function id/version。

lookup 默认规则：

- `LoadAssetAtPath` 先解析 mounted workbook root，再解析 table schema name 和 escaped key path；命中 stale old path 时返回 null，并报告 current path。
- runtime `LoadAsset<T>(string keyOrPath)` 的字符串入口先按 schema key 解析；如果包含 mounted workbook-style path root，再按 asset path 解析。
- `TryGetAssetKey<T>(string, out AssetKey)` 只在解析到唯一有效 identity 时返回 true；duplicate、missing、stale path、类型不匹配都返回 false，并写 stable diagnostic。
- `AssetKey` 是 source context 内的预解析 handle；成功解析后记录 table id、canonical key hash/slot 和 resolved identity generation。source switch 后 identity 仍存在时可以重绑定；identity 删除或 duplicate 后返回 false。
- 高频路径必须在初始化或 `Prewarm` 阶段把字符串 key/path 解析为 `AssetKey` 或 `AssetIdentity`；达到水位线后 handle lookup 不产生 GC allocation。
- debug/report API 可以按 identity 查看 duplicate/invalid row；常规 key/path API 不暴露半有效对象。

diagnostic code：

- `key.missing_required`：key 字段缺失、空白或 default 未允许 materialize。
- `key.normalizer_missing`：schema 声明的 key normalizer 不存在、版本不匹配或不可用于当前 host。
- `key.pattern_invalid`：key path pattern 引用未知字段、语法错误或 deterministic 规则不成立。
- `key.path_not_reversible`：schema 要求从 asset path 创建/反解 key，但 pattern 无法无损反解。
- `key.segment_invalid`：key path segment 为空、含非法 segment 或无法按规则编码。
- `key.asset_path_collision`：不同 identity / canonical key 生成同一个 normalized asset path。
- `key.lookup_ambiguous`：调用方按 key/path 查询时命中 duplicate 或冲突 candidate。

#### 14.15.2 默认 FindAssets filter / asset index 协议

Unity adapter 对外保留 Unity-like `FindAssets` 形态。ExcelDB 额外支持 workbook/table/key/path 维度，但这些只是查询索引，不改变 asset identity。

默认 API：

```csharp
public static string[] FindAssets(string filter);
public static string[] FindAssets(string filter, string[] searchInFolders);
```

asset search index 默认 entry：

```text
asset_guid
asset_identity
current_asset_path
path_sort_key
runtime_type_short_name
runtime_type_full_name
table_schema_name
object_name
canonical_key_text
key_path
label_tokens[]
asset_state
is_main_asset
is_findable_sub_asset
```

label descriptor 默认字段：

```text
label_descriptor_id
label_descriptor_version
role: asset_labels | search_labels
source_field_id
source_property_path
value_shape: enum | string | repeated_enum | repeated_string | computed
token_normalizer_id
display_token_policy
allow_new_string_labels
case_policy
deduplicate_policy
mutable
write_group optional
find_assets_enabled
export_policy
```

默认索引规则：

- index 从 successful import snapshot / editor draft candidate 构建；`FindAssets` 本身不 import、不 validate、不 materialize object。
- main asset 默认进入 index；`embedded` child 不进入；schema 声明为 `sub_asset` 且 `findable=true` 的 child 可以进入。
- warning/error 状态但 identity 有效的 asset 可以进入常规结果；blocker、invalid row、unresolved duplicate identity 默认不进入常规结果。
- `label_tokens` 只来自 schema 声明的 label descriptor；不存在 label schema 时 `l:` 查询只返回空结果，不临时创建标签系统。
- `role=asset_labels` 是 Unity-like `GetLabels/SetLabels/ClearLabels` 暴露的标签集合，同时进入 `FindAssets("l:")`。
- `role=search_labels` 只进入 `FindAssets("l:")` 和 search index，例如 category、review_state、platform marker；它不出现在 `GetLabels` 结果中，也不能被 `SetLabels` 覆盖。
- label token identity 使用 canonical normalized token；display token 只用于 `GetLabels` 返回、Project/browser 展示和 report，不参与 duplicate/diff 判断。
- enum label 使用 enum value id 做 canonical identity，display/export token rename 只刷新 label display/layout，不改变 label identity；删除正在使用的 enum label 按 enum validation 处理。
- repeated labels 按 canonical token 去重并 deterministic 排序；Excel cell 中的物理顺序只有 schema 声明 order-significant 时才进入 source_hash，否则不影响 `FindAssets` 结果排序。
- editor dirty draft 中新建且尚未 stable metadata flush 的 asset 可以在 Editor authoring 中被查到，但 guid/path 必须标记为 temporary，并且不能进入 convert/runtime index。

label API 默认语义：

- `GetLabels(Object obj)` 返回当前 editor context 中 `role=asset_labels` 的 display token 数组，排序为 label descriptor order + canonical token order；没有 asset label descriptor 时返回空数组并记录 `assetdb.labels_not_supported` info/warning。
- `GetLabels` 是 Unity-like editor projection API，返回数组本身可以分配；内部 label index、canonical token lookup 和 stable repaint cache 仍应使用 string table / buffer，不能把该 API 当运行时热路径。
- `SetLabels(Object obj, string[] labels)` 与 `ClearLabels(Object obj)` 不直接写 workbook；它们生成 label edit operation、undoable editor draft diff、dirty/status/index update，落盘仍由 `SaveAssets` 完成。
- `ClearLabels(obj)` 等价于 `SetLabels(obj, Array.Empty<string>())`，但 report action kind 记录为 clear，便于 UI/Undo 展示。
- 只有 schema 声明了唯一 mutable `role=asset_labels` write group 时，`SetLabels` 才允许写入；没有 mutable label descriptor、存在多个可写 write group 但无 primary、label 字段 readonly/deprecated/reserved、目标 object invalid/stale/temporary_imported 或跨 context 时，operation 不创建 draft，并报告 `assetdb.labels_not_supported` / `assetdb.label_mapping_ambiguous` / 对应 context diagnostic。
- `SetLabels` 输入先按 label descriptor normalizer parse/normalize/deduplicate；null label、空白 label、normalizer 拒绝、enum label 不存在且不允许新增 string label、数量/长度/regex 超限时，不创建 draft，并报告 `assetdb.label_invalid` 或字段 validator diagnostic。
- 对 enum label 字段，`SetLabels` 只能写已有 enum value id；不能因为传入新字符串自动扩展 enum。新增枚举值必须改 schema。
- 对 string label 字段，只有 `allow_new_string_labels=true` 才允许写入新 token；normalizer 输出 canonical token，SaveAssets 写 schema 指定的 visible token 格式。
- `SetLabels` 必须保留 `search_labels` 派生标签，不把 category/review_state 等只读搜索标签清掉；如果调用方传入与只读 search label 同名但不可写的 token，只写入 asset label 字段，不能修改派生字段。
- label edit 与普通 `SerializedProperty` 修改共享 diff/merge/validation/reference/source revision 规则；Excel 外部同时改标签字段时进入 conflict，不能由最后一次 `SetLabels` 静默覆盖。
- label 字段如果进入 converted bytes/export view，label edit 会改变 source_hash 并在 runtime hot reload 中表现为 property_changed / source_summary；如果 label 字段是 editor-only 且不影响 export，则只更新 editor search/status index。
- direct Excel edit 改 label 字段时，`Refresh` 重新计算 label index；只改 display token 但 canonical token 不变时只刷新 layout/display report，不触发 runtime property_changed。
- label operation report 必须包含 `label_operation_id`、asset identity、source labels、target labels、normalized labels、write field ids、preserved search labels、dropped duplicate tokens、validator diagnostics、source revision 和 result。
- label edit analyze/preflight/commit 使用 operation arena；达到水位线后 canonical label normalization、dedup、index update 和 machine-readable report append 不得产生 GC allocation。`GetLabels` 返回数组属于 Unity-like projection 分配，必须和 core operation no-GC 证明分账。

filter tokenization 默认规则：

- `null` 或空 filter 表示匹配当前 scope 内全部有效 asset。
- filter 按 whitespace 分词；双引号包裹的内容作为一个 token，例如 `"fire ball"`。
- quoted token 内支持 `\"` 和 `\\` 转义；未闭合引号返回空数组并报告 `assetdb.find_filter_invalid`。
- token 比较使用 culture-invariant case-insensitive ordinal matching；不依赖当前系统区域设置。
- prefix 名称使用 ordinal ignore-case 解析，report 中 canonical 为 lower-case；只有保留 prefix 被解析为结构化 token。未知 `xxx:yyy` 默认作为普通 name token，以免业务 key 中的冒号被误判。
- structured token 的 query 为空时默认 invalid，例如 `t:`、`l:`、`name:`、`key:`、`table:`、`path:`、`guid:`、`state:` 都返回空数组并报告 `assetdb.find_filter_invalid`；搜索全部 asset 使用空 filter，不使用空 prefix。

canonical grammar：

```text
filter          := ws* token (ws+ token)* ws*
token           := quoted_token | bare_token
quoted_token    := '"' quoted_char* '"'
quoted_char     := escaped_quote | escaped_backslash | any char except '"' '\' CR LF
bare_token      := 1*bare_char
bare_char       := any char except ASCII whitespace, '"' CR LF
structured      := prefix ':' query
prefix          := 't' | 'l' | 'name' | 'key' | 'table' | 'path' | 'guid' | 'state'  // case-insensitive
query           := non-empty token payload after ':'; quoted token can include ':' inside payload
```

token normalization 默认规则：

- bare token 外侧没有额外 whitespace；quoted token 去掉外层引号并解析 escape 后参与匹配。
- quoted token 只是把 whitespace 保留在一个 token 内；它不表示 exact phrase operator。`"fire ball"` 等价于一个 substring query `fire ball`。
- `name:"fire ball"`、`l:"rare item"` 这类带 prefix 的 quoted token 使用 prefix 之后的 quoted payload；`"l:rare item"` 没有结构化 prefix，作为普通 name token。
- query 中的 `:` 只有第一个冒号且 prefix 命中保留字时有结构含义；`boss:fire` 因 `boss` 不是保留 prefix，整体作为普通 name token。
- `guid:` query 必须是 lowercase/uppercase 均可接受的 32 hex；解析后 canonical lowercase。非 32 hex 是 `assetdb.find_filter_invalid`。

保留 prefix：

```text
t:<type_name>
l:<label>
name:<text>
key:<text>
table:<schema_name>
path:<asset_path_prefix>
guid:<asset_guid>
state:<asset_state>
```

match 默认规则：

- 无 prefix token 等价于 `name:<text>`。
- `name:` 在 `object_name`、key basename、asset path basename 中做 substring match。
- `key:` 在 canonical key text / key path 中做 substring match。
- `l:` 在 label token 中做 substring match。
- `t:` 匹配 runtime type short name、runtime type full name；`t:Object` 匹配所有 ExcelDB asset object。
- `table:` 精确匹配 table schema name，不匹配可变 sheet name。
- `path:` 按 normalized current asset path 前缀匹配；不解析 stale path，不从旧 path 猜 identity。
- `guid:` 精确匹配 asset guid，主要用于 debug/tooling。
- `state:` 只在 Editor/Development debug policy 下允许查询 non-default state；Release/runtime facade 不暴露该查询。

组合语义默认规则：

- 不同 token family 之间是 AND，例如 `co l:architecture t:Texture2D` 表示 name/key/path 里匹配 `co`，并且 label/type 也匹配。
- 同一 family 的多个 `t:` token 是 OR，匹配任意一个 type。
- 同一 family 的多个 `l:` token 是 OR，匹配任意一个 label token。
- 同一 family 的多个 `state:` token 是 OR，匹配任意一个 debug state。
- 同一 family 的多个 `table:` token 是 OR，匹配任意一个 table schema name。
- 同一 family 的多个 `guid:` token 是 OR，匹配任意一个 guid。
- 多个 name token 使用 AND；每个 name token 都必须在 `object_name`、key basename 或 asset path basename 的任一字段中 substring match。这样 `fire ball` 不会匹配只有 `fire` 或只有 `ball` 的 asset。
- 多个 `key:` token 使用 AND；每个 token 都必须在 canonical key text 或 key path 中 substring match。
- 多个 `path:` token 使用 OR；任一 normalized current asset path prefix 命中即可。
- `searchInFolders` 与 filter 结果做 AND；它不改变 token 语义。

`searchInFolders` 默认规则：

- `null` 或空数组表示搜索全部 mounted workbook roots。
- folder path 使用项目相对路径，统一 `/`，去掉末尾 `/`。
- folder 可以是 mounted workbook root，例如 `Assets/Configs/game.xlsx`，也可以是其下 asset path prefix，例如 `Assets/Configs/game.xlsx/SkillConfig`。
- asset path 等于 folder 或以 `folder + "/"` 开头时视为命中。
- 未 mount 的 folder 不抛异常；该 folder 贡献空结果，并在 report 中记录 `assetdb.search_folder_not_mounted` warning。

返回 / 排序默认规则：

- 返回 `string[]` guid，与 Unity-like API 对齐；未命中返回空数组。
- 结果不包含重复 guid；同一 asset 被多个 token/folder 命中也只返回一次。
- 排序使用 normalized current asset path 的 ordinal ignore-case sort key，再以 asset guid 升序打破平局。
- `GUIDToAssetPath` 找不到 guid 返回空字符串；不会因为 guid 形状可反推而构造路径。
- filter 语法错误返回空数组并写 report，不修改当前 index。

### 14.16 默认 SerializedProperty path 与 Excel cell mapping

SerializedProperty path 默认遵循 Unity 风格，并且能映射回 Excel cell/range。

默认 path 规则：

```text
hp
range.min
range.max
skills.Array.data[0]
waves.Array.data[2].enemy_id
```

canonical grammar：

```text
property_path := property_node ('.' property_node)*
property_node := field_segment | 'Array.data[' non_negative_decimal_index ']'
field_segment := [a-z_][a-z0-9_]*
non_negative_decimal_index := '0' | [1-9][0-9]*
```

规则：

- 普通字段使用 schema field path。
- schema field path segment 默认使用 lower snake case；`.`、`[`、`]`、`/`、`\`、空白、控制字符和保留 segment `Array` / `data` 禁止出现在 segment 中。需要从旧 proto/C# 名称兼容时，必须通过 alias 或 generated C# member name 兼容，不能改变 canonical property path。
- `Array.data[index]` 是唯一合法 array/list index 表达；不接受 `skills[0]`、`skills.0`、`skills.data[0]`、负数、前导零或 key-based path 作为 canonical property path。
- `property_path` 是 descriptor/codegen/editor/debug 的稳定路径，不等于 C# PascalCase member name、Excel header display name、中文名、localized label 或 visible key。
- field path、generated `SerializedProperty` path 和 property path id 必须由 canonical descriptor 生成；schema source 中两个字段、子字段、variant payload 或 generated alias 解析到同一 canonical property path 时产生 `schema.property_path_conflict`。
- C# member rename 只能改变 `codegen_hash` / compatibility wrapper，不能改变 existing `property_path`。如果项目确实要改变 property path，必须提供 migration/alias，并确保旧 workbook、Undo、conflict artifact 和 custom editor binding 可迁移。
- simple struct 的 `expanded_columns` 使用 `parent.child`。
- repeated/list 使用 Unity 风格 `Array.data[index]`。
- object reference 字段通过 `objectReferenceValue` 读写，内部写 row guid/local id，不以显示 key 作为身份。
- `FindProperty("field")` 和 `FindProperty("parent.child")` 必须稳定，不受 Excel header display name 变化影响。
- `GetIterator()` 的遍历顺序默认按 schema field order，再按 array index。

Excel cell/range mapping：

- 单 cell scalar 映射到一个 cell。
- simple struct `single_cell` 的父 property 映射到一个 cell；子 property 是 virtual property，写入时重新序列化整个 cell。
- simple struct `expanded_columns` 的父 property 映射到 cell range；子 property 映射到各自 cell。
- repeated scalar `single_cell` 映射到一个 cell；修改任意元素会重新序列化该 cell。
- repeated horizontal columns 映射到连续 cell range。
- child table / sub-asset 策略映射到子表行集合，不允许用一个 cell 假装完成。

#### 14.16.1 默认 single-cell value codec

single-cell simple struct 和 repeated scalar 必须有 schema 声明的 codec。未声明时默认使用 ExcelDB built-in codec，不允许靠 `ToString()`、当前区域设置或随手 `Split(';')` 解析。

simple struct 默认 codec：

```text
codec: exceldb.key_value_pairs.v1
format: subfield=value;subfield=value
example: min=1;max=10
example: x=1.5;y=-2
```

默认规则：

- subfield 顺序按 schema subfield order。
- subfield 名使用 schema subfield path，不使用 display name。
- writer 必须输出 canonical form：无多余空格，pair 之间使用 `;`，key/value 使用 `=`。
- number 使用 invariant culture，decimal separator 固定为 `.`。
- bool 使用 `true` / `false`。
- enum 使用 schema export token；没有 explicit export token 时使用 enum stable name。
- string 使用 JSON string literal，例如 `name="fire;ice"`；writer 对 string 总是加引号。
- null 使用 `null`，仅 nullable subfield 允许。
- object/internal reference 默认写 visible key/path token，metadata/companion data 仍保存 identity；如果 codec 无法保存 companion identity，schema 必须禁止该 reference 放入 single-cell struct。
- 未知 subfield 默认 error；只有 descriptor 显式声明 `preserve_unknown_pairs=true` 的 open struct 才允许作为 authoring/debug preservation payload 保留。
- 缺少 optional/default subfield 时可 materialize default；缺少 required subfield 是 validation error。

`exceldb.key_value_pairs.v1` 默认 grammar：

```text
cell        := empty | pair (';' pair)*
pair        := field_key '=' value_token
field_key   := schema_field_path_segment ('.' schema_field_path_segment)*
value_token := quoted_json_string | bare_token
bare_token  := 1*bare_char
bare_char   := any character except ';' '=' CR LF
```

key/value codec 细则：

- parser 在 pair、key、`=`、value 外侧允许 ASCII space / tab；writer 一律去掉这些空白。
- `quoted_json_string` 使用 JSON string literal 语法和转义；解析后得到 UTF-8 string token，再交给目标 subfield 的 value shape parser。
- bare token 不能包含 `;`、`=`、换行或控制字符；需要这些字符的 string/reference display token 必须写成 JSON string literal。
- literal `null` 表示 explicit null，只在 nullable subfield 允许；字符串 `"null"` 必须写成 quoted JSON string。
- 空 value，例如 `min=`，默认是 parse error；要表达 empty string 必须写 `""`，要表达 missing 必须省略该 pair。
- trailing delimiter、empty pair、重复 subfield key、unknown key 在 closed struct 中、key 无法映射到 subfield id，都产生 `cell.parse_failed` 或对应 validation diagnostic；不能按最后一个值覆盖。
- open struct 允许 unknown pair 时，descriptor 必须声明 `preserve_unknown_pairs=true`；unknown pair 进入 authoring/debug preservation payload，但不进入 runtime/export canonical value。没有该声明时 unknown pair 默认 error，避免 writeback 丢数据。
- writer 按 schema subfield order 输出已知 subfield；missing optional/default subfield 默认省略，explicit null 输出 `key=null`，explicit empty string 输出 `key=""`。

repeated scalar single-cell 默认 codec：

```text
codec: exceldb.list.v1
format: value;value;value
example: fireball;ice_nova
example: "small;fast";"large"
```

默认规则：

- 空 cell 表示 empty list，不表示 null。
- element string 使用 JSON string literal，writer 对 string 总是加引号。
- element enum/number/bool/reference 使用对应 scalar canonical token。
- list element 默认无 stable identity；insert/delete/reorder 与同 array 其他修改按 14.13.1 处理。
- 如果 schema 声明 stable element id subfield，则不得使用 simple scalar list codec，应使用 expanded struct/list 或 child table。

`exceldb.list.v1` 默认 grammar：

```text
cell          := empty | element (';' element)*
element       := quoted_json_string | bare_token
bare_token    := 1*bare_char
bare_char     := any character except ';' CR LF
```

list codec 细则：

- parser 在 element 外侧允许 ASCII space / tab；writer 一律去掉这些空白。
- trailing delimiter、empty element、控制字符、未闭合 JSON string 都产生 `cell.parse_failed`；要表达 empty string element 必须写 `""`。
- element literal `null` 只在 nullable element schema 允许；普通 string `"null"` 必须 quoted。
- writer 按当前 list order 输出元素；unordered set-like 语义必须使用 map/child table 或 schema 声明 canonical sort policy，不能由 list codec 临时排序。
- reference element 遵循 reference policy：visible token 只是展示/输入，identity 仍来自 metadata/companion data 或 resolver；无法保存 per-element companion identity 的 reference list 不允许进入 `exceldb.list.v1`。

parse / writeback 默认规则：

- parser 可以接受 pair 两侧空白，但 normalized value 和 writer 输出必须 canonical。
- parse error 产生 `cell.parse_failed` diagnostic，并定位到 cell；能定位到 pair/subfield 时同时给 field path。
- writer 不保留用户在 single cell 内的空格和 pair 顺序；保留这些格式需要 schema opt-in custom codec。
- custom codec 必须声明 stable codec id、version、canonical writer、parser、merge granularity、examples、diagnostic code。
- `merge_granularity=cell` 是默认；即使 codec 能解析子字段，也不自动做 child-level merge。
- `merge_granularity=child` 只有 schema 显式声明且 codec 能 lossless roundtrip 时才允许。

#### 14.16.2 默认 child table / sub-asset 协议

复杂 repeated、embedded message、owned child object、行为树节点、关卡波次、掉落项默认不写成 JSON cell。默认使用 child table，并为每个元素保存稳定 element identity。

默认选择矩阵：

| value shape | 默认 Excel 表达 |
| --- | --- |
| scalar | single cell |
| enum | single cell + dropdown |
| simple struct | `single_cell` 或 `expanded_columns`，由 schema 声明 |
| repeated scalar small/fixed | schema 可选 `single_cell` 或 horizontal columns |
| repeated scalar variable | `single_cell` list codec；需要 element identity 时改 child table |
| repeated simple struct variable | child table |
| embedded message | expanded columns；字段多或可变时 child table |
| owned child object | child table |
| polymorphic payload / behavior tree node | child table + type discriminator |

child table descriptor 默认字段：

```text
child table id
parent table id
parent field id
schema field path
child runtime type
child asset kind: embedded | sub_asset
owner policy: owned
order policy: ordered | unordered
element identity policy
owner_event_policy: child_only | owner_summary | owner_property_changed
fields[]
```

child row metadata 默认字段：

```text
owner workbook guid
owner table id
owner row guid/local id
parent field id
element guid/local id
order index
element key optional
row revision
source hash / cell range hash
```

Excel 表达默认规则：

- child table 默认是独立 sheet，sheet name 默认为 `{parent_schema_name}.{field_path}`，例如 `SkillConfig.effects`。
- child sheet 仍使用第 1-7 行 schema-owned structure region，第 8 行以后是 child data region。
- child sheet 必须有可见 owner display column，方便策划知道属于哪个父对象。
- owner display column 只是展示；真实 owner identity 存在 metadata/hidden companion data。
- child row 的 row number 不是 identity；排序由 `order index` 决定。
- child sheet 可以按 owner display + order index 排序；排序不改变 identity。
- orphan child row 默认 error；如果 owner hard missing 或 ownership 断裂，convert blocker。

SerializedProperty 默认映射：

- parent property path 使用 `field.Array.data[index]`。
- index 按 `order index` 排序后的可见顺序计算。
- `GetArrayElementAtIndex(i)` 映射到 child row 的 element guid/local id。
- insert 创建 editor draft child row 和 temporary element identity。
- delete 标记 child row deletion；`SaveAssets` 后提交。
- reorder 只更新 `order index`，不改变 element identity。
- child row 字段修改产生 child field property diff；不重写整个 parent cell。

sub-asset 默认规则：

- `embedded` child 只能通过 parent `SerializedProperty` 访问，不进入常规 `FindAssets`。
- `sub_asset` child 可以被 `LoadAllAssetsAtPath(parentPath)` 返回；顺序为 parent、schema field order、order index。
- sub-asset path 默认是 `{parent_asset_path}/{field_path}/{escaped_element_key_or_id}`。
- sub-asset identity 使用 owner asset identity + parent field id + element guid/local id 派生。
- key rename / reorder 不改变 sub-asset identity，只触发 moved/renamed 或 property_changed。

ownership / delete 默认规则：

- child table 默认 ownership 为 owned；父 asset 删除时 child rows cascade 删除。
- child row 不能被两个 parent 同时拥有。
- 移动 child row 到另一个 parent 默认需要 explicit move operation；直接改 owner display 不改变 owner identity。
- child row missing owner、duplicate element identity、order index 冲突都进入 diagnostic。
- order index 冲突可自动按 stable element identity 重新编号，但必须写 report。

runtime / converted bytes 默认规则：

- converted bytes 中 parent object 保存 child range index，不解析 Excel child sheet。
- child objects 按 parent slot + order index contiguous 写入，便于 no-GC traversal。
- dependency graph 包含 parent -> owned child edge；child reference 变化可以触发 parent dependency_changed。
- hot reload patch 优先按 element guid/local id patch child object；insert/delete/reorder 产生对应 ChangeSet。
- `owner_event_policy=child_only` 时只发布 child identity 的 added/removed/moved/property/dependency event；适合业务直接订阅 child sub-asset。
- `owner_event_policy=owner_summary` 时 child event 仍以 child identity 为主，同时 owner 发布 `dependency_changed` 或 source_summary，适合缓存只关心 parent 依赖闭包。
- `owner_event_policy=owner_property_changed` 时 child insert/delete/reorder/value change 还会让 owner 的 child field 进入 `property_changed`；适合 owner getter 暴露 baked aggregate/range view 的常见配置表。
- 完整目标版默认：embedded child 使用 `owner_property_changed`，findable sub_asset 使用 `child_only` + 必要 `dependency_changed`；schema 可以覆盖，但必须进入 descriptor、ChangeSet digest 和 hot reload tests。

#### 14.16.3 默认高级 schema capability 范围

完整目标版默认支持的高级能力分属不同 descriptor family。

`ValueShapeDescriptor` 只表达字段值的物理布局、canonical value、property path、merge granularity 和 runtime representation。

reference family、label/search descriptor 和 preset expansion 是一等 schema capability，但不是普通 value codec。

value shape：

```text
scalar
enum
simple struct single_cell
simple struct expanded_columns
repeated scalar single_cell
repeated scalar horizontal columns
child table for repeated complex / owned child
map via child table
oneof / union with stable variant id
schema-discriminated polymorphic payload
gameplay expression / condition
weighted selection
curve
```

reference family / localization contract：

```text
internal object reference
UnityResourceRef
LocalizedTextRef
```

AssetDatabase search / label capability：

```text
label descriptor
```

schema authoring preset：

```text
game table archetype / preset expansion
```

完整目标版不提供隐式兜底、必须显式 opt-in 的扩展表达：

```text
arbitrary JSON cell
multi-dimensional array
polymorphic payload without schema discriminator
external localization provider / advanced formatter grammar
```

默认 canonical / 替代表达：

- map 使用 child table 表达为 key/value child rows；key field 必须唯一。
- oneof / union 使用 explicit type discriminator + value columns；每个 variant 必须有 stable variant id。
- polymorphic payload 使用 child table + type discriminator + schema group/ref；不能把任意 JSON 当 payload。
- arbitrary JSON cell 只允许 legacy/custom codec opt-in；默认不参与 property-level merge，convert 前必须通过 validator。
- multi-dimensional array 默认不支持；需要时建模成 child table，并显式声明 row/column index field。

高级 value shape descriptor 默认字段：

```text
value_shape_kind
shape_id: package-qualified stable descriptor id
shape_version
owner_table_id
owner_field_id
layout_strategy
runtime_representation
merge_granularity
export_policy
validators[]
```

map descriptor 默认字段：

```text
map_shape_id: package-qualified stable descriptor id
map_shape_version
child_table_id
key_field_id
value_field_ids[]
key_normalizer_id
key_compare_policy
allow_missing_value
allow_duplicate_key: false
order_policy: ordered | unordered
```

map 默认规则：

- map 默认不是 Excel 单 cell 字典文本，而是 child table rows。每行一个 key/value entry，并拥有 element guid/local id。
- key field 必须是 scalar/string/integer/enum，或声明 deterministic key codec 的 simple struct；float、formula-only、blob、non-deterministic custom codec 不能作为 map key。
- `key_compare_policy` 默认与 table key 使用相同 canonical compare；重复 key 产生 `validation.map_key_duplicate`。
- `order_policy=unordered` 时 runtime 语义不受 Excel 行顺序影响；converted bytes 可以按 canonical key 排序。`ordered` 时 order index 进入 source hash 和 hot reload diff。
- map key 变化默认等价于删除旧 entry + 新增新 entry；不会把旧 key identity 偷偷改成新 key。
- `SerializedProperty` path 默认使用 `field.Array.data[index]` 展示；需要按 key 查找的 editor/drawer 可以提供 map drawer，但写入仍落到 child row identity。

oneof / union descriptor 默认字段：

```text
union_shape_id: package-qualified stable descriptor id
union_shape_version
discriminator_field_id
variants[]
closed: true
unknown_variant_policy: error
layout_strategy: shared_columns | variant_column_groups | child_table
```

variant descriptor 默认字段：

```text
variant_id
stable_name
display_name
aliases[]
deprecated
payload_field_ids[]
runtime_type_id optional
default_payload
```

oneof / union 默认规则：

- discriminator 是事实源，必须保存 stable `variant_id`；display name / token / C# type name 不是身份。
- `closed=true` 默认要求 variant_id 必须在 schema 中存在；未知 variant 产生 `validation.variant_unknown`。
- 当前 variant 之外的 payload cell 默认必须为空或 metadata 标记为 stale payload；非空且会影响导入/导出时产生 `validation.variant_payload_invalid`。
- 切换 variant 默认清空旧 variant payload，并按新 variant default materialize；如果旧 payload 有数据，属于 data-loss-risk edit，需要 Undo/confirmation。
- `shared_columns` 只允许所有 variant 的 payload field id 不冲突且 value shape compatible；否则使用 `variant_column_groups` 或 child table。
- `variant_column_groups` 中每个 variant 的列组由 stable variant id + payload field id 标记，layout refresh 不按显示名猜。
- 行为树节点、效果列表、条件组这类 polymorphic payload 默认用 child table + discriminator；父表只保存 child range，不在父 row 里塞 JSON。

runtime / converted bytes 默认规则：

- map 在 bytes 中保存 entry range；unordered map 可以额外生成 key index，ordered map 同时保存 order index。
- union 在 object record 中保存 variant id 和 payload range/field slice；getter 先读 variant id，再暴露对应 generated typed payload view。
- variant id、map key canonical bytes、child element identity 都进入 source_hash；display token、header text 不进入。
- hot reload patch 对 map entry 优先按 element identity，其次按 map key 做 stable match；二者冲突时进入 conflict/report，不按行号猜。
- union variant 未变时 patch payload field；variant 改变时发布 property_changed，必要时发布 recreated 或 typed payload changed event，由 patch plan 决定。

compatibility 默认规则：

- 把 JSON cell 迁移为 child table 必须有 migration；不能自动猜字段。
- 新增 oneof variant 等价于 enum/variant 新增，可 layout refresh。
- 删除正在使用的 variant 是 convert blocker。
- variant id 改变但 name 相同，按删除旧 variant + 新增 variant 处理，需要 migration。
- map key field 改变、key normalizer 改变或 compare policy 收紧，需要扫描现有 child rows；出现重复或不可逆 key 时至少 `import_allowed_convert_blocked`。
- unordered map 改成 ordered 会让 order index 成为语义，需要 migration 或 compatibility report；ordered 改 unordered 会改变 source hash 规则，但不应丢数据。
- `shared_columns` 与 `variant_column_groups` / child table 互换时必须证明 payload field id 可逆映射；否则 migration required。
- arbitrary JSON cell 升级为 schema shape 必须由 migration 明确字段映射、失败策略和 data_loss_risk；不允许 importer 从 JSON key 名自动生成 field id。

Apply 规则：

- `SerializedObject` 创建时记录 target object revision、source revision 和 property-to-cell mapping revision。
- `ApplyModifiedProperties()` 必须检查这些 revision；如果外部 refresh 改变了目标 property，进入 conflict。
- `ApplyModifiedProperties()` 只产生 property diff，不直接写 Excel；写回由 dirty/undo/save 生命周期处理。
- property diff 必须以 field id 和 row guid/local id 为主，不以 header 文本或 row number 为主。

#### 14.16.4 默认 gameplay expression / condition / curve 协议

本节是完整目标版默认协议，不是未来扩展占位。expression / curve 可以被项目 profile 或 host capability 禁用，但实现必须完整存在；禁用时只能产生稳定 diagnostic / read-only / build gate，不能把表达式或曲线退化成普通 string、Excel formula、JSON 或业务代码临时解释。

游戏公式、条件表达式、AI 条件、解锁条件和数值曲线不能默认当普通字符串、Excel formula 或任意脚本执行。它们必须是 schema 声明的 `Expression<T>` / `ConditionExpression` / `CurveExpression` value shape，在 import/convert 阶段解析、绑定、类型检查，并在 runtime 中以预编译表达式计划读取。

ExpressionDescriptor 默认字段：

```text
expression_id
expression_version
result_type
grammar_id
grammar_version
symbol_table_id
allowed_symbols[]
allowed_functions[]
constant_fold_policy
runtime_representation: ast | bytecode | generated_delegate
max_ast_nodes
max_bytecode_length
max_eval_steps
allocation_policy
deterministic: true
```

ExpressionFunctionDescriptor 默认字段：

```text
function_id
function_version
name
argument_types[]
return_type
deterministic
pure
runtime_cost
host_requirement
```

Excel 表达默认规则：

- 短表达式默认可用 single cell text，但其语义来自 expression grammar，不来自 Excel formula。
- 复杂条件组、行为树条件、带多段曲线的表达式默认使用 child table 或自定义 editor；不能把任意脚本塞进一个 cell。
- Excel formula cell 仍按 14.6.5 处理；它不是 gameplay expression。需要游戏公式时，字段 value shape 必须声明为 expression。
- 表头注释必须展示 result type、允许 symbol、允许 function、示例表达式和 budget 摘要。

parse / bind / normalize 默认规则：

- import 先把 expression text 解析为 AST；parse 失败产生 `expression.parse_failed`。
- symbol 必须来自 schema 声明的 symbol table，例如 `caster.attack`、`target.defense`、`level`；未知 symbol 产生 `expression.symbol_unknown`。
- 函数调用必须命中 generated registry 中的 `ExpressionFunctionDescriptor`；未声明函数产生 `expression.forbidden_call`。
- type checker 必须验证参数类型、返回类型、nullable/default 传播和 enum/value shape 兼容；失败产生 `expression.type_mismatch`。
- canonical expression bytes 使用 AST 结构、symbol id、function id/version、literal canonical value 和 operator id；不保留空白、括号风格或注释。
- source_hash 使用 canonical expression bytes；只改空白或格式化不触发 runtime property_changed。

determinism / safety 默认规则：

- Release/convert 中 expression function 必须 `deterministic=true` 且 `pure=true`。
- 默认禁止访问当前时间、随机数、文件系统、网络、UnityEngine scene state、全局 mutable singleton 和反射。
- 需要随机的玩法逻辑应把 random seed / random stream 作为显式 runtime input symbol，不在表达式中读全局随机。
- host requirement 不可用的 expression function 在 convert/build 下是 blocker；不能把它降级成运行时再解析。
- expression grammar/version/function registry 进入 `schema_hash` 或 `converter_registry_hash`；影响 runtime bytecode 时也进入 bytes cache key。

runtime / no-GC 默认规则：

- converted bytes 默认保存 expression bytecode 或 canonical AST section；runtime open 时绑定 symbol slot、function pointer/table 和 constant pool。
- 稳定热路径 evaluation 不解析字符串、不查字典、不反射、不分配；调用方传入 evaluation context struct / span。
- expression evaluation 若超过 `max_eval_steps` 或 runtime budget，产生 `expression.budget_exceeded`；Release 默认不允许把超预算表达式写入 bytes。
- generated runtime API 可以暴露 `EvaluateDamage(in DamageContext ctx)` / `EvaluateCondition(in ConditionContext ctx)` 这类 typed wrapper；通用解释器 API 必须标明是否 no-GC。
- hot reload 时 expression bytecode 改变产生 property_changed；如果 result type、symbol binding 或 function registry 不兼容，按 patch plan recreated / switch failed 处理。

CurveExpression 默认规则：

- 简单曲线默认建模为 repeated point child table：`x`、`y`、interpolation、optional tangent/weight。
- x key 必须按 schema policy 单调或唯一；违反时使用 validation diagnostic，不能在 runtime 排序猜测。
- curve bake 阶段生成 deterministic segment table；runtime sample 只做数组/range 查找，不分配。
- 如果项目需要 Unity `AnimationCurve` 互操作，由 Unity adapter drawer 做展示和编辑；核心 schema 仍以 curve point descriptor 为事实源。

CurveDescriptor 默认字段：

```text
curve_id
curve_version
owner_table_id
owner_field_id
point_child_table_id
x_field_id
y_field_id
interpolation_field_id optional
in_tangent_field_id optional
out_tangent_field_id optional
weight_field_id optional
x_numeric_type: int32 | int64 | fixed_decimal | float32 | float64
y_numeric_type: int32 | int64 | fixed_decimal | float32 | float64
monotonic_policy: strictly_increasing | non_decreasing | unique_unsorted_authoring
duplicate_x_policy: error | merge_same_y
default_interpolation: step | linear | cubic_hermite
domain_policy: error | clamp | extrapolate
extrapolation_before: constant | linear | cubic | error
extrapolation_after: constant | linear | cubic | error
bake_policy: segments | fixed_lut
lut_sample_count optional
lut_domain_min optional
lut_domain_max optional
precision_policy
unit optional
```

Excel authoring 默认规则：

- 曲线点默认使用 child table；父 row 只持有 child range，不把 `x:y;x:y`、JSON 或 Unity `AnimationCurve` 序列化文本塞进单 cell。
- point child table 至少包含 owner display、`x`、`y`；需要逐点插值时包含 interpolation；需要 Hermite/cubic 时包含 in/out tangent；需要 weighted tangent 时包含 weight。
- 每个曲线点都有 element guid/local id；移动行、按 x 排序或在 drawer 中拖点不改变 point identity。
- Excel 行顺序只是 authoring display；曲线语义顺序由 canonical x order 决定，除非 schema 明确声明 `monotonic_policy=unique_unsorted_authoring` 并提供 deterministic sort。
- 表头注释必须说明 x/y 单位、x 单调/唯一策略、默认插值、外推策略、是否需要 tangent、runtime bake 策略和超出 domain 的行为。
- horizontal columns 或 single-cell curve 只允许作为 schema 显式 opt-in 的 legacy/custom codec；默认不享受 point-level merge、stable point identity 或 no-GC curve drawer guarantee。

validation 默认规则：

- x/y/tangent 按普通 numeric value shape 解析；NaN/Infinity、scale 不匹配、超出范围或非法 fixed decimal 产生现有 cell/validation diagnostic。
- `strictly_increasing` 要求 canonical x 按语义排序后严格递增；`non_decreasing` 允许相同 x 但必须由 interpolation policy 证明可解释；默认推荐 `strictly_increasing`。
- duplicate x 默认 error；`merge_same_y` 只在 y、interpolation、tangent 全部 canonical 相同且 point identity 可保留时允许折叠为同一语义点，仍在 report 中记录 duplicate normalization。
- cubic/hermite 插值缺 tangent、tangent 类型不兼容、weighted tangent 缺 weight、LUT sample count 非法或 LUT domain 与点 domain 不一致，默认产生 `schema.value_shape_invalid` 或 validation error，不能 runtime 猜默认。
- 点数为 0 时，`allow_empty_curve` 必须由 schema 显式声明；否则是 validation error。点数为 1 时只能使用 constant/clamp 语义，linear/cubic 外推需要 schema 明确允许。
- 如果 x 字段来自 formula/cached value，仍按 formula policy 验证；Convert/CI 不能依赖 Excel 打开后重算曲线点。

canonical / hash 默认规则：

- canonical curve bytes 由 CurveDescriptor id/version、domain/extrapolation/bake policy、按 canonical x order 排序的 point records、每个 point 的 element identity、x/y/interpolation/tangent/weight canonical bytes 组成。
- Excel 行号、display order、图表样式、Unity curve editor handle color、预览采样图不进入 source_hash。
- 只改变点的 Excel 行顺序且 canonical x order 不变，不触发 runtime property_changed；改变 x/y/tangent/interpolation/domain/bake policy 才改变 curve source contribution。
- `fixed_lut` 的 baked table bytes 进入 bytes_hash；只改 debug preview/LUT 图表不改变 bytes。
- 使用 float 曲线时 canonical bytes 使用 14.6.5 的 float canonicalization；需要跨平台完全一致的玩法曲线默认应使用 fixed_decimal 或 convert-time baked LUT。

SerializedProperty / editor 默认规则：

- 曲线字段在 `SerializedProperty` 中表现为 array-like child table：`curve_points.Array.data[index].x`、`.y`、`.interpolation`。
- index 由 canonical x order 或 descriptor 声明的 display order 生成；写入仍通过 child row element identity 定位，不能用 index 当身份。
- generic drawer 可以显示 point table；Unity adapter 可以显示 AnimationCurve-like editor，但 Apply 时必须转成 child row create/delete/reorder/value diff。
- drawer 拖点改变 x/y 是普通 authoring value edit，进入 Undo、dirty、validation、SaveAssets；不能直接写 runtime baked segment 或 Unity proxy serialized curve。

runtime / converted bytes 默认规则：

- converted bytes 默认保存 curve descriptor handle、point/segment range、domain、extrapolation mode 和 baked segment table或 LUT。
- runtime sample API 必须是 no-GC：只在 contiguous segment/LUT range 中 binary search 或 index lookup，不构造临时点列表、不排序、不分配 delegate。
- 超出 domain 的行为按 `domain_policy` / extrapolation 字段执行；不能由调用方或 runtime 临时决定 clamp/linear/error。
- hot reload 优先按 point element identity patch；新增/删除点、x 改变导致 segment table 变化时发布 curve field `property_changed`，必要时同时发布 dependency/source summary。
- 如果 curve descriptor、numeric representation、interpolation algorithm 或 baked representation 与当前 generated accessor 不兼容，source switch/hot reload 不能挂临时 adapter；必须走 recreated 或 switch failed。

Unity `AnimationCurve` 互操作默认规则：

- Unity adapter drawer 可以把 CurveDescriptor 投影为 `AnimationCurve` 供编辑，但 `AnimationCurve` 不是核心存盘格式。
- Unity tangent mode、weighted mode、wrap mode 只有在 CurveDescriptor 有对应字段时才可写回；无法表示的 Unity editor 状态必须降级显示或 report，不能丢弃后保存。
- 从 Unity `AnimationCurve` 粘贴/导入曲线时，adapter 必须生成 child point diff 和 normalization report；重复 x、不可表示 tangent、wrap mode 不兼容按 validation / auto-fix proposal 处理。
- Release runtime 不依赖 UnityEngine.AnimationCurve；Unity runtime 若要使用 AnimationCurve projection，只能作为 allocating/projection API，不作为 no-GC 主路径。

compatibility 默认规则：

- grammar id/version、function signature、symbol type 改变属于 semantic diff；必须重新 parse/typecheck 所有表达式字段。
- 只改表达式空白、括号冗余或格式化是 display diff，不改变 canonical value。
- function 实现修 bug 但 canonical output 不变，可以只 bump package version；改变 evaluation result 必须 bump function version 并影响 cache key。
- 从普通 string/Excel formula 迁移到 Expression<T> 必须有 migration；不能把旧字符串按新 grammar 自动解释后静默保存。
- curve numeric type、interpolation algorithm、domain/extrapolation policy、bake policy 或 point table layout 改变是 semantic diff；必须重新 validate/bake 所有曲线字段。
- curve child table 与 single-cell/JSON/Unity AnimationCurve serialized text 互转需要 migration，且必须证明 point identity、x/y/tangent/interpolation 可逆；否则 migration_required 或 data_loss confirmation。
- 只改 curve display color、preview chart、drawer zoom 或 helper graph 是 projection/layout diff，不改变 source_hash。

#### 14.16.5 默认 weighted selection / drop table 协议

本节是完整目标版默认协议，不是未来扩展占位。weighted selection 可以被项目 profile 或 host capability 禁用，但实现必须完整存在；禁用时只能产生稳定 diagnostic / read-only / build gate，不能由 importer、runtime 或业务代码临时解释任意掉落表结构。

掉落池、奖励池、随机事件池和 AI 随机选择不能默认由业务代码现场解释任意表结构。schema 必须声明 weighted selection value shape，convert 阶段烘焙稳定选择结构，runtime 只用显式传入的随机流做 no-GC 选择。

WeightedSelectionDescriptor 默认字段：

```text
selection_id
selection_version
owner_table_id
owner_field_id
entry_child_table_id
entry_identity_policy
selection_mode: relative_weight | probability | first_match_weighted
weight_field_id
probability_field_id optional
condition_field_id optional
result_field_ids[]
quantity_field_id optional
allow_empty: false
zero_weight_policy: exclude | keep_unselectable | error
weight_numeric_type: uint32 | uint64 | fixed_decimal
sum_overflow_policy: error
runtime_algorithm: cumulative | alias_table
```

Excel 表达默认规则：

- 掉落项默认使用 child table，每个 entry 有 element guid/local id；不把 `item:weight;item:weight` 写成普通 string。
- `relative_weight` 使用非负整数或 fixed decimal 权重；默认禁止 float/double 权重，避免不同平台累加误差。
- `probability` 使用 fixed decimal 概率，并声明 scale；required total 默认必须等于 1.0 或 100%，误差容忍必须由 schema 显式声明。
- 条件掉落使用 `condition_field_id` 引用 `ConditionExpression`；条件表达式先按 14.16.4 解析/绑定/类型检查。
- quantity 可以是整数范围、fixed value 或 `Expression<int>`；默认不能在选择算法里访问全局随机。
- 表头注释必须说明 selection mode、权重单位、是否允许 0 权重、是否允许空池、概率总和要求和随机输入来源。

validation 默认规则：

- 权重缺失、负数、非有限数、超过声明范围或 fixed decimal scale 不匹配，产生 `validation.weight_invalid`。
- relative weight 在过滤后总和为 0，且 `allow_empty=false`，产生 `validation.weight_sum_invalid` 或 `validation.random_table_empty`。
- probability 模式概率总和不满足 schema 声明，产生 `validation.probability_sum_invalid`。
- `zero_weight_policy=error` 时 0 权重 entry 是 validation error；默认 `exclude` 表示 entry 保留在 authoring 表中但不进入 runtime selectable range。
- condition 字段是 editor-only 或被当前 export view strip 时，不能用于 runtime selection；这是 `schema.export_policy_invalid`。
- result reference、UnityResourceRef、LocalizedTextRef 等字段仍按各自 reference policy validation；weighted table validation 不替代引用校验。

runtime / converted bytes 默认规则：

- converted bytes 默认保存 selectable entry range、entry identity、result payload range、condition bytecode handle 和 baked selection table。
- `runtime_algorithm=cumulative` 保存 monotonic cumulative weights；`alias_table` 保存 alias/probability arrays。算法选择进入 schema/convert profile，不能由 runtime 临时切换。
- runtime selection API 必须接收显式 RNG/random stream 参数，例如 `Select(in WeightedSelectionContext ctx, ref RandomStream rng, out Result result)`；禁止读取全局随机、当前时间或 UnityEngine.Random。
- runtime 选择不分配，不构造临时候选列表；条件过滤需要预分配 bitset/range 或由调用方提供 buffer。
- hot reload 时 entry weight、condition、result payload 改变产生 property_changed / dependency_changed；entry identity 保持时业务缓存可增量更新 baked table。
- 同一 source、同一 explicit random stream state、同一 selection table 和同一 condition context 必须得到同一选择结果，跨平台 deterministic。

hash / manifest 默认规则：

- entry element identity、condition canonical bytes、weight canonical value、result payload identity 和 selection descriptor id/version 进入 source_hash。
- Excel 行顺序只在 schema 声明 ordered selection 或 tie-break order 时进入 source_hash；默认 relative weighted selection 按 entry identity / stable order bake。
- baked selection table bytes 进入 bytes_hash；算法版本或 numeric representation 改变进入 schema_hash / converter_registry_hash。
- debug report 可以输出 normalized chance preview，但 preview 不参与 runtime selection identity。

compatibility 默认规则：

- 改变 selection mode、weight numeric type、runtime algorithm 或 sum policy 是 semantic diff，需要重新验证所有 entry。
- relative_weight 和 probability 互转需要 migration 或 explicit compatibility rule；不能按当前显示百分比自动猜。
- 删除有引用 payload 的 entry 是 ordinary child row delete；如果外部系统依赖 entry identity，需要 ChangeSet 发布 removed child/dependency_changed。
- 修改权重但 canonical baked table shape 兼容时可 patch；修改 result payload value shape 或 condition symbol binding 不兼容时按 patch plan recreated / switch failed 处理。

#### 14.16.6 默认游戏配置表 archetype / preset 协议

游戏项目里常见的 `id`、名称、描述、备注、分组、标签、启用状态、审核状态、客户端/服务端导出、资源引用、条件、掉落、曲线等列，不能靠 Excel 列名约定或导入器硬编码解释。它们必须来自 schema descriptor；preset 只能是生成 descriptor 的便捷写法，不能成为第二套隐式 schema。

table archetype 默认集合：

```text
config_asset_table
lookup_table
localization_table
weighted_selection_table
curve_table
polymorphic_graph_table
child_table
editor_helper_table
```

common field preset 默认集合：

```text
key
display_name
description
category
tags
enabled
review_state
owner
authoring_note
export_filter
sort_order
localized_text_ref
unity_resource_ref
condition_expression
weighted_selection
curve_points
```

preset expansion 默认字段：

```text
preset_id
preset_version
owner_table_id
expanded_field_ids[]
field_paths[]
value_shapes[]
export_policies[]
validation_policies[]
layout_groups[]
editor_capabilities[]
runtime_accessors[]
```

默认规则：

- preset expansion 必须在 canonical descriptor 阶段完全展开为普通 table/field/enum/struct/reference/validator/editor descriptors；importer、converter、runtime 和 `SerializedProperty` 不识别“魔法列名”。
- preset 不能自动分配稳定 id；`table_id`、`field_id`、`enum_value_id`、`child_table_id`、`variant_id` 仍必须由 schema source 或 id allocator metadata 明确给出。
- `key` preset 只生成 key descriptor、path pattern、normalizer 和 duplicate/rename policy；不能假设第一列就是 key。
- `display_name` preset 只影响 `Object.name`、搜索展示和 Excel authoring display；除非同时被 schema 声明为 key field，否则 display name 改动不触发 asset path identity change。
- `description`、`authoring_note`、`owner`、`review_state` 默认是 editor/export policy 字段；是否进入 converted bytes、source_hash 或 review gate 由 export policy / gate policy 明确决定。
- 自由备注如果只是策划临时信息，默认应进入 schema 声明的 ignored/helper/freeform 区域；如果备注会参与审核、导出、运行时或 merge 决策，就必须是普通 schema field，不能藏在 unknown column。
- `category` / `tags` preset 必须声明 enum/string normalizer、大小写/空白策略、是否允许多值、是否导出以及是否参与 `FindAssets("l:")`；没有 schema label descriptor 时 `l:` 查询不从列名猜标签。
- `enabled` preset 只表示 schema 声明的 row lifecycle field；它的默认值、可空性、导出过滤、验证范围和 hot reload 事件都由 descriptor 决定。
- 如果 `enabled` 被声明为 `row_export_filter`，disabled row 仍是 authoring asset，可被 editor 查到并保存；在当前 export view 中被 strip 的目标如果被 hard reference 指向，按 stripped target/reference policy 报告，不能在 runtime 静默返回 null。
- `export_filter` preset 只生成 ExportViewDescriptor 输入，例如 client/server/editor/development/release；策划不能通过手改第 4 行导出端来改变 schema fact。
- `sort_order` 只有在 schema 声明 row order 有语义时进入 source_hash/runtime bytes；普通 Excel 行顺序只是 authoring display，不影响 runtime identity。
- `localized_text_ref`、`unity_resource_ref`、`condition_expression`、`weighted_selection`、`curve_points` preset 分别展开到 14.17.2、14.17.1、14.16.4、14.16.5 和 curve child table descriptor；不能退化为普通 string。
- `polymorphic_graph_table` 默认使用 child table + discriminator + stable variant id；行为树/AI 节点/效果链不使用 arbitrary JSON cell。
- 任何 preset 展开结果变更都按普通 schema compatibility 处理：只改 display/layout 是 layout diff；改变 value shape、export policy、normalizer、validator、reference target 或 runtime accessor 是 semantic diff。

生成 / 表头默认规则：

- 第 1-7 行展示 preset 展开后的普通 schema 信息，而不是显示 preset 原名；策划看到的是字段名、类型、导出端、规则、说明、枚举可选值、结构体格式和引用填写规则。
- header comment 必须说明 `enabled` / `export_filter` / `tags` / `review_state` 等字段对导出、搜索、审核和运行时是否有影响。
- preset 生成的 helper sheet、dropdown、named range、data validation 和注释仍按 14.3.2 的 schema-owned artifact 规则登记和校验。
- 策划手工新增同名列时，如果没有 matching field id / alias，不会被当作 preset 字段；按 unknown/helper/freeform column 规则处理。

workflow 默认例子：

```text
SkillConfig:
  key: skill_id
  display_name: name
  category: skill_category enum
  tags: repeated enum single_cell
  enabled: bool row_export_filter
  description: editor_only string
  icon: UnityResourceRef<Sprite>
  cost: int client/server
  damage_formula: Expression<int>
  effects: child_table polymorphic payload
```

这个例子中，策划可以直接改 `enabled`、`tags`、`icon.main_asset_path/guid`、`cost` 和 `damage_formula`；但每一列的类型、导出端、可选值、引用目标、表达式语法和运行时含义都来自 schema。Excel 只是 authoring surface，不是额外规则来源。

### 14.17 默认 reference policy

内部资产引用默认是 identity reference，不是字符串 key reference。

reference descriptor 默认字段：

```text
reference family: internal_asset
target table/group/runtime type
target scope
nullable
reference strength: hard | soft
delete policy: restrict | set_null | cascade | leave_missing
ownership
dependency kind
allow duplicate in list
nullable element in list
display token format
resolver policy
```

Excel 表达：

- 可见 cell 默认显示目标 key/path，方便策划阅读。
- metadata 或隐藏 companion data 默认保存 target workbook guid + table id + row guid/local id。
- 用户手填 key/path 时，import 解析为 row guid/local id，并在 metadata flush 时写入稳定身份。
- 如果 visible key/path 与 metadata row guid 指向不同目标，默认以 metadata identity 为准并报 `reference.display_mismatch` warning；用户明确重新选择引用后才更新 identity。

解析默认规则：

- 解析输入优先级是 explicit picker identity > hidden/metadata identity > visible token；没有 metadata identity 时才把 visible token 作为新绑定候选。
- visible token 可以是 schema key、asset path、`workbook_alias:key` 或 schema 声明的 display alias；resolver 必须在 target scope 内 deterministic 解析。
- target scope 默认是当前 mounted source set；schema 可以收紧为 same table、same workbook、specific workbook alias、specific table/group/runtime type。
- visible token 解析到多个目标时产生 `reference.ambiguous_target`，不按 workbook 顺序、路径排序或首个命中猜。
- visible token 解析不到目标时，hard reference 产生 `reference.missing_target` error + `affects_convert=true`；soft reference 产生 warning，并保存 missing state。
- metadata identity 指向的目标不存在时，仍按 hard/soft 规则报告 missing；可见 token 不能自动重绑定到另一个同 key 目标。
- target runtime type 或 table/group 不满足 descriptor 时产生 `reference.type_mismatch`。
- key/path rename 后，只刷新 display token 和 dependency index；reference identity 不变。

写回 / auto-fix 默认规则：

- metadata identity 是写回事实源；display token 是展示投影。SaveAssets 不因显示文本过期而改 identity。
- key/path/display 变化时，可以生成 display token auto-fix proposal；该 proposal 只更新可见 cell，不改变 hidden identity。
- 用户在 Excel 手改 visible token 且 metadata identity 存在时，默认视为 display mismatch；只有 editor picker、明确 rebind command、或 schema 允许 `visible_token_rebind=true` 的保存流程才更新 hidden identity。
- rebind 必须进入 undo/dirty/save/conflict 生命周期，并产生 property_changed + dependency_changed。
- list reference 的重复判断使用 target identity，不使用显示文本；`allow_duplicates=false` 时产生 `reference.duplicate_not_allowed`。

默认 reference policy：

```text
nullable: false
reference strength: hard
delete policy: restrict
ownership: none
dependency kind: runtime preload for runtime fields, validation-only for editor-only fields
list duplicate: false
```

只有 schema 明确声明时才改变默认值：

- `nullable=true` 才允许空引用。
- `soft reference` 缺失时为 warning；hard reference 缺失时为 error，convert blocker。
- `delete_policy=set_null` 只允许 nullable reference。
- `delete_policy=cascade` 只允许 ownership reference。
- `delete_policy=leave_missing` 只允许 soft reference，并且 runtime 必须暴露 missing 状态。
- list reference 默认不允许重复，除非 schema 声明 `allow_duplicates=true`。
- reference list element 默认不允许 null hole；`set_null` 命中 list element 时默认删除元素。只有 schema 声明 `nullable_element=true` 时才写 null element 并保留 element slot/order。

删除目标资产时默认处理：

- restrict：阻止删除，并列出所有 referrer。
- cascade：删除 owned children，并在 report 中列出 cascade set。
- set null：把引用置空，产生 property changed event。
- leave missing：保留引用身份，目标 object 进入 missing/unloaded。

delete planning 默认规则：

- `DeleteAsset` 必须先构建 referrer set，来源是 reverse dependency index + editor draft pending changes，不只看当前 resident graph。
- restrict 命中任何 hard referrer 时，`DeleteAsset` 返回 false，不创建 delete marker，report 包含 referrer asset identity、field id、cell location。
- cascade 只允许 ownership edge；cascade set 必须在 commit 前闭包计算并排序，遇到非 ownership hard referrer 时整个 delete 失败。
- set_null 只允许 nullable reference；commit 时对所有 referrer 生成 property diff，并进入同一 affected workbook set。
- leave_missing 只允许 soft reference；commit 后 referrer 保留 target identity snapshot，runtime 读取 missing state，不伪造 null。
- 跨 workbook delete 如果需要 set_null/cascade 改写其他 workbook，默认把这些 workbook 加入同一 SaveAssets affected set；任一 blocker 时不写任何 workbook。

delete operation 默认阶段：

```text
AnalyzeReferrers
BuildDeletePlan
CreateEditorDraft
SaveAssetsPreflight
SaveAssetsCommit
ReimportAndPublish
```

- `DeleteAsset(path)` / `Undo.DestroyObjectImmediate(obj)` 默认只执行到 `CreateEditorDraft`：创建 delete marker、cascade child delete marker 和 set_null pending property diff；不直接删除 Excel row、不写 workbook、不发布 runtime ChangeSet。
- `BuildDeletePlan` 必须输出 target identity、delete root、cascade closure、set_null referrer patches、leave_missing referrers、restrict blockers、affected workbook set、data_loss_risk 和 plan hash。
- delete root、cascade closure 和 referrer patch 排序固定：workbook guid、table id、row guid/local id、field id、child element identity、property path id。
- 同一 target 在同一 editor draft 中重复 DeleteAsset 是幂等；如果已有 delete marker 的 cascade/set_null closure 与当前 referrer graph 不一致，旧 plan 变 stale，必须重新 analyze。
- delete marker 保存 asset identity，不保存 row number；SaveAssets commit 时按 row guid/local id 和 table id 删除 data row / child rows / metadata tombstone。

policy action 默认语义：

- `restrict`：任何 hard referrer 命中即 blocker；不会创建 delete marker，也不会创建 partial set_null/cascade diff。soft referrer 不阻止 restrict target 删除，但必须在 report 中列出 missing-after-delete 影响。
- `set_null`：只写 referrer field 的 `explicit_null` / missing reference state；对 reference list，默认删除对应 element 而不是写 null hole，除非 list element schema 声明 `nullable_element=true`。set_null patch 必须保留 referrer row identity 和 non-reference fields。
- `cascade`：只沿 ownership edge 删除 owned target；cascade closure 必须是 DAG。遇到 ownership cycle、shared ownership、多 parent ownership 或 closure 外 hard referrer 时整个 plan blocker。
- `leave_missing`：不写 referrer cell，也不改变 hidden identity；只让 target removed 后的 resolver 暴露 missing state。hard reference 不允许 leave_missing。
- mixed policy 同时存在时，先计算 restrict blocker，再计算 cascade closure，再计算 set_null patches，最后记录 leave_missing impacts；任何 blocker 使整个 delete draft 不创建。

SaveAssets / hot reload 默认语义：

- SaveAssets preflight 必须重新计算 delete plan，并验证 target/referrer source revision、schema hash、reference descriptor hash、reverse dependency digest 和 editor draft revision 与创建 delete marker 时一致；否则产生 `transaction.plan_stale`，不写 workbook。
- cascade/set_null 影响跨 workbook 时，所有 affected workbook 必须一起进入 SaveAssets affected set；默认 best_effort 只能在相互无 dependency 的 workbook 间拆分，不能拆分同一 delete plan。
- SaveAssets commit 后重新 import committed source；只有 delete root 和 cascade closure 确认缺失、set_null referrer canonical state 确认写入、leave_missing referrer identity snapshot 确认保留后，才清理 delete draft。
- Runtime hot reload / source switch 对同一 delete plan 发布：deleted root/cascade target 发 `removed`，set_null referrer 发 `property_changed` + `dependency_changed`，leave_missing referrer 发 `dependency_changed`，source summary 列出 affected workbook guids。
- removed object 不再额外发布 property_changed；referrer closure 的 cache invalidation 只通过 dependency_changed / reverse dependency index。

delete report 默认字段：

```text
delete_plan_hash
delete_roots[]
cascade_closure[]
set_null_patches[]
leave_missing_referrers[]
restrict_blockers[]
affected_workbooks[]
referrer_count_by_policy
dependency_closure_digest_before
dependency_closure_digest_after
data_loss_risk
```

runtime 解析：

- 引用解析通过 row guid/local id index，不通过字符串 key。
- key rename 只更新显示和 key index，不破坏引用。
- reference changed 事件必须触发 dependency graph 更新和 reverse dependency index 更新。
- target removed / recreated / missing 时，referrer 的 dependency closure 通过 reverse dependency index 产生 `dependency_changed`；业务缓存不需要扫描全表。

#### 14.17.1 默认 UnityResourceRef reference family 协议

`UnityResourceRef` 是 Unity adapter 的首发外部资源引用 family。核心库只认识 reference family contract，不依赖 UnityEditor / UnityEngine。

默认存盘结构：

```text
main_asset_path
guid
sub_asset_local_file_id optional
sub_asset_name optional
sub_asset_type optional
runtime_provider optional
runtime_key optional generated
```

默认 Excel 展开列：

```text
field.main_asset_path
field.guid
field.sub_asset optional
```

`field.sub_asset` 默认只在 `allow_sub_asset=true` 时生成；默认显示 `sub_asset_name`，隐藏 companion/metadata 保存 `sub_asset_local_file_id` 和 `sub_asset_type`。未启用 sub-asset 的字段不得出现该列，旧 workbook 中遗留的 sub-asset 数据按 capability error 处理。

Excel 可见列默认先写 `main_asset_path` 再写 `guid`，方便策划观察和人工修复；canonical identity、reference hash、runtime dependency 和 provider key 仍只以 `guid`、optional sub asset id 和 runtime provider/key 为准。

canonical identity 默认字段：

```text
guid
sub_asset_local_file_id optional
runtime_provider optional
runtime_key optional
```

display-only 字段：

```text
main_asset_path
sub_asset_name
sub_asset_type_display
```

默认身份规则：

- `guid` 是唯一身份，用于 import、validation、convert、dependency manifest、runtime provider key 生成。
- `main_asset_path` 只是主资源路径展示，方便策划观察、搜索、筛选和人工修复。
- `main_asset_path` 不参与引用身份，不参与 runtime dependency identity；默认也不影响 source hash，除非 schema 明确声明 path display 是 exported runtime field。
- `sub_asset_local_file_id` 只有在 `allow_sub_asset=true` 时参与引用身份；`sub_asset_name` 只是展示和 picker 搜索辅助，不能作为身份。
- `runtime_provider` / `runtime_key` 是 convert/runtime 解析输入；如果由 provider 在 convert 阶段生成，Excel cell 中不要求策划填写，manifest 中必须记录生成来源和 provider descriptor hash。
- guid 与 path 冲突时，以 guid 为准；产生 `unity_ref.path_mismatch` warning，并生成 path display auto-fix proposal。
- guid 不变、path 变化是安全 move/rename，只刷新展示和 report，不触发引用身份变化。
- guid 为空但 path 有值时，Editor/Unity adapter 可以用 `AssetDatabase.AssetPathToGUID` 解析；解析成功后写 guid auto-fix proposal，解析失败按 required/soft policy 产生 diagnostic。
- source hash 默认覆盖 guid、可选 sub_asset_local_file_id、reference family id/version、dependency kind、export policy 和影响 runtime provider key 的 declared inputs；不覆盖 main_asset_path、sub_asset_name 或 provider debug display。
- canonical value hash 使用 guid bytes + optional local file id + provider/runtime key identity bytes；不同 path display 不改变 canonical value hash。

schema 默认选项：

```text
allowed_asset_types[]
guid_required: true
path_display_policy: generated | user_editable_with_guid_priority | hidden
runtime_provider: unity_guid | addressables | resources | custom
dependency_kind: preload | lazy | validation_only | editor_only
missing_policy: hard | soft
allow_sub_asset: false
```

validation 默认规则：

- guid 格式默认必须是 Unity meta guid 的 32 位 hex；大小写 normalize 为 lowercase。
- `guid_required=true` 且 guid 缺失时，hard reference 为 error/convert blocker，soft reference 为 warning。
- Unity adapter validation 使用 UnityEditor.AssetDatabase 查询 guid 当前主路径、main asset type 和存在性；该 step 必须在 Unity main thread。
- 非 Unity core CLI 不能假装验证 Unity asset 存在；只能验证 guid 格式、schema 结构，或消费 Unity adapter 生成的 asset dependency manifest。
- allowed asset type 不匹配产生 `unity_ref.asset_type_mismatch`；hard reference convert blocker。
- guid 找不到当前 asset 时产生 `unity_ref.missing_asset`；hard reference convert/build blocker，soft reference 保留 missing runtime state。
- path 存在但 guid 指向另一个 asset 时，不按 path 改身份；必须由用户明确重新选择引用才更新 guid。

Excel direct edit / writeback 默认规则：

- `field.guid` 是唯一可保存引用身份；`field.main_asset_path` 是 schema-owned display cell，默认可由工具重写为 guid 当前路径。两者都为空表示 missing reference state，按 hard/soft/nullability policy 判定。
- import 时若 `guid` 有值，先解析/校验 guid；`main_asset_path` 只用于 mismatch diagnostic 和 display refresh proposal。即使 path 指向另一个 Unity asset，也不反向改 guid。
- import 时若 `guid` 为空而 `main_asset_path` 有值，Unity adapter 可以在 explicit editor/import repair phase 用 UnityEditor.AssetDatabase 解析 path；解析成功只生成 `guid auto-fix proposal`，不会在普通 import 中直接改变 workbook 身份。用户通过 picker、resolver 或 approved auto-fix commit 后，才同时写入 guid 和 canonical current path。
- import 时若 `guid` 有值而 `main_asset_path` 为空，引用身份有效；layout/display refresh 可以生成 path display writeback proposal。path 缺失本身不改变 canonical value hash。
- import 时若 `guid` 和 `main_asset_path` 都有值且匹配同一 Unity asset，path display 可以按当前 Unity AssetDatabase canonical path 刷新；只改大小写、slash 或移动后的 path 不改变引用身份。
- import 时若 `guid` 和 `main_asset_path` 都有值但指向不同 asset，产生 `unity_ref.path_mismatch` warning；guid 继续作为 canonical identity，path cell 进入 display stale 状态。默认不按 path 改 guid、不按 guid 立即覆盖 path，除非 operation profile 启用 display auto-fix 并生成独立 writeback patch。
- `path_display_policy=generated`：`main_asset_path` 完全由 guid resolver 生成；SaveAssets / layout refresh / explicit display refresh 可以覆盖 stale display cell，但必须记录 old/new path 和 source Unity guid。
- `path_display_policy=user_editable_with_guid_priority`：用户可手动输入 path 作为 repair hint；只有 explicit ResolvePathToGuid / picker commit / approved auto-fix 才更新 guid。保存普通业务字段时不因为 path hint 自动重绑定。
- `path_display_policy=hidden`：不生成可见 path 列；diagnostic/report 仍可显示 current Unity path，但该 path 不进入 workbook data cell。
- path display auto-fix 属于 layout/display patch，不改变 `source_hash` / runtime dependency hash / canonical value hash；若 schema 明确把 path display 导出为 runtime 字段，则必须按普通 field diff 处理，不能走 display-only auto-fix。
- Unity asset rename/move 后，`RefreshUnityResourceDisplay` 或等价 operation 只刷新 display path 和 manifest/report；guid、sub_asset_local_file_id、runtime_key 不变时不发布 runtime property_changed。
- 用户想把引用改到 path 指向的新 asset，必须执行 picker/rebind operation：该 operation 写入 new guid、new path、optional sub-asset identity，并产生 reference changed diff、Undo、dirty、dependency_changed 和 SaveAssets preflight。
- 非 Unity CLI/importer 不能从 `main_asset_path` 解析 guid；它只能报告 `unity_ref.guid_missing` / `unity_ref.path_mismatch`，或消费 Unity adapter 预先生成的 dependency manifest / repair proposal。

sub-asset 默认规则：

- `UnityResourceRef` v1 默认只表示 Unity main asset。
- 如果 schema 需要引用 sprite、animation clip、material sub asset 等 sub-asset，必须显式声明 `allow_sub_asset=true`，并使用单独的 sub-asset extension descriptor 保存 Unity local file id / sub asset name / sub asset type。
- sub-asset extension 不改变 `guid + main_asset_path` 的主资源展示规则；guid 仍指向 main asset 文件，local file id 才区分 sub asset。
- 未声明 `allow_sub_asset` 时，picker 只能选择 main asset；导入发现 sub asset token 时产生 capability error，而不是按 name 猜。
- sub-asset local file id 必须是 Unity 提供的 stable local identifier；如果 adapter 无法取得 stable local id，不能把 sub-asset 当作可导出引用，只能 warning/read-only 或要求项目改用 main asset/provider key。
- sub-asset name/type 改变但 local file id 不变时，只刷新 display hash 和 report；local file id 改变即 identity change，必须产生 property_changed + dependency_changed。

runtime / converted bytes 默认规则：

- converted bytes 不保存 `main_asset_path` 作为 runtime 解析身份；默认保存 guid-derived runtime key、asset type id、provider id 和 dependency kind。
- `runtime_provider=unity_guid` 只适用于 Editor/Development debug；Release build 默认需要 addressables/resources/custom provider 中的一种明确 runtime resolver。
- `runtime_provider=addressables` 时，convert 必须验证 guid 到 Addressable entry 的映射，并把 addressable key / guid mapping 写入 dependency manifest。
- `runtime_provider=resources` 时，convert 必须验证 asset 位于 Resources 路径下，并写入 Resources load path；guid 仍是 authoring identity。
- custom provider 必须声明 provider id/version、guid->runtime key 规则、host requirement 和 deterministic flag。
- runtime resolver 失败不能按 main_asset_path 兜底加载；必须报告 missing provider/runtime key。
- `runtime_key` 必须由 provider descriptor 和 declared inputs deterministic 生成；不能读取当前时间、scene state、unsnapshotted Unity editor cache 或网络状态。
- 如果同一 guid/sub-asset 在不同 provider 下得到不同 runtime key，provider id 是 runtime identity 的一部分；切换 provider 会影响 runtime_dependency_hash 和 converted bytes cache key。
- Editor/Development `runtime_provider=unity_guid` 可以把 guid/local file id 直接作为 runtime key；Release profile 默认禁止，除非 project policy 明确声明该 player runtime 有 guid resolver。

build dependency manifest 默认字段：

```text
guid
main_asset_path
sub_asset_local_file_id optional
sub_asset_name optional
sub_asset_type optional
asset_type
runtime_provider
runtime_key
dependency_kind
referenced_by_assets[]
source_locations[]
provider_descriptor_hash
runtime_dependency_hash
display_dependency_hash
```

Unity build integration 默认规则：

- Unity adapter 在 build 前根据所有 exported `UnityResourceRef` 收集 dependency manifest。
- main_asset_path 来自当前 Unity AssetDatabase 查询结果；Excel 中旧 path 只用于 mismatch diagnostic。
- build cache 默认使用 `runtime_dependency_hash` 纳入 converted bytes cache key；guid 对应 asset 内容、runtime provider、runtime key 或 provider version 变化时重新 convert/build。
- `main_asset_path` 只进入 `display_dependency_hash` 和 report/manifest；单纯 move/rename 且 guid/runtime_key 不变时，可以只刷新 manifest/report，不要求 Release bytes 内容变化。
- 删除或移动 Unity asset 时，只要 guid 可解析，新 path refresh 是 safe；guid 丢失或 meta 文件被重建是 missing asset，不按相同 path 自动认作同一资源。
- `runtime_dependency_hash` 覆盖 guid、optional sub_asset_local_file_id、asset type id、runtime provider id/version、runtime key、dependency kind、asset content/provider dependency hash 和 provider_descriptor_hash。
- `display_dependency_hash` 覆盖 main_asset_path、sub_asset_name/type display、source_locations 和 referrer display path；它不进入 Release bytes content hash。

drawer / picker 默认规则：

- `UnityResourceRef` drawer 显示 main asset path，并持有 guid；选择新 asset 时同时更新 guid 和 main_asset_path。
- 手动编辑 path 不立即改变 guid；需要 resolver 成功并由用户确认或 auto-fix commit 后才更新 guid。
- drawer 在 repaint/OnGUI 热路径不能执行全量 project scan；搜索和验证结果必须缓存或由显式 picker/search 操作产生。

#### 14.17.2 默认 LocalizedTextRef / localization table 协议

`LocalizedTextRef` 是完整目标版默认支持的 reference family / localization contract，不是普通 string，也不是高级 value shape 的占位。完整目标版必须实现存盘结构、文本表 identity、lint、manifest、diagnostic、hash、build gate 和 no-GC runtime lookup / formatter；复杂 token grammar、ICU plural/select 或外部 localization provider 属于显式 extension opt-in，禁用时只允许 stable report / read-only / build blocker。

本地化文本不是普通 `string` 的变体。普通 `string` 表示运行时直接使用的文本值；`LocalizedTextRef` 表示对文本表条目的稳定引用。Excel 中的预览文本只是帮助策划确认 key 是否正确，不参与引用身份。

LocalizationDescriptor 默认字段：

```text
localization_namespace
text_table_ids[]
default_locale
required_locales[]
optional_locales[]
fallback_edges[]
token_validation_policy
export_locale_policy
runtime_provider
```

LocalizedTextRef 默认存盘结构：

```text
text_namespace
text_key
preview_text
```

默认 Excel 展开列：

```text
field.text_key
field.preview
```

文本表默认 layout：

```text
text_key
context
description
tokens
max_length optional
text.<locale>...
```

默认身份规则：

- 默认文本表是普通 ExcelDB asset table，文本条目拥有 workbook guid + table id + row guid/local id。
- `text_key` 是人类可读和运行时 lookup key；引用 metadata/companion data 必须保存目标文本条目的 row identity。
- `preview_text` 默认来自 `default_locale`，只是 authoring display；预览过期产生 `localization.preview_mismatch`，不改变引用 identity。
- key rename 不改变文本条目 identity；引用的 visible key 可以通过 auto-fix 刷新，不能按同名 key 静默重绑定到另一行。
- 如果项目接入外部本地化系统，可以用 custom localization reference family，但必须声明 provider id/version、key namespace、locale manifest 和 deterministic export rules。

locale / fallback 默认规则：

- locale id 使用 BCP-47-like lowercase token，例如 `zh-cn`、`en-us`；schema-lint normalize 后排序，不能依赖 Excel 列顺序。
- `required_locales` 在当前 export view 中缺失时产生 `localization.locale_missing` error + `affects_convert=true`。
- `optional_locales` 缺失只产生 warning/report；是否随包导出由 `export_locale_policy` 决定。
- fallback graph 必须是有向无环图；存在环时产生 `localization.fallback_cycle` blocker。
- fallback 只能用于 runtime 查找缺失 locale 文本，不能让 convert 忽略 required locale 缺失。
- 同一 text_key 在同一 localization namespace 内必须唯一；重复按 table key duplicate 处理。

token / format 默认规则：

- `tokens` 列声明允许的格式化 token，例如 `{player_name}`、`{count:int}`。
- 每个 locale 文本中的 token 集合必须与 descriptor policy 匹配；缺 token、多 token、类型不一致产生 `localization.token_invalid`。
- token 解析使用 ExcelDB tokenizer，不执行字符串插值脚本，不依赖当前系统语言。
- runtime 格式化 API 必须有 no-GC 路径：预解析 token table，调用方提供参数 buffer 或 generated formatter。
- token descriptor 至少包含 token id、name、value type、nullable/default policy、format policy 和 ordering key；runtime 参数按 token id 匹配，不按 display name 字符串查字典。
- 默认 placeholder grammar 只支持 `{token_name}` 和 `{token_name:type}`；literal `{` / `}` 必须用 `{{` / `}}` 转义。未闭合、重复 token、未知 token 或 type 不匹配产生 `localization.token_invalid`。
- 完整目标版默认不内置 ICU message format、plural/select 或脚本表达式；项目需要时必须通过 deterministic localization formatter extension 声明 grammar id/version、function set、locale rules snapshot 和 hash 输入。
- runtime formatted output 的推荐入口是 caller-owned `Span<char>` / generated formatter；返回 managed `string` 的 API 只能标为 low-frequency / allocating，不能作为 no-GC 证明。

runtime lookup / fallback 默认规则：

- runtime 当前 locale 是 `LocaleId` / opaque handle，初始化时由 locale descriptor 解析；稳定帧内不解析 locale string。
- fallback resolution 顺序固定：requested locale exact match -> descriptor 中按 stable edge order 展开的 fallback chain -> default_locale；每个节点最多访问一次。
- required locale 在 convert 前必须存在并通过 token validation；runtime fallback 不能掩盖 required locale 缺失。
- optional locale 缺失时可以 runtime fallback；startup report 必须记录 missing optional locale count，debug/report 可列出 locale id。
- explicit empty string 是有效本地化文本，不触发 fallback；如果项目要把 blank 当 missing，必须在 localization descriptor 声明 `blank_text_policy=missing`。
- fallback 结果必须可进入 dependency graph：目标 locale 文本、fallback locale 文本或 fallback graph 改变时，referrer 收到 `dependency_changed` 或 localization summary。

runtime API 形态默认示例：

```csharp
public readonly struct LocaleId
{
    // Opaque normalized locale id resolved during initialization.
}

public readonly struct LocalizedTextRef
{
    public readonly AssetIdentity textEntryIdentity;
    public readonly int namespaceId;
    public readonly int textKeyStringId;
}

public enum LocalizedTextArgumentKind
{
    Integer,
    Float,
    StringView,
    AssetIdentity,
    CustomProviderValue
}

public readonly struct LocalizedTextArgument
{
    public readonly int tokenId;
    public readonly LocalizedTextArgumentKind kind;
    // Payload is fixed-width or provider-owned view; no boxing.
}

public enum LocalizationFormatStatus
{
    Complete,
    Truncated,
    MissingKey,
    MissingLocale,
    MissingProvider,
    TokenMismatch
}

public interface ILocalizationProvider
{
    string providerId { get; }
    string providerVersion { get; }
    bool TryResolveLocale(ReadOnlySpan<char> localeName, out LocaleId localeId);
    LocalizationFormatStatus TryFormat(
        in LocalizedTextRef textRef,
        LocaleId locale,
        ReadOnlySpan<LocalizedTextArgument> args,
        Span<char> destination,
        out int charsWrittenOrRequired);
}
```

API 默认约束：

- `LocalizedTextRef` 是 generated field getter 的返回形态；getter 不 materialize 文本字符串，只返回 text entry identity / namespace / key handle。
- `ILocalizationProvider.TryFormat` 是 no-GC 主路径；它不能分配临时字典、正则、字符串 builder、闭包或异常对象。
- token 参数缺失、类型不匹配、输出 buffer 不足时通过 `LocalizationFormatStatus` / report code 表达；`Truncated` 时 `charsWrittenOrRequired` 返回 required char count，不分配扩容 buffer。
- provider 注册、locale string 解析、template bytecode/string table 绑定属于 initialization/prewarm；稳定 lookup/format 不读 Excel、不扫 manifest、不做 provider discovery。

runtime / converted bytes 默认规则：

- converted bytes 对 `LocalizedTextRef` 默认保存 text entry identity、text key string id、namespace id 和 runtime provider id。
- localization string table 可以作为 bytes section 或单独 localization artifact；无论哪种方式，manifest 必须记录 `localization_manifest_hash`。
- converted bytes / localization artifact 必须包含 locale table、text entry table、template/token bytecode 或 equivalent provider payload、fallback graph digest、token descriptor digest 和 provider id/version。
- Release runtime 不读取 Excel 文本表，不按 preview_text 兜底；缺 key/locale/provider 必须分别报告 `localization.key_missing`、`localization.locale_missing`、`localization.provider_missing`。
- export view 可以选择导出 locale subset；`export_view_hash` 必须覆盖 locale set 和 fallback graph。
- `LocalizedTextRef` 字段变化、目标文本行内容变化、required locale 内容变化，都要进入 dependency graph，使 UI/任务/物品展示缓存可以收到 `dependency_changed`。

validation 默认规则：

- 引用解析不到文本条目时产生 `localization.key_missing`；exported runtime field 默认 convert blocker。
- required locale cell blank 按 `localization.locale_missing` 处理，不按普通 optional string 默认空串。
- preview 与 default locale 当前文本不一致时只产生 display diagnostic，可通过 diagnostic/layout refresh 更新 preview。
- 文本表中的普通说明、翻译备注、max_length 只在 schema 声明为 validation/runtime 字段时进入 source_hash；普通 translator comment 不进入 runtime hash。
- runtime provider descriptor 缺失、版本不匹配、manifest 未绑定 provider 或宿主未注册 provider 时产生 `localization.provider_missing`；Release 不允许 fallback 到 built-in debug provider。

manifest / cache 默认规则：

- artifact manifest 默认记录 exported locale set、fallback graph hash、localization_manifest_hash 和 localization provider id/version。
- build cache key 包含会影响 runtime 文本解析的 locale set、fallback graph、text canonical values、token descriptor 和 provider version。
- 只改 translator comment、Excel 样式或 preview_text，不要求 Release bytes cache miss；只刷新 report/display hash。

#### 14.17.3 默认 dependency graph / preload plan 协议

依赖图是 runtime object、内部引用、UnityResourceRef、LocalizedTextRef、owned child、表达式和掉落结果之间的统一事实源。它不是某个资源系统的附属缓存；战斗、AI、UI、资源预加载、热更新缓存失效和 build dependency manifest 都必须从同一套 dependency edge 生成。

DependencyEdge 默认字段：

```text
source_identity
source_table_id
source_field_id optional
source_child_identity optional
target_family: internal_asset | unity_resource | localized_text | external_runtime_key
target_identity
target_runtime_type optional
reference_strength: hard | soft
dependency_kind: ownership | runtime_preload | runtime_lazy | validation_only | editor_only | build_only
delete_policy optional
runtime_provider_id optional
export_view_id
preload_group_id optional
condition_binding_id optional
source_location optional
```

内置 target family 默认语义：

- `internal_asset`：target_identity 是 workbook guid + table id + row guid/local id；用于 ExcelDB 内部资产、子表元素和文本表条目。
- `unity_resource`：target_identity 是 Unity guid + optional sub asset identity + runtime provider key；`main_asset_path` 只作为 display/debug location。
- `localized_text`：target_identity 是 localization namespace + text entry identity + locale/fallback policy；实际文本内容变化也会影响依赖。
- `external_runtime_key`：target_identity 由 custom reference family/provider descriptor 定义；必须声明 provider id/version、hash 输入和 no-GC runtime key 表达。

dependency kind 默认语义：

- `ownership`：生命周期依赖；用于 owned child、cascade delete 和 parent/child ChangeSet。ownership edge 必须形成 DAG，出现环产生 `dependency.ownership_cycle`。
- `runtime_preload`：加载 source asset 时默认应随 asset 预加载或进入 preload plan closure。
- `runtime_lazy`：运行时可解析，但不进入默认 preload closure；引用变化仍会产生 dependency_changed。
- `validation_only`：只用于 import/schema/check/report；不进入 Release runtime dependency_index，除非 build profile 显式保留 debug graph。
- `editor_only`：只用于 Editor authoring、picker、drawer 和 diagnostic；不进入 Release runtime dependency_index。
- `build_only`：不影响 runtime object 读取，但影响 Unity build/package/cache，例如 Addressables group、asset bundle、外部资源包内容。

dependency graph 构建默认规则：

- schema reference descriptor、UnityResourceRef、LocalizedTextRef、owned child table、expression symbol/reference binding、weighted selection result payload 和 custom reference family 都通过同一 edge builder 输出 DependencyEdge。
- edge builder 只读取 schema/normalized cell/metadata/provider manifest 中声明的输入；不能扫描未声明外部状态，不能依赖 workbook 读取顺序或字典顺序。
- hard target missing 仍产生对应 reference/unity_ref/localization diagnostic；dependency graph 不把 missing hard target 静默降级成 soft edge。
- 当前 export view strip 掉 target 时，hard runtime edge 产生 `convert.reference_stripped_target`；validation/editor-only edge 可以保留在 report/debug graph，但不进入 Release bytes。
- 同一 source/field/target/kind 的重复 edge 在 canonical graph 中去重；重复出现的 source location 进入 debug/report locations，不影响 runtime edge identity。
- edge canonical sort key 默认是 source slot、source field id、target family、target identity canonical bytes、dependency kind、runtime provider id。
- reverse dependency index 与正向 index 从同一 edge list 生成；不能由另一个实现路径单独构建。

PreloadPlanDescriptor 默认字段：

```text
preload_plan_id
display_name
root_selector: explicit_assets | table | asset_path_prefix | schema_group | build_label
root_identities[] optional
root_table_ids[] optional
include_self
traversal: direct | recursive
max_depth
include_dependency_kinds[]
include_target_families[]
include_soft_missing
cycle_policy: report | error
output_order: source_then_target_canonical
export_view_id
```

preload plan 默认规则：

- preload plan 是 build/export profile 的一部分；影响 bytes 内容时必须进入 `preload_plan_hash`、artifact manifest 和 cache key。
- recursive plan 使用 visited set 截断普通 dependency 环；发现环产生 `dependency.preload_plan_cycle`，除非 `cycle_policy=error`。
- ownership 环永远不是普通 preload 环，必须 blocker。
- root selector 只能使用 schema 声明的 identity/key/path/index；不能执行脚本、读取当前时间或根据本机路径猜。
- `runtime_preload` 是默认 include kind；是否把 `runtime_lazy`、`build_only`、localized_text 或 unity_resource 纳入 plan 必须由 plan 显式声明。
- 预计算 plan 输出是 target range，不是字符串列表；display path/key 只进入 debug_symbols/report。
- 同一 plan 在 ExcelDataSource candidate build 和 converted bytes convert 中必须得到相同 target set、排序和 hash。

converted bytes 默认表达：

- `dependency_index` 保存每个 source slot 的 direct edge range；edge payload 保存 target family、target slot/key、dependency kind、strength 和 provider id。
- `reverse_dependency_index` 保存 target -> referrer range，用于 hot reload/remove/recreate 时快速找缓存失效闭包。
- `preload_plan_index` 是条件 section；只有 profile 声明预计算 plan 时输出，保存 plan descriptor hash、root range、target closure range 和 cycle summary。
- internal asset target 使用 object slot；Unity/localization/external target 使用 provider key table，不在热路径 materialize 字符串。
- dependency_index、reverse_dependency_index、preload_plan_index 的 range 都必须在 `verify-bytes` 中校验。

runtime 查询默认规则：

- runtime dependency traversal 是 no-GC 热路径；所有查询返回 opaque target handle，不返回 string/path/list。
- `GetDependencies` 默认返回 direct edges；recursive 查询必须显式传 `DependencyQuery`，并使用 caller-owned buffer 或预热过的 traversal workspace。
- buffer 不足时返回 `RuntimeQueryStatus.Truncated`，`count` 返回 total required count，实际写入数量为 `Min(buffer.Length, count)`；普通热路径不为了记录 `runtime.dependency_buffer_too_small` 分配 report 字符串。
- named preload plan 查询优先读取 `preload_plan_index`；没有预计算 section 时可以用 dependency_index 即时展开，但仍必须遵守 caller-owned buffer / workspace 和 allocation policy。
- external target 的实际加载由 provider 负责；ExcelDB 只提供 runtime key/provider handle，不在核心 runtime 中调用 UnityEditor AssetDatabase 或发起 IO。

hot reload / source switch 默认规则：

- candidate build 阶段重建 dependency edge list、reverse index 和 preload plan；commit 阶段只交换或 patch range/index，达到水位线后不得分配。
- 字段值变化但 dependency edge set 不变时，只发布 property_changed；edge set 或 target canonical value 变化时发布 dependency_changed。
- target removed/recreated/missing 通过 reverse_dependency_index 找 referrer closure；业务系统不需要扫描全表。
- preload plan digest 变化但 object property 没变时，不发布 property_changed；发布 source_summary / dependency_plan_changed，让资源预加载系统重算计划。
- source switch 失败时旧 dependency graph、旧 preload plan 和旧 runtime provider key table 继续服务。

validation / diagnostic 默认规则：

- schema 声明了未知 dependency kind 或 target family，产生 `dependency.edge_kind_invalid`。
- preload plan root selector 解析为空且 schema/profile 没有声明允许空 plan，产生 `dependency.preload_plan_invalid`。
- preload plan 引用了当前 export view 中不存在的 table/group/label/provider，产生 `dependency.preload_plan_invalid`。
- custom provider 不能给出 deterministic runtime key 或 hash 输入时，按 `extension.nondeterministic` / `schema.export_policy_invalid` 阻止 convert。
- Release build 下 `runtime_preload` / `runtime_lazy` Unity resource edge 没有 runtime provider key 时仍按 `unity_ref.runtime_provider_missing` 阻止 build/convert。

### 14.18 默认 schema compatibility / migration 协议

schema 变化默认不是直接改表，而是先做 compatibility analyze。这个 analyze 是 operation transaction 的第一层输入，必须在 schema generation、import、save、convert、source switch 和 hot reload 前执行。

输入：

- workbook metadata 中记录的 previous schema snapshot。
- 当前代码或 schema 文件生成的 current schema snapshot。
- migration registry。
- workbook 当前 column / row / metadata 状态。
- 当前操作目标：open、generate structure、import、save、convert、runtime open/switch。

默认输出：

```text
schema_diff_set
compatibility_level: compatible | compatible_with_layout_refresh | migration_required | import_allowed_convert_blocked | incompatible
required_migrations
diagnostics
schema_hash_before
schema_hash_after
layout_hash_before
layout_hash_after
```

兼容分级默认语义：

- `compatible`：不需要写 workbook，不影响 import / export / runtime 解释。
- `compatible_with_layout_refresh`：只需要刷新 schema-owned structure region，例如表头、注释、下拉选项、列显示顺序；不改变正式数据语义。
- `migration_required`：可以打开 workbook，但不能静默导入为新语义；必须执行显式 migration 或进入 read-only fallback。
- `import_allowed_convert_blocked`：编辑器可以读入并显示诊断，但 converted bytes / CI / build 必须失败。
- `incompatible`：不能作为当前 schema 的有效 source；不允许写 workbook，不允许替换 runtime source。

默认变化矩阵：

| schema 变化 | 默认判定 | 默认动作 |
| --- | --- | --- |
| 只改 display name、description、header comment、冻结窗格、列宽 | `compatible_with_layout_refresh` | 更新 layout hash；不改变 schema hash；保留数据 |
| sheet rename，但 table id 不变 | `compatible_with_layout_refresh` | 通过 metadata 识别表；更新 sheet mapping |
| field rename，但 field id 不变 | `compatible_with_layout_refresh` | 更新表头和 property display；cell 数据按 field id 保留 |
| 新增 nullable 字段 | `compatible_with_layout_refresh` | 生成列；缺值按 null 读；可在 save 时物化 |
| 新增 optional 字段且有 default | `compatible_with_layout_refresh` | 生成列；缺值按 default 读；不强制改所有数据行 |
| 新增 required 字段且无 default | `import_allowed_convert_blocked` | workbook 可打开；缺值行产生 validation error；convert/build blocker |
| 删除字段但 metadata 标记 deprecated/reserved | `compatible_with_layout_refresh` | 默认保留旧列为 ignored/deprecated，导出剔除 |
| 删除字段且未 reserved，旧表仍有数据 | `migration_required` | 需要迁移声明数据丢弃、移动或保留为 helper；否则不写回 |
| field id 改变但 name 相同 | `migration_required` | 视为删除旧字段并新增新字段；不按名字自动搬数据 |
| 字段类型等价变化，例如 enum display 改名但 value id 不变 | `compatible_with_layout_refresh` | 更新注释/dropdown；数据身份不变 |
| 字段类型 widening，且所有旧值可无损转换 | `migration_required` | 需要声明 type migration；dry-run 验证全量数据 |
| 字段类型 narrowing 或可能丢精度 | `import_allowed_convert_blocked` 或 `incompatible` | 有显式迁移且验证通过才允许 commit |
| scalar 和 simple struct 互换 | `migration_required` | 必须有 migration 或 codec；不能按字符串猜 |
| simple struct `single_cell` 与 `expanded_columns` 互换 | `migration_required` | 同 field id / subfield id 且 codec 可逆时允许自动迁移候选，否则 blocker |
| enum 新增 value | `compatible_with_layout_refresh` | 更新 dropdown/comment/helper sheet |
| enum value rename/display 改名但 value id 不变 | `compatible_with_layout_refresh` | 旧 token 可通过 alias 解析；metadata 写回当前 token |
| enum value 删除但数据未使用 | `compatible_with_layout_refresh` | 更新 dropdown；保留 deprecated alias 记录 |
| enum value 删除且数据正在使用 | `import_allowed_convert_blocked` | 编辑器显示错误；convert/build blocker |
| reference 从 nullable 变 non-null | `import_allowed_convert_blocked` | 空引用行 blocker；需要补数据或 migration |
| reference 从 soft 变 hard | `import_allowed_convert_blocked` | missing target 变 blocker |
| reference 从 hard 变 soft，或 non-null 变 nullable | `compatible` | 语义放宽，不需要改表 |
| key pattern 改变但 key field id 不变 | `compatible_with_layout_refresh` | asset path 变化，触发 moved/renamed report；identity 不变 |
| key field 改变或组合 key 改变 | `migration_required` | 必须 dry-run 检查 duplicate key、引用显示和 asset path 影响 |
| export policy 改变 exported field set | `migration_required` | 需要 runtime compatibility report；converted bytes schema hash 变化 |
| validator 收紧 | `import_allowed_convert_blocked` | 编辑器可打开；违反新规则的行阻止 convert/build |
| validator 放宽 | `compatible` | 不需要写回 |
| table split / merge / table id 改变 | `migration_required` | 必须声明 row identity 映射；否则 incompatible |

#### 14.18.1 默认 schema diff / compatibility classifier 协议

compatibility analyze 的核心产物不是一段人类文本，而是一组稳定 `schema_diff_entry`。不同工具、CI、Unity adapter 和迁移向导必须在同一输入下得到相同 diff 和相同 compatibility level。

schema diff 输入默认规则：

- previous/current schema 都必须来自 canonical `SchemaDescriptor` snapshot，不从 `.proto` 文本、C# reflection、Excel header 或生成代码反推。
- previous snapshot 优先来自 workbook metadata / converted bytes manifest 中记录的 descriptor hash 与 descriptor snapshot；缺失时只能进入 degraded analyze，不允许按当前 schema 猜旧语义。
- current snapshot 来自当前 generated registry；如果 registry descriptor hash 与 schema source 不一致，先 schema-lint blocker，不进入 workbook migration。
- workbook layout / metadata 只用于判断数据是否存在、是否可定位、是否需要 migration；不能改变 schema diff 的 identity 判断。

`schema_diff_entry` 默认字段：

```text
diff_id
diff_kind
compatibility_level
old_descriptor_id optional
new_descriptor_id optional
descriptor_kind: table | field | enum_value | struct | reference | validator | export | key | editor_capability
affected_table_id optional
affected_field_id optional
old_schema_path optional
new_schema_path optional
old_value_shape optional
new_value_shape optional
old_semantic_hash optional
new_semantic_hash optional
old_layout_hash optional
new_layout_hash optional
requires_migration
requires_data_scan
data_loss_risk
suggested_migration_ids[]
diagnostic_codes[]
```

`diff_id` 默认生成规则：

```text
diff_id = sha256(
  "exceldb-schema-diff-v1" +
  descriptor_kind +
  old_descriptor_id +
  new_descriptor_id +
  diff_kind +
  old_semantic_hash +
  new_semantic_hash)
```

- diff entry 排序固定为 descriptor kind、table id、field id、enum value id、diff kind、diff id。
- diff id 不包含显示名、source file path、source line、当前时间、发现顺序或 workbook path。

identity matching 默认规则：

1. table / field / enum value / validator / migration / editor capability 首先按 stable id 匹配。
2. stable id 相同才允许判定为 rename、layout refresh、semantic change 或 validator mode change。
3. stable id 不同，即使 display name、field path、C# member name、header text 完全相同，也默认是 delete + add；只能由显式 migration descriptor 把旧 id 映射到新 id。
4. alias 只能用于 workbook layout repair、visible path 兼容和 migration suggestion；不能让 schema diff 跨 id 自动搬数据。
5. reserved/deprecated registry 是 id 的墓碑；旧 id 被删除后没有进入 reserved/deprecated，是 schema-lint blocker；已 reserved 的 id 重新用于新语义是 `schema.reserved_id_reuse`。

table diff 默认规则：

- table id 相同、schema/display name 变化：layout diff，不改变 table identity。
- table id 相同、runtime type binding 改变：如果 generated binding manifest 声明兼容 adapter，可进入 migration/runtime compatibility；否则 `migration_required` 或 `incompatible`，取决于操作目标。
- old table id 缺失且未 reserved/deprecated：schema-lint blocker；不能把旧 workbook 数据当 unknown sheet 静默保留。
- old table id 已 reserved/deprecated：workbook 中旧 table 默认保留为 ignored/deprecated table，不导出；cleanup 需要显式 migration。
- new table id：add table diff；如果 table 有 required key/runtime fields 且无 default，生成结构可以创建 sheet，但 convert 仍需 validation。

field diff 默认规则：

- field id 相同、field path / display / description / header comment 变化：layout diff。
- field id 相同、export policy、required/default/nullability、value shape、reference strength、validator blocker mode 变化：semantic diff，按矩阵分类。
- old field id 缺失但 reserved/deprecated：旧列默认保留为 ignored/deprecated field group；不导出，不自动删除。
- old field id 缺失且未 reserved，如果 workbook 中有任何非空 canonical data 或 metadata row usage：`migration_required`；如果全空，也必须 report，默认仍保留到 cleanup migration。
- new field id：add field diff；nullable/default 字段是 layout refresh，required 无 default 是 `import_allowed_convert_blocked`。
- field path 相同但 field id 改变：delete old + add new；不按路径自动搬数据。
- current field alias 命中 old field path 但 field id 不同：只生成 migration suggestion，不自动兼容。

value shape compatibility 默认规则：

```text
exact
layout_only
lossless_if_declared
lossy_or_narrowing
representation_changed
unsupported
```

- `exact`：canonical value bytes 不变，例如 display/header 变化。
- `layout_only`：Excel 展示或单元格展开方式变化，但 canonical value 不变，例如 enum display rename。
- `lossless_if_declared`：所有旧值理论上可无损转换，但必须有 migration descriptor 和 dry-run 数据扫描证明，例如 int32 -> int64、single-cell struct -> expanded columns。
- `lossy_or_narrowing`：可能丢精度、截断、改变 null/default 语义；没有显式 migration + confirmation 时不能写回。
- `representation_changed`：canonical bytes、runtime field layout 或 generated accessor slot 变化；converted bytes schema hash 变化，runtime 需要兼容 adapter 或重新 convert。
- `unsupported`：无法证明转换或缺 codec/host requirement，默认 `incompatible`。

enum diff 默认规则：

- enum value id 相同、display/export token 变化：layout diff；旧 token 必须作为 alias/deprecated alias 才能解析旧 workbook。
- 新增 enum value：layout refresh。
- 删除 enum value：先扫描 workbook canonical enum value id；未使用时 compatible_with_layout_refresh，并保留 deprecated/reserved value id；正在使用时 `import_allowed_convert_blocked`，需要用户改数据或 migration。
- enum value id 改变但 token/display 相同：delete + add；不按 token 自动搬数据。

required / default / nullability 默认规则：

- optional -> required，且现有数据无 missing/null：compatible 或 layout refresh；但 dry-run/import 必须扫描证明。
- optional -> required，存在 missing/null：`import_allowed_convert_blocked`。
- required -> optional 或 non-null -> nullable：compatible。
- 新增 default 或改变 default：如果只影响 missing materialization，schema hash 改变；已显式填写的 cell 不重写。是否物化到 Excel 是 save/generation policy，不属于 silent migration。
- default semantic 改变且旧数据依赖 materialized default：需要 data scan；影响 exported/runtime value 时至少 `migration_required` 或 convert blocked。

validator diff 默认规则：

- validator id/version/mode/severity 中会改变 import/export/runtime blocker 的部分属于 semantic diff。
- validator message、localization、UI hint 变化只进入 layout/editor diff。
- validator 收紧必须对 current workbook 执行 validation scan；违反项产生 validation diagnostic，compatibility level 至少为 `import_allowed_convert_blocked`。
- validator 放宽是 compatible，除非它改变 normalized value、reference graph 或 export set。
- custom validator host requirement 不可用时，compatibility analyze 不得假装通过；按 extension host requirement 产生 blocker。

compatibility level 归并规则：

```text
compatible
compatible_with_layout_refresh
import_allowed_convert_blocked
migration_required
incompatible
```

- 最终 level 是所有 diff entry 和 workbook data scan 结果按上表从低到高取最大值。
- `migration_required` 高于 `import_allowed_convert_blocked`：如果既需要迁移又有当前数据 validation blocker，先要求 migration/read-only，不允许 save/convert。
- `incompatible` 只能由无法定位 identity、unsupported value shape、缺失 required host/schema descriptor、或明确禁止的 runtime/schema mismatch 产生；不能把普通数据校验失败升级为 incompatible。
- operation target 可以进一步收紧结果，例如 `ConvertBytes` 对 `import_allowed_convert_blocked` 失败，`OpenWorkbook` 可 read-only 打开。

compatibility report 默认规则：

- report 必须列出所有 diff entry，不只列最高 severity。
- 每个 migration suggestion 必须引用 migration id/version/descriptor hash；不能只写“需要迁移”。
- 如果 classification 依赖数据扫描，report 必须写扫描范围、row count、used enum values、missing required count、lossy conversion count。
- 如果没有 previous descriptor snapshot，report 必须标记 `previous_schema_snapshot_missing=true`，并禁止自动 migration apply。

migration registry 默认要求：

- migration 必须有 stable id、from schema hash 或 version range、to schema hash、affected table/field ids、dry-run、apply、rollback/restore strategy。
- migration 不能只按 header text 或 row number 匹配；必须优先使用 workbook guid、table id、field id、row guid/local id。
- migration dry-run 必须输出将移动、改写、删除、默认填充的数据量和 cell location。
- migration apply 必须走 operation transaction；blocker 时不写 workbook。
- 会丢弃正式数据的 migration 必须显式声明 `data_loss_risk=true`，默认需要用户或 CI policy 确认。
- migration 成功后必须写入 migration history，包括 migration id、before/after schema hash、before/after layout hash、operation id、plan hash 和 dry-run report hash。

#### 14.18.2 默认 migration registry / execution 协议

migration 是 schema contract 的一部分，不是临时脚本。editor、CLI、CI、importer、converter 都从 generated registry 读取同一份 migration descriptor。

MigrationDescriptor 默认字段：

```text
migration_id
migration_version
descriptor_hash
from_schema_hash
from_schema_version_range
to_schema_hash
required_previous_migration_ids
conflicts_with_migration_ids
affected_workbook_scope
affected_table_ids
affected_field_ids
operation_kinds: structure | data | metadata | layout | editor_state
data_loss_risk
host_requirement
deterministic
dry_run_entry
apply_entry
verify_entry
rollback_or_restore_strategy
```

registry lint 默认规则：

- `migration_id + migration_version` 在 registry 中全局唯一；同 id/version 的 descriptor hash 不一致是 schema blocker。
- `from_schema_hash` / `to_schema_hash` 优先于 version range；range 只能用于一组等价 schema 变化，且必须 dry-run 验证实际 hash。
- `affected_table_ids` / `affected_field_ids` 必须引用已知、reserved 或 deprecated 的 schema id；不能只引用 display name。
- `required_previous_migration_ids` / `conflicts_with_migration_ids` 形成的 graph 不能有环。
- `host_requirement` 必须可解析；Unity-only migration 不能在宿主无关 CLI 中静默执行。
- 如果同一个 from/to schema 存在多条可行 migration path，且 registry 没有声明唯一 supersedes/priority，默认报 `schema.migration_ambiguous` blocker。
- migration entry 不能直接读写 workbook 文件、metadata sheet 或 runtime object graph；只能通过 `MigrationContext` 生成 operation diff / patch proposal。

MigrationPlan 默认字段：

```text
plan_format_version
plan_id
plan_hash
operation_id
source_schema_hash
target_schema_hash
source_layout_hash
target_layout_hash
source_fingerprint
source_revision
descriptor_hash
steps[]
affected_workbooks[]
affected_tables[]
affected_fields[]
diff_entries[]
dry_run_summary
dry_run_report_hash
data_loss_confirmations_required[]
backup_required
recovery_preflight
host_requirements[]
step_descriptor_hashes[]
expected_schema_hash_after
expected_layout_hash_after
expected_source_hash_after
```

planning 默认流程：

1. compatibility analyze 先根据 workbook metadata 的 previous schema snapshot 和 current schema snapshot 建 schema diff。
2. registry 只按 schema hash/version range、table id、field id、migration dependency graph 选 migration；不按表头文本或行号猜。
3. 对每个 candidate step 检查 precondition：source field/table/metadata 是否存在，target field/table/metadata 是否不存在或可合并，row identity 是否完整。
4. 拓扑排序 migration steps；同层 step 只能按 stable `migration_id` 排序，不能依赖注册顺序或字典顺序。
5. 如果 plan 不能唯一确定，或某 step 缺失 host requirement，compatibility level 保持 `migration_required` 并输出 blocker diagnostic。

MigrationPlan hash 默认规则：

- `plan_hash` 是 over canonical plan bytes 的 SHA-256 lowercase hex，不包含 human-readable summary、生成时间、本机路径或 UI 排序。
- canonical plan bytes 包含 plan format version、source/target schema/layout hash、descriptor hash、source fingerprint/revision、ordered steps、step descriptor hash、diff entries、data loss confirmation ids 和 expected hashes。
- `dry_run_report_hash` 绑定 dry-run report 的 machine-readable 部分；apply 只能接受与 plan 匹配的 report hash。
- 如果同一 plan 在不同机器、相同输入和相同 registry 下生成不同 `plan_hash`，这是 schema-lint / migration registry bug，不允许 apply。

diff entry 默认字段：

```text
diff_id
step_id
operation_kind: create_sheet | rename_sheet | add_column | move_column | rename_field_display | rewrite_cell | move_cell_range | delete_cell_range | default_fill | metadata_update | helper_preserve | child_row_create | child_row_delete | child_row_move
workbook_guid
workbook_path
table_id
field_id optional
row_guid optional
row_local_id optional
sheet_name
cell_or_range
old_canonical_value_hash optional
new_canonical_value_hash optional
data_loss_risk
requires_confirmation_id optional
```

diff entry 默认规则：

- `diff_id` 按 step order、operation kind、table id、field id、row identity、cell/range deterministic 生成；不能用递增内存序号。
- data cell rewrite 必须带 old/new canonical value hash；只移动结构或刷新显示的 diff 不伪造 value change。
- `helper_preserve` 记录被保护的 helper/freeform range，用于证明 migration 没覆盖策划手写区域。
- delete / narrowing / lossy parse / abandoned unknown data 必须设置 `data_loss_risk=true` 并绑定 confirmation id。
- diff entry 里的 workbook path 只是报告定位；验证以 workbook guid + source fingerprint 为准。

dry-run 默认规则：

- dry-run 不写 workbook、不更新 metadata、不替换 runtime source。
- dry-run 必须构建 candidate diff，并输出将新增、移动、重命名、改写、删除、默认填充的数据量。
- 每个会改正式数据的 diff 都必须能定位到 workbook、sheet、row guid/row number、field id、cell/range。
- dry-run 必须执行 parse、type conversion、reference resolution、unique key、required/default、validator、convert blocker 检查。
- dry-run 必须检查文件锁、写权限、backup/temp/recovery manifest 可创建性。
- `data_loss_risk=true` 的 step 必须列出丢弃或不可逆转换的字段、cell/range、行数和原因。
- dry-run report 是 apply 的输入承诺；apply 时 source revision / schema hash / descriptor hash 变化则必须重新 dry-run。
- dry-run 生成的 `recovery_preflight` 必须说明 backup path、temp path、recovery manifest path 是否可创建，以及 atomic replace 是否可用。

apply 默认规则：

- apply 必须走 operation transaction 和 workbook backup / atomic save / recovery 协议。
- 多 step migration 默认先在 candidate workbook/source 上顺序 apply，全部 verify 通过后再 commit；不能 step 1 写回成功、step 2 失败后留下半升级 workbook。
- migration 只能写 descriptor 声明的 schema-owned structure region、metadata region、目标 data cell/range 或明确声明的 helper/freeform 保留区。
- apply 前必须复查 `plan_hash`、source fingerprint/revision、schema hash、layout hash、descriptor hash、migration descriptor hash、dry-run report hash 和 data loss confirmations。
- 任一 required confirmation 缺失、confirmation id/hash 不匹配或 policy 不允许时产生 `migration.data_loss_confirmation_required`，不写 workbook。
- apply 后必须重新打开 candidate workbook，用 current schema 完整 import，并重新运行 compatibility analyze、validation、reference graph check。
- apply 成功后更新 workbook schema hash / layout hash / field mapping，并写入 migration history record。
- apply 失败时不替换 runtime source，不发布成功 ChangeSet；如果 workbook 已进入 replace 阶段，按 recovery manifest restore 或进入 manual recovery diagnostic。

幂等默认规则：

- migration selection 以 metadata 中的 current schema hash 为准；如果 workbook 已经是 target schema，默认 skip。
- 如果 migration history 显示某 step 已应用，且 descriptor hash、from/to schema hash、plan hash、dry-run report hash 与当前 workbook verify 结果一致，则该 step skip。
- 如果 history 存在但当前 schema hash / layout hash / field mapping verify 不一致，报 `migration.history_inconsistent`，进入 recovery/manual review。
- migration apply body 仍必须有 precondition guard；不能在重复执行时追加重复列、重复 child rows 或重复 metadata。
- migration history 只追加成功 step；失败 step 只能出现在 recovery/report 中，不能写成成功历史。

rollback / reverse 默认规则：

- apply 发布前的默认 rollback 是 restore backup，而不是执行反向业务逻辑。
- apply 发布成功后，默认不提供自动回滚；需要回到旧 schema 时必须定义新的 reverse migration，并同样 dry-run / apply / verify。
- data loss migration 即使有 backup，也不能被 UI 表示为“可无成本撤销”；必须显示不可逆风险和 backup 位置。

host requirement 默认规则：

- `host_requirement=core` 的 migration 可在宿主无关 CLI、Unity adapter 和 CI 中执行。
- `host_requirement=unity_editor` 的 migration 只能在 Unity 2022.3 adapter 可用时执行，例如需要 Unity AssetDatabase 解析资源 guid 的迁移。
- CI 遇到 unavailable host requirement 默认 exit code 2 或 3，不能降级成 warning 后继续 convert。
- 自定义 editor migration 只能引用 schema migration id 或产生 migration proposal；最终仍由本协议执行。

runtime 默认规则：

- `RuntimeDatabase.Open/Switch/Refresh` 不执行 workbook migration。
- runtime 可以加载显式兼容的旧 converted bytes adapter，但必须由 schema descriptor 声明；默认 schema hash 不兼容就拒绝并保留旧 source。
- Development hot reload 遇到 `migration_required` 只报告并保留旧 object graph，不在运行中改 Excel 表结构。

各操作的默认处理：

- `OpenWorkbook`：允许 `compatible`、`compatible_with_layout_refresh`、`import_allowed_convert_blocked` 打开；`migration_required` 只能 read-only 打开或进入 migration wizard；`incompatible` 拒绝作为有效 source。
- `GenerateStructure`：只自动执行 layout refresh 和安全列补齐；不自动执行会改正式数据语义的 migration。
- `Import`：可导入 `import_allowed_convert_blocked`，但 object / asset 状态必须带 error；不能导入 `incompatible` 为有效 object set。
- `SaveAssets`：如果 schema diff 仍是 `migration_required`、`incompatible` 或有 unresolved data-loss confirmation，拒绝保存。
- `ConvertBytes` / CI / build：只允许 `compatible` 或已经完成 migration 后的 schema；任何 `import_allowed_convert_blocked` 都失败。
- `RuntimeDatabase.Open/Switch`：schema hash 不兼容时保留旧 source；只有显式 runtime compatibility adapter 才能读旧 bytes。

#### 14.18.3 默认 compatibility gate / operation target 矩阵

`compatibility analyze` 只回答“当前 workbook/bytes 与当前 schema 的关系”。每个具体操作还必须把这个结果映射成 deterministic `CompatibilityGateResult`，否则 Editor、CLI、runtime、custom editor 会自然长出不同宽松度。

`CompatibilityGateResult` 默认字段：

```text
operation_target
compatibility_level
access_mode: editable | editable_with_errors | read_only | table_view | migration_wizard | report_only | reject
allowed_write_regions[]: schema_owned_structure | metadata | data_cells | diagnostic_projection | none
allowed_publish_targets[]: editor_status | editor_object_set | converted_bytes | runtime_source | none
required_operation_plan_hash optional
required_migration_plan_hash optional
required_layout_refresh_plan_hash optional
required_confirmation_hashes[]
blocked_capabilities[]
diagnostic_ids[]
```

默认原则：

- gate 只能收紧 compatibility analyze 的结论，不能放宽。profile 可以把 warning 升级为 error/blocker，但不能把 `migration_required` 改成 editable。
- gate 必须在 Build candidate 前产生，并写入 operation report。后续 preflight/commit/apply 必须验证 gate 绑定的 source revision、schema/layout hash、profile hash 和 plan hash 未变化。
- `access_mode=editable_with_errors` 表示可以继续做 authoring 修复和保存，但不能 convert/build/runtime publish；错误资产必须在 status/diagnostic view 中保持可见。
- `read_only` 表示允许用当前可证明的 mapping 展示数据和诊断，但任何 `SerializedProperty` setter、direct mutation、metadata repair、layout refresh、SaveAssets 都必须失败或转入显式 operation。
- `table_view` 表示 generic 表格可显示 raw/canonical/diagnostic；custom editor 不满足 capability 时不能继续编辑。
- `migration_wizard` 只允许生成 migration dry-run / plan / confirmation；不能在 wizard UI 中直接写 workbook。
- `report_only` 只能输出 report/status，不创建 resident object set，不替换 runtime source，不修改 workbook。

operation target 默认集合：

```text
editor_open
custom_editor_open
generate_structure
layout_refresh
editor_import
save_assets
convert_bytes
ci_check
runtime_open
runtime_switch
runtime_hot_reload
migration_dry_run
migration_apply
merge_workbook
```

默认 gate 矩阵：

| compatibility level | editor open / generic table | custom editor | structure/layout operation | editor import/status | SaveAssets | convert/CI/build | runtime open/switch/hot reload |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `compatible` | `editable` | capability 通过则 `editable` | no-op 或安全 refresh | 可建立有效 editor object set | 可保存 dirty data/metadata | 允许 | 允许 publish/patch |
| `compatible_with_layout_refresh` | `editable`，带 layout stale 诊断 | 若 editor 不依赖 stale layout 则 `editable`，否则 `table_view`/`read_only` | 只允许 schema-owned layout patch | 可导入；mapping 以 table/field id 为准 | 可保存不与 stale layout 冲突的 dirty；layout refresh 只能显式或 profile 允许时同 transaction 提交 | 允许，除非 export/editor capability 声明 `requires_physical_column_order=true` 且 refresh 未完成 | 允许数据等价 publish；runtime 不写 layout |
| `import_allowed_convert_blocked` | `editable_with_errors` | 只有声明可处理 invalid authoring state 的 editor 可编辑，否则 `table_view`/`read_only` | 只允许不改变数据语义的安全 layout/metadata patch | 可建立带 error status 的 editor object set；invalid raw value 只能按 raw-preserve 规则显示 | 可保存可定位、可确定写回的 authoring 修复；不要求所有 validation error 当场清零；不能覆盖 invalid raw cell | 失败 | 不发布 candidate；保留旧 runtime source/object graph |
| `migration_required` | `read_only` 或 `migration_wizard` | `read_only` / `table_view` / reject open | 只能生成 migration dry-run 或安全 report；不做普通 layout refresh | 只建立 migration/status preview，不作为新语义有效 object set | 失败；除非当前操作就是 `migration_apply` | 失败 | 失败并保留旧 source/object graph |
| `incompatible` | `reject` 或 `report_only` | reject open | 不写 workbook | 不建立有效 object set | 失败 | 失败 | 失败并保留旧 source/object graph |

`compatible_with_layout_refresh` 细化规则：

- 旧表头、旧 data validation、旧 helper sheet 不能作为事实源；导入、保存和 convert 仍以 current schema descriptor + metadata field id mapping 为准。
- 如果 layout refresh patch 与用户 helper/freeform 区、正式数据 cell、formula/comment ownership 无法证明不重叠，gate 对该 layout operation 升级为 blocker，但不自动阻断纯数据读取。
- `SaveAssets` 默认不顺手执行 `CleanupDeprecatedLayout`、批量 default materialization 或 data-loss cleanup；即使同 transaction 执行 safe layout refresh，也只能写 schema-owned structure/metadata/generated artifact。
- runtime 打开的 Excel source 可以接受 layout stale candidate，只要 source equivalence 和 canonical data hash 证明运行时数据等价；runtime 绝不因为 layout stale 写回 Excel。

`import_allowed_convert_blocked` 细化规则：

- 这一级主要服务日常编辑：例如新增 required 字段后，策划需要先打开表、补数据、保存，再让 convert 通过。
- `SaveAssets` 是 authoring persistence gate，不是 build correctness gate。它可以保存仍然带 validation error 的 workbook，但保存报告必须保留 `affects_convert=true` 的 diagnostics。
- 如果某个 cell 无法 parse 成可定位的 canonical/invalid state，或该 save 会覆盖未解析 raw value，affected cell 必须只允许显式用户编辑后的新值；不能由 object state 默认值覆盖 raw Excel 内容。
- custom editor 若没有声明 invalid-state editing capability，只能显示 diagnostic/table view；不能拿缺字段、未知 enum、missing hard reference 的对象继续执行复杂业务编辑逻辑。
- 运行中 hot reload 遇到这一级只更新 editor/status report；runtime candidate 不 publish，不发 property_changed/dependency_changed。

`migration_required` 细化规则：

- 普通 `Refresh`、`SaveAssets`、`GenerateStructure`、`RuntimeDatabase.SwitchDataSource` 都不能把 migration 当副作用执行。
- `migration_dry_run` 可以读取 workbook、生成 `MigrationPlan`、计算 data-loss confirmation 和 recovery preflight，但不写 workbook、不创建 runtime candidate。
- `migration_apply` 是唯一能把这一级推进到新 schema 的默认操作；apply 成功后必须重新 import、compatibility analyze、validation、reference graph verify，结果不满足目标 gate 时恢复/失败。
- 如果 previous descriptor snapshot 缺失或不可信，只能 `read_only/report_only` 打开；禁止自动 migration apply、批量 save、destructive layout rewrite。
- migration preview 中展示的旧数据不能被当作 current schema resident object；引用 picker、FindAssets、runtime event、converted bytes 都不能消费它。

post-commit / publish 复验默认规则：

- `SaveAssets`、`migration_apply`、`merge_workbook`、`layout_refresh`、`metadata_flush` 成功写文件后，必须重新读取目标 workbook，重算 source fingerprint、metadata checksum、compatibility gate、validation 和 reference graph。
- 如果 operation 目标承诺产出 `compatible`，复验后仍是 `import_allowed_convert_blocked` 或更高，commit 失败并按 recovery 协议恢复或进入 manual recovery。
- 如果 operation 目标只承诺 authoring save，复验后允许停在 `import_allowed_convert_blocked`，但 report/result/status 必须保留 convert/build blocker。
- runtime publish 只接受复验后的 candidate；任一 gate 在 publish 前变 stale，产生 `transaction.plan_stale` / `transaction.source_revision_changed`，旧 source/object graph 继续服务。

### 14.19 默认 Object instance / lifecycle / ChangeSet 协议

Object identity 和 object instance 必须分开。identity 是存盘身份，instance 是某个 database context 内的运行时承载对象。

默认 identity：

- asset identity：workbook guid + table id + row guid/local id。
- `DatabaseContextId`：session-local context identity，用于隔离 object pool、dirty draft、operation queue、snapshot pin 和 report；不写 workbook，不进入 converted bytes。
- asset path：identity 的当前可读定位，不参与 identity 判断。
- `Object.name`：当前显示名，可随数据变化更新。
- `GetInstanceID()`：database context 内的 session-local instance id，不写入 workbook，不进入 converted bytes。

默认 instance cache：

- 同一个 `AssetDatabase` / `RuntimeDatabase` context 中，同一个 asset identity + runtime type 只允许一个 canonical resident instance。
- 重复 `LoadAssetAtPath<T>`、`LoadAsset<T>` 或引用解析，返回同一个 canonical instance。
- `FindAssets` 只返回 guid，不强制 materialize object instance。
- object slot 可以在 import/bake 阶段预建；真正实例可 lazy materialize，但 materialize 后默认保持 resident，直到 source close、load set unload、context dispose 或 recreate/remove 事件。
- load set 卸载可以让实例进入 `unloaded`；这不是删除资产，identity/key 仍留在 core index。
- 不同 database context 可以有不同 instance id；不能跨 context 比较 instance id。
- 不同 context 的 resident object 即使 asset identity 相同，也不能互相作为 `SerializedObject` target、reference setter target、dirty target 或 runtime object result；需要跨 context 传递时只传 `AssetIdentity` / stable key，再在目标 context 查询。
- `RuntimeDatabase.Close()` 只关闭当前 source session，不销毁 context；旧 resident object 进入 `unloaded`，旧 `AssetKey` / object handle 不能在无 source 状态下读取，但同一 context 重新 `Open` 后可以按 identity 重新解析。
- `RuntimeDatabaseContext.Dispose()` / host 销毁 context 后，未被 snapshot pin 保留的 resident object 进入 `context_closed`；旧 object handle 不能被新 context 复用。

默认 patch / recreate / remove：

- row guid/local id 不变且 runtime type 不变时，优先 patch 原 instance，`GetInstanceID()` 不变。
- key/path/name 变化只更新索引和显示信息，不 recreate object。
- 字段值变化 patch 后发 `property_changed`。
- reference graph 变化发 `dependency changed`，并更新 reverse dependency index。
- schema 或 type 变化导致无法 patch 时，发 `recreated`；commit 后 `LoadAsset` 返回新 instance，旧 instance 进入 `stale_recreated` 状态。
- row 被删除时，发 `removed`；commit 后按 path/key/guid 正常加载返回 null 或 missing diagnostic，旧 instance 进入 `removed` 状态。
- 所属 load set 被卸载时，发 `source_summary` / load_set_summary；已 materialized wrapper 进入 `unloaded`，重新 `LoadSet` 后可按同 identity 重新 materialize，但不保证复用旧 instance id，除非 pin policy 保留 wrapper。
- removed / recreated 的旧 instance 不允许被重新指向新行；identity tombstone 至少保留到本次 ChangeSet 发布完成。

默认 object state：

```text
transient
temporary_draft
resident
dirty
missing
removed
stale_recreated
unloaded
context_closed
```

读取规则：

- `transient` object 是 `ScriptableObject.CreateInstance` 或等价 factory 创建、尚未绑定 source 的对象；只能用于 `CreateAsset` / editor draft，不进入 runtime source。
- `temporary_draft` object 已在 editor draft 中创建最终 row guid/local id，但尚未 flush 到 workbook metadata；Editor authoring 可查到，convert/runtime 不可见。
- `resident` object 可以正常读取字段。
- `dirty` object 可以读取 editor draft；runtime read-only mode 不产生 dirty。
- `missing` / `removed` / `stale_recreated` / `unloaded` / `context_closed` object 在 editor/debug 下必须能报告 diagnostic；正式 runtime 默认只保留最小错误码。
- `unloaded` object 不能读取 payload field；需要先 `LoadSet` / `PrewarmLoadSet`，或用 identity 查询 metadata/dependency summary。
- `context_closed` object 不能被新 context 复用；任何读取、dirty、reference setter 或 runtime 查询都必须失败。

#### 14.19.1 默认 Object / ScriptableObject / generated asset type 协议

`Object` 是 Unity-like 对外模型的根，不是普通 DTO。它承载 session-local instance、context、asset identity、object state 和 direct mutation 兼容路径；具体配置类型由 schema/codegen 生成，继承 `ScriptableObject` 或 `Object`，字段访问仍由 generated accessor / descriptor binding 驱动。

Object / ScriptableObject 权威 API surface：

```csharp
namespace ExcelDbEngine
{
    [Flags]
    public enum HideFlags
    {
        None = 0,
        HideInHierarchy = 1,
        HideInInspector = 2,
        DontSaveInEditor = 4,
        NotEditable = 8,
        DontSaveInBuild = 16,
        DontUnloadUnusedAsset = 32,
        DontSave = DontSaveInEditor | DontSaveInBuild | DontUnloadUnusedAsset,
        HideAndDontSave = HideInHierarchy | DontSaveInEditor | NotEditable | DontSaveInBuild | DontUnloadUnusedAsset
    }

    public enum ObjectState
    {
        Transient,
        TemporaryDraft,
        Resident,
        Dirty,
        Missing,
        Removed,
        StaleRecreated,
        Unloaded,
        ContextClosed
    }

    public abstract class Object
    {
        public string name { get; set; }
        public HideFlags hideFlags { get; set; }
        public AssetIdentity assetIdentity { get; }
        public DatabaseContextId contextId { get; }
        public ObjectState objectState { get; }
        public bool isValid { get; }

        public int GetInstanceID();
        public override bool Equals(object obj);
        public override int GetHashCode();
        public override string ToString();

        public static bool operator ==(Object lhs, Object rhs);
        public static bool operator !=(Object lhs, Object rhs);
    }

    public abstract class ScriptableObject : Object
    {
        public static T CreateInstance<T>() where T : ScriptableObject;
        public static ScriptableObject CreateInstance(Type type);
    }
}
```

Object API 默认规则：

- `Object` / generated asset class 不能由业务 `new` 出有效 resident instance；新建资产默认走 `ScriptableObject.CreateInstance<T>()` -> `AssetDatabase.CreateAsset()` -> `SaveAssets()`。
- `CreateInstance<T>()` 只创建 `Transient` object，分配 temporary instance id，但没有 workbook guid、row guid/local id 和 asset path；`CreateAsset` 成功进入 `TemporaryDraft`。
- `assetIdentity` 只有绑定 source/draft 后有效；`Transient` object 的 `assetIdentity` 是 empty identity，不能进入 reference field 或 runtime index。
- `contextId` 表示创建或绑定该 object 的 context；跨 context 使用必须按 14.2.1 失败。
- `isValid` 对 `Transient`、`TemporaryDraft`、`Resident`、`Dirty` 返回 true；对 `Missing`、`Removed`、`StaleRecreated`、`Unloaded`、`ContextClosed` 返回 false。
- `obj == null` / `obj != null` 默认提供 Unity-like validity 兼容：actual null 或 `isValid=false` 的 object 与 null 比较为 true；需要区分 actual null 时调用方使用 `ReferenceEquals(obj, null)`。
- 两个非 null object 的 `==` 只在同 context、同 instance id、同 generation 时为 true；不同 context 即使 `assetIdentity` 相同也不相等。
- `Equals(object)` 默认只接受 `ExcelDbEngine.Object` 或 `null`；对 `null` 的结果与 `obj == null` 保持一致，对非 Object 返回 false。
- `GetHashCode()` 使用 context-local instance id + generation 的 stable hash；object 进入 removed/stale/unloaded/context_closed 后 hash 不变，不能使用 asset path、row number、display name 或当前字段值。
- null/validity operator、`Equals`、`GetHashCode`、`isValid`、`objectState`、`GetInstanceID()` 都不得分配；`ToString()` 是 debug/low-frequency API，可以返回 name/path/state 摘要，不是 runtime hot path。

`Object.hideFlags` 默认规则：

- `hideFlags` 使用 Unity-like flag 名称，但在 ExcelDB core 中只是 context-local editor/session presentation flag，不是 schema、metadata、SaveAssets、convert、source hash 或 runtime export policy。
- `hideFlags` 默认值是 `None`；可以在 Transient、TemporaryDraft、Resident、Dirty object 上设置，但不写入 workbook，不进入 converted bytes，不影响 `assetIdentity`、`GetInstanceID()` 或 `source_hash`。
- `DontSaveInEditor` / `DontSaveInBuild` / `DontSave` 不能阻止 ExcelDB 保存 schema-backed dirty draft；是否保存、导出或 strip 只由 schema、OperationProfile、dirty/conflict/preflight 决定。
- `HideInHierarchy` / `HideInInspector` / `NotEditable` 只影响默认 editor browser / generic inspector / drawer presentation；custom editor 仍必须遵守 schema readonly、editor capability 和 SerializedObject 写入规则。
- `DontUnloadUnusedAsset` 不改变 load set unload、snapshot pin 或 provider handle policy；卸载由 14.9.3 的 load set / pin 规则决定。
- Unity adapter 可以把 `ExcelDbEngine.HideFlags` 投影到 Unity proxy object 的 `UnityEngine.HideFlags`，但不得反向把 Unity hide flags 当作 ExcelDB source truth。
- source close、context dispose、source switch recreate 或 object abandoned 时，session-only `hideFlags` 可以丢弃；如果项目需要持久隐藏/只读/导出策略，必须写入 schema/editor visibility/profile，而不是依赖 `hideFlags`。

`Object.name` 默认规则：

- `name` getter 来自 schema 声明的 display name field；没有 display name 时使用 canonical key；再没有时使用 `{table_schema_name}/{row_guid}`。
- `name` getter 使用 stable string pool 或预生成 display string；runtime hot path 重复读取不得分配，除非 profile 明确把 `name` 标为 debug-only allocating display API。
- `name` setter 是 direct mutation 兼容入口，仅在 editor authoring context 且 schema 能把 display name lossless 映射到可编辑 field 时有效。
- `name` setter 不直接写 workbook；它写入 direct mutation overlay，之后必须通过 `EditorUtility.SetDirty(obj)` 进入 editor draft。等价编辑也可以从一开始使用 `SerializedObject`，但 `SerializedObject.ApplyModifiedProperties()` 不接管已经发生的 direct mutation overlay。
- runtime read-only、unloaded、removed、stale、context_closed、无 display field 或 display field readonly 时，`name` setter 不改变对象，并记录 `assetdb.object_mutation_not_mappable` 或 `assetdb.object_state_invalid`。

generated asset type 默认规则：

- 每个 schema asset table 生成一个 `partial` C# asset type，默认继承 `ScriptableObject`；非 asset/embedded child view 可以生成 `struct view` 或内部 row wrapper，不默认暴露为可创建 root asset。
- generated class 的 runtime type identity 来自 table id + generated type id + schema descriptor hash；不能只靠 C# full name。
- generated field/property 名称按 C# 命名规则生成，但稳定绑定只使用 field id；rename display/member name 不改变 field identity。
- generated property getter 是 no-GC 主读取面；setter 是否生成由 mode/profile/schema mutability 决定。
- Release / Development read-only profile 默认不生成 public setter，或 setter 固定失败并写最小 report；不能静默改 resident data。
- Editor authoring profile 可以生成 public setter 作为 direct mutation 兼容路径；setter 只改 direct mutation overlay，不写 Excel、不更新 editor draft、不发布 ChangeSet；进入 draft 必须调用 `EditorUtility.SetDirty(obj)`。
- `SerializedObject` 路径是推荐编辑入口；direct mutation + `SetDirty` 只是兼容 Unity 习惯，必须能被禁用或通过 analyzer/CI report 标记。
- generated class 可以是 `partial` 以允许用户写只读 helper method；用户 partial 不能声明与 schema field 同名的 settable property、不能改 identity/state/context、不能访问内部 storage 指针。
- generated class 不暴露 public mutable `List<T>`、array field、dictionary 或 backing field；collection 编辑必须走 `SerializedProperty`、child table operation 或 explicit low-frequency copy API。

direct mutation / SetDirty 默认规则：

- generated setter、`Object.name` setter 或用户 partial helper 若改变 schema-backed value，必须写入 direct mutation overlay 并递增 object mutation revision。
- `Undo.RecordObject(obj, name)` 捕获 direct mutation 前的 schema snapshot；没有 RecordObject 时，`SetDirty` 仍可保存，但 report/Undo 栈记录 `undo.no_record`。
- `EditorUtility.SetDirty(obj)` 从 base snapshot + current direct mutation overlay 计算 canonical property diff，进入 editor draft、dirty state、validation 和 conflict 流程。
- `SetDirty` 成功后可以清理 direct mutation overlay；失败时 overlay 保留给用户修复或显式 `Refresh/Revert`，不能半写 workbook。
- direct mutation overlay 不参与 convert/runtime index；未 `SetDirty` 的 direct mutation 在 `Refresh`、source switch、source close、context dispose 或 object recreate 时可以被丢弃，但必须在 editor/debug report 中可见。
- 如果 direct mutation 不能映射到 schema field/cell、违反 readonly/capability、命中 stale mapping 或需要数据丢失确认，`SetDirty` 失败并记录 `assetdb.object_mutation_not_mappable` / `serialized.mapping_changed` / validation diagnostic。
- runtime context 不存在 direct mutation overlay；任何 setter/direct mutation API 都必须失败、no-op + report 或在生成阶段不可见。

默认 ChangeSet：

```text
source_identity
source_revision_before
source_revision_after
schema_hash_before
schema_hash_after
events[]
```

这里的 `events[]` 是概念字段；运行时 API 默认暴露为 non-alloc event view，至少提供 `Count`、`ref readonly` indexer 和不分配的 struct enumerator，不在 publish 时为订阅者复制数组。

event 默认字段：

```text
kind
asset_identity
runtime_type
old_path
new_path
old_key
new_key
old_instance_id
new_instance_id
field_ids
property_paths
dependency_edges
diagnostic_codes
```

API 表达默认规则：

- public API 使用 `ChangeSet`、`ChangeEventList` 和 `ChangeEvent` struct view；`events` 不暴露 `T[]`、`List<T>` 或 allocating `IEnumerable<T>`。
- `ChangeEvent.assetIdentity` 是事件目标；root asset 事件中 `ownerIdentity == assetIdentity`，embedded child / sub-asset / child table event 中 `assetIdentity` 是 child element identity，`ownerIdentity` 是所属 root asset identity，`parentFieldId` 指向 owner 上的 child field。
- `RuntimeTypeId`、`FieldId`、`PropertyPathId` 都是 descriptor/generation 绑定的 opaque id；它们不是 table number、column index、header text 或 C# property string。
- `changedFieldCount`、`changedPropertyPathCount`、`changedDependencyCount` 只给调用方预估 buffer；具体内容必须通过 `ChangeSet.GetChangedFields`、`GetChangedPropertyPaths`、`GetChangedDependencies` 写入调用方 Span。
- buffer 不足时返回 `RuntimeQueryStatus.Truncated`，`count` 是完整需要数量；不能为了返回完整列表分配数组或字符串。
- `GetChangedFields` / `GetChangedPropertyPaths` / `GetChangedDependencies` 只能接受同一个 `ChangeSet` 回调 view 中取得的 `ChangeEvent`；跨 context、跨 revision、回调结束后复用 event handle 返回 `RuntimeQueryStatus.InvalidHandle` 或 debug assertion，不分配 report/list。
- `PropertyPathId` 是 editor/debug mapping id；Release runtime 可以没有 property path payload，此时 `changedPropertyPathCount=0`，业务缓存必须依赖 `FieldId` / `AssetIdentity`。
- old/new path、old/new key、diagnostic message 和 human-readable source path 属于 debug/report projection；ChangeEvent 只暴露是否存在这些 display payload 的 flag，取具体字符串必须走低频 report/debug API。
- `GetAffectedAssets` 对同一个 `AssetIdentity` 去重并按 asset identity stable sort；它用于业务缓存批量失效，不替代逐 event 处理。
- `ChangeSet.revisionBefore` / `revisionAfter` 必须和 RuntimeSnapshot revision 对齐；回调中保存 `AssetIdentity + revisionAfter` 后，下一帧可以用新 snapshot 重查。
- `ChangeSet` 是 borrowed view，不是可收藏对象；保存 `ChangeSet`、`ChangeEventList`、`ChangeEvent` 或 slice view 到回调外属于 invalid handle 使用。需要持久化时只保存 stable identity、field id、event kind、revision 和自己的业务 payload。
- `ChangeSet` 不提供 allocating convenience collection；任何 `ToArray()`、LINQ、string path 展开、debug message 构建只能属于显式 debug/report projection API，不能挂在 hot-path-safe surface 上。

event kind 默认集合：

```text
added
removed
moved
renamed
recreated
property_changed
dependency_changed
source_summary
```

发布规则：

- ChangeSet 只在 commit 成功后发布；失败 transaction 不发布成功事件，只发布 failure report。
- 发布顺序固定：removed、added、moved、renamed、recreated、property_changed、dependency_changed、source_summary。
- 每次 publish 先发布完整 `changed(ChangeSet)`，再按同一 ChangeSet 派生 `objectChanged` convenience event；`objectChanged` 的存在与否不影响 ChangeSet digest、event 顺序或业务缓存失效语义。
- `changed` 回调期间读 API 已经指向 `revisionAfter`；旧值只通过旧 snapshot、event hash/id 或 debug report 查询，不能从 removed/stale object 上继续读取 payload。
- subscriber list 对当前 publish 冻结；回调中订阅/退订、开启/关闭 hot reload 或请求 `Refresh/SwitchDataSource/Open/Close` 只进入后续 operation，不改变当前事件序列。
- 同一个 object 的多个 property change 默认 coalesce 到一个 event，field_ids / property_paths 按 schema order 排序。
- 同一 asset 在一次 ChangeSet 中最多有一个 location event：canonical asset path 改变时发 `moved`；path 不变但 display name / primary key display 改变时发 `renamed`；`moved` event 可以携带 old/new key/name debug payload，不再额外发 `renamed`。
- key field canonical value 改变但 schema 也把该字段导出为业务 runtime field 时，除 `moved/renamed` 外还必须产生 `property_changed`；如果 key 只作为 path/index，不作为 runtime field，则只发 location event。
- reference edge set、dependency kind、target runtime provider key 或 target canonical value 影响 referrer closure 时，发布 `dependency_changed`；同一字段既改值又改依赖时同时在 property/dependency 事件中出现，但 field id 排序一致。
- child table insert/delete/reorder 默认以 child identity 为 event target；如果 owner 的 generated getter 展示的是 baked aggregate/range view，owner 也发布 `property_changed` 或 `dependency_changed`，具体由 child field descriptor 的 `owner_event_policy` 决定。
- subscriber 回调异常不能回滚已经 commit 的数据；异常产生 `runtime.subscriber_failed` report，并继续或停止由订阅策略决定，默认继续通知其他 subscriber。
- subscriber 回调中触发新的 refresh/switch/save 默认排队到当前 publish 完成后执行，禁止重入修改当前 ChangeSet。
- subscriber 试图同步触发嵌套 publish、修改当前 source 或持有 ChangeSet view 跨回调使用时，产生 `runtime.publish_reentrant` / `RuntimeQueryStatus.InvalidHandle`；不能静默执行。
- runtime 热路径发布使用预分配 ChangeSet / event buffer；超过水位线时可扩容但必须记录 allocation report。

缓存失效默认规则：

- 业务层不需要猜哪些缓存失效；至少可以订阅 table、type、asset identity、dependency graph 四种粒度。
- `property_changed` 影响当前 asset 的派生缓存。
- `dependency_changed` 影响 referrer 和 dependency closure；dependency closure 计算使用 reverse dependency index。
- `removed` / `recreated` 默认使 referrer 的 dependency cache 失效。
- `moved` / `renamed` 只影响 path/key/name cache，不影响 object identity cache。

#### 14.19.2 默认 runtime patch plan / ChangeSet event generation 协议

hot reload 和 source switch 的 commit 不是“边 diff 边改对象”。默认先从 current snapshot 和 candidate snapshot 生成 patch plan，验证完整 plan 后，再在 owner publish point 原子提交。

patch plan 默认字段：

```text
plan_id
source_identity_before
source_identity_after
source_revision_before
source_revision_after
schema_hash_before
schema_hash_after
layout_hash_before
layout_hash_after
capacity_required
object_patches[]
index_patches[]
dependency_patches[]
event_buffer_plan[]
diagnostics[]
```

object patch kind 默认集合：

```text
add
remove
patch_fields
move_or_rename
recreate
mark_missing
source_summary
```

diff key 默认规则：

- object diff 只按 `asset identity + runtime type id` 匹配。
- key/path/name、row number、object display text、Excel sheet name 都不能作为 object diff identity。
- child table element 按 owner asset identity + parent field id + element guid/local id 匹配。
- reference diff 按 target asset identity / Unity guid / provider runtime key 匹配，不按显示 key/path。

patchable 默认条件：

- asset identity 不变。
- runtime type id 不变。
- generated field binding id/version 兼容。
- value shape codec 可以从 candidate canonical value 写入当前 resident object。
- field mutation 不要求重新构造对象内部不可变布局。
- object 当前状态是 resident 或 dirty editor draft 可合并状态；removed/stale_recreated 不能被 patch 回 resident。

recreate 默认条件：

- runtime type id 改变。
- generated binding 不兼容，字段 layout 无法 patch。
- object construction invariant 需要重跑，且 schema/runtime type 声明 `requires_recreate_on_change=true`。
- field value shape 变化没有 migration/runtime adapter。
- custom runtime provider / codec 声明该字段变化需要 recreate。

commit 默认顺序：

1. 验证 patch plan 仍匹配 current source revision、schema hash、identity map 和 registry manifest。
2. reserve object slots、index entries、dependency edges、ChangeSet/event buffer；不足则水位线增长并记录 allocation_summary。
3. mark removed / recreated old instances 为 transitional state，并保留 tombstone。
4. 创建 added / recreated new instances，但暂不发布给 lookup index。
5. patch existing resident fields，使用 generated accessor，不走反射。
6. 构建 next key/path/guid/reference/dependency index。
7. 校验 next index 没有 duplicate key、dangling hard reference、slot mismatch。
8. 原子切换 current source、object slot table、indexes、dependency graph。
9. 生成并发布 ChangeSet view。
10. 发布结束后清理本次 transitional tombstone 和临时 patch buffer；removed tombstone 至少保留到 ChangeSet 生命周期结束。

rollback 默认规则：

- 在第 8 步之前失败，不改变 current source/index；已创建的新实例丢弃或进入 abandoned internal state，不对外可见。
- 在第 8 步之后 subscriber 失败不回滚数据；subscriber 异常进入 report。
- 如果 patch existing resident field 期间失败，必须使用 pre-patch snapshot 或 staged field buffer 恢复；不能留下半 patch object。
- patch plan 中任何 object patch 失败，整个 source switch / hot reload commit 失败，不发布成功 ChangeSet。

event generation 默认规则：

- event 从 patch plan 生成，不从 subscriber 回调中临时推断。
- 同一 asset identity 的多字段变化 coalesce 成一个 `property_changed` event，`field_ids` 按 schema field order 排序。
- key 改变产生 `renamed`；asset path 改变产生 `moved`；两者可以 coalesce 到同一 identity 的 moved/renamed event，但不替代 property_changed。
- reference target set 变化产生 `dependency_changed`，并且 referrer closure 通过 reverse dependency index 可追踪。
- removed object 不再额外发布 property_changed。
- recreated object 默认发布 `recreated`，不再额外发布 removed+added，除非 debug report 需要展开细节。
- added object 如果同时有 dependency edges，只发布 `added` + 必要 `dependency_changed`，不发布 property_changed。
- source hash 一致的 Excel/bytes no-op switch 只允许发布 `source_summary` 或不发布事件，由 mode/report policy 决定；不得发布 property_changed。

source_summary event 默认字段：

```text
affected_workbook_guids[]
affected_paths[]
source_hash_before[]
source_hash_after[]
schema_hash_before
schema_hash_after
source_kind_before
source_kind_after
no_op
failure_code optional
```

ChangeSet event view 默认规则：

- `field_ids`、`property_paths`、`dependency_edges` 是 slice view，生命周期只到回调栈结束。
- `property_paths` 默认可以在 Release runtime 为空；业务应优先用 field id / asset identity。
- event 中的 old/new path/key 是 debug/display view；正式缓存应使用 stable identity。
- event buffer 排序固定：event kind order、table id、asset identity stable sort、field id order。

diagnostic code：

- `runtime.patch_plan_stale`：commit 前 current source/schema/registry 已变化，必须重新 plan。
- `runtime.patch_failed`：object patch / index patch / dependency patch 失败，旧 source 保留。
- `runtime.event_buffer_overflow`：ChangeSet event buffer 超过水位线并扩容或被 policy 阻止。

### 14.20 默认 AssetDatabase / Undo / EditorUtility facade 协议

Unity-like facade 默认优先匹配 Unity 的命名、返回值习惯和调用节奏；ExcelDB 特有能力通过 report 和 data source 类型表达，不把表模型泄漏给常规调用层。

AssetDatabase / Undo / EditorUtility 默认 API surface：

```csharp
namespace ExcelDbEditor
{
    public readonly struct GUID
    {
        // Unity-like GUID value wrapper for overload compatibility.
        // This wrapper carries ExcelDB AssetGuid, not Unity .meta GUID.
        // Canonical text form is lowercase 32 hex ExcelDB asset guid.
        // Unity asset GUIDs live in UnityResourceRef.guid / UnityGuid.
        // default(GUID) is the empty/invalid guid result used by failed lookups.
    }

    public readonly struct Hash128
    {
        // Unity-like 128-bit hash value wrapper.
        // Formatting/parsing follows lowercase 32 hex.
    }

    [Flags]
    public enum ImportAssetOptions
    {
        Default = 0,
        ForceUpdate = 1 << 0,
        ForceSynchronousImport = 1 << 1,
        ImportRecursive = 1 << 2,
        DontDownloadFromCacheServer = 1 << 3,
        ForceUncompressedImport = 1 << 4
    }

    [Flags]
    public enum ForceReserializeAssetsOptions
    {
        ReserializeAssets = 1 << 0,
        ReserializeMetadata = 1 << 1,
        ReserializeAssetsAndMetadata = ReserializeAssets | ReserializeMetadata
    }

    public enum AssetPathToGUIDOptions
    {
        IncludeRecentlyDeletedAssets = 0,
        OnlyExistingAssets = 1
    }

    public enum AssetMoveResult
    {
        DidMove,
        DidNotMove,
        FailedMove
    }

    public enum AssetDeleteResult
    {
        DidDelete,
        DidNotDelete,
        FailedDelete
    }

    [Flags]
    public enum RemoveAssetOptions
    {
        Default = 0,
        MoveAssetToTrash = 1 << 0
    }

    public abstract class AssetModificationProcessor
    {
        // Unity-like optional static message methods discovered by editor registry:
        // static string[] OnWillSaveAssets(string[] paths);
        // static void OnWillCreateAsset(string assetPath);
        // static AssetDeleteResult OnWillDeleteAsset(string assetPath, RemoveAssetOptions options);
        // static AssetMoveResult OnWillMoveAsset(string sourcePath, string destinationPath);
        // static bool MakeEditable(string[] paths, string prompt, List<string> outNotEditablePaths);
    }

    public enum DiagnosticSeverity
    {
        Info,
        Warning,
        Error,
        Blocker
    }

    [Flags]
    public enum AssetStatusFlags
    {
        None = 0,
        Imported = 1 << 0,
        DirtyEditor = 1 << 1,
        DirtyMetadata = 1 << 2,
        ExternalChanged = 1 << 3,
        Conflict = 1 << 4,
        ImportError = 1 << 5,
        ValidationWarning = 1 << 6,
        ValidationError = 1 << 7,
        ConvertBlocked = 1 << 8,
        MissingReference = 1 << 9,
        StalePath = 1 << 10,
        Removed = 1 << 11,
        TemporaryIdentity = 1 << 12
    }

    [Flags]
    public enum WorkbookStatusFlags
    {
        None = 0,
        Mounted = 1 << 0,
        DirtyEditor = 1 << 1,
        DirtyMetadata = 1 << 2,
        ExternalChanged = 1 << 3,
        Conflict = 1 << 4,
        ImportError = 1 << 5,
        ValidationWarning = 1 << 6,
        ValidationError = 1 << 7,
        ConvertBlocked = 1 << 8,
        SaveFailed = 1 << 9,
        MetadataRepairRequired = 1 << 10
    }

    public readonly struct AssetStatusRecord
    {
        public ExcelDbEngine.AssetIdentity assetIdentity { get; }
        public AssetStatusFlags flags { get; }
        public DiagnosticSeverity maxSeverity { get; }
        public int diagnosticCount { get; }
        public int conflictCount { get; }
        public int dirtyDiffCount { get; }
    }

    public readonly struct WorkbookStatusRecord
    {
        public WorkbookStatusFlags flags { get; }
        public DiagnosticSeverity maxSeverity { get; }
        public int assetCount { get; }
        public int dirtyAssetCount { get; }
        public int diagnosticCount { get; }
        public int conflictCount { get; }
    }

    public readonly struct AssetDiagnosticRecord
    {
        public string diagnosticId { get; }
        public string code { get; }
        public DiagnosticSeverity effectiveSeverity { get; }
        public ExcelDbEngine.AssetIdentity assetIdentity { get; }
        public int fieldId { get; }
        public string propertyPath { get; }
        public ConflictId conflictId { get; }
    }

    public readonly struct ConflictId
    {
        // Opaque conflict lifecycle id.
    }

    public enum ConflictState
    {
        Unresolved,
        ResolvedPendingSave,
        ResolvedSaved,
        Stale,
        DismissedNoop
    }

    public enum ConflictResolutionAction
    {
        ReloadFromExcel,
        KeepEditorValue,
        ManualResolve,
        KeepBoth
    }

    public enum ConflictResolutionSeed
    {
        ExternalValue,
        EditorValue,
        BaseValue
    }

    public readonly struct ConflictRecord
    {
        public ConflictId conflictId { get; }
        public ConflictState conflictState { get; }
        public ExcelDbEngine.AssetIdentity assetIdentity { get; }
        public int fieldId { get; }
        public string propertyPath { get; }
    }

    public sealed class ConflictResolver : IDisposable
    {
        public ConflictId conflictId { get; }
        public SerializedObject serializedObject { get; }

        public bool Apply();
        public void Dispose();
    }

    public static class AssetDatabase
    {
        // Context accessors are defined in 14.2.1.

        public static void MountWorkbook(string workbookPath);
        public static bool UnmountWorkbook(string workbookPath);
        public static void Refresh();
        public static void StartAssetEditing();
        public static void StopAssetEditing();
        public static void DisallowAutoRefresh();
        public static void AllowAutoRefresh();
        public static void ImportAsset(string path);
        public static void ImportAsset(string path, ImportAssetOptions options);
        public static bool FlushMetadata();
        public static bool FlushMetadata(string workbookPath);
        public static void SaveAssets();
        public static void SaveAssetIfDirty(ExcelDbEngine.Object obj);
        public static void SaveAssetIfDirty(GUID guid);
        public static void ForceReserializeAssets();
        public static void ForceReserializeAssets(IEnumerable<string> assetPaths, ForceReserializeAssetsOptions options);

        public static bool IsValidFolder(string path);
        public static string CreateFolder(string parentFolder, string newFolderName);
        public static string[] GetSubFolders(string path);
        public static string[] FindAssets(string filter);
        public static string[] FindAssets(string filter, string[] searchInFolders);
        public static string[] GetDependencies(string pathName);
        public static string[] GetDependencies(string pathName, bool recursive);
        public static string[] GetDependencies(string[] pathNames);
        public static string[] GetDependencies(string[] pathNames, bool recursive);
        public static Hash128 GetAssetDependencyHash(string path);
        public static Hash128 GetAssetDependencyHash(GUID guid);
        public static string[] GetLabels(ExcelDbEngine.Object obj);
        public static void SetLabels(ExcelDbEngine.Object obj, string[] labels);
        public static void ClearLabels(ExcelDbEngine.Object obj);
        public static string GUIDToAssetPath(string guid);
        public static string AssetPathToGUID(string assetPath);
        public static string AssetPathToGUID(string assetPath, AssetPathToGUIDOptions options);
        public static GUID GUIDFromAssetPath(string assetPath);
        public static string GetAssetPath(ExcelDbEngine.Object assetObject);
        public static ExcelDbEngine.Object LoadMainAssetAtPath(string assetPath);
        public static ExcelDbEngine.Object LoadAssetAtPath(string assetPath, Type type);
        public static T LoadAssetAtPath<T>(string assetPath) where T : ExcelDbEngine.Object;
        public static ExcelDbEngine.Object[] LoadAllAssetsAtPath(string assetPath);
        public static ExcelDbEngine.Object[] LoadAllAssetRepresentationsAtPath(string assetPath);
        public static Type GetMainAssetTypeAtPath(string assetPath);
        public static bool Contains(ExcelDbEngine.Object obj);
        public static bool IsMainAsset(ExcelDbEngine.Object obj);
        public static bool IsMainAsset(int instanceID);
        public static bool IsSubAsset(ExcelDbEngine.Object obj);
        public static bool IsSubAsset(int instanceID);
        public static bool IsForeignAsset(ExcelDbEngine.Object obj);
        public static bool IsForeignAsset(int instanceID);
        public static bool IsNativeAsset(ExcelDbEngine.Object obj);
        public static bool IsNativeAsset(int instanceID);
        public static bool OpenAsset(int instanceID, int lineNumber = -1);
        public static bool OpenAsset(int instanceID, int lineNumber, int columnNumber);
        public static bool OpenAsset(ExcelDbEngine.Object target, int lineNumber = -1);
        public static bool OpenAsset(ExcelDbEngine.Object target, int lineNumber, int columnNumber);
        public static bool OpenAsset(ExcelDbEngine.Object[] objects);

        public static void CreateAsset(ExcelDbEngine.Object asset, string assetPath);
        public static void AddObjectToAsset(ExcelDbEngine.Object objectToAdd, string path);
        public static void AddObjectToAsset(ExcelDbEngine.Object objectToAdd, ExcelDbEngine.Object assetObject);
        public static void RemoveObjectFromAsset(ExcelDbEngine.Object objectToRemove);
        public static void SetMainObject(ExcelDbEngine.Object mainObject, string assetPath);
        public static bool CopyAsset(string path, string newPath);
        public static bool DeleteAsset(string assetPath);
        public static string MoveAsset(string oldPath, string newPath);
        public static string RenameAsset(string pathName, string newName);
        public static string GenerateUniqueAssetPath(string path);

        public static bool TryGetGUIDAndLocalFileIdentifier(
            ExcelDbEngine.Object obj,
            out string guid,
            out long localId);
        public static bool TryGetGUIDAndLocalFileIdentifier(
            int instanceID,
            out string guid,
            out long localId);

        public static bool TryGetAssetStatus(
            ExcelDbEngine.Object obj,
            out AssetStatusRecord status);
        public static bool TryGetAssetStatus(
            string assetPath,
            out AssetStatusRecord status);
        public static bool TryGetWorkbookStatus(
            string workbookPath,
            out WorkbookStatusRecord status);
        public static ExcelDbEngine.RuntimeQueryStatus GetAssetDiagnostics(
            ExcelDbEngine.AssetIdentity assetIdentity,
            Span<AssetDiagnosticRecord> buffer,
            out int count);
        public static ExcelDbEngine.RuntimeQueryStatus GetWorkbookDiagnostics(
            string workbookPath,
            Span<AssetDiagnosticRecord> buffer,
            out int count);

        public static ExcelDbEngine.RuntimeQueryStatus GetConflicts(
            Span<ConflictRecord> buffer,
            out int count);
        public static bool TryGetConflict(ConflictId conflictId, out ConflictRecord conflict);
        public static bool ResolveConflict(ConflictId conflictId, ConflictResolutionAction action);
        public static bool TryOpenConflictResolver(
            ConflictId conflictId,
            ConflictResolutionSeed seed,
            out ConflictResolver resolver);

        public static OperationReport GetLastOperationReport();
    }

    public static class Undo
    {
        public static bool isProcessing { get; }
        public static event Action undoRedoPerformed;

        public static void RecordObject(ExcelDbEngine.Object obj, string name);
        public static void RecordObjects(ExcelDbEngine.Object[] objects, string name);
        public static void RegisterCompleteObjectUndo(ExcelDbEngine.Object obj, string name);
        public static void RegisterCompleteObjectUndo(ExcelDbEngine.Object[] objects, string name);
        public static void RegisterCreatedObjectUndo(ExcelDbEngine.Object obj, string name);
        public static void DestroyObjectImmediate(ExcelDbEngine.Object obj);
        public static int GetCurrentGroup();
        public static string GetCurrentGroupName();
        public static void SetCurrentGroupName(string name);
        public static void IncrementCurrentGroup();
        public static void CollapseUndoOperations(int groupIndex);
        public static void FlushUndoRecordObjects();
        public static void PerformUndo();
        public static void PerformRedo();
        public static void RevertAllInCurrentGroup();
        public static void RevertAllDownToGroup(int groupIndex);
        public static void ClearUndo(ExcelDbEngine.Object identifier);
        public static void ClearAll();
    }

    public static class EditorUtility
    {
        public static void SetDirty(ExcelDbEngine.Object target);
        public static bool IsDirty(ExcelDbEngine.Object target);
        public static bool IsPersistent(ExcelDbEngine.Object target);
    }

    public static class EditorGUIUtility
    {
        public static void PingObject(ExcelDbEngine.Object obj);
        public static void PingObject(int targetInstanceID);
    }

    public static class Selection
    {
        public static ExcelDbEngine.Object activeObject { get; set; }
        public static int activeInstanceID { get; }
        public static ExcelDbEngine.Object[] objects { get; set; }
        public static int[] instanceIDs { get; }
        public static string[] assetGUIDs { get; }
        public static int count { get; }
        public static event Action selectionChanged;

        public static bool Contains(ExcelDbEngine.Object obj);
        public static bool Contains(int instanceID);
        public static void SetActiveObjectWithContext(
            ExcelDbEngine.Object obj,
            ExcelDbEngine.Object context);
    }
}
```

错误和 report 默认规则：

- 参数为空、path 格式非法、类型不是 ExcelDB object 这类调用方编程错误可以抛 `ArgumentException` / `InvalidOperationException`。
- workbook 数据错误、schema 不兼容、validation error、file lock、conflict、reference restrict 等内容问题默认进入 `OperationReport`。
- Unity-like 返回值必须保持稳定：`LoadAssetAtPath<T>` 找不到、类型不匹配或 asset invalid 时返回 `null`；`GUIDToAssetPath` / `AssetPathToGUID` / `GetAssetPath` 找不到时返回空字符串；`DeleteAsset` / `CopyAsset` 未执行时返回 `false`；`MoveAsset` / `RenameAsset` 成功返回空字符串，失败返回错误文本并写 report。
- `GetLastOperationReport()` 返回最近一次 AssetDatabase operation 的 machine-readable report；业务逻辑不能解析 human-readable 文本。
- 会修改 workbook/source/object graph 的 API 都必须走 operation transaction；没有 report 的写操作不允许实现。
- `defaultContext` 是 host/bootstrap 创建的 editor authoring context；`currentContext` 是当前绑定 scope 或 default context。
- `BindContext` 用于工具、测试和明确的多 context 编辑；scope 结束必须恢复前一个 current context，不允许泄漏到其他测试或 Unity project。
- 传入其他 context 的 object、dirty draft、SerializedObject target 或 stale context object 时，API 必须返回失败并记录 `assetdb.context_mismatch` / `assetdb.context_closed`，不能按 asset identity 自动切到当前 context。

status / diagnostic query 默认语义：

- `GetLastOperationReport()` 是最近一次 operation 的事实记录；`TryGetAssetStatus`、`TryGetWorkbookStatus`、`GetAssetDiagnostics`、`GetWorkbookDiagnostics` 是当前 editor context 的状态索引视图，不能互相替代。
- status query 不触发 import、refresh、validation、metadata repair、auto-fix 或 report projection；它只读取当前已发布的 editor status index。
- editor status index 在 `MountWorkbook`、`Refresh`、`FlushMetadata`、schema layout refresh、`SerializedObject.ApplyModifiedProperties()`、`SetDirty`、Undo/Redo、conflict resolver、SaveAssets 和 recovery operation 成功 commit 后更新。
- `AssetStatusFlags` 可以组合；例如一个 asset 可以同时是 `DirtyEditor | Conflict | ValidationWarning`。UI 不能只看单个枚举值判断是否可保存或可 convert。
- `maxSeverity` 是当前 effective severity 的最大值，已经应用 operation profile/gate policy；原始 severity 和 suppression provenance 仍以 `OperationReport` 为准。
- `TryGetAssetStatus(Object)` 要求 object 属于当前 editor context 且不是 context_closed；跨 context 或 stale object 返回 false，并记录 context/stale diagnostic。
- `TryGetAssetStatus(string assetPath)` 只接受 current asset path；stale old path 返回 false，并通过 report 指向 current path。
- `TryGetWorkbookStatus(string workbookPath)` 使用 mounted workbook root；未 mounted path 返回 false，不隐式 mount。
- `GetAssetDiagnostics` / `GetWorkbookDiagnostics` 返回当前 status index 中的 lightweight diagnostic records；buffer 不足返回 `RuntimeQueryStatus.Truncated`，`count` 是 total required count，不创建临时数组。
- `AssetDiagnosticRecord.code`、`diagnosticId`、`propertyPath` 必须来自 report/status string table 或 descriptor cache；稳定列表刷新不得为每条诊断拼接新字符串。
- invalid raw row、duplicate key loser、metadata-only error 或 `temporary_imported` row 不进入常规 `FindAssets`，也不能通过 `GetAssetDiagnostics(AssetIdentity)` 查询，但必须能通过 `GetWorkbookDiagnostics` 以 workbook/sheet/row/cell location 查到。
- status index 只保存 machine-readable code、stable ids、counts、flags、severity 和 string table id；human-readable message、Excel comment、localized UI text 属于 report projection。
- status query 属于 editor core read path；达到水位线后 `TryGetAssetStatus`、`TryGetWorkbookStatus` 和 Span-based diagnostic enumeration 不得产生 GC allocation。

`MountWorkbook` 默认语义：

- `workbookPath` 使用项目相对路径，统一 `/`。
- mount 不要求立刻写 workbook；如果 workbook 不存在，可以在 editor authoring mode 创建 candidate workbook。
- mount 成功后建立 workbook source identity、watcher registration、metadata context 和 path root。
- 同一个 workbook 重复 mount 默认幂等；path 大小写或分隔符差异必须归一化到同一个 root。
- mounted workbooks 组成当前 editor source set；跨 workbook 引用和全局 key index 在 source set 内解析。
- mount 失败不污染已有 mounted context。

`UnmountWorkbook` 默认语义：

- unmount 是从当前 editor context 移除 workbook source，不删除 Excel 文件。
- workbook 有 dirty_editor、dirty_metadata 或 unresolved conflict 时，默认返回 false，并 report `source.unmount_dirty_workbook`。
- 其他 mounted workbook 对该 workbook 存在 hard reference 时，默认返回 false，并 report referrers；soft reference 可以进入 missing。
- unmount 成功后移除 watcher/path mapping/key index/reference edges，并发布 source removed summary。
- 被 unmount workbook 的 resident instances 进入 `unloaded` 状态；后续 `LoadAssetAtPath` 返回 null。
- 关闭整个 editor context 时可以批量 unmount；仍必须先处理 dirty/conflict，除非调用方选择 discard policy。

`Refresh` 默认语义：

- refresh 不保存 editor dirty。
- refresh 对 mounted workbook 做 fingerprint/source revision 检查。
- refresh 发现外部变化时进入 import transaction，并遵守 dirty / merge / conflict 状态机。
- refresh 成功后才发布 ChangeSet；失败时保留旧 object graph。

`ImportAsset` 默认语义：

- `ImportAsset(path)` 等价于 `ImportAsset(path, ImportAssetOptions.Default)`；它是 Unity-like targeted refresh/import request，不是隐式 mount、不是 SaveAssets、不是 schema generation，也不写 workbook。
- `path` 命中 mounted workbook root 时，只针对该 workbook 执行 watcher/read-verify/import transaction；命中 ExcelDB virtual asset path 时解析到 owning workbook，导入仍以 workbook 为最小读取单位，但 report 记录 requested asset path 和 affected asset filter。
- `path` 命中 virtual folder / table prefix 且 `ImportRecursive` 开启时，对该 prefix 下当前已知 asset 做 affected filter；未开启时只导入 owning workbook 并 report `requested_recursive=false`，不能把虚拟 folder 当真实目录逐文件扫描。
- 未 mounted workbook path、非 ExcelDB path、stale path、`temporary_imported` path 或 path 不能解析到 mounted workbook/source 时，不隐式 mount、不创建 workbook，记录 `assetdb.import_path_not_mounted` / `assetdb.path_stale` / `assetdb.asset_not_found`。
- `ForceUpdate` 表示即使 fingerprint 快速判断 no-op，也执行 read-verify 和 import classify；如果 canonical source_hash 仍不变，只更新 report/status，不发布 property_changed。
- `ForceSynchronousImport` 要求在当前 owner operation queue 的同步 safe point 完成 import/report；如果当前处于 asset editing batch，request 仍可被 batch defer 到 outermost `StopAssetEditing()`，不能从 drawer/repaint/watcher callback reentrant import。
- `ImportRecursive` 对 mounted workbook root 等价于导入该 workbook；对 search prefix 只影响 affected report/filter，不改变 workbook import 粒度。
- `DontDownloadFromCacheServer` 和 `ForceUncompressedImport` 是 Unity importer 兼容 flag；ExcelDB core 默认记录 ignored option，不改变 workbook source 语义。Unity adapter 若把 ExcelDB virtual asset 同步到 Unity proxy/cache，也必须把这类选项限定在 proxy/cache import，不得影响 workbook/source_hash。
- ImportAsset report 必须包含 requested path、resolved workbook root、resolved virtual asset/filter、options、fingerprint before/after、read-verify result、classifier summary、affected asset identities、published event summary 和 ignored option list。

`StartAssetEditing` / `StopAssetEditing` 默认语义：

- 这组 API 保留 Unity-like 命名，但在 ExcelDB 中表示当前 `AssetDatabaseContext` 的 editor asset editing batch scope；它不是 workbook 文件锁、不是事务提交边界，也不允许跳过 validation / merge / SaveAssets preflight。
- scope 使用 context-local nesting counter：`StartAssetEditing()` depth +1，`StopAssetEditing()` depth -1；只有 depth 从 1 到 0 时触发 batch drain。对未启动的 scope 调用 `StopAssetEditing()` 是调用方错误，记录 `assetdb.asset_editing_scope_unbalanced`，不得修改 workbook 或丢弃 dirty draft。
- scope 必须绑定当前 editor authoring context 和 owner operation queue；在一个 context 中 start，另一个 context 中 stop，按 `assetdb.context_mismatch` 处理。
- asset editing batch 激活期间，自动 watcher refresh、metadata auto flush、status/search/proxy projection rebuild、Project/browser repaint notification 和 editor ChangeSet/UI event publish 默认延后并 coalesce。
- batch 期间的显式 editor 操作仍照常创建 draft overlay、Undo record、operation report 和必要的 correctness preflight；`CreateAsset`、`MoveAsset`、`CopyAsset`、`SetLabels`、`SerializedObject.ApplyModifiedProperties()` 等操作不能因为在 batch 中就跳过 schema/reference/key/identity 检查。
- batch-local draft overlay 对同 context 后续 API 可见：`GUIDToAssetPath`、`AssetPathToGUID`、`GetAssetPath`、`LoadAssetAtPath`、`FindAssets` 和 reference setter 必须能看到已创建/移动/删除/改 label 的 draft 状态；实现可以延迟重建完整索引，但必须维护可查询的 deterministic overlay。
- `Refresh()` 在 batch 激活期间默认只登记 explicit refresh request 和当前 watcher queue watermark，不立即 import 外部 workbook；outermost `StopAssetEditing()` 后按 request sequence 与 workbook path deterministic 排序执行。
- `SaveAssets()` 在 batch 激活期间允许显式执行，但不能跳过内部 verify/reimport。它只对自己写入的 affected workbook 执行 critical reimport 以证明保存结果，然后继续保持外部 watcher refresh deferred；不能因为 batch 激活就凭内存 candidate 清 dirty。
- `StopAssetEditing()` outermost drain 顺序默认是：合并 batch-local draft overlay，重建 status/search/path/label/proxy index，执行允许的 metadata auto flush proposal，处理 queued explicit/manual refresh request，再处理自动 watcher refresh request，最后发布 coalesced editor UI/change summary。
- drain 中任一 deferred operation 失败时，已存在的 editor draft、dirty/conflict/status 保留；`StopAssetEditing()` 本身不回滚已成功的 draft operation，也不把 batch 当成 all-or-nothing 事务。
- 如果 context dispose/close 时 asset editing depth 非零，必须写 report 并清理 scope counter；dirty draft 和 unresolved conflict 仍按 context close/unmount 规则处理，不能因为 scope 泄漏静默丢数据。
- batch report 必须包含 `asset_editing_batch_id`、start/stop depth、deferred operation counts、coalesced workbook set、draft overlay digest、index rebuild summary、deferred refresh result 和 scope diagnostics。
- batch scope 维护属于 editor core operation；达到水位线后 counter 操作、deferred request enqueue/coalesce、overlay lookup、index rebuild scheduling 和 machine-readable report append 不得产生 GC allocation。容量不足时按 allocation policy 记录 `performance.watermark_grew` 或失败。

`DisallowAutoRefresh` / `AllowAutoRefresh` 默认语义：

- 这组 API 只控制自动 watcher refresh，不控制显式 `Refresh()`、`SaveAssets()`、`FlushMetadata()`、schema generation、migration 或 runtime source switch。
- scope 使用 context-local nesting counter：`DisallowAutoRefresh()` depth +1，`AllowAutoRefresh()` depth -1；未 disallow 时调用 allow 记录 `assetdb.auto_refresh_scope_unbalanced`，不丢弃 watcher queue。
- auto refresh 被禁止期间，file watcher 仍记录 event、debounce、fingerprint hint 和 queue watermark，但不会启动 import transaction、metadata auto flush 或 hot reload publish。
- 显式 `Refresh()` 在 auto refresh 禁止期间仍可执行；如果同时处于 `StartAssetEditing` batch，则按 batch 规则 defer 到 outermost stop。
- `SaveAssets()` 仍必须执行自己写入 workbook 的 critical verify/reimport；auto refresh 禁止不能让保存跳过复读 committed snapshot。
- `AllowAutoRefresh()` 使 depth 回到 0 时，如果没有 active asset editing batch，系统在下一个 owner stable point 处理 coalesced watcher queue；如果仍在 batch 中，则继续等 `StopAssetEditing()` outermost drain。
- auto refresh scope report 必须包含 suppression depth、queued watcher event count、coalesced workbook set、oldest/newest event sequence、manual refresh bypass count 和 drain result。

`FlushMetadata` 默认语义：

- `FlushMetadata()` 是 ExcelDB-specific metadata-only 写回事务；它只写 workbook metadata sheet、companion identity cell、schema-owned helper identity artifact 和必要 checksum，不写正式数据 cell、不保存 editor property diff。
- `FlushMetadata(string workbookPath)` 只处理指定 mounted workbook；未 mounted、path stale、context mismatch 或 workbook source revision 变化时返回 false 并写 report。
- `Refresh()` 发现新正式数据行缺 row guid/local id 时，可以分配 session-local `temporary_imported` identity，建立 editor view/status diagnostic，并把 workbook 标记为 `dirty_metadata` / `TemporaryIdentity`；它不因为临时身份本身保存业务数据。
- 如果 operation profile 允许 `metadata_auto_flush` 且 workbook 可写、无 identity blocker、无 unresolved schema/mapping conflict，Refresh 可以在同一 owner operation queue 后续自动执行 metadata-only flush；auto flush 必须生成独立 OperationPlan/report，不能藏在 import 里。
- `SaveAssets()` 默认包含所有 pending metadata flush；但只执行 `FlushMetadata()` 不会提交 `dirty_editor` 的 property diff，也不会清除 editor dirty。
- metadata flush 写入并复读 committed workbook 成功后，`temporary_imported` 获得 stable row guid/local id，并按 deterministic rule 派生 asset guid/local file id；`temporary_draft` 则把已预分配的最终 identity 写入 workbook。两者都会清除 `TemporaryIdentity` status，并更新 metadata checksum、row revision、source revision 和 base snapshot。
- metadata flush 失败时保留 `temporary_imported` / `temporary_draft` 状态、dirty_metadata 和 status diagnostic；如果失败原因是 file lock / protected workbook / package preservation 风险，产生对应 report，不能把 `temporary_imported` 当作 stable asset。
- unresolved duplicate row identity、metadata checksum mismatch、ambiguous row mapping、schema/layout mapping blocker、file lock、signature policy blocker 都会阻止 metadata flush。
- metadata flush 可以和 editor dirty 共存；它只更新 metadata base revision，不改变未保存 property diff。后续 `SaveAssets()` 仍必须按最新 source revision 做 merge/preflight。
- runtime `ExcelDataSource` hot reload 发布新增 row 前必须拥有 stable row identity。若 candidate 中存在只靠 `temporary_imported` identity 的新增 runtime object，默认先请求同 source set 的 editor authoring context 执行 metadata flush；无法 flush 时本次 runtime refresh/switch 保留旧 source，并报告 `metadata.identity_not_flushed`。
- Development runtime 没有可写 editor authoring context 时，missing row identity 是 refresh blocker；可以在 report 中显示新行位置，但不发布 unstable added object。
- `FlushMetadata` 属于 editor core operation；达到水位线后 analyze/preflight/report append 不得分配。实际 xlsx package 写入的外部 backend allocation 仍按 external backend 规则分账。

`SaveAssets` 默认语义：

- save 只保存 editor authoring context 中的 dirty workbook / dirty metadata / dirty asset。
- save 前必须对所有 affected workbook 做 preflight：schema compatibility、source revision、conflict、metadata flush、reference policy、validation blocker、data-loss confirmation。
- 如果 workbook source revision 变化，必须先完成三方 merge 并刷新 merged base snapshot，不能直接写回旧 editor draft。
- preflight 出现 blocker 时不写任何 workbook。
- preflight 通过后按 workbook path deterministic 顺序保存；单个 workbook 内必须 atomic save。
- 每个实际写入的 workbook 都必须走 backup / temp / verify / replace / recovery manifest 流程。
- 多 workbook 保存如果中途失败，report 必须列出已保存和未保存 workbook；后续实现可以提供 explicit all-or-nothing batch，但默认不假装跨文件原子。
- save 失败时保留 dirty 状态和 editor draft，不把内存状态标成已保存。
- save 只有在 replaced workbook verify 通过并重新 import committed snapshot 后才算成功；随后刷新 base snapshot、row revision、source hash、status/path/conflict index，并清除对应 dirty。不能仅凭内存 candidate 或 temp workbook verify 清 dirty。

`AssetModificationProcessor` 默认语义：

- `AssetModificationProcessor` 是 Unity-like editor extension surface，只在 editor authoring context 生效；runtime source、converted bytes reader、Release build reader 和 runtime hot reload 不调用它。
- processor 通过 editor registry 发现继承类上的可选 static message method；不提供任意 delegate 注册入口。discovery、反射绑定和 method table 构建属于初始化阶段，可以分配；operation 热路径只使用预计算 method table。
- 调用顺序必须 deterministic：先按 package / assembly registry order，再按 processor full name ordinal，再按固定 phase 顺序调用。report 必须记录 processor id、phase、输入 path、返回结果、异常类别、reentrant 拒绝和 side-effect 摘要。
- 传入 path 使用当前 ExcelDB virtual asset path，统一 `/`，不传 stale path、不传历史 path、不传本机物理 workbook 路径。metadata-only save 可以传 workbook root marker，并在 report 中标注是 metadata flush closure。
- processor 在 owner operation queue 的 safe point 执行，且不在 workbook temp/write/replace 的临界区内执行。processor 内部再次调用 `AssetDatabase`、`Undo`、`SerializedObject.ApplyModifiedProperties()`、`SaveAssets()`、`Refresh()` 或写回 API 默认视为重入，产生 `assetdb.modification_processor_reentrant` 并拒绝当前嵌套操作；需要异步动作时只能 enqueue 到后续 safe point。
- processor 抛出异常、返回无法解释的 path、返回未请求的新 path、声明未允许 side effect，或声明已完成但没有对应 draft/proxy side-effect ledger 时，产生 `assetdb.modification_processor_failed` 或现有 `transaction.side_effect_mismatch`，本次 operation 不写 workbook、不清 dirty。
- `OnWillCreateAsset(assetPath)` 在 path/schema/key 基础解析通过后、创建 `temporary_draft` 前调用。它是通知型 message，不能 veto；若任一 processor 失败，本次 create 失败且不分配 row identity。
- `OnWillMoveAsset(sourcePath, destinationPath)` 在 path 解析、可逆 key 检查、冲突预检查通过后、创建 key-field draft diff 前调用。`FailedMove` 产生 `assetdb.modification_processor_rejected`，不创建 draft；`DidNotMove` 表示 ExcelDB 继续执行正常 draft move；`DidMove` 只允许用于已声明的 Unity proxy/cache 或外部 side effect，ExcelDB asset 仍必须有对应 draft move proposal，否则按 `transaction.side_effect_mismatch` 失败。
- `OnWillDeleteAsset(assetPath, RemoveAssetOptions options)` 在 reference/delete plan analyze 之后、创建 delete marker 前调用。`FailedDelete` 产生 `assetdb.modification_processor_rejected`，不创建 delete marker；`DidNotDelete` 表示 ExcelDB 继续创建 delete draft；`DidDelete` 只允许用于已声明的外部/proxy artifact，不能表示 processor 已经直接删除 Excel row。对 ExcelDB main/sub asset，仍必须有 delete draft marker 才能在后续 `SaveAssets` 落盘。
- `RemoveAssetOptions.MoveAssetToTrash` 对 ExcelDB 表示“用户希望走可恢复删除/回收语义”；默认仍落成 editor delete marker、tombstone/recent-delete index 和 SaveAssets 写回，不把 workbook、worksheet 或 row 移到 OS trash。
- `OnWillSaveAssets(paths)` 只由 broad `SaveAssets()` 调用：在 dirty workbook/asset/metadata closure 收集完成之后、最终 preflight/write 之前执行。processor 可以返回输入 path 的子集；被移除的 path 保持 dirty 并写入 skipped report。返回新 path、stale path、unknown path 或跨 context path 是 `assetdb.modification_processor_failed`。
- `OnWillSaveAssets` 不能破坏必须同存的 closure。若返回子集拆掉了 owned child、metadata identity flush、cascade/set_null side effect、resolved conflict diff、reference policy side effect 或同 workbook 内不可隔离的 patch closure，本次 save 失败并保持全部相关 dirty draft。
- `SaveAssetIfDirty(Object/GUID)` 不触发 `OnWillSaveAssets`，但 create/move/delete 阶段已经各自触发对应 will message。`ForceReserializeAssets` 默认也不触发 `OnWillSaveAssets`；它是显式 rewrite command，使用自己的 operation report 和 preflight。
- Unity-like open-for-edit / make-editable 能力在 ExcelDB 中归入 lock provider 和 editability extension：需要写 workbook 的 create/move/delete/save 前可以调用 `MakeEditable` 或等价 provider，使结果映射到 `vcs.lock_required`、`vcs.lock_owner_mismatch` 或 `extension.permission_denied`。该阶段不得修改 workbook，只能改变可编辑性判定和 report。
- processor callback 本身的用户代码分配不计入 ExcelDB core no-GC 证明，但 core invocation、path array 构造、结果过滤、side-effect ledger 和 machine-readable report append 必须使用 operation arena；达到水位线后不得为每个 processor 创建临时 list、闭包、异常文本或拼接字符串。

`SaveAssetIfDirty` 默认语义：

- `SaveAssetIfDirty(Object obj)` / `SaveAssetIfDirty(GUID guid)` 是 Unity-like targeted save request；目标先解析为当前 editor context 的 stable asset identity 或 `temporary_draft` identity。missing、removed、stale、`temporary_imported`、跨 context object 或 guid index 未命中时不隐式 load/import/repair，写入 no-op/error report。
- ExcelDB 的物理保存单位仍是 workbook，不是单行文件；实现必须先从目标 asset 得到 save target closure，再扩展到 owned child/sub asset、必要 metadata、reference policy side effect 和 cascade/set_null side effect 影响的 workbook set。
- targeted save 不等于“保存目标所在 workbook 的所有 dirty”。同一 workbook 内不属于目标 closure 的 dirty draft 必须保留为 dirty；如果当前 xlsx backend 无法在保持未知 package part、非目标 dirty cell、样式/批注/公式等保真的前提下只提交目标 closure，则本次操作失败，不能偷偷把同 workbook 其他 dirty 一起落盘。
- 如果目标 asset 没有 dirty_editor / dirty_metadata，且没有必须同步的 identity/helper metadata，本次是 no-op；report 仍记录 target identity、resolved workbook、dirty=false 和 no-op reason。
- preflight 与 `SaveAssets` 使用同一严格边界：schema compatibility、source revision、conflict、metadata flush、reference policy、validation blocker、file lock/write permission、xlsx package preservation、data-loss confirmation、backup/temp/verify/reimport。任一 blocker 出现时不写任何 workbook。
- 目标保存可能因为 delete/copy/reference policy 扩展到跨 workbook side effect；扩展后若命中 unrelated dirty draft，必须只提交 side-effect 所需 diff，或在无法隔离时失败。不能因为 side effect 跨 workbook 就退化为全局 `SaveAssets()`。
- 保存成功后只清除已提交 closure 的 dirty/conflict/resolved_pending_save 状态；同 workbook 未提交的 dirty draft、Undo record、status diagnostic 和 base snapshot 仍保持未保存状态。
- `SaveAssetIfDirty(GUID guid)` 使用当前 guid index 解析，guid 对应 moved/renamed draft 时保存 current identity；guid 不存在或 guid 所在 workbook 未 mounted 时写 report，不尝试从历史 path 或磁盘扫描恢复。
- 和 Unity 语义一致，这个 API 不应在 serialization/repaint/import callback/watcher callback 中直接执行；ExcelDB adapter 必须把它排入 owner operation queue 的 safe point，或以 operation error 拒绝。它不触发 broad save hook / OnWillSaveAssets analog，但必须产出完整 OperationReport。
- targeted save 的 analyze/preflight/commit/report 使用 operation arena 和预注册 string table；达到水位线后 target closure 枚举、dirty diff filter、side-effect ledger、package preservation diff 和 machine-readable report append 不得产生 GC allocation。实际 xlsx backend 分配按 external backend allocation 单独记录。

`ForceReserializeAssets` 默认语义：

- `ForceReserializeAssets()` / `ForceReserializeAssets(assetPaths, options)` 是显式用户/工具命令，用于在 schema/codegen/layout/importer/xlsx writer 升级后，把选定资产或 metadata 重新写成当前 canonical physical representation；它不是 `SaveAssets` 的自动副作用，也不是 migration、cleanup、materialize defaults 或策划数据整理。
- 无参调用默认选择当前 context 下所有 mounted workbook 中可解析、非 removed、非 `temporary_imported` 的 current asset；带 `assetPaths` 时，每个 path 先按 current path index 解析。ExcelDB 额外允许 workbook root、table root 或 virtual folder prefix 作为批量选择前缀；stale/missing/unmounted path 只进入 report，不猜测旧路径。
- `ReserializeAssets` 只重写选中 asset closure 的 schema-owned data cell / owned child row 的规范物理表达；`ReserializeMetadata` 只重写 metadata sheet、identity companion cell、helper identity artifact、checksum / revision helper；`ReserializeAssetsAndMetadata` 同时执行两者。省略 options 的 facade 默认等价于 assets + metadata。
- reserialize 不表达语义数据修改：若 canonical value、authoring state、asset identity、reference identity 和 metadata identity 均不变，则 `source_hash` / value hash / runtime dependency hash 保持不变；可以更新 workbook package fingerprint、source revision、metadata checksum、writer version marker，以及被实际触碰 row/helper 的 row revision。
- reserialize 必须保留 schema 未拥有的 freeform 区域、策划额外说明、普通 Excel 批注、样式、公式、drawing/table/pivot/VBA、未知 xlsx package part 和 project-registered helper region。无法保真 patch 时产生 package preservation / unsupported feature blocker，不整包重写。
- reserialize 不 materialize missing default、不清理 deprecated layout、不删除 tombstone、不修复冲突、不接受 data-loss confirmation；这些必须通过独立的 materialize / cleanup / migration / repair operation 生成 dry-run plan 后执行。
- reserialize 仍走与保存一致的 recovery 边界：operation plan、source revision、schema/layout hash、package fingerprint、backup/temp/verify/replace/reimport。目标集合中存在 unresolved conflict、dirty draft 或外部 source revision 变化时，必须先 merge/resolve 或由用户明确选择保存/丢弃策略；默认不把 dirty 改动混入 canonical rewrite。
- `ForceReserializeAssets()` 无返回值，但 ExcelDB 必须把 plan summary、selected assets、options、changed physical records、unchanged/no-op records、skipped paths、package preservation result、source hash before/after 和 revision changes 写入 OperationReport。高风险或需要人工确认的 case 默认失败，不静默弹性修复。
- 该 API 只能来自直接用户动作、菜单、CLI 或明确工具命令；不能在 serialization/repaint/import callback/watcher callback 内直接执行。Unity adapter 需要排入 owner operation queue 的 safe point，避免和场景/Inspector 修改交错。
- reserialize analyze/preflight/rewrite/report 使用 operation arena；达到水位线后 selected path resolve、closure traversal、canonical record compare、rewrite ledger 和 machine-readable report append 不得产生 GC allocation。xlsx writer/backend 分配按 external backend allocation 记录，不能冒充 core no-GC。

`LoadMainAssetAtPath` / `LoadAssetAtPath` / `LoadAllAssetsAtPath` 默认语义：

- asset path 必须解析到 mounted workbook root、table schema name 和 escaped key path。
- asset path 统一使用 `/`，查询时按 normalized path ordinal ignore-case 匹配；report 中保留 canonical current path。任何 API 都不接受 `\` 作为存盘 canonical path。
- path 解析命中 moved/renamed stale path 时，不猜测 identity；返回 `null` 或空数组，并在 report 中给出 current path。
- path 解析命中 `temporary_imported` row 时，默认返回 `null` 或空数组，并报告 `metadata.identity_not_flushed`；metadata flush 成功后同一路径可正常加载 stable asset。
- `LoadMainAssetAtPath(path)` 返回主 asset object；找不到、stale、类型不可加载或命中 sub_asset-only representation 时返回 null。
- `LoadAssetAtPath(path, Type type)` 返回该 path 下第一个 Project-visible 且可赋值给 `type` 的 object：先检查主 asset，再按 `LoadAllAssetRepresentationsAtPath` 顺序检查 sub-asset representation。找不到、type 为 null、stale path、invalid row 或 `temporary_imported` 返回 null 并写 report。
- `LoadAssetAtPath<T>(path)` 等价于 `LoadAssetAtPath(path, typeof(T)) as T`；因此当主 asset 类型不匹配但存在兼容 sub-asset representation 时，可以返回 sub-asset。需要稳定拿主对象时必须使用 `LoadMainAssetAtPath`。
- 若同一路径下多个 sub-asset 都匹配 `type`，返回顺序必须 deterministic：主 asset 优先，其次 schema field order、child order index、child identity stable sort。该 API 不因为多匹配失败；需要全量结果时使用 `LoadAllAssetsAtPath`。
- `LoadAllAssetsAtPath` 默认返回主 asset 加 schema 声明为 Project-visible 的 sub-asset representations，顺序为主 asset、schema field order、child order index；embedded child、helper object、diagnostic/report artifact 不返回。
- `LoadAllAssetRepresentationsAtPath` 返回 schema 声明的 sub-asset representations，不包含主 asset；embedded child 不返回，除非 schema 声明为 sub_asset representation。
- `GetMainAssetTypeAtPath(path)` 返回主 asset generated runtime type；找不到或 invalid path 返回 null，不 materialize object。
- `Contains(obj)` 只对当前 editor context 中 resident、dirty draft、temporary draft 或 valid sub_asset representation 返回 true；transient、temporary_imported preview、removed、unloaded、stale_recreated、context_closed 或其他 context object 返回 false。
- load 不创建新 asset，不修复 metadata，不写 workbook。

asset classification / persistence query 默认语义：

- `Contains(obj)` 表示 object 当前属于这个 `AssetDatabaseContext` 的 asset set 或 draft asset set；它不是“已写入磁盘”的判断。`temporary_draft` 可以 `Contains=true`，但 `EditorUtility.IsPersistent=false`。
- `IsMainAsset(obj)` 只对 current context 中的 main row asset 返回 true，包括 stable resident、dirty draft over stable row，以及 unsaved `temporary_draft` main asset。schema-owned sub-asset、embedded child、transient、`temporary_imported` preview、removed/stale/unloaded/closed context object 返回 false。
- `IsSubAsset(obj)` 只对 schema-owned `asset_representation=sub_asset` child representation 返回 true，包括 stable sub-asset、dirty draft sub-asset 和 unsaved sub-asset `temporary_draft`。embedded child、plain array element、helper/report object、main asset 和 Unity external resource reference 返回 false。
- `IsMainAsset(instanceID)` / `IsSubAsset(instanceID)` / `IsForeignAsset(instanceID)` / `IsNativeAsset(instanceID)` 先在当前 context-local instance id table 解析 object，再应用 object overload 规则；跨 context、closed context、Unity proxy instance id 或 unknown id 返回 false。Unity adapter 若要支持 proxy id，必须先通过 proxy registry 映射到 ExcelDB object。
- `IsForeignAsset(obj)` 默认对已从 external authoring source 导入并有 stable source identity 的 ExcelDB asset 返回 true：Excel workbook row/sub-asset、converted bytes authoring projection、dirty draft over stable source 都是 foreign asset。`temporary_draft` 在 SaveAssets/FlushMetadata 成功前仍没有磁盘事实源，返回 false。
- `IsNativeAsset(obj)` 默认对 ExcelDB virtual asset 返回 false，因为真实存盘对象是 Excel workbook / converted artifact，不是 Unity native serialized asset。只有某个 adapter/schema 明确声明某类 ExcelDB object 由 Unity native serialization 作为事实源时才可返回 true；可选 Unity proxy/cache 的 native/foreign 状态不反向污染 ExcelDB object。
- `EditorUtility.IsPersistent(target)` 表示目标当前有已提交的磁盘事实源。stable resident main/sub asset、dirty draft over stable asset、read-only converted source asset 返回 true；transient、`temporary_draft`、`temporary_imported` preview、removed/delete draft、stale_recreated、unloaded、context_closed 或其他 context object 返回 false。
- `IsPersistent=true` 不表示当前内存值已保存；dirty draft over stable asset 仍可 persistent=true 且 `EditorUtility.IsDirty=true`。`IsPersistent=false` 也不阻止 `temporary_draft` 被 `FindAssets/GUIDToAssetPath/LoadAssetAtPath` 查询；它只表示 SaveAssets 前 Excel 尚无该 row/sub-asset 的 committed representation。
- classification query 不 import、不刷新、不 repair metadata、不 materialize payload；它只读 object state、identity table、source kind 和 representation descriptor。object overload 与 instanceID overload 在水位线后不得产生 GC allocation，report append 也走 machine-readable arena。

`OpenAsset` 默认语义：

- `OpenAsset(Object/int/objects)` 是 editor projection operation：打开或聚焦与目标 asset 关联的编辑界面，默认优先 generic/custom inspector + ExcelDB browser row；如果 Unity adapter 配置允许，也可以打开 Excel 到 workbook/sheet/row/cell。
- OpenAsset 不保存、不刷新、不 repair metadata、不创建 dirty、不改变 selection、不改变 source_hash；如果打开 Excel 后用户手动修改，仍通过 watcher/Refresh 进入正常 import/merge/conflict 流程。
- object overload 只接受当前 context 的 main asset、schema-owned sub-asset、dirty draft over stable asset 或 `temporary_draft`；`temporary_imported` preview、removed/stale/unloaded/closed context/其他 context object 返回 false 并写 report。
- instanceID overload 先按当前 context-local instance id 解析；Unity proxy instance id 必须由 Unity adapter 先经 proxy registry 映射到 ExcelDB object，否则返回 false。
- object array overload 按输入顺序打开；全部目标可打开才返回 true。若任一目标 invalid 或 adapter 无法处理，默认返回 false，并在 report 中列出 opened/skipped/failed；已经打开的 UI 不回滚，但不得修改数据。
- 对 Excel workbook 打开定位，默认 target location 来自 schema mapping：main asset 定位主 row key/header/data region，sub-asset 定位 child row，field/cell diagnostic 可通过 report/location 精确到 cell。`lineNumber` / `columnNumber` 对 ExcelDB target 可作为 row/column override，但必须先验证在 owning workbook/sheet 有效；无效时忽略并写 warning report，不猜测其他 sheet。
- 对 generated code、schema/proto source 或 external Unity resource target，Unity adapter 可以转交宿主默认 external editor；这属于 adapter projection，不改变 ExcelDB identity 或 source state。
- OpenAsset report 必须包含 target identities、resolved workbook/sheet/range、adapter route（browser / inspector / excel / external editor）、line/column handling、opened/skipped/failed 列表和 UI-only allocation summary。
- OpenAsset 可以分配 editor UI/proxy/external process 资源；但目标解析、identity lookup、location mapping 和 machine-readable report append 在水位线后不得产生 GC allocation。

sub-asset lifecycle API 默认语义：

- `AddObjectToAsset(objectToAdd, path)` / `AddObjectToAsset(objectToAdd, assetObject)` 保留 Unity-like 命名，但 ExcelDB 不提供任意隐藏 sub-asset bag；可加入的目标必须是 schema 声明为 `asset_representation=sub_asset` 的 owned child field / child table row。
- path overload 的 `path` 必须解析为 current main asset path；object overload 的 `assetObject` 必须是当前 context 的 main asset resident 或 `temporary_draft` parent。传入 sub-asset、`temporary_imported`、stale、removed、其他 context object 或 workbook/table/folder prefix 产生 `assetdb.sub_asset_parent_invalid` / `assetdb.asset_not_found`。
- `objectToAdd` 必须是 transient `ScriptableObject` / generated child object，或尚未保存到任何 parent 的 temporary draft child；已经是 stable asset、其他 parent 的 sub-asset、runtime object、Unity external resource object 或 closed context object 时产生 `assetdb.object_state_invalid`，不能把已存盘对象“搬进”另一个 row。
- 目标 child slot 由 schema 决定：若 parent descriptor 只有一个兼容 `sub_asset` append/create slot，则 API 可以自动选择；若没有兼容 slot，产生 `assetdb.sub_asset_not_supported`；若有多个兼容 slot，产生 `assetdb.sub_asset_mapping_ambiguous`，调用方必须改用 `SerializedProperty` 指向具体 child field。
- Add 成功只创建 editor draft child row / sub-asset representation、ownership edge、dependency edge、Undo record 和 dirty state，不直接写 workbook。新 child 立即获得最终 row guid/local id、asset guid/local file id 和 parent field id；`SaveAssets` 成功只是把 identity_state 从 `temporary_draft` flush 为 stable。
- Add 使用 `objectToAdd` 当前 schema-defined serialized state 作为 child 初值，并按目标 child descriptor 重新 parse/normalize/validate/default materialize；context-local instance id、direct mutation overlay provenance、old path/name、diagnostic projection 和外部 Unity object identity 都不复制进 workbook。
- `RemoveObjectFromAsset(objectToRemove)` 只接受 current context 中的 schema-owned sub-asset representation；它创建 child delete/detach draft marker，不删除 parent main asset、不直接写 workbook。embedded child 和普通 array element 必须通过 `SerializedProperty` 删除，main asset 必须通过 `DeleteAsset` 删除。
- Remove 必须遵守 required child、min count、ownership、reference delete policy、cascade/set_null/restrict、validator 和 conflict state；任一 blocker 时不创建 partial draft。
- `GetAssetPath(subAsset)` 返回 owning main asset path；`TryGetGUIDAndLocalFileIdentifier(subAsset, out guid, out localId)` 返回同一 ExcelDB asset guid family 下的 sub-asset local file id。`LoadMainAssetAtPath(path)` 永远返回 parent main asset；`LoadAllAssetsAtPath(path)` / `LoadAllAssetRepresentationsAtPath(path)` 才返回 sub-asset。
- `SetMainObject(mainObject, assetPath)` 默认不改变 ExcelDB source：main object 由 workbook root + table + row identity 决定，sub-asset 不能被提升为主 row。若 `mainObject` 已经是该 path 的 current main asset，记录 no-op report；否则产生 `assetdb.main_object_fixed`。Unity adapter 可在 proxy/cache 层改变 Project 窗口展示优先级，但不得改变 workbook、source_hash、asset guid 或 runtime index。
- sub-asset lifecycle operation report 必须包含 parent identity/path、parent field id、child identity、selected slot、created/removed marker、dependency edge changes、reference/delete blockers、undo unit id 和 save closure digest。
- Add/Remove analyze/preflight/draft/report 使用 operation arena；达到水位线后 parent resolve、slot match、child identity allocation、ownership/dependency edge update 和 machine-readable report append 不得产生 GC allocation。`LoadAllAssetsAtPath` 返回数组作为 Unity-like projection 可以分配，core index traversal 不得分配。

`GetAssetPath` / guid-path 默认语义：

- `GetAssetPath(Object)` 只对当前 mounted editor context 中的 resident、dirty draft、temporary draft asset 返回 current path；unloaded/removed/stale object 返回空字符串并写 report。
- 对 schema-owned sub-asset representation，`GetAssetPath(Object)` 返回 owning main asset path；sub-asset 的区分必须通过 local file id、field id、order/index 或 `LoadAllAssetRepresentationsAtPath` 结果，而不是伪造独立 path。
- `GUIDToAssetPath(guid)` 读取当前 editor path index；如果该 guid 对应 dirty draft move/rename，返回 draft current path。
- `AssetPathToGUID(path)` 等价于 `AssetPathToGUID(path, AssetPathToGUIDOptions.IncludeRecentlyDeletedAssets)`，保持 Unity-like 默认。
- `AssetPathToGUID(path, OnlyExistingAssets)` 只接受 current live path；stale old path、delete draft marker、saved tombstone、removed row 和 `temporary_imported` 返回空字符串，并在 report 中记录 `assetdb.path_stale`、`assetdb.asset_not_found` 或 `metadata.identity_not_flushed`。
- `AssetPathToGUID(path, IncludeRecentlyDeletedAssets)` 除 current live path 外，还可以返回当前 editor session 中刚被 `DeleteAsset` 标记删除、或 SaveAssets 后仍保留 tombstone/recent-delete index 的原 asset guid；它不表示该 asset 可被 `LoadAssetAtPath` 加载，也不会复活对象。
- recently deleted guid 的保留期默认到 editor context close、workbook unmount、tombstone cleanup 成功或 source revision 证明该 tombstone 已被项目策略清理为止；保留期内只服务 guid continuity、Undo/report/merge，不进入 `FindAssets` 常规结果。
- `GUIDFromAssetPath(path)` 返回 `GUID` wrapper，语义等价于按默认 options 调用 `AssetPathToGUID(path)` 后解析；找不到时返回 default/empty `GUID`，不抛异常。
- `temporary_draft` 拥有预分配最终 guid/local file id，可以参与 guid/path API；`temporary_imported` 没有 stable guid/local file id，`AssetPathToGUID` 返回空字符串并报告 `metadata.identity_not_flushed`。
- guid/path API 不 import、不刷新、不修复 metadata；它们只查询当前 path index。

`TryGetGUIDAndLocalFileIdentifier` 默认语义：

- object overload 只对当前 context 的 resident main asset、schema-owned sub-asset representation、dirty draft 和 `temporary_draft` 返回 true；transient、`temporary_imported` preview、removed、unloaded、stale_recreated、context_closed 或其他 context object 返回 false 并写 report。
- instanceID overload 只查询当前 editor context 的 context-local instance id table；它不是 Unity proxy instance id、不是跨 context 全局 id，也不能在 context close 后复用。Unity adapter 若传入 proxy instance id，必须先通过 proxy registry 映射回 ExcelDB object，再调用 object overload。
- main asset 返回 ExcelDB asset guid + main local file id；sub-asset 返回 owning asset guid family + sub-asset local file id。local file id 在同一 asset guid 内稳定，来自 row guid/local id、parent field id、child identity 和 schema representation id 的 deterministic 派生。
- `temporary_draft` 在 CreateAsset/AddObjectToAsset 时已经预分配最终 guid/local file id，SaveAssets 前后查询结果不得改变。`temporary_imported` 没有 stable metadata，必须返回 false，不能临时暴露 metadata flush 前尚不稳定的 guid/local id。
- recently deleted / delete draft object 默认 `isValid=false`，object overload 返回 false；需要历史 guid 时使用 `AssetPathToGUID(path, IncludeRecentlyDeletedAssets)` 或 conflict/report API。
- 该查询不 materialize object、不刷新、不 repair metadata、不触发 import；它只读取 identity/index table。object overload 可做到 no-GC；instanceID lookup 和 report append 达到水位线后也不得产生 GC allocation。

path mutation API 默认语义：

- `MoveAsset`、`RenameAsset`、`CopyAsset`、`GenerateUniqueAssetPath` 操作的是 ExcelDB asset path 投影，不移动 xlsx 文件本身。
- path mutation 必须能解析 mounted workbook root、table schema name、escaped key path，并通过 key path pattern 无损反解 key fields；否则返回失败并报告 `key.path_not_reversible`。
- `MoveAsset(oldPath, newPath)` 默认只允许在同一 mounted workbook root、同一 table id 内改变 key path。跨 workbook / 跨 table move 默认 `assetdb.move_forbidden`；需要显式 migration、copy+delete 或项目扩展 policy。
- `RenameAsset(pathName, newName)` 等价于替换 final asset path segment 后执行 `MoveAsset`；`newName` 不能包含 `/`，并必须能按 schema key normalizer 生成合法 final segment。
- `MoveAsset` / `RenameAsset` 在基础解析、可逆 key 检查和冲突预检查通过后调用 `AssetModificationProcessor.OnWillMoveAsset`；processor 拒绝、异常或声明不一致时不创建 key-field draft diff。
- move/rename 成功只创建 editor draft key-field diff，保持 workbook guid、table id、row guid/local id、asset guid 和 local file id 不变；`SaveAssets` 成功后才写入 Excel。
- move/rename 后 editor path index 使用 draft path；old path 立即成为 stale path，不再被 `LoadAssetAtPath` 猜测解析。
- key/path 变化可能产生 `moved` / `renamed` event，但不产生 recreated；如果只有 path/key 变化，不应发布 property_changed，除非 key field 同时是业务 runtime field 且 schema 选择把它作为 property change 暴露。
- move/rename 目标与现有有效 asset path、case-insensitive path 或 duplicate key 冲突时，不创建 draft，并报告 `key.asset_path_collision` / `validation.unique_key_duplicate`。
- `GenerateUniqueAssetPath(path)` 不创建 asset、不保留 key、不分配 identity；它只查询当前 path index 和 key descriptor，返回一个当前未占用且可逆的 candidate path。
- `GenerateUniqueAssetPath` 默认 suffix policy 是在 final reversible string segment 后追加 `_1`、`_2`、...；如果 schema key normalizer 或 path pattern 不接受该 segment，继续尝试下一个 suffix；无法生成时返回空字符串并写 report。

`CopyAsset` 默认语义：

- `CopyAsset(path, newPath)` 不直接写 workbook；它创建一个 operation plan、一个可 undo 的 editor draft copy closure，并更新 draft path/key index。`SaveAssets` 成功后才落盘到 Excel。
- source 必须能解析为当前 context 的 resident stable asset 或 `temporary_draft` asset；`temporary_imported`、removed、unloaded、invalid、stale path、跨 context object、unresolved conflict 或 validation blocker 都不能作为 copy source，返回 `false` 并报告对应 diagnostic。
- `newPath` 必须解析到 mounted workbook root、目标 table、可逆 key path，并通过 schema key normalizer 反解目标 key fields；目标 path / key 冲突时不创建 draft，报告 `key.asset_path_collision` / `validation.unique_key_duplicate`。
- 默认只允许同 table copy；跨 workbook copy 允许在 mounted target workbook 的同 table 创建新 row。跨 table 或 schema 声明的 compatible runtime type 之外的 copy 返回 `assetdb.copy_forbidden`，必须通过 migration 或项目扩展 policy 实现。
- copy 使用当前 editor authoring applied state：已 Apply 的 dirty draft value 会被复制，未 Apply 的 `SerializedObject` pending buffer 不会被复制。
- 非 key schema data field 按 canonical authoring state 复制：`missing/default_materialized/explicit_null/explicit_empty/explicit_value` 都保持原状态；key fields 由 `newPath` 的反解结果覆盖 source key value。
- copy 必须重新分配 workbook-local row identity、asset guid/local file id 和所有 owned child/sub-asset element identity；不得复制 workbook/row identity、row revision、metadata row record、diagnostic projection、conflict state、dirty provenance、migration history 或 status index entry。
- copy closure 默认包含主 asset、schema-owned child rows、schema-owned sub assets 和 ownership edge；不包含普通引用目标、helper artifact、diagnostic/report artifact、deprecated tombstone 或外部 Unity resource。
- copy closure 内部引用默认 remap 到新副本；closure 外部引用默认保留原 target identity。schema 可按 reference field 声明 `copy_reference_policy: preserve_target | remap_owned_closure | clear | forbidden`；没有声明时使用前述默认。
- remap 后如果违反 target type/table scope、hard reference、nullable/list duplicate、ownership cycle 或 reference policy，copy plan 是 blocker，不创建 draft，也不尝试“先复制再让用户修”。
- Unity external resource reference 复制时保留 guid/local file id/runtime provider key，并按当前 AssetDatabase 重新投影 `main_asset_path`；只改显示路径不改变 resource identity。
- cross-workbook copy 不把目标 workbook 当成 source clone：source workbook guid 不变，target row 使用 target workbook guid 派生新 asset identity，外部 references 默认仍指向原 source set target。
- Undo copy 等价于 undo create closure：移除 draft copy closure，但不复用已分配的 row/local ids；redo 恢复同一 `temporary_draft` identity。
- `SaveAssets` 前必须复检 source revision、target workbook source revision、schema/layout hash、identity allocator、key/path collision、reference policy、validation blocker 和 copy closure digest；任一变化导致 `transaction.plan_stale` / `transaction.preflight_failed`，不写 workbook。
- CopyAsset analyze/preflight/commit 使用 operation arena 和预注册 string table；达到水位线后 copy closure traversal、identity remap table、reference remap table、status index update 和 machine-readable report append 不得产生 GC allocation。closure 超过容量时按 allocation policy 记录 `performance.watermark_grew` 或失败。
- copy operation report 必须包含 `copy_plan_hash`、`source_asset_identity`、`target_asset_identity`、`source_path`、`target_path`、`copied_field_ids`、`copied_child_identities`、`remapped_references`、`preserved_references`、`cleared_references`、`blocked_references`、`identity_allocations` 和 `copy_closure_digest`。

`FindAssets` 默认语义：

- 具体 filter 语法以 14.15.2 为准，默认兼容 Unity-like name / `l:` / `t:` 查询，并扩展 `key:` / `table:` / `path:` / `guid:`。
- `FindAssets(string filter)` 等价于 `FindAssets(filter, null)`。
- `searchInFolders` 限制 current asset path prefix，不改变 identity 或 stale path 处理。
- 返回 guid 数组，不 materialize object instance。
- 排序按 current asset path deterministic。
- blocker/invalid row 不进入常规结果；debug/report API 才能查到。
- `temporary_draft` 可以进入 `FindAssets`；`temporary_imported` 不进入 `FindAssets`，必须通过 `GetWorkbookDiagnostics` / table view / status index 显示。

virtual folder API 默认语义：

- ExcelDB asset path 中的 folder 是 virtual path prefix，不是独立存盘对象；真实存盘对象仍是 workbook + table + row。`IsValidFolder` / `GetSubFolders` 只查询 current asset path index，不创建或修复 workbook。
- `IsValidFolder(path)` 对 mounted workbook root、table schema root、以及至少存在一个 current asset path 以该 prefix 开头的 key-prefix folder 返回 true；对 stale path、unknown workbook、`temporary_imported` only prefix 或物理目录但非 mounted ExcelDB root 返回 false。
- `GetSubFolders(path)` 返回 current asset path index 中 immediate child virtual folders，排序按 normalized path ordinal ignore-case，再按原 path 打破平局；不存在或未 mounted path 返回空数组并写 `assetdb.search_folder_not_mounted`。
- `CreateFolder(parentFolder, newFolderName)` 在 ExcelDB virtual root 下默认返回空字符串并报告 `assetdb.virtual_folder_create_forbidden`。创建空 virtual folder 没有 workbook row identity，会破坏“Excel 是真实存盘对象”；需要新路径时应通过 `CreateAsset` / `MoveAsset` / key 字段编辑创建实际 row。
- Unity adapter 如果需要创建真实 Unity project folder，应调用 `UnityEditor.AssetDatabase.CreateFolder` 或宿主文件系统 API；ExcelDB `AssetDatabase.CreateFolder` 不代理真实目录，避免同名 API 同时改变 Unity project 和 ExcelDB source。
- `newFolderName` 不参与 key normalizer，不预留路径，不改变 `GenerateUniqueAssetPath` 结果；只有 actual asset row 进入 path index 后，folder prefix 才变为 valid。

`GetDependencies` / `GetAssetDependencyHash` 默认语义：

- editor `AssetDatabase.GetDependencies` 是 14.17.3 canonical dependency graph 的 Unity-like path projection；它不构建第二套依赖系统，也不扫描 C# 字段、Excel 文本、Unity project 或 Addressables group 来猜依赖。
- API 形态遵循 Unity 2022.3：`GetDependencies(pathName)` 等价于 `GetDependencies(pathName, true)`；`GetDependencies(pathNames)` 等价于 `GetDependencies(pathNames, true)`。`recursive=true` 返回 indirect dependencies，并包含输入 path 本身；`recursive=false` 只返回 direct dependencies，不包含输入 path。
- `GetDependencies` 读取当前 editor context 的 dependency/path projection index；不触发 import、Refresh、validation、metadata repair、provider discovery、object materialization 或 full workbook scan。
- 输入 path 必须是 current ExcelDB asset path 或 Unity adapter 可解析的 project asset path；stale path、missing path、invalid row、`temporary_imported` 或跨 context path 返回空数组并写 `assetdb.path_stale` / `assetdb.asset_not_found` / `metadata.identity_not_flushed` 等现有 diagnostic。
- 多输入重载先按传入数组顺序解析输入 path，再对结果去重；`recursive=true` 时输入 paths 按传入顺序优先出现在结果中，其余依赖按 target family、canonical target identity、current display path deterministic 排序。
- `internal_asset` target 投影为当前 ExcelDB asset path；如果 target 被 strip、unmounted、missing、deleted 或 unresolved duplicate，不凭 key/path 猜测，按 dependency/reference diagnostic 记录并从 path projection 中省略。
- `unity_resource` target 投影为 `main_asset_path` 或 Unity adapter 缓存的 guid -> asset path projection；guid/local file id/runtime provider key 仍是身份事实源，path 只是返回给 Unity-like API 的显示路径。
- `localized_text` 和 `external_runtime_key` 默认不是 Unity project path，因此不进入 `string[]` 结果；只有 provider descriptor 显式声明 `editor_display_asset_path` 且该 path 可验证时才投影为路径。省略 opaque dependency 不表示 dependency graph 没有该 edge，report 必须记录 skipped target family/count。
- dependency kind 默认包含 `ownership | runtime_preload | runtime_lazy | build_only`；`validation_only` 和 `editor_only` 只有 Editor/Development debug profile 或 explicit query policy 允许时进入 path projection。runtime no-GC `RuntimeDatabase.GetDependencies` 仍按 caller supplied `DependencyQuery.includeKinds` 工作。
- `GetAssetDependencyHash(path)` / `GetAssetDependencyHash(GUID guid)` 返回当前 editor/build projection 的 aggregate `Hash128`。path overload 先按 current path 解析；guid overload 先通过 current guid index 解析，找不到返回 zero hash 并写 report。
- dependency hash 输入必须 canonical，并至少覆盖：source asset identity、current asset path、source row/owned child authoring hash、schema_hash、layout/importer relevant hash、operation/build target profile hash、dependency edge digest、recursive dependency closure digest、Unity/resource/localization/external provider runtime dependency hash、provider descriptor hash 和 current export/build profile id。
- dependency hash 不等于 `source_hash`、`runtime_dependency_hash` 或 `bytes_hash`：它是 Unity-like editor/build cache invalidation signal。只改 human report、Excel comment、debug preview 或本机路径不改变它；只改 display path 是否改变取决于对应 importer/provider descriptor 是否声明 display path 影响 build/import。
- `GetAssetDependencyHash` 读取预计算或增量维护的 dependency hash index；索引 stale 时必须先由 import/refresh/build operation 更新，query 本身不做全量 recompute。
- `GetDependencies` 返回数组和 path string 是 Unity-like editor projection，可以分配；dependency graph traversal、edge digest、dependency hash index 和 report append 在水位线后仍必须遵守 no-GC core contract。

`GetLabels` / `SetLabels` / `ClearLabels` 默认语义：

- label API 以 14.15.2 的 label descriptor 为唯一事实源；不会读取或写入 Unity `.meta` label、隐藏 ExcelDB-only label bag 或本机 editor cache。
- `GetLabels(Object)` 要求 object 属于当前 editor context 且 identity 有效；missing/stale/unloaded/context_closed 返回空数组并写 report。没有 `asset_labels` descriptor 时返回空数组并报告 `assetdb.labels_not_supported`。
- `SetLabels(Object, string[])` 和 `ClearLabels(Object)` 是 editor operation：只创建 label field draft diff、Undo record、dirty/status/search-index update，不直接写 workbook；`SaveAssets` 才提交到 Excel。
- label write target 必须由 schema 声明为唯一 mutable asset label write group；否则失败并报告 `assetdb.labels_not_supported` 或 `assetdb.label_mapping_ambiguous`。
- label write 与普通 property edit 使用同一 source revision / conflict / validation / SaveAssets preflight；Excel 外部同时修改 label cell 时进入 conflict。
- `FindAssets("l:")` 查询 `asset_labels` 与 `search_labels` 的 search index；`GetLabels` 只返回 `asset_labels`。因此 category/review_state 这类只读 search label 可以用于搜索，但不会被 `ClearLabels` 删除。
- `GetLabels` / `FindAssets` 返回数组是 Unity-like editor projection，可以分配；label index 维护、label edit operation 和 report append 在水位线后仍必须遵守 no-GC core contract。

`CreateAsset` 默认语义：

- `assetPath` 决定 workbook、table 和 key/path；schema 必须能从 path 反解或明确填充 key fields。
- `asset` 必须是 transient object 或未绑定 source 的 object；已属于其他 workbook/context 时默认报错。
- create 只创建 editor draft row，不直接写 Excel；创建时预分配最终 row guid/local id 和派生 asset guid/local file id，`identity_state=temporary_draft` 只表示尚未 flush 到 workbook。
- create 在分配 `temporary_draft` identity 和创建 draft row 前调用 `AssetModificationProcessor.OnWillCreateAsset`；processor 异常或重入时本次 create 失败，不留下半分配身份。
- SaveAssets 成功前，temporary draft asset 可以被 Editor authoring 的 `FindAssets` / `GUIDToAssetPath` / `LoadAssetAtPath` 查到，但不能进入 convert/runtime index。
- SaveAssets 成功后同一个 draft row guid/local id 变为 stable；asset guid/local file id 保存前后不得改变。
- undo create 会移除 draft row，但同一 editor context 内默认不复用已分配的 draft row guid/local id；redo create 恢复同一 `temporary_draft` identity。
- default values、required fields、enum default、simple struct default 由 schema materialize。
- duplicate key 是 validation error；duplicate row identity 是 blocker。
- create 应自动进入 dirty 状态，并可通过 `Undo.RegisterCreatedObjectUndo` 回退。

`DeleteAsset` 默认语义：

- delete 默认只是标记 editor draft deletion，不直接改 Excel；实际删除由 `SaveAssets` 提交。
- delete 前必须检查 reference policy：restrict 返回 `false` 并 report referrers；cascade / set_null / leave_missing 只在 schema 显式允许时执行。
- delete 在 reference/delete plan analyze 后调用 `AssetModificationProcessor.OnWillDeleteAsset`；processor 拒绝、异常或声明已删除但没有合法 delete draft proposal 时，不创建 delete marker。
- delete 成功提交后发布 `removed` 和必要的 `dependency_changed`。
- delete 不允许把旧 row identity 复用给新行。

`Undo` 默认语义：

- ExcelDB Undo stack 属于当前 editor authoring context；static `Undo` 按 current/default `AssetDatabaseContext` 路由。不同 context 的 undo group、record buffer、redo stack、isProcessing 和 callbacks 完全隔离。
- `GetCurrentGroup()` 返回 context-local monotonic group id；`IncrementCurrentGroup()` 结束当前 logical edit group 并开启新 group。Unity event/mouse/menu 自动分组可以由 adapter 调用该 API，但 core 不依赖 Unity event object。
- `SetCurrentGroupName(name)` 只设置当前 group 的 UI/display name；不改变 diff、dirty、source_hash 或 save behavior。`GetCurrentGroupName()` 返回显式名称或按 group 内首个 operation 的 deterministic fallback 名称。
- `CollapseUndoOperations(groupIndex)` 把 groupIndex 之后到当前 top 的 undo operations 合并为一个 menu step；它只改变 undo stack grouping，不合并 SaveAssets transaction、不改变 draft diff 内容、不重排 operation commit 顺序。group 不存在或跨 context 时记录 `undo.group_invalid`。
- `RecordObject(obj, name)` 在修改前捕获 schema-defined serialized snapshot、object state token、source revision、row revision、mapping revision 和 direct mutation overlay generation；它不记录 unmanaged runtime cache、proxy state、human report 或 Excel 外部保存。
- `RecordObjects(objects, name)` 等价于对每个 object RecordObject，但必须在同一 current group 下生成一个 group entry；任一 object invalid/cross-context/temporary_imported/stale 时，本次 record all-or-nothing 失败并记录 `undo.record_scope_invalid`。
- `RegisterCompleteObjectUndo(obj/name)` 立即把对象当前 schema-defined complete state 放入当前 group，适合 custom editor 即将做 bulk rewrite、variant 切换、array whole rewrite 或 data-loss-risk edit。对象数组 overload 必须形成同一 group entry，all-or-nothing。
- `RecordObject` 是延迟 diff：只有后续 `SetDirty` / direct mutation flush / `FlushUndoRecordObjects()` 发现 canonical value 改变时才生成 undo record；如果 binary/canonical state 没变，不入栈。
- `FlushUndoRecordObjects()` 把当前 pending RecordObject snapshot 与 current object state 做 canonical diff，并把变化写入 current undo group；它不写 editor draft，除非对应 direct mutation 同时通过 `SetDirty` 成功转成 draft diff。
- `SerializedObject.ApplyModifiedProperties()` 自身默认形成 undoable edit unit；如果调用前已有 current group/name，则加入当前 group，否则使用 operation name / first changed property 生成 group name。
- `RegisterCreatedObjectUndo(obj, name)` 只接受已由 `CreateAsset` / `AddObjectToAsset` 产生的 `temporary_draft` main/sub asset；undo create 移除 draft create marker，redo 恢复同一 row guid/local id、asset guid/local file id，不复用新 identity。
- `DestroyObjectImmediate(obj)` 等价于可 undo 的 `DeleteAsset` / `RemoveObjectFromAsset` draft marker；它不立即删除 Excel row、不写 workbook、不绕过 reference restrict/cascade/set_null plan。对 embedded child/plain array element 应走 `SerializedProperty` array delete，而不是 DestroyObjectImmediate。
- `PerformUndo()` / `PerformRedo()` 只在当前 context 的 undo/redo stack 上执行一个 group；空栈记录 `undo.stack_empty` 并保持 no-op。执行期间 `isProcessing=true`，重入 record/apply/save/open/refresh 默认失败并记录 `undo.processing_reentrant` 或排入 owner queue safe point。
- undo/redo 应用的是 editor draft state transition：property diff、create/delete marker、label edit、copy closure、sub-asset add/remove、conflict resolution draft 等；不会直接写 workbook、不会撤销已经成功的 SaveAssets、不会触发 runtime source switch。
- undo/redo 后必须更新 dirty/status/search/path/label/dependency/proxy projection index，并发布 editor UI/change summary；runtime ChangeSet 只在 source commit/hot reload 后发布。
- undo/redo 后如果 base source revision、row revision、schema hash、layout hash 或 mapping revision 已被外部 Excel refresh 改变，该 group 的目标 asset 进入 needs refresh/merge 或 conflict；下一次 SaveAssets 必须重新 merge/preflight，不能按旧 undo snapshot 直接写回。
- `RevertAllInCurrentGroup()` / `RevertAllDownToGroup(groupIndex)` 执行 undo-like draft revert 但不创建 redo entry；用于取消正在编辑的 UI gesture。group invalid 时记录 `undo.group_invalid`，不得清除无关 dirty。
- `ClearUndo(identifier)` 只清除当前 context undo/redo stack 中与该 object identity 相关的 entries；它不清 editor dirty draft、不删除 conflict、不改变 workbook。`ClearAll()` 清当前 context undo/redo stack，但不清 dirty/conflict/status。
- SaveAssets 成功后不必清空 undo stack；undo 仍可把 editor draft 回到旧值并再次进入 dirty，但它不能回滚已写入 Excel 的历史文件。若项目希望保存后裁剪 undo，需要显式 policy 并在 report 中记录。
- Undo callbacks：`undoRedoPerformed` 在 successful `PerformUndo` / `PerformRedo` 的 editor draft/index commit 后触发；callback 异常进入 report，不回滚已经应用的 undo/redo。callback 中再次修改数据必须进入 owner operation queue，不能重入当前 undo commit。
- ExcelDB Undo 不实现 Unity Scene/GameObject 专用 API（`AddComponent`、`SetTransformParent`、`RegisterChildrenOrderUndo`、`MoveGameObjectToScene` 等）。Unity adapter 处理真实 Unity objects 时应调用 UnityEditor.Undo；ExcelDB facade 只处理 ExcelDB `Object`。
- Undo core operation 使用 operation arena 和 preallocated group/entry/diff buffers；达到水位线后 group lookup、record flush、perform undo/redo、projection index update 和 machine-readable report append 不得产生 GC allocation。human Undo menu text 和 Unity UI repaint 属于 editor projection allocation。

`EditorUtility.SetDirty` 默认语义：

- `SetDirty` 是 direct object mutation 的兼容入口，不是推荐主路径；推荐仍是 `SerializedObject.ApplyModifiedProperties()`。
- `SetDirty` 标记对象需要从 current object state 与 base snapshot 计算 property diff。
- 如果 diff 无法映射到 schema field/cell，进入 error，`SaveAssets` 拒绝写回。
- runtime read-only mode 下 `SetDirty` 明确失败或 no-op + report，不能静默制造可保存 dirty。
- `IsDirty(target)` 只查询当前 editor context 的 dirty/status index：dirty_editor、dirty_metadata、delete marker、copy/create child draft、resolved_pending_save 都返回 true；仅有 diagnostic projection、human report 或 layout-only non-semantic change 默认不算 dirty。
- `IsPersistent(target)` 使用 asset classification / persistence query 默认语义；它不等于 `Contains`，也不等于 `!IsDirty`。
- `SetDirty`、`IsDirty`、`IsPersistent` 对其他 context、runtime context、closed context、stale object 和 `temporary_imported` preview 都必须返回 no-op/false 并写 context/state report；不能按 asset identity 自动重绑定到当前 context。

### 14.21 默认 SerializedObject / SerializedProperty facade 协议

SerializedObject 是编辑 draft 和 property diff 的入口，不是对 C# 字段的简单反射包装。它必须由 schema、metadata、object revision 和 cell mapping 共同驱动。

SerializedObject / SerializedProperty 默认 API surface：

```csharp
namespace ExcelDbEditor
{
    public sealed class SerializedObject
    {
        public SerializedObject(ExcelDbEngine.Object obj);
        public SerializedObject(ExcelDbEngine.Object[] objs);

        public ExcelDbEngine.Object targetObject { get; }
        public ExcelDbEngine.Object[] targetObjects { get; }
        public bool isEditingMultipleObjects { get; }
        public bool hasModifiedProperties { get; }

        public void Update();
        public bool ApplyModifiedProperties();
        public SerializedProperty FindProperty(string propertyPath);
        public SerializedProperty GetIterator();
    }

    public sealed class SerializedProperty
    {
        public string name { get; }
        public string displayName { get; }
        public string tooltip { get; }
        public string propertyPath { get; }
        public SerializedPropertyType propertyType { get; }
        public bool editable { get; }
        public bool hasMultipleDifferentValues { get; }
        public bool hasChildren { get; }
        public bool hasVisibleChildren { get; }
        public bool isArray { get; }
        public int arraySize { get; set; }
        public int depth { get; }
        public bool isExpanded { get; set; }

        public int intValue { get; set; }
        public long longValue { get; set; }
        public bool boolValue { get; set; }
        public float floatValue { get; set; }
        public double doubleValue { get; set; }
        public string stringValue { get; set; }
        public int enumValueIndex { get; set; }
        public string[] enumNames { get; }
        public string[] enumDisplayNames { get; }
        public ExcelDbEngine.Object objectReferenceValue { get; set; }
        public object boxedValue { get; set; }
        public object managedReferenceValue { get; set; }
        public string managedReferenceFullTypename { get; }
        public string managedReferenceFieldTypename { get; }
        public long managedReferenceId { get; }

        public SerializedProperty FindPropertyRelative(string relativePropertyPath);
        public SerializedProperty GetArrayElementAtIndex(int index);
        public void InsertArrayElementAtIndex(int index);
        public void DeleteArrayElementAtIndex(int index);
        public void MoveArrayElement(int srcIndex, int dstIndex);
        public bool NextVisible(bool enterChildren);
        public SerializedProperty Copy();
    }
}
```

默认支持的 `SerializedPropertyType`：

```text
Integer
Boolean
Float
String
Enum
ObjectReference
Generic
Array
ManagedReference
```

默认规则：

- `ManagedReference` 只映射 schema-discriminated polymorphic payload；variant identity、payload field set、migration 和 editor drawer 都来自 schema descriptor，不能把任意 CLR managed reference 或 JSON blob 塞进 workbook。
- `managedReferenceValue` 是 Unity-like allocating editor projection API，只接受 generated variant payload object / proxy 或 `null`；setter 必须解析到 schema 声明的 stable variant id 和 payload field set，失败产生 `serialized.type_mismatch` 或对应 variant diagnostic，不创建 pending diff。
- `managedReferenceFullTypename` / `managedReferenceFieldTypename` 返回 schema-generated stable type token 的 Unity-like projection；它们只是 editor display / compatibility helper，不作为 workbook identity、source hash 或 variant identity。
- `managedReferenceId` 返回当前 payload 的 stable variant/payload projection id；它必须由 schema variant id + owner field/path + payload identity 派生，不能使用 CLR object reference、GC handle 或运行时递增序号。
- 完整目标版不能把 multi-object edit 作为半开放能力；`SerializedObject(Object[] objs)` 必须按 14.24 的 common property scope、mixed value、diff/merge、report 和 all-or-nothing 测试完整实现。
- `SerializedObject(Object[] objs)` 是 Unity-like API surface；`objs.Length == 1` 等价于 single-target 构造，`objs.Length > 1` 默认进入可编辑 multi-target surface。只有目标集合、schema/layout/property mapping、field policy、drawer capability 或项目 profile 不兼容时，才稳定降级为 read-only / table_view 或 reject_open，并报告对应 diagnostic。
- 多目标降级时，adapter 可以创建只读 multi-target view 用于 inspector 展示和 diagnostics，但任一 setter / array edit / `ApplyModifiedProperties()` 必须失败并记录 `serialized.multi_object_not_supported` 或更具体的 incompatibility code；不能只编辑第一个 target。
- `SerializedObject` 创建时记录 target object revision、source revision、schema hash、layout hash、property mapping revision。
- `SerializedObject` 创建时同时记录 target object 的 editor context id；target 不属于当前 editor authoring context、context 已关闭或 object 来自 runtime context 时，创建失败并记录 `serialized.context_mismatch`。
- multi-target 构造要求所有 target 属于同一 editor context、同一 operation profile、同一 owner queue，并且每个 target 都不是 `temporary_imported`、removed、unloaded、stale 或 invalid；否则构造失败或降级只读并记录对应 context/object diagnostic。
- `FindProperty` 使用 canonical property path / Unity-style array path，不使用 Excel header display text、C# member name 或 localized display name。
- `FindProperty` 可以命中 `property_aliases[]`，但 alias 必须由 schema descriptor 显式声明并绑定同一 field id；alias 命中返回当前 canonical property handle，`propertyPath` 仍是 canonical path。
- 找不到 property 返回 `null`，并在 editor diagnostics 中记录；不自动按相似名字、旧 header、C# member name 或列位置猜测。
- 不在核心 property API 增加非 Unity 成员，例如 `DisplayValue`；展示字符串由 property drawer、debug helper 或 report 生成。
- `tooltip` 来自 schema field/header comment；没有注释时返回空字符串，不由 drawer 临时拼接。
- `name`、`displayName`、`tooltip`、`propertyPath`、`enumNames`、`enumDisplayNames` 必须来自 descriptor 或 `SerializedObject` property path cache；稳定 inspector repaint 不得反复创建字符串或数组。
- multi-target `targetObjects` 返回初始化阶段创建的 cached target snapshot；实现内部必须另存权威 target list，调用方修改返回数组不得改变编辑目标。`targetObject` 按 Unity-like 语义返回第一个 target，但任何写入决策不得只看第一个 target。
- `SerializedProperty` 是 Unity-like handle API，但实现必须使用 `SerializedObject` 局部 handle arena/pool；`FindProperty`、`GetIterator`、`Copy`、`GetArrayElementAtIndex` 在 warmup 后不得产生 GC，超过历史最大 property/array path 数量时只允许水位线增长并报告 `performance.watermark_grew`。
- `boxedValue` / `managedReferenceValue` 不进入完整目标版 no-GC 主表面；确需调试或迁移时只能作为明确标记 allocating 的 editor-only helper，不能成为自定义 editor 的推荐路径。

property tree / traversal 默认规则：

- `GetIterator()` 返回 root iterator，root 不是可绘制 property，`depth=-1`，第一次 `NextVisible(true)` 移动到第一个 visible top-level property。
- 遍历顺序为 schema field order、struct subfield order、array visible order；child table backed array 按 order index 映射到 `Array.data[index]`。
- `NextVisible(enterChildren: true)` 在当前 property 有 visible children 时进入子树；`enterChildren: false` 跳过当前 property 子树并移动到下一个同级或祖先同级。
- `NextVisible` 只遍历 schema-visible property：`HideInInspector`、reserved/internal metadata、hidden identity/source/revision cell 默认不可见，但仍可被 importer、validator、report 使用。
- `hasChildren` 表示 schema 上存在 child property；`hasVisibleChildren` 排除隐藏、reserved、当前 capability 不可见或 drawer policy 隐藏的 child。
- `depth` 以 top-level schema field 为 0，struct child / array element / element subfield 逐级加 1；该值只反映 property tree，不反映 Excel 行列位置。
- `isExpanded` 是 editor UI 状态，默认由 context + asset identity + property path 记忆；它不参与 schema hash、source hash、dirty diff、convert bytes，也不改变 `NextVisible` 的确定性。
- `FindPropertyRelative` 只接受相对 schema field path 或相对 Unity-style array path；不能使用 display name、Excel header text 或 C# PascalCase member name。
- `Copy()` 只复制当前位置、visible traversal state、所属 `SerializedObject` handle generation 和 revision token，不复制 pending buffer 或 property value。

`Update` 默认语义：

- `Update()` 从 target object 和 editor draft 重新构建 property view。
- 如果存在未 apply 的本地 property 修改，`Update()` 默认丢弃这些未 apply 修改，保持 Unity 风格。
- 如果 target/source revision 已外部变化，`Update()` 绑定最新可见 revision；如果存在 unresolved conflict，property 仍可浏览但不可 apply。

`ApplyModifiedProperties` 默认语义：

- 无修改时返回 `false`。
- 有修改且成功进入 editor draft/dirty state 时返回 `true`。
- apply 前检查 target object revision、source revision、schema hash、layout hash、property mapping revision。
- revision 变化但 property 未冲突时可以三方 merge；同一 property 冲突时失败并进入 conflict。
- apply 只产生 property diff 和 dirty 状态，不直接写 Excel。
- property diff 使用 row guid/local id + field id + array index/path segment；不使用 row number 或 header text。

pending property 修改默认规则：

- `SerializedProperty` setter 只修改所属 `SerializedObject` 的 pending property buffer，不立即修改 resident object、editor draft row 或 workbook cell。
- 任一 setter 成功后 `hasModifiedProperties=true`；`Update()` 丢弃未 apply 的 pending buffer。
- `ApplyModifiedProperties()` 把 pending buffer 归并成 schema field diff，进入 undo group、editor draft 和 validation preflight。
- setter 写入相同 canonical value 时不产生 diff；但如果 schema normalizer 生成 auto-fix proposal，report 仍可记录 display/canonical mismatch。
- setter 不能绕过 `editable=false`、readonly field、deprecated/reserved field、schema capability 或 runtime read-only mode；这些情况产生 `serialized.read_only` 或明确抛调用错误，不创建 pending diff。

值类型默认映射：

- `intValue` / `longValue` / `boolValue` / `floatValue` / `doubleValue` / `stringValue` 只能用于兼容的 scalar property；类型不兼容产生 `serialized.type_mismatch`，不做隐式宽松转换。
- scalar setter 使用 schema parse/normalize 同一套 canonicalization；例如 `stringValue` 默认不 trim，`intValue` 不写入 float 字段，`doubleValue` 不绕过 fixed-point scale policy。
- `stringValue = null` 默认等价于设置空字符串，不等价于 missing/null；需要清空 nullable/default 字段时必须由 drawer/command 走 explicit clear operation，并接受 nullability/default validation。
- nullable scalar clear 后在 editor draft 中保存 `explicit_null`，SaveAssets 写 bare `null` token；optional/default clear 后保存 `missing/default_materialized`，SaveAssets 清除 value payload 并保留 style/comment，不写 schema default token。
- explicit empty string 由 `stringValue = ""` 或 `stringValue = null` 产生，SaveAssets 写 `""`；它不同于 clear-to-missing / clear-to-null。
- enum 使用 schema value id 作为身份；`enumValueIndex` 是当前 schema enum order 的显示索引。
- 设置 `enumValueIndex` 时必须把 index 解析为 enum value id 并写入 pending buffer；schema enum reorder 后旧 index 不可复用，property view 必须重新 `Update()`。
- `enumNames` / `enumDisplayNames` 来自 schema 当前 descriptor；deprecated enum value 可以显示但写入规则由 schema policy 决定。
- enum rename/display rename 不改变已有 value id；删除正在使用的 enum value 时 property 进入 error，convert blocker。
- object reference 使用 `objectReferenceValue`；内部写 target row guid/local id，不写显示 key。
- `objectReferenceValue = null` 只有 nullable reference 或 object reference array 的 Unity-like delete-first-step 允许；non-null reference 产生 validation error，不能静默写空。
- object reference setter 必须验证 target 属于当前 editor context/source set、runtime type/table scope 匹配、不是 removed/unloaded/stale object；否则产生 `reference.missing_target` / `reference.type_mismatch` 或 editor operation error。
- `UnityResourceRef` 默认作为 simple struct / custom drawer 暴露；是否桥接到 UnityEngine.Object picker 由 Unity adapter drawer 决定。

simple struct 默认映射：

- `expanded_columns`：父 property 为 `Generic`，子 property 使用 `parent.child`，子 property 可独立修改 cell。
- `single_cell`：父 property 映射到一个 cell，子 property 是 virtual property；修改任意子 property 会重写整个 cell。
- struct codec 必须由 schema 声明；不能靠 `ToString()` / split 字符串猜。

array/list 默认映射：

- `arraySize`、insert/delete/move 都必须先检查 field descriptor 的 `array_edit_policy`；API surface 存在不等于当前 field 一定可编辑。
- `arraySize` 增大时，新增元素按 schema default materialize。
- `arraySize` 缩小时，删除尾部元素并生成 property diff。
- `InsertArrayElementAtIndex` 插入 schema default element；object reference element 默认插入 null，除非 schema non-null 且有 default。
- `DeleteArrayElementAtIndex` 对 object reference 数组默认贴近 Unity：元素非 null 时第一次调用清空引用，第二次调用移除元素；非 reference 数组直接移除元素。
- `MoveArrayElement` 是完整目标版 API surface；它必须产生 deterministic reorder diff，不能伪装成 delete + insert。
- repeated single-cell layout 修改任意元素会重写整个 cell；horizontal columns 修改对应 range；child table layout 修改子表行集合。

array element identity 默认规则：

- repeated scalar / repeated simple struct 如果没有 stable element identity，diff key 使用 parent row identity + field id + array index；同一 array 的 insert/delete/reorder 与外部修改默认冲突。
- child table backed array 使用 child row guid/local id 作为 element identity；insert 创建 temporary child identity，SaveAssets 成功后 flush stable identity。
- `arraySize` 修改、insert/delete/move 都先进入 pending buffer；`ApplyModifiedProperties()` 才创建 child row create/delete/reorder marker 或 single-cell/horizontal range diff。
- index 越界、负数、对非 array 调用 array API、对 read-only/deprecated array 修改，产生 `serialized.array_edit_invalid` 或 `serialized.read_only`，不改变 pending buffer。
- child table delete 不复用 element identity；undo delete 恢复同一 draft element identity，外部变化后仍需 conflict resolver。
- child table reorder 只更新 order index，不改变 element identity；repeated scalar/simple struct reorder 没有 element identity 时必须以 whole-array reorder diff 表示，并与外部同 array 任意修改冲突。

property handle 默认生命周期：

- `SerializedProperty` handle 只在所属 `SerializedObject` 当前 revision 内有效。
- `ApplyModifiedProperties()`、`Update()`、external refresh、schema layout refresh 后，旧 handle 需要重新获取；继续写旧 handle 必须产生 `serialized.handle_stale` 或 no-op + diagnostic。
- property-to-cell mapping revision 改变且旧 property 无法重新映射时，产生 `serialized.mapping_changed`，不把旧 pending buffer 写入新 cell。
- iterator copy 只复制当前位置和 revision token，不复制底层 draft。

### 14.22 默认 RuntimeDatabase / DataSource facade 协议

RuntimeDatabase 是宿主无关运行时读入口。它不像 Unity 已有类型，因此使用 ExcelDB-native 命名；但读取对象、引用解析、identity、ChangeSet 和 report 仍复用前文协议。

RuntimeDatabase / DataSource 默认 API surface。`Object`、`ScriptableObject`、`HideFlags` 和 `ObjectState` 的权威定义在 14.19.1；本节不得重新定义这些基础类型，只引用它们。

```csharp
namespace ExcelDbEngine
{
    public enum SourceKind
    {
        Excel,
        ConvertedBytes,
        Composite
    }

    public enum ConvertedBytesBufferOwnership
    {
        Copy,
        TakeOwnership,
        BorrowImmutable
    }

    public enum RuntimeMode
    {
        EditorAuthoring,
        EditorPlayModeDebug,
        DevelopmentBuild,
        Release
    }

    public enum RuntimeAllocationPolicy
    {
        AllowWatermarkGrowth,
        ReportWatermarkGrowth,
        FailOnWatermarkGrowth,
        FailOnAnyHotPathAllocation
    }

    public enum RuntimeQueryStatus
    {
        Complete,
        Truncated,
        InvalidIdentity,
        InvalidHandle
    }

    [Flags]
    public enum DependencyKindMask
    {
        Ownership = 1 << 0,
        RuntimePreload = 1 << 1,
        RuntimeLazy = 1 << 2,
        ValidationOnly = 1 << 3,
        EditorOnly = 1 << 4,
        BuildOnly = 1 << 5,
        Runtime = RuntimePreload | RuntimeLazy,
        All = Ownership | RuntimePreload | RuntimeLazy | ValidationOnly | EditorOnly | BuildOnly
    }

    public enum DependencyTraversal
    {
        Direct,
        Recursive
    }

    public enum DependencyTargetKind
    {
        InternalAsset,
        UnityResource,
        LocalizedText,
        ExternalRuntimeKey
    }

    public readonly struct AssetIdentity
    {
        // Opaque stable identity: workbook guid + table id + row guid/local id.
    }

    public readonly struct AssetKey
    {
        // Opaque pre-resolved key/path lookup handle for no-GC runtime access.
        // Bound to the RuntimeDatabaseContext that created it.
    }

    public readonly struct RuntimeProviderKey
    {
        // Opaque provider-specific key; not a display path or managed string.
    }

    public enum RuntimeArtifactProviderKind
    {
        StreamingAssets,
        Addressables,
        Resources,
        Custom
    }

    public readonly struct RuntimeArtifactLocator
    {
        public RuntimeArtifactLocator(
            string artifactId,
            string providerId,
            RuntimeArtifactProviderKind providerKind,
            RuntimeProviderKey providerKey,
            string manifestHash,
            string bytesHash);

        public readonly string artifactId;
        public readonly string providerId;
        public readonly RuntimeArtifactProviderKind providerKind;
        public readonly RuntimeProviderKey providerKey;
        public readonly string manifestHash;
        public readonly string bytesHash;
    }

    public readonly struct RuntimeArtifactLease : IDisposable
    {
        public RuntimeArtifactLease(
            in RuntimeArtifactLocator locator,
            ReadOnlyMemory<byte> bytes);

        public readonly bool isValid;
        public readonly RuntimeArtifactLocator locator;
        public readonly ReadOnlyMemory<byte> bytes;

        public void Dispose();
    }

    public interface IRuntimeArtifactProvider
    {
        string providerId { get; }
        string providerVersion { get; }
        bool TryOpen(in RuntimeArtifactLocator locator, out RuntimeArtifactLease lease);
    }

    public static class RuntimeArtifactProviderRegistry
    {
        public static void Register(IRuntimeArtifactProvider provider);
        public static bool TryGetProvider(string providerId, out IRuntimeArtifactProvider provider);
    }

    public readonly struct RuntimeLoadSetId
    {
        // Opaque load set id resolved from descriptor/profile during initialization.
        // Bound to the RuntimeDatabaseContext that created it.
    }

    public enum RuntimeLoadSetStatus
    {
        NotFound,
        Unloaded,
        Loading,
        Loaded,
        Blocked
    }

    [Flags]
    public enum RuntimeSnapshotCapability
    {
        ThreadSafe = 1 << 0,
        JobSafe = 1 << 1,
        BurstSafe = 1 << 2
    }

    public readonly struct RuntimeRevision
    {
        // Opaque published source revision + source/materialized hashes.
    }

    public readonly struct RuntimeSnapshotOptions
    {
        public RuntimeSnapshotOptions(RuntimeSnapshotCapability requiredCapabilities);

        public readonly RuntimeSnapshotCapability requiredCapabilities;
    }

    public interface IRuntimeAssetView
    {
        AssetIdentity identity { get; }
    }

    public readonly struct RuntimeSnapshotJobView
    {
        // Opaque immutable view for host job systems; never exposes Object wrappers.
    }

    public readonly struct RuntimeSnapshot : IDisposable
    {
        public readonly bool isValid;
        public readonly DatabaseContextId contextId;
        public readonly RuntimeRevision revision;
        public readonly RuntimeSnapshotCapability capabilities;

        public bool TryGetAsset<TView>(AssetKey key, out TView asset)
            where TView : struct, IRuntimeAssetView;
        public bool TryGetAsset<TView>(AssetIdentity identity, out TView asset)
            where TView : struct, IRuntimeAssetView;
        public RuntimeQueryStatus GetAssets<TView>(Span<TView> buffer, out int count)
            where TView : struct, IRuntimeAssetView;
        public RuntimeQueryStatus GetDependencies(
            AssetIdentity identity,
            Span<DependencyTarget> buffer,
            out int count);
        public RuntimeQueryStatus GetDependencies(
            AssetIdentity identity,
            in DependencyQuery query,
            Span<DependencyTarget> buffer,
            out int count);
        public RuntimeSnapshotJobView AsJobView();
        public void Dispose();
    }

    public readonly struct DependencyQuery
    {
        public DependencyQuery(
            DependencyTraversal traversal,
            DependencyKindMask includeKinds,
            bool includeSelf,
            int maxDepth);

        public readonly DependencyTraversal traversal;
        public readonly DependencyKindMask includeKinds;
        public readonly bool includeSelf;
        public readonly int maxDepth;
    }

    public readonly struct DependencyTarget
    {
        public readonly DependencyTargetKind kind;
        public readonly AssetIdentity assetIdentity;
        public readonly RuntimeProviderKey providerKey;
    }

    public readonly struct RuntimeTypeId
    {
        // Opaque generated runtime type id; stable within the current descriptor.
    }

    public readonly struct FieldId
    {
        // Opaque schema field id. This is not a column index or header string.
    }

    public readonly struct PropertyPathId
    {
        // Opaque generated property path id for diagnostics/editor mapping.
    }

    public enum ChangeEventKind
    {
        Added,
        Removed,
        Moved,
        Renamed,
        Recreated,
        PropertyChanged,
        DependencyChanged,
        SourceSummary
    }

    [Flags]
    public enum ChangeEventKindMask
    {
        Added = 1 << 0,
        Removed = 1 << 1,
        Moved = 1 << 2,
        Renamed = 1 << 3,
        Recreated = 1 << 4,
        PropertyChanged = 1 << 5,
        DependencyChanged = 1 << 6,
        SourceSummary = 1 << 7,
        ObjectLifetime = Added | Removed | Recreated,
        ObjectData = PropertyChanged | DependencyChanged,
        PathOrName = Moved | Renamed,
        All = Added | Removed | Moved | Renamed | Recreated | PropertyChanged | DependencyChanged | SourceSummary
    }

    public readonly struct ChangeEvent
    {
        public readonly ChangeEventKind kind;
        public readonly AssetIdentity assetIdentity;
        public readonly AssetIdentity ownerIdentity;
        public readonly RuntimeTypeId runtimeTypeId;
        public readonly FieldId parentFieldId;
        public readonly int oldInstanceId;
        public readonly int newInstanceId;
        public readonly int changedFieldCount;
        public readonly int changedPropertyPathCount;
        public readonly int changedDependencyCount;
        public readonly bool hasOldPath;
        public readonly bool hasNewPath;
        public readonly bool hasOldKey;
        public readonly bool hasNewKey;
    }

    public readonly struct ChangeEventList
    {
        public int Count { get; }
        public ref readonly ChangeEvent this[int index] { get; }
        public Enumerator GetEnumerator();

        public struct Enumerator
        {
            public ref readonly ChangeEvent Current { get; }
            public bool MoveNext();
        }
    }

    public readonly struct ChangeSet
    {
        public readonly DatabaseContextId contextId;
        public readonly RuntimeRevision revisionBefore;
        public readonly RuntimeRevision revisionAfter;
        public readonly SourceKind sourceKindBefore;
        public readonly SourceKind sourceKindAfter;
        public readonly bool isNoOp;
        public readonly ChangeEventList events;

        public RuntimeQueryStatus GetChangedFields(
            in ChangeEvent changeEvent,
            Span<FieldId> buffer,
            out int count);
        public RuntimeQueryStatus GetChangedPropertyPaths(
            in ChangeEvent changeEvent,
            Span<PropertyPathId> buffer,
            out int count);
        public RuntimeQueryStatus GetChangedDependencies(
            in ChangeEvent changeEvent,
            Span<DependencyTarget> buffer,
            out int count);
        public RuntimeQueryStatus GetAffectedAssets(
            ChangeEventKindMask includeKinds,
            Span<AssetIdentity> buffer,
            out int count);
    }

    public readonly struct SourceLayer
    {
        public SourceLayer(string layerId, DataSource source);
        public SourceLayer(string layerId, DataSource source, string layerDescriptorPath);
    }

    public readonly struct RuntimeCapacity
    {
        public static RuntimeCapacity FromDescriptor(string capacityJsonPath);

        public RuntimeCapacity(
            int objectCount,
            int keyCount,
            int referenceCount,
            int dependencyEdgeCount,
            int stringEntryCount,
            int stringByteCount,
            int rangeCount,
            int changeEventCount,
            int diffEntryCount,
            int loadedObjectCount,
            int loadedSegmentCount,
            int segmentWorkspaceBytes,
            int snapshotHandleCount,
            int snapshotPinCount,
            int parserWorkspaceBytes);

        public readonly int objectCount;
        public readonly int keyCount;
        public readonly int referenceCount;
        public readonly int dependencyEdgeCount;
        public readonly int stringEntryCount;
        public readonly int stringByteCount;
        public readonly int rangeCount;
        public readonly int changeEventCount;
        public readonly int diffEntryCount;
        public readonly int loadedObjectCount;
        public readonly int loadedSegmentCount;
        public readonly int segmentWorkspaceBytes;
        public readonly int snapshotHandleCount;
        public readonly int snapshotPinCount;
        public readonly int parserWorkspaceBytes;
    }

    public abstract class DataSource
    {
        public abstract SourceKind kind { get; }
        public abstract string sourceIdentity { get; }
        public abstract bool supportsRefresh { get; }
        public abstract bool supportsWatch { get; }
    }

    public sealed class ExcelDataSource : DataSource
    {
        public ExcelDataSource(string workbookPath);
        public ExcelDataSource(IEnumerable<string> workbookPaths);
    }

    public sealed class ConvertedBytesDataSource : DataSource
    {
        public ConvertedBytesDataSource(string bytesPath);
        public ConvertedBytesDataSource(in RuntimeArtifactLocator locator);
        public ConvertedBytesDataSource(byte[] bytes);
        public ConvertedBytesDataSource(byte[] bytes, ConvertedBytesBufferOwnership ownership);
        public static ConvertedBytesDataSource CopyFrom(ReadOnlySpan<byte> bytes);
        public static ConvertedBytesDataSource TakeOwnership(byte[] bytes);
        public static ConvertedBytesDataSource BorrowImmutable(ReadOnlyMemory<byte> bytes);
    }

    public sealed class CompositeDataSource : DataSource
    {
        public CompositeDataSource(DataSource baseSource, ReadOnlySpan<SourceLayer> layers);
        public CompositeDataSource(string sourceSetDescriptorPath);
    }

    public static class RuntimeDatabase
    {
        // Context accessors are defined in 14.2.1.

        public static bool Open(DataSource source);
        public static bool SwitchDataSource(DataSource source);
        public static bool Refresh();
        public static void Close();

        public static T LoadAsset<T>(string keyOrPath) where T : Object;
        public static bool TryGetAsset<T>(string keyOrPath, out T asset) where T : Object;
        public static bool TryGetAssetKey<T>(string keyOrPath, out AssetKey key) where T : Object;
        public static bool TryGetAsset<T>(AssetKey key, out T asset) where T : Object;
        public static bool TryGetAsset<T>(AssetIdentity identity, out T asset) where T : Object;
        public static RuntimeQueryStatus GetAssets<T>(Span<T> buffer, out int count) where T : Object;
        public static RuntimeQueryStatus GetDependencies(
            AssetIdentity identity,
            Span<DependencyTarget> buffer,
            out int count);
        public static RuntimeQueryStatus GetDependencies(
            AssetIdentity identity,
            in DependencyQuery query,
            Span<DependencyTarget> buffer,
            out int count);
        public static RuntimeSnapshot AcquireSnapshot();
        public static RuntimeSnapshot AcquireSnapshot(in RuntimeSnapshotOptions options);
        public static bool TryAcquireSnapshot(out RuntimeSnapshot snapshot);
        public static bool TryAcquireSnapshot(in RuntimeSnapshotOptions options, out RuntimeSnapshot snapshot);

        public static void EnableHotReload();
        public static void DisableHotReload();
        public static void Reserve(RuntimeCapacity capacity);
        public static void Prewarm();
        public static bool TryGetLoadSetId(string loadSetId, out RuntimeLoadSetId id);
        public static bool LoadSet(RuntimeLoadSetId id);
        public static bool PrewarmLoadSet(RuntimeLoadSetId id);
        public static bool UnloadSet(RuntimeLoadSetId id);
        public static RuntimeLoadSetStatus GetLoadSetStatus(RuntimeLoadSetId id);
        public static void SetAllocationPolicy(RuntimeAllocationPolicy policy);

        public static RuntimeMode mode { get; }
        public static RuntimeAllocationPolicy allocationPolicy { get; }
        public static DataSource currentSource { get; }
        public static OperationReport GetLastOperationReport();

        public static event Action<ChangeSet> changed;
        public static event Action<Object> objectChanged;
    }
}
```

默认返回和失败规则：

- `Open` 成功返回 `true`；失败返回 `false`，当前 context 保持 unopened，并写 report。
- `SwitchDataSource` 成功返回 `true`；失败返回 `false`，旧 source、旧 object graph、旧 indexes 继续服务。
- `Refresh` 有成功 commit 的数据变化时返回 `true`；无变化、hot reload 关闭或当前 source 不支持 refresh 时返回 `false` 并写最小 report。
- `defaultContext` 是 host/bootstrap 创建的 runtime context；`currentContext` 是当前绑定 scope 或 default context，所有 static API 都委托到它。
- `BindContext` 用于测试、工具、client/server 双 runtime 或明确的多数据源场景；scope 结束必须恢复前一个 current context。
- object、`AssetKey`、`RuntimeSnapshot`、load set handle、provider handle 都绑定创建它们的 runtime context；传给其他 context 时返回失败并记录 `runtime.context_mismatch`。
- 对仍存活但没有 current source 的 context 调用读取、snapshot acquire 或 load set operation 时返回 null/false/invalid handle，并记录 `runtime.source_not_open`；不能隐式重开上一次 source。
- 对已 `Dispose` 的 context 调用读取、source switch、snapshot acquire 或 load set operation 时返回失败并记录 `runtime.context_closed`；不能隐式创建新 context 或复用旧 handle。
- `LoadAsset<T>` 找不到、类型不匹配、asset invalid/missing 时返回 `null`。
- `TryGetAsset<T>` 不分配，成功写 `asset` 并返回 `true`；失败写 `null` 并返回 `false`。
- `TryGetAssetKey<T>(string, out AssetKey)` 把可读 key/path 解析成不分配查询 handle，推荐在初始化或预热阶段调用。
- `TryGetAsset<T>(AssetKey, out T)` 和 `TryGetAsset<T>(AssetIdentity, out T)` 是稳定帧推荐入口，不解析字符串。
- `GetAssets<T>(Span<T>)` 是 zero-allocation 枚举入口；返回 `RuntimeQueryStatus.Truncated` 时 `count` 是 total required count，实际写入数量为 `Min(buffer.Length, count)`。
- `GetDependencies(AssetIdentity, Span<DependencyTarget>)` 是 zero-allocation direct dependency traversal 入口，结果覆盖内部资产、Unity resource、本地化文本和外部 runtime key。
- `GetDependencies(AssetIdentity, in DependencyQuery, Span<DependencyTarget>)` 用于 recursive closure / preload plan 查询；buffer 不足时返回 `RuntimeQueryStatus.Truncated`，`count` 是 total required count，不在热路径创建 report/list/string。
- `RuntimeQueryStatus.InvalidHandle` 用于 `AssetKey`、`RuntimeLoadSetId`、`RuntimeSnapshot`、`ChangeEvent` 等 context-bound handle 失效、跨 context 或跨 revision 复用；它不能触发隐式重绑定或分配式诊断。
- `DependencyTarget` 只携带 opaque identity/provider key；显示 path、Unity main_asset_path、本地化 key 这类文本只能通过低频 debug/report API 查询。
- `TryAcquireSnapshot` 成功返回 pinned immutable snapshot；失败写 invalid snapshot 并返回 `false`，原因进入 report。失败常见原因包括 source 未打开、能力不支持、snapshot pool/pin table 不足或当前 publish 正在关闭。
- `AcquireSnapshot` 是便利入口；能力不满足或 pool 耗尽时按当前 error policy 抛 API 使用异常或返回 invalid snapshot 的具体行为必须由 adapter 固定，热路径推荐 `TryAcquireSnapshot`。
- `RuntimeSnapshot.GetAssets<TView>` / `TryGetAsset<TView>` 只返回 generated read view，不返回 `Object`；跨线程读取必须走 view getter。
- `RuntimeSnapshot.AsJobView()` 只能在 snapshot 具备 `JobSafe` 或更高能力时调用；否则返回 invalid job view 并记录 `runtime.snapshot_capability_unsupported`。
- `TryGetLoadSetId(string, out RuntimeLoadSetId)` 是初始化/低频入口，把可读 load set id 解析为 opaque handle；稳定帧内推荐缓存 handle。
- `LoadSet` / `PrewarmLoadSet` / `UnloadSet` 都是 RuntimeDatabase operation，可能读取 segment、扩容水位线、生成 report；它们不是 no-GC 热路径 API。
- `UnloadSet` 成功只卸载 payload/wrapper/provider handle，不删除 core identity/key/dependency index；被 active snapshot pin 住的 load set 不能卸载，被卸载 asset 的 identity 仍可用于事件和 report。
- 参数非法和 API 使用错误可以抛异常；source 内容错误必须进入 report。
- `SetAllocationPolicy` 只改变后续 operation / benchmark gate 的 budget 策略，不 retroactively 改写已有 report。
- Release 默认 allocation policy 至少是 `FailOnWatermarkGrowth`；开发和编辑器默认可用 `ReportWatermarkGrowth`，但 benchmark/CI 推荐显式切到 `FailOnAnyHotPathAllocation`。

DataSource 默认身份：

- `ExcelDataSource(string workbookPath)` 是单 workbook SourceSetDescriptor 的快捷入口；source identity 来自 source_set_id + source_set_hash，source_set_descriptor_hash 只作为 report/display identity。
- `ExcelDataSource(IEnumerable<string> workbookPaths)` 表示 workbook source set；必须先按 14.14.2 生成 SourceSetDescriptor，再用 source_set_id + source_set_hash 组合 source identity。
- `ConvertedBytesDataSource(string bytesPath)` 的 source identity 来自 normalized bytes path + converted bytes header source hash；reader 可以使用 read-only memory map 或 file buffer lease。
- `ConvertedBytesDataSource(in RuntimeArtifactLocator)` 通过 `RuntimeArtifactProviderRegistry` 找到 package provider，取得 immutable `RuntimeArtifactLease` 后再按 converted bytes reader 打开；locator 中的 artifact_id、manifest_hash、bytes_hash、provider id/key 必须和 package descriptor、artifact manifest、bytes header 交叉验证。
- `ConvertedBytesDataSource(byte[] bytes)` / `CopyFrom` / `TakeOwnership` / `BorrowImmutable` 的 data identity 都来自 converted bytes header source hash + bytes_hash；ownership mode 不改变 source identity，只进入 report、allocation summary 和 safety diagnostics。
- memory bytes 没有稳定文件路径时 path 为空；`supportsRefresh=false`，除非自定义 provider 显式提供 immutable refresh lease。
- artifact locator source 的 `supportsRefresh` 由 provider descriptor 决定；Release 默认不 watch，Development 可以显式启用 provider refresh 并仍按 source switch 事务提交。
- `RuntimeArtifactProviderRegistry.Register` 属于 host/bootstrap 初始化；provider lookup、manifest 读取、lease 绑定和 checksum 校验只允许发生在 `Open` / `SwitchDataSource` / `Refresh` / `LoadSet` operation 阶段，稳定字段读取、引用解析和 dependency traversal 不能回调 provider、做 IO 或分配。
- `RuntimeArtifactLease` 必须在当前 source、active snapshot、loaded segment 和 provider handle 全部释放后才能 Dispose；提前释放或底层 bytes 变化按 `bytes.source_mutated` / `bytes.manifest_mismatch` 处理并保留旧 source。
- `CompositeDataSource` 表示按 SourceLayerDescriptor 归一化后的 layered source；初始化阶段可以解析 manifest/profile、复制 layer descriptor 和构建 materialized view，稳定读取不沿 layer 链查询。
- `CompositeDataSource(string sourceSetDescriptorPath)` 从 canonical SourceSetDescriptor / layer manifest 构建 source；Release hotfix 推荐使用该入口或等价 bytes manifest。
- source identity 只用于 report、hot reload 和 source switch 判断，不替代 asset identity。

DataSource descriptor / opened source session 默认生命周期：

- public `DataSource` object 是 immutable descriptor / input carrier，不是 opened runtime source handle；它可以描述 workbook path set、bytes path、runtime artifact locator、memory bytes 或 composite layer graph。
- `Open` / `SwitchDataSource` / `Refresh` 根据 `DataSource` 创建内部 `OpenedSourceSession`：包含 verified source descriptor、provider/runtime artifact lease、immutable bytes/workbook materialized view、reader workspace binding、source revision、index roots 和 lifetime generation。
- `RuntimeDatabase.currentSource` 返回当前 source 的 descriptor projection；它不是 provider lease，也不能被调用方 `Dispose` 后影响已打开 session。
- path / locator / source-set descriptor 在 `DataSource` 构造时只做参数规范化和轻量身份描述；实际 IO、provider lookup、manifest/header verify、schema/bytes/source equivalence 校验只在 operation 阶段执行，并进入 operation report。
- `DataSource` descriptor 可以在同一或不同 context 中重复 `Open`，前提是它的 payload lifetime contract 仍有效；每个 context 拥有独立 `OpenedSourceSession`、revision、object graph、watcher registration、snapshot pin 和 report。
- `ConvertedBytesDataSource(byte[])` / `CopyFrom` 的 copied bytes 属于 descriptor-owned immutable payload；重复 open 共享同一 immutable payload，但每个 session 建自己的 reader/index binding。
- `TakeOwnership(byte[])` 后数组所有权转移给 descriptor；descriptor 与所有 active session/snapshot/load-set pin 释放前，调用方不得写入、归还对象池或复用数组。重复 open 允许共享该 immutable owned payload。
- `BorrowImmutable(ReadOnlyMemory<byte>)` 不转移所有权；调用方或 host lease 必须保证 descriptor、active session、active snapshot 和 loaded segment 生命周期内底层内存 immutable 且可读。无法证明时应使用 `Copy` 或 provider lease。
- `RuntimeArtifactLease` 和 file/memory-map handle 只属于 `OpenedSourceSession`；open 失败或 switch candidate 被丢弃时必须立即释放 candidate leases，不能泄漏到 descriptor。
- switch 成功后旧 session 进入 retired state；旧 session 的 immutable sections、provider leases 和 segment handles 在没有 active `RuntimeSnapshot`、pending ChangeSet dispatch、loaded load set pin 或 debug/report reader 后释放。
- switch 失败时 candidate session 完整释放，旧 session 继续 current；旧 object graph、indexes、provider leases 和 watcher registration 不受 candidate 副作用影响。
- close 使 current session 进入 retired state 并清空 current source/index；如果仍有 active snapshot pin，immutable sections 延迟释放，context 对新 read API 返回 `runtime.source_not_open`，但旧 snapshot 按 pin revision 可继续读到 Dispose。
- context `Dispose` 释放 owner queue、watcher、subscription、resident object table 和未发布 report；active snapshots 仍按 pin 规则延迟释放底层 immutable sections，但任何 owner API 和新 snapshot acquire 都返回 `runtime.context_closed`。
- `Refresh` 对 path/locator source 创建新 candidate session；当 source fingerprint/hash 未变化时可以丢弃 candidate 并保留 current session，不发布 property/dependency event。
- `Refresh` 对 memory bytes descriptor 默认 no-op；`BorrowImmutable` 底层内容变化不是 refresh 机制，而是 `bytes.source_mutated` contract violation。
- `supportsWatch=true` 只表示 descriptor 可以被 host watcher 监控并产生 refresh request；watcher registration 隶属于 opened session，close/switch retired session 时必须注销旧 watcher。
- no-GC 边界：descriptor 构造和 session open/verify 可以分配；达到水位线后的 current session read、snapshot acquire/release、source switch commit、candidate discard、retired session release bookkeeping 默认不得产生 GC allocation。

CompositeDataSource 初始化示例：

```csharp
var baseSource = new ConvertedBytesDataSource("ConfigData/base.bytes");
var hotfixLayer = new SourceLayer(
    "hotfix_2026_07_05",
    new ConvertedBytesDataSource("ConfigData/hotfix_2026_07_05.patch.bytes"),
    "ConfigData/hotfix_2026_07_05.layer.json");

RuntimeDatabase.Open(new CompositeDataSource(
    baseSource,
    new[] { hotfixLayer }));

RuntimeDatabase.Reserve(capacity);
RuntimeDatabase.Prewarm();
```

上面数组和 descriptor 解析属于初始化期；`Open/Prewarm` 后的 `LoadAsset`、key lookup、reference resolve 和 dependency traversal 只读 materialized view。

`Open` 默认语义：

- open 会完整读取 source header / schema hash / source hash / identity map / reference graph。
- 打开 `ConvertedBytesDataSource` 时必须先完成 header、content checksum、section directory、schema manifest、identity/key/reference/dependency index 交叉校验。
- 打开 `CompositeDataSource` 时必须先验证 SourceLayerDescriptor、layer order、base/target hash、activation、trust/signature，并构建 materialized source；任何 layer blocker 都使 open 失败且不暴露部分 object graph。
- open 成功后建立 canonical resident object cache、key index、guid/local id index、dependency graph。
- open 失败时没有部分可读状态；`LoadAsset` 返回 null，`GetLastOperationReport()` 给出原因。
- Release mode 只允许 `ConvertedBytesDataSource` 或由 converted bytes / signed patch bytes 组成的 `CompositeDataSource`；传入 `ExcelDataSource` 或 development override layer 返回 false + mode diagnostic。

`Close` 默认语义：

- close 关闭当前 source session，不销毁 `RuntimeDatabaseContext`，不修改任何 source 文件。
- close 清空 currentSource、indexes、dependency graph 和 hot reload watcher。
- resident instances 进入 `unloaded` 状态；后续 `LoadAsset` 返回 null，直到重新 `Open`。
- close 默认发布 `source_summary` ChangeSet，用于让业务清理 runtime cache；默认不把 source close 伪装成 workbook row delete，也不写 tombstone。
- 如果项目 profile 要求逐对象 unload 通知，必须作为 source-close lifetime event/report 投影实现，不能复用普通数据删除的语义影响 merge、tombstone 或 converted bytes。
- close 不参与 Excel dirty/save 生命周期；editor authoring dirty 必须由 `AssetDatabase` 管理。

`SwitchDataSource` 默认语义：

- switch 必须按 candidate snapshot 完整导入新 source，再和 current source 做 identity/reference diff。
- schema hash 不兼容、identity blocker、reference hard missing、validation blocker 时 switch 失败并保留旧 source。
- Composite source switch 先完成 layer materialization，再执行 identity/reference/dependency diff；不能把单个 layer 半应用到 resident graph。
- switch commit 优先 patch existing resident instances；无法 patch 的发 `recreated`。
- switch 成功后 currentSource 原子替换，ChangeSet 在 commit 后发布。
- Excel source 和 converted bytes source 如果 source hash 一致，应产生 no-op switch 或只更新 source summary，不重复发布 property_changed。

`Refresh` / hot reload 默认语义：

- `EnableHotReload` 只启用监听或允许 refresh commit；不立即读取 source。
- 手动 `Refresh()` 和 watcher 触发 refresh 走同一 transaction。
- watcher 触发的 refresh 必须进入 owner operation queue；commit / ChangeSet publish 只在 owner thread 的稳定 publish point 执行。
- 当前 source 是 `ExcelDataSource` 时，refresh 读取 workbook fingerprint/source hash；未变化则 no-op。
- 当前 source 是 path-based `ConvertedBytesDataSource` 时，refresh 可以读取新 bytes 并按 source switch 语义 patch。
- 当前 source 是 `CompositeDataSource` 时，refresh 检查 base 与所有 watchable layer 的 fingerprint/hash/signature/activation；任一变化都重建 materialized candidate，再按 source switch 语义 patch。
- 当前 source 是 memory bytes 时，refresh 默认 no-op；需要切换到新的 `ConvertedBytesDataSource(byte[])`。
- Development build hot reload 必须显式 opt-in；Release mode 下 `EnableHotReload` 明确失败或 no-op + report。

读取 API 默认语义：

- `LoadAsset<T>(string keyOrPath)` 默认先按 schema key lookup；如果字符串包含 workbook-style path root，再按 asset path lookup。
- key/path lookup 最终都解析到 row guid/local id；不会用 key 字符串作为引用身份。
- key/path lookup 必须服从 14.15.1 的 canonical key、scope、normalizer 和 duplicate 规则；duplicate key 返回失败而不是选择任意候选。
- 同一 context 内重复 load 返回 canonical resident instance。
- `AssetKey` 是 source context 内的 key/path 解析结果；source switch 后如果 identity 仍存在，handle 可以被重绑定，否则 `TryGetAsset` 返回 false 并通过 ChangeSet/report 说明 moved/removed/recreated。
- `AssetIdentity` 是 workbook guid + table id + row guid/local id 的 opaque struct；适合缓存和事件传递，不暴露旧 `TableId/RowId` 表模型给常规调用层。
- 字符串查询 API 必须内部不分配，但调用方在热路径构造字符串仍可能分配；高频路径默认使用 `AssetKey` / `AssetIdentity`。
- runtime read-only：不暴露 `SerializedObject` 写入，不允许 `EditorUtility.SetDirty` 产生可保存 dirty。

事件默认语义：

- `changed` 发布完整 ChangeSet，是推荐订阅入口。
- `objectChanged` 是便利事件，只对 added、property_changed、recreated 中可读取的 object 调用；它不表达 removed、moved、dependency closure，复杂缓存必须订阅 `changed`。
- non-alloc `ChangeSet.events` view 默认只在回调栈内有效；跨帧保存事件需要业务复制 stable identity / code。
- `changed` 先于 `objectChanged` 派发；`objectChanged` 从同一个 ChangeSet 派生，不改变事件 digest，也不能作为完整缓存失效依据。
- `changed` 回调内调用 `LoadAsset/TryGetAsset/GetDependencies/TryAcquireSnapshot` 默认读取 `revisionAfter`；旧 revision 只通过已持有的旧 snapshot 或 report/debug 查询。
- 订阅/退订在当前 context 上登记 stable subscription sequence；dispatch 开始后冻结 subscriber snapshot，回调中的订阅变化只影响下一次 dispatch。
- event 回调不能重入修改当前 ChangeSet；二次 `Refresh/SwitchDataSource/Open/Close` 默认排队到 publish 后。
- 尝试同步嵌套 publish 产生 `runtime.publish_reentrant` 或进入 owner queue；不能在当前回调栈中直接替换 source。
- 正式 runtime report 只保留 code、severity、source identity 和计数；Editor/Development 可以保留完整 location。

no-GC 默认语义：

- `Reserve(RuntimeCapacity)` 预留 object slot、key index、reference index、dependency edge、stable string pool、range view table、snapshot handle/pin table、ChangeSet/event buffer、parser workspace 和临时 diff buffer 的水位线。
- `Prewarm()` materialize 当前 source 的必要 runtime index、parser workspace 和可选 object pool；它允许分配。
- 完成 open + reserve/prewarm 后，`LoadAsset`、`TryGetAsset`、`TryGetAsset(AssetKey)`、snapshot acquire/read/release、引用解析、dependency traversal、source switch commit、hot reload patch、ChangeSet dispatch 默认不得产生 GC allocation。
- 如果 commit 超过水位线，允许扩容但必须写 allocation report；项目应能用下一次 `Reserve` 消除该分配。
- allocation policy 决定水位线增长和 hot path allocation 是 info/warning、operation failure 还是 CI failure；结果必须写入 `allocation_summary.budget_result`。

### 14.23 默认 schema source / codegen / registry 协议

schema authoring 首发默认使用 `.proto` + `exceldb/options.proto`。但系统运行时、Excel importer、converted bytes converter、Unity adapter 和非 Unity adapter 都不直接解释零散 proto option；它们共同依赖生成出的 canonical `SchemaDescriptor`。

默认产物：

```text
*.proto / schema source
-> SchemaDescriptor
-> generated C# types
-> generated registry bootstrap
-> generated Excel layout metadata
```

默认边界：

- `.proto` / schema source 是人维护的结构事实源。
- `SchemaDescriptor` 是工具链内部唯一 canonical schema representation。
- generated C# type 是对外强类型访问层，不是另一份结构事实源。
- workbook metadata 保存 descriptor 中的 table id、field id、layout hash、schema hash、row identity。
- converted bytes header 保存 descriptor hash；runtime 按 descriptor/hash 校验解释数据。

schema source set 默认规则：

- schema 编译输入不是“当前目录下碰巧能搜到的所有 proto 文件”，而是一个 materialized `SchemaSourceSetDescriptor`。
- `SchemaSourceSetDescriptor` 默认由 project policy / schema-lint command 生成，物理格式是 canonical JSON；它进入 schema-lint report 和 schema compile cache key。
- CLI 可以接受 source root / glob，但必须先展开成 source set manifest；后续 parse、descriptor、codegen、cache 都只看 manifest，不重新按文件系统顺序枚举。
- source set 中所有 path 都是 normalized project-relative logical path；不写本机绝对路径、Unity project path、CI workspace path。
- import/include resolution 必须唯一；同一个 logical import 命中多个 physical file、循环 import、缺失 import 都产生 `schema.source_set_invalid`。
- schema source 的文件顺序、空白、普通注释不影响 descriptor hash；但 `schema_source_set_hash` 覆盖参与编译的 file logical path、content hash、schema source kind、compiler option、extension option registry 和 generator target set。
- `.proto` 是首发默认 source kind；其他 YAML/JSON/DSL source 必须先归一化到同一个 `SchemaDescriptor`，不能让 importer/converter 分别解释不同 source kind。

`SchemaSourceSetDescriptor` 默认字段：

```text
source_set_format_version
schema_package_id
schema_source_kind
schema_source_set_hash
source_files[]
import_roots[]
dependency_locks[]
compiler_options{}
extension_option_registry_hash
generation_targets[]
```

`source_files[]` 默认字段：

```text
logical_path
content_hash
source_kind
package_id
role: schema | option | extension_descriptor | legacy_adapter
declared_schema_id optional
required
```

schema 编译默认阶段：

1. materialize source set manifest。
2. resolve import/include/dependency lock，生成 deterministic source graph。
3. parse source，并把 source diagnostics 定位到 source map。
4. normalize options / extension options；未知 option 默认 `schema.option_invalid`。
5. validate stable ids、reserved/deprecated registry、alias、value shape、reference target、validator/migration registry。
6. 生成 normalized descriptor model。
7. 写 canonical descriptor bytes、`descriptor_hash`、`schema_hash`、`layout_hash`、`codegen_hash`。
8. 生成 binding manifest、registry manifest、layout metadata 和 descriptor source map。
9. 输出 schema-lint report；任何 blocker 阶段失败时不写后续 generated artifact。

descriptor source map 默认规则：

- source map 是诊断和 IDE 跳转 artifact，不进入 `descriptor_hash`、`schema_hash`、`layout_hash` 或 `codegen_hash`。
- source map record 使用 descriptor path 作为 key，例如 `table:100.field:12.validator:range`，value 指向 logical path、line、column、source span 和 option path。
- source map 不存在时，diagnostic 仍必须能落到 descriptor path / table id / field id；不能因为缺 source map 而改变 lint 结论。
- generated code 中的 `#line` / source link 只用于调试，不参与 hash。

`SchemaDescriptor` 默认包含：

```text
schema id / name / version
schema hash
layout hash
table descriptors[]
enum descriptors[]
simple struct descriptors[]
advanced value shape descriptors[]
reference family descriptors[]
localization descriptors[]
expression descriptors[]
weighted selection descriptors[]
validator descriptors[]
editor capability descriptors[]
export view descriptors[]
```

table descriptor 默认字段：

```text
table id
schema name
runtime type full name
generated C# type full name
sheet display name
asset kind: asset | embedded | helper
table version
export policy
key field ids
key descriptor
display name field id
fields[]
```

field descriptor 默认字段：

```text
field id
schema field path
C# member name
serialized property path
display name
description
header comment
aliases
property aliases
value shape
enum/simple struct/reference descriptor identity
required/default/nullability
export policy
layout mode
validation rules
deprecated/reserved flags
```

默认 id 规则：

- table id 必须显式声明；缺失是 schema blocker。
- field id 默认优先显式声明；没有显式 field id 时才允许使用 proto field number 作为初始 field id。
- 一旦 workbook/bytes 发布，table id 和 field id 不能因 rename、移动、C# 属性改名而改变。
- field id 冲突、table id 冲突、enum value id 冲突都是 schema blocker。
- enum value 必须有 stable value id；display name / token rename 不改变 value id。

id identity / scope 默认规则：

- 所有 numeric schema id 使用 positive `uint32` 语义；`0` 是 invalid / unassigned，不能出现在 canonical descriptor、workbook metadata、converted bytes、binding manifest 或 report 的 stable identity 字段中。
- schema source 和 workbook metadata 中的 numeric id 一律以 canonical unsigned decimal text 保存；Excel 单元格显示可以是文本或受保护 helper cell，不能依赖 Excel number formatting 或浮点精度保存大整数。
- `schema_package_id` 是 id namespace 边界；不同 package 的 id 可以相同，但任何跨 package reference、migration、suppression、diagnostic location 都必须携带 package id，不能只写裸 numeric id。
- `table_id` 和 `child_table_id` 在同一 schema package 内共用全局 table id scope；child table 不是字段附属的临时编号，进入 workbook/bytes 后和普通 table 一样不能复用。
- `field_id` 只在 owner descriptor scope 内唯一；owner 可以是 table、simple struct、map entry、union variant payload 或 generated value-shape payload。字段完整身份是 `owner_descriptor_identity + field_id`，不是字段名、列号或 property path。
- `enum_id` 在 schema package 内唯一；`enum_value_id` 在所属 enum 内唯一。enum token、display name、排序和 generated C# enum member name 都不是 value identity。
- `variant_id` 在所属 union/oneof descriptor 内唯一；variant payload 里的字段使用该 variant payload 自己的 field scope。
- simple struct descriptor、advanced value shape descriptor、map/union/expression/weighted/curve/label/preset descriptor、validator、drawer、codec、migration、normalizer、provider、extension、profile 和 command descriptor 使用 package-qualified stable string `descriptor_id`，并使用独立 `descriptor_version` 表达 semantic version，例如 `descriptor_id=com.game.balance.DropTableShape`、`descriptor_version=1`。`com.game.balance.DropTableShape@1` 只能作为 report/display shorthand，不作为 canonical field value。
- descriptor 完整身份是 `descriptor_id + descriptor_version`；descriptor id 不和 numeric schema id 混用。simple struct / advanced value shape 内部的子字段仍使用 owner-scoped numeric `field_id`；union/oneof descriptor 使用 package-qualified descriptor id + version，variant 使用 owner-scoped numeric `variant_id`。
- display name、sheet name、header text、schema field path、C# member name、SerializedProperty path、Excel row/column number、文件顺序、proto declaration order、运行时 hash 都不能作为 id，也不能作为 published id 冲突时的自动 tie-breaker。

id range / allocation registry 默认字段：

```text
schema_package_id
registry_revision
registry_hash
id_ranges[]
active_ids[]
reserved_ids[]
deprecated_ids[]
allocation_history[]
```

`id_ranges[]` 默认字段：

```text
range_id
owner_package_id
id_kind: table | field | enum | enum_value | variant
scope_descriptor_path optional
start_inclusive
end_inclusive
allocation_policy: manual_only | allocate_lowest_free
```

不进入 numeric `id_ranges[]` 的 descriptor 默认使用 package-qualified descriptor id + 独立 version；其冲突、缺失、version 不兼容和 reserved/deprecated 规则由 `DescriptorRegistry` / extension governance 处理，不能临时借用 table/field numeric range。

numeric reserved/deprecated record 默认字段：

```text
id_kind
scope_identity
numeric_id
previous_descriptor_path
previous_display_name optional
state: deprecated | reserved
reason
replacement_identity optional
first_seen_descriptor_hash
retired_descriptor_hash
retired_by_migration_id optional
```

descriptor reserved/deprecated record 默认字段：

```text
descriptor_kind
descriptor_id
descriptor_version
previous_descriptor_hash
previous_display_name optional
state: deprecated | reserved
reason
replacement_descriptor_identity optional
retired_by_migration_id optional
```

id 分配默认规则：

- `schema-lint` 构建 canonical descriptor 时不分配 id；它只能验证 source / registry 已经声明的 id。
- `allocate-id` 是独立 authoring operation，只能修改 schema source / id registry，或输出 `AutoFixProposal` / patch proposal；它不能在 import、convert、runtime open、hot reload、canonical descriptor build 或 generated codegen 阶段注入 transient id。
- 没有显式 id 的全新 descriptor，只有在 `allocate-id` 成功写回 source / registry 后，下一次 `schema-lint` 才能把它纳入 canonical descriptor。未落盘 id 一律产生 `schema.id_missing`。
- `.proto` field number 只允许作为第一次 `allocate-id` 的 seed / request hint；一旦 descriptor 发布，field id 必须由显式 option 或 id registry lock 记录。之后修改 proto field number 不改变 field id；如果显式 id 缺失，按 `schema.id_missing` 处理，而不是重新使用新的 proto number。
- 默认 allocator 对同一批 request 按 stable request key 排序，key 依次包含 `id_kind`、`scope_identity`、`logical_source_path`、`descriptor_path`、`option_path`、`schema_name`；不能用文件系统枚举顺序、AST 遍历副作用、当前时间、随机数或字典顺序。
- `allocate_lowest_free` 从所属 range 中选择未 active、未 deprecated、未 reserved、未在当前 batch 占用的最小 id；`manual_only` range 不自动分配，缺 id 时只报告 proposal。
- 同一 source set hash、registry hash 和 request set 在不同机器、不同文件系统顺序、不同 CLI 调用顺序下必须产生完全相同的 allocation manifest、source patch 和 report hash。
- `allocate-id` 必须带 precondition：`schema_source_set_hash`、`registry_hash`、相关 source file content hash 和 project policy hash。任一变化后 apply proposal 产生 `schema.id_allocation_stale`，不写部分 source。
- range 缺失、range 重叠、range owner 不匹配、start/end 非法、scope_descriptor_path 不存在或 allocation policy 不合法，产生 `schema.id_range_invalid`。
- 同一 id 在 active set 内被两个 descriptor 声明时，table/field/enum value 使用专用 code；其他 descriptor id 使用 `schema.id_conflict`，report payload 必须包含 id_kind、scope_identity、两个 descriptor path 和 source location。

published id / reserved 默认规则：

- 一旦 numeric id 或 descriptor identity 出现在任何成功发布的 `SchemaDescriptor`、generated binding manifest、workbook metadata、converted bytes、artifact manifest、migration history、machine-readable report 或 suppression key 中，就成为 published id。
- 删除 published table/field/enum value/variant/simple struct payload 时，schema source 或 `NumericIdRegistry` 必须留下 numeric `deprecated` 或 `reserved` record；删除 published simple struct / advanced value shape / expression / weighted / curve / label / preset / validator / codec / provider 等 descriptor identity 时，`DescriptorRegistry` 必须留下 descriptor `deprecated` 或 `reserved` record。直接消失是 schema-lint blocker。
- `deprecated` 表示 descriptor 仍可被读取、迁移或在 Excel 中以 deprecated layout 保留，但默认不允许新增引用或新数据；是否导出由 export/migration policy 决定。
- numeric `reserved` 表示 numeric id 已不再 active，但同一 scope 内的 numeric id 永久不可复用；descriptor `reserved` 表示同一 `descriptor_id + descriptor_version` 不能被不同 descriptor hash 重新定义。reserved record 可以保留 replacement identity 和 migration hint，但不能自动把旧数据搬到 replacement。
- active numeric descriptor 不能使用 reserved numeric id；新 numeric descriptor 不能使用 deprecated numeric id。descriptor registry 中同一 `descriptor_id + descriptor_version` 的 descriptor hash 不一致，或 reserved/deprecated descriptor identity 被新语义复用，产生 `schema.reserved_id_reuse` 或更具体的 registry diagnostic。即使 display name、field path 或 token 与旧 descriptor 相同，也不能绕过。
- 删除仍有 workbook 数据、bytes 数据、reference、migration history 或 report suppression 的 id 时，reserved/deprecated record 只是兼容前提；是否能写 workbook、convert 或 runtime switch 仍由 migration/compatibility gate 决定。
- alias 只能帮助 layout repair、visible path 兼容和 migration suggestion；alias 不能解除 reserved id 复用，不能把不同 id 自动视为 rename。
- `NumericIdRegistry` 和 `DescriptorRegistry` 都是 canonical descriptor 的语义输入；active/reserved/deprecated set 进入 `descriptor_hash` 和 `schema_hash`，allocation_history 只进入 registry/report，不进入 schema_hash，除非它改变 active/reserved/deprecated 事实。

workbook / bytes / generated binding 默认规则：

- workbook metadata 保存 last successful descriptor id set 与 minimal previous descriptor snapshot；如果当前 schema source 删除了 id 且没有 reserved/deprecated record，打开 workbook 进入 degraded analyze 或 read-only，不能自动按 header/path 修复。
- Excel 表头、metadata sheet 或 converted bytes 里发现 schema 未声明的 active id 时，不能为 schema 反向创建 id；只能按 unknown/deprecated/preserved data、migration required 或 blocker 分类。
- generated binding manifest 必须记录 table id -> runtime type，field id -> C# member / property path / runtime accessor slot；C# member rename 只改变 codegen hash，不改变 id。
- compatibility diff 首先按 id 匹配；相同 name/path/token 但 id 不同是 delete + add，必须有 migration 才能搬数据。
- source switch / hot reload candidate 中如果 descriptor id set 与 current generated registry 不匹配，必须在 publish 前失败并保留旧 source/object graph。

默认命名绑定：

- `schema field path` 是 descriptor 内的 canonical field path；默认同时作为 Excel metadata、SerializedProperty path、converted bytes mapping 的主字段路径。
- `C# member name` 是生成代码的调用便利，默认由 schema field name 转为 PascalCase。
- `serialized property path` 默认等于 schema field path；不等于 C# member name。
- `canonical_property_path` 是当前 `FindProperty` 的正式路径；`property_aliases[]` 是旧 editor / 旧 drawer 的兼容路径，只能指向同一个 field id，不能跨 field id 自动搬数据。
- `FindProperty(path)` 默认先查 canonical path，再查 alias map；alias 命中时返回当前字段的 property handle，`property.propertyPath` 仍返回 canonical path，并在 editor capability / schema-lint / debug report 中标记 obsolete alias 使用。稳定 inspector repaint 不能为每次 alias 命中分配 report。
- 例子：schema 字段 `mana_cost` 默认生成 C# 属性 `ManaCost`，但 `FindProperty("mana_cost")` 才是稳定 property path。
- 旧 schema 字段 `cost` rename 为 `mana_cost` 时，`FindProperty("cost")` 只有在 `property_aliases=["cost"]` 且 field id 未变时才成功；没有 alias 时返回 null，不按相似名字猜测。
- Excel header display name / 中文名 不参与 property path 和 field identity。
- C# namespace / type rename 必须通过 table descriptor 的 runtime type full name 兼容规则处理，不能改变 table id。

generated C# type 默认规则：

- asset table 默认生成 `public partial class XxxConfig : ExcelDbEngine.ScriptableObject`。
- embedded/simple struct 默认生成 `public partial struct` 或 `public partial class`，由 schema value shape 决定；但它不能成为独立 asset identity，除非 schema 声明 child/sub asset。
- generated type 必须是 partial，允许项目写 custom methods / drawers / validators，但禁止手写字段覆盖 generated field mapping。
- generated code 不引用 `UnityEngine` / `UnityEditor`；Unity adapter 的 drawer/picker 在单独 assembly。
- generated property getter 可以用于 runtime hot path；public setter 默认只在 editor authoring direct mutation overlay 中有效，进入 editor draft 必须再执行 `EditorUtility.SetDirty`。需要绕过 direct mutation overlay 时，从一开始使用 `SerializedObject`。
- runtime read-only mode 下 setter 必须明确失败、no-op + report，或在生成配置中不暴露 public setter；不能静默改 resident runtime data，也不能创建 editor dirty。

registry 默认规则：

- generated registry bootstrap 在初始化阶段注册所有 `SchemaDescriptor`、runtime type、table id、field id、reference family、validator、drawer hint。
- registry 初始化可以分配和使用反射；稳定热路径不得依赖反射查字段。
- registry 必须检查 table id/type name/schema name 冲突、field id/path 冲突、enum value id 冲突、reference target 不存在。
- `SerializedObject`、Excel importer、converted bytes converter、RuntimeDatabase 都从同一 registry 查询 descriptor。
- 不允许 editor adapter 和 runtime converter 各自构造独立 schema view。

schema hash 默认规则：

- schema hash 来自 canonical descriptor 的语义字段，不来自 proto 文件文本、注释顺序、C# 生成时间或本机路径。
- layout hash 来自 Excel authoring layout、display name、description、header comment、列顺序、data validation 展示。
- codegen hash 来自 generated C# public API surface、runtime accessor binding 和 registry bootstrap contract，不来自生成文件格式、banner、换行或时间戳。
- 只改 C# member name 且 schema field path / field id 不变，不改变 schema hash；如果 public API rename 需要兼容层，应由 generated code/adapter 处理。
- 只改 generated code 格式、注释或 partial method stub，不改变 schema hash / layout hash / codegen hash。

#### 14.23.1 默认 canonical descriptor / codegen contract

任何工具都不能直接从 `.proto` AST、C# reflection、Excel header 或生成代码推导自己的 schema view。默认流程是：

```text
schema source
-> normalized descriptor model
-> canonical descriptor bytes
-> descriptor_hash / schema_hash / layout_hash
-> generated code + registry manifest
```

canonical descriptor bytes 默认规则：

- 使用工具链定义的二进制 canonical format；v1 默认所有整数 little-endian，string 为 UTF-8，bool 为 `0/1`。
- canonical format 必须带 `descriptor_format_version`、`canonical_writer_id`、`canonical_writer_version` 和 feature flags；reader 不支持版本时产生 `schema.unsupported_version`。
- 所有 repeated descriptor list 必须 deterministic 排序：table by table id，field by table id + field id，nested subfield by parent field id + field id，enum value by enum id + value id，validator/drawer/migration by stable id/version。
- 所有可选字段在 canonical bytes 中必须显式 materialize default；不能依赖 protobuf 默认值、语言默认值或字段缺省状态。
- map/dictionary 字段必须先归一化为 sorted entry list；排序 key 是 canonical key bytes，不能用运行时 hash 或当前文化比较。
- 浮点默认值必须使用 IEEE-754 bit pattern 写入；decimal/fixed point 使用 unscaled integer + scale；不能写本地化文本。
- 不写入 source file path、source line number、schema source 注释文本、生成时间、机器用户名、绝对路径、换行风格。
- schema-lint 必须能输出 `descriptor.json` / `descriptor.bin` 用于对比；两个等价 schema source 在不同机器输出 byte-for-byte 相同 descriptor bytes 和 descriptor_hash。

hash 输入分类默认规则：

```text
descriptor_hash: canonical descriptor 的完整稳定内容，包含 semantic + layout + editor/tool extension descriptor
schema_hash: 会影响 import/export/runtime 解释、identity、reference、validation blocker、converted bytes layout 的语义字段
layout_hash: 会影响 Excel authoring layout、display、header comment、dropdown、列顺序和 editor presentation 的字段
codegen_hash: 会影响 generated C# public API surface 的字段
```

`descriptor_hash` 默认覆盖：

- canonical descriptor 中所有 stable 字段，包括 semantic、layout、editor capability、extension descriptor、reserved/deprecated registry、compatibility metadata。
- canonical writer id/version 和 descriptor format version。
- 不覆盖 schema source file path、生成时间、report 文本、diagnostic projection。

默认不进入任何 hash：

- schema source 注释、文件顺序、空白。
- generated code 格式、banner、时间戳。
- 本机路径、Unity project path、CI workspace path。
- diagnostic projection、report 文本、本地化文本。

语义字段默认进入 `schema_hash`：

- table id、asset kind、runtime type binding、export policy。
- field id、schema field path、value shape、required/default/nullability、parse policy、export policy。
- enum value id、export token、deprecated/reserved 影响。
- simple struct subfield id/path、single-cell codec id/version、expanded layout semantics。
- reference family、target scope、strength、delete policy、ownership。
- key descriptor、path pattern、normalizer id/version、duplicate policy、rename policy。
- localization token policy、required locale policy 和 runtime provider id/version。
- expression grammar id/version、function registry id/version、symbol binding 和 deterministic budget。
- weighted selection algorithm id/version、weight/probability mode 和 RNG contract。
- runtime provider id/version/key binding、UnityResourceRef guid/local id semantics。
- generated runtime layout binding 中会影响 converted bytes reader、field offset、string/range/reference slot 的部分。
- validator id/version/mode/severity 中会影响 import/export/runtime blocker 的部分。
- migration descriptor 中会影响 schema compatibility 的 stable id/version/from/to/dependency/data-loss fields。

布局字段默认只进入 `layout_hash`：

- display name / 中文名。
- description。
- header comment。
- Excel column order、column width、freeze/filter authoring hints。
- dropdown/helper sheet 展示格式。
- editor group/category/tooltip/readonly visibility，只要不改变 validation/export/runtime 语义。

`codegen_hash` 默认覆盖：

- generated type full name、namespace、base type、partial type kind。
- public property/method surface、C# member name、obsolete/alias member、generated enum names。
- `SerializedProperty` path binding、runtime accessor binding manifest、generated view type shape。
- generated setter policy、nullable/default state accessor naming、range view type naming。
- registry bootstrap entry shape 和 runtime type binding table。

`codegen_hash` 默认不覆盖：

- whitespace、file splitting、banner、comments、region order、generated timestamp。
- private helper method names，只要 public API、runtime binding 和 generated behavior 不变。
- user-written partial method body；partial extension 不能改变 descriptor/hash。

ID 分配 / 保留默认规则：

- table id、field id、enum value id、child table id、variant id 是长期稳定 numeric ID；validator、normalizer、codec、drawer、editor、migration、reference family、runtime provider 和 extension 使用长期稳定 descriptor id + 独立 version。
- table id、field id、enum id、enum value id、child table id、variant id 默认使用 canonical unsigned integer id；0 对 table/field/enum/child/variant 表示 invalid/unset，enum value id 可以显式使用 0 表达 `None/Unknown` 等业务值。
- validator id、normalizer id、codec id、drawer id、editor id、migration id、reference family id、runtime provider id、extension id 默认使用 package-qualified stable string id，semantic version 存在独立 version 字段；不能用显示名或 C# 类型短名当身份。
- table id 和 child table id 在整个 schema package 内全局唯一；field id 在所属 table / struct / variant payload scope 内唯一；enum value id 在所属 enum 内唯一；variant id 在所属 union/oneof descriptor 内唯一。
- nested field 的 canonical identity 是 parent field id chain + child field id；不能只用最终叶子 field id 解释展开列。
- 已经发布到 workbook/bytes 的 numeric id 删除后必须进入 `NumericIdRegistry` 的 reserved/deprecated；已经发布的 descriptor identity 删除后必须进入 `DescriptorRegistry` 的 reserved/deprecated。两者都不能复用给新含义。
- id rename 不是合法概念；只能改 display name、schema field path alias、C# member alias，不能改 id。
- field move 到 struct、child table 或其他 table 时，如果语义身份保持，必须通过 migration 明确记录映射；不能靠路径相似自动搬。
- proto field number 只能作为初次生成 field id 的默认来源；一旦 descriptor 发布，后续以 descriptor id 为准，不随 proto 重排变化。
- proto enum numeric value 可以作为初次生成 enum value id 的默认来源；发布后 enum numeric 重排等同 value id 改变，必须走 migration 或 reserved/deprecated。
- table id、enum id、child table id、variant id 默认必须显式声明；缺失产生 `schema.id_missing`，不能由文件顺序或类型名 hash 临时生成。
- schema 工具可以提供 `allocate-id` / quick fix，但它只能修改 schema source 或生成 proposal；不能在 canonical descriptor 阶段注入未落盘的 ID。
- 自动分配 ID 时默认选择当前 scope 内大于历史最大值的最小可用 id；不得填补 reserved/deprecated id 的空洞。
- schema-lint 必须检查 id missing、reserved id reuse、id collision、alias collision、field path collision、property path collision、runtime type binding collision。

reserved/deprecated registry 默认字段：

```text
descriptor_kind
scope_identity optional
stable_id
old_display_name optional
old_schema_path optional
first_seen_descriptor_hash
retired_descriptor_hash
replacement_id optional
reason
data_migration_required
```

alias 默认规则：

- alias 只服务 layout repair、旧 workbook import、旧 C# member compatibility 或 migration suggestion；alias 不能成为新的事实身份。
- 同一 scope 内 alias、current schema field path、C# alias member、enum token alias 必须唯一解析到一个 stable id；无法唯一解析产生 `schema.alias_ambiguous`。
- alias 命中后，repair/writeback 应写回当前 canonical field path / token / display，不长期保留旧 alias 作为活跃展示。
- alias 删除前必须确认没有 workbook metadata、previous descriptor snapshot 或 migration 仍依赖它；否则 schema-lint warning 或 migration blocker 由 project policy 决定。

previous descriptor snapshot 默认规则：

- workbook metadata 必须保存 last successful `descriptor_hash`、`schema_hash`、`layout_hash`、`codegen_hash` 和 minimal previous descriptor snapshot reference。
- previous descriptor snapshot 至少包含 table/field/enum ids、field paths、value shape、reference/key/export/validator compatibility-relevant fields 和 reserved/deprecated registry。
- schema compatibility analyze 必须从 previous/current canonical descriptor 生成 diff；不能从 proto 文本、C# reflection、Excel header 或 generated code 猜。
- previous snapshot 缺失时只能做 degraded analyze：允许 read-only open/report，禁止自动 migration apply、批量 save 和 destructive layout rewrite。
- 如果 workbook metadata 的 descriptor_hash 与 embedded previous snapshot hash 不一致，产生 `schema.descriptor_hash_mismatch` blocker，不能继续用该 snapshot 做 migration。

generated binding manifest 默认字段：

```text
binding_manifest_format_version
descriptor_hash
schema_hash
layout_hash
codegen_hash
runtime_type_bindings[]
field_property_bindings[]
runtime_accessor_bindings[]
enum_bindings[]
view_type_bindings[]
generator_id
generator_version
```

`runtime_type_bindings[]` 默认字段：

```text
table_id
runtime_type_full_name
generated_type_full_name
asset_kind
object_base_type
type_generation
```

`field_property_bindings[]` 默认字段：

```text
table_id
field_id
schema_field_path
serialized_property_path
csharp_member_name
value_shape
setter_policy
obsolete_alias_members[] optional
```

`runtime_accessor_bindings[]` 默认字段：

```text
table_id
field_id
field_storage_kind
field_offset_or_slot
string_id_slot optional
range_slot optional
reference_slot optional
default_state_slot optional
null_state_slot optional
getter_shape
hot_path_safe
```

binding manifest 默认规则：

- generated C#、RuntimeDatabase、converted bytes reader、SerializedObject 和 AssetDatabase 都必须使用同一个 binding manifest。
- runtime open 必须校验 bytes header 的 descriptor_hash/schema_hash 与当前 registry binding manifest 匹配；binding manifest 的 codegen_hash 与当前 generated assembly 不一致时产生 `schema.codegen_hash_mismatch`，不能继续用旧 accessor 解释 workbook/bytes。
- binding manifest 是 machine-readable artifact，使用 canonical JSON 或 canonical binary；hash 进入 codegen_hash / artifact manifest。
- 只改 private codegen helper 不改变 binding manifest；改 public getter/setter/property path/accessor slot 必须改变 codegen_hash。

binding 补充规则：

- runtime type binding 由 table id -> generated C# full name -> runtime type id 构成；table id 是事实源。
- field binding 由 table id + field id -> schema field path -> C# member name -> SerializedProperty path 构成。
- runtime accessor binding 由 table id + field id -> runtime field offset / string id slot / range slot / reference slot / generated getter shape 构成；converted bytes reader、RuntimeDatabase 和 generated C# type 必须使用同一 binding。
- C# member rename 可以通过 alias/obsolete wrapper 兼容，但不改变 schema field path 或 SerializedProperty path。
- generated registry 必须在初始化时验证 generated binding manifest 的 descriptor_hash 与当前 registered descriptor 匹配。
- 如果代码已更新但 workbook/bytes 的 schema_hash 旧，RuntimeDatabase / AssetDatabase 不能静默按新类型解释旧数据，必须走 compatibility/migration/runtime adapter。

schema-lint 默认检查：

- canonical descriptor 可以生成且 deterministic。
- schema_hash / layout_hash / codegen_hash 输入分类符合规则。
- schema source set 可解析、import 唯一、extension option 已登记；失败分别产生 `schema.source_set_invalid` / `schema.option_invalid`。
- table id、enum id、child table id、variant id 等必须显式声明；缺失产生 `schema.id_missing`。
- table id 全局唯一，冲突产生 `schema.table_id_conflict`；field id scope 内唯一，冲突产生 `schema.field_id_conflict`；enum value id enum 内唯一，冲突产生 `schema.enum_value_id_conflict`。
- reserved/deprecated id 未复用；复用产生 `schema.reserved_id_reuse`。
- alias 不歧义；同一 alias 可解析到多个 stable id 时产生 `schema.alias_ambiguous`。
- table/field/runtime type/property path 绑定唯一；property path 冲突产生 `schema.property_path_conflict`，runtime type binding 冲突产生 `schema.runtime_type_binding_conflict`。
- generated registry 中 descriptor、runtime type、validator、normalizer、codec、drawer、migration 的 id/version 不冲突。
- custom extension 必须声明 host requirement 和 deterministic flag；未声明时 schema blocker。

#### 14.23.2 默认 extension package / registry governance 协议

扩展点不是“随便注册一个回调”。所有自定义 codec、normalizer、validator、drawer、editor、migration、reference family、runtime provider、converter plugin 都必须作为 extension descriptor 进入 canonical descriptor / generated registry，才能被 schema-lint、CI、editor、convert 和 runtime 一致地识别。

extension package descriptor 默认字段：

```text
package_id
package_version
package_namespace
owner
host_requirements[]
extension_descriptors[]
dependencies[]
conflicts[]
deterministic
side_effect_policy
permissions[]
schema_hash_impact
layout_hash_impact
codegen_hash_impact
runtime_hash_impact
```

extension descriptor 默认字段：

```text
extension_kind
extension_id
extension_version
stable_namespace
entry_point
host_requirement
deterministic
phase
ordering_key
affected_table_ids[]
affected_field_ids[]
diagnostic_codes[]
auto_fix_kinds[]
dependencies[]
conflicts[]
capabilities[]
```

extension kind 默认集合：

```text
reference_family
value_codec
normalizer
validator
property_drawer
custom_editor
migration
runtime_provider
converter_plugin
source_backend
report_sink
```

id / namespace 默认规则：

- `package_id` 和 `extension_id` 使用 reverse-domain 或项目稳定前缀，例如 `com.gameplay.drop_table.weight_sum_validator`。
- 内置 ExcelDB id 使用 `exceldb.*`；Unity adapter id 使用 `exceldb.unity.*`；项目扩展不能占用这些前缀。
- 同一 `extension_kind + extension_id + extension_version` 的 descriptor hash 必须完全一致；否则 `extension.registry_conflict`。
- 删除已发布扩展 id/version 后必须进入 deprecated/reserved registry；不能把同 id/version 指给新行为。
- extension id 不包含本机路径、程序集路径、类名 hash 或随机 GUID；entry point 可以变，extension id 不随实现文件移动改变。

版本默认规则：

- `extension_version` 是语义 contract version，不是程序集 build number。
- 改变 parse/normalize/validate/convert/runtime key 语义时必须 bump extension version，并影响相应 hash。
- 只改 editor 展示、帮助文本、drawer UI layout 时不影响 schema hash；但可影响 layout_hash 或 editor capability hash。
- 修 bug 但不改变 canonical output，可以只 bump package version；如果会改变 normalized value、diagnostic code、bytes 输出或 runtime provider key，必须 bump extension version。
- schema-lint 必须能报告当前 descriptor 使用的 extension id/version 和 provider package。

host requirement 默认集合：

```text
core
editor
unity_editor
unity_runtime
development_runtime
ci
external_tool
```

host 默认规则：

- `core` extension 不依赖 UnityEngine / UnityEditor，不读宿主项目状态。
- `unity_editor` extension 只能在 Unity main thread adapter 阶段运行；core CLI 不能假装执行。
- `external_tool` extension 必须声明工具名、版本范围、输入输出文件和 deterministic guarantee；CI 不满足时 `extension.host_requirement_unavailable`。
- Release runtime 不加载 editor-only extension；converted bytes 必须已经包含 runtime 所需的稳定 provider id/version/key。

dependency / ordering 默认规则：

- extension dependencies 只能引用明确的 `extension_kind + extension_id + version range`。
- registry build 先按 dependency graph 拓扑排序，再按 `extension_kind` 固定顺序、`phase`、`ordering_key`、`extension_id` 排序。
- 同 phase 多个 validator 不能依赖注册顺序；如果顺序影响结果，必须声明 dependency 或合并为一个 validator。
- dependency 缺失是 `extension.dependency_missing` blocker；dependency cycle 是 `extension.dependency_cycle` blocker。
- 同一 target 上多个 drawer 只能通过 drawer 选择优先级决出一个；无法唯一决策时 `extension.registry_conflict`。

side effect 默认规则：

- codec / normalizer / validator / converter plugin 默认是 pure function：不能直接写 workbook、metadata、object graph、Unity asset database、文件系统或网络。
- 它们只能输出 normalized value、diagnostic、auto-fix proposal、converted bytes section 或 manifest proposal。
- auto-fix / migration / writeback 必须回到 operation transaction 执行。
- drawer / custom editor 可以产生用户交互和 `SerializedObject` 修改，但不能绕过 dirty/undo/save/conflict 协议。
- runtime provider 在 Release 中只能按 converted bytes manifest / provider registry 解析 runtime key；不能访问 UnityEditor.AssetDatabase 或 Excel source。

determinism 默认规则：

- 任何参与 schema_hash、source_hash、source_set_hash、bytes_hash、cache_key_hash 的 extension 必须声明 `deterministic=true`。
- deterministic extension 不得依赖当前时间、随机数、机器路径、字典遍历顺序、当前区域设置、网络状态或 Unity project scan 的非稳定顺序。
- 需要外部状态的 Unity validator / runtime provider 必须把状态 snapshot 写入 dependency manifest 或 report；未 snapshot 的外部状态不能影响 convert output。
- `deterministic=false` 的 extension 只允许用于 editor display / report sink / debug tooling，不允许参与 convert/build/runtime open。

hash impact 默认规则：

- value codec、normalizer、runtime provider、reference family converter 的 semantic version 进入 schema_hash / converter_registry_hash。
- validator 中会改变 import/export/runtime blocker 的 id/version/mode/severity 进入 schema_hash；只改变 message 文本不进入。
- drawer/custom editor 默认不进入 schema_hash；会改变 editable value shape、validation 或 writeback policy 时必须通过 schema descriptor 表达。
- migration descriptor 中 from/to/dependency/data-loss/host requirement 进入 descriptor_hash 和 schema compatibility 判断。
- source backend 版本如果会改变 raw cell parse/canonical value，进入 source_set_hash 或 converter_registry_hash。

registry build 默认流程：

1. 读取 generated registry bootstrap 和 extension package descriptors。
2. 验证 package id/version、namespace、host requirement、deterministic flag、diagnostic code registration。
3. 解析 dependencies/conflicts，构建 extension graph。
4. 选择当前 host/mode 可用 extension set。
5. 对不可用但 schema 必需的 extension 产生 blocker；对可 fallback 的 drawer/editor 进入 generic fallback 或 read-only。
6. 校验 permission requirements 与当前 `OperationProfile.extension_permission_policy`，拒绝未声明或未授予权限。
7. 输出 registry manifest，包含 extension id/version/descriptor hash、selected host set、granted/denied permissions、disabled/fallback reason。
8. registry manifest hash 纳入 descriptor_hash、codegen_hash 或 converter_registry_hash 的对应输入。

运行期默认规则：

- registry 初始化可以分配、反射、扫描程序集；稳定 runtime hot path 不查找 extension、不反射、不分配。
- runtime hot path 只使用已绑定的 generated accessor、provider handle、codec table 和 index。
- 运行中 hot reload/source switch 不允许加载新的 extension package；必须在 owner operation queue 的 safe point 重新初始化 registry，失败时保留旧 registry/source。

#### 14.23.3 默认 extension permission / external tool sandbox 协议

扩展权限不是宿主实现细节，而是 schema / profile / report 的一部分。任何扩展只要会读取外部状态、写文件、访问 Unity 项目、启动外部工具或联网，都必须先声明权限需求，再由当前 operation profile 授权。

permission requirement descriptor 默认字段：

```text
extension_id
permission_scope
filesystem_read_roots[]
filesystem_write_roots[]
cache_roots[]
network_access
declared_network_hosts[]
environment_variables[]
unity_project_access
external_process
external_tools[]
deterministic_inputs[]
declared_outputs[]
side_effect_policy
required_modes[]
optional_modes[]
```

外部工具 descriptor 默认字段：

```text
tool_id
tool_version_range
executable_identity
args_schema
working_directory_policy
stdin_policy
stdout_policy
stderr_policy
input_files[]
output_files[]
timeout_ms
deterministic_guarantee
```

权限默认规则：

- 默认 deny by default。没有声明的文件系统、网络、环境变量、Unity project mutation、外部进程启动都视为禁止。
- `core` extension 不拥有网络、Unity project、外部进程和任意文件系统写权限；只能消费本次 operation 显式传入的 canonical inputs。
- 文件系统 root 使用 logical root，例如 workbook source root、generated output root、operation temp root、cache root；descriptor 不写本机绝对路径。
- workbook / metadata / diagnostic / generated artifact 写入不能由 extension 直接落盘；只能生成 proposal，由 operation transaction 执行。
- `filesystem_write_roots` 只能覆盖 operation temp、cache、report 或 declared generated output；写到未声明路径产生 `extension.undeclared_side_effect`。
- 网络默认禁止。允许网络的 extension 只能用于 editor display、report sink 或显式 debug tooling；如果网络结果会影响 convert/build/runtime 输出，必须先 snapshot 为 declared input，并把 snapshot hash 写入 manifest。
- 环境变量默认不可读；允许读取的变量必须逐个声明。secret 值不能进入 descriptor/cache key/report 明文，也不能影响 deterministic output，除非先转成已声明的 redacted snapshot。
- Unity project access 分级为 `asset_database_read`、`asset_database_write`、`import_pipeline`、`build_pipeline`；Unity 写操作默认只允许 migration/auto-fix/tooling extension，并且仍要回到 transaction 或 Unity adapter command。

外部进程默认规则：

- 外部工具只能由 host runner 通过 descriptor 启动；extension 不能自己拼 shell command。
- `args_schema` 必须 deterministic，并且只能引用 declared input/output/temp/cache path token。
- runner 必须设置受控 working directory、timeout、stdout/stderr 上限和退出码捕获。
- tool executable identity、实际版本、resolved path hash 或 tool package hash 必须写入 report；会影响输出时进入 converter/runtime provider registry hash 或 cache key。
- 所有 input_files/output_files 必须计算 hash；output_files 之外出现写入或 mutation 时产生 `extension.undeclared_side_effect`。
- 非零退出码产生 `extension.external_tool_failed`；超时产生 `extension.external_tool_timeout`。失败结果不能被缓存为成功 artifact。

permission / hash / report 默认规则：

- permission requirement descriptor 进入 extension descriptor hash；granted permission set 进入 registry manifest。
- 授权策略只改变 pass/fail 时进入 `gate_policy_hash`；授权差异改变输出、runtime provider、generated artifact 或 source/backend 行为时也进入对应 output/cache hash。
- machine-readable report 必须记录 selected extension set、required/granted/denied permissions、external tool id/version、input/output hashes、timeout、exit code 和 side effect summary。
- required permission 在当前 host/profile 不可用时产生 `extension.permission_denied`；optional permission 只能降级为 fallback/read-only/debug-disabled，不能静默改变输出。
- CI 默认不弹交互确认；未在 project policy/profile allowlist 中授权的 required permission 直接失败。

运行时默认规则：

- Release runtime hot path 禁止外部进程、网络、UnityEditor、Excel source scan 和文件系统 discovery。
- runtime provider 必须在初始化阶段绑定 provider handle、runtime key table 和 capacity；稳定读取、引用解析和 dependency traversal 不触发权限检查、反射、外部 IO 或 GC allocation。
- Development runtime 若允许 debug source backend，也必须把权限授予写入 operation profile 和 startup report；热路径仍遵守 allocation policy。

### 14.24 默认 editor capability / drawer registry 协议

自定义 editor 和 property drawer 是编辑体验扩展，不是另一套数据写入系统。它们必须通过 `SchemaDescriptor`、`SerializedObject`、operation transaction、validation/report 协议工作。

editor capability descriptor 默认字段：

```text
editor id
editor version
target table ids / runtime types
supported schema hash or version range
supported layout hash or layout version range
required field ids
optional field ids
required reference families
required validators
required drawer ids
supported array edit policies
multi-object edit policy
migration ids
fallback mode
host requirement
```

fallback mode 默认集合：

```text
editable
read_only
table_view
reject_open
migration_required
```

打开自定义 editor 默认流程：

1. 读取 target object 的 table descriptor、schema hash、layout hash、field mapping revision。
2. 从 editor registry 找到 matching editor id / target table / runtime type。
3. 校验 editor capability：required fields、reference families、validators、drawer ids、schema/layout version。
4. 如果兼容，打开 editable editor，并且所有写入仍走 `SerializedObject`。
5. 如果缺 optional field，允许打开，但 UI 必须以 degraded capability 显示。
6. 如果缺 required field 且有 migration，进入 `migration_required`。
7. 如果不可迁移但 table view 可安全显示，进入 `table_view` 或 `read_only`。
8. 如果 editor 逻辑无法安全展示，`reject_open`，但 workbook/table 本身仍可通过普通 table view 和 report 打开。

默认禁止：

- custom editor 直接写 workbook cell、metadata sheet、runtime object field。
- custom editor 按 row number / column index / header text 作为身份。
- custom editor 在未通过 capability check 时继续编辑。
- drawer 解析 Excel 显示文本并绕过 schema value shape。
- drawer 在 `OnGUI` / repaint 热路径执行 import、validation、source switch 或分配大量临时对象。

property drawer registry 默认规则：

- drawer 注册 key 默认是 `drawer id + value shape + optional reference family + host requirement`。
- drawer 选择优先级：schema explicit drawer id > reference family drawer > value shape default drawer > generic fallback drawer。
- drawer registry 初始化可以分配和反射；绘制稳定路径不得产生 per-property 临时分配。
- drawer 缺失时使用 generic fallback drawer；如果字段是 required custom drawer 才进入 editor capability error。
- Unity adapter drawer 可以依赖 UnityEditor；核心 drawer contract 不依赖 UnityEngine / UnityEditor。

默认 built-in drawer：

- scalar number/string/bool。
- enum dropdown，选项来自 enum descriptor。
- simple struct `expanded_columns` group。
- simple struct `single_cell` codec editor。
- internal asset reference picker，按 schema target scope 搜索。
- `UnityResourceRef` picker，由 Unity adapter 提供 guid/path 展示。
- repeated/list drawer，完整目标版支持 append/delete/reorder；具体控件是否可见由 schema array edit policy 和当前 property capability 决定。

array edit policy 默认规则：

- `SerializedProperty.MoveArrayElement(srcIndex, dstIndex)` 是完整目标版 API surface；是否允许执行由 field descriptor 的 `array_edit_policy` 决定，不能由 drawer 临时猜。
- `array_edit_policy` 至少声明 `allow_append`、`allow_delete`、`allow_reorder`、`reorder_semantics` 和 `default_drawer_reorder_control`。
- `reorder_semantics=stable_element_identity` 用于 child table / owned element guid/local id / 声明了 stable element id 的 expanded list；reorder 只改变 order index，不改变 element identity。
- `reorder_semantics=whole_array_rewrite` 用于 repeated scalar、single-cell list、horizontal range list 或没有 stable element identity 的 simple struct list；reorder 是同一 field 的 whole-array value change，与外部同 array 任意 insert/delete/reorder/value edit 默认冲突。
- `reorder_semantics=forbidden` 用于 unordered map、schema 声明 order 无 runtime/editor 语义的集合、readonly/deprecated/reserved field，或 codec 不能 lossless roundtrip 的 single-cell list。
- 默认 policy：child table / owned element 使用 `stable_element_identity` 且默认显示拖拽 reorder；普通 repeated scalar/simple struct 使用 `whole_array_rewrite` 但 generic drawer 默认隐藏拖拽 reorder，只能通过明确 command 或 schema opt-in drawer 显示；unordered map 默认 `forbidden`。
- `MoveArrayElement` 命中 forbidden、index 越界、readonly、capability 不支持、codec 不可逆或 schema policy 禁止时，不改变 pending buffer，并记录 `serialized.array_edit_invalid` 或 `serialized.read_only`。
- custom editor capability 必须声明它依赖的 array edit policy；如果 editor 需要 stable reorder 但 schema 只有 whole-array rewrite，则进入 degraded/read_only/table_view，不能静默用 delete+insert 模拟。
- drawer 显示 reorder control 时必须使用 `GetArrayElementAtIndex` / `MoveArrayElement`，不能直接修改 C# list、row order index 或 Excel 行号；绘制稳定路径不得为每个元素分配临时 label/list/closure。

visibility / readonly 默认规则：

- visible/hidden/readonly/tooltip/group/category/array element label 都来自 descriptor。
- hidden field 仍可被 import/export/runtime 使用；只是默认 inspector 不显示。
- readonly field 可以显示但不能通过 drawer 修改；如果 Excel 直接修改 readonly runtime field，validation 按 schema policy 处理。
- editor-only field 可在 editor 显示，但不进入 converted bytes runtime object data。
- runtime-only/generated field 默认不允许 Excel 直接填写，除非 schema 声明 materialization/writeback policy。

custom editor migration 默认规则：

- editor migration 必须引用 schema migration id 或声明自己的 editor state migration id。
- editor migration 只能生成 operation transaction 的 migration proposal，不能直接改 workbook。
- editor capability 不兼容时，优先生成 machine-readable diagnostic，包含 editor id、required capability、actual schema/layout、fallback mode。
- editor version 变化不改变 runtime schema hash；如果它改变 schema meaning、validation 或 export policy，必须通过 schema descriptor 改变来体现。

multi-object edit 默认规则：

- 完整目标版不允许 custom editor multi-object edit 半实现；普通 property inspector 默认支持多对象写入，并必须遵守 14.24 的 common property scope、diff/merge、report 和 all-or-nothing policy。custom editor 只有在声明并通过 `multi_object_edit_policy` 后才开放写入；否则只能 read-only / table_view / reject_open。
- Unity adapter / generic inspector 遇到多选 ExcelDB asset 时，默认构建 writable multi-target surface；如果目标集合、schema/layout、field policy、drawer 或项目 profile 不兼容，降级为 read-only table/diagnostic view。任何路径都不能把多选退化为“只编辑第一个 target”。
- custom editor 要开放多对象编辑，必须在 editor capability descriptor 中声明 `multi_object_edit_policy`，并且目标 table / field / drawer / array edit policy 都通过 capability handshake；失败时报告 `serialized.multi_object_not_supported`、`serialized.multi_object_incompatible` 或更具体 diagnostic。
- `multi_object_edit_policy` 默认字段：`enabled`、`target_scope`、`common_property_scope`、`mixed_value_policy`、`apply_atomicity`、`array_policy`、`reference_policy`、`conflict_policy`、`partial_failure_policy`。
- `target_scope` 默认 `same_table_same_schema`；只有 schema 明确声明 compatible editor surface 时才允许跨 table / compatible runtime type 多选。table、schema_hash、layout_hash、property mapping 不兼容时产生 `serialized.multi_object_incompatible`。
- `common_property_scope` 默认只暴露所有 target 共有且 field id、value shape、reference policy、validator、readonly/export policy 一致的 property；不共有 property 不出现在 multi-target iterator 中，`FindProperty` 返回 null 并记录 `serialized.property_not_found`。
- `hasMultipleDifferentValues` 由 canonical state + canonical value hash 判定；display text、Excel cell formatting、本地化 preview、main_asset_path 展示变化不参与 mixed 判定。
- getter 在 mixed value 上可按 Unity-like 兼容返回第一个 target 的值，但 drawer 必须先看 `hasMultipleDifferentValues`；custom editor 若在 mixed value 上按 getter 结果做业务判断，属于 editor bug。
- setter 写 mixed 或 non-mixed property 时，默认把同一个 canonical pending value 应用到所有 target；如果 field policy 禁止批量覆盖 mixed value，setter 失败并记录 `serialized.mixed_value_unsupported`。
- reference setter 对每个 target 分别验证 target scope、same workbook / cross workbook policy、nullable、list duplicate 和 deleted/missing 状态；任一 target 失败时默认整个 pending edit 失败，不创建部分 pending buffer。
- array multi-edit 默认只允许 `array_policy=same_shape` 且每个 target 的 array value shape / element edit policy 一致；array size 不一致时只读显示 mixed。允许编辑时，`arraySize` 使用 Unity-like 最小长度作为可遍历范围，但 insert/delete/move 必须生成 per-target deterministic diff。
- child table array 多选编辑时，每个 target 的 child element identity 各自独立；不能把第一个 target 的 child row identity 复制给其他 target。reorder 只改各 target 的 order index。
- `ApplyModifiedProperties()` 默认 `apply_atomicity=all_or_nothing`：先为所有 target 生成 BatchEditOperationPlan，预检全部 target 的 object/source/schema/layout/mapping revision、dirty/conflict、validation、reference policy、affected workbook set 和 capacity；任何 blocker 都不写任何 editor draft。
- multi-object apply 成功时形成一个 undo group，包含所有 target 的 per-target diff；Undo/Redo 也必须 all-or-nothing，不能只回退一部分 target。
- 多目标中任一 target 在 apply 前被 Excel 外部修改，按普通三方 merge 处理；同一 property 发生 editor/external 冲突时生成 per-target conflict record，batch apply 失败并保留 pending buffer 或进入 resolver，不能跳过失败 target。
- `partial_failure_policy` 默认 `forbidden`；如果项目显式启用 `partial_with_report`，必须在 report 中列出 succeeded/failed/skipped target identities、per-target diff hash、失败原因和用户确认 hash，且不能作为完整目标版默认写入语义。
- multi-object operation report 必须包含 `multi_object_operation_id`、target identity list、common property set digest、mixed value summary、per-target diff hashes、affected workbook set、conflict ids、atomicity mode、capacity/watermark entries 和 result。
- 稳定 repaint、property traversal、mixed value hash 查询、common property set lookup 和 report append 在水位线后不得产生 GC allocation；target 数量或 property 数量超过容量时按 allocation policy 记录 `performance.watermark_grew` 或失败。

### 14.25 默认 CI / command line / build artifact 协议

CI 和构建不是编辑器按钮的自动化版本，而是正式包数据质量 gate。命令行、Unity build integration、非 Unity 构建都必须复用同一套 schema descriptor、workbook import、validation、convert 和 report 协议。

默认命令：

```text
exceldb schema-lint --schema <schema_root> [--profile <profile>] --report <report.json>
exceldb workbook-check --schema <schema_root> --workbooks <paths> [--profile <profile>] --report <report.json>
exceldb clone-workbook --schema <schema_root> --source <source.xlsx> --target <target.xlsx> [--profile <profile>] --report <report.json>
exceldb repair-workbook-guid --schema <schema_root> --workbook <workbook.xlsx> --as-new-source [--profile <profile>] --report <report.json>
exceldb snapshot --schema <schema_root> --workbooks <paths> --out <snapshot.json|dir> [--profile <profile>] --report <report.json>
exceldb diff-workbook --schema <schema_root> --before <workbook-or-snapshot> --after <workbook-or-snapshot> --out <diff.json> [--profile <profile>] --report <report.json>
exceldb merge-workbook --schema <schema_root> --base <workbook-or-snapshot> --ours <workbook.xlsx> --theirs <workbook.xlsx> --out <merged.xlsx> [--conflicts-out <conflicts.json>] [--profile <profile>] --report <report.json>
exceldb verify-source-set --schema <schema_root> --source-set <source-set.json> [--profile <profile>] --report <report.json>
exceldb materialize-defaults --schema <schema_root> --workbooks <paths> --dry-run --plan-out <operation-plan.json> [--profile <profile>] --report <report.json>
exceldb materialize-defaults --schema <schema_root> --workbooks <paths> --apply --plan <operation-plan.json> [--profile <profile>] --report <report.json>
exceldb migrate --schema <schema_root> --workbooks <paths> --dry-run --plan-out <migration-plan.json> [--profile <profile>] --report <report.json>
exceldb migrate --schema <schema_root> --workbooks <paths> --apply --plan <migration-plan.json> [--profile <profile>] --report <report.json>
exceldb convert --schema <schema_root> --workbooks <paths> --out <config.bytes> --manifest <manifest.json> [--profile <profile>] --report <report.json>
exceldb convert --schema <schema_root> --source-set <source-set.json> --out <config.bytes> --manifest <manifest.json> [--profile <profile>] --report <report.json>
exceldb verify-bytes --schema <schema_root> --bytes <config.bytes> --manifest <manifest.json> [--profile <profile>] --report <report.json>
exceldb benchmark-no-gc --schema <schema_root> --source <config.bytes|workbooks> --capacity <capacity.json> [--profile <profile>] --allocation-policy fail-on-any-hot-path-allocation --report <report.json>
```

默认 exit code：

```text
0 success
1 warning_as_error policy failed
2 validation/schema/import/convert error
3 blocker or data loss risk without confirmation
4 command line / environment error
5 internal tool error
```

命令默认规则：

- 所有命令默认输出 machine-readable report；human-readable report 可选。
- `--profile` 可以是内置 profile id、project policy 中的 named profile，或 profile JSON path；省略时按命令选择内置默认 profile。
- CLI 覆盖参数必须先并入 canonical `OperationProfile`，再执行命令；report 必须写 `operation_profile_id`、`operation_profile_hash` 和 `gate_policy_hash`。
- profile normalize、override allowlist、hard invariant 和 hash 规则必须按 14.12.1 执行；失败时产生 `command.profile_invalid` 或 `command.profile_override_forbidden`，不能继续写 workbook、bytes 或 manifest。
- 所有会写 workbook、bytes、manifest、generated artifact、Unity project 或切换 runtime source 的命令必须生成 `OperationPlan` 并在 report 中写 `operation_plan_hash`；`--dry-run` 只输出 plan/report，不提交 side effect。
- `schema-lint` 不读取 workbook 数据，只生成/校验 canonical descriptor、hash、binding manifest、id/reserved-id、reference family、validator/drawer/migration registry。
- `workbook-check` 读取 workbook，执行 import、schema compatibility、validation、reference graph，不输出 converted bytes。
- `clone-workbook` 复制 source workbook 到 target，并按 14.8.2 生成 new workbook guid、remap self references、保留 external references、重建 metadata checksum；source workbook 不被修改。
- `repair-workbook-guid --as-new-source` 只在用户明确把当前 workbook 作为新 source 时生成 new workbook guid；它必须走 backup/temp/verify/reimport，并输出 duplicate guid repair report。
- `snapshot` 读取 workbook 并输出 canonical WorkbookSnapshot；如果 `--out` 是目录，按 project artifact path policy 写入每个 workbook 的 snapshot。
- `diff-workbook` 输出 WorkbookDiff；输入可以是 xlsx 或可信 WorkbookSnapshot，但如果 xlsx 存在，默认重新 import xlsx 验证 snapshot freshness。
- `merge-workbook` 执行 14.13.3 的三方 workbook merge；有 unresolved conflict 时不写 `--out`，只输出 report/conflicts。
- `verify-source-set` 校验 SourceSetDescriptor、source_layers、layer order、activation、trust/signature、base hash 和 materialized_source_hash；不输出 bytes。
- `materialize-defaults --dry-run` 输出 OperationPlan、affected cells、old/new canonical state、source_hash_before/after、converted_bytes_effect 和 dry_run_report_hash，不写 workbook。
- `materialize-defaults --apply --plan` 必须验证 operation_plan_hash、dry_run_report_hash、source revision、schema/layout hash、default descriptor hash 和 package fingerprint，执行 backup/temp/verify/replace/reimport。
- `migrate --dry-run` 读取 workbook 并输出 MigrationPlan、candidate diff、data loss confirmation 和 recovery preflight，不写 workbook。
- `migrate --apply --plan` 必须验证 MigrationPlan 仍然有效，执行 backup/temp/verify/replace/reimport，并写 migration history。
- `convert` 必须先完成 `workbook-check` / `verify-source-set` 等价检查，再输出 bytes 和 artifact manifest。
- `verify-bytes` 打开 bytes，校验 header、artifact_kind、checksum、schema/source/source_set/layer_stack/materialized_source/preload plan hash、identity index、reference graph、dependency/preload plan index、layer_manifest/patch_operations 和 runtime index。
- `benchmark-no-gc` 执行 open + reserve + prewarm 后，按 14.10.2 的测量窗口验证 runtime 热路径、source switch/hot reload commit、ChangeSet dispatch、editor core preflight、machine-readable report append、report projection 和 external backend allocation 分账。
- 命令默认 deterministic；不允许因为当前时间、本机路径、字典顺序导致 report 逻辑字段或 bytes 改变。

artifact manifest 默认字段：

```text
artifact_id
format_version
manifest_format_version
runtime_format_version
cache_key_hash
descriptor_hash
schema_hash
layout_hash
codegen_hash
source_set_hash
source_set_descriptor_hash
layer_stack_hash optional
materialized_source_hash optional
export_view_id
export_view_hash
included_export_targets[]
preload_plan_hash optional
bytes_hash
identity_index_digest
key_index_digest
reference_index_digest
dependency_index_digest
preload_plan_digest optional
load_set_manifest_hash optional
segment_index_digest optional
source_layers[] optional
build_target
build_profile
operation_profile_id
operation_profile_hash
gate_policy_hash
convert_profile_hash
created_by_tool_version
converter_registry_hash
runtime_provider_registry_hash
unity_dependency_runtime_hash
unity_dependency_display_hash
localization_manifest_hash
expression_registry_hash
input_workbooks[]
input_schema_files[]
converted_bytes_path
report_path
diagnostic_summary
stripped_summary
unity_resource_dependencies[]
outputs[]
```

`input_workbooks[]` 默认字段：

```text
normalized_path
workbook_guid
workbook_alias
workbook_role
source_hash
metadata_checksum
schema_hash
layout_hash
row_count_by_table
exported_row_count_by_table
```

`source_layers[]` 默认字段：

```text
layer_id
layer_kind
source_kind
source_identity
source_hash
target_base_source_hash optional
target_layer_stack_hash optional
layer_order
priority_group
activation_result
activation_assignment_id optional
activation_inputs_hash optional
patch_operation_digest optional
trust_result optional
signature_digest optional
```

CI gate 默认策略：

- blocker 直接失败。
- error 默认失败。
- warning 默认不失败；项目可通过 `--warnings-as-errors` 或 CI policy 升级。
- `data_loss_risk=true` 的 migration/auto-fix 默认失败，除非显式 policy 允许。
- dirty metadata、未 flush row identity、duplicate workbook guid、duplicate row guid、hard reference missing、required runtime field missing 都阻止 convert。
- source layer order invalid、base mismatch、overlay conflict、Release forbidden overlay 和 required signature invalid 都阻止 convert/build。
- unresolved `migration_required`、ambiguous migration plan、unavailable migration host requirement、inconsistent migration history 都阻止 convert。
- runtime benchmark gate 默认使用 `--allocation-policy fail-on-any-hot-path-allocation`；`runtime.allocation_budget_exceeded` 默认失败，`performance.watermark_grew` 是否失败由 policy 决定。
- CI 必须记录 `operation_profile_hash` 和 `gate_policy_hash`；同一 report 在不同 gate policy 下重新判定时，必须输出新的 gate result report，不能改写原 report。
- report result_severity 必须和 exit code 一致。

Unity 2022.3 build integration 默认规则：

- Unity adapter 在 build 前运行 `workbook-check` + `convert` 等价流程。
- Unity adapter 必须在 `verify-bytes` 成功后生成 14.25.2 的 `RuntimeArtifactPackageDescriptor` 和 `RuntimeArtifactLocator`；构建脚本不能绕过 descriptor 直接按路径塞 bytes。
- Release build 默认只打包 `ConvertedBytesDataSource` artifact，不打包原始 Excel。
- Development build 可以选择打包 Excel debug source，但必须有显式开关和 report 标记。
- Release / Development 的 bytes 打包位置由 package provider 决定：StreamingAssets、Addressables、Resources 或 custom provider 都必须写入 package descriptor 和 startup report。
- Unity asset dependencies 来自 `UnityResourceRef` guid；main_asset_path 只用于 report 和 inspector 展示。
- Unity resource dependency manifest 默认记录 guid、main_asset_path、asset_type、runtime_provider、runtime_key、dependency_kind、referenced_by_assets、source_locations、runtime/display dependency hash。
- Release build 下 exported `UnityResourceRef` 必须能通过 addressables/resources/custom provider 之一得到 runtime_key；不能依赖 UnityEditor AssetDatabase 或 main_asset_path 兜底。
- 构建缓存命中必须按 14.25.1 的 `cache_key_hash` 和 bytes verify 流程校验；不能只靠文件时间戳或输出路径。
- Unity player build cache 还必须纳入 `package_descriptor_hash`；但 package 输出路径、provider key 或 Addressables group 变化不能污染 converted bytes 的 `source_hash`。

artifact 追踪默认规则：

- `bytes_hash` 是 converted bytes 文件内容 SHA-256。
- `source_set_hash` 来自所有输入 workbook 的 exported data 和 identity，使用 canonical bytes 的 SHA-256。
- `export_view_hash` 来自当前 profile 归一化后的 `ExportViewDescriptor`，client/server/editor/development/release 视图不能互相复用 bytes。
- artifact manifest 应随 bytes 一起提交或作为 CI artifact 保存。
- runtime artifact package descriptor 是 artifact manifest 之后的宿主打包描述；它引用 artifact_id / bytes_hash / manifest_hash，但默认不改写 artifact manifest。
- 正式 runtime startup report 至少包含 schema_hash、export_view_hash、source_set_hash、layer_stack_hash、materialized_source_hash、load_set_manifest_hash、segment_index_digest、bytes_hash、format_version、build_target。
- 如果 runtime 打开的 bytes 与 manifest 不匹配，open 失败并保留旧 source/unopened state。

#### 14.25.1 默认 artifact manifest / build cache 协议

artifact manifest 是 build/runtime 对 converted bytes 的外部索引，不是 report 的装饰文本。CI、Unity build、runtime startup 和 cache lookup 都以 manifest 的 machine-readable 字段为准。

manifest 物理格式默认规则：

- 默认使用 canonical JSON，UTF-8 without BOM，LF 换行。
- JSON object 字段按字典序输出；array 中 record 按稳定 key 排序。
- hash 使用 lowercase SHA-256 hex。
- 不写当前时间、用户名、本机绝对路径、CI workspace path、Unity Library path。
- `created_by_tool_version` 是工具版本事实，不参与 `source_set_hash`；是否参与 cache key 由 cache key 输入规则决定。
- `converted_bytes_path`、`report_path` 使用相对 manifest 所在目录的 normalized path；这些 path 不参与 `bytes_hash`。

`artifact_id` 默认规则：

```text
artifact_id = sha256("exceldb-artifact-v1" + cache_key_hash + bytes_hash)
```

`cache_key_hash` 默认输入：

```text
descriptor_hash
schema_hash
layout_hash
codegen_hash
source_set_hash
layer_stack_hash
materialized_source_hash
export_view_hash
build_target
build_profile
convert_profile_hash
runtime_format_version
created_by_tool_version
converter_registry_hash
runtime_provider_registry_hash
unity_dependency_runtime_hash
localization_manifest_hash
expression_registry_hash
preload_plan_hash optional
load_set_manifest_hash optional
```

cache key 默认不包含：

- workbook 本机绝对路径。
- Excel 样式、列宽、筛选、冻结窗格、普通批注、diagnostic helper sheet。
- human-readable report。
- `gate_policy_hash`、warning-as-error、report detail、data loss confirmation UI policy 等只影响 gate/report、不影响 bytes 内容的字段。
- main_asset_path 的展示变化；该变化只影响 `unity_dependency_display_hash` 和 manifest/report。
- 输出文件路径。

`convert_profile_hash` 默认覆盖：

```text
strip editor-only/runtime-only policy
string table policy
debug symbol policy
compression/alignment policy
platform endian/alignment requirement
runtime provider selection policy
warning-as-error policy that affects output
```

导出目标选择本身由 `ExportViewDescriptor` 归一化，并通过 `export_view_hash` 单独进入 manifest/cache key；`convert_profile_hash` 只覆盖“如何写出当前 export view”的转换策略。

`converter_registry_hash` 默认覆盖：

- built-in converter version。
- custom codec / normalizer / validator / migration / runtime provider descriptor id/version。
- reference family converter id/version。
- converted bytes writer format version。
- compression or packing plugin id/version。

Unity dependency hash 默认规则：

- `unity_dependency_runtime_hash` 只覆盖会影响 runtime 解析或包内容的字段：guid、asset type、runtime provider id/version、runtime key、dependency kind、asset content hash 或 provider dependency hash。
- `unity_dependency_display_hash` 覆盖 main_asset_path、source_locations、referrer display path 等给人看的字段。
- Release bytes cache key 使用 `unity_dependency_runtime_hash`；单纯 main_asset_path 变化不要求 bytes miss。
- Unity build manifest 仍记录 display hash，方便 inspector/report 更新路径。

Localization dependency hash 默认规则：

- `localization_manifest_hash` 覆盖 exported locale set、fallback graph、text entry identity、text key、required locale canonical text、token descriptor 和 localization runtime provider id/version。
- translator comment、preview_text、Excel 样式和非 exported optional locale 不进入 `localization_manifest_hash`，除非当前 export view 声明需要它们。
- Release bytes cache key 使用 `localization_manifest_hash`；只改 preview/comment 不要求 bytes miss。

Expression registry hash 默认规则：

- `expression_registry_hash` 覆盖 expression grammar id/version、function id/version/signature、symbol table id/version、runtime representation 和 bytecode writer version。
- 表达式文本的具体内容已经进入当前 export view 的 `source_hash`；registry hash 只覆盖解释这些文本所需的语义环境。
- Release bytes cache key 使用 `expression_registry_hash`；只改 expression editor UI/formatting helper 不要求 bytes miss。

Preload plan hash 默认规则：

- `preload_plan_hash` 覆盖输出到 bytes 的 PreloadPlanDescriptor、root selector、dependency kind filter、target family filter、recursive/depth/cycle policy 和输出算法版本。
- 没有输出 `preload_plan_index` 时 `preload_plan_hash` 为空固定值，不影响 cache key。
- 只改 preload plan display name、Excel 注释或 debug report 文本，不改变 `preload_plan_hash`。

Load set / segment hash 默认规则：

- `load_set_manifest_hash` 覆盖 LoadSetDescriptor、root selector、residency/object materialization/unload policy、segment membership 和 load set writer version。
- `segment_index_digest` 覆盖 segment id/kind/load_set_id/source hash/offset/length/checksum/compression。
- 不启用 segmented bytes / load set 时两者为空固定值；启用后进入 cache key 和 runtime startup report。

cache hit 默认流程：

1. 从当前 schema、workbook、profile、Unity dependency manifest 和 converter registry 计算 candidate `cache_key_hash`。
2. 在 cache 中查找同 `cache_key_hash` 的 manifest。
3. 重新计算 cached bytes 的 `bytes_hash`，并验证 manifest 中的 `artifact_id`。
4. 执行 `verify-bytes` 等价的轻量校验：header、schema_manifest、export_view_hash、source_set_hash、layer_stack_hash、materialized_source_hash、section/segment directory、identity/key/reference/dependency/load_set index；存在 `preload_plan_index` 时一并校验 preload_plan_hash/digest。
5. 校验通过后才复用 bytes；失败时丢弃 cache entry，重新 convert，并报告 `bytes.manifest_mismatch` 或 `bytes.checksum_mismatch`。

cache miss / invalidation 默认规则：

- schema/source/layer stack/materialized source/profile/tool/runtime provider/converter registry 任一 cache key 输入变化，cache miss。
- cache 默认只保存 successful convert artifact；error/blocker report 不作为可复用 successful artifact。
- warning artifact 可以缓存，但 cache hit 时仍必须保留原 warning summary，并受当前 CI policy 重新判断。
- cache entry 不允许跨 build_target / build_profile / export_view_hash 复用。
- Development build 含 debug symbols / Excel debug source 时必须使用不同 `convert_profile_hash`，不能复用 Release bytes。
- 如果项目选择压缩 bytes，压缩参数进入 `convert_profile_hash`；解压后的 logical content hash 可以单独记录，但 runtime open 默认校验压缩文件的 `bytes_hash`。

runtime manifest verify 默认规则：

- Release runtime 如果随包携带 manifest，open 时校验 manifest 与 bytes header 的 schema_hash、export_view_hash、source_set_hash、bytes_hash、format_version、build_target。
- manifest 缺失时可以只按 bytes header open，但 startup report 必须标记 `manifest_missing=true`；项目可在 Release policy 中禁止。
- manifest 和 bytes 不一致时 open 失败，不尝试用 manifest 修正 bytes。
- runtime 不信任 report 文本；只读 manifest 和 bytes header/index。

#### 14.25.2 默认 runtime artifact package / provider locator 协议

converted bytes artifact 解决“数据是什么”；runtime artifact package 解决“正式包或开发包如何找到、验证和持有这份数据”。这两层不能混成一层：同一份 bytes 可以被放进 StreamingAssets、Addressables、Resources、自定义包或测试内存 provider，但只要 bytes 和 artifact manifest 不变，数据语义 hash 不应改变。

默认产物链路：

```text
Excel workbook / source set
  -> workbook-check / verify-source-set
  -> converted bytes + artifact manifest
  -> RuntimeArtifactPackageDescriptor
  -> RuntimeArtifactLocator
  -> RuntimeArtifactProvider immutable lease
  -> ConvertedBytesDataSource open/switch
```

`RuntimeArtifactPackageDescriptor` 默认字段：

```text
package_id
package_format_version
package_descriptor_hash
artifact_id
artifact_manifest_path
artifact_manifest_hash
provider_kind: streaming_assets | addressables | resources | custom
provider_id
provider_version
provider_key
build_target
build_profile
operation_profile_id
operation_profile_hash
convert_profile_hash
export_view_id
export_view_hash
source_set_hash
source_set_descriptor_hash
layer_stack_hash optional
materialized_source_hash optional
bytes_hash
load_set_manifest_hash optional
segment_index_digest optional
runtime_provider_registry_hash
unity_dependency_runtime_hash
include_excel_debug_source
debug_excel_source_entries[] optional
bytes_entries[]
segment_entries[] optional
trust_result optional
signature_digest optional
outputs[]
```

`package_descriptor_hash` 对 descriptor 中除自身外的 canonical bytes 计算；它用于 Unity player build cache、startup report 和 locator 校验，不进入 converted bytes 的 `bytes_hash`、`source_hash` 或 14.25.1 的 converted bytes `cache_key_hash`。只有当 package provider selection 改变了 converted bytes 内容、UnityResourceRef runtime key、strip/debug symbol、compression/alignment 等输出语义时，才通过 `convert_profile_hash`、`runtime_provider_registry_hash` 或 `unity_dependency_runtime_hash` 影响 converted bytes cache key。

`bytes_entries[]` 默认字段：

```text
entry_id
entry_kind: full_source | patch_layer | segment | manifest
artifact_kind
logical_artifact_path
provider_key
bytes_hash
segment_id optional
load_set_id optional
compression optional
required_for_open
```

`RuntimeArtifactLocator` 默认语义：

- locator 是 player startup 的紧凑入口，只保存 artifact_id、provider_id、provider_kind、provider_key、manifest_hash 和 bytes_hash 这类低频定位字段；完整校验仍以 package descriptor、artifact manifest 和 bytes header/index 为准。
- locator 不能只是一条文件路径；路径、addressable key、resources path 或 custom key 都必须先归一化成 provider key。
- locator 中的 display path 只允许进入 report/debug UI；运行时 open 以 provider id/key、artifact_id、manifest_hash、bytes_hash 和 bytes header 校验为准。
- locator 可以由 generated bootstrap asset、StreamingAssets manifest、Addressables label、Resources asset 或宿主自定义启动参数提供；无论来源如何，最终都要生成同一 `RuntimeArtifactLocator`。

config artifact provider 与 UnityResourceRef runtime provider 的边界：

- config artifact provider 只负责加载 ExcelDB 的 bytes / manifest / segment，服务 `ConvertedBytesDataSource`。
- UnityResourceRef runtime provider 负责把表内的 Unity guid/sub asset identity 解析成运行时资源 key，服务 gameplay 资源加载。
- 两者都叫 provider，但 hash 输入、生命周期和错误边界不同；不能用 config bytes 的 StreamingAssets 路径去兜底加载 `UnityResourceRef`，也不能用 UnityResourceRef 的 main_asset_path 去定位 config bytes。
- Release 中任何 `UnityResourceRef` 若没有明确 runtime provider/key，仍按 `unity_ref.runtime_provider_missing` 或 `build.unity_dependency_missing_runtime_key` 阻止 build/convert。

内置 package provider 默认规则：

- `streaming_assets`：bytes、manifest、segment 按 package descriptor 的 canonical relative path 写入 StreamingAssets；provider key 是相对路径或 manifest entry id，不扫描目录、不按文件名猜最新版本。
- `addressables`：bytes/manifest/segment 作为 Addressables entry 或等价 binary asset 打包；provider key 是 addressable key 或由 descriptor 记录的稳定 key。若 Addressables 需要异步加载，Unity adapter 必须先完成显式 preload operation，`ConvertedBytesDataSource.Open` 只消费已取得的 immutable lease。
- `resources`：bytes/manifest/segment 位于 Resources 路径下；provider key 是 generated Resources load path。Resources 允许作为小项目/工具默认方案，但仍必须写入 package descriptor，不能在 runtime 全量扫描 Resources。
- `custom`：项目 provider 必须声明 provider id/version、host requirement、key format、lease lifetime、deterministic flag、hash inputs 和 no-GC boundary；未声明或不可用时按 `extension.host_requirement_unavailable` / `command.profile_invalid` 阻止 build 或 open。

Unity build 默认流程：

1. 归一化 `OperationProfile`、`ExportViewDescriptor`、convert profile 和 runtime artifact package profile。
2. Unity adapter 收集 `UnityResourceRef` dependency manifest，计算 `unity_dependency_runtime_hash` 和 `unity_dependency_display_hash`。
3. 运行 `workbook-check` / `verify-source-set`，再 `convert` 输出 bytes + artifact manifest。
4. 对输出执行 `verify-bytes`；cache hit 也必须走 14.25.1 的 manifest/bytes/index 轻量校验。
5. 根据 selected package provider 生成 `RuntimeArtifactPackageDescriptor`、locator bootstrap 和 declared outputs。
6. 验证 provider key 在当前 Unity build 内容中可解析到 bytes/manifest/segments，并验证 artifact manifest 与 package descriptor 一致。
7. Release build 检查 `include_excel_debug_source=false`，并确认原始 `.xlsx` 不在 player declared outputs；否则产生 `build.release_excel_source_forbidden`。
8. Development build 只有在 profile 显式启用时才允许打包 Excel debug source；该 source 是可选择的 debug source entry，不是 converted bytes open 失败后的隐式 fallback。

Release / Development 默认运行策略：

- Release startup 只允许从 package locator 打开 `ConvertedBytesDataSource` 或可信 signed `CompositeDataSource`；传入 `ExcelDataSource`、Excel debug source entry 或 development override layer 按 mode/profile 失败。
- Development 可以在显式 opt-in 后同时携带 bytes 和 Excel debug source；当前 source 由 startup profile 或显式 `SwitchDataSource` 决定，不因为 bytes verify 失败自动改读 Excel。
- Excel source 与 package bytes 热切换仍执行 14.14.3 的 SourceEquivalence preflight；export_view_hash、source_set_hash、materialized_source_hash、runtime dependency digest 不一致时不能宣称等价。
- 同一 bytes 被不同 package provider 打包时，如果 artifact_id、bytes_hash、manifest_hash、source/runtime digest 全部一致，只能发布 source_summary/package_summary，不发布 property_changed。
- provider key、package 输出路径或 Addressables group 变化不改变 workbook/source 语义；它影响 package_descriptor_hash 和 Unity player build cache，不影响 `source_hash`。
- `RuntimeArtifactProvider` 只能在 open/switch/refresh/load-set operation 中绑定 immutable lease；稳定读取、getter、reference resolve、dependency traversal 和 ChangeSet dispatch 不得回调 provider 或产生 GC allocation。

运行时打开示例：

```csharp
var locator = ExcelDbUnity.RuntimeArtifacts.LoadArtifactLocator("config_release");

RuntimeDatabase.Open(new ConvertedBytesDataSource(locator));
RuntimeDatabase.Reserve(RuntimeCapacity.FromDescriptor("ConfigData/runtime_capacity.json"));
RuntimeDatabase.Prewarm();

var skill = RuntimeDatabase.LoadAsset<SkillConfig>("skill/fireball");
```

`ExcelDbUnity` 是 Unity adapter namespace，`RuntimeArtifacts` 是 ExcelDB-native helper surface；不要新增前缀式 facade。

Development 热切换示例：

```csharp
var releaseLocator = ExcelDbUnity.RuntimeArtifacts.LoadArtifactLocator("config_development_bytes");
RuntimeDatabase.Open(new ConvertedBytesDataSource(releaseLocator));

RuntimeDatabase.SwitchDataSource(new ExcelDataSource("Design/Config/Skill.xlsx"));
RuntimeDatabase.Refresh();

var rebuiltLocator = ExcelDbUnity.RuntimeArtifacts.LoadArtifactLocator("config_development_bytes_rebuilt");
RuntimeDatabase.SwitchDataSource(new ConvertedBytesDataSource(rebuiltLocator));
```

上面两个 locator 对应的 package 可以不同，但切回 bytes 时必须通过 artifact manifest、bytes header/index 和 SourceEquivalence 证明 export view、source set、identity/key/reference/dependency/preload digest 与当前运行时兼容；不能只因为路径或 provider key 相似就接受。

### 14.26 默认决策协议

本节是实现者和后续 AI 的默认决策协议。它不是低优先级建议，也不是留给实现阶段自由发挥的空白；当本文没有覆盖某个细节时，必须先按本节归类，再选择默认行为。只有项目 policy / operation profile 显式允许，且不违反 14.1 hard priority，才可以偏离。

默认决策流程：

1. 先判断是否影响 import/export/runtime 语义、identity、reference、dependency、schema/layout/codegen hash、converted bytes、SaveAssets 写回、hot reload publish 或 no-GC contract。
2. 如果影响这些语义且本文没有给出兼容规则，默认选择 fail/read-only/no-op + machine-readable report，不猜测、不自动修复、不写 workbook。
3. 如果只影响 editor projection、human-readable 文本、Excel 批注、UI 展示或 debug view，默认可以 projection-only 处理；projection 不能进入 source hash、bytes hash、report hash 或 core no-GC 证明。
4. 如果存在多个看似合理方案，按 14.1 优先级排序；仍不能唯一决定时，选择对数据最保守、对 identity 最稳定、对 runtime 最 deterministic 的方案，并把被放弃的选择写入 report。
5. 如果某实现需要临时降级能力，必须通过 operation profile / backend capability / report 暴露；不能把降级藏在 API 成功返回值里。

默认保守选择：

- 能保留用户 Excel 内容，就不重建整表；无法保真 patch 时失败，不整包重写。
- workbook 与派生 snapshot/diff/review artifact 不一致时，以 workbook 为准，并报告 `vcs.snapshot_stale`；派生 artifact 只能重建，不能反向覆盖 workbook。
- 多个 source/layer 参与运行时读取时，先构建 materialized view，再提供 object/index/dependency API；业务读取不得看到半层叠状态。
- 能用 metadata identity 匹配，就不用 header 文本、行号、路径、中文名或相似字符串猜。
- 能用 schema/descriptor 生成，就不要求策划手填结构信息；Excel 表头展示永远是 schema 投影，不是第二份事实源。
- 能事务提交，就不做半更新；所有跨 workbook/source 的 side effect 必须进入 OperationPlan affected set 和 side-effect ledger。
- 能 patch object，就不重建 object；不能 patch 时必须发布 `recreated`，不能拆成普通 `removed + added`。
- 能复用 buffer，就不在热路径分配；如果容量不足，先登记水位线增长或按 allocation policy 失败。
- 能放在 adapter/extension，就不污染核心；核心只接收 reference family、provider、validator、drawer、source backend 的稳定 descriptor。
- 能生成 machine-readable report，就不只抛异常字符串；常规数据错误必须有 code、stable ids、source location 和 severity。
- 能 deterministic 输出，就不依赖当前机器、当前时间、字典遍历顺序、文件系统枚举顺序、Excel 打开状态或 Unity 当前 selection。

新增 API surface 默认：

- Unity 2022.3 有同构 API 时，Unity adapter facade 默认使用相同类名、方法名、属性名和返回值语义，仅 namespace 不同；差异必须在 ExcelDB 语义段落和 report 中说明。
- Unity 没有同构概念时，使用 ExcelDB-native 命名，并保持同一风格：`RuntimeDatabase`、`ExcelDataSource`、`ConvertedBytesDataSource`、`RuntimeSnapshot`、`OperationReport`。
- static facade 只是 current/default context 的投影；任何新增 static API 都必须有等价 context instance API，除非该 API 本身就是 context 绑定入口。
- 返回数组/string 的 Unity-like projection API 可以分配，但必须有 no-GC core query 或 Span/range/view 入口承载热路径。

新增错误处理默认：

- 会导致数据丢失、身份重建、引用重绑定、runtime 行为变化、hash 不一致或 SaveAssets 误清 dirty 的情况，默认 blocker / operation failure。
- 用户可见但不影响 import/export/runtime 的 layout/comment/projection 变化，默认 warning/info，并保留用户内容。
- 未知 workbook/xlsx package feature 被写回触碰时，默认 blocker；只读 import 可以降级为 warning，但必须保持 source 可诊断。
- 常规内容错误不用异常表达；异常只代表编程错误、extension failure 或 internal unexpected，并转成稳定 diagnostic 摘要。
- callback、watcher、drawer、processor、subscriber 中发生重入写操作时，默认拒绝或排队到 owner safe point；不能同步递归改 object graph 或 workbook。

新增数据写回默认：

- 正式数据行、metadata、schema-owned structure、helper/freeform、diagnostic projection 必须先归属到 owner region；owner 不明确时不写。
- 清理、删除、批量默认值物化、列移动、表迁移、identity repair 都必须先 dry-run，再 apply；没有 dry-run plan hash / source revision / confirmation 时不执行。
- 如果 patch target 与用户内容重叠，默认 blocker；不能把用户内容搬到别处后继续写，除非 schema/project policy 声明了可验证迁移。
- Save 成功的判定必须包含 temp/verify/replace/reimport；不能只因为 xlsx writer 成功或内存 candidate 成功就清 dirty。

新增 runtime 默认：

- Release runtime 默认只读，只接受 trusted converted bytes / runtime artifact package；Excel source、writeback、UnityEditor AssetDatabase、dynamic schema migration 默认禁止。
- Development/Play Mode 如果启用 Excel source/hot reload，也必须遵守 source switch transaction、publish point、ChangeSet、allocation policy 和 old source preservation。
- source switch / hot reload 失败时保留旧 source、旧 object graph 和旧 dependency index；失败 candidate 只进入 report，不对业务半发布。
- runtime lookup 默认不触发 IO、import、provider discovery、migration、materialize defaults 或 payload allocation；需要这些动作时必须是显式 operation。

新增 schema / hash 默认：

- 新 descriptor 字段默认先判断 hash 影响域：影响解释和 runtime 行为进 `schema_hash`，只影响 Excel authoring 展示进 `layout_hash`，影响 generated public API 进 `codegen_hash`。
- 不能判断 hash 影响域时，默认进入更严格的 hash，直到有明确兼容证明；不能为了缓存命中而少算。
- 新 ID 一旦可能进入 workbook/bytes/report/manifest，就必须有稳定 scope、reserved/deprecated 规则和 compatibility test；不能使用显示名、文件顺序或运行时 hash 作为身份。
- 新 value shape 默认要提供 canonical bytes、Excel physical layout、SerializedProperty path、runtime accessor、merge granularity 和 convert representation；缺任一项则不能作为通用完整目标版 shape。

新增性能默认：

- 新 runtime API 默认不是 hot-path-safe，除非提供 no-GC surface、capacity dimension、benchmark 和 allocation_summary 证明。
- 新 editor core operation 默认进入 allocation measurement；如果实现依赖分配式后端，必须声明 external backend allocation，不能冒充 core no-GC。
- 新 report 字段默认是 machine-readable stable payload；human-readable message、localized text、stack trace、debug preview 必须是 projection。
- 新 extension 的分配、IO、反射、外部进程或 Unity project 访问必须声明 phase 和 permission；未声明时 schema-lint / profile gate 阻止使用。

## 15. 完整目标版实现任务与验收

本章是完整目标版的完整实现任务清单。它不是“先定义、以后再做”的分层表；完整目标版交付时，本文默认能力必须完成 editor/import/export/runtime/report/CI/no-GC 验收。内部可以按里程碑施工，但不能用里程碑裁剪对外效果。

```text
完整目标版必须实现：schema contract、workbook metadata、structure generation、canonical value、enum/simple struct、child table、map、union、schema-discriminated polymorphic payload、internal reference、UnityResourceRef、LocalizedTextRef、expression、weighted selection、curve、label、preset、operation/report、compatibility、converted bytes、RuntimeDatabase、AssetDatabase、SerializedObject、Excel/source hot reload、source switch、CI/build/no-GC gate。
项目 profile 可以禁用某些能力，但禁用只能改变当前项目的可用性和 report 结果，不能代表完整目标版没实现。
CompleteConformance 必须启用全部默认能力并进入 CI / benchmark 验收；任何能力只能在项目 profile 中被策略性禁用，不能在 conformance profile 中缺席。
CompleteConformance 分为 CoreConformance、UnityEditorConformance、DevelopmentRuntimeConformance 和 ReleaseRuntimeConformance；任一 conformance profile 缺席或未跑通过，都不能宣称完整目标版完成。
```

1. 扩展 schema options 设计：
   - table display/sheet/version/export。
   - table key descriptor：key field ids、key scope、normalizer id/version、compare policy、path pattern、path segment fields、display name field、alias keys、create_from_path、duplicate/rename policy。
   - field display/type shape/aliases/default/required/export/layout/description/header comment。
   - field parse policy：blank string policy、formula policy、formula dependency/evaluator descriptor、date/time representation、numeric descriptor、numeric alias、string normalizer id/version。
   - canonical value format：stable type tag、canonical_value_bytes、missing/null/default/explicit state、numeric unscaled/scale or IEEE bit pattern、source_cell_hash、raw_cell_fingerprint。
   - authoring value state writeback policy：missing/default/explicit_null/explicit_value 的 physical token、reserved token escaping、clear operation、default materialization operation。
   - enum schema：value id/name/display/aliases/deprecated/description/export token。
   - simple struct schema：subfields、single cell format、expanded columns layout。
   - array edit policy：append/delete/reorder、reorder_semantics、default drawer reorder control、whole-array rewrite conflict rule。
   - single-cell codec：codec id/version、canonical writer、parser、merge granularity、examples、diagnostic code。
   - child table schema：parent table/field、child table id、element identity、order policy、embedded/sub_asset。
   - advanced value shape policy：map descriptor、union/variant descriptor、polymorphic child table、JSON legacy/custom codec opt-in、migration requirement。
   - descriptor identity policy：simple struct / advanced value shape / map / union / expression / weighted / curve / label / preset 使用 package-qualified stable string id + 独立 semantic version；内部 subfield / variant 继续使用 owner-scoped numeric id。
   - capability status：区分完整目标版默认能力、项目 profile 禁用能力、host/extension unavailable；不得再使用“只定义 descriptor、不实现”或“后续再做”作为完整目标版交付状态。
   - reference descriptor：family、target table/group/runtime type、target scope、nullable、strength、delete policy、ownership、dependency kind、list duplicate、display token format、resolver/rebind policy。
   - Unity adapter reference family：UnityResourceRef guid/main_asset_path/asset_type/path_display policy、runtime provider、dependency kind、allow_sub_asset policy。
   - localization descriptor：LocalizedTextRef、text table、locale set、fallback graph、token policy、runtime provider、runtime format API、localization manifest hash。
   - expression descriptor：Expression<T> grammar、symbol table、function registry、budget、runtime bytecode/AST representation、editor/header comment、convert/runtime/no-GC gate。
   - weighted selection descriptor：entry child table、selection mode、weight/probability field、condition field、result payload、runtime algorithm、RNG contract、runtime no-GC selection。
   - curve descriptor：point child table、x/y/tangent/interpolation fields、monotonic/domain/extrapolation/bake policy、Unity AnimationCurve projection boundary、runtime no-GC sample API。
   - label descriptor：asset_labels/search_labels role、source field、token normalizer、display policy、mutable/write group、allow_new_string_labels、FindAssets/GetLabels/SetLabels/ClearLabels binding。
   - game table archetype / preset descriptor：config/lookup/localization/drop/curve/child/editor helper table，以及 key/display/category/tags/enabled/review/export/resource/expression/weighted/curve presets 的 canonical expansion。
   - dependency graph / preload plan descriptor：edge target family、dependency kind、root selector、recursive/depth/cycle policy、preload_plan_hash。
   - runtime artifact package descriptor：package provider、provider key、artifact manifest hash、bytes/segment entries、package_descriptor_hash、Release/Development Excel debug source policy。
   - validator severity/mode。
   - schema source set descriptor：source_set_format_version、schema_package_id、source kind、logical path、content hash、import roots、dependency lock、compiler options、extension option registry hash、generation targets。
   - canonical `SchemaDescriptor`：table/field/enum/simple struct/reference/validator/editor capability descriptors。
   - migration descriptor：stable id/version、from/to schema hash、dependency/conflict、affected ids、data_loss_risk、host requirement、dry-run/apply/verify entry。
   - generated C# type binding：table id -> runtime type，field id -> C# member name / SerializedProperty path。
   - generated binding manifest：descriptor_hash、schema_hash、layout_hash、codegen_hash、runtime type bindings、field/property path bindings、runtime accessor bindings。
   - runtime accessor binding manifest：field storage kind、offset/slot、string/range/reference/default/null state slot、getter shape、hot_path_safe。
   - generated registry bootstrap：注册 descriptor、runtime type、table id、field id、reference family、validator、drawer hint。
   - schema hash / layout hash / codegen hash 输入字段边界。
   - canonical descriptor bytes：descriptor format version、canonical writer id/version、字段默认值 materialize、descriptor list deterministic sort、map 转 sorted entries、禁止 source path/time/local text 进入 hash。
   - descriptor source map：descriptor path -> logical source path/line/column/option path；只用于诊断和 IDE，不进入 descriptor/schema/layout/codegen hash。
   - previous descriptor snapshot：workbook metadata 保存 last successful descriptor/schema/layout/codegen hash 和兼容分析所需 minimal snapshot；缺失时只允许 degraded analyze。
   - generated binding manifest 与当前 generated assembly 的 codegen_hash 不一致时产生 `schema.codegen_hash_mismatch`，不能用旧 accessor 解释 workbook/bytes。
   - project policy descriptor / operation profile：policy format/version、default_profile_by_operation、named profiles、host/build overlays、override allowlist、hard invariants、profile id/version、operation kind、mode、allowed source kinds、warning/suppression policy、data loss confirmation、auto-fix、structure/save、manifest、allocation、report detail、extension permission、convert/build profile、operation/gate/convert hash 分组。
   - source layer descriptor / policy：layer kind、order、activation、target base hash、patch operation granularity、trust/signature、materialized_source_hash。
   - profile hash 边界：operation_profile_hash、gate_policy_hash、convert_profile_hash 各自覆盖范围和 cache key 影响。
   - id namespace / 分配 / 保留规则：table/field/enum/child/variant numeric id scope，validator/codec/migration 等 package-qualified descriptor id + version，numeric id tombstone 与 descriptor id/version tombstone 分账；published id 删除后进入 reserved/deprecated，不得复用。
   - guid namespace / 命名规则：`WorkbookGuid`、`RowGuid`、ExcelDB `AssetGuid`、Unity `.meta` `UnityGuid` 分账；Unity-like `GUID` wrapper 默认只承载 ExcelDB `AssetGuid`，UnityResourceRef 明确承载 `UnityGuid`。
   - schema id allocation registry：id_ranges、active/reserved/deprecated set、allocation request stable sort key、allocate-id precondition hash、allocation manifest、stale proposal 拒绝规则。
   - extension package descriptor：package id/version/namespace、host requirements、extension descriptors、dependencies/conflicts、deterministic、side effect policy、permissions、hash impact。
   - extension permission descriptor：filesystem logical roots、network access、environment variables、Unity project access、external tools、declared inputs/outputs、timeout、side effect policy。
   - extension descriptor：kind/id/version/entry point/host requirement/phase/ordering key/affected ids/diagnostic codes/dependencies/conflicts/capabilities。
   - editor capability descriptor：editor id/version、target table、required/optional fields、schema/layout range、fallback mode、host requirement。
   - layout capability descriptor：column_order_policy、requires_physical_column_order、safe_move requirement、cleanup capability、backend preservation capability。
   - drawer registry descriptor：drawer id、value shape、reference family、host requirement、required custom drawer。
2. 定义 workbook metadata sheet 格式：
   - 使用 `__ExcelDB_Metadata`。
   - metadata sheet header：`EXCELDB_METADATA`、format_version、metadata_schema_version、metadata_checksum。
   - 使用固定 block 顺序的 plain ranges：workbook、tables、fields、rows、migrations、generated_artifacts、system_columns。
   - 所有 metadata record 使用 canonical text，不依赖 Excel number/date formatting。
   - metadata checksum 覆盖 canonical metadata rows，写回后必须复读校验。
   - workbook record：workbook guid、descriptor hash、schema hash、layout hash、generator version、metadata/source revision、last successful import revision。
   - workbook identity allocator：next row local id、identity allocator revision。
   - table record：table id、schema name、current sheet name、table version、data start row、table kind/export policy。
   - field record：table id、field id、field path、column index/span、value shape、codec id、parent field id、deprecated/reserved。
   - row record：table id、row guid/local id、current row number、key snapshot、row revision、source hash/cell range hash、identity state。
   - migration history record：migration id/version、descriptor hash、from/to schema/layout hash、operation id、affected ids、data_loss_risk、result、report hash。
   - generated artifact record：artifact id/kind、owner table/field、sheet/range/name、descriptor/layout hash、artifact checksum、protected/repair policy。
   - system_columns record：table id、sheet name、column kind/index、hidden/protected、purpose。
   - table sheet 生成 hidden companion columns：`__xdb_row_guid`、`__xdb_row_local_id`、`__xdb_row_revision`、`__xdb_identity_state`。
   - metadata sheet 是 identity ledger；table sheet hidden companion cells 是 row-local identity anchor。import 先读取 companion anchor 定位现场行，再与 metadata row record reconcile；metadata current row number 只用于加速和诊断。
   - 策划排序/复制/剪切行后，identity 仍可通过 companion columns 和 row record 修复或报 blocker。
   - metadata format version 过新时默认 read-only open，写回/convert 需要显式 metadata migration。
3. 定义 schema 生成 Excel 结构行的格式：
   - 中文名。
   - 字段名。
   - 类型。
   - 导出端。
   - 规则。
   - 说明。
   - 表头注释。
   - 枚举可选值展示。
   - 简单结构体单 cell / 多列展开规则。
   - data region 默认从第 8 行开始。
   - importer 不依赖 merged cells，merged cells 只作为显示样式。
   - 哪些行只读/受保护，哪些行允许业务备注。
   - 哪些辅助行/列不参与导入导出但必须保留。
   - structure generation 先按 table id / field id 匹配，再按 field path / alias repair；禁止按中文名、列位置或相似字符串猜身份。
   - schema-owned 第 1-7 行可被生成器覆盖；正式数据区第 8 行以后不因 layout refresh 被清空或批量默认填充。
   - Excel data validation dropdown 和 enum/helper sheet 由 schema 生成；只更新 schema-owned artifact。
   - generated helper sheet 物理格式：`EXCELDB_HELPER` marker、format version、descriptor/layout hash、artifact checksum、enum/reference/codec/comment/range blocks。
   - generated named range 由 stable logical id 驱动，metadata 记录 logical id、Excel name、target range、owner field id、checksum。
   - 表头注释以 row 7 为最小事实源，Excel note/comment 只是带 marker/checksum 的 richer presentation。
   - reference options helper range 只是 authoring suggestion；引用身份仍来自 hidden/metadata identity、picker result 或 schema resolver，不从 visible dropdown token 反推。
   - reference options 候选来自 source set identity/key/reference index 与 reference descriptor target scope；large/dynamic/cross-workbook reference 默认写查询 descriptor 和 picker/search 入口，不强行生成完整 dropdown。
   - helper sheet、named range、data validation 的不一致要能区分可安全重生、需 warning、会覆盖用户内容的 blocker。
   - 默认 column order policy 为 `preserve_existing_known_columns`；新增字段找不到稳定插入点时追加到 system companion columns 前。
   - `enforce_column_order=true` 时只能移动完整 field column group，且必须保留 data/style/formula/comment/data validation。
   - `requires_physical_column_order=true` 只能由 editor capability / export adapter descriptor 显式声明；无法保真移动时 layout refresh 失败，不覆盖 helper/freeform。
   - unknown/helper/freeform column 默认保留；与必要 schema patch 重叠时 blocker，不静默覆盖。
   - `CleanupDeprecatedLayout` 是显式操作；`GenerateStructure`、`LayoutRefresh`、`SaveAssets` 不自动删除 empty/deprecated/unknown columns，只输出 cleanup candidates。
4. 做 schema compatibility 测试：
   - rename sheet。
   - rename field。
   - missing optional column。
   - missing required column。
   - unknown column preservation。
   - regenerate structure 不清空正式数据行。
   - regenerate structure 保留不影响导入导出的策划辅助信息。
   - regenerate structure 不依赖 header 文本，field id 匹配成功时可以修正表头显示。
   - field id / table id 多重匹配产生 `structure.field_mapping_ambiguous` / `structure.table_mapping_ambiguous` blocker。
   - 手工调整列顺序后默认保留既有列顺序，新字段追加并报告 `layout.column_appended_due_to_manual_order`。
   - schema enforce column order 时，完整列组移动必须保留数据、样式、公式、批注和 data validation；无法保真移动时报告 `layout.column_move_skipped`。
   - `enforce_column_order=true` 但无 `requires_physical_column_order` 时，无法保真移动只跳过移动并保持 field id mapping；operation profile 升级 severity 不能把 patch 改成覆盖 helper/freeform。
   - `requires_physical_column_order=true` 且无法保真移动时，layout refresh 失败并不写 workbook。
   - helper/freeform 区域与新增 schema 字段插入位置冲突时，不覆盖 helper，产生 `structure.helper_region_overlap`。
   - 删除 reserved/deprecated 字段默认保留旧列为 ignored/deprecated，不导出。
   - 删除未 reserved 且含数据的字段需要 migration 或 data-loss confirmation，generation 不直接删除列。
   - 删除字段且旧列全空时，GenerateStructure/LayoutRefresh 只报告 cleanup candidate，不删除物理列。
   - CleanupDeprecatedLayout dry-run 输出 ownership proof、emptiness proof、metadata reference proof、cleanup_candidates 和 data_loss_risk；apply 无有效 dry-run report hash / confirmation hash 时拒绝。
   - CleanupDeprecatedLayout apply source revision、schema/layout hash 或 package fingerprint 变化时产生 `transaction.plan_stale` / `transaction.source_revision_changed`，不写 workbook。
   - CleanupDeprecatedLayout 不删除 unknown/helper/freeform、含公式/comment/style/validation 的用户区域或正式数据行；成功后 source_hash、bytes data hash 和 runtime identity 不变。
   - blocker 级结构错误不写回 workbook。
   - generation report 区分 safe/warning/error/blocker。
   - generation report 包含 column_mapping_before/after、preserved_helper_ranges、updated_header_cells、updated_data_validations、updated_helper_artifacts、updated_named_ranges、skipped_column_moves、cleanup_candidates、cleanup_actions。
   - enum 字段生成表头注释和 dropdown，重新生成后仍由 schema 控制。
   - reference 字段可生成 helper/dropdown 时，visible token 与 hidden identity 不一致产生 `reference.display_mismatch`，不能静默重绑定。
   - 用户粘贴绕过 data validation 的 reference token 时，import 仍按 schema resolver 报 ambiguous/missing/type mismatch；data validation 不能替代 validator。
   - helper sheet 丢失但 metadata/layout 可确认时可重新生成，并报告 `layout.helper_artifact_mismatch`。
   - helper sheet generated block 被手动改动时可覆盖重生；但 generated range 中出现非 ExcelDB-owned 内容时必须报 `structure.helper_region_overlap` blocker。
   - data validation 缺失、引用旧 named range 或和 schema 不等价时报告 `layout.data_validation_mismatch`；可保真修复时进入 layout refresh patch。
   - 非 ExcelDB-owned Excel comment 不被静默覆盖；schema 注释仍写入 row 7/helper sheet，并报告 `diagnostic.user_comment_preserved`。
   - simple struct 字段在 single cell 与 expanded columns 两种布局下都能保留数据并正确导入导出。
   - repeated simple struct / owned child object 默认使用 child table，不写 JSON cell。
   - child table sheet 默认命名为 `{parent_schema_name}.{field_path}`，并保留 child data region。
   - child row metadata 保存 owner identity、parent field id、element guid/local id、order index。
   - orphan child row、duplicate element identity、hard missing owner 都产生 diagnostic，convert blocker。
- map 默认 canonical 表达为 child table key/value rows，key field 唯一。
   - oneof/union 默认需要 stable variant id；删除正在使用的 variant 是 convert blocker。
   - map child rows 的 duplicate canonical key 产生 `validation.map_key_duplicate`；key normalizer/compare policy 改变必须扫描旧数据。
   - oneof/union 使用 discriminator stable variant id；未知 variant 产生 `validation.variant_unknown`，当前 variant 外 payload 非空产生 `validation.variant_payload_invalid`。
   - union 切换 variant 时旧 payload 清理是 data-loss-risk edit，必须走 Undo/confirmation；layout strategy 互换需要 migration 或可逆证明。
   - arbitrary JSON cell 默认不作为通用能力；opt-in custom codec 不参与 property-level merge。
   - simple struct single-cell 默认 codec 输出 `subfield=value;subfield=value` canonical form。
   - repeated scalar single-cell 默认 codec 输出 `value;value;value` canonical form。
   - string 值使用 JSON string literal，number 使用 invariant culture。
   - single-cell codec 的 `;` 只在非 JSON string literal 上下文分隔；含分隔符、换行或 `=` 的 string/reference token 必须 quoted。
   - trailing delimiter、empty element、重复 subfield、未闭合 JSON string、未声明 preserve 的 unknown key 都产生 `cell.parse_failed` 或对应 validation diagnostic，不能 last-write-wins。
   - empty string 必须写 `""`，explicit null 写 `null` 且仅 nullable 允许，missing 通过省略 pair 或空 cell 表达。
   - 普通 scalar/reference cell 中，blank/absent 默认是 missing，bare `null` 是 explicit null，`""` 是 explicit empty string，显式 default 值必须写 visible token；四者在 authoring snapshot/diff/merge 中不能折叠。
   - literal string `null`、`""` 或 reserved token 文本必须使用 JSON string literal escaping；不能和 state token 混淆。
   - `blank_string_policy=empty_string` 只能用于非 nullable、无 default 的 string 字段；否则 schema-lint 产生 `schema.value_shape_invalid`。
   - clear nullable field 后 SaveAssets 写 bare `null`；clear optional/default field 后 SaveAssets 清除 value payload 并保留 style/comment，不写 default token。
   - `MaterializeDefaultValues` 是显式 operation；普通 GenerateStructure/LayoutRefresh/SaveAssets 不批量物化 schema default。
   - parse error 产生 `cell.parse_failed` 并定位到 cell/subfield。
   - `merge_granularity=cell` 默认不做 child-level merge；`merge_granularity=child` 必须 schema 显式声明。
   - 只改 description/header comment 会更新 layout hash，但不导致 converted bytes schema hash 不兼容。
   - `SkillConfig` 这类常见配置表可以通过 preset 生成 key/display/category/tags/enabled/review/export/resource/expression/child table 字段，但 canonical descriptor 中必须已经展开为普通 field/reference/value shape。
   - preset 不产生隐式魔法列名：手工新增 `enabled`、`tags`、`review_state` 同名列但缺 field id/alias 时按 unknown/helper/freeform 规则处理。
   - `enabled` 作为 row_export_filter 时，disabled row 仍可作为 authoring asset 保存和诊断；当前 export view strip 后被 hard reference 指向时按 reference/export policy 报告 blocker。
   - `tags/category` 只有 schema 声明 label descriptor 时参与 `FindAssets("l:")`；否则不从列名猜标签。
   - `authoring_note` / `description` / `review_state` 是否影响 runtime bytes、source hash、review gate 由 export/gate policy 决定，不能由 importer 硬编码。
   - Unity resource path rename but same guid remains valid。
   - Unity resource missing/empty guid reports diagnostic。
   - incompatible converted bytes schema hash。
   - compatibility analyze 从 canonical previous/current `SchemaDescriptor` 生成 `schema_diff_entry`，不从 proto 文本、C# reflection 或 Excel header 推断。
   - schema_diff_entry 的 diff_id deterministic，排序不依赖发现顺序、字典顺序、本机路径或 workbook path。
   - table/field/enum/validator 首先按 stable id 匹配；display name、field path、C# member name 相同但 id 不同仍是 delete + add。
   - alias 只能用于 layout repair、visible path 兼容和 migration suggestion，不能跨 id 自动搬数据。
   - 缺 previous descriptor snapshot 时进入 degraded analyze，禁止自动 migration apply，并在 report 标记 `previous_schema_snapshot_missing=true`。
   - field id 不变时 rename field 保留数据并只刷新 layout。
   - field id 改变但 name 相同时不自动搬数据，必须要求 migration。
   - 新增 required 且无 default 时 workbook 可打开，但 convert/build blocker。
   - 删除仍有数据的字段必须 deprecated/reserved 或显式 migration。
   - 删除旧 field/table id 未进入 reserved/deprecated registry 是 schema-lint blocker；reserved id 复用产生 `schema.reserved_id_reuse`。
   - enum 删除正在使用的 value 时 editor 可显示错误，convert/build blocker。
   - enum token/display 相同但 value id 改变时按 delete + add，不按 token 自动搬数据。
   - optional -> required 必须扫描当前数据；无缺失才可兼容，有缺失则 import allowed but convert blocked。
   - default semantic 改变且旧数据依赖 materialized default 时必须数据扫描，影响 runtime/export value 时需要 migration 或 convert blocker。
   - validator 收紧必须执行 validation scan，违反项使 compatibility 至少为 `import_allowed_convert_blocked`；validator 放宽默认 compatible。
   - simple struct `single_cell` 与 `expanded_columns` 互换需要 migration dry-run。
   - reference nullable/soft/hard 收紧时按现有数据决定 blocker。
   - key pattern 改变只触发 moved/renamed，key field 改变需要 migration 并检查 duplicate key。
   - export policy 改变 exported field set 时 converted bytes schema hash 变化，并生成 runtime compatibility report。
   - compatibility level 按 compatible < layout_refresh < import_allowed_convert_blocked < migration_required < incompatible 归并；operation target 只能进一步收紧。
   - compatibility gate 对 `editor_open`、`custom_editor_open`、`save_assets`、`convert_bytes`、`runtime_switch` 生成 deterministic `access_mode`、allowed write regions 和 publish target；profile 只能收紧不能放宽。
   - `compatible_with_layout_refresh` 下，stale header/dropdown/helper 不作为事实源；field id mapping 稳定且无 `requires_physical_column_order` 时 convert/runtime publish 可继续，layout refresh 只改 schema-owned structure。
   - layout refresh patch 与 helper/freeform/user comment/formula ownership 重叠时只阻断 layout write；不自动覆盖用户区域，也不把纯数据读取误判成 migration。
   - `import_allowed_convert_blocked` 下 generic table 可编辑并保存 authoring 修复；保存后若仍有 validation error，status/report 保留 convert blocker，`convert` 仍失败。
   - `import_allowed_convert_blocked` 下 custom editor 未声明 invalid-state editing capability 时进入 `table_view` / `read_only`，不能继续按完整业务对象编辑。
   - 无法 parse 的 raw cell 不会被 object 默认值或 default materialization 覆盖；只有用户显式编辑该 cell 后才允许写入新 canonical value。
   - `migration_required` 下 Refresh/SaveAssets/GenerateStructure/RuntimeDatabase.SwitchDataSource 都不执行 migration 副作用；只有 migration dry-run/apply 能推进 schema。
   - previous descriptor snapshot 缺失或 hash 不可信时只能 read-only/report-only 打开，禁止自动 migration apply、批量 save 和 destructive layout rewrite。
   - runtime hot reload 遇到 `import_allowed_convert_blocked` 或 `migration_required` candidate 时不发布 property/dependency change，旧 source/object graph 继续服务。
   - SaveAssets/migration apply/merge/layout refresh 写文件后必须 reimport + compatibility gate 复验；承诺 compatible 的操作复验未达成时恢复或 manual recovery。
   - compatibility report 列出所有 diff entry、数据扫描范围、row count、missing required count、lossy conversion count 和 migration suggestion descriptor hash。
   - migration dry-run 必须输出 canonical MigrationPlan，包含 plan_hash、source fingerprint/revision、descriptor hash、step descriptor hashes、diff_entries、dry_run_report_hash、expected hashes。
   - migration diff entry 必须列出移动、改写、删除、默认填充的数据量、operation kind、table/field/row identity 和 cell/range location。
   - helper/freeform 保留区域必须以 `helper_preserve` diff entry 记录，证明 migration 没覆盖策划手写区域。
   - migration registry duplicate id/version、descriptor hash mismatch、dependency cycle 都是 schema blocker。
   - 同一 from/to schema 有多条可行 migration path 且无唯一优先级时，报 `schema.migration_ambiguous`。
   - migration dry-run 不写 workbook，并检查 source revision、文件锁、backup/temp/recovery manifest 可创建性。
   - migration apply 必须先验证 plan_hash、dry_run_report_hash、source fingerprint/revision、schema hash、layout hash、descriptor hash、step descriptor hash 和 data loss confirmations 未变化。
   - migration apply 成功后写 migration history，包含 plan_hash 和 dry_run_report_hash；重复执行时已验证的 step 必须 skip，不能重复追加列/row/metadata。
   - partial migration / recovery manifest / migration history inconsistent 都产生 recovery diagnostic，阻止 convert。
   - `data_loss_risk=true` migration 需要用户或 CI policy 确认，并列出不可逆 cell/range。
   - host requirement 不可用的 migration 不能执行；Unity-only migration 在 core CLI 中报错。
   - source root/glob 先 materialize 为 `SchemaSourceSetDescriptor`；同一 source set 换机器、换文件枚举顺序仍生成相同 `schema_source_set_hash` 和 descriptor_hash。
   - import 缺失、import 歧义、import cycle、dependency lock 不匹配产生 `schema.source_set_invalid`。
   - 未登记 `exceldb`/extension option 产生 `schema.option_invalid`。
   - table id / enum id / child table id / variant id 缺失产生 `schema.id_missing`；table id 冲突、field id 冲突、enum value id 冲突分别产生 `schema.table_id_conflict`、`schema.field_id_conflict`、`schema.enum_value_id_conflict`；其他 descriptor id scope 冲突产生 `schema.id_conflict`。
   - `allocate-id` 只能修改 schema source 或输出 proposal，不能在 canonical descriptor 阶段注入未落盘 ID。
   - `allocate-id` 对同一 source_set_hash、registry_hash 和 request set 在不同机器生成相同 allocation manifest、source patch、report hash；批量 request 结果不依赖 proto 文件顺序、命令行参数顺序或文件系统枚举顺序。
   - `allocate-id` 遇到缺失/重叠/非法 range、scope 不存在或 allocation policy 不合法时产生 `schema.id_range_invalid`，不写 source。
   - `allocate-id` proposal apply 时 source file content hash、schema_source_set_hash、registry_hash 或 project policy hash 改变，产生 `schema.id_allocation_stale`，不写部分 source。
   - 已发布 field 修改 proto field number 但显式 field id / registry lock 不变时，field identity、schema compatibility 和 workbook column mapping 不变；显式 id 缺失时产生 `schema.id_missing`，不能重新分配。
   - 删除 published table/field/enum value/variant 后没有 reserved/deprecated record 时 schema-lint blocker；补 reserved record 后旧 workbook 可 degraded analyze / migration analyze，但新 descriptor 不能复用该 id。
   - import、convert、runtime open、hot reload、canonical descriptor build 和 codegen 都不能自动 allocate schema id；发现未落盘 id 只能失败或输出 proposal。
   - C# member rename 但 schema field path / field id 不变时，不改变 schema hash，不改变 `FindProperty` path。
   - schema 字段 `mana_cost` 默认生成 C# 属性 `ManaCost`，但稳定 property path 是 `mana_cost`。
   - editor/importer/converter/runtime 从同一个 generated registry 查询 descriptor，不允许各自构建 schema view。
   - schema hash 不受 proto 文件注释、生成时间、本机路径、generated code 格式影响。
   - 两个等价 schema source 在不同机器生成相同 canonical descriptor bytes、descriptor_hash、schema_hash、layout_hash。
   - reserved id 复用、alias 歧义、runtime type binding 冲突、property path binding 冲突都是 schema-lint blocker；alias 歧义产生 `schema.alias_ambiguous`，property path 冲突产生 `schema.property_path_conflict`，runtime type binding 冲突产生 `schema.runtime_type_binding_conflict`。
   - descriptor source map 缺失不改变 lint 结论；存在时 diagnostic 能定位到 logical source path/line/column。
   - extension package id/version/namespace、diagnostic code prefix、host requirement、deterministic flag、permission descriptor 都由 schema-lint 校验。
   - extension dependency missing/cycle、version conflict、registry conflict 分别产生稳定 diagnostic，且 convert/build blocker。
   - deterministic=false 的 extension 不能参与 schema/source/bytes/cache hash；参与 convert/build/runtime open 时 blocker。
   - 未声明或未授权 required permission 产生 `extension.permission_denied`；optional permission 进入 fallback/read-only。
   - 外部工具非零退出、超时、未声明输出分别产生 `extension.external_tool_failed`、`extension.external_tool_timeout`、`extension.undeclared_side_effect`。
   - registry manifest 记录 selected/disabled/fallback extension set、descriptor hash、host set、granted/denied permissions 和 fallback reason。
5. 定义 machine-readable report / diagnostic schema：
   - report lifecycle：`OperationPlan`、`PreflightReport`、`FinalOperationReport`、`GateResultReport` 分离；dry-run 只冻结 preflight report，commit/verify/publish 后冻结 final report，gate result report 不改写原 report。
   - operation_id、operation_kind、report_format_version、tool_version、producer id/version、context id/kind、phase、source identity/revision、schema hash、layout hash、operation_profile_id、operation_profile_hash、gate_policy_hash、operation_plan_hash、result severity、report_hash。
   - OperationPlan 使用 canonical bytes 计算 operation_plan_hash；dry-run/preview/report/commit/apply 都引用同一 plan hash。
   - patch_proposals 记录 patch kind、target subject/location、old/new hash、preconditions、data_loss_risk、confirmation、verification expectation 和 rollback boundary。
   - auto_fix_proposals 记录 proposal id/kind、origin diagnostics、stable target identity、owner region、safety_class、old/new raw/canonical/source hash、required backend feature、can_auto_fix、confirmation 和 verification expectations。
   - 同一 source revision、schema/profile 和 diagnostic 输入在不同机器生成相同 `proposal_id`、`patch_preview_hash` 和 proposal 排序；row number、cell address、sheet name、human-readable action 不得作为 proposal 唯一身份。
   - apply proposal 前必须重新验证 proposal id、operation_plan_hash、source revision、raw fingerprint、schema/layout/profile hash、backend capability、confirmation binding 和 affected set；任一变化产生 stale plan，不写 workbook。
   - `can_auto_fix=true` 不等于自动提交；只有 safety_class、operation profile、owner region、data_loss、side effect、backend capability 和 confirmation gate 同时通过时，proposal 才能转成 patch_proposals。
   - `safe_metadata_only`、`safe_layout_display`、`canonical_display_only`、`authoring_value_change`、`identity_repair`、`external_side_effect`、`data_loss_risk` 的默认 apply gate 在 report/plan/CI 中一致，不允许 validator、metadata repair 或 Unity adapter 各自发明安全等级。
   - affected_subjects 必须闭包展开；commit 时发现未计划 workbook/source/runtime/generated artifact 写入产生 `transaction.side_effect_mismatch` 或重新 analyze。
   - preflight_token 绑定 operation_plan_hash、base revisions、file lock/write permission、backup/temp/recovery path、capacity reservation 和 confirmation hash；过期时产生 `transaction.plan_stale` / `transaction.source_revision_changed`。
   - side_effect_summary 记录 expected/actual/unexpected/missing side effects；unexpected 或 missing side effect 不能把 artifact/cache 标记为成功。
   - commit 只能执行 plan 中声明的 proposal；同一 cell/range/slot 多个 proposal 必须 plan 阶段合并或产生 conflict，不能靠 commit 顺序覆盖。
   - report 使用 canonical JSON，UTF-8 without BOM，LF 换行，object key / diagnostic / diff_summary 排序 deterministic。
   - 同一输入、同一 profile、同一 source revision 在不同机器生成相同 machine-readable report_hash；started_at/duration/debug trace 不进入 report_hash。
   - diagnostic code、diagnostic_id、severity/effective_severity、scope、location、affects_import/convert/runtime/writeback、suggested_action、data_loss_risk。
   - 相同 diagnostic_id 在同一 operation 中去重并合并 occurrence_count / related_locations，不按发现顺序输出重复项。
   - location 使用 workbook guid、table id、field id、row guid/local id 等 stable identity；row number / cell address 只作为现场定位，不能作为 suppression/auto-fix 唯一 key。
   - subreport 继承 operation_id，拥有独立 report_id/report_hash；顶层 result_severity 汇总所有未 suppressed diagnostic 和 subreport result。
   - gate policy 重新判定生成 gate result report，引用原 report_hash，不改写原 report。
   - diagnostic code taxonomy：内置 namespace、v1 core code 清单、默认 severity/affects 约定。
   - code 格式只允许 lowercase ASCII 点分 namespace + snake_case leaf，不包含动态 workbook/table/field/row 内容。
   - code 含义一旦发布不得改写；删除 code 只能进入 deprecated/reserved，并提供 replacement 建议。
   - custom validator / codec / migration / drawer 的 code 必须在 generated registry 中登记，prefix 冲突是 schema-lint error。
   - unknown custom code 在 schema-lint/CI 下不能被当 warning 放过。
   - 文档、schema descriptor、validator、report sink 中出现的内置 diagnostic code 必须全部存在于 core code catalog；同一语义不得出现两个 code 名。
   - human-readable report 不参与逻辑判断。
   - `__ExcelDB_Diagnostics` 是 generated helper sheet，import/export 忽略。
   - diagnostic projection record 写 source_report_id/hash、operation_id、workbook_guid、source_revision、schema/layout hash、projection_checksum。
   - `__ExcelDB_Diagnostics` 默认是 latest projection，全量重建 ExcelDB-owned block，不在 workbook 中累积历史诊断。
   - projection stale 只影响 Excel/UI 展示；status index 和 machine-readable report 仍是当前事实源。
   - diagnostic helper sheet 和 cell marker/comment 不参与 schema hash、layout hash、source hash。
   - diagnostic writeback 不覆盖用户已有 comment/note，只更新 ExcelDB-owned marker/comment。
   - cell marker/comment 没有 ExcelDB projection marker 时视为用户内容；同 cell 多诊断时 marker 只展示最高 severity 和 helper anchor。
   - 新 report 中消失的 diagnostic 只清除对应 ExcelDB-owned marker/comment，不清用户批注、颜色或备注。
   - workbook merge 遇到双方只改 diagnostic projection 时丢弃投影，merge 后重新生成，不产生业务 conflict。
   - diagnostic writeback 失败只增加 report diagnostic，不改变 import/convert 结论。
6. 定义 validation pipeline / validator contract 测试：
   - validation phases：schema_lint、workbook_structure、cell_parse_normalize、row/table/reference/cross_table、convert、runtime_open。
   - severity 只允许 info/warning/error/blocker，convert blocker 通过 affects_convert + policy 表达。
   - required、nullability、type_parse、enum、range、regex、unique_key、row_identity_unique、reference、UnityResourceRef 都有内置 validator。
   - absent/blank cell 默认作为 missing；nullable/default/required/string blank policy 分别按 schema 处理。
   - required missing 产生 `validation.required_missing`；schema 要求实际单元格但 cell absent/blank 时产生 `cell.blank_required`。
   - canonical value bytes 使用 stable type tag + payload；string `"1"`、number `1`、enum value id `1`、bool `true` 不能产生相同 canonical bytes。
   - missing、explicit null、explicit value、default materialized 是不同 canonical state；authoring diff/merge 必须能区分。
   - 用户显式填写 default 值与 missing 后 materialize default 在 workbook source 中不同；只有 export view/profile 明确允许时才能在 converted bytes 中折叠。
   - SaveAssets 对 missing/default_materialized 清除 value payload，不删除 style/comment/hyperlink；对 explicit_null 写 bare `null`；对 explicit empty string 写 `""`。
   - `stringValue = null` / `stringValue = ""` 产生 explicit empty string，不产生 missing/null；nullable/default clear 必须走 explicit clear operation。
   - `MaterializeDefaultValues` dry-run/apply 把 default_materialized 转 explicit_value，输出 old/new canonical state、affected cells 和 report hash；source revision/schema hash 变化时拒绝 apply。
   - number parse 不依赖 Excel 显示格式；int 字段小数部分产生 `cell.integer_fraction`，超出目标类型产生 `cell.number_out_of_range`，decimal/fixed-point 不静默 rounding。
   - NumericDescriptor 覆盖 numeric kind、bit width、scale、range、rounding、physical cell kind、percent/token policy 和 runtime storage；这些字段进入 schema/source/bytes 相关 hash。
   - `i64/u64` 超出 IEEE-754 double 精确整数范围时，Excel number cell 不能被当作精确值；必须使用 invariant string token 或产生 parse/save error。
   - fixed_decimal 输入小数位超过 scale 时，`rounding_policy=reject` 默认失败；启用 half_even/half_away/truncate/floor/ceiling 时 canonical unscaled value 和 report 在不同机器一致。
   - Excel percent number format 只影响显示；raw value `0.25` 与显示 `25%` 的语义由 descriptor 决定，importer 不能按 cell format 猜测乘除 100。
   - string token 中的 `%`、千分位、货币符号只有 schema normalizer 显式允许时接受；normalizer 改 canonical value 时只能生成 auto-fix proposal。
   - SaveAssets 写 fixed_decimal/u64 时不能把会丢精度的值写成 Excel number cell；应写 invariant string token 或拒绝保存。
   - bool 默认只接受 true/false；1/0 必须由 schema numeric alias 显式允许。
   - string 默认 exact value，不 trim、不 case fold、不 Unicode normalize；normalizer 必须有 id/version。
   - normalizer 只能输出 canonical value、diagnostic 和 auto-fix proposal；不能直接写 workbook，也不能失败后返回 default。
   - date/time 默认 ISO-8601 string；Excel serial date 必须声明 1900/1904、timezone 和 precision。
   - formula cell 在 runtime/export 字段默认产生 `cell.formula_not_allowed` convert blocker；`allow_cached_value` 必须校验 cached result、formula text 和 calc state。
   - cached result 缺失/过期/类型不匹配产生 `cell.formula_cache_invalid`；import 范围外公式依赖产生 `cell.formula_dependency_undeclared`。
   - volatile 函数、外部链接、未声明 UDF/宏、当前时间/用户/网络/locale 依赖默认产生 `cell.formula_nondeterministic`，不能进入 converted bytes。
   - deterministic formula evaluator 必须声明 evaluator id/version、function set、host requirement 和 declared dependency ranges；evaluator 不可用或语义变化必须影响 schema/cache/compatibility report。
   - source_cell_hash 不包含样式、列宽、row number、column index、header text；公式 cached value 允许时包含 formula text、cached canonical value 和 calc state。
   - Excel error cell 产生 `cell.error_value`；merged non-top-left data cell 产生 `cell.merged_data_cell`。
   - EditorImport 下 error asset invalid 但 workbook 可显示；Convert/CI 下 affects_convert error 阻止 bytes 输出。
   - normalizer 只生成 canonical value 和 auto-fix proposal，不静默改写 Excel cell。
   - normalizer 只改变 display token 且 canonical value hash/source hash 不变时，proposal safety_class 是 `canonical_display_only`；Apply 后不发布 runtime property_changed。
   - normalizer 改变 canonical value、canonical state、source hash 或 converted bytes 时，proposal safety_class 是 `authoring_value_change`；普通 Refresh/import/SaveAssets 不能静默 apply。
   - stale auto-fix proposal 在 raw cell、source revision、schema/layout/profile hash 或 backend capability 变化后必须拒绝 apply，并保留 workbook 原状。
   - custom validator 有 stable id/version/phase/mode/affected ids，不能直接写 workbook 或修改 object graph。
   - 多个 custom validator 的执行顺序由 phase、dependency graph、ordering key、validator id 决定，不依赖注册顺序。
   - validator/normalizer/codec 的 side effect policy 默认 pure；需要外部状态的 adapter extension 必须把状态 snapshot 写入 dependency manifest 或 report。
   - adapter validator 必须声明 host requirement，核心 runtime 不依赖 Unity AssetDatabase。
   - UnityResourceRef guid/path mismatch 以 guid 为准并产生 auto-fix proposal；path 不反向改 identity。
   - UnityResourceRef guid 为空但 main_asset_path 有值时，普通 import 只生成 guid repair proposal；只有 picker/resolver/approved auto-fix commit 才写 guid。
   - UnityResourceRef guid 有值但 main_asset_path 为空时，引用仍有效；display refresh 可补 path，canonical value/source hash 不变。
   - UnityResourceRef guid 与 main_asset_path 指向不同 asset 时产生 `unity_ref.path_mismatch`；guid 继续作为 canonical identity，普通 SaveAssets 不因 path hint 重绑定。
   - UnityResourceRef 只刷新 stale/missing main_asset_path 时 proposal safety_class 是 `safe_layout_display`；从 path 解析并写入 guid 时是 `authoring_value_change`，需要 picker/resolver/approved apply。
   - `path_display_policy=generated` 允许 display refresh 覆盖 stale main_asset_path；`user_editable_with_guid_priority` 只把用户 path 当 repair hint；`hidden` 不生成可见 path 列。
   - 用户通过 picker/rebind 改 Unity 引用时，必须同时写 new guid、new path、optional sub-asset identity，并产生 reference changed diff、Undo、dirty 和 dependency_changed。
   - UnityResourceRef guid 缺失、guid 格式错误、missing asset、asset type mismatch、runtime provider missing 都有稳定 diagnostic code。
   - UnityResourceRef canonical value / source hash 覆盖 guid、optional sub_asset_local_file_id、reference family/version、dependency kind 和 runtime provider key inputs；main_asset_path、sub_asset_name/type display 默认只影响 display hash/report。
   - `allow_sub_asset=false` 时选择/导入 sub asset 是 capability error；开启 sub asset 时必须保存 local file id / name / type extension，并以 local file id 作为 sub-asset identity。
   - sub_asset name/type 改变但 local file id 不变时只刷新 display/report；local file id 改变产生 property_changed + dependency_changed。
   - Release exported UnityResourceRef 的 runtime_key 必须由 provider descriptor deterministic 生成，并进入 runtime_dependency_hash；不能用 main_asset_path 兜底。
   - LocalizedTextRef 解析不到 text key 产生 `localization.key_missing`；required locale 缺失产生 `localization.locale_missing`。
   - localization fallback graph 有环产生 `localization.fallback_cycle` blocker。
   - locale 文本 token 与 schema token descriptor 不一致产生 `localization.token_invalid`。
   - localization provider 缺失、版本不匹配或 manifest 未绑定 provider 产生 `localization.provider_missing`；Release 不读 Excel/preview_text 兜底。
   - fallback resolution 顺序固定，explicit empty string 不 fallback；blank-as-missing 必须由 descriptor 显式声明。
   - runtime localization formatter 使用 `LocaleId`、`LocalizedTextRef`、typed token id 参数和 caller-owned `Span<char>`；buffer 不足返回 truncation/required count，不分配 string/list/dictionary。
   - preview_text 与 default locale 不一致只产生 `localization.preview_mismatch`，不改变引用 identity。
   - Expression<T> 字段必须在 import/convert 阶段 parse、bind、typecheck；parse/unknown symbol/type mismatch 分别产生稳定 diagnostic。
   - 未声明函数、非 deterministic 函数、外部状态访问和超预算表达式不能进入 Release bytes。
   - 表达式 canonical AST 相同但空白/格式不同不触发 runtime property_changed。
   - curve 字段默认使用 point child table，点拥有 element guid/local id；Excel 行顺序改变但 canonical x order 不变时不触发 runtime property_changed。
   - curve x/y/tangent/interpolation 按 descriptor parse/validate；duplicate x、非单调 x、缺 tangent、非法 LUT/domain policy 产生稳定 validation/schema diagnostic，不由 runtime 猜测。
   - curve converted bytes 保存 deterministic segment table 或 LUT；同一 descriptor/source 在不同机器生成相同 curve bytes hash。
   - runtime curve sample 使用 explicit descriptor/domain/extrapolation policy，预热后 binary search / LUT lookup 不产生 GC allocation，不调用 UnityEngine.AnimationCurve。
   - Unity AnimationCurve drawer 只能作为 projection；粘贴/编辑必须转成 child point diff，无法表示的 tangent/wrap mode 不静默丢弃。
   - weighted selection 使用 child table entries；负权重、概率和不合法、空候选池分别产生稳定 validation diagnostic。
   - weighted selection runtime 必须接收显式 random stream；同 source、同 RNG state、同 condition context 结果 deterministic，且选择过程不产生 GC allocation。
7. 定义非破坏性 Excel 写回测试：
   - 修改单个数据 cell 不破坏 unknown columns。
   - 修改单个数据 cell 不破坏样式、公式、批注、筛选、冻结窗格、data validation。
   - 未在 package patch plan 中声明的 xlsx ZIP entry / OPC part content hash 必须保持不变；否则产生 `save.package_preservation_failed`。
   - sharedStrings 只 append/reuse，不 compact/reorder；既有 shared string index 不变化。
   - `.xlsm` 保存后 `vbaProject.bin` content hash 不变；带数字签名 workbook 默认产生 `save.signature_would_be_invalidated`，除非 policy 明确允许。
   - 尝试覆盖 formula cell 时默认 blocker，除非 schema 明确允许。
   - 修改可能被公式引用的 cell 时，必须安全更新 calcPr/calcChain 以请求 Excel 重算；后端不支持时产生 `save.unsupported_xlsx_feature`。
   - Convert/CI 不能依赖 Excel 打开后自动重算；公式字段要么证明 cached value fresh，要么使用 deterministic evaluator 计算。
   - 新增/移动行列若影响 ListObject、pivot cache、drawing/chart anchor，后端不能保真 patch 时不写 workbook，并报告 `save.unsupported_xlsx_feature` 或 `layout.column_move_skipped`。
   - metadata flush 失败时，涉及新 identity 的保存必须 blocker。
   - 写 workbook 前创建同目录 backup、temp workbook 和 recovery manifest。
   - temp workbook 写完后复读关键 metadata 和 patched cell/range。
   - replace 后再次 verify；verify 失败时 restore backup 或生成 manual recovery required report。
   - replaced workbook verify 通过后必须重新 import committed snapshot；只有 committed snapshot 与 verification_expectations 匹配才清 dirty / resolved_pending_save。
   - save success report 记录 pre_save_revision、committed_revision、source_hash_before/after、metadata_checksum_after 和 reimport_report_hash。
   - replace 与 reimport 之间 source revision 又变化时，不清 dirty，报告 `transaction.source_revision_changed` 或 `transaction.verify_failed`。
   - 平台不支持 atomic replace 时，report 标记 `atomic_replace=false` 并保留 backup。
   - mount 时发现遗留 temp/recovery manifest，生成 recovery diagnostic，不静默删除。
8. 定义 asset identity 与 path policy：
   - row guid/local id 是身份。
   - workbook guid 在同一 context / mount set 内唯一。
   - static `AssetDatabase` / `RuntimeDatabase` 默认委托到 host default context；`BindContext` scope 内委托到绑定 context，scope 结束恢复原 context。
   - 两个 context 打开同一 source 时共享 asset identity，但 `GetInstanceID`、object reference equality、dirty draft、operation queue 和 report 相互隔离。
   - SourceSetDescriptor 记录 normalized_mount_path、canonical_physical_id、workbook_guid、workbook_alias、role、required、schema/layout/source hash。
   - source_set_id 只由 runtime workbook guid set、schema hash 和 role membership 派生；alias/path 不进入 source_set_id。
   - source_set_descriptor_hash 改变不必然改变 source_set_hash；alias/path/display-only 变化不发布 property_changed。
   - workbook alias 只用于人类输入和可见引用 token，不参与 asset identity、source_set_hash、asset guid/local id。
   - alias 缺失或冲突时 resolver 不使用 alias；使用该 alias 的 target scope 产生 `source.workbook_alias_conflict`。
   - 同一 physical workbook 多路径 mount 只保留一个 source context，并报告 `source.mount_path_conflict`；不同 physical workbook 同 mount path 是 blocker。
   - key 是人类可读定位。
   - key rename 不改变 object identity。
   - workbook move 不改变 asset identity，只触发 moved report/event。
   - 同一 physical workbook 多路径 mount 只保留一个 source context；不同 physical workbook 同 guid 即使内容相同也报 duplicate workbook guid blocker。
   - 复制 workbook 作为新配置源时，必须通过 clone/repair operation 生成新 workbook guid。
   - clone 默认保留 row guid/local id，但因 workbook guid 改变而形成新 asset identity；self reference remap 到新 workbook guid，external reference 默认保留。
   - duplicate workbook guid repair 不能在 import/hot reload 中静默执行，只能生成 proposal 或由 clone/repair command 提交。
   - asset path 变化触发 moved/renamed 事件。
   - 新 Excel 行缺 metadata 时进入 `temporary_imported`，成功 metadata flush 后才成为 stable identity。
   - `CreateAsset` / child insert 产生 `temporary_draft`，预分配最终 row guid/local id 和 guid/local file id。
   - `FlushMetadata()` 只写 metadata/companion identity，不写正式数据 cell、不清除 dirty_editor。
   - metadata_auto_flush=`editor_safe` 时 Refresh 可自动提交 metadata-only operation；`off` 时只留下 dirty_metadata/status diagnostic。
   - `temporary_imported` 不能进入 converted bytes、object reference metadata、runtime `added` event 或常规 `FindAssets`；runtime 发布新增 row 前必须先 flush stable identity。
   - `temporary_draft` 可以进入 `FindAssets/GUIDToAssetPath/LoadAssetAtPath`，且 SaveAssets 前后 guid/local id 不变。
   - `TryGetGUIDAndLocalFileIdentifier` 对 `temporary_imported` 返回 false 或明确失败 diagnostic，不能暴露会变化的 guid/local id。
   - row guid 为随机 128-bit lowercase hex；row local id 为 workbook-wide uint64 单调分配且不复用。
   - asset guid/local file id 分别由 workbook guid + table id + row guid、workbook guid + row local id deterministic 派生。
   - duplicate row guid 的自动修复只允许在能识别复制行候选时发生。
   - duplicate row guid/local id repair 必须写 old/new identity、row number、key snapshot、source hash；无法证明复制候选时 blocker。
9. 定义 asset path / guid / lookup 测试：
   - asset path 默认是 `{workbook_asset_path}/{table_schema_name}/{escaped_key_path}`。
   - key descriptor 由 schema 声明 key field ids、scope、normalizer、compare policy、path pattern、display name 和 duplicate/rename policy；importer 不猜第一列或列名。
   - canonical key 使用 parse 后的 canonical value、enum value id、normalizer id/version 和 stable field id；不依赖 Excel display formatting、当前文化或 header 文本。
   - key 缺失、normalizer 缺失、pattern 无效、path 不可逆、segment 无法编码都有 stable diagnostic。
   - duplicate key 不选择第一行获胜，冲突行都不进入常规 key lookup / converted runtime index。
   - 不同 key 生成相同 normalized asset path 或只差大小写时产生 `key.asset_path_collision`。
   - path escaping 对 `/`、`%`、`\`、`?`、`#`、控制字符、保留文件名字符、`.` / `..`、前后空格都有确定行为。
   - percent encoding writer 使用 UTF-8 byte 和 uppercase hex；space 输出 `%20`，`%` 输出 `%25`，`/` 输出 `%2F`，Unicode 输出 UTF-8 bytes 的 `%XX` 序列。
   - percent decoder 接受大小写 hex，但 canonical path/report 输出 uppercase；非法 `%`、非 UTF-8、NUL/control、空 segment、`.` / `..` 产生 `key.segment_invalid`。
   - `+` 不被当作空格；literal plus 与 space 的 key/path roundtrip 可区分。
   - `create_from_path=true` 的 pattern 必须能 segment-level 无歧义反解；多 key field 合并到一个 segment 时没有 deterministic delimiter/inverse function 就产生 `key.path_not_reversible`。
   - `CreateAsset`、`MoveAsset`、`RenameAsset` 对 path 反解失败时不创建 draft、不分配 identity、不写 key field。
   - key rename 后 guid 不变，`GUIDToAssetPath` 返回新 path。
   - `GetAssetPath(Object)` 对 resident/dirty/temporary draft 返回 current path，对 removed/unloaded/stale 返回空字符串并写 report。
   - stale old path 的 `LoadAssetAtPath` 不猜测 identity，并通过 report/event 指向 moved path。
   - `AssetPathToGUID(path, IncludeRecentlyDeletedAssets)` 对当前 session delete draft / tombstone 返回原 guid；`OnlyExistingAssets` 对同一路径返回空字符串。
   - recently deleted guid 不进入 `FindAssets` 常规结果，也不能被 `LoadAssetAtPath` 加载。
   - `CreateAsset(asset, path)` 只有在 key path pattern 可反解时才能从 path 填充 key fields；否则必须显式禁用或报 `key.path_not_reversible`。
   - `CreateAsset` 在 editor draft 创建时预分配最终 row guid/local id 和派生 asset guid/local file id；SaveAssets 前后 guid/local id 不改变。
   - undo create 移除 draft row，但同一 editor context 内不复用已分配的 draft row guid/local id；redo 恢复同一 `temporary_draft` identity。
   - `MoveAsset(oldPath, newPath)` 同 workbook/table 内通过 key path 反解生成 key-field draft，row identity/guid/local id 不变。
   - `RenameAsset(pathName, newName)` 替换 final segment 后复用 MoveAsset 规则；newName 非法或 pattern 不可逆时失败并写 report。
   - 跨 workbook / 跨 table `MoveAsset` 默认失败并报告 `assetdb.move_forbidden`。
   - move/rename 目标发生 duplicate key 或 case-insensitive path collision 时不创建 draft。
   - move/rename 后 editor path index 使用 draft path，old path 变为 stale；`AssetPathToGUID(oldPath)` 返回空字符串并报告 `assetdb.path_stale`。
   - `CopyAsset(path, newPath)` 创建新 row identity，复制 schema data value；owned child 生成新 element identity，普通 references 默认保持原 target identity。
   - `CopyAsset` 复制当前 applied editor draft state，不复制未 Apply 的 SerializedObject pending buffer。
   - `CopyAsset` 的 key fields 来自 `newPath` 反解，非 key 字段保留 canonical authoring state；explicit default、blank/default_materialized、explicit_null 不能被折叠成同一种状态。
   - `CopyAsset` copy closure 内部引用 remap 到新副本，外部引用保留原 target；schema 声明 clear/forbidden 时按字段 policy 生成 plan 或 blocker。
   - `CopyAsset` 对 temporary_imported、unresolved conflict、invalid/stale source、跨 table incompatible target 返回 false，并写 `metadata.identity_not_flushed`、`transaction.conflict_unresolved`、`assetdb.object_state_invalid` 或 `assetdb.copy_forbidden`。
   - `CopyAsset` SaveAssets preflight 复检 source revision、target source revision、copy closure digest、key/path collision 和 reference policy；stale plan 不写 workbook。
   - `CopyAsset` 在水位线后对 copy closure traversal、identity/reference remap table 和 report append 做 0 GC 断言；容量增长只允许记录 `performance.watermark_grew` 或按 policy 失败。
   - `GenerateUniqueAssetPath` 不分配 identity，不保留路径；默认 `_1`、`_2` suffix policy deterministic。
   - `TryGetAssetKey<T>` 对 duplicate、missing、stale path、类型不匹配返回 false，并报告 `key.lookup_ambiguous` 或对应 code。
   - `FindAssets("t:Type")` 返回 guid，按当前 asset path deterministic 排序。
   - `FindAssets(filter, searchInFolders)` 支持 folder/prefix scope；未 mount folder 返回空贡献并报告 `assetdb.search_folder_not_mounted`。
   - `IsValidFolder` 对 mounted workbook root、table root、已有 asset path prefix 返回 true；不存在、stale、未 mount、temporary_imported-only prefix 返回 false。
   - `GetSubFolders` 从当前 asset path index 返回 immediate virtual child folders，排序 deterministic，不触发 import/refresh。
   - `CreateFolder` 在 ExcelDB virtual root 下返回空字符串并报告 `assetdb.virtual_folder_create_forbidden`；不会创建空虚拟目录、不会预留 key path、不会写 workbook。
   - `ImportAsset(workbookPath)` 对 mounted workbook 做 targeted read-verify/import；未 mounted path 不隐式 mount，报告 `assetdb.import_path_not_mounted`。
   - `ImportAsset(virtualAssetPath, ForceUpdate)` 解析 owning workbook，强制 read-verify/classify；source_hash 不变时不发布 property_changed。
   - `ImportAsset` 在 `StartAssetEditing` batch 中可被 defer；`ForceSynchronousImport` 也不能从 repaint/watcher callback reentrant import。
   - FindAssets filter 支持 Unity-like name / `l:` / `t:`，并扩展 `key:` / `table:` / `path:` / `guid:`。
   - FindAssets tokenization 支持 quoted token 和转义；语法错误返回空数组并报告 `assetdb.find_filter_invalid`。
   - FindAssets structured prefix 大小写不敏感并 canonical 为 lower-case；未知 `xxx:yyy` 作为普通 name token，不因冒号报错。
   - FindAssets 空 structured query（例如 `t:` / `l:` / `guid:`）和非法 guid query 返回空数组并报告 `assetdb.find_filter_invalid`。
   - FindAssets 多个 name token 使用 AND，多个 `key:` token 使用 AND；多个 `t:` / `l:` / `table:` / `guid:` / `state:` token 使用 OR，多个 `path:` token 使用 OR。
   - quoted token 只保留 whitespace 为单个 substring token，不表示 exact phrase；`name:"fire ball"` 查一个含空格的 name token，`"l:rare item"` 作为普通 name token。
   - `l:` 查询只读取 schema label descriptor 生成的 asset/search label token；没有 label descriptor 时不从 `tags/category` 列名猜标签。
   - `GetLabels(Object)` 只返回 `role=asset_labels` 的 display token；`search_labels` 可被 `FindAssets("l:")` 命中但不被 `GetLabels` 返回。
   - `SetLabels` / `ClearLabels` 只有唯一 mutable asset label write group 时可写；无 descriptor、多个 write group、readonly/deprecated 字段分别产生 `assetdb.labels_not_supported` 或 `assetdb.label_mapping_ambiguous`，不创建 draft。
   - `SetLabels` 对 enum label 只接受已声明 enum value id；string label 只有 `allow_new_string_labels=true` 才接受新 token；非法输入产生 `assetdb.label_invalid` 或 validator diagnostic。
   - label edit 只创建 editor draft diff、Undo、dirty/status/search-index update，SaveAssets 才写 Excel；Excel 外部同时改 label cell 时进入 conflict。
   - label 字段 editor-only 时只影响 editor search index；进入 export view 时改变 source_hash，并在 runtime hot reload 中按 property_changed/source_summary 发布。
   - label operation core 在水位线后 normalization、dedup、index update、report append 0 GC；`GetLabels` 返回数组作为 Unity-like projection 分配单独分账。
   - FindAssets 不 materialize object，不 import，不修复 metadata，只查询 asset search index。
   - embedded child 不进入 FindAssets；schema 声明 findable 的 sub_asset 可以进入。
   - `temporary_draft` asset 可在 Editor authoring 的 `FindAssets/GUIDToAssetPath/LoadAssetAtPath` 中查到，但不能进入 convert/runtime index。
   - `temporary_imported` row 不进入 `FindAssets`，`AssetPathToGUID` 返回空字符串并报告 `metadata.identity_not_flushed`；`GetWorkbookDiagnostics` 能定位到行/cell。
   - `LoadMainAssetAtPath` 返回主 asset；`LoadAssetAtPath(path, type)` 和 generic overload 按 main-first 再 sub-asset order 返回第一个类型匹配对象。
   - `LoadAllAssetsAtPath` 返回主 asset + Project-visible sub assets；`LoadAllAssetRepresentationsAtPath` 只返回 sub asset representations，不返回 embedded child/helper/report artifact。
   - `GetMainAssetTypeAtPath` 不 materialize object，找不到/invalid/stale 返回 null。
   - `Contains` 只对当前 context 的 resident/dirty/temporary draft/sub asset representation 返回 true；transient、temporary_imported preview、removed/stale/unloaded/其他 context object 返回 false。
   - `TryGetGUIDAndLocalFileIdentifier(Object)` 返回 deterministic asset guid/local file id；`GetInstanceID` 仍只在 context 内稳定。
   - `TryGetGUIDAndLocalFileIdentifier(instanceID)` 只查询当前 context-local instance id table；其他 context、Unity proxy instance id、closed context id 返回 false 或需先经 adapter proxy registry 映射。
   - `Contains(temporary_draft)` 为 true，但 `EditorUtility.IsPersistent(temporary_draft)` 为 false；SaveAssets 成功后 persistent 才为 true。
   - stable asset 的 dirty draft 同时满足 `Contains=true`、`IsPersistent=true`、`EditorUtility.IsDirty=true`；保存成功后只清 dirty，不改变 persistent。
   - `IsMainAsset` 对主 row asset true，对 schema-owned sub-asset false；`IsSubAsset` 反之，对 embedded child/plain array/helper/report object false。
   - `IsForeignAsset` 对 Excel workbook / converted authoring projection 的 stable asset true；`IsNativeAsset` 对默认 ExcelDB virtual asset false，Unity proxy/cache 状态不污染 ExcelDB object。
   - 其他 editor context 的 object 传给 `GetAssetPath`、`SetDirty`、`CreateAsset` 或 path mutation API 时产生 `assetdb.context_mismatch`，不按 identity 自动重绑定。
   - editor context 关闭后旧 object/path handle 调用 AssetDatabase API 产生 `assetdb.context_closed` 或 stale diagnostic，不污染新的 default context。
   - `AddObjectToAsset(transientChild, parentPath)` 只能写入 schema 声明的唯一兼容 `sub_asset` child slot；没有 slot 或多个兼容 slot 时分别产生 `assetdb.sub_asset_not_supported` / `assetdb.sub_asset_mapping_ambiguous`。
   - `AddObjectToAsset` 对已 stable 的 asset、其他 parent 的 sub-asset、runtime object 或 Unity external resource object 产生 `assetdb.object_state_invalid`，不把已存盘对象搬到另一个 row。
   - Add 成功后 child 进入 `temporary_draft`，预分配最终 row guid/local id 和 local file id；`LoadAllAssetsAtPath(parentPath)` 返回 parent + draft child，`GetAssetPath(child)` 返回 parent path。
   - `RemoveObjectFromAsset(subAsset)` 只创建 child delete/detach draft marker；required/min count/reference restrict blocker 时不创建 partial draft。
   - `SetMainObject(child, parentPath)` 默认产生 `assetdb.main_object_fixed`；current main object 传入时只产生 no-op report，不改变 source_hash。
10. 定义 SerializedProperty path / cell mapping 测试：
   - canonical property path grammar 只接受 schema field segment + `.Array.data[index]`；`skills[0]`、负数、前导零、display name、C# PascalCase member name 都不能作为 canonical path。
   - field segment 中出现 `.`、`[`、`]`、slash、空白、控制字符或保留 segment `Array` / `data` 时，schema-lint 必须要求 alias/migration 或产生 `schema.property_path_conflict` / `schema.value_shape_invalid`。
   - C# member rename 不改变 `FindProperty` path；schema 字段 `mana_cost` 生成属性 `ManaCost` 时，`FindProperty("mana_cost")` 成功，`FindProperty("ManaCost")` 默认失败。
   - simple struct expanded columns 使用 `parent.child` property path。
   - repeated/list 使用 `Array.data[index]`。
   - single-cell struct 的子 property 修改会重写整个 cell。
   - `GetIterator()` 的 root `depth=-1`；第一次 `NextVisible(true)` 到第一个 visible top-level property。
   - `NextVisible(false)` 跳过当前子树；hidden metadata/source/revision cell 不进入 visible traversal。
   - `hasChildren` 与 `hasVisibleChildren` 能区分 schema child 和 inspector-visible child。
   - `tooltip` 来自 schema/header comment；稳定 repaint 不重新分配 display/path/enum 字符串或数组。
   - `FindProperty`、`GetIterator`、`Copy`、`GetArrayElementAtIndex` warmup 后 0 GC；超过 property/array path 水位线只记录 `performance.watermark_grew`。
   - `boxedValue` / `managedReferenceValue` 不属于完整目标版 no-GC 主表面；自定义 editor 不依赖它们。
   - `managedReferenceValue` 只能写入 schema-discriminated polymorphic payload 的 generated variant payload object / proxy；未知 variant、payload 字段不匹配或 CLR 类型无法映射时产生 `serialized.type_mismatch` / `validation.variant_unknown` / `validation.variant_payload_invalid`，不创建 pending diff。
   - `managedReferenceFullTypename`、`managedReferenceFieldTypename` 和 `managedReferenceId` 是 Unity-like projection；variant identity 仍按 stable variant id，setter/display 变化不按 CLR 类型名、对象引用或递增 id 判定。
   - variant 切换必须走 Undo、data-loss confirmation、old payload cleanup、SaveAssets preflight 和 hot reload patch；旧 payload 有数据时不能静默丢弃。
   - `ApplyModifiedProperties` 检查 target/source/mapping revision，外部变化时进入 conflict。
   - property diff 使用 field id 和 row guid/local id，不依赖 header 文本或 row number。
   - `Update()` 丢弃未 apply 的本地 property 修改，并重新绑定最新 revision。
   - `ApplyModifiedProperties()` 无修改返回 false，有修改成功进入 dirty 返回 true。
   - enum 使用 schema value id 作为身份，`enumValueIndex` 只是当前显示顺序。
   - `objectReferenceValue` 写入 row guid/local id，不写显示 key。
   - object reference array 的 `DeleteArrayElementAtIndex` 第一次清空非空引用，第二次移除元素。
   - layout refresh / external refresh 后旧 `SerializedProperty` handle 失效，必须重新 `FindProperty`。
   - setter 只修改 pending property buffer；`Update()` 丢弃未 apply 的 pending buffer，`ApplyModifiedProperties()` 才进入 editor draft/undo/dirty。
   - scalar setter 类型不兼容产生 `serialized.type_mismatch`，不做隐式转换。
   - readonly/deprecated/reserved/capability 不允许编辑时产生 `serialized.read_only`，不创建 pending diff。
   - `stringValue = null` 默认是空字符串，不是 missing/null；nullable/default clear 必须走 explicit clear operation 并参与 validation。
   - enum reorder 后旧 `enumValueIndex` 不可复用，必须重新 `Update()`；enum identity 仍按 value id。
   - object reference setter 验证 target context、type/table scope 和 removed/unloaded/stale 状态。
   - `SerializedObject(Object[] targets)` 单目标数组等价于单目标构造；多目标默认按 14.24 构建可编辑 common property surface。目标集合、schema/layout、field policy、drawer 或项目 profile 不兼容时只能只读展示，setter / array edit / Apply 产生 `serialized.multi_object_not_supported`、`serialized.multi_object_incompatible` 或更具体 diagnostic，不能只改第一个 target。
   - multi-object common property set 只包含 field id、value shape、reference policy、validator、readonly/export policy 一致的字段；不兼容 target 产生 `serialized.multi_object_incompatible`。
   - `hasMultipleDifferentValues` 使用 canonical state + canonical value hash 判定；display text、Excel formatting、localized preview 或 UnityResourceRef main path display 变化不算 mixed。
   - multi-object setter 默认向所有 target 写同一 canonical pending value；mixed overwrite 被字段 policy 禁止时产生 `serialized.mixed_value_unsupported`。
   - multi-object array edit 只有 same-shape + array policy 允许时开放；child table element identity 按 target 独立，不能复制第一个 target 的 child identity。
   - multi-object `ApplyModifiedProperties` 默认 all-or-nothing，任一 target revision/conflict/validation/reference blocker 都不写任何 editor draft，并输出 per-target conflict/report。
   - 用其他 editor context 或 runtime context 的 target 创建 `SerializedObject` 产生 `serialized.context_mismatch`。
   - repeated scalar 无 element identity 时 insert/delete/reorder 与外部同 array 修改冲突。
   - child table backed array insert 创建 temporary child identity，SaveAssets 后 flush stable identity；delete 不复用 element identity。
   - `MoveArrayElement` 产生 reorder diff，不伪装成 delete + insert。
   - `MoveArrayElement` 遵守 `array_edit_policy`：stable element identity 只改 order index，whole-array rewrite 产生 field-level reorder diff，forbidden 不改变 pending buffer。
   - generic repeated/list drawer 对 child table 默认显示 reorder；对 whole-array rewrite 默认隐藏拖拽 reorder，除非 schema opt-in；对 forbidden 不显示 reorder。
   - custom editor 需要 stable reorder 但 schema 只有 whole-array rewrite 时进入 degraded/read_only/table_view，不用 delete+insert 伪装。
   - array API 越界、非 array 调用、read-only array 修改产生 `serialized.array_edit_invalid` 或 `serialized.read_only`。
   - child table array 按 order index 映射到 `Array.data[index]`。
   - child table insert 创建 temporary element identity，save 后 flush stable identity。
   - child table delete 标记 child row deletion，不重写 parent row。
   - child table reorder 只更新 order index，不改变 element identity。
   - `LoadAllAssetsAtPath(parentPath)` 返回 parent 后按 schema field order / order index 返回 sub_asset children。
   - custom editor 打开前执行 capability check；缺 required field 时进入 migration_required/read_only/table_view/reject_open。
   - custom editor 所有写入仍走 `SerializedObject`，不得直接写 workbook cell 或 runtime field。
   - drawer 选择优先级是 schema explicit drawer id > reference family drawer > value shape default drawer > generic fallback。
   - required custom drawer 缺失时产生 editor capability error；普通 drawer 缺失时使用 generic fallback。
   - custom editor multi-object edit 不能半开放；generic multi-select 在 common property scope、diff/merge、report 和 all-or-nothing policy 未通过时只能 read-only/table_view，写入稳定失败并记录 diagnostic。
11. 定义 reference policy 测试：
   - 默认 hard / non-null / restrict / no duplicate list。
   - 用户手填 key/path 后 import 解析并写入 target row guid/local id。
   - 跨 workbook 引用 metadata 保存 target workbook guid + table id + row guid/local id。
   - 用户只填 key/path 且 target scope 内有多个匹配目标时，报 `reference.ambiguous_target`，不按加载顺序猜。
   - schema 可将 target scope 收紧到 same table、same workbook、specific workbook alias、specific table/group。
   - visible key/path 与 metadata identity 冲突时以 metadata 为准并报 `reference.display_mismatch`，不自动重绑定。
   - explicit picker / rebind command 才能更新 hidden identity，并产生 property_changed + dependency_changed。
   - delete restrict 阻止删除并列出 referrer asset identity、field id、cell location。
   - nullable + set_null、ownership + cascade、soft + leave_missing 只在 schema 显式声明时启用。
   - DeleteAsset 先生成 delete plan / editor draft marker，不直接写 workbook；重复 DeleteAsset 同一 target 幂等。
   - delete plan 输出 delete roots、cascade closure、set_null patches、leave_missing referrers、restrict blockers、affected workbook set、dependency closure digest 和 plan hash。
   - restrict 命中 hard referrer 时不创建 delete marker，也不创建 partial set_null/cascade diff。
   - cascade closure 只沿 ownership edge，遇到 ownership cycle、shared ownership、多 parent ownership 或 closure 外 hard referrer 时整个 plan blocker。
   - set_null 对 nullable scalar reference 写 explicit_null；对 reference list 默认删除对应 element，除非 `nullable_element=true`。
   - set_null/cascade 影响跨 workbook referrer 时，相关 workbook 进入同一 affected SaveAssets set，任一 blocker 不写任何 workbook。
   - SaveAssets 前重新计算 delete plan；target/referrer source revision、reference descriptor hash 或 reverse dependency digest 改变时产生 `transaction.plan_stale`。
   - SaveAssets 后复读验证 delete root/cascade closure 缺失、set_null 已写入、leave_missing identity snapshot 保留，才清理 delete draft。
   - runtime hot reload 对 delete plan 发布 removed、property_changed、dependency_changed 和 source_summary 的组合，removed object 不额外发布 property_changed。
   - leave_missing soft reference 保留 target identity snapshot，runtime 暴露 missing state，不伪装成 null。
   - key rename 不破坏引用，reference changed 更新 dependency graph 和 reverse dependency index。
12. 定义 converted bytes header / runtime index：
   - magic、format_version、schema_hash、source_hash、build_target、endianness、flags、file_size、section_directory、content_checksum。
   - hash 默认使用 SHA-256 over canonical bytes；report 中使用 lowercase hex。
   - runtime-required section 使用 fixed little-endian binary record；hash/guid 使用 binary bytes，字符串/path 只在 string table/debug/manifest 中出现。
   - section directory entry 包含 kind/version/flags/offset/length/element_count/element_size/section_checksum。
   - schema_manifest、string table、blob table、table directory、object data、identity/key/reference/dependency/reverse dependency index。
   - generated accessor manifest 记录 field id -> runtime field offset / string id slot / range slot / reference slot，并在 open/verify 时校验 descriptor hash。
   - v1 必需 section 缺失、重复、offset 越界、length 越界、section overlap、alignment 错误都是 open/verify blocker。
   - schema_manifest 保存 descriptor_hash、schema_hash、layout_hash、export_view_id/hash、source_hash、source_set_hash、build target/profile、tool version、table/field layout。
   - table_layouts / field_layouts 记录 object_record_size、storage_kind、state_bit_index、fixed offset、slot/range kind，并与 generated binding manifest 校验一致。
   - converted bytes header 区分 `full_source` 和 `patch_layer`；patch layer 必须有 layer_manifest 和 patch_operations。
   - segmented bytes 有 segment_directory、load_set_index、LoadSetDescriptor、SegmentDescriptor 和 load_set_manifest_hash。
   - LoadSetDescriptor root selector 在 convert/verify 阶段展开为 canonical object/segment/dependency index；runtime `LoadSet` 不重新解释字符串 selector。
   - `all/table/schema_group/asset_path_prefix/explicit_assets/dependency_closure` selector 都按 stable id/canonical path/dependency edge 展开，不依赖 display name、sheet name、Excel 行号、本机文件顺序或 DFS/BFS 偶然顺序。
   - root selector 展开为空默认产生 `bytes.load_set_invalid`；只有 `allow_empty=true` 的工具/debug load set 可以为空。
   - ownership child 默认随 owner root 进入同一 load set；跨 load set boundary 必须由 schema 明确声明，否则 parent resident child unloaded 的 getter 返回 not loaded，不返回 missing。
   - 同一 asset 被多个 load set 覆盖时只有一个 resident wrapper/payload binding；加载第二个 load set 只增加 residency ref，不复制 object。
   - 重叠 load set 中同一 object payload hash/layout 不一致时产生 `bytes.load_set_invalid` / `bytes.segment_mismatch`，不能以后加载者覆盖先加载者。
   - ExportViewDescriptor 从 schema + convert_profile + build_target 归一化生成，client/server/editor/development/release 视图有稳定 export_view_hash。
   - key/reference identity 必需字段被 strip 时产生 `schema.export_policy_invalid`；hard reference 指向被 strip row/table 时产生 `convert.reference_stripped_target`。
   - ExcelDataSource development hot reload 和 ConvertedBytesDataSource 必须使用同一 export view 构建 candidate；export_view_hash 不一致不能判定为 no-op。
   - row guid/local id -> object slot。
   - key scope + table id + canonical key hash -> object slot。
   - key hash collision 必须通过 key string id / canonical key bytes 二次确认；duplicate/collision entry 不能返回错误 asset。
   - table id -> contiguous range。
   - parent object -> child range index，child objects 按 parent slot + order index contiguous 写入。
   - dependency graph 包含 parent -> owned child edge。
   - v1 默认 little-endian，不支持时 reader/open/switch 失败并保留旧 source。
   - string table 使用 UTF-8，按 exact bytes 去重，并按 bytes ordinal deterministic 分配 string id。
   - string table UTF-8、offset/length、string id 顺序必须被 `verify-bytes` 校验。
   - converted bytes reader 默认读取 string id / string view，不在字段读取时创建新 string。
   - public string getter 如果用于 runtime hot path，必须在 open/prewarm/candidate build 阶段绑定 stable string pool entry，重复读取不分配。
   - repeated scalar、child table、dependency edge 使用 contiguous range + count，遍历不创建临时 list。
   - value_range、child_range、map_range、union_payload 的 start/count、variant id、element layout、canonical key order 和 payload range 由 `verify-bytes` 校验。
   - preload_plan_index 只有 profile 声明预计算计划时输出；range/hash/digest 必须由 `verify-bytes` 校验。
   - repeated/child runtime getter 使用 count + indexer / `ReadOnlySpan<T>` / struct range view，不默认暴露热路径 `List<T>` / allocating `IEnumerable<T>`。
   - load set 未加载时 `LoadAsset/TryGetAsset` 不隐式加载，返回 not-loaded 状态并报告 `runtime.load_set_not_loaded`。
   - `LoadSet/PrewarmLoadSet/UnloadSet` 是 operation，能校验 segment checksum、更新 residency、记录水位线增长。
   - `LoadSet` all-or-nothing；任一 required segment/provider lease/capacity/schema/source revision blocker 时旧 residency 不变。
   - `UnloadSet` 不发布 removed；被卸载 object 进入 unloaded，identity/key/dependency core index 保留。
   - `UnloadSet(A)` 在 object 仍被其他 loaded load set、snapshot、ChangeSet 或 provider handle 引用时产生 `runtime.load_set_unload_blocked` 或只清 A 的 residency bit，不释放共享 payload。
   - object record 区分 missing、explicit null、explicit value、default materialized。
   - `ConvertedBytesDataSource(byte[])` 默认 safe copy；`CopyFrom` 复制临时输入；`TakeOwnership` / `BorrowImmutable` 不复制但必须记录 ownership mode、bytes_hash、content_checksum 和 lifetime generation。
   - `TakeOwnership` / `BorrowImmutable` 输入在 open/switch/load-set/refresh/debug safety check 中被检测到变化时产生 `bytes.source_mutated`，不发布新的 ChangeSet；旧 source 可用时保留旧 source。
   - 同一 descriptor、metadata、exported data 在不同机器输出 byte-for-byte 一致。
   - source_hash 不包含样式、批注、列宽、筛选、helper/freeform、生成时间、本机路径。
   - source_hash 使用 canonical value；公式 cached value 被允许时同时包含 formula text、cached value 和 calc state。
   - row metadata 未 flush、duplicate row guid、duplicate key、hard reference missing、required runtime field missing 都是 convert blocker。
   - deterministic output，不依赖字典顺序、当前时间、本机路径。
13. 定义 mode / mutability 矩阵测试：
   - Release 下 ExcelDataSource、writeback、watcher/dev hot reload、metadata repair 明确失败；signed converted source/layer activation 只通过显式 source switch / refresh 测试。
   - Development build 下 hot reload/source switch 需要显式 opt-in。
   - Editor Play Mode 默认不把 runtime 修改写回 Excel。
   - mode 状态可查询并出现在 report 中。
   - OperationProfile 归一化顺序固定：built-in default、project descriptor、named profile、host/build overlay、explicit override。
   - CLI/API override 必须并入 canonical profile；相同 canonical profile 生成相同 operation_profile_hash。
   - `ProjectPolicyDescriptor` canonical JSON 不含本机路径、当前用户、当前时间或 Unity Library path；同一内容换机器 hash 不变。
   - profile 字段按声明的 merge kind 合并；未知字段、继承环、同优先级冲突、非法 map/list merge 产生 `command.profile_invalid`。
   - CLI/API 覆盖非 allowlist 字段、schema semantic descriptor、field id、value shape、reference family、validator/codec/migration id 产生 `command.profile_override_forbidden`。
   - hard invariant 不能被 override 放宽：Release 禁止 Excel source/writeback/dynamic migration/UnityEditor AssetDatabase，CI 禁止交互确认，benchmark_no_gc 禁止降低 allocation policy。
   - gate result report 可以用新的 gate_policy_hash 重判定旧 report，但不能改写原 report_hash。
   - suppression 只改变 effective_severity，不能删除原 diagnostic；不可 suppressible 或 affects convert/runtime/writeback 的 blocker 不能降级。
   - gate-only policy 不进入 bytes cache key；输出相关 convert_profile 进入 convert_profile_hash 和 cache_key_hash。
   - profile hash mismatch 产生 `command.profile_hash_mismatch`；涉及 manifest/bytes/runtime open 时保留旧 source 或拒绝输出。
   - Release profile 默认 require manifest、禁止 Excel source、禁止 dynamic migration 和 UnityEditor AssetDatabase。
14. 定义 RuntimeDatabase / DataSource facade 测试：
   - `Open` 成功返回 true，失败返回 false 且 context 保持 unopened。
   - `RuntimeDatabaseContext` 暴露与 `RuntimeDatabase` 等价的实例 API，除了 `defaultContext/currentContext/BindContext`；返回值、report、allocation policy、threading、event 语义一致。
   - static `RuntimeDatabase` 在 `BindContext` scope 内委托到 current context；scope 外恢复 default context。
   - context 实例事件只接收该 context 的 ChangeSet；static `RuntimeDatabase.changed/objectChanged` 不跨 context 广播。
   - static event 订阅/退订按调用时 current context 绑定；切换 `BindContext` 不迁移已有 handler。
   - 两个 `RuntimeDatabaseContext` 可以同时打开不同 source；static facade 在 `BindContext` scope 内只影响当前调用流。
   - `Close` 清空 currentSource/index/dependency graph，resident instances 进入 unloaded；随后读取、snapshot acquire 和 load set operation 返回 `runtime.source_not_open`，同一 context 仍可重新 `Open`。
   - `RuntimeDatabaseContext.Dispose` 后读取、snapshot acquire、source switch 和 load set operation 产生 `runtime.context_closed`，不隐式创建新 context。
   - `SwitchDataSource` 失败时保留旧 source、旧 object graph 和旧 indexes。
   - `Refresh` 无变化、hot reload 关闭或 source 不支持 refresh 时返回 false 并写最小 report。
   - `ExcelDataSource(IEnumerable<string>)` 打开 workbook source set，并 deterministic 组合 source identity。
   - `ExcelDataSource(IEnumerable<string>)` 必须先生成 SourceSetDescriptor；同一 workbook set 不受传入路径顺序影响。
   - `CompositeDataSource` 按 SourceLayerDescriptor 构建 materialized view；稳定读取不沿 layer 链查找。
   - public `DataSource` object 作为 immutable descriptor 可被两个 context 重复 open；两个 context 的 opened source session、revision、watcher、snapshot pin、report 互相隔离。
   - `currentSource` 返回 descriptor projection，不暴露 provider lease；调用方不能通过修改/释放 descriptor 改变已打开 session。
   - open 失败或 switch candidate 被拒绝时，candidate provider lease / memory map / reader session 被释放，旧 session 仍保持 current。
   - switch 成功后旧 session retired；active snapshot 持有旧 immutable sections 时继续可读，最后一个 snapshot Dispose 后释放旧 provider lease。
   - `Close` 后 active snapshot 仍能读 close 前 revision；新 `TryAcquireSnapshot` 返回 false + `runtime.source_not_open`。
   - `Dispose` 后 active snapshot 仍按 pin 规则延迟释放底层 immutable sections，但任何 owner API 和新 snapshot acquire 返回 `runtime.context_closed`。
   - layer stack 没有唯一 base、layer order 不稳定或同 priority 覆盖同 diff key 时产生 source layer diagnostic，保留旧 source。
   - patch layer target base/source hash 不匹配时产生 `source.layer_base_mismatch`，不应用补丁。
   - patch operation digest 按 canonical operation bytes 和 stable sort 计算；同一 operation 文件物理顺序变化不改变 digest、layer_stack_hash 或 materialized_source_hash。
   - 同一 layer 内两个 active operation 命中同一 diff_key 且无法按显式 merge policy 合并时产生 `source.overlay_conflict`，不能以后者覆盖前者。
   - 高层 patch 的 `base_value_hash` 默认对比低层 materialized value；显式绑定原始 base source 时必须使用 target base hash，错配产生 `source.layer_base_mismatch`。
   - tombstone 遮蔽低层同 identity row；更高层恢复必须使用 `restore_row` / `replace_row` 且匹配 base tombstone hash，否则保留旧 source。
   - `required=false` patch 只在当前 export/profile strip 或禁用目标时可忽略；base/hash/signature/identity mismatch 仍失败。
   - layer activation assignment id/input hash/result 变化时走 source switch candidate；commit 前旧 layer stack 继续服务。
   - Release 下 Excel layer、development override、unsigned required patch 都明确失败。
   - 跨 workbook hard reference 存在时，source set candidate 任一 blocker 使整个 runtime hot reload 不替换 object graph。
   - Release mode 下 `ExcelDataSource` 和 `EnableHotReload` 明确失败或 no-op + report。
   - `LoadAsset<T>` 找不到、类型不匹配、asset missing 时返回 null。
   - `TryGetAssetKey<T>` 在初始化期把 key/path 解析为 `AssetKey`。
   - A context 创建的 `AssetKey`、load set handle、snapshot 或 resident object 传入 B context 查询时产生 `runtime.context_mismatch`。
   - `TryGetAssetKey<T>` 只有唯一有效 key/path 命中时返回 true；duplicate、stale path、missing、type mismatch 返回 false 且不创建临时 object。
   - `TryGetAsset<T>(AssetKey)`、`TryGetAsset<T>(AssetIdentity)` 和 `GetAssets<T>(Span<T>)` 不产生 GC allocation。
   - `GetDependencies(AssetIdentity, Span<DependencyTarget>)` 和 recursive `DependencyQuery` 不产生 GC allocation；buffer 不足时返回 `RuntimeQueryStatus.Truncated`，不为 truncation 分配 report/list/string。
   - `TryGetLoadSetId` 在初始化期解析 load set handle；`LoadSet` / `PrewarmLoadSet` 后稳定读取不触发隐式 segment load。
   - `GetLoadSetStatus` 只查询当前 session residency bitset，不触发 provider IO、文件 stat、checksum 或隐式加载。
   - 重叠 load set A/B 同时 loaded 时，`UnloadSet(A)` 不让仍被 B 覆盖的 object 进入 unloaded，也不释放共享 segment/provider lease。
   - `UnloadSet` 遇到 pinned wrapper、active RuntimeSnapshot、active ChangeSet、provider handle 或 policy 禁止时产生 `runtime.load_set_unload_blocked`，不半卸载。
   - source switch 后仍存在的 `AssetKey` 能重绑定到同一 identity；目标删除时返回 false 并有 ChangeSet/report。
   - path-based converted bytes 更新后 `Refresh` 能按 source switch 语义 patch。
   - memory bytes source 的 `Refresh` 默认 no-op，必须通过 `SwitchDataSource` 切入新 bytes。
   - memory bytes source 的 `Copy` 模式下，open 后修改原始数组不影响当前 source；`TakeOwnership` / `BorrowImmutable` 模式下 safety check 发现输入变化产生 `bytes.source_mutated`。
   - source switch / hot reload commit 先生成 patch plan，commit 前验证 current source revision、schema hash、identity map 和 registry manifest 仍匹配。
   - patch plan stale 产生 `runtime.patch_plan_stale`，保留旧 source 和旧 object graph。
   - patch existing resident object 失败产生 `runtime.patch_failed`，必须 rollback staged field/index changes，不发布成功 ChangeSet。
   - event buffer 超过水位线产生 `runtime.event_buffer_overflow` 或 allocation summary，不能静默丢事件。
   - `changed` 发布完整 ChangeSet，`objectChanged` 只是便利事件且不表达 removed/dependency closure。
   - `ChangeSet.events` 使用 non-alloc `ChangeEventList`，支持 `Count`、`ref readonly` indexer 和 struct enumerator；不能给 subscriber 分配数组/List。
   - `GetChangedFields`、`GetChangedPropertyPaths`、`GetChangedDependencies` 和 `GetAffectedAssets` 写入 caller Span；buffer 不足返回 truncated 和 required count，不分配临时集合。
   - 跨 context、跨 revision 或回调结束后复用 `ChangeEvent` handle 返回 `RuntimeQueryStatus.InvalidHandle` 或 debug assertion，不隐式重绑定、不分配 report。
   - watcher refresh 进入 owner operation queue，不能从 watcher/background 线程直接 commit。
   - `ChangeSet.events` non-alloc view 只在 callback 栈内有效；跨帧保存必须复制 stable identity/code。
   - subscriber 抛异常产生 `runtime.subscriber_failed` report，不回滚已提交 source/object/index，默认继续通知其他 subscriber。
   - subscriber dispatch 顺序按 stable subscription sequence；回调中订阅/退订只影响下一次 ChangeSet。
   - `changed` 回调中 `LoadAsset/TryGetAsset/GetDependencies/TryAcquireSnapshot` 读取 `revisionAfter`，旧 revision 只能通过已持有 snapshot 验证。
   - 回调中同步嵌套 `Refresh/SwitchDataSource/Open/Close` 产生 `runtime.publish_reentrant` 或进入 owner queue，不改变当前事件序列。
   - 第一个 subscriber 抛异常后，后续 subscriber 仍按顺序收到同一 ChangeSet；report 记录 subscriber id、revision 和 exception category。
   - `objectChanged` 在 `changed` 之后由同一 ChangeSet 派生；不为 removed/moved/dependency/source_summary 提供完整替代事件。
   - `TryAcquireSnapshot` 返回 immutable generated read view；非 owner 线程读取 snapshot 不访问 resident `Object`。
   - source switch 前 acquire 的 snapshot 在 switch 后仍读旧 revision；switch 后新 acquire 的 snapshot 读新 revision，Dispose 后释放 pin。
   - active snapshot pin 阻止相关 load set unload，产生 `runtime.load_set_unload_blocked`，不会半卸载 segment。
   - 请求 JobSafe/BurstSafe 但 schema/provider 不支持时 acquire 失败并产生 `runtime.snapshot_capability_unsupported`，不能降级成 owner object 读取。
   - 非 owner 线程调用 owner-only `LoadAsset/TryGetAsset/Open/Switch/Refresh/Close` 产生 `runtime.thread_violation` 或 debug assertion。
   - `Reserve` / `Prewarm` 后 runtime 热路径不产生 GC allocation。
15. 定义 dirty / undo / save / conflict 状态机测试：
   - `ApplyModifiedProperties` 后进入 dirty_editor。
   - 新行临时 identity 后进入 dirty_metadata。
   - `Undo.RecordObject` 后没有 canonical change 时不入栈；有 change 且 `SetDirty` 成功时形成可 undo diff。
   - `Undo.RecordObjects` / `RegisterCompleteObjectUndo(Object[])` 任一 target invalid/cross-context/stale 时 all-or-nothing 失败，产生 `undo.record_scope_invalid`。
   - `RegisterCompleteObjectUndo` 能回退 variant 切换 / whole-array rewrite 的完整 schema state，但仍不绕过 validation/save preflight。
   - `IncrementCurrentGroup`、`SetCurrentGroupName`、`CollapseUndoOperations` 只改变 undo grouping/menu 展示，不改变 draft diff 和 SaveAssets affected set。
   - `PerformUndo` / `PerformRedo` 只改 editor draft、dirty/status/projection index；不写 workbook、不触发 runtime ChangeSet。
   - undo/redo 空栈产生 `undo.stack_empty` no-op；undo/redo 处理中重入保存/刷新/再次 undo 产生 `undo.processing_reentrant` 或排入 safe point。
   - `RevertAllInCurrentGroup` / `RevertAllDownToGroup` 不创建 redo entry，且不清无关 dirty/conflict。
   - `ClearUndo(obj)` / `ClearAll()` 只清 undo/redo stack，不清 dirty draft、conflict 或 workbook。
   - SaveAssets 成功后执行 Undo 会把 editor draft 改回旧值并重新 dirty；不能回滚已写入 Excel 的历史文件。
   - editor 和 Excel 修改不同 property 时自动 merge。
   - editor 和 Excel 修改同一 property 时进入 conflict。
   - conflict_id 按 workbook/table/row/diff_key/revision/reason_code deterministic 生成，report 排序不依赖发现顺序。
   - conflict record 包含 conflict_state、conflict_kind、diff_key、base/editor/external revision、mapping revision、reason_code、suggested_actions。
   - `AssetDatabase.GetConflicts(Span<ConflictRecord>)` 枚举 active conflict，不为结果创建临时数组；buffer 不足返回 truncated 和 required count。
   - `TryGetConflict` 对跨 context、已清理或不存在的 conflict 返回 false，并写 `transaction.conflict_not_found` 或 context diagnostic。
   - enum 按 value id 判断冲突，object reference 按 row guid/local id 判断冲突。
   - expanded struct 不同 child field 可以自动 merge。
   - single-cell struct 不同 child property 默认冲突，除非 schema codec 支持 child-level lossless merge。
   - repeated/list 无 stable element identity 时，insert/delete/reorder 与同 array 其他修改冲突。
   - editor 修改 row 而 Excel 删除 row 时进入 conflict。
   - editor 删除 row 而 Excel 修改 row 时进入 conflict。
   - editor 和 Excel 同时新建相同 key 但不同 identity 时进入 duplicate key conflict。
   - `reload from Excel` 丢弃对应 diff 并以 external snapshot rebase。
   - `keep editor value` 以 external snapshot 为新 base 重新写入 editor draft，进入 resolved_pending_save。
   - `ResolveConflict(id, ReloadFromExcel/KeepEditorValue)` 只改变 editor draft/conflict state，不保存 Excel。
   - `TryOpenConflictResolver` 创建 conflict-local `SerializedObject`，`Apply()` 后写入 manual resolved draft。
   - `manual resolve` 的 final value 必须重新 parse/normalize/validate 后写入 draft。
   - resolver 打开后 source/row/mapping/schema/layout revision 改变时，旧 resolution 变 stale，产生 `transaction.conflict_resolution_stale`，不能写入 draft。
   - conflict report 包含 conflict_id、asset_identity、field_id、property_path、base/editor/external value 和 cell location。
   - unresolved conflict 时 `SaveAssets` 拒绝保存。
   - stale conflict 或 resolved_pending_save 的 base revision 过期时，`SaveAssets` 必须重新 merge 或拒绝保存。
   - `SaveAssets` 对所有 affected workbook 做 preflight，任一 blocker 默认不写任何 workbook。
   - `SaveAssets` / migration apply / materialize defaults apply / auto-fix apply / diagnostic writeback 在写 workbook 前必须取得 workbook write lease；lease acquire 失败时不写 temp workbook，并产生 `transaction.preflight_failed` 或更具体 diagnostic。
   - 多 workbook affected set 按 deterministic workbook order acquire write lease；任一 lease 不可取得时释放已取得 lease，整个 all-or-nothing set 不写任何 workbook。
   - write lease 取得后 source revision、fingerprint、schema/layout/profile hash 任一变化时产生 `transaction.source_revision_changed` 或 stale plan，不写 workbook。
   - 遗留 write lease 但无 replace-in-progress recovery manifest 且 owner 已失效时，可以清理 abandoned lease；清理必须进入 side-effect ledger，且下一次写入重新 Analyze。
   - 遗留 write lease 伴随 replace-in-progress recovery manifest 时，必须先走 recovery verify/restore/manual recovery；不能只删除 lease 后继续保存。
   - SaveAssets / LayoutRefresh / MigrationApply / ForceReserializeAssets 从 package patch plan 推导 `required_backend_features[]`，并与 `XlsxBackendCapabilityDescriptor` 比对；能力不足产生 `save.unsupported_xlsx_feature`，不写 temp workbook。
   - xlsx backend capability descriptor hash 进入 operation plan/report；dry-run 和 apply 使用不同 backend 或 backend hash 改变时产生 stale plan，必须重新 dry-run。
   - `zip_copy_through=rewrites_all_entries` 的 backend 遇到 VBA、signature、custom XML、drawing/chart/pivot、unknown part 或用户 comment/style 时默认保存失败，不能整包重写。
   - `content_hash_preserving` backend 保存后必须证明未声明 changed entry 的 uncompressed bytes、CRC/content hash、content type 和 relationship target 不变。
   - `xml_patch_model=whole_part_rewrite` 只有 worksheet part 完全 ExcelDB-owned 或非目标 nodes canonical-equivalent 时可用；普通策划 sheet 不能用 whole-part rewrite 冒充 cell patch。
   - `SaveAssets` 在 dirty/metadata/side-effect closure 收集后调用 `AssetModificationProcessor.OnWillSaveAssets`；processor 返回输入子集时，被移除 path 保持 dirty 并写 skipped report。
   - `OnWillSaveAssets` 返回新 path、stale path、unknown path 或跨 context path 时产生 `assetdb.modification_processor_failed`，不写 workbook。
   - `OnWillSaveAssets` 返回子集拆掉 owned child、metadata flush、cascade/set_null side effect、resolved conflict diff 或不可隔离 patch closure 时，本次 save 失败并保留全部相关 dirty draft。
   - `SaveAssetIfDirty(Object/GUID)` 不触发 `OnWillSaveAssets`，但仍遵守各自 create/move/delete 阶段的 modification processor 语义。
   - affected workbook set 包含跨 workbook hard reference、cascade/set_null delete、owned child table、resolved conflict draft、metadata repair 和 auto-fix proposal。
   - save partial failure 保留失败 workbook 的 dirty draft，并在 report 中列出 saved/skipped/failed/restored。
   - source set 复读验证通过后，resolved_pending_save 才能进入 resolved_saved 并清理 draft。
   - `SaveAssetIfDirty(obj)` 只保存目标 asset closure 和必要 side effect；同 workbook 内 unrelated dirty row 保持 dirty，且不会写入 workbook。
   - targeted save 在 xlsx backend 无法隔离目标 dirty diff、保留未知 package part 或保护非目标 dirty cell 时失败，并保留全部 dirty draft。
   - `SaveAssetIfDirty(GUID)` 只按 current guid index 解析；missing、stale、`temporary_imported` 或未 mounted guid 不扫描磁盘、不猜 path。
   - targeted save 成功后必须 critical reimport committed workbook，并只清除已提交 closure 的 dirty/conflict/resolved_pending_save 状态。
   - targeted save 因 delete/cascade/set_null/reference policy 扩展 affected workbook set 时，必须验证扩展 closure digest；无法隔离 unrelated dirty side effect 时失败。
   - `ForceReserializeAssets(assetPaths, ReserializeAssets)` 对选定 asset closure 做 canonical physical rewrite，semantic source_hash/value hash 不变，row/source revision 按实际触碰记录更新。
   - `ForceReserializeAssets(assetPaths, ReserializeMetadata)` 只重写 metadata/helper identity/checksum，不 materialize default、不清理 deprecated layout、不改变业务 value。
   - `ForceReserializeAssets()` 支持 mounted workbook 全量选择，但遇到 unresolved conflict、dirty draft、package preservation 风险或 unsupported xlsx feature 时默认失败并写 report。
   - reserialize 后必须重新 import 验证 identity/key/reference/dependency digest；no-op rewrite 不发布 property_changed，只允许 source_summary/revision/report 更新。
   - processor 回调内重入 `AssetDatabase` / `Undo` / `SerializedObject.ApplyModifiedProperties` / save / refresh 产生 `assetdb.modification_processor_reentrant`，外层 operation 不提交半成品。
   - `Undo` 只回退 editor draft，不回退外部 Excel 保存。
   - `ApplyModifiedProperties` 形成 undoable edit unit；undo/redo 不直接写 workbook。
16. 定义 workbook version-control / review / merge artifact 测试：
   - `snapshot` 对同一 workbook 在不同机器输出 byte-for-byte 相同 canonical JSON。
   - snapshot 记录 workbook_guid、schema/layout/source hash、metadata checksum、snapshot_scope、row/value/reference canonical records 和 snapshot_hash。
   - value record 记录 canonical_state、canonical_value_hash 和 source_cell_hash；missing、explicit null、explicit value、default materialized 在 authoring_full snapshot 中不能被折叠。
   - debug preview、cell location 和 canonical_value_bytes 裁剪策略不影响默认 CI/merge snapshot_hash；merge 判定不能从 debug text 反推 value。
   - export_view snapshot 只用于 bytes equivalence/review；除非 project policy 显式允许，不能作为普通 authoring workbook merge base。
   - workbook 改动后未更新 snapshot 时，CI 产生 `vcs.snapshot_stale`。
   - snapshot 缺失且 project policy 要求提交 snapshot 时，产生 `vcs.snapshot_missing`。
   - `diff-workbook` 能输出 row add/delete/key rename/property/reference/dependency/helper changes，且不把 key rename 拆成 delete+add。
   - `merge-workbook` 对不同 row、不同 scalar field、expanded struct 不同 child field 自动合并。
   - `merge-workbook` 对同一 diff key 不同 normalized value 产生 `vcs.merge_conflict`，不写 merged xlsx。
   - `merge-workbook --conflicts-out` 输出 canonical conflict artifact，包含 base/ours/theirs snapshot hash、conflicts[]、reason_code、suggested_actions 和 conflict_artifact_hash。
   - conflict artifact 排序和 hash 在不同机器稳定；value conflict 使用 canonical_state + canonical_value_hash 判定，不受 human markdown、Excel display text 或 debug preview 影响。
   - base snapshot 缺失、不可信或无法映射当前 schema 时产生 `vcs.merge_base_missing`。
   - 两边新增相同 key 但不同 identity 时产生 duplicate key merge conflict，不自动合并 identity。
   - 两边复制出相同 row guid/local id 且 base 不存在时产生 duplicate identity / merge conflict。
   - 一边删除 row、另一边修改同 row 时冲突；两边都删除同 row 时接受删除。
   - schema-owned header/helper generated block 由 layout refresh 再生，不按 xlsx 文本冲突合并。
   - registered helper/freeform region 双边修改按 region merge policy；无 policy 时产生 merge conflict。
   - UnityResourceRef guid 相同但 main_asset_path 不同只刷新 display，不产生 data conflict。
   - merge 成功后重新 import merged xlsx，验证 source_hash、metadata_checksum、identity/key/reference/dependency digest 和 snapshot_hash。
   - lock provider 启用 require policy 时，无 lock 保存/merge/migration 产生 `vcs.lock_required` 或 `vcs.lock_owner_mismatch`。
17. 定义 watcher / refresh / import 调度测试：
   - 同一 Excel 保存触发多次文件事件时只产生一次 import report。
   - Excel `~$` lock/temp 文件和 ExcelDB backup/temp/recovery/write-lease 文件被忽略，并报告 `watcher.ignored_temp_file`。
   - debounce 后至少两次 stat 的 size/write-time 稳定才尝试读取；持续变化产生 `watcher.file_unstable`，保留旧数据。
   - 文件锁定时按 bounded backoff retry，超限后报告 `watcher.file_locked` 并保留旧数据。
   - read-verify 校验 xlsx package、metadata header/checksum；半写入或损坏产生 `watcher.read_verify_failed`。
   - fingerprint 未变化时丢弃 watcher 事件。
   - size/write-time 变化但 source_hash 未变化时只产生 `watcher.fingerprint_noop`，不发布 property_changed。
   - manual `AssetDatabase.Refresh()` flush watcher queue，但仍执行 stability/read-verify，不读半写文件。
   - 同一 debounce 窗口内多个 workbook 变化组成 source set candidate。
   - source summary report 列出 affected workbook guids、paths、source hash before/after。
   - watcher report 包含 watcher_events、ignored_paths、retry_summary、fingerprint_before/after、settle_time_ms。
   - watcher queue overflow 产生 `watcher.queue_overflow`，不会从 watcher/background 线程发布半 candidate；旧 source/object graph 继续服务。
   - 自动 watcher event 溢出时允许 coalesce 或 fallback 到 mounted workbook full rescan；manual Refresh / ImportAsset / SwitchDataSource / Open / Close / SaveAssets 请求不能被丢弃。
   - overflow report 记录 dropped/coalesced count、oldest/newest sequence、affected paths、capacity before/after、full rescan fallback 和 manual refresh required。
   - direct edit classifier 输出 action_id/action_kind/table_id/row_identity/field_id/cell range/value hash，并按 table id、row identity、action kind、field id deterministic 排序。
   - 只改样式、列宽或普通批注分类为 `layout_only_change`，不发布 runtime property_changed。
   - 改普通数据 cell 分类为 `property_changed`；改 key cell 同时分类为 `key_changed`，row identity 不变。
   - Excel 新增缺 metadata 行分类为 `row_added_temporary`，进入 `temporary_imported` preview，不进入 runtime added。
   - Excel 新增带有效 stable metadata 行分类为 `row_added_stable`；duplicate/tombstone/allocator 不合法时转 repair/blocker。
   - 删除 stable row 分类为 `row_deleted`；filter/hidden/sort 不误判为删除。
   - 复制行连同 hidden companion identity 时生成 `identity_repair_candidate` 或 `identity_blocker`，不静默改正式数据。
   - 拖动普通行默认只刷新 current row number；拖动有 order policy 的 child row 产生 `row_order_changed`。
   - UnityResourceRef 只改 main_asset_path 且 guid 不变时分类为 `unity_resource_display_changed`，不改变 runtime_dependency_hash。
   - 手改 hidden companion identity 不会把已有 row 当新 asset；必须进入 metadata repair/blocker。
   - `AssetDatabase.Refresh()` 不绕过 transaction/report。
   - editor dirty 存在时 refresh 走 merge/conflict，不覆盖 draft。
   - `DisallowAutoRefresh` 期间 watcher event 只入队/coalesce，不启动 import、metadata auto flush 或 hot reload publish；`AllowAutoRefresh` outermost 后按 event sequence 处理。
   - `DisallowAutoRefresh` 不压制显式 `Refresh()`、`SaveAssets()` 或 `FlushMetadata()`；显式 Refresh 仍走 read-verify/transaction/report。
   - `StartAssetEditing` 期间自动 watcher refresh 和 explicit Refresh request 延后；outermost `StopAssetEditing` 后先合并 batch draft/index，再处理 manual refresh，再处理自动 watcher refresh。
   - `SaveAssets` 在 asset editing batch 中仍必须对自己写入的 workbook 执行 critical verify/reimport，不能因 batch 激活跳过复读清 dirty。
   - scope 不平衡分别产生 `assetdb.asset_editing_scope_unbalanced` / `assetdb.auto_refresh_scope_unbalanced`，不修改 workbook、不丢 watcher queue。
   - 后台 import 可以构建 candidate/report，但 resident object graph 只在 owner thread publish point patch。
   - 同一帧 publish point 前后 revision 一致，commit 不与 `GetAssets` / dependency traversal 调用交错。
   - operation queue overflow 时 coalesce 重复 refresh，不丢 explicit open/switch/close，并写 diagnostic。
18. 定义 platform adapter / assembly 边界：
   - `ExcelDbEngine`、schema、source、convert pipeline 不引用 UnityEngine / UnityEditor。
   - Unity 2022.3 adapter 是首个 adapter，负责 UnityEditor 接入、UnityResourceRef、asset picker、Play Mode 调数和构建集成。
   - 非 Unity adapter 通过 reference family、source、editor facade、validator、converter extension 扩展。
19. 新建 `ExcelDbEngine`、`ExcelDbEditor`、Unity 2022.3 adapter 的项目或 namespace 边界，并定义 Unity editor projection：
   - Unity adapter 提供 virtual asset index、browser/object picker/inspector bridge；默认不为每行生成持久 `.asset` mirror。
   - 可选 Unity proxy/cache 丢失后能从 workbook metadata 重建；proxy cache 不进入 source_hash、bytes_hash、schema_hash、layout_hash 或 build cache key。
   - Unity proxy 的 `.meta` guid、Unity instance id、Unity local file id 不等于 ExcelDB asset guid/local file id；`TryGetGUIDAndLocalFileIdentifier` 返回的是 ExcelDB 派生 identity。
   - `Selection` / `PingObject` / ObjectField 需要 `UnityEngine.Object` 时使用 proxy；proxy inspector 写入必须转回 ExcelDB `SerializedObject`，不能序列化 proxy 字段作为数据源。
   - ExcelDB `Selection.activeObject/objects/assetGUIDs/instanceIDs` 只表达 editor projection state；设置 selection 不创建 dirty、不写 workbook、不发布 runtime ChangeSet。
   - `Selection.assetGUIDs` 对 sub-asset 返回 owning main asset guid；sub-asset 精确身份必须通过 selected object + local file id 查询。
   - `EditorGUIUtility.PingObject` 定位 browser/proxy，不改变 selection、不打开 Excel、不触发 Refresh。
   - ExcelDB internal reference picker 候选来自 virtual asset index 和 reference descriptor target scope，不扫描 UnityEditor.AssetDatabase。
   - `UnityResourceRef` 字段使用 Unity ObjectField 时只写 `{guid, main_asset_path, optional sub_asset}`；不把 `UnityEngine.Object` 引用保存进 workbook。
   - `AssetDatabase.OpenAsset(obj)` 默认打开 inspector/browser，可选打开 Excel 定位到 workbook/sheet/row/cell；不保存、不刷新、不修复 metadata。
   - Unity import hook 只 enqueue refresh/check/convert request，不在 AssetPostprocessor callback 中 patch resident object 或写 workbook。
   - Unity domain reload 后，从 workbook metadata、descriptor registry、project policy 和可选 proxy cache 重建 default editor context；静态字段不是事实源。
20. 定义 `Object`、`ScriptableObject`、`AssetDatabase` 的默认 / 权威 API surface：
   - `AssetDatabaseContext` 暴露与 `AssetDatabase` 等价的实例 API，除了 `defaultContext/currentContext/BindContext`；返回值、report、Undo/dirty/save、operation transaction 语义一致。
   - static `AssetDatabase` 在 `BindContext` scope 内委托到 current editor context；scope 外恢复 default context。
   - `AssetDatabase` 与 `AssetDatabaseContext` 都暴露 conflict query / resolve / manual resolver API；instance 和 static 结果、report、context 绑定一致。
   - `AssetDatabase` 与 `AssetDatabaseContext` 都暴露 `StartAssetEditing` / `StopAssetEditing` / `DisallowAutoRefresh` / `AllowAutoRefresh`；scope counter 绑定当前 context，跨 context start/stop 产生 context diagnostic。
   - `AssetDatabase.GetDependencies(path)` 等价 recursive=true，包含输入 path；`GetDependencies(path, false)` 只返回 direct dependencies 且不包含输入 path。
   - `GetDependencies(string[])` 多输入按输入顺序保留 recursive self paths，其余依赖去重并 deterministic 排序。
   - editor `GetDependencies` 是 dependency graph 的 path projection：internal asset 返回 ExcelDB current path，UnityResourceRef 返回 main_asset_path / guid path projection，localized/external opaque target 默认省略并写 skipped summary。
   - `GetDependencies` query 不触发 Refresh/import/validation/provider discovery/object materialization；stale/missing/temporary_imported path 返回空数组并写现有 diagnostic。
   - `GetAssetDependencyHash(path/GUID)` 返回 editor/build projection aggregate hash；source_hash、runtime_dependency_hash、bytes_hash 不得被误当作该 hash。
   - dependency hash 覆盖 source identity/current path/source authoring hash/schema/import profile/dependency edge digest/recursive closure/provider runtime dependency hash；human report、debug preview、本机路径不影响 hash。
   - `TryGetAssetStatus`、`TryGetWorkbookStatus`、`GetAssetDiagnostics`、`GetWorkbookDiagnostics` 读取当前 status index，不触发 refresh/import/validation。
   - status flags 可组合；dirty、warning、error、convert blocker、conflict 不互相覆盖。
   - invalid raw row、duplicate key loser、`temporary_imported` 行不进入常规 `FindAssets`，但可通过 `GetWorkbookDiagnostics` 定位到 workbook/sheet/row/cell。
   - Span-based status diagnostic enumeration buffer 不足返回 truncated 和 required count，不创建临时数组。
   - `ScriptableObject.CreateInstance<T>()` 创建 `Transient` object；`CreateAsset` 成功后进入 `TemporaryDraft`，SaveAssets 后进入 stable resident/draft source。
   - generated asset class 默认继承 `ExcelDbEngine.ScriptableObject`，不能用业务 `new` 创建有效 resident object。
   - 同一 context 内同一 asset identity + runtime type 重复加载返回 canonical resident instance。
   - `GetInstanceID()` 是 context-local，不写入 workbook / converted bytes。
   - `Equals` / `GetHashCode` 使用 context-local instance id + generation；object removed/stale/unloaded 后 hash 不变，不使用 path/name/field value。
   - `Object.name` getter 来自 display name/key fallback；setter 只在 editor authoring direct mutation overlay 中有效。
   - `Object.hideFlags` 是 context-local presentation flag，不写 workbook、不进入 converted bytes、不改变 SaveAssets/convert/source_hash；`DontSave` 不能阻止 schema-backed dirty 保存。
   - generated public setter 只写 direct mutation overlay；未执行 `SetDirty` 不进入 editor draft、SaveAssets、convert 或 runtime index。
   - `EditorUtility.SetDirty` 把 direct mutation overlay 转为 canonical property diff；无法映射字段/cell 时产生 `assetdb.object_mutation_not_mappable`。
   - runtime read-only profile 下 generated setter 不存在或失败；不得创建 dirty、不得改变 resident source。
   - `obj == null` 对 actual null 和 `isValid=false` object 为 true；`ReferenceEquals(obj, null)` 只表示 actual null。
   - removed、stale_recreated、unloaded、context_closed object 的 `isValid=false`，不能读取 payload field 或被跨 context 复用。
   - key/path/name 变化不 recreate object，只发 moved/renamed。
   - 字段变化 patch 原 instance，并发 property_changed。
   - 无法 patch 时发 recreated，旧 instance 进入 stale_recreated，新 load 返回新 instance。
   - row 删除时发 removed，旧 instance 进入 removed，不能静默指向新行。
   - 同一 object 多字段变化 coalesce 为一个 property_changed event，field_ids 按 schema order 排序。
   - reference 变化产生 dependency_changed，并能通过 reverse dependency index 找到 referrer closure。
   - recreated 不拆成 removed+added；removed 不再额外发布 property_changed。
   - source_hash 相同的 Excel/bytes no-op switch 不发布 property_changed，只允许 source_summary 或无事件。
   - ChangeSet 只在 commit 成功后发布，事件顺序 deterministic。
   - subscriber 异常进入 report，不回滚已 commit 数据。
   - `MountWorkbook` 幂等并归一化 workbook root，不污染已有 context。
   - `UnmountWorkbook` 遇到 dirty/conflict 或 remaining workbook hard reference 时返回 false 并报告原因。
   - `Refresh` 不保存 dirty，外部变化走 import transaction 和 merge/conflict。
   - `FlushMetadata` 是 metadata-only writeback；失败时保留 dirty_metadata/TemporaryIdentity，并写 report。
   - `SaveAssets` 先做所有 dirty workbook preflight；blocker 时不写任何 workbook。
   - `AssetModificationProcessor` 暴露 `OnWillCreateAsset`、`OnWillMoveAsset`、`OnWillDeleteAsset`、`OnWillSaveAssets` 和 `MakeEditable` 对应的 Unity-like optional static message surface。
   - modification processor 按 deterministic registry order 调用；回调拒绝、异常、返回非法路径、声明 side effect 不一致和重入分别写稳定 diagnostic / report。
   - `OnWillSaveAssets` 只由 broad `SaveAssets()` 调用，不能由 processor 返回子集破坏必须同存的 save closure。
   - `SaveAssetIfDirty(Object/GUID)` 是 targeted save：解析当前 identity，保存目标 closure 与必要 side effect；不能把同 workbook 其他 dirty 当作附带保存。
   - `SaveAssetIfDirty` 与 `SaveAssets` 共享 source revision、conflict、metadata、reference、validation、package preservation、backup/temp/verify/reimport 边界；成功后只清目标 closure dirty。
   - `SaveAssetIfDirty(Object/GUID)` 不触发 broad `OnWillSaveAssets`；targeted save 必须产出完整 OperationReport 并保留 unrelated dirty draft。
   - `ForceReserializeAssets` 暴露无参和 path/options 重载；无参选择当前 mounted context 的有效资产，path 可解析 asset、workbook/table/folder prefix。
   - `ForceReserializeAssetsOptions` 区分 data cell canonical rewrite、metadata/helper rewrite 和两者；默认不 materialize defaults、不 cleanup deprecated layout、不迁移业务数据。
   - `ForceReserializeAssets` 不应在 callback/repaint/serialization 期间执行；adapter 必须排入 owner safe point 或拒绝，并写 OperationReport。
   - `AddObjectToAsset` / `RemoveObjectFromAsset` 暴露 Unity-like sub-asset API，但只操作 schema-owned sub_asset child representation；不得创建 ExcelDB 隐藏对象袋。
   - `SetMainObject` 暴露 Unity-like API；ExcelDB source 默认固定 main asset，非 no-op 调用稳定产生 `assetdb.main_object_fixed`。
   - `AssetPathToGUIDOptions` 支持 IncludeRecentlyDeletedAssets / OnlyExistingAssets；默认 string overload 包含 recent delete continuity。
   - `GUIDFromAssetPath` 返回 `GUID` wrapper；找不到返回 empty/default GUID，不抛异常。
   - `LoadAssetAtPath<T>` / `LoadAssetAtPath(path, Type)` 找不到、类型不匹配或 stale path 返回 null，并写 report；存在匹配 sub-asset 时按 deterministic order 返回。
   - `IsMainAsset` / `IsSubAsset` / `IsForeignAsset` / `IsNativeAsset` 同时暴露 object 与 instanceID overload；instanceID 只查当前 context-local id table。
   - `EditorUtility.IsPersistent` 与 `AssetDatabase.Contains`、`EditorUtility.IsDirty` 分别验证，不能互相替代。
   - `OpenAsset(Object/int/Object[])` 返回是否 UI open request 成功；target 解析、Excel range mapping、adapter route 和 opened/skipped/failed 写入 report。
   - `Selection` 暴露 activeObject、objects、activeInstanceID、instanceIDs、assetGUIDs、count、selectionChanged、Contains、SetActiveObjectWithContext；invalid/cross-context selection update all-or-nothing。
   - `EditorGUIUtility.PingObject(Object/int)` 暴露 Unity-like API，失败只写 UI projection report，不修改数据状态。
   - `Undo` 暴露 RecordObjects、RegisterCompleteObjectUndo、group name/index/collapse、FlushUndoRecordObjects、PerformUndo/Redo、Revert、Clear 和 `isProcessing` / `undoRedoPerformed`；这些 API 全部绑定当前 context。
   - `FindAssets` 返回 guid，不 materialize object，并按 current asset path 排序。
   - `CreateAsset` 只创建 editor draft row 和 `temporary_draft` identity；创建时已有最终 row guid/local id 和 asset guid/local file id，`SaveAssets` 只把它们写入 workbook 并把 identity_state 变为 stable。
   - `CreateAsset` 在 draft row / identity allocation 前触发 `OnWillCreateAsset`；processor failure 不留下半分配身份。
   - `AddObjectToAsset` 成功只创建 child draft、ownership/dependency edge、Undo 和 dirty state；`SaveAssets` 后同一 child identity 变 stable。
   - `RemoveObjectFromAsset` 成功只创建 child delete/detach draft；SaveAssets 前 undo 可恢复同一 child identity。
   - Excel 直接新增行缺 metadata 时，Editor 可显示 `temporary_imported` preview，但 runtime hot reload 不发布 unstable added object。
   - `MoveAsset` / `RenameAsset` 只创建 editor draft key-field diff；成功返回空字符串，失败返回错误文本并写 machine-readable report。
   - `MoveAsset` / `RenameAsset` 在 draft diff 前触发 `OnWillMoveAsset`；`FailedMove` 或 processor failure 不创建 draft。
   - `CopyAsset` 创建新 editor draft row，不能复制 row identity、metadata revision 或 diagnostic projection；owned child 和 sub assets 用新 identity，copy closure 内部引用 remap，外部引用默认保留。
   - Excel 复制行但未复制 hidden identity 时按 `row_added_temporary` + `FlushMetadata` 进入稳定身份；复制 hidden identity 时生成 copy-row repair plan，不能在 import/hot reload 中静默 regenerate。
   - `GenerateUniqueAssetPath` 只查询当前 path index，不创建 asset、不分配 identity。
   - `DeleteAsset` 默认标记删除，reference restrict 时返回 false 并列出 referrer。
   - `DeleteAsset` 在 delete marker 前触发 `OnWillDeleteAsset`；`FailedDelete` 或 processor failure 不创建 delete marker。
   - create/delete/undo 不复用 deleted row guid/local id；deleted tombstone 至少保留到下一次成功 save/convert 后 cleanup。
   - `Undo.RecordObject` 只记录 editor draft snapshot，不撤销外部 Excel 保存。
   - `Undo.RegisterCreatedObjectUndo` / `Undo.DestroyObjectImmediate` 只修改 draft create/delete marker，SaveAssets 才落盘。
   - `EditorUtility.SetDirty` 从 object/base snapshot 计算 diff；无法映射到 cell 时拒绝保存。
21. 将现有 `ConfigDatabase` 的 transaction、undo、loader、dependency graph 作为内部实现资产接入新模型；对外 API 不泄漏旧表模型。
22. 做一个 CharacterConfig 的端到端测试：
   - mount workbook
   - load asset at path
   - SerializedObject 修改 hp
   - undo
   - save
   - reread workbook
23. 再做一个 repeated RowRef 的 SerializedProperty 测试。
24. 做一个 Play Mode 热更新测试：
   - 使用 `ExcelDataSource` 启动
   - load 一个 `SkillConfig`
   - 模拟 Excel 修改伤害
   - hot reload
   - 断言同一个 object 实例读到新值
   - 断言 object changed 事件触发
   - 断言 ChangeSet 只在稳定 publish point 发布，watcher callback 不直接 patch object
   - 断言同一 asset 多字段变化被 coalesce，field_ids/property paths 顺序 deterministic
   - 断言引用字段变化产生 dependency_changed，removed/recreated 使 referrer cache 失效
   - 对 child table 模拟 insert/delete/reorder，断言 parent/child identity、owner_event_policy 和 ChangeSet 正确
25. 做一个 Excel / converted bytes 热切换测试：
   - 从 `ConvertedBytesDataSource` 启动
   - 切到 `ExcelDataSource`
   - 修改 Excel 并 hot reload
   - convert bytes
   - 切回 `ConvertedBytesDataSource`
   - 断言 identity、引用和 key index 一致
   - export_view_hash 一致时才允许 Excel/bytes source equivalence no-op；不一致时按普通 switch 或 `convert.export_view_mismatch` 处理
   - source_set_hash 与 identity/key/reference/dependency/preload_plan digest 全部一致时，只发布 source_summary 或 no-op，不发布 property_changed
   - source_set_descriptor_hash 不一致但 source_set_hash 和 digest 一致时，只更新 source summary/path/alias display，不发布 property_changed
   - source_set_hash 一致但任一 digest 不一致时产生 `source.equivalence_mismatch`，保留旧 source
   - Excel source set 与 converted bytes manifest workbook set 不一致时产生 `source.source_set_mismatch`，不能当作等价 no-op
   - 对 multi-workbook source set，断言跨 workbook 引用和 reverse dependency index 一致
26. 定义 CI / build artifact gate 测试：
   - `schema-lint` 不读取 workbook，只检查 descriptor / registry / id conflict。
   - `schema-lint` 先 materialize `SchemaSourceSetDescriptor`，再输出 canonical descriptor artifact、descriptor.bin/json、descriptor source map、generated binding manifest，并验证 schema_source_set_hash/descriptor_hash/schema_hash/layout_hash/codegen_hash deterministic。
   - `schema-lint` 对当前 generated assembly / registry bootstrap 校验 binding manifest；codegen_hash 不一致时产生 `schema.codegen_hash_mismatch`。
   - `schema-lint` 检查 source set invalid、unknown option、id missing、table/field/enum id conflict、reserved id reuse、alias collision、runtime type/property path binding collision、custom extension host requirement/deterministic flag。
   - conformance CI 必须分别运行 `CoreConformance`、`UnityEditorConformance`、`DevelopmentRuntimeConformance` 和 `ReleaseRuntimeConformance`；宿主缺失只能标记对应 conformance 未通过，不能把完整目标版默认能力记为 profile disabled 后继续通过。
   - `workbook-check` 执行 import、compatibility、validation、reference graph，但不输出 bytes。
   - `clone-workbook` 生成新 workbook guid、remap self references、保留 external references，source workbook 不变。
   - `repair-workbook-guid --as-new-source` 对 duplicate workbook guid 生成新 source identity，并走 backup/temp/verify/reimport。
   - `snapshot` 输出 canonical WorkbookSnapshot，CI 能检查 snapshot missing/stale。
   - `diff-workbook` 输出 WorkbookDiff，review gate 不读取 human markdown 判断逻辑。
   - `merge-workbook` 使用 base/ours/theirs 三方输入，冲突时只输出 conflict artifact，不写 merged xlsx。
   - `materialize-defaults --dry-run --plan-out` 输出 OperationPlan、affected cells、old/new canonical state、source_hash_before/after、converted_bytes_effect 和 dry_run_report_hash，不写 workbook。
   - `materialize-defaults --apply --plan` 验证 operation_plan_hash、dry_run_report_hash、source revision、schema/layout hash、default descriptor hash、target raw fingerprint 和 package fingerprint；stale 时不写 workbook。
   - `migrate --dry-run --plan-out` 输出 canonical MigrationPlan、plan_hash、candidate diff、data loss confirmation 和 recovery preflight，不写 workbook。
   - `migrate --apply --plan` 验证 plan_hash、dry_run_report_hash、source fingerprint/revision、descriptor hash 和 confirmation hash 仍有效，执行 backup/temp/verify/replace/reimport，并写 migration history。
   - `convert` 输出 bytes、artifact manifest、machine-readable report。
   - `verify-bytes` 校验 header、artifact_kind、content checksum、bytes_hash、section/segment directory、section/segment checksum、schema manifest、schema/source/source_set/layer_stack/materialized_source/preload_plan/load_set hash、identity/key/reference/dependency/preload_plan/segment digest、load_set_index、layer_manifest/patch_operations 和 runtime index。
   - `verify-source-set` 校验 source layer order、activation、trust/signature、base target hash、layer_stack_hash 和 materialized_source_hash。
   - `verify-source-set` 断言 activation assignment id/input hash/result 已写入 manifest/startup report，且 activation 条件不依赖时间、随机数、网络返回或本机环境。
   - `verify-source-set` 断言 patch_operation_digest 与文件物理顺序无关；same diff_key conflict、低层 materialized base hash、tombstone restore、optional patch ignore policy 都有稳定 blocker/warning 结果。
   - `verify-source-set` 断言 Release patch 签名 payload 覆盖 canonical layer descriptor、patch_operation_digest、target base/layer hash、provider/version、trust policy 和 activation result；debug/source/report 文本变化不影响签名 digest。
   - `benchmark-no-gc` 使用 capacity.json 和 allocation policy，输出包含 measurement_window、allocation_contract_scope、runtime_capacity_hash、gc_bytes、watermark_growth_entries、hot_path_allocation_entries、external_backend_allocation_entries、report_projection_allocation_entries、budget_result 的 allocation_summary。
   - 所有命令接受 `--profile`，省略时使用命令默认 profile；report/manifest 记录 operation_profile_id/hash 和 gate_policy_hash。
   - exit code 0/1/2/3/4/5 分别对应 success、warning policy、error、blocker/data loss、command/env、internal error。
   - artifact manifest 使用 canonical JSON，记录 cache_key_hash、descriptor_hash、schema_hash、layout_hash、codegen_hash、export_view_id/hash、source_set_hash、source_set_descriptor_hash、layer_stack_hash、materialized_source_hash、preload_plan_hash、load_set_manifest_hash、bytes_hash、identity/key/reference/dependency/preload_plan/segment digest、source_layers、load_sets、segments、input_workbooks、build_target/profile、operation_profile_id/hash、gate_policy_hash、convert_profile_hash、converter/runtime provider registry hash、localization_manifest_hash、expression_registry_hash。
   - build cache key 包含 schema/source/layer stack/load set/profile/tool/runtime provider/converter registry/Unity runtime dependency hash/localization manifest hash/expression registry hash；不包含本机路径、human report、输出路径、main_asset_path 展示变化。
   - cache hit 必须重新校验 bytes_hash、artifact_id、schema_manifest、section directory 和 identity/key/reference/dependency/preload_plan digest；校验失败重新 convert。
   - `RuntimeArtifactPackageDescriptor` 记录 artifact_id、artifact_manifest_hash、provider id/version/key、package_descriptor_hash、bytes entries、segment entries、export_view_hash、source_set_hash、materialized_source_hash、runtime_provider_registry_hash 和 unity_dependency_runtime_hash。
   - 同一 converted bytes 分别用 StreamingAssets、Addressables、Resources 或 custom provider 打包时，若 bytes/manifest 不变，则 `bytes_hash`、`source_hash` 和 converted bytes cache_key_hash 不变，只改变 package_descriptor_hash / Unity player build cache。
   - package provider key、输出路径或 Addressables group 变化不能触发 property_changed；runtime switch 只发布 source_summary/package_summary。
   - Release package descriptor 中出现 Excel debug source entry 或 player declared outputs 包含 `.xlsx` 时产生 `build.release_excel_source_forbidden`。
   - Development package 可显式包含 Excel debug source，但 bytes open/verify 失败时不能自动 fallback 到 Excel；必须通过显式 source switch。
   - `ConvertedBytesDataSource(RuntimeArtifactLocator)` 必须通过 provider lease 打开 immutable bytes，并校验 locator、package descriptor、artifact manifest 和 bytes header；provider 缺失、manifest 缺失或 hash 不一致时保留旧 source。
   - Unity resource dependency manifest 记录 guid、main_asset_path、optional sub_asset_local_file_id/name/type、asset_type、runtime_provider、runtime_key、provider_descriptor_hash、dependency_kind、referenced_by_assets、source_locations、runtime/display dependency hash。
   - 单纯 main_asset_path 变化只刷新 display hash 和 manifest/report，不要求 Release bytes cache miss。
   - sub_asset display name/type 变化但 local file id 不变只刷新 display hash；local file id 或 runtime provider/key 变化必须改变 runtime_dependency_hash。
   - Release build 下 exported UnityResourceRef 必须有明确 runtime provider；不能依赖 main_asset_path 兜底加载。
   - Unity Release build 默认只打包 converted bytes，不打包原始 Excel。
   - runtime 打开的 bytes 与 manifest 不匹配时 open 失败。
   - runtime manifest 缺失时 startup report 标记 `manifest_missing=true`；Release policy 可禁止。
27. 做 runtime GC allocation budget 测试：
   - 初始化阶段的 source open、首次批量导入、convert、schema generation、operation workspace init 和水位线增长允许分配，但必须在 allocation_summary 中归因到 initialization 或 watermark growth
   - `RuntimeDatabase.SetAllocationPolicy` 能切换 `AllowWatermarkGrowth`、`ReportWatermarkGrowth`、`FailOnWatermarkGrowth`、`FailOnAnyHotPathAllocation`
   - allocation_summary 记录 policy、measurement_window、allocation_contract_scope、runtime_capacity_hash、requested/effective/observed/recommended capacity、gc_bytes、gc_alloc_count、watermark_growth_count、profiler_backend、sample_reliable、budget_result
   - measurement window 按 runtime_hot_path、editor_core_operation、cli_core_operation、core_report_append、report_projection、external_backend 分账；core window 中出现未登记分配必须失败，不能被 projection 或 external backend summary 抵消
   - `RuntimeCapacityDescriptor` canonical JSON 在不同机器生成相同 runtime_capacity_hash；负数、越界或 dimension/buffer_kind 不匹配产生 `runtime.capacity_invalid`
   - 没有 capacity.json 的 discovery run 可以输出 runtime_capacity_recommendation，但不能算 benchmark pass
   - 预热到水位线后，`LoadAsset`、`TryGetAsset(AssetKey)`、`TryGetAsset(AssetIdentity)`、引用解析、key lookup、依赖遍历不产生 GC allocation
   - 预热到水位线后，`Object.isValid`、`objectState`、`GetInstanceID()`、`Equals`、`GetHashCode`、`obj == null` / `obj != null`、`hideFlags` getter/setter 和 hot-path-safe `Object.name` getter 不产生 GC allocation
   - 预热到水位线后，generated scalar getter、string getter、reference getter、optional/default state accessor 不产生 GC allocation
   - 预热到水位线后，converted bytes 字段读取、string table 访问、repeated/child table 遍历不产生 GC allocation
   - 预热到水位线后，`TryAcquireSnapshot`、snapshot generated view getter、snapshot dependency traversal、snapshot Dispose 不产生 GC allocation
   - 预热到水位线后，已加载 load set 内 `LoadAsset/TryGetAsset/getter/reference/dependency` 不产生 GC allocation
   - 未加载 load set 的查询不隐式加载、不产生 payload allocation，返回 `runtime.load_set_not_loaded` 对应状态
   - `LoadSet/PrewarmLoadSet` 的 segment 映射/解压/对象 wrapper materialization 分配只计入 operation 或水位线增长；相同容量重复加载不再分配
   - 预热到水位线后，`Expression<T>` / `ConditionExpression` typed wrapper 或 bytecode evaluation 使用 caller-provided context、pre-bound symbol slot/function table/constant pool，不解析字符串、不查字典、不反射、不分配
   - 预热到水位线后，weighted selection evaluate/select 使用显式 random stream 和 baked table，不构造临时候选集合，不产生 GC allocation
   - 预热到水位线后，LocalizedTextRef lookup、fallback resolution、typed token format 到 caller-owned `Span<char>` 不产生 GC allocation
   - repeated/child 热路径使用 count + indexer / `ReadOnlySpan<T>` / struct range view；`List<T>`、`ToArray()`、allocating `IEnumerable<T>` 只能作为显式低频 API
   - 预热到水位线后，ExcelDataSource refresh 的 parser workspace、candidate snapshot、hot reload patch、source switch commit、change event 分发不产生 GC allocation
   - 预热到水位线后，owner operation queue、pending ChangeSet、publish dispatch 不产生 GC allocation
   - 预热到水位线后，subscriber dispatch snapshot、stable subscription sequence traversal、异常摘要写入和 `objectChanged` 派生不产生库内部 GC allocation
   - 预热到水位线后，editor core preflight 覆盖 layout dry-run、dirty diff classify、conflict classify、metadata flush preflight、save preflight，不产生 GC allocation
   - 预热到水位线后，`TryGetAssetStatus`、`TryGetWorkbookStatus`、`GetAssetDiagnostics(Span<T>)`、`GetWorkbookDiagnostics(Span<T>)` 不产生 GC allocation
   - 预热到水位线后，machine-readable report append 覆盖 diagnostic、diff entry、allocation entry、side-effect ledger entry，不拼接 message string、不创建异常对象、不创建临时集合
   - human-readable report、Excel 批注、Unity console/UI 投影必须在 `report_projection` scope 单独测量；其分配不能计入 core operation no-GC 通过证据
   - 使用分配式 xlsx/xml/zip backend 时必须产生 `external_backend_allocation_entries[]`；strict CI / benchmark-no-gc / Release runtime source 默认禁止该 backend 进入 core measurement window
   - 数据规模超过水位线时允许一次扩容，必须产生 `allocation_summary.watermark_growth_entries[]`；再次以相同容量、相同 source revision、相同 load set residency 运行时必须 0 GC bytes / 0 watermark growth
   - `fail_on_watermark_growth` 下水位线增长使 benchmark gate 失败；`fail_on_any_hot_path_allocation` 下任意未登记 hot path allocation 产生 `runtime.allocation_budget_exceeded`
   - measurement window 内不构建 human-readable report、日志字符串或分配式 assertion message
   - change event 使用 static subscription / indexed view 或 struct enumerator 验证不分配
   - 热路径禁止 LINQ、闭包捕获、装箱、反射字段访问、临时字符串和 per-event/per-property 临时对象
   - `sample_reliable=false` 的 profiler/backend 结果不能作为 CI 通过证据
   - Unity 2022.3 adapter 用 Unity profiler/GC allocation 测试验证
   - 核心 runtime 用宿主无关 benchmark 或 allocation counter 验证
