// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Events;

public sealed class KafkaFeatureChangeEventSinkOptions
{
    public const string SectionName = "FeatureChangeEvents:Sinks:Kafka";

    public bool Enabled { get; set; }

    public string? BootstrapServers { get; set; }

    public string Topic { get; set; } = "honua.feature-changes";

    public string? DeadLetterTopic { get; set; } = "honua.feature-changes.dlq";

    public bool EnableIdempotence { get; set; } = true;

    public int MessageTimeoutMs { get; set; } = 30_000;

    public string? SecurityProtocol { get; set; }

    public string? SaslMechanism { get; set; }

    public string? SaslUsername { get; set; }

    public string? SaslPassword { get; set; }
}

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
