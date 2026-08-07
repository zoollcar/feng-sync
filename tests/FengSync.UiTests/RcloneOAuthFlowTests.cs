using System.Text.Json;
using FengSync.Core.Rclone.Configuration;
using FengSync.Services;
using Xunit;

namespace FengSync.UiTests;

public sealed class RcloneOAuthFlowTests
{
    [Fact]
    public async Task Create_uses_rc_state_protocol_and_opens_oauthstatus_url()
    {
        var api = new FakeConfigurationApi();
        api.Outputs.Enqueue(State("*oauth-islocal,teamdrive,,", "config_is_local", true));
        api.Outputs.Enqueue(State("teamdrive_ok", "config_change_team_drive", false));
        api.Outputs.Enqueue(State(""));
        Uri? opened = null;
        var flow = new RcloneOAuthFlow(api, uri => { opened = uri; api.AuthorizationCompleted.TrySetResult(); });

        await flow.CreateGoogleDriveAsync("drive_test", new Dictionary<string, string>
        {
            ["scope"] = "drive",
            ["client_id"] = "client"
        });

        Assert.Equal(3, api.Calls.Count);
        Assert.Equal("create", api.Calls[0].Operation);
        Assert.Equal("drive", api.Calls[0].Type);
        Assert.True(api.Calls[0].Options.NonInteractive);
        Assert.Equal("true", api.Calls[1].Options.Result);
        Assert.Equal("false", api.Calls[2].Options.Result);
        Assert.Equal("true", api.Calls[0].Parameters["config_auth_no_browser"]);
        Assert.Equal("http://127.0.0.1:53682/auth?state=abc", opened?.AbsoluteUri);
    }

    [Fact]
    public async Task Reconnect_starts_with_non_interactive_update()
    {
        var api = new FakeConfigurationApi { BlockCallIndex = -1 };
        api.Outputs.Enqueue(State("oauth", "config_is_local", true));
        api.Outputs.Enqueue(State(""));
        var flow = new RcloneOAuthFlow(api, _ => { });

        await flow.ReconnectGoogleDriveAsync("existing");

        Assert.Equal("update", api.Calls[0].Operation);
        Assert.True(api.Calls[0].Options.NonInteractive);
        Assert.Equal("true", api.Calls[1].Options.Result);
    }

    [Fact]
    public async Task Required_unknown_question_fails_instead_of_guessing()
    {
        var api = new FakeConfigurationApi { BlockCallIndex = -1 };
        api.Outputs.Enqueue(State("unknown", "account_choice", required: true, hasDefault: false));
        var flow = new RcloneOAuthFlow(api, _ => { });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => flow.ReconnectGoogleDriveAsync("existing"));

        Assert.Contains("account_choice", error.Message);
    }

    [Fact]
    public async Task Missing_oauth_rc_capability_fails_without_text_fallback()
    {
        var api = new FakeConfigurationApi { SupportsOAuth = false };
        var flow = new RcloneOAuthFlow(api, _ => { });

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => flow.ReconnectGoogleDriveAsync("existing"));

        Assert.Contains("config/oauthstatus", error.Message);
        Assert.Empty(api.Calls);
    }

    private static RcloneConfigState State(string state, string? option = null, bool defaultValue = false, bool required = false, bool hasDefault = true)
    {
        JsonElement? defaultElement = option is not null && hasDefault
            ? JsonSerializer.SerializeToElement(defaultValue)
            : null;
        return new(state, option is null ? null : new(option, "help", defaultElement, required), "");
    }

    private sealed class FakeConfigurationApi : IRcloneConfigurationApi
    {
        public bool SupportsOAuth { get; init; } = true;
        public int BlockCallIndex { get; init; } = 1;
        public Queue<RcloneConfigState> Outputs { get; } = new();
        public List<ConfigCall> Calls { get; } = [];
        public TaskCompletionSource AuthorizationCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> SupportsOAuthControlAsync(CancellationToken cancellationToken = default) => Task.FromResult(SupportsOAuth);
        public Task<IReadOnlyList<string>> ListRemoteNamesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string?> GetRemoteTypeAsync(string name, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteAsync(string name, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task VerifyAsync(string name, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task StopOAuthAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<RcloneConfigState> CreateAsync(string name, string type, IReadOnlyDictionary<string, string> parameters, RcloneConfigOptions options, CancellationToken cancellationToken = default)
            => ConfigureAsync("create", type, parameters, options, cancellationToken);

        public Task<RcloneConfigState> UpdateAsync(string name, IReadOnlyDictionary<string, string> parameters, RcloneConfigOptions options, CancellationToken cancellationToken = default)
            => ConfigureAsync("update", null, parameters, options, cancellationToken);

        public Task<RcloneOAuthStatus> GetOAuthStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new RcloneOAuthStatus("running", new Uri("http://127.0.0.1:53682/auth?state=abc")));

        private async Task<RcloneConfigState> ConfigureAsync(string operation, string? type, IReadOnlyDictionary<string, string> parameters, RcloneConfigOptions options, CancellationToken cancellationToken)
        {
            var index = Calls.Count;
            Calls.Add(new(operation, type, new Dictionary<string, string>(parameters), options));
            if (index == BlockCallIndex) await AuthorizationCompleted.Task.WaitAsync(cancellationToken);
            return Outputs.Dequeue();
        }
    }

    private sealed record ConfigCall(string Operation, string? Type, IReadOnlyDictionary<string, string> Parameters, RcloneConfigOptions Options);
}
