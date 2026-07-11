# 模块 7:运行时

状态：**Provisional**。本文是 M7 运行时领域的唯一 owner；跨模块权威规则、状态和开放决策见 [`docs/spec/README.md`](../spec/README.md)。本文定义宿主无关运行时的最小权威契约。归档计划中的 `Object`/`ScriptableObject` 基类无规范效力；M1 生成的普通 C# 类是数据投影,资产身份、resident/missing 状态和 source 归属由运行时注册表承载。后续引用本文记作 M7§x。

## 1. 范围与硬边界

- 范围:`IDataSource`、generated `RuntimeSchemaRegistry`、`RuntimeDatabase`、resident/missing、converted bytes、`ChangeSet`、Open/Switch/Refresh 事务、模式边界、引用 provider 与 runtime hot-path no-GC。
- Core 宿主无关:`ExcelDbEngine`、schema descriptor、source contract、运行时索引与事务不得引用 UnityEngine/UnityEditor。Unity 2022.3 只通过 adapter 接入。
- 运行时默认只读。workbook 写回、dirty/Undo、结构生成与 metadata 修复属于 Editor authoring,不因 source 是 Excel 而进入 RuntimeDatabase。
- 生成类型是普通 C# 类,不继承库基类,可以由 codegen 工厂或普通 `new` 创建。有效 resident 实例只能由当前 runtime session 的注册表认领;业务自行创建的实例不自动成为资产。
- `Object.name`、`GetInstanceID()`、Unity 式 null operator、`ScriptableObject.CreateInstance` 不属于运行时契约。key、identity、状态查询均走生成成员或 RuntimeDatabase/引用 facade。

## 2. 身份、handle 与 resident 状态

```csharp
public readonly struct AssetIdentity { /* opaque;物理表示待身份模块裁定 */ }
public readonly struct AssetKey      { /* session-local table/slot/generation */ }

public enum RuntimeAssetState : byte { Resident, Missing }
```

- `AssetIdentity` 是跨 Excel/bytes source 稳定的 opaque 资产身份;具体物理表示归 M5 OD1,本模块不得把它提前收窄为 row guid、`(table_id,row_guid)` 或其他组合。key、路径、行号不是 identity。
- `AssetKey` 是当前 RuntimeDatabase session 内的热路径 handle,含 generation 防止行槽复用后旧 handle 命中新资产。切源成功后能继续解析的旧 handle 必须仍指同一 `AssetIdentity`;不能证明时判失效,禁止误命中。
- 每个 `(AssetIdentity,生成类型)` 在一个 session 中至多一个 canonical resident 实例。重复加载与内部引用解析返回同一实例。
- 每个 session 绑定且只绑定一次由当前生成代码提供的 `RuntimeSchemaRegistry`;registry 的 expected SchemaHash、表/type/factory/accessor 绑定共同定义“这份代码期望的数据结构”。成功 Open 后该 registry 在 Close 前固定,Switch/Refresh 不得替换或重解释它。
- 状态由运行时注册表侧表承载,不向生成类注入基类字段。RuntimeDatabase 必须提供按实例或 identity 查询 `RuntimeAssetState` 的无歧义入口;具体成员名在 API surface 冻结前仍为 Provisional。
- 行从新 source 消失时,原实例转 `Missing`:从 key/path/枚举查询剔除,字段重置为 schema materialized 默认值,内部引用 `TryGet` 返回 false,并发布 Removed/DependencyChanged。旧引用不得静默指向另一行。
- 同一 identity 后续重新出现且生成类型兼容时,复用原实例、patch 新值并转回 Resident;发布 Added 及必要 PropertyChanged/DependencyChanged。类型或 layout 无法安全 patch 时走 Recreated,不得伪装普通属性修改。
- Close 后不再允许查询 source 数据;旧 `AssetKey` 失效。POCO 已被业务持有时仍只能通过状态侧表判断有效性,不能依赖字段值猜 Missing。

## 3. DataSource contract

`IDataSource` 表示同一 canonical schema/data model 的一种只读来源。具体接口可以分层实现,但必须满足以下语义:

```csharp
public interface IDataSource
{
    ulong SchemaHash { get; }
    SourceInfo Inspect();              // 只读元信息与能力
    SourceSnapshot Open();             // 构建 candidate;不得发布全局状态
}
```

