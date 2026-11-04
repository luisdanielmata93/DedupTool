# Decisions

## Where the spec was unclear, contradictory, or silent

- **§3.2 vs §3.5 — attachment dedup vs intact families.** This is INGEST-4471. The old code applied §3.2 literally across the batch: DOC-000005 (vendor-report.pdf on the FW email) matched DOC-000003, got suppressed, and DOC-000004 reached review with no attachment. I decided §3.5 wins. Attachments are never suppressed on their own. I dedupe whole families and suppress them whole. I considered keeping §3.2 and pointing the suppressed attachment at the surviving copy, but the load file has no column for that, and the reviewer would still open an email with a missing attachment.
- **§4 custodian self-containment vs §3.3/§3.4 global dedup.** These can't both hold with the current load file. §3.3 cross-mailbox dedup always removes another custodian's copy, so severing the surviving custodian leaves a hole in the other one's set. I went with global dedup, because §3.3/§3.4 are specific and the ticket explicitly asks for §3.3. Meeting §4 properly needs the duplicate-custodian information to go somewhere (an `AllCustodians` column, or a sidecar file). §5 says the columns are fixed, so I didn't change the format. **I'd raise this with the ingest team before shipping.**
- **What makes two emails "the same" (§3.3).** The spec gives no rule. I fingerprint: sentDate converted to UTC, `from` lowercased and trimmed, `to` lowercased, trimmed, deduplicated and sorted, subject (trimmed), and body with line endings normalised and trailing whitespace stripped. Everything is NFC-normalised and the result is SHA-256 hashed. I deliberately don't strip `RE:`/`FW:` or touch body content beyond whitespace. A false positive hides a document from review, which is worse than a false negative (a reviewer sees a copy twice). I didn't use `fileName`: it's mailbox-specific, and the sample matches only by coincidence.
- **Email copies with different attachments.** The family key includes the sorted hashes of all descendants, so two copies only collapse when the survivor carries exactly the same attachment content. Otherwise suppressing one copy could hide an attachment that only it had.
- **Which duplicate survives (§6.1).** "First one seen" depends on input order. I keep the family whose top-level `docId` is lowest (ordinal), and rows come out in a fixed order: families by top-level docId, then depth-first with children by docId. The output is byte-identical for any input order. I considered a custodian priority, but there's no basis for one in the spec.
- **Cross-type matches.** §3.1/§3.2 only dedupe like with like. A loose file never suppresses an email or an attachment, even with the same hash.
- **Nested families.** FamilyID and SentDate come from the top-level document, not the immediate parent. The old code broke on email → attached email → PDF.
- **sentDate without an offset.** The spec says the offset is always present. If it isn't, I treat the value as UTC rather than machine-local time, which would make output depend on the host and break §6.1. An unparseable date fails the run, and the error names the docId.
- **Bad input** (duplicate docId, missing parent, parentId cycle). The spec guarantees these don't happen. If one does, I fail with an `InvalidDataException` naming the document. I'd rather fail loudly than silently write broken families into a production load file.
- **Commas or quotes in file names.** §5 says nothing about CSV quoting. The old writer did a naive join, so `budget, final.xlsx` shifted every column after it. I use RFC 4180 quoting only when a field needs it, so ordinary rows are byte-identical to before.
- **SentDate on a loose file's children.** If a loose file ever has children (e.g. an archive), they get an empty SentDate. §5 only defines the date for email families.

## What I changed and why

- `Deduper.cs` rewritten around families. It builds a parent → children index, walks each top-level document's tree, computes a family key, and either emits or suppresses the whole family. `Suppressed` counts documents, not families, so the summary line still adds up: in = out + suppressed.
- Added `EmailFingerprint` (§3.3) and `NormalizeSentDate` (§5: UTC `yyyy-MM-ddTHH:mm:ssZ`, fractions truncated).
- `LoadFileWriter` now escapes fields. The header and column order are unchanged.
- Tests: I wrote the failing tests first against the original code (10 red), then made them pass. I added coverage for the sample batch end to end, the survivor rule under reordering, near-miss emails (subject, body, date, sender), recipient order and case, different attachment sets, cross-type hash matches, and malformed input. 28 tests, all green.
- Sample batch result: 16 in, 12 out, 4 suppressed (mlee's copy of the Q3 family, 9/10/11, and mlee's policy.docx, 13).

## What I didn't get to

- §4 is not satisfied. See above. This is the most important open item.
- No performance test at the 2M-document scale (§6.2). The algorithm is O(n log n) with hashed keys. But `Program` loads the whole JSON into memory with `File.ReadAllText` + `Deserialize`, so peak memory is the full batch, bodies included. That's the first thing I'd expect to fall over.
- The fingerprint doesn't handle HTML vs plain-text bodies, quoted-printable artefacts, Unicode whitespace, or display names in addresses (`"John Smith" <john.smith@…>`). The batch schema has no Message-ID. If the collection side can supply one, it's a far better key than my heuristic.
- An email attached to an email (`.eml` as an attachment) is matched by sha256 only. If mailbox processing alters the embedded bytes, two copies of the parent won't collapse. That's a safe failure (a duplicate is shown, nothing is lost).
- No end-to-end test through `Program` (argument handling, exit codes, output file encoding).

## What I'd do next

- Agree a §4 approach with the ingest team: an `AllCustodians` column (a format change, so coordinated), or a sidecar `<loadfile>.duplicates.csv` mapping each suppressed docId to its survivor. The sidecar keeps the load file untouched.
- Switch `Program` to streaming deserialization and measure memory and time on a synthetic 2M-doc batch.
- Ask whether Message-ID can be added to the batch schema. If yes, use it as the primary §3.3 key, with the fingerprint as fallback.
- Add a golden-file test that runs the CLI on `data/batch-sample.json` and diffs the CSV.

## If you reimplemented in another language

I didn't. I stayed in C# / .NET 8. The CLI contract and load-file format are unchanged, apart from quoting fields that would otherwise have corrupted the row.

## Working with the AI

- I made it go test-first: prove the bugs with failing tests before touching `Deduper.cs`. It found the INGEST-4471 root cause from the sample data (DOC-000005 suppressed because it shares a hash with DOC-000003), plus the nested-family, date-format, CSV-quoting and order-dependence bugs.
- It flagged the §4 vs §3.4 contradiction instead of picking quietly. I kept that as an explicit open item rather than "solving" it with a format change.
- Things I checked by hand rather than trusting: the expected sample output row by row, that DOC-000001 and DOC-000009 are the same instant (14:22 −05:00 = 19:22Z), and that the fingerprint doesn't collapse a reply (`RE:`) into the original.
- Warning for a teammate: the AI defaults to "make the test pass". On fuzzy matching like §3.3 it's easy to end up with a fingerprint that's too loose. Review what it normalises away, not just whether the tests are green. <3

## Continuous Integration:
 I added a lightweight GitHub Actions workflow (ci.yml) to automatically build and run the test suite on pushes to main. While not explicitly requested, ensuring tests run reliably in an isolated CI environment is a foundational step before any code is merged into a production platform.