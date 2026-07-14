// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Discovery;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.PackageReview.Abstractions;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
using NSubstitute;
using StandardSchema = Newtonsoft.Json.Schema.JSchema;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Conformance tests that assert Honua's live <c>/mcp</c> advertised tool input
/// schemas and resource payload shapes conform to the published
/// <c>geospatial-mcp</c> JSON Schemas (draft 2020-12). Honua is the reference
/// implementation of the open standard, so these tests pin Honua to the standard
/// it champions.
///
/// <para>
/// The vendored standard schemas live under
/// <c>ConformanceSchemas/geospatial-mcp/</c> and the standard's own conformance
/// fixtures under <c>ConformanceSchemas/fixtures/</c> (see the directory README).
/// </para>
///
/// <para>
/// Strategy: for every tool Honua implements, the standard's fixture example
/// inputs (representative valid arguments) MUST validate against BOTH Honua's
/// live advertised <c>inputSchema</c> and the vendored standard schema — i.e.
/// the two contracts agree on what a valid call looks like. Standard tools Honua
/// does not yet implement as discrete MCP tools (the map/app composition and
/// publish families) are tracked as known-gaps: their absence does not fail the
/// suite. Only NON-CONFORMANCE of an IMPLEMENTED tool fails.
/// </para>
/// </summary>
public sealed partial class McpTaxonomyAlignmentTests
{
    /// <summary>
    /// Maps Honua's advertised tool name to the bare standard tool name (and its
    /// vendored schema file). Tools Honua implements today.
    /// </summary>
    private static readonly Dictionary<string, string> ImplementedToolStandardNames =
        new(StringComparer.Ordinal)
        {
            ["honua_plan_analysis"] = "plan_analysis",
            ["honua_ground_candidates"] = "ground_candidates",
            ["honua_clarify_intent"] = "clarify_intent",
            ["honua_validate_plan"] = "validate_plan",
            ["honua_dry_run_plan"] = "validate_plan",
            ["honua_execute_plan"] = "execute_plan",
            ["honua_cancel_job"] = "cancel_job",
            ["honua_propose_operation"] = "propose_operation",
            ["honua_create_map_package"] = "create_map_package",
            ["honua_create_app_package"] = "create_app_package",
            ["honua_geocode_address"] = "geocode_address",
            ["honua_geocode_addresses"] = "geocode_addresses",
            ["honua_ingest_dataset"] = "ingest_dataset",
            ["honua_solve_route"] = "solve_route",
            ["honua_list_layers"] = "list_layers",
            ["honua_query_features"] = "query_features",
            ["honua_render_map"] = "render_map",
            ["honua_get_style"] = "get_style",
            ["honua_apply_style_preset"] = "apply_style_preset",
            ["honua_publish_service"] = "publish_service",
            // honua_publish_result (#2482): promotes a completed analysis job's
            // materialized artifact into a hosted layer. The standard
            // publish_result requires only `sourceId`; the live schema mirrors that
            // required set (additionalProperties allowed) so the required-field
            // match and standard-fixture assertions both hold.
            ["honua_publish_result"] = "publish_result",
            ["honua_ops_health"] = "ops_health",
            ["honua_ops_findings"] = "ops_findings",
            ["honua_alert_events"] = "alert_events",
            ["honua_operate_events"] = "operate_events",
            ["honua_platform_release_status"] = "platform_release_status",
            ["honua_deploy_operations"] = "deploy_operations",
            ["honua_supported_operation_kinds"] = "supported_operation_kinds",
            ["honua_propose_rollback"] = "propose_rollback",
            // Honua extensions over the bare taxonomy (#1949): the standard models
            // entity resolution and capability discovery as CapabilityCatalog reads;
            // the reference implementation exposes them as discrete tools and ships
            // vendored conformance schemas marked x-honua-extension. Their live and
            // vendored required sets agree, so they participate in the full
            // required-field + fixture conformance assertions (unlike the
            // publish_service divergence recorded below).
            ["honua_resolve_entity"] = "resolve_entity",
            ["honua_list_capabilities"] = "list_capabilities",
        };

