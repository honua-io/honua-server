// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Classification-rule tests for <see cref="ToolboxTranslationValidator"/> (#2145): the
/// server-authoritative round-trip of SDK-translated toolbox tool descriptors against the
/// canonical process catalog.
/// </summary>
public sealed class ToolboxTranslationValidatorTests
{
    [Fact]
    public void Validate_FullMapping_ClassifiesTranslatedAndRoundTripsSignature()
    {
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("BufferTool", "test.buffer",
                Mapping("in_geom", "wkb"),
                Mapping("dist", "distance"))),
            new FakeCatalog());

        report.Summary.Should().BeEquivalentTo(new ToolboxTranslationSummary
        {
            ToolCount = 1,
            TranslatedCount = 1,
            PartiallyTranslatedCount = 0,
            UnsupportedCount = 0
        });

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(ToolboxToolClassifications.Translated);
        tool.ProcessId.Should().Be("test.buffer");
        tool.Issues.Should().BeEmpty();
        tool.ParameterBindings.Should().HaveCount(2);
        tool.ParameterBindings[0].TargetParameter.Should().Be("wkb");
        tool.ParameterBindings[0].ValueType.Should().Be(nameof(ProcessParameterValueType.Wkb));
        tool.ParameterBindings[0].Required.Should().BeTrue();
        tool.ParameterBindings[1].ValueType.Should().Be(nameof(ProcessParameterValueType.FloatingPoint));
    }

    [Fact]
    public void Validate_TargetParameterMatch_IsCaseInsensitiveAndEchoesCatalogCasing()
    {
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("BufferTool", "test.buffer",
                Mapping("in_geom", "WKB"),
                Mapping("dist", "Distance"))),
            new FakeCatalog());

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(ToolboxToolClassifications.Translated);
        tool.ParameterBindings.Select(binding => binding.TargetParameter)
            .Should().Equal("wkb", "distance");
    }

    [Fact]
    public void Validate_UnknownTargetParameter_ClassifiesPartiallyTranslated()
    {
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("BufferTool", "test.buffer",
                Mapping("in_geom", "wkb"),
                Mapping("dist", "distance"),
                Mapping("units", "outputUnits"))),
            new FakeCatalog());

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(ToolboxToolClassifications.PartiallyTranslated);
        tool.ParameterBindings.Should().HaveCount(2);
        var issue = tool.Issues.Single();
        issue.Code.Should().Be(ToolboxTranslationIssueCodes.UnknownTargetParameter);
        issue.ParameterName.Should().Be("units");
    }

    [Fact]
    public void Validate_DuplicateTargetParameter_ReportsIssueAndKeepsFirstBinding()
    {
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("BufferTool", "test.buffer",
                Mapping("in_geom", "wkb"),
                Mapping("dist", "distance"),
                Mapping("dist_again", "distance"))),
            new FakeCatalog());

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(ToolboxToolClassifications.PartiallyTranslated);
        tool.ParameterBindings.Should().HaveCount(2);
        var issue = tool.Issues.Single();
        issue.Code.Should().Be(ToolboxTranslationIssueCodes.DuplicateTargetParameter);
        issue.ParameterName.Should().Be("dist_again");
    }

    [Fact]
    public void Validate_MissingRequiredParameter_ClassifiesUnsupported()
    {
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("BufferTool", "test.buffer", Mapping("in_geom", "wkb"))),
            new FakeCatalog());

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(ToolboxToolClassifications.Unsupported);
        tool.ProcessId.Should().Be("test.buffer");
        var issue = tool.Issues.Single();
        issue.Code.Should().Be(ToolboxTranslationIssueCodes.MissingRequiredParameter);
        issue.ParameterName.Should().Be("distance");
    }

    [Fact]
    public void Validate_RequiredParameterWithDefault_DoesNotRequireMapping()
    {
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("SimplifyTool", "test.simplify", Mapping("in_geom", "wkb"))),
            new FakeCatalog());

        report.Tools.Single().Classification.Should().Be(ToolboxToolClassifications.Translated);
    }

    [Fact]
    public void Validate_NoTargetProcess_ClassifiesUnsupportedWithNoNativeExecutor()
    {
        var descriptor = new ToolboxToolDescriptor
        {
            ToolName = "CustomScript",
            TargetProcessId = null,
            UnsupportedConstructs = ["custom Python execution body"]
        };

        var report = ToolboxTranslationValidator.Validate(Manifest(descriptor), new FakeCatalog());

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(ToolboxToolClassifications.Unsupported);
        tool.ProcessId.Should().BeNull();
        tool.ParameterBindings.Should().BeEmpty();
        tool.Issues.Select(issue => issue.Code).Should().BeEquivalentTo(
            ToolboxTranslationIssueCodes.UnsupportedConstruct,
            ToolboxTranslationIssueCodes.NoNativeExecutor);
    }

    [Fact]
    public void Validate_UnknownProcessId_ClassifiesUnsupported()
    {
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("MysteryTool", "test.does-not-exist", Mapping("a", "b"))),
            new FakeCatalog());

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(ToolboxToolClassifications.Unsupported);
        tool.ProcessId.Should().BeNull();
        tool.Issues.Single().Code.Should().Be(ToolboxTranslationIssueCodes.UnknownProcess);
    }

    [Fact]
    public void Validate_UnsupportedConstructOnExecutableTool_ClassifiesPartiallyTranslated()
    {
        var descriptor = Tool("BufferTool", "test.buffer",
            Mapping("in_geom", "wkb"),
            Mapping("dist", "distance"))
            with
        { UnsupportedConstructs = ["arcpy.env.workspace assignment"] };

        var report = ToolboxTranslationValidator.Validate(Manifest(descriptor), new FakeCatalog());

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(ToolboxToolClassifications.PartiallyTranslated);
        tool.Issues.Single().Code.Should().Be(ToolboxTranslationIssueCodes.UnsupportedConstruct);
    }

    [Fact]
    public void Validate_NormalizesToolboxNameAndSourceFormat()
    {
        var manifest = Manifest(Tool("BufferTool", "test.buffer",
            Mapping("in_geom", "wkb"),
            Mapping("dist", "distance")))
            with
        { ToolboxName = "  MyToolbox  ", SourceFormat = "PYT" };

        var report = ToolboxTranslationValidator.Validate(manifest, new FakeCatalog());

        report.ToolboxName.Should().Be("MyToolbox");
        report.SourceFormat.Should().Be("pyt");
    }

    private static ToolboxTranslationManifest Manifest(params ToolboxToolDescriptor[] tools) => new()
    {
        ToolboxName = "TestToolbox",
        SourceFormat = "pyt",
        Tools = tools
    };

    private static ToolboxToolDescriptor Tool(
        string name,
        string? processId,
        params ToolboxParameterMapping[] mappings) => new()
        {
            ToolName = name,
            TargetProcessId = processId,
            ParameterMappings = mappings
        };

    private static ToolboxParameterMapping Mapping(string source, string target) => new()
    {
        SourceName = source,
        TargetParameter = target
    };

    private sealed class FakeCatalog : IProcessCatalog
    {
        private static readonly ProcessDefinition Buffer = new()
        {
            ProcessId = "test.buffer",
            Title = "Buffer",
            Description = "Test buffer process.",
            Category = "test",
            Parameters =
            [
                Parameter("wkb", ProcessParameterValueType.Wkb, required: true),
                Parameter("distance", ProcessParameterValueType.FloatingPoint, required: true),
                Parameter("geodesic", ProcessParameterValueType.Flag, required: false)
            ],
            OutputArtifactKinds = []
        };

        private static readonly ProcessDefinition Simplify = new()
        {
            ProcessId = "test.simplify",
            Title = "Simplify",
            Description = "Test simplify process with a defaulted required parameter.",
            Category = "test",
            Parameters =
            [
                Parameter("wkb", ProcessParameterValueType.Wkb, required: true),
                Parameter("tolerance", ProcessParameterValueType.FloatingPoint, required: true, defaultValue: "1.0")
            ],
            OutputArtifactKinds = []
        };

        public ProcessDefinition? GetProcess(string processId) => processId switch
        {
            "test.buffer" => Buffer,
            "test.simplify" => Simplify,
            _ => null
        };

        public IReadOnlyList<ProcessDefinition> ListProcesses() => [Buffer, Simplify];

        public IReadOnlyList<ProcessDefinition> GetProcessesByCategory(string category) =>
            ListProcesses().Where(definition => definition.Category == category).ToArray();

        private static ProcessParameterSpec Parameter(
            string name,
            ProcessParameterValueType valueType,
            bool required,
            string? defaultValue = null) => new()
            {
                Name = name,
                DisplayName = name,
                Description = name,
                ValueType = valueType,
                Required = required,
                DefaultValue = defaultValue
            };
    }
}
