// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Features.Forms.Domain;

/// <summary>
/// Core form definition for gRPC-native forms.
/// </summary>
/// <param name="FormId">Unique form identifier.</param>
/// <param name="Title">Human-readable form title.</param>
/// <param name="Description">Form description.</param>
/// <param name="Version">Form version string.</param>
/// <param name="TargetServiceId">Target feature service for data submission.</param>
/// <param name="TargetLayerId">Target layer for feature creation.</param>
/// <param name="Controls">List of form controls.</param>
/// <param name="Bindings">Control-to-field bindings.</param>
/// <param name="Layout">Form layout configuration.</param>
/// <param name="Groups">Form control groupings.</param>
/// <param name="Metadata">Form metadata.</param>
public record FormDefinition(
    string FormId,
    string Title,
    string Description,
    string Version,
    string TargetServiceId,
    int TargetLayerId,
    ImmutableList<FormControl> Controls,
    ImmutableList<FormBinding> Bindings,
    FormLayout? Layout,
    ImmutableList<FormGroup> Groups,
    ImmutableList<WorkflowAction> WorkflowActions,
    FormAccessControl? AccessControl,
    FormMetadata Metadata);

/// <summary>
/// Individual form control definition.
/// </summary>
/// <param name="ControlId">Unique control identifier.</param>
/// <param name="Label">Control label.</param>
/// <param name="Hint">Hint text for users.</param>
/// <param name="Required">Whether control is required.</param>
/// <param name="BindReference">Reference to form binding.</param>
/// <param name="GroupId">Optional group membership.</param>
/// <param name="DisplayOrder">Display order within form.</param>
/// <param name="ControlType">Type of control.</param>
/// <param name="Properties">Control-specific properties.</param>
/// <param name="MobileHints">Mobile-specific rendering hints.</param>
public record FormControl(
    string ControlId,
    string Label,
    string Hint,
    bool Required,
    string BindReference,
    string? GroupId,
    int DisplayOrder,
    FormControlType ControlType,
    ImmutableDictionary<string, object> Properties,
    MobileControlHints? MobileHints,
    string VisibilityExpression = "",
    string ReadOnlyExpression = "");

/// <summary>
/// Form control types.
/// </summary>
public enum FormControlType
{
    TextInput,
    NumericInput,
    Select,
    DateTime,
    Location,
    Media,
    Boolean,
    Group,
    Separator
}

/// <summary>
/// Control-to-field binding definition.
/// </summary>
/// <param name="BindingId">Unique binding identifier.</param>
/// <param name="ControlId">Associated control ID.</param>
/// <param name="TargetFieldName">Target feature attribute field.</param>
/// <param name="FieldType">Expected field type.</param>
/// <param name="Required">Whether field is required.</param>
/// <param name="ValidationRules">Field validation rules.</param>
/// <param name="DefaultValue">Default value configuration.</param>
/// <param name="CalculatedValue">Calculated value configuration.</param>
public record FormBinding(
    string BindingId,
    string ControlId,
    string TargetFieldName,
    FieldType FieldType,
    bool Required,
    ImmutableList<ValidationRule> ValidationRules,
    DefaultValue? DefaultValue,
    CalculatedValue? CalculatedValue);

/// <summary>
/// Form layout configuration.
/// </summary>
/// <param name="DefaultLayoutType">Default layout type.</param>
/// <param name="ShowProgressIndicator">Whether to show progress.</param>
/// <param name="EnableNavigationButtons">Enable prev/next buttons.</param>
/// <param name="NavigationStyle">Navigation style.</param>
/// <param name="Theme">Form theme configuration.</param>
public record FormLayout(
    LayoutType DefaultLayoutType,
    bool ShowProgressIndicator,
    bool EnableNavigationButtons,
    NavigationStyle NavigationStyle,
    FormTheme? Theme);

/// <summary>
/// Form control grouping.
/// </summary>
/// <param name="GroupId">Unique group identifier.</param>
/// <param name="Title">Group title.</param>
/// <param name="Description">Group description.</param>
/// <param name="ControlIds">Controls in this group.</param>
/// <param name="DisplayOrder">Group display order.</param>
/// <param name="Layout">Group layout configuration.</param>
public record FormGroup(
    string GroupId,
    string Title,
    string Description,
    ImmutableList<string> ControlIds,
    int DisplayOrder,
    GroupLayout Layout);

