// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed partial class DatasetImportProductionHttpTests
{
    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /mcp")]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}")]
    public async Task NativeManagedBuffer_ExactIssuedKeyExecutesOriginalSourceAndReadsActualInlineArtifact()
    {
        if (!OperatingSystem.IsLinux()) return;
        var postgres = new PostgresFixture();
        await postgres.InitializeAsync();
        string? database = null;
        var root = Path.Join(Path.GetTempPath(), "honua-native-buffer-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connection = await postgres.CreateIsolatedDatabaseAsync(nameof(NativeManagedBuffer_ExactIssuedKeyExecutesOriginalSourceAndReadsActualInlineArtifact));
            database = new NpgsqlConnectionStringBuilder(connection).Database;
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var bridgeKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var settings = new Dictionary<string, string?>
            {
                ["HONUA_SKIP_MIGRATIONS"] = "false",
                ["HONUA_DEV_AUTH"] = "false",
                ["HONUA_DEV_AUTH_ALLOW_BYPASS"] = "false",
                ["HONUA_ADMIN_PASSWORD"] = password,
                ["ConnectionStrings:DefaultConnection"] = connection,
                ["ConnectionStrings:honua"] = connection,
                ["ConnectionStrings:redis"] = redis.ConnectionString,
                ["DataSource:Provider"] = "PostGIS",
                ["Database:Schema"] = "honua",
                ["Licensing:DevGrantEdition"] = "Pro",
                ["AgentWorkspace:DeploymentId"] = "bounded-buffer-" + Guid.NewGuid().ToString("N"),
                ["AgentWorkspace:ReleaseId"] = "native-buffer-test",
                ["Security:ConnectionEncryption:MasterKey"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                ["Security:ConnectionEncryption:Salt"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["Authentication:OperatorBearer:Enabled"] = "true",
                ["Authentication:OperatorBearer:SigningKey"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                ["AgentWorkspace:LocalHumanBridge:Enabled"] = "true",
                ["AgentWorkspace:LocalHumanBridge:Key"] = bridgeKey,
                ["AgentWorkspace:LocalHumanBridge:TenantId"] = "public",
                ["Observability:OpsHealthRollup:Enabled"] = "false",
                ["FileStorage:Provider"] = "Local",
                ["FileStorage:LocalStorage:BasePath"] = Path.Join(root, "general"),
                ["Geoprocessing:Executors:OutputRootDirectory"] = Path.Join(root, "gp-output"),
                ["AgentWorkspace:DatasetImport:LocalStorage:Enabled"] = "true",
                ["AgentWorkspace:DatasetImport:LocalStorage:RootDirectory"] = Path.Join(root, "imports"),
                ["AgentWorkspace:DatasetImport:LocalStorage:DomainId"] = Guid.NewGuid().ToString("N"),
                ["AgentWorkspace:DatasetImport:LocalStorage:GlobalBytes"] = "67108864",
                ["AgentWorkspace:DatasetImport:LocalStorage:TenantBytes"] = "67108864",
                ["AgentWorkspace:DatasetImport:LocalStorage:OwnerBytes"] = "67108864",
                ["AgentWorkspace:DatasetImport:LocalStorage:MaximumObjects"] = "32",
                ["AgentWorkspace:DatasetImport:LocalStorage:JournalCapacity"] = "32",
                ["AgentWorkspace:DatasetImport:LocalStorage:MaximumScratchBytes"] = "16777216",
                ["AgentWorkspace:DatasetImport:LocalStorage:MaximumFileBytes"] = "16777216"
            };
            await using var factory = ConfiguredWebApplicationFactory.Create(builder =>
            {
                foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
            }, "Development");
            factory.UseKestrel(0);
            using var setup = factory.CreateClient();
            setup.DefaultRequestHeaders.Add("X-API-Key", password);

            // Trusted input preparation only: actual canonical source rows and a
            // persisted real provider graph. No requested analysis job is created.
            const int storageLayerId = 73313;
            const string serviceName = "native-buffer";
            var serviceId = Guid.NewGuid().ToString("D");
            var resourceId = Guid.NewGuid().ToString("D");
            var publicationId = Guid.NewGuid().ToString("D");
            var bindingId = Guid.NewGuid().ToString("D");
            await using (var source = new NpgsqlConnection(connection))
            {
                await source.OpenAsync();
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO public.features(layer_id, geometry, attributes) VALUES
                    (73313, ST_SetSRID(ST_Point(1000,2000),3857), '{"source_id":"shelter-first","name":"original"}'),
                    (73313, ST_SetSRID(ST_Point(3000,4000),3857), '{"source_id":"shelter-second","name":"unchanged"}');
                    """, source);
                await seed.ExecuteNonQueryAsync();
            }
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
                store.GetType().Assembly.GetName().Name.Should().Be("Honua.Postgres");
                var current = await store.GetCurrentAsync();
                var graph = new TestMetadataV2GraphBuilder().WithEnvironment(current.Graph.Environment)
                    .AddService(serviceId, serviceName, route: "/rest/services/" + serviceName, protocols: MetadataV2ServiceProtocols.All)
                    .AddResource(resourceId, "shelters", fields:
                    [
                        new() { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                        new() { Name = "source_id", Type = MetadataV2FieldType.String, Length = 120 },
                        new() { Name = "name", Type = MetadataV2FieldType.String, Length = 120 },
                        new() { Name = "shape", Type = MetadataV2FieldType.Geometry }
                    ], spatial: new()
                    {
                        GeometryType = MetadataV2GeometryType.Point,
                        PrimaryGeometryField = "shape",
                        SpatialReference = new() { Srid = 3857, Crs = "EPSG:3857", IsGeographic = false }
                    })
                    .AddStorageBinding(bindingId, resourceId, "features", storageLayerId: storageLayerId,
                        options: new Dictionary<string, JsonElement>
                        {
                            ["geometryColumn"] = JsonSerializer.SerializeToElement("geometry"),
                            ["attributesColumn"] = JsonSerializer.SerializeToElement("attributes"),
                            ["layerDiscriminatorColumn"] = JsonSerializer.SerializeToElement("layer_id")
                        })
                    .AddPublication(publicationId, serviceId, resourceId, layerIndex: 0, storageBindingId: bindingId,
                        publicationType: MetadataV2PublicationType.EsriFeatureLayer, isPrimary: true).Build();
                graph = graph with
                {
                    Resources = graph.Resources.Select(item => item with { Metadata = item.Metadata with { Tenant = "public" } }).ToArray(),
                    Services = graph.Services.Select(item => item with { Metadata = item.Metadata with { Tenant = "public" } }).ToArray(),
                    Publications = graph.Publications.Select(item => item with { Metadata = item.Metadata with { Tenant = "public" } }).ToArray()
                };
                await store.SaveAsync(graph, current.Etag);
            }
            var issued = await Read(await setup.PostAsJsonAsync("/api/v1/admin/api-keys", new
            {
                name = "bounded-native-buffer",
                permissions = new[] { "admin:read", "agent:workspace",
                    "agent:managed-process:analytics.buffer-aggregate-managed", "read:native-buffer/shelters" }
            }), HttpStatusCode.Created);
            var key = issued.GetProperty("data").GetProperty("key").GetString()!;
            var keyId = issued.GetProperty("data").GetProperty("apiKey").GetProperty("id").GetString()!;
            issued.GetProperty("data").GetProperty("apiKey").GetProperty("permissions").EnumerateArray().Select(item => item.GetString())
                .Should().NotContain("admin:write").And.NotContain("*");
            using var agent = factory.CreateClient();
            agent.DefaultRequestHeaders.Add("X-API-Key", key);
            var identity = await Read(await agent.GetAsync("/api/v1/agent/target"), HttpStatusCode.OK);
            var actor = identity.GetProperty("principal").GetProperty("actorId").GetString()!;
            var targetId = identity.GetProperty("targetId").GetString()!;
            factory.Services.GetRequiredService<IConfiguration>()["AgentWorkspace:LocalAgentPrincipalId"] = actor;
            using var human = factory.CreateClient();
            human.DefaultRequestHeaders.Add("X-Honua-Local-Bridge", bridgeKey);
            var session = await Read(await human.PostAsJsonAsync("/api/v1/admin/auth/local-console-session", new { subject = "buffer-owner" }), HttpStatusCode.OK);
            human.DefaultRequestHeaders.Remove("X-Honua-Local-Bridge");
            human.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
            var workspace = await Read(await human.PostAsJsonAsync("/api/v1/agent/workspaces", new { viewId = "buffer-view", targetId }), HttpStatusCode.OK);
            var workspaceId = workspace.GetProperty("workspaceId").GetString()!;
            var context = await Read(await human.PutAsJsonAsync("/api/v1/agent/workspaces/" + workspaceId + "/context", new
            {
                viewId = "buffer-view",
                expectedRevision = workspace.GetProperty("revision").GetInt64(),
                targetId,
                selectedResources = new[] { new { kind = "layer", id = "0", serviceId } },
                task = (object?)null
            }), HttpStatusCode.OK);
            await Read(await human.PostAsJsonAsync("/api/v1/agent/workspaces/" + workspaceId + "/turns", new
            {
                turnId = "buffer-turn",
                viewId = "buffer-view",
                contextRevision = context.GetProperty("revision").GetInt64(),
                prompt = "Buffer the first selected shelter by 500 meters in EPSG:3857, preserving source_id and all original rows."
            }), HttpStatusCode.OK);
            agent.DefaultRequestHeaders.Add("X-Honua-Workspace", workspaceId);
            agent.DefaultRequestHeaders.Add("X-Honua-Turn", "buffer-turn");
            var sourceRead = await Read(await agent.GetAsync("/rest/services/native-buffer/FeatureServer/0/query?where=1%3D1&outFields=*&returnGeometry=true&outSR=3857&f=json&orderByFields=objectid&resultRecordCount=1"), HttpStatusCode.OK);
            testOutput.WriteLine("Actual managed-buffer original source HTTP query: " + sourceRead.GetRawText());
            sourceRead.TryGetProperty("error", out _).Should().BeFalse(sourceRead.GetRawText());
            sourceRead.GetProperty("features").GetArrayLength().Should().Be(1);
            var sourceFeature = sourceRead.GetProperty("features")[0];
            sourceFeature.GetProperty("attributes").GetProperty("source_id").GetString().Should().Be("shelter-first");
            sourceFeature.GetProperty("geometry").GetProperty("x").GetDouble().Should().Be(1000);
            sourceFeature.GetProperty("geometry").GetProperty("y").GetDouble().Should().Be(2000);
            using var initialize = await agent.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0",
                id = "initialize",
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-03-26",
                    capabilities = new { },
                    clientInfo = new { name = "native-buffer", version = "1" },
                    _meta = new Dictionary<string, string> { ["honua.io/workflow-view"] = "analyze" }
                }
            });
            initialize.EnsureSuccessStatusCode();
            agent.DefaultRequestHeaders.Add("Mcp-Session-Id", initialize.Headers.GetValues("Mcp-Session-Id").Single());
            var discovery = await Read(await agent.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0",
                id = "discover",
                method = "tools/list",
                @params = new { view = "analyze" }
            }), HttpStatusCode.OK);
            var executeDescriptor = discovery.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Single(tool => tool.GetProperty("name").GetString() == "honua_execute_plan");
            executeDescriptor.GetProperty("inputSchema").GetProperty("type").GetString().Should().Be("object");
            executeDescriptor.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("plan", out _).Should().BeTrue();
            var plan = AgentManagedProcessContractTests.Plan();
            var wirePlan = new
            {
                planId = plan.PlanId,
                intentId = plan.IntentId,
                steps = plan.Steps.Select(step => new { stepId = step.StepId, kind = "Geoprocess", processId = step.ProcessId, inputs = step.Inputs }).ToArray(),
                outputs = new[] { "FeatureLayer" }
            };
            var accepted = Success(await Rpc(agent, "honua_execute_plan", new { plan = wirePlan, idempotencyKey = "original-buffer" }));
            testOutput.WriteLine("Actual managed-buffer accepted MCP result: " + accepted.GetRawText());
            var acceptedContract = accepted.GetProperty("executionContract");
            acceptedContract.GetProperty("taskAttachmentAllowed").GetBoolean().Should().BeFalse();
            acceptedContract.GetProperty("workspaceId").GetString().Should().Be(workspaceId);
            acceptedContract.GetProperty("turnId").GetString().Should().Be("buffer-turn");
            acceptedContract.GetProperty("maximumInputBytes").GetInt64().Should().Be(32768);
            acceptedContract.GetProperty("maximumOutputBytes").GetInt64().Should().Be(1048576);
            acceptedContract.GetProperty("outputMode").GetString().Should().Be("inline-only");
            acceptedContract.GetProperty("sourceResourceIds").EnumerateArray().Should().ContainSingle().Which.GetString().Should().Be(resourceId);
            var jobId = accepted.GetProperty("jobId").GetString()!;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            JsonElement detail;
            do
            {
                if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("The actual managed job did not terminate.");
                await Task.Delay(100);
                detail = await Read(await agent.GetAsync("/api/v1/admin/jobs/" + jobId), HttpStatusCode.OK);
            } while (detail.GetProperty("status").GetString() is "Queued" or "Provisioning" or "Running");
            testOutput.WriteLine("Actual managed-buffer job HTTP detail: " + detail.GetRawText());
            detail.GetProperty("status").GetString().Should().Be("Succeeded", detail.GetRawText());
            detail.GetProperty("requestedBy").GetString().Should().Be(actor);
            detail.GetProperty("requestFingerprint").GetString().Should().Be(GeoprocessingJobService.CreateRequestFingerprint(plan));
            detail.GetProperty("statusTransitionsTruncated").GetBoolean().Should().BeFalse();
            detail.GetProperty("statusTransitions").EnumerateArray().Select(item => item.GetProperty("toStatus").GetString())
                .Should().Equal("Queued", "Provisioning", "Running", "Succeeded");
            var resources = await Read(await agent.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0",
                id = "results",
                method = "resources/read",
                @params = new { uri = "honua://jobs/" + jobId + "/results" }
            }), HttpStatusCode.OK);
            resources.TryGetProperty("error", out _).Should().BeFalse(resources.GetRawText());
            using var result = JsonDocument.Parse(resources.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!);
            var output = result.RootElement.GetProperty("artifacts")[0].GetProperty("uri").GetString()!;
            testOutput.WriteLine("Actual managed-buffer native result: " + result.RootElement.GetRawText());
            FeatureCollectionArtifact.TryParseDataUri(output, out var buffered, out _, AgentManagedBufferContract.MaximumOutputBytes).Should().BeTrue();
            buffered.Should().ContainSingle();
            buffered[0].Geometry.SRID.Should().Be(3857);
            buffered[0].Geometry.EnvelopeInternal.MinX.Should().BeApproximately(500, .000001);
            buffered[0].Geometry.EnvelopeInternal.MaxX.Should().BeApproximately(1500, .000001);
            buffered[0].Attributes.GetOptionalValue("source_id").Should().Be("shelter-first");
            if (Directory.Exists(Path.Join(root, "gp-output"))) Directory.EnumerateFiles(Path.Join(root, "gp-output"), "*", SearchOption.AllDirectories).Should().BeEmpty();
            var retained = await factory.Services.GetRequiredService<IExecutionJobStore>().GetAsync(jobId);
            retained!.Spec.AgentManagedProcessBinding.Should().NotBeNull();
            retained.ArtifactReferences.Should().ContainSingle().Which.Should().Be(output);
            (await Rpc(agent, "honua_execute_plan", new { plan = wirePlan, idempotencyKey = "original-buffer" })).GetProperty("structuredContent").GetProperty("jobId").GetString().Should().Be(jobId);
            var wrongProcess = await Rpc(agent, "honua_execute_plan", new
            {
                plan = new
                {
                    planId = "foreign-process",
                    intentId = plan.IntentId,
                    steps = new[] { new { stepId = "foreign", kind = "Geoprocess", processId = "geometry.buffer", inputs = plan.Steps[0].Inputs } },
                    outputs = new[] { "FeatureLayer" }
                },
                idempotencyKey = "not-authorized"
            });
            wrongProcess.GetProperty("isError").GetBoolean().Should().BeTrue();
            wrongProcess.GetProperty("structuredContent").GetProperty("code").GetString().Should().Be("permission_denied");
            (await factory.Services.GetRequiredService<IExecutionJobStore>().QueryAsync(new()
            { Kind = ExecutionJobKind.Geoprocessing, RequestedBy = actor, Limit = 10 })).Items.Should().ContainSingle();

            // A real persisted source metadata change cannot retain authority
            // through a previously accepted job/result or an idempotent replay.
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>();
                var current = await store.GetCurrentAsync();
                await store.SaveAsync(current.Graph with
                {
                    Resources = current.Graph.Resources.Select(item => item with
                    {
                        Metadata = item.Metadata with
                        { Annotations = new Dictionary<string, string>(item.Metadata.Annotations) { ["native-source-policy-revision"] = "changed" } }
                    }).ToArray()
                }, current.Etag);
            }
            (await agent.GetAsync("/api/v1/admin/jobs/" + jobId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            var changedSource = await Read(await agent.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0",
                id = "changed-source",
                method = "resources/read",
                @params = new { uri = "honua://jobs/" + jobId + "/results" }
            }), HttpStatusCode.OK);
            changedSource.GetProperty("error").GetProperty("data").GetProperty("code").GetString().Should().Be("permission_denied");
            (await Rpc(agent, "honua_execute_plan", new { plan = wirePlan, idempotencyKey = "original-buffer" }))
                .GetProperty("isError").GetBoolean().Should().BeTrue();
            await Read(await agent.GetAsync("/api/v1/agent/target"), HttpStatusCode.OK);
            await Read(await setup.PostAsync("/api/v1/admin/api-keys/" + keyId + "/revoke", null), HttpStatusCode.OK);
            (await agent.GetAsync("/api/v1/agent/target")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await agent.GetAsync("/api/v1/admin/jobs/" + jobId)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            await using var verify = new NpgsqlConnection(connection);
            await verify.OpenAsync();
            await using var unchanged = new NpgsqlCommand("SELECT jsonb_agg(jsonb_build_array(attributes,ST_AsText(geometry),ST_SRID(geometry)) ORDER BY objectid)::text FROM public.features WHERE layer_id=73313", verify);
            var original = (string)(await unchanged.ExecuteScalarAsync())!;
            original.Should().Contain("shelter-first").And.Contain("shelter-second").And.Contain("POINT(1000 2000)").And.Contain("POINT(3000 4000)");
        }
        finally
        {
            if (database is not null) await postgres.DropDatabaseAsync(database);
            await postgres.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
