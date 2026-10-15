namespace RLoop.Core.Plugins;

/// <summary>Error codes of the plugin core. Every code maps to one exit code so callers cannot drift.</summary>
public static class PluginErrors
{
    public const string ProjectNotFound = "PLUGIN_PROJECT_NOT_FOUND";
    public const string SourceNotFound = "PLUGIN_SOURCE_NOT_FOUND";
    public const string SourceInvalid = "PLUGIN_SOURCE_INVALID";
    public const string GitRefRequired = "PLUGIN_GIT_REF_REQUIRED";
    public const string GitRefNotFound = "PLUGIN_GIT_REF_NOT_FOUND";
    public const string GitFailed = "PLUGIN_GIT_FAILED";
    public const string ManifestNotFound = "PLUGIN_MANIFEST_NOT_FOUND";
    public const string ManifestInvalid = "PLUGIN_MANIFEST_INVALID";
    public const string SchemaUnsupported = "PLUGIN_SCHEMA_UNSUPPORTED";
    public const string IncompatibleVersion = "PLUGIN_INCOMPATIBLE_VERSION";
    public const string NameConflict = "PLUGIN_NAME_CONFLICT";
    public const string AlreadyInstalled = "PLUGIN_ALREADY_INSTALLED";
    public const string NotInstalled = "PLUGIN_NOT_INSTALLED";
    public const string NameInvalid = "PLUGIN_NAME_INVALID";
    public const string RecordInvalid = "PLUGIN_RECORD_INVALID";
    public const string Modified = "PLUGIN_MODIFIED";
    public const string RemoveRefused = "PLUGIN_REMOVE_REFUSED";

    private static readonly IReadOnlyDictionary<string, int> ExitCodeByCode = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [ProjectNotFound] = ExitCodes.ConfigurationError,
        [SourceNotFound] = ExitCodes.NotFound,
        [SourceInvalid] = ExitCodes.ValidationFailed,
        [GitRefRequired] = ExitCodes.InvalidArguments,
        [GitRefNotFound] = ExitCodes.NotFound,
        [GitFailed] = ExitCodes.ExternalToolFailed,
        [ManifestNotFound] = ExitCodes.ValidationFailed,
        [ManifestInvalid] = ExitCodes.ValidationFailed,
        [SchemaUnsupported] = ExitCodes.ValidationFailed,
        [IncompatibleVersion] = ExitCodes.ValidationFailed,
        [NameConflict] = ExitCodes.ValidationFailed,
        [AlreadyInstalled] = ExitCodes.ValidationFailed,
        [NotInstalled] = ExitCodes.NotFound,
        [NameInvalid] = ExitCodes.InvalidArguments,
        [RecordInvalid] = ExitCodes.ValidationFailed,
        [Modified] = ExitCodes.ValidationFailed,
        [RemoveRefused] = ExitCodes.ValidationFailed
    };

    public static RLoopException Create(string code, string message, IReadOnlyDictionary<string, object?>? context = null,
        IReadOnlyList<string>? suggestions = null, Exception? innerException = null) =>
        new(code, message, ExitCodeByCode[code], context, suggestions, innerException);
}
