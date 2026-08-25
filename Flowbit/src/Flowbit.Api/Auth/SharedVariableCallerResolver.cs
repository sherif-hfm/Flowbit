using System.Security.Claims;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;

namespace Flowbit.Api.Auth;

public interface ISharedVariableCallerResolver
{
    SharedVariableCaller Resolve(ClaimsPrincipal principal);
}

public sealed class SharedVariableCallerResolver(IActorContextResolver actorResolver)
    : ISharedVariableCallerResolver
{
    public SharedVariableCaller Resolve(ClaimsPrincipal principal)
    {
        var clientIdentity = principal.Identities.FirstOrDefault(identity =>
            identity.IsAuthenticated
            && string.Equals(
                identity.AuthenticationType,
                SharedVariableClientAuthenticationDefaults.Scheme,
                StringComparison.Ordinal));
        if (clientIdentity is not null)
        {
            var clientId = clientIdentity.FindFirst(
                SharedVariableClientAuthenticationDefaults.ClientIdClaim)?.Value;
            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw new WorkflowUnauthorizedException("Invalid client credentials.");
            }

            var scopes = clientIdentity
                .FindAll(SharedVariableClientAuthenticationDefaults.ScopeClaim)
                .Select(claim => claim.Value)
                .Where(scope => !string.IsNullOrWhiteSpace(scope))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new SharedVariableCaller(
                SharedVariableCallerKinds.Client,
                clientId,
                [],
                scopes);
        }

        var actor = actorResolver.Resolve(principal);
        if (string.IsNullOrWhiteSpace(actor.User))
        {
            throw new WorkflowUnauthorizedException(
                "The authenticated token does not contain a workflow actor identity.");
        }

        return new SharedVariableCaller(
            SharedVariableCallerKinds.User,
            actor.User.Trim(),
            actor.Roles,
            []);
    }
}
