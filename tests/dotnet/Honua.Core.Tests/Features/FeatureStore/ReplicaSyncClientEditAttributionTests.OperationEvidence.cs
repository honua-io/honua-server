// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.FeatureStore;

public sealed partial class ReplicaSyncClientEditAttributionTests
{
    [Fact]
    public async Task ApplyUpload_ExactOperationEvidence_DoesNotAbsorbLaterForeignEdit()
    {
        var repository = await ApplyWithEvidenceAsync([[Evidence(55)]]);

        repository.DetectionUpdates.Should().ContainSingle()
            .Which.ResolutionBaseGeneration.Should().Be(55);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("layer")]
    [InlineData("identity")]
    [InlineData("operation")]
    [InlineData("generation")]
    [InlineData("mixed")]
    public async Task ApplyUpload_UnusableOperationEvidence_KeepsPreBatchBase(string mismatch)
    {
        var evidence = mismatch switch
        {
            "empty" => ImmutableArray<FeatureOperationEvidence>.Empty,
            "layer" => [Evidence(55) with { LayerId = 11 }],
            "identity" => [Evidence(55) with { ObjectId = 43 }],
            "operation" => [Evidence(55) with { Operation = FeatureChangeOperation.Delete }],
            "generation" => [Evidence(50)],
            _ => ImmutableArray.Create(Evidence(55), Evidence(60) with { ObjectId = 43 })
        };
        var repository = await ApplyWithEvidenceAsync([evidence]);

        repository.DetectionUpdates.Should().ContainSingle()
            .Which.ResolutionBaseGeneration.Should().Be(50);
    }

    [Fact]
    public async Task ApplyUpload_RepeatedObjectEdits_UsesEachSlotsOwnGeneration()
    {
        var repository = await ApplyWithEvidenceAsync([[Evidence(55)], [Evidence(60)]]);

        repository.DetectionUpdates.Select(update => update.ResolutionBaseGeneration)
            .Should().Equal(55, 60);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyUpload_UnconfirmedOperationEvidence_DoesNotRaiseBase(bool unknown)
    {
        var repository = await ApplyWithEvidenceAsync([[Evidence(55)]],
            new PartialFailureEditApplier([], failed: true, unknown ? [0] : [])
            {
                OperationEvidence = [[Evidence(55)]]
            });

        repository.DetectionUpdates.Should().ContainSingle()
            .Which.ResolutionBaseGeneration.Should().Be(50);
    }

    [Fact]
    public async Task ApplyUpload_BranchEvidence_UsesLogicalObjectIdentity()
    {
        var repository = await ApplyWithEvidenceAsync(
            [[Evidence(55) with { ObjectId = 900, PublicObjectId = 42, VersionId = Guid.NewGuid() }]]);

        repository.DetectionUpdates.Should().ContainSingle()
            .Which.ResolutionBaseGeneration.Should().Be(55);
    }

    private static FeatureOperationEvidence Evidence(long generation) => new()
    {
        Generation = generation,
        LayerId = 10,
        ObjectId = 42,
        Operation = FeatureChangeOperation.Update
    };

    private static async Task<RecordingConflictRepository> ApplyWithEvidenceAsync(
        ImmutableArray<ImmutableArray<FeatureOperationEvidence>> evidence,
        PartialFailureEditApplier? applier = null)
    {
        var tracker = new RecordingChangeTracker(ServerChange(42) with { Generation = 90 })
        {
            Generations = new Queue<long>([50L, 90L])
        };
        var repository = new RecordingConflictRepository();
        var service = new ReplicaSyncService(tracker, repository, NullLogger<ReplicaSyncService>.Instance);
        var edits = Enumerable.Range(0, evidence.Length)
            .Select(_ => new ReplicaUploadEdit(FeatureEditOperationKind.Update, 42, null)).ToImmutableArray();
        applier ??= new PartialFailureEditApplier(Enumerable.Range(0, edits.Length).ToArray(), failed: false)
        {
            OperationEvidence = evidence
        };

        await service.ApplyUploadAsync(CreateRequest(edits), applier);
        return repository;
    }
}
