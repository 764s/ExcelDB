# 模块 3:工作流(端到端编排)

状态:本文是逐模块重推演的第 3 篇,把 M1(schema)、M2(AssetDatabase 门面)与精简计划的机制串成端到端团队工作流。本文不重定义机制——机制文档回答"是什么",本文回答"谁、何时、经哪个入口、失败后去哪";与机制文档冲突时修一侧,不允许并存两说。对既有契约的修订集中登记于 §12(P§10 命令族新增 `diff`、P§6.6 计划分级追加两行、实施文档 §6 CI 序列追加两道门禁)。模块编号再顺延:M2 所称"模块 3/4/5/6(workbook 与身份/导入与编辑/运行时/兼容)"改读"模块 4/5/6/7"。后续模块引用本文记作 M3§x;精简计划记作 P§x,模块 1/2 记作 M1§x/M2§x。

## 1. 范围、角色与工件

- 范围:七条工作流(WF1-WF7)+ 失败恢复总表。每条工作流 = 步骤序列 + 失败分支(就近陈述或收敛于 §10 总表);步骤只做编排——"操作 + 入口 + 报告去向",机制细节一律引用机制章节。
- 角色 × 工作面(一人可兼多角色;工作面决定能力边界,不是权限系统):

| 角色 | 工作面 | 拥有的工件 |
| --- | --- | --- |
| 程序 | proto、C#、Unity 编辑器、CLI | schema(proto)、生成代码、validator/codec/provider 注册、`ExcelDb.Project.json` |
| 策划 | Excel、Unity 编辑器(Browser/inspector) | 数据区行、辅助行列、自由 sheet(P§5.1) |
| CI | CLI | 门禁与报告工件(实施文档 §6) |
| 游戏 | RuntimeDatabase | 派生缓存(订阅 ChangeSet 自建,P§7.3) |

- 工件 × 版本控制(纪律;违背由 WF7 门禁抓,不靠记忆):

| 工件 | 进版本控制 | 说明 |
| --- | --- | --- |
| `*.proto`、`ExcelDb.Project.json` | 是 | 程序拥有;结构事实源(M1§1) |
| 生成代码(codegen 产物 .cs) | 是 | Unity/宿主直接消费源码;新鲜度由 CI 门禁保证(WF7);手改无效(M1§1) |
| `*.xlsx` | 是 | 数据事实源;二进制,协作规则见 WF6 |
| 快照/缓存(cacheDir,P§6.2) | 否 | 本地缓存,不跨机;身份的跨机载体是文件内 `__guid` 伴随列,快照只提供合并 base 与身份修复(P§5.3 规则 2/5),fresh clone 首次导入重建,缺失退化见 P§6.2 |
| bytes + manifest(P§7.4) | 否 | 可重建工件;CI/构建重新 convert,本地按需 |
| 报告(`--json` 产物) | 否 | 流水线归档的构建工件 |

## 2. 全景与入口映射

```text
proto ──构建(lint+codegen)──> descriptor+生成代码 ──generate──> xlsx 结构(行1-3、下拉、metadata)
xlsx 数据 <──策划 Excel 直编              <──SaveAssets 补丁写回── 编辑器编辑(dirty,内存态)
   │   ↑watcher(P§6.8)                              ↑三方合并/冲突(P§6.3)
   └──导入/校验(P§6.1)──> resident 对象 ──convert──> bytes ──Open──> 运行时
                             ↑ hot reload 环(P§7.5)            ⇄ 切源(P§7.6)
```

- 入口等价:同一操作的全部入口走同一管线、产同一 OperationReport(P§9);菜单是 CLI/API 的 UI 投影,禁止菜单专属行为。

| 操作 | API | Unity 菜单(P§11) | CLI(P§10) | 报告面 |
| --- | --- | --- | --- | --- |
| 挂载/卸载 | Mount/UnmountWorkbook(M2§3) | Mount | —(按项目 json 自动挂载) | workbookImported |
| 刷新/导入 | Refresh/ImportAsset(M2§3) | Refresh;watcher 自动 | check(只读) | workbookImported + 报告窗口 |
| 结构生成 | — | Generate | generate | 报告窗口 / `--json` |
| convert | — | Convert | convert | 同上 |
| schema 校验 | —(构建期) | —(编译即跑) | lint | 编译输出 / `--json` |
| 差异对账 | — | — | diff(§8) | stdout / `--json` |
| 保存 | SaveAssets 族(M2§6) | 保存动作 + 编译/进 Play 拦截(§6) | — | OperationReport |
| 冲突解决 | GetConflicts/ResolveConflict(M2§7) | 冲突对话框 | — | 同上 |
| 切源/热载 | RuntimeDatabase(P§7.2) | 项目设置 + Play 接线(模块 6) | — | Diagnostic + changed 事件 |

