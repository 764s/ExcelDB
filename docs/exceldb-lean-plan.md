# ExcelDB 精简计划(核推演版)

## 0. 度量口径

- 荷载行:直接约束代码工件的行——需求项、类型定义、文件格式、算法步骤、API 签名、状态机、测试项。
- 非荷载行:本节、裁剪边界、施工顺序、附录。裁剪理由单独存放于 `exceldb-cuts-adr.md`,不占本文预算。
- 本文目标:荷载行 / 总行数 > 93%,实测见附录 A。
- 写法规则:每个类型/格式/协议只在一处定义;语义与 Unity 2022.3 相同的 API 不复述语义,只声明"同 Unity"并列出偏差;逐案行为由第 12 章测试固定,文档不穷举。

## 1. 核与推演

11 个核。每个核后列出它强制产生的工件;不在任何核的强制链上的机制,一律裁剪(记录见第 2 章指向的 ADR)。

1. Unity 使用手感 → 同名 facade(`Object`/`ScriptableObject`/`AssetDatabase`/`SerializedObject`/`SerializedProperty`/`Undo`/`EditorUtility`),语义按引用继承 Unity 2022.3 文档;配置类型由用户手写 C# 类声明(Unity 定义 ScriptableObject 的方式),重命名走 `[FormerName]`(对应 Unity `FormerlySerializedAs`)。
2. Excel 存盘 → xlsx 是唯一 authoring 落盘格式;保存 = 补丁式写回(第 6.5 节),不存在"导出后 Excel 可丢弃"的路径。
3. 单一结构事实源 → schema 只来自 C# 配置类 + 特性(第 4 章);Excel 表头是 schema 投影,不是第二事实源。
4. 单一身份事实源 → 行身份只来自隐藏伴随列 row guid(第 5.3 节);key/表名/列名/行号都是展示或定位信息。
5. 重复生成表结构不破坏辅助内容 → workbook 区域所有权 + 结构生成分级(第 5.1、6.6 节)。
6. 编辑器结构化与 Excel 批量修改并存 → import 快照 + 三方合并 + 冲突分级(第 6.2、6.3 节)。
7. 迎合游戏行业填表习惯 → 枚举/引用下拉、表头注释、结构体展开列、子表、引用 token 可读、一等能力家族(第 4.3、4.7、5.1 节);辅助行列/自由 sheet 原样保留。
8. 高性能无动态 GC → converted bytes + generated accessor + handle API + 水位线契约(第 7 章);契约只覆盖运行时热路径,编辑器无 GC 契约。
9. 数据热载 → watcher + 事务式 hot reload + 事务式切源 + ChangeSet 事件(第 7.5-7.7 节);失败保留旧数据。
10. 可选热写回 → 写回只存在于 Editor authoring 上下文(含 Play Mode 经 editor API);player 构建无写回路径。
11. 宿主无关内核 → 装配边界(第 3 章);Unity 类型只出现在 adapter。

核冲突时的仲裁序(编码进各操作的失败分支):不丢数据 > 身份稳定 > 事务原子 > 事件一致 > 运行时 no-GC > 宿主无关 > Unity 手感 > 保留辅助信息。

## 2. 裁剪边界

- 本文出现的机制即全集;旧计划(`unity-like-exceldb-plan.md`)中未被本文收录的子系统一律视为已裁剪,逐项理由与替代物见 `exceldb-cuts-adr.md`。
- 裁剪总原则:机制只有在至少一个核的强制链上才保留;防御性基建在出现真实需求前不建;罕见分支给 blocker + 人工处理,不给自动化子系统。
- 被裁能力如需回归,必须以第 4.6/8.4 的扩展接口或独立 adapter 落地,不得回写进核心契约。

## 3. 架构

三个装配,单向依赖:

```text
ExcelDb.Core    宿主无关。schema 描述符、Object/ScriptableObject、RuntimeDatabase、
                bytes source、Excel source、identity、ChangeSet、Diagnostic。
ExcelDb.Editor  依赖 Core。AssetDatabase、SerializedObject/Property、Undo、
                import/merge/save、结构生成、xlsx 补丁写回、CLI 入口。
ExcelDb.Unity   依赖 Core+Editor。Unity 2022.3 adapter:UnityRef 引用族、
                浏览器窗口、picker、Play Mode 接线、构建集成。
```

- 对外 namespace:`ExcelDbEngine`(Core 的 facade)、`ExcelDbEditor`(Editor 的 facade)。
- Core/Editor 禁止引用 UnityEngine/UnityEditor;编译目标 netstandard2.1,Unity 2022.3 与 CoreCLR 双跑。
- xlsx 读写后端隔离在 `ExcelDb.Editor.Xlsx` 命名空间内,通过接口注入,可替换。

项目配置 `ExcelDb.Project.json`(进版本控制,键即全集,无 profile 体系):

```json
{ "workbooks": ["Assets/Configs/*.xlsx"], "schemaAssembly": "Game.Configs",
  "bytesOutput": "ConfigData/config.bytes", "cacheDir": ".exceldb",
  "developmentExcelSource": false, "developmentHotReload": false, "undoDepth": 256 }
```

