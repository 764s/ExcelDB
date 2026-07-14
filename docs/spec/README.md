# ExcelDB 权威规范总纲

状态：Active normative root

本文与其封闭列出的模块文档共同构成 ExcelDB 的唯一权威规范源。仓库内任何未被本文列入“规范模块”表的文档、代码、样例和测试，都不得反向定义产品契约。

## 1. 权威规则

1. 跨模块产品不变量、事实源、依赖方向和裁决顺序由本文拥有。
2. 领域内详细契约只由“规范模块”表指定的 owner 文档拥有；同一契约不得在两个模块重复定义。
3. 模块发现跨域缺口时，必须回到 owner 文档修订，不得在调用方文档生成第二种解释。
4. 两个规范模块若出现冲突，视为规范缺陷：在冲突修复前相关能力保持未定，不采用“后文优先”或按日期猜测。
5. 模块内“决策记录”只解释理由，不增加契约；与正文冲突时正文是唯一规范。
6. ADR、实施路线、教程、PoC 和归档文档均为非规范材料。它们可以验证或解释规范，不能覆盖规范。

## 2. 产品定位

ExcelDB 不是普通的 Excel 读取库，而是以 Excel 为一等 authoring 存盘对象、以游戏配置行为资产粒度的宿主无关配置系统：

- `.proto` 是有效表结构的唯一事实源；程序既可直接编辑，也可通过工具的交互向导事务性创建和维护，不要求手写。
- 表与字段以显式 export target 集声明各 runtime 投影；首版标准目标为 `client`/`server`，未来目标使用稳定 id 扩展，不改变历史集合含义。
- 策划可以直接在 Excel 中增删改数据；自定义编辑器是并列的结构化工作面，不是唯一入口。
- ASSET 表的一行是可加载、可引用、可编辑、可保存的资产。
- 运行时可消费 Excel 源与 converted bytes 源，并以事务方式打开、刷新和切换；失败保留旧数据。
- Unity 2022.3 是首要 adapter，但 Core、schema、workbook、convert 和 runtime 契约不得依赖 UnityEngine/UnityEditor。
- Unity-like 只约束 facade 的使用手感，不要求生成数据类继承 Unity 或 ExcelDB 基类。

## 3. 事实源与派生工件

| 工件 | 地位 | 所有权与生命周期 |
| --- | --- | --- |
| `*.proto` | 结构事实源 | 程序拥有；表/字段/枚举/variant 的数字身份与结构语义在此声明；交互向导最终必须提交本文件，不得把向导状态变成并列事实源 |
| `*.xlsx` | 数据事实源与 authoring 存盘对象 | 数据区由策划与编辑器共同编辑；结构区是 schema 投影；系统必须保留非拥有内容 |
| row metadata | 资产身份事实 | 与数据同行持久化；key、sheet、header、asset path 只是可变定位信息 |
| 生成代码(全量 authoring 类型 + 每 target runtime 类型/`RuntimeSchemaRegistry`) | schema 投影 | 可重新生成；每个 registry 固化代码期望的 `(SchemaHash,ExportTargetId)` 与该 target 的 live table/type/factory/accessor 绑定；不得手改为第二事实源 |
| 本地快照/cache | 非事实源 | 仅提供合并 base、索引与恢复辅助；不得成为跨机器身份的唯一载体 |
| 每 target converted bytes + manifest | 发布派生物 | 由 schema 与 xlsx 重建；每份工件只含一个目标投影，运行时打开前必须校验 `(SchemaHash,ExportTargetId)` |
| OperationReport/Diagnostic | 操作证据 | 记录结果与恢复信息，不成为数据或结构事实源 |
| `ExcelDb.Project.json` | 唯一项目配置源 | 版本化；只持久化 `schemaDir`、`generatedDir`、`workbooks`、`bytesOutput` 与 `cacheDir`；报告根由 cache 推导，Git/VCS 配置与运行时 source opt-in 不进入本文件 |

## 4. 全局不变量与仲裁序

发生目标冲突时，按以下顺序裁决：

1. 不丢数据。
2. 身份稳定。
3. 事务原子。
4. 事件与已发布状态一致。
5. 运行时热路径 no-GC。
6. 核心宿主无关。
7. Unity-like 使用手感。
8. 保留 workbook 辅助信息。

由此派生的强制规则：

