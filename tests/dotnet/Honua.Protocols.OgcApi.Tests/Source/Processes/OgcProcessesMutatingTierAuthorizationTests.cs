// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
/// End-to-end authorization tests for the mutating-process execution tier (#2798) on the
/// OGC API Processes protocol surface. Proves that an operator whose grant covers only the
/// baseline <c>Process.Execute</c> permission can execute an analytic plan but receives 403
/// when the submitted plan contains a mutating (durable side-effect) step, and that the
/// denial is a genuine tier decision rather than a blanket process-execute rejection.
/// </summary>
[Protocol(TestProtocols.OgcApiProcesses)]
public sealed class OgcProcessesMutatingTierAuthorizationTests
{
    private const string ProcessRunnerRole = "process-runner";
    private const string ExecutionPath = "/ogc/processes/processes/honua-geoprocessing/execution";

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_MutatingPlanWithExecuteOnlyGrant_ReturnsForbidden()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory(
            configureServices: SeedProcessExecuteOnlyRole);
        using var client = ServiceRbacTestFixture.CreateClient(factory, ProcessRunnerRole);

        using var request = BuildExecutionRequest(
            """
            {"inputs":{"plan":{"planId":"plan-delete","steps":[{"stepId":"s1","kind":"geoprocess","processId":"data-management.delete-features","inputs":{"layerId":"0","where":"OBJECTID > 0"}}]}}}
            """);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /ogc/processes/processes/{processId}/execution")]
    public async Task Execute_AnalyticPlanWithExecuteOnlyGrant_IsNotForbidden()
    {
        // Positive control: the same Execute-only grant must clear the baseline tier for an
        // analytic plan, so a non-403 result isolates the mutating-tier denial above to the
        // execution tier rather than a blanket process-execute rejection. (Submission then
        // fails downstream on the absent durable job store, which is not a 403.)
        using var factory = ServiceRbacTestFixture.CreateFactory(
            configureServices: SeedProcessExecuteOnlyRole);
        using var client = ServiceRbacTestFixture.CreateClient(factory, ProcessRunnerRole);

        using var request = BuildExecutionRequest(
            """
            {"inputs":{"plan":{"planId":"plan-buffer","steps":[{"stepId":"s1","kind":"geoprocess","processId":"geometry.buffer","inputs":{"wkb":"AAAA","srid":"4326","distance":"100"}}]}}}
            """);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

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
    /// Minimal role store granting the <see cref="ProcessRunnerRole"/> only
    /// <c>process:*:execute</c> — deliberately withholding
    /// <see cref="OperatorOperation.ExecuteMutatingProcess"/> so the mutating-tier gate fires.
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
