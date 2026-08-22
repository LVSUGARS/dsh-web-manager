using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("DSH Web Launcher")]
[assembly: System.Reflection.AssemblyProduct("DSH Web Launcher")]
[assembly: System.Reflection.AssemblyCompany("LVSUGARS")]
[assembly: System.Reflection.AssemblyVersion("1.4.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.4.0.0")]

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
        public string Language { get; set; }
        public string Theme { get; set; }
        public AppConfig() { Port = 3080; Workspace = ""; Language = "zh"; Theme = "dark"; }
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
        private const string DshNodeOptions = "--max-old-space-size=8192";
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        public readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSHWebManager");
        public string LogDir { get { return Path.Combine(DataDir, "logs"); } }
        public string RuntimeDir { get { return Path.Combine(DataDir, "runtime"); } }
        public string RuntimeInstallLog { get { return Path.Combine(LogDir, "runtime-install.log"); } }
        private string ConfigPath { get { return Path.Combine(DataDir, "config.json"); } }
        private string StatePath { get { return Path.Combine(DataDir, "state.json"); } }
        public string StartupLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DSH Web 启动器.lnk"); } }
        private string LegacyStartupLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "DSH Web Manager.lnk"); } }
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
            request.UserAgent = "DSH-Web-Launcher/1.4.0";
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
                if (!File.Exists(script)) return OperationResult.Fail("安装组件缺失，请重新安装 DSH Web 启动器。\r\n\r\n缺少：" + script);
                var targetVersion = "latest";
                if (updateOnly)
                {
                    Report(progress, 5, "正在检查官方版本...");
                    var current = FindInstallation();
                    if (current == null || !current.Managed) return OperationResult.Fail("仅能更新由 DSH Web 启动器安装的 DSH。");
                    targetVersion = GetLatestDshVersion();
                    if (CompareVersions(current.Version, targetVersion) >= 0)
                    {
                        Report(progress, 100, "DSH 已是最新版本");
                        return OperationResult.Success("DSH " + current.Version + " 已是官方最新版本。");
                    }
                }
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
                           " -RuntimeRoot " + Quote(RuntimeDir) + " -DshVersion " + Quote(targetVersion) + (updateOnly ? " -UpdateOnly" : "");
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
                psi.EnvironmentVariables["NODE_OPTIONS"] = DshNodeOptions;
                var process = new Process { StartInfo = psi };
                if (!process.Start()) return OperationResult.Fail("DSH Web 进程未能启动。");
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (DateTime.UtcNow < deadline)
                {
                    if (process.HasExited)
                    {
                        SafeDelete(StatePath);
                        return OperationResult.Fail("DSH Web 启动进程已退出。\r\n\r\n" + StartupFailure(stderr));
                    }
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
                return OperationResult.Fail("DSH Web 在 45 秒内没有响应，已停止启动进程。\r\n\r\n" + StartupFailure(stderr));
            }
            catch (Exception ex) { return OperationResult.Fail("启动失败：" + ex.Message); }
        }

        private static string StartupFailure(string stderr)
        {
            try
            {
                var text = File.Exists(stderr) ? File.ReadAllText(stderr) : "";
                if (text.IndexOf("out of memory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("heap limit", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "Node 内存不足：当前工作区包含过大的历史会话。请先使用 /compact 或新建会话后再启动。";
                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                return lines.Length == 0 ? "请查看日志目录中的 stderr 日志。" : lines[lines.Length - 1].Trim();
            }
            catch { return "请查看日志目录中的 stderr 日志。"; }
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

        public bool IsAutostartEnabled() { return File.Exists(StartupLink) || File.Exists(LegacyStartupLink); }

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
                if (!enabled) { SafeDelete(StartupLink); SafeDelete(LegacyStartupLink); return OperationResult.Success("已关闭开机启动。"); }
                SaveConfig();
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic shortcut = shell.CreateShortcut(StartupLink);
                shortcut.TargetPath = Application.ExecutablePath;
                shortcut.Arguments = "--start";
                shortcut.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                shortcut.Description = "Start DSH Web at user sign-in";
                shortcut.Save();
                SafeDelete(LegacyStartupLink);
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
        private const int DesignWidth = 720;
        private const int DesignHeight = 630;
        private static Color Page = Color.FromArgb(10, 12, 19);
        private static Color Surface = Color.FromArgb(24, 28, 39);
        private static Color SurfaceRaised = Color.FromArgb(29, 34, 47);
        private static Color Border = Color.FromArgb(65, 72, 89);
        private static Color TextPrimary = Color.FromArgb(243, 245, 250);
        private static Color TextMuted = Color.FromArgb(164, 173, 192);
        private static Color Accent = Color.FromArgb(113, 149, 255);
        private readonly DshEngine engine;
        private readonly Label status = new Label();
        private readonly Label detail = new Label();
        private readonly Label processInfo = new Label();
        private readonly Panel dot = new Panel();
        private readonly TextBox workspace = new TextBox();
        private readonly TextBox port = new TextBox();
        private readonly VectorButton start = new VectorButton();
        private readonly VectorButton stop = new VectorButton();
        private readonly VectorButton open = new VectorButton();
        private readonly VectorButton browse = new VectorButton();
        private readonly VectorButton checkUpdate = new VectorButton();
        private readonly VectorButton updateDsh = new VectorButton();
        private readonly VectorButton installDsh = new VectorButton();
        private readonly Label versionInfo = new Label();
        private readonly VectorButton language = new VectorButton();
        private readonly VectorButton theme = new VectorButton();
        private readonly ProgressBar updateProgress = new ProgressBar();
        private readonly Panel installPanel = new Panel();
        private readonly CheckBox autostart = new CheckBox();
        private readonly ToolTip tips = new ToolTip { AutoPopDelay = 12000, InitialDelay = 450, ReshowDelay = 100 };
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly List<LayoutEntry> designLayout = new List<LayoutEntry>();
        private VectorSurfacePanel statusCard;
        private VectorSurfacePanel configCard;
        private VectorSurfacePanel toolsCard;
        private Panel statusDivider;
        private VectorButton logsButton;
        private VectorButton folderButton;
        private VectorButton minimizeButton;
        private VectorButton maximizeButton;
        private VectorButton closeButton;
        private Panel headerLine;
        private Label footer;
        private Label watermark;
        private bool updating;
        private string updatePhase;
        private int updatePercent;
        private DateTime updateStartedUtc;
        private bool eventsBound;
        private float appliedScale = -1f;
        private Size appliedClientSize = Size.Empty;

        private bool English { get { return string.Equals(engine.Config.Language, "en", StringComparison.OrdinalIgnoreCase); } }
        private bool DarkTheme { get { return !string.Equals(engine.Config.Theme, "light", StringComparison.OrdinalIgnoreCase); } }
        private string T(string chinese, string english) { return English ? english : chinese; }
        private string AppName { get { return T("DSH Web 启动器", "DSH Web Launcher"); } }

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int attributeSize);

        public MainForm(DshEngine engine)
        {
            this.engine = engine;
            ApplyThemeColors();
            Text = AppName;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(DesignWidth, DesignHeight);
            MinimumSize = new Size(DesignWidth, DesignHeight);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = true;
            BackColor = Page;
            Font = new Font("Segoe UI Variable Text", 9.5f);
            DoubleBuffered = true;
            BuildUi();
            HandleCreated += delegate { ApplyWindowShape(); };
            Paint += MainForm_Paint;
            SizeChanged += delegate { ApplyProportionalLayout(); Invalidate(); };
            Load += async delegate { RefreshStatus(); if (engine.FindInstallation() != null) await CheckUpdates(false); };
            FormClosed += delegate { timer.Stop(); };
            timer.Interval = 3000;
            timer.Tick += delegate { if (updating) RenderUpdateProgress(); else RefreshStatus(); };
            timer.Start();
        }

        private void ApplyThemeColors()
        {
            if (DarkTheme)
            {
                Page = Color.FromArgb(10, 12, 19); Surface = Color.FromArgb(24, 28, 39); SurfaceRaised = Color.FromArgb(29, 34, 47);
                Border = Color.FromArgb(65, 72, 89); TextPrimary = Color.FromArgb(243, 245, 250); TextMuted = Color.FromArgb(164, 173, 192); Accent = Color.FromArgb(113, 149, 255);
            }
            else
            {
                Page = Color.FromArgb(244, 247, 252); Surface = Color.FromArgb(255, 255, 255); SurfaceRaised = Color.FromArgb(241, 245, 250);
                Border = Color.FromArgb(205, 214, 229); TextPrimary = Color.FromArgb(28, 39, 57); TextMuted = Color.FromArgb(93, 105, 123); Accent = Color.FromArgb(72, 116, 226);
            }
            BackColor = Page;
        }

        private void ApplyWindowShape()
        {
            var rounded = 2;
            DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
        }

        protected override void WndProc(ref Message m)
        {
            const int WmNcHitTest = 0x84;
            const int HtClient = 1;
            const int HtLeft = 10;
            const int HtRight = 11;
            const int HtTop = 12;
            const int HtTopLeft = 13;
            const int HtTopRight = 14;
            const int HtBottom = 15;
            const int HtBottomLeft = 16;
            const int HtBottomRight = 17;
            base.WndProc(ref m);
            if (m.Msg != WmNcHitTest || (int)m.Result != HtClient) return;
            var position = PointToClient(new Point((short)(long)m.LParam, (short)((long)m.LParam >> 16)));
            const int grip = 8;
            var left = position.X <= grip; var right = position.X >= ClientSize.Width - grip;
            var top = position.Y <= grip; var bottom = position.Y >= ClientSize.Height - grip;
            if (left && top) m.Result = (IntPtr)HtTopLeft;
            else if (right && top) m.Result = (IntPtr)HtTopRight;
            else if (left && bottom) m.Result = (IntPtr)HtBottomLeft;
            else if (right && bottom) m.Result = (IntPtr)HtBottomRight;
            else if (left) m.Result = (IntPtr)HtLeft;
            else if (right) m.Result = (IntPtr)HtRight;
            else if (top) m.Result = (IntPtr)HtTop;
            else if (bottom) m.Result = (IntPtr)HtBottom;
        }

        private void MainForm_Paint(object sender, PaintEventArgs e)
        {
            e.Graphics.Clear(Page);
        }

        private void BeginWindowDrag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0);
        }

        private void AddDraggableHeader(params Control[] controls)
        {
            foreach (var control in controls)
            {
                control.MouseDown += BeginWindowDrag;
                Controls.Add(control);
            }
        }

        private void AddWindowButtons()
        {
            minimizeButton = new VectorButton { IconKind = VectorIcon.Minimize };
            SetupButton(minimizeButton, "", 612, 20, 22, SurfaceRaised, TextMuted, false, 28);
            minimizeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            minimizeButton.Click += delegate { WindowState = FormWindowState.Minimized; };
            maximizeButton = new VectorButton { IconKind = VectorIcon.Maximize };
            SetupButton(maximizeButton, "", 640, 20, 22, SurfaceRaised, TextMuted, false, 28);
            maximizeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            maximizeButton.Click += delegate
            {
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                maximizeButton.IconKind = WindowState == FormWindowState.Maximized ? VectorIcon.Restore : VectorIcon.Maximize;
            };
            closeButton = new VectorButton { IconKind = VectorIcon.Close };
            SetupButton(closeButton, "", 668, 20, 22, DarkTheme ? Color.FromArgb(39, 42, 54) : Color.FromArgb(250, 235, 237), TextMuted, false, 28);
            closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            closeButton.Click += delegate { Close(); };
            Controls.Add(minimizeButton); Controls.Add(maximizeButton); Controls.Add(closeButton);
        }

        private static void StylePill(Label label, Color backColor)
        {
            label.BackColor = backColor;
            label.Paint += delegate(object sender, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = RoundedPath(new Rectangle(0, 0, label.Width - 1, label.Height - 1), 10))
                using (var edge = new LinearGradientBrush(new Rectangle(0, 0, label.Width, label.Height), Color.FromArgb(190, 255, 255, 255), Color.FromArgb(62, 255, 255, 255), LinearGradientMode.ForwardDiagonal))
                using (var pen = new Pen(edge, 1))
                    e.Graphics.DrawPath(pen, path);
            };
        }

        private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2);
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void BuildUi()
        {
            var whale = new PictureBox { Location = new Point(28, 22), Size = new Size(31, 31), SizeMode = PictureBoxSizeMode.Zoom, Image = LoadWhaleImage(), Cursor = Cursors.SizeAll };
            var brand = NewLabel("DSH", 69, 18, 66, 29, 18, FontStyle.Bold, TextPrimary);
            brand.Font = new Font("Segoe UI Variable Display", 18, FontStyle.Bold);
            var badge = NewLabel("WEB LAUNCHER", 139, 23, 102, 21, 8, FontStyle.Bold, DarkTheme ? Color.FromArgb(200, 214, 255) : Accent, ContentAlignment.MiddleCenter);
            StylePill(badge, DarkTheme ? Color.FromArgb(36, 47, 78) : Color.FromArgb(226, 234, 255));
            var subtitle = NewLabel(T("本地 Harness 启动控制", "LOCAL HARNESS LAUNCH CONTROL"), 69, 48, 320, 16, 8, FontStyle.Bold, TextMuted);
            AddDraggableHeader(whale, brand, badge, subtitle);
            headerLine = new Panel { Location = new Point(28, 75), Size = new Size(664, 1), BackColor = Border };
            headerLine.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(headerLine);
            AddWindowButtons();

            theme.IconKind = DarkTheme ? VectorIcon.ThemeLight : VectorIcon.ThemeDark;
            SetupButton(theme, "", 350, 20, 38, SurfaceRaised, TextMuted, false, 28);
            if (!eventsBound) theme.Click += delegate { engine.Config.Theme = DarkTheme ? "light" : "dark"; engine.SaveConfig(); RebuildUi(); };
            Controls.Add(theme);
            SetupButton(language, English ? "中文" : "EN", 398, 20, 44, SurfaceRaised, TextMuted, false, 28);
            language.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            if (!eventsBound) language.Click += delegate { engine.Config.Language = English ? "zh" : "en"; engine.SaveConfig(); RebuildForLanguage(); };
            Controls.Add(language);

            statusCard = SurfaceCard(new Point(28, 97), new Size(664, 204)); statusCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; Controls.Add(statusCard);
            statusCard.Controls.Add(NewCardLabel(T("本地服务", "LOCAL SERVICE"), 24, 20, 130, 16, 8, FontStyle.Bold, TextMuted));
            dot.Location = new Point(24, 53); dot.Size = new Size(9, 9); dot.BackColor = Color.FromArgb(119, 126, 142); statusCard.Controls.Add(dot);
            status.Location = new Point(41, 43); status.Size = new Size(308, 26); status.Font = new Font("Segoe UI Variable Display", 13.5f, FontStyle.Bold); status.ForeColor = TextPrimary; status.BackColor = Surface; statusCard.Controls.Add(status);
            detail.Location = new Point(24, 82); detail.Size = new Size(338, 19); detail.Font = new Font("Consolas", 8.5f); detail.ForeColor = TextMuted; detail.BackColor = Surface; detail.AutoEllipsis = true; detail.TextAlign = ContentAlignment.MiddleLeft; statusCard.Controls.Add(detail);
            processInfo.Location = new Point(24, 107); processInfo.Size = new Size(338, 17); processInfo.Font = new Font("Consolas", 8, FontStyle.Bold); processInfo.ForeColor = Color.FromArgb(126, 205, 225); processInfo.BackColor = Surface; processInfo.AutoEllipsis = true; statusCard.Controls.Add(processInfo);
            versionInfo.Location = new Point(24, 145); versionInfo.Size = new Size(338, 18); versionInfo.Font = new Font("Consolas", 8.25f); versionInfo.ForeColor = TextMuted; versionInfo.BackColor = Surface; versionInfo.AutoEllipsis = true; versionInfo.TextAlign = ContentAlignment.MiddleLeft; statusCard.Controls.Add(versionInfo);
            updateProgress.Location = new Point(24, 176); updateProgress.Size = new Size(338, 5); updateProgress.Minimum = 0; updateProgress.Maximum = 100; updateProgress.Visible = false; statusCard.Controls.Add(updateProgress);
            statusDivider = new Panel { Location = new Point(386, 21), Size = new Size(1, 162), BackColor = Border }; statusCard.Controls.Add(statusDivider);
            SetupButton(open, T("打开本地网页", "OPEN LOCAL WEB"), 410, 24, 228, Accent, TextPrimary, true); open.Anchor = AnchorStyles.Top | AnchorStyles.Right; statusCard.Controls.Add(open);
            SetupButton(start, T("启动", "START"), 410, 78, 109, SurfaceRaised, TextPrimary, false); start.Anchor = AnchorStyles.Top | AnchorStyles.Right; statusCard.Controls.Add(start);
            SetupButton(stop, T("停止", "STOP"), 529, 78, 109, Color.FromArgb(64, 37, 48), Color.FromArgb(255, 207, 217), false); stop.Anchor = AnchorStyles.Top | AnchorStyles.Right; statusCard.Controls.Add(stop);
            SetupButton(checkUpdate, T("检查更新", "CHECK UPDATE"), 410, 132, 109, SurfaceRaised, TextMuted, false); checkUpdate.Anchor = AnchorStyles.Top | AnchorStyles.Right; statusCard.Controls.Add(checkUpdate);
            SetupButton(updateDsh, T("更新 DSH", "UPDATE DSH"), 529, 132, 109, Color.FromArgb(30, 54, 86), Color.FromArgb(193, 216, 255), false); updateDsh.Anchor = AnchorStyles.Top | AnchorStyles.Right; updateDsh.Visible = false; statusCard.Controls.Add(updateDsh);
            if (!eventsBound) { checkUpdate.Click += async delegate { await CheckUpdates(true); }; updateDsh.Click += async delegate { await UpdateDsh(); }; }

            configCard = SurfaceCard(new Point(28, 321), new Size(664, 169)); configCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; Controls.Add(configCard);
            configCard.Controls.Add(NewCardLabel(T("工作区", "WORKSPACE"), 24, 20, 145, 16, 8, FontStyle.Bold, TextMuted));
            workspace.Location = new Point(24, 44); workspace.Size = new Size(448, 32); workspace.Text = engine.Config.Workspace; workspace.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; StyleTextBox(workspace); configCard.Controls.Add(workspace);
            SetupButton(browse, T("选择文件夹", "BROWSE"), 492, 43, 147, SurfaceRaised, TextPrimary, false); browse.Anchor = AnchorStyles.Top | AnchorStyles.Right; configCard.Controls.Add(browse);
            configCard.Controls.Add(NewCardLabel(T("监听端口", "LISTEN PORT"), 24, 101, 115, 16, 8, FontStyle.Bold, TextMuted));
            port.Location = new Point(24, 123); port.Size = new Size(128, 28); port.Text = engine.Config.Port.ToString(); StylePort(port); configCard.Controls.Add(port);
            autostart.Text = T("登录 Windows 后自动启动 DSH Web", "Start DSH Web when signing in to Windows"); autostart.Location = new Point(190, 120); autostart.Size = new Size(380, 30); autostart.Checked = engine.IsAutostartEnabled(); autostart.ForeColor = TextPrimary; autostart.BackColor = Surface; autostart.FlatStyle = FlatStyle.Flat; if (!eventsBound) autostart.CheckedChanged += Autostart_CheckedChanged; configCard.Controls.Add(autostart);
            configCard.Controls.Add(NewCardLabel(T("配置只决定 DSH 从哪个项目目录启动，不会读取或移动你的项目文件。", "This only chooses the project folder DSH starts in. It never reads or moves project files."), 24, 151, 615, 15, 8, FontStyle.Regular, TextMuted));

            toolsCard = SurfaceCard(new Point(28, 510), new Size(664, 73)); toolsCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; Controls.Add(toolsCard);
            toolsCard.Controls.Add(NewCardLabel(T("仅本机访问", "LOCAL ONLY"), 24, 17, 105, 15, 8, FontStyle.Bold, Color.FromArgb(136, 210, 227)));
            toolsCard.Controls.Add(NewCardLabel(T("服务只监听 127.0.0.1。关闭窗口不会停止正在运行的 DSH Web。", "The service only listens on 127.0.0.1. Closing this window does not stop DSH Web."), 24, 38, 425, 19, 9, FontStyle.Regular, TextPrimary));
            logsButton = new VectorButton(); SetupButton(logsButton, T("日志", "LOGS"), 463, 19, 78, SurfaceRaised, TextPrimary, false); logsButton.Anchor = AnchorStyles.Top | AnchorStyles.Right; toolsCard.Controls.Add(logsButton);
            folderButton = new VectorButton(); SetupButton(folderButton, T("文件夹", "FOLDER"), 551, 19, 88, SurfaceRaised, TextPrimary, false); folderButton.Anchor = AnchorStyles.Top | AnchorStyles.Right; toolsCard.Controls.Add(folderButton);
            if (!eventsBound)
            {
                start.Click += async delegate { await RunOperation(T("正在启动...", "Starting..."), engine.Start); };
                stop.Click += async delegate { await RunOperation(T("正在停止...", "Stopping..."), engine.Stop); };
                open.Click += delegate { Process.Start(engine.CurrentUrl); };
            }
            logsButton.Click += delegate { Directory.CreateDirectory(engine.LogDir); Process.Start(engine.LogDir); };
            folderButton.Click += delegate { if (Directory.Exists(workspace.Text)) Process.Start(workspace.Text); };
            footer = NewLabel(T("DeepSeek Harness 在本机运行。DSH Web 启动器是独立的社区工具。", "DeepSeek Harness runs locally. DSH Web Launcher is an independent community tool."), 28, 599, 540, 15, 8, FontStyle.Regular, TextMuted, ContentAlignment.MiddleCenter); footer.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; Controls.Add(footer);
            watermark = NewLabel("LVSUGARS", 610, 599, 82, 15, 7.25f, FontStyle.Regular, TextMuted, ContentAlignment.MiddleRight); watermark.Anchor = AnchorStyles.Bottom | AnchorStyles.Right; Controls.Add(watermark);

            BuildInstallPanel();
            ApplyLanguage();
            eventsBound = true;
            CaptureDesignLayout();
            ApplyProportionalLayout();
        }

        private async void RebuildForLanguage()
        {
            RebuildUi();
            if (engine.FindInstallation() != null) await CheckUpdates(false);
        }

        private void RebuildUi()
        {
            timer.Stop();
            Controls.Clear();
            installPanel.Controls.Clear();
            ApplyThemeColors();
            BuildUi();
            RefreshStatus();
            timer.Start();
        }

        private void CaptureDesignLayout()
        {
            designLayout.Clear();
            CaptureDesignLayout(Controls, true);
            appliedScale = -1f;
            appliedClientSize = Size.Empty;
        }

        private void CaptureDesignLayout(Control.ControlCollection controls, bool topLevel)
        {
            foreach (Control control in controls)
            {
                control.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                designLayout.Add(new LayoutEntry(control, control.Bounds, control.Font, control.Padding, topLevel));
                CaptureDesignLayout(control.Controls, false);
            }
        }

        private void ApplyProportionalLayout()
        {
            if (designLayout.Count == 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            var wide = ClientSize.Width / (float)ClientSize.Height >= 1.55f;
            var scale = wide ? (float)ClientSize.Height / DesignHeight : Math.Min((float)ClientSize.Width / DesignWidth, (float)ClientSize.Height / DesignHeight);
            var canvasWidth = wide ? Math.Min(1120f, ClientSize.Width / scale) : DesignWidth;
            var offsetX = (ClientSize.Width - canvasWidth * scale) / 2f;
            var offsetY = (ClientSize.Height - DesignHeight * scale) / 2f;
            if (Math.Abs(appliedScale - scale) < 0.002f && appliedClientSize == ClientSize) return;

            SuspendLayout();
            try
            {
                foreach (var entry in designLayout)
                {
                    if (entry.Control.IsDisposed) continue;
                    var x = entry.Bounds.X * scale + (entry.TopLevel ? offsetX : 0f);
                    var y = entry.Bounds.Y * scale + (entry.TopLevel ? offsetY : 0f);
                    var width = Math.Max(1, (int)Math.Round(entry.Bounds.Width * scale));
                    var height = Math.Max(1, (int)Math.Round(entry.Bounds.Height * scale));
                    entry.Control.SetBounds((int)Math.Round(x), (int)Math.Round(y), width, height);
                    entry.Control.Padding = ScalePadding(entry.Padding, scale);
                    if (entry.ScaledFont != null) entry.ScaledFont.Dispose();
                    entry.ScaledFont = ScaleFont(entry.Font, scale);
                    entry.Control.Font = entry.ScaledFont;
                    var surface = entry.Control as VectorSurfacePanel;
                    if (surface != null) surface.Radius = Math.Max(4, (int)Math.Round(entry.Radius * scale));
                }
            }
            finally { ResumeLayout(true); }
            if (wide) ApplyWideLayout(scale, canvasWidth, offsetX);
            appliedScale = scale;
            appliedClientSize = ClientSize;
        }

        private void ApplyWideLayout(float scale, float canvasWidth, float offsetX)
        {
            var cardWidth = canvasWidth - 56f;
            SetBounds(statusCard, 28, 97, cardWidth, 204, scale, offsetX, true);
            SetBounds(configCard, 28, 321, cardWidth, 169, scale, offsetX, true);
            SetBounds(toolsCard, 28, 510, cardWidth, 73, scale, offsetX, true);
            SetBounds(headerLine, 28, 75, canvasWidth - 56, 1, scale, offsetX, true);
            SetBounds(footer, 28, 599, canvasWidth - 156, 15, scale, offsetX, true);
            SetBounds(watermark, canvasWidth - 126, 599, 98, 15, scale, offsetX, true);
            SetBounds(language, canvasWidth - 220, 20, 44, 28, scale, offsetX, true);
            SetBounds(theme, canvasWidth - 168, 20, 38, 28, scale, offsetX, true);
            SetBounds(minimizeButton, canvasWidth - 108, 20, 22, 28, scale, offsetX, true);
            SetBounds(maximizeButton, canvasWidth - 80, 20, 22, 28, scale, offsetX, true);
            SetBounds(closeButton, canvasWidth - 52, 20, 22, 28, scale, offsetX, true);

            var dividerX = Math.Max(440f, cardWidth * 0.58f);
            SetBounds(statusDivider, dividerX, 21, 1, 162, scale, 0, false);
            SetBounds(status, 41, 43, dividerX - 68, 26, scale, 0, false);
            SetBounds(detail, 24, 82, dividerX - 48, 19, scale, 0, false);
            SetBounds(processInfo, 24, 107, dividerX - 48, 17, scale, 0, false);
            SetBounds(versionInfo, 24, 145, dividerX - 48, 18, scale, 0, false);
            SetBounds(updateProgress, 24, 176, dividerX - 48, 5, scale, 0, false);
            SetBounds(open, cardWidth - 252, 24, 228, 36, scale, 0, false);
            SetBounds(start, cardWidth - 252, 78, 109, 36, scale, 0, false);
            SetBounds(stop, cardWidth - 133, 78, 109, 36, scale, 0, false);
            SetBounds(checkUpdate, cardWidth - 252, 132, 109, 36, scale, 0, false);
            SetBounds(updateDsh, cardWidth - 133, 132, 109, 36, scale, 0, false);

            SetBounds(workspace, 24, 44, cardWidth - 219, 32, scale, 0, false);
            SetBounds(browse, cardWidth - 171, 43, 147, 36, scale, 0, false);
            SetBounds(autostart, 190, 120, cardWidth - 214, 30, scale, 0, false);
            SetBounds(logsButton, cardWidth - 176, 19, 78, 36, scale, 0, false);
            SetBounds(folderButton, cardWidth - 88, 19, 88, 36, scale, 0, false);
        }

        private static void SetBounds(Control control, float x, float y, float width, float height, float scale, float offsetX, bool topLevel)
        {
            if (control == null) return;
            control.SetBounds((int)Math.Round(x * scale + (topLevel ? offsetX : 0f)), (int)Math.Round(y * scale),
                Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        }

        private Image LoadWhaleImage()
        {
            using (var source = Icon.ToBitmap())
            {
                var target = new Bitmap(source.Width, source.Height);
                var color = DarkTheme ? Color.FromArgb(244, 247, 252) : Color.FromArgb(13, 18, 27);
                for (var y = 0; y < source.Height; y++)
                    for (var x = 0; x < source.Width; x++)
                    {
                        var pixel = source.GetPixel(x, y);
                        target.SetPixel(x, y, pixel.A == 0 ? Color.Transparent : Color.FromArgb(pixel.A, color));
                    }
                return target;
            }
        }

        private static Padding ScalePadding(Padding padding, float scale)
        {
            return new Padding((int)Math.Round(padding.Left * scale), (int)Math.Round(padding.Top * scale),
                (int)Math.Round(padding.Right * scale), (int)Math.Round(padding.Bottom * scale));
        }

        private static Font ScaleFont(Font font, float scale)
        {
            return new Font(font.FontFamily, Math.Max(1f, font.SizeInPoints * scale), font.Style, font.Unit);
        }

        private sealed class LayoutEntry
        {
            public readonly Control Control;
            public readonly Rectangle Bounds;
            public readonly Font Font;
            public readonly Padding Padding;
            public readonly bool TopLevel;
            public readonly int Radius;
            public Font ScaledFont;

            public LayoutEntry(Control control, Rectangle bounds, Font font, Padding padding, bool topLevel)
            {
                Control = control;
                Bounds = bounds;
                Font = font;
                Padding = padding;
                TopLevel = topLevel;
                var surface = control as VectorSurfacePanel;
                Radius = surface == null ? 0 : surface.Radius;
            }
        }

        private void ApplyLanguage()
        {
            Text = AppName;
            language.Text = English ? "中文" : "EN";
            start.Text = T("启动", "START");
            stop.Text = T("停止", "STOP");
            open.Text = T("打开本地网页", "OPEN LOCAL WEB");
            browse.Text = T("选择文件夹", "BROWSE");
            checkUpdate.Text = T("检查更新", "CHECK UPDATE");
            installDsh.Text = T("安装官方 DSH", "INSTALL OFFICIAL DSH");
            autostart.Text = T("登录 Windows 后自动启动 DSH Web", "Start DSH Web when signing in to Windows");
        }

        private void BuildInstallPanel()
        {
            installPanel.Location = new Point(0, 77); installPanel.Size = new Size(720, 550); installPanel.BackColor = Page;
            var whale = new PictureBox { Location = new Point(57, 62), Size = new Size(74, 74), SizeMode = PictureBoxSizeMode.Zoom, Image = Icon.ToBitmap() };
            installPanel.Controls.Add(whale);
            installPanel.Controls.Add(NewLabel(T("需要安装官方 DSH", "Official DSH is required"), 147, 64, 500, 32, 18, FontStyle.Bold, TextPrimary));
            installPanel.Controls.Add(NewLabel(T("安装完成后，这里会成为你的本地 Harness 启动器。", "After installation, this becomes your local Harness launcher."), 147, 100, 500, 26, 10, FontStyle.Regular, TextMuted));
            var installInfo = SurfaceCard(new Point(57, 167), new Size(606, 116));
            installInfo.Controls.Add(NewLabel(T("01  官方 @deepseek-ai/dsh 与 Node.js 运行时", "01  Official @deepseek-ai/dsh and Node.js runtime"), 20, 20, 560, 22, 9.25f, FontStyle.Regular, TextPrimary));
            installInfo.Controls.Add(NewLabel(T("02  校验下载并安装至当前用户目录，约需 350 MB 磁盘空间", "02  Verified download to your user profile, about 350 MB disk space"), 20, 50, 560, 22, 9.25f, FontStyle.Regular, TextMuted));
            installInfo.Controls.Add(NewLabel(T("03  不会覆盖 .dsh 会话、账号、凭据或工作区", "03  Does not overwrite .dsh sessions, accounts, credentials, or workspaces"), 20, 78, 560, 20, 9.25f, FontStyle.Regular, TextMuted));
            installPanel.Controls.Add(installInfo);
            SetupButton(installDsh, T("安装官方 DSH", "INSTALL OFFICIAL DSH"), 57, 312, 260, Accent, TextPrimary, true);
            if (!eventsBound) installDsh.Click += async delegate { await InstallDsh(); }; installPanel.Controls.Add(installDsh);
            var installLogs = new VectorButton(); SetupButton(installLogs, T("查看日志", "VIEW LOGS"), 330, 312, 144, SurfaceRaised, TextPrimary, false); installLogs.Click += delegate { Directory.CreateDirectory(engine.LogDir); Process.Start(engine.LogDir); }; installPanel.Controls.Add(installLogs);
            installPanel.Controls.Add(NewLabel(T("首次安装通常需要 5-20 分钟，安装过程中请保持网络可用。", "First installation usually takes 5-20 minutes. Keep your network available."), 57, 371, 560, 20, 9.25f, FontStyle.Regular, TextMuted));
            installPanel.Controls.Add(NewLabel("LVSUGARS / COMMUNITY TOOL", 57, 462, 606, 20, 8.25f, FontStyle.Bold, TextMuted, ContentAlignment.MiddleRight));
            Controls.Add(installPanel); installPanel.BringToFront();
        }

        private async Task InstallDsh()
        {
            installDsh.Enabled = false; installDsh.Text = T("正在安装...", "INSTALLING..."); UseWaitCursor = true;
            var result = await Task.Run(() => engine.InstallOrUpdateManagedRuntime(false, null));
            UseWaitCursor = false; installDsh.Enabled = true; installDsh.Text = T("安装官方 DSH", "INSTALL OFFICIAL DSH"); RefreshStatus();
            if (!result.Ok) MessageBox.Show(this, ResultText(result.Message), AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            else await CheckUpdates(false);
        }

        private async Task CheckUpdates(bool showFailure)
        {
            var installation = engine.FindInstallation();
            if (installation == null) return;
            SetVersionInfo("DSH " + installation.Version + T("  ·  正在检查更新...", "  ·  Checking for updates..."));
            checkUpdate.Enabled = false;
            try
            {
                var latest = await Task.Run(() => engine.GetLatestDshVersion());
                var current = engine.FindInstallation();
                if (current == null) return;
                var currentText = current.Managed ? T("受管安装", "managed") : T("外部安装", "external");
                var comparison = DshEngine.CompareVersions(current.Version, latest);
                if (comparison == 0)
                {
                    SetVersionInfo("DSH " + current.Version + T("  ·  已是最新  ·  ", "  ·  Up to date  ·  ") + currentText);
                    updateDsh.Visible = false;
                }
                else if (comparison < 0)
                {
                    SetVersionInfo("DSH " + current.Version + "  →  " + latest + T(" 可更新  ·  ", " available  ·  ") + currentText);
                    updateDsh.Visible = current.Managed;
                    updateDsh.Text = T("更新到 ", "UPDATE TO ") + latest;
                }
                else
                {
                    SetVersionInfo("DSH " + current.Version + T("  ·  高于官方 latest  ·  ", "  ·  newer than npm latest  ·  ") + currentText);
                    updateDsh.Visible = false;
                }
            }
            catch (Exception ex)
            {
                SetVersionInfo("DSH " + installation.Version + T("  ·  更新检查失败", "  ·  Update check failed"));
                updateDsh.Visible = false;
                if (showFailure) MessageBox.Show(this, T("无法检查 DSH 更新：", "Unable to check DSH updates: ") + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { checkUpdate.Enabled = true; }
        }

        private async Task UpdateDsh()
        {
            updating = true; updateStartedUtc = DateTime.UtcNow; SetBusy(true); checkUpdate.Visible = false; updateDsh.Visible = false; SetUpdateProgress(5, T("正在准备更新...", "Preparing update..."));
            var result = await Task.Run(() => engine.InstallOrUpdateManagedRuntime(true, SetUpdateProgress));
            if (result.Ok) SetUpdateProgress(100, T("DSH 已更新完成", "DSH update complete"));
            updating = false; updateProgress.Visible = false; checkUpdate.Visible = true; SetBusy(false); checkUpdate.Enabled = true; updateDsh.Enabled = true; RefreshStatus();
            if (!result.Ok) MessageBox.Show(this, ResultText(result.Message), AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            else { MessageBox.Show(this, ResultText(result.Message), AppName, MessageBoxButtons.OK, MessageBoxIcon.Information); await CheckUpdates(false); }
        }

        private void SetUpdateProgress(int percent, string phase)
        {
            if (InvokeRequired) { BeginInvoke(new Action<int, string>(SetUpdateProgress), percent, phase); return; }
            updatePercent = Math.Max(updateProgress.Minimum, Math.Min(updateProgress.Maximum, percent));
            updatePhase = phase;
            updateProgress.Style = percent >= 40 && percent < 70 ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            if (updateProgress.Style == ProgressBarStyle.Continuous) updateProgress.Value = updatePercent;
            updateProgress.Visible = true;
            RenderUpdateProgress();
        }

        private void RenderUpdateProgress()
        {
            if (!updating || string.IsNullOrEmpty(updatePhase)) return;
            var elapsed = DateTime.UtcNow - updateStartedUtc;
            SetVersionInfo(updatePhase + (updateProgress.Style == ProgressBarStyle.Marquee
                ? T("  ·  已用 ", "  ·  ") + ((int)elapsed.TotalMinutes) + T(" 分 ", "m ") + elapsed.Seconds + T(" 秒", "s elapsed")
                : "  ·  " + updatePercent + "%"));
        }

        private async Task RunOperation(string busyText, Func<OperationResult> action)
        {
            SaveInputs(); SetBusy(true); status.Text = busyText;
            var result = await Task.Run(action);
            SetBusy(false); RefreshStatus();
            if (!result.Ok) MessageBox.Show(this, ResultText(result.Message), AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void SaveInputs()
        {
            int parsedPort;
            engine.Config.Workspace = workspace.Text.Trim();
            engine.Config.Port = int.TryParse(port.Text.Trim(), out parsedPort) ? parsedPort : 0;
            engine.SaveConfig();
        }

        private void Browse_Click(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog { Description = T("选择 DSH 工作区", "Choose a DSH workspace"), SelectedPath = Directory.Exists(workspace.Text) ? workspace.Text : "" })
                if (dialog.ShowDialog(this) == DialogResult.OK) { workspace.Text = dialog.SelectedPath; SaveInputs(); }
        }

        private void Autostart_CheckedChanged(object sender, EventArgs e)
        {
            SaveInputs(); var result = engine.SetAutostart(autostart.Checked);
            if (!result.Ok) { MessageBox.Show(this, ResultText(result.Message), AppName, MessageBoxButtons.OK, MessageBoxIcon.Error); autostart.CheckedChanged -= Autostart_CheckedChanged; autostart.Checked = engine.IsAutostartEnabled(); autostart.CheckedChanged += Autostart_CheckedChanged; }
        }

        private void RefreshStatus()
        {
            if (updating) return;
            var installation = engine.FindInstallation();
            installPanel.Visible = installation == null;
            if (installation == null) { timer.Stop(); return; }
            if (!timer.Enabled) timer.Start();
            if (string.IsNullOrEmpty(versionInfo.Text)) SetVersionInfo("DSH " + installation.Version + (installation.Managed ? T("  ·  受管安装", "  ·  managed") : T("  ·  外部安装", "  ·  external")));
            var s = engine.GetStatus(); status.Text = LocalStatus(s.Text);
            if (s.Healthy && s.Managed)
            {
                dot.BackColor = Color.FromArgb(87, 224, 151); status.ForeColor = TextPrimary;
                SetServiceDetail(engine.CurrentUrl, "PROCESS / PID " + s.ListenerPid);
            }
            else if (s.ListenerPid > 0)
            {
                dot.BackColor = Color.FromArgb(255, 145, 132); status.ForeColor = TextPrimary;
                SetServiceDetail(T("端口 ", "Port ") + engine.Config.Port + T(" 正由其他进程使用", " is used by another process"), "PROCESS / PID " + s.ListenerPid);
            }
            else
            {
                dot.BackColor = Color.FromArgb(125, 134, 151); status.ForeColor = TextPrimary;
                SetServiceDetail(T("服务未运行。选择工作区后启动本地 Web 控制台。", "The service is stopped. Select a workspace, then start local DSH Web."), "READY FOR A LOCAL SESSION");
            }
            start.Enabled = s.ListenerPid == 0; stop.Enabled = s.Managed; open.Enabled = s.Healthy;
        }

        private void SetServiceDetail(string serviceText, string processText)
        {
            detail.Text = serviceText; processInfo.Text = processText;
            tips.SetToolTip(detail, serviceText); tips.SetToolTip(processInfo, processText);
        }

        private string LocalStatus(string text)
        {
            if (!English) return text;
            if (text == "运行中") return "Running";
            if (text == "检测到外部 DSH Web") return "External DSH Web detected";
            if (text == "端口被其他程序占用") return "Port is used by another app";
            if (text == "已停止") return "Stopped";
            return text;
        }

        private string ResultText(string text)
        {
            if (!English || string.IsNullOrEmpty(text)) return text;
            return text
                .Replace("DSH Web 已启动。", "DSH Web started.")
                .Replace("DSH Web 已停止。", "DSH Web stopped.")
                .Replace("已开启开机启动。", "Start at Windows sign-in is enabled.")
                .Replace("已关闭开机启动。", "Start at Windows sign-in is disabled.")
                .Replace("DSH 已更新完成", "DSH update complete")
                .Replace("DSH 安装完成", "DSH installation complete")
                .Replace("DSH Web 启动器", "DSH Web Launcher");
        }

        private void SetVersionInfo(string text)
        {
            versionInfo.Text = text; tips.SetToolTip(versionInfo, text);
        }

        private void SetBusy(bool busy) { UseWaitCursor = busy; start.Enabled = stop.Enabled = open.Enabled = browse.Enabled = checkUpdate.Enabled = updateDsh.Enabled = !busy; }
        private static VectorSurfacePanel SurfaceCard(Point location, Size size)
        {
            var card = new VectorSurfacePanel { Location = location, Size = size, SurfaceColor = Surface, BorderColor = Border, Radius = 16 };
            return card;
        }

        private static void StyleTextBox(TextBox input)
        {
            input.BackColor = SurfaceRaised; input.ForeColor = TextPrimary; input.BorderStyle = BorderStyle.None;
            input.Font = new Font("Consolas", 9.5f); input.Padding = new Padding(10, 7, 10, 7);
        }

        private static void StylePort(TextBox input)
        {
            StyleTextBox(input);
            input.TextAlign = HorizontalAlignment.Center;
            input.MaxLength = 5;
            input.KeyPress += delegate(object sender, KeyPressEventArgs e) { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
        }

        private static void SetupButton(VectorButton b, string text, int x, int y, int width, Color back, Color fore, bool primary, int height = 36)
        {
            b.Text = text; b.Location = new Point(x, y); b.Size = new Size(width, height);
            b.FillColor = back; b.ForeColor = primary ? Color.White : fore; b.Primary = primary; b.Font = new Font("Segoe UI Variable Text", 8.25f, FontStyle.Bold);
            b.Cursor = Cursors.Hand; b.TabStop = false;
        }
        private static Label NewCardLabel(string text, int x, int y, int w, int h, float size, FontStyle style, Color color, ContentAlignment align = ContentAlignment.TopLeft)
        {
            var label = NewLabel(text, x, y, w, h, size, style, color, align);
            label.BackColor = Surface;
            return label;
        }

        private static Label NewLabel(string text, int x, int y, int w, int h, float size, FontStyle style, Color color, ContentAlignment align = ContentAlignment.TopLeft) { return new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h), Font = new Font("Microsoft YaHei UI", size, style), ForeColor = color, TextAlign = align, BackColor = Page }; }
    }

    internal sealed class VectorSurfacePanel : Panel
    {
        public Color SurfaceColor { get; set; }
        public Color BorderColor { get; set; }
        public int Radius { get; set; }

        public VectorSurfacePanel()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.Transparent;
        }

        protected override void OnPaintBackground(PaintEventArgs e) { base.OnPaintBackground(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (var path = CreatePath(bounds, Radius))
            using (var fill = new SolidBrush(SurfaceColor))
            using (var edge = new Pen(BorderColor, 1))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(edge, path);
            }
            base.OnPaint(e);
        }

        private static GraphicsPath CreatePath(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), Math.Max(2, radius * 2));
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal enum VectorIcon
    {
        None,
        Minimize,
        Maximize,
        Restore,
        ThemeLight,
        ThemeDark,
        Close
    }

    internal sealed class VectorButton : Button
    {
        private bool hovered;
        private bool pressed;

        public Color FillColor { get; set; }
        public bool Primary { get; set; }
        public VectorIcon IconKind { get; set; }

        public VectorButton()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseDownBackColor = Color.Transparent;
            FlatAppearance.MouseOverBackColor = Color.Transparent;
            UseVisualStyleBackColor = false;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnPaintBackground(PaintEventArgs pevent) { }

        protected override void OnParentChanged(EventArgs e)
        {
            var surface = Parent as VectorSurfacePanel;
            BackColor = surface != null ? surface.SurfaceColor : (Parent == null ? Color.FromArgb(10, 12, 19) : Parent.BackColor);
            base.OnParentChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.Clear(BackColor);
            var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            var radius = IconKind == VectorIcon.None ? 9 : 8;
            var fill = ResolveFill();
            using (var path = CreatePath(bounds, radius))
            {
                if (Primary && Enabled)
                {
                    using (var gradient = new LinearGradientBrush(bounds,
                        Lighten(fill, 18), Darken(fill, 15), LinearGradientMode.Vertical))
                        e.Graphics.FillPath(gradient, path);
                }
                else
                {
                    using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
                }
                using (var pen = new Pen(ResolveBorder(fill), 1)) e.Graphics.DrawPath(pen, path);
            }

            if (IconKind == VectorIcon.None)
            {
                var color = Enabled ? ForeColor : Color.FromArgb(108, ForeColor);
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            else
            {
                DrawIcon(e.Graphics);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        private Color ResolveFill()
        {
            var baseColor = Enabled ? FillColor : (BackColor.GetBrightness() < 0.5f ? Color.FromArgb(34, 39, 53) : Color.FromArgb(225, 231, 240));
            if (pressed) return Darken(baseColor, 18);
            if (hovered) return Lighten(baseColor, Primary ? 10 : 8);
            return baseColor;
        }

        private Color ResolveBorder(Color fill)
        {
            if (!Enabled) return Color.FromArgb(46, 58, 69, 87);
            if (Primary) return hovered ? Color.FromArgb(205, 183, 206, 255) : Color.FromArgb(152, 156, 183, 255);
            return hovered ? Color.FromArgb(122, 145, 157, 180) : Color.FromArgb(72, 105, 114, 137);
        }

        private void DrawIcon(Graphics graphics)
        {
            var color = Enabled ? (hovered && IconKind == VectorIcon.Close ? Color.FromArgb(255, 213, 223) : ForeColor) : Color.FromArgb(108, ForeColor);
            using (var pen = new Pen(color, 1.35f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                var centerX = (Width - 1) / 2f;
                var centerY = (Height - 1) / 2f;
                if (IconKind == VectorIcon.Minimize)
                    graphics.DrawLine(pen, centerX - 4.5f, centerY + 3f, centerX + 4.5f, centerY + 3f);
                else if (IconKind == VectorIcon.Maximize)
                    graphics.DrawRectangle(pen, centerX - 3.8f, centerY - 3.8f, 7.6f, 7.6f);
                else if (IconKind == VectorIcon.Restore)
                {
                    graphics.DrawRectangle(pen, centerX - 2.4f, centerY - 4.1f, 5.8f, 5.8f);
                    graphics.DrawLine(pen, centerX - 4f, centerY - 1.7f, centerX - 4f, centerY + 4f);
                    graphics.DrawLine(pen, centerX - 4f, centerY + 4f, centerX + 1.8f, centerY + 4f);
                }
                else if (IconKind == VectorIcon.ThemeLight)
                {
                    graphics.DrawEllipse(pen, centerX - 3.2f, centerY - 3.2f, 6.4f, 6.4f);
                    graphics.DrawLine(pen, centerX, centerY - 6.2f, centerX, centerY - 4.7f);
                    graphics.DrawLine(pen, centerX, centerY + 4.7f, centerX, centerY + 6.2f);
                    graphics.DrawLine(pen, centerX - 6.2f, centerY, centerX - 4.7f, centerY);
                    graphics.DrawLine(pen, centerX + 4.7f, centerY, centerX + 6.2f, centerY);
                }
                else if (IconKind == VectorIcon.ThemeDark)
                {
                    graphics.DrawArc(pen, centerX - 4.5f, centerY - 5.5f, 9f, 9f, 55, 250);
                }
                else if (IconKind == VectorIcon.Close)
                {
                    graphics.DrawLine(pen, centerX - 3.7f, centerY - 3.7f, centerX + 3.7f, centerY + 3.7f);
                    graphics.DrawLine(pen, centerX + 3.7f, centerY - 3.7f, centerX - 3.7f, centerY + 3.7f);
                }
            }
        }

        private static Color Lighten(Color color, int amount)
        {
            return Color.FromArgb(color.A, Math.Min(255, color.R + amount), Math.Min(255, color.G + amount), Math.Min(255, color.B + amount));
        }

        private static Color Darken(Color color, int amount)
        {
            return Color.FromArgb(color.A, Math.Max(0, color.R - amount), Math.Max(0, color.G - amount), Math.Max(0, color.B - amount));
        }

        private static GraphicsPath CreatePath(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), Math.Max(2, radius * 2));
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
