namespace DedupTool;

/// <summary>
/// Suppresses duplicate documents in a batch. The family is the atomic unit:
/// an email and its attachments are kept or suppressed together, and duplicate
/// representatives are chosen deterministically (lowest DocID) so the load file
/// is identical regardless of input order (SPEC §6.1). Loose files dedupe
/// globally by SHA-256; emails dedupe by normalised content key (SPEC §3.3).
/// </summary>
public static class Deduper
{
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
}
