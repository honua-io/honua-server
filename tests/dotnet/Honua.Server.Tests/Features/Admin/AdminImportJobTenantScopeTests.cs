// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Import.FileImport;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Import;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

[Trait("Tier", "Fast")]
public sealed class AdminImportJobTenantScopeTests
{
    [Theory]
    [InlineData("tenant-a", "admin", 200)]
    [InlineData("tenant-b", "admin", 404)]
    [InlineData("tenant-b", "platform_admin", 200)]
    public async Task HandleGetImportJobStatus_CallerTenantAndRole_ChecksRecordedTenant(string callerTenant, string role, int expected)
    {
        var (scope, _) = ImportScope(callerTenant);

        var context = await InvokeAsync("HandleGetImportJobStatus", scope.Context(role), HttpMethods.Get);

        context.Response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task HandleCancelImportJob_OtherTenant_ReturnsNotFoundWithoutCancelling()
    {
        var (scope, imports) = ImportScope("tenant-b");

        var context = await InvokeAsync("HandleCancelImportJob", scope.Context("admin"), HttpMethods.Post);

        context.Response.StatusCode.Should().Be(404);
        await imports.DidNotReceiveWithAnyArgs().CancelJobAsync(default!, default);
    }

    [Fact]
    public async Task HandleGetActiveJobs_MixedTenants_ListOnlyCallerTenantJobs()
    {
        var (scope, _) = ImportScope("tenant-b");

        var context = await InvokeAsync("HandleGetActiveJobs", scope.Context("admin"), HttpMethods.Get);

        context.Response.StatusCode.Should().Be(200);
        ReadBody(context).Should().Contain("import-b").And.NotContain("import-a");
    }

    [Fact]
    public void BuildJobRecord_TenantBoundRequest_RecordsTheTenantOnTheDurableJob()
    {
        var request = new ImportRequest { FileName = "parcels.geojson", TableName = "parcels", CloudFileId = "cloud-a", TenantId = "tenant-a" };

        var job = DurableImportJobService.BuildJobRecord(
            "import-a", request, 10, new BackgroundImportRunnerDecision(BackgroundImportRunnerMode.DurableLocal), DateTimeOffset.UtcNow);

        OperationTenantAuthorization.OwnerTenantOf(job).Should().Be("tenant-a");
    }

    private static (TenantScopeFixture Scope, IImportJobService Imports) ImportScope(string callerTenant)
    {
        var importA = Progress("import-a", "tenant-a");
        var imports = Substitute.For<IImportJobService>();
        imports.GetProgressAsync("import-a", Arg.Any<CancellationToken>()).Returns(importA);
        imports.GetActiveJobsAsync(Arg.Any<CancellationToken>()).Returns([importA, Progress("import-b", "tenant-b")]);
        imports.CancelJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        var scope = new TenantScopeFixture(callerTenant)
        {
            Configure = services => services.AddSingleton(imports).AddSingleton(authorization)
        };
        return (scope, imports);
    }

    private static ImportProgress Progress(string jobId, string tenant)
        => ImportProgress.CreateInitial(jobId, "parcels", SupportedFileFormat.GeoJson) with
        {
            Status = ImportStatus.Processing,
            TenantId = tenant
        };

    private static async Task<HttpContext> InvokeAsync(string handler, DefaultHttpContext context, string method)
    {
        context.Request.Method = method;
        context.Request.RouteValues = new RouteValueDictionary { ["jobId"] = "import-a" };
        context.Response.Body = new MemoryStream();
        var endpoint = typeof(ImportEndpoints).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Static)!;
        await (Task)endpoint.Invoke(null, [context])!;
        return context;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return reader.ReadToEnd();
    }
}
