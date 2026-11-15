// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Realtime;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Realtime;

// The realtime event families (specification realtime-cross-node-rc3 section 3). Every family but
// features has a head row in realtime_log_heads; features are read from honua.feature_changes.
internal static class RealtimeLogFamilies
{
    public static readonly string Features = RealtimeFamily.Features.Name;
    public static readonly string CollabOps = RealtimeFamily.CollabOps.Name;
    public static readonly string CollabPresence = RealtimeFamily.CollabPresence.Name;
    public static readonly string Observations = RealtimeFamily.Observations.Name;
    public static readonly string Alerts = RealtimeFamily.Alerts.Name;
    public static readonly string Jobs = RealtimeFamily.Jobs.Name;
    public static readonly string Catalog = RealtimeFamily.Catalog.Name;
    public static readonly string Admin = RealtimeFamily.Admin.Name;
    public static readonly string Security = RealtimeFamily.Security.Name;
    public static readonly string Mcp = RealtimeFamily.Mcp.Name;

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

    // auto follows Deployment:Mode (postgres on MultiNode, in-process otherwise).
    public string Provider { get; set; } = RealtimeProviderNames.Auto;

    // Optional dedicated connection string for the listener; DefaultConnection otherwise.
    public const string ListenerConnectionStringName = "RealtimeListener";

    // How often a healthy listener's task probes anyway, to catch lost notifications.
    public TimeSpan SafetyPollInterval { get; set; } = TimeSpan.FromSeconds(10);

    // How often a task probes while its listener is down or not allowed (a connection proxy).
    public TimeSpan DegradedPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    // How often the in-process provider probes for rows written by triggers or other processes.
    public TimeSpan LocalPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    // Notifications received within this window trigger one probe.
    public TimeSpan WakeCoalesceWindow { get; set; } = TimeSpan.FromMilliseconds(20);

    public TimeSpan ListenerReconnectMinDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan ListenerReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    // Rows per read while following the log, and per page while replaying a subscription.
    public int ReadBatchSize { get; set; } = 500;

    public int ReplayBatchSize { get; set; } = 500;

    // A resume further behind the head than this receives reset(too-far-behind).
    public int ResumeMaxEvents { get; set; } = 10_000;

    // Events a resuming subscription holds while its replay catches up; beyond it the replay re-reads.
    public int ReplayHandoffCapacity { get; set; } = 1_024;

    // Queued best-effort (presence) events are appended at most this often.
    public TimeSpan PresenceFlushInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan ShutdownDrain { get; set; } = TimeSpan.FromSeconds(20);

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

        if (!RealtimeProviderNames.IsKnown(options.Provider))
        {
            failures.Add($"Realtime:Provider must be auto, in-process or postgres; it is '{options.Provider}'.");
        }

        ValidateIntervals(options, failures);
        ValidateCounts(options, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
    private static void ValidateIntervals(RealtimeOptions options, List<string> failures)
    {
        foreach (var (setting, interval) in new (string, TimeSpan)[]
                 {
                     (nameof(RealtimeOptions.SafetyPollInterval), options.SafetyPollInterval),
                     (nameof(RealtimeOptions.DegradedPollInterval), options.DegradedPollInterval),
                     (nameof(RealtimeOptions.LocalPollInterval), options.LocalPollInterval),
                     (nameof(RealtimeOptions.ListenerReconnectMinDelay), options.ListenerReconnectMinDelay),
                     (nameof(RealtimeOptions.PresenceFlushInterval), options.PresenceFlushInterval),
                     (nameof(RealtimeOptions.ShutdownDrain), options.ShutdownDrain)
                 })
        {
            if (interval <= TimeSpan.Zero)
            {
                failures.Add($"Realtime:{setting} must be positive.");
            }
        }

        if (options.WakeCoalesceWindow < TimeSpan.Zero)
        {
            failures.Add("Realtime:WakeCoalesceWindow must not be negative.");
        }

        if (options.ListenerReconnectMaxDelay < options.ListenerReconnectMinDelay)
        {
            failures.Add("Realtime:ListenerReconnectMaxDelay must not be less than ListenerReconnectMinDelay.");
        }
    }

    private static void ValidateCounts(RealtimeOptions options, List<string> failures)
    {
        foreach (var (setting, count) in new (string, int)[]
                 {
                     (nameof(RealtimeOptions.ReadBatchSize), options.ReadBatchSize),
                     (nameof(RealtimeOptions.ReplayBatchSize), options.ReplayBatchSize),
                     (nameof(RealtimeOptions.ResumeMaxEvents), options.ResumeMaxEvents),
                     (nameof(RealtimeOptions.ReplayHandoffCapacity), options.ReplayHandoffCapacity)
                 })
        {
            if (count < 1)
            {
                failures.Add($"Realtime:{setting} must be at least 1.");
            }
        }
    }

}

internal static class RealtimeProviderNames
{
    public const string Auto = "auto";
    public const string InProcess = "in-process";
    public const string Postgres = "postgres";

    public static bool IsKnown(string? name)
        => name is not null && (Is(name, Auto) || Is(name, InProcess) || Is(name, Postgres));

    public static bool Is(string? name, string expected) => string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);
}
