using System.Diagnostics;
using OkCloud.Client.Services;

namespace OkCloud.Client
{
    public partial class SyncSetupPage : ContentPage
    {
        private readonly SyncService _syncService;
        private string? _selectedPath;

        public SyncSetupPage()
        {
            InitializeComponent();
            _syncService = Application.Current?.Handler?.MauiContext?.Services.GetService<SyncService>()!;
            
            // Set default path
            var defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Ok Cloud"
            );
            _selectedPath = defaultPath;
            SyncPathEntry.Text = defaultPath;
            PathStatusLabel.IsVisible = true;
            StartSyncButton.IsEnabled = true;
        }

        private async void OnChooseFolderClicked(object sender, EventArgs e)
        {
            try
            {
                // Prompt user to enter a custom path
                var customPath = await DisplayPromptAsync(
                    "Choose Sync Folder",
                    "Enter the full path where you want to sync files:",
                    initialValue: _selectedPath,
                    maxLength: 500,
                    keyboard: Keyboard.Default,
                    placeholder: @"C:\Users\YourName\Ok Cloud"
                );

                if (!string.IsNullOrWhiteSpace(customPath))
                {
                    // Validate the path
                    try
                    {
                        // Try to create the directory if it doesn't exist
                        if (!Directory.Exists(customPath))
                        {
                            var createFolder = await DisplayAlert(
                                "Folder doesn't exist",
                                $"The folder '{customPath}' doesn't exist. Do you want to create it?",
                                "Yes",
                                "No"
                            );

                            if (createFolder)
                            {
                                Directory.CreateDirectory(customPath);
                            }
                            else
                            {
                                return;
                            }
                        }

                        // Test write permissions
                        var testFile = Path.Combine(customPath, ".okcloud_test");
                        File.WriteAllText(testFile, "test");
                        File.Delete(testFile);

                        _selectedPath = customPath;
                        SyncPathEntry.Text = _selectedPath;
                        PathStatusLabel.IsVisible = true;
                        StartSyncButton.IsEnabled = true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        await DisplayAlert("Error", "You don't have permission to write to this folder. Please choose another location.", "OK");
                    }
                    catch (Exception ex)
                    {
                        await DisplayAlert("Error", $"Invalid folder path: {ex.Message}", "OK");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error choosing folder: {ex.Message}");
                await DisplayAlert("Error", "Failed to select folder", "OK");
            }
        }

        private async void OnStartSyncClicked(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedPath))
            {
                await DisplayAlert("Error", "Please select a sync folder", "OK");
                return;
            }

            // Save sync path
            await _syncService.SetSyncFolderPathAsync(_selectedPath);

            // Show syncing UI
            StepChooseFolder.IsVisible = false;
            StepSyncing.IsVisible = true;

            // Subscribe to sync events
            _syncService.OnSyncProgress += OnSyncProgress;
            _syncService.OnSyncComplete += OnSyncComplete;
            _syncService.OnSyncError += OnSyncError;

            // Start sync
            await _syncService.StartInitialSyncAsync();
        }

        private void OnSyncProgress(SyncProgress progress)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                SyncStageLabel.Text = progress.Stage;
                ProgressPercentLabel.Text = $"{progress.Percentage}%";
                SyncProgressBar.Progress = progress.Percentage / 100.0;
                
                if (!string.IsNullOrEmpty(progress.CurrentFile))
                {
                    CurrentFileLabel.Text = progress.CurrentFile;
                }
            });
        }

        private async void OnSyncComplete()
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                // Start FileWatcher
                var fileWatcher = Application.Current?.Handler?.MauiContext?.Services.GetService<FileWatcherService>();
                if (fileWatcher != null)
                {
                    await fileWatcher.StartWatchingAsync();
                    Debug.WriteLine("✅ FileWatcher started after sync complete");
                }
                
                // Close the sync setup page
                await Navigation.PopModalAsync();
                
                // Trigger the Home page to load files
                await Services.AppBridge.SaveTokenAndNotify(
                    await Microsoft.Maui.Storage.SecureStorage.Default.GetAsync("auth_token") ?? "",
                    await Microsoft.Maui.Storage.SecureStorage.Default.GetAsync("auth_cookies") ?? ""
                );
            });
        }

        private void OnSyncError(string error)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await DisplayAlert("Sync Error", error, "OK");
                StepSyncing.IsVisible = false;
                StepChooseFolder.IsVisible = true;
            });
        }

        private void OnDocumentsFolderClicked(object sender, EventArgs e)
        {
            var documentsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Ok Cloud"
            );
            _selectedPath = documentsPath;
            SyncPathEntry.Text = documentsPath;
            PathStatusLabel.IsVisible = true;
            StartSyncButton.IsEnabled = true;
        }

        private void OnDesktopFolderClicked(object sender, EventArgs e)
        {
            var desktopPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "Ok Cloud"
            );
            _selectedPath = desktopPath;
            SyncPathEntry.Text = desktopPath;
            PathStatusLabel.IsVisible = true;
            StartSyncButton.IsEnabled = true;
        }

        private void OnHomeFolderClicked(object sender, EventArgs e)
        {
            var homePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Ok Cloud"
            );
            _selectedPath = homePath;
            SyncPathEntry.Text = homePath;
            PathStatusLabel.IsVisible = true;
            StartSyncButton.IsEnabled = true;
        }

        private async void OnBackClicked(object sender, EventArgs e)
        {
            await Navigation.PopModalAsync();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            
            // Unsubscribe from events
            _syncService.OnSyncProgress -= OnSyncProgress;
            _syncService.OnSyncComplete -= OnSyncComplete;
            _syncService.OnSyncError -= OnSyncError;
        }
    }
}
