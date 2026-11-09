using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DedupTool;

/// <summary>
/// simple version. Deduplicates whole <b>families</b> (SPEC §3.5):
/// a top-level document plus every descendant reachable through <c>parentId</c>.
/// A family is emitted whole or suppressed whole; attachments are never removed on their own.
///
/// Two families are duplicates when their top-level documents match
/// (email: normalised fingerprint, §3.3; loose file: sha256, §3.1) and they carry
/// the same attachment hashes. Among duplicates the lowest top-level docId wins (§6.1).
/// </summary>
public static class Deduper
{
    public static DedupResult Run(Batch batch)
    {
        // 1. Build the tree: parentId -> children, plus the list of top-level documents.
        var children = new Dictionary<string, List<Document>>(StringComparer.Ordinal);
        var roots = new List<Document>();
        foreach (var d in batch.Documents)
        {
            if (d.ParentId is null) { roots.Add(d); continue; }
            if (!children.TryGetValue(d.ParentId, out var list))
                children[d.ParentId] = list = new List<Document>();
            list.Add(d);
        }

        // 2. Fixed order => same survivor regardless of input order (§6.1).
        roots.Sort((a, b) => string.CompareOrdinal(a.DocId, b.DocId));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<LoadFileRow>(batch.Documents.Count);
        int suppressed = 0, visited = 0;

        // 3. One decision per family.
        foreach (var root in roots)
        {
            var family = Flatten(root, children);
            visited += family.Count;

            if (!seen.Add(FamilyKey(root, family)))
            {
                suppressed += family.Count;
                continue;
            }

            var sentDate = root.DocType == "email" ? ToUtc(root.SentDate) : "";
            foreach (var m in family)
            {
                rows.Add(new LoadFileRow
                {
                    DocId = m.DocId,
                    FamilyId = root.DocId,        // top-level doc, even for nested attachments
                    ParentId = m.ParentId ?? "",
                    Custodian = m.Custodian,
                    DocType = m.DocType,
                    FileName = m.FileName,
                    SentDate = sentDate,          // attachments carry the top-level email date
                    Hash = m.Sha256,
                });
            }
        }

        // A missing parent or a parentId cycle leaves documents unreachable: fail loudly, never drop silently.
        if (visited != batch.Documents.Count)
            throw new InvalidDataException("Some documents are not reachable from a top-level document (missing parent or cycle).");

        return new DedupResult(rows, suppressed);
    }

    /// <summary>Root first, then descendants depth-first, children ordered by docId.</summary>
    private static List<Document> Flatten(Document root, Dictionary<string, List<Document>> children)
    {
        var result = new List<Document>();
        var stack = new Stack<Document>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            result.Add(d);
            if (children.TryGetValue(d.DocId, out var kids))
            {
                foreach (var k in kids.OrderByDescending(k => k.DocId, StringComparer.Ordinal))
                    stack.Push(k);
            }
        }
        return result;
    }

    /// <summary>Top-level identity + sorted attachment hashes, hashed to a fixed 64-char key.</summary>
    private static string FamilyKey(Document root, List<Document> family)
    {
        var top = root.DocType == "email" ? "E" + EmailFingerprint(root) : "F" + root.Sha256.ToLowerInvariant();
        var attachments = family.Skip(1).Select(m => m.Sha256.ToLowerInvariant()).Order(StringComparer.Ordinal);
        return Sha256Hex(top + "|" + string.Join(",", attachments));
    }

    /// <summary>
    /// Mailbox-independent identity of an email (§3.3): UTC date, lower-cased addresses,
    /// recipients sorted, line endings and trailing whitespace normalised.
    /// Fields are length-prefixed so values can never run into each other.
    /// </summary>
    public static string EmailFingerprint(Document e)
    {
        var to = (e.To ?? new List<string>())
            .Select(Address).Where(a => a.Length > 0).Distinct().Order(StringComparer.Ordinal);

        var sb = new StringBuilder();
        foreach (var field in new[] { ToUtc(e.SentDate), Address(e.From), string.Join(";", to), (e.Subject ?? "").Trim(), Body(e.Body) })
            sb.Append(field.Length).Append(':').Append(field);
        return Sha256Hex(sb.ToString());
    }

    /// <summary>SPEC §5: UTC, exactly yyyy-MM-ddTHH:mm:ssZ. No offset => treated as UTC.</summary>
    public static string ToUtc(string? sentDate) =>
        string.IsNullOrWhiteSpace(sentDate)
            ? ""
            : DateTimeOffset.Parse(sentDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
                .UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Address(string? a) => (a ?? "").Trim().ToLowerInvariant();

    private static string Body(string? body) =>
        string.Join("\n", (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n').Select(line => line.TrimEnd())).Trim();

    private static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
