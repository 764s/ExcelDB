# 模块 3:工作流(端到端编排)

状态：Accepted Design，尚未 Dependency-Complete。本文是 M3 角色、工件和端到端编排的唯一 owner，只回答“谁、何时、经哪个入口、失败后去哪”，不重定义 M1/M2/M5-M8 的领域机制。跨模块权威规则和开放决策见 [`docs/spec/README.md`](../spec/README.md)。本文仍出现的 `P§x` 只按总纲 §7 的迁移表解析，不指向归档计划；文末决策记录仅解释背景，不增加契约。

## 1. 范围、角色与工件

- 范围:七条工作流(WF1-WF7)+ 失败恢复总表。每条工作流 = 步骤序列 + 失败分支(就近陈述或收敛于 §10 总表);步骤只做编排——"操作 + 入口 + 报告去向",机制细节一律引用机制章节。
- Project 主引导边界:程序主线只有三个用户阶段：“Project 初始化 → 创建或调整 Excel 表结构并生成 C# → 填写 Excel 并生成对应数据”。阶段 2 首次使用 `table create`,后续可在同一阶段零次或多次 `table edit`;阶段 3 固定为用户填写 Excel 后显式 `data prepare → check → convert`,产出默认 `client` 目标的 bytes+manifest。它是一条面向单个 Project 的首次闭环导航,组合 WF1/WF2/WF3/WF7 的相关步骤,不是 WF1-WF7 的总容器,也不改变各 WF 原有的触发者、状态或失败恢复;`server` 与未来其他目标的 CI/发布转换不进入首次主引导,CLI 的具体参数与交付包装仍归 M4 投影。
- 独立工作流边界:应用方产生 dirty、合并并写回 xlsx 仍走独立 WF4;Git/VCS 中的 diff、PR 评审与冲突对账仍走独立 WF6。M4 即使提供入口或报告投影,也不把二者并入 Project 主线。
- 角色 × 工作面(一人可兼多角色;工作面决定能力边界,不是权限系统):

| 角色 | 工作面 | 拥有的工件 |
| --- | --- | --- |
| 程序 | proto、C#、Unity 编辑器、CLI | schema(proto,含字段 effective export targets)、生成代码、validator/codec/provider 与导出策略注册、`ExcelDb.Project.json` |
| 策划 | Excel、Unity 编辑器(Browser/inspector) | 数据区行、辅助行列、自由 sheet(P§5.1) |
| CI | CLI | 门禁与报告工件(M4§5) |
| 游戏 | RuntimeDatabase | 派生缓存(订阅 ChangeSet 自建,P§7.3) |

- 工件 × 版本控制(纪律;违背由 WF7 门禁抓,不靠记忆):

| 工件 | 进版本控制 | 说明 |
| --- | --- | --- |
| `*.proto`、`ExcelDb.Project.json` | 是 | 程序拥有;proto 是结构事实源(M1§1),字段的 effective export target 集也必须由策略展开后显式落在 proto;可由 `table create/edit` 向导维护,Project 是唯一项目配置源(M4§3.2) |
| 生成代码(codegen 产物 .cs) | 是 | schema 的强类型投影,不是数据文件;codegen 必须为 proto 中的每个 effective target 产生对应 runtime surface/registry 投影,具体物理文件包装归 M1;Unity/宿主直接消费源码;新鲜度由 CI 门禁保证(WF7);手改无效(M1§1) |
| `*.xlsx` | 是 | 数据事实源;二进制,协作规则见 WF6 |
| 快照/缓存(cacheDir,P§6.2) | 否 | 本地缓存,不跨机;身份的跨机载体是文件内 `__guid` 伴随列,快照只提供合并 base 与身份修复(P§5.3 规则 2/5),fresh clone 首次导入重建,缺失退化见 P§6.2 |
| bytes + manifest(P§7.4) | 否 | 每对工件只对应一个 export target,并与该 target 的生成 C# runtime surface/registry 及 SchemaHash 对应;可重建,CI/构建按 target 重新 convert,本地按需 |
| 报告(`--json` 产物) | 否 | 流水线归档的构建工件 |

