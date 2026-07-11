# 模块 4:集成工具(场景 × 使用者)

状态:本文是逐模块重推演的第 4 篇,把 M3 的工作流物化为可交付的工具面。2026-07-11 重写:初版按工具面(CLI/Unity/CI/版本控制)组织,评审判定对工作流覆盖不足——按面组织只能保证"每个工具有规格",保证不了"每个工作流时刻有工具";现以场景 × 使用者矩阵(§2)为覆盖度契约重新推导,矩阵逼出的新工具条目见 D5,初版已定的工具规格与修订登记沿用。同日再迭代(D7):CLI 升格为工作空间管理工具——默认入口为选项式门面(W1)+ 工作空间配置 `ExcelDb.Workspace.json`(W2),子命令保留为命令行细节层。总则不变:工具只做机制的投影,不发明行为;窗口手感对齐 Unity 编辑器惯例,像素级 API 契约以 M2 为准。模块编号顺延沿用:待定模块 = 5(workbook 与身份)/6(导入与编辑)/7(运行时)/8(兼容)。后续模块引用本文记作 M4§x。

## 1. 方法与覆盖度契约

- 使用者 = M3§1 四角色(策划/程序/CI/游戏)+ 评审者(WF6 特设视角,由程序或策划兼任);游戏列 = 运行时消费方(含 Development 构建操作者)。
- 场景 = M3 WF1-WF7 的时刻级展开(§2 行全集)。覆盖度契约:
  1. 每个场景行至少一个非空格;每格取值必须落在图例值域内——"想不出填什么"= 设计缺陷,不许留白混过。
  2. 工具条目(§3-§6)必须被至少一格引用;无格引用的工具 = 幽灵工具,删除(唯一豁免:C7 隐藏测试基建,不属团队工具面)。
  3. 新场景先加行、再配工具;新工具先找格、再写规格。
- 格值域(图例):`C/U/I/V/W+编号` = §3-§6 工具条目(W = §3 工作空间门面/配置);`烘焙` = generate 写进 workbook 的产物(下拉/批注/数据有效性,P§5.1),使用时无运行工具;`P§x 钩子` = 自动机制承担,无人工工具(如构建钩子);`API` = 库 API 直接消费,无工具面(游戏侧 UI 归宿主);`→WFn` = 角色交接,不是工具;`约定` = 团队纪律(M3);`✗n` = 显式不提供(§9 行 n);空 = 该使用者不参与。
- 投影原则:工具的每个动作必须映射到机制文档已有的操作与 API,同一操作跨入口走同一管线、产同一 OperationReport(M3§2);门面选项 = 场景的命令序列(§7),执行前回显等价子命令,自身零操作语义(§3.1);UI 可以加糖(如 Browser 搜索扩展),糖不回写 API 语义。
- 失败形态沿用 M2§1:失败返回 + Diagnostic,无静默 no-op;对话框"取消"= 操作中止、零写入。失败的呈现位置按 §8 映射。

## 2. 场景 × 使用者矩阵

| # | 场景(WF) | 策划 | 程序 | 评审者 | CI | 游戏 |
| --- | --- | --- | --- | --- | --- | --- |
| S1 | 建 proto/project/workspace 配置,配 ignore/attributes,首次 generate(WF1) | | W1,W2,C2,V1 | | | |
| S2 | Unity 首开:挂载、空态引导、workbook 状态一览(WF1) | U1,U5 | U1,U5,U11 | | | |
| S3 | 改 proto → 验证 → 应用(WF2) | | C1,C2/U10,C4 | | I1-I4 | |
| S4 | 格式迁移三阶段(WF2) | | C3,U10,C4 | | I4 | |
| S5 | 拉取他人 schema 变更后同步(WF2) | U1,U6 | C4,U6 | | | |
| S6 | Excel 日常填数(WF3) | 烘焙 | | | | |
| S7 | 保存看结果,Unity 开着(WF3) | U3,U6 | | | | |
| S8 | 保存无反馈,纯 Excel 工作面(WF3) | V3,I5 | | | I4 | |
| S9 | 高危编辑:改 key/删行/复制行(WF3) | U3,U6 | | | | |
| S10 | 需要新列/新表(WF3) | →WF2 | C2,U10 | | | |
| S11 | 结构化编辑与保存(WF4) | U2,U3,U4,U12 | 同左 + M2 API | | | |
| S12 | 外改撞 dirty,解冲突(WF4) | U7 | U7 | | | |
| S13 | 危险时机拦截:编译/进 Play/退出/卸载(WF4) | U8 | U8 | | | |
| S14 | 进 Play 选源与陈旧检测(WF5) | U9,U11 | U9,U11 | | | |
| S15 | 运行中改 Excel 热载(WF5) | U13 | U13 | | | API |
| S16 | convert 后切 bytes 复验(WF5) | U2,U13 | U2,U13 | | | |
| S17 | Development 构建现场调数(WF5) | | | | | ✗2,API |
| S18 | 同书并行写的预防(WF6) | 约定 | 约定 | | | |
| S19 | 提交前自查改动(WF6) | U12,V2 | C6,V2 | | | |
| S20 | PR 评审 xlsx 变更(WF6) | | | I5 | I5 | |
| S21 | 版本冲突对账重做(WF6) | U3 + I5/C6 报告 | C6,V2 | | | |
| S22 | 门禁与工件(WF7) | | | | I1-I7 | |
| S23 | 构建出包(WF7) | | U2 + 构建钩子(P§11) | | I4 | |
| S24 | 线上数据补丁(WF7) | | C5 | | I4 | API |
| S25 | 运行时数据排查:来源与版本(WF7) | | | | | API + manifest(P§7.4) |

