# 模块 1:Schema 契约

设计状态：**Dependency-Complete**；实现状态：**Pending verification**。本文是 M1 Schema 领域的唯一 owner；跨模块权威规则、状态和架构决策见 [`docs/spec/README.md`](../spec/README.md)。声明方式 = `.proto` + exceldb options；`.proto` 可由程序直接编辑，也可由结构化工具事务性维护，不要求手写；生成的 C# 类型是消费层投影，不是事实源。Project v2 新增的系统 proto 磁盘镜像只用于编辑器 import 解析，同样不是事实源。本文仍出现的 `P§x` 只按总纲 §7 的迁移表解析，不指向归档计划；文末决策记录仅解释背景，不增加契约。

## 1. 声明方式与身份规则

- 事实源 = 版本化 `.proto` 文件(使用 `exceldb/options.proto`);文本编辑器和交互向导只是维护同一事实源的两个入口,向导状态、缓存与生成的 descriptor/C# 均不得补充结构语义。
- 结构身份全部数字化,proto 原生:表身份 = `(exceldb.table).id`(由程序指定或 Schema Tooling 分配/确认并显式写入 proto,发布后不可变);字段身份 = proto field number;枚举值身份 = enum number;union variant 身份 = oneof 内 field number。
- live 与 retired 表共享同一个 table id 唯一域;retired 声明是版本化 proto 内的永久 tombstone,其 id 与仍存活 id 一样占用且永不释放。Schema Tooling 建议新 table id 时必须同时排除 live 与 retired 集合,不得依赖 cache 或仅扫描生成代码。
- 名字(message 名、字段名、枚举名)是展示与绑定信息,可自由重命名;身份不变即兼容。删除过的 number 用 proto `reserved` 声明,复用即 lint error。
- 展示信息(display_name、header_comment)不参与结构身份,也不进 schema_hash。
- 生成的 C# 类不是第二事实源:工具与二进制只比对 schema_hash;手改生成代码无效。

### 1.1 结构化创建与维护

- Schema Tooling 必须提供结构化 mutation primitive,供交互向导表达建表、增删改字段/枚举/variant 与 option 变更；具体提示、页面、命令名和 Project 编排归 M4,不由本文规定。
- 每次 mutation 以当前 `.proto` 与用户确认的结构意图为输入,先在暂存区形成候选 `.proto`；rename 必须保留数字身份,删除必须写入相应 `reserved`,破坏性变更仍受 M8 兼容与迁移门禁约束。
- 候选必须走 §5 同一 parser、SchemaCompiler、lint 与 codegen 管线。全部成功后才以原子替换提交 `.proto` 与本次生成的 descriptor/C#/`RuntimeSchemaRegistry`；任一步失败均不得改动原 `.proto` 或发布部分新派生物。
- 交互会话可以保存非权威的恢复进度,但删除全部会话状态与 cache 后,仅凭版本化 `.proto` 必须重建字节相同的 descriptor、C# 与 `RuntimeSchemaRegistry`。直接编辑和向导产生相同 `.proto` 时,下游结果必须完全相同。

`table create` 在进入上述 mutation 管线前,必须把“简单字段 + 封闭的自动选项”经一个窄的代码扩展点收敛为 table-local schema draft。该接口只服务新表结构初始化,不初始化业务数据行或运行时对象。语义面为:

```csharp
public interface ITableInitializer
{
    string Id { get; }
    void Initialize(
        in TableInitializationContext context,
        ITableDraft draft);
}
```

- `TableInitializationContext` 只暴露当前 proto source-set 编译得到的只读 schema 视图,以及已由入口收集并规范化的表名、key、简单字段和封闭选项；不暴露 workbook/C# 文件写入、Console/UI 或环境发现能力。
- `ITableDraft` 只能配置本次正在创建的单表 draft:添加 M1§3 已有 singular Scalar/Enum 形状字段、指定 key,以及设置当前表/字段除 legacy `export`/`export_targets` 外的现有 `TableOpts`/`FieldOpts`。导出选择由入口记录用户显式意图后统一交给 `IExportTargetStrategy`,initializer 不得预填或改写以让策略失效。draft 不得创建 sibling table、enum、message 或其他 schema 节点。initializer 不得写原始 proto 文本、直接分配最终数字身份、写 proto/C#/xlsx/cache,或生成/提交 MutationPlan。数字身份由后续 mutation engine 统一建议、避让、展示并确认。
- 正式工具必须提供 `DefaultTableInitializer`,用简单字段与少量强类型选项产生有效 live ASSET 初始结构；自动补齐的字段/option 必须与手工输入一起进入候选 proto 与完整计划,未确认时零写入。
- 定制 Schema Tooling 发行物可以在自身 composition root 以普通 C# 代码显式注册/替换当次活动的 `ITableInitializer`;`DefaultTableInitializer` 仍必须随包可用。initializer 是受信任代码,契约上必须无管线外副作用,不得依赖时间、随机数或未显式环境状态,并对同一 context 产生确定结果；异常或非法 draft 是 blocker,且不得绕过 lint、兼容分析或原子提交。
- initializer 只参与新表 draft 构造,不参与冻结计划的 replay 或后续 schema consumer。每次计划构造可以因用户返回修改而重新调用,但同一 context 必须得到同一 draft。initializer `Id` 只用于计划构造期 Diagnostic/人读审计投影,不进 canonical MutationPlan、proto、Project、descriptor 或 schema_hash,也不是 plan revalidation 前置。提交后的唯一结构事实仍是 `.proto`。
- 不建设 initializer/template/profile 声明文件、JSON/YAML 模板 DSL、任意 option 字典或自动脚本发现。initializer 仅能补齐本次新表 draft 内的简单字段与 option;复杂 message/repeated/map/oneof/ref/codec、辅助类型定义与已有表演进/迁移只经 `table edit` 或直接编辑 proto,不在初次建表表单中再造一套声明语言。

### 1.2 表退役与永久 tombstone

