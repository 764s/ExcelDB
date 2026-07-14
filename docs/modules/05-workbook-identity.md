# 模块 5:Workbook 物理契约与行锚

设计状态：**Dependency-Complete**；实现状态：**Verified**。本文是 M5 Workbook/Identity 领域的唯一 owner，消费 M1 的 proto/数字结构身份与 M2 的 asset path/GUID 门面语义。跨模块权威规则与架构决策见 [`docs/spec/README.md`](../spec/README.md)。本文固定 workbook 区域所有权、三行表头、metadata、row guid/rev、key/path 的定位角色、身份扫描规则与写回保真边界。

归档计划中的 C# 特性、`FormerName`、`Object`/`ScriptableObject` 基类或名字身份均无规范效力。纯 Excel 新行的身份固化、退役 table id 的 proto tombstone 与 `AssetIdentity`/`RowRef` 表示分别由 §9.2/§9.3/§9.1 裁决。

## 1. 职责与依赖方向

本文拥有:

- workbook 内哪些区域由 schema 投影拥有、哪些区域由策划拥有。
- 表、字段、子表与隐藏伴随数据在 xlsx 中的物理落点约束。
- row guid/rev 的 workbook 表示及只读身份扫描结果。
- `DataPreparePlan` 固化候选 row guid 时允许触碰的 workbook 物理边界。
- generate/save 对非目标内容的保真边界。

本文不拥有:

- proto 结构语义、table/field/enum/variant 数字身份与兼容判定——归 M1 / 模块 8。
- AssetDatabase 的调用形态、挂载集合与查询语义——归 M2。
- cell 解析、值校验、resident object、依赖图、dirty/Undo、三方合并与保存事务——归模块 6。
- bytes、运行时 handle、热载、切源与 provider——归模块 7。
- CLI、Unity 窗口、CI 与版本控制入口——M3/M4 只投影本文操作，不得定义新的 workbook 语义。

依赖必须单向:

```text
M1 SchemaDescriptor
        ↓
M5 Workbook projection + row scan
        ↓
M6 import/edit      M7 convert/runtime      M8 compatibility
        ↓                    ↓                       ↓
              M2 facade / M3 workflow / M4 tools 的具体接线
```

## 2. 事实源、投影与定位信息

| 信息 | 角色 | 事实源 |
| --- | --- | --- |
| 表/字段/枚举/variant 的结构与数字身份 | 结构事实 | `.proto` + M1 `SchemaDescriptor` |
| 业务数据值 | authoring 数据事实 | xlsx 数据区 |
| row guid | workbook 行机器锚 | 行的 `__guid` 伴随 cell |
| row rev | 行内容修订提示 | 行的 `__rev` 伴随 cell；不是身份 |
| key | 可变业务定位符 | M1 key 字段对应的数据 cell |
| asset path | 门面展示与查找路径 | workbook 路径 + 表定位段 + key 的投影(M2§2) |
| message/field 名、sheet 名、列号、行号 | 绑定/展示/物理位置 | schema 与 workbook 投影；均不是行身份 |
| metadata、表头、下拉、批注 | 可重建投影/加速信息 | SchemaDescriptor + workbook 当前布局 |
| 导入快照 | 本地历史与合并 base | 最近一次成功导入/保存的缓存；不是跨机事实源 |

由此得到以下硬约束:

- schema 不能从表头或 metadata 反推；两者不能创建 schema 中不存在的结构事实。
- key、asset path、sheet 名、行号或列号变化，不得单独解释为行身份变化。
- row guid 随 Excel 排序、剪切和跨 sheet 的受控移动跟随该行；row rev 不参与相等性判断。
- M2 暴露的编辑期 `GUID` 投影为 ASSET 行的 row guid；完整 canonical identity 仍必须同时携带 table id。

## 3. Workbook 与 sheet 所有权

### 3.1 Workbook 级

一个受管 workbook 包含:

- M1 中 `kind: ASSET` 表的主数据 sheet。
- repeated message/map message 等需要物理子表时生成的子 sheet。
- 隐藏 metadata sheet `__exceldb`。
- 隐藏引用下拉投影 sheet `__exceldb_keys`。
- 任意数量的自由 sheet。

