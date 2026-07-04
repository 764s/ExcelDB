using System;
using System.Collections;
using System.Collections.Generic;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using ExcelDb.Protocol;

namespace ExcelDb
{
    /// <summary>
    /// Descriptor-driven message utilities: generic clone, in-place copy and reference walking.
    /// Editor/tooling path - allocation relaxed by design.
    /// </summary>
    public static class MessageOps
    {
        public const string RowRefFullName = "exceldb.RowRef";
        public const string ExternalRefFullName = "exceldb.ExternalRef";

        public static bool IsRowRefField(FieldDescriptor field) =>
            field.FieldType == FieldType.Message && !field.IsMap && field.MessageType.FullName == RowRefFullName;

        public static bool IsExternalRefField(FieldDescriptor field) =>
            field.FieldType == FieldType.Message && !field.IsMap && field.MessageType.FullName == ExternalRefFullName;

        public static IMessage Clone(IMessage message) =>
            message.Descriptor.Parser.ParseFrom(message.ToByteString());

        /// <summary>
        /// Overwrites <paramref name="target"/> with the content of <paramref name="source"/>
        /// while preserving the target instance identity (holders keep seeing the same object).
        /// </summary>
        public static void CopyInto(IMessage target, IMessage source)
        {
            if (target.Descriptor != source.Descriptor)
                throw new ArgumentException(
                    $"Cannot copy '{source.Descriptor.FullName}' into '{target.Descriptor.FullName}'.");

            foreach (var field in target.Descriptor.Fields.InDeclarationOrder())
                field.Accessor.Clear(target);
            target.MergeFrom(source.ToByteString());
        }

        /// <summary>
        /// Visits every RowRef in the message, including inside embedded messages,
        /// repeated fields and map values. The callback receives the field on which
        /// the RowRef is declared (constraint source) and the reference value.
        /// </summary>
        public static void WalkRowRefs(IMessage message, Action<FieldDescriptor, RowRef> visit) =>
            Walk(message, visit, null);

        /// <summary>Visits every non-empty ExternalRef, same traversal rules as <see cref="WalkRowRefs"/>.</summary>
        public static void WalkExternalRefs(IMessage message, Action<FieldDescriptor, ExternalRef> visit) =>
            Walk(message, null, visit);

        static void Walk(IMessage message, Action<FieldDescriptor, RowRef>? onRow, Action<FieldDescriptor, ExternalRef>? onExternal)
        {
            foreach (var field in message.Descriptor.Fields.InDeclarationOrder())
            {
                if (field.IsMap)
                {
                    var valueField = field.MessageType.FindFieldByNumber(2);
                    if (valueField.FieldType != FieldType.Message)
                        continue;
                    var map = (IDictionary)field.Accessor.GetValue(message);
                    foreach (DictionaryEntry entry in map)
                        VisitValue(field, valueField.MessageType, entry.Value, onRow, onExternal);
                }
                else if (field.FieldType == FieldType.Message)
                {
                    if (field.IsRepeated)
                    {
                        var list = (IList)field.Accessor.GetValue(message);
                        foreach (var item in list)
                            VisitValue(field, field.MessageType, item, onRow, onExternal);
                    }
                    else
                    {
                        VisitValue(field, field.MessageType, field.Accessor.GetValue(message), onRow, onExternal);
                    }
                }
            }
        }

        static void VisitValue(
            FieldDescriptor declaringField, MessageDescriptor type, object? value,
            Action<FieldDescriptor, RowRef>? onRow, Action<FieldDescriptor, ExternalRef>? onExternal)
        {
            if (value == null)
                return;

            if (type.FullName == RowRefFullName)
            {
                var reference = (RowRef)value;
                if (reference.Table != 0 || reference.Id != 0)
                    onRow?.Invoke(declaringField, reference);
            }
            else if (type.FullName == ExternalRefFullName)
            {
                var reference = (ExternalRef)value;
                if (reference.Scheme.Length > 0 || reference.Id.Length > 0)
                    onExternal?.Invoke(declaringField, reference);
            }
            else
            {
                Walk((IMessage)value, onRow, onExternal);
            }
        }
    }
}
