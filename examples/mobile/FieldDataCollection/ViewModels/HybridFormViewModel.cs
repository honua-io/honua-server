// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using System.Collections.ObjectModel;
using System.ComponentModel;
using FieldDataCollection.Models;
using FieldDataCollection.Services;
using Honua.Mobile.Core.Client;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FieldDataCollection.ViewModels;

/// <summary>
/// Enhanced ViewModel supporting both OpenRosa XForms and gRPC-native forms.
/// Provides unified interface with real-time collaboration and mobile optimizations.
/// </summary>
public partial class HybridFormViewModel : BaseViewModel
{
    private readonly IXFormsParserService _xformsParser;
    private readonly IGrpcFormService _grpcFormService;
    private readonly HonuaFeatureClient _featureClient;
    private readonly ILocationService _locationService;
    private readonly IDialogService _dialogService;

    [ObservableProperty]
    private FormType _currentFormType = FormType.OpenRosa;

    // OpenRosa form properties
    [ObservableProperty]
    private XForm? _currentXForm;

    [ObservableProperty]
    private XFormInstance? _currentXFormInstance;

    // gRPC-native form properties
    [ObservableProperty]
    private Geospatial.V1.FormDefinition? _currentGrpcForm;

    [ObservableProperty]
    private Geospatial.V1.FormInstance? _currentGrpcInstance;

    // Unified properties
    [ObservableProperty]
    private FormProgress _progress = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _canSubmit;

    [ObservableProperty]
    private string _validationSummary = string.Empty;

    [ObservableProperty]
    private bool _isCollaborationEnabled;

    [ObservableProperty]
    private string _collaborationSessionId = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _activeCollaborators = new();

    public ObservableCollection<FormValidationResult> ValidationResults { get; } = new();
    public ObservableCollection<FormAttachment> Attachments { get; } = new();
    public ObservableCollection<MobileFormControl> MobileControls { get; } = new();

    /// <summary>
    /// Dynamic form field values keyed by field path (unified for both form types).
    /// </summary>
    public Dictionary<string, object?> FormData { get; private set; } = new();

    public HybridFormViewModel(
        IXFormsParserService xformsParser,
        IGrpcFormService grpcFormService,
        HonuaFeatureClient featureClient,
        ILocationService locationService,
        IDialogService dialogService)
    {
        _xformsParser = xformsParser;
        _grpcFormService = grpcFormService;
        _featureClient = featureClient;
        _locationService = locationService;
        _dialogService = dialogService;

        // Subscribe to form data changes for real-time validation
        PropertyChanged += OnFormDataChanged;
    }

