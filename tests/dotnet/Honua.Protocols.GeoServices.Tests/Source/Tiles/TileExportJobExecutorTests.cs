// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Tiles;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Tiles;

/// <summary>
/// Focused contract tests for the canonical durable tile-export executor.
/// </summary>
[Protocol(TestProtocols.MapServer)]
public sealed class TileExportJobExecutorTests
{
    [UnitTest]
    [Operation(Operations.Export)]
    public void BuildAndTryParse_RoundTripsValidatedPlanWithStableIdentity()
    {
        var plan = CreatePlan();

        var first = TileExportExecutionSpecBuilder.Build(plan);
        var second = TileExportExecutionSpecBuilder.Build(plan);

        first.Kind.Should().Be(ExecutionJobKind.TileExport);
        first.Parameters.Should().Equal(second.Parameters);
        first.Parameters.Should().ContainKey(TileExportJobParameterKeys.ContentIdentity);
        first.Parameters.Values.Should().OnlyContain(static value => value.Length < 1024,
            "execution-job metadata must never embed tile or package bytes");
        TileExportExecutionSpecBuilder.TryParse(first.Parameters, out var parsed, out var error).Should().BeTrue(error);
        parsed.Should().BeEquivalentTo(plan);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void TryParse_InconsistentPlanIdentity_IsRejected()
    {
        var parameters = TileExportExecutionSpecBuilder.Build(CreatePlan()).Parameters
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        parameters[TileExportJobParameterKeys.ZoomLevels] = "0,2,3";

        TileExportExecutionSpecBuilder.TryParse(parameters, out var plan, out var error).Should().BeFalse();

        plan.Should().BeNull();
        error.Should().Contain("identity");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void Build_AdjacentStringBoundaries_ProduceDistinctCanonicalIdentities()
    {
        var first = CreatePlan() with { ResourceId = "ab" };
        var second = CreatePlan() with { ResourceId = "a" };
        second = second with
        {
            Source = ((TileExportMapSourceDescriptor)second.Source) with
            {
                Layers = [new("bc", "default", 1)]
            }
        };

        TileExportArtifactIdentity.Compute(first).Should().NotBe(TileExportArtifactIdentity.Compute(second));
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void Build_ControlCharacterInAnyIdentifier_IsRejected()
    {
        var invalidPlans = new[]
        {
            CreatePlan() with { ResourceId = "world\nbasemap" },
            CreatePlan() with
            {
                Source = ((TileExportMapSourceDescriptor)CreatePlan().Source) with
                {
                    Layers = [new("0\rlayer", "default", 1)]
                }
            },
            CreatePlan() with
            {
                Source = ((TileExportMapSourceDescriptor)CreatePlan().Source) with
                {
                    Layers = [new("0", "default\tstyle", 1)]
                }
            }
        };

        foreach (var plan in invalidPlans)
        {
            var act = () => TileExportExecutionSpecBuilder.Build(plan);

            act.Should().Throw<ArgumentException>();
        }
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void PackageFormats_AdvertiseOnlyImplementedRuntimeSeams()
    {
        Enum.GetValues<TileExportPackageFormat>().Should().Equal(
            TileExportPackageFormat.Zip,
            TileExportPackageFormat.Tpkx);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public void TryParse_UnknownContractKey_IsRejected()
    {
        var parameters = TileExportExecutionSpecBuilder.Build(CreatePlan()).Parameters
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        parameters[TileExportJobParameterKeys.Prefix + "future"] = "ignored-state";

        TileExportExecutionSpecBuilder.TryParse(parameters, out var plan, out var error).Should().BeFalse();

        plan.Should().BeNull();
        error.Should().Contain("exact versioned contract key set");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExecuteAsync_UnexpiredMatchingArtifact_ReusesWithoutGeneration()
    {
        var plan = CreatePlan();
        var storage = Substitute.For<ICloudFileStorage>();
        var producer = Substitute.For<ITileExportPackageProducer>();
        producer.CanProduce(Arg.Any<TileExportJobPlan>()).Returns(true);
        var key = TileExportArtifactIdentity.BuildObjectKey(plan);
        storage.GetMetadataAsync(key, Arg.Any<CancellationToken>()).Returns(StoredFile(
            key,
            plan,
            DateTimeOffset.UtcNow.AddHours(2)));
        var context = new RecordingContext("export-reuse");
        var executor = CreateExecutor(storage, producer);

        var result = await executor.ExecuteAsync(JobFor(plan, context.OperationId), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        context.Artifacts.Should().ContainSingle().Which.Should().Be(key);
        await producer.DidNotReceiveWithAnyArgs().ProduceAsync(default!, default!, default);
        await storage.DidNotReceiveWithAnyArgs().UploadAsync((FileUploadRequest)null!, default);
    }

    [Theory]
    [InlineData(3599, true)]
    [InlineData(3600, false)]
    [Operation(Operations.Export)]
    public async Task ExecuteAsync_MatchingArtifactRetentionBoundary_RegeneratesOnlyWhenTtlIsShort(
        int remainingSeconds,
        bool shouldRegenerate)
    {
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        var plan = CreatePlan() with { RetentionSeconds = 3600 };
        var key = TileExportArtifactIdentity.BuildObjectKey(plan);
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(key, Arg.Any<CancellationToken>()).Returns(StoredFile(
            key,
            plan,
            now.AddSeconds(remainingSeconds)));
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateSuccess(StoredFile(key, plan, now.AddHours(1))));
        var producer = Substitute.For<ITileExportPackageProducer>();
        producer.CanProduce(Arg.Any<TileExportJobPlan>()).Returns(true);
        producer.ProduceAsync(Arg.Any<TileExportJobPlan>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Stream>(1).WriteAsync(new byte[] { 0x01 }).AsTask());
        var context = new RecordingContext("export-retention-boundary");
        var executor = CreateExecutor(storage, new FixedTimeProvider(now), producer);

        var result = await executor.ExecuteAsync(JobFor(plan, context.OperationId), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        await producer.Received(shouldRegenerate ? 1 : 0).ProduceAsync(
            Arg.Any<TileExportJobPlan>(),
            Arg.Any<Stream>(),
            Arg.Any<CancellationToken>());
        await storage.Received(shouldRegenerate ? 1 : 0)
            .UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExecuteAsync_ExpiredArtifact_RegeneratesWithBoundedStorageTtl()
    {
        var plan = CreatePlan() with { RetentionSeconds = 3600 };
        var storage = Substitute.For<ICloudFileStorage>();
        var producer = Substitute.For<ITileExportPackageProducer>();
        producer.CanProduce(Arg.Any<TileExportJobPlan>()).Returns(true);
        producer.ProduceAsync(Arg.Any<TileExportJobPlan>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var stream = call.ArgAt<Stream>(1);
                await stream.WriteAsync(new byte[] { 0x01, 0x02, 0x03 });
            });
        var key = TileExportArtifactIdentity.BuildObjectKey(plan);
        storage.GetMetadataAsync(key, Arg.Any<CancellationToken>()).Returns(StoredFile(
            key,
            plan,
            DateTimeOffset.UtcNow.AddMinutes(-1)));

        FileUploadRequest? captured = null;
        storage.UploadAsync(Arg.Do<FileUploadRequest>(request => captured = request), Arg.Any<CancellationToken>())
            .Returns(_ => UploadResult.CreateSuccess(StoredFile(key, plan, DateTimeOffset.UtcNow.AddHours(1))));
        var context = new RecordingContext("export-expired");
        var executor = CreateExecutor(storage, producer);

        var result = await executor.ExecuteAsync(JobFor(plan, context.OperationId), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        captured.Should().NotBeNull();
        captured!.ObjectKeyOverride.Should().Be(key);
        captured.SizeBytes.Should().Be(3);
        captured.TimeToLive.Should().Be(TimeSpan.FromHours(1));
        captured.Metadata[TileExportArtifactIdentity.IdentityMetadataKey].Should()
            .Be(TileExportArtifactIdentity.Compute(plan));
        context.Artifacts.Should().ContainSingle(key);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExecuteAsync_MissingArtifact_GeneratesAndPublishesStableReference()
    {
        var plan = CreatePlan();
        var key = TileExportArtifactIdentity.BuildObjectKey(plan);
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(key, Arg.Any<CancellationToken>()).Returns((CloudFile?)null);
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateSuccess(StoredFile(key, plan, DateTimeOffset.UtcNow.AddHours(1))));
        var producer = Substitute.For<ITileExportPackageProducer>();
        producer.CanProduce(Arg.Any<TileExportJobPlan>()).Returns(true);
        producer.ProduceAsync(Arg.Any<TileExportJobPlan>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Stream>(1).WriteAsync(new byte[] { 0x01 }).AsTask());
        var context = new RecordingContext("export-missing");
        var executor = CreateExecutor(storage, producer);

        var result = await executor.ExecuteAsync(JobFor(plan, context.OperationId), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        context.Artifacts.Should().ContainSingle(key);
        await producer.Received(1).ProduceAsync(
            Arg.Is<TileExportJobPlan>(candidate =>
                TileExportArtifactIdentity.Compute(candidate) == TileExportArtifactIdentity.Compute(plan)),
            Arg.Any<Stream>(),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExecuteAsync_ProducerExceedsArtifactLimit_FailsWithoutUpload()
    {
        var plan = CreatePlan() with { MaxArtifactBytes = 8 };
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((CloudFile?)null);
        var producer = Substitute.For<ITileExportPackageProducer>();
        producer.CanProduce(Arg.Any<TileExportJobPlan>()).Returns(true);
        producer.ProduceAsync(Arg.Any<TileExportJobPlan>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var stream = call.ArgAt<Stream>(1);
                await stream.WriteAsync(new byte[9]);
            });
        var executor = CreateExecutor(storage, producer);

        var result = await executor.ExecuteAsync(
            JobFor(plan, "export-too-large"),
            new RecordingContext("export-too-large"),
            CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("artifact limit");
        await storage.DidNotReceiveWithAnyArgs().UploadAsync((FileUploadRequest)null!, default);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExecuteAsync_CancelledGeneration_PropagatesWithoutUpload()
    {
        var plan = CreatePlan();
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((CloudFile?)null);
        var producer = Substitute.For<ITileExportPackageProducer>();
        producer.CanProduce(Arg.Any<TileExportJobPlan>()).Returns(true);
        producer.ProduceAsync(Arg.Any<TileExportJobPlan>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromCanceled(call.ArgAt<CancellationToken>(2)));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var executor = CreateExecutor(storage, producer);

        var act = () => executor.ExecuteAsync(
            JobFor(plan, "export-cancel"),
            new RecordingContext("export-cancel"),
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await storage.DidNotReceiveWithAnyArgs().UploadAsync((FileUploadRequest)null!, default);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExecuteAsync_NoProducer_FailsCleanlyWithoutArtifact()
    {
        var plan = CreatePlan();
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((CloudFile?)null);
        var context = new RecordingContext("export-no-producer");
        var executor = CreateExecutor(storage);

        var result = await executor.ExecuteAsync(JobFor(plan, context.OperationId), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("producer");
        context.Artifacts.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task ExecuteAsync_MultipleMatchingProducers_FailsDeterministically()
    {
        var plan = CreatePlan();
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((CloudFile?)null);
        var first = Substitute.For<ITileExportPackageProducer>();
        var second = Substitute.For<ITileExportPackageProducer>();
        first.CanProduce(Arg.Any<TileExportJobPlan>()).Returns(true);
        second.CanProduce(Arg.Any<TileExportJobPlan>()).Returns(true);
        var context = new RecordingContext("export-ambiguous-producer");
        var executor = CreateExecutor(storage, first, second);

        var result = await executor.ExecuteAsync(JobFor(plan, context.OperationId), context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("Multiple");
        context.Artifacts.Should().BeEmpty();
        await first.DidNotReceiveWithAnyArgs().ProduceAsync(default!, default!, default);
        await second.DidNotReceiveWithAnyArgs().ProduceAsync(default!, default!, default);
    }

    private static TileExportJobExecutor CreateExecutor(
        ICloudFileStorage storage,
        params ITileExportPackageProducer[] producers)
        => CreateExecutor(storage, TimeProvider.System, producers);

    private static TileExportJobExecutor CreateExecutor(
        ICloudFileStorage storage,
        TimeProvider timeProvider,
        params ITileExportPackageProducer[] producers)
    {
        var fence = Substitute.For<ITileExportSourceFence>();
        fence.SourceKind.Returns(TileExportSourceKind.Map);
        fence.IsAvailableAsync(Arg.Any<TileExportJobPlan>(), Arg.Any<CancellationToken>()).Returns(true);
        return new(storage, producers, [fence], timeProvider, NullLogger<TileExportJobExecutor>.Instance);
    }

    private static TileExportJobPlan CreatePlan()
        => new()
        {
            SourceKind = TileExportSourceKind.Map,
            ResourceId = "world-basemap",
            Source = new TileExportMapSourceDescriptor(
                42,
                [new("0", "default", 1)],
                "provider-revision-9",
                null),
            ZoomLevels = [0, 2],
            West = -180,
            South = -85,
            East = 180,
            North = 85,
            TileImageFormat = "PNG",
            PackageFormat = TileExportPackageFormat.Tpkx,
            MaxTiles = 10_000,
            MaxArtifactBytes = 1024 * 1024,
            RetentionSeconds = 3600
        };

    private static ExecutionJobRecord JobFor(TileExportJobPlan plan, string operationId)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Running,
            CreatedAt = now,
            UpdatedAt = now,
            Spec = TileExportExecutionSpecBuilder.Build(plan)
        };
    }

    private static CloudFile StoredFile(string key, TileExportJobPlan plan, DateTimeOffset expiresAt)
        => new()
        {
            FileId = key,
            FileName = Path.GetFileName(key),
            StoragePath = key,
            ContentType = "application/octet-stream",
            SizeBytes = 3,
            UploadedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
            Provider = CloudStorageProvider.Local,
            Metadata = ImmutableDictionary<string, string>.Empty.Add(
                TileExportArtifactIdentity.IdentityMetadataKey,
                TileExportArtifactIdentity.Compute(plan))
        };

    private sealed class RecordingContext(string operationId) : IJobExecutionContext
    {
        public string OperationId { get; } = operationId;
        public List<string> Artifacts { get; } = [];
        public List<(double? Percent, string? Phase)> Progress { get; } = [];

        public Task ReportProgressAsync(
            double? percentComplete,
            string? phase,
            CancellationToken cancellationToken = default)
        {
            Progress.Add((percentComplete, phase));
            return Task.CompletedTask;
        }

        public Task AppendLogAsync(ExecutionLogEntry entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
        {
            Artifacts.Add(artifactReference);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
