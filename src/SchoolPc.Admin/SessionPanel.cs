using SchoolPc.Core;
using SchoolPc.Windows;

namespace SchoolPc.Admin;

public sealed class SessionPanel : UserControl
{
    public AdminConnection Connection { get; }
    private readonly ScreenView view = new();
    private readonly CheckBox screen = new() { Text = "화면 보기", AutoSize = true, Margin = new Padding(8) };
    private readonly CheckBox control = new() { Text = "원격 입력", AutoSize = true, Margin = new Padding(8) };
    private readonly CheckBox mouseLock = new() { Text = "마우스 잠금", AutoSize = true, Margin = new Padding(8) };
    private readonly CheckBox keyboardLock = new() { Text = "키보드 잠금", AutoSize = true, Margin = new Padding(8) };
    private readonly NumericUpDown seconds = new() { Minimum = 1, Maximum = 120, Value = 60, Width = 60, Margin = new Padding(6) };
    private readonly Label status = Ui.Label("연결 승인 대기 중…");
    private readonly SemaphoreSlim events = new(1);
    private int pendingEvents;
    private int pendingX = -1, pendingY;
    private readonly System.Windows.Forms.Timer mouseTimer = new() { Interval = 40 };
    private bool mouseSending;
    private bool connected;
    private bool connecting;
    public bool IsConnecting => connecting;
    private bool disposed;
    private readonly SemaphoreSlim startLock = new(1);
    public SessionPanel(Pairing pairing)
    {
        Dock = DockStyle.Fill; Connection = new(pairing);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(6) };
        var showScreen = new ToolStripMenuItem("화면 보기") { Checked = false, CheckOnClick = true };
        showScreen.CheckedChanged += (_, _) => screen.Checked = showScreen.Checked;
        screen.CheckedChanged += (_, _) => showScreen.Checked = screen.Checked;
        toolbar.Controls.AddRange([control,
            Ui.MenuButton("파일", Ui.MenuItem("파일 보내기", async (_, _) => await SendFile()), Ui.MenuItem("파일 받기 요청", async (_, _) => await Run(new("request-file")))),
            Ui.MenuButton("잠금", Ui.MenuItem("입력 잠금 설정…", (_, _) => ShowLocks()), Ui.MenuItem("입력 잠금 해제", async (_, _) => await Unlock()), new ToolStripSeparator(), Ui.MenuItem("Windows 잠금…", async (_, _) => await LockWindows())),
            Ui.MenuButton("전원", Ui.MenuItem("재시작…", async (_, _) => await Power("restart")), Ui.MenuItem("전원 끄기…", async (_, _) => await Power("shutdown"))),
            Ui.MenuButton("더 보기", showScreen, Ui.MenuItem("메시지 보내기", (_, _) => Message()), Ui.MenuItem("대상 Wi-Fi 관리 요청", async (_, _) => await Run(new("network-settings")))),
            Ui.Button("연결 끊기", (_, _) => Connection.Dispose())]);
        layout.Controls.Add(toolbar, 0, 0); layout.Controls.Add(view, 0, 2); layout.Controls.Add(status, 0, 3); Controls.Add(layout);
        screen.CheckedChanged += async (_, _) => { if (connected) await Run(new("screen", Enabled: screen.Checked)); };
        control.CheckedChanged += async (_, _) =>
        {
            view.RemoteInput = connected && control.Checked; view.Invalidate();
            if (connected) await Run(new("control", Enabled: control.Checked));
            status.Text = control.Checked ? "화면을 클릭한 뒤 마우스와 키보드로 제어하세요. 한글 IME는 대상 PC 상태를 따릅니다." : "화면 보기 전용";
        };
        Connection.Received += Receive; Connection.Closed += reason => Post(() => { connected = false; view.RemoteInput = false; screen.Checked = control.Checked = false; status.Text = reason; });
        view.MoveRemote += (x, y) => { pendingX = x; pendingY = y; };
        mouseTimer.Tick += async (_, _) =>
        {
            if (mouseSending || pendingX < 0 || !view.RemoteInput) return;
            int x = pendingX, y = pendingY; pendingX = -1; mouseSending = true;
            try { await Input(new("move", X: x, Y: y)); } finally { mouseSending = false; }
        }; mouseTimer.Start();
        view.ButtonRemote += async (button, down) =>
        {
            if (pendingX >= 0) { int x = pendingX, y = pendingY; pendingX = -1; await Input(new("move", X: x, Y: y)); }
            await Input(new("button", Value: button, Enabled: down));
        };
        view.WheelRemote += async delta => await Input(new("wheel", Value: delta));
        view.KeyRemote += async (key, down) => await Input(new("key", Value: key, Enabled: down));
        view.ReleaseRemote += async () => { if (Connection.Connected) await Input(new("release")); };
    }
    public async Task ConnectAsync()
    {
        if (!await startLock.WaitAsync(0)) return;
        connecting = true;
        try { await Connection.ConnectAsync(); connected = true; screen.Checked = true; status.Text = "연결됨 · 조작하려면 원격 입력을 켜세요."; }
        catch (Exception e) { status.Text = e.Message; throw; }
        finally { connecting = false; startLock.Release(); }
    }
    private void Receive(Packet p)
    {
        if (p.Type == "frame" && p.Data != null)
        {
            try
            {
                using var stream = new MemoryStream(p.Data); using var loaded = Image.FromStream(stream);
                if (loaded.Width > 4096 || loaded.Height > 4096) throw new InvalidDataException("화면 크기가 너무 큽니다.");
                var bitmap = new Bitmap(loaded);
                if (!Post(() => view.SetFrame(bitmap))) bitmap.Dispose();
            }
            catch (Exception e) { Post(() => status.Text = "화면 읽기 오류: " + e.Message); }
        }
        else if (p.Type is "notice" or "error") Post(() => status.Text = p.Text);
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
    private async Task Input(Packet p)
    {
        if (!Connection.Connected || disposed) return;
        // Bound pending input; a stalled connection must not accumulate keystrokes indefinitely.
        if (Interlocked.Increment(ref pendingEvents) > 100) { Interlocked.Decrement(ref pendingEvents); Connection.Dispose(); return; }
        await events.WaitAsync();
        try { if (Connection.Connected) await Connection.SendAsync(p); }
        catch (Exception e) { status.Text = e.Message; }
        finally { events.Release(); Interlocked.Decrement(ref pendingEvents); }
    }
    private void ShowLocks()
    {
        using var dialog = new Form { Text = "입력 잠금", Size = new Size(450, 310), StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false };
        var mouse = new CheckBox { Text = "마우스 잠금", Checked = mouseLock.Checked, AutoSize = true };
        var keyboard = new CheckBox { Text = "키보드 잠금", Checked = keyboardLock.Checked, AutoSize = true };
        var duration = new NumericUpDown { Minimum = 1, Maximum = 120, Value = seconds.Value, Width = 100 };
        var fields = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), FlowDirection = FlowDirection.TopDown };
        fields.Controls.AddRange([mouse, keyboard, Ui.Label("자동 해제 시간 (초, 최대 120)"), duration,
            Ui.Button("적용", async (_, _) => { mouseLock.Checked = mouse.Checked; keyboardLock.Checked = keyboard.Checked; seconds.Value = duration.Value; await Run(new("locks", X: mouse.Checked ? 1 : 0, Y: keyboard.Checked ? 1 : 0, Value: (int)duration.Value)); dialog.Close(); }, true)]);
        dialog.Controls.Add(fields); dialog.ShowDialog(this);
    }
    public async Task Unlock() { mouseLock.Checked = keyboardLock.Checked = false; await Run(new("unlock")); }
    private async Task SendFile()
    {
        using var picker = new OpenFileDialog { Title = "관리 대상에게 보낼 파일 (최대 256MB)" };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try { status.Text = "파일 전송 중…"; await Connection.SendFileAsync(picker.FileName); status.Text = "전송 완료 · 대상 PC의 저장 확인을 기다립니다."; }
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
        if (MessageBox.Show(this, "대상 PC에 전원 작업 승인을 요청할까요?\n대상에서 승인하면 60초 후 실행됩니다.", "전원 요청", MessageBoxButtons.YesNo) == DialogResult.Yes) await Run(new("power", Text: action));
    }
    private async Task LockWindows()
    {
        if (MessageBox.Show(this, "대상 PC를 Windows 로그인 화면으로 잠글까요?\n다시 사용하려면 대상 PC에서 직접 로그인해야 합니다.", "Windows 잠금", MessageBoxButtons.YesNo) == DialogResult.Yes) await Run(new("windows-lock"));
    }
    protected override void Dispose(bool disposing)
    {
        disposed = true; if (disposing) { mouseTimer.Dispose(); Connection.Dispose(); } base.Dispose(disposing);
    }
}
