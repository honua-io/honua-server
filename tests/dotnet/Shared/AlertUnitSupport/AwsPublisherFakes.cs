// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Alerts;

namespace Honua.Server.Tests.Features.Alerts;

internal sealed class FakeSnsPublisher : ISnsPublisher
{
    public string? LastTopicArn { get; private set; }

    public Dictionary<string, string>? LastAttributes { get; private set; }

    public Func<string, string, string?, Dictionary<string, string>, CancellationToken, Task<SnsPublishResult>>? PublishAsyncHandler { get; init; }

    public Task<SnsPublishResult> PublishAsync(
        string topicArn,
        string message,
        string? subject,
        Dictionary<string, string> attributes,
        CancellationToken cancellationToken)
    {
        LastTopicArn = topicArn;
        LastAttributes = attributes;

        return PublishAsyncHandler is null
            ? Task.FromResult(new SnsPublishResult(true, false, null))
            : PublishAsyncHandler(topicArn, message, subject, attributes, cancellationToken);
    }
}

internal sealed class FakeSqsPublisher : ISqsPublisher
{
    public string? LastQueueUrl { get; private set; }

    public Dictionary<string, string>? LastAttributes { get; private set; }

    public Func<string, string, Dictionary<string, string>, CancellationToken, Task<SqsPublishResult>>? SendMessageAsyncHandler { get; init; }

    public Task<SqsPublishResult> SendMessageAsync(
        string queueUrl,
        string messageBody,
        Dictionary<string, string> attributes,
        CancellationToken cancellationToken)
    {
        LastQueueUrl = queueUrl;
        LastAttributes = attributes;

        return SendMessageAsyncHandler is null
            ? Task.FromResult(new SqsPublishResult(true, false, null))
            : SendMessageAsyncHandler(queueUrl, messageBody, attributes, cancellationToken);
    }
}
