using System.Text;
using Solnet.Wallet.Utilities;

namespace OBC.Verify.Web.Services.Solana;

/// <summary>
/// Reads the record memo from a transaction's instruction data (section 6, step 2), never from the RPC's
/// memo summary, which some providers only fill for program ids they recognize.
/// </summary>
public static class MemoExtractor
{
    /// <summary>Legacy memo program, accepted as before for v1/v2.</summary>
    public const string MemoV1ProgramId = "Memo1UhkJRfHyvLMcVucJwxXeuD728EqVDDwQDxFMNo";
    /// <summary>SPL Memo: v1/v2 records and v3 records published before the p-memo switch.</summary>
    public const string SplMemoProgramId = "MemoSq4gqABAXKb96qnH8TysNcWxMyWCqXgDLGmfcHr";
    /// <summary>p-memo, the Pinocchio rewrite of SPL Memo with the same instruction format: v3 records.</summary>
    public const string PMemoProgramId = "Memo4c2pN8afCj432Lb7RMVKi9PbQnnW7ewFFaV3oAH";

    public static readonly IReadOnlySet<string> MemoProgramIds =
        new HashSet<string>(StringComparer.Ordinal) { MemoV1ProgramId, SplMemoProgramId, PMemoProgramId };

    /// <param name="Data">Base58 instruction data, as returned by getTransaction with json encoding.</param>
    public sealed record Instruction(int ProgramIdIndex, int[] Accounts, string? Data);

    /// <param name="Signers">Accounts of the memo instruction that signed the transaction.</param>
    public sealed record Result(string Memo, string ProgramId, IReadOnlyList<string> Signers);

    /// <summary>The first memo instruction of any accepted memo program, or null when there is none.</summary>
    public static Result? Extract(string[] accountKeys, int requiredSignatures, IEnumerable<Instruction> instructions)
    {
        foreach (var ix in instructions)
        {
            var programId = ix.ProgramIdIndex >= 0 && ix.ProgramIdIndex < accountKeys.Length ? accountKeys[ix.ProgramIdIndex] : null;
            if (programId is null || !MemoProgramIds.Contains(programId) || string.IsNullOrEmpty(ix.Data))
            {
                continue;
            }

            var memo = Encoding.UTF8.GetString(new Base58Encoder().DecodeData(ix.Data));
            var signers = new List<string>();
            foreach (var index in ix.Accounts)
            {
                if (index >= 0 && index < requiredSignatures && index < accountKeys.Length && !signers.Contains(accountKeys[index]))
                {
                    signers.Add(accountKeys[index]);
                }
            }
            return new Result(memo, programId, signers);
        }
        return null;
    }
}
