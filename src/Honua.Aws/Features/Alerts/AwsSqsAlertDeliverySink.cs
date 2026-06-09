// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon.SQS;
using Amazon.SQS.Model;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

/// <summary>
/// Thin adapter around <see cref="IAmazonSQS"/> for testability.
/// </summary>
internal interface ISqsPublisher
{
    Task<SqsPublishResult> SendMessageAsync(
        string queueUrl,
        string messageBody,
        Dictionary<string, string> attributes,
        CancellationToken cancellationToken);
}

internal sealed record SqsPublishResult(bool Succeeded, bool Retryable, string? Error);

internal sealed class AwsSqsPublisher : ISqsPublisher, IDisposable
{
    private readonly IAmazonSQS _client;

    public AwsSqsPublisher(IAmazonSQS client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public void Dispose() => _client.Dispose();

    public async Task<SqsPublishResult> SendMessageAsync(
        string queueUrl,
        string messageBody,
        Dictionary<string, string> attributes,
        CancellationToken cancellationToken)
    {
        var request = new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = messageBody
        };

        foreach (var (key, value) in attributes)
        {
            request.MessageAttributes[key] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = value
            };
        }

        try
        {
            var response = await _client.SendMessageAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.HttpStatusCode is >= System.Net.HttpStatusCode.OK and < System.Net.HttpStatusCode.MultipleChoices)
            {
                return new SqsPublishResult(true, false, null);
            }

            var retryable = (int)response.HttpStatusCode >= 500;
            return new SqsPublishResult(false, retryable, $"SQS send responded with {(int)response.HttpStatusCode}.");
        }
        catch (QueueDoesNotExistException)
        {
            return new SqsPublishResult(false, false, "SQS queue not found.");
        }
    }
}

internal sealed class AwsSqsAlertDeliverySink : IAlertDeliverySink
{
    private readonly ISqsPublisher _publisher;
    private readonly AlertDeliveryOptions _options;

    public AwsSqsAlertDeliverySink(
        ISqsPublisher publisher,
        IOptions<AlertDeliveryOptions> options)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public AlertChannelType ChannelType => AlertChannelType.AwsSqs;

    public async Task<AlertDeliveryResult> DeliverAsync(
        AlertDispatchItem dispatchItem,
        AlertEventEnvelope alertEvent,
        CancellationToken cancellationToken = default)
    {
        var sqsOptions = _options.Dispatch.AwsSqs;
        if (sqsOptions is null || string.IsNullOrWhiteSpace(sqsOptions.QueueUrl))
        {
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = false,
                Error = "AWS SQS queue URL is not configured."
            };
        }

        var queueUrl = dispatchItem.Destination ?? sqsOptions.QueueUrl;

        try
        {
            var result = await _publisher.SendMessageAsync(
                queueUrl,
                alertEvent.PayloadJson,
                AlertDeliveryMetadata.BuildStringAttributes(alertEvent),
                cancellationToken).ConfigureAwait(false);

            return new AlertDeliveryResult
            {
                Succeeded = result.Succeeded,
                Retryable = result.Retryable,
                Error = result.Error
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = true,
                Error = "SQS alert delivery failed."
            };
        }
    }
}
