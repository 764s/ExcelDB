# 模块 8:兼容与迁移

状态：**Provisional**。本文是 M8 兼容与迁移领域的唯一 owner；跨模块权威规则、状态和开放决策见 [`docs/spec/README.md`](../spec/README.md)。本文定义“schema 变化后如何保留数据并显式处理破坏操作”。身份与声明严格以 M1 为准:表 = `(exceldb.table).id`,字段 = proto field number,枚举值 = enum number,oneof variant = field number;`aliases`/`legacy` 是兼容输入,不是身份。归档 C# 特性方案及 `FormerName` 无规范效力。后续引用本文记作 M8§x。

## 1. 范围与总则

- 范围:当前/历史 SchemaDescriptor diff、结构生成兼容分级、字段与类型演进、export target membership、格式三阶段、数据保留、显式 purge/rekey、各操作门禁与交付节律。
- 不定义 workbook 物理布局、导入实现、runtime source transaction;分别消费模块 5、6、7 的契约。
- 兼容判断只看 canonical descriptor 数字身份与语义,不从生成 C#、Excel 可见表头、列位置、sheet 名、相似字符串或反射猜历史。
- 默认不丢数据。不能证明安全的变化只报告、保留原数据并门禁写入/convert;不得用“生成最新结构”掩盖迁移。
- 所有可能改结构或数据的操作走 `plan → report → apply-or-abort`;apply 必须复核 source revision/descriptor,补丁写临时文件、复读验证并原子替换。
- `safe/warning/error/blocker` 沿用全局语义:blocker 零写入,error 保留数据且门禁 convert/发布,warning 需报告,safe 才可默认应用于 schema-owned 区域。

## 2. 兼容事实源与 descriptor 历史

一次 compatibility analyze 的输入:

1. 当前 proto 编译得到的 canonical `SchemaDescriptor`,其中同时保留 live table 与 `TableOpts.retired = true` 的退役 table id。
2. 上一次已发布 descriptor 的 canonical 快照,或能按 hash 取回的版本化工件。
3. workbook metadata/导入快照中记录的 descriptor hash与最小映射摘要,用于确认当前 workbook 落后或漂移。
4. 当前 workbook 的数据存在性、值可解析性、identity/key 状态;只影响动作能否安全执行,不改变 descriptor identity diff。
5. 最近 check/normalize/migration report,只作为 legacy 命中归零、数据迁移完成等门禁证据。

历史规则:

- previous/current descriptor 都必须由同一 canonical 编译规则产生。proto 文件顺序、空白和普通注释变化不得制造 diff。
- previous descriptor 缺失时允许 lint/check 当前 schema与只读扫描 workbook,但禁止自动判定 rename、删除清理、类型重解释或 destructive apply。
- descriptor 快照是可重建/版本化的机器工件,不是第二 schema 事实源;当前手写 proto 始终是声明事实源。
- 生成代码只用于新鲜度检查;手改或旧生成代码不能参与兼容分类。
- diff 条目必须可定位到 table id、field id/enum number/variant number及 old/new descriptor path,并确定序输出。

## 3. 数字身份与 rename

| 元素 | 稳定身份 | rename 判定 |
| --- | --- | --- |
| ASSET 表 | `(exceldb.table).id` | id 相同、message/sheet/display 名变化 |
| 字段 | proto field number | owner + number 相同、字段名变化 |
| 枚举值 | enum number | enum + number 相同、token/display 名变化 |
| union variant | oneof 内 field number | owner oneof + number 相同、variant 名变化 |

- 发布后的数字身份不可因 rename 改变;删除过的 proto number 必须 `reserved`,复用即 lint blocker。
- 名称参与 property path/列绑定与当前 M1 schema_hash,所以 rename 会产生 descriptor/hash 变化;但数字 identity相同使其分类为 rename,不等同删除+新增。
- `FieldOpts.aliases` 与 `EnumValueOpts.aliases` 只接受旧表头/token,帮助旧 workbook 解析并投影到当前身份;aliases 不进 schema_hash,不能跨不同数字 id 自动搬数据。
- `FormerName`、`FormerlySerializedAs`、C# 成员特性链与按相似名推断一律不是输入。发现这些旧标记应 lint/report,不能改变兼容结论。
- 数字 id 不同,即使名称和类型完全相同也按删除+新增;只有显式数据迁移计划可以建立值映射,不能合并 identity。