## 4. Schema 契约

### 4.1 声明方式

配置类型 = 手写 C# 类,继承 `ExcelDbEngine.ScriptableObject`。字段默认值 = C# 字段初始化器。

```csharp
[ExcelTable]                                   // sheet 名默认 = 类名
public class SkillConfig : ScriptableObject
{
    [Key] public string id = "";               // 业务 key,表内唯一
    [Display("伤害", Comment = "基础伤害值")]
    [Range(0, 9999)] public int damage = 10;
    public DamageType damageType = DamageType.Physical;   // enum → 下拉
    [ExpandColumns] public Cost cost;          // 结构体 → 展开列 cost.mp / cost.hp
    public List<float> tickTimes = new();      // 标量列表 → 单 cell "0.5; 1; 1.5"
    [ChildTable] public List<SkillLevel> levels = new();  // 复杂列表 → 子表
    public Ref<BuffConfig> appliesBuff;        // 内部引用 → cell 存目标 key
    public UnityRef icon;                      // Unity 资源 → "路径 @guid"
    public Curve damageByLevel;                // 曲线 → 点列 "(1,10) (10,55)"
    public LocalizedTextRef displayName;       // 本地化文本 key
}
```

基类继承即 preset:源生成器展平继承链字段(基类字段在前,`[FormerName]`/`[Validator]` 沿链生效);公共字段组(key/display/tags/enabled 等)以基类交付,无独立 preset 机制。

特性全集(仅此 17 个,新增须过本表):

| 特性 | 作用域 | 语义 |
| --- | --- | --- |
| `[ExcelTable(Sheet=, Kind=Asset\|Embedded)]` | class | 注册表;Embedded 仅作展开/子表元素,无身份 |
| `[Key(Order=)]` | field | 业务 key 段;复合 key 按 Order 拼接,分隔符 `/` |
| `[FormerName("旧名")]` | class/field/enum 值 | 重命名声明;结构生成据此迁移列/表/枚举 token |
| `[Display("表头名", Comment=)]` | field | 表头展示与批注;不进 schema_hash |
| `[ExpandColumns]` | struct field | 单 cell 改为按子字段展开列 |
| `[ChildTable(Sheet=)]` | List<T> field | 元素落子表,外键列自动生成 |
| `[Required]` | field | 空 cell 报 error |
| `[Range(min, max)]` | 数值 field | 越界报 error |
| `[Unique]` | field | 表内去重检查 |
| `[Regex("...")]` | string field | 格式检查 |
| `[Codec(typeof(T))]` | field | 自定义单 cell 编解码,T: ICellCodec |
| `[Validator(typeof(T))]` | class | 行级校验,T: IRowValidator |
| `[Labels]` | List<string> field | 资产标签;建索引,接 FindAssets "l:" 与 Get/SetLabels |
| `[Union]` | abstract class | 联合基类;子类为 variant |
| `[Variant("token")]` | class | variant 稳定 token,进 schema_hash |
| `[Expression(Symbols=typeof(T))]` | Expression<T> field | 符号表结构体,编译期类型检查 |
| `[Weighted(Condition=typeof(T))]` | WeightedList<T> field | 可选条件表达式的符号表 |

### 4.2 描述符

源生成器(Roslyn incremental generator)从配置类提取 `SchemaDescriptor`,编译期常量化:

```csharp
sealed class SchemaDescriptor { TableDescriptor[] Tables; ulong SchemaHash; string ToolVersion; }
sealed class TableDescriptor  { string Name; string SheetName; TableKind Kind; string[] FormerNames;
                                FieldDescriptor[] Fields; int[] KeyFieldIndexes; Type RuntimeType; }
sealed class FieldDescriptor  { string Path;            // 列定位名,如 "cost.mp"、"tickTimes"
                                string[] FormerNames; ValueShape Shape; Type ValueType;
                                string? CodecId; string? CodecVersion;
                                string DisplayName; string? Comment;
                                bool Required; double? Min, Max; string? Pattern; bool Unique;
                                string? RefTargetTable;  // Ref<T>/接口组时为目标表名或接口名
                                int ChildTableIndex; }   // -1 表示非子表
enum ValueShape { Scalar, Enum, StructSingleCell, StructExpanded, ScalarList, ChildTable, Map, Union, Expression, Weighted, Curve, InternalRef, UnityRef, LocalizedRef, Custom }
```

### 4.3 值形与单 cell 文法

