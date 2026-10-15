using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using BeaconSetupManager.Models;
using BeaconSetupManager.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Npgsql;

namespace BeaconSetupManager.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IPrerequisiteService _prerequisitesService;
    private readonly IGitService _git;
    private readonly IDatabaseService _database;
    private readonly IConfigService _config;
    private readonly ILogService _log;
    private readonly IAppLauncherService _launcher;
    private readonly IProcessRunner _runner;
    private readonly ILaunchSettingsService _launchSettings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts, _validationCts;
    private Operation? _operation;
    private bool _loading, _disposed;
    private int _inputVersion;
    private string _verifiedUrl = "";
    private bool _workspaceNeedsOwnershipRepair;
    private DispatcherTimer? _monitor;
    private int? _backendPid, _frontendPid;
    private DateTime _started;
    private record Operation(string Action, string Path, string Url, string Source, LaunchSettings Settings);
    public event Action? OnLogCleared;

    [ObservableProperty] private int _currentStep = 1;
    [ObservableProperty] private string _selectedAction = "Setup";
    [ObservableProperty] private string _selectedTheme = "Dark";
    [ObservableProperty] private string _repoUrl = "https://quantumphinance.visualstudio.com/_git/InsightCorp";
    [ObservableProperty] private string _branch = "BeaconWeb";
    [ObservableProperty] private string _localPath = "";
    [ObservableProperty] private string _selectedBuildConfig = "Debug";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isSetupEnabled = true;
    [ObservableProperty] private bool _isAdminMode;
    [ObservableProperty] private string _busyMessage = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isExecutionFinished, _executionSuccess, _canRetryExecution;
    [ObservableProperty] private string _executionSummaryText = "";
    [ObservableProperty] private bool _isRunApplicationPromptVisible, _isDbSetupFormVisible, _isBuildPromptVisible, _isBuildResultsVisible;
    [ObservableProperty] private string _repoValidationStatus = "NotVerified", _repoValidationMessage = "Repository has not yet been verified.";
    [ObservableProperty] private string _sourceValidationStatus = "NotVerified", _sourceValidationMessage = "Source has not yet been validated.";
    [ObservableProperty] private WorkspaceValidationStatus _workspaceValidationStatus;
    [ObservableProperty] private string _workspaceValidationMessage = "", _lastSuccessfulVerificationText = "";
    [ObservableProperty] private string? _gitResolvedBranch, _gitCommitSha, _gitCommitAuthor, _gitCommitDate, _gitCommitMessage;
    [ObservableProperty] private bool _isBranchTypeVisible, _isCommitTypeVisible;
    [ObservableProperty] private string _buildStatusText = "", _buildConfigText = "", _buildDurationText = "", _buildOutputText = "", _buildErrorsText = "", _activeResultTab = "Output";
    [ObservableProperty] private int _buildWarningsCount, _buildErrorsCount;
    [ObservableProperty] private string _runtimeBackendStatus = "Stopped", _runtimeFrontendStatus = "Stopped", _runtimeCurrentOperation = "Stopped", _runtimeElapsedTime = "00:00";
    [ObservableProperty] private bool _isRunningApplicationActive;
    [ObservableProperty] private string _dbHost = "localhost", _dbAdminUser = "postgres", _dbAdminPassword = "", _dbName = "Beacon", _dbUser = "BeaconUser", _dbPassword = "";
    [ObservableProperty] private string _dbPort = "5432";
    [ObservableProperty] private bool _updateExistingRolePassword, _updateExistingDatabaseOwner;
    [ObservableProperty] private string _statusSummary = "Scan dependencies to check installed tools.";
    public ObservableCollection<PrerequisiteItem> Prerequisites { get; } = new();
    public ObservableCollection<SetupStepItem> SetupSteps { get; } = new();
    public bool IsStandardConfigAction => SelectedAction is "Setup" or "UpdateBranch" or "Repair";
    public bool IsSimplePathConfigAction => !IsStandardConfigAction;
    public bool IsGitFieldVisible => IsStandardConfigAction;
    public bool IsWorkspaceValidated => WorkspaceValidationStatus == WorkspaceValidationStatus.Success || _workspaceNeedsOwnershipRepair && WorkspaceValidationStatus == WorkspaceValidationStatus.Warning;
    public bool HasWorkspaceValidationMessage => !string.IsNullOrEmpty(WorkspaceValidationMessage);
    public bool HasLastSuccessfulVerificationText => !string.IsNullOrEmpty(LastSuccessfulVerificationText);
    public bool IsSourceMetadataVisible => SourceValidationStatus == "Success";
    public bool HasPrerequisiteWarnings => Prerequisites.Any(item => item.Status is ValidationStatus.Missing or ValidationStatus.Failed or ValidationStatus.VersionMismatch);
    public bool CanExecuteAction => !IsBusy && IsWorkspaceValidated && (!IsStandardConfigAction ||
        (RepoValidationStatus == "Success" && (SelectedAction is "Setup" or "Repair" || SourceValidationStatus == "Success" || _workspaceNeedsOwnershipRepair)));
    public string ActionButtonText => SelectedAction switch { "Setup" => "Setup", "UpdateBranch" => "Update", "DatabaseSetup" => "Configure", "RunApplication" => "Run", _ => SelectedAction };
    public bool CanContinueToDbSetup => !IsBusy && BuildStatusText == "✓ Succeeded" && SelectedAction is "Setup" or "Repair";

    public MainViewModel(IPrerequisiteService prerequisiteService, IGitService gitService, IDatabaseService databaseService,
        IConfigService configService, ILogService logService, IAdminService adminService,
        IAppLauncherService appLauncherService, IProcessRunner processRunner, ILaunchSettingsService launchSettingsService)
    {
        _prerequisitesService = prerequisiteService; _git = gitService; _database = databaseService; _config = configService;
        _log = logService; _launcher = appLauncherService; _runner = processRunner; _launchSettings = launchSettingsService;
        IsAdminMode = adminService.IsRunningAsAdmin();
        _loading = true;
        var saved = SettingsStore.Read(GitConfigPath, () => new GitConfigSettings());
        if (!string.IsNullOrWhiteSpace(saved.RepoUrl)) RepoUrl = saved.RepoUrl;
        if (!string.IsNullOrWhiteSpace(saved.Branch)) Branch = saved.Branch;
        LocalPath = saved.LocalPath;
        if (DateTime.TryParse(saved.LastValidationTimestampUtc, out var timestamp)) LastSuccessfulVerificationText = $"Last successful verification: {timestamp.ToLocalTime():g}";
        _loading = false;
        foreach (var item in PrerequisiteItem.CreatePendingItems()) Prerequisites.Add(item);
        ResetSetupSteps();
    }

    private void EligibilityChanged()
    {
        OnPropertyChanged(nameof(CanExecuteAction));
        OnPropertyChanged(nameof(CanContinueToDbSetup));
        ExecuteActionCommand.NotifyCanExecuteChanged();
        ContinueToDbSetupCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsBusyChanged(bool value) => EligibilityChanged();
    partial void OnRepoValidationStatusChanged(string value) => EligibilityChanged();
    partial void OnSourceValidationStatusChanged(string value) { OnPropertyChanged(nameof(IsSourceMetadataVisible)); EligibilityChanged(); }
    partial void OnWorkspaceValidationStatusChanged(WorkspaceValidationStatus value) { OnPropertyChanged(nameof(IsWorkspaceValidated)); EligibilityChanged(); }
    partial void OnWorkspaceValidationMessageChanged(string value) => OnPropertyChanged(nameof(HasWorkspaceValidationMessage));
    partial void OnLastSuccessfulVerificationTextChanged(string value) => OnPropertyChanged(nameof(HasLastSuccessfulVerificationText));
    partial void OnBuildStatusTextChanged(string value) => EligibilityChanged();
    partial void OnSelectedThemeChanged(string value) { if (Application.Current is App app) app.ChangeTheme(value); }
    partial void OnRepoUrlChanged(string? oldValue, string newValue) { if (RestoreBusyInput(() => RepoUrl = oldValue ?? "")) return; _verifiedUrl = ""; RepoValidationStatus = "NotVerified"; InvalidateInputs(); }
    partial void OnBranchChanged(string? oldValue, string newValue) { if (!RestoreBusyInput(() => Branch = oldValue ?? "")) InvalidateInputs(); }
    partial void OnLocalPathChanged(string? oldValue, string newValue) { if (!RestoreBusyInput(() => LocalPath = oldValue ?? "")) InvalidateInputs(); }
    partial void OnSelectedBuildConfigChanged(string? oldValue, string newValue) { if (!RestoreBusyInput(() => SelectedBuildConfig = oldValue ?? "Debug")) InvalidateInputs(); }
    partial void OnSelectedActionChanged(string? oldValue, string newValue)
    {
        if (RestoreBusyInput(() => SelectedAction = oldValue ?? "Setup")) return;
        foreach (var name in new[] { nameof(IsStandardConfigAction), nameof(IsSimplePathConfigAction), nameof(IsGitFieldVisible), nameof(ActionButtonText) }) OnPropertyChanged(name);
        InvalidateInputs();
    }
    private bool RestoreBusyInput(Action restore)
    {
        if (_loading) return true;
        if (!IsBusy) return false;
        _loading = true;
        try { restore(); } finally { _loading = false; }
        return true;
    }
    private void InvalidateInputs()
    {
        if (_loading) return;
        Interlocked.Increment(ref _inputVersion);
        _workspaceNeedsOwnershipRepair = false;
        _validationCts?.Cancel();
        SourceValidationStatus = "NotVerified"; SourceValidationMessage = "Source has not yet been validated.";
        GitCommitSha = null; GitCommitAuthor = null; GitCommitDate = null; GitCommitMessage = null; GitResolvedBranch = null;
        WorkspaceValidationStatus = WorkspaceValidationStatus.NotValidated;
        WorkspaceValidationMessage = "";
        ResetSetupSteps();
        ClearResults();
        if (!IsBusy) _ = ValidateWorkspaceAsync();
    }
    private void ClearResults()
    {
        IsExecutionFinished = false; ExecutionSuccess = false; CanRetryExecution = false;
        IsBuildResultsVisible = false; IsBuildPromptVisible = false; IsRunApplicationPromptVisible = false; IsDbSetupFormVisible = false;
        BuildStatusText = ""; BuildOutputText = ""; BuildErrorsText = "";
    }

    [RelayCommand] public void ToggleTheme() => SelectedTheme = SelectedTheme == "Dark" ? "Light" : "Dark";
    [RelayCommand] public void SelectAction(string action) { if (!IsBusy && action is "Setup" or "UpdateBranch" or "DatabaseSetup" or "RunApplication") SelectedAction = action; }
    [RelayCommand] public async Task GoNextAsync()
    {
        if (IsBusy || CurrentStep >= 3) return;
        CurrentStep++;
        if (CurrentStep == 3)
        {
            await ValidateWorkspaceAsync();
            if (IsStandardConfigAction && _verifiedUrl != RepoUrl) await VerifyRepositoryAsync();
        }
    }
    [RelayCommand] public void GoBack() { if (!IsBusy && CurrentStep > 1) { CurrentStep--; ClearResults(); } }

    [RelayCommand] public async Task ValidateWorkspaceAsync()
    {
        if (_disposed || IsBusy) return;
        _validationCts?.Cancel(); _validationCts?.Dispose();
        var cts = new CancellationTokenSource(); _validationCts = cts;
        int version = _inputVersion;
        string selectedPath = LocalPath, url = RepoUrl, action = SelectedAction;
        _workspaceNeedsOwnershipRepair = false;
        WorkspaceValidationStatus = WorkspaceValidationStatus.Validating;
        WorkspaceValidationMessage = "Checking the workspace...";
        bool Current() => !cts.IsCancellationRequested && version == _inputVersion && !_disposed;
        try
        {
            if (!Path.IsPathFullyQualified(selectedPath)) throw new IOException("Select a fully qualified workspace path.");
            string path = WorkspacePaths.Normalize(selectedPath);
            if (!Directory.Exists(path)) throw new IOException("Select an existing folder. Create an empty folder first for Setup.");
            string probe = Path.Combine(path, ".beacon-write-" + Guid.NewGuid().ToString("N"));
            try { await File.WriteAllTextAsync(probe, "", cts.Token); }
            finally { if (File.Exists(probe)) File.Delete(probe); }
            bool repository = WorkspacePaths.HasRepository(path);
            if (!repository)
            {
                if (action != "Setup" || Directory.EnumerateFileSystemEntries(path).Any()) throw new IOException("This action requires an existing Beacon repository. Setup can use an empty folder.");
            }
            else
            {
                var workTree = await _runner.RunAsync("git", "rev-parse --is-inside-work-tree", path, cancellationToken: cts.Token);
                if (workTree.ExitCode != 0 && workTree.StandardError.Contains("ownership", StringComparison.OrdinalIgnoreCase) && action is "Setup" or "Repair" or "UpdateBranch")
                {
                    if (!Current()) return;
                    _workspaceNeedsOwnershipRepair = true;
                    WorkspaceValidationStatus = WorkspaceValidationStatus.Warning;
                    WorkspaceValidationMessage = "Git ownership must be repaired. Continue to choose an explicit recovery action.";
                    return;
                }
                if (workTree.ExitCode != 0) throw new IOException("Git cannot access this workspace: " + workTree.StandardError);
                if (action is "Setup" or "UpdateBranch" or "Repair")
                {
                    var origin = await _runner.RunAsync("git", "config --get remote.origin.url", path, cancellationToken: cts.Token);
                    if (origin.ExitCode != 0 || !WorkspacePaths.SameRemote(origin.StandardOutput.Trim(), url)) throw new IOException("The workspace origin does not match the selected repository.");
                }
                else if (!File.Exists(Path.Combine(path, "run.ps1")) || !Directory.EnumerateFiles(path, "*.sln").Any()) throw new IOException("Select the Beacon repository root containing run.ps1 and a solution.");
            }
            if (!Current()) return;
            WorkspaceValidationStatus = WorkspaceValidationStatus.Success;
            WorkspaceValidationMessage = repository ? "✓ Existing repository verified." : "✓ Empty workspace ready.";
            SaveGitSettings();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (Current()) { WorkspaceValidationStatus = WorkspaceValidationStatus.Failed; WorkspaceValidationMessage = ex.Message; }
        }
    }

    [RelayCommand] public async Task VerifyRepositoryAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        int version = _inputVersion; string url = RepoUrl;
        IsBusy = true; BusyMessage = "Verifying repository access...";
        _cts = new CancellationTokenSource();
        try
        {
            var result = await _git.VerifyRepositoryAccessAsync(url, cancellationToken: _cts.Token);
            if (version != _inputVersion) return;
            RepoValidationStatus = result.Success ? "Success" : "Failed";
            RepoValidationMessage = result.Success ? "✓ Connected" : result.ErrorMessage ?? "Verification failed.";
            if (result.Success) { _verifiedUrl = url; LastSuccessfulVerificationText = $"Last successful verification: {DateTime.Now:g}"; SaveGitSettings(DateTime.UtcNow.ToString("O")); }
        }
        catch (OperationCanceledException) { if (version == _inputVersion) { RepoValidationStatus = "NotVerified"; RepoValidationMessage = "Verification cancelled."; } }
        catch (Exception ex) { if (version == _inputVersion) { RepoValidationStatus = "Failed"; RepoValidationMessage = ex.Message; } }
        finally { EndOperation(); }
    }

    [RelayCommand] public async Task ValidateSourceAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        int version = _inputVersion; string url = RepoUrl, source = Branch, path = LocalPath;
        IsBusy = true; BusyMessage = "Validating source reference..."; SourceValidationStatus = "Validating";
        _cts = new CancellationTokenSource();
        try
        {
            if (!WorkspacePaths.HasRepository(path) || _workspaceNeedsOwnershipRepair) path = Path.Combine(SettingsStore.DirectoryPath, "validation", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(WorkspacePaths.NormalizeRemote(url)))));
            using var workspaceLock = WorkspaceOperationLock.Acquire(path);
            var result = await _git.ValidateBranchOrCommitAsync(url, source, path, cancellationToken: _cts.Token);
            if (version != _inputVersion) return;
            SourceValidationStatus = result.Success ? "Success" : "Failed";
            SourceValidationMessage = result.Success ? "✓ Source verified." : result.ErrorMessage ?? "Source validation failed.";
            if (result.Success) UpdateGitDetails(result);
        }
        catch (OperationCanceledException) { if (version == _inputVersion) { SourceValidationStatus = "NotVerified"; SourceValidationMessage = "Source validation cancelled."; } }
        catch (Exception ex) { if (version == _inputVersion) { SourceValidationStatus = "Failed"; SourceValidationMessage = ex.Message; } }
        finally { EndOperation(); }
    }
    private void UpdateGitDetails(GitValidationResult result)
    {
        GitResolvedBranch = result.ResolvedBranch; GitCommitSha = result.CommitSha; GitCommitAuthor = result.Author; GitCommitDate = result.Date; GitCommitMessage = result.Message;
        IsBranchTypeVisible = false; IsCommitTypeVisible = true;
    }

    [RelayCommand(CanExecute = nameof(CanExecuteAction))] public async Task ExecuteActionAsync()
    {
        // Revalidate for this action at execution time; cached UI flags never bypass these checks.
        await ValidateWorkspaceAsync();
        if (!CanExecuteAction || !await _gate.WaitAsync(0)) return;
        var op = new Operation(SelectedAction, WorkspacePaths.Normalize(LocalPath), RepoUrl, Branch, _launchSettings.Load(LocalPath));
        await RunOperationAsync(op);
    }
    [RelayCommand] public Task RetryExecutionAsync() => IsDbSetupFormVisible ? RunDbSetupAsync() : ExecuteActionAsync();

    private async Task RunOperationAsync(Operation op)
    {
        _operation = op;
        ClearResults(); OnLogCleared?.Invoke(); ResetSetupSteps();
        CurrentStep = 4; IsBusy = true; ProgressValue = 0; ExecutionSummaryText = $"Running {op.Action}...";
        _cts = new CancellationTokenSource();
        try
        {
            using var workspaceLock = WorkspaceOperationLock.Acquire(op.Path);
            switch (op.Action)
            {
                case "Setup": case "Repair": await SetupAsync(op); break;
                case "UpdateBranch":
                    if (!await RecoverRepositoryAsync(op)) { FinishCancelled(); break; }
                    await CheckoutAsync(op);
                    await StepAsync(SetupStepType.SubmodulesUpdate, () => _git.UpdateSubmodulesAsync(op.Path, LogGit, _cts.Token));
                    SetStepStatus(SetupStepType.RepositoryVerify, StepStatus.Completed); SetStepStatus(SetupStepType.WorkspaceValidate, StepStatus.Completed); SetStepStatus(SetupStepType.RepositoryClone, StepStatus.Completed);
                    WriteCheckpoint(op);
                    ExecutionSuccess = true; IsExecutionFinished = true; IsBuildPromptVisible = true;
                    ExecutionSummaryText = "Branch and submodules updated. Build before running the new revision."; break;
                case "Build": await RunApplicationAsync(op, true); break;
                case "RunApplication": await RunApplicationAsync(op, false); break;
                case "DatabaseSetup": ShowDatabaseForm(op.Path); ExecutionSummaryText = "Configure PostgreSQL and the workspace connection."; break;
                default: throw new ArgumentException("Unknown setup action.");
            }
        }
        catch (OperationCanceledException) { FinishCancelled(); }
        catch (Exception ex) { FinishFailure(ex); }
        finally { StopMonitoring(); EndOperation(); }
    }
    private void FinishCancelled()
    {
        ExecutionSuccess = false; IsExecutionFinished = true; CanRetryExecution = true; ExecutionSummaryText = "Operation cancelled. Completed changes have been retained.";
        foreach (var step in SetupSteps.Where(step => step.Status == StepStatus.Running)) step.Status = StepStatus.Pending;
    }
    private void FinishFailure(Exception ex)
    {
        ExecutionSuccess = false; IsExecutionFinished = true; CanRetryExecution = true; ExecutionSummaryText = ex.Message;
        _log.LogError("Operation", ex.Message, ex);
        if (!_disposed) MessageBox.Show(ex.Message + "\n\nCompleted changes have been retained. Review the log before retrying.", "Operation failed", MessageBoxButton.OK, MessageBoxImage.Error);
    }
    private void EndOperation()
    {
        _cts?.Dispose(); _cts = null; _operation = null; IsBusy = false; _gate.Release();
    }

    private async Task SetupAsync(Operation op)
    {
        var settings = await PromptLaunchSettingsAsync(op, op.Action);
        if (settings == null) { FinishCancelled(); return; }
        op = op with { Settings = settings }; _operation = op;
        var checkpoint = await ReadValidCheckpointAsync(op);
        if (!await RecoverRepositoryAsync(op)) { FinishCancelled(); return; }
        await StepAsync(SetupStepType.RepositoryVerify, async () => (await _git.VerifyRepositoryAccessAsync(op.Url, cancellationToken: _cts!.Token)).Success);
        await StepAsync(SetupStepType.WorkspaceValidate, () => Task.FromResult(Directory.Exists(op.Path)));
        await StepAsync(SetupStepType.RepositoryClone, () => _git.CloneRepositoryAsync(op.Url, op.Path, LogGit, _cts!.Token));
        await CheckoutAsync(op);
        await StepAsync(SetupStepType.SubmodulesUpdate, () => _git.UpdateSubmodulesAsync(op.Path, LogGit, _cts!.Token));
        if (checkpoint?.CompletedSteps.Contains(SetupStepType.SolutionBuild) == true && checkpoint.CommitSha == GitCommitSha && checkpoint.BuildArtifactsHash == BuildArtifactsHash(op) && op.Action == "Setup")
        {
            SetStepStatus(SetupStepType.PackagesRestore, StepStatus.Completed); SetStepStatus(SetupStepType.SolutionBuild, StepStatus.Completed);
            BuildStatusText = "✓ Succeeded"; BuildConfigText = settings.Configuration; BuildOutputText = "Verified build checkpoint for the current revision."; IsBuildResultsVisible = true;
        }
        else
        {
            await BuildAsync(op);
        }
        WriteCheckpoint(op);
        ShowDatabaseForm(op.Path);
        ExecutionSummaryText = "Build succeeded. Continue to database configuration and connection verification.";
    }
    private async Task CheckoutAsync(Operation op)
    {
        var mode = GitCheckoutMode.Normal;
        await StepAsync(SetupStepType.BranchCheckout, async () =>
        {
            while (true)
            {
                _cts!.Token.ThrowIfCancellationRequested();
                var result = await _git.CheckoutAndValidateLocalAsync(op.Source, op.Path, mode, LogGit, _cts.Token);
                if (result.Success) { UpdateGitDetails(result); return true; }
                if (!result.HasCheckoutConflicts) throw new IOException(result.ErrorMessage);
                var dialog = new GitConflictResolutionDialog(result.ErrorMessage ?? "Git checkout conflict.") { Owner = Application.Current.MainWindow };
                if (dialog.ShowDialog() != true) throw new OperationCanceledException(_cts.Token);
                mode = dialog.Result switch { GitConflictResolution.Merge => GitCheckoutMode.Merge, GitConflictResolution.Force => GitCheckoutMode.Force, _ => GitCheckoutMode.Normal };
                if (mode == GitCheckoutMode.Force && MessageBox.Show("Discard uncommitted changes and untracked files in this workspace?", "Confirm force checkout", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) mode = GitCheckoutMode.Normal;
            }
        });
    }
    private async Task<bool> RecoverRepositoryAsync(Operation op)
    {
        if (!WorkspacePaths.HasRepository(op.Path)) return true;
        for (int attempts = 0; attempts < 5; attempts++)
        {
            var state = await _git.AnalyzeRepositoryAsync(op.Path, op.Url, op.Source, _cts!.Token);
            if (state.State is RepositoryState.Clean or RepositoryState.SetupInterrupted || state.State == RepositoryState.LocalModifications && op.Action == "UpdateBranch")
            {
                var origin = await _git.ValidateExistingRepositoryAsync(op.Url, op.Path, _cts.Token);
                if (!origin.Success) throw new IOException(origin.ErrorMessage);
                return true;
            }
            if (state.State == RepositoryState.NotARepository || !state.IsHealthy && state.State != RepositoryState.UnsafeRepository) throw new IOException(state.ErrorMessage);
            var dialog = new RepoRecoveryDialog(state) { Owner = Application.Current.MainWindow };
            if (dialog.ShowDialog() != true) return false;
            bool recovered;
            switch (dialog.Result)
            {
                case RepoRecoveryAction.Resume:
                case RepoRecoveryAction.Repair: recovered = await _git.RepairRepositoryAsync(op.Path, LogGit, _cts.Token); break;
                case RepoRecoveryAction.StartFresh: _git.CleanUpDirectory(op.Path); Directory.CreateDirectory(op.Path); return true;
                case RepoRecoveryAction.Stash: recovered = await _git.StashChangesAsync(op.Path, _cts.Token); break;
                case RepoRecoveryAction.Discard: recovered = await _git.DiscardChangesAsync(op.Path, _cts.Token); break;
                case RepoRecoveryAction.AbortMerge: recovered = await _git.AbortMergeOrRebaseAsync(op.Path, _cts.Token); break;
                case RepoRecoveryAction.FixOwnership: recovered = _runner.FixDirectoryOwnership(op.Path); break;
                default: return false;
            }
            if (!recovered) throw new IOException("Repository recovery did not complete. Resolve the reported issue and retry.");
        }
        throw new IOException("Repository still needs recovery. Resolve its state manually before retrying.");
    }
    private async Task StepAsync(SetupStepType type, Func<Task<bool>> action)
    {
        _cts!.Token.ThrowIfCancellationRequested();
        SetStepStatus(type, StepStatus.Running); BusyMessage = SetupSteps.First(step => step.StepType == type).Name;
        try { if (!await action()) throw new IOException(BusyMessage + " failed. Review command output."); SetStepStatus(type, StepStatus.Completed); }
        catch { SetStepStatus(type, StepStatus.Failed); throw; }
        ProgressValue = Math.Min(85, ((int)type + 1) * 10);
        if (_operation != null && WorkspacePaths.HasRepository(_operation.Path)) WriteCheckpoint(_operation);
    }
    private async Task BuildAsync(Operation op)
    {
        SetStepStatus(SetupStepType.PackagesRestore, StepStatus.Running); SetStepStatus(SetupStepType.SolutionBuild, StepStatus.Running);
        int code = await LaunchAsync(op with { Settings = op.Settings.WithMode("BuildOnly") });
        SetStepStatus(SetupStepType.PackagesRestore, code == 0 ? StepStatus.Completed : StepStatus.Failed);
        SetStepStatus(SetupStepType.SolutionBuild, code == 0 ? StepStatus.Completed : StepStatus.Failed);
        if (code != 0) throw new IOException($"Build exited with code {code}.");
    }
    private Task<LaunchSettings?> PromptLaunchSettingsAsync(Operation op, string action)
    {
        var settings = op.Settings;
        string preferredRunMode = settings.ExecutionMode;
        if (action is "Setup" or "Repair" or "Build") settings.ExecutionMode = "BuildOnly";
        var dialog = new LaunchConfigurationDialog(settings, action) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return Task.FromResult<LaunchSettings?>(null);
        LaunchScriptAdapter.Validate(settings);
        _launchSettings.Save(op.Path, action is "Setup" or "Repair" or "Build" ? settings.WithMode(preferredRunMode) : settings);
        return Task.FromResult<LaunchSettings?>(settings);
    }
    private async Task RunApplicationAsync(Operation op, bool buildOnly)
    {
        var settings = await PromptLaunchSettingsAsync(op, buildOnly ? "Build" : "Run");
        if (settings == null) { FinishCancelled(); return; }
        op = op with { Settings = settings }; _operation = op;
        var head = await _runner.RunAsync("git", "rev-parse HEAD", op.Path, cancellationToken: _cts!.Token);
        if (head.ExitCode != 0) throw new IOException("Cannot resolve the workspace revision.");
        GitCommitSha = head.StandardOutput.Trim();
        if (settings.ExecutionMode != "BuildOnly")
        {
            if (!File.Exists(Path.Combine(op.Path, "Config", "connections.config")))
            { ShowDatabaseForm(op.Path); ExecutionSummaryText = "Configure the workspace database connection before running."; return; }
            await VerifyConfiguredConnectionAsync(op.Path, _cts!.Token);
        }
        int code = await LaunchAsync(op);
        if (settings.ExecutionMode != "RunOnly" && BuildStatusText == "✓ Succeeded")
        {
            SetStepStatus(SetupStepType.PackagesRestore, StepStatus.Completed); SetStepStatus(SetupStepType.SolutionBuild, StepStatus.Completed);
            WriteCheckpoint(op);
        }
        if (code != 0) throw new IOException($"Application script exited with code {code}.");
        ExecutionSuccess = true; IsExecutionFinished = true; ProgressValue = 100;
        ExecutionSummaryText = settings.ExecutionMode == "BuildOnly" ? "Build completed successfully." : "Application session completed successfully.";
    }
    private async Task<int> LaunchAsync(Operation op)
    {
        var output = new System.Text.StringBuilder(); var errors = new System.Text.StringBuilder();
        BuildWarningsCount = 0; BuildErrorsCount = 0; BuildConfigText = op.Settings.Configuration;
        BusyMessage = op.Settings.ExecutionMode == "RunOnly" ? "Starting application..." : "Building the solution...";
        var watch = Stopwatch.StartNew();
        int? buildExit = null;
        TimeSpan? buildDuration = null;
        if (op.Settings.ExecutionMode != "BuildOnly") StartMonitoring();
        try
        {
            int code = await _launcher.RunScriptAsync(op.Path, op.Settings, line =>
            {
                Ui(() =>
                {
                    _log.LogInfo("Application", line);
                    line = LogService.Redact(line);
                    AppendBounded(output, line);
                    if (line.StartsWith("BEACON_BUILD_EXIT:") && int.TryParse(line["BEACON_BUILD_EXIT:".Length..], out int compiled)) { buildExit = compiled; buildDuration = watch.Elapsed; }
                    if (line.Contains(": warning", StringComparison.OrdinalIgnoreCase)) { BuildWarningsCount++; AppendBounded(errors, line); }
                    if (line.Contains(": error", StringComparison.OrdinalIgnoreCase) || line.Contains("error MSB", StringComparison.OrdinalIgnoreCase)) { BuildErrorsCount++; AppendBounded(errors, line); }
                    var warningSummary = System.Text.RegularExpressions.Regex.Match(line, @"^\s*(\d+)\s+Warning\(s\)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var errorSummary = System.Text.RegularExpressions.Regex.Match(line, @"^\s*(\d+)\s+Error\(s\)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (warningSummary.Success) BuildWarningsCount = int.Parse(warningSummary.Groups[1].Value);
                    if (errorSummary.Success) BuildErrorsCount = int.Parse(errorSummary.Groups[1].Value);
                    if (line.StartsWith("BEACON_BACKEND_PID:") && int.TryParse(line["BEACON_BACKEND_PID:".Length..], out int backend)) _backendPid = backend;
                    if (line.StartsWith("BEACON_FRONTEND_PID:") && int.TryParse(line["BEACON_FRONTEND_PID:".Length..], out int frontend)) _frontendPid = frontend;
                    if (line.Contains("1. Building")) RuntimeCurrentOperation = "Building solution...";
                    else if (line.Contains("2. Launching Backend")) RuntimeCurrentOperation = "Starting backend...";
                    else if (line.Contains("3. Launching Frontend")) RuntimeCurrentOperation = "Starting frontend...";
                    else if (line.Contains("Running (")) RuntimeCurrentOperation = "Application is running.";
                });
            }, _cts!.Token);
            if (op.Settings.ExecutionMode != "RunOnly")
            {
                BuildStatusText = (buildExit ?? code) == 0 ? "✓ Succeeded" : "✗ Failed";
                BuildDurationText = (buildDuration ?? watch.Elapsed).ToString(@"hh\:mm\:ss"); BuildOutputText = output.ToString();
                BuildErrorsText = errors.Length == 0 ? (code == 0 ? "No build errors." : output.ToString()) : errors.ToString();
                ActiveResultTab = code == 0 ? "Output" : "Errors"; IsBuildResultsVisible = true;
            }
            return code;
        }
        catch
        {
            if (op.Settings.ExecutionMode != "RunOnly") { BuildStatusText = buildExit == 0 ? "✓ Succeeded" : "✗ Failed"; BuildOutputText = output.ToString(); BuildErrorsText = errors.ToString(); IsBuildResultsVisible = true; }
            throw;
        }
        finally { StopMonitoring(); }
    }
    private static void AppendBounded(System.Text.StringBuilder builder, string line)
    {
        const int maximum = 4 * 1024 * 1024;
        builder.AppendLine(line);
        if (builder.Length > maximum) builder.Remove(0, builder.Length - maximum);
    }
    private async Task<SetupMetadata?> ReadValidCheckpointAsync(Operation op)
    {
        var metadata = _git.ReadSetupMetadata(op.Path);
        if (metadata == null || metadata.FormatVersion != 2 || metadata.WorkspacePath != WorkspacePaths.Key(op.Path) ||
            !WorkspacePaths.SameRemote(metadata.RepoUrl, op.Url) || metadata.BranchOrCommit != op.Source || metadata.BuildConfig != op.Settings.Configuration) return null;
        var head = await _runner.RunAsync("git", "rev-parse HEAD", op.Path, cancellationToken: _cts!.Token);
        if (head.ExitCode != 0 || metadata.CommitSha != head.StandardOutput.Trim()) return null;
        if (!(await _git.AnalyzeRepositoryAsync(op.Path, "", "", _cts.Token)).IsWorkingTreeClean) return null;
        if (!File.Exists(Path.Combine(op.Path, "artifacts", "bin", "EngineUpgraded", op.Settings.Configuration.ToLowerInvariant(), "SheetKraftWeb.dll")) ||
            !File.Exists(Path.Combine(op.Path, "artifacts", "bin", "BeaconAvaClient", op.Settings.Configuration.ToLowerInvariant(), "AvaClient.Desktop.exe"))) return null;
        return !string.IsNullOrEmpty(metadata.BuildArtifactsHash) && metadata.BuildArtifactsHash == BuildArtifactsHash(op) ? metadata : null;
    }
    private void WriteCheckpoint(Operation op)
    {
        var completed = SetupSteps.Where(step => step.Status == StepStatus.Completed).Select(step => step.StepType).ToList();
        _git.WriteSetupMetadata(op.Path, new SetupMetadata { CompletedSteps = completed, LastCompletedStep = completed.Count == 0 ? SetupStepType.WorkspaceValidate : completed.Max(),
            Timestamp = DateTime.UtcNow, RepoUrl = op.Url, BranchOrCommit = op.Source, BuildConfig = op.Settings.Configuration, CommitSha = GitCommitSha ?? "",
            BuildArtifactsHash = completed.Contains(SetupStepType.SolutionBuild) ? BuildArtifactsHash(op) : "" });
    }
    private static string BuildArtifactsHash(Operation op)
    {
        string output = Path.Combine(op.Path, "artifacts", "bin");
        string[] files = [Path.Combine(output, "EngineUpgraded", op.Settings.Configuration.ToLowerInvariant(), "SheetKraftWeb.dll"), Path.Combine(output, "BeaconAvaClient", op.Settings.Configuration.ToLowerInvariant(), "AvaClient.Desktop.exe")];
        if (files.Any(file => !File.Exists(file))) return "";
        var directories = files.Select(Path.GetDirectoryName).Distinct().ToArray();
        var binaries = directories.SelectMany(directory => WorkspacePaths.EnumerateSafeFiles(directory!))
            .Where(file => Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase);
        var manifest = new System.Text.StringBuilder();
        foreach (var binary in binaries)
        {
            using var stream = File.OpenRead(binary);
            manifest.Append(Path.GetRelativePath(op.Path, binary)).Append(':').Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))).AppendLine();
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifest.ToString())));
    }
    private void ResetSetupSteps()
    {
        SetupSteps.Clear();
        foreach (var (type, name) in new[] {
            (SetupStepType.RepositoryVerify,"Verify repository"), (SetupStepType.WorkspaceValidate,"Validate workspace"), (SetupStepType.RepositoryClone,"Clone repository"),
            (SetupStepType.BranchCheckout,"Check out branch / commit"), (SetupStepType.SubmodulesUpdate,"Update submodules"), (SetupStepType.PackagesRestore,"Restore packages"),
            (SetupStepType.SolutionBuild,"Build solution"), (SetupStepType.DatabaseSetup,"Configure database"), (SetupStepType.ConnectionValidation,"Verify configured connection") }) SetupSteps.Add(new(type, name));
    }
    private void SetStepStatus(SetupStepType type, StepStatus status) { var step = SetupSteps.FirstOrDefault(step => step.StepType == type); if (step != null) step.Status = status; }

    [RelayCommand] public async Task ConfirmBuildAsync()
    {
        if (!Path.IsPathFullyQualified(LocalPath)) return;
        if (IsBusy || !await _gate.WaitAsync(0)) return;
        await RunOperationAsync(new("Build", WorkspacePaths.Normalize(LocalPath), RepoUrl, Branch, _launchSettings.Load(LocalPath)));
    }
    [RelayCommand] public void DeclineBuild() { if (IsBusy) return; IsBuildPromptVisible = false; IsRunApplicationPromptVisible = true; ExecutionSuccess = true; IsExecutionFinished = true; ExecutionSummaryText = "Branch update completed; build skipped."; }
    [RelayCommand] public async Task ConfirmRunApplicationAsync() { if (IsBusy) return; SelectedAction = "RunApplication"; await ExecuteActionAsync(); }
    [RelayCommand] public void DeclineRunApplication() { IsRunApplicationPromptVisible = false; IsBuildResultsVisible = false; }
    [RelayCommand(CanExecute = nameof(CanContinueToDbSetup))] public void ContinueToDbSetup() { IsBuildResultsVisible = false; ShowDatabaseForm(LocalPath); IsExecutionFinished = false; }

    private void ShowDatabaseForm(string path)
    {
        if (File.Exists(Path.Combine(path, "Config", "connections.config")))
        {
            try
            {
                var saved = new NpgsqlConnectionStringBuilder(ConfigService.ReadApplicationConnection(path));
                DbHost = saved.Host ?? "localhost"; DbPort = saved.Port.ToString(System.Globalization.CultureInfo.InvariantCulture); DbName = saved.Database ?? "Beacon";
                DbUser = saved.Username ?? "BeaconUser"; DbPassword = saved.Password ?? "";
            }
            catch (Exception ex) { _log.LogWarning("Configuration", "Could not load the saved application connection: " + ex.Message); }
        }
        IsDbSetupFormVisible = true;
    }

    [RelayCommand] public async Task RunDbSetupAsync()
    {
        if (IsBusy || !await _gate.WaitAsync(0)) return;
        string path = LocalPath, host = DbHost, name = DbName, user = DbUser, password = DbPassword, adminUser = DbAdminUser, adminPassword = DbAdminPassword;
        int port = int.TryParse(DbPort, out var parsedPort) ? parsedPort : 0; bool updatePassword = UpdateExistingRolePassword, updateOwner = UpdateExistingDatabaseOwner;
        IsBusy = true; IsExecutionFinished = false; ExecutionSuccess = false; CanRetryExecution = false; _cts = new CancellationTokenSource();
        try
        {
            using var workspaceLock = WorkspaceOperationLock.Acquire(path);
            DatabaseService.ValidateInputs(host, name, user, port);
            path = WorkspacePaths.Normalize(path);
            if (!Directory.Exists(Path.Combine(path, "SheetKraftWeb", "ConfigSamples", "Runner"))) throw new DirectoryNotFoundException("Select a repository with the configuration samples before setting up its database.");
            BusyMessage = "Creating/verifying database objects..."; SetStepStatus(SetupStepType.DatabaseSetup, StepStatus.Running);
            if (!await _database.SetupDatabaseAsync(host, adminUser, adminPassword, name, user, password, message => _log.LogInfo("Database", message), _cts.Token, port, updatePassword, updateOwner)) throw new IOException("Database setup failed. Review the database log; completed objects have been retained.");
            // Check the supplied app credentials before replacing a working configuration file.
            var connection = new NpgsqlConnectionStringBuilder { Host = host, Port = port, Database = name, Username = user, Password = password, Timeout = 10 };
            await using (var test = new NpgsqlConnection(connection.ConnectionString)) await test.OpenAsync(_cts.Token);
            BusyMessage = "Updating workspace configuration...";
            if (!await _config.SetupConfigurationAsync(path, host, name, user, password, onProgress: message => _log.LogInfo("Configuration", message), cancellationToken: _cts.Token, port: port)) throw new IOException("Configuration update failed. Review the log.");
            SetStepStatus(SetupStepType.DatabaseSetup, StepStatus.Completed); SetStepStatus(SetupStepType.ConnectionValidation, StepStatus.Running);
            await VerifyConfiguredConnectionAsync(path, _cts.Token);
            SetStepStatus(SetupStepType.ConnectionValidation, StepStatus.Completed);
            var settings = _launchSettings.Load(path);
            AppLauncherService.SynchronizeRuntimeConfiguration(path, settings.Configuration);
            WriteCheckpoint(new(SelectedAction, path, RepoUrl, Branch, settings));
            IsDbSetupFormVisible = false; IsBuildResultsVisible = false; IsRunApplicationPromptVisible = true;
            IsExecutionFinished = true; ExecutionSuccess = true; ProgressValue = 100;
            ExecutionSummaryText = "Database objects and configured connection verified. Schema upgrades run when the backend starts.";
        }
        catch (OperationCanceledException) { FinishCancelled(); }
        catch (Exception ex)
        {
            foreach (var step in SetupSteps.Where(step => step.Status == StepStatus.Running)) step.Status = StepStatus.Failed;
            FinishFailure(ex);
        }
        finally { EndOperation(); }
    }
    private static async Task VerifyConfiguredConnectionAsync(string path, CancellationToken token)
    {
        var builder = new NpgsqlConnectionStringBuilder(ConfigService.ReadApplicationConnection(path)) { Timeout = 10, CommandTimeout = 15 };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(token);
    }

    [RelayCommand] public async Task RefreshPrerequisitesAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        IsBusy = true; BusyMessage = "Scanning dependencies..."; _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var pending = PrerequisiteItem.CreatePendingItems();
        for (int i = 0; i < pending.Count; i++) Prerequisites[i] = pending[i];
        ProgressValue = 0;
        StatusSummary = $"0 of {Prerequisites.Count} dependencies checked.";
        OnPropertyChanged(nameof(HasPrerequisiteWarnings));
        void UpdateItem(PrerequisiteItem item) => Ui(() =>
        {
            for (int i = 0; i < Prerequisites.Count; i++)
                if (Prerequisites[i].Name == item.Name) { Prerequisites[i] = item; break; }
            int completed = Prerequisites.Count(entry => entry.Status is not (ValidationStatus.Pending or ValidationStatus.Running));
            ProgressValue = 100.0 * completed / Prerequisites.Count;
            StatusSummary = $"{completed} of {Prerequisites.Count} dependencies checked.";
            OnPropertyChanged(nameof(HasPrerequisiteWarnings));
        });
        try
        {
            var items = await Task.Run(() => _prerequisitesService.ValidateAllAsync(onItemChecked: UpdateItem,
                onProgress: message => { _log.LogInfo("Prerequisites", message); Ui(() => BusyMessage = message); }, cancellationToken: token), token);
            foreach (var item in items) UpdateItem(item);
            StatusSummary = $"{items.Count} dependencies checked; {items.Count(item => item.Status is ValidationStatus.Missing or ValidationStatus.Failed or ValidationStatus.VersionMismatch)} need attention.";
            OnPropertyChanged(nameof(HasPrerequisiteWarnings));
        }
        catch (OperationCanceledException) { StatusSummary = "Dependency scan cancelled."; }
        catch (Exception ex) { StatusSummary = "Dependency scan failed: " + ex.Message; }
        finally
        {
            for (int i = 0; i < Prerequisites.Count; i++)
                if (Prerequisites[i].Status == ValidationStatus.Running)
                {
                    var item = pending.First(entry => entry.Name == Prerequisites[i].Name);
                    item.Status = ValidationStatus.Pending;
                    Prerequisites[i] = item;
                }
            EndOperation();
        }
        if (!string.IsNullOrWhiteSpace(LocalPath)) await ValidateWorkspaceAsync();
    }
    [RelayCommand] public async Task DownloadPrerequisiteAsync(PrerequisiteItem item)
    {
        if (IsBusy) return;
        if (item.Name is not ("WebAssembly Tools (wasm-tools)" or "dotnet-script"))
        { if (!string.IsNullOrEmpty(item.DownloadUrl)) Process.Start(new ProcessStartInfo(item.DownloadUrl) { UseShellExecute = true }); return; }
        if (item.Name == "WebAssembly Tools (wasm-tools)" && !IsAdminMode) { MessageBox.Show("Restart as Administrator to install the SDK workload."); return; }
        if (!await _gate.WaitAsync(0)) return;
        IsBusy = true; _cts = new CancellationTokenSource(); BusyMessage = "Installing " + item.Name;
        try
        {
            var result = await _runner.RunAsync("dotnet", item.Name == "dotnet-script" ? "tool install -g dotnet-script" : "workload install wasm-tools", cancellationToken: _cts.Token,
                context: item.Name == "dotnet-script" ? ProcessExecutionContext.CurrentUser : ProcessExecutionContext.Elevated);
            if (result.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
        }
        catch (OperationCanceledException) { StatusSummary = "Installation cancelled."; }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Installation failed"); }
        finally { EndOperation(); }
        await RefreshPrerequisitesAsync();
    }
    [RelayCommand] public void CancelOperation() { _cts?.Cancel(); _validationCts?.Cancel(); }
    [RelayCommand] public void ViewLogs() => _log.OpenLogFolder();
    [RelayCommand] public void SetActiveTab(string tab) => ActiveResultTab = tab;
    [RelayCommand] public void CopyBuildOutput() { try { Clipboard.SetText(ActiveResultTab == "Errors" ? BuildErrorsText : BuildOutputText); } catch (Exception ex) { _log.LogWarning("Clipboard", ex.Message); } }
    [RelayCommand] public void ExportBuildOutput()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Log files (*.log)|*.log", FileName = $"beacon-{DateTime.Now:yyyyMMdd-HHmmss}.log" };
        if (dialog.ShowDialog() == true) { try { File.WriteAllText(dialog.FileName, ActiveResultTab == "Errors" ? BuildErrorsText : BuildOutputText); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    }
    private void LogGit(string message) => _log.LogInfo("Git", message);
    private void StartMonitoring()
    {
        StopMonitoring(); _backendPid = null; _frontendPid = null; _started = DateTime.UtcNow;
        IsRunningApplicationActive = true; RuntimeBackendStatus = "Starting"; RuntimeFrontendStatus = "Starting";
        _monitor = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _monitor.Tick += (_, _) =>
        {
            RuntimeElapsedTime = (DateTime.UtcNow - _started).ToString(@"hh\:mm\:ss");
            RuntimeBackendStatus = ProcessRunning(_backendPid) ? "Running" : _backendPid == null ? "Starting" : "Stopped";
            RuntimeFrontendStatus = ProcessRunning(_frontendPid) ? "Running" : _frontendPid == null ? "Starting" : "Stopped";
        };
        _monitor.Start();
    }
    private static bool ProcessRunning(int? pid) { if (pid == null) return false; try { using var process = Process.GetProcessById(pid.Value); return !process.HasExited; } catch (ArgumentException) { return false; } }
    private void StopMonitoring() { _monitor?.Stop(); _monitor = null; IsRunningApplicationActive = false; RuntimeBackendStatus = "Stopped"; RuntimeFrontendStatus = "Stopped"; RuntimeCurrentOperation = "Stopped"; }
    private void Ui(Action action)
    {
        if (_disposed) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else if (!dispatcher.HasShutdownStarted) dispatcher.Invoke(action);
    }
    private static string GitConfigPath => Path.Combine(SettingsStore.DirectoryPath, "git_config.json");
    private class GitConfigSettings { public GitConfigSettings() { } public string RepoUrl { get; set; } = ""; public string Branch { get; set; } = ""; public string LocalPath { get; set; } = ""; public string LastValidationTimestampUtc { get; set; } = ""; }
    private void SaveGitSettings(string? verifiedAt = null)
    {
        try
        {
            SettingsStore.Update(GitConfigPath, () => new GitConfigSettings(), previous => new()
            { RepoUrl = RepoUrl, Branch = Branch, LocalPath = IsWorkspaceValidated ? LocalPath : previous.LocalPath, LastValidationTimestampUtc = verifiedAt ?? previous.LastValidationTimestampUtc });
        }
        catch (Exception ex) { _log.LogWarning("Settings", "Could not save workspace settings: " + ex.Message); }
    }
    public void Dispose() { _disposed = true; CancelOperation(); StopMonitoring(); }
}

public enum WorkspaceValidationStatus { NotValidated, Validating, Success, Warning, Failed }

internal static class LaunchSettingsExtensions
{
    public static LaunchSettings WithMode(this LaunchSettings settings, string mode) => new()
    { Configuration = settings.Configuration, ExecutionMode = mode, Environment = settings.Environment, Urls = settings.Urls, AllowUpgrade = settings.AllowUpgrade,
        PagesFile = settings.PagesFile, AdditionalBackendArguments = settings.AdditionalBackendArguments, AdditionalFrontendArguments = settings.AdditionalFrontendArguments, RememberSettings = settings.RememberSettings };
}
