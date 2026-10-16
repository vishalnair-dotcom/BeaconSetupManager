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
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;

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
                    if (content is "Light" or "Dark" or "System")
                    {
                        return content;
                    }
                }
            }
            catch
            {
                // Follow Windows when no saved preference is available.
            }
            return "System";
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
                if (themeName is not ("Light" or "Dark" or "System")) throw new ArgumentException("Unknown theme.");
                string effectiveTheme = themeName;
                if (themeName == "System")
                {
                    effectiveTheme = "Light";
                    try
                    {
                        if (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int light && light == 0)
                            effectiveTheme = "Dark";
                    }
                    catch { /* Use light when the Windows preference is unavailable. */ }
                }
                var newDict = new ResourceDictionary { Source = new Uri($"Themes/{effectiveTheme}Theme.xaml", UriKind.Relative) };
                // Clear old theme dictionary
                var themeDict = Resources.MergedDictionaries
                    .FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains("Theme.xaml"));

                if (themeDict != null)
                {
                    Resources.MergedDictionaries[Resources.MergedDictionaries.IndexOf(themeDict)] = newDict;
                }

                // Load new theme
                else Resources.MergedDictionaries.Add(newDict);
                
                SaveTheme(themeName);
            }
            catch (Exception ex)
            {
                ServiceProvider.GetService<ILogService>()?.LogWarning("Theme", "Could not change theme: " + ex.Message);
            }
        }

        private void OnSystemPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ServiceProvider.GetService<MainViewModel>()?.SelectedTheme == "System") ChangeTheme("System");
            }));
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
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
            services.AddSingleton<ISqlServerDatabaseService, SqlServerDatabaseService>();
            services.AddSingleton<IDatabaseConnectionVerifier, DatabaseConnectionVerifier>();
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
