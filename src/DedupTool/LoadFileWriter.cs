using System.Text;

namespace DedupTool;

/// <summary>
/// Writes the load file consumed by the review platform ingest job.
/// Column order is fixed. Do not change without coordinating with the ingest team.
/// </summary>
public static class LoadFileWriter
{
    public const string Header = "DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash";

    public static string ToCsv(IEnumerable<LoadFileRow> rows)
    {
        // Ingest job expects LF line endings regardless of platform.
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');
        foreach (var r in rows)
        {
            sb.AppendJoin(',',
                Escape(r.DocId), Escape(r.FamilyId), Escape(r.ParentId), Escape(r.Custodian),
                Escape(r.DocType), Escape(r.FileName), Escape(r.SentDate), Escape(r.Hash)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// RFC 4180 quoting. Only fields containing a comma, quote or line break are quoted,
    /// so ordinary rows are byte-identical to what the ingest job already receives.
    /// </summary>
    internal static string Escape(string? value)
    {
        value ??= "";
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static void Write(string path, IEnumerable<LoadFileRow> rows)
    {
        File.WriteAllText(path, ToCsv(rows));
    }
}
