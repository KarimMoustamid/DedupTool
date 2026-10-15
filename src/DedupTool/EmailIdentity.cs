using System.Security.Cryptography;
using System.Text;

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
