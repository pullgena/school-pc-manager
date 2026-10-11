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
    [StructLayout(LayoutKind.Sequential)] private struct HookKey { public uint Code, Scan, Flags, Time; public UIntPtr Extra; }
    private static void HookKeyEvent(NativeInput input, Keys key, bool down, bool injected = false)
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<HookKey>());
        try
        {
            Marshal.StructureToPtr(new HookKey { Code = (uint)key, Flags = injected ? 0x10u : 0 }, pointer, false);
            typeof(NativeInput).GetMethod("KeyboardProc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(input, [0, (IntPtr)(down ? 0x100 : 0x101), pointer]);
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
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
            if (Path.GetDirectoryName(saved!) != receiving.ReceiveDirectory || !File.ReadAllBytes(saved!).SequenceEqual(data)) throw new Exception("Target submission did not use the chosen per-PC folder");
        }
        finally { release.TrySetResult(); }
        await sending;
    }
    private async Task TestFrameRecovery(Pairing pairing, X509Certificate2 certificate)
    {
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int acknowledgements = 0, shares = 0;
        var serving = Task.Run(async () =>
        {
            using var tcp = await server.AcceptTcpClientAsync(timeout.Token);
            using var tls = new SslStream(tcp.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token);
            using var wire = new SchoolPc.Core.Channel(tls);
            await wire.ReadAsync(timeout.Token); await wire.SendAsync(new("accepted", Value: 2), timeout.Token);
            await wire.SendAsync(new("frame", Value: 101, Data: [1, 2, 3]), timeout.Token);
            while (!timeout.IsCancellationRequested)
            {
                var packet = await wire.ReadAsync(timeout.Token);
                if (packet.Type == "ping") await wire.SendAsync(new("pong"), timeout.Token);
                if (packet.Type == "frame-ack")
                {
                    if (packet.Value == 101)
                    {
                        Interlocked.Increment(ref acknowledgements);
                        await wire.SendAsync(new("frame", Value: 102, Data: DesktopCapture.Jpeg(blank: true)), timeout.Token);
                    }
                    else if (packet.Value == 102) Interlocked.Increment(ref acknowledgements);
                }
                // Drop the first broadcast acknowledgement; another frame must arrive after the timeout.
                if (packet.Type == "broadcast-frame")
                {
                    if (Interlocked.Increment(ref shares) >= 2) return;
                }
            }
        });
        using var panel = new SessionPanel(pairing with { Port = ((IPEndPoint)server.LocalEndpoint).Port });
        Controls.Add(panel); panel.Show();
        try { await panel.ConnectAsync(); }
        catch { if (serving.IsFaulted) await serving; throw; }
        await WaitUntil(() => Volatile.Read(ref acknowledgements) == 2);
        var image = typeof(ScreenView).GetField("image", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(Descendants(panel).OfType<ScreenView>().Single());
        if (image == null) throw new Exception("Valid frame did not recover after corrupt frame");
        typeof(SessionPanel).GetField("sharing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(panel, true);
        var timer = (System.Windows.Forms.Timer)typeof(SessionPanel).GetField("shareTimer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(panel)!;
        timer.Start(); await serving;
        if (Volatile.Read(ref shares) < 2) throw new Exception("Missing screen-share acknowledgement froze sharing");
        Controls.Remove(panel);
    }
    private async Task Run()
    {
        if (System.Runtime.InteropServices.Marshal.SizeOf<WifiManager.InterfaceInfo>() != 532 || System.Runtime.InteropServices.Marshal.SizeOf<WifiManager.ProfileInfo>() != 516) throw new Exception("WLAN ABI mismatch");
        if (Removal.RoleFiles("SchoolPc.Admin").Contains("agent") || Removal.RoleFiles("SchoolPc.Agent").Contains("admin")) throw new Exception("Removal roles overlap");
        using (var wifi = new WifiForm()) { wifi.CreateControl(); } // Never mutate runner network profiles.
        byte[] secret = [0, 1, 2, 3, 255];
        if (!UserSecret.Unprotect(UserSecret.Protect(secret)).SequenceEqual(secret)) throw new Exception("DPAPI round trip failed");
        using (var input = new NativeInput())
        {
            int emergencies = 0; input.Emergency += () => emergencies++;
            input.SetEmergencyShortcut(new((int)Keys.F10)); input.SetLocks(true, true, 0);
            HookKeyEvent(input, Keys.ControlKey, true); HookKeyEvent(input, Keys.Menu, true);
            HookKeyEvent(input, Keys.F12, true); HookKeyEvent(input, Keys.F12, false);
            HookKeyEvent(input, Keys.F10, true, injected: true);
            if (emergencies != 0 || !input.KeyboardLocked) throw new Exception("Old or remote-injected shortcut released control");
            HookKeyEvent(input, Keys.F10, true); HookKeyEvent(input, Keys.F10, true); HookKeyEvent(input, Keys.F10, false);
            if (emergencies != 1 || input.KeyboardLocked || input.MouseLocked) throw new Exception("Custom shortcut failed during input lock or repeated on key-up");
            HookKeyEvent(input, Keys.ControlKey, false); HookKeyEvent(input, Keys.Menu, false);
            bool invalidShortcut = false;
            try { input.SetEmergencyShortcut(new((int)Keys.F10, Control: false, Alt: false)); } catch (ArgumentException) { invalidShortcut = true; }
            if (!invalidShortcut) throw new Exception("Shortcut could be disabled");
            if (input.MouseHookInstalled) throw new Exception("Unblocked local mouse has a global hook");
            input.SetLocks(true, false, 0);
            if (!input.MouseHookInstalled || !input.MouseLocked) throw new Exception("Mouse lock hook was not installed");
            input.SetRemoteControlBlock(true); input.UnlockManual();
            if (!input.MouseHookInstalled || !input.MouseLocked) throw new Exception("Manual unlock cleared remote mouse block");
            input.SetRemoteControlBlock(false);
            if (input.MouseHookInstalled || input.MouseLocked) throw new Exception("Remote mouse block left its hook installed");
            input.SetLocks(true, false, 1); await Task.Delay(1100); input.RefreshExpiredLocks();
            if (input.MouseHookInstalled || input.MouseLocked) throw new Exception("Expired mouse lock left its hook installed");
            input.Unlock();
        }
        // Real viewport events: no hover motion while unfocused; captured drag clamps at the edge.
        using (var mouseForm = new Form { Size = new Size(400, 300) })
        using (var viewport = new ScreenView { RemoteInput = true })
        using (var other = new TextBox { Dock = DockStyle.Top })
        {
            mouseForm.Controls.Add(viewport); mouseForm.Controls.Add(other); mouseForm.Show();
            viewport.SetFrame(new Bitmap(400, 300));
            var moves = new List<(int X, int Y)>(); var buttons = new List<bool>(); int releases = 0;
            viewport.MoveRemote += (x, y) => moves.Add((x, y));
            viewport.ButtonRemote += (_, down) => buttons.Add(down);
            viewport.ReleaseRemote += () => releases++;
            void Mouse(string method, int x, int y, MouseButtons button = MouseButtons.None) => typeof(Control)
                .GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(viewport, [new MouseEventArgs(button, 1, x, y, 0)]);
            other.Focus(); releases = 0; Mouse("OnMouseMove", viewport.Width / 2, viewport.Height / 2);
            if (moves.Count != 0) throw new Exception("Unfocused viewport moved the target mouse");
            Mouse("OnMouseDown", viewport.Width / 2, viewport.Height / 2, MouseButtons.Left);
            Mouse("OnMouseMove", viewport.Width + 50, viewport.Height + 50);
            Mouse("OnMouseUp", viewport.Width + 50, viewport.Height + 50, MouseButtons.Left);
            if (moves.Count != 3 || !buttons.SequenceEqual([true, false]) || moves.Last() != (65535, 65535) || releases != 0) throw new Exception("Captured drag lost its edge movement or button release");
            Mouse("OnMouseDown", viewport.Width / 2, viewport.Height / 2, MouseButtons.Left);
            viewport.Capture = false;
            if (releases == 0) throw new Exception("Lost capture did not release a held button");
            mouseForm.Close();
        }
        using (var admin = new AdminForm())
        {
            admin.Show(); await Task.Delay(100);
            Directory.CreateDirectory("out/previews"); using var bitmap = new Bitmap(admin.Width, admin.Height); admin.DrawToBitmap(bitmap, admin.ClientRectangle); bitmap.Save("out/previews/admin.png"); admin.Close();
        }
        var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start(); int port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        var settings = new AgentSettings("CI 교실 PC", port, Convert.ToHexString(RandomNumberGenerator.GetBytes(32))); Storage.Save("agent.settings", settings);
        using var agent = new AgentForm(); agent.Show(); await Task.Delay(100);
        var tabs = Descendants(agent).OfType<TabControl>().Single();
        var settingsPage = tabs.TabPages.Cast<TabPage>().Single(p => p.Text == "설정"); tabs.SelectedTab = settingsPage;
        var notify = (NotifyIcon)typeof(AgentForm).GetField("tray", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(agent)!;
        if (notify.ContextMenuStrip!.Items.Cast<ToolStripItem>().Any(i => i.Text?.Contains("긴급") == true)) throw new Exception("Tray still contains emergency release");
        if (!Descendants(settingsPage).OfType<Button>().Any(b => b.Text == "긴급 제어 해제")) throw new Exception("Settings emergency release missing");
        var shortcutKey = Descendants(settingsPage).OfType<ComboBox>().Single(); shortcutKey.SelectedItem = Keys.F10;
        var modifiers = Descendants(settingsPage).OfType<CheckBox>().ToDictionary(c => c.Text);
        if (modifiers["Windows 로그인 시 자동 실행"].Checked != StartupRegistration.Enabled) throw new Exception("Startup checkbox does not match registration");
        modifiers["Ctrl"].Checked = false; modifiers["Alt"].Checked = false;
        ClickButton(settingsPage, "단축키 저장");
        if (Storage.Load<AgentSettings>("agent.settings")!.Shortcut != EmergencyShortcut.Default) throw new Exception("Settings saved an invalid shortcut");
        modifiers["Ctrl"].Checked = true; modifiers["Shift"].Checked = true;
        ClickButton(settingsPage, "단축키 저장");
        var savedShortcut = new EmergencyShortcut((int)Keys.F10, Alt: false, Shift: true);
        if (Storage.Load<AgentSettings>("agent.settings")!.Shortcut != savedShortcut || !Descendants(settingsPage).OfType<Label>().Any(l => l.Text == "현재 긴급 해제 단축키: Ctrl+Shift+F10")) throw new Exception("Custom shortcut was not saved or displayed");
        using (var bitmap = new Bitmap(agent.Width, agent.Height)) { agent.DrawToBitmap(bitmap, agent.ClientRectangle); bitmap.Save("out/previews/agent-settings.png"); }
        tabs.SelectedIndex = 0;
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
        int backgroundFrames = 0;
        Action<Packet> frameProbe = p =>
        {
            if (p.Type == "frame") { Interlocked.Increment(ref backgroundFrames); _ = connection.SendAsync(new("frame-ack", Value: p.Value)); }
        };
        connection.Received += frameProbe;
        await connection.SendAsync(new("screen", Enabled: true)); await WaitUntil(() => Volatile.Read(ref backgroundFrames) >= 2);
        int beforeStall = Volatile.Read(ref backgroundFrames);
        Thread.Sleep(800); // Deliberately stall the UI: TLS reading and capture must keep progressing.
        if (Volatile.Read(ref backgroundFrames) <= beforeStall) throw new Exception("UI stall froze capture or network reception");
        await connection.SendAsync(new("screen", Enabled: false)); await Task.Delay(200); connection.Received -= frameProbe;
        agent.Hide();
        await connection.SendAsync(new("message", Text: "CI 왕복 메시지"));
        await WaitUntil(() => Application.OpenForms.OfType<MessagePopup>().Any());
        var popup = Application.OpenForms.OfType<MessagePopup>().Single();
        if (agent.Visible || !popup.TopMost || !Descendants(popup).OfType<TextBox>().Any(t => t.Text.Contains("CI 왕복 메시지")))
            throw new Exception("Message did not appear in a separate popup while the agent stayed hidden");
        await connection.SendAsync(new("message", Text: "두 번째 메시지\n" + new string('a', 900)));
        await WaitUntil(() => Descendants(popup).OfType<TextBox>().Any(t => t.Text.Contains(new string('a', 900))));
        if (Application.OpenForms.OfType<MessagePopup>().Count() != 1 || agent.Visible) throw new Exception("Repeated messages opened duplicate windows or revealed the agent");
        backgroundFrames = 0; connection.Received += frameProbe;
        await connection.SendAsync(new("screen", Enabled: true));
        await WaitUntil(() => Volatile.Read(ref backgroundFrames) >= 2);
        await connection.SendAsync(new("screen", Enabled: false)); connection.Received -= frameProbe;
        using (var bitmap = new Bitmap(popup.Width, popup.Height)) { popup.DrawToBitmap(bitmap, popup.ClientRectangle); bitmap.Save("out/previews/message-popup.png"); }
        ClickButton(popup, "확인");
        if (!popup.IsDisposed || agent.Visible || !connection.Connected) throw new Exception("Closing message popup changed the agent window or connection");
        agent.Show();
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
        await TestFrameRecovery(pairing, cert);
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
        if (exclusive.Cursor.Handle == Cursors.Default.Handle || Descendants(exclusive).OfType<PictureBox>().Single().Cursor.Handle != exclusive.Cursor.Handle) throw new Exception("Focus mode did not hide the local cursor");
        await connection.SendAsync(new("broadcast-stop")); await WaitUntil(() => exclusive.IsDisposed);
        if (agent.Cursor.Handle != Cursors.Default.Handle) throw new Exception("Focus cursor was not restored");
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
        tabs.SelectedTab = settingsPage; ClickButton(agent, "긴급 제어 해제"); await agent.ExitAsync();
        using (var startupAgent = new AgentForm(startInTray: true))
        {
            startupAgent.Show(); await Task.Delay(200);
            if (!Descendants(startupAgent).OfType<Label>().Any(l => l.Text == "현재 긴급 해제 단축키: Ctrl+Shift+F10")) throw new Exception("Shortcut was not restored after restart");
            if (startupAgent.Visible) throw new Exception("Startup did not hide agent in tray");
            using (var startupConnection = new AdminConnection(pairing)) { await startupConnection.ConnectAsync(); }
            await startupAgent.ExitAsync();
        }
        using (var requestAgent = new AgentForm())
        using (var manager = new AdminForm())
        {
            requestAgent.Show(); manager.Show(); await Task.Delay(200);
            ClickButton(manager, "선택한 PC 연결");
            await WaitUntil(() => Descendants(manager).OfType<SessionPanel>().Any(p => p.Connection.Connected));
            var panel = Descendants(manager).OfType<SessionPanel>().Single();
            typeof(SessionPanel).GetMethod("SetReceiveFolder", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, [Storage.PathFor("assignment-inbox")]);
            if (!Descendants(panel).OfType<Button>().Any(b => b.Text == "① 저장 폴더 선택") || !Descendants(panel).OfType<Button>().Any(b => b.Text == "② 과제 제출 요청")) throw new Exception("Easy file controls missing");
            using (var bitmap = new Bitmap(manager.Width, manager.Height)) { manager.DrawToBitmap(bitmap, manager.ClientRectangle); bitmap.Save("out/previews/admin-files.png"); }
            await panel.Run(new("control", Enabled: true, X: 1));
            await WaitUntil(() => requestAgent.LocalMouseLocked && requestAgent.LocalKeyboardLocked);
            await panel.Connection.RequestAssignmentAsync("CI 과제", 30, 2);
            await WaitUntil(() => Application.OpenForms.Cast<Form>().Any(f => f.Text == "과제 제출 · CI 과제"));
            var submission = (SubmissionForm)Application.OpenForms.Cast<Form>().Single(f => f.Text == "과제 제출 · CI 과제");
            if (!submission.TopMost || requestAgent.LocalMouseLocked || requestAgent.LocalKeyboardLocked) throw new Exception("Submission window is not usable or topmost");
            string secondFile = Storage.PathFor("second-homework.txt"); File.WriteAllText(secondFile, "두 번째 과제");
            submission.SetFiles([file, secondFile, file]);
            if (Descendants(submission).OfType<ListBox>().Single().Items.Count != 0) throw new Exception("Submission accepted too many files");
            submission.SetFiles([file, secondFile]);
            using (var bitmap = new Bitmap(submission.Width, submission.Height)) { submission.DrawToBitmap(bitmap, submission.ClientRectangle); bitmap.Save("out/previews/submission.png"); }
            await submission.SubmitAsync();
            await WaitUntil(() => submission.IsDisposed && panel.Connection.Assignment?.Finished == true);
            var submitted = Directory.GetFiles(panel.Connection.AssignmentFolder!);
            if (submitted.Length != 2 || !submitted.Any(f => File.ReadAllBytes(f).SequenceEqual(data)) || !submitted.Any(f => File.ReadAllText(f) == "두 번째 과제")) throw new Exception("Assignment batch was not saved correctly");
            await WaitUntil(() => Application.OpenForms.Cast<Form>().Any(f => f is FileArrival));
            foreach (var toast in Application.OpenForms.Cast<Form>().Where(f => f is FileArrival).ToArray()) toast.Close();
            await panel.Connection.RequestAssignmentAsync("시간 종료 과제", 1, 1);
            await WaitUntil(() => Application.OpenForms.Cast<Form>().Any(f => f.Text == "과제 제출 · 시간 종료 과제"));
            await WaitUntil(() => !Application.OpenForms.Cast<Form>().Any(f => f.Text == "과제 제출 · 시간 종료 과제"));
            await WaitUntil(() => panel.Connection.Assignment?.Cancelled == true);
            // Exercise the actual modal monitor selection, including its preview.
            using var choose = new System.Windows.Forms.Timer { Interval = 50 };
            choose.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text == "공유할 화면 선택");
                if (dialog == null) return;
                var options = Descendants(dialog).OfType<ComboBox>().Single();
                if (options.Items.Count != Screen.AllScreens.Length || !Descendants(dialog).OfType<PictureBox>().Any(p => p.Image != null)) throw new Exception("Monitor choices or preview missing");
                ClickButton(dialog, "선택");
            }; choose.Start();
            bool selected = (bool)typeof(SessionPanel).GetMethod("SelectShareScreen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, null)!;
            choose.Stop(); if (!selected) throw new Exception("Monitor selection was cancelled");
            using var jpeg = new MemoryStream(DesktopCapture.Jpeg(640, 55, device: Screen.PrimaryScreen!.DeviceName));
            using var captured = Image.FromStream(jpeg);
            if (captured.Width > 640 || captured.Width < 1) throw new Exception("Selected monitor capture size invalid");
            bool missingRejected = false;
            try { DesktopCapture.Jpeg(device: "missing-monitor"); } catch (IOException) { missingRejected = true; }
            if (!missingRejected) throw new Exception("Missing monitor silently shared another display");
            bool allow = false;
            using var answer = new System.Windows.Forms.Timer { Interval = 50 };
            answer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text == "대상 프로그램 종료 승인");
                if (dialog != null) ClickButton(dialog, allow ? "종료 승인" : "거절");
            }; answer.Start();
            await requestAgent.RequestExitAsync();
            if (requestAgent.IsDisposed || !panel.Connection.Connected) throw new Exception("Denied exit stopped the target");
            await panel.Run(new("control", Enabled: true, X: 1));
            await WaitUntil(() => requestAgent.LocalMouseLocked && requestAgent.LocalKeyboardLocked);
            ClickButton(panel, "긴급 제어 중지");
            await WaitUntil(() => !requestAgent.LocalMouseLocked && !requestAgent.LocalKeyboardLocked && !panel.Connection.Connected);
            if (requestAgent.IsDisposed) throw new Exception("Emergency control stop exited the target app");
            await Task.Delay(200); ClickButton(manager, "선택한 PC 연결");
            await WaitUntil(() => Descendants(manager).OfType<SessionPanel>().Any(p => p.Connection.Connected));
            allow = true; await requestAgent.RequestExitAsync();
            await WaitUntil(() => requestAgent.IsDisposed);
            answer.Stop(); manager.Close();
        }
        using (var panel = new SessionPanel(pairing)) { panel.CreateControl(); }
        Console.WriteLine("PASS Windows DPAPI, hooks, forms, real Admin/Agent TLS consent, credential rejection, commands, file transfer and disconnect");
    }
}
