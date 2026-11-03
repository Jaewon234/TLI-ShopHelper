using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using static ShopHelper.Native;

namespace ShopHelper;

/// <summary>
/// 자동 업데이트 (TartarusSolver 와 같은 방식).
///  1) GitHub 의 최신 릴리스를 확인 (켤 때 + 6시간마다)
///  2) 새 버전이면 알림 → 누르면 ShopHelper.zip 을 받아 GitHub 이 알려주는 SHA-256 으로 확인 후 압축 해제
///  3) 새 버전을 "--install-update 설치폴더 pid" 로 실행하고 지금 버전은 종료
///  4) 새 버전이 이전 버전 종료를 기다렸다가 설치 폴더에 자기를 복사하고 그 자리에서 다시 시작
/// 시세(prices.json)와 설정은 %AppData%\ShopHelper 에 있어서 그대로 남음.
/// </summary>
static class Updater
{
    public const string AssetName = "ShopHelper.zip";
    public sealed record Release(Version Ver, string Tag, string Page, string? Url, long Size, string? Sha256);

    static readonly string Staging = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShopHelper", "update");

    static readonly HttpClient Http = CreateHttp();
    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("ShopHelper-Updater");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    public static Version Current => Version.Parse(App.Version);

    /// <summary>릴리스 zip 을 푼 폴더(사용법.txt 가 옆에 있음)에서 실행 중일 때만 자기 자신을 업데이트.</summary>
    public static bool IsReleaseInstall =>
        File.Exists(Path.Combine(App.Dir, "사용법.txt")) && !App.Dir.StartsWith(Staging, StringComparison.OrdinalIgnoreCase);

    /// <summary>최신 릴리스. 저장소가 비공개이거나 릴리스가 없으면 예외.</summary>
    public static async Task<Release> Latest(string repo)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"{repo} 에서 릴리스를 찾지 못함 (저장소가 비공개이거나 릴리스가 없음)");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var ver = Version.Parse(tag.TrimStart('v', 'V'));
        string page = root.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : "";
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is string ds && ds.StartsWith("sha256:") ? ds[7..] : null;
            return new Release(ver, tag, page, a.GetProperty("browser_download_url").GetString(), a.GetProperty("size").GetInt64(), sha);
        }
        return new Release(ver, tag, page, null, 0, null);
    }

    /// <summary>zip 을 받아 확인하고 풀어서, 새 ShopHelper.exe 가 있는 폴더를 돌려줌.</summary>
    public static async Task<string> Download(Release r)
    {
        if (r.Url == null) throw new InvalidOperationException($"릴리스 {r.Tag} 에 {AssetName} 이 없음");
        if (r.Sha256 == null) throw new InvalidOperationException($"릴리스 {r.Tag} 의 {AssetName} 에 확인값(SHA-256)이 없음");
        if (Directory.Exists(Staging)) Directory.Delete(Staging, true);
        Directory.CreateDirectory(Staging);
        string zip = Path.Combine(Staging, AssetName);
        using (var resp = await Http.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var file = File.Create(zip);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[1 << 16];
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                hash.AppendData(buf, 0, n);
                await file.WriteAsync(buf.AsMemory(0, n));
            }
            string got = Convert.ToHexString(hash.GetHashAndReset());
            if (!got.Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("받은 파일의 확인값이 다름 (다운로드가 깨졌거나 바뀜)");
        }
        string unpacked = Path.Combine(Staging, "unpacked");
        ZipFile.ExtractToDirectory(zip, unpacked);
        File.Delete(zip);
        var exe = Directory.EnumerateFiles(unpacked, "ShopHelper.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidDataException("zip 안에 ShopHelper.exe 가 없음");
        return Path.GetDirectoryName(exe)!;
    }

    /// <summary>새 버전을 설치 모드로 실행 (지금 버전은 이 다음에 종료해야 함).</summary>
    public static void Launch(string newDir)
    {
        string target = App.Dir.TrimEnd(Path.DirectorySeparatorChar);
        Process.Start(new ProcessStartInfo(Path.Combine(newDir, "ShopHelper.exe"))
        {
            ArgumentList = { "--install-update", target, Environment.ProcessId.ToString() },
            UseShellExecute = false,
            WorkingDirectory = newDir,
        });
    }

    /// <summary>
    /// "--install-update 설치폴더 pid": 새 버전(임시 폴더)에서 실행됨. 이전 버전이 끝나길 기다린 뒤
    /// 자기 파일을 설치 폴더에 복사하고 그 자리에서 다시 시작.
    /// </summary>
    public static int InstallHere(string target, int pid)
    {
        App.Log($"업데이트 설치: {App.Dir} → {target} (이전 pid {pid})");
        try
        {
            try { using var old = Process.GetProcessById(pid); old.WaitForExit(60_000); } catch (ArgumentException) { }
            string src = App.Dir;
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(src, file);
                if (rel.Equals("helper.log", StringComparison.OrdinalIgnoreCase)) continue;
                string dst = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Copy(file, dst, overwrite: true); break; }
                    catch (IOException) when (attempt < 20) { Thread.Sleep(500); }   // 이전 exe 가 아직 잠겨 있으면 잠시 기다림
                }
            }
            Process.Start(new ProcessStartInfo(Path.Combine(target, "ShopHelper.exe")) { UseShellExecute = false, WorkingDirectory = target });
            return 0;
        }
        catch (Exception e)
        {
            App.Log("업데이트 설치 실패: " + e);
            App.Error("업데이트를 설치하지 못했습니다.\n\n" + e.Message +
                      "\n\n열리는 폴더의 파일을 설치 폴더에 직접 복사해 주세요:\n" + target);
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{App.Dir}\"") { UseShellExecute = true }); } catch { }
            return 1;
        }
    }

    /// <summary>지난 업데이트가 남긴 임시 파일 정리 (그 폴더에서 실행 중이 아닐 때).</summary>
    public static void Cleanup()
    {
        try
        {
            if (Directory.Exists(Staging) && !App.Dir.StartsWith(Staging, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(Staging, true);
        }
        catch { }
    }
}
