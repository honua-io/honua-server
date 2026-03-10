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

        if (options.Dispatch.AwsSns is { } awsSns)
        {
            ValidateDataAnnotations(awsSns, failures, $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.AwsSns)}");
        }

        if (options.Dispatch.AzureEventGrid is { } eventGrid)
        {
            ValidateDataAnnotations(eventGrid, failures, $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.AzureEventGrid)}");

            if (!string.IsNullOrWhiteSpace(eventGrid.TopicEndpoint) &&
                !Uri.TryCreate(eventGrid.TopicEndpoint, UriKind.Absolute, out _))
            {
                failures.Add($"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.AzureEventGrid)}.{nameof(AzureEventGridAlertOptions.TopicEndpoint)} must be a valid absolute URL.");
            }
        }

        if (options.Dispatch.Email is { } email)
        {
            ValidateDataAnnotations(email, failures, $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.Email)}");
        }

        if (options.Dispatch.Slack is { } slack)
        {
            ValidateDataAnnotations(slack, failures, $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.Slack)}");

            if (!string.IsNullOrWhiteSpace(slack.WebhookUrl))
            {
                ValidateOutboundHttpUrl(
                    slack.WebhookUrl,
                    $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.Slack)}.{nameof(SlackAlertOptions.WebhookUrl)}",
                    failures);
            }
        }

        if (options.Dispatch.Teams is { } teams)
        {
            ValidateDataAnnotations(teams, failures, $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.Teams)}");

            if (!string.IsNullOrWhiteSpace(teams.WebhookUrl))
            {
                ValidateOutboundHttpUrl(
                    teams.WebhookUrl,
                    $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.Teams)}.{nameof(TeamsAlertOptions.WebhookUrl)}",
                    failures);
            }
        }

        if (options.Dispatch.AwsSqs is { } awsSqs)
        {
            ValidateDataAnnotations(awsSqs, failures, $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.AwsSqs)}");
        }

        if (options.Dispatch.AzureEventHub is { } eventHub)
        {
            ValidateDataAnnotations(eventHub, failures, $"{nameof(AlertOptions.Dispatch)}.{nameof(AlertDispatchOptions.AzureEventHub)}");
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
