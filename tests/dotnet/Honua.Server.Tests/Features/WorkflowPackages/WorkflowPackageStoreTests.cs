// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Server.Features.WorkflowPackages;
using Honua.TestKit.Attributes;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.WorkflowPackages;

/// <summary>
/// Shared contract tests for <see cref="IWorkflowPackageStore"/> implementations.
/// Both the in-memory fallback and the Redis-backed durable store must satisfy the
/// same package, version, and publication semantics (#1593).
/// </summary>
public abstract class WorkflowPackageStoreContractTests
{
    private protected abstract IWorkflowPackageStore CreateStore();

    [UnitTest]
    public async Task SavePackageAsync_NewPackage_RoundTripsThroughGet()
    {
        var store = CreateStore();
        var package = CreatePackage("pkg-round-trip", "Round Trip");

        var saved = await store.SavePackageAsync(package);
        var loaded = await store.GetPackageAsync(package.PackageId);

        saved.LatestVersion.Should().BeNull();
        loaded.Should().NotBeNull();
        loaded.Should().BeEquivalentTo(package);
    }

    [UnitTest]
    public async Task GetPackageAsync_UnknownId_ReturnsNull()
    {
        var store = CreateStore();

        (await store.GetPackageAsync("missing")).Should().BeNull();
    }

    [UnitTest]
    public async Task ListPackagesAsync_MultiplePackages_OrdersByNameOrdinal()
    {
        var store = CreateStore();
        await store.SavePackageAsync(CreatePackage("pkg-b", "Bravo"));
        await store.SavePackageAsync(CreatePackage("pkg-a", "Alpha"));
        await store.SavePackageAsync(CreatePackage("pkg-c", "Charlie"));

        var packages = await store.ListPackagesAsync();

        packages.Select(package => package.Name).Should().Equal("Alpha", "Bravo", "Charlie");
    }

    [UnitTest]
    public async Task CreateVersionAsync_UnknownPackage_ThrowsKeyNotFound()
    {
        var store = CreateStore();

        await FluentActions
            .Invoking(() => store.CreateVersionAsync("missing", "hash", WorkflowPackageValidationResult.Success("hash"), createdBy: null))
            .Should()
            .ThrowAsync<KeyNotFoundException>();
    }

    [UnitTest]
    public async Task CreateVersionAsync_AssignsMonotonicVersions_AndUpdatesLatestVersion()
    {
        var store = CreateStore();
        var package = CreatePackage("pkg-versions", "Versions");
        await store.SavePackageAsync(package);

        var first = await store.CreateVersionAsync(package.PackageId, "hash-1", WorkflowPackageValidationResult.Success("hash-1"), "operator");
        var second = await store.CreateVersionAsync(package.PackageId, "hash-2", WorkflowPackageValidationResult.Success("hash-2"), "operator");

        first.Version.Should().Be(1);
        second.Version.Should().Be(2);
        second.PackageHash.Should().Be("hash-2");
        second.SchemaVersion.Should().Be(package.Graph.SchemaVersion);

        var loaded = await store.GetPackageAsync(package.PackageId);
        loaded.Should().NotBeNull();
        loaded!.LatestVersion.Should().Be(2);
    }

    [UnitTest]
    public async Task SavePackageAsync_AfterVersionsExist_RetainsLatestVersionFromVersionHistory()
    {
        var store = CreateStore();
        var package = CreatePackage("pkg-resave", "Resave");
        await store.SavePackageAsync(package);
        await store.CreateVersionAsync(package.PackageId, "hash-1", WorkflowPackageValidationResult.Success("hash-1"), "operator");

        // A draft re-save that carries no version knowledge must not clobber LatestVersion.
        var resaved = await store.SavePackageAsync(package with { LatestVersion = null, Description = "updated" });

        resaved.LatestVersion.Should().Be(1);
        (await store.GetPackageAsync(package.PackageId))!.LatestVersion.Should().Be(1);
    }

    [UnitTest]
    public async Task GetVersionAsync_UnknownVersion_ReturnsNull()
    {
        var store = CreateStore();
        var package = CreatePackage("pkg-no-version", "No Version");
        await store.SavePackageAsync(package);

        (await store.GetVersionAsync(package.PackageId, 7)).Should().BeNull();
    }

    [UnitTest]
    public async Task ListVersionsAsync_ReturnsVersionsDescending()
    {
        var store = CreateStore();
        var package = CreatePackage("pkg-list-versions", "List Versions");
        await store.SavePackageAsync(package);
        await store.CreateVersionAsync(package.PackageId, "hash-1", WorkflowPackageValidationResult.Success("hash-1"), "operator");
        await store.CreateVersionAsync(package.PackageId, "hash-2", WorkflowPackageValidationResult.Success("hash-2"), "operator");
        await store.CreateVersionAsync(package.PackageId, "hash-3", WorkflowPackageValidationResult.Success("hash-3"), "operator");

        var versions = await store.ListVersionsAsync(package.PackageId);

        versions.Select(version => version.Version).Should().Equal(3, 2, 1);
    }

