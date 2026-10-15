using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml.Linq;
using BeaconSetupManager.Models;
using BeaconSetupManager.Services;
using BeaconSetupManager.ViewModels;
using Npgsql;

internal static class Program
{
    private static int _passed;
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static string Scratch = "";
    [STAThread]
    private static int Main(string[] args)
    {
        Scratch = Path.Combine(Root, "artifacts", "validation", "regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Scratch);
        SettingsStore.DirectoryOverride = Path.Combine(Scratch, "settings-store");
        try
        {
            if (!args.Contains("--ui-only")) RunAsync().GetAwaiter().GetResult();
            VerifyUi();
            Console.WriteLine($"PASS: {_passed} regression checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        _passed++; Console.WriteLine("PASS: " + name);
    }
    private static void Throws<T>(Action action, string name) where T : Exception
    { try { action(); } catch (T) { Check(true, name); return; } throw new Exception("Expected " + typeof(T).Name + ": " + name); }
    private static async Task RunAsync()
    {
        foreach (var argument in new[] { "", "one word", "a\"b", "C:\\folder with spaces\\", "C:\\plain\\", "apostrophe's" })
            Check(CommandArguments.Split(CommandArguments.Quote(argument)).SequenceEqual(new[] { argument }), "Windows argument round trip: " + argument);
        Throws<ArgumentException>(() => CommandArguments.Split("--name \"unterminated"), "Reject unmatched argument quotes");
        Throws<ArgumentException>(() => CommandArguments.ValidateReference("--force"), "Reject Git option as reference");
        Check(GitService.ParseStatus(" M folder/file with spaces.cs\0R  new name.cs\0old name.cs\0?? untracked.txt\0").SequenceEqual(new[] { "folder/file with spaces.cs", "new name.cs", "untracked.txt" }), "Git NUL status handles rename, spaces and untracked paths");
        Throws<InvalidOperationException>(() => WorkspacePaths.ValidateDeletion(Path.GetPathRoot(Scratch)!), "Reject drive-root deletion");
        var userFolder = Path.Combine(Scratch, "user-files"); Directory.CreateDirectory(userFolder); File.WriteAllText(Path.Combine(userFolder, "keep.txt"), "keep");
        var fake = new FakeRunner(); var git = new GitService(fake, new TestLog());
        Throws<InvalidOperationException>(() => git.CleanUpDirectory(userFolder), "Reject deleting a non-repository");
        Check(File.ReadAllText(Path.Combine(userFolder, "keep.txt")) == "keep", "Rejected cleanup retains existing files");
        try { await git.CloneRepositoryAsync("https://example.invalid/repo", userFolder); throw new Exception("Nonempty clone allowed"); }
        catch (IOException) { Check(File.Exists(Path.Combine(userFolder, "keep.txt")), "Clone refuses nonempty destination without deleting it"); }

        var checkpoint = Path.Combine(Scratch, "settings.json");
        File.WriteAllText(checkpoint, "{bad json");
        SettingsStore.Update(checkpoint, () => new Dictionary<string, string>(), values => { values["key"] = "recovered"; return values; });
        Check(SettingsStore.Read(checkpoint, () => new Dictionary<string, string>())["key"] == "recovered" && File.Exists(checkpoint + ".corrupt"), "Recover corrupted settings with backup");
        Check(!Directory.EnumerateFiles(Scratch, "*.tmp").Any(), "Atomic settings leave no temporary file");
        var savedLaunch = new LaunchSettingsService();
        savedLaunch.Save(userFolder, new LaunchSettings { Environment = "Custom-one" });
        Check(savedLaunch.Load(userFolder).Environment == "Custom-one" && savedLaunch.Load(Path.Combine(Scratch, "other-workspace")).Environment == "BeaconDev", "Launch settings are isolated per workspace");
        savedLaunch.Save(userFolder, new LaunchSettings { Environment = "discard", RememberSettings = false });
        Check(savedLaunch.Load(userFolder).Environment == "BeaconDev", "Unremembered launch settings reset to defaults");
        using (var locked = WorkspaceOperationLock.Acquire(userFolder))
            Throws<IOException>(() => WorkspaceOperationLock.Acquire(userFolder), "Prevent concurrent workspace operations across manager instances");
        Throws<IOException>(() => WorkspacePaths.EnsureContainedWithoutLinks(userFolder, Scratch), "Reject configuration destination outside workspace");

        string workspace = Path.Combine(Scratch, "config-workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "SheetKraftWeb", "ConfigSamples", "Runner", "nested"));
        File.WriteAllText(Path.Combine(workspace, "SheetKraftWeb", "ConfigSamples", "Runner", "nested", "sample.txt"), "sample");
        Directory.CreateDirectory(Path.Combine(workspace, "Config"));
        var configFile = Path.Combine(workspace, "Config", "connections.config");
        File.WriteAllText(configFile, "<connectionStrings><add name='Other' connectionString='retained'/><add name='SheetKraft' connectionString='old'/></connectionStrings>");
        const string password = "a;b'c\"d\\e";
        var config = new ConfigService();
        Check(await config.SetupConfigurationAsync(workspace, "localhost", "Beacon", "app_user", password, true, port: 5544), "Standalone database configuration updates selected connection");
        var parsed = new NpgsqlConnectionStringBuilder(ConfigService.ReadApplicationConnection(workspace));
        Check(parsed.Password == password && parsed.Port == 5544, "Connection strings preserve special passwords and explicit port");
        Check(XDocument.Load(configFile).Root!.Elements("add").Any(entry => (string?)entry.Attribute("name") == "Other" && (string?)entry.Attribute("connectionString") == "retained"), "Preserve unrelated connection definitions");
        Check(File.Exists(configFile + ".bak") && File.Exists(Path.Combine(workspace, "Config", "nested", "sample.txt")), "Back up connections and copy nested samples");
        File.WriteAllText(configFile, "invalid xml");
        Check(!await config.SetupConfigurationAsync(workspace, "localhost", "Beacon", "user", "password") && File.ReadAllText(configFile) == "invalid xml", "Malformed configuration is retained rather than overwritten");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await config.SetupConfigurationAsync(workspace, "localhost", "Beacon", "user", "password", cancellationToken: cancelled.Token); throw new Exception("Config cancellation ignored"); }
            catch (OperationCanceledException) { Check(true, "Configuration honours cancellation"); }
        }
        Throws<ArgumentException>(() => DatabaseService.ValidateInputs("localhost", new string('a', 64), "user", 5432), "Reject overlong database identifier");
        Throws<ArgumentException>(() => DatabaseService.ValidateInputs("localhost", "db", "user", 0), "Reject invalid database port");
        Throws<ArgumentException>(() => LaunchScriptAdapter.Validate(new LaunchSettings { Urls = "file:///c:/app" }), "Reject non-HTTP launch URLs");
        Check(!LogService.Redact("Password=hidden; --token=my-secret https://user:credential@example.com/repo").Contains("hidden"), "Redact connection-string password");
        string checkpointRoot = Path.Combine(Scratch, "checkpoint-workspace");
        string checkpointBackend = Path.Combine(checkpointRoot, "artifacts", "bin", "EngineUpgraded", "debug"), checkpointFrontend = Path.Combine(checkpointRoot, "artifacts", "bin", "BeaconAvaClient", "debug");
        Directory.CreateDirectory(checkpointBackend); Directory.CreateDirectory(checkpointFrontend);
        File.WriteAllText(Path.Combine(checkpointBackend, "SheetKraftWeb.dll"), "framework");
        File.WriteAllText(Path.Combine(checkpointFrontend, "AvaClient.Desktop.exe"), "host");
        string plugin = Path.Combine(checkpointBackend, "Beacon-Plugin.dll"); File.WriteAllText(plugin, "revision-one");
        const string checkpointSha = "0123456789012345678901234567890123456789";
        var checkpointRunner = new FakeRunner { Response = args => FakeRunner.Result(0, args.Contains("rev-parse") ? checkpointSha : "") };
        var checkpointGit = new GitService(checkpointRunner, new TestLog());
        using (var checkpointModel = new MainViewModel(new FakePrerequisites(), checkpointGit, new DatabaseService(), config, new TestLog(), new FakeAdmin(), new AppLauncherService(checkpointRunner, new TestLog()), checkpointRunner, new FakeSettings()))
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var operationType = typeof(MainViewModel).GetNestedType("Operation", System.Reflection.BindingFlags.NonPublic)!;
            var operation = Activator.CreateInstance(operationType, "Setup", checkpointRoot, "https://example.invalid/repo", "main", new LaunchSettings())!;
            var hashMethod = typeof(MainViewModel).GetMethod("BuildArtifactsHash", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            string hash = (string)hashMethod.Invoke(null, new[] { operation })!;
            var progress = new SetupMetadata { RepoUrl = "https://example.invalid/repo", BranchOrCommit = "main", BuildConfig = "Debug", CommitSha = checkpointSha, BuildArtifactsHash = hash, CompletedSteps = new() { SetupStepType.SolutionBuild } };
            checkpointGit.WriteSetupMetadata(checkpointRoot, progress);
            typeof(MainViewModel).GetField("_cts", flags)!.SetValue(checkpointModel, new CancellationTokenSource());
            var read = typeof(MainViewModel).GetMethod("ReadValidCheckpointAsync", flags)!;
            async Task<SetupMetadata?> ReadCheckpoint() => await (Task<SetupMetadata?>)read.Invoke(checkpointModel, new[] { operation })!;
            Check(await ReadCheckpoint() != null, "Resume accepts a verified checkpoint for the current source and binaries");
            progress.CommitSha = new string('a', 40); checkpointGit.WriteSetupMetadata(checkpointRoot, progress);
            Check(await ReadCheckpoint() == null, "Resume rejects a checkpoint for another commit");
            progress.CommitSha = checkpointSha; checkpointGit.WriteSetupMetadata(checkpointRoot, progress);
            File.WriteAllText(plugin, "revision-two");
            Check(await ReadCheckpoint() == null, "Resume rejects changed plugin binaries even when framework DLL and app host are unchanged");
            progress.CompletedSteps = new() { SetupStepType.DatabaseSetup, SetupStepType.ConnectionValidation }; checkpointGit.WriteSetupMetadata(checkpointRoot, progress);
            Check(!checkpointGit.ReadSetupMetadata(checkpointRoot)!.CompletedSteps.Contains(SetupStepType.SolutionBuild), "Database-only progress never certifies earlier build steps");
        }

        var real = new ProcessRunner(new WindowsUserProcessLauncher(new TestLog()), new TestLog());
        string longCommand = "[Console]::WriteLine('long-command-ok'); exit 7; # " + new string('x', 3000);
        string longArguments = CommandArguments.EncodedPowerShell(longCommand);
        var originalVariables = new Dictionary<string, string> { ["BEACON_REPO_ROOT"] = "a path with spaces and an apostrophe's" };
        var transported = PowerShellCommandTransport.Prepare("powershell.exe", longArguments, originalVariables);
        Check(transported.Arguments.Length + "powershell.exe".Length + 4 < 1024, "Long encoded PowerShell command is shortened below the administrator-user API limit");
        Check(originalVariables.Count == 1 && transported.Environment!["BEACON_REPO_ROOT"] == originalVariables["BEACON_REPO_ROOT"], "Command transport preserves custom variables without modifying the caller dictionary");
        var longResult = await real.RunAsync("powershell.exe", longArguments, environmentVariables: originalVariables);
        Check(longResult.ExitCode == 7 && longResult.StandardOutput.Contains("long-command-ok"), "Long-command bootstrap executes as current user and preserves the exit code");
        var missing = await real.RunAsync("beacon-nonexistent-regression-command.exe", "");
        Check(missing.ExitCode != 0 && missing.StandardError.Contains("Win32 2:"), "Process-start failure preserves the native Windows error code and message");
        for (int i = 0; i < 30; i++)
        {
            var fast = await real.RunAsync("cmd.exe", "/d /c exit 0", cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token, context: ProcessExecutionContext.Elevated);
            if (fast.ExitCode != 0) throw new Exception("Immediate-exit process failed");
        }
        Check(true, "Thirty immediate-exit processes finish without event races");
        var output = await real.RunAsync("powershell.exe", CommandArguments.EncodedPowerShell("1..100 | ForEach-Object { [Console]::Out.WriteLine('out-' + $_); [Console]::Error.WriteLine('err-' + $_) }"), context: ProcessExecutionContext.Elevated);
        Check(output.StandardOutput.Contains("out-100") && output.StandardError.Contains("err-100"), "Drain both output streams before returning");
        foreach (var context in new[] { ProcessExecutionContext.CurrentUser, ProcessExecutionContext.Elevated })
        {
            int streamed = 0;
            var bounded = await real.RunAsync("powershell.exe", CommandArguments.EncodedPowerShell(
                "1..200 | ForEach-Object { [Console]::Out.WriteLine(('out-' + $_ + ':' + ('x' * 100))); [Console]::Error.WriteLine(('err-' + $_ + ':' + ('y' * 100))) }"),
                onOutputLine: _ => streamed++, context: context, maximumCapturedOutputCharacters: 2048);
            Check(bounded.ExitCode == 0 && streamed == 400 && bounded.StandardOutput.Length <= 2048 && bounded.StandardError.Length <= 2048 &&
                bounded.StandardOutput.Contains("out-200:") && bounded.StandardError.Contains("err-200:") && !bounded.StandardOutput.Contains("out-1:"),
                "Bounded capture retains the tail while streaming every output line: " + context);
        }
        int longLineLength = 0;
        var longLine = await real.RunAsync("powershell.exe", CommandArguments.EncodedPowerShell("[Console]::WriteLine(('x' * 8000) + 'end-marker')"),
            onOutputLine: line => longLineLength = line.Length, maximumCapturedOutputCharacters: 2048);
        Check(longLine.ExitCode == 0 && longLine.StandardOutput.Length <= 2048 && longLine.StandardOutput.Contains("end-marker") && longLineLength == 8010,
            "A single oversized output line stays bounded without truncating the streaming callback");
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var childSeen = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            string childCommand = "Start-Sleep -Seconds 30";
            var childArgs = CommandArguments.EncodedPowerShell(childCommand);
            string command = "$child=Start-Process powershell.exe -ArgumentList " + CommandArguments.PowerShellLiteral(childArgs) + " -WindowStyle Hidden -PassThru; [Console]::WriteLine($child.Id); Start-Sleep -Seconds 30";
            var running = real.RunAsync("powershell.exe", CommandArguments.EncodedPowerShell(command), onOutputLine: line => { if (int.TryParse(line, out var pid)) childSeen.TrySetResult(pid); }, cancellationToken: cts.Token, context: ProcessExecutionContext.Elevated);
            int child = await childSeen.Task.WaitAsync(TimeSpan.FromSeconds(8));
            cts.Cancel();
            try { await running; throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { Check(true, "Process cancellation remains a distinct result"); }
            bool alive; try { using var process = Process.GetProcessById(child); alive = !process.HasExited; } catch (ArgumentException) { alive = false; }
            Check(!alive, "Cancellation stops this operation's child process");
        }

        // Real local Git exercises command quoting and failure propagation; no remote network is used.
        var localRunner = new LocalRunner(real);
        string origin = Path.Combine(Scratch, "origin.git"), repo = Path.Combine(Scratch, "local repo");
        await localRunner.RunAsync("git", "init --bare " + CommandArguments.Quote(origin));
        await localRunner.RunAsync("git", "clone " + CommandArguments.Quote(origin) + " " + CommandArguments.Quote(repo));
        await localRunner.RunAsync("git", "config user.email regression@example.invalid", repo);
        await localRunner.RunAsync("git", "config user.name Regression", repo);
        File.WriteAllText(Path.Combine(repo, "file with spaces.txt"), "first");
        await localRunner.RunAsync("git", "add .", repo);
        await localRunner.RunAsync("git", "commit -m first", repo);
        await localRunner.RunAsync("git", "branch -M main", repo);
        await localRunner.RunAsync("git", "push -u origin main", repo);
        var localGit = new GitService(localRunner, new TestLog());
        Check((await localGit.CheckoutAndValidateLocalAsync("main", repo)).Success, "Local branch checkout and fast-forward verification");
        var sha = (await localRunner.RunAsync("git", "rev-parse HEAD", repo)).StandardOutput.Trim();
        Check((await localGit.CheckoutAndValidateLocalAsync(sha[..8], repo)).Success, "Resolve abbreviated commit reference");
        File.WriteAllText(Path.Combine(repo, "untracked file.txt"), "user data");
        var state = await localGit.AnalyzeRepositoryAsync(repo, "", "");
        Check(state.State == RepositoryState.LocalModifications && state.ModifiedFiles.Contains("untracked file.txt"), "Untracked paths make repository dirty");
        Check(await localGit.StashChangesAsync(repo) && !File.Exists(Path.Combine(repo, "untracked file.txt")), "Recovery stash includes untracked files");
        // Register an old-form submodule so fixtures do not need Git to rewrite a protected .git pointer file.
        var cloneSubmodule = await localRunner.RunAsync("git", "clone -b main " + CommandArguments.Quote(origin) + " nested-module", repo);
        if (cloneSubmodule.ExitCode != 0) throw new IOException(cloneSubmodule.StandardError);
        await localRunner.RunAsync("git", "config user.email regression@example.invalid", Path.Combine(repo, "nested-module"));
        await localRunner.RunAsync("git", "config user.name Regression", Path.Combine(repo, "nested-module"));
        File.WriteAllText(Path.Combine(repo, ".gitmodules"), "[submodule \"nested-module\"]\n\tpath = nested-module\n\turl = " + origin.Replace('\\', '/') + "\n\tbranch = main\n");
        var registerSubmodule = await localRunner.RunAsync("git", "add .gitmodules nested-module", repo);
        if (registerSubmodule.ExitCode != 0) throw new IOException(registerSubmodule.StandardError);
        var commitSubmodule = await localRunner.RunAsync("git", "commit -m add-submodule", repo);
        if (commitSubmodule.ExitCode != 0) throw new IOException(commitSubmodule.StandardError);
        string nestedFile = Path.Combine(repo, "nested-module", "file with spaces.txt");
        File.WriteAllText(nestedFile, "nested local edit");
        Check(await localGit.StashChangesAsync(repo) && File.ReadAllText(nestedFile) == "first", "Recovery stashes tracked edits inside submodules");
        File.WriteAllText(Path.Combine(repo, "nested-module", "untracked nested.txt"), "nested untracked");
        Check(await localGit.DiscardChangesAsync(repo) && !File.Exists(Path.Combine(repo, "nested-module", "untracked nested.txt")), "Explicit discard also clears untracked submodule files");
        fake.Response = args => args.Contains("pull") ? FakeRunner.Result(1, "", "cannot fast-forward") :
            args.Contains("rev-parse") ? FakeRunner.Result(0, sha) : FakeRunner.Result(0);
        Check(!(await git.CheckoutAndValidateLocalAsync("main", repo)).Success, "A failed pull cannot report successful checkout");

        // Load the provided legacy script but execute only safe fake-build fixtures.
        string beacon = @"C:\Users\QP-202208\Desktop\BeaconCode\BeaconWebdotnet9Upgrade\run.ps1";
        if (File.Exists(beacon))
        {
            string source = File.ReadAllText(beacon);
            Throws<NotSupportedException>(() => LaunchScriptAdapter.Adapt(source + "\nWrite-Output 'custom cleanup'"), "Reject legacy adaptation that would bypass custom cleanup logic");
            string adapted = LaunchScriptAdapter.Adapt(source);
            // Exercise the actual generated probe against a non-default loopback address.
            int probeStart = adapted.IndexOf("$endpoint = [Uri]", StringComparison.Ordinal);
            int probeEnd = adapted.IndexOf("if ($occupied)", probeStart, StringComparison.Ordinal);
            string probeCode = adapted[probeStart..probeEnd];
            var address = System.Net.IPAddress.Parse("127.0.0.2");
            var addressListener = new System.Net.Sockets.TcpListener(address, 0);
            addressListener.Start();
            try
            {
                int addressPort = ((System.Net.IPEndPoint)addressListener.LocalEndpoint).Port;
                var addressResult = await real.RunAsync("powershell.exe", CommandArguments.EncodedPowerShell(
                    "$Urls='http://127.0.0.2:" + addressPort + "'; " + probeCode + "; [Console]::WriteLine(('host={0}; occupied={1}' -f $endpointHost,$occupied)); if (-not $occupied -or $endpointHost -ne '127.0.0.2') { exit 1 }"),
                    context: ProcessExecutionContext.Elevated);
                Check(addressResult.ExitCode == 0, "Legacy probe detects a listener bound only to the configured interface" +
                    (addressResult.ExitCode == 0 ? "" : ": " + addressResult.StandardOutput + addressResult.StandardError));
            }
            finally { addressListener.Stop(); }
            foreach (var (url, expected) in new[] { ("http://0.0.0.0:80", "127.0.0.1"), ("http://*:80", "127.0.0.1"),
                ("http://+:80", "127.0.0.1"), ("http://[::]:80", "::1"), ("http://[::1]:80", "::1") })
            {
                string endpointCode = probeCode[..probeCode.IndexOf("$probe =", StringComparison.Ordinal)];
                var mapped = await real.RunAsync("powershell.exe", CommandArguments.EncodedPowerShell(
                    "$Urls=" + CommandArguments.PowerShellLiteral(url) + "; " + endpointCode + "; [Console]::WriteLine($endpointHost)"),
                    context: ProcessExecutionContext.Elevated);
                Check(mapped.ExitCode == 0 && mapped.StandardOutput.Trim() == expected, "Legacy probe resolves wildcard/IPv6 host: " + url);
            }
            var adaptedPath = Path.Combine(Scratch, "adapted.ps1"); File.WriteAllText(adaptedPath, adapted);
            string syntax = "$e=$null; $null=[System.Management.Automation.Language.Parser]::ParseFile(" + CommandArguments.PowerShellLiteral(adaptedPath) + ",[ref]$null,[ref]$e); if($e){$e | ForEach-Object {$_.Message}; exit 1}";
            var syntaxResult = await real.RunAsync("powershell.exe", CommandArguments.EncodedPowerShell(syntax), context: ProcessExecutionContext.Elevated);
            Check(syntaxResult.ExitCode == 0, "Adapted actual Beacon script parses in Windows PowerShell 5.1: " + syntaxResult.StandardOutput);
            var scriptRoot = Path.Combine(Scratch, "legacy fixture"); Directory.CreateDirectory(scriptRoot);
            File.WriteAllText(Path.Combine(scriptRoot, "run.ps1"), source);
            var fixture = new ScriptFixtureRunner(real, 0);
            var launcher = new AppLauncherService(fixture, new TestLog());
            var messages = new List<string>();
            Check(await launcher.RunScriptAsync(scriptRoot, new LaunchSettings { ExecutionMode = "BuildOnly" }, messages.Add, CancellationToken.None) == 0 && messages.Any(line => line.Contains("BuildOnly mode requested")), "Legacy BuildOnly exits without starting backend/frontend");
            Check(fixture.LastCaptureLimit == 4 * 1024 * 1024, "Application sessions request bounded native output capture");
            fixture.BuildExitCode = 7;
            Check(await launcher.RunScriptAsync(scriptRoot, new LaunchSettings { ExecutionMode = "BuildAndRun" }, _ => { }, CancellationToken.None) == 7, "Failed build stops legacy launch with original exit code");
            Check(File.ReadAllText(Path.Combine(scriptRoot, "run.ps1")) == source, "Launcher retains original run.ps1 byte content");
            fixture.BuildExitCode = 0;
            fixture.FailFrontend = true;
            var backendOutput = Path.Combine(scriptRoot, "artifacts", "bin", "EngineUpgraded", "debug");
            var frontendOutput = Path.Combine(scriptRoot, "artifacts", "bin", "BeaconAvaClient", "debug");
            Directory.CreateDirectory(backendOutput); Directory.CreateDirectory(frontendOutput);
            File.WriteAllText(Path.Combine(backendOutput, "SheetKraftWeb.dll"), "fixture");
            File.WriteAllText(Path.Combine(frontendOutput, "AvaClient.Desktop.exe"), "fixture");
            File.WriteAllText(Path.Combine(frontendOutput, "BeaconAvaClient.pages.json"), "{}");
            int backendPid = -1;
            var listen = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listen.Start();
            fixture.Port = ((System.Net.IPEndPoint)listen.LocalEndpoint).Port; listen.Stop();
            var failure = await launcher.RunScriptAsync(scriptRoot, new LaunchSettings { ExecutionMode = "RunOnly", Urls = "http://127.0.0.1:" + fixture.Port }, line =>
            { if (line.StartsWith("BEACON_BACKEND_PID:")) int.TryParse(line["BEACON_BACKEND_PID:".Length..], out backendPid); }, CancellationToken.None);
            Check(failure != 0 && backendPid > 0, "Frontend startup failure is reported after a tracked backend launch");
            bool backendAlive; try { using var process = Process.GetProcessById(backendPid); backendAlive = !process.HasExited; } catch (ArgumentException) { backendAlive = false; }
            Check(!backendAlive, "Frontend startup failure cleans up the backend");
            listen.Start();
            int occupiedPort = ((System.Net.IPEndPoint)listen.LocalEndpoint).Port;
            fixture.FailFrontend = false;
            var occupiedMessages = new List<string>();
            try
            {
                Check(await launcher.RunScriptAsync(scriptRoot, new LaunchSettings { ExecutionMode = "RunOnly", Urls = "http://127.0.0.1:" + occupiedPort }, occupiedMessages.Add, CancellationToken.None) != 0 && !occupiedMessages.Any(line => line.StartsWith("BEACON_BACKEND_PID:")), "An occupied backend port prevents launching another session");
            }
            finally { listen.Stop(); }
        }
        else Console.WriteLine("SKIP: actual Beacon legacy script is not present.");
        // Eligibility and identity invalidation do not require a WPF window or modal dialogs.
        var scanService = new PausedPrerequisites();
        using (var scanModel = new MainViewModel(scanService, git, new DatabaseService(), config, new TestLog(), new FakeAdmin(), new AppLauncherService(fake, new TestLog()), fake, new FakeSettings()))
        {
            Check(scanModel.Prerequisites.Count == 7 && scanModel.Prerequisites.All(item => item.Status == ValidationStatus.Pending), "All dependency cards exist before scanning");
            var scan = scanModel.RefreshPrerequisitesAsync();
            await scanService.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(scanModel.IsBusy && scanModel.Prerequisites[0].Status == ValidationStatus.Installed && scanModel.Prerequisites[1].Status == ValidationStatus.Running && scanModel.Prerequisites.Skip(2).All(item => item.Status == ValidationStatus.Pending), "Dependency results stream while remaining checks are waiting");
            Check(scanModel.ProgressValue > 0 && scanModel.ProgressValue < 100, "Dependency progress advances before scan completion");
            scanModel.CancelOperation();
            await scan;
            Check(!scanModel.IsBusy && scanModel.Prerequisites[0].Status == ValidationStatus.Installed && scanModel.Prerequisites.Skip(1).All(item => item.Status == ValidationStatus.Pending), "Cancelled scan retains completed cards and clears active checking state");
            scanService.Paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
            scanService.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
            scan = scanModel.RefreshPrerequisitesAsync();
            await scanService.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            scanService.Continue.SetResult();
            await scan;
            Check(scanModel.Prerequisites.Count == 7 && scanModel.Prerequisites.All(item => item.Status == ValidationStatus.Installed) && scanModel.ProgressValue == 100, "Rescan finishes all existing dependency cards without duplicates");
        }
        using var vm = new MainViewModel(new FakePrerequisites(), git, new DatabaseService(), config, new TestLog(), new FakeAdmin(), new AppLauncherService(fake, new TestLog()), fake, new FakeSettings());
        vm.SelectedAction = "Build"; vm.WorkspaceValidationStatus = WorkspaceValidationStatus.Success; vm.RepoValidationStatus = "Failed";
        Check(vm.CanExecuteAction, "Local Build eligibility does not require remote connectivity");
        vm.SelectedAction = "RunApplication"; vm.WorkspaceValidationStatus = WorkspaceValidationStatus.Success;
        Check(vm.CanExecuteAction, "Local Run eligibility does not require remote connectivity");
        vm.SelectedAction = "Setup";
        Check(!vm.IsWorkspaceValidated, "Changing action invalidates workspace eligibility");
        vm.SetupSteps[0].Status = StepStatus.Completed; vm.Branch = "another";
        Check(vm.SetupSteps.All(step => step.Status == StepStatus.Pending), "Changing source resets completed workflow steps");
        vm.BuildStatusText = "✗ Failed";
        Check(!vm.CanContinueToDbSetup, "Failed build cannot continue to database setup");
        string before = vm.Branch; vm.IsBusy = true; vm.Branch = "mutation-during-operation";
        Check(vm.Branch == before, "Running operation inputs cannot be changed midway");
        vm.IsBusy = false;
        string ownershipWorkspace = Path.Combine(Scratch, "ownership-workspace"); Directory.CreateDirectory(Path.Combine(ownershipWorkspace, ".git"));
        fake.Response = args => FakeRunner.Result(1, "", "fatal: detected dubious ownership in repository");
        vm.SelectedAction = "Repair"; vm.LocalPath = ownershipWorkspace; vm.RepoValidationStatus = "Success";
        await vm.ValidateWorkspaceAsync();
        Check(vm.CanExecuteAction && vm.WorkspaceValidationStatus == WorkspaceValidationStatus.Warning, "Ownership warning allows explicit recovery instead of blocking the recovery UI");
    }
    private static void VerifyUi()
    {
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("/BeaconSetupManager;component/Themes/DarkTheme.xaml", UriKind.Relative) });
        var log = new TestLog(); var runner = new FakeRunner();
        using var model = new MainViewModel(new FakePrerequisites(), new GitService(runner, log), new DatabaseService(), new ConfigService(), log, new FakeAdmin(), new AppLauncherService(runner, log), runner, new FakeSettings());
        var window = new BeaconSetupManager.MainWindow(model, log);
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        var password = (System.Windows.Controls.PasswordBox)window.FindName("AppDbPasswordBox");
        password.Password = "fixture-password";
        Check(model.DbPassword == "fixture-password", "Masked password input updates an initially empty view-model password");
        model.DbPassword = "changed-fixture";
        Check(password.Password == "changed-fixture", "Password binding also updates from the view model");
        foreach (string theme in new[] { "Light", "Dark" })
        {
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("/BeaconSetupManager;component/Themes/" + theme + "Theme.xaml", UriKind.Relative) });
            foreach (string key in new[] { "WarningAlertBackgroundBrush", "WarningAlertBorderBrush", "WarningAlertTextBrush" })
                Check(window.TryFindResource(key) is System.Windows.Media.Brush, theme + " theme resolves " + key);
        }
        var recovery = new BeaconSetupManager.RepoRecoveryDialog(new RepositoryAnalysisResult { State = RepositoryState.SubmodulesUninitialized }, allowStartFresh: false);
        recovery.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var startFresh = (System.Windows.Controls.Button)recovery.FindName("StartFreshButton");
        Check(startFresh.Visibility == System.Windows.Visibility.Collapsed, "Update Branch recovery does not offer destructive Start Fresh");
        typeof(BeaconSetupManager.RepoRecoveryDialog).GetMethod("StartFresh_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(recovery, new object[] { startFresh, new System.Windows.RoutedEventArgs() });
        Check(recovery.Result == RepoRecoveryAction.Cancel, "A disabled Start Fresh handler cannot request workspace deletion");
        recovery.Close();
        var setupRecovery = new BeaconSetupManager.RepoRecoveryDialog(new RepositoryAnalysisResult { State = RepositoryState.SubmodulesUninitialized });
        setupRecovery.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(((System.Windows.Controls.Button)setupRecovery.FindName("StartFreshButton")).Visibility == System.Windows.Visibility.Visible,
            "Setup retains its explicitly confirmed Start Fresh action");
        setupRecovery.Close();

        string retryPath = Path.Combine(Scratch, "retry-build-workspace");
        Directory.CreateDirectory(Path.Combine(retryPath, ".git"));
        File.WriteAllText(Path.Combine(retryPath, "run.ps1"), "# fixture");
        File.WriteAllText(Path.Combine(retryPath, "Beacon.sln"), "fixture");
        var commands = new List<string>();
        var retryRunner = new FakeRunner { Response = args => { commands.Add(args); return FakeRunner.Result(0, args.Contains("rev-parse HEAD") ? new string('a', 40) : "true"); } };
        var retryLauncher = new RetryLauncher();
        var retrySettings = new LaunchSettingsService();
        retrySettings.Save(retryPath, new LaunchSettings { ExecutionMode = "RunOnly", Configuration = "Release" });
        using var retryModel = new MainViewModel(new FakePrerequisites(), new GitService(retryRunner, log), new DatabaseService(), new ConfigService(), log,
            new FakeAdmin(), retryLauncher, retryRunner, retrySettings);
        retryModel.SelectedAction = "UpdateBranch";
        retryModel.LocalPath = retryPath;
        var confirmDialogs = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        confirmDialogs.Tick += (_, _) =>
        {
            foreach (var dialog in app.Windows.OfType<BeaconSetupManager.LaunchConfigurationDialog>().Where(dialog => dialog.IsVisible).ToArray())
                typeof(BeaconSetupManager.LaunchConfigurationDialog).GetMethod("Build_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(dialog, new object[] { dialog, new System.Windows.RoutedEventArgs() });
        };
        confirmDialogs.Start();
        try
        {
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            app.MainWindow = window;
            RunUiTask(retryModel.ConfirmBuildAsync);
            Check(retryLauncher.Calls == 1 && retryModel.CanRetryExecution && !retryModel.IsBusy, "Cancelled optional build offers retry and releases operation state");
            commands.Clear();
            RunUiTask(retryModel.RetryExecutionAsync);
            Check(retryLauncher.Calls == 2 && retryModel.ExecutionSuccess && retryModel.SelectedAction == "UpdateBranch" &&
                !commands.Any(command => command.Contains("fetch") || command.Contains("checkout") || command.Contains("pull")),
                "Retry after optional build restarts the build without fetching or checking out the branch");
            Check(retryModel.IsRunApplicationPromptVisible && retrySettings.Load(retryPath).ExecutionMode == "RunOnly" && retrySettings.Load(retryPath).Configuration == "Release",
                "Retried optional build preserves run preferences and offers to run the application");
            retryLauncher.CancelNext = true;
            RunUiTask(retryModel.ConfirmBuildAsync);
            retryModel.Branch = "changed-after-cancellation";
            RunUiTask(retryModel.RetryExecutionAsync);
            Check(retryLauncher.Calls == 3 && !retryModel.CanRetryExecution, "Changing source invalidates a captured build retry");
            retryModel.LocalPath = Path.Combine(Scratch, "missing-build-workspace");
            RunUiTask(retryModel.ConfirmBuildAsync);
            Check(retryLauncher.Calls == 3 && !retryModel.IsBusy, "Optional build revalidates its workspace before launching");
        }
        finally { confirmDialogs.Stop(); }
        window.Close(); app.Shutdown();
    }

    private static void RunUiTask(Func<Task> action)
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var frame = new System.Windows.Threading.DispatcherFrame();
        Exception? failure = null;
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await action(); }
            catch (Exception ex) { failure = ex; }
            finally { frame.Continue = false; }
        }));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        if (failure != null) throw new Exception("UI workflow regression failed.", failure);
    }
}

