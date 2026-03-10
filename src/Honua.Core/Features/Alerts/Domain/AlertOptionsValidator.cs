// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;

namespace Honua.Core.Features.Alerts.Domain;

/// <summary>
/// Validates alerting configuration at startup.
/// </summary>
public sealed class AlertOptionsValidator : OptionsValidator<AlertOptions>
{
    /// <inheritdoc />
    protected override void ValidateOptions(AlertOptions options, List<string> failures)
    {
        if (!Enum.IsDefined(options.Edition))
        {
            failures.Add($"{nameof(AlertOptions.Edition)} has an unsupported value '{options.Edition}'.");
        }

        ValidateDataAnnotations(options.Evaluation, failures, nameof(AlertOptions.Evaluation));
        ValidateDataAnnotations(options.Dispatch, failures, nameof(AlertOptions.Dispatch));

        ValidateTimeSpan(
            options.Evaluation.DwellSweepInterval,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromHours(1),
            $"{nameof(AlertOptions.Evaluation)}.{nameof(AlertEvaluationOptions.DwellSweepInterval)}",
            failures);

        ValidateTimeSpan(
            options.Evaluation.IdleDelay,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMinutes(1),
            $"{nameof(AlertOptions.Evaluation)}.{nameof(AlertEvaluationOptions.IdleDelay)}",
            failures);

        ValidateTimeSpan(
            options.Evaluation.LeaderLeaseDuration,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMinutes(5),
            $"{nameof(AlertOptions.Evaluation)}.{nameof(AlertEvaluationOptions.LeaderLeaseDuration)}",
            failures);

        ValidateTimeSpan(
            options.Dispatch.InitialBackoff,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMinutes(1),
            $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.InitialBackoff)}",
            failures);

        ValidateTimeSpan(
            options.Dispatch.MaxBackoff,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromHours(1),
            $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.MaxBackoff)}",
            failures);

        ValidateTimeSpan(
            options.Dispatch.IdleDelay,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMinutes(1),
            $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.IdleDelay)}",
            failures);

        if (options.Dispatch.InitialBackoff > options.Dispatch.MaxBackoff)
        {
            failures.Add("Dispatch.InitialBackoff must be less than or equal to Dispatch.MaxBackoff.");
        }

        if (!string.IsNullOrWhiteSpace(options.Dispatch.DefaultWebhookUrl))
        {
            ValidateOutboundHttpUrl(
                options.Dispatch.DefaultWebhookUrl,
                $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.DefaultWebhookUrl)}",
                failures);
        }

        ValidateDataAnnotations(options.Dispatch.Digest, failures, $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.Digest)}");

        if (!string.IsNullOrWhiteSpace(options.Dispatch.Digest.WebhookUrl))
        {
            ValidateOutboundHttpUrl(
                options.Dispatch.Digest.WebhookUrl,
                $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.Digest)}.{nameof(DigestAlertOptions.WebhookUrl)}",
                failures);
        }

        ValidateTimeSpan(
            options.Dispatch.Digest.FlushInterval,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(24),
            $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.Digest)}.{nameof(DigestAlertOptions.FlushInterval)}",
            failures);
    }
}
