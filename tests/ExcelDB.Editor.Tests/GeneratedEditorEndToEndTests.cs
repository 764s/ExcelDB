using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using ExcelDb.Editor.Model;
using ExcelDb.Schema.Compilation;
using ExcelDb.Schema.Generation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace ExcelDb.Editor.Tests;

public sealed class GeneratedEditorEndToEndTests
{
    private static readonly Lazy<Task<GeneratedContract>> Contract =
        new(GeneratedContract.CreateAsync);

    [Fact]
    public async Task Real_generated_contract_preserves_nested_editing_presence_and_reference_semantics()
    {
        var contract = await Contract.Value;
        var resident = contract.CreateConfig();
        contract.MaterializeConfigMessage(resident, "single");
        contract.WriteConfigField(resident, "single.value", 7);

        var binding = new GeneratedBindingFactory().Create(contract.ConfigTable, contract.Tables);
        Assert.Contains(binding.Properties, property => property.PropertyPath == "single");
        Assert.Contains(binding.Properties, property => property.PropertyPath == "single.value");

        var history = new EditorUndoHistory();
        using var serialized = new EditorSerializedObject(
            binding,
            [new EditorEditTarget("config", resident, static () => "r1")],
            history);

        // A single-cell message owns one storage snapshot while keeping its generated leaf editable.
        var singleLeaf = serialized.FindProperty("single.value");
        singleLeaf.BoxedValue = 19;
        Assert.Equal(7, contract.ReadConfigField(resident, "single.value"));
        Assert.Equal(
            EditorApplyStatus.Applied,
            serialized.ApplyModifiedProperties("Edit single-cell leaf").Status);
        Assert.Equal(19, contract.ReadConfigField(resident, "single.value"));
        Assert.Equal(EditorHistoryStatus.Applied, history.TryUndo().Status);
        Assert.Equal(7, contract.ReadConfigField(resident, "single.value"));

        // Expanded message metadata remains inspectable while the optional resident value is null.
        Assert.Null(contract.ReadConfigField(resident, "optional_details"));
        var details = serialized.FindProperty("optional_details");
        Assert.True(details.HasPresence);
        Assert.False(details.HasPresenceValue);
        Assert.Contains(details.Children, child => child.SchemaPropertyPath == "optional_details.amount");
        var amount = serialized.FindProperty("optional_details.amount");
        Assert.Equal(0, amount.BoxedValue);

        details.SetPresence(true);
        Assert.True(details.HasPresenceValue);
        Assert.Null(contract.ReadConfigField(resident, "optional_details"));
        Assert.Equal(
            EditorApplyStatus.Applied,
            serialized.ApplyModifiedProperties("Enable optional details").Status);
        Assert.NotNull(contract.ReadConfigField(resident, "optional_details"));
        Assert.Equal(0, contract.ReadConfigField(resident, "optional_details.amount"));
        Assert.Equal(EditorHistoryStatus.Applied, history.TryUndo().Status);
        Assert.Null(contract.ReadConfigField(resident, "optional_details"));

        amount.BoxedValue = 42;
        Assert.Equal(
            EditorApplyStatus.Applied,
            serialized.ApplyModifiedProperties("Materialize optional details").Status);
        Assert.Equal(42, contract.ReadConfigField(resident, "optional_details.amount"));

        details.SetPresence(false);
        Assert.False(details.HasPresenceValue);
        Assert.Equal(42, contract.ReadConfigField(resident, "optional_details.amount"));
        Assert.Equal(
            EditorApplyStatus.Applied,
            serialized.ApplyModifiedProperties("Clear optional details").Status);
        Assert.Null(contract.ReadConfigField(resident, "optional_details"));
        Assert.Equal(EditorHistoryStatus.Applied, history.TryUndo().Status);
        Assert.Equal(42, contract.ReadConfigField(resident, "optional_details.amount"));
        Assert.Equal(EditorHistoryStatus.Applied, history.TryRedo().Status);
        Assert.Null(contract.ReadConfigField(resident, "optional_details"));

        // Repeated-message elements retain generated RowRef metadata rather than falling back to
        // reflection-only Object children.
        var effects = serialized.FindProperty("effects");
        effects.ArraySize = 1;
        var element = effects.GetArrayElementAtIndex(0);
        var direct = Assert.Single(
            element.Children,
            child => child.SchemaPropertyPath == "effects.direct_target");
        var grouped = Assert.Single(
            element.Children,
            child => child.SchemaPropertyPath == "effects.group_target");
        Assert.Equal(EditorPropertyKind.Reference, direct.Kind);
        Assert.Equal("game.Enemy", direct.ReferenceConstraint!.ReferenceTable);
        Assert.Equal(["game.Enemy"], direct.ReferenceConstraint.AllowedTables);
        Assert.Equal(EditorPropertyKind.Reference, grouped.Kind);
        Assert.Equal("damageable", grouped.ReferenceConstraint!.ReferenceGroup);
        Assert.Equal(["game.Enemy", "game.Prop"], grouped.ReferenceConstraint.AllowedTables);

        var enemyGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var propGuid = Guid.Parse("22222222-2222-2222-2222-222222222222");
        direct.ReferenceValue = new EditorReferenceValue(
            contract.TableId("game.Enemy"),
            "game.Enemy",
            enemyGuid.ToString("D"));
        grouped.ReferenceValue = new EditorReferenceValue(
            contract.TableId("game.Prop"),
            "game.Prop",
            propGuid.ToString("D"));
        Assert.Equal(
            EditorApplyStatus.Applied,
            serialized.ApplyModifiedProperties("Edit repeated RowRefs").Status);
        var effect = Assert.Single(
            ((IList)contract.ReadConfigField(resident, "effects")!).Cast<object>());
        Assert.Equal(
            (contract.TableId("game.Enemy"), enemyGuid),
            contract.ReadRepeatedRowRef(effect!, "effects.direct_target"));
        Assert.Equal(
            (contract.TableId("game.Prop"), propGuid),
            contract.ReadRepeatedRowRef(effect!, "effects.group_target"));
        Assert.Equal(EditorHistoryStatus.Applied, history.TryUndo().Status);
        Assert.Empty((IList)contract.ReadConfigField(resident, "effects")!);

        // This path uses names that collide after C# identifier normalization at two nested levels.
        // The assertion therefore exercises the actual generated MemberPath, not a hand-written fake.
        const string collisionPath = "collision.foo_bar.foo__bar";
        Assert.Contains("_F", contract.MemberPath(collisionPath), StringComparison.Ordinal);
        serialized.FindProperty(collisionPath).BoxedValue = "resolved";
        Assert.Equal(
            EditorApplyStatus.Applied,
            serialized.ApplyModifiedProperties("Edit collision path").Status);
        Assert.Equal("resolved", contract.ReadConfigField(resident, collisionPath));
        Assert.Equal(EditorHistoryStatus.Applied, history.TryUndo().Status);
        Assert.Null(contract.ReadConfigField(resident, "collision"));
    }

