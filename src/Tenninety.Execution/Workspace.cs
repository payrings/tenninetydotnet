using FrontierClient = Tenninety.Frontier.HttpFrontierClient;
using MockFrontierClient = Tenninety.Frontier.MockFrontierClient;
using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Stores;
using Tenninety.Git;

namespace Tenninety.Execution;

/// <summary>
/// Bundles the on-disk contracts for a workspace (current directory + .tenninety/) and is shared
/// by the CLI commands and the TUI host.
/// </summary>
public sealed class Workspace
{
    public string Root { get; }
    public TenNinetyConfig Config { get; }
    public PlanStore Plans { get; }
    public StateStore States { get; }
    public ConfigStore Configs { get; }
    public AuditLog Audit { get; }
    public IGitService Git { get; }

    private Workspace(string root, TenNinetyConfig config)
    {
        Root = root;
        Config = config;
        Plans = new PlanStore(Path.Combine(root, TenNinety.StateDir, TenNinety.PlanFile));
        States = new StateStore(Path.Combine(root, TenNinety.StateDir, TenNinety.StateFile));
        Configs = new ConfigStore(Path.Combine(root, TenNinety.StateDir, TenNinety.ConfigFile));
        Audit = new AuditLog(Path.Combine(root, TenNinety.StateDir, TenNinety.AuditFile));
        Git = new GitService(root);
    }

    public static Workspace Load()
    {
        var root = Directory.GetCurrentDirectory();
        var stateDir = Path.Combine(root, TenNinety.StateDir);
        if (!Directory.Exists(stateDir))
            throw new InvalidOperationException(
                $"no '{TenNinety.StateDir}/' directory found here. Run 'tenninety init' first.");
        var config = new ConfigStore(Path.Combine(stateDir, TenNinety.ConfigFile)).Load();
        return new Workspace(root, config);
    }

    public static Workspace Create()
    {
        var root = Directory.GetCurrentDirectory();
        var stateDir = Path.Combine(root, TenNinety.StateDir);
        Directory.CreateDirectory(stateDir);
        var runtimeIgnore = Path.Combine(stateDir, ".gitignore");
        if (!File.Exists(runtimeIgnore))
            File.WriteAllText(runtimeIgnore, RuntimeGitignoreMigration.Contents);
        var configStore = new ConfigStore(Path.Combine(stateDir, TenNinety.ConfigFile));
        var config = configStore.Exists() ? configStore.Load() : new TenNinetyConfig();
        return new Workspace(root, config);
    }

    public Plan LoadPlan()
    {
        if (!Plans.Exists())
            throw new InvalidOperationException(
                $"no '{TenNinety.PlanFile}' found. Run 'tenninety plan --spec ./spec.md' first.");
        var plan = Plans.Load();
        var validation = Core.Validation.PlanValidator.Validate(plan);
        if (!validation.IsValid)
            throw new InvalidOperationException("plan.json is invalid: " + string.Join("; ", validation.Errors));
        return plan;
    }

    public Tenninety.Frontier.IFrontierClient CreateFrontier()
    {
        // The Frontier is a separate concern from the local coding agents: any provider_mode
        // other than the offline rehearsal mode talks to the REAL configured frontier endpoint.
        var mock = Config.NormalizedProviderMode == "mock";
        return mock
            ? new MockFrontierClient()
            : new FrontierClient(new HttpClient { BaseAddress = new Uri(Config.FrontierEndpoint) }, Config);
    }

    public string SpecPath => Path.Combine(Root, TenNinety.SpecFile);
}
