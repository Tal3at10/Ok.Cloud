using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Maui.Storage;
using OkCloud.Client.Models;

namespace OkCloud.Client.Services
{
    public class SyncService
    {
        private readonly ApiService _apiService;
        private LocalDatabaseService? _localDb;
        private string? _syncFolderPath;
        private bool _isSyncing = false;
        private CancellationTokenSource? _syncCancellation;
        private Timer? _periodicSyncTimer;
        
        // Track files downloaded in current sync session to prevent re-uploading them
        private HashSet<string> _downloadedInCurrentSync = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // Track when files were downloaded with timestamps
        private Dictionary<string, DateTime> _downloadedFilesWithTime = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public event Action<SyncProgress>? OnSyncProgress;
        public event Action<string>? OnSyncError;
        public event Action? OnSyncComplete;

        public bool IsSyncing => _isSyncing;
        
        // Periodic sync interval (default: 5 minutes)
        private const int SYNC_INTERVAL_MINUTES = 5;

        public SyncService(ApiService apiService)
        {
            _apiService = apiService;
        }

        public void SetLocalDatabase(LocalDatabaseService localDb)
        {
            _localDb = localDb;
        }

        public async Task<string?> GetSyncFolderPathAsync()
        {
            if (_syncFolderPath == null)
            {
                var basePath = await SecureStorage.Default.GetAsync("sync_folder_path");
                if (!string.IsNullOrEmpty(basePath))
                {
                    // Get current workspace info
                    var currentWorkspaceId = ApiService.CurrentWorkspaceId;
                    var workspaceFolderName = GetWorkspaceName(currentWorkspaceId);
                    
                    // Create workspace-specific path
                    _syncFolderPath = Path.Combine(basePath, workspaceFolderName);
                    Debug.WriteLine($"📁 Workspace-specific sync path: {_syncFolderPath} (Workspace: {workspaceFolderName}, ID: {currentWorkspaceId})");
                    
                    // Migration: Check for old folder naming (without ID prefix)
                    await MigrateOldFolderStructureAsync(basePath, currentWorkspaceId, workspaceFolderName);
                }
            }
            return _syncFolderPath;
        }
        
        /// <summary>
        /// Migrate old folder structure to new workspace-aware structure
        /// CRITICAL: Workspace is NOT a folder - it's a context for files
        /// </summary>
        private async Task MigrateOldFolderStructureAsync(string basePath, int workspaceId, string newFolderName)
        {
            try
            {
                // Skip if new folder already exists
                var newFolderPath = Path.Combine(basePath, newFolderName);
                if (Directory.Exists(newFolderPath))
                {
                    // Set icon for existing folder (in case it wasn't set before)
                    await SetFolderIconAsync(newFolderPath);
                    return;
                }
                
                // CRITICAL FIX: Look for old sync folders that might contain workspace content
                var possibleOldPaths = new[]
                {
                    Path.Combine(basePath, "Default"),
                    Path.Combine(basePath, "0_Default"), 
                    Path.Combine(basePath, $"Workspace_{workspaceId}"),
                    Path.Combine(basePath, $"{workspaceId}_Default"),
                    basePath // Check if files are directly in base path
                };
                
                foreach (var oldPath in possibleOldPaths)
                {
                    if (Directory.Exists(oldPath) && oldPath != newFolderPath)
                    {
                        // Check if this folder has files (indicating it's a sync folder)
                        var hasFiles = Directory.GetFiles(oldPath, "*", SearchOption.AllDirectories).Length > 0;
                        
                        if (hasFiles)
                        {
                            Debug.WriteLine($"🔄 Migrating workspace sync folder");
                            Debug.WriteLine($"   From: {Path.GetFileName(oldPath)}");
                            Debug.WriteLine($"   To: {newFolderName}");
                            
                            Directory.Move(oldPath, newFolderPath);
                            Debug.WriteLine($"✅ Migration successful");
                            
                            // Set custom icon for migrated folder
                            await SetFolderIconAsync(newFolderPath);
                            return;
                        }
                    }
                }
                
                Debug.WriteLine($"ℹ️ No existing sync folder found for workspace {workspaceId}, will create new one");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Failed to migrate old folder structure: {ex.Message}");
            }
        }

        private string GetWorkspaceName(int workspaceId)
        {
            // CRITICAL FIX: Workspace is NOT a folder to sync, it's a context
            // The sync folder should represent the ROOT of the workspace content
            // So we use simple naming without workspace prefix
            
            if (workspaceId == 0)
                return "Default_Workspace";
            
            // Try to get workspace name from API
            try
            {
                // For now, use a simple approach since GetWorkspacesAsync doesn't exist yet
                // We'll implement workspace API calls later if needed
                return $"Workspace_{workspaceId}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠️ Failed to get workspace name for ID {workspaceId}: {ex.Message}");
            }
            
            // Fallback to ID only
            return $"Workspace_{workspaceId}";
        }
        
        private string SanitizeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Workspace";
            
            // Remove invalid characters and replace with underscore
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = string.Join("_", name.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
            
            // Trim and limit length
            sanitized = sanitized.Trim().Substring(0, Math.Min(sanitized.Length, 50));
            
            return string.IsNullOrWhiteSpace(sanitized) ? "Workspace" : sanitized;
        }

        public async Task SetSyncFolderPathAsync(string path)
        {
            // Store the base path (without workspace subfolder)
            await SecureStorage.Default.SetAsync("sync_folder_path", path);
            
            // CRITICAL: Also save for Windows Service compatibility in a shared location
            await SaveForWindowsServiceAsync("sync_folder", path);
            
            // Reset cached path so it gets recalculated with current workspace
            _syncFolderPath = null;
        }
        
