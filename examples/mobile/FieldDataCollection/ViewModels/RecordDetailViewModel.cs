using FieldDataCollection.Services;
using Honua.Mobile.Core.Client;
using Honua.Mobile.Core.Models;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace FieldDataCollection.ViewModels;

/// <summary>
/// View model for the Record Detail/Edit screen.
/// Manages feature viewing, editing, and creation operations.
/// </summary>
[QueryProperty(nameof(RecordId), "recordId")]
[QueryProperty(nameof(Mode), "mode")]
public class RecordDetailViewModel : BaseViewModel
{
    private readonly HonuaFeatureClient _client;
    private readonly INavigationService _navigationService;
    private readonly IDialogService _dialogService;
    private readonly ILocationService _locationService;

    private Feature? _feature;
    private string? _recordId;
    private RecordEditMode _mode = RecordEditMode.View;
    private bool _isEditing;
    private bool _hasChanges;

    public RecordDetailViewModel(
        HonuaFeatureClient client,
        INavigationService navigationService,
        IDialogService dialogService,
        ILocationService locationService)
    {
        _client = client;
        _navigationService = navigationService;
        _dialogService = dialogService;
        _locationService = locationService;

        Attributes = new ObservableCollection<AttributeViewModel>();

        // Initialize commands
        EditCommand = new Command(() => StartEditing(), () => !IsEditing && Mode == RecordEditMode.View);
        SaveCommand = new Command(async () => await SaveAsync(), () => IsEditing && HasChanges);
        CancelCommand = new Command(async () => await CancelAsync(), () => IsEditing);
        DeleteCommand = new Command(async () => await DeleteAsync(), () => Mode != RecordEditMode.Create);
        TakePhotoCommand = new Command(async () => await TakePhotoAsync());
        UpdateLocationCommand = new Command(async () => await UpdateLocationAsync());
    }

    #region Properties

    public ObservableCollection<AttributeViewModel> Attributes { get; }

    public Feature? Feature
    {
        get => _feature;
        set => SetProperty(ref _feature, value);
    }

    public string? RecordId
    {
        get => _recordId;
        set
        {
            SetProperty(ref _recordId, value);
            _ = Task.Run(LoadFeatureAsync);
        }
    }

