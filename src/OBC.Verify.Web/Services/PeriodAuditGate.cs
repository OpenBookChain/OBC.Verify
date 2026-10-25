using System.Threading.RateLimiting;
using OBC.Verify.Web.Models;
using OBC.Verify.Web.Services.Solana;

namespace OBC.Verify.Web.Services;

/// <summary>
/// Server-wide limits for the period audit, shared by every audit (one instance per application):
/// at most <see cref="PeriodAuditOptions.MaxConcurrentAudits"/> audits at a time, and all their Solana RPC
/// calls through one token bucket of <see cref="PeriodAuditOptions.RpcRequestsPerSecond"/>.
/// Calls beyond the rate wait in the bucket's queue instead of hitting the RPC node.
/// </summary>
public sealed class PeriodAuditGate : IDisposable
{
    private readonly SemaphoreSlim _audits;
    private readonly RateLimiter _rpc;

    public PeriodAuditGate(PeriodAuditOptions options)
    {
        _audits = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentAudits));
        var perSecond = Math.Max(1, options.RpcRequestsPerSecond);
        _rpc = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = perSecond,
            TokensPerPeriod = perSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });
    }

    /// <summary>Takes an audit slot without waiting. Returns null when every slot is in use.</summary>
    public IDisposable? TryEnter() => _audits.Wait(0) ? new Slot(_audits) : null;

    /// <summary>Wraps a reader so each of its calls first waits for an RPC token.</summary>
    public ISolanaReader Limit(ISolanaReader reader) => new RateLimitedSolanaReader(reader, _rpc);

    public void Dispose()
    {
        _audits.Dispose();
        _rpc.Dispose();
    }

    private sealed class Slot(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
        }
    }
}

/// <summary>An <see cref="ISolanaReader"/> that acquires one token from a shared limiter before every RPC call.</summary>
public sealed class RateLimitedSolanaReader(ISolanaReader inner, RateLimiter limiter) : ISolanaReader
{
    public string RpcUrl => inner.RpcUrl;

    public async Task<ChainTransaction?> GetTransactionAsync(string signature, CancellationToken ct = default)
    {
        await WaitAsync(ct);
        return await inner.GetTransactionAsync(signature, ct);
    }

    public async Task<ChainAccount?> GetAccountAsync(string address, CancellationToken ct = default)
    {
        await WaitAsync(ct);
        return await inner.GetAccountAsync(address, ct);
    }

    public async Task<IReadOnlyList<ChainSignature>> GetSignaturesAsync(string address, string? before, int limit, CancellationToken ct = default)
    {
        await WaitAsync(ct);
        return await inner.GetSignaturesAsync(address, before, limit, ct);
    }

    private async Task WaitAsync(CancellationToken ct)
    {
        using var lease = await limiter.AcquireAsync(1, ct);
        if (!lease.IsAcquired) throw new InvalidOperationException("The RPC rate limiter refused the call.");
    }
}
