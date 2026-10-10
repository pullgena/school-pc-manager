using SchoolPc.Windows;
namespace SchoolPc.Admin;
internal static class Program
{
    [STAThread] private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (Updater.ApplyIfRequested(args) || Removal.ApplyIfRequested(args)) return;
        using var single = new Mutex(true, "Local\\SchoolPc.Admin", out bool first);
        if (!first) { MessageBox.Show("관리자 프로그램이 이미 실행 중입니다."); return; }
        try { Application.Run(new AdminForm()); }
        catch (Exception e) { MessageBox.Show(e.Message, "School PC 관리자 오류"); }
    }
}
