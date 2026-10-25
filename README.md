# OBC.Verify — Open Book Chain Verification

**OBC.Verify** is the open-source verification portal and standards repository for [Open Book Chain (OBC)](https://openbookchain.com/).

It allows anyone to independently verify that a certified financial journal record published by OBC has not been tampered with, without needing access to the OBC platform itself. An auditor with access to the off-chain files can also check that a publisher's closed accounting periods are complete.

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
| [v2](standards/v2/README.md) | `obc-v2` | ✅ Active | Adds multi-dimensional accounting fields (`accountCode1..10`, `accountCodeName1..10`) |
| [v3](standards/v3/README.md) | `obc-v3` | ✅ Active | Hash-named files, storage bases in an on-chain location account, type-aware envelope, on-chain version history (`d`/`n`/`p`, lookup key `k`), p-memo |

See [`standards/versions.json`](standards/versions.json) for the machine-readable registry.

---

## How to Verify a Record (Manual)

Given a **Solana transaction ID**:

1. Look up the transaction on [Solana Explorer](https://explorer.solana.com/) or via the RPC API.
2. Find the memo instruction and decode the UTF-8 data as JSON.
3. Check `v` field to identify the standard version (`"1"`, `"2"` or `"3"`).
4. Follow the verification steps in the corresponding standard:
   - [v1 Verification Algorithm](standards/v1/README.md#4-verification-algorithm)
   - [v2 Verification Protocol](standards/v2/README.md#4-verification-protocol)
   - [v3 Verification Protocol](standards/v3/README.md#6-verification-protocol)

---

## Linking to a Verification

The verifier at [verify.openbookchain.com](https://verify.openbookchain.com/) accepts a deep link that pre-fills the form and runs the verification once on load:

```
https://verify.openbookchain.com/?tx=<transaction signature>&network=<network>
```

| Parameter | Required | Values |
|-----------|----------|--------|
| `tx`      | yes | The Solana transaction signature (base58, 64 bytes) |
| `network` | no  | `mainnet-beta` (default; `mainnet` is accepted too), `devnet`, `testnet`. Case-insensitive |

Example: [`/?tx=2dkr9oBK…BP95&network=devnet`](https://verify.openbookchain.com/?tx=2dkr9oBKGD4UkzvkFc8626VTfbVcn8dBUuYFQhrsRtYWhUVzMEfZAAmte9F62vropCVvybCEEn8FWLUUwpkpBP95&network=devnet)

- An unknown `network` is ignored and Mainnet Beta is used.
- A malformed `tx` is pre-filled but not verified; the form shows why.
- After every verification the address bar is updated to this format, and the result has a **Copy link to this result** button, so any result can be shared. The expected publisher wallet is never put in the URL.

---

## Accounting Period Audit

Publishers close accounting periods with ordinary v3 records of type [`acc-period-close`](standards/v3/types/acc-period-close/1/README.md). Verifying one such record on the main page checks it like any record and recomputes its member count and Merkle root.

The **Period audit** page (`/audit`) runs the rebuild and completeness check (section 5 of the type's specification). It is **off by default**: it reads every record of a publisher from Solana and downloads every file, which is too heavy for a shared public server. Auditors run the verifier themselves and turn it on:

```
dotnet run --project src/OBC.Verify.Web --Verification:Audit:Enabled=true
```

It needs:

| Input | Required | Description |
|-------|----------|-------------|
| Publisher wallet | yes | The organisation's wallet P |
| Network | yes | Mainnet Beta, Devnet or Testnet |
| Latest close transaction | no | Where to start following `previous-period` links. Without it, the period that ends last is used |
| Storage bases | no | Extra https bases with the off-chain files, tried after the location account's |
| Label map | no | `label:name = https://…` lines that resolve the location account's opaque `label:` bases |

It reads every record P signed and downloads every file, then reports each period as **complete**, **failed** (late entry, omitted, changed after close, broken member, invalid period record) or **open**, plus gaps, overlaps and broken links between periods. Files are only fetched over public https, with the same SSRF checks and the 50 MB cap as any verification.

The audit is bounded by configuration:

| Setting | Default | Description |
|---------|---------|-------------|
| `Verification:Audit:Enabled` | `false` | Shows the page and allows audits on this server |
| `Verification:Audit:MaxConcurrentAudits` | `1` | Audits running at the same time; another request is refused while all are busy |
| `Verification:Audit:RpcRequestsPerSecond` | `5` | Solana RPC calls per second, shared by every audit on the server; further calls wait |
| `Verification:Audit:MaxPages` | `50` | Pages of 1000 signatures read from P's history before the report says "history incomplete" |
| `Verification:Audit:Concurrency` | `4` | Transactions and files read in parallel within one audit |

With your own RPC endpoint you can raise `RpcRequestsPerSecond`; public endpoints allow only a few calls per second.

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
│   ├── v2/
│   └── v3/
│       ├── README.md           ← Full v3 standard specification
│       ├── onchain-schema.json ← JSON Schema for the on-chain memo
│       ├── location-schema.json← JSON Schema for the location account content
│       ├── envelope-schema.json← JSON Schema for the off-chain envelope
│       ├── types.json          ← Registry of document schema ids
│       └── types/
│           ├── journal/2/      ← obc.journal/2 data schema
│           └── acc-period-close/1/ ← obc.acc-period-close/1 schema and specification
├── src/
│   └── OBC.Verify.Web/         ← Blazor verification dashboard
└── tests/
    └── OBC.Verify.Web.Tests/   ← Verifier unit tests
```

---

## Contributing

Standards are **append-only**. To propose a new version, open an issue describing the changes and the motivation. Existing standard documents must not be modified once published.

---

## License

MIT
