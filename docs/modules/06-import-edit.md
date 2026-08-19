# 模块 6:导入与编辑事务

设计状态：**Dependency-Complete**；实现状态：**Verified**。本文是 M6 导入与编辑事务领域的唯一 owner；跨模块权威规则与架构决策见 [`docs/spec/README.md`](../spec/README.md)。本文固定状态变更串行发布、canonical 值作为跨阶段比较边界、写回只触碰计划内且由系统拥有的内容、提交前复读、失败保留旧事实与 dirty。本文不重复 M2 facade,不规定具体库、数据结构、算法、命令或 UI。若与其他规范模块冲突,视为规范缺陷并显式修订，不得由实现静默选边。后续引用记作 M6§x。

## 1. 范围、依赖与核心不变量

本文负责:

- workbook 内容进入 authoring resident view 的导入事务;
- 导入快照作为 merge base 的角色与失效退化;
- 外部内容、内存 dirty 与 base 之间的三方合并;
- `Clean / Dirty / Merging / Conflicted` 状态及其安全转移;
- 写回的 plan → report → commit 事务、保真与失败恢复;
- 纯 Excel `pending-new` 行的 `DataPreparePlan` 身份固化事务,以及 SaveAssets 对同一计划的复用;
- watcher 只调度同一导入/合并路径的约束。

本文消费而不重定义:

- M1 的 SchemaDescriptor、字段路径、canonical 解析/写出器、校验声明与生成类型;
- 模块 5 的 workbook 物理所有权、metadata、行身份及伴随信息;
- M2 的公开 authoring 门面、失败返回形态与报告入口;
- M3 的角色、工件、端到端工作流与发布门禁;
- M4 的 Unity/Project/流水线呈现面;
- 后续运行时模块的 resident 承载、publish point 与 ChangeSet 接线。

核心不变量:

1. **事实不倒置**:xlsx 是持久数据事实源;resident view 是已导入视图;快照是本地派生 base。快照和 resident 都不得反向成为未报告的数据事实源。
2. **失败保旧**:无法形成自洽新视图时保留上一个已发布 resident view;无法完成写回时保留原 workbook、snapshot 与 dirty/conflict 状态。
3. **计划外零改写**:commit 只能写 plan 明列且属于系统拥有区的变化;未知列、辅助内容、自由 sheet 及其他非拥有内容必须保留。
4. **提交后才翻转状态**:临时输出复读通过并完成 workbook 级替换后,才允许更新快照、清除相应 dirty、推进修订信息并发布成功。
5. **同路同义**:显式刷新、挂载导入、保存前 preflight 与 watcher 触发不得分叉出不同的解析、合并、校验或报告语义。
6. **串行发布**:后台工作可以读取或准备候选结果,但 resident、dirty、conflict、snapshot 与索引的状态变更必须在宿主规定的 authoring 发布点串行提交。
7. **身份先固化再转换**:M5 `pending-new` 的候选 row guid 只有经 `DataPreparePlan` 成功写入 xlsx 后才是持久事实；check/导入/watcher 只报告,convert/runtime 必须拒绝未固化身份。

## 2. 导入事务

### 2.1 阶段与提交点

一次导入按以下逻辑阶段推进;阶段是契约,不限定实现如何拆分任务:

```text
读取候选 workbook
→ 读取并核对 metadata / workbook 格式 / schema 身份
→ 按 SchemaDescriptor 建立拥有列绑定
→ 逐行解析 cell 与取得行身份决议
→ 执行字段、行、引用及项目级校验
→ 构造候选 resident view、key 索引与依赖关系
→ 形成 ImportReport
→ 原子发布候选 view 与对应 snapshot base
```

