extern alias FlowbitUi;

using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Configuration;
using Xunit;
using Auth = FlowbitUi::Flowbit.Ui.Auth;
using Clients = FlowbitUi::Flowbit.Ui.Clients;

namespace Flowbit.Tests;

public sealed class UiSessionTransportTests
{
    [Theory]
    [InlineData(Clients.SessionHttpClientFactory.ApiClientName)]
    [InlineData(Clients.WorkflowApiClient.AiClientName)]
    public async Task Shared_transport_never_shares_users_or_replays_rejected_commands(string clientName)
    {
        using var transport = new RecordingTransport();
        var pool = new HandlerFactory(transport);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["WorkflowApi:BaseUrl"] = "https://api.example" }).Build();
        var alice = new Tokens("alice-first");
        var bob = new Tokens("bob-only");
        using var aliceClient = new Clients.SessionHttpClientFactory(pool, alice, configuration).CreateClient(clientName);
        using var bobClient = new Clients.SessionHttpClientFactory(pool, bob, configuration).CreateClient(clientName);

        await Task.WhenAll(Send(aliceClient, "/alice-first"), Send(bobClient, "/bob"));
        alice.Value = "alice-rotated";
        await Send(aliceClient, "/alice-rotated");
        aliceClient.Dispose();
        await Send(bobClient, "/bob-after-alice-disposed");

        Assert.Equal(4, transport.Requests.Count);
        Assert.Contains(("/alice-first", "alice-first"), transport.Requests);
        Assert.Contains(("/alice-rotated", "alice-rotated"), transport.Requests);
        Assert.Contains(("/bob", "bob-only"), transport.Requests);
        Assert.Contains(("/bob-after-alice-disposed", "bob-only"), transport.Requests);
        Assert.Equal(2, alice.Rejections);
        Assert.Equal(2, bob.Rejections);
        Assert.False(transport.Disposed);
        Assert.Empty(bobClient.DefaultRequestHeaders);
        Assert.Equal(clientName == Clients.WorkflowApiClient.AiClientName
            ? Clients.WorkflowApiClient.AiRequestTimeout : TimeSpan.FromMinutes(6), bobClient.Timeout);
    }

    private static async Task Send(HttpClient client, string path)
    {
        using var body = new StringContent("mutation");
        using var response = await client.PostAsync(path, body);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class Tokens(string value) : Auth.IUiAccessTokenProvider
    {
        public string Value = value;
        public int Rejections;
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(Value);
        public void RejectSession() => Interlocked.Increment(ref Rejections);
    }

    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name) => handler;
    }

    private sealed class RecordingTransport : HttpMessageHandler
    {
        public ConcurrentBag<(string Path, string Token)> Requests { get; } = [];
        public bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.False(Disposed);
            Requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization!.Parameter!));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
