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
}
