namespace FieldDataCollection;

/// <summary>
/// Main application class.
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();

        // Set the main shell as the root page
        MainPage = new AppShell();
    }
}