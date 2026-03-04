using FieldDataCollection.Services;
using Honua.Mobile.Core.Client;
using Honua.Mobile.Core.Models;
using Honua.Mobile.Core.Querying;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace FieldDataCollection.ViewModels;

/// <summary>
/// View model for the Map screen.
/// Manages map display, feature visualization, location services, and data collection.
/// </summary>
[QueryProperty(nameof(Latitude), "latitude")]
[QueryProperty(nameof(Longitude), "longitude")]
public class MapViewModel : BaseViewModel
{
    private readonly HonuaFeatureClient _client;
    private readonly ILocationService _locationService;
    private readonly INavigationService _navigationService;
    private readonly IDialogService _dialogService;

    private Location? _currentLocation;
    private double? _locationAccuracy;
    private bool _isLocationEnabled;
    private string _searchText = string.Empty;

    public MapViewModel(
        HonuaFeatureClient client,
        ILocationService locationService,
        INavigationService navigationService,
        IDialogService dialogService)
    {
        _client = client;
        _locationService = locationService;
        _navigationService = navigationService;
        _dialogService = dialogService;

        Title = "Map";

        Features = new ObservableCollection<Feature>();

        // Initialize commands
        RefreshLocationCommand = new Command(async () => await RefreshLocationAsync());
        SearchCommand = new Command<string>(async (text) => await SearchAsync(text));
        CreateFeatureCommand = new Command(async () => await CreateFeatureAsync());
        FeatureSelectedCommand = new Command<Feature>(async (feature) => await OnFeatureSelectedAsync(feature));
        CenterOnLocationCommand = new Command(async () => await CenterOnLocationAsync());

        // Subscribe to location accuracy changes
        _locationService.LocationAccuracyChanged += OnLocationAccuracyChanged;

        // Initialize location services
        _ = Task.Run(InitializeLocationAsync);
    }

    #region Properties

    public ObservableCollection<Feature> Features { get; }

    public Location? CurrentLocation
    {
        get => _currentLocation;
        set => SetProperty(ref _currentLocation, value);
    }

    public double? LocationAccuracy
    {
        get => _locationAccuracy;
        set => SetProperty(ref _locationAccuracy, value);
    }

    public bool IsLocationEnabled
    {
        get => _isLocationEnabled;
        set => SetProperty(ref _isLocationEnabled, value);
    }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    // Navigation parameters
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    #endregion

    #region Commands

    public ICommand RefreshLocationCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand CreateFeatureCommand { get; }
    public ICommand FeatureSelectedCommand { get; }
    public ICommand CenterOnLocationCommand { get; }

    #endregion

    #region Public Methods

    public async Task LoadNearbyFeaturesAsync()
    {
        if (CurrentLocation == null) return;

        await ExecuteAsync(async () =>
        {
            // Query features within 1km of current location
            var query = FeatureQueryBuilder.Create()
                .Near(CurrentLocation.Longitude, CurrentLocation.Latitude, 1000, DistanceUnit.Meters)
                .WithFields("OBJECTID", "NAME", "STATUS", "CREATED_DATE")
                .WithGeometry(true)
                .WithLimit(50)
                .OrderByDesc("CREATED_DATE");

            // For demo purposes, use a placeholder service
            // In a real app, this would come from configuration
            var features = await _client.QueryAsync("demo-service", 0, query);

            Features.Clear();
            foreach (var feature in features.Items)
            {
                Features.Add(feature);
            }
        }, OnError);
    }

    #endregion

    #region Private Methods

    private async Task InitializeLocationAsync()
    {
        IsLocationEnabled = await _locationService.IsLocationAvailableAsync();

        if (!IsLocationEnabled)
        {
            var granted = await _locationService.RequestLocationPermissionAsync();
            IsLocationEnabled = granted;
        }

        if (IsLocationEnabled)
        {
            await RefreshLocationAsync();
        }
    }

    private async Task RefreshLocationAsync()
    {
        if (!IsLocationEnabled) return;

        await ExecuteAsync(async () =>
        {
            CurrentLocation = await _locationService.GetCurrentLocationAsync(
                GeolocationAccuracy.Best,
                TimeSpan.FromSeconds(30));

            if (CurrentLocation != null)
            {
                LocationAccuracy = CurrentLocation.Accuracy;
                await LoadNearbyFeaturesAsync();
            }
        }, OnError);
    }

    private async Task SearchAsync(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText)) return;

        await ExecuteAsync(async () =>
        {
            var query = FeatureQueryBuilder.Create()
                .Where($"NAME LIKE '%{searchText}%'")
                .WithFields("OBJECTID", "NAME", "STATUS", "ADDRESS")
                .WithGeometry(true)
                .WithLimit(20)
                .OrderByAsc("NAME");

            var features = await _client.QueryAsync("demo-service", 0, query);

            Features.Clear();
            foreach (var feature in features.Items)
            {
                Features.Add(feature);
            }
        }, OnError);
    }

    private async Task CreateFeatureAsync()
    {
        if (CurrentLocation == null)
        {
            await _dialogService.DisplayAlertAsync(
                "Location Required",
                "Current location is required to create a new feature. Please enable location services and try again.");
            return;
        }

        await _navigationService.GoToRecordDetailAsync(null, RecordEditMode.Create);
    }

    private async Task OnFeatureSelectedAsync(Feature feature)
    {
        if (feature.Id > 0)
        {
            await _navigationService.GoToRecordDetailAsync(feature.Id.ToString(), RecordEditMode.View);
        }
    }

    private async Task CenterOnLocationAsync()
    {
        await RefreshLocationAsync();
    }

    private void OnLocationAccuracyChanged(object? sender, LocationAccuracyChangedEventArgs e)
    {
        LocationAccuracy = e.AccuracyMeters;
    }

    private async void OnError(Exception ex)
    {
        await _dialogService.DisplayAlertAsync("Error", ex.Message);
    }

    #endregion
}