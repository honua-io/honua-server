// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Routing.Features.Routing.Providers;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Honua.Server.Tests.Routing;

/// <summary>
/// Queued closest-facility, OD-cost-matrix, and location-allocation jobs on the
/// real <see cref="PgRoutingProvider"/> and the <see cref="PgRoutingFixture"/> lattice.
/// Costs, ranks, identities, and geometry are checked against a lattice shortest-path
/// and WGS84 length oracle in this file. The canonical durable worker publishes the
/// named GP results.
/// </summary>
/// <remarks>
/// Opt-in only: set <c>HONUA_ROUTING_TEST=1</c>. The seeded graph is the same 3x3
/// lattice as <see cref="NAServerPgRoutingEndToEndTests"/> (edge cost 1, walking cost 2).
/// </remarks>
[Collection(RedisFixture.CollectionName)]
public sealed class RoutingPgRoutingQueuedJobTests : IClassFixture<PgRoutingFixture>, IAsyncLifetime
{
    private const string ServiceId = WebAppFixture.TestServiceId;
    private const double DegreeTolerance = 1e-8;
    private const double MeterTolerance = 0.05;
    private const double MileTolerance = 1e-4;

    private static readonly TimeSpan StallTolerance = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SuccessBudget = StallTolerance + (JobExecutionService.PollInterval * 6);

    private readonly RedisFixture _redis;
    private readonly PgRoutingFixture _routing;
    private readonly WebAppFixture _host;
    private readonly string _redisConnection;

    public RoutingPgRoutingQueuedJobTests(RedisFixture redis, PgRoutingFixture routing)
    {
        _redis = redis;
        _routing = routing;
        _redisConnection = DurableConnection(redis.ConnectionString, database: null);
        _host = CreateHost(_redisConnection, new ProfiledNetworkDatasetResolver(), routing);
    }

    public async Task InitializeAsync()
    {
        await DeleteControlPlaneKeysAsync(_redisConnection, database: 0);
        await _host.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await DeleteControlPlaneKeysAsync(_redisConnection, database: 0);
    }

    [RoutingTest("HONUA_ROUTING_TEST")]
    public async Task ClosestFacilities_QueuedJob_MatchesLatticeOracle()
    {
        const long incidentId = 0;
        const string incidentName = "SW";
        var incidentVertex = VertexAt(0, 0);
        var facilities = new[]
        {
            (ObjectId: 17L, Name: "East", Vertex: VertexAt(0.01, 0)),
            (ObjectId: 9L, Name: "NE", Vertex: VertexAt(0.02, 0.02)),
        };
        var incidents = PointSet((0, 0, incidentId, incidentName, (double?)null));
        var facilitySet = PointSet(
            (0.01, 0, facilities[0].ObjectId, facilities[0].Name, null),
            (0.02, 0.02, facilities[1].ObjectId, facilities[1].Name, null));

        foreach (var (mode, minutesPerHop) in new[] { ("driving", 1), ("walking", 2) })
        {
            var ranked = facilities
                .Select(facility => (facility, Minutes: TravelMinutes(incidentVertex, facility.Vertex, minutesPerHop)))
                .OrderBy(candidate => candidate.Minutes)
                .ThenBy(candidate => candidate.facility.ObjectId)
                .ToArray();
            ranked[0].Minutes.Should().BeLessThan(ranked[1].Minutes, "the lattice nearest facility is unique");
            var nearest = ranked[0];
            var inputs = new Dictionary<string, string>
            {
                ["Incidents"] = incidents,
                ["Facilities"] = facilitySet,
                ["Number_of_Facilities_to_Find"] = "1",
                ["Travel_Mode"] = mode,
            };

            foreach (var family in new[] { "GPServer", "NAServer" })
            {
                using var client = Client(_host);
                var jobId = await SubmitAsync(client, "FindClosestFacilities", inputs, family);
                using var terminal = await UntilTerminalAsync(client, "FindClosestFacilities", jobId, family, SuccessBudget);
                terminal.RootElement.GetProperty("jobStatus").GetString().Should().Be("esriJobSucceeded", terminal.RootElement.GetRawText());
                AssertNamedResults(terminal.RootElement, "Output_Routes", "Output_Incidents", "Output_Facilities", "Solve_Succeeded");
                await AssertSucceededFlagAsync(client, "FindClosestFacilities", jobId, family);
                await AssertArtifactCountAsync(jobId, _host, 4);

                using var routes = await ResultAsync(client, "FindClosestFacilities", jobId, "Output_Routes", family);
                var value = routes.RootElement.GetProperty("value");
                value.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
                var features = value.GetProperty("features");
                features.GetArrayLength().Should().Be(1);
                var attributes = features[0].GetProperty("attributes");
                attributes.GetProperty("IncidentID").GetInt64().Should().Be(incidentId);
                attributes.GetProperty("FacilityID").GetInt64().Should().Be(nearest.facility.ObjectId);
                attributes.GetProperty("FacilityRank").GetInt32().Should().Be(1);
                attributes.GetProperty("Name").GetString().Should().Be($"{incidentName} - {nearest.facility.Name}");
                attributes.GetProperty("Total_Minutes").GetDouble().Should().BeApproximately(nearest.Minutes, 0.001);
                var path = features[0].GetProperty("geometry").GetProperty("paths")[0];
                path.GetArrayLength().Should().Be(2);
                AssertCoordinate(path[0], 0, 0, DegreeTolerance);
                AssertCoordinate(path[1], 0.01, 0, DegreeTolerance);
                var meters = VincentyMeters(0, 0, 0.01, 0);
                attributes.GetProperty("Total_Kilometers").GetDouble().Should().BeApproximately(meters / 1000d, MeterTolerance / 1000d);
                attributes.GetProperty("Total_Miles").GetDouble().Should().BeApproximately(meters / 1609.344d, MileTolerance);

                using var echoed = await ResultAsync(client, "FindClosestFacilities", jobId, "Output_Incidents", family);
                var incident = echoed.RootElement.GetProperty("value").GetProperty("features")[0].GetProperty("attributes");
                incident.GetProperty("OBJECTID").GetInt64().Should().Be(incidentId);
                incident.GetProperty("Name").GetString().Should().Be(incidentName);
            }
        }
    }

