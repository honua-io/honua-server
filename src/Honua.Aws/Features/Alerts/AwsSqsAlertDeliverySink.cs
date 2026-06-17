// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Amazon.Runtime;
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

internal sealed partial class AwsSqsAlertDeliverySink : IAlertDeliverySink
{
    private readonly ISqsPublisher _publisher;
    private readonly AlertDeliveryOptions _options;
    private readonly ILogger<AwsSqsAlertDeliverySink> _logger;

    public AwsSqsAlertDeliverySink(
        ISqsPublisher publisher,
        IOptions<AlertDeliveryOptions> options,
        ILogger<AwsSqsAlertDeliverySink> logger)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        catch (Exception ex)
        {
            Log.DeliveryFailed(_logger, ex);
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = IsRetryable(ex),
                Error = "SQS alert delivery failed."
            };
        }
    }

    /// <summary>
    /// Classifies a delivery exception so permanent client errors (HTTP 4xx — for
    /// example a >256KB body or an invalid message attribute) are reported as
    /// non-retryable. Retrying those never succeeds and only causes the alert
    /// pipeline to loop and lose work. Transient failures (HTTP 5xx, throttling,
    /// request timeouts, and pre-request network errors that never reached AWS)
    /// stay retryable.
    /// </summary>
    private static bool IsRetryable(Exception ex)
    {
        // AmazonClientException covers SDK-side failures that never reached AWS
        // (DNS, sockets, credential resolution); the request was not rejected, so
        // a retry is worthwhile. It is a sibling of AmazonServiceException in the
        // SDK v4 hierarchy, so check it before the service-exception branch.
        if (ex is not AmazonServiceException serviceEx)
        {
            return true;
        }

        // A zero StatusCode means no HTTP response reached the SDK (transient).
        // 408/429 are explicitly retryable; any other 4xx is a permanent client
        // error (message too large, invalid parameter, access denied) that will
        // never succeed on retry. 5xx is a transient server-side failure.
        if ((int)serviceEx.StatusCode == 0)
        {
            return true;
        }

        if (serviceEx.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        return (int)serviceEx.StatusCode >= 500;
    }

    private static partial class Log
    {
        [LoggerMessage(9051, LogLevel.Warning, "SQS alert delivery failed with an unhandled exception.")]
        public static partial void DeliveryFailed(ILogger logger, Exception ex);
    }
}