    [Fact]
    public async Task Unsupported_generated_property_shapes_do_not_block_supported_fields()
    {
        var contract = await Contract.Value;
        var resident = contract.CreateConfig();
        var binding = new GeneratedBindingFactory().Create(contract.ConfigTable, contract.Tables);
        using var serialized = new EditorSerializedObject(
            binding,
            [new EditorEditTarget("config", resident, static () => "r1")]);

        foreach (var path in new[] { "tuning", "portrait", "title" })
        {
            var property = serialized.FindProperty(path);
            Assert.Equal(EditorPropertyKind.Unsupported, property.Kind);
            Assert.True(property.IsReadOnly);
            Assert.False(string.IsNullOrWhiteSpace(property.UnsupportedReason));
        }

        serialized.FindProperty("display_name").BoxedValue = "Playable";
        Assert.Equal(
            EditorApplyStatus.Applied,
            serialized.ApplyModifiedProperties("Edit supported sibling").Status);
        Assert.Equal("Playable", contract.ReadConfigField(resident, "display_name"));
    }

    private sealed class GeneratedContract
    {
        private const string GeneratedNamespace = "ExcelDb.Generated.Authoring";
        private readonly IReadOnlyDictionary<string, object> _tablesByName;
        private readonly IReadOnlyDictionary<string, object> _configFieldsByPath;

