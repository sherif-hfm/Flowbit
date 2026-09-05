using System.Text;
using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>Authoring validation and bounded, fail-closed task-entry role resolution.</summary>
public static class UserTaskRolePolicyResolver
{
    public const int MaxRoles = 100;
    public const int MaxRoleLength = 300;

    public static void ValidateRoleSource(
        WorkflowModel definition, string? rolesVariable, IEnumerable<string>? literalRoles, string owner)
    {
        if (rolesVariable is null) return;
        if (string.IsNullOrWhiteSpace(rolesVariable))
            throw new WorkflowDomainException($"{owner} rolesVariable must name a declared string[] variable.");
        if ((literalRoles ?? []).Any(role => !string.IsNullOrWhiteSpace(role)))
            throw new WorkflowDomainException($"{owner} cannot combine rolesVariable with nonempty literal roles.");
        var matches = (definition.Variables ?? []).Where(variable => variable is not null
            && string.Equals(variable.Name, rolesVariable.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].DataType != WorkflowVariableTypes.String || !matches[0].IsArray)
            throw new WorkflowDomainException($"{owner} rolesVariable '{rolesVariable}' must reference a declared string[] variable.");
    }

    public static IReadOnlyList<string> ResolveRoles(
        WorkflowModel definition, string? rolesVariable, IEnumerable<string>? literalRoles,
        IReadOnlyDictionary<string, JsonElement> values)
    {
        if (rolesVariable is null)
            return (literalRoles ?? []).Where(role => !string.IsNullOrWhiteSpace(role))
                .Select(role => role.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        ValidateRoleSource(definition, rolesVariable, literalRoles, "User task role policy");
        var value = values.FirstOrDefault(pair => string.Equals(
            pair.Key, rolesVariable.Trim(), StringComparison.OrdinalIgnoreCase)).Value;
        if (value.ValueKind != JsonValueKind.Array)
            throw new WorkflowDomainException($"Role variable '{rolesVariable}' must have a nonempty string[] value when the task is created.");
        if (value.GetArrayLength() > MaxRoles)
            throw new WorkflowDomainException($"Role variable '{rolesVariable}' cannot contain more than {MaxRoles} roles.");
        var roles = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new WorkflowDomainException($"Role variable '{rolesVariable}' must contain only strings.");
            roles.Add(item.GetString()!);
        }
        var normalized = NormalizeManagedRoles(roles);
        if (normalized.Count == 0)
            throw new WorkflowDomainException($"Role variable '{rolesVariable}' must contain at least one nonblank role.");
        return normalized;
    }

    /// <summary>Empty is allowed only for an explicitly supplied unrestricted policy.</summary>
    public static IReadOnlyList<string> NormalizeManagedRoles(IEnumerable<string> roles)
    {
        if (roles is null) throw new WorkflowDomainException("A role list is required; use [] for unrestricted access.");
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var role in roles)
        {
            if (++count > MaxRoles)
                throw new WorkflowDomainException($"A role list cannot contain more than {MaxRoles} roles.");
            if (role is null)
                throw new WorkflowDomainException("A role list must contain only strings.");
            var normalized = role.Trim();
            if (normalized.EnumerateRunes().Count() > MaxRoleLength)
                throw new WorkflowDomainException($"A role cannot exceed {MaxRoleLength} Unicode characters.");
            if (normalized.Length > 0 && seen.Add(normalized)) result.Add(normalized);
        }
        return result;
    }
}