        /// <summary>
        /// Save data in a location accessible by Windows Service
        /// </summary>
        private async Task SaveForWindowsServiceAsync(string key, string value)
        {
            try
            {
                // CRITICAL: Use ProgramData (accessible by SYSTEM account)
                var sharedFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "OkCloud",
                    "SecureStorage"
                );
                
                if (!Directory.Exists(sharedFolder))
                {
                    Directory.CreateDirectory(sharedFolder);
                    Debug.WriteLine($"📁 Created shared storage: {sharedFolder}");
                }
                
                var filePath = Path.Combine(sharedFolder, $"{key}.dat");
                
                // CRITICAL: Use LocalMachine scope (accessible by SYSTEM)
                var data = System.Text.Encoding.UTF8.GetBytes(value);
                var encryptedData = System.Security.Cryptography.ProtectedData.Protect(
                    data, 
                    null, 
                    System.Security.Cryptography.DataProtectionScope.LocalMachine
                );
                
                await File.WriteAllBytesAsync(filePath, encryptedData);
                Debug.WriteLine($"💾 Saved {key} for Windows Service at: {filePath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠️ Failed to save for Windows Service: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Set custom icon for a folder (Windows only)
        /// </summary>
        public async Task SetFolderIconAsync(string folderPath)
        {
            try
            {
                if (!Directory.Exists(folderPath))
                {
                    Debug.WriteLine($"⚠️ Folder doesn't exist: {folderPath}");
                    return;
                }
                
                Debug.WriteLine($"🎨 Setting custom icon for: {folderPath}");
                
                // Get icon path from app resources
                var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "Images", "okcloud_folder.ico");
                
                // If icon doesn't exist, try alternative paths
                if (!File.Exists(iconPath))
                {
                    iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "okcloud_folder.ico");
                }
                
                if (!File.Exists(iconPath))
                {
                    iconPath = Path.Combine(AppContext.BaseDirectory, "okcloud_folder.ico");
                }
                
                if (!File.Exists(iconPath))
                {
                    Debug.WriteLine($"⚠️ Icon file not found, skipping custom icon");
                    return;
                }
                
                Debug.WriteLine($"📁 Using icon: {iconPath}");
                
                // Create desktop.ini file
                var desktopIniPath = Path.Combine(folderPath, "desktop.ini");
                
                var iniContent = $@"[.ShellClassInfo]
IconResource={iconPath},0
IconFile={iconPath}
IconIndex=0
InfoTip=OK Cloud Sync Folder
[ViewState]
Mode=
Vid=
FolderType=Generic";
                
                await File.WriteAllTextAsync(desktopIniPath, iniContent);
                
                // Set hidden + system attributes on desktop.ini
                File.SetAttributes(desktopIniPath, FileAttributes.Hidden | FileAttributes.System);
                
                // Set folder as system folder (required for custom icon to work)
                var dirInfo = new DirectoryInfo(folderPath);
                dirInfo.Attributes |= FileAttributes.System;
                
                // Refresh the folder in Explorer (force icon update)
                RefreshExplorer(folderPath);
                
                Debug.WriteLine($"✅ Custom icon set successfully for: {Path.GetFileName(folderPath)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Failed to set folder icon: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Refresh Windows Explorer to show updated folder icon
        /// </summary>
        private void RefreshExplorer(string folderPath)
        {
            try
            {
                // This forces Windows to refresh the folder icon
                var parentPath = Path.GetDirectoryName(folderPath);
                if (!string.IsNullOrEmpty(parentPath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{folderPath}\"",
                        UseShellExecute = true,
                        CreateNoWindow = true
                    })?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠️ Failed to refresh Explorer: {ex.Message}");
            }
        }

        public async Task SwitchWorkspaceAsync(int newWorkspaceId)
        {
            Debug.WriteLine($"🔄 SyncService switching to workspace: {newWorkspaceId}");
            Debug.WriteLine($"ℹ️ IMPORTANT: Workspace is a context, not a folder. We sync the CONTENTS of the workspace.");
            
            // Stop any ongoing sync
            if (_isSyncing)
            {
                _syncCancellation?.Cancel();
                _syncCancellation?.Dispose();
                _syncCancellation = null;
                _isSyncing = false;
                Debug.WriteLine("🛑 Stopped ongoing sync for workspace switch");
            }
            
            // Store old sync path before switching
            var oldSyncPath = _syncFolderPath;
            
            // CRITICAL: Save workspace ID for Windows Service compatibility
            await SaveForWindowsServiceAsync("workspace_id", newWorkspaceId.ToString());
            await SaveForWindowsServiceAsync("last_active_workspace_id", newWorkspaceId.ToString());
            Debug.WriteLine($"💾 Saved workspace {newWorkspaceId} for Windows Service");
            
            // CRITICAL FIX: Create workspace-specific sync folder
            var basePath = await SecureStorage.Default.GetAsync("sync_folder_path");
            if (!string.IsNullOrEmpty(basePath))
            {
                // Reset cached path to recalculate for new workspace
                _syncFolderPath = null;
                var newSyncPath = await GetSyncFolderPathAsync();
                Debug.WriteLine($"📁 Workspace {newWorkspaceId} sync path: {newSyncPath}");
                
                if (!string.IsNullOrEmpty(newSyncPath) && !Directory.Exists(newSyncPath))
                {
                    Directory.CreateDirectory(newSyncPath);
                    Debug.WriteLine($"📁 Created workspace sync folder: {newSyncPath}");
                    Debug.WriteLine($"ℹ️ This folder will contain the FILES from workspace '{newWorkspaceId}', not the workspace itself");
                    
                    // Set custom icon for the folder
                    await SetFolderIconAsync(newSyncPath);
                }
                
                Debug.WriteLine($"✅ Workspace switch complete. Ready to sync workspace {newWorkspaceId} contents.");
            }
        }
        
        /// <summary>
        /// Handle workspace rename - renames the local folder to match new name
        /// </summary>
        public async Task OnWorkspaceRenamedAsync(int workspaceId, string newName)
        {
            try
            {
                Debug.WriteLine($"✏️ Workspace {workspaceId} renamed to: {newName}");
                
                // Get base sync path
                var basePath = await SecureStorage.Default.GetAsync("sync_folder_path");
                if (string.IsNullOrEmpty(basePath))
                {
                    Debug.WriteLine("⚠️ No base sync path configured");
                    return;
                }
                
                // Find old folder (search for any folder starting with workspaceId_)
                var oldFolderPattern = $"{workspaceId}_*";
                var matchingFolders = Directory.GetDirectories(basePath, oldFolderPattern);
                
                if (matchingFolders.Length == 0)
                {
                    Debug.WriteLine($"⚠️ No existing folder found for workspace {workspaceId}");
                    return;
                }
                
                var oldFolderPath = matchingFolders[0];
                var oldFolderName = Path.GetFileName(oldFolderPath);
                
                // Calculate new folder name
                var sanitizedName = SanitizeFolderName(newName);
                var newFolderName = $"{workspaceId}_{sanitizedName}";
                var newFolderPath = Path.Combine(basePath, newFolderName);
                
                // Check if rename is needed
                if (oldFolderPath == newFolderPath)
                {
                    Debug.WriteLine($"✅ Folder name already correct: {newFolderName}");
                    return;
                }
                
                // Rename the folder
                Debug.WriteLine($"🔄 Renaming workspace folder:");
                Debug.WriteLine($"   From: {oldFolderName}");
                Debug.WriteLine($"   To: {newFolderName}");
                
                Directory.Move(oldFolderPath, newFolderPath);
                
                // Update cached path if this is the current workspace
                if (ApiService.CurrentWorkspaceId == workspaceId)
                {
                    _syncFolderPath = newFolderPath;
                }
                
                Debug.WriteLine($"✅ Workspace folder renamed successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Failed to rename workspace folder: {ex.Message}");
            }
        }

        public async Task<bool> IsSyncConfiguredAsync()
        {
            var path = await GetSyncFolderPathAsync();
            return !string.IsNullOrEmpty(path) && Directory.Exists(path);
        }

        public async Task StartInitialSyncAsync(bool uploadLocalFiles = true, bool downloadRemoteFiles = true)
        {
            if (_isSyncing)
            {
                Debug.WriteLine("⚠️ Sync already in progress");
                return;
            }

            var syncPath = await GetSyncFolderPathAsync();
            if (string.IsNullOrEmpty(syncPath))
            {
                OnSyncError?.Invoke("Sync folder not configured");
                return;
            }

            // CRITICAL: Capture workspace ID at the START of sync to prevent mixing
            var workspaceIdAtStart = ApiService.CurrentWorkspaceId;
            Debug.WriteLine($"🔒 LOCKED workspace ID for this sync: {workspaceIdAtStart}");
            Debug.WriteLine($"🔄 BIDIRECTIONAL SYNC: Uploading local changes AND downloading newer remote files");

            _isSyncing = true;
            
            // CRITICAL: STOP FileWatcher during sync to prevent concurrent uploads
            FileWatcherService? fileWatcherService = null;
            bool wasWatcherRunning = false;
            
            try
            {
                fileWatcherService = Application.Current?.Handler?.MauiContext?.Services.GetService<FileWatcherService>();
                if (fileWatcherService != null && fileWatcherService.IsWatching)
                {
                    Debug.WriteLine("🛑 STOPPING FileWatcher during sync to prevent conflicts");
                    fileWatcherService.StopWatching();
                    wasWatcherRunning = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠️ Could not stop FileWatcher: {ex.Message}");
            }
            
            // Ensure we have a valid cancellation token
            if (_syncCancellation == null || _syncCancellation.IsCancellationRequested)
            {
                _syncCancellation?.Dispose();
                _syncCancellation = new CancellationTokenSource();
                Debug.WriteLine("🔄 Created new cancellation token for sync");
            }
            
            // Create a local reference to prevent race conditions
            var syncCancellationSource = _syncCancellation;
            var cancellationToken = syncCancellationSource.Token;
            
            try
            {
                Debug.WriteLine("🔄 Starting initial sync...");
                Debug.WriteLine($"📁 Sync folder: {syncPath}");
                Debug.WriteLine($"🏢 Workspace: {workspaceIdAtStart}");
                
                // Clear the downloaded files tracker
                _downloadedInCurrentSync.Clear();
                
                // Create sync folder if it doesn't exist
                if (!Directory.Exists(syncPath))
                {
                    Directory.CreateDirectory(syncPath);
                }

                // Get all files from server FIRST
                OnSyncProgress?.Invoke(new SyncProgress
                {
                    Stage = "Fetching file list from server...",
                    Percentage = 10
                });

                var remoteFiles = await _apiService.GetFilesAsync();
                Debug.WriteLine($"📊 Found {remoteFiles.Count} files on server");
                
                // Build a complete map of remote files (including in folders)
                var allRemoteFiles = await BuildCompleteRemoteFileMapAsync(remoteFiles);
                Debug.WriteLine($"📊 Total remote files (including subfolders): {allRemoteFiles.Count}");
                
                OnSyncProgress?.Invoke(new SyncProgress
                {
                    Stage = "Analyzing local files...",
                    Percentage = 20
                });

                // Count local files
                var localFileCount = Directory.Exists(syncPath) 
                    ? Directory.GetFiles(syncPath, "*", SearchOption.AllDirectories).Length 
                    : 0;
                Debug.WriteLine($"📊 Found {localFileCount} files locally");

                // PRIORITY: Upload local files FIRST (folder-based is source of truth)
                if (uploadLocalFiles)
                {
                    // CRITICAL: Verify workspace hasn't changed before uploading
                    if (ApiService.CurrentWorkspaceId != workspaceIdAtStart)
                    {
                        Debug.WriteLine($"⚠️ ABORT: Workspace changed during sync! Started with {workspaceIdAtStart}, now {ApiService.CurrentWorkspaceId}");
                        OnSyncError?.Invoke("Workspace changed during sync");
                        return;
                    }
                    
                    OnSyncProgress?.Invoke(new SyncProgress
                    {
                        Stage = "Uploading local changes to server (Phase 1)...",
                        Percentage = 30
                    });

                    Debug.WriteLine($"📤 PHASE 1: Uploading local files that don't exist on server");
                    
                    // Upload any local files that don't exist on server
                    await UploadLocalFilesAsync(syncPath, allRemoteFiles, cancellationToken, workspaceIdAtStart);
                    
                    // CRITICAL: Verify workspace AGAIN after upload
                    if (ApiService.CurrentWorkspaceId != workspaceIdAtStart)
                    {
                        Debug.WriteLine($"⚠️ ABORT: Workspace changed after upload! Started with {workspaceIdAtStart}, now {ApiService.CurrentWorkspaceId}");
                        OnSyncError?.Invoke("Workspace changed during sync");
                        return;
                    }
                    
                    // Refresh remote files list after uploading
                    remoteFiles = await _apiService.GetFilesAsync();
                    allRemoteFiles = await BuildCompleteRemoteFileMapAsync(remoteFiles);
                    Debug.WriteLine($"📊 Refreshed remote file list after Phase 1 upload: {allRemoteFiles.Count} files");
                }
                else
                {
                    Debug.WriteLine("⏭️ Skipping upload phase (cleanup-only sync)");
                }

                // المزامنة الثنائية: دائماً قم بتنزيل الملفات الأحدث من السيرفر
                OnSyncProgress?.Invoke(new SyncProgress
                {
                    Stage = "Downloading newer files from server...",
                    Percentage = 60
                });

                // Download files from server that are newer or don't exist locally
                await SyncFilesAsync(remoteFiles, syncPath, cancellationToken);
                
                // Refresh remote files list after downloading
                remoteFiles = await _apiService.GetFilesAsync();
                allRemoteFiles = await BuildCompleteRemoteFileMapAsync(remoteFiles);
                Debug.WriteLine($"📊 Refreshed remote file list after download: {allRemoteFiles.Count} files");

                // CRITICAL FIX: بعد التنزيل، رفع الملفات المحلية الأحدث التي تم تخطيها
                if (uploadLocalFiles)
                {
                    // CRITICAL: Verify workspace hasn't changed before uploading
                    if (ApiService.CurrentWorkspaceId != workspaceIdAtStart)
                    {
                        Debug.WriteLine($"⚠️ ABORT: Workspace changed during sync! Started with {workspaceIdAtStart}, now {ApiService.CurrentWorkspaceId}");
                        OnSyncError?.Invoke("Workspace changed during sync");
                        return;
                    }
                    
                    OnSyncProgress?.Invoke(new SyncProgress
                    {
                        Stage = "Uploading local files that are newer...",
                        Percentage = 80
                    });

                    Debug.WriteLine($"📤 PHASE 2: Uploading local files that are newer than server versions");
                    
                    // رفع الملفات المحلية الأحدث (التي تم تخطيها في التنزيل)
                    await UploadLocalFilesAsync(syncPath, allRemoteFiles, cancellationToken, workspaceIdAtStart);
                    
                    // CRITICAL: Verify workspace AGAIN after upload
                    if (ApiService.CurrentWorkspaceId != workspaceIdAtStart)
                    {
                        Debug.WriteLine($"⚠️ ABORT: Workspace changed after upload! Started with {workspaceIdAtStart}, now {ApiService.CurrentWorkspaceId}");
                        OnSyncError?.Invoke("Workspace changed during sync");
                        return;
                    }
                    
                    Debug.WriteLine($"✅ Phase 2 upload complete");
                }

                OnSyncProgress?.Invoke(new SyncProgress
                {
                    Stage = "Sync complete!",
                    Percentage = 100
                });

                OnSyncComplete?.Invoke();
                Debug.WriteLine("✅ Initial sync completed successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Sync error: {ex.Message}");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                OnSyncError?.Invoke(ex.Message);
            }
            finally
            {
                _isSyncing = false;
                syncCancellationSource?.Dispose();
                if (_syncCancellation == syncCancellationSource)
                {
                    _syncCancellation = null;
                }
                
                // CRITICAL: RESTART FileWatcher after sync completes
                if (wasWatcherRunning && fileWatcherService != null)
                {
                    Debug.WriteLine("🔄 RESTARTING FileWatcher after sync completion");
                    
                    // PERFORMANCE FIX: Reduced delay - operations are already complete
                    await Task.Delay(500); // Wait 500ms for FileWatcher to settle (reduced from 10s)
                    
                    // Mark all files in sync folder as "recently synced" to prevent FileWatcher from re-uploading them
                    try
                    {
                        var allLocalFiles = Directory.GetFiles(syncPath, "*", SearchOption.AllDirectories);
                        foreach (var filePath in allLocalFiles)
                        {
                            MarkFileAsDownloaded(filePath); // Mark as synced to prevent re-upload
                        }
                        Debug.WriteLine($"✅ Marked {allLocalFiles.Length} files as synced to prevent FileWatcher re-upload");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"⚠️ Failed to mark files as synced: {ex.Message}");
                    }
                    
                    await fileWatcherService.StartWatchingAsync();
                }
                
                // Clear downloaded files tracker after a delay to prevent FileWatcher from re-uploading
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000); // Wait 5 seconds for FileWatcher to settle (reduced from 60s)
                    _downloadedInCurrentSync.Clear();
                    
                    // PERFORMANCE FIX: Keep entries for 2 hours to prevent duplicate uploads
                    var cutoffTime = DateTime.UtcNow.AddHours(-2);
                    var keysToRemove = _downloadedFilesWithTime
                        .Where(kvp => kvp.Value < cutoffTime)
                        .Select(kvp => kvp.Key)
                        .ToList();
                    
                    foreach (var key in keysToRemove)
                    {
                        _downloadedFilesWithTime.Remove(key);
                    }
                    
                    Debug.WriteLine($"🧹 Cleared downloaded files tracker after delay (removed {keysToRemove.Count} old entries)");
                });
            }
        }

