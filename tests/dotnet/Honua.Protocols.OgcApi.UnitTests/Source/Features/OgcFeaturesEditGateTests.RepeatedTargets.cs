// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Honua.Protocols.OgcApi.UnitTests.Features;

public sealed partial class OgcFeaturesEditGateTests
{
    [UnitTest]
    public async Task BatchPatch_RepeatedTarget_RejectsEveryUpdateBeforeHooksOrWrites()
    {
        foreach (var colors in new[] { new[] { "blue", "red" }, new[] { "red", "blue" } })
        {
            var reader = Substitute.For<IFeatureReader>();
            reader.QueryAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
                .Returns(QueryResult<Feature>.Create(1, [Row(42, "red", "S")]));
            var writer = new CountingWriter();
            var hooks = new RecordingHooks();
            var request = new OgcFeaturesEditRequest
            {
                Operation = OgcFeaturesEditOperation.Batch,
                RollbackOnFailure = true,
                BatchOperations = [.. colors.Select(color => PartialUpdate(42, color))],
            };

            var result = await CreateHandlers(writer, reader).Transaction.ExecuteEditAsync(
                Context(hooks), 7, ResourceWithNullSize(), request, CancellationToken.None);

            result.IsSuccess.Should().BeFalse();
            result.UpdateResults.Should().HaveCount(2).And.OnlyContain(row =>
                row.ObjectId == 42 && row.ErrorMessage == "An edit batch cannot update the same feature more than once.");
            result.UpdateResults.Should().OnlyContain(row =>
                OgcFeatureEditFailureMapper.DetermineStatus(row) == StatusCodes.Status400BadRequest);
            writer.ApplyCount.Should().Be(0);
            hooks.BeforeBatches.Should().BeEmpty();
            hooks.AfterCalls.Should().BeEmpty();
            await reader.DidNotReceive().QueryAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
        }
    }

    [UnitTest]
    public async Task BatchPatch_DistinctTargets_ValidatesBothSnapshotsAndWrites()
    {
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(7, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(2, [Row(42, "red", "S"), Row(43, "red", "S")]));
        var writer = new CountingWriter();
        var hooks = new RecordingHooks();
        var request = new OgcFeaturesEditRequest
        {
            Operation = OgcFeaturesEditOperation.Batch,
            RollbackOnFailure = true,
            BatchOperations = [PartialUpdate(42, "red"), PartialUpdate(43, "red")],
        };

        var result = await CreateHandlers(writer, reader).Transaction.ExecuteEditAsync(
            Context(hooks), 7, ResourceWithNullSize(), request, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.UpdateResults.Select(row => row.ObjectId).Should().Equal(42, 43);
        writer.ApplyCount.Should().Be(1);
        hooks.BeforeBatches.Should().ContainSingle();
        hooks.AfterCalls.Should().ContainSingle();
    }

    private static OgcFeaturesBatchEditOperation PartialUpdate(long id, string color)
        => new()
        {
            Operation = OgcFeaturesEditOperation.Patch,
            ObjectId = id,
            Feature = Feature.Create(id, null, ImmutableDictionary<string, object?>.Empty.Add("color", color)),
        };
}
