// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;

namespace Cordango.Definition;

/// <summary>The result types supported by a computed expression.</summary>
public enum ComputedValueKind
{
    Number,
    Boolean,
    Date,

    /// <summary>
    /// A coded string: the value of a <c>select</c>, or a <c>text</c> field read as itself.
    ///
    /// <para>Compared, never combined. There is no concatenation, no case folding and no ordering —
    /// <c>==</c> and <c>!=</c> and nothing else, because everything else invites an expression to
    /// BUILD a label, and a label built in an expression is one nobody can translate.</para>
    ///
    /// <para>It exists because a closed set is stored as a code (see the field-typing decision: a
    /// closed set becomes a coded select, and storage stays string-coded on purpose). Without this
    /// kind, nothing computed could branch on one — a tax table keyed by Bundesland, a rate keyed by
    /// plan, a fee keyed by country were all simply unwritable, and the author's only move was to
    /// type the answer in by hand on every row.</para>
    /// </summary>
    Text,
}

/// <summary>
/// What an application's own code offers an expression: the kinds it takes, and the kind it answers.
///
/// <para>Kinds, not types. The compiler has no idea what C# is, and must not: this is the same
/// question it asks about a field, so a custom call type-checks by the rules already in force
/// rather than by a second set that happens to agree today.</para>
/// </summary>
public sealed record CustomSignature(
    IReadOnlyList<ComputedValueKind> Parameters, ComputedValueKind Returns);

/// <summary>The statically checked shape of an expression, including every field it reads.</summary>
public sealed record ComputedExprValidation(
    string? Error,
    ComputedValueKind? ResultKind,
    IReadOnlySet<string> Identifiers);

/// <summary>
/// A small, typed expression language: numeric arithmetic, comparisons that return booleans, boolean
/// logic, date durations, <c>pow</c>, the two-value bounds <c>min</c>/<c>max</c>, coded text compared
/// for equality, and one branch — <c>if</c>.
///
/// <para><b>This is the language, not an engine for it.</b> What lives here is the grammar, the
/// parser, the typed AST and the static checking — everything needed to tell an author their
/// expression is wrong, and everything needed to TRANSLATE one. There is deliberately no evaluator:
/// working a figure out over a record is the platform runtime's job, and it does it with its own
/// code over the tree <see cref="Parse"/> returns.</para>
///
/// <para>Two consumers, one grammar. The standalone generator turns each expression into a method
/// compiled into the application it emits; the platform interprets. They must produce the same
/// figures, and that is pinned as data in <c>tests/fixtures/computed/</c> rather than trusted —
/// each side asserted against the same hand-written cases by its own suite.</para>
///
/// <para>Expressions are data, never passed to a language evaluator.</para>
/// </summary>
public static class ComputedExpr
{
    public static readonly IReadOnlySet<string> DurationFuncs =
        new HashSet<string>(StringComparer.Ordinal) { "minutes_between", "hours_between", "days_between" };

    /// <summary>
    /// The parts of one date, each a number: <c>weekday(shift_date)</c>, <c>month_of(due)</c>.
    ///
    /// <para><b>Reading a stored date, never the clock.</b> There is deliberately no
    /// <c>today()</c> or <c>now()</c> here. A computed field is a pure function of its record,
    /// worked out when the row is written and stored beside it — so an expression over the current
    /// time would be right on the day it saved and quietly wrong every day after, with nothing on
    /// the surface to say so. A duration to today is a question for a filter or a report, which
    /// both run when somebody looks.</para>
    ///
    /// <para>These are what group a list by something the date IMPLIES rather than states: the week
    /// a shift falls in, the month a claim was filed. Without them a definition has to store the
    /// week number as a field somebody types, which is a fact the date already knows.</para>
    ///
    /// <para><c>weekday</c> and <c>week_of_year</c> depend on which day starts a week, which is the
    /// app's <c>weekStart</c> — see <see cref="WeekDependentFuncs"/>. The rest are the same
    /// everywhere.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> DatePartFuncs =
        new HashSet<string>(StringComparer.Ordinal)
        { "weekday", "week_of_year", "month_of", "day_of_month", "day_of_year", "year_of", "hour_of" };

    /// <summary>
    /// The DATE a period containing this one begins or ends on: the Monday of a shift's week, the
    /// first of a claim's month.
    ///
    /// <para>These answer a date rather than a number, which is the whole point — a list grouped by
    /// <c>week_of_year</c> sorts correctly and reads as "week 36", while one grouped by
    /// <c>start_of_week</c> reads as the date the week began and needs no legend. The number is the
    /// better key and the date is the better label, so the language has both.</para>
    ///
    /// <para>A computed field holding one is typed <c>date</c>. That is the only place a computed
    /// field is not a number or a boolean, and it is why the gate had to learn a third result
    /// kind.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> DateBoundaryFuncs =
        new HashSet<string>(StringComparer.Ordinal)
        { "start_of_week", "end_of_week", "start_of_month", "end_of_month" };

    /// <summary>The date functions whose answer moves with the app's <c>weekStart</c>. Named so the
    /// emitter can pass the convention only where it changes the answer, rather than threading it
    /// through the ones that have a single answer everywhere.</summary>
    public static readonly IReadOnlySet<string> WeekDependentFuncs =
        new HashSet<string>(StringComparer.Ordinal)
        { "weekday", "week_of_year", "start_of_week", "end_of_week" };

    /// <summary>The one date part that needs a time of day to mean anything. A <c>date</c> column
    /// has none, so <c>hour_of</c> over one is always zero — an answer that looks computed and is
    /// not, which is the same trap <c>{{today-4h}}</c> already refuses.</summary>
    public const string HourFunc = "hour_of";