/// <summary>
/// Group layout configuration.
/// </summary>
/// <param name="LayoutType">Layout type for group.</param>
/// <param name="Columns">Number of columns for grid layout.</param>
/// <param name="Collapsible">Whether group can be collapsed.</param>
/// <param name="DefaultCollapsed">Default collapsed state.</param>
public record GroupLayout(
    LayoutType LayoutType,
    int Columns,
    bool Collapsible,
    bool DefaultCollapsed);

/// <summary>
/// Layout types.
/// </summary>
public enum LayoutType
{
    Vertical,
    Horizontal,
    Grid,
    Tabs
}

/// <summary>
/// Navigation styles.
/// </summary>
public enum NavigationStyle
{
    Buttons,
    Tabs,
    Pages,
    SinglePage
}

/// <summary>
/// Form theme configuration.
/// </summary>
/// <param name="PrimaryColor">Primary color (hex).</param>
/// <param name="AccentColor">Accent color (hex).</param>
/// <param name="BackgroundColor">Background color (hex).</param>
/// <param name="DarkMode">Whether to use dark mode.</param>
/// <param name="FontFamily">Font family name.</param>
public record FormTheme(
    string PrimaryColor,
    string AccentColor,
    string BackgroundColor,
    bool DarkMode,
    string FontFamily);

/// <summary>
/// Mobile control rendering hints.
/// </summary>
/// <param name="PreferredInputMethod">Preferred input method.</param>
/// <param name="KeyboardType">Keyboard type for text inputs.</param>
/// <param name="AutoFocus">Whether to auto-focus this control.</param>
/// <param name="AutoCapitalize">Whether to auto-capitalize text.</param>
/// <param name="SpellCheck">Whether to enable spell check.</param>
/// <param name="MaxDisplayLines">Max lines for multi-line text.</param>
public record MobileControlHints(
    InputMethod PreferredInputMethod,
    KeyboardType KeyboardType,
    bool AutoFocus,
    bool AutoCapitalize,
    bool SpellCheck,
    int MaxDisplayLines);

/// <summary>
/// Input methods.
/// </summary>
public enum InputMethod
{
    Keyboard,
    Voice,
    Handwriting,
    CameraOcr
}

/// <summary>
/// Keyboard types.
/// </summary>
public enum KeyboardType
{
    Default,
    Numeric,
    Email,
    Url,
    Phone,
    DecimalNumber
}

/// <summary>
/// Default value configuration.
/// </summary>
public abstract record DefaultValue;

/// <summary>
/// Static default value.
/// </summary>
/// <param name="Value">Static value.</param>
public record StaticDefaultValue(object Value) : DefaultValue;

/// <summary>
/// Function-based default value.
/// </summary>
/// <param name="Function">Default value function.</param>
public record FunctionDefaultValue(DefaultValueFunction Function) : DefaultValue;

/// <summary>
/// Default value functions.
/// </summary>
public enum DefaultValueFunction
{
    Now,
    Today,
    Uuid,
    CurrentLocation,
    DeviceId,
    UserId
}

/// <summary>
/// Calculated value configuration.
/// </summary>
/// <param name="Expression">Calculation expression.</param>
/// <param name="DependencyFields">Fields this calculation depends on.</param>
/// <param name="RecalculateOnChange">Whether to recalculate when dependencies change.</param>
public record CalculatedValue(
    string Expression,
    ImmutableList<string> DependencyFields,
    bool RecalculateOnChange);

/// <summary>
/// Validation rule definition.
/// </summary>
/// <param name="RuleId">Unique rule identifier.</param>
/// <param name="ValidationType">Type of validation.</param>
/// <param name="ErrorMessage">Error message for validation failure.</param>
/// <param name="Severity">Validation severity.</param>
/// <param name="Configuration">Rule-specific configuration.</param>
public record ValidationRule(
    string RuleId,
    ValidationType ValidationType,
    string ErrorMessage,
    ValidationSeverity Severity,
    ValidationRuleConfiguration Configuration);

