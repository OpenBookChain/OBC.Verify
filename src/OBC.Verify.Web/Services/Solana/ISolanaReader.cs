using OBC.Verify.Web.Models;

namespace OBC.Verify.Web.Services.Solana;

/// <summary>
/// The few read-only Solana RPC calls the verifier needs, always at finalized commitment.
/// Kept small so verification logic can be tested without a live RPC node.
/// </summary>
public interface ISolanaReader
{
    string RpcUrl { get; }

    /// <summary>Returns null when the transaction is not found at finalized commitment.</summary>
    Task<ChainTransaction?> GetTransactionAsync(string signature, CancellationToken ct = default);

    /// <summary>Returns null when no account exists at the address.</summary>
    Task<ChainAccount?> GetAccountAsync(string address, CancellationToken ct = default);

    /// <summary>One page of signatures for an address, newest first, older than <paramref name="before"/>.</summary>
    Task<IReadOnlyList<ChainSignature>> GetSignaturesAsync(string address, string? before, int limit, CancellationToken ct = default);
}

public interface ISolanaReaderFactory
{
    ISolanaReader Create(SolanaNetwork network);
}

/// <param name="Error">Serialized <c>meta.err</c>, null when the transaction succeeded.</param>
/// <param name="Memo">UTF-8 data of the first memo instruction, null when there is none.</param>
/// <param name="MemoSigners">Accounts of the memo instruction that signed the transaction.</param>
/// <param name="FeePayer">First account key, always a signer.</param>
/// <param name="MemoProgramId">The program of the memo instruction, null when there is none.</param>
public sealed record ChainTransaction(
    string Signature,
    ulong Slot,
    long? BlockTime,
    string? Error,
    string? Memo,
    IReadOnlyList<string> MemoSigners,
    string? FeePayer,
    string? MemoProgramId = null);

public sealed record ChainAccount(string Owner, byte[] Data);

/// <param name="Memo">The RPC's memo summary ("[len] text"). Not every provider fills it (e.g. for p-memo), so it is only a hint.</param>
public sealed record ChainSignature(string Signature, ulong Slot, long? BlockTime, bool Failed, string? Memo);
