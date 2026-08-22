using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfApplication = System.Windows.Application;

namespace DshLauncher.App;

internal sealed class CloseChoiceDialog : Window
{
    public string Choice { get; private set; } = "exit";
    public bool RememberChoice { get; private set; }

    private readonly System.Windows.Controls.CheckBox remember;

    public CloseChoiceDialog(Window owner, bool english)
    {
        Owner = owner;
        Title = "关闭 DSH Web Launcher";
        Width = 470;
        Height = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        Background = System.Windows.Media.Brushes.Transparent;
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        Icon = owner.Icon;
          remember = new System.Windows.Controls.CheckBox { Content = english ? "Always use this choice (changeable in Settings)" : "始终如此（可在设置中修改）", Margin = new Thickness(0, 22, 0, 0) };

          var content = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
          var header = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "DSH Web Launcher", FontSize = 12, Foreground = (System.Windows.Media.Brush)owner.FindResource("SecondaryTextBrush"), VerticalAlignment = VerticalAlignment.Center });
        var close = MakeButton("×");
        close.Width = 30;
        close.Height = 28;
        close.Padding = new Thickness(0);
        close.Click += (_, _) => DialogResult = false;
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
        content.Children.Add(header);
          content.Children.Add(new TextBlock { Text = english ? "What should happen when this window closes?" : "关闭窗口时，希望 Launcher 怎么处理？", FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
          content.Children.Add(new TextBlock { Text = english ? "The DSH Web service will keep running independently." : "DSH Web 服务会继续独立运行，不会被强制停止。", Foreground = (System.Windows.Media.Brush)owner.FindResource("SecondaryTextBrush"), FontSize = 13, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(remember);
          var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        var tray = MakeButton(english ? "Minimize to tray" : "最小化到托盘");
        tray.Click += (_, _) => Complete("tray");
        var exit = MakeButton(english ? "Exit Launcher" : "退出 Launcher");
        exit.Margin = new Thickness(10, 0, 0, 0);
        exit.Background = (System.Windows.Media.Brush)owner.FindResource("AccentBrush");
        exit.Foreground = System.Windows.Media.Brushes.White;
        exit.Click += (_, _) => Complete("exit");
        buttons.Children.Add(tray);
        buttons.Children.Add(exit);
        content.Children.Add(buttons);
        Content = new Border
        {
            Background = (System.Windows.Media.Brush)owner.FindResource("WindowBackground"),
            BorderBrush = (System.Windows.Media.Brush)owner.FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Child = content
        };
    }

      private System.Windows.Controls.Button MakeButton(string text) => new() { Content = text, Padding = new Thickness(16, 9, 16, 9), Background = Owner is null ? System.Windows.Media.Brushes.Transparent : (System.Windows.Media.Brush)Owner.FindResource("SurfaceMutedBrush") };
    private void Complete(string choice) { Choice = choice; RememberChoice = remember.IsChecked == true; DialogResult = true; }
}
