namespace Flowbit.Service.Services;

/// <summary>
/// The caller may not perform the request (403). <see cref="Code"/> is a stable machine-readable
/// code that the response carries beside the message, when the refusal has one.
/// </summary>
public sealed class WorkflowForbiddenException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}
