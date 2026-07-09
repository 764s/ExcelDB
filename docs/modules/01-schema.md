# 模块 1:Schema 契约

状态:本文是逐模块重推演的第 1 篇,取代 `exceldb-lean-plan.md` §4 的 C# 特性方案。声明方式 = `.proto` + exceldb options;生成的 C# 类型是消费层投影,不是事实源。后续模块引用本文记作 M1§x。编号顺延(2026-07-09):第 2 篇为 AssetDatabase 门面(`02-assetdatabase.md`),本文所称模块 2/3/4/5(workbook 与身份/导入与编辑/运行时/兼容)现为模块 3/4/5/6。

## 1. 声明方式与身份规则

- 事实源 = 手写 `.proto` 文件(升级现有 `exceldb/options.proto`)。
- 结构身份全部数字化,proto 原生:表身份 = `(exceldb.table).id`(手工分配,发布后不可变);字段身份 = proto field number;枚举值身份 = enum number;union variant 身份 = oneof 内 field number。
- 名字(message 名、字段名、枚举名)是展示与绑定信息,可自由重命名;身份不变即兼容。删除过的 number 用 proto `reserved` 声明,复用即 lint error。
- 展示信息(display_name、header_comment)不参与结构身份,也不进 schema_hash。
- 生成的 C# 类不是第二事实源:工具与二进制只比对 schema_hash;手改生成代码无效。

## 2. options 契约(exceldb/options.proto v2)

v1 的零散扩展(50011-50015)废弃并 reserved;v2 收敛为三个 message 扩展。option 全集如下,新增字段必须过本表:

