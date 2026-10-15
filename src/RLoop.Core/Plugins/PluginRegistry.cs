using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RLoop.Core.Plugins;

public sealed record PluginSource(string Kind, string Location, string? Ref)
{
    public const string DirectoryKind = "directory";
    public const string GitKind = "git";
}

/// <summary>install.json: where a plugin came from and the SHA-256 of every file copied into package/.</summary>
public sealed record PluginInstallRecord(
    int SchemaVersion,
    string Name,
    string Version,
    PluginSource Source,
    string? Commit,
    string InstalledAt,
    string ResoloopVersion,
    IReadOnlyDictionary<string, string> Files)
{
    public const string FileName = "install.json";
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static PluginInstallRecord Read(string path)
    {
        PluginInstallRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<PluginInstallRecord>(File.ReadAllBytes(path), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw Invalid(path, ex.Message, ex);
        }
        var problem = record is null ? "the record is empty." : record.Problem();
        if (problem is not null) throw Invalid(path, problem);
        return record!;
    }

    public void Write(string path)
    {
        var sorted = this with { Files = new SortedDictionary<string, string>(Files.ToDictionary(), StringComparer.Ordinal) };
        File.WriteAllText(path, JsonSerializer.Serialize(sorted, JsonOptions) + "\n", new UTF8Encoding(false));
    }

    // Deserialization does not enforce non-nullable members, so every field is checked here.
    private string? Problem()
    {
        if (SchemaVersion != CurrentSchemaVersion) return $"schemaVersion {SchemaVersion} is not supported.";
        if (!PluginNames.IsValid(Name)) return "name is invalid.";
        if (!SemanticVersion.TryParse(Version, out _)) return "version is invalid.";
        if (Source is null || Source.Kind is not (PluginSource.DirectoryKind or PluginSource.GitKind) || string.IsNullOrWhiteSpace(Source.Location))
            return "source is invalid.";
        if (Source.Kind == PluginSource.GitKind && (Commit is null || Commit.Length is not (40 or 64) || !Commit.All(char.IsAsciiHexDigitLower)))
            return "commit is invalid.";
        if (Files is null || Files.Count == 0) return "files is empty.";
        foreach (var (file, hash) in Files)
            if (!PluginManifest.IsSafeRelativePath(file) || hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigitLower))
                return $"files entry '{file}' is invalid.";
        return null;
    }

    private static RLoopException Invalid(string path, string reason, Exception? innerException = null) =>
        PluginErrors.Create(PluginErrors.RecordInvalid, $"Cannot use plugin record '{path}': {reason}",
            new Dictionary<string, object?> { ["path"] = path },
            ["Remove the plugin with resoloop plugin remove NAME --yes and install it again."], innerException);
}

public sealed record PluginProblem(string Code, string Message, IReadOnlyDictionary<string, object?> Details,
    IReadOnlyList<string> Suggestions);

public sealed record PluginStatus(string Name, string InstallDirectory, PluginInstallRecord? Record, PluginManifest? Manifest,
    PluginProblem? Problem)
{
    public bool Ok => Problem is null;
}

public sealed record InstalledPlugin(string Name, string InstallDirectory, string PackageDirectory, string DataDirectory,
    PluginManifest Manifest, PluginInstallRecord Record);

/// <summary>Plugins installed in one project under .resoloop/plugins. Use <see cref="Load"/> or <see cref="LoadAll"/> before
/// using a plugin: both verify every file against install.json and never return a modified plugin.</summary>
public sealed class PluginRegistry
{
    public const string PackageDirectoryName = "package";
    public const string DataDirectoryName = "data";

    public PluginRegistry(string projectRoot, string resoloopVersion)
    {
        ProjectRoot = Path.GetFullPath(projectRoot);
        PluginsDirectory = Path.Combine(ProjectRoot, ".resoloop", "plugins");
        ResoloopVersion = resoloopVersion;
    }

    public string ProjectRoot { get; }
    public string PluginsDirectory { get; }
    public string ResoloopVersion { get; }

    public static string FindProjectRoot(string? explicitProject, string startDirectory)
    {
        if (explicitProject is not null)
        {
            var root = Path.GetFullPath(explicitProject, Path.GetFullPath(startDirectory));
            if (File.Exists(Path.Combine(root, ConfigResolver.ProjectFileName))) return root;
            throw ProjectNotFound($"--project {root} has no {ConfigResolver.ProjectFileName}.", root);
        }
        var config = ConfigResolver.FindProjectConfigPath(startDirectory);
        return config is not null
            ? Path.GetDirectoryName(config)!
            : throw ProjectNotFound($"No {ConfigResolver.ProjectFileName} was found in {Path.GetFullPath(startDirectory)} or its parents.",
                Path.GetFullPath(startDirectory));
    }

