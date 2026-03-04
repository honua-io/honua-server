using FieldDataCollection.ViewModels;

namespace FieldDataCollection.Views;

/// <summary>
/// Sync center page for managing data synchronization.
/// </summary>
public partial class SyncCenterPage : ContentPage
{
    public SyncCenterPage(SyncCenterViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}