    [RelayCommand]
    public async Task LoadOpenRosaFormAsync(string formId)
    {
        CurrentFormType = FormType.OpenRosa;
        IsLoading = true;

        try
        {
            var xformsXml = await LoadFormXmlAsync(formId);
            CurrentXForm = await _xformsParser.ParseXFormsAsync(xformsXml);
            CurrentXFormInstance = _xformsParser.CreateBlankInstance(CurrentXForm);

            await ConvertToMobileControlsAsync();
            InitializeFormData();
            await ValidateFormAsync();
            UpdateProgress();

            await _dialogService.ShowToastAsync($"Loaded OpenRosa form: {CurrentXForm.FormTitle}");
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Failed to load OpenRosa form", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task LoadGrpcFormAsync(string formId, string serviceId, int layerId)
    {
        CurrentFormType = FormType.GrpcNative;
        IsLoading = true;

        try
        {
            var response = await _grpcFormService.GetFormDefinitionAsync(
                formId, serviceId, layerId);

            CurrentGrpcForm = response.Form;
            CurrentGrpcInstance = CreateBlankGrpcInstance(CurrentGrpcForm);

            await ConvertGrpcToMobileControlsAsync();
            InitializeFormData();
            await ValidateGrpcFormAsync();
            UpdateProgress();

            await _dialogService.ShowToastAsync($"Loaded gRPC form: {CurrentGrpcForm.Title}");
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Failed to load gRPC form", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task StartCollaborationAsync(string sessionId)
    {
        if (CurrentFormType != FormType.GrpcNative || CurrentGrpcForm == null)
        {
            await _dialogService.ShowErrorAsync("Collaboration Error",
                "Real-time collaboration is only available for gRPC-native forms.");
            return;
        }

        try
        {
            CollaborationSessionId = sessionId;
            IsCollaborationEnabled = true;

            // Start real-time collaboration stream
            _ = Task.Run(async () =>
            {
                await foreach (var update in _grpcFormService.StreamFormUpdatesAsync(
                    sessionId, CurrentGrpcForm.FormId, CurrentGrpcInstance?.InstanceId ?? ""))
                {
                    await HandleCollaborationUpdateAsync(update);
                }
            });

            await _dialogService.ShowToastAsync("Real-time collaboration started");
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Collaboration Failed", ex.Message);
            IsCollaborationEnabled = false;
        }
    }

    [RelayCommand]
    public async Task CaptureLocationAsync(string fieldPath)
    {
        if (!_locationService.IsLocationAvailable)
        {
            await _dialogService.ShowErrorAsync("Location Services", "GPS is not available on this device");
            return;
        }

        try
        {
            var location = await _locationService.GetCurrentLocationAsync();
            if (location != null)
            {
                string locationValue;

                if (CurrentFormType == FormType.OpenRosa)
                {
                    // OpenRosa geopoint format: "latitude longitude altitude accuracy"
                    locationValue = $"{location.Latitude} {location.Longitude} {location.Altitude} {location.Accuracy}";
                }
                else
                {
                    // gRPC format: structured location data
                    locationValue = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        latitude = location.Latitude,
                        longitude = location.Longitude,
                        altitude = location.Altitude,
                        accuracy = location.Accuracy,
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
                }

                await SetFieldValueAsync(fieldPath, locationValue);
                await _dialogService.ShowToastAsync($"Location captured with {location.Accuracy:F1}m accuracy");
            }
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Location Error", ex.Message);
        }
    }

    [RelayCommand]
    public async Task CapturePhotoAsync(string fieldPath)
    {
        try
        {
            var photo = await MediaPicker.CapturePhotoAsync(new MediaPickerOptions
            {
                Title = "Capture Photo"
            });

            if (photo != null)
            {
                FormAttachment attachment;

                if (CurrentFormType == FormType.OpenRosa)
                {
                    // Create OpenRosa-style attachment
                    var photoPath = await SavePhotoAsync(photo);
                    attachment = new FormAttachment
                    {
                        FieldName = fieldPath,
                        FileName = Path.GetFileName(photoPath),
                        ContentType = "image/jpeg",
                        FilePath = photoPath,
                        FileSize = new FileInfo(photoPath).Length,
                        CapturedAt = DateTime.Now
                    };

                    await SetFieldValueAsync(fieldPath, attachment.FileName);
                }
                else
                {
                    // Create gRPC-style attachment with metadata
                    attachment = await GrpcFormExtensions.CreateFormAttachmentAsync(
                        fieldPath, photo, new Geospatial.V1.AttachmentMetadata
                        {
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            DeviceId = DeviceInfo.Name
                        });

                    await SetFieldValueAsync(fieldPath, attachment.FileName);
                }

                Attachments.Add(attachment);
                await _dialogService.ShowToastAsync("Photo captured successfully");
            }
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Camera Error", ex.Message);
        }
    }

    [RelayCommand]
    public async Task ValidateFormAsync()
    {
        ValidationResults.Clear();

        try
        {
            List<FormValidationResult> results;

            if (CurrentFormType == FormType.OpenRosa && CurrentXForm != null && CurrentXFormInstance != null)
            {
                results = await _xformsParser.ValidateInstanceAsync(CurrentXForm, CurrentXFormInstance);
            }
            else if (CurrentFormType == FormType.GrpcNative && CurrentGrpcForm != null && CurrentGrpcInstance != null)
            {
                var response = await _grpcFormService.ValidateFormDataAsync(CurrentGrpcForm.FormId, CurrentGrpcInstance);
                results = ConvertGrpcValidationResults(response.Issues);
            }
            else
            {
                results = new List<FormValidationResult>();
            }

            foreach (var result in results)
            {
                ValidationResults.Add(result);
            }

            UpdateValidationSummary();
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Validation Failed", ex.Message);
        }
    }

    [RelayCommand]
    public async Task SubmitFormAsync()
    {
        if (!CanSubmit)
            return;

        var confirmed = await _dialogService.ShowConfirmAsync(
            "Submit Form",
            "Are you sure you want to submit this form? This action cannot be undone.");

        if (!confirmed)
            return;

        IsLoading = true;
        try
        {
            if (CurrentFormType == FormType.OpenRosa)
            {
                await SubmitOpenRosaFormAsync();
            }
            else if (CurrentFormType == FormType.GrpcNative)
            {
                await SubmitGrpcFormAsync();
            }

            await _dialogService.ShowSuccessAsync("Form submitted successfully!");
            await Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Submission Failed", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task SetFieldValueAsync(string fieldPath, object? value)
    {
        FormData[fieldPath] = value;

        // Update appropriate form instance
        if (CurrentFormType == FormType.OpenRosa && CurrentXFormInstance != null)
        {
            CurrentXFormInstance.Data[fieldPath] = value;
            CurrentXFormInstance.ModifiedAt = DateTime.Now;
        }
        else if (CurrentFormType == FormType.GrpcNative && CurrentGrpcInstance != null)
        {
            if (value != null)
            {
                CurrentGrpcInstance.FieldValues[fieldPath] = CreateAttributeValue(value);
            }
            CurrentGrpcInstance.ModifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        // Send collaboration update for gRPC forms
        if (CurrentFormType == FormType.GrpcNative && IsCollaborationEnabled)
        {
            await SendCollaborationUpdateAsync(fieldPath, value);
        }

        UpdateProgress();
        _ = Task.Run(ValidateFormAsync); // Async validation
    }

    public T? GetFieldValue<T>(string fieldPath)
    {
        if (FormData.TryGetValue(fieldPath, out var value))
        {
            try
            {
                return (T?)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return default;
            }
        }
        return default;
    }

    private async Task ConvertToMobileControlsAsync()
    {
        MobileControls.Clear();

        if (CurrentXForm == null)
            return;

        foreach (var control in CurrentXForm.Controls)
        {
            var binding = CurrentXForm.Bindings.FirstOrDefault(b =>
                b.NodeSet.EndsWith(control.Ref.TrimStart('/')));

            if (binding != null)
            {
                var suggestion = _xformsParser.GetMobileControlSuggestion(control, binding);
                var mobileControl = new MobileFormControl
                {
                    ControlId = control.Ref,
                    Label = control.Label,
                    Hint = control.Hint,
                    Required = binding.Required,
                    Type = suggestion.ControlType,
                    Properties = new Dictionary<string, object>
                    {
                        ["appearance"] = control.Appearance ?? "",
                        ["suggestion"] = suggestion.ReasoningText ?? ""
                    }
                };

                MobileControls.Add(mobileControl);
            }
        }
    }

    private async Task ConvertGrpcToMobileControlsAsync()
    {
        MobileControls.Clear();

        if (CurrentGrpcForm == null)
            return;

        var mobileControls = CurrentGrpcForm.ToMobileControls();
        foreach (var control in mobileControls)
        {
            MobileControls.Add(control);
        }
    }

    private async Task HandleCollaborationUpdateAsync(Geospatial.V1.FormUpdateResponse update)
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            switch (update.Update.UpdateType)
            {
                case Geospatial.V1.UpdateType.FieldChanged:
                    if (update.Update.FieldId != null && update.Update.NewValue != null)
                    {
                        var value = ConvertAttributeValueToObject(update.Update.NewValue);
                        FormData[update.Update.FieldId] = value;
                        OnPropertyChanged(nameof(FormData));
                    }
                    break;

                case Geospatial.V1.UpdateType.UserJoined:
                    if (!ActiveCollaborators.Contains(update.Update.UserId))
                    {
                        ActiveCollaborators.Add(update.Update.UserId);
                    }
                    break;

                case Geospatial.V1.UpdateType.UserLeft:
                    ActiveCollaborators.Remove(update.Update.UserId);
                    break;
            }
        });
    }

    private async Task SendCollaborationUpdateAsync(string fieldPath, object? value)
    {
        if (string.IsNullOrEmpty(CollaborationSessionId) || CurrentGrpcInstance == null)
            return;

        try
        {
            var update = new Geospatial.V1.FormUpdate
            {
                UpdateType = Geospatial.V1.UpdateType.FieldChanged,
                FieldId = fieldPath,
                NewValue = value != null ? CreateAttributeValue(value) : null,
                UserId = Environment.UserName,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            await _grpcFormService.SendFormUpdateAsync(CollaborationSessionId, update);
        }
        catch (Exception ex)
        {
            // Log but don't interrupt user experience
            System.Diagnostics.Debug.WriteLine($"Failed to send collaboration update: {ex.Message}");
        }
    }

    private async Task SubmitOpenRosaFormAsync()
    {
        if (CurrentXForm == null || CurrentXFormInstance == null)
            return;

        UpdateInstanceFromFormData();

        var submission = await _xformsParser.PrepareSubmissionAsync(
            CurrentXForm, CurrentXFormInstance, Attachments.ToList());

        await SubmitViaGrpcAsync(submission);
    }

    private async Task SubmitGrpcFormAsync()
    {
        if (CurrentGrpcForm == null || CurrentGrpcInstance == null)
            return;

        var attachments = Attachments.Select(a => new Geospatial.V1.FormAttachment
        {
            AttachmentId = Guid.NewGuid().ToString(),
            FieldId = a.FieldName,
            Filename = a.FileName,
            ContentType = a.ContentType,
            FileSize = a.FileSize,
            Metadata = new Geospatial.V1.AttachmentMetadata
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DeviceId = DeviceInfo.Name
            }
        }).ToList();

        var response = await _grpcFormService.SubmitFormDataAsync(
            CurrentGrpcForm.FormId, CurrentGrpcInstance, attachments);

        if (!response.Result.Success)
        {
            throw new InvalidOperationException($"Submission failed: {response.Result.Message}");
        }
    }

    // Helper methods for data conversion between OpenRosa and gRPC formats
    private Geospatial.V1.AttributeValue CreateAttributeValue(object value) =>
        GrpcFormExtensions.CreateAttributeValue(value);

    private object? ConvertAttributeValueToObject(Geospatial.V1.AttributeValue attributeValue)
    {
        return attributeValue.ValueCase switch
        {
            Geospatial.V1.AttributeValue.ValueOneofCase.StringValue => attributeValue.StringValue,
            Geospatial.V1.AttributeValue.ValueOneofCase.Int32Value => attributeValue.Int32Value,
            Geospatial.V1.AttributeValue.ValueOneofCase.Int64Value => attributeValue.Int64Value,
            Geospatial.V1.AttributeValue.ValueOneofCase.DoubleValue => attributeValue.DoubleValue,
            Geospatial.V1.AttributeValue.ValueOneofCase.BoolValue => attributeValue.BoolValue,
            Geospatial.V1.AttributeValue.ValueOneofCase.DatetimeValue =>
                DateTimeOffset.FromUnixTimeMilliseconds(attributeValue.DatetimeValue).DateTime,
            _ => null
        };
    }

    private Geospatial.V1.FormInstance CreateBlankGrpcInstance(Geospatial.V1.FormDefinition form)
    {
        return new Geospatial.V1.FormInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"),
            FormId = form.FormId,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ModifiedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CreatedBy = Environment.UserName,
            Status = Geospatial.V1.InstanceStatus.Draft
        };
    }

    private List<FormValidationResult> ConvertGrpcValidationResults(
        IEnumerable<Geospatial.V1.ValidationIssue> issues)
    {
        return issues.Select(issue => new FormValidationResult
        {
            FieldId = issue.FieldId,
            Message = issue.Message,
            Severity = issue.Severity switch
            {
                Geospatial.V1.ValidationSeverity.Error => ValidationSeverity.Error,
                Geospatial.V1.ValidationSeverity.Warning => ValidationSeverity.Warning,
                _ => ValidationSeverity.Info
            }
        }).ToList();
    }

    private void UpdateValidationSummary()
    {
        var errorCount = ValidationResults.Count(r => r.Severity == ValidationSeverity.Error);
        var warningCount = ValidationResults.Count(r => r.Severity == ValidationSeverity.Warning);

        if (errorCount > 0)
        {
            ValidationSummary = $"{errorCount} error(s), {warningCount} warning(s)";
            CanSubmit = false;
        }
        else
        {
            ValidationSummary = warningCount > 0 ? $"{warningCount} warning(s)" : "Form is valid";
            CanSubmit = true;
        }
    }

    private void UpdateProgress()
    {
        if (CurrentFormType == FormType.OpenRosa && CurrentXForm != null && CurrentXFormInstance != null)
        {
            Progress = _xformsParser.CalculateProgress(CurrentXForm, CurrentXFormInstance);
        }
        else if (CurrentFormType == FormType.GrpcNative && CurrentGrpcForm != null && CurrentGrpcInstance != null)
        {
            // Calculate progress for gRPC forms
            var totalFields = CurrentGrpcForm.Controls.Count;
            var completedFields = FormData.Count(kvp => kvp.Value != null);

            Progress = new FormProgress
            {
                TotalFields = totalFields,
                CompletedFields = completedFields,
                PercentComplete = totalFields > 0 ? (double)completedFields / totalFields * 100 : 0
            };
        }
    }

    // Legacy methods for OpenRosa compatibility
    private void InitializeFormData()
    {
        FormData.Clear();

        if (CurrentFormType == FormType.OpenRosa && CurrentXFormInstance != null)
        {
            foreach (var kvp in CurrentXFormInstance.Data)
            {
                FormData[kvp.Key] = kvp.Value;
            }
        }
        else if (CurrentFormType == FormType.GrpcNative && CurrentGrpcInstance != null)
        {
            foreach (var kvp in CurrentGrpcInstance.FieldValues)
            {
                FormData[kvp.Key] = ConvertAttributeValueToObject(kvp.Value);
            }
        }
    }

    private void UpdateInstanceFromFormData()
    {
        if (CurrentFormType == FormType.OpenRosa && CurrentXFormInstance != null)
        {
            foreach (var kvp in FormData)
            {
                CurrentXFormInstance.Data[kvp.Key] = kvp.Value;
            }
        }
        else if (CurrentFormType == FormType.GrpcNative && CurrentGrpcInstance != null)
        {
            CurrentGrpcInstance.FieldValues.Clear();
            foreach (var kvp in FormData.Where(kvp => kvp.Value != null))
            {
                CurrentGrpcInstance.FieldValues[kvp.Key] = CreateAttributeValue(kvp.Value!);
            }
        }
    }

    private void OnFormDataChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FormData))
        {
            UpdateProgress();
        }
    }

    // Legacy OpenRosa methods (simplified)
    private async Task<string> LoadFormXmlAsync(string formId) =>
        await File.ReadAllTextAsync($"forms/{formId}.xml"); // Mock implementation

    private async Task<string> SavePhotoAsync(FileResult photo)
    {
        var localAppData = FileSystem.AppDataDirectory;
        var photoDir = Path.Combine(localAppData, "photos");
        Directory.CreateDirectory(photoDir);

        var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{photo.FileName}";
        var localPath = Path.Combine(photoDir, fileName);

        using var stream = await photo.OpenReadAsync();
        using var fileStream = File.Create(localPath);
        await stream.CopyToAsync(fileStream);

        return localPath;
    }

    private async Task SubmitViaGrpcAsync(object submission)
    {
        // Convert OpenRosa submission to feature edit via existing gRPC client
        // Implementation would mirror the existing FormViewModel approach
        await _featureClient.ApplyEditsAsync("field_service", 0, new FeatureEditBatch());
    }
}

/// <summary>
/// Enumeration of supported form types.
/// </summary>
public enum FormType
{
    OpenRosa,
    GrpcNative
}