- S6:填表时刻的"工具"就是 generate 烘焙进表的下拉/批注/数据有效性;运行期工具为零是设计而非缺口(✗1)。
- S8:纯 Excel 工作面接受延迟反馈(pre-commit/CI 兜底)——即时校验只能靠 Excel 插件,已裁(✗1);此格是全矩阵唯一的"事后反馈"格,D5。
- S18:拆书降低相撞 + 认领约定(M3§8);不做工具锁(ADR-12),此格永远是"约定"。
- S17/S25:游戏列的工具面 = API + manifest 伴生 json(直接可读),库不提供游戏内 UI(✗2);ChangeSet 订阅、显式 Open 见 P§7.2/§7.8。
- 含 C 系条目的格:人工执行默认经 W1 门面(选项 = §7 序列,回显可直通细节);CI/脚本直接用子命令(W1 拒非 TTY,§3.1)。

## 3. CLI:工作空间管理工具

`exceldb` 的身份是工作空间管理工具:默认入口 = 选项式门面(W1,场景菜单);子命令(C1-C7)= 命令行细节层——门面每一步都回显等价子命令,可随时直通;CI/脚本只用子命令。

### 3.1 选项式门面(W1)

- 触发:`exceldb` 无子命令 + TTY → 门面;非 TTY 无子命令 → 打印用法,退出码 3(CI 永不误入交互)。
- 选项 = 场景:菜单全集见下表(封闭;新增选项必须先过 §2 矩阵,✗9);选项执行 = §7 对应序列逐条运行,运行前以 `»` 回显等价子命令;危险门(apply、`--purge`/`--rekey`、配方写入)逐项确认,默认否;报告以人读投影入会话,json 落 `outDir`。
- `:` = 会话内直通子命令(与独立进程运行逐字等价);`q` 退出;会话退出码 = 最后一次操作的退出码(无操作 = 0)。
- 状态头 = 工作空间配置概要 + `outDir` 内最近报告摘要;门面无自有状态存储(投影原则,§1)。

| 选项 | 场景 | 序列(回显层) |
| --- | --- | --- |
| `1` 检查 | S3/S5/S8 | `check`(§7.3) |
| `2` 结构演进 | S3/S10 | 构建 `schemaBuild` → `generate --dry-run` → 确认 apply → `check`(§7.3) |
| `3` 格式迁移 | S4 | `check` → `normalize --dry-run` → 确认 apply → `check`(§7.3) |
| `4` 出包 | S16/S22-S24 | `convert [--out]`(§7.5/§7.7) |
| `5` 对账 | S19/S21 | `git show`(`baseBranch`/指定 rev)提取 → `diff`(§7.6) |
| `6` 新建 workbook | S1 | `generate --workbooks <path>`(§7.2) |
| `7` 环境配方 | S8/S19 | V1-V3 与 CI 样例安装:回显写入内容 → 逐项确认(§6) |

### 3.2 工作空间配置(W2,`ExcelDb.Workspace.json`)

编排配置,进版本控制(M3§1 工件表增行),程序拥有;与 `ExcelDb.Project.json`(数据管线,P§3)键域不相交——workspace 内出现 project 键 → 用法错误(退出码 3);文件缺失 → 门面以引导模式起步(逐键询问,写入前回显),子命令与 Unity 不受影响。

