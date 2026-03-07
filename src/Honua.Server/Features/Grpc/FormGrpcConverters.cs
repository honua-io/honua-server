// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Forms.Domain;
using System.Collections.Immutable;
using Proto = Honua.Server.Features.Grpc.Proto;

namespace Honua.Server.Features.Grpc;

/// <summary>
/// Conversion helpers between form domain models and gRPC proto messages.
/// </summary>
internal static class FormGrpcConverters
{
    #region FormDefinition Conversions

    public static Proto.FormDefinition ToProtoFormDefinition(FormDefinition form)
    {
        var protoForm = new Proto.FormDefinition
        {
            FormId = form.FormId,
            Title = form.Title,
            Description = form.Description,
            Version = form.Version,
            TargetServiceId = form.TargetServiceId,
            TargetLayerId = form.TargetLayerId
        };

        // Convert controls
        protoForm.Controls.AddRange(form.Controls.Select(ToProtoFormControl));

        // Convert bindings
        protoForm.Bindings.AddRange(form.Bindings.Select(ToProtoFormBinding));

        // Convert layout
        if (form.Layout != null)
        {
            protoForm.LayoutHints = ToProtoFormLayout(form.Layout);
        }

        // Convert groups
        protoForm.Groups.AddRange(form.Groups.Select(ToProtoFormGroup));

        return protoForm;
    }

    public static Proto.FormControl ToProtoFormControl(FormControl control)
    {
        var protoControl = new Proto.FormControl
        {
            ControlId = control.ControlId,
            Label = control.Label,
            Hint = control.Hint,
            Required = control.Required,
            BindReference = control.BindReference,
            GroupId = control.GroupId ?? "",
            DisplayOrder = control.DisplayOrder
        };

        // Set control type-specific configuration
        switch (control.ControlType)
        {
            case FormControlType.TextInput:
                protoControl.TextInput = CreateTextInputControl(control.Properties);
                break;
            case FormControlType.NumericInput:
                protoControl.NumericInput = CreateNumericInputControl(control.Properties);
                break;
            case FormControlType.Select:
                protoControl.SelectControl = CreateSelectControl(control.Properties);
                break;
            case FormControlType.DateTime:
                protoControl.DatetimeControl = CreateDateTimeControl(control.Properties);
                break;
            case FormControlType.Location:
                protoControl.LocationControl = CreateLocationControl(control.Properties);
                break;
            case FormControlType.Media:
                protoControl.MediaControl = CreateMediaControl(control.Properties);
                break;
            case FormControlType.Boolean:
                protoControl.BooleanControl = CreateBooleanControl(control.Properties);
                break;
            case FormControlType.Group:
                protoControl.GroupControl = CreateGroupControl(control.Properties);
                break;
            case FormControlType.Separator:
                protoControl.SeparatorControl = CreateSeparatorControl(control.Properties);
                break;
        }

        // Set mobile hints
        if (control.MobileHints != null)
        {
            protoControl.MobileHints = ToProtoMobileControlHints(control.MobileHints);
        }

        return protoControl;
    }

    public static Proto.FormBinding ToProtoFormBinding(FormBinding binding)
    {
        var protoBinding = new Proto.FormBinding
        {
            BindingId = binding.BindingId,
            ControlId = binding.ControlId,
            TargetFieldName = binding.TargetFieldName,
            FieldType = GrpcConversionHelpers.ToProtoFieldType(binding.FieldType),
            Required = binding.Required
        };

        // Convert validation rules
        protoBinding.ValidationRules.AddRange(
            binding.ValidationRules.Select(ToProtoValidationRule));

        // Convert default value
        if (binding.DefaultValue != null)
        {
            protoBinding.DefaultValue = ToProtoDefaultValue(binding.DefaultValue);
        }

        // Convert calculated value
        if (binding.CalculatedValue != null)
        {
            protoBinding.CalculatedValue = ToProtoCalculatedValue(binding.CalculatedValue);
        }

        return protoBinding;
    }

    #endregion

    #region FormMetadata Conversions