- schema 生成、导入、保存、迁移、convert、切源和热载均采用“分析/计划 → 报告 → 提交或中止”；Blocker 必须零写入且不得替换旧运行时状态。
- 交互式结构创建与修改必须先形成候选 `.proto`，经同一 schema 编译与 lint 成功后才原子提交；失败保留原 `.proto` 与既有派生物，缓存或交互记录不得补充结构语义。
- `client`/`server` 等 export target membership 是 proto 中的显式结构事实。`ITableInitializer` 与 `IExportTargetStrategy` 只可在 create/edit 计划构造期补未指定的简单字段/目标，确认后结果必须进入候选 proto；apply、build、check、convert 与 runtime 均不得重跑代码策略或把其注册状态当成第二事实源。
- 纯 Excel 新增且 `__guid` 为空的行是 `pending-new`，只读扫描与 `check` 只能报告而不得补写；进入 `convert` 前必须由显式 `data prepare` 计划事务性固化 RowGuid。SaveAssets 可复用同一身份计划，任何 runtime source 都必须拒绝未固化身份。
- 已发布 ASSET 表的永久退役事实写在原 proto message 的 `TableOpts.retired = true`；message 与 table id 必须永久保留为 tombstone，live/retired id 均不得复用。retired 表不再生成或要求 active workbook 投影，也不进入生成代码的 live 类型/注册项或 runtime bytes；历史 sheet/数据仍按 M5/M8 默认保留，只能经显式 purge 删除。
- authoring 写回只能修改声明拥有区与写回计划覆盖的内容；提交前必须复读验证，失败保留原文件与 dirty 状态。
- canonical 值是导入、合并、diff 和写回之间的比较边界；物理 missing、schema default 物化、显式 null 与显式 value 是可区分状态。缺失值只有在读取 effective view 时才按 schema default 投影为 `Defaulted`，不得因此暗写 workbook。
- Runtime `Open` 必须显式取得生成代码提供的单 target `RuntimeSchemaRegistry`、source 与本次 options，并分别校验 source 声明、candidate 实读与 registry expected 的 SchemaHash 三者相等、ExportTargetId 三者相等；不得从 source、manifest、Project 或 cache 反推代码期望。v1 固定使用完整单一 SchemaHash，runtime projection 身份为 `(SchemaHash,ExportTargetId)`。
- 运行时发布采用单写者模型；对象图、索引、依赖图与 ChangeSet 在同一 publish point 原子可见。
- 性能、确定性、写回保真和兼容性必须有可自动验证的门禁；具体工具与阈值属于实现/验证配置，不进入永久架构契约。

## 5. 组件与依赖方向

```text
Schema Tooling ──产出 descriptor/codegen──> Core Runtime
                                             ↑
Authoring / Editor ──────────────────────────┘
        ↑
Host Adapters(Unity 等) + Integration Tools
```

- Schema Tooling 可使用 protoc/descriptor 能力，但正式发布必须把锁定版本的 parser/compiler、descriptor 定义、`exceldb/options.proto`、最小 proto 发射资源、内置默认表初始化器与标准 client/server 导出目标策略封装进 self-contained CLI；普通 C# 扩展只在定制发行的 composition root 注册，不建设可配置的第二表模板/目标声明语言。在 PATH 清空、网络不可用且机器未安装 protoc/.NET SDK 时仍须完成 schema 创建、维护、编译与 codegen。执行期不得从 PATH 发现工具或联网下载依赖；非系统 import 必须是项目本地输入。这些工具期依赖不得进入 Core Runtime。
- Core Runtime 拥有宿主无关的 descriptor 消费、运行时查询、source 抽象、identity、reference 与 ChangeSet 契约。
- Authoring / Editor 依赖 Core，拥有 xlsx 导入、快照、合并、dirty、写回与结构生成。
- Host Adapter 只能投影 Core/Authoring 能力；宿主类型不得反向进入二者。
- Integration Tools 只编排既有操作，不发明领域行为。
- ExcelDataSource 由独立可选 Xlsx source/authoring adapter 装配；Core/Runtime 只拥有 source contract，Release runtime 不引用或链接 xlsx backend，CLI 与 Editor/Development composition root 可显式组合它。

## 6. 规范模块

模块编号是稳定标识，不随新模块插入而重排；真实依赖以本文第 5 节和 owner 列为准。

| ID | Owner 文档 | 领域所有权 | 设计状态 | 实现状态 |
| --- | --- | --- | --- | --- |
| M1 | [`01-schema.md`](../modules/01-schema.md) | proto schema、descriptor、codegen 结构契约 | Dependency-Complete | Implementation-in-progress |
| M2 | [`02-assetdatabase.md`](../modules/02-assetdatabase.md) | authoring facade 使用契约 | Dependency-Complete | Implementation-in-progress |
| M3 | [`03-workflow.md`](../modules/03-workflow.md) | 角色、工件与端到端编排 | Dependency-Complete | Implementation-in-progress |
| M4 | [`04-integration-tools.md`](../modules/04-integration-tools.md) | CLI/Unity/CI/VCS 工具投影 | Dependency-Complete | Implementation-in-progress |
| M5 | [`05-workbook-identity.md`](../modules/05-workbook-identity.md) | workbook、metadata、行身份、路径/token | Dependency-Complete | Implementation-in-progress |
| M6 | [`06-import-edit.md`](../modules/06-import-edit.md) | 导入、快照、合并、dirty、写回、watcher | Dependency-Complete | Implementation-in-progress |
| M7 | [`07-runtime.md`](../modules/07-runtime.md) | source、resident、运行时查询、热载、切源、ChangeSet | Dependency-Complete | Implementation-in-progress |
| M8 | [`08-compatibility.md`](../modules/08-compatibility.md) | schema/workbook/bytes 兼容与迁移 | Dependency-Complete | Implementation-in-progress |

