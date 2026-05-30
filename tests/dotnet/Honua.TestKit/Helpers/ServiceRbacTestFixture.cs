// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Geometry.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Events;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.OData.Models;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;
using OgcGeoJsonFeature = Honua.Protocols.Ogc.Api.Features.Models.GeoJsonFeature;

namespace Honua.TestKit.Helpers;

/// <summary>
/// Shared fixture for service-level RBAC tests across protocols
/// (FeatureServer, OData, OGC API Features). Wires a
/// <see cref="TestWebApplicationFactory"/> with a metadata v2 graph
/// scenario and a test authentication scheme that reads claims from
/// request headers.
/// </summary>
public static class ServiceRbacTestFixture
{
    public const string AlphaService = "alpha";
    public const string BetaService = "beta";
    public const int AlphaLayerId = 0;
    public const int BetaLayerId = 1;

    public static WebApplicationFactory<Program> CreateFactory(
        Func<ITestMetadataV2GraphSource>? layerCatalogFactory = null,
        Action<IServiceCollection>? configureServices = null)
    {
        layerCatalogFactory ??= static () => new RbacTestLayerCatalog();

        return new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");

                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Rbac:RoleClaimType"] = "roles",
                        ["Rbac:DataEditorServicePrefix"] = "data-editor:",
                        ["Limits:Validation:EnableTopologyValidation"] = "false",
                        ["Limits:Validation:EnableAutoRepair"] = "false",
                        ["Limits:Validation:Mode"] = "Accept"
                    });
                });

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IMetadataV2GraphProvider>();
                    services.RemoveAll<IMetadataV2GraphStore>();
                    services.AddSingleton(_ => layerCatalogFactory().BuildProvider());
                    services.AddSingleton<IMetadataV2GraphProvider>(sp =>
                        sp.GetRequiredService<TestMetadataV2GraphProvider>());
                    services.AddSingleton<IMetadataV2GraphStore>(sp =>
                        sp.GetRequiredService<TestMetadataV2GraphProvider>());
                    services.AddSingleton<ICrsRegistry, TestCrsRegistry>();
                    services.AddSingleton<ICoordinateTransformService, TestCoordinateTransformService>();
                    services.AddSingleton<IGeometryTopologyValidator, NoOpGeometryTopologyValidator>();
                    configureServices?.Invoke(services);

                    services.AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

                    services.PostConfigureAll<AuthenticationOptions>(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                        options.DefaultScheme = TestAuthHandler.SchemeName;
                    });
                });
            });
    }

    public static HttpClient CreateClient(WebApplicationFactory<Program> factory, params string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "rbac-user");

        if (roles.Length > 0)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(',', roles));
        }

        return client;
    }

    public static StringContent CreateApplyEditsContent()
    {
        var editsRequest = new ApplyEditsRequest
        {
            Adds = new[]
            {
                new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>
                    {
                        ["name"] = "RBAC Feature"
                    },
                    Geometry = new GeoServicesGeometry
                    {
                        X = -122.4194,
                        Y = 37.7749
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(editsRequest, FeatureServerJsonContext.Default.ApplyEditsRequest);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    public static AccessPolicy CreateServiceMetadata(
        string[]? readRoles = null,
        string[]? writeRoles = null,
        bool allowAnonymous = false,
        bool allowAnonymousWrite = false)
    {
        return new AccessPolicy
        {
            AllowAnonymous = allowAnonymous,
            AllowAnonymousWrite = allowAnonymousWrite,
            AllowedRoles = readRoles,
            AllowedWriteRoles = writeRoles
        };
    }

    public static async Task AssertStatusAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, body);
    }

    public static StringContent CreateOgcFeatureContent()
    {
        var feature = new OgcGeoJsonFeature
        {
            Type = "Feature",
            Geometry = new SimpleGeoJsonGeometry
            {
                Type = "Point",
                CoordinatesJson = "[-122.4194, 37.7749]"
            },
            Properties = new Dictionary<string, object?>
            {
                ["name"] = "RBAC Feature"
            }
        };

        var json = JsonSerializer.Serialize(feature, OgcJsonContext.Default.GeoJsonFeature);
        return new StringContent(json, Encoding.UTF8, MediaTypeHeaderValue.Parse(MediaTypes.GeoJson));
    }

    public static StringContent CreateODataFeatureContent()
    {
        var request = new ODataFeatureRequest
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "RBAC Feature"
            }
        };

        var json = JsonSerializer.Serialize(request, ODataJsonContext.Default.ODataFeatureRequest);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    public static JsonElement GetPropertyCaseInsensitive(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new KeyNotFoundException($"Property '{propertyName}' was not found.");
    }
}

public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string RolesHeader = "X-Test-Roles";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var userValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var userName = userValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(userName))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, userName) };

        if (Request.Headers.TryGetValue(RolesHeader, out var rolesValues))
        {
            var roleTokens = rolesValues.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var role in roleTokens)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
                claims.Add(new Claim("roles", role));
            }
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>
/// A test fixture that builds a Metadata v2 graph provider describing a scenario, letting
/// <see cref="ServiceRbacTestFixture.CreateFactory"/> seed the in-memory v2 graph from any
/// scenario catalog without coupling to a concrete type.
/// </summary>
public interface ITestMetadataV2GraphSource
{
    TestMetadataV2GraphProvider BuildProvider();
}