```proto
syntax = "proto3";
package exceldb;
import "google/protobuf/descriptor.proto";
option csharp_namespace = "ExcelDb.Protocol";

// ---- 值类型:字段可直接使用,类型即 value shape ----
message RowRef           { int32 table = 1; int32 id = 2; }   // 内部引用;cell token 与解析规则在模块 2/3 定
message UnityResourceRef { string guid = 1; string main_asset_path = 2; }  // guid 为身份,path 为展示
message LocalizedTextRef { string key = 1; }
message Curve            { repeated CurvePoint points = 1; }
message CurvePoint       { float x = 1; float y = 2; optional float in_tangent = 3; optional float out_tangent = 4; }

// 表的语义类别:资产表还是嵌入形状(判定规则见 §3)。
enum TableKind {
  TABLE_KIND_UNSPECIFIED = 0;   // 非法:挂 table option 必须显式选 kind(XDB015)
  ASSET = 1;                    // 一行 = 一个有身份的资产;须有表 id 与 key,生成普通 C# 类(M2 Δ11)
  EMBEDDED = 2;                 // 无身份共享形状;仅为挂表级 option(校验器/展示名)而登记,禁 id/key
}

// message 字段的物理布局(仅单数 message 字段有效,语义见 §2 枚举语义块)。
enum ExpandMode {
  EXPAND_AUTO = 0;              // 确定性物化:纯标量/enum → 单 cell;含嵌套/repeated/map/引用 → 展开列
  SINGLE_CELL = 1;              // 整格文法 "mp=30, hp=0";含嵌套 message → XDB005
  EXPANDED_COLUMNS = 2;         // 逐子字段成列,路径如 cost.mp;preset 组合必须用此形态
}

// 被引用行删除时,持有方 RowRef 字段的完整性策略(闭包内 BLOCK 优先)。
enum RefDeletePolicy {
  REF_DELETE_UNSPECIFIED = 0;   // 等价 BLOCK
  BLOCK = 1;                    // 拒绝删除仍被引用的目标,报告列出引用者
  SET_NULL = 2;                 // 目标删除时清空引用格 + dirty
  CASCADE = 3;                  // 持有行级联删除(递归);删除计划先列影响集再 commit
}

// 表达式结果类型:schema 声明,不从 cell 内容推断;决定 Expression<T> 与字节码返回槽。
enum ExprResult {
  EXPR_FLOAT = 0;               // 数值公式(默认)
  EXPR_INT = 1;                 // 整型结果
  EXPR_BOOL = 2;                // 谓词,用于条件门控
}

// 数据出包策略(字段继承表级,表级默认 EXPORT_ALL;物化结果进 schema_hash)。
enum ExportPolicy {
  EXPORT_DEFAULT = 0;           // 继承上级
  EXPORT_ALL = 1;               // 进 converted bytes,运行时可读
  EDITOR_ONLY = 2;              // 仅编辑期:不进 bytes,不生成运行时读取面;非法组合见 XDB016
}

message TableOpts {
  TableKind kind = 1;
  int32 id = 2;                     // 表稳定身份
  repeated string implements = 3;   // 引用组:RowRef 可按组约束目标
  string display_name = 4;
  string sheet_name = 5;            // 默认 = message 名
  ExportPolicy export = 6;
  repeated string validators = 7;   // 行级校验器 id,宿主代码注册
}

message ExpressionOpts { string symbols_type = 1; ExprResult result = 2; }
message WeightedOpts   { int32 weight_field = 1; int32 condition_field = 2; }  // 按元素 message 的 field number 指认
message ChildTableOpts { string sheet_name = 1; }

// 单格式声明器:声明"一个 cell 的文本如何编码一个字段值"。职责边界与可替换性见 §2 声明器语义块。
message CellFormat {
  oneof kind {
    JoinFormat join = 1;    // 位置序拼接:分隔符栈,段按 field number 升序对应子字段
    NamedFormat named = 2;  // 键值对文法(仅 struct/map 层)
    string codec = 3;       // 宿主注册的 ICellFormat 实现 id(id+version 进 hash)
  }
  repeated CellFormat legacy = 4;  // 迁移窗口:仅供解析的旧格式;写出恒为 canonical;不得嵌套 legacy(XDB018)
}
message JoinFormat  { repeated string separators = 1; }  // 由外向内每层一个分隔串,可多字符,如 ["#","&"]、["->"]
message NamedFormat { string pair_separator = 1; string kv_separator = 2; }  // 缺省 ", " 与 "="

message SchemaDefaults {                  // 文件级默认:项目的格式习惯统一在此声明
  CellFormat struct_format = 1;           // struct 单 cell 默认;未声明 = named("mp=30, hp=0")
  CellFormat scalar_list_format = 2;      // repeated 标量默认;未声明 = join([";"])
  CellFormat map_format = 3;              // map 单 cell 默认;未声明 = named("a=1; b=2")
}

message FieldOpts {
  int32 key = 1;                    // 业务 key 段序,>0 生效;复合 key 按序拼接
  string display_name = 2;
  string header_comment = 3;
  repeated string aliases = 4;      // 旧表头文本容错,不进 schema_hash
  bool required = 5;
  string default_value = 6;         // canonical 字面量;proto3 零值不够用时声明
  optional double min = 7;
  optional double max = 8;
  string regex = 9;
  bool unique = 10;
  string ref_table = 11;            // RowRef 目标表(与 ref_group 二选一)
  string ref_group = 12;            // RowRef 目标组(implements 声明的组名)
  RefDeletePolicy delete_policy = 13;
  ExpandMode expand = 14;           // message 字段:单 cell 或展开列
  ChildTableOpts child_table = 15;  // repeated message 默认即子表;仅用于定制 sheet 名
  bool labels = 16;                 // repeated string 上生效:资产标签
  CellFormat format = 17;           // 单格式声明器:join/named/codec 三种 kind(见 §2 声明器语义)
  ExpressionOpts expression = 18;   // string 字段升级为表达式
  WeightedOpts weighted = 19;       // repeated message 升级为加权选择
  ExportPolicy export = 20;
  string map_key_enum = 21;         // map<int32,V> 声明枚举键语义(proto map 键不支持 enum)
}

message EnumValueOpts { string display_name = 1; repeated string aliases = 2; }

extend google.protobuf.MessageOptions   { TableOpts table = 50001; }        // 沿用 v1 号位,字段向后兼容
extend google.protobuf.FieldOptions     { FieldOpts field = 50020; }        // 50011-50015 reserved
extend google.protobuf.EnumValueOptions { EnumValueOpts enum_value = 50030; }
extend google.protobuf.FileOptions      { SchemaDefaults defaults = 50040; }
```

option 枚举语义(零值即默认):

- `ExpandMode`:仅对单数 message 字段有效(其余 → XDB006)。`SINGLE_CELL` 整格文法 `mp=30, hp=0`,含嵌套 message 时 XDB005;`EXPANDED_COLUMNS` 逐子字段成列(路径 `cost.mp`),批注/下拉/合并粒度到列;`EXPAND_AUTO` 按确定性规则物化进 descriptor——子字段全为标量/enum → SINGLE_CELL,含嵌套 message/repeated/map/引用值类型 → EXPANDED_COLUMNS。
- `RefDeletePolicy`:挂在持有方 RowRef 字段。`UNSPECIFIED` ≡ `BLOCK`(拒绝删除仍被引用的目标,报告列出引用者);`SET_NULL` 目标删除时清格 + dirty;`CASCADE` 持有行级联删除(递归),删除计划先列影响集再 commit。评估顺序:闭包内先查 BLOCK,有则整体拒绝。Excel 直删行走同一策略:BLOCK 表现为 `ref.dangling` error 门禁保存/convert,SET_NULL/CASCADE 自动改数 + warning + dirty;运行时热载一律 Missing 语义,不走本策略。
- `ExprResult`:表达式结果类型是 schema 契约,不从 cell 内容推断;决定生成的 `Expression<T>` 类型、字节码返回槽与 `Eval` 签名,导入/convert 期逐行类型检查。零值 = `EXPR_FLOAT`。
- `ExportPolicy`:字段级 `EXPORT_DEFAULT` 继承表级,表级 `EXPORT_DEFAULT` ≡ `EXPORT_ALL`。`EDITOR_ONLY` 不进 converted bytes 且不生成运行时读取面(保证 Excel 源与 bytes 源运行时行为一致);整表 EDITOR_ONLY = 策划辅助表,convert 整体跳过。key 字段 EDITOR_ONLY、导出字段引用 EDITOR_ONLY 目标 → XDB016。

