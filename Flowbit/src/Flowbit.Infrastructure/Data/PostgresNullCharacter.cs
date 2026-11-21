using Npgsql;

namespace Flowbit.Infrastructure.Data;

/// <summary>
/// PostgreSQL cannot hold the null character (U+0000) in <c>text</c> or <c>jsonb</c>. A
/// .NET string carrying one fails at the database: as a text parameter with SQLSTATE
/// <c>22021</c>, naming the byte <c>0x00</c>, and inside a <c>jsonb</c> value with SQLSTATE
/// <c>22P05</c>, its <c>\u0000</c> escape. Stored data never holds the character, and the
/// driver refuses other text it cannot encode before sending it, so either error means the
/// string came from the request, or from a script or service response the request ran.
/// </summary>
public static class PostgresNullCharacter
{
    /// <summary>The <c>error</c> the API returns with <c>400</c>.</summary>
    public const string Message =
        "Text cannot contain a null character (U+0000), because PostgreSQL can neither store nor search for one. "
        + "Remove it from the request, or from the script or service response that produced it.";

    // A wrapper chain this deep is not one the API builds; stop rather than walk a cycle.
    private const int MaxDepth = 16;

    /// <summary>
    /// The SQLSTATE of the PostgreSQL error inside <paramref name="exception"/> that refused a
    /// null character, or <see langword="null"/> when it holds no such error. The error may be
    /// the exception itself or one it wraps, such as the inner exception of a
    /// <c>DbUpdateException</c>.
    /// </summary>
    public static string? FindSqlState(Exception exception)
    {
        var pending = new Stack<(Exception Exception, int Depth)>();
        pending.Push((exception, 0));
        while (pending.Count > 0)
        {
            var (current, depth) = pending.Pop();
            if (current is PostgresException postgres && RefusedNullCharacter(postgres))
            {
                return postgres.SqlState;
            }

            if (depth == MaxDepth)
            {
                continue;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push((inner, depth + 1));
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push((inner, depth + 1));
            }
        }

        return null;
    }

    // 22021 also names other invalid bytes, which the driver never sends; the byte is printed
    // in hex whatever language the server writes its messages in.
    private static bool RefusedNullCharacter(PostgresException exception) => exception.SqlState switch
    {
        PostgresErrorCodes.UntranslatableCharacter => true,
        PostgresErrorCodes.CharacterNotInRepertoire => exception.MessageText.Contains("0x00", StringComparison.Ordinal),
        _ => false
    };
}
