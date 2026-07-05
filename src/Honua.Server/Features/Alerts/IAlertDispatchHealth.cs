// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Domain;

namespace Honua.Alerts;

/// <summary>
/// Exposes the alert dispatcher's most recent operational snapshot to the
/// dispatch-backlog health check. Implemented by
/// <see cref="AlertDispatchBackgroundService"/>, which refreshes the snapshot after
/// each dispatch pass so the health check reads cached state instead of querying the
/// database on every readiness probe.
/// </summary>
internal interface IAlertDispatchHealth
{
    /// <summary>True once the dispatcher loop is running (false when disabled or exited).</summary>
    bool IsDispatcherRunning { get; }

    /// <summary>True when the alert pipeline is enabled by configuration.</summary>
    bool IsDispatcherEnabled { get; }

    /// <summary>Timestamp of the most recent successful dispatch pass, when known.</summary>
    DateTimeOffset? LastPollAt { get; }

    /// <summary>Most recent backlog snapshot captured by the dispatcher, when available.</summary>
    AlertDispatchBacklog? LastBacklog { get; }

    /// <summary>True when the most recent dispatch pass failed to reach the backlog store.</summary>
    bool IsStoragePollFailing { get; }
}
