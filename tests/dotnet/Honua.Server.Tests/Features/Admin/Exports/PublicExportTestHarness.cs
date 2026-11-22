// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy;
using Honua.Core.Features.Exports;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.SharedCaching;
using Honua.Server.Features.Admin.Exports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Honua.Server.Tests.Features.Admin.Exports;

internal sealed record PromotionScenario
{
    public bool DestinationConfigured { get; init; } = true;
    public bool MultiTenancyEnabled { get; init; }
    public bool AnonymousAllowed { get; init; } = true;
    public string? PermanentFilter { get; init; }
    public SqlFragment? RlsFilter { get; init; }
    public bool HasHiddenField { get; init; }
    public string[]? MaskedFields { get; init; }
    public string? FailOnPublicKeyOnce { get; init; }
    public PublicExportCanaryResult? CanaryResult { get; init; }
    public MetadataV2GraphSnapshot[]? CurrentSnapshots { get; init; }
}

internal sealed class PromotionHarness
{
    public required PublicExportPromoter Promoter { get; init; }
    public IPublicPublicationDestination? Destination { get; init; }
    public required PublicExportPromotionInput Input { get; init; }
    public required InMemoryPublicExportStateStore StateStore { get; init; }
    public required FakePublicStorage PublicStorage { get; init; }
    public required List<PublicExportCanaryFailure> CanaryNotices { get; init; }
    public required List<string[]> InvalidationRequests { get; init; }

    public static PromotionHarness Create(PromotionScenario? scenario = null)
    {
        var opts = scenario ?? new PromotionScenario();
        var (privateStorage, descriptor) = CreatePrivateFixture();
        var publicStorage = new FakePublicStorage(opts.FailOnPublicKeyOnce);
        var stateStore = new InMemoryPublicExportStateStore();
        var (canary, notifier, canaryNotices) = CreateCanaryAndNotifier(opts);

        IPublicPublicationDestination? destination = opts.DestinationConfigured
            ? new PublicPublicationDestination(publicStorage.Storage.Object, new Uri("https://cdn.example.test/"))
            : null;
        var input = BuildInput(opts, privateStorage.Object, descriptor);
        var eligibility = CreateEligibility(opts);
        var graphProvider = CreateGraphProvider(input, opts.CurrentSnapshots);
        var (revocation, invalidationRequests) = CreateRevocationService(stateStore, eligibility, graphProvider, destination, notifier);
        var deps = new PublicExportPromoterDependencies
        {
            PrivateStorage = privateStorage.Object,
            Eligibility = eligibility,
            GraphProvider = graphProvider,
            Revocation = revocation,
            StateStore = stateStore,
            Canary = canary,
            OpsNotifier = notifier
        };

        return new PromotionHarness
        {
            Promoter = new PublicExportPromoter(deps, destination),
            Destination = destination,
            Input = input,
            StateStore = stateStore,
            PublicStorage = publicStorage,
            CanaryNotices = canaryNotices,
            InvalidationRequests = invalidationRequests
        };
    }

    private static (IPublicExportCanary Canary, IPublicExportOpsNotifier Notifier, List<PublicExportCanaryFailure> Notices)
        CreateCanaryAndNotifier(PromotionScenario opts)
    {
        var notices = new List<PublicExportCanaryFailure>();
        var canary = new Mock<IPublicExportCanary>();
        canary.Setup(c => c.CheckAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(opts.CanaryResult ?? PublicExportCanaryResult.Readable);
        var notifier = new Mock<IPublicExportOpsNotifier>();
        notifier.Setup(n => n.NotifyCanaryFailedAsync(It.IsAny<PublicExportCanaryFailure>(), It.IsAny<CancellationToken>()))
            .Callback<PublicExportCanaryFailure, CancellationToken>((failure, _) => notices.Add(failure))
            .Returns(Task.CompletedTask);
        return (canary.Object, notifier.Object, notices);
    }

    public static MetadataV2GraphSnapshot CreateSnapshot(MetadataV2Object resource, MetadataV2Service service)
    {
        var graph = new MetadataV2Graph
        {
            Revision = 12,
            Services = [service],
            Resources = [resource],
            Publications =
            [
                new MetadataV2Layer
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub-parcels", Name = "parcels" },
                    ServiceId = service.Metadata.Id,
                    ResourceId = resource.Metadata.Id,
                    LayerIndex = 0
                }
            ]
        };
        return new MetadataV2GraphSnapshot(graph, "etag-12", DateTimeOffset.UtcNow);
    }

