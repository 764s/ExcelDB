# 模块 4:集成工具(场景 × 使用者)

状态：Accepted Design，尚未 Dependency-Complete。本文是 M4 集成工具投影的唯一 owner，以场景 × 使用者矩阵作为设计覆盖契约；工具只投影 M2/M3/M5-M8 的机制，不发明领域行为。跨模块权威规则、实现状态和开放决策见 [`docs/spec/README.md`](../spec/README.md)。本文仍出现的 `P§x` 只按总纲 §7 的迁移表解析，不指向归档计划；文末决策记录仅解释背景，不增加契约。

## 1. 方法与覆盖度契约

- 使用者 = M3§1 四角色(策划/程序/CI/游戏)+ 评审者(WF6 特设视角,由程序或策划兼任);游戏列 = 运行时消费方(含 Development 构建操作者)。
- 场景 = M3 WF1-WF7 的时刻级展开(§2 行全集)。覆盖度契约:
  1. 每个场景行至少一个非空格;每格取值必须落在图例值域内——"想不出填什么"= 设计缺陷,不许留白混过。
  2. 工具条目(§3-§6)必须被至少一格引用;无格引用的工具 = 幽灵工具,删除(唯一豁免:C11 隐藏测试基建,不属团队工具面)。
  3. 新场景先加行、再配工具;新工具先找格、再写规格。
- 格值域(图例):`G1/C/U/I/V+编号` = §3-§6 工具条目(`G1` = §3 Project 主引导);`烘焙` = generate 写进 workbook 的产物(下拉/批注/数据有效性,P§5.1),使用时无运行工具;`P§x 钩子` = 自动机制承担,无人工工具(如构建钩子);`API` = 库 API 直接消费,无工具面(游戏侧 UI 归宿主);`→WFn` = 角色交接,不是工具;`约定` = 团队纪律(M3);`✗n` = 显式不提供(§9 行 n);空 = 该使用者不参与。
- 矩阵覆盖全部工作流时刻,G1 只覆盖“从空目录到 C# 类型 + Excel 数据 + bytes/manifest”的三阶段程序主线;矩阵非空不等于该场景必须进入 G1。Runtime Open、应用方写回/冲突、Play 调参、CI 与 VCS 评审各走自己的 API/U/I/V 入口。
- 投影原则:工具的每个领域动作必须映射到机制文档已有的操作与 API,同一操作跨入口走同一管线、产同一 OperationReport(M3§2);G1 三阶段只组合既有命令并逐条回显,自身只负责导航,不增加领域操作语义(§3.1);UI 可以加糖(如 Browser 搜索扩展),糖不回写 API 语义。
- 失败形态沿用 M2§1:失败返回 + Diagnostic,无静默 no-op;对话框"取消"= 操作中止、零写入。失败的呈现位置按 §8 映射。

## 2. 场景 × 使用者矩阵

| # | 场景(WF) | 策划 | 程序 | 评审者 | CI | 游戏 |
| --- | --- | --- | --- | --- | --- | --- |
| S1 | 空目录放入可执行文件 → 初始化 Project → 创建首表(WF1) | | G1,C1,C3,V1 | | | |
| S2 | Unity 首开:挂载、空态引导、workbook 状态一览(WF1) | U1,U5 | U1,U5,U11 | | | |
| S3 | 修改表结构 → 生成 C# → 同步 Excel(WF2) | | G1,C2,C4,C5/U10,C7 | | I1-I4 | |
| S4 | 格式迁移三阶段(WF2) | | C6,U10,C7 | | I4 | |
| S5 | 拉取他人 schema 变更后同步(WF2) | U1,U6 | C2,C5,C7,U6 | | | |
| S6 | Excel 日常填数与显式数据准备(WF3) | 烘焙,C10 | G1,C10 | | | |
| S7 | 保存看结果,Unity 开着(WF3) | U3,U6 | | | | |
| S8 | 保存无反馈,纯 Excel 工作面(WF3) | V3,I5 | | | I4 | |
| S9 | 高危编辑:改 key/删行/复制行(WF3) | U3,U6 | | | | |
| S10 | 需要新列/新表(WF3) | →WF2 | G1,C3/C4,U10 | | | |
| S11 | 结构化编辑与保存(WF4) | U2,U3,U4,U12 | 同左 + M2 API | | | |
| S12 | 外改撞 dirty,解冲突(WF4) | U7 | U7 | | | |
| S13 | 危险时机拦截:编译/进 Play/退出/卸载(WF4) | U8 | U8 | | | |
| S14 | 进 Play 选源与陈旧检测(WF5) | U9,U13 | U9,U13 | | | |
| S15 | 运行中改 Excel 热载(WF5) | U13 | U13 | | | API |
| S16 | convert 后切 bytes 复验(WF5) | U2,U13 | C8,U2,U13 | | | |
| S17 | Development 构建现场调数(WF5) | | | | | ✗2,API |
| S18 | 同书并行写的预防(WF6) | 约定 | 约定 | | | |
| S19 | 提交前自查改动(WF6) | U12,V2 | C9,V2 | | | |
| S20 | PR 评审 xlsx 变更(WF6) | | | I5 | I5 | |
| S21 | 版本冲突对账重做(WF6) | U3 + I5/C9 报告 | C9,V2 | | | |
| S22 | 门禁与工件(WF7) | | | | I1-I7 | |
| S23 | 构建出包(WF7) | | U2 + 构建钩子(P§11) | | I4 | |
| S24 | 线上数据补丁(WF7) | | C8 | | I4 | API |
| S25 | 运行时数据排查:来源与版本(WF7) | | | | | API + manifest(P§7.4) |

- S6:填表时刻的"工具"就是 generate 烘焙进表的下拉/批注/数据有效性;运行期工具为零是设计而非缺口(✗1)。
- S8:纯 Excel 工作面接受延迟反馈(pre-commit/CI 兜底)——即时校验只能靠 Excel 插件,已裁(✗1);此格是全矩阵唯一的"事后反馈"格,D5。
- S18:拆书降低相撞 + 认领约定(M3§8);不做工具锁(ADR-12),此格永远是"约定"。
- S17/S25:游戏列的工具面 = API + manifest 伴生 json(直接可读),库不提供游戏内 UI(✗2);ChangeSet 订阅、显式 Open 见 P§7.2/§7.8。
- G1 对应 S1/S3/S6/S10 的三阶段主线;这些格也保留 C/U 直达入口。S16 的 Runtime Open/切源复验不因 G1 调用 convert 而并入主线;C9 `diff` 仅供 VCS-only 接线,C8 线上补丁等特殊发布直接用子命令;CI/脚本始终不用 G1(非 TTY 拒绝,§3.1)。

## 3. CLI:Project 管理工具

