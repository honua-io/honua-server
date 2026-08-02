// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.ControlPlane;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Tests.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// End-to-end proof that row-level security and field masking are enforced on a
/// GEOPROCESSING JOB's layer reads (honua-server#3068), not only on the synchronous protocol
/// surfaces. The job runs on the background worker, where there is no ambient
/// <c>HttpContext</c> — before this fix both the RLS predicate and the field mask resolved to
/// "nothing applies" there, so a restricted caller received every row and every attribute
/// through the job artifact.
/// </summary>
/// <remarks>
/// The plan submitted is <c>source.honua-layer</c>, the DAG connector every layer-sourced
/// process reads through, so proving enforcement here proves it for the whole family
/// (<c>analytics.*</c>, <c>generalization.*</c>, <c>conversion.feature-project</c>,
/// enrichment). The seeded test layer has five features — three with <c>category='test'</c>
/// and two with <c>category='sample'</c> — plus a <c>description</c> attribute, which is
/// exactly the shape the synchronous RLS/field-mask tests assert against, so the job artifact
/// can be compared to the known synchronous outcome.
/// </remarks>
[Collection("Redis")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class GeoprocessingJobRowAndFieldSecurityTests(RedisFixture redis)
{
    private const string AdminPassword = "gp-rls-admin-key";
    private const string MaskedAttribute = "description";
    private const string RestrictedRole = "restricted-analyst";
    private const int CategoryTestCount = 3;
    private const int CategorySampleCount = 2;
    private const string ExecutionPath = "/ogc/processes/processes/honua-geoprocessing/execution";

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task LayerSourcedJob_ForRlsAndFieldMaskRestrictedSubmitter_ArtifactIsRowFilteredAndMasked()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture();
        await fixture.InitializeAsync();
        Guid? rlsPolicyId = null;
        Guid? maskPolicyId = null;

        try
        {
            using var adminClient = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

            // Baseline: with no policies the job artifact carries every row and every
            // attribute, so the assertions below cannot pass vacuously (e.g. because the
            // connector silently returned nothing).
            var baseline = await RunLayerSourceJobAsync(fixture, category: "test", extraRole: null);
            baseline.Count.Should().Be(CategoryTestCount + CategorySampleCount);
            baseline.Should().OnlyContain(feature => feature.ContainsKey(MaskedAttribute));

            rlsPolicyId = await CreateRlsPolicyAsync(adminClient);
            maskPolicyId = await CreateFieldMaskPolicyAsync(adminClient);

            // The restricted submitter's job artifact must now match what the SAME principal
            // would receive synchronously: only the rows their 'category' claim permits, and
            // without the masked attribute.
            var restricted = await RunLayerSourceJobAsync(fixture, category: "test", extraRole: RestrictedRole);

            restricted.Count.Should().Be(
                CategoryTestCount,
                "the background read must apply the submitter's RLS predicate, not read the whole layer");
            restricted.Should().OnlyContain(
                feature => (string?)feature.GetValueOrDefault("category") == "test");
            restricted.Should().OnlyContain(
                feature => !feature.ContainsKey(MaskedAttribute),
                "the submitter's field mask must drop the masked attribute from the published artifact");

            // Control: the same plan, same policies, a submitter whose claim selects the other
            // partition and who does not hold the masked role. A different artifact from the
            // same plan proves the filtering is derived from the SUBMITTER, not hard-coded.
            var unmasked = await RunLayerSourceJobAsync(fixture, category: "sample", extraRole: null);

            unmasked.Count.Should().Be(CategorySampleCount);
            unmasked.Should().OnlyContain(
                feature => (string?)feature.GetValueOrDefault("category") == "sample");
            unmasked.Should().OnlyContain(
                feature => feature.ContainsKey(MaskedAttribute),
                "a submitter outside the masked role must still receive the attribute");
        }
        finally
        {
            await DeletePolicyAsync(fixture, "rls-policies", rlsPolicyId);
            await DeletePolicyAsync(fixture, "field-mask-policies", maskPolicyId);
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task LayerSourcedJob_PinsSubmitterSecurityContextOnDurableRecord()
    {
        // The carrier itself: the durable job record must hold the submitter's claim snapshot,
        // because that is what survives a worker restart and lets a DIFFERENT node resolve the
        // same RLS predicate and field mask. Asserted separately from the artifact test so a
        // regression that drops the capture is attributed precisely.
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture();
        await fixture.InitializeAsync();
        try
        {
            var jobId = await SubmitLayerSourceJobAsync(fixture, category: "test", extraRole: RestrictedRole);

            var jobStore = fixture.GetService<IExecutionJobStore>();
            var record = await jobStore.GetAsync(jobId);

            record.Should().NotBeNull();
            record!.Audit.SubmitterSecurityContext.Should().NotBeNull(
                "a job that reads catalog layers must pin the submitter's row/field security identity");
            record.Audit.SubmitterSecurityContext!.Claims.Should().Contain(
                claim => claim.Value == RestrictedRole,
                "field masking keys on the submitter's roles, so the role snapshot must be pinned");
            record.Audit.SubmitterSecurityContext.Claims.Should().Contain(
                claim => claim.Type == "category" && claim.Value == "test",
                "RLS policies key on arbitrary claim types, so the predicate's claim must be pinned too");
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    private WebAppFixture BuildFixture()
        => new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:redis"] = redis.ConnectionString
                    });
                });
            })
            .ConfigureServices(services =>
            {
                // Header-driven claims so a submission can carry both an RLS claim
                // ('category') and a role, exactly as the synchronous RLS/field-mask
                // integration tests do.
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, RlsClaimsTestAuthHandler>(
                        RlsClaimsTestAuthHandler.SchemeName, _ => { });
                services.PostConfigureAll<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = RlsClaimsTestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = RlsClaimsTestAuthHandler.SchemeName;
                    options.DefaultScheme = RlsClaimsTestAuthHandler.SchemeName;
                });

                WireDurableRuntime(services);
            });

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

        // Activate the real worker host: the production source.honua-layer executor and the
        // production Postgres read path are the system under test.
        services.AddJobWorker();
    }

    private static HttpClient CreateSubmitterClient(WebAppFixture fixture, string category, string? extraRole)
    {
        // 'admin' clears the baseline Process.Execute gate so the 403-free path isolates the
        // row/field behaviour under test; the extra role (when present) is what the field-mask
        // policy targets.
        var roles = extraRole is null ? "admin" : $"admin,{extraRole}";
        var client = fixture.CreateClient(c =>
        {
            c.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.UserHeader, "gp-rls-user");
            c.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.RolesHeader, roles);
            c.DefaultRequestHeaders.Add(RlsClaimsTestAuthHandler.CategoryHeader, category);
        });
        client.Timeout = TimeSpan.FromSeconds(60);
        return client;
    }

    private static async Task<string> SubmitLayerSourceJobAsync(
        WebAppFixture fixture,
        string category,
        string? extraRole)
    {
        using var client = CreateSubmitterClient(fixture, category, extraRole);

        using var request = new HttpRequestMessage(HttpMethod.Post, ExecutionPath);
        request.Headers.Add("Prefer", "respond-async");
        // Concatenated rather than interpolated: the payload's trailing brace run collides with
        // raw-string interpolation delimiters at every '$' depth.
        var body = "{\"inputs\":{\"plan\":{\"planId\":\"plan-layer-source-" + category
            + "\",\"steps\":[{\"stepId\":\"s1\",\"kind\":\"geoprocess\","
            + "\"processId\":\"source.honua-layer\",\"inputs\":{\"layerId\":\""
            + WebAppFixture.TestLayerId.ToString(CultureInfo.InvariantCulture) + "\"}}]}}}";
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, payload);

        using var document = JsonDocument.Parse(payload);
        var jobId = document.RootElement.GetProperty("jobID").GetString();
        jobId.Should().NotBeNullOrWhiteSpace();
        return jobId!;
    }

    /// <summary>
    /// Submits the layer-source plan as the described submitter, waits for the job, and decodes
    /// the published FeatureCollection artifact into one attribute dictionary per feature.
    /// </summary>
    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RunLayerSourceJobAsync(
        WebAppFixture fixture,
        string category,
        string? extraRole)
    {
        var jobId = await SubmitLayerSourceJobAsync(fixture, category, extraRole);

        using var client = CreateSubmitterClient(fixture, category, extraRole);
        using var terminal = await PollUntilSucceededAsync(client, jobId);
        terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");

        using var results = await client.GetAsync($"/ogc/processes/jobs/{jobId}/results");
        var payload = await results.Content.ReadAsStringAsync();
        results.StatusCode.Should().Be(HttpStatusCode.OK, payload);

        using var document = JsonDocument.Parse(payload);
        var href = document.RootElement
            .EnumerateObject()
            .Select(property => property.Value)
            .Where(value => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("href", out _))
            .Select(value => value.GetProperty("href").GetString())
            .FirstOrDefault(value => value is not null && value.StartsWith("data:", StringComparison.Ordinal));

        href.Should().NotBeNull("source.honua-layer publishes its FeatureCollection as a data URI artifact");
        return DecodeFeatureAttributes(href!);
    }

    private static List<IReadOnlyDictionary<string, object?>> DecodeFeatureAttributes(string dataUri)
    {
        var separator = dataUri.IndexOf("base64,", StringComparison.Ordinal);
        separator.Should().BeGreaterThan(-1, "the artifact data URI must be base64-encoded");
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(dataUri[(separator + "base64,".Length)..]));

        using var document = JsonDocument.Parse(json);
        var features = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var feature in document.RootElement.GetProperty("features").EnumerateArray())
        {
            var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (feature.TryGetProperty("properties", out var properties) &&
                properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                {
                    attributes[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString(),
                        JsonValueKind.Null => null,
                        _ => property.Value.ToString()
                    };
                }
            }

            features.Add(attributes);
        }

        return features;
    }

    private static async Task<JsonDocument> PollUntilSucceededAsync(HttpClient client, string jobId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/ogc/processes/jobs/{jobId}");
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);

            using var document = JsonDocument.Parse(body);
            var status = document.RootElement.GetProperty("status").GetString();
            if (status == "successful")
            {
                return JsonDocument.Parse(body);
            }

            status.Should().NotBe("failed", body);
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"Timed out waiting for geoprocessing job '{jobId}' to succeed.");
    }

    private static async Task<Guid> CreateRlsPolicyAsync(HttpClient adminClient)
    {
        var request = new CreateRlsPolicyRequest
        {
            Role = "*",
            Service = "*",
            Layer = "*",
            Attribute = "category",
            ClaimType = "category",
            Comparison = "in",
            Description = "GP RLS test: restrict rows by the submitter's category claim",
        };

        using var content = JsonContent.Create(request, RlsPolicyJsonContext.Default.CreateRlsPolicyRequest);
        using var response = await adminClient.PostAsync("/api/v1/admin/rls-policies", content);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, payload);

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("data").GetProperty("policyId").GetGuid();
    }

    private static async Task<Guid> CreateFieldMaskPolicyAsync(HttpClient adminClient)
    {
        var request = new CreateFieldMaskPolicyRequest
        {
            Role = RestrictedRole,
            Service = "*",
            Layer = "*",
            Attribute = MaskedAttribute,
            Description = "GP field-mask test: hide description from the restricted role",
        };

        using var content = JsonContent.Create(request, FieldMaskPolicyJsonContext.Default.CreateFieldMaskPolicyRequest);
        using var response = await adminClient.PostAsync("/api/v1/admin/field-mask-policies", content);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, payload);

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("data").GetProperty("policyId").GetGuid();
    }

    private static async Task DeletePolicyAsync(WebAppFixture fixture, string resource, Guid? policyId)
    {
        if (policyId is not { } id)
        {
            return;
        }

        using var adminClient = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
        _ = await adminClient.DeleteAsync($"/api/v1/admin/{resource}/{id}");
    }

    private static async Task DeleteControlPlaneKeysAsync(string redisConnectionString)
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
        var database = multiplexer.GetDatabase();
        foreach (var server in multiplexer.GetEndPoints().Select(endpoint => multiplexer.GetServer(endpoint)))
        {
            if (!server.IsConnected || server.IsReplica)
            {
                continue;
            }

            var keys = server.Keys(pattern: "controlplane:*").ToArray();
            if (keys.Length > 0)
            {
                await database.KeyDeleteAsync(keys);
            }
        }
    }
}