    [RoutingTest("HONUA_ROUTING_TEST")]
    public async Task OdCostMatrix_QueuedJob_MatchesLatticeOracle()
    {
        var origins = new[]
        {
            (ObjectId: 0L, Name: "O1", Vertex: VertexAt(0, 0), X: 0d, Y: 0d),
            (ObjectId: 3L, Name: "O3", Vertex: VertexAt(0.02, 0), X: 0.02d, Y: 0d),
        };
        var destinations = new[]
        {
            (ObjectId: 7L, Name: "D7", Vertex: VertexAt(0, 0.02), X: 0d, Y: 0.02d),
            (ObjectId: 9L, Name: "D9", Vertex: VertexAt(0.02, 0.02), X: 0.02d, Y: 0.02d),
        };
        var inputs = new Dictionary<string, string>
        {
            ["Origins"] = PointSet(
                (origins[0].X, origins[0].Y, origins[0].ObjectId, origins[0].Name, null),
                (origins[1].X, origins[1].Y, origins[1].ObjectId, origins[1].Name, null)),
            ["Destinations"] = PointSet(
                (destinations[0].X, destinations[0].Y, destinations[0].ObjectId, destinations[0].Name, null),
                (destinations[1].X, destinations[1].Y, destinations[1].ObjectId, destinations[1].Name, null)),
        };

        foreach (var family in new[] { "GPServer", "NAServer" })
        {
            using var client = Client(_host);
            var jobId = await SubmitAsync(client, "GenerateOriginDestinationCostMatrix", inputs, family);
            using var terminal = await UntilTerminalAsync(client, "GenerateOriginDestinationCostMatrix", jobId, family, SuccessBudget);
            terminal.RootElement.GetProperty("jobStatus").GetString().Should().Be("esriJobSucceeded", terminal.RootElement.GetRawText());
            AssertNamedResults(
                terminal.RootElement,
                "Output_Origin_Destination_Lines",
                "Output_Origins",
                "Output_Destinations",
                "Solve_Succeeded");
            await AssertSucceededFlagAsync(client, "GenerateOriginDestinationCostMatrix", jobId, family);
            await AssertArtifactCountAsync(jobId, _host, 4);

            using var lines = await ResultAsync(client, "GenerateOriginDestinationCostMatrix", jobId, "Output_Origin_Destination_Lines", family);
            var features = lines.RootElement.GetProperty("value").GetProperty("features");
            features.GetArrayLength().Should().Be(origins.Length * destinations.Length);
            foreach (var origin in origins)
            {
                var ranked = destinations
                    .Select(destination => (destination, Minutes: TravelMinutes(origin.Vertex, destination.Vertex, minutesPerHop: 1)))
                    .OrderBy(candidate => candidate.Minutes)
                    .ThenBy(candidate => candidate.destination.ObjectId)
                    .ToArray();
                for (var rank = 0; rank < ranked.Length; rank++)
                {
                    var cell = RequirePair(features, "OriginID", origin.ObjectId, "DestinationID", ranked[rank].destination.ObjectId);
                    var attributes = cell.GetProperty("attributes");
                    attributes.GetProperty("DestinationRank").GetInt32().Should().Be(rank + 1);
                    attributes.GetProperty("Total_Time").GetDouble().Should().BeApproximately(ranked[rank].Minutes, 0.001);
                    attributes.GetProperty("OriginName").GetString().Should().Be(origin.Name);
                    attributes.GetProperty("DestinationName").GetString().Should().Be(ranked[rank].destination.Name);
                    cell.GetProperty("geometry").ValueKind.Should().Be(JsonValueKind.Null);
                }
            }

            using var echoed = await ResultAsync(client, "GenerateOriginDestinationCostMatrix", jobId, "Output_Origins", family);
            var echoedOrigin = echoed.RootElement.GetProperty("value").GetProperty("features")[0].GetProperty("attributes");
            echoedOrigin.GetProperty("OBJECTID").GetInt64().Should().Be(0);
            echoedOrigin.GetProperty("Name").GetString().Should().Be("O1");
        }

        var straight = new Dictionary<string, string>
        {
            ["Origins"] = PointSet((0, 0, 0, "O1", null)),
            ["Destinations"] = PointSet((0.02, 0.02, 9, "D9", null)),
            ["Origin_Destination_Line_Shape"] = "Straight Line",
            ["outSR"] = "3857",
        };
        using var straightClient = Client(_host);
        var straightJob = await SubmitAsync(straightClient, "GenerateOriginDestinationCostMatrix", straight);
        using var straightTerminal = await UntilTerminalAsync(
            straightClient, "GenerateOriginDestinationCostMatrix", straightJob, "GPServer", SuccessBudget);
        straightTerminal.RootElement.GetProperty("jobStatus").GetString().Should().Be("esriJobSucceeded");
        using var straightLines = await ResultAsync(
            straightClient, "GenerateOriginDestinationCostMatrix", straightJob, "Output_Origin_Destination_Lines");
        var straightValue = straightLines.RootElement.GetProperty("value");
        var straightFeature = straightValue.GetProperty("features");
        straightFeature.GetArrayLength().Should().Be(1);
        var straightAttributes = straightFeature[0].GetProperty("attributes");
        straightAttributes.GetProperty("OriginID").GetInt64().Should().Be(0);
        straightAttributes.GetProperty("DestinationID").GetInt64().Should().Be(9);
        straightAttributes.GetProperty("Total_Time").GetDouble().Should().BeApproximately(
            TravelMinutes(VertexAt(0, 0), VertexAt(0.02, 0.02), minutesPerHop: 1), 0.001);
        var straightPath = straightFeature[0].GetProperty("geometry").GetProperty("paths")[0];
        straightPath.GetArrayLength().Should().Be(2);
        AssertCoordinate(straightPath[0], 0, 0, 1e-4);
        var mercator = ToWebMercator(0.02, 0.02);
        AssertCoordinate(straightPath[1], mercator.X, mercator.Y, 0.05);
        // The submitted outSR is the working output SR for this queued result, so the
        // envelope and the straight-line coordinates are both Web Mercator.
        straightValue.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(3857);
    }

