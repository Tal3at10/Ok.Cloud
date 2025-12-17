using System.Diagnostics;
using OkCloud.Client.Models;

namespace OkCloud.Client.Services
{
    public class OfflineManager
    {
        private readonly ApiService _apiService;
        private readonly LocalDatabaseService _localDb;
        private readonly SyncService _syncService;
        
        public bool IsOffline { get; private set; }
        public event Action<bool>? OnConnectivityChanged;

        public OfflineManager(ApiService apiService, LocalDatabaseService localDb, SyncService syncService)
        {
            _apiService = apiService;
            _localDb = localDb;
            _syncService = syncService;
            
            // Monitor connectivity
            Connectivity.ConnectivityChanged += Connectivity_ConnectivityChanged;
            CheckConnectivity();
        }

        private void Connectivity_ConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
        {
            CheckConnectivity();
        }

        private void CheckConnectivity()
        {
            var wasOffline = IsOffline;
            IsOffline = Connectivity.Current.NetworkAccess != NetworkAccess.Internet;
            
            if (wasOffline != IsOffline)
            {
                Debug.WriteLine($"🌐 Connectivity changed: {(IsOffline ? "OFFLINE" : "ONLINE")}");
                OnConnectivityChanged?.Invoke(IsOffline);
                
                // If back online, trigger sync
                if (!IsOffline)
                {
                    _ = Task.Run(async () => await SyncWhenOnlineAsync());
                }
            }
        }

        // Get files (online or offline)
        public async Task<List<FileEntry>> GetFilesAsync(int? parentId = null)
        {
            if (IsOffline)
            {
                Debug.WriteLine("📴 OFFLINE MODE: Loading from local database");
                var localFiles = await _localDb.GetFilesByParentAsync(parentId);
                return localFiles.Select(f => f.ToFileEntry()).ToList();
            }
            else
            {
                try
                {
                    Debug.WriteLine("🌐 ONLINE MODE: Loading from server");
                    var files = parentId.HasValue 
                        ? await _apiService.GetFolderContentsAsync(parentId.Value)
                        : await _apiService.GetFilesAsync();
                    
                    // Save to local database
                    await SaveFilesToLocalAsync(files);
                    return files;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"⚠️ API failed, falling back to offline: {ex.Message}");
                    IsOffline = true;
                    var localFiles = await _localDb.GetFilesByParentAsync(parentId);
                    return localFiles.Select(f => f.ToFileEntry()).ToList();
                }
            }
        }

        // Get starred files
        public async Task<List<FileEntry>> GetStarredFilesAsync()
        {
            if (IsOffline)
            {
                var localFiles = await _localDb.GetStarredFilesAsync();
                return localFiles.Select(f => f.ToFileEntry()).ToList();
            }
            else
            {
                try
                {
                    var files = await _apiService.GetStarredFilesAsync();
                    await SaveFilesToLocalAsync(files);
                    return files;
                }
                catch
                {
                    var localFiles = await _localDb.GetStarredFilesAsync();
                    return localFiles.Select(f => f.ToFileEntry()).ToList();
                }
            }
        }

        // Search files
        public async Task<List<FileEntry>> SearchFilesAsync(string query)
        {
            if (IsOffline)
            {
                var localFiles = await _localDb.SearchFilesAsync(query);
                return localFiles.Select(f => f.ToFileEntry()).ToList();
            }
            else
            {
                try
                {
                    var files = await _apiService.SearchFilesAsync(query);
                    return files;
                }
                catch
                {
                    var localFiles = await _localDb.SearchFilesAsync(query);
                    return localFiles.Select(f => f.ToFileEntry()).ToList();
                }
            }
        }

        // Save files to local database
        private async Task SaveFilesToLocalAsync(List<FileEntry> files)
        {
            var syncPath = await _syncService.GetSyncFolderPathAsync();
            if (string.IsNullOrEmpty(syncPath))
                return;

            var localFiles = files.Select(f =>
            {
                var localPath = f.Type == "folder" 
                    ? Path.Combine(syncPath, f.Name)
                    : Path.Combine(syncPath, f.Name);
                return LocalFileEntry.FromFileEntry(f, localPath);
            }).ToList();

            await _localDb.SaveFilesAsync(localFiles);
            Debug.WriteLine($"💾 Saved {localFiles.Count} files to local database");
        }

