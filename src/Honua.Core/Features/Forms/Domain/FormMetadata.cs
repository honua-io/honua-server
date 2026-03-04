// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Forms.Domain;

/// <summary>
/// Form metadata for catalog and management purposes.
/// </summary>
/// <param name="FormId">Unique form identifier.</param>
/// <param name="Title">Form title.</param>
/// <param name="Description">Form description.</param>
/// <param name="Version">Current version.</param>
/// <param name="Author">Form author.</param>
/// <param name="CreatedAt">Creation timestamp.</param>
/// <param name="ModifiedAt">Last modification timestamp.</param>
/// <param name="Status">Current form status.</param>
/// <param name="Tags">Form tags for categorization.</param>
/// <param name="TargetServiceId">Target service for data submission.</param>
/// <param name="TargetLayerId">Target layer for feature creation.</param>
/// <param name="Compatibility">Platform compatibility information.</param>
/// <param name="UsageStats">Form usage statistics.</param>
public record FormMetadata(
    string FormId,
    string Title,
    string Description,
    string Version,
    string Author,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    FormStatus Status,
    ImmutableList<string> Tags,
    string TargetServiceId,
    int TargetLayerId,
    FormCompatibility Compatibility,
    FormUsageStats? UsageStats);

/// <summary>
/// Form status enumeration.
/// </summary>
public enum FormStatus
{
    Draft,
    Published,
    Deprecated,
    Archived
}

/// <summary>
/// Form platform compatibility information.
/// </summary>
/// <param name="MinAppVersion">Minimum app version required.</param>
/// <param name="SupportedPlatforms">Supported platform list.</param>
/// <param name="RequiresOnline">Whether form requires online connectivity.</param>
/// <param name="RequiredPermissions">Required device permissions.</param>
public record FormCompatibility(
    string MinAppVersion,
    ImmutableList<string> SupportedPlatforms,
    bool RequiresOnline,
    ImmutableList<string> RequiredPermissions);

/// <summary>
/// Form usage statistics.
/// </summary>
/// <param name="TotalSubmissions">Total number of submissions.</param>
/// <param name="ActiveUsers">Number of active users.</param>
/// <param name="AverageCompletionTime">Average time to complete form.</param>
/// <param name="CompletionRate">Percentage of started forms that are completed.</param>
/// <param name="LastSubmissionAt">Timestamp of last submission.</param>
/// <param name="PopularFields">Most frequently used fields.</param>
public record FormUsageStats(
    long TotalSubmissions,
    int ActiveUsers,
    TimeSpan AverageCompletionTime,
    double CompletionRate,
    DateTimeOffset? LastSubmissionAt,
    ImmutableDictionary<string, int> PopularFields);

/// <summary>
/// Form validation result.
/// </summary>
/// <param name="IsValid">Whether validation passed.</param>
/// <param name="Issues">List of validation issues.</param>
public record FormValidationResult(
    bool IsValid,
    ImmutableList<ValidationIssue> Issues);

/// <summary>
/// Individual validation issue.
/// </summary>
/// <param name="FieldId">Field identifier (if field-specific).</param>
/// <param name="Severity">Issue severity.</param>
/// <param name="Message">Issue message.</param>
/// <param name="SuggestedValue">Suggested value (if applicable).</param>
public record ValidationIssue(
    string? FieldId,
    ValidationSeverity Severity,
    string Message,
    string? SuggestedValue);

/// <summary>
/// Schema compatibility result.
/// </summary>
/// <param name="IsCompatible">Whether form is compatible with target layer.</param>
/// <param name="Issues">Compatibility issues.</param>
/// <param name="Recommendations">Recommendations for improving compatibility.</param>
public record SchemaCompatibilityResult(
    bool IsCompatible,
    ImmutableList<CompatibilityIssue> Issues,
    ImmutableList<string> Recommendations);

/// <summary>
/// Schema compatibility issue.
/// </summary>
/// <param name="FieldName">Field name causing the issue.</param>
/// <param name="IssueType">Type of compatibility issue.</param>
/// <param name="Message">Issue description.</param>
/// <param name="Suggestion">Suggested fix.</param>
public record CompatibilityIssue(
    string FieldName,
    CompatibilityIssueType IssueType,
    string Message,
    string? Suggestion);

/// <summary>
/// Types of compatibility issues.
/// </summary>
public enum CompatibilityIssueType
{
    MissingField,
    TypeMismatch,
    ConstraintViolation,
    UnsupportedField
}

/// <summary>
/// Collaboration session information.
/// </summary>
/// <param name="SessionId">Unique session identifier.</param>
/// <param name="FormId">Form being collaborated on.</param>
/// <param name="CreatedAt">Session creation time.</param>
/// <param name="Participants">Active participants.</param>
/// <param name="LastActivity">Last activity timestamp.</param>
public record CollaborationSession(
    string SessionId,
    string FormId,
    DateTimeOffset CreatedAt,
    ImmutableList<CollaborationParticipant> Participants,
    DateTimeOffset LastActivity);

/// <summary>
/// Collaboration session participant.
/// </summary>
/// <param name="UserId">User identifier.</param>
/// <param name="DisplayName">Display name.</param>
/// <param name="JoinedAt">When user joined session.</param>
/// <param name="IsActive">Whether user is currently active.</param>
/// <param name="CurrentField">Field user is currently editing (if any).</param>
public record CollaborationParticipant(
    string UserId,
    string DisplayName,
    DateTimeOffset JoinedAt,
    bool IsActive,
    string? CurrentField);

/// <summary>
/// Collaboration session statistics.
/// </summary>
/// <param name="SessionId">Session identifier.</param>
/// <param name="ParticipantCount">Number of participants.</param>
/// <param name="TotalUpdates">Total number of updates.</param>
/// <param name="ConflictCount">Number of conflicts resolved.</param>
/// <param name="AverageResponseTime">Average update response time.</param>
/// <param name="FieldActivityMap">Activity map per field.</param>
public record CollaborationSessionStats(
    string SessionId,
    int ParticipantCount,
    long TotalUpdates,
    int ConflictCount,
    TimeSpan AverageResponseTime,
    ImmutableDictionary<string, int> FieldActivityMap);

/// <summary>
/// Form instance data for storage and tracking.
/// </summary>
/// <param name="InstanceId">Unique instance identifier.</param>
/// <param name="FormId">Associated form ID.</param>
/// <param name="FormVersion">Form version used.</param>
/// <param name="FieldValues">Field values map.</param>
/// <param name="CreatedAt">Instance creation time.</param>
/// <param name="ModifiedAt">Last modification time.</param>
/// <param name="CreatedBy">User who created instance.</param>
/// <param name="Status">Current instance status.</param>
/// <param name="SubmittedAt">Submission timestamp (if submitted).</param>
/// <param name="FeatureId">Created feature ID (if submitted).</param>
public record FormInstance(
    string InstanceId,
    string FormId,
    string FormVersion,
    ImmutableDictionary<string, object?> FieldValues,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    string CreatedBy,
    FormInstanceStatus Status,
    DateTimeOffset? SubmittedAt,
    long? FeatureId);

/// <summary>
/// Form instance status.
/// </summary>
public enum FormInstanceStatus
{
    Draft,
    Complete,
    Submitted,
    Error
}