单格式声明器语义(CellFormat):

职责(声明器拥有的全部):

- cell 文本 ↔ canonical 值的双向映射结构:分段、命名、转义,parse/write 必须往返;内置 kind 通过"分段 + 子字段标量词法"组合实现,缺段/空段映射为 canonical 缺失态(默认值由 canonical 层解释,不属声明器)。
- 自身身份进 descriptor 与 schema_hash:join = 物化分隔串栈,named = pair/kv 分隔串,codec = id+version;格式变更即结构变更。
- 形状域与层数预算:声明自己适用的 ValueShape 与嵌套深度,上限两层,更深必须展开列或子表。
- 表头批注中的格式说明文本(投影,不进 hash)。

不负责:值语义校验(required/range/regex/引用存在性)、列布局(ExpandMode 职责)、合并粒度(单 cell 恒为整格)、bytes 与运行时表示(convert 只消费 canonical 值,运行时不存在 cell 文本)、历史兼容检查(模块 5 消费声明器身份做 diff)。

可替换性:schema 编译、校验、布局、convert、codegen 只经 `ICellFormat { TryParse; Write; Describe; 身份 }` 接口消费格式;内置 join/named 是参数化内置实现,codec kind 是宿主注册实现,同一接口。重设计文法 = 整体替换声明器族,不触碰 schema 其余部分;文法讨论范围恒为 CellFormat 一个 message + 一个接口。

kind 细则:

- join:`separators` 由外向内每层一个分隔串,可多字符;层数与嵌套深度匹配——struct 与 repeated 标量 1 层,repeated message、map、含嵌套 struct 的 struct 2 层。`Cost` 用 `["#"]` 写作 `30#0`;`repeated ItemStack` 用 `["#","&"]` 写作 `sword&2#potion&5`;`范围@速度` 用 `["@","-"]` 写作 `8:00-12:00@2.5`。缺尾段 = 缺失(内层省略简写 `1001#1002~5#1003` 依此成立);空段 = 缺失;段数超出 → error;段内容含分隔串或引号用 CSV 式双引号转义。
- named:pair 间用 `pair_separator`(缺省 `", "`),键值间用 `kv_separator`(缺省 `"="`);`{named:{pair_separator:"#", kv_separator:":"}}` → `mp:30#hp:0`;仅 struct/map。
- codec:包裹符(`[1,2,3]`)、占位 token(`-`)、单位后缀(`30秒`)、位置与命名混排等上下文相关文法全部落此,宿主实现 `ICellFormat` 并注册 id。
- 歧义防护:任意两层分隔串及 pair/kv 分隔串互不为子串、非空、不含引号与转义字符(XDB017)。
- 优先级:字段 `format` > 文件 `SchemaDefaults` > 内置默认(struct/map 为 named,repeated 标量为 `join([";"])`)。
- union 单 cell 固定以 `:` 分隔 variant token 与 payload,payload 沿用 variant message 自己的 format。

格式变更三阶段(新增 → 迁移 → 安全删除;每阶段一次 schema 发布,canonical + legacy 集合都进 schema_hash):

- 前提:格式迁移只改文本结构,值类型与运行时表示不变;类型/语义变更走模块 5 的字段级同构流程(新 number 新增 → 数据迁移 → reserved 删除)。
- 新增:新格式设 canonical,旧格式挂入 `legacy`(`SchemaDefaults` 级同样适用,承载全项目换写法习惯)。窗口内每个 cell 尝试 canonical 与全部 legacy:仅一个成功 → 取之;多个成功且 canonical 值一致 → 取 canonical;值不一致 → `format.ambiguous` error(宁可误报,不许静默换义,防层级对调类陷阱);写出恒为 canonical;legacy 命中计数进 import/check 报告。
- 迁移:`normalize` 操作(结构生成家族,plan → report → apply)把 legacy 命中 cell 批量重写为 canonical;报告重写数与剩余命中数,跨 workbook 归零即迁移完成。
- 安全删除:移除 legacy 项;删除依据 = 最近 check/normalize 报告命中为零;删除后旧格式 cell 直接 parse error,不再存在静默解释路径。
- 任一阶段门禁失败即停留当前时期;写回门禁保证不丢数据。

