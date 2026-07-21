#!/usr/bin/env dotnet-script
#r "nuget: Microsoft.CodeAnalysis.CSharp, 5.0.0"
#nullable enable

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

// Diff modes (each defines an OLD ref -> NEW ref comparison over src/*.cs):
//   --staged        OLD=HEAD    NEW=index      (pre-commit hook)
//   --last-commit   OLD=HEAD^   NEW=HEAD       (post-commit inspection)
//   --since <ref>   OLD=<ref>   NEW=worktree   (whole-module diff; best fit)
// --names-only emits bare changed symbol names instead of full signatures.
var args = Args.ToArray();
var isStaged = args.Contains("--staged");
var isLastCommit = args.Contains("--last-commit");
var namesOnly = args.Contains("--names-only");
var sinceIndex = Array.IndexOf(args, "--since");
var sinceRef = sinceIndex >= 0 && sinceIndex + 1 < args.Length ? args[sinceIndex + 1] : null;

if (!isStaged && !isLastCommit && sinceRef is null)
{
    Console.Error.WriteLine("Usage: check_signatures.csx --staged | --last-commit | --since <ref> [--names-only]");
    Environment.Exit(2);
}

string oldRef;
string newRef;
List<string> files;

if (isStaged)
{
    oldRef = "HEAD";
    newRef = ":INDEX:";
    files = GitLines("diff", "--cached", "--name-only", "--diff-filter=ACMRD", "--", "src");
}
else if (isLastCommit)
{
    oldRef = "HEAD^";
    newRef = "HEAD";
    files = GitLines("diff", "--name-only", "--diff-filter=ACMRD", oldRef, newRef, "--", "src");
}
else
{
    oldRef = sinceRef!;
    newRef = ":WORKTREE:";
    files = GitLines("diff", "--name-only", "--diff-filter=ACMRD", oldRef, "--", "src");
}

var changes = new List<ApiEntry>();

