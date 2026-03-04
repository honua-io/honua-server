// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using System.Xml;
using System.Xml.Linq;
using FieldDataCollection.Models;

namespace FieldDataCollection.Services;

/// <summary>
/// Production implementation of XForms parser with mobile optimizations.
/// Parses OpenRosa-compatible XForms XML for mobile form rendering.
/// </summary>
internal sealed class XFormsParserService : IXFormsParserService
{
    private readonly ILogger<XFormsParserService> _logger;

    public XFormsParserService(ILogger<XFormsParserService> logger)
    {
        _logger = logger;
    }

    public async Task<XForm> ParseXFormsAsync(string xformsXml, CancellationToken cancellationToken = default)
    {
        using var activity = System.Diagnostics.Activity.Current?.Source.StartActivity("parse_xforms");

        try
        {
            var doc = XDocument.Parse(xformsXml);
            var xform = new XForm();

            // Parse head section (model, instance, bindings)
            var head = doc.Root?.Element(XName.Get("head", "http://www.w3.org/1999/xhtml"));
            if (head != null)
            {
                await ParseHeadSectionAsync(head, xform);
            }

            // Parse body section (controls and layout)
            var body = doc.Root?.Element(XName.Get("body", "http://www.w3.org/1999/xhtml"));
            if (body != null)
            {
                ParseBodySection(body, xform);
            }

            // Apply mobile optimizations
            xform = await ApplyMobileOptimizationsAsync(xform, xform.Metadata.MobileSettings);

            _logger.LogDebug("Parsed XForm {FormId} with {ControlCount} controls",
                xform.FormId, xform.Controls.Count);

            return xform;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse XForms XML");
            throw new InvalidOperationException("Invalid XForms XML format", ex);
        }
    }

    public async Task<XForm> ParseXFormsAsync(Stream xformsStream, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(xformsStream);
        var xformsXml = await reader.ReadToEndAsync(cancellationToken);
        return await ParseXFormsAsync(xformsXml, cancellationToken);
    }

    public async Task<List<FormValidationResult>> ValidateFormAsync(XForm xform)
    {
        var results = new List<FormValidationResult>();

        // Validate basic form structure
        if (string.IsNullOrEmpty(xform.FormId))
        {
            results.Add(new FormValidationResult
            {
                IsValid = false,
                ErrorMessage = "Form must have an ID",
                Severity = ValidationSeverity.Error
            });
        }

        if (string.IsNullOrEmpty(xform.FormTitle))
        {
            results.Add(new FormValidationResult
            {
                IsValid = false,
                ErrorMessage = "Form must have a title",
                Severity = ValidationSeverity.Warning
            });
        }

        // Validate controls have proper bindings
        foreach (var control in GetAllControls(xform.Controls))
        {
            if (!control.IsGroup && !string.IsNullOrEmpty(control.Ref))
            {
                var binding = xform.Bindings.FirstOrDefault(b => b.NodeSet.EndsWith(control.Ref.TrimStart('/')));
                if (binding == null)
                {
                    results.Add(new FormValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Control {control.Ref} has no data binding",
                        FieldPath = control.Ref,
                        Severity = ValidationSeverity.Error
                    });
                }
            }
        }

        // Mobile optimization checks
        var controlCount = GetAllControls(xform.Controls).Count(c => !c.IsGroup);
        if (controlCount > 15)
        {
            results.Add(new FormValidationResult
            {
                IsValid = true,
                ErrorMessage = "Form has many controls - consider using groups for better mobile experience",
                Severity = ValidationSeverity.Info
            });
        }

