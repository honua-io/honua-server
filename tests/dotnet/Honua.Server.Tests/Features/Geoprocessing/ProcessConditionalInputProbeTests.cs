// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Direct coverage for the conditional-input probe that backs the toolbox translation lane
/// (#2145). These run the REAL <c>ProcessPlanValidator</c> and the REAL built-in catalog — the
/// same pair the geoprocessing submit path runs — so the assertions are about canonical
/// admissibility rather than a stubbed rule set. Fully offline: no Docker, no Redis, no job
/// store. The end-to-end report shape is covered by
/// <c>Honua.Server.Tests.Import.ToolboxTranslationEndpointTests</c>.
/// </summary>
public sealed class ProcessConditionalInputProbeTests
{
    private readonly BuiltInProcessCatalog _catalog = new();

    private ProcessConditionalInputProbe Probe() => new(_catalog);

    // -----------------------------------------------------------------------
    // Presence-dependent INVALID_PARAMETER_VALUE failures must be preserved
    // -----------------------------------------------------------------------

    [UnitTest]
    public void FindAdmissibilityViolations_UnsatisfiedExactlyOneOfGroup_IsReported()
    {
        // conversion.rasterize accepts exactly one of burnValue/attribute and raises the
        // failure as INVALID_PARAMETER_VALUE, not MISSING_REQUIRED_PARAMETER. Mapping neither
        // is rejected at submit time, so the probe must report it rather than certifying it.
        var violations = Probe().FindAdmissibilityViolations(
            "conversion.rasterize",
            ["source", "cellSize"]);

        violations.Should().ContainSingle();
        violations[0].Kind.Should().Be(ProcessAdmissibilityViolationKind.Inputs);
        violations[0].Message.Should().Contain("exactly one of");
    }

    [UnitTest]
    public void FindAdmissibilityViolations_UnsatisfiedOutputGridGroup_IsReported()
    {
        // The same shape guards the output grid: neither cellSize nor width+height is mapped,
        // so no caller-supplied value can define the grid.
        var violations = Probe().FindAdmissibilityViolations(
            "conversion.rasterize",
            ["source", "burnValue"]);

        violations.Should().ContainSingle();
        violations[0].Kind.Should().Be(ProcessAdmissibilityViolationKind.Inputs);
        violations[0].Message.Should().Contain("cellSize");
    }

    [UnitTest]
    public void FindAdmissibilityViolations_HalfSpecifiedPair_IsReported()
    {
        // raster.interpolate-idw needs BOTH width and height; mapping only width can never be
        // satisfied because the caller cannot supply the unmapped half.
        var violations = Probe().FindAdmissibilityViolations(
            "raster.interpolate-idw",
            ["points", "width"]);

        violations.Should().ContainSingle();
        violations[0].Message.Should().Contain("width").And.Contain("height");
    }