### 3.1 ASSET 表退役与 table-id tombstone

- 表的永久退役事实源是原 proto message 上的 `TableOpts.retired = true`。删除已发布 ASSET 表时不得删除 message 或 table id；必须保留该声明并把同一 id 从 live 状态单向切换为 retired。
- retired 是不可逆 tombstone。不得清除 retired 使其重新成为 live 表；需要恢复相同业务形状时也必须声明新表并分配从未使用的新 id。
- 当前 schema 内所有 live 与 retired table id 共享同一全局唯一性域。新表 id 与其他 live id 冲突、与 retired id 冲突，或两个 retired 声明互相冲突，均为 lint blocker。历史 descriptor 只用于发现 tombstone 遗漏或非法复用,不取代当前 proto tombstone 的事实源地位。
- retired message 仍进入 canonical descriptor 的退役身份集合,用于 diff、lint 与历史校验；它不拥有 active sheet 投影，不生成运行时表类型/注册项，不进入 converted bytes。其旧 workbook sheet 与数据默认原样保留为 removed/deprecated 内容,只能经显式 purge 物理删除。
- 退役前必须先迁移或删除所有 live schema 对该表的引用，并完成需要的宿主代码迁移。任一 live 字段仍引用 retired id 时 blocker；不得靠 importer 忽略引用或由 runtime 兜底。
- message 内不再需要的字段按普通字段退役规则删除并 `reserved` 原 number；删除字段不能代替保留 table message/id 这一 tombstone。

### 3.2 Export target identity 与 legacy 等价

- M1 物化后的 table/field export target 集合是 canonical descriptor 语义；兼容分析比较稳定 `ExportTargetId` 与每个数字 field identity 的 effective membership,不比较 UI 文案、策略实现类型或字段在选项中的书写顺序。`client`/`server` 是 export target,不是 M7 `RuntimeMode`。
- runtime 工件身份暂为完整单一 `(SchemaHash, ExportTargetId)`。同一 SchemaHash 下不同 target 是不同 runtime projection,不能因 hash 相等互换；是否未来拆分 hash 仍只由 §12/总纲 OD6 裁定。
- legacy `ExportPolicy` 先按固定等价规则规范化后再 diff:表级 `EXPORT_DEFAULT/EXPORT_ALL` 固定等价 `{client,server}`,字段级 `EXPORT_DEFAULT/EXPORT_ALL` 保持继承父集,`EDITOR_ONLY` 等价空 target 集。未来新增 target 不得扩大 legacy `EXPORT_ALL` 的含义。
- 只把 legacy 写法改写为 effective target 集完全相同的显式声明属于 safe 语法迁移,不得制造 membership diff；canonical descriptor、SchemaHash、codegen 与各 target bytes 必须保持不变。新旧声明冲突或无法证明等价时不得猜测优先级,按 M1 lint blocker 处理。
- `IExportTargetStrategy` 只在 authoring plan-time 把自动选择展开为候选 proto 中的显式 target 集；其 Id、实现类型或版本不是 compatibility identity。计划冻结后及 schema build/generate/check/convert/runtime 均不得重跑策略或用当前策略重新解释已提交字段。

## 4. 兼容分级

