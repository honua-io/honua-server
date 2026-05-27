// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Forms.Packages;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Security;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Forms;

internal sealed class FormSubmissionService
{
    private const string ClientIdHeader = "X-Honua-Client-Id";
    private static readonly TimeSpan _postClaimWorkTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _terminalPersistenceTimeout = TimeSpan.FromSeconds(30);
    private readonly IFormPackageStore _store;
    private readonly FormPackageValidator _validator;
    private readonly IFormTargetMetadataResolver _targetMetadataResolver;
    private readonly IEditProcessor _editProcessor;
    private readonly IFeatureWriter _featureWriter;
    private readonly IAttachmentStore _attachmentStore;
    private readonly AttachmentLimits _attachmentLimits;
    private readonly FileUploadSecurityOptions _fileUploadOptions;
    private readonly IAuditLog _auditLog;
    private readonly ILogger<FormSubmissionService> _logger;

    public FormSubmissionService(
        IFormPackageStore store,
        FormPackageValidator validator,
        IFormTargetMetadataResolver targetMetadataResolver,
        IEditProcessor editProcessor,
        IFeatureWriter featureWriter,
        IAttachmentStore attachmentStore,
        IOptions<LimitsOptions> limitsOptions,
        IOptions<FileUploadSecurityOptions> fileUploadOptions,
        IAuditLog auditLog,
        ILogger<FormSubmissionService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _targetMetadataResolver = targetMetadataResolver ?? throw new ArgumentNullException(nameof(targetMetadataResolver));
        _editProcessor = editProcessor ?? throw new ArgumentNullException(nameof(editProcessor));
        _featureWriter = featureWriter ?? throw new ArgumentNullException(nameof(featureWriter));
        _attachmentStore = attachmentStore ?? throw new ArgumentNullException(nameof(attachmentStore));
        ArgumentNullException.ThrowIfNull(limitsOptions);
        ArgumentNullException.ThrowIfNull(fileUploadOptions);
        _attachmentLimits = limitsOptions.Value.Attachments;
        _fileUploadOptions = fileUploadOptions.Value;
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IResult> SubmitAsync(HttpContext context, string formId)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity("Forms.Submit");
        activity?.SetTag("honua.protocol", "forms");
        activity?.SetTag("honua.operation", "submit");
        activity?.SetTag("honua.forms.form_id", formId);

        var parseResult = await ReadSubmissionAsync(context).ConfigureAwait(false);
        if (parseResult.Request is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status400BadRequest, parseResult.Error ?? "Request body must be a valid form submission.");
        }

