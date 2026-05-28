// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Forms;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Tests.Features.Forms;

public sealed class FormSubmissionServiceTests
{
    [UnitTest]
    public async Task SubmitAsync_WhenRequestAbortedAfterEdit_PersistsTerminalResponseWithServerToken()
    {
        using var requestAbort = new CancellationTokenSource();
        using var requestServices = CreateRequestServices();
        var store = new FakeFormPackageStore();
        var writer = new FakeFeatureWriter
        {
            OnApplyEdits = requestAbort.Cancel
        };
        var service = CreateService(store, writer);
        var context = CreateContext(CreateSubmission("client-cancel-1", "Create"), requestServices, requestAbort.Token);

        var result = await service.SubmitAsync(context, store.PackageVersion.FormId);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        store.CompleteCalls.Should().Be(1);
        store.CompleteTokenWasCanceled.Should().BeFalse();
        store.CompletedResponse.Should().NotBeNull();
        store.CompletedResponse!.Status.Should().Be("accepted");
        store.CompletedResponse.TargetFeatureId.Should().Be(101);
        writer.ApplyEditsCalls.Should().Be(1);
    }

    [UnitTest]
    public async Task SubmitAsync_WhenIdempotencyClaimLost_ReturnsPendingConflictWithoutApplyingEdit()
    {
        using var requestServices = CreateRequestServices();
        var store = new FakeFormPackageStore
        {
            ClaimResult = false
        };
        var writer = new FakeFeatureWriter();
        var service = CreateService(store, writer);
        var context = CreateContext(CreateSubmission("claim-lost-1"), requestServices);

        var result = await service.SubmitAsync(context, store.PackageVersion.FormId);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        writer.ApplyEditsCalls.Should().Be(0);
        store.CompleteCalls.Should().Be(0);
        store.GetSubmissionByIdempotencyCalls.Should().Be(2);
    }

    [UnitTest]
    public async Task SubmitAsync_WhenFeatureEditFails_DoesNotUploadAttachments()
    {
        using var requestServices = CreateRequestServices();
        var store = new FakeFormPackageStore(
            includeAttachment: true,
            allowedOperations: [FormSubmissionOperations.Update],
            attachmentFieldRequired: false);
        var writer = new FakeFeatureWriter
        {
            Result = FeatureEditResult.Rollback(updateResults: ImmutableArray.Create(EditOperationResult.Failure("conflict", objectId: 101)))
        };
        var attachmentStore = new RecordingAttachmentStore();
        var service = CreateService(store, writer, attachmentStore);
        var context = CreateMultipartContext(
            CreateSubmission(
                "edit-fail-attachment-1",
                FormSubmissionOperations.Update,
                includeAttachment: true,
                targetFeatureId: 101),
            requestServices,
            CreateFormFile());

        var result = await service.SubmitAsync(context, store.PackageVersion.FormId);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        var body = ReadResponseBody(context);
        body.Status.Should().Be("failed");
        body.TargetFeatureId.Should().Be(101);
        body.AttachmentOutcomes.Should().BeEmpty();
        body.Retry.Should().NotBeNull();
        attachmentStore.ListCalls.Should().Be(0);
        attachmentStore.UploadCalls.Should().Be(0);
        store.CompletedResponse!.Status.Should().Be("failed");
    }

    [UnitTest]
    public async Task SubmitAsync_WhenJsonHasNullValueAndAttachmentCollections_ReturnsControlledRejection()
    {
        using var requestServices = CreateRequestServices();
        var store = new FakeFormPackageStore();
        var writer = new FakeFeatureWriter();
        var service = CreateService(store, writer);
        var context = CreateJsonContext(
            """
            {
              "idempotencyKey": "null-collections-1",
              "operation": "create",
              "clientId": "field-client-1",
              "values": null,
              "geometry": { "x": -157.8583, "y": 21.3069, "spatialReference": { "wkid": 4326 } },
              "attachments": null
            }
            """,
            requestServices);

        var result = await service.SubmitAsync(context, store.PackageVersion.FormId);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var body = ReadResponseBody(context);
        body.Status.Should().Be("rejected");
        writer.ApplyEditsCalls.Should().Be(0);
    }

