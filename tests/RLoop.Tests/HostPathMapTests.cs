using RLoop.Core;

namespace RLoop.Tests;

public sealed class HostPathMapTests
{
    private static readonly string Project = Path.Combine(Path.GetTempPath(), "resoloop-host-path", "repo");
    private const string Share = @"\\wsl.localhost\Distro\home\user\repo";

    [Fact]
    public void MapsOnlyPathsUnderTheCliPrefixIntoTheHostSeparatorStyle()
    {
        var source = Path.Combine(Project, "content", "tex.png");
        var map = HostPathMap.Parse(Project + Path.DirectorySeparatorChar + "=" + Share + @"\")!;

        Assert.Equal(Share + @"\content\tex.png", map.Map(source));
        Assert.Equal("/srv/repo/content/tex.png", HostPathMap.Parse(Project + "=/srv/repo")!.Map(source));
        // A sibling directory that merely starts with the same text is not under the prefix.
        var sibling = Path.Combine(Project + "-other", "tex.png");
        Assert.False(map.Covers(sibling));
        Assert.Equal(sibling, map.Map(sibling));
    }

    [Fact]
    public void RejectsValuesWithoutAnAbsoluteCliPrefixOrATarget()
    {
        foreach (var value in new[] { "no-separator", "relative/path=/srv/repo", "=/srv/repo", Project + "=" })
            Assert.Equal("INVALID_HOST_PATH_MAP", Assert.Throws<RLoopException>(() => HostPathMap.Parse(value, "cli")).Code);
    }
}