```json
{ "project": "ExcelDb.Project.json",
  "schemaBuild": "samples/Game.Configs",
  "generatedDir": "samples/Game.Configs/Generated",
  "outDir": "out",
  "baseBranch": "origin/main",
  "scripts": { "diffMarkdown": "samples/scripts/diff-markdown.csx",
               "preCommit": "samples/scripts/pre-commit" } }
```

| 键 | 语义 | 消费方 |
| --- | --- | --- |
| `project` | 数据管线配置指针(P§3,键集不变) | 门面与子命令(经 §3.4 解析链) |
| `schemaBuild` | schema 工程构建目标(`dotnet build <值>`) | 门面选项 2;I1 前置 |
| `generatedDir` | 生成代码目录 | I2 freshness;门面选项 2 |
| `outDir` | 报告与提取物缺省目录 | 门面各序列 `--json` 落点 |
| `baseBranch` | 对账/评审缺省基线 ref | 门面选项 5;I5 |
| `scripts` | samples 脚本登记点(脚本本身非契约) | 选项 7 安装;I5 投影 |

### 3.3 命令全集(命令行细节层;P§10 修订:补登记 `normalize`,`diff` 系 M3§8 已增)

| # | 命令 | 语义(机制归属) | 专属参数 | 服务场景 |
| --- | --- | --- | --- | --- |
| C1 | `lint` | schema 规则 + workbook 结构检查(M1§5、P§10) | — | S3 |
| C2 | `generate` | 结构生成三段式(P§6.6、M3§4) | `--dry-run` `--purge` `--rekey` `--workbook <path>`(新表落点) | S1,S3,S10 |
| C3 | `normalize` | 格式迁移:legacy 命中 cell 批量重写为 canonical(M1§2;generate 家族,同三段式) | `--dry-run` | S4 |
| C4 | `check` | 导入级全量校验(P§10) | — | S3-S5,S8 |
| C5 | `convert` | 产 bytes + manifest(P§7.4) | `--out`(缺省 = json `bytesOutput`) | S16,S22-S24 |
| C6 | `diff` | 两 xlsx 只读对账(M3§8) | 位置参数 `<base> <target>` | S19-S21 |
| C7 | `fixture` | 隐藏子命令:测试 fixture 构建(实施文档 §5.1),非公开契约 | — | —(测试基建) |

### 3.4 公共参数与配置解析(P§10 修订)

- 解析链(D2 经 D7 修订,规格常量)= 显式参数 > `--workspace <path>`(缺省 `./ExcelDb.Workspace.json`;子命令只消费其 `project` 指针,编排键归门面与配方)> `--project <path>`(缺省 `./ExcelDb.Project.json`,供给 schemaAssembly、workbooks、bytesOutput、cacheDir,P§3)> 内置缺省;`--schema`/`--workbooks` 为逐项覆盖。缺必需输入 = 用法错误 → 退出码 3 + stderr 用法说明,不产报告。
- `--json <path>` 与退出码 0=ok/warning 1=error 2=blocker 3=异常,不变(P§10)。
- 输出契约:stdout = 人读投影(操作摘要 + 诊断行 `severity code 定位 text`),不承诺格式稳定;机器消费一律 `--json`(OperationReport,P§9)。

### 3.5 交付形态

`exceldb` 以 dotnet tool 发布;仓库内 `dotnet run --project src/ExcelDb.Cli --` 等价(实施文档 §6)。tool 与 UPM 包同版本发布;版本核对经报告 ToolVersion(P§4.5,记录不进 hash)与 workbook format 门禁(P§5.2),不另建版本协商机制。

## 4. Unity 编辑器套件

