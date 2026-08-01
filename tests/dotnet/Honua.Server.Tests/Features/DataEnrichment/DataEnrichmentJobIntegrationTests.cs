// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Geoprocessing;
using Honua.ControlPlane;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.DataEnrichment;

/// <summary>
/// End-to-end coverage for the async batch enrichment job (#2283): the
/// <c>enrichment.enrich</c> process is submitted through the canonical OGC API
/// Processes execution endpoint with the enrichment vocabulary (datasetId, method,
/// outputFields), executed by the durable job runtime against the real PostGIS
/// seed layers (the enrichment dataset points at seed join layer 1, the source at
/// seed layer 0 — the same wiring as the synchronous <c>POST /api/enrich</c>
/// tests), and its lifecycle is exercised through the EXISTING job endpoints:
/// submit → poll → fetch results → dismiss. Also covers the large-input handoff:
/// the sync endpoint's 413 names this async process, and the same over-cap request
/// succeeds as a job. Edition/entitlement gating is covered at the executor and
/// synchronous-HTTP levels instead — see the note above the large-input test.
/// </summary>
[Collection("Redis")]
[Protocol(ProtocolNames.OgcApiProcesses)]
public sealed class DataEnrichmentJobIntegrationTests(RedisFixture redis)
{
    private const string DatasetKey = "test-boundaries";
    private const string ProcessId = "enrichment.enrich";
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    /// <summary>Seed layer the configured enrichment dataset is backed by.</summary>
    private const int DatasetLayerId = 1;

    /// <summary>A role no test client holds, used to lock a layer down.</summary>
    private const string RestrictedReaderRole = "enrichment-restricted-reader";

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task EnrichJob_SubmitPollFetchResults_ReturnsEnrichedFeatureCollectionWithProvenance()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture(HonuaEdition.Pro);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(60);

            var jobId = await SubmitJobAsync(client, ExecuteRequestBody());

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");

            using var resultsDoc = await GetResultsAsync(client, jobId);
            var collection = DecodeOutputFeatureCollection(resultsDoc);

            // Catalog provenance travels with the artifact (#2283): dataset id and the
            // effective method are foreign members on the FeatureCollection envelope.
            collection.GetProperty("datasetId").GetString().Should().Be(DatasetKey);
            collection.GetProperty("method").GetString().Should().Be("intersects");

