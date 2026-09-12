// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.NodeVue.Emit;

/// <summary>
/// One file that says what this application contains, and one that says what it serves.
///
/// <para><b>Registration and routing, separated.</b> <c>app.ts</c> builds the runtime — the
/// entities, their hooks, the roles and the commands. <c>routes.ts</c> mounts them. They are two
/// files because they answer two different questions, and because the second is the one somebody
/// adds a hand-written endpoint to.</para>
///
/// <para>Both list their entities explicitly rather than looping over a registry. A route table you
/// can read is worth more than a line that saves typing — it is what makes a stack trace, a
/// breakpoint and a search for "where does this URL go" all work the obvious way.</para>
/// </summary>
public static class SetupEmitter
{
    public static GeneratedFile App(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var computed = app.Entities.Where(ComputedEmitter.Has).ToList();
        var auto = app.Entities.Where(AutoFieldsEmitter.Has).ToList();

        var source = new TsSource();

        source.Lines(Header.For(
            $"{app.Name}, assembled.",
            "The entities this application has, the hooks that keep their derived figures right, the\n"
            + "roles that decide who may touch them, and the commands that move them. One place, one line\n"
            + "per thing, in the order the definition lists them — which is why this application starts up\n"
            + "the same way twice."));

        source.Line();
        source.Line("import {");
        source.Line("  CordangoRuntime,");
        source.Line("  addDirectory,");
        source.Line("  type RuntimeOptions,");
        source.Line("} from \"@cordango/standalone\";");
        source.Line();
        source.Line("import { appCommands } from \"./commands.js\";");
        source.Line("import { appPermissions } from \"./permissions.js\";");
        source.Line("import {");
        source.Indent();

        foreach (var entity in app.Entities)
            source.Line($"{TypeScript.Identifier(entity.Key)}Descriptor,");

        foreach (var entity in app.Entities)
            source.Line($"type {entity.TypeName},");

        source.Outdent();
        source.Line("} from \"./entities.js\";");

        foreach (var entity in auto)
            source.Line(
                $"import {{ {AutoFieldsEmitter.FunctionName(entity)} }} "
                + $"from \"./auto/{entity.Key}.js\";");

        foreach (var entity in computed)
            source.Line(
                $"import {{ {ComputedEmitter.FunctionName(entity)} }} "
                + $"from \"./computed/{entity.Key}.js\";");

        source.Line();

        source.Lines(TypeScript.Doc(
            "Build the runtime. Everything this application is, before a single route is mounted.\n"
            + "\n"
            + "The caller supplies the database and the signing secret, because those are facts about "
            + "the DEPLOYMENT and not about the application — see server.ts, which reads them from the "
            + "environment and refuses to start without them.", 0));

        source.Open("export function buildRuntime(options: RuntimeOptions): CordangoRuntime");
        source.Line("const runtime = new CordangoRuntime({");
        source.Indent();
        source.Line("...options,");
        source.Line("permissions: appPermissions,");
        source.Line("commands: appCommands,");
        source.Outdent();
        source.Line("});");
        source.Line();
        source.Line("// People, departments, groups, organizations and contacts. The definition points at");
        source.Line("// these and assumes they exist; on the platform they are shared, and here they ship");
        source.Line("// with the application. Registered FIRST, so a reference from an entity below has");
        source.Line("// something to resolve against.");
        source.Line("addDirectory(runtime);");
        source.Line();

        foreach (var entity in app.Entities)
        {
            var descriptor = TypeScript.Identifier(entity.Key) + "Descriptor";

            var hasAuto = AutoFieldsEmitter.Has(entity);
            var hasComputed = ComputedEmitter.Has(entity);

            if (!hasAuto && !hasComputed)
            {
                source.Line($"runtime.register<{entity.TypeName}>({{ descriptor: {descriptor} }});");
                continue;
            }

            source.Open($"runtime.register<{entity.TypeName}>(");
            source.Line($"descriptor: {descriptor},");
            source.Open("hooks: () => (");

            // ORDER MATTERS on a create: the automatic fields go in first, so a computed figure
            // that reads one of them — `days_open` from `submitted_at`, say — has something to
            // read. Reversed, the figure would be worked out against a blank and be wrong on
            // exactly the rows nobody enters those fields on, which is all of them.
            var beforeCreate = new List<string>();
            if (hasAuto) beforeCreate.Add(AutoFieldsEmitter.FunctionName(entity));
            if (hasComputed) beforeCreate.Add($"(record) => {ComputedEmitter.FunctionName(entity)}(record)");

            source.Line($"beforeCreate: [{string.Join(", ", beforeCreate)}],");

            // NOT the automatic fields on an update. They answer "who created this and when", and
            // re-filling them on every edit would rewrite the record's own history to whoever
            // touched it last.
            if (hasComputed)
                source.Line($"beforeUpdate: [(record) => {ComputedEmitter.FunctionName(entity)}(record)],");

            source.Close("),");
            source.Close(");");
        }

        source.Line();
        source.Line("return runtime;");
        source.Close();

        return new GeneratedFile("api/src/app.ts", source.ToString());
    }

    public static GeneratedFile Routes(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var source = new TsSource();

        source.Lines(Header.For(
            $"What {app.Name} serves.",
            "One line per entity, under /api. The account, administration, directory, notification,\n"
            + "preference and media routes are mounted by the runtime before these and are not repeated\n"
            + "here.\n"
            + "\n"
            + "An endpoint of your own goes in a file beside this one and is mounted from here. Anything\n"
            + "you add survives a rebuild; this file does not."));

        source.Line();
        source.Line("import { recordsRouter, type CordangoRuntime } from \"@cordango/standalone\";");
        source.Line("import type { Router } from \"express\";");
        source.Line();

        source.Open("export function mountRoutes(api: Router, _runtime: CordangoRuntime): void");

        if (app.Entities.Count == 0)
        {
            source.Line("// This definition declares no entities, so there is nothing to mount.");
        }
        else
        {
            foreach (var entity in app.Entities)
                source.Line(
                    $"api.use({TypeScript.Literal("/" + entity.Key)}, "
                    + $"recordsRouter({TypeScript.Literal(entity.Key)}));");
        }

        source.Close();

        return new GeneratedFile("api/src/routes.ts", source.ToString());
    }
}
