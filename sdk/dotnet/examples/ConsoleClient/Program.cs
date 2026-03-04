// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using Honua.Mobile.Core.Auth;
using Honua.Mobile.Core.Client;
using Honua.Mobile.Core.Models;
using Honua.Mobile.Core.Querying;
using Microsoft.Extensions.Logging;

namespace ConsoleClient;

/// <summary>
/// Console client demonstrating Honua Mobile SDK capabilities.
/// Shows gRPC connectivity, authentication, querying, and editing operations.
/// </summary>
internal class Program
{
    private static async Task Main(string[] args)
    {
        // Set up logging
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddConsole().SetMinimumLevel(LogLevel.Information));

        var logger = loggerFactory.CreateLogger<Program>();

        Console.WriteLine("🌍 Honua Mobile SDK - Console Client Demo");
        Console.WriteLine("========================================");
        Console.WriteLine();

        try
        {
            // Get connection settings
            var (serverUrl, apiKey) = GetConnectionSettings();

            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                Console.WriteLine("❌ Server URL is required. Set HONUA_SERVER_URL environment variable or enter when prompted.");
                return;
            }

            Console.WriteLine($"🔗 Connecting to: {serverUrl}");
            Console.WriteLine();

            // Create authentication provider
            var auth = CreateAuthenticationProvider(apiKey, logger);

            // Create the gRPC client
            using var client = new HonuaFeatureClient(serverUrl, auth);

