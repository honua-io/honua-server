// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Resolves the control-plane trigger mode from configuration at composition time, so that the
/// PERIODIC (bucket-b) background-service registrations scattered across assemblies can all make the
/// same Poll-vs-Event decision without each one re-binding the typed options.
/// <para>
/// The value lives at <c>ControlPlane:TriggerMode</c> (the same key the typed
/// <c>ControlPlaneTriggerOptions</c> binds, in <c>Honua.Server</c>). Reading the raw string here keeps
/// the resolver in <c>Honua.Core</c> — where the cross-assembly registrations can reach it — without
/// depending on the server-internal enum. Anything other than a case-insensitive <c>Event</c> resolves
/// to <c>Poll</c>, so an unconfigured or on-prem deployment keeps the in-process timers exactly as
/// before (byte-for-byte).
/// </para>
/// </summary>
public static class ControlPlaneTriggerModeResolver
{
    private const string TriggerModeKey = "ControlPlane:TriggerMode";
    private const string EventModeValue = "Event";

    /// <summary>
    /// Returns <c>true</c> when <c>ControlPlane:TriggerMode</c> is (case-insensitively) <c>Event</c>.
    /// In Event mode the in-process periodic timers must NOT be hosted — the ticks are driven by
    /// EventBridge Scheduler through the scheduled-tick dispatcher instead.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    public static bool IsEventMode(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration[TriggerModeKey];
        return string.Equals(value?.Trim(), EventModeValue, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns <c>true</c> when the in-process periodic timers should be hosted (the default
    /// <c>Poll</c> mode). Convenience inverse of <see cref="IsEventMode"/> for the
    /// <c>AddHostedService</c> registration sites.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    public static bool ShouldHostInProcessTimers(IConfiguration configuration)
        => !IsEventMode(configuration);
}