M1 descriptor 中 `retired = true` 的 ASSET 表是结构 tombstone,不再要求当前 workbook 存在受管 sheet。历史 workbook 若仍含该 table id 的 sheet/metadata,其内容进入 `retired-preserved` 只读保留域:默认原样保留,不得因当前 schema 不再需要数据而由 generate/save/data prepare 自动删除、重建或迁入 live 表。

匿名 embedded shape 与 `kind: EMBEDDED` 共享形状本身不拥有独立资产 sheet；它们只能作为展开字段、单 cell 值、子表元素或其他组合形状被物化。子表元素行具有机器行锚，但不因此成为 ASSET；需要被外部作为资产引用时，必须在 schema 层提升为 ASSET(M1§3)。

### 3.2 每个受管数据 sheet 的三行表头

```text
行 1  展示行:display_name；批注可投影 header_comment、类型说明、枚举/引用提示
行 2  字段路径行:proto 字段名组成的 property/column path，例如 cost.mp
行 3  类型展示行:只读人读投影，例如 int [0..9999]、enum DamageType
行 4+ 数据区
```

- 三行表头均为 schema 投影拥有；策划不得把业务数据放入这三行。
- 行 2 是 workbook 绑定提示，不是字段身份；字段身份始终是 M1 的数字 id path。rename/aliases 的绑定与迁移由模块 6/8 消费历史 descriptor 决定。
- 行 1/3、批注、下拉和数据有效性不参与行身份。它们可由 generate 重建。
- 数据起始行缺省为 4；若未来 format version 允许其他起始行，只能由 `__exceldb` 显式记录，禁止启发式猜测。

### 3.3 列与 sheet 的所有权

- **schema 拥有列**:当前 descriptor 能绑定的字段列、子表父锚列，以及 `__guid`/`__rev` 伴随列。
- **辅助列**:行 2 为空或以 `#` 开头；系统不得解析、重排、清空或覆盖其数据。
- **未知旧列**:行 2 有路径但当前 schema 无绑定；在兼容操作裁定前按辅助内容保留，不能因未知而删除。
- **自由 sheet**:`__exceldb` 未登记且不使用保留名的 sheet；系统不得读写。
- **保留 sheet**:`__exceldb`、`__exceldb_keys` 及 metadata 登记的受管数据/子表 sheet。保留名冲突必须阻断写回，不得静默改名。

受管列的“拥有”只授权对应操作触碰计划内 cell，不授权整列或整行重写。策划仍拥有行 4+ 的业务值；generate 只维护其结构投影，SaveAssets 只提交已进入写回计划的业务变化。

## 4. Metadata 与可重建投影

### 4.1 `__exceldb`

`__exceldb` 是隐藏、schema 拥有的纯文本记录区；不得依赖 Excel 单元格数字格式。其逻辑记录至少包含:

```text
[workbook]
  workbook_guid=<32hex>
  schema_hash=<16hex>
  format=<u16>
  saved_utc=<iso8601>

[tables] 每项
  table_id=<M1 stable id>
  proto_name=<fully-qualified name>
  sheet_name=<physical sheet>
  data_start_row=<u32>

[fields] 每项
  table_id=<M1 stable id>
  field_id_path=<numeric id path>
  property_path=<proto name path>
  column=<u32>
  span=<u32>
```

- `workbook_guid` 标识 workbook 容器和本地快照命名空间，不是资产或行身份。
- `schema_hash` 记录生成该投影时的 M1 schema 版本；不匹配时只能进入兼容检查，不能由 metadata 覆盖当前 descriptor。
- `format` 只表示 workbook 物理格式版本；format 高于工具可读版本时只读打开并阻断写回。
- `saved_utc` 是审计展示信息，不参与身份、兼容或内容相等判断。
- table/field 数字 id 用于关联结构身份；名字、sheet、path、column/span 是当前投影和定位信息。

