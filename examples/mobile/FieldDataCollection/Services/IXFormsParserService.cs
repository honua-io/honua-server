// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using FieldDataCollection.Models;

namespace FieldDataCollection.Services;

/// <summary>
/// Service for parsing XForms XML into mobile-optimized form definitions.
/// Supports OpenRosa-compatible XForms with Honua mobile extensions.
/// </summary>
public interface IXFormsParserService
{
    /// <summary>
    /// Parses XForms XML string into mobile form definition.
    /// </summary>
    /// <param name="xformsXml">XForms XML content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Parsed form ready for mobile rendering.</returns>
    Task<XForm> ParseXFormsAsync(string xformsXml, CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses XForms from stream (file download, etc).
    /// </summary>
    /// <param name="xformsStream">XForms XML stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Parsed form definition.</returns>
    Task<XForm> ParseXFormsAsync(Stream xformsStream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates XForms compatibility and mobile optimization.
    /// </summary>
    /// <param name="xform">Parsed form to validate.</param>
    /// <returns>Validation results with mobile-specific recommendations.</returns>
    Task<List<FormValidationResult>> ValidateFormAsync(XForm xform);

    /// <summary>
    /// Creates a blank form instance ready for data entry.
    /// </summary>
    /// <param name="xform">Form definition.</param>
    /// <returns>Empty form instance with default values.</returns>
    XFormInstance CreateBlankInstance(XForm xform);

    /// <summary>
    /// Validates form instance data against XForms binding rules.
    /// </summary>
    /// <param name="xform">Form definition with validation rules.</param>
    /// <param name="instance">Form instance with user data.</param>
    /// <returns>Validation results for each field.</returns>
    Task<List<FormValidationResult>> ValidateInstanceAsync(XForm xform, XFormInstance instance);

    /// <summary>
    /// Prepares form submission data for gRPC upload.
    /// Note: Uses gRPC instead of traditional OpenRosa submission endpoint.
    /// </summary>
    /// <param name="xform">Form definition.</param>
    /// <param name="instance">Completed form instance.</param>
    /// <param name="attachments">Form attachments (photos, etc).</param>
    /// <returns>Submission ready for Honua gRPC protocols.</returns>
    Task<FormSubmission> PrepareSubmissionAsync(
        XForm xform,
        XFormInstance instance,
        List<FormAttachment> attachments);

    /// <summary>
    /// Calculates form completion percentage for progress display.
    /// </summary>
    /// <param name="xform">Form definition.</param>
    /// <param name="instance">Current form instance.</param>
    /// <returns>Completion progress information.</returns>
    FormProgress CalculateProgress(XForm xform, XFormInstance instance);

    /// <summary>
    /// Applies mobile optimizations to parsed form.
    /// </summary>
    /// <param name="xform">Form to optimize.</param>
    /// <param name="mobileSettings">Device-specific optimization settings.</param>
    /// <returns>Optimized form for mobile rendering.</returns>
    Task<XForm> ApplyMobileOptimizationsAsync(XForm xform, MobileFormSettings mobileSettings);

    /// <summary>
    /// Gets suggested control type for mobile rendering.
    /// </summary>
    /// <param name="control">XForms control definition.</param>
    /// <param name="binding">Associated data binding.</param>
    /// <returns>Optimal mobile control type and configuration.</returns>
    MobileControlSuggestion GetMobileControlSuggestion(XFormControl control, XFormBind binding);
}

/// <summary>
/// Mobile control rendering suggestion.
/// </summary>
public class MobileControlSuggestion
{
    public MobileControlType ControlType { get; set; } = MobileControlType.Entry;
    public string? Appearance { get; set; }
    public bool IsRequired { get; set; }
    public string? Placeholder { get; set; }
    public string? ValidationPattern { get; set; }
    public Dictionary<string, object> Properties { get; set; } = new();
    public string? ReasoningText { get; set; }
}

/// <summary>
/// Mobile-optimized control types for MAUI rendering.
/// </summary>
public enum MobileControlType
{
    Entry,          // Single line text
    Editor,         // Multi-line text
    NumericEntry,   // Number input
    DatePicker,     // Date selection
    TimePicker,     // Time selection
    Picker,         // Single choice dropdown
    CheckBox,       // Boolean toggle
    Switch,         // Boolean switch
    Slider,         // Numeric range
    Stepper,        // Numeric increment
    RadioGroup,     // Single choice radio buttons
    CheckBoxGroup,  // Multiple choice checkboxes
    ImageButton,    // Photo capture
    LocationButton, // GPS capture
    FileButton,     // File upload
    GroupHeader,    // Section heading
    Separator       // Visual separator
}