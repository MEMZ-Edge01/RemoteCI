using System.IO.Compression;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RemoteCI.Server.Services;

/// <summary>GitHub 上发布的一个 release 及其附件。</summary>
public sealed record ReleaseInfo(
    string Tag,
    string Name,
    string Body,
    IReadOnlyList<ReleaseAsset> Assets);

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

public enum UpdateApplyMode
{
    ManagedByPlatform,
    InProcessContainer,
    ExternalInstaller,
}

public sealed record PreparedUpdate(
    string StagingDirectory,
    string ExtractedDirectory);

/// <summary>
/// 服务端更新：从 GitHub 仓库最新 release 拉取当前平台的服务端包，
/// Docker 内可就地覆盖后由重启策略拉起；Windows 和裸机 Linux 由独立更新进程在
/// 当前进程退出后替换文件并重新启动，避免 Windows 原生 DLL 文件锁。
/// </summary>
public sealed class UpdateService
{
    public const string FnosManagedMessage = "由fnOS应用商店管理";
    private const string Repo = "MEMZ-Edge01/RemoteCI";
    private const string LatestApiUrl = $"https://api.github.com/repos/{Repo}/releases/latest";
    private static readonly string UserAgent = $"RemoteCI-Server/{AppVersion.Version}";

    private static readonly HttpClient Http = CreateHttpClient();
    private readonly string[] _serverArguments;

    public UpdateService(string[]? serverArguments = null) => _serverArguments = serverArguments ?? [];

    /// <summary>
    /// 是否运行在飞牛 fnOS 应用环境。fpk 的 docker-compose 会注入
    /// <c>REMOTECI_RUNTIME=fnos</c>；该平台完全由 fnOS 应用商店管理更新。
    /// </summary>
    public static bool IsFnosRuntime =>
        string.Equals(
            Environment.GetEnvironmentVariable("REMOTECI_RUNTIME"),
            "fnos",
            StringComparison.OrdinalIgnoreCase);

    public static bool IsContainerRuntime =>
        string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    public static UpdateApplyMode DetermineApplyMode(bool isFnos, bool isContainer) =>
        isFnos
            ? UpdateApplyMode.ManagedByPlatform
            : isContainer
                ? UpdateApplyMode.InProcessContainer
                : UpdateApplyMode.ExternalInstaller;

    public static UpdateApplyMode CurrentApplyMode =>
        DetermineApplyMode(IsFnosRuntime, IsContainerRuntime);

    public string CurrentVersion => AppVersion.Version;

