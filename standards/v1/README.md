# OBC Standard — Version 1

**Standard ID:** `obc-v1`  
**Status:** Active  
**Introduced:** 2025  
**Description:** The first published standard for On-chain Books Chain (OBC) certified financial journal records.

> **Source of truth:** This standard is derived from the OBC platform source code. Where behaviour is
> controlled by tenant configuration (e.g. anonymization rules, extra on-chain fields, additional
> publisher targets) the specification describes the _possible_ values; a specific record may omit
> optional fields.

---

## Overview

OBC publishes financial journal entries in two parts:

| Part | Location | Purpose |
|------|----------|---------|
| **On-chain record** | Solana blockchain (transaction memo) | Tamper-proof, minimal fingerprint |
| **Off-chain document** | Azure Blob Storage (public JSON file) | Full human/machine-readable journal |

To verify a record, you retrieve the on-chain transaction, extract the `h` hash and `a` file URL, download the off-chain JSON, hash it with SHA-256, and confirm the hashes match.

---

## 1. On-Chain Record (`obc-v1-onchain`)

The on-chain record is stored as a UTF-8 JSON string in the Solana transaction memo instruction.

### Schema

```json
{
  "v": "1",
  "h": "<sha256-hex>",
  "a": "<https://...blob.core.windows.net/.../<filename>.json>"
}
```

### Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `v` | string | ✅ | Standard version. Always `"1"` for this standard. |
| `h` | string | ✅ | SHA-256 hex digest (lowercase, 64 chars) of the canonical off-chain JSON document. |
| `a` | string | ✅ | HTTPS URL to the **primary** off-chain JSON document (first successful file-storage target). |
| `a1`, `a2`, … | string | ⬜ | URLs to additional file-storage targets when more than one is configured (e.g. a second Azure container, IPFS). Index starts at `1` for the second URL. |

> **Tenant-configured extra fields:** If the `Publish / OnChainFields` configuration key is set, the
> platform appends additional top-level journal properties directly to the on-chain memo. For example
> `EntryNumber` or `JournalDate` may appear as string values. Their presence is **optional** and
> varies per tenant. Verifiers should tolerate unknown keys.

### Example

```json
{
  "v": "1",
  "h": "a3f1c2e4b5d67890abcdef1234567890abcdef1234567890abcdef1234567890",
  "a": "https://myaccount.blob.core.windows.net/journals/ORG1-JE-2025-00042.json"
}
```

---

## 2. Off-Chain Document (`obc-v1-offchain`)

The off-chain document is a JSON file stored in one or more configured file-storage targets
(Azure Blob Storage, IPFS, or any future target). Its SHA-256 hash **must** match the `h` field
on-chain.

### File Naming

The file is named using the internal journal ID from the source accounting system:

```
file-<documentJournalId>.json
```

Example: `file-zoho-je-99887766.json`

> This name is stable and idempotent — re-publishing the same journal produces the same file name,
> allowing the platform to detect and skip duplicate uploads.

### Root Object

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `entryNumber` | string \| null | ⬜ | Journal entry number from the accounting system. |
| `documentReference` | string \| null | ⬜ | Source document reference. |
| `documentJournalId` | string \| null | ⬜ | Internal journal ID from the accounting system. |
| `journalDate` | string (ISO 8601) | ✅ | Accounting date of the journal entry. |
| `notes` | string \| null | ⬜ | Free-text notes. |
| `currencyId` | string \| null | ⬜ | Currency identifier. |
| `currencyCode` | string \| null | ⬜ | ISO 4217 currency code (e.g., `"AUD"`, `"USD"`). |
| `currencySymbol` | string \| null | ⬜ | Currency display symbol (e.g., `"$"`). |
| `exchangeRate` | number | ✅ | Exchange rate to base currency at time of entry. |
| `journalType` | string \| null | ⬜ | Journal type (e.g., `"manual"`, `"system"`). |
| `vatTreatment` | string \| null | ⬜ | VAT/GST treatment applied. |
| `productType` | string \| null | ⬜ | Product type classification. |
| `includeInVatReturn` | boolean | ✅ | Whether this journal is included in the VAT return. |
| `isBasAdjustment` | boolean | ✅ | Whether this is a BAS adjustment (AU). |
| `lineItemTotal` | number | ✅ | Sum of all line item amounts (before tax). |
| `total` | number | ✅ | Total including tax in transaction currency. |
| `bcyTotal` | number | ✅ | Total in base currency. |
| `pricePrecision` | integer | ✅ | Number of decimal places used in amount fields. |
| `originalCreatedTime` | string (ISO 8601) | ✅ | When the original document was created in the source system. |
| `lastModifiedTime` | string (ISO 8601) | ✅ | When the document was last modified in the source system. |
| `originalStatus` | string \| null | ⬜ | Status of the document in the source system. |
| `lineItems` | array of `LineItem` | ✅ | Journal debit/credit lines. Must not be empty. |
| `taxes` | array of `Tax` | ✅ | Tax breakdown. May be empty. |
| `customFields` | array of `CustomField` | ✅ | Tenant-defined custom metadata. May be empty. |

### `LineItem` Object

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `accountCode` | string \| null | ⬜ | Chart of accounts code. |
| `accountName` | string \| null | ⬜ | Chart of accounts name. |
| `description` | string \| null | ⬜ | Line description. |
| `debitOrCredit` | string \| null | ⬜ | `"debit"` or `"credit"`. |
| `amount` | number | ✅ | Absolute amount for this line. |
| `customerName` | string \| null | ⬜ | Associated customer name (anonymized if anonymization is enabled). |
| `projectName` | string \| null | ⬜ | Associated project name. |
| `tags` | array of `Tag` | ✅ | Tracking tags. May be empty. |

