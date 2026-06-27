// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Events;

/// <summary>
/// Configuration for the Kafka feature-change event sink (#357).
/// </summary>
/// <remarks>
/// The Kafka sink is one concrete <see cref="IFeatureChangeEventSink"/> adapter
/// behind the broker-agnostic broadcaster. It is off by default and only wired
/// when both the umbrella sink fan-out (<see cref="FeatureChangeEventSinkOptions.Enabled"/>)
/// and this adapter are enabled. Committed feature-change events are published to
/// <see cref="Topic"/> with an idempotent producer (producer-side exactly-once:
/// no duplicates on retry) keyed by service/layer/feature so per-feature ordering
/// is preserved within a partition. Deliveries that fail after the producer's own
/// retries are routed to <see cref="DeadLetterTopic"/> with diagnostic headers so
/// operators can inspect and replay them.
/// </remarks>
public sealed class KafkaFeatureChangeEventSinkOptions
{
    /// <summary>
    /// Configuration section name bound from application configuration.
    /// </summary>
    public const string SectionName = "FeatureChangeEvents:Sinks:Kafka";

    /// <summary>
    /// Enables the Kafka sink adapter. Defaults to <see langword="false"/> so the
    /// dependency is inert unless explicitly configured.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Comma-separated <c>host:port</c> list of Kafka bootstrap servers.
    /// </summary>
    public string? BootstrapServers { get; set; }

    /// <summary>
    /// Topic that committed feature-change events are published to.
    /// </summary>
    public string Topic { get; set; } = "honua.feature-changes";

    /// <summary>
    /// Topic that deliveries which fail after producer retries are routed to.
    /// When null or empty, dead-lettering is disabled and a terminal produce
    /// failure surfaces to the broadcaster (metered, isolated, never fatal).
    /// </summary>
    public string? DeadLetterTopic { get; set; } = "honua.feature-changes.dlq";

    /// <summary>
    /// Enables the idempotent producer for producer-side exactly-once semantics.
    /// When true the underlying producer is configured with <c>acks=all</c>,
    /// bounded in-flight requests, and infinite retries so a broker-acknowledged
    /// message is never duplicated. Defaults to <see langword="true"/>.
    /// </summary>
    public bool EnableIdempotence { get; set; } = true;

    /// <summary>
    /// Per-message produce timeout in milliseconds. A produce attempt that does
    /// not complete within this window is treated as a delivery failure and, when
    /// configured, routed to the dead-letter topic.
    /// </summary>
    public int MessageTimeoutMs { get; set; } = 30_000;

    /// <summary>
    /// Optional SASL/SSL security protocol passed through to the underlying client
    /// (for example <c>SaslSsl</c>). Null leaves the client default (PLAINTEXT).
    /// </summary>
    public string? SecurityProtocol { get; set; }

    /// <summary>
    /// Optional SASL mechanism (for example <c>Plain</c> or <c>ScramSha512</c>).
    /// </summary>
    public string? SaslMechanism { get; set; }

    /// <summary>
    /// Optional SASL username.
    /// </summary>
    public string? SaslUsername { get; set; }

    /// <summary>
    /// Optional SASL password.
    /// </summary>
    public string? SaslPassword { get; set; }
}

/// <summary>
/// Validates <see cref="KafkaFeatureChangeEventSinkOptions"/> on startup so a
/// misconfigured Kafka sink fails fast rather than silently dropping events.
/// </summary>
internal sealed class KafkaFeatureChangeEventSinkOptionsValidator
    : IValidateOptions<KafkaFeatureChangeEventSinkOptions>
{
    public ValidateOptionsResult Validate(string? name, KafkaFeatureChangeEventSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BootstrapServers))
        {
            failures.Add("Kafka sink BootstrapServers must be configured when the Kafka sink is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.Topic))
        {
            failures.Add("Kafka sink Topic must be configured when the Kafka sink is enabled.");
        }

        if (options.MessageTimeoutMs < 1)
        {
            failures.Add("Kafka sink MessageTimeoutMs must be at least 1 millisecond.");
        }

        if (!string.IsNullOrWhiteSpace(options.DeadLetterTopic) &&
            string.Equals(options.DeadLetterTopic, options.Topic, StringComparison.Ordinal))
        {
            failures.Add("Kafka sink DeadLetterTopic must differ from Topic to avoid redelivery loops.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
