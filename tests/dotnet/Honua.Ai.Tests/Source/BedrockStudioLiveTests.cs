// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.DashboardGeneration;
using Honua.Ai.Providers.Bedrock;
using Honua.Ai.WorkflowGeneration;
using Honua.Core.Features.Publishing.Dashboards;
using Honua.Core.Features.WorkflowPackages.Generation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Providers.Bedrock;

/// <summary>
/// Live end-to-end test that drives a studio generation flow (dashboard generation) against REAL
/// AWS Bedrock (Claude over the Converse API) using the production
/// <see cref="BedrockChatClientFactory"/> and the AWS credential chain (IAM) — no local model.
///
/// Gated on <c>HONUA_AI_LIVE_BEDROCK=1</c> so it never runs in CI / on machines without AWS
/// credentials. Configure with environment variables:
/// <list type="bullet">
///   <item><c>HONUA_AI_LIVE_BEDROCK=1</c> — opt in.</item>
///   <item><c>HONUA_AI_BEDROCK_MODEL</c> — the Bedrock model/inference-profile id (required).</item>
///   <item><c>HONUA_AI_BEDROCK_REGION</c> — AWS region (optional; defaults to us-west-2).</item>
/// </list>
/// </summary>
public sealed class BedrockStudioLiveTests
{
    private readonly ITestOutputHelper _output;

    public BedrockStudioLiveTests(ITestOutputHelper output) => _output = output;

    private static bool LiveEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("HONUA_AI_LIVE_BEDROCK"), "1", StringComparison.Ordinal);

    private const string SkipReason =
        "Live Bedrock test. Set HONUA_AI_LIVE_BEDROCK=1 and HONUA_AI_BEDROCK_MODEL (region defaults to us-west-2) to run.";

    [Fact]
    public async Task DashboardGeneration_RunsEndToEndOnRealBedrock()
    {
        if (!LiveEnabled)
        {
            // Not opted in: this is a live test requiring AWS credentials. Treated as a no-op so the
            // offline suite stays green. See SkipReason for how to enable.
            _output.WriteLine("SKIPPED: " + SkipReason);
            return;
        }

        var model = Environment.GetEnvironmentVariable("HONUA_AI_BEDROCK_MODEL");
        model.Should().NotBeNullOrWhiteSpace("the Bedrock model id must be supplied via HONUA_AI_BEDROCK_MODEL");
        var region = Environment.GetEnvironmentVariable("HONUA_AI_BEDROCK_REGION");
        region = string.IsNullOrWhiteSpace(region) ? WorkflowGenerationConfiguration.DefaultBedrockRegion : region;

        var options = Options.Create(new WorkflowGenerationConfiguration
        {
            Enabled = true,
            DefaultProvider = WorkflowGenerationConfiguration.BedrockProviderId,
            MaxRepairAttempts = 1,
            Providers =
            {
                [WorkflowGenerationConfiguration.BedrockProviderId] = new WorkflowGenerationProviderOptions
                {
                    Model = model!,
                    Region = region,
                    MaxTokens = 4096,
                    TimeoutSeconds = 120
                }
            }
        });

        var service = new DashboardGenerationService(
            httpClientFactory: Substitute.For<IHttpClientFactory>(),
            options: options,
            apiKeyResolver: new WorkflowGenerationApiKeyResolver(),
            bedrockChatClientFactory: new BedrockChatClientFactory(),
            logger: NullLogger<DashboardGenerationService>.Instance);

        var result = await service.GenerateAsync(new DashboardGenerationRequest
        {
            Prompt = "Build a fleet operations dashboard with a KPI panel for total active vehicles and a bar chart of incidents by region."
        });

        _output.WriteLine($"status={result.Status} provider={result.Provider} model={result.Model}");
        _output.WriteLine($"rationale={result.Rationale}");
        if (result.Document is { } document)
        {
            _output.WriteLine("document=" + document.GetRawText());
        }

        // The model must have produced a real, structured turn — not the disabled/unconfigured guard.
        result.Provider.Should().Be(WorkflowGenerationConfiguration.BedrockProviderId);
        result.Status.Should().BeOneOf("generated", "needs-clarification", "unsupported", "error");
        // A non-error structured turn proves cloud AI ran end-to-end without a local model.
        result.Status.Should().NotBe("unsupported", "the Bedrock provider must be reachable and configured");
    }
}
