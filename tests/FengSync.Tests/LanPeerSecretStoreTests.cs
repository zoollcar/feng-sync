using System.Text;
using FengSync.Lan;
using Xunit;

namespace FengSync.Tests;

/// <summary>
/// Covers <see cref="LanPeerSecretStore"/>, the one file in the feature that holds something worth stealing.
/// </summary>
/// <remarks>
/// DPAPI is not being re-tested here — what is being tested is the shape around it: that a token is bound to
/// the device that was issued one, that a re-pair takes the old credential away, that an unreadable blob
/// reads as "no device is trusted", and that the bytes on disk contain neither the token nor the device id.
/// That last one is the property the peer list deliberately does not have, so it is checked by reading the
/// file rather than by trusting a comment.
/// </remarks>
public sealed class LanPeerSecretStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public LanPeerSecretStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fengsync-lan-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "lan-peer-secrets.dat");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task An_issue_answers_with_a_token_that_verifies_for_the_device_it_was_given_to()
    {
        var store = new LanPeerSecretStore(_path);

        var token = await store.IssueAsync("phone-a", CancellationToken.None);

        Assert.Equal(2 * LanPeerSecretStore.TokenBytes, token.Length);
        Assert.Matches("^[0-9a-f]+$", token);
        Assert.True(await store.VerifyAsync("phone-a", token, CancellationToken.None));
    }

    [Fact]
    public async Task A_token_binds_the_device_as_much_as_it_binds_the_bytes()
    {
        var store = new LanPeerSecretStore(_path);
        var token = await store.IssueAsync("phone-a", CancellationToken.None);
        await store.IssueAsync("phone-b", CancellationToken.None);

        // The whole point of pairing a device id with a secret: a peer that knows another device's token is not
        // that device, and the window has to be able to say "身份已变化" instead of quietly accepting it.
        Assert.False(await store.VerifyAsync("phone-b", token, CancellationToken.None));
        Assert.False(await store.VerifyAsync("phone-a", token[..^1], CancellationToken.None));
        Assert.False(await store.VerifyAsync("phone-a", "", CancellationToken.None));
        Assert.False(await store.VerifyAsync("", token, CancellationToken.None));
        Assert.False(await store.VerifyAsync("never-paired", token, CancellationToken.None));
    }

    [Fact]
    public async Task A_re_pair_takes_the_old_credential_away()
    {
        var store = new LanPeerSecretStore(_path);
        var oldToken = await store.IssueAsync("phone-a", CancellationToken.None);

        var fresh = await store.IssueAsync("phone-a", CancellationToken.None);

        Assert.NotEqual(oldToken, fresh);
        Assert.True(await store.VerifyAsync("phone-a", fresh, CancellationToken.None));
        Assert.False(await store.VerifyAsync("phone-a", oldToken, CancellationToken.None));
    }

    [Fact]
    public async Task Two_devices_never_hold_the_same_token()
    {
        var store = new LanPeerSecretStore(_path);
        var tokens = new List<string>();
        for (var index = 0; index < 6; index++) tokens.Add(await store.IssueAsync($"device-{index}", CancellationToken.None));

        Assert.Equal(6, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task A_token_survives_a_second_store_pointed_at_the_same_file()
    {
        var token = await new LanPeerSecretStore(_path).IssueAsync("phone-a", CancellationToken.None);

        Assert.True(await new LanPeerSecretStore(_path).VerifyAsync("phone-a", token, CancellationToken.None));
        Assert.Equal(token, await new LanPeerSecretStore(_path).TokenForAsync("phone-a", CancellationToken.None));
    }

    [Fact]
    public async Task The_file_holds_neither_the_token_nor_the_device_id_in_the_clear()
    {
        var store = new LanPeerSecretStore(_path);
        var token = await store.IssueAsync("小明的手机", CancellationToken.None);

        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(_path));
        Assert.DoesNotContain(token, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("小明的手机", raw, StringComparison.Ordinal);
        // A DPAPI blob is encryption, not encoding: a base64 or UTF-16 copy of the same JSON would be one click
        // away from readable in any decoder, so the bytes that grew are the evidence that they changed.
        var plain = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["小明的手机"] = token });
        Assert.NotEqual(plain, raw);
        Assert.True(new FileInfo(_path).Length > System.Text.Encoding.UTF8.GetByteCount(plain),
            $"密文 {new FileInfo(_path).Length} 字节，明文 {System.Text.Encoding.UTF8.GetByteCount(plain)} 字节");
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public async Task A_blob_that_will_not_decrypt_reads_as_no_device_trusted_and_repairs_itself()
    {
        // DPAPI fails for two reasons — somebody else wrote the file, or this is a different Windows user — and
        // both mean the same thing to the listener: re-pair, do not refuse to serve.
        await File.WriteAllBytesAsync(_path, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10], CancellationToken.None);
        var store = new LanPeerSecretStore(_path);

        Assert.False(await store.VerifyAsync("phone-a", "anything", CancellationToken.None));
        Assert.Null(await store.TokenForAsync("phone-a", CancellationToken.None));
        Assert.Empty(await store.SecretDeviceIdsAsync(CancellationToken.None));

        var token = await store.IssueAsync("phone-a", CancellationToken.None);
        Assert.True(await store.VerifyAsync("phone-a", token, CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_blob_is_a_crash_artifact_and_not_a_secret_of_length_zero()
    {
        await File.WriteAllBytesAsync(_path, [], CancellationToken.None);

        Assert.False(await new LanPeerSecretStore(_path).VerifyAsync("phone-a", "anything", CancellationToken.None));
    }

    [Fact]
    public async Task Un_trusting_a_device_deletes_the_credential_behind_it()
    {
        var store = new LanPeerSecretStore(_path);
        var token = await store.IssueAsync("phone-a", CancellationToken.None);
        await store.IssueAsync("phone-b", CancellationToken.None);

        Assert.True(await store.ForgetAsync("phone-a", CancellationToken.None));
        Assert.False(await store.ForgetAsync("phone-a", CancellationToken.None));
        Assert.False(await store.VerifyAsync("phone-a", token, CancellationToken.None));
        Assert.Equal(["phone-b"], await store.SecretDeviceIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Clearing_removes_the_file_rather_than_writing_an_empty_map_into_it()
    {
        var store = new LanPeerSecretStore(_path);
        await store.IssueAsync("phone-a", CancellationToken.None);
        Assert.True(store.HasSecrets);

        await store.ClearAsync(CancellationToken.None);

        Assert.False(store.HasSecrets);
        Assert.False(File.Exists(_path));
        // Nothing is trusted afterwards, and the store is still usable.
        var token = await store.IssueAsync("phone-a", CancellationToken.None);
        Assert.True(await store.VerifyAsync("phone-a", token, CancellationToken.None));
    }

    [Fact]
    public async Task A_device_id_is_matched_without_asking_the_peer_to_get_the_case_right()
    {
        var store = new LanPeerSecretStore(_path);
        var token = await store.IssueAsync("Phone-A", CancellationToken.None);

        Assert.True(await store.VerifyAsync("phone-a", token, CancellationToken.None));
        Assert.Equal(token, await store.TokenForAsync("PHONE-A", CancellationToken.None));
        Assert.True(await store.ForgetAsync("phone-a", CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_issues_do_not_lose_either_token()
    {
        var store = new LanPeerSecretStore(_path);
        var tokens = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(index => Task.Run(() => store.IssueAsync($"device-{index}", CancellationToken.None))));

        for (var index = 0; index < tokens.Length; index++)
            Assert.True(await store.VerifyAsync($"device-{index}", tokens[index], CancellationToken.None));
        Assert.Equal(6, (await store.SecretDeviceIdsAsync(CancellationToken.None)).Count);
        // The atomic replace means the file is either the old map or the new one, never a half-written JSON.
        Assert.Equal(["lan-peer-secrets.dat"], Directory.GetFiles(_root).Select(static path => Path.GetFileName(path)!).ToArray());
    }
}