| 形 | cell 文法 | 说明 |
| --- | --- | --- |
| 标量 | 不变文化格式;bool 用 `TRUE/FALSE`;日期 ISO8601 | 空 = 默认值 |
| enum | 值名;`[Flags]` 用 `A\|B` | 未知名 → error;`[FormerName]` 兼容旧名 |
| 结构体单 cell | `x=1, y=2`(名=值,逗号分隔) | 仅一层;嵌套结构体必须展开列或子表(lint 拒绝) |
| 标量列表 | `0.5; 1; 1.5`(分号分隔) | 含分隔符的字符串用 CSV 式双引号转义 |
| 内部引用 | 目标行 key,如 `fireball` | 复合 key 用 `a/b`;空 = null |
| UnityRef | `Assets/Icons/fire.png @1f2a...` | guid 为身份;path 陈旧时校验期按 guid 修正 |
| map(标量 K/V)| `a=1; b=2` | 复杂 V 用子表 + 唯一 `__key` 列 |
| union | `fire: x=1, y=2`(token + 结构体文法)| 复杂 variant 用类型列 + variant 前缀展开列或子表 |
| expression | `damage * (1 + crit)` | 符号未定义/类型不符 → 导入期 error |
| weighted | 子表:元素列 + `__weight` [+ `__condition`] | 权重 ≤0 → error |
| curve | `(0,0) (0.5,1) (1,0)` 点列 | 需切线/插值控制时用子表列 |
| LocalizedTextRef | 文本 key token | 见 8.3 |
| 自定义 codec | codec 自定义 | codec id+version 进 schema_hash |

子表 sheet:列 = `__parent`(父行 key 展示 + 隐藏父 rowguid 伴随列)+ 元素字段列;行序 = 列表序;元素身份 = 行 guid(支持子表行被引用时升级为 Asset kind)。

### 4.4 codegen 产物

每个配置类生成(partial + 静态注册):

1. cell 解析器:string span → 字段值,不用反射。
2. cell 写出器:字段值 → canonical string(写回与结构生成共用)。
3. bytes 访问器:按 7.4 偏移直读 buffer,标量零转换。
4. patcher:两份实例字段级 diff 与就地覆写(hot reload 用)。
5. 属性树元数据:SerializedProperty 路径表(6.4)。
6. 注册引导:`SchemaRegistry.Register(descriptor, factory, accessor)`。

### 4.5 schema_hash

- 输入:表名、字段 Path、ValueShape、ValueType 全名、key 结构、Required/Range/Regex/Unique、codec id+version、引用目标、子表结构、enum 值名与数值。
- 排除:Display/Comment、FormerNames、字段声明顺序(按 Path 排序后哈希)、工具版本。
- 算法:对 canonical 描述符字节流取 xxHash64。workbook、bytes、报告都记录它;不匹配的组合按 6.6/7.4 的兼容规则处理。

### 4.6 扩展接口

```csharp
interface ICellCodec     { string Id { get; } string Version { get; }
                           bool TryParse(ReadOnlySpan<char> cell, ref object value, ref DiagnosticSink sink);
                           string Write(object value); }
interface IRowValidator  { void Validate(object row, ref DiagnosticSink sink); }
interface IReferenceFamily { string Id { get; }  // "unity" 内置于 adapter
                           bool TryResolve(in RefToken token, out object target);
                           Diagnostic? Validate(in RefToken token); }
```

注册即代码调用(`SchemaRegistry.AddValidator(...)` 等),无配置文件、无权限声明。

### 4.7 一等能力家族

八项能力属于核心契约,cell 文法见 4.3,descriptor 输入全部进 schema_hash:

- map:`Dictionary<K,V>`,K 限标量/enum;导入校验键唯一;runtime 访问器 `TryGetValue`(排序数组 + 二分,零分配)。
- union:`[Union]` 基类字段;canonical 布局 = variant token 列 + 各 variant 字段展开列(列名前缀 token);复杂 variant 用 `[ChildTable]`;未知 token → error;runtime = `VariantIndex` + 类型化 getter。
- expression:`Expression<T>` 字段;文法 = 字面量、算术/比较/逻辑/三目、符号表标识符、注册函数;导入/convert 期编译为字节码并做类型检查;runtime `Eval(in TSymbols)` 强类型零分配。
- weighted:`WeightedList<T>`,元素子表自动生成 `__weight`(float > 0)与可选 `__condition`(expression);convert 期预计算前缀和;runtime `Select(ref Rng[, in TSymbols])` 二分零分配。
- curve:`Curve` 字段;convert 期 bake 为 hermite 段数组;runtime `Evaluate(float)` 零分配;Unity adapter 与 `AnimationCurve` 双向投影(仅 inspector,允许分配)。
- LocalizedTextRef:引用族,规则见 8.3。
- label:`[Labels]` 字段;导入建 label→行索引;绑定 `FindAssets("l:...")` 与 `GetLabels/SetLabels`(写走正常 dirty/save)。
- preset:基类继承展平(4.1),无独立机制。

## 5. Workbook 契约

### 5.1 区域与所有权

每个已注册表对应一个 sheet:

```text
行1  显示名行(schema 拥有):Display 名;批注 = Comment + enum 可选值 + 类型说明
行2  字段路径行(schema 拥有):FieldDescriptor.Path;本行是列绑定唯一依据
行3  类型展示行(schema 拥有,只读展示):如 "int [0..9999]"、"enum DamageType"
行4+ 数据区(策划拥有):结构生成/保存永不整行清写,只触碰 schema 拥有的列的目标 cell
```

