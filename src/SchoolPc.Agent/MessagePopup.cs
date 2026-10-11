namespace SchoolPc.Agent;

internal sealed class MessagePopup : Form
{
    private readonly TextBox message = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
    protected override bool ShowWithoutActivation => true;

    public MessagePopup(string text)
    {
        Text = "관리자 메시지"; TopMost = true; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen; Size = new Size(460, 300);
        MinimumSize = new Size(360, 240); Font = new Font("맑은 고딕", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), RowCount = 2, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        var close = new Button { Text = "확인", Dock = DockStyle.Right, Width = 90 };
        close.Click += (_, _) => Close(); AcceptButton = close; CancelButton = close;
        layout.Controls.Add(message, 0, 0); layout.Controls.Add(close, 0, 1); Controls.Add(layout);
        AppendMessage(text);
    }

    public void AppendMessage(string text)
    {
        if (message.TextLength > 0) message.AppendText("\r\n\r\n──────────\r\n");
        message.AppendText(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        message.SelectionStart = message.TextLength; message.ScrollToCaret();
    }
}
