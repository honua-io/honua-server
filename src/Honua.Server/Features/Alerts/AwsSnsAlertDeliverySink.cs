// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Alerts;

/// <summary>
/// Thin adapter around <see cref="IAmazonSimpleNotificationService"/> for testability.
/// </summary>
internal interface ISnsPublisher
{
    Task<SnsPublishResult> PublishAsync(
        string topicArn,
        string message,
        string? subject,
        Dictionary<string, string> attributes,
        CancellationToken cancellationToken);
}

internal sealed record SnsPublishResult(bool Succeeded, bool Retryable, string? Error);

internal sealed class AwsSnsPublisher : ISnsPublisher, IDisposable
{
    private readonly IAmazonSimpleNotificationService _client;

    public AwsSnsPublisher(IAmazonSimpleNotificationService client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public void Dispose() => _client.Dispose();

    public async Task<SnsPublishResult> PublishAsync(
        string topicArn,
        string message,
        string? subject,
        Dictionary<string, string> attributes,
        CancellationToken cancellationToken)
    {
        var request = new PublishRequest
        {
            TopicArn = topicArn,
            Message = message,
            Subject = subject
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
            var response = await _client.PublishAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.HttpStatusCode is >= System.Net.HttpStatusCode.OK and < System.Net.HttpStatusCode.MultipleChoices)
            {
                return new SnsPublishResult(true, false, null);
            }

            var retryable = (int)response.HttpStatusCode >= 500;
            return new SnsPublishResult(false, retryable, $"SNS publish responded with {(int)response.HttpStatusCode}.");
        }
        catch (AuthorizationErrorException ex)
        {
            return new SnsPublishResult(false, false, $"SNS authorization failed: {ex.Message}");
        }
        catch (NotFoundException ex)
        {
            return new SnsPublishResult(false, false, $"SNS topic not found: {ex.Message}");
        }
    }
}

internal sealed class AwsSnsAlertDeliverySink : IAlertDeliverySink
{
    private readonly ISnsPublisher _publisher;
    private readonly AlertDeliveryOptions _options;

    public AwsSnsAlertDeliverySink(
        ISnsPublisher publisher,
        IOptions<AlertDeliveryOptions> options)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public AlertChannelType ChannelType => AlertChannelType.AwsSns;

    public async Task<AlertDeliveryResult> DeliverAsync(
        AlertDispatchItem dispatchItem,
        AlertEventEnvelope alertEvent,
        CancellationToken cancellationToken = default)
    {
        var snsOptions = _options.Dispatch.AwsSns;
        if (snsOptions is null || string.IsNullOrWhiteSpace(snsOptions.TopicArn))
        {
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = false,
                Error = "AWS SNS topic ARN is not configured."
            };
        }

        var topicArn = dispatchItem.Destination ?? snsOptions.TopicArn;

        try
        {
            var result = await _publisher.PublishAsync(
                topicArn,
                alertEvent.PayloadJson,
                $"Honua Alert: {alertEvent.TriggerType} ({alertEvent.Severity})",
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
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = true,
                Error = ex.Message
            };
        }
    }
}
