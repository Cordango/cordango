// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;

namespace Cordango.SourceGen.DotNetVue.Emit;

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
