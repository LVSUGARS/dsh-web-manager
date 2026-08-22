using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using DshLauncher.Core;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;

namespace DshLauncher.App;

public partial class MainWindow : Window
{
    private readonly DshEngine engine = new();
    private DshStatus? status;
    private LauncherUpdateInfo? launcherUpdate;
    private string? latestDshVersion;
    private Forms.NotifyIcon? trayIcon;
    private bool allowClose;
    private bool isBusy;
    private bool isSidebarCollapsed;
    private bool splashTransitioning;
    private readonly List<SplashParticle> splashParticles = new();
    private readonly DispatcherTimer splashParticleTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private System.Windows.Point? splashMousePosition;
    private System.Windows.Controls.Button? activeNavigation;

    public MainWindow()
    {
        InitializeComponent();
        if (FindResource("WindowIcon") is System.Windows.Media.ImageSource icon) Icon = icon;
        PreviewKeyDown += SplashOverlay_PreviewKeyDown;
        InitializeSplashParticles();
        SplashOverlay.MouseMove += SplashOverlay_ParticleMouseMove;
        SplashOverlay.MouseLeave += SplashOverlay_ParticleMouseLeave;
        splashParticleTimer.Tick += SplashParticleTimer_Tick;
        LoadConfigIntoUi();
        ApplyTheme(engine.Config.Theme);
        ApplyLanguage();
        UpdateSidebar();
        SelectNavigation(HomeNav);
        Loaded += async (_, _) => { UpdateShellClip(); await RefreshStatusAsync(); };
        SizeChanged += (_, _) => UpdateShellClip();
        Shell.SizeChanged += (_, _) => UpdateShellClip();
    }

