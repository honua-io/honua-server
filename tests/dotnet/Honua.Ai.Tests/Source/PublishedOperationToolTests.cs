// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Coverage for #2483 (ADR-0056 Increment 4): validated operations-toolset descriptors
/// projected as first-class MCP tools. Verifies the projection (typed schema +
/// annotations), governance through the policy decision point, deterministic
/// param-keyed caching, deterministic mode, and the surface merge into
/// <c>tools/list</c> / <c>tools/call</c>.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class PublishedOperationToolTests
{
    private const string DeterministicReadOnlyOpId = "geo.summary";
    private const string MutatingOpId = "geo.export";

    // ---- Descriptor projection -------------------------------------------------

    [UnitTest]
    public void Describe_ReadOnlyDeterministicDescriptor_ProjectsTypedToolWithSchemaAndAnnotations()
    {
        var tool = new PublishedOperationTool(
            DeterministicReadOnlyDescriptor(), "cat-v1", NullLogger.Instance);

        var descriptor = tool.Describe();

        descriptor.Name.Should().Be("honua_op_geo_summary");
        descriptor.Title.Should().NotBeNullOrWhiteSpace();
        descriptor.Description.Should().Contain("policy decision point");
        descriptor.Description.Should().Contain("deterministic");

        // Typed input schema projected from the descriptor's parameters.
        descriptor.InputSchema.GetProperty("type").GetString().Should().Be("object");
        descriptor.InputSchema.GetProperty("properties").TryGetProperty("layerId", out _).Should().BeTrue();
        descriptor.InputSchema.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("layerId");

        // Output schema + read-only annotation for a read-only descriptor.
        descriptor.OutputSchema.Should().NotBeNull();
        descriptor.OutputSchema!.Value.GetProperty("properties").TryGetProperty("deterministic", out _)
            .Should().BeTrue();
        descriptor.Annotations!.ReadOnlyHint.Should().BeTrue();
    }

    [UnitTest]
    public void Describe_MutatingDescriptor_ProjectsWriteAnnotations()
    {
        var tool = new PublishedOperationTool(MutatingDescriptor(), "cat-v1", NullLogger.Instance);

        var descriptor = tool.Describe();

        descriptor.Name.Should().Be("honua_op_geo_export");
        descriptor.Annotations!.ReadOnlyHint.Should().BeFalse("a data-mutating operation is not read-only");
    }

    [UnitTest]
    public void ProjectName_SanitizesOperationIdIntoToolName()
    {
        PublishedOperationTool.ProjectName("service.publish").Should().Be("honua_op_service_publish");
        PublishedOperationTool.ProjectName("Geo.Buffer-2").Should().Be("honua_op_geo_buffer_2");
    }

    // ---- Governance through the policy decision point --------------------------

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_op_geo_export")]
    public async Task Invoke_PolicyDenies_ReturnsDeniedWithoutExecuting()
    {
        var executor = new RecordingExecutor(MutatingOpId);
        var invoker = Dispatcher(
            MutatingDescriptor(),
            executor,
            new OperationPolicyOptions
            {
                Enabled = true,
                DefaultDecision = PolicyDecisionKind.Deny,
                DefaultReason = "Denied by policy on this tier.",
            });

        var tool = new PublishedOperationTool(MutatingDescriptor(), "cat-v1", NullLogger.Instance);
        var result = await tool.InvokeAsync(Context(invoker), Args("""{"layerId":"7"}"""), CancellationToken.None);

        var body = result.StructuredContent!.Value;
        body.GetProperty("status").GetString().Should().Be("Denied");
        body.GetProperty("requiresApproval").GetBoolean().Should().BeFalse();
        executor.SubmitCount.Should().Be(0, "a denied operation must never reach the executor");
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call honua_op_geo_export")]
    public async Task Invoke_PolicyRequiresApproval_SurfacesApprovalLane()
    {
        var invoker = Dispatcher(
            MutatingDescriptor(),
            new RecordingExecutor(MutatingOpId),
            new OperationPolicyOptions
            {
                Enabled = true,
                DefaultDecision = PolicyDecisionKind.RequireApproval,
                DefaultApprovalLane = "operator-gate",
                DefaultReason = "Requires operator approval.",
            });

        var tool = new PublishedOperationTool(MutatingDescriptor(), "cat-v1", NullLogger.Instance);
        var result = await tool.InvokeAsync(Context(invoker), Args("""{"layerId":"7"}"""), CancellationToken.None);

        var body = result.StructuredContent!.Value;
        body.GetProperty("status").GetString().Should().Be("RequiresApproval");
        body.GetProperty("requiresApproval").GetBoolean().Should().BeTrue();
        body.GetProperty("approvalLane").GetString().Should().Be("operator-gate");
    }

    [UnitTest]
    public async Task Invoke_PopulatesTierAndRolesOnPolicyContext()
    {
        OperationPolicyContext? captured = null;
        var invoker = new CapturingInvoker((_, ctx) =>
        {
            captured = ctx;
            return CompletedHandle(MutatingOpId);
        });

        var tool = new PublishedOperationTool(MutatingDescriptor(), "cat-v1", NullLogger.Instance);
        var context = Context(invoker, license: ProEdition(), roles: ["operator", "publisher"]);

        await tool.InvokeAsync(context, Args("""{"layerId":"7"}"""), CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Tier.Should().Be("pro", "tier is resolved from the running edition for tier-aware policy");
        captured.Roles.Should().BeEquivalentTo("operator", "publisher");
        captured.PrincipalId.Should().Be("agent-x");
    }

    // ---- Deterministic, param-keyed caching ------------------------------------

    [UnitTest]
    public async Task Invoke_DeterministicReadOnly_CachesOnParams()
    {
        var invoker = new CountingInvoker(_ => CompletedHandle(DeterministicReadOnlyOpId));
        var cache = new PublishedOperationCache();
        var tool = new PublishedOperationTool(DeterministicReadOnlyDescriptor(), "cat-v1", NullLogger.Instance);

        var first = await tool.InvokeAsync(
            Context(invoker, cache), Args("""{"layerId":"7"}"""), CancellationToken.None);
        var second = await tool.InvokeAsync(
            Context(invoker, cache), Args("""{"layerId":"7"}"""), CancellationToken.None);

        invoker.SubmitCount.Should().Be(1, "identical inputs must be served from the param-keyed cache");
        first.StructuredContent!.Value.GetProperty("cacheHit").GetBoolean().Should().BeFalse();
        second.StructuredContent!.Value.GetProperty("cacheHit").GetBoolean().Should().BeTrue();
        second.StructuredContent!.Value.GetProperty("deterministic").GetBoolean().Should().BeTrue();

        // A different parameter is a cache miss → re-executes.
        await tool.InvokeAsync(Context(invoker, cache), Args("""{"layerId":"9"}"""), CancellationToken.None);
        invoker.SubmitCount.Should().Be(2, "a different parameter set is a distinct cache key");
    }

    [UnitTest]
    public async Task Invoke_SameParams_DifferentPrincipal_MissesCacheAndTakesFreshPolicyRoundTrip()
    {
        // Security regression (PR #2584 review): the cache key includes the full
        // policy-relevant principal context, and a cache hit skips the policy
        // decision point — so a result cached under a policy-ALLOWED privileged
        // caller must NEVER be served to a different caller with the same params.
        // Policy here: role "admin" → Allow, everyone else → Deny. Caller A (admin)
        // executes and is cached; caller B (no roles), same params, must MISS the
        // cache, take a fresh policy round-trip, and be DENIED — not receive A's
        // cached result.
        var executor = new RecordingExecutor(DeterministicReadOnlyOpId);
        var invoker = Dispatcher(
            DeterministicReadOnlyDescriptor(),
            executor,
            new OperationPolicyOptions
            {
                Enabled = true,
                DefaultDecision = PolicyDecisionKind.Deny,
                DefaultReason = "Denied for non-admin callers.",
                Rules =
                {
                    new OperationPolicyRule { OperationId = "*", Role = "admin", Decision = PolicyDecisionKind.Allow },
                },
            });
        var cache = new PublishedOperationCache();
        var tool = new PublishedOperationTool(DeterministicReadOnlyDescriptor(), "cat-v1", NullLogger.Instance);

        // Privileged caller A: allowed, executed, cached.
        var first = await tool.InvokeAsync(
            Context(invoker, cache, roles: ["admin"], principalName: "agent-a"),
            Args("""{"layerId":"7"}"""),
            CancellationToken.None);
        first.StructuredContent!.Value.GetProperty("status").GetString().Should().Be("Completed");
        executor.SubmitCount.Should().Be(1);

        // Low-privilege caller B, identical params: fresh policy round-trip → Denied.
        var second = await tool.InvokeAsync(
            Context(invoker, cache, roles: null, principalName: "agent-b"),
            Args("""{"layerId":"7"}"""),
            CancellationToken.None);

        var body = second.StructuredContent!.Value;
        body.GetProperty("status").GetString().Should().Be("Denied",
            "the policy decision point must be consulted for B instead of serving A's cached allow");
        body.GetProperty("cacheHit").GetBoolean().Should().BeFalse();
        executor.SubmitCount.Should().Be(1, "the denied caller never reaches the executor");

        // And A's own cache entry is unaffected: A re-invoking with the same params
        // is a hit with no re-execution.
        var third = await tool.InvokeAsync(
            Context(invoker, cache, roles: ["admin"], principalName: "agent-a"),
            Args("""{"layerId":"7"}"""),
            CancellationToken.None);
        third.StructuredContent!.Value.GetProperty("cacheHit").GetBoolean().Should().BeTrue();
        executor.SubmitCount.Should().Be(1);
    }

    [UnitTest]
    public async Task Invoke_SameParams_SamePrincipalDifferentRoles_MissesCache()
    {
        // Roles are part of the cache key: the same principal whose role set changed
        // (e.g. a revoked grant) must not ride a result cached under the old roles.
        var invoker = new CountingInvoker(_ => CompletedHandle(DeterministicReadOnlyOpId));
        var cache = new PublishedOperationCache();
        var tool = new PublishedOperationTool(DeterministicReadOnlyDescriptor(), "cat-v1", NullLogger.Instance);

        await tool.InvokeAsync(
            Context(invoker, cache, roles: ["admin"]), Args("""{"layerId":"7"}"""), CancellationToken.None);
        await tool.InvokeAsync(
            Context(invoker, cache, roles: ["viewer"]), Args("""{"layerId":"7"}"""), CancellationToken.None);

        invoker.SubmitCount.Should().Be(2, "a changed role set is a distinct principal context and misses the cache");
    }

    [UnitTest]
    public async Task Invoke_MutatingOperation_IsNeverCached()
    {
        var invoker = new CountingInvoker(_ => CompletedHandle(MutatingOpId));
        var cache = new PublishedOperationCache();
        var tool = new PublishedOperationTool(MutatingDescriptor(), "cat-v1", NullLogger.Instance);

        await tool.InvokeAsync(Context(invoker, cache), Args("""{"layerId":"7"}"""), CancellationToken.None);
        var second = await tool.InvokeAsync(
            Context(invoker, cache), Args("""{"layerId":"7"}"""), CancellationToken.None);

        invoker.SubmitCount.Should().Be(2, "a side-effecting operation must re-execute every time");
        second.StructuredContent!.Value.GetProperty("cacheHit").GetBoolean().Should().BeFalse();
        second.StructuredContent!.Value.TryGetProperty("cacheKey", out _)
            .Should().BeFalse("a non-cacheable operation carries no cache key");
    }

    [UnitTest]
    public async Task Invoke_WhenInvokerUnavailable_ReturnsFailedWithoutThrowing()
    {
        var tool = new PublishedOperationTool(DeterministicReadOnlyDescriptor(), "cat-v1", NullLogger.Instance);

        var result = await tool.InvokeAsync(
            Context(invoker: null), Args("""{"layerId":"7"}"""), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("status").GetString().Should().Be("Failed");
        result.StructuredContent!.Value.GetProperty("message").GetString()
            .Should().Contain("operations toolset is unavailable");
    }

    // ---- Tool source: publish / deterministic mode -----------------------------

    [UnitTest]
    public async Task Source_Disabled_PublishesNothing()
    {
        var source = new PublishedOperationToolSource(
            Catalog(DeterministicReadOnlyDescriptor(), MutatingDescriptor()),
            Options.Create(new McpPublishedOperationOptions { Enabled = false }),
            NullLogger<PublishedOperationToolSource>.Instance);

        (await source.GetToolsAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [UnitTest]
    public async Task Source_Enabled_PublishesDescriptorsAndExcludesHandAuthoredOps()
    {
        var source = new PublishedOperationToolSource(
            Catalog(DeterministicReadOnlyDescriptor(), MutatingDescriptor(), ServicePublishDescriptor()),
            Options.Create(new McpPublishedOperationOptions { Enabled = true }),
            NullLogger<PublishedOperationToolSource>.Instance);

        var names = (await source.GetToolsAsync(CancellationToken.None)).Select(t => t.Name).ToArray();

        names.Should().Contain(["honua_op_geo_summary", "honua_op_geo_export"]);
        names.Should().NotContain("honua_op_service_publish",
            "service.publish is already exposed by honua_publish_service");
    }

    [UnitTest]
    public async Task Source_DeterministicMode_PublishesOnlyDeterministicDescriptors()
    {
        var source = new PublishedOperationToolSource(
            Catalog(DeterministicReadOnlyDescriptor(), MutatingDescriptor()),
            Options.Create(new McpPublishedOperationOptions { Enabled = true, DeterministicOnly = true }),
            NullLogger<PublishedOperationToolSource>.Instance);

        var names = (await source.GetToolsAsync(CancellationToken.None)).Select(t => t.Name).ToArray();

        names.Should().Contain("honua_op_geo_summary");
        names.Should().NotContain("honua_op_geo_export",
            "the mutating operation is AI-assisted and excluded from deterministic mode");
    }

    // ---- Surface merge: published tools appear in tools/list and are callable ---

    [UnitTest]
    [Endpoint("POST /mcp tools/list")]
    public async Task Surface_MergesPublishedTool_IntoToolsListAndToolsCall()
    {
        var invoker = new CountingInvoker(_ => CompletedHandle(DeterministicReadOnlyOpId));
        var source = new PublishedOperationToolSource(
            Catalog(DeterministicReadOnlyDescriptor()),
            Options.Create(new McpPublishedOperationOptions { Enabled = true }),
            NullLogger<PublishedOperationToolSource>.Instance);

        var surface = new McpOperatorSurface(
            [],
            [],
            NullLogger<McpOperatorSurface>.Instance,
            limits: null,
            toolSources: [source]);

        // tools/list advertises the runtime-published tool.
        var listResponse = await surface.DispatchAsync(
            AuthenticatedContext(new ServiceCollection().BuildServiceProvider()),
            Rpc("l1", "tools/list", null),
            CancellationToken.None);

        listResponse!.Result!.Value.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .Should().Contain("honua_op_geo_summary");

        // tools/call routes to the published tool.
        var services = new ServiceCollection().AddSingleton<IOperationInvoker>(invoker)
            .AddSingleton<IPublishedOperationCache>(new PublishedOperationCache())
            .BuildServiceProvider();
        var callResponse = await surface.DispatchAsync(
            AuthenticatedContext(services),
            Rpc("c1", "tools/call", """{"name":"honua_op_geo_summary","arguments":{"layerId":"7"}}"""),
            CancellationToken.None);

        callResponse!.Error.Should().BeNull();
        callResponse.Result!.Value.GetProperty("isError").GetBoolean().Should().BeFalse();
        callResponse.Result!.Value.GetProperty("structuredContent").GetProperty("status").GetString()
            .Should().Be("Completed");
        invoker.SubmitCount.Should().Be(1);
    }

    [UnitTest]
    public async Task Surface_StaticToolWins_OverDynamicNameCollision()
    {
        // A dynamic source yielding a tool whose name collides with a static tool
        // must not shadow it: the static tool is listed exactly once.
        var staticTool = new StubNamedTool("honua_op_geo_summary");
        var source = new PublishedOperationToolSource(
            Catalog(DeterministicReadOnlyDescriptor()),
            Options.Create(new McpPublishedOperationOptions { Enabled = true }),
            NullLogger<PublishedOperationToolSource>.Instance);

        var surface = new McpOperatorSurface(
            [staticTool], [], NullLogger<McpOperatorSurface>.Instance, limits: null, toolSources: [source]);

        var response = await surface.DispatchAsync(
            AuthenticatedContext(new ServiceCollection().BuildServiceProvider()),
            Rpc("l2", "tools/list", null),
            CancellationToken.None);

        response!.Result!.Value.GetProperty("tools").EnumerateArray()
            .Count(t => t.GetProperty("name").GetString() == "honua_op_geo_summary")
            .Should().Be(1, "the static tool wins a name collision and is listed once");
    }

    // ---- Helpers ---------------------------------------------------------------

    private static OperationDispatcher Dispatcher(
        OperationDescriptor descriptor,
        IOperationExecutor executor,
        OperationPolicyOptions policyOptions)
        => new(
            Catalog(descriptor),
            [executor],
            new ConfigurableOperationPolicyDecisionPoint(Options.Create(policyOptions)),
            TimeProvider.System);

    private static IOperationCatalog Catalog(params OperationDescriptor[] descriptors)
    {
        var catalog = Substitute.For<IOperationCatalog>();
        catalog.GetSnapshotAsync(Arg.Any<CancellationToken>()).Returns(new OperationCatalogSnapshot
        {
            CatalogVersion = "cat-v1",
            GeneratedAt = DateTimeOffset.UnixEpoch,
            ProviderIds = ["test"],
            Operations = descriptors,
        });
        foreach (var descriptor in descriptors)
        {
            catalog.GetDescriptorAsync(descriptor.OperationId, Arg.Any<CancellationToken>())
                .Returns(descriptor);
        }

        return catalog;
    }

    private static DefaultHttpContext Context(
        IOperationInvoker? invoker,
        IPublishedOperationCache? cache = null,
        ILicenseEntitlementService? license = null,
        string[]? roles = null,
        string principalName = "agent-x")
    {
        var services = new ServiceCollection();
        if (invoker is not null)
        {
            services.AddSingleton(invoker);
        }

        services.AddSingleton<IPublishedOperationCache>(cache ?? new PublishedOperationCache());
        if (license is not null)
        {
            services.AddSingleton(license);
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, principalName) };
        claims.AddRange((roles ?? []).Select(r => new Claim(ClaimTypes.Role, r)));

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
        };
    }

    private static DefaultHttpContext AuthenticatedContext(IServiceProvider services)
    {
        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = services;
        return context;
    }

    private static ILicenseEntitlementService ProEdition()
    {
        var license = Substitute.For<ILicenseEntitlementService>();
        license.GetSnapshot().Returns(new LicenseSnapshot(
            HonuaEdition.Pro,
            true,
            LicenseValidationState.Valid,
            null,
            null,
            null,
            null,
            [],
            new HashSet<string>(),
            1,
            null));
        return license;
    }

    private static OperationHandle CompletedHandle(string operationId) => new()
    {
        OperationId = operationId,
        HandleId = "op-done",
        Status = OperationHandleStatus.Completed,
        Result = new OperationResultSummary
        {
            Summary = "ok",
            Details = new Dictionary<string, string>(StringComparer.Ordinal) { ["rows"] = "3" },
        },
    };

    private static OperationDescriptor DeterministicReadOnlyDescriptor() => new()
    {
        OperationId = DeterministicReadOnlyOpId,
        ProviderId = "test",
        Title = "Summarize layer",
        Description = "Compute a deterministic summary for a layer.",
        Category = "analysis",
        InputSchema = [Param("layerId", required: true), Param("fields", required: false)],
        OutputSchema = [],
        ExecutionKind = OperationExecutionKind.Synchronous,
        ApprovalModel = OperationApprovalModel.None,
        Policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.None,
            SideEffectClass = OperationSideEffectClass.ReadOnly,
            Determinism = OperationDeterminism.Deterministic,
            SupportsDryRun = false,
        },
    };

    private static OperationDescriptor MutatingDescriptor() => new()
    {
        OperationId = MutatingOpId,
        ProviderId = "test",
        Title = "Export layer",
        Description = "Export a layer, mutating data.",
        Category = "lifecycle",
        InputSchema = [Param("layerId", required: true)],
        OutputSchema = [],
        ExecutionKind = OperationExecutionKind.Job,
        ApprovalModel = OperationApprovalModel.OperatorGate,
        Policy = new OperationPolicyMetadata
        {
            BlastRadiusClass = OperationBlastRadiusClass.ServiceScope,
            SideEffectClass = OperationSideEffectClass.MutatesData,
            Determinism = OperationDeterminism.AiAssisted,
            SupportsDryRun = true,
        },
    };

    private static OperationDescriptor ServicePublishDescriptor() => DeterministicReadOnlyDescriptor() with
    {
        OperationId = PublishServiceTool.PublishOperationId,
        Title = "Publish service",
    };

    private static OperationParameterDescriptor Param(string name, bool required) => new()
    {
        Name = name,
        Title = name + " title",
        Required = required,
        Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text },
    };

    private static JsonElement Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static McpJsonRpcRequest Rpc(string id, string method, string? paramsJson) => new()
    {
        JsonRpc = "2.0",
        Id = Args(JsonSerializer.Serialize(id)),
        Method = method,
        Params = paramsJson is null ? null : Args(paramsJson),
    };

    private sealed class CapturingInvoker(Func<OperationRequest, OperationPolicyContext, OperationHandle> handler)
        : IOperationInvoker
    {
        public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(handler(request, context));
    }

    private sealed class CountingInvoker(Func<OperationRequest, OperationHandle> handler) : IOperationInvoker
    {
        public int SubmitCount { get; private set; }

        public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            return Task.FromResult(handler(request));
        }
    }

    private sealed class RecordingExecutor(string operationId) : IOperationExecutor
    {
        public int SubmitCount { get; private set; }

        public string OperationId => operationId;

        public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationValidation { IsValid = true, Status = "valid" });

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            return Task.FromResult(new OperationHandle
            {
                OperationId = operationId,
                HandleId = "op-run",
                Status = OperationHandleStatus.Completed,
            });
        }

        public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
            => Task.FromResult(new OperationStatus
            {
                OperationId = operationId,
                HandleId = handle.HandleId,
                Status = handle.Status,
            });
    }

    private sealed class StubNamedTool(string name) : IMcpTool
    {
        public string Name => name;

        public string WorkflowFamily => McpTelemetry.WorkflowFamily.Lifecycle;

        public McpToolDescriptor Describe() => new()
        {
            Name = name,
            Description = "static",
            InputSchema = EmptySchema,
        };

        public Task<McpToolsCallResult> InvokeAsync(
            HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        private static readonly JsonElement EmptySchema = ParseEmpty();

        private static JsonElement ParseEmpty()
        {
            using var document = JsonDocument.Parse("""{"type":"object"}""");
            return document.RootElement.Clone();
        }
    }
}
