// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.DotNetVue;

namespace Cordango.Standalone.Tests;

/// <summary>What the scaffold contains, and what it must never contain.</summary>
public class ScaffoldTests
{
    private static readonly ScaffoldOptions Expenses = new("Expense Claims", "expense-claims", "ExpenseClaims");

    [Fact]
    public void It_has_a_host_a_front_end_and_the_runtime()
    {
        var paths = Scaffold.Files(Expenses).Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("api/Program.cs", paths);
        Assert.Contains("api/ExpenseClaims.Api.csproj", paths);
        Assert.Contains("api/Identity/AppIdentity.cs", paths);
        Assert.Contains("api/Resources/messages.de.json", paths);
        Assert.Contains("web/src/main.js", paths);
        Assert.Contains("Dockerfile", paths);
        Assert.Contains("docker-compose.yml", paths);
        Assert.Contains("README.md", paths);

        // The runtime is a SIBLING of the application, not a directory inside it — so api/ holds
        // the user's code and nothing else, and swapping the whole thing for a package is a
        // deletion plus one line.
        Assert.Contains("runtime/Security/PermissionResolver.cs", paths);
        Assert.Contains("runtime/Http/RecordsController.cs", paths);
        Assert.Contains("runtime/Directory/Entities.cs", paths);
    }

    /// <summary>
    /// The project file is stored as <c>.csproj.template</c> so that nothing in this repository
    /// mistakes a file full of <c>{{…}}</c> for a project to compile, and it has to come out the
    /// other side as a real one. A suffix that survived would produce an output directory whose
    /// application cannot be built — visible only to whoever tried.
    /// </summary>
    [Fact]
    public void Nothing_is_emitted_with_the_template_suffix()
    {
        Assert.DoesNotContain(Scaffold.Files(Expenses),
            f => f.RelativePath.EndsWith(".template", StringComparison.Ordinal));
    }

    [Fact]
    public void Placeholders_are_substituted_in_paths_and_in_content()
    {
        var files = Scaffold.Files(Expenses).ToDictionary(f => f.RelativePath, f => f.Content, StringComparer.Ordinal);

        Assert.Contains("<RootNamespace>ExpenseClaims</RootNamespace>", files["api/ExpenseClaims.Api.csproj"]);
        Assert.Contains("namespace ExpenseClaims.Data;", files["api/Data/AppDbContext.cs"]);
        Assert.Contains("# Expense Claims", files["README.md"]);
        Assert.Contains("POSTGRES_DB: expense-claims", files["docker-compose.yml"]);
    }

    /// <summary>
    /// No scaffold placeholder survives into the output.
    ///
    /// <para>Worth its own test because of how the failure presents: the file looks right, the
    /// build fails somewhere unrelated, and the compiler reports a C# syntax error rather than a
    /// missing substitution.</para>
    ///
    /// <para>It checks the four tokens by NAME rather than looking for double braces, and that
    /// precision is the point. Vue templates use the same braces for their own bindings, and a doc
    /// comment in the runtime quotes <c>{{salary}}</c> while explaining how a template leaks a
    /// hidden field. A test that flagged every pair of braces would have to be taught about both,
    /// and would go on being taught about the next one — until somebody silenced it.</para>
    /// </summary>
    [Fact]
    public void No_placeholder_survives_into_the_output()
    {
        // The list comes from the scaffold, not from here. A local copy would go stale the first
        // time somebody adds a placeholder, and the test would keep passing.
        Assert.True(Scaffold.Tokens.Count >= 4);

        foreach (var file in Scaffold.Files(Expenses))
            foreach (var token in Scaffold.Tokens)
            {
                Assert.False(file.RelativePath.Contains(token, StringComparison.Ordinal),
                    $"{file.RelativePath}: {token} survived in a PATH.");
                Assert.False(file.Content.Contains(token, StringComparison.Ordinal),
                    $"{file.RelativePath}: {token} survived in the content.");
            }
    }

