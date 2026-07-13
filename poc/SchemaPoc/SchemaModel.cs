namespace SchemaPoc;

/// <summary>模块 1 §5 描述符模型的 PoC 版本(树形,数字 id 主键)。</summary>
public enum ValueShape
{
    Scalar, Enum, StructSingleCell, StructExpanded, ScalarList, ChildTable, StructListSingleCell,
    Map, Union, Expression, Weighted, Curve, InternalRef, UnityRef, LocalizedRef, Custom,
}

public enum FormatKind { None, Named, Join, Codec }

public sealed class FormatSpec
{
    public FormatKind Kind = FormatKind.None;
    public string[] Separators = [];
    public string PairSeparator = ", ";
    public string KvSeparator = "=";
    public string CodecId = "";
    public override string ToString() => Kind switch
    {
        FormatKind.Join => $"join[{string.Join(" ", Separators)}]",
        FormatKind.Named => $"named('{PairSeparator}','{KvSeparator}')",
        FormatKind.Codec => $"codec({CodecId})",
        _ => "-",
    };
}

public sealed class FieldDesc
{
    public int Id;                       // proto field number
    public string Name = "";             // proto 字段名 = 列路径段 = property path 段
    public string DisplayName = "";
    public string HeaderComment = "";
    public ValueShape Shape;
    public string TypeName = "";         // 标量类型名 / enum 全名 / message 全名
    public string CsTypeName = "";       // 生成的 C# 成员类型(emitter 填)
    public bool Repeated;
    public int KeyOrder;                 // >0 = key 段
    public bool Required, Unique, Labels;
    public string DefaultValue = "", Regex = "";
    public double? Min, Max;
    public string RefTable = "", RefGroup = "";
    public string ExprSymbols = ""; public string ExprResult = "";
    public int WeightField, ConditionField;
    public FormatSpec Format = new();
    public string MapKeyType = "", MapValueType = "";
    public List<FieldDesc> Children = new();     // struct 展开/子表元素/union variants
    public string UnionName = "";                // union(oneof)名
    public string[] ExportTargets = [];           // 已物化、ordinal 排序的 runtime target 集
}

public sealed class TableDesc
{
    public int Id;
    public string Name = "";             // message 名
    public string FullName = "";         // 含 package
    public string DisplayName = "";
    public string SheetName = "";
    public bool IsAsset;
    public bool Retired;
    public string[] Implements = [];
    public string[] Validators = [];
    public string[] ExportTargets = [];           // 已物化、ordinal 排序的 runtime target 集
    public List<FieldDesc> Fields = new();
    public List<FieldDesc> KeyFields = new();
}

public sealed class SchemaDesc
{
    public List<TableDesc> Tables = new();
    public List<TableDesc> Shapes = new();   // 无 table option 的嵌入形状(含符号表/variant),仅供 codegen
    public List<EnumDesc> Enums = new();
    public ulong SchemaHash;
    public List<string> Lints = new();
    public bool HasBlocker => Lints.Any(l => l.StartsWith("[blocker]"));
}

public sealed class EnumDesc
{
    public string Name = "", FullName = "";
    public List<(int Number, string Name, string Display)> Values = new();
}
