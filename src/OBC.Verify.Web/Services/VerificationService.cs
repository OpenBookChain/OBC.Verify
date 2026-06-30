using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OBC.Verify.Web.Models;
using Solnet.Rpc;

namespace OBC.Verify.Web.Services;

public class VerificationService
{
    private static readonly HashSet<string> SupportedStandardVersions = new(StringComparer.Ordinal)
    {
        "1",
        "2"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<VerificationService> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public VerificationService(IHttpClientFactory httpClientFactory, ILogger<VerificationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<VerificationResult> VerifyAsync(string transactionId, SolanaNetwork network, CancellationToken ct = default)
    {
        var result = new VerificationResult
        {
            TransactionId = transactionId,
            Network = network.ToString()
        };

        // Step 1: Fetch transaction from Solana
        var rpcUrl = GetRpcUrl(network);
        var rpcClient = ClientFactory.GetClient(rpcUrl);

        result.Steps.Add(new VerificationStep { Name = "Connect to Solana RPC", Passed = true, Detail = rpcUrl });

        string? memoData;
        try
        {
            var txResponse = await rpcClient.GetTransactionAsync(transactionId);
            if (txResponse?.Result?.Transaction?.Message?.Instructions is null)
            {
                return Fail(result, "Step 2 – Fetch Transaction", "Transaction not found or has no instructions.");
            }

            result.Steps.Add(new VerificationStep { Name = "Fetch Transaction", Passed = true, Detail = $"Slot: {txResponse.Result.Slot}" });

            // Step 2: Extract memo instruction
            memoData = ExtractMemoData(txResponse.Result);
            if (string.IsNullOrEmpty(memoData))
            {
                return Fail(result, "Extract Memo", "No memo instruction found in transaction.");
            }

            result.Steps.Add(new VerificationStep { Name = "Extract Memo Instruction", Passed = true, Detail = memoData });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch Solana transaction {TxId}", transactionId);
            return Fail(result, "Fetch Transaction", $"RPC error: {ex.Message}");
        }

        // Step 3: Parse memo JSON
        OnChainMemo memo;
        try
        {
            memo = JsonSerializer.Deserialize<OnChainMemo>(memoData, _jsonOptions)
                   ?? throw new InvalidOperationException("Memo deserialized to null.");
        }
        catch (Exception ex)
        {
            return Fail(result, "Parse Memo JSON", $"Invalid JSON in memo: {ex.Message}");
        }

        result.OnChainMemo = memo;
        result.StandardVersion = memo.Version;
        result.OnChainHash = memo.Hash;
        result.FileUrl = memo.FileUrl;

        // Step 4: Assert version
        if (!SupportedStandardVersions.Contains(memo.Version))
        {
            return Fail(
                result,
                "Check Standard Version",
                $"Unsupported standard version '{memo.Version}'. Only '1' and '2' are supported by this verifier.");
        }
        result.Steps.Add(new VerificationStep
        {
            Name = "Check Standard Version",
            Passed = true,
            Detail = $"v{memo.Version} (obc-v{memo.Version})"
        });

        // Step 5: Validate hash format
        if (string.IsNullOrEmpty(memo.Hash) || memo.Hash.Length != 64 || !IsHex(memo.Hash))
        {
            return Fail(result, "Validate Hash Format", "Hash is missing or not a valid 64-char lowercase hex string.");
        }
        result.Steps.Add(new VerificationStep { Name = "Validate Hash Format", Passed = true, Detail = memo.Hash });

        // Step 6: Download off-chain document
        byte[] documentBytes;
        try
        {
            var httpClient = _httpClientFactory.CreateClient("verify");
            documentBytes = await httpClient.GetByteArrayAsync(memo.FileUrl, ct);
            result.OffChainDocumentJson = Encoding.UTF8.GetString(documentBytes);
            result.Steps.Add(new VerificationStep { Name = "Download Off-Chain Document", Passed = true, Detail = $"{documentBytes.Length:N0} bytes from {memo.FileUrl}" });
        }
        catch (Exception ex)
        {
            return Fail(result, "Download Off-Chain Document", $"Failed to download file: {ex.Message}");
        }

        // Step 7: Compute SHA-256 hash
        var computedHash = ComputeSha256Hex(documentBytes);
        result.ComputedHash = computedHash;
        result.Steps.Add(new VerificationStep { Name = "Compute SHA-256 Hash", Passed = true, Detail = computedHash });

        // Step 8: Compare hashes
        var hashMatch = string.Equals(computedHash, memo.Hash, StringComparison.OrdinalIgnoreCase);
        result.Steps.Add(new VerificationStep
        {
            Name = "Compare Hashes",
            Passed = hashMatch,
            Detail = hashMatch
                ? "On-chain hash matches computed hash ✅"
                : $"MISMATCH — on-chain: {memo.Hash} / computed: {computedHash}"
        });

        if (!hashMatch)
        {
            result.IsVerified = false;
            result.ErrorMessage = "Hash mismatch — the off-chain document does not match the on-chain fingerprint.";
            return result;
        }

        result.IsVerified = true;
        return result;
    }

    private static string GetRpcUrl(SolanaNetwork network) => network switch
    {
        SolanaNetwork.MainnetBeta => "https://api.mainnet-beta.solana.com",
        SolanaNetwork.Devnet => "https://api.devnet.solana.com",
        SolanaNetwork.Testnet => "https://api.testnet.solana.com",
        _ => "https://api.mainnet-beta.solana.com"
    };

    private static string? ExtractMemoData(Solnet.Rpc.Models.TransactionMetaSlotInfo txInfo)
    {
        // Memo program IDs
        const string memoV1 = "Memo1UhkJRfHyvLMcVucJwxXeuD728EqVDDwQDxFMNo";
        const string memoV2 = "MemoSq4gqABAXKb96qnH8TysNcWxMyWCqXgDLGmfcHr";

        var message = txInfo.Transaction?.Message;
        if (message?.Instructions is null) return null;

        var accountKeys = message.AccountKeys;

        foreach (var ix in message.Instructions)
        {
            var programId = (accountKeys != null && ix.ProgramIdIndex < accountKeys.Length)
                ? accountKeys[ix.ProgramIdIndex]
                : null;

            if (programId == memoV1 || programId == memoV2)
            {
                if (!string.IsNullOrEmpty(ix.Data))
                {
                    //var bytes = Convert.FromBase64String(ix.Data);
                    //return Encoding.UTF8.GetString(bytes);

                    var bytes = new Solnet.Wallet.Utilities.Base58Encoder().DecodeData(ix.Data);
                    return Encoding.UTF8.GetString(bytes);
                }
            }
        }
        return null;
    }

    private static string ComputeSha256Hex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexStringLower(hash);
    }

    private static bool IsHex(string s) => s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

    private static VerificationResult Fail(VerificationResult result, string stepName, string message)
    {
        result.Steps.Add(new VerificationStep { Name = stepName, Passed = false, Detail = message });
        result.IsVerified = false;
        result.ErrorMessage = message;
        return result;
    }
}
