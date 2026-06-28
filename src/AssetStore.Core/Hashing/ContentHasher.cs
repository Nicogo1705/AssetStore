// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Security.Cryptography;
using System.Text;

namespace AssetStore.Core.Hashing;

/// <summary>
/// Computes a deterministic SHA-256 hash of a folder's contents.
/// </summary>
/// <remarks>
/// The hash is built from a canonical listing: for every file (recursively), sorted by its
/// forward-slash relative path using ordinal comparison, the line
/// <c>&lt;relativePath&gt;\n&lt;sha256-hex-of-bytes&gt;\n</c> is appended; the SHA-256 of the whole
/// listing (UTF-8) is the result. This is order-independent and platform-independent for a given
/// set of file bytes.
/// </remarks>
public static class ContentHasher
{
    /// <summary>Hashes every file under <paramref name="directory"/> and returns a lowercase hex digest.</summary>
    public static HashResult HashDirectory(string directory)
    {
        var root = Path.GetFullPath(directory);
        var files = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Relative: ToRelative(root, path), Full: path))
            .OrderBy(f => f.Relative, StringComparer.Ordinal)
            .ToList();

        var listing = new StringBuilder();
        long totalBytes = 0;

        foreach (var (relative, full) in files)
        {
            var bytes = File.ReadAllBytes(full);
            totalBytes += bytes.Length;
            // Normalize CRLF->LF for text files so the hash is identical regardless of the OS / git
            // autocrlf setting the asset was checked out with. Binary files are hashed as-is.
            var hashed = NormalizeIfText(bytes);
            listing.Append(relative).Append('\n')
                   .Append(ToHex(SHA256.HashData(hashed))).Append('\n');
        }

        var hash = ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(listing.ToString())));
        return new HashResult(hash, files.Count, totalBytes);
    }

    /// <summary>Removes CR from CRLF pairs for text files (no NUL byte); returns binary content unchanged.</summary>
    private static byte[] NormalizeIfText(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return bytes; // looks binary
        }

        var output = new byte[bytes.Length];
        var n = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0x0D && i + 1 < bytes.Length && bytes[i + 1] == 0x0A)
            {
                continue; // drop CR before LF
            }

            output[n++] = bytes[i];
        }

        return n == bytes.Length ? output : output[..n];
    }

    private static string ToRelative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string ToHex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}

/// <summary>Result of hashing a directory.</summary>
/// <param name="Hash">Lowercase hex SHA-256 digest.</param>
/// <param name="FileCount">Number of files included.</param>
/// <param name="TotalBytes">Total size of included files.</param>
public readonly record struct HashResult(string Hash, int FileCount, long TotalBytes);
