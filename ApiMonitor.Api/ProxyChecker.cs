using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace ApiMonitor.Api;

/// <summary>通过指定 HTTP/SOCKS5 代理访问目标站点，验证代理链路是否可用。</summary>
public sealed class ProxyChecker
{
    public const string DefaultTarget = "https://www.google.com/generate_204";

    public static bool IsValidProxy(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 300 &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme is "http" or "https" or "socks5" or "socks5h" or "socks4" or "socks4a") &&
        !string.IsNullOrEmpty(uri.Host) && uri.Port is > 0 and <= 65535;

    public static bool IsValidTarget(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 500 &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);

public async Task<CheckResult> CheckAsync(string proxyUrl, string? targetUrl, string? username, string? password, string? transportProtocol, CancellationToken ct)
    {
        if (!IsValidProxy(proxyUrl)) return new(false, "代理地址格式不正确，示例：http://127.0.0.1:7890 或 socks5://127.0.0.1:1080", 0);
        var target = string.IsNullOrWhiteSpace(targetUrl) ? DefaultTarget : targetUrl.Trim();
        if (!IsValidTarget(target)) return new(false, "目标地址必须是有效的 HTTP(S) URL", 0);

        var sw = Stopwatch.StartNew();
        var isUdp = string.Equals(transportProtocol, "udp", StringComparison.OrdinalIgnoreCase);
        if (isUdp) return await Socks5UdpChecker.CheckAsync(proxyUrl, username, password, ct);
        try
        {
            using var handler = BuildHandler(proxyUrl, username, password);
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.Add("User-Agent", "ApiMonitor/1.0");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            var code = (int)response.StatusCode;
            var ok = response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent;
            return new(ok, ok ? $"代理 TCP 链路可用，目标返回 HTTP {code}" : $"代理 TCP 链路已连通但目标返回 HTTP {code}", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "通过代理访问超时（15 秒）", sw.ElapsedMilliseconds); }
        catch (HttpRequestException ex) { return new(false, "代理连接失败：" + DetailedError(ex), sw.ElapsedMilliseconds); }
        catch (Exception ex) { return new(false, "代理检测异常：" + DetailedError(ex), sw.ElapsedMilliseconds); }
    }

    private static SocketsHttpHandler BuildHandler(string proxyUrl, string? username, string? password)
    {
        var uri = new Uri(proxyUrl);
        var user = !string.IsNullOrWhiteSpace(username) ? username : null;
        var pass = password ?? "";
        if (user is null && !string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            user = Uri.UnescapeDataString(parts[0]);
            pass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }

        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
        if (uri.Scheme is "socks5" or "socks5h")
            handler.ConnectCallback = (context, ct) => ConnectThroughSocks5Async(uri, user, pass, context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);
        else if (uri.Scheme is "http" or "https")
            handler.ConnectCallback = (context, ct) => ConnectThroughHttpProxyAsync(uri, user, pass, context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);
        else
            throw new InvalidOperationException("当前仅支持 HTTP 和 SOCKS5 代理检测");
        return handler;
    }

