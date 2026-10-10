using System.Text.Json;
using SchoolPc.Core;
using SchoolPc.Windows;

namespace SchoolPc.Admin;

public sealed class AdminForm : Form
{
    private readonly List<Pairing> profiles;
    private readonly ListBox list = new() { Dock = DockStyle.Fill, DisplayMember = nameof(Pairing.Name), IntegralHeight = false };
    private readonly TextBox address = new() { Width = 170 };
    private readonly NumericUpDown port = new() { Minimum = 1024, Maximum = 65535, Width = 85 };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, SessionPanel> sessions = [];
    private readonly CheckBox quickConnect = new() { Text = "목록 클릭으로 바로 연결", AutoSize = true, Checked = false, Margin = new Padding(8) };
    private bool refreshing;
    private readonly Label summary = Ui.Label("등록 파일을 가져온 뒤 PC를 선택해 연결하세요.");
    public AdminForm()
    {
        Text = "School PC · 관리자"; Size = new Size(1400, 900); Ui.Style(this); MinimumSize = new Size(1000, 650);
        profiles = Storage.Load<List<Pairing>>("admin.profiles") ?? [];
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 3 };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 250)); layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var header = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        header.Controls.AddRange([Ui.Label("School PC 관리자", 22), Ui.Button("한 번에 모두 연결", async (_, _) => await ConnectAll(), true), Ui.Button("전체 입력 잠금 해제", async (_, _) => await ForAll(p => p.Unlock()), true),
            Ui.Button("전체 화면 보기", async (_, _) => await ForAll(p => p.Run(new("screen", Enabled: true)))),
            Ui.Button("모든 연결 종료", (_, _) => DisconnectAll()), Ui.Button("내 Windows Wi-Fi 관리", (_, _) => new WifiForm().Show(this)), Ui.Button("현재 프로그램 제거", async (_, _) => await Removal.RequestAsync(this, () => { DisconnectAll(); return Task.CompletedTask; })), Ui.Button("업데이트 확인", async (_, _) => await Updater.CheckAsync(this, () => { DisconnectAll(); return Task.CompletedTask; }))]);
        layout.Controls.Add(header, 0, 0); layout.SetColumnSpan(header, 2);
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(4) };
        sidebar.RowStyles.Add(new(SizeType.AutoSize)); sidebar.RowStyles.Add(new(SizeType.Percent, 100)); sidebar.RowStyles.Add(new(SizeType.AutoSize));
        var import = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        import.Controls.Add(quickConnect);
        import.Controls.AddRange([Ui.Button("등록 파일 가져오기", (_, _) => Import(), true), Ui.Button("PC 등록 삭제", (_, _) => Remove())]);
        sidebar.Controls.Add(import, 0, 0); sidebar.Controls.Add(list, 0, 1);
        var editor = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };
        editor.Controls.AddRange([Ui.Label("대상 PC 주소"), address, Ui.Label("포트"), port,
            Ui.Button("주소 저장", (_, _) => SaveAddress()), Ui.Button("선택한 PC 연결", async (_, _) => await ConnectSelected(), true),
            Ui.Button("모든 PC 연결", async (_, _) => await ConnectAll()), Ui.Button("선택한 탭 닫기", (_, _) => CloseTab()), Ui.Button("받은 파일 폴더", (_, _) => OpenInbox())]);
        sidebar.Controls.Add(editor, 0, 2); layout.Controls.Add(sidebar, 0, 1); layout.Controls.Add(tabs, 1, 1);
        layout.Controls.Add(summary, 0, 2); layout.SetColumnSpan(summary, 2); Controls.Add(layout);
        list.SelectedIndexChanged += (_, _) => { if (list.SelectedItem is Pairing p) { address.Text = p.Host; port.Value = p.Port; } };
        list.MouseClick += async (_, _) => { if (!refreshing && quickConnect.Checked && list.SelectedItem is Pairing p) await Connect(p); };
        FormClosing += (_, _) => DisconnectAll(); RefreshProfiles();
    }
    private void RefreshProfiles()
    {
        refreshing = true;
        int selected = list.SelectedIndex; list.DataSource = null; list.DataSource = profiles.ToArray(); list.DisplayMember = nameof(Pairing.Name);
        if (profiles.Count > 0) list.SelectedIndex = Math.Clamp(selected, 0, profiles.Count - 1);
        Storage.Save("admin.profiles", profiles); refreshing = false;
    }
    private void Import()
    {
        using var picker = new OpenFileDialog { Filter = "관리 대상 등록 파일 (*.json)|*.json", Multiselect = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        foreach (string file in picker.FileNames)
        {
            try
            {
                if (new FileInfo(file).Length > 16384) throw new InvalidDataException("등록 파일이 너무 큽니다.");
                var p = JsonSerializer.Deserialize<Pairing>(File.ReadAllText(file)) ?? throw new InvalidDataException("빈 등록 파일입니다."); p.Validate();
                int existing = profiles.FindIndex(x => x.Fingerprint == p.Fingerprint);
                if (existing >= 0)
                {
                    if (sessions.Remove(p.Fingerprint, out var old)) { tabs.TabPages.Remove((TabPage)old.Parent!); old.Dispose(); }
                    profiles[existing] = p;
                }
                else profiles.Add(p);
            }
            catch (Exception e) { Ui.Error(this, e); }
        }
        RefreshProfiles();
    }
    private void SaveAddress()
    {
        if (list.SelectedItem is not Pairing p) return;
        try
        {
            var updated = p with { Host = address.Text.Trim(), Port = (int)port.Value }; updated.Validate();
            profiles[profiles.IndexOf(p)] = updated; RefreshProfiles(); summary.Text = "주소를 저장했습니다. 이미 연결된 탭은 닫고 다시 연결해 주세요.";
        }
        catch (Exception e) { Ui.Error(this, e); }
    }
    private void Remove()
    {
        if (list.SelectedItem is not Pairing p) return;
        if (MessageBox.Show(this, $"‘{p.Name}’의 PC 등록과 저장된 연결 키를 지우고 연결을 종료할까요?\n대상 PC의 프로그램과 Wi-Fi 설정은 유지합니다.", "PC 등록 삭제", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        if (sessions.Remove(p.Fingerprint, out var panel)) { tabs.TabPages.Remove((TabPage)panel.Parent!); panel.Dispose(); }
        profiles.Remove(p); RefreshProfiles();
    }
    private Task ConnectSelected() => list.SelectedItem is Pairing p ? Connect(p) : Task.CompletedTask;
    private async Task Connect(Pairing p)
    {
        if (sessions.TryGetValue(p.Fingerprint, out var existing))
        {
            if (existing.Connection.Connected || existing.IsConnecting) { tabs.SelectedTab = (TabPage)existing.Parent!; return; }
            var previous = (TabPage)existing.Parent!; sessions.Remove(p.Fingerprint); tabs.TabPages.Remove(previous); previous.Dispose();
        }
        var panel = new SessionPanel(p); var tab = new TabPage(p.Name); tab.Controls.Add(panel); tabs.TabPages.Add(tab); tabs.SelectedTab = tab; sessions.Add(p.Fingerprint, panel);
        try { summary.Text = p.Name + " 연결 중 · 대상 PC에서 승인해 주세요."; await panel.ConnectAsync(); summary.Text = p.Name + " 연결 완료"; }
        catch (Exception e) { summary.Text = p.Name + ": " + e.Message; }
    }
    private async Task ConnectAll()
    {
        // One task per PC allows simultaneous consent prompts without blocking the administrator UI.
        var snapshot = profiles.ToArray(); await Task.WhenAll(snapshot.Select(Connect)); summary.Text = "모든 등록 PC의 연결 시도를 마쳤습니다.";
    }
    private async Task ForAll(Func<SessionPanel, Task> command)
    {
        var connected = sessions.Values.Where(p => p.Connection.Connected).ToArray();
        await Task.WhenAll(connected.Select(command)); summary.Text = $"연결된 {connected.Length}대에 요청했습니다.";
    }
    private void CloseTab()
    {
        if (tabs.SelectedTab is not { } tab || tab.Controls[0] is not SessionPanel panel) return;
        sessions.Remove(panel.Connection.Pairing.Fingerprint); tabs.TabPages.Remove(tab); tab.Dispose();
    }
    private void DisconnectAll() { foreach (var panel in sessions.Values) panel.Connection.Dispose(); }
    private static void OpenInbox()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SchoolPc 받은 파일");
        Directory.CreateDirectory(path); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }
}
