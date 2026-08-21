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

/// <summary>A typed computed result. A numeric result may be null when its inputs cannot produce a
/// trustworthy number (for example division by zero or an unset duration endpoint).</summary>
public readonly record struct ComputedValue(
    ComputedValueKind Kind, decimal? Number, bool? Boolean, DateTimeOffset? Date)
{
    public static ComputedValue FromNumber(decimal? value) => new(ComputedValueKind.Number, value, null, null);
    public static ComputedValue FromBoolean(bool? value) => new(ComputedValueKind.Boolean, null, value, null);
    public static ComputedValue FromDate(DateTimeOffset? value) => new(ComputedValueKind.Date, null, null, value);
}

/// <summary>The statically checked shape of an expression, including every field it reads.</summary>
public sealed record ComputedExprValidation(
    string? Error,
    ComputedValueKind? ResultKind,
    IReadOnlySet<string> Identifiers);

/// <summary>A small, typed expression language shared by the authoring Gate and runtime. It supports
/// numeric arithmetic, comparisons that return booleans, boolean logic, date durations, pow and the
/// two-value bounds min/max. Parsing and evaluation use the same AST, so an expression accepted by
/// the Gate cannot mean something different at runtime. Expressions are data, never passed to a
/// language evaluator.</summary>
public static class ComputedExpr
{
    public static readonly IReadOnlySet<string> DurationFuncs =
        new HashSet<string>(StringComparer.Ordinal) { "minutes_between", "hours_between", "days_between" };

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
    public static ComputedExprValidation Validate(string? expr,
        Func<string, ComputedValueKind?> fieldKind,
        Func<string, string?>? identError = null,
        Func<string, string?>? dateArgError = null,
        Func<string, string?>? prevArgError = null)
    {
        var parser = new Parser(expr, fieldKind, identError ?? (_ => null), dateArgError ?? (_ => null),
            prevArgError ?? identError ?? (_ => null));
        var node = parser.Parse();
        return new ComputedExprValidation(parser.Error, node?.Kind, parser.Identifiers);
    }

    /// <summary>Compatibility entry point for numeric expressions.</summary>
    public static decimal? Evaluate(string? expr, Func<string, decimal?> value,
        Func<string, DateTimeOffset?>? dateValue = null) =>
        EvaluateValue(expr, _ => ComputedValueKind.Number, value, _ => null, dateValue)?.Number;