- 辅助列:行 2 为空或以 `#` 开头的列,系统永不读写。
- 自由 sheet:未在 metadata 注册的 sheet,系统永不读写。
- 隐藏伴随列(schema 拥有,追加在最后一个字段列之后):`__guid`(row guid,32 个十六进制字符,即 128 位)、`__rev`(u32 修订号,每次成功保存/导入内容变化 +1)。
- 枚举列生成 Excel data validation 下拉;删除下拉不算数据错误。
- 内部引用列同样生成下拉:数据有效性引用隐藏 sheet `__exceldb_keys`(schema 拥有,每个被引用表一列 key 清单,结构生成/保存时重建);策划填引用时选 key 而非记 key。

### 5.2 metadata sheet

隐藏 sheet `__exceldb`,纯文本键值区块,不依赖 Excel 数字格式:

```text
[workbook]  guid=<32hex>  schema_hash=<16hex>  format=1  saved_utc=<iso8601>
[tables]    每行: 表名 <tab> sheet 名 <tab> 数据起始行
[fields]    每行: 表名 <tab> 字段 Path <tab> 列号 <tab> 跨度
```

- 打开时以 `[fields]` 为列绑定加速缓存;与行 2 实测不一致时以行 2 为准并报 `workbook.metadata_stale`(warning,下次保存修复)。
- format 过新 → 只读打开 + `workbook.format_too_new`(blocker 于写回)。

### 5.3 行身份规则(全部 5 条)

行身份 = `__guid` 伴随 cell,随 Excel 排序/剪切/移动天然跟行。快照(6.2)是身份历史。

1. 无 guid 的行 = 新行:导入时分配 guid(内存态),下次保存落盘;落盘前重复导入按 (key, 内容 hash) 在快照 pending 区匹配,保证稳定。
2. guid 重复(整行复制):与快照 (key + 内容 hash) 匹配的行保留身份;都匹配或都不匹配时按行序首行保留;其余行按规则 1 作新行,报 `identity.duplicated`(warning)。
3. 快照有、workbook 无的 guid = 删除行。
4. guid 相同、key 变化 = 重命名(身份不变,发 Moved 事件);key 与他行冲突 → `key.duplicate`(error,两行都可加载,key 查找双双失效,保存/convert 被门禁)。
5. guid cell 被策划清空:按 key 与快照匹配 → 恢复原 guid + `identity.recovered`(warning);匹配不到 → 按规则 1 作新行。

AssetGuid ≡ row guid(guid v4 全局唯一,无派生运算)。asset path = `<workbook路径>/<表名>/<key>`,仅作展示与查找。

## 6. 编辑期

### 6.1 打开与导入管线

```text
Mount(workbook) → 读 metadata → 校验 schema_hash → 列绑定(行2 对 FieldDescriptor.Path,
经 FormerName 映射) → 逐行:伴随列取身份 → cell 解析(codegen 解析器) → 身份规则 5 条
→ 校验(Required/Range/Regex/Unique/引用/IRowValidator) → 物化 resident objects
→ 建 key 索引 + 依赖图 → 写导入快照
```

- 未知列(行 2 有路径但 schema 没有):保留 + `schema.unknown_column`(warning);该列 cell 永不触碰。
- 缺失列(schema 有、workbook 无):字段全部默认值 + `schema.missing_column`(warning);结构生成可补。
- schema_hash 不匹配:仍按上述列绑定尽力导入,汇总差异为 `schema.drift` 报告;写回被门禁,直到运行结构生成(6.6)。

### 6.2 导入快照

- 位置:`<项目缓存目录>/exceldb/snapshots/<workbook_guid>.snap`,二进制:schema_hash + 每行 (guid, rev, key, 各字段 canonical 值)。
- 作用:三方合并的 base;身份修复(5.3);pending guid 记录。每次成功导入/保存后原子重写(临时文件 + rename)。
- 快照缺失或损坏:退化为双方合并(workbook vs 内存),所有差异按冲突处理,报 `merge.no_base`(warning)。

### 6.3 三方合并与冲突

触发:watcher 检测到外部修改且内存有 dirty,或 SaveAssets preflight。粒度:cell(canonical 值比较)。

| base→theirs | base→mine | 结果 |
| --- | --- | --- |
| 无变化 | 无变化 | 不动 |
| 变化 | 无变化 | 取 theirs,patch 内存对象,发 Changed |
| 无变化 | 变化 | 取 mine,保存时写回 |
| 变化 | 变化,值相同 | 收敛,不算冲突 |
| 变化 | 变化,值不同 | 冲突:进冲突集,行标记 Conflicted |

- 行级增删与 cell 修改正交合并;同一行 theirs 删除 + mine 修改 = 冲突(整行粒度)。
- 冲突解决 API:`ConflictSet.Resolve(cell, TakeMine|TakeTheirs)`;Unity adapter 提供对话框。存在未解决冲突时 SaveAssets 返回失败报告,不写盘。

### 6.4 SerializedObject / SerializedProperty

语义 = Unity 2022.3 同名 API。支持成员:`FindProperty`、`GetIterator`、`Next(Visible)`、`propertyType/intValue/longValue/floatValue/doubleValue/boolValue/stringValue/enumValueIndex/objectReferenceValue`、`isArray/arraySize/GetArrayElementAtIndex/InsertArrayElementAtIndex/DeleteArrayElementAtIndex/MoveArrayElement`、`ApplyModifiedProperties/Update/hasModifiedProperties`、多目标 ctor(混合值 `hasMultipleDifferentValues`)。