| 变化 | 分级 | 默认动作 |
| --- | --- | --- |
| display_name/header_comment/aliases 变化 | safe | 刷新 schema-owned 展示;不改数据身份 |
| 同数字身份 rename | safe | 更新当前名称/路径映射,数据原位保留 |
| 新 ASSET 表,id 未被任一 live/retired 表使用且落点唯一 | safe | 生成空结构;id 冲突或多 workbook 落点歧义 → blocker |
| live ASSET 表以同 message/id 显式切换为 `retired = true`,且无 live 引用 | warning | 停止 runtime codegen/export；旧 sheet/数据原位保留为 removed/deprecated |
| 已发布表 message/id 被删除而未留下 retired tombstone | blocker | 不产 descriptor/codegen,恢复 message/id 并标 retired |
| retired id 被新 live/retired 表复用,或 retired 被清除后复活 | blocker | 不产 descriptor/codegen；新表必须使用从未使用的新 id |
| 新 optional/有 default 字段(新 number) | safe | 新增列;旧行不批量物化默认值 |
| 新 required 且无 default 字段 | error | 可生成结构并定位缺值;convert/发布门禁 |
| enum 新值(新 number) | safe | 更新当前 schema 投影 |
| 同 field number 新增一个 effective ExportTargetId membership | safe | 保留 authoring 数据/字段身份；为该 target 增加 runtime surface,并按 §5.1 配套发布 codegen/bytes |
| 同 field number 移除一个 effective ExportTargetId membership | warning | 该 target 的 runtime surface 删除；先完成宿主代码迁移,authoring/其他 target 数据原位保留 |
| 同 field number 把 membership 从 target A 移到 target B | warning | 作为 A 移除 + B 新增的同一计划；两侧 codegen/bytes 与宿主改动配套发布 |
| 删除字段/枚举值/variant且已 reserved | warning | 旧物理数据保留为 removed/deprecated,不进入新导出 |
| 删除但未 reserved、或复用旧 number | blocker | 不产 descriptor/不写 workbook |
| 同 field number 改类型、shape、reference 或除 target membership 外的关键语义 | blocker | 禁止就地重解释;走 §5 新 number 三阶段 |
| 旧值按当前 parser 存在不可解析项 | error | 列出全部坏 cell,原值保留,门禁 save/convert涉及项 |
| key 字段集合/顺序变化且表非空 | blocker | 仅显式 rekey plan 可继续,行 guid保持 |
| table id 改变但看似同表 | blocker | 按删旧+建新;必须有显式行 identity/data 映射 |
| schema-owned patch 与 helper/freeform/正式数据重叠 | blocker | 不移动/覆盖用户内容,先解决 ownership |

- safe 只表示可生成 patch,不表示可以重写整个 sheet。物理动作仍受 workbook ownership与非破坏写回约束。
- warning/error 项不得被顺手清理。旧列、旧 token和不可解析原文必须留到显式迁移或破坏操作。
- export target 分级比较 effective membership；表级集合变化导致继承字段批量变化时,报告必须展开到每个 table id/field number/target delta,整体级别取所有展开项中的最高级。key、引用或其他 runtime 闭包在任一目标失配仍按 M1 lint blocker,不因 membership 自身是 safe/warning 而放宽。
- CI 的 `schema.drift` 判 error;本地编辑器可以只读/带诊断打开以完成修复,但不能据此放宽 convert/runtime schema门禁。

## 5. 字段与语义变化三阶段

类型、value shape、引用目标/策略、key语义,以及会改变同一 target 内 canonical value/runtime 解释的非 membership export 语义,不得在原 field number 上直接换义。仅改变一个字段属于哪些 ExportTargetId、不改变其在任一保留 target 内的值语义时,是 §5.1 的明确例外。其余变化使用标准节律:

1. **新增**:以新 field number 声明新字段,旧字段继续存在;必要时让 importer/validator同时理解两者,但生成/写出规则必须确定。
2. **迁移**:显式 plan 扫描旧值,把可证明的值转换到新字段;报告 converted/skipped/error 数与数据丢失风险。迁移失败不清旧值。
3. **删除**:所有受控 workbook 的旧字段使用/迁移欠账归零后,删除旧声明并在 proto 中 `reserved` 旧 number;物理旧列默认仍保留为 removed/deprecated,除非另走 purge。

约束:

- 每阶段是一次独立 schema 发布与 WF2 原子交付,不得一次提交同时新增、搬值、删除旧身份而失去回退窗口。
- 新旧字段同时存在时,同一业务值的 authority 必须由阶段明确;禁止 importer 按“哪个非空”长期猜测。
- 迁移以 numeric identity和canonical value定位,不以表头文字/列序/C# 成员名定位。
- narrowing、单位变化、枚举重映射、引用重绑定等可能丢数据/换义的转换必须显式列出影响集并确认;无法证明时 blocker。
- runtime Open/Switch/Refresh 不执行 workbook migration;schema hash不匹配按 M7 保留旧 source。

### 5.1 Export target membership 变更

