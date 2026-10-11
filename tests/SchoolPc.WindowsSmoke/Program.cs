using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SchoolPc.Admin;
using SchoolPc.Agent;
using SchoolPc.Windows;
using SchoolPc.Core;

internal static class Program
{
    [STAThread] static int Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Storage.TestRoot = Path.Combine(Path.GetTempPath(), "SchoolPcSmoke-" + Guid.NewGuid().ToString("N"));
        using var runner = new TestRunner(); Application.Run(runner);
        try { Directory.Delete(Storage.TestRoot, true); } catch (IOException) { }
        return runner.Result;
    }
}
internal sealed class TestRunner : Form
{
    internal int Result = 1;
    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            await Run(); Result = 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); }
        finally { Close(); }
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls) { yield return child; foreach (var inner in Descendants(child)) yield return inner; }
    }
    private static void ClickButton(Control form, string text) => Descendants(form).OfType<Button>().First(b => b.Text == text).PerformClick();
    private static async Task WaitUntil(Func<bool> predicate, int milliseconds = 10000)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        while (!predicate()) { if (Environment.TickCount64 >= deadline) throw new TimeoutException("Windows integration test timed out"); await Task.Delay(50); }
    }
    private async Task Run()
    {
        if (System.Runtime.InteropServices.Marshal.SizeOf<WifiManager.InterfaceInfo>() != 532 || System.Runtime.InteropServices.Marshal.SizeOf<WifiManager.ProfileInfo>() != 516) throw new Exception("WLAN ABI mismatch");
        if (Removal.RoleFiles("SchoolPc.Admin").Contains("agent") || Removal.RoleFiles("SchoolPc.Agent").Contains("admin")) throw new Exception("Removal roles overlap");
        using (var wifi = new WifiForm()) { wifi.CreateControl(); } // Never mutate runner network profiles.
        byte[] secret = [0, 1, 2, 3, 255];
        if (!UserSecret.Unprotect(UserSecret.Protect(secret)).SequenceEqual(secret)) throw new Exception("DPAPI round trip failed");
        using (var input = new NativeInput()) { input.SetLocks(false, false); input.Unlock(); }
        using (var admin = new AdminForm())
        {
            admin.Show(); await Task.Delay(100);
            Directory.CreateDirectory("out/previews"); using var bitmap = new Bitmap(admin.Width, admin.Height); admin.DrawToBitmap(bitmap, admin.ClientRectangle); bitmap.Save("out/previews/admin.png"); admin.Close();
        }
        var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start(); int port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        var settings = new AgentSettings("CI 교실 PC", port, Convert.ToHexString(RandomNumberGenerator.GetBytes(32))); Storage.Save("agent.settings", settings);
        using var agent = new AgentForm(); agent.Show(); await Task.Delay(100);
        using (var bitmap = new Bitmap(agent.Width, agent.Height)) { agent.DrawToBitmap(bitmap, agent.ClientRectangle); bitmap.Save("out/previews/agent.png"); }
        ClickButton(agent, "연결 대기 시작");
        using var cert = X509CertificateLoader.LoadPkcs12(UserSecret.Unprotect(File.ReadAllBytes(Storage.PathFor("agent.certificate"))), "", X509KeyStorageFlags.EphemeralKeySet);
        var pairing = new Pairing(settings.Name, "127.0.0.1", port, Security.Hash(cert.RawData), settings.Token);
        using (var bad = new AdminConnection(pairing with { Token = new string('A', 64) }))
        {
            bool rejected = false; try { await bad.ConnectAsync(); } catch { rejected = true; }
            if (!rejected) throw new Exception("Agent accepted invalid credentials");
        }
        // Allow the rejected handler's finally block to release the one-session admission gate.
        await Task.Delay(200);
        using var approval = new System.Windows.Forms.Timer { Interval = 50 };
        approval.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text == "관리자 연결 승인");
            if (dialog != null) ClickButton(dialog, "이 연결 허용");
        };
        approval.Start();
        using var connection = new AdminConnection(pairing);
        await connection.ConnectAsync(); approval.Stop();
        if (!connection.Connected) throw new Exception("Admin/Agent TLS connection failed");
        await connection.SendAsync(new("message", Text: "CI 왕복 메시지"));
        await WaitUntil(() => Descendants(agent).OfType<TextBox>().Any(t => t.Text.Contains("CI 왕복 메시지")));
        // Uses the real agent command handler without injecting keyboard/mouse events into the runner.
        await connection.SendAsync(new("control", Enabled: true));
        await connection.SendAsync(new("locks", X: 0, Y: 0, Value: 1));
        await connection.SendAsync(new("unlock")); await connection.SendAsync(new("control", Enabled: false));
        string file = Storage.PathFor("smoke-source.bin"); byte[] data = RandomNumberGenerator.GetBytes(133001); File.WriteAllBytes(file, data);
        bool confirmed = false;
        connection.Received += p => { if (p.Type == "notice" && p.Text.Contains("받은 파일 폴더")) confirmed = true; };
        await connection.SendFileAsync(file); await WaitUntil(() => confirmed);
        string inbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SchoolPc 받은 파일");
        string received = Directory.GetFiles(inbox, "smoke-source-*.bin").Single();
        if (!File.ReadAllBytes(received).SequenceEqual(data)) throw new Exception("Agent file bytes changed");
        File.Delete(received);
        // Fullscreen reparenting must retain the live viewport after returning.
        using (var panel = new SessionPanel(pairing))
        {
            Controls.Add(panel); panel.Show();
            var viewport = Descendants(panel).OfType<ScreenView>().Single();
            ClickButton(panel, "전체 화면"); await Task.Delay(100);
            var fullscreen = Application.OpenForms.Cast<Form>().Single(f => f.Text == "원격 화면 · F11로 돌아가기");
            if (!Descendants(fullscreen).Contains(viewport)) throw new Exception("Fullscreen did not move viewport");
            fullscreen.Close();
            if (!Descendants(panel).Contains(viewport) || viewport.IsDisposed) throw new Exception("Fullscreen disposed viewport");
            Controls.Remove(panel);
        }
        byte[] frame = DesktopCapture.Jpeg(blank: true);
        bool sharedAck = false;
        connection.Received += p => { if (p.Type == "broadcast-ack") sharedAck = true; };
        await connection.SendAsync(new("broadcast-start", Enabled: false));
        await connection.SendAsync(new("broadcast-frame", Data: frame));
        await WaitUntil(() => sharedAck);
        var shared = Application.OpenForms.Cast<Form>().Single(f => f.Text.StartsWith("관리자 화면 공유"));
        if (!Descendants(shared).OfType<PictureBox>().Any(p => p.Image != null)) throw new Exception("Shared frame was not displayed");
        await connection.SendAsync(new("broadcast-stop")); await WaitUntil(() => shared.IsDisposed);
        await connection.SendAsync(new("broadcast-start", Enabled: true));
        await WaitUntil(() => Application.OpenForms.Cast<Form>().Any(f => f.Text.StartsWith("관리자 화면 공유")));
        var exclusive = Application.OpenForms.Cast<Form>().Single(f => f.Text.StartsWith("관리자 화면 공유"));
        if (exclusive.Bounds != Screen.PrimaryScreen!.Bounds || exclusive.FormBorderStyle != FormBorderStyle.None || !exclusive.TopMost) throw new Exception("Exclusive share did not cover the primary monitor");
        await connection.SendAsync(new("broadcast-stop")); await WaitUntil(() => exclusive.IsDisposed);
        await connection.SendAsync(new("screen-lock", Enabled: true));
        await WaitUntil(() => Application.OpenForms.Cast<Form>().Any(f => Descendants(f).OfType<Label>().Any(l => l.Text.StartsWith("관리자 화면 잠금"))));
        await connection.SendAsync(new("unlock"));
        await WaitUntil(() => !Application.OpenForms.Cast<Form>().Any(f => Descendants(f).OfType<Label>().Any(l => l.Text.StartsWith("관리자 화면 잠금"))));
        connection.Dispose(); await WaitUntil(() => Descendants(agent).OfType<Label>().Any(l => l.Text == "연결 대기 중"));
        // The first approval remembered this registration; subsequent connections must not show a dialog.
        await Task.Delay(200);
        using (var automatic = new AdminConnection(pairing))
        {
            await automatic.ConnectAsync();
            if (Application.OpenForms.Cast<Form>().Any(f => f.Text == "관리자 연결 승인")) throw new Exception("Remembered admin prompted again");
            if (!Storage.Load<AgentSettings>("agent.settings")!.AutoApprove) throw new Exception("Approval was not persisted");
        }
        await WaitUntil(() => Descendants(agent).OfType<Label>().Any(l => l.Text == "연결 대기 중"));
        await Task.Delay(200);
        using (var invalidRemembered = new AdminConnection(pairing with { Token = new string('B', 64) }))
        {
            bool rejected = false; try { await invalidRemembered.ConnectAsync(); } catch { rejected = true; }
            if (!rejected) throw new Exception("Automatic approval bypassed credential validation");
        }
        agent.Close();
        if (agent.Visible || agent.IsDisposed) throw new Exception("Close did not keep agent in tray");
        await Task.Delay(200);
        using (var background = new AdminConnection(pairing)) { await background.ConnectAsync(); }
        agent.Show();
        ClickButton(agent, "대기·제어 종료"); await agent.ExitAsync();
        using (var startupAgent = new AgentForm(startInTray: true))
        {
            startupAgent.Show(); await Task.Delay(200);
            if (startupAgent.Visible) throw new Exception("Startup did not hide agent in tray");
            using (var startupConnection = new AdminConnection(pairing)) { await startupConnection.ConnectAsync(); }
            await startupAgent.ExitAsync();
        }
        using (var panel = new SessionPanel(pairing)) { panel.CreateControl(); }
        Console.WriteLine("PASS Windows DPAPI, hooks, forms, real Admin/Agent TLS consent, credential rejection, commands, file transfer and disconnect");
    }
}
