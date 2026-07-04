using System.Collections.Generic;

namespace ExcelDb.Excel
{
    sealed class SheetData
    {
        public List<string?> Headers { get; } = new List<string?>();
        public List<List<string?>> Rows { get; } = new List<List<string?>>();
    }
}