    public RecordEditMode Mode
    {
        get => _mode;
        set
        {
            SetProperty(ref _mode, value);
            UpdateTitle();
            IsEditing = value == RecordEditMode.Create || value == RecordEditMode.Edit;
        }
    }

    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            SetProperty(ref _isEditing, value);
            ((Command)EditCommand).ChangeCanExecute();
            ((Command)SaveCommand).ChangeCanExecute();
            ((Command)CancelCommand).ChangeCanExecute();
        }
    }

    public bool HasChanges
    {
        get => _hasChanges;
        private set
        {
            SetProperty(ref _hasChanges, value);
            ((Command)SaveCommand).ChangeCanExecute();
        }
    }

    public bool IsViewMode => Mode == RecordEditMode.View && !IsEditing;
    public bool IsCreateMode => Mode == RecordEditMode.Create;

    #endregion

    #region Commands

    public ICommand EditCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand TakePhotoCommand { get; }
    public ICommand UpdateLocationCommand { get; }

    #endregion

    #region Public Methods

    public void OnAttributeChanged()
    {
        HasChanges = true;
    }

    #endregion

    #region Private Methods

    private async Task LoadFeatureAsync()
    {
        if (Mode == RecordEditMode.Create)
        {
            await CreateNewFeatureAsync();
            return;
        }

        if (string.IsNullOrEmpty(RecordId)) return;

        await ExecuteAsync(async () =>
        {
            // Query the specific feature by ID
            var query = FeatureQueryBuilder.Create()
                .WithObjectIds(long.Parse(RecordId))
                .WithAllFields()
                .WithGeometry(true);

            var result = await _client.QueryAsync("demo-service", 0, query);
            Feature = result.Items.FirstOrDefault();

            if (Feature != null)
            {
                LoadAttributes();
            }
        }, OnError);
    }

    private async Task CreateNewFeatureAsync()
    {
        var location = await _locationService.GetCurrentLocationAsync();
        var geometry = location != null
            ? PointGeometry.Create(location.Longitude, location.Latitude)
            : PointGeometry.Create(0, 0);

        Feature = Honua.Mobile.Core.Models.Feature.Create(
            new Dictionary<string, object?>
            {
                ["NAME"] = "",
                ["STATUS"] = "Draft",
                ["CREATED_DATE"] = DateTime.Now,
                ["CREATED_BY"] = "Mobile User"
            },
            geometry);

        LoadAttributes();
        HasChanges = false; // New feature doesn't count as changed until user modifies it
    }

    private void LoadAttributes()
    {
        Attributes.Clear();

        if (Feature?.Attributes == null) return;

        foreach (var attr in Feature.Attributes)
        {
            Attributes.Add(new AttributeViewModel
            {
                Name = attr.Key,
                Value = attr.Value?.ToString() ?? "",
                IsReadOnly = attr.Key == "OBJECTID" || (!IsEditing && Mode != RecordEditMode.Create),
                PropertyChanged = (sender, e) => OnAttributeChanged()
            });
        }
    }

    private void StartEditing()
    {
        Mode = RecordEditMode.Edit;
        LoadAttributes(); // Reload to make fields editable
    }

    private async Task SaveAsync()
    {
        if (Feature == null) return;

        await ExecuteAsync(async () =>
        {
            // Update feature with changed attributes
            var updatedAttributes = Attributes.ToDictionary(
                attr => attr.Name,
                attr => (object?)attr.Value);

            var updatedFeature = Feature with { Attributes = updatedAttributes };

            if (Mode == RecordEditMode.Create)
            {
                var createResult = await _client.CreateFeaturesAsync("demo-service", 0, new[] { updatedFeature });
                if (createResult.IsSuccess)
                {
                    await _dialogService.DisplayAlertAsync("Success", "Feature created successfully");
                    await _navigationService.GoBackAsync();
                }
                else
                {
                    await _dialogService.DisplayAlertAsync("Error", createResult.Error?.Message ?? "Failed to create feature");
                }
            }
            else
            {
                var updateResult = await _client.UpdateFeaturesAsync("demo-service", 0, new[] { updatedFeature });
                if (updateResult.IsSuccess)
                {
                    Feature = updatedFeature;
                    Mode = RecordEditMode.View;
                    HasChanges = false;
                    await _dialogService.DisplayAlertAsync("Success", "Feature updated successfully");
                }
                else
                {
                    await _dialogService.DisplayAlertAsync("Error", updateResult.Error?.Message ?? "Failed to update feature");
                }
            }
        }, OnError);
    }

    private async Task CancelAsync()
    {
        if (HasChanges)
        {
            var result = await _dialogService.DisplayConfirmAsync(
                "Discard Changes",
                "Are you sure you want to discard your changes?",
                "Discard",
                "Continue Editing");

            if (!result) return;
        }

        if (Mode == RecordEditMode.Create)
        {
            await _navigationService.GoBackAsync();
        }
        else
        {
            Mode = RecordEditMode.View;
            HasChanges = false;
            await LoadFeatureAsync(); // Reload original data
        }
    }

    private async Task DeleteAsync()
    {
        if (Feature == null || Feature.Id <= 0) return;

        var confirmed = await _dialogService.DisplayConfirmAsync(
            "Delete Feature",
            "Are you sure you want to delete this feature? This action cannot be undone.",
            "Delete",
            "Cancel");

        if (!confirmed) return;

        await ExecuteAsync(async () =>
        {
            var deleteResult = await _client.DeleteFeaturesAsync("demo-service", 0, new[] { Feature.Id });
            if (deleteResult.IsSuccess)
            {
                await _dialogService.DisplayAlertAsync("Success", "Feature deleted successfully");
                await _navigationService.GoBackAsync();
            }
            else
            {
                await _dialogService.DisplayAlertAsync("Error", deleteResult.Error?.Message ?? "Failed to delete feature");
            }
        }, OnError);
    }

    private async Task TakePhotoAsync()
    {
        try
        {
            if (MediaPicker.Default.IsCaptureSupported)
            {
                var photo = await MediaPicker.Default.CapturePhotoAsync();
                if (photo != null)
                {
                    // For now, just show success
                    // Future: Associate photo with feature
                    await _dialogService.DisplayAlertAsync("Photo Captured", $"Photo saved: {photo.FileName}");
                    HasChanges = true;
                }
            }
            else
            {
                await _dialogService.DisplayAlertAsync("Not Supported", "Camera capture is not supported on this device");
            }
        }
        catch (Exception ex)
        {
            await _dialogService.DisplayAlertAsync("Camera Error", ex.Message);
        }
    }

    private async Task UpdateLocationAsync()
    {
        if (Feature == null) return;

        var location = await _locationService.GetCurrentLocationAsync();
        if (location != null)
        {
            Feature = Feature with
            {
                Geometry = PointGeometry.Create(location.Longitude, location.Latitude)
            };
            HasChanges = true;
            await _dialogService.DisplayAlertAsync("Location Updated",
                $"Location updated to {location.Latitude:F6}, {location.Longitude:F6}");
        }
    }

    private void UpdateTitle()
    {
        Title = Mode switch
        {
            RecordEditMode.Create => "New Feature",
            RecordEditMode.Edit => "Edit Feature",
            _ => "Feature Details"
        };
    }

    private async void OnError(Exception ex)
    {
        await _dialogService.DisplayAlertAsync("Error", ex.Message);
    }

    #endregion
}

/// <summary>
/// View model for individual feature attributes.
/// </summary>
public class AttributeViewModel : BaseViewModel
{
    private string _name = string.Empty;
    private string _value = string.Empty;
    private bool _isReadOnly;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public bool IsReadOnly
    {
        get => _isReadOnly;
        set => SetProperty(ref _isReadOnly, value);
    }
}