using System.Text.RegularExpressions;

namespace RLoop.Core.Plugins;

public static partial class PluginNames
{
    public const int MinLength = 2;
    public const int MaxLength = 40;
    public const string ReservedSkillPrefix = "resonite-";
    public const string Rule = "^[a-z][a-z0-9]*(-[a-z0-9]+)*$ with 2..40 characters";

    // Top-level commands dispatched by RLoop.Cli Program (plus "plugins"). Add every new top-level command here.
    public static IReadOnlyList<string> BuiltinCommands { get; } =
    [
        "apply", "blender", "capture", "component", "diff", "discover", "doctor", "find", "flux", "help",
        "hierarchy", "init", "inspect", "item", "logs", "manifest", "observe", "ping", "plan", "plugin",
        "plugins", "scene", "schema", "skills", "slot", "status", "test", "tool", "type", "uix", "validate", "version"
    ];

    public static bool IsValid(string? name) =>
        name is { Length: >= MinLength and <= MaxLength } && Pattern().IsMatch(name);

    /// <summary>Names of <paramref name="manifest"/> that collide with built-ins or with another plugin's
    /// commands, skills or recipes (compared per kind).</summary>
    public static IReadOnlyList<string> FindConflicts(PluginManifest manifest, IEnumerable<PluginManifest> installed)
    {
        var conflicts = new List<string>();
        if (BuiltinCommands.Contains(manifest.Name)) conflicts.Add($"plugin name '{manifest.Name}' is a built-in command.");
        if (BundledSkillManager.Names.Contains(manifest.Name)) conflicts.Add($"plugin name '{manifest.Name}' is a bundled skill.");
        conflicts.AddRange(manifest.Commands.Where(command => BuiltinCommands.Contains(command.Name))
            .Select(command => $"command '{command.Name}' is a built-in command."));
        foreach (var skill in manifest.Skills)
        {
            if (BundledSkillManager.Names.Contains(skill.Name)) conflicts.Add($"skill '{skill.Name}' is a bundled skill.");
            else if (skill.Name.StartsWith(ReservedSkillPrefix, StringComparison.Ordinal))
                conflicts.Add($"skill '{skill.Name}' uses the reserved prefix '{ReservedSkillPrefix}'.");
        }
        conflicts.AddRange(manifest.Recipes.Where(recipe => UixRecipes.Catalog.Any(builtIn => builtIn.Name == recipe.Name))
            .Select(recipe => $"recipe '{recipe.Name}' is a built-in UIX recipe."));
        foreach (var other in installed.Where(other => other.Name != manifest.Name))
        {
            Overlap("command", manifest.Commands.Select(x => x.Name), other.Commands.Select(x => x.Name), other.Name, conflicts);
            Overlap("skill", manifest.Skills.Select(x => x.Name), other.Skills.Select(x => x.Name), other.Name, conflicts);
            Overlap("recipe", manifest.Recipes.Select(x => x.Name), other.Recipes.Select(x => x.Name), other.Name, conflicts);
        }
        return conflicts;
    }

    private static void Overlap(string kind, IEnumerable<string> names, IEnumerable<string> others, string plugin, List<string> conflicts) =>
        conflicts.AddRange(names.Intersect(others, StringComparer.Ordinal)
            .Select(name => $"{kind} '{name}' is also provided by plugin '{plugin}'."));

    [GeneratedRegex(@"^[a-z][a-z0-9]*(-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
