using System.Windows;
using System.Drawing;

namespace DshLauncher.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SetWindowIcon();
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(0);
            return;
        }
        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }

    private static void SetWindowIcon()
    {
        using var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        if (icon is null) return;
        var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        System.Windows.Application.Current.Resources["WindowIcon"] = source;
    }
}
