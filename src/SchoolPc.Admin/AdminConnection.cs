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
    public AdminConnection(Pairing pairing) { Pairing = pairing; }
    private TcpClient? client;
    private Channel? channel;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim upload = new(1);
    private Task? receiveTask, heartbeatTask;
    private long lastPong;
    public event Action<Packet>? Received;
    public event Action<string>? Closed;
    public bool Connected => channel != null && !stop.IsCancellationRequested;
    public async Task ConnectAsync()
    {
        Pairing.Validate(); client = new TcpClient { NoDelay = true };
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(Pairing.Host, Pairing.Port, deadline.Token);
            var tls = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert != null && Security.EqualHash(Security.Hash(cert.GetRawCertData()), Pairing.Fingerprint));
            channel = new Channel(tls);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "SchoolPcAgent", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, deadline.Token);
            await channel.SendAsync(new("hello", Text: Pairing.Token), deadline.Token);
            using var approval = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); approval.CancelAfter(TimeSpan.FromSeconds(40));
            var response = await channel.ReadAsync(approval.Token);
            if (response.Type != "accepted") throw new InvalidOperationException(response.Text.Length == 0 ? "관리 대상에서 접속을 거절했습니다." : response.Text);
            Interlocked.Exchange(ref lastPong, Environment.TickCount64);
            receiveTask = ReadLoop(); heartbeatTask = Heartbeat(); Storage.Audit("admin", "연결: " + Pairing.Name);
        }
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
                    case "file-begin": receiver.Begin(packet); break;
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
        stop.Cancel(); channel?.Dispose(); client?.Dispose();
        // CTS and semaphores remain valid until asynchronous readers/uploads have observed cancellation.
    }
}