    public static Proto.FormMetadata ToProtoFormMetadata(FormMetadata metadata)
    {
        var protoMetadata = new Proto.FormMetadata
        {
            FormId = metadata.FormId,
            Title = metadata.Title,
            Description = metadata.Description,
            Version = metadata.Version,
            Author = metadata.Author,
            CreatedAt = metadata.CreatedAt.ToUnixTimeMilliseconds(),
            ModifiedAt = metadata.ModifiedAt.ToUnixTimeMilliseconds(),
            Status = ToProtoFormStatus(metadata.Status),
            TargetServiceId = metadata.TargetServiceId,
            TargetLayerId = metadata.TargetLayerId,
            Compatibility = ToProtoFormCompatibility(metadata.Compatibility)
        };

        protoMetadata.Tags.AddRange(metadata.Tags);
        return protoMetadata;
    }

    public static Proto.FormStatus ToProtoFormStatus(FormStatus status) => status switch
    {
        FormStatus.Draft => Proto.FormStatus.Draft,
        FormStatus.Published => Proto.FormStatus.Published,
        FormStatus.Deprecated => Proto.FormStatus.Deprecated,
        FormStatus.Archived => Proto.FormStatus.Archived,
        _ => Proto.FormStatus.Unspecified
    };

    public static Proto.FormCompatibility ToProtoFormCompatibility(FormCompatibility compatibility)
    {
        var protoCompatibility = new Proto.FormCompatibility
        {
            MinAppVersion = compatibility.MinAppVersion,
            RequiresOnline = compatibility.RequiresOnline
        };

        protoCompatibility.SupportedPlatforms.AddRange(compatibility.SupportedPlatforms);
        protoCompatibility.RequiredPermissions.AddRange(compatibility.RequiredPermissions);

        return protoCompatibility;
    }

    #endregion

    #region Validation Conversions

    public static Proto.ValidationRule ToProtoValidationRule(ValidationRule rule)
    {
        var protoRule = new Proto.ValidationRule
        {
            RuleId = rule.RuleId,
            ValidationType = ToProtoValidationType(rule.ValidationType),
            ErrorMessage = rule.ErrorMessage,
            Severity = ToProtoValidationSeverity(rule.Severity)
        };

        // Set rule-specific configuration
        switch (rule.Configuration)
        {
            case RangeValidationConfiguration range:
                protoRule.Range = new Proto.RangeValidation
                {
                    MinValue = range.MinValue,
                    MaxValue = range.MaxValue,
                    Inclusive = range.Inclusive
                };
                break;
            case LengthValidationConfiguration length:
                protoRule.Length = new Proto.LengthValidation
                {
                    MinLength = length.MinLength,
                    MaxLength = length.MaxLength
                };
                break;
            case PatternValidationConfiguration pattern:
                protoRule.Pattern = new Proto.PatternValidation
                {
                    RegexPattern = pattern.RegexPattern,
                    CaseSensitive = pattern.CaseSensitive
                };
                break;
        }

        return protoRule;
    }

    public static Proto.ValidationType ToProtoValidationType(ValidationType type) => type switch
    {
        ValidationType.Required => Proto.ValidationType.Required,
        ValidationType.Range => Proto.ValidationType.Range,
        ValidationType.Length => Proto.ValidationType.Length,
        ValidationType.Pattern => Proto.ValidationType.Pattern,
        ValidationType.Custom => Proto.ValidationType.Custom,
        ValidationType.Conditional => Proto.ValidationType.Conditional,
        _ => Proto.ValidationType.Unspecified
    };

    public static Proto.ValidationSeverity ToProtoValidationSeverity(ValidationSeverity severity) => severity switch
    {
        ValidationSeverity.Info => Proto.ValidationSeverity.Info,
        ValidationSeverity.Warning => Proto.ValidationSeverity.Warning,
        ValidationSeverity.Error => Proto.ValidationSeverity.Error,
        _ => Proto.ValidationSeverity.Unspecified
    };

