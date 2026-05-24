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
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Forms.Packages;
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
    private readonly IFormPackageStore _store;
    private readonly FormPackageValidator _validator;
    private readonly ILayerCatalog _catalog;
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
        ILayerCatalog catalog,
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
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
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

        var service = await _catalog.GetServiceAsync(packageVersion.Package.Target?.ServiceId ?? string.Empty, context.RequestAborted)
            .ConfigureAwait(false);
        var layer = service?.GetLayer(packageVersion.Package.Target?.LayerId ?? -1);
        if (service is null || layer is null)
        {
            return ProblemDetailsHelpers.CreateAdminProblem(context, StatusCodes.Status404NotFound, "Target service or layer for the form package was not found.");
        }

        var authorizationFailure = await ServiceDataEditorAuthorization.RequireServiceDataEditorAsync(
            context,
            service,
            layer,
            context.RequestAborted).ConfigureAwait(false);
        if (authorizationFailure is not null)
        {
            await RecordAuditAsync(context, "forms.submission.create", packageVersion.FormId, AuditOutcome.Denied, $"{{\"version\":{packageVersion.Version}}}")
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
        }

        var submissionId = Guid.NewGuid();
        await _store.CreateSubmissionAsync(
            submissionId,
            request.IdempotencyKey,
            actorHash,
            requestHash,
            packageVersion,
            request,
            "pending",
            context.RequestAborted).ConfigureAwait(false);

        var validation = await _validator.ValidateSubmissionAsync(packageVersion, request, context.RequestAborted)
            .ConfigureAwait(false);
        var fileValidation = await ValidateAttachmentFilesAsync(request, parseResult.Files, packageVersion, context.RequestAborted)
            .ConfigureAwait(false);
        validation = MergeValidation(validation, fileValidation.Issues);

        if (!validation.IsValid)
        {
            foreach (var outcome in fileValidation.Outcomes)
            {
                await RecordAttachmentOutcomeAsync(context, submissionId, packageVersion, request, outcome).ConfigureAwait(false);
            }

            var statusCode = validation.Issues.Any(static issue =>
                string.Equals(issue.Code, "operationNotAllowed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(issue.Code, "attachmentsNotAllowed", StringComparison.OrdinalIgnoreCase))
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status400BadRequest;
            var rejected = BuildRejectedResponse(submissionId, packageVersion, request, validation.Issues, statusCode == StatusCodes.Status403Forbidden);
            await _store.CompleteSubmissionAsync(submissionId, rejected, "rejected", context.RequestAborted).ConfigureAwait(false);
            await RecordAuditAsync(context, "forms.submission.create", packageVersion.FormId, AuditOutcome.Failure, $"{{\"version\":{packageVersion.Version},\"issueCount\":{validation.Issues.Length}}}")
                .ConfigureAwait(false);
            return Results.Json(rejected, FormPackageJsonContext.Default.FormSubmissionResponse, statusCode: statusCode);
        }

        try
        {
            var editRequest = BuildEditRequest(packageVersion.Package, request, layer);
            var editValidation = _editProcessor.ValidateEdit(editRequest, layer);
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
                await _store.CompleteSubmissionAsync(submissionId, rejected, "rejected", context.RequestAborted).ConfigureAwait(false);
                return Results.Json(rejected, FormPackageJsonContext.Default.FormSubmissionResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            var optimized = _editProcessor.OptimizeEdit(editRequest, layer);
            var batch = _editProcessor.ToFeatureEditBatch(optimized, layer);
            var editResult = await _featureWriter.ApplyEditsAsync(layer.Id, batch, context.RequestAborted).ConfigureAwait(false);
            var targetFeatureId = ResolveTargetFeatureId(request, editResult);
            var attachmentOutcomes = await UploadAttachmentsAsync(context, packageVersion, request, parseResult.Files, layer, targetFeatureId, submissionId)
                .ConfigureAwait(false);
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
                AttachmentOutcomes = attachmentOutcomes
            };

            await _store.CompleteSubmissionAsync(submissionId, response, response.Status, context.RequestAborted).ConfigureAwait(false);
            await RecordAuditAsync(context, "forms.submission.create", packageVersion.FormId, AuditOutcome.Success, $"{{\"version\":{packageVersion.Version},\"operation\":\"{request.Operation}\"}}")
                .ConfigureAwait(false);
            FormSubmissionLog.SubmissionAccepted(_logger, packageVersion.FormId, packageVersion.Version, submissionId, request.Operation, attachmentOutcomes.Length);
            return Results.Json(response, FormPackageJsonContext.Default.FormSubmissionResponse);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FormSubmissionLog.SubmissionFailed(_logger, ex, packageVersion.FormId, packageVersion.Version, submissionId);
            var response = new FormSubmissionResponse
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
                    Reason = "The server could not complete the submission."
                }
            };
            await _store.CompleteSubmissionAsync(submissionId, response, "failed", context.RequestAborted).ConfigureAwait(false);
            await RecordAuditAsync(context, "forms.submission.create", packageVersion.FormId, AuditOutcome.Failure, $"{{\"version\":{packageVersion.Version}}}")
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

        var byPartName = files.ToDictionary(static file => file.Name, StringComparer.Ordinal);
        var normalized = new FormSubmissionAttachmentDescriptor[request.Attachments.Length];
        for (var i = 0; i < request.Attachments.Length; i++)
        {
            var descriptor = request.Attachments[i];
            if (!string.IsNullOrWhiteSpace(descriptor.PartName) &&
                byPartName.TryGetValue(descriptor.PartName, out var file))
            {
                normalized[i] = new FormSubmissionAttachmentDescriptor
                {
                    ClientAttachmentId = descriptor.ClientAttachmentId,
                    FieldId = descriptor.FieldId,
                    PartName = descriptor.PartName,
                    Filename = string.IsNullOrWhiteSpace(descriptor.Filename) ? file.FileName : descriptor.Filename,
                    ContentType = string.IsNullOrWhiteSpace(descriptor.ContentType) ? file.ContentType : descriptor.ContentType,
                    SizeBytes = descriptor.SizeBytes ?? file.Length,
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

    private async Task<(FormValidationIssue[] Issues, FormSubmissionAttachmentOutcome[] Outcomes)> ValidateAttachmentFilesAsync(
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
        var outcomes = new List<FormSubmissionAttachmentOutcome>();
        var byPartName = files.ToDictionary(static file => file.Name, StringComparer.Ordinal);
        var policy = packageVersion.Package.AttachmentPolicy;
        var maxSize = Math.Min(policy.MaxAttachmentBytes ?? _attachmentLimits.MaxAttachmentSize, _attachmentLimits.MaxAttachmentSize);

        foreach (var descriptor in request.Attachments)
        {
            if (string.IsNullOrWhiteSpace(descriptor.PartName) ||
                !byPartName.TryGetValue(descriptor.PartName, out var file))
            {
                AddAttachmentIssue(issues, outcomes, descriptor, "attachmentPartMissing", "Attachment multipart part was not provided.");
                continue;
            }

            if (!ContentTypeAllowed(policy.AllowedContentTypes, file.ContentType) &&
                !ContentTypeAllowed(SplitAllowedMimeTypes(_attachmentLimits.AllowedMimeTypes), file.ContentType))
            {
                AddAttachmentIssue(issues, outcomes, descriptor, "attachmentContentTypeNotAllowed", $"Attachment content type '{file.ContentType}' is not allowed.");
                continue;
            }

            var validation = await FileUploadSecurity.ValidateFileAsync(
                file,
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
        List<FormSubmissionAttachmentOutcome> outcomes,
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
        outcomes.Add(new FormSubmissionAttachmentOutcome
        {
            ClientAttachmentId = descriptor.ClientAttachmentId,
            FieldId = descriptor.FieldId,
            Status = "rejected",
            Reason = message,
            PrivacyApplied = true
        });
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

    private static UnifiedEditRequest BuildEditRequest(FormPackageDocument package, FormSubmissionRequest request, LayerDefinition layer)
    {
        var attributes = BuildAttributes(package, request, layer);
        var geometry = BuildGeometryWkb(request.Geometry, layer);
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
        LayerDefinition layer)
    {
        var targetFields = layer.Fields.ToDictionary(static field => field.Name, StringComparer.OrdinalIgnoreCase);
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in package.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.FieldId) ||
                string.IsNullOrWhiteSpace(field.TargetField) ||
                !request.Values.TryGetValue(field.FieldId, out var value) ||
                !targetFields.TryGetValue(field.TargetField, out var targetField) ||
                targetField.IsGeometry ||
                IsAttachmentField(field))
            {
                continue;
            }

            builder[field.TargetField] = ConvertJsonValue(value, targetField.Type);
        }

        return builder.ToImmutable();
    }

    private static object? ConvertJsonValue(JsonElement value, FieldType fieldType)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return fieldType switch
        {
            FieldType.String or FieldType.Uuid or FieldType.Time => value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(),
            FieldType.Integer => value.TryGetInt32(out var intValue) ? intValue : value.GetInt64(),
            FieldType.BigInteger => value.GetInt64(),
            FieldType.Double => value.GetDouble(),
            FieldType.Float => (float)value.GetDouble(),
            FieldType.Boolean => value.GetBoolean(),
            FieldType.Date or FieldType.DateTime => value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
                    ? timestamp
                    : value.GetString(),
            FieldType.Json => value.GetRawText(),
            _ => value.GetRawText()
        };
    }

    private static byte[]? BuildGeometryWkb(JsonElement? geometry, LayerDefinition layer)
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

        var point = new GeometryFactory(new PrecisionModel(), layer.SpatialReference.Wkid)
            .CreatePoint(new Coordinate(ReadDouble(x), ReadDouble(y)));
        point.SRID = layer.SpatialReference.Wkid;
        return new WKBWriter(ByteOrder.LittleEndian, handleSRID: true).Write(point);
    }

    private static double ReadDouble(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? double.Parse(value.GetString() ?? "0", CultureInfo.InvariantCulture)
            : value.GetDouble();

    private static long? ResolveTargetFeatureId(FormSubmissionRequest request, FeatureEditResult editResult)
    {
        if (request.Operation == FormSubmissionOperations.Create)
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

    private async Task<FormSubmissionAttachmentOutcome[]> UploadAttachmentsAsync(
        HttpContext context,
        FormPackageVersion packageVersion,
        FormSubmissionRequest request,
        IFormFileCollection files,
        LayerDefinition layer,
        long? targetFeatureId,
        Guid submissionId)
    {
        if (request.Attachments.Length == 0)
        {
            return [];
        }

        var outcomes = new List<FormSubmissionAttachmentOutcome>(request.Attachments.Length);
        var byPartName = files.ToDictionary(static file => file.Name, StringComparer.Ordinal);
        if (targetFeatureId is null)
        {
            foreach (var descriptor in request.Attachments)
            {
                outcomes.Add(await AddAttachmentOutcomeAsync(context, submissionId, packageVersion, request, descriptor, "failed", null, "Target feature id was not available.")
                    .ConfigureAwait(false));
            }

            return outcomes.ToArray();
        }

        var existing = await _attachmentStore.ListAsync(layer.Id, targetFeatureId.Value, context.RequestAborted).ConfigureAwait(false);
        if (existing.Length + request.Attachments.Length > _attachmentLimits.MaxAttachmentsPerFeature)
        {
            foreach (var descriptor in request.Attachments)
            {
                outcomes.Add(await AddAttachmentOutcomeAsync(context, submissionId, packageVersion, request, descriptor, "rejected", null, "Feature attachment count limit would be exceeded.")
                    .ConfigureAwait(false));
            }

            return outcomes.ToArray();
        }

        foreach (var descriptor in request.Attachments)
        {
            if (string.IsNullOrWhiteSpace(descriptor.PartName) ||
                !byPartName.TryGetValue(descriptor.PartName, out var file))
            {
                outcomes.Add(await AddAttachmentOutcomeAsync(context, submissionId, packageVersion, request, descriptor, "rejected", null, "Attachment multipart part was not provided.")
                    .ConfigureAwait(false));
                continue;
            }

            try
            {
                await using var stream = file.OpenReadStream();
                var attachment = await _attachmentStore.UploadAsync(
                    layer.Id,
                    targetFeatureId.Value,
                    FileUploadSecurity.SanitizeFileName(descriptor.Filename ?? file.FileName),
                    descriptor.ContentType ?? file.ContentType,
                    stream,
                    descriptor.FieldId,
                    context.RequestAborted).ConfigureAwait(false);

                outcomes.Add(await AddAttachmentOutcomeAsync(context, submissionId, packageVersion, request, descriptor, "accepted", attachment.Id, null)
                    .ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                FormSubmissionLog.AttachmentUploadFailed(_logger, ex, packageVersion.FormId, packageVersion.Version, submissionId, descriptor.FieldId ?? string.Empty);
                outcomes.Add(await AddAttachmentOutcomeAsync(context, submissionId, packageVersion, request, descriptor, "failed", null, "Attachment could not be persisted.")
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
        string? reason)
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
        await RecordAttachmentOutcomeAsync(context, submissionId, packageVersion, request, outcome).ConfigureAwait(false);
        return outcome;
    }

    private async Task RecordAttachmentOutcomeAsync(
        HttpContext context,
        Guid submissionId,
        FormPackageVersion packageVersion,
        FormSubmissionRequest request,
        FormSubmissionAttachmentOutcome outcome)
    {
        var descriptor = request.Attachments.FirstOrDefault(attachment =>
            string.Equals(attachment.ClientAttachmentId, outcome.ClientAttachmentId, StringComparison.Ordinal) &&
            string.Equals(attachment.FieldId, outcome.FieldId, StringComparison.OrdinalIgnoreCase))
            ?? new FormSubmissionAttachmentDescriptor
            {
                ClientAttachmentId = outcome.ClientAttachmentId,
                FieldId = outcome.FieldId
            };
        await _store.RecordAttachmentOutcomeAsync(submissionId, descriptor, outcome, packageVersion, context.RequestAborted)
            .ConfigureAwait(false);
        await RecordAuditAsync(context, "forms.attachment.policy", packageVersion.FormId, AuditOutcome.Success, $"{{\"version\":{packageVersion.Version},\"status\":\"{outcome.Status}\"}}")
            .ConfigureAwait(false);
        FormSubmissionLog.AttachmentPolicyRecorded(_logger, packageVersion.FormId, packageVersion.Version, submissionId, outcome.FieldId ?? string.Empty, outcome.Status);
    }

    private static FormSubmissionResponse BuildRejectedResponse(
        Guid submissionId,
        FormPackageVersion packageVersion,
        FormSubmissionRequest request,
        FormValidationIssue[] issues,
        bool retryable)
        => new()
        {
            SubmissionId = submissionId,
            Status = "rejected",
            FormId = packageVersion.FormId,
            FormVersion = packageVersion.Version,
            Operation = request.Operation,
            TargetFeatureId = request.TargetFeatureId,
            ValidationIssues = issues,
            Retry = new FormSubmissionRetryGuidance
            {
                Retryable = retryable,
                Reason = retryable ? "Submission policy denied this operation." : "Submission must be corrected before retry."
            }
        };

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
        string details)
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
        }, context.RequestAborted);

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record SubmissionParseResult(
        FormSubmissionRequest? Request,
        string? Error,
        string RawRequest,
        IFormFileCollection Files);
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
}
