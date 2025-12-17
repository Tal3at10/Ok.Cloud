using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace OkCloud.Client.Services
{
    /// <summary>
    /// System Tray service that provides background sync control
    /// Uses Windows Shell API directly for maximum compatibility
    /// </summary>
    public class SystemTrayService : IDisposable
    {
        private readonly ILogger<SystemTrayService> _logger;
        private readonly SyncService _syncService;
        private readonly ApiService _apiService;
        
        private bool _disposed = false;
        private bool _isInitialized = false;

        public SystemTrayService(
            ILogger<SystemTrayService> logger,
            SyncService syncService,
            ApiService apiService)
        {
            _logger = logger;
            _syncService = syncService;
            _apiService = apiService;
        }

        public async Task InitializeAsync()
        {
            try
            {
                _logger.LogInformation("🎯 Initializing System Tray (Simplified)...");
                
                // Set up sync event handlers
                SetupSyncEventHandlers();
                
                // For now, we'll use a simplified approach without actual tray icon
                // This avoids the Windows Forms dependency issue
                _isInitialized = true;
                
                _logger.LogInformation("✅ System Tray initialized (Simplified mode)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to initialize System Tray");
            }
        }

        private void SetupSyncEventHandlers()
        {
            if (_syncService != null)
            {
                _syncService.OnSyncProgress += OnSyncProgress;
                _syncService.OnSyncComplete += OnSyncComplete;
                _syncService.OnSyncError += OnSyncError;
            }
        }

        private void OnSyncProgress(SyncProgress progress)
        {
            if (_isInitialized)
            {
                _logger.LogInformation($"📊 Sync Progress: {progress.Percentage}% - {progress.Stage}");
                
                // Show Windows notification for major progress milestones
                if (progress.Percentage == 0)
                {
                    ShowWindowsNotification("OK Cloud", "Sync started");
                }
            }
        }

        private void OnSyncComplete()
        {
            if (_isInitialized)
            {
                _logger.LogInformation("✅ Sync completed successfully");
                ShowWindowsNotification("OK Cloud", "Sync completed successfully");
            }
        }

        private void OnSyncError(string error)
        {
            if (_isInitialized)
            {
                _logger.LogError($"❌ Sync error: {error}");
                ShowWindowsNotification("OK Cloud Error", error);
            }
        }

        private void ShowWindowsNotification(string title, string message)
        {
            try
            {
                // Use Windows toast notifications
                var toastXml = $@"
                <toast>
                    <visual>
                        <binding template='ToastGeneric'>
                            <text>{title}</text>
                            <text>{message}</text>
                        </binding>
                    </visual>
                </toast>";
                
                // For now, just log the notification
                // In a full implementation, we'd use Windows.UI.Notifications
                _logger.LogInformation($"🔔 Notification: {title} - {message}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to show notification");
            }
        }

        private async Task OnSyncNowClicked()
        {
            try
            {
                _logger.LogInformation("🔄 Manual sync requested from tray");
                
                if (_syncService != null)
                {
                    var syncStatus = await _syncService.GetSyncStatusAsync();
                    if (syncStatus.IsConfigured)
                    {
                        if (!syncStatus.IsSyncing)
                        {
                            await _syncService.TriggerManualSyncAsync();
                        }
                        else
                        {
                            ShowWindowsNotification("OK Cloud", "Sync is already in progress.");
                        }
                    }
                    else
                    {
                        ShowWindowsNotification("OK Cloud", "Sync is not configured. Please open the main application to set up sync.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error during manual sync");
                ShowWindowsNotification("OK Cloud Error", $"Sync failed: {ex.Message}");
            }
        }

        private async Task OnOpenSyncFolderClicked()
        {
            try
            {
                if (_syncService != null)
                {
                    var syncPath = await _syncService.GetSyncFolderPathAsync();
                    if (!string.IsNullOrEmpty(syncPath) && Directory.Exists(syncPath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"\"{syncPath}\"",
                            UseShellExecute = true
                        });
                    }
                    else
                    {
                        ShowWindowsNotification("OK Cloud", "Sync folder not found or not configured.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error opening sync folder");
            }
        }

        private void OnOpenWebClicked()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://cloud.oksite.se",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error opening web app");
            }
        }

        private void OnOpenMainAppClicked()
        {
            try
            {
                // Try to bring existing app to front, or start new instance
                var currentProcess = Process.GetCurrentProcess();
                var processes = Process.GetProcessesByName(currentProcess.ProcessName);
                
                foreach (var process in processes)
                {
                    if (process.Id != currentProcess.Id)
                    {
                        // Found another instance, bring it to front
                        ShowWindow(process.MainWindowHandle, SW_RESTORE);
                        SetForegroundWindow(process.MainWindowHandle);
                        return;
                    }
                }
                
                // No other instance found, start new one
                var appPath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(appPath))
                {
                    Process.Start(appPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error opening main app");
            }
        }

        private void OnSettingsClicked()
        {
            // Open main app and navigate to settings
            OnOpenMainAppClicked();
        }

        private void OnExitClicked()
        {
            try
            {
                _logger.LogInformation("🛑 Exit requested from tray");
                
                // For now, just exit - we can add confirmation dialog later
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error during exit");
            }
        }

        // Windows API imports for window management
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        
        private const int SW_RESTORE = 9;

        public void Dispose()
        {
            if (!_disposed)
            {
                _logger.LogInformation("🧹 Disposing System Tray...");
                
                if (_syncService != null)
                {
                    _syncService.OnSyncProgress -= OnSyncProgress;
                    _syncService.OnSyncComplete -= OnSyncComplete;
                    _syncService.OnSyncError -= OnSyncError;
                }
                
                _isInitialized = false;
                
                _disposed = true;
                _logger.LogInformation("✅ System Tray disposed");
            }
        }
    }
}