// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Honua.Alerts;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Alerts;

// Builds the real SDK request: the sink tests substitute ISqsPublisher and never reach it.
public sealed class AwsSqsPublisherRequestTests
{
    private const string QueueUrl = "https://sqs.us-east-1.amazonaws.com/123456789012/ops";

    [UnitTest]
    public async Task SendMessageAsync_WithAttributes_SendsThemAsStringMessageAttributes()
    {
        using var client = new CapturingSqsClient();
        using var publisher = new AwsSqsPublisher(client);
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["honua.source"] = "ops-reminders",
            ["honua.severity"] = "Warning",
        };

        var result = await publisher.SendMessageAsync(QueueUrl, "body", attributes, CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.IsType<SendMessageRequest>(client.Captured);
        Assert.Equal(QueueUrl, request.QueueUrl);
        Assert.Equal(2, request.MessageAttributes.Count);
        Assert.Equal("String", request.MessageAttributes["honua.severity"].DataType);
        Assert.Equal("Warning", request.MessageAttributes["honua.severity"].StringValue);
    }

    [UnitTest]
    public async Task SendMessageAsync_WithoutAttributes_Sends()
    {
        using var client = new CapturingSqsClient();
        using var publisher = new AwsSqsPublisher(client);

        var result = await publisher.SendMessageAsync(QueueUrl, "body", [], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(Assert.IsType<SendMessageRequest>(client.Captured).MessageAttributes);
    }

    private sealed class CapturingSqsClient() : AmazonSQSClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public SendMessageRequest? Captured { get; private set; }

        public override Task<SendMessageResponse> SendMessageAsync(
            SendMessageRequest request,
            CancellationToken cancellationToken = default)
        {
            Captured = request;
            return Task.FromResult(new SendMessageResponse { HttpStatusCode = HttpStatusCode.OK });
        }
    }
}
