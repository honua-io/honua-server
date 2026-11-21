// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Events;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Streaming;

public sealed class KafkaSinkSecurityOptionsTests
{
    [UnitTheory]
    [InlineData("SaslSsl", "SaslSsl")]
    [InlineData("SASL_SSL", "SaslSsl")]
    [InlineData("sasl_plaintext", "SaslPlaintext")]
    [InlineData("ssl", "Ssl")]
    [InlineData("PLAINTEXT", "Plaintext")]
    public void NormalizeProtocol_AcceptsEnumAndLibrdkafkaSpellings(string configured, string expected)
    {
        KafkaSecurityNames.NormalizeProtocol(configured).Should().Be(expected);
    }

    [UnitTheory]
    [InlineData("SCRAM-SHA-512", "ScramSha512")]
    [InlineData("PLAIN", "Plain")]
    [InlineData("OAUTHBEARER", "OAuthBearer")]
    public void NormalizeMechanism_AcceptsEnumAndLibrdkafkaSpellings(string configured, string expected)
    {
        KafkaSecurityNames.NormalizeMechanism(configured).Should().Be(expected);
    }

    [UnitTest]
    public void Validate_UnparseableSecurityProtocol_Fails()
    {
        var result = Validate(new KafkaFeatureChangeEventSinkOptions
        {
            Enabled = true,
            BootstrapServers = "kafka:9093",
            SecurityProtocol = "SSL_SASL_TLS",
        });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("SecurityProtocol");
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("Plaintext")]
    [InlineData("Ssl")]
    [InlineData("SaslPlaintext")]
    public void Validate_SaslPasswordWithoutSaslSsl_Fails(string? protocol)
    {
        var result = Validate(SaslOptions(protocol));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("SaslSsl");
    }

    [UnitTest]
    public void Validate_SaslOverSaslSsl_Succeeds()
    {
        Validate(SaslOptions("SASL_SSL")).Succeeded.Should().BeTrue();
    }

    [UnitTest]
    public void Validate_UnparseableSaslMechanism_Fails()
    {
        var options = SaslOptions("SaslSsl");
        options.SaslMechanism = "SCRAM-SHA-1024";

        var result = Validate(options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("SaslMechanism");
    }

    [UnitTest]
    public void Validate_NoSecuritySettings_StillSucceeds()
    {
        Validate(new KafkaFeatureChangeEventSinkOptions
        {
            Enabled = true,
            BootstrapServers = "kafka:9092",
        }).Succeeded.Should().BeTrue();
    }

    [UnitTest]
    public void Producer_SaslWithoutSsl_RefusesBeforeBuildingClient()
    {
        var act = () => new ConfluentKafkaEventProducer(Options.Create(SaslOptions("Plaintext")));

        act.Should().Throw<InvalidOperationException>().Where(ex => ex.Message.Contains("SaslSsl"));
    }

    private static KafkaFeatureChangeEventSinkOptions SaslOptions(string? protocol) => new()
    {
        Enabled = true,
        BootstrapServers = "kafka:9093",
        SecurityProtocol = protocol,
        SaslMechanism = "PLAIN",
        SaslUsername = "honua",
        SaslPassword = "s3cret",
    };

    private static ValidateOptionsResult Validate(KafkaFeatureChangeEventSinkOptions options)
        => new KafkaFeatureChangeEventSinkOptionsValidator().Validate(Options.DefaultName, options);
}
