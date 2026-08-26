using System.Reflection;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Tests;

public static class TestSharedVariableCatalog
{
    public static ISharedVariableRepository Create(params SharedVariableRecord[] variables)
    {
        var repository = DispatchProxy.Create<ISharedVariableRepository, CatalogProxy>();
        ((CatalogProxy)(object)repository).Variables = variables.ToDictionary(
            variable => variable.Key,
            StringComparer.OrdinalIgnoreCase);
        return repository;
    }

    public static SharedVariableRecord Active(
        long id,
        string key,
        string dataType,
        bool isArray = false,
        bool nullable = false,
        string? validation = null) =>
        new(
            id,
            key,
            dataType,
            isArray,
            nullable,
            validation,
            Description: null,
            HasValue: false,
            Value: null,
            SharedVariableStatuses.Active,
            Revision: 1,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            ArchivedAt: null,
            ValueRevision: 0);

    public class CatalogProxy : DispatchProxy
    {
        internal IReadOnlyDictionary<string, SharedVariableRecord> Variables { get; set; } =
            new Dictionary<string, SharedVariableRecord>(StringComparer.OrdinalIgnoreCase);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISharedVariableRepository.GetByKeyAsync))
            {
                var key = (string)args![0]!;
                Variables.TryGetValue(key, out var variable);
                return Task.FromResult<SharedVariableRecord?>(variable);
            }

            throw new NotSupportedException(
                $"Test shared-variable catalog does not implement '{targetMethod?.Name}'.");
        }
    }
}