            // Run demonstration scenarios
            await RunDemonstrationScenarios(client);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "❌ Application error");
            Console.WriteLine($"❌ Error: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("Demo completed. Press any key to exit...");
        Console.ReadKey();
    }

    private static (string ServerUrl, string? ApiKey) GetConnectionSettings()
    {
        // Try environment variables first
        var serverUrl = Environment.GetEnvironmentVariable("HONUA_SERVER_URL") ?? "";
        var apiKey = Environment.GetEnvironmentVariable("HONUA_API_KEY");

        // Prompt for server URL if not set
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            Console.Write("Enter Honua server URL (e.g., https://api.honua.com): ");
            serverUrl = Console.ReadLine() ?? "";
        }

        // Prompt for API key if not set
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Write("Enter API key (optional, press Enter to skip): ");
            apiKey = Console.ReadLine();
        }

        return (serverUrl, apiKey);
    }

    private static IMobileAuthenticationProvider CreateAuthenticationProvider(
        string? apiKey,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("⚠️  No API key provided. Requests may fail if authentication is required.");
            return AuthenticationProviderFactory.CreateBasic();
        }

        Console.WriteLine("🔐 Using API key authentication");
        return AuthenticationProviderFactory.CreateBasic(apiKey);
    }

    private static async Task RunDemonstrationScenarios(HonuaFeatureClient client)
    {
        Console.WriteLine("🚀 Starting SDK demonstrations...");
        Console.WriteLine();

        // Demo 1: Basic Query
        await DemoBasicQuery(client);

        // Demo 2: Spatial Query
        await DemoSpatialQuery(client);

        // Demo 3: Streaming Query
        await DemoStreamingQuery(client);

        // Demo 4: Statistical Query
        await DemoStatisticalQuery(client);

        // Demo 5: Feature Editing
        await DemoFeatureEditing(client);

        // Demo 6: Common Query Patterns
        await DemoCommonPatterns(client);
    }

    private static async Task DemoBasicQuery(HonuaFeatureClient client)
    {
        Console.WriteLine("📋 Demo 1: Basic Feature Query");
        Console.WriteLine("-----------------------------");

        try
        {
            // Simple query using the fluent interface
            var query = FeatureQueryBuilder.Create()
                .WithFields("OBJECTID", "NAME", "STATUS")
                .WithoutGeometry()
                .WithLimit(10)
                .OrderByAsc("OBJECTID");

            Console.WriteLine($"Query: Get first 10 features (attributes only)");

            // Execute the query (using placeholder service/layer)
            // In a real scenario, these would be actual service and layer IDs
            var serviceId = "demo-service";
            var layerId = 0;

            var result = await client.QueryAsync(serviceId, layerId, query);

            Console.WriteLine($"✅ Retrieved {result.Items.Count} features");
            Console.WriteLine($"   Object ID field: {result.ObjectIdFieldName}");
            Console.WriteLine($"   Field count: {result.Fields.Count}");
            Console.WriteLine($"   Has more results: {result.HasMoreResults}");

            if (result.Items.Any())
            {
                var firstFeature = result.Items.First();
                Console.WriteLine($"   First feature ID: {firstFeature.Id}");
                Console.WriteLine($"   Attribute count: {firstFeature.Attributes.Count}");

                foreach (var attr in firstFeature.Attributes.Take(3))
                {
                    Console.WriteLine($"   - {attr.Key}: {attr.Value}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Query failed: {ex.Message}");
        }

        Console.WriteLine();
    }

    private static async Task DemoSpatialQuery(HonuaFeatureClient client)
    {
        Console.WriteLine("🗺️  Demo 2: Spatial Query");
        Console.WriteLine("-----------------------");

        try
        {
            // Query features near San Francisco
            var sfLongitude = -122.4194;
            var sfLatitude = 37.7749;

            var query = FeatureQueryBuilder.Create()
                .Near(sfLongitude, sfLatitude, 5000, DistanceUnit.Meters) // 5km radius
                .WithFields("OBJECTID", "NAME", "ADDRESS")
                .WithGeometry(true)
                .WithLimit(5)
                .OrderByAsc("OBJECTID");

            Console.WriteLine($"Query: Features within 5km of San Francisco ({sfLatitude}, {sfLongitude})");

            var result = await client.QueryAsync("demo-service", 0, query);

            Console.WriteLine($"✅ Found {result.Items.Count} nearby features");
            Console.WriteLine($"   Geometry type: {result.GeometryType}");

            if (result.SpatialReference != null)
            {
                var sr = result.SpatialReference;
                Console.WriteLine($"   Spatial reference: WKID {sr.Wkid ?? sr.LatestWkid}");
            }

            foreach (var feature in result.Items.Take(3))
            {
                Console.WriteLine($"   Feature {feature.Id}:");
                if (feature.Geometry is PointGeometry point)
                {
                    Console.WriteLine($"     Location: ({point.X:F6}, {point.Y:F6})");
                }
                if (feature.Attributes.TryGetValue("NAME", out var name))
                {
                    Console.WriteLine($"     Name: {name}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Spatial query failed: {ex.Message}");
        }

        Console.WriteLine();
    }

    private static async Task DemoStreamingQuery(HonuaFeatureClient client)
    {
        Console.WriteLine("🌊 Demo 3: Streaming Query");
        Console.WriteLine("-------------------------");

        try
        {
            var query = FeatureQueryBuilder.Create()
                .WithFields("OBJECTID", "NAME")
                .WithoutGeometry()
                .WithLimit(100) // Large result set for streaming demo
                .OrderByAsc("OBJECTID");

            Console.WriteLine("Query: Stream large result set efficiently");

            var featureCount = 0;
            var startTime = DateTime.Now;

            await foreach (var feature in client.QueryStreamAsync("demo-service", 0, query))
            {
                featureCount++;

                // Show progress for first few features
                if (featureCount <= 5)
                {
                    Console.WriteLine($"   Streamed feature {feature.Id}");
                }
                else if (featureCount == 6)
                {
                    Console.WriteLine("   ... (continuing to stream)");
                }
            }

            var elapsed = DateTime.Now - startTime;
            Console.WriteLine($"✅ Streamed {featureCount} features in {elapsed.TotalMilliseconds:F0}ms");
            Console.WriteLine($"   Average: {elapsed.TotalMilliseconds / Math.Max(featureCount, 1):F1}ms per feature");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Streaming query failed: {ex.Message}");
        }

        Console.WriteLine();
    }

    private static async Task DemoStatisticalQuery(HonuaFeatureClient client)
    {
        Console.WriteLine("📊 Demo 4: Statistical Query");
        Console.WriteLine("---------------------------");

        try
        {
            // Query with statistics and grouping
            var query = FeatureQueryBuilder.Create()
                .WithCommonStatistics("POPULATION", "POP")
                .GroupBy("STATE", "COUNTY")
                .WithoutGeometry()
                .WithLimit(10)
                .OrderByDesc("POP_COUNT");

            Console.WriteLine("Query: Population statistics grouped by state and county");

            var result = await client.QueryAsync("demo-service", 0, query);

            Console.WriteLine($"✅ Retrieved {result.Items.Count} statistical groups");

            foreach (var feature in result.Items.Take(5))
            {
                Console.WriteLine($"   Group:");
                if (feature.Attributes.TryGetValue("STATE", out var state))
                    Console.WriteLine($"     State: {state}");
                if (feature.Attributes.TryGetValue("COUNTY", out var county))
                    Console.WriteLine($"     County: {county}");
                if (feature.Attributes.TryGetValue("POP_COUNT", out var count))
                    Console.WriteLine($"     Count: {count}");
                if (feature.Attributes.TryGetValue("POP_SUM", out var sum))
                    Console.WriteLine($"     Total Population: {sum:N0}");
                Console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Statistical query failed: {ex.Message}");
        }

        Console.WriteLine();
    }

    private static async Task DemoFeatureEditing(HonuaFeatureClient client)
    {
        Console.WriteLine("✏️  Demo 5: Feature Editing");
        Console.WriteLine("-------------------------");

        try
        {
            // Create a new feature
            var newFeature = Feature.Create(
                new Dictionary<string, object?>
                {
                    ["NAME"] = "SDK Demo Feature",
                    ["STATUS"] = "Active",
                    ["CREATED_DATE"] = DateTime.Now
                },
                PointGeometry.Create(-122.4194, 37.7749)
            );

            Console.WriteLine("Creating new feature...");

            var createResult = await client.CreateFeaturesAsync("demo-service", 0, new[] { newFeature });

            if (createResult.IsSuccess)
            {
                var createdFeature = createResult.CreateResults.FirstOrDefault();
                Console.WriteLine($"✅ Created feature with ID: {createdFeature?.ObjectId}");

                if (createdFeature?.ObjectId > 0)
                {
                    // Update the feature
                    var updatedFeature = newFeature with
                    {
                        Id = createdFeature.ObjectId,
                        Attributes = new Dictionary<string, object?>(newFeature.Attributes)
                        {
                            ["STATUS"] = "Updated",
                            ["MODIFIED_DATE"] = DateTime.Now
                        }
                    };

                    Console.WriteLine("Updating feature...");

                    var updateResult = await client.UpdateFeaturesAsync("demo-service", 0, new[] { updatedFeature });

                    if (updateResult.IsSuccess)
                    {
                        Console.WriteLine("✅ Feature updated successfully");

                        // Delete the feature
                        Console.WriteLine("Deleting feature...");

                        var deleteResult = await client.DeleteFeaturesAsync("demo-service", 0, new[] { createdFeature.ObjectId });

                        if (deleteResult.IsSuccess)
                        {
                            Console.WriteLine("✅ Feature deleted successfully");
                        }
                        else
                        {
                            Console.WriteLine($"❌ Delete failed: {deleteResult.Error?.Message}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"❌ Update failed: {updateResult.Error?.Message}");
                    }
                }
            }
            else
            {
                Console.WriteLine($"❌ Create failed: {createResult.Error?.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Editing demo failed: {ex.Message}");
        }

        Console.WriteLine();
    }

    private static async Task DemoCommonPatterns(HonuaFeatureClient client)
    {
        Console.WriteLine("🔧 Demo 6: Common Query Patterns");
        Console.WriteLine("--------------------------------");

        try
        {
            // Demonstrate common query patterns
            Console.WriteLine("1. Active features:");
            var activeQuery = CommonQueries.ActiveFeatures()
                .WithLimit(5);

            var activeResult = await client.CountAsync("demo-service", 0, activeQuery);
            Console.WriteLine($"   Count: {activeResult}");

            Console.WriteLine("2. Recent features (last 7 days):");
            var recentQuery = CommonQueries.CreatedInLastDays(7)
                .WithLimit(5);

            var recentResult = await client.CountAsync("demo-service", 0, recentQuery);
            Console.WriteLine($"   Count: {recentResult}");

            Console.WriteLine("3. Nearby features (1km radius):");
            var nearbyQuery = CommonQueries.NearbyFeatures(-122.4194, 37.7749, 1000)
                .WithLimit(5);

            var nearbyResult = await client.CountAsync("demo-service", 0, nearbyQuery);
            Console.WriteLine($"   Count: {nearbyResult}");

            Console.WriteLine("4. Attribute search:");
            var searchQuery = CommonQueries.AttributeSearch(
                "NAME",
                "Park",
                new[] { "OBJECTID", "NAME", "ADDRESS" },
                limit: 5);

            var searchResult = await client.CountAsync("demo-service", 0, searchQuery);
            Console.WriteLine($"   Count: {searchResult}");

            Console.WriteLine("5. Grouped statistics:");
            var groupQuery = CommonQueries.GroupedCounts("STATUS");

            var groupResult = await client.CountAsync("demo-service", 0, groupQuery);
            Console.WriteLine($"   Groups: {groupResult}");

            Console.WriteLine("✅ All common patterns demonstrated");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Common patterns demo failed: {ex.Message}");
        }

        Console.WriteLine();
    }
}