    public static Proto.ValidationIssue ToProtoValidationIssue(ValidationIssue issue)
    {
        return new Proto.ValidationIssue
        {
            FieldId = issue.FieldId ?? "",
            Severity = ToProtoValidationSeverity(issue.Severity),
            Message = issue.Message,
            SuggestedValue = issue.SuggestedValue ?? ""
        };
    }

    #endregion

    #region Value Conversions

    public static object? FromProtoAttributeValue(Proto.AttributeValue protoValue)
    {
        return protoValue.ValueCase switch
        {
            Proto.AttributeValue.ValueOneofCase.StringValue => protoValue.StringValue,
            Proto.AttributeValue.ValueOneofCase.Int32Value => protoValue.Int32Value,
            Proto.AttributeValue.ValueOneofCase.Int64Value => protoValue.Int64Value,
            Proto.AttributeValue.ValueOneofCase.DoubleValue => protoValue.DoubleValue,
            Proto.AttributeValue.ValueOneofCase.FloatValue => protoValue.FloatValue,
            Proto.AttributeValue.ValueOneofCase.BoolValue => protoValue.BoolValue,
            Proto.AttributeValue.ValueOneofCase.DatetimeValue =>
                DateTimeOffset.FromUnixTimeMilliseconds(protoValue.DatetimeValue).DateTime,
            Proto.AttributeValue.ValueOneofCase.BytesValue => protoValue.BytesValue.ToByteArray(),
            _ => null
        };
    }

    public static Proto.AttributeValue ToProtoAttributeValue(object? value)
    {
        var protoValue = new Proto.AttributeValue();

        switch (value)
        {
            case string stringValue:
                protoValue.StringValue = stringValue;
                break;
            case int intValue:
                protoValue.Int32Value = intValue;
                break;
            case long longValue:
                protoValue.Int64Value = longValue;
                break;
            case double doubleValue:
                protoValue.DoubleValue = doubleValue;
                break;
            case float floatValue:
                protoValue.FloatValue = floatValue;
                break;
            case bool boolValue:
                protoValue.BoolValue = boolValue;
                break;
            case DateTime dateTimeValue:
                protoValue.DatetimeValue = new DateTimeOffset(dateTimeValue).ToUnixTimeMilliseconds();
                break;
            case DateTimeOffset dateTimeOffsetValue:
                protoValue.DatetimeValue = dateTimeOffsetValue.ToUnixTimeMilliseconds();
                break;
            case byte[] bytesValue:
                protoValue.BytesValue = Google.Protobuf.ByteString.CopyFrom(bytesValue);
                break;
            default:
                protoValue.NullValue = Proto.NullValue.NullValue;
                break;
        }

        return protoValue;
    }

    #endregion

    #region Helper Methods for Control Creation

    private static Proto.TextInputControl CreateTextInputControl(ImmutableDictionary<string, object> properties)
    {
        return new Proto.TextInputControl
        {
            Multiline = GetBoolProperty(properties, "multiline", false),
            MaxLength = GetIntProperty(properties, "maxLength", 0),
            Placeholder = GetStringProperty(properties, "placeholder", ""),
            InputType = GetEnumProperty<Proto.TextInputType>(properties, "inputType", Proto.TextInputType.Text),
            ValidationPattern = GetStringProperty(properties, "validationPattern", "")
        };
    }

    private static Proto.NumericInputControl CreateNumericInputControl(ImmutableDictionary<string, object> properties)
    {
        return new Proto.NumericInputControl
        {
            NumericType = GetEnumProperty<Proto.NumericType>(properties, "numericType", Proto.NumericType.Integer),
            MinValue = GetDoubleProperty(properties, "minValue", 0),
            MaxValue = GetDoubleProperty(properties, "maxValue", 0),
            DecimalPlaces = GetIntProperty(properties, "decimalPlaces", 0),
            Placeholder = GetStringProperty(properties, "placeholder", "")
        };
    }

