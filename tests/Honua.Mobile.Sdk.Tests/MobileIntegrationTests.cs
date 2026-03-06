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
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Honua.Core.Models;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Transport.Clients;
using Honua.Mobile.Sdk;
using Honua.Mobile.Sdk.Clients;
using Honua.Mobile.Sdk.Storage;
using NetTopologySuite.Geometries;
using Xunit;
using DomainFeature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Mobile.Sdk.Tests;

/// <summary>
/// Integration tests for mobile SDK with real domain models from Honua.Core.Sdk.
/// </summary>
public class MobileIntegrationTests : IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly OfflineDbContext _dbContext;
    private readonly Mock<IFeatureServiceClient> _mockCoreClient;

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
        _mockCoreClient = new Mock<IFeatureServiceClient>();
        services.AddSingleton(_mockCoreClient.Object);

        // Register mobile client adapter
        services.AddScoped<IFeatureServiceClient<MobileContext>>(provider =>
        {
            var coreClient = provider.GetRequiredService<IFeatureServiceClient>();
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
            new DomainFeature
            {
                ObjectId = 1,
                Attributes = new Dictionary<string, object>
                {
                    ["Name"] = "Test Feature 1",
                    ["Type"] = "Point"
                },
                Geometry = point
            },
            new DomainFeature
            {
                ObjectId = 2,
                Attributes = new Dictionary<string, object>
                {
                    ["Name"] = "Test Feature 2",
                    ["Type"] = "Point"
                },
                Geometry = point
            }
        };

        var mockResult = new QueryResult<DomainFeature>
        {
            Features = mockFeatures.ToImmutableArray(),
            ExceededTransferLimit = false
        };

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
        result.Features[0].ObjectId.Should().Be(1);
        result.Features[0].Attributes!["Name"].Should().Be("Test Feature 1");
        result.Features[0].Geometry.Should().NotBeNull();
        result.Features[0].Geometry!.GeometryType.Should().Be("Point");
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
            new DomainFeature
            {
                ObjectId = 100,
                Attributes = new Dictionary<string, object>
                {
                    ["Name"] = "Cached Feature",
                    ["Area"] = 1234.56,
                    ["Active"] = true
                },
                Geometry = polygon
            }
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
        cachedFeature.ObjectId.Should().Be(100);
        cachedFeature.Attributes!["Name"].Should().Be("Cached Feature");
        cachedFeature.Attributes["Area"].Should().BeEquivalentTo(1234.56);
        cachedFeature.Attributes["Active"].Should().BeEquivalentTo(true);
        cachedFeature.Geometry.Should().NotBeNull();
        cachedFeature.Geometry!.GeometryType.Should().Be("Polygon");
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
        var allFeatures = Enumerable.Range(1, 5).Select(i => new DomainFeature
        {
            ObjectId = i,
            Attributes = new Dictionary<string, object> { ["Id"] = i, ["Name"] = $"Feature {i}" },
            Geometry = point
        }).ToArray();

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
        totalFeatures.Select(f => f.ObjectId).Should().BeEquivalentTo(new[] { 1, 2, 3, 4, 5 });
    }

    [Fact]
    public async Task ApplyEditsAsync_WithRealDomainModels_ShouldQueueForSync()
    {
        // Arrange
        var serviceId = "test-service";
        var layerId = 1;

        var geometryFactory = new GeometryFactory();
        var point = geometryFactory.CreatePoint(new Coordinate(-122.4194, 37.7749));

        var newFeature = new DomainFeature
        {
            Attributes = new Dictionary<string, object>
            {
                ["Name"] = "New Feature",
                ["Category"] = "Test"
            },
            Geometry = point
        };

        var updatedFeature = new DomainFeature
        {
            ObjectId = 100,
            Attributes = new Dictionary<string, object>
            {
                ["Name"] = "Updated Feature",
                ["Category"] = "Modified"
            },
            Geometry = point
        };

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

    private static async IAsyncEnumerable<FeaturePage> MockAsyncEnumerable(Queue<FeaturePage> pages)
    {
        while (pages.Count > 0)
        {
            await Task.Delay(10); // Simulate async delay
            yield return pages.Dequeue();
        }
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
        if (_serviceProvider is IDisposable disposableProvider)
        {
            disposableProvider.Dispose();
        }
    }
}