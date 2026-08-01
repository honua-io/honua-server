// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Alerts;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// Covers the shared classification every alert delivery sink relies on: a destination that cannot
/// be vetted is always blocked, but only a permanently disallowed destination is non-retryable
/// (#3057). Every case injects a resolver, so no test touches live DNS (#3056).
/// </summary>
public sealed class AlertDestinationGuardTests
{
    [UnitTest]
    public async Task CheckAsync_WithTransientResolutionFailure_BlocksButClassifiesRetryable()
    {
        var guard = AlertTestFixtures.GuardWithUnavailableResolver();

        var check = await guard.CheckAsync(
            AlertTestFixtures.HostnameWebhookBaseUrl + "/hook",
            "Webhook destination",
            CancellationToken.None);

        check.IsAllowed.Should().BeFalse("the fail-closed posture still blocks a destination that cannot be vetted");
        check.Uri.Should().BeNull();
        check.Retryable.Should().BeTrue();
        check.Error.Should().StartWith("Webhook destination ");
        check.Error.Should().Contain("resolution", "the operator must not be told a resolver outage was a disallowed destination");
        check.Error.Should().NotContain("not allowed");
    }

    [UnitTest]
    public async Task CheckAsync_WithHostResolvingToPrivateAddress_ClassifiesNonRetryable()
    {
        var guard = AlertTestFixtures.GuardResolvingTo("10.0.0.5");

        var check = await guard.CheckAsync(
            AlertTestFixtures.HostnameWebhookBaseUrl + "/hook",
            "Webhook destination",
            CancellationToken.None);

        check.IsAllowed.Should().BeFalse();
        check.Retryable.Should().BeFalse();
        check.Error.Should().Contain("not allowed");
    }

    [UnitTest]
    public async Task CheckAsync_WithLoopbackDestination_ClassifiesNonRetryable()
    {
        var guard = AlertTestFixtures.GuardWithUnavailableResolver();

        var check = await guard.CheckAsync("https://localhost/hook", "Webhook destination", CancellationToken.None);

        check.IsAllowed.Should().BeFalse();
        check.Retryable.Should().BeFalse("loopback is rejected before any resolution is attempted");
    }

    [UnitTest]
    public async Task CheckAsync_WithNonHttpsScheme_ClassifiesNonRetryable()
    {
        var guard = AlertTestFixtures.GuardWithUnavailableResolver();

        var check = await guard.CheckAsync("http://alerts.example.test/hook", "Webhook destination", CancellationToken.None);

        check.IsAllowed.Should().BeFalse();
        check.Retryable.Should().BeFalse();
        check.Error.Should().Contain("HTTPS");
    }

    [UnitTest]
    public async Task CheckAsync_WithEmbeddedCredentials_ClassifiesNonRetryable()
    {
        var guard = AlertTestFixtures.GuardWithUnavailableResolver();

        var check = await guard.CheckAsync(
            "https://user:pass@alerts.example.test/hook",
            "Webhook destination",
            CancellationToken.None);

        check.IsAllowed.Should().BeFalse();
        check.Retryable.Should().BeFalse();
        check.Error.Should().Contain("credentials");
    }

    [UnitTest]
    public async Task CheckAsync_WithPublicResolvedAddress_AdmitsDestination()
    {
        var guard = AlertTestFixtures.GuardResolvingTo("8.8.8.8");

        var check = await guard.CheckAsync(
            AlertTestFixtures.HostnameWebhookBaseUrl + "/hook",
            "Webhook destination",
            CancellationToken.None);

        check.IsAllowed.Should().BeTrue();
        check.Uri!.Host.Should().Be("alerts.example.test");
        check.Error.Should().BeNull();
    }
}