    private static Proto.SelectControl CreateSelectControl(ImmutableDictionary<string, object> properties)
    {
        var selectControl = new Proto.SelectControl
        {
            AllowMultiple = GetBoolProperty(properties, "allowMultiple", false),
            StyleHint = GetEnumProperty<Proto.SelectStyle>(properties, "styleHint", Proto.SelectStyle.Dropdown),
            AllowOther = GetBoolProperty(properties, "allowOther", false)
        };

        // Add options if present
        if (properties.TryGetValue("options", out var optionsValue) && optionsValue is IEnumerable<object> options)
        {
            foreach (var option in options)
            {
                if (option is IDictionary<string, object> optionDict)
                {
                    selectControl.Options.Add(new Proto.SelectOption
                    {
                        Value = optionDict.TryGetValue("value", out var v) ? v?.ToString() ?? "" : "",
                        Label = optionDict.TryGetValue("label", out var l) ? l?.ToString() ?? "" : "",
                        IconUrl = optionDict.TryGetValue("iconUrl", out var i) ? i?.ToString() ?? "" : "",
                        DefaultSelected = optionDict.TryGetValue("defaultSelected", out var d) && d is bool b && b
                    });
                }
            }
        }

        return selectControl;
    }

    private static Proto.DateTimeControl CreateDateTimeControl(ImmutableDictionary<string, object> properties)
    {
        return new Proto.DateTimeControl
        {
            DatetimeType = GetEnumProperty<Proto.DateTimeType>(properties, "dateTimeType", Proto.DateTimeType.Date),
            MinDate = GetLongProperty(properties, "minDate", 0),
            MaxDate = GetLongProperty(properties, "maxDate", 0),
            DefaultToNow = GetBoolProperty(properties, "defaultToNow", false)
        };
    }

    private static Proto.LocationControl CreateLocationControl(ImmutableDictionary<string, object> properties)
    {
        return new Proto.LocationControl
        {
            RequireAccuracy = GetBoolProperty(properties, "requireAccuracy", false),
            MinAccuracyMeters = GetDoubleProperty(properties, "minAccuracyMeters", 10),
            EnableMapSelection = GetBoolProperty(properties, "enableMapSelection", true),
            CaptureAltitude = GetBoolProperty(properties, "captureAltitude", false),
            AutoCapture = GetBoolProperty(properties, "autoCapture", false)
        };
    }

    private static Proto.MediaControl CreateMediaControl(ImmutableDictionary<string, object> properties)
    {
        var mediaControl = new Proto.MediaControl
        {
            MediaType = GetEnumProperty<Proto.MediaType>(properties, "mediaType", Proto.MediaType.Photo),
            Required = GetBoolProperty(properties, "required", false),
            MaxFileSizeMb = GetIntProperty(properties, "maxFileSizeMb", 10),
            EnableAnnotation = GetBoolProperty(properties, "enableAnnotation", false),
            QualityHint = GetEnumProperty<Proto.MediaQuality>(properties, "qualityHint", Proto.MediaQuality.Medium)
        };

        // Add accepted formats
        if (properties.TryGetValue("acceptedFormats", out var formatsValue) && formatsValue is IEnumerable<string> formats)
        {
            mediaControl.AcceptedFormats.AddRange(formats);
        }

        return mediaControl;
    }

    private static Proto.BooleanControl CreateBooleanControl(ImmutableDictionary<string, object> properties)
    {
        return new Proto.BooleanControl
        {
            Style = GetEnumProperty<Proto.BooleanStyle>(properties, "style", Proto.BooleanStyle.Checkbox),
            TrueLabel = GetStringProperty(properties, "trueLabel", "Yes"),
            FalseLabel = GetStringProperty(properties, "falseLabel", "No")
        };
    }

    private static Proto.GroupControl CreateGroupControl(ImmutableDictionary<string, object> properties)
    {
        return new Proto.GroupControl
        {
            GroupTitle = GetStringProperty(properties, "groupTitle", ""),
            Collapsible = GetBoolProperty(properties, "collapsible", false),
            DefaultCollapsed = GetBoolProperty(properties, "defaultCollapsed", false),
            Style = GetEnumProperty<Proto.GroupStyle>(properties, "style", Proto.GroupStyle.Card)
        };
    }

