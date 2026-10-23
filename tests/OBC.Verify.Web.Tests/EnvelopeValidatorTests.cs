using OBC.Verify.Web.Services.Standard;

namespace OBC.Verify.Web.Tests;

public class EnvelopeValidatorTests
{
    private static EnvelopeResult Validate(string json) => EnvelopeValidator.Validate(TestData.Bytes(json));

    [Fact]
    public void Valid_journal_envelope()
    {
        var result = Validate(TestData.Envelope());

        Assert.True(result.EnvelopeValid, string.Join("; ", result.EnvelopeErrors));
        Assert.Equal("journal", result.Type);
        Assert.Equal("obc.journal/2", result.SchemaId);
        Assert.True(result.TypeRecognized);
        Assert.True(result.DataValidated);
        Assert.Empty(result.DataErrors);
        Assert.False(result.IsVoided);
    }

    [Fact]
    public void Validates_devnet_style_journal_with_minimal_fields()
    {
        var data = """{"EntryNumber":"X","JournalDate":"2026-04-16T00:00:00Z","LineItems":[],"Taxes":[],"CustomFields":[]}""";
        var result = Validate(TestData.Envelope(data));
        Assert.Empty(result.DataErrors);
    }

    [Theory]
    [InlineData("""{"EntryNumber":"X","LineItems":[],"Taxes":[],"CustomFields":[]}""")] // JournalDate missing
    [InlineData("""{"EntryNumber":"X","JournalDate":"2026-04-16T00:00:00Z","Taxes":[],"CustomFields":[]}""")] // LineItems missing
    [InlineData("""{"EntryNumber":"X","JournalDate":"2026-04-16T00:00:00Z","LineItems":[{"Tags":[]}],"Taxes":[],"CustomFields":[]}""")] // Amount missing
    [InlineData("""{"EntryNumber":"X","JournalDate":"2026-04-16T00:00:00Z","LineItems":[{"Amount":"ten","Tags":[]}],"Taxes":[],"CustomFields":[]}""")]
    [InlineData("""{"EntryNumber":"X","JournalDate":"2026-04-16T00:00:00Z","LineItems":[],"Taxes":[],"CustomFields":[],"Unknown":1}""")]
    [InlineData("""{"EntryNumber":"X","JournalDate":"2026-04-16T00:00:00Z","LineItems":[{"Amount":1,"Tags":[],"DebitOrCredit":"sideways"}],"Taxes":[],"CustomFields":[]}""")]
    public void Invalid_journal_data_is_reported(string data)
    {
        var result = Validate(TestData.Envelope(data));

        Assert.True(result.EnvelopeValid);
        Assert.True(result.DataValidated);
        Assert.NotEmpty(result.DataErrors);
    }

    [Fact]
    public void Journal_without_entry_number_is_valid()
    {
        // OBC omits null fields, so a journal with neither an entry number nor a reference has no EntryNumber.
        var data = """{"DocumentJournalId":"J-1","JournalDate":"2026-04-16T00:00:00Z","Total":1.0,"LineItems":[{"DebitOrCredit":"debit","Amount":1.0,"Tags":[]}],"Taxes":[],"CustomFields":[]}""";
        var result = Validate(TestData.Envelope(data));

        Assert.True(result.DataValidated);
        Assert.Empty(result.DataErrors);
    }

    [Fact]
    public void Tombstone_skips_data_validation()
    {
        var result = Validate("""{"version":3,"type":"journal","schema":"obc.journal/2","status":"voided","reason":"voided in source system","data":{}}""");

        Assert.True(result.EnvelopeValid, string.Join("; ", result.EnvelopeErrors));
        Assert.True(result.IsVoided);
        Assert.Equal("voided in source system", result.Reason);
        Assert.False(result.DataValidated);
        Assert.Empty(result.DataErrors);
    }

    [Fact]
    public void Tombstone_envelope_is_still_validated()
    {
        var result = Validate("""{"version":3,"type":"journal","status":"voided","data":{}}""");
        Assert.False(result.EnvelopeValid);
    }

    [Fact]
    public void Unknown_schema_is_not_recognized_but_valid()
    {
        var result = Validate("""{"version":3,"type":"invoice","schema":"obc.invoice/1","data":{"anything":true}}""");

        Assert.True(result.EnvelopeValid, string.Join("; ", result.EnvelopeErrors));
        Assert.False(result.TypeRecognized);
        Assert.False(result.DataValidated);
    }

    [Theory]
    [InlineData("""{"version":3,"type":"invoice","schema":"obc.journal/2","data":{}}""", "does not match")]
    [InlineData("""{"version":2,"type":"journal","schema":"obc.journal/2","data":{}}""", "version")]
    [InlineData("""{"version":"3","type":"journal","schema":"obc.journal/2","data":{}}""", "version")]
    [InlineData("""{"version":3,"type":"Journal","schema":"obc.Journal/2","data":{}}""", "")]
    [InlineData("""{"version":3,"type":"journal","schema":"journal-2","data":{}}""", "")]
    [InlineData("""{"version":3,"type":"journal","schema":"obc.journal/2"}""", "")]
    [InlineData("""{"version":3,"type":"journal","schema":"obc.journal/2","data":[]}""", "")]
    [InlineData("""{"version":3,"type":"journal","schema":"obc.journal/2","status":"deleted","data":{}}""", "")]
    [InlineData("""{"version":3,"type":"journal","schema":"obc.journal/2","links":[{"rel":"source"}],"data":{}}""", "")]
    [InlineData("""[]""", "")]
    [InlineData("""not json""", "JSON")]
    public void Invalid_envelopes(string json, string expected)
    {
        var result = Validate(json);

        Assert.False(result.EnvelopeValid);
        Assert.Contains(result.EnvelopeErrors, e => e.Contains(expected));
        Assert.False(result.DataValidated);
    }

    [Fact]
    public void Ignores_unknown_top_level_fields_and_reports_links()
    {
        var h = new string('c', 64);
        var result = Validate(TestData.Envelope(extra: $$""","links":[{"rel":"source","type":"invoice","h":"{{h}}"},{"rel":"derived","h":"{{h}}","x":1}],"future":{"a":1},"sourceModifiedAt":"2026-10-01T10:00:00Z" """));

        Assert.True(result.EnvelopeValid, string.Join("; ", result.EnvelopeErrors));
        Assert.Empty(result.DataErrors);
        Assert.Equal(2, result.Links.Count);
        Assert.Equal("source", result.Links[0].Rel);
        Assert.Equal("invoice", result.Links[0].Type);
        Assert.Equal(h, result.Links[1].Hash);
    }

    [Fact]
    public void Registry_knows_journal_2()
    {
        Assert.Contains("obc.journal/2", EnvelopeValidator.KnownSchemaIds);
        Assert.DoesNotContain("obc.invoice/1", EnvelopeValidator.KnownSchemaIds);
    }
}
