using Microsoft.Win32;

namespace SchoolPc.Windows;

public static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "SchoolPc.Agent";
    public static bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue(Name) is string; }
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled)
        {
            string exe = Environment.ProcessPath ?? throw new IOException("실행 경로가 없습니다.");
            if (!string.Equals(Path.GetFileName(exe), "SchoolPc.Agent.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("관리 대상 릴리스 EXE에서만 자동 실행을 설정할 수 있습니다.");
            key.SetValue(Name, $"\"{exe}\" --tray", RegistryValueKind.String);
        }
        else key.DeleteValue(Name, false);
    }
}
