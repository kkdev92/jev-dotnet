using System.Net;
using System.Net.Sockets;
using System.Text;
using Kkdev92.Jev.DependencyInjection;
using Kkdev92.Jev.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace Kkdev92.Jev.Tests;

public sealed class HandlerIsolationTests
{
    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task PooledHandlerKeepsCookiesOutOfTheNextClientAndReturnsRedirects(int status)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        var token = limit.Token;
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var url = new Uri($"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/");
        var services = new ServiceCollection();
        services.AddJev(o =>
        {
            o.BaseAddress = url;
            o.AllowLoopbackHttp = true;
            o.AuthenticationMode = JevAuthenticationMode.None;
        });
        using var container = services.BuildServiceProvider();
        var factory = container.GetRequiredService<IHttpMessageHandlerFactory>();
        HttpMessageHandler? pooled = factory.CreateHandler(JevServiceCollectionExtensions.HttpClientName);
        while (pooled is DelegatingHandler layer)
        {
            pooled = layer.InnerHandler;
        }

        Assert.False(Assert.IsType<SocketsHttpHandler>(pooled).UseProxy);
        var first = container.GetRequiredService<JevClient>().GetModelsAsync(cancellationToken: token);
        await ReplyAsync(server, "200 OK", "Set-Cookie: session=first; Path=/", FakeResponses.Models(), token);
        await first;

        var second = container.GetRequiredService<JevClient>().GetModelsAsync(cancellationToken: token);
        var headers = await ReplyAsync(server, $"{status} Redirect", $"Location: {url}next", "", token);
        var error = await Assert.ThrowsAsync<JevHttpException>(() => second);
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("Cookie:", headers, StringComparison.OrdinalIgnoreCase);
        Assert.False(server.Pending());
    }

    private static async Task<string> ReplyAsync(TcpListener listener, string status, string header, string body, CancellationToken token)
    {
        using var peer = await listener.AcceptTcpClientAsync(token);
        using var stream = peer.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var headers = new StringBuilder();
        while (await reader.ReadLineAsync(token) is { Length: > 0 } line)
        {
            headers.AppendLine(line);
        }

        var reply = $"HTTP/1.1 {status}\r\n{header}\r\nConnection: close\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(reply), token);
        return headers.ToString();
    }
}