## 2. 全景与入口映射

```text
空目录 + 单文件 exceldb ──init──> Project 骨架
       └──table create / 可重复 table edit(计划→确认)──> proto ──lint+codegen──> descriptor+C# 类型
                                                        └──generate──> xlsx 结构(行1-3、下拉、metadata)
xlsx 数据 <──用户 Excel 直编                         <──SaveAssets 补丁写回── 编辑器编辑(dirty,内存态)
   │   ↑watcher(P§6.8)                                        ↑三方合并/冲突(P§6.3)
   └──data prepare──> check/导入校验(P§6.1) ──convert(target;G1=client)──> target bytes+manifest

[宿主集成边界] 生成 C# 中同 target 的 RuntimeSchemaRegistry + source + options ──Open──> 运行时 ⇄ 同 target 切源/热载(P§7.5/7.6)
```

- 入口等价:同一操作的全部入口走同一管线、产同一 OperationReport(P§9);菜单是 CLI/API 的 UI 投影,禁止菜单专属行为。
- 入口等价只约束同一操作的语义,不表示所有入口属于同一工作流。Project 引导可以转交 WF4/WF6,但转交不改变它们的独立生命周期与责任边界。

| 操作 | API | Unity 菜单(P§11) | CLI(P§10) | 报告面 |
| --- | --- | --- | --- | --- |
| Project 初始化 | — | 空态引导 | init | 初始化计划 / OperationReport |
| 表结构创建/修改 | — | — | table create / table edit | 变更计划 + OperationReport |
| 挂载/卸载 | Mount/UnmountWorkbook(M2§3) | Mount | —(按项目 json 自动挂载) | workbookImported |
| 刷新/导入 | Refresh/ImportAsset(M2§3) | Refresh;watcher 自动 | check(只读) | workbookImported + 报告窗口 |
| 结构生成 | — | Generate | generate | 报告窗口 / `--json` |
| 新行身份固化 | SaveAssets 可组合(M6) | Data Prepare | data prepare | DataPreparePlan + OperationReport |
| convert | — | Convert(client) | convert(target) | 同上;报告与 manifest 均标记 target |
| schema 编译/校验 | —(工具期) | — | schema build | OperationReport / `--json` |
| 差异对账 | — | — | diff(§8) | stdout / `--json` |
| 保存 | SaveAssets 族(M2§6) | 保存动作 + 编译/进 Play 拦截(§6) | — | OperationReport |
| 冲突解决 | GetConflicts/ResolveConflict(M2§7) | 冲突对话框 | — | 同上 |
| 切源/热载 | RuntimeDatabase(M7) | Play 前临时选择 + Play 工具栏(M7) | — | Diagnostic + changed 事件 |

## 3. WF1 空目录 Project 首次闭环(一次性,程序)

