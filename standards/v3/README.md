# OBC Verifiable Record Standard v3

**Status:** Active  
**Introduced:** 2026  
**Schemas:** [`onchain-schema.json`](onchain-schema.json) | [`location-schema.json`](location-schema.json) | [`envelope-schema.json`](envelope-schema.json) | [`types.json`](types.json)

## 1. Overview

v3 changes how the off-chain document is located and makes it type-aware:

- The on-chain record memo only needs `v` and `h`, plus the version fields `d`, `n` and `p`.
- Files are stored as `{base}/{h}.json`.
- Storage bases are published in a **location account** derived from the publisher wallet. A storage move costs one transaction, and every past record resolves to the new location.
- The off-chain document is an **envelope** naming its document type and schema. The v2 journal content becomes the `data` of type `journal`.
- **Every version of a document is kept and linked on-chain** through `d` (document id), `n` (version number) and `p` (previous hash). Voiding a document publishes a **tombstone** version. A per-document **lookup key** `k` lets verifiers list all versions with one call.
- Verifiers must check and report **who signed** the record.

## 2. Terms

| Term | Meaning |
|---|---|
| Publisher wallet (P) | The wallet that signs record memos |
| Location account (L) | SPL Record account at `CreateWithSeed(P, "obc-meta", recr1L3PCGKLbckBqMNcJhuuyU1zgo8nBhfLVsJNwr5)` |
| Location authority (A) | The key allowed to write L. Initially P. May be transferred to a separate (cold) key or a multisig |
| Base | A storage prefix, e.g. `https://docs.client.com/obc` |
| Document type | What the record is, e.g. `journal`, `invoice` |
| Schema id | A versioned schema for one document type, `obc.<type>/<n>`, e.g. `obc.journal/2` |
| Document id (`d`) | Stable identifier shared by all versions of one source document. Opaque by default (section 3.2) |
| Version chain | The records with the same `d` and signer, linked by `n` and `p` |
| Tombstone | A version that marks the document as voided or deleted (section 4.5) |
| Tenant identity key (K) | Per-tenant secret used to compute `d`. Never published |

## 3. On-chain record memo

