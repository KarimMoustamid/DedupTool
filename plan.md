# DedupTool — Implementation Plan (INGEST-4471)

> **Status: DRAFT — awaiting sign-off on D1–D3 before any code is touched.**
> Command-line contract `DedupTool <batch.json> <loadfile.csv>` is preserved exactly.
> Author: Karim (with AI assist). Time budget for the exercise: 60 minutes.

**Goal:** Fix the missing-attachment bug, implement cross-mailbox email
deduplication (SPEC §3.3), make the load file match SPEC.md, add tests, and fill
`DECISIONS.md` — staying in C# / .NET 8.

**Architecture:** Make the **family** the atomic dedup unit, and pick duplicate
representatives **deterministically** so the load file is identical regardless of
input order (§6.1). Two passes:
1. Index documents and group each one under its family root (walk `parentId` to the top level).
2. Decide keep/suppress per group (loose by `sha256`, emails by a normalised content key), then emit rows sorted by `DocID`.

**Tech stack:** C# / .NET 8 (SDK 8.0.410 installed; repo targets `net8.0`), xUnit 2.9.2.

---

## 1. Diagnosis (why we're here)

| # | Defect | Spec ref | Evidence in sample |
|---|--------|----------|--------------------|
| B1 | Attachments are deduped **globally by `sha256`**, independently of their family. An email is kept but its attachment is dropped because *another* family has byte-identical content. | §3.5, §4 | `vendor-report.pdf` = `DOC-000003` (on `DOC-000001`) **and** `DOC-000005` (on `DOC-000004`) → `DOC-000005` suppressed. `mlee`'s copy `DOC-000009` keeps no attachments: `DOC-000010`/`DOC-000011` suppressed. |
| B2 | `SentDate` is written **verbatim** (raw offset), not converted to UTC `YYYY-MM-DDTHH:MM:SSZ`. | §5 | Output rows show `2024-03-05T14:22:00-05:00`, `2024-03-06T09:10:00-05:00`, `2024-03-06T11:45:00-05:00`. |
| B3 | `FamilyID` uses the **direct parent**, not the family's top-level document. (Latent — no nested chain in the sample.) | §5 | `Deduper.cs:38` `familyId = parent.DocId`. |
| B4 | **Cross-mailbox email dedup is not implemented.** Emails with different bytes (headers, casing, line endings, offset-vs-`Z` date) but identical content are treated as distinct. | §3.3 | `DOC-000001` (jsmith) vs `DOC-000009` (mlee): same subject/to/body-modulo-line-endings/instant; different `sha256` → both kept today. |
| B5 | Kept duplicate is **"first seen"** → output depends on input order. | §6.1 | `seen.Add(sha256)` in `Deduper.cs:24`; flipping input order changes which doc is kept. |

Extra (documented, not necessarily fixed): `LoadFileWriter` has no CSV quoting/escaping; `Program.cs` builds the whole batch in memory (~2M docs worst case).

---

## 2. Open decisions — NEED YOUR SIGN-OFF before implementation

**D1 — Global vs custodial dedup for cross-mailbox emails (§3.3 vs §4).**
§3.3/§3.4 say recognise cross-mailbox copies as duplicates, across the whole batch;
§4 says each custodian's production must be "complete and self-contained."
These conflict when one email sits in two mailboxes.
- **Recommended: GLOBAL.** Suppress the duplicate email's *entire family*; keep one copy.
  Matches §1 ("reviewers never see the same content twice") and §3.4 literally; reads
  §4 as referential integrity, which whole-family suppression still satisfies.
- Alternative (B): CUSTODIAL — dedup only within each custodian; every custodian keeps
  their own copy. Honours §4 but makes §3.3 a no-op (cross-mailbox copies are always
  in different custodians). **This materially changes the sample output: GLOBAL → 12 rows / 4 suppressed; CUSTODIAL → 15 rows / 1 suppressed.**

**D2 — Do loose files stay global (across custodians)?**
- **Recommended: YES.** §3.1 + §3.4 are unambiguous and a loose file has no family to
  protect. Sample `DOC-000013` (mlee `policy.docx`, same hash as jsmith `DOC-000008`)
  stays suppressed, as today.

**D3 — Email identity key composition.**
- **Recommended key** = SHA-256 of: `lower(trim(from))` ␟ `sorted(lower(trim(to)))` ␟
  `trim(subject)` ␟ `sentDate-as-UTC-instant` ␟ `body with CRLF/CR→LF, TrimEnd()`.
  The sample's two copies differ **only** in ways this normalises away, so they match.