    /// <summary>
    /// The two-argument numeric functions. All three take arbitrary sub-expressions, unlike the
    /// duration functions which take bare date field names.
    ///
    /// <para><c>min</c> and <c>max</c> are how a row states a BOUND on one of its own figures — a
    /// usage charge capped at a plan's ceiling, a balance floored at zero. Booleans deliberately do
    /// not coerce to numbers, so the arithmetic dodge <c>(a &lt; b) * a + (a &gt;= b) * b</c> is
    /// rejected: without these a cap simply cannot be written, and the author's only remaining move
    /// is to type the clamped number by hand every month, which is exactly what a computed field
    /// exists to stop.</para>
    ///
    /// <para>They keep that job now that <see cref="ConditionalFunc"/> exists, and are still the
    /// better way to say it: <c>min(usage, plan.cap)</c> is a bound, while
    /// <c>if(usage &gt; plan.cap, plan.cap, usage)</c> is a bound spelled out three times.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> MathFuncs =
        new HashSet<string>(StringComparer.Ordinal) { "pow", "min", "max" };

    /// <summary>
    /// <c>if(test, when_true, when_false)</c> — the one branch in the language.
    ///
    /// <para><b>It was deliberately absent, and that decision is reversed here rather than
    /// forgotten.</b> <see cref="MathFuncs"/> gained <c>min</c>/<c>max</c> precisely because a CAP
    /// was the only branch anyone needed, and a bound reads better than a conditional. What broke
    /// the rule was a rate TABLE: a contribution ceiling that differs by Bundesland, a rate that
    /// differs by number of children, a bracket that differs by tax class. None of those is a bound,
    /// none is expressible as arithmetic over booleans while booleans stay uncoerced, and every one
    /// of them is ordinary in the kind of application this language exists for.</para>
    ///
    /// <para><b>The test is a boolean, and the two answers are the same kind as each other</b> —
    /// that kind is the result. So this is not a fourth numeric function: it serves dates and text as
    /// readily as numbers, and it is the only place a <see cref="ComputedValueKind"/> is decided by
    /// two sub-expressions agreeing rather than by the operator.</para>
    ///
    /// <para><b>Only the branch taken is worked out.</b> That is not an optimisation — it is what
    /// makes <c>if(months == 0, 0, costs / months)</c> mean what it reads as. Working out both sides
    /// would let the division's unknown spread into an answer the author had explicitly guarded
    /// against, which would make the guard worse than useless.</para>
    /// </summary>
    public const string ConditionalFunc = "if";

    /// <summary>
    /// <c>switch(field, key, value, key, value, …, default)</c> — a TABLE keyed on one field.
    ///
    /// <para><c>if</c> arrived for a rate table and is binary, so a table with sixteen rows became
    /// sixteen nested conditionals with a long tail of brackets. Worse than unreadable: the arms are
    /// keyed on codes, and a code that matches no option is a comparison that is false on every row
    /// that will ever exist. It reads as a table and behaves as its own default.</para>
    ///
    /// <para><b>The subject is a FIELD, not an expression.</b> Three reasons pointing the same way:
    /// the desugaring below repeats it once per arm, so it must be cheap; a table is keyed on
    /// something the record HAS; and only a field carries a set of options to check the keys against,
    /// which is what makes this safer than the chain it replaces rather than merely shorter.</para>
    ///
    /// <para>Keys are written out, never worked out — that is what makes them checkable, and what
    /// makes a repeated key an error rather than a row nobody can reach.</para>
    /// </summary>
    public const string SwitchFunc = "switch";

    /// <summary>
    /// <c>case(test, value, test, value, …, default)</c> — the first test that holds.
    ///
    /// <para><see cref="SwitchFunc"/>'s sibling for tables that are not keyed on equality: an income
    /// tax tariff is five zones over a threshold, and a bracket is not a code. The subject differs on
    /// every arm, so nothing can be checked against a set of options — this is the general form and
    /// <c>switch</c> is the safe one, which is why the language has both.</para>
    ///
    /// <para>A default is always required. Without one, a ladder no test matches would have no answer,
    /// and both alternatives are bad: unknown would make a table read as a missing figure, and falling
    /// to the first arm would be a guess.</para>
    /// </summary>
    public const string CaseFunc = "case";

    /// <summary>
    /// Whether a token names a FUNCTION rather than a field.
    ///
    /// <para>Three callers ask: the parser's own dispatch, and both identifier collectors. A name
    /// missing from either collector is reported as a field the row depends on, which then feeds
    /// cycle detection and hop discovery — so this is one list rather than three, because a function
    /// added to two of the three is a bug nobody sees.</para>
    /// </summary>
    public static bool IsFunctionName(string token) =>
        MathFuncs.Contains(token) || DurationFuncs.Contains(token)
        || DatePartFuncs.Contains(token) || DateBoundaryFuncs.Contains(token)
        || token == PrevFunc || token == ConditionalFunc
        || token == SwitchFunc || token == CaseFunc
        || IsCustomCall(token);

    /// <summary>
    /// What a call into the application's own code is spelled as: <c>custom.discount(total)</c>.
    ///
    /// <para><b>A prefix, so that this stays a STATIC question.</b> The two identifier collectors
    /// are context-free by design and have no registry to consult; if they had to know which custom
    /// functions an application declares, an unknown one would be collected as a FIELD and would
    /// quietly corrupt cycle detection and hop discovery. A name nobody declared is still a
    /// function by its shape, and saying so is validation's job rather than the tokenizer's.</para>
    ///
    /// <para>The tokenizer already folds one dot into a single token, so this costs nothing there.
    /// And because <see cref="IsFunctionName"/> is only ever asked about a token followed by
    /// <c>(</c>, a reference field genuinely called <c>custom</c> keeps working: <c>custom.owner</c>
    /// is a hop and <c>custom.owner(</c> is not something anybody can write.</para>
    /// </summary>
    public const string CustomPrefix = "custom.";

