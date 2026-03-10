// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Mail;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Alerts;

internal sealed class EmailAlertDeliverySink : IAlertDeliverySink
{
    private readonly AlertOptions _options;

    public EmailAlertDeliverySink(IOptions<AlertOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
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
            using var message = new MailMessage
            {
                From = new MailAddress(emailOptions.FromAddress, emailOptions.FromName),
                Subject = $"[Honua Alert] {alertEvent.Severity}: {alertEvent.TriggerType} on layer {alertEvent.LayerId}",
                Body = alertEvent.PayloadJson,
                IsBodyHtml = false
            };
            message.To.Add(new MailAddress(recipient));

            message.Headers.Add("X-Honua-Alert-Rule", alertEvent.RuleId.ToString());
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
        catch (SmtpFailedRecipientException ex)
        {
            return new AlertDeliveryResult
            {
                Succeeded = false,
                Retryable = false,
                Error = $"Email delivery failed for recipient: {ex.Message}"
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
                Error = $"SMTP error ({ex.StatusCode}): {ex.Message}"
            };
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