1. 把当前平台的单文件 `exceldb` 可执行文件放入除它之外为空的目录并运行。未发现 `ExcelDb.Project.json` 时,交互入口把 cwd 作为候选 Project 根并询问确认;显式入口是 `init`,非交互参数形态归 M4。
2. `init` 先展示完整计划,确认后一次建立可自洽的 Project 骨架:`ExcelDb.Project.json`、schemaDir 以及约定的 Schema/Generated/Data/Build/cache 目录。初始化不得要求预存 schema 程序集、外部构建工程或 SDK;任一写入失败都不得留下可被识别为成功 Project 的半成品。对已有合法 Project 重跑时只校验并补齐缺失的默认目录,其余为 no-op;只有不兼容同名工件才阻断。
3. 进入 `table create`:用户选择具体 xlsx 路径,只给出表名、显式 key 或 M4 `AutoKey`、首批简单字段与少量封闭的自动选项；每个普通字段以“客户端/服务端”两个简单勾选表达初始导出意图,默认两者均选。M1 `ITableInitializer` 把结构输入展开为 table-local draft；M1 `IExportTargetStrategy` 只在 create plan 构造期为未显式指定 target 的字段补齐目标,标准实现 `StandardClientServerExportTargetStrategy` 补成 client+server。向导再按 M1 规则建议数字身份,并把所有手工/自动字段、effective defaults、effective targets、option 与数字身份在同一计划中交给用户确认。确认后必须把完整 target 结果显式写入 `.proto` 这一结构事实源,不得把策略代码或会话结果留成下游第二事实源；同一操作再完成 lint/codegen 与 workbook 结构生成,产出 `proto + 每 target C# runtime surface/registry 投影 + xlsx` 三件套,具体生成文件包装归 M1。路径不存在时直接创建含 metadata、行 1-3、下拉、`__exceldb_keys` 与伴随列的 workbook(P§5.1/5.2)。任一环节 blocker 均不得只留下其中一部分。
4. 初始结构需要调整时在阶段 2 转入 WF2 的 `table edit`,可重复直到结构确认;恢复已有 Project 时按事实选择动作:proto 存在但 descriptor/C# 陈旧则 `schema build`,xlsx 结构缺失或陈旧则 `generate`,不得盲目重跑 `table create`。之后用户打开 xlsx 填写数据并进入 WF3。Excel 中的任意手工列不会反向成为 schema;正式结构仍由向导更新 proto。
5. 对已填写 workbook 显式执行 `data prepare`,把 `pending-new` 的候选 RowGuid 按已确认计划固化;随后执行只读 `check`,通过后执行默认 `client` 的 `convert`,产出 target 已明确记录、并与生成 C# client `RuntimeSchemaRegistry.ExpectedSchemaHash` 对应的 bytes + manifest。至此单文件工具的首次闭环完成；若 schema 同时存在 `server` 导出面,主引导只提示 M4 的显式 server convert 命令,不代替发布流程选择路径或生成第二 Project 配置。提交应入库的 Project/proto/生成代码/xlsx 形成首次基线;宿主编译生成 C# 并执行 Runtime `Open` 属于独立集成边界,不是主引导的完成条件。

失败分支:`init` 被拒绝或无法完整提交 → 目录仍是未初始化态,修复权限/路径后重跑;initializer/导出策略异常、非法 draft/effective target 或 `table create` 的 schema、codegen、workbook 计划出现 blocker → 三件套零替换,修改初始化/策略代码或输入后重跑;`data prepare` 计划 stale → workbook 零写入并重新扫描/确认;`check`/任一 target 的 `convert` 失败 → 保留事实源与该 target 的旧数据工件,按报告修 Excel 后从 `data prepare` 或 `check` 继续。

## 4. WF2 表结构演进(`table edit`,程序主导)

1. 启动 `table edit`,选择已有表并声明新增、重命名、删除、格式或字段 client/server 导出选择变化。向导读取当前 proto/descriptor 与 workbook,把用户意图和 M1 `IExportTargetStrategy` 只为未显式项补齐的 effective target 集一并写成显式 proto 变更:新增字段用新 number;改名保 number(身份不变即兼容,M1§1);字段删除、导出目标与其他字段语义变更走 M8§5 三阶段;已发布表的删除请求必须转换为 M8§3.1 的显式退役,保留原 message/id 并设置 `TableOpts.retired = true`;格式变更走 M1§2 三阶段。策略 `Id` 只进 plan 构造期审计,plan/apply 与后续 build/generate/check/convert 不再执行或依赖策略；向导维护 proto 不改变它的事实源地位,C# 与 Excel 表头都只是该结构的投影。
2. 计划阶段同时列出 proto patch、将重新生成的 C# 文件和受影响 xlsx 动作,并运行 lint/兼容分析;未确认时零写入,blocker 时 Apply 不可用。每个兼容或格式迁移阶段是一轮 WF2,进入下一轮的证据 = 最近 check/normalize 报告命中归零。
3. 确认后按一个提交单元更新 proto → descriptor/codegen → xlsx 结构;任一步失败都不得形成“proto 新、C# 或 xlsx 旧”的局部成功。底层结构生成仍遵守 generate 的分级动作与门禁(P§6.6);必要时再计划并应用 normalize。

