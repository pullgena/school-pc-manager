using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SchoolPc.Core;

namespace SchoolPc.Windows;

public static class Updater
{
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SchoolPcManager/0.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
    public static Version Current => Assembly.GetEntryAssembly()!.GetName().Version!;
    public static async Task CheckAsync(Form owner, Func<Task> beforeExit)
    {
        try
        {
            string name = Assembly.GetEntryAssembly()!.GetName().Name + ".exe";
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            using var response = await Client.GetAsync($"https://api.github.com/repos/{ReleaseInfo.Repository}/releases/latest", deadline.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                MessageBox.Show(owner, "아직 배포된 정식 릴리스가 없습니다.", "업데이트"); return;
            }
            response.EnsureSuccessStatusCode();
            var release = ReleaseInfo.Parse(await response.Content.ReadAsStringAsync(deadline.Token), name);
            // Assembly version carries a fourth revision; compare semantic major.minor.patch only.
            var current = new Version(Current.Major, Current.Minor, Current.Build);
            if (ReleaseInfo.ParseVersion(release.Version) <= current) { MessageBox.Show(owner, $"최신 버전입니다. ({current})", "업데이트"); return; }
            if (MessageBox.Show(owner, $"{current} → {release.Version}\n\n{release.Notes[..Math.Min(1200, release.Notes.Length)]}\n\n다운로드 후 프로그램을 종료하고 업데이트할까요?", "업데이트", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            string folder = Storage.PathFor("Updates"); Directory.CreateDirectory(folder);
            string stage = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".exe");
            using (var download = await Client.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, deadline.Token))
            {
                download.EnsureSuccessStatusCode();
                await using var source = await download.Content.ReadAsStreamAsync(deadline.Token);
                await using var target = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] bytes = new byte[128 * 1024]; long total = 0; int count;
                while ((count = await source.ReadAsync(bytes, deadline.Token)) > 0)
                {
                    total += count; if (total > 350L * 1024 * 1024) throw new InvalidDataException("업데이트 파일이 너무 큽니다.");
                    await target.WriteAsync(bytes.AsMemory(0, count), deadline.Token);
                }
            }
            await using (var verify = File.OpenRead(stage))
            {
                if (!Security.EqualHash(Convert.ToHexString(await SHA256.HashDataAsync(verify, deadline.Token)), release.Hash))
                { File.Delete(stage); throw new InvalidDataException("업데이트 파일의 SHA-256이 일치하지 않습니다."); }
            }
            string installed = Environment.ProcessPath!;
            if (!installed.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"업데이트는 릴리스에서 받은 {name} 파일로 실행할 때 사용할 수 있습니다.");
            // Only replace this user's writable executable. Never request elevation or run a shell script.
            string probe = Path.Combine(Path.GetDirectoryName(installed)!, ".schoolpc-write-test-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe)) { } File.Delete(probe);
            var start = new ProcessStartInfo(stage) { UseShellExecute = false };
            foreach (string arg in new[] { "--apply-update", Environment.ProcessId.ToString(), installed, stage, release.Hash }) start.ArgumentList.Add(arg);
            await beforeExit();
            _ = Process.Start(start) ?? throw new IOException("업데이트 도우미를 시작하지 못했습니다.");
            Application.Exit();
        }
        catch (Exception e) { Ui.Error(owner, e); }
    }
    public static bool ApplyIfRequested(string[] args)
    {
        if (args.Length == 0 || args[0] != "--apply-update") return false;
        string target = "", backup = ""; bool replaced = false;
        try
        {
            if (args.Length != 5 || !int.TryParse(args[1], out int pid) || !Security.ValidHash(args[4])) throw new InvalidDataException("잘못된 업데이트 요청입니다.");
            target = Path.GetFullPath(args[2]); string source = Path.GetFullPath(args[3]);
            string exeName = Assembly.GetEntryAssembly()!.GetName().Name + ".exe";
            if (!string.Equals(Path.GetFileName(target), exeName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(source, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ||
                !source.StartsWith(Path.GetFullPath(Storage.PathFor("Updates")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                source == target) throw new InvalidDataException("업데이트 경로를 검증하지 못했습니다.");
            using (var input = File.OpenRead(source)) if (!Security.EqualHash(Convert.ToHexString(SHA256.HashData(input)), args[4])) throw new InvalidDataException("검증에 실패했습니다.");
            try { using var parent = Process.GetProcessById(pid); if (!parent.WaitForExit(30_000)) throw new IOException("이전 프로그램이 종료되지 않았습니다."); }
            catch (ArgumentException) { }
            backup = target + ".previous"; File.Copy(target, backup, true);
            File.Copy(source, target, true); replaced = true;
            _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target)! }) ?? throw new IOException("새 버전을 시작하지 못했습니다.");
        }
        catch (Exception e)
        {
            if (replaced && File.Exists(backup)) { try { File.Copy(backup, target, true); } catch (IOException) { } }
            Storage.Audit("update", e.Message);
            MessageBox.Show("업데이트를 완료하지 못했습니다. 기존 파일 또는 .previous 백업을 실행해 주세요.\n" + e.Message, "업데이트 오류");
        }
        return true;
    }
}
