using System.Diagnostics;

namespace Tenninety.Execution;

internal static class ChildProcessEnvironment
{
    private static readonly string[] Allowlist =
    [
        "PATH", "HOME", "LANG", "LC_ALL", "TERM", "USER", "LOGNAME", "TMPDIR",
        "SSL_CERT_FILE", "SSL_CERT_DIR", "XDG_DATA_HOME", "XDG_CONFIG_HOME",
    ];

    public static void ApplyAllowlist(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Clear();
        foreach (var key in Allowlist)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value)) startInfo.Environment[key] = value;
        }
    }
}
