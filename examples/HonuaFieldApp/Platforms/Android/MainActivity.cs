// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");

using Android.App;
using Android.Content.PM;
using Android.OS;

namespace HonuaFieldApp.Platforms.Android;

/// <summary>
/// Main activity for Android platform.
/// Configured for real device testing with proper permissions and optimizations.
/// </summary>
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Request location permissions immediately for field data collection
        RequestLocationPermissions();

        // Configure for field work - keep screen on when plugged in
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            Window?.SetFlags(
                global::Android.Views.WindowManagerFlags.KeepScreenOn,
                global::Android.Views.WindowManagerFlags.KeepScreenOn);
        }
    }

    private void RequestLocationPermissions()
    {
        // Check and request location permissions
        var permissions = new[]
        {
            global::Android.Manifest.Permission.AccessFineLocation,
            global::Android.Manifest.Permission.AccessCoarseLocation,
            global::Android.Manifest.Permission.AccessBackgroundLocation,
            global::Android.Manifest.Permission.Camera,
            global::Android.Manifest.Permission.WriteExternalStorage,
            global::Android.Manifest.Permission.ReadExternalStorage,
            global::Android.Manifest.Permission.Internet,
            global::Android.Manifest.Permission.AccessNetworkState,
            global::Android.Manifest.Permission.AccessWifiState,
            global::Android.Manifest.Permission.WakeLock
        };

        RequestPermissions(permissions, 1000);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        Platform.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        // Log permission results for debugging
        for (int i = 0; i < permissions.Length; i++)
        {
            var permission = permissions[i];
            var result = grantResults[i];
            System.Diagnostics.Debug.WriteLine($"Permission {permission}: {result}");
        }
    }

    protected override void OnResume()
    {
        base.OnResume();

        // Optimize for field data collection
        // Keep CPU awake for GPS tracking
        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            var powerManager = GetSystemService(PowerService) as PowerManager;
            if (powerManager?.IsIgnoringBatteryOptimizations(PackageName) == false)
            {
                // Note: In production, prompt user to disable battery optimization
                System.Diagnostics.Debug.WriteLine("Battery optimization enabled - may affect GPS tracking");
            }
        }
    }
}