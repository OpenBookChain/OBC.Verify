using System.Net;
using System.Text.Json;
using OBC.Verify.Web.Models;
using Solnet.Rpc;
using Solnet.Rpc.Core.Http;
using Solnet.Rpc.Types;

namespace OBC.Verify.Web.Services.Solana;

public sealed class SolnetSolanaReaderFactory : ISolanaReaderFactory
{
    public ISolanaReader Create(SolanaNetwork network) => new SolnetSolanaReader(GetRpcUrl(network));

    public static string GetRpcUrl(SolanaNetwork network) => network switch
    {
        SolanaNetwork.MainnetBeta => "https://api.mainnet-beta.solana.com",
        SolanaNetwork.Devnet => "https://api.devnet.solana.com",
        SolanaNetwork.Testnet => "https://api.testnet.solana.com",
        _ => "https://api.mainnet-beta.solana.com"
    };
}

public sealed class SolnetSolanaReader : ISolanaReader
{
    private readonly IRpcClient _rpc;

    public SolnetSolanaReader(string rpcUrl)
    {
        RpcUrl = rpcUrl;
        _rpc = ClientFactory.GetClient(rpcUrl);
    }

    public string RpcUrl { get; }

    public async Task<ChainTransaction?> GetTransactionAsync(string signature, CancellationToken ct = default)
    {
        var response = await WithRetryAsync(() => _rpc.GetTransactionAsync(signature, Commitment.Finalized), ct);
        if (!response.WasRequestSuccessfullyHandled)
        {
            throw new InvalidOperationException(response.Reason);
        }

        var info = response.Result;
        var message = info?.Transaction?.Message;
        if (info is null || message?.Instructions is null)
        {
            return null;
        }

        var accountKeys = message.AccountKeys ?? [];
        var requiredSignatures = message.Header?.NumRequiredSignatures ?? 0;
        var extracted = MemoExtractor.Extract(
            accountKeys,
            requiredSignatures,
            message.Instructions.Select(ix => new MemoExtractor.Instruction(ix.ProgramIdIndex, ix.Accounts ?? [], ix.Data)));

        var error = info.Meta?.Error is { } err ? JsonSerializer.Serialize(err) : null;

        return new ChainTransaction(
            signature,
            info.Slot,
            info.BlockTime,
            error,
            extracted?.Memo,
            extracted?.Signers ?? [],
            requiredSignatures > 0 && accountKeys.Length > 0 ? accountKeys[0] : null,
            extracted?.ProgramId);
    }

    public async Task<ChainAccount?> GetAccountAsync(string address, CancellationToken ct = default)
    {
        var response = await WithRetryAsync(() => _rpc.GetAccountInfoAsync(address, Commitment.Finalized, BinaryEncoding.Base64), ct);
        if (!response.WasRequestSuccessfullyHandled)
        {
            throw new InvalidOperationException(response.Reason);
        }

        var value = response.Result?.Value;
        if (value is null)
        {
            return null;
        }

        var data = value.Data is { Count: > 0 } ? Convert.FromBase64String(value.Data[0]) : [];
        return new ChainAccount(value.Owner, data);
    }

    public async Task<IReadOnlyList<ChainSignature>> GetSignaturesAsync(string address, string? before, int limit, CancellationToken ct = default)
    {
        var response = await WithRetryAsync(() => _rpc.GetSignaturesForAddressAsync(address, (ulong)limit, before, null, Commitment.Finalized), ct);
        if (!response.WasRequestSuccessfullyHandled)
        {
            throw new InvalidOperationException(response.Reason);
        }

        return (response.Result ?? [])
            .Select(s => new ChainSignature(s.Signature, s.Slot, (long?)s.BlockTime, s.Error is not null, s.Memo))
            .ToList();
    }

    /// <summary>
    /// Public RPC nodes rate-limit per method (the free devnet tier allows 10 calls per method per window).
    /// Back off (1, 2, 4, 8 s) and retry.
    /// </summary>
    private static async Task<RequestResult<T>> WithRetryAsync<T>(Func<Task<RequestResult<T>>> call, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await call();
            if (!IsRateLimited(response) || attempt == 4) return response;
            await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct);
        }
    }

    private static bool IsRateLimited<T>(RequestResult<T> response) =>
        response.HttpStatusCode == HttpStatusCode.TooManyRequests
        || response.Reason?.Contains("Too many requests", StringComparison.OrdinalIgnoreCase) == true;
}