        // Sync when back online
        private async Task SyncWhenOnlineAsync()
        {
            try
            {
                Debug.WriteLine("🔄 Back online - syncing...");
                
                // First, process any queued operations
                await LoadQueueAsync();
                await ProcessQueueAsync();
                
                // Then refresh data from server
                var files = await _apiService.GetFilesAsync();
                await SaveFilesToLocalAsync(files);
                Debug.WriteLine("✅ Sync completed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Sync failed: {ex.Message}");
            }
        }

        // Queue operation for later (when offline)
        private readonly List<OfflineOperation> _operationQueue = new();
        
        public async Task QueueOperationAsync(OfflineOperation operation)
        {
            _operationQueue.Add(operation);
            Debug.WriteLine($"📝 Queued operation: {operation.Type} - {operation.FileName}");
            
            // Save to persistent storage (could use Preferences or a file)
            await SaveQueueAsync();
        }

        private async Task SaveQueueAsync()
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(_operationQueue);
                await SecureStorage.Default.SetAsync("offline_queue", json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Failed to save queue: {ex.Message}");
            }
        }

        private async Task LoadQueueAsync()
        {
            try
            {
                var json = await SecureStorage.Default.GetAsync("offline_queue");
                if (!string.IsNullOrEmpty(json))
                {
                    var queue = System.Text.Json.JsonSerializer.Deserialize<List<OfflineOperation>>(json);
                    if (queue != null)
                    {
                        _operationQueue.Clear();
                        _operationQueue.AddRange(queue);
                        Debug.WriteLine($"📥 Loaded {queue.Count} queued operations");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Failed to load queue: {ex.Message}");
            }
        }

        private async Task ProcessQueueAsync()
        {
            if (_operationQueue.Count == 0)
                return;

            Debug.WriteLine($"🔄 Processing {_operationQueue.Count} queued operations...");
            
            var processed = new List<OfflineOperation>();
            
            foreach (var operation in _operationQueue.ToList())
            {
                try
                {
                    bool success = operation.Type switch
                    {
                        OperationType.Upload => await ProcessUploadAsync(operation),
                        OperationType.Delete => await ProcessDeleteAsync(operation),
                        OperationType.Rename => await ProcessRenameAsync(operation),
                        OperationType.CreateFolder => await ProcessCreateFolderAsync(operation),
                        _ => false
                    };

                    if (success)
                    {
                        processed.Add(operation);
                        Debug.WriteLine($"✅ Processed: {operation.Type} - {operation.FileName}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"❌ Failed to process {operation.Type}: {ex.Message}");
                }
            }

            // Remove processed operations
            foreach (var op in processed)
            {
                _operationQueue.Remove(op);
            }

            await SaveQueueAsync();
            Debug.WriteLine($"✅ Queue processed. {_operationQueue.Count} remaining.");
        }

        private async Task<bool> ProcessUploadAsync(OfflineOperation operation)
        {
            if (string.IsNullOrEmpty(operation.FilePath) || !File.Exists(operation.FilePath))
                return false;

            var result = await _apiService.UploadFileAsync(operation.FilePath, operation.ParentId);
            return result != null;
        }

        private async Task<bool> ProcessDeleteAsync(OfflineOperation operation)
        {
            if (!operation.FileId.HasValue)
                return false;

            return await _apiService.DeleteFileAsync(operation.FileId.Value);
        }

        private async Task<bool> ProcessRenameAsync(OfflineOperation operation)
        {
            if (!operation.FileId.HasValue || string.IsNullOrEmpty(operation.NewName))
                return false;

            return await _apiService.RenameFileAsync(operation.FileId.Value, operation.NewName);
        }

        private async Task<bool> ProcessCreateFolderAsync(OfflineOperation operation)
        {
            if (string.IsNullOrEmpty(operation.FileName))
                return false;

            var result = await _apiService.CreateFolderAsync(operation.FileName, operation.ParentId);
            return result != null;
        }

        // Get offline status info
        public async Task<OfflineStatus> GetStatusAsync()
        {
            var fileCount = await _localDb.GetFileCountAsync();
            var syncPath = await _syncService.GetSyncFolderPathAsync();
            
            return new OfflineStatus
            {
                IsOffline = IsOffline,
                LocalFileCount = fileCount,
                SyncFolderPath = syncPath,
                LastChecked = DateTime.Now,
                PendingOperations = _operationQueue.Count
            };
        }
    }

    public class OfflineStatus
    {
        public bool IsOffline { get; set; }
        public int LocalFileCount { get; set; }
        public string? SyncFolderPath { get; set; }
        public DateTime LastChecked { get; set; }
        public int PendingOperations { get; set; }
    }
}