    private static IMetadataV2GraphProvider CreateGraphProvider(
        PublicExportPromotionInput input,
        MetadataV2GraphSnapshot[]? snapshots)
    {
        var fallback = CreateSnapshot(input.Resource, input.Service);
        var queue = new Queue<MetadataV2GraphSnapshot>(snapshots ?? [fallback]);
        var last = snapshots is { Length: > 0 } ? snapshots[^1] : fallback;
        var provider = new Mock<IMetadataV2GraphProvider>();
        provider.Setup(g => g.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => queue.Count > 0 ? queue.Dequeue() : last);
        return provider.Object;
    }

    private static (PublicExportRevocationService Service, List<string[]> Requests) CreateRevocationService(
        IPublicExportStateStore stateStore,
        PublicExportEligibility eligibility,
        IMetadataV2GraphProvider graphProvider,
        IPublicPublicationDestination? destination,
        IPublicExportOpsNotifier notifier)
    {
        var invalidationRequests = new List<string[]>();
        var invalidator = new Mock<ICdnInvalidator>();
        invalidator.Setup(i => i.InvalidateAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> paths, CancellationToken _) =>
            {
                invalidationRequests.Add([.. paths]);
                return new CdnInvalidationResult(CdnInvalidationOutcome.Submitted, "INV-LATE");
            });
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [PublicExportRevocationService.DistributionIdSetting] = "E123DIST" })
            .Build();
        var statusReader = new Mock<ICdnInvalidationStatusReader>();
        statusReader.Setup(r => r.ReadAsync(It.IsAny<CdnInvalidationReceipt>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CdnInvalidationStatusResult(CdnInvalidationStatus.Completed));
        var service = new PublicExportRevocationService(
            new PublicExportRevocationDependencies
            {
                StateStore = stateStore,
                Eligibility = eligibility,
                GraphProvider = graphProvider,
                CdnInvalidation = new CdnInvalidationService(invalidator.Object, NullLogger<CdnInvalidationService>.Instance),
                OpsNotifier = notifier,
                Configuration = config,
                Destination = destination,
                StatusReader = statusReader.Object
            },
            NullLogger<PublicExportRevocationService>.Instance);
        return (service, invalidationRequests);
    }

    private static PublicExportEligibility CreateEligibility(PromotionScenario opts)
    {
        var evaluator = new Mock<IAccessPolicyEvaluator>();
        evaluator.Setup(e => e.Evaluate(It.IsAny<ClaimsPrincipal>(), It.IsAny<AccessPolicy?>(), It.IsAny<AccessPolicy?>(), It.IsAny<object?>()))
            .Returns(opts.AnonymousAllowed ? AccessDecision.Allowed() : AccessDecision.RequiresAuth("Authentication required."));

        var rlsSource = new Mock<IRowLevelSecurityFilterSource>();
        rlsSource.Setup(s => s.ResolveAsync(It.IsAny<MetadataV2Object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(opts.RlsFilter);

        var fieldMaskSource = new Mock<IFieldMaskSource>();
        fieldMaskSource.Setup(s => s.ResolveAsync(It.IsAny<MetadataV2Object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(opts.MaskedFields is null ? ImmutableArray<string>.Empty : [.. opts.MaskedFields]);

        var services = new ServiceCollection();
        services.AddSingleton(evaluator.Object);
        services.AddSingleton(rlsSource.Object);
        services.AddSingleton(fieldMaskSource.Object);
        return new PublicExportEligibility(
            services.BuildServiceProvider(),
            Options.Create(new TenantIsolationOptions { Enabled = opts.MultiTenancyEnabled }),
            Options.Create(new TenantContextOptions { Enabled = opts.MultiTenancyEnabled }));
    }

    private static (Mock<ICloudFileStorage> Storage, ExportVersion Descriptor) CreatePrivateFixture()
    {
        var pmtilesBytes = Encoding.UTF8.GetBytes("pmtiles-bytes");
        var parquetBytes = Encoding.UTF8.GetBytes("parquet-bytes");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["exports/parcels/v1/parcels.pmtiles"] = pmtilesBytes,
            ["exports/parcels/v1/parcels.parquet"] = parquetBytes,
            ["exports/parcels/v1/README.md"] = Encoding.UTF8.GetBytes("# Parcels"),
            ["exports/parcels/v1/collection.json"] = Encoding.UTF8.GetBytes("{\"type\":\"Collection\"}")
        };

        var storage = new Mock<ICloudFileStorage>();
        storage.Setup(s => s.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => files.TryGetValue(key, out var bytes)
                ? new CloudFile
                {
                    FileId = key,
                    FileName = Path.GetFileName(key),
                    StoragePath = key,
                    ContentType = "application/octet-stream",
                    SizeBytes = bytes.Length,
                    UploadedAt = DateTimeOffset.UtcNow,
                    Provider = CloudStorageProvider.AwsS3
                }
                : null);
        storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => files.TryGetValue(key, out var bytes)
                ? new MemoryStream(bytes, writable: false)
                : null);

        return (storage, CreateDescriptor(pmtilesBytes, parquetBytes));
    }

    private static ExportVersion CreateDescriptor(byte[] pmtilesBytes, byte[] parquetBytes) => new()
    {
        ExportId = "parcels",
        Version = "v1",
        Title = "Parcels",
        PublishedAt = DateTimeOffset.UtcNow,
        SourceLayer = new ExportSourceLayer("svc-parcels", "res-parcels"),
        Capture = new ExportCapture("cap-1", DateTimeOffset.UtcNow, "rev-1", 10),
        Bounds = [-122, 37, -121, 38],
        Crs = new ExportCrs("EPSG:4326", "OGC:CRS84"),
        Fields = [new ExportVersionField("objectid", "integer", true)],
        Identity = new ExportIdentity("objectid", "objectid", "objectid"),
        Geometry = new ExportGeometry("geom", "WKB", ["Polygon"]),
        Artifacts = new ExportArtifacts(
            new PmtilesExportArtifact
            {
                Href = "parcels.pmtiles",
                Bytes = pmtilesBytes.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(pmtilesBytes)),
                CaptureId = "cap-1",
                Fields = ["objectid"],
                VectorLayer = "layer",
                Minzoom = 0,
                Maxzoom = 10
            },
            new GeoparquetExportArtifact
            {
                Href = "parcels.parquet",
                Bytes = parquetBytes.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(parquetBytes)),
                CaptureId = "cap-1",
                Fields = ["objectid"]
            }),
        Validation = new ExportValidation(ExportValidation.Passed, DateTimeOffset.UtcNow, [])
    };

    private static PublicExportPromotionInput BuildInput(
        PromotionScenario opts,
        ICloudFileStorage privateStorage,
        ExportVersion descriptor)
    {
        var resource = new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-parcels", Name = "parcels", Title = "Parcels" },
            PermanentFilter = opts.PermanentFilter is null ? null : new MetadataV2PermanentFilter { Expression = opts.PermanentFilter },
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Hidden = false },
                new MetadataV2Field { Name = "owner", Type = MetadataV2FieldType.String, Hidden = opts.HasHiddenField }
            ]
        };
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-parcels", Name = "parcels-svc" }
        };
        var store = ExportStore.Create(
            privateStorage,
            new CloudStorageOptions { PMTilesPublish = new PMTilesPublishOptions { KeyPrefix = string.Empty } },
            "parcels");
        return new PublicExportPromotionInput
        {
            Descriptor = descriptor,
            PrivateStore = store,
            Resource = resource,
            Service = service,
            StorageLayerId = 7,
            LayerIndex = 0,
            MetadataRevision = 12
        };
    }
}
