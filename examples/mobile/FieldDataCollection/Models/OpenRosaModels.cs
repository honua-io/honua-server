// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace FieldDataCollection.Models;

/// <summary>
/// Represents a parsed XForms definition for mobile rendering.
/// </summary>
public class XForm
{
    public string FormId { get; set; } = string.Empty;
    public string FormTitle { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0";
    public string? Description { get; set; }

    /// <summary>
    /// Root data model instance.
    /// </summary>
    public XFormInstance Instance { get; set; } = new();

    /// <summary>
    /// Form controls and layout.
    /// </summary>
    public List<XFormControl> Controls { get; set; } = new();

    /// <summary>
    /// Binding definitions with validation rules.
    /// </summary>
    public List<XFormBind> Bindings { get; set; } = new();

    /// <summary>
    /// Form metadata from OpenRosa headers.
    /// </summary>
    public XFormMetadata Metadata { get; set; } = new();
}

/// <summary>
/// Data instance structure for form values.
/// </summary>
public class XFormInstance
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Form data values keyed by field path.
    /// </summary>
    public Dictionary<string, object?> Data { get; set; } = new();

    /// <summary>
    /// Current submission state.
    /// </summary>
    public XFormSubmissionState State { get; set; } = XFormSubmissionState.Draft;

    /// <summary>
    /// When this instance was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// When this instance was last modified.
    /// </summary>
    public DateTime ModifiedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Unique instance identifier for submissions.
    /// </summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");
}

public enum XFormSubmissionState
{
    Draft,
    Complete,
    Submitted,
    Error
}

/// <summary>
/// Form control definition from XForms body.
/// </summary>
public class XFormControl
{
    public string Type { get; set; } = string.Empty; // input, select1, select, group, repeat
    public string Ref { get; set; } = string.Empty; // Data binding path
    public string? Label { get; set; }
    public string? Hint { get; set; }
    public string? Appearance { get; set; }
    public bool IsGroup { get; set; }

    /// <summary>
    /// Child controls for groups.
    /// </summary>
    public List<XFormControl> Children { get; set; } = new();

    /// <summary>
    /// Select options for choice controls.
    /// </summary>
    public List<XFormChoice> Choices { get; set; } = new();

    /// <summary>
    /// Media type for upload controls.
    /// </summary>
    public string? MediaType { get; set; }
}

/// <summary>
/// Choice option for select controls.
/// </summary>
public class XFormChoice
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? ImageUri { get; set; }
}

/// <summary>
/// Data binding with validation and calculation rules.
/// </summary>
public class XFormBind
{
    public string NodeSet { get; set; } = string.Empty; // XPath to data node
    public string Type { get; set; } = "string"; // Data type
    public bool Required { get; set; } = false;
    public bool ReadOnly { get; set; } = false;
    public string? Constraint { get; set; } // XPath constraint expression
    public string? ConstraintMsg { get; set; }
    public string? Calculate { get; set; } // XPath calculation
    public string? Relevant { get; set; } // XPath relevance condition
}

/// <summary>
/// Form metadata and configuration.
/// </summary>
public class XFormMetadata
{
    public string? InstanceName { get; set; }
    public string? SubmissionUrl { get; set; }
    public bool AutoSend { get; set; } = false;
    public bool AutoDelete { get; set; } = false;
    public string? DefaultLanguage { get; set; }

    /// <summary>
    /// Honua-specific mobile optimizations.
    /// </summary>
    public MobileFormSettings MobileSettings { get; set; } = new();
}

/// <summary>
/// Mobile-specific form optimization settings.
/// </summary>
public class MobileFormSettings
{
    public bool AllowOfflineEditing { get; set; } = true;
    public bool RequireGpsAccuracy { get; set; } = false;
    public double? MinGpsAccuracy { get; set; } = 10.0; // meters
    public bool AutoCapture { get; set; } = false;
    public PhotoQuality PhotoQuality { get; set; } = PhotoQuality.Medium;
    public bool UseLowPowerMode { get; set; } = false;
    public bool EnableCompression { get; set; } = true;
}

public enum PhotoQuality
{
    Low = 50,
    Medium = 75,
    High = 90
}

/// <summary>
/// Form validation result for real-time feedback.
/// </summary>
public class FormValidationResult : INotifyPropertyChanged
{
    private bool _isValid = true;
    private string _errorMessage = string.Empty;

    public bool IsValid
    {
        get => _isValid;
        set
        {
            _isValid = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsValid)));
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            _errorMessage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorMessage)));
        }
    }

    public string FieldPath { get; set; } = string.Empty;
    public ValidationSeverity Severity { get; set; } = ValidationSeverity.Error;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum ValidationSeverity
{
    Error,
    Warning,
    Info
}

/// <summary>
/// Form submission data ready for upload.
/// </summary>
public class FormSubmission
{
    public string FormId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime SubmissionTime { get; set; } = DateTime.Now;
    public Dictionary<string, object?> Data { get; set; } = new();
    public List<FormAttachment> Attachments { get; set; } = new();
    public string DeviceId { get; set; } = string.Empty;
    public Location? CaptureLocation { get; set; }
}

/// <summary>
/// File attachment from form (photos, documents).
/// </summary>
public class FormAttachment
{
    public string FieldName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime CapturedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// GPS location data with accuracy information.
/// </summary>
public class Location
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double? Altitude { get; set; }
    public double? Accuracy { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;

    public string ToGeoPointString()
    {
        var latStr = Latitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
        var lonStr = Longitude.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);

        if (Altitude.HasValue && Accuracy.HasValue)
        {
            var altStr = Altitude.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            var accStr = Accuracy.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            return $"{latStr} {lonStr} {altStr} {accStr}";
        }
        else if (Altitude.HasValue)
        {
            var altStr = Altitude.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            return $"{latStr} {lonStr} {altStr}";
        }

        return $"{latStr} {lonStr}";
    }

    public static Location? FromGeoPointString(string geoPoint)
    {
        if (string.IsNullOrWhiteSpace(geoPoint))
            return null;

        var parts = geoPoint.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var lon))
        {
            return null;
        }

        var location = new Location { Latitude = lat, Longitude = lon };

        if (parts.Length > 2 && double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var alt))
        {
            location.Altitude = alt;
        }

        if (parts.Length > 3 && double.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var acc))
        {
            location.Accuracy = acc;
        }

        return location;
    }
}

/// <summary>
/// Progress information for form operations.
/// </summary>
public class FormProgress
{
    public int CompletedFields { get; set; }
    public int TotalFields { get; set; }
    public double PercentComplete => TotalFields > 0 ? (double)CompletedFields / TotalFields * 100 : 0;
    public string CurrentOperation { get; set; } = string.Empty;
    public TimeSpan ElapsedTime { get; set; }
    public bool IsUploading { get; set; } = false;
    public bool IsDownloading { get; set; } = false;
}