using System.Diagnostics;

namespace OkCloud.Client.Services
{
    /// <summary>
    /// Simplified background service that runs sync operations
    /// Works without complex hosting dependencies
    /// </summary>
    public class OkCloudBackgroundService
    {
        private Timer? _syncTimer;
        private FileWatcherService? _fileWatcher;
        private SyncService? _syncService;
        private ApiService? _apiService;
        private LocalDatabaseService? _localDb;
        private bool _isRunning = false;
        
        // Sync every 2 minutes when running in background
        private const int BACKGROUND_SYNC_INTERVAL_MINUTES = 2;

        public OkCloudBackgroundService()
        {
        }

        public async Task StartAsync(SyncService syncService, ApiService apiService, LocalDatabaseService localDb, FileWatcherService fileWatcher)
        {
            Debug.WriteLine("🚀 OkCloud Background Service starting (Simplified)...");
            
            try
            {
                // Set services
                _syncService = syncService;
                _apiService = apiService;
                _localDb = localDb;
                _fileWatcher = fileWatcher;
                
                // Start file watching
                await StartFileWatchingAsync();
                
                // Start periodic sync
                StartPeriodicSync();
                
                _isRunning = true;
                Debug.WriteLine("✅ OkCloud Background Service started successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error in background service: {ex.Message}");
            }
        }

        public async Task StopAsync()
        {
            Debug.WriteLine("🛑 OkCloud Background Service stopping...");
            
            await CleanupAsync();
            _isRunning = false;
            
            Debug.WriteLine("✅ OkCloud Background Service stopped");
        }

        private async Task StartFileWatchingAsync()
        {
            if (_fileWatcher != null)
            {
                await _fileWatcher.StartWatchingAsync();
                Debug.WriteLine("👁️ File watching started");
            }
        }

        private void StartPeriodicSync()
        {
            _syncTimer = new Timer(async _ =>
            {
                try
                {
                    Debug.WriteLine("⏰ Background sync triggered");
                    
                    if (_syncService != null)
                    {
                        // Check if sync is configured
                        var syncStatus = await _syncService.GetSyncStatusAsync();
                        if (syncStatus.IsConfigured && !syncStatus.IsSyncing)
                        {
                            await _syncService.TriggerManualSyncAsync();
                            Debug.WriteLine("✅ Background sync completed");
                        }
                        else if (!syncStatus.IsConfigured)
                        {
                            Debug.WriteLine("⚠️ Sync not configured, skipping background sync");
                        }
                        else
                        {
                            Debug.WriteLine("⏭️ Sync already in progress, skipping");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"❌ Background sync error: {ex.Message}");
                }
            }, null, TimeSpan.Zero, TimeSpan.FromMinutes(BACKGROUND_SYNC_INTERVAL_MINUTES));
            
            Debug.WriteLine($"⏰ Periodic sync started (every {BACKGROUND_SYNC_INTERVAL_MINUTES} minutes)");
        }

        private async Task PerformHealthCheckAsync()
        {
            try
            {
                // Check if file watcher is still running
                if (_fileWatcher != null && !_fileWatcher.IsWatching)
                {
                    Debug.WriteLine("⚠️ File watcher stopped, restarting...");
                    await _fileWatcher.StartWatchingAsync();
                }
                
                // Check API connectivity
                if (_apiService != null)
                {
                    var isValid = await _apiService.ValidateTokenAsync();
                    if (!isValid)
                    {
                        Debug.WriteLine("⚠️ API token invalid, background sync may fail");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Health check failed: {ex.Message}");
            }
        }

        private async Task CleanupAsync()
        {
            Debug.WriteLine("🧹 Cleaning up background service...");
            
            _syncTimer?.Dispose();
            _fileWatcher?.StopWatching();
            
            Debug.WriteLine("✅ Background service cleanup completed");
        }
    }
}