| # | 条目 | 规格 |
| --- | --- | --- |
| U1 | 启动挂载与空态 | 编辑器加载按 project json 的 workbooks glob 挂载(M2§3 宿主引导);json 缺失 → Browser/Settings 空态引导创建 json |
| U2 | 菜单全集 | 顶层 `ExcelDB/`,见下表;菜单 = CLI/API 的 UI 投影,禁止菜单专属行为(M3§2) |
| U3 | Browser 窗口 | 三栏:树(workbook → 表)\| 行列表(列 = key、display_name、状态徽标 Dirty/Conflicted/Missing/Error)\| inspector(SO/SP 驱动,门面归模块 6)。搜索框 = FindAssets 文法直通(M2§4)+ display_name 子串(UI 层扩展,不进 API 文法);工具栏 = dirty/冲突/error 计数(点击开 U12)、Save All、Refresh;右键:行 = M2§5 结构操作全集 + `Open in Excel`(OpenAsset,行定位尽力,M2 Δ10),workbook 节点 = Unmount / Open in Excel / 写入 json 持久化 |
| U4 | picker 与拖拽 | picker = FindAssets 驱动搜索窗,自动附 ref_table/ref_group 约束(M1§2);Browser 行拖拽到 RowRef 字段 = 赋值,与 picker 等价 |
| U5 | xlsx Inspector 摘要 | 选中已挂载 xlsx(DefaultAsset)时 Inspector 显示挂载状态、表与行数、schema_hash 对照、"打开 Browser"按钮 |
| U6 | 报告窗口与 Console 投影 | 会话报告环(缺省 100 条,不落盘);列表 = 操作 × Ok × 摘要,详情 = 诊断行(severity/code/定位/text);双击诊断 → Browser 定位行/字段,cell 级再经 Open in Excel 尽力跳转;"导出 json"= OperationReport 序列化。Console:error/blocker 每诊断一行(含定位串),warning 按操作汇总一行,info 不投影;详情恒在报告窗口 |
| U7 | 冲突对话框 | ConflictRecord 列表 + base/mine/theirs 三值预览(实施 §4.1 快照);逐条/批量 ReloadFromExcel / KeepEditorValue(M2§7);未全解 → 保存不可用 |
| U8 | dirty 拦截对话框 | 脚本编译/进 Play/退出编辑器/卸载 workbook 前:保存/放弃/取消;有未解冲突 → 仅 解决(转 U7)/取消(M3§6、M2§3);挂接点接线归模块 7 |
| U9 | 陈旧检测对话框 | 进 Play(bytes 模式)manifest 内容 hash 不符:Convert 后进 / 直接进 / 取消(M3§7) |
| U10 | 计划预览对话框 | generate/normalize 共用:计划项按分级分组着色;存在 blocker → Apply 禁用;`--purge`/`--rekey` 对应显式复选,默认关(P§6.6) |
| U11 | Settings | Project Settings → ExcelDB(SettingsProvider):`ExcelDb.Project.json` 的直接投影——编辑即写 json,无 EditorPrefs 副本;附 schema_hash/ToolVersion 只读展示与"打开 json"按钮 |
| U12 | 待保存清单 | 写回计划的窗口投影(P§6.5-2):dirty 行 × 变更字段 × 旧/新 canonical 值,只读;双击跳 inspector;行级保存 = SaveAssetIfDirty 投影(M2§6),还原经 Undo;新建/删除/改名行以结构操作条目列出 |
| U13 | Play 源工具栏 | Browser 工具栏 Play 态扩展:当前源指示、切源(SwitchDataSource 投影:Excel 源 ⇄ 最近 convert 的 bytes)、热载开关(Enable/DisableHotReload);全部受 P§7.8 模式门禁,禁用态显示原因 |

菜单全集(U2,与实施文档 §8.5 对齐):

| 菜单项 | 行为 |
| --- | --- |
| `Browser` | 打开 U3 |
| `Refresh` | `AssetDatabase.Refresh()`(M2§3) |
| `Save All` | `SaveAssets()`;存在未解冲突 → 打开 U7(报告照常产出) |
| `Generate…` / `Normalize…` | U10 → apply |
| `Convert` | convert + U6 |
| `Open Report` | U6 |
| `Mount Workbook…` | 文件选择 → 会话挂载;询问是否写入 json workbooks 列表(持久化) |
| `Settings…` | 跳转 U11 |

## 5. CI 配方

实施文档 §6 命令序列的流水线化(门禁:任一阶段退出码 ≥ 1 即失败);各阶段 = 命令 + 退出码,占位符取自 W2 工作空间配置;门面(W1)不进 CI——非 TTY 拒绝,流水线一律子命令。供应商(GitHub Actions/Jenkins/GitLab)映射自明,yaml 样例随 samples(非契约):