metadata 缺失、重复或与实际 sheet/header 不一致时，扫描必须报告结构漂移。实际 xlsx 内容不得因 metadata 陈旧而被忽略；修复只能进入显式 generate/save 写回计划，不能在只读 mount/check 中暗改文件。

### 4.2 `__exceldb_keys`

`__exceldb_keys` 是引用下拉的数据投影:每个可引用 ASSET 表输出一列当前可选 key token，供 Excel data validation 使用。它不是引用事实源、key 索引事实源或 RowRef 存储；删除/损坏该 sheet 只使烘焙提示陈旧，generate 可重建。

## 5. 行伴随列

每个受管主表行和需要独立扫描的子表元素行都带隐藏伴随列:

```text
__guid  128-bit row guid，canonical 文本为 32 个小写十六进制字符
__rev   u32 行内容修订号
```

约束:

- 伴随列属于 schema/system 拥有区，跟随对应数据行移动。
- `__guid` 是 workbook 中识别“仍是同一物理业务行”的机器锚；不得从 key、行号、内容或路径派生。
- `__rev` 仅用于快速发现内容是否可能变化；内容相等最终以 canonical 值比较为准。
- 系统成功提交一行的业务变化时，写回计划必须同时更新该行 `__rev`；失败或回滚不得提前递增。
- guid/rev 的生成或修复只能作为写回计划项提交；扫描阶段只产生候选结果和诊断。
- `pending-new` 的候选 row guid 必须是新生成的非零 128-bit 值,不得从 key、行号、路径或业务内容派生；候选在成功写入 `__guid` 前不是持久身份,丢弃计划后可以重新生成。
- 子表 row guid 是 merge/hot-reload 的元素锚，不自动获得 M2 资产加载、路径或外部引用资格。

## 6. Key 与 asset path

- key 的组成、顺序和类型来自 M1；它是业务唯一性约束和人可读定位符，不是不可变身份。
- ASSET 的 asset path 沿用 M2§2，只作为加载、查找、日志和工具持久化之外的展示定位。需要跨 rename/move 持久保存的工具状态不得只存 path。
- 同一 row guid 的 key 变化是 rename 候选；是否有效仍需模块 6 检查 key 唯一域、数据有效性和保存门禁。
- workbook 移动、表/字段 rename、sheet 调整或行排序可以改变 path/位置，但不得据此生成新的 row guid。
- RowRef 的 Excel 人读 token 固定为 `<table-id>:<escaped-key-components>`；table id 使用十进制，key component 使用 UTF-8 percent-encoding，复合 key 以未转义 `|` 分隔。导入先按 table id 限域，再按当前 key 索引解析为 `(table_id,row_guid)`；写回由当前 key 重建 token。key rename 必须在同一影响计划内修复 token，token 本身不是 identity。

## 7. 身份扫描

身份扫描是只读分析阶段。输入为当前 workbook、M1 descriptor，以及可选的最近成功快照；输出为行观察集、身份候选、漂移/冲突诊断和后续写回计划素材。扫描本身不得修改 xlsx。

### 7.1 基本顺序

```text
读取 workbook/metadata
→ 绑定受管 sheet 与列
→ 逐行采集 physical location、key、__guid、__rev、内容指纹
→ 在当前扫描域检查 guid/key 重复
→ 与可用快照对齐
→ 分类 retained / pending-new / renamed / removed / duplicated / recovered
→ 仅产报告与候选变更
```

行号只用于诊断定位；禁止用行号维持跨次扫描身份。快照只提供历史证据；有效且唯一的 workbook row guid 优先于快照中的位置或 key。

### 7.2 当前身份规则

1. **有效且唯一 guid**:保留该 row guid；排序、剪切或物理行号变化不改变分类。
2. **guid 为空**:分类为 `pending-new`，分配候选 row guid；候选只作为 §9.2 `DataPreparePlan` 的输入,扫描与只读入口均不固化。
3. **guid 重复**:若快照能以 `(key,内容指纹)` 唯一识别原行，则原行保留 guid，其余行转 `pending-new`；证据仍歧义时按物理顺序首行暂留、其余转 `pending-new`，并报告 `identity.duplicated`。
4. **快照有 guid、当前 workbook 无该 guid**:分类为 `removed` 候选；delete policy、依赖闭包及最终提交归模块 6。
5. **同 guid、key 改变**:分类为 `renamed` 候选；row guid 不变。若新 key 与其他行冲突，所有相关行仍保留各自 guid，但 key 查询和写回被错误门禁。
6. **guid cell 被清空**:若 key 能唯一匹配快照原行，则分类为 `recovered` 并候选恢复原 guid；否则按 `pending-new` 处理。恢复必须产生诊断，不能静默发生。

