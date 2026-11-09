using System.Buffers;
using System.Text;

namespace DedupTool;

/// <summary>
/// Writes the load file consumed by the review platform ingest job.
/// Column order is fixed. Do not change without coordinating with the ingest team.
/// </summary>
public static class LoadFileWriter
{
    public const string Header = "DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash";

    private static readonly SearchValues<char> NeedsQuoting = SearchValues.Create(",\"\r\n");

    public static string ToCsv(IEnumerable<LoadFileRow> rows)
    {
        using var writer = new StringWriter();
        WriteCsv(writer, rows);
        return writer.ToString();
    }

    /// <summary>Streams rows to disk (UTF-8, no BOM) instead of building the whole file in memory.</summary>
    public static void Write(string path, IEnumerable<LoadFileRow> rows)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false), bufferSize: 64 * 1024);
        WriteCsv(writer, rows);
    }

    private static void WriteCsv(TextWriter w, IEnumerable<LoadFileRow> rows)
    {
        // Ingest job expects LF line endings regardless of platform.
        w.Write(Header);
        w.Write('\n');
        foreach (var r in rows)
        {
            WriteField(w, r.DocId); w.Write(',');
            WriteField(w, r.FamilyId); w.Write(',');
            WriteField(w, r.ParentId); w.Write(',');
            WriteField(w, r.Custodian); w.Write(',');
            WriteField(w, r.DocType); w.Write(',');
            WriteField(w, r.FileName); w.Write(',');
            WriteField(w, r.SentDate); w.Write(',');
            WriteField(w, r.Hash);
            w.Write('\n');
        }
    }

    private static void WriteField(TextWriter w, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (value.AsSpan().IndexOfAny(NeedsQuoting) < 0) w.Write(value);
        else w.Write(Escape(value));
    }

    /// <summary>
    /// RFC 4180 quoting. Only fields containing a comma, quote or line break are quoted,
    /// so ordinary rows are byte-identical to what the ingest job already receives.
    /// </summary>
    internal static string Escape(string? value)
    {
        value ??= "";
        if (value.AsSpan().IndexOfAny(NeedsQuoting) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
