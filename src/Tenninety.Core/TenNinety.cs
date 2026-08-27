namespace Tenninety.Core;

public static class TenNinety
{
    public const string SchemaVersion = "3.2";
    public const string StateDir = ".tenninety";
    public const string SpecFile = "spec.md";
    public const string PlanFile = "plan.json";
    public const string StateFile = "state.json";
    public const string ConfigFile = "config.json";
    public const string AuditFile = "audit-log.jsonl";
    public const string WorkBranchPrefix = "work/";
    public const string HotfixBranchPrefix = "hotfix/";
    public const string MainBranch = "main";

    public static class WpStatus
    {
        public const string Pending = "PENDING";
        public const string Active = "ACTIVE";
        public const string Done = "DONE";
        public const string Blocked = "BLOCKED";
        public const string Cancelled = "CANCELLED";
    }

    public static class FailureTypes
    {
        public const string Coder = "coder";
        public const string Reviewer = "reviewer";
        public const string Tester = "tester";
    }

    /// <summary>
    /// Layer ordering per the v3.2 Enterprise blueprint: L0 INFRA → L1 DOMAIN → L2 DATA →
    /// L3 APP → L4 API/PRESENTATION (incl. UI) → L5 TEST. A lower layer must never depend
    /// on a higher one; unknown layers are skipped by the rank check.
    /// </summary>
    public static readonly Dictionary<string, int> LayerRanks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["INFRA"] = 0,
        ["DOMAIN"] = 1,
        ["DATA"] = 2,
        ["APP"] = 3,
        ["API"] = 4,
        ["UI"] = 4,
        ["TEST"] = 5,
        ["TEST-INTEGRATION"] = 5,
        ["TEST-E2E"] = 5,
        ["UI-INFRA"] = 4,
        ["UI-COMPONENT"] = 4,
        ["UI-VIEW"] = 4,
        ["UI-SERVICE"] = 4,
        ["UI-TEST"] = 5,
    };

    public static string Resolve(params string[] relativeParts) =>
        Path.Combine(new[] { Directory.GetCurrentDirectory(), StateDir }.Concat(relativeParts).ToArray());
}
