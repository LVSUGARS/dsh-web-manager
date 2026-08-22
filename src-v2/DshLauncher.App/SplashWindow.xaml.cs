using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WpfApplication = System.Windows.Application;

namespace DshLauncher.App;

public partial class SplashWindow : Window
{
    private bool transitioning;

    public event EventHandler? ContinueRequested;

    public SplashWindow()
    {
        InitializeComponent();
        if (FindResource("WindowIcon") is ImageSource icon) Icon = icon;
        Loaded += (_, _) => { UpdateShellClip(); StartLogoGlow(); };
        SizeChanged += (_, _) => UpdateShellClip();
        StateChanged += (_, _) =>
        {
            Shell.CornerRadius = WindowState == WindowState.Maximized ? new CornerRadius(0) : new CornerRadius(9);
            UpdateShellClip();
        };
        PreviewKeyDown += SplashWindow_PreviewKeyDown;
    }

    private void StartLogoGlow()
    {
        var pulse = new DoubleAnimation(0.26, 0.52, TimeSpan.FromSeconds(3.6))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        LogoGlow.BeginAnimation(OpacityProperty, pulse);
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Visual source && (source == WindowControls || source.IsDescendantOf(WindowControls))) return;
        RequestContinue();
        e.Handled = true;
    }

    private void RequestContinue()
    {
        if (transitioning) return;
        transitioning = true;
        ContinueRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SplashWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { WpfApplication.Current.Shutdown(); }
        else if (e.Key == Key.Enter || e.Key == Key.Space) { RequestContinue(); e.Handled = true; }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => WpfApplication.Current.Shutdown();

    private void UpdateShellClip()
    {
        var radius = WindowState == WindowState.Maximized ? 0d : 9d;
        Shell.Clip = new RectangleGeometry(new Rect(0, 0, Shell.ActualWidth, Shell.ActualHeight), radius, radius);
    }
}