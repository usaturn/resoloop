namespace RLoop.Core;

// Resonite opens texture and audio import paths on its own host (ImportTexture2DFile / ImportAudioClipFile),
// while mesh data is read by the CLI and sent. This rewrites a CLI path prefix into one that host can read.
public sealed class HostPathMap
{
    private static readonly char[] Separators = ['/', '\\'];

    private HostPathMap(string from, string to, string? source) => (From, To, Source) = (from, to, source);

    public string From { get; }
    public string To { get; }
    public string? Source { get; }

    public static bool AppliesTo(string kind) => kind.ToLowerInvariant() is "texture" or "texture2d" or "audio" or "audioclip";

    public static HostPathMap? Parse(string? value, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var separator = value.IndexOf('=');
        var from = separator > 0 ? value[..separator] : string.Empty;
        var to = separator >= 0 ? value[(separator + 1)..] : string.Empty;
        if (!Path.IsPathFullyQualified(from) || string.IsNullOrWhiteSpace(to))
            throw new RLoopException("INVALID_HOST_PATH_MAP",
                $"Host path map must be FROM=TO with an absolute CLI path prefix FROM, got '{value}'.", ExitCodes.ConfigurationError,
                new Dictionary<string, object?> { ["value"] = value, ["source"] = source },
                [@"Example: /workspaces/repo=\\wsl.localhost\<distro>\home\<user>\repo"]);
        return new HostPathMap(from, to, source);
    }

    public bool Covers(string path) => Remainder(path) is not null;

    public string Map(string path)
    {
        if (Remainder(path) is not { } remainder) return path;
        var separator = To.Contains('\\') || To.Length >= 2 && To[1] == ':' ? '\\' : '/';
        return To.TrimEnd(Separators) +
               remainder.Replace(Path.DirectorySeparatorChar, separator).Replace(Path.AltDirectorySeparatorChar, separator);
    }

    public override string ToString() => From + "=" + To;

    private string? Remainder(string path)
    {
        var prefix = From.TrimEnd(Separators);
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return null;
        if (path.Length == prefix.Length) return string.Empty;
        var next = path[prefix.Length];
        return next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar ? path[prefix.Length..] : null;
    }
}
