// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.NodeVue.Emit;

/// <summary>
/// The figures a record works out for itself.
///
/// <para><b>Why this emits a call rather than arithmetic.</b> The .NET target compiles every
/// expression down into C#, because it has to: there is no expression evaluator in a generated
/// .NET application, and there should not be one. This runtime already carries the parser and the
/// evaluator — the same pair the hosted platform runs, pinned against the decision fixtures in
/// <c>tests/fixtures/computed/</c> that every implementation's suite runs. Emitting a fourth
/// implementation of three-valued unknown, of what <c>if</c> does to an unknown test, and of what
/// <c>0.1 + 0.2</c> means to an invoice would be four chances to disagree with the other three, and
/// the disagreement would be silent.</para>
///
/// <para>So the expression travels as the definition wrote it and is parsed once when the
/// application starts. What comes out is still a function you can read, put a breakpoint in and
/// replace: the expression is beside it in a comment, the order the figures are worked out in is
/// explicit, and nothing about it is hidden behind a framework.</para>
///
/// <para><b>What this does NOT write.</b> A rollup — a figure counted from OTHER records — and a
/// series that reads the row before it. Both need the recompute cascade, which is a different piece
/// of machinery from an expression over one record. The generator reports them rather than emitting
/// a column that stays empty and looks like a data problem.</para>
/// </summary>
public static class ComputedEmitter
{
    /// <summary>Fields this emitter can write: a computed expression over the record's OWN
    /// fields.</summary>
    public static IEnumerable<FieldModel> Fields(EntityModel entity) =>
        entity.AuthoredFields.Where(IsLocalExpression);

    public static bool Has(EntityModel entity) => Fields(entity).Any();

    public static string FunctionName(EntityModel entity) =>
        "compute" + TypeScript.TypeName(entity.Key);

    /// <summary>A field whose <c>computed</c> is an expression this target can work out here and
    /// now: no rollup, no window, no <c>prev()</c>, and nothing reached across a reference.</summary>
    public static bool IsLocalExpression(FieldModel field)
    {
        if (field.Computed is not { } computed) return false;
        if (computed["rollup"] is not null) return false;
        if (computed["window"] is not null || computed["match"] is not null) return false;
        if (computed["series"] is not null) return false;

        var expression = Expression(field);
        if (expression is null) return false;

        // `prev(` reads the row before this one, which is a series and needs an order to exist.
        if (expression.Contains("prev(", StringComparison.Ordinal)) return false;

        // A hop reads a field off a referenced record, which needs a second read the evaluator
        // cannot do from the row alone. Detected here so the generator can REPORT it rather than
        // emit an expression that quietly answers blank.
        return !ReadsAcrossAReference(expression);
    }

    /// <summary>The expression text, whichever of the two spellings the definition used.</summary>
    public static string? Expression(FieldModel field)
    {
        var computed = field.Computed;
        if (computed is null) return null;

        var expression = AppModel.Str(computed["expr"]) ?? AppModel.Str(computed["expression"]);
        return string.IsNullOrWhiteSpace(expression) ? null : expression;
    }

    /// <summary>
    /// Does this expression reach across a reference?
    ///
    /// <para>A dot between two identifier characters, and not one inside a number. Written by hand
    /// rather than by parsing because the generator has no parser for this grammar — the runtime
    /// does — and the cost of being wrong in the cautious direction is a diagnostic saying a field
    /// is not generated when it could have been, which somebody can read and act on.</para>
    /// </summary>
    private static bool ReadsAcrossAReference(string expression)
    {
        for (var i = 1; i < expression.Length - 1; i++)
        {
            if (expression[i] != '.') continue;

            var before = expression[i - 1];
            var after = expression[i + 1];

            // A decimal point has digits on both sides; a hop has identifier characters.
            if (char.IsDigit(before) && char.IsDigit(after)) continue;

            if ((char.IsLetter(before) || before == '_') && (char.IsLetter(after) || after == '_'))
                return true;
        }

        return false;
    }

    public static GeneratedFile? Emit(AppModel app, EntityModel entity)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(entity);

        var fields = Ordered(entity).ToList();
        if (fields.Count == 0) return null;

        var source = new TsSource();

        source.Lines(Header.For(
            $"The figures a {entity.Label} works out for itself.",
            "Each one is the expression the definition wrote, parsed once when this application starts\n"
            + "and worked out by the runtime's own evaluator — the same parser and the same arithmetic the\n"
            + "hosted platform runs, against one set of decision fixtures both suites check.\n"
            + "\n"
            + "The order below is the dependency order: a figure that reads another figure is worked out\n"
            + "after it."));

        source.Line();
        source.Line("import { computeAll, computedField } from \"@cordango/standalone\";");
        source.Line();
        source.Line($"import {{ {TypeScript.Identifier(entity.Key)}Descriptor, type {entity.TypeName} }} from \"../entities.js\";");
        source.Line();

        source.Lines(TypeScript.Doc(
            "Which day begins a week here. The application's own setting, never the machine's — a "
            + "figure that differs by server locale is a figure nobody can reconcile.", 0));
        source.Line($"const options = {{ weekStartsMonday: {TypeScript.Bool(app.WeekStartsMonday)} }};");
        source.Line();

