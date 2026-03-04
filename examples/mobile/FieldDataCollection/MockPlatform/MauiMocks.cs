// Mock implementations for MAUI types to allow compilation in non-MAUI environment
// These would be replaced with real MAUI types when building for mobile platforms

using System.ComponentModel;

namespace Microsoft.Maui.Controls
{
    public class ContentPage : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public object? BindingContext { get; set; }
        public string? Title { get; set; }
    }

    public class Application
    {
        public static Application? Current { get; set; }
        public ContentPage? MainPage { get; set; }
    }

    public class Shell : ContentPage
    {
        public static Shell Current { get; set; } = new Shell();
        public async Task GoToAsync(string route) => await Task.CompletedTask;
        public async Task GoToAsync(string route, IDictionary<string, object> parameters) => await Task.CompletedTask;
    }

    public class ContentView : ContentPage { }
    public class StackLayout : ContentView { }
    public class Grid : ContentView { }
    public class Frame : ContentView { }
    public class Label : ContentView { }
    public class Entry : ContentView { }
    public class Button : ContentView { }
    public class Switch : ContentView { }
    public class ActivityIndicator : ContentView { }
    public class ProgressBar : ContentView { }
    public class SearchBar : ContentView { }
    public class ScrollView : ContentView { }
    public class CollectionView : ContentView { }

    public interface ICommand
    {
        event EventHandler? CanExecuteChanged;
        bool CanExecute(object? parameter);
        void Execute(object? parameter);
    }

    public class Command : ICommand
    {
        private readonly Action? _execute;
        private readonly Func<bool>? _canExecute;

        public Command(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public Command(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = () => execute(null);
            _canExecute = () => canExecute?.Invoke(null) ?? true;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
        public void Execute(object? parameter) => _execute?.Invoke();
        public void ChangeCanExecute() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

namespace Microsoft.Maui.Controls.Maps
{
    public class Map : Microsoft.Maui.Controls.ContentView { }
    public class Pin {
        public Location? Location { get; set; }
        public string? Label { get; set; }
        public string? Address { get; set; }
        public string? MarkerId { get; set; }
        public PinType Type { get; set; }
        public event EventHandler<PinClickedEventArgs>? MarkerClicked;
    }

    public class PinClickedEventArgs : EventArgs
    {
        public bool HideInfoWindow { get; set; }
    }

    public enum PinType { SavedPin }

    public class MapSpan
    {
        public static MapSpan FromCenterAndRadius(Location center, Distance radius) => new();
    }

    public class Distance
    {
        public static Distance FromKilometers(double km) => new();
    }
}

namespace Microsoft.Maui.Essentials
{
    public class Location
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double? Accuracy { get; set; }

        public Location(double latitude, double longitude)
        {
            Latitude = latitude;
            Longitude = longitude;
        }
    }

    public enum GeolocationAccuracy { Medium, Best }

    public class GeolocationRequest
    {
        public GeolocationAccuracy DesiredAccuracy { get; set; }
        public TimeSpan Timeout { get; set; }
    }

    public static class Geolocation
    {
        public static async Task<Location?> GetLocationAsync(GeolocationRequest request) => new Location(0, 0);
    }

    public enum PermissionStatus { Granted, Denied }

    public static class Permissions
    {
        public static async Task<PermissionStatus> CheckStatusAsync<T>() => PermissionStatus.Granted;
        public static async Task<PermissionStatus> RequestAsync<T>() => PermissionStatus.Granted;
        public class LocationWhenInUse { }
    }

    public static class Preferences
    {
        public static string? Get(string key, string? defaultValue) => defaultValue;
        public static void Set(string key, string value) { }
        public static void Remove(string key) { }
        public static int Get(string key, int defaultValue) => defaultValue;
        public static void Set(string key, int value) { }
        public static bool Get(string key, bool defaultValue) => defaultValue;
        public static void Set(string key, bool value) { }
    }

    public static class SecureStorage
    {
        public static async Task<string?> GetAsync(string key) => null;
        public static async Task SetAsync(string key, string value) { }
        public static void Remove(string key) { }
    }

    public enum NetworkAccess { Internet, None }
    public enum ConnectionProfile { WiFi, Cellular, Ethernet }

    public static class Connectivity
    {
        public static ConnectivityInfo Current { get; } = new();
        public static event EventHandler<ConnectivityChangedEventArgs>? ConnectivityChanged;
    }

    public class ConnectivityInfo
    {
        public NetworkAccess NetworkAccess { get; set; } = NetworkAccess.Internet;
        public IEnumerable<ConnectionProfile> ConnectionProfiles { get; set; } = new[] { ConnectionProfile.WiFi };
    }

    public class ConnectivityChangedEventArgs : EventArgs
    {
        public NetworkAccess NetworkAccess { get; set; }
    }

    public static class DeviceInfo
    {
        public static DeviceInfo Current { get; } = new();
        public static string Platform => "Mock";
        public static string Model => "Mock Device";
        public static string VersionString => "1.0.0";
    }

    public static class AppInfo
    {
        public static AppInfo Current { get; } = new();
        public static string VersionString => "1.0.0";
    }

    public static class MediaPicker
    {
        public static MediaPicker Default { get; } = new();
        public bool IsCaptureSupported => false;
        public async Task<FileResult?> CapturePhotoAsync() => null;
    }

    public class FileResult
    {
        public string? FileName { get; set; }
        public string? FullPath { get; set; }
    }
}

namespace Microsoft.Maui.Graphics
{
    public static class Colors
    {
        public static Color Black { get; } = new();
    }

    public class Color
    {
        public static Color FromArgb(string hex) => new();
    }
}

// Extension method for Command
namespace System.Windows.Input
{
    public static class CommandExtensions
    {
        public static async Task ExecuteAsync(this ICommand command, object? parameter = null)
        {
            if (command.CanExecute(parameter))
            {
                command.Execute(parameter);
            }
            await Task.CompletedTask;
        }
    }
}