using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using SchemaPoc;

try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 忽略重定向环境 */ }

// ---- 路径 ----
var protosRoot = Path.Combine(AppContext.BaseDirectory, "Protos");
var repoRoot = FindRepoRoot();
var outDir = Path.Combine(repoRoot, "poc", "out");
var generatedCs = Path.Combine(repoRoot, "poc", "SchemaPoc.GeneratedCheck", "Generated", "GameConfigs.g.cs");
Directory.CreateDirectory(outDir);
Directory.CreateDirectory(Path.GetDirectoryName(generatedCs)!);

// ---- 1. proto → descriptor set → SchemaDesc ----
Console.WriteLine("== 1. schema 编译 ==");
var dsBytes = SchemaCompiler.CompileDescriptorSet(protosRoot, "game/game.proto");
var schema = SchemaCompiler.Compile(dsBytes);

foreach (var lint in schema.Lints) Console.WriteLine("  lint: " + lint);
if (schema.HasBlocker) { Console.WriteLine("存在 blocker,终止。"); return 2; }

Console.WriteLine($"  schema_hash = 0x{schema.SchemaHash:X16}");
foreach (var t in schema.Tables.OrderBy(t => t.Id))
{
    Console.WriteLine($"  表 {t.Id} {t.Name}({t.DisplayName})key = {string.Join("/", t.KeyFields.Select(k => k.Name))}");
    foreach (var c in ExcelEmitter.PlanColumns(schema, t.Fields, "", ""))
        Console.WriteLine($"    列 {c.Path,-28} {c.TypeDisplay}");
    foreach (var f in t.Fields.Where(f => f.Shape is ValueShape.ChildTable or ValueShape.Weighted))
        Console.WriteLine($"    子表 {t.Name}.{f.Name}({f.Shape})元素字段 = {string.Join(", ", f.Children.Select(c => c.Name))}");
}

// ---- 2. 生成 C# ----
Console.WriteLine("== 2. 生成 C# ==");
var code = CSharpEmitter.Emit(schema);
File.WriteAllText(generatedCs, code, new UTF8Encoding(false));
Console.WriteLine($"  写出 {Rel(generatedCs)}({code.Split('\n').Length} 行)");

// ---- 3. 生成 Excel ----
Console.WriteLine("== 3. 生成 Excel ==");
var xlsxPath = Path.Combine(outDir, "game.xlsx");
var sheets = ExcelEmitter.Emit(schema, SampleData.Default());
XlsxWriter.Write(xlsxPath, sheets);
Console.WriteLine($"  写出 {Rel(xlsxPath)}({sheets.Count} sheets:{string.Join(", ", sheets.Select(s => s.Name + (s.Hidden ? "(隐)" : "")))})");

// ---- 4. 自检:重开 xlsx,校验结构 ----
Console.WriteLine("== 4. xlsx 自检 ==");
var failures = 0;
using (var zip = ZipFile.OpenRead(xlsxPath))
{
    var ns = (XNamespace)"http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    var workbook = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open());
    var names = workbook.Descendants(ns + "sheet").Select(e => (string?)e.Attribute("name")).ToList();
    Check(names.Count == sheets.Count, $"sheet 数 {names.Count} == {sheets.Count}");
    Check(names.Contains("__exceldb") && names.Contains("__exceldb_keys"), "元数据/键清单 sheet 存在");

    var hidden = workbook.Descendants(ns + "sheet").Where(e => (string?)e.Attribute("state") == "hidden").Count();
    Check(hidden == 2, $"隐藏 sheet 数 {hidden} == 2");

    // 校验 SkillConfig sheet 行 2 的字段路径与列计划一致
    var skillIndex = names.IndexOf("SkillConfig") + 1;
    var sheetDoc = XDocument.Load(zip.GetEntry($"xl/worksheets/sheet{skillIndex}.xml")!.Open());
    var row2 = sheetDoc.Descendants(ns + "row").First(r => (string?)r.Attribute("r") == "2");
    var paths = row2.Descendants(ns + "t").Select(t => t.Value).ToList();
    var expected = ExcelEmitter.PlanColumns(schema, schema.Tables.First(t => t.Name == "SkillConfig").Fields, "", "")
        .Select(c => c.Path).Append("__guid").Append("__rev").ToList();
    Check(paths.SequenceEqual(expected), $"SkillConfig 行 2 字段路径({paths.Count} 列)与列计划一致");

    // 数据行 cell 抽查:fireball 的 rewards join 文法
    var row4 = sheetDoc.Descendants(ns + "row").First(r => (string?)r.Attribute("r") == "4");
    var rewardsCol = expected.IndexOf("rewards") + 1;
    var rewardsCell = row4.Elements(ns + "c").FirstOrDefault(c => ((string?)c.Attribute("r"))!.StartsWith(XlsxWriter.ColName(rewardsCol) + "4"));
    Check(rewardsCell?.Descendants(ns + "t").FirstOrDefault()?.Value == "sword&2#potion&5", "fireball.rewards = sword&2#potion&5(join 文法)");

    // 数据有效性存在(枚举 + 引用下拉)
    var validations = sheetDoc.Descendants(ns + "dataValidation").Count();
    Check(validations >= 3, $"SkillConfig 数据有效性 {validations} 条(枚举/union/引用下拉)");
}

Console.WriteLine(failures == 0 ? "== 全部自检通过 ==" : $"== 自检失败 {failures} 项 ==");
return failures == 0 ? 0 : 1;

void Check(bool ok, string what)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
    if (!ok) failures++;
}

string Rel(string path) => Path.GetRelativePath(repoRoot, path);

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "poc")))
        dir = dir.Parent;
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}
