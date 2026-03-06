// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");

namespace HonuaFieldApp.Services;

/// <summary>
/// Interface for camera services - provides access to device camera for field data collection.
/// This is a placeholder implementation for the app template.
/// In a real application, implement platform-specific camera functionality.
/// </summary>
public interface ICameraService
{
    /// <summary>
    /// Capture a photo using the device camera.
    /// </summary>
    Task<FileResult?> CapturePhotoAsync();

    /// <summary>
    /// Select a photo from the device gallery.
    /// </summary>
    Task<FileResult?> PickPhotoAsync();

    /// <summary>
    /// Check if camera permissions are granted.
    /// </summary>
    Task<bool> IsCameraAvailableAsync();
}

/// <summary>
/// Stub implementation of camera service for the app template.
/// Replace with platform-specific implementation using Microsoft.Maui.Media or Camera.MAUI packages.
/// </summary>
public class CameraService : ICameraService
{
    public async Task<FileResult?> CapturePhotoAsync()
    {
        try
        {
            var photo = await MediaPicker.CapturePhotoAsync();
            return photo;
        }
        catch (Exception)
        {
            // Handle camera not available or permission denied
            return null;
        }
    }

    public async Task<FileResult?> PickPhotoAsync()
    {
        try
        {
            var photo = await MediaPicker.PickPhotoAsync();
            return photo;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<bool> IsCameraAvailableAsync()
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.Camera>();
            }
            return status == PermissionStatus.Granted;
        }
        catch
        {
            return false;
        }
    }
}