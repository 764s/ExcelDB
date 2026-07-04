using System.Collections.Generic;
using System.Text;
using ExcelDb.Schema;

namespace ExcelDb.Validation
{
    public enum IssueSeverity
    {
        Info = 0,
        Error = 1,
    }

    public readonly struct ValidationIssue
    {
        public readonly IssueSeverity Severity;
        public readonly string Message;

        public ValidationIssue(IssueSeverity severity, string message)
        {
            Severity = severity;
            Message = message;
        }

        public override string ToString() => $"[{Severity}] {Message}";
    }

    public sealed class ValidationReport
    {
        public List<ValidationIssue> Issues { get; } = new List<ValidationIssue>();

        public bool IsClean
        {
            get
            {
                foreach (var issue in Issues)
                    if (issue.Severity == IssueSeverity.Error)
                        return false;
                return true;
            }
        }

        public void Add(IssueSeverity severity, string message) =>
            Issues.Add(new ValidationIssue(severity, message));

        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (var issue in Issues)
                sb.AppendLine(issue.ToString());
            return sb.ToString();
        }
    }
}

namespace ExcelDb.Database
{
    using ExcelDb.Validation;

    public sealed partial class ConfigDatabase
    {
        /// <summary>
        /// Data gate: dangling references, constraint violations. Key and id uniqueness
        /// are enforced structurally at import/commit; revalidated here for defense in depth.
        /// </summary>
        public ValidationReport Validate()
        {
            var report = new ValidationReport();

            foreach (var store in _stores.Values)
            {
                var schema = store.Schema;
                foreach (var pair in store.Rows)
                {
                    var source = new RowId(schema.Id, pair.Key);
                    MessageOps.WalkRowRefs(pair.Value, (field, reference) =>
                    {
                        var target = new RowId(reference.Table, reference.Id);
                        var where = $"{Describe(source)}.{field.Name}";

                        if (!Registry.TryGet(target.Table, out var targetSchema))
                        {
                            report.Add(IssueSeverity.Error, $"{where} -> unknown table {reference.Table}.");
                            return;
                        }
                        if (!TryStoreOf(target.Table, out var targetStore) || !targetStore.Rows.ContainsKey(target.Id))
                        {
                            report.Add(IssueSeverity.Error, $"{where} -> dangling {targetSchema.Name}#{target.Id}.");
                            return;
                        }

                        var constraint = Registry.ConstraintOf(field);
                        switch (constraint.Kind)
                        {
                            case RefKind.Fixed when targetSchema.Name != constraint.Target:
                                report.Add(IssueSeverity.Error,
                                    $"{where} must reference '{constraint.Target}' but points at {Describe(target)}.");
                                break;
                            case RefKind.Group when !Registry.GroupContains(constraint.Target, target.Table):
                                report.Add(IssueSeverity.Error,
                                    $"{where} must reference group '{constraint.Target}' but {targetSchema.Name} does not implement it.");
                                break;
                        }
                    });
                }
            }

            return report;
        }
    }
}
