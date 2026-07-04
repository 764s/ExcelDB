# Unity-like ExcelDB 计划文档

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
- 高性能是宏观目标：除初始化阶段和水位线增长以外，默认不接受额外 GC allocation；运行时稳定热路径必须把 no-GC 当作 contract，而不是事后优化建议。

### 1.1 最终敲定需求索引

后续实现以本节为准。后文的推演、缺口和阶段计划如果出现“建议”“可选”“当前缺口”等语气，应理解为围绕这些最终需求展开，而不是降低这些需求的优先级。

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

7. 引用系统必须按 reference family 设计。
   - 内部资产引用、Unity asset 引用、插件外部引用分别有身份、校验、导出和诊断规则。
   - Unity asset 原生使用 `UnityResourceRef { guid, main_asset_path }`。
   - guid 是身份，main asset path 是给人看的主资源路径。

8. Excel 与 converted bytes 是一等 data source。
   - 两者共享 schema hash、identity、reference graph 和读取 API。
   - `RuntimeDatabase` 必须支持 Excel 与 converted bytes 的事务式 open/switch/refresh。
   - 切源失败时保留旧 source 和旧 object graph。

9. 热更新必须保持运行中对象图的一致性。
   - 能 patch 的对象优先保持 object identity。
   - 无法 patch 时必须发出 added、removed、moved、renamed、recreated、property changed、dependency changed 等事件。
   - 业务缓存失效不能靠调用方猜。

10. 高性能和 GC 边界是核心需求。
   - 除初始化阶段和水位线增长以外，系统默认不接受额外 GC allocation；任何无法满足的后端、API 或流程必须显式进入 report，并受 mode / CI / benchmark 策略约束。
   - 初始化阶段的 source open、首次批量导入、convert、schema generation、import report 生成可以分配内存。
   - 水位线增长可以分配内存；水位线指对象池、索引、事件缓冲、diff buffer、candidate snapshot、Excel/bytes parser workspace、source read buffer、临时工作区等达到历史最大容量时的扩容。
   - 达到水位线后的运行时热路径应避免 GC allocation，包括 `LoadAsset`、引用解析、key/path 查找、遍历依赖、读取字段、source switch commit、hot reload patch 和 change event 分发。
   - `ExcelDataSource` 的重复 refresh/hot reload 也应复用 source parser workspace；如果某个 xlsx 后端无法保证，应限制在 Editor/Development debug 路径，并在 report 中明确标记 allocation，不作为正式 runtime backend。
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

RuntimeDatabase.Reserve(new RuntimeCapacity(
    objectCount: 20000,
    keyCount: 20000,
    referenceCount: 60000,
    dependencyEdgeCount: 80000,
    stringEntryCount: 30000,
    stringByteCount: 2_000_000,
    rangeCount: 40000,
    changeEventCount: 4096,
    diffEntryCount: 8192,
    parserWorkspaceBytes: 4_000_000));

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

主要缺口：

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
- optional Excel data source for debug

核心原则：

- 运行时默认只读。
- 不依赖 UnityEngine / UnityEditor。
- 除初始化阶段的 source open / 首次批量导入和水位线增长以外，运行时热路径避免 GC allocation。
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

- `ExcelDataSource`：编辑期或调试期直接读写 Excel。
- `ConvertedBytesDataSource`：运行时导出的 Excel converted bytes。
- `CompositeDataSource`：一等 layered source，支持基础包 + patch 包 + override / hotfix / development override。

Source 层只负责数据读写和变更通知，不负责高层编辑语义。

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

## 7. Schema 具体能力审查（第一步）

第一步不是先做 `Object` 或 `AssetDatabase` API 外壳，而是先把 schema 定义成全系统契约。Schema 决定：

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

- 策划在表头上方加一行“本周调整重点”，不影响字段映射，应保留。
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

这些情况可能影响团队理解或未来导出，但当前仍可安全导入：

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

### 7.3 当前 schema 已有能力

当前实现已经有一个很好的雏形：

- `TableOptions.kind`：区分 `ASSET` 和 `EMBEDDED`。
- `TableOptions.id`：稳定 table number。
- `TableOptions.implements`：声明 reference group。
- `FieldOptions.key`：声明业务 key。
- `FieldOptions.ref_table` / `ref_group`：约束 `RowRef` 目标。
- `FieldOptions.layout`：预留 Excel layout hint。
- `FieldOptions.meta`：预留扩展 metadata。
- `SchemaRegistry`：能注册 table、检查 table id/name 冲突、收集 key fields、校验 ref constraint。

这些能力足以支撑“表格行数据库”的第一版，但还不足以支撑 Unity-like asset workflow。

### 7.4 Schema 必须表达的核心语义

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

当前缺口：

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

当前缺口：

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

当前缺口：

- repeated 目前是分号拼接。
- enum 没有独立 schema contract，也没有表头注释/下拉生成规则。
- simple struct 没有声明式 contract，单 cell 与展开列策略没有统一入口。
- embedded message 目前偏 JSON。
- map 不支持。
- oneof/union、嵌套展开、子表策略未定义。

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

当前缺口：

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

当前缺口：

- 已有 fixed table 和 group ref。
- 没有 nullable、soft/hard、ownership、delete policy、dependency kind。
- 复制/删除/热更新时无法从 schema 里判断应 cascade 还是报错。
- 当前 `ExternalRef` 是 `scheme:id`，对 Unity 来说不够：缺少主资源路径作为可读展示，也没有明确 guid 优先规则。

统一决策：

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

当前缺口：

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

当前缺口：

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
- object picker target。
- multi-object edit policy。

原因：

- `SerializedObject` 不是简单反射字段；它要稳定地产生 inspector 和自定义 editor 可用的 property tree。
- Excel cell、property path、runtime field 三者需要可追踪映射。

当前缺口：

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

当前缺口：

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

当前缺口：

- 自定义 editor 和 schema 没有 capability handshake。
- migration 目前靠业务代码临时处理，没有注册到 schema contract。

### 7.5 Schema 第一阶段最小闭环

第一阶段不需要把所有高级能力都做完，但必须把 contract 定下来。最低限度应包含：

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

### 7.7 Schema 设计的验收测试

第一步 schema 能力至少要用这些测试验收：

- 新建 workbook，metadata sheet 包含 table id、field id、schema hash。
- rename sheet 后仍能识别同一 table。
- rename field header 后能通过 field id 或 alias 迁移。
- enum 字段生成表头注释和 data validation dropdown，注释能看到可选值。
- simple struct 字段按 schema 指定写入单 cell 或展开为多列，并且表头注释说明填写格式。
- 旧编辑器保存含 unknown column 的 workbook，不删除 unknown column。
- 新 schema 增加 optional field，旧 workbook 可打开并补默认值。
- 新 schema 增加 required field，旧 workbook 可打开但 convert bytes 被 blocker 阻止。
- key rename 不改变 row guid。
- 删除被引用资产时，根据 delete policy 报错或 cascade。
- UnityResourceRef 的 path 变化但 guid 不变时引用保持有效，并刷新展示路径。
- UnityResourceRef 的 guid 缺失或找不到时给出 missing external asset 诊断。
- behavior tree editor 遇到缺字段时走 migration 或 read-only fallback。
- converted bytes schema hash 不兼容时，runtime open/switch 失败且保留旧 source。

## 8. 分阶段计划

### Phase 1：Schema contract 与原生协议闭环

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

### Phase 2：补齐 Object 和 Asset identity

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

### Phase 3：建立 AssetDatabase API 边界

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

### Phase 4：SerializedObject / SerializedProperty

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

### Phase 5：Excel 一等存盘对象

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

### Phase 6：外部 Excel 修改同步

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

### Phase 7：运行时导出数据源

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
- 定义 runtime GC budget：稳定水位线后的读取、查找、引用解析、source switch commit、hot reload patch、事件分发避免 GC allocation。
- 定义 runtime operation queue / publish point，source switch 与 hot reload commit 不与读取遍历交错。

验收：

- 编辑期 Excel 数据和运行时 converted bytes 数据读取结果一致。
- 业务层可从 `ExcelDataSource` 切到 `ConvertedBytesDataSource`，读取 API 基本不变。
- runtime 默认只读，修改 API 不可用或明确失败。
- 热切换后已加载对象尽量保持 identity，不要求业务层重新获取所有引用。
- 切回 converted bytes 后能验证正式包数据与 Excel 调试数据是否一致。
- CI/Build 能通过 manifest 追踪 schema_hash、export_view_hash、source_set_hash、preload_plan_hash、bytes_hash、input_workbooks、dependency digest 和 Unity resource dependencies。
- 在已完成初始化和水位线预热后，常规 `LoadAsset`、handle lookup、引用解析、key lookup、依赖遍历、ExcelDataSource refresh commit、converted bytes read 和 hot reload patch 的测试不产生 GC allocation。

### Phase 8：扩展体系

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
- 除初始化阶段的 source open / 首次批量导入、导入报告生成和水位线增长外，运行时稳定热路径避免 GC allocation。

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

- 创建 workbook 不能只生成可读表格，还必须生成未来 merge 和 identity 需要的隐藏 metadata。
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
- 如果 row guid/local id 缺失，系统生成新 identity，并把 workbook 标记为 metadata dirty。
- 创建 resident `SkillConfig` object。
- 触发 asset added / object changed 事件。
- 更新 key index 和 dependency graph。
- 如果 Excel 当前可写，metadata 会在下一次保存或自动 metadata flush 时写回 workbook。

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

- 不能因为旧编辑器保存一次就把未来字段删掉。
- Excel writer 必须支持保真写回和 unknown column preservation。

#### 10.9.3 字段 rename

例子：

- `cost` 改名为 `mana_cost`。
- proto field number 或稳定 field id 没变。

预期处理：

- importer 通过 field id / metadata 识别这是 rename。
- 自动迁移 header 或提示迁移。
- `SerializedProperty.FindProperty("mana_cost")` 能找到新字段。
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

- 需要 asset import status：clean / warning / error / conflict。
- 需要 object picker 和引用显示，否则配置之间互引很难用。
- 需要 missing reference 的可视化，而不是运行时报空。

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

结论：从操作人员视角看，流程缺陷不只在数据模型，也在“看不见风险、失败不知道怎么恢复”。第一阶段除了 schema contract，还必须同时设计 report、dry-run、定位和回滚策略。

## 12. 实操视角功能缺失审查

这一节从项目真正落地使用的角度审查缺失能力。优先级按“缺了会不会阻断团队日常使用”来排，而不是按技术模块好不好实现来排。

### 12.1 P0：缺了就无法作为主工作流

#### 12.1.1 Workbook metadata 与稳定身份

缺失内容：

- workbook guid。
- table id / field id / row guid / local id。
- row revision / content hash。
- source fingerprint。
- schema hash / layout version。

为什么是 P0：

- 没有 row guid，Excel 复制行、改 key、改顺序、rename sheet 后，系统无法判断是同一个资产还是新资产。
- 没有 field id，字段 rename 会被误判成“删旧列 + 加新列”。
- 没有 revision，无法做 editor dirty 与 Excel 外部修改的三方 merge。

验收标准：

- 改 sheet 名不丢资产。
- 改 key 不改变 object identity。
- 复制行能检测 duplicate row guid。
- 字段 rename 能通过 metadata 识别。

#### 12.1.2 非破坏性 Excel 写回

缺失内容：

- 保留 unknown columns。
- 保留样式、公式、筛选、冻结窗格、批注、数据验证。
- 只写受影响 cell/range，而不是整 sheet 重建。
- 写 metadata 时不破坏用户可见内容。

为什么是 P0：

- 策划会把 Excel 当工作台，不只是数据容器。
- 如果保存一次就丢样式、公式和未知列，团队不会敢用编辑器写回。

验收标准：

- 旧编辑器打开含新列的 Excel，修改已知字段并保存，新列仍保留。
- 有公式和筛选的表保存后仍可用。
- 自定义 editor 保存行为树后，不破坏同 sheet 上其它人工维护内容。

#### 12.1.3 Import snapshot 与三方 merge

缺失内容：

- 上次成功 import 的 base snapshot。
- editor draft snapshot。
- Excel latest snapshot。
- property-level conflict detection。
- conflict resolver。

为什么是 P0：

- 游戏项目里 Excel 和 editor inspector 同时改同一份配置很常见。
- 只靠文件时间戳会导致误覆盖或反复要求 reload。

验收标准：

- 两边改不同字段可自动合并。
- 两边改同字段必须报冲突。
- 未 resolve 冲突时 `SaveAssets` 拒绝静默覆盖 Excel。

#### 12.1.4 结构兼容与迁移

缺失内容：

- missing column policy。
- unknown column preservation。
- field rename migration。
- field type change migration。
- table/sheet rename resolution。
- custom editor schema/layout capability check。

为什么是 P0：

- 游戏配置 schema 会持续变动。
- 编辑器结构和 Excel 表结构不一致不是异常，是日常分支协作和版本迭代的必然。

验收标准：

- 新字段缺列时 workbook 仍可打开，并给出补列/迁移动作。
- unknown column 不被旧编辑器删除。
- 行为树 editor 发现结构不匹配时，可以自动迁移或 read-only fallback。

#### 12.1.5 Runtime hot reload 事务

缺失内容：

- runtime source switch transaction。
- candidate snapshot validation。
- patch resident objects。
- failed reload rollback。
- object identity preservation。

为什么是 P0：

- 运行中调数要求“成功就整批生效，失败就保持旧数据”。
- 半更新会让战斗、AI、UI 缓存进入无法解释的状态。

验收标准：

- Excel 格式错误时，运行中对象保持旧值。
- 切源失败时继续使用旧 source。
- 热更新成功后，同一个 object 实例字段变为新值。

#### 12.1.6 Runtime change events 与缓存失效

缺失内容：

- object changed。
- asset added。
- asset removed。
- asset moved/renamed。
- dependency changed。
- table reloaded。

为什么是 P0：

- 游戏运行系统经常会对配置做派生缓存，例如技能公式、AI 黑板、掉落权重、UI 展示数据。
- 只改 object 字段，不通知缓存重建，看到的效果仍然可能是旧数据。

验收标准：

- 修改技能伤害触发 `SkillConfig` changed，战斗缓存可重建。
- 删除资产触发 removed，旧引用进入 missing/unloaded 语义。
- 引用变化触发 dependency changed，资源预加载可重算。

### 12.2 P1：缺了可以跑，但团队会很痛苦

#### 12.2.1 Validation 与诊断 UX

缺失内容：

- workbook/sheet/row/property/cell 级定位。
- error / warning / info 分级。
- convert blocker 与 editor warning 分离。
- 可跳转 Excel cell。
- 可写回批注或诊断 sheet。

实操影响：

- 策划看到“RowRef dangling”不够，需要知道是哪张表、哪一行、哪个单元格。
- 有些问题可以继续调试，有些问题必须阻止出包。

#### 12.2.2 Convert pipeline 与 CI 集成

缺失内容：

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

缺失内容：

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

缺失内容：

- Editor mode。
- Play Mode debug mode。
- Development build hot reload mode。
- Release read-only mode。
- 对每种模式明确哪些 API 可用。

实操影响：

- 如果运行时也能随便写 Excel，正式包风险很高。
- 如果开发包不能 hot reload，调试效率会大打折扣。

#### 12.2.5 Asset reference 体验

缺失内容：

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

### 12.3 P2：规模化后会暴露的问题

#### 12.3.1 性能与增量

缺失内容：

- runtime allocation budget：除初始化阶段的 source open / 首次批量导入和水位线增长外，稳定热路径不产生 GC allocation。
- 水位线预热机制：对象池、事件缓冲、diff buffer、candidate snapshot、索引、Excel/bytes parser workspace、临时解析工作区可以主动 reserve。
- workbook/sheet/row 级增量 import。
- property-level diff。
- 大表索引。
- 大量 object changed event 的 batch/coalesce。
- converted bytes lazy loading。
- zero-allocation read path：`LoadAsset`、引用解析、key lookup、依赖遍历、字段读取不分配。
- zero-allocation hot reload commit path：patch、事件分发、索引更新尽量复用 buffer。
- zero-allocation ExcelDataSource refresh workspace：重复 hot reload 不应每次创建 ZIP/XML/cell parse 临时对象。
- GC allocation profiler test：在 Unity 2022.3 和核心 .NET 测试中都要能验证。

实操影响：

- 小样例全量 reload 没问题，大项目几万行配置会拖慢 Play Mode 反馈。
- 运行时配置读取和热更新如果持续产生 GC，会把调数便利性转化成帧时间抖动，尤其在战斗、AI、UI 刷新和资源预加载高频路径里很明显。

边界说明：

- 允许初始化阶段的 source open、首次批量导入、convert、report 构建、diagnostic 字符串生成分配内存。
- 允许超过历史容量时水位线增长并产生分配，但应可观测、可预热、可通过 report 暴露；相同规模第二次运行不应再次分配。
- 不接受稳定容量后的 per-row、per-property、per-event 临时分配。

#### 12.3.2 可观察性

缺失内容：

- import 耗时统计。
- hot reload 成功/失败日志。
- source switch report。
- changed asset summary。
- validation dashboard。

实操影响：

- 热更新失败时，程序需要知道是 watcher 没触发、import 失败、validation 失败还是 patch 失败。

#### 12.3.3 扩展点治理

缺失内容：

- custom validator 注册生命周期。
- property drawer 注册生命周期。
- custom importer/migration 注册。
- 不同团队模块的扩展隔离。

实操影响：

- 没有治理时，项目后期会出现 validator 顺序依赖、drawer 冲突、迁移脚本互相踩的问题。

### 12.4 缺失能力的最小闭环

如果要让这套系统先在项目里真正跑起来，最小闭环不是先做完整 UI，而是先完成这些能力：

1. 稳定身份：workbook/table/field/row metadata。
2. 非破坏写回：至少 unknown column preservation 和 cell-level data write。
3. 三方 merge：base/editor/excel property diff。
4. 热更新事务：candidate snapshot validation + atomic patch/rollback。
5. 变化事件：object changed / added / removed / dependency changed。
6. 结构兼容：missing/unknown/rename 的明确 policy。
7. convert bytes：deterministic export + schema hash + runtime read-only load。
8. 诊断：能定位到 Excel cell，并区分 warning 和 blocker。

没有这八项，Unity-like API 再像，也只能是表层像；有了这八项，哪怕 inspector UI 先简陋，团队也能开始把它作为主工作流试用。

## 13. 补丁式决策清理与原生需求

当前文档已经经过多轮推进，部分规则是为了解决后续推演中暴露的问题才补进来的。计划阶段不应该把这些规则当作补丁保留，而应该把它们上升为原生需求，成为系统第一版就承认的基础协议。

### 13.1 已识别的补丁式决策痕迹

1. `ExternalRef` 之后再追加 `UnityResourceRef`。
   - 补丁痕迹：先有泛化外部引用，再发现 Unity 资源需要 path + guid。
   - 统一决策：引用系统从第一天就是 reference family。Unity asset 使用 `UnityResourceRef`，guid 是身份，main asset path 是展示；`ExternalRef` 只用于非 Unity 或扩展场景。

2. “schema 控制表结构”之后再补“保留策划辅助信息”。
   - 补丁痕迹：像是在 schema 强控制和 Excel 自由编辑之间临时找平衡。
   - 统一决策：workbook 原生分区。schema-owned structure region、data region、helper/freeform region、metadata region 各自有所有权和写入规则。生成器只写自己拥有的区域，正式数据和无语义辅助信息默认保留。

3. 表结构生成再补 dry-run、generation report、warning/blocker。
   - 补丁痕迹：先讨论生成，再发现误写 Excel 的风险。
   - 统一决策：所有会改变 workbook、source、converted bytes 或 resident objects 的操作都走 operation transaction 协议：analyze、diff、classify、report、commit、rollback。没有 report 的结构写入不允许进入实现。

