// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using System.Collections.ObjectModel;
using System.ComponentModel;
using FieldDataCollection.Models;
using FieldDataCollection.Services;
using Honua.Mobile.Core.Client;

namespace FieldDataCollection.ViewModels;

/// <summary>
/// ViewModel for rendering and managing OpenRosa XForms in MAUI.
/// Handles form loading, validation, data binding, and submission via gRPC.
/// </summary>
public partial class FormViewModel : BaseViewModel
{
    private readonly IXFormsParserService _xformsParser;
    private readonly HonuaFeatureClient _featureClient;
    private readonly ILocationService _locationService;
    private readonly IDialogService _dialogService;

    [ObservableProperty]
    private XForm? _currentForm;

    [ObservableProperty]
    private XFormInstance? _currentInstance;

    [ObservableProperty]
    private FormProgress _progress = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _canSubmit;

    [ObservableProperty]
    private string _validationSummary = string.Empty;

    public ObservableCollection<FormValidationResult> ValidationResults { get; } = new();
    public ObservableCollection<FormAttachment> Attachments { get; } = new();

    /// <summary>
    /// Dynamic form field values keyed by field path.
    /// </summary>
    public Dictionary<string, object?> FormData { get; private set; } = new();

    public FormViewModel(
        IXFormsParserService xformsParser,
        HonuaFeatureClient featureClient,
        ILocationService locationService,
        IDialogService dialogService)
    {
        _xformsParser = xformsParser;
        _featureClient = featureClient;
        _locationService = locationService;
        _dialogService = dialogService;

        // Subscribe to form data changes for real-time validation
        PropertyChanged += OnFormDataChanged;
    }

