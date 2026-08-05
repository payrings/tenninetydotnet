#nullable enable

using System;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;

// Compares the resolved public/protected API of complete old and new project
// compilations. Syntax-only, changed-file comparison is insufficient: a global
// alias, nullable directive, generated member or project-wide using can change
// an unchanged declaration's effective contract.

var arguments = args.ToArray();
var isStaged = arguments.Contains("--staged");
var isLastCommit = arguments.Contains("--last-commit");
var namesOnly = arguments.Contains("--names-only");
var sinceIndex = Array.IndexOf(arguments, "--since");
var sinceRef = sinceIndex >= 0 && sinceIndex + 1 < arguments.Length
    ? arguments[sinceIndex + 1]
    : null;

if (new[] { isStaged, isLastCommit, sinceRef is not null }.Count(value => value) != 1)
    Fail("Usage: check_signatures.sh --staged | --last-commit | --since <ref> [--names-only]");

if (isStaged)
    RequireGitRef("HEAD");
else if (isLastCommit)
{
    RequireGitRef("HEAD");
    RequireGitRef("HEAD^");
}
else
    RequireGitRef(sinceRef!);

var oldVersion = isLastCommit ? "HEAD^" : isStaged ? "HEAD" : sinceRef!;
var newVersion = isLastCommit ? "HEAD" : isStaged ? ":INDEX:" : ":WORKTREE:";
var temporaryRoot = Path.Combine(Path.GetTempPath(), $"tenninety-api-{Guid.NewGuid():N}");
var oldRoot = Path.Combine(temporaryRoot, "old");
var newRoot = Path.Combine(temporaryRoot, "new");

