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
        // Credential Manager may require first-time sign-in or MFA. Only the user's cancellation
        // should close that interactive flow; a wall-clock deadline also kills its prompt window.
        var result = await runner.RunAsync("git", $"ls-remote -- {CommandArguments.Quote(repoUrl)} HEAD", cancellationToken: cancellationToken, environmentVariables: environmentVariables);
        return result.ExitCode == 0 ? new() { Success = true } : Failure(Error(result));
    }
    public async Task<bool> CloneRepositoryAsync(string repoUrl, string localPath, Action<string>? onProgress = null, CancellationToken cancellationToken = default, string? sourceReference = null)
    {
        localPath = WorkspacePaths.Normalize(localPath);
        if (WorkspacePaths.HasRepository(localPath)) return (await ValidateOrigin(localPath, repoUrl, cancellationToken)).Success;
        if (Directory.Exists(localPath) && Directory.EnumerateFileSystemEntries(localPath).Any()) throw new IOException("Clone requires an empty destination. Existing contents have been retained.");
        runner.EnsureDirectoryExists(Path.GetDirectoryName(localPath)!);
        var args = new List<string> { "clone", "--depth", "1", "--single-branch", "--no-tags", "--progress" };
        if (!string.IsNullOrWhiteSpace(sourceReference))
        {
            string source = CommandArguments.ValidateReference(sourceReference);
            if (source != "HEAD" && !System.Text.RegularExpressions.Regex.IsMatch(source, "^[0-9a-fA-F]{4,64}$"))
            { args.Add("--branch"); args.Add(source); }
        }
        args.AddRange(["--", repoUrl, localPath]);
        onProgress?.Invoke("Cloning one branch with depth 1; unrelated branches and tags are excluded.");
        var result = await Git("", cancellationToken, onProgress, args.ToArray());
        if (result.ExitCode != 0) onProgress?.Invoke("Clone failed. Existing and partially cloned files have been retained: " + Error(result));
        if (result.ExitCode != 0 || !WorkspacePaths.HasRepository(localPath)) return false;
        var accessible = await Git(localPath, cancellationToken, null, "rev-parse", "--is-inside-work-tree");
        // A user-selected empty folder may have been created by Explorer/PowerShell as Administrator.
        // Only a clone completed by this call is eligible for automatic ownership repair.
        if (accessible.ExitCode != 0 && IsOwnershipError(accessible))
        {
            onProgress?.Invoke("The newly cloned folder has a different owner. Correcting its owner to the signed-in user.");
            if (runner.FixDirectoryOwnership(localPath))
                accessible = await Git(localPath, cancellationToken, null, "rev-parse", "--is-inside-work-tree");
        }
        if (accessible.ExitCode != 0) onProgress?.Invoke(WorkspaceAccessFailure(accessible).ErrorMessage!);
        return accessible.ExitCode == 0;
    }
    internal static bool IsOwnershipError(ProcessResult result) =>
        result.StandardError.Contains("ownership", StringComparison.OrdinalIgnoreCase) ||
        result.StandardError.Contains("unsafe repository", StringComparison.OrdinalIgnoreCase) ||
        result.StandardError.Contains("owned by someone else", StringComparison.OrdinalIgnoreCase);

    private static GitValidationResult WorkspaceAccessFailure(ProcessResult result) => IsOwnershipError(result)
        ? Failure("Git cannot access this folder because its owner differs from the signed-in user. Retry Setup or use Repair and choose Fix Ownership. The completed clone is retained; no new clone is needed.")
        : Failure("Git cannot access this workspace: " + Error(result));
    private async Task<GitValidationResult> ValidateOrigin(string path, string url, CancellationToken token)
    {
        var accessible = await Git(path, token, null, "rev-parse", "--is-inside-work-tree");
        if (accessible.ExitCode != 0) return WorkspaceAccessFailure(accessible);
        var origin = await Git(path, token, null, "config", "--get", "remote.origin.url");
        if (origin.ExitCode != 0 || !WorkspacePaths.SameRemote(origin.StandardOutput.Trim(), url)) return Failure("The workspace origin does not match the selected repository. Select the correct workspace.");
        return new() { Success = true };
    }
    public async Task<GitValidationResult> ValidateBranchOrCommitAsync(string repoUrl, string branchOrCommit, string localPath, Dictionary<string, string>? environmentVariables = null, CancellationToken cancellationToken = default)
    {
        var target = CommandArguments.ValidateReference(branchOrCommit);
        if (string.IsNullOrWhiteSpace(repoUrl) || repoUrl.StartsWith('-')) return Failure("Enter a valid repository URL.");
        if (WorkspacePaths.HasRepository(localPath))
        {
            var existingOrigin = await ValidateOrigin(localPath, repoUrl, cancellationToken);
            if (!existingOrigin.Success) return existingOrigin;
        }
        // Azure DevOps and other Git hosts advertise branch/tag tips without transferring objects.
        string[] remoteArgs = ["ls-remote", "--", repoUrl, target == "HEAD" ? "HEAD" : "refs/heads/" + target, "refs/tags/" + target, "refs/tags/" + target + "^{}"];
        var advertised = await runner.RunAsync("git", string.Join(" ", remoteArgs.Select(CommandArguments.Quote)), cancellationToken: cancellationToken, environmentVariables: environmentVariables);
        if (advertised.ExitCode != 0) return Failure("Source verification failed: " + Error(advertised));
        var refs = advertised.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', 2)).Where(parts => parts.Length == 2 && System.Text.RegularExpressions.Regex.IsMatch(parts[0], "^[0-9a-fA-F]{40,64}$"))
            .GroupBy(parts => parts[1]).ToDictionary(group => group.Key, group => group.First()[0], StringComparer.Ordinal);
        string branchRef = target == "HEAD" ? "HEAD" : "refs/heads/" + target;
        if (refs.TryGetValue(branchRef, out var branchSha))
            return new() { Success = true, SourceType = "Branch", ResolvedBranch = target, CommitSha = branchSha, Message = "Remote source verified; author and date load after checkout." };
        if (refs.TryGetValue("refs/tags/" + target, out var tagSha))
            return new() { Success = true, SourceType = "Commit / Tag", ResolvedBranch = "Detached commit", CommitSha = refs.GetValueOrDefault("refs/tags/" + target + "^{}", tagSha), Message = "Remote tag verified; author and date load after checkout." };
        if (!System.Text.RegularExpressions.Regex.IsMatch(target, "^[0-9a-fA-F]{4,64}$"))
            return Failure($"Remote branch or tag '{target}' was not found. Enter an exact branch/tag name or commit SHA.");
        // Commit IDs require objects. Use a repository with no checkout and try just the requested commit.
        bool shallowValidation = !WorkspacePaths.HasRepository(localPath) || await IsShallowAsync(localPath, cancellationToken);
        if (!WorkspacePaths.HasRepository(localPath))
        {
            if (Directory.Exists(localPath) && Directory.EnumerateFileSystemEntries(localPath).Any()) return Failure("Source validation requires an empty cache folder.");
            runner.EnsureDirectoryExists(localPath);
            if ((await Git(localPath, cancellationToken, null, "init")).ExitCode != 0 ||
                (await Git(localPath, cancellationToken, null, "remote", "add", "origin", repoUrl)).ExitCode != 0)
                return Failure("Could not initialize the source validation cache.");
        }
        var origin = await ValidateOrigin(localPath, repoUrl, cancellationToken);
        if (!origin.Success) return origin;
        async Task<ProcessResult> FetchValidation(params string[] args) => await runner.RunAsync("git", string.Join(" ", args.Select(CommandArguments.Quote)), localPath, cancellationToken: cancellationToken, environmentVariables: environmentVariables);
        if (target.Length is 40 or 64)
        {
            var direct = await FetchValidation(shallowValidation ? ["fetch", "--depth", "1", "--no-tags", "origin", target] : ["fetch", "--no-tags", "origin", target]);
            if (direct.ExitCode == 0) return await ResolveReference(localPath, target, cancellationToken);
        }
        // Short IDs and hosts that reject fetching unadvertised IDs need advertised history.
        var fetch = await FetchValidation(shallowValidation ? ["fetch", "--depth", "1", "--tags", "origin", "+refs/heads/*:refs/remotes/origin/*"] : ["fetch", "--tags", "origin"]);
        if (fetch.ExitCode != 0) return Failure("Fetch failed: " + Error(fetch));
        var resolved = await ResolveReference(localPath, target, cancellationToken);
        if (resolved.Success || !await IsShallowAsync(localPath, cancellationToken)) return resolved;
        var history = await FetchValidation("fetch", "--unshallow", "--tags", "origin");
        return history.ExitCode == 0 ? await ResolveReference(localPath, target, cancellationToken) : Failure("History download failed: " + Error(history));
    }
    private async Task<bool> IsShallowAsync(string path, CancellationToken token)
        => (await Git(path, token, null, "rev-parse", "--is-shallow-repository")).StandardOutput.Trim() == "true";

    private async Task<ProcessResult> FetchAsync(string path, string target, CancellationToken token, Action<string>? output)
    {
        var advertised = await Git(path, token, null, "ls-remote", "--", "origin", "refs/heads/" + target, "refs/tags/" + target);
        if (advertised.ExitCode != 0) return advertised;
        var refs = advertised.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', 2)).Where(parts => parts.Length == 2).Select(parts => parts[1]).ToHashSet(StringComparer.Ordinal);
        var args = new List<string> { "fetch", "--no-tags" };
        if (await IsShallowAsync(path, token)) args.AddRange(["--depth", "1"]);
        args.Add("origin");
        if (refs.Contains("refs/heads/" + target)) args.Add($"+refs/heads/{target}:refs/remotes/origin/{target}");
        else if (refs.Contains("refs/tags/" + target)) args.Add($"refs/tags/{target}:refs/tags/{target}");
        else if (System.Text.RegularExpressions.Regex.IsMatch(target, "^[0-9a-fA-F]{40,64}$")) args.Add(target);
        else if (target != "HEAD")
        {
            // Short IDs cannot be fetched directly. Populate advertised tips, then resolve/history fallback.
            args.Add("+refs/heads/*:refs/remotes/origin/*");
            args.Insert(1, "--tags");
            args.Remove("--no-tags");
        }
        var fetched = await Git(path, token, output, args.ToArray());
        if (fetched.ExitCode == 0 && refs.Contains("refs/heads/" + target))
        {
            // A single-branch clone initially tracks only its cloned branch. Register a newly selected branch.
            var configured = await Git(path, token, null, "config", "--get-all", "remote.origin.fetch");
            string refspec = $"+refs/heads/{target}:refs/remotes/origin/{target}";
            var mappings = configured.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (!mappings.Contains("+refs/heads/*:refs/remotes/origin/*") && !mappings.Contains(refspec))
            {
                var added = await Git(path, token, null, "config", "--add", "remote.origin.fetch", refspec);
                if (added.ExitCode != 0) return added;
            }
        }
        return fetched;
    }

    private async Task<GitValidationResult> ResolveWithHistoryAsync(string path, string target, CancellationToken token)
    {
        var result = await ResolveReference(path, target, token);
        if (result.Success || !await IsShallowAsync(path, token)) return result;
        var history = await Git(path, token, null, "fetch", "--unshallow", "--tags", "origin", "+refs/heads/*:refs/remotes/origin/*");
        return history.ExitCode == 0 ? await ResolveReference(path, target, token) : Failure("History download failed: " + Error(history));
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
        var accessible = await Git(localPath, cancellationToken, null, "rev-parse", "--is-inside-work-tree");
        if (accessible.ExitCode != 0) return WorkspaceAccessFailure(accessible);
        var fetch = await FetchAsync(localPath, target, cancellationToken, onProgress);
        if (fetch.ExitCode != 0) return Failure("Fetch failed: " + Error(fetch));
        var resolved = await ResolveWithHistoryAsync(localPath, target, cancellationToken);
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
                // Fast-forward checks require ancestry between the local and remote tips.
                var currentHead = await ResolveReference(localPath, "HEAD", cancellationToken);
                if (currentHead.CommitSha != resolved.CommitSha && await IsShallowAsync(localPath, cancellationToken))
                {
                    onProgress?.Invoke("Downloading history needed to verify a safe fast-forward update.");
                    var history = await Git(localPath, cancellationToken, onProgress, "fetch", "--unshallow", "origin", $"+refs/heads/{target}:refs/remotes/origin/{target}");
                    if (history.ExitCode != 0) return Failure("Could not download history for a safe branch update: " + Error(history));
                }
                // The selected remote tip was just fetched; avoid downloading it a second time.
                var pull = await Git(localPath, cancellationToken, onProgress, "merge", "--ff-only", "refs/remotes/origin/" + target);
                if (pull.ExitCode != 0) return Failure("Fast-forward failed; the workspace was retained: " + Error(pull), HasConflicts(pull));
            }
        }
        var head = await ResolveReference(localPath, "HEAD", cancellationToken);
        return head.Success ? new() { Success = true, SourceType = resolved.SourceType, ResolvedBranch = resolved.ResolvedBranch, CommitSha = head.CommitSha, Author = head.Author, Date = head.Date, Message = head.Message } : Failure("Could not verify HEAD after checkout.");
    }
    public async Task<bool> SetupRepositoryAsync(string repoUrl, string branch, string localPath, bool forcePullLatest, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (!await CloneRepositoryAsync(repoUrl, localPath, onProgress, cancellationToken, branch)) return false;
        if (!(await CheckoutAndValidateLocalAsync(branch, localPath, onProgress: onProgress, cancellationToken: cancellationToken)).Success) return false;
        return await UpdateSubmodulesAsync(localPath, onProgress, cancellationToken);
    }
    public async Task<bool> UpdateSubmodulesAsync(string localPath, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        await PrepareSubmodulePointersAsync(localPath, cancellationToken);
        onProgress?.Invoke("Updating submodules at their recorded commits with depth 1, using up to four parallel downloads.");
        var sync = await Git(localPath, cancellationToken, onProgress, "submodule", "sync", "--recursive");
        if (sync.ExitCode != 0) return false;
        // Older Git for Windows cannot reliably rewrite a hidden .git pointer file.
        // This setting applies only to this command and its children, not the user's Git configuration.
        var update = await Git(localPath, cancellationToken, onProgress, "-c", "core.hideDotFiles=false", "submodule", "update", "--init", "--recursive", "--depth", "1", "--jobs", "4", "--progress");
        if (update.ExitCode != 0 && (Error(update).Contains("Permission denied", StringComparison.OrdinalIgnoreCase) || Error(update).Contains("BUG: submodule", StringComparison.OrdinalIgnoreCase)))
        {
            onProgress?.Invoke("Retrying retained submodule data serially after a Windows file-access failure.");
            await PrepareSubmodulePointersAsync(localPath, cancellationToken);
            update = await Git(localPath, cancellationToken, onProgress, "-c", "core.hideDotFiles=false", "submodule", "update", "--init", "--recursive", "--depth", "1", "--jobs", "1", "--progress");
        }
        if (update.ExitCode != 0) throw new IOException("Submodule update failed. Downloaded files have been retained. " + Error(update));
        return true;
    }

    private async Task PrepareSubmodulePointersAsync(string localPath, CancellationToken token)
    {
        string root = Path.GetFullPath(localPath);
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(root);
        while (pending.TryPop(out var parent))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(parent)) continue;
            string modules = Path.Combine(parent, ".gitmodules");
            if (!File.Exists(modules)) continue;
            if ((File.GetAttributes(modules) & FileAttributes.ReparsePoint) != 0) throw new IOException("The submodule configuration must not be a symbolic link.");
            var paths = await Git(parent, token, null, "config", "--file", modules, "--get-regexp", @"^submodule\..*\.path$");
            if (paths.ExitCode == 1) continue; // No submodules declared.
            if (paths.ExitCode != 0) throw new IOException("Could not read submodule paths: " + Error(paths));
            foreach (string line in paths.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = line.IndexOfAny([' ', '\t']);
                if (separator < 0) continue;
                string child = Path.GetFullPath(Path.Combine(parent, line[(separator + 1)..].TrimStart()));
                string relative = Path.GetRelativePath(root, child);
                if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative))
                    throw new IOException("A submodule path is outside the selected workspace.");
                for (string? directory = child; directory != null && !directory.Equals(root, StringComparison.OrdinalIgnoreCase); directory = Path.GetDirectoryName(directory))
                    if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Submodule permission recovery cannot follow a directory link.");
                if (!Directory.Exists(child)) continue;
                string pointer = Path.Combine(child, ".git");
                if (File.Exists(pointer))
                {
                    var attributes = File.GetAttributes(pointer);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The submodule Git pointer must not be a symbolic link.");
                    if ((attributes & FileAttributes.Hidden) != 0 && File.ReadLines(pointer).FirstOrDefault()?.StartsWith("gitdir: ", StringComparison.Ordinal) == true)
                    {
                        File.SetAttributes(pointer, attributes & ~FileAttributes.Hidden);
                        log.LogInfo("Git", "Removed the hidden attribute from a registered submodule pointer for Windows Git compatibility: " + pointer);
                    }
                }
                pending.Push(child);
            }
        }
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
            result.State = IsOwnershipError(directory) ? RepositoryState.UnsafeRepository : RepositoryState.NotARepository;
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