`exceldb` 的身份是可从空目录启动的 Project 管理工具:默认入口 = 三阶段线性主引导(G1);子命令(C1-C11)= 命令行细节层——G1 每个领域操作都回显等价子命令,可进入细节但不把全部工作流收进主引导;CI/脚本只用子命令。

### 3.1 三阶段线性 Project 主引导(G1)

- 最简入口:把当前平台的 `exceldb` 单文件可执行程序放进空目录并运行。无 Project + TTY → G1 阶段 1;已有 Project + TTY → 从事实重算后续阶段;非 TTY 无子命令 → 打印用法,退出码 3。
- G1 固定顺序为 `Project 初始化 → 创建/调整 Excel 表结构并生成 C# → 填写 Excel 后准备并导出数据`。它不是场景菜单,也不保存私有进度;Project、proto、生成代码、xlsx、报告与 bytes/manifest 是唯一完成证据。
- G1 每个产品操作都回显并调用公开子命令(C1/C2/C3/C4/C5/C10/C7/C8),不存在界面专属写入。C1/C3-C6/C10 的交互入口只收集一次意图,展示不可变 MutationPlan 后确认并应用同一内存计划(§3.4);失败保留旧工件。
- `:` 可临时进入子命令细节,完成后返回当前阶段;`q` 退出。可执行文件本身不是 Project 工件或事实源,可以移动、替换或删除;Project 只依赖版本化文件与可重建缓存。
- 阶段 2 按工件事实恢复而不是盲重跑向导:无 proto 才进入 `table create`;proto 已存在但 descriptor/C# stale 时运行 `schema build`;xlsx 缺失或结构 stale 时运行 `generate`;`table edit` 只是阶段内可选、可重复的显式结构调整。
- 阶段 3 包含一次明确的人工作面交接:工具展示待填 xlsx,用户在 Excel 中生产数据并保存,返回后显式运行 `data prepare → check → convert`。工具不虚构业务值,`check`/`convert` 也不暗中补身份。
- Runtime Open、应用方写回/冲突、Play 切源、Git diff/PR、CI 与发布分发不进入 G1;它们继续使用各自 API/U/V/I 入口。

| 阶段 | 完成条件 | G1 回显的公开管线 |
| --- | --- | --- |
| 1. Project 初始化 | `ExcelDb.Project.json` 合法且配置所指默认目录存在;schema/workbook 可为空 | `init .`(内存计划 → 确认 → apply;已有合法 Project 校验/no-op或补目录) |
| 2. 创建/调整 Excel 表结构并生成 C# | 至少一张 live ASSET 表的 proto、C# 与 xlsx 结构一致,descriptor/cache 可从当前 proto 重建 | 无 proto → `table create`;C#/descriptor stale → `schema build`;xlsx missing/stale → `generate`;按需重复 `table edit`;必要时 `normalize` |
| 3. 填写 Excel 后准备并导出数据 | Excel 已保存,不存在 pending-new 行,check 通过,bytes + manifest 对应当前 schema/data fingerprints | 展示/打开 xlsx → 等待用户继续 → `data prepare` → `check` → `convert [--out]` |

- 已有 Project 可以跳过完成且未 stale 的阶段;跳过与恢复依据必须来自当前工件 fingerprints 和报告,不能靠口头选择或上次会话状态。
- “对应数据”在本文中特指 xlsx authoring 数据经 convert 得到的 bytes + manifest;生成的 `.cs` 是类型/访问器投影,不把业务记录固化为另一份 C# 数据源。

### 3.2 Project 配置(`ExcelDb.Project.json`,唯一配置源)

Project 配置进版本控制,程序拥有;它只描述内置 Schema Tooling、C# 输出、workbook、bytes 与 cache 的项目级路径。键集封闭,未知键 = 用法错误(退出码 3)。`init` 创建的最小配置如下:

```json
{
  "schemaDir": "Schema",
  "generatedDir": "Generated",
  "workbooks": ["Data/*.xlsx"],
  "bytesOutput": "Build/config.bytes",
  "cacheDir": ".exceldb"
}
```

| 键 | 语义 | 必需/缺省 | 主要消费方 |
| --- | --- | --- | --- |
| `schemaDir` | proto 事实源根与 project-local import root;文件发现、确定性排序与内嵌 options 解析归 M1 | 缺省 `Schema`;初始化后可暂时无 proto | C1-C10、G1 |
| `generatedDir` | M1 C# 类型/访问器投影目录 | 必需 | C2-C4、I2、宿主工程 |
| `workbooks` | xlsx 数据事实源与结构投影的 glob 集 | 必需;首表创建前可匹配零文件 | C3-C10、U1 |
| `bytesOutput` | runtime bytes 缺省输出;manifest 同行 | 必需 | C8、G1 阶段 3、构建钩子 |
| `cacheDir` | descriptor、快照、索引、MutationPlan 与默认报告根 | 缺省 `.exceldb` | C1-C10、M6 |

`init` 在新目标中创建 `ExcelDb.Project.json` 及五键所指的默认目录;不创建无表结构的空 xlsx。已有合法 Project 时它是幂等校验:已齐全则 no-op,缺少配置所指目录则计划补齐;只有非法 Project、应为目录却被文件占用、或待创建文件与不兼容现物碰撞时 blocker 2 且零覆盖。`table create <TableName>` 缺显式落点时固定写 `<schemaDir>/<TableName>.proto`;`--schema-dir` 只覆盖本次 schema 根,不产生 glob 型第二配置源。

canonical descriptor 固定派生到 `<cacheDir>/schema/descriptor.bin`,不是配置键或事实源。所有 schema consumer 都以当前 proto 编译结果为准:cache fingerprint 命中才可复用,缺失或 stale 时自动从当前 proto 在内存重建并刷新 cache。C2/C3/C4 可以更新 C#;C5/C6/C7/C8/C9/C10 绝不静默修改 C#,发现 `generatedDir` 与当前 proto/codegen fingerprint 不一致时返回 error 1 并明确要求先运行 C2。

可执行文件可以位于 Project 根,但不登记进配置;目录“空”允许只含当前可执行文件。初始化既有宿主仓库需显式指定目标并展示计划,不由空目录快捷路径猜测。

Git baseline、difftool、pre-commit 与 PR 投影不属于 Project 键域;它们由 V1-V3/I5 独立提供。运行时 source/hot-reload 选择也不存 Project 或 EditorPrefs,由 M7 bootstrap/API 每次显式传入。

### 3.3 命令全集(命令行细节层)