4. 隐藏 metadata sheet 在后文多处被补充。
   - 补丁痕迹：rename、copy row、merge、hot reload 各自都要求 metadata。
   - 统一决策：workbook metadata 是 ExcelDB asset identity 的原生组成部分，不是导入缓存。workbook guid、table id、field id、row guid/local id、row revision、schema hash 必须在第一阶段定义。

5. “支持直接修改 Excel”从目标补成工作流细节。
   - 补丁痕迹：像是额外支持 Excel 外部编辑。
   - 统一决策：Excel 是主 authoring surface 之一。直接改 Excel、保存、验证、导入、热更新、冲突处理，是主链路，不是旁路能力。

6. Excel 与 converted bytes 热切换在 runtime 段落里后补。
   - 补丁痕迹：converted bytes 像是 Excel 的导出副本，后来才要求热切换。
   - 统一决策：`ExcelDataSource` 与 `ConvertedBytesDataSource` 是同一个 `RuntimeDatabase` 的两个一等 source implementation，共用 identity、schema hash、reference graph 和 change event 协议。

7. 热更新后再补 object identity、change event、cache invalidation。
   - 补丁痕迹：先说刷新 resident objects，再补业务系统如何知道变化。
   - 统一决策：runtime object graph 原生支持 change propagation。added、removed、moved、renamed、property changed、dependency changed、recreated 都是明确事件，热切换必须在事务内生成这些事件。

8. 编辑器结构与 Excel 表结构冲突在模拟阶段才展开。
   - 补丁痕迹：schema 变更、generated C#、custom editor、Excel header 各自讨论。
   - 统一决策：schema compatibility 是 Phase 1 的核心协议。字段 rename、type change、required/optional change、deprecated field、editor capability version 都必须有兼容矩阵和诊断规则。

9. “保留旧 `ConfigDatabase` 能力”容易变成 facade 补丁。
   - 补丁痕迹：先换 Unity-like API，再让旧实现继续兜底。
   - 统一决策：旧 `ConfigDatabase` 可以作为内部 storage/transaction/undo/dependency engine，但对外模型必须由 Object、SerializedObject、AssetDatabase、schema、workbook metadata 统一定义。facade 不能暴露旧表模型语义。

10. Unity-like 命名与新增概念命名混在一起。
    - 补丁痕迹：既想像素级 like，又出现 Unity 没有的 `RuntimeDatabase`、DataSource。
    - 统一决策：Unity 已有同构概念采用同名 API，仅 namespace 不同；Unity 没有的概念统一采用 ExcelDB-native 风格命名。这个命名策略是设计原则，不是个别例外。

### 13.2 原生协议收束

后续计划应围绕这些原生协议展开，而不是在各阶段各自补规则：

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
   - 内部资产引用、Unity asset 引用、插件外部引用分别有身份规则。
   - Unity asset 原生使用 `UnityResourceRef { guid, main_asset_path }`。
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
   - 初始化阶段的 source open、首次批量导入、convert、report 生成和水位线增长允许分配。
   - 水位线包括 object/index/event/diff buffer，也包括 ExcelDataSource parser workspace、ConvertedBytesDataSource reader workspace 和 candidate snapshot。
   - 达到水位线后的运行时读取、handle/key 查找、引用解析、依赖遍历、source switch commit、hot reload patch、事件分发应避免 GC allocation。
   - 高频路径使用 `AssetKey` / `AssetIdentity` / caller-owned buffer，不依赖每帧字符串拼接或分配式枚举。
   - 所有 runtime 热路径都要有 allocation budget 测试，Unity adapter 和宿主无关 runtime 都要能验证。

9. Runtime change propagation protocol。
   - resident objects 的 patch、recreate、delete、dependency changed、index changed 都必须有事件。
   - 切源或热更新失败时保留旧 source 和旧 object graph。

10. Editor capability and migration protocol。
   - generated code、custom editor、drawer、validator 与 workbook schema 都要有版本和能力检查。
   - 当编辑器结构落后于表结构时，必须能区分可编辑、只读、可保留、必须升级。

### 13.3 对阶段计划的影响

Phase 1 不应只叫 “schema options + workbook metadata”，而应该交付最小原生协议闭环：

- schema contract。
- value shape and header annotation，包含 enum、simple struct、表头注释和 dropdown。
- workbook region ownership。
- workbook metadata and identity。
- reference family，至少包含 internal ref 与 UnityResourceRef。
- platform adapter boundary，至少包含 Unity 2022.3 adapter 和核心无 Unity 依赖约束。
- runtime performance and GC budget。
- operation transaction/report。
- compatibility matrix。
- schema generation dry-run。
- 基础 import/convert diagnostic。

后续 Phase 2 到 Phase 7 才是在这个协议闭环上实现 Object、AssetDatabase、SerializedObject、Excel source、runtime source switch 和 hot reload。这样不会出现“API 已经像 Unity，但 Excel 工作流靠补丁兜底”的结构性风险。

## 14. 执行细化：确定性默认决策

本节用于消除实现时的自由发挥。若后文没有更具体的规则，默认按本节执行；若两个目标冲突，按优先级选择，并把被牺牲的目标写入 report。

### 14.1 决策优先级

1. 不丢数据，不破坏用户 workbook。
2. schema 是唯一结构事实源，metadata 是唯一稳定身份源。
3. 所有结构写入、import、save、convert、source switch、hot reload 都必须事务化。
4. runtime object identity 和 change event 必须一致，业务缓存不能靠猜。
5. 达到水位线后的 runtime 热路径避免 GC allocation。
6. 核心库保持宿主无关，Unity 2022.3 只在 adapter 中出现。
7. Unity adapter 的对外 API 像素级贴近 Unity。
8. Excel authoring 体验优先保留策划手工辅助信息。

解释：

- 数据安全高于 API 好看；如果一次保存会丢未知列、样式、公式或正式数据行，必须拒绝或进入手动修复流程。
- 身份稳定高于显示文本；key、sheet name、header text、asset path 都可以变，metadata identity 不能被静默重建。
- 运行时热路径性能高于调试便利；需要诊断字符串时应在 report 构建或 editor/debug 路径中生成，不在稳定帧内生成。

### 14.2 默认 assembly / adapter 边界

默认至少拆成三类边界：

- `ExcelDbEngine`：宿主无关 runtime、object identity、source、converted bytes reader、reference family registry、GC budget 基础设施。
- `ExcelDbEditor`：宿主无关的编辑抽象、schema generation、workbook import/export、diagnostic/report、transaction。
- Unity 2022.3 adapter：UnityEditor 菜单、import hook、asset picker、Play Mode hot reload、Unity build integration、`UnityResourceRef` resolver。

硬约束：

- `ExcelDbEngine`、schema、source、convert pipeline 不引用 `UnityEngine` / `UnityEditor`。
- `UnityResourceRef` 是 Unity adapter 首发的 reference family；核心只认识 reference family contract，不认识 Unity asset database。
- 非 Unity adapter 不能修改 schema/workbook/runtime 的核心语义，只能通过 reference family、source、editor facade、validator、converter extension 扩展。

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
- 如果 column move 会覆盖 unknown/helper/freeform column、merged range、formula dependency 或无法被 xlsx backend 保真执行，则不移动，产生 warning 或 blocker，具体由 operation mode 决定。
- column order 变化不改变 field identity、row identity、source hash；只影响 layout hash 和 authoring display。

helper/freeform 保留默认规则：

- schema 未声明但不参与 field mapping 的 column 默认保留；如果它位于 data region 且含数据，产生 `layout.unknown_column_preserved` warning。
- schema 声明 ignored/helper column 后，该列可以带样式、批注、公式和策划备注；import/export/runtime 忽略其语义。
- helper column 与 schema field insertion/move 目标重叠时，不静默覆盖；尝试寻找下一个安全插入点，找不到时 `structure.helper_region_overlap` blocker。
- 表头上方额外说明行默认不在 table sheet v1 的 schema-owned region 内；如果项目允许 pre-header helper rows，必须由 table descriptor 声明 `pre_header_row_count`，否则 generation 不猜测。
- images/charts/pivot/table objects 默认保留；如果它们锚定到要移动的 schema field group，move 前必须确认后端能保真移动，否则降级为不移动列并报告。

missing / deleted field 默认规则：

- 新增 nullable / optional default 字段可以生成列，但不批量写第 8 行以后的默认值；读取时 materialize default/null。
- 新增 required 且无 default 字段可以生成列，但正式数据缺值产生 validation error，convert/build blocker。
- 删除字段且 descriptor 中 reserved/deprecated 时，旧列默认保留为 ignored/deprecated group，不导出。
- 删除字段且未 reserved、旧列含数据时，需要 migration 或 data-loss confirmation；generation 不直接清空或删除列。
- 删除字段且旧列全空，也必须产生 report；是否删除物理列由 cleanup policy 决定，默认保留。

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
- rows 按 `table_id`、`row_local_id` 升序；没有 stable local id 的 temporary row 排在该 table 末尾，并按 temporary identity token 排序。
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
- data row 的 companion cells 是行身份的第一读取来源；`[block:rows].current_row_number` 只是加速和诊断定位，不是身份事实源。
- 策划排序、筛选、剪切、复制数据行时，companion cells 应随行移动；import 以 companion identity 匹配 row record。

metadata 读取默认规则：

- 先验证 header、format_version、metadata_schema_version、metadata_checksum。
- format_version 大于当前支持版本时，默认 read-only open；任何写回、metadata repair、convert 都进入 blocker，除非有显式 metadata migration。
- metadata_checksum mismatch 是 blocker；只允许在用户确认的 repair operation 中重建。
- companion identity 与 `[block:rows]` 冲突时，以 companion cell 为现场事实，进入 repair analyze；能通过 row guid/local id 唯一匹配旧 record 时更新 current row number，否则 blocker。
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
5. Report：输出人类可读 report 和机器可读 report。
6. Pre-commit：准备 backup、临时文件、buffer reserve、identity map、event buffer。
7. Commit：原子替换 workbook/source/object graph 或 patch resident objects。
8. Verify：复读关键 metadata、schema hash、identity map、reference graph。
9. Publish：提交 dirty state、change event、summary report。
10. Rollback：任一步失败时保留旧 workbook/source/object graph。

默认提交规则：

- safe 自动提交。
- warning 可以继续 import/open；结构写回在 editor UI 中需要明确确认，在 CLI/CI 中由参数或 policy 控制。
- error 允许 workbook 打开和 report 展示，但相关 asset/source 不可 convert，不可作为成功 hot reload/source switch 结果发布。
- blocker 不写 workbook，不替换 runtime source，不清空数据，不发布 change event。

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

### 14.6 默认 report / diagnostic 格式

所有 report 默认同时有 human-readable 和 machine-readable 两种输出。逻辑判断只依赖 machine-readable report，不解析人类文本。

machine-readable report 默认字段：

```text
operation_id
operation_kind
report_format_version
tool_version
phase
source_kind
source_identity
schema_hash
layout_hash
operation_profile_id
operation_profile_hash
gate_policy_hash
result_severity
diagnostics[]
diff_summary
allocation_summary
```

diagnostic 默认字段：

```text
code
severity
phase
scope
location
affects_import
affects_convert
affects_runtime
affects_writeback
can_auto_fix
suggested_action
data_loss_risk
details
```

location 默认包含可用的最精确定位：

```text
workbook_path
sheet_name
table_id
row_guid
row_number
field_id
field_path
column_index
cell_address
metadata_record
```

allocation_summary 默认字段：

```text
policy
measurement_window
gc_bytes
gc_alloc_count
watermark_growth_count
watermark_growth_entries[]
hot_path_allocation_entries[]
truncation_entries[]
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

默认规则：

- `code` 必须稳定，例如 `schema.missing_required_column`、`metadata.duplicate_row_guid`。
- `severity` 只能是 `info`、`warning`、`error`、`blocker`。
- `result_severity` 是 diagnostics 中最高严重级别。
- report 排序必须 deterministic：severity、scope、table id、row guid/row number、field id/column、code。
- CI 和 convert 默认只读取 machine-readable report。
- human-readable report 可以本地化，但不得改变 code、severity、location 和 suggested_action。
- runtime 正式包默认不生成大段字符串；只保留 code、severity、source identity、最小 location 和错误计数。
- `allocation_summary.sample_reliable=false` 时，不能把该次运行当作 no-GC 验收证据；CI/benchmark 必须使用可靠 profiler backend 或宿主无关 allocation counter。

#### 14.6.1 默认 diagnostic code taxonomy / compatibility 协议

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
schema.unsupported_version
schema.missing_required_column
schema.field_id_conflict
schema.reserved_id_reuse
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
transaction.source_revision_changed
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
recovery.manifest_found
convert.export_view_empty
convert.reference_stripped_target
convert.export_view_mismatch
bytes.header_invalid
bytes.checksum_mismatch
bytes.schema_hash_mismatch
bytes.section_missing
bytes.index_corrupt
bytes.manifest_missing
bytes.manifest_mismatch
runtime.mode_forbidden
runtime.switch_failed
runtime.hot_reload_disabled
runtime.hot_reload_candidate_invalid
runtime.publish_reentrant
runtime.allocation_budget_exceeded
runtime.operation_queue_overflow
runtime.patch_plan_stale
runtime.patch_failed
runtime.event_buffer_overflow
runtime.dependency_buffer_too_small
assetdb.find_filter_invalid
assetdb.search_folder_not_mounted
assetdb.path_stale
assetdb.asset_not_found
assetdb.type_mismatch
assetdb.move_forbidden
serialized.property_not_found
serialized.handle_stale
serialized.mapping_changed
serialized.type_mismatch
serialized.read_only
serialized.array_edit_invalid
undo.no_record
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
command.environment_error
internal.unexpected
```

默认 severity / affects 约定：

- `*.missing_sheet`、`*.checksum_mismatch`、`*.duplicate_*`、`*.hash_mismatch`、`*.index_corrupt`、`validation.unique_key_duplicate` 默认 blocker。
- `metadata.asset_guid_collision` 默认 blocker；asset guid / local file id 派生碰撞不能通过改 key/path 自动规避。
- `structure.table_mapping_ambiguous`、`structure.field_mapping_ambiguous`、`structure.required_column_missing`、`structure.data_region_ambiguous`、`structure.helper_region_overlap` 默认 blocker。
- `schema.value_shape_invalid` 默认 schema-lint blocker；高级 value shape 必须有稳定 descriptor、layout 和 runtime 表达，不能由 importer 临时猜。
- `key.missing_required`、`key.normalizer_missing`、`key.pattern_invalid`、`key.asset_path_collision` 默认 blocker；`key.path_not_reversible` 在 schema 允许 `CreateAsset(asset, path)` 或 path 反解时是 blocker。
- `key.segment_invalid` 默认 validation error + `affects_convert=true`；`key.lookup_ambiguous` 默认是 lookup operation error，不修改当前 source。
- `schema.export_policy_invalid` 默认 schema-lint blocker；导出视图不能靠 converter 临时猜字段/表/目标端。
- `layout.unknown_column_preserved`、`diagnostic.user_comment_preserved` 默认 warning 或 info，不影响 import/export/runtime。
- `layout.column_appended_due_to_manual_order` 默认 info，`layout.column_move_skipped` 默认 warning；两者不影响 import/export/runtime，除非 project policy 要求强制 schema physical order。
- `layout.schema_owned_header_mismatch`、`layout.helper_artifact_mismatch`、`layout.data_validation_mismatch` 默认 warning/info 且只影响 authoring layout；如果修复会覆盖非 ExcelDB-owned 内容，则升级为 `structure.helper_region_overlap` blocker 或等待显式 repair confirmation。
- `cell.number_out_of_range`、`cell.integer_fraction`、`cell.formula_not_allowed`、`cell.formula_cache_invalid`、`cell.formula_dependency_undeclared`、`cell.error_value` 对 runtime/export 字段默认 `affects_convert=true`。
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
- `watcher.file_locked`、`watcher.file_unstable`、`watcher.fingerprint_noop`、`watcher.ignored_temp_file` 默认不影响旧数据继续服务；`watcher.read_verify_failed` 默认 warning，连续失败可按 project policy 升级。
- `runtime.operation_queue_overflow` 默认 warning；如果丢弃了 explicit open/switch/close 请求则升级为 blocker，但默认实现禁止丢弃这些请求。
- `runtime.patch_plan_stale`、`runtime.patch_failed` 默认阻止本次 source switch / hot reload commit 并保留旧 source；`runtime.event_buffer_overflow` 默认按 allocation policy 处理，不能静默丢事件。
- `runtime.dependency_buffer_too_small` 默认只在 diagnostic/reporting mode 下产生 warning；no-GC 热路径查询必须优先通过返回 status / required count 表达截断，不能为了记录诊断而分配。
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
- `serialized.property_not_found`、`serialized.handle_stale`、`serialized.mapping_changed`、`serialized.type_mismatch`、`serialized.read_only`、`serialized.array_edit_invalid` 默认是 editor operation error，不写 workbook；如果发生在 SaveAssets preflight，阻止 affected workbook 保存。
- `assetdb.asset_not_found`、`assetdb.type_mismatch`、`assetdb.path_stale`、`assetdb.move_forbidden` 默认是 editor operation error；它们不修改 workbook，除非调用方随后通过 resolver/rename/move operation 成功产生 editor draft。
- `performance.watermark_grew` 默认 info；项目可在 benchmark/CI policy 中升级为 warning/error。
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

#### 14.6.2 默认 diagnostic presentation / Excel writeback 协议

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

cell marker/comment 默认规则：

- 默认不覆盖用户已有 comment / note。
- 如果 cell 已有用户 comment，diagnostic marker 只写到 diagnostic helper sheet，并在 report 中记录 `diagnostic.user_comment_preserved`。
- 如果 schema/metadata 标记某个 comment 是 ExcelDB-owned diagnostic comment，可以由新 report 更新或清除。
- cell marker 默认只使用非语义样式，例如边框/填充/批注入口；导入导出不得依赖这些样式。
- 清除诊断只能清除 ExcelDB-owned marker/comment，不能清除用户手写批注、颜色和备注。

写回策略：

- diagnostic writeback 默认只在 Editor authoring 中允许，并且必须走 operation transaction。
- CI/convert 默认只输出 report，不写 workbook。
- runtime 默认不写 diagnostic 到 source。
- diagnostic writeback 失败不能改变 import/convert 结论；只在 report 中增加 `diagnostic.writeback_failed`。
- diagnostic writeback 可以和 schema generation/save 分开执行；默认不因为刷新诊断而改正式数据 cell。

hash / dirty 规则：

- diagnostic helper sheet、diagnostic marker、diagnostic comment 不参与 schema hash、layout hash、source hash。
- diagnostic writeback 不产生 asset dirty，不触发 runtime hot reload。
- 如果 workbook 只有 diagnostic 投影变化，`AssetDatabase.SaveAssets()` 不应把它当作业务数据修改。

#### 14.6.3 默认 validation pipeline / validator 协议

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
- default value 可以在读取 candidate 时 materialize；是否写回 Excel 由 schema compatibility / save policy 决定。
- custom normalizer 必须有 stable id 和 version；normalizer 变化影响 schema hash，除非 schema 标记为 editor-display-only。

custom validator contract：

- custom validator 必须声明 stable id、version、phase、supported modes、affected table/field ids、deterministic flag。
- validator 不能直接写 workbook、修改 object graph、发事件或读取非声明依赖的外部状态。
- 需要 Unity AssetDatabase、文件系统或网络状态的 validator 必须属于 adapter validator，并在 descriptor 中标注 host requirement；核心 runtime 不依赖它。
- validator 输出 diagnostics 和 auto-fix proposal；auto-fix apply 必须走 operation transaction。
- validator 排序 deterministic：phase、table id、field id、validator id。

