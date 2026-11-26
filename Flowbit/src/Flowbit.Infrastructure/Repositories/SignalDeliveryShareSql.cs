namespace Flowbit.Infrastructure.Repositories;

/// <summary>
/// The one SQL condition that decides which delivery job owns a delivery of a split broadcast
/// (EVENT-04G). The page query of a share job and the lost-lease statement both use it, so they
/// always agree on a job's share. The share is computed when it is needed and never stored, so
/// it need not be the same in another PostgreSQL version.
/// </summary>
internal static class SignalDeliveryShareSql
{
    /// <summary>
    /// True when the delivery aliased <paramref name="delivery"/> belongs to share
    /// <paramref name="shareIndex"/> of <paramref name="shareCount"/>; both are SQL expressions.
    /// A hash of the instance ID, not its remainder, because instance IDs follow patterns: a
    /// parent that calls one child per case gives the children every second ID. A start
    /// delivery has no instance until it is delivered, so it falls in the share of instance ID 0.
    /// hashint8 returns a signed 32-bit value; the mask makes it non-negative before the remainder.
    /// </summary>
    public static string Condition(string delivery, string shareIndex, string shareCount) =>
        $"""({shareCount} = 1 OR (hashint8(COALESCE({delivery}."InstanceId", 0))::bigint & 2147483647) % {shareCount} = {shareIndex})""";
}
