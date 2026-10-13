// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// A masked owner must survive a GeoServices replace update without being copied onto the
/// caller-visible feature (honua-server#5426).
/// </summary>
public sealed class OwnerMaskEditPreservationTests
{
    [UnitTest]
    public async Task ConvertAsync_PreserveFlag_ReachesTheFeatureEditBatch()
    {
        var visible = ImmutableDictionary.CreateRange(new Dictionary<string, object?>
        {
            ["name"] = "kept"
        });
        var update = Feature.Create(7, geometry: null, visible) with
        {
            PreserveOmittedMaskedAttributes = true
        };
        var adapter = new GeoServicesEditParameterAdapter(
            NullLogger<GeoServicesEditParameterAdapter>.Instance);
        var converted = await adapter.ConvertAsync(
            new GeoServicesEditRequest { Updates = ImmutableArray.Create(update) },
            new MetadataV2Resource());

        converted.IsSuccess.Should().BeTrue();
        if (converted.EditRequest is not { } request)
        {
            throw new InvalidOperationException("adapter produced no edit request");
        }

        if (request.Updates is not { } updates)
        {
            throw new InvalidOperationException("adapter produced no updates");
        }

        var edit = updates.Should().ContainSingle().Subject;
        edit.PreserveOmittedMaskedAttributes.Should().BeTrue();
        edit.Attributes!.Keys.Should().NotContain("owner");

        var batch = new EditProcessor(NullLogger<EditProcessor>.Instance)
            .ToFeatureEditBatch(request, new MetadataV2Resource());
        var stored = batch.Updates.Should().ContainSingle().Subject;
        stored.PreserveOmittedMaskedAttributes.Should().BeTrue();
        stored.Attributes.ContainsKey("owner").Should().BeFalse();
    }
}