    /// <summary>
    /// Standard tool families enumerated in the geospatial-mcp taxonomy that
    /// Honua does not yet advertise as discrete MCP tools. These are known-gaps:
    /// they are recorded so coverage stays honest, but their absence must not
    /// fail the conformance suite.
    /// </summary>
    private static readonly string[] KnownGapStandardTools =
    {
        "refine_map_package",
        "compose_mixed_protocol_map",
        "preview_map_package",
        "preview_app_package",
        // edit_features is the sole member of the standard's optional 'mutation'
        // profile. Honua deliberately does NOT implement it: the MCP surface
        // exposes no AI-facing feature-mutation tool per ADR-0028 (AI operational
        // data editing is not supported; founder-reaffirmed 2026-07-06). The
        // standard still publishes the schema, so the gap is real and closeable
        // by any adopter that makes a different trust decision — but not by Honua.
        "edit_features",
        // Direct geoprocessing verbs: members of the standard's opt-in 'analysis'
        // conformance profile (geospatial-mcp#55, upstream ADR-0029). The index
        // marks them known-gap for the reference implementation; they are required
        // for FULL only when a manifest declares the analysis profile. Honua does
        // not ship direct verbs yet (#2555/#2566 and the A8/analysis track) —
        // plan_analysis/execute_plan cover those workflows today.
        "buffer_features",
        "overlay_features",
        "summarize_statistics",
        "reproject_features",
        "join_features",
        "export_dataset",
    };

    /// <summary>
    /// Implemented Honua tools that have no 1:1 geospatial-mcp standard schema
    /// because their input contract intentionally diverges from the standard.
    /// <c>honua_publish_service</c> publishes a source database table as a new
    /// hosted service (connectionId/schema/table/layerName); the standard
    /// <c>publish_result</c> instead promotes an existing result/package
    /// (sourceId) and its concrete publish field set is still finalizing upstream
    /// (honua-server#730/#732). These are recorded so coverage stays honest, but
    /// they are excluded from the standard-schema required-field match until the
    /// standard finalizes a service-publish contract.
    /// </summary>
    private static readonly HashSet<string> HonuaNativeToolsWithoutStandardSchema =
        new(StringComparer.Ordinal)
        {
            "honua_publish_service",
            // honua_describe_layer / honua_list_jobs are Honua capability-breadth
            // extensions (#2813) with no 1:1 geospatial-mcp standard tool schema:
            // the standard models layer schema via CapabilityCatalog reads and does
            // not publish a discrete describe_layer or list_jobs tool. Recorded here
            // so coverage stays honest until the standard defines them.
            "honua_describe_layer",
            "honua_list_jobs",
        };

    private static string SchemaRoot =>
        Path.Join(AppContext.BaseDirectory, "ConformanceSchemas", "geospatial-mcp");

    private static string FixtureRoot =>
        Path.Join(AppContext.BaseDirectory, "ConformanceSchemas", "fixtures");

