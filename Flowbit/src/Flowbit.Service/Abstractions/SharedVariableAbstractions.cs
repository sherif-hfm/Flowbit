using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

public interface ISharedVariableRepository
{
    Task<(IReadOnlyList<SharedVariableRecord> Items, long TotalCount)> ListAsync(
        string? search,
        string? status,
        int offset,
        int limit,
        bool includeArchived,
        CancellationToken cancellationToken);

    Task<SharedVariableRecord?> GetByKeyAsync(
        string key,
        bool includeArchived,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, SharedVariableRecord>> GetManyByKeyAsync(
        IReadOnlyCollection<string> keys,
        bool includeArchived,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, SharedVariableCurrentValueRecord>> LoadCurrentAsync(
        IReadOnlyCollection<string> keys,
        bool includeArchived,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, SharedVariableCurrentValueRecord>> LockCurrentAsync(
        IReadOnlyCollection<string> keys,
        bool includeArchived,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, SharedVariableValueStamp>> LoadValueStampsAsync(
        IReadOnlyCollection<string> keys,
        bool includeArchived,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, SharedVariableValueStamp>> LockValueStampsAsync(
        IReadOnlyCollection<string> keys,
        bool includeArchived,
        CancellationToken cancellationToken);

    Task PrelockDefinitionKeysForLegacyAllocatorAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken);

    Task<SharedVariableMutationResult> CreateAsync(
        SharedVariableCreateCommand command,
        CancellationToken cancellationToken);

    Task<SharedVariableMutationResult?> WriteAsync(
        SharedVariableWriteCommand command,
        CancellationToken cancellationToken);

    Task<SharedVariableMutationResult?> ChangeLifecycleAsync(
        SharedVariableLifecycleCommand command,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SharedVariableRevisionRecord>> ListRevisionsAsync(
        string key,
        int offset,
        int limit,
        CancellationToken cancellationToken);

    Task<SharedVariableLifecycleBlockersRecord?> GetLifecycleBlockersAsync(
        string key,
        CancellationToken cancellationToken);

    Task ReplaceDefinitionBindingsAsync(
        long workflowDefinitionId,
        IReadOnlyCollection<SharedVariableDefinitionBindingProjection> bindings,
        CancellationToken cancellationToken);
}

public interface ISharedVariableClientRepository
{
    Task<(IReadOnlyList<SharedVariableClientRecord> Items, long TotalCount)> ListAsync(
        int offset,
        int limit,
        CancellationToken cancellationToken);

    Task<SharedVariableClientRecord?> GetAsync(long id, CancellationToken cancellationToken);

    Task<(SharedVariableClientRecord Client, IReadOnlyList<SharedVariableClientSecretRecord> Secrets)?>
        GetAuthenticationMaterialAsync(string clientId, CancellationToken cancellationToken);

    Task<SharedVariableClientRecord> CreateAsync(
        SharedVariableClientCreateCommand command,
        CancellationToken cancellationToken);

    Task<SharedVariableClientRecord?> UpdateAsync(
        SharedVariableClientUpdateCommand command,
        CancellationToken cancellationToken);

    Task<SharedVariableClientRecord?> RotateAsync(
        SharedVariableClientRotateCommand command,
        CancellationToken cancellationToken);

    Task<SharedVariableClientRecord?> RevokeAsync(
        SharedVariableClientRevokeCommand command,
        CancellationToken cancellationToken);
}

public interface ISharedVariableService
{
    Task<PagedResult<SharedVariableDto>> ListAsync(
        SharedVariableListRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken);

    Task<SharedVariableDto?> GetAsync(
        string key,
        SharedVariableCaller caller,
        CancellationToken cancellationToken);

    Task<SharedVariableDto> CreateAsync(
        CreateSharedVariableRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken);

    Task<SharedVariableDto?> UpdateAsync(
        string key,
        UpdateSharedVariableRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken);

    Task<SharedVariableDto?> UpdateDescriptionAsync(
        string key,
        UpdateSharedVariableDescriptionRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken);

    Task<SharedVariableDto?> ArchiveAsync(
        string key,
        ArchiveSharedVariableRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken);

    Task<SharedVariableDto?> ReactivateAsync(
        string key,
        ReactivateSharedVariableRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SharedVariableRevisionDto>> ListHistoryAsync(
        string key,
        int page,
        int pageSize,
        SharedVariableCaller caller,
        CancellationToken cancellationToken);

    Task<SharedVariableLifecycleBlockersDto?> GetLifecycleBlockersAsync(
        string key,
        SharedVariableCaller caller,
        CancellationToken cancellationToken);
}

public interface ISharedVariableClientService
{
    Task<SharedVariableClientAuthentication?> AuthenticateAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken);

    Task<PagedResult<SharedVariableClientDto>> ListAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<SharedVariableClientDto?> GetAsync(long id, CancellationToken cancellationToken);

    Task<CreateSharedVariableClientResult> CreateAsync(
        CreateSharedVariableClientRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken);

    Task<SharedVariableClientDto?> UpdateAsync(
        long id,
        UpdateSharedVariableClientRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken);

    Task<RotateSharedVariableClientSecretResult?> RotateSecretAsync(
        long id,
        RotateSharedVariableClientSecretRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken);

    Task<SharedVariableClientDto?> RevokeAsync(
        long id,
        RevokeSharedVariableClientRequest request,
        SharedVariableCaller caller,
        CancellationToken cancellationToken);
}
