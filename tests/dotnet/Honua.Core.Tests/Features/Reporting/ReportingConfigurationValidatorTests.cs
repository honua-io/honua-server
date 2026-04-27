// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Reporting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Reporting;

/// <summary>
/// Verifies that <see cref="ReportingConfigurationValidator"/> enforces the
/// documented contract for the LLM narrative provider when the feature is
/// enabled, including refusing unsupported provider tokens that would be
/// silently ignored at registration time.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class ReportingConfigurationValidatorTests
{
    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_WhenNarrativeDisabled_DoesNotEnforceProviderToken()
    {
        var validator = new ReportingConfigurationValidator();
        var options = new ReportingConfiguration
        {
            Narrative = new ReportingNarrativeConfiguration
            {
                Enabled = false,
                Provider = "anything"
            }
        };

        var result = validator.Validate(name: null, options);

        result.Succeeded.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_WhenNarrativeEnabled_AcceptsCanonicalOpenAiToken()
    {
        var validator = new ReportingConfigurationValidator();
        var options = new ReportingConfiguration
        {
            Narrative = new ReportingNarrativeConfiguration
            {
                Enabled = true,
                Provider = "OpenAI",
                Endpoint = "https://api.openai.com/v1",
                Model = "gpt-4o-mini"
            }
        };

        var result = validator.Validate(name: null, options);

        result.Succeeded.Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void Validate_WhenNarrativeEnabledWithUnsupportedProvider_FailsStartup()
    {
        var validator = new ReportingConfigurationValidator();
        var options = new ReportingConfiguration
        {
            Narrative = new ReportingNarrativeConfiguration
            {
                Enabled = true,
                Provider = "anthropic",
                Endpoint = "https://api.anthropic.com/v1",
                Model = "claude-x"
            }
        };

        var result = validator.Validate(name: null, options);

        result.Succeeded.Should().BeFalse();
        result.Failures.Should().ContainMatch("*Reporting:Narrative:Provider*not supported*");
    }
}