internal sealed class RetryLauncher : IAppLauncherService
{
    public int Calls { get; private set; }
    public bool CancelNext { get; set; } = true;
    public Task<int> RunScriptAsync(string repoRoot, LaunchSettings settings, Action<string> onOutputLine, CancellationToken cancellationToken)
    {
        Calls++;
        if (CancelNext) { CancelNext = false; throw new OperationCanceledException(); }
        onOutputLine("BEACON_BUILD_EXIT:0");
        return Task.FromResult(0);
    }
}

internal sealed class TestLog : ILogService
{
    public event Action<LogMessage>? OnMessageLogged { add { } remove { } }
    public void LogInfo(string category, string message) { }
    public void LogWarning(string category, string message) { }
    public void LogError(string category, string message, Exception? exception = null) { }
    public void OpenLogFolder() { }
}
internal class FakeRunner : IProcessRunner
{
    public Func<string, ProcessResult> Response { get; set; } = _ => Result(0);
    public static ProcessResult Result(int code, string output = "", string error = "") => new(code, output, error, TimeSpan.Zero, false, ProcessExecutionContext.CurrentUser, -1);
    public virtual Task<ProcessResult> RunAsync(string fileName, string arguments, string workingDirectory = "", Action<string>? onOutputLine = null, CancellationToken cancellationToken = default, string? stdInput = null, Dictionary<string, string>? environmentVariables = null, ProcessExecutionContext context = ProcessExecutionContext.CurrentUser, int? maximumCapturedOutputCharacters = null) => Task.FromResult(Response(arguments));
    public bool StartBackground(string fileName, string arguments, string workingDirectory = "", ProcessExecutionContext context = ProcessExecutionContext.CurrentUser) => false;
    public void EnsureDirectoryExists(string path) => Directory.CreateDirectory(path);
    public bool FixDirectoryOwnership(string path) => false;
}
internal sealed class LocalRunner(ProcessRunner runner) : FakeRunner
{
    public override Task<ProcessResult> RunAsync(string fileName, string arguments, string workingDirectory = "", Action<string>? onOutputLine = null, CancellationToken cancellationToken = default, string? stdInput = null, Dictionary<string, string>? environmentVariables = null, ProcessExecutionContext context = ProcessExecutionContext.CurrentUser, int? maximumCapturedOutputCharacters = null)
        => runner.RunAsync(fileName, arguments, workingDirectory, onOutputLine, cancellationToken, stdInput, environmentVariables, ProcessExecutionContext.Elevated, maximumCapturedOutputCharacters);
}
internal sealed class ScriptFixtureRunner(ProcessRunner runner, int buildExitCode) : FakeRunner
{
    public int BuildExitCode { get; set; } = buildExitCode;
    public int? LastCaptureLimit { get; private set; }
    public bool FailFrontend { get; set; }
    public int Port { get; set; }
    public override Task<ProcessResult> RunAsync(string fileName, string arguments, string workingDirectory = "", Action<string>? onOutputLine = null, CancellationToken cancellationToken = default, string? stdInput = null, Dictionary<string, string>? environmentVariables = null, ProcessExecutionContext context = ProcessExecutionContext.CurrentUser, int? maximumCapturedOutputCharacters = null)
    {
        if (environmentVariables?.ContainsKey("BEACON_LAUNCH_JSON") == true)
        {
            LastCaptureLimit = maximumCapturedOutputCharacters;
            string command = Encoding.Unicode.GetString(Convert.FromBase64String(arguments.Split(' ').Last()));
            command = "function dotnet { $global:LASTEXITCODE=" + BuildExitCode + "; Write-Host 'fake build' }; " + command;
            if (FailFrontend)
            {
                string listener = "$l=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback," + Port + "); $l.Start(); Start-Sleep -Seconds 30";
                string listenerArgs = CommandArguments.PowerShellLiteral(CommandArguments.EncodedPowerShell(listener));
                command = "function Start-Process { param($FilePath,$ArgumentList,$WorkingDirectory,$WindowStyle,[switch]$PassThru); if ($FilePath -eq 'dotnet') { Microsoft.PowerShell.Management\\Start-Process powershell.exe -ArgumentList " + listenerArgs + " -WindowStyle Hidden -PassThru } else { throw 'Intentional fixture frontend failure' } }; " + command;
            }
            arguments = CommandArguments.EncodedPowerShell(command);
        }
        return runner.RunAsync(fileName, arguments, workingDirectory, onOutputLine, cancellationToken, stdInput, environmentVariables, ProcessExecutionContext.Elevated, maximumCapturedOutputCharacters);
    }
}
internal class FakePrerequisites : IPrerequisiteService
{
    public virtual Task<List<PrerequisiteItem>> ValidateAllAsync(Action<PrerequisiteItem>? onItemChecked = null, Action<string>? onProgress = null, CancellationToken cancellationToken = default) => Task.FromResult(new List<PrerequisiteItem>());
    public Task<(bool Success, string ErrorMessage)> ValidateGitAccessAsync(string repoUrl) => Task.FromResult((true, ""));
    public Task<bool> ApproveGitCredentialsAsync(string repoUrl, string username, string password) => Task.FromResult(true);
}
internal sealed class PausedPrerequisites : FakePrerequisites
{
    public TaskCompletionSource Paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override async Task<List<PrerequisiteItem>> ValidateAllAsync(Action<PrerequisiteItem>? onItemChecked = null, Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var items = PrerequisiteItem.CreatePendingItems();
        items[0].Status = ValidationStatus.Installed;
        onItemChecked?.Invoke(items[0]);
        items[1].Status = ValidationStatus.Running;
        onItemChecked?.Invoke(items[1]);
        Paused.SetResult();
        await Continue.Task.WaitAsync(cancellationToken);
        foreach (var item in items.Skip(1)) { item.Status = ValidationStatus.Installed; onItemChecked?.Invoke(item); }
        return items;
    }
}
internal sealed class FakeAdmin : IAdminService { public bool IsRunningAsAdmin() => false; public void RestartAsAdminAndExit() { } }
internal sealed class FakeSettings : ILaunchSettingsService { public LaunchSettings Load(string path) => new(); public void Save(string path, LaunchSettings settings) { } }
