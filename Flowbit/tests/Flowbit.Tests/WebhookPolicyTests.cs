using System.Net;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Xunit;

namespace Flowbit.Tests;

public sealed class WebhookDestinationPolicyTests
{
    private static readonly Uri PublicDestination = new("https://receiver.example.com/hook");

    [Theory]
    [InlineData("127.0.0.1", WebhookAddressClass.Loopback)]
    [InlineData("10.1.2.3", WebhookAddressClass.Private)]
    [InlineData("172.16.0.1", WebhookAddressClass.Private)]
    [InlineData("172.31.255.255", WebhookAddressClass.Private)]
    [InlineData("172.32.0.1", WebhookAddressClass.Public)]
    [InlineData("192.168.1.1", WebhookAddressClass.Private)]
    [InlineData("100.64.0.1", WebhookAddressClass.Private)]
    [InlineData("169.254.169.254", WebhookAddressClass.Forbidden)]
    [InlineData("0.0.0.0", WebhookAddressClass.Forbidden)]
    [InlineData("224.0.0.1", WebhookAddressClass.Forbidden)]
    [InlineData("255.255.255.255", WebhookAddressClass.Forbidden)]
    [InlineData("203.0.113.9", WebhookAddressClass.Forbidden)]
    [InlineData("8.8.8.8", WebhookAddressClass.Public)]
    [InlineData("::1", WebhookAddressClass.Loopback)]
    [InlineData("::", WebhookAddressClass.Forbidden)]
    [InlineData("fe80::1", WebhookAddressClass.Forbidden)]
    [InlineData("fd12:3456::1", WebhookAddressClass.Private)]
    [InlineData("fd00:ec2::254", WebhookAddressClass.Forbidden)]
    [InlineData("ff02::1", WebhookAddressClass.Forbidden)]
    [InlineData("2001:db8::1", WebhookAddressClass.Forbidden)]
    [InlineData("2001:0:4136:e378::1", WebhookAddressClass.Forbidden)]
    [InlineData("2606:4700:4700::1111", WebhookAddressClass.Public)]
    // Mapped and alternate IPv6 spellings of IPv4 addresses are classified as the address they carry.
    [InlineData("::ffff:127.0.0.1", WebhookAddressClass.Loopback)]
    [InlineData("::ffff:10.0.0.1", WebhookAddressClass.Private)]
    [InlineData("::ffff:169.254.169.254", WebhookAddressClass.Forbidden)]
    [InlineData("::127.0.0.1", WebhookAddressClass.Loopback)]
    [InlineData("64:ff9b::a00:1", WebhookAddressClass.Private)]
    [InlineData("2002:7f00:1::1", WebhookAddressClass.Loopback)]
    [InlineData("::ffff:8.8.8.8", WebhookAddressClass.Public)]
    public void ClassifiesAddressesIncludingMappedForms(string address, WebhookAddressClass expected) =>
        Assert.Equal(expected, WebhookDestinationPolicy.Classify(IPAddress.Parse(address)));

