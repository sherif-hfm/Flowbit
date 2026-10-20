using System.Text;

namespace Flowbit.Service.Ai;

/// <summary>
/// A configured AI model with its provider key, resolved on the server for one authoring run. Never return it to a caller or log
/// its key; its printed form leaves the key out.
/// </summary>
public sealed record AiModelConnection(string Id, string Name, string Endpoint, string Model, string? ReasoningEffort, string ApiKey)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Id = ").Append(Id).Append(", Name = ").Append(Name).Append(", Endpoint = ").Append(Endpoint)
            .Append(", Model = ").Append(Model).Append(", ReasoningEffort = ").Append(ReasoningEffort);
        return true;
    }
}

/// <summary>The configured AI models with their keys, for server-side authoring only.</summary>
public interface IAiModelStore
{
    /// <summary>
    /// The configured models in order; the first is the assistant's default. Throws <see cref="WorkflowAiException"/>
    /// provider_configuration (503) when the stored list is invalid.
    /// </summary>
    Task<IReadOnlyList<AiModelConnection>> GetConnectionsAsync(CancellationToken cancellationToken);
}

/// <summary>A fixed model list for tools and tests that run the authoring service without a database.</summary>
public sealed class FixedAiModelStore(params AiModelConnection[] models) : IAiModelStore
{
    public Task<IReadOnlyList<AiModelConnection>> GetConnectionsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AiModelConnection>>(models);
    }
}