#### 14.6.4 默认 Excel cell value parse / normalize 协议

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
- optional 且有 default 的字段读为 default；default materialization 是否写回 Excel 由 save / generation policy 决定。
- required 且无 default 的字段 missing 产生 `validation.required_missing`；schema 声明必须有实际单元格但 cell absent/blank 时产生 `cell.blank_required`。
- string 字段也遵守 missing 规则；若需要把 blank 当空字符串，schema 必须声明 `blank_string_policy=empty_string`，否则 blank 不是 `""`。
- 非 blank 字符串 cell 的内容默认按 exact value 保留；trim、case fold、全半角转换只能由 schema normalizer 显式声明。

scalar parse 默认规则：

- number cell 解析使用 Excel 存储的数值，不使用显示格式文本；thousands separator、百分号、货币符号只在 schema normalizer 显式声明时接受。
- int/long 字段要求数值是有限值、无小数部分、在目标类型范围内；否则 `cell.number_out_of_range` 或 `cell.integer_fraction`。
- float/double 字段要求数值是有限值；NaN/Infinity 不允许从 Excel 输入。
- decimal/fixed-point 字段默认从 number cell 或 invariant string token 解析，并按 schema scale/rounding policy 验证；没有 policy 时不静默四舍五入。
- bool 字段接受 Excel boolean cell，以及 string token `true` / `false` / `TRUE` / `FALSE`；`1` / `0` 只有 schema 声明 `bool_numeric_alias=true` 时接受。canonical bool token 是 `true` / `false`。
- enum 字段接受 schema 声明的 stable name、export token、alias 或 value id；normalized value 是 enum value id，写回展示 token 由 schema 决定。
- reference 字段接受可见 key/path/display token，但 normalized value 必须是 target workbook guid + table id + row guid/local id；解析歧义产生 `reference.ambiguous_target`。

string / culture 默认规则：

- 所有 culture-sensitive parse 默认使用 invariant culture；禁止依赖当前 OS culture、Excel UI language 或 Unity editor language。
- string canonical value 使用 UTF-8 exact bytes；默认不 trim、不大小写折叠、不 Unicode normalize。
- schema 可以声明 string normalizer，例如 trim、case-insensitive key、Unicode normalization form；normalizer id/version 进入 schema hash。
- 换行在 canonical string 中保留为 `\n`；如果 workbook 存储 `\r\n`，import normalizes to `\n` 并把写回 canonical value 作为 auto-fix proposal。

date / time 默认规则：

- Phase 1 不做隐式日期类型猜测；Excel number format 看起来像日期不改变 raw_kind。
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

parse / normalize output 默认字段：

```text
canonical_kind
canonical_value
is_missing
is_default_materialized
source_cell_hash
display_text
diagnostics[]
auto_fix_proposals[]
```

hash / diff 默认规则：

- `source_hash` 使用 canonical value、row identity、field id、reference identity 和 export policy，不使用 Excel 显示格式、列宽、颜色、普通 comment。
- 如果 schema 允许 formula cached value，`source_hash` 同时包含 formula text 和 cached value，避免公式不变但缓存变更或缓存过期被漏掉。
- canonical value 相同但 Excel 显示文本不同，默认只产生 layout/display auto-fix，不触发 runtime property_changed。
- canonical value 改变才进入 property diff、merge、hot reload patch 和 converted bytes 输出。

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

### 14.8 默认 identity 生成与修复规则

identity 默认来源：

- workbook guid 在 workbook 创建时生成，一旦提交到版本库不得自动更换。
- table id 来自 schema。
- field id 来自 schema，默认优先使用显式 field id；没有显式 field id 时才允许使用 proto field number 作为初始值。
- row guid/local id 来自 metadata；key 不是 identity。

新行默认处理：

- Excel 里出现没有 row guid 的新数据行时，import 可以创建临时 identity。
- 如果 workbook 可写，本次 import/save 后必须 flush stable row guid 到 metadata。
- 如果 workbook 不可写，临时 identity 只在当前 session 有效，并产生 `metadata.identity_not_flushed` warning。
- 未 flush stable identity 的新行不能进入 converted bytes。

duplicate row guid 默认处理：

- 如果两行 row guid 相同，且只有一行有 row revision / source hash 匹配历史 snapshot，另一行视为复制行候选。
- 复制行候选可以自动 regenerate row guid，但必须写 report。
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
identity_state: stable | temporary | deleted | repaired
```

默认生成规则：

- 新 workbook 创建时生成 `workbook_guid`，默认 128-bit random id，lowercase 32 hex。
- workbook record 保存 `next row local id`，默认从 1 开始；每次分配 row / child element local id 后递增。
- `row_guid` 默认使用随机 128-bit id；禁止从 key、row number、display name、source hash 派生。
- `row_local_id` 在同一 workbook 内全局唯一，不按 table 单独分配；删除行后不得复用。
- `row_revision` 初次 stable flush 为 1；每次 ExcelDB 成功写入该 row 的 data cell、metadata identity、key snapshot 或 source hash 时递增。
- 用户直接改 Excel data cell 不会自动递增 row_revision；import 时通过 row_source_hash 发现内容变化，下一次 ExcelDB 写回/metadata flush 时更新 revision。

temporary identity 默认规则：

- import 发现正式数据行没有 row identity 时，可以分配 session-local temporary identity，用于本次 editor view / report / conflict 定位。
- temporary identity 不能进入 converted bytes、不能写入 object reference metadata、不能作为长期 `AssetIdentity` 返回给业务缓存。
- 如果 workbook 可写，metadata flush 必须把 temporary identity 转为 stable identity，并写入 row_guid / row_local_id / row_revision / row_source_hash。
- 如果 workbook 不可写或被锁，保留 temporary identity 并产生 `metadata.identity_not_flushed`；本次 session 可浏览，但 convert/build blocker。

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
- 对复制行候选，repair operation 生成新的 row_guid 和 row_local_id，row_revision 重置为 1，key snapshot 按当前行计算，并写 `identity_state=repaired`。
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
string_table
blob_table
table_directory
object_data
identity_index
key_index
reference_index
dependency_index
reverse_dependency_index
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
included_export_targets[]
build_target
build_profile
tool_version
table_layouts[]
field_layouts[]
source_workbooks[]
```

table / object data 默认布局：

- table_directory 保存 table id、runtime type id、object slot range、object data range、field layout range、child range index。
- object_data 按 table_directory 的稳定顺序写入；同一 table 内按 row guid/local id 排序。
- table-specific object record 由 generated descriptor 定义：fixed-width scalar area、bitset/null/default area、variable range area。
- string/reference/blob/repeated/child data 不在 object record 中复制大对象；record 只保存 string id、blob range、reference slot、child range、repeated range。
- optional/default 字段必须可区分 missing、explicit null、explicit value、default materialized；否则 diff/hot reload 会误判。
- child table 和 repeated range 保存 `start + count`，遍历时不创建 list。

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

reader open 默认流程：

1. 建立 immutable bytes view。path-based source 可以 memory-map 或 read-only file buffer；`byte[]` source 默认视为调用方提供的 immutable bytes，open 后调用方不得修改。
2. 校验 header：magic、format_version、artifact_kind、endianness、file_size、content_checksum、schema_hash、export_view_hash、build_target/profile policy。
3. 读取并校验 section directory：范围、alignment、顺序、重复、checksum、required/optional。
4. 建立 section views：schema manifest、string table、object data、indexes、debug symbols。此阶段允许初始化分配。
5. 交叉验证 counts 和 ranges：table range、object slot、identity/key/reference/dependency index、string/blob offsets。
6. 校验 schema descriptor hash 与当前 generated registry 匹配；不匹配时 open/switch 失败并保留旧 source。
7. 构建或绑定 runtime indexes；完成 `Reserve/Prewarm` 后字段读取、引用解析、依赖遍历不得产生 GC allocation。

`ConvertedBytesDataSource(byte[] bytes)` 默认规则：

- 传入 byte array 后，DataSource 把它当 immutable source；调用方继续修改数组属于未定义行为，debug build 可通过 content checksum 发现并报错。
- 如果项目需要防御性拷贝，必须使用显式 copy/import API；默认构造不复制大 bytes，避免初始化峰值内存翻倍。
- memory bytes 没有稳定 path；report 使用 bytes_hash / source_hash 定位。

`verify-bytes` 默认检查：

- 重新计算 `content_checksum`、`bytes_hash`，并与 manifest 对比。
- 校验 header、format_version、endianness、file_size、section directory、required section、section checksum。
- 校验 schema_manifest 中 descriptor_hash / schema_hash / export_view_hash / source_hash / source_set_hash 与命令输入 schema、profile 和 manifest 一致。
- 校验 string table UTF-8、string id 顺序、object record range、identity index、key index、reference index、dependency/reverse dependency index。
- 校验所有 object slot 可达性：table_directory range 覆盖 object_data 中的 object slot，identity/key/reference/dependency 不指向不存在 slot。
- 有 debug_symbols 时校验其 location 只用于 report，不参与 source_hash，不允许影响 runtime 读取结果。

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

- 允许分配：schema load/codegen、source open、首次批量导入、convert、report 构建、diagnostic 字符串生成、`Reserve` / `Prewarm`、超过历史最大容量的水位线增长。
- 不允许分配：已达到水位线后的常规读取、key/identity lookup、引用解析、依赖遍历、converted bytes field read、source switch commit、hot reload patch、ChangeSet dispatch。
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
- runtime read-only mode 下 generated setter 默认不生成，或明确只在 Editor authoring draft / `SetDirty` 兼容路径可用；不能在 resident runtime object 上静默制造 dirty。

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

no-GC 不是“实现尽量优化”，而是运行时能力声明。任何 source backend、runtime API、adapter 或 extension 只要进入 runtime hot path，就必须能被同一套 allocation budget policy 和 report 验证。

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
- 稳定 refresh commit 窗口：fingerprint no-op、candidate 已构建后的 patch plan commit、index swap、ChangeSet dispatch；默认零分配。
- Excel parser 窗口：重复 `ExcelDataSource.Refresh()` 使用 parser workspace；只有 workspace 容量不足时登记水位线增长。
- 订阅回调窗口：库只保证 ChangeSet view 不分配；subscriber 自己分配不计入库内部 budget，但如果库提供的 convenience event 诱导分配，该 API 不能声明 hot-path safe。

watermark growth 默认规则：

- 每一种可增长 buffer 都必须有 stable `buffer_kind`，例如 `object_slots`、`key_index`、`dependency_edges`、`change_events`、`diff_entries`、`parser_workspace_bytes`、`string_entries`、`string_bytes`、`range_views`。
- 扩容前必须知道 requested capacity；扩容后写 old/new/requested capacity 和 operation kind。
- 同一输入规模、同一 `RuntimeCapacity`、同一 source revision 的第二次运行不应再次增长；否则是 budget bug，而不是正常水位线增长。
- 水位线增长不能发生在 object graph 已经对外切换之后；commit 前增长可以回滚，publish 后增长默认是 hot path violation。
- 因 event buffer 不足导致扩容或被 policy 阻止时，必须额外产生 `runtime.event_buffer_overflow`，不能只写 performance summary。

hot path allocation 默认判定：

- 没有登记为 watermark growth 的 GC allocation，一律按 hot path allocation 处理。
- 装箱、分配式 enumerator、`ToArray/ToList`、LINQ iterator、闭包、字符串拼接、反射 artifact、异常对象、per-event/per-property array 都属于违规来源。
- Debug-only assertion、Profiler marker、human-readable message 构建不在 Release/runtime hot path 执行；Editor/Development 若执行，也必须在 report 中标明 measurement window，不得污染 Release 验收。
- `objectChanged` 等 convenience API 默认不作为 no-GC 验收入口；如果实现声明它 no-GC，必须和 `changed` 一样用预分配 buffer 证明。

验证默认流程：

1. 使用固定测试 source，先执行 open + reserve + prewarm，丢弃初始化期测量。
2. 清空 allocation counters / ProfilerRecorder sample baseline。
3. 执行稳定读取窗口 N 次，N 默认至少 1000，覆盖 scalar/string/reference/repeated/child getter。
4. 执行 no-op refresh / source switch equivalence / patch refresh / ChangeSet dispatch。
5. 读取 allocation counter，生成 `allocation_summary`。
6. 再次以同一 capacity 和 source revision 重跑；如果第一次只有 watermark growth，第二次必须为 0 GC bytes / 0 growth。
7. 根据 policy 将 `performance.watermark_grew` 或 `runtime.allocation_budget_exceeded` 映射到 warning/error/blocker 和 exit code。

profiler backend 默认规则：

- 核心 .NET 测试使用宿主无关 allocation counter 或 benchmark harness；不能只靠 wall time。
- Unity 2022.3 adapter 使用 ProfilerRecorder / GC allocation sample；必须记录采样是否可靠。
- IL2CPP / ReleasePlayer 若无法得到逐调用 allocation sample，必须使用场景级 before/after GC allocated bytes + 重复运行证明；`sample_reliable=false` 不能作为 CI 通过证据。
- benchmark 不能在测量窗口内构建 human-readable report、拼接日志或调用会分配的 assertion message。

report / gate 默认规则：

- `performance.watermark_grew` 只表示允许的水位线增长；它必须有对应 `watermark_growth_entries[]`。
- `runtime.allocation_budget_exceeded` 表示违反 policy 或出现未允许 hot path allocation；它必须有 `hot_path_allocation_entries[]` 或 profiler backend 的最小定位。
- CI 中 `--allocation-policy fail-on-any-hot-path-allocation` 是 runtime benchmark 默认策略。
- 普通 editor authoring 可以使用 `report_watermark_growth`，但 Release build gate 必须至少使用 `fail_on_watermark_growth` 验证 converted bytes runtime 读取。

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
Development build：允许受控 ExcelDataSource/hot reload，默认 read-only，写回必须显式启用。
Release：只允许 ConvertedBytesDataSource 或可信 CompositeDataSource，read-only，不允许 Excel 写回，不允许动态 schema migration。
```

默认能力矩阵：

```text
能力                         Editor authoring  Play Mode debug  Development build  Release
ExcelDataSource              yes               yes              opt-in             no
ConvertedBytesDataSource     yes               yes              yes                yes
CompositeDataSource          yes               yes              opt-in             signed converted layers only
AssetDatabase writeback      yes               editor-only      no                 no
SerializedObject edit         yes               editor-only      no                 no
Hot reload                   yes               yes              opt-in             no
Source switch                yes               yes              opt-in             signed patch/layer switch only
Metadata repair              yes               no               no                 no
Large human report            yes               yes              limited            no
Machine-readable report       yes               yes              yes                minimal
```

默认规则：

- Release 模式遇到需要 Excel/source 写回的 API 必须明确失败，不能静默 no-op。
- Release 模式中的 CompositeDataSource 只能包含 converted base 和可信 patch/DLC/hotfix layer；Excel layer、development override 和 unsigned patch 默认失败。
- Development build 的 hot reload/source switch 必须有显式开关和可见 mode 标识。
- Editor Play Mode 中的运行时对象修改不默认写回 Excel；要写回必须走 editor authoring API。
- mode 变化不能改变 schema/metadata 语义，只改变可用能力和 report 详细程度。

#### 14.12.1 默认 project policy / operation profile 协议

`policy` 不能散落在各个按钮、CLI 参数和 adapter if 分支里。所有会影响 operation gate、report、缓存、输出或运行时能力的开关，都必须先归一化为 `OperationProfile`，并写入 machine-readable report。

Profile 默认来源：

```text
built-in default profile
project policy descriptor
named operation profile
host/build target overlay
explicit command/API override
```

生效顺序默认规则：

- 后一层只能覆盖允许覆盖的字段；不能通过 CLI 临时覆盖 schema semantic descriptor，例如 field id、value shape、reference family、validator id/version。
- 所有覆盖都必须进入 canonical `OperationProfile`；report 和 cache 只看归一化后的 profile，不解析原始命令行。
- 如果两个来源同时设置同一不可合并字段且优先级相同，产生 `command.invalid_args` 或 project policy lint error。
- 未识别字段默认是 error；只有 extension descriptor 声明的 policy namespace 可以被对应 extension 消费。

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

`OperationProfile` 默认字段：

```text
profile_id
profile_version
runtime_mode
allowed_source_kinds[]
allowed_source_layer_kinds[]
source_layer_trust_policy
source_layer_activation_policy
allow_excel_writeback
allow_metadata_repair
allow_hot_reload
allow_dynamic_schema_migration
allow_editor_only_fields
warning_policy
diagnostic_severity_overrides[]
data_loss_confirmation_policy
auto_fix_policy
structure_write_policy
save_policy
backup_retention_policy
recovery_policy
manifest_policy
allocation_policy
report_detail_policy
convert_profile optional
build_profile optional
host_requirements[]
extension_permission_policy
extension_policy_overrides[]
```

profile hash 默认规则：

- `operation_profile_hash` 覆盖完整归一化 `OperationProfile` canonical bytes。
- `gate_policy_hash` 只覆盖 warning/error/blocker 升级、data loss confirmation、allocation gate、manifest required 等会影响 operation pass/fail 但不改变 bytes 输出的字段。
- `convert_profile_hash` 只覆盖会影响 converted bytes 内容、section layout、debug symbol、compression、runtime provider selection、string table policy 的字段。
- source layer activation 改变 materialized output 时进入 `convert_profile_hash` / cache key；只改变 gate/trust 要求而不改变 bytes 时进入 `gate_policy_hash`。
- `build_profile` 是 profile 中的事实字段，参与 cache key；但本机路径、Unity Library path、当前用户名、当前时间不得进入任何 profile hash。
- 如果某个 policy 既影响输出又影响 gate，必须同时进入 `convert_profile_hash` 和 `gate_policy_hash`，不能只放在 report。

warning / severity policy 默认规则：

```text
warning_policy: allow | warnings_as_errors | selected_codes_as_errors
diagnostic_severity_overrides[]: code_or_prefix, min_severity, target_modes[]
```

- policy 可以升级 effective severity，不能改变 diagnostic code 的定义。
- 降级 error/blocker 默认不允许；只有明确标为 editor-only display diagnostic 的 code 才可在 editor UI 降级展示，但 machine-readable severity 仍保留原值。
- selected code prefix 必须匹配已登记内置 code 或 custom code registry；未知 prefix 是 policy lint error。

source layer policy 默认规则：

```text
allowed_source_layer_kinds[]: base | platform_variant | dlc | hotfix_patch | experiment | development_override | editor_unsaved_draft
source_layer_trust_policy: none | report_only | require_signature | require_allowlisted_certificate
source_layer_activation_policy: fixed_profile | declared_assignment | host_provided_assignment | forbidden
```

- Release profile 默认只允许 `base`、`platform_variant`、`dlc`、`hotfix_patch`，并要求 converted/signed layer；`development_override` 和 `editor_unsaved_draft` 默认 forbidden。
- `host_provided_assignment` 只能使用启动前写入的 deterministic assignment；运行中随机切换实验层必须走显式 `SwitchDataSource`，并发布 layer_summary。
- trust policy 只控制 layer 能否被采用；不能改变 patch operation 的 canonical value。

data loss / auto-fix policy 默认规则：

```text
data_loss_confirmation_policy: reject | require_explicit_confirmation | allow_with_signed_confirmation
auto_fix_policy: report_only | apply_safe_only | apply_safe_and_warnings | require_confirmation
```

- `data_loss_risk=true` 的 diff/migration/auto-fix 默认不能由 profile 静默允许；必须绑定 confirmation id/hash。
- `allow_with_signed_confirmation` 的 confirmation 必须绑定 operation id、plan hash、affected workbook guid、cell/range 和 canonical value hash。
- auto-fix 只能应用 report 中 `can_auto_fix=true` 且 patch 不覆盖用户数据的 proposal；warning 级 auto-fix 默认需要 confirmation。

structure / save policy 默认规则：

```text
structure_write_policy:
  refresh_schema_owned_only
  allow_safe_layout_refresh
  enforce_schema_order
  report_only

