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
    private int _failUploads;
    private bool _loseUploadAcknowledgement;

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
                if (_failUploads > 0)
                {
                    _failUploads--;
                    return UploadResult.CreateFailure("Transient storage failure");
                }
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
                if (_loseUploadAcknowledgement)
                {
                    return UploadResult.CreateFailure("Upload acknowledgement lost");
                }
                return UploadResult.CreateSuccess(new CloudFile
                {
                    Provider = CloudStorageProvider.AwsS3,
                    FileId = request.ObjectKeyOverride!,
                    FileName = request.FileName,
                    StoragePath = request.ObjectKeyOverride!,
                    ContentType = request.ContentType,
                    SizeBytes = bytes.Length,
                    UploadedAt = DateTimeOffset.UtcNow,
                    ETag = "generation-1",
                });
            });
        _storage.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<CloudFile?>(_objects.TryGetValue(call.ArgAt<string>(0), out var bytes)
                ? new CloudFile
                {
                    Provider = CloudStorageProvider.AwsS3,
                    FileId = call.ArgAt<string>(0),
                    FileName = "result.json",
                    StoragePath = call.ArgAt<string>(0),
                    ContentType = "application/octet-stream",
                    SizeBytes = bytes.Length,
                    UploadedAt = DateTimeOffset.UtcNow
                }
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
            DateTimeOffset.UtcNow.AddHours(1))
        { Attempt = 1 }, CancellationToken.None);
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

        using var retry = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1", new ByteArrayContent(bytes));
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        _objects.Should().ContainSingle();

        using var overwrite = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1", new ByteArrayContent([0]));
        overwrite.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _objects.Values.Single().Should().Equal(bytes);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}")]
    [Endpoint("GET /api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}")]
    public async Task Download_ChangedCloudBytesOrProvider_FailsBeforeServingAnyArtifactBytes()
    {
        using var upload = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1", new ByteArrayContent([1, 2, 3]));
        upload.StatusCode.Should().Be(HttpStatusCode.Created);
        _objects["gp/custom-artifacts/broker-job/a1/result.json"] = [4, 5, 6];
        using var changed = await _admin.GetAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1");
        changed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await changed.Content.ReadAsByteArrayAsync()).Should().NotEqual([4, 5, 6]);
        _storage.Provider.Returns(CloudStorageProvider.AzureBlob);
        using var provider = await _admin.GetAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1");
        provider.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}")]
    [Endpoint("GET /api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}")]
    public async Task Upload_RejectsCrossJobRevokedAndStaleAttemptCredentials()
    {
        using var crossJob = await _worker.PutAsync("/api/geoprocessing/jobs/other-job/custom-artifacts/result.json?attempt=1", new ByteArrayContent([1]));
        crossJob.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var wrongAttempt = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=2", new ByteArrayContent([1]));
        wrongAttempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _records["broker-job"] = _records["broker-job"] with
        {
            Spec = _records["broker-job"].Spec with
            {
                Parameters = new Dictionary<string, string> { [CustomCodeJobContract.JobTokenEnvParam] = "rotated-token" },
            }
        };
        using var rotated = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1", new ByteArrayContent([1]));
        rotated.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await _issuer.RevokeAsync(_token, CancellationToken.None);
        using var revoked = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1", new ByteArrayContent([1]));
        revoked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var anonymous = _fixture.CreateClient();
        using var forbiddenRead = await anonymous.GetAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1");
        forbiddenRead.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var callbackRead = await _worker.GetAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1");
        callbackRead.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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
            responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.Conflict || response.StatusCode == HttpStatusCode.RequestEntityTooLarge);
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
    public async Task Upload_AggregateBudgetIncludesFailedReservations()
    {
        _records["broker-job"] = _records["broker-job"] with
        {
            ArtifactReferences = Enumerable.Range(0, 4).Select(index => new CustomCodeArtifactDescriptor
            {
                JobId = "broker-job",
                Attempt = 1,
                Name = $"interrupted{index}.bin",
                ObjectKey = $"gp/custom-artifacts/broker-job/a1/interrupted{index}.bin",
                ContentType = "application/octet-stream",
                SizeBytes = CustomCodeArtifactEndpoints.MaximumArtifactBytes,
            }.Serialize()).ToArray(),
        };
        using var response = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1", new ByteArrayContent([1]));
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        _objects.Should().BeEmpty();
        _records["broker-job"].ArtifactReferences.Should().HaveCount(4);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}")]
    public async Task Upload_UserOutputsCannotConsumeReservedJournalCountOrBytes()
    {
        foreach (var fillByteBudget in new[] { false, true })
        {
            var sizes = fillByteBudget
                ? new[] { 64L << 20, 64L << 20, 64L << 20, 63L << 20 }
                : Enumerable.Repeat(1L, 31).ToArray();
            _records["broker-job"] = _records["broker-job"] with
            {
                ArtifactReferences = sizes.Select((size, index) => new CustomCodeArtifactDescriptor
                {
                    JobId = "broker-job",
                    Attempt = 1,
                    Name = $"output{index}.bin",
                    ObjectKey = $"gp/custom-artifacts/broker-job/a1/output{index}.bin",
                    ContentType = "application/octet-stream",
                    SizeBytes = size,
                    Provider = CloudStorageProvider.AwsS3,
                }.Serialize()).ToArray(),
            };
            _objects.Clear();
            using var rejected = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/extra.bin?attempt=1", new ByteArrayContent([1]));
            rejected.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
            using var journal = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/honua-events.v1.jsonl?attempt=1",
                new ByteArrayContent(new byte[CustomCodeArtifactEndpoints.MaximumJournalBytes]));
            journal.StatusCode.Should().Be(HttpStatusCode.Created);
            _records["broker-job"].ArtifactReferences.Count.Should().BeLessThanOrEqualTo(CustomCodeArtifactEndpoints.MaximumArtifacts);
        }
    }

    [IntegrationTest]
    [Endpoint("PUT /api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}")]
    public async Task Upload_RetryRetainsPriorChargesAndReservesCurrentAttemptJournal()
    {
        var prior = Enumerable.Range(0, 31).Select(index => new CustomCodeArtifactDescriptor
        {
            JobId = "broker-job",
            Attempt = 1,
            Name = index == 30 ? "honua-events.v1.jsonl" : $"prior{index}.bin",
            ObjectKey = $"gp/custom-artifacts/broker-job/a1/prior{index}.bin",
            ContentType = "application/octet-stream",
            SizeBytes = 1,
            Provider = CloudStorageProvider.AwsS3,
        }.Serialize()).ToArray();
        var previous = _records["broker-job"] with { ArtifactReferences = prior };
        var guard = new CustomCodeArtifactRetryGuard();
        var admitted = guard.EvaluateRetry(previous)!;
        admitted.Allowed.Should().BeTrue();
        admitted.ArtifactReferences.Should().Equal(prior);
        var token = await _issuer.IssueAsync(new ScopedJobTokenRequest("alice", null, [], [], "broker-job", [],
            DateTimeOffset.UtcNow.AddHours(1)) { Attempt = 2 }, CancellationToken.None);
        _records["broker-job"] = previous with
        {
            AttemptCount = 2,
            ArtifactReferences = admitted.ArtifactReferences,
            Spec = previous.Spec with { Parameters = new Dictionary<string, string> { [CustomCodeJobContract.JobTokenEnvParam] = token.Token } },
        };
        _worker.DefaultRequestHeaders.Remove("X-Api-Key");
        _worker.DefaultRequestHeaders.Add("X-Api-Key", token.Token);
        using var output = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/output.bin?attempt=2", new ByteArrayContent([1]));
        output.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        using var journal = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/honua-events.v1.jsonl?attempt=2", new ByteArrayContent([10]));
        journal.StatusCode.Should().Be(HttpStatusCode.Created);
        var refused = guard.EvaluateRetry(_records["broker-job"])!;
        refused.Allowed.Should().BeFalse();
        refused.RefusalReason.Should().Contain("mandatory journal");
        _records["broker-job"].ArtifactReferences.Should().HaveCount(32);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}")]
    public async Task Upload_InterruptedJournal_RecoversSameReservationWithinRetryLimit()
    {
        _failUploads = 1;
        using var interrupted = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/honua-events.v1.jsonl?attempt=1", new ByteArrayContent([10]));
        interrupted.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var recovered = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/honua-events.v1.jsonl?attempt=1", new ByteArrayContent([10]));
        recovered.StatusCode.Should().Be(HttpStatusCode.Created);
        CustomCodeArtifactDescriptor.TryParse(_records["broker-job"].ArtifactReferences.Single(), out var receipt).Should().BeTrue();
        receipt!.UploadAttempts.Should().Be(2);
        receipt.SizeBytes.Should().Be(1);
        receipt.State.Should().Be("available");
        _objects.Should().ContainSingle();
        await _ingestor.Received(1).IngestAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CustomCodeArtifactDescriptor>(), Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Endpoint("PUT /api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}")]
    public async Task Upload_RepeatedStorageFailures_KeepCapacityAndBoundRecoveryAttempts()
    {
        _failUploads = 4;
        for (var index = 0; index < 3; index++)
        {
            using var interrupted = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1", new ByteArrayContent([10]));
            interrupted.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }

        using var exhausted = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1", new ByteArrayContent([10]));
        exhausted.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _failUploads.Should().Be(1);
        CustomCodeArtifactDescriptor.TryParse(_records["broker-job"].ArtifactReferences.Single(), out var receipt).Should().BeTrue();
        receipt!.UploadAttempts.Should().Be(3);
        receipt.SizeBytes.Should().Be(1);
        receipt.State.Should().Be("pending");
        _objects.Should().BeEmpty();
    }

    [IntegrationTest]
    [Endpoint("PUT /api/geoprocessing/jobs/{jobId}/custom-artifacts/{name}")]
    public async Task Upload_LostCloudAcknowledgement_RecoversOnlyMatchingImmutableBytes()
    {
        _loseUploadAcknowledgement = true;
        using var recovered = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/result.json?attempt=1", new ByteArrayContent([10]));
        recovered.StatusCode.Should().Be(HttpStatusCode.Created);
        _objects["gp/custom-artifacts/broker-job/a1/conflict.json"] = [20];
        using var conflict = await _worker.PutAsync("/api/geoprocessing/jobs/broker-job/custom-artifacts/conflict.json?attempt=1", new ByteArrayContent([10]));
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _objects["gp/custom-artifacts/broker-job/a1/conflict.json"].Should().Equal([20]);
        var receipts = _records["broker-job"].ArtifactReferences.Select(reference =>
        {
            CustomCodeArtifactDescriptor.TryParse(reference, out var receipt).Should().BeTrue();
            return receipt!;
        }).ToArray();
        receipts.Single(receipt => receipt.Name == "result.json").State.Should().Be("available");
        receipts.Single(receipt => receipt.Name == "conflict.json").State.Should().Be("pending");
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
        OperationId = id,
        Status = ExecutionJobStatus.Running,
        Version = 1,
        AttemptCount = 1,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Audit = new OperationAuditInfo { RequestedBy = "alice" },
        Spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.AwsBatch,
            Backend = "aws-batch",
            WorkloadId = "custom-worker",
            WorkloadName = "custom-worker",
            RuntimeProfile = CustomCodeJobContract.RuntimeProfile,
            Parameters = new Dictionary<string, string> { [CustomCodeJobContract.JobTokenEnvParam] = token },
        },
    };
}
