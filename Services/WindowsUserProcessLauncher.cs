using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BeaconSetupManager.Services
{
    public class InteractiveSessionException : Exception
    {
        public int NativeErrorCode { get; }
        public InteractiveSessionException(string message, int nativeErrorCode) : base(message)
        {
            NativeErrorCode = nativeErrorCode;
        }
    }

    public class WindowsUserProcessLauncher : IInteractiveProcessLauncher
    {
        private readonly ILogService _logService;

        public WindowsUserProcessLauncher(ILogService logService)
        {
            _logService = logService;
        }

        #region Win32 API Definitions

        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool DuplicateTokenEx(
            IntPtr hExistingToken,
            uint dwDesiredAccess,
            ref SECURITY_ATTRIBUTES lpTokenAttributes,
            int ImpersonationLevel,
            int TokenType,
            out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessWithTokenW(
            IntPtr hToken,
            uint dwLogonFlags,
            string? lpApplicationName,
            StringBuilder lpCommandLine,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
            ref STARTUPINFO startup, out PROCESS_INFORMATION process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DuplicateHandle(
            IntPtr hSourceProcessHandle,
            IntPtr hSourceHandle,
            IntPtr hTargetProcessHandle,
            out IntPtr lpTargetHandle,
            uint dwDesiredAccess,
            bool bInheritHandle,
            uint dwOptions);

        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint TOKEN_DUPLICATE = 0x0002;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint TOKEN_ALL_ACCESS = 0x000F01FF;
        
        private const int STARTF_USESTDHANDLES = 0x00000100;
        private const int STARTF_USESHOWWINDOW = 0x00000001;
        private const short SW_HIDE = 0;
        
        private const uint CREATE_NO_WINDOW = 0x08000000;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const uint CREATE_NEW_CONSOLE = 0x00000010;
        
        private const uint DUPLICATE_SAME_ACCESS = 2;
        private const uint INFINITE = 0xFFFFFFFF;
        private const uint WAIT_OBJECT_0 = 0x00000000;

        #endregion

        private bool IsRunningAsAdmin()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public IntPtr GetInteractiveUserToken()
        {
            int explorerPid = -1;
            int currentSessionId = Process.GetCurrentProcess().SessionId;
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                if (p.SessionId == currentSessionId)
                {
                    explorerPid = p.Id;
                    break;
                }
            }

            if (explorerPid == -1)
            {
                _logService.LogWarning("Execution Context", "No explorer.exe process found for the active interactive user session.");
                throw new InteractiveSessionException("Active interactive user session explorer.exe process not found.", -1);
            }

            IntPtr hProcess = OpenProcess(PROCESS_QUERY_INFORMATION, false, explorerPid);
            if (hProcess == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                _logService.LogWarning("Execution Context", $"Failed to open explorer.exe process (PID: {explorerPid}). Win32 error: {error}");
                throw new InteractiveSessionException("Failed to open interactive user process.", error);
            }

            if (!OpenProcessToken(hProcess, TOKEN_DUPLICATE | TOKEN_QUERY, out IntPtr hToken))
            {
                int error = Marshal.GetLastWin32Error();
                CloseHandle(hProcess);
                _logService.LogWarning("Execution Context", $"Failed to open process token for explorer.exe. Win32 error: {error}");
                throw new InteractiveSessionException("Failed to open interactive user process token.", error);
            }

            var sa = new SECURITY_ATTRIBUTES();
            sa.nLength = Marshal.SizeOf(sa);
            sa.bInheritHandle = true;

            // SecurityImpersonation = 2, TokenPrimary = 1
            if (!DuplicateTokenEx(hToken, TOKEN_ALL_ACCESS, ref sa, 2, 1, out IntPtr hTokenDup))
            {
                int error = Marshal.GetLastWin32Error();
                CloseHandle(hToken);
                CloseHandle(hProcess);
                _logService.LogWarning("Execution Context", $"Failed to duplicate process token for explorer.exe. Win32 error: {error}");
                throw new InteractiveSessionException("Failed to duplicate interactive user process token.", error);
            }

            CloseHandle(hToken);
            CloseHandle(hProcess);
            using (var current = WindowsIdentity.GetCurrent())
            using (var interactive = new WindowsIdentity(hTokenDup))
            {
                if (current.User != interactive.User)
                {
                    CloseHandle(hTokenDup);
                    throw new InteractiveSessionException("Run the manager using the signed-in Windows account. Elevation using a different administrator account cannot share that user's settings and private launch files safely.", -1);
                }
            }
            return hTokenDup;
        }

        private static IntPtr CreateEnvironmentBlock(Dictionary<string, string>? customVars)
        {
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry de in Environment.GetEnvironmentVariables())
            {
                string? key = de.Key?.ToString();
                string? val = de.Value?.ToString();
                if (key != null && val != null)
                {
                    env[key] = val;
                }
            }
            if (customVars != null)
            {
                foreach (var kvp in customVars)
                {
                    env[kvp.Key] = kvp.Value;
                }
            }
            env["GIT_TERMINAL_PROMPT"] = "0";

            var sb = new StringBuilder();
            foreach (var kvp in env.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(kvp.Key).Append('=').Append(kvp.Value).Append('\0');
            }
            sb.Append('\0');

            string envStr = sb.ToString();
            return Marshal.StringToHGlobalUni(envStr);
        }

        public Task<ProcessResult> RunAsCurrentUserAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            Action<string>? onOutputLine,
            CancellationToken cancellationToken,
            string? stdInput,
            Dictionary<string, string>? environmentVariables,
            int? maximumCapturedOutputCharacters = null)
            => RunNativeAsync(fileName, arguments, workingDirectory, onOutputLine, cancellationToken, stdInput,
                environmentVariables, IsRunningAsAdmin(), ProcessExecutionContext.CurrentUser, maximumCapturedOutputCharacters);

        public Task<ProcessResult> RunAsManagerAsync(string fileName, string arguments, string workingDirectory,
            Action<string>? onOutputLine, CancellationToken cancellationToken, string? stdInput, Dictionary<string, string>? environmentVariables,
            int? maximumCapturedOutputCharacters = null)
            => RunNativeAsync(fileName, arguments, workingDirectory, onOutputLine, cancellationToken, stdInput,
                environmentVariables, false, ProcessExecutionContext.Elevated, maximumCapturedOutputCharacters);

        private async Task<ProcessResult> RunNativeAsync(string fileName, string arguments, string workingDirectory,
            Action<string>? onOutputLine, CancellationToken cancellationToken, string? stdInput,
            Dictionary<string, string>? environmentVariables, bool interactiveToken, ProcessExecutionContext executionContext,
            int? maximumCapturedOutputCharacters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stdoutBuilder = new CapturedProcessOutput(maximumCapturedOutputCharacters);
            var stderrBuilder = new CapturedProcessOutput(maximumCapturedOutputCharacters);
            var transported = PowerShellCommandTransport.Prepare(fileName, arguments, environmentVariables);
            arguments = transported.Arguments;
            environmentVariables = transported.Environment;
            if (interactiveToken && fileName.Length + arguments.Length + 4 >= 1024)
                throw new ArgumentException("The command exceeds the 1,024-character limit for launching as the signed-in user from Administrator Mode.");
            var stopwatch = Stopwatch.StartNew();

            IntPtr hUserToken = IntPtr.Zero;
            IntPtr lpEnvironment = IntPtr.Zero;
            PROCESS_INFORMATION pi = default;

            // Pipe streams for redirection
            AnonymousPipeServerStream? outPipe = null;
            AnonymousPipeServerStream? errPipe = null;
            AnonymousPipeServerStream? inPipe = null;

            IntPtr inheritableOutWrite = IntPtr.Zero;
            IntPtr inheritableErrWrite = IntPtr.Zero;
            IntPtr inheritableInRead = IntPtr.Zero;
            ProcessJob? job = null;
            Task? readers = null;
            var outputGate = new object();
            Exception? outputError = null;

            try
            {
                if (interactiveToken) hUserToken = GetInteractiveUserToken();

                // Setup anonymous pipes
                outPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                errPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

                IntPtr currentProcessHandle = Process.GetCurrentProcess().Handle;
                
                // Duplicate client write handles to make sure they are inheritable
                if (!DuplicateHandle(currentProcessHandle, outPipe.ClientSafePipeHandle.DangerousGetHandle(), currentProcessHandle, out inheritableOutWrite, 0, true, DUPLICATE_SAME_ACCESS))
                {
                    throw new InteractiveSessionException("Failed to duplicate output pipe handle.", Marshal.GetLastWin32Error());
                }
                if (!DuplicateHandle(currentProcessHandle, errPipe.ClientSafePipeHandle.DangerousGetHandle(), currentProcessHandle, out inheritableErrWrite, 0, true, DUPLICATE_SAME_ACCESS))
                {
                    throw new InteractiveSessionException("Failed to duplicate error pipe handle.", Marshal.GetLastWin32Error());
                }

                if (stdInput != null)
                {
                    inPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
                    if (!DuplicateHandle(currentProcessHandle, inPipe.ClientSafePipeHandle.DangerousGetHandle(), currentProcessHandle, out inheritableInRead, 0, true, DUPLICATE_SAME_ACCESS))
                    {
                        throw new InteractiveSessionException("Failed to duplicate input pipe handle.", Marshal.GetLastWin32Error());
                    }
                }

                // Setup startup info
                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                si.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
                si.wShowWindow = SW_HIDE;
                si.hStdOutput = inheritableOutWrite;
                si.hStdError = inheritableErrWrite;
                si.hStdInput = stdInput != null ? inheritableInRead : IntPtr.Zero;

                // Setup environment variables
                lpEnvironment = CreateEnvironmentBlock(environmentVariables);

                // Setup working dir
                string? currentDir = string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory;

                // Format command line
                var commandLine = new StringBuilder($"\"{fileName}\" {arguments}");

                uint creationFlags = CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | 0x4; // suspended until the job owns it

                // Launch process
                bool success = interactiveToken ? CreateProcessWithTokenW(
                    hUserToken,
                    0,
                    null,
                    commandLine,
                    creationFlags,
                    lpEnvironment,
                    currentDir,
                    ref si,
                    out pi) : CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, true, creationFlags,
                        lpEnvironment, currentDir, ref si, out pi);

                if (!success)
                {
                    int error = Marshal.GetLastWin32Error();
                    string nativeMessage = new System.ComponentModel.Win32Exception(error).Message;
                    throw new System.ComponentModel.Win32Exception(error, $"Could not start '{fileName}' (Win32 {error}: {nativeMessage}).");
                }

                job = new ProcessJob(pi.hProcess);
                if (ResumeThread(pi.hThread) == uint.MaxValue)
                    throw new InteractiveSessionException("Could not resume the tracked command.", Marshal.GetLastWin32Error());

                // Close local write copies of pipes immediately so parent reads receive EOF properly
                outPipe.DisposeLocalCopyOfClientHandle();
                errPipe.DisposeLocalCopyOfClientHandle();
                if (inPipe != null)
                {
                    inPipe.DisposeLocalCopyOfClientHandle();
                }

                // Close native duplicated handles in the parent process as they are now duplicated inside the child
                if (inheritableOutWrite != IntPtr.Zero) { CloseHandle(inheritableOutWrite); inheritableOutWrite = IntPtr.Zero; }
                if (inheritableErrWrite != IntPtr.Zero) { CloseHandle(inheritableErrWrite); inheritableErrWrite = IntPtr.Zero; }
                if (inheritableInRead != IntPtr.Zero) { CloseHandle(inheritableInRead); inheritableInRead = IntPtr.Zero; }

                // Output readers
                var stdoutTask = Task.Run(async () =>
                {
                    using var reader = new StreamReader(outPipe, Encoding.UTF8);
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        stdoutBuilder.AppendLine(line);
                        lock (outputGate) { try { onOutputLine?.Invoke(line); } catch (Exception ex) { outputError ??= ex; } }
                    }
                });

                var stderrTask = Task.Run(async () =>
                {
                    using var reader = new StreamReader(errPipe, Encoding.UTF8);
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        stderrBuilder.AppendLine(line);
                        lock (outputGate) { try { onOutputLine?.Invoke(line); } catch (Exception ex) { outputError ??= ex; } }
                    }
                });

                readers = Task.WhenAll(stdoutTask, stderrTask);

                using var registration = cancellationToken.Register(() =>
                {
                    try { job.Terminate(); TerminateProcess(pi.hProcess, 999); } catch { }
                });

                // Write stdin if provided
                if (stdInput != null && inPipe != null)
                {
                    using (var writer = new StreamWriter(inPipe, new UTF8Encoding(false)))
                    {
                        await writer.WriteAsync(stdInput.AsMemory(), cancellationToken);
                        await writer.FlushAsync(cancellationToken);
                    }
                    inPipe.Dispose();
                }

                // Wait for exit
                await Task.Run(() => WaitForSingleObject(pi.hProcess, INFINITE));
                GetExitCodeProcess(pi.hProcess, out uint rawExitCode);

                job.Terminate();
                await readers;
                int exitCode = (int)rawExitCode;
                cancellationToken.ThrowIfCancellationRequested();
                if (outputError != null) throw new InvalidOperationException("Could not process command output.", outputError);

                stopwatch.Stop();
                return new ProcessResult(exitCode, stdoutBuilder.ToString(), stderrBuilder.ToString(), stopwatch.Elapsed, false, executionContext, pi.dwProcessId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                int errCode = ex is InteractiveSessionException ise ? ise.NativeErrorCode : ex is System.ComponentModel.Win32Exception native ? native.NativeErrorCode : -1;
                _logService.LogError("Execution Context", $"Interactive user process launching failed: {ex.Message}. Win32 error code: {errCode}", ex);
                throw;
            }
            finally
            {
                job?.Dispose();
                if (pi.hProcess != IntPtr.Zero && readers == null) TerminateProcess(pi.hProcess, 999);
                if (readers != null) { try { await readers; } catch (IOException) { } }
                if (inheritableOutWrite != IntPtr.Zero) CloseHandle(inheritableOutWrite);
                if (inheritableErrWrite != IntPtr.Zero) CloseHandle(inheritableErrWrite);
                if (inheritableInRead != IntPtr.Zero) CloseHandle(inheritableInRead);
                // Cleanup handles
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                if (hUserToken != IntPtr.Zero) CloseHandle(hUserToken);
                if (lpEnvironment != IntPtr.Zero) Marshal.FreeHGlobal(lpEnvironment);

                outPipe?.Dispose();
                errPipe?.Dispose();
                inPipe?.Dispose();
            }
        }

        public bool StartAsCurrentUser(
            string fileName,
            string arguments,
            string workingDirectory,
            bool createNoWindow,
            bool useShellExecute)
        {
            if (!IsRunningAsAdmin())
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    CreateNoWindow = createNoWindow,
                    UseShellExecute = useShellExecute
                };
                try
                {
                    Process.Start(psi);
                    return true;
                }
                catch (Exception ex)
                {
                    _logService.LogError("Execution Context", $"Failed to start standard background process '{fileName}': {ex.Message}", ex);
                    return false;
                }
            }

            IntPtr hUserToken = IntPtr.Zero;
            PROCESS_INFORMATION pi = default;

            try
            {
                hUserToken = GetInteractiveUserToken();

                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                si.dwFlags = STARTF_USESHOWWINDOW;
                si.wShowWindow = createNoWindow ? (short)0 : (short)1; // SW_SHOWNORMAL = 1

                string? currentDir = string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory;
                var commandLine = new StringBuilder($"\"{fileName}\" {arguments}");

                uint creationFlags = CREATE_NEW_CONSOLE | CREATE_UNICODE_ENVIRONMENT;
                if (createNoWindow)
                {
                    creationFlags |= CREATE_NO_WINDOW;
                }

                IntPtr lpEnvironment = CreateEnvironmentBlock(null);

                bool success = CreateProcessWithTokenW(
                    hUserToken,
                    0,
                    null,
                    commandLine,
                    creationFlags,
                    lpEnvironment,
                    currentDir,
                    ref si,
                    out pi);

                if (lpEnvironment != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(lpEnvironment);
                }

                if (!success)
                {
                    int error = Marshal.GetLastWin32Error();
                    _logService.LogError("Execution Context", $"CreateProcessWithTokenW failed for background process '{fileName}'. Win32 error code: {error}", null);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                int errCode = (ex is InteractiveSessionException ise) ? ise.NativeErrorCode : -1;
                _logService.LogError("Execution Context", $"Background process launch failed: {ex.Message}. Win32 error: {errCode}", ex);
                return false;
            }
            finally
            {
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                if (hUserToken != IntPtr.Zero) CloseHandle(hUserToken);
            }
        }
    }
}

