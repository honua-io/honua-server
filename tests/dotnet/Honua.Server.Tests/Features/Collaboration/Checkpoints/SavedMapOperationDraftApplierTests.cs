// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Studio.Domain;
using Honua.Server.Features.Collaboration.Checkpoints;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Collaboration.Checkpoints;

/// <summary>
/// The checkpoint applier must be lossless against the stored document: a collaboration operation
/// edits the composition projection only, and has no authority over the package-lifecycle members
/// the canonical Map/App family contract requires (honua-server#2999 review).
/// </summary>
public sealed class SavedMapOperationDraftApplierTests
{
    /// <summary>
    /// A canonical map body: the composition projection (<c>layers</c>/<c>view</c>) plus the
    /// members <c>StudioPackageValidator.ValidateFamilyBody</c> requires of a <c>MapPackage</c>.
    /// </summary>
    private const string CanonicalMapBody = """
        {
          "mapPackageId": "map-1",
          "format": "honua_map_package.v1",
          "status": "Ready",
          "createdAt": "2026-01-01T00:00:00Z",
          "sourceBindings": [],
          "initialView": {"bbox":[0,0,10,10],"crs":"EPSG:4326"},
          "layers": [{"id":"parcels","title":"Parcels","visible":true}]
        }
        """;

    [UnitTest]
    public void Apply_ReplaceDocument_ReplacesCompositionWithoutDeletingFamilyRequiredMembers()
    {
        var envelope = BuildEnvelope(CanonicalMapBody);

        var applied = SavedMapOperationDraftApplier.Apply(
            envelope,
            [BuildOperation(SavedMapOperationKind.ReplaceWebMapDocument, """{"layers":[{"id":"roads"}]}""")]);

        var body = applied.Body!.Value;

        // The projection the operation owns is replaced wholesale...
        body.GetProperty("layers").EnumerateArray().Select(layer => layer.GetProperty("id").GetString())
            .Should().Equal("roads");

        // ...while every member the canonical MapPackage contract requires survives. Installing
        // the payload as the whole body deleted all of these and produced an invalid immutable
        // version.
        body.GetProperty("mapPackageId").GetString().Should().Be("map-1");
        body.GetProperty("format").GetString().Should().Be("honua_map_package.v1");
        body.GetProperty("status").GetString().Should().Be("Ready");
        body.GetProperty("createdAt").GetString().Should().Be("2026-01-01T00:00:00Z");
        body.TryGetProperty("sourceBindings", out _).Should().BeTrue();
        body.GetProperty("initialView").GetProperty("crs").GetString().Should().Be("EPSG:4326");
    }

    [UnitTest]
    public void Apply_ReplaceDocumentWithEmptyObject_ClearsCompositionAndKeepsPackageMetadata()
    {
        var envelope = BuildEnvelope(CanonicalMapBody);

        var applied = SavedMapOperationDraftApplier.Apply(
            envelope,
            [BuildOperation(SavedMapOperationKind.ReplaceWebMapDocument, "{}")]);

        var body = applied.Body!.Value;
        body.GetProperty("layers").GetArrayLength().Should().Be(0);
        body.GetProperty("mapPackageId").GetString().Should().Be("map-1");
    }

    [UnitTest]
    public void Apply_ReplaceDocumentThenScalarOperation_KeepsBothTheReplacementAndTheMetadata()
    {
        var envelope = BuildEnvelope(CanonicalMapBody);

        // Replay order is by server cursor, so the visibility edit lands on the replaced document.
        var applied = SavedMapOperationDraftApplier.Apply(
            envelope,
            [
                BuildOperation(SavedMapOperationKind.ReplaceWebMapDocument, """{"layers":[{"id":"roads"}]}""", cursor: 1),
                BuildOperation(
                    SavedMapOperationKind.SetLayerVisibility,
                    """{"layerId":"roads","visible":false}""",
                    cursor: 2),
            ]);

        var body = applied.Body!.Value;
        var layer = body.GetProperty("layers").EnumerateArray().Single();
        layer.GetProperty("id").GetString().Should().Be("roads");
        layer.GetProperty("visible").GetBoolean().Should().BeFalse();
        body.GetProperty("format").GetString().Should().Be("honua_map_package.v1");
    }

    private static StudioPackageEnvelope BuildEnvelope(string bodyJson)
    {
        using var document = JsonDocument.Parse(bodyJson);
        return new StudioPackageEnvelope
        {
            Family = StudioPackageFamily.Map,
            SchemaVersion = "1.0",
            Format = "honua_map_package.v1",
            Body = document.RootElement.Clone(),
        };
    }

    private static SavedMapOperationEnvelope BuildOperation(
        SavedMapOperationKind kind,
        string payloadJson,
        long cursor = 1)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return new SavedMapOperationEnvelope
        {
            OperationId = new SavedMapOperationId($"op-{cursor}"),
            MapId = new SavedMapId("11111111-2222-3333-4444-555555555555"),
            ActorId = new SavedMapActorId("actor-1"),
            BaseCursor = new SavedMapOperationCursor(cursor - 1),
            Kind = kind,
            ServerCursor = new SavedMapOperationCursor(cursor),
            AcceptedAt = DateTimeOffset.UnixEpoch,
            Payload = document.RootElement.Clone(),
        };
    }
}
