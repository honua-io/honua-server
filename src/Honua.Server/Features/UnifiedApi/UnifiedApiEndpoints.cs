// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.UnifiedApi;

/// <summary>
/// Unified API endpoints that provide a clean developer experience
/// across all supported geospatial protocols.
/// </summary>
public static class UnifiedApiEndpoints
{
    public static IEndpointRouteBuilder MapUnifiedApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Control plane discovery - administrative and management operations
        endpoints.MapGet("/", HandleControlPlaneDiscovery)
            .WithName("ControlPlaneDiscovery")
            .WithSummary("Discover Honua API control plane capabilities")
            .WithDescription("Returns available control plane endpoints for service management, configuration, and administration")
            .WithTags("Control Plane")
            .Produces<ControlPlaneResponse>(200);

        // Service discovery - shows all available protocols
        endpoints.MapGet("/{serviceId}", HandleServiceDiscovery)
            .WithName("ServiceDiscovery")
            .WithSummary("Discover available protocols for a service")
            .WithDescription("Returns all available protocols and endpoints for accessing this geospatial service")
            .WithTags("Discovery")
            .Produces<ServiceDiscoveryResponse>(200)
            .Produces(404);

        // Auto-negotiated endpoints
        endpoints.MapGet("/{serviceId}/features", HandleAutoNegotiatedFeatures)
            .WithName("AutoNegotiatedFeatures")
            .WithSummary("Get features using best available protocol")
            .WithDescription("Automatically selects the best protocol based on client capabilities and Accept headers")
            .WithTags("Data", "Auto-Negotiation")
            .Produces(200)
            .Produces(404);

        endpoints.MapGet("/{serviceId}/data", HandleAutoNegotiatedData)
            .WithName("AutoNegotiatedData")
            .WithSummary("Get raw data using best available format")
            .WithTags("Data", "Auto-Negotiation")
            .Produces(200)
            .Produces(404);

        endpoints.MapGet("/{serviceId}/map", HandleAutoNegotiatedMap)
            .WithName("AutoNegotiatedMap")
            .WithSummary("Get map representation using best available format")
            .WithTags("Visualization", "Auto-Negotiation")
            .Produces(200)
            .Produces(404);

        // Control plane endpoint groups
        MapControlPlaneEndpoints(endpoints);

        // Protocol-specific endpoint groups
        MapGeoservicesEndpoints(endpoints);
        MapOgcApiEndpoints(endpoints);  // Modern OGC API suite
        MapOgcLegacyEndpoints(endpoints);  // Legacy OGC services (WMS, WFS, etc.)
        MapODataEndpoints(endpoints);
        MapGrpcEndpoints(endpoints);   // gRPC for mobile/native apps
        MapMcpEndpoints(endpoints);    // Model Context Protocol for AI integration
        MapTileEndpoints(endpoints);

