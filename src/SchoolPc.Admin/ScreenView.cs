using System.ComponentModel;

namespace SchoolPc.Admin;

public sealed class ScreenView : Control
{
    private Image? image;
    private readonly RemoteWindowsKeys windowsKeys;
    private readonly HashSet<Keys> textKeys = [];
    private readonly HashSet<int> pressedButtons = [];
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool RemoteInput { get; set; }
    public event Action<int, int>? MoveRemote;
    public event Action<int, bool>? ButtonRemote;
    public event Action<int>? WheelRemote;
    public event Action<int, bool>? KeyRemote;
    public event Action? ReleaseRemote;
    public event Action<string>? TextRemote;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool TextInput { get; set; } = true;
    public ScreenView()
    {
        windowsKeys = new(() => RemoteInput && Focused && FindForm()?.ContainsFocus == true, (key, down) => KeyRemote?.Invoke(key, down));
        ImeMode = ImeMode.Disable;
        Dock = DockStyle.Fill; BackColor = Color.FromArgb(20, 26, 39); TabStop = true;
        SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    public void SetFrame(Image value) { var previous = image; image = value; previous?.Dispose(); Invalidate(); }
    private Rectangle ImageArea()
    {
        if (image == null) return Rectangle.Empty;
        double ratio = Math.Min(ClientSize.Width / (double)image.Width, ClientSize.Height / (double)image.Height);
        int w = (int)(image.Width * ratio), h = (int)(image.Height * ratio);
        return new((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (image == null) { TextRenderer.DrawText(e.Graphics, "연결 후 ‘화면 보기’를 켜 주세요", Font, ClientRectangle, Color.LightGray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
        e.Graphics.DrawImage(image, ImageArea());
        if (Focused && RemoteInput) ControlPaint.DrawBorder(e.Graphics, ClientRectangle, Color.CornflowerBlue, ButtonBorderStyle.Solid);
    }
    private bool Coordinate(Point p, out int x, out int y)
    {
        var area = ImageArea(); x = y = 0;
        if (area.Width < 1 || area.Height < 1 || (!Capture && !area.Contains(p))) return false;
        p = new Point(Math.Clamp(p.X, area.Left, area.Right - 1), Math.Clamp(p.Y, area.Top, area.Bottom - 1));
        x = (int)Math.Round((p.X - area.Left) * 65535d / Math.Max(1, area.Width - 1));
        y = (int)Math.Round((p.Y - area.Top) * 65535d / Math.Max(1, area.Height - 1)); return true;
    }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (RemoteInput && Focused && Coordinate(e.Location, out int x, out int y)) MoveRemote?.Invoke(x, y); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); Focus();
        if (RemoteInput && Coordinate(e.Location, out int x, out int y)) { pressedButtons.Add(Button(e.Button)); MoveRemote?.Invoke(x, y); ButtonRemote?.Invoke(Button(e.Button), true); Capture = true; }
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        pressedButtons.Remove(Button(e.Button));
        if (RemoteInput) { if (Coordinate(e.Location, out int x, out int y)) MoveRemote?.Invoke(x, y); ButtonRemote?.Invoke(Button(e.Button), false); }
        if (pressedButtons.Count == 0) Capture = false;
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && pressedButtons.Count > 0) { pressedButtons.Clear(); ReleaseRemote?.Invoke(); }
    }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); if (RemoteInput) WheelRemote?.Invoke(e.Delta); }
    private static int Button(MouseButtons button) => button switch { MouseButtons.Left => 1, MouseButtons.Right => 2, MouseButtons.Middle => 3, _ => 1 };
    protected override bool IsInputKey(Keys keyData) => RemoteInput || base.IsInputKey(keyData);
    protected override bool ProcessDialogKey(Keys keyData) => RemoteInput || base.ProcessDialogKey(keyData);
    private static bool Printable(Keys key) => key is >= Keys.D0 and <= Keys.Z or >= Keys.NumPad0 and <= Keys.Divide or >= Keys.Oem1 and <= Keys.OemBackslash or Keys.Space;
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (RemoteInput)
        {
            if (TextInput && !e.Control && !e.Alt && Printable(e.KeyCode))
            {
                textKeys.Add(e.KeyCode); e.Handled = true;
            }
            else { KeyRemote?.Invoke((int)e.KeyCode, true); e.Handled = true; e.SuppressKeyPress = true; }
        }
        base.OnKeyDown(e);
    }
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (RemoteInput && TextInput) { if (!char.IsControl(e.KeyChar)) TextRemote?.Invoke(e.KeyChar.ToString()); e.Handled = true; }
        base.OnKeyPress(e);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        bool textKey = textKeys.Remove(e.KeyCode);
        if (RemoteInput) { if (!textKey) KeyRemote?.Invoke((int)e.KeyCode, false); e.Handled = true; }
        base.OnKeyUp(e);
    }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); textKeys.Clear(); pressedButtons.Clear(); Capture = false; ReleaseRemote?.Invoke(); Invalidate(); }
    protected override void Dispose(bool disposing) { if (disposing) { windowsKeys.Dispose(); image?.Dispose(); } base.Dispose(disposing); }
}
