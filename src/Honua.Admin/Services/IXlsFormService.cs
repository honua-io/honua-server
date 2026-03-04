// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Admin.Models;
using Honua.Core.Features.Catalog.Domain;

namespace Honua.Admin.Services;

/// <summary>
/// Service for managing XLSForm creation, validation, and deployment.
/// Integrates OpenRosa standards with Honua spatial capabilities.
/// </summary>
public interface IXlsFormService
{
    /// <summary>
    /// Creates a new XLSForm from a Honua layer schema.
    /// </summary>
    /// <param name="serviceId">Service identifier.</param>
    /// <param name="layerId">Layer identifier.</param>
    /// <param name="formName">Name for the new form.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Generated form template ready for customization.</returns>
    Task<LayerFormTemplate> CreateFormFromLayerAsync(
        string serviceId,
        int layerId,
        string formName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Converts XLSForm definition to XForms XML for mobile deployment.
    /// </summary>
    /// <param name="xlsForm">XLSForm definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>XForms XML string.</returns>
    Task<string> ConvertToXFormsAsync(
        XlsForm xlsForm,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates XLSForm for correctness and best practices.
    /// </summary>
    /// <param name="xlsForm">XLSForm to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation results and suggestions.</returns>
    Task<List<FormValidationResult>> ValidateFormAsync(
        XlsForm xlsForm,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates preview URL and QR code for form testing.
    /// </summary>
    /// <param name="formId">Form identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Preview information with URLs and QR code.</returns>
    Task<FormPreview> GeneratePreviewAsync(
        string formId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deploys form to mobile clients via gRPC v2 protocols.
    /// </summary>
    /// <param name="formId">Form identifier.</param>
    /// <param name="targetDevices">Target device/user filters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Deployment result with success/failure details.</returns>
    Task<FormDeploymentResult> DeployFormAsync(
        string formId,
        FormDeploymentTarget targetDevices,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all available forms for management.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of forms with status and metadata.</returns>
    Task<List<XlsForm>> GetFormsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets specific form by ID.
    /// </summary>
    /// <param name="formId">Form identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Form definition or null if not found.</returns>
    Task<XlsForm?> GetFormAsync(string formId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves or updates form definition.
    /// </summary>
    /// <param name="xlsForm">Form to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Saved form with updated metadata.</returns>
    Task<XlsForm> SaveFormAsync(XlsForm xlsForm, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes form and removes from mobile clients.
    /// </summary>
    /// <param name="formId">Form identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Deletion result.</returns>
    Task<bool> DeleteFormAsync(string formId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets form submission analytics and statistics.
    /// </summary>
    /// <param name="formId">Form identifier.</param>
    /// <param name="dateRange">Date range for analytics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Form usage analytics.</returns>
    Task<FormAnalytics> GetFormAnalyticsAsync(
        string formId,
        DateRange dateRange,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Suggests optimal form field types based on layer schema analysis.
    /// </summary>
    /// <param name="layerField">Layer field definition.</param>
    /// <returns>Suggested XLSForm question types and configurations.</returns>
    FormFieldSuggestion SuggestFormField(FieldDefinition layerField);

    /// <summary>
    /// Imports XLSForm from Excel file upload.
    /// </summary>
    /// <param name="xlsxFile">Excel file stream.</param>
    /// <param name="fileName">Original file name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Parsed XLSForm definition.</returns>
    Task<XlsForm> ImportFromExcelAsync(
        Stream xlsxFile,
        string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports XLSForm to Excel file for external editing.
    /// </summary>
    /// <param name="formId">Form identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Excel file data.</returns>
    Task<byte[]> ExportToExcelAsync(string formId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Form deployment targeting options.
/// </summary>
public class FormDeploymentTarget
{
    public List<string> DeviceIds { get; set; } = new();
    public List<string> UserGroups { get; set; } = new();
    public List<string> OrganizationIds { get; set; } = new();
    public bool DeployToAll { get; set; } = false;
    public DateTime? ScheduledDeployment { get; set; }
    public bool AutoUpdate { get; set; } = true;
}

/// <summary>
/// Result of form deployment operation.
/// </summary>
public class FormDeploymentResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int TargetDeviceCount { get; set; }
    public int SuccessfulDeployments { get; set; }
    public int FailedDeployments { get; set; }
    public List<string> FailureReasons { get; set; } = new();
    public DateTime DeployedAt { get; set; } = DateTime.UtcNow;
    public string DeploymentId { get; set; } = Guid.NewGuid().ToString("N");
}

/// <summary>
/// Date range for analytics queries.
/// </summary>
public class DateRange
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }

    public static DateRange LastWeek => new()
    {
        StartDate = DateTime.UtcNow.AddDays(-7),
        EndDate = DateTime.UtcNow
    };

    public static DateRange LastMonth => new()
    {
        StartDate = DateTime.UtcNow.AddDays(-30),
        EndDate = DateTime.UtcNow
    };
}

/// <summary>
/// Suggestion for form field configuration based on layer schema.
/// </summary>
public class FormFieldSuggestion
{
    public string SuggestedType { get; set; } = string.Empty;
    public string? SuggestedAppearance { get; set; }
    public string? SuggestedConstraint { get; set; }
    public string? SuggestedLabel { get; set; }
    public string? SuggestedHint { get; set; }
    public List<XlsFormChoice>? SuggestedChoices { get; set; }
    public string? Reasoning { get; set; }
    public int Priority { get; set; } = 1;
}