    [RoutingTest("HONUA_ROUTING_TEST")]
    public async Task LocationAllocation_QueuedJob_MatchesLatticeOracle()
    {
        const int cutoffMinutes = 2;
        var facilities = new[]
        {
            (ObjectId: 0L, Name: "West", Vertex: VertexAt(0, 0), X: 0d, Y: 0d),
            (ObjectId: 2L, Name: "East", Vertex: VertexAt(0.02, 0), X: 0.02d, Y: 0d),
        };
        var demands = new[]
        {
            (ObjectId: 0L, Name: "NW", Weight: 3d, Vertex: VertexAt(0, 0.02), X: 0d, Y: 0.02d),
            (ObjectId: 17L, Name: "NE", Weight: 4d, Vertex: VertexAt(0.02, 0.02), X: 0.02d, Y: 0.02d),
        };
        var inputs = new Dictionary<string, string>
        {
            ["Facilities"] = PointSet(
                (facilities[0].X, facilities[0].Y, facilities[0].ObjectId, facilities[0].Name, null),
                (facilities[1].X, facilities[1].Y, facilities[1].ObjectId, facilities[1].Name, null)),
            ["Demand_Points"] = PointSet(
                (demands[0].X, demands[0].Y, demands[0].ObjectId, demands[0].Name, demands[0].Weight),
                (demands[1].X, demands[1].Y, demands[1].ObjectId, demands[1].Name, demands[1].Weight)),
            ["Problem_Type"] = "Minimize Facilities",
            ["Default_Measurement_Cutoff"] = cutoffMinutes.ToString(CultureInfo.InvariantCulture),
            ["Number_of_Facilities_to_Find"] = "1",
        };

        foreach (var family in new[] { "GPServer", "NAServer" })
        {
            using var client = Client(_host);
            var jobId = await SubmitAsync(client, "SolveLocationAllocation", inputs, family);
            using var terminal = await UntilTerminalAsync(client, "SolveLocationAllocation", jobId, family, SuccessBudget);
            terminal.RootElement.GetProperty("jobStatus").GetString().Should().Be("esriJobSucceeded", terminal.RootElement.GetRawText());
            AssertNamedResults(terminal.RootElement, "Output_Allocation_Lines", "Output_Facilities", "Output_Demand_Points", "Solve_Succeeded");
            await AssertSucceededFlagAsync(client, "SolveLocationAllocation", jobId, family);
            await AssertArtifactCountAsync(jobId, _host, 4);

            using var lines = await ResultAsync(client, "SolveLocationAllocation", jobId, "Output_Allocation_Lines", family);
            var lineFeatures = lines.RootElement.GetProperty("value").GetProperty("features");
            lineFeatures.GetArrayLength().Should().Be(demands.Length);
            foreach (var demand in demands)
            {
                var feasible = facilities
                    .Select(facility => (facility, Minutes: TravelMinutes(facility.Vertex, demand.Vertex, minutesPerHop: 1)))
                    .Where(candidate => candidate.Minutes <= cutoffMinutes)
                    .ToArray();
                feasible.Should().ContainSingle(
                    $"demand {demand.Name} has one lattice facility inside the {cutoffMinutes} minute cutoff");
                var chosen = feasible[0];
                var line = RequirePair(lineFeatures, "DemandID", demand.ObjectId, "FacilityID", chosen.facility.ObjectId);
                var attributes = line.GetProperty("attributes");
                attributes.GetProperty("Name").GetString().Should().Be($"{chosen.facility.Name} - {demand.Name}");
                attributes.GetProperty("Weight").GetDouble().Should().BeApproximately(demand.Weight, 0.001);
                attributes.GetProperty("Total_Minutes").GetDouble().Should().BeApproximately(chosen.Minutes, 0.001);
                attributes.GetProperty("TotalWeighted_Minutes").GetDouble().Should().BeApproximately(chosen.Minutes * demand.Weight, 0.001);
                var path = line.GetProperty("geometry").GetProperty("paths")[0];
                path.GetArrayLength().Should().Be(2);
                AssertCoordinate(path[0], chosen.facility.X, chosen.facility.Y, DegreeTolerance);
                AssertCoordinate(path[1], demand.X, demand.Y, DegreeTolerance);
            }

            using var facilityResult = await ResultAsync(client, "SolveLocationAllocation", jobId, "Output_Facilities", family);
            var facilityFeatures = facilityResult.RootElement.GetProperty("value").GetProperty("features");
            facilityFeatures.GetArrayLength().Should().Be(facilities.Length);
            foreach (var facility in facilities)
            {
                var echoed = RequireFeature(facilityFeatures, "OBJECTID", facility.ObjectId).GetProperty("attributes");
                echoed.GetProperty("Name").GetString().Should().Be(facility.Name);
                echoed.GetProperty("FacilityType").GetInt32().Should().Be(3);
            }

            using var demandResult = await ResultAsync(client, "SolveLocationAllocation", jobId, "Output_Demand_Points", family);
            var demandFeatures = demandResult.RootElement.GetProperty("value").GetProperty("features");
            foreach (var demand in demands)
            {
                var feasible = facilities.Single(facility =>
                    TravelMinutes(facility.Vertex, demand.Vertex, minutesPerHop: 1) <= cutoffMinutes);
                var echoed = RequireFeature(demandFeatures, "OBJECTID", demand.ObjectId).GetProperty("attributes");
                echoed.GetProperty("DemandOID").GetInt64().Should().Be(demand.ObjectId);
                echoed.GetProperty("FacilityOID").GetInt64().Should().Be(feasible.ObjectId);
                echoed.GetProperty("AllocatedWeight").GetDouble().Should().BeApproximately(demand.Weight, 0.001);
            }
        }
    }

