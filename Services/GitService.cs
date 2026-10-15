using System.IO;
using BeaconSetupManager.Models;

namespace BeaconSetupManager.Services;

public class GitService(IProcessRunner runner, ILogService log) : IGitService
{
    private Task<ProcessResult> Git(string path, CancellationToken token, Action<string>? output, params string[] args)
        => runner.RunAsync("git", string.Join(" ", args.Select(CommandArguments.Quote)), path, output, token);
    public async Task<GitValidationResult> VerifyRepositoryAccessAsync(string repoUrl, Dictionary<string, string>? environmentVariables = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repoUrl) || repoUrl.StartsWith('-')) return Failure("Enter a valid repository URL.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var result = await runner.RunAsync("git", $"ls-remote -- {CommandArguments.Quote(repoUrl)} HEAD", cancellationToken: timeout.Token, environmentVariables: environmentVariables);
            return result.ExitCode == 0 ? new() { Success = true } : Failure(Error(result));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Failure("Repository verification timed out. Check connectivity and credentials."); }
    }
    public async Task<bool> CloneRepositoryAsync(string repoUrl, string localPath, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        localPath = WorkspacePaths.Normalize(localPath);
        if (WorkspacePaths.HasRepository(localPath)) return (await ValidateOrigin(localPath, repoUrl, cancellationToken)).Success;
        if (Directory.Exists(localPath) && Directory.EnumerateFileSystemEntries(localPath).Any()) throw new IOException("Clone requires an empty destination. Existing contents have been retained.");
        runner.EnsureDirectoryExists(Path.GetDirectoryName(localPath)!);
        var result = await Git("", cancellationToken, onProgress, "clone", "--progress", "--", repoUrl, localPath);
        if (result.ExitCode != 0) onProgress?.Invoke("Clone failed. Existing and partially cloned files have been retained: " + Error(result));
        return result.ExitCode == 0 && WorkspacePaths.HasRepository(localPath);
    }
    private async Task<GitValidationResult> ValidateOrigin(string path, string url, CancellationToken token)
    {
        var origin = await Git(path, token, null, "config", "--get", "remote.origin.url");
        if (origin.ExitCode != 0 || !WorkspacePaths.SameRemote(origin.StandardOutput.Trim(), url)) return Failure("The workspace origin does not match the selected repository. Select the correct workspace.");
        return new() { Success = true };
    }
    public async Task<GitValidationResult> ValidateBranchOrCommitAsync(string repoUrl, string branchOrCommit, string localPath, Dictionary<string, string>? environmentVariables = null, CancellationToken cancellationToken = default)
    {
        var target = CommandArguments.ValidateReference(branchOrCommit);
        var access = await VerifyRepositoryAccessAsync(repoUrl, environmentVariables, cancellationToken);
        if (!access.Success) return access;
        if (!WorkspacePaths.HasRepository(localPath) && !await CloneRepositoryAsync(repoUrl, localPath, cancellationToken: cancellationToken)) return Failure("Could not clone repository for validation.");
        var origin = await ValidateOrigin(localPath, repoUrl, cancellationToken);
        if (!origin.Success) return origin;
        var fetch = await Git(localPath, cancellationToken, null, "fetch", "--tags", "origin");
        if (fetch.ExitCode != 0) return Failure("Fetch failed: " + Error(fetch));
        return await ResolveReference(localPath, target, cancellationToken);
    }
    private async Task<GitValidationResult> ResolveReference(string path, string target, CancellationToken token)
    {
        foreach (var reference in target == "HEAD" ? new[] { "HEAD" } : new[] { "refs/remotes/origin/" + target, target })
        {
            var resolved = await Git(path, token, null, "rev-parse", "--verify", "--end-of-options", reference + "^{commit}");
            if (resolved.ExitCode != 0) continue;
            var sha = resolved.StandardOutput.Trim();
            var branch = await Git(path, token, null, "show-ref", "--verify", "--quiet", "refs/heads/" + target);
            bool isBranch = reference.StartsWith("refs/remotes/", StringComparison.Ordinal) || branch.ExitCode == 0;
            var metadata = await Git(path, token, null, "show", "-s", "--format=%an%x00%aI%x00%s", sha);
            var fields = metadata.StandardOutput.TrimEnd('\r', '\n').Split('\0', 3);
            return new() { Success = true, SourceType = isBranch ? "Branch" : "Commit / Tag", ResolvedBranch = isBranch ? target : "Detached commit", CommitSha = sha, Author = fields.ElementAtOrDefault(0), Date = fields.ElementAtOrDefault(1), Message = fields.ElementAtOrDefault(2) };
        }
        return Failure($"Branch, tag, or commit '{target}' could not be resolved.");
    }
    public async Task<GitValidationResult> CheckoutAndValidateLocalAsync(string branchOrCommit, string localPath, GitCheckoutMode mode = GitCheckoutMode.Normal, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        string target = CommandArguments.ValidateReference(branchOrCommit);
        var fetch = await Git(localPath, cancellationToken, onProgress, "fetch", "--tags", "origin");
        if (fetch.ExitCode != 0) return Failure("Fetch failed: " + Error(fetch));
        var resolved = await ResolveReference(localPath, target, cancellationToken);
        if (!resolved.Success) return resolved;
        if (mode == GitCheckoutMode.Force && !await DiscardChangesAsync(localPath, cancellationToken)) return Failure("Could not discard the selected workspace's changes.");
        var arguments = new List<string> { "checkout" };
        if (mode == GitCheckoutMode.Merge) arguments.Add("-m");
        if (mode == GitCheckoutMode.Force) arguments.Add("-f");
        if (resolved.SourceType == "Branch")
        {
            var local = await Git(localPath, cancellationToken, null, "show-ref", "--verify", "--quiet", "refs/heads/" + target);
            if (local.ExitCode != 0) { arguments.Add("-b"); arguments.Add(target); arguments.Add("--track"); arguments.Add("origin/" + target); }
            else arguments.Add(target);
        }
        else { arguments.Add("--detach"); arguments.Add(resolved.CommitSha!); }
        var checkout = await Git(localPath, cancellationToken, onProgress, arguments.ToArray());
        if (checkout.ExitCode != 0) return Failure("Checkout failed: " + Error(checkout), HasConflicts(checkout));
        if (resolved.SourceType == "Branch")
        {
            var remote = await Git(localPath, cancellationToken, null, "show-ref", "--verify", "--quiet", "refs/remotes/origin/" + target);
            if (remote.ExitCode == 0)
            {
                var pull = await Git(localPath, cancellationToken, onProgress, "pull", "--ff-only", "origin", target);
                if (pull.ExitCode != 0) return Failure("Pull failed; the workspace was retained: " + Error(pull), HasConflicts(pull));
            }
        }
        var head = await ResolveReference(localPath, "HEAD", cancellationToken);
        return head.Success ? new() { Success = true, SourceType = resolved.SourceType, ResolvedBranch = resolved.ResolvedBranch, CommitSha = head.CommitSha, Author = head.Author, Date = head.Date, Message = head.Message } : Failure("Could not verify HEAD after checkout.");
    }
    public async Task<bool> SetupRepositoryAsync(string repoUrl, string branch, string localPath, bool forcePullLatest, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (!await CloneRepositoryAsync(repoUrl, localPath, onProgress, cancellationToken)) return false;
        if (!(await CheckoutAndValidateLocalAsync(branch, localPath, onProgress: onProgress, cancellationToken: cancellationToken)).Success) return false;
        return await UpdateSubmodulesAsync(localPath, onProgress, cancellationToken);
    }
    public async Task<bool> UpdateSubmodulesAsync(string localPath, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var sync = await Git(localPath, cancellationToken, onProgress, "submodule", "sync", "--recursive");
        return sync.ExitCode == 0 && (await Git(localPath, cancellationToken, onProgress, "submodule", "update", "--init", "--recursive", "--progress")).ExitCode == 0;
    }
    public async Task<GitValidationResult> ValidateExistingRepositoryAsync(string repoUrl, string localPath, CancellationToken cancellationToken = default)
    {
        if (!WorkspacePaths.HasRepository(localPath)) return Failure("The folder is not a Git repository.");
        var origin = await ValidateOrigin(localPath, repoUrl, cancellationToken);
        if (!origin.Success) return origin;
        var tree = await Git(localPath, cancellationToken, null, "rev-parse", "--is-inside-work-tree");
        return tree.ExitCode == 0 ? new() { Success = true } : Failure(Error(tree));
    }
    public static IReadOnlyList<string> ParseStatus(string output)
    {
        var paths = new List<string>();
        var records = output.Split('\0');
        for (int i = 0; i < records.Length; i++)
        {
            var record = records[i].TrimEnd('\r', '\n');
            if (record.Length < 4) continue;
            paths.Add(record[3..]);
            if (record[0] is 'R' or 'C' || record[1] is 'R' or 'C') i++;
        }
        return paths;
    }
    public async Task<RepositoryAnalysisResult> AnalyzeRepositoryAsync(string localPath, string repoUrl, string targetBranch, CancellationToken cancellationToken = default)
    {
        var result = new RepositoryAnalysisResult();
        var directory = await Git(localPath, cancellationToken, null, "rev-parse", "--absolute-git-dir");
        if (directory.ExitCode != 0)
        {
            var error = Error(directory);
            result.State = error.Contains("ownership", StringComparison.OrdinalIgnoreCase) ? RepositoryState.UnsafeRepository : RepositoryState.NotARepository;
            result.ErrorMessage = error;
            return result;
        }
        var gitDir = directory.StandardOutput.Trim();
        result.IsHealthy = true;
        result.IsRemoteConnected = string.IsNullOrEmpty(repoUrl) || (await VerifyRepositoryAccessAsync(repoUrl, cancellationToken: cancellationToken)).Success;
        result.CurrentBranch = (await Git(localPath, cancellationToken, null, "rev-parse", "--abbrev-ref", "HEAD")).StandardOutput.Trim();
        if (new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD" }.Any(file => File.Exists(Path.Combine(gitDir, file))) || new[] { "rebase-merge", "rebase-apply", "sequencer" }.Any(file => Directory.Exists(Path.Combine(gitDir, file))))
        { result.State = RepositoryState.MergeRebaseInProgress; result.ErrorMessage = "A merge, rebase, cherry-pick, or revert is in progress."; return result; }
        var status = await Git(localPath, cancellationToken, null, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=none");
        var submodules = await Git(localPath, cancellationToken, null, "submodule", "status", "--recursive");
        if (status.ExitCode != 0 || submodules.ExitCode != 0) { result.IsHealthy = false; result.State = RepositoryState.Unknown; result.ErrorMessage = Error(status.ExitCode != 0 ? status : submodules); return result; }
        result.ModifiedFiles = ParseStatus(status.StandardOutput).ToList();
        result.IsWorkingTreeClean = result.ModifiedFiles.Count == 0;
        var lines = submodules.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        result.UninitializedSubmodules = lines.Where(line => line.StartsWith('-')).Select(line => line.Length > 42 ? line[42..].Trim() : line).ToList();
        result.SubmoduleStatus = lines.Any(line => line.StartsWith('U')) ? "Merge Conflict" : lines.Any(line => line.StartsWith('-')) ? "Uninitialized" : lines.Any(line => line.StartsWith('+')) ? "Out of Sync" : "Healthy";
        var metadata = ReadSetupMetadata(localPath);
        result.SetupProgressSummary = metadata == null ? "Not Started" : $"Saved checkpoint: {metadata.LastCompletedStep}";
        result.State = !result.IsWorkingTreeClean ? RepositoryState.LocalModifications : result.SubmoduleStatus == "Uninitialized" ? RepositoryState.SubmodulesUninitialized : result.SubmoduleStatus != "Healthy" ? RepositoryState.SubmoduleInitializationFailed : metadata != null && !metadata.CompletedSteps.Contains(SetupStepType.ConnectionValidation) ? RepositoryState.SetupInterrupted : RepositoryState.Clean;
        log.LogInfo("Repository Analysis", $"Workspace: {localPath}; state: {result.State}; modified paths: {result.ModifiedFiles.Count}; submodules: {result.SubmoduleStatus}.");
        return result;
    }
    public async Task<bool> IsWorkingTreeCleanAsync(string localPath, string targetBranchOrCommit = "", CancellationToken cancellationToken = default) => (await AnalyzeRepositoryAsync(localPath, "", targetBranchOrCommit, cancellationToken)).IsWorkingTreeClean;
    public async Task<bool> AbortMergeOrRebaseAsync(string localPath, CancellationToken cancellationToken = default)
    {
        var location = await Git(localPath, cancellationToken, null, "rev-parse", "--absolute-git-dir");
        if (location.ExitCode != 0) return false;
        var dir = location.StandardOutput.Trim();
        var action = File.Exists(Path.Combine(dir, "MERGE_HEAD")) ? "merge" : File.Exists(Path.Combine(dir, "CHERRY_PICK_HEAD")) ? "cherry-pick" : File.Exists(Path.Combine(dir, "REVERT_HEAD")) ? "revert" : "rebase";
        return (await Git(localPath, cancellationToken, null, action, "--abort")).ExitCode == 0;
    }
    public async Task<bool> StashChangesAsync(string localPath, CancellationToken cancellationToken = default)
    {
        // Stash submodule contents before the root can stash .gitmodules or gitlink changes.
        if ((await Git(localPath, cancellationToken, null, "submodule", "foreach", "--recursive", "git stash push --include-untracked -m BeaconSetupRecovery")).ExitCode != 0) return false;
        return (await Git(localPath, cancellationToken, null, "stash", "push", "--include-untracked", "-m", "Beacon Setup Manager recovery")).ExitCode == 0;
    }
    public async Task<bool> DiscardChangesAsync(string localPath, CancellationToken cancellationToken = default)
    {
        if ((await Git(localPath, cancellationToken, null, "submodule", "foreach", "--recursive", "git reset --hard HEAD && git clean -fd")).ExitCode != 0) return false;
        return (await Git(localPath, cancellationToken, null, "reset", "--hard", "HEAD")).ExitCode == 0 && (await Git(localPath, cancellationToken, null, "clean", "-fd")).ExitCode == 0;
    }
    public Task<bool> RepairRepositoryAsync(string localPath, Action<string>? onProgress, CancellationToken cancellationToken = default) => UpdateSubmodulesAsync(localPath, onProgress, cancellationToken);
    public async Task<bool> AttemptSafeAutoRecoveryAsync(string localPath, CancellationToken cancellationToken = default) => (await Git(localPath, cancellationToken, null, "submodule", "sync", "--recursive")).ExitCode == 0;
    public void CleanUpDirectory(string path)
    {
        WorkspacePaths.ValidateDeletion(path);
        if (File.Exists(Path.Combine(path, ".git"))) throw new IOException("Remove linked worktrees with Git, not Start Fresh.");
        void Check(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Remove directory links before starting fresh.");
                if ((attributes & FileAttributes.Directory) != 0) Check(entry);
            }
        }
        Check(path);
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(WorkspacePaths.Normalize(path), true);
        SettingsStore.Update(MetadataPath, () => new Dictionary<string, SetupMetadata>(), entries => { entries.Remove(WorkspacePaths.Key(path)); return entries; });
    }
    private static string MetadataPath => Path.Combine(SettingsStore.DirectoryPath, "setup_progress.json");
    public SetupMetadata? ReadSetupMetadata(string localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath)) return null;
        var entries = SettingsStore.Read(MetadataPath, () => new Dictionary<string, SetupMetadata>());
        return entries.TryGetValue(WorkspacePaths.Key(localPath), out var value) && value.CompletedSteps.Count > 0 ? value : null;
    }
    public void WriteSetupMetadata(string localPath, SetupMetadata metadata)
    {
        metadata.WorkspacePath = WorkspacePaths.Key(localPath);
        metadata.SetupVersion = typeof(GitService).Assembly.GetName().Version?.ToString() ?? "";
        SettingsStore.Update(MetadataPath, () => new Dictionary<string, SetupMetadata>(), entries => { entries[metadata.WorkspacePath] = metadata; return entries; });
    }
    private static string Error(ProcessResult result) => string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput.Trim() : result.StandardError.Trim();
    private static GitValidationResult Failure(string message, bool conflicts = false) => new() { ErrorMessage = message, HasCheckoutConflicts = conflicts };
    private static bool HasConflicts(ProcessResult result) => (result.StandardOutput + result.StandardError).Contains("overwritten", StringComparison.OrdinalIgnoreCase);
}
