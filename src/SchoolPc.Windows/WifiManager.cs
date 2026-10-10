using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SchoolPc.Windows;

public sealed record WifiAdapter(Guid Id, string Name, bool Connected);
public sealed record WifiProfile(Guid AdapterId, string AdapterName, string Name, bool Policy)
{
    public override string ToString() => $"{Name} · {AdapterName}{(Policy ? " · 학교 정책" : "")}";
}
public sealed class WifiManager : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct InterfaceInfo { public Guid Id; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name; public uint State; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProfileInfo { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name; public uint Flags; }
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetProfileList(IntPtr handle, ref Guid id, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern uint WlanDeleteProfile(IntPtr handle, ref Guid id, string name, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanDisconnect(IntPtr handle, ref Guid id, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
    private IntPtr handle;
    public WifiManager() { Check(WlanOpenHandle(2, IntPtr.Zero, out _, out handle)); }
    private static void Check(uint result) { if (result != 0) throw new Win32Exception((int)result); }
    public List<WifiAdapter> Adapters()
    {
        Check(WlanEnumInterfaces(handle, IntPtr.Zero, out var list));
        try
        {
            int count = Marshal.ReadInt32(list); if (count is < 0 or > 128) throw new InvalidDataException("잘못된 어댑터 목록입니다.");
            var result = new List<WifiAdapter>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<InterfaceInfo>(IntPtr.Add(list, 8 + i * Marshal.SizeOf<InterfaceInfo>()));
                result.Add(new(item.Id, item.Name, item.State == 1));
            }
            return result;
        }
        finally { WlanFreeMemory(list); }
    }
    public List<WifiProfile> Profiles()
    {
        var result = new List<WifiProfile>();
        foreach (var adapter in Adapters())
        {
            Guid id = adapter.Id; Check(WlanGetProfileList(handle, ref id, IntPtr.Zero, out var list));
            try
            {
                int count = Marshal.ReadInt32(list); if (count is < 0 or > 4096) throw new InvalidDataException("잘못된 Wi-Fi 목록입니다.");
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<ProfileInfo>(IntPtr.Add(list, 8 + i * Marshal.SizeOf<ProfileInfo>()));
                    result.Add(new(id, adapter.Name, item.Name, (item.Flags & 1) != 0));
                }
            }
            finally { WlanFreeMemory(list); }
        }
        return result;
    }
    public void Delete(WifiProfile profile)
    {
        if (profile.Policy) throw new InvalidOperationException("학교 정책으로 등록된 Wi-Fi는 여기서 삭제하지 않습니다.");
        if (!Profiles().Any(p => p.AdapterId == profile.AdapterId && p.Name == profile.Name && !p.Policy))
            throw new InvalidOperationException("선택한 Wi-Fi 정보가 변경되었습니다. 새로고침해 주세요.");
        Guid id = profile.AdapterId; Check(WlanDeleteProfile(handle, ref id, profile.Name, IntPtr.Zero));
    }
    public void Disconnect(WifiAdapter adapter) { Guid id = adapter.Id; Check(WlanDisconnect(handle, ref id, IntPtr.Zero)); }
    public void Dispose() { if (handle != IntPtr.Zero) { WlanCloseHandle(handle, IntPtr.Zero); handle = IntPtr.Zero; } }
}

public sealed class WifiForm : Form
{
    private readonly ListBox profiles = new() { Dock = DockStyle.Fill };
    private readonly ComboBox adapters = new() { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(WifiAdapter.Name), Width = 350 };
    private readonly Label status = Ui.Label("");
    public WifiForm()
    {
        Text = "이 컴퓨터의 Windows Wi-Fi 관리"; Size = new Size(820, 560); Ui.Style(this);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 5, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(Ui.Label("Windows Wi-Fi 연결과 저장 정보", 18), 0, 0);
        var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        bar.Controls.AddRange([adapters, Ui.Button("Wi-Fi 연결 해제", (_, _) => Disconnect()), Ui.Button("새로고침", (_, _) => RefreshNetworks())]);
        layout.Controls.Add(bar, 0, 1); layout.Controls.Add(profiles, 0, 2);
        var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        bottom.Controls.AddRange([Ui.Button("선택한 Wi-Fi 저장 정보 삭제", (_, _) => Delete()), Ui.Button("Windows 인터넷 설정 열기", (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:network") { UseShellExecute = true }))]);
        layout.Controls.Add(bottom, 0, 3); layout.Controls.Add(status, 0, 4); Controls.Add(layout);
        Shown += (_, _) => RefreshNetworks();
    }
    private void RefreshNetworks()
    {
        try
        {
            using var wifi = new WifiManager(); adapters.DataSource = wifi.Adapters(); profiles.DataSource = wifi.Profiles();
            status.Text = "Wi-Fi 연결을 끊으면 원격 연결도 끊길 수 있습니다. 삭제하면 다시 연결할 때 비밀번호가 필요합니다.";
        }
        catch (Exception e) { adapters.DataSource = null; profiles.DataSource = null; status.Text = "Wi-Fi 장치 또는 WLAN 서비스가 없거나 접근이 제한되었습니다. " + e.Message; }
    }
    private void Disconnect()
    {
        if (adapters.SelectedItem is not WifiAdapter adapter) return;
        if (MessageBox.Show(this, $"이 컴퓨터의 ‘{adapter.Name}’ Wi-Fi 연결을 끊을까요?\n인터넷과 현재 원격 연결이 끊길 수 있습니다.", "Wi-Fi 연결 해제", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { using var wifi = new WifiManager(); wifi.Disconnect(adapter); RefreshNetworks(); } catch (Exception e) { Ui.Error(this, e); }
    }
    private void Delete()
    {
        if (profiles.SelectedItem is not WifiProfile profile) return;
        if (profile.Policy) { MessageBox.Show(this, "학교 정책으로 관리되는 Wi-Fi입니다."); return; }
        if (MessageBox.Show(this, $"이 컴퓨터에 저장된 ‘{profile.Name}’ Wi-Fi 정보를 삭제할까요?\n저장된 연결 정보가 지워지고 다음 연결에 비밀번호가 필요합니다.\n현재 연결을 끊으려면 별도의 ‘Wi-Fi 연결 해제’를 사용하세요.", "Wi-Fi 저장 정보 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { using var wifi = new WifiManager(); wifi.Delete(profile); RefreshNetworks(); } catch (Exception e) { Ui.Error(this, e); }
    }
}
