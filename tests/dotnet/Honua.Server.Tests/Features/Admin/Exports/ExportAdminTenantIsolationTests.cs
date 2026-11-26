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
using Honua.Core.Features.Metadata.Domain.V2;
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
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string ServiceA = "svc-a";
    private const string ServiceB = "svc-b";
    private const string ResourceA = "res-a";
    private const string ResourceB = "res-b";
    private const string ExportA = "alpha-export";
    private const string ExportB = "beta-export";
    private const string ExportVersionId = "20261113T062701Z";

    [UnitTest]
    public async Task ListAsync_TenantAdmin_ReturnsOnlyOwnTenantExportsAndPaginatesCleanly()
    {
        var fixture = await ExportTenantHarness.CreateAsync(TenantB, "admin", multiTenancyEnabled: true);

        var result = await fixture.ListAsync();

        StatusOf(result).Should().Be(StatusCodes.Status200OK);
        var payload = ((IValueHttpResult)result).Value.Should().BeOfType<ApiResponse<ExportListResponse>>().Subject;
        payload.Data!.Items.Select(item => item.Id).Should().Equal(ExportB);
    }

    [UnitTest]
    public async Task GetAsync_TenantAdmin_AllowsOwnExportAndHidesForeignTenantExport()
    {
        var fixture = await ExportTenantHarness.CreateAsync(TenantA, "admin", multiTenancyEnabled: true);

        var own = await fixture.GetAsync(ExportA);
        var foreign = await fixture.GetAsync(ExportB);

        StatusOf(own).Should().Be(StatusCodes.Status200OK);
        StatusOf(foreign).Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    public async Task CreateAsync_TenantAdmin_BlocksForeignLayerAndForeignExportIdCollision()
    {
        var fixture = await ExportTenantHarness.CreateAsync(TenantA, "admin", multiTenancyEnabled: true);

        var foreignLayer = await fixture.CreateAsync(new ExportCreateRequest
        {
            Source = new ExportSourceRequest { Kind = "layer", ServiceId = ServiceB, LayerId = 0 },
            ExportId = "stolen-export",
        });
        var collidingId = await fixture.CreateAsync(new ExportCreateRequest
        {
            Source = new ExportSourceRequest { Kind = "layer", ServiceId = ServiceA, LayerId = 0 },
            ExportId = ExportB,
        });
        var ownLayer = await fixture.CreateAsync(new ExportCreateRequest
        {
            Source = new ExportSourceRequest { Kind = "layer", ServiceId = ServiceA, LayerId = 0 },
            ExportId = "tenant-a-new-export",
        });

        StatusOf(foreignLayer).Should().Be(StatusCodes.Status404NotFound);
        StatusOf(collidingId).Should().Be(StatusCodes.Status409Conflict);
        StatusOf(ownLayer).Should().Be(StatusCodes.Status202Accepted);
        await fixture.TileJobs.Received(1).SubmitAsync(
            Arg.Is<TileOperationSubmission>(submission => submission.TenantId == TenantA && submission.Request.ExportId == "tenant-a-new-export"),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task DeleteAsync_TenantAdmin_RefusesForeignExportAndUnpublishesOwnExport()
    {
        var fixture = await ExportTenantHarness.CreateAsync(TenantA, "admin", multiTenancyEnabled: true);
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

    [UnitTheory]
    [InlineData("platform_admin", true)]
    [InlineData("admin", false)]
    public async Task AllOperations_PlatformAdminAndSingleTenantAdmin_SucceedAcrossTenants(
        string role,
        bool multiTenancyEnabled)
    {
        var fixture = await ExportTenantHarness.CreateAsync(TenantA, role, multiTenancyEnabled);

        var list = await fixture.ListAsync();
        var get = await fixture.GetAsync(ExportB);
        var create = await fixture.CreateAsync(new ExportCreateRequest
        {
            Source = new ExportSourceRequest { Kind = "layer", ServiceId = ServiceB, LayerId = 0 },
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

        public static async Task<ExportTenantHarness> CreateAsync(
            string callerTenant,
            string role,
            bool multiTenancyEnabled)
        {
            var storageOptions = new CloudStorageOptions();
            var storage = new FakePublicStorage();
            storage.Storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) =>
                    storage.Objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes, writable: false) : null);

            await SeedExportAsync(storage, storageOptions, ExportA, ServiceA, ResourceA);
            await SeedExportAsync(storage, storageOptions, ExportB, ServiceB, ResourceB);

            var jobStore = new InMemoryExecutionJobStore();
            await jobStore.TryCreateAsync(CreateJob("job-a", TenantA));
            await jobStore.TryCreateAsync(CreateJob("job-b", TenantB));

            var tileJobs = Substitute.For<ITileCacheJobService>();
            tileJobs.ListAsync(Arg.Any<TileOperationListScope>(), Arg.Any<CancellationToken>())
                .Returns([CompletedExportJob("job-a", ExportA, ServiceA, 0), CompletedExportJob("job-b", ExportB, ServiceB, 0)]);
            tileJobs.SubmitAsync(Arg.Any<TileOperationSubmission>(), Arg.Any<CancellationToken>())
                .Returns("export-job-new");

            var graphProvider = Substitute.For<IMetadataV2GraphProvider>();
            graphProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(BuildTwoTenantSnapshot());
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns(callerTenant);
            tenant.Source.Returns(multiTenancyEnabled ? TenantContextSource.Claim : TenantContextSource.Default);

            var catalog = new ExportCatalog(new ExportCatalogDependencies
            {
                Storage = storage.Storage.Object,
                Options = Options.Create(storageOptions),
                Jobs = tileJobs,
            });

            var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
                .AddSingleton<IAccessPolicyEvaluator>(new AccessPolicyEvaluator())
                .AddSingleton(graphProvider)
                .AddSingleton(catalog)
                .AddSingleton(tileJobs)
                .AddSingleton<IExecutionJobStore>(jobStore)
                .AddSingleton(tenant)
                .AddSingleton(Options.Create(new LimitsOptions()))
                .AddSingleton(Options.Create(new TenantContextOptions
                {
                    Enabled = multiTenancyEnabled,
                    DefaultTenantId = "public",
                }))
                .BuildServiceProvider();

            var context = new DefaultHttpContext
            {
                RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "operator"), new Claim(ClaimTypes.Role, role)],
                    "Test")),
            };
            return new ExportTenantHarness
            {
                HttpContext = context,
                Storage = storage,
                StorageOptions = storageOptions,
                TileJobs = tileJobs,
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

        private static Task<IResult> InvokeAsync(string methodName, object?[] args)
        {
            var method = typeof(ExportAdminEndpoints).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
            return (Task<IResult>)method.Invoke(null, args)!;
        }

        private static async Task SeedExportAsync(
            FakePublicStorage storage,
            CloudStorageOptions options,
            string exportId,
            string serviceId,
            string resourceId)
        {
            var store = ExportStore.Create(storage.Storage.Object, options, exportId);
            var pointer = new ExportPointer(exportId, ExportVersionId, $"{ExportVersionId}/collection.json", DateTimeOffset.UtcNow);
            storage.Objects[store.PointerKey] = JsonSerializer.SerializeToUtf8Bytes(pointer, ExportVersionJsonContext.Default.ExportPointer);

            var template = await File.ReadAllTextAsync(RepositoryPaths.Resolve(
                "docs", "gis", "schemas", "export-collection.v1", "maui-county-parcels.json"));
            var node = JsonNode.Parse(template)!.AsObject();
            node["id"] = exportId;
            node["honua:source"]!["service"] = serviceId;
            node["honua:source"]!["layer"] = resourceId;
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

        private static TileOperationProgress CompletedExportJob(string jobId, string exportId, string serviceId, int layerId)
            => TileOperationProgress.CreateInitial(jobId, TileCacheExecutionSpecBuilder.ExportOperation, serviceId, layerId, "WebMercatorQuad") with
            {
                ExportId = exportId,
                Status = OperationStatus.Completed,
            };

        private static MetadataV2GraphSnapshot BuildTwoTenantSnapshot()
        {
            var serviceA = CreateService(ServiceA, TenantA);
            var serviceB = CreateService(ServiceB, TenantB);
            var resourceA = CreateResource(ResourceA, "bind-a", TenantA);
            var resourceB = CreateResource(ResourceB, "bind-b", TenantB);
            var bindingA = CreateBinding("bind-a", ResourceA, 101, TenantA);
            var bindingB = CreateBinding("bind-b", ResourceB, 202, TenantB);
            var pubA = CreatePublication("pub-a", ServiceA, ResourceA, "bind-a", TenantA);
            var pubB = CreatePublication("pub-b", ServiceB, ResourceB, "bind-b", TenantB);

            return new MetadataV2GraphSnapshot(
                new MetadataV2Graph
                {
                    Revision = 1,
                    Services = [serviceA, serviceB],
                    Resources = [resourceA, resourceB],
                    StorageBindings = [bindingA, bindingB],
                    Publications = [pubA, pubB],
                },
                "\"export-tenant-isolation\"",
                DateTimeOffset.UnixEpoch);
        }

        private static MetadataV2Service CreateService(string id, string tenant) => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id, Tenant = tenant },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };

        private static MetadataV2Object CreateResource(string id, string bindingId, string tenant) => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id, Tenant = tenant },
            StorageBindingIds = [bindingId],
            PrimaryStorageBindingId = bindingId,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };

        private static MetadataV2StorageBinding CreateBinding(string id, string resourceId, int storageLayerId, string tenant) => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id, Tenant = tenant },
            ResourceId = resourceId,
            StorageLayerId = storageLayerId,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };

        private static MetadataV2Layer CreatePublication(
            string id, string serviceId, string resourceId, string bindingId, string tenant) => new()
            {
                Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id, Tenant = tenant },
                ServiceId = serviceId,
                ResourceId = resourceId,
                StorageBindingId = bindingId,
                LayerIndex = 0,
                Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            };
    }
}