## 3. WF1 项目接入(一次性,程序)

1. 建 schema 工程(实施文档 §1 samples 形态):引入 `exceldb/options.proto`,写首个业务 proto;表 id 单调分配、删除走 `reserved` 永不复用(M1§1),lint XDB001 兜底,无外部台账。
2. 写 `ExcelDb.Project.json`(P§3,键集不变):生成代码以源码进宿主工程(Unity asmdef / csproj),CLI 经 `--schema` 消费 schema 程序集(内嵌 descriptor 与 SchemaHash,M1§6)。
3. 首次 `generate --workbooks <具体路径>`:路径不存在 → 计划项"新建 workbook"(safe,§4 追加行),创建含 metadata 的空书并按 descriptor 建 sheet(行 1-3、下拉、`__exceldb_keys`、伴随列,P§5.1/5.2)。
4. Unity 侧 Mount(或启动按 json 自动挂载)→ 策划进入 WF3。
5. 首次 convert + 运行时冒烟(WF7 步骤 2-3 的本地版)→ 全部入库工件提交,形成基线。

## 4. WF2 结构演进(程序主导)

1. 编辑 proto:新增字段用新 number;改名保 number(身份不变即兼容,M1§1);字段/表删除与语义变更走三阶段(模块 7),格式变更走 M1§2 三阶段——本文只锚节律:每阶段一次 WF2 迭代,进入下一阶段的证据 = 最近 check/normalize 报告命中归零。
2. 构建 schema 工程:编译管线内 lint(XDB blocker 即构建失败,M1§5)+ codegen 更新生成代码。
3. `generate --dry-run` 审计划 → apply(分级动作与门禁见 P§6.6)。计划分级表追加两行(P§6.6 修订):

| 变化 | 级别 | 动作 |
| --- | --- | --- |
| workbook 文件不存在(具体路径) | safe | 新建含 metadata 的空书再建表;glob 不产生此项,首建必须给具体路径 |
| 新表落点歧义(多书目标且未指定 `--workbook <path>`) | blocker | 拒绝;已生成表跟随其所在书的 metadata,新表落点是操作者决策;跨书移动表 = 人工剪切 sheet 后两侧 generate 修复 metadata |

4. 本地 check 确认无 drift → 原子交付:proto + 生成代码 + 受影响 xlsx(+ normalize 重写)同一变更集提交。
5. 他人拉取:重编译(新生成代码)→ Refresh 重导入,快照 descriptor 摘要随之更新(P§6.6)。

失败分支:generate error(不可 reparse 坏 cell 清单)→ 修数据或改 schema 后重跑;blocker(key 结构/区域重叠)→ `--rekey`/挪列(P§6.6);漂移漏网 → WF7 门禁红,schema.drift 在 CI check 判 error。

## 5. WF3 Excel 填表与调数(策划主导)

1. 开 Excel 直编:新行 `__guid` 留空(导入分配,P§5.3-1);枚举/引用列用下拉(P§5.1);辅助行列与自由 sheet 随意,系统永不触碰;手工列想转正式字段 → 提给程序走 WF2(手工列在导入侧只是 warning 保留,P§6.1)。
2. 保存 xlsx:
   - Unity 开着:watcher 防抖导入(P§6.8)→ Browser/inspector 即时更新;error 单元格级定位展示,不阻塞继续编辑,只门禁 save/convert(P§9)。
   - Unity 没开:改动静置;开 Unity/Refresh 或 CI check 兜底。纯 Excel 工作面推荐 pre-commit 挂 `exceldb check`(建议,不是机制)。