## 3. 值形映射(proto 构造 → ValueShape)

| proto 构造 | ValueShape | 默认规则 |
| --- | --- | --- |
| 标量 / string / enum | Scalar / Enum | — |
| message 字段 | StructSingleCell | `expand: EXPANDED_COLUMNS` 转展开列;单 cell 文法由 format 声明器决定(named 或 join,`30#0`);嵌套 message 需第二层(上限两层),更深必须展开或子表 |
| repeated 标量 | ScalarList | 单 cell,分隔串可经 format/文件默认定制 |
| repeated message | ChildTable | 元素字段落子表;`weighted` 升级为 Weighted;format 声明 2 层 join(`1&2#2&4`)可改单 cell(StructListSingleCell,限小结构) |
| map<K,V> | Map | K、V 均标量 → 单 cell(format:named 或 2 层 join);V 为 message → 子表 + 唯一键列;枚举键用 `map<int32,V>` + `map_key_enum` |
| oneof | Union | variant 身份 = field number;payload 布局在模块 2 定 |
| exceldb.RowRef | InternalRef | 必须声明 ref_table 或 ref_group |
| exceldb.UnityResourceRef | UnityResourceRef 族 | 解析在 Unity adapter |
| exceldb.LocalizedTextRef | LocalizedRef 族 | provider 解析 |
| exceldb.Curve | Curve | convert 期 bake |
| string + expression | Expression | 编译期对 symbols_type 做类型检查 |
| 任意 + codec | Custom | codec id + version 进 schema_hash |

TableKind 判定:

- 不挂 `(exceldb.table)` 的 message = 匿名 embedded 形状(默认态):展开列结构体、单 cell 结构体、子表元素、oneof variant payload、preset 字段组、expression 符号表都属于此类;无身份、无 sheet 主权、不可作 RowRef 目标、不可独立加载,codegen 生成普通 class/struct。
- `kind: ASSET`:一行 = 一个资产;必须有表 id 与 key 字段;拥有 sheet、行身份锚(模块 2)、asset path,可被 RowRef 引用、可按 key 加载、进入 FindAssets/依赖图/ChangeSet;codegen 生成普通 C# 类,资产语义由注册引导与 facade 承载,不派生库基类(M2 D4/Δ11)。
- `kind: EMBEDDED`:显式登记的共享形状,仅当形状需要表级 option(如子表元素的 `validators`、`display_name`)时声明;不得携带 id/key。
- 挂 option 必须显式选 kind;`UNSPECIFIED` 或 EMBEDDED 带 id/key → XDB015。
- 子表行的 guid 锚是 merge/热载的机器身份,不是资产身份;子表元素需要被外部引用时,应将元素表提升为 ASSET。

preset 不是独立机制:共享字段组定义为普通 message(如 `CommonHeader`),各表以 `expand: EXPANDED_COLUMNS` 组合展开;canonical descriptor 中只见普通字段,importer 不按列名猜语义。

expression 的符号表也是普通 message:标量字段即符号槽,编译期按名解析、按类型检查,运行期生成对应符号结构体供 `Eval` 免装箱传参。

## 4. 声明示例

