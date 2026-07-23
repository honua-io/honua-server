// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Providers.AzureOpenAi;
using Honua.Ai.WorkflowGeneration;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Core.Features.WorkflowPackages.Generation;
using Honua.Core.Features.WorkflowPackages.Generation.Abstractions;
using Honua.Core.Features.WorkflowPackages.Generation.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Providers.AzureOpenAi;

/// <summary>
/// Offline wiring tests for the Azure OpenAI / Azure AI Foundry provider that backs the studio
/// generation flows. These prove — without a live Azure endpoint (the HTTP layer is mocked) — that:
/// <list type="bullet">
///   <item>The shared config accepts the <c>azureopenai</c> provider id with endpoint + deployment
///   and no api-key (Entra Managed Identity is the preferred auth).</item>
///   <item>The deployment-routed URL and the api-key / Entra auth header are built correctly.</item>
///   <item>The workflow provider maps a strict json_schema completion into the proposal contract.</item>
///   <item>A truncated completion (<c>finish_reason=length</c>) surfaces an actionable error rather
///   than an opaque deserialize failure.</item>
/// </list>
/// A live end-to-end test against real Azure OpenAI lives in <c>AzureOpenAiStudioLiveTests</c>,
/// gated on <c>HONUA_AI_LIVE_AZURE_OPENAI=1</c>.
/// </summary>
public sealed class AzureOpenAiStudioProviderTests
{
    private const string Endpoint = "https://contoso.openai.azure.com";

    [UnitTest]
    public void ConfigurationValidator_AcceptsAzureOpenAiWithEndpointAndDeployment_NoKey()
    {
        var validator = new WorkflowGenerationConfigurationValidator();
        var options = new WorkflowGenerationConfiguration
        {
            Enabled = true,
            DefaultProvider = WorkflowGenerationConfiguration.AzureOpenAiProviderId,
            Providers =
            {
                [WorkflowGenerationConfiguration.AzureOpenAiProviderId] = new WorkflowGenerationProviderOptions
                {
                    // No api-key — Azure uses Entra Managed Identity by default.
                    Endpoint = Endpoint,
                    Deployment = "gpt-4o",
                    ApiVersion = "2024-10-21"
                }
            }
        };

        var result = validator.Validate(name: null, options);

        result.Succeeded.Should().BeTrue(because: "Azure needs only an endpoint + deployment; Managed Identity supplies auth");
    }

    [UnitTest]
    public void ConfigurationValidator_RejectsAzureOpenAiWithoutEndpoint()
    {
        var validator = new WorkflowGenerationConfigurationValidator();
        var options = new WorkflowGenerationConfiguration
        {
            Enabled = true,
            DefaultProvider = WorkflowGenerationConfiguration.AzureOpenAiProviderId,
            Providers =
            {
                [WorkflowGenerationConfiguration.AzureOpenAiProviderId] = new WorkflowGenerationProviderOptions
                {
                    Deployment = "gpt-4o"
                }
            }
        };

        var result = validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
    }

    [UnitTest]
    public void ConfigurationValidator_RejectsAzureOpenAiWithoutDeploymentOrModel()
    {
        var validator = new WorkflowGenerationConfigurationValidator();
        var options = new WorkflowGenerationConfiguration
        {
            Enabled = true,
            DefaultProvider = WorkflowGenerationConfiguration.AzureOpenAiProviderId,
            Providers =
            {
                [WorkflowGenerationConfiguration.AzureOpenAiProviderId] = new WorkflowGenerationProviderOptions
                {
                    Endpoint = Endpoint
                }
            }
        };

        var result = validator.Validate(name: null, options);

        result.Failed.Should().BeTrue();
    }

    [UnitTest]
    public void Endpoint_BuildsDeploymentRoutedUriWithApiVersion()
    {
        var uri = AzureOpenAiEndpoint.BuildChatCompletionsUri(
            "https://contoso.openai.azure.com/",
            "gpt-4o",
            "2024-10-21");

        uri.Should().Be("https://contoso.openai.azure.com/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21");
    }

