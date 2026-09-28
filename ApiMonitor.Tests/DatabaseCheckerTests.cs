using System.Net;
using ApiMonitor.Api;

namespace ApiMonitor.Tests;

public sealed class DatabaseCheckerTests
{
    [Fact]
    public async Task Milvus_anonymous_access_is_success_but_reports_authentication_was_not_checked()
    {
        var handler = new MilvusHandler(_ => (HttpStatusCode.OK, "{\"code\":0,\"data\":[]}"));
        var checker = new DatabaseChecker(new TestFactory(handler));

        var result = await checker.CheckAsync(DbKind.Milvus, "localhost", 19530, null, "invalid", "invalid", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("未启用鉴权", result.Message);
        Assert.Single(handler.Requests);
        Assert.Null(handler.Requests[0]);
    }

    [Fact]
    public async Task Milvus_requires_successful_authenticated_request_after_anonymous_denial()
    {
        var handler = new MilvusHandler(auth => auth is null
            ? (HttpStatusCode.OK, "{\"code\":1800,\"message\":\"unauthorized\"}")
            : auth == "root:correct"
                ? (HttpStatusCode.OK, "{\"code\":0,\"data\":[]}")
                : (HttpStatusCode.OK, "{\"code\":1800,\"message\":\"unauthorized\"}"));
        var checker = new DatabaseChecker(new TestFactory(handler));

        var invalid = await checker.CheckAsync(DbKind.Milvus, "localhost", 19530, null, "root", "wrong", CancellationToken.None);
        var valid = await checker.CheckAsync(DbKind.Milvus, "localhost", 19530, null, "root", "correct", CancellationToken.None);

        Assert.False(invalid.Success);
        Assert.True(valid.Success);
        Assert.Equal(new string?[] { null, "root:wrong", null, "root:correct" }, handler.Requests);
    }

    private sealed class MilvusHandler(Func<string?, (HttpStatusCode Status, string Body)> response) : HttpMessageHandler
    {
        public List<string?> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v2/vectordb/collections/list", request.RequestUri!.AbsolutePath);
            var auth = request.Headers.Authorization?.Parameter;
            Requests.Add(auth);
            var (status, body) = response(auth);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class TestFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
