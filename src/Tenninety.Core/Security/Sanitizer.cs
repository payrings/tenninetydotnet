using System.Text.RegularExpressions;

namespace Tenninety.Core.Security;

/// <summary>
/// Part VI defense in depth for framework-built prompts. Redacts common API keys, tokens and
/// private keys; terminal coding agents still have workspace access and require sandboxing.
/// </summary>
public static partial class Sanitizer
{
    /// <summary>File patterns whose contents must never be included in model context.</summary>
    public static readonly string[] ExcludedFilePatterns =
    [
        ".env", "*.env", ".env.*", "*.pem", "*.key", "*.pfx", "*.p12",
        "secrets.json", "*secret*", "id_rsa*", "*.keystore",
    ];

    public static bool IsExcludedFile(string path)
    {
        var name = Path.GetFileName(path);
        return ExcludedFilePatterns.Any(pattern => GlobMatch(pattern, name));
    }

    /// <summary>Case-insensitive glob where '*' matches any sequence (including empty).</summary>
    private static bool GlobMatch(string pattern, string name) =>
        Regex.IsMatch(
            name,
            "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"\b(?:sk-[A-Za-z0-9_\-]{16,}|ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|AKIA[0-9A-Z]{16})\b")]
    private static partial Regex KnownTokenFormats();

    [GeneratedRegex(@"(?im)^\s*([A-Za-z_][A-Za-z0-9_]*(?:KEY|TOKEN|SECRET|PASSWORD|PASSWD)[A-Za-z0-9_]*)\s*=\s*(.+)$")]
    private static partial Regex EnvAssignment();

    [GeneratedRegex(@"(?i)\b((?:proxy-)?authorization|bearer)\s*[:=]?\s*(?:(?:basic|bearer)\s+)?[A-Za-z0-9._~+/=\-]{6,}")]
    private static partial Regex AuthHeader();

    [GeneratedRegex(@"(?i)([""']?(?:api[_\-]?key|(?:client[_\-]?)?secret|password|passwd|(?:access[_\-]?)?token)[""']?\s*[=:]\s*[""']?)([^\s"",;]{6,})([""']?)")]
    private static partial Regex InlineSecret();

    [GeneratedRegex(@"(?i)\b(A3F[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12})\b")]
    private static partial Regex AzureKey();

    public static string SanitizeText(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var text = input;
        text = PrivateKeyBlock().Replace(text, "[REDACTED:private-key]");
        text = EnvAssignment().Replace(text, m => $"{m.Groups[1].Value}=[REDACTED]");
        text = KnownTokenFormats().Replace(text, "[REDACTED:token]");
        text = AuthHeader().Replace(text, m => m.Groups[1].Value + "=[REDACTED]");
        // Keep the key, separator and surrounding quote while redacting only the value.
        text = InlineSecret().Replace(
            text, m => $"{m.Groups[1].Value}[REDACTED]{m.Groups[3].Value}");
        text = AzureKey().Replace(text, "[REDACTED:key]");
        return text;
    }

    /// <summary>
    /// Best-effort DETECTION of high-confidence secret material (private key blocks, known
    /// token formats such as sk-/ghp_/github_pat_/AKIA, and Azure key shapes). Used by the
    /// candidate promotion policy's bounded content scan: a detected secret rejects the whole
    /// candidate patch. Best-effort by design — it never claims completeness.
    /// </summary>
    public static bool ContainsLikelySecret(string input)
    {
        if (string.IsNullOrEmpty(input)) return false;
        return PrivateKeyBlock().IsMatch(input) ||
               KnownTokenFormats().IsMatch(input) ||
               AzureKey().IsMatch(input);
    }

    public static IEnumerable<string> FilterContextFiles(IEnumerable<string> paths) =>
        paths.Where(p => !IsExcludedFile(p));
}