- 前一阶段产生的诊断必须随候选结果进入同一报告,不得被后续阶段吞掉。
- “视图可发布”与“数据可写回/可转换”是两个不同判断。authoring 可以保留带数据诊断的可检查视图,但 error 或 schema drift 所要求的写回/转换门禁仍然生效。
- 若读取、结构解释或身份绑定失败到无法形成自洽候选 view,导入不发布部分结果,保留旧 view 与旧 snapshot。
- schema 身份不一致时允许按当前 descriptor 尽力绑定并完整报告 drift;在结构重新对齐前不得写回。具体兼容迁移归模块 8。
- workbook 中未被当前 schema 拥有的列或 sheet 不参加 resident 物化,但必须作为持久内容保留;缺少 schema 所需列必须报告。缺失 cell、字段默认值与显式 null 按 §9.1 的 canonical 状态真值表处理。
- M5 身份扫描得到的 `pending-new` 行可以进入带诊断的 authoring view,但必须附 `identity.pending-new` error 并关闭 convert/runtime 门禁。check 投影同一诊断且保持只读；导入、watcher、check 与 convert 均不得顺手把候选 guid 写入 workbook。

### 2.2 导入发布的一致性

一次已发布导入必须对应一个确定的 workbook 内容版本。候选构造期间若文件内容变化,候选作废并重新进入读取阶段,不得把不同时刻读取的内容拼成一个 view。

发布点至少同时确定:

- 当前 workbook 内容指纹;
- 当前 SchemaDescriptor 身份与列绑定摘要;
- resident 行集合及可用索引;
- 该视图的诊断集合与写回门禁状态;
- 可作为后续合并 base 的 snapshot 内容。

对外事件的精确先后顺序归后续运行时/adapter 接线决定,但任何观察者都不得看到“新 view + 旧索引”或“新 snapshot + 旧 view”的混合状态。

## 3. 导入快照

### 3.1 角色与内容边界

快照是本地、可重建、非版本控制工件。它只承担:

- 三方合并中的 base;
- 行身份恢复所需的历史输入(具体规则归模块 5);
- 判断 resident mine 相对上次已发布 workbook 内容发生了什么变化;
- 保存前确认计划仍绑定同一个 base。

快照至少能表达其所对应的 workbook/schema 身份、行身份/key/修订摘要以及参与合并的字段 canonical 状态。具体文件格式、编码与索引方式不是本文契约。

快照不得:

- 覆盖 xlsx 中已经存在且合法的持久事实;
- 跨机器充当行身份的唯一载体;
- 把尚未写入 workbook 的候选 row guid 提升为已固化身份或据此放行 convert;
- 因为缓存命中而绕过 metadata、schema 或内容指纹核对;
- 在导入或写回尚未提交时提前推进。

### 3.2 更新与失效

- 导入发布成功后,快照与发布的 resident view 对齐;写回成功后,快照与复读通过的新 workbook 对齐。
- 导入/写回失败、发生未解冲突或文件替换失败时,旧快照保持不变。
- 快照与 workbook/schema 身份不匹配时视为不可用,不得尝试“猜中”旧 base。
- clean 状态下快照缺失或损坏:以当前 workbook 完整导入建立新 base,同时报告缓存退化。
- dirty 状态下快照缺失或损坏:不存在可证明的共同 base,不得自动选择 mine 或 theirs;进入需显式裁决的保守恢复路径并保留 dirty。

## 4. Dirty 与 Conflict 状态模型

### 4.1 状态定义

| 状态 | 含义 | 允许的安全推进 |
| --- | --- | --- |
| `Clean` | resident 与当前已发布 workbook/base 无待写回差异 | 导入外部新版本,或本地编辑进入 Dirty |
| `Dirty` | resident 含尚未提交到 workbook 的本地变化 | 继续编辑、生成写回计划、外改时进入 Merging |
| `Merging` | 正在以 base/theirs/mine 构造候选合并结果;瞬态 | 无冲突回到 Dirty/或 Clean,有冲突进入 Conflicted |
| `Conflicted` | 至少一个变化没有可自动证明的唯一结果 | 只能继续裁决、取消当前危险转移;未全解不得写回受影响事务 |

