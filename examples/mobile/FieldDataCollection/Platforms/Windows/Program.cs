using Microsoft.UI.Xaml;

namespace FieldDataCollection.WinUI;

public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Application.Start((p) => new App());
    }
}