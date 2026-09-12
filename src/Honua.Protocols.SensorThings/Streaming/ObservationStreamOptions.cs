// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Protocols.SensorThings.Streaming;

/// <summary>
/// Admission and buffering limits for the SensorThings observation stream
/// (<c>SensorThings:Streaming</c>). Sessions are admitted against three nested caps so a
/// single credential or tenant cannot pin the node-wide budget and lock every other
/// subscriber out (#4198): per principal, per tenant, and per node.
/// </summary>
internal sealed class ObservationStreamOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = SensorThingsOptions.SectionName + ":Streaming";

    /// <summary>Maximum concurrent observation-stream sessions on this node across all callers.</summary>
    public int MaxConcurrentSessions { get; set; } = 256;

    /// <summary>
    /// Maximum concurrent sessions for one tenant. Callers without a resolved tenant (for
    /// example platform administrators) share a single untenanted partition.
    /// </summary>
    public int MaxSessionsPerTenant { get; set; } = 64;

    /// <summary>Maximum concurrent sessions for one authenticated principal within a tenant.</summary>
    public int MaxSessionsPerPrincipal { get; set; } = 8;

    /// <summary>Maximum queued frames per connection before the slow consumer's frames are dropped.</summary>
    public int MaxBufferPerConnection { get; set; } = 256;

    /// <summary>Retry-After hint, in seconds, returned with a rejected session.</summary>
    public int RetryAfterSeconds { get; set; } = 30;
}

/// <summary>Validates <see cref="ObservationStreamOptions"/> at startup.</summary>
internal sealed class ObservationStreamOptionsValidator : IValidateOptions<ObservationStreamOptions>
{
    public ValidateOptionsResult Validate(string? name, ObservationStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        RequirePositive(failures, nameof(options.MaxConcurrentSessions), options.MaxConcurrentSessions);
        RequirePositive(failures, nameof(options.MaxSessionsPerTenant), options.MaxSessionsPerTenant);
        RequirePositive(failures, nameof(options.MaxSessionsPerPrincipal), options.MaxSessionsPerPrincipal);
        RequirePositive(failures, nameof(options.MaxBufferPerConnection), options.MaxBufferPerConnection);
        RequirePositive(failures, nameof(options.RetryAfterSeconds), options.RetryAfterSeconds);

        // A per-scope cap at or above the node cap re-creates the single-caller lockout.
        if (options.MaxSessionsPerTenant >= options.MaxConcurrentSessions)
        {
            failures.Add($"{ObservationStreamOptions.SectionName}:MaxSessionsPerTenant must be less than MaxConcurrentSessions.");
        }

        if (options.MaxSessionsPerPrincipal > options.MaxSessionsPerTenant)
        {
            failures.Add($"{ObservationStreamOptions.SectionName}:MaxSessionsPerPrincipal must not exceed MaxSessionsPerTenant.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void RequirePositive(List<string> failures, string key, int value)
    {
        if (value <= 0)
        {
            failures.Add($"{ObservationStreamOptions.SectionName}:{key} must be a positive integer.");
        }
    }
}
