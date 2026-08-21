// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;

namespace Cordango.Cli.Workspace;

/// <summary>
/// Writing source files so a crash cannot leave half of one.
///
/// <para><b>Why this exists at all.</b> A watch loop rebuilds on save, and an agent applying an
/// operation rewrites several files in a row. A partially-written <c>.cordango</c> is not merely a bad
/// build — <see cref="Cord.CordFiles.Join"/> would report it as unreadable and the app would appear
/// to have lost an entity. Write-then-rename makes every observer see either the old file or the new
/// one, which is the property the whole "last valid build stays live" behaviour rests on.</para>
/// </summary>
public static class AtomicFile
{
    /// <summary>UTF-8 with NO byte-order mark. The BOM would change the bytes
    /// <see cref="Cord.CordFiles"/> hashes, so a file written here and a file hashed there would
    /// disagree about identical content.</summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static void Write(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Same directory as the target: File.Move across volumes is a copy, and a copy is not atomic.
        var temp = path + ".tmp" + Environment.ProcessId;
        File.WriteAllText(temp, content, Utf8);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Reads a file as the bytes it actually holds.
    ///
    /// <para><b>No line-ending translation, deliberately.</b> Canonical <c>.cordango</c> content is LF,
    /// and the hash covers it. If a Windows checkout converted to CRLF, every file would read as
    /// modified and <c>cordango fmt</c> would rewrite the tree on every run — so the scaffold ships a
    /// <c>.gitattributes</c> pinning <c>*.cord</c> to LF, and this reads without helping.</para>
    /// </summary>
    public static string Read(string path) => File.ReadAllText(path, Utf8);
}
