using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BeaconSetupManager.Models;
using BeaconSetupManager.Services;
using BeaconSetupManager.ViewModels;

internal static class UxReview
{
    public static void Run(string rootPath)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BeaconSetupManager;component/Themes/LightTheme.xaml", UriKind.Relative) });
        string output = Path.Combine(rootPath, "artifacts", "validation", "ux-review");
        Directory.CreateDirectory(output);
        var runner = new FakeRunner(); var log = new TestLog();
        using var model = new MainViewModel(new FakePrerequisites(), new GitService(runner, log), new DatabaseService(), new ConfigService(), log,
            new FakeAdmin(), new AppLauncherService(runner, log), runner, new FakeSettings());
        var window = new BeaconSetupManager.MainWindow(model, log);
        foreach (var item in model.Prerequisites) { item.Status = ValidationStatus.Installed; item.DetectedVersion = "Installed"; }
        var notes = new List<string>();
        foreach (string theme in new[] { "Light", "Dark" })
        {
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BeaconSetupManager;component/Themes/" + theme + "Theme.xaml", UriKind.Relative) });
            foreach (var size in new[] { new Size(800, 540), new Size(900, 620), new Size(1340, 720), new Size(1880, 1000), new Size(800, 460) })
            foreach (string screen in new[] { "prerequisites", "actions", "repository", "run-settings", "progress", "build-results", "database" })
            {
                model.SelectedAction = screen == "run-settings" ? "RunApplication" : "Setup";
                model.IsBusy = false; model.IsDbSetupFormVisible = false; model.IsBuildResultsVisible = false; model.IsBuildPromptVisible = false;
                model.IsRunApplicationPromptVisible = false; model.IsExecutionFinished = false;
                model.CurrentStep = screen switch { "prerequisites" => 1, "actions" => 2, "repository" or "run-settings" => 3, _ => 4 };
                if (screen == "progress") { model.IsBusy = true; model.BusyMessage = "Updating submodules at their recorded commits…"; model.ProgressValue = 50; }
                if (screen == "build-results") { model.IsBuildResultsVisible = true; model.IsExecutionFinished = true; model.ExecutionSuccess = true; model.BuildStatusText = "✓ Succeeded"; model.BuildOutputText = "Build succeeded.\n0 Warning(s)\n0 Error(s)"; model.ProgressValue = 100; }
                if (screen == "database") { model.IsDbSetupFormVisible = true; model.SelectedDatabaseEngine = DatabaseEngine.SqlServer; model.ConnectToExistingDatabase = false; }
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Capture(window, size, Path.Combine(output, $"{theme}-{screen}-{size.Width}x{size.Height}.png"));
                notes.Add($"Rendered {theme} {screen} {size.Width}x{size.Height}");
            }
            model.IsBusy = false;
            foreach (string action in new[] { "Setup", "Build", "Run" })
            {
                var dialog = new BeaconSetupManager.LaunchConfigurationDialog(new LaunchSettings(), action);
                Capture(dialog, new Size(540, 520), Path.Combine(output, $"{theme}-launch-{action}.png"));
                foreach (var expander in Descendants((DependencyObject)dialog.Content).OfType<Expander>()) expander.IsExpanded = true;
                Capture(dialog, new Size(540, 380), Path.Combine(output, $"{theme}-launch-{action}-short-advanced.png")); dialog.Close();
            }
            foreach (var state in new[] { RepositoryState.UnsafeRepository, RepositoryState.LocalModifications, RepositoryState.SubmodulesUninitialized })
            {
                var dialog = new BeaconSetupManager.RepoRecoveryDialog(new RepositoryAnalysisResult { State = state });
                Capture(dialog, new Size(760, 460), Path.Combine(output, $"{theme}-recovery-{state}.png")); dialog.Close();
            }
            var conflict = new BeaconSetupManager.GitConflictResolutionDialog("Local changes would be overwritten by checkout.");
            Capture(conflict, new Size(680, 360), Path.Combine(output, $"{theme}-conflict.png")); conflict.Close();
        }
        File.WriteAllLines(Path.Combine(output, "rendered-screens.txt"), notes);
        window.Close(); app.Shutdown();
        Console.WriteLine("Rendered UX review screens to " + output);
    }

    private static void Capture(Window window, Size size, string file)
    {
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var root = (FrameworkElement)window.Content;
        root.Measure(size); root.Arrange(new Rect(new Point(), size)); root.UpdateLayout();
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(new Point(), size));
        }
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file); encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}
