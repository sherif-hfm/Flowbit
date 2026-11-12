namespace Flowbit.Service.Services;

/// <summary>Temporary integration switch. Production and other non-Development hosts always ignore it.</summary>
public sealed class AutomaticMultiInstanceOptions
{
    public const string SectionName = "WorkflowAutomaticMultiInstance";

    public bool AllowInDevelopment { get; set; }

    public bool Enabled { get; private set; }

    public void ResolveForEnvironment(bool isDevelopment) => Enabled = isDevelopment && AllowInDevelopment;
}