`table create` 在首次闭环后也可用于增加新表,并与 `table edit` 共用上述计划/确认/提交纪律。新表落点规则追加两行(P§6.6 修订):

| 变化 | 级别 | 动作 |
| --- | --- | --- |
| workbook 文件不存在(具体路径) | safe | `table create` 新建含 metadata 的空书再建表;glob 不产生此项,首建必须给具体路径 |
| 新表落点歧义(多书目标且未指定具体 workbook) | blocker | 拒绝;已生成表跟随其所在书的 metadata,新表落点是操作者决策;跨书移动表 = 人工剪切 sheet 后两侧 generate 修复 metadata |

4. 本地 `check` 确认无 drift → 原子交付:proto + 生成代码 + 受影响 xlsx(+ normalize 重写)同一变更集提交。
5. 他人拉取:新生成代码随仓库到达 → Refresh 重导入,快照 descriptor 摘要随之更新(P§6.6);若 CI 重跑 codegen 发现不新鲜则拒绝交付。

失败分支:`table edit` 计划 lint/兼容 blocker → 零写入并修改结构意图;apply 中 codegen/generate error → 三件套保留旧一致版本;不可 reparse 坏 cell → 修数据或改 schema 后重跑;key 结构/区域重叠 → `--rekey`/挪列(P§6.6);漂移漏网 → WF7 门禁红,schema.drift 在 CI check 判 error。

## 5. WF3 Excel 填表与调数(策划主导)

1. 开 Excel 直编:新行 `__guid` 留空并被只读扫描标记为 `pending-new`;扫描、watcher、diff 与 `check` 都不得替用户写入身份。枚举/引用列用下拉(P§5.1);辅助行列与自由 sheet 随意,系统永不触碰;手工列想转正式字段 → 提给程序走 WF2(手工列在导入侧只是 warning 保留,P§6.1)。
2. 保存 xlsx:
   - Unity 开着:watcher 防抖导入(P§6.8)→ Browser/inspector 即时更新;error 单元格级定位展示,不阻塞继续编辑,只门禁 save/convert(P§9)。
   - Unity 没开:改动静置;开 Unity/Refresh 或 CI check 兜底。纯 Excel 工作面推荐 pre-commit 挂 `exceldb check`(建议,不是机制)。
3. 高危编辑的既定语义(引用即可,不复述):整行复制 → 首行保身份、余为新行(P§5.3-2);改 key → 改名 + 引用 cell 自动改写(P§5.3-4、P§8.1);删被引用行 → 持有方 delete_policy 门禁,BLOCK 表现为 ref.dangling error(M1§2)。
4. 提交:存在 `pending-new` 时先显式执行 `data prepare`,其计划绑定 schema、workbook 与观察行指纹,确认后只固化系统 `__guid` cell且不改业务值;再执行 `check`,绿色后 xlsx 入库。`convert` 与 runtime source 对任何 pending identity 都硬拒绝;SaveAssets 若同时落盘新行,必须组合复用同一身份计划。PR 评审工件见 §8。

## 6. WF4 应用方结构化编辑与写回(独立工作流)

本 WF 由应用方/编辑器侧对 resident 数据的编辑触发,从 dirty、外改合并到 xlsx 写回自行闭环;它不以完成或进入 M4 Project 主引导为前提。

1. Browser/inspector 或工具代码:SO/SP 编辑、直改字段 + SetDirty(门面细节归 M6);结构操作(Create/Delete/Rename/Move/Copy)即时生效于内存与索引(M2§5)。
2. 一切落盘收敛到 SaveAssets(M2 Δ3):preflight 指纹比对 → 外部有改先合并(P§6.3)→ 冲突全解才写(M2§7)→ 补丁写回 + 复读 + 原子替换(P§6.5)。
3. 与 Excel 双开同书合法(核 6 的设计场景):外改由 watcher 合并;大批量操作用 Start/StopAssetEditing 聚合(M2§5)。
4. dirty 与 Undo 是内存态:domain reload(脚本编译、进 Play)会清空。adapter 必须在编译与进 Play 前拦截未保存 dirty——默认弹窗(保存/放弃/取消),存在未解冲突时仅可解决或取消;dirty 静默丢失违反总纲 §4。接线归 M7。

