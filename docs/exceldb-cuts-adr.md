# ExcelDB 裁剪决策记录(ADR)

本文记录 `exceldb-lean-plan.md` 相对旧计划(`unity-like-exceldb-plan.md`)裁掉的子系统及理由。它是决策记录,不是规格;精简计划内出现的机制即全集,旧计划中未被精简计划收录的子系统一律视为已裁剪,以本文为准查理由。

格式:被裁项 → 理由;替代物(括号内为精简计划章节)。

1. ProjectPolicyDescriptor / OperationProfile / 7 种内置 profile / override allowlist / profile hash → 策略间接层没有付费客户;替代:`RuntimeMode` 枚举 4 值 + 固定能力矩阵(7.8)+ `ExcelDb.Project.json`(3)。
2. CompleteConformance 四分区验收体系 / capability status 分类学 → 验收就是测试套件通过;替代:测试清单(12)。
3. hash 家族(layout/codegen/report/profile/preload/manifest 等十余种)→ 只保留 `schema_hash` 与内容 hash 两类(4.5、7.4);展示变化用生成时直接单元格比对,codegen 陈旧用内嵌 schema_hash 比对。
4. 扩展包/权限描述符/文件系统根/网络声明/side-effect ledger → 这是库不是插件市场;替代:普通 C# 接口注册(4.6、8.3)。
5. 签名工件/信任链/分层 CompositeDataSource → 投机需求;替代:`IDataSource` 接口留作扩展点(7.2)。
6. 1100 行报告格式/报告 hash/投影分账 → 替代:单一 `Diagnostic` 结构 + json 报告(9)。
7. id 分配注册表/tombstone/保留区 → 数字 id 整体取消;字段身份 = 名字 + `[FormerName]`;复用检查 = 对上次快照的 lint(6.6)。
   修订(2026-07-08 评审):数字 id 恢复——schema 声明回归 proto 后,表/字段/枚举值/variant 身份 = proto number,`[FormerName]` 机制废弃;复用防护 = proto `reserved` + lint(modules/01-schema.md §1/§5)。独立的 id 分配注册表子系统仍维持裁剪。
8. schema-discriminated polymorphic payload / arbitrary JSON legacy codec → union(稳定 variant token)与 `ICellCodec` 已覆盖其需求;维持裁剪。
   修订(2026-07-06 评审):本条初版曾把 LocalizedTextRef/expression/weighted/curve/label/preset/union/map 八项能力家族一并降级为 samples;评审决定恢复为一等核心能力,规格见精简计划 4.7,多态 payload 与 JSON legacy 维持裁剪。
9. canonical value 四态(missing/default/explicit_null/explicit_value)→ 二态:空 = 默认(可空型为 null),非空 = 值(4.3)。
10. 编辑器 core operation no-GC 契约 / external_backend 分账 → 编辑器操作是人触发的,GC 无感知;no-GC 契约收缩到运行时(7.9)。
11. metadata sheet 行记录 + 伴随列双锚点及 reconciliation 流程 → 单锚点:伴随列是唯一行身份载体,快照提供历史,修复规则 5 条(5.3)。
12. workbook write lease → OS 文件锁 + 保存前重新比对(6.5)。
13. 迁移描述符子系统(migration id/dry-run/verify/data_loss_risk)→ 结构生成操作自身的 plan/apply 即迁移(6.6)。
14. 表头上方 helper 行 → 策划备注走单元格批注、辅助列、自由 sheet;表头区域固定 3 行(5.1)。
15. `boxedValue`/`managedReferenceValue`/gradient 等分配型投影 → 不提供;drawer 直接操作具体类型属性(6.4)。
16. proto schema 源(options.proto)→ 与"Unity 使用手感"核冲突(异质工具链,Unity 用户以 C# 类定义资产);现有 `ConfigDatabase`/`ExcelTableLoader` 按件拆用(UndoStack、DependencyGraph、xlsx IO),对外模型废弃(4.1、13)。
   修订(2026-07-08 评审):本条裁剪撤销——声明方式回归 proto(原计划 7.6/14.23 的首发默认),数字 number 身份优于名字身份,Unity 手感由生成的 C# 类型与 facade 承载,与声明层解耦;规格见 modules/01-schema.md。`ConfigDatabase` 对外模型废弃的结论不变。
17. 操作事务通用框架(OperationPlan/affected set/side-effect ledger)→ 各危险操作直接实现同一"plan → report → apply-or-abort"三段式约定,共享 `OperationReport` 类型(9),不做框架。
18. 旧计划 14.6/14.14/14.16 等章节的穷举式子协议(报告生命周期、watcher 调度状态机、property path 与 cell 映射的逐案枚举)→ 收敛为单页规则 + codegen 携带映射(6.4、6.8);逐案行为由测试固定,不由文档穷举。
19. xlsx canonical snapshot / diff / review artifact 家族(旧计划 12.2.3:快照入库、快照同步 CI、merge 命令、conflict artifact、修复工具族)→ 部分恢复(2026-07-10 模块 3):单一 `exceldb diff` 命令(只读导入 + guid 对齐 + canonical 值比较,modules/03-workflow.md §8)承担版本冲突对账与 PR 评审工件;快照入库、三方 merge 命令与独立修复工具族维持裁剪——身份修复与 rename/copy 诊断已由导入管线内建(5.3),版本冲突走"整文件二选一 + diff 对账重做"剧本(M3§8)。

裁剪总原则:机制只有在至少一个核的强制链上才保留;防御性基建(注册表、权限、签名、分账)在出现真实攻击面或真实多租户之前不建;"几乎不会发生"的分支给 blocker + 人工处理,不给自动化子系统。
