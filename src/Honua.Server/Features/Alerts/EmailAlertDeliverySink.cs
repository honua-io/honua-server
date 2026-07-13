// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Mail;
using Honua.Alerts.Ops;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Alerts;

internal sealed class EmailAlertDeliverySink : IAlertDeliverySink
{
    private readonly AlertDeliveryOptions _options;
    private readonly ILogger<EmailAlertDeliverySink>? _logger;

    public EmailAlertDeliverySink(IOptions<AlertDeliveryOptions> options, ILogger<EmailAlertDeliverySink>? logger = null)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public AlertChannelType ChannelType => AlertChannelType.Email;

    public async Task<AlertDeliveryResult> DeliverAsync(
        AlertDispatchItem dispatchItem,
        AlertEventEnvelope alertEvent,
        CancellationToken cancellationToken = default)
    {
        var emailOptions = _options.Dispatch.Email;
        if (emailOptions is null || string.IsNullOrWhiteSpace(emailOptions.SmtpHost))
        {
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = false,
                Error = "Email SMTP settings are not configured."
            };
        }

        var recipient = dispatchItem.Destination ?? emailOptions.DefaultRecipient;
        if (string.IsNullOrWhiteSpace(recipient))
        {
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = false,
                Error = "Email recipient is not configured."
            };
        }

        try
        {
            var content = BuildContent(alertEvent);
            using var message = new MailMessage
            {
                From = new MailAddress(emailOptions.FromAddress, emailOptions.FromName),
                Subject = content.Subject,
                Body = content.Body,
                IsBodyHtml = false
            };
            message.To.Add(new MailAddress(recipient));

            if (content.RuleHeader is not null)
            {
                message.Headers.Add("X-Honua-Alert-Rule", content.RuleHeader);
            }

            message.Headers.Add("X-Honua-Alert-Event", alertEvent.DedupeKey);

            using var client = new SmtpClient(emailOptions.SmtpHost, emailOptions.SmtpPort)
            {
                EnableSsl = emailOptions.UseSsl
            };

            if (!string.IsNullOrWhiteSpace(emailOptions.Username))
            {
                client.Credentials = new NetworkCredential(emailOptions.Username, emailOptions.Password);
            }

            await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);

            return new AlertDeliveryResult { Succeeded = true, Retryable = false };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SmtpFailedRecipientException)
        {
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = false,
                Error = "Email delivery failed for recipient."
            };
        }
        catch (SmtpException ex)
        {
            var retryable = ex.StatusCode is SmtpStatusCode.ServiceNotAvailable
                or SmtpStatusCode.MailboxBusy
                or SmtpStatusCode.ServiceClosingTransmissionChannel;

            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = retryable,
                Error = $"SMTP error ({ex.StatusCode})."
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Email alert delivery failed for event {DedupeKey}.", alertEvent.DedupeKey);
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = true,
                Error = "Email delivery failed."
            };
        }
    }

    /// <summary>
    /// Builds the email subject, body, and rule header for an alert event. Ops
    /// notifications render their human-readable title/body/attributes; the rule
    /// header is omitted (they are not linked to an alert rule). GIS alerts keep the
    /// existing subject/body/rule-header formatting.
    /// </summary>
    internal static EmailAlertContent BuildContent(AlertEventEnvelope alertEvent)
    {
        if (OpsNotificationPresentation.TryResolve(alertEvent) is { } ops)
        {
            var body = ops.Body;
            var attributes = OpsNotificationPresentation.FormatAttributes(ops.Attributes, "\n");
            if (attributes.Length > 0)
            {
                body = string.IsNullOrWhiteSpace(body) ? attributes : $"{body}\n\n{attributes}";
            }

            return new EmailAlertContent(
                Subject: $"[Honua Ops] {alertEvent.Severity}: {ops.Title}",
                Body: body,
                RuleHeader: null);
        }

        return new EmailAlertContent(
            Subject: $"[Honua Alert] {alertEvent.Severity}: {alertEvent.TriggerType} on layer {alertEvent.LayerId}",
            Body: alertEvent.PayloadJson,
            RuleHeader: alertEvent.RuleId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Rendered email content for an alert event.</summary>
    /// <param name="Subject">Email subject line.</param>
    /// <param name="Body">Plain-text email body.</param>
    /// <param name="RuleHeader">Value for the <c>X-Honua-Alert-Rule</c> header, or null to omit it (ops events).</param>
    internal readonly record struct EmailAlertContent(string Subject, string Body, string? RuleHeader);
}