```proto
syntax = "proto3";
package game;
import "exceldb/options.proto";

message CommonHeader {                       // preset:组合 + 展开
  string id = 1 [(exceldb.field) = { key: 1 }];
  exceldb.LocalizedTextRef display_name = 2 [(exceldb.field) = { display_name: "显示名" }];
  repeated string tags = 3 [(exceldb.field) = { labels: true, display_name: "标签" }];
  bool enabled = 4 [(exceldb.field) = { display_name: "启用", default_value: "TRUE" }];
}

enum DamageType {
  DAMAGE_TYPE_UNSPECIFIED = 0;
  PHYSICAL = 1 [(exceldb.enum_value) = { display_name: "物理" }];
  FIRE = 2;
  FROST = 3;
}

message Cost        { int32 mp = 1; int32 hp = 2; }
message CritSymbols { float base_damage = 1; float crit = 2; }     // expression 符号表
message DropSymbols { int32 player_level = 1; }

message DamageEffect { int32 amount = 1; }
message DotEffect    { int32 amount_per_tick = 1; float duration = 2; }

message SkillLevel {
  int32 level = 1 [(exceldb.field) = { key: 1 }];
  int32 required_exp = 2;
}

message ItemStack {
  exceldb.RowRef item = 1 [(exceldb.field) = { ref_table: "ItemConfig" }];
  int32 count = 2 [(exceldb.field) = { default_value: "1" }];
}

message DropEntry {
  exceldb.RowRef item = 1 [(exceldb.field) = { ref_table: "ItemConfig" }];
  int32 count = 2 [(exceldb.field) = { default_value: "1" }];
  float weight = 3;
  string condition = 4 [(exceldb.field) = { expression: { symbols_type: "game.DropSymbols", result: EXPR_BOOL } }];
}

message SkillConfig {
  option (exceldb.table) = { kind: ASSET, id: 101, display_name: "技能", validators: "skill_rules" };

  CommonHeader common = 1 [(exceldb.field) = { expand: EXPANDED_COLUMNS }];
  int32 damage = 2 [(exceldb.field) = { display_name: "伤害", header_comment: "基础伤害值", min: 0, max: 9999, default_value: "10" }];
  DamageType damage_type = 3;
  Cost cost = 4 [(exceldb.field) = { expand: EXPANDED_COLUMNS, display_name: "消耗" }];
  repeated float tick_times = 5;                                   // 标量列表 → 单 cell
  repeated SkillLevel levels = 6;                                  // repeated message → 子表
  exceldb.RowRef next_rank = 7 [(exceldb.field) = { ref_table: "SkillConfig", display_name: "下一级" }];
  exceldb.UnityResourceRef icon = 8;
  exceldb.Curve damage_by_level = 9;
  string crit_formula = 10 [(exceldb.field) = { expression: { symbols_type: "game.CritSymbols", result: EXPR_FLOAT } }];
  repeated DropEntry drops = 11 [(exceldb.field) = { weighted: { weight_field: 3, condition_field: 4 } }];
  map<string, float> resist_decay = 12;
  repeated ItemStack rewards = 13 [(exceldb.field) = { format: { join: { separators: ["#", "&"] } }, display_name: "奖励" }];  // cell: "sword&2#potion&5"
  oneof effect {                                                   // union
    DamageEffect effect_damage = 20;
    DotEffect effect_dot = 21;
  }
}
```

对应的对外调用形态(生成物,细节在 §6):`skill.Damage`、`skill.Common.Id`、`SerializedObject.FindProperty("damage")`、`FindProperty("cost.mp")`——C# 成员 PascalCase,property path 与列路径 = proto 字段名,和原计划第 4 章示范(`hero.Hp` / `FindProperty("hp")`)同构。

## 5. 编译管线与 SchemaDescriptor

管线(工具期,允许分配;Google.Protobuf 依赖只存在于此,Core/运行时零依赖):

```text
*.proto → protoc --descriptor_set_out(锁定版本,仅作 parser)
→ SchemaCompiler:读 FileDescriptorSet + options → lint → normalize(按 id 排序、默认值物化)
→ SchemaDescriptor(canonical 二进制 + 调试 json)→ schema_hash → codegen
```

descriptor 模型(数字 id 为主键,树形):

```csharp
sealed class SchemaDescriptor { TableDescriptor[] Tables; EnumDescriptor[] Enums; ulong SchemaHash; }
sealed class TableDescriptor  { int Id; string Name; TableKind Kind; string SheetName;
                                string[] Implements; string[] ValidatorIds;
                                FieldDescriptor[] Fields; int[] KeyFieldIds; }
sealed class FieldDescriptor  { int Id; string Name;              // path = 沿树 join Name;id path = 沿树 join Id
                                ValueShape Shape; string TypeName;
                                FieldDescriptor[] Children;       // struct 展开/子表元素/oneof variants
                                /* options 原样归一化:required/default/min/max/regex/unique/
                                   ref/delete_policy/labels/cell format 物化身份/expression/weighted/export */ }
sealed class EnumDescriptor   { string Name; EnumValueDescriptor[] Values; }  // value: {Number, Name, Aliases}
```

schema_hash(xxHash64,对 canonical descriptor 字节流):

- 进:table/field/enum/variant 的 id 与 name、kind、shape、类型引用、key 结构、required/default/min/max/regex/unique、ref_table/ref_group/delete_policy、weighted/expression 描述、enum 值 number+name、表级与字段级 export policy(物化继承后的值)、expand 物化结果、cell format 物化身份(join 分隔串栈 / named pair+kv / codec id+version,含 legacy 集合)。
- 不进:display_name、header_comment、aliases、文件顺序、空白注释、protoc 与工具版本(记录在 descriptor 里,不入 hash)。
- name 进 hash 的原因:字段名绑定 property path 与列路径,是消费语义;兼容分析(模块 5)按数字 id 判断 rename。

lint 规则(XDB0xx,blocker 即不产出 descriptor/codegen;作用域 = 项目 package 内的声明,不含 google/exceldb 系统 import——PoC 实证:否则 descriptor.proto 自身的枚举会误触 XDB013):

