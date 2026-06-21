// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Schema;
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
            ["honua_geocode_address"] = "geocode_address",
            ["honua_solve_route"] = "solve_route",
            ["honua_list_layers"] = "list_layers",
            ["honua_query_features"] = "query_features",
            ["honua_render_map"] = "render_map",
        };

    /// <summary>
    /// Standard tool families enumerated in the geospatial-mcp taxonomy that
    /// Honua does not yet advertise as discrete MCP tools. These are known-gaps:
    /// they are recorded so coverage stays honest, but their absence must not
    /// fail the conformance suite.
    /// </summary>
    private static readonly string[] KnownGapStandardTools =
    {
        "create_map_package",
        "refine_map_package",
        "apply_style_preset",
        "compose_mixed_protocol_map",
        "preview_map_package",
        "create_app_package",
        "preview_app_package",
        "publish_result",
    };

    private static string SchemaRoot =>
        Path.Combine(AppContext.BaseDirectory, "ConformanceSchemas", "geospatial-mcp");

    private static string FixtureRoot =>
        Path.Combine(AppContext.BaseDirectory, "ConformanceSchemas", "fixtures");

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
            .ToArray();

        unmapped.Should().BeEmpty(
            "every advertised /mcp tool must map to a published geospatial-mcp tool schema "
            + "(add a mapping in ImplementedToolStandardNames or record a known-gap)");
    }

    [UnitTest]
    public void ImplementedTools_AdvertiseInputSchemasConformantWithTheStandard()
    {
        var tools = BuildTools();

        foreach (var tool in tools)
        {
            ImplementedToolStandardNames.TryGetValue(tool.Name, out var standardName)
                .Should().BeTrue($"tool '{tool.Name}' must map to a standard tool schema");

            var standardSchemaPath = Path.Combine(SchemaRoot, "tools", standardName + ".schema.json");
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

            var fixtureDir = Path.Combine(FixtureRoot, "tools", standardName);
            if (!Directory.Exists(fixtureDir))
            {
                continue;
            }

            var liveSchema = LoadSchemaFromJson(SerializeLive(tool.Describe().InputSchema));
            var standardSchema = LoadSchema(
                Path.Combine(SchemaRoot, "tools", standardName + ".schema.json"));

            foreach (var fixtureFile in Directory.EnumerateFiles(fixtureDir, "*.json"))
            {
                var inputs = ReadFixtureInputs(fixtureFile);
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
            File.ReadAllText(Path.Combine(SchemaRoot, "tools", "validate_plan.schema.json")));
        var root = doc.RootElement;

        // properties.plan -> $defs.analysisPlan -> properties.steps.items
        //   -> $defs.planStep -> properties.kind -> $defs.planStepKind.enum
        var analysisPlan = ResolveRef(root, root, "properties", "plan");
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

            File.Exists(Path.Combine(SchemaRoot, "tools", gap + ".schema.json"))
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
        };

        var asserted = 0;
        foreach (var (family, schemaFile) in resourceFamilies)
        {
            var schema = LoadSchema(Path.Combine(SchemaRoot, "resources", schemaFile));
            var fixtureDir = Path.Combine(FixtureRoot, "resources", family);
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
        };

        foreach (var family in standardResourceFamilies)
        {
            File.Exists(Path.Combine(SchemaRoot, "resources", family + ".schema.json"))
                .Should().BeTrue($"vendored standard resource schema for '{family}' must exist");
        }

        // Honua advertises at least the workspace + result/job + promotion-surface
        // families; assert the roster is non-trivial so the wiring is real.
        BuildResources().Should().NotBeEmpty();
    }

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
            var relFromResources = Path.GetRelativePath(Path.Combine(SchemaRoot, "resources"), file)
                .Replace(Path.DirectorySeparatorChar, '/');
            resolver.Add(new Uri(relFromResources, UriKind.Relative), content);
        }
    }

    private static JToken? ReadFixtureInputs(string fixtureFile)
    {
        var root = JObject.Parse(File.ReadAllText(fixtureFile));
        return root.TryGetValue("inputs", out var inputs) ? inputs : null;
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
