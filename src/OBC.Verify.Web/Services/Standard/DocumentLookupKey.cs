using System.Security.Cryptography;
using System.Text;
using Solnet.Wallet;

namespace OBC.Verify.Web.Services.Standard;

/// <summary>
/// The per-document lookup key k (section 3.3). Every v3 record published with p-memo also sends
/// 0 lamports from P to k, so <c>getSignaturesForAddress(k)</c> lists the versions of one document.
/// </summary>
public static class DocumentLookupKey
{
    public const string Prefix = "obc-doc";

    /// <summary><c>sha256( utf8("obc-doc") ‖ P (32 bytes) ‖ utf8(d) )</c> as a base58 address.</summary>
    public static string Derive(string publisher, string documentId)
    {
        if (!PublicKey.IsValid(publisher))
        {
            throw new ArgumentException($"'{publisher}' is not a valid public key.", nameof(publisher));
        }

        var prefix = Encoding.UTF8.GetBytes(Prefix);
        var key = new PublicKey(publisher).KeyBytes;
        var d = Encoding.UTF8.GetBytes(documentId);

        var input = new byte[prefix.Length + key.Length + d.Length];
        prefix.CopyTo(input, 0);
        key.CopyTo(input, prefix.Length);
        d.CopyTo(input, prefix.Length + key.Length);

        return new PublicKey(SHA256.HashData(input)).Key;
    }
}
