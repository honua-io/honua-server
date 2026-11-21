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

        failures.AddRange(SecurityFailures(options));

        if (!string.IsNullOrWhiteSpace(options.DeadLetterTopic) &&
            string.Equals(options.DeadLetterTopic, options.Topic, StringComparison.Ordinal))
        {
            failures.Add("Kafka sink DeadLetterTopic must differ from Topic to avoid redelivery loops.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    // An unparseable protocol used to fall back to PLAINTEXT silently, which with SASL PLAIN or SCRAM
    // put the password on the wire in clear; both cases now fail instead.
    internal static IReadOnlyList<string> SecurityFailures(KafkaFeatureChangeEventSinkOptions options)
    {
        var failures = new List<string>();
        var protocol = KafkaSecurityNames.NormalizeProtocol(options.SecurityProtocol);
        if (!string.IsNullOrWhiteSpace(options.SecurityProtocol) && protocol is null)
        {
            failures.Add("Kafka sink SecurityProtocol must be one of Plaintext, Ssl, SaslPlaintext or SaslSsl.");
        }

        if (!string.IsNullOrWhiteSpace(options.SaslMechanism) &&
            KafkaSecurityNames.NormalizeMechanism(options.SaslMechanism) is null)
        {
            failures.Add("Kafka sink SaslMechanism must be one of Gssapi, Plain, ScramSha256, ScramSha512 or OAuthBearer.");
        }

        if (UsesSasl(options, protocol) && protocol != KafkaSecurityNames.SaslSsl)
        {
            failures.Add("Kafka sink SASL settings require SecurityProtocol SaslSsl so credentials are never sent in clear text.");
        }

        return failures;
    }

    private static bool UsesSasl(KafkaFeatureChangeEventSinkOptions options, string? protocol)
        => protocol == KafkaSecurityNames.SaslPlaintext ||
            !string.IsNullOrWhiteSpace(options.SaslMechanism) ||
            !string.IsNullOrWhiteSpace(options.SaslUsername) ||
            !string.IsNullOrWhiteSpace(options.SaslPassword);
}

// Kept free of Confluent types so validation still compiles when the event brokers are excluded.
// Both the Confluent enum names (SaslSsl, ScramSha256) and the librdkafka spellings
// (SASL_SSL, SCRAM-SHA-256) are accepted because operators copy either from broker docs.
internal static class KafkaSecurityNames
{
    public const string SaslPlaintext = "SaslPlaintext";

    public const string SaslSsl = "SaslSsl";

    private static readonly string[] Protocols = ["Plaintext", "Ssl", SaslPlaintext, SaslSsl];

    private static readonly string[] Mechanisms = ["Gssapi", "Plain", "ScramSha256", "ScramSha512", "OAuthBearer"];

    public static string? NormalizeProtocol(string? value) => Normalize(value, Protocols);

    public static string? NormalizeMechanism(string? value) => Normalize(value, Mechanisms);

    private static string? Normalize(string? value, string[] names)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var compact = value.Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return Array.Find(names, name => string.Equals(name, compact, StringComparison.OrdinalIgnoreCase));
    }
}