3. 高危编辑的既定语义(引用即可,不复述):整行复制 → 首行保身份、余为新行(P§5.3-2);改 key → 改名 + 引用 cell 自动改写(P§5.3-4、P§8.1);删被引用行 → 持有方 delete_policy 门禁,BLOCK 表现为 ref.dangling error(M1§2)。
4. 提交:check 绿 → xlsx 入库;PR 评审工件见 §8。

## 6. WF4 编辑器结构化编辑

1. Browser/inspector 或工具代码:SO/SP 编辑、直改字段 + SetDirty(门面细节归模块 5);结构操作(Create/Delete/Rename/Move/Copy)即时生效于内存与索引(M2§5)。
2. 一切落盘收敛到 SaveAssets(M2 Δ3):preflight 指纹比对 → 外部有改先合并(P§6.3)→ 冲突全解才写(M2§7)→ 补丁写回 + 复读 + 原子替换(P§6.5)。
3. 与 Excel 双开同书合法(核 6 的设计场景):外改由 watcher 合并;大批量操作用 Start/StopAssetEditing 聚合(M2§5)。
4. dirty 与 Undo 是内存态:domain reload(脚本编译、进 Play)会清空。adapter 必须在编译与进 Play 前拦截未保存 dirty——默认弹窗(保存/放弃/取消),存在未解冲突时仅可解决或取消;dirty 静默丢失违反仲裁序第一条(P§1)。接线归模块 6。

## 7. WF5 Play Mode 现场调参

1. 进 Play 选源(`developmentExcelSource`,P§3):bytes 模式先做陈旧检测——manifest 内容 hash 对当前 xlsx(P§7.4)不符 → 提示 convert 后进 / 带旧数据进 / 取消;接线归模块 6。
2. Excel 源 + `developmentHotReload`:改 Excel 保存 → 运行时热载(P§7.5),Game 视图即时生效;Play 中 inspector 改数走 editor API 写回(核 10)→ SaveAssets 落盘 → 同一热载环。两条改数路径的汇合点 = xlsx 文件;编辑器与运行时不共享草稿。
3. 调好 → Convert → SwitchDataSource(bytes) 复验出包数据(P§7.6,实施文档 §8.4)。
4. 停 Play → 按 WF3/WF4 收尾提交。

失败分支:热载解析失败 → 保留旧数据 + 报告,修表再存即自动重试(P§7.5);切源 schema_hash 不符 → 拒绝并保旧源,重新 convert(P§7.6)。

## 8. WF6 协作与版本控制

- 协作粒度 = workbook:按域/负责人拆书(挂载集即 json glob;跨书引用自由,key 唯一域跨书,P§8.1);同书并行写高危 → 团队认领约定,不做工具锁(ADR-12)。
- xlsx 在版本控制里不可文本合并。冲突剧本:
  1. 冲突发生 → 整文件二选一(通常取先合入方)。
  2. `exceldb diff <merge-base 版> <被弃版>` 列出被弃改动(版本文件经 `git show` 提取)。
  3. 在存活版上重做被弃改动(Excel 或编辑器均可)→ check → 提交;重做后再 diff 一次收敛自查(可选)。
- `exceldb diff`(P§10 命令族新增;差异是事实不是失败,退出码仍按公共约定只反映操作成败):

```text
exceldb diff --schema <asm> <base.xlsx> <target.xlsx> [--json <path>]
双方各走只读导入(P§6.1 宽松列绑定;不写快照、不落身份分配);结构漂移以 schema.drift 随报告
行对齐:__guid 优先;无 guid 行按 (key, 行序) 尽力配对
分类:added / removed / renamed(同 guid 异 key)/ modified(cell 级 canonical 值对,字段路径定位)
```

- 评审工件:PR 流水线对每本变更 workbook 产出 check 报告 + 对 merge-base 的 diff 报告并附评审(二进制不可读的补偿);快照入库、签名工件维持裁剪(ADR-19)。

## 9. WF7 出包与发布

1. CI 门禁序 = 实施文档 §6 基线 + 两道追加(实施文档 §6 修订):生成代码新鲜度(重跑编译管线后对生成目录 `git diff --exit-code`)、评审工件(§8);schema.drift 在 check 判 error。
2. 构建:`IPreprocessBuildWithReport` 钩子 convert → `StreamingAssets/ExcelDb/`(P§11);manifest 随 bytes 同行。
3. Release 运行:`Open(ConvertedBytesDataSource)`;无热载与写回(P§7.8)。
4. 线上数据补丁 = 重新 convert + 宿主资源系统分发新 bytes + 显式 Open(P§7.8);bytes 的 schema_hash 必须等于包内代码的 SchemaHash(P§7.4 校验拒绝)——补丁窗口 = 纯调数,结构变更必须随包发布。

