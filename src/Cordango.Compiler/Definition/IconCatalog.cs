// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Definition;

/// <summary>
/// The curated icon vocabulary (Material Design Icons, bare names — the renderer adds the
/// <c>mdi-</c> prefix). One source of truth for three consumers: the generation prompt (teaches
/// the model which icons exist), the compiler (validates AI-chosen icons + stamps keyword-based
/// fallbacks so every app/page always has an icon), and — implicitly — the renderer, which can
/// trust that any icon in a compiled manifest is real.
/// </summary>
public static class IconCatalog
{
    /// <summary>Allowed icon names. Curated for internal business apps; ~100 names keeps the
    /// prompt cheap and the choices meaningful.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        // people & orgs
        "account", "account-group", "account-tie", "account-multiple", "badge-account", "school",
        "office-building", "domain", "handshake", "human-greeting",
        // work & process
        "briefcase", "clipboard-text", "clipboard-check", "clipboard-list", "check-circle",
        "checkbox-marked", "format-list-bulleted", "format-list-checks", "progress-check",
        "timeline-clock", "sitemap", "source-branch", "swap-horizontal", "approval",
        // tickets, support & requests
        "ticket", "ticket-confirmation", "lifebuoy", "headset", "message-text", "forum",
        "email", "phone", "bell", "alert", "alert-circle", "bug",
        // documents & knowledge
        "file-document", "file-document-multiple", "folder", "folder-open", "book-open",
        "notebook", "newspaper", "receipt", "certificate", "stamper",
        // time & planning
        "calendar", "calendar-clock", "calendar-check", "clock-outline", "timer", "history",
        // money & commerce
        "cash", "cash-multiple", "currency-usd", "credit-card", "cart", "finance", "chart-bar",
        "chart-line", "chart-pie", "scale-balance", "percent", "bank",
        // things & logistics
        "package-variant", "package-variant-closed", "truck", "warehouse", "barcode",
        "tag", "tag-multiple", "cube-outline", "palette-swatch",
        // tech & equipment
        "laptop", "monitor", "cellphone", "printer", "server", "database", "cloud", "cog",
        "wrench", "tools", "hammer", "robot", "chip",
        // places & travel
        "map-marker", "earth", "airplane", "car", "home", "door", "key",
        // health, safety & misc domains
        "hospital-box", "medical-bag", "pill", "flask", "shield-check", "lock", "fire",
        "leaf", "recycle", "lightning-bolt", "water", "food", "coffee", "gift", "star",
        "trophy", "medal", "target", "lightbulb", "rocket-launch", "flag", "heart",
        "thumb-up", "camera", "image", "video", "music",
        // generic fallbacks
        "table", "view-dashboard", "view-grid", "apps", "shape",
    };

    /// <summary>Keyword → icon fallback map, checked against an entity/app key + label. Order
    /// matters — first hit wins, so more specific words come first.</summary>
    private static readonly (string Keyword, string Icon)[] Keywords =
    {
        // Domain-shape words first: an app/entity named "customer support form" is a FORM about
        // customers, not a person — so form/ticket/survey outrank people words.
        ("survey", "clipboard-text"), ("questionnaire", "clipboard-text"), ("form", "clipboard-text"),
        ("ticket", "ticket"), ("helpdesk", "lifebuoy"), ("support", "lifebuoy"),
        ("inventory", "warehouse"), ("checklist", "format-list-checks"),
        ("candidate", "badge-account"), ("applicant", "badge-account"), ("recruit", "badge-account"),
        ("employee", "account-tie"), ("staff", "account-tie"), ("person", "account"),
        ("people", "account-group"), ("contact", "account"), ("customer", "account"),
        ("client", "account"), ("user", "account"), ("member", "account-group"),
        ("team", "account-group"), ("respondent", "account"),
        ("company", "office-building"), ("organization", "office-building"), ("organisation", "office-building"),
        ("vendor", "office-building"), ("supplier", "truck"), ("department", "sitemap"),
        ("deal", "handshake"), ("opportunit", "handshake"), ("sale", "cart"), ("lead", "target"),
        ("ticket", "ticket"), ("issue", "bug"), ("incident", "alert-circle"), ("bug", "bug"),
        ("request", "lifebuoy"),
        ("task", "check-circle"), ("todo", "check-circle"), ("activity", "clock-outline"),
        ("project", "briefcase"), ("job", "briefcase"), ("position", "briefcase"),
        ("interview", "forum"), ("meeting", "calendar-clock"), ("appointment", "calendar-check"),
        ("event", "calendar"), ("schedule", "calendar-clock"), ("booking", "calendar-check"),
        ("shift", "clock-outline"), ("vacation", "airplane"), ("leave", "airplane"),
        ("absence", "calendar-clock"), ("holiday", "airplane"),
        ("survey", "clipboard-text"), ("form", "clipboard-text"), ("question", "clipboard-list"),
        ("questionnaire", "clipboard-text"), ("response", "clipboard-check"), ("answer", "clipboard-check"),
        ("assessment", "clipboard-check"), ("review", "star"), ("feedback", "thumb-up"),
        ("inspection", "clipboard-check"), ("audit", "shield-check"), ("checklist", "format-list-checks"),
        ("invoice", "receipt"), ("payment", "credit-card"), ("expense", "cash"),
        ("budget", "finance"), ("order", "cart"), ("quote", "receipt"), ("contract", "certificate"),
        ("subscription", "credit-card"), ("price", "currency-usd"), ("cost", "cash"),
        ("product", "package-variant"), ("item", "cube-outline"), ("inventory", "warehouse"),
        ("stock", "warehouse"), ("asset", "laptop"), ("equipment", "tools"), ("device", "cellphone"),
        ("license", "key"), ("material", "cube-outline"), ("part", "cog"),
        ("shipment", "truck"), ("delivery", "truck"), ("warehouse", "warehouse"),
        ("document", "file-document"), ("file", "folder"), ("note", "notebook"),
        ("article", "newspaper"), ("policy", "book-open"), ("report", "chart-bar"),
        ("training", "school"), ("course", "school"), ("onboarding", "human-greeting"),
        ("approval", "approval"), ("workflow", "source-branch"), ("process", "source-branch"),
        ("status", "progress-check"), ("stage", "progress-check"),
        ("location", "map-marker"), ("site", "map-marker"), ("room", "door"), ("office", "office-building"),
        ("vehicle", "car"), ("trip", "airplane"), ("travel", "airplane"),
        ("message", "message-text"), ("comment", "forum"), ("notification", "bell"),
        ("goal", "target"), ("okr", "target"), ("kpi", "chart-line"), ("metric", "chart-line"),
        ("risk", "alert"), ("compliance", "scale-balance"), ("legal", "scale-balance"),
        ("patient", "medical-bag"), ("prescription", "pill"),
        ("recipe", "food"), ("menu", "food"), ("ingredient", "food"),
    };

    /// <summary>
    /// Resolve an icon: keep <paramref name="requested"/> when it's in the vocabulary (with or
    /// without an accidental <c>mdi-</c> prefix), otherwise fall back to the first keyword hit
    /// across the given hints (key, label, …), otherwise <paramref name="fallback"/>.
    /// </summary>
    public static string Resolve(string? requested, string fallback, params string?[] hints)
    {
        var name = requested?.Trim();
        if (name is { Length: > 0 })
        {
            if (name.StartsWith("mdi-", StringComparison.OrdinalIgnoreCase)) name = name[4..];
            name = name.ToLowerInvariant();
            if (All.Contains(name)) return name;
        }

        foreach (var hint in hints)
        {
            if (string.IsNullOrWhiteSpace(hint)) continue;
            var h = hint.ToLowerInvariant();
            foreach (var (keyword, icon) in Keywords)
                if (h.Contains(keyword) && All.Contains(icon))
                    return icon;
        }
        return fallback;
    }

    /// <summary>The vocabulary as a comma-separated line for the generation prompt.</summary>
    public static string PromptList => string.Join(", ", All.OrderBy(x => x, StringComparer.Ordinal));
}
