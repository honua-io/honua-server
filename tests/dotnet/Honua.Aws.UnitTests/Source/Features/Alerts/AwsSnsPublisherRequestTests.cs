// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Honua.Alerts;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Alerts;

// Builds the real SDK request: the sink tests substitute ISnsPublisher and never reach it.
public sealed class AwsSnsPublisherRequestTests
{
    private const string TopicArn = "arn:aws:sns:us-east-1:123456789012:ops";

    [UnitTest]
    public async Task PublishAsync_WithAttributes_SendsThemAsStringMessageAttributes()
    {
        using var client = new CapturingSnsClient();
        using var publisher = new AwsSnsPublisher(client);
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["honua.source"] = "ops-reminders",
            ["honua.severity"] = "Warning",
        };

        var result = await publisher.PublishAsync(TopicArn, "body", "subject", attributes, CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.IsType<PublishRequest>(client.Captured);
        Assert.Equal(TopicArn, request.TopicArn);
        Assert.Equal(2, request.MessageAttributes.Count);
        Assert.Equal("String", request.MessageAttributes["honua.severity"].DataType);
        Assert.Equal("Warning", request.MessageAttributes["honua.severity"].StringValue);
    }

    [UnitTest]
    public async Task PublishAsync_WithoutAttributes_Publishes()
    {
        using var client = new CapturingSnsClient();
        using var publisher = new AwsSnsPublisher(client);

        var result = await publisher.PublishAsync(TopicArn, "body", null, [], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(Assert.IsType<PublishRequest>(client.Captured).MessageAttributes);
    }

    private sealed class CapturingSnsClient() : AmazonSimpleNotificationServiceClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public PublishRequest? Captured { get; private set; }

        public override Task<PublishResponse> PublishAsync(PublishRequest request, CancellationToken cancellationToken = default)
        {
            Captured = request;
            return Task.FromResult(new PublishResponse { HttpStatusCode = HttpStatusCode.OK });
        }
    }
}
