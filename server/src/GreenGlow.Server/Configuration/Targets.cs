using System.Text.RegularExpressions;

namespace GreenGlow.Server.Configuration;

/// <summary>An IBM i system the server may connect to. The browser only ever sees Name and Label.</summary>
/// <param name="MaxSessionsPerUser">Most host sessions one user may have open on it at once.</param>
/// <param name="IsTest">A test system, where automated sign-on tests may run.</param>
public sealed record Target(string Name, string Label, bool Secure, string Host, int Port, int MaxSessionsPerUser = 3, bool IsTest = false,
    string? Color = null, string? TrustedCaPem = null, string? TrustedName = null);

public sealed class ConfigException(string message) : Exception(message);

/// <summary>Checks connection names, URLs (scheme://host[:port]) and labels.</summary>
public static partial class TargetParser
{
    public const int MaxLabel = 40;

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex NamePattern();

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    /// <summary>One target URL: tls://host[:992] (verified TLS) or tcp://host[:23] (plain Telnet).</summary>
    public static Target ParseUrl(string name, string label, string spec)
    {
        if (!Uri.TryCreate(spec, UriKind.Absolute, out var url))
            throw new ConfigException($"Target \"{name}\": \"{spec}\" is not a URL");
        var (secure, defaultPort) = url.Scheme switch
        {
            "tls" => (true, 992),
            "tcp" => (false, 23),
            _ => throw new ConfigException($"Target \"{name}\": the scheme must be tls:// or tcp://, not {url.Scheme}://"),
        };
        if (string.IsNullOrEmpty(url.Host) || url.UserInfo != "" || url.Query != "" || url.Fragment != "")
            throw new ConfigException($"Target \"{name}\": use scheme://host[:port] with nothing else");
        if (url.AbsolutePath is not ("" or "/"))
            throw new ConfigException($"Target \"{name}\": use scheme://host[:port] with no path");
        var host = url.Host.Trim('[', ']');
        return new Target(name, ValidLabel(name, label), secure, host, url.IsDefaultPort || url.Port < 0 ? defaultPort : url.Port);
    }

    public static string ValidLabel(string name, string label)
    {
        if (label.Length == 0 || label.Length > MaxLabel || label.Any(char.IsControl))
            throw new ConfigException($"The label for \"{name}\" must be 1 to {MaxLabel} printable characters");
        return label;
    }
}
