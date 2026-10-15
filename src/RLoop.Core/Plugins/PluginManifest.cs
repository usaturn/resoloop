using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RLoop.Core.Plugins;

public sealed record PluginVersionRange(SemanticVersion MinVersion, SemanticVersion? MaxVersionExclusive)
{
    public bool Contains(SemanticVersion version) =>
        version.CompareTo(MinVersion) >= 0 && (MaxVersionExclusive is null || version.CompareTo(MaxVersionExclusive) < 0);

    public override string ToString() =>
        MaxVersionExclusive is null ? $">= {MinVersion}" : $">= {MinVersion} and < {MaxVersionExclusive}";
}

public sealed record PluginCommand(string Name, string Description, string Program, IReadOnlyList<string> Args, int? TimeoutSeconds);

public sealed record PluginSkill(string Name, string Path);

public sealed record PluginRecipe(string Name, string Path, string Prototype, string Purpose,
    IReadOnlyDictionary<string, string> Parameters, IReadOnlyDictionary<string, string> Ports, IReadOnlyList<string> Requirements);

public sealed record PluginTool(string Name, string Executable, IReadOnlyList<string> VersionArgs, bool Required, string? Hint);

/// <summary>resoloop-plugin.json, schema version 1. Unknown fields are rejected so typos never pass silently.</summary>
public sealed partial record PluginManifest(
    int SchemaVersion,
    string Name,
    SemanticVersion Version,
    string? Description,
    PluginVersionRange Resoloop,
    IReadOnlyList<PluginCommand> Commands,
    IReadOnlyList<PluginSkill> Skills,
    IReadOnlyList<PluginRecipe> Recipes,
    IReadOnlyList<PluginTool> Tools)
{
    public const string FileName = "resoloop-plugin.json";
    public const int CurrentSchemaVersion = 1;
    public const int MaxBytes = 256 * 1024;
    public const string PluginRootToken = "${pluginRoot}";
    public const string PluginDataToken = "${pluginData}";
    private const int MaxText = 500;
    private const int MaxPath = 260;

    /// <summary>Reads and validates the manifest of an unpacked plugin. <paramref name="files"/> lists every plugin file as a
    /// '/'-separated path relative to <paramref name="rootDirectory"/>; every path the manifest references must be listed.</summary>
    public static PluginManifest Load(string rootDirectory, IReadOnlyCollection<string> files)
    {
        if (!files.Contains(FileName))
            throw PluginErrors.Create(PluginErrors.ManifestNotFound, $"Plugin manifest '{FileName}' was not found in {rootDirectory}.",
                new Dictionary<string, object?> { ["rootDirectory"] = rootDirectory }, [$"Put {FileName} at the plugin root."]);
        var path = System.IO.Path.Combine(rootDirectory, FileName);
        byte[] bytes;
        try
        {
            if (new FileInfo(path).Length > MaxBytes) throw Invalid([$"$: the manifest is larger than {MaxBytes} bytes."]);
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw PluginErrors.Create(PluginErrors.ManifestInvalid, $"Cannot read plugin manifest '{path}': {ex.Message}", innerException: ex);
        }
        var manifest = Parse(bytes);
        var errors = new List<string>();
        manifest.ValidateFiles(rootDirectory, files as IReadOnlySet<string> ?? files.ToHashSet(StringComparer.Ordinal), errors);
        if (errors.Count > 0) throw Invalid(errors);
        return manifest;
    }

    /// <summary>Parses the manifest JSON and validates every field that does not depend on plugin files.</summary>
    public static PluginManifest Parse(byte[] utf8Json)
    {
        ReadOnlyMemory<byte> json = utf8Json;
        if (json.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) json = json[3..];
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException ex) { throw Invalid([$"$: not valid JSON: {ex.Message}"]); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid(["$: must be a JSON object."]);
            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion))
                throw Invalid(["$.schemaVersion: is required and must be an integer."]);
            if (schemaVersion != CurrentSchemaVersion)
                throw PluginErrors.Create(PluginErrors.SchemaUnsupported,
                    $"Plugin manifest schemaVersion {schemaVersion} is not supported; this resoloop reads schemaVersion {CurrentSchemaVersion}.",
                    new Dictionary<string, object?> { ["schemaVersion"] = schemaVersion },
                    ["Use a plugin release written for this resoloop, or update resoloop."]);

            var errors = new List<string>();
            var node = Node.Of(root, "$", errors,
                ["schemaVersion", "name", "version", "description", "resoloop", "commands", "skills", "recipes", "tools"])!;
            var name = node.Name("name");
            var version = node.Version("version", required: true);
            var description = node.Text("description", required: false);
            var range = ParseRange(node.Object("resoloop", required: true, ["minVersion", "maxVersionExclusive"]));
            var commands = node.Objects("commands", 50, ["name", "description", "program", "args", "timeoutSeconds"]).Select(ParseCommand).ToArray();
            var skills = node.Objects("skills", 50, ["name", "path"]).Select(ParseSkill).ToArray();
            var recipes = node.Objects("recipes", 50,
                ["name", "path", "prototype", "purpose", "parameters", "ports", "requirements"]).Select(ParseRecipe).ToArray();
            var tools = node.Objects("tools", 20, ["name", "executable", "versionArgs", "required", "hint"]).Select(ParseTool).ToArray();
            RequireUnique("commands", commands.Select(x => x.Name), errors);
            RequireUnique("skills", skills.Select(x => x.Name), errors);
            RequireUnique("recipes", recipes.Select(x => x.Name), errors);
            RequireUnique("tools", tools.Select(x => x.Name), errors);
            if (errors.Count > 0) throw Invalid(errors);
            return new PluginManifest(schemaVersion, name!, version!, description, range!, commands, skills, recipes, tools);
        }
    }

    public void EnsureCompatibleWith(string resoloopVersion)
    {
        if (SemanticVersion.TryParse(resoloopVersion, out var current) && Resoloop.Contains(current)) return;
        throw PluginErrors.Create(PluginErrors.IncompatibleVersion,
            $"Plugin '{Name}' {Version} supports resoloop {Resoloop}; this is resoloop {resoloopVersion}.",
            new Dictionary<string, object?>
            {
                ["plugin"] = Name, ["minVersion"] = Resoloop.MinVersion.Text,
                ["maxVersionExclusive"] = Resoloop.MaxVersionExclusive?.Text, ["resoloopVersion"] = resoloopVersion
            },
            ["Install a plugin release that supports this resoloop version, or change resoloop."]);
    }

    /// <summary>A '/'-separated relative path that cannot leave the plugin root on any supported platform.</summary>
    public static bool IsSafeRelativePath(string path) =>
        path.Length is > 0 and <= MaxPath && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') &&
        !path.Any(char.IsControl) && path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");

    internal static bool IsCommandName(string text) => CommandNamePattern().IsMatch(text) && text.Trim('.').Length > 0;

    private void ValidateFiles(string rootDirectory, IReadOnlySet<string> files, List<string> errors)
    {
        for (var i = 0; i < Commands.Count; i++)
            if (Commands[i].Program.StartsWith("./", StringComparison.Ordinal) && !files.Contains(Commands[i].Program[2..]))
                errors.Add($"$.commands[{i}].program: '{Commands[i].Program}' is not a file in the plugin.");
        for (var i = 0; i < Skills.Count; i++)
            if (!files.Contains(Skills[i].Path + "/SKILL.md"))
                errors.Add($"$.skills[{i}].path: '{Skills[i].Path}/SKILL.md' is not a file in the plugin.");
        for (var i = 0; i < Recipes.Count; i++)
        {
            var recipe = Recipes[i];
            var where = $"$.recipes[{i}].path";
            if (!files.Contains(recipe.Path))
            {
                errors.Add($"{where}: '{recipe.Path}' is not a file in the plugin.");
                continue;
            }
            try
            {
                var full = System.IO.Path.Combine(rootDirectory, recipe.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (JsonNode.Parse(File.ReadAllText(full)) is not JsonObject root ||
                    root["prototypes"] is not JsonObject prototypes || prototypes[recipe.Prototype] is not JsonObject)
                    errors.Add($"{where}: '{recipe.Path}' must be a JSON object whose prototypes.{recipe.Prototype} is an object.");
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{where}: cannot read '{recipe.Path}' as JSON: {ex.Message}");
            }
        }
    }

    private static PluginVersionRange? ParseRange(Node? node)
    {
        if (node is null) return null;
        var min = node.Version("minVersion", required: true);
        var max = node.Version("maxVersionExclusive", required: false);
        if (min is not null && max is not null && max.CompareTo(min) <= 0)
            node.Error("maxVersionExclusive", "must be greater than minVersion.");
        return min is null ? null : new PluginVersionRange(min, max);
    }

    private static PluginCommand ParseCommand(Node node)
    {
        var name = node.Name("name");
        var description = node.Text("description", required: true);
        var program = node.Text("program", required: true, MaxPath);
        if (program is not null && !(program.StartsWith("./", StringComparison.Ordinal) ? IsSafeRelativePath(program[2..]) : IsCommandName(program)))
            node.Error("program", "must be './<relative path in the plugin>' or a command name on PATH ([A-Za-z0-9._+-], no path separators).");
        var args = node.Strings("args");
        for (var i = 0; i < args.Count; i++)
            if (args[i].Replace(PluginRootToken, "", StringComparison.Ordinal).Replace(PluginDataToken, "", StringComparison.Ordinal)
                .Contains("${", StringComparison.Ordinal))
                node.Error($"args[{i}]", $"only {PluginRootToken} and {PluginDataToken} may be substituted.");
        var timeout = node.Integer("timeoutSeconds", 1, 86400);
        return new PluginCommand(name!, description!, program!, args, timeout);
    }

    private static PluginSkill ParseSkill(Node node) => new(node.Name("name")!, node.RelativePath("path")!);

    private static PluginRecipe ParseRecipe(Node node)
    {
        var name = node.Name("name");
        var path = node.RelativePath("path");
        var prototype = node.Text("prototype", required: true, 100);
        var purpose = node.Text("purpose", required: true);
        var parameters = node.Map("parameters", required: true);
        if (node.Has("parameters") && !parameters.ContainsKey("key")) node.Error("parameters", "must include 'key'.");
        var ports = node.Map("ports", required: false);
        var requirements = node.Strings("requirements");
        return new PluginRecipe(name!, path!, prototype!, purpose!, parameters, ports, requirements);
    }

    private static PluginTool ParseTool(Node node)
    {
        var name = node.Name("name");
        var executable = node.Text("executable", required: true, 100);
        if (executable is not null && !IsCommandName(executable))
            node.Error("executable", "must be a command name on PATH ([A-Za-z0-9._+-], no path separators).");
        var versionArgs = node.Strings("versionArgs");
        var required = node.Boolean("required") ?? true;
        var hint = node.Text("hint", required: false);
        return new PluginTool(name!, executable!, versionArgs, required, hint);
    }

    private static void RequireUnique(string list, IEnumerable<string?> names, List<string> errors) =>
        errors.AddRange(names.OfType<string>().GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .Select(group => $"$.{list}: name '{group.Key}' is duplicated."));

    private static RLoopException Invalid(IReadOnlyList<string> errors) =>
        PluginErrors.Create(PluginErrors.ManifestInvalid,
            $"Plugin manifest is invalid: {errors[0]}" + (errors.Count > 1 ? $" (+{errors.Count - 1} more)" : ""),
            new Dictionary<string, object?> { ["errors"] = errors }, [$"Fix every listed field in {FileName}."]);

    [GeneratedRegex(@"^[A-Za-z0-9._+][A-Za-z0-9._+-]{0,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommandNamePattern();

    /// <summary>One JSON object being validated. Errors are collected with JSON paths instead of stopping at the first.</summary>
    private sealed class Node
    {
        private readonly Dictionary<string, JsonElement> _fields;
        private readonly List<string> _errors;

        private Node(string path, Dictionary<string, JsonElement> fields, List<string> errors)
        {
            Path = path;
            _fields = fields;
            _errors = errors;
        }

        public string Path { get; }

        public static Node? Of(JsonElement element, string path, List<string> errors, IReadOnlyCollection<string> allowed)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path}: must be an object.");
                return null;
            }
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!allowed.Contains(property.Name)) errors.Add($"{path}.{property.Name}: is not a known field.");
                else if (!fields.TryAdd(property.Name, property.Value)) errors.Add($"{path}.{property.Name}: is duplicated.");
            }
            return new Node(path, fields, errors);
        }

        public bool Has(string name) => _fields.ContainsKey(name);

        public void Error(string field, string message) => _errors.Add($"{Path}.{field}: {message}");

        public string? Text(string name, bool required, int maxLength = MaxText)
        {
            if (!TryGet(name, required, out var value)) return null;
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && text.Length <= maxLength) return text;
            Error(name, $"must be a string of 1..{maxLength} characters.");
            return null;
        }

        public string? Name(string name)
        {
            if (!TryGet(name, required: true, out var value)) return null;
            if (value.ValueKind == JsonValueKind.String && PluginNames.IsValid(value.GetString())) return value.GetString();
            Error(name, $"must be a string matching {PluginNames.Rule}.");
            return null;
        }

        public string? RelativePath(string name)
        {
            var path = Text(name, required: true, MaxPath);
            if (path is null || IsSafeRelativePath(path)) return path;
            Error(name, "must be a '/'-separated relative path inside the plugin (no '..', '.', '\\', ':' or leading '/').");
            return null;
        }

        public SemanticVersion? Version(string name, bool required)
        {
            var text = Text(name, required, 128);
            if (text is null) return null;
            if (SemanticVersion.TryParse(text, out var version)) return version;
            Error(name, $"'{text}' is not a SemVer 2.0 version.");
            return null;
        }

        public int? Integer(string name, int min, int max)
        {
            if (!TryGet(name, required: false, out var value)) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= min && number <= max) return number;
            Error(name, $"must be an integer in {min}..{max}.");
            return null;
        }

        public bool? Boolean(string name)
        {
            if (!TryGet(name, required: false, out var value)) return null;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
            Error(name, "must be true or false.");
            return null;
        }

        public IReadOnlyList<string> Strings(string name)
        {
            if (!TryGet(name, required: false, out var value)) return [];
            if (value.ValueKind != JsonValueKind.Array)
            {
                Error(name, "must be an array of strings.");
                return [];
            }
            var result = new List<string>();
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) result.Add(item.GetString()!);
                else Error($"{name}[{index}]", "must be a string.");
                index++;
            }
            return result;
        }

        public IReadOnlyDictionary<string, string> Map(string name, bool required)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!TryGet(name, required, out var value)) return map;
            if (value.ValueKind != JsonValueKind.Object)
            {
                Error(name, "must be an object whose values are strings.");
                return map;
            }
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { Length: > 0 and <= MaxText } text)
                    Error($"{name}.{property.Name}", $"must be a string of 1..{MaxText} characters.");
                else if (property.Name.Length == 0 || !map.TryAdd(property.Name, text))
                    Error($"{name}.{property.Name}", "must be a unique, non-empty key.");
            }
            return map;
        }

        public Node? Object(string name, bool required, IReadOnlyCollection<string> allowed) =>
            TryGet(name, required, out var value) ? Of(value, $"{Path}.{name}", _errors, allowed) : null;

        public IReadOnlyList<Node> Objects(string name, int maxItems, IReadOnlyCollection<string> allowed)
        {
            if (!TryGet(name, required: false, out var value)) return [];
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maxItems)
            {
                Error(name, $"must be an array of at most {maxItems} objects.");
                return [];
            }
            return value.EnumerateArray().Select((item, index) => Of(item, $"{Path}.{name}[{index}]", _errors, allowed))
                .OfType<Node>().ToArray();
        }

        private bool TryGet(string name, bool required, out JsonElement value)
        {
            if (_fields.TryGetValue(name, out value)) return true;
            if (required) Error(name, "is required.");
            return false;
        }
    }
}
