using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using ExcelDb.Protocol;
using ExcelDb.Schema;

namespace ExcelDb.Excel
{
    public static class ExcelFieldCodec
    {
        const char ListSeparator = ';';

        public static string FormatField(IMessage message, FieldDescriptor field)
        {
            if (field.IsMap)
                throw new NotSupportedException($"Map field '{field.FullName}' is not supported by the Excel cell codec.");

            var value = field.Accessor.GetValue(message);
            if (field.IsRepeated)
            {
                var items = ((IEnumerable)value).Cast<object?>()
                    .Select(item => FormatSingle(field, item))
                    .Where(text => text.Length > 0);
                return string.Join(ListSeparator.ToString(), items);
            }

            return FormatSingle(field, value);
        }

        public static void SetField(IMessage message, FieldDescriptor field, string text, SchemaRegistry registry)
        {
            if (field.IsMap)
                throw new NotSupportedException($"Map field '{field.FullName}' is not supported by the Excel cell codec.");

            if (field.IsRepeated)
            {
                var list = field.Accessor.GetValue(message);
                var elementType = RepeatedElementType(list);
                ClearRepeated(list);
                foreach (var part in SplitList(text))
                {
                    if (part.Length == 0)
                        continue;
                    AddRepeated(list, ParseSingle(field, part, registry, elementType));
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                field.Accessor.Clear(message);
                return;
            }

            var current = field.Accessor.GetValue(message);
            field.Accessor.SetValue(message, ParseSingle(field, text.Trim(), registry, current?.GetType()));
        }

        public static FieldDescriptor? FindField(MessageDescriptor descriptor, string name)
        {
            var direct = descriptor.FindFieldByName(name);
            if (direct != null)
                return direct;

            foreach (var field in descriptor.Fields.InDeclarationOrder())
                if (string.Equals(field.JsonName, name, StringComparison.Ordinal))
                    return field;
            return null;
        }

        static string FormatSingle(FieldDescriptor field, object? value)
        {
            if (value == null)
                return string.Empty;

            switch (field.FieldType)
            {
                case FieldType.Message:
                    return FormatMessage((IMessage)value);
                case FieldType.Bytes:
                    return Convert.ToBase64String(((ByteString)value).ToByteArray());
                case FieldType.Double:
                    return ((double)value).ToString(CultureInfo.InvariantCulture);
                case FieldType.Float:
                    return ((float)value).ToString(CultureInfo.InvariantCulture);
                case FieldType.Bool:
                    return ((bool)value) ? "true" : "false";
                default:
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        static string FormatMessage(IMessage message)
        {
            if (message is RowRef rowRef)
                return rowRef.Table == 0 && rowRef.Id == 0
                    ? string.Empty
                    : rowRef.Table.ToString(CultureInfo.InvariantCulture) + ":" + rowRef.Id.ToString(CultureInfo.InvariantCulture);
            if (message is ExternalRef externalRef)
                return string.IsNullOrEmpty(externalRef.Scheme) && string.IsNullOrEmpty(externalRef.Id)
                    ? string.Empty
                    : externalRef.Scheme + ":" + externalRef.Id;

            return JsonFormatter.Default.Format(message);
        }

        static object ParseSingle(FieldDescriptor field, string text, SchemaRegistry registry, Type? expectedType)
        {
            switch (field.FieldType)
            {
                case FieldType.Message:
                    return ParseMessage(field, text, registry);
                case FieldType.Double:
                    return double.Parse(text, CultureInfo.InvariantCulture);
                case FieldType.Float:
                    return float.Parse(text, CultureInfo.InvariantCulture);
                case FieldType.Int64:
                case FieldType.SInt64:
                case FieldType.SFixed64:
                    return long.Parse(text, CultureInfo.InvariantCulture);
                case FieldType.UInt64:
                case FieldType.Fixed64:
                    return ulong.Parse(text, CultureInfo.InvariantCulture);
                case FieldType.Int32:
                case FieldType.SInt32:
                case FieldType.SFixed32:
                    return int.Parse(text, CultureInfo.InvariantCulture);
                case FieldType.UInt32:
                case FieldType.Fixed32:
                    return uint.Parse(text, CultureInfo.InvariantCulture);
                case FieldType.Bool:
                    return ParseBool(text);
                case FieldType.String:
                    return text;
                case FieldType.Bytes:
                    return ByteString.CopyFrom(Convert.FromBase64String(text));
                case FieldType.Enum:
                    return ParseEnum(field, text, expectedType);
                default:
                    throw new NotSupportedException($"Field type '{field.FieldType}' is not supported for '{field.FullName}'.");
            }
        }

        static IMessage ParseMessage(FieldDescriptor field, string text, SchemaRegistry registry)
        {
            if (field.MessageType.FullName == MessageOps.RowRefFullName)
                return ParseRowRef(field, text, registry);
            if (field.MessageType.FullName == MessageOps.ExternalRefFullName)
                return ParseExternalRef(text);

            return JsonParser.Default.Parse(text, field.MessageType);
        }

        static RowRef ParseRowRef(FieldDescriptor field, string text, SchemaRegistry registry)
        {
            var separator = text.IndexOf(':');
            if (separator < 0)
                separator = text.IndexOf('#');

            if (separator >= 0)
            {
                var tableText = text.Substring(0, separator).Trim();
                var idText = text.Substring(separator + 1).Trim();
                return new RowRef
                {
                    Table = ParseTable(tableText, registry).Number,
                    Id = int.Parse(idText, CultureInfo.InvariantCulture),
                };
            }

            var constraint = registry.ConstraintOf(field);
            if (constraint.Kind != RefKind.Fixed)
                throw new FormatException(
                    $"'{field.FullName}' needs 'TableName:id' or 'tableNumber:id' because it is not a fixed-table reference.");

            return new RowRef
            {
                Table = registry.Get(constraint.Target).Id.Number,
                Id = int.Parse(text, CultureInfo.InvariantCulture),
            };
        }

        static ExternalRef ParseExternalRef(string text)
        {
            var separator = text.IndexOf(':');
            if (separator < 0)
                throw new FormatException("ExternalRef cells must use 'scheme:id'.");

            return new ExternalRef
            {
                Scheme = text.Substring(0, separator).Trim(),
                Id = text.Substring(separator + 1).Trim(),
            };
        }

        static TableId ParseTable(string text, SchemaRegistry registry)
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                return new TableId(number);
            return registry.Get(text).Id;
        }

        static bool ParseBool(string text)
        {
            if (bool.TryParse(text, out var value))
                return value;
            if (text == "1")
                return true;
            if (text == "0")
                return false;
            throw new FormatException($"'{text}' is not a boolean value.");
        }

        static object ParseEnum(FieldDescriptor field, string text, Type? expectedType)
        {
            var enumValue = field.EnumType.FindValueByName(text);
            var number = enumValue != null
                ? enumValue.Number
                : int.Parse(text, CultureInfo.InvariantCulture);

            if (expectedType != null && expectedType.GetTypeInfo().IsEnum)
                return Enum.ToObject(expectedType, number);
            return number;
        }

        static IReadOnlyList<string> SplitList(string text) =>
            string.IsNullOrWhiteSpace(text)
                ? Array.Empty<string>()
                : text.Split(new[] { ListSeparator }, StringSplitOptions.None)
                    .Select(part => part.Trim())
                    .ToArray();

        static void ClearRepeated(object list)
        {
            if (list is IList nonGeneric)
            {
                nonGeneric.Clear();
                return;
            }

            var clear = list.GetType().GetRuntimeMethod("Clear", Type.EmptyTypes)
                ?? throw new NotSupportedException($"Repeated field collection '{list.GetType().FullName}' has no Clear method.");
            clear.Invoke(list, Array.Empty<object>());
        }

        static Type? RepeatedElementType(object list)
        {
            var type = list.GetType().GetTypeInfo();
            if (type.IsGenericType)
                return type.GenericTypeArguments.FirstOrDefault();

            foreach (var candidate in type.ImplementedInterfaces)
            {
                var info = candidate.GetTypeInfo();
                if (info.IsGenericType && info.GetGenericTypeDefinition() == typeof(IList<>))
                    return info.GenericTypeArguments[0];
            }
            return null;
        }

        static void AddRepeated(object list, object value)
        {
            if (list is IList nonGeneric)
            {
                nonGeneric.Add(value);
                return;
            }

            var add = list.GetType().GetRuntimeMethods()
                .FirstOrDefault(m =>
                    m.Name == "Add" &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType.GetTypeInfo().IsAssignableFrom(value.GetType().GetTypeInfo()));
            if (add == null)
                throw new NotSupportedException($"Repeated field collection '{list.GetType().FullName}' has no compatible Add method.");
            add.Invoke(list, new[] { value });
        }
    }
}
