using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace RLoop.Core.Plugins;

internal sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs the git CLI without prompts. Installation runs git only; it never runs plugin code.</summary>
internal static class PluginGit
{
    public static async Task<GitResult> RunAsync(string? workingDirectory, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.fsmonitor=false");
        if (workingDirectory is not null)
        {
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(workingDirectory);
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            throw PluginErrors.Create(PluginErrors.GitFailed, $"Cannot start git: {ex.Message}",
                suggestions: ["Install git and make sure it is on PATH."], innerException: ex);
        }
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        return new GitResult(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>Tracked files plus untracked files that are not ignored, when <paramref name="directory"/> is the top level of a
    /// work tree. Null when git is missing or the directory is not a work tree top level (for example a folder that an
    /// enclosing repository ignores), so the caller copies the directory as it is.</summary>
    public static async Task<IReadOnlyList<string>?> ListWorkTreeFilesAsync(string directory, CancellationToken cancellationToken)
    {
        GitResult top;
        try { top = await RunAsync(directory, ["rev-parse", "--show-toplevel"], cancellationToken); }
        catch (RLoopException ex) when (ex.Code == PluginErrors.GitFailed) { return null; }
        if (top.ExitCode != 0 || !SamePath(top.StandardOutput.Trim(), directory)) return null;
        var files = await RunAsync(directory, ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], cancellationToken);
        if (files.ExitCode != 0) throw Failed("git ls-files", files, directory);
        return files.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Clones <paramref name="url"/> into <paramref name="checkout"/>, resolves <paramref name="gitRef"/> to a commit and
    /// checks it out. Symlinks and submodules are refused before any file is written to the work tree.</summary>
    public static async Task<(string Commit, IReadOnlyList<string> Files)> CheckoutAsync(string url, string gitRef, string checkout,
        CancellationToken cancellationToken)
    {
        var clone = await RunAsync(null, ["clone", "--quiet", "--no-checkout", "--", url, checkout], cancellationToken);
        if (clone.ExitCode != 0) throw Failed("git clone", clone, url);
        var commit = await ResolveAsync(checkout, gitRef, cancellationToken) ?? throw PluginErrors.Create(PluginErrors.GitRefNotFound,
            $"'{gitRef}' is not a tag, branch or commit of {url}.", new Dictionary<string, object?> { ["url"] = url, ["ref"] = gitRef },
            ["List the refs with git ls-remote URL and pass an existing one to --ref."]);
        var tree = await RunAsync(checkout, ["ls-tree", "-r", "-z", "--full-tree", commit], cancellationToken);
        if (tree.ExitCode != 0) throw Failed("git ls-tree", tree, url);
        var files = new List<string>();
        var refused = new List<string>();
        foreach (var entry in tree.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = entry[(entry.IndexOf('\t') + 1)..];
            if (entry.StartsWith("100644 ", StringComparison.Ordinal) || entry.StartsWith("100755 ", StringComparison.Ordinal)) files.Add(path);
            else refused.Add(path);
        }
        if (refused.Count > 0)
            throw PluginErrors.Create(PluginErrors.SourceInvalid,
                $"{url} at {commit} contains symlinks or submodules, which plugins cannot use: {string.Join(", ", refused.Take(5))}",
                new Dictionary<string, object?> { ["url"] = url, ["commit"] = commit, ["refused"] = refused },
                ["Replace symlinks with regular files and copy submodule content into the plugin repository."]);
        var result = await RunAsync(checkout, ["-c", "advice.detachedHead=false", "checkout", "--quiet", "--detach", commit], cancellationToken);
        if (result.ExitCode != 0) throw Failed("git checkout", result, url);
        files.Sort(StringComparer.Ordinal);
        return (commit, files);
    }

    private static async Task<string?> ResolveAsync(string checkout, string gitRef, CancellationToken cancellationToken)
    {
        // Tags, full or short commits and the default branch resolve directly; other branches exist only as origin/<name>.
        foreach (var candidate in new[] { gitRef, "refs/remotes/origin/" + gitRef })
        {
            var result = await RunAsync(checkout, ["rev-parse", "--verify", "--quiet", "--end-of-options", candidate + "^{commit}"],
                cancellationToken);
            if (result.ExitCode == 0) return result.StandardOutput.Trim();
        }
        return null;
    }

    private static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static RLoopException Failed(string operation, GitResult result, string location) =>
        PluginErrors.Create(PluginErrors.GitFailed, $"{operation} failed with exit code {result.ExitCode}: {result.StandardError.Trim()}",
            new Dictionary<string, object?> { ["location"] = location, ["exitCode"] = result.ExitCode },
            ["Check the URL, network access and credentials (use a git credential helper)."]);
}
