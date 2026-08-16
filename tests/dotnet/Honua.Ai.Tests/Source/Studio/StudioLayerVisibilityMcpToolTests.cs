// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Interface-level integration tests for <c>honua_studio_set_layer_visibility</c>
/// (honua-server#3199), driven through the real <c>POST /mcp</c> JSON-RPC surface and
/// read back through the REST Studio draft surface a client actually syncs from
/// (<c>GET /api/v1/studio/package-drafts/{draftId}</c>).
/// </summary>
/// <remarks>
/// The defect this covers is a persistence gap, not a handler bug: <c>visible</c> is part of
/// the stored <c>StudioCompositionLayer</c> wire shape but <c>honua_studio_add_layer</c> was its
/// only writer (and rejects duplicate ids), so a client-local table-of-contents toggle was
/// overwritten by the next draft sync. Proving that requires the real write path AND the real
/// read path, which is why these run against the composed host rather than a tool harness.
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class StudioLayerVisibilityMcpToolTests : IAsyncLifetime
{
    private const string JsonMediaType = "application/json";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureServices(services =>
        {
            // Same in-memory Studio store StudioPackageEndpointsTests uses: this suite exercises
            // the HTTP write/read round trip, not the Postgres store's persistence.
            services.RemoveAll<IStudioPackageStore>();
            services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
        });

    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp")]
    [Endpoint("GET /api/v1/studio/package-drafts/{draftId}")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task SetLayerVisibility_AfterToolCall_SurvivesADraftSyncReadBack()
    {
        var draft = await CreateMapDraftWithParcelsLayerAsync();

        // The layer starts visible (addLayer's default), so the toggle is a real change.
        var beforeSync = await SyncCompositionAsync(draft.DraftId);
        LayerVisibility(beforeSync, "parcels").Should().BeTrue();

        var toggled = await CallToolAsync(
            "honua_studio_set_layer_visibility",
            $$"""{"draftId":"{{draft.DraftId}}","generation":{{draft.Generation}},"layerId":"parcels","visible":false}""");
        toggled.GetProperty("generation").GetInt64().Should().Be(draft.Generation + 1);

        // The acceptance criterion: re-reading the draft through the normal sync path reports
        // the toggle, rather than clobbering it with the stored value.
        var afterSync = await SyncCompositionAsync(draft.DraftId);
        LayerVisibility(afterSync, "parcels").Should().BeFalse();

        // Re-showing round-trips too, and the sibling layer is untouched.
        var restored = await CallToolAsync(
            "honua_studio_set_layer_visibility",
            $$"""{"draftId":"{{draft.DraftId}}","generation":{{draft.Generation + 1}},"layerId":"parcels","visible":true}""");
        restored.GetProperty("generation").GetInt64().Should().Be(draft.Generation + 2);

        var finalSync = await SyncCompositionAsync(draft.DraftId);
        LayerVisibility(finalSync, "parcels").Should().BeTrue();
        LayerVisibility(finalSync, "zoning").Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task SetLayerVisibility_PreservesTheLayersStyleBindingAndSourceThroughSync()
    {
        var draft = await CreateMapDraftWithParcelsLayerAsync();
        var styled = await CallToolAsync(
            "honua_studio_set_layer_style",
            $$"""{"draftId":"{{draft.DraftId}}","generation":{{draft.Generation}},"layerId":"parcels","styleRef":"style_parcels_default"}""");
        var generation = styled.GetProperty("generation").GetInt64();

        await CallToolAsync(
            "honua_studio_set_layer_visibility",
            $$"""{"draftId":"{{draft.DraftId}}","generation":{{generation}},"layerId":"parcels","visible":false}""");

        var body = await SyncCompositionAsync(draft.DraftId);
        var layer = body.Layers.Single(l => l.Id == "parcels");
        layer.Visible.Should().BeFalse();
        layer.StyleRef.Should().Be("style_parcels_default", "hiding a layer must not drop its style binding");
        layer.SourceId.Should().Be("content.parcels");
    }

    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task SetLayerVisibility_WhenGenerationIsStale_SurfacesFailedPreconditionLikeSiblingCompositionTools()
    {
        var draft = await CreateMapDraftWithParcelsLayerAsync();

        // Consume the current generation with one accepted call...
        await CallToolAsync(
            "honua_studio_set_layer_visibility",
            $$"""{"draftId":"{{draft.DraftId}}","generation":{{draft.Generation}},"layerId":"parcels","visible":false}""");

        // ...then replay it. Identical to how honua_studio_set_layer_style rejects a stale
        // generation: a typed failed_precondition inside the result envelope, not a silent
        // clobber of the concurrent edit.
        var stale = await CallToolRawAsync(
            "honua_studio_set_layer_visibility",
            $$"""{"draftId":"{{draft.DraftId}}","generation":{{draft.Generation}},"layerId":"parcels","visible":true}""");
        stale.GetProperty("isError").GetBoolean().Should().BeTrue();
        stale.GetProperty("structuredContent").GetProperty("code").GetString().Should().Be("failed_precondition");

        var siblingStale = await CallToolRawAsync(
            "honua_studio_set_layer_style",
            $$"""{"draftId":"{{draft.DraftId}}","generation":{{draft.Generation}},"layerId":"parcels","styleRef":"style_x"}""");
        siblingStale.GetProperty("structuredContent").GetProperty("code").GetString()
            .Should().Be("failed_precondition");

        // Rejected, not partially applied: the draft still holds the first (accepted) toggle.
        LayerVisibility(await SyncCompositionAsync(draft.DraftId), "parcels").Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task SetLayerVisibility_WithUnknownLayerOrMissingVisible_SurfacesTypedToolErrors()
    {
        var draft = await CreateMapDraftWithParcelsLayerAsync();

        var unknownLayer = await CallToolRawAsync(
            "honua_studio_set_layer_visibility",
            $$"""{"draftId":"{{draft.DraftId}}","generation":{{draft.Generation}},"layerId":"no-such-layer","visible":false}""");
        unknownLayer.GetProperty("isError").GetBoolean().Should().BeTrue();
        unknownLayer.GetProperty("structuredContent").GetProperty("code").GetString().Should().Be("not_found");

        // MCP dispatch never evaluates the advertised inputSchema, so the required 'visible' is
        // enforced in the handler: an omitted value must be rejected, never defaulted.
        var missingVisible = await CallToolRawAsync(
            "honua_studio_set_layer_visibility",
            $$"""{"draftId":"{{draft.DraftId}}","generation":{{draft.Generation}},"layerId":"parcels"}""");
        missingVisible.GetProperty("isError").GetBoolean().Should().BeTrue();
        missingVisible.GetProperty("structuredContent").GetProperty("code").GetString()
            .Should().Be("invalid_argument");
    }

    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task SetVisibilityInteraction_BoundToADraft_DispatchesThroughTheServerToolEndToEnd()
    {
        // REQ-003. ADR-0030's action-verb set already admits `setVisibility`, and dispatch is
        // generic — the verb needed no special wiring to be BOUND. What was missing was an
        // execution path: the bound action had no server tool, so the verb could be declared but
        // never applied to composition state. This drives the whole loop: bind the interaction,
        // read it back off the synced draft, resolve its do.ref/do.args through the shared
        // ADR-0030 vocabulary, and execute it against the draft.
        var draft = await CreateMapDraftWithParcelsLayerAsync();

        var bound = await CallToolAsync(
            "honua_studio_bind_interaction",
            $$"""
            {
              "draftId":"{{draft.DraftId}}",
              "generation":{{draft.Generation}},
              "interaction":{
                "id":"hide-parcels-on-select",
                "on":{"ref":"layer:zoning","event":"featureSelect"},
                "do":{"ref":"layer:parcels","verb":"setVisibility","args":{"visible":false} }
              }
            }
            """);
        var generation = bound.GetProperty("generation").GetInt64();

        var synced = await SyncCompositionAsync(draft.DraftId);
        var interaction = synced.Interactions.Should().ContainSingle().Subject;
        interaction.Do.Verb.Should().Be("setVisibility");

        // The dispatch step: the binding's do.ref resolves to a composed layer through the same
        // vocabulary the server admits bindings with, and its args carry the target visibility.
        StudioInteractionVocabulary.ResolveRef(synced, interaction.Do.Ref)
            .Should().Be(StudioComponentRefResolution.Resolved);
        interaction.Do.Ref.Should().StartWith(StudioInteractionVocabulary.LayerRefPrefix);
        var targetLayerId = interaction.Do.Ref[StudioInteractionVocabulary.LayerRefPrefix.Length..];
        var targetVisible = interaction.Do.Args!.Value.GetProperty("visible").GetBoolean();

        await CallToolAsync(
            "honua_studio_set_layer_visibility",
            $$"""{"draftId":"{{draft.DraftId}}","generation":{{generation}},"layerId":"{{targetLayerId}}","visible":{{(targetVisible ? "true" : "false")}}}""");

        var afterDispatch = await SyncCompositionAsync(draft.DraftId);
        LayerVisibility(afterDispatch, "parcels").Should().Be(targetVisible);
        afterDispatch.Interactions.Should().ContainSingle(i => i.Id == "hide-parcels-on-select",
            "executing a binding must not consume or rewrite it");
    }

    private static bool LayerVisibility(StudioCompositionBody body, string layerId) =>
        body.Layers.Single(layer => layer.Id == layerId).Visible;

    /// <summary>
    /// Creates a map-family draft carrying the <c>parcels</c> and <c>zoning</c> layers every
    /// case here toggles, entirely through the public <c>POST /mcp</c> tool surface.
    /// </summary>
    private async Task<(Guid DraftId, long Generation)> CreateMapDraftWithParcelsLayerAsync()
    {
        var created = await CallToolAsync(
            "honua_studio_create_draft",
            """{"packageKey":"parcels-visibility-map","family":"map","schemaVersion":"1.0"}""");
        var draftId = created.GetProperty("draftId").GetGuid();

        var withParcels = await CallToolAsync(
            "honua_studio_add_layer",
            $$"""{"draftId":"{{draftId}}","generation":{{created.GetProperty("generation").GetInt64()}},"layer":{"id":"parcels","type":"fill","sourceId":"content.parcels"} }""");
        var withZoning = await CallToolAsync(
            "honua_studio_add_layer",
            $$"""{"draftId":"{{draftId}}","generation":{{withParcels.GetProperty("generation").GetInt64()}},"layer":{"id":"zoning","type":"fill"} }""");

        return (draftId, withZoning.GetProperty("generation").GetInt64());
    }

    /// <summary>
    /// Reads the draft back through the REST surface a Studio client syncs from and returns its
    /// composition projection — the read half of "a visibility toggle survives a draft sync".
    /// </summary>
    private async Task<StudioCompositionBody> SyncCompositionAsync(Guid draftId)
    {
        using var response = await _client.GetAsync($"/api/v1/studio/package-drafts/{draftId:D}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var envelopeJson = document.RootElement.GetProperty("data").GetProperty("envelope").GetRawText();
        var envelope = JsonSerializer.Deserialize(envelopeJson, StudioJsonContext.Default.StudioPackageEnvelope)
            ?? throw new InvalidOperationException("Expected a Studio package envelope in the draft response.");
        return StudioCompositionBodyEditor.ReadBody(envelope);
    }

    /// <summary>Calls a tool and asserts it succeeded, returning its structured content.</summary>
    private async Task<JsonElement> CallToolAsync(string toolName, string argumentsJson)
    {
        var result = await CallToolRawAsync(toolName, argumentsJson);
        var failed = result.TryGetProperty("isError", out var isError) && isError.GetBoolean();
        var failure = failed && result.TryGetProperty("structuredContent", out var errorContent)
            ? errorContent.GetRawText()
            : "(none)";
        failed.Should().BeFalse($"'{toolName}' must succeed, but reported {failure}");
        return result.GetProperty("structuredContent");
    }

    /// <summary>Calls a tool over <c>POST /mcp</c> and returns the JSON-RPC <c>result</c> object.</summary>
    private async Task<JsonElement> CallToolRawAsync(string toolName, string argumentsJson)
    {
        var body = $$"""
            {"jsonrpc":"2.0","id":"{{toolName}}","method":"tools/call","params":{"name":"{{toolName}}","arguments":{{argumentsJson}} } }
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8) { Headers = { ContentType = new MediaTypeHeaderValue(JsonMediaType) } },
        };

        using var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var hasProtocolError = document.RootElement.TryGetProperty("error", out var error);
        var protocolError = hasProtocolError ? error.GetRawText() : "(none)";
        hasProtocolError.Should().BeFalse(
            $"'{toolName}' must not fail at the JSON-RPC protocol level: {protocolError}");
        return document.RootElement.GetProperty("result").Clone();
    }
}
