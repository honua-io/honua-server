// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Honua.Core.Models;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Transport.Clients;
using Honua.Core.Transport.Converters;
using Honua.Mobile.Sdk;
using Honua.Mobile.Sdk.Clients;
using Honua.Mobile.Sdk.Storage;
using NetTopologySuite.Geometries;
using Xunit;
using DomainFeature = Honua.Core.Features.FeatureStore.Domain.Feature;
using CoreEnvelope = Honua.Core.Models.Envelope;

namespace Honua.Mobile.Sdk.Tests;

/// <summary>
/// Integration tests for mobile SDK with real domain models from Honua.Core.Sdk.
/// </summary>
public class MobileIntegrationTests : IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly OfflineDbContext _dbContext;
    private readonly Mock<IFeatureServiceClient<object>> _mockCoreClient;

    public MobileIntegrationTests()
    {
        // Setup test services
        var services = new ServiceCollection();

        // Configure logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // Configure mobile client options
        services.Configure<HonuaMobileClientOptions>(options =>
        {
            options.ServerAddress = "http://localhost:5000";
            options.OfflineDatabase = ":memory:"; // Use in-memory database for tests
            options.MobilePageSize = 100;
            options.RequestTimeout = TimeSpan.FromSeconds(30);
        });

        // Setup in-memory database for testing
        services.AddDbContext<OfflineDbContext>(options =>
        {
            options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString());
            options.ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        });

        // Register connectivity service mock
        var mockConnectivityService = new Mock<IConnectivityService>();
        mockConnectivityService.Setup(x => x.IsConnectionAvailableAsync(It.IsAny<NetworkPolicy>()))
            .ReturnsAsync(true);
        mockConnectivityService.Setup(x => x.IsBatteryLevelSufficientAsync(It.IsAny<BatteryPolicy>()))
            .ReturnsAsync(true);
        mockConnectivityService.Setup(x => x.GetNetworkConnectionTypeAsync())
            .ReturnsAsync(NetworkConnectionType.WiFi);
        mockConnectivityService.Setup(x => x.GetBatteryLevelAsync())
            .ReturnsAsync(100.0);

        services.AddSingleton(mockConnectivityService.Object);

        // Register offline storage service
        services.AddScoped<IOfflineStorageService, SqliteOfflineStorageService>();

        // Mock core client
        _mockCoreClient = new Mock<IFeatureServiceClient<object>>();
        services.AddSingleton(_mockCoreClient.Object);

        // Register mobile client adapter
        services.AddScoped<IFeatureServiceClient<MobileContext>>(provider =>
        {
            var coreClient = provider.GetRequiredService<IFeatureServiceClient<object>>();
            var options = provider.GetRequiredService<IOptions<HonuaMobileClientOptions>>();
            var logger = provider.GetRequiredService<ILogger<MobileFeatureServiceClient>>();

            return new MobileFeatureServiceClient(coreClient, options, logger);
        });

        // Register main mobile client
        services.AddScoped<HonuaMobileClient>();

        _serviceProvider = services.BuildServiceProvider();
        _dbContext = _serviceProvider.GetRequiredService<OfflineDbContext>();

        // Ensure database is created
        _dbContext.Database.EnsureCreated();
    }

    [Fact]
    public async Task QueryFeaturesAsync_WithRealDomainModels_ShouldReturnFeatures()
    {
        // Arrange
        var serviceId = "test-service";
        var layerId = 1;

        var query = new FeatureQuery
        {
            Where = "1=1",
            ResultRecordCount = 10,
            OutFields = ImmutableArray.Create("Name", "Type")
        };

        var geometryFactory = new GeometryFactory();
        var point = geometryFactory.CreatePoint(new Coordinate(-122.4194, 37.7749)); // San Francisco

        var mockFeatures = new[]
        {
            CreateFeature(
                1,
                point,
                new Dictionary<string, object?>
                {
                    ["Name"] = "Test Feature 1",
                    ["Type"] = "Point",
                }),
            CreateFeature(
                2,
                point,
                new Dictionary<string, object?>
                {
                    ["Name"] = "Test Feature 2",
                    ["Type"] = "Point",
                }),
        };

        var mockResult = QueryResult<DomainFeature>.Create(mockFeatures.Length, mockFeatures.ToImmutableArray());

        _mockCoreClient.Setup(x => x.QueryFeaturesAsync(
                serviceId, layerId, It.IsAny<FeatureQuery>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResult);

        var mobileClient = _serviceProvider.GetRequiredService<HonuaMobileClient>();
        var context = new MobileContext
        {
            AllowOffline = true,
            NetworkPolicy = NetworkPolicy.WifiOrCellular
        };

        // Act
        var result = await mobileClient.QueryFeaturesAsync(serviceId, layerId, query, context);

        // Assert
        result.Should().NotBeNull();
        result.Features.Should().HaveCount(2);
        result.Features[0].Id.Should().Be(1);
        result.Features[0].Attributes["Name"].Should().Be("Test Feature 1");
        result.Features[0].Geometry.Should().NotBeNull();
        GeometryConverter.FromWkb(result.Features[0].Geometry!).GeometryType.Should().Be("Point");
    }

    [Fact]
    public async Task CacheFeaturesAsync_WithRealDomainModels_ShouldStoreAndRetrieve()
    {
        // Arrange
        var serviceId = "test-service";
        var layerId = 1;

        var geometryFactory = new GeometryFactory();
        var polygon = geometryFactory.CreatePolygon(new[]
        {
            new Coordinate(-122.5, 37.7),
            new Coordinate(-122.4, 37.7),
            new Coordinate(-122.4, 37.8),
            new Coordinate(-122.5, 37.8),
            new Coordinate(-122.5, 37.7)
        });

        var features = new[]
        {
            CreateFeature(
                100,
                polygon,
                new Dictionary<string, object?>
                {
                    ["Name"] = "Cached Feature",
                    ["Area"] = 1234.56,
                    ["Active"] = true,
                }),
        }.ToImmutableArray();

        var storageService = _serviceProvider.GetRequiredService<IOfflineStorageService>();

        // Act - Cache the features
        await storageService.CacheFeaturesAsync(serviceId, layerId, features);

        // Act - Query the cached features
        var query = new FeatureQuery { Where = "1=1" };
        var result = await storageService.QueryFeaturesAsync(serviceId, layerId, query);

        // Assert
        result.Should().NotBeNull();
        result.Features.Should().HaveCount(1);

        var cachedFeature = result.Features[0];
        cachedFeature.Id.Should().Be(100);
        ((JsonElement)cachedFeature.Attributes["Name"]!).GetString().Should().Be("Cached Feature");
        ((JsonElement)cachedFeature.Attributes["Area"]!).GetDouble().Should().BeApproximately(1234.56, 0.001);
        ((JsonElement)cachedFeature.Attributes["Active"]!).GetBoolean().Should().BeTrue();
        cachedFeature.Geometry.Should().NotBeNull();
        GeometryConverter.FromWkb(cachedFeature.Geometry!).GeometryType.Should().Be("Polygon");
    }

    [Fact]
    public async Task QueryFeaturesStreamAsync_WithMobileOptimizations_ShouldStreamPages()
    {
        // Arrange
        var serviceId = "test-service";
        var layerId = 1;

        var query = new FeatureQuery
        {
            ResultRecordCount = 2 // Small page size for testing
        };

        var geometryFactory = new GeometryFactory();
        var point = geometryFactory.CreatePoint(new Coordinate(-122.4194, 37.7749));

        // Mock multiple pages
        var allFeatures = Enumerable.Range(1, 5)
            .Select(i => CreateFeature(
                i,
                point,
                new Dictionary<string, object?>
                {
                    ["Id"] = i,
                    ["Name"] = $"Feature {i}",
                }))
            .ToArray();

        // Setup mock to return pages
        var pageQueue = new Queue<FeaturePage>();
        pageQueue.Enqueue(new FeaturePage
        {
            Features = allFeatures.Take(2).ToImmutableArray(),
            IsLastPage = false,
            PageNumber = 0
        });
        pageQueue.Enqueue(new FeaturePage
        {
            Features = allFeatures.Skip(2).Take(2).ToImmutableArray(),
            IsLastPage = false,
            PageNumber = 1
        });
        pageQueue.Enqueue(new FeaturePage
        {
            Features = allFeatures.Skip(4).Take(1).ToImmutableArray(),
            IsLastPage = true,
            PageNumber = 2
        });

        _mockCoreClient.Setup(x => x.QueryFeaturesStreamAsync(
                serviceId, layerId, It.IsAny<FeatureQuery>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(MockAsyncEnumerable(pageQueue));

        var mobileClient = _serviceProvider.GetRequiredService<HonuaMobileClient>();
        var context = new MobileContext
        {
            AllowOffline = false, // Force network query
            NetworkPolicy = NetworkPolicy.WifiOrCellular
        };

        // Act
        var pages = new List<FeaturePage>();
        await foreach (var page in mobileClient.QueryFeaturesStreamAsync(serviceId, layerId, query, context))
        {
            pages.Add(page);
        }

        // Assert
        pages.Should().HaveCount(3);
        pages[0].Features.Should().HaveCount(2);
        pages[1].Features.Should().HaveCount(2);
        pages[2].Features.Should().HaveCount(1);
        pages[2].IsLastPage.Should().BeTrue();

        var totalFeatures = pages.SelectMany(p => p.Features).ToList();
        totalFeatures.Should().HaveCount(5);
        totalFeatures.Select(f => f.Id).Should().BeEquivalentTo(new[] { 1L, 2L, 3L, 4L, 5L });
    }

    [Fact]
    public async Task ApplyEditsAsync_WithRealDomainModels_ShouldQueueForSync()
    {
        // Arrange
        var serviceId = "test-service";
        var layerId = 1;

        var geometryFactory = new GeometryFactory();
        var point = geometryFactory.CreatePoint(new Coordinate(-122.4194, 37.7749));

        var newFeature = CreateFeature(
            0,
            point,
            new Dictionary<string, object?>
            {
                ["Name"] = "New Feature",
                ["Category"] = "Test",
            });

        var updatedFeature = CreateFeature(
            100,
            point,
            new Dictionary<string, object?>
            {
                ["Name"] = "Updated Feature",
                ["Category"] = "Modified",
            });

        var edits = new FeatureEdits
        {
            Adds = ImmutableArray.Create(newFeature),
            Updates = ImmutableArray.Create(updatedFeature),
            Deletes = ImmutableArray.Create(99L)
        };

        var mobileClient = _serviceProvider.GetRequiredService<HonuaMobileClient>();
        var context = new MobileContext
        {
            NetworkPolicy = NetworkPolicy.Offline // Force offline mode
        };

        // Act
        var result = await mobileClient.ApplyEditsAsync(serviceId, layerId, edits, context);

        // Assert
        result.Should().NotBeNull();
        result.AddResults.Should().HaveCount(1);
        result.UpdateResults.Should().HaveCount(1);
        result.DeleteResults.Should().HaveCount(1);

        result.AddResults[0].IsSuccess.Should().BeTrue();
        result.UpdateResults[0].IsSuccess.Should().BeTrue();
        result.DeleteResults[0].IsSuccess.Should().BeTrue();

        // Verify edits were queued in database
        var pendingEdits = await _dbContext.PendingEdits.ToListAsync();
        pendingEdits.Should().HaveCount(3);
        pendingEdits.Should().Contain(pe => pe.OperationType == "Add");
        pendingEdits.Should().Contain(pe => pe.OperationType == "Update");
        pendingEdits.Should().Contain(pe => pe.OperationType == "Delete");
    }

    [Fact]
    public async Task ApplyEditsAsync_WhenImmediateSyncSucceeds_ShouldMarkQueuedEditsAsSynced()
    {
        var serviceId = "test-service";
        var layerId = 1;
        var edits = CreateSampleEdits();

        _mockCoreClient.Setup(x => x.ApplyEditsAsync(
                serviceId,
                layerId,
                It.IsAny<FeatureEdits>(),
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EditResult
            {
                AddResults = [new OperationResult { ObjectId = 501, Success = true }],
                UpdateResults = [new OperationResult { ObjectId = 100, Success = true }],
                DeleteResults = [new OperationResult { ObjectId = 99, Success = true }]
            });

        var mobileClient = _serviceProvider.GetRequiredService<HonuaMobileClient>();
        var context = new MobileContext
        {
            AllowOffline = true,
            NetworkPolicy = NetworkPolicy.WifiOrCellular
        };

        var result = await mobileClient.ApplyEditsAsync(serviceId, layerId, edits, context);

        result.AddResults[0].ObjectId.Should().Be(501);
        (await _dbContext.PendingEdits.Where(pe => !pe.IsSynced).CountAsync()).Should().Be(0);
        (await _dbContext.PendingEdits.CountAsync(pe => pe.IsSynced)).Should().Be(3);
    }

    [Fact]
    public async Task SyncPendingEditsAsync_WithQueuedEdits_ShouldReplayUnsyncedOperations()
    {
        var serviceId = "test-service";
        var layerId = 1;
        var edits = CreateSampleEdits();
        var mobileClient = _serviceProvider.GetRequiredService<HonuaMobileClient>();

        await mobileClient.ApplyEditsAsync(
            serviceId,
            layerId,
            edits,
            new MobileContext { NetworkPolicy = NetworkPolicy.Offline });

        _mockCoreClient.Setup(x => x.ApplyEditsAsync(
                serviceId,
                layerId,
                It.Is<FeatureEdits>(candidate =>
                    candidate.Adds.Length == 1 &&
                    candidate.Updates.Length == 1 &&
                    candidate.Deletes.Length == 1),
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EditResult
            {
                AddResults = [new OperationResult { ObjectId = 601, Success = true }],
                UpdateResults = [new OperationResult { ObjectId = 100, Success = true }],
                DeleteResults = [new OperationResult { ObjectId = 99, Success = true }]
            });

        var syncResult = await mobileClient.SyncPendingEditsAsync();

        syncResult.SyncedOperations.Should().Be(3);
        syncResult.FailedOperations.Should().Be(0);
        (await _dbContext.PendingEdits.Where(pe => !pe.IsSynced).CountAsync()).Should().Be(0);
        _mockCoreClient.Verify(x => x.ApplyEditsAsync(
            serviceId,
            layerId,
            It.IsAny<FeatureEdits>(),
            It.IsAny<object>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueryFeaturesAsync_WithUnsupportedOfflineFilter_ShouldFallbackToNetwork()
    {
        var serviceId = "test-service";
        var layerId = 1;
        var geometryFactory = new GeometryFactory();
        var offlinePoint = geometryFactory.CreatePoint(new Coordinate(-122.4194, 37.7749));
        var networkPoint = geometryFactory.CreatePoint(new Coordinate(-157.8583, 21.3069));
        var storageService = _serviceProvider.GetRequiredService<IOfflineStorageService>();

        await storageService.CacheFeaturesAsync(
            serviceId,
            layerId,
            [CreateFeature(100, offlinePoint, new Dictionary<string, object?> { ["Name"] = "Offline" })]);

        var networkFeature = CreateFeature(200, networkPoint, new Dictionary<string, object?> { ["Name"] = "Server" });
        _mockCoreClient.Setup(x => x.QueryFeaturesAsync(
                serviceId,
                layerId,
                It.IsAny<FeatureQuery>(),
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(QueryResult<DomainFeature>.Create(1, [networkFeature]));

        var mobileClient = _serviceProvider.GetRequiredService<HonuaMobileClient>();
        var result = await mobileClient.QueryFeaturesAsync(
            serviceId,
            layerId,
            new FeatureQuery { Where = "Name = 'Server'" },
            new MobileContext
            {
                AllowOffline = true,
                NetworkPolicy = NetworkPolicy.WifiOrCellular
            });

        result.Features.Should().ContainSingle();
        result.Features[0].Id.Should().Be(200);
        _mockCoreClient.Verify(x => x.QueryFeaturesAsync(
            serviceId,
            layerId,
            It.IsAny<FeatureQuery>(),
            It.IsAny<object>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueryFeaturesAsync_OfflineOnlyWithUnsupportedOfflineFilter_ShouldThrow()
    {
        var mobileClient = _serviceProvider.GetRequiredService<HonuaMobileClient>();

        var act = async () => await mobileClient.QueryFeaturesAsync(
            "test-service",
            1,
            new FeatureQuery { Where = "Name = 'Server'" },
            new MobileContext
            {
                AllowOffline = true,
                NetworkPolicy = NetworkPolicy.Offline
            });

        await act.Should().ThrowAsync<NotSupportedException>();
        _mockCoreClient.Verify(x => x.QueryFeaturesAsync(
            It.IsAny<string>(),
            It.IsAny<int>(),
            It.IsAny<FeatureQuery>(),
            It.IsAny<object>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DownloadAreaAsync_WithNetworkResults_ShouldCacheDownloadedFeatures()
    {
        var serviceId = "download-service";
        var layerId = 7;
        var geometryFactory = new GeometryFactory();
        var point = geometryFactory.CreatePoint(new Coordinate(-157.8583, 21.3069));
        var features = new[]
        {
            CreateFeature(1, point, new Dictionary<string, object?> { ["Name"] = "A" }),
            CreateFeature(2, point, new Dictionary<string, object?> { ["Name"] = "B" })
        };

        var pageQueue = new Queue<FeaturePage>();
        pageQueue.Enqueue(new FeaturePage
        {
            Features = features.ToImmutableArray(),
            IsLastPage = true,
            PageNumber = 0
        });

        _mockCoreClient.Setup(x => x.QueryFeaturesStreamAsync(
                serviceId,
                layerId,
                It.IsAny<FeatureQuery>(),
                It.IsAny<object>(),
                It.IsAny<CancellationToken>()))
            .Returns(MockAsyncEnumerable(pageQueue));

        var mobileClient = _serviceProvider.GetRequiredService<HonuaMobileClient>();
        var result = await mobileClient.DownloadAreaAsync(
            serviceId,
            layerId,
            new CoreEnvelope
            {
                XMin = -158,
                YMin = 21,
                XMax = -157,
                YMax = 22,
                SpatialReference = new SpatialReference { WKID = 4326 }
            });

        result.IsSuccess.Should().BeTrue();
        result.FeaturesDownloaded.Should().Be(2);

        var cached = await _serviceProvider.GetRequiredService<IOfflineStorageService>()
            .QueryFeaturesAsync(serviceId, layerId, new FeatureQuery { Where = "1=1" });
        cached.Features.Should().HaveCount(2);
    }

    private static async IAsyncEnumerable<FeaturePage> MockAsyncEnumerable(Queue<FeaturePage> pages)
    {
        while (pages.Count > 0)
        {
            await Task.Delay(10); // Simulate async delay
            yield return pages.Dequeue();
        }
    }

    private static FeatureEdits CreateSampleEdits()
    {
        var geometryFactory = new GeometryFactory();
        var point = geometryFactory.CreatePoint(new Coordinate(-122.4194, 37.7749));

        return new FeatureEdits
        {
            Adds = [CreateFeature(0, point, new Dictionary<string, object?> { ["Name"] = "New Feature" })],
            Updates = [CreateFeature(100, point, new Dictionary<string, object?> { ["Name"] = "Updated Feature" })],
            Deletes = [99L]
        };
    }

    private static DomainFeature CreateFeature(long id, Geometry geometry, IDictionary<string, object?> attributes)
        => DomainFeature.Create(
            id,
            GeometryConverter.ToWkb(geometry),
            attributes.ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value));

    public void Dispose()
    {
        _dbContext?.Dispose();
        if (_serviceProvider is IDisposable disposableProvider)
        {
            disposableProvider.Dispose();
        }
    }
}
