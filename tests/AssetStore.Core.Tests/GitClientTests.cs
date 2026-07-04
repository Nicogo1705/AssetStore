// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Git;

namespace AssetStore.Core.Tests;

public sealed class GitClientTests
{
    [Theory]
    [InlineData("https://github.com/owner/MyAsset", "MyAsset")]
    [InlineData("https://github.com/owner/MyAsset.git", "MyAsset")]
    [InlineData("https://github.com/owner/MyAsset/", "MyAsset")]
    public void SafeRepoFolderName_extracts_clean_name(string url, string expected) =>
        Assert.Equal(expected, GitClient.SafeRepoFolderName(url));

    [Theory]
    [InlineData("https://github.com/owner/..")]
    [InlineData("https://evil/x/.")]
    [InlineData("https://evil/a:b")]
    public void SafeRepoFolderName_rejects_traversal(string url) =>
        Assert.Throws<InvalidOperationException>(() => GitClient.SafeRepoFolderName(url));
}
