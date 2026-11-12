// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

internal sealed class AuthenticationFailureLimiterOptions
{
    public const string SectionName = "Authentication:FailedAttempts";

    internal static readonly TimeSpan MaxDuration = TimeSpan.FromDays(1);
    internal const int MaxSources = 1_000_000;

    public bool Enabled { get; set; } = true;

    public int Threshold { get; set; } = 10;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan BlockDuration { get; set; } = TimeSpan.FromMinutes(15);

    public int MaxTrackedSources { get; set; } = 10_000;

    // CIDR ranges that are never counted or refused, such as a proxy or health checker that shares one address for many clients.
    public List<string> ExemptNetworks { get; set; } = [];
}

internal sealed class AuthenticationFailureLimiterOptionsValidator : IValidateOptions<AuthenticationFailureLimiterOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthenticationFailureLimiterOptions options)
    {
        var failures = new List<string>();
        AddIfOutOfRange(failures, nameof(options.Threshold), options.Threshold, int.MaxValue);
        AddIfOutOfRange(failures, nameof(options.MaxTrackedSources), options.MaxTrackedSources, AuthenticationFailureLimiterOptions.MaxSources);
        AddIfOutOfRange(failures, nameof(options.Window), options.Window);
        AddIfOutOfRange(failures, nameof(options.BlockDuration), options.BlockDuration);
        foreach (var network in options.ExemptNetworks.Where(network => !IPNetwork.TryParse(network, out _)))
        {
            failures.Add($"{AuthenticationFailureLimiterOptions.SectionName}:ExemptNetworks entry '{network}' is not a CIDR range.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void AddIfOutOfRange(List<string> failures, string setting, TimeSpan value)
    {
        if (value <= TimeSpan.Zero || value > AuthenticationFailureLimiterOptions.MaxDuration)
        {
            failures.Add($"{AuthenticationFailureLimiterOptions.SectionName}:{setting} must be positive and at most {AuthenticationFailureLimiterOptions.MaxDuration}.");
        }
    }

    private static void AddIfOutOfRange(List<string> failures, string setting, int value, int maximum)
    {
        if (value <= 0 || value > maximum)
        {
            failures.Add($"{AuthenticationFailureLimiterOptions.SectionName}:{setting} must be positive and at most {maximum}.");
        }
    }
}
