// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Geoprocessing.Cli;
using Xunit;

namespace Honua.Geoprocessing.Cli.Tests;

/// <summary>
/// Offline unit tests for the DI-registration + catalog-entry injectors that make a
/// scaffolded process REGISTERED in one command (GP Devkit P4, issue #2125). All pure
/// string transforms against representative source snippets.
/// </summary>
public sealed class GpScaffoldInjectorTests
{
    private static string LeadingWhitespace(string line) =>
        line[..(line.Length - line.TrimStart(' ', '\t').Length)];

    private const string ManagedRegistrationSource =
        """
        private static void AddProcessExecutors(IServiceCollection services)
        {
            Register<GeometryBufferJobExecutor>(services);
            Register<GeometryClipJobExecutor>(services);
            Register<ImportDatasetJobExecutor>(services);
        }
        """;

    private const string GdalRegistrationSource =
        """
        public static IServiceCollection AddGdalProcessExecutors(this IServiceCollection services, IConfiguration configuration)
        {
            services.TryAddSingleton<IGdalCommandRunner, ProcessGdalCommandRunner>();
            RegisterGdalExecutor<GdalVectorConvertJobExecutor>(services);
            RegisterGdalExecutor<PdalPointCloudConvertJobExecutor>(services);

            return services;
        }
        """;

    [Fact]
    public void TryInsertRegistration_Managed_InsertsAfterLastRegisterCall()
    {
        GpScaffoldInjector.TryInsertRegistration(
            ManagedRegistrationSource,
            "Register<GeometryRecenterJobExecutor>(services);",
            out var result,
            out var error).Should().BeTrue(error);

        result.Should().Contain("Register<GeometryRecenterJobExecutor>(services);");
        // Inserted after the last existing Register<> line (ImportDataset), keeping its indentation.
        var lines = result.Split('\n');
        var importIndex = Array.FindIndex(lines, l => l.Contains("ImportDatasetJobExecutor", StringComparison.Ordinal));
        var newIndex = Array.FindIndex(lines, l => l.Contains("GeometryRecenterJobExecutor", StringComparison.Ordinal));
        newIndex.Should().Be(importIndex + 1);
        // Indentation is copied from the anchor (last existing Register<> line).
        LeadingWhitespace(lines[newIndex]).Should().Be(LeadingWhitespace(lines[importIndex]));
    }

    [Fact]
    public void TryInsertRegistration_Gdal_InsertsAfterLastGdalRegisterCall()
    {
        GpScaffoldInjector.TryInsertRegistration(
            GdalRegistrationSource,
            "RegisterGdalExecutor<GdalWarpClipNativeJobExecutor>(services);",
            out var result,
            out var error).Should().BeTrue(error);

        result.Should().Contain("RegisterGdalExecutor<GdalWarpClipNativeJobExecutor>(services);");
        // It must NOT have anchored to the TryAddSingleton line — only RegisterGdalExecutor<> lines.
        var lines = result.Split('\n');
        var newIndex = Array.FindIndex(lines, l => l.Contains("GdalWarpClipNativeJobExecutor", StringComparison.Ordinal));
        var pdalIndex = Array.FindIndex(lines, l => l.Contains("PdalPointCloudConvertJobExecutor", StringComparison.Ordinal));
        newIndex.Should().Be(pdalIndex + 1);
    }

    [Fact]
    public void TryInsertRegistration_IsIdempotent()
    {
        GpScaffoldInjector.TryInsertRegistration(
            ManagedRegistrationSource,
            "Register<GeometryRecenterJobExecutor>(services);",
            out var once,
            out _).Should().BeTrue();

        GpScaffoldInjector.TryInsertRegistration(
            once,
            "Register<GeometryRecenterJobExecutor>(services);",
            out var twice,
            out _).Should().BeTrue();

        twice.Should().Be(once);
    }

    [Fact]
    public void TryInsertRegistration_FailsWhenNoAnchorFound()
    {
        GpScaffoldInjector.TryInsertRegistration(
            "public void Nothing() { }",
            "Register<GeometryRecenterJobExecutor>(services);",
            out _,
            out var error).Should().BeFalse();

        error.Should().Contain("Register<");
    }

    private const string CatalogSource =
        """
        internal sealed class BuiltInProcessCatalog
        {
            public BuiltInProcessCatalog()
            {
                var definitions = BuildDefinitions();
            }

            private static ProcessDefinition[] BuildDefinitions() =>
            [
                new ProcessDefinition
                {
                    ProcessId = "geometry.buffer",
                    Title = "Buffer",
                },
            ];

            private static readonly ProcessParameterSpec[] SharedFilters =
            [
                Param("where", "Where", "x", ProcessParameterValueType.Text),
            ];
        }
        """;

    [Fact]
    public void TryInsertCatalogEntry_InsertsInsideBuildDefinitionsNotSharedArrays()
    {
        GpScaffoldInjector.TryInsertCatalogEntry(
            CatalogSource,
            "geometry.recenter",
            GpProcessKind.Geometry,
            out var result,
            out var error).Should().BeTrue(error);

        result.Should().Contain("ProcessId = \"geometry.recenter\"");
        result.Should().Contain("Title = \"Geometry Recenter\"");
        result.Should().Contain("Category = \"geometry\"");

        // The new entry must appear BEFORE the shared-arrays declaration (i.e. it was inserted
        // into BuildDefinitions(), not into SharedFilters), so the array element types match.
        var recenterPos = result.IndexOf("geometry.recenter", StringComparison.Ordinal);
        var sharedPos = result.IndexOf("SharedFilters", StringComparison.Ordinal);
        recenterPos.Should().BeLessThan(sharedPos);

        // And it must sit before the FIRST '];' that closes BuildDefinitions().
        var firstClose = result.IndexOf("\n    ];", StringComparison.Ordinal);
        recenterPos.Should().BeLessThan(firstClose);
    }

    [Fact]
    public void TryInsertCatalogEntry_Gdal_AddsNativeRuntimeProfile()
    {
        GpScaffoldInjector.TryInsertCatalogEntry(
            CatalogSource,
            "gdal.warp-clip",
            GpProcessKind.Gdal,
            out var result,
            out _).Should().BeTrue();

        result.Should().Contain("ProcessId = \"gdal.warp-clip\"");
        result.Should().Contain("RuntimeProfile = RuntimeProfiles.Native");
    }

    [Fact]
    public void TryInsertCatalogEntry_IsIdempotent()
    {
        GpScaffoldInjector.TryInsertCatalogEntry(
            CatalogSource, "geometry.recenter", GpProcessKind.Geometry, out var once, out _)
            .Should().BeTrue();

        GpScaffoldInjector.TryInsertCatalogEntry(
            once, "geometry.recenter", GpProcessKind.Geometry, out var twice, out _)
            .Should().BeTrue();

        twice.Should().Be(once);
    }
}