Memo instruction signed by P (P is the memo instruction's only account, as a signer). See [`onchain-schema.json`](onchain-schema.json).

- v3 records are published with **p-memo** (`Memo4c2pN8afCj432Lb7RMVKi9PbQnnW7ewFFaV3oAH`), the Pinocchio rewrite of the memo program with the same instruction format (data = UTF-8 memo).
- v1/v2 records, and v3 records published before the switch, use **SPL Memo** (`MemoSq4gqABAXKb96qnH8TysNcWxMyWCqXgDLGmfcHr`).
- **Verifiers accept a record memo from either program, for every version.**
- A v3 record transaction also carries a 0-lamport transfer to the document lookup key (section 3.3).

| Field | Required | Description |
|---|---|---|
| `v` | yes | `"3"` |
| `h` | yes | 64-char lowercase SHA-256 hex of the off-chain file bytes |
| `d` | yes | Document id, the same for every version of one source document (section 3.2) |
| `n` | yes | Version number as a JSON integer, starting at `1`, incremented by 1 per version |
| `p` | if `n` > 1 | `h` of the previous version (`n - 1`). Must be absent when `n` = 1 |
| `a` | no | Location hint (HTTPS URL). Optional, kept for transition and convenience. Never required for verification |
| *(tenant)* | no | Tenant-configured extra fields, as in v2 |

`a1`/`a2` are not used in v3.

First version, and a later correction of the same document:

```json
{"v":"3","h":"a3f1…(64 hex)","d":"5e0c…(64 hex)","n":1}
{"v":"3","h":"77b2…(64 hex)","d":"5e0c…(64 hex)","n":2,"p":"a3f1…(64 hex)"}
```

### 3.1 Reserved keys and size

- Single-letter keys and keys starting with `_` are reserved for the standard. Tenant extra fields must not use them.
- **Size limits** (UTF-8). Publishers must refuse to send a memo above the limit instead of truncating it:
  - **v3 (p-memo): ≤ 950 bytes.** The 1232-byte transaction size is the limit: a record transaction (one signer, the memo, the lookup-key transfer) has 252 bytes of overhead.
  - **v1/v2 (SPL Memo): ≤ 450 bytes.** SPL Memo's UTF-8 check costs about 350 compute units per byte, so a signed memo fails above about 520 bytes within the default compute budget.
  - A v3 memo with an HMAC `d` and `p` is about 230 bytes, plus up to ~200 for an `a` hint.
- Tenant extra fields are public and permanent.

### 3.2 Document id

| Mode | `d` | Who can link `d` to the real document |
|---|---|---|
| **Private (default)** | lowercase hex of `HMAC-SHA256(K, "{orgId}\n{sourceSystem}\n{sourceKey}")`, 64 chars | An auditor with the off-chain files, or one given K |
| **Public** (tenant opt-in) | `{orgId}:{sourceSystem}:{sourceKey}` in plain text, at most 128 UTF-8 bytes | Anyone |

Both modes use the same normalized parts:

- `orgId`: the OBC organisation id.
- `sourceSystem`: the source system name, trimmed and lowercased (e.g. `zoho`).
- `sourceKey`: `{SourceSystemDocumentId}/{DocumentJournalId}`. When several journals of one source document share a `DocumentJournalId`, they get `#1`, `#2`, … appended in id order.

- `d` always includes the org and the source system.
- `d` is deterministic, so re-fetching or re-importing a document continues the same chain.
- The off-chain `data` keeps the source document id, so anyone with the files can match `d` to the document they describe.
- Rotating K starts new chains for every document and must be planned like a key migration.

### 3.3 Document lookup key

Every v3 record transaction lists a per-document address `k`, so a verifier can list all versions of a document directly instead of scanning the publisher's history:

```
k = sha256( utf8("obc-doc") ‖ P (32 bytes) ‖ utf8(d) )   → 32 bytes, used as a Solana address
```

- `k` is the recipient of a **0-lamport `System.Transfer` from P** in the record transaction. Memo programs reject any account that isn't a signer, so `k` can't be an account of the memo instruction itself.
- Nobody holds a key for `k`, and it never becomes an account: the transfer creates nothing and adds no fee.
- `k` is part of the signed message, so it can't be removed from a record. It reveals nothing beyond `d`, which is already in the memo.
- Records without `k` (v3 records published before p-memo, all v1/v2 records) are still found by scanning (section 6, step 11).

Test vector: P = `DPVzN6gC9uQVmD1JEXmnEfG3rStwFGpotVhAoSVAtLM5`, d = `test-70c5043d0da44c2cae82ef02b6f2d5c8` gives k = `De6Ro1d5gNSZUdSNkeSY49Df2V3zVdDkBGXJYizrhq72`.

### 3.4 Versioning rules for publishers

- A new version is published only when the off-chain bytes change. If the new `h` equals the latest version's `h`, nothing is published.
- `n` and `p` always follow the latest **finalized** version of `d`. Two versions of the same `d` must not be published concurrently.
- A document voided or deleted in the source system, or removed on tenant request, gets a tombstone version (section 4.5) before or instead of silent removal.

## 4. Off-chain document

### 4.1 Envelope

See [`envelope-schema.json`](envelope-schema.json).

```json
{
  "version": 3,
  "type": "journal",
  "schema": "obc.journal/2",
  "links": [
    { "rel": "source", "type": "invoice", "h": "9b1d…64 hex…" }
  ],
  "data": { "...": "document content, validated against obc.journal/2" }
}
```

| Field | Required | Description |
|---|---|---|
| `version` | yes | `3` |
| `type` | yes | Document type, lowercase `[a-z0-9_-]+`, e.g. `journal`, `invoice` |
| `schema` | yes | Schema id `obc.<type>/<n>`. The `<type>` part must equal `type` |
| `links` | no | Related anchored records, see 4.3 |
| `status` | no | `active` (default) or `voided`, see 4.5 |
| `reason` | no | Free text for a tombstone, see 4.5 |
| `sourceModifiedAt` | no | When the document last changed in the source system (ISO 8601 UTC) |
| `data` | yes | Document content. Must validate against `schema` |

Unknown top-level fields must be ignored.

### 4.2 Type registry

- [`envelope-schema.json`](envelope-schema.json) defines the envelope.
- [`types.json`](types.json) lists the known schema ids and points to `types/<type>/<n>/schema.json`.
- Entries:
  - `obc.journal/2` ([schema](types/journal/2/schema.json)): the v2 journal content (the v2 off-chain document without its top-level `version`), with property names as OBC serializes them (PascalCase, e.g. `EntryNumber`, `LineItems`). **Required** for v3.
  - `obc.acc-period-close/1` ([schema](types/acc-period-close/1/schema.json), [specification](types/acc-period-close/1/README.md)): an accounting period close or reopen. Lists every record booked in the period with a count and a Merkle root, so an auditor with off-chain access can check that the period is complete.
  - `obc.invoice/1`: reserved for the invoice type. Its schema isn't defined yet.
- A type that can be a member of an accounting period names its booking-date field in `types.json` (`bookingDate`): `JournalDate` for `obc.journal/2`.
- A schema version, once published, never changes. New fields mean `obc.<type>/<n+1>`.
- Type schemas are closed: `obc.journal/2` sets `additionalProperties: false`, so a field the publisher adds later fails validation against it. That is intended. A publisher that adds a field must publish it under a new schema id (e.g. `obc.journal/3`) and keep emitting the old shape for the old id.
- Fields that the publisher omits when they are empty or null (such as `EntryNumber` when a journal has neither an entry number nor a reference) are optional in the schema. `JournalDate`, `LineItems`, `Taxes` and `CustomFields` are always emitted, and every line item always has `Amount` and `Tags`.

### 4.3 Links

Records are anchored one by one, but different documents can belong together (an invoice and the journals it produced). `links` records this without any on-chain change:

| Field | Required | Description |
|---|---|---|
| `rel` | yes | `source` (this record was derived from the target), `derived` (the target was derived from this record), or another registered value |
| `type` | no | The target's document type, as a hint |
| `h` | yes | The target's off-chain hash. The verifier resolves it the same way as any record (`{base}/{h}.json`) |

A link can only point to a record whose hash is already known, so publishers anchor targets first.

Versions of the **same** document are not expressed with `links`. They use the on-chain `d` / `n` / `p` fields (section 3), which are signed into the anchor itself.

### 4.4 Storage and serving

- Stored at `{base}/{h}.json` for every base listed in L.
- `h` is the hash of the exact stored bytes. The publisher serializes the envelope **once** and stores and hashes those bytes. Verifiers never re-serialize.
- Every version is a separate file. Publishers never overwrite or delete the file of an earlier version as part of republishing.
- Served over HTTPS with `200 OK`, raw bytes, no transformation. The SHA-256 of the response body must equal `h`.
- Base URLs must not contain credentials (SAS tokens, API keys). Access control belongs to the storage layer.

### 4.5 Version metadata and tombstones

A **tombstone** is a normal version (`n + 1`, `p` = last `h`) whose envelope has `"status": "voided"`. Its `data` may be empty (`{}`) so a deletion request can be honoured off-chain while the fact of the deletion stays on-chain. Verifiers **don't validate `data` against the schema** for a voided record (section 6, step 9): the envelope is still validated, and the hash check still applies. After a tombstone, no further versions of that `d` may be published.

```json
{"version":3,"type":"journal","schema":"obc.journal/2","status":"voided","reason":"voided in source system","data":{}}
```

## 5. Location account

### 5.1 Address and ownership

- Address: `CreateWithSeed(P, "obc-meta", RecordProgram)`, i.e. `SHA-256(P || "obc-meta" || recr1L3PCGKLbckBqMNcJhuuyU1zgo8nBhfLVsJNwr5)`, created with `SystemProgram.CreateAccountWithSeed` (P signs as base).
- Owner: SPL Record program (`recr1L3PCGKLbckBqMNcJhuuyU1zgo8nBhfLVsJNwr5`). Verifiers must reject an account at that address with any other owner.

### 5.2 Layout

| Offset | Size | Content |
|---|---|---|
| 0 | 1 | Record program version (`1`) |
| 1 | 32 | Authority public key |
| 33 | n | UTF-8 JSON, zero-padded |

- Recommended data size: **1024 bytes**.
- The JSON must be **≤ 900 bytes** so every update fits in one transaction (atomic for readers).
- Each update is one `Write` instruction at offset 0 with the JSON followed by zero padding that covers at least the previous content. Readers trim trailing `0x00` bytes before parsing.

### 5.3 JSON content

See [`location-schema.json`](location-schema.json).

```json
{"v":1,"s":2,"b":["https://docs.client.com/obc","https://old.blob.core.windows.net/journals"]}
```

| Field | Required | Description |
|---|---|---|
| `v` | yes | Location format version, `1` |
| `s` | yes | Sequence number, incremented on every update |
| `b` | yes | 0–10 bases, newest/preferred first. Either `https://…` URLs or opaque labels `label:<name>` |
| `succ` | no | Address of another location account to use instead (key rotation, see 5.5) |

Unknown fields must be ignored.

**Opaque labels** (`label:vault-1`) are for private or air-gapped tenants that must not publish hostnames on a public chain. Verifiers resolve labels from local configuration. A public verifier skips them.

### 5.4 Updating locations (storage move)

1. Copy all files to the new storage under the same `{h}.json` names.
2. Write L with the new base first and the old base still listed. Increment `s`.
3. Wait for finalization and check a sample of records resolves through the new base.
4. Remove the old base from L in a later update, then retire the old storage.

### 5.5 Key rotation

- Records signed by the old key (P1) resolve L1 = derive(P1). Records signed by the new key (P2) resolve L2 = derive(P2).
- Before P1 is retired, either:
  - transfer L1's authority (`SetAuthority`) to whoever manages locations from now on and keep L1 and L2 in sync, or
  - write `{"v":1,"s":…,"b":[],"succ":"<L2>"}` into L1 so verifiers follow it to L2.
- If the location authority is a separate cold key from day one, rotating the publisher key needs no change to L1 at all.
- If P1 (as authority) is lost before either step, records signed by P1 stay on their last published locations.

## 6. Verification protocol

1. **Fetch the transaction** at `finalized` commitment. Reject if not found or if `meta.err` is not null.
2. **Extract the memo** from an instruction of the SPL Memo **or** the p-memo program, reading the instruction data (some RPC providers only fill the `memo` field of `getSignaturesForAddress` for program ids they recognize), and parse it as JSON. Assert `v == "3"`, the `h` format, that `d` and `n` are present, and that `p` is present exactly when `n` > 1.
3. **Identify the signer** P of the memo instruction. A v3 memo must be signed by P: P must be a signer listed on the memo instruction itself. There is no fallback to the fee payer, and a v3 memo instruction without a signer is rejected. Report P to the user. If the verifier knows the expected organisation wallet, assert that it is a signer of the memo instruction.
4. **Resolve locations:**
   1. Derive L from P. Fetch it with `getAccountInfo`.
   2. If it exists, assert the owner is the SPL Record program and the version byte is `1`. Parse the JSON.
   3. If `succ` is present, repeat with that address (at most 5 hops, stop on cycles).
5. **Build candidate URLs:** `{base}/{h}.json` for each base in `b` (in order), then the memo's `a` if present.
6. **Fetch** each candidate until one succeeds:
   - HTTPS only. Reject hosts that resolve to private, loopback or link-local addresses. Re-check after redirects.
   - Size cap (50 MB, which fits a period close of about 250,000 members) and timeout.
7. **Hash** the raw response bytes with SHA-256 and compare with `h`. Report which source matched.
8. **Validate** the envelope against `envelope-schema.json`. Check that `schema` matches `type`.
9. **Validate** `data` against the schema named in `schema`. If the verifier doesn't know that schema id, it reports the record as **integrity verified, type not recognized** (steps 1–7 passed) rather than failing. **Skip this step for a tombstone** (`"status": "voided"`): report the record as **voided**, with its `reason`.
10. **Report links** with their `rel` and `h`. A verifier may verify linked records too (steps 4–9 with the link's `h`, using the same signer's locations). A broken link doesn't change the result for this record.
11. **Check the version chain.** Only memos **signed by P** count. Memos from other signers that reuse the same `d` are ignored.
    1. **List the document's records:** derive `k` from P and `d` (section 3.3) and call `getSignaturesForAddress(k)`. Keep only transactions signed by P whose memo has this `d`.
    2. **Fallback scan:** for records published without `k` (v3 records before the p-memo switch), page through `getSignaturesForAddress(P)` instead and filter the same way.
    3. **Backwards:** if `n` > 1, the previous version is the record with `n - 1` and `h == p`. If it can't be found, report **chain gap** (warning).
    4. **Forwards:** if a record with a higher `n` exists, report **superseded by version N (date)**, and **voided** if the latest is a tombstone.
    5. **Forks:** two different signed records with the same `d` and `n`, or two with the same `p`, are reported as **fork** (warning). The verifier never silently picks one.
    6. The integrity result from step 7 stands on its own. A superseded, voided or forked record is still reported as **authentic**, with the history next to it.

For an `acc-period-close` record, step 9 also recomputes the member count and Merkle root. Checking that a whole period is complete is a separate auditor procedure that needs off-chain access; see [acc-period-close section 5](types/acc-period-close/1/README.md#5-verifying).

The location account only affects **where** the verifier looks. A wrong, stale or hostile location can make verification fail, never succeed falsely, because step 7 checks the bytes.

## 7. Backward compatibility

- v1/v2 records keep verifying as v1/v2, through `a` / `a1` / `a2`.
- Verifiers should apply steps 1, 3 and 6 (transaction success, signer reporting, safe fetching) to v1/v2 records as well.
- v1/v2 off-chain documents have no envelope. Verifiers treat them as type `journal`.
- v1/v2 memos have no `d`/`n`/`p`, so they have no version history.
- Existing v1/v2 data is not migrated. A document published again under v3 starts a new chain at `n = 1`.

## 8. Security and privacy considerations

- **Spam:** anyone can reference any address in a transaction, so transaction histories can be flooded. Location resolution never scans history, and only the authority can change account data.
- **Authority compromise** affects availability, not integrity: an attacker could point verifiers at wrong locations, but cannot make altered files verify.
- **Public metadata:** bases are public. Use opaque labels when hostnames are sensitive. No secrets in bases.
- **Document type stays off-chain.** The memo reveals neither what kind of document was anchored nor how records relate.
- **Why `d` is opaque:** the memo is public and permanent. A plain id leaks booking volume and correction rate, can contain sensitive references and can never be erased. An unkeyed hash of a short id is reversible by brute force. Hence HMAC with a tenant key, with the public mode as an explicit opt-in.
- **What version history does and doesn't guarantee:**
  - **Covered:** a published version can't be changed without detection (step 7), and replacing or voiding it under the same `d` is visible to anyone (step 11).
  - **Changes before the first publish** are out of scope. The chain shows what was certified, not every edit made in the books.
  - **Deliberate chain breaking:** an insider could publish a changed document under a new `d`. A public verifier can't detect this in private mode; an auditor can, because both off-chain files carry the same source document id.
  - **Deleted off-chain files:** the chain still proves that a version existed and when, but its content is gone if the tenant deletes it.
  - **Publisher key compromise:** whoever holds P can write history, including fake versions and tombstones.
  - **K compromise** (private mode) only reveals which records belong to which source document. It doesn't affect integrity.
- **Discovery cost:** with `k`, finding every version of a document is one `getSignaturesForAddress(k)` call (step 11.1). Anyone can list `k` in their own transactions, but those aren't signed by P, so they're dropped: spam can make the list longer, never wrong. Records without `k` still need the scan of P's history.
- **History retention:** verifying older records needs an RPC node that keeps full transaction history.
- **Program trust:** the SPL Record program is a standard audited program, but its maintainers can upgrade it.
- **p-memo is upgradeable**, while SPL Memo is immutable. An upgrade can only change how future memos are processed: a published record's memo is part of a finalized transaction and can't change. If p-memo stopped accepting memos, publishers would fall back to SPL Memo, which verifiers accept anyway.

## 9. Costs

- Location account: ~0.006 SOL rent for 1024 data bytes, one-time and refundable on close.
- Each location update: one transaction (5000 lamports).
- Record memos: one transaction per record (5000 lamports). About 230 bytes with `d`/`n`/`p` and no `a`.
- Compute per v3 record: about 600–1,500 CU with p-memo (memo plus lookup-key transfer), versus 70,000–190,000 CU with SPL Memo.
- Each correction or void is one more record transaction.

## Appendix A: SPL Record instruction encoding

| Instruction | Data | Accounts |
|---|---|---|
| Initialize | `0x00` | `[w]` record, `[]` authority |
| Write | `0x01` + offset `u64 LE` + length `u32 LE` + bytes | `[w]` record, `[s]` authority |
| SetAuthority | `0x02` | `[w]` record, `[s]` current authority, `[]` new authority |
| CloseAccount | `0x03` | `[w]` record, `[s]` authority, `[w]` lamport receiver |
| Reallocate | `0x04` + data length `u64 LE` | `[w]` record, `[s]` authority (top up rent first with a System transfer) |
