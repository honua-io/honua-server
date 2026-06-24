// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Events;

/// <summary>
/// Configuration for broker-agnostic feature-change event sinks (#357).
/// </summary>
/// <remarks>
/// Sinks are off by default. When disabled, no sink fan-out is wired and the
/// publish path behaves exactly as it did before sinks existed (durable append
/// plus live streaming only). Enabling sinks activates the in-process broadcaster
/// and any registered sink adapters (the live Kafka/NATS adapters are delivered
/// in a follow-up increment).
/// </remarks>
public sealed class FeatureChangeEventSinkOptions
{
    /// <summary>
    /// Configuration section name bound from application configuration.
    /// </summary>
    public const string SectionName = "FeatureChangeEvents:Sinks";

    /// <summary>
    /// Enables fan-out of committed feature-change events to registered sinks.
    /// Defaults to <see langword="false"/> so existing deployments are unaffected.
    /// </summary>
    public bool Enabled { get; set; }
}