| # | 命令 | 语义(机制归属) | 专属参数 | 服务场景 |
| --- | --- | --- | --- | --- |
| C1 | `init [path]` | 幂等初始化/校验 Project 与默认目录;允许无 Project 执行 | MutationPlan(§3.4) | S1 |
| C2 | `schema build` | 内嵌 M1 工具链:当前 proto → lint → canonical descriptor/cache → C# codegen | `--check`(C# 只比较不替换;descriptor cache 仍可重建) | S3,S5,S22 |
| C3 | `table create [<TableName>]` | 建议/确认稳定身份;缺省写 `<schemaDir>/<TableName>.proto`,在同一计划复用 C2 编译与 C5 投影机制产 C#/xlsx | MutationPlan;`--workbook <path>` | S1,S10 |
| C4 | `table edit [<table>]` | 字段三阶段变更或表退役;保留数字身份,在同一计划复用 C2/C5 机制同步 C#/xlsx | MutationPlan | S3,S10 |
| C5 | `generate` | 把已有 schema 结构投影/修复到 workbook,不改变业务数据 | MutationPlan;`--purge` `--rekey` `--workbook <path>` | S3,S5,S10 |
| C6 | `normalize` | legacy cell 批量重写为 canonical(M1§2) | MutationPlan | S4 |
| C7 | `check` | 只读导入级全量校验;除可重建 cache/显式报告外不写 proto/C#/xlsx/bytes,pending-new 只报 identity error | — | S1,S3-S5,S8 |
| C8 | `convert` | 产与当前 schema/data fingerprints 对应的 bytes + manifest;存在 pending-new 即 blocker 2 | `--out`(缺省 `bytesOutput`) | S16,S22-S24 |
| C9 | `diff` | **VCS-only**:两 xlsx 只读对账,不进入 G1 | 位置参数 `<base> <target>` | S19-S21(V2/I5) |
| C10 | `data prepare` | 为 pending-new 行显式固化系统身份;只写系统 `__guid`,业务值逐 cell 不变 | MutationPlan;`--workbook <path>` | S6 |
| C11 | `fixture` | 隐藏子命令:测试 fixture 构建,非公开产品契约 | — | —(测试基建) |

- C1/C3/C4 是一等产品操作而非 G1 私有向导。TTY 中缺少结构输入时逐项询问;非 TTY 必须给出完整机器输入,否则用法错误 3。机器输入的最终形态由实现设计决定,不得新增第二 schema 事实源。
- C3 的 `TableName` 与 C4 的 `table` 在收集/生成计划时必需(交互可询问);仅 `<command> --apply-plan <path>` 从计划读取目标并省略位置参数。apply 时重新传位置参数属于改变意图,用法错误 3。
- C3/C4 的候选 proto、descriptor、C# 与 xlsx 先写临时区并整体校验;任一步失败均不替换旧工件。C2 只更新可重建 descriptor/cache 与 C#。数字身份分配、reserved/retired 与兼容语义归 M1/M8,G1 不另解释。
- C10 的计划绑定 schema hash、目标 workbook fingerprint 与每个 pending-new 行 fingerprint;apply 只为这些行写入计划内确定的 `__guid`。它不得规范化、补默认值或改写任一业务 cell。C7 只投 Diagnostic,C8 遇 pending-new 必须拒绝,二者都不得隐式调用 C10。

### 3.4 通用不可变 MutationPlan

C1/C3/C4/C5/C6/C10 的全部领域写入共享同一种 canonical `MutationPlan`;命令专属计划类型、二次确认对象或“确认后重新扫描再执行”均不存在。

- 计划至少包含 `operation`、ToolVersion、Project/config hash、当前 schema hash、全部 source fingerprints(含文件不存在状态)、目标前置 fingerprints、确定序 mutation 列表、诊断/风险及覆盖 canonical plan body(`planHash` 字段自身除外)的 `planHash`。fingerprint 采用内容与身份事实,不得只靠时间戳。
- 交互形态只收集一次意图 → 从当前事实生成一个内存计划 → 完整展示 → 用户确认 → 原计划 revalidate/apply。确认后不得重建“等价”计划;取消 = 0 写入。
- 非交互预览固定为 `<command> <intent...> --dry-run --plan <path>`:只把完整计划写到该路径、向 stdout 展示摘要后退出,不另写 OperationReport 或任何领域目标。`--dry-run` 与 `--plan` 必须同时出现且不得再带 `--json`,违反即用法错误 3。
- 执行固定为 `<command> --apply-plan <path>`:不得再接收会改变意图的 table/field/workbook/`--purge`/`--rekey` 参数。工具验证 `planHash` 后逐项复核全部 fingerprints;任一不匹配即 stale blocker 2、零领域写入,必须重新产计划。
- plan 文件是可丢弃的操作证据,不是事实源或 Project 配置。apply 成功后仍以 proto/xlsx 等正式工件为准;交互内存计划与序列化计划必须产生逐项等价的 mutation/report。
- 禁止把 `--dry-run` 后不带 plan 重跑同一意图命令当作 apply;该形态会重新观察输入,不具备用户已确认的对象身份。

### 3.5 公共参数与配置解析

- Project 选择:显式 `--project <path>` 优先;未指定时从 cwd 向上查找最近的 `ExcelDb.Project.json`。无 Project 时只允许 `init`、`--help`、`--version`;无子命令 + TTY 等价进入 `init` 引导,其余命令/非 TTY 返回用法错误 3。
- 字段解析:命令参数覆盖 > Project 字段 > §3.2 明定缺省。`--schema-dir <path>`/`--workbooks <glob>`/`--out` 分别覆盖 `schemaDir`/`workbooks`/`bytesOutput`;schema 输入只接受目录覆盖,不接受 glob override。除 §3.4 dry-run 计划外,`--json` 只决定本次报告路径,不写回 Project。缺必需字段、未知字段或非法组合 = 用法错误 3,不产领域报告。
- 路径基准:`--project` 与 `init [path]` 的相对路径按 cwd 解析;Project 选定后,其余相对配置、参数和位置路径均按 Project 目录解析;绝对路径不变。
- `schema build` 与所有消费 descriptor 的命令只使用随可执行文件内嵌的锁定工具链;不从 PATH、系统 SDK 或网络获取 protoc/parser/codegen。
- `--json <path>` 与退出码 0=ok/warning、1=error、2=blocker、3=用法/环境异常;stdout 仅为人读投影,机器消费使用 OperationReport json,MutationPlan 预览则只消费 §3.4 plan 文件。

### 3.6 交付形态

- 首要交付是按支持的 RID(OS/架构)分别发布的 self-contained single-file executable;不存在一个文件跨全部平台。用户复制对应文件即可离线完成 `init → table create/edit → data prepare → check → convert`,不要求预装 .NET Runtime/SDK、protoc、全局 tool 或联网安装。
- 锁定版 proto parser/compiler、SchemaCompiler、codegen、xlsx 与 convert 能力随该文件交付。实现可在系统临时目录或 `<cacheDir>/tool/` 解包内部资源,但必须校验完整性,且这种解包不改变“一个用户交付文件”的契约。
- `dotnet tool` 可以作为附加安装方式,不得成为主工作流前置;同版本下其命令、报告和字节产物必须与单文件形态等价。
- 宿主工程编译生成的 `.cs` 仍使用 Unity/.NET 自己的构建链,不属于 ExcelDB 数据生产前置。可执行文件和 UPM 包独立更新,版本经 OperationReport 记录并受 workbook format/schema_hash 门禁,不建立第二配置源。

