// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Server.Features.Protocols.Zarr;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.Coverages.Multidimensional;

/// <summary>
/// Integration tests for the cloud-optimized HDF5 / NetCDF4 coverage admin
/// surface. Per ADR-0039 Path B, a metadata refresh dispatches a GDAL worker job
/// (202 + status URL); the status endpoint materializes the scanned metadata when
/// the job succeeds. The job substrate is replaced with in-memory recording
/// doubles so the async contract is exercised without Redis or the GDAL worker.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Cog)]
[Operation(Operations.CogAdmin)]
public class MultidimensionalCoverageEndpointTests : IAsyncLifetime
{
    private const string Route = "/api/v1/admin/multidim-coverages";

    private readonly RecordingJobStore _jobStore = new();
    private readonly RecordingJobQueue _jobQueue = new();
    private readonly InMemoryZarrStore _zarrStore = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public MultidimensionalCoverageEndpointTests()
    {
        _fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IExecutionJobStore>();
                services.RemoveAll<IJobQueue>();
                services.RemoveAll<IZarrStore>();
                services.AddSingleton<IExecutionJobStore>(_jobStore);
                services.AddSingleton<IJobQueue>(_jobQueue);
                services.AddSingleton<IZarrStore>(_zarrStore);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/multidim-coverages")]
    public async Task Register_WithMissingName_Returns400()
    {
        var request = new
        {
            layerId = 1,
            name = "",
            format = "NetCdf4",
            provider = "AwsS3",
            bucket = "bucket",
            objectKey = "granule.nc4",
            variables = Array.Empty<string>()
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/multidim-coverages", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/multidim-coverages")]
    public async Task Register_WithMismatchedExtension_Returns400()
    {
        var request = new
        {
            layerId = 1,
            name = "bad-ext",
            format = "NetCdf4",
            provider = "AwsS3",
            bucket = "bucket",
            objectKey = "granule.tif",
            variables = Array.Empty<string>()
        };

        var response = await _client.PostAsJsonAsync(Route, request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(".nc");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/multidim-coverages")]
    public async Task Register_WithPathTraversal_Returns400()
    {
        var request = new
        {
            layerId = 1,
            name = "traversal",
            format = "NetCdf4",
            provider = "AwsS3",
            bucket = "bucket",
            objectKey = "../../etc/passwd.nc",
            variables = Array.Empty<string>()
        };

        var response = await _client.PostAsJsonAsync(Route, request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/multidim-coverages")]
    public async Task Register_WithLocalProvider_Returns400()
    {
        var request = new
        {
            layerId = 1,
            name = "local",
            format = "NetCdf4",
            provider = "Local",
            bucket = "bucket",
            objectKey = "granule.nc4",
            variables = Array.Empty<string>()
        };

        var response = await _client.PostAsJsonAsync(Route, request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/multidim-coverages")]
    public async Task Register_WithMissingLayer_Returns404()
    {
        var request = new
        {
            layerId = 99999,
            name = "missing-layer",
            format = "NetCdf4",
            provider = "AwsS3",
            bucket = "bucket",
            objectKey = "granule.nc4",
            variables = Array.Empty<string>()
        };

        var response = await _client.PostAsJsonAsync(Route, request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/multidim-coverages")]
    public async Task List_WithoutLayerId_Returns400()
    {
        var response = await _client.GetAsync("/api/v1/admin/multidim-coverages");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/multidim-coverages/{id}")]
    public async Task Get_WithNonexistentId_Returns404()
    {
        var response = await _client.GetAsync("/api/v1/admin/multidim-coverages/99999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/multidim-coverages/{id}")]
    public async Task Delete_WithNonexistentId_Returns404()
    {
        var response = await _client.DeleteAsync("/api/v1/admin/multidim-coverages/99999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/multidim-coverages")]
    public async Task Crud_RegisterListGetDelete_Lifecycle()
    {
        var request = new
        {
            layerId = 1,
            name = "lifecycle-multidim",
            description = "integration test",
            format = "NetCdf4",
            provider = "AwsS3",
            bucket = "noaa-sst",
            objectKey = "ghrsst/2026/05/lifecycle.nc4",
            variables = new[] { "analysed_sst" }
        };

        var createResponse = await _client.PostAsJsonAsync(Route, request);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var createDoc = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var id = createDoc.RootElement.GetProperty("id").GetInt64();
        id.Should().BeGreaterThan(0);
        createDoc.RootElement.GetProperty("format").GetString().Should().Be("NetCdf4");
        createDoc.RootElement.GetProperty("status").GetString().Should().Be("reader-not-enabled");

        var listResponse = await _client.GetAsync($"{Route}?layerId=1");
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        listDoc.RootElement.EnumerateArray()
            .Should().Contain(e => e.GetProperty("id").GetInt64() == id);

        var getResponse = await _client.GetAsync($"{Route}/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var getDoc = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());
        getDoc.RootElement.GetProperty("objectKey").GetString().Should().Be("ghrsst/2026/05/lifecycle.nc4");
        getDoc.RootElement.GetProperty("variables").EnumerateArray()
            .Should().ContainSingle().Which.GetString().Should().Be("analysed_sst");

        var deleteResponse = await _client.DeleteAsync($"{Route}/{id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _client.GetAsync($"{Route}/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/multidim-coverages/{id}/refresh")]
    public async Task Refresh_DispatchesScanJob_Returns202()
    {
        var id = await RegisterAsync("refresh-dispatch", "ghrsst/refresh-dispatch.nc4");
        try
        {
            var refreshResponse = await _client.PostAsync($"{Route}/{id}/refresh", null);

            refreshResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
            using var doc = JsonDocument.Parse(await refreshResponse.Content.ReadAsStringAsync());
            var jobId = doc.RootElement.GetProperty("jobId").GetString();
            jobId.Should().NotBeNullOrEmpty();
            doc.RootElement.GetProperty("status").GetString().Should().Be("queued");
            doc.RootElement.GetProperty("statusUrl").GetString().Should().Be($"{Route}/jobs/{jobId}");

            _jobQueue.Enqueued.Should().Contain(jobId!);
        }
        finally
        {
            await _client.DeleteAsync($"{Route}/{id}");
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/multidim-coverages/{id}/refresh")]
    public async Task Refresh_WithNonexistentId_Returns404()
    {
        var response = await _client.PostAsync("/api/v1/admin/multidim-coverages/99999/refresh", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/multidim-coverages/jobs/{jobId}")]
    public async Task ScanJobStatus_WithNonexistentJob_Returns404()
    {
        // Use the full literal route here (not the $"{Route}/..." indirection used
        // elsewhere in this file) so the EndpointRegistry drift scanner, which matches
        // route templates against literal request strings in test source, recognizes
        // this GET as backing "GET /api/v1/admin/multidim-coverages/jobs/{jobId}".
        var response = await _client.GetAsync("/api/v1/admin/multidim-coverages/jobs/covscan-does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/multidim-coverages/jobs/{jobId}")]
    public async Task ScanJobStatus_SucceededJob_MaterializesMetadata()
    {
        var id = await RegisterAsync("scan-materialize", "ghrsst/scan-materialize.nc4");
        try
        {
            // Dispatch creates the job in the recording store (Queued).
            var refreshResponse = await _client.PostAsync($"{Route}/{id}/refresh", null);
            refreshResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
            using var refreshDoc = JsonDocument.Parse(await refreshResponse.Content.ReadAsStringAsync());
            var jobId = refreshDoc.RootElement.GetProperty("jobId").GetString()!;

            // Simulate the GDAL worker completing the job with a gdalmdiminfo artifact.
            var job = await _jobStore.GetAsync(jobId);
            job.Should().NotBeNull();
            var envelope = """{"mdiminfo":""" + GdalMdimInfoJson + ""","zarr":{"rootPath":"ghrsst/scan-materialize.zarr"}}""";
            var artifact = "data:application/json;base64," +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope));
            await _jobStore.SetAsync(job! with
            {
                Status = ExecutionJobStatus.Succeeded,
                CompletedAt = DateTimeOffset.UtcNow,
                ArtifactReferences = new[] { artifact },
            });

            var statusResponse = await _client.GetAsync($"{Route}/jobs/{jobId}");
            statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            using var statusDoc = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
            statusDoc.RootElement.GetProperty("status").GetString().Should().Be("succeeded");
            statusDoc.RootElement.GetProperty("coverage").GetProperty("variableCount").GetInt32().Should().Be(1);

            // The registration is now materialized for subsequent reads.
            using var getDoc = JsonDocument.Parse(await (await _client.GetAsync($"{Route}/{id}")).Content.ReadAsStringAsync());
            getDoc.RootElement.GetProperty("variableCount").GetInt32().Should().Be(1);

            // The derived Zarr store is registered as a Zarr coverage so pixel slices
            // resolve through the existing Zarr serving path.
            var zarrRegistrations = await _zarrStore.ListByLayerAsync(1);
            zarrRegistrations.Should().Contain(z => z.RootPath == "ghrsst/scan-materialize.zarr");
        }
        finally
        {
            await _client.DeleteAsync($"{Route}/{id}");
        }
    }

    private const string GdalMdimInfoJson =
        """{"type":"group","driver":"netCDF","name":"/","arrays":{"sst":{"datatype":"Float32","dimensions":["/time"],"dimension_size":[3],"block_size":[1],"unit":"degC"}}}""";

    private async Task<long> RegisterAsync(string name, string objectKey)
    {
        var request = new
        {
            layerId = 1,
            name,
            format = "NetCdf4",
            provider = "AwsS3",
            bucket = "noaa-sst",
            objectKey,
            variables = Array.Empty<string>()
        };

        var createResponse = await _client.PostAsJsonAsync(Route, request);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        using var createDoc = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        return createDoc.RootElement.GetProperty("id").GetInt64();
    }

    private sealed class RecordingJobStore : IExecutionJobStore
    {
        private readonly Dictionary<string, ExecutionJobRecord> _jobs = new(StringComparer.Ordinal);

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCreateAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            if (_jobs.ContainsKey(job.OperationId))
            {
                return Task.FromResult(false);
            }

            _jobs[job.OperationId] = job;
            return Task.FromResult(true);
        }

        public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_jobs.TryGetValue(operationId, out var job) ? job : null);

        public Task SetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _jobs[job.OperationId] = job;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _jobs[job.OperationId] = job;
            return Task.FromResult(true);
        }

        public Task<ExecutionJobPage> QueryAsync(ExecutionJobQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new ExecutionJobPage
            {
                Items = _jobs.Values
                    .Where(job => !query.Kind.HasValue || job.Spec.Kind == query.Kind.Value)
                    .OrderByDescending(job => job.CreatedAt)
                    .Take(query.Limit)
                    .ToArray(),
                NextCursor = null
            });

        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(ExecutionJobKind? kind = null, int? limit = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionJobRecord>>(
                _jobs.Values.Where(job => !kind.HasValue || job.Spec.Kind == kind.Value).ToArray());
    }

    private sealed class RecordingJobQueue : IJobQueue
    {
        public List<string> Enqueued { get; } = [];

        public Task EnqueueAsync(string operationId, OperationPriority priority = OperationPriority.Normal, CancellationToken cancellationToken = default)
        {
            Enqueued.Add(operationId);
            return Task.CompletedTask;
        }

        public Task<string?> TryClaimAsync(
            string workerId,
            IReadOnlySet<ExecutionJobKind>? acceptedKinds = null,
            IReadOnlySet<string>? acceptedRuntimeProfiles = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task RequeueAsync(string operationId, OperationPriority priority = OperationPriority.Normal, TimeSpan? visibleAfter = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<long> GetQueueDepthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<long>(0);
    }
}
