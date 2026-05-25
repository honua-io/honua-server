// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Shared.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.Forms;

[Protocol(Protocols.Admin)]
public sealed class FormPackageValidatorTests
{
    [UnitTest]
    public async Task ValidateForPublish_WithValidPackage_ReturnsValidResult()
    {
        var validator = CreateValidator();

        var result = await validator.ValidateForPublishAsync(CreatePackage());

        result.IsValid.Should().BeTrue();
        result.Issues.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ValidateForPublish_WithSchemaAndPolicyDrift_ReturnsExpectedIssueCodes()
    {
        var validator = CreateValidator(serviceCapabilities: ["Query", "Extract"]);
        var package = new FormPackageDocument
        {
            Title = "Inspection Form",
            Target = new FormTargetDefinition
            {
                ServiceId = "test",
                LayerId = 0
            },
            Fields =
            [
                new FormFieldDefinition
                {
                    FieldId = "name",
                    Label = "Name",
                    Type = "text",
                    TargetField = "name",
                    Required = true,
                    SectionId = "missing-section",
                    Validation =
                    [
                        new FormValidationRule
                        {
                            Type = "maxLength",
                            Parameters = Json("""{"value":300}""")
                        }
                    ],
                    Visibility = new FormConditionalRule
                    {
                        DependsOnFieldId = "name",
                        Operator = "near"
                    }
                },
                new FormFieldDefinition
                {
                    FieldId = "name-copy",
                    Label = "Name copy",
                    Type = "text",
                    TargetField = "name",
                    Required = true,
                    SectionId = "main"
                },
                new FormFieldDefinition
                {
                    FieldId = "category",
                    Label = "Category",
                    Type = "choice",
                    TargetField = "category",
                    Domain = new FormFieldDomainDefinition
                    {
                        Type = "codedValue",
                        Choices =
                        [
                            new FormDomainChoice { Code = Json("\"a\""), Label = "A" },
                            new FormDomainChoice { Code = Json("\"a\""), Label = "Duplicate A" }
                        ]
                    }
                },
                new FormFieldDefinition
                {
                    FieldId = "photo",
                    Label = "Photo",
                    Type = "attachment",
                    Required = true
                }
            ],
            Sections =
            [
                new FormSectionDefinition
                {
                    SectionId = "main",
                    Label = "Main",
                    FieldIds = ["name", "missing-field"]
                },
                new FormSectionDefinition
                {
                    SectionId = "main",
                    Label = "Duplicate",
                    FieldIds = []
                }
            ],
            SubmitPolicy = new FormSubmitPolicy
            {
                AllowedOperations =
                [
                    FormSubmissionOperations.Create,
                    FormSubmissionOperations.Update,
                    FormSubmissionOperations.Delete
                ],
                AllowAttachments = false
            },
            AttachmentPolicy = new FormAttachmentPolicy
            {
                Enabled = true,
                MaxAttachmentsPerSubmission = 99,
                MaxAttachmentBytes = 99_000_000,
                MaxTotalBytes = 99_000_000,
                AllowedContentTypes = ["application/x-msdownload"],
                RequireExifStripping = true,
                Fields =
                [
                    new FormFieldAttachmentPolicy
                    {
                        FieldId = "missing-attachment",
                        Required = true
                    }
                ]
            },
            PrivacyPolicy = new FormPrivacyPolicy
            {
                PrivateFieldIds = ["missing-private-field"],
                RequiredTransformations = ["serverRedact"],
                RetentionDays = 0
            },
            OfflinePolicy = new FormOfflinePolicy
            {
                Enabled = true,
                PreferredTransports = ["custom-sync"],
                ReplicaTransportEnabled = false,
                FieldCollectionTransportEnabled = false
            }
        };

        var result = await validator.ValidateForPublishAsync(package);

        result.IsValid.Should().BeFalse();
        result.Issues.Select(static issue => issue.Code).Should().Contain(
            "targetOperationUnsupported",
            "sectionNotFound",
            "sectionIdDuplicate",
            "sectionFieldNotFound",
            "validationMaxLengthExceedsTarget",
            "visibilityOperatorUnsupported",
            "visibilityCycle",
            "domainChoiceDuplicate",
            "attachmentsDisallowedBySubmitPolicy",
            "attachmentCountLimitInvalid",
            "attachmentSizeLimitInvalid",
            "attachmentTotalLimitInvalid",
            "attachmentContentTypeNotAllowed",
            "attachmentTransformUnsupported",
            "attachmentPolicyFieldNotFound",
            "privacyFieldNotFound",
            "privacyTransformUnsupported",
            "privacyRetentionInvalid",
            "offlineTransportUnsupported",
            "offlineTransportRequired");
    }

    [UnitTest]
    public async Task ValidateSubmission_WithPublishedPackage_EnforcesOperationGeometryAndAttachmentPolicy()
    {
        var validator = CreateValidator();
        var packageVersion = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = CreatePackage(includeAttachment: true)
        };
        var request = new FormSubmissionRequest
        {
            Operation = FormSubmissionOperations.Update,
            Values = new Dictionary<string, JsonElement>
            {
                ["name"] = Json("\"Inspection\"")
            },
            Geometry = Json("""{"x":-157.8583,"y":21.3069,"spatialReference":{"wkid":3857}}""")
        };

        var result = await validator.ValidateSubmissionAsync(packageVersion, request);

        result.IsValid.Should().BeFalse();
        result.Issues.Select(static issue => issue.Code).Should().Contain(
            "operationNotAllowed",
            "targetFeatureIdRequired",
            "geometrySridMismatch",
            "requiredAttachmentMissing");
    }