- Open 产出不可见的 candidate snapshot:规范化行值、identity/key 索引、引用与依赖图、source revision/content hash、诊断。candidate 在 commit 前不得修改当前 source、resident 实例或索引。
- source 必须声明 read/refresh/watch 能力;RuntimeDatabase 不从 source 类型推断写权限。
- `ConvertedBytesDataSource` 是所有模式可用的正式运行时来源。
- `ExcelDataSource` 仅允许 EditorAuthoring，以及经 session bootstrap/API 明确选择的 EditorPlayDebug 与 Development；只提供 read/refresh/watch,不提供 workbook writeback。Release 即使显式请求也必须拒绝。
- 自定义 source 只能通过 `IDataSource` 和 canonical descriptor 接入,不得改变 identity、引用、事务或 ChangeSet 语义。
- source 声明及 candidate 实际读到的 schema hash 必须相同,并且都必须等于 Open 显式传入、随后由 session 固定的 generated `RuntimeSchemaRegistry.ExpectedSchemaHash`;格式版本、hash 或完整性任一校验失败时 Open 失败,作为 Switch/Refresh candidate 时保留旧 source。禁止从 source 自身、manifest、Project、cache 或“最近一次成功值”反推代码期望 hash。
- source 中任一 runtime-exported 行存在 M5§9.2 所述尚未固化、空缺或仅会话内生成的 pending identity,一律产生 `identity.pending` blocker。Open 必须保持 closed,Switch/Refresh 必须保留旧 source/object graph/index;RuntimeDatabase 不得分配、修复、容忍或降级为 warning,任何模式和 opt-in 都不能绕过。identity 只能先在 authoring 侧固化并重新生成/打开 source。

## 4. RuntimeDatabase 门面

以下是语义面,具体重载集在实现冻结时以不降低本节契约为前提收口:

```csharp
// 由 M1 codegen 生成具体实现/单例;调用方不能只用 source hash 临时拼装。
public abstract class RuntimeSchemaRegistry
{
    public abstract ulong ExpectedSchemaHash { get; }
    // 生成的 table id ↔ CLR type/factory/accessor/patcher 绑定。
}

public readonly struct RuntimeBootstrapOptions
{
    public readonly RuntimeMode Mode;
    public readonly bool EnableHotReload;

    public RuntimeBootstrapOptions(RuntimeMode mode, bool enableHotReload)
    {
        Mode = mode;
        EnableHotReload = enableHotReload;
    }
}

public static class RuntimeDatabase
{
    public static bool Open(
        IDataSource source,
        RuntimeSchemaRegistry registry,
        in RuntimeBootstrapOptions options);
    public static void Close();
    public static bool SwitchDataSource(IDataSource source);
    public static bool Refresh();
    public static void EnableHotReload();
    public static void DisableHotReload();
    public static void Prewarm();

    public static T? LoadAsset<T>(string key) where T : class;
    public static bool TryGetAssetKey<T>(string key, out AssetKey key) where T : class;
    public static bool TryGetAsset<T>(AssetKey key, out T asset) where T : class;
    public static RuntimeQueryStatus GetAssets<T>(Span<T> buffer, out int count) where T : class;

    public static RuntimeMode mode { get; }
    public static event Action<ChangeSet> changed;
}
```

- `Open` 必须同时显式取得初始 `source`、generated `RuntimeSchemaRegistry` 与 `RuntimeBootstrapOptions`;三者都不可省略。允许宿主 facade 把 generated registry 静态绑定后提供等价入口,但该入口必须不可被未生成/调用方自造的 hash 替代,也不得从 source、manifest、Project、cache 或环境状态推导 expected hash。
- `source` 是 session 初始源的显式选择；`RuntimeBootstrapOptions.Mode` 是模式的显式输入，`EnableHotReload = true` 是初始热载的显式 opt-in。增加 registry 参数不改变这些选择规则。session 建立后的源变化只经 `SwitchDataSource`，热载变化只经 `EnableHotReload`/`DisableHotReload`。
- generated registry 是不可变注册工件。成功 `Open` 捕获 registry 引用、`ExpectedSchemaHash` 与生成绑定并固定到该 session;失败 Open 不得残留半绑定 registry。`SwitchDataSource`/`Refresh` 只能针对同一 registry 验证 candidate,不存在运行中替换 registry 的 API。要切换生成代码/schema registry 必须先 `Close`,再以新 registry 显式 `Open` 新 session。
- EditorPlayDebug 与 Development 不存在隐式 Excel 源或热载默认值，也不得从 `ExcelDb.Project.json`、EditorPrefs 或上次会话恢复这些选择。宿主 adapter 可以提供 UI，但 UI 必须把当次选择逐项传入上述 bootstrap/API，而不是维护第二配置源。
- 每次 bootstrap、Open/Switch 与热载启停都必须投 Diagnostic，至少记录请求模式、请求/生效 source kind、registry expected schema hash、source 声明/实际 schema hash、请求/生效 hot-reload 状态以及拒绝原因；宿主报告只投影同一组 Diagnostic，不另算结果。
- `LoadAsset` 是便捷/低频入口,允许初始化期物化;`TryGetAsset`/caller-owned buffer 是热路径入口。
- `GetAssets` buffer 不足时返回 `Truncated`,`count` 是完整所需数量;禁止为补齐结果临时分配数组。
- 参数、状态或模式失败返回失败值并投 Diagnostic;编程错误与禁止的重入写操作抛 `InvalidOperationException`。禁止静默 no-op。
- RuntimeDatabase 的可变状态归一个 owner execution context;static 门面不等于允许无约束全局并发。是否公开多 context API不在本文范围。