```text
I1 build      dotnet build <schemaBuild> -c Release(schema 工程编译即 lint 门禁,M1§5)
I2 freshness  重跑 schema 编译管线 → git diff --exit-code <generatedDir>(M3§9)
I3 test       Core/Editor 测试(含 no-GC 门禁与基准断言,实施 §5.3/5.4)
I4 verify     lint + check + convert 三连,各 --json;check 中 schema.drift 判 error(M3§9)
I5 pr-diff    每本变更 workbook:git show <merge-base(baseBranch)>:<path> → exceldb diff --json
              → json 上传为工件 + markdown 投影发 PR 评论(脚本经 W2 scripts.diffMarkdown 登记,
                随 samples,非契约;按 added/removed/renamed/modified 分组,modified 到字段路径与旧/新值)
I6 playmode   Unity TestProject(license 可用时;降级规则见实施 §6)
I7 工件       bytes + manifest + 全部 json 报告
```

## 6. 版本控制接线(全部为配方 + samples 脚本,非机制)

| # | 配方 | 内容 |
| --- | --- | --- |
| V1 | ignore/attributes | `.gitignore`:cacheDir(缺省 `.exceldb`)、bytes 输出、报告输出目录(M3§1 工件表);`.gitattributes`:`*.xlsx binary`(防 autocrlf 事故;二进制不可文本合并本就成立,M3§8) |
| V2 | git difftool | `[difftool "exceldb"] cmd = exceldb diff "$LOCAL" "$REMOTE"`;用法 `git difftool -t exceldb <rev> -- "*.xlsx"`(本地看差异与 WF6 对账,免手工 `git show`) |
| V3 | pre-commit | 可选,纯 Excel 工作面(M3§5"建议不是机制"):对暂存 xlsx 跑 `exceldb check`;脚本随 samples |

## 7. 场景命令样例

约定:仓库根有 `ExcelDb.Workspace.json`(W2,§3.2 样例值)与 `ExcelDb.Project.json`(P§3 样例值:workbooks = `Assets/Configs/*.xlsx`,bytesOutput = `ConfigData/config.bytes`);`exceldb` 为 dotnet tool 形态,`dotnet run --project src/ExcelDb.Cli --` 逐字等价(§3.5);报告一律可加 `--json`,样例仅在断言处标注。人工会话默认经门面(7.1),7.2 起各块即门面的回显层,亦可直接键入。样例即规格(M2 D1 惯例):命令形态、参数组合与注释断言进验收(§10 第 13 条);本节未出现的参数组合不存在(全集封闭,§1)。

### 7.1 门面会话(W1,任意人工场景的默认入口)

```text
$ exceldb                                    # 无子命令 + TTY → 门面;非 TTY → 用法,退出码 3
ExcelDB 工作空间  E:\Game
project: ExcelDb.Project.json | workbooks: Assets/Configs/*.xlsx(2 本)
schema: samples/Game.Configs | base: origin/main | 最近: check 绿 10:41(out/check.json)
 [1] 检查  [2] 结构演进  [3] 格式迁移  [4] 出包  [5] 对账  [6] 新建 workbook  [7] 环境配方  [:] 命令行  [q] 退出
> 2
» dotnet build samples/Game.Configs -c Release           # » = 回显即将执行的等价命令(命令行细节)
» exceldb generate --dry-run --json out/plan.json
  计划:safe 3 / warning 1 / error 0 / blocker 0(d = 看明细)
  应用?[y/N] y                                           # 危险门逐项确认,默认否(--purge/--rekey 同)
» exceldb generate --json out/generate.json
» exceldb check --json out/check.json
  绿 → proto + 生成代码 + xlsx 同一变更集提交(M3§4)
> :check --workbooks Assets/Configs/game.xlsx            # ":" = 直通子命令,与独立运行逐字等价
> q                                                      # 会话退出码 = 最后一次操作退出码
```

### 7.2 接入(S1,WF1)

```text
exceldb generate --workbooks Assets/Configs/game.xlsx       # 路径不存在 → 计划项"新建 workbook"(safe,M3§4);glob 不触发首建
exceldb generate --workbooks Assets/Configs/game_dlc1.xlsx
exceldb convert                                             # --out 缺省 = json bytesOutput(D2)
exceldb check --json out/check.json                         # 基线绿(退出码 0)→ proto/json/生成代码/xlsx 入库

# 配置覆盖形态(D2 解析顺序:显式 > json)
exceldb check --project client/ExcelDb.Project.json                                     # 非 cwd 的 json
exceldb lint  --schema out/Game.Configs.dll --workbooks "tests/fixtures/golden/*.xlsx"  # 逐项覆盖(fixtures 场景)
exceldb convert --schema out/Game.Configs.dll               # 无 json 的目录下缺 workbooks → 用法错误,退出码 3,不产报告
```

