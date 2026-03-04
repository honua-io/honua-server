// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Admin.Models;

/// <summary>
/// Represents an XLSForm definition for OpenRosa-compatible form creation.
/// </summary>
public class XlsForm
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Required]
    public string Version { get; set; } = "1.0";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    public string? CreatedBy { get; set; }

    /// <summary>
    /// Associated Honua service and layer for spatial data collection.
    /// </summary>
    public string? ServiceId { get; set; }
    public int? LayerId { get; set; }

    /// <summary>
    /// XLSForm survey worksheet data.
    /// </summary>
    public List<XlsFormSurveyRow> Survey { get; set; } = new();

    /// <summary>
    /// XLSForm choices worksheet data for select questions.
    /// </summary>
    public List<XlsFormChoice> Choices { get; set; } = new();

    /// <summary>
    /// XLSForm settings worksheet data.
    /// </summary>
    public XlsFormSettings Settings { get; set; } = new();

    /// <summary>
    /// Generated XForms XML for mobile deployment.
    /// </summary>
    public string? XFormsXml { get; set; }

    /// <summary>
    /// Form deployment status.
    /// </summary>
    public FormDeploymentStatus Status { get; set; } = FormDeploymentStatus.Draft;
}

/// <summary>
/// Represents a row in the XLSForm survey worksheet.
/// </summary>
public class XlsFormSurveyRow
{
    [Required]
    public string Type { get; set; } = string.Empty;

    [Required]
    public string Name { get; set; } = string.Empty;

    public string? Label { get; set; }

    public string? Hint { get; set; }

    public string? Constraint { get; set; }

    public string? ConstraintMessage { get; set; }

    public string? Required { get; set; }

    public string? Readonly { get; set; }

    public string? Default { get; set; }

    public string? Relevant { get; set; }

    public string? Repeat { get; set; }

    public string? Appearance { get; set; }

    /// <summary>
    /// For select questions, the list name from choices worksheet.
    /// </summary>
    public string? Choice { get; set; }

    /// <summary>
    /// Order in the form.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Group or section this question belongs to.
    /// </summary>
    public string? Group { get; set; }

    /// <summary>
    /// Whether this field maps to a Honua layer attribute.
    /// </summary>
    public bool IsLayerField { get; set; }

    /// <summary>
    /// Corresponding Honua layer field name.
    /// </summary>
    public string? LayerFieldName { get; set; }
}

/// <summary>
/// Represents a choice option in the XLSForm choices worksheet.
/// </summary>
public class XlsFormChoice
{
    [Required]
    public string ListName { get; set; } = string.Empty;

    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string Label { get; set; } = string.Empty;

    public string? Image { get; set; }

    public int Order { get; set; }
}

/// <summary>
/// XLSForm settings for form behavior and metadata.
/// </summary>
public class XlsFormSettings
{
    public string FormTitle { get; set; } = string.Empty;
    public string FormId { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0";
    public string? InstanceName { get; set; }
    public string? SubmissionUrl { get; set; }
    public string? AutoSend { get; set; }
    public string? AutoDelete { get; set; }
    public string? DefaultLanguage { get; set; }

    /// <summary>
    /// Honua-specific settings for spatial data collection.
    /// </summary>
    public bool AllowOfflineEditing { get; set; } = true;
    public bool RequireGpsAccuracy { get; set; } = false;
    public double? MinGpsAccuracy { get; set; }
    public bool AutoCapture { get; set; } = false;
    public string? PhotoQuality { get; set; } = "medium";
}

/// <summary>
/// Form deployment and publishing status.
/// </summary>
public enum FormDeploymentStatus
{
    Draft,
    Testing,
    Published,
    Archived,
    Error
}

/// <summary>
/// Template for creating forms from Honua layer schemas.
/// </summary>
public class LayerFormTemplate
{
    public string ServiceId { get; set; } = string.Empty;
    public int LayerId { get; set; }
    public string LayerName { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>
    /// Field mappings from layer schema to form fields.
    /// </summary>
    public List<LayerFieldMapping> FieldMappings { get; set; } = new();

    /// <summary>
    /// Suggested form structure based on field types.
    /// </summary>
    public List<XlsFormSurveyRow> SuggestedSurvey { get; set; } = new();
}

/// <summary>
/// Mapping between Honua layer fields and XLSForm questions.
/// </summary>
public class LayerFieldMapping
{
    public string LayerFieldName { get; set; } = string.Empty;
    public string LayerFieldType { get; set; } = string.Empty;
    public string? LayerFieldAlias { get; set; }
    public bool IsRequired { get; set; }
    public int? MaxLength { get; set; }

    /// <summary>
    /// Suggested XLSForm question type.
    /// </summary>
    public string SuggestedType { get; set; } = string.Empty;

    /// <summary>
    /// Whether to include this field in the form.
    /// </summary>
    public bool IncludeInForm { get; set; } = true;

    /// <summary>
    /// Custom form question configuration.
    /// </summary>
    public string? CustomLabel { get; set; }
    public string? CustomHint { get; set; }
    public string? CustomConstraint { get; set; }
    public string? CustomAppearance { get; set; }
}

/// <summary>
/// Form preview and testing information.
/// </summary>
public class FormPreview
{
    public string FormId { get; set; } = string.Empty;
    public string PreviewUrl { get; set; } = string.Empty;
    public string QrCodeData { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Validation results for the form.
    /// </summary>
    public List<FormValidationResult> ValidationResults { get; set; } = new();
}

/// <summary>
/// Form validation result for quality checking.
/// </summary>
public class FormValidationResult
{
    public FormValidationSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? FieldName { get; set; }
    public string? Suggestion { get; set; }
}

public enum FormValidationSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// Statistics and analytics for deployed forms.
/// </summary>
public class FormAnalytics
{
    public string FormId { get; set; } = string.Empty;
    public int TotalSubmissions { get; set; }
    public int SubmissionsToday { get; set; }
    public int SubmissionsThisWeek { get; set; }
    public DateTime? LastSubmission { get; set; }
    public double AverageCompletionTime { get; set; }
    public List<string> MostActiveDevices { get; set; } = new();
    public Dictionary<string, int> FieldCompletionRates { get; set; } = new();
}