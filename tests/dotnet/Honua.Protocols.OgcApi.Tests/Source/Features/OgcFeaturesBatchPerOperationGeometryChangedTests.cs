// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

/// <summary>
/// Unit tests for the OGC Features batch handler's per-operation geometry-change
/// flag builder. The helper is the single source of truth for whether each row
/// in an OGC Features batch transaction reports <c>GeometryChanged=true</c> in
/// the outbox payload — and the contract for Replace-mode updates is that the
/// flag must reflect either the request feature's geometry intent OR the
/// existing row's geometry, so a body-less Replace that clears a non-null
/// geometry is not silently published as <c>GeometryChanged=false</c> (#692).
/// </summary>
[Protocol(TestProtocols.OgcApiFeatures)]
public sealed class OgcFeaturesBatchPerOperationGeometryChangedTests
{
    [UnitTest]
    public void EmptyBatch_ReturnsNull()
    {
        var result = OgcFeaturesTransactionHandler.BuildBatchPerOperationGeometryChanged(
            ImmutableArray<OgcFeaturesTransactionHandler.PreparedBatchOperation>.Empty);

        result.Should().BeNull();
    }

    [UnitTest]
    public void Update_BodyHasGeometry_FlagsTrue()
    {
        var prepared = ImmutableArray.Create(BuildUpdate(
            featureGeometry: SamplePointWkb,
            existingHadGeometry: false));

        var result = OgcFeaturesTransactionHandler.BuildBatchPerOperationGeometryChanged(prepared);

        result.Should().NotBeNull();
        result!["update"].Should().Equal(true);
    }

    [UnitTest]
    public void Update_BodyHasGeometry_AndExistingHadGeometry_FlagsTrue()
    {
        var prepared = ImmutableArray.Create(BuildUpdate(
            featureGeometry: SamplePointWkb,
            existingHadGeometry: true));

        var result = OgcFeaturesTransactionHandler.BuildBatchPerOperationGeometryChanged(prepared);

        result.Should().NotBeNull();
        result!["update"].Should().Equal(true);
    }

    [UnitTest]
    public void Update_BodyMissingGeometry_AndExistingHadGeometry_FlagsTrue()
    {
        // Replace-mode update where the request body omitted Geometry but the
        // existing row carried geometry: the operation will overwrite the existing
        // value with null, so the row's GeometryChanged must be true even though
        // the request feature's WKB is null.
        var prepared = ImmutableArray.Create(BuildUpdate(
            featureGeometry: null,
            existingHadGeometry: true));

        var result = OgcFeaturesTransactionHandler.BuildBatchPerOperationGeometryChanged(prepared);

        result.Should().NotBeNull();
        // A body-less Replace that clears a non-null geometry must surface the change to consumers.
        result!["update"].Should().Equal(true);
    }

    [UnitTest]
    public void Update_BodyMissingGeometry_AndExistingNull_FlagsFalse()
    {
        // Replace-mode update where neither side has geometry: no transition, so
        // the per-row flag stays false. This is the only no-change case for a
        // Replace-mode update.
        var prepared = ImmutableArray.Create(BuildUpdate(
            featureGeometry: null,
            existingHadGeometry: false));

        var result = OgcFeaturesTransactionHandler.BuildBatchPerOperationGeometryChanged(prepared);

        result.Should().NotBeNull();
        result!["update"].Should().Equal(false);
    }

    [UnitTest]
    public void Create_BodyHasGeometry_FlagsTrue()
    {
        var prepared = ImmutableArray.Create(BuildCreate(featureGeometry: SamplePointWkb));

        var result = OgcFeaturesTransactionHandler.BuildBatchPerOperationGeometryChanged(prepared);

        result.Should().NotBeNull();
        result!["create"].Should().Equal(true);
    }

    [UnitTest]
    public void Create_BodyMissingGeometry_FlagsFalse()
    {
        var prepared = ImmutableArray.Create(BuildCreate(featureGeometry: null));

        var result = OgcFeaturesTransactionHandler.BuildBatchPerOperationGeometryChanged(prepared);

        result.Should().NotBeNull();
        result!["create"].Should().Equal(false);
    }

    [UnitTest]
    public void Delete_AlwaysFlagsFalse()
    {
        var prepared = ImmutableArray.Create(BuildDelete());

        var result = OgcFeaturesTransactionHandler.BuildBatchPerOperationGeometryChanged(prepared);

        result.Should().NotBeNull();
        result!["delete"].Should().Equal(false);
    }

    [UnitTest]
    public void MixedBatch_FlagsAreOrderedPerKind()
    {
        // Create-then-update-then-delete dispatch order is what
        // ApplyEditsAsync iterates per kind, so the per-row flags must align
        // with that ordering.
        var prepared = ImmutableArray.Create(
            BuildCreate(featureGeometry: SamplePointWkb) with { Index = 0 },
            BuildUpdate(featureGeometry: null, existingHadGeometry: true) with { Index = 1 },
            BuildUpdate(featureGeometry: null, existingHadGeometry: false) with { Index = 2 },
            BuildCreate(featureGeometry: null) with { Index = 3 },
            BuildDelete() with { Index = 4 });

        var result = OgcFeaturesTransactionHandler.BuildBatchPerOperationGeometryChanged(prepared);

        result.Should().NotBeNull();
        result!["create"].Should().Equal(true, false);
        result["update"].Should().Equal(true, false);
        result["delete"].Should().Equal(false);
    }

    private static OgcFeaturesTransactionHandler.PreparedBatchOperation BuildCreate(
        byte[]? featureGeometry)
        => new(
            OgcFeaturesTransactionHandler.BatchOperationKind.Create,
            new BatchOperation { Type = "CREATE" },
            Feature: featureGeometry is null
                ? Feature.Create(0, geometry: null, ImmutableDictionary<string, object?>.Empty)
                : Feature.Create(0, geometry: featureGeometry, ImmutableDictionary<string, object?>.Empty),
            ObjectId: null);

    private static OgcFeaturesTransactionHandler.PreparedBatchOperation BuildUpdate(
        byte[]? featureGeometry,
        bool existingHadGeometry)
        => new(
            OgcFeaturesTransactionHandler.BatchOperationKind.Update,
            new BatchOperation { Type = "UPDATE" },
            Feature: featureGeometry is null
                ? Feature.Create(1, geometry: null, ImmutableDictionary<string, object?>.Empty)
                : Feature.Create(1, geometry: featureGeometry, ImmutableDictionary<string, object?>.Empty),
            ObjectId: 1)
        {
            ExistingHadGeometry = existingHadGeometry
        };

    private static OgcFeaturesTransactionHandler.PreparedBatchOperation BuildDelete()
        => new(
            OgcFeaturesTransactionHandler.BatchOperationKind.Delete,
            new BatchOperation { Type = "DELETE" },
            Feature: null,
            ObjectId: 1);

    // Minimal non-empty WKB stand-in. The helper only tests Length > 0.
    private static readonly byte[] SamplePointWkb = [0x01];
}
