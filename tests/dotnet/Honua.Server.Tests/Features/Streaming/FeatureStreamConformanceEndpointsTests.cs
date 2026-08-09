// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Integration tests for the controlled-conformance mutation workflow (honua-server#3038,
/// REQ-005/REQ-006/NFR-001): lease isolation, ownership-checked mutation and cleanup,
/// deployment-revision binding, fail-closed refusals, and observation of a controlled
/// mutation on the live feature stream.
/// </summary>
/// <remarks>
/// The conformance source is pointed at the seeded <c>test</c> service/layer with the
/// ownership marker stored in its <c>name</c> attribute. That keeps the test honest about the
/// contract that matters — a dedicated source named by configuration, with ownership read back
/// from the stored row — without needing a second seeded schema. The seeded baseline record's
/// <c>name</c> is ordinary text and therefore never parses as a marker, which is exactly the
/// property that protects real records in a deployment.
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Streaming)]
[Operation(Operations.Streaming)]
public sealed class FeatureStreamConformanceEndpointsTests : IAsyncLifetime
{
    private const string RunsPath = "/api/v1/streaming/conformance/runs";
    private const string ResetPath = "/api/v1/admin/streaming/conformance/reset";
    private const string RunTokenHeader = "X-Honua-Conformance-Run-Token";
    private const string TestRevision = "0123456789abcdef0123456789abcdef01234567";

    private readonly WebAppFixture _fixture = CreateFixture(maxConcurrentRuns: 2);
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    // ── REQ-005: lease, correlate, mutate, clean up ─────────────────────────────

