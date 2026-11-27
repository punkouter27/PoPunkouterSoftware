using PoPunkouterSoftware.Client;

namespace PoPunkouterSoftware.Unit;

public sealed class GlobalFormatTests
{
    [Theory]
    [InlineData("https://github.com/punkouter27/PoBox.git", "PoBox/SCREENSHOTS/a.gif", "https://github.com/punkouter27/PoBox/blob/HEAD/SCREENSHOTS/a.gif")]
    [InlineData("https://github.com/punkouter27/PoBox", "SCREENSHOTS/a.gif", "https://github.com/punkouter27/PoBox/blob/HEAD/SCREENSHOTS/a.gif")]
    [InlineData("https://github.com/punkouter27/PoPunkouterSoftware.git", "docs/unite/RULES.md", "https://github.com/punkouter27/PoPunkouterSoftware/blob/HEAD/docs/unite/RULES.md")]
    [InlineData("https://github.com/punkouter27/PoBox.git", "PoBoxTools/x.md", "https://github.com/punkouter27/PoBox/blob/HEAD/PoBoxTools/x.md")]
    public void GitHubPath_DropsTheWorkspaceRepoFolderOnce(string remote, string path, string expected) =>
        GlobalFormat.GitHubPath(remote, null, path).Should().Be(expected);

    [Fact]
    public void GitHubPath_WithoutARepoOrPath_IsNull()
    {
        GlobalFormat.GitHubPath(null, null, "PoBox/a.gif").Should().BeNull();
        GlobalFormat.GitHubPath("https://github.com/punkouter27/PoBox.git", null, "").Should().BeNull();
    }
}