    /// <summary>Evaluate a statically typed expression over one record. Blank numeric inputs retain
    /// the historical zero semantics; blank booleans read as false. Invalid expressions return null
    /// and should only be possible when an unvalidated manifest reaches the runtime.</summary>
    /// <param name="prevValue">The same field on the PREVIOUS row of an ordered series, or null when
    /// there is none. Null itself (no resolver) means this caller has no series, and every
    /// <c>prev()</c> falls back to its seed.</param>
    public static ComputedValue? EvaluateValue(string? expr,
        Func<string, ComputedValueKind?> fieldKind,
        Func<string, decimal?> numberValue,
        Func<string, bool?> booleanValue,
        Func<string, DateTimeOffset?>? dateValue = null,
        Func<string, decimal?>? prevValue = null)
    {
        var parser = new Parser(expr, fieldKind, _ => null, _ => null, _ => null);
        var node = parser.Parse();
        if (node is null || parser.Error is not null) return null;
        return Eval(node, numberValue, booleanValue, dateValue ?? (_ => null), prevValue);
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

    private abstract record Node(ComputedValueKind Kind);
    private sealed record NumberNode(decimal Value) : Node(ComputedValueKind.Number);
    private sealed record BooleanNode(bool Value) : Node(ComputedValueKind.Boolean);
    private sealed record FieldNode(string Key, ComputedValueKind FieldKind) : Node(FieldKind);
    private sealed record UnaryNode(string Op, Node Operand, ComputedValueKind ResultKind) : Node(ResultKind);
    private sealed record BinaryNode(string Op, Node Left, Node Right, ComputedValueKind ResultKind) : Node(ResultKind);
    private sealed record FunctionNode(string Name, Node Left, Node Right) : Node(ComputedValueKind.Number);
    private sealed record DurationNode(string Name, string From, string To) : Node(ComputedValueKind.Number);
    private sealed record PrevNode(string Field, Node? Seed) : Node(ComputedValueKind.Number);

    private static ComputedValue Eval(Node node,
        Func<string, decimal?> numberValue,
        Func<string, bool?> booleanValue,
        Func<string, DateTimeOffset?> dateValue,
        Func<string, decimal?>? prevValue = null)
    {
        decimal? Num(Node n) => Eval(n, numberValue, booleanValue, dateValue, prevValue).Number;
        bool Bool(Node n) => Eval(n, numberValue, booleanValue, dateValue, prevValue).Boolean == true;
        DateTimeOffset? Date(Node n) => Eval(n, numberValue, booleanValue, dateValue, prevValue).Date;

        return node switch
        {
            NumberNode n => ComputedValue.FromNumber(n.Value),
            BooleanNode b => ComputedValue.FromBoolean(b.Value),
            FieldNode f when f.Kind == ComputedValueKind.Number => ComputedValue.FromNumber(numberValue(f.Key) ?? 0m),
            FieldNode f when f.Kind == ComputedValueKind.Boolean => ComputedValue.FromBoolean(booleanValue(f.Key) ?? false),
            FieldNode f => ComputedValue.FromDate(dateValue(f.Key)),
            UnaryNode { Op: "-" } u => ComputedValue.FromNumber(-Num(u.Operand)),
            UnaryNode u => ComputedValue.FromBoolean(!Bool(u.Operand)),
            BinaryNode { Op: "+" } b => ComputedValue.FromNumber(Num(b.Left) + Num(b.Right)),
            BinaryNode { Op: "-" } b => ComputedValue.FromNumber(Num(b.Left) - Num(b.Right)),
            BinaryNode { Op: "*" } b => ComputedValue.FromNumber(Num(b.Left) * Num(b.Right)),
            BinaryNode { Op: "/" } b => Divide(Num(b.Left), Num(b.Right)),
            BinaryNode { Op: "<", Left.Kind: ComputedValueKind.Date } b => Compare(Date(b.Left), Date(b.Right), (a, c) => a < c),
            BinaryNode { Op: "<=", Left.Kind: ComputedValueKind.Date } b => Compare(Date(b.Left), Date(b.Right), (a, c) => a <= c),
            BinaryNode { Op: ">", Left.Kind: ComputedValueKind.Date } b => Compare(Date(b.Left), Date(b.Right), (a, c) => a > c),
            BinaryNode { Op: ">=", Left.Kind: ComputedValueKind.Date } b => Compare(Date(b.Left), Date(b.Right), (a, c) => a >= c),
            BinaryNode { Op: "<" } b => Compare(Num(b.Left), Num(b.Right), (a, c) => a < c),
            BinaryNode { Op: "<=" } b => Compare(Num(b.Left), Num(b.Right), (a, c) => a <= c),
            BinaryNode { Op: ">" } b => Compare(Num(b.Left), Num(b.Right), (a, c) => a > c),
            BinaryNode { Op: ">=" } b => Compare(Num(b.Left), Num(b.Right), (a, c) => a >= c),
            BinaryNode { Op: "==", Left.Kind: ComputedValueKind.Number } b =>
                Compare(Num(b.Left), Num(b.Right), (a, c) => a == c),
            BinaryNode { Op: "!=", Left.Kind: ComputedValueKind.Number } b =>
                Compare(Num(b.Left), Num(b.Right), (a, c) => a != c),
            BinaryNode { Op: "==", Left.Kind: ComputedValueKind.Date } b =>
                Compare(Date(b.Left), Date(b.Right), (a, c) => a == c),
            BinaryNode { Op: "!=", Left.Kind: ComputedValueKind.Date } b =>
                Compare(Date(b.Left), Date(b.Right), (a, c) => a != c),
            BinaryNode { Op: "==" } b => ComputedValue.FromBoolean(Bool(b.Left) == Bool(b.Right)),
            BinaryNode { Op: "!=" } b => ComputedValue.FromBoolean(Bool(b.Left) != Bool(b.Right)),
            BinaryNode { Op: "and" } b => ComputedValue.FromBoolean(Bool(b.Left) && Bool(b.Right)),
            BinaryNode { Op: "or" } b => ComputedValue.FromBoolean(Bool(b.Left) || Bool(b.Right)),
            FunctionNode { Name: "pow" } f => ComputedValue.FromNumber(Power(Num(f.Left), Num(f.Right))),
            FunctionNode { Name: "min" } f => ComputedValue.FromNumber(Bound(Num(f.Left), Num(f.Right), lower: true)),
            FunctionNode { Name: "max" } f => ComputedValue.FromNumber(Bound(Num(f.Left), Num(f.Right), lower: false)),
            DurationNode d => ComputedValue.FromNumber(Duration(d.Name, dateValue(d.From), dateValue(d.To))),
            // No previous row → the seed, or zero. `prevValue` returning null means "there is no
            // previous row"; a previous row whose field is blank also reads as the seed, which is the
            // same thing for a running total.
            PrevNode p => ComputedValue.FromNumber(prevValue?.Invoke(p.Field)
                ?? (p.Seed is null ? 0m : Num(p.Seed))),
            _ => ComputedValue.FromNumber(null),
        };
    }

    private static ComputedValue Divide(decimal? left, decimal? right) =>
        ComputedValue.FromNumber(right is null or 0m ? null : left / right);

    private static ComputedValue Compare(decimal? left, decimal? right, Func<decimal, decimal, bool> compare) =>
        ComputedValue.FromBoolean(left is { } a && right is { } b ? compare(a, b) : null);

    private static ComputedValue Compare(DateTimeOffset? left, DateTimeOffset? right,
        Func<DateTimeOffset, DateTimeOffset, bool> compare) =>
        ComputedValue.FromBoolean(left is { } a && right is { } b ? compare(a, b) : null);

    private static decimal? Duration(string name, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is not { } a || to is not { } b) return null;
        var span = b - a;
        return name switch
        {
            "minutes_between" => (decimal)span.TotalMinutes,
            "hours_between" => (decimal)span.TotalHours,
            "days_between" => (decimal)span.TotalDays,
            _ => null,
        };
    }

