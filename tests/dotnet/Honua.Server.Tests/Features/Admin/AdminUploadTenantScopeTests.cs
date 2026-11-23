// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Import.FileImport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

[Trait("Tier", "Fast")]
public sealed class AdminUploadTenantScopeTests
{
    [Theory]
    [InlineData("tenant-a", "admin", 200)]
    [InlineData("tenant-b", "admin", 404)]
    [InlineData("", "admin", 404)]
    [InlineData("tenant-b", "platform_admin", 200)]
    public async Task UploadEndpoints_AcceptedUpload_UseSubmittingTenant(string caller, string role, int expected)
    {
        var (stored, store, storage) = AcceptedUploadStorage();
        TenantScopeFixture Scope(string tenant) => new(tenant)
        {
            Configure = services => services.AddSingleton(store).AddSingleton(storage)
        };
        var writer = Scope("tenant-a").Context("admin");
        var request = new ImportExecutionRequest
        {
            TableName = "parcels",
            File = new StagedImportFile { FileName = "parcels.geojson", LocalFilePath = "unused", SizeBytes = 10 }
        };
        await InvokeAsync("ReportAcceptedUploadAsync", writer, "upload-a", request, CancellationToken.None);
        ((ITenantOwnedProgress)stored["upload-a"]).TenantId.Should().Be("tenant-a");

        var scope = Scope(caller);
        var status = Context(scope, role, HttpMethods.Get);
        await InvokeAsync("HandleGetUploadProgress", status);
        status.Response.StatusCode.Should().Be(expected);

        var list = Context(scope, role, HttpMethods.Get);
        await InvokeAsync("HandleGetActiveUploads", list);
        list.Response.Body.Position = 0;
        using var reader = new StreamReader(list.Response.Body);
        (await reader.ReadToEndAsync()).Contains("upload-a", StringComparison.Ordinal).Should().Be(expected == 200);

        var cancel = Context(scope, role, HttpMethods.Post);
        await InvokeAsync("HandleCancelUpload", cancel);
        cancel.Response.StatusCode.Should().Be(expected);
        if (expected == 404)
        {
            await storage.DidNotReceiveWithAnyArgs().CancelUploadAsync(default!, default);
        }
        else
        {
            await storage.Received(1).CancelUploadAsync("upload-a", Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData("completed", 2L)]
    [InlineData("cancelled", 2L)]
    [InlineData("completed", 11534336L)]
    [InlineData("cancelled", 11534336L)]
    public async Task ImportExecution_AllocatesIndependentUploadAndPreservesLiveOwner(string outcome, long stagedSize)
    {
        await using var live = new LiveUploadFixture();
        await live.Started;
        var scope = await ExecuteImportAsync(live, outcome, stagedSize);
        live.Stored["upload-a"].TenantId.Should().Be("tenant-a");
        live.Stored.Values.Should().Contain(progress => progress.TenantId == "tenant-b" && progress.UploadId != "upload-a");
        var cancel = Context(scope, "admin", HttpMethods.Post);
        await InvokeAsync("HandleCancelUpload", cancel);
        cancel.Response.StatusCode.Should().Be(404);
        (await live.Storage.CancelUploadAsync("upload-a")).Should().BeTrue();
    }

    [Theory]
    [InlineData(TenantContextSource.Default, 200)]
    [InlineData(TenantContextSource.Claim, 404)]
    public async Task UploadStatus_ProviderOnlyProgress_UsesDeploymentPolicy(TenantContextSource source, int expected)
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetUploadProgressAsync("upload-a", Arg.Any<CancellationToken>())
            .Returns(UploadProgress.CreateInitial("upload-a", "parcels.geojson", 10));
        var scope = new TenantScopeFixture("public", source)
        {
            Configure = services => services.AddSingleton(storage)
        };
        var context = Context(scope, "admin", HttpMethods.Get);

        await InvokeAsync("HandleGetUploadProgress", context);

        context.Response.StatusCode.Should().Be(expected);
    }

    private static async Task<TenantScopeFixture> ExecuteImportAsync(LiveUploadFixture live, string outcome, long stagedSize)
    {
        using var cancellation = new CancellationTokenSource();
        var import = ImportService(outcome, cancellation);
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        var scope = new TenantScopeFixture("tenant-b")
        {
            Configure = services => services.AddSingleton(live.Store).AddSingleton<ICloudFileStorage>(live.Storage)
                .AddSingleton(import).AddSingleton(authorization)
        };
        var stagedPath = Path.Join(live.DirectoryPath, "staged.geojson");
        await File.WriteAllTextAsync(stagedPath, "{}");
        var context = scope.Context("admin");
        context.Response.Body = new MemoryStream();
        var execution = InvokeAsync("ExecuteStagedImportAsync", context, new ImportExecutionRequest
        {
            UploadId = "upload-a",
            TableName = "parcels",
            File = new StagedImportFile { FileName = "parcels.geojson", LocalFilePath = stagedPath, SizeBytes = stagedSize }
        }, cancellation.Token);
        if (outcome == "cancelled")
        {
            await FluentActions.Awaiting(() => execution).Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await execution;
        }

        return scope;
    }

    private static IFileImportService ImportService(string outcome, CancellationTokenSource cancellation)
    {
        var import = Substitute.For<IFileImportService>();
        import.Limits.Returns(ImportLimits.Default);
        import.ImportFileAsync(Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (outcome == "cancelled")
                {
                    cancellation.Cancel();
                    return Task.FromCanceled<ImportResult>(cancellation.Token);
                }

                return Task.FromResult(new ImportResult { Success = false, TableName = "parcels", Format = SupportedFileFormat.GeoJson });
            });
        return import;
    }

    private static (Dictionary<string, UploadProgress>, IUploadProgressStore, ICloudFileStorage) AcceptedUploadStorage()
    {
        var stored = new Dictionary<string, UploadProgress>();
        var store = Substitute.For<IUploadProgressStore>();
        store.SetProgressAsync(Arg.Any<string>(), Arg.Any<UploadProgress>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { stored[call.ArgAt<string>(0)] = call.ArgAt<UploadProgress>(1); return Task.CompletedTask; });
        store.GetProgressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(stored.GetValueOrDefault(call.ArgAt<string>(0))));
        store.GetActiveUploadsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<UploadProgress>>(stored.Values.ToArray()));
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetActiveUploadsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<UploadProgress>>(stored.Values.ToArray()));
        storage.CancelUploadAsync("upload-a", Arg.Any<CancellationToken>()).Returns(true);
        return (stored, store, storage);
    }

    private static DefaultHttpContext Context(TenantScopeFixture scope, string role, string method)
    {
        var context = scope.Context(role);
        context.Request.Method = method;
        context.Request.RouteValues = new RouteValueDictionary { ["uploadId"] = "upload-a" };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static Task InvokeAsync(string handler, params object[] arguments)
        => (Task)typeof(ImportEndpoints).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, arguments)!;
}
