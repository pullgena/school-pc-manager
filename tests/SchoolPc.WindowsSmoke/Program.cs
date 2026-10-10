using SchoolPc.Admin;
using SchoolPc.Agent;
using SchoolPc.Windows;
using SchoolPc.Core;
internal static class Program
{
    [STAThread] static void Main()
    {
        ApplicationConfiguration.Initialize();
        byte[] secret = [0, 1, 2, 3, 255];
        if (!UserSecret.Unprotect(UserSecret.Protect(secret)).SequenceEqual(secret)) throw new Exception("DPAPI round trip failed");
        using (var input = new NativeInput()) { input.SetLocks(false, false); input.Unlock(); }
        using (var admin = new AdminForm()) { admin.Show(); Application.DoEvents(); admin.Close(); }
        using (var agent = new AgentForm()) { agent.Show(); Application.DoEvents(); agent.Close(); Application.DoEvents(); }
        using (var panel = new SessionPanel(new("test", "127.0.0.1", 45832, new string('A', 64), new string('B', 64)))) { panel.CreateControl(); }
        Console.WriteLine("PASS Windows DPAPI, input hook lifecycle, Admin/Agent form initialization");
    }
}
