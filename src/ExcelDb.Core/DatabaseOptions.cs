namespace ExcelDb
{
    /// <summary>
    /// Behavior when an operation hits a capability gap (read-only pack, missing external loader).
    /// Policy never applies to bad data - data errors always throw.
    /// </summary>
    public enum UnsupportedPolicy
    {
        /// <summary>Fail fast at the operation entry point. Recommended for editor / dev builds.</summary>
        Throw = 0,
        /// <summary>Operations complete as fully usable no-ops; occurrences are counted and surfaced via diagnostics.</summary>
        Silent = 1,
    }

    public sealed class DatabaseOptions
    {
        public UnsupportedPolicy UnsupportedOperation = UnsupportedPolicy.Throw;

        /// <summary>Bounded global undo depth; oldest entries are dropped beyond it.</summary>
        public int UndoDepth = 256;
    }
}
