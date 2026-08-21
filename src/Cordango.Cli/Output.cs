// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordango.Cli;

/// <summary>
/// The one place a command says anything.
///
/// <para><b>Two audiences, one call site.</b> A command builds a result object and hands it here;
/// this decides whether the person reading gets three lines or the agent gets JSON. Commands that
/// formatted their own output drifted apart in every CLI that has ever done it, and the drift always
/// shows up as `--json` missing a field the human output has.</para>
///
/// <para><b>JSON goes to stdout, diagnostics to stderr.</b> `cordango inspect --json | jq` has to work,
/// which means nothing may share stdout with the payload — not a progress line, not a warning.</para>
/// </summary>
public sealed class Output(bool json)
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public bool IsJson => json;

    /// <summary>The successful answer. <paramref name="human"/> runs only when not in JSON mode, so
    /// a command can build an expensive human rendering without paying for it in an agent loop.</summary>
    public int Ok(JsonObject payload, Action<TextWriter> human)
    {
        if (json)
        {
            payload["ok"] = true;
            Console.Out.WriteLine(payload.ToJsonString(Pretty));
        }
        else
        {
            human(Console.Out);
        }
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The command ran and the answer is no.
    ///
    /// <para>Errors are a LIST, always, even when there is one. A caller that has to handle both a
    /// string and an array gets it wrong for the single-error case exactly once and then carries the
    /// bug forever.</para>
    /// </summary>
    public int Fail(string summary, IEnumerable<string> errors, JsonObject? payload = null,
        int code = ExitCodes.Failed)
    {
        var list = errors.ToList();

        if (json)
        {
            var doc = payload ?? [];
            doc["ok"] = false;
            doc["summary"] = summary;
            doc["errors"] = new JsonArray([.. list.Select(e => (JsonNode)e!)]);
            Console.Out.WriteLine(doc.ToJsonString(Pretty));
        }
        else
        {
            Console.Error.WriteLine(summary);
            foreach (var error in list) Console.Error.WriteLine("  " + error);
        }

        return code;
    }

    /// <summary>A usage mistake — reported the same way in both modes because an agent that called
    /// the command wrong needs to read the correction as much as a person does.</summary>
    public int Usage(string message) => Fail(message, [], code: ExitCodes.Usage);

    /// <summary>Something worth saying that is not the answer. Never stdout — see the class note.</summary>
    public void Note(string message)
    {
        if (!json) Console.Error.WriteLine(message);
    }
}