    public static bool IsCustomCall(string token) =>
        token.StartsWith(CustomPrefix, StringComparison.Ordinal) && token.Length > CustomPrefix.Length;

    /// <summary>The name an application declares, without the prefix an expression writes.</summary>
    public static string CustomName(string token) =>
        IsCustomCall(token) ? token[CustomPrefix.Length..] : token;

    /// <summary>
    /// <c>prev(field)</c> or <c>prev(field, seed)</c> — the value of <c>field</c> on the PREVIOUS row
    /// of an ordered series, and what to use when there is no previous row.
    ///
    /// <para>The recurrence Excel writes as <c>=B26+C24-C25</c>: this month's active tenants are last
    /// month's plus new minus churned. No expression over a single record can say it, which is why a
    /// budget planner could not be modelled at all — three fields had to be typed twenty-four times
    /// and a running cash balance was simply impossible (live 2026-08-05).</para>
    ///
    /// <para>The seed matters as much as the hop. A cash balance starts from the scenario's opening
    /// cash, exactly as a spreadsheet's first column reads a seed cell: <c>prev(cash_end,
    /// scenario.starting_cash) + net_cash_movement</c>. Without it, row one would silently start from
    /// zero and every figure after it would be wrong by the opening balance.</para>
    /// </summary>
    public const string PrevFunc = "prev";

    public static readonly IReadOnlySet<string> Keywords =
        new HashSet<string>(StringComparer.Ordinal) { "true", "false", "and", "or", "not" };

    /// <summary>Compatibility entry point for numeric-only callers.</summary>
    public static string? Error(string? expr, Func<string, string?> identError,
        Func<string, string?>? dateArgError = null) =>
        Validate(expr, _ => ComputedValueKind.Number, identError, dateArgError).Error;

    /// <summary>Parse, resolve identifiers and statically infer the expression result.</summary>
    /// <param name="prevArgError">Validates the field named inside <c>prev()</c>. Separate from
    /// <paramref name="identError"/> because a computed field referring to ITSELF is an error
    /// everywhere except here, where it is the entire point: <c>prev(cash_end)</c> on the field
    /// <c>cash_end</c> is a running balance, not a circular definition.</param>
    /// <param name="codeError">Validates a text LITERAL against the field it is compared with, given
    /// the field and the literal. The parser knows a select stores a code; only the caller knows WHICH
    /// codes, so the closed set is answered here rather than carried in the grammar.</param>
    /// <param name="datePartArgError">Validates one date PART against the field it reads, given the
    /// function name and the field. Separate from <paramref name="dateArgError"/> because the rule
    /// it carries depends on which part was asked for rather than on the field alone:
    /// <c>hour_of</c> needs a time of day and a <c>date</c> column has none.</param>
    public static ComputedExprValidation Validate(string? expr,
        Func<string, ComputedValueKind?> fieldKind,
        Func<string, string?>? identError = null,
        Func<string, string?>? dateArgError = null,
        Func<string, string?>? prevArgError = null,
        Func<string, string, string?>? datePartArgError = null,
        Func<string, string, string?>? codeError = null,
        Func<string, CustomSignature?>? customSignature = null)
    {
        var parser = new Parser(expr, fieldKind, identError ?? (_ => null), dateArgError ?? (_ => null),
            prevArgError ?? identError ?? (_ => null), datePartArgError ?? ((_, _) => null),
            codeError ?? ((_, _) => null), customSignature);
        var node = parser.Parse();
        return new ComputedExprValidation(parser.Error, node?.Kind, parser.Identifiers);
    }

