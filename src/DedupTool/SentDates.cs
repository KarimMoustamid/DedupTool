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