`Error`、`Missing` 等是行/字段或 resident 可用性诊断,不是对上述 authoring 事务状态的替代。一个 Dirty view 可以同时带数据 error;一个 Conflicted view 也可以包含未受冲突影响的可检查行。

### 4.2 转移不变量

```text
Clean --本地编辑--> Dirty
Clean --外部变化且导入发布成功--> Clean
Clean --DataPrepare commit 成功--> Clean(以已固化 workbook 推进 base)
Dirty --外部变化或保存前发现外改--> Merging
Merging --全部自动收敛--> Dirty(若无本地剩余变化则 Clean)
Merging --存在分歧--> Conflicted
Conflicted --全部显式裁决--> Dirty(若无待写回变化则 Clean)
Dirty --写回 commit 成功--> Clean
Dirty/Conflicted --任何写回失败--> 原状态保留
```

- dirty 必须由实际语义变化驱动;只读检查、打开窗口或重复导入同内容不得制造 dirty。
- `pending-new` 诊断和扫描候选本身不制造 dirty；身份准备是显式系统写入,必须出现在 `DataPreparePlan` 与报告中,不得静默落盘。
- 其他由系统自动产生且需要持久化的修复属于 dirty,必须出现在写回 plan 与报告中。
- Clean workbook 可以单独提交 `DataPreparePlan` 并在成功后保持 Clean；存在业务 Dirty 时不得先做一笔独立身份写回改变 base,必须由 SaveAssets 把同一 `DataPreparePlan` 与业务 WritePlan 合成一次 workbook commit。
- domain reload、进入 Play、退出宿主或卸载等会丢失内存态的转移,必须由宿主在状态消失前拦截;不得把状态清空当作成功保存。
- 显式 Discard 以最近一次成功导入/保存的独立深快照为基线，恢复值、路径、key、workbook 与影响闭包；未保存的新建行直接移除。跨进程草稿与崩溃恢复仍按 §9 的边界处理。

当前 facade 的首个安全落地对 dirty 期间的外部变化采用保守队列：`Refresh`/watcher 记录待导入并报告 `EXAD0004`，不立即覆盖 resident；成功 Save 先由 xlsx adapter 执行已有三方合并再排空队列，显式 Discard 则先恢复最近成功深快照再导入外部版本。立即在 Refresh 点形成字段级 `Merging` 仍需要 Import contract 暴露 canonical base/theirs/mine，本实现不通过伪调用 Save 或浅 POCO 合并冒充该能力。

## 5. 三方合并

### 5.1 三个输入

- `base`:与当前 resident 起点对应的有效 snapshot;
- `theirs`:重新读取并按当前导入规则解释的 workbook 候选;
- `mine`:当前 resident 中相对 base 的 dirty 语义值。

三方比较以字段 canonical 语义为边界;展示文本、存储形态或无语义格式差异本身不得制造冲突。对 missing/default/null 尚不能证明等价的值不得静默收敛,见 §9。

### 5.2 自动收敛矩阵

| base → theirs | base → mine | 结果 |
| --- | --- | --- |
| 无语义变化 | 无语义变化 | 保持原值 |
| 变化 | 无语义变化 | 采用 theirs,更新候选 resident |
| 无语义变化 | 变化 | 保留 mine,继续 Dirty |
| 变化 | 变化且 canonical 结果相同 | 收敛为该结果,不产生冲突 |
| 变化 | 变化且 canonical 结果不同 | 记录冲突,进入 Conflicted |

无冲突合并发布后,当前 theirs 成为新的 merge base;仍需保留的 mine 变化相对该新 base 重新标记为 dirty。不得继续以旧 snapshot 为下一轮合并 base,也不得因为推进 base 而把尚未写回的 mine 误判为 clean。

行存在性按同一三方原则处理。明确成立的底线是:theirs 删除一行而 mine 修改同一行时,该行整体冲突;不得把删除或修改静默丢弃。其他涉及身份恢复、复制行或跨 workbook 移动的判定消费模块 5 的身份契约。

