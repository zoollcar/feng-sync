using FengSync.Core.Updates;

namespace FengSync.Tests;

public sealed class ReleaseManifestTests
{
    [Fact]
    public void Validator_requires_product_runtime_version_and_sorted_safe_files()
    {
        var invalid = new ReleaseManifest("Other", "1.0.0-beta", "linux-x64", [new("b.bin", 1, new string('a', 64)), new("a.bin", -1, "bad")]);
        Assert.NotEmpty(ReleaseManifestValidator.Validate(invalid));
    }
}