    /// <summary>拉取最新 release 元数据；仓库暂无 release 时返回 null。</summary>
    public async Task<ReleaseInfo?> FetchLatestReleaseAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApiUrl);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagElement)) return null;
        var assets = root.TryGetProperty("assets", out var assetsElement)
            ? assetsElement.EnumerateArray()
                .Select(asset => new ReleaseAsset(
                    asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "",
                    asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0))
                .Where(a => !string.IsNullOrEmpty(a.Name) && !string.IsNullOrEmpty(a.DownloadUrl))
                .ToList()
            : [];
        return new ReleaseInfo(
            tagElement.GetString() ?? "",
            root.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            assets);
    }

    /// <summary>挑选与当前运行平台匹配的服务端压缩包。</summary>
    public ReleaseAsset? SelectServerAsset(ReleaseInfo release)
    {
        var os = OperatingSystem.IsWindows() ? "win" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        var version = NormalizeVersion(release.Tag);
        return release.Assets.FirstOrDefault(asset =>
            asset.Name.StartsWith($"RemoteCI.Server-{version}-{os}-{arch}", StringComparison.OrdinalIgnoreCase) &&
            asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeVersion(string tag) => tag.TrimStart('v', 'V');

    /// <summary>比较语义版本：latest 更新时返回 true。</summary>
    public static bool IsNewer(string latest, string current)
    {
        var left = NormalizeVersion(latest).Split('-', '+')[0].Split('.').Select(p => int.TryParse(p, out var v) ? v : 0).ToArray();
        var right = NormalizeVersion(current).Split('-', '+')[0].Split('.').Select(p => int.TryParse(p, out var v) ? v : 0).ToArray();
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            var diff = (i < left.Length ? left[i] : 0) - (i < right.Length ? right[i] : 0);
            if (diff != 0) return diff > 0;
        }
        return false;
    }

    /// <summary>
    /// 下载并解压更新到数据库旁的暂存目录；此阶段不触碰运行文件。
    /// </summary>
    public async Task<PreparedUpdate> PrepareUpdateAsync(
        ReleaseInfo release,
        ReleaseAsset asset,
        string databasePath,
        string contentRoot,
        CancellationToken ct)
    {
        var updatesRoot = GetUpdatesRoot(databasePath, contentRoot);
        var staging = Path.Combine(updatesRoot, NormalizeVersion(release.Tag));
        Directory.CreateDirectory(staging);

        var archive = Path.Combine(staging, asset.Name);
        await DownloadAsync(asset.DownloadUrl, archive, ct);

        var extracted = Path.Combine(staging, "extracted");
        if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
        Directory.CreateDirectory(extracted);
        ZipFile.ExtractToDirectory(archive, extracted, overwriteFiles: true);

        var version = NormalizeVersion(release.Tag);
        ValidatePackageVersion(extracted, version);
        return new PreparedUpdate(staging, extracted);
    }

    /// <summary>启动对应平台的安全应用流程；成功返回后调用方应平滑退出当前服务端。</summary>
    public async Task<UpdateApplyMode> BeginApplyAsync(
        PreparedUpdate update,
        string contentRoot,
        CancellationToken ct)
    {
        var mode = CurrentApplyMode;
        if (mode == UpdateApplyMode.ManagedByPlatform)
            throw new InvalidOperationException(FnosManagedMessage);

        if (mode == UpdateApplyMode.InProcessContainer)
        {
            await UpdateInstaller.ApplyFilesAsync(update.ExtractedDirectory, contentRoot, ct);
            return mode;
        }

        StartExternalInstaller(update, contentRoot);
        return mode;
    }

    /// <summary>更新包统一存放在数据库同级目录的 updates 子目录。</summary>
    public static string GetUpdatesRoot(string databasePath, string contentRoot)
    {
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        return Path.Combine(dataDir is { Length: > 0 } ? dataDir : contentRoot, "updates");
    }

    private async Task DownloadAsync(string url, string target, CancellationToken ct)
    {
        var partial = target + ".part";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using (var output = File.Create(partial))
        {
            await input.CopyToAsync(output, ct);
        }
        File.Move(partial, target, overwrite: true);
    }

    private void StartExternalInstaller(PreparedUpdate update, string contentRoot)
    {
        var runnerAssembly = Path.Combine(update.ExtractedDirectory, "RemoteCI.Server.dll");
        if (!File.Exists(runnerAssembly))
            throw new InvalidDataException("更新包缺少 RemoteCI.Server.dll，无法启动外部安装器。");

        var planPath = Path.Combine(update.StagingDirectory, "install-plan.json");
        var plan = new UpdateInstallPlan
        {
            SourceDirectory = update.ExtractedDirectory,
            DestinationDirectory = Path.GetFullPath(contentRoot),
            DotnetHostPath = ResolveDotnetHost(),
            ServerAssemblyPath = Path.Combine(Path.GetFullPath(contentRoot), "RemoteCI.Server.dll"),
            ServerArguments = _serverArguments,
            ServerProcessId = Environment.ProcessId,
            LogPath = Path.Combine(update.StagingDirectory, "update.log"),
        };
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));

        var startInfo = new ProcessStartInfo(plan.DotnetHostPath)
        {
            WorkingDirectory = update.ExtractedDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(runnerAssembly);
        startInfo.ArgumentList.Add(UpdateInstaller.Command);
        startInfo.ArgumentList.Add(planPath);
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动外部更新器，运行文件未被修改。");
    }

    private static void ValidatePackageVersion(string extractedDirectory, string expectedVersion)
    {
        var assembly = Path.Combine(extractedDirectory, "RemoteCI.Server.dll");
        if (!File.Exists(assembly)) throw new InvalidDataException("更新包缺少 RemoteCI.Server.dll。");
        var actual = FileVersionInfo.GetVersionInfo(assembly).ProductVersion?.Split('+', 2)[0];
        if (!string.Equals(actual, expectedVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"更新包版本不匹配：Release 为 v{expectedVersion}，包内服务端为 v{actual ?? "未知"}。");
    }

    private static string ResolveDotnetHost()
    {
        var current = Environment.ProcessPath;
        if (current is not null && string.Equals(
                Path.GetFileNameWithoutExtension(current), "dotnet", StringComparison.OrdinalIgnoreCase))
            return current;

        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate)) return candidate;
        }

        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var candidate = Path.Combine(programFiles, "dotnet", "dotnet.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return "dotnet";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