    [Fact]
    public void The_same_options_produce_the_same_files_in_the_same_order()
    {
        var first = Scaffold.Files(Expenses);
        var second = Scaffold.Files(Expenses);

        Assert.Equal(first.Select(f => f.RelativePath), second.Select(f => f.RelativePath));
        Assert.Equal(first.Select(f => f.Content), second.Select(f => f.Content));
        Assert.Equal(first.Select(f => f.RelativePath).Order(StringComparer.Ordinal), first.Select(f => f.RelativePath));
    }

    /// <summary>
    /// Every line ending is <c>\n</c> before anything hashes or writes it.
    ///
    /// <para>This repository is cloned on machines with <c>core.autocrlf=true</c>, which rewrites
    /// the working copy on checkout. Without normalisation the scaffold's fingerprint — and every
    /// file hash in the generated build metadata — would depend on which operating system ran the
    /// build, and two byte-identical applications would disagree about what they contain.</para>
    /// </summary>
    [Fact]
    public void Line_endings_are_normalised()
    {
        foreach (var file in Scaffold.Files(Expenses))
            Assert.False(file.Content.Contains('\r'),
                $"{file.RelativePath} carries a carriage return. The scaffold reader normalises on the way in; something bypassed it.");
    }

    /// <summary>
    /// The scaffold version is derived from the scaffold's contents, so it cannot go stale and
    /// cannot be forgotten. Its job in <c>cordango.build.json</c> is to answer "which scaffold
    /// produced this" years later, and a hand-maintained constant answers that only for as long as
    /// somebody remembers to change it.
    /// </summary>
    [Fact]
    public void The_version_is_a_fingerprint_of_the_contents()
    {
        Assert.Matches(@"^1\.0\.0\+[0-9a-f]{12}$", Scaffold.Version);
        Assert.Equal(Scaffold.Version, Scaffold.Version);
    }

    /// <summary>
    /// Generated files belong to the user and carry no Cordango header; the runtime source does,
    /// because it is our code arriving under our licence.
    ///
    /// <para>Both halves matter. Stamping our copyright on a user's Program.cs claims something
    /// untrue about a file they will edit on day one. Shipping our runtime without its notice
    /// removes the only thing that travels with a copied file and says what it is.</para>
    /// </summary>
    [Fact]
    public void The_runtime_is_stamped_and_the_users_code_is_not()
    {
        const string marker = "SPDX-License-Identifier: Apache-2.0";

        foreach (var file in Scaffold.Files(Expenses))
        {
            var head = string.Join('\n', file.Content.Split('\n').Take(6));
            // The runtime's own project file is ours too, and it is XML: the header convention is
            // about source files, and that file already opens with a comment explaining what the
            // directory is.
            var isRuntime = file.RelativePath.StartsWith("runtime/", StringComparison.Ordinal)
                && file.RelativePath.EndsWith(".cs", StringComparison.Ordinal);

            if (isRuntime)
                Assert.True(head.Contains(marker, StringComparison.Ordinal),
                    $"{file.RelativePath} is Cordango runtime source and has lost its licence header.");
            else
                Assert.False(head.Contains(marker, StringComparison.Ordinal),
                    $"{file.RelativePath} is the user's file and must not carry our copyright.");
        }
    }

