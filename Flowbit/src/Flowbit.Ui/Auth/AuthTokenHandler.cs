using System.Net.Http.Headers;

namespace Flowbit.Ui.Auth;

/// <summary>
/// Acquires the current circuit's API token and attaches it to this request only.
/// Responses are never retried, including rejected workflow mutations.
/// </summary>
public sealed class AuthTokenHandler(IUiAccessTokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await tokens.GetAccessTokenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized) tokens.RejectSession();
        return response;
    }
}