        foreach (var field in fields)
        {
            var expression = Expression(field)!;

            source.Line($"// {field.Label}");
            source.Line($"const {TypeScript.Identifier(field.Key)} = computedField<{entity.TypeName}>(");
            source.Indent();
            source.Line($"{TypeScript.Identifier(entity.Key)}Descriptor,");
            source.Line($"{TypeScript.Literal(field.Key)},");
            source.Line($"{TypeScript.Literal(expression)},");
            source.Line("options,");
            source.Outdent();
            source.Line(");");
            source.Line();
        }

        source.Lines(TypeScript.Doc(
            $"Work out every figure a {entity.Label} derives from its own fields.\n"
            + "\n"
            + "Called before a create and before an update, so the stored value and the expression can "
            + "never disagree. A figure is never accepted from the caller: a client that could send one "
            + "could send a total that does not match its own lines.", 0));

        source.Line($"export const {FunctionName(entity)} = computeAll<{entity.TypeName}>([");
        source.Indent();
        foreach (var field in fields) source.Line($"{TypeScript.Identifier(field.Key)},");
        source.Outdent();
        source.Line("]);");

        return new GeneratedFile($"api/src/computed/{entity.Key}.ts", source.ToString());
    }

    /// <summary>
    /// The fields in the order they have to be worked out.
    ///
    /// <para>A figure that reads another figure has to come after it. Resolved by walking the
    /// dependencies rather than by trusting the definition's order, because the definition lists
    /// fields in the order somebody found readable and nothing makes that a topological one.</para>
    ///
    /// <para>A cycle cannot be ordered and is left in declaration order; the generator reports it
    /// separately, because the honest news is "these count each other in a circle" rather than a
    /// silently arbitrary answer.</para>
    /// </summary>
    public static IEnumerable<FieldModel> Ordered(EntityModel entity)
    {
        var fields = Fields(entity).ToList();
        var byKey = fields.ToDictionary(f => f.Key, StringComparer.Ordinal);

        var emitted = new List<FieldModel>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in fields) Visit(field);

        return emitted;

        void Visit(FieldModel field)
        {
            if (placed.Contains(field.Key)) return;

            // A cycle: stop rather than recurse. The generator reports it; emitting something is
            // better than a stack overflow inside the build.
            if (!visiting.Add(field.Key)) return;

            foreach (var dependency in Identifiers(Expression(field) ?? ""))
                if (byKey.TryGetValue(dependency, out var other)
                    && !string.Equals(other.Key, field.Key, StringComparison.Ordinal))
                    Visit(other);

            visiting.Remove(field.Key);
            if (placed.Add(field.Key)) emitted.Add(field);
        }
    }

    /// <summary>
    /// The bare identifiers an expression mentions.
    ///
    /// <para>Over-inclusive on purpose: a function name reads as an identifier here, and a field
    /// that happens to be called <c>round</c> would be treated as a dependency of anything that
    /// rounds. The cost of that is an ordering constraint that was not needed; the cost of missing
    /// a real dependency is a figure worked out from a stale one.</para>
    /// </summary>
    private static IEnumerable<string> Identifiers(string expression)
    {
        var start = -1;

        for (var i = 0; i <= expression.Length; i++)
        {
            var isPart = i < expression.Length
                && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_');

            if (isPart)
            {
                if (start < 0) start = i;
                continue;
            }

            if (start >= 0)
            {
                var word = expression[start..i];
                if (!char.IsDigit(word[0])) yield return word;
                start = -1;
            }
        }
    }

    /// <summary>A computed field this target does not write, and the sentence saying why. Null when
    /// the field is one it does write, or is not computed at all.</summary>
    public static string? WhyNotEmitted(FieldModel field)
    {
        if (field.Computed is not { } computed) return null;
        if (IsLocalExpression(field)) return null;

        if (computed["rollup"] is not null)
            return "is a rollup — a figure counted from other records. That needs the recompute "
                + "cascade, which works out which rows a write disturbs and in what order, and this "
                + "target does not build it yet";

        if (computed["window"] is not null || computed["match"] is not null)
            return "is aggregated over a window or across siblings, which needs the recompute "
                + "cascade this target does not build yet";

        var expression = Expression(field);

        if (expression is null)
            return "has a `computed` block this generator does not recognise";

        if (expression.Contains("prev(", StringComparison.Ordinal))
            return "reads the row before it, which needs an ordered series and the recompute "
                + "cascade that keeps one right";

        if (ReadsAcrossAReference(expression))
            return "reads a field across a reference, which this target does not resolve yet";

        return "is computed from something this generator cannot work out here";
    }

    /// <summary>Whether this entity's computed fields refer to each other in a circle. Nothing in
    /// any application anybody has written does this; it is checked because the emitted code would
    /// be the thing that failed, at run time, in production.</summary>
    public static bool IsCyclic(EntityModel entity)
    {
        var fields = Fields(entity).ToList();
        var byKey = fields.ToDictionary(f => f.Key, StringComparer.Ordinal);

        var settled = new HashSet<string>(StringComparer.Ordinal);
        var stack = new HashSet<string>(StringComparer.Ordinal);

        return fields.Any(field => Cyclic(field));

        bool Cyclic(FieldModel field)
        {
            if (settled.Contains(field.Key)) return false;
            if (!stack.Add(field.Key)) return true;

            foreach (var dependency in Identifiers(Expression(field) ?? ""))
                if (byKey.TryGetValue(dependency, out var other)
                    && !string.Equals(other.Key, field.Key, StringComparison.Ordinal)
                    && Cyclic(other))
                    return true;

            stack.Remove(field.Key);
            settled.Add(field.Key);
            return false;
        }
    }
}
