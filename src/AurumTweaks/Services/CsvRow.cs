using System.Collections.Generic;
using System.Text;

namespace AurumTweaks.Services;

/// <summary>
/// Splits one line of RFC-4180 CSV — the exact quoting <c>ConvertTo-Csv</c> emits — into its fields. Shared by
/// every Aurum surface that reads a PowerShell CSV back (scheduled-task and Appx state), so the fiddly quote
/// handling lives in one tested place instead of a copy per parser. A field may be quoted; inside quotes a doubled
/// <c>""</c> decodes to a single literal quote and commas are taken verbatim (never a field break).
/// </summary>
public static class CsvRow
{
    public static IReadOnlyList<string> Split(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } // doubled quote → literal
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
