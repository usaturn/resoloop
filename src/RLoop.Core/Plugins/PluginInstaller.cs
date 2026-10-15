using System.Globalization;
using System.Text.RegularExpressions;

namespace RLoop.Core.Plugins;

public sealed record PluginInstallResult(string Name, string Version, PluginSource Source, string? Commit, string InstallDirectory,
    int FileCount);

public sealed record PluginRemoveResult(string Name, string RemovedDirectory, string Note);

/// <summary>Installs plugins into .resoloop/plugins/NAME without running plugin code, and removes exactly that directory.</summary>
public static partial class PluginInstaller
{
    public const int MaxFiles = 10_000;
    public const long MaxTotalBytes = 200L * 1024 * 1024;

    public static async Task<PluginInstallResult> InstallAsync(PluginRegistry registry, string source, string? gitRef,
        CancellationToken cancellationToken = default)
    {
        var kind = Classify(source, gitRef);
        Directory.CreateDirectory(registry.PluginsDirectory);
        // Everything is assembled in a staging directory and renamed into place last, so a failure leaves no plugin behind.
        var staging = Path.Combine(registry.PluginsDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string sourceRoot;
            IReadOnlyList<string> files;
            PluginSource pluginSource;
            string? commit = null;
            if (kind == PluginSource.DirectoryKind)
            {
                sourceRoot = Path.GetFullPath(source);
                files = await ListDirectoryAsync(sourceRoot, cancellationToken);
                pluginSource = new PluginSource(kind, sourceRoot, null);
            }
            else
            {
                sourceRoot = Path.Combine(staging, "checkout");
                (commit, files) = await PluginGit.CheckoutAsync(source, gitRef!, sourceRoot, cancellationToken);
                pluginSource = new PluginSource(kind, source, gitRef);
            }
            CheckFiles(sourceRoot, files);
            var manifest = PluginManifest.Load(sourceRoot, files.ToHashSet(StringComparer.Ordinal));
            manifest.EnsureCompatibleWith(registry.ResoloopVersion);
            EnsureInstallable(registry, manifest);
            var hashes = Copy(sourceRoot, files, Path.Combine(staging, PluginRegistry.PackageDirectoryName));
            if (kind == PluginSource.GitKind) DeleteTree(sourceRoot);
            new PluginInstallRecord(PluginInstallRecord.CurrentSchemaVersion, manifest.Name, manifest.Version.Text, pluginSource, commit,
                    DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), registry.ResoloopVersion, hashes)
                .Write(Path.Combine(staging, PluginInstallRecord.FileName));
            var target = Path.Combine(registry.PluginsDirectory, manifest.Name);
            try { Directory.Move(staging, target); }
            catch (IOException ex) when (Directory.Exists(target) || File.Exists(target))
            {
                throw AlreadyInstalled(manifest.Name, ex);
            }
            return new PluginInstallResult(manifest.Name, manifest.Version.Text, pluginSource, commit, target, hashes.Count);
        }
        finally
        {
            if (Directory.Exists(staging)) DeleteTree(staging);
        }
    }

    public static PluginRemoveResult Remove(PluginRegistry registry, string name)
    {
        if (!PluginNames.IsValid(name))
            throw PluginErrors.Create(PluginErrors.NameInvalid, $"'{name}' is not a plugin name ({PluginNames.Rule}).");
        var target = Path.Combine(registry.PluginsDirectory, name);
        var directory = new DirectoryInfo(target);
        if (directory.LinkTarget is not null || File.Exists(target))
            throw PluginErrors.Create(PluginErrors.RemoveRefused,
                $"{target} is not a real directory; resoloop removes only plugin directories that it created.",
                new Dictionary<string, object?> { ["path"] = target }, ["Inspect the entry and remove it by hand."]);
        if (!directory.Exists)
            throw PluginErrors.Create(PluginErrors.NotInstalled, $"Plugin '{name}' is not installed in {registry.ProjectRoot}.",
                new Dictionary<string, object?> { ["plugin"] = name }, ["Run resoloop plugin list --json."]);
        DeleteTree(target);
        return new PluginRemoveResult(name, target,
            "Only the install directory was removed. Files distributed outside it (for example skills) were left in place.");
    }

    /// <summary>Deletes a directory tree without following links: a link is removed, never the files it points to.</summary>
    internal static void DeleteTree(string path)
    {
        var directory = new DirectoryInfo(path);
        foreach (var entry in directory.EnumerateFileSystemInfos("*", PluginFiles.Shallow))
        {
            if (entry.LinkTarget is not null)
            {
                if (entry is DirectoryInfo && OperatingSystem.IsWindows()) Directory.Delete(entry.FullName);
                else File.Delete(entry.FullName);
            }
            else if (entry is DirectoryInfo) DeleteTree(entry.FullName);
            else
            {
                // git marks object files read-only on Windows.
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
                entry.Delete();
            }
        }
        directory.Delete();
    }

    internal static string Classify(string source, string? gitRef)
    {
        if (Directory.Exists(source))
        {
            if (gitRef is not null)
                throw SourceInvalid("--ref applies only to Git URLs; a local directory is copied as it is.", source);
            return PluginSource.DirectoryKind;
        }
        if (!GitUrlPattern().IsMatch(source))
            throw PluginErrors.Create(PluginErrors.SourceNotFound, $"'{source}' is neither an existing directory nor a Git URL.",
                new Dictionary<string, object?> { ["source"] = source },
                ["Pass a plugin directory, or a Git URL (https://, ssh://, git@host:path, file://) with --ref."]);
        if (gitRef is null)
            throw PluginErrors.Create(PluginErrors.GitRefRequired, "Installing from a Git URL requires --ref TAG|BRANCH|COMMIT.",
                suggestions: ["Pin the release you want, for example --ref v0.1.0."]);
        if (!RefPattern().IsMatch(gitRef) || gitRef.Contains("..", StringComparison.Ordinal))
            throw SourceInvalid($"--ref '{gitRef}' is not a tag, branch or commit name.", null);
        // Never record a token in install.json or in an error message.
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length > 0)
            throw SourceInvalid("Git URLs must not contain user names or tokens; use a git credential helper instead.", null);
        return PluginSource.GitKind;
    }

    private static async Task<IReadOnlyList<string>> ListDirectoryAsync(string root, CancellationToken cancellationToken)
    {
        var listed = await PluginGit.ListWorkTreeFilesAsync(root, cancellationToken);
        if (listed is null)
        {
            var (files, links) = PluginFiles.Scan(root, relative => Path.GetFileName(relative) == ".git");
            RefuseLinks(root, links);
            return files;
        }
        var result = new List<string>();
        var refused = new List<string>();
        foreach (var file in listed)
        {
            var info = new FileInfo(Path.Combine(root, file));
            if (info.LinkTarget is not null || Directory.Exists(info.FullName)) refused.Add(file); // symlink, submodule or nested repository
            else if (info.Exists) result.Add(file); // tracked files deleted from the work tree are skipped
        }
        RefuseLinks(root, refused);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static void RefuseLinks(string root, IReadOnlyList<string> links)
    {
        if (links.Count > 0)
            throw SourceInvalid($"The plugin contains symlinks or nested repositories, which plugins cannot use: {string.Join(", ", links.Take(5))}",
                root, links);
    }

    private static void CheckFiles(string root, IReadOnlyList<string> files)
    {
        var unsafePaths = files.Where(file => !PluginManifest.IsSafeRelativePath(file)).ToArray();
        if (unsafePaths.Length > 0)
            throw SourceInvalid($"These paths cannot be installed portably: {string.Join(", ", unsafePaths.Take(5))}", root, unsafePaths);
        if (files.Count > MaxFiles) throw SourceInvalid($"The plugin has {files.Count} files; at most {MaxFiles} are allowed.", root);
        var total = files.Sum(file => new FileInfo(Path.Combine(root, file)).Length);
        if (total > MaxTotalBytes) throw SourceInvalid($"The plugin has {total} bytes; at most {MaxTotalBytes} are allowed.", root);
    }

    private static void EnsureInstallable(PluginRegistry registry, PluginManifest manifest)
    {
        var statuses = registry.List();
        if (statuses.FirstOrDefault(status => !status.Ok) is { Problem: { } problem } broken)
            throw PluginErrors.Create(problem.Code, $"Cannot install while plugin '{broken.Name}' is unusable: {problem.Message}",
                new Dictionary<string, object?>(problem.Details) { ["plugin"] = broken.Name }, problem.Suggestions);
        var target = Path.Combine(registry.PluginsDirectory, manifest.Name);
        if (statuses.Any(status => status.Name == manifest.Name) || Directory.Exists(target) || File.Exists(target))
            throw AlreadyInstalled(manifest.Name);
        var conflicts = PluginNames.FindConflicts(manifest, statuses.Select(status => status.Manifest!));
        if (conflicts.Count > 0)
            throw PluginErrors.Create(PluginErrors.NameConflict, $"Plugin '{manifest.Name}' cannot be installed: {conflicts[0]}",
                new Dictionary<string, object?> { ["plugin"] = manifest.Name, ["conflicts"] = conflicts },
                ["Rename the conflicting commands, skills or recipes in the plugin, or remove the other plugin."]);
    }

    private static SortedDictionary<string, string> Copy(string sourceRoot, IReadOnlyList<string> files, string package)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var target = Path.Combine(package, file.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(sourceRoot, file.Replace('/', Path.DirectorySeparatorChar)), target);
            }
            catch (IOException ex)
            {
                throw SourceInvalid($"Cannot copy '{file}' (two paths may differ only in case): {ex.Message}", sourceRoot);
            }
            hashes[file] = PluginFiles.Sha256(target);
        }
        return hashes;
    }

    private static RLoopException SourceInvalid(string message, string? source, IReadOnlyList<string>? paths = null) =>
        PluginErrors.Create(PluginErrors.SourceInvalid, message,
            new Dictionary<string, object?> { ["source"] = source, ["paths"] = paths });

    private static RLoopException AlreadyInstalled(string name, Exception? innerException = null) =>
        PluginErrors.Create(PluginErrors.AlreadyInstalled, $"Plugin '{name}' is already installed.",
            new Dictionary<string, object?> { ["plugin"] = name },
            [$"To replace it, run resoloop plugin remove {name} --yes, then install again."], innerException);

    [GeneratedRegex(@"^(?:(?:https?|ssh|git|file)://\S+|[A-Za-z0-9][A-Za-z0-9._-]*@[A-Za-z0-9][A-Za-z0-9._-]*:\S+)\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GitUrlPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._/][A-Za-z0-9._/-]{0,199}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RefPattern();
}
