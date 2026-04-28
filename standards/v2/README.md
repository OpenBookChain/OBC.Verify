# OBC Verifiable Journal Standard v2

**Status:** Active  
**Introduced:** 2026  
**Schema:** [`offchain-schema.json`](offchain-schema.json) | [`onchain-schema.json`](onchain-schema.json)

## 1. Overview

The **On-chain Books Chain (OBC)** standard v2 introduces a major upgrade to support **multi-dimensional accounting**. While version 1 only supported a single `accountCode` and `accountName` per line item, version 2 supports up to 10 distinct account code dimensions per line item (e.g., Department, Cost Center, Project, Fund, Location).

### Key Changes in v2

*   **Multi-dimensional mapping:** Added `accountCode1` through `accountCode10` and `accountCodeName1` through `accountCodeName10` to `lineItems`.
*   **Deprecation:** The legacy `accountCode` and `accountName` fields are no longer required in v2, strongly encouraging the use of the new dimensional fields for all account segmentation.
*   **Version marker:** The on-chain `v` parameter is now `"2"`.
*   **Off-chain version:** The off-chain JSON now requires `"version": 2`.

This upgrade enables complex organizational reporting directly from published on-chain records, crucial for fund accounting (non-profits), government reporting, and large enterprises.

---

## 2. On-Chain Footprint

A compliant v2 transaction currently targets **Solana** via a memo program instruction (`MemoSq4gqABAXKb96qnH8TysNcWxMyWCqXgDLGmfcHr`).

The memo content must be a valid JSON object matching the [`onchain-schema.json`](onchain-schema.json).

### Required Fields

| Field | Type | Description |
|-------|------|-------------|
| `v` | string | Standard version. Must be `"2"`. |
| `h` | string | 64-character lowercase SHA-256 hash of the canonical off-chain JSON document. |
| `a` | string | HTTPS URL pointing to the primary off-chain JSON file. |

### Optional Fields

| Field | Type | Description |
|-------|------|-------------|
| `a1`, `a2` | string | Fallback/replica URLs pointing to the same document in other storage targets. |
| *(tenant)* | string | Tenants may configure extra fields to be pushed on-chain (e.g., `amount`, `currency`). |

Example on-chain memo:
```json
{
  "v": "2",
  "h": "a3f1c2e4b5d67890abcdef1234567890abcdef1234567890abcdef1234567890",
  "a": "https://myaccount.blob.core.windows.net/journals/ORG1-JE-2026-00042.json"
}
```

---

## 3. Off-Chain Document

The file referenced by the `a` (or `a1`, `a2`) URL must return a JSON payload matching the v2 [`offchain-schema.json`](offchain-schema.json) with an HTTP `200 OK` and a descriptive `Content-Type` (typically `application/json`).

### Requirements

*   **Encoding:** UTF-8 without BOM.
*   **Immutability:** The file content must exactly match the `h` hash. If a file is altered, verification fails.
*   **Formatting:** When hashing the literal file bytes fetched from the URL, the resulting SHA-256 digest must match the `h` field. Do not format, trim, or canonicalize the JSON after downloading before hashing.

---

## 4. Verification Protocol

To cryptographically verify a v2 record, third-party consumers must follow these steps:

1.  **Extract the memo** from the blockchain transaction (e.g., Solana instruction data).
2.  **Parse** the memo as JSON.
3.  **Assert** `v` is `"2"`.
4.  **Fetch** the file from URL `a` (or fallbacks if `a` is inaccessible).
5.  **Compute** the SHA-256 hash of the raw HTTP response body bytes.
6.  **Format** the hash as a lowercase hexadecimal string.
7.  **Assert** the computed hash strictly equals the `h` property from the memo.
8.  **Parse** the fetched body as JSON and assert it satisfies the v2 JSON schema.

If all steps succeed, the record is cryptographically verified to exactly match what the publisher originally recorded on-chain.

---

## 5. Multi-dimensional Accounting & Anonymization

V2 introduces `accountCode1`..`10` and `accountCodeName1`..`10`.

### Anonymization Rules

Anonymization is evaluated **before** hashing. The `h` value covers the already-anonymized document.

In v2, the following Line Item fields are subject to per-tenant anonymization rules:

*   `customerName`
*   `taxName`
*   `taxAuthorityName`
*   `acquisitionVatName`
*   `reverseChargeVatName`
*   `projectName`
*   `accountCode1` through `accountCode10`
*   `accountCodeName1` through `accountCodeName10`
*   (Legacy `accountName` and `accountCode` if present)

When a field is anonymized, its value is replaced by a deterministic 8-character alphanumeric token (seeded from the original value).

---

## 6. Example v2 Record

### Off-Chain JSON (`ORG1-JE-2026-00042.json`)
```json
{
  "version": 2,
  "entryNumber": "JE-2026-00042",
  "documentReference": "GRANT-Q1-505",
  "documentJournalId": "zoho-je-99887766",
  "journalDate": "2026-06-01T00:00:00Z",
  "notes": "Grant disbursement",
  "currencyCode": "USD",
  "currencySymbol": "$",
  "exchangeRate": 1.0,
  "journalType": "manual",
  "includeInVatReturn": false,
  "isBasAdjustment": false,
  "lineItemTotal": 5000.00,
  "total": 5000.00,
  "bcyTotal": 5000.00,
  "pricePrecision": 2,
  "lineItems": [
    {
      "description": "Program materials",
      "debitOrCredit": "debit",
      "amount": 5000.00,
      "accountCode1": "5000",
      "accountCodeName1": "Operating Expenses",
      "accountCode2": "DEPT-A",
      "accountCodeName2": "Community Outreach",
      "accountCode3": "GRANT-26",
      "accountCodeName3": "City Hall Grant Y26",
      "tags": []
    },
    {
      "description": "Program materials",
      "debitOrCredit": "credit",
      "amount": 5000.00,
      "accountCode1": "1000",
      "accountCodeName1": "Main Bank Account",
      "tags": []
    }
  ],
  "taxes": [],
  "customFields": []
}
```