## 5. Open / Switch / Refresh 事务

三种操作共享 `candidate → validate → diff → commit → publish`:

1. **Candidate**:读取新 source,构建 canonical 行、identity/key/ref/dependency 索引与诊断;允许初始化/变更内容所需分配。
2. **Validate**:校验 registry 非空且生成绑定自洽,source 声明 hash = candidate 实际 hash = `registry.ExpectedSchemaHash`,并校验 source 格式、完整性、无 pending identity、identity/key 唯一性、硬引用与 runtime-exported 数据。任一 blocker 中止;hash 比较不得用 source 自洽代替 registry 比较。
3. **Diff**:按 `AssetIdentity + 生成类型` 对齐 current/candidate,形成 patch/create/missing/recreate 计划及 ChangeSet 缓冲需求。
4. **Commit**:只在 owner publish point patch resident 实例,更新 key/guid/引用/依赖索引,并原子交换 current source/revision。
5. **Publish**:current state 全部可见后同步派发 ChangeSet,再更新 last successful report并释放旧 source。

失败不变量:

- Candidate/Validate/Diff/容量准备失败:旧 source、旧实例、旧索引与旧 revision 完全不变。
- Open 的 registry/expected hash 只在成功 Commit 时成为 session 状态;失败时保持 closed 且不缓存为后续 expected 值。Switch/Refresh 失败不得改变当前 registry。
- Commit 前必须完成所需容量增长;commit 中不得因临时容器不足留下半更新。
- Commit/verify 失败必须回滚到旧 source/object graph,且不发布成功 ChangeSet。
- Open 没有旧 source 时失败后保持 closed;Switch/Refresh 失败时继续由旧 source 服务。
- Refresh 无 canonical 变化时返回 false,不发布空的业务 ChangeSet;报告可以记录 no-op。

操作差异:

- `Open`:从 closed 建立首个 session;初始化分配许可;成功时同时固定 generated registry、expected hash 与初始 source。
- `SwitchDataSource`:沿用 session 固定 registry 验证并打开新 source,再与 resident 全量 join;成功才交换 source。Excel 与 bytes 双向语义对称,但都不得携带不同 schema hash。
- `Refresh`:沿用 session 固定 registry,只刷新当前 source;优先用 source fingerprint 与字段级 diff 收窄工作量,但结果必须与全量 candidate 等价。刷新后 source hash 变化即 blocker,不是动态 schema migration。

## 6. 发布点、线程与重入

- watcher/文件 IO/后台解析线程只能构建只读 candidate并投递结果,不得 patch resident 实例、索引或发布事件。
- 所有状态变更在宿主指定的 owner publish point执行;Unity adapter 默认映射到主线程的稳定 editor tick/PlayerLoop 点,Core 不写死 Unity 调度器。
- `changed` 在 commit 完成后同步派发;回调看到的是新 revision 的完整状态。
- 回调期间禁止重入 Open/Switch/Refresh/Close 等写事务;一律抛 `InvalidOperationException`,不在本模块引入隐式排队语义。
- 同一发布内索引、依赖图、resident 字段与事件列表属于同一 revision;读取方不得观察半更新。
- 本节只固定 RuntimeDatabase 内部 commit 与 ChangeSet 顺序;Editor import view/report、`workbookImported` 与后续 Runtime ChangeSet 的跨模块可观察次序仍归 M6 OD7,不得由 adapter 偷渡默认。

