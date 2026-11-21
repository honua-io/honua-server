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
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy;
using Honua.Core.Features.Publishing.Descriptors;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Admin.PairedPublications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace Honua.Server.Tests.Features.Admin.PairedPublications;

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
}

internal sealed class FakePublicStorage
{
    private bool _failedOnce;
    private readonly string? _failOnKeyOnce;

    public FakePublicStorage(string? failOnKeyOnce = null)
    {
        _failOnKeyOnce = failOnKeyOnce;
        Storage = BuildMock();
    }

    public Mock<ICloudFileStorage> Storage { get; }
    public List<(string Key, StoredObjectCachePolicy CachePolicy)> Writes { get; } = [];
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ImmutableDictionary<string, string>> Metadata { get; } = new(StringComparer.Ordinal);

    private Mock<ICloudFileStorage> BuildMock()
    {
        var mock = new Mock<ICloudFileStorage>();
        mock.Setup(s => s.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => CreateCloudFileOrNull(key));
        mock.Setup(s => s.ListFilesAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? prefix, int _, bool _, CancellationToken _) => ListByPrefix(prefix));
        mock.Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => Objects.Remove(key));
        mock.Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => Objects.ContainsKey(key));
        mock.Setup(s => s.UploadAsync(It.IsAny<FileUploadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileUploadRequest request, CancellationToken _) => HandleUpload(request));
        return mock;
    }

    private CloudFile? CreateCloudFileOrNull(string key)
        => Objects.TryGetValue(key, out var bytes)
            ? new CloudFile
            {
                FileId = key, FileName = Path.GetFileName(key), StoragePath = key,
                ContentType = "application/octet-stream", SizeBytes = bytes.Length,
                UploadedAt = DateTimeOffset.UtcNow, Provider = CloudStorageProvider.AwsS3,
                Metadata = Metadata.TryGetValue(key, out var meta) ? meta : ImmutableDictionary<string, string>.Empty
            }
            : null;

    private CloudFile[] ListByPrefix(string? prefix)
        => [.. Objects.Keys
            .Where(k => prefix is null || k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => CreateCloudFileOrNull(k)!)];

    private UploadResult HandleUpload(FileUploadRequest request)
    {
        var key = request.ObjectKeyOverride!;
        if (key == _failOnKeyOnce && !_failedOnce)
        {
            _failedOnce = true;
            return UploadResult.CreateFailure("Simulated crash before pointer.");
        }

        using var copy = new MemoryStream();
        request.Content.CopyTo(copy);
        Objects[key] = copy.ToArray();
        Metadata[key] = request.Metadata;
        Writes.Add((key, request.CachePolicy));
        return UploadResult.CreateSuccess(CreateCloudFileOrNull(key)!, TimeSpan.Zero);
    }
}

