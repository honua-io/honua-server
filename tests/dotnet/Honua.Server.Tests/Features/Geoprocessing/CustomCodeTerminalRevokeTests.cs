// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Geoprocessing;
using Honua.Geoprocessing.CustomCode;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Proves a custom-code job's scoped callback token is revoked the moment the job
/// reaches a terminal state (Phase-0 invariant #5: the credential never outlives the
/// job).
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.GPServer)]
[Operation(Operations.Security)]
public sealed class CustomCodeTerminalRevokeTests
{
    [UnitTest]
    public async Task OnTerminal_CustomCodeJob_RevokesScopedToken()
    {
        var issuer = new ScopedJobTokenIssuer(new MemoryCache(new MemoryCacheOptions()), NullLogger<ScopedJobTokenIssuer>.Instance);
        const string jobId = "gp-cc-terminal-1";

        var issuance = await issuer.IssueAsync(
            new ScopedJobTokenRequest(
                PrincipalId: "alice",
                TenantId: "tenant-A",
                Roles: ["data-editor:parcels"],
                JobId: jobId,
                ResourceScope: [new JobResourceScopeEntry("parcels", null, JobResourceAccess.Write)],
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30)),
            CancellationToken.None);

        // Token is valid before terminal.
        (await issuer.ValidateAsync(issuance.Token, jobId, CancellationToken.None)).Should().NotBeNull();

        var callback = CreateCallback(issuer);
        var job = TerminalCustomCodeJob(jobId, issuance.Token);

        await callback.OnTerminalAsync(job, CancellationToken.None);

        // After terminal, the token no longer validates — it was revoked.
        (await issuer.ValidateAsync(issuance.Token, jobId, CancellationToken.None)).Should().BeNull();
    }

    [UnitTest]
    public async Task OnTerminal_NonCustomCodeJob_NoTokenToRevoke_NoThrow()
    {
        var issuer = new ScopedJobTokenIssuer(new MemoryCache(new MemoryCacheOptions()), NullLogger<ScopedJobTokenIssuer>.Instance);
        var callback = CreateCallback(issuer);

        var job = new ExecutionJobRecord
        {
            OperationId = "gp-plain-1",
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "plain"
            }
        };

        var act = async () => await callback.OnTerminalAsync(job, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    private static GeoprocessingJobTerminalCallback CreateCallback(ScopedJobTokenIssuer issuer)
    {
        var progressStore = Substitute.For<IUniversalProgressStore>();
        var processCatalog = Substitute.For<IProcessCatalog>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        return new GeoprocessingJobTerminalCallback(
            progressStore,
            processCatalog,
            new StaticOptionsMonitor<GeoprocessingExecutorOptions>(new GeoprocessingExecutorOptions()),
            resultPackageStore: null,
            scopeFactory,
            NullLogger<GeoprocessingJobTerminalCallback>.Instance,
            issuer);
    }

    private static ExecutionJobRecord TerminalCustomCodeJob(string jobId, string token) => new()
    {
        OperationId = jobId,
        Status = ExecutionJobStatus.Succeeded,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.AwsBatch,
            Backend = "aws-batch",
            WorkloadName = "customcode",
            RuntimeProfile = CustomCodeJobContract.RuntimeProfile,
            Parameters = new Dictionary<string, string>
            {
                [CustomCodeJobContract.JobTokenEnvParam] = token
            }
        }
    };

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
