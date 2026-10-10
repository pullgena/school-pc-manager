namespace SchoolPc.Windows;

public static class Ui
{
    public static readonly Color Blue = Color.FromArgb(41, 98, 255);
    public static void Style(Form form)
    {
        form.Font = new Font("맑은 고딕", 10);
        form.BackColor = Color.FromArgb(244, 247, 252);
        form.StartPosition = FormStartPosition.CenterScreen;
        form.MinimumSize = new Size(720, 520);
    }
    public static Button Button(string text, EventHandler handler, bool primary = false)
    {
        Button button = new() { Text = text, AutoSize = true, Height = 38, Padding = new Padding(10, 5, 10, 5), Margin = new Padding(4), FlatStyle = FlatStyle.Flat, BackColor = primary ? Blue : Color.White, ForeColor = primary ? Color.White : Color.FromArgb(30, 40, 60) };
        button.FlatAppearance.BorderColor = Color.FromArgb(214, 220, 230); button.Click += handler; return button;
    }
    public static ToolStripMenuItem MenuItem(string text, EventHandler handler)
    {
        var item = new ToolStripMenuItem(text); item.Click += handler; return item;
    }
    public static Button MenuButton(string text, params ToolStripItem[] items)
    {
        var menu = new ContextMenuStrip { Font = new Font("맑은 고딕", 10), ShowImageMargin = false };
        menu.Items.AddRange(items);
        var button = Button(text + " ▾", (_, _) => { });
        button.Click += (_, _) => menu.Show(button, new Point(0, button.Height));
        button.Disposed += (_, _) => menu.Dispose(); return button;
    }
    public static Label Label(string text, float size = 10) => new() { Text = text, AutoSize = true, Font = new Font("맑은 고딕", size), Margin = new Padding(6), ForeColor = Color.FromArgb(30, 40, 60) };
    public static void Error(IWin32Window owner, Exception e) => MessageBox.Show(owner, e.Message, "작업을 완료하지 못했습니다", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    public static Task<T> OnUi<T>(Control control, Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (control.IsDisposed) { tcs.SetException(new ObjectDisposedException(control.Name)); return tcs.Task; }
        void Run() { try { tcs.TrySetResult(action()); } catch (Exception e) { tcs.TrySetException(e); } }
        if (control.InvokeRequired) control.BeginInvoke((Action)Run); else Run();
        return tcs.Task;
    }
}
