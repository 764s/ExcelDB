# 模块 2:AssetDatabase 门面(使用样例)

状态:本文是逐模块重推演的第 2 篇,以使用样例锁定 `ExcelDbEditor.AssetDatabase` 的对外契约,取代 `exceldb-lean-plan.md` §6.7 的签名清单。对齐基准 = Unity 2022.3 `UnityEditor.AssetDatabase`,像素级:成员名、参数名、返回类型、重载集与调用形态逐字对照;语义与 Unity 相同的成员不复述文档(P§0 写法规则),偏差全部登记在 §8。模块编号自本篇顺延,并经第 3 篇(工作流,`03-workflow.md`)与第 4 篇(集成工具,`04-integration-tools.md`,2026-07-11)再顺延:M1 文中预派的"模块 2(workbook 与身份)/模块 3(导入与编辑)/模块 4(运行时)/模块 5(兼容)"与本文正文所写"模块 3/4/5/6"统一改读"模块 5/6/7/8"。后续模块引用本文记作 M2§x;精简计划记作 P§x,模块 1 记作 M1§x。

## 1. 范围、映射与失败形态

- 范围:编辑期 authoring 门面——挂载、刷新与导入、加载、查找、结构操作、保存、依赖、标签、冲突。运行时读取是 RuntimeDatabase(模块 5),不在本文。
- 命名空间对应:`ExcelDbEditor` ↔ `UnityEditor`(`AssetDatabase`/`GUID`)。数据类型无 Unity 对应物:生成类是普通 C# 类,不派生 `Object`/`ScriptableObject`(Δ11);本文签名中的资产参数一律为 `object` 或泛型 `T`,合法实参 = SchemaRegistry 已注册的生成类实例,违者按失败形态返回 + Diagnostic。
- 样例即规格:样例展示的调用形态、返回值与注释断言就是契约;`Assert(条件)` 表示验收断言(§9 的测试基线),不是 API 成员。全文样例基于 M1§4 的 `game.proto` 生成类型(`Game.Configs`,C# 成员 PascalCase,M1§6),workbook 为 `Assets/Configs/game.xlsx`(SkillConfig 表 id 101、ItemConfig 102,key = `common.id`)与同 schema 的第二本 `Assets/Configs/game_dlc1.xlsx`;各代码块共享 §2 开头的 using 与路径常量。
- 概念映射(资产模型的像素对位):

| Unity | ExcelDB | 备注 |
| --- | --- | --- |
| 资产(.asset 文件) | ASSET 表的一行 | 行是唯一资产粒度(M1§3);实例为普通 C# 类(Δ11) |
| 文件夹 | workbook 文件、表名段 | 容器路径,Δ2 |
| 文件名(主资产名) | key(路径末段) | key 字段本体即名,无 `name` 投影(Δ11) |
| GUID(.meta 记录) | row guid(`__guid` 伴随列) | AssetGuid ≡ row guid(P§5.3) |
| .meta 文件 | 伴随列 + metadata sheet | P§5.1/5.2 |
| Library 导入缓存 | 导入快照 | P§6.2 |
| AssetImporter / AssetPostprocessor | 固定导入管线 + `workbookImported` 事件 | Δ1,管线细节归模块 4 |
| 子资产(sub-asset) | 无 | 相关成员整族不提供(§8) |
| 标签(任意资产可挂) | schema 声明的 labels 字段 | M1§2,Δ8 |

- 失败形态(全门面统一,样例不逐处重复):
  - 模式矩阵(P§7.8)禁用的调用 → 抛 `InvalidOperationException`。
  - 参数/状态失败 → 按签名返回失败值(`null`/`false`/空串/错误串),同时投 `Diagnostic`(P§9)进当次操作报告;禁止静默 no-op 与部分写入。
  - `void` 成员失败 = Diagnostic + 状态不变;`SaveAssets` 恒产出 OperationReport(P§6.5)。
- 全集封闭:§8 签名块 + 类型补充 + 不提供表 = 本门面全集;未列出的 Unity 成员视为不提供,新增必须过 §8。

## 2. asset path 与 GUID

- 路径文法:`<workbook 路径>/<表名>/<key>`。workbook 路径 = 项目根相对、`/` 分隔(Unity 工程下即 `Assets/...`);表名段 = message 名(`TableDescriptor.Name`,M1§5;`sheet_name` 只是 Excel 展示);其后剩余整串 = key,复合 key 段按 key 序以 `/` 拼接(P§4.1 约定沿用)。大小写敏感。
- 解析规则:先最长匹配已挂载 workbook 路径,再匹配表名段,剩余 = key——因此 key 含 `/` 不产生歧义。
- 路径是展示与查找信息(P§5.3),随改名/移动而变;GUID 是身份,跨改名、移动、保存、切源稳定——与 Unity"路径易变、GUID 稳定"同构,工具持久存储一律存 GUID。
- `GUID` = 128 位值类型;串形 = 32 个十六进制小写字符;`TryParse`/`ToString`/`Empty()` 同 Unity 形态。UnityResourceRef 里的 Unity `.meta` guid 是另一命名空间(`UnityGuid`,P§6.7),本门面不解析。

```csharp
using ExcelDbEngine;                        // Core 值类型(RuntimeQueryStatus 等;无 Object 基类,Δ11)
using ExcelDbEditor;                        // ↔ UnityEditor
using Game.Configs;                         // M1§6 生成类型

const string Game     = "Assets/Configs/game.xlsx";
const string Dlc      = "Assets/Configs/game_dlc1.xlsx";
const string Fireball = Game + "/SkillConfig/fireball";

var skill = AssetDatabase.LoadAssetAtPath<SkillConfig>(Fireball);
Assert(skill.Common.Id == "fireball");                       // key 字段即身份展示;无 name 投影(Δ11)
Assert(AssetDatabase.GetAssetPath(skill) == Fireball);
Assert(AssetDatabase.Contains(skill));                       // 已持久化(同 Unity)

// GUID 三函数,string 与结构体双形态(同 Unity)
string hex  = AssetDatabase.AssetPathToGUID(Fireball);       // "7c9e6679f4b04d3ab2c0e8d5f1a2b3c4"
GUID   guid = AssetDatabase.GUIDFromAssetPath(Fireball);
Assert(AssetDatabase.GUIDToAssetPath(hex)  == Fireball);
Assert(AssetDatabase.GUIDToAssetPath(guid) == Fireball);
Assert(AssetDatabase.GUIDToAssetPath("00000000000000000000000000000000") == "");  // 未知 → 空串
Assert(AssetDatabase.GUIDFromAssetPath(Game + "/SkillConfig/nope").Empty());      // 未知 → 空 GUID

// 容器路径(Δ2):workbook 与表是"文件夹"
Assert(AssetDatabase.IsValidFolder(Game));
Assert(AssetDatabase.IsValidFolder(Game + "/SkillConfig"));
Assert(!AssetDatabase.IsValidFolder(Fireball));              // 行是资产,不是容器
Assert(!AssetDatabase.IsValidFolder("Assets/Other/x.xlsx")); // 未挂载
Assert(AssetDatabase.GetMainAssetTypeAtPath(Fireball) == typeof(SkillConfig));
```

## 3. 挂载、刷新与导入事件

- `MountWorkbook`/`UnmountWorkbook` 是 Unity 没有的入口(Δ1):Unity 的搜索域 = 整个工程,ExcelDB 的域 = 显式挂载集。宿主引导(Unity adapter 启动、CLI)按 `ExcelDb.Project.json` 的 `workbooks` glob 逐本挂载(P§3)。
- `Refresh()` 语义同 Unity:扫描外部变化并导入(走 P§6.1 导入、必要时 P§6.3 合并)。watcher(P§6.8)自动触发同一路径,手调是兜底。
- `ImportAsset(path)` 只接受 workbook 路径:单本强制走导入管线;`ImportAssetOptions.ForceUpdate` 忽略内容指纹(Δ9)。
- `workbookImported` 在每次导入完成后派发(挂载、Refresh、ImportAsset、watcher 触发各算一次);Unity 对应物是 `AssetPostprocessor.OnPostprocessAllAssets`,ExcelDB 以事件而非基类交付(Δ1)。
- 卸载时存在未保存 dirty → 失败 + Diagnostic(不丢数据):先 `SaveAssets`,或走模块 4 的撤销/回滚后再卸载。

```csharp
AssetDatabase.workbookImported += report =>
{
    // ImportReport = OperationReport(P§9)的 import 投影,字段细节归模块 4
    if (!report.Ok)
        foreach (var d in report.Diagnostics)
            Console.WriteLine($"{d.Code} {d.Severity}");
};

AssetDatabase.MountWorkbook(Game);
AssetDatabase.MountWorkbook(Dlc);

AssetDatabase.Refresh();                                     // 全部已挂载 workbook
AssetDatabase.ImportAsset(Game);                             // 单本;指纹变化才重导
AssetDatabase.ImportAsset(Game, ImportAssetOptions.ForceUpdate);

AssetDatabase.UnmountWorkbook(Dlc);                          // dirty 未保存 → 失败诊断,保持挂载
```

## 4. 加载与查找

- `LoadAssetAtPath`:未挂载/不存在/类型不符 → `null`(同 Unity);返回实例 = resident object,同 guid 恒同实例(P§7.1)。
- `LoadAllAssetsAtPath`:行路径 → 单元素;表容器路径 → 该表全部行;workbook 容器路径 → 全书行(Δ2:Unity 只接受文件路径)。
- `FindAssets` 返回 GUID 串数组;`searchInFolders` 元素 = 容器路径(Δ2)。返回序确定:表 id 升序、行序(Unity 不保证序;确定序供 golden 测试,Δ6)。
- 过滤文法(同 Unity 文档口径,差异标 Δ6):
  - 词 = 名字子串 | `t:类型名` | `l:标签` | `ref:目标`,空白分词;同键多值 OR,名字多词 AND,异键之间 AND。
  - 名字子串匹配 key(不区分大小写);`display_name` 不参与(那是 UI 检索的事)。
  - `t:` 匹配生成类型名;类型无继承层级(Δ11),`implements` 组名可作 `t:` 目标(M1§2,组内各表全部行);全部行 = 省略 `t:` 词(空 filter 配 `searchInFolders`,同 Unity);EMBEDDED/匿名形状不是资产,匹配不到(M1§3)。
  - `l:` 匹配 labels 字段值(Δ8)。
  - `ref:` 为 ExcelDB 扩展(Unity 的 `ref:` 属 Search 窗口而非 FindAssets):接完整行路径或 `表名/key` 短形,返回直接引用者。
  - 不支持 `a:`/`b:`/`glob:`(无 packages 域、无 AssetBundle,Δ6);未知类型/标签 → 空数组(同 Unity,静默)。

```csharp
Assert(AssetDatabase.LoadAssetAtPath<ItemConfig>(Fireball) == null);   // 类型不符 → null(同 Unity)

// 枚举一张表:Unity 惯用两步(GUID → 路径 → 加载)
foreach (var g in AssetDatabase.FindAssets("t:SkillConfig"))
{
    var path = AssetDatabase.GUIDToAssetPath(g);
    var s    = AssetDatabase.LoadAssetAtPath<SkillConfig>(path);
    Console.WriteLine(s.Common.Id);
}

var inDlc    = AssetDatabase.FindAssets("t:SkillConfig", new[] { Dlc });   // 限一本 workbook
var inTable  = AssetDatabase.FindAssets("nova", new[] { Game + "/SkillConfig" });
var bossFire = AssetDatabase.FindAssets("fire t:SkillConfig l:boss");      // 名字 ∧ 类型 ∧ 标签
var refs     = AssetDatabase.FindAssets("ref:SkillConfig/fireball_2");     // 谁直接引用 fireball_2

object[] whole = AssetDatabase.LoadAllAssetsAtPath(Game);                  // 全书行(Δ2)
```

## 5. 创建、删除、改名、移动、复制

- 落盘时机是本组最大偏差(Δ3):全部结构操作即时生效于内存与索引,统一在 `SaveAssets` 经合并-补丁管线落盘(P§6.5);Unity 是即时写盘。样例注释中的"落盘"皆指此。AssetDatabase 结构操作不进 Undo 栈(同 Unity)。
- `CreateAsset`:类型面 = ASSET 表的生成类(M1§3);实例用 `new` 创建(普通 C# 类,Δ11;Unity 为 `CreateInstance`),类型合法性 = SchemaRegistry 已注册,运行期校验,未注册类型 → error。path 表段必须已挂载且 sheet 已生成(缺 sheet → error,提示先跑结构生成 P§6.6)。path 末段覆写 key 字段(同 Unity 主资产名随文件名);guid 即时分配(内存态),落盘走身份规则 1(P§5.3)。已存在同 key → error + no-op(Δ4:Unity 直接覆写);key 唯一域 = 目标表、跨 workbook(P§8.1)。已持久化实例再 CreateAsset → error(同 Unity)。
- `DeleteAsset` 返回是否删除,行为按持有方 RowRef 的 `delete_policy`(M1§2):引用闭包内有 BLOCK → `false` + 引用者清单诊断;SET_NULL/CASCADE → 按删除计划执行 → `true`;删除后 resident 实例进入 missing 态(P§7.1 语义;POCO 类型面下的承载方式归模块 5)。`DeleteAssets` 全部成功才 `true`,失败路径落 `outFailedPaths`(同 Unity)。
- `RenameAsset` 改 key:返回空串 = 成功,否则错误串(同 Unity);guid 不变,全部引用 cell 自动改写并进保存计划(P§8.1),路径随 key 变化。
- `MoveAsset` 跨 workbook 迁移行(guid 与引用不变);末段变化兼作改名(同 Unity);表段变化/目标未挂载/目标缺 sheet → 错误串。
- `CopyAsset` = 复制即新资产:新行新 guid(同 Unity 复制语义),行内 RowRef 照抄、仍指原目标;目标已存在 → `false`。
- `StartAssetEditing`/`StopAssetEditing`:计数式可嵌套、必须 try/finally 配对(同 Unity);计数 > 0 时 watcher 合并、索引重建与写回全部挂起,归零时一次性聚合;长期不配对 = 挂起 + `assetdb.editing_unbalanced` warning。
- 生成类无 `name` 成员(Δ11):已持久化行的 key 变更走 `RenameAsset`;key 字段被直接改写(绕过 RenameAsset)的判定与归一属导入/编辑门面,归模块 4(§11)。

```csharp
// 创建:new → 填字段 → CreateAsset(Unity 惯用序的 C# 化:普通类直接 new,Δ11)
var nova = new SkillConfig();
nova.Common.Id = "ice_nova";                                 // key 草稿(未持久化可直写)
nova.Damage    = 80;
Assert(!AssetDatabase.Contains(nova));                       // 尚未持久化(同 Unity)

AssetDatabase.CreateAsset(nova, Game + "/SkillConfig/ice_nova");   // 末段为准覆写 key(同 Unity 随文件名)
Assert(AssetDatabase.Contains(nova) && nova.Common.Id == "ice_nova");
Assert(!AssetDatabase.GUIDFromAssetPath(Game + "/SkillConfig/ice_nova").Empty());  // guid 已分配(内存态)
Assert(ReferenceEquals(nova,
    AssetDatabase.LoadAssetAtPath<SkillConfig>(Game + "/SkillConfig/ice_nova")));  // 实例即 resident(P§7.1)

// 重复 key:Unity 直接覆写,ExcelDB 拒绝(Δ4)
var other = new SkillConfig();
AssetDatabase.CreateAsset(other, Game + "/SkillConfig/ice_nova");  // Diagnostic error + no-op
var unique = AssetDatabase.GenerateUniqueAssetPath(Game + "/SkillConfig/ice_nova");
Assert(unique == Game + "/SkillConfig/ice_nova_1");                // Δ5:'_' 分隔(Unity 缺省空格)

// 改名 = 改 key:空串成功,错误串失败(同 Unity);guid 稳定;引用 cell 全改写(P§8.1)
var g0 = AssetDatabase.GUIDFromAssetPath(Game + "/SkillConfig/ice_nova");
Assert(AssetDatabase.RenameAsset(Game + "/SkillConfig/ice_nova", "frost_nova") == "");
Assert(AssetDatabase.GUIDFromAssetPath(Game + "/SkillConfig/frost_nova") == g0);
Assert(AssetDatabase.RenameAsset(Game + "/SkillConfig/frost_nova", "fireball") != "");  // key 冲突

// 移动:跨 workbook 迁移(同表);末段变化兼作改名(同 Unity)
Assert(AssetDatabase.MoveAsset(
    Game + "/SkillConfig/frost_nova", Dlc + "/SkillConfig/frost_nova") == "");
Assert(AssetDatabase.MoveAsset(Fireball, Game + "/ItemConfig/fireball") != "");         // 表段变化

// 复制:新行新 guid(同 Unity);RowRef 照抄
Assert(AssetDatabase.CopyAsset(Fireball, Game + "/SkillConfig/fireball_copy"));
Assert(AssetDatabase.GUIDFromAssetPath(Game + "/SkillConfig/fireball_copy")
    != AssetDatabase.GUIDFromAssetPath(Fireball));

// 删除 × delete_policy(M1§2):sword 被 rewards/drops 以 BLOCK(默认)引用
Assert(!AssetDatabase.DeleteAsset(Game + "/ItemConfig/sword"));    // false + 引用者清单诊断

// 批量:挂起 watcher/写回,Stop 归零时聚合(同 Unity 计数式,必须 finally 配对)
AssetDatabase.StartAssetEditing();
try
{
    for (var i = 0; i < 100; i++)
        AssetDatabase.CreateAsset(new SkillConfig(), $"{Game}/SkillConfig/gen_{i}");
}
finally { AssetDatabase.StopAssetEditing(); }

var failedPaths = new List<string>();
AssetDatabase.DeleteAssets(
    new[] { Game + "/SkillConfig/gen_0", Game + "/SkillConfig/gen_1" }, failedPaths);

AssetDatabase.SaveAssets();   // Δ3:以上全部至此才落盘(合并 → 补丁写回 → 复读 → 原子替换,P§6.5)
```

## 6. 保存、依赖、标签与打开

- `SaveAssets` = P§6.5 事务全序(preflight 合并 → 写回计划 → 补丁写回 → 复读校验 → 原子替换 → 快照与 `__rev`);`SaveAssetIfDirty` 把写回计划收窄到单行(该行所在 workbook,仅该行 cell + metadata)。
- 字段编辑与 dirty 语义(`SerializedObject`/`Undo`/`EditorUtility.SetDirty`)是模块 4 的门面,样例仅示意闭环。
- `GetDependencies`:依赖 = 该行经 RowRef 引用的行(子表、单 cell、weighted 内的 RowRef 都归属父行);`recursive` 缺省 true 且结果含输入自身(同 Unity),false = 仅直接依赖、不含自身。UnityResourceRef/LocalizedTextRef 目标不是 ExcelDB 资产,不进结果(Δ7)。逆向查询 = `FindAssets("ref:…")`。
- 标签:`GetLabels`/`SetLabels`/`ClearLabels` 读写 labels 字段(M1§2 `labels: true`,样例 schema 即 `common.tags`);Set/Clear 走正常 dirty/保存(P§4.7)。无 labels 字段的表:Get → 空数组,Set/Clear → error(Δ8:Unity 标签存 .meta,任意资产可用)。
- `OpenAsset` 用系统关联程序打开所在 workbook(Excel),尽力定位 sheet 与行(adapter 实现,Δ10)。

```csharp
var fireball = AssetDatabase.LoadAssetAtPath<SkillConfig>(Fireball);

fireball.Damage = 150;
EditorUtility.SetDirty(fireball);                            // 模块 4 门面;ApplyModifiedProperties 自动含此
AssetDatabase.SaveAssetIfDirty(fireball);                    // 只写 fireball 一行
AssetDatabase.SaveAssetIfDirty(AssetDatabase.GUIDFromAssetPath(Fireball));   // GUID 重载(同 Unity)

var closure = AssetDatabase.GetDependencies(Fireball);       // 含自身 + 传递闭包(同 Unity recursive 缺省)
var direct  = AssetDatabase.GetDependencies(Fireball, false);
// direct → [".../SkillConfig/fireball_2", ".../ItemConfig/sword", ".../ItemConfig/potion"]
//   (next_rank 与 rewards/drops 子表内的 RowRef;序 = 表 id、行序)
// icon(UnityResourceRef)与 display_name(LocalizedTextRef)不进结果(Δ7)

var labels = AssetDatabase.GetLabels(fireball);              // → ["boss", "fire"](common.tags)
AssetDatabase.SetLabels(fireball, new[] { "boss", "fire", "s1" });   // 写字段 + dirty
Assert(AssetDatabase.FindAssets("l:s1").Length == 1);
AssetDatabase.ClearLabels(fireball);

AssetDatabase.OpenAsset(fireball);                           // Excel 打开 game.xlsx,定位尽力(Δ10)
```

## 7. 冲突

- 冲突来自三方合并(P§6.3):外部修改撞上本地 dirty。`GetConflicts` 是 buffer 填充形态(同 P§7.2 `GetAssets`):`Truncated` 时 `count` = 所需总数;存在未解决冲突时 `SaveAssets` 产出失败报告、零写入。
- `ResolveConflict`:`ReloadFromExcel` 弃本地取外部,`KeepEditorValue` 以外部为新 base 保留本地。Unity 无对应成员(Δ1);Unity adapter 的冲突对话框(P§11)是本 API 的 UI 投影。

```csharp
var conflicts = new ConflictRecord[64];
if (AssetDatabase.GetConflicts(conflicts, out var total) == RuntimeQueryStatus.Truncated)
{
    conflicts = new ConflictRecord[total];                   // Truncated:count = 所需总数
    AssetDatabase.GetConflicts(conflicts, out total);
}

for (var i = 0; i < total; i++)
{
    var c = conflicts[i];                                    // { id, assetIdentity, fieldPath }(P§6.3)
    AssetDatabase.ResolveConflict(c.id,
        c.fieldPath == "damage" ? ConflictResolutionAction.KeepEditorValue
                                : ConflictResolutionAction.ReloadFromExcel);
}
AssetDatabase.SaveAssets();                                  // 全部解决后方可写盘(P§6.3)
```

## 8. 成员总表与偏差

标记:`=U` = 语义按 Unity 2022.3 同名成员继承,不复述;`Δn` = 偏差表第 n 行。签名全集如下,新增成员必须过本表:

```csharp
namespace ExcelDbEditor;   // 资产实参 = SchemaRegistry 已注册的生成类实例,签名以 object/泛型承接(Δ11)

public static class AssetDatabase
{
    // ---- 挂载(Δ1:Unity 无 mount 概念,域 = 显式挂载集) ----
    public static void MountWorkbook(string path);
    public static void UnmountWorkbook(string path);                        // dirty 未保存 → 失败诊断

    // ---- 刷新与导入 ----
    public static void Refresh(ImportAssetOptions options = ImportAssetOptions.Default);      // =U Δ9
    public static void ImportAsset(string path, ImportAssetOptions options = ImportAssetOptions.Default);
                                                                            // =U(仅 workbook 路径)Δ9
    public static event Action<ImportReport> workbookImported;              // Δ1(↔ AssetPostprocessor)

    // ---- 加载 ----
    public static T LoadAssetAtPath<T>(string assetPath) where T : class;   // =U(约束 Object → class,Δ11)
    public static object LoadAssetAtPath(string assetPath, Type type);      // =U Δ11
    public static object[] LoadAllAssetsAtPath(string assetPath);           // Δ2 Δ11(容器路径可用)
    public static Type GetMainAssetTypeAtPath(string assetPath);            // =U

    // ---- 查找 ----
    public static string[] FindAssets(string filter);                       // Δ6
    public static string[] FindAssets(string filter, string[] searchInFolders);   // Δ2 Δ6
    public static bool Contains(object obj);                                // =U Δ11
    public static string GetAssetPath(object assetObject);                  // =U(未持久化 → 空串)
    public static bool IsValidFolder(string path);                          // Δ2

    // ---- GUID ----
    public static string AssetPathToGUID(string path);                      // =U
    public static GUID GUIDFromAssetPath(string path);                      // =U
    public static string GUIDToAssetPath(string guid);                      // =U
    public static string GUIDToAssetPath(GUID guid);                        // =U

    // ---- 结构操作(落盘统一在 SaveAssets,Δ3) ----
    public static void CreateAsset(object asset, string path);              // Δ4(不覆写)Δ11
    public static bool DeleteAsset(string path);                            // =U + delete_policy(M1§2)
    public static bool DeleteAssets(string[] paths, List<string> outFailedPaths);  // =U
    public static string RenameAsset(string pathName, string newName);      // =U(空串 = 成功)
    public static string MoveAsset(string oldPath, string newPath);         // =U(跨 workbook 迁移)
    public static bool CopyAsset(string path, string newPath);              // =U(新 guid)
    public static string GenerateUniqueAssetPath(string path);              // Δ5
    public static void StartAssetEditing();                                 // =U(计数式,须配对)
    public static void StopAssetEditing();                                  // =U

    // ---- 保存 ----
    public static void SaveAssets();                                        // =U(P§6.5 事务)
    public static void SaveAssetIfDirty(object obj);                        // =U(单行粒度)
    public static void SaveAssetIfDirty(GUID guid);                         // =U

    // ---- 依赖与标签 ----
    public static string[] GetDependencies(string pathName);                // =U(recursive = true)Δ7
    public static string[] GetDependencies(string pathName, bool recursive);// =U Δ7
    public static string[] GetLabels(object obj);                           // Δ8
    public static void SetLabels(object obj, string[] labels);              // Δ8
    public static void ClearLabels(object obj);                             // Δ8

    // ---- 打开 ----
    public static bool OpenAsset(object target);                            // Δ10

    // ---- 冲突(Δ1:Unity 无) ----
    public static RuntimeQueryStatus GetConflicts(Span<ConflictRecord> buffer, out int count);
    public static bool ResolveConflict(ConflictId id, ConflictResolutionAction action);
}
```

类型补充:

```csharp
public enum ImportAssetOptions { Default = 0, ForceUpdate = 1 }   // Δ9:其余 Unity 枚举值不提供
public readonly struct GUID { /* 128 位;TryParse / ToString(32 hex 小写)/ Empty() 同 Unity 形态 */ }
// ImportReport = OperationReport(P§9)的 import 投影(字段细节归模块 4);
// ConflictRecord / ConflictId / ConflictResolutionAction / RuntimeQueryStatus 沿用 P§6.3/§6.7,不重定义。
```

偏差表(像素级对齐的全部例外;样例内以 Δn 引用):

| Δ | 偏差 | 理由 / 仲裁 |
| --- | --- | --- |
| Δ1 | `MountWorkbook`/`UnmountWorkbook`/`workbookImported`/`GetConflicts`/`ResolveConflict`:Unity 无对应成员 | 域 = 显式挂载集(P§3),无"整个工程"概念(核 11);Excel 双主编辑(核 6)必然产生导入报告与冲突面;`workbookImported` ↔ `AssetPostprocessor.OnPostprocessAllAssets` |
| Δ2 | 路径伸进 xlsx:workbook/表为"文件夹"(`IsValidFolder`、`searchInFolders`、`LoadAllAssetsAtPath` 容器可用);Unity 路径止于文件 | 行是唯一资产粒度,容器顺势成"文件夹",免去子资产模型与新容器类型(D3) |
| Δ3 | 结构操作与编辑即时生效于内存/索引,落盘统一 `SaveAssets`;Unity 即时写盘 | xlsx 是共享文档:写回必须走合并-补丁-复读-原子替换事务(P§6.5),散写会撕裂 workbook 并绕过冲突门禁 |
| Δ4 | `CreateAsset` 不覆写已存在 key(error + no-op);Unity 直接覆写已有资产 | 仲裁序(P§1):不丢数据 > 身份稳定 > Unity 手感;覆写 = 静默丢行 |
| Δ5 | `GenerateUniqueAssetPath` 后缀 `_N`;Unity 缺省 ` N`(空格,`EditorSettings.assetNamingUsesSpace`) | key 进 RowRef cell token 与路径,空格徒增引号转义 |
| Δ6 | `FindAssets`:扩展 `ref:`;不支持 `a:`/`b:`/`glob:`;`t:` 无派生匹配(生成类无层级,Δ11),`implements` 组名可作 `t:` 目标;返回序确定(表 id、行序) | 无 packages 域与 AssetBundle;`ref:` 由依赖图直供(P§6.1);组即 ExcelDB 的"多态"面(M1§2);确定序供 golden 测试 |
| Δ7 | `GetDependencies` 只走 RowRef 图;数组重载不提供 | UnityResourceRef/LocalizedTextRef 目标不是 ExcelDB 资产(P§8.2/8.3);数组重载调用侧可组合 |
| Δ8 | 标签依赖 schema 的 labels 字段;无字段表 Get → 空、Set/Clear → error | 标签是数据列不是 .meta 附件(M1§2):进 schema、进版本控制、可被 Excel 直编 |
| Δ9 | `ImportAssetOptions` 仅 `Default`/`ForceUpdate` | 其余 Unity 值绑定其原生 importer 体系,无对应物 |
| Δ10 | `OpenAsset` 仅 `object` 重载,行定位尽力(adapter);行号/列号重载不提供 | xlsx 无行号语义;定位能力因宿主与 Excel 版本而异 |
| Δ11 | 生成类是普通 C# 类:不派生 `Object`/`ScriptableObject`,无 `name`/`GetInstanceID` 成员;创建用 `new`(Unity 为 `CreateInstance`);facade 资产参数 = `object`/泛型 `where T : class`,类型合法性 = SchemaRegistry 注册,运行期校验 + Diagnostic(编译期基类约束不可得) | 库定位 Unity 无关(核 11):Unity 手感限于 facade 用法,不复刻数据基类——生成类不占用户继承位、可在任意宿主(服务端/CLI/Unity)直接消费;key 字段本体即名,resident/missing 语义由库侧注册表承载(模块 5) |

不提供清单(Unity 成员 → 一句理由;不在签名块与本表的成员一律视同此表):

| Unity 成员(族) | 不提供的理由 |
| --- | --- |
| `CreateFolder` | 容器由 schema 与结构生成建立(M1、P§6.6) |
| `MoveAssetToTrash` / `MoveAssetsToTrash` | 行不是 OS 文件;删除走 `DeleteAsset` + 版本控制回收 |
| `AddObjectToAsset` / `RemoveObjectFromAsset` / `SetMainObject` / `ExtractAsset` / `IsMainAsset` / `IsSubAsset` / `LoadMainAssetAtPath` / `LoadAllAssetRepresentationsAtPath` | 无主/子资产模型:行是唯一粒度(Δ2) |
| `GetAllAssetPaths` / `GetAssetPath(int instanceID)` | 无 instanceID 命名空间(Δ11);全量枚举 = 空过滤 `FindAssets`,对象反查 = `GetAssetPath(object)` |
| `TryGetGUIDAndLocalFileIdentifier` | 无 localId 命名空间,GUID 即行身份(P§5.3) |
| `GetAssetDependencyHash` | 内容指纹在导入快照与 convert manifest(P§6.2/§7.4) |
| `ForceReserializeAssets` | canonical 重写 = normalize 与结构生成(M1§2、P§6.6) |
| `IsOpenForEdit` 族 / meta 文件族(`GetTextMetaFilePathFromAssetPath` 等) | 无 VCS checkout 概念,写锁 = OS 文件锁 + 保存前比对(P§6.5);.meta 替代 = 伴随列 + metadata sheet(P§5.1/5.2) |
| `GetCachedIcon` | UI 投影归 Unity adapter(P§11) |
| `ImportPackage` / `ExportPackage` | xlsx 文件本身即交换格式 |
| AssetBundle 族 / importer 定制族(`SetImporterOverride`、`GetAvailableImporters` 等) | 无 bundle 与可定制导入器;出包 = convert(P§7.4/§10) |
| `RefreshSettings` / `WriteImportSettingsIfDirty` / `ReleaseCachedFileHandles` / `CanOpenAssetInEditor` / `OpenAsset(line, column)` 重载 | 无 importer 设置层、句柄缓存与行号语义(Δ10) |

## 9. 模块验收测试

样例内每条 `Assert` 与期望注释都是断言;下列为组织后的测试面:

1. 路径与 GUID:三函数往返;未知输入的空值形态(空串/空 GUID/null);GUID 串 = 32 hex 小写;改名/移动/保存/切源后 guid 不变。
2. 容器:`IsValidFolder` 四态(workbook/表/行/未挂载);`GetMainAssetTypeAtPath`;`LoadAllAssetsAtPath` 三种粒度与确定序。
3. 加载生命周期:类型不符 → null;`new` 实例 `Contains` false、`GetAssetPath` 空串;`CreateAsset` 后翻转;`UnmountWorkbook` 后 → null;同 guid 恒同实例。
4. FindAssets 文法矩阵:名字多词 AND;`t:` 类型名/`implements` 组名与多值 OR(Δ11);空 filter = 全部行;`l:` OR;`ref:` 两种目标形;`searchInFolders` 两级域;异键 AND;未知类型/标签 → 空数组;返回序确定。
5. CreateAsset:末段覆写 key 字段;guid 内存态分配、落盘写 `__guid`(P§5.3 规则 1);重复 key 拒绝(Δ4);缺 sheet/未挂载/已持久化实例/未注册类型的失败形态;key 唯一域跨 workbook(P§8.1)。
6. GenerateUniqueAssetPath:无冲突原样返回;`_N` 递增(Δ5)。
7. RenameAsset:空串/错误串双形态;guid 不变;引用 cell 全改写并进保存计划(P§8.1);旧路径 → null、新路径可达。
8. MoveAsset:跨 workbook 后 guid 与引用不变;末段改名等价 RenameAsset;表段变化/未挂载/缺 sheet → 错误串。
9. CopyAsset:新 guid;RowRef 照抄指原目标;目标已存在 → false。
10. DeleteAsset(s) × delete_policy:BLOCK → false + 引用者清单;SET_NULL 清格 + dirty;CASCADE 影响集且闭包内 BLOCK 优先(M1§2);`outFailedPaths` 精确;删除后 resident 进入 missing 态(P§7.1 语义,承载归模块 5)。
11. 批量编辑:计数嵌套;挂起 watcher/写回;归零聚合一次;不配对 → 挂起 + warning。
12. 保存:Δ3 时机(SaveAssets 前 xlsx 字节不变);`SaveAssetIfDirty` 单行写回计划与 GUID 重载;SaveAssets 全序与失败报告(P§6.5)。
13. 依赖:recursive 含自身、直接不含;子表/单 cell 内 RowRef 归属父行;UnityResourceRef/LocalizedTextRef 不进(Δ7);与 `ref:` 查询互逆。
14. 标签:Get/Set/Clear 往返;与 `l:` 一致;dirty 进保存;无 labels 字段的形态(Δ8)。
15. 冲突:Truncated 语义(count = 总数);两种 Resolve 动作;未解决冲突门禁 SaveAssets(P§6.3)。
16. 事件与失败形态:`workbookImported` 四个触发面各一;模式矩阵禁用抛 `InvalidOperationException`(P§7.8);普通失败 = 失败返回值 + Diagnostic、无静默 no-op;未注册类型/非生成类 object 传入 facade 的失败形态(Δ11)。

## 10. 与仓库现状衔接

- 本文取代 P§6.7:`RenameAsset` 返回值 bool → string(像素修正);新增 `Contains`/`GetAssetPath`/`IsValidFolder`/`LoadAllAssetsAtPath`/`GetMainAssetTypeAtPath`/`ImportAsset`/`MoveAsset`/`CopyAsset`/`DeleteAssets`/`GenerateUniqueAssetPath`/`StartAssetEditing`/`StopAssetEditing`/`SaveAssetIfDirty`/`GetDependencies`/`ClearLabels`/`OpenAsset`/`GUIDFromAssetPath`;`FindAssets` 文法与 `GUID` 家族细化;`GUID`/`UnityGuid` 双命名空间结论沿用。`exceldb-lean-plan.md` 与 `exceldb-implementation.md` 头部注记已同步指向本文。
- 生成类型面改为普通 C# 类(D4/Δ11):M1§3/§6 已同步修订;P§7.1 的 `Object`/`ScriptableObject` 基类模型作废,`AssetKey`/`AssetIdentity` 与 resident/missing 语义保留、承载方式归模块 5;poc `CSharpEmitter` 已改为无基类发射。
- 样例类型基底 = M1§4 proto 经 codegen 的 `Game.Configs`(poc 已生成同名类型 `SkillConfig.Damage`/`Common.Id`,见 `poc/SchemaPoc.GeneratedCheck/Generated/GameConfigs.g.cs`)。
- 本模块仅文档,无代码交付(评审要求);实施对应实施文档 §3-M3,其 3.1"AssetDatabase facade 全签名(P§6.7)"读作指向本文。

## 11. 待后续模块决定的边界

- workbook 物理契约:三行表头、下拉、批注、metadata sheet 细节、RowRef cell token、`RowRef.id` 与行身份(guid/表内 id)的关系 → 模块 3(M1§9 预派给"模块 2"的议题,编号顺延)。
- 导入管线细节(校验时机、诊断码全集、`ImportReport` 字段、快照格式)与 `SerializedObject`/`Undo`/`EditorUtility` 门面 → 模块 4;key 字段经字段/属性直改(绕过 `RenameAsset`)的判定与归一(视作 rename 还是校验拒绝)也在该处定。
- POCO 类型面下 resident 实例语义的承载(同 guid 恒同实例、missing 态的暴露方式:注册表/弱表/生成 partial 成员)→ 模块 5(D4/Δ11)。
- Play Mode 下 AssetDatabase 写回与 RuntimeDatabase 热载的接线、ChangeSet 与 `workbookImported` 的派发次序 → 模块 5 / Unity adapter。
- `FindAssets` 的 label/ref 反查索引与性能预算、`OpenAsset` 行定位实现 → 实施文档 / Unity adapter。

## 12. 决策记录

约定沿用 M1§10:引用块 = 评审原话;一句加粗 = 关键洞察;落点一行。

---

### D1(2026-07-09)模块 2 = AssetDatabase,使用样例即规格

> 生成 AssetDatabase 的使用样例 ExcelDB. 要求像素级参考 Unity 的相关 API. 这是第二个模块. 仅生成文档, 不需要有实际完整功能的代码.

**API 门面先于机制成模:像素级使用样例就是规格与验收基线——先锁死对外手感,workbook/导入/合并转为门面之下的实现细节,不再反向塑形 API;模块编号就此顺延(workbook 与身份 → 模块 3)。**

落点:全文样例 + §9 测试;状态行的编号顺延声明;§1"样例即规格"与全集封闭规则。

---

### D2(2026-07-09)像素对齐的上限 = 仲裁序

推演自发:Unity 的 `CreateAsset` 覆写已存在路径并换新 GUID、结构操作即时写盘——照搬即静默丢行、断引用、绕过合并门禁。

**像素级对齐的对象是签名与调用形态,不是事故语义:语义与仲裁序(P§1"不丢数据 > 身份稳定 > … > Unity 手感")冲突处,保留形状、改失败与时机——CreateAsset 拒绝覆写(Δ4),一切落盘收敛到 SaveAssets 的合并-补丁事务(Δ3);每处例外必须落偏差表,偏差表之外不得偏离 Unity。**

落点:§5 样例、§8 Δ3/Δ4、§1 失败形态约定。

---

### D3(2026-07-09)行 = 资产,workbook/表 = 文件夹

推演自发:Unity 的资产粒度是文件,ExcelDB 的"资产"住在文件内部——路径要不要伸进 xlsx?

**行是唯一资产粒度,workbook 与表映射为"文件夹"而非主资产:路径伸进文件后,`searchInFolders`/`IsValidFolder`/`LoadAllAssetsAtPath` 全部顺势成立,子资产 API 整族裁掉,无需新增容器类型。**

落点:§2 路径文法与解析规则、Δ2、§8 不提供表(子资产族)。

---

### D4(2026-07-10)生成类 = 普通 C# 类,Unity 手感止于 facade 用法

> 澄清, 本库定位为 Unity 无关的库, 只是用法引用了 C#库. 因此样例中的 直接让导出类实现自 ScriptableObject 是不符合要求的.

**Unity 手感的边界是"用法"不是"类型系统":facade 的成员名与调用形态照 Unity,数据类不背 Unity 的对象模型——生成类是普通 C# 类,`new` 即创建,不占用户继承位,任意宿主直接消费;资产身份与 resident/missing 语义由 SchemaRegistry/库侧注册表承载,类型合法性从编译期基类约束后移为运行期注册校验。**

落点:Δ11 重写(原"name 禁写"条随 `name` 成员一并消亡,key 直改判定仍归模块 4);§8 签名 `Object` → `object`/`where T : class`;§1 概念映射与 §2/§4/§5 样例改 `new` 与 key 字段断言;M1§3/§6 同步修订;P§7.1 基类模型作废,resident 承载归模块 5(§11)。
