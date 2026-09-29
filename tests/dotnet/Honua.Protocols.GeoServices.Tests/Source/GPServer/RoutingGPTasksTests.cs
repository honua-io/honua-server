// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.ControlPlane;
using Honua.Protocols.GeoServices.GPServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

public sealed class RoutingGPTasksTests
{
    [UnitTheory]
    [InlineData("routing.route", "esriGeometryPolyline", new[] { "OBJECTID", "Total_Length", "Total_TravelTime" })]
    [InlineData("routing.service-area", "esriGeometryPolygon", new[] { "OBJECTID", "FacilityID", "FromBreak", "ToBreak" })]
    public void EmptyRoutingResult_PreservesClientFeatureClassSchema(string processId, string geometryType, string[] fieldNames)
    {
        var job = new ExecutionJobRecord
        {
            OperationId = "empty-routing-result",
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:routing",
                Parameters = new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId,
                },
            },
        };
        var artifact = "data:application/geo+json;base64," + Convert.ToBase64String(
            Encoding.UTF8.GetBytes("{\"type\":\"FeatureCollection\",\"features\":[]}"));

        var result = RoutingGPTasks.TranslateResult(job, ArtifactKind.FeatureLayer, artifact, 3857, null);

        result.GetProperty("geometryType").GetString().Should().Be(geometryType);
        result.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(3857);
        result.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("name").GetString())
            .Should().Equal(fieldNames);
        result.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("type").GetString())
            .Should().Equal(processId == "routing.route"
                ? ["esriFieldTypeOID", "esriFieldTypeDouble", "esriFieldTypeDouble"]
                : ["esriFieldTypeOID", "esriFieldTypeInteger", "esriFieldTypeDouble", "esriFieldTypeDouble"]);
        result.GetProperty("objectIdFieldName").GetString().Should().Be("OBJECTID");
        result.GetProperty("features").GetArrayLength().Should().Be(0);
    }

    [UnitTest]
    public void ServiceAreaResult_PreservesIntegerFacilityIdentity()
    {
        var job = new ExecutionJobRecord
        {
            OperationId = "service-area-result",
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:routing",
                Parameters = new Dictionary<string, string>
                {
                    [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = "routing.service-area",
                },
            },
        };
        var artifact = "data:application/geo+json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("""
            {"type":"FeatureCollection","features":[{"type":"Feature",
              "geometry":{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]},
              "properties":{"facilityId":7,"fromBreak":0,"toBreak":5}}]}
            """));

        var result = RoutingGPTasks.TranslateResult(job, ArtifactKind.FeatureLayer, artifact, 4326, null);

        result.GetProperty("fields").EnumerateArray()
            .Single(field => field.GetProperty("name").GetString() == "FacilityID")
            .GetProperty("type").GetString().Should().Be("esriFieldTypeInteger");
        result.GetProperty("features")[0].GetProperty("attributes").GetProperty("FacilityID").GetInt32().Should().Be(7);
    }
}