## 10. 失败与恢复总表

| # | 失败点(所在 WF) | 表现 | 恢复动作 |
| --- | --- | --- | --- |
| 1 | lint blocker(WF2) | schema 工程构建失败(M1§5) | 修 proto;不产出 descriptor 即无下游污染 |
| 2 | generate error/blocker(WF2) | 坏 cell 清单 / 零写入(P§6.6) | 修数据或 schema;`--rekey`/挪列后重跑 |
| 3 | 导入 error(WF3) | 行可加载,save/convert 门禁(P§9) | 按 cell 定位修 Excel |
| 4 | schema.drift(WF3) | 写回门禁(P§6.1);CI check 红 | 拉最新代码,或找程序走 WF2 补 generate |
| 5 | 保存遇外改(WF4) | 自动合并;冲突 → Conflicted(P§6.3) | ResolveConflict 全解 → SaveAssets(M2§7) |
| 6 | 保存遇文件锁(WF4) | 重试 3 次失败,dirty 保留(P§6.5-5) | 关 Excel/解锁 → 再 SaveAssets |
| 7 | 热载失败(WF5) | 旧数据保留 + 报告(P§7.5) | 修表再存,下一次变更自动重试 |
| 8 | 切源被拒(WF5) | 保留旧源(P§7.6) | 重新 convert 后再切 |
| 9 | workbook 格式过新(任意) | 只读打开(P§5.2) | 升级工具版本 |
| 10 | xlsx 版本控制冲突(WF6) | 文本合并不可用 | §8 冲突剧本(二选一 + diff 对账重做) |
| 11 | convert error(WF7) | 构建失败,退出码门禁(P§10) | 按报告修复后重跑 |
| 12 | 坏数据已入库(任意) | 游戏/校验暴露 | 版本控制回退 xlsx;本地无 dirty → Refresh 直接导入,有 dirty → 走合并/冲突(P§6.3) |

## 11. 模块验收测试

带"复用"标记的断言主体在既有测试面,本模块只加编排层剧本:

1. 接入剧本:空目录 → WF1 全序 → 运行时读到首行(P§12-9 前半复用;新增断言:generate 对不存在路径建书,metadata/伴随列/下拉齐备)。
2. 演进剧本:加字段 / rename(number 不变)/ 删字段三例 → generate 分级动作与数据保留(P§12-6 复用);原子交付门禁:构造"proto 新、xlsx 旧"仓库态 → CI check 以 schema.drift error 红。
3. 新鲜度剧本:改 proto 不重生成 → CI 新鲜度门禁红;重生成 → 绿。
4. 调数剧本:Excel 改 cell/加行/复制行/改 key/删被引用行 → 身份与引用语义(P§12-3 复用;delete_policy 语义 M1§2)→ check 绿后入库。
5. 双面剧本:编辑器 dirty + 外部改同 cell → 冲突两种 Resolve 各一 → SaveAssets 落盘(P§12-4 复用)。
6. 拦截剧本:dirty 未保存触发脚本编译/进 Play → 拦截提示;未解冲突时无"保存并继续"路径(§6;模块 6 落地后补接线测试)。
7. Play Mode 剧本:bytes 进 Play(陈旧检测提示一例)→ 切 Excel 源 → 热载 5 事件(P§12-7 复用)→ convert → 切回 bytes 值等价。
8. 协作剧本:两副本并行改同书 → diff 四分类各至少一例 → 对账重做后 diff 归零;schema.drift 下的宽松 diff 一例。
9. 新表落点:多书 glob 含新表且缺 `--workbook` → blocker;指定后落点正确;新建 workbook 的 safe 路径。
10. 失败恢复:总表 12 行各至少一测(复用处标注既有测试项)。

## 12. 与仓库现状衔接