- 退役是 live ASSET 表的单向生命周期转换:`live → retired`,没有复活边。只有曾作为 live schema 发布、已有稳定 table id 的 ASSET message 才能设置 `retired = true`;新建即 retired、匿名/EMBEDDED retired、无有效 id retired 均非法。
- 退役提交必须保留原 proto message 声明与 `(exceldb.table).id`,只把其状态变为 retired。该 message 此后必须永久留在版本化 proto;删除 tombstone、清除 retired 标记、改 id 或让任何 live/retired 表复用其 id 都是 blocker。message rename 仍遵守 §1 的数字身份规则:保 id 即兼容 rename,不会复活该表。
- retired message 只承担身份墓碑职责:不要求对应 workbook/sheet 或 key 数据,不产生 live C# 数据类型、运行时注册项或 converted bytes 表。它在 canonical descriptor 中物化为按 id 确定序的 retired tombstone,并永久参与 schema_hash。
- 当前 schema 的静态 lint 负责 retired 形态与 live+retired id 唯一性;发布转换 lint 还必须把候选 descriptor 与显式提供的 previous published descriptor 比较,证明“先前 live → 当前 retired”或“先前 retired → 当前同一 tombstone”。previous 只用于历史转换判定,不得替代从当前 schemaDir 编译 candidate;其选择与兼容报告归 M8。

## 2. options 契约(exceldb/options.proto v2)

v1 的零散扩展(50011-50015)废弃并 reserved;v2 收敛为三个 message 扩展。option 全集如下,新增字段必须过本表:

```proto
syntax = "proto3";
package exceldb;
import "google/protobuf/descriptor.proto";
option csharp_namespace = "ExcelDb.Protocol";

// ---- 值类型:字段可直接使用,类型即 value shape ----
message RowRef           { int32 table = 1; bytes row_guid = 2; } // 内部引用；row_guid 必须恰为 16 bytes 且非零
message UnityResourceRef { string guid = 1; string main_asset_path = 2; }  // guid 为身份,path 为展示
message LocalizedTextRef { string key = 1; }
message Curve            { repeated CurvePoint points = 1; }
message CurvePoint       { float x = 1; float y = 2; optional float in_tangent = 3; optional float out_tangent = 4; }

// 表的语义类别:资产表还是嵌入形状(判定规则见 §3)。
enum TableKind {
  TABLE_KIND_UNSPECIFIED = 0;   // 非法:挂 table option 必须显式选 kind(XDB015)
  ASSET = 1;                    // live:须有表 id/key并生成 C#;retired:仅保留稳定 id tombstone
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

// legacy 单投影出包策略;仅作旧 proto 兼容输入,新声明使用 ExportTargetSet。
enum ExportPolicy {
  EXPORT_DEFAULT = 0;           // 继承上级
  EXPORT_ALL = 1;               // 表级固定 {client,server};字段级继承父集,只随父集的显式变化而变化
  EDITOR_ONLY = 2;              // 映射到显式空 target 集
}

// 显式 runtime 导出目标集;message presence 区分“未声明/继承”与“显式空/editor-only”。
message ExportTargetSet { repeated string ids = 1; }

message TableOpts {
  TableKind kind = 1;
  int32 id = 2;                     // 表稳定身份
  repeated string implements = 3;   // 引用组:RowRef 可按组约束目标
  string display_name = 4;
  string sheet_name = 5;            // 默认 = message 名
  ExportPolicy export = 6 [deprecated = true]; // legacy
  repeated string validators = 7;   // 行级校验器 id,宿主代码注册
  bool retired = 8;                 // 已发布 ASSET 的永久 table-id tombstone;单向不可逆
  ExportTargetSet export_targets = 9;
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
  ExportPolicy export = 20 [deprecated = true]; // legacy
  string map_key_enum = 21;         // map<int32,V> 声明枚举键语义(proto map 键不支持 enum)
  ExportTargetSet export_targets = 22;
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
- `ExportTargetSet` 是新的唯一 canonical 导出语义:表未声明时固定物化为 `{client,server}`;字段未声明时继承其表/父路径的 effective target 集。显式集合是 replace 而非 union/exclude:只能收窄父集,不得扩大;显式空 message `{}` = editor-only,不进任何 target 的 bytes 或运行时读取面。target id 必须匹配 `[a-z][a-z0-9-]*`,按 ordinal 排序后物化,重复、非法或 case-fold 冲突均为 blocker。
- legacy `ExportPolicy` 只是迁移输入:表 `DEFAULT/ALL` → `{client,server}`,字段 `DEFAULT/ALL` → 继承父集,`EDITOR_ONLY` → 空集。同一 table/field 同时出现 `export_targets` 与非 `EXPORT_DEFAULT` legacy 值是 blocker。新 target(如 `lite-client`)必须由候选 proto 显式写入,旧 `EXPORT_ALL` 绝不会因工具升级自动扩大暴露面。
- 每个 target 独立形成 runtime projection。表在某 target 可见时,全部 key 字段必须在该 target 可见;该 target 内的硬 `RowRef` 必须指向同 target 可见的表/key。空 target 集的整表是策划辅助表:仍有 authoring 投影,但不产生任何 runtime codegen/registry/bytes;因其表 target 亦为空,其 key 字段空集不属于非法组合。
- xlsx 始终保留全量 authoring 字段;client/server/lite-client 只是 runtime projection,不改变 workbook ownership 或 cell canonical 值。对同一 target,Excel source 与 converted bytes source 必须暴露相同字段面。

导出目标的自动补齐是一个仅存在于结构操作计划期的窄 C# 策略:

```csharp
public interface IExportTargetStrategy
{
    string Id { get; }
    void Apply(
        in ExportTargetStrategyContext context,
        IExportTargetDraft draft);
}
```

- `StandardClientServerExportTargetStrategy` 是标准实现,只拥有稳定 target 目录 `client/server`、展示元数据与表默认集 `{client,server}`。用户显式选择优先；未指定表补该默认集,未指定字段复制当前父 effective set,不得一律写成 `{client,server}` 后扩大 client-only/server-only 父集。任何自定义策略同样只能为本次 create/edit 的未声明项填值,字段输出必须是父集子集,不得改写已确认选择。
- 定制 Schema Tooling 可在 composition root 以普通 C# 注册策略(例如增加 `lite-client`)。策略必须确定、无 IO/时间/随机/环境依赖,不拥有 codegen、convert、路径或 runtime 语义。它的全部结果必须在 candidate proto 中显式物化并进入 MutationPlan;计划冻结/apply 及后续 schema consumer 都不再调用策略。
- 策略 `Id` 只用于计划期 Diagnostic/人读审计,不进 canonical MutationPlan、proto、Project、descriptor 或 schema_hash。提交后删除/替换策略不得让已有表漂移;新 target 的持久事实是 proto 中的显式 id,不是策略注册状态。
- 该接口不是通用 profile/policy 框架,也不引入 JSON/YAML 模板、任意参数袋、脚本/程序集扫描或 runtime 插件。如未来某 target 需要不同编码后端,必须另立 versioned backend 契约,不扩张本策略的字段归类职责。
- `TableOpts.retired`:零值 `false` = live。`true` 只表示 §1.2 的永久 ASSET tombstone,不是禁用、隐藏或临时下线开关;retired message 豁免 live ASSET 的 key/workbook/导出要求,其余非法形态及历史逆转统一由 XDB019 阻止。

单格式声明器语义(CellFormat):

职责(声明器拥有的全部):

- cell 文本 ↔ canonical 值的双向映射结构:分段、命名、转义,parse/write 必须往返;内置 kind 通过"分段 + 子字段标量词法"组合实现,缺段/空段映射为 canonical 缺失态(默认值由 canonical 层解释,不属声明器)。
- 自身身份进 descriptor 与 schema_hash:join = 物化分隔串栈,named = pair/kv 分隔串,codec = id+version;格式变更即结构变更。
- 形状域与层数预算:声明自己适用的 ValueShape 与嵌套深度,上限两层,更深必须展开列或子表。
- 表头批注中的格式说明文本(投影,不进 hash)。

不负责:值语义校验(required/range/regex/引用存在性)、列布局(ExpandMode 职责)、合并粒度(单 cell 恒为整格)、bytes 与运行时表示(convert 只消费 canonical 值,运行时不存在 cell 文本)、历史兼容检查(M8 消费声明器身份做 diff)。

可替换性:schema 编译、校验、布局、convert、codegen 只经 `ICellFormat { TryParse; Write; Describe; 身份 }` 接口消费格式;内置 join/named 是参数化内置实现,codec kind 是宿主注册实现,同一接口。重设计文法 = 整体替换声明器族,不触碰 schema 其余部分;文法讨论范围恒为 CellFormat 一个 message + 一个接口。

kind 细则:

- join:`separators` 由外向内每层一个分隔串,可多字符;层数与嵌套深度匹配——struct 与 repeated 标量 1 层,repeated message、map、含嵌套 struct 的 struct 2 层。`Cost` 用 `["#"]` 写作 `30#0`;`repeated ItemStack` 用 `["#","&"]` 写作 `sword&2#potion&5`;`范围@速度` 用 `["@","-"]` 写作 `8:00-12:00@2.5`。缺尾段 = 缺失(内层省略简写 `1001#1002~5#1003` 依此成立);空段 = 缺失;段数超出 → error;段内容含分隔串或引号用 CSV 式双引号转义。
- named:pair 间用 `pair_separator`(缺省 `", "`),键值间用 `kv_separator`(缺省 `"="`);`{named:{pair_separator:"#", kv_separator:":"}}` → `mp:30#hp:0`;仅 struct/map。
- codec:包裹符(`[1,2,3]`)、占位 token(`-`)、单位后缀(`30秒`)、位置与命名混排等上下文相关文法全部落此,宿主实现 `ICellFormat` 并注册 id。
- 歧义防护:任意两层分隔串及 pair/kv 分隔串互不为子串、非空、不含引号与转义字符(XDB017)。
- 优先级:字段 `format` > 文件 `SchemaDefaults` > 内置默认(struct/map 为 named,repeated 标量为 `join([";"])`)。
- union 单 cell 固定以 `:` 分隔 variant token 与 payload,payload 沿用 variant message 自己的 format。

