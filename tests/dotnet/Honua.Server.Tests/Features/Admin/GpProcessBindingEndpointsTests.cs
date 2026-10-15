// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing.ProcessRelease;
using Honua.Geoprocessing.Release;
using Honua.Server.Features.Geoprocessing.ProcessRelease;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// The GP process release and binding admin API (honua-server#6221) driven the way
/// honua-release's deploy workflow drives it: publish v1 and v2, activate v1 under expected
/// version 0, promote v2 under version 1, refuse the stale and the wrong-target attempts with
/// named reasons, roll back to v1, and read every projection back. The store is the in-memory
/// one so the sequence is deterministic; the Postgres store runs the same policy under a row
/// lock and is exercised by its own migration-backed path.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class GpProcessBindingEndpointsTests : IAsyncLifetime
{
    private const string Base = "/api/v1/admin/gp/process-releases";
    private const string JobDefinitionArn = "arn:aws:batch:us-west-2:123456789012:job-definition/buffer-features:7";
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public GpProcessBindingEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IGpProcessReleaseStore>();
                services.AddSingleton<IGpProcessReleaseStore>(new InMemoryGpProcessReleaseStore());
                services.RemoveAll<GpProcessReleaseEnvironmentResolver>();
                services.AddSingleton(new GpProcessReleaseEnvironmentResolver(new AwsBatchOnlyRegistry()));
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/gp/process-releases")]
    [Endpoint("GET /api/v1/admin/gp/process-releases")]
    [Endpoint("GET /api/v1/admin/gp/process-releases/{digest}")]
    [Endpoint("GET /api/v1/admin/gp/process-releases/bindings")]
    [Endpoint("GET /api/v1/admin/gp/process-releases/bindings/{process}")]
    [Endpoint("POST /api/v1/admin/gp/process-releases/bindings/{process}/activate")]
    public async Task DeployPromoteAndRollBack_WalksTheBindingVersionsAndRefusesStaleAttempts()
    {
        // Publish v1 twice: the second publish is a no-op with the same digest.
        var publishV1 = await _client.PostAsync(Base, Json(Lock("1.0.0", 'a')));
        publishV1.StatusCode.Should().Be(HttpStatusCode.Created);
        var v1 = await publishV1.Content.ReadFromJsonAsync<JsonElement>();
        var v1Digest = v1.GetProperty("lockDigest").GetString()!;
        v1Digest.Should().StartWith("sha256:");
        var republish = await _client.PostAsync(Base, Json(Lock("1.0.0", 'a')));
        republish.StatusCode.Should().Be(HttpStatusCode.OK);
        (await republish.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("created").GetBoolean().Should().BeFalse();

        var publishV2 = await _client.PostAsync(Base, Json(Lock("2.0.0", 'b')));
        publishV2.StatusCode.Should().Be(HttpStatusCode.Created);
        var v2Digest = (await publishV2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lockDigest").GetString()!;

        // The environment projection before any activation: version 0, server facts present.
        var unbound = await _client.GetFromJsonAsync<JsonElement>($"{Base}/bindings/buffer-features");
        unbound.GetProperty("bindingVersion").GetInt32().Should().Be(0);
        unbound.GetProperty("lockDigest").ValueKind.Should().Be(JsonValueKind.Null);
        unbound.GetProperty("serverContractVersion").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        unbound.GetProperty("supportedTargets").EnumerateArray().Select(e => e.GetString()).Should().Contain("aws-batch");

        // Deploy v1.
        var deploy = await _client.PostAsJsonAsync($"{Base}/bindings/buffer-features/activate", Activate(v1Digest, expected: 0));
        deploy.StatusCode.Should().Be(HttpStatusCode.OK, await deploy.Content.ReadAsStringAsync());
        var bound = await deploy.Content.ReadFromJsonAsync<JsonElement>();
        bound.GetProperty("bindingVersion").GetInt32().Should().Be(1);
        bound.GetProperty("lockDigest").GetString().Should().Be(v1Digest);
        bound.GetProperty("targets")[0].GetProperty("jobDefinitionArn").GetString().Should().Be(JobDefinitionArn);
        bound.GetProperty("targets")[0].GetProperty("image").GetString().Should().EndWith("@sha256:" + new string('b', 64));

        // Promote v2 under the version the deploy workflow read.
        var promote = await _client.PostAsJsonAsync($"{Base}/bindings/buffer-features/activate", Activate(v2Digest, expected: 1));
        promote.StatusCode.Should().Be(HttpStatusCode.OK, await promote.Content.ReadAsStringAsync());
        var promoted = await promote.Content.ReadFromJsonAsync<JsonElement>();
        promoted.GetProperty("bindingVersion").GetInt32().Should().Be(2);
        promoted.GetProperty("previousLockDigest").GetString().Should().Be(v1Digest);

        // A concurrent deployment that still believes the version is 1 is refused, not applied.
        var stale = await _client.PostAsJsonAsync($"{Base}/bindings/buffer-features/activate", Activate(v2Digest, expected: 1));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var refusal = await stale.Content.ReadFromJsonAsync<JsonElement>();
        refusal.GetProperty("reasons").EnumerateArray().Select(e => e.GetString()).Should().Contain("STALE_BINDING_VERSION");
        refusal.GetProperty("binding").GetProperty("bindingVersion").GetInt32().Should().Be(2);

        // A rollback must name the previous release.
        var wrongTarget = await _client.PostAsJsonAsync($"{Base}/bindings/buffer-features/activate", Activate(v2Digest, expected: 2, operation: "rollback"));
        wrongTarget.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await wrongTarget.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reasons").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("ROLLBACK_TARGET_MISMATCH");

        var rollback = await _client.PostAsJsonAsync($"{Base}/bindings/buffer-features/activate", Activate(v1Digest, expected: 2, operation: "rollback"));
        rollback.StatusCode.Should().Be(HttpStatusCode.OK, await rollback.Content.ReadAsStringAsync());
        var rolledBack = await rollback.Content.ReadFromJsonAsync<JsonElement>();
        rolledBack.GetProperty("bindingVersion").GetInt32().Should().Be(3);
        rolledBack.GetProperty("lockDigest").GetString().Should().Be(v1Digest);
        rolledBack.GetProperty("operation").GetString().Should().Be("rollback");
        rolledBack.GetProperty("scheduleReconciliation").GetString().Should().Contain("pending-orchestration-integration");

        // Projections.
        var list = await _client.GetFromJsonAsync<JsonElement>($"{Base}?process=buffer-features");
        list.GetProperty("releases").GetArrayLength().Should().Be(2);
        var bindings = await _client.GetFromJsonAsync<JsonElement>($"{Base}/bindings");
        bindings.GetProperty("bindings").EnumerateArray().Should().ContainSingle(b => b.GetProperty("process").GetString() == "buffer-features");
        var lockBody = await _client.GetStringAsync($"{Base}/{v1Digest}");
        lockBody.Should().Contain("\"schema\": \"honua.gp-process-release-lock/v1\"").And.Contain("\"version\": \"1.0.0\"");
        (await _client.GetAsync($"{Base}/sha256:" + new string('0', 64))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/gp/process-releases")]
    public async Task Publish_RejectsAnInvalidLockWithTheFindings()
    {
        var response = await _client.PostAsync(Base, new StringContent("{\"schema\":\"honua.gp-process-release-lock/v1\"}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("process is required");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/gp/process-releases/bindings/{process}/activate")]
    public async Task Activate_RefusesAnUnpublishedLockAndAMissingJobDefinition()
    {
        var unknown = await _client.PostAsJsonAsync($"{Base}/bindings/buffer-features/activate", Activate("sha256:" + new string('0', 64), expected: 0));
        unknown.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reasons").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("LOCK_NOT_FOUND");

        var publish = await _client.PostAsync(Base, Json(Lock("3.0.0", 'c')));
        var digest = (await publish.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lockDigest").GetString()!;
        var noIdentity = await _client.PostAsJsonAsync($"{Base}/bindings/buffer-features/activate",
            new { lockDigest = digest, expectedBindingVersion = 0, operation = "deploy", targets = Array.Empty<object>() });
        noIdentity.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await noIdentity.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reasons").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("TARGET_IDENTITY_MISSING");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/gp/process-releases/bindings")]
    public async Task Bindings_RequireAdminCredentials()
    {
        using var anonymous = _fixture.CreateClient();

        var response = await anonymous.GetAsync($"{Base}/bindings");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    public async Task PostgresActivation_CustomSchema_ConcurrentPublishesAndDeploysPersistOnce()
    {
        var process = "binding-test-" + Guid.NewGuid().ToString("N");
        var schema = _fixture.CurrentSchema!;
        using var builder = new NpgsqlCommandBuilder();
        var quotedSchema = builder.QuoteIdentifier(schema);
        await using (var connection = await _fixture.Postgres.GetConnectionAsync())
        {
            // Isolate the release metadata as a configured non-default schema would.
            foreach (var table in new[] { "gp_process_release", "gp_process_binding", "gp_process_binding_history" })
            {
                await using var command = new NpgsqlCommand(
                    $"CREATE TABLE IF NOT EXISTS {quotedSchema}.{table} (LIKE honua.{table} INCLUDING ALL)", connection);
                await command.ExecuteNonQueryAsync();
            }
        }

        var store = new PostgresGpProcessReleaseStore(
            _fixture.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, schema);
        var document = GpProcessReleaseLock.Parse(Lock("1.0.0", 'a').Replace("buffer-features", process, StringComparison.Ordinal));
        var publications = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => store.PublishWithStatusAsync(document, "test-operator")));
        publications.Count(result => result.Created).Should().Be(1);
        var release = publications[0].Record;
        publications.Select(result => result.Record.PublishedAt).Distinct().Should().ContainSingle();
        var defaultSchemaStore = new PostgresGpProcessReleaseStore(
            _fixture.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        (await defaultSchemaStore.GetReleaseAsync(release.Digest)).Should().BeNull();
        var activation = new GpProcessBindingActivation
        {
            Process = process,
            LockDigest = release.Digest,
            ExpectedBindingVersion = 0,
            Operation = "deploy",
            Actor = "test-operator",
            Targets = [new GpProcessBindingTargetIdentity
            {
                Kind = "aws-batch", JobDefinitionArn = JobDefinitionArn.Replace("buffer-features", process, StringComparison.Ordinal),
            }],
        };
        var environment = new GpProcessReleaseEnvironment
        {
            ServerContractVersion = 1,
            SupportedTargetKinds = ["aws-batch"],
            CustomCodeMayUseKubernetes = false,
            ReadyTargets = [new GpProcessBindingTarget
            {
                Kind = "aws-batch",
                Image = document.RuntimeImage.Reference,
                JobDefinitionArn = activation.Targets[0].JobDefinitionArn,
            }],
        };

        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => store.ActivateAsync(activation, environment)));

        results.Count(r => r.Activated).Should().Be(1);
        results.Where(r => !r.Activated).Should().OnlyContain(r => r.Reasons.Contains(GpProcessBindingRefusal.StaleBindingVersion));
        var reopened = new PostgresGpProcessReleaseStore(
            _fixture.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, schema);
        var persisted = await reopened.GetBindingAsync(process);
        persisted!.BindingVersion.Should().Be(1);
        persisted.LockDigest.Should().Be(release.Digest);
        (await reopened.GetReleaseAsync(release.Digest))!.Lock.Digest.Should().Be(release.Digest);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static object Activate(string digest, int expected, string operation = "deploy")
        => new
        {
            lockDigest = digest,
            expectedBindingVersion = expected,
            operation,
            targets = new[] { new { kind = "aws-batch", jobDefinitionArn = JobDefinitionArn } },
        };

    private static string Lock(string version, char sha)
        => $$"""
            {
              "schema": "honua.gp-process-release-lock/v1",
              "process": {
                "name": "buffer-features",
                "version": "{{version}}",
                "runtime": "python",
                "entrypoint": "buffer_features:execute",
                "contract": {
                  "inputSchemaSha256": "sha256:{{new string('1', 64)}}",
                  "outputSchemaSha256": "sha256:{{new string('2', 64)}}"
                }
              },
              "source": { "repository": "honua-io/honua-samples", "sha": "{{new string(sha, 40)}}", "descriptorPath": "samples/gp-local-dev/honua-gp.yaml" },
              "dependencyLock": { "path": "samples/gp-local-dev/uv.lock", "sha256": "sha256:{{new string('d', 64)}}" },
              "runtimeImage": { "reference": "ghcr.io/honua-io/honua-gp-worker@sha256:{{new string('b', 64)}}", "digest": "sha256:{{new string('b', 64)}}" },
              "workflowVersion": "wf-2026.1-7",
              "configurationReferences": [],
              "targets": [ { "kind": "aws-batch", "workerImageDigest": "sha256:{{new string('b', 64)}}", "jobDefinitionName": "buffer-features" } ],
              "compatibility": { "serverContractVersion": { "minimum": 1, "maximum": 4 } },
              "artifacts": [ { "name": "buffer-features-{{version}}.tar.gz", "sha256": "sha256:{{new string('e', 64)}}", "sizeBytes": 18432 } ],
              "validation": { "receipts": [ { "name": "golden-tests.junit.xml", "sha256": "sha256:{{new string('9', 64)}}", "status": "pass" } ] },
              "generatedAt": "2026-10-15T04:00:00Z"
            }
            """;

    /// <summary>A registry that advertises one activatable AWS Batch workload.</summary>
    private sealed class AwsBatchOnlyRegistry : IExecutionJobDefinitionRegistry
    {
        private static readonly ExecutionJobDefinition Definition = new()
        {
            WorkloadId = "custom-code-batch",
            TargetKind = BatchComputeTargetKind.AwsBatch,
            Backend = "aws-batch",
            Kind = ExecutionJobKind.Geoprocessing,
            WorkloadName = "Custom code (AWS Batch)",
            RuntimeProfile = "custom-code",
            ArtifactReference = "ghcr.io/honua-io/honua-gp-worker@sha256:" + new string('b', 64),
            Parameters = new Dictionary<string, string> { [GpProcessReleaseAdmission.AwsBatchJobDefinitionParam] = JobDefinitionArn },
        };

        public Task<IReadOnlyList<ExecutionJobDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionJobDefinition>>([Definition]);

        public Task<ExecutionJobDefinition?> GetAsync(string workloadId, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(workloadId, Definition.WorkloadId, StringComparison.Ordinal) ? Definition : null);
    }
}