    [UnitTest]
    public async Task ValidateSubmission_WithMixedCaseOperations_UsesCaseInsensitiveOperationBranches()
    {
        var validator = CreateValidator();
        var packageVersion = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = CreatePackage(allowedOperations:
            [
                FormSubmissionOperations.Create,
                FormSubmissionOperations.Update,
                FormSubmissionOperations.Delete
            ])
        };
        var update = CreateSubmissionRequest("Update");
        var delete = CreateSubmissionRequest(
            "Delete",
            values: new Dictionary<string, JsonElement>(),
            includeGeometry: false);

        var updateResult = await validator.ValidateSubmissionAsync(packageVersion, update);
        var deleteResult = await validator.ValidateSubmissionAsync(packageVersion, delete);

        updateResult.Issues.Select(static issue => issue.Code).Should().Contain("targetFeatureIdRequired");
        deleteResult.Issues.Select(static issue => issue.Code).Should().Contain("targetFeatureIdRequired");
        deleteResult.Issues.Select(static issue => issue.Code).Should().NotContain([
            "requiredFieldMissing",
            "geometryRequired"
        ]);
    }

    [UnitTest]
    public async Task ValidateSubmission_WithNonNumericPointCoordinate_ReturnsValidationIssue()
    {
        var validator = CreateValidator();
        var packageVersion = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = CreatePackage()
        };
        var request = CreateSubmissionRequest(
            FormSubmissionOperations.Create,
            geometry: Json("""{"x":"abc","y":"21.3069","spatialReference":{"wkid":4326}}"""));

        var result = await validator.ValidateSubmissionAsync(packageVersion, request);

        result.IsValid.Should().BeFalse();
        result.Issues.Select(static issue => issue.Code).Should().Contain("geometryCoordinateInvalid");
    }

    [UnitTest]
    public async Task ValidateSubmission_WithArcGisWebMercatorLatestWkid_AcceptsLayerSrid()
    {
        var validator = CreateValidator(spatialReference: SpatialReference.WebMercator);
        var packageVersion = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = CreatePackage()
        };
        var request = CreateSubmissionRequest(
            FormSubmissionOperations.Create,
            geometry: Json("""{"x":100,"y":200,"spatialReference":{"wkid":102100,"latestWkid":3857}}"""));

        var result = await validator.ValidateSubmissionAsync(packageVersion, request);

        result.IsValid.Should().BeTrue();
        result.Issues.Select(static issue => issue.Code).Should().NotContain("geometrySridMismatch");
    }

    [UnitTest]
    public async Task ValidateSubmission_WithUnparseableNumericField_ReturnsValidationIssue()
    {
        var validator = CreateValidator();
        var package = CreatePackage();
        package = new FormPackageDocument
        {
            Title = package.Title,
            Target = package.Target,
            Sections =
            [
                new FormSectionDefinition
                {
                    SectionId = "main",
                    Label = "Main",
                    FieldIds = ["name", "count"]
                }
            ],
            Fields =
            [
                CreateNameField(),
                new FormFieldDefinition
                {
                    FieldId = "count",
                    Label = "Count",
                    Type = "integer",
                    TargetField = "count",
                    Required = true,
                    SectionId = "main"
                }
            ],
            SubmitPolicy = package.SubmitPolicy,
            AttachmentPolicy = package.AttachmentPolicy,
            OfflinePolicy = package.OfflinePolicy
        };
        var packageVersion = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = package
        };
        var request = CreateSubmissionRequest(
            FormSubmissionOperations.Create,
            values: new Dictionary<string, JsonElement>
            {
                ["name"] = Json("\"Inspection\""),
                ["count"] = Json("1.5")
            });

        var result = await validator.ValidateSubmissionAsync(packageVersion, request);

        result.IsValid.Should().BeFalse();
        result.Issues.Should().Contain(issue => issue.Code == "fieldValueTypeMismatch" && issue.FieldId == "count");
    }

    [UnitTest]
    public async Task ValidateSubmission_WithPerFieldAttachmentPolicy_EnforcesCountRequiredAndContentType()
    {
        var validator = CreateValidator();
        var packageVersion = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = CreatePackage(
                includeAttachment: true,
                attachmentFieldRequired: false,
                fieldAttachmentRequired: true,
                attachmentAllowedContentTypes: ["image/*"],
                fieldAttachmentAllowedContentTypes: ["image/png"],
                fieldAttachmentMaxCount: 1)
        };
        var missing = CreateSubmissionRequest(FormSubmissionOperations.Create);
        var invalid = CreateSubmissionRequest(
            FormSubmissionOperations.Create,
            attachments:
            [
                new FormSubmissionAttachmentDescriptor
                {
                    ClientAttachmentId = "photo-1",
                    FieldId = "photo",
                    PartName = "photo-1",
                    ContentType = "image/jpeg",
                    SizeBytes = 100
                },
                new FormSubmissionAttachmentDescriptor
                {
                    ClientAttachmentId = "photo-2",
                    FieldId = "photo",
                    PartName = "photo-2",
                    ContentType = "image/png",
                    SizeBytes = 100
                }
            ]);

        var missingResult = await validator.ValidateSubmissionAsync(packageVersion, missing);
        var invalidResult = await validator.ValidateSubmissionAsync(packageVersion, invalid);

        missingResult.Issues.Select(static issue => issue.Code).Should().Contain("requiredAttachmentMissing");
        invalidResult.Issues.Select(static issue => issue.Code).Should().Contain([
            "attachmentFieldCountExceeded",
            "attachmentContentTypeNotAllowed"
        ]);
    }

    [UnitTest]
    public async Task ValidateSubmission_DeleteWithRequiredAttachmentField_DoesNotRequireAttachment()
    {
        var validator = CreateValidator();
        var packageVersion = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = CreatePackage(
                includeAttachment: true,
                allowedOperations: [FormSubmissionOperations.Delete],
                attachmentFieldRequired: true,
                fieldAttachmentRequired: true)
        };
        var request = CreateSubmissionRequest(
            FormSubmissionOperations.Delete,
            targetFeatureId: 101,
            values: new Dictionary<string, JsonElement>(),
            includeGeometry: false);

        var result = await validator.ValidateSubmissionAsync(packageVersion, request);

        result.IsValid.Should().BeTrue();
        result.Issues.Select(static issue => issue.Code).Should().NotContain("requiredAttachmentMissing");
    }

    [UnitTest]
    public async Task ValidateSubmission_DeleteWithAttachmentDescriptor_ReturnsAttachmentPolicyIssue()
    {
        var validator = CreateValidator();
        var packageVersion = new FormPackageVersion
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            Package = CreatePackage(
                includeAttachment: true,
                allowedOperations: [FormSubmissionOperations.Delete],
                attachmentFieldRequired: false,
                fieldAttachmentRequired: false)
        };
        var request = CreateSubmissionRequest(
            FormSubmissionOperations.Delete,
            targetFeatureId: 101,
            values: new Dictionary<string, JsonElement>(),
            includeGeometry: false,
            attachments:
            [
                new FormSubmissionAttachmentDescriptor
                {
                    ClientAttachmentId = "photo-1",
                    FieldId = "photo",
                    PartName = "photo-1",
                    ContentType = "image/png",
                    SizeBytes = 100
                }
            ]);

        var result = await validator.ValidateSubmissionAsync(packageVersion, request);

        result.IsValid.Should().BeFalse();
        result.Issues.Select(static issue => issue.Code).Should().Contain("attachmentsNotAllowedForDelete");
        result.Issues.Select(static issue => issue.Code).Should().NotContain("requiredAttachmentMissing");
    }

    [UnitTest]
    public async Task ValidateForPublish_WithInvalidFieldAttachmentPolicy_ReturnsExpectedIssueCodes()
    {
        var validator = CreateValidator();
        var package = CreatePackage(
            includeAttachment: true,
            attachmentAllowedContentTypes: ["image/*"],
            fieldAttachmentAllowedContentTypes: ["application/pdf"],
            fieldAttachmentMaxCount: 3);

        var result = await validator.ValidateForPublishAsync(package);

        result.IsValid.Should().BeFalse();
        result.Issues.Select(static issue => issue.Code).Should().Contain([
            "attachmentFieldCountLimitInvalid",
            "attachmentFieldContentTypeNotAllowed"
        ]);
    }

    private static FormPackageValidator CreateValidator(
        string[]? serviceCapabilities = null,
        SpatialReference? spatialReference = null)
    {
        var layer = CreateLayer(spatialReference ?? SpatialReference.WGS84);
        var service = new ServiceDefinition(
            "test",
            "Test service",
            [layer],
            spatialReference ?? SpatialReference.WGS84,
            Capabilities: serviceCapabilities ?? ["Query", "Extract", "Create", "Update", "Delete"]);
        return new FormPackageValidator(
            new StaticLayerCatalog(service),
            Options.Create(new LimitsOptions
            {
                Attachments = new AttachmentLimits
                {
                    MaxAttachmentSize = 5_000_000,
                    MaxAttachmentsPerFeature = 5,
                    MaxTotalAttachmentSize = 20_000_000,
                    AllowedMimeTypes = "image/*,application/pdf"
                }
            }));
    }

    private static LayerDefinition CreateLayer(SpatialReference spatialReference)
        => new(
            0,
            "Inspections",
            "Field inspections",
            GeometryType.Point,
            spatialReference,
            [
                new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                new FieldDefinition("name", FieldType.String, Length: 255),
                new FieldDefinition("count", FieldType.Integer),
                new FieldDefinition("category", FieldType.String, Length: 64),
                new FieldDefinition("shape", FieldType.Geometry)
            ]);

    private static FormPackageDocument CreatePackage(
        bool includeAttachment = false,
        string[]? allowedOperations = null,
        string[]? attachmentAllowedContentTypes = null,
        string[]? fieldAttachmentAllowedContentTypes = null,
        int? fieldAttachmentMaxCount = 1,
        bool attachmentFieldRequired = true,
        bool fieldAttachmentRequired = true)
        => new()
        {
            Title = "Inspection Form",
            Target = new FormTargetDefinition
            {
                ServiceId = "test",
                LayerId = 0
            },
            Sections =
            [
                new FormSectionDefinition
                {
                    SectionId = "main",
                    Label = "Main",
                    FieldIds = includeAttachment ? ["name", "photo"] : ["name"]
                }
            ],
            Fields = includeAttachment
                ?
                [
                    CreateNameField(),
                    new FormFieldDefinition
                    {
                        FieldId = "photo",
                        Label = "Photo",
                        Type = "attachment",
                        Required = attachmentFieldRequired,
                        SectionId = "main"
                    }
                ]
                : [CreateNameField()],
            SubmitPolicy = new FormSubmitPolicy
            {
                AllowedOperations = allowedOperations ?? [FormSubmissionOperations.Create],
                AllowAttachments = includeAttachment
            },
            AttachmentPolicy = includeAttachment
                ? new FormAttachmentPolicy
                {
                    Enabled = true,
                    MaxAttachmentsPerSubmission = 2,
                    MaxAttachmentBytes = 1_000_000,
                    MaxTotalBytes = 2_000_000,
                    AllowedContentTypes = attachmentAllowedContentTypes ?? ["image/png"],
                    Fields =
                    [
                        new FormFieldAttachmentPolicy
                        {
                            FieldId = "photo",
                            Required = fieldAttachmentRequired,
                            MaxCount = fieldAttachmentMaxCount,
                            AllowedContentTypes = fieldAttachmentAllowedContentTypes ?? ["image/png"]
                        }
                    ]
                }
                : new FormAttachmentPolicy(),
            OfflinePolicy = new FormOfflinePolicy
            {
                Enabled = true,
                PreferredTransports = ["feature-server-replica", "fieldcollection"]
            }
        };

    private static FormFieldDefinition CreateNameField()
        => new()
        {
            FieldId = "name",
            Label = "Name",
            Type = "text",
            TargetField = "name",
            Required = true,
            SectionId = "main"
        };

    private static FormSubmissionRequest CreateSubmissionRequest(
        string operation,
        long? targetFeatureId = null,
        Dictionary<string, JsonElement>? values = null,
        JsonElement? geometry = null,
        bool includeGeometry = true,
        FormSubmissionAttachmentDescriptor[]? attachments = null)
        => new()
        {
            Operation = operation,
            TargetFeatureId = targetFeatureId,
            Values = values ?? new Dictionary<string, JsonElement>
            {
                ["name"] = Json("\"Inspection\"")
            },
            Geometry = includeGeometry
                ? geometry ?? Json("""{"x":-157.8583,"y":21.3069,"spatialReference":{"wkid":4326}}""")
                : null,
            Attachments = attachments ?? []
        };

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class StaticLayerCatalog(ServiceDefinition service) : ILayerCatalog
    {
        public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(service.GetLayer(layerId));

        public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(service.Layers);

        public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(serviceName, service.Name, StringComparison.OrdinalIgnoreCase) ? service : null);

        public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new[] { service });

        public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(service.GetLayer(layerId) is not null);

        public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(serviceName, service.Name, StringComparison.OrdinalIgnoreCase));

        public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
            => Task.FromResult<Relationship?>(null);

        public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<Relationship>());
    }
}
