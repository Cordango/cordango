// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Cordango.Standalone.Http;

/// <summary>
/// Turns an error code into a sentence in the reader's language.
///
/// <para><b>The code travels, the sentence does not.</b> Everything below the boundary — stores,
/// hooks, permission checks — raises a <see cref="RecordException"/> carrying a dotted code and an
/// English sentence as a fallback. Only here, at the edge, does anything decide which language to
/// answer in. That is what keeps a translation from having to be threaded through every layer, and
/// what keeps a client switching on <c>record.not_found</c> from breaking when somebody improves the
/// wording.</para>
/// </summary>
public interface IApiMessages
{
    /// <summary>The message for this code, or <paramref name="fallback"/> when the code has no entry
    /// — a missing translation should degrade to an English sentence, never to a blank or to the
    /// code itself.</summary>
    string Translate(string code, string fallback);
}

/// <summary>The default when an application has not set up translations: the fallback, as
/// written.</summary>
public sealed class PassThroughApiMessages : IApiMessages
{
    public string Translate(string code, string fallback) => fallback;
}

/// <summary>
/// Messages from a JSON file per language, picked by the request's <c>Accept-Language</c>.
///
/// <para>Only the primary subtag is read, so <c>de-AT</c> and <c>de-CH</c> both find <c>de</c>. An
/// application that genuinely differs by region adds the fuller tag as its own file and it is found
/// first.</para>
/// </summary>
public sealed class JsonApiMessages : IApiMessages
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _byLanguage;
    private readonly IHttpContextAccessor _accessor;
    private readonly string _default;

    public JsonApiMessages(IHttpContextAccessor accessor, string resourceDirectory, string defaultLanguage = "en")
    {
        _accessor = accessor;
        _default = defaultLanguage;
        _byLanguage = Load(resourceDirectory);
    }

    public string Translate(string code, string fallback)
    {
        foreach (var language in Preferred())
            if (_byLanguage.TryGetValue(language, out var table) && table.TryGetValue(code, out var message))
                return message;

        return fallback;
    }

    /// <summary>Languages to try, best first, ending with the application's default.</summary>
    private IEnumerable<string> Preferred()
    {
        var header = _accessor.HttpContext?.Request.Headers.AcceptLanguage.ToString();

        if (!string.IsNullOrWhiteSpace(header))
        {
            // Ordered by the q-value the client sent, which is the client saying which of several
            // acceptable languages it actually prefers. Ignoring it and taking the first listed
            // works for the common one-language case and quietly picks wrong for everyone else.
            var ranked = header
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Parse)
                .Where(x => x.Quality > 0)
                .OrderByDescending(x => x.Quality)
                .Select(x => x.Language);

            foreach (var language in ranked) yield return language;
        }

        yield return _default;
    }

    private static (string Language, double Quality) Parse(string entry)
    {
        var parts = entry.Split(';', StringSplitOptions.TrimEntries);
        var tag = parts[0].Split('-')[0].ToLowerInvariant();

        var quality = 1.0;
        foreach (var parameter in parts.Skip(1))
            if (parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(parameter[2..], System.Globalization.CultureInfo.InvariantCulture, out var q))
                quality = q;

        return (tag, quality);
    }

    /// <summary>Read once at startup. Messages change when the application is redeployed, and
    /// re-reading a file on every 404 would be a strange place to spend a syscall.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load(string directory)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!System.IO.Directory.Exists(directory)) return result;

        foreach (var path in System.IO.Directory.EnumerateFiles(directory, "messages.*.json"))
        {
            var language = Path.GetFileNameWithoutExtension(path).Split('.').Last().ToLowerInvariant();
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (table is not null) result[language] = table;
        }

        return result;
    }
}
