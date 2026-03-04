using FieldDataCollection.ViewModels;
using Honua.Mobile.Core.Models;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Maps;

namespace FieldDataCollection.Views;

/// <summary>
/// Map page for visualizing and interacting with geospatial features.
/// </summary>
public partial class MapPage : ContentPage
{
    private readonly MapViewModel _viewModel;

    public MapPage(MapViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        BindingContext = _viewModel;

        // Subscribe to collection changes to update map pins
        _viewModel.Features.CollectionChanged += OnFeaturesChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Focus map on navigation parameters if provided
        if (_viewModel.Latitude != 0 && _viewModel.Longitude != 0)
        {
            var location = new Location(_viewModel.Latitude, _viewModel.Longitude);
            MapView.MoveToRegion(MapSpan.FromCenterAndRadius(location, Distance.FromKilometers(1)));
        }
    }

    private void OnFeaturesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Update map pins when features collection changes
        UpdateMapPins();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MapViewModel.CurrentLocation))
        {
            UpdateCurrentLocationPin();
        }
    }

    private void UpdateMapPins()
    {
        // Clear existing feature pins (keep user location)
        var pinsToRemove = MapView.Pins.Where(p => p.MarkerId != "UserLocation").ToList();
        foreach (var pin in pinsToRemove)
        {
            MapView.Pins.Remove(pin);
        }

        // Add pins for current features
        foreach (var feature in _viewModel.Features)
        {
            if (feature.Geometry is PointGeometry pointGeometry)
            {
                var pin = new Pin
                {
                    Location = new Location(pointGeometry.Y, pointGeometry.X),
                    Label = feature.Attributes.TryGetValue("NAME", out var name) ? name?.ToString() : $"Feature {feature.Id}",
                    Address = feature.Attributes.TryGetValue("STATUS", out var status) ? status?.ToString() : "",
                    MarkerId = feature.Id.ToString()
                };

                pin.MarkerClicked += async (s, args) =>
                {
                    args.HideInfoWindow = true;
                    await _viewModel.FeatureSelectedCommand.ExecuteAsync(feature);
                };

                MapView.Pins.Add(pin);
            }
        }

        // Center map on features if we have them and no current location
        if (_viewModel.Features.Any() && _viewModel.CurrentLocation == null)
        {
            CenterMapOnFeatures();
        }
    }

    private void UpdateCurrentLocationPin()
    {
        // Remove existing user location pin
        var existingPin = MapView.Pins.FirstOrDefault(p => p.MarkerId == "UserLocation");
        if (existingPin != null)
        {
            MapView.Pins.Remove(existingPin);
        }

        // Add new user location pin
        if (_viewModel.CurrentLocation != null)
        {
            var userPin = new Pin
            {
                Location = new Location(_viewModel.CurrentLocation.Latitude, _viewModel.CurrentLocation.Longitude),
                Label = "Your Location",
                Address = $"Accuracy: {_viewModel.LocationAccuracy:F1}m",
                MarkerId = "UserLocation",
                Type = PinType.SavedPin
            };

            MapView.Pins.Add(userPin);

            // Center map on user location
            var region = MapSpan.FromCenterAndRadius(
                new Location(_viewModel.CurrentLocation.Latitude, _viewModel.CurrentLocation.Longitude),
                Distance.FromKilometers(1));

            MapView.MoveToRegion(region);
        }
    }

    private void CenterMapOnFeatures()
    {
        if (!_viewModel.Features.Any()) return;

        var pointFeatures = _viewModel.Features
            .Where(f => f.Geometry is PointGeometry)
            .Select(f => f.Geometry as PointGeometry)
            .Where(g => g != null)
            .ToList();

        if (!pointFeatures.Any()) return;

        // Calculate bounds
        var minLat = pointFeatures.Min(p => p!.Y);
        var maxLat = pointFeatures.Max(p => p!.Y);
        var minLon = pointFeatures.Min(p => p!.X);
        var maxLon = pointFeatures.Max(p => p!.X);

        var centerLat = (minLat + maxLat) / 2;
        var centerLon = (minLon + maxLon) / 2;

        // Calculate appropriate zoom level
        var latDelta = Math.Abs(maxLat - minLat);
        var lonDelta = Math.Abs(maxLon - minLon);
        var maxDelta = Math.Max(latDelta, lonDelta);
        var distance = Distance.FromKilometers(Math.Max(1, maxDelta * 111)); // Rough km conversion

        var region = MapSpan.FromCenterAndRadius(
            new Location(centerLat, centerLon),
            distance);

        MapView.MoveToRegion(region);
    }
}