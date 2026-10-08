using RLoop.Core;

namespace RLoop.Tests;

public sealed class ProjectInitializerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resoloop-init-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreatesPortableStarterProject()
    {
        var result = ProjectInitializer.Initialize(_root);

        Assert.Equal(Path.GetFullPath(_root), result.RootDirectory);
        Assert.Equal(9 + BundledSkillManager.Files.Count, result.Created.Count);
        Assert.True(File.Exists(Path.Combine(_root, ".resoloop.json")));
        var apply = ApplyDocument.Load(Path.Combine(_root, "content", "main.json"));
        Assert.Equal("1", apply.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(apply.Ownership?.Key));
        Assert.Equal("root", apply.Slot!.Key);
        Assert.StartsWith("ResoLoop_Test_", apply.Slot!.Name);
        Assert.Equal("FrooxEngine.Grabbable", Assert.Single(apply.Components!).Type);
        Assert.Contains("module Main", File.ReadAllText(Path.Combine(_root, "flux", "Main.pg")));
        Assert.True(File.Exists(Path.Combine(_root, "flux", "resoloop.flux.json")));
        Assert.Contains("resoloop doctor --json", File.ReadAllText(Path.Combine(_root, "AGENTS.md")));
        Assert.NotNull(apply.Cameras!["main"]);
        foreach (var skillName in BundledSkillManager.Names)
        {
            var skillPath = Path.Combine(_root, ".agents", "skills", skillName, "SKILL.md");
            Assert.True(File.Exists(skillPath), $"Expected bundled skill at {skillPath}");
            Assert.StartsWith("---", File.ReadAllText(skillPath));
        }
    }

    [Fact]
    public void IsIdempotentWhenGeneratedFilesAreUnchanged()
    {
        ProjectInitializer.Initialize(_root);

        var result = ProjectInitializer.Initialize(_root);

        Assert.Empty(result.Created);
        Assert.Equal(8 + BundledSkillManager.Files.Count, result.Unchanged.Count);
    }

    [Fact]
    public void TreatsLineEndingOnlyChangesAsUnchanged()
    {
        ProjectInitializer.Initialize(_root);
        var configPath = Path.Combine(_root, ".resoloop.json");
        File.WriteAllText(configPath, File.ReadAllText(configPath).Replace("\n", "\r\n", StringComparison.Ordinal));

        var result = ProjectInitializer.Initialize(_root);

        Assert.Empty(result.Created);
        Assert.Equal(8 + BundledSkillManager.Files.Count, result.Unchanged.Count);
    }

    [Fact]
    public void PreservesExistingAgentInstructions()
    {
        Directory.CreateDirectory(_root);
        var agentsPath = Path.Combine(_root, "AGENTS.md");
        File.WriteAllText(agentsPath, "user instructions");

        var result = ProjectInitializer.Initialize(_root);

        Assert.Equal("user instructions", File.ReadAllText(agentsPath));
        Assert.DoesNotContain("AGENTS.md", result.Created);
        Assert.DoesNotContain("AGENTS.md", result.Unchanged);
        Assert.True(File.Exists(Path.Combine(_root, ".resoloop.json")));
    }

    [Fact]
    public void RefusesConflictsBeforeWritingAnyOtherFile()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".resoloop.json"), "user content");

        var ex = Assert.Throws<RLoopException>(() => ProjectInitializer.Initialize(_root));

        Assert.Equal("INIT_FILE_EXISTS", ex.Code);
        Assert.False(File.Exists(Path.Combine(_root, "content", "main.json")));
    }

    [Fact]
    public void RefusesModifiedProjectSkillBeforeWritingAnyOtherFile()
    {
        var skillDirectory = Path.Combine(_root, ".agents", "skills", "resonite-build");
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "user content");

        var ex = Assert.Throws<RLoopException>(() => ProjectInitializer.Initialize(_root));

        Assert.Equal("INIT_FILE_EXISTS", ex.Code);
        Assert.Contains(".agents/skills/resonite-build/SKILL.md", Assert.IsType<List<string>>(ex.Context!["conflicts"]));
        Assert.False(File.Exists(Path.Combine(_root, ".resoloop.json")));
    }

    [Fact]
    public void SkillSyncCheckIsCleanAfterInitialization()
    {
        ProjectInitializer.Initialize(_root);

        var result = BundledSkillManager.Sync(_root, update: false);

        Assert.True(result.Synchronized);
        Assert.All(result.Skills, skill => Assert.Equal("current", skill.Status));
    }

    [Fact]
    public void SkillSyncUpdateIsNoOpWhenAlreadyCurrent()
    {
        ProjectInitializer.Initialize(_root);
        var lockPath = Path.Combine(_root, ".agents", "skills", ".resoloop-bundled.json");
        var before = File.GetLastWriteTimeUtc(lockPath);

        var result = BundledSkillManager.Sync(_root, update: true);

        Assert.True(result.Synchronized);
        Assert.Empty(result.Updated);
        Assert.Equal(before, File.GetLastWriteTimeUtc(lockPath));
    }

    [Fact]
    public void SkillSyncUpdatesOnlyContentMatchingThePreviousLock()
    {
        ProjectInitializer.Initialize(_root);
        var skillPath = Path.Combine(_root, ".agents", "skills", "resonite-build", "SKILL.md");
        const string oldBundled = "---\nname: resonite-build\ndescription: old bundled\n---\n";
        File.WriteAllText(skillPath, oldBundled);
        var oldHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(oldBundled)));
        var lockPath = Path.Combine(_root, ".agents", "skills", ".resoloop-bundled.json");
        File.WriteAllText(lockPath, $$"""
            { "schemaVersion": 1, "skills": { "resonite-build": "{{oldHash}}" } }
            """);

        var check = BundledSkillManager.Sync(_root, update: false);
        var updated = BundledSkillManager.Sync(_root, update: true);

        Assert.False(check.Synchronized);
        Assert.Equal("update-available", check.Skills.Single(skill => skill.Name == "resonite-build").Status);
        Assert.True(updated.Synchronized);
        Assert.Contains(".agents/skills/resonite-build/SKILL.md", updated.Updated);
        Assert.Contains("Build or modify Resonite", File.ReadAllText(skillPath));
        Assert.True(BundledSkillManager.Sync(_root, update: false).Synchronized);
    }

    [Fact]
    public void SkillSyncRefusesUserModifiedContentBeforeUpdatingAnything()
    {
        ProjectInitializer.Initialize(_root);
        var modifiedPath = Path.Combine(_root, ".agents", "skills", "resonite-build", "SKILL.md");
        var untouchedPath = Path.Combine(_root, ".agents", "skills", "resonite-debug", "SKILL.md");
        File.WriteAllText(modifiedPath, "user content");
        var untouched = File.ReadAllText(untouchedPath);

        var error = Assert.Throws<RLoopException>(() => BundledSkillManager.Sync(_root, update: true));

        Assert.Equal("SKILL_SYNC_CONFLICT", error.Code);
        Assert.Equal("user content", File.ReadAllText(modifiedPath));
        Assert.Equal(untouched, File.ReadAllText(untouchedPath));
    }

    [Fact]
    public void SkillSyncUpdateRestoresMissingLockedSkill()
    {
        ProjectInitializer.Initialize(_root);
        var skillPath = Path.Combine(_root, ".agents", "skills", "resonite-flux", "SKILL.md");
        File.Delete(skillPath);

        var check = BundledSkillManager.Sync(_root, update: false);
        var updated = BundledSkillManager.Sync(_root, update: true);

        Assert.False(check.Synchronized);
        Assert.Equal("missing", check.Skills.Single(skill => skill.Name == "resonite-flux").Status);
        Assert.True(updated.Synchronized);
        Assert.True(File.Exists(skillPath));
    }

    [Fact]
    public void ReferenceFilesAreInstalledRestoredAndProtectedBeforeAnyUpdate()
    {
        ProjectInitializer.Initialize(_root);
        var assets = Path.Combine(_root, ".agents/skills/resonite-uix/references/assets.md");
        var layout = Path.Combine(_root, ".agents/skills/resonite-uix/references/layout.md");
        Assert.True(File.Exists(assets));
        var original = File.ReadAllText(assets);
        File.Delete(assets);
        File.WriteAllText(layout, "user layout guidance");
        var error = Assert.Throws<RLoopException>(() => BundledSkillManager.Sync(_root, true));
        Assert.Equal("SKILL_SYNC_CONFLICT", error.Code);
        Assert.False(File.Exists(assets));
        Assert.Equal("user layout guidance", File.ReadAllText(layout));
        File.Delete(layout);
        var updated = BundledSkillManager.Sync(_root, true);
        Assert.Contains(".agents/skills/resonite-uix/references/assets.md", updated.Updated);
        Assert.Equal(original, File.ReadAllText(assets));
        Assert.True(BundledSkillManager.Sync(_root, false).Synchronized);
    }

    [Fact]
    public void ReferenceUpdateUsesItsOwnPreviousHashAndMigratesV1Lock()
    {
        ProjectInitializer.Initialize(_root);
        var lockPath = Path.Combine(_root, BundledSkillManager.LockRelativePath);
        var reference = Path.Combine(_root, ".agents/skills/resonite-uix/references/layout.md");
        var content = File.ReadAllText(reference);
        var old = "previous layout guidance\n";
        File.WriteAllText(reference, old);
        var locked = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(lockPath))!;
        locked["files"]!["resonite-uix/references/layout.md"] = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(old)));
        File.WriteAllText(lockPath, locked.ToJsonString());
        BundledSkillManager.Sync(_root, true);
        Assert.Equal(content, File.ReadAllText(reference));
        File.WriteAllText(lockPath, "{\"schemaVersion\":1,\"skills\":{}}");
        BundledSkillManager.Sync(_root, true);
        var migrated = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(lockPath))!;
        Assert.Equal(2, migrated["schemaVersion"]!.GetValue<int>());
        Assert.NotNull(migrated["files"]!["resonite-uix/references/layout.md"]);
        Assert.Null(migrated["skills"]);
    }

    // Catch Meshy packaging gaps, check-mode writes and sync touching user-owned
    // operation/journal/generated/editable/state files (including files in the skill tree).
    [Fact]
    public void MeshySyncPreservesUserDataAndCheckIsReadOnly()
    {
        ProjectInitializer.Initialize(_root);
        var entry = Path.Combine(_root, ".agents/skills/meshy-resoloop/scripts/meshy.py");
        Assert.True(File.Exists(entry), $"Expected executable entry at {entry}");
        Assert.NotEmpty(File.ReadAllBytes(entry));
        var retainedPaths = new[]
        {
            "content/generated/meshy/asset/operation.json",
            ".resoloop/meshy-cli/operations/retained.json",
            "content/generated/meshy/asset/source/original.glb",
            "content/generated/meshy/asset/converted/model.blend",
            ".resoloop/state/mesh.json",
            ".agents/skills/meshy-resoloop/user-notes.txt"
        };
        foreach (var relative in retainedPaths)
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0, 17, 255, 42]);
        }
        var before = SnapshotProject();

        var check = BundledSkillManager.Sync(_root, update: false);

        Assert.True(check.Synchronized);
        Assert.Equal(before, SnapshotProject());
        foreach (var file in check.Skills.Where(skill => skill.Path.StartsWith(
                     ".agents/skills/meshy-resoloop/", StringComparison.Ordinal)))
            Assert.NotEmpty(File.ReadAllBytes(Path.Combine(_root, file.Path)));

        File.Delete(entry);
        var missing = SnapshotProject();
        Assert.False(BundledSkillManager.Sync(_root, update: false).Synchronized);
        Assert.Equal(missing, SnapshotProject());
        var updated = BundledSkillManager.Sync(_root, update: true);
        Assert.True(updated.Synchronized);
        Assert.Contains(".agents/skills/meshy-resoloop/scripts/meshy.py", updated.Updated);
        Assert.Equal(before.Where(file => retainedPaths.Contains(file.Path)).ToArray(),
            SnapshotProject().Where(file => retainedPaths.Contains(file.Path)).ToArray());
        Assert.True(BundledSkillManager.Sync(_root, update: false).Synchronized);
    }

    // Catch partial restoration before conflict preflight; even lock bytes/mtime
    // must remain unchanged until the user reconciles the edited executable.
    [Fact]
    public void MeshySyncRefusesEditedScriptBeforeRestoringMissingFile()
    {
        ProjectInitializer.Initialize(_root);
        var edited = Path.Combine(_root, ".agents/skills/meshy-resoloop/scripts/meshy.py");
        var missing = Path.Combine(_root, ".agents/skills/meshy-resoloop/scripts/meshy_workflow.py");
        Assert.True(File.Exists(edited), $"Expected executable entry at {edited}");
        Assert.True(File.Exists(missing), $"Expected sibling module at {missing}");
        var original = File.ReadAllBytes(edited);
        var sibling = File.ReadAllBytes(missing);
        File.WriteAllText(edited, "user-edited executable\n");
        File.Delete(missing);
        var before = SnapshotProject();

        foreach (var update in new[] { false, true })
        {
            var error = Assert.Throws<RLoopException>(() => BundledSkillManager.Sync(_root, update));
            Assert.Equal("SKILL_SYNC_CONFLICT", error.Code);
            Assert.Equal(before, SnapshotProject());
        }

        File.WriteAllBytes(edited, original);
        var restored = BundledSkillManager.Sync(_root, update: true);
        Assert.True(restored.Synchronized);
        Assert.Equal(sibling, File.ReadAllBytes(missing));
        Assert.True(BundledSkillManager.Sync(_root, update: false).Synchronized);
    }

    // Catch refusing an older project or failing to add new executable resources.
    // Both historical schemas are supported; no lock-supplied write paths are trusted.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MeshySyncInstallsIntoPreMigrationLock(int schemaVersion)
    {
        ProjectInitializer.Initialize(_root);
        var skillDirectory = Path.Combine(_root, ".agents/skills/meshy-resoloop");
        Assert.True(Directory.Exists(skillDirectory), $"Expected skill at {skillDirectory}");
        Directory.Delete(skillDirectory, recursive: true);
        var lockPath = Path.Combine(_root, BundledSkillManager.LockRelativePath);
        var installed = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(lockPath))!;
        var oldFiles = installed["files"]!.AsObject().Where(pair => !pair.Key.StartsWith(
                "meshy-resoloop/", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>());
        File.WriteAllText(lockPath, schemaVersion == 2
            ? System.Text.Json.JsonSerializer.Serialize(new { schemaVersion, files = oldFiles })
            : System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion,
                skills = oldFiles.Where(pair => pair.Key.EndsWith("/SKILL.md", StringComparison.Ordinal))
                    .ToDictionary(pair => pair.Key[..^9], pair => pair.Value)
            }));
        var before = SnapshotProject();

        var check = BundledSkillManager.Sync(_root, update: false);
        Assert.False(check.Synchronized);
        Assert.Equal(before, SnapshotProject());
        var updated = BundledSkillManager.Sync(_root, update: true);

        Assert.True(updated.Synchronized);
        var entry = Path.Combine(skillDirectory, "scripts/meshy.py");
        Assert.NotEmpty(File.ReadAllBytes(entry));
        var migrated = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(lockPath))!;
        Assert.Equal(2, migrated["schemaVersion"]!.GetValue<int>());
        Assert.NotNull(migrated["files"]!["meshy-resoloop/scripts/meshy.py"]);
        Assert.Null(migrated["skills"]);
        Assert.True(BundledSkillManager.Sync(_root, update: false).Synchronized);
    }

    private (string Path, string? Bytes, DateTime Modified)[] SnapshotProject() =>
        Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .Prepend(_root)
            .Order(StringComparer.Ordinal)
            .Select(path => (System.IO.Path.GetRelativePath(_root, path).Replace('\\', '/'),
                File.Exists(path) ? Convert.ToHexString(File.ReadAllBytes(path)) : null,
                File.GetLastWriteTimeUtc(path))).ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
