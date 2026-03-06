// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");

using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
#if ANDROID || IOS
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;
#endif
using Honua.Core.Models;
using Honua.Mobile.Sdk.Clients;
using HonuaFieldApp.Services;

namespace HonuaFieldApp.ViewModels;

/// <summary>
/// ViewModel for the map page demonstrating real-time gRPC communication,
/// GPS tracking, feature rendering, and performance monitoring.
/// </summary>
public partial class MapPageViewModel : ObservableObject
{
    private readonly HonuaMobileClient _honuaClient;
    private readonly IGpsLocationService _gpsService;
    private readonly IMapRenderingService _mapRenderingService;
    private readonly IPerformanceMonitorService _performanceMonitor;

    // Observable properties for UI binding
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string loadingMessage = "Loading...";
    [ObservableProperty] private string connectionStatus = "Disconnected";
    [ObservableProperty] private Color connectionStatusColor = Colors.Red;
    [ObservableProperty] private string gpsStatus = "Unknown";
    [ObservableProperty] private Color gpsStatusColor = Colors.Gray;
    [ObservableProperty] private int featureCount;
    [ObservableProperty] private double renderTime;
    [ObservableProperty] private double memoryUsage;
    [ObservableProperty] private string whereClause = "1=1";
#if ANDROID || IOS
    [ObservableProperty] private MapType selectedMapType = MapType.Street;
#else
    [ObservableProperty] private int selectedMapType = 0; // Placeholder for non-mobile builds
#endif
    [ObservableProperty] private bool isShowingUser = true;
    [ObservableProperty] private bool canDownloadArea;

    public bool IsNotLoading => !IsLoading;

    // Collections for map data
#if ANDROID || IOS
    public ObservableCollection<Pin> MapPins { get; } = new();
    public ObservableCollection<Polygon> MapPolygons { get; } = new();
    public ObservableCollection<Polyline> MapPolylines { get; } = new();
#else
    public ObservableCollection<object> MapPins { get; } = new();
    public ObservableCollection<object> MapPolygons { get; } = new();
    public ObservableCollection<object> MapPolylines { get; } = new();
#endif

    // Services for current operation
    private readonly string _defaultServiceId = "field-data-service";
    private readonly int _defaultLayerId = 1;
#if ANDROID || IOS
    private Location? _currentLocation;
#else
    private object? _currentLocation; // Placeholder for non-mobile builds
#endif
    private CancellationTokenSource? _currentOperation;

    public MapPageViewModel(
        HonuaMobileClient honuaClient,
        IGpsLocationService gpsService,
        IMapRenderingService mapRenderingService,
        IPerformanceMonitorService performanceMonitor)
    {
        _honuaClient = honuaClient;
        _gpsService = gpsService;
        _mapRenderingService = mapRenderingService;
        _performanceMonitor = performanceMonitor;

        // Start monitoring
        _ = Task.Run(StartLocationTracking);
        _ = Task.Run(StartPerformanceMonitoring);
        _ = Task.Run(InitializeMapData);
    }

    /// <summary>
    /// Refresh map data from the server using gRPC.
    /// Demonstrates end-to-end connectivity and performance measurement.
    /// </summary>
    [RelayCommand]
    private async Task RefreshData()
    {
        await QueryFeaturesAsync();
    }

    /// <summary>
    /// Execute a feature query with the current where clause.
    /// </summary>
    [RelayCommand]
    private async Task QueryFeatures()
    {
        await QueryFeaturesAsync();
    }