- 本模块仅文档,无代码交付(同 M2 惯例)。
- 修订登记:P§10 命令族新增 `diff`(§8);P§6.6 计划分级追加两行(§4);实施文档 §6 CI 序列追加新鲜度与评审工件门禁(§9);实施文档 §8.5 操作序列以本文 WF3-WF5 为准。上述文件头部注记已同步。
- `exceldb-cuts-adr.md` 追加第 19 条:评审/对账工件恢复为单一 `diff` 命令,快照入库与签名工件维持裁剪。
- poc 无工作流对应物;WF 剧本的可执行化随模块 5(导入与编辑)与模块 6(运行时/adapter)的实现推进。

## 13. 待后续模块决定的边界

- workbook 物理契约(三行表头、下拉、metadata sheet、RowRef cell token、`RowRef.id` 与行身份关系)→ 模块 4(M2§11 预派顺延)。
- 导入管线细节、诊断码全集、ImportReport 字段、SO/Undo/EditorUtility 门面、diff 的报告 schema → 模块 5。
- resident 承载、Play Mode 接线(陈旧检测对话框、domain reload 拦截、发布点与 ChangeSet/workbookImported 次序)→ 模块 6 / Unity adapter。
- 字段级三阶段兼容矩阵与 normalize 细则(本文只锚节律,WF2)→ 模块 7。

## 14. 决策记录

约定沿用 M1§10:引用块 = 评审原话;一句加粗 = 关键洞察;落点一行。

---

### D1(2026-07-10)模块 3 = 工作流,编排层定位

> 设计工作流 - 第三个模块

**机制文档回答"是什么",工作流回答"谁、何时、经哪个入口、失败后去哪"——在导入/运行时成模前先锁编排,后续模块的实现取舍就有了用户旅程当裁判;工作流自身不定义机制,发现机制缺口时回修机制文档,本篇仅三处小修订(§12)。**

落点:全文七条 WF + §10 总表;编号再顺延(workbook 与身份/导入与编辑/运行时/兼容 → 模块 4/5/6/7)。

---

### D2(2026-07-10)工件版本控制策略

推演自发:快照、bytes、生成代码、报告是否入库,此前散落无定论,而它决定每个 WF 的起点状态。

**版本控制里只放"人改的事实源 + 消费方直接需要的投影":生成代码入库(Unity 直接消费源码),投影陈旧靠 CI 新鲜度门禁不靠记忆;快照是本地缓存——身份的跨机载体是文件内伴随列,不是缓存目录。**

落点:§1 工件表、§9 门禁 1;快照缺失的退化语义引 P§6.2/5.3,不另建机制。

---

### D3(2026-07-10)协作粒度 = workbook,冲突 = 二选一 + diff 对账

推演自发:xlsx 二进制在版本控制里不可合并,旧计划的评审工件子系统已裁,团队协作却是真实约束。

**治理手段是"拆书降低相撞 + 相撞后对账重做",不是给二进制发明合并器;对账需要机器可读差异,而 diff = 只读导入 + guid 对齐 + canonical 比较,全是既有机制的免费组合——恢复为单一 CLI 命令,快照入库/签名工件维持裁剪。**

落点:§8 全节、P§10 修订、ADR-19;工具锁维持裁剪(ADR-12),认领是团队约定不是机制。

---

### D4(2026-07-10)结构变更原子交付与新表落点

推演自发:proto、生成代码、xlsx 三工件存在跨人漂移窗口;多书项目里 generate 不知道新表建到哪本。

**漂移窗口只能用"同一变更集"消灭,门禁(lint/新鲜度/check-drift)抓漏网;表的物理归属以其所在书的 metadata 为事实,新表落点是操作者决策不是 schema 事实——多书歧义给 blocker 不给猜测,schema 里也不添 workbook 字段。**

落点:§4 步骤 4 与追加分级两行;§9 门禁;drift 在 CI check 判 error。

---

### D5(2026-07-10)dirty 不跨 domain reload 静默丢失

推演自发:dirty 与 Undo 是内存态,Unity 脚本编译/进 Play 的 domain reload 会清空,半小时编辑可能无声蒸发。

**内存 dirty 是全链路最脆的工件,而"不丢数据"是仲裁序第一条——编译与进 Play 必须成为拦截点(保存/放弃/取消;有未解冲突时不给"保存并继续");这是 adapter 的义务,不是可选优化。**

落点:§6 步骤 4、§7 前置、§11 剧本 6;接线归模块 6。
