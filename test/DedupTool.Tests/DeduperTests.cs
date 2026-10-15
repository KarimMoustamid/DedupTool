using DedupTool;
using Xunit;

namespace DedupTool.Tests;

public class DeduperTests
{
    private static Document Loose(string id, string custodian, string hash) => new()
    {
        DocId = id, Custodian = custodian, DocType = "loose", FileName = id + ".txt", Sha256 = hash, SizeBytes = 10,
    };

    private static Document Email(string id, string custodian, string hash,
        string from, string[] to, string subject, string sent, string body,
        string? parentId = null) => new()
    {
        DocId = id, Custodian = custodian, DocType = "email", FileName = id + ".eml",
        Sha256 = hash, SentDate = sent, From = from, To = new(to), Subject = subject,
        Body = body, ParentId = parentId,
    };

    private static Document Attachment(string id, string custodian, string parentId, string hash) => new()
    {
        DocId = id, Custodian = custodian, DocType = "attachment", ParentId = parentId,
        FileName = id + ".pdf", Sha256 = hash, SizeBytes = 10,
    };

    [Fact]
    public void AttachmentWithBytesIdenticalToAnotherFamily_IsKept()
    {
        // A1 (on E1) and A2 (on E2) are byte-identical. Family-atomic dedup must
        // NOT drop an attachment because another family has the same bytes (B1).
        var e1 = Email("E1", "c1", "h-e1", "a@x.com", new[] { "b@x.com" }, "s1", "2024-03-05T14:22:00-05:00", "b1");
        var a1 = Attachment("A1", "c1", "E1", "shaPDF");
        var e2 = Email("E2", "c1", "h-e2", "a@x.com", new[] { "c@x.com" }, "s2", "2024-03-06T09:10:00-05:00", "b2");
        var a2 = Attachment("A2", "c1", "E2", "shaPDF");

        var result = Deduper.Run(new Batch { Documents = new() { e1, a1, e2, a2 } });

        Assert.Equal(4, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
        Assert.Contains(result.Rows, r => r.DocId == "A1" && r.FamilyId == "E1");
        Assert.Contains(result.Rows, r => r.DocId == "A2" && r.FamilyId == "E2");
    }

    [Fact]
    public void CrossMailboxEmailFamily_IsSuppressedAsAWhole()
    {
        // Same message in two mailboxes: different bytes/sha256, casing, line
        // endings and offset-vs-Z date. The whole duplicate family is suppressed;
        // the lowest-DocID copy (and all its attachments) is kept (D1=GLOBAL).
        var e1 = Email("DOC-000001", "jsmith", "h-e1", "john.smith@example.com",
            new[] { "maria.lee@example.com" }, "Q3 budget review",
            "2024-03-05T14:22:00-05:00", "Hi Maria,\n\nAttached.\n");
        var a1 = Attachment("DOC-000002", "jsmith", "DOC-000001", "h-a1");

        var e9 = Email("DOC-000009", "mlee", "h-e9", "John.Smith@example.com",
            new[] { "maria.lee@example.com" }, "Q3 budget review",
            "2024-03-05T19:22:00Z", "Hi Maria,\r\n\r\nAttached.\r\n");
        var a9 = Attachment("DOC-000010", "mlee", "DOC-000009", "h-a9");

        var result = Deduper.Run(new Batch { Documents = new() { e1, a1, e9, a9 } });

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(2, result.Suppressed);
        Assert.DoesNotContain(result.Rows, r => r.DocId is "DOC-000009" or "DOC-000010");
        Assert.Contains(result.Rows, r => r.DocId == "DOC-000001" && r.FamilyId == "DOC-000001");
        Assert.Contains(result.Rows, r => r.DocId == "DOC-000002" && r.FamilyId == "DOC-000001");
    }

    [Fact]
    public void ReversedInputOrder_ProducesIdenticalOutput()
    {
        // §6.1: the load file must not depend on input order.
        var docs = new List<Document>
        {
            Loose("L1", "c1", "same"),
            Loose("L2", "c2", "same"),
            Email("E1", "c1", "h-e1", "a@x.com", new[] { "b@x.com" }, "subj", "2024-03-05T14:22:00-05:00", "body\n"),
            Email("E2", "c2", "h-e2", "A@X.com", new[] { "b@x.com" }, "subj", "2024-03-05T19:22:00Z", "body\r\n"),
            Attachment("A2", "c2", "E2", "h-a2"),
        };

        var forward = LoadFileWriter.ToCsv(Deduper.Run(new Batch { Documents = new(docs) }).Rows);
        var reversed = Enumerable.Reverse(docs).ToList();
        var backward = LoadFileWriter.ToCsv(Deduper.Run(new Batch { Documents = reversed }).Rows);

        Assert.Equal(forward, backward);
    }

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
    public void NestedFamily_FamilyId_ResolvesToTopLevelDocument()
    {
        // email -> attached email -> pdf. FamilyID must be the top-level email
        // for all three (B3: was the direct parent before).
        var root = new Document
        {
            DocId = "E1", Custodian = "c1", DocType = "email", FileName = "e1.eml",
            Sha256 = "h-e1", SentDate = "2024-03-05T14:22:00-05:00",
        };
        var inner = new Document
        {
            DocId = "E2", Custodian = "c1", DocType = "email", ParentId = "E1",
            FileName = "e2.eml", Sha256 = "h-e2", SentDate = "2024-03-05T15:00:00-05:00",
        };
        var pdf = new Document
        {
            DocId = "P1", Custodian = "c1", DocType = "attachment", ParentId = "E2",
            FileName = "p1.pdf", Sha256 = "h-p1",
        };

        var batch = new Batch { Documents = new() { root, inner, pdf } };

        var result = Deduper.Run(batch);

        Assert.Equal(3, result.Rows.Count);
        Assert.All(result.Rows, r => Assert.Equal("E1", r.FamilyId));
    }

    [Fact]
    public void LoadFile_StartsWithFixedHeader()
    {
        var csv = LoadFileWriter.ToCsv(Array.Empty<LoadFileRow>());

        Assert.StartsWith("DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash", csv);
    }
}
