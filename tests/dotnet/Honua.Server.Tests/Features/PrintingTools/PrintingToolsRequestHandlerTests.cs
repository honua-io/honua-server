// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Reflection;
using System.Threading.Channels;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.Options;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;
using Honua.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using NSubstitute;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.PrintingTools;
using Honua.Server.Features.PrintingTools.Layout;
using Honua.Server.Features.PrintingTools.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.PrintingTools;

/// <summary>
/// Unit tests for <see cref="PrintingToolsRequestHandlers"/> helpers not covered by
/// <see cref="LayoutTemplateRegistryTests"/> (edition gating, warnings, format/DPI resolution,
/// and output-format constants).
/// </summary>
[Trait("Component", "PrintingTools")]
public class PrintingToolsRequestHandlerTests
{
    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [Protocol(TestProtocols.PrintingTools)]
    public async Task Execute_PublicationVisibilityAndQueryGrants_ControlLayerReads(
        bool tenantVisible, bool queryGrant, bool coarseAllowed, bool allowed)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service", Name = "service" },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active }
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource", Name = "resource" },
            AccessPolicy = new AccessPolicy { AllowedRoles = [coarseAllowed ? "reader" : "editor"] },
            StorageBindingIds = ["binding"],
            PrimaryStorageBindingId = "binding",
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active }
        };
        var snapshot = new MetadataV2GraphSnapshot(new MetadataV2Graph
        {
            Revision = 1,
            Services = [service],
            Resources = [resource],
            StorageBindings = [new MetadataV2StorageBinding
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "binding", Name = "binding" },
                ResourceId = "resource", StorageLayerId = 7,
                Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active }
            }],
            Publications = [new MetadataV2Publication
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "publication", Name = "publication", Tenant = tenantVisible ? "tenant-a" : "tenant-b" },
                ServiceId = "service", ResourceId = "resource", StorageBindingId = "binding", LayerIndex = 1,
                Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active }
            }]
        }, "print-test", DateTimeOffset.UnixEpoch);
        var validator = Substitute.For<IResourceValidator>();
        validator.ValidateServiceV2Async("service", Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(service));
        var graph = Substitute.For<IMetadataV2GraphProvider>();
        graph.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        var styles = Substitute.For<ILayerStyleCatalog>();
        var reader = Substitute.For<IFeatureReader>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", "tenant-a"), new Claim(ClaimTypes.NameIdentifier, "user-1"), new Claim(ClaimTypes.Role, "reader")], "test"));
        var map = new WebMapDefinition
        {
            OperationalLayers = [new WebMapOperationalLayer { Url = "/rest/services/service/MapServer/1" }]
        };

        var permissions = Substitute.For<IPermissionResolver>();
        permissions.AuthorizeAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), "service", "resource",
            AuthorizationOperation.Query, true, Arg.Any<CancellationToken>())
            .Returns(queryGrant ? PermissionDecision.Allow(new PermissionGrant
            { Service = "service", Layer = "resource", Operation = "query" }) : PermissionDecision.NoMatch());
        await using var authorizationServices = new ServiceCollection()
            .AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions()))
            .AddSingleton(permissions)
            .AddSingleton<IAccessPolicyEvaluator>(new AccessPolicyEvaluator()).BuildServiceProvider();

        await PrintingToolsRequestHandlers.ExecuteAsync(map, "PNG32", "MAP_ONLY", 96,
            validator, graph, reader, styles, NullLogger.Instance, default,
            callerPrincipal: principal,
            authorizationServices: authorizationServices, tenantId: "tenant-a");

        await styles.Received(allowed ? 1 : 0).GetLayerStyleAsync(7, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public async Task ProcessPrintJob_SubmitterPrincipal_ReachesFeatureSecurityScope()
    {
        var progress = Substitute.For<IUniversalProgressStore>();
        var graph = Substitute.For<IMetadataV2GraphProvider>();
        JobSecurityScopeState? observed = null;
        graph.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            observed = JobSecurityScope.Current;
            return new MetadataV2GraphSnapshot(new MetadataV2Graph(), "print-test", DateTimeOffset.UnixEpoch);
        });
        await using var services = new ServiceCollection()
            .AddSingleton(progress).AddSingleton(graph)
            .AddSingleton(Substitute.For<IResourceValidator>())
            .AddSingleton(Substitute.For<IFeatureReader>())
            .AddSingleton(Substitute.For<ILayerStyleCatalog>())
            .AddSingleton<IAccessPolicyEvaluator>(new AccessPolicyEvaluator())
            .AddSingleton(Substitute.For<ITemporaryFileService>())
            .BuildServiceProvider();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user-1"), new Claim("tenant_id", "tenant-a")], "test"));
        var job = new PrintJob("print-1", new WebMapDefinition(), "PNG32", "MAP_ONLY", 96, 1, JobSecurityContextCapture.Capture(principal, new RbacOptions()));
        using var worker = new PrintingToolsBackgroundService(Channel.CreateUnbounded<PrintJob>(),
            services.GetRequiredService<IServiceScopeFactory>(), new PrintJobCancellationTokens(),
            NullLogger<PrintingToolsBackgroundService>.Instance);
        var process = typeof(PrintingToolsBackgroundService).GetMethod("ProcessJobCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await (Task)process.Invoke(worker, [job, CancellationToken.None])!;

        observed.Should().NotBeNull();
        observed!.Submitter.Should().NotBeNull();
        observed.Submitter!.TenantId.Should().Be("tenant-a");
        JobSecurityScope.Current.Should().BeNull();
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public async Task JobStatus_DifferentSubmitter_ReturnsNotFound()
    {
        var store = Substitute.For<IUniversalProgressStore>();
        IOperationProgress? persisted = null;
        store.SetProgressAsync(Arg.Any<string>(), Arg.Any<IOperationProgress>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { persisted = call.Arg<IOperationProgress>(); return Task.CompletedTask; });
        store.GetProgressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => persisted);
        await using var services = new ServiceCollection()
            .AddSingleton(store).AddSingleton(Channel.CreateUnbounded<PrintJob>())
            .AddLogging().BuildServiceProvider();
        var submitter = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-1")], "test"));
        var submitContext = new DefaultHttpContext { RequestServices = services, User = submitter };
        var submit = typeof(PrintingToolsEndpoints).GetMethod("SubmitJobInternalAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        await (Task<IResult>)submit.Invoke(null,
            [submitContext, new WebMapDefinition(), "PNG32", "MAP_ONLY", 96, NullLogger.Instance, CancellationToken.None])!;
        persisted.Should().BeOfType<PrintProgress>();
        var statusContext = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-2")], "test"))
        };
        statusContext.Request.RouteValues["jobId"] = ((PrintProgress)persisted!).JobId;
        var status = typeof(PrintingToolsEndpoints).GetMethod("HandleJobStatus", BindingFlags.Static | BindingFlags.NonPublic)!;

        var response = await (Task<IResult>)status.Invoke(null, [statusContext, CancellationToken.None])!;

        response.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(404);
    }

    [Theory]
    [InlineData("user-1", "tenant-a", false, false, true)]
    [InlineData("user-2", "tenant-a", false, false, false)]
    [InlineData("user-2", "tenant-a", true, false, true)]
    [InlineData("user-1", "tenant-b", false, false, false)]
    [InlineData("user-2", "tenant-b", true, false, false)]
    [InlineData(null, "tenant-a", true, false, false)]
    [InlineData(null, "tenant-a", false, true, true)]
    [InlineData(null, "tenant-b", false, true, false)]
    [Protocol(TestProtocols.PrintingTools)]
    public async Task JobPolling_RespectsTenantAndSubmitter(
        string? callerId, string callerTenant, bool admin, bool anonymousSubmission, bool allowed)
    {
        var store = Substitute.For<IUniversalProgressStore>();
        IOperationProgress? persisted = null;
        store.SetProgressAsync(Arg.Any<string>(), Arg.Any<IOperationProgress>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { persisted = call.Arg<IOperationProgress>(); return Task.CompletedTask; });
        store.GetProgressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => persisted);
        await using var services = new ServiceCollection()
            .AddSingleton(store).AddSingleton(Channel.CreateUnbounded<PrintJob>()).AddLogging().BuildServiceProvider();
        var submitClaims = new List<Claim> { new("tenant_id", "tenant-a") };
        if (!anonymousSubmission) submitClaims.Add(new Claim(ClaimTypes.NameIdentifier, "user-1"));
        var submitContext = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(submitClaims, anonymousSubmission ? null : "test"))
        };
        var submit = typeof(PrintingToolsEndpoints).GetMethod("SubmitJobInternalAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        await (Task<IResult>)submit.Invoke(null,
            [submitContext, new WebMapDefinition(), "PNG32", "MAP_ONLY", 96, NullLogger.Instance, CancellationToken.None])!;
        var callerClaims = new List<Claim> { new("tenant_id", callerTenant) };
        if (callerId is not null) callerClaims.Add(new Claim(ClaimTypes.NameIdentifier, callerId));
        if (admin) callerClaims.Add(new Claim(ClaimTypes.Role, "admin"));
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(callerClaims, callerId is null ? null : "test"))
        };
        context.Request.RouteValues["jobId"] = ((PrintProgress)persisted!).JobId;
        foreach (var method in new[] { "HandleJobStatus", "HandleJobResult" })
        {
            var handler = typeof(PrintingToolsEndpoints).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!;
            var response = await (Task<IResult>)handler.Invoke(null, [context, CancellationToken.None])!;
            var statusCode = response.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode ?? 200;
            // A queued result is not available yet, while authorized status polling succeeds.
            statusCode.Should().Be(allowed ? method == "HandleJobStatus" ? 200 : 400 : 404);
        }
    }

    // --- ResolveFormat ---

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void ResolveFormat_Null_DefaultsToPng32()
    {
        PrintingToolsRequestHandlers.ResolveFormat(null).Should().Be("PNG32");
        PrintingToolsRequestHandlers.ResolveFormat("").Should().Be("PNG32");
        PrintingToolsRequestHandlers.ResolveFormat("  ").Should().Be("PNG32");
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void ResolveFormat_ValidFormat_PreservedTrimmed()
    {
        PrintingToolsRequestHandlers.ResolveFormat("PDF").Should().Be("PDF");
        PrintingToolsRequestHandlers.ResolveFormat(" JPG ").Should().Be("JPG");
    }

    // --- ResolveDpi ---

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void ResolveDpi_Default_Returns96()
    {
        var webMap = new WebMapDefinition();

        PrintingToolsRequestHandlers.ResolveDpi(webMap).Should().Be(96);
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void ResolveDpi_Null_Returns96()
    {
        PrintingToolsRequestHandlers.ResolveDpi(null).Should().Be(96);
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void ResolveDpi_ClampsToRange()
    {
        var low = new WebMapDefinition { ExportOptions = new WebMapExportOptions { Dpi = 10 } };
        PrintingToolsRequestHandlers.ResolveDpi(low).Should().Be(72);

        var high = new WebMapDefinition { ExportOptions = new WebMapExportOptions { Dpi = 9999 } };
        PrintingToolsRequestHandlers.ResolveDpi(high).Should().Be(600);
    }

    // --- ValidateEdition ---

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void ValidateEdition_CommunityMapOnlyPng_Allowed()
    {
        LayoutTemplateRegistry.TryGetTemplate("MAP_ONLY", out var template);

        PrintingToolsRequestHandlers.ValidateEdition(template, "PNG32", HonuaEdition.Community, NullLogger.Instance).Should().BeNull();
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void ValidateEdition_CommunityPdf_Blocked()
    {
        LayoutTemplateRegistry.TryGetTemplate("MAP_ONLY", out var template);

        PrintingToolsRequestHandlers.ValidateEdition(template, "PDF", HonuaEdition.Community, NullLogger.Instance).Should().Contain("Pro");
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void ValidateEdition_CommunityLayoutTemplate_Blocked()
    {
        LayoutTemplateRegistry.TryGetTemplate("Letter ANSI A Portrait", out var template);

        PrintingToolsRequestHandlers.ValidateEdition(template, "PNG32", HonuaEdition.Community, NullLogger.Instance).Should().Contain("Pro");
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void ValidateEdition_ProEdition_AllAllowed()
    {
        LayoutTemplateRegistry.TryGetTemplate("Letter ANSI A Portrait", out var template);

        PrintingToolsRequestHandlers.ValidateEdition(template, "PDF", HonuaEdition.Pro, NullLogger.Instance).Should().BeNull();
    }

    // --- CollectWarnings ---

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void CollectWarnings_WithBaseMap_EmitsWarning()
    {
        var json = """{"baseMap":{"title":"Topographic"},"mapOptions":{"extent":{"xmin":0,"ymin":0,"xmax":1,"ymax":1}}}""";
        var webMap = PrintingToolsRequestHandlers.ParseWebMapJson(json)!;
        var logger = NullLogger.Instance;

        var warnings = PrintingToolsRequestHandlers.CollectWarnings(webMap, logger);

        warnings.Should().ContainSingle();
        warnings[0].Type.Should().Be("esriJobMessageTypeWarning");
        warnings[0].Description.Should().Contain("baseMap");
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void CollectWarnings_WithoutBaseMap_NoWarnings()
    {
        var json = """{"mapOptions":{"extent":{"xmin":0,"ymin":0,"xmax":1,"ymax":1}}}""";
        var webMap = PrintingToolsRequestHandlers.ParseWebMapJson(json)!;
        var logger = NullLogger.Instance;

        var warnings = PrintingToolsRequestHandlers.CollectWarnings(webMap, logger);

        warnings.Should().BeEmpty();
    }

    // --- PrintOutputFormat ---

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void PrintOutputFormat_IsSupported_KnownFormats()
    {
        PrintOutputFormat.IsSupported("PDF").Should().BeTrue();
        PrintOutputFormat.IsSupported("PNG32").Should().BeTrue();
        PrintOutputFormat.IsSupported("JPG").Should().BeTrue();
        PrintOutputFormat.IsSupported("PNG8").Should().BeFalse();
        PrintOutputFormat.IsSupported("pdf").Should().BeTrue();
        PrintOutputFormat.IsSupported("TIFF").Should().BeFalse();
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void PrintOutputFormat_GetContentType_ReturnsCorrectMimeTypes()
    {
        PrintOutputFormat.GetContentType("PDF").Should().Be("application/pdf");
        PrintOutputFormat.GetContentType("PNG32").Should().Be("image/png");
        PrintOutputFormat.GetContentType("JPG").Should().Be("image/jpeg");
    }

    [UnitTest]
    [Protocol(TestProtocols.PrintingTools)]
    public void PrintOutputFormat_GetExtension_ReturnsCorrectExtensions()
    {
        PrintOutputFormat.GetExtension("PDF").Should().Be(".pdf");
        PrintOutputFormat.GetExtension("PNG32").Should().Be(".png");
        PrintOutputFormat.GetExtension("JPG").Should().Be(".jpg");
    }
}
