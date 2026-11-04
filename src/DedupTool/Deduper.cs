using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DedupTool;

/// <summary>
/// Suppresses duplicate content in a batch while keeping families intact (SPEC §3).
///
/// The unit of deduplication is the <b>family</b>: a top-level document plus every
/// descendant reachable through <c>parentId</c>. A family is either emitted whole or
/// suppressed whole; individual attachments are never removed from their parent.
///
/// Two families are duplicates when their top-level documents match and they carry
/// the same set of attachment content:
///  - top-level email: <see cref="EmailFingerprint"/> (cross-mailbox, §3.3)
///  - anything else (loose file): <c>sha256</c> (§3.1)
///
/// Among duplicates the family whose top-level <c>docId</c> sorts first (ordinal) is kept,
/// so the output does not depend on input order (§6.1).
/// </summary>
public static class Deduper
{
    public static DedupResult Run(Batch batch)
    {
        var docs = batch.Documents;

        var byId = new Dictionary<string, Document>(docs.Count, StringComparer.Ordinal);
        foreach (var d in docs)
        {
            if (!byId.TryAdd(d.DocId, d))
                throw new InvalidDataException($"Duplicate docId '{d.DocId}' in batch.");
        }

        var children = new Dictionary<string, List<Document>>(StringComparer.Ordinal);
        foreach (var d in docs)
        {
            if (d.ParentId is null) continue;
            if (!byId.ContainsKey(d.ParentId))
                throw new InvalidDataException($"Document '{d.DocId}' references missing parent '{d.ParentId}'.");
            if (!children.TryGetValue(d.ParentId, out var list))
                children[d.ParentId] = list = new List<Document>();
            list.Add(d);
        }
        foreach (var list in children.Values)
            list.Sort((a, b) => string.CompareOrdinal(a.DocId, b.DocId));

        // Top-level documents, in a fixed order so survivor selection and row order are stable.
        var roots = docs.Where(d => d.ParentId is null)
                        .OrderBy(d => d.DocId, StringComparer.Ordinal)
                        .ToList();

        var rows = new List<LoadFileRow>(docs.Count);
        var seenFamilies = new HashSet<string>(StringComparer.Ordinal);
        var suppressed = 0;
        var emitted = 0;

        foreach (var root in roots)
        {
            var members = Flatten(root, children);
            emitted += members.Count;

            if (!seenFamilies.Add(FamilyKey(root, members)))
            {
                suppressed += members.Count;
                continue;
            }

            var familyDate = root.DocType == "email" ? NormalizeSentDate(root.SentDate, root.DocId) : "";
            foreach (var m in members)
            {
                rows.Add(new LoadFileRow
                {
                    DocId = m.DocId,
                    FamilyId = root.DocId,
                    ParentId = m.ParentId ?? "",
                    Custodian = m.Custodian,
                    DocType = m.DocType,
                    FileName = m.FileName,
                    SentDate = familyDate,
                    Hash = m.Sha256,
                });
            }
        }

        // Every document must be reachable from a root; anything left over is a parentId cycle.
        if (emitted != docs.Count)
            throw new InvalidDataException("Batch contains a parentId cycle; some documents have no top-level ancestor.");

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
                for (var i = kids.Count - 1; i >= 0; i--)
                    stack.Push(kids[i]);
            }
        }
        return result;
    }

    /// <summary>
    /// Identity of a family's content. The top-level identity plus the multiset of
    /// descendant hashes, so two copies of an email only collapse when the surviving
    /// copy carries the same attachments as the suppressed one.
    /// </summary>
    private static string FamilyKey(Document root, List<Document> members)
    {
        var rootKey = root.DocType == "email"
            ? "E:" + EmailFingerprint(root)
            : "F:" + root.Sha256.ToLowerInvariant();

        if (members.Count == 1) return rootKey;

        var childHashes = members.Skip(1)
                                 .Select(m => m.Sha256.ToLowerInvariant())
                                 .OrderBy(h => h, StringComparer.Ordinal);
        return rootKey + "|" + string.Join(",", childHashes);
    }

    /// <summary>
    /// Mailbox-independent identity of an email (SPEC §3.3). Built from the fields that
    /// survive collection from different mailboxes, normalised for the differences we know
    /// about: timezone offset, address casing, recipient order, line endings, trailing whitespace.
    /// Hashed so memory stays flat on ~2M-document batches.
    /// </summary>
    public static string EmailFingerprint(Document email)
    {
        const char Sep = '\u001F';
        var sb = new StringBuilder();
        sb.Append(NormalizeSentDate(email.SentDate, email.DocId)).Append(Sep);
        sb.Append(NormalizeAddress(email.From)).Append(Sep);
        sb.AppendJoin(';', (email.To ?? new List<string>())
                               .Select(NormalizeAddress)
                               .Where(a => a.Length > 0)
                               .Distinct(StringComparer.Ordinal)
                               .OrderBy(a => a, StringComparer.Ordinal)).Append(Sep);
        sb.Append(NormalizeText(email.Subject).Trim()).Append(Sep);
        sb.Append(NormalizeBody(email.Body));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>
    /// SPEC §5: UTC, exactly yyyy-MM-ddTHH:mm:ssZ. Fractional seconds are truncated.
    /// A value with no offset is treated as UTC (never machine-local time, which would break §6.1).
    /// </summary>
    public static string NormalizeSentDate(string? sentDate, string docId)
    {
        if (string.IsNullOrWhiteSpace(sentDate)) return "";

        if (!DateTimeOffset.TryParse(sentDate.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
        {
            throw new InvalidDataException($"Document '{docId}' has an unparseable sentDate '{sentDate}'.");
        }

        return parsed.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private static string NormalizeAddress(string? address) =>
        NormalizeText(address).Trim().ToLowerInvariant();

    private static string NormalizeText(string? s) =>
        (s ?? "").Normalize(NormalizationForm.FormC);

    private static string NormalizeBody(string? body)
    {
        var text = NormalizeText(body).Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n').Select(l => l.TrimEnd());
        return string.Join("\n", lines).Trim();
    }
}