## 7. WF5 Play Mode 现场调参

1. 进 Play 时由宿主把当次 source、当前生成代码提供的同 target `RuntimeSchemaRegistry` 与 `RuntimeBootstrapOptions` 三者显式传给 M7;Unity 工具面缺省使用 `client`。bytes 模式先做陈旧检测——manifest 的 target 或该 target 内容 hash 与当前请求/xlsx(M7)不符 → 提示 Convert Client 后进 / 带旧数据进 / 取消。Project、EditorPrefs、source/manifest 与上次会话都不得提供隐式 runtime target 或反推 registry expected hash。
2. 当次显式选择同 target Excel 源并启用 hot reload:改 Excel 保存 → 运行时仅按该 target 的 runtime surface 热载(P§7.5),Game 视图即时生效;Play 中 inspector 改数走 editor API 写回(核 10)→ SaveAssets 落盘 → 同一热载环。两条改数路径的汇合点 = xlsx 文件;编辑器与运行时不共享草稿。
3. 调好 → Convert Client → SwitchDataSource(client bytes) 复验出包数据(M7)。
4. 停 Play → 按 WF3/WF4 收尾提交。

失败分支:热载解析失败 → 保留旧数据 + 报告,修表再存即自动重试(P§7.5);切源 schema_hash 或 export target 不符 → 拒绝并保旧源,按同 target 重新 convert(P§7.6)。

## 8. WF6 协作与版本控制(独立 Git/VCS 行为)

- 边界:本 WF 从版本控制中的变更、评审或合并冲突开始,以仓库状态和评审结果收敛,不是“Project 初始化 → 创建或调整 Excel 表结构并生成 C# → 填写 Excel 并生成对应数据”主引导中的一步。Project 工具可以提供跳转或人读报告,但 diff/PR 对账的事实源与生命周期仍归 Git/VCS。

- 协作粒度 = workbook:按域/负责人拆书(挂载集即 json glob;跨书引用自由,key 唯一域跨书,P§8.1);同书并行写高危 → 团队认领约定,不做工具锁(ADR-12)。
- xlsx 在版本控制里不可文本合并。冲突剧本:
  1. 冲突发生 → 整文件二选一(通常取先合入方)。
  2. `exceldb diff <merge-base 版> <被弃版>` 列出被弃改动(版本文件经 `git show` 提取)。
  3. 在存活版上重做被弃改动(Excel 或编辑器均可)→ check → 提交;重做后再 diff 一次收敛自查(可选)。
- `exceldb diff`(P§10 命令族新增;差异是事实不是失败,退出码仍按公共约定只反映操作成败):

```text
exceldb diff [--schema-dir <dir>] <base.xlsx> <target.xlsx> [--json <path>]
双方各走只读导入(P§6.1 宽松列绑定;不写快照、不落身份分配);结构漂移以 schema.drift 随报告
行对齐:__guid 优先;无 guid 行按 (key, 行序) 尽力配对
分类:added / removed / renamed(同 guid 异 key)/ modified(cell 级 canonical 值对,字段路径定位)
```

- 评审工件:PR 流水线对每本变更 workbook 产出 check 报告 + 对 merge-base 的 diff 报告并附评审(二进制不可读的补偿);快照入库、签名工件维持裁剪(ADR-19)。

## 9. WF7 出包与发布

