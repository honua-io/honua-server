// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.Alerts;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class EmailAlertDeliverySinkTests
{
    [UnitTest]
    public async Task DeliverAsync_WithNoSmtpConfigured_ReturnsNonRetryableFailure()
    {
        var sink = new EmailAlertDeliverySink(Options.Create(new AlertDeliveryOptions()));

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Email),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("not configured", result.Error, StringComparison.Ordinal);
    }

    [UnitTest]
    public async Task DeliverAsync_WithNoRecipient_ReturnsNonRetryableFailure()
    {
        var options = new AlertDeliveryOptions
        {
            Dispatch = new AlertDeliveryDispatchOptions
            {
                Email = new EmailChannelOptions
                {
                    SmtpHost = "smtp.example.com",
                    FromAddress = "alerts@example.com"
                }
            }
        };

        var sink = new EmailAlertDeliverySink(Options.Create(options));
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Email),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("recipient", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    public async Task DeliverAsync_WithDispatchDestination_AttemptsDeliveryWithoutRecipientError()
    {
        var options = new AlertDeliveryOptions
        {
            Dispatch = new AlertDeliveryDispatchOptions
            {
                Email = new EmailChannelOptions
                {
                    // Use a non-routable host so SendMailAsync fails predictably.
                    SmtpHost = "192.0.2.1",
                    SmtpPort = 25,
                    FromAddress = "alerts@example.com",
                    UseSsl = false
                }
            }
        };

        var sink = new EmailAlertDeliverySink(Options.Create(options));
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.Email, destination: "user@example.com"),
            AlertTestFixtures.CreateAlertEvent());

        // The SMTP connection will fail, but the dispatch destination was accepted
        // as the recipient (not rejected for "recipient is not configured").
        Assert.False(result.Succeeded);
        Assert.DoesNotContain("recipient", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    public void ChannelType_ReturnsEmail()
    {
        var sink = new EmailAlertDeliverySink(Options.Create(new AlertDeliveryOptions()));
        Assert.Equal(AlertChannelType.Email, sink.ChannelType);
    }
}