public sealed class RbacTestLayerCatalog : ITestMetadataV2GraphSource
{
    private static readonly string[] _supportedFormats = ["JSON", "GeoJSON"];
    private static readonly string[] _capabilities = ["Query", "Create", "Update", "Delete"];

    private readonly string _alphaServiceName;
    private readonly string _betaServiceName;
    private readonly bool _betaAlsoIncludesAlphaLayer;
    private readonly bool _reverseServiceOrder;
    private readonly AccessPolicy? _alphaServiceAccessPolicy;
    private readonly AccessPolicy? _betaServiceAccessPolicy;
    private readonly AccessPolicy? _alphaLayerAccessPolicy;
    private readonly AccessPolicy? _betaLayerAccessPolicy;

    public RbacTestLayerCatalog(
        AccessPolicy? alphaServiceMetadata = null,
        AccessPolicy? betaServiceMetadata = null,
        AccessPolicy? alphaLayerMetadata = null,
        AccessPolicy? betaLayerMetadata = null,
        bool betaAlsoIncludesAlphaLayer = false,
        bool reverseServiceOrder = false,
        string? alphaServiceName = null,
        string? betaServiceName = null)
    {
        _alphaServiceName = alphaServiceName ?? ServiceRbacTestFixture.AlphaService;
        _betaServiceName = betaServiceName ?? ServiceRbacTestFixture.BetaService;
        _alphaServiceAccessPolicy = alphaServiceMetadata;
        _betaServiceAccessPolicy = betaServiceMetadata;
        _alphaLayerAccessPolicy = alphaLayerMetadata;
        _betaLayerAccessPolicy = betaLayerMetadata;
        _betaAlsoIncludesAlphaLayer = betaAlsoIncludesAlphaLayer;
        _reverseServiceOrder = reverseServiceOrder;
    }

    /// <summary>
    /// Builds the Metadata v2 graph for the configured RBAC scenario (two layers, two services,
    /// optional shared alpha layer / reversed service order, with per-service and per-layer
    /// access policies seeded onto services and resources respectively).
    /// </summary>
    public TestMetadataV2GraphProvider BuildProvider()
    {
        var builder = new TestMetadataV2GraphBuilder();
        AddLayer(builder, ServiceRbacTestFixture.AlphaLayerId, "Alpha Layer", _alphaLayerAccessPolicy);
        AddLayer(builder, ServiceRbacTestFixture.BetaLayerId, "Beta Layer", _betaLayerAccessPolicy);

        var alpha = (Name: _alphaServiceName, Policy: _alphaServiceAccessPolicy,
            LayerIds: new[] { ServiceRbacTestFixture.AlphaLayerId });
        var beta = (Name: _betaServiceName, Policy: _betaServiceAccessPolicy,
            LayerIds: _betaAlsoIncludesAlphaLayer
                ? new[] { ServiceRbacTestFixture.AlphaLayerId, ServiceRbacTestFixture.BetaLayerId }
                : new[] { ServiceRbacTestFixture.BetaLayerId });

        foreach (var svc in _reverseServiceOrder ? new[] { beta, alpha } : new[] { alpha, beta })
        {
            var serviceId = $"svc-{svc.Name}";
            builder.AddService(
                serviceId,
                svc.Name,
                protocols: MetadataV2ServiceProtocols.All,
                accessPolicy: svc.Policy,
                options: BuildServiceOptions());

            foreach (var layerId in svc.LayerIds.OrderBy(static id => id))
            {
                // A layer's canonical publication lives in its "owning" service:
                // alpha layer <-> alpha service, beta layer <-> beta service. When
                // betaAlsoIncludesAlphaLayer is set, beta has a SECONDARY (non-primary)
                // publication of alpha so the canonical-boundary enforcement tests
                // (collections leak, secondary-scoped-editor denial) can assert on
                // the IsPrimary flag.
                var isPrimary =
                    (layerId == ServiceRbacTestFixture.AlphaLayerId && svc.Name == _alphaServiceName) ||
                    (layerId == ServiceRbacTestFixture.BetaLayerId && svc.Name == _betaServiceName);

                builder.AddPublication(
                    $"{serviceId}-layer-{layerId.ToString(CultureInfo.InvariantCulture)}",
                    serviceId,
                    $"res-layer-{layerId.ToString(CultureInfo.InvariantCulture)}",
                    layerIndex: layerId,
                    storageBindingId: $"binding-layer-{layerId.ToString(CultureInfo.InvariantCulture)}",
                    publicationType: MetadataV2PublicationType.ODataEntitySet,
                    isPrimary: isPrimary);
            }
        }

        return builder.BuildProvider();
    }