## 7. ChangeSet

```csharp
public enum ChangeKind : byte
{
    Removed, Added, Moved, Renamed, Recreated, PropertyChanged, DependencyChanged
}

public readonly struct ChangeEvent
{
    public ChangeKind kind { get; }
    public AssetKey key { get; }
    public AssetIdentity assetIdentity { get; }
    public Type runtimeType { get; }
}

public readonly struct ChangeSet
{
    public uint version { get; }
    public ChangeEventList events { get; }
}
```

- 事件缓冲双缓冲/池化;ChangeSet 与事件 view 仅在派发调用栈内有效。跨回调持久化只能复制业务所需的 identity/kind/version。
- 排序固定:Removed → Added → Moved → Renamed → Recreated → PropertyChanged → DependencyChanged。
- key/path 变化不改 identity;按实际变化发 Moved/Renamed。字段变更发 PropertyChanged。
- 引用边变化、目标 Missing/Recreated 或被引用数据变化导致反向依赖闭包失效时发 DependencyChanged。闭包计算必须环安全且确定序。
- 同一事务内同一资产的同类字段事件应合并,避免按 cell 洪泛;不得牺牲缓存正确性来裁事件。

## 8. converted bytes 不变量

- converted bytes 是 immutable runtime 数据产品,不是 workbook 的压缩副本;只包含 runtime-exported 字段。
- 文件至少携带 magic/format/schema_hash、确定序表目录、去重 UTF-8 string table、定长/变长数据区、key/guid 索引、已解析内部引用与完整性 hash。
- convert 期将内部引用解析为稳定表/行槽;pending/空缺 identity、悬空硬引用、重复 identity/key、runtime-required 值无效均阻止产物。即使自定义或损坏的 bytes 绕过 convert 携带 pending identity,Open Validate 仍必须硬拒绝。
- 相同 descriptor + 相同 exported canonical data 必须产 byte-for-byte deterministic 输出;不得受当前时间、机器路径、区域设置或字典遍历顺序影响。
- 打开时校验 magic/format/schema_hash/完整性,并把 bytes header 与 candidate hash 都和 Open 传入或 session 已固定的 generated registry expected hash 比较;任一失败拒绝 candidate。伴生 manifest 记录 schema hash、源内容 hash、行数与工具版本,但 manifest 不能充当 expected hash 的权威来源。
- 打开允许一次性大块载入/pin/mmap与索引绑定;热路径按生成访问器和预绑定偏移读取,不得反射解析 workbook 文本。

## 9. 模式矩阵

| 能力 | EditorAuthoring | EditorPlayDebug | Development | Release |
| --- | --- | --- | --- | --- |
| ExcelDataSource | yes | explicit opt-in | explicit opt-in | no |
| ConvertedBytesDataSource | yes | yes | yes | yes |
| watcher hot reload | yes | explicit opt-in | explicit opt-in | no |
| 显式 Open/Switch 新 bytes | yes | yes | yes | yes |
| workbook 写回 | Editor 层 | 经 Editor 层 | no | no |
| schema/metadata 修复 | Editor 层 | no | no | no |

- 模式在 Open/session bootstrap 时由 `RuntimeBootstrapOptions.Mode` 明确传入并固定,不能靠调用方约定或隐式环境状态切换。
- 四种模式都必须显式绑定 generated registry并执行同一 expected/source hash 与 pending identity 硬门禁;Editor/Development 的调试能力不提供放宽入口。
- Release 只读,不读取 Excel、不启 watcher、不运行动态 schema migration;运行中更新只能显式 Open/Switch 已验证的 converted bytes。
- EditorPlayDebug/Development 的 Excel source 与 hot reload 必须分别经 bootstrap/API 显式 opt-in并可诊断；拒绝或失败均保留旧 source 与热载状态，不能成为 Release fallback。

## 10. Unity、本地化与自定义引用

- schema canonical 值 `UnityResourceRef { guid, main_asset_path }` 的 guid 是身份,path 是展示。Core 只保存值与 provider handle,不调用 Unity API。
- Unity adapter 注册 `IUnityAssetProvider`,在初始化/bake期把 UnityGuid 绑定为运行时 provider key;Release 不以展示 path 兜底身份。
- `LocalizedTextRef` 是 key 引用。宿主通过 `ILocalizedTextProvider` 注册解析;无 provider/缺 key时返回 key 本身并产生去重 warning。本文不引入内建 locale/fallback/token 子系统。
- 自定义引用族经 `IReferenceFamily` 注册。family id/version、canonical parse/write、校验、convert 与 runtime provider语义必须确定且进入当前 M1 schema_hash;热路径只使用预绑定 handle,不做 provider discovery。
- provider 与引用族不得绕过 source transaction、模式门禁或宿主无关边界。

