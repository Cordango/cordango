// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.NodeVue;

/// <summary>
/// Writing a generated TypeScript file.
///
/// <para><b>Why this exists rather than using <see cref="Source"/> directly.</b> The shared writer
/// opens a block by putting the brace on its own line, which is C#'s convention and is wrong here —
/// TypeScript puts it at the end of the line that opened it, and every formatter, linter and
/// reviewer in that ecosystem expects it there. It is not a style quibble: generated code that does
/// not look like the language it is written in reads as machine output somebody has to tolerate
/// rather than source they can own, which is the whole promise of this target.</para>
///
/// <para>Everything else — two-space indentation, <c>\n</c> endings on every platform, exactly one
/// trailing newline — is <see cref="Source"/>'s, wrapped rather than reimplemented, because those
/// are the properties the determinism tests measure.</para>
/// </summary>
internal sealed class TsSource
{
    private readonly Source _source = new(2);

    public int IndentWidth => _source.IndentWidth;

    public TsSource Line(string text = "")
    {
        _source.Line(text);
        return this;
    }

    /// <summary>A multi-line block, each line indented at the current depth. For doc comments and
    /// the prose an emitter carries.</summary>
    public TsSource Lines(string text)
    {
        _source.Lines(text);
        return this;
    }

    /// <summary>
    /// Open a block: the brace goes at the end of <paramref name="head"/>, where TypeScript puts
    /// it.
    ///
    /// <para>With a space, except after an opening bracket — <c>register(</c> and <c>() =&gt; (</c>
    /// both take the brace immediately, and <c>register( {</c> is not something anybody would
    /// write.</para>
    /// </summary>
    public TsSource Open(string head)
    {
        var joined = head.EndsWith('(') || head.EndsWith('[') ? head + "{" : head + " {";
        _source.Line(joined);
        _source.Indent();
        return this;
    }

    /// <summary>Close a block. <paramref name="suffix"/> carries whatever follows the brace — a
    /// semicolon for an object literal assigned to a constant, a comma for one inside an
    /// array.</summary>
    public TsSource Close(string suffix = "")
    {
        _source.Outdent();
        _source.Line("}" + suffix);
        return this;
    }

    /// <summary>Open a call whose arguments are one per line: <c>name(</c> then the arguments, then
    /// <c>);</c> from <see cref="CloseCall"/>.</summary>
    public TsSource OpenCall(string head)
    {
        _source.Line(head + "(");
        _source.Indent();
        return this;
    }

    public TsSource CloseCall(string suffix = ";")
    {
        _source.Outdent();
        _source.Line(")" + suffix);
        return this;
    }

    /// <summary>Open an array literal, one element per line. An empty head opens a bare array —
    /// an argument to the call above it — rather than one with a stray space in front of it.</summary>
    public TsSource OpenArray(string head = "")
    {
        _source.Line(head.Length == 0 ? "[" : head + " [");
        _source.Indent();
        return this;
    }

    public TsSource CloseArray(string suffix = "")
    {
        _source.Outdent();
        _source.Line("]" + suffix);
        return this;
    }

    /// <summary>Indent without a brace — for a continued expression or an argument list.</summary>
    public TsSource Indent()
    {
        _source.Indent();
        return this;
    }

    public TsSource Outdent()
    {
        _source.Outdent();
        return this;
    }

    public override string ToString() => _source.ToString();
}