- 属性路径文法 = Unity:`damage`、`cost.mp`、`tickTimes.Array.size`、`tickTimes.Array.data[2]`、子表 `levels.Array.data[0].requiredExp`。
- cell 映射:路径前缀查 FieldDescriptor → (sheet, 列, 行) 或子表行;codegen 属性树元数据直接携带映射,无运行时字符串拼接查找(路径 → 预解析 handle)。
- 偏差声明:不支持 `boxedValue/managedReferenceValue/gradientValue/animationCurveValue`;`objectReferenceValue` 类型为 `ExcelDbEngine.Object`(内部引用)。

### 6.5 Dirty / Undo / Save

- `Undo.RecordObject(obj, name)`:压入该行全字段 canonical 快照(池化 buffer);undo/redo 栈按 workbook 分组,深度默认 256。
- `EditorUtility.SetDirty(obj)`:行进 dirty 集。`ApplyModifiedProperties` 自动 RecordObject+SetDirty(同 Unity)。
- `AssetDatabase.SaveAssets()` 步骤:
  1. 对每个 dirty workbook:重读文件指纹,变化则先走 6.3 合并;有未解冲突 → 中止该 workbook,报告。
  2. 生成写回计划:dirty 行的变更 cell + 新行(含伴随列)+ 删除行(整行删除)+ metadata 更新。
  3. 补丁式写回:仅触碰计划内 cell,保留其余一切(样式/公式/列宽/批注/未知列/辅助列/自由 sheet);实现要求 xlsx 后端支持 cell 级 patch,做不到则整包重写前必须逐区块回拷非拥有内容,回拷验证失败 → blocker。
  4. 临时文件写入 → 复读校验(重新解析计划内 cell 与写入值一致)→ 原子替换 → 更新快照 → 清 dirty、`__rev`+1。
  5. 文件被占用/锁定:重试 3 次(200ms 退避)后失败报告,dirty 保留。
- 状态机:`Clean --edit--> Dirty --save--> Clean`;`Dirty --外部修改--> Merging --无冲突--> Dirty`;`Merging --有冲突--> Conflicted --全部 Resolve--> Dirty`;`Conflicted --RevertAll--> Clean`。

### 6.6 结构生成与兼容

命令:`Generate(workbook)`(编辑器菜单与 CLI 共用)。输入:当前 SchemaDescriptor vs workbook 现状 + 上次快照的 descriptor 摘要。三段式:plan → report → apply-or-abort。

计划项分级:

| 变化 | 级别 | 动作 |
| --- | --- | --- |
| 新表/新列 | safe | 建 sheet/在拥有区末尾追加列,写行 1-3 |
| Display/Comment/类型展示变化 | safe | 重写行 1/3 与批注(直接比对,无 hash) |
| `[FormerName]` 命中的表/列/枚举重命名 | safe | 更新行 2/表名映射,数据列原地保留 |
| 列类型变化且旧值可全部 reparse | warning | 不动数据,导入按新类型解析 |
| 列类型变化且存在不可 reparse 值 | error | 列出坏 cell 清单;数据保留,写回门禁 |
| schema 删字段 | warning | 列头改 `#removed:<path>` 转辅助列,数据保留;`--purge` 显式二次运行才物理删列 |
| 无 FormerName 的疑似重命名(删+增同型)| warning | 按删+增执行并提示补 FormerName |
| key 字段结构变化且表非空 | blocker | 拒绝;需 `--rekey` 显式确认(重建 key 索引,guid 不变)|
| 目标区与策划内容重叠(辅助列占位)| blocker | 拒绝,提示挪列 |

apply 遵守 6.5 步骤 3-4 的补丁写回与复读校验。成功后更新 metadata 与快照 descriptor 摘要。

### 6.7 AssetDatabase facade

语义 = Unity 2022.3,偏差内联注明:

```csharp
static class AssetDatabase {
    static void MountWorkbook(string path);          // 偏差:Unity 无此概念
    static void UnmountWorkbook(string path);
    static void Refresh();                           // 重扫已挂载 workbook,走 6.1/6.3
    static T LoadAssetAtPath<T>(string path);        // path 见 5.3
    static string[] FindAssets(string filter);       // 支持 "t:Type"、"l:label"、名字子串、"ref:key" 四种
    static string[] GetLabels(Object asset); static void SetLabels(Object asset, string[] labels);
    static string GUIDToAssetPath(GUID guid); static GUID AssetPathToGUID(string path);
    static void CreateAsset(Object asset, string path);  // 新行,身份规则 1
    static bool DeleteAsset(string path);            // 删行(保存时落盘)
    static bool RenameAsset(string path, string newKey); // 改 key,身份不变
    static void SaveAssets();                        // 6.5
    static event Action<ImportReport> workbookImported;
}
```

`GUID` = 128 位值类型,承载 AssetGuid(≡ row guid)。UnityRef 里的 Unity `.meta` guid 是另一命名空间,类型为 `UnityGuid`,二者无隐式转换。