    [UnitTest]
    public void VendoredStandardSchemas_AreDraft2020_12AndLoad()
    {
        Directory.Exists(SchemaRoot).Should().BeTrue(
            $"vendored geospatial-mcp schemas must be present at '{SchemaRoot}'");

        var schemaFiles = Directory
            .EnumerateFiles(SchemaRoot, "*.schema.json", SearchOption.AllDirectories)
            .ToArray();

        schemaFiles.Should().NotBeEmpty("vendored standard schemas must be copied to the test output");

        foreach (var file in schemaFiles)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            doc.RootElement.TryGetProperty("$schema", out var dialect).Should().BeTrue(
                $"'{file}' must declare a $schema dialect");
            dialect.GetString().Should().Be(
                "https://json-schema.org/draft/2020-12/schema",
                $"'{file}' must be draft 2020-12");

            // The schema must compile under a JSON-schema engine.
            var act = () => LoadSchema(file);
            act.Should().NotThrow($"'{file}' must be a loadable JSON schema");
        }
    }

    [UnitTest]
    public void EveryImplementedTool_IsMappedToAStandardSchema_OrIsADeliberatePackageReviewTool()
    {
        // Guard: a NEW tool added to BuildTools() that has no standard mapping
        // and is not a deliberately-excluded reference tool fails here, forcing
        // the author to either map it to a standard schema or document the gap.
        var liveToolNames = BuildTools().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var unmapped = liveToolNames
            .Where(n => !ImplementedToolStandardNames.ContainsKey(n))
            .Where(n => !HonuaNativeToolsWithoutStandardSchema.Contains(n))
            .ToArray();

        unmapped.Should().BeEmpty(
            "every advertised /mcp tool must map to a published geospatial-mcp tool schema "
            + "(add a mapping in ImplementedToolStandardNames, record it as a Honua-native "
            + "divergence, or record a known-gap)");
    }

    [UnitTest]
    public void ImplementedTools_AdvertiseInputSchemasConformantWithTheStandard()
    {
        var tools = BuildTools();

        foreach (var tool in tools)
        {
            // Honua-native tools that intentionally diverge from the standard
            // (e.g. honua_publish_service) carry no 1:1 standard schema to match.
            if (HonuaNativeToolsWithoutStandardSchema.Contains(tool.Name))
            {
                continue;
            }

            ImplementedToolStandardNames.TryGetValue(tool.Name, out var standardName)
                .Should().BeTrue($"tool '{tool.Name}' must map to a standard tool schema");

            var standardSchemaPath = Path.Join(SchemaRoot, "tools", standardName + ".schema.json");
            File.Exists(standardSchemaPath).Should().BeTrue(
                $"vendored standard schema for '{standardName}' must exist");

            var liveSchema = LoadSchemaFromJson(SerializeLive(tool.Describe().InputSchema));
            var standardSchema = LoadSchema(standardSchemaPath);

            // Both schemas must be object schemas describing an argument bag.
            liveSchema.Type.Should().HaveFlag(JSchemaType.Object,
                $"Honua '{tool.Name}' inputSchema must be an object schema");
            standardSchema.Type.Should().HaveFlag(JSchemaType.Object,
                $"standard '{standardName}' schema must be an object schema");

            // The standard MUST NOT require a field Honua's live schema does not
            // require, otherwise a schema-driven client following the standard
            // could send a call Honua rejects (or omit a field Honua needs).
            // For implemented tools the two required sets must agree.
            var liveRequired = liveSchema.Required.OrderBy(x => x, StringComparer.Ordinal);
            var standardRequired = standardSchema.Required.OrderBy(x => x, StringComparer.Ordinal);
            standardRequired.Should().BeEquivalentTo(liveRequired,
                $"required top-level fields for '{tool.Name}' must match the standard '{standardName}'");
        }
    }

    [UnitTest]
    public void ImplementedTools_StandardFixtureInputs_ValidateAgainstBothLiveAndStandardSchemas()
    {
        // The strongest conformance assertion: the standard's own example inputs
        // (representative VALID calls) must be accepted by BOTH Honua's live
        // advertised inputSchema AND the published standard schema. If Honua's
        // schema rejected a standard-valid call, Honua would not conform.
        var tools = BuildTools().ToDictionary(t => t.Name, StringComparer.Ordinal);
        var asserted = 0;

        foreach (var (liveName, standardName) in ImplementedToolStandardNames)
        {
            if (!tools.TryGetValue(liveName, out var tool))
            {
                // honua_dry_run_plan maps to the same standard schema as
                // honua_validate_plan; both are present, but skip silently if a
                // mapping entry has no live tool (covered by the mapping guard).
                continue;
            }

            var fixtureDir = Path.Join(FixtureRoot, "tools", standardName);
            if (!Directory.Exists(fixtureDir))
            {
                continue;
            }

            var liveSchema = LoadSchemaFromJson(SerializeLive(tool.Describe().InputSchema));
            var standardSchema = LoadSchema(
                Path.Join(SchemaRoot, "tools", standardName + ".schema.json"));

            foreach (var fixtureFile in Directory.EnumerateFiles(fixtureDir, "*.json"))
            {
                var inputs = ReadFixtureInputs(
                    fixtureFile,
                    expectedSchemaRef: "tools/" + standardName + ".schema.json");
                if (inputs is null)
                {
                    continue;
                }

                inputs.IsValid(standardSchema, out IList<string> standardErrors);
                standardErrors.Should().BeEmpty(
                    $"standard fixture '{Path.GetFileName(fixtureFile)}' must validate "
                    + $"against the standard '{standardName}' schema");

                inputs.IsValid(liveSchema, out IList<string> liveErrors);
                liveErrors.Should().BeEmpty(
                    $"standard fixture '{Path.GetFileName(fixtureFile)}' (a standard-valid "
                    + $"call) must be accepted by Honua's live '{liveName}' inputSchema — "
                    + "otherwise Honua does not conform to the standard");

                asserted++;
            }
        }

        asserted.Should().BeGreaterThan(0, "at least one tool fixture must be asserted");
    }

    [UnitTest]
    public void PlanTools_StepKindAndArtifactEnums_MatchTheStandard()
    {
        // The standard pins AnalysisPlanStepKind and ArtifactKind enum values.
        // Honua's live plan schemas source the same enums; assert the live enum
        // is a SUBSET of (i.e. not broader than) what the standard advertises so
        // a standard-conformant client never sees an undocumented step kind.
        // The vendored validate_plan schema expresses the plan via $defs/$ref;
        // resolve the enums against the raw JSON so the assertion does not depend
        // on a JSON-schema engine's $ref representation.
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Join(SchemaRoot, "tools", "validate_plan.schema.json")));
        var root = doc.RootElement;

        // properties.plan -> $defs.analysisPlan -> properties.steps.items
        //   -> $defs.planStep -> properties.kind -> $defs.planStepKind.enum
        var analysisPlan = ResolveRef(root, root.GetProperty("properties"), "plan");
        var steps = analysisPlan.GetProperty("properties").GetProperty("steps");
        var planStep = ResolveRef(root, steps, "items");
        var kind = ResolveRef(root, planStep.GetProperty("properties"), "kind");
        var standardStepKinds = EnumStrings(kind);

        var outputs = analysisPlan.GetProperty("properties").GetProperty("outputs");
        var artifactKind = ResolveRef(root, outputs, "items");
        var standardArtifactKinds = EnumStrings(artifactKind);

        var liveStepKinds = McpToolSchemas.PlanStepKindNames;
        var liveArtifactKinds = McpToolSchemas.ArtifactKindNames;

        liveStepKinds.Should().BeSubsetOf(standardStepKinds,
            "every live AnalysisPlanStepKind must be advertised by the standard");
        liveArtifactKinds.Should().BeSubsetOf(standardArtifactKinds,
            "every live ArtifactKind must be advertised by the standard");
    }

    [UnitTest]
    public void KnownGapStandardTools_AreDocumented_AndDoNotFailOnAbsence()
    {
        // Records the standard tools Honua has NOT yet implemented as discrete
        // MCP tools. The test asserts they are genuinely absent from the live
        // roster (so an accidental implementation flips them out of the gap list)
        // and that the standard ships a schema for each (so the gap is real and
        // closeable), WITHOUT failing on the absence itself.
        var liveToolNames = BuildTools().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var gap in KnownGapStandardTools)
        {
            liveToolNames.Should().NotContain("honua_" + gap,
                $"'{gap}' is recorded as a known-gap; if Honua now implements it, "
                + "map it in ImplementedToolStandardNames and remove it from the gap list");

            File.Exists(Path.Join(SchemaRoot, "tools", gap + ".schema.json"))
                .Should().BeTrue($"the standard must publish a schema for the gap tool '{gap}'");
        }
    }

    [UnitTest]
    public void ResourcePayloads_StandardFixtures_ConformToTheVendoredResourceSchemas()
    {
        // Resource families Honua projects today, with concrete shapes the
        // standard enumerates. The standard's resource fixtures must validate
        // against the vendored resource schemas (the inspection-projection
        // contract Honua's resource surfaces emit).
        var resourceFamilies = new[]
        {
            ("result-package", "result-package.schema.json"),
            ("artifact", "artifact.schema.json"),
            ("provenance", "provenance.schema.json"),
            ("workspace", "workspace.schema.json"),
            ("ops-health", "ops-health.schema.json"),
            ("ops-findings", "ops-findings.schema.json"),
        };

        var asserted = 0;
        foreach (var (family, schemaFile) in resourceFamilies)
        {
            var schema = LoadSchema(Path.Join(SchemaRoot, "resources", schemaFile));
            var fixtureDir = Path.Join(FixtureRoot, "resources", family);
            Directory.Exists(fixtureDir).Should().BeTrue(
                $"resource fixtures for '{family}' must be vendored");

            foreach (var fixtureFile in Directory.EnumerateFiles(fixtureDir, "*.json"))
            {
                var inputs = ReadFixtureInputs(fixtureFile);
                if (inputs is null)
                {
                    continue;
                }

                inputs.IsValid(schema, out IList<string> errors);
                errors.Should().BeEmpty(
                    $"resource fixture '{Path.GetFileName(fixtureFile)}' must conform to "
                    + $"the '{family}' payload schema");
                asserted++;
            }
        }

        asserted.Should().BeGreaterThan(0, "at least one resource fixture must be asserted");
    }

    [UnitTest]
    public async Task LiveWorkspacePayload_ConformsToVendoredWorkspaceSchema()
    {
        var lifecycle = Substitute.For<IWorkspaceLifecycleService>();
        lifecycle.GetWorkspaceAsync("ws-42", Arg.Any<CancellationToken>())
            .Returns(new Workspace
            {
                WorkspaceId = "ws-42",
                Kind = WorkspaceKind.ResultCollection,
                Label = "Result workspace",
                OwnerId = "test-user",
                State = WorkspaceLifecycleState.Active,
                Uri = "honua://workspace-storage/ws-42",
                CreatedAt = DateTimeOffset.Parse("2026-05-18T12:00:00Z", CultureInfo.InvariantCulture),
                ExpiresAt = DateTimeOffset.Parse("2026-05-19T12:00:00Z", CultureInfo.InvariantCulture),
                Artifacts =
                [
                    new Artifact
                    {
                        ArtifactId = "artifact-1",
                        Kind = ArtifactKind.FeatureLayer,
                        Label = "Output layer",
                        State = ArtifactLifecycleState.Available,
                        WorkspaceId = "ws-42",
                        CreatedAt = DateTimeOffset.Parse("2026-05-18T12:05:00Z", CultureInfo.InvariantCulture),
                        Metadata = new Dictionary<string, string>
                        {
                            ["resultPackageId"] = "result_3f21"
                        }
                    }
                ]
            });

        await using var services = new ServiceCollection()
            .AddScoped(_ => lifecycle)
            .BuildServiceProvider();
        var resource = new WorkspaceResource(
            Substitute.For<IGeoprocessingJobService>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WorkspaceResource>.Instance);

        var result = await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://workspaces/ws-42",
            CancellationToken.None);

        var payload = JToken.Parse(result.Contents[0].Text);
        payload["lifecycleState"]!.Value<string>().Should().Be("active");
        payload["resultsUri"]!.Value<string>().Should().Be("honua://results/result_3f21");
        payload["status"].Should().BeNull();
        AssertPayloadConformsToResourceSchema(payload, "workspace.schema.json", "live workspace payload");
    }

    [UnitTest]
    public async Task LiveResultPayload_ConformsToVendoredResultPackageSchema()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.GetJobResultsAsync("job-xyz", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(AnalysisResultPackage.CreateCompleted(
                "result_3f21",
                new ResultSummary
                {
                    Title = "Buffered parcels",
                    Description = "5-meter parcel buffers"
                },
                [
                    new ArtifactRef
                    {
                        ArtifactId = "artifact-1",
                        Kind = ArtifactKind.FeatureLayer,
                        Label = "Buffered output",
                        Uri = "honua://artifacts/artifact-1",
                        ContentType = "application/geo+json",
                        Metadata = new Dictionary<string, string>
                        {
                            ["resultPackageId"] = "result_3f21"
                        }
                    }
                ],
                [
                    new WorkspaceRef
                    {
                        WorkspaceId = "ws-42",
                        Kind = WorkspaceKind.Scratch,
                        Label = "Scratch workspace",
                        Uri = "honua://workspace-storage/ws-42",
                        ExpiresAt = DateTimeOffset.Parse("2026-05-19T12:00:00Z", CultureInfo.InvariantCulture)
                    }
                ],
                new ProvenanceRecord
                {
                    Sources =
                    [
                        new ProvenanceSource
                        {
                            SourceId = "parcels",
                            Version = "v1",
                            Description = "County parcels"
                        }
                    ],
                    ProcessDefinitions = ["geometry.buffer"],
                    Assumptions = ["buffers use planar meters"],
                    ClarificationsAsked = ["distance_units"],
                    ClarificationsAnswered = ["meters"],
                    ExecutedAt = DateTimeOffset.Parse("2026-05-18T12:05:00Z", CultureInfo.InvariantCulture),
                    GeneratedArtifactIds = ["artifact-1"]
                },
                assumptions: ["output uses source CRS"]));

        var resource = new JobResultsResource(jobService, NullLogger<JobResultsResource>.Instance);
        var result = await resource.ReadAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            "honua://jobs/job-xyz/results",
            CancellationToken.None);

        var payload = JToken.Parse(result.Contents[0].Text);
        payload["resultPackageId"]!.Value<string>().Should().Be("result_3f21");
        AssertPayloadConformsToResourceSchema(payload, "result-package.schema.json", "live result payload");
    }

    [UnitTest]
    public void HonuaResourceFamilies_AreCoveredByTheStandardResourceSchemas()
    {
        // Every resource family Honua advertises that the standard defines a
        // payload schema for must have a vendored schema present, so the
        // conformance surface stays complete as new resources are added.
        var standardResourceFamilies = new HashSet<string>(StringComparer.Ordinal)
        {
            "result-package", "artifact", "provenance", "workspace",
            "map-package", "app-package", "style", "theme",
            "map-template", "published-service", "deployment",
            "ops-health", "ops-findings",
        };

        foreach (var family in standardResourceFamilies)
        {
            File.Exists(Path.Join(SchemaRoot, "resources", family + ".schema.json"))
                .Should().BeTrue($"vendored standard resource schema for '{family}' must exist");
        }

        // Honua advertises at least the workspace + result/job + promotion-surface
        // families; assert the roster is non-trivial so the wiring is real.
        BuildResources().Should().NotBeEmpty();
    }

    // -------------------------------------------------------------------
    // Resource-URI-TEMPLATE conformance (Track A, #2323): the live resource
    // grammar IS the contract. Every live URI template that maps onto a
    // standard resource family must match the vendored index.json uriForm
    // (Decision A grammar), and every standard family the index marks
    // 'implemented' must be served by a live template. This makes any URI
    // drift — e.g. reverting honua://map-packages/{id} to honua://maps/{id} —
    // a build failure rather than a silent spec/server divergence.
    // -------------------------------------------------------------------

    private static string VendoredIndexPath => Path.Join(SchemaRoot, "index.json");

    /// <summary>
    /// Normalizes a URI template by collapsing every <c>{token}</c> placeholder
    /// to <c>{}</c> so the server's domain token names
    /// (<c>{packageId}</c>) compare equal to the standard's snake_case tokens
    /// (<c>{map_package_id}</c>): Decision A pins the path grammar, not the
    /// placeholder spelling.
    /// </summary>
    private static string NormalizeUriTemplate(string uri) =>
        Regex.Replace(uri, "\\{[^}]*\\}", "{}");

    private static Dictionary<string, (string UriForm, string Status)> ReadIndexResourceUriForms()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(VendoredIndexPath));
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var resource in doc.RootElement.GetProperty("resources").EnumerateArray())
        {
            var family = resource.GetProperty("family").GetString()!;
            var uriForm = resource.GetProperty("uriForm").GetString()!;
            var status = resource.TryGetProperty("implementationStatus", out var s)
                ? s.GetString()!
                : "implemented";
            map[family] = (uriForm, status);
        }

        return map;
    }

    [UnitTest]
    public void LiveResourceTemplates_MatchVendoredIndexUriForms_AndCoverEveryImplementedFamily()
    {
        var indexResources = ReadIndexResourceUriForms();

        // live resource-family tag -> standard family label. Sourced from the
        // emitter's canonical projection so the emitter, the live catalog, and
        // the vendored index cannot drift apart independently.
        var liveFamilyToStandard = CapabilityManifestEmitter.ResourcesByLiveFamily;

        var coveredStandardFamilies = new HashSet<string>(StringComparer.Ordinal);

        foreach (var resource in BuildResources())
        {
            if (!liveFamilyToStandard.TryGetValue(resource.Family, out var projection))
            {
                // Honua-native families with no standard analog (jobs, proposals,
                // process/feature catalog, job report) are outside the standard
                // resource vocabulary and are intentionally not projected.
                continue;
            }

            var standardFamily = projection.Family;
            indexResources.TryGetValue(standardFamily, out var indexEntry).Should().BeTrue(
                $"standard family '{standardFamily}' must exist in the vendored index.json");
            indexEntry.Status.Should().Be("implemented",
                $"family '{standardFamily}' is served by the live surface, so the index must mark it implemented");

            var uriForms = resource.DescribeTemplates()
                .Select(t => t.UriTemplate)
                .Concat(resource.Describe().Select(d => d.Uri))
                .ToArray();
            uriForms.Should().NotBeEmpty(
                $"served family '{standardFamily}' must advertise a URI form");

            foreach (var uriForm in uriForms)
            {
                NormalizeUriTemplate(uriForm).Should().Be(
                    NormalizeUriTemplate(indexEntry.UriForm),
                    $"live URI form '{uriForm}' for family '{standardFamily}' must match "
                    + $"the Decision A uriForm '{indexEntry.UriForm}' pinned in the vendored index.json");
            }

            coveredStandardFamilies.Add(standardFamily);
        }

        // Every standard family the index marks 'implemented' must be served by a
        // live template — a spec family flipped to implemented without a backing
        // live resource fails here.
        var implementedIndexFamilies = indexResources
            .Where(kvp => kvp.Value.Status == "implemented")
            .Select(kvp => kvp.Key)
            .ToArray();

        coveredStandardFamilies.Should().BeEquivalentTo(implementedIndexFamilies,
            "every index resource family marked 'implemented' must be served by a live URI template, "
            + "and every served family must be marked implemented");
    }

    // -------------------------------------------------------------------
    // Emitted-manifest conformance (Track A, #2323): the CapabilityManifestEmitter
    // projection must stay bound to the live tool catalog and score FULL against
    // the vendored index — the same coverage check conformance/check_manifest.py
    // runs in geospatial-mcp CI.
    // -------------------------------------------------------------------

    private static IReadOnlyList<IMcpTool> BuildLiveToolRoster()
    {
        // BuildTools() omits the package-review tools because they take extra
        // service dependencies; the live /mcp DI registration advertises them, so
        // the full advertised roster includes them here.
        var reviewService = Substitute.For<IPackageReviewService>();
        var jobService = Substitute.For<IGeoprocessingJobService>();
        return
        [
            .. BuildTools(),
            new ValidatePackageTool(reviewService, jobService, NullLogger<ValidatePackageTool>.Instance),
            new PreviewPackageTool(reviewService, jobService, NullLogger<PreviewPackageTool>.Instance),
        ];
    }

    [UnitTest]
    public void EmittedManifest_ToolRoster_MatchesLiveCatalog_AndTitleCasesLiveWorkflowFamily()
    {
        var manifest = CapabilityManifestEmitter.EmitManifest();
        var liveTools = BuildLiveToolRoster();

        // 1. The emitted advertised-name set is exactly the live advertised roster.
        var emittedNames = manifest.Tools.Select(t => t.AdvertisedName).ToHashSet(StringComparer.Ordinal);
        var liveNames = liveTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        emittedNames.Should().BeEquivalentTo(liveNames,
            "the emitter projection must advertise exactly the live /mcp tool roster — add or remove "
            + "the tool in CapabilityManifestEmitter.Tools when the catalog changes");

        // 2. Every emitted workflowFamily is the title-cased live telemetry family.
        var liveFamilyByName = liveTools.ToDictionary(t => t.Name, t => t.WorkflowFamily, StringComparer.Ordinal);
        foreach (var tool in manifest.Tools)
        {
            var expected = TitleCase(liveFamilyByName[tool.AdvertisedName]);
            tool.WorkflowFamily.Should().Be(expected,
                $"emitted workflowFamily for '{tool.AdvertisedName}' must be the title-cased live telemetry family");
        }
    }

    [UnitTest]
    public void EmittedManifest_ScoresFull_AgainstVendoredIndex()
    {
        // Mirrors conformance/check_manifest.py: every advertised standardName maps
        // onto the index, every 'implemented' index tool/resource family is
        // advertised, and every advertised resource uriForm equals the index.
        var manifest = CapabilityManifestEmitter.EmitManifest();

        using var doc = JsonDocument.Parse(File.ReadAllText(VendoredIndexPath));
        var indexTools = doc.RootElement.GetProperty("tools").EnumerateArray()
            .ToDictionary(
                t => t.GetProperty("standardName").GetString()!,
                t => t.TryGetProperty("implementationStatus", out var s) ? s.GetString()! : "implemented",
                StringComparer.Ordinal);
        var indexResources = ReadIndexResourceUriForms();

        // Every advertised tool maps onto a standard name.
        var advertisedStd = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in manifest.Tools)
        {
            indexTools.ContainsKey(tool.StandardName).Should().BeTrue(
                $"advertised tool '{tool.AdvertisedName}' maps to standardName '{tool.StandardName}' "
                + "which must exist in the vendored index.json");
            advertisedStd.Add(tool.StandardName);
        }

        // Every 'implemented' index tool is advertised (FULL tool coverage).
        var implementedIndexTools = indexTools.Where(kvp => kvp.Value == "implemented").Select(kvp => kvp.Key);
        advertisedStd.Should().Contain(implementedIndexTools,
            "every standard tool marked 'implemented' in the index must be advertised by the emitted manifest");

        // Every advertised resource family maps onto the index with an equal uriForm.
        var advertisedFamilies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in manifest.Resources)
        {
            indexResources.TryGetValue(resource.Family, out var indexEntry).Should().BeTrue(
                $"advertised resource family '{resource.Family}' must exist in the vendored index.json");
            resource.UriForm.Should().Be(indexEntry.UriForm,
                $"advertised uriForm for '{resource.Family}' must equal the index-pinned uriForm");
            advertisedFamilies.Add(resource.Family);
        }

        // Every 'implemented' index resource family is advertised (FULL resource coverage).
        var implementedIndexFamilies = indexResources.Where(kvp => kvp.Value.Status == "implemented").Select(kvp => kvp.Key);
        advertisedFamilies.Should().Contain(implementedIndexFamilies,
            "every standard resource family marked 'implemented' in the index must be advertised by the emitted manifest");
    }

    private static string TitleCase(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    // -------------------------------------------------------------------
    // JSON-schema loading + fixture helpers
    // -------------------------------------------------------------------

    private static string SerializeLive(JsonElement schema) => schema.GetRawText();

    private static StandardSchema LoadSchema(string path)
    {
        var resolver = new JSchemaPreloadedResolver();
        PreloadVendoredSchemas(resolver);
        using var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(File.ReadAllText(path)));
        return StandardSchema.Load(reader, new JSchemaReaderSettings { Resolver = resolver });
    }

    private static StandardSchema LoadSchemaFromJson(string json)
    {
        var resolver = new JSchemaPreloadedResolver();
        PreloadVendoredSchemas(resolver);
        using var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(json));
        return StandardSchema.Load(reader, new JSchemaReaderSettings { Resolver = resolver });
    }

    private static void PreloadVendoredSchemas(JSchemaPreloadedResolver resolver)
    {
        // Register every vendored schema by both its $id and relative file paths
        // so cross-file $ref (e.g. result-package -> artifact.schema.json,
        // ../common/geoprocessing-error.schema.json) resolves offline.
        foreach (var file in Directory.EnumerateFiles(SchemaRoot, "*.json", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("$id", out var id) && id.GetString() is { } idValue)
            {
                resolver.Add(new Uri(idValue), content);
            }

            // Relative-path forms used by $ref within the resources/ directory.
            var fileName = Path.GetFileName(file);
            resolver.Add(new Uri(fileName, UriKind.Relative), content);
            var relFromResources = Path.GetRelativePath(Path.Join(SchemaRoot, "resources"), file)
                .Replace(Path.DirectorySeparatorChar, '/');
            resolver.Add(new Uri(relFromResources, UriKind.Relative), content);
        }
    }

    private static JToken? ReadFixtureInputs(string fixtureFile, string? expectedSchemaRef = null)
    {
        var root = JObject.Parse(File.ReadAllText(fixtureFile));
        if (expectedSchemaRef is not null &&
            root.TryGetValue("schemaRef", out var schemaRef) &&
            !string.Equals(schemaRef.Value<string>(), expectedSchemaRef, StringComparison.Ordinal))
        {
            return null;
        }

        return root.TryGetValue("inputs", out var inputs) ? inputs : null;
    }

    private static void AssertPayloadConformsToResourceSchema(JToken payload, string schemaFile, string reason)
    {
        var schema = LoadSchema(Path.Join(SchemaRoot, "resources", schemaFile));
        payload.IsValid(schema, out IList<string> errors);
        errors.Should().BeEmpty($"{reason} must conform to '{schemaFile}'");
    }

    /// <summary>
    /// Reads <paramref name="property"/> from <paramref name="parent"/> and, if it is a
    /// draft 2020-12 <c>$ref</c> into local <c>$defs</c>, resolves the referenced
    /// schema against <paramref name="root"/>. Only the local
    /// <c>#/$defs/&lt;name&gt;</c> form used by the vendored schemas is supported.
    /// </summary>
    private static JsonElement ResolveRef(JsonElement root, JsonElement parent, string property)
    {
        parent.TryGetProperty(property, out var value).Should().BeTrue(
            $"schema must expose property '{property}'");

        if (value.TryGetProperty("$ref", out var refElement)
            && refElement.GetString() is { } reference
            && reference.StartsWith("#/$defs/", StringComparison.Ordinal))
        {
            var defName = reference["#/$defs/".Length..];
            root.GetProperty("$defs").TryGetProperty(defName, out var resolved).Should().BeTrue(
                $"schema must define $defs/{defName}");
            return resolved;
        }

        return value;
    }

    private static string[] EnumStrings(JsonElement schema)
    {
        schema.TryGetProperty("enum", out var enumElement).Should().BeTrue(
            "schema node must declare an enum");
        enumElement.ValueKind.Should().Be(JsonValueKind.Array);
        return enumElement.EnumerateArray().Select(e => e.GetString()!).ToArray();
    }
}
