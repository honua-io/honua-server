// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Studio.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Testcontainers.Redis;

namespace Honua.Server.Tests.Features.Studio;

/// <summary>
/// Owner ruling (2026-10-10): the governed proposal control plane requires Redis to be present
/// and working, not a <c>CONFIG GET</c>-proven durable policy. This boots the real composition
/// root in <c>Production</c> (so the production Redis path, the distributed-events requirement and
/// the mandatory key-ring certificate all apply) against a STOCK <c>redis:7</c> — no
/// <c>appendonly</c>, the same non-attested shape AWS ElastiCache Redis 7 presents — and proves
/// that <c>operations.proposals</c> is advertised, the manifest publishes the durability outcome
/// as information, and a Studio publication request actually creates a durable proposal.
/// Before the ruling the manifest read <c>dependency-unavailable</c> with
/// <c>missingDependency=redis</c> on exactly this topology although Redis was serving.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Studio)]
[Operation(Operations.StudioLifecycle)]
public sealed class StudioPublishRequestNonAttestedRedisTests : IAsyncLifetime
{
    private const string JsonMediaType = "application/json";
    private const string KeyRingPassword = "non-attested-redis-keyring";

    // Production enforces a complex bootstrap admin password.
    private const string AdminPassword = "Synthetic-NonAttested-Redis-4721!";