### 7.3 结构演进与格式迁移(S3/S4/S10,WF2)

```text
dotnet build samples/Game.Configs -c Release     # lint 内嵌于 schema 构建(XDB blocker 即失败,M1§5)+ codegen
exceldb generate --dry-run --json out/plan.json  # 审计划:P§6.6 分级清单,零写入
exceldb generate                                 # apply;error 项 → 坏 cell 清单 + 该项零写入(P§6.6)
exceldb check                                    # 无 drift(退出码 0)→ proto/生成代码/xlsx 原子交付(M3§4)

exceldb generate --workbook Assets/Configs/game_dlc1.xlsx   # S10 新表落点;多书含新表且缺此参数 → blocker(退出码 2)
exceldb generate --dry-run --purge               # 显式二次运行族:物理删 #removed:* 列,先看影响面
exceldb generate --rekey                         # key 字段结构变化的显式确认(P§6.6)

# S4 格式迁移三阶段(M1§2;每阶段一次 WF2 迭代)
exceldb check --json out/check.json              # 阶段一发布后:报告含 legacy 命中计数
exceldb normalize --dry-run                      # 迁移计划:命中 cell 清单,零写入
exceldb normalize                                # 批量重写为 canonical(plan → report → apply)
exceldb check                                    # 命中归零 → 下一次 schema 发布可安全删 legacy
```

### 7.4 纯 Excel 工作面(S8,WF3)

```text
# V3 pre-commit(samples 脚本;git-bash/WSL 同形)
for f in $(git diff --cached --name-only -- '*.xlsx'); do
  exceldb check --workbooks "$f" || exit 1       # 1=error 即拦截;0=ok/warning 放行(P§10)
done
```

### 7.5 调参收尾(S16,WF5)

```text
exceldb convert                                  # 与菜单 Convert 等价(M3§2 入口等价)
# 切回 bytes 复验走编辑器 U13;CLI 无运行时切源面(切源是运行时事务,P§7.6/§7.8)
```

### 7.6 协作对账(S19/S21,WF6)

```text
# S19 提交前自查:difftool 配方(V2)
git difftool -t exceldb HEAD -- "Assets/Configs/*.xlsx"
# 等价手工形(git show 提取 + 直接调用,M3§8)
git show HEAD:Assets/Configs/game.xlsx > out/game.base.xlsx
exceldb diff out/game.base.xlsx Assets/Configs/game.xlsx --json out/game.diff.json

# S21 版本冲突对账重做(M3§8 剧本;示例:保留本侧,重做对侧)
git checkout --ours -- Assets/Configs/game.xlsx
git show $(git merge-base HEAD MERGE_HEAD):Assets/Configs/game.xlsx > out/base.xlsx
git show MERGE_HEAD:Assets/Configs/game.xlsx > out/theirs.xlsx
exceldb diff out/base.xlsx out/theirs.xlsx --json out/redo.json    # 被弃改动 = 重做清单(added/removed/renamed/modified)
#(在存活版重做后)
exceldb check --workbooks Assets/Configs/game.xlsx                 # 绿 → git add + commit;可再 diff 一次自查收敛
```

### 7.7 CI 全序(S20/S22-S24,WF6/WF7)

```text
dotnet build ExcelDb.sln -c Release                          # I1
git diff --exit-code -- samples/Game.Configs/Generated       # I2 freshness(目录 = W2 generatedDir;前置:重跑 schema 构建)
dotnet test tests/ExcelDb.Core.Tests   -c Release            # I3
dotnet test tests/ExcelDb.Editor.Tests -c Release
exceldb lint    --json out/lint.json                         # I4:任一退出码 ≥ 1 即门禁失败
exceldb check   --json out/check.json                        #     drift 在 CI check 判 error(M3§9)
exceldb convert --json out/convert.json                      #     产物 + manifest 上传工件(I7)

# I5 pr-diff:对 PR 每本变更 workbook(S20 评审工件)
base=$(git merge-base origin/main HEAD)                      # origin/main = W2 baseBranch
git show $base:Assets/Configs/game.xlsx > out/game.base.xlsx
exceldb diff out/game.base.xlsx Assets/Configs/game.xlsx --json out/game.diff.json
# out/*.diff.json → markdown 投影 → PR 评论(samples 脚本,非契约)

# S24 线上补丁窗口(纯调数,结构变更必须随包,M3§9;schema_hash 由 bytes 打开校验兜底,P§7.4)
exceldb convert --out Build/Patch/config.bytes --json out/patch.json
```

