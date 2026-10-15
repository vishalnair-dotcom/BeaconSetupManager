using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace BeaconSetupManager.Services
{
    public class AdminService : IAdminService
    {
        public bool IsRunningAsAdmin()
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

        public void RestartAsAdminAndExit()
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                exePath = Process.GetCurrentProcess().MainModule?.FileName;
            }

            if (string.IsNullOrEmpty(exePath))
            {
                MessageBox.Show("Unable to determine application executable path for restart.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            };

            try
            {
                Process.Start(startInfo);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                // Can happen if the user clicks No on the UAC prompt
                MessageBox.Show($"Elevation failed: {ex.Message}. The application will now exit.", "Administrator Rights Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                Application.Current.Shutdown();
            }
        }
    }
}
