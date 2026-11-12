using System.Text.Json;
using Flowbit.Service.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Automatic multi-instance limits are enforced twice: by the code, and by check
/// constraints over <c>octet_length(column::text)</c>. Both must measure the same text, or
/// a value can pass the code and then fail the database with an unmapped error.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class JsonbTextTests(PostgresApiFixture fixture)
{
    public static TheoryData<string> Samples => new()
    {
        "[0,0,0]",
        "{}",
        "[]",
        """{"a":1,"b":[true,false,null]}""",
        """{"nested":{"k":[1.25,-3,"x"]},"z":""}""",
        "\"plain\"",
        "\"quote \\\" back \\\\ slash / solidus\"",
        "\"tab\\tnew\\nline\\r\\b\\f\"",
        "\"\\u0001\\u001f\\u007f\"",
        "\"é漢字😀\"",
        "\"\\u00e9\\u6f22\\ud83d\\ude00\"",
        "\"<>&'+\"",
        "1E+300",
        "1e-7",
        "1.500",
        "-0",
        "-0.0",
        "0.000",
        "123.456e2",
        "5E-324",
        "1.7976931348623157E+308",
        "100e-1",
        "0e5",
        "-1.0E-2",
        "0.1e1",
        "12345678901234567890",
        "-42"
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Counts_the_bytes_PostgreSQL_prints_for_a_stored_value(string json)
    {
        await using var db = fixture.CreateDbContext();
        var stored = await db.Database
            .SqlQuery<int>($"SELECT octet_length(CAST({json} AS jsonb)::text) AS \"Value\"")
            .SingleAsync();

        Assert.Equal(stored, JsonbText.ByteCount(JsonDocument.Parse(json).RootElement));
    }

    [Fact]
    public async Task Counts_values_the_engine_serializes_the_way_PostgreSQL_stores_them()
    {
        // System.Text.Json writes 1E+300 and escapes < > & ' + as \u sequences.
        var serialized = JsonSerializer.SerializeToElement(new
        {
            large = 1e300,
            small = 1e-7,
            inexact = 0.1 + 0.2,
            text = "é <tag> & 'quote' +",
            zeros = new int[180_000]
        });
        await using var db = fixture.CreateDbContext();
        var stored = await db.Database
            .SqlQuery<int>($"SELECT octet_length(CAST({serialized.GetRawText()} AS jsonb)::text) AS \"Value\"")
            .SingleAsync();

        Assert.Equal(stored, JsonbText.ByteCount(serialized));
        // Compact JSON is far smaller, which is how a value used to pass the code and
        // then break the database's check.
        Assert.True(serialized.GetRawText().Length < stored);
    }

    [Fact]
    public void A_zero_array_takes_three_bytes_per_element_after_the_first()
    {
        var zeros = JsonSerializer.SerializeToElement(new int[180_000]);

        Assert.Equal(540_000, JsonbText.ByteCount(zeros));
        Assert.Equal(360_001, zeros.GetRawText().Length);
    }
}