格式变更三阶段(新增 → 迁移 → 安全删除;每阶段一次 schema 发布,canonical + legacy 集合都进 schema_hash):

- 前提:格式迁移只改文本结构,值类型与运行时表示不变;类型/语义变更走 M8 的字段级同构流程(新 number 新增 → 数据迁移 → reserved 删除)。
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
| oneof | Union | variant 身份 = field number;payload 布局由 M5 定 |
| exceldb.RowRef | InternalRef | 必须声明 ref_table 或 ref_group |
| exceldb.UnityResourceRef | UnityResourceRef 族 | 解析在 Unity adapter |
| exceldb.LocalizedTextRef | LocalizedRef 族 | provider 解析 |
| exceldb.Curve | Curve | convert 期 bake |
| string + expression | Expression | 编译期对 symbols_type 做类型检查 |
| 任意 + codec | Custom | codec id + version 进 schema_hash |

TableKind 判定:

- 不挂 `(exceldb.table)` 的 message = 匿名 embedded 形状(默认态):展开列结构体、单 cell 结构体、子表元素、oneof variant payload、preset 字段组、expression 符号表都属于此类;无身份、无 sheet 主权、不可作 RowRef 目标、不可独立加载,codegen 生成普通 class/struct。
- `kind: ASSET, retired: false`:一行 = 一个资产;必须有表 id 与 key 字段;拥有 sheet、行身份锚(M5)、asset path,可被 RowRef 引用、可按 key 加载、进入 FindAssets/依赖图/ChangeSet;codegen 生成普通 C# 类,资产语义由 generated registry 与 facade 承载,不派生库基类(M2 D4/Δ11)。
- `kind: ASSET, retired: true`:只保留 §1.2 table-id tombstone,不再是可挂载/引用/加载的表,不要求 key 或 sheet;descriptor、codegen 与 convert 必须把它和 live ASSET 分流。
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
*.proto → 内嵌的锁定版本 proto parser/compiler → FileDescriptorSet
→ SchemaCompiler:读 FileDescriptorSet + options → lint → normalize(按 id 排序、默认值物化)
→ SchemaDescriptor(canonical 二进制 + 调试 json)→ schema_hash → codegen
```

正式 Schema Tooling 是 self-contained CLI 的内嵌能力,而不是外部环境前置条件:

- M4 提供 Project v2 的 `schemaDir`;M1 递归枚举其下 `*.proto`,统一为 schemaDir-relative `/` 路径并按 ordinal 排序。首表创建前空输入集合法,其余 schema 操作遇空集为用法错误。§5.1 catalog 登记的系统镜像路径必须先验证再从业务 source-set 排除。
- `schemaDir` 是唯一业务 import root;业务/第三方 proto 必须用相对该目录的 import 路径,禁止随 cwd 或当前文件目录改变解析结果,也禁止 `..` 逃逸。`google/protobuf/*` 与 `exceldb/options.proto` 的编译内容只从内嵌系统 catalog 解析；同路径磁盘镜像不能覆盖 catalog。
- lint、结构化 mutation、descriptor/codegen、workbook 布局、check/import 与 convert 等所有 schema consumer,每次操作都必须从当时 schemaDir 的完整当前 proto 集进入上述同一编译管线。生成 C#、程序集、旧报告或 cache 中的 descriptor 都不是 schema 输入,不得在当前 proto 已变化时继续驱动下游。
- descriptor、调试 json、输入摘要与 generated registry cache 全是可删除派生物。实现可以在完整校验当前 proto 内容摘要与锁定工具身份后复用 cache 加速,但删除/污染/过期 cache 必须只触发从 schemaDir 重建,不得改变 descriptor、schema_hash、诊断或产物字节。
- 发布物必须随包携带锁定版本的 proto parser/compiler、完整 `google/protobuf/*` 系统 proto 集、`exceldb/options.proto`、最小 proto 发射资源与 `DefaultTableInitializer`；这些资源不构成可配置的表模板语言。实现可以使用库或受控同包组件,但不得按 PATH 查找 `protoc`、`dotnet` 或其他 schema 构建程序。
- 系统 import 从内嵌资源解析,业务与第三方 import 只从 Project 声明的本地输入解析；缺失即本地诊断并中止,不得尝试联网恢复、下载 SDK/package 或调用包管理器。
- 同一发布物在 PATH 清空、网络拒绝且机器未安装 protoc/.NET SDK 的环境中,必须可从 `.proto` 完成 descriptor、schema_hash 与全部 §6 codegen。内嵌工具版本记录在 descriptor/报告中但不进入 schema_hash。
- 一次编译先在暂存区形成 FileDescriptorSet、candidate SchemaDescriptor/hash、全部 C# 与 `RuntimeSchemaRegistry`,再统一 lint/自检。普通 build 只在全部成功后替换派生物;结构化 mutation 还必须把 candidate proto 与这些派生物作为一个提交单元。任一步失败均保留原 proto 与上一组完整派生物,不得让新 descriptor、旧 registry 或局部 C# 可见。
- previous published descriptor 只作为 retired/M8 历史转换 lint 的显式对照输入;当前 candidate 始终只由当前 schemaDir 编译。previous 缺失时可以重建并消费一个已经发布的当前 schema,但不得批准 live→retired、删除/复活 tombstone等需要历史证明的新发布转换。

### 5.1 系统 proto catalog 与编辑镜像

EXE 内只有一个 canonical 系统 proto 来源。实现须公开以下只读能力供编译器、镜像修复和计划指纹共用，禁止编译器与镜像器各维护一份资源表：

```csharp
public interface ISystemProtoCatalog
{
    string CatalogHash { get; }
    IReadOnlyList<SystemProtoFile> Files { get; }
    bool TryGet(string logicalPath, out SystemProtoFile file);
}

public sealed record SystemProtoFile(
    string LogicalPath,
    ReadOnlyMemory<byte> CanonicalBytes,
    string Sha256);
```

- catalog 至少覆盖 `exceldb/options.proto` 及该文件和业务 proto 可能传递导入的完整随包 `google/protobuf/*` 集。`LogicalPath` 使用 `/`、不得为绝对路径或包含 `.`/`..`;文件按 logical path ordinal 排序；`CatalogHash` 由完整 `(path,bytes)` 集确定性计算。
- 初始化、显式“修复 Proto 依赖”和重新生成的 MutationPlan 把 catalog 镜像为 `<schemaDir>/exceldb/options.proto` 与 `<schemaDir>/google/protobuf/*.proto`，并把所有权、逐文件 SHA-256 与 catalog hash 写入 `.exceldb/system-imports.json`。该记录随 Project 配置版本化。
- 镜像只服务普通编辑器的 import 跳转/补全。Windows 上两个系统目录设置 Hidden、文件设置 ReadOnly；`<schemaDir>/.gitignore` 忽略 `/exceldb/` 与 `/google/protobuf/`。属性设置失败是 warning，不能改变 canonical 文件内容或领域工件。
- source-set 扫描遇 catalog reserved path 时先逐字节验证。内容与 catalog 相同且所有权记录一致时排除；缺失或旧 catalog 可由修复计划补齐；内容或所有权记录不符时结构写操作 blocker 并要求显式修复。未被 catalog 登记的 `exceldb/*` 或 `google/protobuf/*` 一律 blocker，不得作为业务 schema 编译。
- 镜像、`.exceldb/system-imports.json`、文件属性及其修复状态不得进入业务 proto source-set、source fingerprint、canonical descriptor、SchemaHash、业务 lint 或 proto emitter。镜像存在、缺失、旧版和修复后的同一业务输入必须得到字节相同的 descriptor、C# 与 target bytes。
- 编译器始终从 catalog 解析 reserved imports；不得因磁盘镜像缺失而报告 `exceldb/options.proto` 或随包 Google import 不存在，也不得把项目镜像内容悄悄当成编译输入。真正缺失的非系统 import 仍须报告完整 import chain。

descriptor 模型(数字 id 为主键,树形):

```csharp
sealed class SchemaDescriptor { TableDescriptor[] Tables; RetiredTableDescriptor[] RetiredTables;
                                EnumDescriptor[] Enums; ulong SchemaHash; }
sealed class TableDescriptor  { int Id; string Name; TableKind Kind; string SheetName;
                                string[] Implements; string[] ValidatorIds;
                                FieldDescriptor[] Fields; int[] KeyFieldIds; }
sealed class RetiredTableDescriptor { int Id; string Name; } // 永久 tombstone,无 live layout/runtime surface
sealed class FieldDescriptor  { int Id; string Name;              // path = 沿树 join Name;id path = 沿树 join Id
                                ValueShape Shape; string TypeName;
                                FieldDescriptor[] Children;       // struct 展开/子表元素/oneof variants
                                /* options 原样归一化:required/default/min/max/regex/unique/
                                   ref/delete_policy/labels/cell format 物化身份/expression/weighted/export */ }
