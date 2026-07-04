// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
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

    [Fact]
    public void Crlf_and_lf_checkouts_hash_identically()
    {
        var crlf = MakeDir(("a.txt", "line1\r\nline2\r\n"));
        var lf = MakeDir(("a.txt", "line1\nline2\n"));

        Assert.Equal(ContentHasher.HashDirectory(lf).Hash, ContentHasher.HashDirectory(crlf).Hash);
    }

    [Fact]
    public void Crlf_across_the_streaming_chunk_boundary_is_normalized()
    {
        // 81920 is the hasher's internal chunk size — put the CR at the last byte of chunk 1
        // and the LF at the first byte of chunk 2.
        var crlf = MakeDir(("a.txt", new string('a', 81919) + "\r\n" + new string('b', 10)));
        var lf = MakeDir(("a.txt", new string('a', 81919) + "\n" + new string('b', 10)));

        Assert.Equal(ContentHasher.HashDirectory(lf).Hash, ContentHasher.HashDirectory(crlf).Hash);
    }

    [Fact]
    public void Lone_cr_is_preserved_even_at_the_chunk_boundary()
    {
        var withCr = MakeDir(("a.txt", new string('a', 81919) + "\r" + new string('b', 10)));
        var without = MakeDir(("a.txt", new string('a', 81919) + new string('b', 10)));

        Assert.NotEqual(ContentHasher.HashDirectory(without).Hash, ContentHasher.HashDirectory(withCr).Hash);
    }

    [Fact]
    public void Binary_files_are_hashed_as_is()
    {
        var crlf = MakeBinaryDir(("a.bin", new byte[] { 0x00, 0x0D, 0x0A }));
        var lf = MakeBinaryDir(("a.bin", new byte[] { 0x00, 0x0A }));

        Assert.NotEqual(ContentHasher.HashDirectory(lf).Hash, ContentHasher.HashDirectory(crlf).Hash);
    }

    [Fact]
    public void Empty_directories_are_invisible_to_the_hash()
    {
        var plain = MakeDir(("a.txt", "x"));
        var withEmpty = MakeDir(("a.txt", "x"));
        Directory.CreateDirectory(Path.Combine(withEmpty, "empty", "nested"));

        Assert.Equal(ContentHasher.HashDirectory(plain).Hash, ContentHasher.HashDirectory(withEmpty).Hash);
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

    private string MakeBinaryDir(params (string Path, byte[] Content)[] files)
    {
        var root = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
        }

        return root;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