快照缺失或损坏时，有效且唯一的 workbook guid 仍可直接使用；需要历史证据的 duplicated/recovered/removed 判断必须降级并报告，禁止用内容相似度静默猜身份。

retired 表按当前 descriptor 的原 proto message 与 table id 识别。其 sheet 缺失不是 drift,历史 sheet 存在则只报告 `table.retired-present` 并按 §3.1 保留；扫描不得把其中行分类为 live `retained`/`pending-new`,不得分配候选 row guid,也不得把 message 消失、同 id 换名重现或 `retired → live` 猜成合法删除/复活。此类跨版本合法性只消费 M1/M8 的 lint/兼容结论,M5 只负责保留物理内容并投影诊断。

## 8. 写回保真边界

任何改变 xlsx 的操作都必须先形成明确写回计划。本文只规定计划允许触碰的物理边界，不定义模块 6 的事务 API。

### 8.1 Generate

- 可创建缺失的受管 workbook/sheet、三行表头、schema 列、伴随列、metadata 与下拉投影。
- 可更新展示行、类型行、批注、数据有效性、metadata 的当前投影。
- 不为 retired 表创建缺失 sheet,也不删除、清空或重投影历史 `retired-preserved` sheet/metadata；物理 purge 只能来自 M8 明确分级且获授权的兼容计划。
- 不得仅因 schema 未识别就删除未知列、辅助列或自由 sheet。
- 不得为了“对齐模板”整行清写数据区；涉及业务值迁移、purge 或 rekey 必须是显式兼容计划项，由模块 8 分级。

### 8.2 Save/normalize/兼容 apply

- 只触碰写回计划列出的 cell、行结构、伴随列和 metadata 项。
- 非计划 cell 的值、公式、样式、批注、合并、列宽、行高、隐藏状态、未知 OpenXML part、辅助列和自由 sheet 必须保留。
- 写回经临时文件完成后，必须复读计划内值与关键 metadata；验证失败不得替换原文件。
- 后端支持 cell 级 patch 时优先 patch；若必须重写包，须逐区块回拷非拥有内容并验证。无法证明保真即 blocker。
- 失败、文件锁、冲突或验证错误时，原 workbook 保持不变；候选 guid/rev 与 metadata 不得部分提交。

### 8.3 只读入口

mount、check、diff 和 dry-run 可以更新明确标为本地缓存的快照/报告，但不得以“顺手修复”为由写 workbook。任何 metadata、guid、rev、表头或下拉修复都必须在报告中成为可见计划项，再由获授权的写操作提交。check 遇 `pending-new` 必须报告 `identity.pending-new` error 并保持 workbook 零写入；diff 可以报告该状态但不得替任一侧固化身份。

### 8.4 `DataPreparePlan` 的物理写入边界

`DataPreparePlan` 是模块 6 拥有的专用写回事务,用于在 convert 前把 §7 扫描得到的候选 row guid 固化进 xlsx。本文只规定它可以改什么:

