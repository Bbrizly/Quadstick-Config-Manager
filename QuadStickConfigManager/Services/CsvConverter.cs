using System.IO;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using QSCM.Models;


namespace QSCM.Services;

public static class CsvConverter
{
    static readonly CsvConfiguration cfg = new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = false
    };

    public static List<ConfigRow> FromCsv(Stream csvStream)
    {
        using var reader = new StreamReader(csvStream);
        using var csv    = new CsvReader(reader, cfg);
        return csv.GetRecords<ConfigRow>().ToList();
    }

    public static byte[] ToCsv(IEnumerable<ConfigRow> rows)
    {
        using var mem = new MemoryStream();
        using var writer = new StreamWriter(mem);
        using var csv = new CsvWriter(writer, cfg);

        csv.WriteRecords(rows);
        writer.Flush();
        return mem.ToArray();
    }
}