    [RoutingTest("HONUA_ROUTING_TEST")]
    public async Task UnsupportedSettings_FailWithoutFabricatedResults()
    {
        var incidents = PointSet((0, 0, 0, "SW", null));
        var facilities = PointSet((0.01, 0, 17, "East", null));
        var origins = PointSet((0, 0, 0, "O1", null));
        var destinations = PointSet((0.02, 0.02, 9, "D9", null));
        var demands = PointSet((0, 0.02, 0, "NW", 1));
        var durableFailures = new (string Task, string Output, Dictionary<string, string> Inputs, string Fragment)[]
        {
            (
                "FindClosestFacilities",
                "Output_Routes",
                new() { ["Incidents"] = incidents, ["Facilities"] = facilities, ["Travel_Mode"] = "cycling" },
                "not supported by the configured routing provider"),
            (
                "GenerateOriginDestinationCostMatrix",
                "Output_Origin_Destination_Lines",
                new()
                {
                    ["Origins"] = origins,
                    ["Destinations"] = destinations,
                    ["Travel_Mode"] = """{"name":"driving","useHierarchy":true}""",
                },
                "useHierarchy"),
        };
        // Choice lists are enforced before a job exists. The truthful result is the
        // submit error itself: no job id and no fabricated features.
        var rejectedAtSubmit = new (string Task, Dictionary<string, string> Inputs, string Fragment)[]
        {
            (
                "SolveLocationAllocation",
                new()
                {
                    ["Facilities"] = facilities,
                    ["Demand_Points"] = demands,
                    ["Travel_Direction"] = "Demand to Facility",
                    ["Problem_Type"] = "Minimize Facilities",
                    ["Default_Measurement_Cutoff"] = "2",
                },
                "Parameter 'travel_direction': 'Demand to Facility' is not in the allowed values"),
            (
                "GenerateOriginDestinationCostMatrix",
                new()
                {
                    ["Origins"] = origins,
                    ["Destinations"] = destinations,
                    ["Origin_Destination_Line_Shape"] = "True Shape Along Network",
                },
                "Parameter 'line_shape': 'True Shape Along Network' is not in the allowed values"),
            (
                "FindClosestFacilities",
                new() { ["Incidents"] = incidents, ["Facilities"] = facilities, ["Measurement_Units"] = "Meters" },
                "Parameter 'time_units': 'Meters' is not in the allowed values"),
        };

        using var client = Client(_host);
        foreach (var failure in durableFailures)
        {
            var jobId = await SubmitAsync(client, failure.Task, failure.Inputs);
            using var terminal = await UntilTerminalAsync(client, failure.Task, jobId, "GPServer", SuccessBudget);
            var body = terminal.RootElement.GetRawText();
            terminal.RootElement.GetProperty("jobStatus").GetString().Should().Be("esriJobFailed", body);
            body.Should().Contain(failure.Fragment);
            var durable = (await _host.GetService<IExecutionJobStore>().GetAsync(jobId))!;
            durable.Status.Should().Be(ExecutionJobStatus.Failed);
            durable.AttemptCount.Should().Be(1);
            durable.ArtifactReferences.Should().BeEmpty();
            durable.ErrorMessage.Should().Contain(failure.Fragment);
            await AssertResultIsErrorAsync(client, failure.Task, jobId, failure.Output);
        }

        foreach (var failure in rejectedAtSubmit)
        {
            var text = await PostSubmitAsync(client, failure.Task, failure.Inputs);
            using var body = JsonDocument.Parse(text);
            body.RootElement.TryGetProperty("jobId", out _).Should().BeFalse(text);
            body.RootElement.TryGetProperty("error", out var error).Should().BeTrue(text);
            error.GetProperty("code").GetInt32().Should().Be(400, text);
            var details = string.Join('\n', error.GetProperty("details").EnumerateArray().Select(item => item.GetString()));
            details.Should().Contain(failure.Fragment);
            text.Should().NotContain("\"features\"");
        }
    }