- 每个计划项必须指向扫描分类为 `pending-new` 的确切受管行,并携带该行的候选 row guid、观察到的 `__guid` cell 状态、行内容指纹以及所属 workbook/schema 身份；纯 Excel 新行的观察状态为空,duplicate 分支转入 pending 的行则保留扫描所见旧值。候选在本次计划集合和当前 workbook 扫描域内必须唯一。
- commit 只允许把计划列出的、状态仍与观察值相同的 `__guid` cell 写为对应候选值。业务 cell、公式、行顺序、表头、样式、metadata 与其他伴随 cell 均不得改变；身份固化不是业务变化,不得递增 `__rev`。
- 计划必须绑定扫描所见的完整 workbook 内容指纹。commit 前指纹、schema 身份、行内容指纹、物理定位或 `__guid` 观察状态任一变化,计划即 stale,必须零写入并重新扫描/计划。
- 临时候选 workbook 必须复读确认所有计划 guid 正确、候选唯一、全部业务 canonical 值和 `__rev` 未变,并满足 §8.2 的计划外保真后,才允许 workbook 级原子替换。
- 成功替换后,写入 xlsx 的 `__guid` 才成为跨机器 row guid 事实；本地候选、报告或快照不能提前取得该地位。SaveAssets 可以把同一个 `DataPreparePlan` 组合进自身 workbook 事务,不得另设身份分配路径。

存在任一 `pending-new` 时,convert 必须以 blocker 拒绝且不得隐式执行本计划；runtime source 同样不得接收未固化身份。操作者必须先经获授权入口显式提交 `DataPreparePlan`（或提交复用它的 SaveAssets 事务）,再重新 check/convert。

## 9. 身份决策

### 9.1 OD1:统一 `AssetIdentity` 与 `RowRef` 表示(已裁决)

1. canonical `AssetIdentity` 固定为 `(positive table_id, non-zero 128-bit row_guid)`。table id 来自 M1，row guid 来自 ASSET 行 `__guid`；二者共同参与相等与排序。项目挂载域仍要求 row guid 全局唯一，以保持 M2 `GUID → path` 无歧义；重复值是 blocker，不靠表或物理顺序降级消歧。
2. M1 `RowRef { table, row_guid }`、authoring canonical 值与 converted bytes 均承载同一二元组；protobuf `row_guid` 恰为 16 bytes，xlsx canonical 文本仍为 32 个小写十六进制字符。全零、错误长度或未知 table id 均无效。
3. Excel cell 使用 §6 的人读 key token；导入必须在当前索引中唯一解析后才得到 canonical RowRef。token/key/path 改变不改变 identity；rename 写回修复 token，无法唯一解析产生 `ref.unresolved`，不得用历史名称或相似度猜测。
4. 子表 row guid 只锚定所属父资产内的元素，其 canonical element identity 为 `(parent AssetIdentity, numeric field-id path, element row_guid)`，不构成独立 AssetIdentity，也不能成为外部 RowRef 目标。
5. M7 `AssetKey` 是含 generation 的 session-local handle，仅加速定位上述 AssetIdentity；它不进入 xlsx、descriptor、bytes identity 或持久工具状态。

### 9.2 OD2:纯 Excel 新行的身份固化(已裁决)

已接受事实:策划可在 Excel 新增 `__guid` 为空的行；扫描将其识别为 `pending-new`，且只读入口不能暗改 workbook。

裁决:

1. 身份扫描继续只读并为每个 `pending-new` 产生候选 row guid；候选仅是计划素材,不写 snapshot 充当跨机器身份。
2. 固化只经 M6 `DataPreparePlan` 的 plan → report → commit 事务。计划绑定 workbook 内容指纹、schema 身份、观察行指纹与确切 `__guid` cell 观察状态；stale 一律拒绝并重新扫描。
3. commit 只写候选 row guid,业务值、行结构、`__rev` 与其他投影不变；临时文件复读和 workbook 级原子替换沿用 §8.2/§8.4。
4. check 只报告 `identity.pending-new` error；convert 与 runtime 对任何未固化行硬拒绝,不得为方便而暗写 workbook。
5. 独立数据准备入口与 SaveAssets 必须复用同一个 `DataPreparePlan`。SaveAssets 可以将它与本次业务 WritePlan 合并为一次 workbook 替换,但不能重新计算另一组身份。

因此,无 Unity 的 pre-commit/CI check 也能稳定发现未固化行；要让新行跨机器有效,必须在提交 xlsx 或 convert 前显式完成数据准备写回。该裁决解除新行身份固化时机对 M6/M3/M4 的阻断,但不预判 §9.1 的最终 `AssetIdentity` 表示。