### `Tax` Object

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `taxName` | string \| null | ⬜ | Name of the tax component. |
| `taxAmount` | number | ✅ | Amount of tax. |

### `CustomField` Object

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `fieldName` | string \| null | ⬜ | Custom field label. |
| `fieldValue` | string \| null | ⬜ | Custom field value. |

### `Tag` Object

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `tagName` | string \| null | ⬜ | Tag category name. |
| `tagValue` | string \| null | ⬜ | Tag value. |

---

## 3. Hashing & Serialization Rules

These rules define how the canonical hash (`h`) is computed.

1. **Serializer:** `System.Text.Json` with `JsonIgnoreCondition.WhenWritingNull` and `ReferenceHandler.IgnoreCycles`.
2. **Property naming:** camelCase (default `System.Text.Json` behavior).
3. **Encoding:** UTF-8, no BOM.
4. **Whitespace:** No indentation, no extra whitespace (minified).
5. **Hash algorithm:** SHA-256.
6. **Hash encoding:** Lowercase hexadecimal string (64 characters).

> ⚠️ Any deviation from these serialization rules will produce a different hash and fail verification.

---

## 4. Verification Algorithm

```
1. Obtain the Solana transaction ID from the user or record.
2. Fetch the transaction from the Solana RPC node.
3. Extract the memo instruction data (UTF-8 decode).
4. Parse the memo JSON.
5. Assert: memo["v"] == "1"
6. Read h_onchain = memo["h"]
7. Read file_url   = memo["a"]
8. HTTP GET file_url → raw_bytes
9. h_computed = SHA256(raw_bytes).ToLowerHex()
10. Assert: h_computed == h_onchain
11. Parse raw_bytes as UTF-8 JSON → off-chain document
12. VERIFICATION PASSED ✅
```

---

## 5. Anonymization Notes

OBC supports per-field, per-tenant anonymization rules. Anonymization is applied **before**
hashing — the `h` value on-chain covers the already-anonymized document.

Anonymization is evaluated at the `LineItem` level. The following fields can be anonymized:

| Field (in `lineItems`) | Description |
|------------------------|-------------|
| `accountName` | Chart of accounts name |
| `accountCode` | Chart of accounts code |
| `customerName` | Customer name |
| `taxName` | Tax component name |
| `taxAuthorityName` | Tax authority name |
| `acquisitionVatName` | Acquisition VAT name |
| `reverseChargeVatName` | Reverse charge VAT name |
| `projectName` | Project name |

Each field has an independent rule stored per tenant as a **DynamicExpresso expression** evaluated
against the line-item entity. Example condition: `entity.AccountCode.StartsWith("900")`.

When a field is anonymized its value is replaced by a deterministic 8-character alphanumeric token
(seeded from the original value). The same input always produces the same token within a tenant,
so records from the same entity are consistently obfuscated.

Verifiers **should not** attempt to reverse anonymized values. The presence of short random-looking
strings in the fields listed above indicates anonymization was applied.

---

## 6. Publisher Targets

In v1, the platform supports the following publisher targets. The targets active for any given
record depend on tenant configuration.

| Category | Type key | Status | Notes |
|----------|----------|--------|-------|
| File Storage | `azure` / `azureblob` | ✅ Production | Azure Blob Storage — primary target |
| File Storage | `ipfs` | ⚠️ Preview | IPFS — implementation in progress |
| Blockchain | `solana` | ✅ Production | Solana — memo instruction |
| Blockchain | `ethereum` | 🔜 Planned | Not yet implemented |

File-storage URLs appear in the on-chain memo as `a`, `a1`, `a2`, … in priority order.

---

## 7. Versioning Policy

| Version | Standard ID | Status | Notes |
|---------|-------------|--------|-------|
| 1 | `obc-v1` | ✅ Active | Initial release |

When a new standard version is introduced:
- The `v` field in the on-chain memo will be incremented (e.g., `"2"`).
- A new folder `standards/v2/` will be published with a new `README.md` and JSON Schema.
- Old versions remain verifiable via their respective schemas.

---

## 8. Example Full Record

### On-Chain Memo
```json
{
  "v": "1",
  "h": "a3f1c2e4b5d67890abcdef1234567890abcdef1234567890abcdef1234567890",
  "a": "https://myaccount.blob.core.windows.net/journals/ORG1-JE-2025-00042.json"
}
```

### Off-Chain JSON (`ORG1-JE-2025-00042.json`)
```json
{
  "entryNumber": "JE-2025-00042",
  "documentReference": "INV-0099",
  "documentJournalId": "zoho-je-99887766",
  "journalDate": "2025-06-01T00:00:00Z",
  "notes": "Office supplies purchase",
  "currencyCode": "AUD",
  "currencySymbol": "$",
  "exchangeRate": 1.0,
  "journalType": "manual",
  "includeInVatReturn": true,
  "isBasAdjustment": false,
  "lineItemTotal": 100.00,
  "total": 110.00,
  "bcyTotal": 110.00,
  "pricePrecision": 2,
  "originalCreatedTime": "2025-06-01T08:30:00Z",
  "lastModifiedTime": "2025-06-01T08:30:00Z",
  "originalStatus": "published",
  "lineItems": [
    {
      "accountCode": "6-0001",
      "accountName": "Office Expenses",
      "description": "Office supplies",
      "debitOrCredit": "debit",
      "amount": 100.00,
      "tags": []
    },
    {
      "accountCode": "2-0010",
      "accountName": "Accounts Payable",
      "description": "Office supplies",
      "debitOrCredit": "credit",
      "amount": 100.00,
      "tags": []
    }
  ],
  "taxes": [
    {
      "taxName": "GST",
      "taxAmount": 10.00
    }
  ],
  "customFields": []
}
```
