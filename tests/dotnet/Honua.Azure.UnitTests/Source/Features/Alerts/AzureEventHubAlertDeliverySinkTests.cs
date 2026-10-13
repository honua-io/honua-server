// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;
using Honua.Alerts;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AzureEventHubAlertDeliverySinkTests
{
    private static AlertDeliveryOptions CreateOptionsWithEventHub() =>
        new()
        {
            Dispatch = new AlertDeliveryDispatchOptions
            {
                AzureEventHub = new AzureEventHubChannelOptions
                {
                    ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=xxx",
                    EventHubName = "test-hub"
                }
            }
        };

    [UnitTest]
    public async Task DeliverAsync_WithNoConnectionStringConfigured_ReturnsNonRetryableFailure()
    {
        var publisher = new FakeEventHubPublisher();
        var sink = new AzureEventHubAlertDeliverySink(
            publisher,
            Options.Create(new AlertDeliveryOptions()),
            NullLogger<AzureEventHubAlertDeliverySink>.Instance);

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AzureEventHub),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("not configured", result.Error, StringComparison.Ordinal);
    }

    [UnitTest]
    public async Task DeliverAsync_WithSuccessfulSend_ReturnsSuccess()
    {
        var publisher = new FakeEventHubPublisher
        {
            SendAsyncHandler = static (_, _, _) => Task.FromResult(new EventHubPublishResult(true, false, null))
        };

        var sink = new AzureEventHubAlertDeliverySink(
            publisher,
            Options.Create(CreateOptionsWithEventHub()),
            NullLogger<AzureEventHubAlertDeliverySink>.Instance);
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AzureEventHub),
            AlertTestFixtures.CreateAlertEvent());

        Assert.True(result.Succeeded);
        Assert.False(result.Retryable);
    }

    [UnitTest]
    public async Task DeliverAsync_WithTransientError_ReturnsRetryableFailure()
    {
        var publisher = new FakeEventHubPublisher
        {
            SendAsyncHandler = static (_, _, _) => Task.FromResult(new EventHubPublishResult(false, true, "Event Hub transient error: test"))
        };

        var sink = new AzureEventHubAlertDeliverySink(
            publisher,
            Options.Create(CreateOptionsWithEventHub()),
            NullLogger<AzureEventHubAlertDeliverySink>.Instance);
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AzureEventHub),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
    }

    [UnitTest]
    public async Task DeliverAsync_WithUnhandledException_LogsAndReturnsRetryableFailure()
    {
        var publisher = new FakeEventHubPublisher
        {
            SendAsyncHandler = static (_, _, _) => throw new InvalidOperationException("boom")
        };
        var logger = new CapturingLogger<AzureEventHubAlertDeliverySink>();

        var sink = new AzureEventHubAlertDeliverySink(
            publisher,
            Options.Create(CreateOptionsWithEventHub()),
            logger);

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AzureEventHub),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.LogLevel);
        Assert.IsType<InvalidOperationException>(entry.Exception);
        Assert.Equal("boom", entry.Exception!.Message);
    }

    [UnitTest]
    public async Task DeliverAsync_WithResourceNotFound_ReturnsNonRetryableFailure()
    {
        var publisher = new FakeEventHubPublisher
        {
            SendAsyncHandler = static (_, _, _) => Task.FromResult(new EventHubPublishResult(false, false, "Event Hub not found: test"))
        };

        var sink = new AzureEventHubAlertDeliverySink(
            publisher,
            Options.Create(CreateOptionsWithEventHub()),
            NullLogger<AzureEventHubAlertDeliverySink>.Instance);
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AzureEventHub),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    public async Task DeliverAsync_IncludesEventProperties()
    {
        var publisher = new FakeEventHubPublisher();

        var sink = new AzureEventHubAlertDeliverySink(
            publisher,
            Options.Create(CreateOptionsWithEventHub()),
            NullLogger<AzureEventHubAlertDeliverySink>.Instance);
        await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AzureEventHub),
            AlertTestFixtures.CreateAlertEvent());

        Assert.NotNull(publisher.LastProperties);
        Assert.True(publisher.LastProperties.ContainsKey("X-Honua-Alert-Rule"));
        Assert.True(publisher.LastProperties.ContainsKey("X-Honua-Alert-Event"));
        Assert.True(publisher.LastProperties.ContainsKey("X-Honua-Trigger-Type"));
        Assert.True(publisher.LastProperties.ContainsKey("X-Honua-Severity"));
        Assert.True(publisher.LastProperties.ContainsKey("X-Honua-Incident-Status"));
    }

    [UnitTest]
    public void ChannelType_ReturnsAzureEventHub()
    {
        var publisher = new FakeEventHubPublisher();
        var sink = new AzureEventHubAlertDeliverySink(
            publisher,
            Options.Create(new AlertDeliveryOptions()),
            NullLogger<AzureEventHubAlertDeliverySink>.Instance);
        Assert.Equal(AlertChannelType.AzureEventHub, sink.ChannelType);
    }
}

/// <summary>
/// Captures log entries emitted to it for assertions; used to verify the
/// previously-silent unhandled-exception path (PA-062) now emits a warning
/// with the original exception attached.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel LogLevel, EventId EventId, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, eventId, formatter(state, exception), exception));
    }
}