        return results;
    }

    public XFormInstance CreateBlankInstance(XForm xform)
    {
        var instance = new XFormInstance
        {
            Id = xform.Instance.Id,
            Version = xform.Instance.Version
        };

        // Initialize with default values from bindings
        foreach (var binding in xform.Bindings)
        {
            var fieldPath = ExtractFieldName(binding.NodeSet);
            instance.Data[fieldPath] = GetDefaultValue(binding.Type);
        }

        // Add system fields
        instance.Data["start"] = DateTime.Now;
        instance.Data["deviceid"] = GetDeviceId();

        return instance;
    }

    public async Task<List<FormValidationResult>> ValidateInstanceAsync(XForm xform, XFormInstance instance)
    {
        var results = new List<FormValidationResult>();

        foreach (var binding in xform.Bindings)
        {
            var fieldPath = ExtractFieldName(binding.NodeSet);
            var value = instance.Data.GetValueOrDefault(fieldPath);

            // Check required fields
            if (binding.Required && (value == null || string.IsNullOrWhiteSpace(value.ToString())))
            {
                results.Add(new FormValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "This field is required",
                    FieldPath = fieldPath,
                    Severity = ValidationSeverity.Error
                });
                continue;
            }

            // Validate data type
            var typeValidation = ValidateDataType(value, binding.Type);
            if (!typeValidation.IsValid)
            {
                results.Add(new FormValidationResult
                {
                    IsValid = false,
                    ErrorMessage = typeValidation.ErrorMessage,
                    FieldPath = fieldPath,
                    Severity = ValidationSeverity.Error
                });
            }

            // Apply constraints (simplified XPath evaluation)
            if (!string.IsNullOrEmpty(binding.Constraint))
            {
                var constraintValid = await EvaluateSimpleConstraintAsync(binding.Constraint, value);
                if (!constraintValid)
                {
                    results.Add(new FormValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = binding.ConstraintMsg ?? "Value does not meet constraint",
                        FieldPath = fieldPath,
                        Severity = ValidationSeverity.Error
                    });
                }
            }
        }

        return results;
    }

    public async Task<FormSubmission> PrepareSubmissionAsync(
        XForm xform,
        XFormInstance instance,
        List<FormAttachment> attachments)
    {
        var submission = new FormSubmission
        {
            FormId = xform.FormId,
            InstanceId = instance.InstanceId,
            Version = xform.Version,
            Data = new Dictionary<string, object?>(instance.Data),
            Attachments = attachments,
            DeviceId = GetDeviceId()
        };

        // Add completion timestamp
        submission.Data["end"] = DateTime.Now;

        // Calculate duration
        if (submission.Data.TryGetValue("start", out var startObj) && startObj is DateTime start)
        {
            var duration = DateTime.Now - start;
            submission.Data["duration"] = duration.TotalSeconds;
        }

        return submission;
    }

    public FormProgress CalculateProgress(XForm xform, XFormInstance instance)
    {
        var allControls = GetAllControls(xform.Controls).Where(c => !c.IsGroup).ToList();
        var completedCount = 0;

        foreach (var control in allControls)
        {
            var fieldPath = ExtractFieldName(control.Ref);
            var binding = xform.Bindings.FirstOrDefault(b => ExtractFieldName(b.NodeSet) == fieldPath);

            if (instance.Data.TryGetValue(fieldPath, out var value) && !IsEmptyValue(value))
            {
                completedCount++;
            }
            else if (binding?.Required == false)
            {
                completedCount++; // Optional fields count as complete if empty
            }
        }

        return new FormProgress
        {
            CompletedFields = completedCount,
            TotalFields = allControls.Count,
            ElapsedTime = instance.Data.TryGetValue("start", out var startObj) && startObj is DateTime start
                ? DateTime.Now - start
                : TimeSpan.Zero
        };
    }

    public async Task<XForm> ApplyMobileOptimizationsAsync(XForm xform, MobileFormSettings mobileSettings)
    {
        if (mobileSettings.UseLowPowerMode)
        {
            // Reduce visual complexity
            foreach (var control in GetAllControls(xform.Controls))
            {
                if (control.Appearance?.Contains("minimal") != true)
                {
                    control.Appearance = "minimal";
                }
            }
        }

        if (mobileSettings.AutoCapture)
        {
            // Auto-capture location for geopoint controls
            var locationControls = GetAllControls(xform.Controls)
                .Where(c => c.Type == "input" && GetControlDataType(c, xform) == "geopoint");

            foreach (var control in locationControls)
            {
                control.Appearance = "maps";
            }
        }

        return xform;
    }

    public MobileControlSuggestion GetMobileControlSuggestion(XFormControl control, XFormBind binding)
    {
        var suggestion = new MobileControlSuggestion
        {
            IsRequired = binding.Required,
            Properties = new Dictionary<string, object>()
        };

        switch (control.Type.ToLowerInvariant())
        {
            case "input":
                suggestion = GetInputControlSuggestion(control, binding);
                break;

            case "select1":
                suggestion.ControlType = MobileControlType.Picker;
                suggestion.Appearance = control.Appearance;
                break;

            case "select":
                suggestion.ControlType = MobileControlType.CheckBoxGroup;
                break;

            case "upload":
                if (control.MediaType?.StartsWith("image/") == true)
                {
                    suggestion.ControlType = MobileControlType.ImageButton;
                    suggestion.Properties["quality"] = PhotoQuality.Medium;
                }
                else
                {
                    suggestion.ControlType = MobileControlType.FileButton;
                }
                break;

            case "group":
                suggestion.ControlType = MobileControlType.GroupHeader;
                break;

            default:
                suggestion.ControlType = MobileControlType.Entry;
                break;
        }

        return suggestion;
    }

    #region Private Helper Methods

    private async Task ParseHeadSectionAsync(XElement head, XForm xform)
    {
        // Parse title
        var title = head.Element(XName.Get("title", "http://www.w3.org/1999/xhtml"));
        if (title != null)
        {
            xform.FormTitle = title.Value;
        }

        // Parse model
        var model = head.Element("model");
        if (model != null)
        {
            ParseModel(model, xform);
        }
    }

    private void ParseModel(XElement model, XForm xform)
    {
        // Parse instance
        var instance = model.Element("instance");
        if (instance != null)
        {
            var dataElement = instance.Elements().FirstOrDefault();
            if (dataElement != null)
            {
                xform.Instance.Id = dataElement.Attribute("id")?.Value ?? "";
                xform.Instance.Version = dataElement.Attribute("version")?.Value ?? "";
                xform.FormId = xform.Instance.Id;
            }
        }

        // Parse bindings
        foreach (var bind in model.Elements("bind"))
        {
            var binding = new XFormBind
            {
                NodeSet = bind.Attribute("nodeset")?.Value ?? "",
                Type = bind.Attribute("type")?.Value ?? "string",
                Required = bind.Attribute("required")?.Value == "true()",
                ReadOnly = bind.Attribute("readonly")?.Value == "true()",
                Constraint = bind.Attribute("constraint")?.Value,
                ConstraintMsg = bind.Attribute("jr:constraintMsg")?.Value,
                Calculate = bind.Attribute("calculate")?.Value,
                Relevant = bind.Attribute("relevant")?.Value
            };

            xform.Bindings.Add(binding);
        }
    }

    private void ParseBodySection(XElement body, XForm xform)
    {
        foreach (var element in body.Elements())
        {
            var control = ParseControl(element);
            if (control != null)
            {
                xform.Controls.Add(control);
            }
        }
    }

    private XFormControl? ParseControl(XElement element)
    {
        var control = new XFormControl
        {
            Type = element.Name.LocalName,
            Ref = element.Attribute("ref")?.Value ?? "",
            Appearance = element.Attribute("appearance")?.Value
        };

        // Parse label
        var label = element.Element("label");
        if (label != null)
        {
            control.Label = label.Value;
        }

        // Parse hint
        var hint = element.Element("hint");
        if (hint != null)
        {
            control.Hint = hint.Value;
        }

        // Parse choices for select controls
        if (control.Type == "select1" || control.Type == "select")
        {
            foreach (var item in element.Elements("item"))
            {
                var choice = new XFormChoice();

                var itemLabel = item.Element("label");
                if (itemLabel != null)
                {
                    choice.Label = itemLabel.Value;
                }

                var value = item.Element("value");
                if (value != null)
                {
                    choice.Value = value.Value;
                }

                control.Choices.Add(choice);
            }
        }

        // Parse child controls for groups
        if (control.Type == "group")
        {
            control.IsGroup = true;
            foreach (var child in element.Elements().Where(e => e.Name.LocalName != "label"))
            {
                var childControl = ParseControl(child);
                if (childControl != null)
                {
                    control.Children.Add(childControl);
                }
            }
        }

        // Parse media type for upload controls
        if (control.Type == "upload")
        {
            control.MediaType = element.Attribute("mediatype")?.Value;
        }

        return control;
    }

    private MobileControlSuggestion GetInputControlSuggestion(XFormControl control, XFormBind binding)
    {
        var suggestion = new MobileControlSuggestion { IsRequired = binding.Required };

        switch (binding.Type.ToLowerInvariant())
        {
            case "int":
            case "decimal":
                suggestion.ControlType = MobileControlType.NumericEntry;
                suggestion.Properties["inputType"] = binding.Type == "int" ? "integer" : "decimal";
                break;

            case "date":
                suggestion.ControlType = MobileControlType.DatePicker;
                break;

            case "time":
                suggestion.ControlType = MobileControlType.TimePicker;
                break;

            case "geopoint":
                suggestion.ControlType = MobileControlType.LocationButton;
                suggestion.Appearance = control.Appearance ?? "maps";
                suggestion.Properties["requiresGps"] = true;
                break;

            case "binary":
                suggestion.ControlType = MobileControlType.ImageButton;
                suggestion.Properties["mediaType"] = "image/*";
                break;

            default: // string
                if (control.Appearance?.Contains("multiline") == true)
                {
                    suggestion.ControlType = MobileControlType.Editor;
                }
                else
                {
                    suggestion.ControlType = MobileControlType.Entry;
                    suggestion.Placeholder = control.Hint;
                }
                break;
        }

        return suggestion;
    }

    private static IEnumerable<XFormControl> GetAllControls(List<XFormControl> controls)
    {
        foreach (var control in controls)
        {
            yield return control;

            foreach (var child in GetAllControls(control.Children))
            {
                yield return child;
            }
        }
    }

    private static string ExtractFieldName(string nodeset)
    {
        return nodeset.Split('/').LastOrDefault()?.Trim() ?? "";
    }

    private static object? GetDefaultValue(string dataType)
    {
        return dataType.ToLowerInvariant() switch
        {
            "int" => 0,
            "decimal" => 0.0,
            "date" => DateTime.Today,
            "dateTime" => DateTime.Now,
            "boolean" => false,
            _ => null
        };
    }

    private static string GetDeviceId()
    {
        // In production, this would get actual device ID
        return Environment.MachineName + "_" + Guid.NewGuid().ToString("N")[..8];
    }

    private static FormValidationResult ValidateDataType(object? value, string expectedType)
    {
        if (value == null)
        {
            return new FormValidationResult { IsValid = true };
        }

        var result = new FormValidationResult { IsValid = true };

        try
        {
            switch (expectedType.ToLowerInvariant())
            {
                case "int":
                    if (value is not int && !int.TryParse(value.ToString(), out _))
                    {
                        result.IsValid = false;
                        result.ErrorMessage = "Please enter a valid number";
                    }
                    break;

                case "decimal":
                    if (value is not double && !double.TryParse(value.ToString(), out _))
                    {
                        result.IsValid = false;
                        result.ErrorMessage = "Please enter a valid decimal number";
                    }
                    break;

                case "date":
                    if (value is not DateTime && !DateTime.TryParse(value.ToString(), out _))
                    {
                        result.IsValid = false;
                        result.ErrorMessage = "Please enter a valid date";
                    }
                    break;

                case "geopoint":
                    if (Location.FromGeoPointString(value.ToString() ?? "") == null)
                    {
                        result.IsValid = false;
                        result.ErrorMessage = "Invalid GPS location";
                    }
                    break;
            }
        }
        catch
        {
            result.IsValid = false;
            result.ErrorMessage = $"Invalid {expectedType} value";
        }

        return result;
    }

    private static async Task<bool> EvaluateSimpleConstraintAsync(string constraint, object? value)
    {
        // Simplified constraint evaluation - would be enhanced with proper XPath
        if (constraint.Contains(">") && double.TryParse(value?.ToString(), out var numValue))
        {
            var parts = constraint.Split('>');
            if (parts.Length == 2 && double.TryParse(parts[1].Trim(), out var threshold))
            {
                return numValue > threshold;
            }
        }

        return true; // Default to valid for unsupported constraints
    }

    private static bool IsEmptyValue(object? value)
    {
        return value == null ||
               (value is string s && string.IsNullOrWhiteSpace(s)) ||
               (value is DateTime dt && dt == DateTime.MinValue);
    }

    private string GetControlDataType(XFormControl control, XForm xform)
    {
        var binding = xform.Bindings.FirstOrDefault(b => b.NodeSet.EndsWith(control.Ref.TrimStart('/')));
        return binding?.Type ?? "string";
    }

    #endregion
}