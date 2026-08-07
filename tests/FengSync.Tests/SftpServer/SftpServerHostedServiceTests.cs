using System.Text.Json;
using FengSync.Core.Rclone.Lifecycle;
using FengSync.Core.SftpServer;

namespace FengSync.Tests.SftpServer;

public sealed class SftpServerHostedServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fengsync-sftp-rc-" + Guid.NewGuid().ToString("N"));

    public SftpServerHostedServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Start_and_stop_use_dedicated_serve_json_endpoints()
    {
        const string secret = "never-on-command-line";
        var rc = new FakeServeClient();
        var service = new SftpServerHostedService(rc, _ => Task.FromResult<string?>(secret), (_, _) => Task.FromResult(true));

        await service.StartAsync(EnabledOptions());

        Assert.True(service.IsRunning);
        Assert.Equal("sftp-test", service.ServerId);
        var start = Assert.Single(rc.Calls, x => x.Operation == "serve/start");
        Assert.Equal("sftp", start.Payload.GetProperty("type").GetString());
        Assert.Equal(secret, start.Payload.GetProperty("pass").GetString());
        Assert.DoesNotContain(rc.Calls, x => x.Operation == "core/command");
        Assert.Contains(rc.Calls, x => x.Operation == "serve/list");

        await service.StopAsync();

        Assert.False(service.IsRunning);
        Assert.Contains(rc.Calls, x => x.Operation == "serve/stop" && x.Payload.GetProperty("id").GetString() == "sftp-test");
    }

    [Fact]
    public async Task Start_failure_never_surfaces_secret_bearing_rc_text()
    {
        const string secret = "top-secret-password";
        var service = new SftpServerHostedService(new ThrowingClient(secret),
            _ => Task.FromResult<string?>(secret), (_, _) => Task.FromResult(true));

        var error = await Assert.ThrowsAsync<SftpServerOperationException>(() => service.StartAsync(EnabledOptions()));

        Assert.Equal("serve/start", error.Operation);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.NotEmpty(error.CorrelationId);
        Assert.NotEmpty(error.SuggestedAction);
    }

    [Fact]
    public async Task Failed_stop_keeps_server_identity_and_can_be_retried()
    {
        var rc = new FailFirstStopClient();
        var service = new SftpServerHostedService(rc, _ => Task.FromResult<string?>("password"),
            (_, _) => Task.FromResult(true));
        await service.StartAsync(EnabledOptions());

        await Assert.ThrowsAsync<SftpServerOperationException>(() => service.StopAsync());

        Assert.True(service.IsRunning);
        Assert.Equal("sftp-retry", service.ServerId);

        await service.StopAsync();

        Assert.False(service.IsRunning);
        Assert.Null(service.ServerId);
        Assert.Equal(2, rc.StopAttempts);
    }

    private SftpServerOptions EnabledOptions() => new(
        Enabled: true,
        RootPath: _root,
        UserName: "feng",
        HostKeyPath: Path.Combine(_root, "host.key"),
        PasswordConfigured: true);

    private sealed class FakeServeClient : IRcloneLifecycleClient
    {
        private bool _running;
        public List<(string Operation, JsonElement Payload)> Calls { get; } = [];

        public Task<JsonElement> CallAsync(string operation, object payload, CancellationToken cancellationToken = default)
        {
            var json = JsonSerializer.SerializeToElement(payload);
            Calls.Add((operation, json));
            if (operation == "serve/start")
            {
                _running = true;
                return Task.FromResult(JsonSerializer.SerializeToElement(new { id = "sftp-test", addr = "127.0.0.1:2222" }));
            }
            if (operation == "serve/stop") _running = false;
            if (operation == "serve/list")
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    list = _running ? new[] { new { id = "sftp-test", addr = "127.0.0.1:2222" } } : []
                }));
            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        }
    }

    private sealed class ThrowingClient(string secret) : IRcloneLifecycleClient
    {
        public Task<JsonElement> CallAsync(string operation, object payload, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("raw RC input contained " + secret);
    }

    private sealed class FailFirstStopClient : IRcloneLifecycleClient
    {
        public int StopAttempts { get; private set; }

        public Task<JsonElement> CallAsync(string operation, object payload, CancellationToken cancellationToken = default)
        {
            if (operation == "serve/start")
                return Task.FromResult(JsonSerializer.SerializeToElement(new { id = "sftp-retry", addr = "127.0.0.1:2222" }));
            if (operation == "serve/stop" && ++StopAttempts == 1)
                throw new InvalidOperationException("temporary stop failure");
            if (operation == "serve/list")
                return Task.FromResult(JsonSerializer.SerializeToElement(new { list = new[] { new { id = "sftp-retry" } } }));
            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        }
    }
}