            var features = collection.GetProperty("features");
            features.GetArrayLength().Should().BeGreaterThan(0);
            foreach (var feature in features.EnumerateArray())
            {
                feature.GetProperty("properties").TryGetProperty("JOIN_COUNT", out var joinCount).Should().BeTrue();
                joinCount.GetInt64().Should().BeGreaterOrEqualTo(0);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    [IntegrationTest]
    [Operation(Operations.JobDismiss)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("DELETE /ogc/processes/jobs/{jobId}")]
    public async Task EnrichJob_Dismiss_UsesExistingJobLifecycleEndpoints()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture(HonuaEdition.Pro);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(60);

            var jobId = await SubmitJobAsync(client, ExecuteRequestBody());

            // The enrichment job is dismissed through the SHARED lifecycle endpoint, so
            // it inherits that endpoint's full outcome set and the race with the worker
            // (the small seed-layer job can finish before the DELETE lands):
            //   * 200 + "dismissed"   — the job was cancelled before it went terminal;
            //   * 200 + live status   — cancellation was requested while the worker owns
            //                           terminal state (BuildDismissOutcomeResult returns
            //                           the plain status info in that case);
            //   * 409                 — the job already reached a terminal state.
            // Assert the shared contract rather than one racy branch: the endpoint
            // answered for THIS job id with a canonical OGC status, or refused it as
            // already-terminal.
            using var dismissResponse = await client.DeleteAsync($"/ogc/processes/jobs/{jobId}");
            dismissResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
            using var dismissDoc = JsonDocument.Parse(await dismissResponse.Content.ReadAsStringAsync());
            if (dismissResponse.StatusCode == HttpStatusCode.OK)
            {
                dismissDoc.RootElement.GetProperty("jobID").GetString().Should().Be(
                    jobId, "the shared dismiss endpoint must answer for the enrichment job it was given");
                dismissDoc.RootElement.GetProperty("status").GetString().Should().BeOneOf(
                    "dismissed", "accepted", "running", "successful", "failed");
            }
            else
            {
                dismissDoc.RootElement.GetProperty("detail").GetString().Should().Contain("terminal");
            }
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    // NOTE on edition gating (#2283): there is deliberately NO Community-edition job
    // test here. In Community edition the platform never starts a durable job worker at
    // all — AddJobWorker self-gates on IJobQueue, which AddJobOrchestration only
    // registers when the Redis multiplexer is wired under the license entitlement
    // (honua-server#1841). A Community job would therefore sit Queued forever and never
    // reach a terminal status, so such a test would assert a pre-existing platform
    // property rather than enrichment gating, and would hang rather than fail cleanly.
    // The enrichment edition gates are proven where they actually execute:
    //   * executor level — EnrichmentJobExecutorTests covers both the
    //     analytics.spatial-join entitlement denial and the dataset MinimumEdition
    //     denial, asserting the classified failure message;
    //   * HTTP level — DataEnrichmentEditionGateTests covers the Community 402 on the
    //     synchronous /api/enrich surface.

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /api/enrich")]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task EnrichJob_LargeInput_SyncReturns413NamingAsyncPath_AsyncJobSucceeds()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        // Cap the synchronous path at 1 input feature so the seed layer trips the
        // 413 guard, then run the SAME enrichment as a job — the async path has no
        // synchronous input cap.
        var fixture = BuildFixture(HonuaEdition.Pro, extraConfig: new Dictionary<string, string?>
        {
            ["Limits:Analytics:MaxInputFeatures"] = "1",
        });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(60);

            var syncPayload = JsonSerializer.Serialize(new
            {
                datasetKey = DatasetKey,
                sourceLayerId = WebAppFixture.TestLayerId,
                method = "intersects",
            });
            using var syncContent = new StringContent(syncPayload, Encoding.UTF8, "application/json");
            using var syncResponse = await client.PostAsync("/api/enrich", syncContent);

            syncResponse.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
            var syncBody = await syncResponse.Content.ReadAsStringAsync();
            syncBody.Should().Contain(ProcessId, "the sync over-limit error must reference the async batch path");

            var jobId = await SubmitJobAsync(client, ExecuteRequestBody());

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be(
                "successful", "inputs above the synchronous cap must succeed on the async job path");

            using var resultsDoc = await GetResultsAsync(client, jobId);
            var collection = DecodeOutputFeatureCollection(resultsDoc);
            collection.GetProperty("features").GetArrayLength().Should().BeGreaterThan(1);
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    // Parameter honesty at submission (#3043 review). Two ways an enrichment submission
    // could previously be accepted and then behave differently than the caller asked:
    //   * 'where'/'bbox' supplied with a staged 'input' source — documented in the catalog
    //     as layer-source-only, never applied to the inline collection, so the job
    //     "succeeded" over EVERY staged feature;
    //   * a malformed 'bbox' on the layer-backed source — cleared the generic text-type
    //     check, queued a job, and only failed deep inside the provider read.
    // Both must now be refused at the submit surface with an actionable parameter error and
    // without queueing a job.

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task EnrichJob_InlineSourceWithLayerOnlyFilters_RefusesSubmissionWithoutQueueingJob()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture(HonuaEdition.Pro);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(60);

            // Positive control: the same staged collection with no layer-only filter is
            // accepted, so the refusals below are attributable to the filters alone.
            using (var allowed = await PostExecutionAsync(client, InlineExecuteRequestBody()))
            {
                allowed.StatusCode.Should().Be(HttpStatusCode.Created);
            }

            using var withWhere = await PostExecutionAsync(
                client, InlineExecuteRequestBody(("where", "name = 'a'")));
            withWhere.StatusCode.Should().Be(
                HttpStatusCode.BadRequest,
                "'where' windows the registered source layer read and cannot be applied to a staged collection");
            var whereBody = await withWhere.Content.ReadAsStringAsync();
            whereBody.Should().Contain("where");
            whereBody.Should().NotContain("jobID", "a refused submission must not queue a job");

            using var withBbox = await PostExecutionAsync(
                client, InlineExecuteRequestBody(("bbox", "-10,-10,10,10")));
            withBbox.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var bboxBody = await withBbox.Content.ReadAsStringAsync();
            bboxBody.Should().Contain("bbox");
            bboxBody.Should().NotContain("jobID");
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task EnrichJob_MalformedBbox_RefusesSubmissionWithParameterError()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture(HonuaEdition.Pro);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(60);

            // Positive control: a well-formed four-ordinate bbox still submits.
            using (var allowed = await PostExecutionAsync(
                client, ExecuteRequestBody(extraInputs: [("bbox", "-180,-90,180,90")])))
            {
                allowed.StatusCode.Should().Be(HttpStatusCode.Created);
            }

            // Three ordinates.
            using var truncated = await PostExecutionAsync(
                client, ExecuteRequestBody(extraInputs: [("bbox", "-10,-10,10")]));
            truncated.StatusCode.Should().Be(
                HttpStatusCode.BadRequest,
                "a malformed bbox must fail as a parameter error, not as a later source-read failure");
            var truncatedBody = await truncated.Content.ReadAsStringAsync();
            truncatedBody.Should().Contain("minX,minY,maxX,maxY");
            truncatedBody.Should().NotContain("jobID", "a refused submission must not queue a job");

            // Nonnumeric ordinate.
            using var nonNumeric = await PostExecutionAsync(
                client, ExecuteRequestBody(extraInputs: [("bbox", "-10,-10,east,10")]));
            nonNumeric.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await nonNumeric.Content.ReadAsStringAsync()).Should().Contain("minX,minY,maxX,maxY");
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    // Layer read authorization (#2283 review). Process.Execute authorizes RUNNING a
    // process; it does not authorize the specific catalog layers that process reads. The
    // synchronous POST /api/enrich handler calls ValidateLayerWithAccessV2Async at
    // AccessScope.Read for BOTH the caller-selected source layer and the dataset's
    // backing layer, so the async job path must refuse the same two reads.
    //
    // Both tests model the finding's principal exactly: Process.Execute is held, but the
    // caller has NO layer-read grant, so the coarse AccessPolicy decides the read. The
    // default InMemoryRoleStore seeds `admin` with a wildcard `*:*:*` grant that would
    // authorize every layer regardless of policy, so it is replaced with a store that
    // grants only `process:*:execute` — otherwise the test would pass vacuously.
    //
    // Each test submits the SAME request twice against the same fixture — once with the
    // seed policy (accepted) and once with the layer restricted to a role the caller does
    // not hold (refused) — so the refusal is attributable to the layer policy alone.

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task EnrichJob_SubmitterLacksReadOnSourceLayer_RefusesSubmissionWithoutQueueingJob()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture(HonuaEdition.Pro, configureServices: SeedProcessExecuteOnlyRole);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(60);