### 5.3 冲突与裁决

- 每个冲突必须稳定定位到资产身份与字段路径;行存在性冲突定位到整行。
- 冲突视图必须保留足够的 base/mine/theirs 信息供操作者判断,但具体 UI 与公开枚举面归 M2/M4。
- 显式选择 theirs 表示放弃该处 mine;显式选择 mine 表示以当前 theirs 为新 base 并保留 mine 为待写回值。
- 裁决只更新候选 resident、dirty 与 conflict 集;它本身不写 workbook。
- 未解冲突期间必须保留该轮 base/theirs 与裁决进度;全部裁决后才把 theirs 推进为新 snapshot base,剩余 mine 继续作为相对新 base 的 dirty。若没有剩余 mine,状态可直接回到 Clean。
- 未解冲突必须使受影响写回事务零写入。冲突之外是否允许形成独立事务取决于 §9 的影响闭包决策,实现不得自行部分保存。

## 6. 写回事务:plan → report → commit

### 6.1 Preflight 与 Plan

写回首先执行 preflight:

1. 读取目标 workbook 当前身份与内容指纹;
2. 若相对当前 base 已有外部变化,先走 §5 合并;
3. 检查 schema drift、数据 error、未解冲突、只读格式及所有权边界等写回门禁;
4. 在门禁允许时构造完整、不可隐式扩张的 WritePlan。

WritePlan 必须:

- 绑定明确的 workbook 内容版本与 schema 身份;
- 列出所有预期写入的 cell、行存在性变化及系统 metadata/伴随信息变化;
- 标明每项变化的原因与拥有方;
- 声明事务影响范围;单行请求是否扩张到引用、identity、metadata 或其他 workbook 由 §9 决定;
- 能在零写入条件下形成并审计。

纯 Excel 新行的 row guid 固化不使用上述开放式影响闭包,而使用 §6.4 封闭的 `DataPreparePlan`。SaveAssets 发现本次 workbook 范围含 `pending-new` 时,只能组合该计划,不得在 WritePlan 内另行分配身份。

`identity.pending-new` error 是 §6.4 的触发条件,不是阻止该计划修复自身的门禁；它仍阻止不含 `DataPreparePlan` 的普通写回和所有 convert。DataPrepare/SaveAssets 组合仍须通过其他 schema drift、业务数据 error、冲突、格式与所有权门禁。

如果 commit 前指纹或门禁状态变化,旧 plan 失效;事务必须重新 preflight/merge/plan,不得把旧 plan 套到新文件。

### 6.2 Report

提交前报告是 plan 的结构化投影,至少包含:

- 基线身份与影响范围;
- 计划新增、修改、删除及 metadata 变化摘要;
- 被保留的非拥有内容类别;
- 全部 diagnostics、未解冲突与写回门禁;
- 当前是否具备 commit 条件。

报告不是某个 UI 或命令的专属能力。M2/M4 的入口只投影同一事务报告,不得删减会改变操作者判断的影响范围或失败信息。

### 6.3 Patch、验证与 Commit

满足门禁后,commit 只能执行 report 所对应的原 plan:

1. 从当前目标 workbook 产生临时候选文件;
2. 仅修改 plan 明列且由系统拥有的内容;
3. 保留样式、公式、列宽、批注、未知列、辅助列、自由 sheet 及所有其他非计划内容;
4. 对候选文件重跑必要读取,确认计划内值的 canonical 结果、行存在性和 metadata 与 plan 一致;
5. 无法证明计划内正确或计划外保真时中止,原 workbook 不变;
6. 以 workbook 级原子替换提交候选;
7. 替换成功后才更新 snapshot、清理本事务覆盖的 dirty/conflict、推进修订并发布成功结果。

具体文档库、压缩/XML 策略、临时文件命名、重试次数与比较算法均为非规范实现细节。

### 6.4 `DataPreparePlan`:pending-new 身份固化

