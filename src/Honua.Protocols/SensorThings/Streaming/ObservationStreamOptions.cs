// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Protocols.SensorThings.Streaming;

// Sessions are capped per principal, per tenant, and per node so one credential cannot lock every other subscriber out.
internal sealed class ObservationStreamOptions
{
    public const string SectionName = SensorThingsOptions.SectionName + ":Streaming";

    public int MaxConcurrentSessions { get; set; } = 256;

    public int MaxSessionsPerTenant { get; set; } = 64;

    public int MaxSessionsPerPrincipal { get; set; } = 8;

    public int MaxBufferPerConnection { get; set; } = 256;

    public int RetryAfterSeconds { get; set; } = 30;
}

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
