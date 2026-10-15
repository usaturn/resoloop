using System.Diagnostics;
using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.Core.Plugins;

namespace RLoop.Tests;

public sealed class PluginInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resoloop-plugin-installer-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly PluginRegistry _registry;

    public PluginInstallerTests()
    {
        _project = PluginFixture.CreateProject(Path.Combine(_root, "project"));
        _registry = new PluginRegistry(_project, PluginFixture.ResoloopVersion);
    }

    [Fact]
    public async Task InstallsFromADirectoryAndRecordsEveryFile()
    {
        var source = PluginFixture.CopySample(Path.Combine(_root, "source"));

        var result = await PluginInstaller.InstallAsync(_registry, source, null);

        var plugin = _registry.Load("sample-plugin");
        Assert.Equal("1.2.3", result.Version);
        Assert.Equal(Path.Combine(_registry.PluginsDirectory, "sample-plugin"), result.InstallDirectory);
        Assert.Equal(new PluginSource("directory", Path.GetFullPath(source), null), plugin.Record.Source);
        Assert.Null(plugin.Record.Commit);
        Assert.Equal(PluginFixture.Files(source).Order(StringComparer.Ordinal), plugin.Record.Files.Keys);
        Assert.Equal(PluginFixture.ResoloopVersion, plugin.Record.ResoloopVersion);
        Assert.Equal(["sample-plugin"], PluginsDirectoryEntries());
    }

    [Fact]
    public async Task CopiesOnlyFilesGitWouldTrackFromAWorkTree()
    {
        var source = PluginFixture.CopySample(Path.Combine(_root, "source"));
        Git(source, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(source, ".gitignore"), ".venv/\nignored.txt\n");
        Directory.CreateDirectory(Path.Combine(source, ".venv", "bin"));
        File.WriteAllText(Path.Combine(source, ".venv", "bin", "python"), "binary");
        File.WriteAllText(Path.Combine(source, "ignored.txt"), "local only");
        File.WriteAllText(Path.Combine(source, "untracked.txt"), "new file");

        await PluginInstaller.InstallAsync(_registry, source, null);

        var files = _registry.Load("sample-plugin").Record.Files.Keys.ToArray();
        Assert.Contains("untracked.txt", files);
        Assert.Contains(".gitignore", files);
        Assert.DoesNotContain("ignored.txt", files);
        Assert.DoesNotContain(files, file => file.StartsWith(".venv/", StringComparison.Ordinal) || file.StartsWith(".git/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CopiesEveryFileFromADirectoryThatAnEnclosingRepositoryIgnores()
    {
        var outer = Directory.CreateDirectory(Path.Combine(_root, "outer")).FullName;
        Git(outer, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(outer, ".gitignore"), "plugins/\n");
        var source = PluginFixture.CopySample(Path.Combine(outer, "plugins", "sample"));

        await PluginInstaller.InstallAsync(_registry, source, null);

        Assert.Equal(PluginFixture.Files(source).Order(StringComparer.Ordinal), _registry.Load("sample-plugin").Record.Files.Keys);
    }

    [Theory]
    [InlineData("v1", "first")]
    [InlineData("main", "second")]
    [InlineData("feature", "first")]
    [InlineData("<first-commit>", "first")]
    public async Task InstallsFromAGitUrlPinnedByTagBranchOrCommit(string gitRef, string expected)
    {
        var (url, first, second) = CreateRepository();
        if (gitRef == "<first-commit>") gitRef = first;

        var result = await PluginInstaller.InstallAsync(_registry, url, gitRef);

        var plugin = _registry.Load("sample-plugin");
        Assert.Equal(expected == "first" ? first : second, result.Commit);
        Assert.Equal(expected == "first" ? "1.2.3" : "1.3.0", plugin.Manifest.Version.Text);
        Assert.Equal(new PluginSource("git", url, gitRef), plugin.Record.Source);
        Assert.Equal(result.Commit, plugin.Record.Commit);
        Assert.False(Directory.Exists(Path.Combine(plugin.PackageDirectory, ".git")));
        Assert.Equal(["sample-plugin"], PluginsDirectoryEntries());
    }

    [Theory]
    [InlineData("https://example.invalid/plugin.git", null, "PLUGIN_GIT_REF_REQUIRED")]
    [InlineData("git@example.invalid:owner/plugin.git", null, "PLUGIN_GIT_REF_REQUIRED")]
    [InlineData("https://user:secret-token@example.invalid/plugin.git", "v1", "PLUGIN_SOURCE_INVALID")]
    [InlineData("https://example.invalid/plugin.git", "-c", "PLUGIN_SOURCE_INVALID")]
    [InlineData("https://example.invalid/plugin.git", "v1..v2", "PLUGIN_SOURCE_INVALID")]
    [InlineData("<source>", "v1", "PLUGIN_SOURCE_INVALID")]
    [InlineData("no-such-directory", null, "PLUGIN_SOURCE_NOT_FOUND")]
    public async Task RejectsBadSourcesBeforeContactingGit(string source, string? gitRef, string code)
    {
        if (source == "<source>") source = PluginFixture.CopySample(Path.Combine(_root, "source"));

        var ex = await Assert.ThrowsAsync<RLoopException>(() => PluginInstaller.InstallAsync(_registry, source, gitRef));

        Assert.Equal(code, ex.Code);
        Assert.DoesNotContain("secret-token", ex.Message);
        Assert.Empty(PluginsDirectoryEntries());
    }

    [Fact]
    public async Task ReportsAMissingRefAndAFailedCloneWithoutLeavingFiles()
    {
        var (url, _, _) = CreateRepository();
        var missing = new Uri(Path.Combine(_root, "no-such-repository")).AbsoluteUri;

        var missingRef = await Assert.ThrowsAsync<RLoopException>(() => PluginInstaller.InstallAsync(_registry, url, "v9"));
        var failedClone = await Assert.ThrowsAsync<RLoopException>(() => PluginInstaller.InstallAsync(_registry, missing, "v1"));

        Assert.Equal("PLUGIN_GIT_REF_NOT_FOUND", missingRef.Code);
        Assert.Equal("PLUGIN_GIT_FAILED", failedClone.Code);
        Assert.Equal(ExitCodes.ExternalToolFailed, failedClone.ExitCode);
        Assert.Empty(PluginsDirectoryEntries());
    }

    [Fact]
    public async Task RefusesSymlinksCommittedToAGitSource()
    {
        var (url, _, _) = CreateRepository();
        var repository = new Uri(url).LocalPath;
        var blob = Git(repository, "hash-object", "-w", Path.Combine(repository, PluginManifest.FileName));
        Git(repository, "update-index", "--add", "--cacheinfo", $"120000,{blob},linked");
        Git(repository, "commit", "-q", "-m", "add a symlink");
        Git(repository, "tag", "v2");

        var ex = await Assert.ThrowsAsync<RLoopException>(() => PluginInstaller.InstallAsync(_registry, url, "v2"));

        Assert.Equal("PLUGIN_SOURCE_INVALID", ex.Code);
        Assert.Empty(PluginsDirectoryEntries());
    }

    [Fact]
    public async Task RefusesSymlinksInADirectorySource()
    {
        var source = PluginFixture.CopySample(Path.Combine(_root, "source"));
        if (!TryCreateFileLink(Path.Combine(source, "linked.txt"), Path.Combine(source, PluginManifest.FileName))) return;

        var ex = await Assert.ThrowsAsync<RLoopException>(() => PluginInstaller.InstallAsync(_registry, source, null));

        Assert.Equal("PLUGIN_SOURCE_INVALID", ex.Code);
        Assert.Empty(PluginsDirectoryEntries());
    }

    [Theory]
    [InlineData("built-in-command", "PLUGIN_NAME_CONFLICT")]
    [InlineData("bundled-skill", "PLUGIN_NAME_CONFLICT")]
    [InlineData("broken-manifest", "PLUGIN_MANIFEST_INVALID")]
    [InlineData("unsupported-version", "PLUGIN_INCOMPATIBLE_VERSION")]
    public async Task RefusesUnusablePluginsBeforeInstalling(string problem, string code)
    {
        var source = PluginFixture.CopySample(Path.Combine(_root, "source"));
        if (problem == "broken-manifest") File.WriteAllText(Path.Combine(source, PluginManifest.FileName), "{ \"schemaVersion\": 1, ");
        else PluginFixture.EditManifest(source, json =>
        {
            if (problem == "built-in-command") json["commands"]![0]!["name"] = "doctor";
            if (problem == "bundled-skill") json["skills"]![0]!["name"] = "resonite-build";
            if (problem == "unsupported-version") json["resoloop"]!["minVersion"] = "9.0.0";
            if (problem == "unsupported-version") json["resoloop"]!.AsObject().Remove("maxVersionExclusive");
        });

        var ex = await Assert.ThrowsAsync<RLoopException>(() => PluginInstaller.InstallAsync(_registry, source, null));

        Assert.Equal(code, ex.Code);
        Assert.Empty(PluginsDirectoryEntries());
    }

    [Fact]
    public async Task RefusesASecondCopyAndAPluginWithTheSameNames()
    {
        var source = PluginFixture.CopySample(Path.Combine(_root, "source"));
        var sameNames = PluginFixture.CopySample(Path.Combine(_root, "same-names"));
        PluginFixture.EditManifest(sameNames, json => json["name"] = "other-plugin");
        await PluginInstaller.InstallAsync(_registry, source, null);

        var again = await Assert.ThrowsAsync<RLoopException>(() => PluginInstaller.InstallAsync(_registry, source, null));
        var conflict = await Assert.ThrowsAsync<RLoopException>(() => PluginInstaller.InstallAsync(_registry, sameNames, null));

        Assert.Equal("PLUGIN_ALREADY_INSTALLED", again.Code);
        Assert.Equal("PLUGIN_NAME_CONFLICT", conflict.Code);
        Assert.Equal(["sample-plugin"], PluginsDirectoryEntries());
    }

    [Fact]
    public async Task RefusesToInstallWhileAnInstalledPluginIsModified()
    {
        var installed = await PluginInstaller.InstallAsync(_registry, PluginFixture.CopySample(Path.Combine(_root, "source")), null);
        File.AppendAllText(Path.Combine(installed.InstallDirectory, "package", "scripts", "hello.sh"), "echo changed\n");

        var ex = await Assert.ThrowsAsync<RLoopException>(() => PluginInstaller.InstallAsync(_registry, CreateDistinctSource("other-plugin"), null));

        Assert.Equal("PLUGIN_MODIFIED", ex.Code);
    }

    [Fact]
    public async Task RemoveDeletesOnlyTheInstallDirectory()
    {
        var removed = await PluginInstaller.InstallAsync(_registry, PluginFixture.CopySample(Path.Combine(_root, "source")), null);
        await PluginInstaller.InstallAsync(_registry, CreateDistinctSource("other-plugin"), null);
        Directory.CreateDirectory(Path.Combine(removed.InstallDirectory, "data"));
        File.WriteAllText(Path.Combine(removed.InstallDirectory, "data", "cache.txt"), "runtime");
        var distributed = Path.Combine(_project, ".agents", "skills", "sample-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(distributed)!);
        File.WriteAllText(distributed, "distributed by a later sync");

        var result = PluginInstaller.Remove(_registry, "sample-plugin");

        Assert.Equal(removed.InstallDirectory, result.RemovedDirectory);
        Assert.False(Directory.Exists(removed.InstallDirectory));
        Assert.True(File.Exists(distributed));
        Assert.Equal("other-plugin", Assert.Single(_registry.LoadAll()).Name);
    }

    [Fact]
    public async Task RemoveDoesNotFollowLinksInsideThePackage()
    {
        var installed = await PluginInstaller.InstallAsync(_registry, PluginFixture.CopySample(Path.Combine(_root, "source")), null);
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "user file");
        if (!TryCreateDirectoryLink(Path.Combine(installed.InstallDirectory, "package", "linked"), outside)) return;

        PluginInstaller.Remove(_registry, "sample-plugin");

        Assert.False(Directory.Exists(installed.InstallDirectory));
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void RemoveRefusesInvalidNamesMissingPluginsAndLinks()
    {
        Assert.Equal("PLUGIN_NAME_INVALID", Assert.Throws<RLoopException>(() => PluginInstaller.Remove(_registry, "../project")).Code);
        Assert.Equal("PLUGIN_NOT_INSTALLED", Assert.Throws<RLoopException>(() => PluginInstaller.Remove(_registry, "absent-plugin")).Code);
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        Directory.CreateDirectory(_registry.PluginsDirectory);
        if (!TryCreateDirectoryLink(Path.Combine(_registry.PluginsDirectory, "linked-plugin"), outside)) return;

        Assert.Equal("PLUGIN_REMOVE_REFUSED", Assert.Throws<RLoopException>(() => PluginInstaller.Remove(_registry, "linked-plugin")).Code);
        Assert.True(Directory.Exists(outside));
    }

    public void Dispose() => PluginFixture.DeleteDirectory(_root);

    private string[] PluginsDirectoryEntries() => Directory.Exists(_registry.PluginsDirectory)
        ? Directory.EnumerateFileSystemEntries(_registry.PluginsDirectory).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray()
        : [];

    private string CreateDistinctSource(string name)
    {
        var source = PluginFixture.CopySample(Path.Combine(_root, name));
        PluginFixture.EditManifest(source, json =>
        {
            json["name"] = name;
            foreach (var list in new[] { "commands", "skills", "recipes" })
                foreach (var item in json[list]!.AsArray())
                    item!["name"] = name + "-" + item["name"]!.GetValue<string>();
        });
        return source;
    }

    // A repository with v1 (1.2.3) on the first commit, a "feature" branch there, and main at the second commit (1.3.0).
    private (string Url, string First, string Second) CreateRepository()
    {
        var repository = PluginFixture.CopySample(Path.Combine(_root, "repository"));
        Git(repository, "init", "-q", "-b", "main");
        Git(repository, "add", "-A");
        Git(repository, "commit", "-q", "-m", "first");
        Git(repository, "tag", "v1");
        Git(repository, "branch", "feature");
        var first = Git(repository, "rev-parse", "HEAD");
        PluginFixture.EditManifest(repository, json => json["version"] = "1.3.0");
        Git(repository, "commit", "-q", "-a", "-m", "second");
        return (new Uri(repository).AbsoluteUri, first, Git(repository, "rev-parse", "HEAD"));
    }

    private static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        string[] all = ["-C", directory, "-c", "user.name=resoloop-tests", "-c", "user.email=tests@example.invalid",
            "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false", .. arguments];
        foreach (var argument in all) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error.Result}");
        return output.Trim();
    }

    private static bool TryCreateDirectoryLink(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // Symlinks need privileges on some Windows hosts.
        }
    }

    private static bool TryCreateFileLink(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