    private static Proto.SeparatorControl CreateSeparatorControl(ImmutableDictionary<string, object> properties)
    {
        return new Proto.SeparatorControl
        {
            Label = GetStringProperty(properties, "label", ""),
            Style = GetEnumProperty<Proto.SeparatorStyle>(properties, "style", Proto.SeparatorStyle.Line)
        };
    }

    private static Proto.MobileControlHints ToProtoMobileControlHints(MobileControlHints hints)
    {
        return new Proto.MobileControlHints
        {
            PreferredInputMethod = ToProtoInputMethod(hints.PreferredInputMethod),
            KeyboardType = ToProtoKeyboardType(hints.KeyboardType),
            AutoFocus = hints.AutoFocus,
            AutoCapitalize = hints.AutoCapitalize,
            SpellCheck = hints.SpellCheck,
            MaxDisplayLines = hints.MaxDisplayLines
        };
    }

    private static Proto.InputMethod ToProtoInputMethod(InputMethod method) => method switch
    {
        InputMethod.Keyboard => Proto.InputMethod.Keyboard,
        InputMethod.Voice => Proto.InputMethod.Voice,
        InputMethod.Handwriting => Proto.InputMethod.Handwriting,
        InputMethod.CameraOcr => Proto.InputMethod.CameraOcr,
        _ => Proto.InputMethod.Unspecified
    };

    private static Proto.KeyboardType ToProtoKeyboardType(KeyboardType type) => type switch
    {
        KeyboardType.Default => Proto.KeyboardType.Default,
        KeyboardType.Numeric => Proto.KeyboardType.Numeric,
        KeyboardType.Email => Proto.KeyboardType.Email,
        KeyboardType.Url => Proto.KeyboardType.Url,
        KeyboardType.Phone => Proto.KeyboardType.Phone,
        KeyboardType.Decimal => Proto.KeyboardType.Decimal,
        _ => Proto.KeyboardType.Unspecified
    };

    // Additional helper methods for layout, groups, etc.
    private static Proto.FormLayout ToProtoFormLayout(FormLayout layout)
    {
        var protoLayout = new Proto.FormLayout
        {
            DefaultLayout = ToProtoLayoutType(layout.DefaultLayoutType),
            ShowProgressIndicator = layout.ShowProgressIndicator,
            EnableNavigationButtons = layout.EnableNavigationButtons,
            NavigationStyle = ToProtoNavigationStyle(layout.NavigationStyle)
        };

        if (layout.Theme != null)
        {
            protoLayout.Theme = ToProtoFormTheme(layout.Theme);
        }

        return protoLayout;
    }

    private static Proto.LayoutType ToProtoLayoutType(LayoutType type) => type switch
    {
        LayoutType.Vertical => Proto.LayoutType.Vertical,
        LayoutType.Horizontal => Proto.LayoutType.Horizontal,
        LayoutType.Grid => Proto.LayoutType.Grid,
        LayoutType.Tabs => Proto.LayoutType.Tabs,
        _ => Proto.LayoutType.Unspecified
    };

    private static Proto.NavigationStyle ToProtoNavigationStyle(NavigationStyle style) => style switch
    {
        NavigationStyle.Buttons => Proto.NavigationStyle.Buttons,
        NavigationStyle.Tabs => Proto.NavigationStyle.Tabs,
        NavigationStyle.Pages => Proto.NavigationStyle.Pages,
        NavigationStyle.SinglePage => Proto.NavigationStyle.SinglePage,
        _ => Proto.NavigationStyle.Unspecified
    };

    private static Proto.FormTheme ToProtoFormTheme(FormTheme theme)
    {
        return new Proto.FormTheme
        {
            PrimaryColor = theme.PrimaryColor,
            AccentColor = theme.AccentColor,
            BackgroundColor = theme.BackgroundColor,
            DarkMode = theme.DarkMode,
            FontFamily = theme.FontFamily
        };
    }

