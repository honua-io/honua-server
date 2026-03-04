using Android.App;
using Android.Content.PM;

namespace FieldDataCollection.Platforms.Android;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
    LaunchMode = LaunchMode.SingleTop)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Configure for map support
        Microsoft.Maui.Handlers.MauiMapsHandler.Mapper.AppendToMapping("CustomMapHandler", (handler, view) =>
        {
            // Custom map configuration can go here
        });
    }
}