    [UnitTest]
    public async Task ListVersionsAsync_UnknownPackage_ReturnsEmpty()
    {
        var store = CreateStore();

        (await store.ListVersionsAsync("missing")).Should().BeEmpty();
    }

    [UnitTest]
    public async Task SavePublicationAsync_RoundTripsThroughGet()
    {
        var store = CreateStore();
        var publication = CreatePublication("pub-round-trip", "pkg-1", DateTimeOffset.UtcNow);

        await store.SavePublicationAsync(publication);
        var loaded = await store.GetPublicationAsync(publication.PublicationId);

        loaded.Should().NotBeNull();
        loaded.Should().BeEquivalentTo(publication);
    }

    [UnitTest]
    public async Task SavePublicationAsync_DuplicateId_ThrowsConflict()
    {
        var store = CreateStore();
        var publication = CreatePublication("pub-dup", "pkg-1", DateTimeOffset.UtcNow);
        await store.SavePublicationAsync(publication);

        await FluentActions
            .Invoking(() => store.SavePublicationAsync(publication with { PackageId = "pkg-other" }))
            .Should()
            .ThrowAsync<InvalidOperationException>();

        // The original publication must remain untouched.
        (await store.GetPublicationAsync(publication.PublicationId))!.PackageId.Should().Be("pkg-1");
    }

    [UnitTest]
    public async Task GetPublicationAsync_UnknownId_ReturnsNull()
    {
        var store = CreateStore();

        (await store.GetPublicationAsync("missing")).Should().BeNull();
    }

    [UnitTest]
    public async Task ListPublicationsAsync_FiltersByPackageId_AndOrdersByCreatedAtDescending()
    {
        var store = CreateStore();
        var baseline = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await store.SavePublicationAsync(CreatePublication("pub-1", "pkg-a", baseline));
        await store.SavePublicationAsync(CreatePublication("pub-2", "pkg-a", baseline.AddMinutes(2)));
        await store.SavePublicationAsync(CreatePublication("pub-3", "pkg-b", baseline.AddMinutes(1)));

        var all = await store.ListPublicationsAsync();
        var filtered = await store.ListPublicationsAsync("pkg-a");

        all.Select(publication => publication.PublicationId).Should().Equal("pub-2", "pub-3", "pub-1");
        filtered.Select(publication => publication.PublicationId).Should().Equal("pub-2", "pub-1");
    }

    private protected static WorkflowPackage CreatePackage(string packageId, string name) => new()
    {
        PackageId = packageId,
        Name = name,
        Description = "Contract test package",
        Namespace = "tests",
        Graph = CreateGraph(),
        CreatedAt = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 5, 2, 9, 30, 0, TimeSpan.Zero),
        CreatedBy = "operator",
        UpdatedBy = "operator",
        Metadata = new Dictionary<string, string> { ["origin"] = "contract-test" }
    };

    private protected static WorkflowGraph CreateGraph() => new()
    {
        Nodes =
        [
            new WorkflowNode
            {
                NodeId = "n1",
                NodeTypeId = "geoprocessing.buffer",
                Parameters = new Dictionary<string, string> { ["distance"] = "10" }
            },
            new WorkflowNode
            {
                NodeId = "n2",
                NodeTypeId = "geoprocessing.dissolve"
            }
        ],
        Edges = [new WorkflowEdge { SourceNodeId = "n1", TargetNodeId = "n2" }],
        Schedule = new WorkflowSchedule { CronExpression = "0 4 * * *", TimeZone = "UTC" }
    };

    private protected static WorkflowPublication CreatePublication(
        string publicationId,
        string packageId,
        DateTimeOffset createdAt) => new()
        {
            PublicationId = publicationId,
            PackageId = packageId,
            PackageVersion = 1,
            PackageHash = "hash-1",
            Target = WorkflowPublicationTarget.Job,
            Eligibility = WorkflowPackageValidationResult.Success("hash-1"),
            CreatedAt = createdAt,
            CreatedBy = "operator",
            Provenance = new Dictionary<string, string> { ["honua.workflow.publicationId"] = publicationId }
        };
}

/// <summary>
/// Runs the <see cref="IWorkflowPackageStore"/> contract against the in-memory fallback.
/// </summary>
[Collection("Unit")]
public sealed class InMemoryWorkflowPackageStoreContractTests : WorkflowPackageStoreContractTests
{
    private protected override IWorkflowPackageStore CreateStore() => new InMemoryWorkflowPackageStore();
}

