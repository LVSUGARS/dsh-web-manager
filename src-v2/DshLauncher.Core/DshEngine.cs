using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DshLauncher.Core;

public sealed class DshEngine
{
    private const string NodeOptions = "--max-old-space-size=8192";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string configPath;
    private readonly string statePath;

    public string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSHWebManager");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string RuntimeDirectory => Path.Combine(DataDirectory, "runtime");
    public string StartupShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DSH Web 启动器.lnk");
    public AppConfig Config { get; private set; }
    public string LauncherVersion => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "2.0.0";
    public string CurrentUrl => $"http://127.0.0.1:{Config.Port}";

    public DshEngine()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        configPath = Path.Combine(DataDirectory, "config.json");
        statePath = Path.Combine(DataDirectory, "state.json");
        Config = LoadConfig();
    }

    public void SaveConfig()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(configPath, JsonSerializer.Serialize(Config, JsonOptions), new UTF8Encoding(false));
    }

    public bool IsAutostartEnabled() => File.Exists(StartupShortcut);

    public OperationResult SetAutostart(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(StartupShortcut)) File.Delete(StartupShortcut);
                return OperationResult.Success("已关闭开机启动。");
            }

            SaveConfig();
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return OperationResult.Fail("系统未提供 Windows 快捷方式组件。");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(StartupShortcut);
            shortcut.TargetPath = Environment.ProcessPath!;
            shortcut.Arguments = "--start";
            shortcut.WorkingDirectory = AppContext.BaseDirectory;
            shortcut.Description = "Start DSH Web at user sign-in";
            shortcut.Save();
            return OperationResult.Success("已开启开机启动。");
        }
        catch (Exception ex) { return OperationResult.Fail("修改开机启动失败：" + ex.Message); }
    }

    public DshInstallation? FindInstallation()
    {
        var managedNode = Path.Combine(RuntimeDirectory, "node", "node.exe");
        var managedCli = Path.Combine(RuntimeDirectory, "dsh", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        if (File.Exists(managedNode) && File.Exists(managedCli) && File.Exists(Path.Combine(RuntimeDirectory, "ready.json")))
            return NewInstallation(managedNode, managedCli, true);

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "runtime"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm")
        };
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var fallbackNode = candidates.Select(p => Path.Combine(p, "node.exe")).FirstOrDefault(File.Exists);
        foreach (var directory in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var cli = Path.Combine(directory, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
            if (!File.Exists(cli)) continue;
            var node = Path.Combine(directory, "node.exe");
            if (!File.Exists(node) && fallbackNode is not null) node = fallbackNode;
            if (File.Exists(node)) return NewInstallation(node, cli, false);
        }
        return null;
    }

    public async Task<DshStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var installation = FindInstallation();
        var pid = GetListenerPid(Config.Port);
        var healthy = pid > 0 && await IsHealthyAsync(Config.Port, cancellationToken);
        var state = Load<RuntimeState>(statePath);
        var managed = pid > 0 && VerifyProcess(pid, state);
        var dshState = installation is null ? DshState.NotInstalled :
            pid == 0 ? DshState.Stopped : healthy && managed ? DshState.Running : healthy ? DshState.ExternalService : DshState.Error;
        var message = dshState switch
        {
            DshState.NotInstalled => "尚未安装官方 DSH CLI",
            DshState.Stopped => "DSH Web 已停止",
            DshState.Running => "DSH Web 正在运行",
            DshState.ExternalService => $"端口 {Config.Port} 正由其他服务使用",
            DshState.Error => "检测到端口占用，但服务没有正常响应",
            _ => "正在检测 DSH Web 状态"
        };
        return new DshStatus(dshState, message, pid, installation, CurrentUrl, healthy, managed);
    }

    public async Task<OperationResult> StartAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Config.Workspace) || !Directory.Exists(Config.Workspace))
            return OperationResult.Fail("请先选择有效的工作区。");
        if (Config.Port is < 1024 or > 65535) return OperationResult.Fail("端口必须在 1024 到 65535 之间。");
        var current = await GetStatusAsync(cancellationToken);
        if (current.ListenerPid > 0)
        {
            if (current.Healthy) return OperationResult.Fail(current.Managed ? "DSH Web 已经在运行。" : $"端口 {Config.Port} 正由其他服务使用。");
            return OperationResult.Fail($"端口 {Config.Port} 已被 PID {current.ListenerPid} 占用，请更换端口。");
        }
        var installation = FindInstallation();
        if (installation is null) return OperationResult.Fail("没有找到官方 DSH CLI，请先安装 DSH。");
        Directory.CreateDirectory(LogDirectory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var stdout = Path.Combine(LogDirectory, $"dsh-web-{stamp}.stdout.log");
        var stderr = Path.Combine(LogDirectory, $"dsh-web-{stamp}.stderr.log");
        var command = $"\"{installation.NodePath}\" \"{installation.CliPath}\" web --host 127.0.0.1 --port {Config.Port}{(installation.Managed ? " --no-open" : "")} 1>>\"{stdout}\" 2>>\"{stderr}\"";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                Arguments = "/d /s /c \"" + command + "\"",
                WorkingDirectory = Path.GetFullPath(Config.Workspace),
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.Environment["NODE_OPTIONS"] = NodeOptions;
        if (!process.Start()) return OperationResult.Fail("DSH Web 进程未能启动。");
        progress?.Report("正在等待本地 Web 响应…");
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited) return OperationResult.Fail("DSH Web 启动进程已退出，请查看日志。");
            if (await IsHealthyAsync(Config.Port, cancellationToken))
            {
                var listenerPid = GetListenerPid(Config.Port);
                if (listenerPid > 0)
                {
                    var listener = Process.GetProcessById(listenerPid);
                    Save(new RuntimeState(listenerPid, listener.StartTime.ToUniversalTime().Ticks, Path.GetFullPath(Config.Workspace), installation.NodePath, installation.CliPath, Config.Port, stdout, stderr), statePath);
                    return OperationResult.Success("DSH Web 已启动。");
                }
            }
            await Task.Delay(300, cancellationToken);
        }
        try { if (!process.HasExited) process.Kill(); } catch { }
        return OperationResult.Fail("DSH Web 在 45 秒内没有响应，请查看日志。");
    }

    public async Task<OperationResult> InstallOrUpdateManagedRuntimeAsync(bool updateOnly, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "Install-DshRuntime.ps1");
        if (!File.Exists(script)) return OperationResult.Fail("安装组件缺失，请重新下载启动器。");
        var arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{script}\" -RuntimeRoot \"{RuntimeDirectory}\" -DshVersion latest{(updateOnly ? " -UpdateOnly" : string.Empty)}";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        if (!process.Start()) return OperationResult.Fail("安装进程未能启动。");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data)) return;
            output.AppendLine(args.Data);
            progress?.Report(args.Data);
        };
        process.ErrorDataReceived += (_, args) => { if (!string.IsNullOrWhiteSpace(args.Data)) output.AppendLine(args.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) return OperationResult.Fail((updateOnly ? "DSH 更新失败：" : "DSH 安装失败：") + LastUsefulLine(output.ToString()));
        return OperationResult.Success(updateOnly ? "DSH 已更新完成。" : "DSH 安装完成。");
    }

    public async Task<string> GetLatestDshVersionAsync(CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DSH-Web-Launcher/2.0.0");
        var json = await client.GetStringAsync("https://registry.npmjs.org/@deepseek-ai%2Fdsh/latest", cancellationToken);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("version", out var version) || string.IsNullOrWhiteSpace(version.GetString()))
            throw new InvalidDataException("npm registry did not return a version.");
        return version.GetString()!;
    }

    public OperationResult Stop()
    {
        try
        {
            var state = Load<RuntimeState>(statePath);
            var pid = GetListenerPid(Config.Port);
            if (pid == 0) { SafeDelete(statePath); return OperationResult.Success("DSH Web 已停止。"); }
            if (!VerifyProcess(pid, state)) return OperationResult.Fail("该进程不是本程序启动并验证过的 DSH Web，因此拒绝停止。");
            using var process = Process.GetProcessById(pid);
            process.Kill();
            if (!process.WaitForExit(10000)) return OperationResult.Fail("进程没有在 10 秒内停止。");
            SafeDelete(statePath);
            return OperationResult.Success("DSH Web 已停止。");
        }
        catch (ArgumentException) { SafeDelete(statePath); return OperationResult.Success("DSH Web 已停止。"); }
        catch (Exception ex) { return OperationResult.Fail("停止失败：" + ex.Message); }
    }

    public async Task<LauncherUpdateInfo> CheckLauncherUpdateAsync(CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DSH-Web-Launcher", LauncherVersion));
        using var response = await client.GetAsync("https://api.github.com/repos/LVSUGARS/dsh-web-launcher/releases/latest", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var latest = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? LauncherVersion;
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        var asset = assets.FirstOrDefault(x => x.GetProperty("name").GetString()?.EndsWith("win-x64.zip", StringComparison.OrdinalIgnoreCase) == true);
        var assetName = asset.ValueKind == JsonValueKind.Undefined ? null : asset.GetProperty("name").GetString();
        var assetUrl = asset.ValueKind == JsonValueKind.Undefined ? null : asset.GetProperty("browser_download_url").GetString();
        var checksum = assets.FirstOrDefault(x => x.GetProperty("name").GetString()?.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) == true);
        var checksumUrl = checksum.ValueKind == JsonValueKind.Undefined ? null : checksum.GetProperty("browser_download_url").GetString();
        return new LauncherUpdateInfo(CompareVersions(LauncherVersion, latest) < 0, LauncherVersion, latest, root.GetProperty("name").GetString() ?? latest, root.GetProperty("body").GetString() ?? string.Empty, root.GetProperty("html_url").GetString() ?? "https://github.com/LVSUGARS/dsh-web-launcher/releases", assetUrl, assetName, checksumUrl);
    }

    public async Task<string> DownloadLauncherUpdateAsync(LauncherUpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.PackageUrl) || string.IsNullOrWhiteSpace(update.PackageName) || string.IsNullOrWhiteSpace(update.ChecksumUrl))
            throw new InvalidOperationException("该更新没有提供完整的 x64 安装包或 SHA-256 校验文件。");
        var stage = Path.Combine(Path.GetTempPath(), "dsh-web-launcher-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var package = Path.Combine(stage, update.PackageName);
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DSH-Web-Launcher", LauncherVersion));
        await using (var source = await client.GetStreamAsync(update.PackageUrl, cancellationToken))
        await using (var target = File.Create(package))
        {
            var buffer = new byte[81920];
            long written = 0;
            var length = (await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, update.PackageUrl), cancellationToken)).Content.Headers.ContentLength;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;
                if (length is > 0) progress?.Report((double)written / length.Value);
            }
        }
        var checksumText = await client.GetStringAsync(update.ChecksumUrl, cancellationToken);
        var expected = checksumText.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(expected) || expected.Length != 64) throw new InvalidDataException("更新校验文件格式无效。");
        await using var file = File.OpenRead(package);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包 SHA-256 校验失败，已拒绝安装。");
        progress?.Report(1);
        return package;
    }

    public async Task<string> GetNodeVersionAsync(CancellationToken cancellationToken = default)
    {
        var installation = FindInstallation();
        if (installation is null) return "未检测到";
        using var process = new Process { StartInfo = new ProcessStartInfo(installation.NodePath, "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true } };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0 ? output.Trim() : "无法读取";
    }

    public static int CompareVersions(string left, string right)
    {
        if (Version.TryParse(left.Split('-', 2)[0], out var a) && Version.TryParse(right.Split('-', 2)[0], out var b)) return a.CompareTo(b);
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private AppConfig LoadConfig()
    {
        var config = Load<AppConfig>(configPath) ?? new AppConfig();
        config.Version = 2;
        if (config.Port == 0) config.Port = 3080;
        if (string.IsNullOrWhiteSpace(config.Theme)) config.Theme = "system";
        if (string.IsNullOrWhiteSpace(config.CloseBehavior)) config.CloseBehavior = "ask";
        return config;
    }

    private DshInstallation NewInstallation(string node, string cli, bool managed)
    {
        var packagePath = Path.Combine(Directory.GetParent(Directory.GetParent(cli)!.FullName)!.FullName, "package.json");
        var version = "未知";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packagePath));
            if (doc.RootElement.TryGetProperty("version", out var versionElement)) version = versionElement.GetString() ?? version;
        }
        catch { }
        return new DshInstallation(Path.GetFullPath(node), Path.GetFullPath(cli), version, managed);
    }

    private bool VerifyProcess(int pid, RuntimeState? state)
    {
        if (state is null || state.Pid != pid || !File.Exists(state.CliPath)) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (Math.Abs(process.StartTime.ToUniversalTime().Ticks - state.StartUtcTicks) > TimeSpan.TicksPerSecond) return false;
            var commandLine = GetCommandLine(pid);
            return commandLine.Contains(state.CliPath, StringComparison.OrdinalIgnoreCase) && commandLine.Contains(" web", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string GetCommandLine(int pid)
    {
        using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId={pid}");
        foreach (ManagementObject item in searcher.Get()) return Convert.ToString(item["CommandLine"]) ?? string.Empty;
        return string.Empty;
    }

    private static int GetListenerPid(int port)
    {
        using var process = Process.Start(new ProcessStartInfo("netstat.exe", "-ano -p tcp") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
        if (process is null) return 0;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5 && parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase) && parts[1].EndsWith(":" + port) && parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[4], out var pid)) return pid;
        }
        return 0;
    }

    private static async Task<bool> IsHealthyAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/", cancellationToken);
            return (int)response.StatusCode is >= 200 and < 500;
        }
        catch { return false; }
    }

    private static T? Load<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : null; }
        catch { return null; }
    }

    private static void Save<T>(T value, string path) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
    private static void SafeDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static string LastUsefulLine(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "请查看日志目录。" : lines[^1];
    }
}