    [Theory]
    [InlineData("https://receiver.example.com/hook", true)]
    [InlineData("https://receiver.example.com/hook?token=abc", true)]
    [InlineData("https://receiver.example.com:443/hook", true)]
    [InlineData("https://8.8.8.8/hook", true)]
    [InlineData("http://receiver.example.com/hook", false)]
    [InlineData("https://receiver.example.com:8443/hook", false)]
    [InlineData("https://user@receiver.example.com/hook", false)]
    [InlineData("https://receiver.example.com/hook#part", false)]
    [InlineData("https://127.0.0.1/hook", false)]
    [InlineData("https://2130706433/hook", false)]
    [InlineData("https://0x7f.1/hook", false)]
    [InlineData("https://[::1]/hook", false)]
    [InlineData("https://localhost/hook", false)]
    [InlineData("https://api.localhost/hook", false)]
    [InlineData("https://192.168.0.10/hook", false)]
    [InlineData(" https://receiver.example.com/hook", false)]
    [InlineData("ftp://receiver.example.com/hook", false)]
    public void ValidatesUrlsAtSaveTime(string url, bool valid)
    {
        var policy = new WebhookDestinationPolicy(new WebhookNetworkOptions());
        Assert.Equal(valid, policy.TryValidateUrl(url, out _, out var error));
        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void RejectsOverlongUrls()
    {
        var policy = new WebhookDestinationPolicy(new WebhookNetworkOptions());
        var url = "https://receiver.example.com/" + new string('a', WebhookConstraints.MaxEndpointUrlLength);
        Assert.False(policy.TryValidateUrl(url, out _, out var error));
        Assert.Contains("2048", error, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorAllowlistsAdmitNamedPortsPrivateReceiversAndRanges()
    {
        var policy = new WebhookDestinationPolicy(new WebhookNetworkOptions
        {
            AllowedHosts = ["hooks.internal.example:8443", "crm.internal.example"],
            AllowedCidrs = ["198.18.0.0/15"]
        });
        Assert.True(policy.TryValidateUrl("https://hooks.internal.example:8443/in", out var named, out _));
        Assert.False(policy.TryValidateUrl("https://other.internal.example:8443/in", out _, out _));
        Assert.True(policy.IsAddressAllowed(IPAddress.Parse("10.2.3.4"), named!));
        Assert.True(policy.IsAddressAllowed(IPAddress.Parse("10.2.3.4"), new Uri("https://crm.internal.example/in")));
        Assert.False(policy.IsAddressAllowed(IPAddress.Parse("10.2.3.4"), PublicDestination));
        // An allowlisted host still never reaches link-local metadata or loopback.
        Assert.False(policy.IsAddressAllowed(IPAddress.Parse("169.254.169.254"), named!));
        Assert.False(policy.IsAddressAllowed(IPAddress.Parse("127.0.0.1"), named!));
        Assert.True(policy.IsAddressAllowed(IPAddress.Parse("198.18.4.4"), PublicDestination));
        Assert.True(policy.IsAddressAllowed(IPAddress.Parse("::ffff:198.18.4.4"), PublicDestination));
    }

    [Fact]
    public void DevelopmentLoopbackExceptionAppliesOnlyToLoopback()
    {
        var policy = new WebhookDestinationPolicy(new WebhookNetworkOptions { AllowLoopbackHttp = true });
        Assert.True(policy.TryValidateUrl("http://127.0.0.1:5099/hook", out var uri, out _));
        Assert.True(policy.IsAddressAllowed(IPAddress.Loopback, uri!));
        Assert.False(policy.TryValidateUrl("http://receiver.example.com/hook", out _, out _));
        Assert.False(policy.IsAddressAllowed(IPAddress.Loopback, PublicDestination));
        Assert.Throws<InvalidOperationException>(() => new WebhookOptions
        {
            Network = new WebhookNetworkOptions { AllowLoopbackHttp = true }
        }.Validate(isDevelopment: false));
        new WebhookOptions { Network = new WebhookNetworkOptions { AllowLoopbackHttp = true } }.Validate(isDevelopment: true);
    }

    [Fact]
    public void InvalidOperatorEntriesFailStartup()
    {
        Assert.Throws<InvalidOperationException>(() => new WebhookDestinationPolicy(new WebhookNetworkOptions { AllowedCidrs = ["10.0.0.0/33"] }));
        Assert.Throws<InvalidOperationException>(() => new WebhookDestinationPolicy(new WebhookNetworkOptions { AllowedHosts = ["https://x/y"] }));
    }

    [Fact]
    public void OriginsKeepOnlySchemeHostAndPort() =>
        Assert.Equal(
            "https://receiver.example.com:443",
            WebhookDestinationPolicy.Origin(new Uri("https://receiver.example.com/secret/path?token=abc")));
}

public sealed class WebhookRetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(200, WebhookOutcomeCodes.Succeeded)]
    [InlineData(202, WebhookOutcomeCodes.Succeeded)]
    [InlineData(204, WebhookOutcomeCodes.Succeeded)]
    [InlineData(301, WebhookOutcomeCodes.RedirectRefused)]
    [InlineData(308, WebhookOutcomeCodes.RedirectRefused)]
    [InlineData(400, WebhookOutcomeCodes.ReceiverRejected)]
    [InlineData(401, WebhookOutcomeCodes.ReceiverRejected)]
    [InlineData(404, WebhookOutcomeCodes.ReceiverRejected)]
    [InlineData(408, WebhookOutcomeCodes.HttpRetryable)]
    [InlineData(410, WebhookOutcomeCodes.ReceiverGone)]
    [InlineData(425, WebhookOutcomeCodes.HttpRetryable)]
    [InlineData(429, WebhookOutcomeCodes.HttpRetryable)]
    [InlineData(500, WebhookOutcomeCodes.HttpRetryable)]
    [InlineData(503, WebhookOutcomeCodes.HttpRetryable)]
    public void ClassifiesEveryHttpClass(int status, string expected) =>
        Assert.Equal(expected, WebhookRetryPolicy.ClassifyStatus(status));

    [Fact]
    public void TwelveSendsWithMaximumJitterFitInsideTheWindow()
    {
        var total = TimeSpan.Zero;
        for (var retry = 1; retry < WebhookConstraints.MaxAttemptsPerGeneration; retry++)
        {
            total += WebhookRetryPolicy.RetryDelay(retry) * 1.1;
        }
        Assert.True(total < WebhookConstraints.GenerationWindow, $"Delays total {total}.");
        Assert.True(total > TimeSpan.FromHours(69));
    }

    [Fact]
    public void RetryableOutcomesScheduleWithinTheDeadlineAndExhaustAtTwelve()
    {
        var deadline = Now + WebhookConstraints.GenerationWindow;
        var first = WebhookRetryPolicy.Decide(WebhookOutcomeCodes.Timeout, 1, deadline, null, Now, 1.0);
        Assert.Equal(WebhookDeliveryStates.RetryScheduled, first.State);
        Assert.Equal(Now + TimeSpan.FromSeconds(10), first.DueAt);
        var jittered = WebhookRetryPolicy.Decide(WebhookOutcomeCodes.NetworkError, 2, deadline, null, Now, 0.9);
        Assert.Equal(Now + TimeSpan.FromSeconds(54), jittered.DueAt);
        var last = WebhookRetryPolicy.Decide(WebhookOutcomeCodes.HttpRetryable, 12, deadline, null, Now, 1.0);
        Assert.Equal(WebhookDeliveryStates.Failed, last.State);
        Assert.Equal(WebhookOutcomeCodes.AttemptsExhausted, last.FailureCode);
        var late = WebhookRetryPolicy.Decide(WebhookOutcomeCodes.HttpRetryable, 3, Now.AddMinutes(1), null, Now, 1.0);
        Assert.Equal(WebhookOutcomeCodes.DeadlineExceeded, late.FailureCode);
        // An unknown outcome is retried within budget too.
        Assert.Equal(WebhookDeliveryStates.RetryScheduled,
            WebhookRetryPolicy.Decide(WebhookOutcomeCodes.OutcomeUnknown, 1, deadline, null, Now, 1.0).State);
    }

    [Fact]
    public void RetryAfterIsALowerBoundCappedAtADayAndFailsBeyondTheDeadline()
    {
        var deadline = Now + WebhookConstraints.GenerationWindow;
        var hinted = WebhookRetryPolicy.Decide(WebhookOutcomeCodes.HttpRetryable, 1, deadline, TimeSpan.FromMinutes(7), Now, 1.0);
        Assert.Equal(Now + TimeSpan.FromMinutes(7), hinted.DueAt);
        var smaller = WebhookRetryPolicy.Decide(WebhookOutcomeCodes.HttpRetryable, 4, deadline, TimeSpan.FromSeconds(1), Now, 1.0);
        Assert.Equal(Now + TimeSpan.FromMinutes(30), smaller.DueAt);
        var capped = WebhookRetryPolicy.Decide(WebhookOutcomeCodes.HttpRetryable, 1, deadline, TimeSpan.FromDays(3), Now, 1.0);
        Assert.Equal(Now + TimeSpan.FromHours(24), capped.DueAt);
        var beyond = WebhookRetryPolicy.Decide(WebhookOutcomeCodes.HttpRetryable, 1, Now.AddHours(1), TimeSpan.FromHours(2), Now, 1.0);
        Assert.Equal(WebhookOutcomeCodes.RetryAfterBeyondDeadline, beyond.FailureCode);
    }

    [Theory]
    [InlineData(WebhookOutcomeCodes.ReceiverRejected)]
    [InlineData(WebhookOutcomeCodes.ReceiverGone)]
    [InlineData(WebhookOutcomeCodes.RedirectRefused)]
    [InlineData(WebhookOutcomeCodes.DestinationDenied)]
    [InlineData(WebhookOutcomeCodes.TlsError)]
    [InlineData(WebhookOutcomeCodes.SigningUnavailable)]
    public void PermanentOutcomesFailImmediately(string outcome)
    {
        var decision = WebhookRetryPolicy.Decide(outcome, 1, Now.AddHours(72), null, Now, 1.0);
        Assert.Equal(WebhookDeliveryStates.Failed, decision.State);
        Assert.Equal(outcome, decision.FailureCode);
    }

    [Fact]
    public void BackoffStartsAfterFiveFailuresDoublesOnProbesToACapAndPausesAfterSeventyTwoHours()
    {
        var state = new WebhookReceiverState(0, null, null, 0, false);
        for (var failure = 1; failure < WebhookConstraints.BackoffFailureThreshold; failure++)
        {
            state = WebhookRetryPolicy.NextReceiverState(state, WebhookOutcomeCodes.Timeout, null, Now, Now);
            Assert.Null(state.BackoffUntil);
        }
        state = WebhookRetryPolicy.NextReceiverState(state, WebhookOutcomeCodes.Timeout, null, Now, Now);
        Assert.Equal(Now, state.BackoffStartedAt);
        Assert.Equal(Now.AddSeconds(30), state.BackoffUntil);

        // Each probe goes out when the backoff ends and fails; the backoff doubles to its one-hour cap.
        var (probe, failedAt) = Probe(state, WebhookOutcomeCodes.HttpRetryable);
        Assert.Equal(60, probe.BackoffSeconds);
        while (probe.BackoffSeconds < 3600)
        {
            (probe, failedAt) = Probe(probe, WebhookOutcomeCodes.NetworkError);
        }
        Assert.False(probe.AutomaticallyPause);
        // Hourly probes that keep failing pause the subscription once 72 hours have passed since backoff began.
        while (!probe.AutomaticallyPause)
        {
            Assert.True(failedAt - Now < TimeSpan.FromHours(72));
            (probe, failedAt) = Probe(probe, WebhookOutcomeCodes.Timeout);
        }
        Assert.InRange(failedAt - Now, TimeSpan.FromHours(72), TimeSpan.FromHours(73));

        // Permanent rejections and unknown outcomes are neutral; a success ends backoff.
        Assert.Equal(probe.BackoffUntil, WebhookRetryPolicy.NextReceiverState(probe, WebhookOutcomeCodes.ReceiverRejected, null, failedAt, failedAt).BackoffUntil);
        Assert.Equal(probe.ConsecutiveFailures, WebhookRetryPolicy.NextReceiverState(probe, WebhookOutcomeCodes.OutcomeUnknown, null, failedAt, failedAt).ConsecutiveFailures);
        var healthy = WebhookRetryPolicy.NextReceiverState(probe, WebhookOutcomeCodes.Succeeded, null, failedAt, failedAt);
        Assert.Equal(new WebhookReceiverState(0, null, null, 0, false), healthy);
    }

    [Fact]
    public void FailuresOfRequestsAlreadyInFlightCountWithoutExtendingBackoff()
    {
        // Four requests are in flight when the fifth failure starts backoff.
        var sentBeforeBackoff = Now.AddSeconds(-5);
        var state = WebhookRetryPolicy.NextReceiverState(
            new WebhookReceiverState(4, null, null, 0, false), WebhookOutcomeCodes.HttpRetryable, null, Now, sentBeforeBackoff);
        Assert.Equal(Now.AddSeconds(30), state.BackoffUntil);
        for (var index = 1; index <= 3; index++)
        {
            state = WebhookRetryPolicy.NextReceiverState(
                state, WebhookOutcomeCodes.HttpRetryable, null, Now.AddSeconds(index), sentBeforeBackoff);
        }
        Assert.Equal(8, state.ConsecutiveFailures);
        Assert.Equal(30, state.BackoffSeconds);
        Assert.Equal(Now.AddSeconds(30), state.BackoffUntil);

        // A Retry-After hint from one of them still holds the queue at least that long.
        var hinted = WebhookRetryPolicy.NextReceiverState(
            state, WebhookOutcomeCodes.HttpRetryable, TimeSpan.FromMinutes(10), Now.AddSeconds(4), sentBeforeBackoff);
        Assert.Equal(30, hinted.BackoffSeconds);
        Assert.Equal(Now.AddSeconds(4).AddMinutes(10), hinted.BackoffUntil);

        // The probe sent when backoff ends is the first failure that doubles it.
        var probe = WebhookRetryPolicy.NextReceiverState(
            state, WebhookOutcomeCodes.Timeout, null, Now.AddSeconds(45), Now.AddSeconds(31));
        Assert.Equal(60, probe.BackoffSeconds);
        Assert.Equal(Now.AddSeconds(105), probe.BackoffUntil);
    }

    [Fact]
    public void ABackoffThatLapsedWithoutProbesStartsAgainInsteadOfPausing()
    {
        // The queue emptied half an hour into a backoff that began four days ago; nothing probed since.
        var lapsed = new WebhookReceiverState(9, Now, Now.AddMinutes(30), 1800, false);
        var later = Now.AddDays(4);
        var state = WebhookRetryPolicy.NextReceiverState(lapsed, WebhookOutcomeCodes.Timeout, null, later, later.AddSeconds(-1));
        Assert.False(state.AutomaticallyPause);
        Assert.Equal(later, state.BackoffStartedAt);
        Assert.Equal(30, state.BackoffSeconds);
        Assert.Equal(later.AddSeconds(30), state.BackoffUntil);

        // A probe that failed within the hour after the window ended is still part of the same backoff.
        var continuous = WebhookRetryPolicy.NextReceiverState(
            lapsed, WebhookOutcomeCodes.Timeout, null, Now.AddMinutes(80), Now.AddMinutes(79));
        Assert.Equal(Now, continuous.BackoffStartedAt);
        Assert.Equal(3600, continuous.BackoffSeconds);
    }

    [Fact]
    public void ARetryAfterHintStartsBackoffImmediately()
    {
        var state = WebhookRetryPolicy.NextReceiverState(
            new WebhookReceiverState(0, null, null, 0, false),
            WebhookOutcomeCodes.HttpRetryable,
            TimeSpan.FromMinutes(10),
            Now,
            Now);
        Assert.Equal(Now, state.BackoffStartedAt);
        Assert.Equal(Now.AddMinutes(10), state.BackoffUntil);
    }

    [Fact]
    public void TheDefaultWebhookPoolGivesEverySendAConnectionBesideTheReserve()
    {
        var options = new WebhookOptions();
        Assert.Equal(options.MaxConcurrentSends + WebhookOptions.ReservedConnections, options.RecommendedConnectionPoolSize);
        Assert.True(options.ConnectionPoolSize >= options.RecommendedConnectionPoolSize);
        options.Validate(isDevelopment: false);
    }

    // A probe sent when the backoff window ends, failing ten seconds later.
    private static (WebhookReceiverState State, DateTimeOffset FailedAt) Probe(WebhookReceiverState state, string outcome)
    {
        var sentAt = state.BackoffUntil!.Value;
        var failedAt = sentAt.AddSeconds(10);
        return (WebhookRetryPolicy.NextReceiverState(state, outcome, null, failedAt, sentAt), failedAt);
    }

    [Theory]
    [InlineData("120", 120)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    [InlineData("soon", null)]
    [InlineData("", null)]
    [InlineData("999999", 86400)]
    [InlineData("Sat, 07 Nov 2026 09:05:00 GMT", 300)]
    [InlineData("Sat, 07 Nov 2026 08:00:00 GMT", null)]
    public void ParsesRetryAfterDelaysAndDates(string value, int? expectedSeconds) =>
        Assert.Equal(
            expectedSeconds is null ? null : TimeSpan.FromSeconds(expectedSeconds.Value),
            WebhookRetryPolicy.ParseRetryAfter(value, Now));
}