sealed class EnumDescriptor   { string Name; EnumValueDescriptor[] Values; }  // value: {Number, Name, Aliases}
```

- `Tables` 保留全部非 retired ASSET/EMBEDDED descriptor,沿既有确定序归一化;`RetiredTables` 只含按 id 排序的 ASSET tombstone。table id 唯一性在 `Tables` 的 live ASSET 与 `RetiredTables` 并集上检查。retired proto message 归一化为 `{Id,Name}`;其旧字段/layout 不再进入 live descriptor。RowRef、workbook 布局与 convert 只把 `Tables` 中的 live ASSET 当作数据表。
- previous/current descriptor diff 以同一 id join live 与 retired 集合:previous live → current retired 是唯一合法退役边;previous retired 必须在 current 保持同 id retired。previous retired 消失、转 live、改 id或其 id 被另一 live/retired 声明占用均命中 XDB019;同 id 的 name 变化按普通 rename 分类。

schema_hash(xxHash64,对 canonical descriptor 字节流):

- 进:live table/field/enum/variant 的 id 与 name、kind、shape、类型引用、key 结构、required/default/min/max/regex/unique、ref_table/ref_group/delete_policy、weighted/expression 描述、enum 值 number+name、每个表/字段物化并确定序的 effective export target 集、expand 物化结果、cell format 物化身份(join 分隔串栈 / named pair+kv / codec id+version,含 legacy 集合),以及每个 retired tombstone 的 id+原 message name+retired 状态。
- 不进:display_name、header_comment、aliases、文件顺序、空白注释、protoc 与工具版本(记录在 descriptor 里,不入 hash)。
- name 进 hash 的原因:字段名绑定 property path 与列路径,是消费语义;兼容分析(M8)按数字 id 判断 rename。
- v1 只有上述一个完整 `schema_hash`:所有 target 共享该 hash,并以 `(schema_hash, export_target_id)` 作为目标运行时投影身份。不发明 `export_view_hash` 或放宽原 hash 门禁;代价是任一 target 的结构变化会保守地要求全部 target 重建。

lint 规则(XDB0xx,blocker 即不产出 descriptor/codegen;作用域 = 项目 package 内的声明,不含 google/exceldb 系统 import——回归验收必须证明系统 import 不会让 descriptor.proto 自身的枚举误触 XDB013):

```text
XDB001 ASSET 表 id 缺失,或 live ASSET+retired 唯一域内重复/复用 tombstone
XDB002 live ASSET 表无 key 字段
XDB003 field number 复用 reserved         XDB004 key 落在非标量字段
XDB005 单 cell 嵌套深度超过 join 声明层数(上限两层)  XDB006 expand 用于非 message 字段
XDB007 ref_table/ref_group 目标不存在     XDB008 weighted 的 field number 指认失败
XDB009 expression symbols_type 不存在或含非标量字段
XDB010 map 键类型不支持 / map_key_enum 目标不是 enum
XDB011 labels 用于非 repeated string      XDB012 codec/validator id 未注册(warning)
XDB013 enum 缺 0 值 / 值 number 重复      XDB014 oneof variant 含 repeated/map
XDB015 table option 的 kind 未显式指定,或 EMBEDDED 声明 id/key
XDB016 字段 target 扩大父集、key 未覆盖表全 target,或某 target 的硬引用目标/key 在该 target 不可见
XDB017 format 分隔串非法(空串/互为子串/含引号或转义字符,含 pair/kv 分隔),或层数与嵌套深度不匹配
XDB018 format legacy 项嵌套 legacy,或 legacy 与 canonical 物化身份相同
XDB019 retired 用于非 ASSET/无稳定 id/非 previous-live 新表,或 previous tombstone 被删除、清标记复活、改 id、id 被复用
XDB020 export target id 非法/重复,显式 target 集与非默认 legacy export 同时出现,或旧声明无法等价物化
```

## 6. codegen 产物

生成器消费 SchemaDescriptor(不使用 protoc 的 C# 插件),同时生成全量 authoring surface 与每个 export target 的独立 runtime surface:

1. authoring 强类型类:live ASSET 与被 live schema 使用的 EMBEDDED 均为普通 C# 类/结构,包含全量 authoring 字段,无库基类与 `name`/`GetInstanceID` 成员。C# 成员 PascalCase,绑定表记录 field id ↔ C# 成员 ↔ property path(= proto 字段名)。
2. target runtime 强类型面:对 descriptor 中每个稳定 target id 独立投影表、字段、EMBEDDED 依赖与读取 API。某 target 不可见的字段不得出现在该 runtime surface 或透过反射/侧表读取。Project v2 的物理包装固定为 `<generatedCSharpDir>/Authoring/` 与 `<generatedCSharpDir>/Runtime/<target-id>/`；target id 必须先按 M1 语法校验再作为单个目录段使用，不得逃逸输出根。
3. cell 解析器与写出器:authoring 面覆盖全量 canonical 字面量 ↔ 字段值;target Excel source 只绑定该 target runtime surface 所需路径。结构文法经声明器生成,不用反射。
4. target bytes 访问器:每个 target 只为已导出列生成偏移直读绑定(格式由 M7 定)。
5. target patcher:只对当前 target 可见字段做实例级 diff 与就地覆写(hot reload 用)。
6. authoring 属性树元数据:SerializedProperty 路径表与数组/子表访问桩覆盖全量字段;runtime 不借此绕过 target 投影。
7. 符号结构体:每个 symbols_type 按 target 实际运行时闭包生成 `struct`,`Expression<T>.Eval(in TSymbols)` 免装箱;仅 convert 所需的 authoring 输入不因此进入 bytes/读取面。
8. target 不可变 `RuntimeSchemaRegistry`:每个 target 由生成代码提供具体实现/单例,`ExpectedSchemaHash` 编译为当前完整 `SchemaDescriptor.SchemaHash`,`ExpectedExportTarget` 编译为当前稳定 `ExportTargetId`;按 live table id 确定序固化该 target 的 table id → CLR type/factory/accessor/patcher 绑定。registry 没有运行时追加/替换 target 或 binding 的入口,作为 M7 `RuntimeDatabase.Open` 的不可省略代码期望输入。
9. 内嵌身份常量:每个 target 生成类型、registry `ExpectedSchemaHash`/`ExpectedExportTarget` 必须来自同一 candidate descriptor 与 target projection;运行期由 M7 把 registry 期望的 `(schema_hash,target_id)` 与 Excel/bytes source 硬比较。v1 固定使用完整单 hash + target id，不另增 target runtime hash。

retired table 不产生任何 authoring/runtime 新类型、cell parser/writer、bytes accessor、patcher、属性树元数据或 registry binding,也不进入任何 target bytes table 集。空 target 集的 live 表仍产生 authoring surface,但不产生 target runtime surface。一次 codegen 必须按 candidate 的完整 authoring + target 输出清单原子替换派生物,禁止旧 target 类型或 binding 残留为幽灵读取面。

`<generatedCSharpDir>/codegen.manifest.json` 是 codegen 对生成根的唯一所有权清单：

- manifest 确定序记录工具版本、SchemaHash、catalog hash、每个逻辑输出路径及内容 hash；只允许删除上一份有效 manifest 明确拥有且仍位于声明根内的文件。
- 目标路径存在未拥有文件、manifest 无法验证或路径经 symlink/junction 逃逸声明根时 blocker。未知文件和未知目录永不因普通 generate 被清理。
- generated C# 根可位于 Project 外；M1 只产出声明根相对 mutation，声明根、跨根计划与恢复协议归 M4。相同 proto 与 catalog 在任意合法绝对落点生成的文件内容和 manifest 逻辑项必须相同，物理绝对路径不得进入生成源码或 SchemaHash。

## 7. 模块验收测试

1. determinism:schemaDir 递归枚举顺序、cwd、proto 文件顺序/空白/注释/option 书写顺序变化 → 归一输入集、descriptor 字节与 schema_hash 不变;schemaDir-relative import 成功,逃逸/缺失 import 失败。
2. 身份:字段/表/枚举值 rename(number 不变)→ id path 不变,兼容分析判为 rename;number 变化 → 判为删+增。
3. reserved/tombstone:字段/enum number 复用 reserved → XDB003;live/retired 当前唯一域重复 table id → XDB001;复用 previous retired id → XDB019。
4. preset:CommonHeader 展开后 descriptor 只见普通字段,子字段 id path 以 common 字段 id 为前缀。
5. 全 shape 覆盖:§3 表每行至少一个描述符快照测试(golden descriptor json 比对)。
6. expression:符号解析、类型检查、非法符号/结果类型 → XDB009 或编译 error。
7. weighted:weight_field/condition_field 指认与非法指认。
8. map:string 键、int32+map_key_enum 键、message 值(子表形)三例;重复键留给 M6 导入测试。
9. oneof:variant 集合进 hash;variant 增删改变 hash;XDB014。
10. 示例 proto(§4)编译 → descriptor golden + codegen 编译通过 + `skill.Damage`/`FindProperty("cost.mp")` 绑定表断言;generated registry 的 live table/type/accessor 绑定与 descriptor 一致。
11. TableKind/状态:匿名形状 / EMBEDDED / live ASSET / retired ASSET 各一例;kind 未指定与 EMBEDDED 带 id/key → XDB015;retired 非 ASSET → XDB019。
12. option 枚举:AUTO expand 物化确定性(纯标量 → 单 cell,含嵌套 → 展开列);表未声明 target 固定为 `{client,server}`,字段未声明继承、显式空集无运行时读取面,集合书写顺序不影响 descriptor/hash。字段扩大父集、key 未覆盖表 target、某 target 的硬引用目标/key 不可见 → XDB016；非法/重复 id 与 legacy/显式冲突 → XDB020。legacy 表 DEFAULT/ALL、字段 DEFAULT/ALL、EDITOR_ONLY 分别与正文的显式集合等价,未来加入 `lite-client` 时 legacy ALL 不得自动扩大；delete_policy 三值在删除计划中的行为(BLOCK 优先)。
13. 单格式声明器:`30#0`(struct 1 层)、`sword&2#potion&5`(repeated struct 2 层)、`8:00-12:00@2.5`(嵌套 struct 2 层)、`101->102`(多字符分隔)、`mp:30#hp:0`(named 自定义 pair/kv)、map 位置序各一例往返;缺尾段/空段缺失态与超段 error;权重省略简写;分隔串转义;互为子串 → XDB017;文件默认与字段覆盖的物化确定性;自定义 `ICellFormat` 注册为 codec kind 的往返与替换一例。
14. 格式三阶段:窗口解析三态(唯一成功/多成功值一致/`format.ambiguous`,含层级对调陷阱 `1&2#3&4`);写出恒 canonical;normalize 重写与命中归零报告;删除 legacy 后旧 cell fail loud;SchemaDefaults 级窗口(全项目换习惯)一例;legacy 嵌套与同身份 → XDB018。
15. 入口等价:分别由文本编辑和结构化 mutation 产出语义与字节均相同的 `.proto` → descriptor、schema_hash、C# 与 `RuntimeSchemaRegistry` 逐字节一致；删除向导状态/cache 后重建结果不变。
16. mutation/编译原子性:建表、加字段、保 number rename、reserved 删除、live 表退役各一例成功提交；构造重复 number、XDB019、lint blocker、codegen/registry 失败,断言原 `.proto`、descriptor、C# 与 registry 全部逐字节不变且无部分新文件可见。退役成功时旧表生成文件与 registry binding 在同一提交中消失。
17. 离线自包含:仅把 self-contained CLI 发布物复制到空目录,清空 PATH、拒绝网络并确保无 protoc/.NET SDK；经结构化入口创建首个 proto、再修改字段,两次均能从内嵌 system import 产出 descriptor、C# 与 registry,且无进程查找或下载行为。项目本地 import 成功,缺失 import 只报本地错误。
18. 表退役生命周期:previous published live ASSET → 保留同 message/id并设 retired,唯一合法转换成功;descriptor 从 Tables 的 live ASSET 集移入 RetiredTables且 hash 改变。断言 retired 无 workbook 要求、C# 类型、runtime registry binding 与 bytes table;table-id 建议同时避开 live/retired。新建即 retired、无 previous live、删除 tombstone、清标记、改 id及任意 id 复用分别命中 XDB019;当前集合重复另命中 XDB001。同 id retired rename 判兼容 rename且状态仍 retired。
19. retired 确定性与历史 diff:含多个 live/retired 的 schema 在文件/枚举顺序变化、cache 删除/污染后重建出相同 Tables/RetiredTables、descriptor 字节与 hash;previous/current diff 能区分 live→retired、retired 保持、tombstone 消失/复活/复用。previous 变化不得改变由相同当前 proto 编译出的 candidate 字节,只改变转换 lint 结果。
20. per-target generated registry/runtime surface:分别生成 client/server registry,反射断言二者不可变、无运行时 Register/Replace API,`ExpectedSchemaHash == SchemaDescriptor.SchemaHash`,`ExpectedExportTarget` 分别为正确 `ExportTargetId`,binding 按该 target 可见的 live table id 确定序且不含 retired。client-only/server-only/空集字段只出现在对应或任何 runtime surface 之外,不得经反射/侧表绕过；相同完整 hash 的 client/server 仍因 target id 不同而不可互换。分别污染/删除 descriptor cache、旧 target C# 与旧 registry 后运行 descriptor/codegen、layout、check/import、convert consumer,断言都重新观察 schemaDir 当前 proto并得到同一 hash/对应 target 绑定；过期 cache 不得让任何 consumer 接受旧 schema/target。
21. 表初始化器:内置 initializer 仅用表名、一个简单 key 与数个简单字段产生有效 live ASSET draft,自动字段/option 全部进候选 proto 与计划。注册一个简单 C# initializer 自动补一字段及 option,断言与直接产生同一 draft 时的 proto/descriptor/C# 逐字节等价。异常、重名字段、非标量 key 或其他非法输出均报 blocker 且零替换；提交后删除 initializer/会话/cache 仍能仅凭 proto 得到相同下游结果。
22. 导出目标策略:标准策略为未指定表补 `{client,server}`,为未指定字段复制父 effective set；显式 client-only 表下的未指定字段保持 client-only,字段显式 client-only/server-only/空集选择均不被覆盖。注册一个确定性 C# 策略补 `lite-client`,断言预览展示其全部 effective 集且确认后以显式 target id 写入 candidate proto。策略异常、输出非法/重复 id、扩大父集或破坏 key/ref target 闭包均报 blocker 且零替换；提交后删除/替换策略并清空会话/cache,build/codegen/check/convert 仍仅凭 proto 得到相同 descriptor/hash/target 产物,且策略 `Id` 不改变 canonical 结果。
23. 系统 import 编辑镜像:初始化后普通文件解析能找到 `exceldb/options.proto` 与全部传递 Google imports；镜像存在、缺失、旧版及修复后编译同一业务 proto，descriptor、SchemaHash、C# 与 bytes 均逐字节相同。修改镜像、伪造所有权记录、增加未登记 reserved path 分别产生准确 blocker；真正缺失的业务 import 报完整 import chain。Windows Hidden/ReadOnly 设置与幂等修复另覆盖 warning 路径。
24. codegen 所有权:默认与项目外 `generatedCSharpDir` 均生成固定 `Authoring/Runtime/<target>` 包装和 canonical manifest。未知文件不删除、已拥有旧文件可清理、路径碰撞/损坏 manifest/symlink 或 junction 逃逸均 blocker；改变绝对落点不改变源码、逻辑 manifest 或 SchemaHash。