    private static async ValueTask<Stream> ConnectThroughHttpProxyAsync(Uri proxy, string? username, string password, string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(proxy.Host, proxy.Port, ct);
            var stream = tcp.GetStream();
            var request = $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\nProxy-Connection: Keep-Alive\r\n";
            if (!string.IsNullOrEmpty(username))
            {
                var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}:{password}"));
                request += $"Proxy-Authorization: Basic {token}\r\n";
            }
            request += "\r\n";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(request), ct);
            var header = await ReadHttpHeadersAsync(stream, ct);
            var firstLine = header.Split("\r\n", 2)[0];
            var parts = firstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var status)) throw new IOException($"HTTP 代理返回无效响应：{firstLine}");
            if (status != 200) throw new IOException($"HTTP 代理 CONNECT 失败：{firstLine}");
            return new TcpClientStream(stream, tcp);
        }
        catch { tcp.Dispose(); throw; }
    }

    private static async Task<string> ReadHttpHeadersAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var buffer = new byte[512];
        while (bytes.Count < 16384)
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0) throw new IOException("HTTP 代理响应提前结束");
            bytes.AddRange(buffer.AsSpan(0, count).ToArray());
            var text = System.Text.Encoding.ASCII.GetString(bytes.ToArray());
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) return text;
        }
        throw new IOException("HTTP 代理响应头过大");
    }

    private static async ValueTask<Stream> ConnectThroughSocks5Async(Uri proxy, string? username, string password, string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(proxy.Host, proxy.Port, ct);
            var stream = tcp.GetStream();
            var withAuth = !string.IsNullOrEmpty(username);
            await stream.WriteAsync(withAuth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, ct);
            var selected = await ReadExactAsync(stream, 2, ct);
            if (selected[1] == 255) throw new IOException("SOCKS5 代理不接受认证方式");
            if (selected[1] == 2)
            {
                if (username is null || username.Length > 255 || password.Length > 255) throw new IOException("SOCKS5 用户名或密码无效");
                var ub = System.Text.Encoding.UTF8.GetBytes(username); var pb = System.Text.Encoding.UTF8.GetBytes(password);
                await stream.WriteAsync(new[] { (byte)1, (byte)ub.Length }.Concat(ub).Concat(new[] { (byte)pb.Length }).Concat(pb).ToArray(), ct);
                if ((await ReadExactAsync(stream, 2, ct))[1] != 0) throw new IOException("SOCKS5 用户名或密码错误");
            }
            var hb = System.Text.Encoding.UTF8.GetBytes(host);
            if (hb.Length > 255) throw new IOException("目标主机名过长");
            await stream.WriteAsync(new[] { (byte)5, (byte)1, (byte)0, (byte)3, (byte)hb.Length }.Concat(hb).Concat(new[] { (byte)(port >> 8), (byte)port }).ToArray(), ct);
            var reply = await ReadExactAsync(stream, 4, ct);
            if (reply[1] != 0) throw new IOException($"SOCKS5 CONNECT 失败（代码 {reply[1]}）");
            var addressLength = reply[3] switch { 1 => 4, 4 => 16, 3 => (await ReadExactAsync(stream, 1, ct))[0], _ => throw new IOException("SOCKS5 返回未知地址类型") };
            await ReadExactAsync(stream, addressLength + 2, ct);
            return new TcpClientStream(stream, tcp);
        }
        catch { tcp.Dispose(); throw; }
    }

    private sealed class TcpClientStream(Stream inner, TcpClient client) : Stream
    {
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); client.Dispose(); } base.Dispose(disposing); }
        public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => inner.CanWrite; public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush(); public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct); public override int Read(byte[] b, int o, int c) => inner.Read(b, o, c); public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) => inner.ReadAsync(b, o, c, ct); public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) => inner.ReadAsync(b, ct); public override void Write(byte[] b, int o, int c) => inner.Write(b, o, c); public override Task WriteAsync(byte[] b, int o, int c, CancellationToken ct) => inner.WriteAsync(b, o, c, ct); public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) => inner.WriteAsync(b, ct); public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
    }

    private static string DetailedError(Exception ex) => string.Join(" | ", new[] { ex.Message, ex.InnerException?.Message }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();

    private static async Task<CheckResult> CheckSocks5UdpAsync(string proxyUrl, string? username, string? password, Stopwatch sw, CancellationToken ct)
    {
        if (!proxyUrl.StartsWith("socks5", StringComparison.OrdinalIgnoreCase)) return new(false, "UDP 检测仅支持 SOCKS5 代理", 0);
        try
        {
            var uri = new Uri(proxyUrl); using var tcp = new TcpClient(); await tcp.ConnectAsync(uri.Host, uri.Port, ct);
            using var stream = tcp.GetStream(); var auth = string.IsNullOrWhiteSpace(username) ? new byte[] { 5, 1, 0 } : new byte[] { 5, 1, 2 };
            await stream.WriteAsync(auth, ct); var method = await ReadExactAsync(stream, 2, ct); if (method[0] != 5 || method[1] == 255) return new(false, "SOCKS5 不接受认证方式", sw.ElapsedMilliseconds);
            if (method[1] == 2) { var u = System.Text.Encoding.UTF8.GetBytes(username ?? ""); var p = System.Text.Encoding.UTF8.GetBytes(password ?? ""); var credentials = new byte[3 + u.Length + p.Length]; credentials[0] = 1; credentials[1] = (byte)u.Length; u.CopyTo(credentials, 2); credentials[2 + u.Length] = (byte)p.Length; p.CopyTo(credentials, 3 + u.Length); await stream.WriteAsync(credentials, ct); var authResult = await ReadExactAsync(stream, 2, ct); if (authResult[1] != 0) return new(false, "SOCKS5 用户名或密码错误", sw.ElapsedMilliseconds); }
            await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, ct); var reply = await ReadExactAsync(stream, 4, ct); if (reply[1] != 0) return new(false, $"SOCKS5 UDP ASSOCIATE 失败（代码 {reply[1]}）", sw.ElapsedMilliseconds); var address = reply[3] == 1 ? await ReadExactAsync(stream, 4, ct) : reply[3] == 3 ? await ReadExactAsync(stream, (await ReadExactAsync(stream, 1, ct))[0], ct) : await ReadExactAsync(stream, 16, ct); var port = await ReadExactAsync(stream, 2, ct); _ = address; _ = port; return new(true, "SOCKS5 UDP ASSOCIATE 成功，代理 UDP 通道可用", sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { return new(false, "SOCKS5 UDP 通道失败：" + Trim(ex.Message), sw.ElapsedMilliseconds); }
    }
    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken ct) { var buffer = new byte[length]; var offset = 0; while (offset < length) { var count = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), ct); if (count == 0) throw new IOException("代理提前关闭连接"); offset += count; } return buffer; }
    private static string Trim(string message) => message[..Math.Min(message.Length, 160)];

