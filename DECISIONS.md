# Decisions

## Where the spec was unclear, contradictory, or silent

3.2 vs 3.5. 3.2 says two attachments with the same sha256 are duplicates. 3.5 says a
family is produced intact. In the sample DOC-000003 (on DOC-000001) and DOC-000005 (on
DOC-000004) sit on two different emails and share a sha256, so 3.2 read literally
deletes DOC-000005 and leaves "FW vendor report" in review with no attachment.
Decided: 3.5 wins. Same-hash suppression runs between families, never inside one. An
attachment is only dropped because its family was dropped. Rejected: re-emit the
orphaned attachment, since its ParentID would point at a document not in the file.
Rejected: call the two emails duplicates because they share a file.

3.3/3.4 vs 4. 3.4 says dedup is batch-wide and 3.3 says a mailbox copy is a duplicate,
so when jsmith sends mlee a message hers is dropped. 4 says each custodian's set must be
complete and self-contained. Both cannot hold.
Decided: follow 3.3/3.4, the explicit instruction. The cost is real: mlee keeps
DOC-000012, 014, 015, 016 and loses the Q3 budget email plus both attachments, so her
set is not self-contained. Considered a --by-custodian mode that dedups within each
custodian and satisfies 4 without weakening 3.3, but it changes output for everyone, so
it is the question I would raise in the review call rather than decide alone.

Email identity. 3.3 needs to recognise mailbox copies and never says how, and the schema
has no messageId.
Decided: normalised from, plus sorted and lowercased to, plus trimmed subject, plus
sentDate as an instant, plus body with newlines normalised and trailing whitespace
trimmed. Joined with U+001F (unit separator). Strict, because the two failure directions
are not equally bad: too strict leaves a duplicate in the file, too loose merges two
documents and one never reaches review, which is the failure this tool exists to
prevent. Accepted: a copy re-wrapped in transit, or with a footer appended, will not
match its twin and the same content appears twice. Considered fuzzy body matching (needs
a threshold, and a wrong one loses documents silently) and the Message-ID header (not in
the schema). This is the call I would most like reviewed; I made it rather than wait.

Re:/Fw:. Silent. Decided: do not strip, stripping folds a real reply into the message it
replies to. DOC-000012 stays its own document.

Attachment matching a loose file. Silent. Decided: not a duplicate. 3.1 covers loose
against loose, 3.2 attachment against attachment; pooling them would delete an attachment
out of a family to save bytes, which 3.5 forbids.

Documents missing the fields identity depends on. Silent, and not handled. Key falls
back to empty strings, so two emails both missing from and sentDate with the same
to/subject/body get the same key and one is suppressed. Listed below as unfinished.

6.3 "documents suppressed": documents or families? Decided: documents, matching the
ticket's phrasing, which also makes the three numbers reconcile.

## What I changed and why

- Deduper.cs rewritten to decide at family granularity. The old code kept one HashSet of
  sha256 and dropped any document whose hash had been seen, so an attachment matching an
  earlier attachment was dropped on its own and its email stayed in review with nothing
  attached. That is DOC-000009 (as reported) and DOC-000004 (same bug, unreported). Now
  every document resolves to its family root, one decision is made per family, and the
  family is written or dropped whole.
- EmailIdentity.cs added, holding the 3.3 rule alone so "same email" is testable in
  isolation.
- SentDates.cs added: sentDate to UTC in the exact format 5 requires, fractional seconds
  dropped. A separate defect from the ticket (raw offsets in the output), also fixed.
- The survivor is the family root with the lowest docId, so the surviving set does not
  depend on input order. Reversing the sample on the old code swapped survivors: jsmith
  kept hers one way, mlee hers the other. Rows are written sorted by DocID, so 6.1 holds
  literally and the file is byte-identical across input orders.
- 19 tests added: 4 in DeduperTests.cs, 8 in EmailIdentityTests.cs, 7 cases in
  SentDatesTests.cs. Suite is 22 green. Against the original Deduper.cs, 4 fail:
  AttachmentWithBytesIdenticalToAnotherFamily_IsKept,
  CrossMailboxEmailFamily_IsSuppressedAsAWhole,
  ReversedInputOrder_ProducesIdenticalOutput,
  NestedFamily_FamilyId_ResolvesToTopLevelDocument.

Two changes beyond the ticket, flagged because they change column values rather than
being refactors. FamilyID is now the top-level ancestor instead of the immediate parent,
which the old code got wrong for a nested chain; no effect on this sample, which has no
chain deeper than one level. And family members inherit the root's sentDate, which 5
requires.

## What I didn't get to

- No guard for emails missing identity fields, per above. Needs a decision before code.
- An unparseable sentDate passes through unchanged instead of being flagged. Spec is
  silent; I preferred it visible over silently dropped.
- Nothing is streamed. The whole batch is held in memory plus a row per survivor. Fine
  for the sample, unmeasured at 6.2's two million.
- No CSV quoting. LoadFileWriter joins with commas and does not quote, so a fileName
  containing a comma or a quote corrupts the row. Real collections contain such names.
- Cycle and dangling-parentId paths do not throw, but nothing tests them and the FamilyID
  they produce is not meaningful. Spec 2 says the parent is always present.

## What I'd do next

1. Get a ruling on 3.3 vs 4, then implement --by-custodian if 4 is meant to win.
2. Add messageId to the input schema and prefer it over the derived key.
3. Stream the batch and measure at 2M documents.
4. Quote CSV fields.

## If you reimplemented in another language

I did not. It is .NET 8 and the fix is one rewritten class plus two small new ones, so a
rewrite would have added risk without changing the output. What to preserve if it were
rewritten: the family decision (no document is ever decided alone), the identity rule
and its strictness, a separator that cannot occur in the components, sentDate compared
as an instant, and the lowest-root-docId survivor rule.

## Working with the AI

What it got right: found the root cause and reproduced the reported bug byte-for-byte
against the committed baseline before changing anything. Reframed "attachments go
missing" as "decisions are made per document instead of per family", which is the actual
defect and generalises past the sample. Wrote the identity rule as a pure function,
which is what made the tests cheap.

What I had to correct or reject: scope crept silently, since moving family resolution to
the top-level ancestor also changed FamilyID and the inherited SentDate, and neither was
in the ticket nor called out; I found them reading the diff. Its first instinct on a
suppression bug was a better dedup key rather than a question about the granularity of
the decision, which would have looked plausible and not fixed the ticket. It picked the
batch-wide policy without flagging the cost. Its own summary of the work was wrong, which
is how this file got its first draft: test names that appear nowhere in the repo, 24
tests in a file that does not exist, 27 green when the suite is 22, "8 tests fail" when 4
do. Every number here is checked against the code and the test run; treat its prose as a
claim, not a record. It overwrote a committed fixture, pointing a verification run at
data/current-tool-output.csv, the checked-in baseline the bug was reported against;
restored from git and no longer modified. And the summary line does not move: 16 in, 12
out, 4 suppressed on both sides of the fix, so "did the counts change" passes while the
file is still wrong.

What I would tell a teammate: do not accept the summary line as evidence, diff row
identity and check git status before trusting a fixture comparison. Do not let it choose
output-affecting policy such as which copy survives or how identity is defined; those
need a human call and a written reason because they are invisible in review and expensive
later. And it will confidently default when a question goes unanswered, so keep the
questions in front of you.