## 8. 与仓库现状衔接

- 旧实现(`ConfigDatabase`/`ExcelTableLoader`/SkillEditor 样例/旧测试/excels 样例数据)已于 2026-07-09 整体移除(D11);历史实现经 git 历史查阅,`UndoStack`/`DependencyGraph`/xlsx IO 需要时按件回捞参考。
- 用于验证部分 schema 契约的旧 `poc/SchemaPoc` 已按用户要求于 2026-07-13 删除,不作为正式工程起点,也不从中迁移实现代码。
- M1 既有纯 C# 实现已覆盖内嵌 schema 编译、canonical descriptor/hash、lint、codegen、初始化器与导出目标策略；Project v2 的完整 system proto catalog、编辑镜像排除、固定 C# 包装和所有权 manifest 尚待按 §7 第 23-24 条重验，故本模块实现状态保持 Pending verification。`options.proto` 保持相对 v1(git 历史)的兼容姿态:`TableOptions` 沿用 50001 号位,50011-50015 reserved,`ExternalRef` 由 reference family 取代；proto parser/descriptor 依赖仅存在于 Schema Tooling,不得进入 Core Runtime。

## 9. 跨模块边界与架构决策

- RowRef canonical 身份固定为 `(table_id,row_guid)`；`row_guid` 为 16-byte 非零值。Excel 人读 token、key rename 修复与 bytes 编码由 M5/M6/M7 投影，但不得改变该身份。
- 各 shape 的 cell 文法、表头三行布局、下拉与批注 → M5。
- 导入校验时机与诊断码、key 索引 → M6。
- bytes 布局与运行时访问器细节、`(SchemaHash,ExportTargetId)` source/session 门禁与换 target 生命周期 → M7。
- 表 ID 退役与永久 tombstone 已由本文 §1.2/§2/§5 裁决,不再是开放边界:M8 只消费 live/retired previous diff 做兼容报告与发布门禁,M7 只消费排除 retired binding 的 generated registry；任何模块不得另建第二份 retired-id 注册表。
- schema 变更兼容矩阵与迁移 → M8:字段级三阶段(新 number 新增 → 数据迁移 → reserved 删除)、位置序 format 的非尾部插字段段位漂移检查(需对比上次发布的 descriptor 快照,声明器自身无历史知识)。
- 空目录 Project 初始化、交互提示/页面、CLI 命令形态与阶段编排 → M4；本文只拥有结构化 mutation 的事实源、事务与离线 Schema Tooling 契约。