        private GeneratedContract(Assembly assembly)
        {
            var bindings = assembly.GetType(
                GeneratedNamespace + ".GeneratedSchemaBindings",
                throwOnError: true)!;
            Tables = AsObjects(ReadStaticProperty(bindings, "Tables"));
            _tablesByName = Tables.ToDictionary(
                table => ReadProperty<string>(table, "FullName"),
                StringComparer.Ordinal);
            ConfigTable = _tablesByName["game.Config"];
            ConfigType = ReadProperty<Type>(ConfigTable, "ClrType");
            _configFieldsByPath = AsObjects(ReadProperty<object>(ConfigTable, "Fields"))
                .ToDictionary(
                    field => ReadProperty<string>(field, "PropertyPath"),
                    StringComparer.Ordinal);
        }

        public IReadOnlyList<object> Tables { get; }

        public object ConfigTable { get; }

        public Type ConfigType { get; }

        public static async Task<GeneratedContract> CreateAsync()
        {
            using var schema = TemporarySchema.Create();
            schema.Write("generated-editor.proto", SchemaText);
            var result = await new SchemaCompiler().CompileAsync(schema.Path);
            Assert.True(result.Succeeded, Describe(result));

            var generated = new SchemaCodeGenerator().Generate(
                result.Descriptor!,
                PackageSystemProtoCatalog.Default.CatalogHash);
            var authoring = Assert.Single(
                generated.Artifacts,
                artifact => artifact.Kind == "authoring-csharp");
            return new GeneratedContract(CompileAndLoad(authoring.Content));
        }

        public object CreateConfig() => Activator.CreateInstance(ConfigType)!;

        public int TableId(string fullName) =>
            ReadProperty<int>(_tablesByName[fullName], "TableId");

        public string MemberPath(string propertyPath) =>
            ReadProperty<string>(_configFieldsByPath[propertyPath], "MemberPath");

        public object? ReadConfigField(object config, string propertyPath) =>
            ReadMemberPath(config, MemberPath(propertyPath));

        public void WriteConfigField(object config, string propertyPath, object? value) =>
            WriteMemberPath(config, MemberPath(propertyPath), value);

        public void MaterializeConfigMessage(object config, string propertyPath)
        {
            var memberPath = MemberPath(propertyPath);
            var member = ResolveMember(config.GetType(), memberPath);
            WriteMember(member, config, Activator.CreateInstance(MemberType(member)));
        }

        public (int Table, Guid RowGuid) ReadRepeatedRowRef(
            object element,
            string propertyPath)
        {
            var fullPath = MemberPath(propertyPath);
            var separator = fullPath.IndexOf('.');
            Assert.True(separator > 0, $"Expected a repeated child MemberPath, got '{fullPath}'.");
            var rowRef = ReadMemberPath(element, fullPath[(separator + 1)..])!;
            return (
                ReadProperty<int>(rowRef, "Table"),
                ReadProperty<Guid>(rowRef, "RowGuid"));
        }

