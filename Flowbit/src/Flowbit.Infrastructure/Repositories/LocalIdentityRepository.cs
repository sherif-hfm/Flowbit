using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Flowbit.Infrastructure.Repositories;

public sealed class LocalIdentityRepository(AppDbContext db) : ILocalIdentityRepository
{
    private IDbContextTransaction? transaction;
    public async Task<IAsyncDisposable> BeginAsync(CancellationToken ct)
    {
        transaction = await db.Database.BeginTransactionAsync(ct);
        // One transaction-scoped directory lock makes bootstrap, last-admin protection,
        // schema edits, lockout and refresh rotation consistent across API replicas.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(732468160921)", ct);
        await db.LocalRefreshSessions.Where(s => s.ExpiresAt <= DateTimeOffset.UtcNow).ExecuteDeleteAsync(ct);
        return new Scope(transaction, db);
    }
    public async Task CommitAsync(CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await transaction!.CommitAsync(ct);
    }
    public async Task<LocalUserAccount?> FindUserAsync(long id, CancellationToken ct)
    { var entity = await db.LocalUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct); return entity is null ? null : Map(entity); }
    public async Task<LocalUserAccount?> FindUsernameAsync(string username, CancellationToken ct)
    { var entity = await db.LocalUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username, ct); return entity is null ? null : Map(entity); }
    public async Task<IReadOnlyList<LocalUserAccount>> AllUsersAsync(CancellationToken ct)
        => (await db.LocalUsers.AsNoTracking().ToListAsync(ct)).Select(Map).ToArray();
    public async Task<PagedResult<LocalUserDto>> ListAsync(string? search, bool? active, int page, int pageSize, CancellationToken ct)
    {
        var query = db.LocalUsers.AsNoTracking();
        if (active is not null) query = query.Where(u => u.IsActive == active);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(u => EF.Functions.ILike(u.Username, pattern) || EF.Functions.ILike(u.FirstName, pattern) || EF.Functions.ILike(u.LastName, pattern));
        }
        var count = await query.CountAsync(ct);
        var entities = await query.OrderBy(u => u.Username).ThenBy(u => u.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(entities.Select(e => LocalIdentityService.ToDto(Map(e))).ToArray(), page, pageSize, count);
    }
    public async Task SaveUserAsync(LocalUserAccount u, CancellationToken ct)
    {
        var entity = u.Id == 0 ? null : await db.LocalUsers.FindAsync([u.Id], ct);
        if (entity is null) { entity = new(); db.LocalUsers.Add(entity); }
        entity.Username = u.Username; entity.FirstName = u.FirstName; entity.LastName = u.LastName;
        entity.IsActive = u.IsActive; entity.PasswordHash = u.PasswordHash;
        entity.RolesJson = JsonSerializer.Serialize(u.Roles); entity.ClaimsJson = JsonSerializer.Serialize(u.Claims);
        entity.AttributesJson = JsonSerializer.Serialize(u.Attributes); entity.Revision = u.Revision;
        entity.CreatedAt = u.CreatedAt; entity.UpdatedAt = u.UpdatedAt;
        entity.FailedAttempts = u.FailedAttempts; entity.LockedUntil = u.LockedUntil;
        await db.SaveChangesAsync(ct); u.Id = entity.Id;
    }
    public async Task<IReadOnlyList<UserAttributeDto>> AttributesAsync(CancellationToken ct)
        => (await db.UserAttributes.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct))
            .Select(e => new UserAttributeDto(e.Id, e.Key, e.Type, e.Label, e.Required,
                JsonSerializer.Deserialize<string[]>(e.ChoicesJson)!, e.ClaimType, e.Revision)).ToArray();
    public async Task<UserAttributeDto> SaveAttributeAsync(UserAttributeDto a, CancellationToken ct)
    {
        var entity = a.Id == 0 ? null : await db.UserAttributes.FindAsync([a.Id], ct);
        if (entity is null) { entity = new(); db.UserAttributes.Add(entity); }
        entity.Key = a.Key; entity.Type = a.Type; entity.Label = a.Label; entity.Required = a.Required;
        entity.ChoicesJson = JsonSerializer.Serialize(a.Choices); entity.ClaimType = a.ClaimType; entity.Revision = a.Revision;
        await db.SaveChangesAsync(ct);
        return a with { Id = entity.Id };
    }
    public async Task<LocalRefreshSession?> FindRefreshAsync(string hash, CancellationToken ct)
    {
        var e = await db.LocalRefreshSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Hash == hash, ct);
        return e is null ? null : new() { Hash = e.Hash, FamilyId = e.FamilyId, UserId = e.UserId, ExpiresAt = e.ExpiresAt, Used = e.Used, Revoked = e.Revoked };
    }
    public async Task SaveRefreshAsync(LocalRefreshSession s, CancellationToken ct)
    {
        var entity = await db.LocalRefreshSessions.FindAsync([s.Hash], ct);
        if (entity is null) { entity = new() { Hash = s.Hash }; db.LocalRefreshSessions.Add(entity); }
        entity.FamilyId = s.FamilyId; entity.UserId = s.UserId; entity.ExpiresAt = s.ExpiresAt;
        entity.Used = s.Used; entity.Revoked = s.Revoked;
        await db.SaveChangesAsync(ct);
    }
    public Task RevokeAsync(long? userId, Guid? familyId, CancellationToken ct)
        => db.LocalRefreshSessions.Where(s => (!userId.HasValue || s.UserId == userId) && (!familyId.HasValue || s.FamilyId == familyId))
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Revoked, true), ct);
    public Task<bool> SessionActiveAsync(long userId, Guid familyId, DateTimeOffset now, CancellationToken ct)
        => db.LocalRefreshSessions.AnyAsync(s => s.UserId == userId && s.FamilyId == familyId && !s.Revoked && !s.Used && s.ExpiresAt > now
            && db.LocalUsers.Any(u => u.Id == userId && u.IsActive), ct);
    private static LocalUserAccount Map(LocalUserEntity e) => new() { Id = e.Id, Username = e.Username,
        FirstName = e.FirstName, LastName = e.LastName, IsActive = e.IsActive, PasswordHash = e.PasswordHash,
        Roles = JsonSerializer.Deserialize<string[]>(e.RolesJson)!, Claims = JsonSerializer.Deserialize<UserClaimDto[]>(e.ClaimsJson)!,
        Attributes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(e.AttributesJson)!, Revision = e.Revision,
        CreatedAt = e.CreatedAt, UpdatedAt = e.UpdatedAt, FailedAttempts = e.FailedAttempts, LockedUntil = e.LockedUntil };
    private sealed class Scope(IDbContextTransaction transaction, AppDbContext db) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await transaction.DisposeAsync(); db.ChangeTracker.Clear(); }
    }
}

public sealed class LocalPasswordHasher : ILocalPasswordHasher
{
    private readonly PasswordHasher<object> hasher = new();
    private readonly object user = new();
    private readonly string dummy;
    public LocalPasswordHasher() => dummy = hasher.HashPassword(user, Guid.NewGuid().ToString());
    public string Hash(string password) => hasher.HashPassword(user, password);
    public bool Verify(string hash, string password)
    {
        var result = hasher.VerifyHashedPassword(user, string.IsNullOrEmpty(hash) ? dummy : hash, password);
        return !string.IsNullOrEmpty(hash) && result != PasswordVerificationResult.Failed;
    }
}
