// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.ServiceRegistration;

namespace Honua.Core.Features.Alerts.Domain;

/// <summary>
/// Validates alerting configuration at startup.
/// </summary>
public sealed class AlertOptionsValidator : ConfigurationValidator<AlertOptions>
{
    /// <inheritdoc />
    protected override void PerformFeatureSpecificValidation(AlertOptions options, List<string> errors)
    {
        if (!Enum.IsDefined(options.Edition))
        {
            errors.Add($"{nameof(AlertOptions.Edition)} has an unsupported value '{options.Edition}'.");
        }

        ValidateDataAnnotations(options.Evaluation, errors, nameof(AlertOptions.Evaluation));
        ValidateDataAnnotations(options.Dispatch, errors, nameof(AlertOptions.Dispatch));

        ValidateStringLength(
            options.Evaluation.WorkerName,
            minLength: 1,
            maxLength: 64,
            $"{nameof(AlertOptions.Evaluation)}.{nameof(AlertEvaluationOptions.WorkerName)}",
            errors);

        ValidateStringLength(
            options.Evaluation.LeaderElectionMode,
            minLength: 1,
            maxLength: 32,
            $"{nameof(AlertOptions.Evaluation)}.{nameof(AlertEvaluationOptions.LeaderElectionMode)}",
            errors);

        ValidateTimeSpan(
            options.Evaluation.DwellSweepInterval,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromHours(1),
            $"{nameof(AlertOptions.Evaluation)}.{nameof(AlertEvaluationOptions.DwellSweepInterval)}",
            errors);

        ValidateTimeSpan(
            options.Evaluation.IdleDelay,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMinutes(1),
            $"{nameof(AlertOptions.Evaluation)}.{nameof(AlertEvaluationOptions.IdleDelay)}",
            errors);

        ValidateTimeSpan(
            options.Evaluation.LeaderLeaseDuration,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(5),
            $"{nameof(AlertOptions.Evaluation)}.{nameof(AlertEvaluationOptions.LeaderLeaseDuration)}",
            errors);

        ValidateTimeSpan(
            options.Dispatch.InitialBackoff,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMinutes(1),
            $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.InitialBackoff)}",
            errors);

        ValidateTimeSpan(
            options.Dispatch.MaxBackoff,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromHours(1),
            $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.MaxBackoff)}",
            errors);

        ValidateTimeSpan(
            options.Dispatch.IdleDelay,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMinutes(1),
            $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.IdleDelay)}",
            errors);

        if (options.Dispatch.InitialBackoff > options.Dispatch.MaxBackoff)
        {
            errors.Add("Dispatch.InitialBackoff must be less than or equal to Dispatch.MaxBackoff.");
        }

        if (!string.IsNullOrWhiteSpace(options.Dispatch.DefaultWebhookUrl))
        {
            ValidateOutboundHttpUrl(
                options.Dispatch.DefaultWebhookUrl,
                $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.DefaultWebhookUrl)}",
                errors);

            if (string.IsNullOrWhiteSpace(options.Dispatch.DefaultWebhookSecret))
            {
                errors.Add($"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.DefaultWebhookSecret)} must be configured when {nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.DefaultWebhookUrl)} is set.");
            }
        }

        ValidateDataAnnotations(options.Dispatch.Digest, errors, $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.Digest)}");

        if (!string.IsNullOrWhiteSpace(options.Dispatch.Digest.WebhookUrl))
        {
            ValidateOutboundHttpUrl(
                options.Dispatch.Digest.WebhookUrl,
                $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.Digest)}.{nameof(DigestAlertOptions.WebhookUrl)}",
                errors);

            if (string.IsNullOrWhiteSpace(options.Dispatch.Digest.WebhookSecret))
            {
                errors.Add($"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.Digest)}.{nameof(DigestAlertOptions.WebhookSecret)} must be configured when {nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.Digest)}.{nameof(DigestAlertOptions.WebhookUrl)} is set.");
            }
        }

        ValidateTimeSpan(
            options.Dispatch.Digest.FlushInterval,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(24),
            $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.Digest)}.{nameof(DigestAlertOptions.FlushInterval)}",
            errors);
    }

    private static void ValidateStringLength(
        string? value,
        int minLength,
        int maxLength,
        string propertyName,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{propertyName} is required and cannot be empty.");
            return;
        }

        if (value.Length < minLength || value.Length > maxLength)
        {
            errors.Add($"{propertyName} must be between {minLength} and {maxLength} characters.");
        }
    }
}
