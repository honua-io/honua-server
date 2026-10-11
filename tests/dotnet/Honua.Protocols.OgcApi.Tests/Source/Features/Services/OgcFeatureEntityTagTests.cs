// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Infrastructure.Caching;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features.Services;

/// <summary>
/// Covers the representationETag consistency invariant for OGC API Features single-item GET
/// (BH6-006): the entity hash embedded in the representation ETag must be derived from
/// the same Feature snapshot used to serialize the response payload. A mixed-state tag
/// (entity hash from storedFeature V1, payload bytes from responseFeature V2) causes
/// spurious 412 Precondition Failed on subsequent If-Match PUT/PATCH because
/// MatchesEntityOrRepresentation checks the tag against the current V2 entity hash.
/// </summary>
public sealed class OgcFeatureEntityTagTests
{
    private static readonly ETagService ETagService = new();

    private static Feature MakeFeature(long id, string name)
        => Feature.Create(
            id,
            geometry: null,
            ImmutableDictionary<string, object?>.Empty.Add("name", name));

    [UnitTest]
    public void Compute_SparseAndNullPaddedAttributes_ProduceSameEntityETag()
    {
        var sparse = MakeFeature(1, "original");
        var projected = sparse with
        {
            Attributes = sparse.Attributes
                .Add("description", null)
                .Add("category", System.Text.Json.JsonSerializer.SerializeToElement<object?>(null))
        };

        var sparseTag = OgcFeatureEntityTag.Compute(sparse, ETagService);
        var projectedTag = OgcFeatureEntityTag.Compute(projected, ETagService);
        projectedTag.Should().Be(sparseTag);
        var representation = OgcFeatureEntityTag.ComputeRepresentation(
            System.Text.Encoding.UTF8.GetBytes("{\"properties\":{\"name\":\"original\",\"description\":null}}"),
            projectedTag, ETagService);
        OgcFeatureEntityTag.MatchesEntityOrRepresentation(representation, sparseTag, ETagService)
            .Should().BeTrue();

        var changed = projected with { Attributes = projected.Attributes.SetItem("description", "added") };
        OgcFeatureEntityTag.Compute(changed, ETagService).Should().NotBe(sparseTag);
    }

    [UnitTest]
    public void SelectEntitySnapshot_SameCrs_UsesResponseFeature()
    {
        var response = MakeFeature(1, "response") with { Geometry = [1, 2] };
        var storage = MakeFeature(1, "storage") with { Geometry = [9, 9] };
        var resolved = MakeFeature(1, "resolved");

        var selected = OgcFeatureEntityTag.SelectEntitySnapshot(4326, 4326, response, storage, resolved);

        selected.Attributes["name"].Should().Be("response");
        OgcFeatureEntityTag.Compute(selected, ETagService)
            .Should().Be(OgcFeatureEntityTag.Compute(response, ETagService));
    }

    [UnitTest]
    public void SelectEntitySnapshot_DifferentCrs_UsesStorageSnapshot()
    {
        var response = MakeFeature(1, "projected") with { Geometry = [1, 2, 3] };
        var storage = MakeFeature(1, "stored") with { Geometry = [9, 9] };
        var resolved = MakeFeature(1, "resolved");

        var selected = OgcFeatureEntityTag.SelectEntitySnapshot(3857, 4326, response, storage, resolved);

        OgcFeatureEntityTag.Compute(selected, ETagService)
            .Should().Be(OgcFeatureEntityTag.Compute(storage, ETagService));
        OgcFeatureEntityTag.Compute(response, ETagService)
            .Should().NotBe(OgcFeatureEntityTag.Compute(storage, ETagService));
    }

    [UnitTest]
    public void SelectEntitySnapshot_DifferentCrsWithoutStorageRead_UsesResolvedFeature()
    {
        var response = MakeFeature(1, "projected") with { Geometry = [1] };
        var resolved = MakeFeature(1, "resolved") with { Geometry = [4, 5] };

        var selected = OgcFeatureEntityTag.SelectEntitySnapshot(3857, 4326, response, null, resolved);

        selected.Attributes["name"].Should().Be("resolved");
    }

    [UnitTest]
    public void Compute_NestedJsonNullAndAbsentProperty_ProduceDifferentEntityETags()
    {
        var original = MakeFeature(1, "original");
        var absent = original with
        {
            Attributes = original.Attributes.Add("metadata", System.Text.Json.JsonSerializer.SerializeToElement(new { }))
        };
        var explicitNull = original with
        {
            Attributes = original.Attributes.Add("metadata",
                System.Text.Json.JsonSerializer.SerializeToElement(new { value = (string?)null }))
        };

        OgcFeatureEntityTag.Compute(absent, ETagService)
            .Should().NotBe(OgcFeatureEntityTag.Compute(explicitNull, ETagService));
    }