    /// <summary>
    /// Center map on current GPS location.
    /// </summary>
    [RelayCommand]
    private async Task CenterOnLocation()
    {
        try
        {
            var location = await _gpsService.GetCurrentLocationAsync(
                new GeolocationRequest(GeolocationAccuracy.High, TimeSpan.FromSeconds(10)));

            if (location != null)
            {
                _currentLocation = location;
                UpdateGpsStatus("GPS Active", Colors.Green);

                // Trigger map centering through service
                await _mapRenderingService.CenterMapAsync(location.Latitude, location.Longitude);
            }
            else
            {
                UpdateGpsStatus("GPS Failed", Colors.Red);
            }
        }
        catch (Exception ex)
        {
            UpdateGpsStatus($"GPS Error: {ex.Message}", Colors.Red);
        }
    }

    /// <summary>
    /// Add a point feature at the current location.
    /// Demonstrates real-time data collection and gRPC edit operations.
    /// </summary>
    [RelayCommand]
    private async Task AddPoint()
    {
        if (_currentLocation == null)
        {
            await Shell.Current.DisplayAlert("Location Required", "Please enable GPS and get current location first.", "OK");
            return;
        }

        try
        {
            IsLoading = true;
            LoadingMessage = "Creating feature...";

            // Create a new feature at current location
            var newFeature = await _mapRenderingService.CreatePointFeatureAsync(
                _currentLocation.Latitude,
                _currentLocation.Longitude,
                new Dictionary<string, object>
                {
                    ["Name"] = $"Field Point {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    ["Type"] = "GPS Collected",
                    ["Accuracy"] = _currentLocation.Accuracy ?? 0,
                    ["Timestamp"] = DateTime.UtcNow,
                    ["CreatedBy"] = "Mobile App"
                });

            var edits = new FeatureEdits
            {
                Adds = [newFeature]
            };

            var context = new MobileContext
            {
                AllowOffline = true,
                NetworkPolicy = NetworkPolicy.WifiPreferred,
                ProgressReporter = new Progress<SyncProgress>(progress =>
                {
                    LoadingMessage = progress.Message;
                })
            };

            var result = await _honuaClient.ApplyEditsAsync(_defaultServiceId, _defaultLayerId, edits, context);

            if (result.AddResults.FirstOrDefault()?.Success == true)
            {
                await Shell.Current.DisplayAlert("Success", "Point feature created successfully!", "OK");
                await QueryFeaturesAsync(); // Refresh map
            }
            else
            {
                var error = result.AddResults.FirstOrDefault()?.Error?.Message ?? "Unknown error";
                await Shell.Current.DisplayAlert("Error", $"Failed to create feature: {error}", "OK");
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Failed to add point: {ex.Message}", "OK");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Download map area for offline use.
    /// Demonstrates offline capability and large dataset handling.
    /// </summary>
    [RelayCommand]
    private async Task DownloadArea()
    {
        if (_currentLocation == null)
        {
            await Shell.Current.DisplayAlert("Location Required", "Please get current location first.", "OK");
            return;
        }

        try
        {
            IsLoading = true;
            LoadingMessage = "Downloading area for offline use...";

            // Create bounding box around current location (2km radius)
            var buffer = 0.01; // Approximately 1km at equator
            var boundingBox = new NetTopologySuite.Geometries.Envelope(
                _currentLocation.Longitude - buffer,
                _currentLocation.Longitude + buffer,
                _currentLocation.Latitude - buffer,
                _currentLocation.Latitude + buffer);

            var result = await _honuaClient.DownloadAreaAsync(
                _defaultServiceId,
                _defaultLayerId,
                boundingBox);

            if (result.Success)
            {
                await Shell.Current.DisplayAlert("Success",
                    $"Downloaded {result.FeatureCount} features for offline use!", "OK");
            }
            else
            {
                await Shell.Current.DisplayAlert("Error",
                    $"Download failed: {result.ErrorMessage}", "OK");
            }
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlert("Error", $"Download failed: {ex.Message}", "OK");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Toggle map layer visibility.
    /// </summary>
    [RelayCommand]
    private void ToggleLayers()
    {
        // Cycle through map types
        SelectedMapType = SelectedMapType switch
        {
            MapType.Street => MapType.Satellite,
            MapType.Satellite => MapType.Hybrid,
            MapType.Hybrid => MapType.Street,
            _ => MapType.Street
        };
    }

    /// <summary>
    /// Zoom to show all features.
    /// </summary>
    [RelayCommand]
    private async Task ZoomToFeatures()
    {
        await _mapRenderingService.ZoomToFeaturesAsync(MapPins, MapPolygons, MapPolylines);
    }

    /// <summary>
    /// Handle map click events for feature selection.
    /// </summary>
    [RelayCommand]
    private async Task MapClicked(MapClickedEventArgs args)
    {
        var position = args.Location;

        // Find nearby features
        var nearbyFeatures = await _mapRenderingService.FindFeaturesNearLocationAsync(
            position.Latitude, position.Longitude, 100); // 100m radius

        if (nearbyFeatures.Any())
        {
            var feature = nearbyFeatures.First();
            await Shell.Current.GoToAsync($"featuredetails?id={feature.ObjectId}");
        }
    }

    /// <summary>
    /// Show settings page.
    /// </summary>
    [RelayCommand]
    private async Task ShowSettings()
    {
        await Shell.Current.GoToAsync("settings");
    }

    /// <summary>
    /// Main method for querying features and updating the map.
    /// </summary>
    private async Task QueryFeaturesAsync()
    {
        try
        {
            IsLoading = true;
            LoadingMessage = "Querying features...";

            _currentOperation?.Cancel();
            _currentOperation = new CancellationTokenSource();

            var stopwatch = Stopwatch.StartNew();

            var query = new FeatureQuery
            {
                Where = WhereClause,
                ResultRecordCount = 1000,
                ReturnGeometry = true,
                OutFields = ["Name", "Type", "Category", "Description", "Timestamp"]
            };

            // Add spatial filter if current location is available
            if (_currentLocation != null)
            {
                var buffer = 0.05; // 5km radius approximately
                var geometryFactory = new NetTopologySuite.Geometries.GeometryFactory(
                    new NetTopologySuite.Geometries.PrecisionModel(), 4326);
                var envelope = new NetTopologySuite.Geometries.Envelope(
                    _currentLocation.Longitude - buffer,
                    _currentLocation.Longitude + buffer,
                    _currentLocation.Latitude - buffer,
                    _currentLocation.Latitude + buffer);
                var boundingBox = geometryFactory.ToGeometry(envelope);

                query = query with
                {
                    SpatialFilter = new SpatialFilter
                    {
                        Geometry = boundingBox,
                        SpatialRelationship = SpatialRelationship.Intersects
                    }
                };
            }

            var context = new MobileContext
            {
                AllowOffline = true,
                NetworkPolicy = NetworkPolicy.WifiPreferred,
                BatteryPolicy = BatteryPolicy.Normal,
                CancellationToken = _currentOperation.Token,
                ProgressReporter = new Progress<SyncProgress>(progress =>
                {
                    LoadingMessage = progress.Message;
                })
            };

            var result = await _honuaClient.QueryFeaturesAsync(
                _defaultServiceId, _defaultLayerId, query, context, _currentOperation.Token);

            stopwatch.Stop();

            // Update UI with results
            await UpdateMapWithFeatures(result.Features);

            FeatureCount = result.Features.Count;
            RenderTime = stopwatch.ElapsedMilliseconds;

            // Update connection status
            UpdateConnectionStatus("Connected", Colors.Green);

            LoadingMessage = $"Loaded {FeatureCount} features in {RenderTime}ms";
        }
        catch (OperationCanceledException)
        {
            LoadingMessage = "Query cancelled";
        }
        catch (Exception ex)
        {
            UpdateConnectionStatus($"Error: {ex.Message}", Colors.Red);
            await Shell.Current.DisplayAlert("Query Error", ex.Message, "OK");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Update map pins and polygons with feature data.
    /// </summary>
    private async Task UpdateMapWithFeatures(IReadOnlyList<Honua.Core.Features.FeatureStore.Domain.Feature> features)
    {
        await Task.Run(() =>
        {
            MapPins.Clear();
            MapPolygons.Clear();
            MapPolylines.Clear();

            foreach (var feature in features)
            {
                if (feature.Geometry == null) continue;

                var geometry = NetTopologySuite.IO.WKBReader.Read(feature.Geometry);

                switch (geometry.GeometryType)
                {
                    case "Point":
                        var point = (NetTopologySuite.Geometries.Point)geometry;
                        var pin = new Pin
                        {
                            Location = new Location(point.Y, point.X),
                            Label = feature.Attributes?.GetValueOrDefault("Name")?.ToString() ?? $"Feature {feature.ObjectId}",
                            Address = feature.Attributes?.GetValueOrDefault("Type")?.ToString() ?? "Unknown Type",
                            Type = PinType.Place
                        };

                        MainThread.BeginInvokeOnMainThread(() => MapPins.Add(pin));
                        break;

                    case "Polygon":
                        var polygon = (NetTopologySuite.Geometries.Polygon)geometry;
                        var mapPolygon = new Polygon
                        {
                            StrokeColor = Colors.Blue,
                            StrokeWidth = 2,
                            FillColor = Colors.Blue.WithAlpha(0.3f)
                        };

                        foreach (var coordinate in polygon.ExteriorRing.Coordinates)
                        {
                            mapPolygon.Geopath.Add(new Location(coordinate.Y, coordinate.X));
                        }

                        MainThread.BeginInvokeOnMainThread(() => MapPolygons.Add(mapPolygon));
                        break;

                    case "LineString":
                        var lineString = (NetTopologySuite.Geometries.LineString)geometry;
                        var mapPolyline = new Polyline
                        {
                            StrokeColor = Colors.Red,
                            StrokeWidth = 3
                        };

                        foreach (var coordinate in lineString.Coordinates)
                        {
                            mapPolyline.Geopath.Add(new Location(coordinate.Y, coordinate.X));
                        }

                        MainThread.BeginInvokeOnMainThread(() => MapPolylines.Add(mapPolyline));
                        break;
                }
            }
        });
    }

    /// <summary>
    /// Start background GPS tracking.
    /// </summary>
    private async Task StartLocationTracking()
    {
        while (true)
        {
            try
            {
                await CenterOnLocation();
                CanDownloadArea = _currentLocation != null;
                await Task.Delay(TimeSpan.FromMinutes(1)); // Update every minute
            }
            catch
            {
                UpdateGpsStatus("GPS Unavailable", Colors.Orange);
                await Task.Delay(TimeSpan.FromMinutes(5)); // Retry in 5 minutes
            }
        }
    }

    /// <summary>
    /// Start performance monitoring.
    /// </summary>
    private async Task StartPerformanceMonitoring()
    {
        while (true)
        {
            try
            {
                var metrics = await _performanceMonitor.GetCurrentMetricsAsync();
                MemoryUsage = metrics.MemoryUsageMB;

                await Task.Delay(TimeSpan.FromSeconds(5)); // Update every 5 seconds
            }
            catch
            {
                await Task.Delay(TimeSpan.FromMinutes(1)); // Retry in 1 minute
            }
        }
    }

    /// <summary>
    /// Initialize map with default data.
    /// </summary>
    private async Task InitializeMapData()
    {
        await Task.Delay(1000); // Let UI initialize
        await QueryFeaturesAsync();
    }

    private void UpdateConnectionStatus(string status, Color color)
    {
        ConnectionStatus = status;
        ConnectionStatusColor = color;
    }

    private void UpdateGpsStatus(string status, Color color)
    {
        GpsStatus = status;
        GpsStatusColor = color;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName == nameof(IsLoading))
        {
            OnPropertyChanged(nameof(IsNotLoading));
        }
    }
}