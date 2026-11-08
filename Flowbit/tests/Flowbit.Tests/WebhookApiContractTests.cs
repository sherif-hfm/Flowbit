using System.Reflection;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Xunit;

namespace Flowbit.Tests;

/// <summary>Webhook API contract rules that need no database: confidential string forms and failure codes.</summary>
public sealed class WebhookApiContractTests
{
    private const string EndpointUrl = "https://receiver.example.com/flowbit/events?token=receiver-token";
    private const string Secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";

    [Fact]
    public void DtosHoldingAUrlOrSecretPrintOnlyTheirTypeName()
    {
        var now = DateTimeOffset.UtcNow;
        var destination = new WebhookDestinationDto(12, 1, EndpointUrl, "https://receiver.example.com:443",
            [LifecycleEventTypes.TaskCreated], "purchase-review", now, "admin");
        var revision = new WebhookDestinationRevisionDto(destination, true, 0);
        var subscription = new WebhookSubscriptionDto(Guid.CreateVersion7(), "CRM feed", null,
            WebhookSubscriptionStates.Paused, WebhookPauseReasons.Administrator, 4, 1, destination, [],
            new WebhookReceiverHealthDto(0, null, null, null, null, null, null, null),
            new WebhookQueueCountsDto(0, 0, 0, 0), now, "admin", now, "admin", null, null, null);
        var delivery = new WebhookDeliveryDto(Guid.CreateVersion7(), Guid.CreateVersion7(), subscription.Id,
            subscription.Name, 12, 1, destination.Origin, LifecycleEventTypes.TaskCreated, false, "purchase-review", 7,
            now, WebhookDeliveryStates.Failed, null, 1, 1, 1, WebhookOutcomeCodes.ReceiverRejected, 400,
            WebhookOutcomeCodes.ReceiverRejected, now, null, 3, now, now, true, true);
        var secret = new WebhookSecretResultDto(subscription, Guid.CreateVersion7(), Secret);
        object[] confidential =
        [
            destination,
            revision,
            subscription,
            new WebhookSubscriptionDetailDto(subscription, [revision]),
            new CreateWebhookSubscriptionRequest("CRM feed", null, EndpointUrl, [LifecycleEventTypes.TaskCreated], null, null),
            new UpdateWebhookSubscriptionRequest("CRM feed", null, EndpointUrl, [LifecycleEventTypes.TaskCreated], null, null, 1),
            secret,
            new WebhookDeliveryDetailDto(delivery, EndpointUrl, "{}", WebhookSubscriptionStates.Active, null, null, 12, true, true)
        ];
        foreach (var value in confidential)
        {
            Assert.Equal(value.GetType().Name, value.ToString());
        }

        // Interpolation and the objects that wrap these DTOs, including pages, print the same safe form.
        var wrapped = $"{new { Result = secret, Page = new PagedResult<WebhookSubscriptionDto>([subscription], 1, 50, 1) }}";
        foreach (var text in new[] { wrapped, string.Join(' ', confidential) })
        {
            Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain("/flowbit/events", text, StringComparison.Ordinal);
            Assert.DoesNotContain("token", text, StringComparison.Ordinal);
        }
        // A list delivery carries only the origin, which is safe to log, so it keeps its generated form.
        Assert.Contains(destination.Origin, delivery.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("token", delivery.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FailureCodesAreExactlyTheCodesAFailedDeliveryCanCarry()
    {
        // abortedBeforeSend closes an attempt released before sending; it never settles a delivery.
        var outcomes = typeof(WebhookOutcomeCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(code => code != WebhookOutcomeCodes.AbortedBeforeSend)
            .ToArray();
        var now = DateTimeOffset.UnixEpoch;
        var budgets = new (int Attempts, DateTimeOffset Deadline, TimeSpan? RetryAfter)[]
        {
            (1, now + WebhookConstraints.GenerationWindow, null),
            (WebhookConstraints.MaxAttemptsPerGeneration, now + WebhookConstraints.GenerationWindow, null),
            (1, now.AddSeconds(1), null),
            (1, now.AddSeconds(1), TimeSpan.FromMinutes(5))
        };
        var produced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var outcome in outcomes)
        {
            foreach (var (attempts, deadline, retryAfter) in budgets)
            {
                var decision = WebhookRetryPolicy.Decide(outcome, attempts, deadline, retryAfter, now, 1.0);
                if (decision.State == WebhookDeliveryStates.Failed)
                {
                    produced.Add(decision.FailureCode!);
                }
            }
        }

        // The delivery-list and bulk filters accept exactly these, so no valid filter is refused.
        Assert.Equal(
            WebhookOutcomeCodes.FailureCodes.Order(StringComparer.Ordinal),
            produced.Order(StringComparer.Ordinal));
        Assert.Equal(WebhookOutcomeCodes.FailureCodes.Count, WebhookOutcomeCodes.FailureCodes.Distinct().Count());
    }
}