    [RoutingTest("HONUA_ROUTING_TEST")]
    public async Task ProviderFailure_FailsDurablyWithoutPartialResults()
    {
        var connection = DurableConnection(_redis.ConnectionString, database: 1);
        await DeleteControlPlaneKeysAsync(connection, database: 1);
        await using var host = CreateHost(connection, new MissingTableNetworkDatasetResolver(), _routing);
        await host.InitializeAsync();
        using var client = Client(host);
        client.Timeout = TimeSpan.FromMinutes(5);
        var retry = JobRetryPolicy.Default;
        var budget = SuccessBudget + retry.ComputeDelay(2) + retry.ComputeDelay(3);
        var cases = new (string Task, string First, string Second, string Output)[]
        {
            ("FindClosestFacilities", "Incidents", "Facilities", "Output_Routes"),
            ("GenerateOriginDestinationCostMatrix", "Origins", "Destinations", "Output_Origin_Destination_Lines"),
            ("SolveLocationAllocation", "Facilities", "Demand_Points", "Output_Allocation_Lines"),
        };

        try
        {
            foreach (var failure in cases)
            {
                var inputs = new Dictionary<string, string>
                {
                    [failure.First] = PointSet((0, 0, 0, "A", null)),
                    [failure.Second] = PointSet((0.01, 0, 17, "B", failure.Task == "SolveLocationAllocation" ? 1 : null)),
                };
                if (failure.Task == "SolveLocationAllocation")
                {
                    inputs["Problem_Type"] = "Minimize Facilities";
                    inputs["Default_Measurement_Cutoff"] = "2";
                }

                var jobId = await SubmitAsync(client, failure.Task, inputs);
                using var terminal = await UntilTerminalAsync(client, failure.Task, jobId, "GPServer", budget);
                var body = terminal.RootElement.GetRawText();
                terminal.RootElement.GetProperty("jobStatus").GetString().Should().Be("esriJobFailed", body);
                AssertNoProviderLeak(body);
                var durable = (await host.GetService<IExecutionJobStore>().GetAsync(jobId))!;
                durable.Status.Should().Be(ExecutionJobStatus.Failed);
                durable.AttemptCount.Should().Be(retry.MaxAttempts);
                durable.ErrorMessage.Should().Be("Job execution failed.");
                durable.CurrentPhase.Should().Be("Failed (abandoned)");
                durable.ArtifactReferences.Should().BeEmpty();
                AssertNoProviderLeak(durable.ErrorMessage);
                await AssertResultIsErrorAsync(client, failure.Task, jobId, failure.Output);
            }
        }
        finally
        {
            await DeleteControlPlaneKeysAsync(connection, database: 1);
        }
    }

