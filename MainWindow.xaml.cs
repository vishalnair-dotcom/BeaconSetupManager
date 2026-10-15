using System;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using BeaconSetupManager.Services;
using BeaconSetupManager.ViewModels;

namespace BeaconSetupManager
{
    public partial class MainWindow : Window
    {
        private readonly ILogService _logService;

        public MainWindow(MainViewModel viewModel, ILogService logService)
        {
            InitializeComponent();
            DataContext = viewModel;
            _logService = logService;

            // Subscribe to real-time logs
            _logService.OnMessageLogged += LogService_OnMessageLogged;

            // Clear logs on request
            viewModel.OnLogCleared += () =>
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(() => LogRichTextBox.Document.Blocks.Clear()));
                }
                else
                {
                    LogRichTextBox.Document.Blocks.Clear();
                }
            };

            // Auto-refresh prerequisites on load
            Loaded += async (s, e) =>
            {
                await viewModel.RefreshPrerequisitesAsync();
            };
            Closing += (_, _) => viewModel.CancelOperation();
            Closed += (_, _) => { viewModel.Dispose(); _logService.OnMessageLogged -= LogService_OnMessageLogged; };
        }

        private void LogService_OnMessageLogged(LogMessage log)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => LogService_OnMessageLogged(log)));
                return;
            }

            var paragraph = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };

            // Timestamp
            var timeRun = new Run($"[{log.Timestamp:HH:mm:ss}] ");
            timeRun.SetResourceReference(TextElement.ForegroundProperty, "MutedTextBrush");
            paragraph.Inlines.Add(timeRun);

            // Category
            var catRun = new Run($"[{log.Category}] ") { FontWeight = FontWeights.Bold };
            catRun.SetResourceReference(TextElement.ForegroundProperty, "SecondaryTextBrush");
            paragraph.Inlines.Add(catRun);

            // Message
            var msgRun = new Run(log.Message);
            if (log.IsError)
            {
                msgRun.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444")); // Red 500
                msgRun.FontWeight = FontWeights.SemiBold;
            }
            else if (log.IsWarning)
            {
                msgRun.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B")); // Amber 500
            }
            else
            {
                msgRun.SetResourceReference(TextElement.ForegroundProperty, "ConsoleForegroundBrush");
            }
            paragraph.Inlines.Add(msgRun);

            LogRichTextBox.Document.Blocks.Add(paragraph);
            while (LogRichTextBox.Document.Blocks.Count > 2000)
                LogRichTextBox.Document.Blocks.Remove(LogRichTextBox.Document.Blocks.FirstBlock);

            // Auto scroll to bottom
            LogRichTextBox.ScrollToEnd();
        }

        private void BrowseLocalPath_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Select Local Target Folder"
                };

                if (!string.IsNullOrEmpty(vm.LocalPath) && Directory.Exists(vm.LocalPath))
                {
                    dialog.InitialDirectory = vm.LocalPath;
                }
                else
                {
                    dialog.InitialDirectory = Directory.Exists(@"C:\") ? @"C:\" : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                }

                if (dialog.ShowDialog() == true)
                {
                    vm.LocalPath = dialog.FolderName;
                }
            }
        }

        private void LocalPath_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                if (sender is System.Windows.Controls.TextBox textBox)
                {
                    var expression = textBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty);
                    expression?.UpdateSource();
                }
            }
        }
    }
}
