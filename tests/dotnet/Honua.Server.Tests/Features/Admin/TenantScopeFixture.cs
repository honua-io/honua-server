// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Progress;
using Honua.Migration;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

// A default-sourced tenant stands for a single-tenant deployment; any other source means tenancy is enabled.
internal sealed class TenantScopeFixture(
    string callerTenant,
    TenantContextSource source = TenantContextSource.Claim)
{
    public UniversalProgressStore Progress { get; } = new();

    public static OperationAuditInfo AuditTenant(string tenant) => new() { TenantId = tenant };

    public static int StatusOf(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode ?? 200;

    public InMemoryExecutionJobStore Jobs { get; } = new();

    public ITileCacheJobService? TileJobs { get; set; }

    public Action<IServiceCollection>? Configure { get; set; }

    public async Task AddUploadAsync(string operationId, OperationAuditInfo audit)
    {
        var progress = UploadProgress.CreateInitial(operationId, "data.geojson", 10) with { Status = OperationStatus.Processing };
        await Progress.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        await Jobs.TryCreateAsync(Job(operationId, audit));
    }

    public static ExecutionJobRecord Job(string operationId, OperationAuditInfo audit, ExecutionJobKind kind = ExecutionJobKind.FileImport)
        => new()
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Audit = audit,
            Spec = new ExecutionJobSpec
            {
                Kind = kind,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "remote-test",
                WorkloadName = "tenant-scope"
            }
        };

    public Task<IResult> GetStatusAsync(string operationId, string role)
        => Invoke("HandleGetOperationStatus", operationId, Context(role));

    public Task<IResult> CancelAsync(string operationId, string role)
        => Invoke("HandleCancelOperation", operationId, Context(role));

    public DefaultHttpContext Context(string role) => new()
    {
        RequestServices = Services(),
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "operator"), new Claim(ClaimTypes.Role, role)], "Test")),
    };

    private ServiceProvider Services()
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(callerTenant);
        tenant.Source.Returns(source);
        var options = new TenantContextOptions { Enabled = source != TenantContextSource.Default, DefaultTenantId = "public" };
        var services = new ServiceCollection().AddLogging();
        if (TileJobs is not null)
        {
            services.AddSingleton(TileJobs);
        }

        Configure?.Invoke(services);

        return services
            .AddSingleton(tenant)
            .AddSingleton<IUniversalProgressStore>(Progress)
            .AddSingleton<IExecutionJobStore>(Jobs)
            .AddSingleton(Options.Create(options))
            .BuildServiceProvider();
    }

    private async Task<IResult> Invoke(string handler, string operationId, HttpContext context)
    {
        var method = typeof(OperationsProgressEndpoints).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = method.GetParameters().Select(parameter => parameter.ParameterType switch
        {
            var type when type == typeof(string) => operationId,
            var type when type == typeof(HttpContext) => context,
            var type when type == typeof(IUniversalProgressStore) => Progress,
            var type when type == typeof(IEnumerable<IJobCancellationNotifier>) => Array.Empty<IJobCancellationNotifier>(),
            var type when type == typeof(CancellationToken) => CancellationToken.None,
            _ => (object?)null,
        }).ToArray();
        return await (Task<IResult>)method.Invoke(null, args)!;
    }
}