            // Positive control: the identical submission is accepted while the caller can
            // read both layers.
            using (var allowed = await PostExecutionAsync(client, ExecuteRequestBody()))
            {
                allowed.StatusCode.Should().Be(
                    HttpStatusCode.Created,
                    "an entitled principal must still be able to submit the enrichment job");
            }

            fixture.UpdateV2ResourceMetadata(
                WebAppFixture.TestLayerId,
                accessPolicy: new AccessPolicy { AllowedRoles = [RestrictedReaderRole] });

            using var denied = await PostExecutionAsync(client, ExecuteRequestBody());
            denied.StatusCode.Should().Be(
                HttpStatusCode.Forbidden,
                "a caller that cannot read the source layer must not be able to read it through an async enrichment job");

            var deniedBody = await denied.Content.ReadAsStringAsync();
            deniedBody.Should().NotContain("jobID", "a refused submission must not queue a job");

            // Non-leakage: an unknown layer id must be refused with the SAME response as a
            // policy denial, so the submit surface cannot be used to probe which layers exist.
            using var unknown = await PostExecutionAsync(client, ExecuteRequestBody(sourceLayerId: 987654));
            unknown.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await unknown.Content.ReadAsStringAsync()).Should().Be(deniedBody);
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task EnrichJob_SubmitterLacksReadOnDatasetLayer_RefusesSubmissionWithoutQueueingJob()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture(HonuaEdition.Pro, configureServices: SeedProcessExecuteOnlyRole);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(60);

            using (var allowed = await PostExecutionAsync(client, ExecuteRequestBody()))
            {
                allowed.StatusCode.Should().Be(
                    HttpStatusCode.Created,
                    "an entitled principal must still be able to submit the enrichment job");
            }

            // The dataset's backing layer is never named by the caller — it is resolved
            // from the catalog — so it is the gap a caller-parameter check would miss.
            fixture.UpdateV2ResourceMetadata(
                DatasetLayerId,
                accessPolicy: new AccessPolicy { AllowedRoles = [RestrictedReaderRole] });

            using var denied = await PostExecutionAsync(client, ExecuteRequestBody());
            denied.StatusCode.Should().Be(
                HttpStatusCode.Forbidden,
                "a caller that cannot read the enrichment dataset's backing layer must not be able to read it through an async enrichment job");

            (await denied.Content.ReadAsStringAsync()).Should().NotContain(
                "jobID", "a refused submission must not queue a job");
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers (mirrors VectorProcessParityIntegrationTests' durable-runtime wiring)
    // -------------------------------------------------------------------------

    private WebAppFixture BuildFixture(
        HonuaEdition edition,
        Dictionary<string, string?>? extraConfig = null,
        Action<IServiceCollection>? configureServices = null)
        => new WebAppFixture()
            .WithTestLicense(edition)
            .ConfigureWebHost(builder =>
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    var config = new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:redis"] = redis.ConnectionString,
                        ["DataEnrichment:Datasets:0:Key"] = DatasetKey,
                        ["DataEnrichment:Datasets:0:DisplayName"] = "Test Boundaries",
                        ["DataEnrichment:Datasets:0:Category"] = "boundary",
                        ["DataEnrichment:Datasets:0:LayerId"] = "1",
                        ["DataEnrichment:Datasets:0:Predicate"] = "intersects",
                    };
                    if (extraConfig is not null)
                    {
                        foreach (var (key, value) in extraConfig)
                        {
                            config[key] = value;
                        }
                    }

                    configBuilder.AddInMemoryCollection(config);
                });
            })
            .ConfigureServices(WireDurableRuntime)
            .ConfigureServices(services => configureServices?.Invoke(services));

    /// <summary>
    /// Replaces the default role store — whose seeded <c>admin</c> role carries a wildcard
    /// <c>*:*:*</c> grant that authorizes every layer read regardless of the layer's
    /// <see cref="AccessPolicy"/> — with one granting only <c>process:*:execute</c>. That
    /// leaves the caller with process-execution authority and NO layer-read grant, which is
    /// precisely the principal the finding describes; layer reads then fall through to the
    /// coarse access policy.
    /// </summary>
    private static void SeedProcessExecuteOnlyRole(IServiceCollection services)
    {
        services.RemoveAll<IRoleStore>();
        services.AddSingleton<IRoleStore>(new ProcessExecuteOnlyRoleStore());
    }

    /// <summary>
    /// Minimal role store granting every caller only <c>process:*:execute</c>. The grant
    /// service is the literal <c>process</c> operator resource, so it can never match a
    /// catalog <c>(service, layer, query)</c> tuple and never authorizes a layer read.
    /// </summary>
    private sealed class ProcessExecuteOnlyRoleStore : IRoleStore
    {
        private static readonly PermissionGrant ExecuteGrant = new()
        {
            Service = "process",
            Layer = "*",
            Operation = "execute"
        };

        public Task<EffectivePermissions> GetEffectivePermissionsAsync(
            string userId,
            IReadOnlyList<string> roles,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new EffectivePermissions
            {
                UserId = userId,
                Roles = roles,
                Permissions = [ExecuteGrant],
                ResolvedAt = DateTimeOffset.UtcNow
            });

        public Task<IReadOnlyList<RoleDefinition>> ListRolesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RoleDefinition>>([]);

        public Task<RoleDefinition?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
            => Task.FromResult<RoleDefinition?>(null);

        public Task<RoleDefinition> CreateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RoleDefinition?> UpdateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
            => Task.FromResult<RoleDefinition?>(null);

        public Task<bool> DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<PermissionGrant>> GetPermissionsAsync(
            Guid roleId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PermissionGrant>>([]);

        public Task<IReadOnlyList<PermissionGrant>> SetPermissionsAsync(
            Guid roleId,
            IReadOnlyList<PermissionGrant> permissions,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PermissionGrant>>([]);
    }

    private void WireDurableRuntime(IServiceCollection services)
    {
        services.RemoveAll<IConnectionMultiplexer>();
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis.ConnectionString));

        services.RemoveAll<IExecutionJobStore>();
        services.AddSingleton<IExecutionJobStore>(sp =>
            new RedisExecutionJobStore(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<ILogger<RedisExecutionJobStore>>()));

        services.RemoveAll<IGeoprocessingResultPackageStore>();
        services.AddSingleton<IGeoprocessingResultPackageStore>(sp =>
            new RedisGeoprocessingResultPackageStore(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<IOptionsMonitor<GeoprocessingExecutorOptions>>(),
                sp.GetRequiredService<ILogger<RedisGeoprocessingResultPackageStore>>()));

        services.RemoveAll<RedisJobQueue>();
        services.RemoveAll<IJobQueue>();
        services.RemoveAll<IQueueClaimReconciler>();
        services.AddSingleton<RedisJobQueue>(sp =>
            new RedisJobQueue(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<IExecutionJobStore>(),
                sp.GetRequiredService<ILogger<RedisJobQueue>>()));
        services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<RedisJobQueue>());
        services.AddSingleton<IQueueClaimReconciler>(sp => sp.GetRequiredService<RedisJobQueue>());

        services.RemoveAll<IExecutionLogStore>();
        services.AddSingleton<IExecutionLogStore>(sp =>
            new RedisExecutionLogStore(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<ILogger<RedisExecutionLogStore>>()));

        // Activate the worker host: the production dispatcher registered by
        // AddGeoprocessing routes enrichment.enrich to EnrichmentJobExecutor —
        // that executor is the system-under-test for this suite.
        services.AddJobWorker();
    }

    // OGC execution request carrying the enrichment vocabulary: dataset id, the
    // registered source layer, and the enrichment method.
    private static string ExecuteRequestBody(
        int? sourceLayerId = null,
        (string Name, string Value)[]? extraInputs = null)
    {
        var inputs = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["datasetId"] = DatasetKey,
            ["layerId"] = sourceLayerId ?? WebAppFixture.TestLayerId,
            ["method"] = "intersects",
        };

        foreach (var (name, value) in extraInputs ?? [])
        {
            inputs[name] = value;
        }

        return JsonSerializer.Serialize(new { response = "document", inputs });
    }

    /// <summary>
    /// The same enrichment request sourced from a staged inline FeatureCollection rather
    /// than a registered layer, so the layer-only windowing filters have nothing to act on.
    /// </summary>
    private static string InlineExecuteRequestBody(params (string Name, string Value)[] extraInputs)
    {
        const string StagedCollection = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[0.5,0.5]},"properties":{"name":"a"}},
              {"type":"Feature","geometry":{"type":"Point","coordinates":[1.5,1.5]},"properties":{"name":"b"}}
            ]}
            """;

        var inputs = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["datasetId"] = DatasetKey,
            ["input"] = DataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(StagedCollection)),
            ["method"] = "intersects",
        };

        foreach (var (name, value) in extraInputs)
        {
            inputs[name] = value;
        }

        return JsonSerializer.Serialize(new { response = "document", inputs });
    }

    /// <summary>
    /// Posts an execution request and hands the raw response back so authorization
    /// outcomes can be asserted without the 201-only expectation
    /// <see cref="SubmitJobAsync"/> bakes in.
    /// </summary>
    private static async Task<HttpResponseMessage> PostExecutionAsync(HttpClient client, string body)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/ogc/processes/processes/{ProcessId}/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        return await client.SendAsync(request);
    }

    private static async Task<string> SubmitJobAsync(HttpClient client, string body)
    {
        using var submit = await PostExecutionAsync(client, body);
        submit.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
        var jobId = doc.RootElement.GetProperty("jobID").GetString();
        jobId.Should().NotBeNullOrWhiteSpace();
        return jobId!;
    }

    private static async Task<JsonDocument> PollUntilTerminalAsync(HttpClient client, string jobId)
    {
        // Generous deadline: the durable worker claims from Redis on its own poll
        // cadence, so a loaded CI host can take well over the per-job compute time
        // to reach a terminal status.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/ogc/processes/jobs/{jobId}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.GetProperty("status").GetString();
            if (status is "successful" or "failed" or "dismissed")
            {
                return JsonDocument.Parse(body);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"Timed out waiting for job '{jobId}' to reach a terminal status.");
    }

    private static async Task<JsonDocument> GetResultsAsync(HttpClient client, string jobId)
    {
        using var response = await client.GetAsync($"/ogc/processes/jobs/{jobId}/results");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement DecodeOutputFeatureCollection(JsonDocument resultsDoc)
    {
        var output = resultsDoc.RootElement.GetProperty("outputFeatureLayer");
        output.GetProperty("kind").GetString().Should().Be("FeatureLayer");
        output.GetProperty("type").GetString().Should().Be("application/geo+json");

        var href = output.GetProperty("href").GetString();
        href.Should().NotBeNull();
        href!.Should().StartWith(DataUriPrefix);

        var bytes = Convert.FromBase64String(href[DataUriPrefix.Length..]);
        using var doc = JsonDocument.Parse(bytes);
        var collection = doc.RootElement.Clone();
        collection.GetProperty("type").GetString().Should().Be("FeatureCollection");
        collection.GetProperty("processId").GetString().Should().Be(ProcessId);
        return collection;
    }

    private static async Task DeleteControlPlaneKeysAsync(string redisConnectionString)
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
        var database = multiplexer.GetDatabase();
        var endpoints = multiplexer.GetEndPoints();
        if (endpoints.Length == 0)
        {
            return;
        }

        var server = multiplexer.GetServer(endpoints[0]);
        var keys = server.Keys(pattern: "controlplane:*").ToArray();
        if (keys.Length > 0)
        {
            await database.KeyDeleteAsync(keys);
        }
    }
}
