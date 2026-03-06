// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Honua.Mobile.Sdk.Clients;
using HonuaFieldApp.Services;

namespace HonuaFieldApp.ViewModels;

/// <summary>
/// ViewModel for data collection page demonstrating field data capture,
/// form generation, photo capture, and offline sync capabilities.
/// </summary>
public partial class DataCollectionViewModel : ObservableObject
{
    private readonly HonuaMobileClient _honuaClient;
    private readonly IFormDataService _formDataService;
    private readonly ICameraService _cameraService;
    private readonly IGpsLocationService _gpsService;

    // Observable properties for UI binding
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string loadingMessage = "Loading...";
    [ObservableProperty] private FormDefinition? currentForm;
    [ObservableProperty] private bool hasLocation;
    [ObservableProperty] private Location? currentLocation;
    [ObservableProperty] private FileResult? capturedPhoto;
    [ObservableProperty] private bool canSave;
    [ObservableProperty] private string photoPath = string.Empty;

    // Form data
    public ObservableCollection<FormFieldData> FormFields { get; } = new();
    public Dictionary<string, object> FormData { get; } = new();

    // Services configuration
    private readonly string _defaultServiceId = "field-data-service";
    private readonly int _defaultLayerId = 1;

    public DataCollectionViewModel(
        HonuaMobileClient honuaClient,
        IFormDataService formDataService,
        ICameraService cameraService,
        IGpsLocationService gpsService)
    {
        _honuaClient = honuaClient;
        _formDataService = formDataService;
        _cameraService = cameraService;
        _gpsService = gpsService;

        // Initialize form
        _ = Task.Run(InitializeForm);
        _ = Task.Run(UpdateLocation);
    }

