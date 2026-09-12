// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.Node.Emit;

/// <summary>
/// The fields the runtime fills in rather than the person.
///
/// <para>The compiler marks them: <c>submitted_by</c> and <c>owner</c> are whoever is writing,
/// <c>submitted_at</c> and <c>logged_at</c> are when. They are hidden from every form, so if
/// nothing filled them they would simply be null — and "who submitted this" being null on every row
/// is the sort of emptiness that looks like a data problem rather than a missing feature.</para>
///
/// <para>A hook rather than a default on the column, because the answer depends on the request.
/// <c>??=</c> throughout: a value the caller legitimately supplied — a seed load, an import, a
/// record created on somebody else's behalf — is kept rather than overwritten by whoever happened
/// to be holding the session.</para>
/// </summary>
public static class AutoFieldsEmitter
{
    /// <summary>The kinds of automatic field this target fills. <c>publicToken</c> is deliberately
    /// absent: it belongs to the forms archetype, whose public endpoints this target does not serve
    /// yet, and minting an address for a form nothing can reach would be worse than not minting
    /// one.</summary>
    private static readonly IReadOnlySet<string> Kinds =
        new HashSet<string>(StringComparer.Ordinal) { "currentUser", "currentTime" };

    public static IEnumerable<FieldModel> Fields(EntityModel entity) =>
        entity.AuthoredFields.Where(f => AppModel.Str(f.Json["auto"]) is { } kind && Kinds.Contains(kind));

    public static bool Has(EntityModel entity) => Fields(entity).Any();

    public static string FunctionName(EntityModel entity) =>
        "fill" + TypeScript.TypeName(entity.Key);

    public static GeneratedFile? Emit(EntityModel entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var auto = Fields(entity).ToList();
        if (auto.Count == 0) return null;

        var source = new TsSource();

        source.Lines(Header.For(
            $"The {entity.Label} fields the runtime owns rather than the person.",
            "Filled on the way in, before the record is written. `??=` throughout: a value that was\n"
            + "genuinely supplied — by a seed load, an import, or a record created on somebody else's\n"
            + "behalf — is kept rather than overwritten by whoever happens to be holding the session."));

        // PlainDate only when a date field actually needs it. The generated project compiles under
        // `noUnusedLocals`, so an import nothing uses is not untidiness — it is a build failure in
        // the application this generator just claimed to have produced.
        var needsDate = auto.Any(f =>
            AppModel.Str(f.Json["auto"]) == "currentTime" && f.Type == "date");

        source.Line();
        source.Line(needsDate
            ? "import { PlainDate, type RecordContext } from \"@cordango/standalone\";"
            : "import type { RecordContext } from \"@cordango/standalone\";");
        source.Line();
        source.Line($"import type {{ {entity.TypeName} }} from \"../entities.js\";");
        source.Line();

        source.Lines(TypeScript.Doc(
            $"Fill in what a new {entity.Label} does not get from the form.", 0));

        source.Open(
            $"export function {FunctionName(entity)}"
            + $"(record: {entity.TypeName}, context: RecordContext): void");

        foreach (var field in auto)
        {
            var kind = AppModel.Str(field.Json["auto"]);

            source.Line(kind == "currentUser"
                ? $"// {field.Label}: whoever is writing."
                : $"// {field.Label}: when they wrote it.");

            var expression = kind switch
            {
                "currentUser" => "context.user.personId ?? null",
                // A date field wants the day, not the instant. Taken from the same clock as the
                // tracking stamp, so a record cannot be created on one day and stamped on another.
                _ when field.Type == "date" =>
                    "PlainDate.parse(context.clock.utcNow.toString().slice(0, 10))",
                _ => "context.clock.utcNow",
            };

            source.Line($"record.{TypeScript.Key(field.Key)} ??= {expression};");
        }

        source.Close();

        return new GeneratedFile($"api/src/auto/{entity.Key}.ts", source.ToString());
    }
}