    /// <summary>Every installed plugin with the result of verification. Problems are reported per plugin, never thrown.</summary>
    public IReadOnlyList<PluginStatus> List()
    {
        if (!Directory.Exists(PluginsDirectory)) return [];
        return new DirectoryInfo(PluginsDirectory).EnumerateFileSystemInfos("*", PluginFiles.Shallow)
            .Where(entry => !entry.Name.StartsWith('.'))
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(Inspect)
            .ToArray();
    }

    /// <summary>All plugins, only when every one of them is verified and no two provide the same name.</summary>
    public IReadOnlyList<InstalledPlugin> LoadAll()
    {
        var statuses = List();
        var broken = statuses.Where(status => !status.Ok).ToArray();
        if (broken.Length > 0) throw ToException(broken[0], broken);
        var plugins = statuses.Select(ToInstalled).ToArray();
        for (var i = 1; i < plugins.Length; i++)
        {
            var conflicts = PluginNames.FindConflicts(plugins[i].Manifest, plugins.Take(i).Select(plugin => plugin.Manifest));
            if (conflicts.Count > 0)
                throw PluginErrors.Create(PluginErrors.NameConflict,
                    $"Plugin '{plugins[i].Name}' conflicts with another installed plugin: {conflicts[0]}",
                    new Dictionary<string, object?> { ["plugin"] = plugins[i].Name, ["conflicts"] = conflicts },
                    ["Remove one of the conflicting plugins with resoloop plugin remove NAME --yes."]);
        }
        return plugins;
    }