## 4. Unity 编辑器套件

| # | 条目 | 规格 |
| --- | --- | --- |
| U1 | 启动挂载与空态 | 编辑器加载按 Project 的 workbooks glob 挂载(M2§3 宿主引导);Project 缺失 → 空态调用 C1 同管线初始化,不得只写一份 json |
| U2 | 菜单全集 | 顶层 `ExcelDB/`,见下表;菜单 = CLI/API 的 UI 投影,禁止菜单专属行为(M3§2) |
| U3 | Browser 窗口 | 三栏:树(workbook → 表)\| 行列表(列 = key、display_name、状态徽标 Dirty/Conflicted/Missing/Error)\| inspector(SO/SP 驱动,门面归 M6)。搜索框 = FindAssets 文法直通(M2§4)+ display_name 子串(UI 层扩展,不进 API 文法);工具栏 = dirty/冲突/error 计数(点击开 U12)、Save All、Refresh;右键:行 = M2§5 结构操作全集 + `Open in Excel`(OpenAsset,行定位尽力,M2 Δ10),workbook 节点 = Unmount / Open in Excel / 写入 json 持久化 |
| U4 | picker 与拖拽 | picker = FindAssets 驱动搜索窗,自动附 ref_table/ref_group 约束(M1§2);Browser 行拖拽到 RowRef 字段 = 赋值,与 picker 等价 |
| U5 | xlsx Inspector 摘要 | 选中已挂载 xlsx(DefaultAsset)时 Inspector 显示挂载状态、表与行数、schema_hash 对照、"打开 Browser"按钮 |
| U6 | 报告窗口与 Console 投影 | 会话报告环(缺省 100 条,不落盘);列表 = 操作 × Ok × 摘要,详情 = 诊断行(severity/code/定位/text);双击诊断 → Browser 定位行/字段,cell 级再经 Open in Excel 尽力跳转;"导出 json"= OperationReport 序列化。Console:error/blocker 每诊断一行(含定位串),warning 按操作汇总一行,info 不投影;详情恒在报告窗口 |
| U7 | 冲突对话框 | ConflictRecord 列表 + base/mine/theirs 三值预览(实施 §4.1 快照);逐条/批量 ReloadFromExcel / KeepEditorValue(M2§7);未全解 → 保存不可用 |
| U8 | dirty 拦截对话框 | 脚本编译/进 Play/退出编辑器/卸载 workbook 前:保存/放弃/取消;有未解冲突 → 仅 解决(转 U7)/取消(M3§6、M2§3);挂接点接线归 M7 |
| U9 | 陈旧检测对话框 | 进 Play(bytes 模式)manifest 内容 hash 不符:Convert 后进 / 直接进 / 取消(M3§7) |
| U10 | 计划预览对话框 | Unity 的 generate/normalize/data prepare 共用同一 MutationPlan 投影:显示 plan hash、source fingerprints 与确定序 mutation;存在 blocker → Apply 禁用;`--purge`/`--rekey` 显式复选,默认关。确认后 apply 原内存计划;table create/edit 的等价计划由 CLI/G1 直接显示 |
| U11 | Settings | Project Settings → ExcelDB(SettingsProvider):已有 `ExcelDb.Project.json` 的五键直接投影——编辑即写 json,无 EditorPrefs 副本;缺 Project 转 U1;附 schema_hash/ToolVersion 只读展示 |
| U12 | 待保存清单 | 写回计划的窗口投影(P§6.5-2):dirty 行 × 变更字段 × 旧/新 canonical 值,只读;双击跳 inspector;行级保存 = SaveAssetIfDirty 投影(M2§6),还原经 Undo;新建/删除/改名行以结构操作条目列出 |
| U13 | 临时 source picker / Play 工具栏 | Play 前选择“下一次会话”的 source 与 hot-reload,仅存于待进入 Play 的内存请求;adapter 在 bootstrap 时把 generated registry、所选 source 与 RuntimeBootstrapOptions 一次性传给 M7。Play 中显示当前源并显式 SwitchDataSource/Enable/DisableHotReload。取消/退出即丢弃,不得写 Project、EditorPrefs 或跨 session 恢复;模式拒绝原因可见 |

菜单全集(U2,是 M3 工作流的工具投影):

| 菜单项 | 行为 |
| --- | --- |
| `Browser` | 打开 U3 |
| `Refresh` | `AssetDatabase.Refresh()`(M2§3) |
| `Save All` | `SaveAssets()`;存在未解冲突 → 打开 U7(报告照常产出) |
| `Generate…` / `Normalize…` / `Data Prepare…` | U10 → apply 原计划 |
| `Convert` | convert + U6 |
| `Open Report` | U6 |
| `Mount Workbook…` | 文件选择 → 会话挂载;询问是否写入 json workbooks 列表(持久化) |
| `Settings…` | 跳转 U11 |

## 5. CI 配方

M3 门禁语义的流水线化(任一阶段退出码 ≥ 1 即失败);I1/I2/I4 只消费 Project 路径与内嵌 Schema Tooling,I5 的基线与投影由 Git/CI 自己提供;G1 不进 CI,流水线一律子命令。I3 的宿主测试可使用项目自己的 SDK,但不构成 ExcelDB schema/data 生产前置。

```text
I1 schema     exceldb schema build --json <cacheDir>/reports/schema.json(M1 lint+descriptor+codegen)
I2 freshness  重跑 I1 → git diff --exit-code <generatedDir>(M3§9)
I3 test       Core/Editor 测试(含 no-GC 门禁与基准断言,实施 §5.3/5.4)
I4 verify     schema build --check + check + convert;check 中 schema.drift 判 error(M3§9)
I5 pr-diff    VCS-only:每本变更 workbook由 CI 求 <merge-base> → exceldb diff --json
              → json 上传为工件 + markdown 投影发 PR 评论(投影脚本属 CI/samples 接线,不登记 Project;
                按 added/removed/renamed/modified 分组,modified 到字段路径与旧/新值)
I6 playmode   Unity TestProject(license 可用时;降级规则见实施 §6)
I7 工件       bytes + manifest + 全部 json 报告
```

## 6. 版本控制接线(全部为 VCS-only 配方 + samples 脚本,独立于 G1 与 Project 配置)

