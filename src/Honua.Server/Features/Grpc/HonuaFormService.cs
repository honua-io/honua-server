// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Grpc.Core;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Forms.Abstractions;
using Honua.Core.Features.Forms.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using Proto = Honua.Server.Features.Grpc.Proto;

namespace Honua.Server.Features.Grpc;

/// <summary>
/// gRPC service implementation for form definition and submission.
/// Provides type-safe form operations with real-time collaboration.
/// </summary>
internal sealed class HonuaFormService : Proto.FormService.FormServiceBase
{
    private readonly IResourceValidator _resourceValidator;
    private readonly IFormDefinitionStore _formStore;
    private readonly IFormCollaborationManager _collaborationManager;
    private readonly IFormValidationService _formValidator;
    private readonly IFeatureWriter _featureWriter;
    private readonly ILogger<HonuaFormService> _logger;
    private readonly FormServiceOptions _options;

    // In-memory collaboration sessions (in production, use Redis/SignalR)
    private readonly ConcurrentDictionary<string, CollaborationSession> _activeSessions = new();

    public HonuaFormService(
        IResourceValidator resourceValidator,
        IFormDefinitionStore formStore,
        IFormCollaborationManager collaborationManager,
        IFormValidationService formValidator,
        IFeatureWriter featureWriter,
        IOptions<FormServiceOptions> options,
        ILogger<HonuaFormService> logger)
    {
        _resourceValidator = resourceValidator;
        _formStore = formStore;
        _collaborationManager = collaborationManager;
        _formValidator = formValidator;
        _featureWriter = featureWriter;
        _options = options.Value;
        _logger = logger;
    }

    public override async Task<Proto.GetFormDefinitionResponse> GetFormDefinition(
        Proto.GetFormDefinitionRequest request,
        ServerCallContext context)
    {
        try
        {
            _logger.LogInformation("Getting form definition for form {FormId}", request.FormId);

            // Validate access to target service/layer
            var validation = await _resourceValidator.ValidateServiceLayerAsync(
                request.ServiceId, request.LayerId, context.CancellationToken);

            if (!validation.IsValid)
            {
                throw new RpcException(new Status(
                    validation.ErrorCode == ResourceValidationError.NotFound
                        ? StatusCode.NotFound
                        : StatusCode.InvalidArgument,
                    validation.ErrorMessage ?? "Resource validation failed"));
            }

            var (service, layer) = validation.Resource!;
            EnsureGrpcEnabled(service);

            // Get form definition
            var formDefinition = await _formStore.GetFormDefinitionAsync(
                request.FormId, request.Version, context.CancellationToken);

            if (formDefinition == null)
            {
                throw new RpcException(new Status(StatusCode.NotFound,
                    $"Form '{request.FormId}' not found"));
            }

            // Apply mobile optimizations
            var optimizedForm = await ApplyMobileOptimizationsAsync(
                formDefinition, request.MobileCapabilities, layer);

            var response = new Proto.GetFormDefinitionResponse
            {
                Form = FormGrpcConverters.ToProtoFormDefinition(optimizedForm),
                Metadata = FormGrpcConverters.ToProtoFormMetadata(formDefinition.Metadata),
                MobileOptimizations = CreateMobileOptimizations(request.MobileCapabilities)
            };

            // Add validation rules
            var validationRules = await _formValidator.GetValidationRulesAsync(
                formDefinition.FormId, context.CancellationToken);
            response.ValidationRules.AddRange(
                validationRules.Select(FormGrpcConverters.ToProtoValidationRule));

            _logger.LogInformation("Successfully returned form definition for {FormId} with {ControlCount} controls",
                request.FormId, optimizedForm.Controls.Count);

            return response;
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get form definition for {FormId}", request.FormId);
            throw new RpcException(new Status(StatusCode.Internal, "Failed to retrieve form definition"));
        }
    }

