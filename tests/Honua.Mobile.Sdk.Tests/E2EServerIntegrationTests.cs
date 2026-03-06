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
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Honua.Core.Models;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Transport.Clients;
using Honua.Mobile.Sdk;
using Honua.Mobile.Sdk.Clients;
using Honua.Mobile.Sdk.Storage;
using NetTopologySuite.Geometries;
using Xunit;
using Xunit.Abstractions;
using DomainFeature = Honua.Core.Features.FeatureStore.Domain.Feature;

namespace Honua.Mobile.Sdk.Tests;

/// <summary>
/// End-to-end integration tests that demonstrate the mobile SDK connecting to a real honua-server instance
/// and performing actual gRPC operations with live geospatial data.
/// </summary>
public class E2EServerIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;
    private readonly HttpClient _httpClient;
    private readonly GrpcChannel _grpcChannel;
    private readonly IServiceProvider _serviceProvider;
    private readonly HonuaMobileClient _mobileClient;

    public E2EServerIntegrationTests(WebApplicationFactory<Program> factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;

        // Setup test server with gRPC enabled
        _httpClient = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Configure test services for E2E scenarios
                services.Configure<HonuaMobileClientOptions>(options =>
                {
                    options.ServerAddress = "http://localhost:5000";
                    options.OfflineDatabase = ":memory:";
                    options.MobilePageSize = 50;
                    options.RequestTimeout = TimeSpan.FromSeconds(30);
                });
            });
        }).CreateClient();

        // Create gRPC channel pointing to test server
        _grpcChannel = GrpcChannel.ForAddress(_httpClient.BaseAddress!, new GrpcChannelOptions
        {
            HttpClient = _httpClient
        });

        // Setup mobile SDK services
        _serviceProvider = SetupMobileServices();
        _mobileClient = _serviceProvider.GetRequiredService<HonuaMobileClient>();
    }

    [Fact]
    public async Task QueryFeaturesAsync_ConnectToLiveServer_ShouldReturnRealFeatures()
    {
        // Arrange
        var serviceId = "test-service-1";
        var layerId = 1;

        var query = new FeatureQuery
        {
            Where = "1=1", // Get all features
            ResultRecordCount = 10,
            ReturnGeometry = true,
            OutFields = ImmutableArray.Create("Name", "Category", "Area")
        };

        var context = new MobileContext
        {
            AllowOffline = false, // Force live server query
            NetworkPolicy = NetworkPolicy.WifiOrCellular,
            BatteryPolicy = BatteryPolicy.Normal,
            ProgressReporter = new Progress<SyncProgress>(progress =>
            {
                _output.WriteLine($"Progress: {progress.Message}");
            })
        };

        // Act
        var result = await _mobileClient.QueryFeaturesAsync(serviceId, layerId, query, context);

        // Assert
        result.Should().NotBeNull();
        _output.WriteLine($"Retrieved {result.Features.Count} features from live server");

        if (result.Features.Any())
        {
            var firstFeature = result.Features[0];
            firstFeature.ObjectId.Should().BeGreaterThan(0);
            firstFeature.Attributes.Should().NotBeNull();
            _output.WriteLine($"First feature ID: {firstFeature.ObjectId}");

            if (firstFeature.Geometry != null)
            {
                firstFeature.Geometry.Should().NotBeNull();
                _output.WriteLine($"First feature geometry type: {firstFeature.Geometry.GeometryType}");
            }
        }
    }

    [Fact]
    public async Task QueryFeaturesWithSpatialFilter_ConnectToLiveServer_ShouldFilterByGeometry()
    {
        // Arrange
        var serviceId = "test-service-1";
        var layerId = 1;

        // Create a bounding box for San Francisco Bay Area
        var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
        var envelope = new Envelope(-122.6, -122.3, 37.7, 37.9); // SF Bay Area bounds
        var boundingBox = geometryFactory.ToGeometry(envelope);

        var spatialFilter = new SpatialFilter
        {
            Geometry = boundingBox,
            SpatialRelationship = SpatialRelationship.Intersects,
            SpatialReference = SpatialReference.Create(4326)
        };

        var query = new FeatureQuery
        {
            Where = "1=1",
            SpatialFilter = spatialFilter,
            ResultRecordCount = 20,
            ReturnGeometry = true
        };

        var context = new MobileContext
        {
            AllowOffline = false,
            NetworkPolicy = NetworkPolicy.WifiOrCellular,
            ProgressReporter = new Progress<SyncProgress>(progress =>
            {
                _output.WriteLine($"Spatial Query Progress: {progress.Message}");
            })
        };

        // Act
        var result = await _mobileClient.QueryFeaturesAsync(serviceId, layerId, query, context);

        // Assert
        result.Should().NotBeNull();
        _output.WriteLine($"Spatial filter returned {result.Features.Count} features");

        // Verify that returned features are within or intersect the bounding box
        foreach (var feature in result.Features.Where(f => f.Geometry != null))
        {
            var featureEnvelope = feature.Geometry!.EnvelopeInternal;
            featureEnvelope.Intersects(envelope).Should().BeTrue(
                $"Feature {feature.ObjectId} should intersect the query bounds");
        }
    }

    [Fact]
    public async Task QueryFeaturesStreamAsync_ConnectToLiveServer_ShouldStreamLargeDataset()
    {
        // Arrange
        var serviceId = "test-service-1";
        var layerId = 1;

        var query = new FeatureQuery
        {
            Where = "1=1",
            ResultRecordCount = 5, // Small page size to test streaming
            ReturnGeometry = true
        };

        var context = new MobileContext
        {
            AllowOffline = false,
            NetworkPolicy = NetworkPolicy.WifiOrCellular,
            ProgressReporter = new Progress<SyncProgress>(progress =>
            {
                _output.WriteLine($"Streaming Progress: {progress.Message}");
            })
        };

        var allFeatures = new List<DomainFeature>();
        var pageCount = 0;

        // Act
        await foreach (var page in _mobileClient.QueryFeaturesStreamAsync(serviceId, layerId, query, context))
        {
            pageCount++;
            allFeatures.AddRange(page.Features);

            _output.WriteLine($"Page {pageCount}: {page.Features.Length} features, IsLastPage: {page.IsLastPage}");

            // Validate page metadata on first page
            if (pageCount == 1 && page.Metadata != null)
            {
                page.Metadata.ObjectIdFieldName.Should().NotBeNullOrEmpty();
                page.Metadata.GeometryType.Should().NotBeNullOrEmpty();
                _output.WriteLine($"Layer geometry type: {page.Metadata.GeometryType}");
                _output.WriteLine($"Object ID field: {page.Metadata.ObjectIdFieldName}");
            }

            if (page.IsLastPage)
                break;
        }

        // Assert
        pageCount.Should().BeGreaterThan(0, "Should receive at least one page");
        _output.WriteLine($"Total: {allFeatures.Count} features across {pageCount} pages");

        // Verify feature quality
        var featuresWithIds = allFeatures.Where(f => f.ObjectId > 0).ToList();
        featuresWithIds.Should().NotBeEmpty("Should have features with valid IDs");

        // Verify no duplicate features across pages
        var uniqueIds = allFeatures.Select(f => f.ObjectId).Distinct().ToList();
        uniqueIds.Count.Should().Be(allFeatures.Count, "Should not have duplicate features across pages");
    }

    [Fact]
    public async Task ApplyEditsAsync_ConnectToLiveServer_ShouldCreateUpdateDeleteFeatures()
    {
        // Arrange
        var serviceId = "test-service-1";
        var layerId = 1;

        var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);

        // Create a new feature
        var newFeature = DomainFeature.Create(
            id: 0, // Will be assigned by server
            geometry: geometryFactory.CreatePoint(new Coordinate(-122.4194, 37.7749)).AsBinary(), // San Francisco
            attributes: new Dictionary<string, object>
            {
                ["Name"] = "Mobile SDK Test Feature",
                ["Category"] = "Test",
                ["Area"] = 100.0,
                ["CreatedBy"] = "E2E Integration Test"
            });

        var edits = new FeatureEdits
        {
            Adds = ImmutableArray.Create(newFeature),
            RollbackOnFailure = true
        };

        var context = new MobileContext
        {
            AllowOffline = false,
            NetworkPolicy = NetworkPolicy.WifiOrCellular,
            ProgressReporter = new Progress<SyncProgress>(progress =>
            {
                _output.WriteLine($"Edit Progress: {progress.Message}");
            })
        };

        // Act - Apply edits
        var result = await _mobileClient.ApplyEditsAsync(serviceId, layerId, edits, context);

        // Assert
        result.Should().NotBeNull();
        result.AddResults.Should().HaveCount(1);

        var addResult = result.AddResults[0];
        addResult.Success.Should().BeTrue($"Feature creation should succeed: {addResult.Error?.Message}");
        addResult.ObjectId.Should().BeGreaterThan(0, "Should get a valid object ID for new feature");

        _output.WriteLine($"Created feature with ID: {addResult.ObjectId}");

        // Verify the feature was actually created by querying it back
        var verificationQuery = new FeatureQuery
        {
            ObjectIds = ImmutableArray.Create(addResult.ObjectId),
            ReturnGeometry = true
        };

        var queryResult = await _mobileClient.QueryFeaturesAsync(serviceId, layerId, verificationQuery, context);
        queryResult.Features.Should().HaveCount(1, "Should be able to query the created feature");

        var createdFeature = queryResult.Features[0];
        createdFeature.ObjectId.Should().Be(addResult.ObjectId);
        createdFeature.Attributes.Should().ContainKey("Name");
        createdFeature.Attributes["Name"].Should().Be("Mobile SDK Test Feature");
    }

    [Fact]
    public async Task OfflineToOnlineSync_ShouldSyncPendingEdits()
    {
        // Arrange - Create edits in offline mode first
        var serviceId = "test-service-1";
        var layerId = 1;

        var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
        var newFeature = DomainFeature.Create(
            id: 0,
            geometry: geometryFactory.CreatePoint(new Coordinate(-122.5, 37.8)).AsBinary(),
            attributes: new Dictionary<string, object>
            {
                ["Name"] = "Offline Created Feature",
                ["Category"] = "Offline Test"
            });

        var offlineEdits = new FeatureEdits
        {
            Adds = ImmutableArray.Create(newFeature)
        };

        var offlineContext = new MobileContext
        {
            NetworkPolicy = NetworkPolicy.Offline, // Force offline mode
            AllowOffline = true
        };

        // Act 1 - Apply edits offline (should queue them)
        var offlineResult = await _mobileClient.ApplyEditsAsync(serviceId, layerId, offlineEdits, offlineContext);

        // Assert offline results
        offlineResult.AddResults.Should().HaveCount(1);
        offlineResult.AddResults[0].Success.Should().BeTrue();

        // Act 2 - Sync pending edits to server
        var syncContext = new MobileContext
        {
            NetworkPolicy = NetworkPolicy.WifiOrCellular,
            AllowOffline = true,
            ProgressReporter = new Progress<SyncProgress>(progress =>
            {
                _output.WriteLine($"Sync Progress: {progress.Message}");
            })
        };

        var syncResult = await _mobileClient.SyncPendingEditsAsync(syncContext.CancellationToken);

        // Assert sync results
        syncResult.Should().NotBeNull();
        _output.WriteLine($"Sync result: {syncResult.TotalOperations} operations, {syncResult.SuccessfulOperations} successful");

        if (syncResult.SuccessfulOperations > 0)
        {
            syncResult.SuccessfulOperations.Should().BeGreaterThan(0, "Should have successfully synced operations");
        }
    }

    [Fact]
    public async Task PerformanceTest_LargeFeatureQuery_ShouldMeetPerformanceCriteria()
    {
        // Arrange
        var serviceId = "test-service-1";
        var layerId = 1;

        var query = new FeatureQuery
        {
            Where = "1=1",
            ResultRecordCount = 1000, // Large result set
            ReturnGeometry = true
        };

        var context = new MobileContext
        {
            AllowOffline = false,
            NetworkPolicy = NetworkPolicy.WifiOrCellular,
            Priority = RequestPriority.High
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var initialMemory = GC.GetTotalMemory(false);

        // Act
        var result = await _mobileClient.QueryFeaturesAsync(serviceId, layerId, query, context);

        stopwatch.Stop();
        var finalMemory = GC.GetTotalMemory(false);
        var memoryUsage = finalMemory - initialMemory;

        // Assert Performance Criteria
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000, "Query should complete within 5 seconds");
        memoryUsage.Should().BeLessThan(50_000_000, "Memory usage should be less than 50MB"); // 50MB limit

        result.Features.Count.Should().BeGreaterThan(0, "Should return features");

        _output.WriteLine($"Performance Results:");
        _output.WriteLine($"  - Query time: {stopwatch.ElapsedMilliseconds}ms");
        _output.WriteLine($"  - Features returned: {result.Features.Count}");
        _output.WriteLine($"  - Memory used: {memoryUsage / 1024 / 1024:F2}MB");
        _output.WriteLine($"  - Avg time per feature: {(double)stopwatch.ElapsedMilliseconds / result.Features.Count:F2}ms");
    }

    private IServiceProvider SetupMobileServices()
    {
        var services = new ServiceCollection();

        // Configure logging for detailed debugging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddDebug();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // Configure mobile client options
        services.Configure<HonuaMobileClientOptions>(options =>
        {
            options.ServerAddress = _httpClient.BaseAddress!.ToString();
            options.OfflineDatabase = ":memory:";
            options.MobilePageSize = 50;
            options.RequestTimeout = TimeSpan.FromSeconds(30);
        });

        // Register gRPC client factory that uses the test server
        services.AddScoped<Func<MobileContext, Geospatial.V1.FeatureService.FeatureServiceClient>>(provider =>
        {
            return (context) => new Geospatial.V1.FeatureService.FeatureServiceClient(_grpcChannel);
        });

        // Register core gRPC client with mobile context adapter
        services.AddScoped<IFeatureServiceClient<MobileContext>>(provider =>
        {
            var clientFactory = provider.GetRequiredService<Func<MobileContext, Geospatial.V1.FeatureService.FeatureServiceClient>>();
            var logger = provider.GetRequiredService<ILogger<GrpcFeatureServiceClient<MobileContext>>>();

            return new GrpcFeatureServiceClient<MobileContext>(
                clientFactory,
                new GrpcClientOptions
                {
                    MaxRetries = 3,
                    BaseRetryDelayMs = 1000,
                    RequestTimeout = TimeSpan.FromSeconds(30),
                    StreamTimeout = TimeSpan.FromMinutes(5)
                },
                logger);
        });

        // Register connectivity service (mock for tests)
        services.AddSingleton<IConnectivityService>(provider =>
        {
            var mock = new Moq.Mock<IConnectivityService>();
            mock.Setup(x => x.IsConnectionAvailableAsync(It.IsAny<NetworkPolicy>())).ReturnsAsync(true);
            mock.Setup(x => x.IsBatteryLevelSufficientAsync(It.IsAny<BatteryPolicy>())).ReturnsAsync(true);
            mock.Setup(x => x.GetNetworkConnectionTypeAsync()).ReturnsAsync(NetworkConnectionType.WiFi);
            mock.Setup(x => x.GetBatteryLevelAsync()).ReturnsAsync(100.0);
            return mock.Object;
        });

        // Register offline storage service (in-memory for tests)
        services.AddScoped<IOfflineStorageService>(provider =>
        {
            var mock = new Moq.Mock<IOfflineStorageService>();

            // Setup basic offline storage behaviors
            mock.Setup(x => x.QueryFeaturesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<FeatureQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QueryResult<DomainFeature> { Features = ImmutableArray<DomainFeature>.Empty });

            mock.Setup(x => x.HasCachedDataAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            mock.Setup(x => x.QueueEditsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<FeatureEdits>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EditResult
                {
                    AddResults = ImmutableArray.Create(new OperationResult { ObjectId = 1, Success = true })
                });

            mock.Setup(x => x.SyncPendingEditsAsync(It.IsAny<MobileContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SyncResult
                {
                    TotalOperations = 1,
                    SuccessfulOperations = 1,
                    FailedOperations = 0
                });

            return mock.Object;
        });

        // Register the main mobile client
        services.AddScoped<HonuaMobileClient>();

        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _grpcChannel?.Dispose();
        _httpClient?.Dispose();
        if (_serviceProvider is IDisposable disposableProvider)
        {
            disposableProvider.Dispose();
        }
    }
}