| # | 配方 | 内容 |
| --- | --- | --- |
| V1 | ignore/attributes | `.gitignore`:cacheDir(缺省 `.exceldb`)、bytes 输出、报告输出目录(M3§1 工件表);`.gitattributes`:`*.xlsx binary`(防 autocrlf 事故;二进制不可文本合并本就成立,M3§8) |
| V2 | git difftool | `[difftool "exceldb"] cmd = exceldb diff "$LOCAL" "$REMOTE"`;用法 `git difftool -t exceldb <rev> -- "*.xlsx"`(本地看差异与 WF6 对账,免手工 `git show`) |
| V3 | pre-commit | 可选,纯 Excel 工作面(M3§5"建议不是机制"):对暂存 xlsx 跑 `exceldb check`;脚本随 samples |

## 7. 场景命令样例

约定:7.1 从只含 `exceldb.exe` 的 `E:\Game` 开始;初始化后使用 §3.2 的唯一 Project 配置(`schemaDir = Schema`,`Generated`,`Data/*.xlsx`,`Build/config.bytes`,`.exceldb`)。7.1-7.5 是 Windows 本地示例,显式使用 `.\exceldb.exe`;7.6/7.7 分别运行在 Git/CI runner,允许 PATH 中的 `exceldb`。样例即规格:命令形态与注释断言进验收(§10 第 15 条)。

### 7.1 三阶段 Project 主引导会话(G1)

```text
PS E:\Game> .\exceldb.exe
ExcelDB Project 主引导  E:\Game
[1/3] Project 初始化
  未发现 ExcelDb.Project.json;当前目录除本程序外为空。
» .\exceldb.exe init .
  MutationPlan 7b2…:创建 Project + Schema/Data/Generated/Build/.exceldb
  初始化?[Y/n] y
  ✓ 已应用同一内存计划;Project 可解析,proto/workbook 当前为空
[2/3] 创建/调整 Excel 表结构并生成 C#
  事实恢复:未发现 proto → table create(不会盲重跑已有表向导)
  workbook: Data/game.xlsx | table: Hero | id: 1001(建议) | key: id
  fields: id:string, name:string, hp:int32
» .\exceldb.exe table create Hero --workbook Data/game.xlsx
  MutationPlan 19a…:Schema/Hero.proto + Generated/Hero.g.cs + Data/game.xlsx
  应用?[y/N] y
  ✓ 已应用同一内存计划;proto/C#/xlsx 首表基线完成
  调整表结构?[y/N] n  # table edit 是本阶段可选且可重复,不是独立阶段
[3/3] 填写 Excel 后准备并导出数据
  请填写并保存 Data/game.xlsx;完成后按 Enter 继续。
» .\exceldb.exe data prepare
  MutationPlan 42c…:2 个 pending-new 行各写一个系统 __guid;业务 cell 0 项
  应用?[y/N] y
» .\exceldb.exe check --json .exceldb/reports/check.json
» .\exceldb.exe convert --json .exceldb/reports/convert.json
  ✓ Build/config.bytes + 同行 manifest
完成。Runtime Open、应用方写回/冲突、Play、Git diff/PR、CI 与工件分发使用各自入口。
```

### 7.2 接入(S1,WF1)

```text
.\exceldb.exe init . --dry-run --plan init.plan.json         # 无 Project 合法;只写不可变计划
.\exceldb.exe init --apply-plan init.plan.json               # hash/fingerprints 匹配才提交
.\exceldb.exe init .                                         # TTY:已合法 no-op;缺目录仍展示内存计划后确认补齐
.\exceldb.exe table create Hero --workbook Data/game.xlsx --dry-run --plan .exceldb/plans/create-hero.json
.\exceldb.exe table create --apply-plan .exceldb/plans/create-hero.json # 只执行已确认计划
.\exceldb.exe check --json .exceldb/reports/check.json       # 首表基线绿;只报告不写

# Project/字段覆盖形态(显式参数只覆盖,不成为第二配置源)
.\exceldb.exe check --project client/ExcelDb.Project.json
.\exceldb.exe schema build --project tests/fixture/ExcelDb.Project.json --schema-dir Schema --check
.\exceldb.exe convert                                        # 无 Project 的目录执行 → 用法错误 3
```

### 7.3 结构演进与格式迁移(S3/S4/S10,WF2)

```text
.\exceldb.exe table edit Hero --dry-run --plan .exceldb/plans/edit-hero.json
.\exceldb.exe table edit --apply-plan .exceldb/plans/edit-hero.json # rename 保 number,退役写 reserved/retired
.\exceldb.exe check                                # 无 drift → proto/Generated/*.g.cs/Data/*.xlsx 同一变更集

.\exceldb.exe table create Item --workbook Data/game_dlc1.xlsx --dry-run --plan .exceldb/plans/create-item.json
.\exceldb.exe table create --apply-plan .exceldb/plans/create-item.json

# 按工件事实恢复:proto 已存在而 descriptor/C# stale 时只 build;绝不重跑 table create
.\exceldb.exe schema build                         # 内嵌 lint+descriptor/cache+codegen;无需 dotnet/protoc
# xlsx missing/stale 时只生成并应用绑定 fingerprints 的计划
.\exceldb.exe generate --dry-run --plan .exceldb/plans/generate.json
.\exceldb.exe generate --apply-plan .exceldb/plans/generate.json
.\exceldb.exe generate --purge --dry-run --plan .exceldb/plans/purge.json
.\exceldb.exe generate --apply-plan .exceldb/plans/purge.json
.\exceldb.exe generate --rekey --dry-run --plan .exceldb/plans/rekey.json
.\exceldb.exe generate --apply-plan .exceldb/plans/rekey.json

# S4 格式迁移三阶段(M1§2;每阶段一次 WF2 迭代)
.\exceldb.exe check --json .exceldb/reports/check.json # 阶段一发布后:报告含 legacy 命中计数
.\exceldb.exe normalize --dry-run --plan .exceldb/plans/normalize.json
.\exceldb.exe normalize --apply-plan .exceldb/plans/normalize.json
.\exceldb.exe check                                # 命中归零 → 下一次 schema 发布可安全删 legacy
```

### 7.4 纯 Excel 工作面(S8,WF3)

```text
# Windows 本地:保存后显式固化 pending-new 行身份;check 本身永不写 workbook
.\exceldb.exe data prepare --dry-run --plan .exceldb/plans/data-prepare.json
.\exceldb.exe data prepare --apply-plan .exceldb/plans/data-prepare.json
.\exceldb.exe check

# V3 pre-commit(samples 脚本;git-bash/WSL 同形)
for f in $(git diff --cached --name-only -- '*.xlsx'); do
  exceldb check --workbooks "$f" || exit 1       # 1=error 即拦截;0=ok/warning 放行(P§10)
done
```

### 7.5 调参收尾(S16,WF5)

```text
.\exceldb.exe convert                            # 与菜单 Convert 等价;pending-new → blocker 2
# 切回 bytes 复验走编辑器 U13;CLI 无运行时切源面(切源是运行时事务,P§7.6/§7.8)
```

