// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Honua.Core.Features.Alerts.Domain;
using Honua.Alerts;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AwsSqsAlertDeliverySinkTests
{
    private static AlertDeliveryOptions CreateOptionsWithSqs(string queueUrl = "https://sqs.us-east-1.amazonaws.com/123456/test-queue") =>
        new()
        {
            Dispatch = new AlertDeliveryDispatchOptions
            {
                AwsSqs = new AwsSqsChannelOptions { QueueUrl = queueUrl }
            }
        };

    [UnitTest]
    public async Task DeliverAsync_WithNoQueueUrlConfigured_ReturnsNonRetryableFailure()
    {
        var publisher = new FakeSqsPublisher();
        var sink = new AwsSqsAlertDeliverySink(publisher, Options.Create(new AlertDeliveryOptions()), NullLogger<AwsSqsAlertDeliverySink>.Instance);

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSqs),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("not configured", result.Error, StringComparison.Ordinal);
    }

    [UnitTest]
    public async Task DeliverAsync_WithSuccessfulSend_ReturnsSuccess()
    {
        var publisher = new FakeSqsPublisher
        {
            SendMessageAsyncHandler = static (_, _, _, _) => Task.FromResult(new SqsPublishResult(true, false, null))
        };

        var sink = new AwsSqsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSqs()), NullLogger<AwsSqsAlertDeliverySink>.Instance);
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSqs),
            AlertTestFixtures.CreateAlertEvent());

        Assert.True(result.Succeeded);
        Assert.False(result.Retryable);
    }

    [UnitTest]
    public async Task DeliverAsync_WithServerError_ReturnsRetryableFailure()
    {
        var publisher = new FakeSqsPublisher
        {
            SendMessageAsyncHandler = static (_, _, _, _) => Task.FromResult(new SqsPublishResult(false, true, "SQS send responded with 500."))
        };

        var sink = new AwsSqsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSqs()), NullLogger<AwsSqsAlertDeliverySink>.Instance);
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSqs),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
    }

    [UnitTest]
    public async Task DeliverAsync_WithQueueNotFoundError_ReturnsNonRetryableFailure()
    {
        var publisher = new FakeSqsPublisher
        {
            SendMessageAsyncHandler = static (_, _, _, _) => Task.FromResult(new SqsPublishResult(false, false, "SQS queue not found: test"))
        };

        var sink = new AwsSqsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSqs()), NullLogger<AwsSqsAlertDeliverySink>.Instance);
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSqs),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("queue not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    public async Task DeliverAsync_WithDestinationOverride_UsesDispatchDestination()
    {
        var publisher = new FakeSqsPublisher();

        var overrideUrl = "https://sqs.us-east-1.amazonaws.com/123456/override-queue";
        var sink = new AwsSqsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSqs()), NullLogger<AwsSqsAlertDeliverySink>.Instance);

        await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSqs, destination: overrideUrl),
            AlertTestFixtures.CreateAlertEvent());

        Assert.Equal(overrideUrl, publisher.LastQueueUrl);
    }

    [UnitTest]
    public async Task DeliverAsync_IncludesMessageAttributes()
    {
        var publisher = new FakeSqsPublisher();

        var sink = new AwsSqsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSqs()), NullLogger<AwsSqsAlertDeliverySink>.Instance);
        await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSqs),
            AlertTestFixtures.CreateAlertEvent());

        Assert.NotNull(publisher.LastAttributes);
        Assert.True(publisher.LastAttributes.ContainsKey("X-Honua-Alert-Rule"));
        Assert.True(publisher.LastAttributes.ContainsKey("X-Honua-Alert-Event"));
        Assert.True(publisher.LastAttributes.ContainsKey("X-Honua-Trigger-Type"));
        Assert.True(publisher.LastAttributes.ContainsKey("X-Honua-Severity"));
    }

    [UnitTest]
    public void ChannelType_ReturnsAwsSqs()
    {
        var publisher = new FakeSqsPublisher();
        var sink = new AwsSqsAlertDeliverySink(publisher, Options.Create(new AlertDeliveryOptions()), NullLogger<AwsSqsAlertDeliverySink>.Instance);
        Assert.Equal(AlertChannelType.AwsSqs, sink.ChannelType);
    }

    [UnitTest]
    public async Task AwsSqsPublisher_SendMessageAsync_PopulatesMessageAttributesOnSdkV4Request()
    {
        using var client = new CapturingSqsClient();
        using var publisher = new AwsSqsPublisher(client);

        var result = await publisher.SendMessageAsync(
            "https://sqs.us-east-1.amazonaws.com/123456/ops-alerts",
            "{\"event\":\"test\"}",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["X-Honua-Alert-Rule"] = "ops-reminder",
                ["X-Honua-Severity"] = "Warning"
            },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(client.LastRequest);
        Assert.Equal("https://sqs.us-east-1.amazonaws.com/123456/ops-alerts", client.LastRequest.QueueUrl);
        Assert.NotNull(client.LastRequest.MessageAttributes);
        Assert.Equal("String", client.LastRequest.MessageAttributes["X-Honua-Alert-Rule"].DataType);
        Assert.Equal("ops-reminder", client.LastRequest.MessageAttributes["X-Honua-Alert-Rule"].StringValue);
        Assert.Equal("Warning", client.LastRequest.MessageAttributes["X-Honua-Severity"].StringValue);
    }

    private sealed class CapturingSqsClient() : AmazonSQSClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public SendMessageRequest? LastRequest { get; private set; }

        public override Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new SendMessageResponse { HttpStatusCode = HttpStatusCode.OK });
        }
    }
}
