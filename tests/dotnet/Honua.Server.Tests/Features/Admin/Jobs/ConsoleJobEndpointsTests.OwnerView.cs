// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin.Jobs;

// The diagnostics owner view withholds worker and provider identities; detail, logs and
// steps apply the same projection, so no read endpoint serves what another withholds.
public sealed partial class ConsoleJobEndpointsTests
{
    private const string OwnerViewAdminPassword = "console-job-owner-view-admin-bootstrap-key";
    private const string OwnerViewJobId = "job-owner-view";
    private const string OwnerViewWorker = "worker-internal-9";
    private const string OwnerViewProvider = "provider-op-9";

    private static readonly string[] OwnerViewReads =
    [
        $"/api/v1/admin/jobs/{OwnerViewJobId}",
        $"/api/v1/admin/jobs/{OwnerViewJobId}/logs",
        $"/api/v1/admin/jobs/{OwnerViewJobId}/steps",
        $"/api/v1/admin/jobs/{OwnerViewJobId}/diagnostics"
    ];

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}")]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/logs")]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/steps")]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/diagnostics")]
    public async Task JobReads_ScopedReadKey_WithholdWorkerAndProviderIdentitiesOnEveryEndpoint()
    {
        var fixture = await StartOwnerViewFixtureAsync();
        try
        {
            using var admin = fixture.CreateClient(
                client => client.DefaultRequestHeaders.Add("X-API-Key", OwnerViewAdminPassword));
            var readKey = await CreateApiKeyAsync(admin, "console-job-owner-view-read-only", ["admin:read"]);
            using var reader = fixture.CreateClient(
                client => client.DefaultRequestHeaders.Add("X-API-Key", readKey.Key));

            foreach (var path in OwnerViewReads)
            {
                var body = await ReadOkBodyAsync(reader, path);
                body.Should().NotContain(OwnerViewWorker, "{0} is read with an owner view", path);
                body.Should().NotContain(OwnerViewProvider, "{0} is read with an owner view", path);
            }

            foreach (var path in OwnerViewReads[1..])
            {
                (await ReadOkBodyAsync(reader, path)).Should()
                    .Contain("Attempt 1 of 3 lost", "{0} keeps the rest of the lost-attempt record", path);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}")]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/logs")]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/steps")]
    public async Task JobReads_ExecuteScopedOperator_ReceiveWorkerAndProviderIdentities()
    {
        var fixture = await StartOwnerViewFixtureAsync();
        try
        {
            using var admin = fixture.CreateClient(
                client => client.DefaultRequestHeaders.Add("X-API-Key", OwnerViewAdminPassword));

            using var detail = JsonDocument.Parse(await ReadOkBodyAsync(admin, OwnerViewReads[0]));
            detail.RootElement.GetProperty("claimedBy").GetString().Should().Be(OwnerViewWorker);
            detail.RootElement.GetProperty("providerOperationId").GetString().Should().Be(OwnerViewProvider);

            using var logs = JsonDocument.Parse(await ReadOkBodyAsync(admin, OwnerViewReads[1]));
            logs.RootElement.GetProperty("items")[0].GetProperty("metadata").GetProperty("worker.id").GetString()
                .Should().Be(OwnerViewWorker);

            using var steps = JsonDocument.Parse(await ReadOkBodyAsync(admin, OwnerViewReads[2]));
            steps.RootElement.GetProperty("steps")[0].GetProperty("metadata").GetProperty("worker.id").GetString()
                .Should().Be(OwnerViewWorker);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<string> ReadOkBodyAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "{0}: {1}", path, body);
        return body;
    }

    private static async Task<WebAppFixture> StartOwnerViewFixtureAsync()
    {
        var jobStore = new InMemoryJobStore();
        var now = DateTimeOffset.UtcNow;
        jobStore.Set(CreateJob(OwnerViewJobId, ExecutionJobStatus.Running, now.AddMinutes(-3), "corr-owner-view") with
        {
            CurrentPhase = "Running",
            ClaimedBy = OwnerViewWorker,
            ClaimedAt = now.AddMinutes(-1),
            LastHeartbeatAt = now,
            ProviderOperationId = OwnerViewProvider,
            AttemptCount = 2,
            RetryPolicy = new JobRetryPolicy { MaxAttempts = 3 }
        });

        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", OwnerViewAdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IExecutionJobStore>();
                services.RemoveAll<IExecutionLogStore>();
                services.AddSingleton<IExecutionJobStore>(jobStore);
                services.AddSingleton<IExecutionLogStore>(LostAttemptLogStore(OwnerViewJobId, OwnerViewWorker));
            });
        await fixture.InitializeAsync();
        return fixture;
    }
}
