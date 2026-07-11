# M4 已撤销的 Workspace 决策

状态：**Historical / Non-normative / Superseded by M4 D9**

本文仅保存 M4 D7-D8 的历史评审轨迹。当前配置和主引导契约只读 [`docs/modules/04-integration-tools.md`](../../modules/04-integration-tools.md) 正文与 D9；下列文字不得作为实现、评审或验收依据。

## D7(2026-07-11)工作空间管理工具：选项式门面 + 命令行细节双层

> 将这个集成工具做成一个工作空间管理工具, 给出一个配置文件, 集成工具默认以选项式门面给出, 允许进入命令行细节.

历史结论曾引入选项式场景菜单，以及由 `ExcelDb.Workspace.json` 指向 `ExcelDb.Project.json` 的双层配置；该结论已被 M4 D8/D9 全部撤销。

## D8(2026-07-11)工作空间主入口 = 四阶段线性引导，diff 归 Git

> 工作流应该是一个线性工作流(这并不是全部工作流,例如应用方写回并不在这个流程里:
> 工作空间初始化引导 -> 创建表引导 -> 修改表结构引导 -> 发布到处数据引导.
> 至于 diff,应该是 git 的单独行为,不建议纳入.

其中“四阶段线性主线”和“diff 归 Git/VCS”由 M4 D9 保留；Workspace 配置、W1/W2 命名及场景菜单均已撤销。
