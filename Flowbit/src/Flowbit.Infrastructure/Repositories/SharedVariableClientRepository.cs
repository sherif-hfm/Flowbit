using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Flowbit.Infrastructure.Repositories;

public sealed class SharedVariableClientRepository(AppDbContext dbContext)
    : ISharedVariableClientRepository
{
    private const string ProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public async Task<(IReadOnlyList<SharedVariableClientRecord> Items, long TotalCount)> ListAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var total = await dbContext.SharedVariableClients.LongCountAsync(cancellationToken);
        var clients = await dbContext.SharedVariableClients.AsNoTracking()
            .OrderBy(client => client.DisplayName)
            .ThenBy(client => client.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return (clients.Select(Map).ToArray(), total);
    }

    public async Task<SharedVariableClientRecord?> GetAsync(
        long id,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.SharedVariableClients.AsNoTracking()
            .SingleOrDefaultAsync(client => client.Id == id, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<(SharedVariableClientRecord Client, IReadOnlyList<SharedVariableClientSecretRecord> Secrets)?>
        GetAuthenticationMaterialAsync(
            string clientId,
            CancellationToken cancellationToken)
    {
        var entity = await dbContext.SharedVariableClients.AsNoTracking()
            .Include(client => client.Secrets)
            .SingleOrDefaultAsync(client => client.ClientId == clientId, cancellationToken);
        if (entity is null) return null;
        return (
            Map(entity),
            entity.Secrets.OrderByDescending(secret => secret.Version).Select(Map).ToArray());
    }

    public async Task<SharedVariableClientRecord> CreateAsync(
        SharedVariableClientCreateCommand command,
        CancellationToken cancellationToken)
    {
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        var now = UtcNow();
        var entity = new SharedVariableClientEntity
        {
            ClientId = command.ClientId,
            DisplayName = command.DisplayName,
            Scopes = command.Scopes.Order(StringComparer.Ordinal).ToList(),
            Status = SharedVariableClientStatuses.Active,
            Revision = 1,
            ActiveSecretVersion = 1,
            ExpiresAt = command.ExpiresAt,
            CreatedByKind = command.CreatedByKind,
            CreatedById = command.CreatedById,
            UpdatedByKind = command.CreatedByKind,
            UpdatedById = command.CreatedById,
            CreatedAt = now,
            UpdatedAt = now
        };
        entity.Secrets.Add(new SharedVariableClientSecretEntity
        {
            Version = 1,
            Algorithm = command.Algorithm,
            Iterations = command.Iterations,
            Salt = command.Salt.ToArray(),
            Digest = command.Digest.ToArray(),
            CreatedAt = now
        });
        dbContext.SharedVariableClients.Add(entity);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new WorkflowConflictException(
                $"Shared-variable client id '{command.ClientId}' already exists.");
        }
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<SharedVariableClientRecord?> RotateAsync(
        SharedVariableClientRotateCommand command,
        CancellationToken cancellationToken)
    {
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        var entity = await LockClientAsync(command.ClientId, cancellationToken);
        if (entity is null) return null;
        if (entity.Revision != command.ExpectedRevision)
        {
            throw Stale(entity);
        }
        if (entity.Status != SharedVariableClientStatuses.Active)
        {
            throw new WorkflowConflictException(
                $"Shared-variable client '{entity.ClientId}' is revoked and cannot rotate secrets.");
        }

        await dbContext.Entry(entity).Collection(client => client.Secrets).LoadAsync(cancellationToken);
        var current = entity.Secrets.SingleOrDefault(secret =>
            secret.Version == entity.ActiveSecretVersion);
        if (current is null)
        {
            throw new InvalidOperationException(
                $"Shared-variable client '{entity.ClientId}' has no active secret version.");
        }
        var now = UtcNow();
        foreach (var older in entity.Secrets.Where(secret =>
                     secret.Version != current.Version && secret.RevokedAt is null))
        {
            older.RevokedAt = now;
            older.ValidUntil = now;
        }
        current.ValidUntil = command.PreviousSecretValidUntil;

        var nextVersion = checked(entity.ActiveSecretVersion + 1);
        entity.Secrets.Add(new SharedVariableClientSecretEntity
        {
            ClientId = entity.Id,
            Version = nextVersion,
            Algorithm = command.Algorithm,
            Iterations = command.Iterations,
            Salt = command.Salt.ToArray(),
            Digest = command.Digest.ToArray(),
            CreatedAt = now
        });
        entity.ActiveSecretVersion = nextVersion;
        entity.Revision++;
        entity.UpdatedByKind = command.RotatedByKind;
        entity.UpdatedById = command.RotatedById;
        entity.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<SharedVariableClientRecord?> UpdateAsync(
        SharedVariableClientUpdateCommand command,
        CancellationToken cancellationToken)
    {
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        var entity = await LockClientAsync(command.ClientId, cancellationToken);
        if (entity is null)
        {
            return null;
        }
        if (entity.Revision != command.ExpectedRevision)
        {
            throw Stale(entity);
        }
        if (entity.Status != SharedVariableClientStatuses.Active)
        {
            throw new WorkflowConflictException(
                $"Shared-variable client '{entity.ClientId}' is revoked and cannot be edited.");
        }

        entity.DisplayName = command.DisplayName;
        entity.Scopes = command.Scopes.Order(StringComparer.Ordinal).ToList();
        entity.ExpiresAt = command.ExpiresAt;
        entity.Revision++;
        entity.UpdatedByKind = command.UpdatedByKind;
        entity.UpdatedById = command.UpdatedById;
        entity.UpdatedAt = UtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        if (ownedTransaction is not null)
        {
            await ownedTransaction.CommitAsync(cancellationToken);
        }
        return Map(entity);
    }

    public async Task<SharedVariableClientRecord?> RevokeAsync(
        SharedVariableClientRevokeCommand command,
        CancellationToken cancellationToken)
    {
        await using var ownedTransaction = await BeginOwnedTransactionAsync(cancellationToken);
        var entity = await LockClientAsync(command.ClientId, cancellationToken);
        if (entity is null) return null;
        if (entity.Revision != command.ExpectedRevision)
        {
            throw Stale(entity);
        }

        await dbContext.Entry(entity).Collection(client => client.Secrets).LoadAsync(cancellationToken);
        var now = UtcNow();
        foreach (var secret in entity.Secrets.Where(secret => secret.RevokedAt is null))
        {
            secret.RevokedAt = now;
            secret.ValidUntil = now;
        }
        entity.Status = SharedVariableClientStatuses.Revoked;
        entity.RevokedAt = now;
        entity.RevocationReason = string.IsNullOrWhiteSpace(command.Reason)
            ? null
            : command.Reason.Trim();
        entity.Revision++;
        entity.UpdatedByKind = command.RevokedByKind;
        entity.UpdatedById = command.RevokedById;
        entity.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
        return Map(entity);
    }

    private async Task<SharedVariableClientEntity?> LockClientAsync(
        long id,
        CancellationToken cancellationToken) => IsNpgsql()
        ? await dbContext.SharedVariableClients.FromSqlInterpolated(
                $"""SELECT * FROM flowbit.shared_variable_clients WHERE "Id" = {id} FOR UPDATE""")
            .SingleOrDefaultAsync(cancellationToken)
        : await dbContext.SharedVariableClients.SingleOrDefaultAsync(client => client.Id == id, cancellationToken);

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?>
        BeginOwnedTransactionAsync(CancellationToken cancellationToken) =>
        dbContext.Database.IsRelational() && dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

    private bool IsNpgsql() =>
        string.Equals(dbContext.Database.ProviderName, ProviderName, StringComparison.Ordinal);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static WorkflowConflictException Stale(SharedVariableClientEntity entity) =>
        new($"Shared-variable client '{entity.ClientId}' is at revision {entity.Revision}; reload and retry.");

    private static DateTimeOffset UtcNow()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }

    private static SharedVariableClientRecord Map(SharedVariableClientEntity entity) => new(
        entity.Id,
        entity.ClientId,
        entity.DisplayName,
        entity.Scopes.Order(StringComparer.Ordinal).ToArray(),
        entity.Status,
        entity.Revision,
        entity.ExpiresAt,
        entity.CreatedAt,
        entity.UpdatedAt,
        entity.RevokedAt);

    private static SharedVariableClientSecretRecord Map(SharedVariableClientSecretEntity entity) => new(
        entity.Id,
        entity.ClientId,
        entity.Version,
        entity.Algorithm,
        entity.Iterations,
        entity.Salt.ToArray(),
        entity.Digest.ToArray(),
        entity.CreatedAt,
        entity.ValidUntil,
        entity.RevokedAt);
}
