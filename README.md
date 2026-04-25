# OBC.Verify — Open Book Chain Verification

**OBC.Verify** is the open-source verification portal and standards repository for [Open Book Chain (OBC)](https://openbookchain.com/).

It allows anyone to independently verify that a certified financial journal record published by OBC has not been tampered with, without needing access to the OBC platform itself.

---

## What is OBC?

OBC is a multi-tenant financial document certification platform. It takes journal entries from accounting systems (e.g. Zoho Books), processes and optionally anonymizes them, then:

1. **Stores the full document** as a JSON file in Azure Blob Storage (off-chain).
2. **Records a fingerprint** (SHA-256 hash + file URL) in a Solana blockchain transaction (on-chain).

The combination of the public file and the immutable on-chain hash lets any third party independently verify authenticity.

---

## Standards

Each published version of the OBC format is documented as a **standard**. Standards never change once published — new capabilities are introduced as new versions.

| Version | Standard ID | Status | Summary |
|---------|-------------|--------|---------|
| [v1](standards/v1/README.md) | `obc-v1` | ✅ Active | Solana memo (v, h, a) + Azure Blob JSON |

See [`standards/versions.json`](standards/versions.json) for the machine-readable registry.

---

## How to Verify a Record (Manual)

Given a **Solana transaction ID**:

1. Look up the transaction on [Solana Explorer](https://explorer.solana.com/) or via the RPC API.
2. Find the memo instruction and decode the UTF-8 data as JSON.
3. Check `v` field to identify the standard version (e.g. `"1"`).
4. Follow the verification steps in the corresponding standard:
   - [v1 Verification Algorithm](standards/v1/README.md#4-verification-algorithm)

---

## Repository Structure

```
OBC.Verify/
├── standards/
│   ├── versions.json           ← Machine-readable version registry
│   ├── v1/
│   │   ├── README.md           ← Full v1 standard specification
│   │   ├── onchain-schema.json ← JSON Schema for on-chain memo
│   │   └── offchain-schema.json← JSON Schema for off-chain document
│   └── v2/                     ← (future)
└── src/
    └── OBC.Verify.Web/         ← Blazor verification dashboard (coming soon)
```

---

## Contributing

Standards are **append-only**. To propose a new version, open an issue describing the changes and the motivation. Existing standard documents must not be modified once published.

---

## License

MIT
