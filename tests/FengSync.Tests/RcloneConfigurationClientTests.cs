using System.Net;
using System.Text;
using System.Text.Json;
using FengSync.Core;
using FengSync.Core.Rclone.Configuration;

namespace FengSync.Tests;

public sealed class RcloneConfigurationClientTests
{
    [Fact]
    public async Task Lists_names_then_reads_only_each_backend_type()
    {
        var handler = new ConfigurationHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://rc.test/") };
        var api = new RcloneConfigurationClient(new RcloneRcClient(http, http.BaseAddress, "user", "pass"));

        var names = await api.ListRemoteNamesAsync();
        var types = await Task.WhenAll(names.Select(name => api.GetRemoteTypeAsync(name)));

        Assert.Equal(["archive", "personal"], names);
        Assert.Equal(["s3", "drive"], types.Select(type => type!).ToArray());
        Assert.DoesNotContain(handler.Bodies, body => body.Contains("config/dump", StringComparison.Ordinal));
        Assert.All(handler.Bodies.Skip(1), body => Assert.Contains("\"name\"", body));
    }

    [Fact]
    public async Task Create_sends_credentials_in_json_body_with_obscure_option()
    {
        var handler = new ConfigurationHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://rc.test/") };
        var api = new RcloneConfigurationClient(new RcloneRcClient(http, http.BaseAddress, "user", "pass"));

        await api.CreateAsync("server", "sftp", new Dictionary<string, string> { ["pass"] = "very-secret" }, new(Obscure: true));

        var request = JsonDocument.Parse(handler.Bodies.Single(body => body.Contains("very-secret", StringComparison.Ordinal))).RootElement;
        Assert.Equal("server", request.GetProperty("name").GetString());
        Assert.Equal("sftp", request.GetProperty("type").GetString());
        Assert.Equal("very-secret", request.GetProperty("parameters").GetProperty("pass").GetString());
        Assert.True(request.GetProperty("opt").GetProperty("obscure").GetBoolean());
    }

    [Fact]
    public async Task Verify_uses_non_recursive_directory_only_operations_list()
    {
        var handler = new ConfigurationHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://rc.test/") };
        var api = new RcloneConfigurationClient(new RcloneRcClient(http, http.BaseAddress, "user", "pass"));

        await api.VerifyAsync("personal");

        var body = handler.Bodies.Last();
        Assert.Contains("\"fs\":\"personal:\"", body);
        Assert.Contains("\"recurse\":false", body);
        Assert.Contains("\"dirsOnly\":true", body);
    }

    [Fact]
    public async Task OAuth_capabilities_are_read_from_real_rc_command_descriptors()
    {
        var handler = new ConfigurationHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://rc.test/") };
        var api = new RcloneConfigurationClient(new RcloneRcClient(http, http.BaseAddress, "user", "pass"));

        Assert.True(await api.SupportsOAuthControlAsync());
    }

    [Fact]
    public async Task S3_provider_metadata_preserves_enum_values_and_provider_specific_region_suggestions()
    {
        var handler = new ConfigurationHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://rc.test/") };
        var api = new RcloneConfigurationClient(new RcloneRcClient(http, http.BaseAddress, "user", "pass"));

        var providers = await api.GetS3ProvidersAsync();

        Assert.Collection(providers,
            aws => { Assert.Equal("AWS", aws.Name); Assert.Contains("us-east-1", aws.RegionSuggestions); },
            cloudflare => { Assert.Equal("Cloudflare", cloudflare.Name); Assert.Contains("auto", cloudflare.RegionSuggestions); });
    }

    private sealed class ConfigurationHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var path = request.RequestUri!.AbsolutePath;
            var json = path switch
            {
                "/config/listremotes" => "{\"remotes\":[\"personal:\",\"archive:\"]}",
                "/config/get" => Bodies[^1].Contains("archive", StringComparison.Ordinal)
                    ? "{\"type\":\"s3\",\"secret_access_key\":\"must-not-escape\"}"
                    : "{\"type\":\"drive\",\"token\":\"must-not-escape\"}",
                "/rc/list" => "{\"commands\":[{\"Path\":\"config/oauthstatus\"},{\"Path\":\"config/oauthstop\"}]}",
                "/config/oauthstatus" => "{\"status\":\"running\",\"authUrl\":\"http://127.0.0.1:1234/auth?state=x\"}",
                "/config/providers" => "{\"providers\":[{\"Name\":\"s3\",\"Options\":[{\"Name\":\"provider\",\"Examples\":[{\"Value\":\"AWS\",\"Help\":\"Amazon\"},{\"Value\":\"Cloudflare\",\"Help\":\"R2\"}]},{\"Name\":\"region\",\"Examples\":[{\"Value\":\"us-east-1\",\"Provider\":\"AWS\"},{\"Value\":\"auto\",\"Provider\":\"Cloudflare\"}]}]}]}",
                "/operations/list" => "{\"list\":[]}",
                _ => "{}"
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