    private readonly WebAppFixture _fixture = new();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7").Build();
    private string? _keyRingPath;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        _keyRingPath = WriteKeyRingCertificate();
        _fixture
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment(Environments.Production);
                builder.UseSetting("ConnectionStrings:redis", _redis.GetConnectionString());
                builder.UseSetting("Operations:SecretChannel:KeyRingCertificatePath", _keyRingPath);
                builder.UseSetting("Operations:SecretChannel:KeyRingCertificatePassword", KeyRingPassword);
                builder.UseSetting("Licensing:Mode", "Disabled");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Studio:EndUserAuthorization:Enabled", "true");
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IStudioPackageStore>();
                services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
                services.RemoveAll<IOperatorAuthorizationEvaluator>();
                services.AddSingleton<IOperatorAuthorizationEvaluator, AllowAllOperatorAuthorizationEvaluator>();
            });
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        await _redis.DisposeAsync();
        if (_keyRingPath is not null)
        {
            File.Delete(_keyRingPath);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/capabilities/manifest")]
    public async Task StockRedis_Production_AdvertisesOperationsProposalsAndPublishesDurabilityAsInformation()
    {
        _fixture.Services.GetRequiredService<IHostEnvironment>().EnvironmentName.Should().Be(Environments.Production);
        var substrate = _fixture.Services.GetRequiredService<IOptions<DurableJobSubstrateOptions>>().Value;
        substrate.RedisDurabilityAttestation.Should().BeNull("stock redis:7 runs without appendonly");
        substrate.RedisDurabilityStatus.Should().Be(RedisDurabilityStatuses.NotDurable);

        using var client = CreateAdminClient();
        using var response = await client.GetAsync("/api/v1/capabilities/manifest");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        foreach (var capabilityId in new[] { CapabilityUnavailableCodes.ControlPlaneProposalsCapability, "jobs.runner" })
        {
            var capability = root.GetProperty("capabilities").EnumerateArray()
                .Single(entry => entry.GetProperty("id").GetString() == capabilityId);
            capability.GetProperty("available").GetBoolean().Should().BeTrue(
                $"a connected Redis enables {capabilityId} whatever the durability attestation said: {capability.GetRawText()}");
        }

        var job = root.GetProperty("limits").GetProperty("job");
        job.GetProperty("durableJobRuntimeAvailable").GetBoolean().Should().BeTrue();
        var durability = job.GetProperty("redisDurability");
        durability.GetProperty("status").GetString().Should().Be(RedisDurabilityStatuses.NotDurable);
        durability.GetProperty("cause").GetString()
            .Should().Be(nameof(DurableJobSubstrateCause.RedisPersistenceDisabled));
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/package-drafts")]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/validate")]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/preview-plan")]
    [Endpoint("POST /api/v1/studio/package-drafts/{draftId}/content-versions")]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/versions/{versionId}/publish-requests")]
    public async Task StockRedis_Production_PublishRequestCreatesADurableProposal()
    {
        var proposalStore = _fixture.Services.GetRequiredService<IOperationProposalStore>();
        UnavailableOperationProposalStore.IsDurable(proposalStore).Should().BeTrue(
            "a connected Redis composes the Redis-backed proposal store");

        using var client = CreateAdminClient();
        var createResponse = await PostAsync(
            client,
            "/api/v1/studio/package-drafts",
            new CreateStudioPackageDraftRequest
            {
                PackageKey = $"non-attested-{Guid.NewGuid():N}",
                WorkspaceId = "studio",
                Envelope = BuildEnvelope(),
            },
            StudioApiJsonContext.Default.CreateStudioPackageDraftRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, await createResponse.Content.ReadAsStringAsync());
        var draft = await ReadAsync(createResponse, StudioApiJsonContext.Default.ApiResponseStudioPackageDraft);

        (await PostEmptyJsonAsync(client, $"/api/v1/studio/package-drafts/{draft.DraftId:D}/validate"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostEmptyJsonAsync(client, $"/api/v1/studio/package-drafts/{draft.DraftId:D}/preview-plan"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var saveResponse = await PostAsync(
            client,
            $"/api/v1/studio/package-drafts/{draft.DraftId:D}/content-versions",
            new SaveStudioContentVersionRequest { ChangeNote = "non-attested redis save" },
            StudioApiJsonContext.Default.SaveStudioContentVersionRequest);
        saveResponse.StatusCode.Should().Be(HttpStatusCode.Created, await saveResponse.Content.ReadAsStringAsync());
        var version = await ReadAsync(saveResponse, StudioApiJsonContext.Default.ApiResponseStudioContentVersion);

        var publishResponse = await PostAsync(
            client,
            $"/api/v1/studio/content-items/{version.ItemId:D}/versions/{version.VersionId:D}/publish-requests",
            new CreateStudioPublicationRequest { WarningAcknowledgement = "reviewed" },
            StudioApiJsonContext.Default.CreateStudioPublicationRequest);

        var body = await publishResponse.Content.ReadAsStringAsync();
        publishResponse.StatusCode.Should().Be(HttpStatusCode.Accepted, body);
        body.Should().NotContain(CapabilityUnavailableCodes.ErrorCode);
        var handle = JsonSerializer.Deserialize(body, StudioApiJsonContext.Default.ApiResponseOperationHandle);
        handle.Should().NotBeNull();
        handle!.Data.Should().NotBeNull();
        var proposalId = handle.Data!.ProposalId;
        proposalId.Should().NotBeNullOrWhiteSpace("an approval-gated publication request persists a proposal");

        var proposal = await proposalStore.GetAsync(proposalId!);
        proposal.Should().NotBeNull("the proposal is persisted in the Redis-backed store");
        proposal!.ProposalId.Should().Be(proposalId);
    }

    private HttpClient CreateAdminClient()
        => _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));

    private static string WriteKeyRingCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Honua non-attested Redis key ring",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
        var path = Path.Join(Path.GetTempPath(), $"honua-keyring-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12, KeyRingPassword));
        return path;
    }

    private static async Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string path, T body, JsonTypeInfo<T> typeInfo)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body, typeInfo), Encoding.UTF8, JsonMediaType);
        return await client.PostAsync(path, content);
    }

    private static async Task<HttpResponseMessage> PostEmptyJsonAsync(HttpClient client, string path)
    {
        using var content = new StringContent("{}", Encoding.UTF8, JsonMediaType);
        return await client.PostAsync(path, content);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<ApiResponse<T>> typeInfo)
    {
        var envelope = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), typeInfo);
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        return envelope.Data!;
    }

    private static StudioPackageEnvelope BuildEnvelope()
    {
        using var body = JsonDocument.Parse("""{"where":"1=1"}""");
        return new StudioPackageEnvelope
        {
            Family = StudioPackageFamily.Query,
            SchemaVersion = "1.0",
            Format = "studio_query_package.v1",
            Bindings =
            [
                new StudioPackageBinding
                {
                    Key = "source",
                    Kind = "content",
                    Ref = "content.parcels",
                    Crs = "EPSG:4326",
                    Srid = 4326,
                    RequiredPermissions = ["metadata.read"],
                },
            ],
            Dependencies =
            [
                new StudioPackageDependency { Kind = "content-item", Ref = "content.parcels", VersionId = "v1" },
            ],
            Provenance =
            [
                new StudioProvenanceRef { Kind = "prompt", Ref = "prompt-1", Rel = "generated-by" },
            ],
            PublicationIntent = new StudioPublicationIntent { Route = "/studio/non-attested-redis", Visibility = "organization" },
            Body = body.RootElement.Clone(),
        };
    }

    private sealed class AllowAllOperatorAuthorizationEvaluator : IOperatorAuthorizationEvaluator
    {
        public Task<AccessDecision> EvaluateAsync(
            ClaimsPrincipal principal,
            OperatorAuthorizationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AccessDecision.Allowed("non-attested redis publication fixture"));
    }
}
