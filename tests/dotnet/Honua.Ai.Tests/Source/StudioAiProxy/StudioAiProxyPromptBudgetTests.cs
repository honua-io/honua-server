// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Ai.Protocols.Mcp.Views;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Abstractions;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Server.Tests.Features.Protocols.Mcp;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.StudioAiProxy;

/// <summary>
/// honua-server#4919: the default <see cref="StudioAiProxyConfiguration.MaxPromptCharacters"/> must admit
/// every round of a model-driven Studio map lifecycle over the setup view
/// (create → update → validate → get → save → reopen → propose). The proxy re-counts every tool
/// definition on every round, so the replay sends the live setup-view Studio descriptors each round and
/// grows the conversation by the tool-result sizes measured on the 548b7a5 candidate
/// (honua-sdk-js#1397, <c>terminal-session-lifecycle:admin-api-key-model</c>).
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class StudioAiProxyPromptBudgetTests
{
    private const string ProviderName = "scripted";
    private const string SystemPrompt = "Scripted qualification turn.";
    private const string UserPrompt =
        "Create the Honolulu map, move its view, validate, save, reopen and submit it for publication.";

    // Message-content characters each tool result added on the candidate: the receipt's per-round
    // content totals were 121, 3808, 5281, 5420, 6893, 11037 and 14952. The candidate stopped before
    // a propose result existed, so its size is an estimate for the closing round.
    private static readonly (string Tool, string Arguments, int ResultCharacters)[] Lifecycle =
    [
        ("honua_studio_create_draft",
            """{"packageKey":"sdk1397-model-run","family":"map","schemaVersion":"1.0","body":{"title":"Honolulu","view":{"center":[-157.8583,21.3069],"zoom":10,"crs":"EPSG:4326"},"layers":[],"widgets":[],"controls":[],"interactions":[]}}""",
            3_687),
        ("honua_studio_update_draft",
            """{"packageKey":"sdk1397-model-run","schemaVersion":"1.0","body":{"title":"Honolulu","view":{"center":[-157.8167,21.2833],"zoom":11,"crs":"EPSG:4326"},"layers":[{"id":"places","title":"Honolulu places","visible":true}],"widgets":[],"controls":[],"interactions":[]},"draftId":"00000000-0000-0000-0000-000000000001","generation":1}""",
            1_473),
        ("honua_studio_validate_draft",
            """{"draftId":"00000000-0000-0000-0000-000000000001","generation":2}""",
            139),
        ("honua_studio_get_draft",
            """{"draftId":"00000000-0000-0000-0000-000000000001","generation":2}""",
            1_473),
        ("honua_studio_save_version",
            """{"changeNote":"honua-sdk-js#1397 candidate replay","draftId":"00000000-0000-0000-0000-000000000001","generation":2}""",
            4_144),
        ("honua_studio_reopen_version",
            """{"itemId":"00000000-0000-0000-0000-000000000002","versionId":"00000000-0000-0000-0000-000000000003","draftId":"00000000-0000-0000-0000-000000000001","generation":2}""",
            3_915),
        ("honua_studio_propose_publication",
            """{"itemId":"00000000-0000-0000-0000-000000000002","versionId":"00000000-0000-0000-0000-000000000003","contentHash":"sha256:0000000000000000000000000000000000000000000000000000000000000000","route":"/studio/sdk1397-model-run","visibility":"personal","note":"honua-sdk-js#1397 candidate replay","draftId":"00000000-0000-0000-0000-000000000004","generation":1}""",
            2_000),
    ];

    private readonly ITestOutputHelper _output;

    public StudioAiProxyPromptBudgetTests(ITestOutputHelper output) => _output = output;

    [UnitTest]
    public async Task ValidateRequest_SetupViewMapLifecycle_EveryRoundFitsTheDefaultLimit()
    {
        var tools = await SetupViewStudioToolsAsync();
        var configuration = Configuration(new StudioAiProxyConfiguration().MaxPromptCharacters);
        var service = CreateService(configuration);
        var messages = new List<StudioAiMessage> { new() { Role = StudioAiRole.User, Content = UserPrompt } };

        // Round N asks the model for step N, carrying the results of steps 1..N-1. One extra round
        // after propose lets the model report the outcome.
        for (var round = 1; round <= Lifecycle.Length + 1; round++)
        {
            var request = Request(messages, tools);
            _output.WriteLine($"round {round}: {messages.Count} messages, {CountedCharacters(request)} counted characters");
            service.ValidateRequest(request).Should().BeNull(
                $"round {round} of the setup-view map lifecycle must reach the provider under the default limit");

            if (round <= Lifecycle.Length)
            {
                AppendStep(messages, round);
            }
        }
    }

    [UnitTest]
    public async Task ValidateRequest_SetupViewMapLifecycle_ReproducesTheCandidateRefusalUnderThePreviousDefault()
    {
        var tools = await SetupViewStudioToolsAsync();
        var service = CreateService(Configuration(32_000));
        var messages = new List<StudioAiMessage> { new() { Role = StudioAiRole.User, Content = UserPrompt } };
        for (var step = 1; step < Lifecycle.Length; step++)
        {
            AppendStep(messages, step);
        }

        // The seventh round (propose) was the one refused on 548b7a5; the replay must be at least
        // that large or the default-limit test above proves nothing about the reported failure.
        service.ValidateRequest(Request(messages, tools)).Should()
            .Be("Request content exceeds the configured limit of 32000 characters.");
    }

    [UnitTest]
    public void ValidateRequest_OverTheDefaultLimit_NamesTheLimit()
    {
        var limit = new StudioAiProxyConfiguration().MaxPromptCharacters;
        var service = CreateService(Configuration(limit));

        var error = service.ValidateRequest(Request(
            [new StudioAiMessage { Role = StudioAiRole.User, Content = new string('x', limit + 1) }],
            tools: []));

        error.Should().Be($"Request content exceeds the configured limit of {limit} characters.");
    }

    private static void AppendStep(List<StudioAiMessage> messages, int step)
    {
        var (tool, arguments, resultCharacters) = Lifecycle[step - 1];
        var callId = "call_" + step;
        messages.Add(new StudioAiMessage
        {
            Role = StudioAiRole.Assistant,
            ToolCalls =
            [
                new StudioAiToolCall
                {
                    Id = callId,
                    Name = tool,
                    Arguments = JsonDocument.Parse(arguments).RootElement.Clone()
                }
            ]
        });
        messages.Add(new StudioAiMessage
        {
            Role = StudioAiRole.Tool,
            ToolCallId = callId,
            ToolName = tool,
            Content = ToolResult(tool, resultCharacters)
        });
    }

    private static string ToolResult(string tool, int characters)
    {
        var prefix = "{\"status\":\"ok\",\"tool\":\"" + tool + "\",\"result\":\"";
        const string suffix = "\"}";
        var padding = characters - prefix.Length - suffix.Length;
        padding.Should().BeGreaterThanOrEqualTo(0);
        return prefix + new string('r', padding) + suffix;
    }

    /// <summary>
    /// The Studio-classified members of the live setup view, exactly as <c>tools/list</c> serves them
    /// and as the SDK forwards them to the proxy (name, description, input schema, annotations,
    /// output schema).
    /// </summary>
    private static async Task<IReadOnlyList<StudioAiToolDefinition>> SetupViewStudioToolsAsync()
    {
        var catalog = new Honua.Core.Features.Operations.Services.OperationCatalog(
            [new Honua.Server.Features.Operations.ServerOperationDescriptorProvider()], TimeProvider.System);
        var descriptors = Honua.Server.Features.Operations.StudioDraftOperations.BuildDescriptors();
        var source = new PublishedOperationToolSource(
            catalog,
            Options.Create(new McpPublishedOperationOptions { Enabled = true }),
            NullLogger<PublishedOperationToolSource>.Instance,
            requestMappers: descriptors.Select(descriptor =>
                new Honua.Server.Features.Operations.StudioDraftApprovalRequestMapper(descriptor.OperationId)));
        var surface = new McpDataAccessSurface(
            tools: McpTaxonomyAlignmentTests.BuildTools(),
            resources: [],
            logger: NullLogger<McpDataAccessSurface>.Instance,
            toolSources: [source]);

        var request = JsonSerializer.Deserialize(
            "{\"jsonrpc\":\"2.0\",\"id\":\"t\",\"method\":\"tools/list\",\"params\":{\"view\":\""
                + McpWorkflowViewCatalog.SetupViewName + "\"}}",
            McpJsonContext.Default.McpJsonRpcRequest)!;
        var response = await surface.DispatchAsync(McpTestFactory.AuthenticatedHttpContext(), request, CancellationToken.None);
        response!.Error.Should().BeNull();

        var tools = response.Result!.Value.GetProperty("tools").EnumerateArray()
            .Where(tool => tool.GetProperty("name").GetString()!.StartsWith("honua_studio_", StringComparison.Ordinal))
            .Select(tool => new StudioAiToolDefinition
            {
                Name = tool.GetProperty("name").GetString()!,
                Description = tool.TryGetProperty("description", out var description) ? description.GetString() : null,
                InputSchema = tool.GetProperty("inputSchema").Clone(),
                Annotations = tool.TryGetProperty("annotations", out var annotations) ? annotations.Clone() : null,
                OutputSchema = tool.TryGetProperty("outputSchema", out var outputSchema) ? outputSchema.Clone() : null
            })
            .ToList();

        tools.Select(tool => tool.Name).Should().Contain(Lifecycle.Select(step => step.Tool),
            "the replay must carry every Studio descriptor the lifecycle calls");
        return tools;
    }

    private static StudioAiChatRequest Request(IReadOnlyList<StudioAiMessage> messages, IReadOnlyList<StudioAiToolDefinition> tools) => new()
    {
        System = SystemPrompt,
        Messages = messages.ToList(),
        Tools = tools
    };

    private static long CountedCharacters(StudioAiChatRequest request)
        => request.Messages.Sum(message => (long)message.Content.Length + (message.ToolCallId?.Length ?? 0) + (message.ToolName?.Length ?? 0)
            + (message.ToolCalls?.Sum(call => call.Id.Length + call.Name.Length + call.Arguments.GetRawText().Length) ?? 0))
            + (request.System?.Length ?? 0)
            + (request.Tools?.Sum(tool => (long)tool.Name.Length + (tool.Description?.Length ?? 0) + tool.InputSchema.GetRawText().Length
                + (tool.Annotations?.GetRawText().Length ?? 0) + (tool.OutputSchema?.GetRawText().Length ?? 0)) ?? 0);

    private static StudioAiProxyConfiguration Configuration(int maxPromptCharacters) => new()
    {
        Enabled = true,
        DefaultProvider = ProviderName,
        MaxPromptCharacters = maxPromptCharacters,
        Providers =
        {
            [ProviderName] = new StudioAiProxyProviderOptions
            {
                Kind = StudioAiProxyConfiguration.OpenAiKind,
                Endpoint = "https://llm.example.com/v1",
                Model = "scripted-model",
                ApiKey = "test-key",
                SupportsTools = true
            }
        }
    };

    private static StudioAiProxyService CreateService(StudioAiProxyConfiguration configuration)
        => new(
            Options.Create(configuration),
            [new ConfiguredAdapter()],
            new StudioAiTranscriptSigner(Options.Create(configuration), TimeProvider.System),
            NullLogger<StudioAiProxyService>.Instance);

    private sealed class ConfiguredAdapter : IStudioAiProxyAdapter
    {
        public string Kind => StudioAiProxyConfiguration.OpenAiKind;

        public bool IsConfigured(string providerName, StudioAiProxyProviderOptions options) => true;

        public IAsyncEnumerable<StudioAiChatEvent> StreamAsync(
            StudioAiProxyProviderOptions options,
            StudioAiChatRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("Validation-only adapter.");
    }
}
