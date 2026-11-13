// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Geoprocessing;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.MultiTenancy;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed partial class GeoprocessingJobServiceTests
{
    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    public async Task GetJob_LegacyTenantlessJob_SingleTenantUpgradePreservesOwnership()
    {
        var tenant = new RequestTenantContext();
        await using var services = new ServiceCollection().AddSingleton<ITenantContext>(tenant).BuildServiceProvider();
        var principal = CreatePrincipal();
        var sut = CreateTenantJobService(services, principal);
        var job = CreateTenantJobRecord("legacy-upgrade", null);
        _jobStore.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(job);
        (await sut.GetJobAsync(job.OperationId, principal)).Should().BeSameAs(job);

        _jobStore.QueryAsync(Arg.Any<ExecutionJobQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ExecutionJobPage { Items = [job] });
        var context = new DefaultHttpContext { RequestServices = services, User = principal };
        await new TenantContextMiddleware(_ => Task.CompletedTask, Options.Create(new TenantContextOptions()),
            NullLogger<TenantContextMiddleware>.Instance).InvokeAsync(context);
        (await sut.GetJobAsync(job.OperationId, principal)).Should().BeSameAs(job);
        (await sut.GetJobForTerminalAsync(job.OperationId, principal)).Should().BeSameAs(job);
        var page = await sut.ListJobsAsync(new GeoprocessingJobListFilter(), principal);
        page.Items.Should().ContainSingle().Which.Should().BeSameAs(job);
        await _jobStore.Received(1).QueryAsync(
            Arg.Is<ExecutionJobQuery>(query => query.ApplyTenantScope && query.TenantId == "public"
                && query.IncludeUnassignedTenant && query.RequestedBy != null),
            Arg.Any<CancellationToken>());
        var stranger = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "stranger")], "Test"));
        var denied = () => sut.GetJobAsync(job.OperationId, stranger);
        await denied.Should().ThrowAsync<GeoprocessingNotFoundException>();

        tenant.Set("public", TenantContextSource.Claim);
        denied = () => sut.GetJobAsync(job.OperationId, principal);
        await denied.Should().ThrowAsync<GeoprocessingNotFoundException>();
        job.Audit.SubmitterSecurityContext!.TenantId.Should().BeNull();
    }
}
