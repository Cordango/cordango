// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Cordango.Cli.Workspace;

/// <summary>
/// The syntax layer: a <see cref="JsonObject"/> as YAML bytes, and back.
///
/// <para><b>The only place in the product that knows what YAML is.</b>
/// <see cref="Cord.CordSource"/> owns the format — which files exist, what they are called, what
/// they contain — and produces plain JSON documents, because rule 0 keeps a serializer out of
/// <c>Cordango.Cord</c>. That split also keeps CordyOSS §15's JSONC-versus-YAML question a swap of
/// this one file rather than a rewrite.</para>
///
/// <para><b>Emitted through the low-level emitter, not a serializer.</b> A serializer would need an
/// object graph, and the two properties that matter here — <b>key order</b> and <b>exact number
/// text</b> — are precisely what a graph round trip loses. Key order is how a file stays readable
/// and diffable; number text is how <c>1.0</c> stays a decimal instead of becoming <c>1</c> and
/// changing the definition hash.</para>
/// </summary>
public static class Yaml
{
    /// <summary>Canonical bytes: block style, two-space indent, indented sequences, LF, one trailing
    /// newline. Formatting is contract — two paths that produce one application must produce one set
    /// of bytes, or every diff is whitespace and "the files and the model agree" stops being
    /// checkable.</summary>
    public static string Write(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var text = new StringWriter { NewLine = "\n" };
        var emitter = new Emitter(text, new EmitterSettings()
            .WithBestIndent(2)
            .WithIndentedSequences()
            .WithNewLine("\n"));

        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart(null, null, isImplicit: true));
        Emit(emitter, document);
        emitter.Emit(new DocumentEnd(isImplicit: true));
        emitter.Emit(new StreamEnd());

