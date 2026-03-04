// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Grpc.Proto;

namespace FieldDataCollection.Services;

/// <summary>
/// gRPC-native form service interface providing type-safe form operations.
/// Designed as next-generation alternative to OpenRosa XML with mobile optimization.
/// </summary>
public interface IGrpcFormService
{
    /// <summary>
    /// Retrieves a type-safe form definition optimized for mobile rendering.
    /// </summary>
    /// <param name="formId">Form identifier.</param>
    /// <param name="serviceId">Target feature service for data submission.</param>
    /// <param name="layerId">Target layer for feature creation.</param>
    /// <param name="capabilities">Device capabilities for optimization.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Complete form definition with mobile optimizations.</returns>
    Task<GetFormDefinitionResponse> GetFormDefinitionAsync(
        string formId,
        string serviceId,
        int layerId,
        MobileCapabilities? capabilities = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits completed form data as feature edits via efficient gRPC.
    /// </summary>
    /// <param name="formId">Form identifier.</param>
    /// <param name="instance">Completed form instance.</param>
    /// <param name="attachments">Media attachments (photos, etc.).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Submission result with validation feedback.</returns>
    Task<SubmitFormDataResponse> SubmitFormDataAsync(
        string formId,
        FormInstance instance,
        List<FormAttachment> attachments,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates form data against definition rules without submission.
    /// </summary>
    /// <param name="formId">Form identifier.</param>
    /// <param name="instance">Form instance to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation results with actionable feedback.</returns>
    Task<ValidateFormDataResponse> ValidateFormDataAsync(
        string formId,
        FormInstance instance,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts real-time collaborative form editing session.
    /// </summary>
    /// <param name="sessionId">Collaboration session identifier.</param>
    /// <param name="formId">Form identifier.</param>
    /// <param name="instanceId">Form instance identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stream of real-time form updates.</returns>
    IAsyncEnumerable<FormUpdateResponse> StreamFormUpdatesAsync(
        string sessionId,
        string formId,
        string instanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends form update for real-time collaboration.
    /// </summary>
    /// <param name="sessionId">Collaboration session identifier.</param>
    /// <param name="update">Form field update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Update acknowledgment.</returns>
    Task SendFormUpdateAsync(
        string sessionId,
        FormUpdate update,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves available form catalog with filtering.
    /// </summary>
    /// <param name="serviceId">Filter by target service.</param>
    /// <param name="tags">Filter by form tags.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Available forms metadata.</returns>
    Task<GetFormMetadataResponse> GetFormCatalogAsync(
        string? serviceId = null,
        List<string>? tags = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Extensions for working with gRPC form specifications.
/// </summary>
public static class GrpcFormExtensions
{
    /// <summary>
    /// Converts gRPC form definition to mobile-optimized view models.
    /// </summary>
    public static List<MobileFormControl> ToMobileControls(this FormDefinition formDefinition)
    {
        var controls = new List<MobileFormControl>();

        foreach (var control in formDefinition.Controls)
        {
            var mobileControl = control.ControlType.InnerType switch
            {
                FormControl.ControlTypeOneofCase.TextInput => new MobileFormControl
                {
                    ControlId = control.ControlId,
                    Label = control.Label,
                    Hint = control.Hint,
                    Required = control.Required,
                    Type = MobileControlType.Entry,
                    Properties = CreateTextInputProperties(control.TextInput)
                },
                FormControl.ControlTypeOneofCase.NumericInput => new MobileFormControl
                {
                    ControlId = control.ControlId,
                    Label = control.Label,
                    Hint = control.Hint,
                    Required = control.Required,
                    Type = MobileControlType.NumericEntry,
                    Properties = CreateNumericInputProperties(control.NumericInput)
                },
                FormControl.ControlTypeOneofCase.LocationControl => new MobileFormControl
                {
                    ControlId = control.ControlId,
                    Label = control.Label,
                    Hint = control.Hint,
                    Required = control.Required,
                    Type = MobileControlType.LocationButton,
                    Properties = CreateLocationProperties(control.LocationControl)
                },
                FormControl.ControlTypeOneofCase.MediaControl => new MobileFormControl
                {
                    ControlId = control.ControlId,
                    Label = control.Label,
                    Hint = control.Hint,
                    Required = control.Required,
                    Type = MobileControlType.ImageButton,
                    Properties = CreateMediaProperties(control.MediaControl)
                },
                FormControl.ControlTypeOneofCase.SelectControl => new MobileFormControl
                {
                    ControlId = control.ControlId,
                    Label = control.Label,
                    Hint = control.Hint,
                    Required = control.Required,
                    Type = control.SelectControl.AllowMultiple ? MobileControlType.CheckBoxGroup : MobileControlType.Picker,
                    Properties = CreateSelectProperties(control.SelectControl)
                },
                FormControl.ControlTypeOneofCase.DatetimeControl => new MobileFormControl
                {
                    ControlId = control.ControlId,
                    Label = control.Label,
                    Hint = control.Hint,
                    Required = control.Required,
                    Type = control.DatetimeControl.DatetimeType switch
                    {
                        DateTimeType.Date => MobileControlType.DatePicker,
                        DateTimeType.Time => MobileControlType.TimePicker,
                        _ => MobileControlType.DatePicker
                    },
                    Properties = CreateDateTimeProperties(control.DatetimeControl)
                },
                FormControl.ControlTypeOneofCase.BooleanControl => new MobileFormControl
                {
                    ControlId = control.ControlId,
                    Label = control.Label,
                    Hint = control.Hint,
                    Required = control.Required,
                    Type = MobileControlType.Switch,
                    Properties = CreateBooleanProperties(control.BooleanControl)
                },
                _ => null
            };

            if (mobileControl != null)
            {
                controls.Add(mobileControl);
            }
        }

        return controls.OrderBy(c => c.DisplayOrder).ToList();
    }

    /// <summary>
    /// Gets current device capabilities for form optimization.
    /// </summary>
    public static MobileCapabilities GetCurrentDeviceCapabilities()
    {
        return new MobileCapabilities
        {
            HasCamera = MediaPicker.IsCaptureSupported,
            HasGps = true, // Assume GPS available on mobile devices
            Platform = DeviceInfo.Platform.ToString().ToLowerInvariant(),
            DeviceType = DeviceInfo.Idiom switch
            {
                DeviceIdiom.Phone => "phone",
                DeviceIdiom.Tablet => "tablet",
                DeviceIdiom.Desktop => "desktop",
                _ => "unknown"
            },
            NetworkType = Connectivity.NetworkAccess switch
            {
                NetworkAccess.Internet => NetworkType.Wifi, // Simplified
                NetworkAccess.ConstrainedInternet => NetworkType.Limited,
                NetworkAccess.Local => NetworkType.Limited,
                NetworkAccess.None => NetworkType.Offline,
                _ => NetworkType.Unspecified
            },
            BatteryLevel = Battery.ChargeLevel switch
            {
                > 0.5 => BatteryLevel.High,
                > 0.2 => BatteryLevel.Medium,
                _ => BatteryLevel.Low
            }
        };
    }

    /// <summary>
    /// Creates form instance from mobile form data.
    /// </summary>
    public static FormInstance CreateFormInstance(string formId, Dictionary<string, object?> formData)
    {
        var instance = new FormInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"),
            FormId = formId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ModifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Status = InstanceStatus.Complete
        };

        foreach (var kvp in formData)
        {
            if (kvp.Value != null)
            {
                instance.FieldValues[kvp.Key] = CreateAttributeValue(kvp.Value);
            }
        }

        return instance;
    }

    /// <summary>
    /// Creates attachment from mobile photo capture.
    /// </summary>
    public static async Task<FormAttachment> CreateFormAttachmentAsync(
        string fieldId,
        FileResult photo,
        AttachmentMetadata? metadata = null)
    {
        using var stream = await photo.OpenReadAsync();
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream);

        return new FormAttachment
        {
            AttachmentId = Guid.NewGuid().ToString("N"),
            FieldId = fieldId,
            Filename = photo.FileName ?? $"photo_{DateTime.Now:yyyyMMdd_HHmmss}.jpg",
            ContentType = "image/jpeg",
            FileSize = memoryStream.Length,
            Content = Google.Protobuf.ByteString.CopyFrom(memoryStream.ToArray()),
            Metadata = metadata ?? new AttachmentMetadata
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DeviceId = GetDeviceId()
            }
        };
    }

    private static Dictionary<string, object> CreateTextInputProperties(TextInputControl textInput)
    {
        return new Dictionary<string, object>
        {
            ["multiline"] = textInput.Multiline,
            ["maxLength"] = textInput.MaxLength,
            ["placeholder"] = textInput.Placeholder ?? "",
            ["inputType"] = textInput.InputType.ToString(),
            ["validationPattern"] = textInput.ValidationPattern ?? ""
        };
    }

    private static Dictionary<string, object> CreateNumericInputProperties(NumericInputControl numericInput)
    {
        return new Dictionary<string, object>
        {
            ["numericType"] = numericInput.NumericType.ToString(),
            ["minValue"] = numericInput.MinValue,
            ["maxValue"] = numericInput.MaxValue,
            ["decimalPlaces"] = numericInput.DecimalPlaces,
            ["placeholder"] = numericInput.Placeholder ?? ""
        };
    }

    private static Dictionary<string, object> CreateLocationProperties(LocationControl locationControl)
    {
        return new Dictionary<string, object>
        {
            ["requireAccuracy"] = locationControl.RequireAccuracy,
            ["minAccuracyMeters"] = locationControl.MinAccuracyMeters,
            ["enableMapSelection"] = locationControl.EnableMapSelection,
            ["captureAltitude"] = locationControl.CaptureAltitude,
            ["autoCapture"] = locationControl.AutoCapture
        };
    }

    private static Dictionary<string, object> CreateMediaProperties(MediaControl mediaControl)
    {
        return new Dictionary<string, object>
        {
            ["mediaType"] = mediaControl.MediaType.ToString(),
            ["maxFileSizeMb"] = mediaControl.MaxFileSizeMb,
            ["acceptedFormats"] = mediaControl.AcceptedFormats.ToList(),
            ["enableAnnotation"] = mediaControl.EnableAnnotation,
            ["qualityHint"] = mediaControl.QualityHint.ToString()
        };
    }

    private static Dictionary<string, object> CreateSelectProperties(SelectControl selectControl)
    {
        return new Dictionary<string, object>
        {
            ["allowMultiple"] = selectControl.AllowMultiple,
            ["options"] = selectControl.Options.Select(o => new { o.Value, o.Label, o.DefaultSelected }).ToList(),
            ["styleHint"] = selectControl.StyleHint.ToString(),
            ["allowOther"] = selectControl.AllowOther
        };
    }

    private static Dictionary<string, object> CreateDateTimeProperties(DateTimeControl dateTimeControl)
    {
        return new Dictionary<string, object>
        {
            ["dateTimeType"] = dateTimeControl.DatetimeType.ToString(),
            ["minDate"] = dateTimeControl.MinDate,
            ["maxDate"] = dateTimeControl.MaxDate,
            ["defaultToNow"] = dateTimeControl.DefaultToNow
        };
    }

    private static Dictionary<string, object> CreateBooleanProperties(BooleanControl booleanControl)
    {
        return new Dictionary<string, object>
        {
            ["style"] = booleanControl.Style.ToString(),
            ["trueLabel"] = booleanControl.TrueLabel ?? "Yes",
            ["falseLabel"] = booleanControl.FalseLabel ?? "No"
        };
    }

    private static AttributeValue CreateAttributeValue(object value)
    {
        var attributeValue = new AttributeValue();

        switch (value)
        {
            case string stringValue:
                attributeValue.StringValue = stringValue;
                break;
            case int intValue:
                attributeValue.Int32Value = intValue;
                break;
            case long longValue:
                attributeValue.Int64Value = longValue;
                break;
            case double doubleValue:
                attributeValue.DoubleValue = doubleValue;
                break;
            case float floatValue:
                attributeValue.FloatValue = floatValue;
                break;
            case bool boolValue:
                attributeValue.BoolValue = boolValue;
                break;
            case DateTime dateTimeValue:
                attributeValue.DatetimeValue = new DateTimeOffset(dateTimeValue).ToUnixTimeMilliseconds();
                break;
            case DateTimeOffset dateTimeOffsetValue:
                attributeValue.DatetimeValue = dateTimeOffsetValue.ToUnixTimeMilliseconds();
                break;
            case byte[] bytesValue:
                attributeValue.BytesValue = Google.Protobuf.ByteString.CopyFrom(bytesValue);
                break;
            default:
                attributeValue.NullValue = NullValue.NullValue;
                break;
        }

        return attributeValue;
    }

    private static string GetDeviceId()
    {
        // Platform-specific device ID generation
        return DeviceInfo.Name + "_" + DeviceInfo.Model;
    }
}

/// <summary>
/// Mobile-optimized form control definition.
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