membership 是同一 authoring field identity 的 runtime projection 集合,不是新的字段身份；因此下列变化均保留原 proto field number,不复制 cell、不搬值、不写 `reserved`:

1. **加入 target**:按 §4 判 safe。计划必须列出新增 runtime table/field/API、随 target 导出的既有数据量及依赖闭包；M1 lint 证明 key、引用、embedded/enum/表达式等运行时依赖对该 target 完整后方可应用。
2. **移除 target**:按 §4 判 warning。先迁移该 target 的宿主代码与引用,再让对应生成成员/访问器和 bytes 数据消失；原 xlsx cell、authoring 读取面及其他 target membership 原位保留,不得顺手 purge。
3. **移动 target**:按 §4 判 warning,并在同一不可变计划中展开为 source target 移除与 destination target 加入；任一侧 closure、宿主迁移或产物准备失败即整项不发布,不得形成只有一侧生效的中间态。

当前 SchemaHash 是包含全部 target 声明/物化结果的完整单一 hash,没有 target view hash。任一 effective membership 变化都会产生一个新的完整 SchemaHash；即使某个 target 的字段集合未变化,其 generated registry/codegen 与 bytes header/manifest 也必须随本次发布重建到同一新 hash。一次 schema 发布不得混用旧 target registry/bytes 与新 target registry/bytes；每个 runtime source 还必须携带自身 ExportTargetId,由 M7 以 `(SchemaHash, ExportTargetId)` 校验。

## 6. CellFormat 三阶段

格式迁移只允许改变 cell 文本结构,canonical 值类型与runtime表示必须不变。类型或语义变化走 §5。

1. **新增窗口**:新格式设 canonical,旧格式加入 `CellFormat.legacy`;canonical与全部 legacy身份共同进入当前 M1 schema_hash。解析每格时:
   - 仅一个成功 → 使用其 canonical 值;
   - 多个成功且 canonical值相同 → 取 canonical;
   - 多个成功但值不同 → `format.ambiguous` error,不猜;
   - 写出始终用 canonical,legacy命中计数进入 check/import report。
2. **迁移窗口**:`normalize` 先 dry-run/plan,再把命中 legacy的 cell重写为 canonical。未命中、不可解析、歧义 cell不改;报告每个 workbook/字段的重写数、剩余命中与错误数。
3. **安全删除**:最近一次覆盖全部受控 workbook的 check/normalize证据显示 legacy命中为零后,下一次 schema发布才移除 legacy。移除后旧文本解析失败,不得回退猜测。

- `legacy` 不得嵌套 legacy,不得与 canonical物化身份相同(M1 XDB018)。
- canonical或legacy任一阶段变化都会改变当前单一 schema_hash;迁移期间运行时 bytes必须与对应生成代码/hash配套。
- SchemaDefaults级格式窗口与字段级窗口遵守相同证据与删除门禁。
- 任一阶段门禁失败即停留当前阶段;不能为了通过 convert 临时放宽 parser后不登记 descriptor。

## 7. 数据保留与物理清理

- Generate/normalize/migration默认只触碰计划内 schema-owned cell、metadata与明确目标数据格;其余样式、公式、批注、未知列、辅助列和自由 sheet保持不变。
- schema删除字段不等于物理删除列。默认把旧列保留为 removed/deprecated辅助数据,不导入当前字段、不进converted bytes。
- 从一个或全部 runtime target 移除字段也不等于 schema 删除或物理清理；字段仍属于 authoring schema,其 cell 与未移除 target 数据保持原位。只有 proto field 真正删除并 reserved 后才进入普通删除规则,物理清理仍须显式 purge。
- 旧值不可解析时保存原始 cell,并提供定位诊断;不得用default/零值覆盖。
- 新字段缺值/default的canonical读取结果沿用M6 OD1最终真值表;无论该决策如何,不得借结构刷新批量把default写入旧行,除非显式materialize operation。
- apply写临时文件,复读计划内canonical值与descriptor/metadata映射,成功后原子替换;失败保留原文件和dirty/migration状态。
- 多 workbook引用、rekey或迁移形成同一语义闭包时,plan/gate必须先完整列出所有workbook;任一preflight blocker则零写入。跨workbook实际commit是全闭包原子、逐本提交还是journal恢复,仍归 M6§9 第 3 项,本文不提前裁定。