        var result = text.ToString().Replace("\r\n", "\n");
        return result.EndsWith('\n') ? result : result + "\n";
    }

    public static (JsonObject? Document, string? Error) Read(string text)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));

            if (stream.Documents.Count == 0) return (new JsonObject(), null);
            if (stream.Documents[0].RootNode is not YamlMappingNode root)
                return (null, "the document is not a mapping");

            return ((JsonObject)Convert(root)!, null);
        }
        catch (YamlException ex)
        {
            return (null, $"line {ex.Start.Line}: {ex.Message}");
        }
    }

    // ---- writing ----------------------------------------------------------------------------------

    private static void Emit(IEmitter emitter, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                emitter.Emit(new MappingStart(null, null, isImplicit: true, MappingStyle.Block));
                foreach (var (name, value) in o)
                {
                    // A KEY needs the same quoting guard a value does. `on` matches every
                    // identifier pattern and is the boolean true in YAML 1.1 — that is how the
                    // hand-authored Budget Planner lost the trigger from all six of its automations.
                    emitter.Emit(Scalar(name, alwaysQuoteIfAmbiguous: true));
                    Emit(emitter, value);
                }
                emitter.Emit(new MappingEnd());
                return;

            case JsonArray a:
                emitter.Emit(new SequenceStart(null, null, isImplicit: true, SequenceStyle.Block));
                foreach (var item in a) Emit(emitter, item);
                emitter.Emit(new SequenceEnd());
                return;

            case null:
                emitter.Emit(new Scalar(null, null, "null", ScalarStyle.Plain, true, false));
                return;

            case JsonValue v:
                Emit(emitter, v);
                return;
        }
    }

    private static void Emit(IEmitter emitter, JsonValue value)
    {
        // GetValueKind rather than GetValue<JsonElement>: a JsonValue built from a CLR string is not
        // JsonElement-backed and throws, and half of these documents are built in code rather than
        // parsed.
        switch (value.GetValueKind())
        {
            case JsonValueKind.True:
            case JsonValueKind.False:
                emitter.Emit(Plain(value.GetValueKind() == JsonValueKind.True ? "true" : "false"));
                return;

            case JsonValueKind.Number:
                // VERBATIM. `ToJsonString` keeps 1.0 a decimal and 1 an integer; parsing to double
                // and re-rendering does not, and that difference changes DefinitionHash.
                emitter.Emit(Plain(value.ToJsonString()));
                return;

            default:
                emitter.Emit(Scalar(value.TryGetValue<string>(out var text) ? text : value.ToString(),
                    alwaysQuoteIfAmbiguous: true));
                return;
        }
    }

    private static Scalar Plain(string text) =>
        new(null, null, text, ScalarStyle.Plain, isPlainImplicit: true, isQuotedImplicit: false);

    /// <summary>
    /// Tokens YAML 1.1 resolves to something other than a string, and the shapes a plain scalar
    /// mangles.
    ///
    /// <para><c>on</c>/<c>off</c>/<c>yes</c>/<c>no</c> are the famous ones. The quiet one is
    /// whitespace: a plain scalar is STRIPPED on the way back in, so <c>unit:  yr</c> round-trips as
    /// <c>"yr"</c> and the space the author typed is gone without a word — which is exactly what
    /// three Budget Planner fields shipped with.</para>
    /// </summary>
    private static bool Ambiguous(string text) =>
        text.Length == 0
        || text != text.Trim()
        || text is "null" or "Null" or "NULL" or "~"
        || bool.TryParse(text, out _)
        || text is "y" or "Y" or "n" or "N"
        || string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase)
        || string.Equals(text, "no", StringComparison.OrdinalIgnoreCase)
        || string.Equals(text, "on", StringComparison.OrdinalIgnoreCase)
        || string.Equals(text, "off", StringComparison.OrdinalIgnoreCase)
        || double.TryParse(text, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out _);

    private static Scalar Scalar(string text, bool alwaysQuoteIfAmbiguous)
    {
        // Single-quoted rather than double: no escape processing, so a Windows path or a `{{token}}`
        // survives unchanged and the file stays readable.
        var style = alwaysQuoteIfAmbiguous && Ambiguous(text) ? ScalarStyle.SingleQuoted : ScalarStyle.Any;
        return new Scalar(null, null, text, style, isPlainImplicit: true, isQuotedImplicit: false);
    }

    // ---- reading ----------------------------------------------------------------------------------

    private static JsonNode? Convert(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode map:
                var o = new JsonObject();
                foreach (var (key, value) in map.Children)
                    o[Scalar(key)] = Convert(value);
                return o;

            case YamlSequenceNode sequence:
                var a = new JsonArray();
                foreach (var item in sequence.Children) a.Add(Convert(item));
                return a;

            case YamlScalarNode scalar:
                return Value(scalar);

            default:
                return null;
        }
    }

    private static string Scalar(YamlNode node) => node is YamlScalarNode s ? s.Value ?? "" : node.ToString();

    /// <summary>
    /// A scalar back to JSON, resolving the plain forms and nothing else.
    ///
    /// <para>A QUOTED scalar is always a string — that is what the quotes were for, and honouring
    /// them is what makes <c>version: '2.0'</c> survive as text rather than becoming the number
    /// 2. Only an unquoted scalar is resolved, and only to the YAML 1.2 core types: this reader does
    /// not turn a bare <c>on</c> into <c>true</c>, because the writer never produces one and a
    /// hand-written one almost certainly means the word.</para>
    /// </summary>
    private static JsonNode? Value(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? "";
        if (scalar.Style is not (ScalarStyle.Plain or ScalarStyle.Any)) return JsonValue.Create(text);

        if (text is "null" or "Null" or "NULL" or "~" or "") return null;
        if (text is "true" or "True" or "TRUE") return JsonValue.Create(true);
        if (text is "false" or "False" or "FALSE") return JsonValue.Create(false);

        if (long.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var integer)
            && integer.ToString(System.Globalization.CultureInfo.InvariantCulture) == text)
            return JsonValue.Create(integer);

        if (decimal.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number)
            && JsonNode.Parse(text) is JsonValue parsed)
            return parsed;

        return JsonValue.Create(text);
    }

    /// <summary>UTF-8 without a byte-order mark — the BOM would change the bytes a hash covers.</summary>
    public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