save_policy:
  materialize_defaults: never | on_create | on_save_explicit | always
  cleanup_deprecated: never | if_empty | explicit_only
  write_diagnostic_projection: never | explicit | on_save
  multi_workbook_atomic: best_effort | require_all_or_nothing
```

- 默认 `materialize_defaults=on_create`：新增行可以写 default，旧行缺值读取时 materialize，但不批量改写旧正式数据。
- 默认 `cleanup_deprecated=explicit_only`：deprecated/reserved 列不因普通 SaveAssets 删除。
- 默认 `write_diagnostic_projection=explicit`：诊断投影不让普通 SaveAssets 产生业务 dirty。
- `multi_workbook_atomic=require_all_or_nothing` 只有在 host 提供事务文件系统或项目实现 batch recovery 时可用；否则 profile lint error。

manifest / runtime source policy 默认规则：

```text
manifest_policy:
  require_manifest_for_release: true
  allow_manifest_missing_in_development: true
  release_allow_excel_source: false
  development_allow_excel_source: opt_in
```

- Release profile 默认只允许 `ConvertedBytesDataSource`，manifest 缺失是 gate failure。
- Development profile 可允许 Excel source/hot reload，但必须在 report 中标记 opt-in。
- Profile 不能允许 Release runtime 访问 UnityEditor AssetDatabase、Excel source 或 dynamic migration。

extension permission policy 默认规则：

```text
extension_permission_policy:
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

- `editor_authoring`：`ReportWatermarkGrowth`。
- `editor_play_mode_debug`：`ReportWatermarkGrowth`。
- `development_build`：默认 `FailOnWatermarkGrowth`，可显式降级为 report-only。
- `release_build`：至少 `FailOnWatermarkGrowth`。
- `benchmark_no_gc`：`FailOnAnyHotPathAllocation`。

CLI / API override 默认规则：

- CLI 显式参数例如 `--warnings-as-errors`、`--allocation-policy`、`--allow-data-loss-confirmation` 必须编译进 canonical profile，并在 report 的 `operation_profile_hash` 中体现。
- API 设置如 `RuntimeDatabase.SetAllocationPolicy` 只影响后续 operation profile，不 retroactively 修改已完成 report。
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

- 一次 `SerializedObject.ApplyModifiedProperties()` 默认形成一个 undoable edit unit。
- `Undo.RecordObject(obj, name)` 必须在修改前捕获 schema-defined serialized snapshot；只记录 schema 字段和 editor draft state，不记录 unmanaged runtime cache。
- `Undo.RegisterCreatedObjectUndo` 记录 create marker 和 temporary identity；undo create 必须移除 draft row，并且未 flush stable identity 时不得残留 metadata。
- `Undo.DestroyObjectImmediate` 默认等价于 editor draft delete marker；真正删除 Excel row / metadata tombstone 发生在 `SaveAssets()` commit。
- undo/redo 只改变 editor draft 和 dirty 状态，不写 Excel、不触发 runtime source switch、不撤销外部 Excel 保存。
- undo/redo 后如果 base source revision 已外部变化，相关 asset 进入 needs refresh/merge；下一次 save 必须三方 merge。

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

- `CreateAsset` 创建 editor draft row 和 temporary identity，立即进入 dirty_editor/dirty_metadata；SaveAssets 成功后写 stable identity。
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
canonical_value
value_hash
cell_location optional
export_policy
```

snapshot canonical 规则：

- 使用 canonical JSON，UTF-8 without BOM，LF 换行，object 字段按字典序输出。
- array record 按 workbook guid、table id、row identity、field id、property path、element identity/index 排序。
- 不写 Excel 样式、列宽、筛选状态、当前时间、用户名、本机绝对路径、临时文件路径。
- `canonical_value` 使用 import 后 normalized value；数字、enum、reference、UnityResourceRef、LocalizedTextRef、single-cell codec 都按各自 canonical representation 写。
- 大文本/blob 可以写 `value_hash + preview`；如果 preview 被截断，必须显式标记，merge 判定只使用 hash/canonical bytes。
- helper/freeform region 只写 registered range 的 content hash、owner、cell range 和 optional preview；不能把未登记区域当作 schema 数据。
- `snapshot_hash` 覆盖除自身字段外整个 canonical snapshot。

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

默认手动 `AssetDatabase.Refresh()`：

- 立即 flush 当前 watcher queue。
- 对所有 mounted workbook 检查 fingerprint。
- 不绕过 transaction/report。
- 如果存在 editor dirty，refresh 可以 import candidate，但不能覆盖 dirty draft；必须走 merge/conflict。

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
- ExcelDB backup/temp/recovery 文件，例如 `.__exceldb_tmp_`、`.__exceldb_backup_`、`.__exceldb_recovery_`。
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
- layer order、activation result、source hash、schema/export view hash 共同生成 `layer_stack_hash`。
- `materialized_source_hash` 是应用所有 active layer 后的有效 source hash；它才是 runtime object graph 的 data identity。

patch operation 默认字段：

```text
operation_kind: set_property | replace_row | add_row | delete_row | move_key | set_reference | patch_child | delete_child
table_id
row_guid_or_local_id
field_id optional
property_path optional
element_identity_or_index optional
canonical_value optional
base_value_hash optional
patch_value_hash
patch_source_location optional
data_loss_risk
```

patch / override 默认规则：

- 默认 granularity 是 property-level canonical patch，复用 14.13.1 的 diff key；row replace/table replace 必须由 layer descriptor 显式允许。
- patch 必须声明 base value hash 或 target base source hash；base 不匹配时产生 `source.layer_base_mismatch`，不能按当前值强行覆盖。
- patch 不允许改变 workbook guid、table id、row guid/local id、field id 或 schema value shape；需要改变身份/结构时必须走 migration 或完整 base source。
- key/path 变化使用 `move_key`，保持 row identity；不能通过 delete + add 伪装 rename。
- delete 使用 tombstone operation；被删除 row 不进入 materialized key/reference/runtime index，旧 resident object 在 hot reload 时发布 `removed`。
- add_row 必须携带 stable row identity；不能在 Release patch 应用时现场分配 identity。
- child table patch 默认按 child element identity；没有 stable element identity 的 repeated/list 只能按 row/field replace，或产生 overlay conflict。
- single-cell struct 默认按父 cell patch；只有 codec 声明 `merge_granularity=child` 时才能 patch 子字段。

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
- trust/signature 不进入 materialized_source_hash 的 canonical data 值，但进入 startup report、gate policy 和 cache/trust verification result。

manifest / cache 默认规则：

- artifact manifest 记录 `source_layers[]`、`layer_stack_hash`、`materialized_source_hash`、per-layer source_hash、per-layer patch digest 和 activation summary。
- build cache key 包含会影响 materialized bytes 的 layer descriptors、activation condition、source hashes、patch operation bytes 和 trust-required provider version。
- 如果 convert 选择输出单一 materialized bytes，bytes header/source_hash 使用 `materialized_source_hash`，debug manifest 仍保留 layer stack。
- 如果 convert 选择输出 base bytes + patch bytes，patch bytes 必须有独立 header、target base/layer hash、patch operation section 和 signature/trust metadata。

hot reload / source switch 默认规则：

- layer activation、layer order、patch bytes、base source 任一变化都构成 source switch candidate。
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

- converted bytes 可以是单文件包含整个 source set，也可以是 manifest + 多 bytes 文件；默认 Phase 1 优先单文件 source set。
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
- 跨线程读取 runtime object graph 不在 Phase 1 默认支持范围；需要跨线程读配置的项目必须使用 converted bytes immutable snapshot 或未来显式 snapshot API。

默认发布点：

- core runtime 默认 manual publish：只有显式 `RuntimeDatabase.Refresh()` / `SwitchDataSource()` / `Open()` / `Close()` 调用会执行 commit 和发布事件。
- `EnableHotReload()` 只允许 watcher 入队和 host scheduler 触发 refresh，不代表 watcher 线程可以立即 commit。
- Unity Editor authoring 默认在 editor update 中处理 watcher queue；Play Mode 默认在 Unity main thread 的稳定 PlayerLoop 发布点处理，不能在 inspector repaint、property drawer `OnGUI`、文件 watcher callback 或事件回调中直接 patch。
- Development build 如果启用 hot reload，必须声明 host scheduler 的 publish point；默认建议在一帧 gameplay update 前或后固定位置执行，不能在同一帧任意系统中途插入。
- Release mode 不启用 hot reload；path-based converted bytes refresh 也必须由显式 `Refresh` 或受控 scheduler 触发。

commit / read 一致性默认规则：

- candidate build 可以和上一版 runtime 读取并行，但 commit 不能和运行时读取/遍历交错。
- commit 默认是短暂停顿的 atomic publish：先完成 object/index patch，再切换 current source，再发布 ChangeSet。
- 一个 gameplay frame 内如果没有到达 publish point，读者看到同一 source revision；不会半帧读到旧对象、半帧读到新索引。
- `GetAssets(Span<T>)`、dependency traversal、ChangeSet event view 在一次调用内看到同一 revision；如果 publish 排队中，等待当前调用结束后再 commit。
- 订阅者回调期间禁止重入修改当前 ChangeSet；新的 `Refresh/Switch/Open/Close` 进入 owner queue，在当前 publish 完成后按 deterministic 顺序处理。

owner queue 默认规则：

- watcher event、manual refresh request、source switch request、close/open request 都进入 owner operation queue。
- operation queue 按 request sequence deterministic 排序；同一 workbook/source 的 refresh request 可以 coalesce。
- queue / pending ChangeSet / pending report buffer 属于水位线；超过容量可以扩容但必须写 allocation report。
- 如果 queue overflow 且不能扩容，默认丢弃低优先级重复 refresh request，不丢 explicit `SwitchDataSource/Open/Close` request，并写 `runtime.operation_queue_overflow` diagnostic。

ChangeSet view 生命周期：

- non-alloc `ChangeSet.events` view 默认只保证在 `changed` 回调栈内有效。
- 业务如果要跨帧保存事件，需要复制需要的 identity/code/path；这部分分配属于业务选择。
- `AssetIdentity`、row guid/local id、field id 是可长期保存的稳定值；event view 的 slices、property path view、debug string view 不保证跨 publish 稳定。

Unity adapter 默认规则：

- 任何需要 UnityEditor.AssetDatabase、UnityEngine.Object、Unity main asset path 查询、inspector/drawer 的操作只能在 Unity main thread 执行。
- UnityResourceRef 的 guid/path validation 如果需要 Unity AssetDatabase，必须作为 main-thread adapter validation step；核心后台 import 只能验证格式和已有 manifest。
- PlayerLoop hook 必须可开关并在 report 中标记 publish mode；开发包里自动 hot reload 必须有明显模式标识。

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
- path segment 按 UTF-8 percent-encode：`%`、`/`、`\`、`?`、`#`、ASCII control chars、Windows 保留文件名字符、空 segment、`.` / `..` segment、前后空格都必须编码或报 `key.segment_invalid`。
- path 规范化统一使用 `/`；不做隐式 trim、大小写折叠或 Unicode normalization，除非 normalizer 明确声明。
- 默认要做 case-insensitive path collision 检查；两个不同 canonical key 如果生成只差大小写的 asset path，产生 `key.asset_path_collision`，避免 Unity/文件系统/人工观察层歧义。
- `key path pattern` 必须能从 path 反解 key fields，才能支持 `CreateAsset(asset, path)`；不可逆 pattern 必须声明 `create_from_path=false`，否则产生 `key.path_not_reversible`。
- key pattern 改变只导致 asset path moved/renamed；key field 集合、normalizer 或 key scope 改变属于 migration_required，必须 dry-run duplicate/path collision/reference display 影响。

lookup 默认规则：

- `LoadAssetAtPath` 先解析 mounted workbook root，再解析 table schema name 和 escaped key path；命中 stale old path 时返回 null，并报告 current path。
- runtime `LoadAsset<T>(string keyOrPath)` 的字符串入口先按 schema key 解析；如果包含 mounted workbook-style path root，再按 asset path 解析。
- `TryGetAssetKey<T>(string, out AssetKey)` 只在解析到唯一有效 identity 时返回 true；duplicate、missing、stale path、类型不匹配都返回 false，并写 stable diagnostic。
- `AssetKey` 是 source context 内的预解析 handle；成功解析后记录 table id、canonical key hash/slot 和 resolved identity generation。source switch 后 identity 仍存在时可以重绑定；identity 删除或 duplicate 后返回 false。
- 高频路径应在初始化或 `Prewarm` 阶段把字符串 key/path 解析为 `AssetKey` 或 `AssetIdentity`；达到水位线后 handle lookup 不产生 GC allocation。
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

默认索引规则：

- index 从 successful import snapshot / editor draft candidate 构建；`FindAssets` 本身不 import、不 validate、不 materialize object。
- main asset 默认进入 index；`embedded` child 不进入；schema 声明为 `sub_asset` 且 `findable=true` 的 child 可以进入。
- warning/error 状态但 identity 有效的 asset 可以进入常规结果；blocker、invalid row、unresolved duplicate identity 默认不进入常规结果。
- `label_tokens` 默认来自 schema 声明的 label/tag 字段或 adapter 明确映射的标签；不存在 label schema 时 `l:` 查询只返回空结果，不临时创建标签系统。
- editor dirty draft 中新建且尚未 stable metadata flush 的 asset 可以在 Editor authoring 中被查到，但 guid/path 必须标记为 temporary，并且不能进入 convert/runtime index。

filter tokenization 默认规则：

- `null` 或空 filter 表示匹配当前 scope 内全部有效 asset。
- filter 按 whitespace 分词；双引号包裹的内容作为一个 token，例如 `"fire ball"`。
- quoted token 内支持 `\"` 和 `\\` 转义；未闭合引号返回空数组并报告 `assetdb.find_filter_invalid`。
- token 比较使用 culture-invariant case-insensitive ordinal matching；不依赖当前系统区域设置。
- 只有保留 prefix 被解析为结构化 token；未知 `xxx:yyy` 默认作为普通 name token，以免业务 key 中的冒号被误判。

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
- 同一 family 的多个 name/key/path/table/guid token 默认 OR；需要更严格搜索时由调用方拆分结果或使用更具体 prefix。
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

规则：

- 普通字段使用 schema field path。
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
- 未知 subfield 默认 warning；如果该 struct 标记 closed，则 unknown subfield 是 error。
- 缺少 optional/default subfield 时可 materialize default；缺少 required subfield 是 validation error。

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

#### 14.16.3 默认高级 value shape 范围

Phase 1 默认支持的可编辑 value shape：

```text
scalar
enum
simple struct single_cell
simple struct expanded_columns
repeated scalar single_cell
repeated scalar horizontal columns
child table for repeated complex / owned child
internal object reference
UnityResourceRef
```

默认不在 Phase 1 开放为通用能力：

```text
map
oneof / union
arbitrary JSON cell
polymorphic payload without schema discriminator
multi-dimensional array
```

默认降级/替代表达：

- map 使用 child table 表达为 key/value child rows；key field 必须唯一。
- oneof / union 使用 explicit type discriminator + value columns；每个 variant 必须有 stable variant id。
- polymorphic payload 使用 child table + type discriminator + schema group/ref；不能把任意 JSON 当 payload。
- arbitrary JSON cell 只允许 legacy/custom codec opt-in；默认不参与 property-level merge，convert 前必须通过 validator。
- multi-dimensional array 默认不支持；需要时建模成 child table，并显式声明 row/column index field。

高级 value shape descriptor 默认字段：

