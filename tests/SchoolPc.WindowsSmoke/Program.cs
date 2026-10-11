using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Runtime.InteropServices;
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
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
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
    private static async Task TestChosenReceiveFolder(Pairing pairing, X509Certificate2 cert, string file, byte[] data)
    {
        string folder = Storage.PathFor("chosen-inbox");
        Storage.Save("admin.receive-folders", new Dictionary<string, string> { [pairing.Fingerprint] = folder });
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sending = Task.Run(async () =>
        {
            using var tcp = await server.AcceptTcpClientAsync();
            using var tls = new SslStream(tcp.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });
            using var wire = new SchoolPc.Core.Channel(tls);
            var hello = await wire.ReadAsync(CancellationToken.None);
            if (hello.Type != "hello" || hello.Text != pairing.Token) throw new Exception("Fixture handshake failed");
            await wire.SendAsync(new("accepted", Value: 1), CancellationToken.None);
            // A target-initiated submission has no preceding request-file command.
            await FileSender.SendAsync(wire, file, CancellationToken.None);
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
        });
        using var receiving = new AdminConnection(pairing with { Port = ((IPEndPoint)server.LocalEndpoint).Port });
        if (receiving.ReceiveFolder != folder) throw new Exception("Per-PC receive folder was not restored");
        string? saved = null;
        receiving.Received += p => { if (p.Type == "notice" && p.Text.StartsWith("파일 저장 완료: ")) saved = p.Text["파일 저장 완료: ".Length..]; };
        try
        {
            await receiving.ConnectAsync(); await WaitUntil(() => saved != null);
            if (Path.GetDirectoryName(saved!) != folder || !File.ReadAllBytes(saved!).SequenceEqual(data)) throw new Exception("Target submission did not use the chosen folder");
        }
        finally { release.TrySetResult(); }
        await sending;
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
        // Character delivery is checked against an explicitly focused, disposable test textbox.
        using (var target = new Form { Text = "CI input target" })
        using (var text = new TextBox { Dock = DockStyle.Fill, Multiline = true })
        using (var viewport = new ScreenView { RemoteInput = true })
        {
            target.Controls.Add(text); target.Show(); SetForegroundWindow(target.Handle); target.Activate(); text.Focus();
            await WaitUntil(() => text.Focused);
            await connection.SendAsync(new("control", Enabled: true));
            string? routed = null; int rawKeys = 0;
            viewport.TextRemote += value => routed = value;
            viewport.KeyRemote += (_, _) => rawKeys++;
            SendMessage(viewport.Handle, 0x100, (IntPtr)Keys.A, IntPtr.Zero);
            SendMessage(viewport.Handle, 0x102, (IntPtr)'a', IntPtr.Zero);
            SendMessage(viewport.Handle, 0x101, (IntPtr)Keys.A, IntPtr.Zero);
            if (routed != "a" || rawKeys != 0) throw new Exception("Character routing duplicated or dropped English input");
            SendMessage(viewport.Handle, 0x100, (IntPtr)Keys.F1, IntPtr.Zero);
            SendMessage(viewport.Handle, 0x101, (IntPtr)Keys.F1, IntPtr.Zero);
            if (rawKeys != 2) throw new Exception("Non-character key routing failed");
            await connection.SendAsync(new("text", Text: "aAzZ09 !?"));
            await WaitUntil(() => text.Text == "aAzZ09 !?");
            await connection.SendAsync(new("control", Enabled: true, X: 1));
            await WaitUntil(() => agent.LocalMouseLocked && agent.LocalKeyboardLocked);
            await connection.SendAsync(new("text", Text: "B"));
            await WaitUntil(() => text.Text == "aAzZ09 !?B");
            // Stopping screen sharing must not clear the independent remote-control lock.
            await connection.SendAsync(new("broadcast-stop"));
            await connection.SendAsync(new("text", Text: "C"));
            await WaitUntil(() => text.Text == "aAzZ09 !?BC");
            if (!agent.LocalMouseLocked || !agent.LocalKeyboardLocked) throw new Exception("Screen-share stop cleared the remote-control lock");
            await connection.SendAsync(new("control", Enabled: true, X: 0));
            await WaitUntil(() => !agent.LocalMouseLocked && !agent.LocalKeyboardLocked);
            // Turning remote input off clears its lock while preserving a separately requested lock.
            await connection.SendAsync(new("locks", X: 1, Y: 0, Value: 0));
            await connection.SendAsync(new("control", Enabled: true, X: 1));
            await WaitUntil(() => agent.LocalMouseLocked && agent.LocalKeyboardLocked);
            await connection.SendAsync(new("control", Enabled: false));
            await WaitUntil(() => agent.LocalMouseLocked && !agent.LocalKeyboardLocked);
            await connection.SendAsync(new("unlock"));
            await WaitUntil(() => !agent.LocalMouseLocked && !agent.LocalKeyboardLocked);
            target.Close();
        }
        string file = Storage.PathFor("smoke-source.bin"); byte[] data = RandomNumberGenerator.GetBytes(133001); File.WriteAllBytes(file, data);
        bool confirmed = false;
        connection.Received += p => { if (p.Type == "notice" && p.Text.Contains("받은 파일 폴더")) confirmed = true; };
        await connection.SendFileAsync(file); await WaitUntil(() => confirmed);
        string inbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SchoolPc 받은 파일");
        string received = Directory.GetFiles(inbox, "smoke-source-*.bin").Single();
        if (!File.ReadAllBytes(received).SequenceEqual(data)) throw new Exception("Agent file bytes changed");
        File.Delete(received);
        await TestChosenReceiveFolder(pairing, cert, file, data);
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
        await connection.SendAsync(new("control", Enabled: true, X: 1));
        await WaitUntil(() => agent.LocalMouseLocked && agent.LocalKeyboardLocked);
        connection.Dispose(); await WaitUntil(() => Descendants(agent).OfType<Label>().Any(l => l.Text == "연결 대기 중"));
        if (agent.LocalMouseLocked || agent.LocalKeyboardLocked) throw new Exception("Disconnect did not clear local input locks");
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
        await WaitUntil(() => Descendants(agent).OfType<Label>().Any(l => l.Text == "연결 대기 중"));
        await Task.Delay(200);
        Storage.Save("admin.profiles", new List<Pairing> { pairing });
        using (var workspace = new AdminForm())
        {
            workspace.Show();
            var pcList = Descendants(workspace).OfType<ListBox>().Single();
            var row = pcList.GetItemRectangle(0);
            void ListClick(Point point) => typeof(Control).GetMethod("OnMouseClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(pcList, [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)]);
            ListClick(new Point(row.Left + 5, row.Top + 5));
            await WaitUntil(() => Descendants(workspace).OfType<SessionPanel>().Any(p => p.Connection.Connected));
            var first = Descendants(workspace).OfType<SessionPanel>().Single();
            ClickButton(first, "연결 끊기");
            await WaitUntil(() => !first.Connection.Connected && Descendants(agent).OfType<Label>().Any(l => l.Text == "연결 대기 중"));
            ListClick(new Point(row.Left + 5, row.Top + 5)); await Task.Delay(300);
            if (first.IsDisposed || first.IsConnecting || first.Connection.Connected) throw new Exception("List click reconnected a manually disconnected PC");
            ListClick(new Point(5, pcList.ClientSize.Height - 5)); await Task.Delay(100);
            if (first.IsDisposed || first.Connection.Connected) throw new Exception("Blank list click started a connection");
            ClickButton(workspace, "선택한 PC 연결");
            await WaitUntil(() => Descendants(workspace).OfType<SessionPanel>().Any(p => p.Connection.Connected));
            if (!first.IsDisposed) throw new Exception("Explicit reconnect did not replace the stopped session");
            var second = Descendants(workspace).OfType<SessionPanel>().Single();
            ClickButton(workspace, "모두 연결 끊기");
            await WaitUntil(() => !second.Connection.Connected && Descendants(agent).OfType<Label>().Any(l => l.Text == "연결 대기 중"));
            ListClick(new Point(row.Left + 5, row.Top + 5)); await Task.Delay(300);
            if (second.IsDisposed || second.IsConnecting || second.Connection.Connected) throw new Exception("Disconnect all allowed a list reconnect");
            ClickButton(workspace, "모두 연결");
            await WaitUntil(() => Descendants(workspace).OfType<SessionPanel>().Any(p => p.Connection.Connected));
            workspace.Close();
        }
        await WaitUntil(() => Descendants(agent).OfType<Label>().Any(l => l.Text == "연결 대기 중"));
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