    private static void AddLayer(TestMetadataV2GraphBuilder builder, int layerId, string name, AccessPolicy? accessPolicy)
    {
        var resourceId = $"res-layer-{layerId.ToString(CultureInfo.InvariantCulture)}";
        builder
            .AddResource(
                resourceId,
                name,
                MetadataV2ResourceType.FeatureDataset,
                fields:
                [
                    new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, Description = "Object ID" },
                    new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true, Length = 255, Description = "Name field" },
                    new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = false, Description = "Geometry" }
                ],
                accessPolicy: accessPolicy,
                // WMS / Map render paths gate on resource.Spatial.GeometryType — the
                // original v1 RbacTestLayerCatalog declared GeometryType.Point, so
                // mirror that here or every WMS-protocol test against this fixture
                // resolves zero layers.
                spatial: new MetadataV2ResourceSpatial
                {
                    GeometryType = MetadataV2GeometryType.Point,
                    SpatialReference = new MetadataV2SpatialReference
                    {
                        Srid = 4326,
                        Crs = "EPSG:4326",
                        IsGeographic = true
                    },
                    PrimaryGeometryField = "shape"
                })
            .AddStorageBinding(
                $"binding-layer-{layerId.ToString(CultureInfo.InvariantCulture)}",
                resourceId,
                $"test.layers.{layerId.ToString(CultureInfo.InvariantCulture)}",
                storageLayerId: layerId);
    }

    private static Dictionary<string, JsonElement> BuildServiceOptions()
        => new(StringComparer.Ordinal)
        {
            ["capabilities"] = JsonSerializer.SerializeToElement(_capabilities),
            ["supportedFormats"] = JsonSerializer.SerializeToElement(_supportedFormats)
        };
}

public sealed class TestCrsRegistry : ICrsRegistry
{
    private static readonly CrsDefinition _crs84 = new("http://www.opengis.net/def/crs/OGC/1.3/CRS84", 4326, AxisOrder.EastNorth, true);
    private static readonly CrsDefinition _epsg4326 = new("http://www.opengis.net/def/crs/EPSG/0/4326", 4326, AxisOrder.NorthEast, true);
    private static readonly CrsDefinition _epsg3857 = new("http://www.opengis.net/def/crs/EPSG/0/3857", 3857, AxisOrder.EastNorth, false);

    public ValueTask<CrsDefinition?> ResolveAsync(string? crsIdentifier, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(crsIdentifier))
        {
            return ValueTask.FromResult<CrsDefinition?>(_crs84);
        }

        var normalized = crsIdentifier.Trim().Trim('<', '>');
        if (normalized.Equals("CRS84", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("OGC:CRS84", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(_crs84.Uri, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult<CrsDefinition?>(_crs84);
        }

        if (TryParseEpsg(normalized, out var srid))
        {
            return ValueTask.FromResult<CrsDefinition?>(ResolveBySrid(srid));
        }

        return ValueTask.FromResult<CrsDefinition?>(null);
    }

    public ValueTask<CrsDefinition?> ResolveBySridAsync(int srid, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<CrsDefinition?>(ResolveBySrid(srid));

    public ValueTask<bool> IsSridSupportedAsync(int srid, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(ResolveBySrid(srid).HasValue);

    private static CrsDefinition? ResolveBySrid(int srid)
    {
        return srid switch
        {
            4326 => _epsg4326,
            3857 => _epsg3857,
            _ => null
        };
    }

    private static bool TryParseEpsg(string identifier, out int srid)
    {
        srid = 0;

        if (identifier.StartsWith("http://www.opengis.net/def/crs/EPSG/0/", StringComparison.OrdinalIgnoreCase))
        {
            var code = identifier["http://www.opengis.net/def/crs/EPSG/0/".Length..];
            return int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid);
        }

        if (identifier.StartsWith("urn:ogc:def:crs:EPSG::", StringComparison.OrdinalIgnoreCase))
        {
            var code = identifier["urn:ogc:def:crs:EPSG::".Length..];
            return int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid);
        }

        if (identifier.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase))
        {
            var code = identifier["EPSG:".Length..];
            return int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid);
        }

        return int.TryParse(identifier, NumberStyles.Integer, CultureInfo.InvariantCulture, out srid);
    }
}

public sealed class TestCoordinateTransformService : ICoordinateTransformService
{
    public ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
        double minX,
        double minY,
        double maxX,
        double maxY,
        int fromSrid,
        int toSrid,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<(double MinX, double MinY, double MaxX, double MaxY)?>((minX, minY, maxX, maxY));

    public ValueTask<(double X, double Y)?> TransformPointAsync(
        double x,
        double y,
        int fromSrid,
        int toSrid,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<(double X, double Y)?>((x, y));
}

public sealed class NoOpGeometryTopologyValidator : IGeometryTopologyValidator
{
    public Task<GeometryValidationResult> ValidateTopologyAsync(byte[] wkb, CancellationToken cancellationToken = default)
        => Task.FromResult(GeometryValidationResult.Success());

    public Task<GeometryRepairResult> RepairAsync(byte[] wkb, CancellationToken cancellationToken = default)
        => Task.FromResult(GeometryRepairResult.NotNeeded(wkb));
}