## 8. 显式破坏操作

下列动作永不由普通 Generate、Refresh、SaveAssets、normalize或convert顺手触发:

### 8.1 purge

- 物理删除 removed/deprecated列、sheet或旧格式辅助物必须显式 `purge`/`--purge`。
- dry-run报告 ownership proof、数据是否为空、公式/批注/validation/helper依赖、受影响cell与不可逆风险。
- 含正式数据或不能证明属于ExcelDB-owned区域时默认blocker;只有显式数据迁移及data-loss确认才能继续。
- purge不改变仍存活数字identity的语义,也不能删除proto `reserved` 历史或 retired table message/id tombstone。

### 8.2 rekey

- key字段集合、顺序或canonical规则变化且表非空时必须显式 `rekey`/`--rekey`。
- plan全量计算新key、重复项、asset path变化、引用展示token改写与跨workbook影响;行guid/AssetIdentity保持不变。
- duplicate/ambiguous引用或无法完整改写时blocker,不部分更新key索引或引用cell。
- rekey成功是数据迁移,必须与proto、生成代码和受影响xlsx原子交付。

### 8.3 data-loss migration

- 删除值、narrowing、单位/枚举换义、table split/merge等不可逆变化必须在plan逐项标`data_loss_risk`,并要求显式确认。
- 确认绑定当前descriptor、source revision与影响集;输入变化后计划stale,必须重做dry-run。
- 备份不是降低风险分级的理由;失败仍须原文件可恢复且不得发布新runtime source。

## 9. 操作门禁

| 操作 | 允许条件 | 不满足时 |
| --- | --- | --- |
| schema build/lint | 当前descriptor内部合法、field/enum/variant number 未复用,live/retired table id 全域唯一且退役转换合法 | blocker,不产descriptor/codegen |
| generate dry-run | 可读current/previous descriptor与workbook mapping | 仅报告;历史缺失禁破坏项 |
| generate apply | plan无blocker,目标区ownership可证明 | 零写入 |
| editor import/check | mapping可确定;数据错误可定位 | 带诊断载入或read-only;CI drift为error |
| SaveAssets | 无mapping blocker/未解兼容冲突 | dirty保留,零写入 |
| normalize | legacy parse唯一且plan未stale | 歧义/错误cell保留 |
| convert/build | 无error/blocker,迁移完成,schema匹配；本次 target 集的全部 registry/codegen/bytes/manifest 使用同一完整 SchemaHash 且各带正确 ExportTargetId | 失败,不产或不发布混合版本 target 工件 |
| Runtime Open/Switch/Refresh | source `(SchemaHash,ExportTargetId)` 等于代码期望与 session 固定身份 | 失败并按M7保持 closed或保留旧source；换 target 只能 Close+Open |

兼容分类与门禁只允许操作面收紧,不允许Unity菜单、CLI、CI或runtime各自发明更宽松解释。

## 10. 团队交付节律

- 每次WF2变更先改proto并构建descriptor/codegen,再generate dry-run,最后apply/check。
- proto、生成代码、受影响xlsx以及本阶段normalize/migration结果必须在同一变更集原子交付。
- 表退役必须先清除 live 引用与完成宿主迁移,再以保留 message/id、设置 retired 的独立变更交付；该变更停止运行时表生成,但不得顺手删除旧 workbook 数据或 tombstone。
- export target membership 变化必须把 proto、全部 target generated registry/codegen、全部 target bytes+manifest 及受影响 target 的宿主代码作为同一 schema 发布配套交付；移除/移动 target 还必须在发布前证明旧 runtime API 使用已迁移。完整单 hash 下不得只更新“字段集合实际变化”的那个 target 而留下其他 target 的旧 hash 工件。
- 生成代码新鲜度与`schema.drift`是CI门禁;旧生成代码不得掩盖proto/workbook不一致。
- format/字段三阶段每阶段至少一次独立变更;进入下一阶段的证据是最近覆盖全部受控workbook的machine-readable报告。
- PR对变更workbook产check+diff工件,使binary数据变化可评审;报告本身是构建工件,不是新的authoring事实源。

## 11. 明确禁止

