// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.SharedCaching;
using Honua.Server.Features.Admin.Exports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Honua.Server.Tests.Features.Admin.Exports;

internal sealed record RevocationScenario
{
    public bool AnonymousAllowed { get; init; }
    public bool MultiTenancyEnabled { get; init; }
    public bool IncludeParcelsPublication { get; init; } = true;
    public int GraphFailuresBeforeSuccess { get; init; }
    public CdnInvalidationResult[]? InvalidationOutcomes { get; init; }
    public IPublicExportStateStore? StateStoreOverride { get; init; }
    public ICloudFileStorage? PrivateStorage { get; init; }
}

internal sealed class RevocationHarness
{
    public required PublicExportRevocationService Service { get; init; }
    public required IPublicPublicationDestination Destination { get; init; }
    public required InMemoryPublicExportStateStore StateStore { get; init; }
    public required FakePublicStorage PublicStorage { get; init; }
    public required List<string[]> InvalidationRequests { get; init; }
    public required List<CdnInvalidationReceipt> PollReceipts { get; init; }
    public required List<(PublicExportRevocationRecord Record, string Detail)> FailureNotices { get; init; }

    public static RevocationHarness Create(RevocationScenario? scenario = null)
    {
        var opts = scenario ?? new RevocationScenario();
        var publicStorage = CreateSeededPublicStorage();
        var stateStore = CreateSeededStateStore();
        var invalidationRequests = new List<string[]>();
        var pollReceipts = new List<CdnInvalidationReceipt>();
        var failureNotices = new List<(PublicExportRevocationRecord, string)>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [PublicExportRevocationService.DistributionIdSetting] = "E123DIST" })
            .Build();

        var destination = new PublicPublicationDestination(publicStorage.Storage.Object, new Uri("https://cdn.example.test/"));
        var service = new PublicExportRevocationService(
            new PublicExportRevocationDependencies
            {
                StateStore = opts.StateStoreOverride ?? stateStore,
                Eligibility = CreateEligibility(opts),
                GraphProvider = CreateGraphProvider(opts).Object,
                CdnInvalidation = CreateCdnService(opts.InvalidationOutcomes, invalidationRequests),
                OpsNotifier = CreateNotifier(failureNotices).Object,
                Configuration = config,
                Destination = destination,
                StatusReader = CreateStatusReader(pollReceipts).Object,
                PrivateStorage = opts.PrivateStorage,
                PrivateStorageOptions = opts.PrivateStorage is null ? null : Options.Create(new CloudStorageOptions())
            },
            NullLogger<PublicExportRevocationService>.Instance);

        return new RevocationHarness
        {
            Service = service,
            Destination = destination,
            StateStore = stateStore,
            PublicStorage = publicStorage,
            InvalidationRequests = invalidationRequests,
            PollReceipts = pollReceipts,
            FailureNotices = failureNotices
        };
    }

    public PublicExportRevocationHostedService CreateHostedService(TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Destination);
        services.AddSingleton(Service);
        var provider = services.BuildServiceProvider();
        return new PublicExportRevocationHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PublicExportRevocationHostedService>.Instance,
            timeProvider);
    }

    private static FakePublicStorage CreateSeededPublicStorage()
    {
        var storage = new FakePublicStorage();
        storage.Objects["data/publications/parcels/v1/parcels.pmtiles"] = [1];
        storage.Objects["data/publications/parcels/v1/parcels.parquet"] = [2];
        storage.Objects["data/publications/parcels/v1/collection.json"] = [3];
        storage.Objects["data/publications/roads/v1/collection.json"] = [4];
        storage.Objects["data/catalog.json"] = "{}"u8.ToArray();
        return storage;
    }

    private static InMemoryPublicExportStateStore CreateSeededStateStore()
    {
        var store = new InMemoryPublicExportStateStore();
        store.Promoted["parcels"] = new PromotedExportRecord
        {
            PublicationId = "parcels",
            Version = "v1",
            ServiceId = "svc-parcels",
            ResourceId = "res-parcels",
            StorageLayerId = 7,
            Title = "Parcels",
            CollectionUrl = "https://cdn.example.test/data/publications/parcels/v1/collection.json",
            CatalogUrl = "https://cdn.example.test/data/catalog.json",
            PromotedAt = DateTimeOffset.UtcNow,
            CanaryState = PublicExportCanaryState.Readable
        };
        store.Promoted["roads"] = new PromotedExportRecord
        {
            PublicationId = "roads",
            Version = "v1",
            ServiceId = "svc-roads",
            ResourceId = "res-roads",
            StorageLayerId = 8,
            Title = "Roads",
            CollectionUrl = "https://cdn.example.test/data/publications/roads/v1/collection.json",
            CatalogUrl = "https://cdn.example.test/data/catalog.json",
            PromotedAt = DateTimeOffset.UtcNow,
            CanaryState = PublicExportCanaryState.Readable
        };
        return store;
    }

    private static PublicExportEligibility CreateEligibility(RevocationScenario opts)
    {
        var evaluator = new Mock<IAccessPolicyEvaluator>();
        evaluator.Setup(e => e.Evaluate(It.IsAny<ClaimsPrincipal>(), It.IsAny<AccessPolicy?>(), It.IsAny<AccessPolicy?>(), It.IsAny<object?>()))
            .Returns((ClaimsPrincipal _, AccessPolicy? layerPolicy, AccessPolicy? _, object? _) =>
                layerPolicy?.AllowAnonymous == false && !opts.AnonymousAllowed
                    ? AccessDecision.RequiresAuth("Restricted")
                    : AccessDecision.Allowed());

        var services = new ServiceCollection();
        services.AddSingleton(evaluator.Object);
        return new PublicExportEligibility(
            services.BuildServiceProvider(),
            Options.Create(new TenantIsolationOptions { Enabled = opts.MultiTenancyEnabled }),
            Options.Create(new TenantContextOptions { Enabled = opts.MultiTenancyEnabled }));
    }

    private static Mock<ICdnInvalidationStatusReader> CreateStatusReader(List<CdnInvalidationReceipt> pollReceipts)
    {
        var statusReader = new Mock<ICdnInvalidationStatusReader>();
        statusReader.Setup(r => r.ReadAsync(It.IsAny<CdnInvalidationReceipt>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CdnInvalidationReceipt receipt, CancellationToken _) =>
            {
                pollReceipts.Add(receipt);
                return new CdnInvalidationStatusResult(CdnInvalidationStatus.Completed);
            });
        return statusReader;
    }

    private static Mock<IPublicExportOpsNotifier> CreateNotifier(
        List<(PublicExportRevocationRecord Record, string Detail)> failureNotices)
    {
        var notifier = new Mock<IPublicExportOpsNotifier>();
        notifier.Setup(n => n.NotifyRevocationFailedAsync(
                It.IsAny<PublicExportRevocationRecord>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<PublicExportRevocationRecord, string, CancellationToken>((rec, detail, _) =>
                failureNotices.Add((rec, detail)))
            .Returns(Task.CompletedTask);
        return notifier;
    }

    private static Mock<IMetadataV2GraphProvider> CreateGraphProvider(RevocationScenario opts)
    {
        var provider = new Mock<IMetadataV2GraphProvider>();
        var snapshot = new MetadataV2GraphSnapshot(BuildGraph(opts), "etag-2", DateTimeOffset.UtcNow);
        var remainingFailures = opts.GraphFailuresBeforeSuccess;
        provider.Setup(g => g.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (remainingFailures > 0)
                {
                    remainingFailures--;
                    return ValueTask.FromException<MetadataV2GraphSnapshot>(new InvalidOperationException("Transient metadata error."));
                }

                return ValueTask.FromResult(snapshot);
            });
        return provider;
    }

    private static MetadataV2Graph BuildGraph(RevocationScenario opts)
    {
        var publications = new List<MetadataV2Layer>
        {
            new() { Metadata = new MetadataV2ObjectMetadata { Id = "pub-roads", Name = "roads" }, ServiceId = "svc-roads", ResourceId = "res-roads", LayerIndex = 1 }
        };
        if (opts.IncludeParcelsPublication)
        {
            publications.Add(new MetadataV2Layer { Metadata = new MetadataV2ObjectMetadata { Id = "pub-parcels", Name = "parcels" }, ServiceId = "svc-parcels", ResourceId = "res-parcels", LayerIndex = 0 });
        }

        return new MetadataV2Graph
        {
            Revision = 2,
            Services =
            [
                new MetadataV2Service { Metadata = new MetadataV2ObjectMetadata { Id = "svc-parcels", Name = "parcels-svc" } },
                new MetadataV2Service { Metadata = new MetadataV2ObjectMetadata { Id = "svc-roads", Name = "roads-svc" } }
            ],
            Resources =
            [
                new MetadataV2Object { Metadata = new MetadataV2ObjectMetadata { Id = "res-parcels", Name = "parcels" }, AccessPolicy = new AccessPolicy { AllowAnonymous = opts.AnonymousAllowed } },
                new MetadataV2Object { Metadata = new MetadataV2ObjectMetadata { Id = "res-roads", Name = "roads" }, AccessPolicy = new AccessPolicy { AllowAnonymous = true } }
            ],
            Publications = publications
        };
    }

    private static CdnInvalidationService CreateCdnService(
        CdnInvalidationResult[]? outcomes,
        List<string[]> requests)
    {
        var queue = new Queue<CdnInvalidationResult>(
            outcomes ?? [new CdnInvalidationResult(CdnInvalidationOutcome.Submitted, "INV-1")]);
        var invalidator = new Mock<ICdnInvalidator>();
        invalidator.Setup(i => i.InvalidateAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> paths, CancellationToken _) =>
            {
                requests.Add([.. paths]);
                return queue.Count > 0 ? queue.Dequeue() : new CdnInvalidationResult(CdnInvalidationOutcome.Submitted, "INV-NEXT");
            });
        return new CdnInvalidationService(invalidator.Object, NullLogger<CdnInvalidationService>.Instance);
    }
}
