// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Enforces the canonical Module Dependency Policy
/// (<c>docs/contributor/adr/0047-module-dependency-policy.md</c>): every
/// <c>&lt;ProjectReference&gt;</c> declared by a runtime csproj must point at a
/// provider that the consumer's matrix row permits.
/// </summary>
/// <remarks>
/// <para>
/// The matrix in ADR-0047 is the single source of truth for module placement.
/// This test parses every <c>.csproj</c> under <c>src/</c>, <c>tests/</c>,
/// <c>samples/</c>, and <c>benchmarks/</c>, classifies it into one of the
/// matrix roles by name pattern, and then walks every <c>ProjectReference</c>
/// to verify the <c>(consumer-role, provider-role)</c> cell is allowed.
/// </para>
///
/// <para>
/// Unclassified csprojs (samples, benchmarks, tests, the test-kit) fall
/// through to a default policy that permits any reference. Tooling assemblies
/// are not part of the runtime topology and are governed by their own
/// hygiene tests; this test only guards the runtime matrix.
/// </para>
///
/// <para>
/// Pre-existing matrix violations are tolerated through the
/// <see cref="_techDebtAllowances"/> ratchet, identical in shape to the
/// cross-protocol ratchet in <see cref="CrossProtocolIsolationTests"/>: each
/// entry pins a <c>(consumer-role, provider-role, MaxCount)</c> triple and may
/// only shrink. When a violation is paid down, decrement the cap (or delete
/// the entry); the test fails if the cap is loose.
/// </para>
///
/// <para>
/// <strong>Editing protocol:</strong> the matrix in ADR-0047 and the
/// <see cref="_allowedCells"/> set in this file must be edited together. A PR
/// that touches only the test silently drifts from policy; a PR that touches
/// only the ADR will fail this test. The two artefacts are linked in writing,
/// in code, and in the diff.
/// </para>
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class ModuleDependencyPolicyTests
{
    /// <summary>
    /// Canonical module roles from ADR-0047. <see cref="ModuleRole.Unclassified"/>
    /// is the catch-all for tooling (samples, benchmarks, tests, test-kit). The
    /// default-policy branch treats <see cref="ModuleRole.Unclassified"/> consumers
    /// as permitted to reference anything.
    /// </summary>
    private enum ModuleRole
    {
        Unclassified,
        Abstractions,
        Core,
        Geometry,
        Geocoding,
        Routing,
        Aws,
        Azure,
        Hosting,
        Jobs,
        Ai,
        Geoprocessing,
        Scene,
        Io,
        Import,
        Postgres,
        DuckDB,
        MySql,
        SqlServer,
        ArcGisRest,
        Oracle,
        Protocols,
        PluginsAbstractions,
        Plugins,
        Server,
        ServiceDefaults,
        AppHost,
        Worker,
        Sample,
    }

    /// <summary>
    /// The dependency-direction matrix from ADR-0047, encoded as the set of
    /// <c>(consumer, provider)</c> pairs that are allowed. Every pair NOT in
    /// this set is forbidden; violations either fail the test or — if the
    /// violation is pre-existing — must appear in <see cref="_techDebtAllowances"/>.
    /// </summary>
    /// <remarks>
    /// Reading the matrix: each entry is "this consumer may reference this
    /// provider". The Server row is the only one that may reference everything
    /// below it in the tier stack. The Abstractions row is empty (the contract
    /// surface depends on nothing in the repo).
    /// </remarks>
    private static readonly HashSet<(ModuleRole Consumer, ModuleRole Provider)> _allowedCells = new()
    {
        // Core can reference Abstractions only.
        (ModuleRole.Core, ModuleRole.Abstractions),

        // Geospatial / cloud satellites depend on Abstractions + Core.
        (ModuleRole.Geometry,  ModuleRole.Abstractions),
        (ModuleRole.Geometry,  ModuleRole.Core),
        (ModuleRole.Geocoding, ModuleRole.Abstractions),
        (ModuleRole.Geocoding, ModuleRole.Core),
        // Routing (pgRouting engine + NAServer route/service-area solves, #1266).
        // Mirrors the Geocoding satellite: it depends on Abstractions only (the
        // IDatabaseConnectionProvider surface) and is consumed by the GeoServices
        // NAServer protocol adapter. It must NEVER reference a storage provider or
        // back-reference Server.
        (ModuleRole.Routing,   ModuleRole.Abstractions),
        (ModuleRole.Aws,       ModuleRole.Abstractions),
        (ModuleRole.Aws,       ModuleRole.Core),
        (ModuleRole.Azure,     ModuleRole.Abstractions),
        (ModuleRole.Azure,     ModuleRole.Core),

        // Hosting: Abstractions + Core + Geometry + ServiceDefaults. Hosting
        // owns the IGeometryService abstraction surface and host-level
        // geometry plumbing, so it consumes NetTopologySuite types through
        // Honua.Geometry. Geometry depends only on Abstractions + Core, so the
        // edge is acyclic and directionally down-stack.
        (ModuleRole.Hosting, ModuleRole.Abstractions),
        (ModuleRole.Hosting, ModuleRole.Core),
        (ModuleRole.Hosting, ModuleRole.Geometry),
        (ModuleRole.Hosting, ModuleRole.ServiceDefaults),

        // Storage providers: Abstractions + Core + Geometry (NTS); Postgres
        // additionally may use Aws (S3 backed cloud-storage paths).
        (ModuleRole.Postgres,  ModuleRole.Abstractions),
        (ModuleRole.Postgres,  ModuleRole.Core),
        (ModuleRole.Postgres,  ModuleRole.Geometry),
        // Intra-family: the Postgres role spans Honua.Postgres plus its split
        // sub-assemblies (Honua.Postgres.Shared substrate today; Migrations /
        // Catalog / FeatureStore / Streaming / Outbox planned). Members of the
        // family may reference each other; the no-cross-provider rule only
        // forbids Postgres<->DuckDB/MySql/SqlServer edges.
        (ModuleRole.Postgres,  ModuleRole.Postgres),
        (ModuleRole.Postgres,  ModuleRole.Aws),
        (ModuleRole.DuckDB,    ModuleRole.Abstractions),
        (ModuleRole.DuckDB,    ModuleRole.Core),
        (ModuleRole.DuckDB,    ModuleRole.Geometry),
        (ModuleRole.MySql,     ModuleRole.Abstractions),
        (ModuleRole.MySql,     ModuleRole.Core),
        (ModuleRole.MySql,     ModuleRole.Geometry),
        (ModuleRole.SqlServer, ModuleRole.Abstractions),
        (ModuleRole.SqlServer, ModuleRole.Core),
        (ModuleRole.SqlServer, ModuleRole.Geometry),
        // ArcGIS REST federated read-through provider (#1251): consumes Abstractions
        // + Core; needs no NTS bindings because all wire-format conversion happens
        // inline against the canonical Feature/WKB seam.
        (ModuleRole.ArcGisRest, ModuleRole.Abstractions),
        (ModuleRole.ArcGisRest, ModuleRole.Core),
        (ModuleRole.Oracle,    ModuleRole.Abstractions),
        (ModuleRole.Oracle,    ModuleRole.Core),
        (ModuleRole.Oracle,    ModuleRole.Geometry),

        // Protocol modules: Abstractions + Core + Geometry + Hosting +
        // ServiceDefaults. They may also reference Jobs + Geoprocessing: the OGC
        // API Processes / GeoServices GPServer adapters are thin protocol surfaces
        // over the canonical job/process runtime (IExecutionJobStore,
        // IGeoprocessingJobService, the ControlPlane batch-backend helpers) and
        // must not reimplement it — mirrors the Ai -> Jobs/Geoprocessing precedent.
        // Protocols -> Protocols permits a shared protocol foundation
        // (Honua.Protocols.Ogc.Shared) consumed by OgcApi/OgcClassic/Stac; the
        // specific allowed protocol-to-protocol edges are governed by
        // CrossProtocolIsolationTests, not this role-level matrix.
        (ModuleRole.Protocols, ModuleRole.Abstractions),
        (ModuleRole.Protocols, ModuleRole.Core),
        (ModuleRole.Protocols, ModuleRole.Geometry),
        (ModuleRole.Protocols, ModuleRole.Hosting),
        (ModuleRole.Protocols, ModuleRole.Jobs),
        (ModuleRole.Protocols, ModuleRole.Geoprocessing),
        (ModuleRole.Protocols, ModuleRole.Routing),
        (ModuleRole.Protocols, ModuleRole.Scene),
        (ModuleRole.Protocols, ModuleRole.ServiceDefaults),
        (ModuleRole.Protocols, ModuleRole.Protocols),
        // Plugin/extension SDK (#347, ADR-0024): protocol edit handlers consume ONLY the lean
        // Honua.Plugins.Abstractions contract surface (IPluginEditPipeline) to fire plugin
        // validators/hooks. They must NOT couple to the host-side plugin runtime (Honua.Plugins) —
        // hence PluginsAbstractions, not Plugins, is the allowed provider here.
        (ModuleRole.Protocols, ModuleRole.PluginsAbstractions),

        // Jobs: the durable job-execution substrate. Depends on
        // Abstractions + Core + Hosting + ServiceDefaults (no AWS/Azure
        // packages — the provider backends sit in Honua.Aws / Honua.Azure).
        (ModuleRole.Jobs, ModuleRole.Abstractions),
        (ModuleRole.Jobs, ModuleRole.Core),
        (ModuleRole.Jobs, ModuleRole.Hosting),
        (ModuleRole.Jobs, ModuleRole.ServiceDefaults),

        // Aws / Azure additionally reach Hosting (cloud-control-plane base
        // types lifted into Hosting by ADR-0044) and Jobs (durable substrate).
        (ModuleRole.Aws,   ModuleRole.Hosting),
        (ModuleRole.Aws,   ModuleRole.Jobs),
        (ModuleRole.Aws,   ModuleRole.ServiceDefaults),
        (ModuleRole.Azure, ModuleRole.Hosting),
        (ModuleRole.Azure, ModuleRole.Jobs),
        (ModuleRole.Azure, ModuleRole.ServiceDefaults),

        // Ai (carved AiBuilder + Grounding + NlQuery + AnalysisContent).
        // Same provider set as the cloud satellites (Abstractions + Core +
        // Hosting + Jobs + ServiceDefaults) plus Geoprocessing: Grounding and
        // AnalysisContent orchestrate analysis jobs through
        // IGeoprocessingJobService, which lives in Honua.Geoprocessing.
        // Crucially, Ai must NEVER reference Server — that one-way edge is
        // enforced by HonuaAiIsolationTests.
        (ModuleRole.Ai, ModuleRole.Abstractions),
        (ModuleRole.Ai, ModuleRole.Core),
        (ModuleRole.Ai, ModuleRole.Hosting),
        (ModuleRole.Ai, ModuleRole.Jobs),
        (ModuleRole.Ai, ModuleRole.Geoprocessing),
        (ModuleRole.Ai, ModuleRole.ServiceDefaults),

        // Geoprocessing: the canonical process/analysis runtime carved out of
        // Server so the OGC API Processes / GeoServices GPServer protocol
        // adapters (and Ai's analysis orchestration) can reference it without
        // pulling Server transitively. Depends on Abstractions + Core +
        // Geometry (NTS) + Hosting + Jobs + ServiceDefaults; it must NEVER
        // back-reference Server (enforced by GeoprocessingIsolationTests).
        (ModuleRole.Geoprocessing, ModuleRole.Abstractions),
        (ModuleRole.Geoprocessing, ModuleRole.Core),
        (ModuleRole.Geoprocessing, ModuleRole.Geometry),
        (ModuleRole.Geoprocessing, ModuleRole.Hosting),
        (ModuleRole.Geoprocessing, ModuleRole.Jobs),
        (ModuleRole.Geoprocessing, ModuleRole.ServiceDefaults),

        // Scene: the carved-out 3D scene capability (3D Tiles generation,
        // scene registry, publishing executor, and the gRPC scene/tile/
        // elevation service implementations). Referenced by Protocols.Scene
        // and Server; depends on Abstractions + Core + Geometry (NTS, for
        // elevation profile WKB) + ServiceDefaults. Scene *domain* records
        // stay in Abstractions (MetadataV2 references them). It must NEVER
        // back-reference Server (enforced by SceneIsolationTests).
        (ModuleRole.Scene, ModuleRole.Abstractions),
        (ModuleRole.Scene, ModuleRole.Core),
        (ModuleRole.Scene, ModuleRole.Geometry),
        (ModuleRole.Scene, ModuleRole.Hosting),
        (ModuleRole.Scene, ModuleRole.ServiceDefaults),

        // Io: the file input/output module (file storage + export writers
        // today; upload primitives planned). ASP.NET-coupled like Hosting;
        // depends on Abstractions + Core + Geometry (NTS, for the export
        // writers) + Hosting + ServiceDefaults. Must NEVER reference Server
        // (enforced by HonuaIoIsolationTests). Storage providers must NOT
        // reference Io — that is why the file-format readers stay in the light
        // Honua.Geometry instead of moving here.
        (ModuleRole.Io, ModuleRole.Abstractions),
        (ModuleRole.Io, ModuleRole.Core),
        (ModuleRole.Io, ModuleRole.Geometry),
        (ModuleRole.Io, ModuleRole.Hosting),
        (ModuleRole.Io, ModuleRole.ServiceDefaults),

        // Import: the data-ingest / migration HTTP surface (Migration endpoints
        // + job managers, file-import upload plumbing, raster-import endpoints).
        // ASP.NET-coupled; depends on Abstractions + Core + Geometry + Hosting +
        // ServiceDefaults. The provider-agnostic ingest domain stays in Core
        // (provider-consumed); the NTS readers stay in Geometry. Must NEVER
        // reference Server (enforced by HonuaImportIsolationTests).
        (ModuleRole.Import, ModuleRole.Abstractions),
        (ModuleRole.Import, ModuleRole.Core),
        (ModuleRole.Import, ModuleRole.Geometry),
        (ModuleRole.Import, ModuleRole.Hosting),
        (ModuleRole.Import, ModuleRole.ServiceDefaults),

        // Plugin/extension SDK (#347, ADR-0024), split into two roles so protocol assemblies can
        // depend on the lean public contract surface (PluginsAbstractions = Honua.Plugins.Abstractions,
        // which depends on Abstractions only) WITHOUT coupling to the host-side plugin runtime
        // (Plugins = Honua.Plugins, which depends on Core for the license gate + audit sink and on
        // the contract surface). Neither may back-reference Server or a storage provider.
        (ModuleRole.PluginsAbstractions, ModuleRole.Abstractions),
        (ModuleRole.Plugins, ModuleRole.Core),
        (ModuleRole.Plugins, ModuleRole.PluginsAbstractions),

        // Server (composition root): every tier below it.
        (ModuleRole.Server, ModuleRole.Abstractions),
        (ModuleRole.Server, ModuleRole.Core),
        (ModuleRole.Server, ModuleRole.Geometry),
        (ModuleRole.Server, ModuleRole.Geocoding),
        (ModuleRole.Server, ModuleRole.Routing),
        (ModuleRole.Server, ModuleRole.Aws),
        (ModuleRole.Server, ModuleRole.Azure),
        (ModuleRole.Server, ModuleRole.Ai),
        (ModuleRole.Server, ModuleRole.Geoprocessing),
        (ModuleRole.Server, ModuleRole.Scene),
        (ModuleRole.Server, ModuleRole.Io),
        (ModuleRole.Server, ModuleRole.Import),
        (ModuleRole.Server, ModuleRole.Hosting),
        (ModuleRole.Server, ModuleRole.Jobs),
        (ModuleRole.Server, ModuleRole.Postgres),
        (ModuleRole.Server, ModuleRole.DuckDB),
        (ModuleRole.Server, ModuleRole.MySql),
        (ModuleRole.Server, ModuleRole.SqlServer),
        (ModuleRole.Server, ModuleRole.ArcGisRest),
        (ModuleRole.Server, ModuleRole.Oracle),
        (ModuleRole.Server, ModuleRole.Protocols),
        (ModuleRole.Server, ModuleRole.Plugins),
        (ModuleRole.Server, ModuleRole.ServiceDefaults),

        // ServiceDefaults sits sideways from the main stack but currently
        // references Core for shared logging/diagnostics primitives.
        (ModuleRole.ServiceDefaults, ModuleRole.Abstractions),
        (ModuleRole.ServiceDefaults, ModuleRole.Core),

        // AppHost is the Aspire orchestration shell; references only
        // ServiceDefaults today.
        (ModuleRole.AppHost, ModuleRole.ServiceDefaults),

        // Worker.Gdal is a side-car GDAL process. Per ADR-0038 it deliberately
        // does NOT reference Honua.Server (to avoid pulling the whole host into
        // the worker image); it consumes the job substrate and contracts
        // directly: Abstractions + Core + Jobs.
        (ModuleRole.Worker, ModuleRole.Abstractions),
        (ModuleRole.Worker, ModuleRole.Core),
        (ModuleRole.Worker, ModuleRole.Jobs),

        // Server may host a sample/demo app as static content (the StacOpsDemo
        // Blazor WASM client). Samples never reference back into the runtime.
        (ModuleRole.Server, ModuleRole.Sample),
    };

    /// <summary>
    /// Tech-debt ratchet for pre-existing policy violations. Each entry pins
    /// the number of distinct <c>ProjectReference</c> edges that today violate
    /// the matrix, partitioned by <c>(consumer-role, provider-role)</c>. The
    /// ratchet is one-way: caps may only shrink. When the matrix in ADR-0047
    /// is the actual state of the world, this list is empty.
    /// </summary>
    /// <remarks>
    /// As of ADR-0047, every runtime csproj reference matches the matrix
    /// exactly. Future heavy-package-migration PRs (carving
    /// <c>Honua.Geometry</c> and <c>Honua.Geocoding</c> out of
    /// <c>Honua.Core</c>) may introduce transitional edges that land here.
    /// </remarks>
    private static readonly IReadOnlyList<TechDebtAllowance> _techDebtAllowances = Array.Empty<TechDebtAllowance>();

    private sealed record TechDebtAllowance(ModuleRole Consumer, ModuleRole Provider, int MaxCount);

    [ArchitectureTest]
    public void ProjectReferences_ShouldMatch_ModuleDependencyPolicyMatrix()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        var allCsprojs = EnumerateRuntimeCsprojs(repositoryRoot).ToArray();
        allCsprojs.Should().NotBeEmpty(
            "csproj enumeration must find at least one project; an empty result " +
            "means the repository layout has shifted and the test is silently passing.");

        var violations = new List<string>();
        var ratchetCounts = new Dictionary<(ModuleRole, ModuleRole), int>();

        foreach (var csprojPath in allCsprojs)
        {
            var consumerRole = ClassifyByCsprojName(Path.GetFileNameWithoutExtension(csprojPath));

            // Unclassified consumers (samples / benchmarks / tests / test-kit)
            // fall through to the default policy: tooling may reference anything.
            if (consumerRole == ModuleRole.Unclassified)
            {
                continue;
            }

            var projectReferences = LoadProjectReferenceNames(csprojPath);
            foreach (var providerName in projectReferences)
            {
                var providerRole = ClassifyByCsprojName(providerName);

                // Unclassified providers are out-of-policy tooling; runtime
                // assemblies should not reference them, but if they do we surface
                // the case explicitly rather than silently allow it.
                if (providerRole == ModuleRole.Unclassified)
                {
                    var relative = Path.GetRelativePath(repositoryRoot, csprojPath);
                    violations.Add(
                        $"Runtime project '{Path.GetFileNameWithoutExtension(csprojPath)}' " +
                        $"({relative}) references unclassified provider '{providerName}'. " +
                        "Runtime assemblies may reference only the modules named in " +
                        "ADR-0047's matrix. Add the provider to the matrix (and to " +
                        $"{nameof(ClassifyByCsprojName)}) if it belongs there, or remove the " +
                        "reference.");
                    continue;
                }

                var cell = (consumerRole, providerRole);
                if (_allowedCells.Contains(cell))
                {
                    continue;
                }

                // Forbidden by the matrix. Could be ratcheted tech-debt; otherwise
                // it is a hard violation.
                if (_techDebtAllowances.Any(allowance =>
                        allowance.Consumer == consumerRole && allowance.Provider == providerRole))
                {
                    ratchetCounts.TryGetValue(cell, out var prior);
                    ratchetCounts[cell] = prior + 1;
                    continue;
                }

                var relativePath = Path.GetRelativePath(repositoryRoot, csprojPath);
                violations.Add(
                    $"Module dependency policy violation: '{consumerRole}' " +
                    $"({Path.GetFileNameWithoutExtension(csprojPath)}, {relativePath}) " +
                    $"references '{providerRole}' ({providerName}), which is forbidden by " +
                    "the ADR-0047 dependency-direction matrix. See " +
                    "docs/contributor/adr/0047-module-dependency-policy.md " +
                    $"§ 'Dependency direction matrix' — the ({consumerRole}, {providerRole}) " +
                    "cell is empty. Either route the dependency through a permitted layer, " +
                    "or (only if intentional) update both the ADR matrix and the test's " +
                    "_allowedCells in the same PR.");
            }
        }

        violations
            .OrderBy(message => message, StringComparer.Ordinal)
            .Should()
            .BeEmpty(
                "every runtime ProjectReference must match the ADR-0047 dependency-direction " +
                "matrix. See docs/contributor/adr/0047-module-dependency-policy.md for the " +
                "policy and the decision tree.");

        // ----- Ratchet enforcement -----
        // For each tech-debt allowance, the actual count must MATCH the cap exactly.
        // - actual > cap : regression — a new csproj started violating the matrix.
        // - actual < cap : debt was paid down — decrement so the ratchet tightens.
        var ratchetFailures = new List<string>();
        foreach (var allowance in _techDebtAllowances)
        {
            ratchetCounts.TryGetValue((allowance.Consumer, allowance.Provider), out var actual);

            if (actual > allowance.MaxCount)
            {
                ratchetFailures.Add(
                    $"Tech-debt cap for '{allowance.Consumer} -> {allowance.Provider}' exceeded: " +
                    $"declared MaxCount = {allowance.MaxCount}, actual = {actual}. " +
                    "A new csproj started violating the matrix. Remove the new reference, or " +
                    "(only if justified) raise the cap and update the entry's burn-down note.");
            }
            else if (actual < allowance.MaxCount)
            {
                ratchetFailures.Add(
                    $"Tech-debt cap for '{allowance.Consumer} -> {allowance.Provider}' is loose: " +
                    $"declared MaxCount = {allowance.MaxCount}, actual = {actual}. " +
                    "Debt was paid down — decrement MaxCount to the actual count " +
                    "(or delete the entry if actual is 0) so the ratchet tightens.");
            }
        }

        ratchetFailures
            .OrderBy(message => message, StringComparer.Ordinal)
            .Should()
            .BeEmpty(
                "the module-dependency tech-debt ratchet is one-way: caps can only shrink. " +
                "Tightening keeps the policy honest as Geometry/Geocoding/Aws/Azure satellites " +
                "land and the heavy-package refs migrate out of Honua.Core.");
    }

    [ArchitectureTest]
    public void MatrixAndAdr_ShouldCrossReference_EachOther()
    {
        // Cheap, fast sanity check: the editing-protocol contract in the test's
        // xmldoc explicitly says the matrix and the ADR must be edited together.
        // If someone deletes the ADR without updating the test (or vice versa)
        // this assertion surfaces the drift loudly.
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var adrPath = Path.Combine(
            repositoryRoot,
            "docs",
            "contributor",
            "adr",
            "0047-module-dependency-policy.md");

        File.Exists(adrPath).Should().BeTrue(
            "ADR-0047 must exist alongside this test; the test enforces its matrix and the " +
            "two artefacts are linked. See the test's xmldoc 'Editing protocol' note.");

        var adrText = File.ReadAllText(adrPath);
        adrText.Should().Contain(
            "ModuleDependencyPolicyTests",
            "ADR-0047 must name the arch test that enforces it; otherwise a future contributor " +
            "editing the matrix may not realise the test exists.");
        adrText.Should().Contain(
            "Dependency direction matrix",
            "ADR-0047 must contain a 'Dependency direction matrix' section — the error message " +
            "this test emits points readers at that section by name.");
    }

    private static IEnumerable<string> EnumerateRuntimeCsprojs(string repositoryRoot)
    {
        var roots = new[] { "src", "tests", "samples", "benchmarks" };
        foreach (var root in roots)
        {
            var rootPath = Path.Combine(repositoryRoot, root);
            if (!Directory.Exists(rootPath))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(rootPath, "*.csproj", SearchOption.AllDirectories))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return path;
            }
        }
    }

    private static List<string> LoadProjectReferenceNames(string csprojPath)
    {
        var document = XDocument.Load(csprojPath);
        var names = new List<string>();

        foreach (var element in document.Descendants("ProjectReference"))
        {
            var include = element.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var fileName = Path.GetFileNameWithoutExtension(include.Replace('\\', '/'));
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                names.Add(fileName);
            }
        }

        return names;
    }

    /// <summary>
    /// Maps a csproj name to its <see cref="ModuleRole"/>. Order matters — the
    /// more-specific names (e.g. <c>Honua.Core.Abstractions</c>) must be tested
    /// before their less-specific prefixes (e.g. <c>Honua.Core</c>). Names that
    /// do not match any matrix role return <see cref="ModuleRole.Unclassified"/>;
    /// the caller treats unclassified consumers as tooling (allowed to
    /// reference anything) but flags unclassified runtime providers as a
    /// violation that needs an explicit matrix entry.
    /// </summary>
    private static ModuleRole ClassifyByCsprojName(string projectName)
    {
        // Tooling first: test projects and the test-kit are not part of the
        // runtime topology. This guard must precede the family-prefix checks
        // below, otherwise e.g. "Honua.Protocols.GeoServices.Tests" would match
        // the "Honua.Protocols." prefix and be misclassified as a runtime
        // Protocols consumer (and its test-only Postgres reference would trip
        // the matrix). Unclassified consumers may reference anything.
        if (projectName.EndsWith(".Tests", StringComparison.Ordinal) ||
            projectName.Equals("Honua.TestKit", StringComparison.Ordinal))
        {
            return ModuleRole.Unclassified;
        }

        // Tier 1: Abstractions — must come before Core because of the prefix.
        if (projectName.Equals("Honua.Core.Abstractions", StringComparison.Ordinal))
        {
            return ModuleRole.Abstractions;
        }

        // Tier 2: Core.
        if (projectName.Equals("Honua.Core", StringComparison.Ordinal))
        {
            return ModuleRole.Core;
        }

        // Tier 3: Geospatial / cloud satellites. None of these csprojs exist
        // yet (ADR-0047 reserves their slots); the classifier names them so
        // the policy is ready when they land.
        if (projectName.Equals("Honua.Geometry", StringComparison.Ordinal))
        {
            return ModuleRole.Geometry;
        }
        if (projectName.Equals("Honua.Geocoding", StringComparison.Ordinal))
        {
            return ModuleRole.Geocoding;
        }
        if (projectName.Equals("Honua.Routing", StringComparison.Ordinal))
        {
            return ModuleRole.Routing;
        }
        if (projectName.Equals("Honua.Aws", StringComparison.Ordinal) ||
            projectName.StartsWith("Honua.Aws.", StringComparison.Ordinal))
        {
            return ModuleRole.Aws;
        }
        if (projectName.Equals("Honua.Azure", StringComparison.Ordinal) ||
            projectName.StartsWith("Honua.Azure.", StringComparison.Ordinal))
        {
            return ModuleRole.Azure;
        }

        // Tier 4: Hosting.
        if (projectName.Equals("Honua.Hosting", StringComparison.Ordinal) ||
            projectName.StartsWith("Honua.Hosting.", StringComparison.Ordinal))
        {
            return ModuleRole.Hosting;
        }

        // Durable-job substrate (carved out of Server in the jobs-split
        // refactor). Sits alongside Hosting in tier 4.
        if (projectName.Equals("Honua.Jobs", StringComparison.Ordinal) ||
            projectName.StartsWith("Honua.Jobs.", StringComparison.Ordinal))
        {
            return ModuleRole.Jobs;
        }

        // AI feature surface (AiBuilder + Grounding + NlQuery +
        // AnalysisContent) carved out of Server in the ai-split refactor.
        // Sits alongside Hosting / Jobs in the upper tier of the topology.
        if (projectName.Equals("Honua.Ai", StringComparison.Ordinal) ||
            projectName.StartsWith("Honua.Ai.", StringComparison.Ordinal))
        {
            return ModuleRole.Ai;
        }

        // Canonical process/analysis runtime carved out of Server in the
        // geoprocessing-split refactor. Sits alongside Hosting / Jobs / Ai in
        // the upper tier; referenced by Server, Ai, and (forward-looking) the
        // OGC API Processes / GeoServices GPServer protocol modules.
        if (projectName.Equals("Honua.Geoprocessing", StringComparison.Ordinal) ||
            projectName.StartsWith("Honua.Geoprocessing.", StringComparison.Ordinal))
        {
            return ModuleRole.Geoprocessing;
        }

        // 3D scene capability (3D Tiles generation, scene registry, publishing,
        // and the gRPC scene/tile/elevation services) carved out of Core/Server
        // in the scene-module refactor. Sits alongside Geoprocessing in the
        // upper tier; referenced by Server and the Protocols.Scene adapter.
        if (projectName.Equals("Honua.Scene", StringComparison.Ordinal) ||
            projectName.StartsWith("Honua.Scene.", StringComparison.Ordinal))
        {
            return ModuleRole.Scene;
        }

        // File input/output module (file storage; export + upload to follow)
        // carved out of Server in the io-split refactor. ASP.NET-coupled like
        // Hosting; sits alongside Hosting / Jobs / Ai / Geoprocessing.
        if (projectName.Equals("Honua.Io", StringComparison.Ordinal) ||
            projectName.StartsWith("Honua.Io.", StringComparison.Ordinal))
        {
            return ModuleRole.Io;
        }

        // Data-ingest / migration HTTP surface carved out of Server in the
        // import-split refactor. ASP.NET-coupled; sits in tier 4 alongside
        // Hosting / Jobs / Ai / Geoprocessing / Io.
        if (projectName.Equals("Honua.Import", StringComparison.Ordinal) ||
            projectName.StartsWith("Honua.Import.", StringComparison.Ordinal))
        {
            return ModuleRole.Import;
        }

        // Tier 5: Storage providers. Honua.Postgres + planned sub-assemblies
        // (Migrations / Catalog / FeatureStore / Streaming / Outbox) all share
        // the Postgres role; the matrix gains no rows when the split lands.
        if (projectName.Equals("Honua.Postgres", StringComparison.Ordinal) ||
            projectName.StartsWith("Honua.Postgres.", StringComparison.Ordinal))
        {
            return ModuleRole.Postgres;
        }
        if (projectName.Equals("Honua.DuckDB", StringComparison.Ordinal))
        {
            return ModuleRole.DuckDB;
        }
        if (projectName.Equals("Honua.MySql", StringComparison.Ordinal))
        {
            return ModuleRole.MySql;
        }
        if (projectName.Equals("Honua.SqlServer", StringComparison.Ordinal))
        {
            return ModuleRole.SqlServer;
        }
        if (projectName.Equals("Honua.ArcGisRest", StringComparison.Ordinal))
        {
            return ModuleRole.ArcGisRest;
        }
        if (projectName.Equals("Honua.Oracle", StringComparison.Ordinal))
        {
            return ModuleRole.Oracle;
        }

        // Tier 6: Protocol modules.
        if (projectName.StartsWith("Honua.Protocols.", StringComparison.Ordinal))
        {
            return ModuleRole.Protocols;
        }

        // Plugin/extension SDK (#347), split into two roles: the lean public contract surface
        // (Honua.Plugins.Abstractions) that protocol assemblies may consume, and the host-side
        // runtime (Honua.Plugins) that only Server composes. The Abstractions exact-match must
        // precede the "Honua.Plugins." prefix check. Honua.Plugins.Tests is already routed to
        // Unclassified by the .Tests guard above.
        if (projectName.Equals("Honua.Plugins.Abstractions", StringComparison.Ordinal))
        {
            return ModuleRole.PluginsAbstractions;
        }
        if (projectName.Equals("Honua.Plugins", StringComparison.Ordinal) ||
            projectName.StartsWith("Honua.Plugins.", StringComparison.Ordinal))
        {
            return ModuleRole.Plugins;
        }

        // Tier 7: Server (composition root).
        if (projectName.Equals("Honua.Server", StringComparison.Ordinal))
        {
            return ModuleRole.Server;
        }

        // Out-of-stack: shared utility, Aspire shell, side-car worker.
        if (projectName.Equals("Honua.ServiceDefaults", StringComparison.Ordinal))
        {
            return ModuleRole.ServiceDefaults;
        }
        if (projectName.Equals("Honua.AppHost", StringComparison.Ordinal))
        {
            return ModuleRole.AppHost;
        }
        if (projectName.StartsWith("Honua.Worker.", StringComparison.Ordinal))
        {
            return ModuleRole.Worker;
        }

        // Hosted sample/demo apps (e.g. the Honua.StacOpsDemo Blazor WASM client
        // that Server mounts as static assets behind a conditional reference).
        // Server is the only runtime assembly allowed to reference a sample.
        if (projectName.EndsWith("Demo", StringComparison.Ordinal) ||
            projectName.EndsWith(".Sample", StringComparison.Ordinal) ||
            projectName.EndsWith(".Samples", StringComparison.Ordinal))
        {
            return ModuleRole.Sample;
        }

        // Tooling (tests, samples, benchmarks, test-kit) is unclassified by
        // design: the matrix governs the runtime topology, not the tools that
        // exercise it.
        return ModuleRole.Unclassified;
    }
}
