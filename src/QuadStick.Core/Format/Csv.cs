namespace QuadStick.Format;

// Small CSV reader/writer. Quoted fields handled; profiles rarely need it.
public static class Csv
{
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        bool inQuotes = false;
        int i = 0;

        void EndField() { row.Add(field.ToString()); field.Clear(); }
        void EndRow() { EndField(); rows.Add(row.ToArray()); row.Clear(); }

        while (i < text.Length)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': EndField(); break;
                case '\r': break;
                case '\n': EndRow(); break;
                default: field.Append(c); break;
            }
            i++;
        }
        if (field.Length > 0 || row.Count > 0) EndRow();
        return rows;
    }

    public static string Write(IEnumerable<string[]> rows)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var row in rows)
        {
            for (int i = 0; i < row.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var f = row[i] ?? "";
                if (f.Contains(',') || f.Contains('"') || f.Contains('\n') || f.Contains('\r'))
                    sb.Append('"').Append(f.Replace("\"", "\"\"")).Append('"');
                else
                    sb.Append(f);
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }
}
