// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Server.Features.ControlPlane;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// End-to-end parity coverage for the slice-2 through slice-5 vector
/// executors. Each test submits a process through the OGC API Processes
/// execute endpoint, polls the durable runtime until the job reaches a
/// terminal status, retrieves the result document, and compares the
/// published GeoJSON Feature (FeatureLayer outputs), FeatureCollection
/// (geometry.dissolve), or measure-result document (Scalar outputs)
/// against a golden expectation (geometry type, feature count, area /
/// coordinate position, measure value). Mirrors the slice-1 buffer
/// integration test shape so the new processes pin the same evidence
/// contract.
/// </summary>
[Collection("Redis")]
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class VectorProcessParityIntegrationTests(RedisFixture redis)
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task ClipProcess_CompletesAndReturnsClippedSquare()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            var target = BuildBoxPolygon(0, 0, 10, 10);
            var clipEnvelope = BuildBoxPolygon(5, 5, 15, 15);
            var body = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"response\":\"document\",\"inputs\":{{\"targetWkb\":\"{0}\",\"clipEnvelopeWkb\":\"{1}\",\"srid\":4326}}}}",
                WkbBase64(target),
                WkbBase64(clipEnvelope));

            var jobId = await SubmitJobAsync(client, "geometry.clip", body);

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");

            using var resultsDoc = await GetResultsAsync(client, jobId);
            var feature = DecodeOutputFeature(resultsDoc, "geometry.clip");

            feature.GetProperty("properties").GetProperty("inputSrid").GetInt32().Should().Be(4326);

            var geometry = ReadGeometry(feature.GetProperty("geometry"));
            geometry.Area.Should().BeApproximately(25.0, 1e-6,
                "clip of (0..10) by (5..15) envelope yields a 5x5 square");

            var resultStore = fixture.GetService<IGeoprocessingResultPackageStore>();
            var package = await resultStore.GetAsync(jobId);
            package.Should().NotBeNull();
            package!.Status.Should().Be(GeoprocessingWorkflowStatus.Completed);
            package.Artifacts.Should().ContainSingle();
            package.Artifacts[0].Uri.Should().StartWith(DataUriPrefix);
            package.Artifacts[0].Label.Should().Be("outputFeatureLayer");
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task IntersectProcess_CompletesAndReturnsOverlapSquare()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            var target = BuildBoxPolygon(0, 0, 10, 10);
            var intersector = BuildBoxPolygon(5, 5, 15, 15);
            var body = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"response\":\"document\",\"inputs\":{{\"targetWkb\":\"{0}\",\"intersectorWkb\":\"{1}\",\"srid\":4326}}}}",
                WkbBase64(target),
                WkbBase64(intersector));

            var jobId = await SubmitJobAsync(client, "geometry.intersect", body);

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");

            using var resultsDoc = await GetResultsAsync(client, jobId);
            var feature = DecodeOutputFeature(resultsDoc, "geometry.intersect");

            var geometry = ReadGeometry(feature.GetProperty("geometry"));
            geometry.Area.Should().BeApproximately(25.0, 1e-6,
                "overlap of two 10x10 boxes offset by (5,5) is a 5x5 square");
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task ProjectProcess_IdentityTransform_RoundTripsCoordinates()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            // POINT(1 2) in WGS 84 — identity projection back to WGS 84.
            var point = NetTopologySuite.NtsGeometryServices.Instance
                .CreateGeometryFactory(srid: 4326)
                .CreatePoint(new Coordinate(1.0, 2.0));

            var body = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"response\":\"document\",\"inputs\":{{\"wkb\":\"{0}\",\"fromSrid\":4326,\"toSrid\":4326}}}}",
                WkbBase64(point));

            var jobId = await SubmitJobAsync(client, "geometry.project", body);

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");

            using var resultsDoc = await GetResultsAsync(client, jobId);
            var feature = DecodeOutputFeature(resultsDoc, "geometry.project");

            feature.GetProperty("properties").GetProperty("fromSrid").GetInt32().Should().Be(4326);
            feature.GetProperty("properties").GetProperty("toSrid").GetInt32().Should().Be(4326);

            var geometry = ReadGeometry(feature.GetProperty("geometry"));
            ((Point)geometry).X.Should().BeApproximately(1.0, 1e-9);
            ((Point)geometry).Y.Should().BeApproximately(2.0, 1e-9);
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task AreaProcess_CompletesAndReturnsPlanarMeasure()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            // 10x10 box has planar area 100 in input-CRS units squared.
            var polygon = BuildBoxPolygon(0, 0, 10, 10);
            var body = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"response\":\"document\",\"inputs\":{{\"wkb\":\"{0}\",\"srid\":4326}}}}",
                WkbBase64(polygon));

            var jobId = await SubmitJobAsync(client, "geometry.area", body);

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");

            using var resultsDoc = await GetResultsAsync(client, jobId);
            var measure = DecodeScalarMeasure(resultsDoc, "geometry.area");
            measure.GetProperty("measure").GetString().Should().Be("area");
            measure.GetProperty("value").GetDouble().Should().BeApproximately(100.0, 1e-9);
            measure.GetProperty("unit").GetString().Should().Be("input-crs-units-squared");
            measure.GetProperty("inputSrid").GetInt32().Should().Be(4326);
            measure.GetProperty("inputGeometryType").GetString().Should().Be("Polygon");

            var resultStore = fixture.GetService<IGeoprocessingResultPackageStore>();
            var package = await resultStore.GetAsync(jobId);
            package.Should().NotBeNull();
            package!.Status.Should().Be(GeoprocessingWorkflowStatus.Completed);
            package.Artifacts.Should().ContainSingle();
            package.Artifacts[0].Uri.Should().StartWith("data:application/json;base64,");
            package.Artifacts[0].Label.Should().Be("outputScalar");
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task UnionProcess_CompletesAndReturnsAggregatePolygon()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            // Two overlapping 10x10 boxes offset by (5,5) → area 100+100-25 = 175.
            var first = BuildBoxPolygon(0, 0, 10, 10);
            var second = BuildBoxPolygon(5, 5, 15, 15);
            var body = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"response\":\"document\",\"inputs\":{{\"wkbs\":[\"{0}\",\"{1}\"],\"srid\":4326}}}}",
                WkbBase64(first),
                WkbBase64(second));

            var jobId = await SubmitJobAsync(client, "geometry.union", body);

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");

            using var resultsDoc = await GetResultsAsync(client, jobId);
            var feature = DecodeOutputFeature(resultsDoc, "geometry.union");

            feature.GetProperty("properties").GetProperty("inputSrid").GetInt32().Should().Be(4326);
            feature.GetProperty("properties").GetProperty("inputCount").GetInt32().Should().Be(2);

            var geometry = ReadGeometry(feature.GetProperty("geometry"));
            geometry.Area.Should().BeApproximately(175.0, 1e-6,
                "union of two 10x10 boxes overlapping in a 5x5 square has area 175");

            var resultStore = fixture.GetService<IGeoprocessingResultPackageStore>();
            var package = await resultStore.GetAsync(jobId);
            package.Should().NotBeNull();
            package!.Status.Should().Be(GeoprocessingWorkflowStatus.Completed);
            package.Artifacts.Should().ContainSingle();
            package.Artifacts[0].Uri.Should().StartWith(DataUriPrefix);
            package.Artifacts[0].Label.Should().Be("outputFeatureLayer");
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task CentroidProcess_CompletesAndReturnsCenterPoint()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            // Centroid of a 10x10 box anchored at (0,0) is (5,5).
            var polygon = BuildBoxPolygon(0, 0, 10, 10);
            var body = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"response\":\"document\",\"inputs\":{{\"wkb\":\"{0}\",\"srid\":4326}}}}",
                WkbBase64(polygon));

            var jobId = await SubmitJobAsync(client, "geometry.centroid", body);

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");

            using var resultsDoc = await GetResultsAsync(client, jobId);
            var feature = DecodeOutputFeature(resultsDoc, "geometry.centroid");

            feature.GetProperty("properties").GetProperty("inputSrid").GetInt32().Should().Be(4326);
            feature.GetProperty("properties").GetProperty("inputGeometryType").GetString().Should().Be("Polygon");

            var geometry = ReadGeometry(feature.GetProperty("geometry"));
            geometry.Should().BeOfType<Point>();
            var point = (Point)geometry;
            point.X.Should().BeApproximately(5.0, 1e-9);
            point.Y.Should().BeApproximately(5.0, 1e-9);

            var resultStore = fixture.GetService<IGeoprocessingResultPackageStore>();
            var package = await resultStore.GetAsync(jobId);
            package.Should().NotBeNull();
            package!.Status.Should().Be(GeoprocessingWorkflowStatus.Completed);
            package.Artifacts.Should().ContainSingle();
            package.Artifacts[0].Uri.Should().StartWith(DataUriPrefix);
            package.Artifacts[0].Label.Should().Be("outputFeatureLayer");
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task LengthProcess_CompletesAndReturnsPlanarMeasure()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            // LINESTRING(0 0, 3 0, 3 4) — 3-4-5 right-triangle linestring with planar length 7.
            var factory = NetTopologySuite.NtsGeometryServices.Instance
                .CreateGeometryFactory(srid: 4326);
            var line = factory.CreateLineString(new[]
            {
                new Coordinate(0, 0),
                new Coordinate(3, 0),
                new Coordinate(3, 4),
            });
            var body = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"response\":\"document\",\"inputs\":{{\"wkb\":\"{0}\",\"srid\":4326}}}}",
                WkbBase64(line));

            var jobId = await SubmitJobAsync(client, "geometry.length", body);

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");

            using var resultsDoc = await GetResultsAsync(client, jobId);
            var measure = DecodeScalarMeasure(resultsDoc, "geometry.length");
            measure.GetProperty("measure").GetString().Should().Be("length");
            measure.GetProperty("value").GetDouble().Should().BeApproximately(7.0, 1e-9);
            measure.GetProperty("unit").GetString().Should().Be("input-crs-units");
            measure.GetProperty("inputSrid").GetInt32().Should().Be(4326);
            measure.GetProperty("inputGeometryType").GetString().Should().Be("LineString");

            var resultStore = fixture.GetService<IGeoprocessingResultPackageStore>();
            var package = await resultStore.GetAsync(jobId);
            package.Should().NotBeNull();
            package!.Status.Should().Be(GeoprocessingWorkflowStatus.Completed);
            package.Artifacts.Should().ContainSingle();
            package.Artifacts[0].Uri.Should().StartWith("data:application/json;base64,");
            package.Artifacts[0].Label.Should().Be("outputScalar");
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task ConvexHullProcess_CompletesAndReturnsBoundingPolygon()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            // MultiPoint with four corners of a 10x10 box plus the centroid.
            // Hull is the bounding 10x10 box itself with area 100.
            var factory = NetTopologySuite.NtsGeometryServices.Instance
                .CreateGeometryFactory(srid: 4326);
            var multipoint = factory.CreateMultiPoint(new[]
            {
                factory.CreatePoint(new Coordinate(0, 0)),
                factory.CreatePoint(new Coordinate(10, 0)),
                factory.CreatePoint(new Coordinate(10, 10)),
                factory.CreatePoint(new Coordinate(0, 10)),
                factory.CreatePoint(new Coordinate(5, 5)),
            });
            var body = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"response\":\"document\",\"inputs\":{{\"wkb\":\"{0}\",\"srid\":4326}}}}",
                WkbBase64(multipoint));

            var jobId = await SubmitJobAsync(client, "geometry.convex-hull", body);

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");

            using var resultsDoc = await GetResultsAsync(client, jobId);
            var feature = DecodeOutputFeature(resultsDoc, "geometry.convex-hull");

            feature.GetProperty("properties").GetProperty("inputSrid").GetInt32().Should().Be(4326);
            feature.GetProperty("properties").GetProperty("inputGeometryType").GetString().Should().Be("MultiPoint");

            var geometry = ReadGeometry(feature.GetProperty("geometry"));
            geometry.Area.Should().BeApproximately(100.0, 1e-6,
                "hull of four corners of a 10x10 box plus the centroid is the 10x10 box itself");

            var resultStore = fixture.GetService<IGeoprocessingResultPackageStore>();
            var package = await resultStore.GetAsync(jobId);
            package.Should().NotBeNull();
            package!.Status.Should().Be(GeoprocessingWorkflowStatus.Completed);
            package.Artifacts.Should().ContainSingle();
            package.Artifacts[0].Uri.Should().StartWith(DataUriPrefix);
            package.Artifacts[0].Label.Should().Be("outputFeatureLayer");
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task DissolveProcess_CompletesAndReturnsFeatureCollectionPerGroup()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            // Two overlapping 10x10 boxes share a single group key → one
            // feature with union area 100 + 100 - 25 = 175.
            var first = BuildBoxPolygon(0, 0, 10, 10);
            var second = BuildBoxPolygon(5, 5, 15, 15);
            var body = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"response\":\"document\",\"inputs\":{{\"wkbs\":[\"{0}\",\"{1}\"],\"groupKeys\":[\"a\",\"a\"],\"srid\":4326}}}}",
                WkbBase64(first),
                WkbBase64(second));

            var jobId = await SubmitJobAsync(client, "geometry.dissolve", body);

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");

            using var resultsDoc = await GetResultsAsync(client, jobId);
            var collection = DecodeOutputFeatureCollection(resultsDoc, "geometry.dissolve");
            collection.GetProperty("groupCount").GetInt32().Should().Be(1);
            collection.GetProperty("inputCount").GetInt32().Should().Be(2);

            var features = collection.GetProperty("features");
            features.GetArrayLength().Should().Be(1);
            var feature = features[0];
            feature.GetProperty("properties").GetProperty("groupKey").GetString().Should().Be("a");

            var geometry = ReadGeometry(feature.GetProperty("geometry"));
            geometry.Area.Should().BeApproximately(175.0, 1e-6,
                "two overlapping 10x10 boxes under one group dissolve to area 175");

            var resultStore = fixture.GetService<IGeoprocessingResultPackageStore>();
            var package = await resultStore.GetAsync(jobId);
            package.Should().NotBeNull();
            package!.Status.Should().Be(GeoprocessingWorkflowStatus.Completed);
            package.Artifacts.Should().ContainSingle();
            package.Artifacts[0].Uri.Should().StartWith(DataUriPrefix);
            package.Artifacts[0].Label.Should().Be("outputFeatureLayer");
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task SimplifyProcess_CollapsesBelowToleranceVertices()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            // LINESTRING(0 0, 5 0.001, 10 0) simplified at tolerance 1.0
            // collapses to LINESTRING(0 0, 10 0).
            var factory = NetTopologySuite.NtsGeometryServices.Instance
                .CreateGeometryFactory(srid: 4326);
            var line = factory.CreateLineString(new[]
            {
                new Coordinate(0, 0),
                new Coordinate(5, 0.001),
                new Coordinate(10, 0),
            });
            var body = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"response\":\"document\",\"inputs\":{{\"wkb\":\"{0}\",\"srid\":4326,\"tolerance\":1.0}}}}",
                WkbBase64(line));

            var jobId = await SubmitJobAsync(client, "geometry.simplify", body);

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");

            using var resultsDoc = await GetResultsAsync(client, jobId);
            var feature = DecodeOutputFeature(resultsDoc, "geometry.simplify");

            feature.GetProperty("properties").GetProperty("inputSrid").GetInt32().Should().Be(4326);
            feature.GetProperty("properties").GetProperty("tolerance").GetDouble().Should().Be(1.0);
            feature.GetProperty("properties").GetProperty("inputGeometryType").GetString().Should().Be("LineString");

            var geometry = ReadGeometry(feature.GetProperty("geometry"));
            geometry.Should().BeOfType<LineString>();
            ((LineString)geometry).NumPoints.Should().Be(2,
                "tolerance 1.0 collapses the 0.001-offset midpoint");

            var resultStore = fixture.GetService<IGeoprocessingResultPackageStore>();
            var package = await resultStore.GetAsync(jobId);
            package.Should().NotBeNull();
            package!.Status.Should().Be(GeoprocessingWorkflowStatus.Completed);
            package.Artifacts.Should().ContainSingle();
            package.Artifacts[0].Uri.Should().StartWith(DataUriPrefix);
            package.Artifacts[0].Label.Should().Be("outputFeatureLayer");
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    [Endpoint("GET /ogc/processes/jobs/{jobId}/results")]
    public async Task SnapProcess_PullsInputOntoReferenceVertex()
    {
        await DeleteControlPlaneKeysAsync(redis.ConnectionString);

        var fixture = BuildFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            // Input POINT(0.1 0.1) snapped to reference POINT(0 0) at
            // tolerance 0.5 lands on the reference origin.
            var factory = NetTopologySuite.NtsGeometryServices.Instance
                .CreateGeometryFactory(srid: 4326);
            var input = factory.CreatePoint(new Coordinate(0.1, 0.1));
            var reference = factory.CreatePoint(new Coordinate(0, 0));

            var body = string.Format(
                CultureInfo.InvariantCulture,
                "{{\"response\":\"document\",\"inputs\":{{\"wkb\":\"{0}\",\"referenceWkb\":\"{1}\",\"srid\":4326,\"tolerance\":0.5}}}}",
                WkbBase64(input),
                WkbBase64(reference));

            var jobId = await SubmitJobAsync(client, "geometry.snap", body);

            using var terminal = await PollUntilTerminalAsync(client, jobId);
            terminal.RootElement.GetProperty("status").GetString().Should().Be("successful");

            using var resultsDoc = await GetResultsAsync(client, jobId);
            var feature = DecodeOutputFeature(resultsDoc, "geometry.snap");

            feature.GetProperty("properties").GetProperty("inputSrid").GetInt32().Should().Be(4326);
            feature.GetProperty("properties").GetProperty("tolerance").GetDouble().Should().Be(0.5);
            feature.GetProperty("properties").GetProperty("inputGeometryType").GetString().Should().Be("Point");

            var geometry = ReadGeometry(feature.GetProperty("geometry"));
            geometry.Should().BeOfType<Point>();
            var snapped = (Point)geometry;
            snapped.X.Should().BeApproximately(0.0, 1e-9);
            snapped.Y.Should().BeApproximately(0.0, 1e-9);

            var resultStore = fixture.GetService<IGeoprocessingResultPackageStore>();
            var package = await resultStore.GetAsync(jobId);
            package.Should().NotBeNull();
            package!.Status.Should().Be(GeoprocessingWorkflowStatus.Completed);
            package.Artifacts.Should().ContainSingle();
            package.Artifacts[0].Uri.Should().StartWith(DataUriPrefix);
            package.Artifacts[0].Label.Should().Be("outputFeatureLayer");
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(redis.ConnectionString);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private WebAppFixture BuildFixture()
        => new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:redis"] = redis.ConnectionString
                    });
                });
            })
            .ConfigureServices(WireDurableRuntime);

    private void WireDurableRuntime(IServiceCollection services)
    {
        services.RemoveAll<IConnectionMultiplexer>();
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis.ConnectionString));

        services.RemoveAll<IExecutionJobStore>();
        services.AddSingleton<IExecutionJobStore>(sp =>
            new RedisExecutionJobStore(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<ILogger<RedisExecutionJobStore>>()));

        services.RemoveAll<IGeoprocessingResultPackageStore>();
        services.AddSingleton<IGeoprocessingResultPackageStore>(sp =>
            new RedisGeoprocessingResultPackageStore(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<ILogger<RedisGeoprocessingResultPackageStore>>()));

        services.RemoveAll<RedisJobQueue>();
        services.RemoveAll<IJobQueue>();
        services.RemoveAll<IQueueClaimReconciler>();
        services.AddSingleton<RedisJobQueue>(sp =>
            new RedisJobQueue(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<IExecutionJobStore>(),
                sp.GetRequiredService<ILogger<RedisJobQueue>>()));
        services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<RedisJobQueue>());
        services.AddSingleton<IQueueClaimReconciler>(sp => sp.GetRequiredService<RedisJobQueue>());

        services.RemoveAll<IExecutionLogStore>();
        services.AddSingleton<IExecutionLogStore>(sp =>
            new RedisExecutionLogStore(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<ILogger<RedisExecutionLogStore>>()));

        // Activate the worker host. The production dispatcher registered by
        // AddGeoprocessing routes geometry.clip / geometry.intersect /
        // geometry.project / geometry.area / geometry.union /
        // geometry.centroid / geometry.length / geometry.convex-hull /
        // geometry.dissolve / geometry.simplify / geometry.snap ids to
        // their per-process executors — that's the system-under-test for
        // this parity suite.
        services.AddJobWorker();
    }

    private static Polygon BuildBoxPolygon(double minX, double minY, double maxX, double maxY)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        return factory.CreatePolygon(new[]
        {
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY),
        });
    }

    private static string WkbBase64(Geometry geometry)
        => Convert.ToBase64String(new WKBWriter().Write(geometry));

    private static async Task<string> SubmitJobAsync(HttpClient client, string processId, string body)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/ogc/processes/processes/{processId}/execution");
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var submit = await client.SendAsync(request);
        submit.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
        var jobId = doc.RootElement.GetProperty("jobID").GetString();
        jobId.Should().NotBeNullOrWhiteSpace();
        return jobId!;
    }

    private static async Task<JsonDocument> PollUntilTerminalAsync(HttpClient client, string jobId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/ogc/processes/jobs/{jobId}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.GetProperty("status").GetString();
            if (status is "successful" or "failed" or "dismissed")
            {
                return JsonDocument.Parse(body);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"Timed out waiting for job '{jobId}' to reach a terminal status.");
    }

    private static async Task<JsonDocument> GetResultsAsync(HttpClient client, string jobId)
    {
        using var response = await client.GetAsync($"/ogc/processes/jobs/{jobId}/results");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement DecodeOutputFeature(JsonDocument resultsDoc, string expectedProcessId)
    {
        var output = resultsDoc.RootElement.GetProperty("outputFeatureLayer");
        output.GetProperty("kind").GetString().Should().Be("FeatureLayer");
        output.GetProperty("type").GetString().Should().Be("application/geo+json");

        var href = output.GetProperty("href").GetString();
        href.Should().NotBeNull();
        href!.Should().StartWith(DataUriPrefix);

        var base64 = href[DataUriPrefix.Length..];
        var bytes = Convert.FromBase64String(base64);
        var doc = JsonDocument.Parse(bytes);
        var feature = doc.RootElement.Clone();
        feature.GetProperty("type").GetString().Should().Be("Feature");
        feature.GetProperty("properties").GetProperty("processId").GetString()
            .Should().Be(expectedProcessId);
        return feature;
    }

    private static JsonElement DecodeOutputFeatureCollection(JsonDocument resultsDoc, string expectedProcessId)
    {
        var output = resultsDoc.RootElement.GetProperty("outputFeatureLayer");
        output.GetProperty("kind").GetString().Should().Be("FeatureLayer");
        output.GetProperty("type").GetString().Should().Be("application/geo+json");

        var href = output.GetProperty("href").GetString();
        href.Should().NotBeNull();
        href!.Should().StartWith(DataUriPrefix);

        var base64 = href[DataUriPrefix.Length..];
        var bytes = Convert.FromBase64String(base64);
        var doc = JsonDocument.Parse(bytes);
        var collection = doc.RootElement.Clone();
        collection.GetProperty("type").GetString().Should().Be("FeatureCollection");
        collection.GetProperty("processId").GetString().Should().Be(expectedProcessId);
        return collection;
    }

    private static Geometry ReadGeometry(JsonElement geometryElement)
        => new GeoJsonReader().Read<Geometry>(geometryElement.GetRawText());

    private static JsonElement DecodeScalarMeasure(JsonDocument resultsDoc, string expectedProcessId)
    {
        const string scalarPrefix = "data:application/json;base64,";

        var output = resultsDoc.RootElement.GetProperty("outputScalar");
        output.GetProperty("kind").GetString().Should().Be("Scalar");
        output.GetProperty("type").GetString().Should().Be("application/json");

        var href = output.GetProperty("href").GetString();
        href.Should().NotBeNull();
        href!.Should().StartWith(scalarPrefix);

        var base64 = href[scalarPrefix.Length..];
        var bytes = Convert.FromBase64String(base64);
        var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement.Clone();
        root.GetProperty("type").GetString().Should().Be("MeasureResult");
        root.GetProperty("processId").GetString().Should().Be(expectedProcessId);
        return root;
    }

    private static async Task DeleteControlPlaneKeysAsync(string redisConnectionString)
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
        var database = multiplexer.GetDatabase();
        var endpoints = multiplexer.GetEndPoints();
        if (endpoints.Length == 0)
        {
            return;
        }

        var server = multiplexer.GetServer(endpoints[0]);
        var keys = server.Keys(pattern: "controlplane:*").ToArray();
        if (keys.Length > 0)
        {
            await database.KeyDeleteAsync(keys);
        }
    }
}