    /// <summary>
    /// The application's own directory holds the application, and nothing else.
    ///
    /// <para>The runtime used to land in <c>api/Cordango/</c>, which meant twenty files of framework
    /// code sitting among somebody's own. It is a library; it belongs beside the application, not
    /// inside it.</para>
    /// </summary>
    [Fact]
    public void The_runtime_is_beside_the_application_and_not_inside_it()
    {
        var files = Scaffold.Files(Expenses);

        Assert.DoesNotContain(files, f => f.RelativePath.StartsWith("api/Cordango/", StringComparison.Ordinal));
        Assert.Contains(files, f => f.RelativePath == "runtime/Cordango.Standalone.csproj");

        var project = files.Single(f => f.RelativePath == "api/ExpenseClaims.Api.csproj").Content;
        Assert.Contains(@"<ProjectReference Include=""..\runtime\Cordango.Standalone.csproj"" />",
            project, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--runtime package</c> emits the shape this is all heading towards: a PackageReference and
    /// no checked-in runtime at all.
    ///
    /// <para>Pinned now, while the package is still private, because the alternative is discovering
    /// on publication day that the option nobody could exercise had rotted.</para>
    /// </summary>
    [Fact]
    public void Referencing_the_runtime_as_a_package_leaves_nothing_to_check_in()
    {
        var files = Scaffold.Files(Expenses with { RuntimeAsPackage = true });

        Assert.DoesNotContain(files, f => f.RelativePath.StartsWith("runtime/", StringComparison.Ordinal));

        var project = files.Single(f => f.RelativePath == "api/ExpenseClaims.Api.csproj").Content;
        Assert.Contains(
            $@"<PackageReference Include=""Cordango.Standalone"" Version=""{Scaffold.RuntimeVersion}"" />",
            project, StringComparison.Ordinal);
    }

    /// <summary>
    /// The README says nothing about a partial build unless the build was partial. Absence has to
    /// mean something, or a knowingly incomplete application could pass for a complete one later.
    /// </summary>
    [Fact]
    public void The_partial_build_section_appears_only_when_it_is_supplied()
    {
        var whole = Scaffold.Files(Expenses).Single(f => f.RelativePath == "README.md").Content;
        Assert.DoesNotContain("Partial build", whole, StringComparison.Ordinal);

        var scarred = Scaffold.Files(Expenses with
        {
            PartialBuildSection = "\n## Partial build\n\nSome screens could not be generated.\n",
        }).Single(f => f.RelativePath == "README.md").Content;

        Assert.Contains("## Partial build", scarred, StringComparison.Ordinal);
    }

    /// <summary>
    /// Guards the guard. If the embedding glob ever stops matching — a renamed directory, a changed
    /// Exclude — <see cref="Scaffold.Files"/> would return a short list and every assertion above
    /// that names a specific file would still be the only thing complaining. A floor makes the
    /// silence audible.
    /// </summary>
    [Fact]
    public void The_scaffold_is_not_quietly_empty()
    {
        var files = Scaffold.Files(Expenses);
        Assert.True(files.Count >= 40,
            $"Only {files.Count} scaffold files were embedded. The glob in Cordango.SourceGen.DotNetVue.csproj "
            + "has probably stopped matching.");

        Assert.True(files.Count(f => f.RelativePath.StartsWith("runtime/", StringComparison.Ordinal)) >= 12,
            "The runtime source did not come along.");
    }

    /// <summary>
    /// The runtime the generator emits is the runtime this repository compiles and tests. If the two
    /// ever diverge — a file added and not embedded, an Exclude that grew — an application would
    /// ship code nobody here ever ran.
    /// </summary>
    [Fact]
    public void Every_runtime_source_file_is_embedded()
    {
        var onDisk = System.IO.Directory
            .EnumerateFiles(RuntimeSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(RuntimeSourceDirectory, p).Replace('\\', '/'))
            .Where(p => !p.StartsWith("Templates/", StringComparison.Ordinal)
                        && !p.StartsWith("bin/", StringComparison.Ordinal)
                        && !p.StartsWith("obj/", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var embedded = Scaffold.Files(Expenses)
            .Where(f => f.RelativePath.StartsWith("runtime/", StringComparison.Ordinal)
                && f.RelativePath.EndsWith(".cs", StringComparison.Ordinal))
            .Select(f => f.RelativePath["runtime/".Length..])
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(onDisk, embedded);
    }

    private static string RuntimeSourceDirectory =>
        Path.Combine(TestPaths.RepoRoot(), "src", "Cordango.Standalone");
}
