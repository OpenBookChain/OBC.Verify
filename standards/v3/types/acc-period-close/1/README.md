# Document type `acc-period-close` (schema `obc.acc-period-close/1`)

**Status:** Active  
**Standard:** [OBC v3](../../../README.md). Registers a new document type and adds an optional auditor procedure; nothing in v3 changes.  
**Schema:** [`schema.json`](schema.json)

## 1. Overview

v3 proves that each anchored record is unchanged and shows its version history. It can't show that a **set** of records is complete: a publisher could never anchor a document, or anchor one for an old period months later.

Closing an accounting period is therefore **another v3 document**:

- It is published exactly like a journal: v3 memo (`v`, `h`, `d`, `n`, `p`), lookup key `k`, file at `{base}/{h}.json`. On-chain it can't be told apart from any other record.
- Its `data` lists every record booked in the period (`d`, `n`, `h`, booking date), with a count and a Merkle root.
- One period has one stable `d`. Closing, reopening and closing again are versions `n`, `n+1`, … of that document, so the ordinary version chain shows how often a period was closed and reopened, and when.
- Each close links to the previous period's close (`rel: "previous-period"`), so the periods form a chain and a missing period shows up as a gap.
- Only someone with off-chain access can read a close. With it they can rebuild the books period by period (section 5).

## 2. Terms

| Term | Meaning |
|---|---|
| Period | A booking-date range `[from, to]` (inclusive ISO 8601 dates) of one publisher. Periods of one publisher never overlap |
| Booking date | The date a document is booked under. Each type names its field in [`types.json`](../../../types.json) (`bookingDate`); for `obc.journal/2` it is `JournalDate` (its date part) |
| Member | A document whose latest version's booking date falls in the period |
| Close | A version of the period's document with `state: "closed"` |
| Reopen | A version with `state: "reopened"` |

## 2.1 Document id of a period

`d` is computed as in v3 section 3.2 with `sourceSystem` = `obc` and `sourceKey` = `acc-period/{from}/{to}`:

- Private mode (default): HMAC with the tenant key. Nobody without the key can tell that a record is a period close.
- Public mode: e.g. `7:obc:acc-period/2026-09-01/2026-09-30`. Verifiers check that the range in the id matches `from`/`to`.
- The range is part of the id, so it can't change. A wrong range is voided (tombstone) and new periods are closed.

## 3. Off-chain document

```json
{
  "version": 3,
  "type": "acc-period-close",
  "schema": "obc.acc-period-close/1",
  "links": [
    { "rel": "previous-period", "type": "acc-period-close", "h": "c41e…64 hex…" }
  ],
  "status": "active",
  "data": {
    "from": "2026-09-01",
    "to": "2026-09-30",
    "state": "closed",
    "reason": "Month-end close",
    "closedAt": "2026-10-05T14:02:11Z",
    "count": 3,
    "root": "8d0f…64 hex…",
    "members": [
      { "d": "0a91…", "n": 1, "h": "a3f1…", "type": "journal", "date": "2026-09-03" },
      { "d": "5e0c…", "n": 2, "h": "77b2…", "type": "journal", "date": "2026-09-14" },
      { "d": "e7d2…", "n": 3, "h": "19aa…", "type": "journal", "date": "2026-09-30", "status": "voided" }
    ]
  }
}
```

### 3.1 `data` fields

| Field | Required | Description |
|---|---|---|
| `from`, `to` | yes | Period range, inclusive ISO 8601 dates. `to` ≥ `from` |
| `state` | yes | `closed` or `reopened` |
| `reason` | no | Free text |
| `closedAt` | no | When the user confirmed the action (informational; the block time is authoritative) |
| `count` | if `closed` | Number of entries in `members` |
| `root` | if `closed` | Merkle root of `members` (3.2), lowercase hex |
| `members` | if `closed` | Every member, sorted by `d` (ordinal order of the UTF-8 bytes), one entry per `d` |
| `totals` | no | Per-currency debit/credit sums of the active members, as decimal strings |

A `reopened` version has no `members`, `count` or `root`.

Member entry:

| Field | Required | Description |
|---|---|---|
| `d` | yes | The member's document id |
| `n` | yes | The member's latest finalized version when the close was published |
| `h` | yes | That version's hash |
| `type` | yes | Its document type |
| `date` | yes | Its booking date, inside the period |
| `status` | no | `voided` when version `n` is a tombstone. Voided documents stay members |

Unknown fields are ignored, as for every envelope. A voided period is an ordinary tombstone (`"status": "voided"`, empty `data`) with the same type and schema; it counts as never closed.

### 3.2 Merkle root

RFC 6962 Merkle tree hash over `members` in their listed order:

```
leaf(m)   = SHA-256( 0x00 ‖ utf8(m.d) ‖ 0x0A ‖ utf8(decimal m.n) ‖ 0x0A ‖ utf8(m.h) )
node(l,r) = SHA-256( 0x01 ‖ l ‖ r )
MTH(list) = leaf(list[0]) for one member; otherwise node(MTH(first k), MTH(rest)),
            k = the largest power of two smaller than the count
root      = MTH(members)      (no members: SHA-256 of the empty string)
```

