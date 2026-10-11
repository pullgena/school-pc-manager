using SchoolPc.Windows;

namespace SchoolPc.Agent;

internal sealed class SubmissionForm : Form
{
    internal string RequestId { get; }
    private readonly int maxFiles;
    private readonly long deadline;
    private readonly Func<string[], Task> send;
    private readonly ListBox files = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly Label remaining = Ui.Label("");
    private readonly Label status = Ui.Label("파일 선택 후 제출해 주세요.");
    private readonly Button choose, submit;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private bool sending, completed;
    internal bool Completed => completed;
    internal SubmissionForm(string id, string title, int seconds, int maxFiles, Func<string[], Task> send)
    {
        RequestId = id; this.maxFiles = maxFiles; this.send = send; deadline = Environment.TickCount64 + seconds * 1000L;
        Text = "과제 제출 · " + title; TopMost = true; Size = new(620, 460); MinimumSize = new(580, 420); StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), RowCount = 5 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        choose = Ui.Button("제출할 파일 선택", (_, _) =>
        {
            using var picker = new OpenFileDialog { Title = "제출할 파일 선택", Multiselect = true };
            if (picker.ShowDialog(this) == DialogResult.OK) SetFiles(picker.FileNames);
        });
        submit = Ui.Button("제출", async (_, _) => await SubmitAsync(), true);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; buttons.Controls.AddRange([choose, submit, Ui.Button("닫기", (_, _) => Close())]);
        layout.Controls.Add(Ui.Label(title, 18), 0, 0); layout.Controls.Add(remaining, 0, 1); layout.Controls.Add(files, 0, 2); layout.Controls.Add(status, 0, 3); layout.Controls.Add(buttons, 0, 4); Controls.Add(layout);
        timer.Tick += (_, _) =>
        {
            long secondsLeft = Math.Max(0, (deadline - Environment.TickCount64 + 999) / 1000);
            remaining.Text = $"남은 시간 {secondsLeft / 60:00}:{secondsLeft % 60:00} · 선택 {files.Items.Count}/{maxFiles}개";
            if (!sending && secondsLeft == 0) { status.Text = "제출 시간이 종료되었습니다."; choose.Enabled = submit.Enabled = false; if (Environment.TickCount64 >= deadline + 3000) Close(); }
        }; timer.Start();
        FormClosing += (_, e) => { if (sending) e.Cancel = true; };
    }
    internal void SetFiles(string[] paths)
    {
        if (paths.Length < 1 || paths.Length > maxFiles) { status.Text = $"파일은 1~{maxFiles}개까지 선택해 주세요."; return; }
        if (paths.Any(p => !File.Exists(p) || new FileInfo(p).Length > SchoolPc.Core.FileReceiver.MaxFile)) { status.Text = "파일을 확인해 주세요. 파일당 최대 256MB입니다."; return; }
        files.Items.Clear(); files.Items.AddRange(paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()); status.Text = $"{files.Items.Count}개 선택했습니다. 제출을 눌러 주세요.";
    }
    internal async Task SubmitAsync()
    {
        if (sending || Environment.TickCount64 >= deadline || files.Items.Count == 0) return;
        sending = true; choose.Enabled = submit.Enabled = false; status.Text = "전송 중… 저장 완료를 확인하고 있습니다.";
        try { await send(files.Items.Cast<string>().ToArray()); completed = true; sending = false; Close(); }
        catch (Exception e) { if (!IsDisposed) { status.Text = "제출 실패: " + e.Message; choose.Enabled = submit.Enabled = true; } }
        finally { sending = false; }
    }
    internal void EndRequest() { sending = false; Close(); }
    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}