## 10. 决策记录

约定:本节按时间追加,不回改;修订以新条目引用旧条目。每条决策是记忆锚,不是第二份规格——机制细节只在正文,此处不复述。旧全局裁剪台账已归档于 [`docs/archive/2026-07-authority-merge/`](../archive/2026-07-authority-merge/README.md),仅供历史追溯。

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

---

### D12(2026-07-11)向导维护 proto,Schema Tooling 随 CLI 离线内嵌

> 再次考虑工作流.
> 预期 会有一个可执行文件, 当我爸这个可执行文件粘贴到一个空文件夹时, 该文件夹会引导我将这个空文件夹初始化为一个项目.
> 该可执行文件还将引导我:
> 从0创建一个excel.
> 修改表结构, 并生产cs数据文件,
> 生产cs文件的对应数据,

**可执行文件是 schema 的易用门面,不是新的结构事实源——向导把用户意图事务性提交为 proto,由同一 descriptor/codegen 管线生成 C# 投影；要让“复制到空目录即可开始”成立,parser、options 与模板必须随 self-contained CLI 离线内嵌,不能把 PATH、SDK 或网络偷偷变成前置条件。**

落点:§1 取消“必须手写”并增加结构化 mutation 契约,§5 固定离线自包含边界,§7 增加入口等价/原子性/隔离环境验收；交互形态与 Project 编排仍归 M4。

