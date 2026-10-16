using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RLoop.Core;
using RLoop.Core.Plugins;

namespace RLoop.Tests;

public sealed class PluginManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resoloop-plugin-manifest-tests-" + Guid.NewGuid().ToString("N"));

    public PluginManifestTests() => PluginFixture.CopySample(_root);

    [Fact]
    public void LoadsEveryFieldOfTheSampleManifest()
    {
        var manifest = PluginManifest.Load(_root, PluginFixture.Files(_root));

        Assert.Equal("sample-plugin", manifest.Name);
        Assert.Equal("1.2.3", manifest.Version.Text);
        Assert.Equal(["sample-hello", "sample-uv"], manifest.Commands.Select(command => command.Name));
        Assert.Equal("./scripts/hello.sh", manifest.Commands[0].Program);
        Assert.Equal(["--root", PluginManifest.PluginRootToken, "--data=" + PluginManifest.PluginDataToken], manifest.Commands[0].Args);
        Assert.Equal(60, manifest.Commands[0].TimeoutSeconds);
        Assert.Null(manifest.Commands[1].TimeoutSeconds);
        Assert.Equal("skills/sample-skill", Assert.Single(manifest.Skills).Path);
        var recipe = Assert.Single(manifest.Recipes);
        Assert.Equal("sample.card", recipe.Prototype);
        Assert.Equal("Unique stable key prefix.", recipe.Parameters["key"]);
        Assert.Equal("$slot:<key>", recipe.Ports["root"]);
        var tool = Assert.Single(manifest.Tools);
        Assert.False(tool.Required);
        Assert.Equal(["--version"], tool.VersionArgs);
        Assert.True(manifest.Resoloop.Contains(Version(PluginFixture.ResoloopVersion)));
    }

    [Fact]
    public void ReportsEveryInvalidFieldWithItsJsonPath()
    {
        PluginFixture.EditManifest(_root, json =>
        {
            json["name"] = "Bad_Name";
            json["version"] = "1.0";
            json["extra"] = true;
            json["commands"]![0]!["program"] = "/usr/bin/env";
            json["commands"]![1]!["args"] = new JsonArray("${HOME}");
            json["skills"]![0]!["path"] = "../outside";
            json["tools"]![0]!["required"] = "yes";
        });

        var errors = ManifestErrors(() => PluginManifest.Load(_root, PluginFixture.Files(_root)));

        foreach (var path in new[] { "$.name:", "$.version:", "$.extra:", "$.commands[0].program:", "$.commands[1].args[0]:",
                     "$.skills[0].path:", "$.tools[0].required:" })
            Assert.Contains(errors, error => error.StartsWith(path, StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsDuplicatedFields()
    {
        var json = File.ReadAllText(Path.Combine(_root, PluginManifest.FileName));
        File.WriteAllText(Path.Combine(_root, PluginManifest.FileName), json.Replace("\"name\": \"sample-plugin\",",
            "\"name\": \"sample-plugin\", \"name\": \"other-plugin\",", StringComparison.Ordinal));

        var errors = ManifestErrors(() => PluginManifest.Load(_root, PluginFixture.Files(_root)));

        Assert.Contains("$.name: is duplicated.", errors);
    }

    [Fact]
    public void RejectsDuplicatedNamesWithinAList()
    {
        PluginFixture.EditManifest(_root, json => json["commands"]![1]!["name"] = "sample-hello");

        var errors = ManifestErrors(() => PluginManifest.Load(_root, PluginFixture.Files(_root)));

        Assert.Contains(errors, error => error.StartsWith("$.commands:", StringComparison.Ordinal) && error.Contains("sample-hello"));
    }

    [Fact]
    public void ReadsAManifestWithAUtf8ByteOrderMark()
    {
        var path = Path.Combine(_root, PluginManifest.FileName);
        File.WriteAllText(path, File.ReadAllText(path), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal("sample-plugin", PluginManifest.Load(_root, PluginFixture.Files(_root)).Name);
    }

    [Fact]
    public void RejectsMalformedJson()
    {
        File.WriteAllText(Path.Combine(_root, PluginManifest.FileName), "{ \"schemaVersion\": 1,");

        Assert.NotEmpty(ManifestErrors(() => PluginManifest.Load(_root, PluginFixture.Files(_root))));
    }

    [Fact]
    public void RejectsAnUnsupportedSchemaVersion()
    {
        PluginFixture.EditManifest(_root, json => json["schemaVersion"] = 2);

        var ex = Assert.Throws<RLoopException>(() => PluginManifest.Load(_root, PluginFixture.Files(_root)));

        Assert.Equal("PLUGIN_SCHEMA_UNSUPPORTED", ex.Code);
    }

    [Fact]
    public void RequiresTheManifestFile()
    {
        File.Delete(Path.Combine(_root, PluginManifest.FileName));

        var ex = Assert.Throws<RLoopException>(() => PluginManifest.Load(_root, PluginFixture.Files(_root)));

        Assert.Equal("PLUGIN_MANIFEST_NOT_FOUND", ex.Code);
        Assert.Equal(ExitCodes.ValidationFailed, ex.ExitCode);
    }

    [Fact]
    public void RejectsReferencedFilesThatAreNotInThePlugin()
    {
        File.Delete(Path.Combine(_root, "skills", "sample-skill", "SKILL.md"));
        PluginFixture.EditManifest(_root, json =>
        {
            json["commands"]![0]!["program"] = "./scripts/missing.sh";
            json["recipes"]![0]!["prototype"] = "sample.missing";
        });

        var errors = ManifestErrors(() => PluginManifest.Load(_root, PluginFixture.Files(_root)));

        Assert.Contains(errors, error => error.StartsWith("$.commands[0].program:", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("$.skills[0].path:", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("$.recipes[0].path:", StringComparison.Ordinal));
    }

    [Fact]
    public void RequiresTheRecipeKeyParameterAndAnIncreasingVersionRange()
    {
        PluginFixture.EditManifest(_root, json =>
        {
            json["recipes"]![0]!["parameters"] = new JsonObject { ["size"] = "Size." };
            json["resoloop"]!["maxVersionExclusive"] = "0.1.0-preview.1";
        });

        var errors = ManifestErrors(() => PluginManifest.Load(_root, PluginFixture.Files(_root)));

        Assert.Contains(errors, error => error.StartsWith("$.recipes[0].parameters:", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("$.resoloop.maxVersionExclusive:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0.1.0-preview.9", "0.1.0-preview.17")]
    [InlineData("0.1.0-preview.17", "0.1.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.9.0", "1.10.0")]
    public void OrdersVersionsBySemVerPrecedence(string lower, string higher)
    {
        Assert.True(Version(lower).CompareTo(Version(higher)) < 0);
        Assert.True(Version(higher).CompareTo(Version(lower)) > 0);
    }

    [Fact]
    public void IgnoresBuildMetadataWhenComparing() =>
        Assert.Equal(0, Version("1.0.0+build.1").CompareTo(Version("1.0.0+build.2")));

    [Theory]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-01")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0\n")]
    public void RejectsTextThatIsNotSemVer(string text) => Assert.False(SemanticVersion.TryParse(text, out _));

    [Theory]
    [InlineData("0.1.0-preview.17", true)]
    [InlineData("0.1.0-preview.1", true)]
    [InlineData("0.0.9", false)]
    [InlineData("0.2.0", false)]
    [InlineData("unknown", false)]
    public void ChecksTheSupportedResoloopVersion(string resoloopVersion, bool compatible)
    {
        var manifest = PluginManifest.Load(_root, PluginFixture.Files(_root));

        if (compatible) manifest.EnsureCompatibleWith(resoloopVersion);
        else Assert.Equal("PLUGIN_INCOMPATIBLE_VERSION",
            Assert.Throws<RLoopException>(() => manifest.EnsureCompatibleWith(resoloopVersion)).Code);
    }

    [Theory]
    [InlineData("ab", true)]
    [InlineData("meshy-glb", true)]
    [InlineData("a1-b2", true)]
    [InlineData("a", false)]
    [InlineData("Ab", false)]
    [InlineData("1a", false)]
    [InlineData("ab-", false)]
    [InlineData("a--b", false)]
    [InlineData("a_b", false)]
    [InlineData("ab\n", false)]
    [InlineData("abcdefghijabcdefghijabcdefghijabcdefghijk", false)]
    public void AppliesTheNameRule(string name, bool valid) => Assert.Equal(valid, PluginNames.IsValid(name));

    [Fact]
    public void FindsConflictsWithBuiltInsAndOtherPlugins()
    {
        var manifest = PluginManifest.Load(_root, PluginFixture.Files(_root));
        var conflicting = manifest with
        {
            Name = "apply",
            Commands = [manifest.Commands[0] with { Name = "doctor" }],
            Skills = [new PluginSkill("resonite-build", "skills/sample-skill"), new PluginSkill("resonite-extra", "skills/sample-skill")],
            Recipes = [manifest.Recipes[0] with { Name = "button" }]
        };
        var other = manifest with { Name = "other-plugin" };

        var conflicts = PluginNames.FindConflicts(conflicting, []);
        var withOther = PluginNames.FindConflicts(manifest, [other]);

        Assert.Contains("plugin name 'apply' is a built-in command.", conflicts);
        Assert.Contains("command 'doctor' is a built-in command.", conflicts);
        Assert.Contains("skill 'resonite-build' is a bundled skill.", conflicts);
        Assert.Contains("skill 'resonite-extra' uses the reserved prefix 'resonite-'.", conflicts);
        Assert.Contains("recipe 'button' is a built-in UIX recipe.", conflicts);
        Assert.Contains("command 'sample-hello' is also provided by plugin 'other-plugin'.", withOther);
        Assert.Contains("skill 'sample-skill' is also provided by plugin 'other-plugin'.", withOther);
        Assert.Contains("recipe 'sample-card' is also provided by plugin 'other-plugin'.", withOther);
        Assert.Empty(PluginNames.FindConflicts(manifest, []));
    }

    public void Dispose() => PluginFixture.DeleteDirectory(_root);

    private static SemanticVersion Version(string text) =>
        SemanticVersion.TryParse(text, out var version) ? version : throw new ArgumentException(text);

    private static IReadOnlyList<string> ManifestErrors(Action action)
    {
        var ex = Assert.Throws<RLoopException>(action);
        Assert.Equal("PLUGIN_MANIFEST_INVALID", ex.Code);
        return Assert.IsAssignableFrom<IReadOnlyList<string>>(ex.Context["errors"]);
    }
}

/// <summary>Shared helpers for the plugin tests. Every test works in its own temporary directory.</summary>
internal static class PluginFixture
{
    public const string ResoloopVersion = "0.1.0-preview.17";

    private static readonly EnumerationOptions AllFiles = new() { RecurseSubdirectories = true, AttributesToSkip = 0 };

    public static string SamplePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "plugins", "core", "sample-plugin");

    public static string CopySample(string destination)
    {
        CopyDirectory(SamplePath, destination);
        return destination;
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", AllFiles))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public static IReadOnlyCollection<string> Files(string root) =>
        Directory.EnumerateFiles(root, "*", AllFiles)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet(StringComparer.Ordinal);

    public static void EditManifest(string root, Action<JsonObject> edit)
    {
        var path = Path.Combine(root, PluginManifest.FileName);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(json);
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string CreateProject(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".resoloop.json"), "{}\n");
        return root;
    }

    // Removes links without following them, and clears read-only flags that git sets on Windows object files.
    public static void DeleteDirectory(string path)
    {
        var root = new DirectoryInfo(path);
        if (root.LinkTarget is not null) { File.Delete(path); return; }
        if (!root.Exists) return;
        foreach (var entry in root.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if (entry.LinkTarget is not null)
            {
                if (entry is DirectoryInfo && OperatingSystem.IsWindows()) Directory.Delete(entry.FullName);
                else File.Delete(entry.FullName);
            }
            else if (entry is DirectoryInfo) DeleteDirectory(entry.FullName);
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }
        root.Delete();
    }
}
