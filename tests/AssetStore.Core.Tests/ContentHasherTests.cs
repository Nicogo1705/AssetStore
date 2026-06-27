// Copyright (c) Stride contributors (https://stride3d.net)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using AssetStore.Core.Hashing;

namespace AssetStore.Core.Tests;

public sealed class ContentHasherTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ash-").FullName;

    [Fact]
    public void Hash_is_stable_for_identical_content()
    {
        var a = MakeDir(("a.txt", "hello"), ("sub/b.txt", "world"));
        var b = MakeDir(("a.txt", "hello"), ("sub/b.txt", "world"));

        Assert.Equal(ContentHasher.HashDirectory(a).Hash, ContentHasher.HashDirectory(b).Hash);
    }

    [Fact]
    public void Hash_is_independent_of_file_creation_order()
    {
        var a = MakeDir(("a.txt", "1"), ("b.txt", "2"));
        var b = MakeDir(("b.txt", "2"), ("a.txt", "1"));

        Assert.Equal(ContentHasher.HashDirectory(a).Hash, ContentHasher.HashDirectory(b).Hash);
    }

    [Fact]
    public void Hash_changes_when_content_changes()
    {
        var a = MakeDir(("a.txt", "hello"));
        var b = MakeDir(("a.txt", "HELLO"));

        Assert.NotEqual(ContentHasher.HashDirectory(a).Hash, ContentHasher.HashDirectory(b).Hash);
    }

    [Fact]
    public void Reports_file_count_and_size()
    {
        var dir = MakeDir(("a.txt", "abc"), ("b.txt", "de"));
        var result = ContentHasher.HashDirectory(dir);

        Assert.Equal(2, result.FileCount);
        Assert.Equal(5, result.TotalBytes);
    }

    private string MakeDir(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return root;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