    [UnitTest]
    public void AzureOpenAiWorkflowProvider_IsConfigured_WhenEndpointAndDeploymentAreSet()
    {
        var configured = NewProvider(new StubHttpClientFactory(new StubChatHandler(["{}"])), DefaultOptions());

        configured.IsConfigured.Should().BeTrue();
        configured.ProviderId.Should().Be(WorkflowGenerationConfiguration.AzureOpenAiProviderId);

        var unconfigured = NewProvider(
            new StubHttpClientFactory(new StubChatHandler(["{}"])),
            Options.Create(new WorkflowGenerationConfiguration { Enabled = true }));

        unconfigured.IsConfigured.Should().BeFalse();
    }

    [UnitTest]
    public async Task Generate_WithKeyAuth_SendsApiKeyHeaderToDeploymentUrl_AndMapsProposal()
    {
        const string Proposal = """
        { "status": "needs-clarification", "rationale": "Which fleet region should this workflow target?", "clarifications": [] }
        """;

        var handler = new StubChatHandler([WrapAsChatCompletion(Proposal, finishReason: "stop")]);
        var provider = NewProvider(new StubHttpClientFactory(handler), KeyAuthOptions());

        var proposal = await provider.GenerateAsync(Request());

        proposal.Status.Should().Be(WorkflowGenerationStatus.NeedsClarification);
        proposal.ProviderId.Should().Be(WorkflowGenerationConfiguration.AzureOpenAiProviderId);
        proposal.Model.Should().Be("gpt-4o");

        // The request was routed to the deployment URL and authenticated with the api-key header.
        handler.LastRequestUri.Should().Be(
            "https://contoso.openai.azure.com/openai/deployments/gpt-4o-deploy/chat/completions?api-version=2024-10-21");
        handler.LastApiKeyHeader.Should().Be("test-azure-key");
        handler.LastAuthorizationHeader.Should().BeNull();
    }

    [UnitTest]
    public async Task Generate_WithEntraToken_SendsBearerHeader_WhenNoKeyConfigured()
    {
        const string Proposal = """
        { "status": "needs-clarification", "rationale": "Clarify the schedule.", "clarifications": [] }
        """;

        var handler = new StubChatHandler([WrapAsChatCompletion(Proposal, finishReason: "stop")]);
        var provider = NewProvider(
            new StubHttpClientFactory(handler),
            DefaultOptions(),
            tokenProvider: new StubTokenProvider("entra-access-token"));

        var proposal = await provider.GenerateAsync(Request());

        proposal.Status.Should().Be(WorkflowGenerationStatus.NeedsClarification);
        handler.LastAuthorizationHeader.Should().Be("Bearer entra-access-token");
        handler.LastApiKeyHeader.Should().BeNull();
    }

    [UnitTest]
    public async Task Generate_WhenCompletionTruncated_ReturnsActionableError()
    {
        // A "length" finish_reason means max_tokens was reached and the JSON is truncated.
        var handler = new StubChatHandler([WrapAsChatCompletion("{ \"status\": \"gener", finishReason: "length")]);
        var provider = NewProvider(new StubHttpClientFactory(handler), KeyAuthOptions());

        var proposal = await provider.GenerateAsync(Request());

        proposal.Status.Should().Be(WorkflowGenerationStatus.Error);
        proposal.ErrorMessage.Should().Contain("truncated");
        proposal.ErrorMessage.Should().Contain("MaxTokens");
    }

    [UnitTest]
    public async Task Generate_WhenNoCredential_ReturnsAuthenticationError()
    {
        var handler = new StubChatHandler(["{}"], throwIfCalled: true);
        var provider = NewProvider(
            new StubHttpClientFactory(handler),
            DefaultOptions(),
            tokenProvider: new StubTokenProvider(token: null));

        var proposal = await provider.GenerateAsync(Request());

        proposal.Status.Should().Be(WorkflowGenerationStatus.Error);
        proposal.ErrorMessage.Should().Contain("not authenticated");
    }

    // -- helpers --------------------------------------------------------------------------

