namespace RLoop.Core;

/// <summary>Finds the local folder used by Resonite's generic OS screenshot exporter.</summary>
public static class ScreenshotDirectoryResolver
{
    private const string PlatformDirectory = "Resonite";
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    public static string ResolveDefault(
        string? picturesDirectory = null,
        string? userProfile = null,
        Func<string, string?>? getEnvironment = null)
    {
        getEnvironment ??= Environment.GetEnvironmentVariable;
        picturesDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidates = new HashSet<string>(PathComparer);
        // GetFolderPath returns "" when the known folder is missing (Linux without ~/Pictures). Combining it would
        // yield the relative path "Resonite" under the working directory, which Resonite never writes to.
        string? primary = null;
        if (!string.IsNullOrWhiteSpace(picturesDirectory))
        {
            primary = Path.GetFullPath(Path.Combine(picturesDirectory, PlatformDirectory));
            candidates.Add(primary);
        }

        if (OperatingSystem.IsWindows())
        {
            AddOneDriveCandidates(candidates, getEnvironment("OneDriveConsumer"));
            AddOneDriveCandidates(candidates, getEnvironment("OneDrive"));

            if (Directory.Exists(userProfile))
            {
                try
                {
                    foreach (var root in Directory.EnumerateDirectories(userProfile, "OneDrive*", SearchOption.TopDirectoryOnly))
                        AddOneDriveCandidates(candidates, root);
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }

        // Resonite creates the primary path on first export. Prefer an existing candidate
        // with the newest exported image so stale known-folder redirection does not win.
        var active = candidates
            .Select(path => (Path: path, Latest: LatestImageWriteTime(path)))
            .Where(candidate => candidate.Latest is not null)
            .OrderByDescending(candidate => candidate.Latest)
            .FirstOrDefault();
        return active.Path ?? primary ?? throw Unreachable(picturesDirectory, candidates);
    }

    // Shared with ScreenshotExport so every failure about the export folder gives the same next steps.
    internal static readonly string[] ExportFolderSuggestions =
    [
        "Set --screenshots-dir, RESOLOOP_SCREENSHOTS_DIR or screenshotsDirectory to a folder this CLI can read.",
        "If Resonite runs on another machine or outside this container (for example a Windows host with a Linux dev container), mount its Pictures/Resonite folder and point the setting at the mount."
    ];

    private static RLoopException Unreachable(string picturesDirectory, IEnumerable<string> candidates) => new(
        "CAPTURE_EXPORT_DIR_UNREACHABLE",
        "No screenshot export folder is readable from this CLI: the Pictures known folder is unavailable and no screenshot directory is configured.",
        ExitCodes.ConfigurationError,
        new Dictionary<string, object?>
        {
            ["picturesFolder"] = picturesDirectory,
            ["candidates"] = candidates.Select(path => new ScreenshotDirectoryCandidate(path, Directory.Exists(path))).ToArray(),
            ["oneDriveCandidatesChecked"] = OperatingSystem.IsWindows()
        },
        ExportFolderSuggestions);

    private static void AddOneDriveCandidates(HashSet<string> candidates, string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        root = Path.GetFullPath(root);
        candidates.Add(Path.Combine(root, PlatformDirectory));
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                candidates.Add(Path.Combine(directory, PlatformDirectory));
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static DateTime? LatestImageWriteTime(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        try
        {
            DateTime? latest = null;
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (!ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                var writeTime = File.GetLastWriteTimeUtc(path);
                if (latest is null || writeTime > latest) latest = writeTime;
            }
            return latest;
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException) { return null; }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

/// <summary>A screenshot folder the default resolver considered, reported when none is usable.</summary>
public sealed record ScreenshotDirectoryCandidate(string Path, bool Exists);
