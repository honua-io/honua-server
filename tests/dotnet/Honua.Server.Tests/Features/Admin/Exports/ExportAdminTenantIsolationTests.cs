// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Exports;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.Exports;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin.Exports;

[Trait("Tier", "Fast")]
[SecurityTest]
public sealed class ExportAdminTenantIsolationTests
{
    private const string ExportA = "alpha-export";
    private const string ExportB = "beta-export";
    private const string ExportVersionId = "20261113T062701Z";

    private readonly record struct CallerIdentity(string TenantId, string Role, TenantContextSource Source);

    private readonly record struct SeedExportSpec(string ExportId, string ServiceId, string ResourceId);

    private readonly record struct CompletedExportJobSpec(string JobId, string ExportId, string ServiceId);

    [UnitTest]
    public async Task ListAsync_TenantAdmin_ReturnsOnlyOwnTenantExportsAndPaginatesCleanly()
    {
        var fixture = await ExportTenantHarness.CreateAsync(
            new CallerIdentity(TwoTenantGraphFixture.TenantB, "admin", TenantContextSource.Claim));

        var result = await fixture.ListAsync();

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        var payload = ((IValueHttpResult)result).Value.Should().BeOfType<ApiResponse<ExportListResponse>>().Subject;
        payload.Data!.Items.Select(item => item.Id).Should().Equal(ExportB);
    }