### 7.6 VCS-only 协作对账(S19/S21,WF6;不进入 G1)

```text
# S19 提交前自查:difftool 配方(V2)
git difftool -t exceldb HEAD -- "Data/*.xlsx"
# 等价手工形(git show 提取 + 直接调用,M3§8)
git show HEAD:Data/game.xlsx > .exceldb/game.base.xlsx
exceldb diff .exceldb/game.base.xlsx Data/game.xlsx --json .exceldb/reports/game.diff.json

# S21 版本冲突对账重做(M3§8 剧本;示例:保留本侧,重做对侧)
git checkout --ours -- Data/game.xlsx
git show $(git merge-base HEAD MERGE_HEAD):Data/game.xlsx > .exceldb/base.xlsx
git show MERGE_HEAD:Data/game.xlsx > .exceldb/theirs.xlsx
exceldb diff .exceldb/base.xlsx .exceldb/theirs.xlsx --json .exceldb/reports/redo.json
#(在存活版重做后)
exceldb check --workbooks Data/game.xlsx                           # 绿 → git add + commit;可再 diff 一次自查收敛
```

### 7.7 CI 全序(S20/S22-S24,WF6/WF7)

```text
exceldb schema build --json .exceldb/reports/schema.json     # I1:内嵌 lint+descriptor+codegen
git diff --exit-code -- Generated                            # I2 freshness = Project.generatedDir
dotnet test tests/ExcelDb.Core.Tests   -c Release            # I3:宿主测试,不是 ExcelDB 工具前置
dotnet test tests/ExcelDb.Editor.Tests -c Release
exceldb schema build --check                                 # I4:当前 proto/descriptor/C# 一致
exceldb check   --json .exceldb/reports/check.json           #     drift 在 CI check 判 error(M3§9)
exceldb convert --json .exceldb/reports/convert.json         #     bytes + manifest 上传工件(I7)

# I5 pr-diff:对 PR 每本变更 workbook(S20 评审工件)
base=$(git merge-base origin/main HEAD)                      # 基线由 CI/Git 接线提供,不是 Project 键
git show $base:Data/game.xlsx > .exceldb/game.base.xlsx
exceldb diff .exceldb/game.base.xlsx Data/game.xlsx --json .exceldb/reports/game.diff.json
# *.diff.json → markdown 投影 → PR 评论(samples 脚本,非契约)

# S24 线上补丁窗口(纯调数,结构变更必须随包,M3§9;schema_hash 由 bytes 打开校验兜底,P§7.4)
exceldb convert --out Build/Patch/config.bytes --json .exceldb/reports/patch.json
```

## 8. 失败呈现面(M3§10 总表 × 首见者 × 位置)

| M3§10# | 失败 | 首见者 | 呈现面 |
| --- | --- | --- | --- |
| 1 | schema lint/兼容 blocker | 程序 | G1/U10 计划分级 / C2-C4 报告 |
| 2 | codegen/generate error | 程序 | G1/U10 失败阶段 / C2-C5 报告;proto/C#/xlsx 保旧 |
| 3 | 导入 error | 策划 | U6(Console error 行 + 报告窗口),save/convert 门禁 |
| 4 | schema.drift | 策划先 | U6 warning + 写回门禁;CI 侧 I4 error |
| 5 | 保存遇冲突 | 策划 | U7 |
| 6 | 保存遇文件锁 | 策划 | U6(重试耗尽 warning,dirty 保留) |
| 7 | 热载失败 | 策划 | U6 + Game 视图数据不变 |
| 8 | 切源被拒 | 策划/程序 | U13 禁用态 + U6 |
| 9 | workbook 格式过新 | 任意 | U6 / CLI 报告(只读打开) |
| 10 | xlsx 版本冲突 | 程序/策划 | git 冲突 → C9/V2/I5 对账 |
| 11 | convert error | CI/程序 | I4 退出码 + 报告工件 / U6 |
| 12 | 坏数据已入库 | 游戏 | 宿主日志(运行时 Diagnostic 投影);回退后编辑器侧 U6 |
| 13 | init 不兼容碰撞/写入/内嵌资源失败 | 程序 | G1/C1 计划与失败报告;已有合法 Project 不属碰撞,仅校验/no-op或补目录 |
| — | MutationPlan hash/fingerprint stale | 操作者 | C1/C3-C6/C10 `--apply-plan` → blocker 2 + stale 定位,全部领域目标零写入 |
| 14 | pending-new 行尚未固化身份 | 策划/程序 | C7 只报告;C8 blocker 2;转 C10 生成并确认身份计划 |

## 9. 不提供清单

| ✗ | 项 | 理由 |
| --- | --- | --- |
| 1 | Excel 插件/加载项(VSTO/Office.js) | 下拉/批注/数据有效性已烘焙进 workbook(P§5.1);插件引入安装分发与 Office 版本矩阵;S8 的延迟反馈是接受的代价(D5) |
| 2 | Development 构建内置调参面板 | 切源/热载/Refresh 的 API 已具备(P§7.2/§7.8),游戏内 UI 形态因项目而异,归宿主自建;库提供 API 与 manifest,不提供 UI |
| 3 | xlsx textconv / `dump` 子命令 | 单 `diff` 命令覆盖对账与评审(ADR-19);V2 配方复用之 |
| 4 | CLI watch 模式 | 文件监听是编辑器职责(P§6.8);CLI 是批处理面 |
| 5 | `doctor` 等环境自检命令 | 防御性基建;失败报告已含定位 |
| 6 | 报告持久化存储/历史库 | 报告 = 会话信息 + 流水线工件;历史 = CI 工件保留策略 |
| 7 | 菜单热键 | 避让 Unity 原生键位;团队自绑 |
| 8 | 角色/权限面板 | 工作面不是权限系统(M3§1) |
| 9 | G1 阶段自定义/插件化 | G1 是固定三阶段 Project 主引导,不是工作流注册表;其他场景走子命令、U/API/I/V 入口,不把主线扩成菜单 |

## 10. 模块验收测试

CLI 与配方可自动化;窗口/对话框为手测清单(实施 §3-M5 惯例):

