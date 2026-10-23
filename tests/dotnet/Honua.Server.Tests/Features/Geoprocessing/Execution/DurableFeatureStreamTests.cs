// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.FileStorage;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Protocols.Ogc.Api.Processes;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>Production dispatcher, independent shared-store clients, and OGC value read-back.</summary>
public sealed class DurableFeatureStreamTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "honua-shared-stream-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, ExecutionJobRecord> _records = new(StringComparer.Ordinal);
    private readonly IExecutionJobStore _jobs = Substitute.For<IExecutionJobStore>();
    private readonly GeoprocessingOutputStagingOptions _staging;
    private readonly string _producerRoot;
    private readonly string _consumerRoot;
    private readonly string _payload = new('x', 1100 * 1024);

    public DurableFeatureStreamTests()
    {
        Directory.CreateDirectory(_root);
        _producerRoot = Path.Join(_root, "producer-temp");
        _consumerRoot = Path.Join(_root, "consumer-temp");
        var shared = Path.Join(_root, "persistent-volume");
        Directory.CreateDirectory(shared);
        _staging = GeoprocessingOutputStoreTestHelper.Attest(new GeoprocessingOutputStagingOptions
        {
            Enabled = true,
            LocalRootPath = shared,
            StoreReference = "managed-features",
        });
        _jobs.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(_records.GetValueOrDefault(call.ArgAt<string>(0))));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedOutput_ProducerRemoved_ConsumerAndOgcReadExactSharedBytes(bool raw)
    {
        var producer = await Produce();
        producer.Result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        DurableFeatureStream.TryParse(producer.Reference, out var descriptor).Should().BeTrue();
        producer.Reference.Should().NotContain(_producerRoot);
        Directory.EnumerateFiles(_producerRoot, "*", SearchOption.AllDirectories).Should().BeEmpty();
        Directory.Delete(_producerRoot, recursive: true);

        // A distinct store client and a different scratch root model another worker.
        var storeB = NewStore();
        var consumer = Job("consumer", "transform.attribute-filter", new Dictionary<string, string>
        {
            ["input"] = producer.Reference,
            ["field"] = "id",
            ["value"] = "one",
        });
        var consumed = await Run(consumer, new AttributeFilterTransformExecutor(ExecutorOptions(_consumerRoot)), storeB);
        consumed.Result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        var output = await Read(consumed.Reference, _records["consumer"], storeB);
        output.Should().ContainSingle();
        output[0].Geometry.Coordinate.X.Should().Be(12);
        output[0].Geometry.Coordinate.Y.Should().Be(34);
        output[0].Geometry.SRID.Should().Be(4326);
        output[0].Attributes.GetOptionalValue("payload").Should().Be(_payload);

        var rendered = await Render(producer.Reference, _records["producer"], storeB, raw);
        rendered.Status.Should().Be(200);
        using var json = JsonDocument.Parse(rendered.Body);
        var value = raw ? json.RootElement : json.RootElement.GetProperty("outputFeatureLayer").GetProperty("value");
        var features = value.GetProperty("features");
        features.GetArrayLength().Should().Be(1);
        features[0].GetProperty("properties").GetProperty("payload").GetString().Should().Be(_payload);
        features[0].GetProperty("geometry").GetProperty("coordinates")[0].GetDouble().Should().Be(12);
        rendered.Body.Should().NotContain(_staging.LocalRootPath!);
        descriptor!.Bytes.Should().BeGreaterThan(FeatureStreamArtifact.DefaultInlineThresholdBytes);
    }

    [UnitTest]
    public async Task ManagedRead_RejectsOtherOwnerTenantForgedReceiptAndStaleAttemptBeforeYield()
    {
        var produced = await Produce();
        var original = _records["producer"];
        var store = NewStore();
        foreach (var (owner, tenant) in new[] { ("bob", "tenant-a"), ("alice", "tenant-b") })
        {
            var other = Job("other", "transform.attribute-filter", new Dictionary<string, string>(), owner, tenant);
            await FluentActions.Awaiting(() => Read(produced.Reference, other, store)).Should().ThrowAsync<InvalidOperationException>();
        }

        DurableFeatureStream.TryParse(produced.Reference, out var descriptor).Should().BeTrue();
        var forged = (descriptor! with { Sha256 = new string('a', 64) }).Serialize();
        await FluentActions.Awaiting(() => Read(forged, original, store)).Should().ThrowAsync<InvalidOperationException>();
        _records["producer"] = original with { AttemptCount = 2 };
        await FluentActions.Awaiting(() => Read(produced.Reference, original, store)).Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTest]
    public async Task ManagedRead_CorruptionAndMissingAttestation_FailBeforePublishingValues()
    {
        var produced = await Produce();
        var original = _records["producer"];
        DurableFeatureStream.TryParse(produced.Reference, out var descriptor).Should().BeTrue();
        var path = Path.Join(_staging.LocalRootPath!, descriptor!.ObjectKey);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[10] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);
        var store = NewStore();
        await FluentActions.Awaiting(() => Read(produced.Reference, original, store)).Should().ThrowAsync<InvalidOperationException>();
        var response = await Render(produced.Reference, original, store, raw: false);
        response.Status.Should().Be(500);
        response.Body.Should().NotContain(_payload);

        File.Delete(Path.Join(_staging.LocalRootPath!, GeoprocessingOutputStoreAttestation.FileName));
        var openHost = () => NewStore();
        openHost.Should().Throw<InvalidOperationException>();
        await FluentActions.Awaiting(() => Read(produced.Reference, original, store)).Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTest]
    public async Task ManagedSpill_WithoutSharedStorage_FailsAndPublishesNothing()
    {
        var options = ExecutorOptions(_producerRoot);
        var source = new GeoJsonSourceExecutor(options);
        var dispatcher = new GeoprocessingDispatchJobExecutor([source], NullLogger<GeoprocessingDispatchJobExecutor>.Instance);
        var context = Substitute.For<IJobExecutionContext>();
        var result = await dispatcher.ExecuteAsync(SourceJob(), context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("shared persistent output staging");
        await context.DidNotReceive().PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Directory.Exists(_producerRoot).Should().BeFalse();
    }

    [UnitTest]
    public async Task ManagedSpill_InterruptedEnumeration_DeletesTemporaryBytes()
    {
        var context = new ManagedFeatureStreamContext(Substitute.For<IJobExecutionContext>(), SourceJob(), NewStore(), _jobs, _staging);
        var attributes = new AttributesTable { { "payload", _payload } };
        var feature = new Feature(null, attributes);
        await FluentActions.Awaiting(() => FeatureStreamPublisher.PublishAsync(
            Interrupted(feature), "source.geojson", 1, _producerRoot, "producer", CancellationToken.None,
            executionContext: context)).Should().ThrowAsync<OperationCanceledException>();
        Directory.EnumerateFiles(_producerRoot, "*", SearchOption.AllDirectories).Should().BeEmpty();
        Directory.EnumerateFiles(_staging.LocalRootPath!, "*.ndjson", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [UnitTest]
    public async Task ConfiguredInlineCeiling_StagesSmallStreamAboveOperatorLimit()
    {
        _staging.MaxInlineArtifactBytes = 1024;
        GeoprocessingOutputStoreTestHelper.Attest(_staging);
        var context = new ManagedFeatureStreamContext(Substitute.For<IJobExecutionContext>(), SourceJob(), NewStore(), _jobs, _staging);
        var feature = new Feature(null, new AttributesTable { { "payload", new string('x', 2048) } });
        var result = await FeatureStreamPublisher.PublishAsync(One(feature), "source.geojson", 1, _producerRoot,
            "producer", CancellationToken.None, executionContext: context);
        DurableFeatureStream.TryParse(result.ArtifactReference, out var receipt).Should().BeTrue();
        receipt!.Bytes.Should().BeGreaterThan(1024).And.BeLessThan(FeatureStreamArtifact.DefaultInlineThresholdBytes);
    }

    [UnitTest]
    public async Task PromotedWorkspaceStream_AfterProducerExpiry_RemainsOwnedReadableAndRetainedUntilCleanup()
    {
        var produced = await Produce();
        var producer = _records["producer"];
        var store = NewStore();
        DurableFeatureStream.TryParse(produced.Reference, out var descriptor).Should().BeTrue();
        var artifacts = Substitute.For<IArtifactStore, IAtomicWorkspaceStore, IWorkspaceStreamReferenceStore>();
        var rows = new Dictionary<string, Artifact>();
        var workspaces = Substitute.For<IWorkspaceStore>();
        var source = new Workspace
        {
            WorkspaceId = "scratch",
            Kind = WorkspaceKind.Scratch,
            Label = "scratch",
            OwnerId = "alice",
            ScopeId = "tenant-a",
            State = WorkspaceLifecycleState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var target = source with { WorkspaceId = "saved", Kind = WorkspaceKind.Persistent };
        workspaces.GetAsync("scratch", Arg.Any<CancellationToken>()).Returns(source);
        workspaces.GetAsync("saved", Arg.Any<CancellationToken>()).Returns(target);
        artifacts.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => rows.GetValueOrDefault(call.ArgAt<string>(0)));
        var atomic = (IAtomicWorkspaceStore)artifacts;
        atomic.PublishAsync(Arg.Any<Artifact>(), Arg.Any<bool>(), Arg.Any<WorkspaceQuota>(),
                Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var artifact = call.ArgAt<Artifact>(0);
                if (!await call.ArgAt<Func<CancellationToken, Task<bool>>>(3)(CancellationToken.None))
                {
                    return null;
                }
                rows[artifact.ArtifactId] = artifact;
                return artifact;
            });
        atomic.CreateArtifactWithQuotaAsync(Arg.Any<Artifact>(), Arg.Any<WorkspaceQuota>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var artifact = call.ArgAt<Artifact>(0);
                rows[artifact.ArtifactId] = artifact;
                return artifact;
            });
        artifacts.TransitionStateAsync(Arg.Any<string>(), Arg.Any<ArtifactLifecycleState>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.ArgAt<string>(0);
                rows[id] = rows[id] with { State = call.ArgAt<ArtifactLifecycleState>(1) };
                return true;
            });
        ((IWorkspaceStreamReferenceStore)artifacts).HasRetainedStreamAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => rows.Values.Any(row => row.Metadata.GetValueOrDefault(WorkspaceStreamProvenance.ObjectKey) == call.ArgAt<string>(0)
                && row.Metadata.GetValueOrDefault(WorkspaceStreamProvenance.StoreReference) == call.ArgAt<string>(1)));
        var retention = Substitute.For<IRetentionPolicyEvaluator>();
        retention.IsEligibleForPromotion(WorkspaceKind.Scratch, WorkspaceLifecycleState.Active).Returns(true);
        var lifecycle = new WorkspaceLifecycleService(workspaces, artifacts, retention, Options.Create(new WorkspaceOptions()),
            TimeProvider.System, NullLogger<WorkspaceLifecycleService>.Instance);
        var published = await lifecycle.PublishArtifactAsync(new WorkspaceArtifactPublication("scratch", "producer", 0,
            ArtifactKind.FeatureLayer, "features", false, produced.Reference), _ => Task.FromResult(true));
        published!.SizeBytes.Should().Be(descriptor!.Bytes);
        var promotion = await lifecycle.PromoteArtifactAsync(new ArtifactPromotionRequest
        {
            SourceWorkspaceId = "scratch",
            TargetWorkspaceId = "saved",
            ArtifactId = published.ArtifactId,
        });
        promotion.Succeeded.Should().BeTrue();
        var promoted = rows[promotion.PromotedArtifactId!];
        promoted.Uri.Should().NotBe(produced.Reference);
        _records.Remove("producer");
        await using var services = new ServiceCollection().AddSingleton(artifacts).AddSingleton(workspaces).BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        (await Read(promoted.Uri!, producer, store, scopes)).Should().ContainSingle();
        await FluentActions.Awaiting(() => Read(promoted.Uri!, Job("other", "source.geojson", [], "bob"), store, scopes))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => Read(promoted.Uri!, Job("other", "source.geojson", [], tenant: "tenant-b"), store, scopes))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => lifecycle.AddArtifactAsync("saved", ArtifactKind.FeatureLayer, "forged", promoted.Uri))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => lifecycle.AddArtifactAsync("saved", ArtifactKind.FeatureLayer, "forged-marker",
            metadata: new Dictionary<string, string> { [WorkspaceStreamProvenance.Published] = "v1" }))
            .Should().ThrowAsync<ArgumentException>();

        var path = Path.Join(_staging.LocalRootPath!, descriptor.ObjectKey);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
        // Remove the read lease so retention, not the preceding read, decides the sweep.
        var fakeStore = Substitute.For<IGeoprocessingOutputObjectStore>();
        fakeStore.StoreReference.Returns(store.StoreReference);
        fakeStore.ListAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Staged(descriptor));
        fakeStore.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var sweeper = new GeoprocessingOutputArtifactSweeper(fakeStore, _jobs, Monitor(_staging),
            NullLogger<GeoprocessingOutputArtifactSweeper>.Instance, scopes);
        (await sweeper.SweepOnceAsync(CancellationToken.None)).Deleted.Should().Be(0);
        rows.Clear();
        (await sweeper.SweepOnceAsync(CancellationToken.None)).Deleted.Should().Be(1);
        await FluentActions.Awaiting(() => Read(promoted.Uri!, producer, store, scopes)).Should().ThrowAsync<InvalidOperationException>();
    }

    private static async IAsyncEnumerable<GeoprocessingStagedObjectInfo> Staged(DurableFeatureStream descriptor)
    {
        yield return new GeoprocessingStagedObjectInfo(descriptor.ObjectKey, descriptor.Bytes, DateTimeOffset.UtcNow.AddDays(-8));
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<IFeature> One(IFeature feature)
    {
        yield return feature;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<IFeature> Interrupted(IFeature feature)
    {
        yield return feature;
        await Task.Yield();
        throw new OperationCanceledException();
    }

    private Task<(JobExecutionResult Result, string Reference)> Produce()
        => Run(SourceJob(), new GeoJsonSourceExecutor(ExecutorOptions(_producerRoot)), NewStore());

    private ExecutionJobRecord SourceJob() => Job("producer", "source.geojson", new Dictionary<string, string>
    {
        ["inline"] = "{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[12,34]},\"properties\":{\"id\":\"one\",\"payload\":\"" + _payload + "\"}}]}",
    });

    private async Task<(JobExecutionResult Result, string Reference)> Run(ExecutionJobRecord job, IProcessExecutor executor, IGeoprocessingOutputObjectStore store)
    {
        _records[job.OperationId] = job;
        var context = Substitute.For<IJobExecutionContext>();
        string? reference = null;
        context.When(item => item.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                reference = call.ArgAt<string>(0);
                _records[job.OperationId] = job with { ArtifactReferences = [reference] };
            });
        var dispatcher = new GeoprocessingDispatchJobExecutor([executor], NullLogger<GeoprocessingDispatchJobExecutor>.Instance,
            outputStore: store, jobStore: _jobs, stagingOptions: Monitor(_staging));
        var result = await dispatcher.ExecuteAsync(job, context, CancellationToken.None);
        _records[job.OperationId] = _records[job.OperationId] with { Status = result.Status };
        reference.Should().NotBeNull();
        return (result, reference!);
    }

    private async Task<List<IFeature>> Read(string reference, ExecutionJobRecord authorized, IGeoprocessingOutputObjectStore store, IServiceScopeFactory? workspaceScopes = null)
    {
        var features = new List<IFeature>();
        await foreach (var feature in DurableFeatureStream.ReadAsync(reference, authorized, _jobs, store, _staging, workspaceScopes: workspaceScopes))
        {
            features.Add(feature);
        }
        return features;
    }

    private async Task<(int Status, string Body)> Render(string reference, ExecutionJobRecord job, IGeoprocessingOutputObjectStore store, bool raw)
    {
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton(store).AddSingleton(_jobs).AddSingleton(Monitor(_staging))
            .AddSingleton<IGeoprocessingFeatureStreamReader, GeoprocessingFeatureStreamReader>()
            .Configure<GeoprocessingExecutorOptions>(options => options.MaxArtifactBytes = 4 * 1024 * 1024)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        await using var body = new MemoryStream();
        context.Response.Body = body;
        var package = AnalysisResultPackage.CreateCompleted("stream-result", new ResultSummary { Title = "output" },
            [new ArtifactRef { ArtifactId = "features", Kind = ArtifactKind.FeatureLayer, Label = "outputFeatureLayer", Uri = reference }],
            [], new ProvenanceRecord { Sources = [], ProcessDefinitions = ["source.geojson"], ExecutedAt = DateTimeOffset.UtcNow });
        var result = raw ? await JobEndpoints.BuildRawResultsResponseAsync(job, context, package)
            : await JobEndpoints.BuildValueResultsResponseAsync(job, context, package);
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    private FileSystemGeoprocessingOutputObjectStore NewStore() => new(Options.Create(_staging));

    private static IOptionsMonitor<GeoprocessingExecutorOptions> ExecutorOptions(string root)
        => Monitor(new GeoprocessingExecutorOptions { OutputRootDirectory = root });

    private static IOptionsMonitor<T> Monitor<T>(T value) where T : class
    {
        var monitor = Substitute.For<IOptionsMonitor<T>>();
        monitor.CurrentValue.Returns(value);
        return monitor;
    }

    private static ExecutionJobRecord Job(string id, string process, Dictionary<string, string> inputs, string owner = "alice", string tenant = "tenant-a")
    {
        var parameters = inputs.ToDictionary(item => Honua.ControlPlane.ExecutionJobParameterKeys.GeoprocessingStepInputPrefix + "0." + item.Key, item => item.Value);
        parameters[Honua.ControlPlane.ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = process;
        parameters["protocolProcessId"] = process;
        return new ExecutionJobRecord
        {
            OperationId = id,
            AttemptCount = 1,
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Audit = new OperationAuditInfo
            {
                RequestedBy = owner,
                SubmitterSecurityContext = new JobSecurityContext(owner, tenant, []) { OwnerActorId = owner },
            },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = process,
                Parameters = parameters,
            },
        };
    }
}