    [UnitTest]
    public async Task GetAsync_TenantAdmin_AllowsOwnExportAndHidesForeignTenantExport()
    {
        var fixture = await ExportTenantHarness.CreateAsync(
            new CallerIdentity(TwoTenantGraphFixture.TenantA, "admin", TenantContextSource.Claim));

        var own = await fixture.GetAsync(ExportA);
        var foreign = await fixture.GetAsync(ExportB);

        StatusOf(own).Should().Be(StatusCodes.Status200OK);
        StatusOf(foreign).Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    public async Task CreateAsync_TenantAdmin_BlocksForeignLayerAndForeignExportIdCollision()
    {
        var fixture = await ExportTenantHarness.CreateAsync(
            new CallerIdentity(TwoTenantGraphFixture.TenantA, "admin", TenantContextSource.Claim));

        var foreignLayer = await fixture.CreateAsync(new ExportCreateRequest
        {
            Source = new ExportSourceRequest { Kind = "layer", ServiceId = TwoTenantGraphFixture.ServiceB, LayerId = 0 },
            ExportId = "stolen-export",
        });
        var collidingId = await fixture.CreateAsync(new ExportCreateRequest
        {
            Source = new ExportSourceRequest { Kind = "layer", ServiceId = TwoTenantGraphFixture.ServiceA, LayerId = 0 },
            ExportId = ExportB,
        });
        var ownLayer = await fixture.CreateAsync(new ExportCreateRequest
        {
            Source = new ExportSourceRequest { Kind = "layer", ServiceId = TwoTenantGraphFixture.ServiceA, LayerId = 0 },
            ExportId = "tenant-a-new-export",
        });

        StatusOf(foreignLayer).Should().Be(StatusCodes.Status404NotFound);
        StatusOf(collidingId).Should().Be(StatusCodes.Status409Conflict);
        StatusOf(ownLayer).Should().Be(StatusCodes.Status202Accepted);
        await fixture.TileJobs.Received(1).SubmitAsync(
            Arg.Is<TileOperationSubmission>(submission =>
                submission.TenantId == TwoTenantGraphFixture.TenantA &&
                submission.Request.ExportId == "tenant-a-new-export"),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task DeleteAsync_TenantAdmin_RefusesForeignExportAndUnpublishesOwnExport()
    {
        var fixture = await ExportTenantHarness.CreateAsync(
            new CallerIdentity(TwoTenantGraphFixture.TenantA, "admin", TenantContextSource.Claim));
        var pointerKeyB = ExportStore.Create(fixture.Storage.Storage.Object, fixture.StorageOptions, ExportB).PointerKey;
        var pointerKeyA = ExportStore.Create(fixture.Storage.Storage.Object, fixture.StorageOptions, ExportA).PointerKey;

        var foreignDelete = await fixture.DeleteAsync(ExportB);
        var ownDelete = await fixture.DeleteAsync(ExportA);

        StatusOf(foreignDelete).Should().Be(StatusCodes.Status404NotFound);
        fixture.Storage.Objects.Should().ContainKey(pointerKeyB);
        StatusOf(ownDelete).Should().Be(StatusCodes.Status200OK);
        fixture.Storage.Objects.Should().NotContainKey(pointerKeyA);
        var payload = ((IValueHttpResult)ownDelete).Value.Should().BeOfType<ApiResponse<ExportResponse>>().Subject;
        payload.Data!.Status.Should().Be(ExportResponse.Unpublished);
    }

    [UnitTest]
    public async Task AllOperations_PlatformAdmin_SucceedAcrossTenants()
        => await AssertCrossTenantOperationsSucceedAsync(
            new CallerIdentity(TwoTenantGraphFixture.TenantA, "platform_admin", TenantContextSource.Claim));

    [UnitTest]
    public async Task AllOperations_SingleTenantAdmin_SucceedAcrossTenants()
        => await AssertCrossTenantOperationsSucceedAsync(
            new CallerIdentity(TwoTenantGraphFixture.TenantA, "admin", TenantContextSource.Default));

    private static async Task AssertCrossTenantOperationsSucceedAsync(CallerIdentity caller)
    {
        var fixture = await ExportTenantHarness.CreateAsync(caller);

        var list = await fixture.ListAsync();
        var get = await fixture.GetAsync(ExportB);
        var create = await fixture.CreateAsync(new ExportCreateRequest
        {
            Source = new ExportSourceRequest { Kind = "layer", ServiceId = TwoTenantGraphFixture.ServiceB, LayerId = 0 },
            ExportId = "cross-tenant-export",
        });
        var delete = await fixture.DeleteAsync(ExportB);

        var listed = ((IValueHttpResult)list).Value.Should().BeOfType<ApiResponse<ExportListResponse>>().Subject;
        listed.Data!.Items.Select(item => item.Id).Should().Equal(ExportA, ExportB);
        StatusOf(get).Should().Be(StatusCodes.Status200OK);
        StatusOf(create).Should().Be(StatusCodes.Status202Accepted);
        StatusOf(delete).Should().Be(StatusCodes.Status200OK);
    }

    private static int StatusOf(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode ?? 200;

    private sealed class ExportTenantHarness
    {
        public required DefaultHttpContext HttpContext { get; init; }

        public required FakePublicStorage Storage { get; init; }

        public required CloudStorageOptions StorageOptions { get; init; }

        public required ITileCacheJobService TileJobs { get; init; }

        private sealed record ExportHarnessStores
        {
            public required FakePublicStorage Storage { get; init; }

            public required CloudStorageOptions StorageOptions { get; init; }

            public required InMemoryExecutionJobStore JobStore { get; init; }

            public required ITileCacheJobService TileJobs { get; init; }
        }

        public static async Task<ExportTenantHarness> CreateAsync(CallerIdentity caller)
        {
            var storageOptions = new CloudStorageOptions();
            var stores = new ExportHarnessStores
            {
                Storage = await CreateSeededStorageAsync(storageOptions),
                StorageOptions = storageOptions,
                JobStore = await CreateSeededJobStoreAsync(),
                TileJobs = CreateTileJobsMock(),
            };
            var services = BuildServices(caller, stores);
            var context = new DefaultHttpContext
            {
                RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "operator"), new Claim(ClaimTypes.Role, caller.Role)],
                    "Test")),
            };
            return new ExportTenantHarness
            {
                HttpContext = context,
                Storage = stores.Storage,
                StorageOptions = stores.StorageOptions,
                TileJobs = stores.TileJobs,
            };
        }

        public Task<IResult> ListAsync()
            => InvokeAsync("ListAsync", [HttpContext, CancellationToken.None]);

        public Task<IResult> GetAsync(string exportId)
            => InvokeAsync("GetAsync", [exportId, HttpContext, CancellationToken.None]);

        public Task<IResult> CreateAsync(ExportCreateRequest request)
            => InvokeAsync("CreateAsync", [request, HttpContext, CancellationToken.None]);

        public Task<IResult> DeleteAsync(string exportId)
            => InvokeAsync("DeleteAsync", [exportId, HttpContext, CancellationToken.None]);

        private static async Task<FakePublicStorage> CreateSeededStorageAsync(CloudStorageOptions storageOptions)
        {
            var storage = new FakePublicStorage();
            storage.Storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) =>
                    storage.Objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes, writable: false) : null);

            await SeedExportAsync(storage, storageOptions,
                new SeedExportSpec(ExportA, TwoTenantGraphFixture.ServiceA, TwoTenantGraphFixture.ResourceA));
            await SeedExportAsync(storage, storageOptions,
                new SeedExportSpec(ExportB, TwoTenantGraphFixture.ServiceB, TwoTenantGraphFixture.ResourceB));
            return storage;
        }

        private static async Task<InMemoryExecutionJobStore> CreateSeededJobStoreAsync()
        {
            var jobStore = new InMemoryExecutionJobStore();
            await jobStore.TryCreateAsync(CreateJob("job-a", TwoTenantGraphFixture.TenantA));
            await jobStore.TryCreateAsync(CreateJob("job-b", TwoTenantGraphFixture.TenantB));
            return jobStore;
        }

        private static ITileCacheJobService CreateTileJobsMock()
        {
            var tileJobs = Substitute.For<ITileCacheJobService>();
            tileJobs.ListAsync(Arg.Any<TileOperationListScope>(), Arg.Any<CancellationToken>())
                .Returns([
                    CompletedExportJob(new CompletedExportJobSpec("job-a", ExportA, TwoTenantGraphFixture.ServiceA)),
                    CompletedExportJob(new CompletedExportJobSpec("job-b", ExportB, TwoTenantGraphFixture.ServiceB))
                ]);
            tileJobs.SubmitAsync(Arg.Any<TileOperationSubmission>(), Arg.Any<CancellationToken>())
                .Returns("export-job-new");
            return tileJobs;
        }

        private static ServiceProvider BuildServices(CallerIdentity caller, ExportHarnessStores stores)
        {
            var graphProvider = Substitute.For<IMetadataV2GraphProvider>();
            graphProvider.GetCurrentAsync(Arg.Any<CancellationToken>())
                .Returns(TwoTenantGraphFixture.BuildSnapshot("\"export-tenant-isolation\""));
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns(caller.TenantId);
            tenant.Source.Returns(caller.Source);
            var catalog = new ExportCatalog(new ExportCatalogDependencies
            {
                Storage = stores.Storage.Storage.Object,
                Options = Options.Create(stores.StorageOptions),
                Jobs = stores.TileJobs,
            });
            return new ServiceCollection()
                .AddLogging()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
                .AddSingleton<IAccessPolicyEvaluator>(new AccessPolicyEvaluator())
                .AddSingleton(graphProvider)
                .AddSingleton(catalog)
                .AddSingleton(stores.TileJobs)
                .AddSingleton<IExecutionJobStore>(stores.JobStore)
                .AddSingleton(tenant)
                .AddSingleton(Options.Create(new LimitsOptions()))
                .AddSingleton(Options.Create(new TenantContextOptions
                {
                    Enabled = caller.Source != TenantContextSource.Default,
                    DefaultTenantId = "public",
                }))
                .BuildServiceProvider();
        }

        private static Task<IResult> InvokeAsync(string methodName, object?[] args)
        {
            var method = typeof(ExportAdminEndpoints).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
            return (Task<IResult>)method.Invoke(null, args)!;
        }

        private static async Task SeedExportAsync(
            FakePublicStorage storage,
            CloudStorageOptions options,
            SeedExportSpec spec)
        {
            var store = ExportStore.Create(storage.Storage.Object, options, spec.ExportId);
            var pointer = new ExportPointer(spec.ExportId, ExportVersionId, $"{ExportVersionId}/collection.json", DateTimeOffset.UtcNow);
            storage.Objects[store.PointerKey] = JsonSerializer.SerializeToUtf8Bytes(pointer, ExportVersionJsonContext.Default.ExportPointer);

            var template = await File.ReadAllTextAsync(RepositoryPaths.Resolve(
                "docs", "gis", "schemas", "export-collection.v1", "maui-county-parcels.json"));
            var node = JsonNode.Parse(template)!.AsObject();
            node["id"] = spec.ExportId;
            node["honua:source"]!["service"] = spec.ServiceId;
            node["honua:source"]!["layer"] = spec.ResourceId;
            storage.Objects[store.VersionKey(ExportVersionId, ExportCollectionDocument.FileName)] =
                Encoding.UTF8.GetBytes(node.ToJsonString());
        }

        private static ExecutionJobRecord CreateJob(string jobId, string tenantId) => new()
        {
            OperationId = jobId,
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Audit = new OperationAuditInfo { TenantId = tenantId },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.TileCache,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "test",
                WorkloadName = "export-tenant-test",
            },
        };

        private static TileOperationProgress CompletedExportJob(CompletedExportJobSpec spec)
            => TileOperationProgress.CreateInitial(spec.JobId, TileCacheExecutionSpecBuilder.ExportOperation, spec.ServiceId, 0, "WebMercatorQuad") with
            {
                ExportId = spec.ExportId,
                Status = OperationStatus.Completed,
            };
    }
}
