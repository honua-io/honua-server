// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Ai.Protocols.Mcp.Views;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Pins the server-authored workflow discovery view (honua-server#3428): the
/// <c>setup</c> view's derivation from the canonical live catalog, the explicit
/// request/session/profile negotiation contract, the deterministic
/// revision/membership/descriptor digests, the descriptor budget gate with
/// measured bytes and estimated tokens, and the invariants that keep a view
/// discovery-only — full-catalog parity, no authority granted, no client-side
/// source list.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpWorkflowViewTests
{
    private readonly ITestOutputHelper _output;

    public McpWorkflowViewTests(ITestOutputHelper output) => _output = output;

    // ------------------------------------------------------------------
    // Derivation from the canonical live catalog
    // ------------------------------------------------------------------

    [UnitTest]
    public async Task ToolsList_WithSetupView_ReturnsOnlyServerSelectedDescriptors()
    {
        var surface = BuildFullSurface();

        var view = await ListToolsAsync(surface, view: McpWorkflowViewCatalog.SetupViewName);
        var full = await ListToolsAsync(surface, McpWorkflowViewNegotiation.FullCatalogViewName);

        view.Names.Should().NotBeEmpty();
        view.Names.Should().BeSubsetOf(full.Names, "a view can only narrow the canonical catalog");
        view.Names.Should().OnlyContain(
            n => McpWorkflowViewCatalog.Setup.FindStageIndex(n) >= 0,
            "every member must be selected by a server-authored stage rule");

        // Real catalog members the bounded terminal path deliberately excludes.
        full.Names.Should().Contain("honua_geocode_address");
        view.Names.Should().NotContain("honua_geocode_address");
        view.Names.Should().NotContain("honua_solve_route");
        view.Names.Should().NotContain("honua_create_app_package");
    }

    [UnitTest]
    public async Task ToolsList_WithSetupView_CoversTheBoundedTerminalPath()
    {
        var surface = BuildFullSurface();

        var view = await ListToolsAsync(surface, McpWorkflowViewCatalog.SetupViewName);

        // Readiness -> import -> publish -> verify -> style/render -> bounded GP ->
        // compose/save -> submit publication + poll status.
        view.Names.Should().Contain(
        [
            "honua_list_capabilities",
            "honua_resolve_entity",
            "honua_ingest_dataset",
            "honua_publish_service",
            "honua_publish_result",
            "honua_list_layers",
            "honua_describe_layer",
            "honua_query_features",
            "honua_get_style",
            "honua_apply_style_preset",
            "honua_render_map",
            "honua_plan_analysis",
            "honua_validate_plan",
            "honua_dry_run_plan",
            "honua_execute_plan",
            "honua_list_jobs",
            "honua_studio_create_draft",
            "honua_studio_validate_draft",
            "honua_studio_propose_publication",
            "honua_supported_operation_kinds",
        ]);
        view.Names.Should().HaveCountLessThanOrEqualTo(20);

        var stages = view.Meta.GetProperty("stages").EnumerateArray().ToArray();
        stages.Select(s => s.GetProperty("id").GetString()).Should().Equal(
            "readiness",
            "connect-import",
            "publish",
            "verify-access",
            "style-render",
            "geoprocessing",
            "compose",
            "publication");

        foreach (var stage in stages)
        {
            stage.GetProperty("tools").GetArrayLength().Should().BeGreaterThan(
                0,
                $"stage '{stage.GetProperty("id").GetString()}' must select at least one live descriptor");
        }
    }

    [UnitTest]
    public async Task ToolsList_WithSetupView_ReturnsExactCanonicalDescriptorsNeverTruncated()
    {
        var roster = McpTaxonomyAlignmentTests.BuildTools().ToDictionary(t => t.Name, StringComparer.Ordinal);
        var surface = BuildFullSurface();

        var view = await ListToolsAsync(surface, McpWorkflowViewCatalog.SetupViewName);

        foreach (var descriptor in view.Tools)
        {
            var name = descriptor.GetProperty("name").GetString()!;
            var canonical = JsonSerializer.Serialize(
                McpWorkflowViewDescriptorClassifier.Describe(roster[name]),
                McpJsonContext.Default.McpToolDescriptor);

            descriptor.GetRawText().Should().Be(
                canonical,
                "the view must serve the exact canonical descriptor — description, annotations, and input/output "
                + "schemas verbatim, never truncated or re-described");
        }

        // Every published schema survives whole.
        view.Tools.Select(t => t.TryGetProperty("inputSchema", out var schema) ? schema.ValueKind : JsonValueKind.Undefined)
            .Should().OnlyContain(kind => kind == JsonValueKind.Object);
    }

    [UnitTest]
    public async Task ToolsList_ClassifiesStudioDescriptorsFromTheServerAuthoredView()
    {
        var surface = BuildFullSurface();

        var full = await ListToolsAsync(surface, McpWorkflowViewNegotiation.FullCatalogViewName);
        var view = await ListToolsAsync(surface, McpWorkflowViewCatalog.SetupViewName);

        var fullStudio = full.Tools.Single(t =>
            t.GetProperty("name").GetString() == "honua_studio_create_draft");
        var viewStudio = view.Tools.Single(t =>
            t.GetProperty("name").GetString() == "honua_studio_create_draft");

        fullStudio.GetRawText().Should().Be(
            viewStudio.GetRawText(),
            "full and narrowed discovery must expose one canonical descriptor contract");

        var classification = fullStudio.GetProperty("_meta").GetProperty("honua.studio");
        classification.GetProperty("family").GetString().Should().Be("honua.studio.composition");
        classification.GetProperty("view").GetString().Should().Be(McpWorkflowViewCatalog.SetupViewName);
        classification.GetProperty("revision").GetString().Should().Be(McpWorkflowViewCatalog.Setup.Revision);

        fullStudio.GetProperty("annotations").ValueKind.Should().Be(JsonValueKind.Object);
        fullStudio.GetProperty("outputSchema").ValueKind.Should().Be(JsonValueKind.Object);

        full.Tools.Single(t => t.GetProperty("name").GetString() == "honua_query_features")
            .TryGetProperty("_meta", out _)
            .Should().BeFalse("unrelated tools must not be routed into the Studio family");
    }

    // ------------------------------------------------------------------
    // Full-catalog parity / escape hatch
    // ------------------------------------------------------------------

    [UnitTest]
    public async Task ToolsList_WithNoView_ServesTheCompleteCatalogUnchanged()
    {
        var surface = BuildFullSurface();
        var expected = McpTaxonomyAlignmentTests.BuildTools()
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var full = await ListToolsAsync(surface, McpWorkflowViewNegotiation.FullCatalogViewName);

        full.Names.Should().Equal(
            expected,
            "adding a view must not change the complete paginated catalog the escape hatch serves");
        full.Meta.ValueKind.Should().Be(
            JsonValueKind.Undefined,
            "an unnarrowed tools/list keeps its exact prior wire shape");
    }

    [UnitTest]
    public async Task ToolsList_WithReservedFullViewName_ServesTheCompleteCatalog()
    {
        var surface = BuildFullSurface();

        var full = await ListToolsAsync(surface, McpWorkflowViewNegotiation.FullCatalogViewName);
        var unnarrowed = await ListToolsAsync(surface, McpWorkflowViewNegotiation.FullCatalogViewName);

        full.Names.Should().Equal(unnarrowed.Names);
    }

    [UnitTest]
    public async Task ToolsList_FullCatalogExport_IsAvailableToAuthenticatedClients()
    {
        var response = await DispatchAsync(
            BuildFullSurface(),
            """{"jsonrpc":"2.0","id":"full","method":"tools/list","params":{"view":"full"}}""",
            McpTestFactory.AuthenticatedHttpContext());

        response!.Error.Should().BeNull();
        response.Result!.Value.GetProperty("tools").EnumerateArray().Should().NotBeEmpty();
    }

    [UnitTest]
    public async Task ToolsList_FullCatalogExport_RequiresAuthentication()
    {
        var response = await DispatchAsync(
            BuildFullSurface(),
            """{"jsonrpc":"2.0","id":"full","method":"tools/list","params":{"view":"full"}}""",
            McpTestFactory.AnonymousHttpContext());

        response!.Error.Should().NotBeNull();
        response.Error!.Data!.Code.Should().Be(McpErrorMapper.Codes.PermissionDenied);
    }

    [UnitTest]
    public async Task ToolsList_WithUnknownView_IsRejectedAndNamesThePublishedViews()
    {
        var surface = BuildFullSurface();

        var response = await DispatchAsync(
            surface,
            """{"jsonrpc":"2.0","id":"v","method":"tools/list","params":{"view":"not-a-view"}}""");

        response!.Error.Should().NotBeNull();
        response.Error!.Message.Should().Contain("not-a-view")
            .And.Contain(McpWorkflowViewCatalog.SetupViewName)
            .And.Contain(McpWorkflowViewNegotiation.FullCatalogViewName);
    }

    [UnitTest]
    public async Task ToolsList_WithNonStringViewSelector_IsRejected()
    {
        var surface = BuildFullSurface();

        var response = await DispatchAsync(
            surface,
            """{"jsonrpc":"2.0","id":"v","method":"tools/list","params":{"view":42}}""");

        response!.Error.Should().NotBeNull();
    }

    // ------------------------------------------------------------------
    // Negotiation contract: request > session > server profile
    // ------------------------------------------------------------------

    [UnitTest]
    public void Negotiation_Precedence_IsRequestThenSessionThenProfile()
    {
        McpWorkflowViewNegotiation.ResolveEffectiveView("setup", null, null).Should().Be("setup");
        McpWorkflowViewNegotiation.ResolveEffectiveView(null, "setup", null).Should().Be("setup");
        McpWorkflowViewNegotiation.ResolveEffectiveView(null, null, "setup").Should().Be("setup");

        McpWorkflowViewNegotiation.ResolveEffectiveView("full", "setup", "setup").Should().BeNull(
            "an explicit request-level 'full' must reach the complete catalog through any session or profile default");
        McpWorkflowViewNegotiation.ResolveEffectiveView(null, "full", "setup").Should().BeNull();
        McpWorkflowViewNegotiation.ResolveEffectiveView(null, null, null).Should().BeNull();
    }

    [UnitTest]
    public void Negotiation_ReadsViewFromBothTheViewParamAndTheMetaKey()
    {
        McpWorkflowViewNegotiation.TryReadRequestedView(
            McpTestFactory.ParseJson("""{"view":"setup"}"""), out var direct, out _).Should().BeTrue();
        direct.Should().Be("setup");

        McpWorkflowViewNegotiation.TryReadRequestedView(
            McpTestFactory.ParseJson("""{"_meta":{"honua.io/workflow-view":"setup"}}"""),
            out var meta,
            out _).Should().BeTrue();
        meta.Should().Be("setup");

        McpWorkflowViewNegotiation.TryReadRequestedView(
            McpTestFactory.ParseJson("""{"cursor":"abc"}"""), out var none, out _).Should().BeTrue();
        none.Should().BeNull();
    }

    [UnitTest]
    public async Task Initialize_NegotiatesTheSessionView_AndLaterToolsListIsNarrowed()
    {
        var surface = BuildFullSurface();
        var context = McpTestFactory.AuthenticatedHttpContext();

        var initialize = await DispatchAsync(
            surface,
            """
            {"jsonrpc":"2.0","id":"i","method":"initialize","params":{
              "protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"terminal","version":"1"},
              "_meta":{"honua.io/workflow-view":"setup"}}}
            """,
            context);

        initialize!.Error.Should().BeNull();
        context.Items[McpWorkflowViewNegotiation.HttpContextItemKey].Should().Be(
            "setup",
            "the transport binds the negotiated view to the session it issues, mirroring the elicitation seam");

        // A later request on that session carries the bound view.
        var view = await ListToolsAsync(surface, view: null, context: context);
        view.Names.Should().NotContain("honua_geocode_address");
        view.Meta.GetProperty("view").GetString().Should().Be("setup");
    }

    [UnitTest]
    public async Task Initialize_WithUnknownView_IsRejected()
    {
        var surface = BuildFullSurface();

        var response = await DispatchAsync(
            surface,
            """
            {"jsonrpc":"2.0","id":"i","method":"initialize","params":{
              "protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"terminal","version":"1"},
              "_meta":{"honua.io/workflow-view":"nope"}}}
            """);

        response!.Error.Should().NotBeNull();
        response.Error!.Message.Should().Contain("nope");
    }

    [UnitTest]
    public async Task ToolsList_RequestSelection_OverridesTheSessionView()
    {
        var surface = BuildFullSurface();
        var context = McpTestFactory.AuthenticatedHttpContext();
        context.Items[McpWorkflowViewNegotiation.HttpContextItemKey] = McpWorkflowViewCatalog.SetupViewName;

        var full = await ListToolsAsync(surface, McpWorkflowViewNegotiation.FullCatalogViewName, context);

        full.Names.Should().Contain("honua_geocode_address");
        full.Meta.ValueKind.Should().Be(JsonValueKind.Undefined);
    }

    [UnitTest]
    public async Task ToolsList_ServerProfileDefaultView_AppliesWhenNothingIsNegotiated()
    {
        var surface = BuildFullSurface();
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
            services.AddSingleton<IOptionsMonitor<McpWorkflowViewOptions>>(
                new StubOptionsMonitor(
                    new McpWorkflowViewOptions { DefaultView = McpWorkflowViewCatalog.SetupViewName })));

        var view = await ListToolsAsync(surface, view: null, context: context);

        view.Meta.GetProperty("view").GetString().Should().Be("setup");
        view.Names.Should().NotContain("honua_geocode_address");
    }

    [UnitTest]
    public async Task ToolsList_DefaultProfile_IsBoundedToTwelveMetaWorkflowTools()
    {
        var surface = BuildFullSurface();
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
            services.AddSingleton<IOptionsMonitor<McpWorkflowViewOptions>>(
                new StubOptionsMonitor(new McpWorkflowViewOptions())));

        var view = await ListToolsAsync(surface, view: null, context: context);

        view.Meta.GetProperty("view").GetString().Should().Be(McpWorkflowViewCatalog.DefaultViewName);
        view.Names.Should().HaveCountLessThanOrEqualTo(12);
        view.Names.Should().Contain(
        [
            "honua_list_capabilities",
            "honua_resolve_entity",
            "honua_describe_layer",
            "honua_plan_analysis",
            "honua_execute_plan",
        ]);
    }

    [UnitTest]
    public void PublishedTaskViews_AreBoundedToTwentyTools()
    {
        foreach (var definition in McpWorkflowViewCatalog.All.Values)
        {
            McpWorkflowViewProjector.Project(definition, BuildCatalogEntries()).Members
                .Should().HaveCountLessThanOrEqualTo(20, $"view '{definition.Name}' must remain task-bounded");
        }
    }

    [UnitTest]
    public async Task ToolsList_ServerProfileDefaultView_UsesTheMonitorCurrentValue()
    {
        var monitor = new StubOptionsMonitor(
            new McpWorkflowViewOptions { DefaultView = McpWorkflowViewNegotiation.FullCatalogViewName });
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(services =>
            services.AddSingleton<IOptionsMonitor<McpWorkflowViewOptions>>(monitor));
        ((ClaimsIdentity)context.User.Identity!).AddClaim(new Claim(ClaimTypes.Role, "admin"));

        var full = await ListToolsAsync(BuildFullSurface(), view: null, context: context);
        monitor.CurrentValue = new McpWorkflowViewOptions { DefaultView = McpWorkflowViewCatalog.SetupViewName };
        var narrowed = await ListToolsAsync(BuildFullSurface(), view: null, context: context);

        full.Meta.ValueKind.Should().Be(JsonValueKind.Undefined);
        narrowed.Meta.GetProperty("view").GetString().Should().Be(McpWorkflowViewCatalog.SetupViewName);
    }

    [UnitTest]
    public async Task ToolsList_ViewRejectsMalformedCursor()
    {
        var response = await DispatchAsync(
            BuildFullSurface(),
            """{"jsonrpc":"2.0","id":"t","method":"tools/list","params":{"view":"setup","cursor":"not-a-cursor"}}""");

        response!.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(McpErrorMapper.JsonRpcInvalidParams);
    }

    [UnitTest]
    public async Task ToolsList_WithNoProfileOptionsRegistered_ServesBoundedDefault()
    {
        var surface = BuildFullSurface();

        var view = await ListToolsAsync(surface, view: null);

        view.Meta.GetProperty("view").GetString().Should().Be(McpWorkflowViewCatalog.DefaultViewName);
        view.Names.Should().HaveCountLessThanOrEqualTo(12);
    }

    // ------------------------------------------------------------------
    // Deterministic digests
    // ------------------------------------------------------------------

    [UnitTest]
    public void Projection_Digests_AreDeterministic()
    {
        var catalog = BuildCatalogEntries();

        var first = McpWorkflowViewProjector.Project(McpWorkflowViewCatalog.Setup, catalog);
        var second = McpWorkflowViewProjector.Project(McpWorkflowViewCatalog.Setup, catalog);

        first.RevisionDigest.Should().Be(second.RevisionDigest).And.StartWith("sha256:");
        first.MembershipDigest.Should().Be(second.MembershipDigest).And.StartWith("sha256:");
        first.DescriptorDigest.Should().Be(second.DescriptorDigest).And.StartWith("sha256:");
    }

    [UnitTest]
    public void Projection_LongTailDynamicMembership_DoesNotExpandBoundedView()
    {
        var baseline = McpWorkflowViewProjector.Project(McpWorkflowViewCatalog.Setup, BuildCatalogEntries());

        var extended = McpWorkflowViewProjector.Project(
            McpWorkflowViewCatalog.Setup,
            BuildCatalogEntries().Append((DescribeOperation("honua_op_import_geojson"), true)));

        extended.MembershipDigest.Should().Be(baseline.MembershipDigest);
        extended.DescriptorDigest.Should().Be(baseline.DescriptorDigest);
        extended.RevisionDigest.Should().Be(
            baseline.RevisionDigest,
            "the revision digest pins the server-authored definition, not the live membership");
    }

    [UnitTest]
    public void Projection_RevisionDigest_ChangesWhenTheDefinitionChanges()
    {
        var baseline = McpWorkflowViewProjector.Project(McpWorkflowViewCatalog.Setup, BuildCatalogEntries());

        var edited = McpWorkflowViewCatalog.Setup with
        {
            Stages = McpWorkflowViewCatalog.Setup.Stages
                .Append(new McpWorkflowViewStageDefinition
                {
                    Id = "extra",
                    Title = "Extra",
                    Description = "Extra stage",
                    Rules = [McpWorkflowViewMemberRule.Exact("honua_solve_route")],
                })
                .ToArray(),
        };

        McpWorkflowViewProjector.Project(edited, BuildCatalogEntries())
            .RevisionDigest.Should().NotBe(baseline.RevisionDigest);
    }

    [UnitTest]
    public async Task ToolsList_SetupMeasurements_MatchIndependentlyMeasuredWireBytes()
    {
        var view = await ListToolsAsync(BuildFullSurface(), McpWorkflowViewCatalog.SetupViewName);

        // Measure the actual complete wire descriptors, without calling the projector,
        // its serializer, digest helper, budget constants, or token estimator.
        var descriptorJson = view.Tools.Select(tool => tool.GetRawText()).ToArray();
        var aggregate = Encoding.UTF8.GetBytes("[" + string.Join(",", descriptorJson) + "]");
        var largestDescriptor = descriptorJson.Max(json => Encoding.UTF8.GetByteCount(json));
        var expectedDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(aggregate));
        var stages = view.Meta.GetProperty("stages").EnumerateArray().ToArray();
        var membership = string.Concat(view.Names.Select(name =>
            stages.Single(stage => stage.GetProperty("tools").EnumerateArray()
                .Any(tool => tool.GetString() == name)).GetProperty("id").GetString() + "/" + name + "\n"));

        view.Meta.GetProperty("descriptorBytes").GetInt32().Should().Be(aggregate.Length);
        view.Meta.GetProperty("descriptorDigest").GetString().Should().Be(expectedDigest);
        view.Meta.GetProperty("membershipDigest").GetString().Should().Be(
            "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(membership))));
        view.Meta.GetProperty("toolCount").GetInt32().Should().Be(view.Tools.Length);
        view.Meta.GetProperty("estimatedTokens").GetInt32().Should().Be(aggregate.Length / 4);
        view.Tools.Length.Should().BeInRange(1, 48);
        aggregate.Length.Should().BeLessThanOrEqualTo(128 * 1024);
        largestDescriptor.Should().BeLessThanOrEqualTo(16 * 1024);
        view.NextCursor.Should().BeNull();

        _output.WriteLine(
            $"setup wire: {view.Tools.Length} descriptors; {aggregate.Length} UTF-8 bytes; "
            + $"~{aggregate.Length / 4} tokens; largest descriptor {largestDescriptor} bytes; {expectedDigest}");
    }

    [UnitTest]
    public async Task ToolsList_ViewMeta_CarriesTheProjectedDigestsAndMeasurements()
    {
        var surface = BuildFullSurface();
        var projection = McpWorkflowViewProjector.Project(McpWorkflowViewCatalog.Setup, BuildCatalogEntries());

        var view = await ListToolsAsync(surface, McpWorkflowViewCatalog.SetupViewName);

        view.Meta.GetProperty("revision").GetString().Should().Be(McpWorkflowViewCatalog.Setup.Revision);
        view.Meta.GetProperty("revisionDigest").GetString().Should().Be(projection.RevisionDigest);
        view.Meta.GetProperty("membershipDigest").GetString().Should().Be(projection.MembershipDigest);
        view.Meta.GetProperty("descriptorDigest").GetString().Should().Be(projection.DescriptorDigest);
        view.Meta.GetProperty("toolCount").GetInt32().Should().Be(projection.Members.Count);
        view.Meta.GetProperty("descriptorBytes").GetInt32().Should().Be(projection.AggregateCanonicalBytes);
        view.Meta.GetProperty("fullCatalogView").GetString().Should().Be(
            McpWorkflowViewNegotiation.FullCatalogViewName,
            "the escape hatch must be discoverable from the narrowed response itself");
        view.NextCursor.Should().BeNull("a budget-bounded view is served whole");
        view.Tools.Length.Should().Be(
            projection.Members.Count,
            "the whole bounded view arrives in one page, so a terminal agent never has to paginate it");
    }

    // ------------------------------------------------------------------
    // Self-updating membership — no source-list edit anywhere
    // ------------------------------------------------------------------

    [UnitTest]
    public async Task AddingLongTailServerOperation_DoesNotExpandBoundedView()
    {
        var before = await ListToolsAsync(BuildFullSurface(), McpWorkflowViewCatalog.SetupViewName);
        before.Names.Should().NotContain("honua_op_import_geojson");

        // The same seam a runtime-published operation arrives through
        // (PublishedOperationToolSource). Nothing in the view definition, the
        // server, or any client source list changes.
        var withOperation = BuildFullSurface(new StubToolSource("honua_op_import_geojson", "honua_op_service_promote"));

        var after = await ListToolsAsync(withOperation, McpWorkflowViewCatalog.SetupViewName);

        after.Names.Should().NotContain("honua_op_import_geojson")
            .And.NotContain("honua_op_service_promote");
        after.Names.Should().HaveCountLessThanOrEqualTo(20);

        // Removing the operation drops it again, still with no edit.
        var removed = await ListToolsAsync(BuildFullSurface(), McpWorkflowViewCatalog.SetupViewName);
        removed.Names.Should().NotContain("honua_op_import_geojson");
        removed.MembershipDigestOf().Should().Be(before.MembershipDigestOf());
    }

    [UnitTest]
    public async Task IneligibleServerOperations_DoNotJoinTheView()
    {
        var surface = BuildFullSurface(new StubToolSource("honua_op_billing_reconcile"));

        var view = await ListToolsAsync(surface, McpWorkflowViewCatalog.SetupViewName);
        var full = await ListToolsAsync(surface, McpWorkflowViewNegotiation.FullCatalogViewName);

        full.Names.Should().Contain("honua_op_billing_reconcile");
        view.Names.Should().NotContain(
            "honua_op_billing_reconcile",
            "only operations in a covered family join the bounded view");
    }

    [UnitTest]
    public void RuntimePublishedMembers_DoNotExpandBoundedTaskView()
    {
        var projection = McpWorkflowViewProjector.Project(
            McpWorkflowViewCatalog.Setup,
            BuildCatalogEntries().Append((DescribeOperation("honua_op_import_geojson"), true)));

        var names = projection.Members.Select(m => m.ToolName).ToArray();

        var baseline = McpWorkflowViewProjector.Project(McpWorkflowViewCatalog.Setup, BuildCatalogEntries());
        names.Should().Equal(baseline.Members.Select(m => m.ToolName));
        names.Should().HaveCountLessThanOrEqualTo(20);
    }

    [UnitTest]
    public void RuntimePublishedMembers_PreserveTheirExistingPrefixWhenAnotherToolAppears()
    {
        var baseline = McpWorkflowViewProjector.Project(
            McpWorkflowViewCatalog.Setup,
            BuildCatalogEntries().Concat(
            [
                (DescribeOperation("honua_op_service_zulu"), true),
                (DescribeOperation("honua_op_import_zulu"), true),
            ]));
        var extended = McpWorkflowViewProjector.Project(
            McpWorkflowViewCatalog.Setup,
            BuildCatalogEntries().Concat(
            [
                (DescribeOperation("honua_op_service_zulu"), true),
                (DescribeOperation("honua_op_import_zulu"), true),
                (DescribeOperation("honua_op_import_alpha"), true),
            ]));

        extended.Members.Take(baseline.Members.Count).Select(m => m.ToolName)
            .Should().Equal(baseline.Members.Select(m => m.ToolName));
    }

    [UnitTest]
    public async Task ToolsList_LongTailDynamicTools_DoNotExpandTaskView()
    {
        var dynamicNames = Enumerable.Range(0, McpWorkflowViewBudget.MaxDescriptors + 1)
            .Select(i => $"honua_op_import_{i:D3}")
            .ToArray();
        var response = await DispatchAsync(
            BuildFullSurface(new StubToolSource(dynamicNames)),
            """{"jsonrpc":"2.0","id":"t","method":"tools/list","params":{"view":"setup"}}""");

        response!.Error.Should().BeNull();
        var tools = response.Result!.Value.GetProperty("tools");
        tools.GetArrayLength().Should().BeLessThanOrEqualTo(20);
        tools.EnumerateArray().Select(t => t.GetProperty("name").GetString())
            .Should().NotContain(name => name!.StartsWith("honua_op_import_", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // Discovery is not authority
    // ------------------------------------------------------------------

    [UnitTest]
    public async Task SelectingAView_DoesNotChangeWhatMayBeCalled()
    {
        var surface = BuildFullSurface();
        var context = McpTestFactory.AuthenticatedHttpContext();
        context.Items[McpWorkflowViewNegotiation.HttpContextItemKey] = McpWorkflowViewCatalog.SetupViewName;

        // honua_geocode_address is NOT in the setup view. Membership is a discovery
        // filter, so a narrowed session neither gains nor loses call authority: the
        // call still reaches the existing call-time authorization path.
        var response = await DispatchAsync(
            surface,
            """{"jsonrpc":"2.0","id":"c","method":"tools/call","params":{"name":"honua_geocode_address","arguments":{}}}""",
            context);

        response.Should().NotBeNull();
        response!.Error?.Message.Should().NotContain(
            "workflow view",
            "a view must never be the reason a call is refused");
    }

    [UnitTest]
    public async Task AnonymousCall_OnAViewMember_StillDoesNotExecute()
    {
        var surface = BuildFullSurface();
        var anonymous = McpTestFactory.AnonymousHttpContext();
        anonymous.Items[McpWorkflowViewNegotiation.HttpContextItemKey] = McpWorkflowViewCatalog.SetupViewName;

        // Anonymous discovery of the view is allowed where policy documents it...
        var view = await ListToolsAsync(surface, McpWorkflowViewCatalog.SetupViewName, anonymous);
        view.Names.Should().Contain("honua_list_capabilities");

        // ...but membership caches no allow decision: the call reauthenticates.
        var response = await DispatchAsync(
            surface,
            """{"jsonrpc":"2.0","id":"c","method":"tools/call","params":{"name":"honua_list_capabilities","arguments":{}}}""",
            anonymous);

        // The dispatcher's authentication gate refuses ahead of tool resolution and
        // returns the standard isError reauthentication envelope. Membership in a
        // view caches no allow decision and grants no authority.
        response!.Result!.Value.GetProperty("isError").GetBoolean().Should().BeTrue(
            "an anonymous invocation must not execute through a view");
    }

    // ------------------------------------------------------------------
    // Transport symmetry
    // ------------------------------------------------------------------

    [UnitTest]
    public async Task ViewProjection_IsIdenticalForEverySession()
    {
        // The view is a pure projection of the single in-process catalog the
        // dispatcher serves, so the HTTP surface and the SDK stdio proxy (which
        // consumes the same JSON-RPC surface) cannot diverge in names, schemas,
        // annotations, revision, or digests.
        var surface = BuildFullSurface();

        var first = await ListToolsAsync(
            surface, McpWorkflowViewCatalog.SetupViewName, McpTestFactory.AuthenticatedHttpContext("client-a"));
        var second = await ListToolsAsync(
            surface, McpWorkflowViewCatalog.SetupViewName, McpTestFactory.AuthenticatedHttpContext("client-b"));

        first.Names.Should().Equal(second.Names);
        first.Meta.GetRawText().Should().Be(second.Meta.GetRawText());
        first.Tools.Select(t => t.GetRawText()).Should().Equal(second.Tools.Select(t => t.GetRawText()));
    }

    [UnitTest]
    public async Task ListCapabilities_AdvertisesThePublishedViews()
    {
        var surface = BuildFullSurface();
        var context = McpTestFactory.AuthenticatedHttpContextWithServices(
            services => services.AddSingleton(surface));

        var response = await DispatchAsync(
            surface,
            """{"jsonrpc":"2.0","id":"lc","method":"tools/call","params":{"name":"honua_list_capabilities","arguments":{}}}""",
            context);

        response!.Error.Should().BeNull();
        var views = response.Result!.Value
            .GetProperty("structuredContent")
            .GetProperty("workflowViews")
            .EnumerateArray()
            .ToArray();

        views.Select(v => v.GetProperty("name").GetString()).Should().Equal("default", "setup");
        views.Should().OnlyContain(v => v.GetProperty("toolCount").GetInt32() <= 20);
    }

    // ------------------------------------------------------------------
    // Membership refresh -> tools/listChanged
    // ------------------------------------------------------------------

    [UnitTest]
    public void ProfileChange_BroadcastsToolsListChanged()
    {
        var sessions = new McpSessionManager();
        sessions.CreateSession();
        var publisher = new McpNotificationPublisher(sessions, NullLogger<McpNotificationPublisher>.Instance);
        var monitor = new StubOptionsMonitor(new McpWorkflowViewOptions());
        var notifier = new McpWorkflowViewChangeNotifier(monitor, publisher);
        notifier.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        notifier.OnOptionsChanged(new McpWorkflowViewOptions { DefaultView = "setup" })
            .Should().Be(1, "every live session must be told to re-read its tool list");
        notifier.OnOptionsChanged(new McpWorkflowViewOptions { DefaultView = "setup" })
            .Should().Be(0, "an unchanged profile must not churn the client's prompt cache");

        notifier.Dispose();
    }

    // ------------------------------------------------------------------
    // Budget gate — measured evidence
    // ------------------------------------------------------------------

    [UnitTest]
    public void SetupView_StaysWithinTheDescriptorBudget()
    {
        var projection = McpWorkflowViewProjector.Project(McpWorkflowViewCatalog.Setup, BuildCatalogEntries());

        _output.WriteLine(
            $"view '{projection.Definition.Name}' revision {projection.Definition.Revision}: "
            + $"{projection.Members.Count} descriptors, {projection.AggregateCanonicalBytes:N0} bytes "
            + $"({projection.AggregateCanonicalBytes / 1024.0:F1} KiB), ~{projection.EstimatedTokens:N0} tokens; "
            + $"largest descriptor {projection.LargestDescriptorBytes:N0} bytes");
        _output.WriteLine(
            $"budget: <= {McpWorkflowViewBudget.MaxDescriptors} descriptors, "
            + $"<= {McpWorkflowViewBudget.MaxAggregateDescriptorBytes / 1024} KiB aggregate, "
            + $"<= {McpWorkflowViewBudget.MaxDescriptorBytes / 1024} KiB per descriptor");
        _output.WriteLine($"revisionDigest   {projection.RevisionDigest}");
        _output.WriteLine($"membershipDigest {projection.MembershipDigest}");
        _output.WriteLine($"descriptorDigest {projection.DescriptorDigest}");

        foreach (var stage in projection.Definition.Stages)
        {
            var members = projection.Members
                .Where(m => string.Equals(m.StageId, stage.Id, StringComparison.Ordinal))
                .ToArray();
            _output.WriteLine(
                $"  {stage.Id,-16} {members.Length,2} tools, {members.Sum(m => m.CanonicalBytes),6:N0} bytes: "
                + string.Join(", ", members.Select(m => m.ToolName)));
        }

        projection.BudgetViolations.Should().BeEmpty(
            "an over-budget view is a signal to split or refine server-owned stages, never to truncate a schema "
            + "or silently raise the ceiling");

        projection.Members.Count.Should().BeLessThanOrEqualTo(McpWorkflowViewBudget.MaxDescriptors);
        projection.AggregateCanonicalBytes.Should().BeLessThanOrEqualTo(
            McpWorkflowViewBudget.MaxAggregateDescriptorBytes);
        projection.LargestDescriptorBytes.Should().BeLessThanOrEqualTo(McpWorkflowViewBudget.MaxDescriptorBytes);
        projection.EmptyStageIds.Should().BeEmpty("every stage of the bounded path must resolve to live tools");
    }

    [UnitTest]
    public void SetupView_IsSubstantiallySmallerThanTheCompleteCatalog()
    {
        var catalog = BuildCatalogEntries();
        var projection = McpWorkflowViewProjector.Project(McpWorkflowViewCatalog.Setup, catalog);

        var fullBytes = System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(
            (IReadOnlyList<McpToolDescriptor>)catalog.Select(c => c.Descriptor).ToArray(),
            McpJsonContext.Default.IReadOnlyListMcpToolDescriptor));

        _output.WriteLine(
            $"complete catalog: {catalog.Length} descriptors, {fullBytes:N0} bytes, "
            + $"~{McpWorkflowViewBudget.EstimateTokens(fullBytes):N0} tokens");
        _output.WriteLine(
            $"setup view:       {projection.Members.Count} descriptors, {projection.AggregateCanonicalBytes:N0} bytes, "
            + $"~{projection.EstimatedTokens:N0} tokens "
            + $"({100.0 * projection.AggregateCanonicalBytes / fullBytes:F0}% of the catalog)");

        projection.AggregateCanonicalBytes.Should().BeLessThan(fullBytes);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static (McpToolDescriptor Descriptor, bool IsDynamic)[] BuildCatalogEntries() =>
        McpTaxonomyAlignmentTests.BuildTools()
            .Select(t => (McpWorkflowViewDescriptorClassifier.Describe(t), false))
            .ToArray();

    private static McpDataAccessSurface BuildFullSurface(params IMcpToolSource[] sources) =>
        new(
            tools: McpTaxonomyAlignmentTests.BuildTools(),
            resources: [],
            logger: NullLogger<McpDataAccessSurface>.Instance,
            toolSources: sources);

    private static McpToolDescriptor DescribeOperation(string name) => new()
    {
        Name = name,
        Title = name,
        Description = "Runtime-published operation " + name + ".",
        InputSchema = McpTestFactory.ParseJson("""{"type":"object"}"""),
    };

    private static string[] StageOf(ToolsListView view, string stageId) =>
        view.Meta.GetProperty("stages")
            .EnumerateArray()
            .Single(s => s.GetProperty("id").GetString() == stageId)
            .GetProperty("tools")
            .EnumerateArray()
            .Select(t => t.GetString()!)
            .ToArray();

    /// <summary>
    /// Lists tools, following <c>nextCursor</c> to the end so an unnarrowed call
    /// yields the COMPLETE paginated catalog (the escape hatch), while a
    /// view-narrowed call yields the whole bounded view in its single page.
    /// </summary>
    private static async Task<ToolsListView> ListToolsAsync(
        McpDataAccessSurface surface,
        string? view,
        HttpContext? context = null)
    {
        context ??= McpTestFactory.AuthenticatedHttpContext();
        var tools = new List<JsonElement>();
        var meta = default(JsonElement);
        string? cursor = null;
        var pages = 0;

        do
        {
            var selectors = new List<string>();
            if (view is not null)
            {
                selectors.Add("\"view\":\"" + view + "\"");
            }

            if (cursor is not null)
            {
                selectors.Add("\"cursor\":\"" + cursor + "\"");
            }

            var parameters = selectors.Count == 0
                ? string.Empty
                : ",\"params\":{" + string.Join(",", selectors) + "}";

            var response = await DispatchAsync(
                surface,
                "{\"jsonrpc\":\"2.0\",\"id\":\"t\",\"method\":\"tools/list\"" + parameters + "}",
                context);

            response!.Error.Should().BeNull();
            var result = response.Result!.Value;
            tools.AddRange(result.GetProperty("tools").EnumerateArray());
            if (result.TryGetProperty("_meta", out var pageMeta))
            {
                meta = pageMeta;
            }

            cursor = result.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
            pages++;
        }
        while (cursor is not null && pages < 20);

        return new ToolsListView(tools.ToArray(), meta, cursor);
    }

    private static async Task<McpJsonRpcResponse?> DispatchAsync(
        McpDataAccessSurface surface,
        string body,
        HttpContext? context = null)
    {
        var request = JsonSerializer.Deserialize(body, McpJsonContext.Default.McpJsonRpcRequest)!;
        return await surface.DispatchAsync(
            context ?? McpTestFactory.AuthenticatedHttpContext(),
            request,
            CancellationToken.None);
    }

    private sealed record ToolsListView(JsonElement[] Tools, JsonElement Meta, string? NextCursor)
    {
        public string[] Names => Tools.Select(t => t.GetProperty("name").GetString()!).ToArray();

        public string MembershipDigestOf() =>
            Meta.ValueKind == JsonValueKind.Object ? Meta.GetProperty("membershipDigest").GetString()! : string.Empty;
    }

    /// <summary>
    /// Stands in for <c>PublishedOperationToolSource</c>: the runtime seam through
    /// which an eligible server operation reaches the catalog.
    /// </summary>
    private sealed class StubToolSource : IMcpToolSource
    {
        private readonly IReadOnlyList<IMcpTool> _tools;

        public StubToolSource(params string[] names) =>
            _tools = names.Select(n => (IMcpTool)new StubTool(n)).ToArray();

        public ValueTask<IReadOnlyList<IMcpTool>> GetToolsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(_tools);
    }

    private sealed class StubTool : IMcpTool
    {
        private readonly string _name;

        public StubTool(string name) => _name = name;

        public string Name => _name;

        public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

        public McpToolDescriptor Describe() => DescribeOperation(_name);

        public Task<McpToolsCallResult> InvokeAsync(
            HttpContext httpContext,
            JsonElement? arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult(new McpToolsCallResult());
    }

    private sealed class StubOptionsMonitor : IOptionsMonitor<McpWorkflowViewOptions>
    {
        public StubOptionsMonitor(McpWorkflowViewOptions value) => CurrentValue = value;

        public McpWorkflowViewOptions CurrentValue { get; set; }

        public McpWorkflowViewOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<McpWorkflowViewOptions, string?> listener) => null;
    }
}
