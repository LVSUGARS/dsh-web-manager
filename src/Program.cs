using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("DSH Web Manager")]
[assembly: System.Reflection.AssemblyProduct("DSH Web Manager")]
[assembly: System.Reflection.AssemblyCompany("DSH Web Manager Community Build")]
[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.2.0.0")]

namespace DSHWebManager
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var engine = new DshEngine();
            if (args.Contains("--start")) { Environment.Exit(engine.Start().Ok ? 0 : 1); }
            if (args.Contains("--stop")) { Environment.Exit(engine.Stop().Ok ? 0 : 1); }
            if (args.Contains("--install-dsh")) { Environment.Exit(engine.InstallOrUpdateManagedRuntime(false, null).Ok ? 0 : 1); }
            if (args.Contains("--update-dsh")) { Environment.Exit(engine.InstallOrUpdateManagedRuntime(true, null).Ok ? 0 : 1); }
            if (args.Contains("--self-test")) { Environment.Exit(engine.SelfTest() ? 0 : 1); }
            Application.Run(new MainForm(engine));
        }
    }

    internal sealed class AppConfig
    {
        public string Workspace { get; set; }
        public int Port { get; set; }
        public AppConfig() { Port = 3080; Workspace = ""; }
    }

    internal sealed class RuntimeState
    {
        public int Pid { get; set; }
        public long StartUtcTicks { get; set; }
        public string Workspace { get; set; }
        public string Node { get; set; }
        public string Cli { get; set; }
        public int Port { get; set; }
        public string StdoutLog { get; set; }
        public string StderrLog { get; set; }
    }

    internal sealed class Installation
    {
        public string Node { get; set; }
        public string Cli { get; set; }
        public string Version { get; set; }
        public bool Managed { get; set; }
    }

    internal sealed class OperationResult
    {
        public bool Ok { get; private set; }
        public string Message { get; private set; }
        public static OperationResult Success(string message) { return new OperationResult { Ok = true, Message = message }; }
        public static OperationResult Fail(string message) { return new OperationResult { Ok = false, Message = message }; }
    }

    internal sealed class StatusInfo
    {
        public bool Healthy { get; set; }
        public bool Managed { get; set; }
        public int ListenerPid { get; set; }
        public string Text { get; set; }
    }

    internal sealed class DshEngine
    {
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        public readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSHWebManager");
        public string LogDir { get { return Path.Combine(DataDir, "logs"); } }
        public string RuntimeDir { get { return Path.Combine(DataDir, "runtime"); } }
        public string RuntimeInstallLog { get { return Path.Combine(LogDir, "runtime-install.log"); } }
        private string ConfigPath { get { return Path.Combine(DataDir, "config.json"); } }
        private string StatePath { get { return Path.Combine(DataDir, "state.json"); } }
        public string StartupLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DSH Web Manager.lnk"); } }
        public AppConfig Config { get; private set; }

        public DshEngine()
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(LogDir);
            Config = Load<AppConfig>(ConfigPath) ?? new AppConfig();
        }

        public void SaveConfig()
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(ConfigPath, json.Serialize(Config), new UTF8Encoding(false));
        }

        public bool SelfTest()
        {
            try
            {
                SaveConfig();
                int progress; string stage;
                return Directory.Exists(DataDir) && FindInstallation() != null &&
                       CompareVersions("0.1.0-rc.6", "0.1.0-rc.7") < 0 &&
                       CompareVersions("0.1.0-rc.7", "0.1.0") < 0 &&
                       CompareVersions("1.0.0", "1.0.0") == 0 &&
                       ProgressEvent("DSH_PROGRESS:75:正在验证 DSH", out progress, out stage) && progress == 75 && stage == "正在验证 DSH";
            }
            catch { return false; }
        }

        public Installation FindInstallation()
        {
            var managedNode = Path.Combine(RuntimeDir, "node", "node.exe");
            var managedCli = Path.Combine(RuntimeDir, "dsh", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
            var managedReady = Path.Combine(RuntimeDir, "ready.json");
            if (File.Exists(managedNode) && File.Exists(managedCli) && File.Exists(managedReady))
                return NewInstallation(managedNode, managedCli, true);

            var candidates = new List<string>();
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            candidates.Add(Path.Combine(appDir, "runtime", "node"));
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            candidates.AddRange(path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().Trim('"')));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm"));
            var winGet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
            try
            {
                if (Directory.Exists(winGet))
                    candidates.AddRange(Directory.EnumerateDirectories(winGet, "OpenJS.NodeJS*").SelectMany(p => SafeDirectories(p, "node-v*-win-x64")));
            }
            catch { }

            string fallbackNode = candidates.Select(p => Path.Combine(p, "node.exe")).FirstOrDefault(File.Exists);
            foreach (var dir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var cli = Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                if (!File.Exists(cli)) continue;
                var node = Path.Combine(dir, "node.exe");
                if (!File.Exists(node)) node = fallbackNode;
                if (File.Exists(node)) return NewInstallation(node, cli, false);
            }
            return null;
        }

        private Installation NewInstallation(string node, string cli, bool managed)
        {
            var packagePath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(cli)), "package.json");
            var version = "未知";
            try
            {
                var package = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(packagePath, Encoding.UTF8));
                object value;
                if (package.TryGetValue("version", out value)) version = Convert.ToString(value);
            }
            catch { }
            return new Installation { Node = Path.GetFullPath(node), Cli = Path.GetFullPath(cli), Version = version, Managed = managed };
        }

        public string GetLatestDshVersion()
        {
            var request = (HttpWebRequest)WebRequest.Create("https://registry.npmjs.org/@deepseek-ai%2Fdsh/latest");
            request.Timeout = 15000;
            request.UserAgent = "DSH-Web-Manager/1.2.0";
            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                var metadata = json.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                object value;
                if (!metadata.TryGetValue("version", out value) || string.IsNullOrWhiteSpace(Convert.ToString(value)))
                    throw new InvalidDataException("npm registry did not return a version.");
                return Convert.ToString(value);
            }
        }

        public OperationResult InstallOrUpdateManagedRuntime(bool updateOnly, Action<int, string> progress)
        {
            var stoppedForUpdate = false;
            try
            {
                var script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Install-DshRuntime.ps1");
                if (!File.Exists(script)) return OperationResult.Fail("安装组件缺失，请重新安装 DSH Web Manager。\r\n\r\n缺少：" + script);
                var wasRunning = GetStatus().Managed;
                if (updateOnly && wasRunning)
                {
                    Report(progress, 12, "正在停止当前 DSH Web...");
                    var stopped = Stop();
                    if (!stopped.Ok) return stopped;
                    stoppedForUpdate = true;
                }
                Report(progress, updateOnly ? 25 : 10, updateOnly ? "正在准备新版本..." : "正在准备安装...");
                var args = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File " + Quote(script) +
                           " -RuntimeRoot " + Quote(RuntimeDir) + (updateOnly ? " -UpdateOnly" : "");
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = args,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var process = Process.Start(psi))
                {
                    var lastOutput = "";
                    var lastError = "";
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (string.IsNullOrWhiteSpace(e.Data)) return;
                        lastOutput = e.Data;
                        int percent; string stage;
                        if (ProgressEvent(e.Data, out percent, out stage)) Report(progress, percent, stage);
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data)) lastError = e.Data;
                    };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                        return FailedInstall("DSH 安装失败。\r\n\r\n" + LastUsefulLine(lastError + "\r\n" + lastOutput) + "\r\n\r\n可在日志目录查看 runtime-install.log。", stoppedForUpdate);
                }
                var installation = FindInstallation();
                if (installation == null || !installation.Managed) return FailedInstall("安装已结束，但没有检测到受管 DSH CLI。请查看安装日志。", stoppedForUpdate);
                if (updateOnly && wasRunning)
                {
                    Report(progress, 95, "正在重启 DSH Web...");
                    var started = Start();
                    if (!started.Ok) return OperationResult.Fail("DSH 已更新，但 Web 未能重新启动：\r\n" + started.Message);
                }
                Report(progress, 100, updateOnly ? "DSH 已更新完成" : "DSH 安装完成");
                return OperationResult.Success(updateOnly ? "DSH 已更新到 " + installation.Version + "。" : "DSH " + installation.Version + " 安装完成。");
            }
            catch (Exception ex) { return FailedInstall((updateOnly ? "更新" : "安装") + "失败：" + ex.Message, stoppedForUpdate); }
        }

        private OperationResult FailedInstall(string message, bool restartOldRuntime)
        {
            if (!restartOldRuntime) return OperationResult.Fail(message);
            var restarted = Start();
            return OperationResult.Fail(message + (restarted.Ok
                ? "\r\n\r\n已自动恢复原版本 DSH Web。"
                : "\r\n\r\n原版本 DSH Web 自动恢复失败：" + restarted.Message));
        }

        private static string LastUsefulLine(string text)
        {
            var lines = (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? "没有可用的错误详情。" : lines[lines.Length - 1].Trim();
        }

        private static void Report(Action<int, string> progress, int percent, string stage)
        {
            if (progress != null) progress(percent, stage);
        }

        internal static bool ProgressEvent(string line, out int percent, out string stage)
        {
            percent = 0; stage = "";
            const string prefix = "DSH_PROGRESS:";
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var parts = line.Substring(prefix.Length).Split(new[] { ':' }, 2);
            return parts.Length == 2 && int.TryParse(parts[0], out percent) && percent >= 0 && percent <= 100 && (stage = parts[1]).Length > 0;
        }

        public static int CompareVersions(string left, string right)
        {
            var leftParts = (left ?? "").Split(new[] { '-' }, 2);
            var rightParts = (right ?? "").Split(new[] { '-' }, 2);
            Version a, b;
            if (!Version.TryParse(leftParts[0], out a) || !Version.TryParse(rightParts[0], out b))
                return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            var core = a.CompareTo(b);
            if (core != 0) return core;
            var aPre = leftParts.Length == 2; var bPre = rightParts.Length == 2;
            if (aPre != bPre) return aPre ? -1 : 1;
            if (!aPre) return 0;
            var ai = leftParts[1].Split('.'); var bi = rightParts[1].Split('.');
            for (var i = 0; i < Math.Max(ai.Length, bi.Length); i++)
            {
                if (i >= ai.Length) return -1;
                if (i >= bi.Length) return 1;
                int an, bn; var aNumber = int.TryParse(ai[i], out an); var bNumber = int.TryParse(bi[i], out bn);
                if (aNumber && bNumber && an != bn) return an.CompareTo(bn);
                if (aNumber != bNumber) return aNumber ? -1 : 1;
                var part = string.Compare(ai[i], bi[i], StringComparison.OrdinalIgnoreCase);
                if (part != 0) return part;
            }
            return 0;
        }

        private static IEnumerable<string> SafeDirectories(string root, string pattern)
        {
            try { return Directory.EnumerateDirectories(root, pattern).ToArray(); }
            catch { return new string[0]; }
        }

        public StatusInfo GetStatus()
        {
            var pid = GetListenerPid(Config.Port);
            var healthy = pid > 0 && IsHealthy(Config.Port);
            var state = Load<RuntimeState>(StatePath);
            var managed = pid > 0 && VerifyProcess(pid, state);
            string text;
            if (healthy && managed) text = "运行中";
            else if (pid > 0) text = healthy ? "检测到外部 DSH Web" : "端口被其他程序占用";
            else text = "已停止";
            return new StatusInfo { Healthy = healthy, Managed = managed, ListenerPid = pid, Text = text };
        }

        public OperationResult Start()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Config.Workspace) || !Directory.Exists(Config.Workspace))
                    return OperationResult.Fail("请先选择有效的工作区。\r\n\r\n工作区是你希望 DSH 打开的项目文件夹。");
                if (Config.Port < 1024 || Config.Port > 65535)
                    return OperationResult.Fail("端口必须在 1024 到 65535 之间。");
                var current = GetStatus();
                if (current.ListenerPid > 0)
                {
                    if (current.Healthy)
                    {
                        var existingInstallation = FindInstallation();
                        if (existingInstallation != null && AdoptRunningDsh(current.ListenerPid, existingInstallation))
                            return OperationResult.Success("已识别并接管正在运行的 DSH Web。");
                        return OperationResult.Fail("端口 " + Config.Port + " 上有健康的 Web 服务，但无法验证为当前 DSH 安装，因此拒绝接管。");
                    }
                    return OperationResult.Fail("端口 " + Config.Port + " 已被 PID " + current.ListenerPid + " 占用，请更换端口。");
                }

                var installation = FindInstallation();
                if (installation == null)
                    return OperationResult.Fail("没有找到官方 DSH CLI。\r\n\r\n请先安装 DSH，再重新打开本程序。程序不会读取或复制其他人的账号、凭据或会话数据。");

                Directory.CreateDirectory(LogDir);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var stdout = Path.Combine(LogDir, "dsh-web-" + stamp + ".stdout.log");
                var stderr = Path.Combine(LogDir, "dsh-web-" + stamp + ".stderr.log");
                var command = Quote(installation.Node) + " " + Quote(installation.Cli) +
                              " web --host 127.0.0.1 --port " + Config.Port + (installation.Managed ? " --no-open" : "") +
                              " 1>>" + Quote(stdout) + " 2>>" + Quote(stderr);
                var psi = new ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                    Arguments = "/d /s /c \"" + command + "\"",
                    WorkingDirectory = Path.GetFullPath(Config.Workspace),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var process = new Process { StartInfo = psi };
                if (!process.Start()) return OperationResult.Fail("DSH Web 进程未能启动。");
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (DateTime.UtcNow < deadline)
                {
                    if (IsHealthy(Config.Port))
                    {
                        var listenerPid = GetListenerPid(Config.Port);
                        var listener = Process.GetProcessById(listenerPid);
                        var state = new RuntimeState
                        {
                            Pid = listenerPid,
                            StartUtcTicks = listener.StartTime.ToUniversalTime().Ticks,
                            Workspace = Path.GetFullPath(Config.Workspace),
                            Node = installation.Node,
                            Cli = installation.Cli,
                            Port = Config.Port,
                            StdoutLog = stdout,
                            StderrLog = stderr
                        };
                        File.WriteAllText(StatePath, json.Serialize(state), new UTF8Encoding(false));
                        var stableUntil = DateTime.UtcNow.AddSeconds(3);
                        while (DateTime.UtcNow < stableUntil)
                        {
                            if (GetListenerPid(Config.Port) != listenerPid || !IsHealthy(Config.Port))
                            {
                                SafeDelete(StatePath);
                                return OperationResult.Fail("DSH Web 启动后退出。可能已有另一个 DSH Web 实例占用了用户账本，请先停止旧实例并查看日志。");
                            }
                            Thread.Sleep(300);
                        }
                        return OperationResult.Success("DSH Web 已启动。");
                    }
                    Thread.Sleep(300);
                }
                if (!process.HasExited) process.Kill();
                SafeDelete(StatePath);
                return OperationResult.Fail("DSH Web 在 45 秒内没有响应，已停止启动进程。请查看日志。");
            }
            catch (Exception ex) { return OperationResult.Fail("启动失败：" + ex.Message); }
        }

        public OperationResult Stop()
        {
            try
            {
                var state = Load<RuntimeState>(StatePath);
                var pid = GetListenerPid(Config.Port);
                if (pid == 0) { SafeDelete(StatePath); return OperationResult.Success("DSH Web 已停止。"); }
                if (!VerifyProcess(pid, state))
                    return OperationResult.Fail("PID " + pid + " 不是本程序启动并验证过的 DSH Web，因此拒绝停止。该保护可避免误杀其他 Node 服务。");
                var process = Process.GetProcessById(pid);
                process.Kill();
                if (!process.WaitForExit(10000)) return OperationResult.Fail("进程没有在 10 秒内停止。");
                SafeDelete(StatePath);
                return OperationResult.Success("DSH Web 已停止。");
            }
            catch (ArgumentException) { SafeDelete(StatePath); return OperationResult.Success("DSH Web 已停止。"); }
            catch (Exception ex) { return OperationResult.Fail("停止失败：" + ex.Message); }
        }

        public bool IsAutostartEnabled() { return File.Exists(StartupLink); }

        private bool AdoptRunningDsh(int pid, Installation installation)
        {
            try
            {
                var commandLine = GetCommandLine(pid);
                if (commandLine.IndexOf(installation.Cli, StringComparison.OrdinalIgnoreCase) < 0 ||
                    commandLine.IndexOf(" web", StringComparison.OrdinalIgnoreCase) < 0) return false;
                var process = Process.GetProcessById(pid);
                var state = new RuntimeState
                {
                    Pid = pid,
                    StartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    Workspace = Path.GetFullPath(Config.Workspace),
                    Node = installation.Node,
                    Cli = installation.Cli,
                    Port = Config.Port,
                    StdoutLog = "",
                    StderrLog = ""
                };
                File.WriteAllText(StatePath, json.Serialize(state), new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }

        public OperationResult SetAutostart(bool enabled)
        {
            try
            {
                if (!enabled) { SafeDelete(StartupLink); return OperationResult.Success("已关闭开机启动。"); }
                SaveConfig();
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic shortcut = shell.CreateShortcut(StartupLink);
                shortcut.TargetPath = Application.ExecutablePath;
                shortcut.Arguments = "--start";
                shortcut.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                shortcut.Description = "Start DSH Web at user sign-in";
                shortcut.Save();
                return OperationResult.Success("已开启开机启动。");
            }
            catch (Exception ex) { return OperationResult.Fail("修改开机启动失败：" + ex.Message); }
        }

        public string CurrentUrl { get { return "http://127.0.0.1:" + Config.Port; } }

        private bool VerifyProcess(int pid, RuntimeState state)
        {
            if (state == null || state.Pid != pid || !File.Exists(state.Cli)) return false;
            try
            {
                var process = Process.GetProcessById(pid);
                if (Math.Abs(process.StartTime.ToUniversalTime().Ticks - state.StartUtcTicks) > TimeSpan.TicksPerSecond) return false;
                var commandLine = GetCommandLine(pid);
                return commandLine.IndexOf(state.Cli, StringComparison.OrdinalIgnoreCase) >= 0 &&
                       commandLine.IndexOf(" web", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private static string GetCommandLine(int pid)
        {
            using (var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid))
                foreach (ManagementObject item in searcher.Get()) return Convert.ToString(item["CommandLine"]) ?? "";
            return "";
        }

        private static int GetListenerPid(int port)
        {
            var psi = new ProcessStartInfo("netstat.exe", "-ano -p tcp") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using (var process = Process.Start(psi))
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5 && parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase) &&
                        parts[1].EndsWith(":" + port) && parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                    { int pid; if (int.TryParse(parts[4], out pid)) return pid; }
                }
            }
            return 0;
        }

        private static bool IsHealthy(int port)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/");
                request.Timeout = 2500; request.AllowAutoRedirect = false;
                using (var response = (HttpWebResponse)request.GetResponse()) return (int)response.StatusCode >= 200 && (int)response.StatusCode < 500;
            }
            catch { return false; }
        }

        private T Load<T>(string path) where T : class
        {
            try { return File.Exists(path) ? json.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)) : null; }
            catch { return null; }
        }

        private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
        private static void SafeDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }

    internal sealed class MainForm : Form
    {
        private readonly DshEngine engine;
        private readonly Label status = new Label();
        private readonly Label detail = new Label();
        private readonly Panel dot = new Panel();
        private readonly TextBox workspace = new TextBox();
        private readonly NumericUpDown port = new NumericUpDown();
        private readonly Button start = new Button();
        private readonly Button stop = new Button();
        private readonly Button open = new Button();
        private readonly Button browse = new Button();
        private readonly Button checkUpdate = new Button();
        private readonly Button updateDsh = new Button();
        private readonly Button installDsh = new Button();
        private readonly Label versionInfo = new Label();
        private readonly ProgressBar updateProgress = new ProgressBar();
        private readonly Panel installPanel = new Panel();
        private readonly CheckBox autostart = new CheckBox();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private bool updating;

        public MainForm(DshEngine engine)
        {
            this.engine = engine;
            Text = "DSH Web Manager";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            ClientSize = new Size(620, 456);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Color.FromArgb(245, 247, 250);
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BuildUi();
            Load += async delegate { RefreshStatus(); if (engine.FindInstallation() != null) await CheckUpdates(false); };
            FormClosed += delegate { timer.Stop(); };
            timer.Interval = 3000;
            timer.Tick += delegate { RefreshStatus(); };
            timer.Start();
        }

        private void BuildUi()
        {
            var title = NewLabel("DSH Web Manager", 28, 18, 560, 34, 20, FontStyle.Bold, Color.FromArgb(27, 31, 40));
            Controls.Add(title);
            Controls.Add(NewLabel("本地 DSH Web 控制台", 29, 51, 300, 20, 9.5f, FontStyle.Regular, Color.FromArgb(104, 111, 125)));

            var statusCard = new Panel { Location = new Point(28, 80), Size = new Size(564, 110), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(statusCard);
            dot.Location = new Point(21, 22); dot.Size = new Size(11, 11); statusCard.Controls.Add(dot);
            status.Location = new Point(43, 13); status.Size = new Size(330, 27); status.Font = new Font(Font.FontFamily, 11, FontStyle.Bold); statusCard.Controls.Add(status);
            detail.Location = new Point(43, 40); detail.Size = new Size(340, 22); detail.ForeColor = Color.FromArgb(92, 99, 114); statusCard.Controls.Add(detail);
            versionInfo.Location = new Point(20, 65); versionInfo.Size = new Size(395, 20); versionInfo.Font = new Font(Font.FontFamily, 8.75f); versionInfo.ForeColor = Color.FromArgb(104, 111, 125); statusCard.Controls.Add(versionInfo);
            updateProgress.Location = new Point(20, 88); updateProgress.Size = new Size(527, 8); updateProgress.Minimum = 0; updateProgress.Maximum = 100; updateProgress.Visible = false; statusCard.Controls.Add(updateProgress);
            SetupSecondaryButton(checkUpdate, "检查更新", 444, 15, 103, 29); checkUpdate.Click += async delegate { await CheckUpdates(true); }; statusCard.Controls.Add(checkUpdate);
            SetupSecondaryButton(updateDsh, "更新 DSH", 444, 50, 103, 29); updateDsh.Visible = false; statusCard.Controls.Add(updateDsh); updateDsh.Click += async delegate { await UpdateDsh(); };

            Controls.Add(NewLabel("工作区", 29, 210, 100, 22, 9.5f, FontStyle.Bold, Color.FromArgb(55, 61, 75)));
            workspace.Location = new Point(29, 234); workspace.Size = new Size(474, 30); workspace.Text = engine.Config.Workspace; Controls.Add(workspace);
            browse.Text = "选择..."; browse.Location = new Point(515, 233); browse.Size = new Size(76, 32); browse.Click += Browse_Click; Controls.Add(browse);

            Controls.Add(NewLabel("端口", 29, 282, 80, 22, 9.5f, FontStyle.Bold, Color.FromArgb(55, 61, 75)));
            port.Location = new Point(29, 306); port.Size = new Size(100, 30); port.Minimum = 1024; port.Maximum = 65535; port.Value = Math.Max(1024, Math.Min(65535, engine.Config.Port)); Controls.Add(port);
            autostart.Text = "登录 Windows 后自动启动 DSH Web"; autostart.Location = new Point(160, 306); autostart.Size = new Size(310, 28); autostart.Checked = engine.IsAutostartEnabled(); autostart.CheckedChanged += Autostart_CheckedChanged; Controls.Add(autostart);

            SetupButton(start, "启动", 29, 358, 106, Color.FromArgb(45, 99, 235), Color.White);
            SetupButton(stop, "停止", 145, 358, 106, Color.FromArgb(214, 65, 65), Color.White);
            SetupButton(open, "打开网页", 261, 358, 122, Color.FromArgb(235, 238, 244), Color.FromArgb(38, 44, 56));
            var logs = new Button(); SetupButton(logs, "日志", 393, 358, 91, Color.FromArgb(235, 238, 244), Color.FromArgb(38, 44, 56));
            var folder = new Button(); SetupButton(folder, "工作区", 494, 358, 97, Color.FromArgb(235, 238, 244), Color.FromArgb(38, 44, 56));
            start.Click += async delegate { await RunOperation("正在启动...", engine.Start); };
            stop.Click += async delegate { await RunOperation("正在停止...", engine.Stop); };
            open.Click += delegate { Process.Start(engine.CurrentUrl); };
            logs.Click += delegate { Directory.CreateDirectory(engine.LogDir); Process.Start(engine.LogDir); };
            folder.Click += delegate { if (Directory.Exists(workspace.Text)) Process.Start(workspace.Text); };

            var hint = NewLabel("关闭窗口不会停止 DSH Web。程序不会删除你的 .dsh 会话或工作区。", 29, 420, 560, 22, 9, FontStyle.Regular, Color.FromArgb(112, 118, 132));
            Controls.Add(hint);

            BuildInstallPanel();
        }

        private void BuildInstallPanel()
        {
            installPanel.Location = new Point(0, 74); installPanel.Size = new Size(620, 364); installPanel.BackColor = BackColor;
            var whale = new PictureBox { Location = new Point(42, 32), Size = new Size(86, 86), SizeMode = PictureBoxSizeMode.Zoom, Image = Icon.ToBitmap() };
            installPanel.Controls.Add(whale);
            installPanel.Controls.Add(NewLabel("需要安装官方 DSH", 152, 37, 420, 32, 17, FontStyle.Bold, Color.FromArgb(28, 32, 42)));
            installPanel.Controls.Add(NewLabel("未检测到 DSH CLI。安装完成后会自动进入控制台。", 152, 74, 420, 26, 10, FontStyle.Regular, Color.FromArgb(80, 87, 102)));
            var installInfo = new Panel { Location = new Point(42, 140), Size = new Size(536, 74), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            installInfo.Controls.Add(NewLabel("官方 @deepseek-ai/dsh  ·  Node.js 校验下载  ·  约 350 MB 磁盘空间", 15, 12, 500, 24, 9.25f, FontStyle.Regular, Color.FromArgb(80, 87, 102)));
            installInfo.Controls.Add(NewLabel("安装通常需要 5-20 分钟。不会覆盖 .dsh 会话、账号或工作区。", 15, 39, 500, 24, 9.25f, FontStyle.Regular, Color.FromArgb(80, 87, 102)));
            installPanel.Controls.Add(installInfo);
            installDsh.Text = "安装官方 DSH"; installDsh.Location = new Point(42, 242); installDsh.Size = new Size(174, 42); installDsh.FlatStyle = FlatStyle.Flat; installDsh.FlatAppearance.BorderSize = 0; installDsh.BackColor = Color.FromArgb(45, 99, 235); installDsh.ForeColor = Color.White; installDsh.Cursor = Cursors.Hand;
            installDsh.Click += async delegate { await InstallDsh(); }; installPanel.Controls.Add(installDsh);
            var installLogs = new Button { Text = "查看安装日志", Location = new Point(228, 242), Size = new Size(138, 42), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(235, 238, 244), ForeColor = Color.FromArgb(38, 44, 56), Cursor = Cursors.Hand };
            installLogs.FlatAppearance.BorderColor = Color.FromArgb(217, 222, 231); installLogs.FlatAppearance.BorderSize = 1; installLogs.Click += delegate { Directory.CreateDirectory(engine.LogDir); Process.Start(engine.LogDir); }; installPanel.Controls.Add(installLogs);
            Controls.Add(installPanel); installPanel.BringToFront();
        }

        private async Task InstallDsh()
        {
            installDsh.Enabled = false; installDsh.Text = "正在安装..."; UseWaitCursor = true;
            var result = await Task.Run(() => engine.InstallOrUpdateManagedRuntime(false, null));
            UseWaitCursor = false; installDsh.Enabled = true; installDsh.Text = "安装官方 DSH"; RefreshStatus();
            if (!result.Ok) MessageBox.Show(this, result.Message, "DSH Web Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else await CheckUpdates(false);
        }

        private async Task CheckUpdates(bool showFailure)
        {
            var installation = engine.FindInstallation();
            if (installation == null) return;
            versionInfo.Text = "DSH " + installation.Version + "  ·  正在检查更新...";
            checkUpdate.Enabled = false;
            try
            {
                var latest = await Task.Run(() => engine.GetLatestDshVersion());
                var current = engine.FindInstallation();
                if (current == null) return;
                var currentText = current.Managed ? "受管安装" : "外部安装";
                var comparison = DshEngine.CompareVersions(current.Version, latest);
                if (comparison == 0)
                {
                    versionInfo.Text = "DSH " + current.Version + "  ·  已是最新  ·  " + currentText;
                    updateDsh.Visible = false;
                }
                else if (comparison < 0)
                {
                    versionInfo.Text = "DSH " + current.Version + "  →  " + latest + " 可更新  ·  " + currentText;
                    updateDsh.Visible = current.Managed;
                    updateDsh.Text = "更新到 " + latest;
                }
                else
                {
                    versionInfo.Text = "DSH " + current.Version + "  ·  高于官方 latest  ·  " + currentText;
                    updateDsh.Visible = false;
                }
            }
            catch (Exception ex)
            {
                versionInfo.Text = "DSH " + installation.Version + "  ·  更新检查失败";
                updateDsh.Visible = false;
                if (showFailure) MessageBox.Show(this, "无法检查 DSH 更新：" + ex.Message, "DSH Web Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { checkUpdate.Enabled = true; }
        }

        private async Task UpdateDsh()
        {
            updating = true; SetBusy(true); checkUpdate.Visible = false; updateDsh.Visible = false; SetUpdateProgress(5, "正在准备更新...");
            var result = await Task.Run(() => engine.InstallOrUpdateManagedRuntime(true, SetUpdateProgress));
            if (result.Ok) SetUpdateProgress(100, "DSH 已更新完成");
            updating = false; updateProgress.Visible = false; checkUpdate.Visible = true; SetBusy(false); checkUpdate.Enabled = true; updateDsh.Enabled = true; RefreshStatus();
            if (!result.Ok) MessageBox.Show(this, result.Message, "DSH Web Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else { MessageBox.Show(this, result.Message, "DSH Web Manager", MessageBoxButtons.OK, MessageBoxIcon.Information); await CheckUpdates(false); }
        }

        private void SetUpdateProgress(int percent, string phase)
        {
            if (InvokeRequired) { BeginInvoke(new Action<int, string>(SetUpdateProgress), percent, phase); return; }
            updateProgress.Value = Math.Max(updateProgress.Minimum, Math.Min(updateProgress.Maximum, percent));
            updateProgress.Visible = true;
            versionInfo.Text = phase + "  ·  " + percent + "%";
        }

        private async Task RunOperation(string busyText, Func<OperationResult> action)
        {
            SaveInputs(); SetBusy(true); status.Text = busyText;
            var result = await Task.Run(action);
            SetBusy(false); RefreshStatus();
            if (!result.Ok) MessageBox.Show(this, result.Message, "DSH Web Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void SaveInputs()
        {
            engine.Config.Workspace = workspace.Text.Trim(); engine.Config.Port = (int)port.Value; engine.SaveConfig();
        }

        private void Browse_Click(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择 DSH 工作区", SelectedPath = Directory.Exists(workspace.Text) ? workspace.Text : "" })
                if (dialog.ShowDialog(this) == DialogResult.OK) { workspace.Text = dialog.SelectedPath; SaveInputs(); }
        }

        private void Autostart_CheckedChanged(object sender, EventArgs e)
        {
            SaveInputs(); var result = engine.SetAutostart(autostart.Checked);
            if (!result.Ok) { MessageBox.Show(this, result.Message, "DSH Web Manager", MessageBoxButtons.OK, MessageBoxIcon.Error); autostart.CheckedChanged -= Autostart_CheckedChanged; autostart.Checked = engine.IsAutostartEnabled(); autostart.CheckedChanged += Autostart_CheckedChanged; }
        }

        private void RefreshStatus()
        {
            if (updating) return;
            var installation = engine.FindInstallation();
            installPanel.Visible = installation == null;
            if (installation == null) { timer.Stop(); return; }
            if (!timer.Enabled) timer.Start();
            if (string.IsNullOrEmpty(versionInfo.Text)) versionInfo.Text = "DSH " + installation.Version + (installation.Managed ? "  ·  受管安装" : "  ·  外部安装");
            var s = engine.GetStatus(); status.Text = s.Text;
            if (s.Healthy && s.Managed) { dot.BackColor = Color.FromArgb(34, 165, 91); status.ForeColor = Color.FromArgb(25, 126, 67); detail.Text = engine.CurrentUrl + "  |  PID " + s.ListenerPid; }
            else if (s.ListenerPid > 0) { dot.BackColor = Color.FromArgb(220, 65, 65); status.ForeColor = Color.FromArgb(180, 45, 45); detail.Text = "端口 " + engine.Config.Port + "  |  PID " + s.ListenerPid; }
            else { dot.BackColor = Color.FromArgb(148, 154, 168); status.ForeColor = Color.FromArgb(82, 89, 105); detail.Text = "服务未运行"; }
            start.Enabled = s.ListenerPid == 0; stop.Enabled = s.Managed; open.Enabled = s.Healthy;
        }

        private void SetBusy(bool busy) { UseWaitCursor = busy; start.Enabled = stop.Enabled = open.Enabled = browse.Enabled = checkUpdate.Enabled = updateDsh.Enabled = !busy; }
        private void SetupButton(Button b, string text, int x, int y, int width, Color back, Color fore) { b.Text = text; b.Location = new Point(x, y); b.Size = new Size(width, 42); b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = fore == Color.White ? 0 : 1; b.FlatAppearance.BorderColor = Color.FromArgb(217, 222, 231); b.BackColor = back; b.ForeColor = fore; b.Cursor = Cursors.Hand; Controls.Add(b); }
        private static void SetupSecondaryButton(Button b, string text, int x, int y, int width, int height) { b.Text = text; b.Location = new Point(x, y); b.Size = new Size(width, height); b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 1; b.FlatAppearance.BorderColor = Color.FromArgb(217, 222, 231); b.BackColor = Color.White; b.ForeColor = Color.FromArgb(48, 55, 68); b.Cursor = Cursors.Hand; }
        private static Label NewLabel(string text, int x, int y, int w, int h, float size, FontStyle style, Color color) { return new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h), Font = new Font("Microsoft YaHei UI", size, style), ForeColor = color }; }
    }
}
