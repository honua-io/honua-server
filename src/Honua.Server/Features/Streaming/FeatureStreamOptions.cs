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
    /// Maximum concurrent feature-stream sessions per tenant, or per principal when tenancy is off.
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

    /// <summary>
    /// Maximum number of features emitted in one baseline snapshot for a snapshot-then-delta
    /// subscription. Reaching the cap ends the snapshot with <c>complete: false</c> so the
    /// client never treats a truncated baseline as authoritative state.
    /// </summary>
    public int MaxSnapshotFeatures { get; set; } = 5000;

    /// <summary>
    /// Maximum number of stored rows scanned while building one baseline snapshot. The
    /// subscription predicate is evaluated in-process against the same admission rules the
    /// delta path uses, so this bounds the work a low-selectivity filter can cause.
    /// </summary>
    public int MaxSnapshotScanRows { get; set; } = 20000;

    /// <summary>
    /// Maximum serialized payload, in bytes, that one baseline snapshot may emit before it is
    /// truncated with <c>complete: false</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="MaxSnapshotFeatures"/> bounds the feature COUNT, which says nothing about the
    /// response size: on the demo seed the same 5000-feature cap yields a ~6.6 MB baseline for
    /// parcel multipolygons but ~3.3 MB for building footprints, so geometry complexity alone
    /// moves the payload by a factor of two. Deployments that buffer the whole response before returning it
    /// (API gateways and serverless function URLs typically cap an invoke response at 6 MB)
    /// discard an over-large 200 and substitute their own untyped 500, so a baseline the server
    /// considered successful never reaches the client (honua-server#3181). Bounding the payload
    /// keeps every advertised layer servable and moves the truncation into the protocol's own
    /// fail-closed <c>complete: false</c> contract, where the client can see it.
    /// </remarks>
    public int MaxSnapshotBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Number of features read per page while building a baseline snapshot.
    /// </summary>
    public int SnapshotPageSize { get; set; } = 500;
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

        if (options.MaxSnapshotFeatures <= 0)
        {
            failures.Add("FeatureStreaming:MaxSnapshotFeatures must be a positive integer.");
        }

        if (options.MaxSnapshotScanRows < options.MaxSnapshotFeatures)
        {
            failures.Add("FeatureStreaming:MaxSnapshotScanRows must be greater than or equal to MaxSnapshotFeatures.");
        }

        if (options.MaxSnapshotBytes <= 0)
        {
            failures.Add("FeatureStreaming:MaxSnapshotBytes must be a positive integer.");
        }

        if (options.SnapshotPageSize <= 0)
        {
            failures.Add("FeatureStreaming:SnapshotPageSize must be a positive integer.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