/// <summary>
/// Runs the <see cref="IWorkflowPackageStore"/> contract against the Redis-backed store
/// using a faked <see cref="IDatabase"/> (no live Redis required), plus Redis-specific
/// durability semantics.
/// </summary>
[Collection("Unit")]
public sealed class RedisWorkflowPackageStoreContractTests : WorkflowPackageStoreContractTests
{
    private protected override IWorkflowPackageStore CreateStore()
        => new RedisWorkflowPackageStore(new FakeWorkflowPackageRedis().Redis);

    [UnitTest]
    public async Task Store_AfterSimulatedRestart_RetainsPackagesVersionsAndPublications()
    {
        var harness = new FakeWorkflowPackageRedis();
        var store = new RedisWorkflowPackageStore(harness.Redis);
        var package = CreatePackage("pkg-durable", "Durable");
        await store.SavePackageAsync(package);
        await store.CreateVersionAsync(package.PackageId, "hash-1", WorkflowPackageValidationResult.Success("hash-1"), "operator");
        await store.SavePublicationAsync(CreatePublication("pub-durable", package.PackageId, DateTimeOffset.UtcNow));

        // A fresh store over the same Redis data simulates a process restart or a
        // sibling replica sharing the instance.
        var restarted = new RedisWorkflowPackageStore(harness.Redis);

        (await restarted.GetPackageAsync(package.PackageId))!.LatestVersion.Should().Be(1);
        (await restarted.GetVersionAsync(package.PackageId, 1)).Should().NotBeNull();
        (await restarted.ListPublicationsAsync(package.PackageId)).Should().ContainSingle(
            publication => publication.PublicationId == "pub-durable");
    }

    [UnitTest]
    public async Task GetPackageAsync_StalePackageRecord_OverlaysLatestVersionFromSequence()
    {
        var harness = new FakeWorkflowPackageRedis();
        var store = new RedisWorkflowPackageStore(harness.Redis);
        var package = CreatePackage("pkg-overlay", "Overlay");
        await store.SavePackageAsync(package);
        await store.CreateVersionAsync(package.PackageId, "hash-1", WorkflowPackageValidationResult.Success("hash-1"), "operator");

        // Simulate a racing replica writing back a stale package record with an older
        // LatestVersion than the durable version counter.
        harness.SeedString(
            "workflow-packages:pkg:" + package.PackageId,
            JsonSerializer.Serialize(package with { LatestVersion = null }, WorkflowPackagesJsonContext.Default.WorkflowPackage));

        (await store.GetPackageAsync(package.PackageId))!.LatestVersion.Should().Be(1);
    }
}

/// <summary>
/// AOT-safe source-generated JSON round-trip tests for the payload models the
/// Redis-backed workflow package store persists.
/// </summary>
[Collection("Unit")]
public sealed class WorkflowPackageStoreSerializationTests
{
    [UnitTest]
    public void WorkflowPackage_RoundTripsThroughSourceGeneratedContext()
    {
        var package = new WorkflowPackage
        {
            PackageId = "pkg-json",
            Name = "Json Package",
            Description = "Round trip",
            Namespace = "tests",
            Graph = new WorkflowGraph
            {
                Nodes =
                [
                    new WorkflowNode
                    {
                        NodeId = "n1",
                        NodeTypeId = "geoprocessing.buffer",
                        Parameters = new Dictionary<string, string> { ["distance"] = "25" },
                        Metadata = new Dictionary<string, string> { ["x"] = "1" }
                    }
                ],
                Edges = [new WorkflowEdge { SourceNodeId = "n1", TargetNodeId = "n1", Kind = WorkflowEdgeKind.Data }],
                Schedule = new WorkflowSchedule { CronExpression = "*/5 * * * *", TimeZone = "Pacific/Honolulu", Enabled = true },
                WorkerProfile = "default"
            },
            LatestVersion = 3,
            CreatedAt = new DateTimeOffset(2026, 5, 1, 8, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 5, 2, 9, 30, 0, TimeSpan.Zero),
            CreatedBy = "operator",
            UpdatedBy = "operator",
            Metadata = new Dictionary<string, string> { ["origin"] = "serialization-test" }
        };

        var payload = JsonSerializer.Serialize(package, WorkflowPackagesJsonContext.Default.WorkflowPackage);
        var roundTripped = JsonSerializer.Deserialize(payload, WorkflowPackagesJsonContext.Default.WorkflowPackage);

        roundTripped.Should().BeEquivalentTo(package);
    }