    private static Proto.FormGroup ToProtoFormGroup(FormGroup group)
    {
        var protoGroup = new Proto.FormGroup
        {
            GroupId = group.GroupId,
            Title = group.Title,
            Description = group.Description,
            DisplayOrder = group.DisplayOrder,
            Layout = ToProtoGroupLayout(group.Layout)
        };

        protoGroup.ControlIds.AddRange(group.ControlIds);
        return protoGroup;
    }

    private static Proto.GroupLayout ToProtoGroupLayout(GroupLayout layout)
    {
        return new Proto.GroupLayout
        {
            LayoutType = ToProtoLayoutType(layout.LayoutType),
            Columns = layout.Columns,
            Collapsible = layout.Collapsible,
            DefaultCollapsed = layout.DefaultCollapsed
        };
    }

    private static Proto.DefaultValue ToProtoDefaultValue(DefaultValue defaultValue)
    {
        var protoDefault = new Proto.DefaultValue();

        switch (defaultValue)
        {
            case StaticDefaultValue staticValue:
                protoDefault.StaticValue = ToProtoAttributeValue(staticValue.Value);
                break;
            case FunctionDefaultValue functionValue:
                protoDefault.Function = ToProtoDefaultValueFunction(functionValue.Function);
                break;
        }

        return protoDefault;
    }

    private static Proto.DefaultValueFunction ToProtoDefaultValueFunction(DefaultValueFunction function) => function switch
    {
        DefaultValueFunction.Now => Proto.DefaultValueFunction.Now,
        DefaultValueFunction.Today => Proto.DefaultValueFunction.Today,
        DefaultValueFunction.Uuid => Proto.DefaultValueFunction.Uuid,
        DefaultValueFunction.CurrentLocation => Proto.DefaultValueFunction.CurrentLocation,
        DefaultValueFunction.DeviceId => Proto.DefaultValueFunction.DeviceId,
        DefaultValueFunction.UserId => Proto.DefaultValueFunction.UserId,
        _ => Proto.DefaultValueFunction.Unspecified
    };

    private static Proto.CalculatedValue ToProtoCalculatedValue(CalculatedValue calculatedValue)
    {
        var protoCalculated = new Proto.CalculatedValue
        {
            Expression = calculatedValue.Expression,
            RecalculateOnChange = calculatedValue.RecalculateOnChange
        };

        protoCalculated.DependencyFields.AddRange(calculatedValue.DependencyFields);
        return protoCalculated;
    }

    // Property extraction helpers
    private static string GetStringProperty(ImmutableDictionary<string, object> properties, string key, string defaultValue)
    {
        return properties.TryGetValue(key, out var value) && value is string stringValue
            ? stringValue
            : defaultValue;
    }

    private static bool GetBoolProperty(ImmutableDictionary<string, object> properties, string key, bool defaultValue)
    {
        return properties.TryGetValue(key, out var value) && value is bool boolValue
            ? boolValue
            : defaultValue;
    }

    private static int GetIntProperty(ImmutableDictionary<string, object> properties, string key, int defaultValue)
    {
        return properties.TryGetValue(key, out var value) && value is int intValue
            ? intValue
            : defaultValue;
    }

    private static long GetLongProperty(ImmutableDictionary<string, object> properties, string key, long defaultValue)
    {
        return properties.TryGetValue(key, out var value) && value is long longValue
            ? longValue
            : defaultValue;
    }

    private static double GetDoubleProperty(ImmutableDictionary<string, object> properties, string key, double defaultValue)
    {
        return properties.TryGetValue(key, out var value) && value is double doubleValue
            ? doubleValue
            : defaultValue;
    }

    private static T GetEnumProperty<T>(ImmutableDictionary<string, object> properties, string key, T defaultValue)
        where T : struct, Enum
    {
        if (properties.TryGetValue(key, out var value))
        {
            if (value is T enumValue)
                return enumValue;
            if (value is string stringValue && Enum.TryParse<T>(stringValue, true, out var parsedValue))
                return parsedValue;
        }
        return defaultValue;
    }

    #endregion
}