    [IntegrationTest]
    [Endpoint("POST /api/v1/streaming/conformance/runs")]
    public async Task LeaseRun_BindsTheRunToTheDeploymentRevisionAndTheConfiguredSource()
    {
        using var content = new StringContent("""{"clientLabel":"conformance-test"}""", Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(
            "/api/v1/streaming/conformance/runs",
            content,
            CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var run = ReadLeasedRun(await ReadJsonAsync(response));

        run.RunId.Should().NotBeNullOrWhiteSpace();
        run.RunToken.Should().NotBeNullOrWhiteSpace();
        run.ServiceId.Should().Be("test");
        run.RunIdField.Should().Be("name");
        run.DeploymentRevision.Should().Be(TestRevision);
        run.RunMarker.Should().StartWith("honua-conformance:");
        run.BaselineDigest.Should().StartWith("sha256:");
        run.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/streaming/conformance/runs/{runId}/mutations")]
    public async Task Mutate_InsertUpdateTouchDelete_AreAcceptedAndBounded()
    {
        var run = await LeaseRunAsync();

        using var insertContent = new StringContent(
            """{"operation":"insert","label":"alpha"}""",
            Encoding.UTF8,
            "application/json");
        using var insertRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/streaming/conformance/runs/{run.RunId}/mutations")
        {
            Content = insertContent
        };
        insertRequest.Headers.Add(RunTokenHeader, run.RunToken);
        using var inserted = await _client.SendAsync(insertRequest, CancellationToken.None);
        inserted.StatusCode.Should().Be(HttpStatusCode.OK);
        var insertedBody = await ReadJsonAsync(inserted);
        var objectId = insertedBody.GetProperty("data").GetProperty("objectId").GetInt64();
        insertedBody.GetProperty("data").GetProperty("mutationOrdinal").GetInt32().Should().Be(1);
        insertedBody.GetProperty("data").GetProperty("ownedRecords").GetInt32().Should().Be(1);

        var updated = await MutateAsync(run, $$"""{"operation":"update","objectId":{{objectId}},"label":"beta"}""");
        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        var touched = await MutateAsync(run, $$"""{"operation":"touch","objectId":{{objectId}}}""");
        touched.StatusCode.Should().Be(HttpStatusCode.OK);

        var deleted = await MutateAsync(run, $$"""{"operation":"delete","objectId":{{objectId}}}""");
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);
        var deletedBody = await ReadJsonAsync(deleted);
        deletedBody.GetProperty("data").GetProperty("ownedRecords").GetInt32().Should().Be(0);

        await CleanupAsync(run);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/streaming/conformance/runs/{runId}/mutations")]
    public async Task Mutate_WithUnknownOperation_Returns400()
    {
        var run = await LeaseRunAsync();

        var response = await MutateAsync(run, """{"operation":"truncate"}""");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an unrecognized operation must fail closed rather than defaulting to a mutation the caller did not ask for");
        await CleanupAsync(run);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/streaming/conformance/runs/{runId}/mutations")]
    public async Task Mutate_WhenTheMutationBudgetIsSpent_Returns409()
    {
        var run = await LeaseRunAsync(ttlSeconds: 300);

        // The fixture caps a run at three mutations.
        for (var i = 0; i < 3; i++)
        {
            (await MutateAsync(run, """{"operation":"insert"}""")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var overBudget = await MutateAsync(run, """{"operation":"insert"}""");

        overBudget.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await CleanupAsync(run);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/streaming/conformance/runs/{runId}")]
    public async Task CleanupRun_DeletesEveryOwnedRecordAndRestoresTheBaselineDigest()
    {
        var run = await LeaseRunAsync();

        await MutateAsync(run, """{"operation":"insert","label":"first"}""");
        await MutateAsync(run, """{"operation":"insert","label":"second"}""");

        using var cleanupRequest = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/v1/streaming/conformance/runs/{run.RunId}");
        cleanupRequest.Headers.Add(RunTokenHeader, run.RunToken);
        using var cleanup = await _client.SendAsync(cleanupRequest, CancellationToken.None);
        cleanup.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await ReadJsonAsync(cleanup)).GetProperty("data");
        data.GetProperty("deletedRecords").GetInt32().Should().Be(2);
        data.GetProperty("baselineRestored").GetBoolean().Should().BeTrue();
        data.GetProperty("baselineDigest").GetString().Should().Be(run.BaselineDigest,
            "a run that cleaned up left the immutable baseline exactly as it found it");

        // Cleanup is idempotent from the caller's perspective: the lease is gone, so a second
        // call from a finally block cannot resurrect or double-delete anything.
        var second = await CleanupAsync(run);
        second.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── REQ-005/REQ-006: concurrent runs cannot claim or destroy each other ──────

    [IntegrationTest]
    [Endpoint("POST /api/v1/streaming/conformance/runs/{runId}/mutations")]
    public async Task Mutate_AgainstAnotherRunsRecord_Returns404AndLeavesItIntact()
    {
        var owner = await LeaseRunAsync(label: "owner");
        var intruder = await LeaseRunAsync(label: "intruder");

        var inserted = await ReadJsonAsync(await MutateAsync(owner, """{"operation":"insert"}"""));
        var objectId = inserted.GetProperty("data").GetProperty("objectId").GetInt64();

        var stolenUpdate = await MutateAsync(intruder, $$"""{"operation":"update","objectId":{{objectId}}}""");
        var stolenDelete = await MutateAsync(intruder, $$"""{"operation":"delete","objectId":{{objectId}}}""");

        stolenUpdate.StatusCode.Should().Be(HttpStatusCode.NotFound);
        stolenDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The intruder's own cleanup must not take the owner's record with it.
        var intruderCleanup = await ReadJsonAsync(await CleanupAsync(intruder));
        intruderCleanup.GetProperty("data").GetProperty("deletedRecords").GetInt32().Should().Be(0);

        var ownerCleanup = await ReadJsonAsync(await CleanupAsync(owner));
        ownerCleanup.GetProperty("data").GetProperty("deletedRecords").GetInt32().Should().Be(1);
        ownerCleanup.GetProperty("data").GetProperty("baselineRestored").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/streaming/conformance/runs/{runId}/mutations")]
    public async Task Mutate_WithoutTheRunToken_Returns404()
    {
        var run = await LeaseRunAsync();

        using var content = new StringContent("""{"operation":"insert"}""", Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync($"{RunsPath}/{run.RunId}/mutations", content, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a valid conformance credential alone must not be enough to act as a particular run");
        await CleanupAsync(run);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/streaming/conformance/runs")]
    public async Task LeaseRun_WhenEveryLeaseIsHeld_Returns409()
    {
        var first = await LeaseRunAsync();
        var second = await LeaseRunAsync();

        using var response = await PostLeaseAsync(label: "third");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await CleanupAsync(first);
        await CleanupAsync(second);
    }

    // ── REQ-006: fail closed on identity mismatch ───────────────────────────────

    [IntegrationTest]
    [Endpoint("POST /api/v1/streaming/conformance/runs")]
    public async Task LeaseRun_WithAnotherDeploymentsRevision_Returns409()
    {
        using var content = new StringContent(
            """{"expectedDeploymentRevision":"ffffffffffffffffffffffffffffffffffffffff"}""",
            Encoding.UTF8,
            "application/json");

        using var response = await _client.PostAsync(RunsPath, content, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a run scheduled against one image must never silently produce evidence against another");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/streaming/conformance/runs")]
    public async Task LeaseRun_WithAnotherSourceIdentity_Returns409()
    {
        using var content = new StringContent(
            """{"expectedServiceId":"maui-inspections"}""",
            Encoding.UTF8,
            "application/json");

        using var response = await _client.PostAsync(RunsPath, content, CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/streaming/conformance/runs")]
    public async Task LeaseRun_WithoutAConformanceCredential_Returns401()
    {
        using var anonymous = _fixture.CreateClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await anonymous.PostAsync(RunsPath, content, CancellationToken.None);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    // ── NFR-001/NFR-002: reset, and the anonymous advertisement ─────────────────

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/streaming/conformance/reset")]
    public async Task Reset_DropsEveryLeaseAndDeletesEveryControlledRecord()
    {
        var run = await LeaseRunAsync();
        await MutateAsync(run, """{"operation":"insert"}""");

        using var response = await _client.PostAsync(
            "/api/v1/admin/streaming/conformance/reset",
            content: null,
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body.GetProperty("data").GetProperty("releasedRuns").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        body.GetProperty("data").GetProperty("deletedRecords").GetInt32().Should().BeGreaterThanOrEqualTo(1);

        // The released run can no longer act.
        var afterReset = await MutateAsync(run, """{"operation":"insert"}""");
        afterReset.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features/capabilities")]
    public async Task Capabilities_AdvertiseTheConformanceContractAndTheImmutableRevision()
    {
        using var response = await _client.GetAsync("/api/v1/streaming/features/capabilities", CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var data = (await ReadJsonAsync(response)).GetProperty("data");

        // REQ-004: the immutable revision is published under the field name realtime clients
        // read, separately from the mutable release version.
        data.GetProperty("serverRevision").GetString().Should().Be(TestRevision);
        data.GetProperty("deploymentRevision").GetString().Should().Be(TestRevision);
        data.GetProperty("serverVersion").GetString().Should().NotBe(TestRevision);

        data.GetProperty("modes").EnumerateArray().Select(mode => mode.GetString())
            .Should().Contain(["delta", "snapshot", "snapshot-then-delta"]);

        var conformance = data.GetProperty("conformance");
        conformance.GetProperty("enabled").GetBoolean().Should().BeTrue();
        conformance.GetProperty("serviceId").GetString().Should().Be("test");
        conformance.GetProperty("runIdField").GetString().Should().Be("name");
        conformance.GetProperty("maxConcurrentRuns").GetInt32().Should().Be(2);
        conformance.GetProperty("operations").EnumerateArray().Select(op => op.GetString())
            .Should().Contain(["insert", "update", "touch", "delete"]);

        // NFR-002: the advertisement is credential-free.
        conformance.TryGetProperty("runToken", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task Manifest_PublishesTheImmutableRevisionUnderBothNames()
    {
        using var response = await _client.GetAsync("/api/v1/capabilities/manifest", CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await ReadJsonAsync(response);
        var server = ResolveServerBlock(body);

        server.GetProperty("serverRevision").GetString().Should().Be(TestRevision);
        server.GetProperty("deploymentRevision").GetString().Should().Be(TestRevision);
    }

    // ── REQ-005: the controlled mutation is observable on the live stream ────────

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task ControlledMutation_IsObservedOnTheStreamAfterTheBaseline()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/streaming/features?layers=0&mode=snapshot-then-delta");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var snapshot = await ReadSseEventAsync(reader, "snapshot", cts.Token);
        snapshot.Should().NotBeNull("a snapshot-then-delta subscription starts from a batched baseline");
        var snapshotFrame = snapshot!.Value;
        var baselineSequence = snapshotFrame.GetProperty("sequence").GetInt64();

        var run = await LeaseRunAsync();
        var inserted = await ReadJsonAsync(await MutateAsync(run, """{"operation":"insert","label":"observed"}"""));
        var objectId = inserted.GetProperty("data").GetProperty("objectId").GetInt64();

        var delta = await ReadSseEventAsync(reader, "feature-change", cts.Token);
        delta.Should().NotBeNull("a controlled mutation goes through the canonical edit pipeline and is therefore streamed");
        var deltaFrame = delta!.Value;
        deltaFrame.GetProperty("objectId").GetInt64().Should().Be(objectId);
        deltaFrame.GetProperty("operation").GetString().Should().Be("insert");
        deltaFrame.GetProperty("sequence").GetInt64().Should().Be(baselineSequence + 1,
            "the first delta continues the batched baseline's subscription-local sequence");

        // The run's marker is on the streamed after-image, which is how a subscriber
        // recognizes its own correlated mutation.
        deltaFrame.GetProperty("attributes").GetProperty("name").GetString().Should().Be(run.RunMarker);

        await CleanupAsync(run);
    }

    // ── fail closed when the deployment provisions no conformance source ────────

    [IntegrationTest]
    [Endpoint("POST /api/v1/streaming/conformance/runs")]
    public async Task LeaseRun_WhenTheDeploymentProvisionsNoConformanceSource_Returns403()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, configBuilder) =>
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FeatureStreaming:Conformance:Enabled"] = "false"
                })));

        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var response = await client.PostAsync(RunsPath, content, CancellationToken.None);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/streaming/conformance/runs")]
    public async Task LeaseRun_WhenTheDeploymentReportsNoImmutableRevision_Returns503()
    {
        var fixture = new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, configBuilder) =>
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FeatureStreaming:Conformance:Enabled"] = "true",
                    ["FeatureStreaming:Conformance:ServiceId"] = "test",
                    ["FeatureStreaming:Conformance:LayerId"] = "0",
                    ["FeatureStreaming:Conformance:RunIdField"] = "name",
                    // Deliberately malformed: DeploymentIdentity rejects it, so the deployment
                    // reports no revision at all.
                    ["Deployment:Revision"] = "not-a-revision"
                })));

        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var response = await client.PostAsync(RunsPath, content, CancellationToken.None);

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
                "evidence that cannot name the deployment it was produced against is not evidence");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static WebAppFixture CreateFixture(int maxConcurrentRuns)
        => new WebAppFixture()
            .ReplaceService<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Pro))
            .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, configBuilder) =>
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FeatureStreaming:Conformance:Enabled"] = "true",
                    ["FeatureStreaming:Conformance:ServiceId"] = "test",
                    ["FeatureStreaming:Conformance:LayerId"] = "0",
                    ["FeatureStreaming:Conformance:RunIdField"] = "name",
                    ["FeatureStreaming:Conformance:LabelField"] = "category",
                    ["FeatureStreaming:Conformance:MaxConcurrentRuns"] = maxConcurrentRuns.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["FeatureStreaming:Conformance:MaxMutationsPerRun"] = "3",
                    ["FeatureStreaming:Conformance:MaxRecordsPerRun"] = "3",
                    // A long sweep interval keeps the background sweeper from racing these
                    // tests; TTL reclamation itself is covered by the registry unit tests.
                    ["FeatureStreaming:Conformance:SweepInterval"] = "00:30:00",
                    ["Deployment:Revision"] = TestRevision
                })));

    private async Task<LeasedRun> LeaseRunAsync(string? label = null, int? ttlSeconds = null)
    {
        using var response = await PostLeaseAsync(label, ttlSeconds);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return ReadLeasedRun(await ReadJsonAsync(response));
    }

    private static LeasedRun ReadLeasedRun(JsonElement body)
    {
        var data = body.GetProperty("data");
        return new LeasedRun(
            data.GetProperty("runId").GetString()!,
            data.GetProperty("runToken").GetString()!,
            data.GetProperty("runMarker").GetString()!,
            data.GetProperty("serviceId").GetString()!,
            data.GetProperty("runIdField").GetString()!,
            data.GetProperty("deploymentRevision").GetString()!,
            data.GetProperty("baselineDigest").GetString()!,
            data.GetProperty("expiresAt").GetDateTimeOffset());
    }

    private async Task<HttpResponseMessage> PostLeaseAsync(string? label = null, int? ttlSeconds = null)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["clientLabel"] = label ?? "conformance-test",
            ["ttlSeconds"] = ttlSeconds
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        return await _client.PostAsync(RunsPath, content, CancellationToken.None);
    }

    private async Task<HttpResponseMessage> MutateAsync(LeasedRun run, string payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{RunsPath}/{run.RunId}/mutations")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add(RunTokenHeader, run.RunToken);
        return await _client.SendAsync(request, CancellationToken.None);
    }

    private async Task<HttpResponseMessage> CleanupAsync(LeasedRun run)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{RunsPath}/{run.RunId}");
        request.Headers.Add(RunTokenHeader, run.RunToken);
        return await _client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync(CancellationToken.None);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The manifest wraps its payload in the shared API envelope on some surfaces and returns
    /// it bare on others; accept either rather than pinning the test to the envelope shape.
    /// </summary>
    private static JsonElement ResolveServerBlock(JsonElement body)
        => body.TryGetProperty("data", out var data) && data.TryGetProperty("server", out var wrapped)
            ? wrapped
            : body.GetProperty("server");

    private static async Task<JsonElement?> ReadSseEventAsync(
        StreamReader reader,
        string eventName,
        CancellationToken cancellationToken)
    {
        string? currentEvent = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return null;
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                currentEvent = line["event: ".Length..];
                continue;
            }

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(currentEvent, eventName, StringComparison.Ordinal))
            {
                currentEvent = null;
                continue;
            }

            using var document = JsonDocument.Parse(line["data: ".Length..]);
            return document.RootElement.Clone();
        }

        return null;
    }

    private readonly record struct LeasedRun(
        string RunId,
        string RunToken,
        string RunMarker,
        string ServiceId,
        string RunIdField,
        string DeploymentRevision,
        string BaselineDigest,
        DateTimeOffset ExpiresAt);
}
