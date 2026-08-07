using System.Net;
using System.Text;
using FengSync.Core;
using FengSync.Core.Rclone.Diagnostics;

namespace FengSync.Tests.Rclone;

public sealed class RcloneDiagnosticsTests
{
    [Fact]
    public async Task Rc_error_is_typed_classified_and_sensitive_input_is_redacted()
    {
        using var http = new HttpClient(new ErrorHandler()) { BaseAddress = new Uri("http://rc.test/") };
        var client = new RcloneRcClient(http, http.BaseAddress, "user", "pass");

        var exception = await Assert.ThrowsAsync<RcloneException>(() =>
            client.CallAsync("operations/list", new { }));

        Assert.Equal(RcloneFailureCategory.Authentication, exception.Failure.Category);
        Assert.Equal("operations/list", exception.Failure.Operation);
        Assert.Contains("***", exception.Failure.SanitizedInput);
        Assert.DoesNotContain("top-secret", exception.Failure.SanitizedInput);
        Assert.NotEmpty(exception.Failure.CorrelationId);
    }

    [Fact]
    public void Json_log_parser_preserves_fields_and_redacts_tokens()
    {
        var entry = RcloneLogParser.Parse(
            "{\"time\":\"2026-08-07T01:02:03Z\",\"level\":\"error\",\"source\":\"drive\",\"msg\":\"request token=secret-value failed\"}",
            "stderr");

        Assert.Equal("error", entry.Level);
        Assert.Equal("drive", entry.Source);
        Assert.DoesNotContain("secret-value", entry.Message);
        Assert.Contains("***", entry.Message);
    }

    [Fact]
    public async Task Async_job_is_polled_and_returns_output()
    {
        using var handler = new JobHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://rc.test/") };
        var client = new RcloneRcClient(http, http.BaseAddress, "user", "pass");

        var result = await client.RunJobAsync("operations/list", new { fs = "drive:", _async = true });

        Assert.Equal(1, result.GetProperty("list").GetArrayLength());
        Assert.Equal(2, handler.StatusCalls);
    }

    [Fact]
    public async Task Cancelling_async_job_requests_job_stop()
    {
        using var handler = new NeverFinishingJobHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://rc.test/") };
        var client = new RcloneRcClient(http, http.BaseAddress, "user", "pass");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.RunJobAsync("sync/move", new { _async = true }, cancellation.Token));

        Assert.True(handler.StopCalled);
    }

    private sealed class ErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"invalid_grant\",\"status\":401,\"path\":\"operations/list\",\"input\":{\"token\":\"top-secret\",\"remote\":\"drive:\"}}",
                    Encoding.UTF8, "application/json")
            });
    }

    private sealed class JobHandler : HttpMessageHandler
    {
        public int StatusCalls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("operations/list") ? "{\"jobid\":42}" :
                ++StatusCalls == 1 ? "{\"finished\":false}" :
                "{\"finished\":true,\"success\":true,\"output\":{\"list\":[{}]}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class NeverFinishingJobHandler : HttpMessageHandler
    {
        public bool StopCalled { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("job/stop")) StopCalled = true;
            var body = path.EndsWith("sync/move") ? "{\"jobid\":7}" : "{\"finished\":false}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