    /// <summary>The lower or upper of two values. Null in, null out — same discipline as
    /// <see cref="Power"/>. A bound that could not be worked out is not the same as no bound, and
    /// returning the other side would silently un-cap the figure.</summary>
    private static decimal? Bound(decimal? left, decimal? right, bool lower)
    {
        if (left is not { } a || right is not { } b) return null;
        return lower ? Math.Min(a, b) : Math.Max(a, b);
    }

    private static decimal? Power(decimal? b, decimal? e)
    {
        if (b is not { } bv || e is not { } ev) return null;
        var result = Math.Pow((double)bv, (double)ev);
        if (double.IsNaN(result) || double.IsInfinity(result)) return null;
        try { return (decimal)result; } catch (OverflowException) { return null; }
    }

    private sealed class Parser
    {
        private readonly List<string> _tokens;
        private readonly Func<string, ComputedValueKind?> _fieldKind;
        private readonly Func<string, string?> _identError;
        private readonly Func<string, string?> _dateArgError;
        private readonly Func<string, string?> _prevArgError;
        private int _pos;

        public string? Error { get; private set; }
        public HashSet<string> Identifiers { get; } = new(StringComparer.Ordinal);

        public Parser(string? expr, Func<string, ComputedValueKind?> fieldKind,
            Func<string, string?> identError, Func<string, string?> dateArgError,
            Func<string, string?>? prevArgError = null)
        {
            _prevArgError = prevArgError ?? identError;
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
            if (!MathFuncs.Contains(name) && !DurationFuncs.Contains(name) && name != PrevFunc)
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
