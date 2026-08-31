// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Security.Cryptography;
using System.Text;

namespace Cordango.Standalone.Forms;

/// <summary>
/// The address a published record is served at, and the credential that serves it.
///
/// <para>Generated, never authored. A memorable public address is a guessable one, and guessing it is
/// the whole attack — so the author declares the ROLE ("this is where the record is served") and the
/// runtime supplies the entropy. The field is read-only everywhere as a result, which is what keeps a
/// client from choosing its own.</para>
///
/// <para>128 bits as 26 characters of Crockford base32: no i, l, o or u, because this gets read aloud
/// and written down.</para>
/// </summary>
public static class PublicToken
{
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    public static string Mint()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);

        var builder = new StringBuilder(26);
        var accumulator = 0UL;
        var bits = 0;

        foreach (var b in bytes)
        {
            accumulator = (accumulator << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                builder.Append(Alphabet[(int)((accumulator >> bits) & 31)]);
            }
        }

        if (bits > 0) builder.Append(Alphabet[(int)((accumulator << (5 - bits)) & 31)]);
        return builder.ToString();
    }
}