        // Build a complete map of all remote files (including nested folders) - OPTIMIZED FOR SPEED
        private async Task<Dictionary<string, FileEntry>> BuildCompleteRemoteFileMapAsync(List<FileEntry> rootFiles)
        {
            var fileMap = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            var mapLock = new object();
            
            async Task ProcessFiles(List<FileEntry> files, string basePath = "")
            {
                // PERFORMANCE FIX: Process folders in parallel for faster mapping
                var folders = files.Where(f => f.Type == "folder").ToList();
                var filesList = files.Where(f => f.Type == "file").ToList();
                
                // Add files immediately (no async needed)
                foreach (var file in filesList)
                {
                    var filePath = string.IsNullOrEmpty(basePath) 
                        ? file.Name 
                        : Path.Combine(basePath, file.Name);
                    lock (mapLock)
                    {
                        fileMap[filePath] = file;
                    }
                }
                
                // Process folders in parallel (like Google Drive)
                if (folders.Any())
                {
                    var folderTasks = folders.Select(async file =>
                    {
                        var filePath = string.IsNullOrEmpty(basePath) 
                            ? file.Name 
                            : Path.Combine(basePath, file.Name);
                        
                        lock (mapLock)
                        {
                            fileMap[filePath] = file;
                        }
                        
                        // Process folder contents
                        try
                        {
                            var folderContents = await _apiService.GetFolderContentsAsync(file.Id);
                            await ProcessFiles(folderContents, filePath);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"⚠️ Failed to get contents of folder {file.Name}: {ex.Message}");
                        }
                    });
                    
                    await Task.WhenAll(folderTasks);
                }
            }
            
