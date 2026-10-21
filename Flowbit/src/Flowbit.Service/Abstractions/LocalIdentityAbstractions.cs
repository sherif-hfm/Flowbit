using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

public interface ILocalIdentityRepository
{
    Task<IAsyncDisposable> BeginAsync(CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
    Task<LocalUserAccount?> FindUserAsync(long id, CancellationToken ct);
    Task<LocalUserAccount?> FindUsernameAsync(string username, CancellationToken ct);
    Task<PagedResult<LocalUserDto>> ListAsync(string? search, bool? active, int page, int pageSize, CancellationToken ct);
    Task<IReadOnlyList<LocalUserAccount>> AllUsersAsync(CancellationToken ct);
    Task SaveUserAsync(LocalUserAccount user, CancellationToken ct);
    Task<IReadOnlyList<UserAttributeDto>> AttributesAsync(CancellationToken ct);
    Task<UserAttributeDto> SaveAttributeAsync(UserAttributeDto attribute, CancellationToken ct);
    Task<LocalRefreshSession?> FindRefreshAsync(string hash, CancellationToken ct);
    Task SaveRefreshAsync(LocalRefreshSession session, CancellationToken ct);
    Task RevokeAsync(long? userId, Guid? familyId, CancellationToken ct);
    Task<bool> SessionActiveAsync(long userId, Guid familyId, DateTimeOffset now, CancellationToken ct);
}
public interface ILocalPasswordHasher
{
    string Hash(string password);
    bool Verify(string hash, string password);
}
public interface ILocalTokenIssuer
{
    string Issue(LocalUserAccount user, IReadOnlyList<UserClaimDto> claims, Guid sessionId, DateTimeOffset expiresAt);
    IReadOnlySet<string> ReservedClaims { get; }
}