    [UnitTest]
    public async Task SubmitAsync_WithDuplicateMultipartPartName_ReturnsRejectedValidationResponse()
    {
        using var requestServices = CreateRequestServices();
        var store = new FakeFormPackageStore(
            includeAttachment: true,
            allowedOperations: [FormSubmissionOperations.Create],
            attachmentFieldRequired: false);
        var writer = new FakeFeatureWriter();
        var attachmentStore = new RecordingAttachmentStore();
        var service = CreateService(store, writer, attachmentStore);
        var context = CreateMultipartContext(
            CreateSubmission("duplicate-part-1", includeAttachment: true),
            requestServices,
            CreateFormFile(fileName: "first.png"),
            CreateFormFile(fileName: "second.png"));

        var result = await service.SubmitAsync(context, store.PackageVersion.FormId);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var body = ReadResponseBody(context);
        body.Status.Should().Be("rejected");
        body.ValidationIssues.Should().Contain(issue => issue.Code == "attachmentPartNameDuplicate");
        body.AttachmentOutcomes.Should().ContainSingle(outcome =>
            outcome.Status == "rejected" &&
            outcome.FieldId == "photo");
        writer.ApplyEditsCalls.Should().Be(0);
        attachmentStore.UploadCalls.Should().Be(0);
    }

    [UnitTest]
    public async Task SubmitAsync_WithUnderreportedMultipartSizes_ReturnsRejectedByActualTotal()
    {
        using var requestServices = CreateRequestServices();
        var store = new FakeFormPackageStore(
            includeAttachment: true,
            allowedOperations: [FormSubmissionOperations.Create],
            attachmentFieldRequired: false,
            maxAttachmentBytes: 2_000_000,
            maxTotalBytes: 1_000_000,
            fieldAttachmentMaxCount: 2);
        var writer = new FakeFeatureWriter();
        var attachmentStore = new RecordingAttachmentStore();
        var service = CreateService(store, writer, attachmentStore);
        var request = CreateSubmission(
            "underreported-total-1",
            attachments:
            [
                new FormSubmissionAttachmentDescriptor
                {
                    ClientAttachmentId = "photo-1",
                    FieldId = "photo",
                    PartName = "photo-1",
                    Filename = "photo-1.png",
                    ContentType = "image/png",
                    SizeBytes = 1
                },
                new FormSubmissionAttachmentDescriptor
                {
                    ClientAttachmentId = "photo-2",
                    FieldId = "photo",
                    PartName = "photo-2",
                    Filename = "photo-2.png",
                    ContentType = "image/png",
                    SizeBytes = 1
                }
            ]);
        var context = CreateMultipartContext(
            request,
            requestServices,
            CreateFormFile(partName: "photo-1", fileName: "photo-1.png", byteLength: 900_000),
            CreateFormFile(partName: "photo-2", fileName: "photo-2.png", byteLength: 900_000));

        var result = await service.SubmitAsync(context, store.PackageVersion.FormId);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var body = ReadResponseBody(context);
        body.Status.Should().Be("rejected");
        body.ValidationIssues.Should().Contain(issue => issue.Code == "attachmentTotalSizeExceeded");
        body.ValidationIssues.Should().NotContain(issue => issue.Code == "attachmentSizeInvalid");
        body.AttachmentOutcomes.Should().HaveCount(2);
        writer.ApplyEditsCalls.Should().Be(0);
        attachmentStore.UploadCalls.Should().Be(0);
    }