public static class Socks5UdpChecker
{
    public static async Task<CheckResult> CheckAsync(string proxyUrl, string? username, string? password, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (!proxyUrl.StartsWith("socks5", StringComparison.OrdinalIgnoreCase)) return new(false, "UDP 检测仅支持 SOCKS5 代理", 0);
        try
        {
            var uri = new Uri(proxyUrl);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(uri.Host, uri.Port, ct);
            await using var stream = tcp.GetStream();
            var user = username ?? ""; var pass = password ?? "";
            var methods = string.IsNullOrEmpty(user) ? new byte[] { 5, 1, 0 } : new byte[] { 5, 1, 2 };
            await stream.WriteAsync(methods, ct);
            var selected = await ReadExactAsync(stream, 2, ct);
            if (selected[0] != 5 || selected[1] == 255) return new(false, "SOCKS5 不接受 UDP 检测所需的认证方式", sw.ElapsedMilliseconds);
            if (selected[1] == 2)
            {
                var ub = System.Text.Encoding.UTF8.GetBytes(user); var pb = System.Text.Encoding.UTF8.GetBytes(pass);
                if (ub.Length > 255 || pb.Length > 255) return new(false, "SOCKS5 用户名或密码过长", sw.ElapsedMilliseconds);
                await stream.WriteAsync(new[] { (byte)1, (byte)ub.Length }.Concat(ub).Concat(new[] { (byte)pb.Length }).Concat(pb).ToArray(), ct);
                if ((await ReadExactAsync(stream, 2, ct))[1] != 0) return new(false, "SOCKS5 用户名或密码错误", sw.ElapsedMilliseconds);
            }
            await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, ct);
            var header = await ReadExactAsync(stream, 4, ct);
            if (header[1] != 0) return new(false, $"SOCKS5 UDP ASSOCIATE 失败（代码 {header[1]}）", sw.ElapsedMilliseconds);
            var relayHost = header[3] switch { 1 => new IPAddress(await ReadExactAsync(stream, 4, ct)).ToString(), 3 => System.Text.Encoding.ASCII.GetString(await ReadExactAsync(stream, (await ReadExactAsync(stream, 1, ct))[0], ct)), 4 => new IPAddress(await ReadExactAsync(stream, 16, ct)).ToString(), _ => throw new IOException("SOCKS5 返回未知地址类型") };
            var relayPortBytes = await ReadExactAsync(stream, 2, ct); var relayPort = (relayPortBytes[0] << 8) | relayPortBytes[1];
            if (relayHost == "0.0.0.0" || relayHost == "::") relayHost = uri.Host;
            using var udp = new UdpClient(); udp.Connect(relayHost, relayPort);
            var query = BuildDnsQuery();
            var packet = new byte[10 + query.Length];
            // SOCKS5 UDP 请求头：RSV(2)、FRAG(1)、ATYP(1)、DST.ADDR、DST.PORT、DATA。
            packet[0] = 0; packet[1] = 0; packet[2] = 0; packet[3] = 1;
            packet[4] = 8; packet[5] = 8; packet[6] = 8; packet[7] = 8; packet[8] = 0; packet[9] = 53;
            query.CopyTo(packet, 10);
            await udp.SendAsync(packet, packet.Length);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await udp.ReceiveAsync(timeout.Token);
            var payload = UnwrapUdpReply(response.Buffer);
            var valid = payload.Length >= 12 && payload[0] == query[0] && payload[1] == query[1] && (payload[2] & 0x80) != 0;
            return new(valid, valid ? "SOCKS5 UDP 通道可用，DNS UDP 探测收到响应" : "SOCKS5 UDP 通道已建立但响应无效", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "SOCKS5 UDP 探测超时", sw.ElapsedMilliseconds); }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException) { return new(false, "SOCKS5 UDP 通道失败：" + ex.Message, sw.ElapsedMilliseconds); }
    }
    private static byte[] UnwrapUdpReply(byte[] packet)
    {
        if (packet.Length < 10 || packet[0] != 0 || packet[1] != 0 || packet[2] != 0) return [];
        var offset = packet[3] switch { 1 => 4 + 4 + 2, 3 => packet.Length > 4 ? 5 + packet[4] + 2 : 0, 4 => 4 + 16 + 2, _ => 0 };
        return offset > 0 && offset <= packet.Length ? packet[offset..] : [];
    }

    private static byte[] BuildDnsQuery()
    {
        var id = (ushort)Random.Shared.Next(1, 65535); var name = new byte[] { 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0 };
        return new[] { (byte)(id >> 8), (byte)id, (byte)1, (byte)0, (byte)0, (byte)1, (byte)0, (byte)0, (byte)0, (byte)0 }.Concat(name).Concat(new byte[] { 0, 1, 0, 1 }).ToArray();
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken ct)
    {
        var buffer = new byte[length]; var offset = 0;
        while (offset < length) { var n = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), ct); if (n == 0) throw new IOException("代理提前关闭连接"); offset += n; }
        return buffer;
    }
    }
}