### 9.3 OD3:退役 table id 的 proto tombstone(已裁决)

裁决:

1. proto 是 table retirement 的唯一事实源。一个已发布 ASSET 表退役时,必须保留原 proto message 与原 `(exceldb.table).id`,并设置 `TableOpts.retired = true`；删除 message 或把旧 id 只留在 cache/descriptor 历史中都不构成有效 tombstone。message 内字段的退役形态继续遵守 M1/M8,不由 M5 加码。
2. live 与 retired table id 位于同一唯一域。retired id 永久占位,不得分配给另一 message；同一 message 从 retired 恢复为 live 也不得由 workbook 扫描、名称相似或数据重现推断为合法,其跨版本判定归 M8 显式兼容规则。
3. retired 表不再要求当前 workbook sheet、authoring 行或 runtime/converted 数据；缺失这些数据不产生结构漂移。历史 workbook 已有的 sheet、metadata 与业务数据默认按 §3.1 `retired-preserved` 保留,不参与 live 导入、DataPrepare、convert 或 runtime 注册。
4. 扫描发现“旧 message 直接消失”“retired id 被另一 live/retired message 使用”或“历史 sheet 被当作 live 表复活”时,只能投影 M1/M8 的 blocker/兼容诊断并保留原 workbook,不得自行把它解释为合法删除、ID 复用或复活。

具体 `TableOpts.retired` descriptor 形态、唯一性 lint 代码与退役/复活兼容矩阵分别归 M1/M8；M5 只拥有历史 workbook 的识别、默认保留和扫描报告边界。

## 10. 验收边界

1. 区域所有权:generate/save 后，辅助列、自由 sheet、未知列与非计划 OpenXML part 保持不变。
2. metadata:descriptor 投影确定；metadata stale/缺失只产漂移与修复计划，只读扫描零写入。
3. 行锚:排序、剪切、改 key 不改变有效 row guid；`__rev` 不参与身份比较。
4. 扫描规则:§7.2 六类各有 workbook + snapshot golden；快照缺失有明确降级诊断。
5. 原子性:复读失败、文件锁或计划中止时，workbook 字节保持原样，guid/rev/metadata 无部分提交。
6. pending 只读性:空 `__guid` 行经 mount/check/diff/dry-run 后 workbook 字节不变；check 报 `identity.pending-new` error,候选只存在于扫描/计划素材。
7. 身份准备保真:`DataPreparePlan` 成功后只对应 `__guid` cell 改为计划候选；所有业务 canonical 值、公式、样式、行序、metadata 与 `__rev` 不变,候选唯一且复读一致。
8. stale 与失败:计划形成后分别修改 workbook 指纹、目标行内容、位置、schema 或 guid cell → commit 全部拒绝且原 workbook 字节不变；文件锁/复读/替换失败同样零部分固化。
9. 门禁与复用:存在 pending 时 convert/runtime 拒绝；固化并重新 check 后放行。在两份相同基线 workbook 上复用同一个 `DataPreparePlan`,独立准备与 SaveAssets 组合写入完全相同的 row guid,且各自只做一次 workbook 原子替换。
10. retired 物理边界:当前 descriptor 的 retired 表无 sheet 时 check/generate 不报缺失且不创建；历史 sheet 存在时报告 `table.retired-present`,其 metadata、业务值、样式与非拥有内容经 generate/save/DataPrepare 后逐项不变,且不进入 live 导入/convert/runtime 数据集。
11. tombstone 身份:同一已发布 proto message 从 live 改为 `retired = true` 后保留原 table id；live+retired id 同域唯一。删除 message、另一 message 复用 id、或 retired 历史 sheet/同名 message 被当作 live 复活,均消费 M1/M8 blocker/兼容结论并断言 M5 不产生合法身份猜测或 workbook 写入。
12. 身份跨面一致:同一 `(table_id,row_guid)` 在 xlsx token 解析、authoring snapshot、converted bytes 与 runtime join 中一致；全局重复 row guid、错误长度/全零 RowRef、歧义 token 与把子表锚当资产均为 blocker。