internal sealed class InMemoryPublicExportStateStore : IPublicExportStateStore
{
    public Dictionary<string, PromotedExportRecord> Promoted { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, PublicExportRevocationRecord> Revocations { get; } = new(StringComparer.Ordinal);

    public Task SavePromotedAsync(PromotedExportRecord record, CancellationToken cancellationToken)
    {
        Promoted[record.PublicationId] = record;
        return Task.CompletedTask;
    }

    public Task<PromotedExportRecord?> GetPromotedAsync(string publicationId, CancellationToken cancellationToken)
        => Task.FromResult(Promoted.GetValueOrDefault(publicationId));

    public Task DeletePromotedAsync(string publicationId, CancellationToken cancellationToken)
    {
        Promoted.Remove(publicationId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PromotedExportRecord>> ListPromotedAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<PromotedExportRecord>>([.. Promoted.Values]);

    public Task SaveRevocationAsync(PublicExportRevocationRecord record, CancellationToken cancellationToken)
    {
        Revocations[record.RevocationId] = record;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PublicExportRevocationRecord>> ListPendingRevocationsAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<PublicExportRevocationRecord>>(
            [.. Revocations.Values.Where(r => r.State != PublicExportRevocationState.Completed)]);
}

internal sealed class PromotionHarness
{
    public required PublicExportPromoter Promoter { get; init; }
    public required PublicExportPromotionInput Input { get; init; }
    public required InMemoryPublicExportStateStore StateStore { get; init; }
    public required FakePublicStorage PublicStorage { get; init; }
    public required List<PublicExportCanaryFailure> CanaryNotices { get; init; }

    public static PromotionHarness Create(PromotionScenario? scenario = null)
    {
        var opts = scenario ?? new PromotionScenario();
        var (privateStorage, descriptor) = CreatePrivateFixture();
        var publicStorage = new FakePublicStorage(opts.FailOnPublicKeyOnce);
        var stateStore = new InMemoryPublicExportStateStore();
        var canaryNotices = new List<PublicExportCanaryFailure>();

        var canary = new Mock<IPublicExportCanary>();
        canary.Setup(c => c.CheckAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(opts.CanaryResult ?? PublicExportCanaryResult.Readable);
        var notifier = new Mock<IPublicExportOpsNotifier>();
        notifier.Setup(n => n.NotifyCanaryFailedAsync(It.IsAny<PublicExportCanaryFailure>(), It.IsAny<CancellationToken>()))
            .Callback<PublicExportCanaryFailure, CancellationToken>((failure, _) => canaryNotices.Add(failure))
            .Returns(Task.CompletedTask);

        IPublicPublicationDestination? destination = opts.DestinationConfigured
            ? new PublicPublicationDestination(publicStorage.Storage.Object, new Uri("https://cdn.example.test/"))
            : null;
        var deps = new PublicExportPromoterDependencies
        {
            PrivateStorage = privateStorage.Object, Eligibility = CreateEligibility(opts),
            StateStore = stateStore, Canary = canary.Object, OpsNotifier = notifier.Object
        };

        return new PromotionHarness
        {
            Promoter = new PublicExportPromoter(deps, destination),
            Input = BuildInput(opts, privateStorage.Object, descriptor),
            StateStore = stateStore, PublicStorage = publicStorage, CanaryNotices = canaryNotices
        };
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

    private static (Mock<ICloudFileStorage> Storage, PublicationDescriptor Descriptor) CreatePrivateFixture()
    {
        var pmtilesBytes = Encoding.UTF8.GetBytes("pmtiles-bytes");
        var parquetBytes = Encoding.UTF8.GetBytes("parquet-bytes");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["publications/parcels/v1/parcels.pmtiles"] = pmtilesBytes,
            ["publications/parcels/v1/parcels.parquet"] = parquetBytes,
            ["publications/parcels/v1/README.md"] = Encoding.UTF8.GetBytes("# Parcels"),
            ["publications/parcels/v1/collection.json"] = Encoding.UTF8.GetBytes("{\"type\":\"Collection\"}")
        };

        var storage = new Mock<ICloudFileStorage>();
        storage.Setup(s => s.GetMetadataAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => files.TryGetValue(key, out var bytes)
                ? new CloudFile
                {
                    FileId = key, FileName = Path.GetFileName(key), StoragePath = key,
                    ContentType = "application/octet-stream", SizeBytes = bytes.Length,
                    UploadedAt = DateTimeOffset.UtcNow, Provider = CloudStorageProvider.AwsS3
                }
                : null);
        storage.Setup(s => s.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string key, CancellationToken _) => files.TryGetValue(key, out var bytes)
                ? new MemoryStream(bytes, writable: false)
                : null);

        return (storage, CreateDescriptor(pmtilesBytes, parquetBytes));
    }

    private static PublicationDescriptor CreateDescriptor(byte[] pmtilesBytes, byte[] parquetBytes) => new()
    {
        PublicationId = "parcels",
        Version = "v1",
        Title = "Parcels",
        PublishedAt = DateTimeOffset.UtcNow,
        SourceLayer = new PublicationSourceLayer("svc-parcels", "res-parcels"),
        Capture = new PublicationCapture("cap-1", DateTimeOffset.UtcNow, "rev-1", 10),
        Bounds = [-122, 37, -121, 38],
        Crs = new PublicationCrs("EPSG:4326", "OGC:CRS84"),
        Fields = [new PublicationField("objectid", "integer", true)],
        Identity = new PublicationIdentity("objectid", "objectid", "objectid"),
        Geometry = new PublicationGeometry("geom", "WKB", ["Polygon"]),
        Artifacts = new PublicationArtifacts(
            new PmtilesPublicationArtifact
            {
                Href = "parcels.pmtiles", Bytes = pmtilesBytes.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(pmtilesBytes)),
                CaptureId = "cap-1", Fields = ["objectid"], VectorLayer = "layer", Minzoom = 0, Maxzoom = 10
            },
            new GeoparquetPublicationArtifact
            {
                Href = "parcels.parquet", Bytes = parquetBytes.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(parquetBytes)),
                CaptureId = "cap-1", Fields = ["objectid"]
            }),
        Validation = new PublicationValidation(PublicationValidation.Passed, DateTimeOffset.UtcNow, [])
    };

    private static PublicExportPromotionInput BuildInput(
        PromotionScenario opts,
        ICloudFileStorage privateStorage,
        PublicationDescriptor descriptor)
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
        var store = PairedPublicationStore.Create(
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