var ApiDisplayFormat = new SymbolDisplayFormat(
    globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
    typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters |
                     SymbolDisplayGenericsOptions.IncludeTypeConstraints |
                     SymbolDisplayGenericsOptions.IncludeVariance,
    memberOptions: SymbolDisplayMemberOptions.IncludeParameters |
                   SymbolDisplayMemberOptions.IncludeContainingType |
                   SymbolDisplayMemberOptions.IncludeType |
                   SymbolDisplayMemberOptions.IncludeAccessibility |
                   SymbolDisplayMemberOptions.IncludeModifiers |
                   SymbolDisplayMemberOptions.IncludeConstantValue |
                   SymbolDisplayMemberOptions.IncludeRef,
    delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
    parameterOptions: SymbolDisplayParameterOptions.IncludeType |
                      SymbolDisplayParameterOptions.IncludeName |
                      SymbolDisplayParameterOptions.IncludeDefaultValue |
                      SymbolDisplayParameterOptions.IncludeParamsRefOut |
                      SymbolDisplayParameterOptions.IncludeExtensionThis,
    propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
    kindOptions: SymbolDisplayKindOptions.IncludeTypeKeyword |
                 SymbolDisplayKindOptions.IncludeMemberKeyword,
    miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                          SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                          SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

var NameDisplayFormat = SymbolDisplayFormat.FullyQualifiedFormat
    .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
    .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

var TypeDisplayFormat = SymbolDisplayFormat.FullyQualifiedFormat
    .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
    .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                              SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                              SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

Directory.CreateDirectory(oldRoot);
Directory.CreateDirectory(newRoot);

try
{
    Materialize(oldVersion, oldRoot);
    Materialize(newVersion, newRoot);

    // Register the installed SDK before MSBuildWorkspace loads any MSBuild
    // assemblies. Both snapshots use the repository's global.json.
    RegisterMSBuild();

    var oldApi = await ReadSolutionApi(oldRoot);
    var newApi = await ReadSolutionApi(newRoot);

    var changes = oldApi.Keys
        .Except(newApi.Keys, StringComparer.Ordinal)
        .Select(key => oldApi[key])
        .Concat(newApi.Keys
            .Except(oldApi.Keys, StringComparer.Ordinal)
            .Select(key => newApi[key]))
        .DistinctBy(entry => entry.Signature)
        .OrderBy(entry => entry.Symbol, StringComparer.Ordinal)
        .ThenBy(entry => entry.Signature, StringComparer.Ordinal)
        .ToList();

    if (isStaged && changes.Count > 0)
    {
        var architectureStaged = GitLines("diff", "--cached", "--name-only")
            .Contains(".agent/rules/architecture.md", StringComparer.Ordinal);
        if (!architectureStaged)
        {
            Console.WriteLine("BLOCK: a resolved public C# API changed without .agent/rules/architecture.md in the same commit.");
            Console.WriteLine("Changed API entries:");
            foreach (var change in changes)
                Console.WriteLine($"  {change.Signature}");
            Environment.Exit(1);
        }
    }

    if (namesOnly)
    {
        foreach (var symbol in changes.Select(entry => entry.Symbol).Distinct().OrderBy(value => value, StringComparer.Ordinal))
            Console.WriteLine($"API\t{symbol}");
    }
    else if (!isStaged)
    {
        foreach (var change in changes)
            Console.WriteLine(change.Signature);
    }
}
finally
{
    try { Directory.Delete(temporaryRoot, recursive: true); }
    catch { /* best-effort cleanup of an external temporary directory */ }
}

async Task<Dictionary<string, ApiEntry>> ReadSolutionApi(string root)
{
    var sourceRoot = Path.Combine(root, "src");
    if (!Directory.Exists(sourceRoot))
        Fail($"Snapshot has no src directory: {root}");

    var projectPaths = Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .ToList();
    if (projectPaths.Count == 0)
        Fail($"Snapshot has no C# project under src/: {root}");

    // Restore evaluates only committed/staged trusted build inputs. Automatic
    // response files are disabled consistently with the test runners.
    foreach (var projectPath in projectPaths)
    {
        var restore = RunProcess(
            "dotnet",
            new[] { "restore", projectPath, "--locked-mode", "-noAutoResponse" },
            root);
        if (restore.ExitCode != 0)
            FailProcess($"Could not restore API snapshot project '{Relative(root, projectPath)}'.", restore);
    }

    var result = new Dictionary<string, ApiEntry>(StringComparer.Ordinal);
    foreach (var projectPath in projectPaths)
    {
        var targetFrameworks = ReadTargetFrameworks(root, projectPath);
        foreach (var targetFramework in targetFrameworks)
        {
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(targetFramework))
                properties["TargetFramework"] = targetFramework;
            using var workspace = MSBuildWorkspace.Create(properties);
            var workspaceFailures = new ConcurrentQueue<string>();
            using var workspaceFailureRegistration = workspace.RegisterWorkspaceFailedHandler(eventArgs =>
            {
                if (eventArgs.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                    workspaceFailures.Enqueue(eventArgs.Diagnostic.Message);
            });

            var project = await workspace.OpenProjectAsync(projectPath);
            var projectKey = Relative(root, projectPath) +
                (string.IsNullOrEmpty(targetFramework) ? "" : $"@{targetFramework}");
            if (workspaceFailures.Count > 0)
                Fail($"MSBuild failed to load '{projectKey}':\n  {string.Join("\n  ", workspaceFailures)}");

            var compilation = await project.GetCompilationAsync();
            if (compilation is null)
                Fail($"MSBuild produced no compilation for '{projectKey}'.");

            var errors = compilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Take(20)
                .ToList();
            if (errors.Count > 0)
                Fail($"Compilation errors prevent a trustworthy API comparison for '{projectKey}':\n  {string.Join("\n  ", errors)}");

            foreach (var entry in ExtractAssemblyApi(compilation.Assembly, projectKey))
                result[$"{projectKey}\0{entry.Signature}"] = entry;
        }
    }
    return result;
}

void RegisterMSBuild()
{
    if (MSBuildLocator.IsRegistered)
        return;
    if (!MSBuildLocator.CanRegister)
        Fail("Microsoft.Build assemblies loaded before MSBuildLocator registration.");
    MSBuildLocator.RegisterDefaults();
}

List<string> ReadTargetFrameworks(string root, string projectPath)
{
    var plural = ReadMsBuildProperty(root, projectPath, "TargetFrameworks");
    if (!string.IsNullOrWhiteSpace(plural))
        return plural.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    var single = ReadMsBuildProperty(root, projectPath, "TargetFramework");
    return string.IsNullOrWhiteSpace(single) ? new List<string> { "" } : new List<string> { single };
}

string ReadMsBuildProperty(string root, string projectPath, string property)
{
    var query = RunProcess(
        "dotnet",
        new[] { "msbuild", projectPath, "-nologo", "-noAutoResponse", $"-getProperty:{property}" },
        root);
    if (query.ExitCode != 0)
        FailProcess($"Could not query MSBuild property '{property}' for '{Relative(root, projectPath)}'.", query);
    var lines = query.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (lines.Length > 1)
        Fail($"MSBuild returned ambiguous '{property}' output for '{Relative(root, projectPath)}':\n  {string.Join("\n  ", lines)}");
    return lines.SingleOrDefault() ?? "";
}

IEnumerable<ApiEntry> ExtractAssemblyApi(IAssemblySymbol assembly, string projectKey)
{
    var entries = new List<ApiEntry>();

    void VisitNamespace(INamespaceSymbol namespaceSymbol)
    {
        foreach (var child in namespaceSymbol.GetNamespaceMembers())
            VisitNamespace(child);
        foreach (var type in namespaceSymbol.GetTypeMembers())
            VisitType(type);
    }

    void VisitType(INamedTypeSymbol type)
    {
        if (!IsExternallyVisible(type))
            return;

        entries.Add(ToEntry(type, projectKey));
        foreach (var member in type.GetMembers())
        {
            if (member is INamedTypeSymbol nested)
            {
                VisitType(nested);
                continue;
            }
            if (!IsApiMember(member))
                continue;
            entries.Add(ToEntry(member, projectKey));
        }
    }

    VisitNamespace(assembly.GlobalNamespace);
    return entries;
}

bool IsExternallyVisible(INamedTypeSymbol type)
{
    if (!IsExternalAccessibility(type.DeclaredAccessibility))
        return false;
    return type.ContainingType is null || IsExternallyVisible(type.ContainingType);
}

bool IsApiMember(ISymbol symbol)
{
    if (!IsExternalAccessibility(symbol.DeclaredAccessibility))
        return false;
    if (symbol is IMethodSymbol method && method.MethodKind is
        MethodKind.PropertyGet or MethodKind.PropertySet or
        MethodKind.EventAdd or MethodKind.EventRemove or
        MethodKind.StaticConstructor or MethodKind.Destructor or
        MethodKind.LocalFunction or MethodKind.AnonymousFunction)
        return false;
    return symbol is IMethodSymbol or IPropertySymbol or IEventSymbol or IFieldSymbol;
}

bool IsExternalAccessibility(Accessibility accessibility) => accessibility is
    Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;

ApiEntry ToEntry(ISymbol symbol, string projectKey)
{
    var symbolName = SymbolName(symbol);
    // SymbolDisplayFormat historically omits some named-type accessibility and
    // modifiers even when the corresponding options are enabled. Keep those
    // ABI-relevant facts explicit instead of trusting presentation behavior.
    var signature = $"{projectKey}: {symbol.ToDisplayString(ApiDisplayFormat)}" +
        $" | accessibility:{symbol.DeclaredAccessibility}";

    if (symbol is INamedTypeSymbol type)
    {
        signature += $" | type-shape:{type.TypeKind};static={type.IsStatic};" +
            $"abstract={type.IsAbstract};sealed={type.IsSealed};" +
            $"readonly={type.IsReadOnly};ref-like={type.IsRefLikeType};record={type.IsRecord}";
        if (type.EnumUnderlyingType is not null)
            signature += $";enum-underlying={TypeFingerprint(type.EnumUnderlyingType)}";

        var bases = new List<string>();
        if (type.BaseType is not null && type.BaseType.SpecialType != SpecialType.System_Object)
            bases.Add(TypeFingerprint(type.BaseType));
        bases.AddRange(type.Interfaces.Select(TypeFingerprint));
        if (bases.Count > 0)
            signature += " : " + string.Join(", ", bases.Order(StringComparer.Ordinal));
    }

    var nullability = NullabilityFingerprint(symbol);
    if (nullability.Length > 0)
        signature += $" | nullability:{nullability}";

    var attributes = ApiAttributeFingerprints(symbol)
        .Order(StringComparer.Ordinal)
        .ToList();
    if (attributes.Count > 0)
        signature += " | attributes:" + string.Join(",", attributes);

    return new ApiEntry(symbolName, signature);
}

IEnumerable<string> ApiAttributeFingerprints(ISymbol symbol)
{
    var entries = new List<string>();
    void Add(string location, IEnumerable<AttributeData> attributes) =>
        entries.AddRange(attributes
            .Where(attribute => attribute.AttributeClass is not null)
            .Select(attribute => $"{location}:{AttributeFingerprint(attribute)}"));

    Add("symbol", symbol.GetAttributes());
    switch (symbol)
    {
        case IMethodSymbol method:
            Add("return", method.GetReturnTypeAttributes());
            foreach (var parameter in method.Parameters)
                Add($"parameter[{parameter.Ordinal}]", parameter.GetAttributes());
            foreach (var parameter in method.TypeParameters)
                Add($"type-parameter[{parameter.Ordinal}]", parameter.GetAttributes());
            break;
        case IPropertySymbol property:
            foreach (var parameter in property.Parameters)
                Add($"parameter[{parameter.Ordinal}]", parameter.GetAttributes());
            if (property.GetMethod is not null) Add("getter", property.GetMethod.GetAttributes());
            if (property.SetMethod is not null) Add("setter", property.SetMethod.GetAttributes());
            break;
        case IEventSymbol @event:
            if (@event.AddMethod is not null) Add("add", @event.AddMethod.GetAttributes());
            if (@event.RemoveMethod is not null) Add("remove", @event.RemoveMethod.GetAttributes());
            break;
        case INamedTypeSymbol type:
            foreach (var parameter in type.TypeParameters)
                Add($"type-parameter[{parameter.Ordinal}]", parameter.GetAttributes());
            break;
    }
    return entries;
}

string SymbolName(ISymbol symbol)
{
    if (symbol is INamedTypeSymbol type)
        return type.ToDisplayString(NameDisplayFormat);
    var container = symbol.ContainingType?.ToDisplayString(NameDisplayFormat) ?? "";
    return string.IsNullOrEmpty(container) ? symbol.MetadataName : $"{container}.{symbol.MetadataName}";
}

string NullabilityFingerprint(ISymbol symbol) => symbol switch
{
    IMethodSymbol method =>
        $"return={TypeFingerprint(method.ReturnType)};" +
        string.Join(";", method.Parameters.Select(parameter =>
            $"{parameter.Ordinal}:{TypeFingerprint(parameter.Type)}")),
    IPropertySymbol property =>
        $"type={TypeFingerprint(property.Type)};get={property.GetMethod?.DeclaredAccessibility};" +
        $"set={property.SetMethod?.DeclaredAccessibility};" +
        string.Join(";", property.Parameters.Select(parameter =>
            $"{parameter.Ordinal}:{TypeFingerprint(parameter.Type)}")),
    IEventSymbol @event => $"type={TypeFingerprint(@event.Type)}",
    IFieldSymbol field => $"type={TypeFingerprint(field.Type)}",
    INamedTypeSymbol type => string.Join(";", type.TypeParameters.Select(parameter =>
        $"{parameter.Ordinal}:{parameter.NullableAnnotation}")),
    _ => ""
};

string TypeFingerprint(ITypeSymbol type)
{
    var nested = type switch
    {
        IArrayTypeSymbol array => $"[{TypeFingerprint(array.ElementType)}]",
        IPointerTypeSymbol pointer => $"*{TypeFingerprint(pointer.PointedAtType)}",
        INamedTypeSymbol named when named.TypeArguments.Length > 0 =>
            "<" + string.Join(",", named.TypeArguments.Select(TypeFingerprint)) + ">",
        _ => ""
    };
    return $"{type.ToDisplayString(TypeDisplayFormat)}#{type.NullableAnnotation}{nested}";
}

string AttributeFingerprint(AttributeData attribute)
{
    var name = attribute.AttributeClass!.ToDisplayString(NameDisplayFormat);
    var constructor = string.Join(",", attribute.ConstructorArguments.Select(TypedConstantFingerprint));
    var named = string.Join(",", attribute.NamedArguments
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => $"{pair.Key}={TypedConstantFingerprint(pair.Value)}"));
    return $"{name}({constructor}){{{named}}}";
}