## 11. runtime hot-path no-GC

预热并达到水位线后,下列路径必须零额外 GC allocation:

- `TryGetAssetKey`、`TryGetAsset`、caller-owned buffer 枚举、key/guid 查询、引用解析与依赖遍历。
- converted bytes 字段读取及 M1 能力族运行时访问(map/curve/weighted/expression)。
- ChangeSet 形成与派发、切源 commit、hot reload patch;新字符串/新行等真实变更内容允许 O(变更内容)预算。

不覆盖:Open、Prewarm、首次便捷 LoadAsset 物化、candidate 全量构建、Editor 操作、CLI。水位线增长允许分配但必须计数、可预留、可查询;相同容量的稳定重复操作不得持续增长。

实现热路径禁止 LINQ、捕获闭包、装箱、反射、临时字符串拼接、分配式枚举器和异常控制流。CoreCLR allocation counter 与 Unity ProfilerRecorder 都必须覆盖读取、commit/patch 与事件派发。

## 12. 开放决策

1. **ExcelDataSource 装配归属**:语义已由 §3/§9 固定,但工程归属尚未决定——放 `ExcelDb.Core` 的可选 source 包、`ExcelDb.Editor`、还是独立 `ExcelDb.Source.Xlsx`。约束:Core contract 不引用 Unity,Release 不链接 xlsx backend,Editor/Development 复用同一 canonical importer。
2. **schema hash 是否拆分**:本模块暂按 M1 单一 `schema_hash` 做 Open/Switch 硬门禁。是否拆为 runtime semantic/layout/codegen 等 hash 由 M8 开放决策统一裁定;裁定前不得自行放宽不匹配 source。

## 13. 模块验收

1. 生成类型反射断言:无 ExcelDb `Object`/`ScriptableObject` 基类、无强制 `name`/`GetInstanceID`;resident/missing 仍可经侧表查询。
2. 同 identity 重复加载、Refresh、Excel↔bytes Switch 均保持 canonical 实例;删除 → Missing,再出现 → 复活;不误绑新行。
3. Open/Switch/Refresh 在格式、schema、引用、校验、容量准备各失败点均保留旧 source/object graph/index,且不发成功 ChangeSet。
4. watcher 后台完成 candidate,只在 owner publish point commit;回调读到新 revision,重入写操作被拒绝。
5. ChangeSet 七类、固定排序、反向依赖闭包、借用期与 buffer 水位线均有测试。
6. 四模式逐格门禁；EditorPlayDebug/Development 分别覆盖显式选择 bytes、显式选择 Excel、显式启停热载及其 Diagnostic，且 Project/EditorPrefs/上次会话均不能改变选择；Release 对 Excel/watcher 请求失败并保持无 Excel/watcher/writeback/dynamic migration 路径。
7. no-GC 清单在 CoreCLR 与 Unity 达到水位线后通过;真实变更分配与水位线增长可归因。
8. Unity/Localized/custom provider 缺失、成功、热路径预绑定与 Core 无宿主依赖各一组测试。
9. registry 硬门禁:API surface 不存在可省略 generated registry 或只从 source 推导 expected hash 的 Open 路径;null/无效 registry 保持 closed。分别用 Excel/bytes/custom source 覆盖“声明 hash 与实际 hash 不同”“source hash 与 `ExpectedSchemaHash` 不同”,均拒绝且 Diagnostic 同时含 expected/声明/实际值。成功 Open 后,同 hash Switch/Refresh 可成功;不同 hash 必须保留旧状态;只有 Close 后以新 registry 重新 Open 才能切换 schema。
10. pending identity 硬拒绝:对 Excel/custom candidate 各构造一行 pending/空 identity,对损坏 bytes 构造绕过 convert 的 pending identity;分别覆盖 Open、Switch、Refresh 与四模式,均命中 `identity.pending` blocker。断言运行时从不分配替代 identity,Open 保持 closed,Switch/Refresh 的旧 source/object graph/index/revision/registry 与 ChangeSet 均不变;convert 自身也拒绝产生含 pending identity 的 bytes。