    private static WebAppFixture CreateHost(string redisConnection, INetworkDatasetResolver resolver, PgRoutingFixture routing)
        => new WebAppFixture()
            .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:redis"] = redisConnection })))
            .ConfigureServices(services =>
            {
                services.RemoveAll<IRoutingProvider>();
                services.AddScoped<IRoutingProvider>(_ => new PgRoutingProvider(
                    new FixtureDatabaseSessionFactory(
                        new FixtureDatabaseConnectionProvider(routing.DataSource, routing.ConnectionString)),
                    NullLogger<PgRoutingProvider>.Instance,
                    resolver,
                    Options.Create(new RoutingConfiguration())));
                services.RemoveAll<IConnectionMultiplexer>();
                services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnection));
                services.RemoveAll<IExecutionJobStore>();
                services.AddSingleton<IExecutionJobStore>(sp => new RedisExecutionJobStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    sp.GetRequiredService<ILogger<RedisExecutionJobStore>>()));
                services.RemoveAll<IGeoprocessingResultPackageStore>();
                services.AddSingleton<IGeoprocessingResultPackageStore>(sp => new RedisGeoprocessingResultPackageStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    sp.GetRequiredService<IOptionsMonitor<GeoprocessingExecutorOptions>>(),
                    sp.GetRequiredService<ILogger<RedisGeoprocessingResultPackageStore>>()));
                services.RemoveAll<RedisJobQueue>();
                services.RemoveAll<IJobQueue>();
                services.RemoveAll<IQueueClaimReconciler>();
                services.AddSingleton<RedisJobQueue>(sp => new RedisJobQueue(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    sp.GetRequiredService<IExecutionJobStore>(),
                    sp.GetRequiredService<ILogger<RedisJobQueue>>()));
                services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<RedisJobQueue>());
                services.AddSingleton<IQueueClaimReconciler>(sp => sp.GetRequiredService<RedisJobQueue>());
                services.RemoveAll<IExecutionLogStore>();
                services.AddSingleton<IExecutionLogStore>(sp => new RedisExecutionLogStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(),
                    sp.GetRequiredService<ILogger<RedisExecutionLogStore>>()));
                services.AddJobWorker();
            });

    private sealed class MissingTableNetworkDatasetResolver : INetworkDatasetResolver
    {
        private static readonly NetworkDataset Dataset = NetworkDataset.Default with
        {
            EdgeTable = "public.missing_ways",
            VertexTable = "public.missing_vertices",
        };

        public Task<NetworkDataset?> ResolveAsync(string datasetId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<NetworkDataset?>(
                string.Equals(datasetId, NetworkDataset.DefaultId, StringComparison.OrdinalIgnoreCase) ? Dataset : null);
        }
    }

    private static HttpClient Client(WebAppFixture host)
    {
        var client = host.CreateAdminClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        return client;
    }

    private static string DurableConnection(string connectionString, int? database)
    {
        var connection = connectionString + ",syncTimeout=30000,asyncTimeout=30000";
        return database is { } index
            ? connection + ",defaultDatabase=" + index.ToString(CultureInfo.InvariantCulture)
            : connection;
    }

    private static async Task DeleteControlPlaneKeysAsync(string connectionString, int database)
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var server = multiplexer.GetServer(multiplexer.GetEndPoints()[0]);
        var keys = server.Keys(database: database, pattern: "controlplane:*").ToArray();
        if (keys.Length > 0)
        {
            await multiplexer.GetDatabase(database).KeyDeleteAsync(keys);
        }
    }

    private static async Task<string> SubmitAsync(
        HttpClient client, string task, Dictionary<string, string> inputs, string family = "GPServer")
    {
        var text = await PostSubmitAsync(client, task, inputs, family);
        using var body = JsonDocument.Parse(text);
        body.RootElement.TryGetProperty("jobId", out var jobId).Should().BeTrue(text);
        return jobId.GetString()!;
    }

    private static async Task<string> PostSubmitAsync(
        HttpClient client, string task, Dictionary<string, string> inputs, string family = "GPServer")
    {
        inputs["f"] = "json";
        using var content = new FormUrlEncodedContent(inputs);
        using var response = await client.PostAsync(
            $"/rest/services/{ServiceId}/{family}/{task}/submitJob", content);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return text;
    }

    private static async Task<JsonDocument> ResultAsync(
        HttpClient client, string task, string jobId, string output, string family = "GPServer")
    {
        using var response = await client.GetAsync(ResultPath(task, jobId, output, family));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text);
    }

    private static async Task AssertResultIsErrorAsync(HttpClient client, string task, string jobId, string output)
    {
        using var response = await client.GetAsync(ResultPath(task, jobId, output));
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        body.RootElement.TryGetProperty("error", out _).Should().BeTrue(text);
        body.RootElement.TryGetProperty("value", out _).Should().BeFalse(text);
        text.Should().NotContain("\"features\"");
    }

    private static async Task AssertSucceededFlagAsync(HttpClient client, string task, string jobId, string family)
    {
        using var succeeded = await ResultAsync(client, task, jobId, "Solve_Succeeded", family);
        succeeded.RootElement.GetProperty("dataType").GetString().Should().Be("GPBoolean");
        succeeded.RootElement.GetProperty("value").GetBoolean().Should().BeTrue();
    }

    private static async Task AssertArtifactCountAsync(string jobId, WebAppFixture host, int count)
    {
        var job = (await host.GetService<IExecutionJobStore>().GetAsync(jobId))!;
        job.Status.Should().Be(ExecutionJobStatus.Succeeded);
        job.ArtifactReferences.Should().HaveCount(count);
    }

    private static async Task<JsonDocument> UntilTerminalAsync(
        HttpClient client, string task, string jobId, string family, TimeSpan budget)
    {
        var statusUrl = $"/rest/services/{ServiceId}/{family}/{task}/jobs/{jobId}?f=json";
        var elapsed = Stopwatch.StartNew();
        string? lastObserved = null;
        while (elapsed.Elapsed < budget)
        {
            using var response = await client.GetAsync(statusUrl);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            lastObserved = body;
            using var document = JsonDocument.Parse(body);
            var status = document.RootElement.GetProperty("jobStatus").GetString();
            if (status is "esriJobSucceeded" or "esriJobFailed" or "esriJobCancelled" or "esriJobTimedOut")
            {
                return JsonDocument.Parse(body);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException(
            $"Routing job '{jobId}' was not terminal within {budget.TotalSeconds:F0}s. Last status: {lastObserved ?? "none"}.");
    }

    private static void AssertNamedResults(JsonElement status, params string[] names)
    {
        var results = status.GetProperty("results");
        results.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(names);
        foreach (var name in names)
        {
            results.GetProperty(name).GetProperty("paramUrl").GetString().Should().Be("results/" + name);
        }
    }

    private static void AssertNoProviderLeak(string? text)
    {
        text.Should().NotBeNullOrWhiteSpace();
        text.Should().NotContain("missing_ways");
        text.Should().NotContain("Npgsql");
        text.Should().NotContain("42P01");
        text.Should().NotContain("SELECT");
    }

    private static string ResultPath(string task, string jobId, string output, string family = "GPServer")
        => $"/rest/services/{ServiceId}/{family}/{task}/jobs/{jobId}/results/{output}?f=json";

    private static string PointSet(params (double X, double Y, long ObjectId, string Name, double? Weight)[] features)
    {
        var builder = new StringBuilder();
        builder.Append("""{"geometryType":"esriGeometryPoint","spatialReference":{"wkid":4326},"features":[""");
        for (var index = 0; index < features.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            var feature = features[index];
            builder.Append(CultureInfo.InvariantCulture, $"{{\"geometry\":{{\"x\":{feature.X},\"y\":{feature.Y}}},\"attributes\":{{\"OBJECTID\":{feature.ObjectId},\"Name\":\"{feature.Name}\"");
            if (feature.Weight is { } weight)
            {
                builder.Append(CultureInfo.InvariantCulture, $",\"Weight\":{weight.ToString(CultureInfo.InvariantCulture)}");
            }

            builder.Append("}}");
        }

        builder.Append("]}");
        return builder.ToString();
    }

    private static JsonElement RequireFeature(JsonElement features, string attribute, long id)
    {
        foreach (var feature in features.EnumerateArray())
        {
            if (feature.GetProperty("attributes").GetProperty(attribute).GetInt64() == id)
            {
                return feature;
            }
        }

        throw new InvalidOperationException($"No feature with {attribute}={id}.");
    }

    private static JsonElement RequirePair(JsonElement features, string firstName, long firstId, string secondName, long secondId)
    {
        foreach (var feature in features.EnumerateArray())
        {
            var attributes = feature.GetProperty("attributes");
            if (attributes.GetProperty(firstName).GetInt64() == firstId
                && attributes.GetProperty(secondName).GetInt64() == secondId)
            {
                return feature;
            }
        }

        throw new InvalidOperationException($"No feature with {firstName}={firstId} and {secondName}={secondId}.");
    }

    private static void AssertCoordinate(JsonElement point, double x, double y, double tolerance)
    {
        point[0].GetDouble().Should().BeApproximately(x, tolerance);
        point[1].GetDouble().Should().BeApproximately(y, tolerance);
    }

    private readonly record struct LatticeVertex(int Id, double Lon, double Lat);

    private static readonly LatticeVertex[] Lattice =
    [
        new(1, 0, 0), new(2, 0.01, 0), new(3, 0.02, 0),
        new(4, 0, 0.01), new(5, 0.01, 0.01), new(6, 0.02, 0.01),
        new(7, 0, 0.02), new(8, 0.01, 0.02), new(9, 0.02, 0.02),
    ];

    private static int VertexAt(double lon, double lat)
        => Lattice.Single(vertex => vertex.Lon == lon && vertex.Lat == lat).Id;

    /// <summary>Orthogonal 3x3 lattice. Each hop costs <paramref name="minutesPerHop"/> minutes.</summary>
    private static int TravelMinutes(int from, int to, int minutesPerHop)
    {
        var distance = new Dictionary<int, int> { [from] = 0 };
        var queue = new PriorityQueue<int, int>();
        queue.Enqueue(from, 0);
        while (queue.TryDequeue(out var current, out var hops))
        {
            if (hops != distance[current])
            {
                continue;
            }

            if (current == to)
            {
                return hops * minutesPerHop;
            }

            var column = (current - 1) % 3;
            var row = (current - 1) / 3;
            Consider(current - 1, column > 0);
            Consider(current + 1, column < 2);
            Consider(current - 3, row > 0);
            Consider(current + 3, row < 2);

            void Consider(int next, bool linked)
            {
                if (!linked)
                {
                    return;
                }

                var nextHops = hops + 1;
                if (!distance.TryGetValue(next, out var known) || nextHops < known)
                {
                    distance[next] = nextHops;
                    queue.Enqueue(next, nextHops);
                }
            }
        }

        throw new InvalidOperationException($"No lattice path from {from} to {to}.");
    }

    /// <summary>WGS84 Vincenty inverse. Independent of PostGIS <c>ST_Length(geography)</c>.</summary>
    private static double VincentyMeters(double lon1Deg, double lat1Deg, double lon2Deg, double lat2Deg)
    {
        const double a = 6378137.0;
        const double f = 1.0 / 298.257223563;
        var b = a * (1 - f);
        var phi1 = lat1Deg * Math.PI / 180.0;
        var phi2 = lat2Deg * Math.PI / 180.0;
        var longitudeDelta = (lon2Deg - lon1Deg) * Math.PI / 180.0;
        var u1 = Math.Atan((1 - f) * Math.Tan(phi1));
        var u2 = Math.Atan((1 - f) * Math.Tan(phi2));
        var sinU1 = Math.Sin(u1);
        var cosU1 = Math.Cos(u1);
        var sinU2 = Math.Sin(u2);
        var cosU2 = Math.Cos(u2);
        var lambda = longitudeDelta;
        double sinSigma = 0;
        double cosSigma = 0;
        double sigma = 0;
        double cosSqAlpha = 0;
        double cos2SigmaM = 0;
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var sinLambda = Math.Sin(lambda);
            var cosLambda = Math.Cos(lambda);
            sinSigma = Math.Sqrt(
                Math.Pow(cosU2 * sinLambda, 2)
                + Math.Pow((cosU1 * sinU2) - (sinU1 * cosU2 * cosLambda), 2));
            if (sinSigma == 0)
            {
                return 0;
            }

            cosSigma = (sinU1 * sinU2) + (cosU1 * cosU2 * cosLambda);
            sigma = Math.Atan2(sinSigma, cosSigma);
            var sinAlpha = cosU1 * cosU2 * sinLambda / sinSigma;
            cosSqAlpha = 1 - (sinAlpha * sinAlpha);
            cos2SigmaM = cosSqAlpha == 0 ? 0 : cosSigma - (2 * sinU1 * sinU2 / cosSqAlpha);
            var c = f / 16 * cosSqAlpha * (2 + (f * (4 - (3 * cosSqAlpha))));
            var nextLambda = longitudeDelta + ((1 - c) * f * sinAlpha
                * (sigma + (c * sinSigma * (cos2SigmaM + (c * cosSigma * (-1 + (2 * cos2SigmaM * cos2SigmaM)))))));
            if (Math.Abs(nextLambda - lambda) < 1e-12)
            {
                lambda = nextLambda;
                break;
            }

            lambda = nextLambda;
        }

        var uSq = cosSqAlpha * ((a * a) - (b * b)) / (b * b);
        var coefficientA = 1 + (uSq / 16384 * (4096 + (uSq * (-768 + (uSq * (320 - (175 * uSq)))))));
        var coefficientB = uSq / 1024 * (256 + (uSq * (-128 + (uSq * (74 - (47 * uSq))))));
        var deltaSigma = coefficientB * sinSigma * (cos2SigmaM + (coefficientB / 4
            * ((cosSigma * (-1 + (2 * cos2SigmaM * cos2SigmaM)))
                - (coefficientB / 6 * cos2SigmaM * (-3 + (4 * sinSigma * sinSigma)) * (-3 + (4 * cos2SigmaM * cos2SigmaM))))));
        return b * coefficientA * (sigma - deltaSigma);
    }

    /// <summary>Spherical Web Mercator used by EPSG:3857, radius 6378137.</summary>
    private static (double X, double Y) ToWebMercator(double lonDeg, double latDeg)
    {
        const double radius = 6378137.0;
        var x = radius * lonDeg * Math.PI / 180.0;
        var y = radius * Math.Log(Math.Tan((Math.PI / 4.0) + (latDeg * Math.PI / 360.0)));
        return (x, y);
    }
}
