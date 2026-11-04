using DedupTool;
using Xunit;

namespace DedupTool.Tests;

public class DeduperTests
{
    // ---------------------------------------------------------------------
    // Builders
    // ---------------------------------------------------------------------

    private static Document Loose(string id, string custodian, string hash) => new()
    {
        DocId = id, Custodian = custodian, DocType = "loose", FileName = id + ".txt", Sha256 = hash, SizeBytes = 10,
    };

    private static Document Email(
        string id,
        string custodian,
        string hash,
        string sentDate = "2024-03-05T19:22:00Z",
        string from = "john.smith@example.com",
        string subject = "Q3 budget review",
        string body = "Hi Maria,\n\nAttached is the budget.",
        params string[] to) => new()
    {
        DocId = id,
        Custodian = custodian,
        DocType = "email",
        FileName = id + ".eml",
        Sha256 = hash,
        SizeBytes = 100,
        SentDate = sentDate,
        From = from,
        To = to.Length == 0 ? new List<string> { "maria.lee@example.com" } : to.ToList(),
        Subject = subject,
        Body = body,
    };

    private static Document Attachment(string id, string custodian, string parentId, string hash, string? fileName = null) => new()
    {
        DocId = id,
        Custodian = custodian,
        DocType = "attachment",
        ParentId = parentId,
        FileName = fileName ?? id + ".pdf",
        Sha256 = hash,
        SizeBytes = 50,
    };

    private static LoadFileRow Row(DedupResult result, string docId) =>
        Assert.Single(result.Rows, r => r.DocId == docId);

    // ---------------------------------------------------------------------
    // Existing baseline tests (currently pass)
    // ---------------------------------------------------------------------

    [Fact]
    public void IdenticalLooseDocuments_SecondIsSuppressed()
    {
        var batch = new Batch
        {
            Documents = new() { Loose("A", "c1", "h1"), Loose("B", "c2", "h1") },
        };

        var result = Deduper.Run(batch);

        Assert.Single(result.Rows);
        Assert.Equal("A", result.Rows[0].DocId);
        Assert.Equal(1, result.Suppressed);
    }

