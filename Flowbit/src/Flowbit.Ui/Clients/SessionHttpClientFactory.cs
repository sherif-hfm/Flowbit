using Flowbit.Ui.Auth;

namespace Flowbit.Ui.Clients;

/// <summary>Handlers belong to the circuit, while pooled transports never capture a user's state.</summary>
public sealed class SessionHttpClientFactory(IHttpMessageHandlerFactory handlers, IUiAccessTokenProvider tokens,
    IConfiguration configuration) : IHttpClientFactory
{
    public const string ApiClientName = "Flowbit.Api";
    public HttpClient CreateClient(string name)
    {
        var transport = handlers.CreateHandler(name);
        var client = new HttpClient(new AuthTokenHandler(tokens)
        {
            InnerHandler = new BorrowedTransport(transport)
        })
        {
            BaseAddress = new Uri(configuration["WorkflowApi:BaseUrl"] ?? "http://localhost:5017"),
            Timeout = name == WorkflowApiClient.AiClientName ? WorkflowApiClient.AiRequestTimeout : TimeSpan.FromMinutes(6)
        };
        return client;
    }

    // Disposing an individual request client must not dispose the factory-owned handler pool.
    private sealed class BorrowedTransport(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker invoker = new(inner, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => invoker.SendAsync(request, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) invoker.Dispose(); base.Dispose(disposing); }
    }
}