    [UnitTest]
    public void FindAdmissibilityViolations_SatisfiedExactlyOneOfGroup_ReportsNothing()
    {
        var violations = Probe().FindAdmissibilityViolations(
            "conversion.rasterize",
            ["source", "burnValue", "cellSize"]);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    public void FindAdmissibilityViolations_SubstitutedValueComplaint_IsNotReported()
    {
        // sink.external-postgis wants connectionId to be a GUID and the probe substitutes a
        // placeholder. That complaint is about a value the caller supplies, not about which
        // parameters are mapped, so it must stay unreported even though adding connectionName
        // would replace it with the mutually-exclusive complaint on the same field.
        var violations = Probe().FindAdmissibilityViolations(
            "sink.external-postgis",
            ["input", "connectionId", "table", "targetSrid"]);

        violations.Should().BeEmpty();
    }

    [UnitTest]
    public void FindAdmissibilityViolations_MutuallyExclusiveInputsBothMapped_IsReported()
    {
        var violations = Probe().FindAdmissibilityViolations(
            "sink.external-postgis",
            ["input", "connectionName", "connectionId", "table", "targetSrid"]);

        violations.Should().ContainSingle();
        violations[0].Kind.Should().Be(ProcessAdmissibilityViolationKind.Inputs);
        violations[0].Message.Should().Contain("exactly one of");
    }

    // -----------------------------------------------------------------------
    // Advertised-but-not-executable processes are never certified
    // -----------------------------------------------------------------------

    [UnitTest]
    public void FindAdmissibilityViolations_AdvertisedButNotExecutableProcess_IsNotJobExecutable()
    {
        // raster.interpolate-kriging validates cleanly (points is its only required input) but
        // no kriging backend is bundled, so its executor fails every job.
        var violations = Probe().FindAdmissibilityViolations("raster.interpolate-kriging", ["points"]);

        violations.Should().ContainSingle();
        violations[0].Kind.Should().Be(ProcessAdmissibilityViolationKind.NotJobExecutable);
        violations[0].Message.Should().Contain("raster.interpolate-idw");
    }

    [UnitTest]
    public void FindAdmissibilityViolations_DeploymentDependentProcess_IsStillAdmissible()
    {
        // imagery.classify fails only where no inference backend is configured, which a static
        // catalog cannot see, so it must NOT be treated as unconditionally unexecutable.
        var violations = Probe().FindAdmissibilityViolations("imagery.classify", ["source", "model"]);

        violations.Should().NotContain(violation =>
            violation.Kind == ProcessAdmissibilityViolationKind.NotJobExecutable);
    }

    [UnitTest]
    public void FindAdmissibilityViolations_SyncOnlyProcess_IsNotJobExecutable()
    {
        var violations = Probe().FindAdmissibilityViolations("analytics.cluster", ["layerId"]);

        violations.Should().Contain(violation =>
            violation.Kind == ProcessAdmissibilityViolationKind.NotJobExecutable);
    }

    // -----------------------------------------------------------------------
    // Structured-text validation is not a token domain
    // -----------------------------------------------------------------------

    [UnitTest]
    public void FindUnverifiableConditionalParameters_ConstrainedFreeFormText_ReportsNothing()
    {
        // raster.map-algebra's 'expression' is format-constrained (an allow-listed band
        // expression), not a discriminator: no rule branches on its value, and dataType/noData
        // are optional on every branch. Mistaking the format rejection for an undeclared token
        // set returned both and downgraded a mapping the submit path accepts.
        var unverifiable = Probe().FindUnverifiableConditionalParameters(
            "raster.map-algebra",
            ["sources", "expression"]);

        unverifiable.Should().BeEmpty();
    }

    [UnitTest]
    public void FindUnverifiableConditionalParameters_UndeclaredTokenDomain_ReportsBranchCandidates()
    {
        // transform.computed-field branches on 'op', whose legal values the catalog does not
        // declare, so a caller can select a branch the probe never visits and the pessimistic
        // answer must stand.
        var unverifiable = Probe().FindUnverifiableConditionalParameters(
            "transform.computed-field",
            ["input", "target", "op"]);

        unverifiable.Should().Contain("fields");
    }

    [UnitTest]
    public void FindUnverifiableConditionalParameters_UnconditionallyOptionalOmission_ReportsNothing()
    {
        var unverifiable = Probe().FindUnverifiableConditionalParameters(
            "geometry.dissolve",
            ["wkbs", "srid"]);

        unverifiable.Should().BeEmpty();
    }

    [UnitTest]
    public void FindUnverifiableConditionalParameters_ResultIsSubsetOfUnmappedDefaultlessParameters()
    {
        // The probe is only ever allowed to NARROW the no-probe fallback set, so enabling it can
        // never uncertify a mapping the static fallback certified.
        var supplied = new[] { "sources", "expression" };
        var definition = _catalog.GetProcess("raster.map-algebra");
        definition.Should().NotBeNull();

        var fallback = definition!.Parameters
            .Where(parameter => parameter.DefaultValue is null && !supplied.Contains(parameter.Name))
            .Select(parameter => parameter.Name)
            .ToArray();

        var unverifiable = Probe().FindUnverifiableConditionalParameters("raster.map-algebra", supplied);

        unverifiable.Should().BeSubsetOf(fallback);
    }

    [UnitTest]
    public void FindUnverifiableConditionalParameters_AcrossWholeCatalog_NeverExceedsStaticFallback()
    {
        // The subset property is the whole safety argument for enabling the probe: its answer
        // must always be contained in the set the no-probe fallback would have returned, so no
        // mapping the static fallback certified can become uncertified. Asserted over EVERY
        // catalog process rather than one example, because a future rule that widened the
        // answer for a single process would silently break the guarantee.
        var probe = Probe();

        foreach (var definition in _catalog.ListProcesses())
        {
            var supplied = definition.Parameters
                .Where(parameter => parameter.Required)
                .Select(parameter => parameter.Name)
                .ToArray();

            var fallback = definition.Parameters
                .Where(parameter => parameter.DefaultValue is null && !supplied.Contains(parameter.Name))
                .Select(parameter => parameter.Name)
                .ToArray();

            var unverifiable = probe.FindUnverifiableConditionalParameters(definition.ProcessId, supplied);

            unverifiable.Should().BeSubsetOf(
                fallback,
                "the probe may only narrow the static fallback set for '{0}'",
                definition.ProcessId);
        }
    }

    [UnitTest]
    public void AdvertisedButNotExecutableProcesses_KeysResolveToCatalogProcesses()
    {
        // The set is keyed by process id and consulted with an exact string lookup, so a typo
        // (or a process rename) silently disables the check with no other symptom — the report
        // would quietly go back to certifying a tool whose executor can only fail. Pin the keys
        // to real catalog entries so that failure mode is loud.
        BuiltInProcessCatalog.AdvertisedButNotExecutableProcesses.Should().NotBeEmpty();

        foreach (var (processId, reason) in BuiltInProcessCatalog.AdvertisedButNotExecutableProcesses)
        {
            _catalog.GetProcess(processId).Should().NotBeNull(
                "'{0}' is listed as advertised-but-not-executable, so it must exist in the catalog",
                processId);
            reason.Should().NotBeNullOrWhiteSpace();
        }
    }
}
