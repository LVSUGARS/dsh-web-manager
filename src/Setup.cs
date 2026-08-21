using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("DSH Web Manager Setup")]
[assembly: AssemblyProduct("DSH Web Manager")]
[assembly: AssemblyCompany("DSH Web Manager Community Build")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace DSHWebManagerSetup
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            var quiet = Array.Exists(args, value => value.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
            if (!quiet)
            {
                var result = MessageBox.Show(
                    "将为当前 Windows 用户安装 DSH Web Manager。\r\n\r\n安装不需要管理员权限，也不会修改或删除 .dsh 会话和工作区。",
                    "安装 DSH Web Manager", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                if (result != DialogResult.OK) return;
            }
            var temp = Path.Combine(Path.GetTempPath(), "dsh-web-manager-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                    archive.ExtractToDirectory(temp);
                var script = Path.Combine(temp, "Install.ps1");
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -SourceDir \"" + temp + "\"" + (quiet ? " -NoLaunch" : ""),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException("安装脚本退出码：" + process.ExitCode + "\r\n" + stderr + "\r\n" + stdout);
                if (!quiet) MessageBox.Show("安装完成。桌面和开始菜单中已添加 DSH Web Manager。", "DSH Web Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                try
                {
                    var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSHWebManager");
                    Directory.CreateDirectory(logDir);
                    File.WriteAllText(Path.Combine(logDir, "setup-error.log"), ex.ToString());
                }
                catch { }
                if (!quiet) MessageBox.Show("安装失败：" + ex.Message, "DSH Web Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }
            finally { try { Directory.Delete(temp, true); } catch { } }
        }
    }
}
