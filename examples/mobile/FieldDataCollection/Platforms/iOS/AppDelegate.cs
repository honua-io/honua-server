using Foundation;
using UIKit;

namespace FieldDataCollection.Platforms.iOS;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
    {
        var result = base.FinishedLaunching(application, launchOptions);

        // Configure for map support
        Microsoft.Maui.Handlers.MauiMapsHandler.Mapper.AppendToMapping("CustomMapHandler", (handler, view) =>
        {
            // Custom map configuration can go here
        });

        return result;
    }
}