using System.Drawing.Imaging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SchoolPc.Core;
using SchoolPc.Windows;
using Channel = SchoolPc.Core.Channel;

namespace SchoolPc.Agent;

public sealed record AgentSettings(string Name, int Port, string Token, bool AutoApprove = false, EmergencyShortcut? Shortcut = null);
internal sealed class AgentSession(Channel channel, CancellationTokenSource stop, string address)
{
    public Channel Channel { get; } = channel;
    public CancellationTokenSource Stop { get; } = stop;
    public string Address { get; } = address;
    public volatile bool ScreenEnabled, ControlEnabled, PrivacyEnabled;
    public bool FrameAcknowledgement;
    public bool SequencedFrames;
    public int FrameSequence;
    public int FramePending;
    public long FrameSent;
    public long LastHeartbeat = Environment.TickCount64;
    public readonly SemaphoreSlim SendFile = new(1);
}

public sealed class AgentForm : Form
{
    private AgentSettings settings;
    private readonly X509Certificate2 certificate;
    private readonly NativeInput input = new();
    internal bool LocalMouseLocked => input.MouseLocked;
    internal bool LocalKeyboardLocked => input.KeyboardLocked;
    private readonly TextBox name = new() { Width = 190 };
    private readonly NumericUpDown port = new() { Minimum = 1024, Maximum = 65535, Width = 95 };
    private readonly TextBox host = new() { Width = 165 };
    private readonly Label status = Ui.Label("연결 대기 꺼짐", 15);
    private readonly Label lockStatus = Ui.Label("입력 잠금 없음");
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer watchdog = new() { Interval = 500 };
    private readonly string inbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SchoolPc 받은 파일");
    private TcpListener? listener;
    private CancellationTokenSource? listening;
    private AgentSession? session;
    private int busy;
    private bool closing;
    private bool exiting;
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Text = "School PC · 관리 대상", Visible = true };
    private readonly bool startInTray;
    private SharedScreenForm? sharedScreen;
    private Cursor? focusCursor;
    private SubmissionForm? submission;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> submissionReplies = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> savedReplies = new();
    private readonly List<Form> curtains = [];
    private bool stoppingDisplay;
    private int choosingFolder;
    private int requestingExit;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> exitRequests = new();
    private Task? acceptTask;
    private readonly HashSet<Task> handlers = [];
    private readonly object handlerGate = new();

    public AgentForm(bool startInTray = false)
    {
        this.startInTray = startInTray;
        Text = "School PC · 관리 대상"; Size = new Size(850, 670); Ui.Style(this);
        settings = Storage.Load<AgentSettings>("agent.settings") ?? new(Environment.MachineName, 45832, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        if (settings.Shortcut is not { Valid: true }) settings = settings with { Shortcut = EmergencyShortcut.Default };
        input.SetEmergencyShortcut(settings.Shortcut!);
        Storage.Save("agent.settings", settings);
        certificate = LoadCertificate();
        name.Text = settings.Name; port.Value = settings.Port; host.Text = LocalAddress();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 7 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(Ui.Label("내 PC 관리 연결", 22), 0, 0);
        layout.Controls.Add(Ui.Label("실행하면 연결 대기가 시작됩니다. 관리자에게 등록 파일을 전달해 주세요."), 0, 1);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([Ui.Button("연결 대기 시작", (_, _) => StartListening(), true),
            Ui.Button("관리자 등록 파일 저장", (_, _) => ExportPairing())]);
        var otherSettings = Ui.MenuButton("기타 설정", Ui.MenuItem("PC 이름·주소·포트", (_, _) => EditConnection()), Ui.MenuItem("등록 키 변경…", (_, _) => RotateToken()), Ui.MenuItem("등록 관리자 자동 허용 설정", (_, _) => ConfigureApproval()),
                Ui.MenuItem("프로그램 종료", async (_, _) => await RequestExitAsync()), new ToolStripSeparator(),
                Ui.MenuItem("관리자에게 파일 보내기", async (_, _) => await SendLocalFile()), Ui.MenuItem("받은 파일 폴더", (_, _) => OpenInbox()),
                Ui.MenuItem("Windows Wi-Fi 관리", (_, _) => new WifiForm().Show(this)), new ToolStripSeparator(),
                Ui.MenuItem("업데이트 확인", async (_, _) => await Updater.CheckAsync(this, ShutdownAsync)), Ui.MenuItem("현재 프로그램 제거…", async (_, _) => await Removal.RequestAsync(this, ShutdownAsync)));
        layout.Controls.Add(buttons, 0, 2); layout.Controls.Add(status, 0, 3); layout.Controls.Add(lockStatus, 0, 4);
        var details = new Panel { Dock = DockStyle.Fill };
        var toggle = Ui.Button("활동 내역 보기", (_, _) => { log.Visible = !log.Visible; }); toggle.Dock = DockStyle.Top;
        log.Visible = false; details.Controls.Add(log); details.Controls.Add(toggle); layout.Controls.Add(details, 0, 5);
        layout.Controls.Add(Ui.Label("X를 누르면 트레이에서 계속 실행됩니다. 설정은 ‘설정’ 탭에서 변경하세요."), 0, 6);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var connectionTab = new TabPage("연결"); connectionTab.Controls.Add(layout);
        var settingsTab = new TabPage("설정"); settingsTab.Controls.Add(BuildSettings(otherSettings));
        tabs.TabPages.AddRange([connectionTab, settingsTab]); Controls.Add(tabs);
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("창 열기", null, (_, _) => ShowWindow());
        trayMenu.Items.Add("프로그램 종료", null, async (_, _) => await RequestExitAsync());
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (_, _) => ShowWindow();
        Shown += (_, _) => { StartListening(); if (this.startInTray) Hide(); };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        input.Emergency += () => { if (!IsDisposed) BeginInvoke((Action)(() => StopListening())); };
        watchdog.Tick += (_, _) =>
        {
            input.RefreshExpiredLocks();
            lockStatus.Text = input.MouseLocked || input.KeyboardLocked ? $"마우스: {(input.MouseLocked ? "잠금" : "사용 가능")} / 키보드: {(input.KeyboardLocked ? "잠금" : "사용 가능")} · {(input.Remaining < 0 ? "연결 중 ON" : input.Remaining + "초 뒤 해제")}" : "입력 잠금 없음";
            if (session is { } s && Environment.TickCount64 - Interlocked.Read(ref s.LastHeartbeat) > 15000) Disconnect("연결 응답 시간 초과");
        };
        watchdog.Start();
        FormClosing += async (_, e) =>
        {
            if (closing) return;
            if (e.CloseReason == CloseReason.UserClosing && !exiting) { e.Cancel = true; Hide(); return; }
            e.Cancel = true; closing = true;
            await ShutdownAsync(); input.Dispose(); certificate.Dispose(); watchdog.Dispose();
            tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); Close();
        };
        AddLog("등록 파일을 신뢰하는 관리자에게만 전달하세요. X 버튼은 트레이로 숨깁니다.");
    }
    private void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private Control BuildSettings(Control otherSettings)
    {
        var fields = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        var current = Ui.Label("현재 긴급 해제 단축키: " + input.Shortcut, 13);
        var ctrl = new CheckBox { Text = "Ctrl", Checked = input.Shortcut.Control, AutoSize = true };
        var alt = new CheckBox { Text = "Alt", Checked = input.Shortcut.Alt, AutoSize = true };
        var shift = new CheckBox { Text = "Shift", Checked = input.Shortcut.Shift, AutoSize = true };
        var key = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        key.Items.AddRange(EmergencyShortcut.AllowedKeys.Cast<object>().ToArray()); key.SelectedItem = (Keys)input.Shortcut.Key;
        var modifiers = new FlowLayoutPanel { AutoSize = true }; modifiers.Controls.AddRange([ctrl, alt, shift, key]);
        var result = Ui.Label("");
        var startup = new CheckBox { Text = "Windows 로그인 시 자동 실행", AutoSize = true, Checked = StartupRegistration.Enabled };
        bool updatingStartup = false;
        startup.CheckedChanged += (_, _) =>
        {
            if (updatingStartup) return;
            try { StartupRegistration.SetEnabled(startup.Checked); }
            catch (Exception e) { Ui.Error(this, e); }
            finally { updatingStartup = true; startup.Checked = StartupRegistration.Enabled; updatingStartup = false; }
        };
        Activated += (_, _) => { updatingStartup = true; startup.Checked = StartupRegistration.Enabled; updatingStartup = false; };
        fields.Controls.AddRange([Ui.Label("긴급 해제", 20),
            Ui.Button("긴급 제어 해제", (_, _) => StopListening()),
            Ui.Label("현재 연결과 대기를 종료하고 화면·마우스·키보드 잠금을 해제합니다."), current,
            Ui.Label("Ctrl·Alt·Shift 중 두 개 이상과 키를 선택하세요. 잠금 중에도 작동합니다."), modifiers,
            Ui.Button("단축키 저장", (_, _) =>
            {
                var shortcut = new EmergencyShortcut((int)(Keys)key.SelectedItem!, ctrl.Checked, alt.Checked, shift.Checked);
                if (!shortcut.Valid) { result.Text = "Ctrl·Alt·Shift 중 두 개 이상을 선택해 주세요."; return; }
                try
                {
                    var updated = settings with { Shortcut = shortcut }; Storage.Save("agent.settings", updated);
                    settings = updated; input.SetEmergencyShortcut(shortcut);
                    current.Text = "현재 긴급 해제 단축키: " + shortcut; result.Text = "저장했습니다. 다음 실행에도 적용됩니다.";
                }
                catch (Exception e) { Ui.Error(this, e); }
            }, true), result, startup, otherSettings]);
        return fields;
    }
    internal async Task RequestExitAsync()
    {
        if (Interlocked.Exchange(ref requestingExit, 1) != 0) return;
        try
        {
            var active = session;
            if (active == null || active.Stop.IsCancellationRequested) { await ExitAsync(); return; }
            string id = Guid.NewGuid().ToString("N");
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            exitRequests[id] = answer;
            try
            {
                AddLog("관리자에게 프로그램 종료 승인을 요청했습니다.");
                await active.Channel.SendAsync(new("exit-request", Id: id), active.Stop.Token);
                bool approved = await answer.Task.WaitAsync(TimeSpan.FromSeconds(35), active.Stop.Token);
                if (approved && session == active && !active.Stop.IsCancellationRequested) await ExitAsync();
                else AddLog("프로그램 종료가 승인되지 않았습니다.");
            }
            finally { exitRequests.TryRemove(id, out _); }
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException)
        { AddLog("종료 승인 응답을 받지 못했습니다. 프로그램은 계속 실행됩니다."); }
        finally { Interlocked.Exchange(ref requestingExit, 0); }
    }
    internal async Task ExitAsync() { exiting = true; await ShutdownAsync(); Close(); }
    private void ConfigureApproval()
    {
        bool enabled = !settings.AutoApprove;
        string message = enabled
            ? "등록 키를 가진 관리자의 연결·화면 공유·원격 입력·잠금·전원 요청을 추가 승인 없이 허용할까요?\n긴급 해제는 계속 사용할 수 있습니다."
            : "자동 허용을 끄고 연결마다 승인하도록 바꿀까요? 현재 연결도 종료합니다.";
        if (MessageBox.Show(this, message, "등록 관리자 자동 허용", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Disconnect("자동 허용 설정 변경"); settings = settings with { AutoApprove = enabled }; Storage.Save("agent.settings", settings);
        AddLog(enabled ? "등록 관리자 자동 허용 켜짐" : "등록 관리자 자동 허용 꺼짐");
    }
    private static X509Certificate2 LoadCertificate()
    {
        string path = Storage.PathFor("agent.certificate");
        if (!File.Exists(path))
        {
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest("CN=SchoolPcAgent", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            File.WriteAllBytes(path, UserSecret.Protect(created.Export(X509ContentType.Pfx, "")));
        }
        return X509CertificateLoader.LoadPkcs12(UserSecret.Unprotect(File.ReadAllBytes(path)), "", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
    }
    private static string LocalMac(string address)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString() == address));
        string mac = adapter?.GetPhysicalAddress().ToString() ?? "";
        return mac.Length == 12 ? mac : "";
    }
    private static string LocalAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).First(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Address.ToString();
        }
        catch { return "127.0.0.1"; }
    }
    private void EditConnection()
    {
        if (listener != null) { MessageBox.Show(this, "연결 대기를 종료한 뒤 변경할 수 있습니다."); return; }
        using var dialog = new Form { Text = "내 PC 연결 설정", Size = new Size(450, 360), StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
        var pcName = new TextBox { Text = name.Text, Width = 310 };
        var pcHost = new TextBox { Text = host.Text, Width = 310 };
        var pcPort = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = port.Value, Width = 110 };
        var fields = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), FlowDirection = FlowDirection.TopDown };
        fields.Controls.AddRange([Ui.Label("PC 이름"), pcName, Ui.Label("연결 주소"), pcHost, Ui.Label("포트"), pcPort,
            Ui.Button("저장", (_, _) => { try { name.Text = pcName.Text; host.Text = pcHost.Text; port.Value = pcPort.Value; SaveFields(); dialog.Close(); } catch (Exception e) { Ui.Error(dialog, e); } }, true)]);
        dialog.Controls.Add(fields); dialog.ShowDialog(this);
    }
    private void SaveFields()
    {
        if (name.Text.Trim().Length is < 1 or > 100) throw new InvalidDataException("PC 이름은 1~100글자로 입력해 주세요.");
        settings = settings with { Name = name.Text.Trim(), Port = (int)port.Value }; Storage.Save("agent.settings", settings);
    }
    private void StartListening()
    {
        try
        {
            if (listener != null) return;
            SaveFields(); listening = new(); listener = new TcpListener(IPAddress.Any, settings.Port); listener.Start(8);
            name.Enabled = port.Enabled = false; status.Text = $"연결 대기 중 · {host.Text}:{settings.Port}";
            var server = listener; var token = listening.Token;
            acceptTask = Task.Run(() => AcceptLoop(server, token)); AddLog("연결 대기 시작 (TLS 암호화)");
        }
        catch (Exception e) { listener?.Stop(); listener = null; listening?.Dispose(); listening = null; Ui.Error(this, e); }
    }
    private async Task AcceptLoop(TcpListener server, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await server.AcceptTcpClientAsync(ct); client.NoDelay = true;
                if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) { client.Dispose(); continue; }
                var task = HandleClient(client, ct);
                lock (handlerGate) handlers.Add(task);
                _ = task.ContinueWith(t => { lock (handlerGate) handlers.Remove(t); }, TaskScheduler.Default);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task HandleClient(TcpClient client, CancellationToken serverToken)
    {
        AgentSession? active = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        using var tls = new SslStream(client.GetStream(), false);
        using var receiver = new FileReceiver(inbox);
        Task? frames = null;
        string address = ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address.ToString() ?? "알 수 없음";
        try
        {
            await Ui.OnUi(this, () => { AddLog("연결 요청 도착: " + address); log.Visible = true; return true; });
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, ClientCertificateRequired = false }, handshake.Token);
            }
            using var channel = new Channel(tls);
            Packet hello;
            using (var auth = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            { auth.CancelAfter(TimeSpan.FromSeconds(5)); hello = await channel.ReadAsync(auth.Token); }
            if (hello.Type != "hello" || hello.Text.Length != 64 || !Security.EqualHash(Security.HashToken(hello.Text), Security.HashToken(settings.Token)))
            { await Ui.OnUi(this, () => { AddLog("등록 키가 일치하지 않아 연결을 거절했습니다. 등록 파일을 새로 저장해 관리자에 다시 추가해 주세요."); return true; }); return; }
            bool approved = settings.AutoApprove || await ApproveAsync(address, stop.Token);
            if (!approved) { await channel.SendAsync(new("denied", Text: "관리 대상에서 연결을 승인하지 않았습니다."), stop.Token); return; }
            active = new(channel, stop, address) { FrameAcknowledgement = hello.Value >= 1, SequencedFrames = hello.Value >= 2 };
            await Ui.OnUi(this, () => { stop.Token.ThrowIfCancellationRequested(); session = active; status.Text = $"관리자 연결됨 · {address}"; tray.Text = "School PC · 관리자 연결됨";
                tray.ShowBalloonTip(5000, "관리자 연결됨", "화면 공유·원격 제어 연결이 시작되었습니다.", ToolTipIcon.Info); AddLog("관리자 연결 승인: " + address); return true; });
            await channel.SendAsync(new("accepted", Text: settings.Name, Value: 3), stop.Token);
            frames = Task.Run(() => ScreenLoop(active));
            while (!stop.IsCancellationRequested)
            {
                var packet = await channel.ReadAsync(stop.Token);
                if (packet.Type == "ping") { Interlocked.Exchange(ref active.LastHeartbeat, Environment.TickCount64); await channel.SendAsync(new("pong"), stop.Token); continue; }
                try
                {
                    switch (packet.Type)
                    {
                        case "assignment-ready":
                            if (submissionReplies.TryGetValue(packet.Id, out var ready)) ready.TrySetResult(packet.Enabled);
                            break;
                        case "file-saved":
                            if (savedReplies.TryGetValue(packet.Id, out var receipt)) receipt.TrySetResult(packet.Enabled);
                            break;
                        case "assignment-request":
                            var ticket = new SubmissionTicket(packet.Id, packet.Text, checked((int)packet.Size), packet.Value, Environment.TickCount64);
                            await SessionUi(active, () => { OpenSubmission(active, ticket, (int)packet.Size); return true; });
                            break;
                        case "exit-response":
                            if (exitRequests.TryGetValue(packet.Id, out var answer)) answer.TrySetResult(packet.Enabled);
                            break;
                        case "frame-ack":
                            if (active.SequencedFrames) Interlocked.CompareExchange(ref active.FramePending, 0, packet.Value);
                            else Interlocked.Exchange(ref active.FramePending, 0);
                            break;
                        case "select-folder":
                            _ = ChooseReceiveFolder(active, receiver, packet); break;
                        case "screen-lock":
                            await SessionUi(active, () => { SetPrivacy(active, packet.Enabled); return true; }); break;
                        case "broadcast-start":
                            await SessionUi(active, () => { StartDisplay(packet.Enabled); return true; }); break;
                        case "broadcast-frame":
                            if (packet.Data == null || packet.Data.Length > 2 * 1024 * 1024) throw new InvalidDataException("공유 화면 크기가 잘못되었습니다.");
                            using (var stream = new MemoryStream(packet.Data))
                            using (var decoded = Image.FromStream(stream))
                            {
                                if (decoded.Width > 1920 || decoded.Height > 1920) throw new InvalidDataException("공유 화면 해상도가 너무 큽니다.");
                                var bitmap = new Bitmap(decoded);
                                await SessionUi(active, () => { if (sharedScreen is { IsDisposed: false }) sharedScreen.SetFrame(bitmap); else bitmap.Dispose(); return true; });
                            }
                            await channel.SendAsync(new("broadcast-ack", Value: packet.Value), stop.Token); break;
                        case "broadcast-stop":
                            await SessionUi(active, () => { StopDisplay(); return true; }); break;
                        case "screen": active.ScreenEnabled = packet.Enabled; Storage.Audit("agent", packet.Enabled ? "화면 공유 시작" : "화면 공유 종료"); break;
                        case "control":
                            await SessionUi(active, () => { input.SetRemoteControlBlock(packet.Enabled && packet.X == 1); active.ControlEnabled = packet.Enabled; if (!packet.Enabled) { input.ReleaseRemote(); } AddLog(packet.Enabled ? "원격 입력 켜짐" : "원격 입력 꺼짐"); return true; }); break;
                        case "move": case "button": case "wheel": case "key": case "text":
                            if (!active.ControlEnabled) throw new InvalidOperationException("원격 입력이 꺼져 있습니다.");
                            await SessionUi(active, () => { ExecuteInput(packet); return true; }); break;
                        case "release": await SessionUi(active, () => { input.ReleaseRemote(); return true; }); break;
                        case "locks":
                            await SessionUi(active, () => { ClearCurtains(); active.PrivacyEnabled = false; input.SetLocks(packet.X == 1, packet.Y == 1, packet.Value); AddLog("입력 잠금 ON/OFF 변경"); return true; });
                            await channel.SendAsync(new("notice", Text: "입력 잠금을 변경했습니다. 연결 종료·긴급 해제 시 풀립니다."), stop.Token); break;
                        case "unlock": await SessionUi(active, () => { ClearCurtains(); active.PrivacyEnabled = false; StopDisplay(); input.Unlock(); input.ReleaseRemote(); AddLog("입력 잠금 해제"); return true; }); break;
                        case "windows-lock":
                            await SessionUi(active, () => { ClearCurtains(); active.PrivacyEnabled = false; StopDisplay(); input.Unlock(); input.ReleaseRemote(); NativeInput.LockWindows(); AddLog("Windows 잠금 요청"); return true; }); break;
                        case "message":
                            if (packet.Text.Length > 1000) throw new InvalidDataException("메시지가 너무 깁니다.");
                            await SessionUi(active, () => { AddLog("관리자 메시지: " + packet.Text, audit: false); Show(); WindowState = FormWindowState.Normal; Activate(); return true; }); break;
                        case "file-begin": receiver.Begin(packet); break;
                        case "file-chunk": receiver.Chunk(packet); break;
                        case "file-end":
                            string saved = receiver.End(packet);
                            await SessionUi(active, () => { AddLog("파일 받기 완료: " + Path.GetFileName(saved), audit: false); FileArrival.Notify(this, saved); return true; });
                            Storage.Audit("agent", "파일 받기 완료");
                            await channel.SendAsync(new("notice", Text: "관리 대상 PC의 받은 파일 폴더 또는 선택한 폴더에 저장되었습니다: " + saved), stop.Token); break;
                        case "network-settings":
                            await SessionUi(active, () => { ClearCurtains(); active.PrivacyEnabled = false; StopDisplay(); input.Unlock(); new WifiForm().Show(this); AddLog("관리자 Wi-Fi 관리 창 열기 요청"); return true; }); break;
                        case "request-file":
                            _ = SendLocalFile(); break;
                        case "power":
                            if (packet.Text is not ("restart" or "shutdown")) throw new InvalidDataException("잘못된 전원 요청입니다.");
                            await SessionUi(active, () => { RequestPower(packet.Text); return true; }); break;
                        case "disconnect": return;
                        default: throw new InvalidDataException("지원하지 않는 명령입니다.");
                    }
                }
                catch (Exception e)
                {
                    if (packet.Type.StartsWith("file-")) { receiver.Dispose(); throw; }
                    await channel.SendAsync(new("error", Text: e.Message), stop.Token);
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
        { await ReportConnectionError(e); }
        catch (Exception e) { await ReportConnectionError(e); }
        finally
        {
            stop.Cancel(); client.Dispose();
            if (frames != null) { try { await frames; } catch { } }
            try { await Ui.OnUi(this, () => { if (session == active && active != null) { submission?.EndRequest(); submission = null; ClearCurtains(); StopDisplay(); session = null; tray.Text = "School PC · 관리 대상"; input.Unlock(); input.ReleaseRemote(); status.Text = listener == null ? "연결 대기 꺼짐" : "연결 대기 중"; AddLog("관리자 연결 종료 · 입력 잠금 해제"); } return true; }); } catch (ObjectDisposedException) { }
            Interlocked.Exchange(ref busy, 0);
        }
    }
    private async Task ReportConnectionError(Exception error)
    {
        Storage.Audit("agent", "연결 오류: " + error.GetType().Name);
        try { await Ui.OnUi(this, () => { AddLog("연결 처리 실패: " + error.GetType().Name + " · 관리자에서 등록 정보와 연결 상태를 확인해 주세요.", audit: false); log.Visible = true; return true; }); }
        catch (ObjectDisposedException) { }
    }
    private Task<T> SessionUi<T>(AgentSession active, Func<T> action) => Ui.OnUi(this, () =>
    {
        active.Stop.Token.ThrowIfCancellationRequested();
        if (session != active) throw new OperationCanceledException("연결이 종료되었습니다.");
        return action();
    });
    private async Task<bool> ApproveAsync(string address, CancellationToken ct)
    {
        return await Ui.OnUi(this, () =>
        {
            if (ct.IsCancellationRequested) return false;
            Show(); WindowState = FormWindowState.Normal; Activate();
            AddLog("등록 확인 완료 · 연결 승인 창을 확인해 주세요.");
            using var dialog = new Form { Text = "관리자 연결 승인", TopMost = true, ShowInTaskbar = true, Size = new Size(640, 490), StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
            Ui.Style(dialog); dialog.MinimumSize = new Size(640, 490);
            var content = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), FlowDirection = FlowDirection.TopDown };
            content.Controls.Add(Ui.Label($"관리자 주소: {address}\n\n화면 공유·관리자 화면 표시·원격 입력·파일 받기·입력 잠금 및\nWindows 잠금 요청을 허용하시겠습니까?\n종료·재시작 요청은 즉시 실행됩니다.\n파일 보내기는 이 PC에서 직접 선택합니다.\n30초 안에 승인하지 않으면 거절합니다."));
            var remember = new CheckBox { Text = "이 등록 키를 가진 관리자의 다음 연결·전원 요청 자동 허용", AutoSize = true, Checked = true };
            content.Controls.Add(remember);
            content.Controls.Add(Ui.Button("이 연결 허용", (_, _) => { dialog.DialogResult = DialogResult.Yes; dialog.Close(); }, true));
            content.Controls.Add(Ui.Button("거절", (_, _) => { dialog.DialogResult = DialogResult.No; dialog.Close(); })); dialog.Controls.Add(content);
            using var timer = new System.Windows.Forms.Timer { Interval = 250 }; long end = Environment.TickCount64 + 30000;
            timer.Tick += (_, _) => { if (ct.IsCancellationRequested || Environment.TickCount64 >= end) { dialog.DialogResult = DialogResult.No; dialog.Close(); } }; timer.Start();
            bool allowed = dialog.ShowDialog(this) == DialogResult.Yes && !ct.IsCancellationRequested;
            if (allowed && remember.Checked) { settings = settings with { AutoApprove = true }; Storage.Save("agent.settings", settings); }
            return allowed;
        });
    }
    private static async Task ScreenLoop(AgentSession s)
    {
        int failures = 0;
        try
        {
            while (!s.Stop.IsCancellationRequested)
            {
                if (s.ScreenEnabled && (!s.FrameAcknowledgement || Volatile.Read(ref s.FramePending) == 0 || Environment.TickCount64 - Interlocked.Read(ref s.FrameSent) > 2000))
                {
                    try
                    {
                        byte[] bytes = DesktopCapture.Jpeg(1280, 55, s.PrivacyEnabled);
                        int sequence = s.FrameSequence = s.FrameSequence == int.MaxValue ? 1 : s.FrameSequence + 1;
                        Interlocked.Exchange(ref s.FramePending, sequence); Interlocked.Exchange(ref s.FrameSent, Environment.TickCount64);
                        await s.Channel.SendAsync(new("frame", Value: sequence, Data: bytes), s.Stop.Token); failures = 0;
                    }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException or ArgumentException)
                    {
                        if (++failures == 1) await s.Channel.SendAsync(new("notice", Text: "화면을 캡처할 수 없습니다. 로그인 상태 또는 보안 화면을 확인해 주세요."), s.Stop.Token);
                    }
                }
                await Task.Delay(80, s.Stop.Token);
            }
        }
        catch (Exception) { s.Stop.Cancel(); s.Channel.Dispose(); }
    }
    private void ExecuteInput(Packet p)
    {
        switch (p.Type)
        {
            case "move": input.Move(p.X, p.Y); break;
            case "button": input.Button(p.Value, p.Enabled); break;
            case "wheel": input.Wheel(p.Value); break;
            case "key": input.Key(p.Value, p.Enabled); break;
            case "text": input.Text(p.Text); break;
        }
    }
    private async Task SendLocalFile()
    {
        // The request can arrive on a network thread. Always select and open the dialog on the UI thread.
        AgentSession? s = session;
        if (s == null) return;
        if (!await s.SendFile.WaitAsync(0)) return;
        try
        {
            string? path = await Ui.OnUi(this, () =>
            {
                using var picker = new OpenFileDialog { Title = "관리자에게 보낼 파일을 직접 선택하세요" };
                return picker.ShowDialog(this) == DialogResult.OK ? picker.FileName : null;
            });
            if (path == null || session != s || s.Stop.IsCancellationRequested) return;
            await FileSender.SendAsync(s.Channel, path, s.Stop.Token);
            await Ui.OnUi(this, () => { AddLog("파일 보내기 완료", audit: true); return true; });
        }
        catch (Exception e) { await Ui.OnUi(this, () => { Ui.Error(this, e); return true; }); }
        finally { s.SendFile.Release(); }
    }
    private void OpenSubmission(AgentSession active, SubmissionTicket ticket, int seconds)
    {
        if (submission is { IsDisposed: false }) throw new InvalidOperationException("이미 과제 제출 창이 열려 있습니다.");
        // File selection needs local input even if this PC was previously in a focus session.
        active.ControlEnabled = active.PrivacyEnabled = false; StopDisplay(); ClearCurtains(); input.Unlock(); input.ReleaseRemote();
        var form = new SubmissionForm(ticket.Id, ticket.Title, seconds, ticket.MaxFiles, files => Task.Run(() => SendSubmission(active, ticket.Id, files)));
        submission = form;
        form.FormClosed += async (_, _) =>
        {
            if (submission == form) submission = null;
            if (!active.Stop.IsCancellationRequested)
                try { await active.Channel.SendAsync(new("assignment-status", Id: ticket.Id, Enabled: form.Completed, Text: form.Completed ? "제출 완료" : "제출 창 닫힘 또는 시간 종료"), active.Stop.Token); } catch (Exception) { }
        };
        form.Show(); form.Activate();
        _ = active.Channel.SendAsync(new("assignment-open", Id: ticket.Id), active.Stop.Token);
    }
    private async Task SendSubmission(AgentSession active, string requestId, string[] files)
    {
        if (!await active.SendFile.WaitAsync(0)) throw new IOException("다른 파일 전송이 진행 중입니다. 잠시 후 다시 제출해 주세요.");
        try
        {
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); submissionReplies[requestId] = ready;
            try
            {
                await active.Channel.SendAsync(new("assignment-submit", Id: requestId, Value: files.Length), active.Stop.Token);
                if (!await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), active.Stop.Token)) throw new IOException("제출 시간이 끝났거나 요청이 취소되었습니다.");
            }
            finally { submissionReplies.TryRemove(requestId, out _); }
            foreach (string file in files)
            {
                string id = Guid.NewGuid().ToString("N"); var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); savedReplies[id] = saved;
                try
                {
                    await FileSender.SendAsync(active.Channel, file, active.Stop.Token, requestId, id);
                    if (!await saved.Task.WaitAsync(TimeSpan.FromSeconds(15), active.Stop.Token)) throw new IOException("파일 저장을 확인하지 못했습니다.");
                }
                finally { savedReplies.TryRemove(id, out _); }
            }
        }
        catch { active.Stop.Cancel(); active.Channel.Dispose(); throw; }
        finally { active.SendFile.Release(); }
    }
    private async Task ChooseReceiveFolder(AgentSession active, FileReceiver receiver, Packet request)
    {
        if (Interlocked.CompareExchange(ref choosingFolder, 1, 0) != 0) return;
        try
        {
            string? folder = await Ui.OnUi(this, () =>
            {
                ShowWindow();
                using var picker = new FolderBrowserDialog { Description = "관리자가 파일을 보낼 저장 위치를 확인·선택하세요", UseDescriptionForTitle = true };
                if (request.Text.Length <= 1000 && Path.IsPathFullyQualified(request.Text) && Directory.Exists(request.Text)) picker.SelectedPath = request.Text;
                return picker.ShowDialog(this) == DialogResult.OK ? picker.SelectedPath : null;
            });
            if (active.Stop.IsCancellationRequested) return;
            if (folder != null) receiver.SetFolder(folder);
            await active.Channel.SendAsync(new("folder-selected", Id: request.Id, Text: folder ?? "", Enabled: folder != null), active.Stop.Token);
        }
        catch (Exception e) { Storage.Audit("agent", "저장 폴더 선택 실패: " + e.GetType().Name); }
        finally { Interlocked.Exchange(ref choosingFolder, 0); }
    }
    private void ClearCurtains()
    {
        foreach (var form in curtains) form.Dispose(); curtains.Clear();
    }
    private void SetPrivacy(AgentSession active, bool enabled)
    {
        ClearCurtains(); active.PrivacyEnabled = enabled;
        if (!enabled) { input.UnlockManual(); return; }
        StopDisplay(); input.SetLocks(true, true, 0);
        foreach (var screen in Screen.AllScreens)
        {
            var cover = new Form { FormBorderStyle = FormBorderStyle.None, Bounds = screen.Bounds, StartPosition = FormStartPosition.Manual, TopMost = true, ShowInTaskbar = false, BackColor = Color.Black };
            cover.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter, Text = "관리자 화면 잠금\n설정에서 지정한 긴급 해제 단축키로 해제할 수 있습니다." });
            curtains.Add(cover); cover.Show();
        }
        AddLog("화면·마우스·키보드 잠금 ON");
    }
    private void StartDisplay(bool exclusive)
    {
        StopDisplay(); ClearCurtains(); if (session is { } s) s.PrivacyEnabled = false;
        if (exclusive) { focusCursor = InvisibleCursor.Create(); Cursor = Cursor.Current = focusCursor; }
        sharedScreen = new SharedScreenForm(exclusive, focusCursor);
        sharedScreen.FormClosed += (_, _) => { if (!stoppingDisplay) { ClearCurtains(); input.UnlockManual(); sharedScreen = null; Cursor = Cursor.Current = Cursors.Default; focusCursor?.Dispose(); focusCursor = null; } };
        if (exclusive)
        {
            input.SetLocks(true, true, 0);
            foreach (var screen in Screen.AllScreens.Where(s => !s.Primary))
            {
                var cover = new Form { FormBorderStyle = FormBorderStyle.None, Bounds = screen.Bounds, StartPosition = FormStartPosition.Manual, TopMost = true, ShowInTaskbar = false, BackColor = Color.Black };
                cover.Cursor = focusCursor; curtains.Add(cover); cover.Show();
            }
        }
        sharedScreen.Show(); AddLog(exclusive ? "관리자 화면 공유 · 집중 모드" : "관리자 화면 공유 · 일반 모드");
    }
    private void StopDisplay()
    {
        stoppingDisplay = true;
        try { sharedScreen?.Dispose(); sharedScreen = null; ClearCurtains(); Cursor = Cursor.Current = Cursors.Default; focusCursor?.Dispose(); focusCursor = null; input.UnlockManual(); }
        finally { stoppingDisplay = false; }
    }
    private void RequestPower(string action)
    {
        string label = action == "restart" ? "다시 시작" : "종료";
        if (!settings.AutoApprove && MessageBox.Show(this, $"관리자가 PC {label}을 요청했습니다. 저장하지 않은 작업을 확인해 주세요.\n허용하면 즉시 실행됩니다. 허용할까요?", "전원 요청", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        input.Unlock(); input.ReleaseRemote();
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(action == "restart" ? "/r" : "/s"); start.ArgumentList.Add("/t"); start.ArgumentList.Add("0");
        System.Diagnostics.Process.Start(start); AddLog("등록 관리자 전원 요청 (즉시): " + label);
        tray.ShowBalloonTip(10000, "PC " + label + " 예정", "관리자 요청으로 즉시 실행합니다.", ToolTipIcon.Warning);
    }
    private void ExportPairing()
    {
        try
        {
            if (listener == null) SaveFields();
            var pairing = new Pairing(settings.Name, host.Text.Trim(), settings.Port, Security.Hash(certificate.RawData), settings.Token, LocalMac(host.Text.Trim())); pairing.Validate();
            using var picker = new SaveFileDialog { FileName = "pc-registration.json", Filter = "등록 파일 (*.json)|*.json" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllText(picker.FileName, JsonSerializer.Serialize(pairing, new JsonSerializerOptions { WriteIndented = true }));
            MessageBox.Show(this, "관리자 PC에서 이 파일을 가져오세요. 등록 키가 포함되어 있으므로 공개 저장소에 올리지 마세요. 등록 후 전달용 사본은 지워 주세요.", "등록 파일 저장");
        }
        catch (Exception e) { Ui.Error(this, e); }
    }
    private void RotateToken()
    {
        if (MessageBox.Show(this, "이전 관리자 등록 키를 무효화하고 연결을 종료할까요?", "등록 키 변경", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        StopListening(); settings = settings with { Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), AutoApprove = false }; Storage.Save("agent.settings", settings); AddLog("등록 키 변경 완료 · 등록 파일을 다시 전달해 주세요.");
    }
    private void Disconnect(string reason)
    {
        submission?.EndRequest(); submission = null;
        ClearCurtains(); StopDisplay();
        input.Unlock(); input.ReleaseRemote();
        if (session is { } s) { s.ControlEnabled = s.ScreenEnabled = false; s.Stop.Cancel(); s.Channel.Dispose(); }
        AddLog(reason);
    }
    private void StopListening()
    {
        Disconnect("대기·제어 종료"); listening?.Cancel(); listener?.Stop(); listener = null;
        name.Enabled = port.Enabled = true; status.Text = "연결 대기 꺼짐";
    }
    private async Task ShutdownAsync()
    {
        StopListening(); if (acceptTask != null) await acceptTask;
        Task[] pending; lock (handlerGate) pending = handlers.ToArray();
        await Task.WhenAll(pending); listening?.Dispose(); listening = null;
    }
    private void OpenInbox() { Directory.CreateDirectory(inbox); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(inbox) { UseShellExecute = true }); }
    private void AddLog(string text, bool audit = true)
    {
        if (log.TextLength > 40000) log.Clear(); log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\r\n"); if (audit) Storage.Audit("agent", text);
    }
}
