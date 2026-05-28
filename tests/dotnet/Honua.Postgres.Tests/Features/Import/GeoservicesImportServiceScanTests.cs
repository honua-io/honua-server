// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Postgres.Features.Migration;
using Honua.Postgres.Features.FileImport;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class GeoservicesImportServiceScanTests
{
    [Fact]
    public async Task ScanSourceAsync_ArcGisWebMercatorAlias_NormalizesToEpsg3857()
    {
        var service = CreateService(
            new GeoservicesScanHandler(
                serviceDescription: "Parcel Viewer",
                spatialReferenceJson: """{"wkid":102100}"""));

        var artifact = await service.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
            TimeoutSeconds = 5
        });

        var spatialReference = artifact.Resources.Should().ContainSingle().Subject.SpatialReferences.Should().ContainSingle().Subject;
        spatialReference.SourceValue.Should().Be("WKID:102100");
        spatialReference.Srid.Should().Be(3857);
        spatialReference.CrsUri.Should().Be("http://www.opengis.net/def/crs/EPSG/0/3857");
    }

    [Fact]
    public async Task ScanSourceAsync_UnknownArcGisWkid_DoesNotInventEpsgUri()
    {
        var service = CreateService(
            new GeoservicesScanHandler(
                serviceDescription: "Parcel Viewer",
                spatialReferenceJson: """{"wkid":54004}"""));

        var artifact = await service.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
            TimeoutSeconds = 5
        });

        var spatialReference = artifact.Resources.Should().ContainSingle().Subject.SpatialReferences.Should().ContainSingle().Subject;
        spatialReference.SourceValue.Should().Be("WKID:54004");
        spatialReference.CrsUri.Should().BeNull();
    }

    [Fact]
    public async Task ScanSourceAsync_ServiceDescriptionChanges_DoNotChurnArtifactIds()
    {
        const string rendererUrl = "https://user:secret@example.com/assets/symbol.png?token=abc#frag";

        var firstService = CreateService(
            new GeoservicesScanHandler(
                serviceDescription: "Parcel Viewer A",
                spatialReferenceJson: """{"wkid":3857}""",
                rendererUrl: rendererUrl));
        var secondService = CreateService(
            new GeoservicesScanHandler(
                serviceDescription: "Parcel Viewer B",
                spatialReferenceJson: """{"wkid":3857}""",
                rendererUrl: rendererUrl));

        var firstArtifact = await firstService.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
            TimeoutSeconds = 5
        });
        var secondArtifact = await secondService.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
            TimeoutSeconds = 5
        });

        firstArtifact.Source.DisplayName.Should().Be("Parcel Viewer A");
        secondArtifact.Source.DisplayName.Should().Be("Parcel Viewer B");
        firstArtifact.Containers.Should().ContainSingle().Which.Title.Should().Be("Parcel Viewer A");
        secondArtifact.Containers.Should().ContainSingle().Which.Title.Should().Be("Parcel Viewer B");

        firstArtifact.Containers.Should().ContainSingle().Which.Id.Should().Be("service:Parcels");
        secondArtifact.Containers.Should().ContainSingle().Which.Id.Should().Be("service:Parcels");
        firstArtifact.Resources.Should().ContainSingle().Which.Id.Should().Be(secondArtifact.Resources.Single().Id);
        firstArtifact.Styles.Should().ContainSingle().Which.Id.Should().Be(secondArtifact.Styles.Single().Id);
        firstArtifact.ExternalDependencies.Should().ContainSingle().Which.Id.Should().Be(secondArtifact.ExternalDependencies.Single().Id);
    }

    [Fact]
    public async Task ScanSourceAsync_RendererExternalUrls_AreSanitizedAndHashed()
    {
        var service = CreateService(
            new GeoservicesScanHandler(
                serviceDescription: "Parcel Viewer",
                spatialReferenceJson: """{"wkid":3857}""",
                rendererUrl: "https://user:secret@example.com/assets/symbol.png?token=abc#frag"));

        var artifact = await service.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
            TimeoutSeconds = 5
        });

        var dependency = artifact.ExternalDependencies.Should().ContainSingle().Subject;
        dependency.Kind.Should().Be("external-symbol");
        dependency.Address.Should().Be("https://example.com/assets/symbol.png");
        dependency.Id.Should().MatchRegex("^renderer:Parcels:0:external:[0-9a-f]{16}$");
        dependency.Id.Should().NotContain("https://");
        dependency.Id.Should().NotContain("secret");
        dependency.Id.Should().NotContain("token");
        artifact.Styles.Should().ContainSingle().Which.ExternalDependencyIds.Should().ContainSingle(dependency.Id);
    }

    [Fact]
    public async Task ScanSourceAsync_WithResolvedToken_AppendsTokenAndReportsTokenPosture()
    {
        const string accessToken = "resolved-arcgis-token";
        var handler = new GeoservicesScanHandler(
            serviceDescription: "Parcel Viewer",
            spatialReferenceJson: """{"wkid":3857}""",
            expectedToken: accessToken);
        var service = CreateService(handler);

        var artifact = await service.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
            TimeoutSeconds = 5,
            Credentials = new GeoservicesCredentialDescriptor
            {
                Mode = GeoservicesAuthenticationModes.Token,
                AccessToken = accessToken,
                AccessTokenSecretReference = "env:ARCGIS_TOKEN"
            }
        });

        handler.RequestCount.Should().Be(3);
        artifact.AuthPosture.Mode.Should().Be(GeoservicesAuthenticationModes.Token);
        artifact.AuthPosture.CredentialsSupplied.Should().BeTrue();
        artifact.AuthPosture.AccessConfirmed.Should().BeTrue();

        var artifactJson = JsonSerializer.Serialize(artifact);
        artifactJson.Should().NotContain(accessToken);
        artifactJson.Should().NotContain("env:ARCGIS_TOKEN");
    }

    [Fact]
    public async Task ScanSourceAsync_WithExpiredTokenError_ReportsExpiredTokenPostureWithoutSecretValues()
    {
        const string accessToken = "expired-arcgis-token";
        var service = CreateService(new ArcGisErrorHandler(498, "Invalid token."));

        var artifact = await service.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
            TimeoutSeconds = 5,
            Credentials = new GeoservicesCredentialDescriptor
            {
                Mode = GeoservicesAuthenticationModes.Token,
                AccessToken = accessToken,
                AccessTokenSecretReference = "env:ARCGIS_TOKEN"
            }
        });

        artifact.AuthPosture.Mode.Should().Be(GeoservicesAuthenticationModes.ExpiredToken);
        artifact.AuthPosture.CredentialsSupplied.Should().BeTrue();
        artifact.AuthPosture.AccessConfirmed.Should().BeFalse();
        artifact.OverallCompatibility.Code.Should().Be(ImportCompatibilityCodes.ArcGisTokenExpired);

        var artifactJson = JsonSerializer.Serialize(artifact);
        artifactJson.Should().NotContain(accessToken);
        artifactJson.Should().NotContain("env:ARCGIS_TOKEN");
    }

    [Fact]
    public async Task ScanSourceAsync_WithForbiddenError_ReportsDeniedPosture()
    {
        var service = CreateService(new ArcGisErrorHandler(403, "Forbidden."));

        var artifact = await service.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
            TimeoutSeconds = 5,
            Credentials = new GeoservicesCredentialDescriptor
            {
                Mode = GeoservicesAuthenticationModes.Basic,
                Username = "scanner",
                Password = "resolved-basic-secret",
                PasswordSecretReference = "env:ARCGIS_PASSWORD"
            }
        });

        artifact.AuthPosture.Mode.Should().Be(GeoservicesAuthenticationModes.Denied);
        artifact.AuthPosture.CredentialsSupplied.Should().BeTrue();
        artifact.AuthPosture.AccessConfirmed.Should().BeFalse();
        artifact.OverallCompatibility.Code.Should().Be(ImportCompatibilityCodes.ArcGisAccessDenied);
    }

    [Fact]
    public async Task ScanSourceAsync_MissingAttachmentMetadata_PreservesUnknownState()
    {
        var service = CreateService(
            new GeoservicesScanHandler(
                serviceDescription: "Parcel Viewer",
                spatialReferenceJson: """{"wkid":3857}"""));

        var artifact = await service.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
            TimeoutSeconds = 5
        });

        var resource = artifact.Resources.Should().ContainSingle().Subject;
        resource.HasAttachments.Should().BeNull();
        resource.Compatibility.Warnings.Should().NotContain(warning => warning.Contains("Attachments", StringComparison.Ordinal));
        artifact.ExternalDependencies.Should().NotContain(dependency => dependency.Kind == "attachments");
    }

    [Fact]
    public async Task ScanSourceAsync_ProjectedWkt_UsesProjectedLengthUnit()
    {
        var service = CreateService(
            new GeoservicesScanHandler(
                serviceDescription: "Parcel Viewer",
                spatialReferenceJson: JsonSerializer.Serialize(new
                {
                    wkt = SpatialReference.WebMercator.Wkt
                })));

        var artifact = await service.ScanSourceAsync(new GeoservicesDiscoveryRequest
        {
            ServiceUrl = "https://example.com/arcgis/rest/services/Parcels/FeatureServer",
            TimeoutSeconds = 5
        });

        var spatialReference = artifact.Resources.Should().ContainSingle().Subject.SpatialReferences.Should().ContainSingle().Subject;
        spatialReference.Unit.Should().Be("metre");
        spatialReference.IsGeographic.Should().BeFalse();
    }

    private static GeoservicesImportService CreateService(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var restClient = new ArcGisRestClient(
            httpClient,
            NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var connectionProvider = new Mock<IDatabaseConnectionProvider>(MockBehavior.Strict);
        var crsRegistry = new Mock<ICrsRegistry>(MockBehavior.Strict);

        crsRegistry.Setup(registry => registry.ResolveBySridAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((int srid, CancellationToken _) => new ValueTask<CrsDefinition?>(
                srid switch
                {
                    3857 => new CrsDefinition("http://www.opengis.net/def/crs/EPSG/0/3857", 3857, AxisOrder.EastNorth, false),
                    _ => null
                }));

        return new GeoservicesImportService(
            restClient,
            connectionProvider.Object,
            crsRegistry.Object,
            NullLogger<GeoservicesImportService>.Instance);
    }

    private sealed class GeoservicesScanHandler : HttpMessageHandler
    {
        private readonly string _serviceDescription;
        private readonly string? _rendererUrl;
        private readonly string? _expectedToken;
        private readonly JsonElement _spatialReference;

        public GeoservicesScanHandler(
            string serviceDescription,
            string spatialReferenceJson,
            string? rendererUrl = null,
            string? expectedToken = null)
        {
            _serviceDescription = serviceDescription;
            _rendererUrl = rendererUrl;
            _expectedToken = expectedToken;
            _spatialReference = JsonDocument.Parse(spatialReferenceJson).RootElement.Clone();
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(_expectedToken))
            {
                pathAndQuery.Should().Contain($"token={Uri.EscapeDataString(_expectedToken)}");
                pathAndQuery = pathAndQuery.Replace($"&token={Uri.EscapeDataString(_expectedToken)}", string.Empty, StringComparison.Ordinal);
            }

            var rendererJson = _rendererUrl == null
                ? JsonSerializer.Serialize(new { type = "simple" })
                : JsonSerializer.Serialize(new
                {
                    type = "simple",
                    symbol = new
                    {
                        type = "esriPMS",
                        url = _rendererUrl
                    }
                });
            var payload = pathAndQuery switch
            {
                "/arcgis/rest/services/Parcels/FeatureServer?f=json" => JsonSerializer.Serialize(new
                {
                    currentVersion = 11.2,
                    serviceDescription = _serviceDescription,
                    capabilities = "Query",
                    layers = new[]
                    {
                        new
                        {
                            id = 0,
                            name = "Parcels"
                        }
                    }
                }),
                "/arcgis/rest/services/Parcels/FeatureServer/0?f=json" => $$"""
                    {
                      "id": 0,
                      "name": "Parcels",
                      "description": "Parcel polygons",
                      "geometryType": "esriGeometryPolygon",
                      "capabilities": "Query",
                      "spatialReference": {{_spatialReference.GetRawText()}},
                      "drawingInfo": {
                        "renderer": {{rendererJson}}
                      }
                    }
                    """,
                "/arcgis/rest/services/Parcels/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json" => """{"count":42}""",
                _ => throw new InvalidOperationException($"Unexpected ArcGIS request path: {pathAndQuery}")
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ArcGisErrorHandler(int code, string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.Serialize(new
            {
                error = new
                {
                    code,
                    message
                }
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }
}
