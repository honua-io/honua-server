// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing.CustomCode;
using Honua.Server.Features.Geoprocessing.CustomCode;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class CustomCodeCapabilityRedemptionEndpointsTests : IAsyncLifetime
{
    private const string JobId = "redeem-job";
    private const string Capability = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Code = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
    private const string RedeemPath = "/api/geoprocessing/jobs/redeem-job/attempts/1/capability";
    private readonly ConcurrentDictionary<string, ExecutionJobRecord> _records = new(StringComparer.Ordinal);
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public CustomCodeCapabilityRedemptionEndpointsTests()
    {
        var jobs = Substitute.For<IExecutionJobStore>();
        jobs.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(_records.GetValueOrDefault(call.ArgAt<string>(0))));
        jobs.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var next = call.ArgAt<ExecutionJobRecord>(0);
                return _records.TryGetValue(next.OperationId, out var current) && current.Version == next.Version
                    && _records.TryUpdate(next.OperationId, next with { Version = next.Version + 1 }, current);
            });
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IExecutionJobStore>();
                services.AddSingleton(jobs);
                services.RemoveAll<IJobQueue>();
                services.AddSingleton(Substitute.For<IJobQueue>());
            });
    }

    public async Task InitializeAsync()
    {
        _records[JobId] = Job(ExecutionJobStatus.Queued, DateTimeOffset.UtcNow.AddMinutes(10));
        await _fixture.InitializeAsync();
        _client = _fixture.CreateClient();
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/geoprocessing/jobs/{jobId}/attempts/{attempt}/capability")]
    public async Task Redeem_ValidCode_ReturnsCapabilityOnceAndSpendsTheCode()
    {
        using var first = await RedeemAsync(RedeemPath, Code);
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        (await first.Content.ReadAsStringAsync()).Should().Be(Capability);
        first.Headers.CacheControl!.NoStore.Should().BeTrue();
        _records[JobId].Spec.Parameters.Should().NotContainKey(CustomCodeCapabilityRedemptionEndpoints.RedemptionCodeParameter);
        _records[JobId].Spec.Parameters[CustomCodeJobContract.JobTokenEnvParam].Should().Be(Capability);

        using var replay = await RedeemAsync(RedeemPath, Code);
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await replay.Content.ReadAsStringAsync()).Should().NotContain(Capability);
    }

    [IntegrationTest]
    [Endpoint("POST /api/geoprocessing/jobs/{jobId}/attempts/{attempt}/capability")]
    public async Task Redeem_WrongCodeAttemptOrJob_IsRefusedAndLeavesTheCodeUnspent()
    {
        using var missing = await _client.PostAsync(RedeemPath, null);
        using var wrongCode = await RedeemAsync(RedeemPath, new string('a', 64));
        using var wrongAttempt = await RedeemAsync("/api/geoprocessing/jobs/redeem-job/attempts/2/capability", Code);
        using var wrongJob = await RedeemAsync("/api/geoprocessing/jobs/other-job/attempts/1/capability", Code);
        using var upperCase = await RedeemAsync(RedeemPath, Code.ToUpperInvariant());

        foreach (var response in new[] { missing, wrongCode, wrongAttempt, wrongJob, upperCase })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await response.Content.ReadAsStringAsync()).Should().NotContain(Capability);
        }

        _records[JobId].Spec.Parameters.Should().ContainKey(CustomCodeCapabilityRedemptionEndpoints.RedemptionCodeParameter);
    }

    [IntegrationTest]
    [Endpoint("POST /api/geoprocessing/jobs/{jobId}/attempts/{attempt}/capability")]
    public async Task Redeem_AttemptNotDispatchedYet_AsksTheHarnessToRetry()
    {
        var records = new[]
        {
            Job(ExecutionJobStatus.Provisioning, DateTimeOffset.UtcNow.AddMinutes(10)) with { AttemptCount = 0, ProviderOperationId = null },
            Job(ExecutionJobStatus.Queued, DateTimeOffset.UtcNow.AddMinutes(10)) with { ProviderOperationId = null },
        };

        foreach (var record in records)
        {
            _records[JobId] = record;
            using var response = await RedeemAsync(RedeemPath, Code);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        _records[JobId].Spec.Parameters.Should().ContainKey(CustomCodeCapabilityRedemptionEndpoints.RedemptionCodeParameter);
    }

    [IntegrationTest]
    [Endpoint("POST /api/geoprocessing/jobs/{jobId}/attempts/{attempt}/capability")]
    public async Task Redeem_AfterStartDeadlineCancellationOrTerminal_IsGone()
    {
        var records = new[]
        {
            Job(ExecutionJobStatus.Running, DateTimeOffset.UtcNow.AddSeconds(-1)),
            Job(ExecutionJobStatus.Running, DateTimeOffset.UtcNow.AddMinutes(10)) with { CancellationRequestedAt = DateTimeOffset.UtcNow },
            Job(ExecutionJobStatus.Failed, DateTimeOffset.UtcNow.AddMinutes(10)),
            Job(ExecutionJobStatus.Queued, DateTimeOffset.UtcNow.AddMinutes(10)) with { NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(1) },
        };

        foreach (var record in records)
        {
            _records[JobId] = record;
            using var response = await RedeemAsync(RedeemPath, Code);
            response.StatusCode.Should().Be(HttpStatusCode.Gone);
            (await response.Content.ReadAsStringAsync()).Should().NotContain(Capability);
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/geoprocessing/jobs/{jobId}/attempts/{attempt}/capability")]
    public async Task Redeem_ConcurrentRequests_ReleaseTheCapabilityExactlyOnce()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RedeemAsync(RedeemPath, Code)));
        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
            responses.Where(response => response.StatusCode != HttpStatusCode.OK)
                .Should().OnlyContain(response => response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Conflict);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    private async Task<HttpResponseMessage> RedeemAsync(string path, string code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add(CustomCodeCapabilityRedemptionEndpoints.CodeHeader, code);
        return await _client.SendAsync(request);
    }

    private static ExecutionJobRecord Job(ExecutionJobStatus status, DateTimeOffset startDeadline) => new()
    {
        OperationId = JobId,
        Status = status,
        Version = 1,
        AttemptCount = 1,
        ProviderOperationId = "batch-job-1",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Audit = new OperationAuditInfo
        {
            RequestedBy = "alice",
            CustomCodeOwnerScope = new CustomCodeOwnerScope("alice", "public", [], [], []),
        },
        Spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.AwsBatch,
            Backend = "aws-batch",
            WorkloadId = "custom-worker",
            WorkloadName = "custom-worker",
            RuntimeProfile = CustomCodeJobContract.RuntimeProfile,
            Parameters = new Dictionary<string, string>
            {
                [CustomCodeJobContract.JobTokenEnvParam] = Capability,
                [CustomCodeCapabilityRedemptionEndpoints.RedemptionCodeParameter] = Code,
                ["env.HONUA_JOB_ID"] = JobId,
                ["env.HONUA_JOB_ATTEMPT"] = "1",
                ["env.HONUA_JOB_START_DEADLINE"] = startDeadline.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            },
        },
    };
}
