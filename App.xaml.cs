using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using BeaconSetupManager.Services;
using BeaconSetupManager.ViewModels;

namespace BeaconSetupManager
{
    public partial class App : Application
    {
        public static IServiceProvider ServiceProvider { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. Setup DI
            var services = new ServiceCollection();
            ConfigureServices(services);
            ServiceProvider = services.BuildServiceProvider();

            // 2. Check Admin Rights
            var adminService = ServiceProvider.GetRequiredService<IAdminService>();
            if (!adminService.IsRunningAsAdmin())
            {
                var result = MessageBox.Show(
                    "Beacon Setup Manager can run in either Administrator Mode or Standard User Mode.\n\n" +
                    "Administrator Mode is recommended to enable all features, including prerequisite checks and other administrator-level operations.\n\n" +
                    "How would you like to continue?",
                    "Run as Administrator?",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    adminService.RestartAsAdminAndExit();
                    return;
                }
                else if (result == MessageBoxResult.Cancel)
                {
                    Shutdown();
                    return;
                }
            }

            // 3. Start Main Window
            var savedTheme = LoadSavedTheme();
            ChangeTheme(savedTheme);

            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
            var viewModel = ServiceProvider.GetRequiredService<MainViewModel>();
            viewModel.SelectedTheme = savedTheme;

            mainWindow.Show();
        }

        private static readonly string ThemeFilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QuantumPhinance",
            "theme.txt");

        private string LoadSavedTheme()
        {
            try
            {
                if (System.IO.File.Exists(ThemeFilePath))
                {
                    string content = System.IO.File.ReadAllText(ThemeFilePath).Trim();
                    if (content == "Light" || content == "Dark")
                    {
                        return content;
                    }
                }
            }
            catch
            {
                // Fallback to default Dark
            }
            return "Dark"; // Default
        }

        public void SaveTheme(string themeName)
        {
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(ThemeFilePath);
                if (dir != null && !System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }
                string temporary = ThemeFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { System.IO.File.WriteAllText(temporary, themeName); System.IO.File.Move(temporary, ThemeFilePath, true); }
                finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
            }
            catch (Exception ex)
            {
                ServiceProvider.GetService<ILogService>()?.LogWarning("Theme", "Could not save theme: " + ex.Message);
            }
        }

        public void ChangeTheme(string themeName)
        {
            try
            {
                if (themeName is not ("Light" or "Dark")) throw new ArgumentException("Unknown theme.");
                var newDict = new ResourceDictionary { Source = new Uri($"Themes/{themeName}Theme.xaml", UriKind.Relative) };
                // Clear old theme dictionary
                var themeDict = Resources.MergedDictionaries
                    .FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains("Theme.xaml"));

                if (themeDict != null)
                {
                    Resources.MergedDictionaries.Remove(themeDict);
                }

                // Load new theme
                Resources.MergedDictionaries.Add(newDict);
                
                SaveTheme(themeName);
            }
            catch (Exception ex)
            {
                ServiceProvider.GetService<ILogService>()?.LogWarning("Theme", "Could not change theme: " + ex.Message);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            (ServiceProvider as IDisposable)?.Dispose();
            Serilog.Log.CloseAndFlush();
            base.OnExit(e);
        }

        private void ConfigureServices(IServiceCollection services)
        {
            // Services
            services.AddSingleton<IProcessRunner, ProcessRunner>();
            services.AddSingleton<IInteractiveProcessLauncher, WindowsUserProcessLauncher>();
            services.AddSingleton<IPrerequisiteService, PrerequisiteService>();
            services.AddSingleton<IGitService, GitService>();
            services.AddSingleton<IDatabaseService, DatabaseService>();
            services.AddSingleton<IConfigService, ConfigService>();
            services.AddSingleton<IAdminService, AdminService>();
            services.AddSingleton<ILogService, LogService>();
            services.AddSingleton<IAppLauncherService, AppLauncherService>();
            services.AddSingleton<ILaunchSettingsService, LaunchSettingsService>();

            // ViewModels
            services.AddSingleton<MainViewModel>();

            // Views
            services.AddSingleton<MainWindow>();
        }
    }
}
