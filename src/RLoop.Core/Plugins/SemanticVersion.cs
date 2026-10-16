using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RLoop.Core.Plugins;

/// <summary>SemVer 2.0.0 version. Build metadata is kept in <see cref="Text"/> and ignored when comparing.</summary>
[JsonConverter(typeof(SemanticVersionJsonConverter))]
public sealed partial class SemanticVersion : IComparable<SemanticVersion>
{
    private const int MaxLength = 128;
    private readonly string[] _core;
    private readonly string[] _prerelease;

    private SemanticVersion(string text, string[] core, string[] prerelease)
    {
        Text = text;
        _core = core;
        _prerelease = prerelease;
    }

    public string Text { get; }

    public static bool TryParse(string? text, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (text is null || text.Length > MaxLength) return false;
        var match = Pattern().Match(text);
        if (!match.Success) return false;
        version = new SemanticVersion(text,
            [match.Groups["major"].Value, match.Groups["minor"].Value, match.Groups["patch"].Value],
            match.Groups["pre"].Success ? match.Groups["pre"].Value.Split('.') : []);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < _core.Length; i++)
        {
            var core = CompareNumeric(_core[i], other._core[i]);
            if (core != 0) return core;
        }
        // A version without pre-release identifiers has higher precedence than one with them.
        if (_prerelease.Length == 0 || other._prerelease.Length == 0)
            return other._prerelease.Length.CompareTo(_prerelease.Length);
        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var left = _prerelease[i];
            var right = other._prerelease[i];
            var leftNumeric = left.All(char.IsAsciiDigit);
            var rightNumeric = right.All(char.IsAsciiDigit);
            var result = leftNumeric && rightNumeric ? CompareNumeric(left, right)
                : leftNumeric ? -1
                : rightNumeric ? 1
                : Math.Sign(string.CompareOrdinal(left, right));
            if (result != 0) return result;
        }
        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    public override string ToString() => Text;

    // SemVer forbids leading zeros in numeric identifiers, so a longer digit string is always the larger number.
    private static int CompareNumeric(string left, string right) =>
        left.Length != right.Length ? left.Length.CompareTo(right.Length) : Math.Sign(string.CompareOrdinal(left, right));

    [GeneratedRegex(@"^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)" +
                    @"(?:-(?<pre>(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?" +
                    @"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

public sealed class SemanticVersionJsonConverter : JsonConverter<SemanticVersion>
{
    public override SemanticVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        SemanticVersion.TryParse(reader.GetString(), out var version) ? version : throw new JsonException("Expected a SemVer 2.0 version string.");

    public override void Write(Utf8JsonWriter writer, SemanticVersion value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Text);
}
