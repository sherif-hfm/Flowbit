using System.Security.Claims;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;

namespace Flowbit.Api.Auth;

public interface ISignalCallerResolver
{
    SignalCaller Resolve(ClaimsPrincipal principal);
}

/// <summary>
/// Turns an authorized principal into the caller of a signal command: a managed API
/// client by its client id, or a JWT user with roles and the selected audit claims.
/// </summary>
public sealed class SignalCallerResolver(IActorContextResolver actorResolver) : ISignalCallerResolver
{
    public SignalCaller Resolve(ClaimsPrincipal principal)
    {
        var clientIdentity = principal.Identities.FirstOrDefault(identity =>
            identity.IsAuthenticated
            && string.Equals(
                identity.AuthenticationType,
                SharedVariableClientAuthenticationDefaults.Scheme,
                StringComparison.Ordinal));
        if (clientIdentity is not null)
        {
            var clientId = clientIdentity.FindFirst(SharedVariableClientAuthenticationDefaults.ClientIdClaim)?.Value;
            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw new WorkflowUnauthorizedException("Invalid client credentials.");
            }

            return new SignalCaller(SignalThrowerKinds.Client, clientId, [], null);
        }

        var actor = actorResolver.Resolve(principal);
        if (string.IsNullOrWhiteSpace(actor.User))
        {
            throw new WorkflowUnauthorizedException(
                "The authenticated token does not contain a workflow actor identity.");
        }

        return new SignalCaller(
            SignalThrowerKinds.User,
            actor.User.Trim(),
            actor.Roles.ToArray(),
            actor.AuditClaims);
    }
}
