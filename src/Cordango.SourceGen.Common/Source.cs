// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;

namespace Cordango.SourceGen.Common;

/// <summary>
/// Writing a generated file.
///
/// <para>Line endings are <c>\n</c> and indentation is spaces, on every platform, because the
/// alternative is output that differs by build machine — and the whole contract of this generator is
/// that a definition produces one application rather than a family of similar ones.</para>
/// </summary>
public sealed class Source
{
    private readonly StringBuilder _builder = new();
    private int _depth;

    public Source(int indentWidth = 4) => IndentWidth = indentWidth;

    public int IndentWidth { get; }

    public Source Line(string text = "")
    {
        if (text.Length == 0)
        {
            _builder.Append('\n');
            return this;
        }

        _builder.Append(' ', _depth * IndentWidth).Append(text).Append('\n');
        return this;
    }

    /// <summary>A multi-line string, each line indented at the current depth. Used for the doc
    /// comments and prose blocks emitters carry, so they wrap correctly wherever they land.</summary>
    public Source Lines(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            Line(line);
        return this;
    }

    public Source Open(string text)
    {
        Line(text);
        Line("{");
        _depth++;
        return this;
    }

    public Source Close(string suffix = "")
    {
        _depth--;
        Line("}" + suffix);
        return this;
    }

    /// <summary>Indent without a brace — for a continued expression or an initialiser list.</summary>
    public Source Indent()
    {
        _depth++;
        return this;
    }

    public Source Outdent()
    {
        _depth--;
        return this;
    }

    /// <summary>The file, with exactly one trailing newline. Editors and diff tools both expect
    /// one; none and two are equally noisy.</summary>
    public override string ToString()
    {
        var text = _builder.ToString().TrimEnd('\n');
        return text.Length == 0 ? "" : text + "\n";
    }
}

/// <summary>
/// Splicing a built block into a raw-string template.
///
/// <para>An emitter reads best when it shows the file it produces — the braces balance by eye and
/// the indentation is the indentation. What that costs is one sharp edge: C# strips the common
/// margin from the LITERAL lines of a raw string only, so an interpolated block lands at column zero
/// however neatly the template around it is laid out. These two put it back.</para>
/// </summary>
public static class Code
{
    /// <summary>Parts joined and indented as one block, for the hole in a template that stands on
    /// its own line. An empty sequence yields an empty string rather than a stray blank line.</summary>
    public static string Block(IEnumerable<string> parts, int indent, string separator = "\n\n") =>
        Indent(string.Join(separator, parts), indent);

    /// <summary>Every line moved right, except the blank ones — a blank line with trailing spaces on
    /// it is invisible in an editor and loud in a diff.</summary>
    public static string Indent(string text, int spaces)
    {
        if (text.Length == 0) return "";

        var pad = new string(' ', spaces);
        return string.Join('\n', text.Split('\n').Select(line => line.Length == 0 ? line : pad + line));
    }
}
