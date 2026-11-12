// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Realtime;

// The realtime event families (specification realtime-cross-node-rc3 section 3). Every family but
// features has a head row in realtime_log_heads; features are read from honua.feature_changes.
internal static class RealtimeLogFamilies
{
    public const string Features = "features";
    public const string CollabOps = "collab-ops";
    public const string CollabPresence = "collab-presence";
    public const string Observations = "observations";
    public const string Alerts = "alerts";
    public const string Jobs = "jobs";
    public const string Catalog = "catalog";
    public const string Admin = "admin";
    public const string Security = "security";
    public const string Mcp = "mcp";

    // The families stored in realtime_events, in the order the migration seeds them.
    public static readonly IReadOnlyList<string> Logged =
        [CollabOps, CollabPresence, Observations, Alerts, Jobs, Catalog, Admin, Security, Mcp];

    // Configuration keys are case-insensitive, so a family named in any case is that family.
    public static string? Canonical(string family)
        => string.Equals(family, Features, StringComparison.OrdinalIgnoreCase)
            ? Features
            : Logged.FirstOrDefault(logged => string.Equals(logged, family, StringComparison.OrdinalIgnoreCase));

    public static bool IsKnown(string family) => Canonical(family) is not null;
}

internal sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";

    // How long each family's events stay readable. Logged families write theirs to
    // realtime_log_heads at startup; features use theirs as the resume window only.
    public Dictionary<string, TimeSpan> Retention { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [RealtimeLogFamilies.Features] = TimeSpan.FromHours(24),
        [RealtimeLogFamilies.CollabOps] = TimeSpan.FromHours(24),
        [RealtimeLogFamilies.CollabPresence] = TimeSpan.FromMinutes(2),
        [RealtimeLogFamilies.Observations] = TimeSpan.FromHours(1),
        [RealtimeLogFamilies.Alerts] = TimeSpan.FromDays(7),
        [RealtimeLogFamilies.Jobs] = TimeSpan.FromHours(24),
        [RealtimeLogFamilies.Catalog] = TimeSpan.FromHours(24),
        [RealtimeLogFamilies.Admin] = TimeSpan.FromHours(24),
        [RealtimeLogFamilies.Security] = TimeSpan.FromHours(1),
        [RealtimeLogFamilies.Mcp] = TimeSpan.FromMinutes(10)
    };

    // The retention of every logged family, as written to realtime_log_heads.
    public IReadOnlyDictionary<string, TimeSpan> LoggedRetention()
    {
        var logged = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        foreach (var (key, retention) in Retention)
        {
            if (RealtimeLogFamilies.Canonical(key) is { } family && family != RealtimeLogFamilies.Features)
            {
                logged[family] = retention;
            }
        }

        return logged;
    }
}

internal sealed class RealtimeOptionsValidator : IValidateOptions<RealtimeOptions>
{
    public ValidateOptionsResult Validate(string? name, RealtimeOptions options)
    {
        var failures = new List<string>();
        foreach (var (family, retention) in options.Retention)
        {
            if (!RealtimeLogFamilies.IsKnown(family))
            {
                failures.Add($"Realtime:Retention:{family} names no realtime family.");
            }
            else if (retention <= TimeSpan.Zero)
            {
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Realtime:Retention:{family} must be positive; it is {retention}."));
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
