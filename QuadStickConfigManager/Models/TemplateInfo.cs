namespace QSCM.Models
{
    /// <summary>
    /// One row from the index sheet.
    /// </summary>
    public class TemplateInfo
    {
        public string Name        { get; set; } // e.g. "8BitDo Lite SE"
        public string FileId      { get; set; } // long ID from column 2
        public string CsvFilename { get; set; } // e.g. "8BitDoSE.csv"
    }
}