```text
value_shape_kind
shape_id
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
map_shape_id
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
union_shape_id
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
- Excel formula cell 仍按 14.6.4 处理；它不是 gameplay expression。需要游戏公式时，字段 value shape 必须声明为 expression。
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

compatibility 默认规则：

- grammar id/version、function signature、symbol type 改变属于 semantic diff；必须重新 parse/typecheck 所有表达式字段。
- 只改表达式空白、括号冗余或格式化是 display diff，不改变 canonical value。
- function 实现修 bug 但 canonical output 不变，可以只 bump package version；改变 evaluation result 必须 bump function version 并影响 cache key。
- 从普通 string/Excel formula 迁移到 Expression<T> 必须有 migration；不能把旧字符串按新 grammar 自动解释后静默保存。

#### 14.16.5 默认 weighted selection / drop table 协议

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

runtime 解析：

- 引用解析通过 row guid/local id index，不通过字符串 key。
- key rename 只更新显示和 key index，不破坏引用。
- reference changed 事件必须触发 dependency graph 更新和 reverse dependency index 更新。
- target removed / recreated / missing 时，referrer 的 dependency closure 通过 reverse dependency index 产生 `dependency_changed`；业务缓存不需要扫描全表。

#### 14.17.1 默认 UnityResourceRef reference family 协议

`UnityResourceRef` 是 Unity adapter 的首发外部资源引用 family。核心库只认识 reference family contract，不依赖 UnityEditor / UnityEngine。

默认存盘结构：

```text
guid
main_asset_path
```

默认 Excel 展开列：

```text
field.main_asset_path
field.guid
```

默认身份规则：

- `guid` 是唯一身份，用于 import、validation、convert、dependency manifest、runtime provider key 生成。
- `main_asset_path` 只是主资源路径展示，方便策划观察、搜索、筛选和人工修复。
- `main_asset_path` 不参与引用身份，不参与 runtime dependency identity；默认也不影响 source hash，除非 schema 明确声明 path display 是 exported runtime field。
- guid 与 path 冲突时，以 guid 为准；产生 `unity_ref.path_mismatch` warning，并生成 path display auto-fix proposal。
- guid 不变、path 变化是安全 move/rename，只刷新展示和 report，不触发引用身份变化。
- guid 为空但 path 有值时，Editor/Unity adapter 可以用 `AssetDatabase.AssetPathToGUID` 解析；解析成功后写 guid auto-fix proposal，解析失败按 required/soft policy 产生 diagnostic。

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

sub-asset 默认规则：

- `UnityResourceRef` v1 默认只表示 Unity main asset。
- 如果 schema 需要引用 sprite、animation clip、material sub asset 等 sub-asset，必须显式声明 `allow_sub_asset=true`，并使用单独的 sub-asset extension descriptor 保存 Unity local file id / sub asset name / sub asset type。
- sub-asset extension 不改变 `guid + main_asset_path` 的主资源展示规则；guid 仍指向 main asset 文件，local file id 才区分 sub asset。
- 未声明 `allow_sub_asset` 时，picker 只能选择 main asset；导入发现 sub asset token 时产生 capability error，而不是按 name 猜。

runtime / converted bytes 默认规则：

- converted bytes 不保存 `main_asset_path` 作为 runtime 解析身份；默认保存 guid-derived runtime key、asset type id、provider id 和 dependency kind。
- `runtime_provider=unity_guid` 只适用于 Editor/Development debug；Release build 默认需要 addressables/resources/custom provider 中的一种明确 runtime resolver。
- `runtime_provider=addressables` 时，convert 必须验证 guid 到 Addressable entry 的映射，并把 addressable key / guid mapping 写入 dependency manifest。
- `runtime_provider=resources` 时，convert 必须验证 asset 位于 Resources 路径下，并写入 Resources load path；guid 仍是 authoring identity。
- custom provider 必须声明 provider id/version、guid->runtime key 规则、host requirement 和 deterministic flag。
- runtime resolver 失败不能按 main_asset_path 兜底加载；必须报告 missing provider/runtime key。

build dependency manifest 默认字段：

```text
guid
main_asset_path
asset_type
runtime_provider
runtime_key
dependency_kind
referenced_by_assets[]
source_locations[]
runtime_dependency_hash
display_dependency_hash
```

Unity build integration 默认规则：

- Unity adapter 在 build 前根据所有 exported `UnityResourceRef` 收集 dependency manifest。
- main_asset_path 来自当前 Unity AssetDatabase 查询结果；Excel 中旧 path 只用于 mismatch diagnostic。
- build cache 默认使用 `runtime_dependency_hash` 纳入 converted bytes cache key；guid 对应 asset 内容、runtime provider、runtime key 或 provider version 变化时重新 convert/build。
- `main_asset_path` 只进入 `display_dependency_hash` 和 report/manifest；单纯 move/rename 且 guid/runtime_key 不变时，可以只刷新 manifest/report，不要求 Release bytes 内容变化。
- 删除或移动 Unity asset 时，只要 guid 可解析，新 path refresh 是 safe；guid 丢失或 meta 文件被重建是 missing asset，不按相同 path 自动认作同一资源。

drawer / picker 默认规则：

- `UnityResourceRef` drawer 显示 main asset path，并持有 guid；选择新 asset 时同时更新 guid 和 main_asset_path。
- 手动编辑 path 不立即改变 guid；需要 resolver 成功并由用户确认或 auto-fix commit 后才更新 guid。
- drawer 在 repaint/OnGUI 热路径不能执行全量 project scan；搜索和验证结果必须缓存或由显式 picker/search 操作产生。

#### 14.17.2 默认 LocalizedTextRef / localization table 协议

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

runtime / converted bytes 默认规则：

- converted bytes 对 `LocalizedTextRef` 默认保存 text entry identity、text key string id、namespace id 和 runtime provider id。
- localization string table 可以作为 bytes section 或单独 localization artifact；无论哪种方式，manifest 必须记录 `localization_manifest_hash`。
- Release runtime 不读取 Excel 文本表，不按 preview_text 兜底；缺 key/locale/provider 必须报告 code。
- export view 可以选择导出 locale subset；`export_view_hash` 必须覆盖 locale set 和 fallback graph。
- `LocalizedTextRef` 字段变化、目标文本行内容变化、required locale 内容变化，都要进入 dependency graph，使 UI/任务/物品展示缓存可以收到 `dependency_changed`。

validation 默认规则：

- 引用解析不到文本条目时产生 `localization.key_missing`；exported runtime field 默认 convert blocker。
- required locale cell blank 按 `localization.locale_missing` 处理，不按普通 optional string 默认空串。
- preview 与 default locale 当前文本不一致时只产生 display diagnostic，可通过 diagnostic/layout refresh 更新 preview。
- 文本表中的普通说明、翻译备注、max_length 只在 schema 声明为 validation/runtime 字段时进入 source_hash；普通 translator comment 不进入 runtime hash。

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

### 14.19 默认 Object instance / lifecycle / ChangeSet 协议

Object identity 和 object instance 必须分开。identity 是存盘身份，instance 是某个 database context 内的运行时承载对象。

默认 identity：

- asset identity：workbook guid + table id + row guid/local id。
- asset path：identity 的当前可读定位，不参与 identity 判断。
- `Object.name`：当前显示名，可随数据变化更新。
- `GetInstanceID()`：database context 内的 session-local instance id，不写入 workbook，不进入 converted bytes。

默认 instance cache：

- 同一个 `AssetDatabase` / `RuntimeDatabase` context 中，同一个 asset identity + runtime type 只允许一个 canonical resident instance。
- 重复 `LoadAssetAtPath<T>`、`LoadAsset<T>` 或引用解析，返回同一个 canonical instance。
- `FindAssets` 只返回 guid，不强制 materialize object instance。
- object slot 可以在 import/bake 阶段预建；真正实例可 lazy materialize，但 materialize 后默认保持 resident，直到 unmount/close 或 recreate/remove 事件。
- 不同 database context 可以有不同 instance id；不能跨 context 比较 instance id。

默认 patch / recreate / remove：

- row guid/local id 不变且 runtime type 不变时，优先 patch 原 instance，`GetInstanceID()` 不变。
- key/path/name 变化只更新索引和显示信息，不 recreate object。
- 字段值变化 patch 后发 `property_changed`。
- reference graph 变化发 `dependency changed`，并更新 reverse dependency index。
- schema 或 type 变化导致无法 patch 时，发 `recreated`；commit 后 `LoadAsset` 返回新 instance，旧 instance 进入 `stale_recreated` 状态。
- row 被删除时，发 `removed`；commit 后按 path/key/guid 正常加载返回 null 或 missing diagnostic，旧 instance 进入 `removed` 状态。
- removed / recreated 的旧 instance 不允许被重新指向新行；identity tombstone 至少保留到本次 ChangeSet 发布完成。

默认 object state：

```text
resident
dirty
missing
removed
stale_recreated
unloaded
```

读取规则：

- `resident` object 可以正常读取字段。
- `dirty` object 可以读取 editor draft；runtime read-only mode 不产生 dirty。
- `missing` / `removed` / `stale_recreated` object 在 editor/debug 下必须能报告 diagnostic；正式 runtime 默认只保留最小错误码。
- Unity adapter 可以模拟 destroyed-object / fake-null 风格；核心 runtime 不要求调用方依赖 fake-null，必须同时提供显式 diagnostic/state 查询。

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
- 同一个 object 的多个 property change 默认 coalesce 到一个 event，field_ids / property_paths 按 schema order 排序。
- subscriber 回调异常不能回滚已经 commit 的数据；异常进入 report，并继续或停止由订阅策略决定，默认继续通知其他 subscriber。
- subscriber 回调中触发新的 refresh/switch/save 默认排队到当前 publish 完成后执行，禁止重入修改当前 ChangeSet。
- runtime 热路径发布使用预分配 ChangeSet / event buffer；超过水位线时可扩容但必须记录 allocation report。

缓存失效默认规则：

- 业务层不需要猜哪些缓存失效；至少可以订阅 table、type、asset identity、dependency graph 四种粒度。
- `property_changed` 影响当前 asset 的派生缓存。
- `dependency_changed` 影响 referrer 和 dependency closure；dependency closure 计算使用 reverse dependency index。
- `removed` / `recreated` 默认使 referrer 的 dependency cache 失效。
- `moved` / `renamed` 只影响 path/key/name cache，不影响 object identity cache。

#### 14.19.1 默认 runtime patch plan / ChangeSet event generation 协议

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

最小 API surface：

```csharp
namespace ExcelDbEditor
{
    public static class AssetDatabase
    {
        public static void MountWorkbook(string workbookPath);
        public static bool UnmountWorkbook(string workbookPath);
        public static void Refresh();
        public static void SaveAssets();

        public static string[] FindAssets(string filter);
        public static string[] FindAssets(string filter, string[] searchInFolders);
        public static string GUIDToAssetPath(string guid);
        public static string AssetPathToGUID(string assetPath);
        public static string GetAssetPath(ExcelDbEngine.Object assetObject);
        public static T LoadAssetAtPath<T>(string assetPath) where T : ExcelDbEngine.Object;
        public static ExcelDbEngine.Object[] LoadAllAssetsAtPath(string assetPath);

        public static void CreateAsset(ExcelDbEngine.Object asset, string assetPath);
        public static bool CopyAsset(string path, string newPath);
        public static bool DeleteAsset(string assetPath);
        public static string MoveAsset(string oldPath, string newPath);
        public static string RenameAsset(string pathName, string newName);
        public static string GenerateUniqueAssetPath(string path);

        public static bool TryGetGUIDAndLocalFileIdentifier(
            ExcelDbEngine.Object obj,
            out string guid,
            out long localId);

        public static OperationReport GetLastOperationReport();
    }

    public static class Undo
    {
        public static void RecordObject(ExcelDbEngine.Object obj, string name);
        public static void RegisterCreatedObjectUndo(ExcelDbEngine.Object obj, string name);
        public static void DestroyObjectImmediate(ExcelDbEngine.Object obj);
    }

    public static class EditorUtility
    {
        public static void SetDirty(ExcelDbEngine.Object target);
        public static bool IsDirty(ExcelDbEngine.Object target);
    }
}
```

错误和 report 默认规则：

- 参数为空、path 格式非法、类型不是 ExcelDB object 这类调用方编程错误可以抛 `ArgumentException` / `InvalidOperationException`。
- workbook 数据错误、schema 不兼容、validation error、file lock、conflict、reference restrict 等内容问题默认进入 `OperationReport`。
- Unity-like 返回值必须保持稳定：`LoadAssetAtPath<T>` 找不到、类型不匹配或 asset invalid 时返回 `null`；`GUIDToAssetPath` / `AssetPathToGUID` / `GetAssetPath` 找不到时返回空字符串；`DeleteAsset` / `CopyAsset` 未执行时返回 `false`；`MoveAsset` / `RenameAsset` 成功返回空字符串，失败返回错误文本并写 report。
- `GetLastOperationReport()` 返回最近一次 AssetDatabase operation 的 machine-readable report；业务逻辑不能解析 human-readable 文本。
- 会修改 workbook/source/object graph 的 API 都必须走 operation transaction；没有 report 的写操作不允许实现。

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

`SaveAssets` 默认语义：

- save 只保存 editor authoring context 中的 dirty workbook / dirty metadata / dirty asset。
- save 前必须对所有 affected workbook 做 preflight：schema compatibility、source revision、conflict、metadata flush、reference policy、validation blocker、data-loss confirmation。
- 如果 workbook source revision 变化，必须先完成三方 merge 并刷新 merged base snapshot，不能直接写回旧 editor draft。
- preflight 出现 blocker 时不写任何 workbook。
- preflight 通过后按 workbook path deterministic 顺序保存；单个 workbook 内必须 atomic save。
- 每个实际写入的 workbook 都必须走 backup / temp / verify / replace / recovery manifest 流程。
- 多 workbook 保存如果中途失败，report 必须列出已保存和未保存 workbook；后续实现可以提供 explicit all-or-nothing batch，但默认不假装跨文件原子。
- save 失败时保留 dirty 状态和 editor draft，不把内存状态标成已保存。
- save 成功后刷新 base snapshot、row revision、source hash，并清除 dirty。

`LoadAssetAtPath` / `LoadAllAssetsAtPath` 默认语义：

- asset path 必须解析到 mounted workbook root、table schema name 和 escaped key path。
- path 解析命中 moved/renamed stale path 时，不猜测 identity；返回 `null` 或空数组，并在 report 中给出 current path。
- `LoadAssetAtPath<T>` 只返回主 asset；类型不匹配返回 `null`。
- `LoadAllAssetsAtPath` 默认返回主 asset 加 schema 声明的 child/sub assets，顺序为主 asset、schema field order、child order index。
- load 不创建新 asset，不修复 metadata，不写 workbook。

`GetAssetPath` / guid-path 默认语义：

- `GetAssetPath(Object)` 只对当前 mounted editor context 中的 resident、dirty draft、temporary draft asset 返回 current path；unloaded/removed/stale object 返回空字符串并写 report。
- `GUIDToAssetPath(guid)` 读取当前 editor path index；如果该 guid 对应 dirty draft move/rename，返回 draft current path。
- `AssetPathToGUID(path)` 只接受 current path；stale old path 返回空字符串，并在 report 中记录 `assetdb.path_stale` 和 current path。
- guid/path API 不 import、不刷新、不修复 metadata；它们只查询当前 path index。

path mutation API 默认语义：

- `MoveAsset`、`RenameAsset`、`CopyAsset`、`GenerateUniqueAssetPath` 操作的是 ExcelDB asset path 投影，不移动 xlsx 文件本身。
- path mutation 必须能解析 mounted workbook root、table schema name、escaped key path，并通过 key path pattern 无损反解 key fields；否则返回失败并报告 `key.path_not_reversible`。
- `MoveAsset(oldPath, newPath)` 默认只允许在同一 mounted workbook root、同一 table id 内改变 key path。跨 workbook / 跨 table move 默认 `assetdb.move_forbidden`；需要显式 migration、copy+delete 或项目扩展 policy。
- `RenameAsset(pathName, newName)` 等价于替换 final asset path segment 后执行 `MoveAsset`；`newName` 不能包含 `/`，并必须能按 schema key normalizer 生成合法 final segment。
- move/rename 成功只创建 editor draft key-field diff，保持 workbook guid、table id、row guid/local id、asset guid 和 local file id 不变；`SaveAssets` 成功后才写入 Excel。
- move/rename 后 editor path index 使用 draft path；old path 立即成为 stale path，不再被 `LoadAssetAtPath` 猜测解析。
- key/path 变化可能产生 `moved` / `renamed` event，但不产生 recreated；如果只有 path/key 变化，不应发布 property_changed，除非 key field 同时是业务 runtime field 且 schema 选择把它作为 property change 暴露。
- move/rename 目标与现有有效 asset path、case-insensitive path 或 duplicate key 冲突时，不创建 draft，并报告 `key.asset_path_collision` / `validation.unique_key_duplicate`。
- `CopyAsset(path, newPath)` 创建新 editor draft row；默认复制 schema data field normalized value，不复制 workbook/row identity、row revision、metadata record、diagnostic projection 或 dirty/conflict state。
- copy 到同 table / compatible runtime type 才允许；跨 workbook copy 可以在目标 workbook 创建新 row，但 reference 字段默认保持原 target identity，不自动 remap 到 copied workbook。
- owned child rows / sub assets 在 copy 时生成新的 element guid/local id；普通 external/internal references 保持指向原目标，除非 schema copy policy 显式声明 remap。
- `GenerateUniqueAssetPath(path)` 不创建 asset、不保留 key、不分配 identity；它只查询当前 path index 和 key descriptor，返回一个当前未占用且可逆的 candidate path。
- `GenerateUniqueAssetPath` 默认 suffix policy 是在 final reversible string segment 后追加 `_1`、`_2`、...；如果 schema key normalizer 或 path pattern 不接受该 segment，继续尝试下一个 suffix；无法生成时返回空字符串并写 report。

`FindAssets` 默认语义：

- 具体 filter 语法以 14.15.2 为准，默认兼容 Unity-like name / `l:` / `t:` 查询，并扩展 `key:` / `table:` / `path:` / `guid:`。
- `FindAssets(string filter)` 等价于 `FindAssets(filter, null)`。
- `searchInFolders` 限制 current asset path prefix，不改变 identity 或 stale path 处理。
- 返回 guid 数组，不 materialize object instance。
- 排序按 current asset path deterministic。
- blocker/invalid row 不进入常规结果；debug/report API 才能查到。

`CreateAsset` 默认语义：

- `assetPath` 决定 workbook、table 和 key/path；schema 必须能从 path 反解或明确填充 key fields。
- `asset` 必须是 transient object 或未绑定 source 的 object；已属于其他 workbook/context 时默认报错。
- create 只创建 editor draft row，不直接写 Excel；创建时预分配最终 row guid/local id 和派生 asset guid/local file id，`identity_state=temporary` 只表示尚未 flush 到 workbook。
- SaveAssets 成功前，temporary draft asset 可以被 Editor authoring 的 `FindAssets` / `GUIDToAssetPath` / `LoadAssetAtPath` 查到，但不能进入 convert/runtime index。
- SaveAssets 成功后同一个 draft row guid/local id 变为 stable；asset guid/local file id 保存前后不得改变。
- undo create 会移除 draft row，但同一 editor context 内默认不复用已分配 temporary row guid/local id；redo create 恢复同一 temporary identity。
- default values、required fields、enum default、simple struct default 由 schema materialize。
- duplicate key 是 validation error；duplicate row identity 是 blocker。
- create 应自动进入 dirty 状态，并可通过 `Undo.RegisterCreatedObjectUndo` 回退。

`DeleteAsset` 默认语义：

- delete 默认只是标记 editor draft deletion，不直接改 Excel；实际删除由 `SaveAssets` 提交。
- delete 前必须检查 reference policy：restrict 返回 `false` 并 report referrers；cascade / set_null / leave_missing 只在 schema 显式允许时执行。
- delete 成功提交后发布 `removed` 和必要的 `dependency_changed`。
- delete 不允许把旧 row identity 复用给新行。

`Undo` 默认语义：

- `Undo.RecordObject` 在修改前捕获 schema-defined serialized snapshot；不记录 Excel 外部保存。
- 一次 `SerializedObject.ApplyModifiedProperties()` 默认形成一个 undoable edit unit。
- undo/redo 只改变 editor draft，并产生 dirty 状态；不会直接写 workbook。
- 外部 Excel 已变化时，undo 后的 save 仍必须经过 source revision / conflict 检查。
- create/delete undo 必须恢复 identity 和 draft 状态；未 flush stable identity 的新对象 undo 后不得残留 metadata。

`EditorUtility.SetDirty` 默认语义：

- `SetDirty` 是 direct object mutation 的兼容入口，不是推荐主路径；推荐仍是 `SerializedObject.ApplyModifiedProperties()`。
- `SetDirty` 标记对象需要从 current object state 与 base snapshot 计算 property diff。
- 如果 diff 无法映射到 schema field/cell，进入 error，`SaveAssets` 拒绝写回。
- runtime read-only mode 下 `SetDirty` 明确失败或 no-op + report，不能静默制造可保存 dirty。

### 14.21 默认 SerializedObject / SerializedProperty facade 协议

SerializedObject 是编辑 draft 和 property diff 的入口，不是对 C# 字段的简单反射包装。它必须由 schema、metadata、object revision 和 cell mapping 共同驱动。

最小 API surface：

```csharp
namespace ExcelDbEditor
{
    public sealed class SerializedObject
    {
        public SerializedObject(ExcelDbEngine.Object obj);

        public ExcelDbEngine.Object targetObject { get; }
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
        public string propertyPath { get; }
        public SerializedPropertyType propertyType { get; }
        public bool editable { get; }
        public bool hasChildren { get; }
        public bool isArray { get; }
        public int arraySize { get; set; }

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

