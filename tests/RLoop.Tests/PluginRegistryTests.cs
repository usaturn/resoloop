using RLoop.Core;
using RLoop.Core.Plugins;

namespace RLoop.Tests;

public sealed class PluginRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resoloop-plugin-registry-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly PluginRegistry _registry;

    public PluginRegistryTests()
    {
        _project = PluginFixture.CreateProject(Path.Combine(_root, "project"));
        _registry = new PluginRegistry(_project, PluginFixture.ResoloopVersion);
    }

    [Fact]
    public void ListsNothingBeforeAnyPluginIsInstalled()
    {
        Assert.Empty(_registry.List());
        Assert.Empty(_registry.LoadAll());
    }

    [Fact]
    public void VerifiesAndLoadsAnInstalledPlugin()
    {
        var installDirectory = Install("sample-plugin");

        var status = Assert.Single(_registry.List());
        var plugin = _registry.Load("sample-plugin");

        Assert.True(status.Ok);
        Assert.Equal("1.2.3", status.Record!.Version);
        Assert.Equal(Path.Combine(installDirectory, "package"), plugin.PackageDirectory);
        Assert.Equal(Path.Combine(installDirectory, "data"), plugin.DataDirectory);
        Assert.Equal("sample-hello", plugin.Manifest.Commands[0].Name);
        Assert.Equal("sample-plugin", Assert.Single(_registry.LoadAll()).Name);
    }

    [Theory]
    [InlineData("changed", "scripts/hello.sh")]
    [InlineData("missing", "skills/sample-skill/SKILL.md")]
    [InlineData("unexpected", "json.py")]
    [InlineData("unexpected", ".hidden")]
    public void RefusesAPluginWhoseFilesChangedAfterInstall(string kind, string path)
    {
        var package = Path.Combine(Install("sample-plugin"), "package");
        var file = Path.Combine(package, path);
        if (kind == "missing") File.Delete(file);
        else File.AppendAllText(file, "print('injected')\n");

        var status = Assert.Single(_registry.List());
        var load = Assert.Throws<RLoopException>(() => _registry.Load("sample-plugin"));
        var loadAll = Assert.Throws<RLoopException>(() => _registry.LoadAll());

        Assert.Equal("PLUGIN_MODIFIED", status.Problem!.Code);
        Assert.Contains(path, Assert.IsAssignableFrom<IEnumerable<string>>(status.Problem.Details[kind]));
        Assert.Equal("PLUGIN_MODIFIED", load.Code);
        Assert.Equal("PLUGIN_MODIFIED", loadAll.Code);
        Assert.Equal(ExitCodes.ValidationFailed, load.ExitCode);
    }

    [Fact]
    public void RefusesALinkInsideThePackage()
    {
        var package = Path.Combine(Install("sample-plugin"), "package");
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        if (!TryCreateDirectoryLink(Path.Combine(package, "linked"), outside)) return; // Symlinks need privileges on some Windows hosts.

        var status = Assert.Single(_registry.List());

        Assert.Equal("PLUGIN_MODIFIED", status.Problem!.Code);
        Assert.Contains("linked", Assert.IsAssignableFrom<IEnumerable<string>>(status.Problem.Details["links"]));
    }

    [Fact]
    public void ReportsAnUnreadableOrMismatchedRecord()
    {
        File.WriteAllText(Path.Combine(Install("sample-plugin"), "install.json"), "{ not json");
        var renamed = Install("other-plugin");
        Directory.Move(renamed, Path.Combine(_registry.PluginsDirectory, "third-plugin"));

        var statuses = _registry.List();

        Assert.All(statuses, status => Assert.Equal("PLUGIN_RECORD_INVALID", status.Problem!.Code));
        Assert.Equal(["sample-plugin", "third-plugin"], statuses.Select(status => status.Name));
    }

    [Fact]
    public void ReportsAPluginThatNoLongerSupportsTheRunningResoloop()
    {
        Install("sample-plugin");

        var upgraded = new PluginRegistry(_project, "0.2.0");

        Assert.Equal("PLUGIN_INCOMPATIBLE_VERSION", Assert.Single(upgraded.List()).Problem!.Code);
        Assert.Equal("PLUGIN_INCOMPATIBLE_VERSION", Assert.Throws<RLoopException>(() => upgraded.LoadAll()).Code);
    }

    [Fact]
    public void IgnoresStagingDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_registry.PluginsDirectory, ".staging-0123"));

        Assert.Empty(_registry.List());
    }

    [Fact]
    public void LoadReportsMissingAndInvalidNames()
    {
        Assert.Equal("PLUGIN_NOT_INSTALLED", Assert.Throws<RLoopException>(() => _registry.Load("absent-plugin")).Code);
        Assert.Equal("PLUGIN_NAME_INVALID", Assert.Throws<RLoopException>(() => _registry.Load("../outside")).Code);
    }

    [Fact]
    public void LoadAllRefusesPluginsThatProvideTheSameNames()
    {
        Install("sample-plugin");
        Install("other-plugin");

        Assert.All(_registry.List(), status => Assert.True(status.Ok));
        var ex = Assert.Throws<RLoopException>(() => _registry.LoadAll());

        Assert.Equal("PLUGIN_NAME_CONFLICT", ex.Code);
    }

    [Fact]
    public void FindsTheNearestProjectOrTheExplicitOne()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_project, "content", "deep")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "not-a-project")).FullName;

        Assert.Equal(_project, PluginRegistry.FindProjectRoot(null, nested));
        Assert.Equal(_project, PluginRegistry.FindProjectRoot(_project, outside));
        var ex = Assert.Throws<RLoopException>(() => PluginRegistry.FindProjectRoot(outside, nested));
        Assert.Equal("PLUGIN_PROJECT_NOT_FOUND", ex.Code);
        Assert.Equal(ExitCodes.ConfigurationError, ex.ExitCode);
    }

    public void Dispose() => PluginFixture.DeleteDirectory(_root);

    // Builds an install directory by hand so these tests do not depend on PluginInstaller.
    private string Install(string name)
    {
        var installDirectory = Path.Combine(_registry.PluginsDirectory, name);
        var package = PluginFixture.CopySample(Path.Combine(installDirectory, "package"));
        PluginFixture.EditManifest(package, json => json["name"] = name);
        var files = PluginFixture.Files(package).Order(StringComparer.Ordinal)
            .ToDictionary(file => file, file => PluginFiles.Sha256(Path.Combine(package, file)), StringComparer.Ordinal);
        new PluginInstallRecord(1, name, "1.2.3", new PluginSource(PluginSource.DirectoryKind, PluginFixture.SamplePath, null), null,
            "2026-10-16T00:00:00Z", PluginFixture.ResoloopVersion, files).Write(Path.Combine(installDirectory, "install.json"));
        return installDirectory;
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
            return false;
        }
    }
}
