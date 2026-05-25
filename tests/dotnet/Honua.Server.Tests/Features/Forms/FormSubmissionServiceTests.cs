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
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Forms;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Security;
using Honua.TestKit.Attributes;
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
        var catalog = new StaticLayerCatalog(store.Service);
        return new FormSubmissionService(
            store,
            new FormPackageValidator(catalog, Options.Create(CreateLimitsOptions())),
            catalog,
            new PassThroughEditProcessor(),
            writer,
            attachmentStore ?? new RecordingAttachmentStore(),
            Options.Create(CreateLimitsOptions()),
            Options.Create(new FileUploadSecurityOptions()),
            NullAuditLog.Instance,
            NullLogger<FormSubmissionService>.Instance);
    }

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
    {
        var payload = JsonSerializer.Serialize(request, FormPackageJsonContext.Default.FormSubmissionRequest);
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
        string contentType = "image/png")
    {
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");
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
        long? targetFeatureId = null)
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
            Attachments = includeAttachment
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
                : []
        };

    private static FormPackageVersion CreatePackageVersion(
        ServiceDefinition service,
        bool includeAttachment = false,
        string[]? allowedOperations = null,
        bool attachmentFieldRequired = false)
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
                    ServiceId = service.Name,
                    LayerId = service.Layers[0].Id
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
                        MaxAttachmentBytes = 1_000_000,
                        MaxTotalBytes = 2_000_000,
                        AllowedContentTypes = ["image/png"],
                        Fields =
                        [
                            new FormFieldAttachmentPolicy
                            {
                                FieldId = "photo",
                                Required = attachmentFieldRequired,
                                MaxCount = 1,
                                AllowedContentTypes = ["image/png"]
                            }
                        ]
                    }
                    : new FormAttachmentPolicy()
            }
        };

    private static ServiceDefinition CreateServiceDefinition()
    {
        var layer = new LayerDefinition(
            0,
            "Inspections",
            "Field inspections",
            GeometryType.Point,
            SpatialReference.WGS84,
            [
                new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                new FieldDefinition("name", FieldType.String, Length: 255),
                new FieldDefinition("shape", FieldType.Geometry)
            ]);
        return new ServiceDefinition(
            "test",
            "Test service",
            [layer],
            SpatialReference.WGS84,
            Capabilities: ["Query", "Extract", "Create", "Update", "Delete"]);
    }

    private static JsonElement Json(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeFormPackageStore : IFormPackageStore
    {
        public FakeFormPackageStore(
            bool includeAttachment = false,
            string[]? allowedOperations = null,
            bool attachmentFieldRequired = false)
        {
            Service = CreateServiceDefinition();
            PackageVersion = CreatePackageVersion(Service, includeAttachment, allowedOperations, attachmentFieldRequired);
        }

        public ServiceDefinition Service { get; }

        public FormPackageVersion PackageVersion { get; }

        public bool ClaimResult { get; init; } = true;

        public int CompleteCalls { get; private set; }

        public bool CompleteTokenWasCanceled { get; private set; }

        public FormSubmissionResponse? CompletedResponse { get; private set; }

        public int GetSubmissionByIdempotencyCalls { get; private set; }

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
            => Task.CompletedTask;
    }

    private sealed class StaticLayerCatalog(ServiceDefinition service) : ILayerCatalog
    {
        public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(service.GetLayer(layerId));

        public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(service.Layers);

        public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult<ServiceDefinition?>(string.Equals(serviceName, service.Name, StringComparison.OrdinalIgnoreCase) ? service : null);

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

    private sealed class PassThroughEditProcessor : IEditProcessor
    {
        public EditValidationResult ValidateEdit(UnifiedEditRequest editRequest, LayerDefinition layer)
            => EditValidationResult.Success();

        public UnifiedEditRequest OptimizeEdit(UnifiedEditRequest editRequest, LayerDefinition layer)
            => editRequest;

        public FeatureEditBatch ToFeatureEditBatch(UnifiedEditRequest editRequest, LayerDefinition layer)
            => FeatureEditBatch.Create();

        public TransactionValidationResult ValidateTransaction(EditTransaction transaction, LayerDefinition layer)
            => TransactionValidationResult.Success();

        public EditExecutionStrategy DetermineExecutionStrategy(UnifiedEditRequest editRequest, LayerDefinition layer)
            => new();

        public Task<EditPerformanceEstimate> EstimatePerformanceAsync(
            UnifiedEditRequest editRequest,
            LayerDefinition layer,
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
