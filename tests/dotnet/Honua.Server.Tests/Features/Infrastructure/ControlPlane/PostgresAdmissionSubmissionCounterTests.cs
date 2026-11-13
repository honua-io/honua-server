// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresAdmissionSubmissionCounterTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private const string Alice = "tenant:tenant-a:alice";
    private static readonly TimeSpan _window = TimeSpan.FromMinutes(1);

    [IntegrationTest]
    public async Task CountAsync_MixedJobs_CountsOnlyThePrincipalsJobsOfTheKindCreatedInsideTheWindow()
    {
        await using var harness = await CreateHarnessAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(harness,
            Job(Alice, ExecutionJobKind.Geoprocessing, now.AddSeconds(-10)),
            Job(Alice, ExecutionJobKind.Geoprocessing, now.AddSeconds(-1)),
            Job(Alice, ExecutionJobKind.Geoprocessing, now - _window - TimeSpan.FromSeconds(30)),
            Job(Alice, ExecutionJobKind.ExtractTransformLoad, now),
            Job("tenant:tenant-a:bob", ExecutionJobKind.Geoprocessing, now),
            Job(ratePrincipal: null, ExecutionJobKind.Geoprocessing, now));

        var counted = await harness.JobStore.CountAsync(AliceWindow(countCap: 100));

        counted.Should().Be(2);
    }

    [IntegrationTest]
    public async Task CountAsync_MoreJobsThanTheCap_StopsAtTheCap()
    {
        await using var harness = await CreateHarnessAsync();
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(harness, Enumerable.Range(1, 5)
            .Select(second => Job(Alice, ExecutionJobKind.Geoprocessing, now.AddSeconds(-second)))
            .ToArray());

        var counted = await harness.JobStore.CountAsync(AliceWindow(countCap: 3));

        counted.Should().Be(3);
    }

    private Task<PostgresJobSubstrateHarness> CreateHarnessAsync()
        => PostgresJobSubstrateHarness.CreateAsync(database, nameof(PostgresAdmissionSubmissionCounterTests));

    private static AdmissionSubmissionWindow AliceWindow(int countCap)
        => new(ExecutionJobKind.Geoprocessing, Alice, _window, countCap);

    private static async Task SeedAsync(PostgresJobSubstrateHarness harness, params ExecutionJobRecord[] jobs)
    {
        foreach (var job in jobs)
        {
            (await harness.JobStore.TryCreateAsync(job)).Should().BeTrue();
        }
    }

    private static ExecutionJobRecord Job(string? ratePrincipal, ExecutionJobKind kind, DateTimeOffset createdAt)
        => new()
        {
            OperationId = $"admission-count-{Guid.NewGuid():N}",
            Status = ExecutionJobStatus.Queued,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            AdmissionRatePrincipal = ratePrincipal,
            Spec = new ExecutionJobSpec
            {
                Kind = kind,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBatchComputeBackend.BackendId,
                WorkloadName = "admission-submission-count"
            }
        };
}