## 8. 失败呈现面(M3§10 总表 × 首见者 × 位置)

| M3§10# | 失败 | 首见者 | 呈现面 |
| --- | --- | --- | --- |
| 1 | lint blocker | 程序 | 构建输出(IDE/I1) |
| 2 | generate error/blocker | 程序 | U10 计划分级 / C2 报告 |
| 3 | 导入 error | 策划 | U6(Console error 行 + 报告窗口),save/convert 门禁 |
| 4 | schema.drift | 策划先 | U6 warning + 写回门禁;CI 侧 I4 error |
| 5 | 保存遇冲突 | 策划 | U7 |
| 6 | 保存遇文件锁 | 策划 | U6(重试耗尽 warning,dirty 保留) |
| 7 | 热载失败 | 策划 | U6 + Game 视图数据不变 |
| 8 | 切源被拒 | 策划/程序 | U13 禁用态 + U6 |
| 9 | workbook 格式过新 | 任意 | U6 / CLI 报告(只读打开) |
| 10 | xlsx 版本冲突 | 程序/策划 | git 冲突 → C6/V2/I5 对账 |
| 11 | convert error | CI/程序 | I4 退出码 + 报告工件 / U6 |
| 12 | 坏数据已入库 | 游戏 | 宿主日志(运行时 Diagnostic 投影);回退后编辑器侧 U6 |

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
| 9 | 门面菜单自定义/插件化 | 选项全集 = 场景全集(§2 矩阵):新场景先过矩阵再进菜单;配置化菜单会让工具面脱离覆盖度契约 |

## 10. 模块验收测试

CLI 与配方可自动化;窗口/对话框为手测清单(实施 §3-M5 惯例):

1. 矩阵覆盖(文档评审断言):§2 每行至少一非空格、每格取值合法;§3-§6 每个工具条目被至少一格引用。
2. CLI 解析:显式参数覆盖 json / json 缺省 / 缺必需项 → 退出码 3 三态;convert 缺 `--out` 用 json `bytesOutput`。
3. 退出码矩阵:0/1/2/3 各至少一例;`--json` 报告可反序列化为 OperationReport。
4. normalize:legacy 命中重写与命中计数报告;`--dry-run` 零写入(M1§7-14 复用,CLI 面新增)。
5. 入口等价:Generate/Convert/Refresh 的菜单与 CLI 对同一输入产等价报告(结构化比对,Utc 除外;M3§2)。
6. difftool 配方:经 git difftool 调用与直接调用 diff 同报告;I5 markdown 投影分组与字段级旧/新值(golden)。
7. freshness 阶段:改 proto 不重生成 → 红;重生成 → 绿(M3§11-3 的配方化)。
8. S8 剧本:纯 Excel 提交坏数据 → V3 拦截;绕过 hook → I4 红且 PR 评论可读定位。
9. U12:构造三行 dirty(改值/新建/删除)→ 清单条目与旧/新值;行级保存收窄写回计划(M2§6);Undo 后条目消失。
10. U13:Play 中切源成功与失败保留旧源(P§12-7 复用)的入口面;非 Play/模式禁用态及原因展示。
11. 手测清单——对话框:U7 三值预览、批量 Resolve、未全解禁保存;U8 四触发面 × 按钮集(冲突态无"保存并继续");U9 三选;U10 blocker 禁 Apply、purge/rekey 默认关。
12. 手测清单——Browser 族:U3 搜索文法直通 + display_name 扩展、徽标四态、右键全集、Open in Excel;U4 拖拽赋值与 picker 约束;U5 摘要;U6 环上限、双击定位链、Console 分级投影;U11 单源(UI 改 → json 变、无 EditorPrefs 残留、外部改 json → UI 刷新);U1 空态引导。
13. 命令样例回放:§7 逐块在 fixture 仓库可执行,注释断言(计划分级、legacy 命中计数、退出码、diff 四分类、用法错误三态)成立;§7 未出现的参数组合以用法错误拒绝(全集封闭)。
14. W1 门面:无子命令 + TTY → 菜单,非 TTY → 用法 + 退出码 3;各选项回显序列与 §7 对应块逐字一致(golden);危险门默认否;`:` 直通与独立运行产等价报告;会话退出码 = 最后一次操作。
15. W2 配置:解析链三态(显式 > workspace > project 缺省);workspace 含 project 键域副本 → 用法错误 + 退出码 3;缺 workspace → 门面引导创建(写入前回显)且子命令不受影响;`generatedDir` 驱动 I2、`baseBranch` 驱动选项 5 与 I5。