            await ProcessFiles(rootFiles);
            return fileMap;
        }

        private async Task SyncFilesAsync(List<FileEntry> files, string basePath, CancellationToken cancellationToken)
        {
            var totalFiles = files.Count;
            var processedFiles = 0;
            var processedLock = new object();

            // PERFORMANCE FIX: Separate folders and files for better processing
            var folders = files.Where(f => f.Type == "folder").ToList();
            var filesToDownload = files.Where(f => f.Type == "file").ToList();

            // STEP 1: Process folders first (must be sequential to maintain hierarchy)
            foreach (var folder in folders)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var folderPath = Path.Combine(basePath, folder.Name);
                    if (!Directory.Exists(folderPath))
                    {
                        Directory.CreateDirectory(folderPath);
                        Debug.WriteLine($"📁 Created folder: {folder.Name}");
                    }

                    // Sync folder contents recursively
                    if (folder.Id > 0)
                    {
                        var folderContents = await _apiService.GetFolderContentsAsync(folder.Id);
                        await SyncFilesAsync(folderContents, folderPath, cancellationToken);
                    }

                    lock (processedLock)
                    {
                        processedFiles++;
                        var percentage = 35 + (int)((processedFiles / (double)totalFiles) * 60);
                        OnSyncProgress?.Invoke(new SyncProgress
                        {
                            Stage = $"Syncing files... ({processedFiles}/{totalFiles})",
                            Percentage = percentage,
                            CurrentFile = folder.Name
                        });
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"❌ Error syncing folder {folder.Name}: {ex.Message}");
                }
            }

                // STEP 2: Download files in PARALLEL (15 concurrent for maximum speed)
                if (filesToDownload.Any())
                {
                    Debug.WriteLine($"🚀 PARALLEL DOWNLOAD: Processing {filesToDownload.Count} files with max 15 concurrent downloads");
                    
                    var semaphore = new SemaphoreSlim(50, 50); // 50 concurrent downloads for maximum speed (like Google Drive)
                var downloadTasks = new List<Task>();

                foreach (var file in filesToDownload)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    var downloadTask = Task.Run(async () =>
                    {
                        await semaphore.WaitAsync(cancellationToken);
                        try
                        {
                            var localFilePath = Path.Combine(basePath, file.Name);
                            
                            // Check if file needs to be downloaded
                            if (ShouldDownloadFile(file, localFilePath))
                            {
                                Debug.WriteLine($"⬇️ PARALLEL DOWNLOAD: {file.Name}");
                                var downloadedPath = await _apiService.DownloadFileAsync(file, basePath);
                                
                            if (downloadedPath != null)
                            {
                                Debug.WriteLine($"✅ Downloaded: {file.Name}");
                                
                                // CRITICAL: Track this file so we don't upload it again
                                MarkFileAsDownloaded(downloadedPath);
                                
                                // Save to local database
                                if (_localDb != null)
                                {
                                    var localFile = LocalFileEntry.FromFileEntry(file, downloadedPath);
                                    await _localDb.SaveFileAsync(localFile);
                                }
                            }
                        }
                        else
                        {
                            Debug.WriteLine($"⏭️ Skipped (up to date): {file.Name}");
                            
                            // CRITICAL FIX: لا نضع علامة "downloaded" على الملفات المحلية الأحدث
                            // لأنها يجب أن يتم رفعها في المرحلة التالية
                            // MarkFileAsDownloaded(localFilePath); // تم تعطيله
                            
                            // Still save metadata to database
                            if (_localDb != null)
                            {
                                var localFile = LocalFileEntry.FromFileEntry(file, localFilePath);
                                await _localDb.SaveFileAsync(localFile);
                            }
                        }

                            lock (processedLock)
                            {
                                processedFiles++;
                                var percentage = 35 + (int)((processedFiles / (double)totalFiles) * 60);
                                OnSyncProgress?.Invoke(new SyncProgress
                                {
                                    Stage = $"Syncing files... ({processedFiles}/{totalFiles})",
                                    Percentage = percentage,
                                    CurrentFile = file.Name
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"❌ Error downloading {file.Name}: {ex.Message}");
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, cancellationToken);

                    downloadTasks.Add(downloadTask);
                }

                // Wait for all downloads to complete
                try
                {
                    await Task.WhenAll(downloadTasks);
                    Debug.WriteLine($"✅ All {filesToDownload.Count} files processed");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"⚠️ Some downloads failed: {ex.Message}");
                }
            }
        }

        private bool ShouldDownloadFile(FileEntry remoteFile, string localFilePath)
        {
            // If file doesn't exist locally, download it (no size limit)
            if (!File.Exists(localFilePath))
            {
                // Log large file downloads for visibility
                if (remoteFile.FileSize > 100 * 1024 * 1024) // > 100 MB
                {
                    var sizeMB = remoteFile.FileSize / 1024.0 / 1024.0;
                    Debug.WriteLine($"⬇️ Downloading large file: {remoteFile.Name} ({sizeMB:F0} MB)");
                }
                return true;
            }

            var localFileInfo = new FileInfo(localFilePath);
            
            // Compare file size - if different, download
            if (localFileInfo.Length != remoteFile.FileSize)
            {
                Debug.WriteLine($"📊 Size mismatch for {remoteFile.Name}: local={localFileInfo.Length}, remote={remoteFile.FileSize}");
                return true;
            }

            // المزامنة الثنائية: مقارنة التواريخ - الأحدث يفوز
            if (remoteFile.UpdatedAt.HasValue)
            {
                var localModified = localFileInfo.LastWriteTimeUtc;
                var remoteModified = remoteFile.UpdatedAt.Value.ToUniversalTime();
                var timeDiff = (remoteModified - localModified).TotalSeconds;
                
                // إذا كان الملف على السيرفر أحدث (بأكثر من 2 ثانية)، قم بتنزيله
                if (timeDiff > 2)
                {
                    Debug.WriteLine($"⬇️ REMOTE IS NEWER: {remoteFile.Name} (remote: {remoteModified}, local: {localModified}, diff: {timeDiff:F1}s)");
                    Debug.WriteLine($"   Downloading newer version from server");
                    return true;
                }
                
                // إذا كان الملف المحلي أحدث (بأكثر من 2 ثانية)، لا تقم بتنزيله (سيتم رفعه لاحقاً)
                if (timeDiff < -2)
                {
                    Debug.WriteLine($"⬆️ LOCAL IS NEWER: {remoteFile.Name} (local: {localModified}, remote: {remoteModified}, diff: {Math.Abs(timeDiff):F1}s)");
                    Debug.WriteLine($"   Skipping download - local file will be uploaded in Phase 2");
                    // CRITICAL: لا نضع علامة "downloaded" لأن الملف سيتم رفعه
                    return false;
                }
                
                // الفرق أقل من 2 ثانية - نفس الملف تقريباً
                Debug.WriteLine($"✅ Files are in sync: {remoteFile.Name} (diff: {Math.Abs(timeDiff):F1}s)");
            }

            // File exists and matches - no need to download
            return false;
        }

        // Upload local files that don't exist on server
        private async Task UploadLocalFilesAsync(string basePath, Dictionary<string, FileEntry> remoteFileMap, CancellationToken cancellationToken, int expectedWorkspaceId, string relativePath = "", HashSet<string>? processedFiles = null)
        {
            try
            {
                Debug.WriteLine($"📤 Checking for local files to upload in: {basePath}");
                Debug.WriteLine($"📊 Current relative path: '{relativePath}'");
                
                // Initialize processed files tracker on first call
                if (processedFiles == null)
                {
                    processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Debug.WriteLine($"🆕 INITIALIZED: Global processed files tracker");
                }
                
                // Get all local files and folders
                var localFiles = Directory.GetFiles(basePath);
                var localFolders = Directory.GetDirectories(basePath);
                
                // FIRST: Process folders to ensure proper hierarchy - SORT BY DEPTH
                // Process shallow folders first, then deeper ones to ensure parent folders exist
                var sortedFolders = localFolders
                    .Select(path => new { Path = path, Depth = path.Split(Path.DirectorySeparatorChar).Length })
                    .OrderBy(x => x.Depth)
                    .Select(x => x.Path)
                    .ToArray();
                
                Debug.WriteLine($"📁 FIXED: Processing {sortedFolders.Length} folders in depth order:");
                for (int i = 0; i < sortedFolders.Length; i++)
                {
                    var folderName = Path.GetFileName(sortedFolders[i]);
                    var depth = sortedFolders[i].Split(Path.DirectorySeparatorChar).Length;
                    Debug.WriteLine($"   {i + 1}. {folderName} (depth: {depth})");
                }
                
                foreach (var localFolderPath in sortedFolders)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    
                    var folderName = Path.GetFileName(localFolderPath);
                    
                    // CRITICAL: Skip .git folders and other version control/system folders
                    // These folders contain many small files and short folder names that cause issues
                    if (folderName.StartsWith(".") || 
                        folderName.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                        folderName.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                        folderName.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                        folderName.Equals(".idea", StringComparison.OrdinalIgnoreCase) ||
                        folderName.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                        folderName.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                        folderName.Equals("__pycache__", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine($"⏭️ SKIP: System/version control folder: {folderName}");
                        continue;
                    }
                    
                    // CRITICAL: Skip folders inside .git directory
                    var folderRelativePath = string.IsNullOrEmpty(relativePath) 
                        ? folderName 
                        : Path.Combine(relativePath, folderName).Replace('\\', '/');
                    
                    if (folderRelativePath.Contains("/.git/") || folderRelativePath.Contains("\\.git\\") ||
                        folderRelativePath.StartsWith(".git/") || folderRelativePath.StartsWith(".git\\"))
                    {
                        Debug.WriteLine($"⏭️ SKIP: Folder inside .git directory: {folderRelativePath}");
                        continue;
                    }
                    
                    Debug.WriteLine($"📁 Processing folder: {folderName} → {folderRelativePath}");
                    
                    // Check if folder exists on server at the EXACT path (case-insensitive)
                    var normalizedFolderPath = folderRelativePath.Replace('\\', '/');
                    var remoteFolder = remoteFileMap.FirstOrDefault(kvp => 
                        string.Equals(kvp.Key.Replace('\\', '/'), normalizedFolderPath, StringComparison.OrdinalIgnoreCase)
                    ).Value;
                    
                    // FIXED: Only check for duplicates in the EXACT same location, not anywhere in structure
                    // This was preventing proper folder hierarchy creation
                    
                    if (remoteFolder == null)
                    {
                        // Get parent folder ID for proper hierarchy
                        var parentFolderId = GetParentFolderIdFromPath(folderRelativePath, remoteFileMap);
                        
                        Debug.WriteLine($"📁 FIXED: Creating folder: {folderName} (parent ID: {parentFolderId})");
                        Debug.WriteLine($"   Folder relative path: {folderRelativePath}");
                        Debug.WriteLine($"   Normalized path: {normalizedFolderPath}");
                        
                        remoteFolder = await _apiService.CreateFolderAsync(folderName, parentFolderId);
                        
                        if (remoteFolder != null)
                        {
                            // CRITICAL: Add to remote file map immediately
                            remoteFileMap[normalizedFolderPath] = remoteFolder;
                            Debug.WriteLine($"✅ FIXED: Created folder: {folderName} (ID: {remoteFolder.Id}, Parent: {remoteFolder.ParentId})");
                            Debug.WriteLine($"   Added to remote map: '{normalizedFolderPath}' → ID {remoteFolder.Id}");
                            
                            if (_localDb != null)
                            {
                                var localFolder = LocalFileEntry.FromFileEntry(remoteFolder, localFolderPath);
                                await _localDb.SaveFileAsync(localFolder);
                            }
                            
                            // PERFORMANCE FIX: No delay needed - folder is already created and added to map
                            // Removed 100ms delay for faster folder creation
                            
                            // VERIFICATION: Check if folder was added correctly
                            if (remoteFileMap.ContainsKey(normalizedFolderPath))
                            {
                                Debug.WriteLine($"✅ VERIFIED: Folder '{folderName}' is now in remote map");
                            }
                            else
                            {
                                Debug.WriteLine($"❌ ERROR: Folder '{folderName}' not found in remote map after creation!");
                            }
                        }
                        else
                        {
                            Debug.WriteLine($"❌ FIXED: Failed to create folder: {folderName}");
                            continue; // Skip this folder and its contents
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"📁 Folder exists: {folderRelativePath} (ID: {remoteFolder.Id})");
                        
                        // Ensure it's in local database
                        if (_localDb != null)
                        {
                            var localFolder = LocalFileEntry.FromFileEntry(remoteFolder, localFolderPath);
                            await _localDb.SaveFileAsync(localFolder);
                        }
                    }
                    
                    // CRITICAL: Verify workspace hasn't changed before processing subfolder
                    if (ApiService.CurrentWorkspaceId != expectedWorkspaceId)
                    {
                        Debug.WriteLine($"⚠️ ABORT: Workspace changed during folder processing! Expected {expectedWorkspaceId}, now {ApiService.CurrentWorkspaceId}");
                        return;
                    }
                    
                    // Recursively process subfolder with shared processed files tracker
                    await UploadLocalFilesAsync(localFolderPath, remoteFileMap, cancellationToken, expectedWorkspaceId, folderRelativePath, processedFiles);
                }
                
                // SECOND: Process files after folders are created
                // Collect files to upload first, then upload in parallel
                var filesToUpload = new List<(string localFilePath, string fileRelativePath, string normalizedPath, int? parentFolderId)>();
                
                foreach (var localFilePath in localFiles)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    
                    var fileName = Path.GetFileName(localFilePath);
                    
                    // Skip system files
                    if (fileName.StartsWith(".") || fileName == "desktop.ini" || fileName == "Thumbs.db")
                        continue;
                    
                    // CRITICAL: Skip files inside .git directory
                    if (localFilePath.Contains("\\.git\\") || localFilePath.Contains("/.git/"))
                    {
                        Debug.WriteLine($"⏭️ SKIP: File inside .git directory: {fileName}");
                        continue;
                    }
                    
                    // CRITICAL: Skip files that were downloaded in this sync session
                    if (_downloadedInCurrentSync.Contains(localFilePath))
                    {
                        Debug.WriteLine($"⏭️ SKIP: {fileName} (downloaded in this sync)");
                        continue;
                    }
                    
                    // Build the relative path for this file (normalize path separators)
                    var fileRelativePath = string.IsNullOrEmpty(relativePath) 
                        ? fileName 
                        : Path.Combine(relativePath, fileName).Replace('\\', '/');
                    
                    // Normalize the file name for comparison (case-insensitive)
                    var normalizedPath = fileRelativePath.Replace('\\', '/');
                    
                    // CRITICAL: Check if this file was already processed by any recursive call
                    if (processedFiles.Contains(normalizedPath))
                    {
                        Debug.WriteLine($"⏭️ SKIP: File already processed by another recursive call: {fileName}");
                        continue;
                    }
                    
                    // Mark as being processed to prevent duplicates across recursive calls
                    processedFiles.Add(normalizedPath);
                    Debug.WriteLine($"🔒 MARKED: '{normalizedPath}' as being processed");
                    
                    // CRITICAL FIX: Check if file already exists on server using multiple methods
                    // Method 1: Check remote file map (fastest)
                    var remoteFile = remoteFileMap.FirstOrDefault(kvp => 
                        string.Equals(kvp.Key.Replace('\\', '/'), normalizedPath, StringComparison.OrdinalIgnoreCase)
                    ).Value;
                    
                    // Method 2: If not found in map, check local database (fallback)
                    if (remoteFile == null && _localDb != null)
                    {
                        var allFiles = await _localDb.GetAllFilesAsync();
                        var dbFile = allFiles.FirstOrDefault(f => 
                            f.LocalPath == localFilePath || 
                            (f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase) && f.FileSize > 0)
                        );
                        
                        if (dbFile != null && dbFile.Id > 0)
                        {
                            // File exists in database - check if it still exists on server
                            Debug.WriteLine($"🔍 File found in local DB: {fileName} (ID: {dbFile.Id}), verifying on server...");
                            // We'll let the upload proceed but ApiService will check for duplicates
                        }
                    }
                    
                    if (remoteFile != null)
                    {
                        // File exists on server - check if it's the same file
                        var localFileInfo = new FileInfo(localFilePath);
                        
                        // Compare file size AND name to ensure it's the same file
                        bool isSameFile = localFileInfo.Length == remoteFile.FileSize && 
                                         remoteFile.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase);
                        
                        if (isSameFile)
                        {
                            Debug.WriteLine($"⏭️ SKIP: {fileRelativePath} (already on server, size={localFileInfo.Length}, ID={remoteFile.Id})");
                            
                            // Save to local database
                            if (_localDb != null)
                            {
                                var localFile = LocalFileEntry.FromFileEntry(remoteFile, localFilePath);
                                await _localDb.SaveFileAsync(localFile);
                            }
                            continue;
                        }
                        else
                        {
                            // CONFLICT: File exists in both places with different sizes
                            // المزامنة الثنائية: الأحدث يفوز
                            var localModified = localFileInfo.LastWriteTimeUtc;
                            var remoteModified = remoteFile.UpdatedAt?.ToUniversalTime() ?? DateTime.MinValue;
                            var timeDiff = (localModified - remoteModified).TotalSeconds;
                            
                            // إذا كان الملف المحلي أحدث (بأكثر من 2 ثانية)، ارفعه
                            if (timeDiff > 2)
                            {
                                Debug.WriteLine($"⚠️ CONFLICT: Local file is newer, will upload: {fileRelativePath}");
                                Debug.WriteLine($"   Local: {localFileInfo.Length} bytes, modified {localModified}");
                                Debug.WriteLine($"   Remote: {remoteFile.FileSize} bytes, modified {remoteModified}");
                                Debug.WriteLine($"   Time diff: {timeDiff:F1}s (local is newer)");
                                
                                // Delete old version on server first
                                try
                                {
                                    await _apiService.DeleteFileAsync(remoteFile.Id);
                                    Debug.WriteLine($"🗑️ Deleted old version from server");
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"⚠️ Failed to delete old version: {ex.Message}");
                                }
                            }
                            // إذا كان الملف على السيرفر أحدث (بأكثر من 2 ثانية)، قم بتنزيله
                            else if (timeDiff < -2)
                            {
                                Debug.WriteLine($"⬇️ CONFLICT: Remote file is newer, will download: {fileRelativePath}");
                                Debug.WriteLine($"   Remote: {remoteFile.FileSize} bytes, modified {remoteModified}");
                                Debug.WriteLine($"   Local: {localFileInfo.Length} bytes, modified {localModified}");
                                Debug.WriteLine($"   Time diff: {Math.Abs(timeDiff):F1}s (remote is newer)");
                                
                                // Download the newer version from server
                                try
                                {
                                    var downloadedPath = await _apiService.DownloadFileAsync(remoteFile, Path.GetDirectoryName(localFilePath) ?? basePath);
                                    if (downloadedPath != null)
                                    {
                                        Debug.WriteLine($"✅ Downloaded newer version: {downloadedPath}");
                                        
                                        // Mark as downloaded to prevent re-upload
                                        MarkFileAsDownloaded(downloadedPath);
                                        
                                        // Save to local database
                                        if (_localDb != null)
                                        {
                                            var localFile = LocalFileEntry.FromFileEntry(remoteFile, downloadedPath);
                                            await _localDb.SaveFileAsync(localFile);
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"❌ Failed to download newer version: {ex.Message}");
                                }
                                
                                continue; // Skip upload, we downloaded instead
                            }
                            else
                            {
                                // الفرق أقل من 2 ثانية - نفس الملف تقريباً، لكن الحجم مختلف
                                // في هذه الحالة، نفضل الملف المحلي (folder-based priority)
                                Debug.WriteLine($"⚠️ CONFLICT: Files have different sizes but similar timestamps: {fileRelativePath}");
                                Debug.WriteLine($"   Local: {localFileInfo.Length} bytes, modified {localModified}");
                                Debug.WriteLine($"   Remote: {remoteFile.FileSize} bytes, modified {remoteModified}");
                                Debug.WriteLine($"   Time diff: {Math.Abs(timeDiff):F1}s - preferring local version");
                                
                                // Delete old version on server and upload local
                                try
                                {
                                    await _apiService.DeleteFileAsync(remoteFile.Id);
                                    Debug.WriteLine($"🗑️ Deleted old version from server");
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"⚠️ Failed to delete old version: {ex.Message}");
                                }
                            }
                        }
                    }
                    
                    // Get parent folder ID for upload
                    var parentFolderId = GetParentFolderIdFromPath(fileRelativePath, remoteFileMap);
                    
                    // CRITICAL: Verify parent folder exists before upload - ENHANCED CHECK
                    var expectedParentPath = Path.GetDirectoryName(fileRelativePath)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(expectedParentPath) && expectedParentPath != "." && parentFolderId == null)
                    {
                        Debug.WriteLine($"❌ FIXED: ABORT UPLOAD: Parent folder not found for {fileRelativePath}");
                        Debug.WriteLine($"   Expected parent path: '{expectedParentPath}'");
                        Debug.WriteLine($"   This would cause file to appear in ROOT instead of correct folder");
                        Debug.WriteLine($"   SKIPPING to prevent misplacement");
                        continue; // Skip this file - parent folder must exist first
                    }
                    
                    if (parentFolderId == null && !string.IsNullOrEmpty(expectedParentPath))
                    {
                        Debug.WriteLine($"⚠️ FIXED: WARNING - File will go to ROOT: {fileName}");
                        Debug.WriteLine($"   Expected to be in: {expectedParentPath}");
                    }
                    
                    // Add to upload queue
                    filesToUpload.Add((localFilePath, fileRelativePath, normalizedPath, parentFolderId));
                }
                
                // PERFORMANCE FIX: Use parallel uploads (max 15 concurrent) for maximum speed
                Debug.WriteLine($"🚀 PARALLEL UPLOAD: Processing {filesToUpload.Count} files with max 15 concurrent uploads");
                
                var semaphore = new SemaphoreSlim(50, 50); // 50 concurrent uploads for maximum speed (like Google Drive)
                var uploadTasks = new List<Task>();
                
                foreach (var fileInfo in filesToUpload)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    
                    var (localFilePath, fileRelativePath, normalizedPath, parentFolderId) = fileInfo;
                    var fileName = Path.GetFileName(localFilePath);
                    
                    // Create upload task with semaphore control
                    var uploadTask = Task.Run(async () =>
                    {
                        await semaphore.WaitAsync(cancellationToken);
                        try
                        {
                            Debug.WriteLine($"⬆️ PARALLEL UPLOAD START: {fileRelativePath}");
                            Debug.WriteLine($"🎯 {fileName} → Parent ID: {parentFolderId}");
                            
                            // CRITICAL: Check our local map first (faster) - with improved path matching
                            var existingInMap = remoteFileMap.FirstOrDefault(kvp => 
                                string.Equals(kvp.Key.Replace('\\', '/'), normalizedPath, StringComparison.OrdinalIgnoreCase)
                            ).Value;
                            
                            if (existingInMap != null && existingInMap.Id > 0)
                            {
                                // Also check file size to ensure it's the same file
                                var localFileInfo = new FileInfo(localFilePath);
                                if (existingInMap.FileSize == localFileInfo.Length)
                                {
                                    Debug.WriteLine($"⏭️ SKIP: File already in remote map (same path and size): {fileName} (ID: {existingInMap.Id}, Size: {existingInMap.FileSize})");
                                    return;
                                }
                                else
                                {
                                    Debug.WriteLine($"⚠️ File exists in map but size differs: {fileName} (Remote: {existingInMap.FileSize}, Local: {localFileInfo.Length})");
                                    // Continue to upload - file might have changed
                                }
                            }
                            
                            // CRITICAL: Check if we're already processing this file (with improved path matching)
                            lock (remoteFileMap)
                            {
                                // Check with improved path matching
                                var alreadyExists = remoteFileMap.Any(kvp => 
                                    string.Equals(kvp.Key.Replace('\\', '/'), normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                                    kvp.Value.Id > 0 // Only check real files, not placeholders
                                );
                                
                                if (alreadyExists)
                                {
                                    Debug.WriteLine($"⏭️ SKIP: File already exists in map (improved check): {fileName}");
                                    return;
                                }
                                
                                // LOCK: Add placeholder to prevent concurrent uploads of same file
                                remoteFileMap[normalizedPath] = new FileEntry 
                                { 
                                    Id = -1, 
                                    Name = fileName, 
                                    Type = "file" 
                                };
                                Debug.WriteLine($"🔒 LOCKED: Reserved '{normalizedPath}' to prevent concurrent upload");
                            }
                            
                            // FINAL CHECK: Verify file doesn't exist in local database before upload
                            // CRITICAL FIX: Must check BOTH name, size AND parent folder ID to prevent false positives
                            if (_localDb != null)
                            {
                                var allFiles = await _localDb.GetAllFilesAsync();
                                var localFileInfo = new FileInfo(localFilePath);
                                
                                // Only skip if file with SAME name, size AND parent folder exists
                                var dbFile = allFiles.FirstOrDefault(f => 
                                    f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                                    f.FileSize == localFileInfo.Length &&
                                    f.ParentId == parentFolderId && // CRITICAL: Must be in same folder!
                                    f.Id > 0
                                );
                                
                                if (dbFile != null)
                                {
                                    Debug.WriteLine($"⏭️ SKIP: File exists in local DB with same name, size AND parent: {fileName} (ID: {dbFile.Id}, Parent: {dbFile.ParentId})");
                                    Debug.WriteLine($"   This file was likely already synced - preventing duplicate upload");
                                    
                                    // Remove placeholder
                                    lock (remoteFileMap)
                                    {
                                        if (remoteFileMap.ContainsKey(normalizedPath) && remoteFileMap[normalizedPath].Id == -1)
                                        {
                                            remoteFileMap.Remove(normalizedPath);
                                        }
                                    }
                                    return;
                                }
                            }
                            
                            // الرفع مع retry mechanism مدمج في ApiService
                            FileEntry? uploadedFile = null;
                            try
                            {
                                uploadedFile = await _apiService.UploadFileAsync(localFilePath, parentFolderId);
                            }
                            catch (Exception uploadEx)
                            {
                                Debug.WriteLine($"❌ PARALLEL UPLOAD EXCEPTION: {fileName}");
                                Debug.WriteLine($"   Error: {uploadEx.Message}");
                                Debug.WriteLine($"   Type: {uploadEx.GetType().Name}");
                                
                                // Remove the placeholder since upload failed
                                lock (remoteFileMap)
                                {
                                    if (remoteFileMap.ContainsKey(normalizedPath) && remoteFileMap[normalizedPath].Id == -1)
                                    {
                                        remoteFileMap.Remove(normalizedPath);
                                        Debug.WriteLine($"🔓 UNLOCKED: Removed placeholder after exception: '{normalizedPath}'");
                                    }
                                }
                                
                                // لا نعيد المحاولة هنا - ApiService.UploadFileAsync يتعامل مع retry داخلياً
                                return;
                            }
                            
                            if (uploadedFile != null)
                            {
                                Debug.WriteLine($"✅ PARALLEL UPLOAD SUCCESS: {fileName}");
                                Debug.WriteLine($"   Uploaded to: {(parentFolderId.HasValue ? $"Folder ID {parentFolderId}" : "Root")}");
                                Debug.WriteLine($"   File ID: {uploadedFile.Id}");
                                
                                // VERIFICATION: Check if file ended up in correct location
                                if (parentFolderId.HasValue && uploadedFile.ParentId != parentFolderId)
                                {
                                    Debug.WriteLine($"⚠️ WARNING: File uploaded to wrong parent!");
                                    Debug.WriteLine($"   Expected parent: {parentFolderId}");
                                    Debug.WriteLine($"   Actual parent: {uploadedFile.ParentId}");
                                }
                                
                                // CRITICAL: Update the locked entry with real file data
                                lock (remoteFileMap)
                                {
                                    remoteFileMap[normalizedPath] = uploadedFile;
                                    Debug.WriteLine($"🔒 UPDATED: Replaced placeholder with real file data for '{normalizedPath}'");
                                }
                                
                                // Save to local database IMMEDIATELY to prevent FileWatcher from re-uploading
                                if (_localDb != null)
                                {
                                    var localFile = LocalFileEntry.FromFileEntry(uploadedFile, localFilePath);
                                    await _localDb.SaveFileAsync(localFile);
                                    Debug.WriteLine($"💾 Saved to database immediately: {fileName} (ID: {uploadedFile.Id})");
                                }
                                
                                // CRITICAL: Mark file as synced to prevent FileWatcher from re-uploading
                                MarkFileAsDownloaded(localFilePath);
                                Debug.WriteLine($"🔒 Marked as synced to prevent re-upload: {fileName}");
                            }
                            else
                            {
                                Debug.WriteLine($"❌ PARALLEL UPLOAD FAILED: {fileName} (returned null)");
                                
                                // Remove the placeholder since upload failed
                                lock (remoteFileMap)
                                {
                                    if (remoteFileMap.ContainsKey(normalizedPath) && remoteFileMap[normalizedPath].Id == -1)
                                    {
                                        remoteFileMap.Remove(normalizedPath);
                                        Debug.WriteLine($"🔓 UNLOCKED: Removed placeholder for failed upload: '{normalizedPath}'");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            var fileSize = File.Exists(localFilePath) ? new FileInfo(localFilePath).Length : 0;
                            var fileSizeMB = fileSize / 1024.0 / 1024.0;
                            
                            Debug.WriteLine($"❌ Failed to upload {fileName}: {ex.Message}");
                            Debug.WriteLine($"   File size: {fileSizeMB:F2} MB");
                            Debug.WriteLine($"   Error type: {ex.GetType().Name}");
                            
                            // Check if it's a size limit error
                            if (ex.Message.Contains("too large") || ex.Message.Contains("exceeds"))
                            {
                                Debug.WriteLine($"⏭️ SKIPPING: File too large for server, continuing with other files");
                            }
                            else
                            {
                                Debug.WriteLine($"   Stack trace: {ex.StackTrace?.Substring(0, Math.Min(200, ex.StackTrace?.Length ?? 0))}");
                            }
                            
                            // Remove the placeholder since upload failed
                            lock (remoteFileMap)
                            {
                                if (remoteFileMap.ContainsKey(normalizedPath) && remoteFileMap[normalizedPath].Id == -1)
                                {
                                    remoteFileMap.Remove(normalizedPath);
                                    Debug.WriteLine($"🔓 UNLOCKED: Removed placeholder after exception: '{normalizedPath}'");
                                }
                            }
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, cancellationToken);
                    
                    uploadTasks.Add(uploadTask);
                }
                
                // Wait for all uploads to complete with timeout protection
                try
                {
                    // استخدام Task.WhenAll مع timeout لمنع التوقف الأبدي
                    var timeoutTask = Task.Delay(TimeSpan.FromMinutes(120)); // 2 hours timeout
                    var uploadsTask = Task.WhenAll(uploadTasks);
                    
                    var completedTask = await Task.WhenAny(uploadsTask, timeoutTask);
                    
                    if (completedTask == timeoutTask)
                    {
                        Debug.WriteLine($"⚠️ Upload timeout after 2 hours - some files may still be uploading");
                        Debug.WriteLine($"   Continuing with completed uploads...");
                    }
                    else
                    {
                        // All uploads completed
                        await uploadsTask; // Re-await to propagate any exceptions
                    }
                    
                    // Count successful uploads
                    var successCount = remoteFileMap.Count(kvp => 
                        kvp.Value.Type == "file" && 
                        kvp.Value.Id > 0 && 
                        filesToUpload.Any(f => f.normalizedPath == kvp.Key)
                    );
                    var failCount = filesToUpload.Count - successCount;
                    
                    Debug.WriteLine($"✅ Upload batch complete: {successCount} succeeded, {failCount} failed/skipped out of {filesToUpload.Count} total");
                    
                    if (failCount > 0)
                    {
                        Debug.WriteLine($"⚠️ {failCount} files were not uploaded (may be too large or had errors)");
                        Debug.WriteLine($"   Check logs above for specific error messages");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"⚠️ Some uploads failed: {ex.Message}");
                    Debug.WriteLine($"   Error type: {ex.GetType().Name}");
                    Debug.WriteLine($"   Stack trace: {ex.StackTrace?.Substring(0, Math.Min(300, ex.StackTrace?.Length ?? 0))}");
                    Debug.WriteLine($"   This may indicate network issues or server problems");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error uploading local files: {ex.Message}");
            }
        }

        // Get parent folder ID from relative path using the remote file map
        // NOTE: This method is async for future extensibility but currently runs synchronously
        private Task<int?> GetParentFolderIdFromPathAsync(string relativePath, Dictionary<string, FileEntry> remoteFileMap)
        {
            return Task.FromResult<int?>(GetParentFolderIdFromPath(relativePath, remoteFileMap));
        }

        // Cleanup local files that no longer exist on server
        private async Task CleanupDeletedFilesAsync(string basePath, Dictionary<string, FileEntry> remoteFileMap, CancellationToken cancellationToken, string relativePath = "")
        {
            try
            {
                Debug.WriteLine($"🧹 Cleaning up deleted files in: {basePath}");
                Debug.WriteLine($"📊 Remote file map has {remoteFileMap.Count} entries");
                
                // Get all local files and folders
                var localFiles = Directory.GetFiles(basePath);
                var localFolders = Directory.GetDirectories(basePath);
                
                // Check each local file
                foreach (var localFilePath in localFiles)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    
                    var fileName = Path.GetFileName(localFilePath);
                    
                    // Skip system files
                    if (fileName.StartsWith(".") || fileName == "desktop.ini" || fileName == "Thumbs.db")
                        continue;
                    
                    // CRITICAL: Skip files inside .git directory
                    if (localFilePath.Contains("\\.git\\") || localFilePath.Contains("/.git/"))
                    {
                        Debug.WriteLine($"⏭️ SKIP: File inside .git directory: {fileName}");
                        continue;
                    }
                    
                    // Build relative path (normalize to forward slashes)
                    var fileRelativePath = string.IsNullOrEmpty(relativePath) 
                        ? fileName 
                        : Path.Combine(relativePath, fileName).Replace('\\', '/');
                    
                    // Check if file exists on server (case-insensitive)
                    var existsOnServer = remoteFileMap.Keys.Any(k => 
                        string.Equals(k.Replace('\\', '/'), fileRelativePath, StringComparison.OrdinalIgnoreCase));
                    
                    Debug.WriteLine($"   📄 {fileRelativePath} → {(existsOnServer ? "EXISTS on server" : "NOT on server - will delete")}");
                    
                    if (!existsOnServer)
                    {
                        // File doesn't exist on server - delete it locally
                        Debug.WriteLine($"🗑️ Deleting local file (not on server): {fileRelativePath}");
                        
                        try
                        {
                            File.Delete(localFilePath);
                            
                            // Remove from local database
                            if (_localDb != null)
                            {
                                await _localDb.DeleteFileByPathAsync(localFilePath);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"❌ Failed to delete {fileName}: {ex.Message}");
                        }
                    }
                }
                
                // Check each local folder
                foreach (var localFolderPath in localFolders)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;
                    
                    var folderName = Path.GetFileName(localFolderPath);
                    var folderRelativePath = string.IsNullOrEmpty(relativePath) 
                        ? folderName 
                        : Path.Combine(relativePath, folderName).Replace('\\', '/');
                    
                    // Check if folder exists on server (case-insensitive)
                    var existsOnServer = remoteFileMap.Keys.Any(k => 
                        string.Equals(k.Replace('\\', '/'), folderRelativePath, StringComparison.OrdinalIgnoreCase));
                    
                    if (!existsOnServer)
                    {
                        // FOLDER-BASED PRIORITY: Never delete local folders
                        // Local folders are the source of truth and should be uploaded, not deleted
                        Debug.WriteLine($"⏭️ Local folder not on server (will be uploaded next sync): {folderRelativePath}");
                        // Don't delete - local folders have priority
                        continue;
                    }
                    else
                    {
                        // Folder exists - recursively check its contents
                        await CleanupDeletedFilesAsync(localFolderPath, remoteFileMap, cancellationToken, folderRelativePath);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error cleaning up deleted files: {ex.Message}");
            }
        }

        public void StopSync()
        {
            _syncCancellation?.Cancel();
            _isSyncing = false;
        }

        public async Task<SyncStatus> GetSyncStatusAsync()
        {
            var syncPath = await GetSyncFolderPathAsync();
            
            if (string.IsNullOrEmpty(syncPath) || !Directory.Exists(syncPath))
            {
                return new SyncStatus
                {
                    IsConfigured = false,
                    IsSyncing = false
                };
            }

            var localFiles = Directory.GetFiles(syncPath, "*", SearchOption.AllDirectories);
            
            return new SyncStatus
            {
                IsConfigured = true,
                IsSyncing = _isSyncing,
                SyncFolderPath = syncPath,
                LocalFileCount = localFiles.Length
            };
        }

        /// <summary>
        /// Start periodic background sync to download new files from cloud
        /// </summary>
        public void StartPeriodicSync()
        {
            if (_periodicSyncTimer != null)
            {
                Debug.WriteLine("⚠️ Periodic sync already running");
                return;
            }

            Debug.WriteLine($"🔄 Starting periodic sync (every {SYNC_INTERVAL_MINUTES} minutes)");
            
            // Run sync immediately, then every X minutes
            _periodicSyncTimer = new Timer(async _ =>
            {
                try
                {
                    Debug.WriteLine($"⏰ Periodic sync triggered");
                    
                    // Check for workspace rename before syncing
                    await CheckWorkspaceRenameAsync();
                    
                    // Normal sync
                    await TriggerManualSyncAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"❌ Periodic sync error: {ex.Message}");
                }
            }, null, TimeSpan.Zero, TimeSpan.FromMinutes(SYNC_INTERVAL_MINUTES));
        }
        
        /// <summary>
        /// Check if current workspace was renamed and update folder name accordingly
        /// </summary>
        private async Task CheckWorkspaceRenameAsync()
        {
            try
            {
                // Skip if no sync folder configured
                if (string.IsNullOrEmpty(_syncFolderPath))
                    return;
                
                // Get current workspace info from API
                var workspaces = await _apiService.GetWorkspacesAsync();
                var currentWorkspace = workspaces.FirstOrDefault(w => w.Id == ApiService.CurrentWorkspaceId);
                
                if (currentWorkspace == null)
                    return;
                
                // Calculate expected folder name based on current workspace name
                var expectedFolderName = GetWorkspaceName(currentWorkspace.Id);
                var currentFolderName = Path.GetFileName(_syncFolderPath);
                
                // Check if folder name matches expected name
                if (currentFolderName != expectedFolderName)
                {
                    Debug.WriteLine($"🔄 Workspace rename detected!");
                    Debug.WriteLine($"   Current folder: {currentFolderName}");
                    Debug.WriteLine($"   Expected folder: {expectedFolderName}");
                    Debug.WriteLine($"   Workspace: {currentWorkspace.Name} (ID: {currentWorkspace.Id})");
                    
                    // Trigger rename
                    await OnWorkspaceRenamedAsync(currentWorkspace.Id, currentWorkspace.Name);
                    
                    // CRITICAL: Update cached path after successful rename
                    _syncFolderPath = null;
                    var updatedPath = await GetSyncFolderPathAsync();
                    Debug.WriteLine($"✅ Updated sync path after rename: {updatedPath}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠️ Workspace rename check failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop periodic background sync
        /// </summary>
        public void StopPeriodicSync()
        {
            if (_periodicSyncTimer != null)
            {
                Debug.WriteLine("⏹️ Stopping periodic sync");
                _periodicSyncTimer.Dispose();
                _periodicSyncTimer = null;
            }
        }

        /// <summary>
        /// Trigger a manual sync now
        /// </summary>
        public async Task TriggerManualSyncAsync(bool uploadLocalFiles = true)
        {
            if (_isSyncing)
            {
                Debug.WriteLine("⚠️ Sync already in progress");
                return;
            }

            try
            {
                // Ensure we have a valid cancellation token
                if (_syncCancellation == null || _syncCancellation.IsCancellationRequested)
                {
                    _syncCancellation?.Dispose();
                    _syncCancellation = new CancellationTokenSource();
                    Debug.WriteLine("🔄 Created new cancellation token for manual sync");
                }
                
                Debug.WriteLine($"🔄 Manual sync triggered (uploadLocalFiles={uploadLocalFiles})");
                await StartInitialSyncAsync(uploadLocalFiles);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Manual sync error: {ex.Message}");
                throw;
            }
        }
        
        /// <summary>
        /// Trigger a cleanup-only sync (download + cleanup, no upload)
        /// Use this after deleting files in the app to avoid re-uploading them
        /// </summary>
        public async Task TriggerCleanupSyncAsync()
        {
            Debug.WriteLine("🧹 Cleanup sync triggered (no uploads)");
            await StartInitialSyncAsync(uploadLocalFiles: false);
        }
        
        /// <summary>
        /// Check if a file was recently downloaded in the current sync session
        /// This helps prevent re-uploading files that were just downloaded
        /// </summary>
        public bool IsFileRecentlyDownloaded(string filePath)
        {
            // Normalize the path to handle different path formats
            var normalizedPath = Path.GetFullPath(filePath);
            
            // Check both the current sync tracker and the time-based tracker
            if (_downloadedInCurrentSync.Contains(filePath) || _downloadedInCurrentSync.Contains(normalizedPath))
            {
                Debug.WriteLine($"🔍 File found in current sync tracker: {Path.GetFileName(filePath)}");
                return true;
            }
            
            // Check both original and normalized paths in time-based tracker
            var pathsToCheck = new[] { filePath, normalizedPath };
            
            foreach (var pathToCheck in pathsToCheck)
            {
                if (_downloadedFilesWithTime.TryGetValue(pathToCheck, out DateTime downloadTime))
                {
                    var timeSinceDownload = DateTime.UtcNow - downloadTime;
                    if (timeSinceDownload.TotalHours < 2) // PERFORMANCE FIX: Consider files downloaded in last 2 hours as "recent" to prevent duplicates
                    {
                        Debug.WriteLine($"🔍 File downloaded recently ({timeSinceDownload.TotalSeconds:F0}s ago): {Path.GetFileName(filePath)}");
                        return true;
                    }
                    else
                    {
                        // Remove old entries
                        _downloadedFilesWithTime.Remove(pathToCheck);
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Add a file to the downloaded files tracking
        /// Used by FileWatcher to mark files as "don't upload"
        /// </summary>
        public void MarkFileAsDownloaded(string filePath)
        {
            // Normalize the path to handle different path formats
            var normalizedPath = Path.GetFullPath(filePath);
            
            _downloadedInCurrentSync.Add(normalizedPath);
            _downloadedFilesWithTime[normalizedPath] = DateTime.UtcNow;
            
            // Also add the original path in case it's different
            if (filePath != normalizedPath)
            {
                _downloadedInCurrentSync.Add(filePath);
                _downloadedFilesWithTime[filePath] = DateTime.UtcNow;
            }
            
            Debug.WriteLine($"📝 Marked file as downloaded: {Path.GetFileName(filePath)}");
        }
        
        /// <summary>
        /// Clean up duplicate files that were uploaded multiple times
        /// </summary>
        public async Task<int> CleanupDuplicateFilesAsync()
        {
            try
            {
                Debug.WriteLine("🧹 EMERGENCY: Scanning for duplicate files...");
                
                var remoteFiles = await _apiService.GetFilesAsync();
                var allRemoteFiles = await BuildCompleteRemoteFileMapAsync(remoteFiles);
                
                // Group files by name and parent folder
                var fileGroups = allRemoteFiles
                    .Where(kvp => kvp.Value.Type == "file")
                    .GroupBy(kvp => new { 
                        Name = kvp.Value.Name.ToLowerInvariant(), 
                        ParentId = kvp.Value.ParentId 
                    })
                    .Where(group => group.Count() > 1)
                    .ToList();
                
                var cleanedCount = 0;
                
                foreach (var group in fileGroups)
                {
                    var fileName = group.Key.Name;
                    var duplicates = group.OrderBy(g => g.Value.Id).ToList(); // Keep oldest, delete newer
                    
                    Debug.WriteLine($"🚨 DUPLICATE FILES DETECTED: '{fileName}' ({duplicates.Count} copies)");
                    
                    // Keep the first one, delete the rest
                    for (int i = 1; i < duplicates.Count; i++)
                    {
                        var duplicate = duplicates[i];
                        Debug.WriteLine($"🗑️ Deleting duplicate: {duplicate.Value.Name} (ID: {duplicate.Value.Id})");
                        
                        var deleted = await _apiService.DeleteFileAsync(duplicate.Value.Id);
                        if (deleted)
                        {
                            cleanedCount++;
                            Debug.WriteLine($"✅ Deleted duplicate file: {duplicate.Value.Name}");
                        }
                        else
                        {
                            Debug.WriteLine($"❌ Failed to delete: {duplicate.Value.Name}");
                        }
                        
                        // PERFORMANCE FIX: Removed delay - parallel processing handles rate limiting
                        // await Task.Delay(500); // Removed for faster cleanup
                    }
                }
                
                Debug.WriteLine($"🧹 EMERGENCY CLEANUP: {cleanedCount} duplicate files removed");
                return cleanedCount;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error during duplicate cleanup: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Clean up files that were uploaded to wrong location (Root instead of intended folder)
        /// </summary>
        public async Task<int> CleanupMisplacedFilesAsync()
        {
            try
            {
                Debug.WriteLine("🧹 Scanning for misplaced files...");
                
                var remoteFiles = await _apiService.GetFilesAsync();
                var allRemoteFiles = await BuildCompleteRemoteFileMapAsync(remoteFiles);
                
                var rootFiles = allRemoteFiles.Where(kvp => 
                    kvp.Value.Type == "file" && 
                    !kvp.Key.Contains('/') // Files directly in root
                ).ToList();
                
                var cleanedCount = 0;
                
                foreach (var rootFile in rootFiles)
                {
                    var fileName = rootFile.Value.Name;
                    
                    // Look for the same file in a subfolder
                    var duplicateInSubfolder = allRemoteFiles.FirstOrDefault(kvp =>
                        kvp.Value.Type == "file" &&
                        kvp.Key.Contains('/') && // In a subfolder
                        kvp.Key.Split('/').Last().Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
                        kvp.Value.FileSize == rootFile.Value.FileSize // Same size
                    );
                    
                    if (duplicateInSubfolder.Value != null)
                    {
                        Debug.WriteLine($"🚨 MISPLACED FILE DETECTED:");
                        Debug.WriteLine($"   Root: {rootFile.Key} (ID: {rootFile.Value.Id})");
                        Debug.WriteLine($"   Correct: {duplicateInSubfolder.Key} (ID: {duplicateInSubfolder.Value.Id})");
                        
                        // Delete the misplaced file from root
                        var deleted = await _apiService.DeleteFileAsync(rootFile.Value.Id);
                        if (deleted)
                        {
                            Debug.WriteLine($"✅ Cleaned up misplaced file: {fileName}");
                            cleanedCount++;
                        }
                        else
                        {
                            Debug.WriteLine($"❌ Failed to clean up: {fileName}");
                        }
                    }
                }
                
                if (cleanedCount > 0)
                {
                    Debug.WriteLine($"🧹 Cleanup complete: {cleanedCount} misplaced files removed");
                }
                else
                {
                    Debug.WriteLine("✅ No misplaced files found");
                }
                
                return cleanedCount;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error during cleanup: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Detect and report duplicate folders in the remote file structure
        /// </summary>
        public async Task<List<string>> DetectDuplicateFoldersAsync()
        {
            try
            {
                Debug.WriteLine("🔍 Scanning for duplicate folders...");
                
                var remoteFiles = await _apiService.GetFilesAsync();
                var allRemoteFiles = await BuildCompleteRemoteFileMapAsync(remoteFiles);
                
                var folderGroups = allRemoteFiles
                    .Where(kvp => kvp.Value.Type == "folder")
                    .GroupBy(kvp => kvp.Key.Split('/').Last().ToLowerInvariant())
                    .Where(group => group.Count() > 1)
                    .ToList();
                
                var duplicates = new List<string>();
                
                foreach (var group in folderGroups)
                {
                    var folderName = group.Key;
                    var locations = group.Select(g => $"{g.Key} (ID: {g.Value.Id})").ToList();
                    
                    Debug.WriteLine($"🚨 DUPLICATE FOLDER DETECTED: '{folderName}'");
                    foreach (var location in locations)
                    {
                        Debug.WriteLine($"   - {location}");
                    }
                    
                    duplicates.Add($"Folder '{folderName}' found in: {string.Join(", ", locations)}");
                }
                
                if (duplicates.Count == 0)
                {
                    Debug.WriteLine("✅ No duplicate folders detected");
                }
                else
                {
                    Debug.WriteLine($"⚠️ Found {duplicates.Count} duplicate folder groups");
                }
                
                return duplicates;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error detecting duplicates: {ex.Message}");
                return new List<string>();
            }
        }

        // Get parent folder ID from relative path using the remote file map
        private int? GetParentFolderIdFromPath(string relativePath, Dictionary<string, FileEntry> remoteFileMap)
        {
            var parentPath = Path.GetDirectoryName(relativePath)?.Replace('\\', '/');
            
            if (string.IsNullOrEmpty(parentPath) || parentPath == ".")
            {
                Debug.WriteLine($"🏠 FIXED: '{relativePath}' belongs to Root folder");
                return null; // Root folder
            }
            
            Debug.WriteLine($"🔍 FIXED SEARCH: Looking for parent folder: '{parentPath}' for item: '{relativePath}'");
            Debug.WriteLine($"   Remote file map has {remoteFileMap.Count} total entries");
            
            // Find parent folder in remote file map (case-insensitive)
            var parentFolder = remoteFileMap.FirstOrDefault(kvp => 
                kvp.Value.Type == "folder" &&
                string.Equals(kvp.Key.Replace('\\', '/'), parentPath, StringComparison.OrdinalIgnoreCase)
            ).Value;
            
            if (parentFolder != null)
            {
                Debug.WriteLine($"✅ FIXED: PARENT FOUND: {parentFolder.Name} (ID: {parentFolder.Id})");
                Debug.WriteLine($"   Path match: '{parentPath}' → ID {parentFolder.Id}");
                return parentFolder.Id;
            }
            
            Debug.WriteLine($"❌ FIXED: PARENT NOT FOUND: '{parentPath}'");
            Debug.WriteLine($"   This means the parent folder hasn't been created yet or path mismatch");
            Debug.WriteLine($"   Available folders in remote map:");
            
            var availableFolders = remoteFileMap.Where(kvp => kvp.Value.Type == "folder").Take(15);
            foreach (var folder in availableFolders)
            {
                Debug.WriteLine($"   - '{folder.Key}' (ID: {folder.Value.Id})");
            }
            
            // CRITICAL: If parent not found, this file will go to ROOT
            Debug.WriteLine($"⚠️ FIXED: File '{Path.GetFileName(relativePath)}' will be uploaded to ROOT due to missing parent!");
            
            return null;
        }

        /// <summary>
        /// Upload a single file to the server
        /// </summary>
        public async Task<bool> UploadFileAsync(string filePath, int? parentFolderId = null)
        {
            try
            {
                Debug.WriteLine($"📤 Uploading file: {Path.GetFileName(filePath)}");
                
                var uploadedFile = await _apiService.UploadFileAsync(filePath, parentFolderId);
                
                if (uploadedFile != null)
                {
                    Debug.WriteLine($"✅ File uploaded successfully: {uploadedFile.Name}");
                    
                    // Save to local database
                    if (_localDb != null)
                    {
                        var localFile = LocalFileEntry.FromFileEntry(uploadedFile, filePath);
                        await _localDb.SaveFileAsync(localFile);
                    }
                    
                    return true;
                }
                
                Debug.WriteLine($"❌ Upload failed for: {Path.GetFileName(filePath)}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Upload error for {Path.GetFileName(filePath)}: {ex.Message}");
                return false;
            }
        }



    }

    public class SyncProgress
    {
        public string Stage { get; set; } = string.Empty;
        public int Percentage { get; set; }
        public string? CurrentFile { get; set; }
    }

    public class SyncStatus
    {
        public bool IsConfigured { get; set; }
        public bool IsSyncing { get; set; }
        public string? SyncFolderPath { get; set; }
        public int LocalFileCount { get; set; }
    }
}
