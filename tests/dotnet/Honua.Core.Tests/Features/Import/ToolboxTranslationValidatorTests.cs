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
    public void Validate_RequiredParameterWithDefault_StillRequiresMapping()
    {
        // ProcessPlanValidator requires a declared-required input key to be present even
        // when the parameter declares a default, so an unmapped one is not executable.
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("SimplifyTool", "test.simplify", Mapping("in_geom", "wkb"))),
            new FakeCatalog());

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(ToolboxToolClassifications.Unsupported);
        var issue = tool.Issues.Single();
        issue.Code.Should().Be(ToolboxTranslationIssueCodes.MissingRequiredParameter);
        issue.ParameterName.Should().Be("tolerance");
    }

    [Fact]
    public void Validate_AllRequiredParametersMapped_IsTranslated()
    {
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("SimplifyTool", "test.simplify",
                Mapping("in_geom", "wkb"),
                Mapping("tol", "tolerance"))),
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
    public void Validate_ProbeReportsMissingConditionalInput_ClassifiesUnsupported()
    {
        // Every parameter of test.optional-only is statically optional, so no
        // missing-required-parameter issue fires. The canonical probe still rejects the
        // mapping, and the tool must not be certified.
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("AspectLikeTool", "test.optional-only", Mapping("unit", "units"))),
            new FakeCatalog(),
            new FakeProbe(["source", "layerId"]));

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(ToolboxToolClassifications.Unsupported);
        tool.ProcessId.Should().Be("test.optional-only");
        tool.Issues.Single().Code.Should()
            .Be(ToolboxTranslationIssueCodes.UnsatisfiedConditionalInputs);
        report.Summary.TranslatedCount.Should().Be(0);
    }

    [Fact]
    public void Validate_ProbeSatisfiedByMappedOptionalInput_IsTranslated()
    {
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("AspectLikeTool", "test.optional-only", Mapping("in_raster", "source"))),
            new FakeCatalog(),
            new FakeProbe(["source", "layerId"]));

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(ToolboxToolClassifications.Translated);
        tool.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WithoutProbe_FallsBackToStaticRequiredFlagsOnly()
    {
        // No probe, so no conditional-input violation fires; 'source' is still neither
        // mapped nor defaulted, so the mapping is reported for review rather than certified.
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("AspectLikeTool", "test.optional-only")),
            new FakeCatalog());

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(ToolboxToolClassifications.PartiallyTranslated);
        tool.Issues.Select(issue => issue.Code).Should()
            .OnlyContain(code => code == ToolboxTranslationIssueCodes.UnverifiableConditionalBranches);
    }

    [Fact]
    public void Validate_UnmappedDefaultlessParameter_IsNotCertifiedTranslated()
    {
        // test.buffer's 'geodesic' is defaulted, so a wkb+distance mapping stays certifiable;
        // dropping 'distance' from the catalog default set is what makes a value undetermined.
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("BufferTool", "test.buffer",
                Mapping("in_geom", "wkb"),
                Mapping("dist", "distance"))),
            new FakeCatalog());

        report.Tools.Single().Classification.Should().Be(ToolboxToolClassifications.Translated);
    }

    [Fact]
    public void Validate_ProbeNotConsultedWhenStaticRequiredParameterMissing()
    {
        // A statically-missing required parameter already makes the tool unsupported; the
        // probe must not add a duplicate conditional issue on top of it.
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("BufferTool", "test.buffer", Mapping("in_geom", "wkb"))),
            new FakeCatalog(),
            new FakeProbe(["nothing-supplied"]));

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(ToolboxToolClassifications.Unsupported);
        tool.Issues.Select(issue => issue.Code).Should()
            .OnlyContain(code => code == ToolboxTranslationIssueCodes.MissingRequiredParameter);
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

    /// <summary>
    /// Stands in for the canonical <see cref="IProcessConditionalInputProbe"/>: reports a
    /// missing conditional input unless at least one member of the group is supplied.
    /// </summary>
    private sealed class FakeProbe(string[] requiredAnyOf) : IProcessConditionalInputProbe
    {
        public IReadOnlyList<ProcessAdmissibilityViolation> FindAdmissibilityViolations(
            string processId,
            IReadOnlyCollection<string> suppliedParameterNames)
            => requiredAnyOf.Any(name => suppliedParameterNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                ? []
                : [new ProcessAdmissibilityViolation(
                    ProcessAdmissibilityViolationKind.Inputs,
                    $"Step requires one of {string.Join('/', requiredAnyOf)} for process '{processId}'.")];
    }

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
                Parameter("geodesic", ProcessParameterValueType.Flag, required: false, defaultValue: "false")
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

        /// <summary>
        /// Mirrors the shape of catalog processes such as <c>surface.aspect</c> whose raster
        /// source parameters are individually optional but conditionally required by the
        /// canonical plan validator.
        /// </summary>
        private static readonly ProcessDefinition OptionalOnly = new()
        {
            ProcessId = "test.optional-only",
            Title = "Optional Only",
            Description = "Test process whose parameters are all statically optional.",
            Category = "test",
            Parameters =
            [
                Parameter("source", ProcessParameterValueType.Text, required: false),
                Parameter("layerId", ProcessParameterValueType.LayerId, required: false, defaultValue: "0"),
                Parameter("units", ProcessParameterValueType.Text, required: false, defaultValue: "m")
            ],
            OutputArtifactKinds = []
        };

        public ProcessDefinition? GetProcess(string processId) => processId switch
        {
            "test.buffer" => Buffer,
            "test.simplify" => Simplify,
            "test.optional-only" => OptionalOnly,
            _ => null
        };

        public IReadOnlyList<ProcessDefinition> ListProcesses() => [Buffer, Simplify, OptionalOnly];

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