### 6.8 Watcher 与刷新调度

- `FileSystemWatcher` 监听已挂载 workbook;事件 300ms 防抖 + 内容 hash 去重(Excel 保存的多次触发合一)。
- 变化入主线程队列,下一次 editor tick 执行:无 dirty → 直接 6.1 重导入 + patch 对象 + 发事件;有 dirty → 6.3 合并。
- Excel 进程持有写锁导致读失败:静默重试至可读,不报错。

## 7. 运行时

### 7.1 对象模型

```csharp
class Object          { string name; int GetInstanceID(); bool IsMissing { get; } }   // name = key
class ScriptableObject : Object { static T CreateInstance<T>(); }
readonly struct AssetKey      { ushort table; uint row; ushort gen; }                  // 会话内稳定 handle
readonly struct AssetIdentity { GUID guid; }                                           // 跨源稳定
```

- resident object:每个 (table, row guid) 至多一个实例;切源/热载后同 guid 复用同实例(核 4 的运行时回报)。
- 删除语义:实例 `IsMissing=true`,字段重置默认值,查询与 key 查找剔除,`Ref<T>.TryGet` 返回 false;同 guid 再出现 → 同实例复活,发 Changed。

### 7.2 RuntimeDatabase API

```csharp
static class RuntimeDatabase {
    static void Open(IDataSource source);                    // 冷路径,初始化分配许可
    static void Close();
    static void SwitchDataSource(IDataSource source);        // 7.6
    static void Refresh();                                   // 对当前源热载,7.5
    static void Prewarm();                                   // 物化全部 resident objects
    static T LoadAsset<T>(string key);                       // 便捷路径,允许分配
    static bool TryGetAssetKey<T>(string key, out AssetKey k);
    static bool TryGetAsset<T>(AssetKey k, out T asset);     // 热路径,零分配
    static RuntimeQueryStatus GetAssets<T>(T[] buffer, out int count); // 截断返回 Truncated
    static event Action<ChangeSet> changed;
    static RuntimeMode mode { get; }
}
interface IDataSource { SourceInfo Open(); RowBlock ReadTable(int tableIndex); ulong SchemaHash { get; } }
```

### 7.3 ChangeSet

```csharp
enum ChangeKind : byte { Added, Removed, Changed, Moved, Recreated, DependencyChanged }
readonly struct ChangeEvent { ChangeKind kind; AssetKey key; AssetIdentity identity; Type runtimeType; }
readonly ref struct ChangeSet { uint version; ReadOnlySpan<ChangeEvent> events; }
```

- 事件缓冲双缓冲池化;派发在 publish point(主线程/宿主指定 tick),同一次事务内事件排序:Removed → Added → Moved → Changed → Recreated → DependencyChanged。
- DependencyChanged 覆盖变更行的反向依赖传递闭包(池化 visited 栈)。

### 7.4 converted bytes 格式

`exceldb convert` 产物,单文件:

```text
header   : magic "XDB1" | format u16 | schema_hash u64 | string_table 偏移/长度 | 表目录偏移 | 表数量
strings  : 去重排序 UTF8 池;全文件字符串统一为 string id (u32)
表块 ×N  : 表名 sid | 行数 | 行 stride | 列目录[(字段 Path sid, shape u8, 偏移 u16)]
           定长行数组(标量就地;字符串/列表/引用存 sid 或 (offset,count) 指向变长区)
           变长区 | key 索引(open addressing, key hash u64 → 行序 u32)
           guid 表(16B×行) | rev 表(u32×行)
引用     : 内部引用在 convert 期解析为 (表序 u16, 行序 u32);悬空 → convert error
尾部     : 全文 xxHash64
manifest : 伴生 json:schema_hash、各源 workbook 内容 hash、行数统计、工具版本
```

- 打开:整块读入 pinned buffer(或 mmap),key 索引就地可用;唯一大分配发生在 Open(初始化阶段)。
- 校验:magic/format/schema_hash/尾部 hash 任一不符 → 拒绝打开,保留旧源(若为切源)。
- Runtime 读取:generated bytes 访问器按列目录偏移直读;字符串按 sid 查池,首次物化 interned,后续零分配。

### 7.5 hot reload 事务(ExcelDataSource / 开发期 bytes 替换)

```text
watcher/显式 Refresh → 池化 workspace 重新解析源 → 校验(error 即中止,保留旧数据,出报告)
→ 与 resident 按 guid diff(rev/内容) → publish point:patcher 就地覆写变更字段、
  新行建实例、消失行置 Missing → 更新 key 索引与依赖图(增量) → 发 ChangeSet → 更新内部快照
```

分配预算:O(变更内容)(新字符串、新行实例),禁止 O(表大小);workspace 复用,水位线增长记账。

### 7.6 切源事务

```text
新源完整 Open+校验+建索引(失败 → 保留旧源与旧对象图,出报告)
→ 按 (表, row guid) 与 resident 全量 join → patch/create/missing 同 7.5
→ 原子交换 source 与索引 → 发 ChangeSet → 释放旧源
```