    [UnitTest]
    public void WorkflowPackageVersion_RoundTripsThroughSourceGeneratedContext()
    {
        var version = new WorkflowPackageVersion
        {
            PackageId = "pkg-json",
            Version = 4,
            SchemaVersion = "workflow-package.v1",
            PackageHash = "abc123",
            Graph = new WorkflowGraph
            {
                Nodes = [new WorkflowNode { NodeId = "n1", NodeTypeId = "geoprocessing.clip" }]
            },
            Validation = WorkflowPackageValidationResult.Success("abc123", warnings: ["heads-up"]),
            CreatedAt = new DateTimeOffset(2026, 5, 3, 10, 0, 0, TimeSpan.Zero),
            CreatedBy = "operator"
        };

        var payload = JsonSerializer.Serialize(version, WorkflowPackagesJsonContext.Default.WorkflowPackageVersion);
        var roundTripped = JsonSerializer.Deserialize(payload, WorkflowPackagesJsonContext.Default.WorkflowPackageVersion);

        roundTripped.Should().BeEquivalentTo(version);
    }

    [UnitTest]
    public void WorkflowPublication_RoundTripsThroughSourceGeneratedContext()
    {
        var publication = new WorkflowPublication
        {
            PublicationId = "pub-json",
            PackageId = "pkg-json",
            PackageVersion = 4,
            PackageHash = "abc123",
            Target = WorkflowPublicationTarget.Schedule,
            Status = WorkflowPublicationStatus.Active,
            Schedule = new WorkflowSchedule { CronExpression = "0 4 * * *", TimeZone = "UTC", Enabled = true },
            WorkflowDefinitionId = "workflow-package:pkg-json:v4",
            EndpointPath = "/api/v1/console/workflow-publications/pub-json/runs",
            Eligibility = WorkflowPackageValidationResult.Failed(
                [new WorkflowPackageValidationFailure { Code = "X", Message = "failure", FieldPath = "graph" }],
                "abc123"),
            CreatedAt = new DateTimeOffset(2026, 5, 4, 11, 0, 0, TimeSpan.Zero),
            CreatedBy = "operator",
            Provenance = new Dictionary<string, string> { ["honua.workflow.packageId"] = "pkg-json" }
        };

        var payload = JsonSerializer.Serialize(publication, WorkflowPackagesJsonContext.Default.WorkflowPublication);
        var roundTripped = JsonSerializer.Deserialize(payload, WorkflowPackagesJsonContext.Default.WorkflowPublication);

        roundTripped.Should().BeEquivalentTo(publication);
    }
}

/// <summary>
/// In-process fake of the Redis primitives <see cref="RedisWorkflowPackageStore"/> uses
/// (string get/set with NX, INCR, and set indexes), mirroring the harness style used by
/// the existing Redis store atomicity tests. No live Redis required.
/// </summary>
internal sealed class FakeWorkflowPackageRedis
{
    private readonly ConcurrentDictionary<string, RedisValue> _strings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sets = new(StringComparer.Ordinal);

    public FakeWorkflowPackageRedis()
    {
        var database = Substitute.For<IDatabase>();
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var key = call.ArgAt<RedisKey>(0).ToString();
                return Task.FromResult(_strings.TryGetValue(key, out var value) ? value : RedisValue.Null);
            });

        database.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<bool>(),
                Arg.Any<When>(),
                Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var key = call.ArgAt<RedisKey>(0).ToString();
                var when = call.ArgAt<When>(4);
                if (when == When.NotExists && _strings.ContainsKey(key))
                {
                    return Task.FromResult(false);
                }

                _strings[key] = call.ArgAt<RedisValue>(1);
                return Task.FromResult(true);
            });

        database.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var key = call.ArgAt<RedisKey>(0).ToString();
                var increment = call.ArgAt<long>(1);
                var next = _strings.AddOrUpdate(
                    key,
                    _ => increment,
                    (_, current) => current.TryParse(out long value) ? value + increment : increment);
                return Task.FromResult((long)next);
            });

        database.SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var set = _sets.GetOrAdd(
                    call.ArgAt<RedisKey>(0).ToString(),
                    _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
                return Task.FromResult(set.TryAdd(call.ArgAt<RedisValue>(1).ToString(), 1));
            });

        database.SetMembersAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var key = call.ArgAt<RedisKey>(0).ToString();
                var members = _sets.TryGetValue(key, out var set)
                    ? set.Keys.Select(member => (RedisValue)member).ToArray()
                    : Array.Empty<RedisValue>();
                return Task.FromResult(members);
            });

        Redis = redis;
    }

    public IConnectionMultiplexer Redis { get; }

    public void SeedString(string key, string value) => _strings[key] = value;
}