1. CI 门禁语义由本文规定,工具配方由 M4§5 投影:生成代码新鲜度、schema/workbook 一致性、数据校验、`client`/`server` 两次显式 convert 与评审工件均必须通过;schema.drift 在 CI check 判 error。一次 convert 只生成一个 target 的 bytes+manifest,不存在 `all` 或多目标隐式循环；CI 在 staging 中等待全部要求 target 成功后才发布。
2. 客户端构建:`IPreprocessBuildWithReport` 钩子固定执行 `client` convert → `StreamingAssets/ExcelDb/`(P§11),只包装 client bytes 与其同行 manifest。服务端构建由宿主/CI 显式执行 `server` convert 到独立路径并只包装 server 工件；Project 不保存发布矩阵或服务端路径。
3. Release 运行:宿主编译当前 schema 为目标生成的 C# runtime surface,把同 target 的 `RuntimeSchemaRegistry`、`ConvertedBytesDataSource` 与本次 `RuntimeBootstrapOptions` 显式传给 `Open`;无热载与写回(P§7.8)。source 声明、candidate 实读与 registry expected 三个 SchemaHash 必须相等,三方 export target 也必须一致；客户端包不得携带或以 server registry 打开 server 工件,反之亦然。
4. 线上数据补丁 = 重新 `data prepare/check/convert --target <同目标>` + 宿主资源系统只向该目标分发新 bytes+manifest + 使用同 target generated registry 显式 Open(P§7.8);bytes 的 schema_hash 必须等于包内代码的 SchemaHash(P§7.4 校验拒绝),manifest/report 必须明确 target。client/server 补丁路径、工件与发布批次隔离,不得用改名把一端产物投给另一端；补丁窗口 = 纯调数,结构或 effective target 变化必须随对应代码包发布。

## 10. 失败与恢复总表

| # | 失败点(所在 WF) | 表现 | 恢复动作 |
| --- | --- | --- | --- |
| 1 | initializer/导出策略失败或 lint/兼容 blocker(WF1/WF2) | `table create/edit` 计划拒绝,proto/C#/xlsx 零替换(M1§1.1/§5) | 修初始化/策略代码或在向导中修结构意图;需要高级能力时修 proto 后重跑计划 |
| 2 | codegen/generate error(WF1/WF2) | 三件套保留旧一致版本;报告列出失败阶段与坏 cell(P§6.6) | 修数据或 schema;`--rekey`/挪列后重跑 |
| 3 | 导入 error(WF3) | 行可加载,save/convert 门禁(P§9) | 按 cell 定位修 Excel |
| 4 | schema.drift(WF3) | 写回门禁(P§6.1);CI check 红 | 拉最新代码,或由程序走 `table edit`/底层 generate 恢复一致投影 |
| 5 | 保存遇外改(WF4) | 自动合并;冲突 → Conflicted(P§6.3) | ResolveConflict 全解 → SaveAssets(M2§7) |
| 6 | 保存遇文件锁(WF4) | 重试 3 次失败,dirty 保留(P§6.5-5) | 关 Excel/解锁 → 再 SaveAssets |
| 7 | 热载失败(WF5) | 旧数据保留 + 报告(P§7.5) | 修表再存,下一次变更自动重试 |
| 8 | schema hash 或 export target 不匹配导致切源被拒(WF5) | 保留旧源(P§7.6) | 以当前 registry 的同 target 重新 convert 后再切 |
| 9 | workbook 格式过新(任意) | 只读打开(P§5.2) | 升级工具版本 |
| 10 | xlsx 版本控制冲突(WF6) | 文本合并不可用 | §8 冲突剧本(二选一 + diff 对账重做) |
| 11 | target convert error(WF1/WF7) | 不产出该 target 新的 bytes+manifest,该 target 旧数据工件不被替换;其他 target 工件也不被本次调用触碰,退出码门禁(P§10) | 按带 target 的报告修 Excel 后从 check 重跑;CI 全目标成功前不发布 |
| 12 | 坏数据已入库(任意) | 游戏/校验暴露 | 版本控制回退 xlsx;本地无 dirty → Refresh 直接导入,有 dirty → 走合并/冲突(P§6.3) |
| 13 | init 碰撞/写入/内嵌资源失败(WF1) | 目录仍为未初始化态,无半 Project;碰撞 = blocker 2,资源/环境异常 = 3 | 改用空目标或处理同名工件后重跑;内嵌资源损坏则替换可执行文件 |
| 14 | pending identity / stale DataPreparePlan(WF1/WF3/WF7) | check 报 `identity.pending-new`;convert/runtime 拒绝;计划 stale 时 workbook 零写入 | 重新扫描并确认 `data prepare`,固化后重跑 check/convert |

## 11. 模块验收测试