- Sub-decision: include `sentDate` (as an instant) in the key — recommended, it reduces
  false merges (a genuine resend with identical from/to/subject/body); risk is a false
  split only if header dates differ, which they shouldn't across mailboxes.

> Everything below assumes D1=GLOBAL, D2=YES, D3=as stated. I will not start coding until you confirm.

---

## 3. Invariants to preserve (do NOT change)

- CLI: `DedupTool <batch.json> <loadfile.csv>`; exit `2` on wrong arg count.
- Header text and column order (`LoadFileWriter.Header`).
- LF (`\n`) line endings on every platform.
- `Hash` column = the document's own `sha256` (even when suppressed-by-content).
- One-line summary (§6.3).

---

## 4. Task list (bite-sized, TDD, one commit each)

### Task 0 — Baseline (5 min)
**Run:**
```bash
cd /Users/karimmoustamid/Downloads/DedupTool
dotnet test
cd src/DedupTool && dotnet run -- ../../data/batch-sample.json /tmp/before.csv
cd ../..
```
**Expected:** 3 tests pass; `/tmp/before.csv` matches `data/current-tool-output.csv` (12 rows, offsets not normalised). Confirms the bug reproduces before we change anything.
**Keep `data/current-tool-output.csv` untouched** — it is the "before" evidence.

---

### Task 1 — SentDate → UTC `YYYY-MM-DDTHH:MM:SSZ` (10 min)
**Create:** `src/DedupTool/SentDates.cs`
```csharp
using System.Globalization;

namespace DedupTool;

/// <summary>
/// Normalises a raw email sentDate (ISO-8601 with offset or Z) to the load-file
/// format: UTC, exactly yyyy-MM-ddTHH:mm:ssZ, no fractional seconds, no offset.
/// Returns "" for null/empty. Unparseable input is returned unchanged so it is
/// visible rather than silently lost (spec is silent on malformed dates).
/// </summary>
public static class SentDates
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return raw;
        return dto.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);
    }
}
```
**Test:** `test/DedupTool.Tests/SentDatesTests.cs`
```csharp
using DedupTool; using Xunit;
namespace DedupTool.Tests;

public class SentDatesTests
{
    [Theory]
    [InlineData("2024-03-05T14:22:00-05:00", "2024-03-05T19:22:00Z")]
    [InlineData("2024-03-05T19:22:00Z",     "2024-03-05T19:22:00Z")]
    [InlineData("2024-03-06T09:10:00-05:00", "2024-03-06T14:10:00Z")]
    [InlineData("2024-03-06T11:45:00-05:00", "2024-03-06T16:45:00Z")]
    [InlineData("2024-03-05T19:22:00.123Z",  "2024-03-05T19:22:00Z")] // fractional dropped
    [InlineData(null, "")] [InlineData("", "")]
    public void Normalize_ToUtcZFormat(string? input, string expected)
        => Assert.Equal(expected, SentDates.Normalize(input));
}
```
**Run:** `dotnet test` → all pass. **Commit:** `fix: normalise SentDate to UTC Z format`.

---

### Task 2 — Resolve the family root (top-level doc) (10 min)
**Modify:** `src/DedupTool/Deduper.cs` — add helper:
```csharp
// Walk parentId to the top-level document. The parent is always present in the
// batch (SPEC §2); guard handles cycles/dangling refs defensively.
private static Document ResolveRoot(Document d, Dictionary<string, Document> byId)
{
    var cur = d; var guard = 0;
    while (cur.ParentId != null && byId.TryGetValue(cur.ParentId, out var parent))
    {
        cur = parent;
        if (++guard > 10_000) break;
    }
    return cur;
}
```
**Test:** nested chain `email → attached email → pdf` ⇒ `FamilyID` = top email id for all three (fixes B3).
**Run:** `dotnet test`. **Commit:** `fix: resolve FamilyID to the family's top-level document`.

---