/// <summary>
/// Validation types.
/// </summary>
public enum ValidationType
{
    Required,
    Range,
    Length,
    Pattern,
    Custom,
    Conditional
}

/// <summary>
/// Validation severities.
/// </summary>
public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// Base class for validation rule configurations.
/// </summary>
public abstract record ValidationRuleConfiguration;

/// <summary>
/// Range validation configuration.
/// </summary>
/// <param name="MinValue">Minimum value.</param>
/// <param name="MaxValue">Maximum value.</param>
/// <param name="Inclusive">Whether range is inclusive.</param>
public record RangeValidationConfiguration(
    double MinValue,
    double MaxValue,
    bool Inclusive) : ValidationRuleConfiguration;

/// <summary>
/// Length validation configuration.
/// </summary>
/// <param name="MinLength">Minimum length.</param>
/// <param name="MaxLength">Maximum length.</param>
public record LengthValidationConfiguration(
    int MinLength,
    int MaxLength) : ValidationRuleConfiguration;

/// <summary>
/// Pattern validation configuration.
/// </summary>
/// <param name="RegexPattern">Regular expression pattern.</param>
/// <param name="CaseSensitive">Whether pattern is case sensitive.</param>
public record PatternValidationConfiguration(
    string RegexPattern,
    bool CaseSensitive) : ValidationRuleConfiguration;

/// <summary>
/// Workflow action triggered by a form lifecycle event.
/// </summary>
/// <param name="ActionId">Unique action identifier.</param>
/// <param name="Trigger">Lifecycle event that fires this action.</param>
/// <param name="ConditionExpression">Optional guard expression.</param>
/// <param name="ActionType">Type of action to perform.</param>
/// <param name="Parameters">Action-specific key/value parameters.</param>
/// <param name="ExecutionOrder">Order when multiple actions share a trigger.</param>
public record WorkflowAction(
    string ActionId,
    WorkflowTrigger Trigger,
    string ConditionExpression,
    WorkflowActionType ActionType,
    ImmutableDictionary<string, string> Parameters,
    int ExecutionOrder);

/// <summary>
/// Workflow trigger events.
/// </summary>
public enum WorkflowTrigger
{
    OnLoad,
    OnSave,
    OnSubmit,
    OnApprove,
    OnReject,
    OnFieldChange
}

/// <summary>
/// Workflow action types.
/// </summary>
public enum WorkflowActionType
{
    Webhook,
    SetField,
    SetStatus,
    SendNotification,
    CreateFeature,
    Validate
}

/// <summary>
/// Role- and location-based access control for a form.
/// </summary>
/// <param name="AllowedRoles">Roles allowed to open this form. Empty means unrestricted.</param>
/// <param name="ApproverRoles">Roles allowed to approve/reject submissions.</param>
/// <param name="LocationBoundary">Optional geographic boundary for form availability.</param>
/// <param name="FieldAccessRules">Per-field role overrides.</param>
public record FormAccessControl(
    ImmutableList<string> AllowedRoles,
    ImmutableList<string> ApproverRoles,
    Extent? LocationBoundary,
    ImmutableList<FieldAccessRule> FieldAccessRules);

/// <summary>
/// Per-field access restriction by role.
/// </summary>
/// <param name="FieldId">Field identifier.</param>
/// <param name="VisibleToRoles">Roles that can see this field. Empty = all allowed roles.</param>
/// <param name="EditableByRoles">Roles that can edit this field. Empty = all allowed roles.</param>
/// <param name="ReadOnly">Hard read-only regardless of role.</param>
public record FieldAccessRule(
    string FieldId,
    ImmutableList<string> VisibleToRoles,
    ImmutableList<string> EditableByRoles,
    bool ReadOnly);

/// <summary>
/// Geographic bounding box.
/// </summary>
/// <param name="Xmin">Minimum X (longitude).</param>
/// <param name="Ymin">Minimum Y (latitude).</param>
/// <param name="Xmax">Maximum X (longitude).</param>
/// <param name="Ymax">Maximum Y (latitude).</param>
public record Extent(
    double Xmin,
    double Ymin,
    double Xmax,
    double Ymax);