    /// <summary>
    /// Proves that two Feature versions with the same id but different attributes produce
    /// different entity ETags.
    /// </summary>
    [UnitTest]
    public void Compute_DifferentFeatureVersions_ProduceDifferentEntityETags()
    {
        var v1 = MakeFeature(1, "original");
        var v2 = MakeFeature(1, "updated");

        var etagV1 = OgcFeatureEntityTag.Compute(v1, ETagService);
        var etagV2 = OgcFeatureEntityTag.Compute(v2, ETagService);

        etagV1.Should().NotBe(etagV2, "changing an attribute must change the entity ETag");
    }

    /// <summary>
    /// Proves the bug (BH6-006): a mixed-state representationETag (entity hash from V1,
    /// payload bytes from V2) is NOT matched by MatchesEntityOrRepresentation against V2's
    /// entityETag. Without the fix, HandleGetItemAsync computed entityETag from storedFeature
    /// (V1 under a concurrent write) but serialized the payload from responseFeature (V2),
    /// producing exactly this mixed-state tag.
    /// </summary>
    [UnitTest]
    public void MatchesEntityOrRepresentation_MixedStateRepresentationETag_ReturnsFalse()
    {
        var v1 = MakeFeature(1, "original");
        var v2 = MakeFeature(1, "updated");

        var entityETagV1 = OgcFeatureEntityTag.Compute(v1, ETagService);
        var entityETagV2 = OgcFeatureEntityTag.Compute(v2, ETagService);

        // Payload bytes come from V2 (the response body), but entity hash is from V1 (the bug).
        var payloadV2 = System.Text.Encoding.UTF8.GetBytes("{\"id\":1,\"properties\":{\"name\":\"updated\"}}");
        var mixedStateTag = OgcFeatureEntityTag.ComputeRepresentation(payloadV2, entityETagV1, ETagService);

        // A client with the current V2 entity ETag tries If-Match with the mixed-state tag.
        // It should match (client holds the current data) but the mixed-state hash fails it.
        OgcFeatureEntityTag.MatchesEntityOrRepresentation(mixedStateTag, entityETagV2, ETagService)
            .Should().BeFalse(
                "a representation ETag built from a stale V1 entity hash cannot match a V2 If-Match check");
    }

    /// <summary>
    /// Proves the fix: when both entity hash and payload bytes come from the same Feature (V2),
    /// the representationETag matches V2's entityETag in MatchesEntityOrRepresentation.
    /// This is the correct post-fix behavior where HandleGetItemAsync derives entityETag from
    /// responseFeature.Value (the same instance used to build the payload).
    /// </summary>
    [UnitTest]
    public void MatchesEntityOrRepresentation_ConsistentRepresentationETag_ReturnsTrue()
    {
        var v2 = MakeFeature(1, "updated");
        var entityETagV2 = OgcFeatureEntityTag.Compute(v2, ETagService);

        var payloadV2 = System.Text.Encoding.UTF8.GetBytes("{\"id\":1,\"properties\":{\"name\":\"updated\"}}");

        // After the fix: both entity hash and payload bytes come from V2.
        var consistentTag = OgcFeatureEntityTag.ComputeRepresentation(payloadV2, entityETagV2, ETagService);

        OgcFeatureEntityTag.MatchesEntityOrRepresentation(consistentTag, entityETagV2, ETagService)
            .Should().BeTrue(
                "a representation ETag built from the same V2 entity must match V2's If-Match check");
    }

    [UnitTest]
    public async Task ReadUntilStorageStable_SkipsADivergentProjection()
    {
        var storage = new Queue<Feature?>(
        [
            MakeFeature(1, "v1"),
            MakeFeature(1, "v2"),
            MakeFeature(1, "v2"),
            MakeFeature(1, "v2"),
        ]);
        var projected = new Queue<Feature?>(
        [
            MakeFeature(1, "projected-v1"),
            MakeFeature(1, "projected-v2"),
        ]);

        var result = await OgcFeaturesResponseHelpers.ReadUntilStorageStableAsync(
            _ => Task.FromResult(storage.Dequeue()),
            _ => Task.FromResult(projected.Dequeue()),
            CancellationToken.None);

        result.Status.Should().Be(ConsistentFeatureReadStatus.Found);
        result.Storage.HasValue.Should().BeTrue();
        result.Response.HasValue.Should().BeTrue();
        result.Storage.GetValueOrDefault().Attributes["name"].Should().Be("v2");
        result.Response.GetValueOrDefault().Attributes["name"].Should().Be("projected-v2");
    }

    [UnitTest]
    public async Task ReadUntilStorageStable_UnstableWhenEveryReadDiverges()
    {
        var version = 0;
        var result = await OgcFeaturesResponseHelpers.ReadUntilStorageStableAsync(
            _ => Task.FromResult<Feature?>(MakeFeature(1, "v" + version++)),
            _ => Task.FromResult<Feature?>(MakeFeature(1, "projected")),
            CancellationToken.None,
            attempts: 2);

        result.Status.Should().Be(ConsistentFeatureReadStatus.Unstable);
        result.Storage.HasValue.Should().BeFalse();
        result.Response.HasValue.Should().BeFalse();
    }
}
