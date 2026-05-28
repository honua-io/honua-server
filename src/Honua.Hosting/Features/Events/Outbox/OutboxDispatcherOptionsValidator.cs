// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Events.Outbox;

/// <summary>
/// Validates <see cref="OutboxDispatcherOptions"/> at startup so a misconfigured
/// dispatcher fails fast instead of silently disabling claim/dispatch behavior.
/// Pairs with <c>.ValidateOnStart()</c> on the options registration. Mirrors the
/// in-module pattern established by <see cref="Honua.Server.Features.Infrastructure.Events.FeatureChangeWebhookOptionsValidator"/>.
/// </summary>
internal sealed class OutboxDispatcherOptionsValidator : IValidateOptions<OutboxDispatcherOptions>
{
    public ValidateOptionsResult Validate(string? name, OutboxDispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        // BatchSize must be positive: PostgresFeatureChangeOutboxRepository.ClaimPendingAsync
        // returns Array.Empty<>() for non-positive batch sizes, which would idle the
        // dispatcher forever while pending rows accumulate silently.
        if (options.BatchSize <= 0)
        {
            failures.Add("Outbox dispatcher BatchSize must be greater than 0.");
        }

        // IdlePollIntervalMs must be positive: 0 would hot-spin between empty passes
        // and Task.Delay rejects values below -1.
        if (options.IdlePollIntervalMs <= 0)
        {
            failures.Add("Outbox dispatcher IdlePollIntervalMs must be greater than 0.");
        }

        // ClaimTtlSeconds must be positive: a zero or negative TTL would expire claims
        // immediately, racing recovery against the in-flight publish on every pass.
        if (options.ClaimTtlSeconds <= 0)
        {
            failures.Add("Outbox dispatcher ClaimTtlSeconds must be greater than 0.");
        }

        // RecoveryIntervalSeconds must be positive: a zero/negative interval would run
        // recovery on every pass and burn database round trips, and the timestamp math
        // in MaybeRecoverExpiredClaimsAsync becomes nonsensical for negative inputs.
        if (options.RecoveryIntervalSeconds <= 0)
        {
            failures.Add("Outbox dispatcher RecoveryIntervalSeconds must be greater than 0.");
        }

        // MaxRetries must be positive: the SQL CASE in MarkFailedAsync compares
        // retry_count + 1 >= max_retries, so a non-positive value dead-letters every
        // failure on first attempt — the retry budget the runbook documents would not exist.
        if (options.MaxRetries <= 0)
        {
            failures.Add("Outbox dispatcher MaxRetries must be greater than 0.");
        }

        // DegradedBacklogThreshold must be positive: OutboxHealthCheck reports Degraded
        // when PendingCount >= threshold, so a non-positive threshold flips an empty
        // backlog to Degraded and removes the operator's Healthy signal.
        if (options.DegradedBacklogThreshold <= 0)
        {
            failures.Add("Outbox dispatcher DegradedBacklogThreshold must be greater than 0.");
        }

        // UnhealthyDeadLetterThreshold must be positive: the same Healthy/Unhealthy
        // comparison flips a zero-dead-letter backlog to Unhealthy at threshold 0,
        // permanently failing readiness with no operator-actionable signal.
        if (options.UnhealthyDeadLetterThreshold <= 0)
        {
            failures.Add("Outbox dispatcher UnhealthyDeadLetterThreshold must be greater than 0.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