---

### D13(2026-07-11)表退役留在 proto,运行时期望留在 generated registry

推演自发:表 id 若随 message 删除而消失,新表建议与 fresh clone 都无法证明未复用；运行时若从 source 自己取得 expected hash,错误代码与错误数据也会“自洽”通过。

**同一份版本化 proto 同时保存 live 声明与单向 retired tombstone,因此 cache 全删后仍能重建永久 table-id 占用集合；同一 candidate descriptor 再生成不可变 `RuntimeSchemaRegistry`,把代码期望的完整 hash 与 live 类型绑定交给 M7 Open,而 retired 永远不回到运行时表面。**

落点:§1.2、`TableOpts.retired = 8`、XDB019、SchemaDescriptor live/retired 集、§6 registry、§7 第 18-20 条；表 ID tombstone 不再列为开放决策。

---

### D14(2026-07-12)首次建表用简单字段,自动补齐交给 C# 表初始化器

> 所以应该填结构支持的简单字段， 甚至允许经过选项自动设置。 关于这一项， 希望在代码实现时抽象为表初始化器接口， 允许自定义（简单cs代码扩展即可， 不再通过复杂的自定义声明）

**首次建表的扩展点是“代码策略填 draft”,不是“再造一种表模板语言”:`ITableInitializer` 确定性地把简单输入/选项展开为候选结构,数字身份、lint、codegen 与提交仍由原管线统一裁决；最终 proto 完整吸收结果,所以 initializer 不会变成第二事实源。**

