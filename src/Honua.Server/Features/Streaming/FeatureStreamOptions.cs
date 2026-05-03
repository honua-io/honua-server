// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Configuration for real-time feature-change streaming transport.
/// </summary>
public sealed class FeatureStreamOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "FeatureStreaming";

    /// <summary>
    /// Interval between heartbeat frames sent to connected clients.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum number of queued messages per connection before the slow consumer is disconnected.
    /// </summary>
    public int MaxBufferPerConnection { get; set; } = 256;

    /// <summary>
    /// Maximum number of concurrently connected feature-stream sessions.
    /// </summary>
    public int MaxConcurrentSessions { get; set; } = 256;

    /// <summary>
    /// Number of events fetched per batch during cursor-based replay on reconnect.
    /// </summary>
    public int ReplayBatchSize { get; set; } = 200;

    /// <summary>
    /// Interval between shared-store sweeps used to pick up events published on other nodes.
    /// </summary>
    public TimeSpan CrossNodeSyncInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum byte size for an inbound WebSocket control frame (subscribe, unsubscribe, ping).
    /// Frames that exceed this cap are rejected with a client-safe error and the connection
    /// is closed, preventing a malicious client from buffering an unbounded fragmented message.
    /// </summary>
    public int MaxControlFrameBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Maximum number of concurrent subscriptions a single WebSocket session may hold.
    /// Subscribe control frames over this cap are rejected with a client-safe error so a
    /// single connection cannot accumulate unbounded per-session fan-out work.
    /// </summary>
    public int MaxSubscriptionsPerSession { get; set; } = 64;

    /// <summary>
    /// Maximum length of a client-supplied subscription identifier. Subscribe control frames
    /// with longer ids are rejected; the cap protects the per-session state and downstream
    /// logging from unbounded string allocations.
    /// </summary>
    public int MaxSubscriptionIdLength { get; set; } = 128;
}

/// <summary>
/// Validates <see cref="FeatureStreamOptions"/> at startup to surface configuration errors early.
/// </summary>
internal sealed class FeatureStreamOptionsValidator : IValidateOptions<FeatureStreamOptions>
{
    public ValidateOptionsResult Validate(string? name, FeatureStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.HeartbeatInterval <= TimeSpan.Zero)
        {
            failures.Add("FeatureStreaming:HeartbeatInterval must be a positive duration.");
        }

        if (options.MaxBufferPerConnection <= 0)
        {
            failures.Add("FeatureStreaming:MaxBufferPerConnection must be a positive integer.");
        }

        if (options.MaxConcurrentSessions <= 0)
        {
            failures.Add("FeatureStreaming:MaxConcurrentSessions must be a positive integer.");
        }

        if (options.ReplayBatchSize <= 0)
        {
            failures.Add("FeatureStreaming:ReplayBatchSize must be a positive integer.");
        }

        if (options.CrossNodeSyncInterval <= TimeSpan.Zero)
        {
            failures.Add("FeatureStreaming:CrossNodeSyncInterval must be a positive duration.");
        }

        if (options.MaxControlFrameBytes <= 0)
        {
            failures.Add("FeatureStreaming:MaxControlFrameBytes must be a positive integer.");
        }

        if (options.MaxSubscriptionsPerSession <= 0)
        {
            failures.Add("FeatureStreaming:MaxSubscriptionsPerSession must be a positive integer.");
        }

        if (options.MaxSubscriptionIdLength <= 0)
        {
            failures.Add("FeatureStreaming:MaxSubscriptionIdLength must be a positive integer.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
