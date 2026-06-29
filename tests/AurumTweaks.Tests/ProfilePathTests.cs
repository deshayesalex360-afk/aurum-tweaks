using System.IO;
using AurumTweaks.Services;
using Xunit;

namespace AurumTweaks.Tests;

/// <summary>
/// Pins the path-containment rule extracted from <see cref="ProfileService"/> into <see cref="ProfilePath"/>:
/// a profile id — even a malicious or future user-influenced one carrying ".." or an absolute/rooted path —
/// must always resolve to a file INSIDE the profiles directory, never escape it. Plain Path.Combine would
/// honour an absolute second argument as an arbitrary write; Path.GetFileName is what closes that. Pure path
/// math — no file is created.
/// </summary>
public class ProfilePathTests
{
    [Theory]
    [InlineData("preset-stock", "preset-stock.json")]        // ordinary id — passes through unchanged
    [InlineData("a1b2-c3d4-guid", "a1b2-c3d4-guid.json")]
    [InlineData("../../evil", "evil.json")]                  // posix traversal stripped
    [InlineData(@"..\..\evil", "evil.json")]                 // windows traversal stripped
    [InlineData(@"C:\Windows\System32\evil", "evil.json")]   // absolute/rooted escape stripped
    public void For_AlwaysResolvesInsideTheProfilesDir(string id, string expectedLeaf)
    {
        var dir = Path.Combine(Path.GetTempPath(), "AurumProfilesPathTest");

        var resolved = ProfilePath.For(dir, id);

        Assert.Equal(Path.Combine(dir, expectedLeaf), resolved);
        // Containment stated independently of the leaf: the resolved full path stays under dir.
        Assert.StartsWith(Path.GetFullPath(dir), Path.GetFullPath(resolved));
    }
}