`DataPreparePlan` 是专用 WritePlan,唯一目的 = 在 convert 前把 M5 只读扫描产生的候选 row guid 写入对应 xlsx `__guid` cell。它不是通用数据修复或 generate:

1. **形成计划**:消费最近一次完整身份扫描,绑定 workbook 内容指纹与 schema 身份；逐项记录受管表、扫描时物理定位、观察行内容指纹、`__guid` cell 观察状态和候选 row guid（纯 Excel 新行的观察状态为空）。候选在本计划与 workbook 扫描域内必须唯一,且不得从 key/位置/业务值派生。
2. **报告**:列出每个待固化行的稳定诊断定位与候选 guid,明确声明业务值、行结构、`__rev`、metadata 和非拥有内容均不在影响范围。未确认时零写入。
3. **commit 前复读**:重新读取完整 workbook 指纹、schema 身份、每个观察行指纹、物理定位与 guid cell。任一变化均使计划 stale；返回 blocker,原 workbook/snapshot/resident/dirty 不变,必须重新扫描和计划,不得尝试按 key 或相似内容搬用旧候选。
4. **patch 与验证**:在临时候选 workbook 中只把计划列出的、仍匹配观察状态的 `__guid` 写为对应候选；复读确认 guid 唯一且相等、所有业务 canonical 值和 `__rev` 未变,并执行 §6.3 的计划外保真验证。
5. **提交与发布**:验证通过后 workbook 级原子替换；替换成功后才以新 workbook 重建/更新 snapshot、resident 身份绑定与索引,清除已固化行的 `identity.pending-new` 诊断并发布成功报告。

独立数据准备只允许用于没有未提交业务变化的 Clean workbook。SaveAssets 在 Dirty workbook 上复用同一计划时,将其作为普通 WritePlan 的封闭子计划,共用同一个基线指纹、临时候选、复读和 workbook 替换；候选 guid 不得重新分配,最终只发生一次文件 commit。身份 cell 写入本身不递增 `__rev`;若同一 SaveAssets 事务还提交业务变化,仅那些业务变化按 M5§5 推进对应 rev。

check 必须报告 `identity.pending-new` error 但不形成隐式 commit；convert 发现任一 pending 行直接 blocker 且不产新 bytes/manifest。二者都不得自动调用 `DataPreparePlan`。具体 CLI/UI/API 名称归 M2/M4,但所有入口必须投影本节同一计划、报告与提交语义。

### 6.5 失败恢复

- 文件锁、权限、空间不足、临时文件写入失败、复读失败或替换失败:原 workbook 保持不变,dirty 保留,snapshot 不推进。
- 写回期间发现新的外部版本:候选作废,回到 preflight 与 merge,不覆盖外部内容。
- 门禁或冲突失败:零写入,报告必须给出可恢复位置与原因。
- workbook 替换成功但后续本地派生状态更新异常时,下次打开必须以已提交 workbook 为事实重建;不得用旧 snapshot 回滚已成功替换的文件。是否需要本地 commit journal 见 §9。

## 7. Watcher 与刷新调度

watcher 是变化提示器,不是第二条导入实现:

- 后台只观察并投递“可能变化”的信号;信号可以合并、延迟或重复,其本身不作为内容事实。
- 状态读取、候选构造与发布按 §2 在 authoring 发布点串行执行。
- 当前为 Clean 时,调度普通导入;当前有 Dirty 时,调度 §5 三方合并。
- watcher 不直接写 workbook、不直接更新 snapshot、不提交 `DataPreparePlan`、不分配持久身份、不绕过诊断或门禁。
- 显式刷新、挂载后的首次导入、保存前 preflight 与 watcher 最终都复用同一读取、绑定、解析、校验、发布/合并路径。
- 文件暂时不可读只表示候选尚不可形成;旧 view 与 dirty 保留。重试、防抖和去重策略属于实现细节,不得改变最终语义。

## 8. 失败与恢复语义总表