落点:§1.1 `ITableInitializer`/draft 语义与单一事实源约束,§5 自包含初始化资源,§7 第 21 条；用户交互与定制发行边界分别由 M3/M4 投影。

---

### D15(2026-07-12)字段导出用显式目标集,自动归类交给计划期 C# 策略

> 另外允许表的字段声明为，服务端导出和客户端导出， 这一块也建议抽象为借口策略， 暂时这个策略仅关心前后端， 未来可能有简易前端之类的

**“前端/后端”不是会随枚举扩张而换义的二选一开关,而是字段所属的稳定 runtime target 集；`IExportTargetStrategy` 只在 create/edit 计划期帮助填写这组简单事实,确认后 proto 完整接管,所以未来增加 `lite-client` 只需普通 C# 扩展和显式 id,不会让历史字段或运行时重新分类。**

落点:§2 `ExportTargetSet`/legacy 映射与 `IExportTargetStrategy`,§5 完整 hash + target identity,§6 每 target codegen,§7 第 12/20/22 条；转换、运行时与兼容发布边界分别由 M4/M7/M8 投影。

---

### D16(2026-07-13)删除已有实现,按模块计划推进

> 删除已有实现, 按模块计划推进.

**有限 PoC 不再承担正式实现的衔接职责；工作区回到规范驱动状态,后续实现从 M1 的模块验收开始逐项建立。**

落点:§8 仓库现状衔接；总纲 M1 实现状态回到 Not implemented。

---

### D17(2026-07-14)系统 proto 磁盘镜像只解决编辑器 import，编译仍信任内嵌 catalog

> proto 内部 import 的文件会被提示不存在, 想办法解决这一点.

**同一份内嵌 catalog 同时喂给编译器和镜像修复器，才能既让普通编辑器在磁盘找到 import，又保证隐藏镜像不会悄悄变成第二 schema、改变 fingerprint 或污染 descriptor；codegen 也必须用 manifest 证明自己只清理自己拥有的文件。**

落点:§5.1 system proto catalog/镜像与 reserved path 排除，§6 固定 C# 包装和 manifest，§7 第 23-24 条；本裁决覆盖旧的“系统 import 只从内嵌资源解析且无需磁盘投影”在编辑器体验上的缺口，但不改变编译事实源。
