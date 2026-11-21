using Flowbit.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Which database errors the API answers with <c>400</c> for a null character: only
/// PostgreSQL's refusal of the byte <c>0x00</c> (22021) or of a <c>\u0000</c> jsonb escape
/// (22P05), found wherever the exception that reaches the API wraps it.
/// </summary>
public sealed class PostgresNullCharacterTests
{
    [Theory]
    [InlineData("22021", "invalid byte sequence for encoding \"UTF8\": 0x00", "22021")]
    // The server may write its messages in another language; the byte stays in hex.
    [InlineData("22021", "ungueltige Byte-Sequenz fuer Kodierung UTF8: 0x00", "22021")]
    [InlineData("22P05", "unsupported Unicode escape sequence", "22P05")]
    // Other invalid bytes, which the driver never sends, and other errors are not the client's text.
    [InlineData("22021", "invalid byte sequence for encoding \"UTF8\": 0xc3 0x28", null)]
    [InlineData("23505", "duplicate key value violates unique constraint", null)]
    [InlineData("22001", "value too long for type character varying(300)", null)]
    [InlineData("40P01", "deadlock detected", null)]
    public void FindSqlState_MatchesOnlyARefusedNullCharacter(string sqlState, string message, string? expected)
    {
        Assert.Equal(expected, PostgresNullCharacter.FindSqlState(Postgres(sqlState, message)));
    }

    [Fact]
    public void FindSqlState_FindsTheErrorInsideTheExceptionsThatWrapIt()
    {
        var refused = Postgres("22P05", "unsupported Unicode escape sequence");

        Assert.Equal("22P05", PostgresNullCharacter.FindSqlState(
            new DbUpdateException("An error occurred while saving the entity changes.", refused)));
        Assert.Equal("22P05", PostgresNullCharacter.FindSqlState(new AggregateException(
            new InvalidOperationException("first"),
            new InvalidOperationException("second", refused))));
        Assert.Null(PostgresNullCharacter.FindSqlState(new InvalidOperationException("No database error.")));
        Assert.Null(PostgresNullCharacter.FindSqlState(new DbUpdateException(
            "An error occurred while saving the entity changes.",
            Postgres("23505", "duplicate key value violates unique constraint"))));
    }

    [Fact]
    public void FindSqlState_StopsAtSixteenWrappers()
    {
        Exception found = Postgres("22021", "invalid byte sequence for encoding \"UTF8\": 0x00");
        for (var depth = 0; depth < 16; depth++)
        {
            found = new InvalidOperationException("wrapper", found);
        }
        var tooDeep = new InvalidOperationException("wrapper", found);

        Assert.Equal("22021", PostgresNullCharacter.FindSqlState(found));
        Assert.Null(PostgresNullCharacter.FindSqlState(tooDeep));
    }

    [Fact]
    public void Message_NamesTheCharacterAndWhereToRemoveIt()
    {
        Assert.Contains("null character (U+0000)", PostgresNullCharacter.Message, StringComparison.Ordinal);
        Assert.Contains("Remove it from the request", PostgresNullCharacter.Message, StringComparison.Ordinal);
    }

    private static PostgresException Postgres(string sqlState, string message) =>
        new(message, "ERROR", "ERROR", sqlState);
}