        var request = ApplyHeaderClientId(parseResult.Request, context);
        var packageVersion = await ResolvePackageVersionAsync(formId, request, context.RequestAborted).ConfigureAwait(false);
        if (packageVersion is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, $"Published form package '{formId}' was not found.");
        }

        request = NormalizeAttachmentDescriptors(request, parseResult.Files);
        activity?.SetTag("honua.forms.version", packageVersion.Version);
        activity?.SetTag("honua.forms.operation", request.Operation);

        var targetMetadata = await _targetMetadataResolver.ResolveAsync(packageVersion.Package.Target, context.RequestAborted)
            .ConfigureAwait(false);
        if (targetMetadata.Service is null ||
            targetMetadata.Resource is null ||
            targetMetadata.StorageLayerId is not int storageLayerId)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, "Target service or layer for the form package was not found.");
        }

        var authorizationFailure = await RequireTargetWriteAccessAsync(
            context,
            targetMetadata,
            targetMetadata.Service.Metadata.Name,
            context.RequestAborted).ConfigureAwait(false);
        if (authorizationFailure is not null)
        {
            await RecordAuditAsync(context, "forms.submission.create", packageVersion.FormId, AuditOutcome.Denied, $"{{\"version\":{packageVersion.Version}}}", context.RequestAborted)
                .ConfigureAwait(false);
            return authorizationFailure;
        }

        var actorHash = Hash($"{ResolveActor(context)}|{request.ClientId}");
        var requestHash = Hash(parseResult.RawRequest);
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var existing = await _store.GetSubmissionByIdempotencyAsync(
                packageVersion.FormId,
                packageVersion.Version,
                actorHash,
                request.IdempotencyKey,
                context.RequestAborted).ConfigureAwait(false);
            if (existing is not null)
            {
                return ResolveIdempotencyReplay(context, existing, requestHash);
            }
        }

        var submissionId = Guid.NewGuid();
        using var postClaimCts = new CancellationTokenSource(_postClaimWorkTimeout);
        var postClaimToken = postClaimCts.Token;

        var claimed = await _store.CreateSubmissionAsync(
            submissionId,
            request.IdempotencyKey,
            actorHash,
            requestHash,
            packageVersion,
            request,
            "pending",
            postClaimToken).ConfigureAwait(false);
        if (!claimed)
        {
            return await ResolveClaimConflictAsync(
                context,
                packageVersion,
                actorHash,
                request.IdempotencyKey,
                requestHash).ConfigureAwait(false);
        }

        try
        {
            var validation = await _validator.ValidateSubmissionAsync(packageVersion, request, postClaimToken)
                .ConfigureAwait(false);
            var fileValidation = await ValidateAttachmentFilesAsync(request, parseResult.Files, packageVersion, postClaimToken)
                .ConfigureAwait(false);
            validation = MergeValidation(validation, fileValidation.Issues);

            if (!validation.IsValid)
            {
                var rejectedAttachmentRecords = BuildRejectedAttachmentOutcomes(request, validation.Issues, fileValidation.Outcomes);
                foreach (var record in rejectedAttachmentRecords)
                {
                    await RecordAttachmentOutcomeAsync(context, submissionId, packageVersion, record.Descriptor, record.Outcome, postClaimToken)
                        .ConfigureAwait(false);
                }

                var rejectedAttachmentOutcomes = rejectedAttachmentRecords
                    .Select(static record => record.Outcome)
                    .ToArray();
                var statusCode = validation.Issues.Any(static issue =>
                    string.Equals(issue.Code, "operationNotAllowed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(issue.Code, "attachmentsNotAllowed", StringComparison.OrdinalIgnoreCase))
                    ? StatusCodes.Status403Forbidden
                    : StatusCodes.Status400BadRequest;
                var rejected = BuildRejectedResponse(
                    submissionId,
                    packageVersion,
                    request,
                    validation.Issues,
                    statusCode == StatusCodes.Status403Forbidden,
                    rejectedAttachmentOutcomes);
                await CompleteSubmissionAsync(submissionId, rejected, "rejected").ConfigureAwait(false);
                await RecordAuditAsync(context, "forms.submission.create", packageVersion.FormId, AuditOutcome.Failure, $"{{\"version\":{packageVersion.Version},\"issueCount\":{validation.Issues.Length}}}", CancellationToken.None)
                    .ConfigureAwait(false);
                return Results.Json(rejected, FormPackageJsonContext.Default.FormSubmissionResponse, statusCode: statusCode);
            }

            var editRequest = BuildEditRequest(packageVersion.Package, request, targetMetadata.Resource);
            var editValidation = _editProcessor.ValidateEdit(editRequest, targetMetadata.Resource);
            if (!editValidation.IsValid)
            {
                var rejected = BuildRejectedResponse(
                    submissionId,
                    packageVersion,
                    request,
                    [new FormValidationIssue
                    {
                        Code = "editValidationFailed",
                        Severity = "error",
                        Message = "Submission did not satisfy target edit validation."
                    }],
                    retryable: false);
                await CompleteSubmissionAsync(submissionId, rejected, "rejected").ConfigureAwait(false);
                return Results.Json(rejected, FormPackageJsonContext.Default.FormSubmissionResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            var optimized = _editProcessor.OptimizeEdit(editRequest, targetMetadata.Resource);
            var batch = _editProcessor.ToFeatureEditBatch(optimized, targetMetadata.Resource);
            var editResult = await _featureWriter.ApplyEditsAsync(storageLayerId, batch, postClaimToken).ConfigureAwait(false);
            var targetFeatureId = ResolveTargetFeatureId(request, editResult);
            var attachmentOutcomes = ShouldUploadAttachments(request, editResult)
                ? await UploadAttachmentsAsync(context, packageVersion, request, parseResult.Files, storageLayerId, targetFeatureId, submissionId, postClaimToken)
                    .ConfigureAwait(false)
                : [];
            var response = new FormSubmissionResponse
            {
                SubmissionId = submissionId,
                Status = editResult.IsSuccess ? "accepted" : "failed",
                FormId = packageVersion.FormId,
                FormVersion = packageVersion.Version,
                Operation = request.Operation,
                TargetFeatureId = targetFeatureId,
                EditOutcome = new FormEditOutcome
                {
                    Succeeded = editResult.IsSuccess,
                    Created = editResult.CreatedCount,
                    Updated = editResult.UpdatedCount,
                    Deleted = editResult.DeletedCount,
                    Error = editResult.HasErrors ? "One or more feature edits failed." : null
                },
                AttachmentOutcomes = attachmentOutcomes,
                Retry = editResult.IsSuccess
                    ? null
                    : new FormSubmissionRetryGuidance
                    {
                        Retryable = true,
                        Reason = "Feature edit did not complete successfully; attachments were not uploaded."
                    }
            };

            await CompleteSubmissionAsync(submissionId, response, response.Status).ConfigureAwait(false);
            await RecordAuditAsync(context, "forms.submission.create", packageVersion.FormId, editResult.IsSuccess ? AuditOutcome.Success : AuditOutcome.Failure, $"{{\"version\":{packageVersion.Version},\"operation\":\"{request.Operation}\"}}", CancellationToken.None)
                .ConfigureAwait(false);
            if (editResult.IsSuccess)
            {
                FormSubmissionLog.SubmissionAccepted(_logger, packageVersion.FormId, packageVersion.Version, submissionId, request.Operation, attachmentOutcomes.Length);
            }
            else
            {
                FormSubmissionLog.SubmissionEditFailed(_logger, packageVersion.FormId, packageVersion.Version, submissionId, request.Operation);
            }

            return Results.Json(response, FormPackageJsonContext.Default.FormSubmissionResponse);
        }
        catch (OperationCanceledException ex)
        {
            FormSubmissionLog.SubmissionFailed(_logger, ex, packageVersion.FormId, packageVersion.Version, submissionId);
            var response = BuildFailedResponse(submissionId, packageVersion, request, "The server could not complete the submission before the post-claim timeout.");
            await CompleteSubmissionAsync(submissionId, response, "failed").ConfigureAwait(false);
            await RecordAuditAsync(context, "forms.submission.create", packageVersion.FormId, AuditOutcome.Failure, $"{{\"version\":{packageVersion.Version},\"timeout\":true}}", CancellationToken.None)
                .ConfigureAwait(false);
            return Results.Json(response, FormPackageJsonContext.Default.FormSubmissionResponse, statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (Exception ex)
        {
            FormSubmissionLog.SubmissionFailed(_logger, ex, packageVersion.FormId, packageVersion.Version, submissionId);
            var response = BuildFailedResponse(submissionId, packageVersion, request, "The server could not complete the submission.");
            await CompleteSubmissionAsync(submissionId, response, "failed").ConfigureAwait(false);
            await RecordAuditAsync(context, "forms.submission.create", packageVersion.FormId, AuditOutcome.Failure, $"{{\"version\":{packageVersion.Version}}}", CancellationToken.None)
                .ConfigureAwait(false);
            return Results.Json(response, FormPackageJsonContext.Default.FormSubmissionResponse, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private async Task<FormPackageVersion?> ResolvePackageVersionAsync(
        string formId,
        FormSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.FormVersion is int version)
        {
            var packageVersion = await _store.GetVersionAsync(formId, version, cancellationToken).ConfigureAwait(false);
            return packageVersion is not null &&
                   string.Equals(packageVersion.Status, FormPackageStatus.Published, StringComparison.OrdinalIgnoreCase)
                ? packageVersion
                : null;
        }

        return await _store.GetCurrentVersionAsync(formId, FormPackageStatus.Published, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IResult?> RequireTargetWriteAccessAsync(
        HttpContext context,
        FormTargetMetadataResolution target,
        string fallbackServiceId,
        CancellationToken cancellationToken)
    {
        var (service, _, resource, _) = target;
        if (resource is not null)
        {
            return await ServiceDataEditorAuthorization.RequireResourceDataEditorAsync(
                context,
                resource,
                service,
                cancellationToken).ConfigureAwait(false);
        }

        if (service is not null)
        {
            return await ServiceDataEditorAuthorization.RequireServiceDataEditorAsync(
                context,
                service,
                cancellationToken).ConfigureAwait(false);
        }

        return await ServiceDataEditorAuthorization.RequireServiceDataEditorAsync(
            context,
            fallbackServiceId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IResult> ResolveClaimConflictAsync(
        HttpContext context,
        FormPackageVersion packageVersion,
        string actorHash,
        string? idempotencyKey,
        string requestHash)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status409Conflict, "Submission could not be claimed.");
        }

        var existing = await _store.GetSubmissionByIdempotencyAsync(
            packageVersion.FormId,
            packageVersion.Version,
            actorHash,
            idempotencyKey,
            context.RequestAborted).ConfigureAwait(false);
        if (existing is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status409Conflict, "Submission with this idempotency key is still pending.");
        }

        return ResolveIdempotencyReplay(context, existing, requestHash);
    }

    private static IResult ResolveIdempotencyReplay(HttpContext context, FormSubmissionRecord existing, string requestHash)
    {
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status409Conflict, "Idempotency key was already used with a different submission payload.");
        }

        if (existing.Response is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status409Conflict, "Submission with this idempotency key is still pending.");
        }

        return Results.Json(CloneReplayResponse(existing.Response), FormPackageJsonContext.Default.FormSubmissionResponse);
    }

    private async Task CompleteSubmissionAsync(Guid submissionId, FormSubmissionResponse response, string status)
    {
        using var completionCts = new CancellationTokenSource(_terminalPersistenceTimeout);
        await _store.CompleteSubmissionAsync(submissionId, response, status, completionCts.Token).ConfigureAwait(false);
    }

    private static async Task<SubmissionParseResult> ReadSubmissionAsync(HttpContext context)
    {
        try
        {
            if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
                var submissionJson = form["submission"].ToString();
                if (string.IsNullOrWhiteSpace(submissionJson))
                {
                    return new SubmissionParseResult(null, "Multipart submissions must include a 'submission' JSON part.", string.Empty, new FormFileCollection());
                }

                var request = JsonSerializer.Deserialize(submissionJson, FormPackageJsonContext.Default.FormSubmissionRequest);
                return new SubmissionParseResult(request, null, submissionJson, form.Files);
            }

            if (context.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            {
                return new SubmissionParseResult(null, "Content-Type must be application/json or multipart/form-data.", string.Empty, new FormFileCollection());
            }

            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            var raw = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
            var jsonRequest = JsonSerializer.Deserialize(raw, FormPackageJsonContext.Default.FormSubmissionRequest);
            return new SubmissionParseResult(jsonRequest, null, raw, new FormFileCollection());
        }
        catch (JsonException)
        {
            return new SubmissionParseResult(null, "Submission JSON was invalid.", string.Empty, new FormFileCollection());
        }
    }

    private static FormSubmissionRequest ApplyHeaderClientId(FormSubmissionRequest request, HttpContext context)
    {
        if (!string.IsNullOrWhiteSpace(request.ClientId))
        {
            return request;
        }

        var clientId = context.Request.Headers[ClientIdHeader].FirstOrDefault();
        return string.IsNullOrWhiteSpace(clientId)
            ? request
            : CloneRequest(request, clientId);
    }

    private static FormSubmissionRequest CloneRequest(FormSubmissionRequest request, string? clientId = null, FormSubmissionAttachmentDescriptor[]? attachments = null)
        => new()
        {
            IdempotencyKey = request.IdempotencyKey,
            FormVersion = request.FormVersion,
            Operation = request.Operation,
            TargetFeatureId = request.TargetFeatureId,
            ClientId = clientId ?? request.ClientId,
            SubmittedAt = request.SubmittedAt,
            Values = request.Values,
            Geometry = request.Geometry,
            Attachments = attachments ?? request.Attachments
        };

    private static FormSubmissionRequest NormalizeAttachmentDescriptors(
        FormSubmissionRequest request,
        IFormFileCollection files)
    {
        if (request.Attachments.Length == 0 || files.Count == 0)
        {
            return request;
        }

        var (byPartName, duplicatePartNames) = BuildFilePartLookup(files);
        var normalized = new FormSubmissionAttachmentDescriptor[request.Attachments.Length];
        for (var i = 0; i < request.Attachments.Length; i++)
        {
            var descriptor = request.Attachments[i];
            var partName = descriptor.PartName;
            if (!string.IsNullOrWhiteSpace(partName) &&
                !duplicatePartNames.Contains(partName) &&
                byPartName.TryGetValue(partName, out var file))
            {
                normalized[i] = new FormSubmissionAttachmentDescriptor
                {
                    ClientAttachmentId = descriptor.ClientAttachmentId,
                    FieldId = descriptor.FieldId,
                    PartName = descriptor.PartName,
                    Filename = string.IsNullOrWhiteSpace(descriptor.Filename) ? file.FileName : descriptor.Filename,
                    ContentType = string.IsNullOrWhiteSpace(descriptor.ContentType) ? file.ContentType : descriptor.ContentType,
                    SizeBytes = file.Length,
                    Sha256 = descriptor.Sha256
                };
            }
            else
            {
                normalized[i] = descriptor;
            }
        }

        return CloneRequest(request, attachments: normalized);
    }

    private async Task<(FormValidationIssue[] Issues, AttachmentOutcomeRecord[] Outcomes)> ValidateAttachmentFilesAsync(
        FormSubmissionRequest request,
        IFormFileCollection files,
        FormPackageVersion packageVersion,
        CancellationToken cancellationToken)
    {
        if (request.Attachments.Length == 0)
        {
            return ([], []);
        }

        var issues = new List<FormValidationIssue>();
        var outcomes = new List<AttachmentOutcomeRecord>();
        var (byPartName, duplicatePartNames) = BuildFilePartLookup(files);
        var policy = packageVersion.Package.AttachmentPolicy;
        var fieldPolicies = BuildAttachmentFieldPolicies(policy);
        var maxSize = Math.Min(policy.MaxAttachmentBytes ?? _attachmentLimits.MaxAttachmentSize, _attachmentLimits.MaxAttachmentSize);

        foreach (var descriptor in request.Attachments)
        {
            var partName = descriptor.PartName;
            if (string.IsNullOrWhiteSpace(partName))
            {
                AddAttachmentIssue(issues, outcomes, descriptor, "attachmentPartMissing", "Attachment multipart part was not provided.");
                continue;
            }

            if (duplicatePartNames.Contains(partName))
            {
                AddAttachmentIssue(issues, outcomes, descriptor, "attachmentPartNameDuplicate", $"Attachment multipart part name '{partName}' appears more than once.");
                continue;
            }

            if (!byPartName.TryGetValue(partName, out var file))
            {
                AddAttachmentIssue(issues, outcomes, descriptor, "attachmentPartMissing", "Attachment multipart part was not provided.");
                continue;
            }

            fieldPolicies.TryGetValue(descriptor.FieldId ?? string.Empty, out var fieldPolicy);
            if (!AttachmentContentTypeAllowed(policy, fieldPolicy, file.ContentType))
            {
                AddAttachmentIssue(issues, outcomes, descriptor, "attachmentContentTypeNotAllowed", $"Attachment content type '{file.ContentType}' is not allowed.");
                continue;
            }

            var validation = await FileUploadSecurity.ValidateFileAsync(
                GetEffectiveFileName(descriptor, file),
                file.ContentType,
                file.Length,
                file.OpenReadStream,
                maxSize,
                _fileUploadOptions.MaxSecurityScanSizeBytes,
                cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                AddAttachmentIssue(issues, outcomes, descriptor, "attachmentSecurityRejected", validation.ErrorMessage ?? "Attachment failed security validation.");
            }
        }

        return (issues.ToArray(), outcomes.ToArray());
    }

    private static void AddAttachmentIssue(
        List<FormValidationIssue> issues,
        List<AttachmentOutcomeRecord> outcomes,
        FormSubmissionAttachmentDescriptor descriptor,
        string code,
        string message)
    {
        issues.Add(new FormValidationIssue
        {
            Code = code,
            Severity = "error",
            FieldId = descriptor.FieldId,
            Path = "attachments",
            Message = message
        });
        var outcome = new FormSubmissionAttachmentOutcome
        {
            ClientAttachmentId = descriptor.ClientAttachmentId,
            FieldId = descriptor.FieldId,
            Status = "rejected",
            Reason = message,
            PrivacyApplied = true
        };
        outcomes.Add(new AttachmentOutcomeRecord(descriptor, outcome));
    }

    private static FormPackageValidationResult MergeValidation(
        FormPackageValidationResult validation,
        FormValidationIssue[] additionalIssues)
    {
        if (additionalIssues.Length == 0)
        {
            return validation;
        }

        var allIssues = validation.Issues.Concat(additionalIssues).ToArray();
        return new FormPackageValidationResult
        {
            IsValid = !allIssues.Any(static issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase)),
            Issues = allIssues
        };
    }

    private static UnifiedEditRequest BuildEditRequest(
        FormPackageDocument package,
        FormSubmissionRequest request,
        MetadataV2Resource resource)
    {
        var attributes = BuildAttributes(package, request, resource);
        var geometry = BuildGeometryWkb(request.Geometry, resource);
        return request.Operation.ToLowerInvariant() switch
        {
            FormSubmissionOperations.Create => UnifiedEditRequest.WithCreates(
                ImmutableArray.Create(EditFeature.ForCreate(geometry, attributes))),
            FormSubmissionOperations.Update => UnifiedEditRequest.WithUpdates(
                ImmutableArray.Create(EditFeature.ForUpdate(request.TargetFeatureId!.Value, geometry, attributes, EditUpdateMode.Partial))),
            FormSubmissionOperations.Delete => UnifiedEditRequest.WithDeletes(ImmutableArray.Create(request.TargetFeatureId!.Value)),
            _ => UnifiedEditRequest.WithCreates(ImmutableArray<EditFeature>.Empty)
        };
    }

    private static ImmutableDictionary<string, object?> BuildAttributes(
        FormPackageDocument package,
        FormSubmissionRequest request,
        MetadataV2Resource resource)
    {
        var targetFields = resource.SchemaFields.ToDictionary(static field => field.Name, StringComparer.OrdinalIgnoreCase);
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in package.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.FieldId) ||
                string.IsNullOrWhiteSpace(field.TargetField) ||
                !request.Values.TryGetValue(field.FieldId, out var value) ||
                !targetFields.TryGetValue(field.TargetField, out var targetField) ||
                IsGeometryField(targetField) ||
                IsAttachmentField(field))
            {
                continue;
            }

            builder[field.TargetField] = ConvertJsonValue(value, targetField.Type);
        }

        return builder.ToImmutable();
    }

    private static object? ConvertJsonValue(JsonElement value, MetadataV2FieldType fieldType)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return fieldType switch
        {
            MetadataV2FieldType.String or MetadataV2FieldType.Uuid or MetadataV2FieldType.Time => value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(),
            MetadataV2FieldType.Integer => value.GetInt32(),
            MetadataV2FieldType.BigInteger => value.GetInt64(),
            MetadataV2FieldType.Double => value.GetDouble(),
            MetadataV2FieldType.Float => value.GetSingle(),
            MetadataV2FieldType.Boolean => value.GetBoolean(),
            MetadataV2FieldType.Date or MetadataV2FieldType.DateTime => value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
                    ? timestamp
                    : value.GetString(),
            MetadataV2FieldType.Json => value.GetRawText(),
            _ => value.GetRawText()
        };
    }

    private static byte[]? BuildGeometryWkb(JsonElement? geometry, MetadataV2Resource resource)
    {
        if (geometry is null)
        {
            return null;
        }

        var element = geometry.Value;
        if (!element.TryGetProperty("x", out var x) || !element.TryGetProperty("y", out var y))
        {
            return null;
        }

        if (!TryReadFiniteDouble(x, out var longitude) || !TryReadFiniteDouble(y, out var latitude))
        {
            return null;
        }

        var srid = resource.Spatial?.SpatialReference?.ResolveSrid() ?? 4326;
        var point = new GeometryFactory(new PrecisionModel(), srid)
            .CreatePoint(new Coordinate(longitude, latitude));
        point.SRID = srid;
        return new WKBWriter(ByteOrder.LittleEndian, handleSRID: true).Write(point);
    }

    private static bool IsGeometryField(MetadataV2Field field)
        => field.Type is MetadataV2FieldType.Geometry or MetadataV2FieldType.Geography;

    private static bool TryReadFiniteDouble(JsonElement value, out double result)
    {
        result = 0;
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetDouble(out result) && double.IsFinite(result);
        }

        return value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
            double.IsFinite(result);
    }

    private static long? ResolveTargetFeatureId(FormSubmissionRequest request, FeatureEditResult editResult)
    {
        if (OperationEquals(request.Operation, FormSubmissionOperations.Create))
        {
            if (!editResult.CreatedIds.IsDefaultOrEmpty)
            {
                return editResult.CreatedIds[0];
            }

            if (!editResult.CreateResults.IsDefaultOrEmpty)
            {
                return editResult.CreateResults[0].ObjectId;
            }
        }

        return request.TargetFeatureId;
    }

    private static bool ShouldUploadAttachments(FormSubmissionRequest request, FeatureEditResult editResult)
        => editResult.IsSuccess &&
           request.Attachments.Length > 0 &&
           !OperationEquals(request.Operation, FormSubmissionOperations.Delete);

    private async Task<FormSubmissionAttachmentOutcome[]> UploadAttachmentsAsync(
        HttpContext context,
        FormPackageVersion packageVersion,
        FormSubmissionRequest request,
        IFormFileCollection files,
        int storageLayerId,
        long? targetFeatureId,
        Guid submissionId,
        CancellationToken cancellationToken)
    {
        if (request.Attachments.Length == 0)
        {
            return [];
        }

        var outcomes = new List<FormSubmissionAttachmentOutcome>(request.Attachments.Length);
        var (byPartName, duplicatePartNames) = BuildFilePartLookup(files);
        if (targetFeatureId is null)
        {
            foreach (var descriptor in request.Attachments)
            {
                outcomes.Add(await AddAttachmentOutcomeAsync(context, submissionId, packageVersion, request, descriptor, "failed", null, "Target feature id was not available.", cancellationToken)
                    .ConfigureAwait(false));
            }

            return outcomes.ToArray();
        }

        var existing = await _attachmentStore.ListAsync(storageLayerId, targetFeatureId.Value, cancellationToken).ConfigureAwait(false);
        if (existing.Length + request.Attachments.Length > _attachmentLimits.MaxAttachmentsPerFeature)
        {
            foreach (var descriptor in request.Attachments)
            {
                outcomes.Add(await AddAttachmentOutcomeAsync(context, submissionId, packageVersion, request, descriptor, "rejected", null, "Feature attachment count limit would be exceeded.", cancellationToken)
                    .ConfigureAwait(false));
            }

            return outcomes.ToArray();
        }

        foreach (var descriptor in request.Attachments)
        {
            var partName = descriptor.PartName;
            if (string.IsNullOrWhiteSpace(partName) ||
                duplicatePartNames.Contains(partName) ||
                !byPartName.TryGetValue(partName, out var file))
            {
                outcomes.Add(await AddAttachmentOutcomeAsync(context, submissionId, packageVersion, request, descriptor, "rejected", null, "Attachment multipart part was not provided.", cancellationToken)
                    .ConfigureAwait(false));
                continue;
            }

            try
            {
                await using var stream = file.OpenReadStream();
                var attachment = await _attachmentStore.UploadAsync(
                    storageLayerId,
                    targetFeatureId.Value,
                    FileUploadSecurity.SanitizeFileName(GetEffectiveFileName(descriptor, file)),
                    descriptor.ContentType ?? file.ContentType,
                    stream,
                    descriptor.FieldId,
                    cancellationToken).ConfigureAwait(false);

                outcomes.Add(await AddAttachmentOutcomeAsync(context, submissionId, packageVersion, request, descriptor, "accepted", attachment.Id, null, cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                FormSubmissionLog.AttachmentUploadFailed(_logger, ex, packageVersion.FormId, packageVersion.Version, submissionId, descriptor.FieldId ?? string.Empty);
                outcomes.Add(await AddAttachmentOutcomeAsync(context, submissionId, packageVersion, request, descriptor, "failed", null, "Attachment could not be persisted.", cancellationToken)
                    .ConfigureAwait(false));
            }
        }

        return outcomes.ToArray();
    }

    private async Task<FormSubmissionAttachmentOutcome> AddAttachmentOutcomeAsync(
        HttpContext context,
        Guid submissionId,
        FormPackageVersion packageVersion,
        FormSubmissionRequest request,
        FormSubmissionAttachmentDescriptor descriptor,
        string status,
        long? attachmentId,
        string? reason,
        CancellationToken cancellationToken)
    {
        _ = request;
        var outcome = new FormSubmissionAttachmentOutcome
        {
            ClientAttachmentId = descriptor.ClientAttachmentId,
            FieldId = descriptor.FieldId,
            Status = status,
            AttachmentId = attachmentId,
            Reason = reason,
            PrivacyApplied = true
        };
        await RecordAttachmentOutcomeAsync(context, submissionId, packageVersion, descriptor, outcome, cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    private async Task RecordAttachmentOutcomeAsync(
        HttpContext context,
        Guid submissionId,
        FormPackageVersion packageVersion,
        FormSubmissionAttachmentDescriptor descriptor,
        FormSubmissionAttachmentOutcome outcome,
        CancellationToken cancellationToken)
    {
        await _store.RecordAttachmentOutcomeAsync(submissionId, descriptor, outcome, packageVersion, cancellationToken)
            .ConfigureAwait(false);
        await RecordAuditAsync(context, "forms.attachment.policy", packageVersion.FormId, AuditOutcome.Success, $"{{\"version\":{packageVersion.Version},\"status\":\"{outcome.Status}\"}}", cancellationToken)
            .ConfigureAwait(false);
        FormSubmissionLog.AttachmentPolicyRecorded(_logger, packageVersion.FormId, packageVersion.Version, submissionId, outcome.FieldId ?? string.Empty, outcome.Status);
    }

    private static FormSubmissionResponse BuildRejectedResponse(
        Guid submissionId,
        FormPackageVersion packageVersion,
        FormSubmissionRequest request,
        FormValidationIssue[] issues,
        bool retryable,
        FormSubmissionAttachmentOutcome[]? attachmentOutcomes = null)
        => new()
        {
            SubmissionId = submissionId,
            Status = "rejected",
            FormId = packageVersion.FormId,
            FormVersion = packageVersion.Version,
            Operation = request.Operation,
            TargetFeatureId = request.TargetFeatureId,
            AttachmentOutcomes = attachmentOutcomes ?? [],
            ValidationIssues = issues,
            Retry = new FormSubmissionRetryGuidance
            {
                Retryable = retryable,
                Reason = retryable ? "Submission policy denied this operation." : "Submission must be corrected before retry."
            }
        };

    private static FormSubmissionResponse BuildFailedResponse(
        Guid submissionId,
        FormPackageVersion packageVersion,
        FormSubmissionRequest request,
        string retryReason)
        => new()
        {
            SubmissionId = submissionId,
            Status = "failed",
            FormId = packageVersion.FormId,
            FormVersion = packageVersion.Version,
            Operation = request.Operation,
            TargetFeatureId = request.TargetFeatureId,
            EditOutcome = new FormEditOutcome
            {
                Succeeded = false,
                Error = "Submission could not be applied."
            },
            Retry = new FormSubmissionRetryGuidance
            {
                Retryable = true,
                Reason = retryReason
            }
        };

    private static AttachmentOutcomeRecord[] BuildRejectedAttachmentOutcomes(
        FormSubmissionRequest request,
        FormValidationIssue[] issues,
        AttachmentOutcomeRecord[] existingOutcomes)
    {
        if (request.Attachments.Length == 0)
        {
            return existingOutcomes;
        }

        var outcomes = existingOutcomes.ToList();
        foreach (var descriptor in request.Attachments)
        {
            if (outcomes.Any(outcome => ReferenceEquals(outcome.Descriptor, descriptor)))
            {
                continue;
            }

            var issue = issues.FirstOrDefault(issue =>
                IsAttachmentIssue(issue) &&
                (string.Equals(issue.FieldId, descriptor.FieldId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(issue.Path, "attachments", StringComparison.OrdinalIgnoreCase)));
            if (issue is null)
            {
                continue;
            }

            outcomes.Add(new AttachmentOutcomeRecord(
                descriptor,
                new FormSubmissionAttachmentOutcome
                {
                    ClientAttachmentId = descriptor.ClientAttachmentId,
                    FieldId = descriptor.FieldId,
                    Status = "rejected",
                    Reason = issue.Message,
                    PrivacyApplied = true
                }));
        }

        return outcomes.ToArray();
    }

    private static bool IsAttachmentIssue(FormValidationIssue issue)
        => issue.Code.StartsWith("attachment", StringComparison.OrdinalIgnoreCase) ||
           issue.Code.StartsWith("requiredAttachment", StringComparison.OrdinalIgnoreCase);

    private static FormSubmissionResponse CloneReplayResponse(FormSubmissionResponse response)
        => new()
        {
            SubmissionId = response.SubmissionId,
            Status = response.Status,
            FormId = response.FormId,
            FormVersion = response.FormVersion,
            Operation = response.Operation,
            TargetFeatureId = response.TargetFeatureId,
            EditOutcome = response.EditOutcome,
            AttachmentOutcomes = response.AttachmentOutcomes,
            ValidationIssues = response.ValidationIssues,
            Retry = response.Retry,
            IdempotentReplay = true
        };

    private static bool IsAttachmentField(FormFieldDefinition field)
        => string.Equals(field.Type, "attachment", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(field.Type, "media", StringComparison.OrdinalIgnoreCase);

    private static string GetEffectiveFileName(FormSubmissionAttachmentDescriptor descriptor, IFormFile file)
        => string.IsNullOrWhiteSpace(descriptor.Filename) ? file.FileName : descriptor.Filename!;

    private static (Dictionary<string, IFormFile> ByPartName, HashSet<string> DuplicatePartNames) BuildFilePartLookup(
        IFormFileCollection files)
    {
        var byPartName = new Dictionary<string, IFormFile>(StringComparer.Ordinal);
        var duplicatePartNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Name))
            {
                continue;
            }

            if (!byPartName.TryAdd(file.Name, file))
            {
                duplicatePartNames.Add(file.Name);
            }
        }

        return (byPartName, duplicatePartNames);
    }

    private bool AttachmentContentTypeAllowed(
        FormAttachmentPolicy policy,
        FormFieldAttachmentPolicy? fieldPolicy,
        string contentType)
    {
        if (fieldPolicy?.AllowedContentTypes.Length > 0 &&
            !ContentTypeAllowed(fieldPolicy.AllowedContentTypes, contentType))
        {
            return false;
        }

        if (policy.AllowedContentTypes.Length > 0 &&
            !ContentTypeAllowed(policy.AllowedContentTypes, contentType))
        {
            return false;
        }

        return ContentTypeAllowed(SplitAllowedMimeTypes(_attachmentLimits.AllowedMimeTypes), contentType);
    }

    private static Dictionary<string, FormFieldAttachmentPolicy> BuildAttachmentFieldPolicies(FormAttachmentPolicy policy)
        => policy.Fields
            .Where(static fieldPolicy => !string.IsNullOrWhiteSpace(fieldPolicy.FieldId))
            .GroupBy(static fieldPolicy => fieldPolicy.FieldId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

    private static bool OperationEquals(string operation, string expected)
        => string.Equals(operation, expected, StringComparison.OrdinalIgnoreCase);

    private static bool ContentTypeAllowed(IEnumerable<string> allowedContentTypes, string contentType)
    {
        var normalized = contentType.Split(';', 2)[0].Trim();
        foreach (var allowed in allowedContentTypes)
        {
            if (string.IsNullOrWhiteSpace(allowed))
            {
                continue;
            }

            var pattern = allowed.Trim();
            if (string.Equals(pattern, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (pattern.EndsWith("/*", StringComparison.Ordinal) &&
                normalized.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] SplitAllowedMimeTypes(string allowedMimeTypes)
        => allowedMimeTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ResolveActor(HttpContext context)
        => context.User.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? context.User.Identity?.Name
           ?? AuditEvent.AnonymousActor;

    private Task RecordAuditAsync(
        HttpContext context,
        string action,
        string formId,
        AuditOutcome outcome,
        string details,
        CancellationToken cancellationToken)
        => _auditLog.RecordAsync(new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.AdminAction,
            Actor = ResolveActor(context),
            ActorType = context.User.Identity?.IsAuthenticated == true ? AuditActorType.UserId : AuditActorType.Anonymous,
            ResourceType = "form_package",
            ResourceId = formId,
            Action = action,
            Outcome = outcome,
            CorrelationId = context.TraceIdentifier,
            RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            Details = details
        }, cancellationToken);

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record SubmissionParseResult(
        FormSubmissionRequest? Request,
        string? Error,
        string RawRequest,
        IFormFileCollection Files);

    private readonly record struct AttachmentOutcomeRecord(
        FormSubmissionAttachmentDescriptor Descriptor,
        FormSubmissionAttachmentOutcome Outcome);
}

internal static partial class FormSubmissionLog
{
    [LoggerMessage(EventId = 118440, Level = LogLevel.Information, Message = "Accepted form submission {SubmissionId} for {FormId} version {Version}; operation={Operation}, attachmentCount={AttachmentCount}.")]
    public static partial void SubmissionAccepted(ILogger logger, string formId, int version, Guid submissionId, string operation, int attachmentCount);

    [LoggerMessage(EventId = 118441, Level = LogLevel.Warning, Message = "Form submission {SubmissionId} for {FormId} version {Version} failed.")]
    public static partial void SubmissionFailed(ILogger logger, Exception exception, string formId, int version, Guid submissionId);

    [LoggerMessage(EventId = 118442, Level = LogLevel.Warning, Message = "Attachment upload failed for form submission {SubmissionId}; form={FormId}, version={Version}, field={FieldId}.")]
    public static partial void AttachmentUploadFailed(ILogger logger, Exception exception, string formId, int version, Guid submissionId, string fieldId);

    [LoggerMessage(EventId = 118443, Level = LogLevel.Information, Message = "Recorded attachment policy outcome for form submission {SubmissionId}; form={FormId}, version={Version}, field={FieldId}, status={Status}.")]
    public static partial void AttachmentPolicyRecorded(ILogger logger, string formId, int version, Guid submissionId, string fieldId, string status);

    [LoggerMessage(EventId = 118444, Level = LogLevel.Warning, Message = "Form submission {SubmissionId} for {FormId} version {Version} failed during edit application; operation={Operation}.")]
    public static partial void SubmissionEditFailed(ILogger logger, string formId, int version, Guid submissionId, string operation);
}