    [UnitTest]
    public async Task SubmitAsync_WithUnsafeDescriptorFilename_ReturnsRejectedBeforeUpload()
    {
        using var requestServices = CreateRequestServices();
        var store = new FakeFormPackageStore(
            includeAttachment: true,
            allowedOperations: [FormSubmissionOperations.Create],
            attachmentFieldRequired: false);
        var writer = new FakeFeatureWriter();
        var attachmentStore = new RecordingAttachmentStore();
        var service = CreateService(store, writer, attachmentStore);
        var request = CreateSubmission(
            "unsafe-descriptor-name-1",
            attachments:
            [
                new FormSubmissionAttachmentDescriptor
                {
                    ClientAttachmentId = "photo-1",
                    FieldId = "photo",
                    PartName = "photo-file",
                    Filename = "photo.exe",
                    ContentType = "image/png",
                    SizeBytes = 100
                }
            ]);
        var context = CreateMultipartContext(request, requestServices, CreateFormFile(fileName: "photo.png"));

        var result = await service.SubmitAsync(context, store.PackageVersion.FormId);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var body = ReadResponseBody(context);
        body.Status.Should().Be("rejected");
        body.ValidationIssues.Should().Contain(issue => issue.Code == "attachmentSecurityRejected");
        writer.ApplyEditsCalls.Should().Be(0);
        attachmentStore.UploadCalls.Should().Be(0);
    }

    [UnitTest]
    public async Task SubmitAsync_WithRejectedDuplicateAttachmentIds_RecordsEachDescriptorPart()
    {
        using var requestServices = CreateRequestServices();
        var store = new FakeFormPackageStore(
            includeAttachment: true,
            allowedOperations: [FormSubmissionOperations.Create],
            attachmentFieldRequired: false,
            fieldAttachmentMaxCount: 2);
        var writer = new FakeFeatureWriter();
        var attachmentStore = new RecordingAttachmentStore();
        var service = CreateService(store, writer, attachmentStore);
        var request = CreateSubmission(
            "duplicate-client-attachment-id-1",
            attachments:
            [
                new FormSubmissionAttachmentDescriptor
                {
                    ClientAttachmentId = "photo-1",
                    FieldId = "photo",
                    PartName = "missing-photo-1",
                    Filename = "photo-1.png",
                    ContentType = "image/png",
                    SizeBytes = 100
                },
                new FormSubmissionAttachmentDescriptor
                {
                    ClientAttachmentId = "photo-1",
                    FieldId = "photo",
                    PartName = "missing-photo-2",
                    Filename = "photo-2.png",
                    ContentType = "image/png",
                    SizeBytes = 100
                }
            ]);
        var context = CreateMultipartContext(request, requestServices);

        var result = await service.SubmitAsync(context, store.PackageVersion.FormId);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var body = ReadResponseBody(context);
        body.AttachmentOutcomes.Should().HaveCount(2);
        store.RecordedAttachmentDescriptors.Select(static descriptor => descriptor.PartName)
            .Should().Equal("missing-photo-1", "missing-photo-2");
        writer.ApplyEditsCalls.Should().Be(0);
        attachmentStore.UploadCalls.Should().Be(0);
    }

    [UnitTest]
    public async Task SubmitAsync_WithDeleteAttachmentDescriptor_ReturnsRejectedWithoutEditing()
    {
        using var requestServices = CreateRequestServices();
        var store = new FakeFormPackageStore(
            includeAttachment: true,
            allowedOperations: [FormSubmissionOperations.Delete],
            attachmentFieldRequired: true);
        var writer = new FakeFeatureWriter();
        var attachmentStore = new RecordingAttachmentStore();
        var service = CreateService(store, writer, attachmentStore);
        var context = CreateMultipartContext(
            CreateSubmission(
                "delete-attachment-1",
                FormSubmissionOperations.Delete,
                includeAttachment: true,
                targetFeatureId: 101),
            requestServices,
            CreateFormFile());

        var result = await service.SubmitAsync(context, store.PackageVersion.FormId);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var body = ReadResponseBody(context);
        body.Status.Should().Be("rejected");
        body.ValidationIssues.Should().Contain(issue => issue.Code == "attachmentsNotAllowedForDelete");
        writer.ApplyEditsCalls.Should().Be(0);
        attachmentStore.UploadCalls.Should().Be(0);
    }

