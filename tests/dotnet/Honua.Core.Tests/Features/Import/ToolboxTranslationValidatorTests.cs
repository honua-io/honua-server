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
    public void Validate_ProbeReportsNoBranchDependentParameter_IsTranslated()
    {
        // 'source' of test.optional-only is unmapped and defaultless, but the canonical
        // validator requires it on no branch once 'layerId' is supplied. An unconditional
        // omission is admissible at submit time, so the tool must be certified rather than
        // downgraded for a review that has nothing to review.
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("AspectLikeTool", "test.optional-only", Mapping("in_layer", "layerId"))),
            new FakeCatalog(),
            new FakeProbe(["source", "layerId"]));

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(ToolboxToolClassifications.Translated);
        tool.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Validate_ProbeReportsBranchDependentParameter_ClassifiesPartiallyTranslated()
    {
        // The mirror image: when the canonical validator CAN require the unmapped 'source' on
        // some admissible branch, the mapping stays uncertified. Narrowing the signal must not
        // collapse into "never downgrade" — an over-claimed 'translated' tells a migrating user
        // a tool works when submit-time validation will reject it.
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("AspectLikeTool", "test.optional-only", Mapping("in_layer", "layerId"))),
            new FakeCatalog(),
            new FakeProbe(["source", "layerId"], branchDependent: ["source"]));

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(ToolboxToolClassifications.PartiallyTranslated);
        var issue = tool.Issues.Single();
        issue.Code.Should().Be(ToolboxTranslationIssueCodes.UnverifiableConditionalBranches);
        issue.ParameterName.Should().Be("source");
    }

    [Fact]
    public void Validate_ProbeReportsBranchQualifiedRequirement_ReplacesTheConservativeDowngrade()
    {
        // Where the discriminator's domain IS enumerable the probe answers exactly, so the
        // report must name the faulting branches instead of falling back to the conservative
        // "cannot pin this down" wording (#3048).
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("AspectLikeTool", "test.optional-only", Mapping("in_layer", "layerId"))),
            new FakeCatalog(),
            new FakeProbe(
                ["source", "layerId"],
                branchRequirements:
                [
                    new ProcessBranchRequirement("mode=exact", "source", "Step is missing 'source'."),
                    new ProcessBranchRequirement("mode=strict", "source", "Step is missing 'source'.")
                ]));

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(ToolboxToolClassifications.PartiallyTranslated);
        var issue = tool.Issues.Single();
        issue.Code.Should().Be(ToolboxTranslationIssueCodes.ConditionalBranchRequirement);
        issue.ParameterName.Should().Be("source");
        // One issue per parameter, listing every branch that faults, rather than one per branch.
        issue.Message.Should().Contain("mode=exact").And.Contain("mode=strict");
    }

    [Fact]
    public void Validate_BranchGapsCoverEveryDiscriminatorValue_DoesNotRecommendConstrainingTheSource()
    {
        // Gaps are reported per PARAMETER, so each group read as individually escapable even
        // when the groups TOGETHER covered every branch. With eps missing on dbscan and k
        // missing on kmeans, no value of `algorithm` executes, and telling the operator to
        // "constrain the source value" points at a remedy that cannot exist (#3048 review).
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("ClusterLikeTool", "test.branching", Mapping("in_layer", "input"), Mapping("algo", "algorithm"))),
            new FakeCatalog(),
            new FakeProbe(
                ["input", "algorithm"],
                branchRequirements:
                [
                    new ProcessBranchRequirement("algorithm=dbscan", "eps", "Step is missing 'eps'."),
                    new ProcessBranchRequirement("algorithm=kmeans", "k", "Step is missing 'k'.")
                ]));

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(
            ToolboxToolClassifications.Unsupported,
            "no admissible discriminator value produces an executable native process");
        tool.Issues.Should().OnlyContain(issue =>
            issue.Code == ToolboxTranslationIssueCodes.ConditionalBranchRequirement);
        tool.Issues.Should().HaveCount(2, "one issue per unmapped parameter");
        tool.Issues.Should().OnlyContain(issue => issue.Message.Contains("must be mapped or defaulted"));
        tool.Issues.Should().NotContain(issue => issue.Message.Contains("Every other admissible value executes"));
    }

    [Fact]
    public void Validate_BranchGapsLeaveOneDiscriminatorValueClear_StillRecommendsConstrainingTheSource()
    {
        // The complement: `kmeans` has no gap, so constraining the source value genuinely does
        // certify the tool and the report must keep offering that cheaper remedy.
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("ClusterLikeTool", "test.branching", Mapping("in_layer", "input"), Mapping("algo", "algorithm"))),
            new FakeCatalog(),
            new FakeProbe(
                ["input", "algorithm"],
                branchRequirements:
                [
                    new ProcessBranchRequirement("algorithm=dbscan", "eps", "Step is missing 'eps'.")
                ]));

        var issue = report.Tools.Single().Issues.Single();
        issue.Code.Should().Be(ToolboxTranslationIssueCodes.ConditionalBranchRequirement);
        issue.Message.Should().Contain("At least one admissible branch is not covered");
    }

    [Fact]
    public void Validate_PartialCollectiveCoverage_DoesNotClaimEveryOtherValueExecutes()
    {
        // NDBI is executable, but NDVI/SAVI/EVI all fail for red and NDWI fails for green.
        // Each per-parameter issue must describe the collective uncovered branch rather than
        // claim every value outside that individual parameter's list executes (#3048 review).
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool(
                "SpectralLikeTool",
                "test.partial-branching",
                Mapping("in_layer", "input"),
                Mapping("formula", "index"),
                Mapping("nir_band", "nir"),
                Mapping("swir_band", "swir"),
                Mapping("blue_band", "blue"))),
            new FakeCatalog(),
            new FakeProbe(
                ["input", "index"],
                branchRequirements:
                [
                    new ProcessBranchRequirement("index=ndvi", "red", "Step is missing 'red'."),
                    new ProcessBranchRequirement("index=savi", "red", "Step is missing 'red'."),
                    new ProcessBranchRequirement("index=evi", "red", "Step is missing 'red'."),
                    new ProcessBranchRequirement("index=ndwi", "green", "Step is missing 'green'.")
                ]));

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(ToolboxToolClassifications.PartiallyTranslated);
        tool.Issues.Should().HaveCount(2);
        tool.Issues.Should().OnlyContain(issue =>
            issue.Message.Contains("At least one admissible branch is not covered"));
        tool.Issues.Should().NotContain(issue =>
            issue.Message.Contains("Every other admissible value executes"));
    }

    [Fact]
    public void Validate_ProbeReportsBranchRequirementForAMappedParameter_IsIgnored()
    {
        // A mapped parameter is supplied at submit time on every branch, so a stale requirement
        // naming it must never downgrade the tool.
        var report = ToolboxTranslationValidator.Validate(
            Manifest(Tool("AspectLikeTool", "test.optional-only", Mapping("in_layer", "layerId"))),
            new FakeCatalog(),
            new FakeProbe(
                ["source", "layerId"],
                branchRequirements:
                    [new ProcessBranchRequirement("mode=exact", "layerId", "Step is missing 'layerId'.")]));

        var tool = report.Tools.Single();
        tool.Classification.Should().Be(ToolboxToolClassifications.Translated);
        tool.Issues.Should().BeEmpty();
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
    /// missing conditional input unless at least one member of the group is supplied, reports
    /// the parameters it was told are branch-dependent, and reports the exact branch
    /// requirements it was told an enumerable domain proves.
    /// </summary>
    private sealed class FakeProbe(
        string[] requiredAnyOf,
        string[]? branchDependent = null,
        ProcessBranchRequirement[]? branchRequirements = null)
        : IProcessConditionalInputProbe
    {
        public IReadOnlyList<ProcessBranchRequirement> FindConditionalBranchRequirements(
            string processId,
            IReadOnlyCollection<string> suppliedParameterNames)
            => [.. (branchRequirements ?? [])
                .Where(requirement => !suppliedParameterNames.Contains(
                    requirement.ParameterName, StringComparer.OrdinalIgnoreCase))];

        public IReadOnlyList<ProcessAdmissibilityViolation> FindAdmissibilityViolations(
            string processId,
            IReadOnlyCollection<string> suppliedParameterNames)
            => requiredAnyOf.Any(name => suppliedParameterNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                ? []
                : [new ProcessAdmissibilityViolation(
                    ProcessAdmissibilityViolationKind.Inputs,
                    $"Step requires one of {string.Join('/', requiredAnyOf)} for process '{processId}'.")];

        public IReadOnlyList<string> FindUnverifiableConditionalParameters(
            string processId,
            IReadOnlyCollection<string> suppliedParameterNames)
            => [.. (branchDependent ?? [])
                .Where(name => !suppliedParameterNames.Contains(name, StringComparer.OrdinalIgnoreCase))];
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

        /// <summary>
        /// Mirrors a process such as <c>analytics.cluster-managed</c>: an enumerable
        /// discriminator whose branches require different parameters, so branch coverage across
        /// all reported gaps is decidable.
        /// </summary>
        private static readonly ProcessDefinition Branching = new()
        {
            ProcessId = "test.branching",
            Title = "Branching",
            Description = "Test process with an enumerable discriminator.",
            Category = "test",
            Parameters =
            [
                Parameter("input", ProcessParameterValueType.Text, required: true),
                Parameter(
                    "algorithm",
                    ProcessParameterValueType.Text,
                    required: true,
                    allowedValues: ["dbscan", "kmeans"]),
                Parameter("eps", ProcessParameterValueType.FloatingPoint, required: false),
                Parameter("k", ProcessParameterValueType.WholeNumber, required: false)
            ],
            OutputArtifactKinds = []
        };

        private static readonly ProcessDefinition PartialBranching = new()
        {
            ProcessId = "test.partial-branching",
            Title = "Partial Branching",
            Description = "Test process whose reported gaps leave one discriminator branch executable.",
            Category = "test",
            Parameters =
            [
                Parameter("input", ProcessParameterValueType.Text, required: true),
                Parameter(
                    "index",
                    ProcessParameterValueType.Text,
                    required: true,
                    allowedValues: ["ndbi", "ndwi", "ndvi", "savi", "evi"]),
                Parameter("nir", ProcessParameterValueType.Text, required: false),
                Parameter("swir", ProcessParameterValueType.Text, required: false),
                Parameter("blue", ProcessParameterValueType.Text, required: false),
                Parameter("red", ProcessParameterValueType.Text, required: false),
                Parameter("green", ProcessParameterValueType.Text, required: false)
            ],
            OutputArtifactKinds = []
        };

        public ProcessDefinition? GetProcess(string processId) => processId switch
        {
            "test.buffer" => Buffer,
            "test.simplify" => Simplify,
            "test.optional-only" => OptionalOnly,
            "test.branching" => Branching,
            "test.partial-branching" => PartialBranching,
            _ => null
        };

        public IReadOnlyList<ProcessDefinition> ListProcesses() =>
            [Buffer, Simplify, OptionalOnly, Branching, PartialBranching];

        public IReadOnlyList<ProcessDefinition> GetProcessesByCategory(string category) =>
            ListProcesses().Where(definition => definition.Category == category).ToArray();

        private static ProcessParameterSpec Parameter(
            string name,
            ProcessParameterValueType valueType,
            bool required,
            string? defaultValue = null,
            IReadOnlyList<string>? allowedValues = null) => new()
            {
                Name = name,
                DisplayName = name,
                Description = name,
                ValueType = valueType,
                Required = required,
                DefaultValue = defaultValue,
                AllowedValues = allowedValues
            };
    }
}