Excel 源与 bytes 源共用 guid/rev/schema_hash,因此双向切换语义对称;schema_hash 不一致 → 拒绝切换。

### 7.7 ExcelDataSource

- 仅 EditorAuthoring/EditorPlayDebug/Development 模式可用;直接解析 xlsx 为 RowBlock,解析 workspace 池化复用。
- 只读 + refresh + watch;无写能力(写回是 Editor 层 AssetDatabase 的事)。
- xlsx 解析允许分配的部分限制在 workspace 内,重复 refresh 不增长(水位线除外)。

### 7.8 模式矩阵

| 能力 | EditorAuthoring | EditorPlayDebug | Development | Release |
| --- | --- | --- | --- | --- |
| ExcelDataSource | yes | yes | opt-in | no |
| BytesDataSource | yes | yes | yes | yes |
| hot reload / 切源 | yes | yes | opt-in | no(仅显式 Open 新 bytes)|
| AssetDatabase 写回 | yes | yes(经 editor API)| no | no |
| SerializedObject 编辑 | yes | yes | no | no |
| 结构生成/metadata 修复 | yes | no | no | no |

模式在 Open 时确定;禁用能力被调用 → 编辑器抛 `InvalidOperationException`,运行时返回失败 + Diagnostic,禁止静默 no-op。

### 7.9 no-GC 契约

- 覆盖(预热与水位线后零分配,CI 门禁):`TryGetAssetKey/TryGetAsset/GetAssets`、bytes 字段读取、能力家族访问(map 查找/curve 采样/weighted 选取/expression 求值)、key/guid 查找、依赖遍历、ChangeSet 派发、切源 commit 与 hot reload patch(扣除 O(变更内容) 预算)。
- 不覆盖:`Open`/`Prewarm`/首次 `LoadAsset` 物化(初始化阶段)、编辑器全部操作、CLI。
- 实现约束:热路径禁 LINQ/闭包/装箱/字符串拼接/反射;容量增长走显式池扩容并计数(水位线记账,可查询 `RuntimeDatabase.AllocationStats`)。
- 验收方法:`GC.GetAllocatedBytesForCurrentThread` 包夹 10^4 次循环断言零增量(CoreCLR CI)+ Unity playmode 等价测试(Mono)。

## 8. 引用体系

### 8.1 内部引用

```csharp
readonly struct Ref<T> where T : Object { bool TryGet(out T target); T Get(); AssetIdentity Identity { get; } bool IsNull { get; } }
```

- Excel 态 = 目标 key token;导入解析为 identity;bytes 态 = (表序, 行序) 已解析。
- 目标可为具体类或接口(多表实现同接口 → cell 用 `表名/key` 消歧,单表实现可省表名)。
- 悬空引用:编辑期 error(可保存草稿、禁 convert);运行期 TryGet false + 首次触达报一次 Diagnostic。
- key 全局唯一域 = 目标表(跨 workbook 合并检查)。
- key 改名修复:编辑器内 `RenameAsset` 直接改写全部引用 cell(依赖图驱动,进保存计划);Excel 手工改名 → 导入遇悬空 token 且 base 快照中存在旧 key == token 的同表行 → 自动改写为该行新 key(warning + 记 dirty);多候选或无候选 → 保持悬空 error。

### 8.2 UnityRef

```csharp
readonly struct UnityRef { UnityGuid Guid { get; } string MainAssetPath { get; } }
```

- cell 文法见 4.3;guid 是身份,path 是展示;校验期 guid→path 陈旧自动修正(记 dirty),guid 缺失按 path 反查补 guid + warning。
- 解析与 picker 在 `ExcelDb.Unity`;Core 只持有值与文法。运行时经 `IUnityAssetProvider`(adapter 注册)转真实资源加载。

### 8.3 LocalizedTextRef

```csharp
readonly struct LocalizedTextRef { string Key { get; } }
```

- cell = 文本 key token;校验:key 存在于项目指定 text table(普通 ExcelDB 表)或 provider 回调。
- runtime 经 `ILocalizedTextProvider`(宿主注册)解析;无 provider 或缺 key → 返回 key 本身 + 首次一次 warning;解析结果 interned,重复读取零分配。

### 8.4 自定义引用族

实现 `IReferenceFamily`(4.6)+ 值类型字段(`[Codec]` 声明解析)即可;族 id 进 schema_hash。样例:FMOD 事件引用(见 samples)。

## 9. 诊断与报告

```csharp
enum Severity : byte { Info, Warning, Error, Blocker }
readonly struct Diagnostic { string Code;          // 稳定字符串,如 "identity.duplicated"
                             Severity Severity; AssetIdentity Target; string? FieldPath;
                             CellRef Cell;         // (sheet, row, col),可空
                             long A, B;            // 结构化参数
                             string? Text; }       // 人读补充,允许为空
sealed class OperationReport { string Operation; bool Ok; Diagnostic[] Diagnostics;
                               string Summary; DateTime Utc; }   // 可 json 序列化
```