    private static FormSubmissionService CreateService(
        FakeFormPackageStore store,
        FakeFeatureWriter writer,
        IAttachmentStore? attachmentStore = null)
    {
        var targetMetadataResolver = new FormTargetMetadataResolver(CreateMetadataProvider());
        return new FormSubmissionService(
            store,
            new FormPackageValidator(targetMetadataResolver, Options.Create(CreateLimitsOptions())),
            targetMetadataResolver,
            new PassThroughEditProcessor(),
            writer,
            attachmentStore ?? new RecordingAttachmentStore(),
            Options.Create(CreateLimitsOptions()),
            Options.Create(new FileUploadSecurityOptions()),
            NullAuditLog.Instance,
            NullLogger<FormSubmissionService>.Instance);
    }

    private const string TargetServiceName = "test";
    private const int TargetLayerId = 0;

    private static TestMetadataV2GraphProvider CreateMetadataProvider()
        => new TestMetadataV2GraphBuilder()
            .AddConnection("managed", "Managed")
            .AddResource(
                "resource-inspections",
                "Inspections",
                fields:
                [
                    new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, Editable = true },
                    new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Length = 255, Editable = true },
                    new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Editable = false }
                ],
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
                },
                annotations: new Dictionary<string, string>
                {
                    ["honua.io/attachments"] = bool.TrueString
                })
            .AddStorageBinding(
                "binding-inspections",
                "resource-inspections",
                "inspections",
                "managed",
                storageLayerId: TargetLayerId)
            .AddService(
                "service-test",
                TargetServiceName,
                protocols: ["FeatureServer"],
                options: new Dictionary<string, JsonElement>
                {
                    ["capabilities"] = JsonArray(["Query", "Extract", "Create", "Update", "Delete"])
                })
            .AddPublication(
                "publication-inspections",
                "service-test",
                "resource-inspections",
                layerIndex: TargetLayerId,
                storageBindingId: "binding-inspections",
                publicationType: MetadataV2PublicationType.EsriFeatureLayer)
            .BuildProvider();

    private static JsonElement JsonArray(IEnumerable<string> values)
        => JsonSerializer.SerializeToElement(values);

    private static ServiceProvider CreateRequestServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions
        {
            DataEditorRoles = ["data-editor"]
        }));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext CreateContext(
        FormSubmissionRequest request,
        IServiceProvider requestServices,
        CancellationToken requestAborted = default)
        => CreateJsonContext(
            JsonSerializer.Serialize(request, FormPackageJsonContext.Default.FormSubmissionRequest),
            requestServices,
            requestAborted);

    private static DefaultHttpContext CreateJsonContext(
        string payload,
        IServiceProvider requestServices,
        CancellationToken requestAborted = default)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = requestServices,
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "field-user"),
                new Claim("roles", "data-editor")
            ], "test")),
            Response =
            {
                Body = new MemoryStream()
            }
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        context.RequestAborted = requestAborted;
        return context;
    }

    private static DefaultHttpContext CreateMultipartContext(
        FormSubmissionRequest request,
        IServiceProvider requestServices,
        params IFormFile[] files)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = requestServices,
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "field-user"),
                new Claim("roles", "data-editor")
            ], "test")),
            Response =
            {
                Body = new MemoryStream()
            }
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "multipart/form-data; boundary=test-boundary";
        var formFiles = new FormFileCollection();
        foreach (var file in files)
        {
            formFiles.Add(file);
        }

        var form = new FormCollection(
            new Dictionary<string, StringValues>(StringComparer.Ordinal)
            {
                ["submission"] = JsonSerializer.Serialize(request, FormPackageJsonContext.Default.FormSubmissionRequest)
            },
            formFiles);
        context.Features.Set<IFormFeature>(new FormFeature(form));
        return context;
    }

    private static FormSubmissionResponse ReadResponseBody(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return JsonSerializer.Deserialize(
            context.Response.Body,
            FormPackageJsonContext.Default.FormSubmissionResponse)!;
    }

    private static FormFile CreateFormFile(
        string partName = "photo-file",
        string fileName = "photo.png",
        string contentType = "image/png",
        int? byteLength = null)
    {
        var bytes = byteLength is int length
            ? new byte[length]
            : Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");
        var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, stream.Length, partName, fileName)
        {
            Headers = new HeaderDictionary()
        };
        file.ContentType = contentType;
        return file;
    }

    private static LimitsOptions CreateLimitsOptions()
        => new()
        {
            Attachments = new AttachmentLimits
            {
                MaxAttachmentSize = 5_000_000,
                MaxAttachmentsPerFeature = 5,
                MaxTotalAttachmentSize = 20_000_000,
                AllowedMimeTypes = "image/*,application/pdf"
            }
        };

    private static FormSubmissionRequest CreateSubmission(
        string idempotencyKey,
        string operation = FormSubmissionOperations.Create,
        bool includeAttachment = false,
        long? targetFeatureId = null,
        FormSubmissionAttachmentDescriptor[]? attachments = null)
        => new()
        {
            IdempotencyKey = idempotencyKey,
            Operation = operation,
            TargetFeatureId = targetFeatureId,
            ClientId = "field-client-1",
            Values = new Dictionary<string, JsonElement>
            {
                ["name"] = Json(JsonSerializer.Serialize("Inspection"))
            },
            Geometry = Json("""{"x":-157.8583,"y":21.3069,"spatialReference":{"wkid":4326}}"""),
            Attachments = attachments ?? (includeAttachment
                ?
                [
                    new FormSubmissionAttachmentDescriptor
                    {
                        ClientAttachmentId = "photo-1",
                        FieldId = "photo",
                        PartName = "photo-file",
                        Filename = "photo.png",
                        ContentType = "image/png"
                    }
                ]
                : [])
        };

    private static FormPackageVersion CreatePackageVersion(
        bool includeAttachment = false,
        string[]? allowedOperations = null,
        bool attachmentFieldRequired = false,
        long maxAttachmentBytes = 1_000_000,
        long maxTotalBytes = 2_000_000,
        int fieldAttachmentMaxCount = 1)
        => new()
        {
            FormId = "inspection",
            Version = 1,
            Status = FormPackageStatus.Published,
            ContentHash = "content-hash",
            PolicyHash = "policy-hash",
            Package = new FormPackageDocument
            {
                FormId = "inspection",
                Title = "Inspection Form",
                Target = new FormTargetDefinition
                {
                    ServiceId = TargetServiceName,
                    LayerId = TargetLayerId
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
                    new FormFieldDefinition
                    {
                        FieldId = "name",
                        Label = "Name",
                        Type = "text",
                        TargetField = "name",
                        Required = true,
                        SectionId = "main"
                    },
                    new FormFieldDefinition
                    {
                        FieldId = "photo",
                        Label = "Photo",
                        Type = "attachment",
                        Required = attachmentFieldRequired,
                        SectionId = "main"
                    }
                    ]
                    :
                    [
                    new FormFieldDefinition
                    {
                        FieldId = "name",
                        Label = "Name",
                        Type = "text",
                        TargetField = "name",
                        Required = true,
                        SectionId = "main"
                    }
                    ],
                SubmitPolicy = new FormSubmitPolicy
                {
                    AllowedOperations = allowedOperations ?? [FormSubmissionOperations.Create],
                    RequiresGeometry = true,
                    AllowAttachments = includeAttachment
                },
                AttachmentPolicy = includeAttachment
                    ? new FormAttachmentPolicy
                    {
                        Enabled = true,
                        MaxAttachmentsPerSubmission = 2,
                        MaxAttachmentBytes = maxAttachmentBytes,
                        MaxTotalBytes = maxTotalBytes,
                        AllowedContentTypes = ["image/png"],
                        Fields =
                        [
                            new FormFieldAttachmentPolicy
                            {
                                FieldId = "photo",
                                Required = attachmentFieldRequired,
                                MaxCount = fieldAttachmentMaxCount,
                                AllowedContentTypes = ["image/png"]
                            }
                        ]
                    }
                    : new FormAttachmentPolicy()
            }
        };

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeFormPackageStore : IFormPackageStore
    {
        public FakeFormPackageStore(
            bool includeAttachment = false,
            string[]? allowedOperations = null,
            bool attachmentFieldRequired = false,
            long maxAttachmentBytes = 1_000_000,
            long maxTotalBytes = 2_000_000,
            int fieldAttachmentMaxCount = 1)
        {
            PackageVersion = CreatePackageVersion(
                includeAttachment,
                allowedOperations,
                attachmentFieldRequired,
                maxAttachmentBytes,
                maxTotalBytes,
                fieldAttachmentMaxCount);
        }

        public FormPackageVersion PackageVersion { get; }

        public bool ClaimResult { get; init; } = true;

        public int CompleteCalls { get; private set; }

        public bool CompleteTokenWasCanceled { get; private set; }

        public FormSubmissionResponse? CompletedResponse { get; private set; }

        public int GetSubmissionByIdempotencyCalls { get; private set; }

        public List<FormSubmissionAttachmentDescriptor> RecordedAttachmentDescriptors { get; } = [];

        public List<FormSubmissionAttachmentOutcome> RecordedAttachmentOutcomes { get; } = [];

        private string? LastRequestHash { get; set; }

        public Task<FormPackageSummary[]> ListPackagesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FormPackageVersion?> GetCurrentVersionAsync(
            string formId,
            string status,
            CancellationToken cancellationToken = default)
            => Task.FromResult<FormPackageVersion?>(PackageVersion);

        public Task<FormPackageVersion?> GetVersionAsync(
            string formId,
            int version,
            CancellationToken cancellationToken = default)
            => Task.FromResult<FormPackageVersion?>(PackageVersion);

        public Task<FormPackageVersion[]> ListVersionsAsync(
            string formId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FormPackageVersion> SaveDraftAsync(
            FormPackageDocument package,
            string actor,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FormPackageVersion?> UpdateDraftAsync(
            string formId,
            int version,
            FormPackageDocument package,
            string expectedETag,
            string actor,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FormPackageVersion?> StoreValidationAsync(
            string formId,
            int version,
            FormPackageValidationResult validation,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FormPackageVersion?> PublishAsync(
            string formId,
            int version,
            FormPackageValidationResult validation,
            string actor,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FormPackageVersion?> ReopenAsync(
            string formId,
            int publishedVersion,
            string actor,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FormSubmissionRecord?> GetSubmissionByIdempotencyAsync(
            string formId,
            int formVersion,
            string actorHash,
            string idempotencyKey,
            CancellationToken cancellationToken = default)
        {
            GetSubmissionByIdempotencyCalls++;
            if (GetSubmissionByIdempotencyCalls == 1)
            {
                return Task.FromResult<FormSubmissionRecord?>(null);
            }

            return Task.FromResult<FormSubmissionRecord?>(new FormSubmissionRecord
            {
                SubmissionId = Guid.NewGuid(),
                RequestHash = LastRequestHash ?? string.Empty,
                Status = "pending"
            });
        }

        public Task<bool> CreateSubmissionAsync(
            Guid submissionId,
            string? idempotencyKey,
            string actorHash,
            string requestHash,
            FormPackageVersion packageVersion,
            FormSubmissionRequest request,
            string status,
            CancellationToken cancellationToken = default)
        {
            LastRequestHash = requestHash;
            return Task.FromResult(ClaimResult);
        }

        public Task CompleteSubmissionAsync(
            Guid submissionId,
            FormSubmissionResponse response,
            string status,
            CancellationToken cancellationToken = default)
        {
            CompleteCalls++;
            CompleteTokenWasCanceled = cancellationToken.IsCancellationRequested;
            if (CompleteTokenWasCanceled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            CompletedResponse = response;
            return Task.CompletedTask;
        }

        public Task RecordAttachmentOutcomeAsync(
            Guid submissionId,
            FormSubmissionAttachmentDescriptor descriptor,
            FormSubmissionAttachmentOutcome outcome,
            FormPackageVersion packageVersion,
            CancellationToken cancellationToken = default)
        {
            RecordedAttachmentDescriptors.Add(descriptor);
            RecordedAttachmentOutcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }

    private sealed class PassThroughEditProcessor : IEditProcessor
    {
        public EditValidationResult ValidateEdit(UnifiedEditRequest editRequest, MetadataV2Resource resource)
            => EditValidationResult.Success();

        public UnifiedEditRequest OptimizeEdit(UnifiedEditRequest editRequest, MetadataV2Resource resource)
            => editRequest;

        public FeatureEditBatch ToFeatureEditBatch(UnifiedEditRequest editRequest, MetadataV2Resource resource)
            => FeatureEditBatch.Create();

        public TransactionValidationResult ValidateTransaction(EditTransaction transaction, MetadataV2Resource resource)
            => TransactionValidationResult.Success();

        public EditExecutionStrategy DetermineExecutionStrategy(UnifiedEditRequest editRequest, MetadataV2Resource resource)
            => new();

        public Task<EditPerformanceEstimate> EstimatePerformanceAsync(
            UnifiedEditRequest editRequest,
            MetadataV2Resource resource,
            CancellationToken cancellationToken)
            => Task.FromResult(new EditPerformanceEstimate());
    }

    private sealed class FakeFeatureWriter : IFeatureWriter
    {
        public int ApplyEditsCalls { get; private set; }

        public Action? OnApplyEdits { get; init; }

        public FeatureEditResult Result { get; init; } = FeatureEditResult.Success(1, 0, 0, ImmutableArray.Create(101L));

        public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
        {
            ApplyEditsCalls++;
            OnApplyEdits?.Invoke();
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingAttachmentStore : IAttachmentStore
    {
        public int ListCalls { get; private set; }

        public int UploadCalls { get; private set; }

        public Task<Attachment?> GetAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default)
            => Task.FromResult<Attachment?>(null);

        public Task<Attachment[]> ListAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(Array.Empty<Attachment>());
        }

        public Task<Attachment> CreateAsync(int layerId, long featureId, Attachment attachment, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Attachment> UpdateAsync(int layerId, long featureId, Attachment attachment, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Attachment> ReplaceAsync(
            int layerId,
            long featureId,
            long attachmentId,
            string filename,
            string contentType,
            Stream content,
            string? keywords = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Attachment> UploadAsync(
            int layerId,
            long featureId,
            string filename,
            string contentType,
            Stream content,
            string? keywords = null,
            CancellationToken cancellationToken = default)
        {
            UploadCalls++;
            return Task.FromResult(Attachment.Create(
                UploadCalls,
                featureId,
                layerId,
                filename,
                contentType,
                content.Length,
                DateTime.UtcNow,
                $"memory/{UploadCalls}",
                keywords));
        }

        public Task<AttachmentContent?> DownloadAsync(int layerId, long featureId, long attachmentId, CancellationToken cancellationToken = default)
            => Task.FromResult<AttachmentContent?>(null);
    }
}
