namespace SchoolPc.Windows;

public sealed class FileArrival : Form
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private Cursor? hiddenCursor;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000; return p; } }
    private FileArrival(Control owner, string path)
    {
        Text = "파일 받기 완료"; TopMost = true; ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual; Size = new Size(400, 165); BackColor = Color.White;
        var area = Screen.FromControl(owner).WorkingArea; Location = new(area.Right - Width - 16, area.Bottom - Height - 16);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 2 };
        content.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "파일이 저장되었습니다.\n" + Path.GetFileName(path), AutoEllipsis = true }, 0, 0);
        content.Controls.Add(Ui.Button("저장 폴더 열기", (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true })), 0, 1);
        Controls.Add(content); long deadline = Environment.TickCount64 + 6000;
        timer.Tick += (_, _) =>
        {
            if (owner.IsDisposed || Environment.TickCount64 >= deadline) { Close(); return; }
            if (owner.Cursor.Handle == Cursors.Default.Handle) Cursor = Cursors.Default;
            else Cursor = hiddenCursor ??= InvisibleCursor.Create();
        }; timer.Start();
    }
    public static void Notify(Control owner, string path)
    {
        var toast = new FileArrival(owner, path); toast.Show(owner.FindForm());
        System.Media.SystemSounds.Asterisk.Play();
    }
    protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); hiddenCursor?.Dispose(); } base.Dispose(disposing); }
}