## 11. 与仓库现状衔接

- 本模块仅文档,无代码交付。本次为覆盖度重写:结构改为矩阵驱动,初版工具规格全部保留(编号化为 C/U/I/V 条目);新增 U12/U13、I5 的 markdown 投影、✗2,均为既有机制/API 的投影(P§6.5-2、P§7.2、P§9),不产生机制修订。
- 2026-07-11 增补 §7 场景命令样例(样例即规格,M2 D1 惯例延伸到命令面);原 §7-§12 顺移为 §8-§13。
- 2026-07-11 再迭代(D7):CLI 升格为工作空间管理工具——新增 W1 选项式门面与 W2 `ExcelDb.Workspace.json`(§3.1/§3.2),§3 分层重排(3.3 命令全集 / 3.4 解析链 / 3.5 交付),§7.1 增门面会话样例;M3§1 工件表物理增行(引本文 §3.2);lean-plan 与实施文档头部注记已扩注;门面为纯组合层,无机制修订。
- 修订登记(沿用初版):P§10 命令族补登记 `normalize`(M1§2 已定义语义,P 原文漏列入口)、公共参数增 `--project`(`--schema`/`--workbooks` 转覆盖项);P§11 的菜单/浏览器/picker/冲突对话框以本文 §4 细化;实施文档 §6 读作本文 §5 流水线配方,§3-M5 5.2/5.3 与 §3-M6 6.3 工作项以本文为规格。头部注记引用节号已随本次重写同步(CLI=§3、Unity=§4、CI=§5)。
- samples 增补(非契约):CI yaml 样例、pre-commit 与 difftool 配置脚本、diff json→markdown 投影脚本。
- poc 无对应物。

## 12. 待后续模块决定的边界

- workbook 物理契约(下拉、批注、metadata sheet)的生成细节 → 模块 5(workbook 与身份)。
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

推演自发:CLI 逐命令传 `--schema`/`--workbooks`,Unity 设置若另存 EditorPrefs,同一仓库就有三份工具配置。

**`ExcelDb.Project.json` 是工具配置的唯一事实源:CLI 参数是覆盖不是平行来源,Settings UI 是文件投影不是副本;解析顺序(显式 > json)必须是规格常量——否则两个入口对同一仓库跑出不同结果,入口等价(M3§2)从根上破产。**

落点:§3 公共参数与解析顺序(P§10 修订)、U11;convert `--out` 缺省 = json `bytesOutput`。

---

### D3(2026-07-11)命令族收口:normalize 补登记,不长新命令

推演自发:M1§2 定义了 normalize 操作,P§10 命令族却无登记——操作存在而入口缺席;反向诱惑同样存在(watch/dump/doctor)。

**命令全集必须与操作全集同构:机制已定义的操作必须补登记入口,机制没有的操作不得从工具侧长出——工具层发现缺口回修机制文档(M3 D1 原则复用),工具层的便利需求用配方组合既有命令(difftool 复用 diff)。**

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

落点:§7 全节;验收 §10 第 13 条(样例回放);衔接登记与小节顺移(§11)。

---

### D7(2026-07-11)工作空间管理工具:选项式门面 + 命令行细节双层

> 将这个集成工具做成一个工作空间管理工具, 给出一个配置文件, 集成工具默认以选项式门面给出, 允许进入命令行细节.

**CLI 的默认界面从"记忆子命令"换成"认场景":门面把 §2 矩阵的场景列直接做成菜单,选项执行 = §7 命令序列逐条回显后运行——细节永远可进入(`»` 回显、`:` 直通)、可脚本化(CI 只用子命令),门面自身零操作语义;配置随之分双层:`ExcelDb.Workspace.json`(编排:构建目标/生成目录/基线分支/脚本指针)指向 `ExcelDb.Project.json`(数据管线),键域不相交——D2 的单源原则升级为"每个关注点恰一个源"。**

落点:§3.1/§3.2(W1/W2)、§3.4 解析链(修订 D2)、§7.1 会话样例;M3§1 工件表增行;✗9;CI 不受门面影响(非 TTY 拒绝,I 系原样)。
