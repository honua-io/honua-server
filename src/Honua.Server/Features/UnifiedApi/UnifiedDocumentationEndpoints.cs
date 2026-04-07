// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.UnifiedApi;

/// <summary>
/// Documentation endpoints for the unified API that provide
/// developer-friendly guides and examples.
/// </summary>
public static class UnifiedDocumentationEndpoints
{
    public static IEndpointRouteBuilder MapUnifiedDocumentationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Service-specific documentation
        endpoints.MapGet("/docs/{serviceId}/", HandleServiceDocumentation)
            .WithName("ServiceDocumentation")
            .WithSummary("Get comprehensive documentation for a service")
            .WithTags("Documentation")
            .Produces<string>(200, "text/html")
            .ExcludeFromDescription();

        // Quick start guide with copy-paste examples
        endpoints.MapGet("/docs/{serviceId}/quickstart", HandleQuickStartGuide)
            .WithName("QuickStartGuide")
            .WithSummary("Get quick start examples for all protocols")
            .WithTags("Documentation")
            .Produces<object>(200)
            .ExcludeFromDescription();

        // Protocol-specific documentation
        endpoints.MapGet("/docs/{serviceId}/geoservices", HandleGeoservicesDocumentation)
            .WithName("GeoservicesDocumentation")
            .WithTags("Documentation")
            .ExcludeFromDescription();

        endpoints.MapGet("/docs/{serviceId}/ogcapi", HandleOgcApiDocumentation)
            .WithName("OgcApiDocumentation")
            .WithTags("Documentation")
            .ExcludeFromDescription();

        endpoints.MapGet("/docs/{serviceId}/ogc", HandleOgcLegacyDocumentation)
            .WithName("OgcLegacyDocumentation")
            .WithTags("Documentation")
            .ExcludeFromDescription();

        endpoints.MapGet("/docs/{serviceId}/odata", HandleODataDocumentation)
            .WithName("ODataDocumentation")
            .WithTags("Documentation")
            .ExcludeFromDescription();

        // SDK and tooling
        endpoints.MapGet("/docs/{serviceId}/postman", HandlePostmanCollection)
            .WithName("PostmanCollection")
            .WithSummary("Generate Postman collection for this service")
            .WithTags("Documentation")
            .Produces<object>(200)
            .ExcludeFromDescription();

        endpoints.MapGet("/docs/{serviceId}/sdk", HandleSdkInformation)
            .WithName("SdkInformation")
            .WithSummary("Get information about available SDKs")
            .WithTags("Documentation")
            .Produces<object>(200)
            .ExcludeFromDescription();

        // OpenAPI spec generation
        endpoints.MapGet("/{serviceId}/openapi.json", HandleOpenApiSpec)
            .WithName("ServiceOpenApiSpec")
            .WithSummary("Get OpenAPI specification for this service")
            .WithTags("Documentation")
            .Produces<object>(200, "application/json")
            .ExcludeFromDescription();