    [RelayCommand]
    public async Task LoadFormAsync(string formId)
    {
        IsLoading = true;
        try
        {
            // In production, this would download from gRPC v2 form service
            // For now, simulate form loading
            var xformsXml = await LoadFormXmlAsync(formId);

            CurrentForm = await _xformsParser.ParseXFormsAsync(xformsXml);
            CurrentInstance = _xformsParser.CreateBlankInstance(CurrentForm);

            InitializeFormData();
            await ValidateFormAsync();
            UpdateProgress();

            await _dialogService.ShowToastAsync($"Loaded form: {CurrentForm.FormTitle}");
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Failed to load form", ex.Message);
        }
        finally
        {
            IsLoading = false;
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
                var geoPoint = new Location
                {
                    Latitude = location.Latitude,
                    Longitude = location.Longitude,
                    Altitude = location.Altitude,
                    Accuracy = location.Accuracy,
                    Timestamp = DateTime.Now
                };

                SetFieldValue(fieldPath, geoPoint.ToGeoPointString());
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
                // Save photo and create attachment
                var photoPath = await SavePhotoAsync(photo);
                var attachment = new FormAttachment
                {
                    FieldName = fieldPath,
                    FileName = Path.GetFileName(photoPath),
                    ContentType = "image/jpeg",
                    FilePath = photoPath,
                    FileSize = new FileInfo(photoPath).Length,
                    CapturedAt = DateTime.Now
                };

                Attachments.Add(attachment);
                SetFieldValue(fieldPath, attachment.FileName);

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
        if (CurrentForm == null || CurrentInstance == null)
            return;

        ValidationResults.Clear();

        var results = await _xformsParser.ValidateInstanceAsync(CurrentForm, CurrentInstance);
        foreach (var result in results)
        {
            ValidationResults.Add(result);
        }

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

    [RelayCommand]
    public async Task SubmitFormAsync()
    {
        if (CurrentForm == null || CurrentInstance == null || !CanSubmit)
            return;

        var confirmed = await _dialogService.ShowConfirmAsync(
            "Submit Form",
            "Are you sure you want to submit this form? This action cannot be undone.");

        if (!confirmed)
            return;

        IsLoading = true;
        try
        {
            // Update instance with final data
            UpdateInstanceFromFormData();

            // Prepare submission for gRPC protocols
            var submission = await _xformsParser.PrepareSubmissionAsync(
                CurrentForm, CurrentInstance, Attachments.ToList());

            // Submit via Honua gRPC (instead of traditional OpenRosa endpoint)
            await SubmitViaGrpcAsync(submission);

            await _dialogService.ShowSuccessAsync("Form submitted successfully!");

            // Navigate back or clear form
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

    [RelayCommand]
    public async Task SaveDraftAsync()
    {
        if (CurrentForm == null || CurrentInstance == null)
            return;

        try
        {
            UpdateInstanceFromFormData();
            CurrentInstance.State = XFormSubmissionState.Draft;
            CurrentInstance.ModifiedAt = DateTime.Now;

            // Save to local storage for offline capability
            await SaveDraftLocallyAsync(CurrentInstance);

            await _dialogService.ShowToastAsync("Draft saved successfully");
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("Save Failed", ex.Message);
        }
    }

    public void SetFieldValue(string fieldPath, object? value)
    {
        FormData[fieldPath] = value;

        if (CurrentInstance != null)
        {
            CurrentInstance.Data[fieldPath] = value;
            CurrentInstance.ModifiedAt = DateTime.Now;
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

    private void InitializeFormData()
    {
        if (CurrentInstance == null)
            return;

        FormData.Clear();
        foreach (var kvp in CurrentInstance.Data)
        {
            FormData[kvp.Key] = kvp.Value;
        }
    }

    private void UpdateInstanceFromFormData()
    {
        if (CurrentInstance == null)
            return;

        foreach (var kvp in FormData)
        {
            CurrentInstance.Data[kvp.Key] = kvp.Value;
        }
    }

    private void UpdateProgress()
    {
        if (CurrentForm == null || CurrentInstance == null)
            return;

        Progress = _xformsParser.CalculateProgress(CurrentForm, CurrentInstance);
    }

    private void OnFormDataChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Real-time progress updates as user fills form
        if (e.PropertyName == nameof(FormData))
        {
            UpdateProgress();
        }
    }

    private async Task<string> LoadFormXmlAsync(string formId)
    {
        // Mock XForms XML - in production would download from gRPC form service
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <h:html xmlns:h="http://www.w3.org/1999/xhtml" xmlns:jr="http://openrosa.org/javarosa">
              <h:head>
                <h:title>{formId} Data Collection</h:title>
                <model>
                  <instance>
                    <data id="{formId}" version="1.0">
                      <start/>
                      <location/>
                      <name/>
                      <description/>
                      <status/>
                      <priority/>
                      <photo/>
                      <end/>
                      <deviceid/>
                    </data>
                  </instance>
                  <bind nodeset="/data/start" type="dateTime" />
                  <bind nodeset="/data/location" type="geopoint" required="true()" />
                  <bind nodeset="/data/name" type="string" required="true()" />
                  <bind nodeset="/data/description" type="string" />
                  <bind nodeset="/data/status" type="string" required="true()" />
                  <bind nodeset="/data/priority" type="int" constraint=". > 0" />
                  <bind nodeset="/data/photo" type="binary" />
                  <bind nodeset="/data/end" type="dateTime" />
                  <bind nodeset="/data/deviceid" type="string" />
                </model>
              </h:head>
              <h:body>
                <input ref="/data/location" appearance="maps">
                  <label>Current Location</label>
                  <hint>Tap to capture GPS location</hint>
                </input>
                <input ref="/data/name">
                  <label>Feature Name</label>
                  <hint>Enter a descriptive name</hint>
                </input>
                <input ref="/data/description" appearance="multiline">
                  <label>Description</label>
                  <hint>Enter detailed description</hint>
                </input>
                <select1 ref="/data/status">
                  <label>Status</label>
                  <item><label>Active</label><value>active</value></item>
                  <item><label>Inactive</label><value>inactive</value></item>
                  <item><label>Pending</label><value>pending</value></item>
                </select1>
                <input ref="/data/priority">
                  <label>Priority Level</label>
                  <hint>Enter priority (1-10)</hint>
                </input>
                <upload ref="/data/photo" mediatype="image/*">
                  <label>Take Photo</label>
                  <hint>Capture photo of the feature</hint>
                </upload>
              </h:body>
            </h:html>
            """;
    }

    private async Task<string> SavePhotoAsync(FileResult photo)
    {
        // Save photo to app data directory
        var localAppData = FileSystem.AppDataDirectory;
        var photoDir = Path.Combine(localAppData, "photos");
        Directory.CreateDirectory(photoDir);

        var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(photo.FileName)}";
        var localPath = Path.Combine(photoDir, fileName);

        using var stream = await photo.OpenReadAsync();
        using var fileStream = File.Create(localPath);
        await stream.CopyToAsync(fileStream);

        return localPath;
    }

    private async Task SubmitViaGrpcAsync(FormSubmission submission)
    {
        // Convert OpenRosa submission to Honua feature for gRPC v2 submission
        var feature = ConvertSubmissionToFeature(submission);

        // Submit using Honua's gRPC protocols (60% smaller, better mobile optimization)
        var editBatch = new FeatureEditBatch { Adds = new[] { feature } };

        // In production, would get actual service/layer from form metadata
        var result = await _featureClient.ApplyEditsAsync("field_service", 0, editBatch);

        if (!result.AddResults.All(r => r.Success))
        {
            var errors = result.AddResults.Where(r => !r.Success).Select(r => r.Error?.Message);
            throw new InvalidOperationException($"Submission failed: {string.Join(", ", errors)}");
        }
    }

    private Feature ConvertSubmissionToFeature(FormSubmission submission)
    {
        var attributes = new Dictionary<string, object?>();

        foreach (var data in submission.Data)
        {
            // Convert form data to feature attributes
            if (data.Key == "location" && data.Value is string geoPointStr)
            {
                // Location handled as geometry
                continue;
            }

            attributes[data.Key.ToUpperInvariant()] = data.Value;
        }

        var geometry = ExtractGeometryFromSubmission(submission);

        return new Feature
        {
            Attributes = attributes,
            Geometry = geometry
        };
    }

    private GeometryValue? ExtractGeometryFromSubmission(FormSubmission submission)
    {
        if (submission.Data.TryGetValue("location", out var locationValue) && locationValue is string geoPointStr)
        {
            var location = Location.FromGeoPointString(geoPointStr);
            if (location != null)
            {
                return new GeometryValue
                {
                    Type = GeometryType.Point,
                    Coordinates = new[] { location.Longitude, location.Latitude }
                };
            }
        }

        return null;
    }

    private async Task SaveDraftLocallyAsync(XFormInstance instance)
    {
        var draftsDir = Path.Combine(FileSystem.AppDataDirectory, "drafts");
        Directory.CreateDirectory(draftsDir);

        var fileName = $"{instance.Id}_{instance.InstanceId}.json";
        var filePath = Path.Combine(draftsDir, fileName);

        var json = System.Text.Json.JsonSerializer.Serialize(instance);
        await File.WriteAllTextAsync(filePath, json);
    }
}