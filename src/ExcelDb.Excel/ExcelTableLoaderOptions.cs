namespace ExcelDb.Excel
{
    public sealed class ExcelTableLoaderOptions
    {
        public string IdColumnName { get; set; } = "_id";
        public bool CreateIfMissing { get; set; }
    }
}
