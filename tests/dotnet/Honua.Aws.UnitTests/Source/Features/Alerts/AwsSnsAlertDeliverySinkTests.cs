// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Honua.Core.Features.Alerts.Domain;
using Honua.Alerts;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

public sealed class AwsSnsAlertDeliverySinkTests
{
    private static AlertDeliveryOptions CreateOptionsWithSns(string topicArn = "arn:aws:sns:us-east-1:123456:test-topic") =>
        new()
        {
            Dispatch = new AlertDeliveryDispatchOptions
            {
                AwsSns = new AwsSnsChannelOptions { TopicArn = topicArn }
            }
        };

    [UnitTest]
    public async Task DeliverAsync_WithNoTopicArnConfigured_ReturnsNonRetryableFailure()
    {
        var publisher = new FakeSnsPublisher();
        var sink = new AwsSnsAlertDeliverySink(publisher, Options.Create(new AlertDeliveryOptions()), NullLogger<AwsSnsAlertDeliverySink>.Instance);

        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSns),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("not configured", result.Error, StringComparison.Ordinal);
    }

    [UnitTest]
    public async Task DeliverAsync_WithSuccessfulPublish_ReturnsSuccess()
    {
        var publisher = new FakeSnsPublisher
        {
            PublishAsyncHandler = static (_, _, _, _, _) => Task.FromResult(new SnsPublishResult(true, false, null))
        };

        var sink = new AwsSnsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSns()), NullLogger<AwsSnsAlertDeliverySink>.Instance);
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSns),
            AlertTestFixtures.CreateAlertEvent());

        Assert.True(result.Succeeded);
        Assert.False(result.Retryable);
    }

    [UnitTest]
    public async Task DeliverAsync_WithServerError_ReturnsRetryableFailure()
    {
        var publisher = new FakeSnsPublisher
        {
            PublishAsyncHandler = static (_, _, _, _, _) => Task.FromResult(new SnsPublishResult(false, true, "SNS publish responded with 500."))
        };

        var sink = new AwsSnsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSns()), NullLogger<AwsSnsAlertDeliverySink>.Instance);
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSns),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
    }

    [UnitTest]
    public async Task DeliverAsync_WithAuthorizationError_ReturnsNonRetryableFailure()
    {
        var publisher = new FakeSnsPublisher
        {
            PublishAsyncHandler = static (_, _, _, _, _) => Task.FromResult(new SnsPublishResult(false, false, "SNS authorization failed: Access denied"))
        };

        var sink = new AwsSnsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSns()), NullLogger<AwsSnsAlertDeliverySink>.Instance);
        var result = await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSns),
            AlertTestFixtures.CreateAlertEvent());

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("authorization", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [UnitTest]
    public async Task DeliverAsync_WithDestinationOverride_UsesDispatchDestination()
    {
        var publisher = new FakeSnsPublisher();

        var overrideArn = "arn:aws:sns:us-east-1:123456:override-topic";
        var sink = new AwsSnsAlertDeliverySink(publisher, Options.Create(
            CreateOptionsWithSns("arn:aws:sns:us-east-1:123456:default-topic")), NullLogger<AwsSnsAlertDeliverySink>.Instance);

        await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSns, destination: overrideArn),
            AlertTestFixtures.CreateAlertEvent());

        Assert.Equal(overrideArn, publisher.LastTopicArn);
    }

    [UnitTest]
    public async Task DeliverAsync_IncludesMessageAttributes()
    {
        var publisher = new FakeSnsPublisher();

        var sink = new AwsSnsAlertDeliverySink(publisher, Options.Create(CreateOptionsWithSns()), NullLogger<AwsSnsAlertDeliverySink>.Instance);
        await sink.DeliverAsync(
            AlertTestFixtures.CreateDispatchItem(AlertChannelType.AwsSns),
            AlertTestFixtures.CreateAlertEvent());

        Assert.NotNull(publisher.LastAttributes);
        Assert.True(publisher.LastAttributes.ContainsKey("X-Honua-Alert-Rule"));
        Assert.True(publisher.LastAttributes.ContainsKey("X-Honua-Alert-Event"));
        Assert.True(publisher.LastAttributes.ContainsKey("X-Honua-Trigger-Type"));
        Assert.True(publisher.LastAttributes.ContainsKey("X-Honua-Severity"));
    }

    [UnitTest]
    public void ChannelType_ReturnsAwsSns()
    {
        var publisher = new FakeSnsPublisher();
        var sink = new AwsSnsAlertDeliverySink(publisher, Options.Create(new AlertDeliveryOptions()), NullLogger<AwsSnsAlertDeliverySink>.Instance);
        Assert.Equal(AlertChannelType.AwsSns, sink.ChannelType);
    }

    [UnitTest]
    public async Task AwsSnsPublisher_PublishAsync_PopulatesMessageAttributesOnSdkV4Request()
    {
        using var client = new CapturingSnsClient();
        using var publisher = new AwsSnsPublisher(client);

        var result = await publisher.PublishAsync(
            "arn:aws:sns:us-east-1:123456:ops-alerts",
            "{\"event\":\"test\"}",
            "Honua Alert",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["X-Honua-Alert-Rule"] = "ops-reminder",
                ["X-Honua-Severity"] = "Warning"
            },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(client.LastRequest);
        Assert.Equal("arn:aws:sns:us-east-1:123456:ops-alerts", client.LastRequest.TopicArn);
        Assert.NotNull(client.LastRequest.MessageAttributes);
        Assert.Equal("String", client.LastRequest.MessageAttributes["X-Honua-Alert-Rule"].DataType);
        Assert.Equal("ops-reminder", client.LastRequest.MessageAttributes["X-Honua-Alert-Rule"].StringValue);
        Assert.Equal("Warning", client.LastRequest.MessageAttributes["X-Honua-Severity"].StringValue);
    }

    private sealed class CapturingSnsClient() : AmazonSimpleNotificationServiceClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public PublishRequest? LastRequest { get; private set; }

        public override Task<PublishResponse> PublishAsync(PublishRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new PublishResponse { HttpStatusCode = HttpStatusCode.OK });
        }
    }
}
