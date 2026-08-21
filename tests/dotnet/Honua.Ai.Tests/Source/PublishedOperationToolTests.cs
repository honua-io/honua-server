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
using Honua.Core.Features.Security;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Server.Features.Operations.Admin;
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
        PublishedOperationTool.ProjectName("admin.server.status").Should().Be("honua_admin_server_status");
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
        captured.PrincipalId.Should().Be("oidc:subject:https%3A%2F%2Fissuer.example:agent-x");
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
    public async Task Invoke_SameParams_SamePrincipalDifferentTenantOrPermissions_MissesCache()
    {
        var invoker = new CountingInvoker(_ => CompletedHandle(DeterministicReadOnlyOpId));
        var cache = new PublishedOperationCache();
        var tool = new PublishedOperationTool(DeterministicReadOnlyDescriptor(), "cat-v1", NullLogger.Instance);

        await tool.InvokeAsync(
            Context(invoker, cache, tenantId: "tenant-a", permissions: ["admin:read"]),
            Args("""{"layerId":"7"}"""),
            CancellationToken.None);
        await tool.InvokeAsync(
            Context(invoker, cache, tenantId: "tenant-b", permissions: ["admin:read"]),
            Args("""{"layerId":"7"}"""),
            CancellationToken.None);
        await tool.InvokeAsync(
            Context(invoker, cache, tenantId: "tenant-b", permissions: ["admin:list"]),
            Args("""{"layerId":"7"}"""),
            CancellationToken.None);

        invoker.SubmitCount.Should().Be(3,
            "tenant changes and permission downgrades must take a fresh authorization path");
    }

    [UnitTest]
    public async Task Invoke_DifferentOidcSubjectsWithSameDisplayName_MissCache()
    {
        var invoker = new CountingInvoker(_ => CompletedHandle(DeterministicReadOnlyOpId));
        var cache = new PublishedOperationCache();
        var tool = new PublishedOperationTool(DeterministicReadOnlyDescriptor(), "cat-v1", NullLogger.Instance);

        await tool.InvokeAsync(
            Context(invoker, cache, principalName: "Same Name", subjectId: "subject-a"),
            Args("""{"layerId":"7"}"""),
            CancellationToken.None);
        await tool.InvokeAsync(
            Context(invoker, cache, principalName: "Same Name", subjectId: "subject-b"),
            Args("""{"layerId":"7"}"""),
            CancellationToken.None);

        invoker.SubmitCount.Should().Be(2, "display names are not cache identities");
    }

    [UnitTest]
    public async Task Invoke_SameOidcSubjectFromDifferentIssuers_MissesCache()
    {
        var invoker = new CountingInvoker(_ => CompletedHandle(DeterministicReadOnlyOpId));
        var cache = new PublishedOperationCache();
        var tool = new PublishedOperationTool(DeterministicReadOnlyDescriptor(), "cat-v1", NullLogger.Instance);

        await tool.InvokeAsync(
            Context(
                invoker,
                cache,
                subjectId: "shared-subject",
                subjectIssuer: "https://issuer-a.example"),
            Args("""{"layerId":"7"}"""),
            CancellationToken.None);
        await tool.InvokeAsync(
            Context(
                invoker,
                cache,
                subjectId: "shared-subject",
                subjectIssuer: "https://issuer-b.example"),
            Args("""{"layerId":"7"}"""),
            CancellationToken.None);

        invoker.SubmitCount.Should().Be(2,
            "issuer-qualified identities with the same subject must never share cached authorization results");
    }

    [UnitTest]
    public async Task Invoke_ForgedApiKeyIdFromDifferentOidcIssuersCannotCollideInCache()
    {
        var invoker = new CountingInvoker(_ => CompletedHandle(DeterministicReadOnlyOpId));
        var cache = new PublishedOperationCache();
        var tool = new PublishedOperationTool(DeterministicReadOnlyDescriptor(), "cat-v1", NullLogger.Instance);
        const string forgedKeyId = "01234567-89ab-cdef-0123-456789abcdef";

        await tool.InvokeAsync(
            Context(
                invoker,
                cache,
                subjectId: "shared-subject",
                subjectIssuer: "https://issuer-a.example",
                forgedApiKeyId: forgedKeyId),
            Args("""{"layerId":"7"}"""),
            CancellationToken.None);
        await tool.InvokeAsync(
            Context(
                invoker,
                cache,
                subjectId: "shared-subject",
                subjectIssuer: "https://issuer-b.example",
                forgedApiKeyId: forgedKeyId),
            Args("""{"layerId":"7"}"""),
            CancellationToken.None);

        invoker.SubmitCount.Should().Be(2,
            "an issuer-controlled api_key_id must not replace the issuer-qualified OIDC actor");
    }

    [UnitTest]
    public async Task Invoke_ClientCertificateSubjectExecutesAndCachesOnlyForSameMappedPrincipal()
    {
        var invoker = new CountingInvoker(_ => CompletedHandle(DeterministicReadOnlyOpId));
        var cache = new PublishedOperationCache();
        var tool = new PublishedOperationTool(DeterministicReadOnlyDescriptor(), "cat-v1", NullLogger.Instance);

        await tool.InvokeAsync(
            Context(
                invoker,
                cache,
                subjectId: "native-prod-admin",
                subjectIssuer: null,
                authenticationType: FrameworkAuthenticationIdentity.ClientCertificateAuthenticationType),
            Args("""{"layerId":"7"}"""),
            CancellationToken.None);
        var repeat = await tool.InvokeAsync(
            Context(
                invoker,
                cache,
                subjectId: "native-prod-admin",
                subjectIssuer: null,
                authenticationType: FrameworkAuthenticationIdentity.ClientCertificateAuthenticationType),
            Args("""{"layerId":"7"}"""),
            CancellationToken.None);
        await tool.InvokeAsync(
            Context(
                invoker,
                cache,
                subjectId: "native-prod-reader",
                subjectIssuer: null,
                authenticationType: FrameworkAuthenticationIdentity.ClientCertificateAuthenticationType),
            Args("""{"layerId":"7"}"""),
            CancellationToken.None);

        invoker.SubmitCount.Should().Be(2);
        repeat.StructuredContent!.Value.GetProperty("cacheHit").GetBoolean().Should().BeTrue();
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
    public async Task Invoke_AdminReadOnlyOperation_IsNeverCached()
    {
        const string operationId = "admin.server.status";
        var invoker = new CountingInvoker(_ => CompletedHandle(operationId));
        var cache = new PublishedOperationCache();
        var tool = new PublishedOperationTool(
            AdminDescriptor(operationId, deterministic: true, readOnly: true),
            "cat-v1",
            NullLogger.Instance);

        await tool.InvokeAsync(Context(invoker, cache), Args("""{"layerId":"7"}"""), CancellationToken.None);
        await tool.InvokeAsync(Context(invoker, cache), Args("""{"layerId":"7"}"""), CancellationToken.None);

        invoker.SubmitCount.Should().Be(2,
            "admin operations always take a fresh authorization and execution path");
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
            Options.Create(new McpPublishedOperationOptions { Enabled = true, DeterministicOnly = false }),
            NullLogger<PublishedOperationToolSource>.Instance);

        var names = (await source.GetToolsAsync(CancellationToken.None)).Select(t => t.Name).ToArray();

        names.Should().Contain(["honua_op_geo_summary", "honua_op_geo_export"]);
        names.Should().NotContain("honua_op_service_publish",
            "service.publish is already exposed by honua_publish_service");
    }

    [UnitTest]
    public async Task Source_Enabled_PublishesSecretSafeAdminRosterWithReservedNamesAndTypedSchemas()
    {
        var adminCatalog = new AdminOpenApiOperationCatalog(FindAdminOpenApi());
        using var catalog = new OperationCatalog(
            [new AdminOperationDescriptorProvider(adminCatalog)],
            TimeProvider.System);
        var source = new PublishedOperationToolSource(
            catalog,
            Options.Create(new McpPublishedOperationOptions { Enabled = true }),
            NullLogger<PublishedOperationToolSource>.Instance);

        var tools = await source.GetToolsAsync(CancellationToken.None);

        tools.Should().HaveCount(385,
            "all 396 Admin OpenAPI operations are classified and eleven secret/session issuers are withheld");
        tools.Should().HaveCount(adminCatalog.Definitions.Count - AdminPublishedOperationSafety.WithheldOperationCount);
        tools.Select(tool => tool.Name).Should().OnlyHaveUniqueItems();
        AdminPublishedOperationSafety.Exclusions.Should().OnlyContain(exclusion =>
            !string.IsNullOrWhiteSpace(exclusion.OpenApiOperationId)
            && !string.IsNullOrWhiteSpace(exclusion.Code)
            && !string.IsNullOrWhiteSpace(exclusion.Reason));
        AdminPublishedOperationSafety.Exclusions.Select(exclusion => exclusion.OpenApiOperationId)
            .Should().OnlyHaveUniqueItems();
        var excludedIds = AdminPublishedOperationSafety.Exclusions
            .Select(exclusion => exclusion.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        tools.Select(tool => tool.Name).Should().BeEquivalentTo(
            adminCatalog.Definitions
                .Where(definition => !excludedIds.Contains(definition.Descriptor.OperationId))
                .Select(definition => PublishedOperationTool.ProjectName(definition.Descriptor.OperationId)),
            "every Admin OpenAPI operation must be either projected or have an explicit exclusion record");
        tools.Select(tool => tool.Name).Should().OnlyContain(name => name.StartsWith("honua_admin_", StringComparison.Ordinal));
        tools.Select(tool => tool.Name).Should().NotContain(
            "honua_admin_api_key_create",
            "honua_admin_api_key_rotate",
            "honua_admin_oauth_client_create",
            "honua_admin_openapi_get_admin_auth_session",
            "honua_admin_openapi_logout_admin_auth_session");
        AdminPublishedOperationSafety.Exclusions.Should().ContainSingle(exclusion =>
            exclusion.OperationId == "admin.openapi.logout-admin-auth-session"
            && exclusion.OpenApiOperationId == "logoutAdminAuthSession"
            && exclusion.Code == "session-bound-auth-flow"
            && exclusion.Reason.Contains("ID-token hint", StringComparison.Ordinal));
        AdminPublishedOperationSafety.Exclusions.Should().ContainSingle(exclusion =>
            exclusion.OperationId == "admin.openapi.get-admin-auth-session"
            && exclusion.OpenApiOperationId == "getAdminAuthSession"
            && exclusion.Code == "session-bound-auth-flow"
            && exclusion.Reason.Contains("cookie session", StringComparison.Ordinal));
        var createConnection = tools.Should().ContainSingle(tool => tool.Name == "honua_admin_connection_create").Subject;
        var body = createConnection.Describe().InputSchema.GetProperty("properties").GetProperty("body");
        body.GetProperty("type").GetString().Should().Be("object");
        body.GetProperty("properties").TryGetProperty("secretReference", out _).Should().BeTrue();
        body.GetProperty("properties").TryGetProperty("password", out _).Should().BeFalse();
        var oidcCreate = tools.Should().ContainSingle(tool => tool.Name == "honua_admin_oidc_provider_create").Subject;
        oidcCreate.Describe().InputSchema.GetProperty("properties").GetProperty("body")
            .GetProperty("properties").TryGetProperty("clientSecret", out _).Should().BeFalse();
        var geoserverImport = tools.Should()
            .ContainSingle(tool => tool.Name == "honua_admin_openapi_start_geo_server_import").Subject;
        var geoserverBody = geoserverImport.Describe().InputSchema.GetProperty("properties").GetProperty("body")
            .GetProperty("properties");
        geoserverBody.TryGetProperty("honuaApiKey", out _).Should().BeFalse();
        geoserverBody.TryGetProperty("honuaApiKeySecretReference", out _).Should().BeTrue();
        var rateLimitBody = tools.Should()
            .ContainSingle(tool => tool.Name == "honua_admin_rate_limit_create").Subject
            .Describe().InputSchema.GetProperty("properties").GetProperty("body")
            .GetProperty("properties");
        rateLimitBody.TryGetProperty("key", out _).Should().BeTrue(
            "a rate-limit partition key is an identifier, not credential material");
    }

    [UnitTest]
    public async Task Invoke_RateLimitCreate_PreservesNonSecretKeyWhileSecretFieldsRemainDenied()
    {
        OperationRequest? submitted = null;
        var invoker = new CountingInvoker(request =>
        {
            submitted = request;
            return CompletedHandle("admin.rate-limit.create");
        });
        var adminCatalog = new AdminOpenApiOperationCatalog(FindAdminOpenApi());
        using var catalog = new OperationCatalog(
            [new AdminOperationDescriptorProvider(adminCatalog)],
            TimeProvider.System);
        var source = new PublishedOperationToolSource(
            catalog,
            Options.Create(new McpPublishedOperationOptions { Enabled = true }),
            NullLogger<PublishedOperationToolSource>.Instance);
        var tool = (await source.GetToolsAsync(CancellationToken.None))
            .Single(candidate => candidate.Name == "honua_admin_rate_limit_create");

        var result = await tool.InvokeAsync(
            Context(invoker),
            Args("""{"body":{"scope":"tenant","key":"tenant-a","permitLimit":10,"windowSeconds":60}}"""),
            CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("status").GetString().Should().Be("Completed");
        invoker.SubmitCount.Should().Be(1);
        submitted.Should().NotBeNull();
        submitted!.Parameters["body"].Should().Contain("\"key\":\"tenant-a\"");

        var denied = await tool.InvokeAsync(
            Context(invoker),
            Args("""{"body":{"scope":"tenant","key":"tenant-a","password":"must-not-cross-mcp"}}"""),
            CancellationToken.None);
        denied.StructuredContent!.Value.GetProperty("status").GetString().Should().Be("Denied");
        invoker.SubmitCount.Should().Be(1, "real credential fields remain fail-closed");
    }

    [UnitTest]
    public async Task Invoke_AdminOperationWithRawCredential_DeniesBeforeInvoker()
    {
        var invoker = new CountingInvoker(_ => CompletedHandle("admin.connection.create"));
        var source = new PublishedOperationToolSource(
            Catalog(AdminDescriptor("admin.connection.create", deterministic: true, readOnly: false)),
            Options.Create(new McpPublishedOperationOptions { Enabled = true }),
            NullLogger<PublishedOperationToolSource>.Instance);
        var tool = (await source.GetToolsAsync(CancellationToken.None)).Single();

        var result = await tool.InvokeAsync(
            Context(invoker),
            Args("""{"body":{"name":"example","honuaApiKey":"must-not-cross-mcp"}}"""),
            CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("status").GetString().Should().Be("Denied");
        result.StructuredContent.Value.GetProperty("message").GetString().Should().Contain("secret reference");
        invoker.SubmitCount.Should().Be(0);
    }

    [UnitTest]
    public async Task Source_Default_PublishesOnlyDeterministicAdminDescriptors()
    {
        var source = new PublishedOperationToolSource(
            Catalog(
                DeterministicReadOnlyDescriptor(),
                AdminDescriptor("admin.connections.list", deterministic: true, readOnly: true),
                AdminDescriptor("admin.services.delete", deterministic: true, readOnly: false),
                AdminDescriptor("admin.services.suggest", deterministic: false, readOnly: true)),
            Options.Create(new McpPublishedOperationOptions()),
            NullLogger<PublishedOperationToolSource>.Instance);

        var tools = await source.GetToolsAsync(CancellationToken.None);

        tools.Should().HaveCount(2);
        tools.Select(tool => tool.Describe().Title)
            .Should().BeEquivalentTo("admin.connections.list", "admin.services.delete");
        tools.Select(tool => tool.Describe().Annotations).Should().Contain(annotation =>
            annotation != null && annotation.ReadOnlyHint == true && annotation.DestructiveHint == false);
        tools.Select(tool => tool.Describe().Annotations).Should().Contain(annotation =>
            annotation != null && annotation.ReadOnlyHint == false && annotation.DestructiveHint == true);
    }

    [UnitTest]
    public void CacheKey_CanonicalEnvelope_IsOrderIndependentAndDelimiterCollisionSafe()
    {
        var context = new OperationPolicyContext
        {
            PrincipalId = "oidc:subject:https%3A%2F%2Fissuer.example:subject-1",
            Tier = "Enterprise",
            TenantId = "Tenant-A",
            Roles = ["Publisher", "Viewer"],
            Permissions = ["admin:read", "jobs:read"],
        };
        var ordered = IPublishedOperationCache.BuildKey(
            "admin.server.status",
            "catalog-1",
            new Dictionary<string, string?> { ["a"] = "1", ["b"] = "2" },
            context);
        var reversed = IPublishedOperationCache.BuildKey(
            "admin.server.status",
            "catalog-1",
            new Dictionary<string, string?> { ["b"] = "2", ["a"] = "1" },
            context with { Roles = ["viewer", "publisher"], Permissions = ["jobs:read", "admin:read"] });
        ordered.Should().Be(reversed);
        ordered.Should().MatchRegex("^mcpop:v1:[0-9a-f]{64}$");

        var embeddedDelimiters = IPublishedOperationCache.BuildKey(
            "admin.server.status",
            "catalog-1",
            new Dictionary<string, string?> { ["a"] = "b;c=d" },
            context);
        var splitParameters = IPublishedOperationCache.BuildKey(
            "admin.server.status",
            "catalog-1",
            new Dictionary<string, string?> { ["a"] = "b", ["c"] = "d" },
            context);
        embeddedDelimiters.Should().NotBe(splitParameters);

        var embeddedRoleDelimiter = IPublishedOperationCache.BuildKey(
            "admin.server.status",
            "catalog-1",
            new Dictionary<string, string?>(),
            context with { Roles = ["publisher,viewer"] });
        var splitRoles = IPublishedOperationCache.BuildKey(
            "admin.server.status",
            "catalog-1",
            new Dictionary<string, string?>(),
            context with { Roles = ["publisher", "viewer"] });
        embeddedRoleDelimiter.Should().NotBe(splitRoles);
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
    public async Task Surface_DefaultAdminFamily_SnapshotCarriesSafetyAnnotations()
    {
        var source = new PublishedOperationToolSource(
            Catalog(
                AdminDescriptor("admin.connections.list", deterministic: true, readOnly: true),
                AdminDescriptor("admin.services.delete", deterministic: true, readOnly: false),
                AdminDescriptor("admin.services.suggest", deterministic: false, readOnly: true)),
            Options.Create(new McpPublishedOperationOptions()),
            NullLogger<PublishedOperationToolSource>.Instance);
        var surface = new McpDataAccessSurface(
            [], [], NullLogger<McpDataAccessSurface>.Instance, limits: null, toolSources: [source]);

        var response = await surface.DispatchAsync(
            AuthenticatedContext(new ServiceCollection().BuildServiceProvider()),
            Rpc("admin-list", "tools/list", null),
            CancellationToken.None);

        var tools = response!.Result!.Value.GetProperty("tools").EnumerateArray().ToArray();
        tools.Should().HaveCount(2, "AI-assisted admin descriptors stay unpublished by default");
        tools.Select(tool => tool.GetProperty("title").GetString())
            .Should().Equal("admin.connections.list", "admin.services.delete");

        var read = tools[0].GetProperty("annotations");
        read.GetProperty("readOnlyHint").GetBoolean().Should().BeTrue();
        read.GetProperty("destructiveHint").GetBoolean().Should().BeFalse();

        var delete = tools[1].GetProperty("annotations");
        delete.GetProperty("readOnlyHint").GetBoolean().Should().BeFalse();
        delete.GetProperty("destructiveHint").GetBoolean().Should().BeTrue();
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/list")]
    public async Task Surface_MergesPublishedTool_IntoToolsListAndToolsCall()
    {
        var invoker = new CountingInvoker(_ => CompletedHandle(DeterministicReadOnlyOpId));
        var source = new PublishedOperationToolSource(
            Catalog(DeterministicReadOnlyDescriptor()),
            Options.Create(new McpPublishedOperationOptions { Enabled = true }),
            NullLogger<PublishedOperationToolSource>.Instance);

        var surface = new McpDataAccessSurface(
            [],
            [],
            NullLogger<McpDataAccessSurface>.Instance,
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

        var surface = new McpDataAccessSurface(
            [staticTool], [], NullLogger<McpDataAccessSurface>.Instance, limits: null, toolSources: [source]);

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
        string principalName = "agent-x",
        string? subjectId = null,
        string? subjectIssuer = "https://issuer.example",
        string? tenantId = null,
        string[]? permissions = null,
        string? forgedApiKeyId = null,
        string authenticationType = "Oidc")
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

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, principalName),
            new(ClaimTypes.NameIdentifier, subjectId ?? principalName),
            new("auth_type", "oidc"),
            new(Honua.Core.Features.Security.IdentityProtocolProvenance.ClaimType, "oidc"),
        };
        if (subjectIssuer is not null)
        {
            claims.Add(new Claim("iss", subjectIssuer));
        }
        claims.AddRange((roles ?? []).Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange((permissions ?? []).Select(permission => new Claim("permission", permission)));
        if (tenantId is not null)
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }
        if (forgedApiKeyId is not null)
        {
            claims.Add(new Claim("api_key_id", forgedApiKeyId));
        }

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType)),
        };
    }

    private static DefaultHttpContext AuthenticatedContext(IServiceProvider services)
    {
        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = services;
        var identity = (ClaimsIdentity)context.User.Identity!;
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "mcp-test-subject"));
        identity.AddClaim(new Claim(IdentityProtocolProvenance.ClaimType, IdentityProtocolProvenance.Oidc));
        identity.AddClaim(new Claim("iss", "https://issuer.example"));
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

    private static OperationDescriptor AdminDescriptor(string operationId, bool deterministic, bool readOnly)
        => DeterministicReadOnlyDescriptor() with
        {
            OperationId = operationId,
            Title = operationId,
            Policy = new OperationPolicyMetadata
            {
                BlastRadiusClass = readOnly
                    ? OperationBlastRadiusClass.None
                    : OperationBlastRadiusClass.ServiceScope,
                SideEffectClass = readOnly
                    ? OperationSideEffectClass.ReadOnly
                    : OperationSideEffectClass.DestroysState,
                Determinism = deterministic
                    ? OperationDeterminism.Deterministic
                    : OperationDeterminism.AiAssisted,
                SupportsDryRun = !readOnly,
            },
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

    private static string FindAdminOpenApi()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "admin-openapi.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "docs", "developer", "api-specs", "admin-api.json")),
        };

        return candidates.First(File.Exists);
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
