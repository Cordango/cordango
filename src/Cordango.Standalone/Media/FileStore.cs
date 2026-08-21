// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Security.Cryptography;
using Cordango.Standalone.Http;

namespace Cordango.Standalone.Media;

/// <summary>What an attachment field's value points at.</summary>
/// <param name="Reference">The opaque token stored in the record's field.</param>
/// <param name="FileName">What the file was called when it was uploaded.</param>
/// <param name="ContentType">As declared by the uploader, and never trusted for anything but the
/// download header.</param>
/// <param name="Length">Bytes.</param>
public sealed record StoredFile(string Reference, string FileName, string ContentType, long Length);

/// <summary>Where uploaded files live. One implementation ships; an application that wants object
/// storage writes another and changes one registration.</summary>
public interface IFileStore
{
    Task<StoredFile> SaveAsync(Stream content, string fileName, string contentType, CancellationToken ct);

    Task<(StoredFile Meta, Stream Content)?> OpenAsync(string reference, CancellationToken ct);

    Task<bool> DeleteAsync(string reference, CancellationToken ct);
}

/// <summary>
/// Files on the local disk, addressed by the hash of their contents.
///
/// <para><b>Content addressing, for two reasons.</b> The same document attached to forty records is
/// stored once. And the reference cannot be guessed or walked: it is a hash, so there is no
/// <c>../</c> to smuggle through it and no sequence to enumerate. The original file name is metadata
/// rather than a path, which is what keeps a file called <c>..\..\web.config</c> from being a
/// problem.</para>
/// </summary>
public sealed class LocalFileStore : IFileStore
{
    private readonly string _root;

    public LocalFileStore(string root)
    {
        _root = Path.GetFullPath(root);
        System.IO.Directory.CreateDirectory(_root);
    }

    /// <summary>Bytes above which an upload is refused, before anything is written.</summary>
    public long MaxBytes { get; init; } = 25 * 1024 * 1024;

    public async Task<StoredFile> SaveAsync(Stream content, string fileName, string contentType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Staged to a temporary file first: the reference is the hash of the content, and the hash
        // is not known until the last byte has been read.
        var staging = Path.Combine(_root, "incoming-" + Guid.NewGuid().ToString("n"));
        string hash;
        long length;

        try
        {
            await using (var file = File.Create(staging))
            {
                using var sha = SHA256.Create();
                await using var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write);

                length = await CopyLimited(content, hashing, MaxBytes, ct);
                await hashing.FlushFinalBlockAsync(ct);
                hash = Convert.ToHexStringLower(sha.Hash!);
            }

            var target = PathFor(hash);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Already there: identical bytes, so the upload is complete and the staging copy is
            // redundant rather than a conflict.
            if (File.Exists(target)) File.Delete(staging);
            else File.Move(staging, target);

            var meta = new StoredFile(hash, SafeName(fileName), contentType, length);
            await File.WriteAllTextAsync(target + ".meta", System.Text.Json.JsonSerializer.Serialize(meta), ct);
            return meta;
        }
        catch
        {
            if (File.Exists(staging)) File.Delete(staging);
            throw;
        }
    }

    public async Task<(StoredFile Meta, Stream Content)?> OpenAsync(string reference, CancellationToken ct)
    {
        if (!IsReference(reference)) return null;

        var path = PathFor(reference);
        if (!File.Exists(path)) return null;

        var meta = File.Exists(path + ".meta")
            ? System.Text.Json.JsonSerializer.Deserialize<StoredFile>(await File.ReadAllTextAsync(path + ".meta", ct))
            : null;

        meta ??= new StoredFile(reference, reference, "application/octet-stream", new FileInfo(path).Length);
        return (meta, File.OpenRead(path));
    }

    public Task<bool> DeleteAsync(string reference, CancellationToken ct)
    {
        if (!IsReference(reference)) return Task.FromResult(false);

        var path = PathFor(reference);
        if (!File.Exists(path)) return Task.FromResult(false);

        File.Delete(path);
        if (File.Exists(path + ".meta")) File.Delete(path + ".meta");
        return Task.FromResult(true);
    }

    /// <summary>Two levels of fan-out, so a busy application does not end up with a directory
    /// holding a million entries.</summary>
    private string PathFor(string hash) => Path.Combine(_root, hash[..2], hash[2..4], hash);

    /// <summary>A reference is 64 lower-case hex characters and nothing else. Checked before the
    /// value is ever joined onto a path — a validated shape is the defence, not a
    /// <c>Replace("..", "")</c> after the fact.</summary>
    private static bool IsReference(string? value) =>
        value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>Keep the name for the download header, drop anything that makes it a path.</summary>
    private static string SafeName(string fileName)
    {
        var name = Path.GetFileName(fileName ?? "");
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "file" : name;
    }

    /// <summary>Copy, and stop at the limit rather than after it. A <c>Content-Length</c> header is
    /// the uploader's claim about the body, not a fact about it, so the ceiling has to be enforced
    /// against the bytes actually arriving.</summary>
    private static async Task<long> CopyLimited(Stream from, Stream to, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;

        while ((read = await from.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > limit)
                throw new RecordException("media.too_large",
                    $"The file is larger than the {limit / (1024 * 1024)} MB limit.", 413);

            await to.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return total;
    }
}
