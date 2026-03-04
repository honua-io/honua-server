using FieldDataCollection.ViewModels;

namespace FieldDataCollection.Views;

/// <summary>
/// Settings page for application configuration and diagnostics.
/// </summary>
public partial class SettingsPage : ContentPage
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}