    [Fact]
    public void DistinctDocuments_AllKept()
    {
        var batch = new Batch
        {
            Documents = new() { Loose("A", "c1", "h1"), Loose("B", "c1", "h2") },
        };

        var result = Deduper.Run(batch);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void LoadFile_StartsWithFixedHeader()
    {
        var csv = LoadFileWriter.ToCsv(Array.Empty<LoadFileRow>());

        Assert.StartsWith("DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash", csv);
    }

    // ---------------------------------------------------------------------
    // INGEST-4471: emails arriving in review without their attachments
    // SPEC §3.5 — families must be produced intact.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reproduces the ticket with the same shape as data/batch-sample.json:
    /// DOC-000003 (on email 1) and DOC-000005 (on a forward, email 4) share a hash.
    /// The current global "first SHA wins" set suppresses DOC-000005, so the
    /// forward reaches review with zero attachments.
    /// </summary>
    [Fact]
    public void SameAttachmentOnTwoDifferentEmails_BothFamiliesKeepTheirAttachment()
    {
        var batch = new Batch
        {
            Documents = new()
            {
                Email("DOC-1", "jsmith", "e1", subject: "Q3 budget review"),
                Attachment("DOC-3", "jsmith", "DOC-1", "pdf-hash", "vendor-report.pdf"),
                Email("DOC-4", "jsmith", "e4", subject: "FW: vendor report", body: "see below"),
                Attachment("DOC-5", "jsmith", "DOC-4", "pdf-hash", "vendor-report.pdf"),
            },
        };

        var result = Deduper.Run(batch);

        Assert.Contains(result.Rows, r => r.DocId == "DOC-4");
        Assert.Contains(result.Rows, r => r.DocId == "DOC-5" && r.FamilyId == "DOC-4");
        Assert.Equal(0, result.Suppressed);
    }

    /// <summary>
    /// §3.1/§3.2 define dedup within loose files and within attachments.
    /// The current code uses one hash set for every docType, so a loose file
    /// with the same bytes as an attachment silently strips it from its email.
    /// </summary>
    [Fact]
    public void AttachmentMatchingAnEarlierLooseFile_IsNotStrippedFromItsEmail()
    {
        var batch = new Batch
        {
            Documents = new()
            {
                Loose("L1", "jsmith", "shared-hash"),
                Email("E1", "mlee", "e1"),
                Attachment("A1", "mlee", "E1", "shared-hash"),
            },
        };

        var result = Deduper.Run(batch);

        Assert.Contains(result.Rows, r => r.DocId == "E1");
        Assert.Contains(result.Rows, r => r.DocId == "A1" && r.FamilyId == "E1");
    }

    // ---------------------------------------------------------------------
    // Nested families (email -> attached email -> PDF)
    // SPEC §2 parentId chains; §5 FamilyID = top-level doc; SentDate = top-level email date.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The current code only walks one level up (byId[d.ParentId]), so a
    /// grandchild gets its immediate parent as FamilyID instead of the top-level email.
    /// </summary>
    [Fact]
    public void NestedAttachment_FamilyIdIsTopLevelDocument()
    {
        var batch = new Batch
        {
            Documents = new()
            {
                Email("TOP", "jsmith", "top"),
                Attachment("MID", "jsmith", "TOP", "mid", "forwarded.eml"),
                Attachment("LEAF", "jsmith", "MID", "leaf", "report.pdf"),
            },
        };

        var result = Deduper.Run(batch);

        Assert.Equal("TOP", Row(result, "MID").FamilyId);
        Assert.Equal("TOP", Row(result, "LEAF").FamilyId);
        Assert.Equal("MID", Row(result, "LEAF").ParentId);
    }

    /// <summary>
    /// The immediate parent of LEAF is an attachment, whose SentDate is null,
    /// so the current code writes an empty SentDate for the grandchild.
    /// </summary>
    [Fact]
    public void NestedAttachment_CarriesTopLevelEmailSentDate()
    {
        var batch = new Batch
        {
            Documents = new()
            {
                Email("TOP", "jsmith", "top", sentDate: "2024-03-05T19:22:00Z"),
                Attachment("MID", "jsmith", "TOP", "mid", "forwarded.eml"),
                Attachment("LEAF", "jsmith", "MID", "leaf", "report.pdf"),
            },
        };

        var result = Deduper.Run(batch);

        Assert.Equal("2024-03-05T19:22:00Z", Row(result, "LEAF").SentDate);
    }

    // ---------------------------------------------------------------------
    // SentDate format — SPEC §5: UTC, exactly YYYY-MM-DDTHH:MM:SSZ.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The current code passes the raw header value through, so
    /// "2024-03-05T14:22:00-05:00" lands in the load file with its offset.
    /// </summary>
    [Fact]
    public void EmailSentDateWithOffset_IsNormalisedToUtcZ()
    {
        var batch = new Batch
        {
            Documents = new()
            {
                Email("E1", "jsmith", "e1", sentDate: "2024-03-05T14:22:00-05:00"),
                Attachment("A1", "jsmith", "E1", "a1"),
            },
        };

        var result = Deduper.Run(batch);

        Assert.Equal("2024-03-05T19:22:00Z", Row(result, "E1").SentDate);
        Assert.Equal("2024-03-05T19:22:00Z", Row(result, "A1").SentDate);
    }

    [Fact]
    public void EmailSentDateWithFractionalSeconds_IsTruncatedToWholeSeconds()
    {
        var batch = new Batch
        {
            Documents = new() { Email("E1", "jsmith", "e1", sentDate: "2024-03-05T19:22:00.123+00:00") },
        };

        var result = Deduper.Run(batch);

        Assert.Equal("2024-03-05T19:22:00Z", Row(result, "E1").SentDate);
    }

    // ---------------------------------------------------------------------
    // Cross-mailbox email dedup — SPEC §3.3 (never implemented).
    // ---------------------------------------------------------------------

    /// <summary>
    /// Mirrors DOC-000001 (jsmith Sent Items) vs DOC-000009 (mlee Inbox):
    /// different sha256, different offset for the same instant, different
    /// casing on From, CRLF vs LF in body. These are the same email.
    /// The current code compares sha256 only, so both families are emitted.
    /// </summary>
    [Fact]
    public void SameEmailFromTwoMailboxes_WithDifferentHashes_OneFamilyIsSuppressed()
    {
        var batch = new Batch
        {
            Documents = new()
            {
                Email("S1", "jsmith", "sent-bytes",
                    sentDate: "2024-03-05T14:22:00-05:00",
                    from: "john.smith@example.com",
                    body: "Hi Maria,\n\nAttached is the budget."),
                Attachment("S1-A", "jsmith", "S1", "xlsx"),

                Email("I1", "mlee", "inbox-bytes",
                    sentDate: "2024-03-05T19:22:00Z",
                    from: "John.Smith@example.com",
                    body: "Hi Maria,\r\n\r\nAttached is the budget."),
                Attachment("I1-A", "mlee", "I1", "xlsx"),
            },
        };

        var result = Deduper.Run(batch);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(2, result.Suppressed);
        // Whichever copy is kept, it must be kept as a whole family.
        var keptEmail = Assert.Single(result.Rows, r => r.DocType == "email");
        var keptAttachment = Assert.Single(result.Rows, r => r.DocType == "attachment");
        Assert.Equal(keptEmail.DocId, keptAttachment.FamilyId);
    }

    // ---------------------------------------------------------------------
    // Determinism — SPEC §6.1: same output regardless of input order.
    // ---------------------------------------------------------------------

    /// <summary>
    /// "First one we see is kept" means which DocId/Custodian survives
    /// depends on input order. Reversing the batch currently changes the load file.
    /// </summary>
    [Fact]
    public void ReorderedInput_ProducesIdenticalLoadFile()
    {
        var docs = new List<Document>
        {
            Loose("DOC-000008", "jsmith", "policy"),
            Loose("DOC-000013", "mlee", "policy"),
            Loose("DOC-000007", "jsmith", "notes"),
        };

        var forward = Deduper.Run(new Batch { Documents = docs.ToList() });
        var reversed = Deduper.Run(new Batch { Documents = Enumerable.Reverse(docs).ToList() });

        static string[] Lines(DedupResult r) =>
            LoadFileWriter.ToCsv(r.Rows).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .OrderBy(l => l, StringComparer.Ordinal).ToArray();

        Assert.Equal(Lines(forward), Lines(reversed));
    }

    // ---------------------------------------------------------------------
    // Load file CSV integrity — SPEC §5: column order is fixed; ingest breaks otherwise.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The writer does a naive string.Join(","), so a file name containing a
    /// comma shifts every following column one to the right.
    /// </summary>
    [Fact]
    public void LoadFile_FileNameContainingComma_IsQuotedAndKeepsEightColumns()
    {
        var row = new LoadFileRow
        {
            DocId = "D1", FamilyId = "D1", ParentId = "", Custodian = "jsmith",
            DocType = "loose", FileName = "budget, final.xlsx", SentDate = "", Hash = "h",
        };

        var csv = LoadFileWriter.ToCsv(new[] { row });
        var dataLine = csv.Split('\n')[1];

        Assert.Equal("D1,D1,,jsmith,loose,\"budget, final.xlsx\",,h", dataLine);
    }

    [Fact]
    public void LoadFile_FileNameContainingQuote_IsEscaped()
    {
        var row = new LoadFileRow
        {
            DocId = "D1", FamilyId = "D1", Custodian = "jsmith",
            DocType = "loose", FileName = "the \"real\" plan.docx", Hash = "h",
        };

        var csv = LoadFileWriter.ToCsv(new[] { row });
        var dataLine = csv.Split('\n')[1];

        Assert.Equal("D1,D1,,jsmith,loose,\"the \"\"real\"\" plan.docx\",,h", dataLine);
    }

    [Fact]
    public void LoadFile_PlainFields_AreNotQuoted()
    {
        var row = new LoadFileRow
        {
            DocId = "D1", FamilyId = "D1", Custodian = "jsmith", DocType = "loose",
            FileName = "notes.txt", Hash = "h",
        };

        Assert.Equal("D1,D1,,jsmith,loose,notes.txt,,h", LoadFileWriter.ToCsv(new[] { row }).Split('\n')[1]);
    }

    // ---------------------------------------------------------------------
    // Behaviour added by the fix (guards against regressions / over-matching)
    // ---------------------------------------------------------------------

    [Fact]
    public void SampleBatch_ProducesExpectedLoadFile()
    {
        var json = File.ReadAllText(FindRepoFile("data", "batch-sample.json"));
        var batch = System.Text.Json.JsonSerializer.Deserialize<Batch>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var result = Deduper.Run(batch);

        // Suppressed: mlee's copy of the Q3 email family (9,10,11) and mlee's policy.docx (13).
        Assert.Equal(
            new[] { "DOC-000001", "DOC-000002", "DOC-000003", "DOC-000004", "DOC-000005", "DOC-000006",
                    "DOC-000007", "DOC-000008", "DOC-000012", "DOC-000014", "DOC-000015", "DOC-000016" },
            result.Rows.Select(r => r.DocId).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(4, result.Suppressed);

        var fw = Row(result, "DOC-000005");
        Assert.Equal("DOC-000004", fw.FamilyId);
        Assert.Equal("2024-03-06T14:10:00Z", fw.SentDate);
        Assert.All(result.Rows, r => Assert.True(r.SentDate == "" || r.SentDate.EndsWith('Z')));
    }

    [Fact]
    public void DuplicateFamilies_SurvivorIsLowestTopLevelDocId_RegardlessOfOrder()
    {
        var docs = new List<Document>
        {
            Email("E9", "mlee", "inbox", sentDate: "2024-03-05T19:22:00Z"),
            Attachment("E9-A", "mlee", "E9", "x"),
            Email("E1", "jsmith", "sent", sentDate: "2024-03-05T14:22:00-05:00"),
            Attachment("E1-A", "jsmith", "E1", "x"),
        };

        foreach (var order in new[] { docs, Enumerable.Reverse(docs).ToList() })
        {
            var result = Deduper.Run(new Batch { Documents = order.ToList() });
            Assert.Equal(new[] { "E1", "E1-A" }, result.Rows.Select(r => r.DocId));
        }
    }

    [Fact]
    public void SameEmailHeaders_ButDifferentAttachments_AreNotCollapsed()
    {
        // Collapsing these would hide A2's content from review entirely.
        var batch = new Batch
        {
            Documents = new()
            {
                Email("E1", "jsmith", "s1"),
                Attachment("A1", "jsmith", "E1", "x"),
                Email("E2", "mlee", "s2"),
                Attachment("A2", "mlee", "E2", "y"),
            },
        };

        var result = Deduper.Run(batch);

        Assert.Equal(4, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
    }

    [Theory]
    [InlineData("subject", "Re: Q3 budget review")]
    [InlineData("body", "Hi Maria,\n\nAttached is the revised budget.")]
    [InlineData("sentDate", "2024-03-05T19:23:00Z")]
    [InlineData("from", "maria.lee@example.com")]
    public void EmailsDifferingInContent_AreNotDuplicates(string field, string value)
    {
        var other = Email("E2", "mlee", "s2");
        switch (field)
        {
            case "subject": other.Subject = value; break;
            case "body": other.Body = value; break;
            case "sentDate": other.SentDate = value; break;
            case "from": other.From = value; break;
        }

        var result = Deduper.Run(new Batch { Documents = new() { Email("E1", "jsmith", "s1"), other } });

        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void EmailRecipientOrderAndCase_DoNotAffectDuplicateDetection()
    {
        var batch = new Batch
        {
            Documents = new()
            {
                Email("E1", "jsmith", "s1", to: new[] { "a@example.com", "B@example.com" }),
                Email("E2", "mlee", "s2", to: new[] { "b@example.com", " A@Example.com " }),
            },
        };

        var result = Deduper.Run(batch);

        Assert.Equal("E1", Assert.Single(result.Rows).DocId);
    }

    [Fact]
    public void EmailAndLooseFileWithSameHash_AreNotDuplicates()
    {
        var batch = new Batch
        {
            Documents = new() { Email("E1", "jsmith", "same"), Loose("L1", "jsmith", "same") },
        };

        Assert.Equal(2, Deduper.Run(batch).Rows.Count);
    }

    [Fact]
    public void SentDateWithoutOffset_IsTreatedAsUtc()
    {
        var batch = new Batch { Documents = new() { Email("E1", "jsmith", "e1", sentDate: "2024-03-05T19:22:00") } };

        Assert.Equal("2024-03-05T19:22:00Z", Row(Deduper.Run(batch), "E1").SentDate);
    }

    [Fact]
    public void UnparseableSentDate_FailsWithDocId()
    {
        var batch = new Batch { Documents = new() { Email("E1", "jsmith", "e1", sentDate: "yesterday-ish") } };

        var ex = Assert.Throws<InvalidDataException>(() => Deduper.Run(batch));
        Assert.Contains("E1", ex.Message);
    }

    [Fact]
    public void MissingParent_Fails()
    {
        var batch = new Batch { Documents = new() { Attachment("A1", "jsmith", "NOPE", "x") } };

        Assert.Throws<InvalidDataException>(() => Deduper.Run(batch));
    }

    [Fact]
    public void ParentCycle_Fails()
    {
        var batch = new Batch
        {
            Documents = new() { Attachment("A", "jsmith", "B", "x"), Attachment("B", "jsmith", "A", "y") },
        };

        Assert.Throws<InvalidDataException>(() => Deduper.Run(batch));
    }

    [Fact]
    public void DuplicateDocId_Fails()
    {
        var batch = new Batch { Documents = new() { Loose("A", "c1", "h1"), Loose("A", "c1", "h2") } };

        Assert.Throws<InvalidDataException>(() => Deduper.Run(batch));
    }

    private static string FindRepoFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(Path.Combine(parts));
    }
}