    /// <summary>
    /// Field references this expression depends on WITHIN ITS OWN ROW.
    ///
    /// <para>Everything <see cref="Identifiers"/> returns except the field named inside a
    /// <c>prev()</c>. That one is read off the PREVIOUS row, so it is not a dependency of this row's
    /// computation — and treating it as one makes the commonest recurrence look like a cycle:
    /// <c>active_tenants = prev(active_tenants) + new - churned</c> depends on itself, the cycle guard
    /// blanks it, and the whole chain silently reads zero.</para>
    /// </summary>
    public static IReadOnlySet<string> LocalIdentifiers(string? expr)
    {
        var tokens = Tokenize(expr, out _);
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (!IsIdentifier(t) || Keywords.Contains(t)) continue;
            if (i + 1 < tokens.Count && tokens[i + 1] == "(" && IsFunctionName(t))
            {
                // Skip prev's FIRST argument — it belongs to the previous row.
                if (t == PrevFunc && i + 2 < tokens.Count && IsIdentifier(tokens[i + 2])) i += 2;
                continue;
            }
            result.Add(t);
        }
        return result;
    }

    /// <summary>Lexical field references for dependency ordering. Validation remains authoritative;
    /// this helper deliberately returns a best-effort set even for malformed input.</summary>
    public static IReadOnlySet<string> Identifiers(string? expr)
    {
        var tokens = Tokenize(expr, out _);
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (!IsIdentifier(t) || Keywords.Contains(t)) continue;
            if (i + 1 < tokens.Count && tokens[i + 1] == "(" && IsFunctionName(t)) continue;
            result.Add(t);
        }
        return result;
    }

    /// <summary>
    /// The parsed expression, for a caller that needs to TRANSLATE it rather than evaluate it.
    ///
    /// <para>The standalone generator turns each computed field into a C# method, so that a
    /// generated application computes a total with arithmetic a person can read and step through
    /// rather than by carrying an expression interpreter. Doing that needs the tree.</para>
    ///
    /// <para>Returns null when the expression does not parse. Validation stays the authority on
    /// WHY — this is for callers who have already asked <see cref="Validate"/> and got an answer
    /// they were happy with.</para>
    /// </summary>
    /// <param name="fieldKind">What type each identifier has, so the tree carries the same static
    /// typing the evaluator relies on: <c>a == b</c> means something different for two dates than
    /// for two numbers, and the answer is decided here rather than at every use.</param>
    public static Node? Parse(string? expr, Func<string, ComputedValueKind?> fieldKind) =>
        Parse(expr, fieldKind, null);

    /// <param name="customSignature">What the application's own code offers. A translator MUST pass
    /// the same lookup its validator did: without it a custom call validates and then fails to
    /// parse, and the target emits nothing for a field the author was told was fine.</param>
    public static Node? Parse(string? expr, Func<string, ComputedValueKind?> fieldKind,
        Func<string, CustomSignature?>? customSignature)
    {
        var parser = new Parser(expr, fieldKind, _ => null, _ => null, _ => null, null, null,
            customSignature);
        var node = parser.Parse();
        return parser.Error is null ? node : null;
    }

    /// <summary>
    /// One node of a parsed expression.
    ///
    /// <para>Public so it can be translated, and deliberately a closed set of records rather than a
    /// visitor interface: a caller pattern-matches over eight shapes and the compiler tells them
    /// when one is missed. A visitor would need a method per shape and would make adding one a
    /// breaking change for every implementor rather than a new case they can choose to handle.</para>
    ///
    /// <para>There is ONE parser. Everything that reads this language — the gate, the platform
    /// evaluator, the standalone generator — goes through it, because a second implementation of an
    /// expression language is a second set of answers to <c>1 / 0</c>.</para>
    /// </summary>
    public abstract record Node(ComputedValueKind Kind);

    /// <summary>A literal.</summary>
    public sealed record NumberNode(decimal Value) : Node(ComputedValueKind.Number);

    public sealed record BooleanNode(bool Value) : Node(ComputedValueKind.Boolean);

    /// <summary>A quoted code: <c>'sachsen'</c>. The value is what was between the quotes, with no
    /// unescaping to do — the grammar has no escapes.</summary>
    public sealed record TextNode(string Value) : Node(ComputedValueKind.Text);

    /// <summary><c>if(test, when_true, when_false)</c>. The result kind is whatever the two answers
    /// agreed on, which the parser has already checked they do.</summary>
    public sealed record ConditionalNode(Node Condition, Node Then, Node Else) : Node(Then.Kind);

    /// <summary>A reference to a field: this record's own, or <c>reference.field</c> for one hop.</summary>
    public sealed record FieldNode(string Key, ComputedValueKind FieldKind) : Node(FieldKind);

    /// <summary>
    /// <c>custom.discount(total, tier)</c> — a call into code the application carries.
    ///
    /// <para><b>The one node shape that could not be desugared, and the reason it is worth adding
    /// anyway.</b> House policy prefers a surface form that lowers onto existing nodes, because a
    /// new shape is a change to every evaluator, every emitter and every port. A user-written body
    /// cannot lower onto anything. The alternative was a built-in per function, which is that same
    /// change EVERY TIME somebody needs a calculation we did not think of — so the cost is paid
    /// once here and never again.</para>
    ///
    /// <para><see cref="Name"/> is the name without the prefix, because the prefix is how an
    /// expression SPELLS the call and has nothing to do with what is being called.</para>
    /// </summary>
    public sealed record CustomCallNode(
        string Name, IReadOnlyList<Node> Args, ComputedValueKind ResultKind) : Node(ResultKind);

    /// <summary><c>-x</c> or <c>not x</c>.</summary>
    public sealed record UnaryNode(string Op, Node Operand, ComputedValueKind ResultKind) : Node(ResultKind);

    public sealed record BinaryNode(string Op, Node Left, Node Right, ComputedValueKind ResultKind) : Node(ResultKind);

    /// <summary><c>pow</c>, <c>min</c> or <c>max</c> — two arbitrary sub-expressions.</summary>
    public sealed record FunctionNode(string Name, Node Left, Node Right) : Node(ComputedValueKind.Number);

    /// <summary><c>days_between(a, b)</c> and its siblings, whose arguments are bare date field
    /// names rather than expressions.</summary>
    public sealed record DurationNode(string Name, string From, string To) : Node(ComputedValueKind.Number);

    /// <summary><c>weekday(d)</c> and its siblings — one part of one date, named as a bare field the
    /// same way a duration's ends are.</summary>
    public sealed record DatePartNode(string Name, string Field) : Node(ComputedValueKind.Number);

    /// <summary><c>start_of_week(d)</c> and its siblings — the DATE a containing period begins or
    /// ends on, so this is the one function shape whose result is a date.</summary>
    public sealed record DateBoundaryNode(string Name, string Field) : Node(ComputedValueKind.Date);

    /// <summary><c>prev(field)</c> or <c>prev(field, seed)</c> — the previous row of an ordered
    /// series, and what to use when there is not one.</summary>
    public sealed record PrevNode(string Field, Node? Seed) : Node(ComputedValueKind.Number);

    private sealed class Parser
    {
        private readonly List<string> _tokens;
        private readonly Func<string, ComputedValueKind?> _fieldKind;
        private readonly Func<string, string?> _identError;
        private readonly Func<string, string?> _dateArgError;
        private readonly Func<string, string, string?> _datePartArgError;
        private readonly Func<string, string, string?> _codeError;
        private readonly Func<string, string?> _prevArgError;
        private readonly Func<string, CustomSignature?> _customSignature;
        private int _pos;

        public string? Error { get; private set; }
        public HashSet<string> Identifiers { get; } = new(StringComparer.Ordinal);

        public Parser(string? expr, Func<string, ComputedValueKind?> fieldKind,
            Func<string, string?> identError, Func<string, string?> dateArgError,
            Func<string, string?>? prevArgError = null,
            Func<string, string, string?>? datePartArgError = null,
            Func<string, string, string?>? codeError = null,
            Func<string, CustomSignature?>? customSignature = null)
        {
            _customSignature = customSignature ?? (_ => null);
            _prevArgError = prevArgError ?? identError;
            _datePartArgError = datePartArgError ?? ((_, _) => null);
            _codeError = codeError ?? ((_, _) => null);
            _tokens = Tokenize(expr, out var error);
            Error = error;
            _fieldKind = fieldKind;
            _identError = identError;
            _dateArgError = dateArgError;
        }

        public Node? Parse()
        {
            if (Error is not null) return null;
            var node = Or();
            if (Error is null && _pos < _tokens.Count) Error = $"unexpected '{_tokens[_pos]}'";
            return Error is null ? node : null;
        }

        private Node? Or()
        {
            var left = And();
            while (Error is null && Take("or")) left = Binary("or", left, And(), ComputedValueKind.Boolean);
            return left;
        }

        private Node? And()
        {
            var left = Equality();
            while (Error is null && Take("and")) left = Binary("and", left, Equality(), ComputedValueKind.Boolean);
            return left;
        }

        private Node? Equality()
        {
            var left = Comparison();
            while (Error is null && Peek() is "==" or "!=")
            {
                var op = _tokens[_pos++];
                var right = Comparison();
                if (left is not null && right is not null && left.Kind != right.Kind)
                    Error = $"operator '{op}' cannot compare a {Name(left.Kind)} with a {Name(right.Kind)}";
                else
                {
                    CheckCode(left, right);
                    left = Binary(op, left, right, ComputedValueKind.Boolean, sameKind: true);
                }
            }
            return left;
        }

        private Node? Comparison()
        {
            var left = Additive();
            while (Error is null && Peek() is "<" or "<=" or ">" or ">=")
            {
                var op = _tokens[_pos++];
                left = Binary(op, left, Additive(), ComputedValueKind.Boolean, ordered: true);
            }
            return left;
        }

        private Node? Additive()
        {
            var left = Term();
            while (Error is null && Peek() is "+" or "-")
            {
                var op = _tokens[_pos++];
                left = Binary(op, left, Term(), ComputedValueKind.Number, numbers: true);
            }
            return left;
        }

        private Node? Term()
        {
            var left = Unary();
            while (Error is null && Peek() is "*" or "/")
            {
                var op = _tokens[_pos++];
                left = Binary(op, left, Unary(), ComputedValueKind.Number, numbers: true);
            }
            return left;
        }

        private Node? Unary()
        {
            if (Take("-"))
            {
                var operand = Unary();
                if (operand is not null && operand.Kind != ComputedValueKind.Number)
                    Error = "unary '-' requires a number";
                return operand is null ? null : new UnaryNode("-", operand, ComputedValueKind.Number);
            }
            if (Take("not") || Take("!"))
            {
                var operand = Unary();
                if (operand is not null && operand.Kind != ComputedValueKind.Boolean)
                    Error = "'not' requires a boolean";
                return operand is null ? null : new UnaryNode("not", operand, ComputedValueKind.Boolean);
            }
            return Primary();
        }

        private Node? Primary()
        {
            if (Error is not null) return null;
            if (_pos >= _tokens.Count) { Error = "expression ends unexpectedly"; return null; }
            var token = _tokens[_pos++];
            if (token == "(")
            {
                var inner = Or();
                if (!Take(")")) Error ??= "missing closing parenthesis";
                return inner;
            }
            if (token == "true") return new BooleanNode(true);
            if (token == "false") return new BooleanNode(false);
            if (char.IsAsciiDigit(token[0]))
            {
                if (decimal.TryParse(token, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                    return new NumberNode(number);
                Error = $"'{token}' isn't a number";
                return null;
            }
            if (IsTextLiteral(token)) return new TextNode(token[1..^1]);
            if (!IsIdentifier(token)) { Error = $"unexpected '{token}'"; return null; }
            if (Peek() == "(") return Function(token);

            Identifiers.Add(token);
            if (_identError(token) is { } identError) { Error = identError; return null; }
            if (_fieldKind(token) is not { } kind)
            {
                Error = $"'{token}' is not a numeric, boolean, date, or text field";
                return null;
            }
            return new FieldNode(token, kind);
        }

        private Node? Function(string name)
        {
            if (!IsFunctionName(name))
            {
                Error = $"'{name}' is not a known function";
                return null;
            }
            _pos++; // '('
            if (IsCustomCall(name)) return CustomCall(name);
            if (name == SwitchFunc || name == CaseFunc)
            {
                var rows = new List<Node>();
                while (true)
                {
                    var arg = Or();
                    if (arg is null) { Error ??= $"'{name}(' is missing its closing parenthesis"; return null; }
                    rows.Add(arg);
                    if (Take(",")) continue;
                    break;
                }
                if (!Take(")")) { Error ??= $"'{name}(' is missing its closing parenthesis"; return null; }
                return name == SwitchFunc ? Switch(rows) : Case(rows);
            }
            if (name == ConditionalFunc)
            {
                var test = Or();
                if (!Take(",")) { Error ??= Arity(name); return null; }
                var whenTrue = Or();
                if (!Take(",")) { Error ??= Arity(name); return null; }
                var whenFalse = Or();
                if (Peek() == ",") { Error = Arity(name); return null; }
                if (!Take(")")) { Error ??= $"'{name}(' is missing its closing parenthesis"; return null; }
                if (test is null || whenTrue is null || whenFalse is null) return null;
                if (test.Kind != ComputedValueKind.Boolean)
                { Error = $"'{name}' tests something true or false, not a {Name(test.Kind)}"; return null; }
                // Both answers, one kind. Without this a field typed `money` could be handed a text
                // on one branch, and the mismatch would surface as a build error inside generated
                // source rather than as a sentence about the expression somebody wrote.
                if (whenTrue.Kind != whenFalse.Kind)
                {
                    Error = $"'{name}' must answer the same kind of thing either way, "
                          + $"not a {Name(whenTrue.Kind)} and a {Name(whenFalse.Kind)}";
                    return null;
                }
                return new ConditionalNode(test, whenTrue, whenFalse);
            }
            if (name == PrevFunc)
            {
                // The first argument is a FIELD NAME, not a value — `prev` reads that field on the
                // previous row, so passing an expression would be meaningless.
                var target = Peek();
                if (target is null || !IsIdentifier(target) || Keywords.Contains(target))
                { Error = $"'{name}' takes a field name, not '{target ?? ")"}'"; return null; }
                _pos++;
                Identifiers.Add(target);
                if (_prevArgError(target) is { } prevError) { Error = prevError; return null; }
                if (_fieldKind(target) is not ComputedValueKind.Number)
                { Error = $"'{name}({target})' needs a numeric field"; return null; }

                Node? seed = null;
                if (Take(","))
                {
                    seed = Or();
                    if (seed is null) return null;
                    if (seed.Kind != ComputedValueKind.Number)
                    { Error = $"'{name}' takes a number as its fallback"; return null; }
                }
                if (Peek() == ",") { Error = $"'{name}' takes at most two arguments"; return null; }
                if (!Take(")")) { Error ??= $"'{name}(' is missing its closing parenthesis"; return null; }
                return new PrevNode(target, seed);
            }
            if (MathFuncs.Contains(name))
            {
                var left = Or();
                if (!Take(",")) { Error ??= $"'{name}' takes exactly two arguments"; return null; }
                var right = Or();
                if (Peek() == ",") { Error = $"'{name}' takes exactly two arguments"; return null; }
                if (!Take(")")) { Error ??= $"'{name}(' is missing its closing parenthesis"; return null; }
                if (left is null || right is null) return null;
                if (left.Kind != ComputedValueKind.Number || right.Kind != ComputedValueKind.Number)
                { Error = $"'{name}' takes two numbers"; return null; }
                return new FunctionNode(name, left, right);
            }

            if (DatePartFuncs.Contains(name) || DateBoundaryFuncs.Contains(name))
            {
                var field = Peek();
                if (field is null || !IsIdentifier(field) || Keywords.Contains(field))
                { Error = $"'{name}' takes a date field, not '{field ?? ")"}'"; return null; }
                _pos++;
                Identifiers.Add(field);
                if (_dateArgError(field) is { } partError) { Error = partError; return null; }
                if (_datePartArgError(name, field) is { } conventionError)
                { Error = conventionError; return null; }
                if (Peek() == ",") { Error = $"'{name}' takes exactly one date field"; return null; }
                if (!Take(")")) { Error ??= $"'{name}(' is missing its closing parenthesis"; return null; }
                return DateBoundaryFuncs.Contains(name)
                    ? new DateBoundaryNode(name, field)
                    : new DatePartNode(name, field);
            }

            var args = new List<string>();
            while (Error is null)
            {
                if (_pos >= _tokens.Count) { Error = $"'{name}(' is missing its closing parenthesis"; return null; }
                var arg = _tokens[_pos];
                if (!IsIdentifier(arg)) { Error = $"'{name}' takes date fields, not '{arg}'"; return null; }
                _pos++;
                Identifiers.Add(arg);
                if (_dateArgError(arg) is { } dateError) { Error = dateError; return null; }
                args.Add(arg);
                if (Take(",")) continue;
                break;
            }
            if (!Take(")")) { Error ??= $"'{name}(' is missing its closing parenthesis"; return null; }
            if (args.Count != 2) { Error = $"'{name}' takes exactly two date fields"; return null; }
            return new DurationNode(name, args[0], args[1]);
        }

        /// <summary>
        /// <c>switch</c>, folded into the conditionals it means.
        ///
        /// <para>Nothing downstream learns a new shape. The evaluator, both emitters and the Node port
        /// already handle <see cref="ConditionalNode"/>, already work out only the branch taken, and
        /// already agree on what an unknown test answers. A table therefore computes to exactly what
        /// the nested <c>if</c> chain it replaces computed to — byte-identical generated code — and the
        /// only things that changed are what a person has to read and what the compiler can check.</para>
        /// </summary>
        private Node? Switch(List<Node> rows)
        {
            if (rows.Count < 4 || rows.Count % 2 != 0)
            {
                Error = $"'{SwitchFunc}' takes a field, then a key and a value for each row of the "
                      + "table, then a default — an even number of arguments, and at least four";
                return null;
            }
            if (rows[0] is not FieldNode subject)
            {
                Error = $"'{SwitchFunc}' looks a value up ON something, so its first argument is a "
                      + "field, not a working-out";
                return null;
            }
            if (subject.Kind is not (ComputedValueKind.Text or ComputedValueKind.Number))
            {
                Error = $"'{SwitchFunc}' keys a table on a code or a number, not a {Name(subject.Kind)}";
                return null;
            }

            var pairs = (rows.Count - 2) / 2;
            var keys = new List<Node>(pairs);
            var values = new List<Node>(pairs);
            var seenText = new HashSet<string>(StringComparer.Ordinal);
            var seenNumber = new HashSet<decimal>();

            for (var i = 0; i < pairs; i++)
            {
                var key = rows[1 + (i * 2)];
                if (key is not (TextNode or NumberNode))
                {
                    Error = $"'{SwitchFunc}' keys are written out, not worked out — a key that has to "
                          + "be computed cannot be checked against the field's options";
                    return null;
                }
                if (key.Kind != subject.Kind)
                {
                    Error = $"'{SwitchFunc}' keys '{subject.Key}', which is a {Name(subject.Kind)}, so "
                          + $"its keys are {Name(subject.Kind)}s and not {Name(key.Kind)}s";
                    return null;
                }

                // A repeated key is dead code wearing the shape of a table row, and a wide table is
                // exactly where that mistake hides. Free to catch: the keys are literals, already here.
                var text = key as TextNode;
                var fresh = text is not null
                    ? seenText.Add(text.Value)
                    : seenNumber.Add(((NumberNode)key).Value);
                if (!fresh)
                {
                    Error = $"'{SwitchFunc}' repeats the key {Literal(key)}, so the second one can "
                          + "never be reached";
                    return null;
                }
                if (text is not null && _codeError(subject.Key, text.Value) is { } codeMessage)
                {
                    Error = codeMessage;
                    return null;
                }

                keys.Add(key);
                values.Add(rows[2 + (i * 2)]);
            }

            var fallback = rows[^1];
            if (Answers(SwitchFunc, values, fallback) is { } kindMessage) { Error = kindMessage; return null; }

            var node = fallback;
            for (var i = pairs - 1; i >= 0; i--)
                node = new ConditionalNode(
                    new BinaryNode("==", subject, keys[i], ComputedValueKind.Boolean), values[i], node);
            return node;
        }

        /// <summary>The first test that holds, folded the same way <see cref="Switch"/> is.</summary>
        private Node? Case(List<Node> rows)
        {
            if (rows.Count < 3 || rows.Count % 2 == 0)
            {
                Error = $"'{CaseFunc}' takes a test and a value for each row, then a default — an odd "
                      + "number of arguments, and at least three";
                return null;
            }

            var pairs = (rows.Count - 1) / 2;
            var tests = new List<Node>(pairs);
            var values = new List<Node>(pairs);
            for (var i = 0; i < pairs; i++)
            {
                var test = rows[i * 2];
                if (test.Kind != ComputedValueKind.Boolean)
                {
                    Error = $"'{CaseFunc}' tests something true or false, not a {Name(test.Kind)}";
                    return null;
                }
                tests.Add(test);
                values.Add(rows[(i * 2) + 1]);
            }

            var fallback = rows[^1];
            if (Answers(CaseFunc, values, fallback) is { } message) { Error = message; return null; }

            var node = fallback;
            for (var i = pairs - 1; i >= 0; i--)
                node = new ConditionalNode(tests[i], values[i], node);
            return node;
        }

        /// <summary>Every way out answers the same kind, which is then the kind of the whole table —
        /// the rule <c>if</c> already carries, counted over a row of arms instead of two.</summary>
        private static string? Answers(string name, List<Node> values, Node fallback)
        {
            foreach (var value in values)
                if (value.Kind != fallback.Kind)
                    return $"'{name}' must answer the same kind of thing every way it can go, "
                         + $"not a {Name(value.Kind)} and a {Name(fallback.Kind)}";
            return null;
        }

        private static string Literal(Node key) => key switch
        {
            TextNode t => $"'{t.Value}'",
            NumberNode n => n.Value.ToString(CultureInfo.InvariantCulture),
            _ => "",
        };

        /// <summary>
        /// A code compared with a field that has a CLOSED set of them.
        ///
        /// <para><c>tax_class == 'klasse1'</c> parses, type-checks as text against text, and is false
        /// on every row that will ever exist. The arm is dead, the else branch pays out, and the figure
        /// it produces looks entirely plausible — a German payroll app shipped a Lohnsteuer of zero for
        /// every employee this way. Nothing was wrong with the expression as an expression; the field
        /// simply knew its options and nobody asked it.</para>
        ///
        /// <para>Both orders, because <c>'klasse1' == tax_class</c> is the same mistake. A field with no
        /// options — free text, or one whose codes the caller cannot see — is not checked at all: the
        /// caller answers null and the comparison stands.</para>
        /// </summary>
        private void CheckCode(Node? left, Node? right)
        {
            if (Error is not null) return;
            if ((Code(left, right) ?? Code(right, left)) is { } message) Error = message;
        }

        private string? Code(Node? field, Node? literal) =>
            field is FieldNode { FieldKind: ComputedValueKind.Text } f && literal is TextNode t
                ? _codeError(f.Key, t.Value)
                : null;

        private Node? Binary(string op, Node? left, Node? right, ComputedValueKind result,
            bool numbers = false, bool sameKind = false, bool ordered = false)
        {
            if (left is null || right is null) return null;
            if (numbers && (left.Kind != ComputedValueKind.Number || right.Kind != ComputedValueKind.Number))
            { Error = $"operator '{op}' requires numbers"; return null; }
            if (ordered && (left.Kind != right.Kind || left.Kind is not (ComputedValueKind.Number or ComputedValueKind.Date)))
            { Error = $"operator '{op}' requires two numbers or two dates"; return null; }
            if (!numbers && !sameKind && (left.Kind != ComputedValueKind.Boolean || right.Kind != ComputedValueKind.Boolean))
                if (!ordered) { Error = $"operator '{op}' requires booleans"; return null; }
            return new BinaryNode(op, left, right, result);
        }

        private string? Peek() => _pos < _tokens.Count ? _tokens[_pos] : null;
        private bool Take(string token)
        {
            if (Peek() != token) return false;
            _pos++;
            return true;
        }
        /// <summary>
        /// A call into the application's own code, checked against what that code declares.
        ///
        /// <para>Arity and kinds are checked HERE rather than left to the target's compiler, because
        /// the target's compiler speaks about C# parameters and this reader wrote a formula. Being
        /// told that <c>custom.discount</c> takes a number where you passed a date is actionable;
        /// being told that <c>decimal?</c> is not convertible from <c>string?</c> in a generated
        /// file is not.</para>
        /// </summary>
        private Node? CustomCall(string token)
        {
            var name = CustomName(token);
            var signature = _customSignature(name);

            if (signature is null)
            {
                Error = $"'{token}' is not a function this application declares. Custom functions "
                    + "come from the code under custom/, and are called by the name their "
                    + "[CordangoFunction] gives them.";
                return null;
            }

            var args = new List<Node>();
            if (!Take(")"))
            {
                while (true)
                {
                    var arg = Or();
                    if (arg is null) { Error ??= $"'{token}(' is missing its closing parenthesis"; return null; }
                    args.Add(arg);
                    if (Take(",")) continue;
                    break;
                }

                if (!Take(")")) { Error ??= $"'{token}(' is missing its closing parenthesis"; return null; }
            }

            if (args.Count != signature.Parameters.Count)
            {
                Error = $"'{token}' takes {Count(signature.Parameters.Count)}, not "
                    + $"{Count(args.Count)}";
                return null;
            }

            for (var i = 0; i < args.Count; i++)
            {
                if (args[i].Kind == signature.Parameters[i]) continue;
                Error = $"'{token}' takes a {Name(signature.Parameters[i])} as argument {i + 1}, "
                    + $"not a {Name(args[i].Kind)}";
                return null;
            }

            return new CustomCallNode(name, args, signature.Returns);
        }

        private static string Count(int n) => n == 1 ? "1 argument" : $"{n} arguments";

        private static string Arity(string name) =>
            $"'{name}' takes three arguments: a test, and one answer for each way it can go";

        private static string Name(ComputedValueKind kind) => kind switch
        {
            ComputedValueKind.Number => "number",
            ComputedValueKind.Boolean => "boolean",
            ComputedValueKind.Text => "text",
            _ => "date",
        };
    }

    private static List<string> Tokenize(string? expr, out string? error)
    {
        error = null;
        var source = (expr ?? "").Trim();
        var tokens = new List<string>();
        if (source.Length == 0) { error = "expression is empty"; return tokens; }
        for (var i = 0; i < source.Length;)
        {
            var c = source[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (i + 1 < source.Length && source.Substring(i, 2) is "<=" or ">=" or "==" or "!=")
            { tokens.Add(source.Substring(i, 2)); i += 2; continue; }
            if ("()+-*/!,<>".Contains(c)) { tokens.Add(c.ToString()); i++; continue; }

            // A TEXT LITERAL, kept whole — quotes included — so it stays distinguishable from an
            // identifier by its first character alone. That is what keeps `Identifiers` and
            // `LocalIdentifiers` from reporting 'sachsen' as a field this expression depends on.
            //
            // Either quote, and no escapes: a value that needs one quote is written with the other,
            // and a value needing both is not a select code. Codes are what this compares.
            if (c is '\'' or '"')
            {
                var close = source.IndexOf(c, i + 1);
                if (close < 0)
                { error = $"a text value opened with {c} is never closed"; return tokens; }
                if (source[(i + 1)..close].AsSpan().IndexOfAny('\n', '\r') >= 0)
                { error = "a text value cannot span lines"; return tokens; }
                tokens.Add(source[i..(close + 1)]);
                i = close + 1;
                continue;
            }
            if (c == '=') { error = "'=' isn't valid in an expression; use '==' for equality"; return tokens; }
            if (char.IsAsciiDigit(c))
            {
                var j = i;
                while (j < source.Length && (char.IsAsciiDigit(source[j]) || source[j] == '.')) j++;
                tokens.Add(source[i..j]); i = j; continue;
            }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                var j = i;
                while (j < source.Length && (char.IsAsciiLetterOrDigit(source[j]) || source[j] == '_')) j++;
                // ONE dot hop: `scenario.price_per_user` reads a field on the record this one
                // references. A single token on purpose — this class stays ignorant of what the hop
                // means, and the Gate and the data service each resolve it their own way. The digit
                // branch above already claimed `1.5`, so a decimal literal is unaffected.
                if (j < source.Length && source[j] == '.'
                    && j + 1 < source.Length && (char.IsAsciiLetter(source[j + 1]) || source[j + 1] == '_'))
                {
                    var k = j + 1;
                    while (k < source.Length && (char.IsAsciiLetterOrDigit(source[k]) || source[k] == '_')) k++;
                    tokens.Add(source[i..k]); i = k; continue;
                }
                tokens.Add(source[i..j]); i = j; continue;
            }
            error = $"'{c}' isn't valid in an expression";
            return tokens;
        }
        return tokens;
    }

    /// <summary>A token the tokenizer kept its quotes on. Checked before <see cref="IsIdentifier"/>
    /// everywhere, and mutually exclusive with it by construction: no identifier starts with a
    /// quote.</summary>
    private static bool IsTextLiteral(string token) =>
        token.Length >= 2 && token[0] is '\'' or '"' && token[^1] == token[0];

    private static bool IsIdentifier(string token) => token.Length > 0 &&
        (char.IsAsciiLetter(token[0]) || token[0] == '_') &&
        token.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '.');

    /// <summary>Splits a reference hop into (reference field, field on the target), or null for a
    /// plain local field. The ONE place that knows the shape of a hop, so the Gate's check and the
    /// runtime's lookup cannot disagree about what an expression means.</summary>
    public static (string Reference, string Field)? Hop(string identifier)
    {
        var dot = identifier.IndexOf('.');
        return dot <= 0 || dot == identifier.Length - 1
            ? null
            : (identifier[..dot], identifier[(dot + 1)..]);
    }
}
