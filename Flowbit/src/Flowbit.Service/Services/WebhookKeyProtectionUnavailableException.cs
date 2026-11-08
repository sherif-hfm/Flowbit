namespace Flowbit.Service.Services;

/// <summary>
/// The webhook key ring's certificate is missing or unusable, so subscriber secrets cannot be created, rotated, or
/// used. The API answers 503; Flowbit never generates replacement secrets or falls back to another key store.
/// </summary>
public sealed class WebhookKeyProtectionUnavailableException(string message) : Exception(message);
