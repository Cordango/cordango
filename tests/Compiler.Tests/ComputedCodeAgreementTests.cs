// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Cord;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

/// <summary>
/// One question, two layers, one answer: is this literal a code the field can hold?
///
/// <para>The check itself lives in the parser, but the parser cannot know WHICH codes a field offers,
/// so both semantic layers answer that for it — the Gate from the lowered document, Cord from its own
/// model. They cannot share the lookup, because they read different shapes, and that is exactly why
/// this file exists. An authoring layer that accepts what the gate then refuses is worse than one that
/// checks nothing: the author has already moved on by the time anybody disagrees with them.</para>
///
/// <para>So these assert the VERDICTS match, not the wording. Each case is run through both and both
/// must reach the same conclusion.</para>
/// </summary>
public class ComputedCodeAgreementTests
{
    private static readonly CordOption[] TaxClasses =
    [
        new("1", "I"), new("2", "II"), new("3", "III"),
        new("4", "IV"), new("5", "V"), new("6", "VI"),
    ];

    private static CordApp App(string expr, params CordField[] extra) => new(
        Key: "payroll",
        Name: "Payroll",
        Version: "1.0.0",
        Entities:
        [
            new CordEntity("employer", "Employer", DisplayField: "name", Fields:
            [
                new CordField("name", "Name", "text"),
                new CordField("sector", "Sector", "select",
                    Options: [new CordOption("public", "Public"), new CordOption("private", "Private")]),
            ]),
            new CordEntity("calculation", "Calculation", DisplayField: "label", Fields:
            [
                new CordField("label", "Label", "text"),
                new CordField("note", "Note", "text"),
                new CordField("gross", "Gross", "money"),
                new CordField("employer", "Employer", "reference", TargetEntity: "employer"),
                new CordField("tax_class", "Steuerklasse", "select", Options: TaxClasses),
                .. extra,
                new CordField("answer", "Answer", "money", Calc: new CordExpr(expr)),
            ]),
        ]);

    private static CordApp Governed(string expr) => new(
        Key: "payroll",
        Name: "Payroll",
        Version: "1.0.0",
        Entities:
        [
            new CordEntity("claim", "Claim", DisplayField: "title", Fields:
            [
                new CordField("title", "Title", "text"),
                new CordField("gross", "Gross", "money"),
                new CordField("stage", "Stage", "select", Role: "status"),
                new CordField("answer", "Answer", "money", Calc: new CordExpr(expr)),
            ]),
        ],
        Processes:
        [
            new CordProcess("review", "claim", "stage", "draft",
                States:
                [
                    new CordState("draft", "Draft"),
                    new CordState("submitted", "Submitted"),
                    new CordState("approved", "Approved", Terminal: true),
                ],
                Transitions:
                [
                    new CordTransition("submit", "Submit", ["draft"], "submitted"),
                    new CordTransition("approve", "Approve", ["submitted"], "approved"),
                ]),
        ]);

    private const string Marker = "is not an option of";

    private static void Agree(CordApp app, bool refused)
    {
        var cord = CordCheck.Errors(app).Select(e => e.Message).ToList();
        var gate = Gate.SemanticErrors(CordLower.Lower(app));
        Assert.True(refused == cord.Any(e => e.Contains(Marker, StringComparison.Ordinal)),
            $"Cord said: {(cord.Count == 0 ? "nothing" : string.Join(" | ", cord))}");
        Assert.True(refused == gate.Any(e => e.Contains(Marker, StringComparison.Ordinal)),
            $"Gate said: {(gate.Count == 0 ? "nothing" : string.Join(" | ", gate))}");
    }

    [Fact]
    public void Both_refuse_a_code_no_option_offers() =>
        Agree(App("if(tax_class == 'klasse1', gross, 0)"), refused: true);

    [Fact]
    public void Both_accept_a_code_an_option_offers() =>
        Agree(App("if(tax_class == '3', gross, 0)"), refused: false);

    [Fact]
    public void Both_check_the_literal_on_the_left() =>
        Agree(App("if('klasse1' == tax_class, gross, 0)"), refused: true);

    [Fact]
    public void Both_check_under_not_equals() =>
        Agree(App("if(tax_class != 'klasse1', gross, 0)"), refused: true);

    [Fact]
    public void Both_leave_a_free_text_field_alone() =>
        Agree(App("if(note == 'anything at all', gross, 0)"), refused: false);

    [Fact]
    public void Both_leave_two_fields_compared_to_each_other_alone() =>
        Agree(App("if(note == label, gross, 0)"), refused: false);

    [Fact]
    public void Both_refuse_a_bad_code_across_a_hop() =>
        Agree(App("if(employer.sector == 'privat', gross, 0)"), refused: true);

    [Fact]
    public void Both_accept_a_good_code_across_a_hop() =>
        Agree(App("if(employer.sector == 'private', gross, 0)"), refused: false);

    [Fact]
    public void Both_refuse_a_state_the_process_does_not_have() =>
        Agree(Governed("if(stage == 'aproved', gross, 0)"), refused: true);

    [Fact]
    public void Both_accept_a_state_the_process_does_have() =>
        Agree(Governed("if(stage == 'approved', gross, 0)"), refused: false);
}
