using SchoolPc.Core;
using SchoolPc.Windows;

namespace SchoolPc.Admin;

public sealed class SessionPanel : UserControl
{
    public AdminConnection Connection { get; }
    public event Action? DisconnectRequested;
    private readonly ScreenView view = new();
    private Form? fullScreen;
    private TableLayoutPanel layout = null!;
    private readonly object frameGate = new();
    private Bitmap? latestFrame;
    private bool framePosted;
    private readonly System.Windows.Forms.Timer shareTimer = new() { Interval = 100 };
    private bool sharing, sharingBusy, shareAwaitingAck;
    private int shareGeneration;
    private bool privacyLocked;
    private readonly CheckBox screen = new() { Text = "화면 보기", AutoSize = true, Margin = new Padding(8) };
    private readonly CheckBox control = new() { Text = "원격 입력", AutoSize = true, Margin = new Padding(8) };
    private readonly CheckBox blockLocal = new() { Text = "원격 입력 중 대상 입력 차단", AutoSize = true, Margin = new Padding(8) };
    private readonly CheckBox mouseLock = new() { Text = "마우스 잠금", AutoSize = true, Margin = new Padding(8) };
    private readonly CheckBox keyboardLock = new() { Text = "키보드 잠금", AutoSize = true, Margin = new Padding(8) };
    private readonly Label status = Ui.Label("대상 PC 접속 준비 중…");
    private readonly SemaphoreSlim events = new(1);
    private int pendingEvents;
    private int pendingX = -1, pendingY;
    private readonly System.Windows.Forms.Timer mouseTimer = new() { Interval = 16 };
    private bool mouseSending;
    private bool connected;
    private bool connecting;
    public bool IsConnecting => connecting;
    private bool disposed;
    private readonly SemaphoreSlim startLock = new(1);
    public SessionPanel(Pairing pairing)
    {
        Dock = DockStyle.Fill; Connection = new(pairing);
        layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(6) };
        var showScreen = new ToolStripMenuItem("화면 보기") { Checked = false, CheckOnClick = true };
        showScreen.CheckedChanged += (_, _) => screen.Checked = showScreen.Checked;
        screen.CheckedChanged += (_, _) => showScreen.Checked = screen.Checked;
        var textInput = new ToolStripMenuItem("영문·문자 직접 입력") { Checked = true, CheckOnClick = true };
        textInput.CheckedChanged += (_, _) => { view.TextInput = textInput.Checked; status.Text = textInput.Checked ? "입력한 문자를 대상에 전달합니다." : "키 입력 모드 · 한글 IME는 대상 PC 상태를 따릅니다."; };
        toolbar.Controls.AddRange([control, blockLocal,
            Ui.MenuButton("파일", Ui.MenuItem("받은 파일 저장 위치…", (_, _) => ChooseReceiveFolder()), Ui.MenuItem("파일 보내기", async (_, _) => await SendFile()), Ui.MenuItem("파일 받기 요청 (대상 선택)", async (_, _) => await RequestFile())),
            Ui.Button("전체 화면", (_, _) => FullScreen()),
            Ui.MenuButton("내 화면 공유", Ui.MenuItem("일반 창으로 공유", async (_, _) => await StartSharing(false)), Ui.MenuItem("집중 모드로 공유", async (_, _) => await StartSharing(true)), Ui.MenuItem("공유 종료", async (_, _) => await StopSharing())),
            Ui.MenuButton("잠금", Ui.MenuItem("화면 잠금 ON/OFF", async (_, _) => { privacyLocked = !privacyLocked; await Run(new("screen-lock", Enabled: privacyLocked)); }), Ui.MenuItem("입력 잠금 ON/OFF…", (_, _) => ShowLocks()), Ui.MenuItem("입력 잠금 해제", async (_, _) => await Unlock()), new ToolStripSeparator(), Ui.MenuItem("Windows 잠금…", async (_, _) => await LockWindows())),
            Ui.MenuButton("전원", Ui.MenuItem("재시작…", async (_, _) => await Power("restart")), Ui.MenuItem("전원 끄기…", async (_, _) => await Power("shutdown"))),
            Ui.MenuButton("더 보기", showScreen, textInput, Ui.MenuItem("메시지 보내기", (_, _) => Message()), Ui.MenuItem("대상 Wi-Fi 관리 요청", async (_, _) => await Run(new("network-settings")))),
            Ui.Button("연결 끊기", (_, _) => Disconnect())]);
        layout.Controls.Add(toolbar, 0, 0); layout.Controls.Add(view, 0, 2); layout.Controls.Add(status, 0, 3); Controls.Add(layout);
        screen.CheckedChanged += async (_, _) => { if (connected) await Run(new("screen", Enabled: screen.Checked)); };
        control.CheckedChanged += async (_, _) =>
        {
            pendingX = -1;
            view.RemoteInput = connected && control.Checked; view.Invalidate();
            if (connected) await Run(new("control", Enabled: control.Checked, X: blockLocal.Checked ? 1 : 0));
            status.Text = control.Checked ? "화면의 입력 칸을 클릭한 뒤 입력하세요. 한글 IME는 더 보기에서 문자 직접 입력을 꺼 주세요." : "화면 보기 전용";
        };
        blockLocal.CheckedChanged += async (_, _) => { if (connected) await Run(new("control", Enabled: control.Checked, X: blockLocal.Checked ? 1 : 0)); };
        shareTimer.Tick += async (_, _) =>
        {
            if (!sharing || sharingBusy || shareAwaitingAck || !Connection.Connected) return;
            sharingBusy = true; int generation = shareGeneration;
            try
            {
                byte[] bytes = await Task.Run(() => DesktopCapture.Jpeg(1280, 55));
                if (sharing && generation == shareGeneration) { shareAwaitingAck = true; await Connection.SendAsync(new("broadcast-frame", Data: bytes)); }
            }
            catch (Exception e) { sharing = false; shareTimer.Stop(); await Run(new("broadcast-stop")); status.Text = "내 화면 공유 중단: " + e.Message; }
            finally { sharingBusy = false; }
        };
        Connection.Progress += message => Post(() => status.Text = message);
        Connection.Received += Receive; Connection.Closed += reason => Post(() => { sharing = false; shareGeneration++; shareTimer.Stop(); fullScreen?.Close(); privacyLocked = false; mouseLock.Checked = keyboardLock.Checked = false; pendingX = -1; connected = false; view.RemoteInput = false; screen.Checked = control.Checked = false; status.Text = reason; });
        view.MoveRemote += (x, y) => { pendingX = x; pendingY = y; };
        mouseTimer.Tick += async (_, _) =>
        {
            if (mouseSending || pendingX < 0 || !view.RemoteInput || !view.Focused) return;
            int x = pendingX, y = pendingY; pendingX = -1; mouseSending = true;
            try { await Input(new("move", X: x, Y: y)); } finally { mouseSending = false; }
        }; mouseTimer.Start();
        view.ButtonRemote += async (button, down) =>
        {
            if (pendingX >= 0)
            {
                int x = pendingX, y = pendingY; pendingX = -1;
                await InputBatch(new("move", X: x, Y: y), new("button", Value: button, Enabled: down));
            }
            else await Input(new("button", Value: button, Enabled: down));
        };
        view.WheelRemote += async delta => await Input(new("wheel", Value: delta));
        view.KeyRemote += async (key, down) => await Input(new("key", Value: key, Enabled: down));
        view.TextRemote += async text => await Input(new("text", Text: text));
        view.ReleaseRemote += async () => { pendingX = -1; if (Connection.Connected) await Input(new("release")); };
    }
    public void Disconnect()
    {
        // Notify the workspace before cancellation so list clicks cannot start another session.
        DisconnectRequested?.Invoke();
        pendingX = -1; connected = false; view.RemoteInput = false;
        sharing = false; shareGeneration++; shareTimer.Stop();
        Connection.Dispose();
        status.Text = "연결을 끊었습니다. 다시 연결하려면 ‘선택한 PC 연결’을 누르세요.";
    }
    public async Task ConnectAsync()
    {
        if (!await startLock.WaitAsync(0)) return;
        connecting = true;
        try { await Connection.ConnectAsync(); if (!Connection.Connected) throw new IOException("연결 요청이 취소되었습니다."); connected = true; screen.Checked = true; status.Text = "연결됨 · 조작하려면 원격 입력을 켜세요."; }
        catch (Exception e) { status.Text = e.Message; throw; }
        finally { connecting = false; startLock.Release(); }
    }
    private void Receive(Packet p)
    {
        if (p.Type == "broadcast-ack") { Post(() => shareAwaitingAck = false); return; }
        if (p.Type == "frame" && p.Data != null)
        {
            try
            {
                using var stream = new MemoryStream(p.Data); using var loaded = Image.FromStream(stream);
                if (loaded.Width > 4096 || loaded.Height > 4096) throw new InvalidDataException("화면 크기가 너무 큽니다.");
                var bitmap = new Bitmap(loaded);
                QueueFrame(bitmap);
            }
            catch (Exception e) { Post(() => status.Text = "화면 읽기 오류: " + e.Message); }
        }
        else if (p.Type is "notice" or "error") Post(() => status.Text = p.Text);
    }
    private void QueueFrame(Bitmap bitmap)
    {
        lock (frameGate)
        {
            if (disposed) { bitmap.Dispose(); return; }
            latestFrame?.Dispose(); latestFrame = bitmap;
            if (framePosted) return;
            framePosted = true;
            if (!Post(() =>
            {
                Bitmap? next;
                lock (frameGate) { next = latestFrame; latestFrame = null; framePosted = false; }
                if (next != null) view.SetFrame(next);
                if (Connection.FrameAcknowledgement) _ = Run(new("frame-ack"));
            })) { latestFrame.Dispose(); latestFrame = null; framePosted = false; }
        }
    }
    private void FullScreen()
    {
        if (fullScreen != null) { fullScreen.Activate(); return; }
        fullScreen = new Form { Text = "원격 화면 · F11로 돌아가기", FormBorderStyle = FormBorderStyle.None, WindowState = FormWindowState.Maximized, KeyPreview = true };
        var back = Ui.Button("전체 화면 종료 (F11)", (_, _) => fullScreen?.Close()); back.Dock = DockStyle.Top;
        layout.Controls.Remove(view); fullScreen.Controls.Add(view); fullScreen.Controls.Add(back);
        fullScreen.KeyDown += (_, e) => { if (e.KeyCode == Keys.F11) { e.Handled = e.SuppressKeyPress = true; fullScreen?.Close(); } };
        fullScreen.FormClosed += (_, _) => { fullScreen = null; if (!disposed) { layout.Controls.Add(view, 0, 2); view.Focus(); } };
        fullScreen.Show(FindForm()); view.Focus();
    }
    private async Task StartSharing(bool exclusive)
    {
        if (!Connection.Connected) return;
        shareGeneration++; sharing = false; shareAwaitingAck = false;
        await Run(new("broadcast-start", Enabled: exclusive)); sharing = true; shareTimer.Start();
        status.Text = exclusive ? "내 화면 공유 중 · 대상 집중 모드" : "내 화면 공유 중 · 대상은 다른 작업도 가능";
    }
    private async Task StopSharing()
    {
        sharing = false; shareGeneration++; shareTimer.Stop(); await Run(new("broadcast-stop"));
    }
    private async Task RequestFile()
    {
        if (!ChooseReceiveFolder()) return;
        await Run(new("request-file")); status.Text = "대상에서 보낼 파일을 선택하면 지정한 폴더로 복사됩니다.";
    }
    private bool ChooseReceiveFolder()
    {
        using var folder = new FolderBrowserDialog { Description = "관리자 PC에 받은 파일을 저장할 폴더 (대상이 직접 제출한 파일에도 적용)", UseDescriptionForTitle = true, SelectedPath = Connection.ReceiveFolder ?? "" };
        if (folder.ShowDialog(this) != DialogResult.OK) return false;
        Connection.ReceiveFolder = folder.SelectedPath;
        var preferences = Storage.Load<Dictionary<string, string>>("admin.receive-folders") ?? [];
        preferences[Connection.Pairing.Fingerprint] = folder.SelectedPath;
        Storage.Save("admin.receive-folders", preferences);
        status.Text = "받은 파일 저장 위치: " + folder.SelectedPath;
        return true;
    }
    private bool Post(Action action)
    {
        if (disposed || IsDisposed || !IsHandleCreated) return false;
        try { BeginInvoke((Action)(() => { if (!disposed) action(); })); return true; } catch (InvalidOperationException) { return false; }
    }
    public async Task Run(Packet packet)
    {
        try { await Connection.SendAsync(packet); }
        catch (Exception e) { status.Text = e.Message; }
    }
    private Task Input(Packet packet) => InputBatch([packet]);
    private async Task InputBatch(params Packet[] packets)
    {
        if (!Connection.Connected || disposed) return;
        // Bound pending input; a stalled connection must not accumulate keystrokes indefinitely.
        if (Interlocked.Increment(ref pendingEvents) > 100) { Interlocked.Decrement(ref pendingEvents); Connection.Dispose(); return; }
        await events.WaitAsync();
        try
        {
            foreach (var p in packets)
            {
                if (!Connection.Connected) break;
                if (p.Type == "move" && (!view.RemoteInput || !view.Focused)) continue;
                await Connection.SendAsync(p);
            }
        }
        catch (Exception e) { status.Text = e.Message; }
        finally { events.Release(); Interlocked.Decrement(ref pendingEvents); }
    }
    private void ShowLocks()
    {
        using var dialog = new Form { Text = "입력 잠금", Size = new Size(450, 310), StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
        var mouse = new CheckBox { Text = "마우스 잠금", Checked = mouseLock.Checked, AutoSize = true };
        var keyboard = new CheckBox { Text = "키보드 잠금", Checked = keyboardLock.Checked, AutoSize = true };
        var fields = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), FlowDirection = FlowDirection.TopDown };
        fields.Controls.AddRange([mouse, keyboard, Ui.Label("연결 중 ON/OFF · 긴급 해제와 연결 종료 시 해제"),
            Ui.Button("적용", async (_, _) => { mouseLock.Checked = mouse.Checked; keyboardLock.Checked = keyboard.Checked; await Run(new("locks", X: mouse.Checked ? 1 : 0, Y: keyboard.Checked ? 1 : 0, Value: 0)); dialog.Close(); }, true)]);
        dialog.Controls.Add(fields); dialog.ShowDialog(this);
    }
    public async Task Unlock() { privacyLocked = false; mouseLock.Checked = keyboardLock.Checked = false; await Run(new("unlock")); }
    private async Task SendFile()
    {
        using var picker = new OpenFileDialog { Title = "관리 대상에게 보낼 파일 (최대 256MB)" };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            string proposed = Microsoft.VisualBasic.Interaction.InputBox("대상 PC의 저장 폴더 경로를 입력하세요. 대상에서 확인·선택합니다.\n비워 두면 대상에서 폴더를 직접 선택합니다.", "대상 저장 위치");
            status.Text = "대상에서 저장 폴더 확인을 기다립니다…";
            string folder = await Connection.SelectTargetFolderAsync(proposed);
            status.Text = "파일 전송 중…"; await Connection.SendFileAsync(picker.FileName); status.Text = "전송 완료 · 대상 저장 위치: " + folder; }
        catch (Exception e) { status.Text = e.Message; }
    }
    private void Message()
    {
        using var dialog = new Form { Text = "메시지 보내기", Size = new Size(520, 280), StartPosition = FormStartPosition.CenterParent };
        var text = new TextBox { Multiline = true, Dock = DockStyle.Fill, MaxLength = 1000 };
        var send = Ui.Button("보내기", async (_, _) => { await Run(new("message", Text: text.Text)); dialog.Close(); }, true); send.Dock = DockStyle.Bottom;
        dialog.Controls.Add(text); dialog.Controls.Add(send); dialog.ShowDialog(this);
    }
    private async Task Power(string action)
    {
        if (MessageBox.Show(this, "대상 PC에 전원 작업을 요청할까요?\n자동 허용 상태이면 추가 승인 없이 즉시 실행됩니다. 저장하지 않은 작업을 확인해 주세요.", "전원 요청", MessageBoxButtons.YesNo) == DialogResult.Yes) await Run(new("power", Text: action));
    }
    private async Task LockWindows()
    {
        if (MessageBox.Show(this, "대상 PC를 Windows 로그인 화면으로 잠글까요?\n다시 사용하려면 대상 PC에서 직접 로그인해야 합니다.", "Windows 잠금", MessageBoxButtons.YesNo) == DialogResult.Yes) await Run(new("windows-lock"));
    }
    protected override void Dispose(bool disposing)
    {
        disposed = true; if (disposing) { shareTimer.Dispose(); mouseTimer.Dispose(); fullScreen?.Close();
            lock (frameGate) { latestFrame?.Dispose(); latestFrame = null; }
            view.Dispose(); Connection.Dispose(); } base.Dispose(disposing);
    }
}