foreach (var file in files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
{
    var oldApi = ExtractApi(ReadVersion(oldRef, file), file, strict: false);
    var newApi = ExtractApi(ReadVersion(newRef, file), file, strict: true);

    foreach (var removed in oldApi.Keys.Except(newApi.Keys))
        changes.Add(oldApi[removed]);

    foreach (var added in newApi.Keys.Except(oldApi.Keys))
        changes.Add(newApi[added]);
}

changes = changes
    .DistinctBy(c => c.Signature)
    .OrderBy(c => c.Symbol)
    .ThenBy(c => c.Signature)
    .ToList();

if (isStaged && changes.Count > 0)
{
    var architectureStaged = GitLines("diff", "--cached", "--name-only")
        .Contains(".cline/rules/architecture.md");

    if (!architectureStaged)
    {
        Console.WriteLine("BLOCK: a declared C# type/member signature changed without .cline/rules/architecture.md in the same commit.");
        Console.WriteLine("Changed API entries:");
        foreach (var change in changes)
            Console.WriteLine($"  {change.Signature}");
        Environment.Exit(1);
    }
}

if (namesOnly)
{
    foreach (var symbol in changes.Select(c => c.Symbol).Distinct().Order())
        Console.WriteLine(symbol);
}
else if (!isStaged)
{
    foreach (var change in changes)
        Console.WriteLine(change.Signature);
}

// Full declared public/protected API surface, keyed by canonical signature so
// overloads stay distinct and return-type/modifier/generic/base changes show.
Dictionary<string, ApiEntry> ExtractApi(string source, string file, bool strict)
{
    if (string.IsNullOrWhiteSpace(source))
        return new();

    var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
    var tree = CSharpSyntaxTree.ParseText(source, parseOptions);

    // Fail closed: a source that won't parse yields a wrong API surface, which
    // would silently MISS drift. Only the new version is gated (old may be a
    // deleted/renamed file we can't parse).
    if (strict)
    {
        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        if (errors.Count > 0)
        {
            Console.Error.WriteLine($"ERROR: could not parse '{file}' (signature check cannot be trusted):");
            foreach (var e in errors.Take(10))
                Console.Error.WriteLine($"  {e}");
            Environment.Exit(2);
        }
    }

    var root = tree.GetRoot();
    var result = new Dictionary<string, ApiEntry>(StringComparer.Ordinal);

    foreach (var node in root.DescendantNodes())
    {
        ApiEntry? entry = node switch
        {
            BaseTypeDeclarationSyntax type => TypeEntry(type),
            DelegateDeclarationSyntax del => new ApiEntry(
                QualifiedName(del, del.Identifier.Text),
                $"{Mods(del.Modifiers)} delegate {Norm(del.ReturnType)} {QualifiedName(del, del.Identifier.Text)}{Norm(del.TypeParameterList)}{Norm(del.ParameterList)}{Constraints(del.ConstraintClauses)}"),
            MethodDeclarationSyntax method => new ApiEntry(
                QualifiedName(method, method.Identifier.Text),
                $"{Mods(method.Modifiers)} {Norm(method.ReturnType)} {QualifiedName(method, method.Identifier.Text)}{Norm(method.TypeParameterList)}{Norm(method.ParameterList)}{Constraints(method.ConstraintClauses)}"),
            ConstructorDeclarationSyntax ctor => new ApiEntry(
                QualifiedName(ctor, ctor.Identifier.Text),
                $"{Mods(ctor.Modifiers)} {QualifiedName(ctor, ctor.Identifier.Text)}{Norm(ctor.ParameterList)}"),
            PropertyDeclarationSyntax property => new ApiEntry(
                QualifiedName(property, property.Identifier.Text),
                $"{Mods(property.Modifiers)} {Norm(property.Type)} {QualifiedName(property, property.Identifier.Text)} {Accessors(property.AccessorList)}"),
            IndexerDeclarationSyntax indexer => new ApiEntry(
                QualifiedName(indexer, "this"),
                $"{Mods(indexer.Modifiers)} {Norm(indexer.Type)} {QualifiedName(indexer, "this")}{Norm(indexer.ParameterList)} {Accessors(indexer.AccessorList)}"),
            EventDeclarationSyntax ev => new ApiEntry(
                QualifiedName(ev, ev.Identifier.Text),
                $"{Mods(ev.Modifiers)} event {Norm(ev.Type)} {QualifiedName(ev, ev.Identifier.Text)} {Accessors(ev.AccessorList)}"),
            EventFieldDeclarationSyntax evf => new ApiEntry(
                QualifiedName(evf, string.Join(",", evf.Declaration.Variables.Select(v => v.Identifier.Text))),
                $"{Mods(evf.Modifiers)} event {Norm(evf.Declaration.Type)} {QualifiedContainer(evf)}.{string.Join(",", evf.Declaration.Variables.Select(v => v.Identifier.Text))}"),
            FieldDeclarationSyntax field => new ApiEntry(
                QualifiedName(field, string.Join(",", field.Declaration.Variables.Select(v => v.Identifier.Text))),
                $"{Mods(field.Modifiers)} {Norm(field.Declaration.Type)} {QualifiedContainer(field)}.{string.Join(",", field.Declaration.Variables.Select(v => v.Identifier.Text))}"),
            _ => null
        };

        // Only surface public/protected declarations (the inheritable API).
        if (entry is not null && IsExposed(node))
            result[entry.Signature] = entry;
    }

    return result;
}

bool IsExposed(SyntaxNode node)
{
    var mods = node switch
    {
        MemberDeclarationSyntax m => m.Modifiers,
        _ => default
    };
    if (mods.Any(t => t.IsKind(SyntaxKind.PublicKeyword))) return true;
    if (mods.Any(t => t.IsKind(SyntaxKind.ProtectedKeyword))) return true;

    // Interface members are implicitly public and normally carry no access
    // modifier at all, so a modifier-only test silently ignores every ordinary
    // interface method – exactly the signatures the contract tests pin. Treat
    // any non-private interface member as public. (Explicit 'private' members
    // in an interface are C# 8+ implementation details and stay excluded.)
    if (node.Parent is InterfaceDeclarationSyntax
        && !mods.Any(t => t.IsKind(SyntaxKind.PrivateKeyword)))
    {
        return true;
    }
    return false;
}

ApiEntry TypeEntry(BaseTypeDeclarationSyntax type)
{
    var keyword = type switch
        {

            RecordDeclarationSyntax recordDeclaration
                when !recordDeclaration.ClassOrStructKeyword.IsKind(SyntaxKind.None)
                    => $"record {recordDeclaration.ClassOrStructKeyword.Text}",
            RecordDeclarationSyntax => "record",
            ClassDeclarationSyntax => "class",
            StructDeclarationSyntax => "struct",
            InterfaceDeclarationSyntax => "interface",
            EnumDeclarationSyntax => "enum",
            _ => type.Kind().ToString()
        };
        var typeParams = type is TypeDeclarationSyntax td ? Norm(td.TypeParameterList) : "";
    var constraints = type is TypeDeclarationSyntax td2 ? Constraints(td2.ConstraintClauses) : "";
    var primaryCtor = type is RecordDeclarationSyntax record ? Norm(record.ParameterList) : "";
    var bases = Norm(type.BaseList);
    var name = QualifiedName(type, type.Identifier.Text);
    return new ApiEntry(name, $"{Mods(type.Modifiers)} {keyword} {name}{typeParams}{primaryCtor}{bases}{constraints}");
}

string QualifiedName(SyntaxNode node, string name)
{
    var container = QualifiedContainer(node);
    return string.IsNullOrEmpty(container) ? name : $"{container}.{name}";
}

string QualifiedContainer(SyntaxNode node)
{
    var parts = new List<string>();
    foreach (var ancestor in node.Ancestors().Reverse())
    {
        switch (ancestor)
        {
            case BaseNamespaceDeclarationSyntax ns:
                parts.Add(ns.Name.ToString());
                break;
            case BaseTypeDeclarationSyntax type:
                parts.Add(type.Identifier.Text);
                break;
        }
    }
    return string.Join(".", parts);
}

string Mods(SyntaxTokenList modifiers) => string.Join(" ", modifiers.Select(m => m.Text));
string Norm(SyntaxNode? node) => node is null ? "" : node.NormalizeWhitespace().ToFullString();
string Constraints(SyntaxList<TypeParameterConstraintClauseSyntax> clauses) =>
    clauses.Count == 0 ? "" : " " + string.Join(" ", clauses.Select(Norm));
string Accessors(AccessorListSyntax? list) => list is null
    ? ""
    : "{ " + string.Join(" ", list.Accessors.Select(a => $"{Mods(a.Modifiers)} {a.Keyword.Text}".Trim())) + " }";

string ReadVersion(string version, string file)
{
    if (version == ":WORKTREE:")
        return File.Exists(file) ? File.ReadAllText(file) : "";
    if (version == ":INDEX:")
        return GitTextAllowFailure("show", $":{file}");
    return GitTextAllowFailure("show", $"{version}:{file}");
}

List<string> GitLines(params string[] args) => GitTextAllowFailure(args)
    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToList();

string GitTextAllowFailure(params string[] args)
{
    var psi = new ProcessStartInfo("git")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    foreach (var arg in args)
        psi.ArgumentList.Add(arg);

    using var process = Process.Start(psi)!;
    var stdout = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return process.ExitCode == 0 ? stdout : "";
}

record ApiEntry(string Symbol, string Signature);
