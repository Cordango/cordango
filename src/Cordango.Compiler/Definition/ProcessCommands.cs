// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Definition;

/// <summary>
/// The one place that knows what a transition's automatic button is called.
///
/// <para><b>Why it had to become shared.</b> Every process transition gets an invokable command
/// whether or not it names one — the compiler builds it so that every legal state move has a button.
/// But the compiler runs AFTER the gate, and the gate validates the DEFINITION, so a document
/// referring to that command was rejected before the thing it referred to had been created:</para>
///
/// <code>
/// Gate.Validate(definition)   ← "action command 'task_complete' is not a command on 'task'"
/// DesignDefaults.Apply
/// Gate.Validate(definition)
/// AppCompiler.Compile         ← task_complete is created here, too late to be referenced
/// </code>
///
/// <para>Found by a live agent building a task app: it wanted a Done button on a card, could not bind
/// one to the lifecycle's own transition, and authored a duplicate command that did the same thing —
/// leaving two buttons side by side on the record page. It reported the duplication as a cord gap,
/// and it was right.</para>
///
/// <para><b>The rule lives here rather than in either caller</b> because two hand-written copies of a
/// naming convention is the same defect class as two hand-written copies of an enum: they agree until
/// one changes. The gate uses it to know the command will exist; the compiler uses it to create the
/// command. One string, one place.</para>
/// </summary>
public static class ProcessCommands
{
    /// <summary>
    /// The command key synthesized for a transition that names none.
    ///
    /// <para>Entity-qualified because command keys are unique per entity, not globally: two entities
    /// may each have an <c>approve</c> transition, and <c>claim_approve</c> and
    /// <c>invoice_approve</c> are different buttons.</para>
    /// </summary>
    public static string SynthesizedKey(string entity, string transitionKey) =>
        entity + "_" + transitionKey;

    /// <summary>
    /// Whether this transition gets a synthesized command — i.e. it does not name one itself.
    ///
    /// <para>Stated as a predicate rather than left to each caller's null check, because "authored
    /// commands always win" is the rule both sides have to agree on, and it is one line to get
    /// subtly wrong in two places.</para>
    /// </summary>
    public static bool IsSynthesized(string? namedCommand) => string.IsNullOrEmpty(namedCommand);
}