### Task 3 — Email identity key (10 min)
**Create:** `src/DedupTool/EmailIdentity.cs`
```csharp
using System.Security.Cryptography; using System.Text;
namespace DedupTool;

/// <summary>
/// Content-based identity for an email so the same message collected from
/// different mailboxes (differing bytes/sha256/line endings/address casing/
/// offset-vs-Z date) hashes to the same value (SPEC §3.3).
/// </summary>
public static class EmailIdentity
{
    public static string Key(Document d)
    {
        var from = (d.From ?? "").Trim().ToLowerInvariant();
        var to = string.Join(",", (d.To ?? new()).Select(x => x.Trim().ToLowerInvariant())
                                       .OrderBy(x => x, StringComparer.Ordinal));
        var subject = (d.Subject ?? "").Trim();
        var body = (d.Body ?? "").Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
        var sent = SentDates.Normalize(d.SentDate);          // UTC instant
        var canonical = string.Join('\u001f', from, to, subject, sent, body);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                      .ToLowerInvariant();
    }
}
```
**Tests:** identical-after-normalisation ⇒ equal; differing body (and differing `to` set/order-independent) ⇒ accounted for.
**Run:** `dotnet test`. **Commit:** `feat: content-based email identity for cross-mailbox dedup`.

---

### Task 4 — Family-centric dedup rewrite (15 min)
**Modify:** `src/DedupTool/Deduper.cs` — replace `Run` body:
```csharp
public static DedupResult Run(Batch batch)
{
    var byId = new Dictionary<string, Document>(StringComparer.Ordinal);
    foreach (var d in batch.Documents) byId[d.DocId] = d;

    // Pass 1: group every document under its family root.
    var familyOrder = new List<string>();
    var families = new Dictionary<string, List<Document>>(StringComparer.Ordinal);
    foreach (var d in batch.Documents)
    {
        var root = ResolveRoot(d, byId);
        if (!families.TryGetValue(root.DocId, out var list))
        { list = new(); families[root.DocId] = list; familyOrder.Add(root.DocId); }
        list.Add(d);
    }

    // Pass 2: deterministic representative per duplicate group (lowest DocID wins) → order-independent (§6.1).
    var keepEmail = new Dictionary<string, string>(StringComparer.Ordinal); // key -> rootId
    var keepLoose = new Dictionary<string, string>(StringComparer.Ordinal); // sha  -> docId
    foreach (var rootId in familyOrder)
    {
        var root = byId[rootId];
        bool isEmail = root.DocType == "email";
        var key = isEmail ? EmailIdentity.Key(root) : root.Sha256;
        var map = isEmail ? keepEmail : keepLoose;
        if (!map.TryGetValue(key, out var best) || string.CompareOrdinal(root.DocId, best) < 0)
            map[key] = rootId;
    }

    // Pass 3: emit kept families; attachments never dropped independently (§3.5).
    var rows = new List<LoadFileRow>();
    foreach (var rootId in familyOrder)
    {
        var root = byId[rootId];
        bool isEmail = root.DocType == "email";
        bool keep = isEmail ? keepEmail[EmailIdentity.Key(root)] == rootId
                            : keepLoose[root.Sha256] == rootId;
        if (!keep) continue;

        var sent = SentDates.Normalize(root.SentDate); // "" for loose
        foreach (var d in families[rootId])
            rows.Add(new LoadFileRow {
                DocId = d.DocId, FamilyId = rootId, ParentId = d.ParentId ?? "",
                Custodian = d.Custodian, DocType = d.DocType, FileName = d.FileName,
                SentDate = sent, Hash = d.Sha256 });
    }

    rows.Sort((a, b) => string.CompareOrdinal(a.DocId, b.DocId));
    return new DedupResult(rows, batch.Documents.Count - rows.Count);
}
```
**Tests (`DeduperTests.cs`):**
1. Keep existing 3 tests (loose dedup, distinct kept, header) — must still pass.
2. **Attachment kept when identical bytes live in another family** (FW case): `DOC-000003` & `DOC-000005` both survive; `DOC-000005` retained.
3. **Cross-custodian email + attachments** (mlee `DOC-000009`/`010`/`011`) suppressed **as a family** when D1=GLOBAL.
4. **Nested family** `FamilyID` correctness (Task 2).
5. **Order independence (§6.1):** same batch, reversed document order ⇒ byte-identical `ToCsv` output.
**Run:** `dotnet test`. **Commit:** `fix(INGEST-4471): family-atomic dedup + cross-mailbox email dedup`.

---

### Task 5 — Golden output + integration test (5 min)
**Create:** `data/expected-tool-output.csv` — the corrected output for `batch-sample.json`:
- Rows (sorted by DocID), **12 rows**, suppressed **4** (`DOC-000009`, `-10`, `-11`, `-13`).
- `SentDate` normalised: `001/002/003 → 2024-03-05T19:22:00Z`, `004/005 → 2024-03-06T14:10:00Z`,
  `006 → 2024-03-06T16:45:00Z`, `012 → 2024-03-05T20:05:00Z`, `015/016 → 2024-03-07T16:00:00Z`, loose → empty.
