namespace Flowbit.BrowserTests.Infrastructure;

/// <summary>Explicit opt-in configuration; the parameterless smoke stack never uses a Worker.</summary>
public sealed record BrowserAcceptanceOptions
{
    public string? RepositoryRoot { get; init; }
    public string? ApiHostDirectory { get; init; }
    public string? UiHostDirectory { get; init; }
    public string? WorkerHostDirectory { get; init; }
    public string? ArtifactRoot { get; init; }
    public bool? Headed { get; init; }
    public bool StartEditorHost { get; init; }
    public bool LegacyUiWithoutInteractiveMarkers { get; init; }
    public IReadOnlyList<string> AuditClaims { get; init; } = ["department", "region"];
    /// <summary>Test-owned config.* values supplied identically to API and Worker.</summary>
    public IReadOnlyDictionary<string, string> WorkflowContextConfig { get; init; } =
        new Dictionary<string, string>();
    public TimeSpan ScenarioTimeout { get; init; } = TimeSpan.FromMinutes(5);
}