        return endpoints;
    }

    private static void MapControlPlaneEndpoints(IEndpointRouteBuilder endpoints)
    {
        var adminGroup = endpoints.MapGroup("/admin")
            .WithTags("Control Plane - Administration")
            .RequireAuthorization(); // Control plane requires authentication

        var servicesGroup = endpoints.MapGroup("/services")
            .WithTags("Control Plane - Services")
            .RequireAuthorization();

        var configGroup = endpoints.MapGroup("/config")
            .WithTags("Control Plane - Configuration")
            .RequireAuthorization();

        var monitoringGroup = endpoints.MapGroup("/monitoring")
            .WithTags("Control Plane - Monitoring")
            .RequireAuthorization();

        // Service management
        servicesGroup.MapGet("/", HandleListServices)
            .WithName("ListServices")
            .WithSummary("List all available services");

        servicesGroup.MapPost("/", HandleCreateService)
            .WithName("CreateService")
            .WithSummary("Create a new geospatial service");

        servicesGroup.MapGet("/{serviceId}", HandleGetService)
            .WithName("GetService")
            .WithSummary("Get service configuration");

        servicesGroup.MapPut("/{serviceId}", HandleUpdateService)
            .WithName("UpdateService")
            .WithSummary("Update service configuration");

        servicesGroup.MapDelete("/{serviceId}", HandleDeleteService)
            .WithName("DeleteService")
            .WithSummary("Delete a service");

        // Configuration management
        configGroup.MapGet("/", HandleGetGlobalConfig)
            .WithName("GetGlobalConfiguration")
            .WithSummary("Get global Honua configuration");

        configGroup.MapPut("/", HandleUpdateGlobalConfig)
            .WithName("UpdateGlobalConfiguration")
            .WithSummary("Update global Honua configuration");

        configGroup.MapGet("/auth", HandleGetAuthConfig)
            .WithName("GetAuthConfiguration")
            .WithSummary("Get authentication configuration");

        configGroup.MapPut("/auth", HandleUpdateAuthConfig)
            .WithName("UpdateAuthConfiguration")
            .WithSummary("Update authentication configuration");

        // User and role management
        adminGroup.MapGet("/users", HandleListUsers)
            .WithName("ListUsers")
            .WithSummary("List all users");

        adminGroup.MapPost("/users", HandleCreateUser)
            .WithName("CreateUser")
            .WithSummary("Create a new user");

        adminGroup.MapGet("/roles", HandleListRoles)
            .WithName("ListRoles")
            .WithSummary("List all roles");

        // Monitoring and observability
        monitoringGroup.MapGet("/health", HandleSystemHealth)
            .WithName("SystemHealth")
            .WithSummary("Get system health status");

        monitoringGroup.MapGet("/metrics", HandleSystemMetrics)
            .WithName("SystemMetrics")
            .WithSummary("Get system performance metrics");

        monitoringGroup.MapGet("/logs", HandleSystemLogs)
            .WithName("SystemLogs")
            .WithSummary("Get system logs");

        // Deployment and infrastructure control
        adminGroup.MapPost("/deploy", HandleDeploy)
            .WithName("Deploy")
            .WithSummary("Deploy configuration changes");

        adminGroup.MapGet("/status", HandleDeploymentStatus)
            .WithName("DeploymentStatus")
            .WithSummary("Get deployment status");
    }

    private static void MapGeoservicesEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/{serviceId}/geoservices")
            .WithTags("GeoServices")
            .WithOpenApi();

        // Service metadata
        group.MapGet("/", HandleGeoservicesMetadata)
            .WithName("GeoservicesMetadata")
            .WithSummary("Get GeoServices metadata")
            .Produces<object>(200);

        // Redirect to existing FeatureServer endpoints
        group.MapGet("/{path:path}", (string serviceId, string path, HttpContext context) =>
        {
            var newPath = $"/rest/services/{serviceId}/FeatureServer/{path}";
            var queryString = context.Request.QueryString.Value;
            return Results.Redirect(newPath + queryString, permanent: false);
        })
        .WithName("GeoservicesProxy")
        .WithSummary("Proxy to GeoServices FeatureServer")
        .ExcludeFromDescription();

        group.MapPost("/{path:path}", (string serviceId, string path, HttpContext context) =>
        {
            var newPath = $"/rest/services/{serviceId}/FeatureServer/{path}";
            return Results.Redirect(newPath, permanent: false);
        })
        .WithName("GeoservicesProxyPost")
        .ExcludeFromDescription();
    }

    private static void MapOgcApiEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/{serviceId}/ogcapi")
            .WithTags("OGC API")
            .WithOpenApi();

        group.MapGet("/", HandleOgcApiMetadata)
            .WithName("OgcApiMetadata")
            .WithSummary("Get OGC API metadata")
            .Produces<object>(200);

        // Redirect to existing OGC API endpoints
        group.MapGet("/{path:path}", (string serviceId, string path, HttpContext context) =>
        {
            var newPath = $"/ogc/features/collections/{serviceId}/{path}";
            var queryString = context.Request.QueryString.Value;
            return Results.Redirect(newPath + queryString, permanent: false);
        })
        .WithName("OgcApiProxy")
        .WithSummary("Proxy to OGC API Features")
        .ExcludeFromDescription();
    }

    private static void MapOgcLegacyEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/{serviceId}/ogc")
            .WithTags("OGC Legacy")
            .WithOpenApi();

        group.MapGet("/", HandleOgcLegacyMetadata)
            .WithName("OgcLegacyMetadata")
            .WithSummary("Get legacy OGC services metadata")
            .Produces<object>(200);

        // WMS endpoint
        group.MapGet("/wms", (string serviceId, HttpContext context) =>
        {
            var newPath = $"/rest/services/{serviceId}/MapServer/WMSServer";
            var queryString = context.Request.QueryString.Value;
            return Results.Redirect(newPath + queryString, permanent: false);
        })
        .WithName("OgcWmsProxy")
        .WithSummary("Proxy to WMS service");

        // WFS endpoint
        group.MapGet("/wfs", (string serviceId, HttpContext context) =>
        {
            var newPath = $"/wfs"; // TODO: Add service-specific WFS routing
            var queryString = context.Request.QueryString.Value;
            return Results.Redirect(newPath + queryString, permanent: false);
        })
        .WithName("OgcWfsProxy")
        .WithSummary("Proxy to WFS service");
    }

    private static void MapODataEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/{serviceId}/odata")
            .WithTags("OData")
            .WithOpenApi();

        group.MapGet("/", HandleODataMetadata)
            .WithName("ODataMetadata")
            .WithSummary("Get OData service metadata")
            .Produces<object>(200);

        group.MapGet("/$metadata", (string serviceId, HttpContext context) =>
        {
            var newPath = $"/odata/{serviceId}/$metadata";
            var queryString = context.Request.QueryString.Value;
            return Results.Redirect(newPath + queryString, permanent: false);
        })
        .WithName("ODataSchema")
        .WithSummary("Get OData schema");

        group.MapGet("/{path:path}", (string serviceId, string path, HttpContext context) =>
        {
            var newPath = $"/odata/{serviceId}/{path}";
            var queryString = context.Request.QueryString.Value;
            return Results.Redirect(newPath + queryString, permanent: false);
        })
        .WithName("ODataProxy")
        .WithSummary("Proxy to OData endpoints")
        .ExcludeFromDescription();
    }

    private static void MapTileEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/{serviceId}/tiles")
            .WithTags("Tiles")
            .WithOpenApi();

        group.MapGet("/", HandleTileMetadata)
            .WithName("TileMetadata")
            .WithSummary("Get tile service metadata")
            .Produces<object>(200);

        group.MapGet("/{z:int}/{x:int}/{y:int}.mvt", (string serviceId, int z, int x, int y, HttpContext context) =>
        {
            // Determine layer ID from service - this would need service lookup
            var layerId = GetLayerIdFromService(serviceId); // TODO: Implement
            var newPath = $"/tiles/{layerId}/{z}/{x}/{y}.mvt";
            var queryString = context.Request.QueryString.Value;
            return Results.Redirect(newPath + queryString, permanent: false);
        })
        .WithName("VectorTile")
        .WithSummary("Get vector tile")
        .Produces<byte[]>(200, "application/vnd.mapbox-vector-tile");
    }

    private static void MapGrpcEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/{serviceId}/grpc")
            .WithTags("gRPC")
            .WithOpenApi();

        group.MapGet("/", HandleGrpcMetadata)
            .WithName("GrpcMetadata")
            .WithSummary("Get gRPC service metadata and reflection info")
            .Produces<object>(200);

        // gRPC service reflection endpoint
        group.MapGet("/reflection", (string serviceId, HttpContext context) =>
        {
            var metadata = new
            {
                ServiceId = serviceId,
                GrpcEndpoint = $"grpc://api.example.com/{serviceId}",
                ProtoFiles = new[]
                {
                    $"/{serviceId}/grpc/proto/features.proto",
                    $"/{serviceId}/grpc/proto/geometry.proto"
                },
                Services = new[]
                {
                    "honua.FeatureService",
                    "honua.StreamingService",
                    "honua.GeometryService"
                },
                Documentation = $"/docs/{serviceId}/grpc/"
            };
            return Results.Ok(metadata);
        })
        .WithName("GrpcReflection")
        .WithSummary("Get gRPC reflection information");

        // Proto file serving
        group.MapGet("/proto/{fileName}", (string serviceId, string fileName) =>
        {
            // TODO: Serve actual proto files
            return Results.NotFound($"Proto file {fileName} not found for service {serviceId}");
        })
        .WithName("GrpcProtoFile")
        .WithSummary("Download proto definition files")
        .Produces<string>(200, "text/plain");
    }

    private static void MapMcpEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/{serviceId}/mcp")
            .WithTags("MCP")
            .WithOpenApi();

        group.MapGet("/", HandleMcpMetadata)
            .WithName("McpMetadata")
            .WithSummary("Get Model Context Protocol metadata")
            .Produces<object>(200);

        // MCP tools discovery
        group.MapGet("/tools", (string serviceId) =>
        {
            var tools = new
            {
                ServiceId = serviceId,
                AvailableTools = new[]
                {
                    new
                    {
                        Name = "query_features",
                        Description = $"Query features from {serviceId} with natural language",
                        InputSchema = new
                        {
                            Type = "object",
                            Properties = new
                            {
                                Query = new { Type = "string", Description = "Natural language query" },
                                Limit = new { Type = "integer", Description = "Maximum results", Default = 100 }
                            }
                        }
                    },
                    new
                    {
                        Name = "spatial_analysis",
                        Description = $"Perform spatial analysis on {serviceId}",
                        InputSchema = new
                        {
                            Type = "object",
                            Properties = new
                            {
                                Operation = new { Type = "string", Description = "Analysis type (intersect, buffer, etc.)" },
                                Geometry = new { Type = "object", Description = "GeoJSON geometry" }
                            }
                        }
                    }
                }
            };
            return Results.Ok(tools);
        })
        .WithName("McpTools")
        .WithSummary("Get available MCP tools for AI integration");

        // MCP resource access
        group.MapGet("/resources", (string serviceId) =>
        {
            var resources = new
            {
                ServiceId = serviceId,
                AvailableResources = new[]
                {
                    new
                    {
                        Uri = $"honua://{serviceId}/features",
                        Name = $"{serviceId} Features",
                        Description = "Geospatial features dataset",
                        MimeType = "application/geo+json"
                    },
                    new
                    {
                        Uri = $"honua://{serviceId}/schema",
                        Name = $"{serviceId} Schema",
                        Description = "Feature schema and field definitions",
                        MimeType = "application/json"
                    }
                }
            };
            return Results.Ok(resources);
        })
        .WithName("McpResources")
        .WithSummary("Get available MCP resources");
    }


    // Discovery and auto-negotiation handlers
    private static async Task<IResult> HandleServiceDiscovery(
        string serviceId,
        HttpContext context,
        [FromServices] IServiceProvider serviceProvider)
    {
        // TODO: Check if service exists
        var response = new ServiceDiscoveryResponse
        {
            ServiceId = serviceId,
            Name = serviceId, // TODO: Get actual service name
            Protocols = new Dictionary<string, ProtocolInfo>
            {
                ["geoservices"] = new()
                {
                    Name = "GeoServices (ArcGIS REST compatible)",
                    BaseUrl = $"/{serviceId}/geoservices/",
                    Capabilities = ["query", "editing", "metadata"],
                    MimeTypes = ["application/json"]
                },
                ["ogcapi"] = new()
                {
                    Name = "OGC API Features (Modern)",
                    BaseUrl = $"/{serviceId}/ogcapi/",
                    Capabilities = ["query", "metadata", "geojson"],
                    MimeTypes = ["application/geo+json", "application/json"]
                },
                ["ogc"] = new()
                {
                    Name = "OGC Legacy Services (WMS, WFS)",
                    BaseUrl = $"/{serviceId}/ogc/",
                    Capabilities = ["wms", "wfs", "mapping"],
                    MimeTypes = ["image/png", "application/gml+xml", "text/xml"]
                },
                ["odata"] = new()
                {
                    Name = "OData v4",
                    BaseUrl = $"/{serviceId}/odata/",
                    Capabilities = ["query", "metadata"],
                    MimeTypes = ["application/json", "application/xml"]
                },
                ["grpc"] = new()
                {
                    Name = "gRPC (Mobile & Native Apps)",
                    BaseUrl = $"/{serviceId}/grpc/",
                    Capabilities = ["streaming", "mobile", "native", "high-performance"],
                    MimeTypes = ["application/grpc", "application/grpc+proto"]
                },
                ["mcp"] = new()
                {
                    Name = "Model Context Protocol (AI Integration)",
                    BaseUrl = $"/{serviceId}/mcp/",
                    Capabilities = ["ai", "context", "llm-integration", "semantic-search"],
                    MimeTypes = ["application/json", "application/mcp+json"]
                },
                ["tiles"] = new()
                {
                    Name = "Vector Tiles",
                    BaseUrl = $"/{serviceId}/tiles/",
                    Capabilities = ["visualization"],
                    MimeTypes = ["application/vnd.mapbox-vector-tile"]
                }
            },
            AutoNegotiation = new AutoNegotiationInfo
            {
                FeaturesUrl = $"/{serviceId}/features",
                DataUrl = $"/{serviceId}/data",
                MapUrl = $"/{serviceId}/map"
            },
            Documentation = $"/docs/{serviceId}/",
            OpenApiSpec = $"/{serviceId}/openapi.json"
        };

        return Results.Ok(response);
    }

    private static async Task<IResult> HandleAutoNegotiatedFeatures(
        string serviceId,
        HttpContext context)
    {
        var acceptHeader = context.Request.Headers.Accept.ToString();
        var userAgent = context.Request.Headers.UserAgent.ToString();

        // Smart protocol selection
        var selectedProtocol = SelectBestProtocol(acceptHeader, userAgent, "features");

        return selectedProtocol switch
        {
            "grpc" => Results.Redirect($"/{serviceId}/grpc/", permanent: false),
            "mcp" => Results.Redirect($"/{serviceId}/mcp/", permanent: false),
            "ogcapi" => Results.Redirect($"/{serviceId}/ogcapi/features", permanent: false),
            "ogc" => Results.Redirect($"/{serviceId}/ogc/wms", permanent: false), // Legacy OGC defaults to WMS
            "odata" => Results.Redirect($"/{serviceId}/odata/Features", permanent: false),
            "geoservices" => Results.Redirect($"/{serviceId}/geoservices/query", permanent: false),
            _ => Results.Redirect($"/{serviceId}/geoservices/query", permanent: false) // Default
        };
    }

    private static async Task<IResult> HandleAutoNegotiatedData(string serviceId, HttpContext context)
    {
        // Similar auto-negotiation for raw data access
        return Results.Redirect($"/{serviceId}/odata/Features", permanent: false);
    }

    private static async Task<IResult> HandleAutoNegotiatedMap(string serviceId, HttpContext context)
    {
        // Auto-negotiate best map format
        return Results.Redirect($"/{serviceId}/tiles/", permanent: false);
    }

    private static Task<IResult> HandleGeoservicesMetadata(string serviceId)
    {
        var metadata = new
        {
            ServiceType = "GeoServices FeatureServer",
            ServiceId = serviceId,
            Capabilities = new[] { "Query", "Edit", "Create", "Update", "Delete" },
            Endpoints = new
            {
                Query = $"/{serviceId}/geoservices/query",
                Layers = $"/{serviceId}/geoservices/layers",
                AddFeatures = $"/{serviceId}/geoservices/addFeatures",
                UpdateFeatures = $"/{serviceId}/geoservices/updateFeatures",
                DeleteFeatures = $"/{serviceId}/geoservices/deleteFeatures"
            }
        };
        return Task.FromResult(Results.Ok(metadata));
    }

    private static Task<IResult> HandleOgcApiMetadata(string serviceId)
    {
        var metadata = new
        {
            ServiceType = "OGC API Features",
            ServiceId = serviceId,
            Version = "1.0",
            Description = "Modern OGC API Features service with GeoJSON support",
            Endpoints = new
            {
                Collections = $"/{serviceId}/ogcapi/collections",
                Features = $"/{serviceId}/ogcapi/features",
                Conformance = $"/{serviceId}/ogcapi/conformance"
            },
            Formats = new[] { "application/geo+json", "application/json" }
        };
        return Task.FromResult(Results.Ok(metadata));
    }

    private static Task<IResult> HandleOgcLegacyMetadata(string serviceId)
    {
        var metadata = new
        {
            ServiceType = "OGC Legacy Services",
            ServiceId = serviceId,
            Description = "Traditional OGC services (WMS, WFS) for desktop GIS compatibility",
            Services = new
            {
                WMS = new
                {
                    Version = "1.3.0",
                    Endpoint = $"/{serviceId}/ogc/wms",
                    Capabilities = $"/{serviceId}/ogc/wms?SERVICE=WMS&REQUEST=GetCapabilities",
                    Formats = new[] { "image/png", "image/jpeg" }
                },
                WFS = new
                {
                    Version = "2.0.0",
                    Endpoint = $"/{serviceId}/ogc/wfs",
                    Capabilities = $"/{serviceId}/ogc/wfs?SERVICE=WFS&REQUEST=GetCapabilities",
                    Formats = new[] { "application/gml+xml", "text/xml" }
                }
            }
        };
        return Task.FromResult(Results.Ok(metadata));
    }

    private static Task<IResult> HandleODataMetadata(string serviceId)
    {
        var metadata = new
        {
            ServiceType = "OData v4",
            ServiceId = serviceId,
            Endpoints = new
            {
                Schema = $"/{serviceId}/odata/$metadata",
                Features = $"/{serviceId}/odata/Features"
            }
        };
        return Task.FromResult(Results.Ok(metadata));
    }

    private static Task<IResult> HandleTileMetadata(string serviceId)
    {
        var metadata = new
        {
            ServiceType = "Vector Tiles",
            ServiceId = serviceId,
            TileMatrixSets = new[] { "WebMercatorQuad" },
            Template = $"/{serviceId}/tiles/{{z}}/{{x}}/{{y}}.mvt"
        };
        return Task.FromResult(Results.Ok(metadata));
    }

    private static Task<IResult> HandleGrpcMetadata(string serviceId)
    {
        var metadata = new
        {
            ServiceType = "gRPC",
            ServiceId = serviceId,
            Description = "High-performance gRPC API for mobile and native applications",
            Endpoint = $"grpc://api.example.com/{serviceId}",
            Features = new[]
            {
                "Bidirectional streaming",
                "Type-safe protocol buffers",
                "Mobile-optimized",
                "Native SDK support",
                "Offline synchronization"
            },
            SupportedLanguages = new[]
            {
                "C#/.NET MAUI",
                "Swift/iOS",
                "Kotlin/Android",
                "Flutter/Dart",
                "React Native",
                "C++/Unreal Engine"
            },
            Endpoints = new
            {
                Reflection = $"/{serviceId}/grpc/reflection",
                ProtoFiles = $"/{serviceId}/grpc/proto/",
                Documentation = $"/docs/{serviceId}/grpc/"
            }
        };
        return Task.FromResult(Results.Ok(metadata));
    }

    private static Task<IResult> HandleMcpMetadata(string serviceId)
    {
        var metadata = new
        {
            ServiceType = "Model Context Protocol",
            ServiceId = serviceId,
            Description = "AI-friendly protocol for LLM integration and semantic search",
            Version = "0.1.0",
            Features = new[]
            {
                "Natural language queries",
                "Semantic geospatial search",
                "AI tool integration",
                "Context-aware responses",
                "LLM-friendly data formats"
            },
            Capabilities = new
            {
                Tools = new[] { "query_features", "spatial_analysis", "data_summary" },
                Resources = new[] { "features", "schema", "metadata", "samples" },
                Prompts = new[] { "explore_data", "find_patterns", "generate_insights" }
            },
            Endpoints = new
            {
                Tools = $"/{serviceId}/mcp/tools",
                Resources = $"/{serviceId}/mcp/resources",
                Documentation = $"/docs/{serviceId}/mcp/"
            }
        };
        return Task.FromResult(Results.Ok(metadata));
    }

    // Control plane handlers
    private static async Task<IResult> HandleControlPlaneDiscovery(HttpContext context)
    {
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";

        var response = new ControlPlaneResponse
        {
            Name = "Honua Geospatial Platform",
            Version = "1.0.0",
            Description = "Control plane for managing geospatial services, users, and infrastructure",
            Capabilities = new ControlPlaneCapabilities
            {
                ServiceManagement = new ServiceManagementCapabilities
                {
                    BaseUrl = "/services/",
                    Operations = ["create", "read", "update", "delete", "list"],
                    SupportedFormats = ["geojson", "shapefile", "geoparquet", "cog", "raster"]
                },
                UserManagement = new UserManagementCapabilities
                {
                    BaseUrl = "/admin/users/",
                    Authentication = ["api-key", "oidc", "oauth2"],
                    Authorization = ["rbac", "policy-based"]
                },
                Configuration = new ConfigurationCapabilities
                {
                    BaseUrl = "/config/",
                    Scopes = ["global", "service", "user"],
                    Features = ["hot-reload", "validation", "rollback"]
                },
                Monitoring = new MonitoringCapabilities
                {
                    BaseUrl = "/monitoring/",
                    Metrics = ["performance", "usage", "errors", "security"],
                    Alerting = ["webhooks", "email", "slack"]
                },
                Deployment = new DeploymentCapabilities
                {
                    BaseUrl = "/admin/deploy/",
                    Strategies = ["rolling", "blue-green", "canary"],
                    Infrastructure = ["kubernetes", "docker", "cloud-native"]
                }
            },
            Endpoints = new ControlPlaneEndpoints
            {
                Services = "/services/",
                Configuration = "/config/",
                Users = "/admin/users/",
                Monitoring = "/monitoring/",
                Documentation = "/docs/control-plane/",
                ApiSpec = "/control-plane/openapi.json"
            }
        };

        return Results.Ok(response);
    }

    // Service management handlers (redirects to existing endpoints)
    private static Task<IResult> HandleListServices() =>
        Task.FromResult(Results.Redirect("/admin/services", permanent: false));

    private static Task<IResult> HandleCreateService() =>
        Task.FromResult(Results.Redirect("/admin/services", permanent: false));

    private static Task<IResult> HandleGetService(string serviceId) =>
        Task.FromResult(Results.Redirect($"/admin/services/{serviceId}", permanent: false));

    private static Task<IResult> HandleUpdateService(string serviceId) =>
        Task.FromResult(Results.Redirect($"/admin/services/{serviceId}", permanent: false));

    private static Task<IResult> HandleDeleteService(string serviceId) =>
        Task.FromResult(Results.Redirect($"/admin/services/{serviceId}", permanent: false));

    // Configuration management handlers
    private static Task<IResult> HandleGetGlobalConfig() =>
        Task.FromResult(Results.Redirect("/admin/config", permanent: false));

    private static Task<IResult> HandleUpdateGlobalConfig() =>
        Task.FromResult(Results.Redirect("/admin/config", permanent: false));

    private static Task<IResult> HandleGetAuthConfig() =>
        Task.FromResult(Results.Redirect("/admin/auth/config", permanent: false));

    private static Task<IResult> HandleUpdateAuthConfig() =>
        Task.FromResult(Results.Redirect("/admin/auth/config", permanent: false));

    // User management handlers
    private static Task<IResult> HandleListUsers() =>
        Task.FromResult(Results.Redirect("/admin/users", permanent: false));

    private static Task<IResult> HandleCreateUser() =>
        Task.FromResult(Results.Redirect("/admin/users", permanent: false));

    private static Task<IResult> HandleListRoles() =>
        Task.FromResult(Results.Redirect("/admin/roles", permanent: false));

    // Monitoring handlers
    private static Task<IResult> HandleSystemHealth() =>
        Task.FromResult(Results.Redirect("/health", permanent: false));

    private static Task<IResult> HandleSystemMetrics() =>
        Task.FromResult(Results.Redirect("/admin/metrics", permanent: false));

    private static Task<IResult> HandleSystemLogs() =>
        Task.FromResult(Results.Redirect("/admin/logs", permanent: false));

    // Deployment handlers
    private static Task<IResult> HandleDeploy() =>
        Task.FromResult(Results.Redirect("/admin/deploy", permanent: false));

    private static Task<IResult> HandleDeploymentStatus() =>
        Task.FromResult(Results.Redirect("/admin/deploy/status", permanent: false));

    private static string SelectBestProtocol(string acceptHeader, string userAgent, string operation)
    {
        // Mobile and native app detection - prefer gRPC for performance
        if (userAgent.Contains("Honua", StringComparison.OrdinalIgnoreCase) || // Your own mobile SDK
            userAgent.Contains("Mobile", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("Flutter", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("ReactNative", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("MAUI", StringComparison.OrdinalIgnoreCase))
            return "grpc";

        // AI/LLM client detection for MCP
        if (userAgent.Contains("Claude", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("GPT", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("LLM", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("AI", StringComparison.OrdinalIgnoreCase) ||
            acceptHeader.Contains("application/mcp"))
            return "mcp";

        // gRPC protocol detection
        if (acceptHeader.Contains("application/grpc"))
            return "grpc";

        // Client-specific optimization
        if (userAgent.Contains("QGIS", StringComparison.OrdinalIgnoreCase))
        {
            // QGIS supports both - prefer OGC API for features, legacy OGC for maps
            return operation == "features" ? "ogcapi" : "ogc";
        }

        if (userAgent.Contains("ArcGIS", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("Esri", StringComparison.OrdinalIgnoreCase))
            return "geoservices";

        if (userAgent.Contains("PowerBI", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("Excel", StringComparison.OrdinalIgnoreCase))
            return "odata";

        // Desktop GIS applications often prefer legacy OGC for WMS/WFS
        if (userAgent.Contains("GIS", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("MapInfo", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("FME", StringComparison.OrdinalIgnoreCase))
            return operation == "features" ? "ogcapi" : "ogc";

        // Accept header optimization
        if (acceptHeader.Contains("application/geo+json"))
            return "ogcapi"; // Modern OGC API for GeoJSON

        if (acceptHeader.Contains("image/png") || acceptHeader.Contains("image/jpeg"))
            return "ogc"; // Legacy OGC WMS for images

        if (acceptHeader.Contains("application/gml") || acceptHeader.Contains("text/xml"))
            return "ogc"; // Legacy OGC WFS for GML/XML

        if (acceptHeader.Contains("application/json") && operation == "features")
            return "geoservices";

        // Default to GeoServices for maximum compatibility
        return "geoservices";
    }

    private static int GetLayerIdFromService(string serviceId)
    {
        // TODO: Implement service-to-layer mapping
        // This would need to lookup the service configuration
        return 0; // Default to first layer for now
    }
}

// Response models for discovery
public record ServiceDiscoveryResponse
{
    public string ServiceId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, ProtocolInfo> Protocols { get; init; } = new();
    public AutoNegotiationInfo AutoNegotiation { get; init; } = new();
    public string Documentation { get; init; } = string.Empty;
    public string OpenApiSpec { get; init; } = string.Empty;
}

public record ProtocolInfo
{
    public string Name { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public string[] Capabilities { get; init; } = Array.Empty<string>();
    public string[] MimeTypes { get; init; } = Array.Empty<string>();
}

public record AutoNegotiationInfo
{
    public string FeaturesUrl { get; init; } = string.Empty;
    public string DataUrl { get; init; } = string.Empty;
    public string MapUrl { get; init; } = string.Empty;
}

// Control plane response models
public record ControlPlaneResponse
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public ControlPlaneCapabilities Capabilities { get; init; } = new();
    public ControlPlaneEndpoints Endpoints { get; init; } = new();
}

public record ControlPlaneCapabilities
{
    public ServiceManagementCapabilities ServiceManagement { get; init; } = new();
    public UserManagementCapabilities UserManagement { get; init; } = new();
    public ConfigurationCapabilities Configuration { get; init; } = new();
    public MonitoringCapabilities Monitoring { get; init; } = new();
    public DeploymentCapabilities Deployment { get; init; } = new();
}

public record ServiceManagementCapabilities
{
    public string BaseUrl { get; init; } = string.Empty;
    public string[] Operations { get; init; } = Array.Empty<string>();
    public string[] SupportedFormats { get; init; } = Array.Empty<string>();
}

public record UserManagementCapabilities
{
    public string BaseUrl { get; init; } = string.Empty;
    public string[] Authentication { get; init; } = Array.Empty<string>();
    public string[] Authorization { get; init; } = Array.Empty<string>();
}

public record ConfigurationCapabilities
{
    public string BaseUrl { get; init; } = string.Empty;
    public string[] Scopes { get; init; } = Array.Empty<string>();
    public string[] Features { get; init; } = Array.Empty<string>();
}

public record MonitoringCapabilities
{
    public string BaseUrl { get; init; } = string.Empty;
    public string[] Metrics { get; init; } = Array.Empty<string>();
    public string[] Alerting { get; init; } = Array.Empty<string>();
}

public record DeploymentCapabilities
{
    public string BaseUrl { get; init; } = string.Empty;
    public string[] Strategies { get; init; } = Array.Empty<string>();
    public string[] Infrastructure { get; init; } = Array.Empty<string>();
}

public record ControlPlaneEndpoints
{
    public string Services { get; init; } = string.Empty;
    public string Configuration { get; init; } = string.Empty;
    public string Users { get; init; } = string.Empty;
    public string Monitoring { get; init; } = string.Empty;
    public string Documentation { get; init; } = string.Empty;
    public string ApiSpec { get; init; } = string.Empty;
}