- 禁止C#特性schema、`FormerName`/`FormerlySerializedAs`链、反射生成类型推断兼容。
- 禁止按同名、相似名、列位置或sheet顺序把不同数字id自动视为同一字段/表。
- 禁止在原field number上静默改变canonical类型/shape/引用语义。
- 禁止把 client/server 编码为 RuntimeMode、按 source kind 猜 ExportTargetId,或在 runtime 用 `IExportTargetStrategy` 动态重算/扩大已提交 target membership。
- 禁止普通Save/Generate顺手purge、rekey、批量物化default或丢弃不可解析原值。
- 禁止runtime执行workbook/target migration、在 Switch/Refresh 中改变 ExportTargetId,或以旧 schema/错误 target candidate 替换当前有效source。
- 禁止把aliases/legacy当作永久双事实源;它们必须有清理证据和明确退出阶段。
- 禁止删除已发布 table message/id、清除 retired 复活旧表，或把任一 live/retired table id 分配给新声明。

## 12. 开放决策

1. **schema hash拆分**:M1当前完整单一`schema_hash`包含名称、类型/shape、全部 export target 声明及物化 membership、引用、format canonical+legacy等语义,排除display/comment/aliases。ExportTargetId 当前只是与该 hash 正交的 runtime 身份维度,工件/session 用 `(SchemaHash,ExportTargetId)` 区分 projection,不新增 target view hash。是否未来拆为 authoring/workbook/runtime semantic/layout/codegen hash仍未决定；裁定前所有工具和M7 source门禁继续只使用M1单 hash + 显式 target,不得先实现多hash或按 target 自算 view hash再让入口产生不同兼容结论。

## 13. 模块验收

1. descriptor diff按数字identity:表/字段/enum/variant rename判rename;同名不同number判删+增;`FormerName`不影响结果。
2. previous descriptor缺失时safe只读分析可用,purge/rekey/type migration自动apply被拒绝。
3. 新增optional/required、删除reserved/未reserved、类型换义、key结构变化、ownership重叠逐行命中§4分级。另对同 field number 的 target 加入/移除/移动分别命中 safe/warning/warning,且表级继承变化展开为确定序字段级 delta。
4. 字段语义三阶段:新旧并存→迁移部分失败保旧→命中归零→reserved删除;每阶段可独立回退。
5. 表退役:live 表清除引用后以同 message/id 设置 retired → descriptor 保留 tombstone、runtime codegen/bytes 不含该表、旧 sheet/数据保留；直接删 message/id、复用 live/retired id、清除 retired 复活、live 引用 retired 各自 lint blocker 且不产 descriptor/codegen。
6. format三阶段覆盖唯一成功、多成功同值、`format.ambiguous`、normalize零写入dry-run、跨workbook命中归零和安全删除。
7. 不可解析值、unknown/helper/freeform、样式/公式/批注在generate/normalize/migration失败和成功路径均保留。
8. purge、rekey、data-loss migration在plan stale、确认缺失、跨workbook任一blocker时均零写入,且 purge 永不删除 retired table tombstone。
9. legacy export 等价:表 DEFAULT/ALL→`{client,server}`,字段 DEFAULT/ALL→继承父集,EDITOR_ONLY→空集；把 legacy 写法改为相同 effective target set 的显式声明,断言 descriptor、SchemaHash、codegen 与 client/server bytes 均不变。未来新增第三 target 时 legacy ALL 仍不自动包含它；冲突声明 lint blocker。
10. target 发布闭包:加入、移除、移动 membership 后 field number 与 xlsx cell 不变,完整 SchemaHash 改变；client/server generated registry、codegen、bytes header、manifest 全部重建到同一新 hash且分别携带正确 target。任意混入旧 hash、错 target、只更新单侧移动结果均门禁失败且不发布混合工件。
11. lint/generate/import/save/normalize/convert/runtime对同一compatibility输入得到一致或更严格门禁,无入口特例。M7 分别拒绝同 hash 错 target、同 target 错 hash及 Switch/Refresh 换 target,只有 Close+Open 可切换。
12. schema hash拆分在未决状态下有防止实现自行放宽的测试/文档断言；断言当前不存在 target view hash,所有 runtime 身份均使用 `(SchemaHash,ExportTargetId)`。