    public InstalledPlugin Load(string name)
    {
        if (!PluginNames.IsValid(name))
            throw PluginErrors.Create(PluginErrors.NameInvalid, $"'{name}' is not a plugin name ({PluginNames.Rule}).");
        var path = Path.Combine(PluginsDirectory, name);
        FileSystemInfo entry = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!entry.Exists && entry.LinkTarget is null)
            throw PluginErrors.Create(PluginErrors.NotInstalled, $"Plugin '{name}' is not installed in {ProjectRoot}.",
                new Dictionary<string, object?> { ["plugin"] = name }, ["Run resoloop plugin list --json."]);
        var status = Inspect(entry);
        return status.Ok ? ToInstalled(status) : throw ToException(status, [status]);
    }

    private PluginStatus Inspect(FileSystemInfo entry)
    {
        PluginInstallRecord? record = null;
        PluginManifest? manifest = null;
        try
        {
            if (entry is not DirectoryInfo || entry.LinkTarget is not null)
                throw RecordInvalid(entry.Name, "the entry is not a real directory.");
            if (!PluginNames.IsValid(entry.Name))
                throw RecordInvalid(entry.Name, $"the directory name does not match {PluginNames.Rule}.");
            record = PluginInstallRecord.Read(Path.Combine(entry.FullName, PluginInstallRecord.FileName));
            if (record.Name != entry.Name) throw RecordInvalid(entry.Name, $"{PluginInstallRecord.FileName} names '{record.Name}'.");
            var package = Path.Combine(entry.FullName, PackageDirectoryName);
            VerifyFiles(entry.Name, package, record.Files);
            manifest = PluginManifest.Load(package, record.Files.Keys.ToHashSet(StringComparer.Ordinal));
            if (manifest.Name != record.Name || manifest.Version.Text != record.Version)
                throw RecordInvalid(entry.Name,
                    $"the manifest declares {manifest.Name} {manifest.Version}, but {PluginInstallRecord.FileName} records {record.Name} {record.Version}.");
            manifest.EnsureCompatibleWith(ResoloopVersion);
            var conflicts = PluginNames.FindConflicts(manifest, []);
            if (conflicts.Count > 0)
                throw PluginErrors.Create(PluginErrors.NameConflict, $"Plugin '{entry.Name}' uses a reserved name: {conflicts[0]}",
                    new Dictionary<string, object?> { ["plugin"] = entry.Name, ["conflicts"] = conflicts },
                    ["Remove the plugin and install a release that does not use built-in names."]);
            return new PluginStatus(entry.Name, entry.FullName, record, manifest, null);
        }
        catch (RLoopException ex) when (ex.Code.StartsWith("PLUGIN_", StringComparison.Ordinal))
        {
            return new PluginStatus(entry.Name, entry.FullName, record, manifest,
                new PluginProblem(ex.Code, ex.Message, ex.Context, ex.Suggestions));
        }
    }

    private static void VerifyFiles(string name, string package, IReadOnlyDictionary<string, string> expected)
    {
        List<string> files = [], links = [];
        try
        {
            if (Directory.Exists(package)) (files, links) = PluginFiles.Scan(package);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PluginErrors.Create(PluginErrors.Modified, $"Cannot read plugin '{name}' files in {package}: {ex.Message}",
                new Dictionary<string, object?> { ["plugin"] = name }, innerException: ex);
        }
        var changed = new List<string>();
        foreach (var file in files.Where(expected.ContainsKey))
        {
            try
            {
                if (PluginFiles.Sha256(Path.Combine(package, file)) != expected[file]) changed.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                changed.Add(file);
            }
        }
        var missing = expected.Keys.Except(files, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var unexpected = files.Where(file => !expected.ContainsKey(file)).ToArray();
        if (changed.Count + missing.Length + unexpected.Length + links.Count == 0) return;
        throw PluginErrors.Create(PluginErrors.Modified,
            $"Plugin '{name}' changed after it was installed ({changed.Count} changed, {missing.Length} missing, " +
            $"{unexpected.Length} unexpected, {links.Count} links).",
            new Dictionary<string, object?>
            {
                ["plugin"] = name, ["changed"] = changed, ["missing"] = missing, ["unexpected"] = unexpected, ["links"] = links
            },
            [$"Reinstall it: resoloop plugin remove {name} --yes, then resoloop plugin install SOURCE.",
             "Plugins must write runtime files under their data directory, never under package/."]);
    }

    private static InstalledPlugin ToInstalled(PluginStatus status) => new(status.Name, status.InstallDirectory,
        Path.Combine(status.InstallDirectory, PackageDirectoryName), Path.Combine(status.InstallDirectory, DataDirectoryName),
        status.Manifest!, status.Record!);

    private static RLoopException ToException(PluginStatus status, IReadOnlyList<PluginStatus> all)
    {
        var problem = status.Problem!;
        var context = new Dictionary<string, object?>(problem.Details)
        {
            ["plugin"] = status.Name,
            ["problems"] = all.Select(item => new Dictionary<string, object?>
            {
                ["name"] = item.Name, ["code"] = item.Problem!.Code, ["message"] = item.Problem.Message
            }).ToArray()
        };
        return PluginErrors.Create(problem.Code, problem.Message, context, problem.Suggestions);
    }

    private static RLoopException RecordInvalid(string name, string reason) =>
        PluginErrors.Create(PluginErrors.RecordInvalid, $"Plugin '{name}' has an invalid install record: {reason}",
            new Dictionary<string, object?> { ["plugin"] = name },
            [$"Remove it with resoloop plugin remove {name} --yes and install it again."]);

    private static RLoopException ProjectNotFound(string message, string directory) =>
        PluginErrors.Create(PluginErrors.ProjectNotFound, message, new Dictionary<string, object?> { ["directory"] = directory },
            ["Run the command inside a resoloop project, pass --project DIR, or create one with resoloop init."]);
}

internal static class PluginFiles
{
    public static readonly EnumerationOptions Shallow = new() { AttributesToSkip = 0, RecurseSubdirectories = false };

    public static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Files below <paramref name="root"/> as sorted '/'-separated relative paths. Links (symlinks, junctions) are never
    /// followed and are returned separately so callers can refuse them. <paramref name="skip"/> drops entries by relative path.</summary>
    public static (List<string> Files, List<string> Links) Scan(string root, Func<string, bool>? skip = null)
    {
        var files = new List<string>();
        var links = new List<string>();
        void Walk(DirectoryInfo directory, string prefix)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos("*", Shallow))
            {
                var relative = prefix + entry.Name;
                if (skip?.Invoke(relative) == true) continue;
                // LinkTarget covers symlinks and junctions; other reparse points (for example OneDrive files) are regular content.
                if (entry.LinkTarget is not null) links.Add(relative);
                else if (entry is DirectoryInfo child) Walk(child, relative + "/");
                else files.Add(relative);
            }
        }
        Walk(new DirectoryInfo(root), "");
        files.Sort(StringComparer.Ordinal);
        links.Sort(StringComparer.Ordinal);
        return (files, links);
    }
}
