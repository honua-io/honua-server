// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Immutable;

namespace Honua.Core.Transport.Clients;

/// <summary>
/// Generic interface for form service clients that work across different platforms.
/// Provides gRPC-native form capabilities as an alternative to OpenRosa XML.
/// </summary>
/// <typeparam name="TContext">Platform-specific context type</typeparam>
public interface IFormServiceClient<TContext>
{
    /// <summary>
    /// Retrieves a form definition for mobile rendering.
    /// </summary>
    /// <param name="formId">Form identifier</param>
    /// <param name="version">Optional form version (uses latest if not specified)</param>
    /// <param name="context">Platform-specific context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Form definition with fields and validation rules</returns>
    Task<FormDefinition> GetFormDefinitionAsync(
        string formId,
        string? version,
        TContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits completed form data as feature edits.
    /// </summary>
    /// <param name="formId">Form identifier</param>
    /// <param name="submission">Form submission data</param>
    /// <param name="context">Platform-specific context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Submission result with validation errors if any</returns>
    Task<FormSubmissionResult> SubmitFormDataAsync(
        string formId,
        FormSubmission submission,
        TContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enables real-time collaborative form editing.
    /// </summary>
    /// <param name="formId">Form identifier</param>
    /// <param name="sessionId">Collaboration session identifier</param>
    /// <param name="context">Platform-specific context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Bidirectional stream for form updates</returns>
    Task<IFormCollaborationSession> StartCollaborationSessionAsync(
        string formId,
        string sessionId,
        TContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a form definition with fields and validation rules.
/// </summary>
public class FormDefinition
{
    /// <summary>
    /// Unique form identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Form version.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Display title of the form.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Optional description of the form.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Form fields organized in sections.
    /// </summary>
    public ImmutableArray<FormSection> Sections { get; set; } = ImmutableArray<FormSection>.Empty;

    /// <summary>
    /// Target service and layer for form submissions.
    /// </summary>
    public FormTarget? Target { get; set; }

    /// <summary>
    /// Form-level validation rules.
    /// </summary>
    public ImmutableArray<ValidationRule> ValidationRules { get; set; } = ImmutableArray<ValidationRule>.Empty;
}

/// <summary>
/// Represents a section within a form.
/// </summary>
public class FormSection
{
    /// <summary>
    /// Section identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Section title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Fields in this section.
    /// </summary>
    public ImmutableArray<FormField> Fields { get; set; } = ImmutableArray<FormField>.Empty;

    /// <summary>
    /// Whether this section is collapsible.
    /// </summary>
    public bool Collapsible { get; set; }

    /// <summary>
    /// Whether this section is initially collapsed.
    /// </summary>
    public bool InitiallyCollapsed { get; set; }
}

/// <summary>
/// Represents a field within a form.
/// </summary>
public class FormField
{
    /// <summary>
    /// Field identifier (maps to feature attribute name).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display label for the field.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Field data type.
    /// </summary>
    public FormFieldType FieldType { get; set; }

    /// <summary>
    /// Whether the field is required.
    /// </summary>
    public bool Required { get; set; }

    /// <summary>
    /// Default value for the field.
    /// </summary>
    public object? DefaultValue { get; set; }

    /// <summary>
    /// Field-specific validation rules.
    /// </summary>
    public ImmutableArray<ValidationRule> ValidationRules { get; set; } = ImmutableArray<ValidationRule>.Empty;

    /// <summary>
    /// Options for choice fields (radio, dropdown, etc.).
    /// </summary>
    public ImmutableArray<FieldOption> Options { get; set; } = ImmutableArray<FieldOption>.Empty;

    /// <summary>
    /// Help text for the field.
    /// </summary>
    public string? HelpText { get; set; }
}

/// <summary>
/// Supported form field types.
/// </summary>
public enum FormFieldType
{
    Text,
    Number,
    Date,
    DateTime,
    Boolean,
    SingleChoice,
    MultipleChoice,
    Photo,
    Location,
    Signature,
    Barcode,
    File
}

/// <summary>
/// Option for choice fields.
/// </summary>
public class FieldOption
{
    /// <summary>
    /// Option value (stored in feature attributes).
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Display label for the option.
    /// </summary>
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// Validation rule for form fields.
/// </summary>
public class ValidationRule
{
    /// <summary>
    /// Type of validation.
    /// </summary>
    public ValidationType Type { get; set; }

    /// <summary>
    /// Validation parameter (e.g., max length, min value).
    /// </summary>
    public object? Parameter { get; set; }

    /// <summary>
    /// Error message to display if validation fails.
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;
}

/// <summary>
/// Types of field validation.
/// </summary>
public enum ValidationType
{
    MinLength,
    MaxLength,
    MinValue,
    MaxValue,
    Pattern,
    Custom
}

/// <summary>
/// Target service and layer for form submissions.
/// </summary>
public class FormTarget
{
    /// <summary>
    /// Target service identifier.
    /// </summary>
    public string ServiceId { get; set; } = string.Empty;

    /// <summary>
    /// Target layer identifier.
    /// </summary>
    public int LayerId { get; set; }
}

/// <summary>
/// Represents a completed form submission.
/// </summary>
public class FormSubmission
{
    /// <summary>
    /// Form identifier.
    /// </summary>
    public string FormId { get; set; } = string.Empty;

    /// <summary>
    /// Form version used for submission.
    /// </summary>
    public string FormVersion { get; set; } = string.Empty;

    /// <summary>
    /// Submitted field values.
    /// </summary>
    public Dictionary<string, object?> Values { get; set; } = new();

    /// <summary>
    /// Submission metadata.
    /// </summary>
    public SubmissionMetadata Metadata { get; set; } = new();
}

/// <summary>
/// Metadata for form submissions.
/// </summary>
public class SubmissionMetadata
{
    /// <summary>
    /// Timestamp when the form was started.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Timestamp when the form was submitted.
    /// </summary>
    public DateTime SubmissionTime { get; set; }

    /// <summary>
    /// Device identifier.
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// User identifier.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Location where the form was submitted.
    /// </summary>
    public LocationInfo? Location { get; set; }
}

/// <summary>
/// Location information for submissions.
/// </summary>
public class LocationInfo
{
    /// <summary>
    /// Latitude in decimal degrees.
    /// </summary>
    public double Latitude { get; set; }

    /// <summary>
    /// Longitude in decimal degrees.
    /// </summary>
    public double Longitude { get; set; }

    /// <summary>
    /// Elevation in meters.
    /// </summary>
    public double? Elevation { get; set; }

    /// <summary>
    /// Location accuracy in meters.
    /// </summary>
    public double? Accuracy { get; set; }
}

/// <summary>
/// Result of a form submission.
/// </summary>
public class FormSubmissionResult
{
    /// <summary>
    /// Whether the submission was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Object ID of the created/updated feature.
    /// </summary>
    public long? ObjectId { get; set; }

    /// <summary>
    /// Validation errors if any.
    /// </summary>
    public ImmutableArray<ValidationError> ValidationErrors { get; set; } = ImmutableArray<ValidationError>.Empty;

    /// <summary>
    /// Error message if submission failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Validation error for form fields.
/// </summary>
public class ValidationError
{
    /// <summary>
    /// Field identifier that failed validation.
    /// </summary>
    public string FieldId { get; set; } = string.Empty;

    /// <summary>
    /// Error message.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Interface for real-time form collaboration sessions.
/// </summary>
public interface IFormCollaborationSession : IDisposable
{
    /// <summary>
    /// Sends a form update to other collaborators.
    /// </summary>
    /// <param name="update">Form update to send</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SendUpdateAsync(FormUpdate update, CancellationToken cancellationToken = default);

    /// <summary>
    /// Receives form updates from other collaborators.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of form updates</returns>
    IAsyncEnumerable<FormUpdate> ReceiveUpdatesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a real-time form update.
/// </summary>
public class FormUpdate
{
    /// <summary>
    /// Type of update.
    /// </summary>
    public FormUpdateType UpdateType { get; set; }

    /// <summary>
    /// Field identifier being updated.
    /// </summary>
    public string? FieldId { get; set; }

    /// <summary>
    /// New value for the field.
    /// </summary>
    public object? Value { get; set; }

    /// <summary>
    /// User who made the update.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Timestamp of the update.
    /// </summary>
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Types of form updates for collaboration.
/// </summary>
public enum FormUpdateType
{
    FieldChanged,
    FieldFocused,
    FieldBlurred,
    UserJoined,
    UserLeft
}