using OBC.Verify.Web.Services.Solana;
using OBC.Verify.Web.Services.Standard;

namespace OBC.Verify.Web.Tests;

public class MemoExtractorTests
{
    private const string LookupKey = "De6Ro1d5gNSZUdSNkeSY49Df2V3zVdDkBGXJYizrhq72";

    [Fact]
    public void Derives_document_lookup_key_test_vector()
    {
        Assert.Equal(LookupKey, DocumentLookupKey.Derive(TestData.Publisher, "test-70c5043d0da44c2cae82ef02b6f2d5c8"));
    }

    [Fact]
    public void Lookup_key_depends_on_signer_and_document()
    {
        var k = DocumentLookupKey.Derive(TestData.Publisher, "a");
        Assert.NotEqual(k, DocumentLookupKey.Derive(TestData.Publisher, "b"));
        Assert.NotEqual(k, DocumentLookupKey.Derive(TestData.OtherWallet, "a"));
        Assert.Throws<ArgumentException>(() => DocumentLookupKey.Derive("not-a-key", "a"));
    }

    [Fact]
    public void Extracts_p_memo_from_record_with_lookup_key_transfer()
    {
        // Devnet tx GvJ3v7id…: p-memo instruction signed by P, then a 0-lamport System transfer P → k.
        string[] keys = [TestData.Publisher, LookupKey, "11111111111111111111111111111111", MemoExtractor.PMemoProgramId];
        var result = MemoExtractor.Extract(keys, 1,
        [
            new(3, [0], "5EUbqkzyecLJ2ocH4S3pESLS1Hu75UwusZmh6BqCSjbdoAWZG1ywTpiDTmTGkJXRKc966hrbzDD69rk8GP2UHZEVJEJgdeF3MGvbR3FMWTnLQY7LvTDDcSptjCwn3Kqekudcgzu6DRDoxPMqybGA6JzbNwKwpyENyfmusPu4867EjjF5ac"),
            new(2, [0, 1], "3Bxs3zrfFUZbEPqZ")
        ]);

        Assert.NotNull(result);
        Assert.Equal(MemoExtractor.PMemoProgramId, result.ProgramId);
        Assert.Equal("""{"v":"3","h":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","d":"test-70c5043d0da44c2cae82ef02b6f2d5c8","n":2}""", result.Memo);
        Assert.Equal([TestData.Publisher], result.Signers);
    }

    [Fact]
    public void Extracts_memo_when_transfer_comes_first()
    {
        string[] keys = [TestData.Publisher, LookupKey, "11111111111111111111111111111111", MemoExtractor.PMemoProgramId];
        var result = MemoExtractor.Extract(keys, 1,
        [
            new(2, [0, 1], "3Bxs3zrfFUZbEPqZ"),
            new(3, [0], Base58("{\"v\":\"3\"}"))
        ]);

        Assert.Equal("{\"v\":\"3\"}", result!.Memo);
    }

    [Fact]
    public void Extracts_spl_memo_from_existing_devnet_record()
    {
        // Devnet tx 5dJyuxcB…: the first v3 record, published with SPL Memo before the p-memo switch.
        string[] keys = [TestData.Publisher, MemoExtractor.SplMemoProgramId];
        var result = MemoExtractor.Extract(keys, 1,
        [
            new(1, [0], "3XSNY4UAbajPD4nykdBkSMjkLjsL2W2iYoR4iLxSZfD696zTUaNsKuY5goFFrCxE9PQNn4EBn6vqMyH169hrpLyvn5HnvjkFCEhuyDcN2XWzpibch7oM4cFEyj8goELHnhAi4gTQ2SgSDxexopErQ1JrkUjNXgc3TGc4SMtMWb1XPoFTsEWnLibT5rVhLtLmJQSERjZh985D3PCHc4gpjvk")
        ]);

        Assert.Equal(MemoExtractor.SplMemoProgramId, result!.ProgramId);
        Assert.Equal("""{"v":"3","h":"c166b5ed15c756881ce9433885116746eb3ee707096ca1f9c6df2601f4fa2767","d":"1ee6871ee99332e2ffbd5b1ad429d4e450f207c2be600e019c69d3ec4efe7302","n":1}""", result.Memo);
        Assert.Equal([TestData.Publisher], result.Signers);
    }

    [Fact]
    public void Still_accepts_legacy_memo_program()
    {
        string[] keys = [TestData.Publisher, MemoExtractor.MemoV1ProgramId];
        var result = MemoExtractor.Extract(keys, 1, [new(1, [], Base58("{\"v\":\"1\"}"))]);

        Assert.Equal("{\"v\":\"1\"}", result!.Memo);
        Assert.Empty(result.Signers);
    }

    [Fact]
    public void Ignores_other_programs_and_non_signer_accounts()
    {
        string[] keys = [TestData.Publisher, TestData.OtherWallet, "11111111111111111111111111111111", MemoExtractor.PMemoProgramId];

        Assert.Null(MemoExtractor.Extract(keys, 1, [new(2, [0, 1], "3Bxs3zrfFUZbEPqZ")]));

        var result = MemoExtractor.Extract(keys, 1, [new(3, [0, 1], Base58("x"))]);
        Assert.Equal([TestData.Publisher], result!.Signers);
    }

    private static string Base58(string text) =>
        new Solnet.Wallet.Utilities.Base58Encoder().EncodeData(System.Text.Encoding.UTF8.GetBytes(text));
}
