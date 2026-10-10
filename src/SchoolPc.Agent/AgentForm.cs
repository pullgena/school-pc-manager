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

public sealed record AgentSettings(string Name, int Port, string Token);
internal sealed class AgentSession(Channel channel, CancellationTokenSource stop, string address)
{
    public Channel Channel { get; } = channel;
    public CancellationTokenSource Stop { get; } = stop;
    public string Address { get; } = address;
    public volatile bool ScreenEnabled, ControlEnabled;
    public long LastHeartbeat = Environment.TickCount64;
    public readonly SemaphoreSlim SendFile = new(1);
}

public sealed class AgentForm : Form
{
    private AgentSettings settings;
    private readonly X509Certificate2 certificate;
    private readonly NativeInput input = new();
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
    private Task? acceptTask;
    private readonly HashSet<Task> handlers = [];
    private readonly object handlerGate = new();

    public AgentForm()
    {
        Text = "School PC · 관리 대상"; Size = new Size(850, 670); Ui.Style(this);
        settings = Storage.Load<AgentSettings>("agent.settings") ?? new(Environment.MachineName, 45832, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        Storage.Save("agent.settings", settings);
        certificate = LoadCertificate();
        name.Text = settings.Name; port.Value = settings.Port; host.Text = LocalAddress();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 7 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(Ui.Label("내 PC 관리 연결", 22), 0, 0);
        var fields = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        fields.Controls.AddRange([Ui.Label("PC 이름"), name, Ui.Label("포트"), port, Ui.Label("연결 주소"), host]); layout.Controls.Add(fields, 0, 1);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([Ui.Button("연결 대기 시작", (_, _) => StartListening(), true), Ui.Button("대기·제어 종료", (_, _) => StopListening()),
            Ui.Button("관리자 등록 파일 저장", (_, _) => ExportPairing()), Ui.Button("등록 키 변경", (_, _) => RotateToken()),
            Ui.Button("관리자에게 파일 보내기", async (_, _) => await SendLocalFile()), Ui.Button("받은 파일 열기", (_, _) => OpenInbox()),
            Ui.Button("Windows Wi-Fi 관리", (_, _) => new WifiForm().Show(this)), Ui.Button("현재 프로그램 제거", async (_, _) => await Removal.RequestAsync(this, ShutdownAsync)), Ui.Button("업데이트 확인", async (_, _) => await Updater.CheckAsync(this, ShutdownAsync))]);
        layout.Controls.Add(buttons, 0, 2);
        layout.Controls.Add(status, 0, 3); layout.Controls.Add(lockStatus, 0, 4); layout.Controls.Add(log, 0, 5);
        layout.Controls.Add(Ui.Label("접속 시 이 PC에서 승인합니다. 긴급 종료: Ctrl+Alt+F12 · 잠금은 최대 120초\nWindows 로그인 화면과 UAC 보안 화면은 원격 제어하지 않습니다."), 0, 6);
        Controls.Add(layout);
        input.Emergency += () => { if (!IsDisposed) BeginInvoke((Action)(() => Disconnect("긴급 해제"))); };
        watchdog.Tick += (_, _) =>
        {
            lockStatus.Text = input.MouseLocked || input.KeyboardLocked ? $"마우스: {(input.MouseLocked ? "잠금" : "사용 가능")} / 키보드: {(input.KeyboardLocked ? "잠금" : "사용 가능")} · {input.Remaining}초 뒤 자동 해제" : "입력 잠금 없음";
            if (session is { } s && Environment.TickCount64 - Interlocked.Read(ref s.LastHeartbeat) > 15000) Disconnect("연결 응답 시간 초과");
        };
        watchdog.Start();
        FormClosing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true; closing = true;
            await ShutdownAsync(); input.Dispose(); certificate.Dispose(); watchdog.Dispose(); Close();
        };
        AddLog("등록 파일을 관리자 PC로 전달한 뒤 연결 대기를 시작해 주세요.");
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
    private static string LocalAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).First(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Address.ToString();
        }
        catch { return "127.0.0.1"; }
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
            acceptTask = AcceptLoop(listener, listening.Token); AddLog("연결 대기 시작 (TLS 암호화)");
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
            { Storage.Audit("agent", $"인증 실패 {address}"); return; }
            bool approved = await ApproveAsync(address, stop.Token);
            if (!approved) { await channel.SendAsync(new("denied", Text: "관리 대상에서 연결을 승인하지 않았습니다."), stop.Token); return; }
            active = new(channel, stop, address);
            await Ui.OnUi(this, () => { session = active; status.Text = $"관리자 연결됨 · {address}"; AddLog("관리자 연결 승인: " + address); return true; });
            await channel.SendAsync(new("accepted", Text: settings.Name), stop.Token);
            frames = ScreenLoop(active);
            while (!stop.IsCancellationRequested)
            {
                var packet = await channel.ReadAsync(stop.Token);
                if (packet.Type == "ping") { Interlocked.Exchange(ref active.LastHeartbeat, Environment.TickCount64); await channel.SendAsync(new("pong"), stop.Token); continue; }
                try
                {
                    switch (packet.Type)
                    {
                        case "screen": active.ScreenEnabled = packet.Enabled; Storage.Audit("agent", packet.Enabled ? "화면 공유 시작" : "화면 공유 종료"); break;
                        case "control":
                            await Ui.OnUi(this, () => { active.ControlEnabled = packet.Enabled; if (!packet.Enabled) { input.ReleaseRemote(); input.Unlock(); } AddLog(packet.Enabled ? "원격 입력 켜짐" : "원격 입력 꺼짐"); return true; }); break;
                        case "move": case "button": case "wheel": case "key":
                            if (!active.ControlEnabled) throw new InvalidOperationException("원격 입력이 꺼져 있습니다.");
                            await Ui.OnUi(this, () => { ExecuteInput(packet); return true; }); break;
                        case "release": await Ui.OnUi(this, () => { input.ReleaseRemote(); return true; }); break;
                        case "locks":
                            await Ui.OnUi(this, () => { input.SetLocks(packet.X == 1, packet.Y == 1, packet.Value); AddLog($"입력 잠금 변경 · 최대 {Math.Clamp(packet.Value, 1, 120)}초"); return true; });
                            await channel.SendAsync(new("notice", Text: "입력 잠금을 변경했습니다. 제한 시간 뒤 자동 해제됩니다."), stop.Token); break;
                        case "unlock": await Ui.OnUi(this, () => { input.Unlock(); input.ReleaseRemote(); AddLog("입력 잠금 해제"); return true; }); break;
                        case "windows-lock":
                            await Ui.OnUi(this, () => { input.Unlock(); input.ReleaseRemote(); NativeInput.LockWindows(); AddLog("Windows 잠금 요청"); return true; }); break;
                        case "message":
                            if (packet.Text.Length > 1000) throw new InvalidDataException("메시지가 너무 깁니다.");
                            await Ui.OnUi(this, () => { AddLog("관리자 메시지: " + packet.Text, audit: false); Show(); WindowState = FormWindowState.Normal; Activate(); return true; }); break;
                        case "file-begin": receiver.Begin(packet); break;
                        case "file-chunk": receiver.Chunk(packet); break;
                        case "file-end":
                            string saved = receiver.End(packet);
                            await Ui.OnUi(this, () => { AddLog("파일 받기 완료: " + Path.GetFileName(saved), audit: false); return true; });
                            Storage.Audit("agent", "파일 받기 완료");
                            await channel.SendAsync(new("notice", Text: "관리 대상 PC의 받은 파일 폴더에 저장되었습니다."), stop.Token); break;
                        case "network-settings":
                            await Ui.OnUi(this, () => { input.Unlock(); new WifiForm().Show(this); AddLog("관리자 Wi-Fi 관리 창 열기 요청"); return true; }); break;
                        case "request-file":
                            _ = SendLocalFile(); break;
                        case "power":
                            if (packet.Text is not ("restart" or "shutdown")) throw new InvalidDataException("잘못된 전원 요청입니다.");
                            await Ui.OnUi(this, () => { RequestPower(packet.Text); return true; }); break;
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
        { Storage.Audit("agent", "연결 종료: " + e.GetType().Name); }
        catch (Exception e) { Storage.Audit("agent", "연결 오류: " + e.GetType().Name); }
        finally
        {
            stop.Cancel(); client.Dispose();
            if (frames != null) { try { await frames; } catch { } }
            try { await Ui.OnUi(this, () => { if (session == active && active != null) { session = null; input.Unlock(); input.ReleaseRemote(); status.Text = listener == null ? "연결 대기 꺼짐" : "연결 대기 중"; AddLog("관리자 연결 종료 · 입력 잠금 해제"); } return true; }); } catch (ObjectDisposedException) { }
            Interlocked.Exchange(ref busy, 0);
        }
    }
    private async Task<bool> ApproveAsync(string address, CancellationToken ct)
    {
        return await Ui.OnUi(this, () =>
        {
            if (ct.IsCancellationRequested) return false;
            using var dialog = new Form { Text = "관리자 연결 승인", Size = new Size(570, 390), StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
            Ui.Style(dialog); dialog.MinimumSize = new Size(570, 390);
            var content = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), FlowDirection = FlowDirection.TopDown };
            content.Controls.Add(Ui.Label($"관리자 주소: {address}\n\n화면 공유, 원격 입력, 파일 받기, 입력 잠금 및\nWindows 잠금 요청을 허용하시겠습니까?\n파일 보내기는 이 PC에서 직접 선택합니다.\n30초 안에 승인하지 않으면 거절합니다."));
            content.Controls.Add(Ui.Button("이 연결 허용", (_, _) => { dialog.DialogResult = DialogResult.Yes; dialog.Close(); }, true));
            content.Controls.Add(Ui.Button("거절", (_, _) => { dialog.DialogResult = DialogResult.No; dialog.Close(); })); dialog.Controls.Add(content);
            using var timer = new System.Windows.Forms.Timer { Interval = 250 }; long end = Environment.TickCount64 + 30000;
            timer.Tick += (_, _) => { if (ct.IsCancellationRequested || Environment.TickCount64 >= end) { dialog.DialogResult = DialogResult.No; dialog.Close(); } }; timer.Start();
            return dialog.ShowDialog(this) == DialogResult.Yes && !ct.IsCancellationRequested;
        });
    }
    private static async Task ScreenLoop(AgentSession s)
    {
        int failures = 0;
        try
        {
            while (!s.Stop.IsCancellationRequested)
            {
                if (s.ScreenEnabled)
                {
                    try
                    {
                        // Primary monitor, desktop pixels; secure desktops intentionally remain inaccessible.
                        var bounds = Screen.PrimaryScreen!.Bounds;
                        using var original = new Bitmap(bounds.Width, bounds.Height);
                        using (var graphics = Graphics.FromImage(original)) graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                        double ratio = Math.Min(1, 1600d / bounds.Width);
                        using var scaled = new Bitmap(original, new Size(Math.Max(1, (int)(bounds.Width * ratio)), Math.Max(1, (int)(bounds.Height * ratio))));
                        using var bytes = new MemoryStream();
                        var encoder = ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");
                        using var parameters = new EncoderParameters(1); parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 65L);
                        scaled.Save(bytes, encoder, parameters);
                        await s.Channel.SendAsync(new("frame", X: bounds.Width, Y: bounds.Height, Data: bytes.ToArray()), s.Stop.Token); failures = 0;
                    }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException or ArgumentException)
                    {
                        if (++failures == 1) await s.Channel.SendAsync(new("notice", Text: "화면을 캡처할 수 없습니다. 로그인 상태 또는 보안 화면을 확인해 주세요."), s.Stop.Token);
                    }
                }
                await Task.Delay(300, s.Stop.Token);
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
    private void RequestPower(string action)
    {
        string label = action == "restart" ? "다시 시작" : "종료";
        if (MessageBox.Show(this, $"관리자가 PC {label}을 요청했습니다. 저장하지 않은 작업을 확인해 주세요.\n허용하면 60초 후 실행됩니다. 허용할까요?", "전원 요청", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        input.Unlock(); input.ReleaseRemote();
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(action == "restart" ? "/r" : "/s"); start.ArgumentList.Add("/t"); start.ArgumentList.Add("60");
        System.Diagnostics.Process.Start(start); AddLog("승인된 전원 요청: " + label);
    }
    private void ExportPairing()
    {
        try
        {
            if (listener == null) SaveFields();
            var pairing = new Pairing(settings.Name, host.Text.Trim(), settings.Port, Security.Hash(certificate.RawData), settings.Token); pairing.Validate();
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
        StopListening(); settings = settings with { Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) }; Storage.Save("agent.settings", settings); AddLog("등록 키 변경 완료 · 등록 파일을 다시 전달해 주세요.");
    }
    private void Disconnect(string reason)
    {
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
