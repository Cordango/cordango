// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>
/// Rebuilding a draft from the operations that made it.
///
/// <para><b>Why a journal and not the last document.</b> The journal is the accepted semantic history:
/// it preserves aggregate boundaries and names the first unreadable change. Re-importing a definition
/// is instead the sharing/bootstrap path. Shapes Cord lowered raise back into semantics; shapes outside
/// its vocabulary remain lossless and are editable only by the human advanced editor's validated
/// replacement entry â€” never by a model tool.</para>
///
/// <para>The operations are the semantic history and they are already stored: <c>ProposedPatches</c> on
/// each proposal row holds the exact <c>ops</c> array the model sent. Replaying them reconstructs the
/// draft as CORD understood it — entities as entities, screens as screens — with nothing in the raw
/// overlay that was not there at the start.</para>
///
/// <para>This is not a <c>.cord</c> source format and must not become one. It is a recovery mechanism
/// over history the platform already keeps.</para>
/// </summary>
public static class CordJournal
{
    private const string ImportedDefinition = "$importedDefinition";
    private const string ReplacedDefinition = "$replacedDefinition";

    /// <summary>
    /// The one bootstrap entry that is not an authored operation batch: the App Definition an older
    /// app arrived with. It is internal journal vocabulary, never present in a model tool schema.
    ///
    /// <para>Fully expressible definitions do not need it — their canonical operations are stored
    /// instead. This exists for the honest remainder: a definition Cord can preserve losslessly but
    /// cannot yet materialize completely as <c>.cord</c> files. Keeping that starting document lets
    /// supported aggregates be edited without pretending the unsupported pieces vanished.</para>
    /// </summary>
    public static JsonObject Imported(JsonNode definition) => new()
    {
        [ImportedDefinition] = definition.DeepClone(),
    };

    /// <summary>
    /// A human-authored replacement of the imported remainder. This is internal journal vocabulary,
    /// never a model operation: the manual advanced editor starts from the currently lowered complete
    /// definition, changes only the explicitly presented preserved fragments, and records that whole
    /// checked definition so replay remains lossless. Later semantic operations continue on top.
    /// </summary>
    public static JsonObject Replaced(JsonNode definition) => new()
    {
        [ReplacedDefinition] = definition.DeepClone(),
    };

    /// <summary>
    /// Replays operation batches, in order, onto a starting identity.
    ///
    /// <para><b>Only COHERENT batches may be passed in.</b> That filter belongs to the caller, which is
    /// the only party that knows which submissions the validator accepted — Cord cannot know, and rule 0
    /// says it must not be able to ask. A rejected batch is kept in the record for diagnostics and token
    /// accounting; replaying one would rebuild a draft the session had already refused.</para>
    ///
    /// <para>A batch that fails to replay STOPS the replay and returns everything before it rather than
    /// skipping it. Order is the whole meaning of a journal: an <c>upsert_field</c> whose entity arrived
    /// in a batch that did not apply would land on a draft that never existed, and continuing past a gap
    /// silently produces an application nobody authored. Stopping is recoverable — the model carries on
    /// from a real state — while skipping is not detectable at all.</para>
    /// </summary>
    /// <param name="identity">The draft the session started from: key, name, version, and nothing else.</param>
    /// <param name="batches">Each element is one <c>ops</c> array, oldest first.</param>
    public static CordReplay Replay(CordApp identity, IEnumerable<JsonNode?> batches)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(batches);

        var draft = identity;
        var applied = 0;

        foreach (var batch in batches)
        {
            if (batch is JsonObject marker && marker[ImportedDefinition] is { } definition)
            {
                if (applied != 0)
                    return new CordReplay(draft, applied,
                    [new CordError(CordErrorCode.MalformedOperation, "journal",
                        "an imported definition may only be the first baseline entry")]);

                draft = CordImport.Import(definition);
                applied++;
                continue;
            }

            if (batch is JsonObject replacement && replacement[ReplacedDefinition] is { } replaced)
            {
                draft = CordImport.Import(replaced);
                applied++;
                continue;
            }

            // No `allowed` filter: the concern that produced each batch is not recorded, and it does
            // not need to be. These operations were already accepted through a concern-scoped tool once;
            // re-imposing the scope here would only be able to reject history that is known good.
            var prepared = CordTransaction.Prepare(draft, batch);
            if (!prepared.Ok) return new CordReplay(draft, applied, prepared.Errors);

            draft = prepared.Next;
            applied++;
        }

        return new CordReplay(draft, applied, []);
    }
}

/// <param name="Draft">The rebuilt draft — everything that replayed cleanly, and nothing after the
/// first batch that did not.</param>
/// <param name="Applied">How many batches went in. Compared against how many were offered, this is how
/// a caller finds out the replay stopped short.</param>
/// <param name="Errors">Why it stopped, empty when it did not.</param>
public sealed record CordReplay(CordApp Draft, int Applied, IReadOnlyList<CordError> Errors)
{
    public bool Complete => Errors.Count == 0;
}

/// <summary>
/// What a stored journal turned out to contain.
/// </summary>
/// <param name="Batches">Every operation array that could be read, oldest first — including the ones
/// before a gap, because continuing from them is still the right thing to do.</param>
/// <param name="Complete">False when a coherent row could not be read at all: unparseable, truncated
/// past the storage limit, or not an operation array. <b>A caller must never publish a replay of an
/// incomplete journal</b> — the prefix may well validate, and shipping it would silently drop every
/// change after the gap.</param>
/// <param name="Error">What was wrong, for the log and for the person reading it later.</param>
public sealed record CordJournalRead(IReadOnlyList<JsonNode> Batches, bool Complete, string? Error);
