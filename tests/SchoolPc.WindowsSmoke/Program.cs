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
        ApplicationConfiguration.Initialize();
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
    private static void Click(Form form, string text) => Descendants(form).OfType<Button>().First(b => b.Text == text).PerformClick();
    private static async Task WaitUntil(Func<bool> predicate, int milliseconds = 10000)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        while (!predicate()) { if (Environment.TickCount64 >= deadline) throw new TimeoutException("Windows integration test timed out"); await Task.Delay(50); }
    }
    private async Task Run()
    {
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
        Click(agent, "연결 대기 시작");
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
            if (dialog != null) Click(dialog, "이 연결 허용");
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
        connection.Dispose(); await WaitUntil(() => Descendants(agent).OfType<Label>().Any(l => l.Text == "연결 대기 중"));
        Click(agent, "대기·제어 종료"); agent.Close();
        using (var panel = new SessionPanel(pairing)) { panel.CreateControl(); }
        Console.WriteLine("PASS Windows DPAPI, hooks, forms, real Admin/Agent TLS consent, credential rejection, commands, file transfer and disconnect");
    }
}
