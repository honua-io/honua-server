// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing.Cli;
using Honua.Geoprocessing.CustomCode;
using Xunit;

namespace Honua.Geoprocessing.Cli.Tests;

/// <summary>
/// Runnability (#6217): registration never proves readiness. A native op needs GDAL, a
/// service-backed op needs the integration stack, custom code needs its harness, and the
/// verdict names the first missing prerequisite instead of guessing.
/// </summary>
public sealed class GpRunnabilityTests
{
    private static ProcessDefinition Definition(string id, string runtime = RuntimeProfiles.Managed, string? dependency = null) => new()
    {
        ProcessId = id,
        Title = id,
        Description = "test",
        Category = "test",
        Parameters =
        [
            new ProcessParameterSpec { Name = "wkb", DisplayName = "wkb", Description = "geometry", ValueType = ProcessParameterValueType.Wkb, Required = true },
            new ProcessParameterSpec { Name = "distance", DisplayName = "distance", Description = "d", ValueType = ProcessParameterValueType.FloatingPoint, Required = true, DefaultValue = "10" },
            new ProcessParameterSpec { Name = "mode", DisplayName = "mode", Description = "m", ValueType = ProcessParameterValueType.Text, AllowedValues = ["fast", "exact"] },
            new ProcessParameterSpec { Name = "srid", DisplayName = "srid", Description = "s", ValueType = ProcessParameterValueType.Srid },
        ],
        OutputArtifactKinds = [],
        RuntimeProfile = runtime,
        ConfigurationDependency = dependency,
    };

    [Fact]
    public void Managed_IsAlwaysRunnable()
    {
        var probe = new GpEnvironmentProbe(hostGdalAvailable: false, workerImageAvailable: null, pythonAvailable: false, integrationStackConfigured: false);

        var verdict = GpRunnabilityAssessor.Assess(Definition("geometry.buffer"), "geometry.buffer", null, probe);

        verdict.ExecutionMode.Should().Be(GpExecutionMode.Managed);
        verdict.RunnableHere.Should().BeTrue();
        verdict.BlockedBy.Should().BeNull();
        verdict.SupportedTargets.Should().Contain("local").And.Contain("aws-batch");
    }

    [Fact]
    public void Native_NeedsTheWorkerImageOrHostGdal_AndNamesTheGapOtherwise()
    {
        var definition = Definition("gdal.ogr2ogr", RuntimeProfiles.Native);

        var blocked = GpRunnabilityAssessor.Assess(definition, "gdal.ogr2ogr", RuntimeProfiles.Native,
            new GpEnvironmentProbe(hostGdalAvailable: false, workerImageAvailable: false, pythonAvailable: true, integrationStackConfigured: true));
        blocked.ExecutionMode.Should().Be(GpExecutionMode.Native);
        blocked.RunnableHere.Should().BeFalse();
        blocked.BlockedBy.Should().Be("GDAL (worker image or host CLIs)");
        blocked.Prerequisites[0].Detail.Should().Contain("docker/worker-gdal/Dockerfile");

        var hostCli = GpRunnabilityAssessor.Assess(definition, "gdal.ogr2ogr", RuntimeProfiles.Native,
            new GpEnvironmentProbe(hostGdalAvailable: true, workerImageAvailable: null, pythonAvailable: false, integrationStackConfigured: false));
        hostCli.RunnableHere.Should().BeTrue();
        hostCli.Prerequisites[0].Name.Should().Be("host GDAL CLIs (ogr2ogr, gdal_translate)");
        hostCli.Prerequisites[0].Detail.Should().Contain("not probed");

        var image = GpRunnabilityAssessor.Assess(definition, "gdal.ogr2ogr", RuntimeProfiles.Native,
            new GpEnvironmentProbe(hostGdalAvailable: false, workerImageAvailable: true, pythonAvailable: false, integrationStackConfigured: false, workerImage: "honua-worker-etl:dev"));
        image.RunnableHere.Should().BeTrue();
        image.Prerequisites[0].Name.Should().Be("GDAL worker image (honua-worker-etl:dev)");
    }

    [Fact]
    public void ServiceBacked_NeedsTheIntegrationStack_AndNeverSubmits()
    {
        var definition = Definition("routing.solve", dependency: "Routing:NetworkDatasetId");

        var blocked = GpRunnabilityAssessor.Assess(definition, "routing.solve", null,
            new GpEnvironmentProbe(hostGdalAvailable: true, workerImageAvailable: true, pythonAvailable: true, integrationStackConfigured: false));
        blocked.ExecutionMode.Should().Be(GpExecutionMode.ServiceBacked);
        blocked.RunnableHere.Should().BeFalse();
        blocked.BlockedBy.Should().Be("integration stack (Routing:NetworkDatasetId)");
        blocked.Prerequisites[0].Detail.Should().Contain("never submits to a server");
        blocked.SupportedTargets.Should().NotContain("local");

        GpRunnabilityAssessor.Assess(definition, "routing.solve", null,
            new GpEnvironmentProbe(hostGdalAvailable: false, workerImageAvailable: null, pythonAvailable: false, integrationStackConfigured: true))
            .RunnableHere.Should().BeTrue();
    }

    [Fact]
    public void Custom_NeedsTheLocalHarness()
    {
        var verdict = GpRunnabilityAssessor.Assess(null, "custom.tool", CustomCodeJobContract.RuntimeProfile,
            new GpEnvironmentProbe(hostGdalAvailable: false, workerImageAvailable: null, pythonAvailable: false, integrationStackConfigured: false));

        verdict.ExecutionMode.Should().Be(GpExecutionMode.Custom);
        verdict.RunnableHere.Should().BeFalse();
        verdict.BlockedBy.Should().Be("custom-code local harness");
        verdict.SupportedTargets.Should().Equal("local-harness", "aws-batch");
    }

    [Fact]
    public void ExampleValues_PreferDefault_ThenAllowed_ThenTypeSample()
    {
        var examples = GpRunnabilityAssessor.ExampleValues(Definition("geometry.buffer"));

        examples["distance"]!.GetValue<string>().Should().Be("10");
        examples["mode"]!.GetValue<string>().Should().Be("fast");
        examples["srid"]!.GetValue<string>().Should().Be("4326");
        examples["wkb"]!.GetValue<string>().Should().Be("AQEAAAAAAAAAAAAAAAAAAAAAAAAA");
    }

    [Fact]
    public void WriteTo_EmitsTheStableFieldSet()
    {
        var verdict = GpRunnabilityAssessor.Assess(Definition("geometry.buffer"), "geometry.buffer", null,
            new GpEnvironmentProbe(hostGdalAvailable: false, workerImageAvailable: null, pythonAvailable: false, integrationStackConfigured: false));
        var descriptor = new JsonObject();

        verdict.WriteTo(descriptor);

        descriptor.Select(static p => p.Key).Should().Equal("executionMode", "prerequisites", "runnableHere", "blockedBy", "supportedTargets");
        descriptor["prerequisites"]![0]!["status"]!.GetValue<string>().Should().Be("satisfied");
    }
}