依赖闭包：M1 + M5 是数据模型基础；M6 消费 M1/M5；M7 消费 M1/M5 的运行时投影；M8 约束 M1/M5/M6/M7 的跨版本演进；M2 投影 M5/M6/M7；M3 编排 M1/M2/M5-M8；M4 投影 M2/M3/M5-M8。

## 7. 旧 `P§` 引用迁移表

M1-M4 中仍存在的 `P§x` 是旧精简计划的迁移别名，不再指向归档文件。读取时只按下表解析；待对应模块定稿后机械替换为 owner 引用。

| 旧引用 | 当前 owner |
| --- | --- |
| P§1-2 | 本文 §2/§4/§9 |
| P§3 | 本文 §3/§5；工具配置细节归 M4 |
| P§4 | M1 |
| P§5、P§8.1 | M5 |
| P§6.1-6.6、P§6.8 | M6 |
| P§6.7 | M2 |
| P§7、P§8.2-8.4 | M7 |
| P§9 | 本文 §4 与 M6/M7 的报告投影 |
| P§10-11 | M4 |
| P§12 | 各 owner 模块的验收节 |

归档文件中的旧段落不能补充、覆盖或细化上述 owner；owner 尚未定义的内容保持未定。

## 8. 规范生命周期

模块分别记录两个维度：

- 设计：Proposed → Accepted Design → Dependency-Complete。
- 交付：Not implemented → Implemented → Verified → Released。

“Accepted Design”只表示该模块自身方向被接受，不表示依赖已闭合或产品可用。只有 Dependency-Complete 且 Verified 的能力才能进入用户操作指南的“当前可用”路径。

## 9. 明确非目标

- 不建设通用 profile/policy/conformance 间接层、插件权限市场或签名信任链。
- 不把 xlsx 变成文本合并格式，也不建设自动版本控制三方合并器；协作粒度是 workbook。
- 不建设跨所有操作的通用事务框架；每个危险操作实现同构的 plan/report/commit 语义。
- Release 不提供 Excel authoring、热写回或编辑器 API。
- Core 不依赖 Unity，也不要求生成数据类继承库基类。
- 不以本地 cache、行号、key、sheet 名或路径作为最终资产身份。
- 不把 export target 编码为 RuntimeMode，也不建设 target profile、JSON/YAML 声明或 runtime 动态归类插件。

## 10. 已裁决架构决策

| ID | v1 裁决 | 影响模块 |
| --- | --- | --- |
| OD1 | canonical `AssetIdentity = (positive table_id, non-zero 128-bit RowGuid)`；RowRef 的 runtime/bytes 身份使用同一二元组，Excel 人读 token 只按当前 key 索引解析，key/path 不是 identity | M1/M2/M5/M6/M7 |
| OD4 | `Missing`、`Defaulted`、显式 `Null`、显式 `Value` 与 `Invalid(raw)` 可区分；default 只在 effective read materialize，除显式 materialize operation 外不写回 | M1/M5/M6/M8 |
| OD5 | ExcelDataSource 位于独立可选 Xlsx adapter；CLI/Editor/Development 可组合，Core/Runtime/Release 不依赖 xlsx | M5/M6/M7 |
| OD6 | v1 不拆 hash；完整单一 SchemaHash 与正交 ExportTargetId 组成 runtime projection identity；artifact 可有内容 hash但不得替代兼容身份 | M1/M5/M7/M8 |

这些裁决由 owner 模块继续展开物理格式、事务和验收；后续若改变必须走新的版本化架构决策与兼容迁移，不得由实现静默漂移。

## 11. 非规范材料

- 历史计划与旧裁剪记录：[`docs/archive/2026-07-authority-merge/`](../archive/2026-07-authority-merge/README.md)
- 既有 Schema PoC 已删除；当前纯 C# 正式实现与自动化验收覆盖 M1–M8，PoC 不参与产品解释。
- 实施与后续演进必须继续从本规范模块生成，不得由代码、样例或旧归档自行定义契约。
