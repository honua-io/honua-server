using FieldDataCollection.Views;

namespace FieldDataCollection;

/// <summary>
/// Application shell providing the main navigation structure.
/// Implements tabbed interface with Map, Sync, and Settings tabs.
/// </summary>
public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();

        // Register navigation routes for pages not in tabs
        RegisterNavigationRoutes();
    }

    private static void RegisterNavigationRoutes()
    {
        // Register Record Detail page for navigation from Map
        Routing.RegisterRoute("RecordDetailPage", typeof(RecordDetailPage));
    }
}