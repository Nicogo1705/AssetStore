// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Git;

namespace AssetStore.Core.Tests;

public sealed class GitTagParsingTests
{
    [Fact]
    public void Parses_lightweight_and_annotated_tags()
    {
        // Annotated tag v1.1.0: a tag-object line plus a peeled (^{}) line carrying the real commit.
        var output = string.Join('\n',
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\trefs/tags/v1.0.0",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\trefs/tags/v1.1.0",
            "cccccccccccccccccccccccccccccccccccccccc\trefs/tags/v1.1.0^{}");

        var tags = GitClient.ParseLsRemoteTags(output);

        Assert.Equal(2, tags.Count);
        Assert.Equal(("v1.0.0", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), tags[0]);
        // Annotated tag resolves to the peeled commit, not the tag-object sha.
        Assert.Equal(("v1.1.0", "cccccccccccccccccccccccccccccccccccccccc"), tags[1]);
    }

    [Fact]
    public void Ignores_non_tag_lines()
    {
        var tags = GitClient.ParseLsRemoteTags("dddddddddddddddddddddddddddddddddddddddd\trefs/heads/main");
        Assert.Empty(tags);
    }
}
