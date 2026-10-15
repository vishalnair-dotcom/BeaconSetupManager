using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeaconSetupManager.Models;

namespace BeaconSetupManager.Services
{
    public interface IPrerequisiteService
    {
        // Publishes a Running item when its check starts, followed by its final result.
        Task<List<PrerequisiteItem>> ValidateAllAsync(Action<PrerequisiteItem>? onItemChecked = null, Action<string>? onProgress = null, System.Threading.CancellationToken cancellationToken = default);
        Task<(bool Success, string ErrorMessage)> ValidateGitAccessAsync(string repoUrl);
        Task<bool> ApproveGitCredentialsAsync(string repoUrl, string username, string password);
    }
}
