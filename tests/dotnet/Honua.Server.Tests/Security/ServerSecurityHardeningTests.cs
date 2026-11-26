// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.DataEnrichment.Domain;
using Honua.Core.Features.EnrichmentCatalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Scene.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Monitoring;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Rendering;
using Honua.Server.Features.Admin;
using Honua.Server.Features.DataEnrichment;
using Honua.Server.Features.PrintingTools;
using Honua.Server.Features.PrintingTools.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Security;

[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class ServerSecurityHardeningTests
{
    [UnitTest]
    public async Task EnrichmentDatasetAdminEndpoints_TenantBoundAdmin_ReturnsForbidden()
    {
        await using var app = await StartAdminAppAsync(
            tenantId: "tenant-a",
            isPlatformAdmin: false,
            configureServices: services =>
            {
                var store = Substitute.For<IEnrichmentDatasetCatalogStore>();
                services.AddSingleton(store);
                services.AddSingleton(new EnrichmentDatasetCatalogService(
                    store,
                    new EnrichmentCatalog(Options.Create(new EnrichmentCatalogOptions())),
                    cache: null,
                    schemaContext: null));
            },
            mapEndpoints: endpoints => endpoints.MapEnrichmentDatasetAdminEndpoints());

        using var client = app.GetTestClient();
        using var body = new StringContent("""{"id":"ds-1","title":"Test","category":"boundary","layerId":1}""", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/enrich/datasets/", body);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain(PlatformDeployAuthority.DenialCode);
    }

    [UnitTest]
    public async Task SceneDatasetEndpoints_TenantBoundAdmin_ReturnsForbidden()
    {
        var scenes = Substitute.For<ISceneRegistrationService>();
        await using var app = await StartAdminAppAsync(
            tenantId: "tenant-a",
            isPlatformAdmin: false,
            configureServices: services => services.AddSingleton(scenes),
            mapEndpoints: endpoints => endpoints.MapSceneDatasetEndpoints());

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/v1/admin/scenes/");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await scenes.DidNotReceive().ListAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ReplicaManagementEndpoints_CrossTenantService_ReturnsNotFound()
    {
        var otherTenantService = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-b", Name = "svc-b", Tenant = "tenant-b" },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };
        var validator = Substitute.For<IResourceValidator>();
        validator.ValidateServiceV2Async("svc-b", Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(otherTenantService));
        var replicas = Substitute.For<IReplicaRepository>();

        await using var app = await StartAdminAppAsync(
            tenantId: "tenant-a",
            isPlatformAdmin: false,
            configureServices: services =>
            {
                services.AddSingleton(validator);
                services.AddSingleton(replicas);
                services.AddSingleton(Substitute.For<IReplicaConflictRepository>());
            },
            mapEndpoints: endpoints => endpoints.MapReplicaManagementEndpoints());

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/v1/admin/services/svc-b/replicas/");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await replicas.DidNotReceive().ListByServiceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ReplicaManagementEndpoints_SameTenantService_ReturnsOk()
    {
        var sameTenantService = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-a", Name = "svc-a", Tenant = "tenant-a" },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };
        var validator = Substitute.For<IResourceValidator>();
        validator.ValidateServiceV2Async("svc-a", Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(sameTenantService));
        var replicas = Substitute.For<IReplicaRepository>();
        replicas.ListByServiceAsync("svc-a", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ReplicaRecord>());

        await using var app = await StartAdminAppAsync(
            tenantId: "tenant-a",
            isPlatformAdmin: false,
            configureServices: services =>
            {
                services.AddSingleton(validator);
                services.AddSingleton(replicas);
                services.AddSingleton(Substitute.For<IReplicaConflictRepository>());
            },
            mapEndpoints: endpoints => endpoints.MapReplicaManagementEndpoints());

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/v1/admin/services/svc-a/replicas/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await replicas.Received(1).ListByServiceAsync("svc-a", Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public void CreateSurface_ZeroDimensions_ThrowsInvalidOperationException()
    {
        Action act = () => PrintingToolsRequestHandlers.CreateSurface(0, 0);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*0x0*");
    }

    [UnitTest]
    public async Task ExecuteAsync_A3At600Dpi_ThrowsPrintRenderCapacityExceededException()
    {
        using var limiter = new RasterRenderCapacityLimiter();
        var services = new ServiceCollection()
            .AddSingleton(limiter)
            .BuildServiceProvider();
        var webMap = new WebMapDefinition
        {
            MapOptions = new WebMapExtent
            {
                Extent = new WebMapBbox { Xmin = -10, Ymin = -10, Xmax = 10, Ymax = 10 },
            },
            ExportOptions = new WebMapExportOptions { Dpi = 600 },
        };

        var act = () => PrintingToolsRequestHandlers.ExecuteAsync(
            webMap,
            PrintOutputFormat.Png32,
            "A3 Landscape",
            600,
            Substitute.For<IResourceValidator>(),
            new TestMetadataV2GraphProvider(new MetadataV2Graph { Revision = 1 }),
            Substitute.For<IFeatureReader>(),
            Substitute.For<ILayerStyleCatalog>(),
            NullLogger.Instance,
            CancellationToken.None,
            callerPrincipal: new ClaimsPrincipal(new ClaimsIdentity("Test")),
            authorizationServices: services);

        await act.Should().ThrowAsync<PrintRenderCapacityExceededException>();
    }

    [UnitTest]
    public void AddObservability_BackgroundOtlpProbeHttpClient_DisablesAutoRedirect()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions<Honua.ServiceDefaults.TracingOptions>();
        services.AddObservability(configuration);

        using var provider = services.BuildServiceProvider();
        var factoryOptions = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(BackgroundOtlpProbe.HttpClientName);
        var builder = new StubHttpMessageHandlerBuilder(provider);

        foreach (var action in factoryOptions.HttpMessageHandlerBuilderActions)
        {
            action(builder);
        }

        builder.PrimaryHandler.Should().BeOfType<HttpClientHandler>()
            .Which.AllowAutoRedirect.Should().BeFalse();
    }

    private static async Task<WebApplication> StartAdminAppAsync(
        string tenantId,
        bool isPlatformAdmin,
        Action<IServiceCollection> configureServices,
        Action<IEndpointRouteBuilder> mapEndpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        ConfigureAdminTestServices(builder.Services, tenantId, isPlatformAdmin);
        configureServices(builder.Services);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        mapEndpoints(app);
        await app.StartAsync();
        return app;
    }

    private static void ConfigureAdminTestServices(
        IServiceCollection services,
        string tenantId,
        bool isPlatformAdmin)
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(tenantId);
        tenant.Source.Returns(TenantContextSource.Claim);
        var license = Substitute.For<ILicenseEntitlementService>();
        license.GetSnapshot().Returns(new LicenseSnapshot(
            HonuaEdition.Enterprise,
            true,
            LicenseValidationState.Valid,
            null,
            null,
            null,
            null,
            [],
            new HashSet<string>(FeatureCatalog.All.Select(feature => feature.Key), StringComparer.OrdinalIgnoreCase),
            1,
            null));

        license.CheckEntitlement(Arg.Any<string>()).Returns(call => new LicenseEntitlementDecision(
            call.Arg<string>(),
            IsActive: true,
            HonuaEdition.Enterprise,
            LicenseValidationState.Valid,
            RequiredEdition: null,
            UpgradeMessage: string.Empty));

        services.AddApiVersioning();
        services.AddSingleton(tenant);
        services.AddSingleton(license);
        services.Configure<TenantContextOptions>(options => options.Enabled = true);
        services.AddAuthentication(AuthenticationExtensions.ApiKeyScheme)
            .AddScheme<TestAdminAuthOptions, TestAdminAuthHandler>(
                AuthenticationExtensions.ApiKeyScheme,
                options =>
                {
                    options.TenantId = tenantId;
                    options.IsPlatformAdmin = isPlatformAdmin;
                });
        services.AddAuthorization(options =>
            options.AddPolicy(AuthenticationExtensions.AdminPolicy, policy => policy.RequireAuthenticatedUser()));
    }

    private sealed class StubHttpMessageHandlerBuilder(IServiceProvider services) : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }
        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();
        public override IList<DelegatingHandler> AdditionalHandlers { get; } = [];
        public override IServiceProvider Services { get; } = services;
        public override HttpMessageHandler Build() => PrimaryHandler;
    }

    private sealed class TestAdminAuthOptions : AuthenticationSchemeOptions
    {
        public string TenantId { get; set; } = string.Empty;
        public bool IsPlatformAdmin { get; set; }
    }

    private sealed class TestAdminAuthHandler(
        IOptionsMonitor<TestAdminAuthOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<TestAdminAuthOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "admin-user"),
                new(ClaimTypes.Role, "admin"),
                new("tenant_id", Options.TenantId),
            };
            if (Options.IsPlatformAdmin)
            {
                claims.Add(new Claim(ClaimTypes.Role, "platform-admin"));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