        return endpoints;
    }

    private static async Task<IResult> HandleServiceDocumentation(
        string serviceId,
        HttpContext context)
    {
        var html = GenerateServiceDocumentationHtml(serviceId, context.Request.GetDisplayUrl());
        return Results.Text(html, "text/html");
    }

    private static async Task<IResult> HandleQuickStartGuide(string serviceId)
    {
        var baseUrl = $"https://api.example.com"; // TODO: Get from configuration

        var quickStart = new
        {
            Service = serviceId,
            AutoNegotiation = new
            {
                Description = "Smart endpoints that automatically choose the best protocol",
                Examples = new
                {
                    GetFeatures = $"curl {baseUrl}/{serviceId}/features",
                    GetData = $"curl {baseUrl}/{serviceId}/data",
                    GetMap = $"curl {baseUrl}/{serviceId}/map"
                }
            },
            Protocols = new
            {
                GeoServices = new
                {
                    Description = "ArcGIS REST compatible API",
                    QueryFeatures = $"curl \"{baseUrl}/{serviceId}/geoservices/query?where=1=1&outFields=*&f=json\"",
                    GetMetadata = $"curl {baseUrl}/{serviceId}/geoservices/",
                    AddFeatures = $"curl -X POST {baseUrl}/{serviceId}/geoservices/addFeatures -d '{{\"features\": [...]}}'",
                    JavaScript = $@"
// JavaScript/TypeScript
const response = await fetch('{baseUrl}/{serviceId}/geoservices/query?where=1=1&outFields=*&f=json');
const data = await response.json();
console.log(`Found ${{data.features.length}} features`);",
                    Python = $@"
# Python
import requests
response = requests.get('{baseUrl}/{serviceId}/geoservices/query', params={{
    'where': '1=1',
    'outFields': '*',
    'f': 'json'
}})
features = response.json()['features']
print(f'Found {{len(features)}} features')"
                },
                OGC_API = new
                {
                    Description = "OGC API Features (Modern, GeoJSON)",
                    GetFeatures = $"curl {baseUrl}/{serviceId}/ogcapi/features",
                    GetCollection = $"curl {baseUrl}/{serviceId}/ogcapi/",
                    JavaScript = $@"
// JavaScript/TypeScript
const response = await fetch('{baseUrl}/{serviceId}/ogcapi/features');
const geojson = await response.json();
console.log(`Found ${{geojson.features.length}} features`);",
                    Python = $@"
# Python
import requests
response = requests.get('{baseUrl}/{serviceId}/ogcapi/features')
geojson = response.json()
print(f'Found {{len(geojson[""features""])}} features')"
                },
                OGC_Legacy = new
                {
                    Description = "OGC Legacy Services (WMS, WFS for desktop GIS)",
                    WMS_GetMap = $"curl \"{baseUrl}/{serviceId}/ogc/wms?SERVICE=WMS&REQUEST=GetMap&LAYERS={serviceId}&FORMAT=image/png\"",
                    WFS_GetFeature = $"curl \"{baseUrl}/{serviceId}/ogc/wfs?SERVICE=WFS&REQUEST=GetFeature&TYPENAME={serviceId}\"",
                    GetCapabilities = $"curl {baseUrl}/{serviceId}/ogc/",
                    QGIS = $@"
// QGIS Data Source Manager
// WMS: {baseUrl}/{serviceId}/ogc/wms
// WFS: {baseUrl}/{serviceId}/ogc/wfs",
                    ArcGIS_Desktop = $@"
// ArcMap/ArcGIS Pro Add Data
// WMS URL: {baseUrl}/{serviceId}/ogc/wms
// WFS URL: {baseUrl}/{serviceId}/ogc/wfs"
                },
                OData = new
                {
                    Description = "OData v4 (Excel, PowerBI compatible)",
                    GetFeatures = $"curl {baseUrl}/{serviceId}/odata/Features",
                    GetMetadata = $"curl {baseUrl}/{serviceId}/odata/$metadata",
                    Filter = $"curl \"{baseUrl}/{serviceId}/odata/Features?$filter=Name eq 'Example'\"",
                    JavaScript = $@"
// JavaScript/TypeScript
const response = await fetch('{baseUrl}/{serviceId}/odata/Features');
const data = await response.json();
console.log(`Found ${{data.value.length}} records`);",
                    PowerBI = $@"
// Power BI / Excel
Data Source: {baseUrl}/{serviceId}/odata/
Table: Features"
                },
                gRPC = new
                {
                    Description = "High-performance streaming API for mobile and native apps",
                    Endpoint = $"grpc://{baseUrl.Replace("https://", "").Replace("http://", "")}/{serviceId}",
                    DotNetMAUI = $@"
// .NET MAUI
var channel = GrpcChannel.ForAddress(""{baseUrl}"");
var client = new FeatureService.FeatureServiceClient(channel);
var features = await client.GetFeaturesAsync(new GetFeaturesRequest {{ ServiceId = ""{serviceId}"" }});",
                    Flutter = $@"
// Flutter/Dart
final channel = ClientChannel('{baseUrl.Replace("https://", "").Replace("http://", "")}');
final stub = FeatureServiceClient(channel);
final response = await stub.getFeatures(GetFeaturesRequest()..serviceId = '{serviceId}');",
                    ReactNative = $@"
// React Native with gRPC
import {{ FeatureServiceClient }} from './proto/features_grpc_web_pb';
const client = new FeatureServiceClient('{baseUrl}');
const features = await client.getFeatures(request);"
                },
                MCP = new
                {
                    Description = "AI-friendly protocol for LLM integration and semantic analysis",
                    QueryFeatures = $"mcp://{baseUrl.Replace("https://", "").Replace("http://", "")}/{serviceId}/tools/query_features",
                    NaturalLanguage = $@"
// Natural language query via MCP
Query: ""Find all parks within 1 mile of downtown""
Tool: query_features
Parameters: {{
  ""query"": ""parks near downtown"",
  ""buffer_distance"": ""1 mile""
}}",
                    AIIntegration = $@"
// Claude/LLM integration
const mcpClient = new MCPClient('{baseUrl}/{serviceId}/mcp/');
const tools = await mcpClient.listTools();
const result = await mcpClient.callTool('spatial_analysis', {{
  operation: 'intersect',
  geometry: geojsonPolygon
}});"
                },
                Tiles = new
                {
                    Description = "Vector tiles for web maps",
                    TileTemplate = $"{baseUrl}/{serviceId}/tiles/{{z}}/{{x}}/{{y}}.mvt",
                    MapboxGL = $@"
// Mapbox GL JS
map.addSource('data', {{
    type: 'vector',
    tiles: ['{baseUrl}/{serviceId}/tiles/{{z}}/{{x}}/{{y}}.mvt']
}});
map.addLayer({{
    id: 'layer',
    type: 'fill',
    source: 'data',
    'source-layer': '{serviceId}'
}});",
                    Leaflet = $@"
// Leaflet with Mapbox Vector Tiles
const vectorTileOptions = {{
    vectorTileLayerName: '{serviceId}',
    interactive: true
}};
L.vectorGrid.protobuf('{baseUrl}/{serviceId}/tiles/{{z}}/{{x}}/{{y}}.mvt', vectorTileOptions).addTo(map);"
                }
            },
            ClientDetection = new
            {
                Description = "Auto-negotiation works by detecting your client",
                Mobile_Apps = $"iOS/Android/Flutter/React Native → gRPC for high performance",
                AI_LLM = $"Claude/GPT/AI tools → MCP for semantic integration",
                QGIS = $"Features → OGC API, Maps → Legacy OGC WMS",
                ArcGIS = $"Automatically uses GeoServices for full feature support",
                Desktop_GIS = $"Features → OGC API, Maps → Legacy OGC WMS/WFS",
                PowerBI = $"Automatically uses OData for seamless integration",
                WebBrowser = $"GeoJSON → OGC API, Images → Legacy OGC WMS"
            },
            NextSteps = new
            {
                Documentation = $"/docs/{serviceId}/",
                PostmanCollection = $"/docs/{serviceId}/postman",
                SDKs = $"/docs/{serviceId}/sdk"
            }
        };

        return Results.Ok(quickStart);
    }

    private static string GenerateServiceDocumentationHtml(string serviceId, string baseUrl)
    {
        var html = new StringBuilder();
        html.AppendLine($@"<!DOCTYPE html>
<html>
<head>
    <title>{serviceId} - API Documentation</title>
    <style>
        body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; line-height: 1.6; margin: 40px; }}
        .protocol {{ border: 1px solid #ddd; margin: 20px 0; padding: 20px; border-radius: 8px; }}
        .protocol h3 {{ margin-top: 0; color: #2c3e50; }}
        pre {{ background: #f4f4f4; padding: 15px; border-radius: 4px; overflow-x: auto; }}
        code {{ background: #f4f4f4; padding: 2px 6px; border-radius: 3px; }}
        .auto-neg {{ background: #e8f5e8; }}
        .copy-btn {{ margin-left: 10px; padding: 5px 10px; background: #007bff; color: white; border: none; border-radius: 3px; cursor: pointer; }}
    </style>
</head>
<body>
    <h1>{serviceId} API Documentation</h1>

    <h2>🚀 Quick Start</h2>
    <div class=""protocol auto-neg"">
        <h3>Auto-Negotiation (Recommended)</h3>
        <p>Smart endpoints that automatically choose the best protocol for your client:</p>
        <pre><code>curl {baseUrl.Replace(":443", "").Replace("http://", "https://")}/{serviceId}/features</code> <button class=""copy-btn"" onclick=""copyToClipboard(this.previousElementSibling.textContent)"">Copy</button></pre>
    </div>

    <h2>📡 Available Protocols</h2>

    <div class=""protocol"">
        <h3>🌐 GeoServices (ArcGIS Compatible)</h3>
        <p>Full-featured API supporting queries, edits, and advanced spatial operations.</p>
        <pre><code>curl ""{baseUrl.Replace(":443", "").Replace("http://", "https://")}/{serviceId}/geoservices/query?where=1=1&outFields=*&f=json""</code> <button class=""copy-btn"" onclick=""copyToClipboard(this.previousElementSibling.textContent)"">Copy</button></pre>
        <p><strong>Best for:</strong> ArcGIS clients, advanced spatial queries, editing operations</p>
    </div>

    <div class=""protocol"">
        <h3>🗺️ OGC API Features</h3>
        <p>Standard OGC API returning GeoJSON for maximum interoperability.</p>
        <pre><code>curl {baseUrl.Replace(":443", "").Replace("http://", "https://")}/{serviceId}/ogc/features</code> <button class=""copy-btn"" onclick=""copyToClipboard(this.previousElementSibling.textContent)"">Copy</button></pre>
        <p><strong>Best for:</strong> QGIS, web mapping, standards compliance</p>
    </div>

    <div class=""protocol"">
        <h3>📊 OData v4</h3>
        <p>Business intelligence friendly API with rich query capabilities.</p>
        <pre><code>curl {baseUrl.Replace(":443", "").Replace("http://", "https://")}/{serviceId}/odata/Features</code> <button class=""copy-btn"" onclick=""copyToClipboard(this.previousElementSibling.textContent)"">Copy</button></pre>
        <p><strong>Best for:</strong> Excel, Power BI, business applications</p>
    </div>

    <div class=""protocol"">
        <h3>🗺️ Vector Tiles</h3>
        <p>High-performance tiles for web mapping applications.</p>
        <pre><code>{baseUrl.Replace(":443", "").Replace("http://", "https://")}/{serviceId}/tiles/{{z}}/{{x}}/{{y}}.mvt</code> <button class=""copy-btn"" onclick=""copyToClipboard(this.textContent)"">Copy</button></pre>
        <p><strong>Best for:</strong> Mapbox GL, Leaflet, web map visualization</p>
    </div>

    <h2>🔧 Tools & Resources</h2>
    <ul>
        <li><a href=""/docs/{serviceId}/quickstart"">📋 Quick Start Examples</a></li>
        <li><a href=""/docs/{serviceId}/postman"">📮 Postman Collection</a></li>
        <li><a href=""/{serviceId}/openapi.json"">📜 OpenAPI Specification</a></li>
        <li><a href=""/docs/{serviceId}/sdk"">📦 SDKs & Client Libraries</a></li>
    </ul>

    <script>
        function copyToClipboard(text) {{
            navigator.clipboard.writeText(text).then(function() {{
                console.log('Copied to clipboard');
            }});
        }}
    </script>
</body>
</html>");

        return html.ToString();
    }

    private static async Task<IResult> HandlePostmanCollection(string serviceId)
    {
        var baseUrl = "{{baseUrl}}"; // Postman variable

        var collection = new
        {
            info = new
            {
                name = $"{serviceId} API Collection",
                description = $"Comprehensive API collection for {serviceId} service with examples for all supported protocols.",
                schema = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
            },
            variable = new[]
            {
                new { key = "baseUrl", value = "https://api.example.com", type = "string" },
                new { key = "serviceId", value = serviceId, type = "string" }
            },
            item = new[]
            {
                new
                {
                    name = "Auto-Negotiation",
                    item = new[]
                    {
                        new
                        {
                            name = "Get Features (Auto)",
                            request = new
                            {
                                method = "GET",
                                header = new[] { new { key = "Accept", value = "application/json" } },
                                url = new
                                {
                                    raw = $"{baseUrl}/{{{{serviceId}}}}/features",
                                    host = new[] { "{{baseUrl}}" },
                                    path = new[] { "{{serviceId}}", "features" }
                                }
                            }
                        }
                    }
                },
                new
                {
                    name = "GeoServices",
                    item = new[]
                    {
                        new
                        {
                            name = "Query Features",
                            request = new
                            {
                                method = "GET",
                                url = new
                                {
                                    raw = $"{baseUrl}/{{{{serviceId}}}}/geoservices/query?where=1=1&outFields=*&f=json",
                                    host = new[] { "{{baseUrl}}" },
                                    path = new[] { "{{serviceId}}", "geoservices", "query" },
                                    query = new[]
                                    {
                                        new { key = "where", value = "1=1" },
                                        new { key = "outFields", value = "*" },
                                        new { key = "f", value = "json" }
                                    }
                                }
                            }
                        }
                    }
                },
                new
                {
                    name = "OGC API Features",
                    item = new[]
                    {
                        new
                        {
                            name = "Get Features",
                            request = new
                            {
                                method = "GET",
                                header = new[] { new { key = "Accept", value = "application/geo+json" } },
                                url = new
                                {
                                    raw = $"{baseUrl}/{{{{serviceId}}}}/ogc/features",
                                    host = new[] { "{{baseUrl}}" },
                                    path = new[] { "{{serviceId}}", "ogc", "features" }
                                }
                            }
                        }
                    }
                },
                new
                {
                    name = "OData",
                    item = new[]
                    {
                        new
                        {
                            name = "Get Features",
                            request = new
                            {
                                method = "GET",
                                url = new
                                {
                                    raw = $"{baseUrl}/{{{{serviceId}}}}/odata/Features",
                                    host = new[] { "{{baseUrl}}" },
                                    path = new[] { "{{serviceId}}", "odata", "Features" }
                                }
                            }
                        }
                    }
                }
            }
        };

        return Results.Ok(collection);
    }

    private static async Task<IResult> HandleSdkInformation(string serviceId)
    {
        var sdkInfo = new
        {
            Service = serviceId,
            AvailableSDKs = new
            {
                JavaScript = new
                {
                    Name = "Honua JS SDK",
                    Installation = "npm install @honua/client",
                    Example = $@"
import {{ HonuaClient }} from '@honua/client';
const client = new HonuaClient('https://api.example.com');
const service = client.service('{serviceId}');
const features = await service.features();"
                },
                Python = new
                {
                    Name = "Honua Python SDK",
                    Installation = "pip install honua-client",
                    Example = $@"
from honua_client import HonuaClient
client = HonuaClient('https://api.example.com')
service = client.service('{serviceId}')
features = service.features()"
                },
                DotNet = new
                {
                    Name = "Honua .NET SDK",
                    Installation = "dotnet add package Honua.Client",
                    Example = $@"
using Honua.Client;
var client = new HonuaClient(""https://api.example.com"");
var service = client.Service(""{serviceId}"");
var features = await service.FeaturesAsync();"
                }
            },
            DirectAccess = new
            {
                Description = "No SDK required - use standard HTTP clients",
                cURL = $"curl https://api.example.com/{serviceId}/features",
                HTTPie = $"http https://api.example.com/{serviceId}/features",
                Postman = $"/docs/{serviceId}/postman"
            }
        };

        return Results.Ok(sdkInfo);
    }

    private static async Task<IResult> HandleOpenApiSpec(string serviceId)
    {
        // TODO: Generate comprehensive OpenAPI spec combining all protocols
        var spec = new
        {
            openapi = "3.0.0",
            info = new
            {
                title = $"{serviceId} Unified API",
                version = "1.0.0",
                description = $"Unified API for {serviceId} supporting multiple geospatial protocols"
            },
            servers = new[]
            {
                new { url = "https://api.example.com", description = "Production server" }
            },
            paths = new Dictionary<string, object>
            {
                [$"/{serviceId}/features"] = new
                {
                    get = new
                    {
                        summary = "Get features (auto-negotiated)",
                        description = "Automatically selects the best protocol based on client capabilities",
                        responses = new Dictionary<string, object>
                        {
                            ["200"] = new { description = "Features retrieved successfully" }
                        }
                    }
                }
            }
        };

        return Results.Ok(spec);
    }

    // Placeholder implementations for protocol-specific docs
    private static Task<IResult> HandleGeoservicesDocumentation(string serviceId) =>
        Task.FromResult(Results.Ok(new { Protocol = "GeoServices", ServiceId = serviceId }));

    private static Task<IResult> HandleOgcApiDocumentation(string serviceId) =>
        Task.FromResult(Results.Ok(new { Protocol = "OGC API Features", ServiceId = serviceId }));

    private static Task<IResult> HandleOgcLegacyDocumentation(string serviceId) =>
        Task.FromResult(Results.Ok(new { Protocol = "OGC Legacy (WMS/WFS)", ServiceId = serviceId }));

    private static Task<IResult> HandleODataDocumentation(string serviceId) =>
        Task.FromResult(Results.Ok(new { Protocol = "OData", ServiceId = serviceId }));
}