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
}

/// <summary>The statically checked shape of an expression, including every field it reads.</summary>
public sealed record ComputedExprValidation(
    string? Error,
    ComputedValueKind? ResultKind,
    IReadOnlySet<string> Identifiers);

/// <summary>
/// A small, typed expression language: numeric arithmetic, comparisons that return booleans, boolean
/// logic, date durations, <c>pow</c>, and the two-value bounds <c>min</c>/<c>max</c>.
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
    /// usage charge capped at a plan's ceiling, a balance floored at zero. There is no conditional in
    /// this language and booleans deliberately do not coerce to numbers, so the arithmetic dodge
    /// <c>(a &lt; b) * a + (a &gt;= b) * b</c> is rejected too: without these a cap simply cannot be
    /// written, and the author's only remaining move is to type the clamped number by hand every
    /// month, which is exactly what a computed field exists to stop.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> MathFuncs =
        new HashSet<string>(StringComparer.Ordinal) { "pow", "min", "max" };

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
    /// <param name="datePartArgError">Validates one date PART against the field it reads, given the
    /// function name and the field. Separate from <paramref name="dateArgError"/> because the rule
    /// it carries depends on which part was asked for rather than on the field alone:
    /// <c>hour_of</c> needs a time of day and a <c>date</c> column has none.</param>
    public static ComputedExprValidation Validate(string? expr,
        Func<string, ComputedValueKind?> fieldKind,
        Func<string, string?>? identError = null,
        Func<string, string?>? dateArgError = null,
        Func<string, string?>? prevArgError = null,
        Func<string, string, string?>? datePartArgError = null)
    {
        var parser = new Parser(expr, fieldKind, identError ?? (_ => null), dateArgError ?? (_ => null),
            prevArgError ?? identError ?? (_ => null), datePartArgError ?? ((_, _) => null));
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
            if (i + 1 < tokens.Count && tokens[i + 1] == "("
                && (MathFuncs.Contains(t) || DurationFuncs.Contains(t) || t == PrevFunc))
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
            if (i + 1 < tokens.Count && tokens[i + 1] == "("
                && (MathFuncs.Contains(t) || DurationFuncs.Contains(t) || t == PrevFunc))
                continue;
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
    public static Node? Parse(string? expr, Func<string, ComputedValueKind?> fieldKind)
    {
        var parser = new Parser(expr, fieldKind, _ => null, _ => null, _ => null);
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

    /// <summary>A reference to a field: this record's own, or <c>reference.field</c> for one hop.</summary>
    public sealed record FieldNode(string Key, ComputedValueKind FieldKind) : Node(FieldKind);

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
        private readonly Func<string, string?> _prevArgError;
        private int _pos;

        public string? Error { get; private set; }
        public HashSet<string> Identifiers { get; } = new(StringComparer.Ordinal);

        public Parser(string? expr, Func<string, ComputedValueKind?> fieldKind,
            Func<string, string?> identError, Func<string, string?> dateArgError,
            Func<string, string?>? prevArgError = null,
            Func<string, string, string?>? datePartArgError = null)
        {
            _prevArgError = prevArgError ?? identError;
            _datePartArgError = datePartArgError ?? ((_, _) => null);
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
                else left = Binary(op, left, right, ComputedValueKind.Boolean, sameKind: true);
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
            if (!IsIdentifier(token)) { Error = $"unexpected '{token}'"; return null; }
            if (Peek() == "(") return Function(token);

            Identifiers.Add(token);
            if (_identError(token) is { } identError) { Error = identError; return null; }
            if (_fieldKind(token) is not { } kind)
            {
                Error = $"'{token}' is not a numeric, boolean, or date field";
                return null;
            }
            return new FieldNode(token, kind);
        }

        private Node? Function(string name)
        {
            if (!MathFuncs.Contains(name) && !DurationFuncs.Contains(name)
                && !DatePartFuncs.Contains(name) && !DateBoundaryFuncs.Contains(name)
                && name != PrevFunc)
            {
                Error = $"'{name}' is not a known function";
                return null;
            }
            _pos++; // '('
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
        private static string Name(ComputedValueKind kind) => kind switch
        {
            ComputedValueKind.Number => "number",
            ComputedValueKind.Boolean => "boolean",
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