        public SerializedProperty FindPropertyRelative(string relativePropertyPath);
        public SerializedProperty GetArrayElementAtIndex(int index);
        public void InsertArrayElementAtIndex(int index);
        public void DeleteArrayElementAtIndex(int index);
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
ManagedReference: not in first phase
```

默认规则：

- Phase 1 只承诺 single-target `SerializedObject`；multi-object edit 需要单独定义多对象 diff/merge 后才能开放，不能半实现。
- `SerializedObject` 创建时记录 target object revision、source revision、schema hash、layout hash、property mapping revision。
- `FindProperty` 使用 schema field path / Unity-style array path，不使用 Excel header display text。
- 找不到 property 返回 `null`，并在 editor diagnostics 中记录；不自动按相似名字猜测。
- `GetIterator()` 返回 root iterator；遍历顺序为 schema field order、struct subfield order、array index。
- 不在核心 property API 增加非 Unity 成员，例如 `DisplayValue`；展示字符串由 property drawer、debug helper 或 report 生成。

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
- nullable scalar clear 后在 editor draft 中保存 explicit null；optional default clear 后保存 missing/default materialized 状态，是否写回空 cell 由 save policy 决定。
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

- `arraySize` 增大时，新增元素按 schema default materialize。
- `arraySize` 缩小时，删除尾部元素并生成 property diff。
- `InsertArrayElementAtIndex` 插入 schema default element；object reference element 默认插入 null，除非 schema non-null 且有 default。
- `DeleteArrayElementAtIndex` 对 object reference 数组默认贴近 Unity：元素非 null 时第一次调用清空引用，第二次调用移除元素；非 reference 数组直接移除元素。
- `MoveArrayElement` 不进入 Phase 1 最小 API；需要时必须定义稳定 reorder diff，而不是删除再插入伪装。
- repeated single-cell layout 修改任意元素会重写整个 cell；horizontal columns 修改对应 range；child table layout 修改子表行集合。

array element identity 默认规则：

- repeated scalar / repeated simple struct 如果没有 stable element identity，diff key 使用 parent row identity + field id + array index；同一 array 的 insert/delete/reorder 与外部修改默认冲突。
- child table backed array 使用 child row guid/local id 作为 element identity；insert 创建 temporary child identity，SaveAssets 成功后 flush stable identity。
- `arraySize` 修改、insert/delete 都先进入 pending buffer；`ApplyModifiedProperties()` 才创建 child row create/delete marker 或 single-cell/horizontal range diff。
- index 越界、负数、对非 array 调用 array API、对 read-only/deprecated array 修改，产生 `serialized.array_edit_invalid` 或 `serialized.read_only`，不改变 pending buffer。
- child table delete 不复用 element identity；undo delete 恢复同一 draft element identity，外部变化后仍需 conflict resolver。

property handle 默认生命周期：

- `SerializedProperty` handle 只在所属 `SerializedObject` 当前 revision 内有效。
- `ApplyModifiedProperties()`、`Update()`、external refresh、schema layout refresh 后，旧 handle 需要重新获取；继续写旧 handle 必须产生 `serialized.handle_stale` 或 no-op + diagnostic。
- property-to-cell mapping revision 改变且旧 property 无法重新映射时，产生 `serialized.mapping_changed`，不把旧 pending buffer 写入新 cell。
- iterator copy 只复制当前位置和 revision token，不复制底层 draft。

### 14.22 默认 RuntimeDatabase / DataSource facade 协议

RuntimeDatabase 是宿主无关运行时读入口。它不像 Unity 已有类型，因此使用 ExcelDB-native 命名；但读取对象、引用解析、identity、ChangeSet 和 report 仍复用前文协议。

最小 API surface：

```csharp
namespace ExcelDbEngine
{
    public enum SourceKind
    {
        Excel,
        ConvertedBytes,
        Composite
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
        InvalidIdentity
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
    }

