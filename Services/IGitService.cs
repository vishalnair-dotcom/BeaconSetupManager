using System;
using System.Threading;
using System.Threading.Tasks;
using BeaconSetupManager.Models;

namespace BeaconSetupManager.Services
{
    public interface IGitService
    {
        Task<bool> SetupRepositoryAsync(
            string repoUrl,
            string branch,
            string localPath,
            bool forcePullLatest,
            Action<string>? onProgress = null,
            CancellationToken cancellationToken = default);

        Task<GitValidationResult> ValidateBranchOrCommitAsync(
            string repoUrl,
            string branchOrCommit,
            string localPath,
            System.Collections.Generic.Dictionary<string, string>? environmentVariables = null,
            CancellationToken cancellationToken = default);

        Task<GitValidationResult> VerifyRepositoryAccessAsync(
            string repoUrl,
            System.Collections.Generic.Dictionary<string, string>? environmentVariables = null,
            CancellationToken cancellationToken = default);

        Task<bool> IsWorkingTreeCleanAsync(
            string localPath,
            string targetBranchOrCommit = "",
            CancellationToken cancellationToken = default);

        Task<bool> CloneRepositoryAsync(
            string repoUrl,
            string localPath,
            Action<string>? onProgress = null,
            CancellationToken cancellationToken = default);

        Task<GitValidationResult> CheckoutAndValidateLocalAsync(
            string branchOrCommit,
            string localPath,
            GitCheckoutMode mode = GitCheckoutMode.Normal,
            Action<string>? onProgress = null,
            CancellationToken cancellationToken = default);

        Task<bool> UpdateSubmodulesAsync(
            string localPath,
            Action<string>? onProgress = null,
            CancellationToken cancellationToken = default);

        Task<GitValidationResult> ValidateExistingRepositoryAsync(
            string repoUrl,
            string localPath,
            CancellationToken cancellationToken = default);

        void CleanUpDirectory(string path);

        Task<RepositoryAnalysisResult> AnalyzeRepositoryAsync(
            string localPath,
            string repoUrl,
            string targetBranch,
            CancellationToken cancellationToken = default);

        Task<bool> AbortMergeOrRebaseAsync(
            string localPath,
            CancellationToken cancellationToken = default);

        Task<bool> StashChangesAsync(
            string localPath,
            CancellationToken cancellationToken = default);

        Task<bool> DiscardChangesAsync(
            string localPath,
            CancellationToken cancellationToken = default);

        Task<bool> RepairRepositoryAsync(
            string localPath,
            Action<string>? onProgress = null,
            CancellationToken cancellationToken = default);

        Task<bool> AttemptSafeAutoRecoveryAsync(
            string localPath,
            CancellationToken cancellationToken = default);

        SetupMetadata? ReadSetupMetadata(string localPath);

        void WriteSetupMetadata(string localPath, SetupMetadata metadata);
    }
}
