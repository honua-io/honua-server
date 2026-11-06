// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Streaming;

public sealed class FeatureStreamOptions
{
    public const string SectionName = "FeatureStreaming";

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxBufferPerConnection { get; set; } = 256;

    public int MaxConcurrentSessions { get; set; } = 256;

    public int ReplayBatchSize { get; set; } = 200;

    public TimeSpan CrossNodeSyncInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan SubscriberPolicyRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxControlFrameBytes { get; set; } = 64 * 1024;

    // Subscribe frames over the cap are rejected so one connection cannot grow unbounded fan-out.
    public int MaxSubscriptionsPerSession { get; set; } = 64;

    public int MaxSubscriptionIdLength { get; set; } = 128;

    // Reaching the feature cap ends the snapshot incomplete.
    public int MaxSnapshotFeatures { get; set; } = 5000;

    public int MaxSnapshotScanRows { get; set; } = 20000;

    // The byte cap exists because a feature count does not bound the response, and an oversized body becomes an untyped gateway error.
    public int MaxSnapshotBytes { get; set; } = 4 * 1024 * 1024;

    public int SnapshotPageSize { get; set; } = 500;
}

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

        if (options.SubscriberPolicyRefreshInterval <= TimeSpan.Zero)
        {
            failures.Add("FeatureStreaming:SubscriberPolicyRefreshInterval must be a positive duration.");
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
