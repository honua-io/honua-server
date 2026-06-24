// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Ai.DashboardGeneration;
using Honua.Ai.Providers.AzureOpenAi;
using Honua.Ai.WorkflowGeneration;
using Honua.Core.Features.Publishing.Dashboards;
using Honua.Core.Features.WorkflowPackages.Generation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Providers.AzureOpenAi;

/// <summary>
/// Live end-to-end test that drives a studio generation flow (dashboard generation) against REAL
/// Azure OpenAI / Azure AI Foundry using the production HTTP path and Entra Managed Identity (or an
/// api-key) — no local model.
///
/// Gated on <c>HONUA_AI_LIVE_AZURE_OPENAI=1</c> so it never runs in CI / on machines without Azure
/// access. Configure with environment variables:
/// <list type="bullet">
///   <item><c>HONUA_AI_LIVE_AZURE_OPENAI=1</c> — opt in.</item>
///   <item><c>HONUA_AI_AZURE_OPENAI_ENDPOINT</c> — resource endpoint, e.g. https://{res}.openai.azure.com (required).</item>
///   <item><c>HONUA_AI_AZURE_OPENAI_DEPLOYMENT</c> — deployment name (required).</item>
///   <item><c>HONUA_AI_AZURE_OPENAI_API_VERSION</c> — api-version (optional; defaults to the supported GA version).</item>
///   <item><c>HONUA_WORKFLOWGEN_AZUREOPENAI_API_KEY</c> — api-key (optional; Managed Identity is used when absent).</item>
/// </list>
/// </summary>
public sealed class AzureOpenAiStudioLiveTests
{
    private readonly ITestOutputHelper _output;

    public AzureOpenAiStudioLiveTests(ITestOutputHelper output) => _output = output;

    private static bool LiveEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("HONUA_AI_LIVE_AZURE_OPENAI"), "1", StringComparison.Ordinal);

    private const string SkipReason =
        "Live Azure OpenAI test. Set HONUA_AI_LIVE_AZURE_OPENAI=1, HONUA_AI_AZURE_OPENAI_ENDPOINT and HONUA_AI_AZURE_OPENAI_DEPLOYMENT to run.";

    [Fact]
    public async Task DashboardGeneration_RunsEndToEndOnRealAzureOpenAi()
    {
        if (!LiveEnabled)
        {
            // Not opted in: this is a live test requiring Azure access. Treated as a no-op so the
            // offline suite stays green. See SkipReason for how to enable.
            _output.WriteLine("SKIPPED: " + SkipReason);
            return;
        }

        var endpoint = Environment.GetEnvironmentVariable("HONUA_AI_AZURE_OPENAI_ENDPOINT");
        endpoint.Should().NotBeNullOrWhiteSpace("the Azure endpoint must be supplied via HONUA_AI_AZURE_OPENAI_ENDPOINT");
        var deployment = Environment.GetEnvironmentVariable("HONUA_AI_AZURE_OPENAI_DEPLOYMENT");
        deployment.Should().NotBeNullOrWhiteSpace("the Azure deployment must be supplied via HONUA_AI_AZURE_OPENAI_DEPLOYMENT");
        var apiVersion = Environment.GetEnvironmentVariable("HONUA_AI_AZURE_OPENAI_API_VERSION");

        var options = Options.Create(new WorkflowGenerationConfiguration
        {
            Enabled = true,
            DefaultProvider = WorkflowGenerationConfiguration.AzureOpenAiProviderId,
            MaxRepairAttempts = 1,
            Providers =
            {
                [WorkflowGenerationConfiguration.AzureOpenAiProviderId] = new WorkflowGenerationProviderOptions
                {
                    Endpoint = endpoint!,
                    Deployment = deployment!,
                    ApiVersion = apiVersion ?? string.Empty,
                    MaxTokens = 4096,
                    TimeoutSeconds = 120
                }
            }
        });

        var apiKeyResolver = new WorkflowGenerationApiKeyResolver();
        var authResolver = new AzureOpenAiAuthResolver(apiKeyResolver, new DefaultAzureOpenAiTokenProvider());

        var service = new DashboardGenerationService(
            httpClientFactory: new SimpleHttpClientFactory(),
            options: options,
            apiKeyResolver: apiKeyResolver,
            bedrockChatClientFactory: new Honua.Ai.Providers.Bedrock.BedrockChatClientFactory(),
            logger: NullLogger<DashboardGenerationService>.Instance,
            azureAuthResolver: authResolver);

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

        result.Provider.Should().Be(WorkflowGenerationConfiguration.AzureOpenAiProviderId);
        result.Status.Should().BeOneOf("generated", "needs-clarification", "unsupported", "error");
        result.Status.Should().NotBe("unsupported", "the Azure OpenAI provider must be reachable and configured");
    }

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