```text
XDB001 表 id 缺失/重复/复用 reserved      XDB002 ASSET 表无 key 字段
XDB003 field number 复用 reserved         XDB004 key 落在非标量字段
XDB005 单 cell 嵌套深度超过 join 声明层数(上限两层)  XDB006 expand 用于非 message 字段
XDB007 ref_table/ref_group 目标不存在     XDB008 weighted 的 field number 指认失败
XDB009 expression symbols_type 不存在或含非标量字段
XDB010 map 键类型不支持 / map_key_enum 目标不是 enum
XDB011 labels 用于非 repeated string      XDB012 codec/validator id 未注册(warning)
XDB013 enum 缺 0 值 / 值 number 重复      XDB014 oneof variant 含 repeated/map
XDB015 table option 的 kind 未显式指定,或 EMBEDDED 声明 id/key
XDB016 key 字段声明 EDITOR_ONLY,或导出字段的引用目标为 EDITOR_ONLY 表/字段
XDB017 format 分隔串非法(空串/互为子串/含引号或转义字符,含 pair/kv 分隔),或层数与嵌套深度不匹配
XDB018 format legacy 项嵌套 legacy,或 legacy 与 canonical 物化身份相同
```

## 6. codegen 产物

生成器消费 SchemaDescriptor(不使用 protoc 的 C# 插件),每个 ASSET/EMBEDDED message 生成:

1. 强类型类:ASSET 与 EMBEDDED 均为普通 C# 类/结构,无库基类与 `name`/`GetInstanceID` 成员(库定位 Unity 无关,Unity 手感止于 facade 用法,M2 D4/Δ11);资产身份经注册引导(产物 7)由库侧维护;C# 成员 PascalCase,绑定表记录 field id ↔ C# 成员 ↔ property path(= proto 字段名)。
2. cell 解析器与写出器:canonical 字面量 ↔ 字段值,span 上直解,不用反射;结构文法经声明器——内置 join/named 按物化参数内联生成,codec kind 走 `ICellFormat` 注册接口。
3. bytes 访问器:按列目录偏移直读(格式在模块 4)。
4. patcher:实例字段级 diff 与就地覆写(hot reload 用)。
5. 属性树元数据:SerializedProperty 路径表与数组/子表访问桩。
6. 符号结构体:每个 symbols_type 生成 `struct`,`Expression<T>.Eval(in TSymbols)` 免装箱。
7. 注册引导:表 id → 类型/工厂/访问器,enum 别名表,validator/codec id 绑定点。
8. 内嵌 `SchemaHash` 常量:运行期与 workbook/bytes 校验,不一致走模块 5 兼容规则。

## 7. 模块验收测试

1. determinism:proto 文件顺序、空白、注释、option 书写顺序变化 → descriptor 字节与 schema_hash 不变。
2. 身份:字段/表/枚举值 rename(number 不变)→ id path 不变,兼容分析判为 rename;number 变化 → 判为删+增。
3. reserved:复用 reserved number → XDB003/XDB001 blocker。
4. preset:CommonHeader 展开后 descriptor 只见普通字段,子字段 id path 以 common 字段 id 为前缀。
5. 全 shape 覆盖:§3 表每行至少一个描述符快照测试(golden descriptor json 比对)。
6. expression:符号解析、类型检查、非法符号/结果类型 → XDB009 或编译 error。
7. weighted:weight_field/condition_field 指认与非法指认。
8. map:string 键、int32+map_key_enum 键、message 值(子表形)三例;重复键留给模块 3 导入测试。
9. oneof:variant 集合进 hash;variant 增删改变 hash;XDB014。
10. 示例 proto(§4)编译 → descriptor golden + codegen 编译通过 + `skill.Damage`/`FindProperty("cost.mp")` 绑定表断言。
11. TableKind 三态:匿名形状 / EMBEDDED / ASSET 各一例;kind 未指定与 EMBEDDED 带 id/key → XDB015。
12. option 枚举:AUTO expand 物化确定性(纯标量 → 单 cell,含嵌套 → 展开列);export 继承链物化;key 字段 EDITOR_ONLY 与导出引用指向 EDITOR_ONLY 目标 → XDB016;delete_policy 三值在删除计划中的行为(BLOCK 优先)。
13. 单格式声明器:`30#0`(struct 1 层)、`sword&2#potion&5`(repeated struct 2 层)、`8:00-12:00@2.5`(嵌套 struct 2 层)、`101->102`(多字符分隔)、`mp:30#hp:0`(named 自定义 pair/kv)、map 位置序各一例往返;缺尾段/空段缺失态与超段 error;权重省略简写;分隔串转义;互为子串 → XDB017;文件默认与字段覆盖的物化确定性;自定义 `ICellFormat` 注册为 codec kind 的往返与替换一例。
14. 格式三阶段:窗口解析三态(唯一成功/多成功值一致/`format.ambiguous`,含层级对调陷阱 `1&2#3&4`);写出恒 canonical;normalize 重写与命中归零报告;删除 legacy 后旧 cell fail loud;SchemaDefaults 级窗口(全项目换习惯)一例;legacy 嵌套与同身份 → XDB018。

## 8. 与仓库现状衔接

- 旧实现(`ConfigDatabase`/`ExcelTableLoader`/SkillEditor 样例/旧测试/excels 样例数据)已于 2026-07-09 整体移除(D11);历史实现经 git 历史查阅,`UndoStack`/`DependencyGraph`/xlsx IO 需要时按件回捞参考。
- `options.proto` v2 相对 v1(git 历史)的兼容姿态保持:`TableOptions` 沿用 50001 号位,50011-50015 reserved;`ExternalRef` 由 reference family 取代。
- 现行可运行衔接 = `poc/SchemaPoc`(schema 编译 + C#/Excel 双生成 + 自检,见 `poc/README.md`);正式工程按实施文档 §1 布局另起,protoc 经 `Grpc.Tools` 仅在工具工程引用,运行时零 Google.Protobuf 依赖。

## 9. 待后续模块决定的边界

- RowRef 的 cell token、`RowRef.id` 与行身份(guid/表内 id)的关系 → 模块 2(workbook 与身份)。
- 各 shape 的 cell 文法、表头三行布局、下拉与批注 → 模块 2。
- 导入校验时机与诊断码、key 索引 → 模块 3(导入与编辑)。
- bytes 布局与运行时访问器细节 → 模块 4(运行时)。
- schema 变更兼容矩阵与迁移 → 模块 5:字段级三阶段(新 number 新增 → 数据迁移 → reserved 删除)、位置序 format 的非尾部插字段段位漂移检查(需对比上次发布的 descriptor 快照,声明器自身无历史知识)。

## 10. 决策记录

约定:本节按时间追加,不回改;修订以新条目引用旧条目;后续模块沿用。每条决策是一个三级标题条目,条目间以分隔线隔开;条目内是一次对话拍:引用块 = 评审原话(仅实质设计发起才录;过堂暴露或推演自发的条目以一行普通问题陈述代替);随后**一句加粗的重点回应** = 当时让方案成立的关键洞察;最后"落点"一行 = 决策落在规格何处与次要说明(否决备选、边界划分)。本节是记忆锚,不是第二份规格——机制细节只在正文,此处不复述。全局裁剪台账见 `exceldb-cuts-adr.md`。

---

### D1(2026-07-09)声明方式 = proto,数字身份

> 依然错误. 声明方式使用 proto 或者自定义 schema. 让我们先只从 schema 开始. 然后逐步推演到其他模块.

**数字身份是 proto 语言原生保证——field number + reserved 本身就是身份系统;C# 特性路线是在用更弱的机制(名字 + `[FormerName]`)重新发明它。Unity 手感由生成的 C# 类型承载,与声明层无关。**

落点:§1/§2;撤销精简计划 2026-07-06 的 C# 特性方案;否决备选:C# 特性(身份弱)、自定义 DSL(工具链成本无对价)。

---

### D2(2026-07-09)TableKind 三态判定

概念过堂(解释 TableKind)暴露:枚举只有值,没有"什么算资产"的判定规则。

**"是不是资产"必须显式声明;子表行的 guid 是机器锚不是资产身份——要被外部引用就把表提升为 ASSET,而不是给形状偷偷加身份。**

落点:§3 判定块、XDB015;匿名形状是默认态,EMBEDDED 仅为挂表级 option 而存在。

---

### D3(2026-07-09)EXPAND_AUTO 确定性物化

概念过堂(解释四个 option 枚举,催生 D3-D5)暴露:AUTO 没有物化规则,实现者会各猜一套。

**AUTO 的存在价值是区分"显式选择"与"未声明"(proto3 零值);因此它的物化规则必须是规格常量,不是工具行为——否则工具升级即 hash 漂移。**

落点:§2 枚举语义(纯标量 → 单 cell,含嵌套 → 展开列),物化结果进 descriptor 与 hash。

---

### D4(2026-07-09)RefDeletePolicy 默认 BLOCK,策略跨面一致

概念过堂暴露(同 D3):删除被引用行的行为未定,且编辑器删除与 Excel 直删是两个入口。

**默认 BLOCK——"不丢数据"第一,自动改数必须显式声明;两个 authoring 入口共用一套策略,运行时一律 Missing 语义:删除策略是 authoring 概念,不进运行时。**

落点:§2 枚举语义;闭包内 BLOCK 优先,Excel 直删下 BLOCK 表现为 `ref.dangling` 门禁。

---

### D5(2026-07-09)ExportPolicy 进 hash,EDITOR_ONLY 无运行时读取面

概念过堂暴露(同 D3):export 漏出 schema_hash;EDITOR_ONLY 在 Excel 源下的运行时可见性未定。

**EDITOR_ONLY 的正确语义是"无运行时读取面",而不只是"bytes 里没有"——否则切到 Excel 源就多出字段,双源同语义被破坏;export 改变 bytes 布局,必然进 hash。**

落点:§2 枚举语义、§5 hash 输入、XDB016(key 字段与导出引用的非法组合)。

---

### D6(2026-07-09)单格式声明器 CellFormat

> 关于展开模式, 希望有更多策略. 你提出的合并写法通常不常见. 实际使用场景中, 会有, 简单的连接: 30#0; 更复杂的二级连接: 1&2#2&4; 并且写法动机通常很随意, 适合作为扩展机制.

> 其实想要的是一个单格式声明器. 这个声明器可以被整体替换(在重构时, 或者局部重构设计这个系统时), 并且明确声明器的职责. 这样可以将范围框定在一个小范围讨论.

**格式是纯 authoring 概念——运行时不存在 cell 文本;把它收敛为单声明点 + 单接口(`ICellFormat`),文法讨论就永远锁在一个 message 里,整体可替换。三档阶梯:named 内置 → join 声明 → codec 代码。**

落点:§2 CellFormat 与声明器语义块(职责/不负责清单);否决备选:分散的 codec/join 双 option(声明点不唯一,职责隐含)。

---

### D7(2026-07-09)分隔符栈表达力修正

> separators 的声明方式能覆盖各种可能需求吗, 需要你猜测一些使用场景, 进行验证. 担心的点是 "separators 从内到外" 的规则表达力不够.

**压测结论:方向约定没问题,缺口在别处——多字符分隔、named 不可配、嵌套一刀切,修这三处;拼接之外的文法(包裹/占位/单位/混排)刻意不进声明式,防止分隔符栈长成小语言。**

落点:§2 kind 细则(`repeated string`、`NamedFormat`、深度预算 ≤ 2)、XDB017;两层上限刻意保留,矩阵走子表。

---

### D8(2026-07-09)格式变更三阶段(新增 → 迁移 → 安全删除)

> 设想, 当格式发生变更时, 如何保证安全. 一个比较稳妥的流程是: 新增 -> 迁移 -> 安全删除. 跨越这样三个较大的时期, 完成修改.

**重解析验证只能抓"解析失败",抓不住"错但合法"的静默换义(层级对调 `1&2#3&4`)——窗口内必须双解比对;删除只认命中归零的证据,不认记忆。**

落点:§2 三阶段块(`CellFormat.legacy`、`format.ambiguous`、`normalize`、窗口进 hash);类型/语义变更归模块 5 字段级同构流程。

---

### D9(2026-07-09)项目写法习惯进 schema 本体

推演自发(源于 D6「写法动机通常很随意」):分隔符习惯每项目不同,但它决定 cell 如何被解释。

**凡影响 cell 解释的声明都是结构事实,必须进 schema 本体并进 hash——项目习惯放进工具配置就是第二事实源。**

落点:`SchemaDefaults`(FileOptions 50040),字段级覆盖;legacy 窗口同样适用于文件级。

---

### D10(2026-07-09)决策记录约定本身

> 实际我们需要在文档末尾增加一个决策记录, 来追踪决策产生的背景, 选择决策的理由等.

> 如果决策记录由我发起, 给出我的描述. 这可以帮助我快速记起决策的背景.

> 使用引用的格式, 一眼能看出来这是我发起的.

> 有了引用格式以后 "评审发起:" 这样的非我原话的内容就不需要了

> 需要有意义的决策发起才进记录, 简单的解释这种, 没必要进.

> 背景:xx, 决策: xx, 理由: xx. 这个格式想的很好, 但是缺乏重点, 不好理解. 更理想的方向是, 我的决策发起提出了一个问题, 反馈方给出了灵光一闪式的重点回应. 这描述的仅仅是一种感觉, 而非实际样式.

**决策记录是记忆锚,不是第二份规格:条目 = 对话拍——原话引用块(问)+ 一句加粗的关键洞察(答)+ 落点指回正文;等密度的背景/决策/理由三段没有重音,回看时无法快速召回"当时那个想法"。**

落点:本节约定行;后续模块沿用。

---

### D11(2026-07-09)移除旧实现,从零重写

> 删除老的无关代码, 防止干扰

**旧代码的价值只剩"可回捞的参考",留在工作区就是干扰源,git 历史是更好的存放处——新契约与旧模型(proto v1 行表 / ConfigDatabase)之间不存在渐进迁移路径,按件回捞优于带着旧骨架施工。**

落点:`src/`、`samples/`、`tests/`、`excels/` 全部移除(git 历史保留);`ExcelDB.slnx` 改指 poc 工程;§8 改写为仓库现状衔接;PoC 冒烟验证删除后全绿。