    private void SplashOverlay_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SplashOverlay.Visibility != Visibility.Visible) return;
        if (e.OriginalSource is Visual source && (source == SplashWindowControls || source.IsDescendantOf(SplashWindowControls))) return;
        FadeOutSplash();
        e.Handled = true;
    }

    private void SplashOverlay_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (SplashOverlay.Visibility != Visibility.Visible) return;
        if (e.Key == Key.Escape)
        {
            Application.Current.Shutdown();
        }
        else if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            FadeOutSplash();
            e.Handled = true;
        }
    }

    private async void FadeOutSplash()
    {
        if (splashTransitioning) return;
        splashTransitioning = true;
        splashMousePosition = null;
        ExplodeParticles();
        await Task.Delay(420);
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        fade.Completed += (_, _) =>
        {
            splashParticleTimer.Stop();
            SplashOverlay.Visibility = Visibility.Collapsed;
            SplashOverlay.IsHitTestVisible = false;
        };
        SplashOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private void ExplodeParticles()
    {
        foreach (var p in splashParticles)
        {
            p.Target = new System.Windows.Point(Random.Shared.NextDouble() * 900 - 100, Random.Shared.NextDouble() * 650 - 100);
        }
    }

    private void SplashMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void SplashMaximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void SplashClose_Click(object sender, RoutedEventArgs e) => Close();

    private void InitializeSplashParticles()
    {
        ParticleCanvas.Children.Clear();
        splashParticles.Clear();
        var targets = SampleWhaleParticleTargets(700, 450, 5);
        var brush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        foreach (var target in targets)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 3, Height = 3, Fill = brush, Opacity = 0.92 };
            var start = new System.Windows.Point(target.X + (Random.Shared.NextDouble() - 0.5) * 220, target.Y + (Random.Shared.NextDouble() - 0.5) * 220);
            System.Windows.Controls.Canvas.SetLeft(dot, start.X - dot.Width / 2);
            System.Windows.Controls.Canvas.SetTop(dot, start.Y - dot.Height / 2);
            ParticleCanvas.Children.Add(dot);
            splashParticles.Add(new SplashParticle(dot, start, new System.Windows.Vector(0, 0), target));
        }
        splashParticleTimer.Start();
    }

    private static System.Collections.Generic.List<System.Windows.Point> SampleWhaleParticleTargets(int width, int height, int step)
    {
        var result = new System.Collections.Generic.List<System.Windows.Point>();
        if (!(System.Windows.Application.Current.FindResource("WhaleGeometry") is System.Windows.Media.Geometry geometry)) return result;
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bounds = geometry.Bounds;
            var scale = Math.Min(width / bounds.Width, height / bounds.Height) * 0.8;
            var tx = (width - bounds.Width * scale) / 2 - bounds.X * scale;
            var ty = (height - bounds.Height * scale) / 2 - bounds.Y * scale;
            dc.PushTransform(new System.Windows.Media.MatrixTransform(scale, 0, 0, scale, tx, ty));
            dc.DrawGeometry(System.Windows.Media.Brushes.White, null, geometry);
            dc.Pop();
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var stride = width * 4;
        var pixels = new byte[height * stride];
        rtb.CopyPixels(pixels, stride, 0);
        for (var y = 0; y < height; y += step)
        {
            for (var x = 0; x < width; x += step)
            {
                if (pixels[y * stride + x * 4 + 3] > 80) result.Add(new System.Windows.Point(x, y));
            }
        }
        return result;
    }

    private void SplashParticleTimer_Tick(object? sender, EventArgs e)
    {
        foreach (var p in splashParticles)
        {
            var dx = p.Target.X - p.Position.X;
            var dy = p.Target.Y - p.Position.Y;
            p.Velocity.X += dx * 0.018;
            p.Velocity.Y += dy * 0.018;
            if (splashMousePosition is System.Windows.Point mouse)
            {
                var mx = p.Position.X - mouse.X;
                var my = p.Position.Y - mouse.Y;
                var dist = Math.Sqrt(mx * mx + my * my);
                if (dist < 90 && dist > 0.01)
                {
                    var force = (90 - dist) / 90 * 3.0;
                    p.Velocity.X += (mx / dist) * force;
                    p.Velocity.Y += (my / dist) * force;
                }
            }
            p.Velocity.X *= 0.86;
            p.Velocity.Y *= 0.86;
            p.Position.X += p.Velocity.X;
            p.Position.Y += p.Velocity.Y;
            System.Windows.Controls.Canvas.SetLeft(p.Dot, p.Position.X - p.Dot.Width / 2);
            System.Windows.Controls.Canvas.SetTop(p.Dot, p.Position.Y - p.Dot.Height / 2);
        }
    }

    private void SplashOverlay_ParticleMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        splashMousePosition = e.GetPosition(ParticleCanvas);
    }

    private void SplashOverlay_ParticleMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        splashMousePosition = null;
    }

    private sealed class SplashParticle
    {
        public System.Windows.Shapes.Ellipse Dot { get; }
          public System.Windows.Point Position;
          public System.Windows.Vector Velocity;
          public System.Windows.Point Target;
        public SplashParticle(System.Windows.Shapes.Ellipse dot, System.Windows.Point position, System.Windows.Vector velocity, System.Windows.Point target)
        {
            Dot = dot; Position = position; Velocity = velocity; Target = target;
        }
    }

    private bool English => string.Equals(engine.Config.Language, "en", StringComparison.OrdinalIgnoreCase);
    private string T(string zh, string en) => English ? en : zh;

    private async Task RefreshStatusAsync()
    {
        if (isBusy) return;
        status = await engine.GetStatusAsync();
        HeroState.Text = StateText(status.State);
        HeroMessage.Text = status.Message;
        NavStatusText.Text = StateText(status.State);
        UrlText.Text = status.Url;
        PidText.Text = status.ListenerPid > 0 ? status.ListenerPid.ToString() : "—";
        WorkspaceText.Text = string.IsNullOrWhiteSpace(engine.Config.Workspace) ? T("尚未选择", "Not selected") : engine.Config.Workspace;
        PortText.Text = engine.Config.Port.ToString();
        var installation = status.Installation;
        DshVersionText.Text = installation?.Version ?? T("未检测到", "Not detected");
        AboutDshVersion.Text = installation?.Version ?? T("未检测到", "Not detected");
        AboutNodeVersion.Text = installation is null ? T("未检测到", "Not detected") : await engine.GetNodeVersionAsync();
        InstallModeText.Text = installation is null ? "—" : installation.Managed ? T("Launcher 管理", "Managed by Launcher") : T("外部安装", "External installation");
        AboutStatus.Text = StateText(status.State);
        HeroDot.Fill = StatusBrush(status.State);
        StatusDot.Fill = StatusBrush(status.State);
        PrimaryAction.Content = status.State switch
        {
            DshState.NotInstalled => T("安装官方 DSH", "Install official DSH"),
            DshState.Running => T("打开 DSH Web", "Open DSH Web"),
            DshState.ExternalService or DshState.Error => T("重新检测", "Check again"),
            _ => T("启动 DSH Web", "Start DSH Web")
        };
        SecondaryAction.Visibility = status.State == DshState.Running ? Visibility.Visible : Visibility.Collapsed;
        SecondaryAction.IsEnabled = status.Managed;
    }

    private async void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (status?.State == DshState.Running)
        {
            OpenUrl();
            return;
        }
        if (status?.State == DshState.ExternalService || status?.State == DshState.Error)
        {
            await RefreshStatusAsync();
            return;
        }
        if (status?.State == DshState.NotInstalled)
        {
            isBusy = true;
            PrimaryAction.IsEnabled = false;
            HeroState.Text = T("正在安装", "Installing");
            HeroMessage.Text = T("正在准备官方 DSH 运行时…", "Preparing the official DSH runtime…");
            var install = await engine.InstallOrUpdateManagedRuntimeAsync(false, new Progress<string>(message => HeroMessage.Text = message));
            isBusy = false;
            PrimaryAction.IsEnabled = true;
            if (!install.Ok) MessageBox.Show(this, install.Message, "DSH Web Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            await RefreshStatusAsync();
            return;
        }
        SaveConfigFromUi();
        isBusy = true;
        PrimaryAction.IsEnabled = false;
        HeroState.Text = T("正在启动", "Starting");
        HeroMessage.Text = T("正在等待本地 Web 响应…", "Waiting for the local Web service…");
        var result = await engine.StartAsync(new Progress<string>(message => HeroMessage.Text = message));
        isBusy = false;
        PrimaryAction.IsEnabled = true;
        if (!result.Ok) MessageBox.Show(this, result.Message, "DSH Web Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
        else if (engine.Config.AutoOpenBrowser) OpenUrl();
        await RefreshStatusAsync();
    }

    private async void SecondaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (status?.State != DshState.Running) return;
        isBusy = true;
        SecondaryAction.IsEnabled = false;
        var result = await Task.Run(engine.Stop);
        isBusy = false;
        if (!result.Ok) MessageBox.Show(this, result.Message, "DSH Web Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
        await RefreshStatusAsync();
    }

    private void SaveConfigFromUi()
    {
        engine.Config.Workspace = WorkspaceInput.Text.Trim();
        if (int.TryParse(PortInput.Text.Trim(), out var port)) engine.Config.Port = port;
        engine.Config.AutoOpenBrowser = AutoOpenCheck.IsChecked == true;
        engine.SaveConfig();
    }

    private void LoadConfigIntoUi()
    {
        WorkspaceInput.Text = engine.Config.Workspace;
        PortInput.Text = engine.Config.Port.ToString();
        AutoOpenCheck.IsChecked = engine.Config.AutoOpenBrowser;
        AutostartCheck.IsChecked = engine.IsAutostartEnabled();
        UpdateOptionButtons();
        LauncherVersionText.Text = T("Launcher 版本 ", "Launcher version ") + engine.LauncherVersion;
        AboutNodeVersion.Text = T("检测中", "Detecting");
    }

    private void ApplyLanguage()
    {
        LanguageButton.Content = English ? "中文" : "EN";
        AppTitleText.Text = "DSH Web";
        AppSubtitleText.Text = "Launcher";
        HomeNavText.Text = T("启动", "Launch");
        SettingsNavText.Text = T("设置", "Settings");
        AboutNavText.Text = T("关于与更新", "About & updates");
        HomeNav.ToolTip = HomeNavText.Text;
        SettingsNav.ToolTip = SettingsNavText.Text;
        AboutNav.ToolTip = AboutNavText.Text;
        SidebarToggleButton.ToolTip = isSidebarCollapsed ? T("展开侧边栏", "Expand sidebar") : T("收起侧边栏", "Collapse sidebar");
        NavStateLabel.Text = T("状态", "Status");
        HomeTitle.Text = T("启动台", "Launchpad");
        HomeSubtitle.Text = T("管理 DSH Web 的运行状态、工作区与本地访问。", "Manage DSH Web status, workspace and local access.");
        ConfigTitle.Text = T("运行配置", "Runtime configuration");
        RuntimeTitle.Text = T("运行环境", "Runtime");
        AddressLabel.Text = T("地址  ", "Address  ");
        WorkspaceLabel.Text = T("工作区", "Workspace");
        PortLabel.Text = T("监听端口", "Port");
        InstallModeLabel.Text = T("安装方式", "Installation");
        SettingsTitle.Text = T("设置", "Settings");
        SettingsSubtitle.Text = T("调整 Launcher 和 DSH Web 的默认行为。", "Adjust Launcher and DSH Web defaults.");
        WorkspacePathLabel.Text = T("工作区路径", "Workspace path");
        BrowseWorkspaceButton.Content = T("选择文件夹", "Browse");
        PortInputLabel.Text = T("监听端口", "Port");
        AutoOpenCheck.Content = T("DSH Web 启动成功后自动打开浏览器", "Open the browser after DSH Web starts");
        AutostartCheck.Content = T("登录 Windows 后自动启动 DSH Web", "Start DSH Web after signing in to Windows");
        AboutTitle.Text = T("关于与更新", "About & updates");
        AboutSubtitle.Text = T("查看版本信息和 Launcher 更新状态。", "View versions and Launcher update status.");
        DshUpdateTitle.Text = T("DSH Web 更新", "DSH Web updates");
        DshUpdateText.Text = T("尚未检查 DSH 更新。", "DSH updates have not been checked yet.");
        DshCheckUpdateButton.Content = T("检查更新", "Check for updates");
        DshUpdateButton.Content = T("更新 DSH", "Update DSH");
        DshOpenOfficialButton.Content = T("打开官网", "Open official site");
        DshOpenGitHubButton.Content = T("GitHub", "GitHub");
        CurrentStatusLabel.Text = T("当前状态", "Current status");
        LauncherUpdateTitle.Text = T("Launcher 更新", "Launcher updates");
        CheckUpdateButton.Content = T("检查更新", "Check for updates");
        InstallUpdateButton.Content = T("下载并安装", "Download & install");
        OpenReleaseButton.Content = T("打开 GitHub Releases", "Open GitHub Releases");
        AppearanceTitle.Text = T("外观与关闭行为", "Appearance & close behavior");
        ThemeLabel.Text = T("主题", "Theme");
        CloseBehaviorLabel.Text = T("关闭窗口", "When closing the window");
        ThemeSystemButton.Content = T("跟随系统", "System");
        ThemeLightButton.Content = T("浅色", "Light");
        ThemeDarkButton.Content = T("深色", "Dark");
        CloseAskButton.Content = T("每次询问", "Ask every time");
        CloseTrayButton.Content = T("最小化到托盘", "Minimize to tray");
        CloseExitButton.Content = T("直接退出 Launcher", "Exit Launcher");
        CloseBehaviorHelp.Text = T("选择“每次询问”时，关闭窗口会让你决定最小化到托盘或直接退出。", "Ask every time lets you choose between minimizing to tray and exiting.");
        LauncherVersionText.Text = T("Launcher 版本 ", "Launcher version ") + engine.LauncherVersion;
        AboutStatus.Text = status is null ? T("检测中", "Detecting") : StateText(status.State);
        UpdateOptionButtons();
    }

    private void Language_Click(object sender, RoutedEventArgs e)
    {
        engine.Config.Language = English ? "zh" : "en";
        engine.SaveConfig();
        ApplyLanguage();
        if (status is not null)
        {
            HeroState.Text = StateText(status.State);
            HeroMessage.Text = status.Message;
            NavStatusText.Text = StateText(status.State);
            AboutStatus.Text = StateText(status.State);
        }
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        SaveConfigFromUi();
        if (sender == AutostartCheck) engine.SetAutostart(AutostartCheck.IsChecked == true);
    }

    private void ThemeOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button) return;
        engine.Config.Theme = button.Tag?.ToString() ?? "system";
        engine.SaveConfig();
        ApplyTheme(engine.Config.Theme);
        UpdateOptionButtons();
    }

    private void CloseBehaviorOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button) return;
        engine.Config.CloseBehavior = button.Tag?.ToString() ?? "ask";
        engine.SaveConfig();
        UpdateOptionButtons();
    }

    private void UpdateOptionButtons()
    {
        SetOptionState(ThemeSystemButton, engine.Config.Theme == "system");
        SetOptionState(ThemeLightButton, engine.Config.Theme == "light");
        SetOptionState(ThemeDarkButton, engine.Config.Theme == "dark");
        SetOptionState(CloseAskButton, engine.Config.CloseBehavior == "ask");
        SetOptionState(CloseTrayButton, engine.Config.CloseBehavior == "tray");
        SetOptionState(CloseExitButton, engine.Config.CloseBehavior == "exit");
    }

    private void SetOptionState(System.Windows.Controls.Button button, bool selected)
    {
        button.Background = (System.Windows.Media.Brush)FindResource(selected ? "AccentSoftBrush" : "SurfaceMutedBrush");
        button.Foreground = (System.Windows.Media.Brush)FindResource(selected ? "AccentBrush" : "SecondaryTextBrush");
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void ApplyTheme(string theme)
    {
        var dark = theme == "dark" || (theme == "system" && IsSystemDarkTheme());
        Resources["WindowBackground"] = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(dark ? "#07111F" : "#F4F7FC"));
        Resources["AtmosphereBrush"] = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(dark ? "#0C1D35" : "#EAF0FA"));
        Resources["HeaderBrush"] = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(dark ? "#07111F" : "#F4F7FC"));
        Resources["SidebarBrush"] = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(dark ? "#071522" : "#EEF3FB"));
        Resources["SurfaceBrush"] = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(dark ? "#122238" : "#FFFFFF"));
        Resources["SurfaceMutedBrush"] = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(dark ? "#1A2D47" : "#E8EEF8"));
        Resources["BorderBrush"] = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(dark ? "#37506D" : "#D2DCEB"));
        Resources["PrimaryTextBrush"] = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(dark ? "#F4F8FF" : "#152033"));
        Resources["SecondaryTextBrush"] = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(dark ? "#9BAFC7" : "#64738B"));
        Resources["AccentBrush"] = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(dark ? "#78A8FF" : "#3E6EDB"));
        Resources["AccentSoftBrush"] = new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(dark ? "#1E3A65" : "#C3D6FF"));
        Resources["PrimaryActionGradient"] = new LinearGradientBrush((WpfColor)WpfColorConverter.ConvertFromString(dark ? "#86B3FF" : "#5C86E8"), (WpfColor)WpfColorConverter.ConvertFromString(dark ? "#4B78E6" : "#365FC6"), 45);
        Resources["HeroGradient"] = new LinearGradientBrush((WpfColor)WpfColorConverter.ConvertFromString(dark ? "#132A49" : "#F8FAFF"), (WpfColor)WpfColorConverter.ConvertFromString(dark ? "#09111D" : "#EAF0FA"), 45);
        UpdateOptionButtons();
        SelectNavigation(activeNavigation ?? HomeNav);
    }

    private static bool IsSystemDarkTheme()
    {
        var value = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0);
        return value is not int light || light == 0;
    }

    private void BrowseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = T("选择 DSH 工作区", "Select DSH workspace") };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) { WorkspaceInput.Text = dialog.SelectedPath; SaveConfigFromUi(); }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateText.Text = T("正在检查 GitHub 最新版本…", "Checking the latest GitHub release…");
        try
        {
            var update = await engine.CheckLauncherUpdateAsync();
            launcherUpdate = update;
            InstallUpdateButton.Visibility = update.IsAvailable && update.PackageUrl is not null ? Visibility.Visible : Visibility.Collapsed;
            UpdateText.Text = update.IsAvailable
                ? T($"发现新版本 {update.LatestVersion}（当前 {update.CurrentVersion}）。\n{update.ReleaseNotes}", $"Version {update.LatestVersion} is available (current {update.CurrentVersion}).\n{update.ReleaseNotes}")
                : T($"当前已是最新版本 {update.CurrentVersion}。", $"Launcher is up to date ({update.CurrentVersion}).");
        }
        catch (Exception ex) { UpdateText.Text = T("检查更新失败：", "Update check failed: ") + ex.Message; }
        finally { CheckUpdateButton.IsEnabled = true; }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (launcherUpdate is null || !launcherUpdate.IsAvailable) return;
        var confirm = MessageBox.Show(this, T($"确认下载并安装 DSH Web Launcher {launcherUpdate.LatestVersion}？\n\n安装完成后 Launcher 会自动重启。", $"Download and install DSH Web Launcher {launcherUpdate.LatestVersion}?\n\nThe Launcher will restart after installation."), "DSH Web Launcher", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (confirm != MessageBoxResult.OK) return;
        CheckUpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateText.Text = T("正在下载更新…", "Downloading update…");
        try
        {
            var package = await engine.DownloadLauncherUpdateAsync(launcherUpdate, new Progress<double>(value => UpdateProgress.Value = value));
            var updater = Path.Combine(AppContext.BaseDirectory, "UpdatePackage.ps1");
            if (!File.Exists(updater)) throw new InvalidOperationException(T("更新组件缺失，请重新下载 Launcher。", "The update component is missing. Download the Launcher again."));
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{updater}\" -Package \"{package}\" -ProcessId {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true });
            allowClose = true;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateText.Text = T("更新失败：", "Update failed: ") + ex.Message;
            CheckUpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = true;
        }
    }

    private void OpenRelease_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://github.com/LVSUGARS/dsh-web-launcher/releases") { UseShellExecute = true });
    private async void DshCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        DshCheckUpdateButton.IsEnabled = false;
        DshUpdateButton.Visibility = Visibility.Collapsed;
        DshUpdateText.Text = T("正在检查 DSH 最新版本…", "Checking latest DSH version…");
        try
        {
            if (status?.Installation is null) await RefreshStatusAsync();
            var installation = status?.Installation;
            if (installation is null)
            {
                DshUpdateText.Text = T("未检测到 DSH 安装。", "DSH installation was not detected.");
                return;
            }
            latestDshVersion = await engine.GetLatestDshVersionAsync();
            var comparison = DshLauncher.Core.DshEngine.CompareVersions(installation.Version, latestDshVersion);
            if (comparison == 0)
            {
                DshUpdateText.Text = T($"DSH {installation.Version} · 已是最新版本。", $"DSH {installation.Version} · Up to date.");
                DshUpdateButton.Visibility = Visibility.Collapsed;
            }
            else if (comparison < 0)
            {
                DshUpdateText.Text = T($"DSH {installation.Version} → {latestDshVersion} · 可更新。", $"DSH {installation.Version} → {latestDshVersion} · Update available.");
                DshUpdateButton.Visibility = installation.Managed ? Visibility.Visible : Visibility.Collapsed;
                if (!installation.Managed) DshUpdateText.Text += T("（外部安装不能自动更新）", " (external install cannot auto-update)");
            }
            else
            {
                DshUpdateText.Text = T($"DSH {installation.Version} · 高于官方 latest {latestDshVersion}。", $"DSH {installation.Version} · Newer than npm latest {latestDshVersion}.");
                DshUpdateButton.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            DshUpdateText.Text = T("DSH 更新检查失败：", "DSH update check failed: ") + ex.Message;
        }
        finally
        {
            DshCheckUpdateButton.IsEnabled = true;
        }
    }

    private async void DshUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (status?.State == DshState.Running)
        {
            MessageBox.Show(this, T("请先停止 DSH Web，再执行更新。", "Please stop DSH Web before updating."), "DSH Web Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DshUpdateButton.IsEnabled = false;
        DshUpdateText.Text = T("正在更新 DSH…", "Updating DSH…");
        try
        {
            var result = await engine.InstallOrUpdateManagedRuntimeAsync(true, new Progress<string>(m => DshUpdateText.Text = m));
            DshUpdateText.Text = result.Message;
            await RefreshStatusAsync();
            if (result.Ok) DshUpdateButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            DshUpdateText.Text = T("DSH 更新失败：", "DSH update failed: ") + ex.Message;
        }
        finally
        {
            DshUpdateButton.IsEnabled = true;
        }
    }

    private void DshOpenOfficial_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://www.deepseek.com/harness/") { UseShellExecute = true });
    private void DshOpenGitHub_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://github.com/deepseek-ai/deepseek-harness") { UseShellExecute = true });

    private void OpenUrl() => Process.Start(new ProcessStartInfo(engine.CurrentUrl) { UseShellExecute = true });

    private void HomeNav_Click(object sender, RoutedEventArgs e) => ShowPage(HomePage, HomeNav, T("控制台", "Console"));
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage, SettingsNav, T("设置", "Settings"));
    private void AboutNav_Click(object sender, RoutedEventArgs e) => ShowPage(AboutPage, AboutNav, T("关于与更新", "About & updates"));
    private void ConfigCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => SettingsNav_Click(sender, e);
    private void RuntimeCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => AboutNav_Click(sender, e);
    private void ShowPage(UIElement page, System.Windows.Controls.Button navigation, string title)
    {
        HomePage.Visibility = SettingsPage.Visibility = AboutPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
        SelectNavigation(navigation);
    }

    private void SelectNavigation(System.Windows.Controls.Button active)
    {
        activeNavigation = active;
        foreach (var button in new[] { HomeNav, SettingsNav, AboutNav })
        {
            var selected = button == active;
            button.Background = (System.Windows.Media.Brush)FindResource(selected ? "AccentSoftBrush" : "SidebarBrush");
            button.Foreground = (System.Windows.Media.Brush)FindResource(selected ? "AccentBrush" : "SecondaryTextBrush");
            button.BorderThickness = selected ? new Thickness(1) : new Thickness(0);
            button.BorderBrush = selected ? (System.Windows.Media.Brush)FindResource("BorderBrush") : System.Windows.Media.Brushes.Transparent;
            button.Padding = selected ? new Thickness(12, 10, 12, 10) : new Thickness(13, 11, 13, 11);
        }
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        isSidebarCollapsed = !isSidebarCollapsed;
        UpdateSidebar();
    }

    private void SidebarLogo_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (isSidebarCollapsed)
        {
            SidebarToggle_Click(sender, e);
            e.Handled = true;
        }
    }

    private void AnimateSidebarWidth(double target)
    {
        var animation = new DoubleAnimation(SidebarBorder.Width, target, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        SidebarBorder.BeginAnimation(FrameworkElement.WidthProperty, animation);
    }

    private void UpdateSidebar()
    {
        AnimateSidebarWidth(isSidebarCollapsed ? 72 : 220);
        BrandTextPanel.Visibility = isSidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarToggleButton.Visibility = isSidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        HomeNavText.Visibility = SettingsNavText.Visibility = AboutNavText.Visibility = isSidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        NavStateLabel.Visibility = NavStatusText.Visibility = isSidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarStatusPanel.HorizontalAlignment = isSidebarCollapsed ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Left;
        StatusDot.Margin = isSidebarCollapsed ? new Thickness(0) : new Thickness(0, 0, 8, 0);
        SidebarTogglePath.Data = Geometry.Parse(isSidebarCollapsed ? "M3,3 L8,8 L3,13" : "M8,3 L3,8 L8,13");
        SidebarToggleButton.ToolTip = isSidebarCollapsed ? T("展开侧边栏", "Expand sidebar") : T("收起侧边栏", "Collapse sidebar");

        if (isSidebarCollapsed)
        {
            SidebarLogoColumn.Width = new GridLength(1, GridUnitType.Star);
            SidebarBrandColumn.Width = GridLength.Auto;
            SidebarToggleColumn.Width = GridLength.Auto;
            SidebarLogo.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            SidebarLogo.Margin = new Thickness(0);
            SidebarNavPanel.Margin = new Thickness(12, 14, 12, 0);
            HomeNav.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;
            SettingsNav.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;
            AboutNav.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;
        }
        else
        {
            SidebarLogoColumn.Width = GridLength.Auto;
            SidebarBrandColumn.Width = new GridLength(1, GridUnitType.Star);
            SidebarToggleColumn.Width = GridLength.Auto;
            SidebarLogo.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            SidebarLogo.Margin = new Thickness(19, 0, 0, 0);
            SidebarNavPanel.Margin = new Thickness(12, 14, 12, 0);
            HomeNav.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left;
            SettingsNav.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left;
            AboutNav.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "❐" : "□";
        Shell.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(9);
        UpdateShellClip();
    }

    private void UpdateShellClip()
    {
        var radius = WindowState == WindowState.Maximized ? 0d : 9d;
        Shell.Clip = new RectangleGeometry(new Rect(0, 0, Shell.ActualWidth, Shell.ActualHeight), radius, radius);
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (allowClose) return;
        var behavior = engine.Config.CloseBehavior;
        if (behavior == "ask")
        {
            var dialog = new CloseChoiceDialog(this, English);
            if (dialog.ShowDialog() != true) { e.Cancel = true; return; }
            behavior = dialog.Choice;
            if (dialog.RememberChoice)
            {
                engine.Config.CloseBehavior = behavior;
                engine.SaveConfig();
            }
        }
        if (behavior == "tray") { e.Cancel = true; HideToTray(); return; }
        allowClose = true;
        trayIcon?.Dispose();
    }

    private void HideToTray()
    {
        trayIcon ??= CreateTrayIcon();
        Hide();
        trayIcon.Visible = true;
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var icon = new Forms.NotifyIcon { Text = "DSH Web Launcher", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(T("打开 Launcher", "Open Launcher"), null, (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); });
        menu.Items.Add("打开 DSH Web", null, (_, _) => OpenUrl());
        menu.Items.Add(T("停止 DSH Web", "Stop DSH Web"), null, (_, _) => { var result = engine.Stop(); if (!result.Ok) MessageBox.Show(result.Message); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(T("退出 Launcher", "Exit Launcher"), null, (_, _) => { allowClose = true; trayIcon!.Visible = false; trayIcon.Dispose(); Application.Current.Shutdown(); });
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => { Show(); WindowState = WindowState.Normal; Activate(); };
        return icon;
    }

    private string StateText(DshState state) => state switch
    {
        DshState.NotInstalled => T("未安装", "Not installed"),
        DshState.Stopped => T("已停止", "Stopped"),
        DshState.Starting => T("启动中", "Starting"),
        DshState.Running => T("运行中", "Running"),
        DshState.ExternalService => T("外部服务", "External service"),
        DshState.Error => T("异常", "Error"),
        _ => T("检测中", "Detecting")
    };

    private static System.Windows.Media.Brush StatusBrush(DshState state) => new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(state switch
    {
        DshState.Running => "#2EAB72",
        DshState.Error or DshState.ExternalService => "#C74255",
        DshState.Stopped => "#A4ADBC",
        _ => "#D5A33A"
    }));
}