1. 矩阵覆盖(文档评审断言):§2 每行至少一非空格、每格取值合法;§3-§6 每个工具条目被至少一格引用。
2. CLI 解析:`init`/help/version 无 Project 合法,其他命令缺 Project → 3;`--project` 显式选择 / cwd 向上最近 Project;`--project` 与 init target 相对 cwd、其余路径相对 Project;`--schema-dir`/`--workbooks`/`--out` 覆盖对应字段,convert 缺 `--out` 用 `bytesOutput`;未登记的 schema glob override 必须拒绝。
3. 退出码矩阵:0/1/2/3 各至少一例;`--json` 报告可反序列化为 OperationReport。
4. MutationPlan:C1/C3/C4/C5/C6/C10 的任一已生成计划 canonical 序列化稳定且 `planHash` 可复核;交互确认应用同一对象。`--dry-run --plan` 除计划外零写入;`--apply-plan` 成功逐项等价。分别篡改 plan body、Project、proto、workbook、目标不存在状态后 apply → stale blocker 2 且零领域写入;dry-run 不带 plan、apply-plan 混入意图参数均 → 3。
5. normalize:legacy 命中重写与计数进入计划;只允许 plan apply,命中归零后 check 通过(M1§7-14 复用)。
6. 入口等价:G1/U1 初始化与 C1 同计划/报告/落盘;G1 table create/edit 与 C3/C4 同管线;Generate/Data Prepare/Convert 的菜单与 CLI 对同一输入产等价报告。
7. difftool 配方:经 git difftool 调用与直接调用 C9 同报告;I5 markdown 投影分组与字段级旧/新值(golden)。
8. freshness/cache:删除或篡改 descriptor cache 后,C2-C10 从当前 proto 自动重建并得到同 schema hash;改 proto 使 C# stale 时 C5-C10 均 error 并指向 C2,且不改 C#;C2 重跑后 descriptor/C# 确定且 I2 git diff 归零。C3/C4 更新 C# 的事务路径另测。
9. 数据准备:含三条 pending-new 与一条既有行的 workbook 先经 C7,断言只报告且字节不变;C8 blocker 2。C10 plan 绑定 schema/workbook/行 fingerprints,apply 后只新增三枚计划内 `__guid`,全部业务 cell 逐字节等价;任一行外改后旧计划 stale。
10. S8 剧本:纯 Excel 提交坏数据或 pending-new → V3 拦截;绕过 hook → I4 红且 PR 评论可读定位。
11. U12:构造三行 dirty(改值/新建/删除)→ 清单条目与旧/新值;行级保存收窄写回计划(M2§6);Undo 后条目消失。
12. U13:Play 前 picker 的下一会话选择与 generated registry、RuntimeBootstrapOptions 一起只进入一次 M7 bootstrap;取消不生效。Play 中切源/热载成功与拒绝均可诊断并保留旧源;退出后 Project/EditorPrefs/后续 session 均无残留。
13. 手测清单——对话框:U7 三值预览、批量 Resolve、未全解禁保存;U8 四触发面 × 按钮集;U9 三选;U10 展示 plan hash/fingerprints,确认后 apply 原对象,blocker 禁 Apply,purge/rekey 默认关。
14. 手测清单——Browser 族:U3 搜索/徽标/右键/Open in Excel;U4 拖拽与 picker;U5 摘要;U6 定位与 Console;U11 五键单源、无 EditorPrefs;U1 缺 Project 必须调用 C1 而非只写 json。
15. 命令样例回放:§7 逐块在 fixture 仓库可执行,注释断言(空目录、不可变计划、legacy 计数、pending 身份、退出码、diff 四分类)成立;不存在“dry-run 后裸重跑同一意图命令”的样例;公开 C1-C10 与已登记 flag 封闭,C11 仅测试内部可见。
16. G1 主引导:目录仅含当前 exe 时无子命令 + TTY → 三阶段 `init → table create/[可选重复 table edit] → Excel 填数 → data prepare → check → convert`;非 TTY 无子命令 → 3。断言 Runtime Open、应用方写回、Play、diff/PR、CI 均不进入 G1。
17. init 幂等与阶段恢复:新目录初始化成功后再跑 C1 → no-op;逐个删除五键所指默认目录后重跑只补目录;目录位被文件占用或 Project 非法 → blocker 2 零覆盖。阶段 2 分别构造“无 proto”“proto 有而 C#/descriptor stale”“xlsx missing/stale”,断言只选择 C3、C2、C5,绝不盲重跑 C3/C4。
18. Project-only 配置:§3.2 恰为 `schemaDir/generatedDir/workbooks/bytesOutput/cacheDir`;schemaDir 缺省 `Schema`,`table create Hero` 缺省落 `Schema/Hero.proto`;`--schema-dir` 可覆盖,旧 schema glob 字段/参数及 assembly/build target/runtime/Git/script 键均拒绝。descriptor/report 从 cacheDir 推导,C9/I5 不读取 G1 进度。
19. 单文件离线:每个支持 RID 在无 .NET Runtime/SDK、无 PATH protoc、无网络的隔离机完成 init→首表/结构调整→data prepare→check→convert;给定同一输入和同一序列化 MutationPlan,单文件与可选 dotnet tool 的报告、C#、xlsx、bytes 逐字/逐字节等价。
20. 原子写入:分别在 C1/C3/C4/C5/C6/C10 的每个目标写入点注入失败;断言 init 不留下半 Project,其他命令不局部替换 proto/C#/xlsx/identity。C2 的 descriptor/cache+C# 替换也单独验证全成或全不成。

## 11. 与仓库现状衔接

- 本模块仅文档,无代码交付。本次为覆盖度重写:结构改为矩阵驱动,初版工具规格全部保留(编号化为 C/U/I/V 条目);新增 U12/U13、I5 的 markdown 投影、✗2,均为既有机制/API 的投影(P§6.5-2、P§7.2、P§9),不产生机制修订。
- 2026-07-11 增补 §7 场景命令样例(样例即规格,M2 D1 惯例延伸到命令面);原 §7-§12 顺移为 §8-§13。
- 2026-07-11 早期的选项菜单与双层配置决策已归档;D9 只保留“线性 Project 主线”和“diff 归 Git”的结论,阶段数由 D10 后续收敛为三阶段。
- 2026-07-11 D9:Project 成为唯一配置源;早期第二配置层和相关选择参数撤销,主引导改称 G1,第一阶段改为 Project 初始化。
- 2026-07-11 D10:产品入口改为可复制的 self-contained 单文件;G1 从空目录经三阶段闭环到 C#/xlsx/bytes,补 C1/C3/C4/C10,统一 MutationPlan,并把外部 schema build 依赖收回内嵌 C2。
- 权威合并后,CLI/Unity/CI/VCS 的工具契约只由本文拥有；归档计划中的命令、菜单和流水线不再参与解释。
- samples 增补(非契约):CI yaml 样例、pre-commit 与 difftool 配置脚本、diff json→markdown 投影脚本。
- poc 仅证明 schema 编译与 C#/Excel 双生成子集;不证明单文件分发、init/table 事务或完整 G1 已实现。

## 12. 跨模块边界与开放决策

