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
/// <para>
/// Only files participate: <b>empty directories are invisible to the hash</b> (adding or removing
/// one does not change it) — consistent with git, which doesn't track them either.
/// Files are streamed in fixed-size chunks, so memory use is constant regardless of file size.
/// </para>
/// </remarks>
public static class ContentHasher
{
    private const byte Cr = 0x0D;
    private const byte Lf = 0x0A;

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
        var buffer = new byte[81920];

        foreach (var (relative, full) in files)
        {
            using var stream = File.OpenRead(full);
            totalBytes += stream.Length;
            listing.Append(relative).Append('\n')
                   .Append(HashFile(stream, buffer)).Append('\n');
        }

        var hash = ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(listing.ToString())));
        return new HashResult(hash, files.Count, totalBytes);
    }

    /// <summary>
    /// Hashes one file. Text files (no NUL byte) are normalized CRLF-&gt;LF so the hash is identical
    /// regardless of the OS / git autocrlf setting the asset was checked out with; binary files are
    /// hashed as-is. Two streamed passes: a NUL scan (cheap — real binaries hit a NUL early), then
    /// the incremental hash.
    /// </summary>
    private static string HashFile(FileStream stream, byte[] buffer)
    {
        var isText = !ContainsNulByte(stream, buffer);
        stream.Position = 0;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (isText)
        {
            AppendCrlfNormalized(stream, sha, buffer);
        }
        else
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.AppendData(buffer, 0, read);
            }
        }

        return ToHex(sha.GetHashAndReset());
    }

    /// <summary>Feeds the stream to the hash with CR dropped from CRLF pairs (lone CRs are kept),
    /// handling a CR that falls on a chunk boundary.</summary>
    private static void AppendCrlfNormalized(Stream stream, IncrementalHash sha, byte[] buffer)
    {
        var output = new byte[buffer.Length + 1]; // +1: a CR carried over from the previous chunk
        var pendingCr = false;
        int read;

        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var n = 0;
            if (pendingCr)
            {
                if (buffer[0] != Lf)
                {
                    output[n++] = Cr; // previous chunk ended on a lone CR — keep it
                }

                pendingCr = false;
            }

            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (b == Cr)
                {
                    if (i + 1 == read)
                    {
                        pendingCr = true; // decision needs the next chunk's first byte
                    }
                    else if (buffer[i + 1] != Lf)
                    {
                        output[n++] = b;
                    }
                }
                else
                {
                    output[n++] = b;
                }
            }

            sha.AppendData(output, 0, n);
        }

        if (pendingCr)
        {
            sha.AppendData([Cr]); // file ended on a CR — keep it
        }
    }

    private static bool ContainsNulByte(Stream stream, byte[] buffer)
    {
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0)
            {
                return true;
            }
        }

        return false;
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
