namespace SchoolPc.Agent;

internal sealed class SharedScreenForm : Form
{
    private readonly PictureBox picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    public SharedScreenForm(bool exclusive, Cursor? hiddenCursor = null)
    {
        Text = "관리자 화면 공유";
        Size = new Size(1000, 700); BackColor = Color.Black;
        if (exclusive) { StartPosition = FormStartPosition.Manual; FormBorderStyle = FormBorderStyle.None; Bounds = Screen.PrimaryScreen!.Bounds; TopMost = true; Cursor = picture.Cursor = hiddenCursor ?? Cursors.Default; }
        Controls.Add(picture);
    }
    public void SetFrame(Bitmap frame) { var previous = picture.Image; picture.Image = frame; previous?.Dispose(); }
    protected override void Dispose(bool disposing) { if (disposing) picture.Image?.Dispose(); base.Dispose(disposing); }
}
