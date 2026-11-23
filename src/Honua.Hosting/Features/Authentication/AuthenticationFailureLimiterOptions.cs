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
    internal static readonly TimeSpan MaxPollInterval = TimeSpan.FromMinutes(1);

    public bool Enabled { get; set; } = true;

    public int Threshold { get; set; } = 10;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan BlockDuration { get; set; } = TimeSpan.FromMinutes(15);

    public int MaxTrackedSources { get; set; } = 10_000;

    // With a system database, how soon a refusal made by another server instance takes effect on this one.
    public TimeSpan SharedPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    // CIDR ranges that are never counted or refused, such as a proxy or health checker that shares one address for many clients.
    public List<string> ExemptNetworks { get; set; } = [];
}

internal sealed class AuthenticationFailureLimiterOptionsValidator : IValidateOptions<AuthenticationFailureLimiterOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthenticationFailureLimiterOptions options)
    {
        var failures = new[]
            {
                OutOfRange(nameof(options.Threshold), options.Threshold, int.MaxValue),
                OutOfRange(nameof(options.MaxTrackedSources), options.MaxTrackedSources, AuthenticationFailureLimiterOptions.MaxSources),
                OutOfRange(nameof(options.Window), options.Window),
                OutOfRange(nameof(options.BlockDuration), options.BlockDuration),
                OutOfRange(nameof(options.SharedPollInterval), options.SharedPollInterval, AuthenticationFailureLimiterOptions.MaxPollInterval),
            }
            .Concat(options.ExemptNetworks.Select(NotANetwork))
            .OfType<string>()
            .ToList();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static string? OutOfRange(string setting, int value, int maximum)
        => value <= 0 || value > maximum
            ? $"{AuthenticationFailureLimiterOptions.SectionName}:{setting} must be positive and at most {maximum}."
            : null;

    private static string? OutOfRange(string setting, TimeSpan value)
        => OutOfRange(setting, value, AuthenticationFailureLimiterOptions.MaxDuration);

    private static string? OutOfRange(string setting, TimeSpan value, TimeSpan maximum)
        => value <= TimeSpan.Zero || value > maximum
            ? $"{AuthenticationFailureLimiterOptions.SectionName}:{setting} must be positive and at most {maximum}."
            : null;

    private static string? NotANetwork(string network)
        => IPNetwork.TryParse(network, out _)
            ? null
            : $"{AuthenticationFailureLimiterOptions.SectionName}:ExemptNetworks entry '{network}' is not a network address with a prefix length, such as 10.0.0.0/8 or 192.0.2.10/32.";
}