- `DOC-000005` **present** (family of `DOC-000004`); `DOC-000009` **absent** (dup of `DOC-000001`).
**Test:** `test/DedupTool.Tests/SampleBatchTests.cs` — deserialise `../../data/batch-sample.json`, `Deduper.Run`, assert `LoadFileWriter.ToCsv` equals `data/expected-tool-output.csv` (normalise path separators; find repo root by walking up).
**Run:** `dotnet test`; then `cd src/DedupTool && dotnet run -- ../../data/batch-sample.json /tmp/after.csv && diff /tmp/after.csv ../../data/expected-tool-output.csv` → no diff.
**Commit:** `test: golden sample output and end-to-end comparison`.

---

### Task 6 — Fill `DECISIONS.md` (10 min)
Populate each section from the analysis above. Must cover:
- **Unclear/contradictory/silent:** D1 (§3.3 vs §4), §3.2 vs §3.5 (attachment bytes vs family intact — §3.5 wins), representative-selection rule (lowest DocID, chosen for §6.1; "first seen" rejected as order-dependent), malformed-date handling, CSV escaping silence, `parentId` chains with no email root.
- **What I changed & why:** B1–B5, one bullet each, referencing the spec clause.
- **Didn't get to:** CSV quoting/escaping, streaming for ~2M docs, no `.sln` (add one?), malformed-input validation.
- **Next:** performance pass, configurable representative rule, property-based order-independence test.
- **Reimplementation:** N/A (kept C#) — state that explicitly.
- **Working with the AI:** what it got right / what I pushed back on (see Task 7).
**Commit:** `docs: complete DECISIONS.md`.

---

### Task 7 — AI transcripts (5 min)
**Create:** `transcripts/` with the exported AI session(s) for this exercise.
Capture the key moments the interviewer cares about: how the tool was directed,
what was checked, and what was rejected/corrected (e.g. choosing lowest-DocID over
first-seen for §6.1; flagging the §3.3/§4 tension rather than silently guessing).
**Commit:** `docs: add AI transcripts`.

---

### Task 8 — Final verification (5 min)
```bash
cd /Users/karimmoustamid/Downloads/DedupTool
dotnet test                                   # all green
git status --short                            # only intended files
git log --oneline -8
```
Checklist:
- [ ] CLI contract + header + LF unchanged.
- [ ] `DOC-000005` present, `DOC-000009` absent, suppressed = 4 on the sample.
- [ ] All `SentDate` values UTC `...Z`; loose empty.
- [ ] Same output for reversed input order (test).
- [ ] `DECISIONS.md` complete; transcripts included.

---

## 5. Files touched

| File | Action |
|------|--------|
| `src/DedupTool/Deduper.cs` | rewrite `Run`, add `ResolveRoot` |
| `src/DedupTool/SentDates.cs` | new |
| `src/DedupTool/EmailIdentity.cs` | new |
| `src/DedupTool/LoadFileWriter.cs` | unchanged (possible stretch: CSV escaping) |
| `src/DedupTool/Program.cs` | likely unchanged |
| `test/DedupTool.Tests/SentDatesTests.cs` | new |
| `test/DedupTool.Tests/DeduperTests.cs` | extend |
| `test/DedupTool.Tests/SampleBatchTests.cs` | new |
| `data/expected-tool-output.csv` | new (golden) |
| `data/current-tool-output.csv` | **leave as-is** (before evidence) |
| `DECISIONS.md` | fill in |
| `transcripts/` | AI transcripts |

---

## 6. Risks / open questions

- **§6.1 vs "first seen".** Any rule where the kept copy depends on input order breaks §6.1. Lowest-DocID is a choice; confirm it's acceptable (documented in DECISIONS.md).
- **False merges.** Including `sentDate` in the email key guards against merging a genuine resend; confirm D3.
- **CSV escaping.** Spec is silent; sample has no commas/quotes in values. Deferred + documented unless you want it in scope.
- **Scale (§6.2).** Current approach is O(n) but fully in-memory. Fine for the sample; note as a gap for ~2M docs.
- **No `.sln`.** `dotnet test` from root works via project discovery; adding a `.sln` is optional polish.

## 7. Definition of done
Both the missing-attachment bug and §3.3 are fixed, load file matches SPEC.md,
`dotnet test` is green with new coverage, `DECISIONS.md` is complete, transcripts
are included, and the CLI contract is byte-for-byte preserved.
