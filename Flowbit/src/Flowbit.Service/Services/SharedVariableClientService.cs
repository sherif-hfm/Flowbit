using System.Security.Cryptography;
using System.Text;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

public sealed class SharedVariableClientService(
    ISharedVariableClientRepository repository,
    TimeProvider timeProvider,
    ILogger<SharedVariableClientService> logger) : ISharedVariableClientService
{
    private const string Algorithm = "PBKDF2-SHA256";
    private const int Iterations = 210_000;
    private const int DigestLength = 32;
    private static readonly byte[] DummySalt = SHA256.HashData("flowbit-shared-variable-client"u8);
    private static readonly byte[] DummyDigest = new byte[DigestLength];

    public async Task<SharedVariableClientAuthentication?> AuthenticateAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var material = string.IsNullOrEmpty(clientId)
            ? null
            : await repository.GetAuthenticationMaterialAsync(clientId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var candidates = material?.Secrets
            .Where(secret => secret.RevokedAt is null
                && (secret.ValidUntil is null || secret.ValidUntil > now))
            .OrderByDescending(secret => secret.Version)
            .Take(2)
            .ToArray() ?? [];

        var matchedVersion = 0;
        if (candidates.Length == 0)
        {
            var dummy = Derive(clientSecret ?? string.Empty, DummySalt, Iterations);
            _ = CryptographicOperations.FixedTimeEquals(dummy, DummyDigest);
            CryptographicOperations.ZeroMemory(dummy);
        }
        else
        {
            foreach (var candidate in candidates)
            {
                if (!string.Equals(candidate.Algorithm, Algorithm, StringComparison.Ordinal)
                    || candidate.Iterations is < 100_000 or > 2_000_000)
                    continue;
                var derived = Derive(clientSecret ?? string.Empty, candidate.Salt, candidate.Iterations);
                var matched = CryptographicOperations.FixedTimeEquals(derived, candidate.Digest);
                CryptographicOperations.ZeroMemory(derived);
                if (matched) matchedVersion = candidate.Version;
            }
        }

        if (material is null
            || matchedVersion == 0
            || material.Value.Client.Status != SharedVariableClientStatuses.Active
            || (material.Value.Client.ExpiresAt is DateTimeOffset expiresAt && expiresAt <= now))
            return null;

        return new SharedVariableClientAuthentication(
            material.Value.Client.Id,
            material.Value.Client.ClientId,
            material.Value.Client.DisplayName,
            material.Value.Client.Scopes,
            matchedVersion);
    }

    public async Task<PagedResult<SharedVariableClientDto>> ListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ValidatePage(page, pageSize);
        var (items, total) = await repository.ListAsync(
            checked((page - 1) * pageSize),
            pageSize,
            cancellationToken);
        return new PagedResult<SharedVariableClientDto>(
            items.Select(Map).ToArray(),
            page,
            pageSize,
            total);
    }

    public async Task<SharedVariableClientDto?> GetAsync(
        long id,
        CancellationToken cancellationToken)
    {
        ValidateId(id);
        var record = await repository.GetAsync(id, cancellationToken);
        return record is null ? null : Map(record);
    }

    public async Task<CreateSharedVariableClientResult> CreateAsync(
        CreateSharedVariableClientRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken)
    {
        ValidateCaller(caller);
        var clientId = NormalizeClientId(request.ClientId);
        var displayName = NormalizeDisplayName(request.DisplayName);
        var scopes = NormalizeScopes(request.Scopes);
        if (request.ExpiresAt is DateTimeOffset expiresAt
            && expiresAt <= timeProvider.GetUtcNow())
            throw new WorkflowDomainException("expiresAt must be in the future.");

        var secret = GenerateSecret();
        var salt = RandomNumberGenerator.GetBytes(32);
        var digest = Derive(secret, salt, Iterations);
        try
        {
            var record = await repository.CreateAsync(
                new SharedVariableClientCreateCommand(
                    clientId,
                    displayName,
                    scopes,
                    request.ExpiresAt,
                    caller.Kind,
                    caller.Id,
                    Algorithm,
                    Iterations,
                    salt,
                    digest),
                cancellationToken);
            logger.LogInformation(
                "Shared-variable API client {ClientId} created by {CallerKind} {CallerId}.",
                record.ClientId,
                caller.Kind,
                caller.Id);
            return new CreateSharedVariableClientResult(Map(record), secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public async Task<RotateSharedVariableClientSecretResult?> RotateSecretAsync(
        long id,
        RotateSharedVariableClientSecretRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken)
    {
        ValidateCaller(caller);
        ValidateId(id);
        ValidateRevision(request.ExpectedRevision);
        if (request.GracePeriodHours is < 0 or > 168)
            throw new WorkflowDomainException("gracePeriodHours must be between 0 and 168.");

        var secret = GenerateSecret();
        var salt = RandomNumberGenerator.GetBytes(32);
        var digest = Derive(secret, salt, Iterations);
        try
        {
            var record = await repository.RotateAsync(
                new SharedVariableClientRotateCommand(
                    id,
                    request.ExpectedRevision,
                    timeProvider.GetUtcNow().AddHours(request.GracePeriodHours),
                    caller.Kind,
                    caller.Id,
                    Algorithm,
                    Iterations,
                    salt,
                    digest),
                cancellationToken);
            if (record is null) return null;
            logger.LogInformation(
                "Shared-variable API client {ClientId} rotated to revision {Revision}.",
                record.ClientId,
                record.Revision);
            return new RotateSharedVariableClientSecretResult(Map(record), secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public async Task<SharedVariableClientDto?> UpdateAsync(
        long id,
        UpdateSharedVariableClientRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken)
    {
        ValidateCaller(caller);
        ValidateId(id);
        ValidateRevision(request.ExpectedRevision);
        var displayName = NormalizeDisplayName(request.DisplayName);
        var scopes = NormalizeScopes(request.Scopes);
        if (request.ExpiresAt is DateTimeOffset expiresAt
            && expiresAt <= timeProvider.GetUtcNow())
        {
            throw new WorkflowDomainException("expiresAt must be in the future.");
        }

        var record = await repository.UpdateAsync(
            new SharedVariableClientUpdateCommand(
                id,
                request.ExpectedRevision,
                displayName,
                scopes,
                request.ExpiresAt,
                caller.Kind,
                caller.Id),
            cancellationToken);
        if (record is not null)
        {
            logger.LogInformation(
                "Shared-variable API client {ClientId} metadata and scopes updated at revision {Revision}.",
                record.ClientId,
                record.Revision);
        }
        return record is null ? null : Map(record);
    }

    public async Task<SharedVariableClientDto?> RevokeAsync(
        long id,
        RevokeSharedVariableClientRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken)
    {
        ValidateCaller(caller);
        ValidateId(id);
        ValidateRevision(request.ExpectedRevision);
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason?.EnumerateRunes().Count() > 1000)
            throw new WorkflowDomainException("reason must contain at most 1000 Unicode scalar values.");
        var record = await repository.RevokeAsync(
            new SharedVariableClientRevokeCommand(
                id,
                request.ExpectedRevision,
                caller.Kind,
                caller.Id,
                reason),
            cancellationToken);
        if (record is not null)
        {
            logger.LogInformation(
                "Shared-variable API client {ClientId} revoked at revision {Revision}.",
                record.ClientId,
                record.Revision);
        }
        return record is null ? null : Map(record);
    }

    private static byte[] Derive(string secret, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            DigestLength);

    private static string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            return "fsv_" + Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string NormalizeClientId(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 3 or > 100
            || !normalized.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '_' or ':' or '-'))
            throw new WorkflowDomainException(
                "clientId must be 3-100 ASCII letters, digits, '.', '_', ':', or '-'.");
        return normalized;
    }

    private static string NormalizeDisplayName(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.EnumerateRunes().Count() > 300)
            throw new WorkflowDomainException("displayName is required and must be at most 300 characters.");
        return normalized;
    }

    private static IReadOnlyCollection<string> NormalizeScopes(IReadOnlyCollection<string> scopes)
    {
        if (scopes is null || scopes.Count == 0)
            throw new WorkflowDomainException("At least one shared-variable client scope is required.");
        var normalized = scopes
            .Select(scope => scope?.Trim() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var invalid = normalized.FirstOrDefault(scope => !SharedVariableClientScopes.Allowed.Contains(scope));
        if (invalid is not null)
            throw new WorkflowDomainException($"Unknown shared-variable client scope '{invalid}'.");
        return normalized;
    }

    private static SharedVariableClientDto Map(SharedVariableClientRecord record) => new(
        record.Id,
        record.ClientId,
        record.DisplayName,
        record.Scopes,
        record.Status,
        record.Revision,
        record.ExpiresAt,
        record.CreatedAt,
        record.UpdatedAt,
        record.RevokedAt);

    private static void ValidateCaller(SharedVariableCaller caller)
    {
        if (caller is null
            || string.IsNullOrWhiteSpace(caller.Kind)
            || string.IsNullOrWhiteSpace(caller.Id)
            || caller.Id.EnumerateRunes().Count() > 300)
            throw new WorkflowDomainException("A valid client-management caller is required.");
    }

    private static void ValidatePage(int page, int pageSize)
    {
        if (page <= 0) throw new WorkflowDomainException("page must be greater than zero.");
        if (pageSize is < 1 or > 200)
            throw new WorkflowDomainException("pageSize must be between 1 and 200.");
    }

    private static void ValidateId(long id)
    {
        if (id <= 0) throw new WorkflowDomainException("client id must be greater than zero.");
    }

    private static void ValidateRevision(long revision)
    {
        if (revision <= 0) throw new WorkflowDomainException("expectedRevision must be greater than zero.");
    }
}