| 失败点 | 已发布状态 | 恢复原则 |
| --- | --- | --- |
| 候选 workbook 无法读取或结构无法解释 | 旧 resident/snapshot 不变 | 修复或等待文件可读后重新导入 |
| 数据可检查但存在 error/schema drift | 发布带诊断的 authoring view;关闭相应写回/转换门禁 | 修数据或先完成结构对齐 |
| 存在 `pending-new` | 发布带 `identity.pending-new` error 的可检查 view;check 只报告,convert/runtime 拒绝 | 显式审阅并提交 `DataPreparePlan` 或复用它的 SaveAssets,再重新 check |
| dirty 下外改且可自动收敛 | 发布合并后 view,保留剩余 dirty | 继续编辑或重新计划写回 |
| dirty 下外改产生冲突 | 进入 Conflicted,workbook 不写 | 显式裁决全部受影响冲突 |
| snapshot 缺失且无 dirty | 从当前 workbook 重建 view/base | 报告缓存退化即可 |
| snapshot 缺失且有 dirty | resident 与 workbook 均保留,不自动选边 | 进入显式恢复流程 |
| 写回计划过期 | workbook/dirty/snapshot 不变 | 重新 preflight、合并与计划 |
| `DataPreparePlan` 指纹/行观察 stale | workbook/resident/dirty/snapshot 不变,候选不固化 | 丢弃旧计划,完整重扫后生成新计划 |
| patch、复读或替换失败 | 原 workbook、dirty、snapshot 不变 | 消除失败原因后重试完整事务 |

## 9. 事务与值语义决策

1. **canonical missing/default/null 真值表**：物理 cell 不存在或空且无显式 token = `Missing`；schema 有 default 时 effective read 产生 `Defaulted(value)`，但 raw snapshot 仍记录 Missing；文本 `~` = 显式 `Null`，字符串字面量 `~` 必须写为 percent-escaped `%7E`；其他成功解析内容 = `Value`；解析失败 = `Invalid(raw)`。五态不相等。required 对 Missing/Null/Invalid 报错；没有显式 materialize operation 时 Defaulted 不写回。merge/dirty 先比较 raw canonical 状态，再由 validation 使用 effective view。
2. **单行保存影响闭包**：`SaveAssetIfDirty` 的请求根是单行，但 planner 必须展开 key rename、RowRef token 修复、delete policy、metadata 与同事务 pending identity 的完整确定性影响闭包；报告逐项列出。闭包超出该 workbook 时进入 §9.3 多书事务，不能为保持“单行”而留下悬空引用。
3. **多 workbook commit**：先对全部 workbook 复读/校验并在同卷 staging 产生候选与 backup，再写 versioned journal；逐文件替换后写 commit marker，最后复读全闭包并清理。中途失败按 journal 恢复全部旧文件；进程崩溃后启动时必须在任何导入前完成 rollback 或已提交候选验证。它是可恢复的全闭包事务，不宣称文件系统提供不可观察的多文件原子指令。
4. **导入 error resident**：无法解析的 cell 以 `Invalid(raw)` 与定位诊断保留在 authoring view，整行仍可检查；该字段及依赖它的 key/ref/dependency binding 不进入有效索引，写回/convert 被门禁。此前已发布 runtime view 保持最后成功版本，不能把 raw 错值注入 runtime。
5. **直接改 key**：与结构化 rename 同义；保留 AssetIdentity，检查项目域唯一性，展开所有人读 RowRef token 与 asset path 投影的影响闭包。无法完整修复时进入 conflict/blocker，不要求用户改用另一入口。
6. **放弃、回滚与草稿**：显式 Discard 把 mine 恢复到最近成功 snapshot/theirs，并清对应 dirty/conflict；结构操作在 commit 前由同一内存 mutation 可逆。普通草稿不跨进程/domain reload 持久化，宿主必须先提示保存/放弃/取消；持久化工件只有 §9.3 commit journal，不能恢复未提交业务草稿。
7. **发布顺序与重入**：owner publish point 串行执行 `resident values → indexes/dependencies → snapshot/base → last report → workbookImported → optional Runtime refresh/ChangeSet`。每一步看到同一 revision；事件回调中的写事务重入抛 `InvalidOperationException`，外部 watcher 信号只能排队到下一 publish point。
8. **commit journal 生命周期**：journal 记录 format/version、transaction id、全部 old/new fingerprints、staging/backup 路径和阶段。创建后写穿；全部文件替换并复读成功前不推进 snapshot。commit marker 后派生状态失败时，下次以 workbook 新事实重建；无 marker 则回滚。恢复完成后才删除 staging/backup/journal，旧计划因 fingerprint stale 永久不可重放。

