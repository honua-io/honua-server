using FieldDataCollection.ViewModels;

namespace FieldDataCollection.Views;

/// <summary>
/// Record detail page for viewing and editing feature attributes.
/// </summary>
public partial class RecordDetailPage : ContentPage
{
    public RecordDetailPage(RecordDetailViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}