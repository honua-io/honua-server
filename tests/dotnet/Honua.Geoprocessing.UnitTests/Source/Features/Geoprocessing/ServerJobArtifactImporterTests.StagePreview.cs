// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Geoprocessing.Tests;

public sealed partial class ServerJobArtifactImporterTests
{
    [UnitTest]
    public async Task PreviewAsync_VerifiedArtifact_DoesNotImport()
    {
        var scenario = await StageScenarioAsync();

        var preview = await ServerJobArtifactImporter.PreviewAsync(
            scenario.Command, scenario.Context, CancellationToken.None);

        preview.SourceSrid.Should().Be(3857);
        preview.FeatureCount.Should().Be(1);
        preview.ContentSha256.Should().HaveLength(64);
        preview.CrsState.Should().Be("known");
        scenario.Recording.Request.Should().BeNull();
    }

    [UnitTest]
    public async Task ImportAsync_PreviewedArtifact_CreatesOnlyAndReplaysReceipt()
    {
        var scenario = await StageScenarioAsync();
        var preview = await ServerJobArtifactImporter.PreviewAsync(
            scenario.Command, scenario.Context, CancellationToken.None);
        var command = scenario.Command with { StagePreview = preview };

        var first = await ImportStagedAsync(scenario, command);
        var replay = await ImportStagedAsync(scenario, command);

        scenario.Recording.Request!.LoadMode.Should().Be(ImportLoadMode.CreateOnly);
        scenario.Recording.Request.OverwriteExisting.Should().BeFalse();
        replay.Should().BeSameAs(first);
        await scenario.Recording.Service.Received(1).ImportFileAsync(
            Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("intent")]
    [InlineData("schema")]
    [InlineData("destination")]
    [InlineData("crs")]
    [InlineData("source-crs")]
    [InlineData("reference")]
    [InlineData("content")]
    [InlineData("count")]
    [InlineData("preview-crs")]
    [InlineData("preview-crs-state")]
    public async Task ImportAsync_PreviewBindingChanged_RefusesBeforeImport(string change)
    {
        var scenario = await StageScenarioAsync();
        var preview = await ServerJobArtifactImporter.PreviewAsync(
            scenario.Command, scenario.Context, CancellationToken.None);
        var command = scenario.Command with { StagePreview = preview };
        command = change switch
        {
            "tenant" => command with { TenantId = "tenant-b" },
            "actor" => command with { ActorId = "actor-2" },
            "intent" => command with { IntentId = "another-intent" },
            "schema" => command with { Schema = "another_schema" },
            "destination" => command with { Table = "another_table" },
            "crs" => command with { TargetSrid = 3857 },
            "source-crs" => command with { SourceSrid = 4326 },
            "reference" => command with { Reference = command.Reference with { ReferenceSha256 = new string('0', 64) } },
            "content" => command with { StagePreview = preview with { ContentSha256 = new string('0', 64) } },
            "count" => command with { StagePreview = preview with { FeatureCount = preview.FeatureCount + 1 } },
            "preview-crs" => command with { StagePreview = preview with { SourceSrid = 4326 } },
            "preview-crs-state" => command with { StagePreview = preview with { CrsState = "unknown" } },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        var act = () => ImportStagedAsync(scenario, command);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
        scenario.Recording.Request.Should().BeNull();
    }

    [UnitTest]
    public async Task ImportAsync_JobChangedAfterPreview_RefusesBeforeImport()
    {
        var scenario = await StageScenarioAsync();
        var preview = await ServerJobArtifactImporter.PreviewAsync(
            scenario.Command, scenario.Context, CancellationToken.None);
        scenario.Context.Jobs.GetJobAsync(scenario.Command.Reference.JobId,
                Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(CompletedJob("changed-reference", 3));

        var act = () => ImportStagedAsync(scenario, scenario.Command with { StagePreview = preview });

        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
        scenario.Recording.Request.Should().BeNull();
    }

    [UnitTest]
    public async Task ImportAsync_LegacyAppendReceipt_DoesNotSatisfyStagedImport()
    {
        var scenario = await StageScenarioAsync();
        await ImportStagedAsync(scenario, scenario.Command);
        var preview = await ServerJobArtifactImporter.PreviewAsync(
            scenario.Command, scenario.Context, CancellationToken.None);

        var act = () => ImportStagedAsync(scenario, scenario.Command with { StagePreview = preview });

        await act.Should().ThrowAsync<GeoprocessingIdempotencyConflictException>();
        await scenario.Recording.Service.Received(1).ImportFileAsync(
            Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>());
    }

    private static async Task<StageScenario> StageScenarioAsync()
    {
        var artifact = await BufferArtifactAsync("3857", "10");
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var job = CompletedJob(artifact.Uri, 2);
        jobs.GetJobAsync(job.OperationId, Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(job);
        var recording = new RecordingImports();
        var context = new ServerJobArtifactImportContext(
            Principal(), jobs, recording.Service)
        {
            Services = new ServiceCollection().BuildServiceProvider(),
        };
        return new StageScenario(Command(Reference(artifact, 2, 1)), context, recording);
    }

    private static Task<ServerJobArtifactImportReceipt> ImportStagedAsync(
        StageScenario scenario, ServerJobArtifactImportCommand command)
        => ServerJobArtifactImporter.ImportAsync(command, scenario.Context.Principal,
            scenario.Context.Jobs, scenario.Context.Imports, scenario.Ledger,
            scenario.Context.Services, CancellationToken.None);

    private sealed record StageScenario(
        ServerJobArtifactImportCommand Command, ServerJobArtifactImportContext Context,
        RecordingImports Recording)
    {
        internal MemoryServerJobArtifactImportLedger Ledger { get; } = new();
    }
}
