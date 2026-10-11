using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using SchoolPc.Core;

namespace SchoolPc.Windows;

public static class Removal
{
    internal static string RoleFiles(string assembly) => assembly switch
    {
        "SchoolPc.Admin" => "admin.profiles,admin-audit.log",
        "SchoolPc.Agent" => "agent.settings,agent.certificate,agent-audit.log",
        _ => throw new InvalidDataException("제거할 프로그램을 확인하지 못했습니다.")
    };
    public static async Task RequestAsync(Form owner, Func<Task> shutdown)
    {
        try
        {
            string assembly = Assembly.GetEntryAssembly()!.GetName().Name!;
            _ = RoleFiles(assembly);
            string current = Environment.ProcessPath!;
            if (!string.Equals(Path.GetFileName(current), assembly + ".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("릴리스에서 받은 원래 이름의 EXE로 실행해 주세요.");
            if (MessageBox.Show(owner, $"현재 프로그램을 종료하고 아래 파일을 제거할까요?\n\n{current}\n\n이 프로그램의 등록·인증 설정과 로그도 지웁니다.\n다른 역할의 프로그램, 받은 파일, Wi-Fi 정보는 그대로 둡니다.", "현재 프로그램 제거", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            string uninstaller = Path.Combine(Path.GetDirectoryName(current)!, "unins000.exe");
            if (File.Exists(Path.Combine(Path.GetDirectoryName(current)!, "installed.ini")) && File.Exists(uninstaller))
            {
                await shutdown();
                var uninstall = new ProcessStartInfo(uninstaller) { UseShellExecute = true };
                // The uninstaller waits for the app mutex; release it by exiting this process.
                _ = Process.Start(uninstall) ?? throw new IOException("제거 프로그램을 시작하지 못했습니다.");
                Application.Exit(); return;
            }
            string folder = Path.Combine(Path.GetTempPath(), "SchoolPcRemove", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            string helper = Path.Combine(folder, assembly + ".exe"); File.Copy(current, helper);
            string hash; using (var file = File.OpenRead(helper)) hash = Convert.ToHexString(SHA256.HashData(file));
            var start = new ProcessStartInfo(helper) { UseShellExecute = false };
            foreach (string arg in new[] { "--remove-app", Environment.ProcessId.ToString(), current, hash }) start.ArgumentList.Add(arg);
            await shutdown(); _ = Process.Start(start) ?? throw new IOException("제거 도우미를 시작하지 못했습니다."); Application.Exit();
        }
        catch (Exception e) { Ui.Error(owner, e); }
    }
    public static bool ApplyIfRequested(string[] args)
    {
        if (args.Length == 0 || args[0] != "--remove-app") return false;
        try
        {
            string assembly = Assembly.GetEntryAssembly()!.GetName().Name!;
            string files = RoleFiles(assembly);
            if (args.Length != 4 || !int.TryParse(args[1], out int pid) || !Security.ValidHash(args[3])) throw new InvalidDataException("잘못된 제거 요청입니다.");
            string target = Path.GetFullPath(args[2]), helper = Path.GetFullPath(Environment.ProcessPath!);
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SchoolPcRemove")) + Path.DirectorySeparatorChar;
            if (!helper.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(target), assembly + ".exe", StringComparison.OrdinalIgnoreCase) || target == helper)
                throw new InvalidDataException("제거 경로를 확인하지 못했습니다.");
            using (var input = File.OpenRead(helper)) if (!Security.EqualHash(Security.Hash(File.ReadAllBytes(target)), Convert.ToHexString(SHA256.HashData(input))) || !Security.EqualHash(args[3], Security.Hash(File.ReadAllBytes(target)))) throw new InvalidDataException("프로그램이 변경되어 제거하지 않았습니다.");
            try { using var parent = Process.GetProcessById(pid); if (!parent.WaitForExit(30000)) throw new IOException("프로그램이 아직 실행 중입니다."); } catch (ArgumentException) { }
            // Delete exact, role-specific paths. Do not recursively remove the installation folder.
            if (assembly == "SchoolPc.Agent") StartupRegistration.SetEnabled(false);
            File.Delete(target); File.Delete(target + ".previous");
            foreach (string name in files.Split(',')) { File.Delete(Storage.PathFor(name)); File.Delete(Storage.PathFor(name) + ".new"); }
            MessageBox.Show("현재 프로그램과 해당 등록·인증 설정을 제거했습니다.\n다운로드한 ZIP, 전달용 등록 파일, 임시 제거 도우미 사본은 별도로 삭제할 수 있습니다.", "제거 완료");
        }
        catch (Exception e) { MessageBox.Show("제거를 완료하지 못했습니다.\n" + e.Message, "제거 오류"); }
        return true;
    }
}
