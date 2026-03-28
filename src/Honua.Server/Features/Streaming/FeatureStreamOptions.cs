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
    /// Number of events fetched per batch during cursor-based replay on reconnect.
    /// </summary>
    public int ReplayBatchSize { get; set; } = 200;
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

        if (options.ReplayBatchSize <= 0)
        {
            failures.Add("FeatureStreaming:ReplayBatchSize must be a positive integer.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