## 10. 模块验收基线

1. 导入阶段故障注入:每个提交前阶段失败均保留旧 view/snapshot,不发布半成品。
2. 内容版本一致性:候选构造期间外部变化使候选失效,重新读取后发布的 view 来自单一文件版本。
3. 快照退化:clean/dirty 两种状态分别覆盖 snapshot 缺失、损坏、schema/workbook 身份不符。
4. 合并矩阵:§5.2 五行逐一覆盖;同值收敛不生冲突,delete-vs-modify 形成整行冲突。
5. 状态机:§4.2 所有合法转移与非法越过均覆盖;任意写回失败不清 dirty/conflict。
6. 计划约束:plan 绑定的指纹变化后不可 commit;报告完整投影计划、门禁与影响范围。
7. 写回保真:计划内 canonical/行存在性/metadata 复读一致;计划外拥有内容不变,非拥有内容保真;验证失败原文件不变。
8. 提交次序:只有 workbook 替换成功后才更新 snapshot、清 dirty 与发布成功。
9. 调度等价:显式刷新与 watcher 对同一初态/输入得到等价 view、diagnostics、dirty/conflict 结果。
10. pending 只读门禁:纯 Excel 空 guid 行经导入/check/watcher 后 workbook 字节不变、状态不因候选分配变 Dirty,报告含 `identity.pending-new` error；convert 不产新 bytes/manifest。
11. DataPrepare 保真与发布:成功计划只改变目标 `__guid`,业务 canonical 值、行结构、公式、样式、metadata 与 `__rev` 不变；替换成功后才更新 snapshot/resident/index 并清 pending 诊断。
12. DataPrepare stale/故障注入:形成计划后改变完整指纹、schema、行内容、位置或 guid cell,以及 patch/复读/替换各失败点,均断言原 workbook 与已发布状态不变、旧候选未固化。
13. SaveAssets 复用:在两份相同基线 workbook 上复用同一个 `DataPreparePlan`,分别走独立 Clean 准备与 Dirty SaveAssets 组合,写入的候选 guid 完全相同；组合路径只有一个临时候选和一次 workbook 替换,业务 rev 只由业务变化推进。
14. §9 决策验收:五态 canonical 真值表、影响闭包、多书故障注入与 crash recovery、Invalid(raw) 索引隔离、直接 key rename、discard/domain reload、发布事件顺序/重入及 journal 生命周期均有确定性测试。

## 11. 与既有文档的衔接

- 本文已经接管导入、快照、合并、dirty、写回和 watcher 调度契约；归档计划中的同类段落不再具有规范效力。
- 具体工程、库、算法、数据结构、重试参数与示例属于非规范实现细节；当前实现及后续演进均不得反向改变本文不变量。
- M3 的 WF3/WF4/WF5 消费本文事务;M4 的窗口、对话框与报告只是本文状态和报告的投影。
- M5§9.2 已由 §6.4 落为 `DataPreparePlan`;M3/M4 后续只能增加该事务的入口投影,不得让 check/convert/watcher 形成隐式身份写入。
- 本文不改变 M2 已公布的 facade 形状;§9 中影响到既有入口语义的决策完成后,应回写唯一权威文档,不得长期并存两说。
