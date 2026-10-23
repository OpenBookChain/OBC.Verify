using System.Net;
using System.Security.Cryptography;
using System.Text;
using OBC.Verify.Web.Models;
using OBC.Verify.Web.Services.Fetching;
using OBC.Verify.Web.Services.Solana;
using OBC.Verify.Web.Services.Standard;

namespace OBC.Verify.Web.Tests;

/// <summary>An in-memory chain: transactions, accounts and per-address signature history (newest first).</summary>
internal sealed class FakeSolanaReader : ISolanaReader, ISolanaReaderFactory
{
    private ulong _slot = 1000;

    public Dictionary<string, ChainTransaction> Transactions { get; } = new();
    public Dictionary<string, ChainAccount> Accounts { get; } = new();
    public Dictionary<string, List<ChainSignature>> History { get; } = new();
    public int TransactionReads { get; private set; }

    public string RpcUrl => "fake://rpc";

    public ISolanaReader Create(SolanaNetwork network) => this;

    /// <summary>Adds a memo transaction signed by <paramref name="signer"/> and puts it on top of the signer's history.</summary>
    /// <param name="lookupKey">Also lists the transaction under this address, like the 0-lamport transfer to k.</param>
    /// <param name="withSummary">False for an RPC provider that doesn't fill the memo field of getSignaturesForAddress.</param>
    /// <param name="memoSigners">Signers listed on the memo instruction, defaults to the signer only.</param>
    public string AddMemo(string signer, string memo, string? error = null, long? blockTime = null, string? lookupKey = null, bool withSummary = true,
        IReadOnlyList<string>? memoSigners = null)
    {
        var signature = $"sig{Transactions.Count + 1}";
        var slot = ++_slot;
        var time = blockTime ?? 1_790_000_000 + (long)slot;
        var program = lookupKey is null ? MemoExtractor.SplMemoProgramId : MemoExtractor.PMemoProgramId;
        Transactions[signature] = new ChainTransaction(signature, slot, time, error, memo, memoSigners ?? [signer], signer, program);
        var entry = new ChainSignature(signature, slot, time, error is not null, withSummary ? $"[{Encoding.UTF8.GetByteCount(memo)}] {memo}" : null);
        AddHistory(signer, entry);
        if (lookupKey is not null) AddHistory(lookupKey, entry);
        return signature;
    }

    public void AddHistory(string address, ChainSignature entry)
    {
        if (!History.TryGetValue(address, out var list)) History[address] = list = new();
        list.Insert(0, entry);
    }

    public void SetLocation(string publisher, string json, string? owner = null, byte version = 1) =>
        Accounts[LocationAccount.DeriveAddress(publisher)] = new ChainAccount(owner ?? LocationAccount.RecordProgramId, LocationData(json, version));

    public static byte[] LocationData(string json, byte version = 1, int size = 1024)
    {
        var data = new byte[Math.Max(size, 33 + Encoding.UTF8.GetByteCount(json))];
        data[0] = version;
        Encoding.UTF8.GetBytes(json).CopyTo(data, 33);
        return data;
    }

    public Task<ChainTransaction?> GetTransactionAsync(string signature, CancellationToken ct = default)
    {
        TransactionReads++;
        return Task.FromResult(Transactions.GetValueOrDefault(signature));
    }

    public Task<ChainAccount?> GetAccountAsync(string address, CancellationToken ct = default) =>
        Task.FromResult(Accounts.GetValueOrDefault(address));

    public Task<IReadOnlyList<ChainSignature>> GetSignaturesAsync(string address, string? before, int limit, CancellationToken ct = default)
    {
        var list = History.GetValueOrDefault(address) ?? [];
        var start = before is null ? 0 : list.FindIndex(s => s.Signature == before) + 1;
        return Task.FromResult<IReadOnlyList<ChainSignature>>(list.Skip(start).Take(limit).ToList());
    }
}

internal sealed class FakeFetcher : IDocumentFetcher
{
    public Dictionary<string, byte[]> Files { get; } = new();
    public List<string> Requests { get; } = new();

    public Task<FetchResult> FetchAsync(string url, CancellationToken ct = default)
    {
        Requests.Add(url);
        return Task.FromResult(Files.TryGetValue(url, out var content)
            ? FetchResult.Ok(content, url)
            : FetchResult.Fail($"HTTP 404 Not Found ({url})."));
    }
}

/// <summary>An HttpMessageHandler answering from a function, for testing the fetcher without a network.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    public static HttpResponseMessage Ok(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
}

internal static class TestData
{
    public const string Publisher = "DPVzN6gC9uQVmD1JEXmnEfG3rStwFGpotVhAoSVAtLM5";
    public const string PublisherLocation = "D2E8CtgzCUzBxL5qbVMTFWV7wtUE9ZUcXfdJeSjn3oTR";
    public const string OtherWallet = "9xQeWvG816bUx9EPjHmaT23yvVM2ZWbrrpZb9PusVFin";
    public const string Base = "https://mvp1.blob.core.windows.net/publicdocs";
    public const string DocumentId = "1ee6871ee99332e2ffbd5b1ad429d4e450f207c2be600e019c69d3ec4efe7302";

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    public const string JournalData =
        """{"EntryNumber":"JE-1","DocumentJournalId":"JE-1","JournalDate":"2026-04-16T00:00:00Z","CurrencyCode":"GBP","ExchangeRate":0.000000,"IncludeInVatReturn":false,"IsBasAdjustment":false,"LineItemTotal":0.00,"Total":10.50,"BcyTotal":0.00,"PricePrecision":0,"OriginalCreatedTime":"0001-01-01T00:00:00","LastModifiedTime":"0001-01-01T00:00:00","ApprovedUserName":"someone@example.com","ApprovedDate":"2026-10-21T22:22:47.978027Z","LineItems":[{"Description":"Grant","DebitOrCredit":"debit","Amount":10.50,"CustomerName":"Customer","AccountCode1":"2100","AccountCodeName1":"Deferred Grants","Tags":[]},{"Description":"Grant","DebitOrCredit":"credit","Amount":10.50,"AccountCode1":"4100","Tags":[{"TagName":"Fund","TagValue":"A"}]}],"Taxes":[],"CustomFields":[]}""";

    public static string Envelope(string data = JournalData, string extra = "") =>
        $$"""{"version":3,"type":"journal","schema":"obc.journal/2"{{extra}},"data":{{data}}}""";

    public static string MemoV3(string h, long n, string? p = null, string d = DocumentId, string? a = null) =>
        $$"""{"v":"3","h":"{{h}}","d":"{{d}}","n":{{n}}{{(p is null ? "" : $",\"p\":\"{p}\"")}}{{(a is null ? "" : $",\"a\":\"{a}\"")}}}""";
}
