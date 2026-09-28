using System.Net;
using System.Net.Sockets;
using System.Text;
using ApiMonitor.Api;

namespace ApiMonitor.Tests;

public sealed class ProxyCheckerTests
{
    [Fact]
    public async Task Http_proxy_connect_tunnel_returns_target_response()
    {
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        var targetTask = Task.Run(async () =>
        {
            using var client = await target.AcceptTcpClientAsync();
            await ReadHeaders(client.GetStream());
            var response = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n");
            await client.GetStream().WriteAsync(response);
        });

        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
        var proxyTask = Task.Run(async () =>
        {
            using var incoming = await proxy.AcceptTcpClientAsync();
            var input = incoming.GetStream();
            var header = await ReadHeaders(input);
            var connectLine = header.Split("\r\n", 2)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("CONNECT", connectLine[0]);
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(IPAddress.Loopback, targetPort);
            await input.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"));
            var toTarget = input.CopyToAsync(upstream.GetStream());
            var toClient = upstream.GetStream().CopyToAsync(input);
            await Task.WhenAny(toTarget, toClient);
        });

        var checker = new ProxyChecker();
        var result = await checker.CheckAsync($"http://127.0.0.1:{proxyPort}", $"http://127.0.0.1:{targetPort}/health", null, null, "tcp", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Contains("204", result.Message);
        await Task.WhenAll(targetTask, proxyTask);
    }

    private static async Task<string> ReadHeaders(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var buffer = new byte[256];
        while (true)
        {
            var count = await stream.ReadAsync(buffer);
            Assert.NotEqual(0, count);
            bytes.AddRange(buffer.AsSpan(0, count).ToArray());
            var text = Encoding.ASCII.GetString(bytes.ToArray());
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) return text;
        }
    }
}