带"复用"标记的断言主体在既有测试面,本模块只加编排层剧本:

1. 空目录首次闭环:测试目录只有当前平台单文件 exe → 无参数运行 → 完成“Project 初始化 → 创建或调整 Excel 表结构并生成 C# → 填写 Excel 并生成对应数据”三阶段。断言 Project/schemaDir 及约定目录可自洽,不依赖预存程序集、宿主工程、SDK、PATH 工具或网络。`table create` 指定不存在的 xlsx,只输入表名、`AutoKey`、简单字段及 client/server 勾选,预览时 initializer 自动字段/option、`StandardClientServerExportTargetStrategy` 为未显式项补出的 effective targets、M1 effective defaults 与数字身份均完整可见;确认后 target 集显式落 proto,一次得到 proto、每 target 生成 runtime surface/registry、C# 与 workbook,三者 SchemaHash/数字身份一致且 metadata/伴随列/下拉齐备。填写首行 → data prepare → check → 裸 convert,断言仅 `__guid` 被计划写入、业务值不变,只产生 target=`client` 的 bytes+manifest,其 schema_hash 等于 client generated registry 的 ExpectedSchemaHash；G1 仅提示而不执行 server convert。分别在 init、initializer、导出策略、lint、codegen、xlsx 提交点注入失败,断言无半初始化 Project 且三件套零局部替换;宿主编译 C# 后的 Runtime Open 冒烟归独立集成测试。
2. 演进剧本:`table edit` 对加字段 / rename(number 不变)/ 删字段 / 修改 client-server 勾选四例先只看计划,断言零写入且策略只补未显式 target;apply 后 effective target 完整落 proto,proto/C#/xlsx 同步且数据保留(P§12-6 复用)。删除策略/会话/cache 后从 proto 重建仍得到相同 target surfaces；构造"proto 新、xlsx 旧"仓库态 → CI check 以 schema.drift error 红。
3. 新鲜度剧本:由向导或直接改 proto 后不重生成 → CI 新鲜度门禁红;重生成 → 绿。
4. 调数剧本:Excel 改 cell/加行/复制行/改 key/删被引用行 → 身份与引用语义(P§12-3 复用;delete_policy 语义 M1§2)→ pending 时 check 只报错且零写入 → data prepare 计划固化身份 → check 绿后入库;计划前后任一指纹变化均以 stale 拒绝。
5. 双面剧本:编辑器 dirty + 外部改同 cell → 冲突两种 Resolve 各一 → SaveAssets 落盘(P§12-4 复用)。
6. 拦截剧本:dirty 未保存触发脚本编译/进 Play → 拦截提示;未解冲突时无"保存并继续"路径(§6;M7 落地后补接线测试)。
7. Play Mode 剧本:宿主显式传入同 target source + generated registry + options,断言 source 声明/实读 hash 与 registry expected hash 三者相等且三方 target 一致后 client bytes 进 Play(陈旧检测含 target/hash 提示一例)→ 切 client Excel 源 → 热载 5 事件(P§12-7 复用)→ Convert Client → 切回 client bytes 值等价;缺 registry、任一 hash 不符或 client/server 交叉组合均拒绝且不反推、不替换旧源。
8. 协作剧本:两副本并行改同书 → diff 四分类各至少一例 → 对账重做后 diff 归零;schema.drift 下的宽松 diff 一例。
9. 新表落点:多书 glob 含新表且 `table create` 未指定具体 workbook → blocker;指定后落点正确;不存在路径走新建 workbook 的 safe 计划。
10. 失败恢复:总表 14 行各至少一测(复用处标注既有测试项)。
11. 多目标发布:同一 proto/data 先裸 convert 得 client 工件,再显式 server convert 到独立路径；两份 manifest/report 分别标记 target,client/server bytes 只含各自字段。只修改 server-only 值时 client bytes 与 target 内容 hash 不变,反向同理；任一 target 转换失败时该 target 旧工件保留且流水线不发布,另一 target 不被本次调用触碰。客户端/服务端包装各自只含同 target 工件,补丁交叉投递与 client/server registry/source 交叉 Open 均被拒绝。