    public readonly struct RuntimeProviderKey
    {
        // Opaque provider-specific key; not a display path or managed string.
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

    public readonly struct SourceLayer
    {
        public SourceLayer(string layerId, DataSource source);
        public SourceLayer(string layerId, DataSource source, string layerDescriptorPath);
    }

    public readonly struct RuntimeCapacity
    {
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
        public ConvertedBytesDataSource(byte[] bytes);
    }

    public sealed class CompositeDataSource : DataSource
    {
        public CompositeDataSource(DataSource baseSource, ReadOnlySpan<SourceLayer> layers);
        public CompositeDataSource(string sourceSetDescriptorPath);
    }

    public static class RuntimeDatabase
    {
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

        public static void EnableHotReload();
        public static void DisableHotReload();
        public static void Reserve(RuntimeCapacity capacity);
        public static void Prewarm();
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
- `LoadAsset<T>` 找不到、类型不匹配、asset invalid/missing 时返回 `null`。
- `TryGetAsset<T>` 不分配，成功写 `asset` 并返回 `true`；失败写 `null` 并返回 `false`。
- `TryGetAssetKey<T>(string, out AssetKey)` 把可读 key/path 解析成不分配查询 handle，推荐在初始化或预热阶段调用。
- `TryGetAsset<T>(AssetKey, out T)` 和 `TryGetAsset<T>(AssetIdentity, out T)` 是稳定帧推荐入口，不解析字符串。
- `GetAssets<T>(Span<T>)` 是 zero-allocation 枚举入口；返回 `RuntimeQueryStatus.Truncated` 时 `count` 是 total required count，实际写入数量为 `Min(buffer.Length, count)`。
- `GetDependencies(AssetIdentity, Span<DependencyTarget>)` 是 zero-allocation direct dependency traversal 入口，结果覆盖内部资产、Unity resource、本地化文本和外部 runtime key。
- `GetDependencies(AssetIdentity, in DependencyQuery, Span<DependencyTarget>)` 用于 recursive closure / preload plan 查询；buffer 不足时返回 `RuntimeQueryStatus.Truncated`，`count` 是 total required count，不在热路径创建 report/list/string。
- `DependencyTarget` 只携带 opaque identity/provider key；显示 path、Unity main_asset_path、本地化 key 这类文本只能通过低频 debug/report API 查询。
- 参数非法和 API 使用错误可以抛异常；source 内容错误必须进入 report。
- `SetAllocationPolicy` 只改变后续 operation / benchmark gate 的 budget 策略，不 retroactively 改写已有 report。
- Release 默认 allocation policy 至少是 `FailOnWatermarkGrowth`；开发和编辑器默认可用 `ReportWatermarkGrowth`，但 benchmark/CI 推荐显式切到 `FailOnAnyHotPathAllocation`。

DataSource 默认身份：

- `ExcelDataSource(string workbookPath)` 是单 workbook SourceSetDescriptor 的快捷入口；source identity 来自 source_set_id + source_set_hash，source_set_descriptor_hash 只作为 report/display identity。
- `ExcelDataSource(IEnumerable<string> workbookPaths)` 表示 workbook source set；必须先按 14.14.2 生成 SourceSetDescriptor，再用 source_set_id + source_set_hash 组合 source identity。
- `ConvertedBytesDataSource(string bytesPath)` 的 source identity 来自 normalized bytes path + converted bytes header source hash。
- `ConvertedBytesDataSource(byte[] bytes)` 的 source identity 来自 bytes header source hash；没有稳定文件路径时 path 为空。
- `CompositeDataSource` 表示按 SourceLayerDescriptor 归一化后的 layered source；初始化阶段可以解析 manifest/profile、复制 layer descriptor 和构建 materialized view，稳定读取不沿 layer 链查询。
- `CompositeDataSource(string sourceSetDescriptorPath)` 从 canonical SourceSetDescriptor / layer manifest 构建 source；Release hotfix 推荐使用该入口或等价 bytes manifest。
- source identity 只用于 report、hot reload 和 source switch 判断，不替代 asset identity。

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

- close 关闭当前 runtime context，不修改任何 source 文件。
- close 清空 currentSource、indexes、dependency graph 和 hot reload watcher。
- resident instances 进入 `unloaded` 状态；后续 `LoadAsset` 返回 null，直到重新 `Open`。
- close 可以发布 `source_summary` / `removed` ChangeSet 给已订阅系统；Release 可以只记录最小 report。
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
- event 回调不能重入修改当前 ChangeSet；二次 `Refresh/SwitchDataSource/Open/Close` 默认排队到 publish 后。
- 正式 runtime report 只保留 code、severity、source identity 和计数；Editor/Development 可以保留完整 location。

no-GC 默认语义：

- `Reserve(RuntimeCapacity)` 预留 object slot、key index、reference index、dependency edge、stable string pool、range view table、ChangeSet/event buffer、parser workspace 和临时 diff buffer 的水位线。
- `Prewarm()` materialize 当前 source 的必要 runtime index、parser workspace 和可选 object pool；它允许分配。
- 完成 open + reserve/prewarm 后，`LoadAsset`、`TryGetAsset`、`TryGetAsset(AssetKey)`、引用解析、dependency traversal、source switch commit、hot reload patch、ChangeSet dispatch 默认不得产生 GC allocation。
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
value shape
enum/simple struct/reference descriptor id
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

默认命名绑定：

- `schema field path` 是 Excel metadata、SerializedProperty path、converted bytes mapping 的主字段路径。
- `C# member name` 是生成代码的调用便利，默认由 schema field name 转为 PascalCase。
- `serialized property path` 默认等于 schema field path；不等于 C# member name。
- 例子：schema 字段 `mana_cost` 默认生成 C# 属性 `ManaCost`，但 `FindProperty("mana_cost")` 才是稳定 property path。
- Excel header display name / 中文名 不参与 property path 和 field identity。
- C# namespace / type rename 必须通过 table descriptor 的 runtime type full name 兼容规则处理，不能改变 table id。

generated C# type 默认规则：

- asset table 默认生成 `public partial class XxxConfig : ExcelDbEngine.ScriptableObject`。
- embedded/simple struct 默认生成 `public partial struct` 或 `public partial class`，由 schema value shape 决定；但它不能成为独立 asset identity，除非 schema 声明 child/sub asset。
- generated type 必须是 partial，允许项目写 custom methods / drawers / validators，但禁止手写字段覆盖 generated field mapping。
- generated code 不引用 `UnityEngine` / `UnityEditor`；Unity adapter 的 drawer/picker 在单独 assembly。
- generated property getter 可以用于 runtime hot path；setter 默认只在 editor authoring draft 或 direct mutation + `SetDirty` 路径有效。
- runtime read-only mode 下 setter 必须明确失败、no-op + report，或在生成配置中不暴露 public setter；不能静默改 resident runtime data。

registry 默认规则：

- generated registry bootstrap 在初始化阶段注册所有 `SchemaDescriptor`、runtime type、table id、field id、reference family、validator、drawer hint。
- registry 初始化可以分配和使用反射；稳定热路径不得依赖反射查字段。
- registry 必须检查 table id/type name/schema name 冲突、field id/path 冲突、enum value id 冲突、reference target 不存在。
- `SerializedObject`、Excel importer、converted bytes converter、RuntimeDatabase 都从同一 registry 查询 descriptor。
- 不允许 editor adapter 和 runtime converter 各自构造独立 schema view。

schema hash 默认规则：

- schema hash 来自 canonical descriptor 的语义字段，不来自 proto 文件文本、注释顺序、C# 生成时间或本机路径。
- layout hash 来自 Excel authoring layout、display name、description、header comment、列顺序、data validation 展示。
- 只改 C# member name 且 schema field path / field id 不变，不改变 schema hash；如果 public API rename 需要兼容层，应由 generated code/adapter 处理。
- 只改 generated code 格式、注释或 partial method stub，不改变 schema hash / layout hash。

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
- 所有 repeated descriptor list 必须 deterministic 排序：table by table id，field by field path then field id，enum value by value id，validator/drawer/migration by stable id/version。
- 所有可选字段在 canonical bytes 中必须显式 materialize default；不能依赖 protobuf 默认值、语言默认值或字段缺省状态。
- 不写入 source file path、source line number、注释文本、生成时间、机器用户名、绝对路径、换行风格。
- schema-lint 必须能输出 `descriptor.json` / `descriptor.bin` 用于对比；两个等价 schema source 在不同机器输出 byte-for-byte 相同 descriptor bytes。

hash 输入分类默认规则：

```text
descriptor_hash: canonical descriptor 的完整稳定内容，包含 semantic + layout + editor/tool extension descriptor
schema_hash: 会影响 import/export/runtime 解释、identity、reference、validation blocker、converted bytes layout 的语义字段
layout_hash: 会影响 Excel authoring layout、display、header comment、dropdown、列顺序和 editor presentation 的字段
codegen_hash: 会影响 generated C# public API surface 的字段
```

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
- validator id/version/mode/severity 中会影响 import/export/runtime blocker 的部分。
- migration descriptor 中会影响 schema compatibility 的 stable id/version/from/to/dependency/data-loss fields。

布局字段默认只进入 `layout_hash`：

- display name / 中文名。
- description。
- header comment。
- Excel column order、column width、freeze/filter authoring hints。
- dropdown/helper sheet 展示格式。
- editor group/category/tooltip/readonly visibility，只要不改变 validation/export/runtime 语义。

ID 分配 / 保留默认规则：

- table id、field id、enum value id、child table id、variant id、validator id、migration id 都是长期稳定 ID。
- 已经发布到 workbook/bytes 的 id 删除后必须进入 reserved/deprecated registry，不能复用给新含义。
- id rename 不是合法概念；只能改 display name、schema field path alias、C# member alias，不能改 id。
- field move 到 struct、child table 或其他 table 时，如果语义身份保持，必须通过 migration 明确记录映射；不能靠路径相似自动搬。
- proto field number 只能作为初次生成 field id 的默认来源；一旦 descriptor 发布，后续以 descriptor id 为准，不随 proto 重排变化。
- schema-lint 必须检查 reserved id reuse、id collision、alias collision、field path collision、runtime type binding collision。

generated binding manifest 默认字段：

```text
descriptor_hash
schema_hash
layout_hash
codegen_hash
generated_at_tool_version
runtime_type_bindings[]
field_bindings[]
property_path_bindings[]
enum_bindings[]
runtime_accessor_bindings[]
source_file_fingerprints[]
```

binding 规则：

- runtime type binding 由 table id -> generated C# full name -> runtime type id 构成；table id 是事实源。
- field binding 由 table id + field id -> schema field path -> C# member name -> SerializedProperty path 构成。
- runtime accessor binding 由 table id + field id -> runtime field offset / string id slot / range slot / reference slot / generated getter shape 构成；converted bytes reader、RuntimeDatabase 和 generated C# type 必须使用同一 binding。
- C# member rename 可以通过 alias/obsolete wrapper 兼容，但不改变 schema field path 或 SerializedProperty path。
- generated registry 必须在初始化时验证 generated binding manifest 的 descriptor_hash 与当前 registered descriptor 匹配。
- 如果代码已更新但 workbook/bytes 的 schema_hash 旧，RuntimeDatabase / AssetDatabase 不能静默按新类型解释旧数据，必须走 compatibility/migration/runtime adapter。

schema-lint 默认检查：

- canonical descriptor 可以生成且 deterministic。
- schema_hash / layout_hash / codegen_hash 输入分类符合规则。
- id 唯一、reserved id 未复用、alias 不歧义。
- table/field/runtime type/property path 绑定唯一。
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
- drawer registry 初始化可以分配和反射；绘制稳定路径应避免 per-property 临时分配。
- drawer 缺失时使用 generic fallback drawer；如果字段是 required custom drawer 才进入 editor capability error。
- Unity adapter drawer 可以依赖 UnityEditor；核心 drawer contract 不依赖 UnityEngine / UnityEditor。

默认 built-in drawer：

- scalar number/string/bool。
- enum dropdown，选项来自 enum descriptor。
- simple struct `expanded_columns` group。
- simple struct `single_cell` codec editor。
- internal asset reference picker，按 schema target scope 搜索。
- `UnityResourceRef` picker，由 Unity adapter 提供 guid/path 展示。
- repeated/list drawer，Phase 1 支持 append/delete，reorder 只有 schema 声明 stable element identity 后才开放。

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

- Phase 1 默认不开放 custom editor multi-object edit。
- 普通 property inspector 也只承诺 single-target edit。
- 未来开放 multi-object edit 时，必须定义多对象 diff、mixed value、批量 conflict resolver 和 partial failure report。

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
- `schema-lint` 不读取 workbook 数据，只生成/校验 canonical descriptor、hash、binding manifest、id/reserved-id、reference family、validator/drawer/migration registry。
- `workbook-check` 读取 workbook，执行 import、schema compatibility、validation、reference graph，不输出 converted bytes。
- `clone-workbook` 复制 source workbook 到 target，并按 14.8.2 生成 new workbook guid、remap self references、保留 external references、重建 metadata checksum；source workbook 不被修改。
- `repair-workbook-guid --as-new-source` 只在用户明确把当前 workbook 作为新 source 时生成 new workbook guid；它必须走 backup/temp/verify/reimport，并输出 duplicate guid repair report。
- `snapshot` 读取 workbook 并输出 canonical WorkbookSnapshot；如果 `--out` 是目录，按 project artifact path policy 写入每个 workbook 的 snapshot。
- `diff-workbook` 输出 WorkbookDiff；输入可以是 xlsx 或可信 WorkbookSnapshot，但如果 xlsx 存在，默认重新 import xlsx 验证 snapshot freshness。
- `merge-workbook` 执行 14.13.3 的三方 workbook merge；有 unresolved conflict 时不写 `--out`，只输出 report/conflicts。
- `verify-source-set` 校验 SourceSetDescriptor、source_layers、layer order、activation、trust/signature、base hash 和 materialized_source_hash；不输出 bytes。
- `migrate --dry-run` 读取 workbook 并输出 MigrationPlan、candidate diff、data loss confirmation 和 recovery preflight，不写 workbook。
- `migrate --apply --plan` 必须验证 MigrationPlan 仍然有效，执行 backup/temp/verify/replace/reimport，并写 migration history。
- `convert` 必须先完成 `workbook-check` / `verify-source-set` 等价检查，再输出 bytes 和 artifact manifest。
- `verify-bytes` 打开 bytes，校验 header、artifact_kind、checksum、schema/source/source_set/layer_stack/materialized_source/preload plan hash、identity index、reference graph、dependency/preload plan index、layer_manifest/patch_operations 和 runtime index。
- `benchmark-no-gc` 执行 open + reserve + prewarm 后，按 14.10.2 的测量窗口验证 runtime 热路径、source switch/hot reload commit 和 ChangeSet dispatch 的 allocation budget。
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
- Release build 默认只打包 `ConvertedBytesDataSource` artifact，不打包原始 Excel。
- Development build 可以选择打包 Excel debug source，但必须有显式开关和 report 标记。
- Unity asset dependencies 来自 `UnityResourceRef` guid；main_asset_path 只用于 report 和 inspector 展示。
- Unity resource dependency manifest 默认记录 guid、main_asset_path、asset_type、runtime_provider、runtime_key、dependency_kind、referenced_by_assets、source_locations、runtime/display dependency hash。
- Release build 下 exported `UnityResourceRef` 必须能通过 addressables/resources/custom provider 之一得到 runtime_key；不能依赖 UnityEditor AssetDatabase 或 main_asset_path 兜底。
- 构建缓存命中必须按 14.25.1 的 `cache_key_hash` 和 bytes verify 流程校验；不能只靠文件时间戳或输出路径。

artifact 追踪默认规则：

- `bytes_hash` 是 converted bytes 文件内容 SHA-256。
- `source_set_hash` 来自所有输入 workbook 的 exported data 和 identity，使用 canonical bytes 的 SHA-256。
- `export_view_hash` 来自当前 profile 归一化后的 `ExportViewDescriptor`，client/server/editor/development/release 视图不能互相复用 bytes。
- artifact manifest 应随 bytes 一起提交或作为 CI artifact 保存。
- 正式 runtime startup report 至少包含 schema_hash、export_view_hash、source_set_hash、layer_stack_hash、materialized_source_hash、bytes_hash、format_version、build_target。
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

cache hit 默认流程：

1. 从当前 schema、workbook、profile、Unity dependency manifest 和 converter registry 计算 candidate `cache_key_hash`。
2. 在 cache 中查找同 `cache_key_hash` 的 manifest。
3. 重新计算 cached bytes 的 `bytes_hash`，并验证 manifest 中的 `artifact_id`。
4. 执行 `verify-bytes` 等价的轻量校验：header、schema_manifest、export_view_hash、source_set_hash、layer_stack_hash、materialized_source_hash、section directory、identity/key/reference/dependency index；存在 `preload_plan_index` 时一并校验 preload_plan_hash/digest。
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

### 14.26 未细化事项的默认解法

当文档没有写到某个细节时，默认按下面顺序选择最自然方案：

- 能保留用户 Excel 内容，就不重建整表。
- workbook 与派生 snapshot/diff/review artifact 不一致时，以 workbook 为准，并报告 `vcs.snapshot_stale`。
- 多个 source/layer 参与运行时读取时，先构建 materialized view，再提供 object/index/dependency API。
- 能用 metadata 匹配，就不用 header 文本猜。
- 能用 schema 生成，就不要求策划手填结构信息。
- 能事务提交，就不做半更新。
- 能 patch object，就不重建 object。
- 能复用 buffer，就不在热路径分配。
- 能放在 adapter，就不污染核心。
- 能生成 report，就不只抛异常字符串。
- 能 deterministic 输出，就不依赖当前机器、当前时间、字典遍历顺序或 Excel 打开状态。

## 15. 首批实现任务

1. 扩展 schema options 设计：
   - table display/sheet/version/export。
   - table key descriptor：key field ids、key scope、normalizer id/version、compare policy、path pattern、path segment fields、display name field、alias keys、create_from_path、duplicate/rename policy。
   - field display/type shape/aliases/default/required/export/layout/description/header comment。
   - field parse policy：blank string policy、formula policy、date/time representation、numeric alias、string normalizer id/version。
   - enum schema：value id/name/display/aliases/deprecated/description/export token。
   - simple struct schema：subfields、single cell format、expanded columns layout。
   - single-cell codec：codec id/version、canonical writer、parser、merge granularity、examples、diagnostic code。
   - child table schema：parent table/field、child table id、element identity、order policy、embedded/sub_asset。
   - advanced value shape policy：map descriptor、union/variant descriptor、polymorphic child table、JSON legacy/custom codec opt-in、migration requirement。
   - reference descriptor：family、target table/group/runtime type、target scope、nullable、strength、delete policy、ownership、dependency kind、list duplicate、display token format、resolver/rebind policy。
   - Unity adapter reference family：UnityResourceRef guid/main_asset_path/asset_type/path_display policy、runtime provider、dependency kind、allow_sub_asset policy。
   - localization descriptor：LocalizedTextRef、text table、locale set、fallback graph、token policy、runtime provider、localization manifest hash。
   - expression descriptor：Expression<T> grammar、symbol table、function registry、budget、runtime bytecode/AST representation。
   - weighted selection descriptor：entry child table、selection mode、weight/probability field、condition field、result payload、runtime algorithm、RNG contract。
   - dependency graph / preload plan descriptor：edge target family、dependency kind、root selector、recursive/depth/cycle policy、preload_plan_hash。
   - validator severity/mode。
   - canonical `SchemaDescriptor`：table/field/enum/simple struct/reference/validator/editor capability descriptors。
   - migration descriptor：stable id/version、from/to schema hash、dependency/conflict、affected ids、data_loss_risk、host requirement、dry-run/apply/verify entry。
   - generated C# type binding：table id -> runtime type，field id -> C# member name / SerializedProperty path。
   - generated binding manifest：descriptor_hash、schema_hash、layout_hash、codegen_hash、runtime type bindings、field/property path bindings、runtime accessor bindings。
   - generated registry bootstrap：注册 descriptor、runtime type、table id、field id、reference family、validator、drawer hint。
   - schema hash / layout hash 输入字段边界。
   - canonical descriptor bytes：字段默认值 materialize、descriptor list deterministic sort、禁止 source path/time/local text 进入 hash。
   - project policy descriptor / operation profile：profile id/version、mode、allowed source kinds、warning policy、data loss confirmation、auto-fix、structure/save、manifest、allocation、report detail、convert/build profile。
   - source layer descriptor / policy：layer kind、order、activation、target base hash、patch operation granularity、trust/signature、materialized_source_hash。
   - profile hash 边界：operation_profile_hash、gate_policy_hash、convert_profile_hash 各自覆盖范围和 cache key 影响。
   - id 保留规则：published table/field/enum/child/variant/validator/migration id 删除后进入 reserved/deprecated，不得复用。
   - extension package descriptor：package id/version/namespace、host requirements、extension descriptors、dependencies/conflicts、deterministic、side effect policy、permissions、hash impact。
   - extension permission descriptor：filesystem logical roots、network access、environment variables、Unity project access、external tools、declared inputs/outputs、timeout、side effect policy。
   - extension descriptor：kind/id/version/entry point/host requirement/phase/ordering key/affected ids/diagnostic codes/dependencies/conflicts/capabilities。
   - editor capability descriptor：editor id/version、target table、required/optional fields、schema/layout range、fallback mode、host requirement。
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
   - import 以 companion identity 为第一行身份来源，metadata current row number 只用于加速和诊断。
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
   - helper sheet、named range、data validation 的不一致要能区分可安全重生、需 warning、会覆盖用户内容的 blocker。
   - 默认 column order policy 为 `preserve_existing_known_columns`；新增字段找不到稳定插入点时追加到 system companion columns 前。
   - `enforce_column_order=true` 时只能移动完整 field column group，且必须保留 data/style/formula/comment/data validation。
   - unknown/helper/freeform column 默认保留；与必要 schema patch 重叠时 blocker，不静默覆盖。
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
   - helper/freeform 区域与新增 schema 字段插入位置冲突时，不覆盖 helper，产生 `structure.helper_region_overlap`。
   - 删除 reserved/deprecated 字段默认保留旧列为 ignored/deprecated，不导出。
   - 删除未 reserved 且含数据的字段需要 migration 或 data-loss confirmation，generation 不直接删除列。
   - blocker 级结构错误不写回 workbook。
   - generation report 区分 safe/warning/error/blocker。
   - generation report 包含 column_mapping_before/after、preserved_helper_ranges、updated_header_cells、updated_data_validations、updated_helper_artifacts、updated_named_ranges、skipped_column_moves。
   - enum 字段生成表头注释和 dropdown，重新生成后仍由 schema 控制。
   - helper sheet 丢失但 metadata/layout 可确认时可重新生成，并报告 `layout.helper_artifact_mismatch`。
   - helper sheet generated block 被手动改动时可覆盖重生；但 generated range 中出现非 ExcelDB-owned 内容时必须报 `structure.helper_region_overlap` blocker。
   - data validation 缺失、引用旧 named range 或和 schema 不等价时报告 `layout.data_validation_mismatch`；可保真修复时进入 layout refresh patch。
   - 非 ExcelDB-owned Excel comment 不被静默覆盖；schema 注释仍写入 row 7/helper sheet，并报告 `diagnostic.user_comment_preserved`。
   - simple struct 字段在 single cell 与 expanded columns 两种布局下都能保留数据并正确导入导出。
   - repeated simple struct / owned child object 默认使用 child table，不写 JSON cell。
   - child table sheet 默认命名为 `{parent_schema_name}.{field_path}`，并保留 child data region。
   - child row metadata 保存 owner identity、parent field id、element guid/local id、order index。
   - orphan child row、duplicate element identity、hard missing owner 都产生 diagnostic，convert blocker。
   - map 默认降级为 child table key/value rows，key field 唯一。
   - oneof/union 默认需要 stable variant id；删除正在使用的 variant 是 convert blocker。
   - map child rows 的 duplicate canonical key 产生 `validation.map_key_duplicate`；key normalizer/compare policy 改变必须扫描旧数据。
   - oneof/union 使用 discriminator stable variant id；未知 variant 产生 `validation.variant_unknown`，当前 variant 外 payload 非空产生 `validation.variant_payload_invalid`。
   - union 切换 variant 时旧 payload 清理是 data-loss-risk edit，必须走 Undo/confirmation；layout strategy 互换需要 migration 或可逆证明。
   - arbitrary JSON cell 默认不作为通用能力；opt-in custom codec 不参与 property-level merge。
   - simple struct single-cell 默认 codec 输出 `subfield=value;subfield=value` canonical form。
   - repeated scalar single-cell 默认 codec 输出 `value;value;value` canonical form。
   - string 值使用 JSON string literal，number 使用 invariant culture。
   - parse error 产生 `cell.parse_failed` 并定位到 cell/subfield。
   - `merge_granularity=cell` 默认不做 child-level merge；`merge_granularity=child` 必须 schema 显式声明。
   - 只改 description/header comment 会更新 layout hash，但不导致 converted bytes schema hash 不兼容。
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
   - table id 缺失、table id 冲突、field id 冲突、enum value id 冲突都是 schema blocker。
   - C# member rename 但 schema field path / field id 不变时，不改变 schema hash，不改变 `FindProperty` path。
   - schema 字段 `mana_cost` 默认生成 C# 属性 `ManaCost`，但稳定 property path 是 `mana_cost`。
   - editor/importer/converter/runtime 从同一个 generated registry 查询 descriptor，不允许各自构建 schema view。
   - schema hash 不受 proto 文件注释、生成时间、本机路径、generated code 格式影响。
   - 两个等价 schema source 在不同机器生成相同 canonical descriptor bytes、descriptor_hash、schema_hash、layout_hash。
   - reserved id 复用、alias 歧义、runtime type binding 冲突、property path binding 冲突都是 schema-lint blocker。
   - extension package id/version/namespace、diagnostic code prefix、host requirement、deterministic flag、permission descriptor 都由 schema-lint 校验。
   - extension dependency missing/cycle、version conflict、registry conflict 分别产生稳定 diagnostic，且 convert/build blocker。
   - deterministic=false 的 extension 不能参与 schema/source/bytes/cache hash；参与 convert/build/runtime open 时 blocker。
   - 未声明或未授权 required permission 产生 `extension.permission_denied`；optional permission 进入 fallback/read-only。
   - 外部工具非零退出、超时、未声明输出分别产生 `extension.external_tool_failed`、`extension.external_tool_timeout`、`extension.undeclared_side_effect`。
   - registry manifest 记录 selected/disabled/fallback extension set、descriptor hash、host set、granted/denied permissions 和 fallback reason。
5. 定义 machine-readable report / diagnostic schema：
   - operation_id、operation_kind、report_format_version、tool_version、phase、source identity、schema hash、layout hash、operation_profile_id、operation_profile_hash、gate_policy_hash、result severity。
   - diagnostic code、severity、scope、location、affects_import/convert/runtime/writeback、suggested_action、data_loss_risk。
   - diagnostic code taxonomy：内置 namespace、v1 core code 清单、默认 severity/affects 约定。
   - code 格式只允许 lowercase ASCII 点分 namespace + snake_case leaf，不包含动态 workbook/table/field/row 内容。
   - code 含义一旦发布不得改写；删除 code 只能进入 deprecated/reserved，并提供 replacement 建议。
   - custom validator / codec / migration / drawer 的 code 必须在 generated registry 中登记，prefix 冲突是 schema-lint error。
   - unknown custom code 在 schema-lint/CI 下不能被当 warning 放过。
   - 文档、schema descriptor、validator、report sink 中出现的内置 diagnostic code 必须全部存在于 core code catalog；同一语义不得出现两个 code 名。
   - deterministic sorting。
   - human-readable report 不参与逻辑判断。
   - `__ExcelDB_Diagnostics` 是 generated helper sheet，import/export 忽略。
   - diagnostic helper sheet 和 cell marker/comment 不参与 schema hash、layout hash、source hash。
   - diagnostic writeback 不覆盖用户已有 comment/note，只更新 ExcelDB-owned marker/comment。
   - diagnostic writeback 失败只增加 report diagnostic，不改变 import/convert 结论。
6. 定义 validation pipeline / validator contract 测试：
   - validation phases：schema_lint、workbook_structure、cell_parse_normalize、row/table/reference/cross_table、convert、runtime_open。
   - severity 只允许 info/warning/error/blocker，convert blocker 通过 affects_convert + policy 表达。
   - required、nullability、type_parse、enum、range、regex、unique_key、row_identity_unique、reference、UnityResourceRef 都有内置 validator。
   - absent/blank cell 默认作为 missing；nullable/default/required/string blank policy 分别按 schema 处理。
   - required missing 产生 `validation.required_missing`；schema 要求实际单元格但 cell absent/blank 时产生 `cell.blank_required`。
   - number parse 不依赖 Excel 显示格式；int 字段小数部分产生 `cell.integer_fraction`，超出目标类型产生 `cell.number_out_of_range`，decimal/fixed-point 不静默 rounding。
   - bool 默认只接受 true/false；1/0 必须由 schema numeric alias 显式允许。
   - string 默认 exact value，不 trim、不 case fold、不 Unicode normalize；normalizer 必须有 id/version。
   - date/time 默认 ISO-8601 string；Excel serial date 必须声明 1900/1904、timezone 和 precision。
   - formula cell 在 runtime/export 字段默认产生 `cell.formula_not_allowed` convert blocker；`allow_cached_value` 必须校验 cached result、formula text 和 calc state。
   - cached result 缺失/过期/类型不匹配产生 `cell.formula_cache_invalid`；import 范围外公式依赖产生 `cell.formula_dependency_undeclared`。
   - Excel error cell 产生 `cell.error_value`；merged non-top-left data cell 产生 `cell.merged_data_cell`。
   - EditorImport 下 error asset invalid 但 workbook 可显示；Convert/CI 下 affects_convert error 阻止 bytes 输出。
   - normalizer 只生成 canonical value 和 auto-fix proposal，不静默改写 Excel cell。
   - custom validator 有 stable id/version/phase/mode/affected ids，不能直接写 workbook 或修改 object graph。
   - 多个 custom validator 的执行顺序由 phase、dependency graph、ordering key、validator id 决定，不依赖注册顺序。
   - validator/normalizer/codec 的 side effect policy 默认 pure；需要外部状态的 adapter extension 必须把状态 snapshot 写入 dependency manifest 或 report。
   - adapter validator 必须声明 host requirement，核心 runtime 不依赖 Unity AssetDatabase。
   - UnityResourceRef guid/path mismatch 以 guid 为准并产生 auto-fix proposal；path 不反向改 identity。
   - UnityResourceRef guid 缺失、guid 格式错误、missing asset、asset type mismatch、runtime provider missing 都有稳定 diagnostic code。
   - `allow_sub_asset=false` 时选择/导入 sub asset 是 capability error；开启 sub asset 时必须保存 local file id / name / type extension。
   - LocalizedTextRef 解析不到 text key 产生 `localization.key_missing`；required locale 缺失产生 `localization.locale_missing`。
   - localization fallback graph 有环产生 `localization.fallback_cycle` blocker。
   - locale 文本 token 与 schema token descriptor 不一致产生 `localization.token_invalid`。
   - preview_text 与 default locale 不一致只产生 `localization.preview_mismatch`，不改变引用 identity。
   - Expression<T> 字段必须在 import/convert 阶段 parse、bind、typecheck；parse/unknown symbol/type mismatch 分别产生稳定 diagnostic。
   - 未声明函数、非 deterministic 函数、外部状态访问和超预算表达式不能进入 Release bytes。
   - 表达式 canonical AST 相同但空白/格式不同不触发 runtime property_changed。
   - weighted selection 使用 child table entries；负权重、概率和不合法、空候选池分别产生稳定 validation diagnostic。
   - weighted selection runtime 必须接收显式 random stream；同 source、同 RNG state、同 condition context 结果 deterministic，且选择过程不产生 GC allocation。
7. 定义非破坏性 Excel 写回测试：
   - 修改单个数据 cell 不破坏 unknown columns。
   - 修改单个数据 cell 不破坏样式、公式、批注、筛选、冻结窗格、data validation。
   - 尝试覆盖 formula cell 时默认 blocker，除非 schema 明确允许。
   - metadata flush 失败时，涉及新 identity 的保存必须 blocker。
   - 写 workbook 前创建同目录 backup、temp workbook 和 recovery manifest。
   - temp workbook 写完后复读关键 metadata 和 patched cell/range。
   - replace 后再次 verify；verify 失败时 restore backup 或生成 manual recovery required report。
   - 平台不支持 atomic replace 时，report 标记 `atomic_replace=false` 并保留 backup。
   - mount 时发现遗留 temp/recovery manifest，生成 recovery diagnostic，不静默删除。
8. 定义 asset identity 与 path policy：
   - row guid/local id 是身份。
   - workbook guid 在同一 context / mount set 内唯一。
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
   - 新 Excel 行先临时 identity，成功 metadata flush 后才成为 stable identity。
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
   - key rename 后 guid 不变，`GUIDToAssetPath` 返回新 path。
   - `GetAssetPath(Object)` 对 resident/dirty/temporary draft 返回 current path，对 removed/unloaded/stale 返回空字符串并写 report。
   - stale old path 的 `LoadAssetAtPath` 不猜测 identity，并通过 report/event 指向 moved path。
   - `CreateAsset(asset, path)` 只有在 key path pattern 可反解时才能从 path 填充 key fields；否则必须显式禁用或报 `key.path_not_reversible`。
   - `CreateAsset` 在 editor draft 创建时预分配最终 row guid/local id 和派生 asset guid/local file id；SaveAssets 前后 guid/local id 不改变。
   - undo create 移除 draft row，但同一 editor context 内不复用 temporary row guid/local id；redo 恢复同一 temporary identity。
   - `MoveAsset(oldPath, newPath)` 同 workbook/table 内通过 key path 反解生成 key-field draft，row identity/guid/local id 不变。
   - `RenameAsset(pathName, newName)` 替换 final segment 后复用 MoveAsset 规则；newName 非法或 pattern 不可逆时失败并写 report。
   - 跨 workbook / 跨 table `MoveAsset` 默认失败并报告 `assetdb.move_forbidden`。
   - move/rename 目标发生 duplicate key 或 case-insensitive path collision 时不创建 draft。
   - move/rename 后 editor path index 使用 draft path，old path 变为 stale；`AssetPathToGUID(oldPath)` 返回空字符串并报告 `assetdb.path_stale`。
   - `CopyAsset(path, newPath)` 创建新 row identity，复制 schema data value；owned child 生成新 element identity，普通 references 默认保持原 target identity。
   - `GenerateUniqueAssetPath` 不分配 identity，不保留路径；默认 `_1`、`_2` suffix policy deterministic。
   - `TryGetAssetKey<T>` 对 duplicate、missing、stale path、类型不匹配返回 false，并报告 `key.lookup_ambiguous` 或对应 code。
   - `FindAssets("t:Type")` 返回 guid，按当前 asset path deterministic 排序。
   - `FindAssets(filter, searchInFolders)` 支持 folder/prefix scope；未 mount folder 返回空贡献并报告 `assetdb.search_folder_not_mounted`。
   - FindAssets filter 支持 Unity-like name / `l:` / `t:`，并扩展 `key:` / `table:` / `path:` / `guid:`。
   - FindAssets tokenization 支持 quoted token 和转义；语法错误返回空数组并报告 `assetdb.find_filter_invalid`。
   - FindAssets 同 family 多个 `t:` / `l:` token 使用 OR，不同 family 之间使用 AND。
   - FindAssets 不 materialize object，不 import，不修复 metadata，只查询 asset search index。
   - embedded child 不进入 FindAssets；schema 声明 findable 的 sub_asset 可以进入。
   - temporary editor draft asset 可在 Editor authoring 中查到，但不能进入 convert/runtime index。
   - `TryGetGUIDAndLocalFileIdentifier` 返回 deterministic asset guid/local file id；`GetInstanceID` 仍只在 context 内稳定。
10. 定义 SerializedProperty path / cell mapping 测试：
   - simple struct expanded columns 使用 `parent.child` property path。
   - repeated/list 使用 `Array.data[index]`。
   - single-cell struct 的子 property 修改会重写整个 cell。
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
   - repeated scalar 无 element identity 时 insert/delete/reorder 与外部同 array 修改冲突。
   - child table backed array insert 创建 temporary child identity，SaveAssets 后 flush stable identity；delete 不复用 element identity。
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
   - Phase 1 不开放 multi-object edit。
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
   - set_null/cascade 影响跨 workbook referrer 时，相关 workbook 进入同一 affected SaveAssets set，任一 blocker 不写任何 workbook。
   - leave_missing soft reference 保留 target identity snapshot，runtime 暴露 missing state，不伪装成 null。
   - key rename 不破坏引用，reference changed 更新 dependency graph 和 reverse dependency index。
12. 定义 converted bytes header / runtime index：
   - magic、format_version、schema_hash、source_hash、build_target、endianness、flags、file_size、section_directory、content_checksum。
   - hash 默认使用 SHA-256 over canonical bytes；report 中使用 lowercase hex。
   - section directory entry 包含 kind/version/flags/offset/length/element_count/element_size/section_checksum。
   - schema_manifest、string table、blob table、table directory、object data、identity/key/reference/dependency/reverse dependency index。
   - generated accessor manifest 记录 field id -> runtime field offset / string id slot / range slot / reference slot，并在 open/verify 时校验 descriptor hash。
   - v1 必需 section 缺失、重复、offset 越界、length 越界、section overlap、alignment 错误都是 open/verify blocker。
   - schema_manifest 保存 descriptor_hash、schema_hash、layout_hash、export_view_id/hash、source_hash、source_set_hash、build target/profile、tool version、table/field layout。
   - converted bytes header 区分 `full_source` 和 `patch_layer`；patch layer 必须有 layer_manifest 和 patch_operations。
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
   - preload_plan_index 只有 profile 声明预计算计划时输出；range/hash/digest 必须由 `verify-bytes` 校验。
   - repeated/child runtime getter 使用 count + indexer / `ReadOnlySpan<T>` / struct range view，不默认暴露热路径 `List<T>` / allocating `IEnumerable<T>`。
   - object record 区分 missing、explicit null、explicit value、default materialized。
   - `ConvertedBytesDataSource(byte[])` 默认不复制大 bytes，并把输入视为 immutable；debug 可用 checksum 发现被修改。
   - 同一 descriptor、metadata、exported data 在不同机器输出 byte-for-byte 一致。
   - source_hash 不包含样式、批注、列宽、筛选、helper/freeform、生成时间、本机路径。
   - source_hash 使用 canonical value；公式 cached value 被允许时同时包含 formula text、cached value 和 calc state。
   - row metadata 未 flush、duplicate row guid、duplicate key、hard reference missing、required runtime field missing 都是 convert blocker。
   - deterministic output，不依赖字典顺序、当前时间、本机路径。
13. 定义 mode / mutability 矩阵测试：
   - Release 下 ExcelDataSource、writeback、hot reload、metadata repair 明确失败。
   - Development build 下 hot reload/source switch 需要显式 opt-in。
   - Editor Play Mode 默认不把 runtime 修改写回 Excel。
   - mode 状态可查询并出现在 report 中。
   - OperationProfile 归一化顺序固定：built-in default、project descriptor、named profile、host/build overlay、explicit override。
   - CLI/API override 必须并入 canonical profile；相同 canonical profile 生成相同 operation_profile_hash。
   - gate-only policy 不进入 bytes cache key；输出相关 convert_profile 进入 convert_profile_hash 和 cache_key_hash。
   - Release profile 默认 require manifest、禁止 Excel source、禁止 dynamic migration 和 UnityEditor AssetDatabase。
14. 定义 RuntimeDatabase / DataSource facade 测试：
   - `Open` 成功返回 true，失败返回 false 且 context 保持 unopened。
   - `Close` 清空 currentSource/index/dependency graph，resident instances 进入 unloaded。
   - `SwitchDataSource` 失败时保留旧 source、旧 object graph 和旧 indexes。
   - `Refresh` 无变化、hot reload 关闭或 source 不支持 refresh 时返回 false 并写最小 report。
   - `ExcelDataSource(IEnumerable<string>)` 打开 workbook source set，并 deterministic 组合 source identity。
   - `ExcelDataSource(IEnumerable<string>)` 必须先生成 SourceSetDescriptor；同一 workbook set 不受传入路径顺序影响。
   - `CompositeDataSource` 按 SourceLayerDescriptor 构建 materialized view；稳定读取不沿 layer 链查找。
   - layer stack 没有唯一 base、layer order 不稳定或同 priority 覆盖同 diff key 时产生 source layer diagnostic，保留旧 source。
   - patch layer target base/source hash 不匹配时产生 `source.layer_base_mismatch`，不应用补丁。
   - Release 下 Excel layer、development override、unsigned required patch 都明确失败。
   - 跨 workbook hard reference 存在时，source set candidate 任一 blocker 使整个 runtime hot reload 不替换 object graph。
   - Release mode 下 `ExcelDataSource` 和 `EnableHotReload` 明确失败或 no-op + report。
   - `LoadAsset<T>` 找不到、类型不匹配、asset missing 时返回 null。
   - `TryGetAssetKey<T>` 在初始化期把 key/path 解析为 `AssetKey`。
   - `TryGetAssetKey<T>` 只有唯一有效 key/path 命中时返回 true；duplicate、stale path、missing、type mismatch 返回 false 且不创建临时 object。
   - `TryGetAsset<T>(AssetKey)`、`TryGetAsset<T>(AssetIdentity)` 和 `GetAssets<T>(Span<T>)` 不产生 GC allocation。
   - `GetDependencies(AssetIdentity, Span<DependencyTarget>)` 和 recursive `DependencyQuery` 不产生 GC allocation；buffer 不足时返回 `RuntimeQueryStatus.Truncated`，不为 truncation 分配 report/list/string。
   - source switch 后仍存在的 `AssetKey` 能重绑定到同一 identity；目标删除时返回 false 并有 ChangeSet/report。
   - path-based converted bytes 更新后 `Refresh` 能按 source switch 语义 patch。
   - memory bytes source 的 `Refresh` 默认 no-op，必须通过 `SwitchDataSource` 切入新 bytes。
   - source switch / hot reload commit 先生成 patch plan，commit 前验证 current source revision、schema hash、identity map 和 registry manifest 仍匹配。
   - patch plan stale 产生 `runtime.patch_plan_stale`，保留旧 source 和旧 object graph。
   - patch existing resident object 失败产生 `runtime.patch_failed`，必须 rollback staged field/index changes，不发布成功 ChangeSet。
   - event buffer 超过水位线产生 `runtime.event_buffer_overflow` 或 allocation summary，不能静默丢事件。
   - `changed` 发布完整 ChangeSet，`objectChanged` 只是便利事件且不表达 removed/dependency closure。
   - watcher refresh 进入 owner operation queue，不能从 watcher/background 线程直接 commit。
   - `ChangeSet.events` non-alloc view 只在 callback 栈内有效；跨帧保存必须复制 stable identity/code。
   - `Reserve` / `Prewarm` 后 runtime 热路径不产生 GC allocation。
15. 定义 dirty / undo / save / conflict 状态机测试：
   - `ApplyModifiedProperties` 后进入 dirty_editor。
   - 新行临时 identity 后进入 dirty_metadata。
   - editor 和 Excel 修改不同 property 时自动 merge。
   - editor 和 Excel 修改同一 property 时进入 conflict。
   - conflict_id 按 workbook/table/row/diff_key/revision/reason_code deterministic 生成，report 排序不依赖发现顺序。
   - conflict record 包含 conflict_state、conflict_kind、diff_key、base/editor/external revision、mapping revision、reason_code、suggested_actions。
   - enum 按 value id 判断冲突，object reference 按 row guid/local id 判断冲突。
   - expanded struct 不同 child field 可以自动 merge。
   - single-cell struct 不同 child property 默认冲突，除非 schema codec 支持 child-level lossless merge。
   - repeated/list 无 stable element identity 时，insert/delete/reorder 与同 array 其他修改冲突。
   - editor 修改 row 而 Excel 删除 row 时进入 conflict。
   - editor 删除 row 而 Excel 修改 row 时进入 conflict。
   - editor 和 Excel 同时新建相同 key 但不同 identity 时进入 duplicate key conflict。
   - `reload from Excel` 丢弃对应 diff 并以 external snapshot rebase。
   - `keep editor value` 以 external snapshot 为新 base 重新写入 editor draft，进入 resolved_pending_save。
   - `manual resolve` 的 final value 必须重新 parse/normalize/validate 后写入 draft。
   - resolver 打开后 source/row/mapping/schema/layout revision 改变时，旧 resolution 变 stale，不能写入 draft。
   - conflict report 包含 conflict_id、asset_identity、field_id、property_path、base/editor/external value 和 cell location。
   - unresolved conflict 时 `SaveAssets` 拒绝保存。
   - stale conflict 或 resolved_pending_save 的 base revision 过期时，`SaveAssets` 必须重新 merge 或拒绝保存。
   - `SaveAssets` 对所有 affected workbook 做 preflight，任一 blocker 默认不写任何 workbook。
   - affected workbook set 包含跨 workbook hard reference、cascade/set_null delete、owned child table、resolved conflict draft、metadata repair 和 auto-fix proposal。
   - save partial failure 保留失败 workbook 的 dirty draft，并在 report 中列出 saved/skipped/failed/restored。
   - source set 复读验证通过后，resolved_pending_save 才能进入 resolved_saved 并清理 draft。
   - `Undo` 只回退 editor draft，不回退外部 Excel 保存。
   - `ApplyModifiedProperties` 形成 undoable edit unit；undo/redo 不直接写 workbook。
16. 定义 workbook version-control / review / merge artifact 测试：
   - `snapshot` 对同一 workbook 在不同机器输出 byte-for-byte 相同 canonical JSON。
   - snapshot 记录 workbook_guid、schema/layout/source hash、metadata checksum、row/value/reference canonical records 和 snapshot_hash。
   - workbook 改动后未更新 snapshot 时，CI 产生 `vcs.snapshot_stale`。
   - snapshot 缺失且 project policy 要求提交 snapshot 时，产生 `vcs.snapshot_missing`。
   - `diff-workbook` 能输出 row add/delete/key rename/property/reference/dependency/helper changes，且不把 key rename 拆成 delete+add。
   - `merge-workbook` 对不同 row、不同 scalar field、expanded struct 不同 child field 自动合并。
   - `merge-workbook` 对同一 diff key 不同 normalized value 产生 `vcs.merge_conflict`，不写 merged xlsx。
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
   - Excel `~$` lock/temp 文件和 ExcelDB backup/temp/recovery 文件被忽略，并报告 `watcher.ignored_temp_file`。
   - debounce 后至少两次 stat 的 size/write-time 稳定才尝试读取；持续变化产生 `watcher.file_unstable`，保留旧数据。
   - 文件锁定时按 bounded backoff retry，超限后报告 `watcher.file_locked` 并保留旧数据。
   - read-verify 校验 xlsx package、metadata header/checksum；半写入或损坏产生 `watcher.read_verify_failed`。
   - fingerprint 未变化时丢弃 watcher 事件。
   - size/write-time 变化但 source_hash 未变化时只产生 `watcher.fingerprint_noop`，不发布 property_changed。
   - manual `AssetDatabase.Refresh()` flush watcher queue，但仍执行 stability/read-verify，不读半写文件。
   - 同一 debounce 窗口内多个 workbook 变化组成 source set candidate。
   - source summary report 列出 affected workbook guids、paths、source hash before/after。
   - watcher report 包含 watcher_events、ignored_paths、retry_summary、fingerprint_before/after、settle_time_ms。
   - `AssetDatabase.Refresh()` 不绕过 transaction/report。
   - editor dirty 存在时 refresh 走 merge/conflict，不覆盖 draft。
   - 后台 import 可以构建 candidate/report，但 resident object graph 只在 owner thread publish point patch。
   - 同一帧 publish point 前后 revision 一致，commit 不与 `GetAssets` / dependency traversal 调用交错。
   - operation queue overflow 时 coalesce 重复 refresh，不丢 explicit open/switch/close，并写 diagnostic。
18. 定义 platform adapter / assembly 边界：
   - `ExcelDbEngine`、schema、source、convert pipeline 不引用 UnityEngine / UnityEditor。
   - Unity 2022.3 adapter 是首个 adapter，负责 UnityEditor 接入、UnityResourceRef、asset picker、Play Mode 调数和构建集成。
   - 非 Unity adapter 通过 reference family、source、editor facade、validator、converter extension 扩展。
19. 新建 `ExcelDbEngine`、`ExcelDbEditor`、Unity 2022.3 adapter 的项目或 namespace 边界。
20. 定义 `Object`、`ScriptableObject`、`AssetDatabase` 的最小 API surface：
   - 同一 context 内同一 asset identity + runtime type 重复加载返回 canonical resident instance。
   - `GetInstanceID()` 是 context-local，不写入 workbook / converted bytes。
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
   - `SaveAssets` 先做所有 dirty workbook preflight；blocker 时不写任何 workbook。
   - `LoadAssetAtPath<T>` 找不到、类型不匹配或 stale path 返回 null，并写 report。
   - `FindAssets` 返回 guid，不 materialize object，并按 current asset path 排序。
   - `CreateAsset` 只创建 editor draft row，`SaveAssets` 成功后才 flush stable identity。
   - `MoveAsset` / `RenameAsset` 只创建 editor draft key-field diff；成功返回空字符串，失败返回错误文本并写 machine-readable report。
   - `CopyAsset` 创建新 editor draft row，不能复制 row identity、metadata revision 或 diagnostic projection。
   - `GenerateUniqueAssetPath` 只查询当前 path index，不创建 asset、不分配 identity。
   - `DeleteAsset` 默认标记删除，reference restrict 时返回 false 并列出 referrer。
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
   - 对 child table 模拟 insert/delete/reorder，断言 parent/child identity 和 ChangeSet 正确
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
   - `schema-lint` 输出 canonical descriptor artifact，并验证 descriptor_hash/schema_hash/layout_hash/codegen_hash deterministic。
   - `schema-lint` 检查 reserved id reuse、alias collision、runtime type/property path binding collision、custom extension host requirement/deterministic flag。
   - `workbook-check` 执行 import、compatibility、validation、reference graph，但不输出 bytes。
   - `clone-workbook` 生成新 workbook guid、remap self references、保留 external references，source workbook 不变。
   - `repair-workbook-guid --as-new-source` 对 duplicate workbook guid 生成新 source identity，并走 backup/temp/verify/reimport。
   - `snapshot` 输出 canonical WorkbookSnapshot，CI 能检查 snapshot missing/stale。
   - `diff-workbook` 输出 WorkbookDiff，review gate 不读取 human markdown 判断逻辑。
   - `merge-workbook` 使用 base/ours/theirs 三方输入，冲突时只输出 conflict artifact，不写 merged xlsx。
   - `migrate --dry-run --plan-out` 输出 canonical MigrationPlan、plan_hash、candidate diff、data loss confirmation 和 recovery preflight，不写 workbook。
   - `migrate --apply --plan` 验证 plan_hash、dry_run_report_hash、source fingerprint/revision、descriptor hash 和 confirmation hash 仍有效，执行 backup/temp/verify/replace/reimport，并写 migration history。
   - `convert` 输出 bytes、artifact manifest、machine-readable report。
   - `verify-bytes` 校验 header、artifact_kind、content checksum、bytes_hash、section directory、section checksum、schema manifest、schema/source/source_set/layer_stack/materialized_source/preload_plan hash、identity/key/reference/dependency/preload_plan digest、layer_manifest/patch_operations 和 runtime index。
   - `verify-source-set` 校验 source layer order、activation、trust/signature、base target hash、layer_stack_hash 和 materialized_source_hash。
   - `benchmark-no-gc` 使用 capacity.json 和 allocation policy，输出包含 measurement_window、gc_bytes、watermark_growth_entries、hot_path_allocation_entries、budget_result 的 allocation_summary。
   - 所有命令接受 `--profile`，省略时使用命令默认 profile；report/manifest 记录 operation_profile_id/hash 和 gate_policy_hash。
   - exit code 0/1/2/3/4/5 分别对应 success、warning policy、error、blocker/data loss、command/env、internal error。
   - artifact manifest 使用 canonical JSON，记录 cache_key_hash、descriptor_hash、schema_hash、layout_hash、codegen_hash、export_view_id/hash、source_set_hash、source_set_descriptor_hash、layer_stack_hash、materialized_source_hash、preload_plan_hash、bytes_hash、identity/key/reference/dependency/preload_plan digest、source_layers、input_workbooks、build_target/profile、operation_profile_id/hash、gate_policy_hash、convert_profile_hash、converter/runtime provider registry hash、localization_manifest_hash、expression_registry_hash。
   - build cache key 包含 schema/source/profile/tool/runtime provider/converter registry/Unity runtime dependency hash/localization manifest hash/expression registry hash；不包含本机路径、human report、输出路径、main_asset_path 展示变化。
   - cache hit 必须重新校验 bytes_hash、artifact_id、schema_manifest、section directory 和 identity/key/reference/dependency/preload_plan digest；校验失败重新 convert。
   - Unity resource dependency manifest 记录 guid、main_asset_path、asset_type、runtime_provider、runtime_key、dependency_kind、referenced_by_assets、source_locations、runtime/display dependency hash。
   - 单纯 main_asset_path 变化只刷新 display hash 和 manifest/report，不要求 Release bytes cache miss。
   - Release build 下 exported UnityResourceRef 必须有明确 runtime provider；不能依赖 main_asset_path 兜底加载。
   - Unity Release build 默认只打包 converted bytes，不打包原始 Excel。
   - runtime 打开的 bytes 与 manifest 不匹配时 open 失败。
   - runtime manifest 缺失时 startup report 标记 `manifest_missing=true`；Release policy 可禁止。
27. 做 runtime GC allocation budget 测试：
   - 初始化阶段的 source open、首次批量导入和水位线增长允许分配
   - `RuntimeDatabase.SetAllocationPolicy` 能切换 `AllowWatermarkGrowth`、`ReportWatermarkGrowth`、`FailOnWatermarkGrowth`、`FailOnAnyHotPathAllocation`
   - allocation_summary 记录 policy、measurement_window、gc_bytes、gc_alloc_count、watermark_growth_count、profiler_backend、sample_reliable、budget_result
   - 预热到水位线后，`LoadAsset`、`TryGetAsset(AssetKey)`、`TryGetAsset(AssetIdentity)`、引用解析、key lookup、依赖遍历不产生 GC allocation
   - 预热到水位线后，generated scalar getter、string getter、reference getter、optional/default state accessor 不产生 GC allocation
   - 预热到水位线后，converted bytes 字段读取、string table 访问、repeated/child table 遍历不产生 GC allocation
   - 预热到水位线后，weighted selection evaluate/select 使用显式 random stream 和 baked table，不构造临时候选集合，不产生 GC allocation
   - repeated/child 热路径使用 count + indexer / `ReadOnlySpan<T>` / struct range view；`List<T>`、`ToArray()`、allocating `IEnumerable<T>` 只能作为显式低频 API
   - 预热到水位线后，ExcelDataSource refresh 的 parser workspace、candidate snapshot、hot reload patch、source switch commit、change event 分发不产生 GC allocation
   - 预热到水位线后，owner operation queue、pending ChangeSet、publish dispatch 不产生 GC allocation
   - 数据规模超过水位线时允许一次扩容，必须产生 `allocation_summary.watermark_growth_entries[]`，再次以相同容量和相同 source revision 运行时不再分配
   - `fail_on_watermark_growth` 下水位线增长使 benchmark gate 失败；`fail_on_any_hot_path_allocation` 下任意未登记 hot path allocation 产生 `runtime.allocation_budget_exceeded`
   - measurement window 内不构建 human-readable report、日志字符串或分配式 assertion message
   - change event 使用 static subscription / indexed view 或 struct enumerator 验证不分配
   - 热路径禁止 LINQ、闭包捕获、装箱、反射字段访问、临时字符串和 per-event/per-property 临时对象
   - `sample_reliable=false` 的 profiler/backend 结果不能作为 CI 通过证据
   - Unity 2022.3 adapter 用 Unity profiler/GC allocation 测试验证
   - 核心 runtime 用宿主无关 benchmark 或 allocation counter 验证