    public override async Task<Proto.SubmitFormDataResponse> SubmitFormData(
        Proto.SubmitFormDataRequest request,
        ServerCallContext context)
    {
        try
        {
            _logger.LogInformation("Submitting form data for form {FormId}, instance {InstanceId}",
                request.FormId, request.Instance.InstanceId);

            // Get form definition for validation
            var formDefinition = await _formStore.GetFormDefinitionAsync(
                request.FormId, request.FormVersion, context.CancellationToken);

            if (formDefinition == null)
            {
                throw new RpcException(new Status(StatusCode.NotFound,
                    $"Form '{request.FormId}' not found"));
            }

            // Validate target service/layer access
            var validation = await _resourceValidator.ValidateServiceLayerAsync(
                formDefinition.TargetServiceId, formDefinition.TargetLayerId, context.CancellationToken);

            if (!validation.IsValid)
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied,
                    "Access denied to target service/layer"));
            }

            var (service, layer) = validation.Resource!;
            EnsureGrpcEnabled(service);

            // Validate form data
            var validationResult = await _formValidator.ValidateFormInstanceAsync(
                formDefinition, request.Instance, context.CancellationToken);

            var response = new Proto.SubmitFormDataResponse
            {
                Result = new Proto.SubmissionResult
                {
                    Success = validationResult.IsValid,
                    Message = validationResult.IsValid ? "Form submitted successfully" : "Validation failed",
                    ServerTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            };

            // Add validation issues
            response.ValidationIssues.AddRange(
                validationResult.Issues.Select(FormGrpcConverters.ToProtoValidationIssue));

            if (!validationResult.IsValid)
            {
                _logger.LogWarning("Form validation failed for {FormId}: {IssueCount} issues",
                    request.FormId, validationResult.Issues.Count);
                return response;
            }

            // Convert form data to feature for insertion
            var feature = await ConvertFormToFeatureAsync(
                formDefinition, request.Instance, request.Attachments, layer);

            // Submit as feature edit
            var editBatch = new FeatureEditBatch
            {
                Adds = new[] { feature }
            };

            var editResult = await _featureWriter.ApplyEditsAsync(
                formDefinition.TargetLayerId, editBatch, context.CancellationToken);

            if (editResult.AddResults.Any() && editResult.AddResults.First().Success)
            {
                response.CreatedFeatureId = editResult.AddResults.First().ObjectId;
                response.Result.Success = true;

                _logger.LogInformation("Successfully submitted form {FormId} as feature {FeatureId}",
                    request.FormId, response.CreatedFeatureId);
            }
            else
            {
                response.Result.Success = false;
                response.Result.Message = "Failed to create feature from form data";

                _logger.LogError("Failed to create feature from form {FormId}", request.FormId);
            }

            return response;
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit form data for {FormId}", request.FormId);
            throw new RpcException(new Status(StatusCode.Internal, "Form submission failed"));
        }
    }

    public override async Task StreamFormUpdates(
        IAsyncStreamReader<Proto.FormUpdateRequest> requestStream,
        IServerStreamWriter<Proto.FormUpdateResponse> responseStream,
        ServerCallContext context)
    {
        var sessionId = "";
        CollaborationSession? session = null;

        try
        {
            await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken))
            {
                if (string.IsNullOrEmpty(sessionId))
                {
                    sessionId = request.SessionId;
                    session = await GetOrCreateCollaborationSessionAsync(sessionId, request.FormId);

                    _logger.LogInformation("User joined collaboration session {SessionId} for form {FormId}",
                        sessionId, request.FormId);
                }

                // Process the update
                var response = await ProcessFormUpdateAsync(session!, request, context.CancellationToken);

                // Broadcast to all participants
                await BroadcastToSessionParticipantsAsync(session!, response, context.CancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Collaboration stream cancelled for session {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in collaboration stream for session {SessionId}", sessionId);
            throw new RpcException(new Status(StatusCode.Internal, "Collaboration stream error"));
        }
        finally
        {
            if (session != null && !string.IsNullOrEmpty(sessionId))
            {
                await RemoveFromCollaborationSessionAsync(sessionId, context);
            }
        }
    }

    public override async Task<Proto.ValidateFormDataResponse> ValidateFormData(
        Proto.ValidateFormDataRequest request,
        ServerCallContext context)
    {
        try
        {
            var formDefinition = await _formStore.GetFormDefinitionAsync(
                request.FormId, version: null, context.CancellationToken);

            if (formDefinition == null)
            {
                throw new RpcException(new Status(StatusCode.NotFound,
                    $"Form '{request.FormId}' not found"));
            }

            var validationResult = await _formValidator.ValidateFormInstanceAsync(
                formDefinition, request.Instance, context.CancellationToken);

            var response = new Proto.ValidateFormDataResponse
            {
                IsValid = validationResult.IsValid
            };

            response.Issues.AddRange(
                validationResult.Issues.Select(FormGrpcConverters.ToProtoValidationIssue));

            return response;
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate form data for {FormId}", request.FormId);
            throw new RpcException(new Status(StatusCode.Internal, "Form validation failed"));
        }
    }

    public override async Task<Proto.GetFormMetadataResponse> GetFormMetadata(
        Proto.GetFormMetadataRequest request,
        ServerCallContext context)
    {
        try
        {
            var forms = await _formStore.GetFormMetadataAsync(
                request.FormIds.ToList(),
                request.Tags.ToList(),
                request.ServiceId,
                context.CancellationToken);

            var response = new Proto.GetFormMetadataResponse
            {
                TotalCount = forms.Count
            };

            response.Forms.AddRange(
                forms.Select(f => FormGrpcConverters.ToProtoFormMetadata(f)));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get form metadata");
            throw new RpcException(new Status(StatusCode.Internal, "Failed to retrieve form metadata"));
        }
    }

    private async Task<CollaborationSession> GetOrCreateCollaborationSessionAsync(string sessionId, string formId)
    {
        return _activeSessions.GetOrAdd(sessionId, _ => new CollaborationSession
        {
            SessionId = sessionId,
            FormId = formId,
            CreatedAt = DateTimeOffset.UtcNow,
            Participants = new ConcurrentDictionary<string, ParticipantInfo>()
        });
    }

    private async Task<Proto.FormUpdateResponse> ProcessFormUpdateAsync(
        CollaborationSession session,
        Proto.FormUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var response = new Proto.FormUpdateResponse
        {
            SessionId = request.SessionId,
            Update = request.Update,
            Status = Proto.UpdateStatus.Applied
        };

        // Add participant if new user joined
        if (request.Update.UpdateType == Proto.UpdateType.UserJoined)
        {
            session.Participants.TryAdd(request.Update.UserId, new ParticipantInfo
            {
                UserId = request.Update.UserId,
                JoinedAt = DateTimeOffset.UtcNow
            });
        }
        else if (request.Update.UpdateType == Proto.UpdateType.UserLeft)
        {
            session.Participants.TryRemove(request.Update.UserId, out _);
        }

        // Update active users list
        response.ActiveUsers.AddRange(session.Participants.Keys);

        return response;
    }

    private async Task BroadcastToSessionParticipantsAsync(
        CollaborationSession session,
        Proto.FormUpdateResponse response,
        CancellationToken cancellationToken)
    {
        // In production, this would use SignalR or similar
        // For now, just log the broadcast
        _logger.LogDebug("Broadcasting update to {ParticipantCount} participants in session {SessionId}",
            session.Participants.Count, session.SessionId);
    }

    private async Task RemoveFromCollaborationSessionAsync(string sessionId, ServerCallContext context)
    {
        if (_activeSessions.TryGetValue(sessionId, out var session))
        {
            // Remove participant based on context (would need user identification)
            _logger.LogInformation("Participant left collaboration session {SessionId}", sessionId);

            // Remove empty sessions
            if (!session.Participants.Any())
            {
                _activeSessions.TryRemove(sessionId, out _);
                _logger.LogInformation("Removed empty collaboration session {SessionId}", sessionId);
            }
        }
    }

    private async Task<FormDefinition> ApplyMobileOptimizationsAsync(
        FormDefinition form,
        Proto.MobileCapabilities capabilities,
        LayerDefinition targetLayer)
    {
        // Apply device-specific optimizations
        var optimizedForm = form with
        {
            Controls = form.Controls.Select(control => ApplyMobileOptimizations(control, capabilities)).ToList()
        };

        return optimizedForm;
    }

    private FormControl ApplyMobileOptimizations(FormControl control, Proto.MobileCapabilities capabilities)
    {
        // Apply mobile-specific optimizations based on device capabilities
        if (capabilities.BatteryLevel == Proto.BatteryLevel.Low)
        {
            // Reduce media quality for low battery
            if (control.ControlType == FormControlType.Media)
            {
                control = control with
                {
                    Properties = control.Properties.SetItem("quality", "low")
                };
            }
        }

        if (capabilities.NetworkType == Proto.NetworkType.Cellular ||
            capabilities.NetworkType == Proto.NetworkType.Limited)
        {
            // Compress media for limited networks
            if (control.ControlType == FormControlType.Media)
            {
                control = control with
                {
                    Properties = control.Properties.SetItem("compress", true)
                };
            }
        }

        return control;
    }

    private async Task<Feature> ConvertFormToFeatureAsync(
        FormDefinition formDefinition,
        Proto.FormInstance instance,
        IEnumerable<Proto.FormAttachment> attachments,
        LayerDefinition targetLayer)
    {
        var attributes = new Dictionary<string, object?>();
        Geometry? geometry = null;

        // Convert form fields to feature attributes
        foreach (var field in instance.FieldValues)
        {
            var value = FormGrpcConverters.FromProtoAttributeValue(field.Value);

            // Handle geometry fields specially
            if (IsGeometryField(formDefinition, field.Key))
            {
                geometry = ConvertToGeometry(value);
            }
            else
            {
                // Map to target layer field
                var targetFieldName = GetTargetFieldName(formDefinition, field.Key, targetLayer);
                if (!string.IsNullOrEmpty(targetFieldName))
                {
                    attributes[targetFieldName] = value;
                }
            }
        }

        // Add metadata fields
        attributes["CREATED_AT"] = DateTimeOffset.FromUnixTimeMilliseconds(instance.CreatedAt).DateTime;
        attributes["CREATED_BY"] = instance.CreatedBy;
        attributes["FORM_ID"] = instance.FormId;
        attributes["INSTANCE_ID"] = instance.InstanceId;

        return new Feature
        {
            Attributes = attributes,
            Geometry = geometry
        };
    }

    private bool IsGeometryField(FormDefinition form, string fieldId)
    {
        return form.Controls.Any(c => c.ControlId == fieldId &&
            c.ControlType == FormControlType.Location);
    }

    private Geometry? ConvertToGeometry(object? value)
    {
        if (value is string geometryString && !string.IsNullOrEmpty(geometryString))
        {
            // Parse geometry from various formats (WKT, GeoJSON, etc.)
            return GeometryParser.Parse(geometryString);
        }
        return null;
    }

    private string? GetTargetFieldName(FormDefinition form, string controlId, LayerDefinition layer)
    {
        // Map form control to layer field (could be configurable)
        var control = form.Controls.FirstOrDefault(c => c.ControlId == controlId);
        if (control != null && control.Properties.TryGetValue("targetField", out var targetField))
        {
            return targetField.ToString();
        }

        // Default mapping (could be enhanced)
        return layer.AttributeFields.FirstOrDefault(f =>
            f.Name.Equals(controlId, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    private Proto.MobileOptimizations CreateMobileOptimizations(Proto.MobileCapabilities capabilities)
    {
        return new Proto.MobileOptimizations
        {
            CompressMedia = capabilities.NetworkType == Proto.NetworkType.Cellular,
            DefaultMediaQuality = capabilities.BatteryLevel == Proto.BatteryLevel.Low
                ? Proto.MediaQuality.Low
                : Proto.MediaQuality.Medium,
            LocationAccuracyMeters = capabilities.BatteryLevel == Proto.BatteryLevel.Low ? 50 : 10,
            EnableOfflineMode = true,
            AutoSaveIntervalSeconds = 30,
            ReduceAnimations = capabilities.BatteryLevel == Proto.BatteryLevel.Low,
            PreferNativeControls = true
        };
    }

    private static void EnsureGrpcEnabled(ServiceDefinition service)
    {
        if (ServiceProtocols.IsProtocolEnabled(service.Metadata, ServiceProtocols.Grpc))
        {
            return;
        }

        throw new RpcException(new Status(StatusCode.NotFound, "gRPC is not enabled for this service."));
    }
}

// Supporting classes
internal class CollaborationSession
{
    public string SessionId { get; set; } = string.Empty;
    public string FormId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public ConcurrentDictionary<string, ParticipantInfo> Participants { get; set; } = new();
}

internal class ParticipantInfo
{
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset JoinedAt { get; set; }
}

internal class FormServiceOptions
{
    public int MaxCollaborationSessions { get; set; } = 1000;
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromHours(24);
    public bool EnableRealTimeCollaboration { get; set; } = true;
}