        private static Assembly CompileAndLoad(string source)
        {
            var trustedAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
            Assert.False(string.IsNullOrWhiteSpace(trustedAssemblies));
            var references = trustedAssemblies!
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(static path => MetadataReference.CreateFromFile(path))
                .ToArray();
            var syntax = CSharpSyntaxTree.ParseText(
                SourceText.From(source, Encoding.UTF8),
                new CSharpParseOptions(LanguageVersion.CSharp9));
            var compilation = CSharpCompilation.Create(
                "ExcelDb.Generated.EditorAcceptance." + Guid.NewGuid().ToString("N"),
                [syntax],
                references,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release,
                    deterministic: true,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var image = new MemoryStream();
            var emit = compilation.Emit(image);
            Assert.True(
                emit.Success,
                string.Join(
                    Environment.NewLine,
                    emit.Diagnostics
                        .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                        .Select(static diagnostic => diagnostic.ToString())));
            image.Position = 0;
            return AssemblyLoadContext.Default.LoadFromStream(image);
        }

        private static IReadOnlyList<object> AsObjects(object value) =>
            ((IEnumerable)value).Cast<object>().ToArray();

        private static object ReadStaticProperty(Type type, string name) =>
            type.GetProperty(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        private static T ReadProperty<T>(object owner, string name) =>
            (T)owner.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!
                .GetValue(owner)!;

        private static object? ReadMemberPath(object owner, string memberPath)
        {
            object? current = owner;
            foreach (var segment in memberPath.Split('.'))
            {
                if (current is null)
                    return null;
                current = ReadMember(ResolveMember(current.GetType(), segment), current);
            }
            return current;
        }

        private static void WriteMemberPath(object owner, string memberPath, object? value)
        {
            var segments = memberPath.Split('.');
            object current = owner;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                current = ReadMember(ResolveMember(current.GetType(), segments[index]), current)
                    ?? throw new InvalidOperationException(
                        $"Member path '{memberPath}' crosses a null value at '{segments[index]}'.");
            }
            WriteMember(ResolveMember(current.GetType(), segments[^1]), current, value);
        }

        private static MemberInfo ResolveMember(Type owner, string name) =>
            (MemberInfo?)owner.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? owner.GetField(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException(owner.FullName, name);

        private static Type MemberType(MemberInfo member) => member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => throw new NotSupportedException(member.MemberType.ToString()),
        };

        private static object? ReadMember(MemberInfo member, object owner) => member switch
        {
            PropertyInfo property => property.GetValue(owner),
            FieldInfo field => field.GetValue(owner),
            _ => throw new NotSupportedException(member.MemberType.ToString()),
        };

        private static void WriteMember(MemberInfo member, object owner, object? value)
        {
            switch (member)
            {
                case PropertyInfo property:
                    property.SetValue(owner, value);
                    break;
                case FieldInfo field:
                    field.SetValue(owner, value);
                    break;
                default:
                    throw new NotSupportedException(member.MemberType.ToString());
            }
        }

        private static string Describe(SchemaCompilationResult result) =>
            string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}"));

        private const string SchemaText = """
            syntax = "proto3";
            package game;
            import "exceldb/options.proto";

            message Enemy {
              option (exceldb.table) = { kind: ASSET, id: 301, implements: "damageable" };
              string id = 1 [(exceldb.field) = { key: 1 }];
            }

            message Prop {
              option (exceldb.table) = { kind: ASSET, id: 302, implements: "damageable" };
              string id = 1 [(exceldb.field) = { key: 1 }];
            }

            message SingleCell {
              int32 value = 1;
            }

            message OptionalDetails {
              int32 amount = 1;
            }

            message Effect {
              exceldb.RowRef direct_target = 1 [(exceldb.field) = { ref_table: "Enemy" }];
              exceldb.RowRef group_target = 2 [(exceldb.field) = { ref_group: "damageable" }];
            }

            message CollisionLeaf {
              string foo__bar = 1;
              string Foo__bar = 2;
            }

            message CollisionBranch {
              CollisionLeaf foo_bar = 3 [(exceldb.field) = { expand: EXPANDED_COLUMNS }];
              CollisionLeaf Foo_bar = 4 [(exceldb.field) = { expand: EXPANDED_COLUMNS }];
            }

            message Config {
              option (exceldb.table) = { kind: ASSET, id: 300 };
              string id = 1 [(exceldb.field) = { key: 1 }];
              SingleCell single = 2;
              OptionalDetails optional_details = 3 [(exceldb.field) = { expand: EXPANDED_COLUMNS }];
              repeated Effect effects = 4 [(exceldb.field) = { child_table: { sheet_name: "ConfigEffects" } }];
              CollisionBranch collision = 5 [(exceldb.field) = { expand: EXPANDED_COLUMNS }];
              exceldb.UnityResourceRef portrait = 6;
              exceldb.LocalizedTextRef title = 7;
              map<string, int32> tuning = 8;
              string display_name = 9;
            }
            """;
    }

    private sealed class TemporarySchema : IDisposable
    {
        private TemporarySchema(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporarySchema Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "exceldb-generated-editor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporarySchema(path);
        }

        public void Write(string logicalPath, string contents)
        {
            var path = System.IO.Path.Combine(Path, logicalPath);
            File.WriteAllText(path, contents, new UTF8Encoding(false));
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
