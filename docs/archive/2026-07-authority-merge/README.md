# 2026-07 权威结构合并归档

状态：**Historical / Non-normative**

本目录保存 2026-07-11 建立模块化唯一权威规范前的计划、裁剪记录和实施草案，仅用于追溯设计背景。当前规范唯一入口是 [`docs/spec/README.md`](../../spec/README.md)。

## 使用规则

1. 本目录任何文字均不得作为实现、评审或验收依据。
2. 归档内容与总纲或 M1-M8 冲突时，不做“新旧取舍”；归档内容直接无规范效力。
3. 归档中存在而规范 owner 尚未接管的行为保持未定，不得因归档写过就视为已决定。
4. 需要恢复某项思想时，必须先在总纲登记 owner/开放决策，再修改对应规范模块；不得直接取消归档状态。
5. 历史文件中的 `P§x`、章节号和文件头权威声明只在当时语境有效。现行 M1-M4 中残留的 `P§x` 统一按总纲 §7 迁移表解释。

## 文件清单

| 归档文件 | 原角色 | 已迁移到 |
| --- | --- | --- |
| `unity-like-exceldb-plan.md` | 完整目标计划与大量执行细化 | 总纲产品目标/边界；M1-M8 owner 文档 |
| `exceldb-lean-plan.md` | 精简计划与跨域契约 | 总纲不变量；M5-M8；M1-M4 已接受契约 |
| `exceldb-cuts-adr.md` | 旧裁剪决策及多次修订 | 总纲非目标；必要理由保留在本归档 |
| `exceldb-implementation.md` | 旧实施布局、算法、里程碑和样例 | 总纲组件/事务/质量边界；未来非规范实施路线 |
| `module-04-workspace-decisions.md` | M4 已撤销的 Workspace/W1/W2 与选项菜单决策 | M4 D9 的 Project-only 配置与 G1 线性主引导 |

## 已明确废弃的旧方向

- C# 特性作为 schema 事实源；现行事实源为 M1 proto。
- 生成类型继承 ExcelDB `Object`/`ScriptableObject`；现行为普通 C# 类 + facade/侧表。
- `FormerName` 作为结构身份；现行为 proto 数字身份、aliases 与 descriptor history。
- 由旧计划、实施文档或文件头补丁共同计算当前契约。
- 按文档日期或章节先后解决规范冲突。
