// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using FieldDataCollection.Services;

namespace FieldDataCollection.Models;

/// <summary>
/// Configuration for mobile form optimization and device capabilities.
/// </summary>
public class MobileFormConfiguration
{
    /// <summary>
    /// Current device capabilities for form optimization.
    /// </summary>
    public static Geospatial.V1.MobileCapabilities GetDeviceCapabilities()
    {
        return new Geospatial.V1.MobileCapabilities
        {
            HasCamera = MediaPicker.IsCaptureSupported,
            HasGps = true, // Assume GPS available on mobile devices
            HasAccelerometer = Accelerometer.IsSupported,
            HasGyroscope = Gyroscope.IsSupported,
            Platform = DeviceInfo.Platform.ToString().ToLowerInvariant(),
            DeviceType = DeviceInfo.Idiom switch
            {
                DeviceIdiom.Phone => "phone",
                DeviceIdiom.Tablet => "tablet",
                DeviceIdiom.Desktop => "desktop",
                _ => "unknown"
            },
            NetworkType = GetCurrentNetworkType(),
            BatteryLevel = GetCurrentBatteryLevel()
        };
    }

    /// <summary>
    /// Gets mobile optimization settings based on device state.
    /// </summary>
    public static Geospatial.V1.MobileOptimizations GetOptimizations()
    {
        var batteryLevel = GetCurrentBatteryLevel();
        var networkType = GetCurrentNetworkType();

        return new Geospatial.V1.MobileOptimizations
        {
            CompressMedia = networkType == Geospatial.V1.NetworkType.Cellular ||
                           networkType == Geospatial.V1.NetworkType.Limited,
            DefaultMediaQuality = batteryLevel == Geospatial.V1.BatteryLevel.Low
                ? Geospatial.V1.MediaQuality.Low
                : Geospatial.V1.MediaQuality.Medium,
            LocationAccuracyMeters = batteryLevel == Geospatial.V1.BatteryLevel.Low ? 50 : 10,
            EnableOfflineMode = true,
            AutoSaveIntervalSeconds = 30,
            ReduceAnimations = batteryLevel == Geospatial.V1.BatteryLevel.Low,
            PreferNativeControls = true
        };
    }

    /// <summary>
    /// Gets mobile control hints for optimal UX.
    /// </summary>
    public static Geospatial.V1.MobileControlHints GetControlHints(Geospatial.V1.FormControl control)
    {
        var hints = new Geospatial.V1.MobileControlHints
        {
            AutoFocus = false,
            AutoCapitalize = true,
            SpellCheck = true,
            MaxDisplayLines = 3
        };

        // Set control-specific hints
        switch (control.ControlType.InnerType)
        {
            case Geospatial.V1.FormControl.ControlTypeOneofCase.TextInput:
                var textInput = control.TextInput;
                hints.KeyboardType = textInput.InputType switch
                {
                    Geospatial.V1.TextInputType.Email => Geospatial.V1.KeyboardType.Email,
                    Geospatial.V1.TextInputType.Url => Geospatial.V1.KeyboardType.Url,
                    Geospatial.V1.TextInputType.Phone => Geospatial.V1.KeyboardType.Phone,
                    _ => Geospatial.V1.KeyboardType.Default
                };
                hints.SpellCheck = textInput.InputType == Geospatial.V1.TextInputType.Text;
                break;

            case Geospatial.V1.FormControl.ControlTypeOneofCase.NumericInput:
                hints.KeyboardType = control.NumericInput.NumericType switch
                {
                    Geospatial.V1.NumericType.Integer => Geospatial.V1.KeyboardType.Numeric,
                    _ => Geospatial.V1.KeyboardType.Decimal
                };
                hints.SpellCheck = false;
                break;

            case Geospatial.V1.FormControl.ControlTypeOneofCase.LocationControl:
                hints.PreferredInputMethod = Geospatial.V1.InputMethod.Keyboard;
                break;

            case Geospatial.V1.FormControl.ControlTypeOneofCase.MediaControl:
                hints.PreferredInputMethod = Geospatial.V1.InputMethod.Keyboard;
                break;
        }

        return hints;
    }

    private static Geospatial.V1.NetworkType GetCurrentNetworkType()
    {
        return Connectivity.NetworkAccess switch
        {
            NetworkAccess.Internet when Connectivity.ConnectionProfiles.Contains(ConnectionProfile.WiFi)
                => Geospatial.V1.NetworkType.Wifi,
            NetworkAccess.Internet when Connectivity.ConnectionProfiles.Contains(ConnectionProfile.Cellular)
                => Geospatial.V1.NetworkType.Cellular,
            NetworkAccess.ConstrainedInternet => Geospatial.V1.NetworkType.Limited,
            NetworkAccess.Local => Geospatial.V1.NetworkType.Limited,
            NetworkAccess.None => Geospatial.V1.NetworkType.Offline,
            _ => Geospatial.V1.NetworkType.Unspecified
        };
    }

    private static Geospatial.V1.BatteryLevel GetCurrentBatteryLevel()
    {
        try
        {
            var level = Battery.ChargeLevel;
            return level switch
            {
                > 0.5 => Geospatial.V1.BatteryLevel.High,
                > 0.2 => Geospatial.V1.BatteryLevel.Medium,
                _ => Geospatial.V1.BatteryLevel.Low
            };
        }
        catch
        {
            return Geospatial.V1.BatteryLevel.Medium; // Safe default
        }
    }
}

/// <summary>
/// Mobile-optimized form control definition for UI rendering.
/// </summary>
public class MobileFormControl
{
    public string ControlId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Hint { get; set; } = string.Empty;
    public bool Required { get; set; }
    public MobileControlType Type { get; set; }
    public Dictionary<string, object> Properties { get; set; } = new();
    public int DisplayOrder { get; set; }
    public Geospatial.V1.MobileControlHints? Hints { get; set; }
}

/// <summary>
/// Mobile control types for native rendering.
/// </summary>
public enum MobileControlType
{
    Entry,
    Editor,
    NumericEntry,
    DatePicker,
    TimePicker,
    Picker,
    Switch,
    LocationButton,
    ImageButton,
    CheckBoxGroup,
    RadioGroup
}

/// <summary>
/// Form progress tracking.
/// </summary>
public class FormProgress
{
    public int TotalFields { get; set; }
    public int CompletedFields { get; set; }
    public double PercentComplete { get; set; }
}

/// <summary>
/// Form validation result.
/// </summary>
public class FormValidationResult
{
    public string FieldId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public ValidationSeverity Severity { get; set; }
}

/// <summary>
/// Validation severity levels.
/// </summary>
public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// Form attachment for media files.
/// </summary>
public class FormAttachment
{
    public string FieldName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime CapturedAt { get; set; }
}