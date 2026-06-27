using System.Windows;

namespace AssetCuaStudio;

public partial class App : Application
{
    public App()
    {
        // never crash silently to desktop — surface UI-thread exceptions and keep running
        DispatcherUnhandledException += (s, e) =>
        {
            MessageBox.Show(e.Exception.Message, "Asset CUA Studio — error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };
    }
}