## 12. 与仓库现状衔接

- 本模块仅文档,无代码交付(同 M2 惯例)。
- 权威迁移后,工作流与门禁语义由本文拥有;CLI/UI/CI/VCS 的具体投影只由 M4 拥有,归档计划与实施文档均不得补充第二种流程。
- M4 已同步 self-contained 单文件交付、Project 自举配置、三阶段 G1、target-scoped convert 与 `init`/`table create`/`table edit`/`data prepare`;具体实现仍为 Not implemented,不因文档闭环提升交付状态。
- M1 已同步“向导可事务性维护 proto”入口与导出目标策略边界,且 proto 的结构事实源地位不变;策略展开的 effective targets 必须显式落 proto,Excel 与生成 C# 仍只是数据事实源/结构投影,不得反向定义 schema。
- 历史裁剪 ADR 第 19 条曾记录评审/对账工件恢复为单一 `diff` 命令；该记录现已归档,当前工具边界只以 M4 为准。
- poc 无工作流对应物;WF 剧本的可执行化随 M6(导入与编辑)与 M7(运行时/adapter)的实现推进。

## 13. 跨模块边界与开放决策

- proto 数字身份、向导可执行的 schema patch、`IExportTargetStrategy` 及每 target runtime surface/registry 的 codegen 原子提交机制与物理包装 → M1;策略结果必须落 proto,C# 始终只是类型投影。
- self-contained 单文件交付、空目录发现、`init`/`table create`/`table edit` 参数与 Project 可自举配置 → M4;M3 只规定步骤、完成态与失败恢复。
- workbook 物理契约、metadata、RowRef token、`pending-new` 与身份计划可写边界 → M5。
- 导入、DataPreparePlan 事务、诊断、ImportReport、SO/Undo/EditorUtility 与 diff 数据面 → M6。
- resident 承载、Play Mode、发布点与 ChangeSet/workbookImported 次序 → M7 / Unity adapter。
- 字段级三阶段兼容矩阵与 normalize 细则 → M8。

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

---

### D6(2026-07-11)单文件 exe 是空目录首次闭环入口

> 预期 会有一个可执行文件, 当我爸这个可执行文件粘贴到一个空文件夹时, 该文件夹会引导我将这个空文件夹初始化为一个项目.
> 该可执行文件还将引导我:
> 从0创建一个excel.
> 修改表结构, 并生产cs数据文件,
> 生产cs文件的对应数据,

**首次体验的最小闭环不是先要求用户搭 schema 工程,而是由可复制的单文件 exe 自举 Project,再以三个用户阶段收敛:`table create` 与可重复 `table edit` 在同一结构阶段维护仍为事实源的 proto,并把 proto、C# 类型投影与 xlsx 结构作为同一计划提交;用户填写 xlsx 后显式 `data prepare/check/convert` 产生与 generated registry SchemaHash 对应的 bytes+manifest。应用方写回与 Git diff 仍是独立工作流。**

落点:§1 主线与工件语义、§2 全景、WF1/WF2、§10-11;M1 D12 已同步向导维护 proto,M4 D10 已同步 self-contained 单文件、三阶段引导与 `init`/`table create`/`table edit`/`data prepare` 投影。

---

### D7(2026-07-12)字段导出按 client/server 目标显式物化,首次闭环只产 client

> 另外允许表的字段声明为，服务端导出和客户端导出， 这一块也建议抽象为借口策略， 暂时这个策略仅关心前后端， 未来可能有简易前端之类的

**使用者只勾选字段的客户端/服务端意图,`IExportTargetStrategy` 只在 create/edit 计划构造期补未显式项并把 effective target 集完整写进 proto；从此 build/codegen/convert/runtime 不再依赖策略。一个 convert 只产一个 target,因此 G1 安全地固定 client,server 与未来目标由 CI/发布显式转换、隔离包装。**

落点:§1 工件、§2 入口、WF1/WF2/WF5/WF7、§10-11；具体 CLI 五键与 target 参数归 M4 D12,策略与每 target runtime surface/codegen 归 M1。
