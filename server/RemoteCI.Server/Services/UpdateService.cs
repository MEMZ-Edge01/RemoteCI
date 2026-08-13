using System.IO.Compression;
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

/// <summary>
/// 服务端更新：从 GitHub 仓库最新 release 拉取当前平台的服务端包，
/// 解压后就地覆盖运行目录，再触发进程退出由宿主（Docker restart 策略）重启。
/// </summary>
public sealed class UpdateService
{
    private const string Repo = "MEMZ-Edge01/RemoteCI";
    private const string LatestApiUrl = $"https://api.github.com/repos/{Repo}/releases/latest";
    private static readonly string UserAgent = $"RemoteCI-Server/{AppVersion.Version}";

    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>
    /// 是否运行在飞牛 fnOS 应用环境。fpk 的 docker-compose 会注入
    /// <c>REMOTECI_RUNTIME=fnos</c>；该模式下更新流程改为下载 fpk 安装包，
    /// 由用户在飞牛应用中心完成安装升级。
    /// </summary>
    public static bool IsFnosRuntime =>
        string.Equals(
            Environment.GetEnvironmentVariable("REMOTECI_RUNTIME"),
            "fnos",
            StringComparison.OrdinalIgnoreCase);

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

    /// <summary>挑选 fnOS 平台对应的 fpk 安装包（RemoteCI-&lt;版本&gt;.fpk）。</summary>
    public ReleaseAsset? SelectFnosAsset(ReleaseInfo release)
    {
        var version = NormalizeVersion(release.Tag);
        return release.Assets.FirstOrDefault(asset =>
            asset.Name.Equals($"RemoteCI-{version}.fpk", StringComparison.OrdinalIgnoreCase));
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
    /// 下载并应用更新。返回后由调用方在响应送达后触发进程退出。
    /// 运行目录不可写（例如 Windows 直接运行）时抛出带说明的异常。
    /// </summary>
    public async Task ApplyUpdateAsync(
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

        // 就地覆盖运行目录；Linux 容器内允许替换已加载的程序集，
        // 进程退出后由 Docker restart 策略以新文件重新启动。
        CopyDirectory(extracted, contentRoot);
    }

    /// <summary>
    /// 下载 fnOS 安装包（.fpk）到数据卷的 updates 目录，供管理员从
    /// 飞牛应用中心手动安装。fnOS 当前没有允许容器内应用自我安装的开放接口，
    /// 因此安装确认这一步需要在应用中心完成。
    /// </summary>
    public async Task<string> DownloadFnosFpkAsync(
        ReleaseInfo release,
        ReleaseAsset asset,
        string databasePath,
        string contentRoot,
        CancellationToken ct)
    {
        var updatesRoot = GetUpdatesRoot(databasePath, contentRoot);
        Directory.CreateDirectory(updatesRoot);
        // 防止 release 附件名携带路径片段；只保留文件名。
        var safeName = Path.GetFileName(asset.Name);
        var target = Path.Combine(updatesRoot, safeName);
        if (File.Exists(target))
        {
            return target;
        }

        var partial = target + ".part";
        await DownloadAsync(asset.DownloadUrl, partial, ct);
        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>更新包统一存放在数据库同级目录的 updates 子目录。</summary>
    public static string GetUpdatesRoot(string databasePath, string contentRoot)
    {
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        return Path.Combine(dataDir is { Length: > 0 } ? dataDir : contentRoot, "updates");
    }

    private async Task DownloadAsync(string url, string target, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(target);
        await input.CopyToAsync(output, ct);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // 运行中文件被占用时最多重试 10 次；Windows 下仍失败则向上抛出。
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Copy(file, target, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(200);
                }
            }
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
