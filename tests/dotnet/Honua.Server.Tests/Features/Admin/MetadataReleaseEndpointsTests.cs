// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Metadata.Services;
using Honua.Server.Features.Admin.Models;
using Honua.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Metadata)]
public sealed class MetadataReleaseEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public MetadataReleaseEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IMetadataV2EnvironmentSnapshotReader>();
                services.RemoveAll<IMetadataReleasePackageStore>();
                services.AddSingleton<IMetadataV2EnvironmentSnapshotReader>(
                    new StaticEnvironmentReader(
                        BuildGraph("dev", 41),
                        BuildGraph("staging", 7, includeSchemaField: false)));
                services.AddSingleton<IMetadataReleasePackageStore, CancellationAwareReleasePackageStore>();
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/metadata/environments/{environment}/inventory")]
    public async Task GetInventory_WithFilters_ReturnsRevisionStampedSemanticInventory()
    {
        var response = await _client.GetAsync(
            "/api/v1/admin/metadata/environments/dev/inventory?artifactKind=resource&resourceType=map");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        var inventory = JsonSerializer.Deserialize(
            payload,
            MetadataReleaseJsonContext.Default.MetadataSemanticInventoryResponse);

        inventory.Should().NotBeNull();
        inventory!.Environment.Should().Be("dev");
        inventory.Revision.Should().Be(41);
        inventory.ETag.Should().Be("etag-dev-41");
        inventory.Entries.Should().ContainSingle(entry =>
            entry.SemanticId == "res.parcels" &&
            entry.ArtifactKind == MetadataSemanticArtifactKind.Resource &&
            entry.ResourceType == MetadataV2ResourceType.Map);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/environment-bindings/query")]
    public async Task QueryEnvironmentBindings_WithUnavailableEnvironment_ReturnsSecretSafeStates()
    {
        var body = JsonSerializer.Serialize(
            new MetadataEnvironmentBindingsRequest
            {
                Environments = ["dev", "prod"],
                SemanticIds = ["res.parcels"],
            },
            MetadataReleaseJsonContext.Default.MetadataEnvironmentBindingsRequest);

        var response = await _client.PostAsync(
            "/api/v1/admin/metadata/environment-bindings/query",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("aws-sm://honua/dev/parcels");
        payload.Should().NotContain("super-secret-password");

        var bindings = JsonSerializer.Deserialize(
            payload,
            MetadataReleaseJsonContext.Default.MetadataEnvironmentBindingsResponse);
        bindings.Should().NotBeNull();
        bindings!.Bindings.Should().Contain(binding =>
            binding.Environment == "dev" &&
            binding.State == MetadataEnvironmentBindingState.Bound &&
            binding.Connection!.SecretRef == "aws-sm://honua/dev/parcels");
        bindings.Bindings.Should().Contain(binding =>
            binding.Environment == "prod" &&
            binding.State == MetadataEnvironmentBindingState.EnvironmentUnavailable);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/environment-bindings/query")]
    [Endpoint("POST /api/v1/admin/metadata/release-packages")]
    public async Task ReleaseRequestEndpoints_WithExplicitNullArrays_ReturnBadRequest()
    {
        (string Path, string Body, string ExpectedMessage)[] cases =
        [
            (
                "/api/v1/admin/metadata/environment-bindings/query",
                "{\"environments\":null,\"semanticIds\":[\"res.parcels\"]}",
                "Environments must be an array."),
            (
                "/api/v1/admin/metadata/environment-bindings/query",
                "{\"environments\":[\"dev\"],\"semanticIds\":null}",
                "SemanticIds must be an array."),
            (
                "/api/v1/admin/metadata/release-packages",
                "{\"sourceEnvironment\":\"dev\",\"targetEnvironments\":null,\"semanticIds\":[\"res.parcels\"]}",
                "TargetEnvironments must be an array."),
            (
                "/api/v1/admin/metadata/release-packages",
                "{\"sourceEnvironment\":\"dev\",\"targetEnvironments\":[\"staging\"],\"semanticIds\":null}",
                "SemanticIds must be an array."),
            (
                "/api/v1/admin/metadata/release-packages",
                "{\"sourceEnvironment\":\"dev\",\"targetEnvironments\":[\"staging\"],\"semanticIds\":[\"res.parcels\"],\"provenance\":null}",
                "Provenance must be an array."),
        ];

        foreach (var (path, body, expectedMessage) in cases)
        {
            var response = await _client.PostAsync(
                path,
                new StringContent(body, Encoding.UTF8, "application/json"));

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var payload = await response.Content.ReadAsStringAsync();
            payload.Should().Contain(expectedMessage);
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/release-packages")]
    [Endpoint("GET /api/v1/admin/metadata/release-packages/{packageId}")]
    [Endpoint("GET /api/v1/admin/metadata/release-packages/{packageId}/gitops-manifest")]
    public async Task ReleasePackageEndpoints_CreateReadAndExportGitOpsSafeManifest()
    {
        var body = JsonSerializer.Serialize(
            new CreateMetadataReleasePackageRequest
            {
                Title = "Promote parcels",
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["res.parcels"],
            },
            MetadataReleaseJsonContext.Default.CreateMetadataReleasePackageRequest);

        var createResponse = await _client.PostAsync(
            "/api/v1/admin/metadata/release-packages",
            new StringContent(body, Encoding.UTF8, "application/json"));

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var createPayload = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(
            createPayload,
            MetadataReleaseJsonContext.Default.MetadataReleasePackage);
        created.Should().NotBeNull();
        created!.SourceRevision.Should().Be(41);
        created.Entries.Should().ContainSingle(entry =>
            entry.SemanticId == "res.parcels" &&
            entry.TargetStates.Single().CurrentMetadataRevision == 7);

        var getResponse = await _client.GetAsync(
            $"/api/v1/admin/metadata/release-packages/{created.PackageId:D}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var manifestResponse = await _client.GetAsync(
            $"/api/v1/admin/metadata/release-packages/{created.PackageId:D}/gitops-manifest");
        manifestResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var manifestPayload = await manifestResponse.Content.ReadAsStringAsync();
        manifestPayload.Should().Contain("MetadataReleasePackage");
        manifestPayload.Should().Contain("content-v1");
        manifestPayload.Should().NotContain("super-secret-password");

        var manifest = JsonSerializer.Deserialize(
            manifestPayload,
            MetadataReleaseJsonContext.Default.GitOpsMetadataReleaseManifest);
        manifest.Should().NotBeNull();
        manifest!.Spec.Source.Revision.Should().Be(41);
        manifest.Spec.Targets.Should().ContainSingle(target => target.Environment == "staging");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/release-packages")]
    [Endpoint("GET /api/v1/admin/metadata/release-packages/{packageId}/gitops-manifest")]
    public async Task ReleasePackageEndpoints_WithFieldAndMissingTarget_ExportsSourceFieldIdentity()
    {
        var body = JsonSerializer.Serialize(
            new CreateMetadataReleasePackageRequest
            {
                Title = "Promote parcel APN",
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["field.parcels.apn"],
            },
            MetadataReleaseJsonContext.Default.CreateMetadataReleasePackageRequest);

        var createResponse = await _client.PostAsync(
            "/api/v1/admin/metadata/release-packages",
            new StringContent(body, Encoding.UTF8, "application/json"));

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var createPayload = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize(
            createPayload,
            MetadataReleaseJsonContext.Default.MetadataReleasePackage);
        created.Should().NotBeNull();
        var entry = created!.Entries.Should().ContainSingle().Subject;
        entry.SourceField.Should().NotBeNull();
        entry.SourceField!.ParentResourceId.Should().Be("res.parcels");
        entry.SourceField.FieldName.Should().Be("apn");
        var targetState = entry.TargetStates.Should().ContainSingle().Subject;
        targetState.Environment.Should().Be("staging");
        targetState.BindingState.Should().Be(MetadataEnvironmentBindingState.Missing);

        var manifestResponse = await _client.GetAsync(
            $"/api/v1/admin/metadata/release-packages/{created.PackageId:D}/gitops-manifest");
        manifestResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var manifestPayload = await manifestResponse.Content.ReadAsStringAsync();
        manifestPayload.Should().Contain("\"sourceField\"");
        manifestPayload.Should().Contain("\"parentResourceId\":\"res.parcels\"");
        manifestPayload.Should().Contain("\"fieldName\":\"apn\"");
    }

    [IntegrationTest]
    [Operation(Operations.Validation)]
    [Endpoint("POST /api/v1/admin/metadata/prevalidate")]
    public async Task Prevalidate_WithInlinePackage_ReturnsBlockedCompatibilityReport()
    {
        var body = JsonSerializer.Serialize(
            new MetadataPrevalidateRequest
            {
                ReleasePackage = BuildInlinePackage(),
                TargetEnvironment = "staging",
            },
            MetadataPrevalidationJsonContext.Default.MetadataPrevalidateRequest);

        var response = await _client.PostAsync(
            "/api/v1/admin/metadata/prevalidate",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await ReadPrevalidationReportAsync(response);
        report.Status.Should().Be(MetadataCompatibilityStatus.Blocked);
        report.CanCreatePullRequest.Should().BeFalse();
        report.Findings.Should().Contain(finding =>
            finding.Code == MetadataCompatibilityCode.FieldMissing &&
            finding.AffectedSemanticId == "field.parcels.apn");
    }

    [IntegrationTest]
    [Operation(Operations.Validation)]
    [Endpoint("POST /api/v1/admin/metadata/prevalidate")]
    public async Task Prevalidate_WithPersistedPackageId_ReturnsCompatibilityReport()
    {
        var createBody = JsonSerializer.Serialize(
            new CreateMetadataReleasePackageRequest
            {
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["res.parcels"],
            },
            MetadataReleaseJsonContext.Default.CreateMetadataReleasePackageRequest);
        var createResponse = await _client.PostAsync(
            "/api/v1/admin/metadata/release-packages",
            new StringContent(createBody, Encoding.UTF8, "application/json"));
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = JsonSerializer.Deserialize(
            await createResponse.Content.ReadAsStringAsync(),
            MetadataReleaseJsonContext.Default.MetadataReleasePackage);

        var body = JsonSerializer.Serialize(
            new MetadataPrevalidateRequest
            {
                ReleasePackageId = created!.PackageId,
                TargetEnvironment = "staging",
            },
            MetadataPrevalidationJsonContext.Default.MetadataPrevalidateRequest);

        var response = await _client.PostAsync(
            "/api/v1/admin/metadata/prevalidate",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await ReadPrevalidationReportAsync(response);
        report.ReleasePackageId.Should().Be(created.PackageId);
        report.TargetEnvironment.Should().Be("staging");
        report.Status.Should().Be(MetadataCompatibilityStatus.Blocked);
    }

    [IntegrationTest]
    [Operation(Operations.Validation)]
    [Endpoint("POST /api/v1/admin/metadata/prevalidate")]
    public async Task Prevalidate_WithCoveringDataScript_ReturnsWarningReport()
    {
        var body = JsonSerializer.Serialize(
            new MetadataPrevalidateRequest
            {
                ReleasePackage = BuildInlinePackage(),
                TargetEnvironment = "staging",
                DataScripts =
                [
                    new MetadataDataScriptEntry
                    {
                        ScriptId = "script.add-apn",
                        Kind = "sql",
                        Reversible = true,
                        DeclaredOperations = ["add-field"],
                        BeforeContract = MissingFieldContract(exists: false),
                        AfterContract = MissingFieldContract(exists: true),
                    },
                ],
            },
            MetadataPrevalidationJsonContext.Default.MetadataPrevalidateRequest);

        var response = await _client.PostAsync(
            "/api/v1/admin/metadata/prevalidate",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await ReadPrevalidationReportAsync(response);
        report.Status.Should().Be(MetadataCompatibilityStatus.Warning);
        report.UncoveredErrorCount.Should().Be(0);
        report.Findings.Should().Contain(finding =>
            finding.Code == MetadataCompatibilityCode.FieldMissing &&
            finding.CoverageState == MetadataCompatibilityCoverageState.CoveredByScript &&
            finding.CoveringScriptId == "script.add-apn");
        report.RollbackReadiness.Classification.Should().Be(MetadataRollbackReadinessClassification.ScriptReversible);
    }

    [IntegrationTest]
    [Operation(Operations.Validation)]
    [Endpoint("POST /api/v1/admin/metadata/prevalidate")]
    public async Task Prevalidate_WithInvalidRequestShape_ReturnsBadRequest()
    {
        var response = await _client.PostAsync(
            "/api/v1/admin/metadata/prevalidate",
            new StringContent("{\"targetEnvironment\":\"staging\"}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("Exactly one of ReleasePackageId or ReleasePackage is required.");
    }

    [IntegrationTest]
    [Operation(Operations.Validation)]
    [Endpoint("POST /api/v1/admin/metadata/prevalidate")]
    public async Task Prevalidate_WithNullDataScripts_ReturnsBadRequest()
    {
        var response = await _client.PostAsync(
            "/api/v1/admin/metadata/prevalidate",
            new StringContent(
                "{\"releasePackageId\":\"33333333-3333-3333-3333-333333333333\",\"targetEnvironment\":\"staging\",\"dataScripts\":null}",
                Encoding.UTF8,
                "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("DataScripts must be an array.");
    }

    [IntegrationTest]
    [Operation(Operations.Validation)]
    [Endpoint("POST /api/v1/admin/metadata/prevalidate")]
    public async Task Prevalidate_WithNullDataScriptContractEntries_ReturnsBadRequest()
    {
        (string Body, string ExpectedMessage)[] cases =
        [
            (
                """
                {
                  "releasePackageId": "33333333-3333-3333-3333-333333333333",
                  "targetEnvironment": "staging",
                  "dataScripts": [null]
                }
                """,
                "DataScripts cannot contain null entries."),
            (
                """
                {
                  "releasePackageId": "33333333-3333-3333-3333-333333333333",
                  "targetEnvironment": "staging",
                  "dataScripts": [
                    {
                      "scriptId": "script.bad-resource",
                      "declaredOperations": [],
                      "afterContract": {
                        "resources": [null]
                      }
                    }
                  ]
                }
                """,
                "Data script 'script.bad-resource' afterContract.resources cannot contain null entries."),
            (
                """
                {
                  "releasePackageId": "33333333-3333-3333-3333-333333333333",
                  "targetEnvironment": "staging",
                  "dataScripts": [
                    {
                      "scriptId": "script.bad-field",
                      "declaredOperations": [],
                      "afterContract": {
                        "resources": [
                          {
                            "semanticId": "res.parcels",
                            "fields": [null]
                          }
                        ]
                      }
                    }
                  ]
                }
                """,
                "Data script 'script.bad-field' afterContract.resources.fields cannot contain null entries."),
            (
                """
                {
                  "releasePackageId": "33333333-3333-3333-3333-333333333333",
                  "targetEnvironment": "staging",
                  "dataScripts": [
                    {
                      "scriptId": "script.bad-operations",
                      "declaredOperations": null
                    }
                  ]
                }
                """,
                "Data script 'script.bad-operations' declaredOperations must be an array."),
            (
                """
                {
                  "releasePackageId": "33333333-3333-3333-3333-333333333333",
                  "targetEnvironment": "staging",
                  "dataScripts": [
                    {
                      "scriptId": "script.bad-resource-capabilities",
                      "declaredOperations": [],
                      "afterContract": {
                        "resources": [
                          {
                            "semanticId": "svc.features",
                            "semanticKind": "service",
                            "capabilities": null
                          }
                        ]
                      }
                    }
                  ]
                }
                """,
                "Data script 'script.bad-resource-capabilities' afterContract.resources.capabilities must be an array."),
            (
                """
                {
                  "releasePackageId": "33333333-3333-3333-3333-333333333333",
                  "targetEnvironment": "staging",
                  "dataScripts": [
                    {
                      "scriptId": "script.bad-publication-formats",
                      "declaredOperations": [],
                      "afterContract": {
                        "resources": [
                          {
                            "semanticId": "pub.parcels",
                            "semanticKind": "publication",
                            "supportedFormats": null
                          }
                        ]
                      }
                    }
                  ]
                }
                """,
                "Data script 'script.bad-publication-formats' afterContract.resources.supportedFormats must be an array."),
            (
                """
                {
                  "releasePackageId": "33333333-3333-3333-3333-333333333333",
                  "targetEnvironment": "staging",
                  "dataScripts": [
                    {
                      "scriptId": "script.bad-field-roles",
                      "declaredOperations": [],
                      "afterContract": {
                        "resources": [
                          {
                            "semanticId": "res.parcels",
                            "fields": [
                              {
                                "name": "apn",
                                "semanticRoles": null
                              }
                            ]
                          }
                        ]
                      }
                    }
                  ]
                }
                """,
                "Data script 'script.bad-field-roles' afterContract.resources.fields.semanticRoles must be an array."),
            (
                """
                {
                  "releasePackageId": "33333333-3333-3333-3333-333333333333",
                  "targetEnvironment": "staging",
                  "dataScripts": [
                    {
                      "scriptId": "script.bad-storage-capabilities",
                      "declaredOperations": [],
                      "afterContract": {
                        "resources": [
                          {
                            "semanticId": "res.parcels",
                            "storage": {
                              "capabilities": null
                            }
                          }
                        ]
                      }
                    }
                  ]
                }
                """,
                "Data script 'script.bad-storage-capabilities' afterContract.resources.storage.capabilities must be an array."),
        ];

        foreach (var (body, expectedMessage) in cases)
        {
            var response = await _client.PostAsync(
                "/api/v1/admin/metadata/prevalidate",
                new StringContent(body, Encoding.UTF8, "application/json"));

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var payload = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(payload);
            document.RootElement.GetProperty("detail").GetString().Should().Contain(expectedMessage);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Validation)]
    [Endpoint("POST /api/v1/admin/metadata/prevalidate")]
    public async Task Prevalidate_WithOmittedDataScriptCollections_TreatsCollectionsAsEmpty()
    {
        const string body =
            """
            {
              "releasePackageId": "33333333-3333-3333-3333-333333333333",
              "targetEnvironment": "staging",
              "dataScripts": [
                {
                  "scriptId": "script.omitted-collections",
                  "afterContract": {
                    "resources": [
                      {
                        "semanticId": "res.parcels"
                      }
                    ]
                  }
                }
              ]
            }
            """;

        var response = await _client.PostAsync(
            "/api/v1/admin/metadata/prevalidate",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await ReadPrevalidationReportAsync(response);
        report.Status.Should().Be(MetadataCompatibilityStatus.Unknown);
    }

    [IntegrationTest]
    [Operation(Operations.Validation)]
    [Endpoint("POST /api/v1/admin/metadata/prevalidate")]
    public async Task Prevalidate_WithUnavailableTarget_ReturnsUnknownReport()
    {
        var body = JsonSerializer.Serialize(
            new MetadataPrevalidateRequest
            {
                ReleasePackage = BuildInlinePackage(),
                TargetEnvironment = "prod",
            },
            MetadataPrevalidationJsonContext.Default.MetadataPrevalidateRequest);

        var response = await _client.PostAsync(
            "/api/v1/admin/metadata/prevalidate",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await ReadPrevalidationReportAsync(response);
        report.Status.Should().Be(MetadataCompatibilityStatus.Unknown);
        report.CanPromote.Should().BeFalse();
        report.Findings.Should().ContainSingle(finding => finding.Code == MetadataCompatibilityCode.StateUnavailable);
    }

    [IntegrationTest]
    [Operation(Operations.Validation)]
    [Endpoint("POST /api/v1/admin/metadata/prevalidate")]
    public async Task Prevalidate_WithoutAdminAuth_ReturnsUnauthorized()
    {
        using var unauthorizedClient = _fixture.CreateClient();
        var body = JsonSerializer.Serialize(
            new MetadataPrevalidateRequest
            {
                ReleasePackage = BuildInlinePackage(),
                TargetEnvironment = "staging",
            },
            MetadataPrevalidationJsonContext.Default.MetadataPrevalidateRequest);

        var response = await unauthorizedClient.PostAsync(
            "/api/v1/admin/metadata/prevalidate",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/release-packages")]
    public async Task CreateReleasePackage_WithRepeatedGeneratedTitleKeys_ReturnsCreatedPackages()
    {
        var body = JsonSerializer.Serialize(
            new CreateMetadataReleasePackageRequest
            {
                Title = "Promote parcels",
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["res.parcels"],
            },
            MetadataReleaseJsonContext.Default.CreateMetadataReleasePackageRequest);

        var firstResponse = await _client.PostAsync(
            "/api/v1/admin/metadata/release-packages",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var secondResponse = await _client.PostAsync(
            "/api/v1/admin/metadata/release-packages",
            new StringContent(body, Encoding.UTF8, "application/json"));

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var firstPayload = await firstResponse.Content.ReadAsStringAsync();
        var secondPayload = await secondResponse.Content.ReadAsStringAsync();
        var first = JsonSerializer.Deserialize(
            firstPayload,
            MetadataReleaseJsonContext.Default.MetadataReleasePackage);
        var second = JsonSerializer.Deserialize(
            secondPayload,
            MetadataReleaseJsonContext.Default.MetadataReleasePackage);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first!.Metadata.Name.Should().StartWith("promote-parcels-");
        second!.Metadata.Name.Should().StartWith("promote-parcels-");
        second.Metadata.Name.Should().NotBe(first.Metadata.Name);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/release-packages")]
    public async Task CreateReleasePackage_WithDuplicateDefaultNamespacePackageKey_ReturnsConflict()
    {
        var body = JsonSerializer.Serialize(
            new CreateMetadataReleasePackageRequest
            {
                PackageKey = "duplicate-package-key",
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["res.parcels"],
            },
            MetadataReleaseJsonContext.Default.CreateMetadataReleasePackageRequest);
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var firstResponse = await _client.PostAsync(
            "/api/v1/admin/metadata/release-packages",
            content);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var secondResponse = await _client.PostAsync(
            "/api/v1/admin/metadata/release-packages",
            new StringContent(body, Encoding.UTF8, "application/json"));

        secondResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var payload = await secondResponse.Content.ReadAsStringAsync();
        payload.Should().Contain("Metadata release package conflicts with an existing package.");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/release-packages")]
    public async Task CreateReleasePackage_WhenStoreCancellationOccurs_DoesNotMapToPackageCreateFailure()
    {
        var body = JsonSerializer.Serialize(
            new CreateMetadataReleasePackageRequest
            {
                PackageKey = "cancelled-package",
                SourceEnvironment = "dev",
                TargetEnvironments = ["staging"],
                SemanticIds = ["res.parcels"],
            },
            MetadataReleaseJsonContext.Default.CreateMetadataReleasePackageRequest);

        var response = await _client.PostAsync(
            "/api/v1/admin/metadata/release-packages",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.RequestTimeout);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotContain("Metadata release package could not be created.");
    }

    private static MetadataV2Graph BuildGraph(string environment, long revision, bool includeSchemaField = true)
    {
        var passwordOption = JsonSerializer.Deserialize<JsonElement>("\"super-secret-password\"");
        return new MetadataV2Graph
        {
            Environment = environment,
            Revision = revision,
            GeneratedAt = DateTimeOffset.UtcNow,
            Resources =
            [
                new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata
                    {
                        Id = "res.parcels",
                        Name = "parcels",
                        Title = "Parcels",
                        Annotations = new Dictionary<string, string>
                        {
                            ["honua.io/content-version-id"] = "content-v1",
                        },
                    },
                    Type = MetadataV2ResourceType.Map,
                    StorageBindingIds = ["storage.parcels"],
                    PrimaryStorageBindingId = "storage.parcels",
                    SchemaFields = BuildSchemaFields(includeSchemaField),
                },
            ],
            Connections =
            [
                new MetadataV2Connection
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "conn.parcels", Name = "parcels-db" },
                    Type = MetadataV2ConnectionType.Database,
                    Provider = "postgres",
                    SecretRef = "aws-sm://honua/dev/parcels",
                    Options = new Dictionary<string, JsonElement>
                    {
                        ["password"] = passwordOption,
                    },
                },
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "storage.parcels", Name = "storage.parcels" },
                    ResourceId = "res.parcels",
                    ConnectionId = "conn.parcels",
                    StorageType = MetadataV2StorageType.RelationalTable,
                    Locator = $"{environment}_schema.parcels",
                },
            ],
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "svc.features", Name = "features" },
                    ServiceType = MetadataV2ServiceType.OgcApiFeatures,
                    Route = $"/{environment}/ogc/features",
                    PublicationIds = ["pub.parcels"],
                },
            ],
            Publications =
            [
                new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub.parcels", Name = "parcels" },
                    ResourceId = "res.parcels",
                    ServiceId = "svc.features",
                    StorageBindingId = "storage.parcels",
                    PublicationType = MetadataV2PublicationType.OgcCollection,
                    Path = "parcels",
                    ServiceLocalId = "parcels",
                },
            ],
        };
    }

    private static async Task<MetadataCompatibilityReport> ReadPrevalidationReportAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        var envelope = JsonSerializer.Deserialize(
            payload,
            MetadataPrevalidationJsonContext.Default.ApiResponseMetadataCompatibilityReport);
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        envelope.Data.Should().NotBeNull();
        return envelope.Data!;
    }

    private static MetadataReleasePackage BuildInlinePackage()
        => new()
        {
            PackageId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "pkg.parcels",
                Name = "promote-parcels",
                Title = "Promote parcels",
            },
            SourceEnvironment = "dev",
            SourceRevision = 41,
            SourceEtag = "etag-dev-41",
            TargetEnvironments = ["staging"],
            Entries =
            [
                new MetadataReleaseEntry
                {
                    SemanticId = "res.parcels",
                    ArtifactKind = MetadataSemanticArtifactKind.Resource,
                    ResourceType = MetadataV2ResourceType.Map,
                    DesiredMetadataRevision = 41,
                },
            ],
            CreatedBy = "tester",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static MetadataDataScriptContract MissingFieldContract(bool exists)
        => new()
        {
            Resources =
            [
                new MetadataScriptResourceContract
                {
                    SemanticId = "res.parcels",
                    SemanticKind = MetadataSemanticArtifactKind.Resource,
                    Exists = true,
                    Fields =
                    [
                        new MetadataScriptFieldContract
                        {
                            SemanticId = "field.parcels.apn",
                            Name = "apn",
                            Exists = exists,
                            Type = "string",
                            Nullable = false,
                        },
                    ],
                },
            ],
        };

    private static MetadataV2Field[] BuildSchemaFields(bool includeSchemaField)
    {
        if (!includeSchemaField)
        {
            return Array.Empty<MetadataV2Field>();
        }

        return
        [
            new MetadataV2Field
            {
                SemanticId = "field.parcels.apn",
                Name = "apn",
                Type = MetadataV2FieldType.String,
            },
        ];
    }

    private sealed class StaticEnvironmentReader : IMetadataV2EnvironmentSnapshotReader
    {
        private readonly Dictionary<string, MetadataV2GraphSnapshot> _snapshots;

        public StaticEnvironmentReader(params MetadataV2Graph[] graphs)
        {
            _snapshots = graphs.ToDictionary(
                static graph => graph.Environment,
                static graph => new MetadataV2GraphSnapshot(
                    graph,
                    $"etag-{graph.Environment}-{graph.Revision}",
                    DateTimeOffset.UtcNow),
                StringComparer.OrdinalIgnoreCase);
        }

        public ValueTask<MetadataV2GraphSnapshot?> GetCurrentAsync(
            string environment,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(
                _snapshots.TryGetValue(environment, out var snapshot) ? snapshot : null);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
            string environment,
            long revision,
            CancellationToken cancellationToken = default)
        {
            var snapshot = _snapshots.TryGetValue(environment, out var current) && current.Revision == revision
                ? current
                : null;
            return ValueTask.FromResult(snapshot);
        }

        public async IAsyncEnumerable<MetadataV2EnvironmentRevision> ListCurrentRevisionsAsync(
            IReadOnlyList<string> environments,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var environment in environments)
            {
                if (_snapshots.TryGetValue(environment, out var snapshot))
                {
                    yield return new MetadataV2EnvironmentRevision
                    {
                        Environment = snapshot.Graph.Environment,
                        Revision = snapshot.Revision,
                        ETag = snapshot.Etag,
                        ActivatedAt = snapshot.LoadedAt,
                    };
                }
            }
        }
    }

    private sealed class CancellationAwareReleasePackageStore : IMetadataReleasePackageStore
    {
        private readonly InMemoryMetadataReleasePackageStore _inner = new();

        public Task<MetadataReleasePackage> CreateAsync(
            MetadataReleasePackage package,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(package.Metadata.Name, "cancelled-package", StringComparison.Ordinal))
            {
                throw new OperationCanceledException("Simulated package store cancellation.");
            }

            return _inner.CreateAsync(package, cancellationToken);
        }

        public Task<MetadataReleasePackage?> GetAsync(
            Guid packageId,
            CancellationToken cancellationToken = default)
            => _inner.GetAsync(packageId, cancellationToken);
    }
}
