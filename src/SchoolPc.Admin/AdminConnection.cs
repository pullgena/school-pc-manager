using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using SchoolPc.Core;
using SchoolPc.Windows;
using Channel = SchoolPc.Core.Channel;

namespace SchoolPc.Admin;

public sealed class AdminConnection : IDisposable
{
    public Pairing Pairing { get; }
    public AdminConnection(Pairing pairing)
    {
        Pairing = pairing;
        var preferences = Storage.Load<Dictionary<string, string>>("admin.receive-folders");
        if (preferences != null && preferences.TryGetValue(pairing.Fingerprint, out string? folder)) ReceiveFolder = folder;
    }
    private TcpClient? client;
    private Channel? channel;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim upload = new(1);
    private Task? receiveTask, heartbeatTask;
    private long lastPong;
    private volatile bool established;
    public bool FrameAcknowledgement { get; private set; }
    public string? ReceiveFolder { get; set; }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<string>> folderRequests = new();
    public event Action<string>? Progress;
    public event Action<Packet>? Received;
    public event Action<string>? Closed;
    public bool Connected => established && !stop.IsCancellationRequested;
    public async Task ConnectAsync()
    {
        Pairing.Validate(); client = new TcpClient { NoDelay = true };
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
            Progress?.Invoke($"대상 주소 접속 중 · {Pairing.Host}:{Pairing.Port}");
            await client.ConnectAsync(Pairing.Host, Pairing.Port, deadline.Token);
            Progress?.Invoke("주소 접속 완료 · 암호화 연결 확인 중");
            var tls = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert != null && Security.EqualHash(Security.Hash(cert.GetRawCertData()), Pairing.Fingerprint));
            channel = new Channel(tls);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "SchoolPcAgent", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, deadline.Token);
            await channel.SendAsync(new("hello", Text: Pairing.Token, Value: 1), deadline.Token);
            Progress?.Invoke("연결 요청 전송 완료 · 대상의 인증·승인 응답 대기 중");
            using var approval = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); approval.CancelAfter(TimeSpan.FromSeconds(40));
            var response = await channel.ReadAsync(approval.Token);
            if (response.Type != "accepted") throw new InvalidOperationException(response.Text.Length == 0 ? "관리 대상에서 접속을 거절했습니다." : response.Text);
            stop.Token.ThrowIfCancellationRequested();
            FrameAcknowledgement = response.Value >= 1; established = true; Interlocked.Exchange(ref lastPong, Environment.TickCount64);
            receiveTask = ReadLoop(); heartbeatTask = Heartbeat(); Storage.Audit("admin", "연결: " + Pairing.Name);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        { Dispose(); throw new IOException("연결 시간이 초과되었습니다. 대상의 연결 대기 상태, 등록 주소·키, Windows 방화벽 허용 및 활동 내역을 확인해 주세요."); }
        catch { Dispose(); throw; }
    }
    public async Task SendAsync(Packet packet)
    {
        if (!Connected) throw new InvalidOperationException("PC가 연결되어 있지 않습니다.");
        try { await channel!.SendAsync(packet, stop.Token); }
        catch { Dispose(); throw; }
    }
    public async Task SendFileAsync(string path)
    {
        if (!await upload.WaitAsync(0)) throw new InvalidOperationException("파일을 이미 보내는 중입니다.");
        try
        {
            if (!Connected) throw new InvalidOperationException("PC가 연결되어 있지 않습니다.");
            await FileSender.SendAsync(channel!, path, stop.Token); Storage.Audit("admin", "파일 보내기 완료");
        }
        catch { Dispose(); throw; }
        finally { upload.Release(); }
    }
    public async Task<string> SelectTargetFolderAsync(string proposed)
    {
        string id = Guid.NewGuid().ToString("N");
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        folderRequests[id] = pending;
        try
        {
            await SendAsync(new("select-folder", Id: id, Text: proposed));
            return await pending.Task.WaitAsync(TimeSpan.FromSeconds(90), stop.Token);
        }
        finally { folderRequests.TryRemove(id, out _); }
    }
    private async Task ReadLoop()
    {
        string inbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SchoolPc 받은 파일", "PC-" + SafeFolder(Pairing.Name));
        using var receiver = new FileReceiver(inbox);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var packet = await channel!.ReadAsync(stop.Token);
                switch (packet.Type)
                {
                    case "pong": Interlocked.Exchange(ref lastPong, Environment.TickCount64); break;
                    case "folder-selected":
                        if (folderRequests.TryGetValue(packet.Id, out var pending))
                        {
                            if (packet.Enabled) pending.TrySetResult(packet.Text);
                            else pending.TrySetException(new IOException("대상에서 저장 폴더 선택을 취소했습니다."));
                        }
                        break;
                    case "file-begin": receiver.SetFolder(ReceiveFolder ?? inbox); receiver.Begin(packet); break;
                    case "file-chunk": receiver.Chunk(packet); break;
                    case "file-end":
                        string saved = receiver.End(packet); Storage.Audit("admin", "파일 받기 완료");
                        Received?.Invoke(new("notice", Text: "파일 저장 완료: " + saved)); break;
                    default: Received?.Invoke(packet); break;
                }
            }
        }
        catch (Exception e) { if (!stop.IsCancellationRequested) Closed?.Invoke(e is OperationCanceledException ? "연결 시간 초과" : e.Message); }
        finally { Dispose(); Closed?.Invoke("연결 종료"); }
    }
    private async Task Heartbeat()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (Environment.TickCount64 - Interlocked.Read(ref lastPong) > 15000) throw new IOException("PC의 응답이 없습니다.");
                await channel!.SendAsync(new("ping"), stop.Token); await Task.Delay(2000, stop.Token);
            }
        }
        catch (Exception e) { if (!stop.IsCancellationRequested) Closed?.Invoke(e.Message); Dispose(); }
    }
    private static string SafeFolder(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.') is { Length: > 0 } safe ? safe : "PC";
    public void Dispose()
    {
        if (stop.IsCancellationRequested) return;
        foreach (var request in folderRequests.Values) request.TrySetCanceled();
        established = false; stop.Cancel(); channel?.Dispose(); client?.Dispose();
        // CTS and semaphores remain valid until asynchronous readers/uploads have observed cancellation.
    }
}