    /// <summary>
    /// Capture a photo using the device camera.
    /// </summary>
    [RelayCommand]
    private async Task CapturePhoto()
    {
        try
        {
            var hasPermission = await _cameraService.IsCameraAvailableAsync();
            if (!hasPermission)
            {
                await Shell.Current.DisplayAlert("Permission Denied",
                    "Camera permission is required to capture photos.", "OK");
                return;
            }

            IsLoading = true;
            LoadingMessage = "Opening camera...";

            CapturedPhoto = await _cameraService.CapturePhotoAsync();
            if (CapturedPhoto != null)
            {
                PhotoPath = CapturedPhoto.FullPath;
                FormData["Photo"] = PhotoPath;
                UpdateCanSave();
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Camera Error",
                $"Failed to capture photo: {ex.Message}", "OK");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Select a photo from the gallery.
    /// </summary>
    [RelayCommand]
    private async Task SelectPhoto()
    {
        try
        {
            IsLoading = true;
            LoadingMessage = "Opening gallery...";

            CapturedPhoto = await _cameraService.PickPhotoAsync();
            if (CapturedPhoto != null)
            {
                PhotoPath = CapturedPhoto.FullPath;
                FormData["Photo"] = PhotoPath;
                UpdateCanSave();
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Gallery Error",
                $"Failed to select photo: {ex.Message}", "OK");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Get current GPS location.
    /// </summary>
    [RelayCommand]
    private async Task GetLocation()
    {
        try
        {
            IsLoading = true;
            LoadingMessage = "Getting location...";

            var location = await _gpsService.GetCurrentLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.High, TimeSpan.FromSeconds(10)));

            if (location != null)
            {
                CurrentLocation = location;
                HasLocation = true;
                FormData["Latitude"] = location.Latitude;
                FormData["Longitude"] = location.Longitude;
                FormData["Accuracy"] = location.Accuracy ?? 0;
                FormData["Timestamp"] = DateTime.UtcNow;
                UpdateCanSave();
            }
            else
            {
                await Shell.Current.DisplayAlert("Location Error",
                    "Unable to get current location. Please try again.", "OK");
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("GPS Error",
                $"Failed to get location: {ex.Message}", "OK");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Save the collected data as a new feature.
    /// </summary>
    [RelayCommand]
    private async Task SaveData()
    {
        if (CurrentForm == null)
        {
            await Shell.Current.DisplayAlert("Error", "No form definition loaded.", "OK");
            return;
        }

        try
        {
            IsLoading = true;
            LoadingMessage = "Validating data...";

            // Validate form data
            var validation = await _formDataService.ValidateFormDataAsync(CurrentForm, FormData);
            if (!validation.IsValid)
            {
                var errors = string.Join("\n", validation.Errors);
                await Shell.Current.DisplayAlert("Validation Errors", errors, "OK");
                return;
            }

            LoadingMessage = "Creating feature...";

            // Create geometry if location is available
            byte[]? geometry = null;
            if (CurrentLocation != null)
            {
                var geometryFactory = new NetTopologySuite.Geometries.GeometryFactory(
                    new NetTopologySuite.Geometries.PrecisionModel(), 4326);
                var point = geometryFactory.CreatePoint(
                    new NetTopologySuite.Geometries.Coordinate(CurrentLocation.Longitude, CurrentLocation.Latitude));
                geometry = point.ToBinary();
            }

            // Create feature from form data
            var newFeature = await _formDataService.CreateFeatureFromFormAsync(
                CurrentForm, FormData, geometry);

            LoadingMessage = "Saving to server...";

            // Apply edits to server
            var edits = new Honua.Core.Models.FeatureEdits
            {
                Adds = [newFeature]
            };

            var context = new Honua.Mobile.Sdk.Clients.MobileContext
            {
                AllowOffline = true,
                NetworkPolicy = Honua.Mobile.Sdk.Clients.NetworkPolicy.WifiPreferred,
                ProgressReporter = new Progress<Honua.Mobile.Sdk.Clients.SyncProgress>(progress =>
                {
                    LoadingMessage = progress.Message;
                })
            };

            var result = await _honuaClient.ApplyEditsAsync(_defaultServiceId, _defaultLayerId, edits, context);

            if (result.AddResults.FirstOrDefault()?.Success == true)
            {
                await Shell.Current.DisplayAlert("Success", "Data saved successfully!", "OK");
                await ClearForm();
            }
            else
            {
                var error = result.AddResults.FirstOrDefault()?.Error?.Message ?? "Unknown error";
                await Shell.Current.DisplayAlert("Save Error", $"Failed to save data: {error}", "OK");
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Failed to save data: {ex.Message}", "OK");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Save current form data as a draft.
    /// </summary>
    [RelayCommand]
    private async Task SaveDraft()
    {
        try
        {
            var draftId = $"draft_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            await _formDataService.SaveDraftAsync(draftId, FormData);

            await Shell.Current.DisplayAlert("Draft Saved",
                "Form data has been saved as a draft.", "OK");
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Draft Error",
                $"Failed to save draft: {ex.Message}", "OK");
        }
    }

    /// <summary>
    /// Clear form data.
    /// </summary>
    [RelayCommand]
    private async Task ClearForm()
    {
        FormData.Clear();
        FormFields.Clear();
        CapturedPhoto = null;
        PhotoPath = string.Empty;
        HasLocation = false;
        CurrentLocation = null;
        CanSave = false;

        if (CurrentForm != null)
        {
            await PopulateFormFields();
        }
    }

    /// <summary>
    /// Initialize form definition.
    /// </summary>
    private async Task InitializeForm()
    {
        try
        {
            IsLoading = true;
            LoadingMessage = "Loading form definition...";

            CurrentForm = await _formDataService.GetFormDefinitionAsync(_defaultServiceId, _defaultLayerId);
            await PopulateFormFields();
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Form Error",
                $"Failed to load form: {ex.Message}", "OK");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Update current location in background.
    /// </summary>
    private async Task UpdateLocation()
    {
        while (true)
        {
            try
            {
                if (!HasLocation)
                {
                    await GetLocation();
                }
                await Task.Delay(TimeSpan.FromMinutes(5)); // Update every 5 minutes
            }
            catch
            {
                await Task.Delay(TimeSpan.FromMinutes(1)); // Retry in 1 minute
            }
        }
    }

    /// <summary>
    /// Populate form fields from definition.
    /// </summary>
    private async Task PopulateFormFields()
    {
        if (CurrentForm == null) return;

        await Task.Run(() =>
        {
            foreach (var field in CurrentForm.Fields)
            {
                var fieldData = new FormFieldData
                {
                    Field = field,
                    Value = field.DefaultValue
                };

                if (!string.IsNullOrEmpty(field.DefaultValue))
                {
                    FormData[field.Name] = field.DefaultValue;
                }

                MainThread.BeginInvokeOnMainThread(() => FormFields.Add(fieldData));
            }
        });

        UpdateCanSave();
    }

    /// <summary>
    /// Update form field value.
    /// </summary>
    public void UpdateFieldValue(string fieldName, object? value)
    {
        if (value != null)
        {
            FormData[fieldName] = value;
        }
        else if (FormData.ContainsKey(fieldName))
        {
            FormData.Remove(fieldName);
        }

        UpdateCanSave();
    }

    /// <summary>
    /// Update whether form can be saved.
    /// </summary>
    private void UpdateCanSave()
    {
        if (CurrentForm == null)
        {
            CanSave = false;
            return;
        }

        // Check if all required fields have values
        var requiredFields = CurrentForm.Fields.Where(f => f.Required);
        CanSave = requiredFields.All(field =>
            FormData.ContainsKey(field.Name) &&
            FormData[field.Name] != null &&
            !string.IsNullOrWhiteSpace(FormData[field.Name].ToString()));
    }
}

/// <summary>
/// Wrapper class for form field data binding.
/// </summary>
public class FormFieldData : ObservableObject
{
    public required FormField Field { get; set; }

    private object? _value;
    public object? Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}