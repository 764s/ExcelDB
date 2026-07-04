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
- 高性能是宏观目标：除初始化阶段的 source open / 首次批量导入和水位线增长以外，运行时稳定热路径应避免产生 GC allocation。

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
   - 初始化阶段的 source open、首次批量导入、convert、schema generation、import report 生成可以分配内存。
   - 水位线增长可以分配内存；水位线指对象池、索引、事件缓冲、diff buffer、临时工作区等达到历史最大容量时的扩容。
   - 达到水位线后的运行时热路径应避免 GC allocation，包括 `LoadAsset`、引用解析、key/path 查找、遍历依赖、读取字段、source switch commit、hot reload patch 和 change event 分发。
   - 性能设计优先使用预分配、对象池、stable buffers、batch/coalesce 事件和增量索引，避免 LINQ、闭包、装箱、临时字符串、反射热路径和 per-row/per-property 临时对象。

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
    Console.WriteLine($"{iterator.propertyPath} = {iterator.DisplayValue}");
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
RuntimeDatabase.objectChanged += obj =>
{
    if (obj is SkillConfig skill)
        SkillRuntimeCache.Rebuild(skill);
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
- `CompositeDataSource`：可选，支持基础包 + patch 包 + override。

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
- `single_cell` 模式必须声明稳定文本格式、分隔符/转义规则、示例值和 parse error 诊断。
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

真实实现不一定照这个 proto 字段命名，但需要覆盖这些语义。

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
- 定义 workbook region ownership：schema-owned structure、data、helper/freeform、metadata 的边界和写入规则。
- 建立 schema hash、table id、field id、row guid/local id 的 metadata 规范。
- 定义 missing column、unknown column、field rename、table rename、type change 的兼容策略。
- 定义 required/default/nullability、validation severity、convert blocker。
- 定义 enum 与 simple struct contract：enum 可选值、显示/导出值、simple struct 单 cell / 多列展开。
- 定义 reference family：内部资产引用、Unity asset 引用、插件外部引用分别有身份和诊断规则。
- 定义 Unity 2022.3 adapter 的 `UnityResourceRef` contract：guid 为身份，main_asset_path 为展示。
- 定义 platform adapter boundary：Unity 2022.3 adapter 是首个实现，核心 schema/source/runtime 不依赖 UnityEngine / UnityEditor。
- 定义 Excel layout 与 unknown preservation 的最低协议。
- 定义由 schema 生成的 Excel 结构行和表头注释：中文名、字段名、类型、导出端、规则、说明、枚举可选值、结构体填写格式。
- 定义 schema 管辖区域和策划辅助区域：前者由 schema 生成/校验，后者在不影响导入导出时保留。
- 定义 operation transaction/report：表结构生成、metadata flush、import、save、convert、source switch、hot reload 都要能 dry-run、风险分级和原子提交。
- 定义 converted bytes schema compatibility rule。

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
- rename sheet 不丢 table。
- rename field 可通过 field id/alias 迁移。
- Unity resource path rename 不改变引用身份，guid 缺失/无效会产生诊断。
- 核心 Engine/schema/source 能在没有 Unity assemblies 的环境下成立；Unity 2022.3 相关依赖只出现在 Unity adapter。
- 旧编辑器保存 unknown column 不丢数据。
- 新 schema 加 optional field 可打开旧表并补默认。
- 新 schema 加 required field 可打开旧表，但 convert bytes 被 blocker 阻止。
- converted bytes schema hash 不兼容时 runtime open/switch 失败且保留旧 source。

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
- 支持 schema hash/version 校验。
- 支持按类型和 key 加载。
- 支持引用解析和依赖预加载。
- 支持 `ExcelDataSource` 与 `ConvertedBytesDataSource` 热切换。
- 支持 converted bytes 更新后热切换到运行中的数据。
- 定义新增、删除、修改、key 变化、引用变化的热切换事件。
- 定义 runtime GC budget：稳定水位线后的读取、查找、引用解析、source switch commit、hot reload patch、事件分发避免 GC allocation。

验收：

- 编辑期 Excel 数据和运行时 converted bytes 数据读取结果一致。
- 业务层可从 `ExcelDataSource` 切到 `ConvertedBytesDataSource`，读取 API 基本不变。
- runtime 默认只读，修改 API 不可用或明确失败。
- 热切换后已加载对象尽量保持 identity，不要求业务层重新获取所有引用。
- 切回 converted bytes 后能验证正式包数据与 Excel 调试数据是否一致。
- 在已完成初始化和水位线预热后，常规 `LoadAsset`、引用解析、key lookup、依赖遍历和 hot reload patch 的测试不产生 GC allocation。

### Phase 8：扩展体系

目标：

- validator 注册。
- property drawer 注册。
- Excel layout planner。
- schema migration。
- custom metadata 使用规范。

验收：

- 项目可为字段定义自定义校验和编辑展示。
- schema 变更后可迁移已有 Excel。
- `layout` / `meta` 从 proto option 真正参与编辑和导入导出。

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

- 需要命令行工具，不只是编辑器按钮。
- 需要 deterministic output，否则版本库会噪音很大。
- 需要 CI-friendly report，例如 json + human readable。
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
- metadata 冲突修复工具。
- duplicate row guid 修复工具。
- key rename / row move / copied row 的辅助诊断。

实操影响：

- 多人改 Excel 是必然。
- 只依赖 Git 二进制冲突处理不够，需要工具层修复身份冲突。

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
- 水位线预热机制：对象池、事件缓冲、diff buffer、索引、临时解析工作区可以主动 reserve。
- workbook/sheet/row 级增量 import。
- property-level diff。
- 大表索引。
- 大量 object changed event 的 batch/coalesce。
- converted bytes lazy loading。
- zero-allocation read path：`LoadAsset`、引用解析、key lookup、依赖遍历、字段读取不分配。
- zero-allocation hot reload commit path：patch、事件分发、索引更新尽量复用 buffer。
- GC allocation profiler test：在 Unity 2022.3 和核心 .NET 测试中都要能验证。

实操影响：

- 小样例全量 reload 没问题，大项目几万行配置会拖慢 Play Mode 反馈。
- 运行时配置读取和热更新如果持续产生 GC，会把调数便利性转化成帧时间抖动，尤其在战斗、AI、UI 刷新和资源预加载高频路径里很明显。

边界说明：

- 允许初始化阶段的 source open、首次批量导入、convert、report 构建、diagnostic 字符串生成分配内存。
- 允许超过历史容量时水位线增长并产生分配，但应可观测、可预热、可通过 report 暴露。
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
   - 达到水位线后的运行时读取、查找、引用解析、依赖遍历、source switch commit、hot reload patch、事件分发应避免 GC allocation。
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
- 可选 generated helper sheet：例如 enum 大量可选值说明、diagnostic summary；这些 sheet 必须有 metadata 标记，不能被当作 table sheet。

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

### 14.4 默认 metadata 格式

`__ExcelDB_Metadata` 默认保存四类记录，格式可以是多块 table 或同 sheet 分区，但字段语义必须稳定：

1. workbook record：
   - workbook guid。
   - schema hash。
   - layout hash。
   - generator version。
   - last successful import revision。

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

hash 规则：

- `schema hash` 只覆盖会影响 import、export、runtime 解释、reference、validation blocker 的语义。
- `layout hash` 覆盖 display name、description、header comment、列顺序、冻结窗格、筛选、Excel authoring layout。
- 只改注释、中文名或表头说明不应导致 converted bytes schema hash 不兼容，但可以触发布局刷新。

### 14.5 默认 operation transaction 流程

所有危险操作默认走同一流程：

1. Analyze：只读 workbook/source/runtime state，读取 schema 和 metadata。
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

### 14.6 默认 runtime no-GC 实现规则

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
- 热路径不用闭包捕获、装箱、临时字符串拼接、按字段创建临时对象。
- 对外遍历 API 提供 struct enumerator 或 caller-provided buffer 版本。
- change event 默认 batch/coalesce 到预分配 `ChangeSet`。
- key/path 在 import/bake 阶段完成 hash/intern/cache；runtime lookup 不构造新字符串。
- 超过水位线时可以扩容，但必须记录 allocation report，并允许用户预热或 reserve。

### 14.7 默认 source switch / hot reload 语义

source switch 和 hot reload 默认都按 candidate snapshot 处理：

- 先完整导入 candidate source。
- 校验 schema hash、source hash、identity map、reference graph、validation blocker。
- 在 commit 前完成必要 buffer reserve；如果触发水位线增长，本次 allocation 合法，但必须可观测。
- commit 时优先 patch 已有 object，不重建可保持 identity 的对象。
- 删除的 object 进入 missing/unloaded 语义，不让旧引用静默指向新对象。
- 新增 object 分配 stable identity 后加入 index。
- key/path 变化触发 moved/renamed 事件，不改变 row guid/local id。
- 事件发布顺序固定为 removed、added、moved/renamed、property changed、dependency changed、table/source summary。

如果 candidate 失败：

- 旧 source 继续服务。
- 旧 object graph 不变。
- 不发布成功事件，只发布失败 report。
- editor/development build 可以显示失败诊断；正式 runtime 默认只保留机器可读错误码和最小日志。

### 14.8 未细化事项的默认解法

当文档没有写到某个细节时，默认按下面顺序选择最自然方案：

- 能保留用户 Excel 内容，就不重建整表。
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
   - field display/type shape/aliases/default/required/export/layout/description/header comment。
   - enum schema：value id/name/display/aliases/deprecated/description/export token。
   - simple struct schema：subfields、single cell format、expanded columns layout。
   - reference nullable/ownership/delete policy。
   - Unity adapter reference family：UnityResourceRef guid/main_asset_path/asset_type/path_display policy。
   - validator severity/mode。
2. 定义 workbook metadata sheet 格式：
   - 使用 `__ExcelDB_Metadata`。
   - workbook record：workbook guid、schema hash、layout hash、generator version、last successful import revision。
   - table record：table id、schema name、current sheet name、table version、data start row、table kind/export policy。
   - field record：table id、field id、field path、column index、value shape、parent field id、deprecated/reserved。
   - row record：table id、row guid/local id、current row number、key snapshot、row revision、source hash/cell range hash。
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
4. 做 schema compatibility 测试：
   - rename sheet。
   - rename field。
   - missing optional column。
   - missing required column。
   - unknown column preservation。
   - regenerate structure 不清空正式数据行。
   - regenerate structure 保留不影响导入导出的策划辅助信息。
   - blocker 级结构错误不写回 workbook。
   - generation report 区分 safe/warning/error/blocker。
   - enum 字段生成表头注释和 dropdown，重新生成后仍由 schema 控制。
   - simple struct 字段在 single cell 与 expanded columns 两种布局下都能保留数据并正确导入导出。
   - 只改 description/header comment 会更新 layout hash，但不导致 converted bytes schema hash 不兼容。
   - Unity resource path rename but same guid remains valid。
   - Unity resource missing/empty guid reports diagnostic。
   - incompatible converted bytes schema hash。
5. 定义 asset identity 与 path policy：
   - row guid/local id 是身份。
   - key 是人类可读定位。
   - key rename 不改变 object identity。
   - asset path 变化触发 moved/renamed 事件。
6. 定义 platform adapter / assembly 边界：
   - `ExcelDbEngine`、schema、source、convert pipeline 不引用 UnityEngine / UnityEditor。
   - Unity 2022.3 adapter 是首个 adapter，负责 UnityEditor 接入、UnityResourceRef、asset picker、Play Mode 调数和构建集成。
   - 非 Unity adapter 通过 reference family、source、editor facade、validator、converter extension 扩展。
7. 新建 `ExcelDbEngine`、`ExcelDbEditor`、Unity 2022.3 adapter 的项目或 namespace 边界。
8. 定义 `Object`、`ScriptableObject`、`AssetDatabase` 的最小 API surface。
9. 将现有 `ConfigDatabase` 的 transaction、undo、loader、dependency graph 作为内部实现资产接入新模型；对外 API 不泄漏旧表模型。
10. 做一个 CharacterConfig 的端到端测试：
   - mount workbook
   - load asset at path
   - SerializedObject 修改 hp
   - undo
   - save
   - reread workbook
11. 再做一个 repeated RowRef 的 SerializedProperty 测试。
12. 做一个 Play Mode 热更新测试：
   - 使用 `ExcelDataSource` 启动
   - load 一个 `SkillConfig`
   - 模拟 Excel 修改伤害
   - hot reload
   - 断言同一个 object 实例读到新值
   - 断言 object changed 事件触发
13. 做一个 Excel / converted bytes 热切换测试：
   - 从 `ConvertedBytesDataSource` 启动
   - 切到 `ExcelDataSource`
   - 修改 Excel 并 hot reload
   - convert bytes
   - 切回 `ConvertedBytesDataSource`
   - 断言 identity、引用和 key index 一致
14. 做 runtime GC allocation budget 测试：
   - 初始化阶段的 source open、首次批量导入和水位线增长允许分配
   - 预热到水位线后，`LoadAsset`、引用解析、key lookup、依赖遍历不产生 GC allocation
   - 预热到水位线后，hot reload patch、source switch commit、change event 分发不产生 GC allocation
   - Unity 2022.3 adapter 用 Unity profiler/GC allocation 测试验证
   - 核心 runtime 用宿主无关 benchmark 或 allocation counter 验证