- workbook 物理契约(下拉、批注、metadata sheet)的生成细节 → 模块 5(workbook 与身份)。
- `schemaDir` 下 proto source-set/import-root、内嵌编译确定性、descriptor cache fingerprint 与 C# 产物结构 → M1;M4 只拥有五键、命令与路径投影。
- table create/edit 的 proto/C#/xlsx 跨工件提交纪律与 MutationPlan 原子边界 → M3;字段兼容裁决归 M8,workbook 写入细节与 C10 pending identity patch 归 M5/M6。
- inspector 的 SO/SP 门面与 drawer 集、diff 的 `--json` 报告 schema、ImportReport 字段、U12 所需的旧/新值取数面(写回计划投影)→ 模块 6(导入与编辑)。
- 对话框挂接点(assembly reload / play mode 状态 / 退出)与 Play 接线、U13 与 RuntimeDatabase 的接线、OpenAsset 行定位实现 → 模块 7(运行时)/ Unity adapter 实施。

## 13. 决策记录

约定沿用 M1§10:引用块 = 评审原话;一句加粗 = 关键洞察;落点一行。

---

### D1(2026-07-11)模块 4 = 集成工具,工具只投影不发明

> workflow 设计集成工具, 依然 md.

**工具面的铁律是"只投影,不发明":四个集成面(CLI/Unity/CI/版本控制)全部是机制与工作流的投影,行为差异只能来自机制文档;UI 可以加糖(Browser 搜索扩 display_name),糖不回写 API 语义;第五面(Excel 插件)显式不存在——表内烘焙 + watcher 反馈环已覆盖其价值,不引入 Office 版本矩阵。**

落点:§1 总则与 §9 不提供表;编号再顺延(workbook 与身份/导入与编辑/运行时/兼容 → 模块 5/6/7/8)。

---

### D2(2026-07-11)配置单源:CLI 参数与 Settings 皆 json 投影

推演自发:CLI 逐命令传 `--schema-dir`/`--workbooks`,Unity 设置若另存 EditorPrefs,同一仓库就有三份工具配置。

**`ExcelDb.Project.json` 是工具配置的唯一事实源:CLI 参数是覆盖不是平行来源,Settings UI 是文件投影不是副本;解析顺序(显式 > json)必须是规格常量——否则两个入口对同一仓库跑出不同结果,入口等价(M3§2)从根上破产。**

落点:§3 公共参数与解析顺序(P§10 修订)、U11;convert `--out` 缺省 = json `bytesOutput`。

---

### D3(2026-07-11)命令族收口:命令与 owner 操作同构

推演自发:M1§2 定义了 normalize 操作,P§10 命令族却无登记——操作存在而入口缺席;反向诱惑同样存在(watch/dump/doctor)。

**命令全集必须与操作全集同构:机制已定义的操作必须登记入口;工具层发现缺口先回修 owner 文档,再投影为命令,不得由 M4 单方面发明领域语义。纯便利需求仍用配方组合既有命令(difftool 复用 diff);C1/C3/C4/C10 均是在相应 owner 契约收口后登记的一等操作。**

落点:§3 命令表(P§10 修订)、V2、§9 不提供表。

---

### D4(2026-07-11)版本控制接线 = 配方,不是机制

推演自发:difftool/pre-commit/gitattributes 有真实价值,但一旦成为机制就要背测试矩阵与跨平台兼容。

**git 集成全部交付为文档化配方 + samples 脚本:工具本体只保证 diff 的双位置参数形态可被 difftool 直接引用;配方坏了改配方,机制面零增长——与 M3 D3"不给二进制发明合并器"同一取向。**

落点:§6 全节;samples 增补(非契约,§11)。

---

### D5(2026-07-11)覆盖度契约 = 场景 × 使用者矩阵(重写本篇)

> 对工作流的覆盖度不足.
> 应该从场景 x 使用者的视角重新设计.

**按工具面组织只能保证"每个工具有规格",保证不了"每个时刻有工具"——覆盖度的度量单位是矩阵格子;把"交接/约定/烘焙/API/不提供"纳入合法值域,空格就成为非法状态,缺口全部被逼到明面:切源无入口(补 U13)、保存前看不见改动(补 U12)、评审者无人读投影(补 I5 markdown)、纯 Excel 工作面的延迟反馈(显式接受,✗1)、Development 构建调参(显式不提供 UI,✗2)。**

落点:§1 契约三条 + §2 矩阵 + §8 失败呈现面;验收增矩阵覆盖断言(§10-1);D1-D4 的工具规格与修订登记不变,仅编号化重排。

---

### D6(2026-07-11)命令样式 = 场景样例,样例即规格延伸到命令面

> 给出各种场景的命令样式

**把 M2"样例即规格"的惯例延伸到命令面:每个 CLI 参与的场景给出可回放的命令序列,命令形态与注释断言就是验收基线;样例集同时是全集封闭的证词——未出现的参数组合不存在,出现的每条都落在矩阵某格(含 git 配方与 CI 阶段的逐字形态)。**

落点:§7 全节;验收 §10 第 15 条(样例回放);衔接登记与小节顺移(§11)。

---

### D9(2026-07-11)删除 Workspace,Project 是唯一配置源

> 建议将工作空间去除, 仅保留 Project 吗
>
> 好的, 推进.

**Workspace 没有独立事实、跨 Project 聚合或编排能力,只是在 Project 外形成第二配置源;删除 `ExcelDb.Workspace.json`、W2 与 `--workspace`,把项目路径输入收回唯一 `ExcelDb.Project.json`。线性主线保留并改称 G1 Project 引导,后续收敛为三阶段;diff 继续只归 Git/VCS。**

落点:总纲 §3、M3§1、本文 §1-§3/§5-§7/§9-§11;被撤销的双层配置/选项菜单决策移入归档,线性主线与 diff 边界保留。

---

### D10(2026-07-11)单文件可执行程序从空目录完成首次数据闭环

> 预期 会有一个可执行文件, 当我爸这个可执行文件粘贴到一个空文件夹时, 该文件夹会引导我将这个空文件夹初始化为一个项目.
> 该可执行文件还将引导我:
> 从0创建一个excel.
> 修改表结构, 并生产cs数据文件,
> 生产cs文件的对应数据,
>
> 推进, 对架构进行修正.

**可执行程序不是已有 schema 工程外面的薄壳,而是离线 Project 自举器:每个平台一个 self-contained 单文件,内嵌 schema 编译/codegen/xlsx/convert;G1 固定为“幂等 init → 创建/可选重复调整表结构并生成 C# → Excel 填数后显式 data prepare/check/convert”三阶段。所有领域写入确认的是带 hash/fingerprints 的同一不可变 MutationPlan,恢复只看当前工件事实;proto 仍是结构事实源,C# 是类型投影,data prepare 只固化 pending 行身份。**

落点:§2 S1/S3/S6/S10、§3 全节、§5、§7.1-7.4/7.7、§10 第 16-20 条;D3 的“不得从工具侧长新命令”仅保留“先回修机制再投影”的原则,缺失的一等操作由 owner 修订后登记为 C1/C3/C4/C10。
