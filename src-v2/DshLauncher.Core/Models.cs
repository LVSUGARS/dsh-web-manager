namespace DshLauncher.Core;

public enum DshState
{
    Detecting,
    NotInstalled,
    Stopped,
    Starting,
    Running,
    ExternalService,
    Error
}

public sealed class AppConfig
{
    public int Version { get; set; } = 2;
    public string Workspace { get; set; } = string.Empty;
    public int Port { get; set; } = 3080;
    public string Language { get; set; } = "zh";
    public string Theme { get; set; } = "system";
    public string CloseBehavior { get; set; } = "ask";
    public bool AutoOpenBrowser { get; set; } = true;
    public bool AutoCheckLauncherUpdates { get; set; } = true;
    public bool StartAtSignIn { get; set; }
}

public sealed record DshInstallation(string NodePath, string CliPath, string Version, bool Managed);

public sealed record RuntimeState(
    int Pid,
    long StartUtcTicks,
    string Workspace,
    string NodePath,
    string CliPath,
    int Port,
    string StdoutLog,
    string StderrLog);

public sealed record DshStatus(
    DshState State,
    string Message,
    int ListenerPid,
    DshInstallation? Installation,
    string Url,
    bool Healthy,
    bool Managed);

public sealed record OperationResult(bool Ok, string Message)
{
    public static OperationResult Success(string message) => new(true, message);
    public static OperationResult Fail(string message) => new(false, message);
}

public sealed record LauncherUpdateInfo(
    bool IsAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseName,
    string ReleaseNotes,
    string HtmlUrl,
    string? PackageUrl,
    string? PackageName,
    string? ChecksumUrl);