Test vectors: members `d = doc-{i}`, `n = i + 1`, `h = hex(SHA-256("h{i}"))` for `i = 0 … count-1`:

| Members | Root |
|---|---|
| 0 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |
| 1 | `fe5845309210c9e11800fefff8ebd5f34e5eb0eb7688ac006252d4f8d9ea4164` |
| 2 | `96b6c2daccf3185ddfc05891f5cbbd76072adc70240e8b6d75b5de9d35bd3e4a` |
| 3 | `7a5a97bdded18f7ff402b4723915b196e67ef7c823400a143cd022252f42a48b` |
| 7 | `a18152a88628396c54f324ce65b20a0ec0eca977ae97f8df983ad501720308dd` |

The close's own `h` already commits to the whole member list. The root allows later inclusion proofs (one document belongs to a closed period, without revealing the others); proofs aren't part of `/1`.

### 3.3 Versioning rules

- `n = 1` is `closed`. After that, versions alternate `closed` → `reopened` → `closed` → …
- Every `closed` version lists the **full** member set again, not a diff.
- `previous-period` points at the `h` of the then-current close of the period that ends the day before `from`. It's absent only for the publisher's first period. If that period is later reopened and closed again, the link keeps pointing at the old version; verifiers follow the period's own version chain forward.

## 4. Publisher rules

1. A close is refused while any document booked in the period is still being published.
2. `members` lists every document of the publisher booked in the period, at its latest finalized version, tombstones included.
3. Periods close in order, without gaps or overlaps. The first period starts the books.
4. While a period is closed, nothing booked in it (old or new booking date) is published: no new document, version or tombstone. The user reopens first.
5. Closing and publishing are serialized, so nothing lands between building the member list and anchoring the close.
6. All members are v3 records.
7. A close file is at most 50 MB (about 250,000 members), the verifier's fetch cap.

## 5. Verifying

### 5.1 A single close or reopen

Verified like any v3 record (v3 section 6): integrity, signer, envelope, schema and version chain. For this type the verifier also:

- recomputes `count` and `root` from `members`, and checks the member order and that every member's date is inside the range. A mismatch fails the data check, as a schema violation would;
- checks that a reopen has no members, count or root;
- in public mode, checks that the range in `d` matches `from`/`to` (warning);
- reports the `previous-period` link.

### 5.2 Rebuild and completeness check (auditor, off-chain access)

Input: the publisher wallet P, off-chain access (storage bases, or a map for the location account's `label:` bases) and, optionally, the transaction of the latest close.

1. **Collect periods.** Read P's history (`getSignaturesForAddress(P)`) and keep the valid v3 memos signed by P. Fetch each record's file, check its hash and read its type, status and booking date (a tombstone keeps the date of the version it voids). Group the period records by `d`; each period's latest version gives its state. Starting from the latest close, follow `previous-period` links back.
2. **Check coverage.** Ranges must not overlap, and each period must start the day after the previous one ends: otherwise **period gap** or **period overlap**. A link to a period that doesn't end the day before is a **broken link**.
3. **Check each close.** Its data rules (5.1) and versioning rules (3.3). Every member must be a record signed by P with the listed `n` and `h`, published before the close, and its file must have the listed type, status and booking date, inside the range: otherwise **broken member**.
4. **Classify every record** booked in a period, by slot order against the period's versions:

| Finding | Meaning | Result |
|---|---|---|
| Listed in the close at exactly that `n`/`h` | Covered | ok |
| Published while the period was open or reopened, and listed (or superseded by a listed version) in the following close | Normal correction | ok |
| Published while the period was closed, for a document the close doesn't list | **Late entry** | failed |
| Published while the period was open, but its document is missing from the following close, or listed at an older version | **Omitted** | failed |
| A newer version (or tombstone) of a listed member, published while the period was closed | **Changed after close** | failed |
| A listed member can't be found, has another `h`, date or type, or isn't signed by P | **Broken member** | failed |
| The latest version is a reopen | **Open** | reported, not a failure |

5. **Report per period:** complete / failed (with every finding) / open / voided, how many times it was closed and reopened, with dates.

A failed check doesn't change the integrity result of any single record: each record stays authentic on its own. Records whose files can't be read can't be placed in a period, so the report counts them; so does a partly read history.

## 6. Security and privacy

- **What a close proves:** at its block time, the holder of P declared these exact versions to be the whole period. Adding, changing, voiding or backdating a record in the period afterwards is detectable by the rebuild.
- **What it doesn't prove:** that the source books had nothing else. A document never anchored and not listed before the first close is invisible.
- **Reopening is legitimate but visible:** every reopen is a permanent version with its date.
- **On-chain footprint:** one ordinary record per close or reopen. In private mode the public learns nothing beyond "P published a record".
- **Off-chain content:** the member list reveals document ids, booking dates and counts to whoever can read the files, the same audience that can read the documents.
