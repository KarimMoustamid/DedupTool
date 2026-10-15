using DedupTool;
using Xunit;

namespace DedupTool.Tests;

public class EmailIdentityTests
{
    // DOC-000001 (jsmith) style: LF body, offset date, lowercase from.
    private static Document JsmithCopy() => new()
    {
        DocId = "DOC-000001", Custodian = "jsmith", DocType = "email",
        From = "john.smith@example.com",
        To = new() { "maria.lee@example.com" },
        Subject = "Q3 budget review",
        SentDate = "2024-03-05T14:22:00-05:00",
        Body = "Hi Maria,\n\nAttached is the Q3 budget.\n",
    };

    // DOC-000009 (mlee) style: CRLF body, Z date, mixed-case from.
    private static Document MleeCopy() => new()
    {
        DocId = "DOC-000009", Custodian = "mlee", DocType = "email",
        From = "John.Smith@example.com",
        To = new() { "maria.lee@example.com" },
        Subject = "Q3 budget review",
        SentDate = "2024-03-05T19:22:00Z",
        Body = "Hi Maria,\r\n\r\nAttached is the Q3 budget.\r\n",
    };

    [Fact]
    public void SameMessage_DifferentMailboxBytes_SameKey()
        => Assert.Equal(EmailIdentity.Key(JsmithCopy()), EmailIdentity.Key(MleeCopy()));

    [Fact]
    public void DifferentBody_DifferentKey()
    {
        var a = JsmithCopy();
        var b = JsmithCopy();
        b.Body = "Totally different content.";
        Assert.NotEqual(EmailIdentity.Key(a), EmailIdentity.Key(b));
    }

    [Fact]
    public void ToOrderIndependent_SameKey()
    {
        var a = JsmithCopy();
        var b = JsmithCopy();
        a.To = new() { "ray.kim@example.com", "maria.lee@example.com" };
        b.To = new() { "maria.lee@example.com", "ray.kim@example.com" };
        Assert.Equal(EmailIdentity.Key(a), EmailIdentity.Key(b));
    }

    [Fact]
    public void ToCasingAndWhitespace_NormalisedSameKey()
    {
        var a = JsmithCopy();
        var b = JsmithCopy();
        a.To = new() { "Maria.Lee@Example.com" };
        b.To = new() { "  maria.lee@example.com  " };
        Assert.Equal(EmailIdentity.Key(a), EmailIdentity.Key(b));
    }

    [Fact]
    public void DifferentSubject_DifferentKey()
    {
        var a = JsmithCopy();
        var b = JsmithCopy();
        b.Subject = "FW: Q3 budget review";
        Assert.NotEqual(EmailIdentity.Key(a), EmailIdentity.Key(b));
    }

    [Fact]
    public void DifferentSentInstant_DifferentKey()
    {
        // Guards against merging a genuine resend: same from/to/subject/body,
        // different send instant.
        var a = JsmithCopy();
        var b = JsmithCopy();
        b.SentDate = "2024-03-06T14:22:00-05:00";
        Assert.NotEqual(EmailIdentity.Key(a), EmailIdentity.Key(b));
    }

    [Fact]
    public void ExtraRecipient_DifferentKey()
    {
        var a = JsmithCopy();
        var b = JsmithCopy();
        b.To = new() { "maria.lee@example.com", "ray.kim@example.com" };
        Assert.NotEqual(EmailIdentity.Key(a), EmailIdentity.Key(b));
    }

    [Fact]
    public void SentOffsetVsZ_SameInstant_SameKey()
    {
        var a = JsmithCopy();
        var b = JsmithCopy();
        b.SentDate = "2024-03-05T19:22:00Z";
        Assert.Equal(EmailIdentity.Key(a), EmailIdentity.Key(b));
    }
}