    private static AzureOpenAiWorkflowGenerationProvider NewProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<WorkflowGenerationConfiguration> options,
        IAzureOpenAiTokenProvider? tokenProvider = null)
    {
        var apiKeyResolver = new WorkflowGenerationApiKeyResolver();
        var authResolver = new AzureOpenAiAuthResolver(
            apiKeyResolver,
            tokenProvider ?? new StubTokenProvider(token: null));
        return new AzureOpenAiWorkflowGenerationProvider(
            WorkflowGenerationConfiguration.AzureOpenAiProviderId,
            httpClientFactory,
            authResolver,
            options,
            NullLogger<AzureOpenAiWorkflowGenerationProvider>.Instance);
    }

    private static IOptions<WorkflowGenerationConfiguration> DefaultOptions() =>
        Options.Create(new WorkflowGenerationConfiguration
        {
            Enabled = true,
            DefaultProvider = WorkflowGenerationConfiguration.AzureOpenAiProviderId,
            MaxRepairAttempts = 0,
            Providers =
            {
                [WorkflowGenerationConfiguration.AzureOpenAiProviderId] = new WorkflowGenerationProviderOptions
                {
                    Endpoint = Endpoint,
                    Model = "gpt-4o",
                    Deployment = "gpt-4o-deploy",
                    ApiVersion = "2024-10-21",
                    MaxTokens = 2048,
                    TimeoutSeconds = 30
                }
            }
        });

    private static IOptions<WorkflowGenerationConfiguration> KeyAuthOptions()
    {
        var options = DefaultOptions();
        options.Value.GetProvider(WorkflowGenerationConfiguration.AzureOpenAiProviderId)!.ApiKey = "test-azure-key";
        return options;
    }

    private static WorkflowGenerationProviderRequest Request() => new()
    {
        Prompt = "Copy features then calculate a field",
        Registry = Snapshot()
    };

    private static WorkflowNodeRegistrySnapshot Snapshot() => new()
    {
        RegistryVersion = "test-registry-1",
        GeneratedAt = DateTimeOffset.UnixEpoch,
        Providers = [],
        Nodes =
        [
            Node("process:data-management.copy-features"),
            Node("process:geometry.area")
        ]
    };

    private static WorkflowNodeDefinition Node(string nodeTypeId) => new()
    {
        NodeTypeId = nodeTypeId,
        ProviderId = "test",
        RuntimeKind = WorkflowNodeRuntimeKind.Geoprocessing,
        Title = nodeTypeId,
        Description = nodeTypeId,
        Category = "transform",
        ParameterSchemas = [],
        CapabilityFlags = new WorkflowNodeCapabilityFlags { Executable = true },
        RuntimeHints = new WorkflowNodeRuntimeHints()
    };

    /// <summary>Wrap a model proposal JSON in an OpenAI-compatible chat-completion envelope.</summary>
    private static string WrapAsChatCompletion(string proposalContent, string finishReason)
    {
        var content = JsonSerializer.Serialize(proposalContent);
        return $$"""
            { "id": "chatcmpl-test", "choices": [ { "index": 0, "message": { "role": "assistant", "content": {{content}} }, "finish_reason": "{{finishReason}}" } ], "usage": { "prompt_tokens": 10, "completion_tokens": 20, "total_tokens": 30 } }
            """;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubChatHandler(IReadOnlyList<string> responses, bool throwIfCalled = false) : HttpMessageHandler
    {
        private int _call;

        public string? LastRequestUri { get; private set; }

        public string? LastApiKeyHeader { get; private set; }

        public string? LastAuthorizationHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (throwIfCalled)
            {
                throw new InvalidOperationException("The provider must not call Azure when no credential is available.");
            }

            LastRequestUri = request.RequestUri?.ToString();
            LastApiKeyHeader = request.Headers.TryGetValues("api-key", out var values) ? values.FirstOrDefault() : null;
            LastAuthorizationHeader = request.Headers.Authorization?.ToString();

            var index = Math.Min(_call, responses.Count - 1);
            _call++;
            var body = responses.Count == 0 ? "{}" : responses[index];
            // Ownership transfers to the HttpClient pipeline, which disposes the response.
            // codeql[cs/local-not-disposed] -- ownership transfers to the returned or containing disposable object.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubTokenProvider(string? token) : IAzureOpenAiTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(token);
    }
}
