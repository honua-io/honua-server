// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Events;

/// <summary>
/// Configuration for the NATS JetStream feature-change event sink (#357).
/// </summary>
/// <remarks>
/// The NATS sink is one concrete <see cref="IFeatureChangeEventSink"/> adapter
/// behind the broker-agnostic broadcaster. It is off by default and only wired
/// when both the umbrella sink fan-out (<see cref="FeatureChangeEventSinkOptions.Enabled"/>)
/// and this adapter are enabled. Committed feature-change events are published to a
/// JetStream <see cref="Subject"/>; when <see cref="EnableDeduplication"/> is set
/// each message carries a <c>Nats-Msg-Id</c> equal to the event id so JetStream
/// rejects duplicates within the stream's dedup window (publish-side exactly-once).
/// Deliveries that fail are routed to <see cref="DeadLetterSubject"/> with
/// diagnostic headers so operators can inspect and replay them. JetStream
/// preserves per-subject ordering, so all mutations published to the same subject
/// retain their commit order.
/// </remarks>
public sealed class NatsFeatureChangeEventSinkOptions
{
    /// <summary>
    /// Configuration section name bound from application configuration.
    /// </summary>
    public const string SectionName = "FeatureChangeEvents:Sinks:Nats";

    /// <summary>
    /// Enables the NATS sink adapter. Defaults to <see langword="false"/> so the
    /// dependency is inert unless explicitly configured.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Comma-separated NATS server URL list (for example <c>nats://localhost:4222</c>).
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// JetStream subject that committed feature-change events are published to.
    /// </summary>
    public string Subject { get; set; } = "honua.feature-changes";

    /// <summary>
    /// Subject that deliveries which fail are routed to. When null or empty,
    /// dead-lettering is disabled and a terminal publish failure surfaces to the
    /// broadcaster (metered, isolated, never fatal).
    /// </summary>
    public string? DeadLetterSubject { get; set; } = "honua.feature-changes.dlq";

    /// <summary>
    /// Enables JetStream message deduplication for publish-side exactly-once
    /// semantics. When true each message is published with a <c>Nats-Msg-Id</c>
    /// equal to the event id, so a redelivery within the stream's dedup window is
    /// rejected as a duplicate. Defaults to <see langword="true"/>.
    /// </summary>
    public bool EnableDeduplication { get; set; } = true;

    /// <summary>
    /// Per-message publish timeout in milliseconds. A publish attempt that does not
    /// complete within this window is treated as a delivery failure and, when
    /// configured, routed to the dead-letter subject.
    /// </summary>
    public int PublishTimeoutMs { get; set; } = 30_000;

    /// <summary>
    /// Optional path to a NATS credentials (<c>.creds</c>) file for JWT/NKey auth.
    /// </summary>
    public string? CredsFile { get; set; }

    /// <summary>
    /// Optional bearer token used for token authentication.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// Optional username for user/password authentication.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Optional password for user/password authentication.
    /// </summary>
    public string? Password { get; set; }
}

/// <summary>
/// Validates <see cref="NatsFeatureChangeEventSinkOptions"/> on startup so a
/// misconfigured NATS sink fails fast rather than silently dropping events.
/// </summary>
internal sealed class NatsFeatureChangeEventSinkOptionsValidator
    : IValidateOptions<NatsFeatureChangeEventSinkOptions>
{
    public ValidateOptionsResult Validate(string? name, NatsFeatureChangeEventSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Url))
        {
            failures.Add("NATS sink Url must be configured when the NATS sink is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.Subject))
        {
            failures.Add("NATS sink Subject must be configured when the NATS sink is enabled.");
        }

        if (options.PublishTimeoutMs < 1)
        {
            failures.Add("NATS sink PublishTimeoutMs must be at least 1 millisecond.");
        }

        if (!string.IsNullOrWhiteSpace(options.DeadLetterSubject) &&
            string.Equals(options.DeadLetterSubject, options.Subject, StringComparison.Ordinal))
        {
            failures.Add("NATS sink DeadLetterSubject must differ from Subject to avoid redelivery loops.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
