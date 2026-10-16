// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Geoprocessing.CustomCode;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Geoprocessing.CustomCode;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>HTTP broker security, durable receipt publication and immutable byte round trips.</summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class CustomCodeArtifactEndpointsTests : IAsyncLifetime
{
    private readonly ConcurrentDictionary<string, ExecutionJobRecord> _records = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly ICloudFileStorage _storage = Substitute.For<ICloudFileStorage>();
    private readonly ICustomCodeArtifactIngestor _ingestor = Substitute.For<ICustomCodeArtifactIngestor>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly ScopedJobTokenIssuer _issuer;
    private readonly WebAppFixture _fixture;
    private HttpClient _worker = null!;
    private HttpClient _admin = null!;
    private string _token = string.Empty;
    private bool _rotateDuringUpload;

    public CustomCodeArtifactEndpointsTests()
    {
        _issuer = new ScopedJobTokenIssuer(_cache, NullLogger<ScopedJobTokenIssuer>.Instance);
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
        _storage.Provider.Returns(CloudStorageProvider.AwsS3);
        _storage.UploadIfMatchAsync(Arg.Any<FileUploadRequest>(), null, Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var request = call.ArgAt<FileUploadRequest>(0);
                using var bytes = new MemoryStream();
                await request.Content.CopyToAsync(bytes);
                if (!_objects.TryAdd(request.ObjectKeyOverride!, bytes.ToArray()))
                {
                    return UploadResult.CreateFailure("Already exists");
                }
                if (_rotateDuringUpload)
                {
                    _records["broker-job"] = _records["broker-job"] with { AttemptCount = 2 };
                }
                return UploadResult.CreateSuccess(new CloudFile
                {
                    FileId = request.ObjectKeyOverride!, FileName = request.FileName, StoragePath = request.ObjectKeyOverride!,
                    ContentType = request.ContentType, SizeBytes = bytes.Length, UploadedAt = DateTimeOffset.UtcNow, ETag = "generation-1",
                });
            });
        _storage.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<CloudFile?>(_objects.TryGetValue(call.ArgAt<string>(0), out var bytes)
                ? new CloudFile { FileId = call.ArgAt<string>(0), FileName = "result.json", StoragePath = call.ArgAt<string>(0),
                    ContentType = "application/octet-stream", SizeBytes = bytes.Length, UploadedAt = DateTimeOffset.UtcNow }
                : null));
        _storage.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<Stream?>(_objects.TryGetValue(call.ArgAt<string>(0), out var bytes) ? new MemoryStream(bytes) : null));
        _storage.DeleteIfMatchAsync(Arg.Any<string>(), "generation-1", Arg.Any<CancellationToken>())
            .Returns(call => _objects.TryRemove(call.ArgAt<string>(0), out _));
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
                services.RemoveAll<ICloudFileStorage>();
                services.AddSingleton(_storage);
                services.RemoveAll<IScopedJobTokenIssuer>();
                services.AddSingleton<IScopedJobTokenIssuer>(_issuer);
                services.AddSingleton(_ingestor);
                services.RemoveAll<IJobQueue>();
                services.AddSingleton(Substitute.For<IJobQueue>());
            });
    }

    public async Task InitializeAsync()
    {
        var issuance = await _issuer.IssueAsync(new ScopedJobTokenRequest("alice", null, [], [], "broker-job", [],
            DateTimeOffset.UtcNow.AddHours(1)) { Attempt = 1 }, CancellationToken.None);
        _token = issuance.Token;
        _records["broker-job"] = Job("broker-job", _token);
        _records["other-job"] = Job("other-job", "other-token");
        await _fixture.InitializeAsync();
        _worker = _fixture.CreateClient();
        _worker.DefaultRequestHeaders.Add("X-Api-Key", _token);
        _admin = _fixture.CreateAdminClient();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        _cache.Dispose();
    }

    [IntegrationTest]
    [Endpoint("PUT /api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}")]
    [Endpoint("GET /api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}")]
    [Endpoint("GET /api/v1/admin/jobs/{jobId}/artifacts")]
    public async Task Upload_DownloadAndAdminListing_UseImmutableAuthorizedBytes()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"result\":42}");
        using var uploaded = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1", new ByteArrayContent(bytes));
        uploaded.StatusCode.Should().Be(HttpStatusCode.Created, await uploaded.Content.ReadAsStringAsync());
        CustomCodeArtifactDescriptor.TryParse(_records["broker-job"].ArtifactReferences.Single(), out var receipt).Should().BeTrue();
        receipt!.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        receipt.State.Should().Be("available");
        using var download = await _admin.GetAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1");
        download.StatusCode.Should().Be(HttpStatusCode.OK, await download.Content.ReadAsStringAsync());
        (await download.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
        using var listing = await _admin.GetAsync("/api/v1/admin/jobs/broker-job/artifacts");
        listing.StatusCode.Should().Be(HttpStatusCode.OK, await listing.Content.ReadAsStringAsync());
        var item = (await listing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];
        item.GetProperty("label").GetString().Should().Be("result.json");
        item.GetProperty("sizeBytes").GetInt64().Should().Be(bytes.Length);
        item.GetProperty("providerLink").GetString().Should().Be(receipt.ContentUrl);

        using var overwrite = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1", new ByteArrayContent([0]));
        overwrite.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _objects.Values.Single().Should().Equal(bytes);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}")]
    [Endpoint("GET /api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}")]
    public async Task Upload_RejectsCrossJobExpiredAndStaleAttemptCredentials()
    {
        using var crossJob = await _worker.PutAsync("/api/geoprocessing/jobs/other-job/custom-artifacts/result.json?attempt=1", new ByteArrayContent([1]));
        crossJob.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var wrongAttempt = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=2", new ByteArrayContent([1]));
        wrongAttempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _records["broker-job"] = _records["broker-job"] with { Spec = _records["broker-job"].Spec with
        {
            Parameters = new Dictionary<string, string> { [CustomCodeJobContract.JobTokenEnvParam] = "rotated-token" },
        } };
        using var rotated = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1", new ByteArrayContent([1]));
        rotated.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await _issuer.RevokeAsync(_token, CancellationToken.None);
        using var revoked = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1", new ByteArrayContent([1]));
        revoked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var anonymous = _fixture.CreateClient();
        using var forbiddenRead = await anonymous.GetAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1");
        forbiddenRead.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _objects.Should().BeEmpty();
    }

    [IntegrationTest]
    [Endpoint("PUT /api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}")]
    public async Task Upload_RejectsUnsafeNamesOversizedBodiesAndTerminalJobs()
    {
        using var unsafeName = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/bad..json?attempt=1", new ByteArrayContent([1]));
        unsafeName.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var content = new ByteArrayContent([1]);
        content.Headers.ContentLength = CustomCodeArtifactEndpoints.MaximumArtifactBytes + 1;
        using var oversized = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/large.json?attempt=1", content);
        oversized.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        foreach (var status in new[] { ExecutionJobStatus.Provisioning, ExecutionJobStatus.Succeeded, ExecutionJobStatus.Failed, ExecutionJobStatus.Cancelled })
        {
            _records["broker-job"] = _records["broker-job"] with { Status = status };
            using var stopped = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1", new ByteArrayContent([1]));
            stopped.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        _objects.Should().BeEmpty();
    }

    [IntegrationTest]
    [Endpoint("PUT /api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}")]
    public async Task Upload_ConcurrentReservations_RespectCountCap()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 40).Select(index => _worker.PutAsync(
            $"/api/geoprocessing/jobs/broker-job/custom-artifacts/file{index}.json?attempt=1", new ByteArrayContent([1]))));
        try
        {
            responses.Should().OnlyContain(response => response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict or HttpStatusCode.RequestEntityTooLarge);
            _records["broker-job"].ArtifactReferences.Count.Should().BeLessThanOrEqualTo(CustomCodeArtifactEndpoints.MaximumArtifacts);
            _objects.Count.Should().BeLessThanOrEqualTo(CustomCodeArtifactEndpoints.MaximumArtifacts);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [IntegrationTest]
    [Endpoint("PUT /api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}")]
    public async Task Upload_AttemptChangesDuringCloudWrite_DoesNotPublishAndDeletesOwnGeneration()
    {
        _rotateDuringUpload = true;
        using var response = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1", new ByteArrayContent([1]));
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _objects.Should().BeEmpty();
        CustomCodeArtifactDescriptor.TryParse(_records["broker-job"].ArtifactReferences.Single(), out var receipt).Should().BeTrue();
        receipt!.State.Should().Be("pending");
    }

    [IntegrationTest]
    [Endpoint("PUT /api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}")]
    public async Task Upload_Journal_PublishesImmutableReceiptAndCallsIdempotentIngestor()
    {
        using var first = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/honua-events.v1.jsonl?attempt=1", new ByteArrayContent([10]));
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        using var retry = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/honua-events.v1.jsonl?attempt=1", new ByteArrayContent([20]));
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        _objects.Values.Single().Should().Equal([10]);
        await _ingestor.Received(2).IngestAsync(Arg.Any<ExecutionJobRecord>(),
            Arg.Is<CustomCodeArtifactDescriptor>(artifact => artifact.Name == HarnessEvents.FileName && artifact.Attempt == 1 && artifact.State == "available"),
            Arg.Any<CancellationToken>());
    }

    private static ExecutionJobRecord Job(string id, string token) => new()
    {
        OperationId = id, Status = ExecutionJobStatus.Running, Version = 1, AttemptCount = 1,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        Audit = new OperationAuditInfo { RequestedBy = "alice" },
        Spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing, TargetKind = BatchComputeTargetKind.AwsBatch, Backend = "aws-batch",
            WorkloadId = "custom-worker", WorkloadName = "custom-worker", RuntimeProfile = CustomCodeJobContract.RuntimeProfile,
            Parameters = new Dictionary<string, string> { [CustomCodeJobContract.JobTokenEnvParam] = token },
        },
    };
}