- 语义:Error = 数据无效,操作可继续但 save/convert 被门禁;Blocker = 操作中止,零写入。跨操作一致。
- 运行时热路径只产 `Diagnostic` 结构(无字符串拼接,Text 留空);编辑器/CLI 投影人读文本。
- 初始 code 命名空间:`workbook.* schema.* identity.* key.* merge.* save.* generate.* convert.* runtime.* ref.*`;新增 code 必须带测试。

## 10. 转换与 CLI

```text
exceldb lint     --schema <程序集> [--workbooks <glob>]        # schema 规则 + workbook 结构检查
exceldb generate --schema <asm> --workbooks <glob> [--purge] [--rekey] [--dry-run]
exceldb convert  --schema <asm> --workbooks <glob> --out <file>
exceldb check    --schema <asm> --workbooks <glob>             # 导入级全量校验(CI 用)
公共参数:--json <path> 输出 OperationReport;退出码 0=ok/warning 1=error 2=blocker 3=异常
```

CI 基线 = `lint + check + convert` 三连 + 测试套件(含 no-GC 门禁)。convert 产物 + manifest 进构建工件。

## 11. Unity adapter

- asmdef:`ExcelDb.Unity`(引用 Core/Editor DLL);Player 构建只链接 Core。
- ExcelDB Browser 窗口(UIToolkit):workbook/表/行树 + inspector 面板(SerializedObject 驱动,generic drawer 按 ValueShape;`[CustomDrawer(typeof(T))]` 注册自定义 drawer)。
- picker:内部引用搜索窗(FindAssets 驱动);UnityRef 用 ObjectField 桥接,写回 guid+path。
- 冲突对话框:6.3 冲突集的 TakeMine/TakeTheirs 界面。
- Play Mode:进入时按项目设置选 ExcelDataSource 或最近 convert 的 bytes;domain reload 后自动重开;Excel 保存 → 热载 → Game 视图即时生效。
- 构建:`IPreprocessBuildWithReport` 钩子跑 convert,产物进 `StreamingAssets/ExcelDb/`;Release 运行时 `Open(new BytesDataSource(路径))`。
- 菜单:Mount/Refresh/Generate/Convert/打开报告。

## 12. 测试与验收

验收定义 = 下列全部通过,无 conformance 分类学:

1. schema 单元:特性提取、schema_hash 稳定性(声明顺序无关)、FormerName 链、lint 规则。
2. cell 文法往返:每种 ValueShape 的 parse↔write 幂等 + 转义边界。
3. 身份:5 条规则各至少 1 测;排序/剪切/复制行后身份保持(golden workbook 操作重放)。
4. 合并矩阵:6.3 表格 5 行 × 行增删组合,共 12 场景。
5. 保存保真:补丁写回后,非计划 cell 的样式/公式/批注/未知列/辅助列/自由 sheet 二进制级不变(OpenXML part 比对);复读校验路径。
6. 结构生成:6.6 分级表每行 1 测;--purge/--rekey 门禁;dry-run 零写入。
7. 运行时:bytes 打开校验(magic/hash/schema_hash 各一坏例)、key/handle 查找、切源成功/失败保留旧图、hot reload 增改删/rename/引用变化 5 事件、Missing 复活同实例。
8. no-GC 门禁:7.9 方法,CoreCLR + Unity playmode 双跑。
9. 端到端 golden:samples 的 game.xlsx 从建表 → 填数 → 改结构 → convert → 运行读取全链路快照比对。
10. 能力家族:map 键唯一与查找、union 往返与未知 token、expression 编译错误/求值、weighted 分布与条件门、curve bake 采样一致性、LocalizedTextRef 缺 provider fallback、label 索引与 FindAssets、preset 展平与跨基类 FormerName。

## 13. 施工顺序

1. M1 Core 竖切:schema 特性 + 源生成器 + descriptor + bytes 格式 + BytesDataSource + RuntimeDatabase 读 + 测试 1/2/7(读取部分)。
2. M2 Excel 导入:xlsx 读 + 列绑定 + 身份 + 快照 + 校验 + 测试 3。
3. M3 编辑闭环:AssetDatabase + SerializedObject/Property + Undo/dirty + 补丁写回 + 测试 5。
4. M4 并发与热度:三方合并 + watcher + hot reload + 切源 + 测试 4/7(其余)。
5. M5 Unity adapter:浏览器/picker/Play Mode/构建集成 + playmode 测试。
6. M6 收口:结构生成完整分级 + CLI/CI + no-GC 门禁 + 能力家族 bake/provider 收口 + golden 端到端(测试 6/8/9/10)。

现有代码处置:`UndoStack`、`DependencyGraph`、xlsx IO 按件评估复用;`ConfigDatabase` 行表模型与 proto 契约不迁移。

## 附录 A. 荷载预算

计数规则:按非空行计;荷载 = 第 1、3-12 章,非荷载 = 标题与第 0、2、13 章及本附录。

| 分类 | 章节 | 非空行 |
| --- | --- | --- |
| 荷载 | 1、3-12 | 400 |
| 非荷载 | 0、2、13、附录 A | 25 |

实测:总计 425 行,荷载占比 94.1%;裁剪理由 18 条外置于 `exceldb-cuts-adr.md`,不占预算。