string TypedConstantFingerprint(TypedConstant constant)
{
    if (constant.IsNull)
        return "null";
    if (constant.Kind == TypedConstantKind.Array)
        return "[" + string.Join(",", constant.Values.Select(TypedConstantFingerprint)) + "]";
    return $"{constant.Type?.ToDisplayString(TypeDisplayFormat)}:{constant.Value}";
}

void Materialize(string version, string destination)
{
    if (version == ":INDEX:")
    {
        var prefix = destination.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var checkout = RunGit("checkout-index", "--all", "--force", $"--prefix={prefix}");
        if (checkout.ExitCode != 0)
            FailProcess("Could not materialize the Git index.", checkout);
        return;
    }

    if (version == ":WORKTREE:")
    {
        var root = Directory.GetCurrentDirectory();
        foreach (var relative in GitNullPaths("ls-files", "-z", "--cached", "--others", "--exclude-standard"))
        {
            var source = Path.GetFullPath(Path.Combine(root, relative));
            if (!source.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                Fail($"Git returned an unsafe path: {relative}");
            if (!File.Exists(source))
                continue;
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
        return;
    }

    MaterializeCommit(version, destination);
}

void MaterializeCommit(string reference, string destination)
{
    var archiveInfo = GitProcessInfo("archive", "--format=tar", reference);
    archiveInfo.RedirectStandardOutput = true;
    var extractInfo = new ProcessStartInfo("tar")
    {
        RedirectStandardInput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    foreach (var argument in new[] { "-xf", "-", "-C", destination })
        extractInfo.ArgumentList.Add(argument);

    using var archive = Process.Start(archiveInfo)!;
    using var extract = Process.Start(extractInfo)!;
    var archiveErrors = archive.StandardError.ReadToEndAsync();
    var extractErrors = extract.StandardError.ReadToEndAsync();
    archive.StandardOutput.BaseStream.CopyTo(extract.StandardInput.BaseStream);
    extract.StandardInput.Close();
    archive.WaitForExit();
    extract.WaitForExit();
    if (archive.ExitCode != 0 || extract.ExitCode != 0)
        Fail($"Could not materialize Git ref '{reference}': " +
             $"{archiveErrors.GetAwaiter().GetResult()} {extractErrors.GetAwaiter().GetResult()}");
}

List<string> GitLines(params string[] arguments) => GitTextRequired(arguments)
    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToList();

List<string> GitNullPaths(params string[] arguments) => GitTextRequired(arguments)
    .Split('\0', StringSplitOptions.RemoveEmptyEntries)
    .ToList();

void RequireGitRef(string reference)
{
    var result = RunGit("rev-parse", "--verify", "--quiet", $"{reference}^{{commit}}");
    if (result.ExitCode != 0)
        FailProcess($"Git reference '{reference}' does not resolve to a commit.", result);
}

string GitTextRequired(params string[] arguments)
{
    var result = RunGit(arguments);
    if (result.ExitCode != 0)
        FailProcess($"git {string.Join(" ", arguments)} failed.", result);
    return result.Stdout;
}

ProcessResult RunGit(params string[] arguments) =>
    RunProcess("git", arguments, Directory.GetCurrentDirectory());

ProcessStartInfo GitProcessInfo(params string[] arguments)
{
    var info = new ProcessStartInfo("git")
    {
        RedirectStandardError = true,
        UseShellExecute = false
    };
    foreach (var argument in arguments)
        info.ArgumentList.Add(argument);
    return info;
}

ProcessResult RunProcess(string executable, IEnumerable<string> arguments, string workingDirectory)
{
    var info = new ProcessStartInfo(executable)
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    foreach (var argument in arguments)
        info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    return new ProcessResult(
        process.ExitCode,
        stdout.GetAwaiter().GetResult(),
        stderr.GetAwaiter().GetResult());
}

string Relative(string root, string path) =>
    Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

void FailProcess(string message, ProcessResult result) =>
    Fail($"{message}\n{result.Stderr.TrimEnd()}\n{result.Stdout.TrimEnd()}".TrimEnd());

[DoesNotReturn]
void Fail(string message)
{
    Console.Error.WriteLine($"ERROR: {message}");
    Environment.Exit(2);
}

record ApiEntry(string Symbol, string Signature);
record ProcessResult(int ExitCode, string Stdout, string Stderr);
