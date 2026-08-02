// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

/// <summary>
/// End-to-end per-layer read authorization tests for layer-sourced geoprocessing
/// (honua-server#3046) on the OGC API Processes surface. A caller holding only the
/// baseline <c>Process.Execute</c> grant must not be able to read a layer it is denied
/// on by routing that read through a geoprocessing job, and the denial must be a genuine
/// per-layer decision rather than a blanket rejection of layer-sourced plans.
/// </summary>
/// <remarks>
/// The fixture publishes two layers: <c>alpha</c> (layer 0) carries a read
/// <c>AccessPolicy</c> restricted to <see cref="LayerReaderRole"/>, and <c>beta</c>
/// (layer 1) carries none. The submitting principal always holds
/// <c>process:*:execute</c>, so a 403 here can only come from the layer gate — proved by
/// the positive controls, which submit the SAME plans and are not forbidden.
/// </remarks>
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesLayerAccessAuthorizationTests
{
    private const string ProcessRunnerRole = "process-runner";
    private const string LayerReaderRole = "layer-reader";
    private const string ExecutionPath = "/ogc/processes/processes/honua-geoprocessing/execution";

    /// <summary>Layer 0 (alpha) — restricted to <see cref="LayerReaderRole"/>.</summary>
    private const int RestrictedLayerId = ServiceRbacTestFixture.AlphaLayerId;

    /// <summary>Layer 1 (beta) — no access policy, readable by any authenticated caller.</summary>
    private const int OpenLayerId = ServiceRbacTestFixture.BetaLayerId;

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_LayerSourcedPlanWithoutLayerReadGrant_ReturnsForbidden()
    {
        using var factory = CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, ProcessRunnerRole);

        using var request = BuildExecutionRequest(BufferAggregatePlan(RestrictedLayerId));

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_SpatialJoinWithUnauthorizedJoinLayer_ReturnsForbidden()
    {
        // The JOIN layer is as much a read as the target layer: a caller authorized on the
        // target must still not siphon a denied layer's attributes through joinLayerId.
        using var factory = CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, ProcessRunnerRole);

        using var request = BuildExecutionRequest(
            SpatialJoinPlan(layerId: OpenLayerId, joinLayerId: RestrictedLayerId));

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_HonuaLayerSourceWithoutLayerReadGrant_ReturnsForbidden()
    {
        // The source.honua-layer DAG connector streams a catalog layer straight into a job
        // artifact, so it is the most direct exfiltration path and must be gated too.
        using var factory = CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, ProcessRunnerRole);

        using var request = BuildExecutionRequest(HonuaLayerSourcePlan(RestrictedLayerId));

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_UnresolvableLayerId_ReturnsForbiddenWithoutRevealingExistence()
    {
        // An unknown layer id must produce the SAME denial as a forbidden one, otherwise the
        // submit path becomes an oracle for which layer ids exist. The caller here HAS the
        // layer-reader role, so only the unresolvable id can explain the 403.
        using var factory = CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, ProcessRunnerRole, LayerReaderRole);

        using var request = BuildExecutionRequest(HonuaLayerSourcePlan(9999));

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("not found");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_LayerSourcedPlanWithLayerReadGrant_IsNotForbidden()
    {
        // Positive control: the same plan the first test refused is accepted once the caller
        // holds the layer's read role, so the denial above is a per-layer decision and not a
        // blanket rejection of layer-sourced plans. (Submission then fails downstream on the
        // absent durable job store, which is not a 403.)
        using var factory = CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, ProcessRunnerRole, LayerReaderRole);

        using var request = BuildExecutionRequest(BufferAggregatePlan(RestrictedLayerId));

        var response = await client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_PlanReferencingUnrestrictedLayer_IsNotForbidden()
    {
        // Regression control: a layer with no access policy stays readable by any
        // authenticated process runner, exactly as it is through the query surfaces.
        using var factory = CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, ProcessRunnerRole);

        using var request = BuildExecutionRequest(BufferAggregatePlan(OpenLayerId));

        var response = await client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CreateFactory()
        => ServiceRbacTestFixture.CreateFactory(
            layerCatalogFactory: static () => new RbacTestLayerCatalog(
                alphaLayerMetadata: ServiceRbacTestFixture.CreateServiceMetadata(
                    readRoles: [LayerReaderRole])),
            configureServices: SeedProcessExecuteOnlyRole);

    private static string BufferAggregatePlan(int layerId)
        => BuildPlan(
            "plan-buffer-aggregate",
            "analytics.buffer-aggregate",
            "\"layerId\":\"" + Id(layerId) + "\",\"distance\":\"100\"");

    private static string SpatialJoinPlan(int layerId, int joinLayerId)
        => BuildPlan(
            "plan-spatial-join",
            "analytics.spatial-join",
            "\"layerId\":\"" + Id(layerId) + "\",\"joinLayerId\":\"" + Id(joinLayerId) + "\"");

    private static string HonuaLayerSourcePlan(int layerId)
        => BuildPlan(
            "plan-layer-source",
            "source.honua-layer",
            "\"layerId\":\"" + Id(layerId) + "\"");

    /// <summary>
    /// Builds a single-step canonical plan submission. Concatenated rather than interpolated
    /// because the payload's trailing brace run collides with raw-string interpolation
    /// delimiters at every <c>$</c> depth.
    /// </summary>
    private static string BuildPlan(string planId, string processId, string inputsJson)
        => "{\"inputs\":{\"plan\":{\"planId\":\"" + planId
            + "\",\"steps\":[{\"stepId\":\"s1\",\"kind\":\"geoprocess\",\"processId\":\"" + processId
            + "\",\"inputs\":{" + inputsJson + "}}]}}}";

    private static string Id(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static HttpRequestMessage BuildExecutionRequest(string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ExecutionPath);
        request.Headers.Add("Prefer", "respond-async");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private static void SeedProcessExecuteOnlyRole(IServiceCollection services)
    {
        services.RemoveAll<IRoleStore>();
        services.AddSingleton<IRoleStore>(new ProcessExecuteOnlyRoleStore());
    }

    /// <summary>
    /// Minimal role store granting <see cref="ProcessRunnerRole"/> only
    /// <c>process:*:execute</c>. It deliberately grants NOTHING on the alpha/beta data
    /// services, so per-layer authorization falls through to the layer's coarse
    /// <c>AccessPolicy</c> — the same path the synchronous query surfaces take.
    /// </summary>
    private sealed class ProcessExecuteOnlyRoleStore : IRoleStore
    {
        private static readonly PermissionGrant ExecuteGrant = new()
        {
            Service = "process",
            Layer = "*",
            Operation = "execute"
        };

        public Task<EffectivePermissions> GetEffectivePermissionsAsync(
            string userId,
            IReadOnlyList<string> roles,
            CancellationToken cancellationToken = default)
        {
            var permissions = roles.Contains(ProcessRunnerRole, StringComparer.OrdinalIgnoreCase)
                ? new[] { ExecuteGrant }
                : Array.Empty<PermissionGrant>();

            return Task.FromResult(new EffectivePermissions
            {
                UserId = userId,
                Roles = roles,
                Permissions = permissions,
                ResolvedAt = DateTimeOffset.UtcNow
            });
        }

        public Task<IReadOnlyList<RoleDefinition>> ListRolesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RoleDefinition>>([]);

        public Task<RoleDefinition?> GetRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
            => Task.FromResult<RoleDefinition?>(null);

        public Task<RoleDefinition> CreateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RoleDefinition?> UpdateRoleAsync(RoleDefinition role, CancellationToken cancellationToken = default)
            => Task.FromResult<RoleDefinition?>(null);

        public Task<bool> DeleteRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<PermissionGrant>> GetPermissionsAsync(Guid roleId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PermissionGrant>>([]);

        public Task<IReadOnlyList<PermissionGrant>> SetPermissionsAsync(
            Guid roleId,
            IReadOnlyList<PermissionGrant> permissions,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PermissionGrant>>([]);
    }
}
