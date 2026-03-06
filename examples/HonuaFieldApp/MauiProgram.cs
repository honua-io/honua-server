// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls.Maps;
using HonuaFieldApp.Views;
using HonuaFieldApp.ViewModels;
using HonuaFieldApp.Services;
using Honua.Mobile.Sdk;
using CommunityToolkit.Maui;

namespace HonuaFieldApp;

/// <summary>
/// MAUI application configuration and dependency injection setup.
/// Demonstrates production-ready mobile app using Honua Mobile SDK.
/// </summary>
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .UseMauiMaps()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Configure logging
        builder.Logging.AddDebug();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);

        // Register Honua Mobile SDK services
        builder.Services.AddHonuaMobile(options =>
        {
            // Configure for your Honua server instance
            options.ServerAddress = "https://your-honua-server.com"; // Update this
            options.ApiKey = "your-api-key"; // Configure authentication

            // Mobile-optimized settings
            options.RequestTimeout = TimeSpan.FromSeconds(30);
            options.EnableOfflineMode = true;
            options.OfflineDatabase = "honua_offline.db";
        });

        // Register application services
        builder.Services.AddTransient<IGpsLocationService, GpsLocationService>();
        builder.Services.AddTransient<ICameraService, CameraService>();
        builder.Services.AddTransient<IMapRenderingService, MapRenderingService>();
        builder.Services.AddTransient<IFormDataService, FormDataService>();

        // Register pages and view models
        builder.Services.AddTransient<MainPage>();
        builder.Services.AddTransient<MainPageViewModel>();

        // Register pages and view models
        // Note: Uncomment when view classes are implemented
        // builder.Services.AddTransient<MapPage>();
        builder.Services.AddTransient<MapPageViewModel>();

        // builder.Services.AddTransient<DataCollectionPage>();
        builder.Services.AddTransient<DataCollectionViewModel>();

        // Performance and monitoring services
        builder.Services.AddSingleton<IPerformanceMonitorService, PerformanceMonitorService>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}