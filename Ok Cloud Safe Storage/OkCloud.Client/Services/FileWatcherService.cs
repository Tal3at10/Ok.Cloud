    using System.Diagnostics;
using OkCloud.Client.Models;

namespace OkCloud.Client.Services
{
    public class FileWatcherService : IDisposable
    {
        private FileSystemWatcher? _watcher;
        private readonly ApiService _apiService;
        private readonly LocalDatabaseService _localDb;
        private readonly SyncService _syncService;
        private string? _syncFolderPath;
        private bool _isWatching;
        
        // CRITICAL: Track workspace ID to prevent cross-workspace uploads
        private int _workspaceIdWhenStarted;

        // Enhanced debouncing - prevent multiple uploads of same file
        private readonly Dictionary<string, DateTime> _lastProcessedTime = new();
        private readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(1); // Reduced to 1 second for faster sync (like Google Drive)
        
        // Track files currently being processed to prevent concurrent uploads
        private readonly HashSet<string> _filesBeingProcessed = new();
        
        // Grace period - ignore files modified recently (likely by sync)
        private DateTime _watcherStartTime = DateTime.MinValue;
        
        // CRITICAL: Track when sync completed to prevent re-uploading synced files
        private DateTime _lastSyncCompletionTime = DateTime.MinValue;

        // Events
        public event Func<string, Task>? OnFileAdded;
        public event Func<string, Task>? OnFileChanged;
        public event Func<string, Task>? OnFileDeleted;
        public event Func<string, string, Task>? OnFileRenamed;

        public FileWatcherService(
            ApiService apiService, 
            LocalDatabaseService localDb,
            SyncService syncService)
        {
            _apiService = apiService;
            _localDb = localDb;
            _syncService = syncService;
        }

        public async Task StartWatchingAsync()
        {
            if (_isWatching)
            {
                Debug.WriteLine("⚠️ FileWatcher already running - performing initial scan anyway");
                // Still perform initial scan even if watcher is running
                _ = Task.Run(async () => await PerformInitialScanAsync());
                return;
            }

            _syncFolderPath = await _syncService.GetSyncFolderPathAsync();
            
            Debug.WriteLine($"🔍 FileWatcher checking sync folder: '{_syncFolderPath}'");
            
            if (string.IsNullOrEmpty(_syncFolderPath))
            {
                Debug.WriteLine("❌ Sync folder path is null or empty");
                return;
            }
            
            if (!Directory.Exists(_syncFolderPath))
            {
                Debug.WriteLine($"❌ Sync folder doesn't exist: {_syncFolderPath}");
                return;
            }

            try
            {
                // CRITICAL: Capture workspace ID when starting watcher
                _workspaceIdWhenStarted = ApiService.CurrentWorkspaceId;
                Debug.WriteLine($"🔒 FileWatcher locked to workspace ID: {_workspaceIdWhenStarted}");
                
                Debug.WriteLine($"🚀 Creating FileSystemWatcher for: {_syncFolderPath}");
                
                _watcher = new FileSystemWatcher(_syncFolderPath)
                {
                    NotifyFilter = NotifyFilters.FileName 
                                 | NotifyFilters.DirectoryName 
                                 | NotifyFilters.LastWrite 
                                 | NotifyFilters.Size,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };

                // Subscribe to events
                _watcher.Created += OnCreated;
                _watcher.Changed += OnChanged;
                _watcher.Deleted += OnDeleted;
                _watcher.Renamed += OnRenamed;
                _watcher.Error += OnError;

                _isWatching = true;
                _watcherStartTime = DateTime.Now;
                
                // CRITICAL: Update sync completion time to prevent re-uploading files synced recently
                _lastSyncCompletionTime = DateTime.Now;
                
                Debug.WriteLine($"✅ FileWatcher started successfully!");
                Debug.WriteLine($"   📁 Monitoring: {_syncFolderPath}");
                Debug.WriteLine($"   🏢 Workspace: {_workspaceIdWhenStarted}");
                Debug.WriteLine($"   🔧 Include subdirectories: {_watcher.IncludeSubdirectories}");
                Debug.WriteLine($"   📊 Notify filters: {_watcher.NotifyFilter}");
                Debug.WriteLine($"   ⚡ Events enabled: {_watcher.EnableRaisingEvents}");
                
                // Start cleanup task for debouncing dictionary
                _ = Task.Run(async () =>
                {
                    while (_isWatching)
                    {
                        await Task.Delay(TimeSpan.FromMinutes(5));
                        CleanupOldDebounceEntries();
                    }
                });
                
                // Test the folder by listing some files
                var files = Directory.GetFiles(_syncFolderPath, "*", SearchOption.TopDirectoryOnly);
                Debug.WriteLine($"   📄 Found {files.Length} files in sync folder");
                
                // DISABLED: Initial scan disabled to prevent duplicate uploads
                Debug.WriteLine("� Initial sgcan disabled - SyncService handles bulk uploads");
                // _ = Task.Run(async () => await PerformInitialScanAsync());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Failed to start FileWatcher: {ex.Message}");
                Debug.WriteLine($"   Stack trace: {ex.StackTrace}");
            }
        }

        public void StopWatching()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
                _isWatching = false;
                Debug.WriteLine("🛑 FileWatcher stopped");
            }
        }

        public async Task SwitchWorkspaceAsync()
        {
            Debug.WriteLine("🔄 FileWatcher switching workspace...");
            
            // Stop current watcher
            StopWatching();
            
            // Start watching new workspace folder
            await StartWatchingAsync();
        }

        /// <summary>
        /// Force an initial scan for existing files (DISABLED to prevent duplicates)
        /// </summary>
        public async Task ForceInitialScanAsync()
        {
            Debug.WriteLine("�  FORCE SCAN DISABLED: Preventing duplicate uploads");
            Debug.WriteLine("ℹ️ Use SyncService.StartInitialSyncAsync() for bulk uploads");
            await Task.CompletedTask;
        }

        private async void OnCreated(object sender, FileSystemEventArgs e)
        {
            try
            {
                // Debounce check
                if (!ShouldProcessFile(e.FullPath))
                {
                    Debug.WriteLine($"⏭️ Skipping (debounced): {e.Name}");
                    return;
                }

                Debug.WriteLine($"📁 File created: {e.Name}");
                
                // Wait a bit to ensure file/folder is ready
                await Task.Delay(500);
                
                // Check if it still exists (might have been renamed or deleted)
                if (!File.Exists(e.FullPath) && !Directory.Exists(e.FullPath))
                {
                    Debug.WriteLine($"⏭️ Skipping '{e.Name}' - was renamed or deleted");
                    return;
                }
                
                // Handle based on type
                if (File.Exists(e.FullPath))
                {
                    await HandleFileAddedAsync(e.FullPath);
                }
                else if (Directory.Exists(e.FullPath))
                {
                    await HandleFolderAddedAsync(e.FullPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error handling created file: {ex.Message}");
            }
        }

        private async void OnChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                // Ignore directory changes
                if (Directory.Exists(e.FullPath))
                    return;

                // Debounce check - IMPORTANT for Changed events
                if (!ShouldProcessFile(e.FullPath))
                {
                    return; // Silent skip for changed events
                }

                Debug.WriteLine($"📝 File changed: {e.Name}");
                
                // Wait to ensure file is fully written (reduced delay for faster sync)
                await Task.Delay(200);
                
                if (File.Exists(e.FullPath))
                {
                    await HandleFileChangedAsync(e.FullPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error handling changed file: {ex.Message}");
            }
        }

        private async void OnDeleted(object sender, FileSystemEventArgs e)
        {
            try
            {
                Debug.WriteLine($"🗑️ File deleted: {e.Name}");
                await HandleFileDeletedAsync(e.FullPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error handling deleted file: {ex.Message}");
            }
        }

        private async void OnRenamed(object sender, RenamedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"✏️ File renamed: {e.OldName} → {e.Name}");
                await HandleFileRenamedAsync(e.OldFullPath, e.FullPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error handling renamed file: {ex.Message}");
            }
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            Debug.WriteLine($"❌ FileWatcher error: {e.GetException()?.Message}");
        }

        // Handle file added
        private async Task HandleFileAddedAsync(string filePath)
        {
            try
            {
                var fileName = Path.GetFileName(filePath);
                Debug.WriteLine($"⬆️ Processing new file: {fileName}");
                
                // CRITICAL: Prevent concurrent processing of same file
                lock (_filesBeingProcessed)
                {
                    if (_filesBeingProcessed.Contains(filePath))
                    {
                        Debug.WriteLine($"⏭️ SKIP: File already being processed: {fileName}");
                        return;
                    }
                    _filesBeingProcessed.Add(filePath);
                }
                
                try
                {
                    // CRITICAL: Verify workspace hasn't changed
                    if (ApiService.CurrentWorkspaceId != _workspaceIdWhenStarted)
                    {
                        Debug.WriteLine($"⚠️ ABORT: Workspace changed! FileWatcher started with {_workspaceIdWhenStarted}, now {ApiService.CurrentWorkspaceId}");
                        Debug.WriteLine($"   File will be uploaded when FileWatcher restarts for new workspace");
                        return;
                    }
                    
                    // PERFORMANCE FIX: Reduced debounce delay for faster sync
                    var enhancedDebounceDelay = TimeSpan.FromSeconds(1); // Reduced to 1 second for faster sync
                    
                    if (_lastProcessedTime.TryGetValue(filePath, out DateTime lastTime))
                    {
                        var timeSinceLastProcess = DateTime.Now - lastTime;
                        if (timeSinceLastProcess < enhancedDebounceDelay)
                        {
                            Debug.WriteLine($"⏭️ SKIP: File processed recently ({timeSinceLastProcess.TotalSeconds:F1}s ago): {fileName}");
                            return;
                        }
                    }
                    
                    // Update last processed time
                    _lastProcessedTime[filePath] = DateTime.Now;
                    
                    // CRITICAL: Additional delay for complex folder structures to prevent race conditions
                    await Task.Delay(1000);
                
                // STEP 1: Skip system files and hidden files
                if (fileName.StartsWith(".") || fileName == "desktop.ini" || fileName == "Thumbs.db" || 
                    fileName.StartsWith("~$") || fileName.EndsWith(".tmp") || fileName.EndsWith(".temp"))
                {
                    Debug.WriteLine($"⏭️ SKIP: System/hidden file: {fileName}");
                    return;
                }
                
                // STEP 1.5: CRITICAL - Check if file was recently downloaded by sync
                if (_syncService.IsFileRecentlyDownloaded(filePath))
                {
                    Debug.WriteLine($"⏭️ SKIP: File was recently downloaded, not re-uploading: {fileName}");
                    return;
                }
                
                // STEP 1.6: CRITICAL - Check if file was created very recently (likely downloaded)
                var fileCreationInfo = new FileInfo(filePath);
                var timeSinceCreation = DateTime.Now - fileCreationInfo.CreationTime;
                var timeSinceModified = DateTime.Now - fileCreationInfo.LastWriteTime;
                
                // PERFORMANCE FIX: Increased grace period to 2 hours to prevent duplicate uploads
                if (timeSinceCreation.TotalMinutes < 120 || timeSinceModified.TotalMinutes < 120) // File created/modified in last 2 hours
                {
                    Debug.WriteLine($"⏭️ SKIP: File created/modified very recently ({timeSinceCreation.TotalSeconds:F1}s ago), likely synced: {fileName}");
                    return;
                }
                
                // STEP 2: Check if file already exists in local database
                var allFiles = await _localDb.GetAllFilesAsync();
                var existingFile = allFiles.FirstOrDefault(f => f.LocalPath == filePath);
                
                if (existingFile != null)
                {
                    Debug.WriteLine($"⏭️ SKIP: File already exists in database: {fileName} (ID: {existingFile.Id})");
                    return;
                }
                
                // STEP 2.5: Get file info and parent folder ID for duplicate checking
                var fileInfo = new FileInfo(filePath);
                
                // STEP 2.6: Check if file with same name, size AND parent folder exists (MUST check parent folder!)
                var parentId = await GetParentFolderIdAsync(filePath);
                Debug.WriteLine($"🔍 Calculated parent ID for new file: {parentId}");
                
                var duplicateFile = allFiles.FirstOrDefault(f => 
                    f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase) && 
                    f.ParentId == parentId && 
                    f.FileSize == fileInfo.Length);
                
                if (duplicateFile != null)
                {
                    Debug.WriteLine($"⏭️ SKIP: Duplicate file detected in same folder: {fileName} (same name, parent, and size)");
                    Debug.WriteLine($"   Existing: ID={duplicateFile.Id}, Size={duplicateFile.FileSize}, Parent={duplicateFile.ParentId}");
                    Debug.WriteLine($"   New file: Size={fileInfo.Length}, Parent={parentId}");
                    
                    // Update the local path in database to point to this file
                    duplicateFile.LocalPath = filePath;
                    await _localDb.UpdateFileAsync(duplicateFile);
                    Debug.WriteLine($"✅ Updated local path for existing file: {fileName}");
                    return;
                }
                
                // STEP 3: Check if file was recently downloaded by sync
                if (_syncService != null && _syncService.IsFileRecentlyDownloaded(filePath))
                {
                    Debug.WriteLine($"⏭️ SKIP: File was recently downloaded by sync: {fileName}");
                    return;
                }
                
                // STEP 4: Check if we're currently in a sync operation
                if (_syncService != null)
                {
                    var syncStatus = await _syncService.GetSyncStatusAsync();
                    if (syncStatus.IsSyncing)
                    {
                        Debug.WriteLine($"⏭️ SKIP: File created during active sync operation: {fileName}");
                        return;
                    }
                }
                
                // STEP 4.5: CRITICAL - Check if file was modified right after sync completion
                var timeSinceSyncCompletion = DateTime.Now - _lastSyncCompletionTime;
                if (timeSinceSyncCompletion.TotalMinutes < 10) // Within 10 minutes of sync completion
                {
                    // استخدام fileInfo الموجود من السطر 356 بدلاً من تعريف واحد جديد
                    var timeSinceFileModified = DateTime.Now - fileInfo.LastWriteTime;
                    
                    // If file was modified before or during sync, skip it (likely synced)
                    if (timeSinceFileModified.TotalMinutes > timeSinceSyncCompletion.TotalMinutes)
                    {
                        Debug.WriteLine($"⏭️ SKIP: File modified before sync completion ({timeSinceFileModified.TotalMinutes:F1}m ago), likely synced: {fileName}");
                        return;
                    }
                }
                
                // STEP 5: File is truly new - upload it
                // PERFORMANCE FIX: Final duplicate check before upload to prevent duplicates
                var finalCheck = allFiles.FirstOrDefault(f => 
                    f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase) && 
                    f.ParentId == parentId && 
                    f.FileSize == fileInfo.Length);
                
                if (finalCheck != null)
                {
                    Debug.WriteLine($"⏭️ SKIP: Final check - duplicate detected right before upload: {fileName}");
                    Debug.WriteLine($"   Existing: ID={finalCheck.Id}, Size={finalCheck.FileSize}");
                    return;
                }
                
                Debug.WriteLine($"🆕 Uploading genuinely new file: {fileName}");
                var uploadedFile = await _apiService.UploadFileAsync(filePath, parentId);
                
                if (uploadedFile != null)
                {
                    // Save to local database
                    var localEntry = LocalFileEntry.FromFileEntry(uploadedFile, filePath);
                    await _localDb.SaveFileAsync(localEntry);
                    
                    Debug.WriteLine($"✅ File uploaded and synced: {uploadedFile.Name} (parent: {uploadedFile.ParentId})");
                    
                    // Notify UI
                    if (OnFileAdded != null)
                        await OnFileAdded.Invoke(filePath);
                }
                }
                finally
                {
                    // CRITICAL: Always remove from processing list
                    lock (_filesBeingProcessed)
                    {
                        _filesBeingProcessed.Remove(filePath);
                    }
                }
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Not enough storage"))
            {
                Debug.WriteLine($"⚠️ Storage quota exceeded: {ex.Message}");
                // User will see error in UI
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Failed to upload file: {ex.Message}");
                // User will see error in UI
            }
        }

        // Handle folder added
        private async Task HandleFolderAddedAsync(string folderPath)
        {
            try
            {
                var folderName = Path.GetFileName(folderPath);
                Debug.WriteLine($"📁 FILEWATCHER: Processing new folder: {folderName}");
                Debug.WriteLine($"   Full path: {folderPath}");
                

                
                // CRITICAL: Verify workspace hasn't changed
                if (ApiService.CurrentWorkspaceId != _workspaceIdWhenStarted)
                {
                    Debug.WriteLine($"⚠️ ABORT: Workspace changed! FileWatcher started with {_workspaceIdWhenStarted}, now {ApiService.CurrentWorkspaceId}");
                    return;
                }
                
                // CRITICAL: Add delay to let parent folders be created first
                await Task.Delay(1000);
                
                // Check if folder already exists in database
                var allFiles = await _localDb.GetAllFilesAsync();
                var existingFolder = allFiles.FirstOrDefault(f => 
                    f.LocalPath == folderPath && f.Type == "folder");
                
                if (existingFolder != null)
                {
                    Debug.WriteLine($"⏭️ FILEWATCHER: Folder already exists in DB: {existingFolder.Name} (ID: {existingFolder.Id})");
                    return;
                }
                
                // CRITICAL: Wait for parent folder to be created and saved to database
                var parentId = await GetParentFolderIdWithRetryAsync(folderPath);
                Debug.WriteLine($"🔍 FILEWATCHER: Final parent ID for '{folderName}': {parentId}");
                
                // Refresh database after parent creation
                allFiles = await _localDb.GetAllFilesAsync();
                
                // Check for duplicate folder with same name and parent
                var duplicateFolder = allFiles.FirstOrDefault(f => 
                    f.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase) && 
                    f.Type == "folder" &&
                    f.ParentId == parentId);
                
                if (duplicateFolder != null)
                {
                    Debug.WriteLine($"🔄 FILEWATCHER: Found existing folder with same name and parent: {duplicateFolder.Name} (ID: {duplicateFolder.Id})");
                    Debug.WriteLine($"   Updating local path to match new physical location");
                    
                    // Update the existing folder's local path
                    duplicateFolder.LocalPath = folderPath;
                    await _localDb.UpdateFileAsync(duplicateFolder);
                    Debug.WriteLine($"✅ FILEWATCHER: Updated folder path mapping");
                    return;
                }
                
                // Create new folder on server
                Debug.WriteLine($"🆕 FILEWATCHER: Creating new folder on server: '{folderName}' (parent: {parentId})");
                
                try
                {
                    var folder = await _apiService.CreateFolderAsync(folderName, parentId);
                    
                    if (folder != null)
                    {
                        var localEntry = LocalFileEntry.FromFileEntry(folder, folderPath);
                        await _localDb.SaveFileAsync(localEntry);
                        Debug.WriteLine($"✅ FILEWATCHER: Folder created and synced: {folderName} (ID: {folder.Id}, Parent: {folder.ParentId})");
                        
                        // CRITICAL: Wait a bit to ensure database is updated before other operations
                        await Task.Delay(500);
                    }
                    else
                    {
                        Debug.WriteLine($"❌ FILEWATCHER: Server returned null for folder creation: {folderName}");
                    }
                }
                catch (Exception ex) when (ex.Message.Contains("already exists"))
                {
                    Debug.WriteLine($"⚠️ FILEWATCHER: Folder already exists on server: {folderName}");
                    Debug.WriteLine($"   This might be due to concurrent operations or sync conflicts");
                    
                    // Try to find the existing folder and update our database
                    await Task.Delay(1000); // Give more time for database to update
                    allFiles = await _localDb.GetAllFilesAsync();
                    
                    var serverFolder = allFiles.FirstOrDefault(f => 
                        f.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase) && 
                        f.Type == "folder" &&
                        f.ParentId == parentId);
                    
                    if (serverFolder != null)
                    {
                        serverFolder.LocalPath = folderPath;
                        await _localDb.UpdateFileAsync(serverFolder);
                        Debug.WriteLine($"✅ FILEWATCHER: Mapped existing server folder to local path");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ FILEWATCHER: Failed to create folder '{Path.GetFileName(folderPath)}': {ex.Message}");
                Debug.WriteLine($"   Stack trace: {ex.StackTrace}");
            }
        }

        // Handle file changed
        private async Task HandleFileChangedAsync(string filePath)
        {
            try
            {
                var fileName = Path.GetFileName(filePath);
                Debug.WriteLine($"🔄 Processing changed file: {fileName}");
                
                // CRITICAL: Prevent concurrent processing of same file
                lock (_filesBeingProcessed)
                {
                    if (_filesBeingProcessed.Contains(filePath))
                    {
                        Debug.WriteLine($"⏭️ SKIP: File already being processed: {fileName}");
                        return;
                    }
                    _filesBeingProcessed.Add(filePath);
                }

                try
                {
                    // CRITICAL: Verify workspace hasn't changed
                    if (ApiService.CurrentWorkspaceId != _workspaceIdWhenStarted)
                    {
                        Debug.WriteLine($"⚠️ ABORT: Workspace changed! FileWatcher started with {_workspaceIdWhenStarted}, now {ApiService.CurrentWorkspaceId}");
                        return;
                    }

                    // CRITICAL: Debouncing - check if file was processed recently
                    if (_lastProcessedTime.TryGetValue(filePath, out DateTime lastTime))
                    {
                        var timeSinceLastProcess = DateTime.Now - lastTime;
                        if (timeSinceLastProcess < _debounceDelay)
                        {
                            Debug.WriteLine($"⏭️ SKIP: File processed recently ({timeSinceLastProcess.TotalSeconds:F1}s ago): {fileName}");
                            return;
                        }
                    }

                    // Update last processed time
                    _lastProcessedTime[filePath] = DateTime.Now;

                    // STEP 1: Skip system files and hidden files
                    if (fileName.StartsWith(".") || fileName == "desktop.ini" || fileName == "Thumbs.db" ||
                        fileName.StartsWith("~$") || fileName.EndsWith(".tmp") || fileName.EndsWith(".temp"))
                    {
                        Debug.WriteLine($"⏭️ SKIP: System/hidden file: {fileName}");
                        return;
                    }

                    // STEP 2: Check if file was recently downloaded (don't re-upload downloaded files)
                    if (_syncService != null && _syncService.IsFileRecentlyDownloaded(filePath))
                    {
                        Debug.WriteLine($"⏭️ SKIP: File was recently downloaded, not re-uploading: {fileName}");
                        return;
                    }

                    // STEP 2: Find existing file in database
                    var allFiles = await _localDb.GetAllFilesAsync();
                    var existingFile = allFiles.FirstOrDefault(f => f.LocalPath == filePath);

                    if (existingFile != null)
                    {
                        // STEP 3: Check if file actually changed (compare size and timestamp)
                        var fileInfo = new FileInfo(filePath);
                        var timeDiff = Math.Abs((fileInfo.LastWriteTimeUtc - (existingFile.UpdatedAt?.ToUniversalTime() ?? DateTime.MinValue)).TotalSeconds);

                        if (existingFile.FileSize == fileInfo.Length && timeDiff < 5)
                        {
                            Debug.WriteLine($"⏭️ SKIP: File hasn't actually changed: {fileName} (size and time match)");
                            return;
                        }

                        Debug.WriteLine($"🔄 File genuinely changed: {fileName}");
                        Debug.WriteLine($"   Size: {existingFile.FileSize} → {fileInfo.Length}");
                        Debug.WriteLine($"   Time diff: {timeDiff:F1}s");

                        // STEP 4: Use the existing file's parent ID to maintain folder hierarchy
                        var originalParentId = existingFile.ParentId;
                        Debug.WriteLine($"🔍 Original parent ID: {originalParentId}");

                        // STEP 5: Delete old version and upload new version
                        await _apiService.DeleteFileAsync(existingFile.Id);
                        var uploadedFile = await _apiService.UploadFileAsync(filePath, originalParentId);

                        if (uploadedFile != null)
                        {
                            var localEntry = LocalFileEntry.FromFileEntry(uploadedFile, filePath);
                            await _localDb.SaveFileAsync(localEntry);
                            Debug.WriteLine($"✅ File updated in correct folder: {uploadedFile.Name} (parent: {originalParentId})");

                            if (OnFileChanged != null)
                                await OnFileChanged.Invoke(filePath);
                        }
                    }
                    else
                    {
                        // File not in database - treat as new file
                        Debug.WriteLine($"⚠️ File not found in DB, treating as new file: {fileName}");
                        await HandleFileAddedAsync(filePath);
                    }
                }
                finally
                {
                    // CRITICAL: Always remove from processing list
                    lock (_filesBeingProcessed)
                    {
                        _filesBeingProcessed.Remove(filePath);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Failed to update file: {ex.Message}");
            }
        }

        // Handle file deleted
        private async Task HandleFileDeletedAsync(string filePath)
        {
            try
            {
                // CRITICAL: Verify workspace hasn't changed
                if (ApiService.CurrentWorkspaceId != _workspaceIdWhenStarted)
                {
                    Debug.WriteLine($"⚠️ ABORT: Workspace changed! FileWatcher started with {_workspaceIdWhenStarted}, now {ApiService.CurrentWorkspaceId}");
                    return;
                }
                
                // Find file in database
                var allFiles = await _localDb.GetAllFilesAsync();
                var existingFile = allFiles.FirstOrDefault(f => f.LocalPath == filePath);
                
                if (existingFile != null)
                {
                    // Delete from server
                    await _apiService.DeleteFileAsync(existingFile.Id);
                    
                    // Delete from local database
                    await _localDb.DeleteFileAsync(existingFile.Id);
                    
                    Debug.WriteLine($"✅ File deleted from server: {existingFile.Name}");
                    
                    if (OnFileDeleted != null)
                        await OnFileDeleted.Invoke(filePath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Failed to delete file: {ex.Message}");
            }
        }

        // Handle file renamed
        private async Task HandleFileRenamedAsync(string oldPath, string newPath)
        {
            try
            {
                // CRITICAL: Verify workspace hasn't changed
                if (ApiService.CurrentWorkspaceId != _workspaceIdWhenStarted)
                {
                    Debug.WriteLine($"⚠️ ABORT: Workspace changed! FileWatcher started with {_workspaceIdWhenStarted}, now {ApiService.CurrentWorkspaceId}");
                    return;
                }
                
                var allFiles = await _localDb.GetAllFilesAsync();
                var existingFile = allFiles.FirstOrDefault(f => f.LocalPath == oldPath);
                
                if (existingFile != null)
                {
                    // WORKSPACE RESTRICTION: Prevent folder renaming in sync mode
                    if (existingFile.Type == "folder")
                    {
                        var oldName = Path.GetFileName(oldPath);
                        var newName = Path.GetFileName(newPath);
                        
                        Debug.WriteLine($"🚫 FILEWATCHER: Folder rename blocked in sync mode");
                        Debug.WriteLine($"   Folder '{oldName}' → '{newName}' will not be synced");
                        Debug.WriteLine($"   Folders should be managed through the workspace system");
                        
                        // Show user notification about the restriction
                        try
                        {
                            await Application.Current.MainPage.DisplayAlert(
                                "Folder Rename Restricted", 
                                $"The folder rename '{oldName}' → '{newName}' was detected but will not be synced to the server.\n\n" +
                                "In sync mode, folders are managed through the workspace system. " +
                                "Please use the web interface to rename folders.", 
                                "OK");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"⚠️ Could not show notification: {ex.Message}");
                        }
                        
                        // Revert the local rename to prevent confusion
                        try
                        {
                            if (Directory.Exists(newPath) && !Directory.Exists(oldPath))
                            {
                                Directory.Move(newPath, oldPath);
                                Debug.WriteLine($"🔄 Reverted folder rename to maintain sync consistency");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"⚠️ Could not revert folder rename: {ex.Message}");
                        }
                        
                        return;
                    }
                    
                    var fileName = Path.GetFileName(newPath);
                    
                    // Rename on server (only for files, not folders)
                    var success = await _apiService.RenameFileAsync(existingFile.Id, fileName);
                    
                    if (success)
                    {
                        // Update local database
                        existingFile.Name = fileName;
                        existingFile.LocalPath = newPath;
                        await _localDb.UpdateFileAsync(existingFile);
                        
                        Debug.WriteLine($"✅ File renamed: {fileName}");
                        
                        if (OnFileRenamed != null)
                            await OnFileRenamed.Invoke(oldPath, newPath);
                    }
                }
                else
                {
                    // File not found in DB - treat as new creation with the new name
                    Debug.WriteLine($"⚠️ Quick rename: '{Path.GetFileName(oldPath)}' → '{Path.GetFileName(newPath)}' (treating as new)");
                    
                    if (Directory.Exists(newPath))
                    {
                        await HandleFolderAddedAsync(newPath);
                    }
                    else if (File.Exists(newPath))
                    {
                        await HandleFileAddedAsync(newPath);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Failed to rename file: {ex.Message}");
            }
        }

        // Debouncing helper - prevents processing same file multiple times
        private bool ShouldProcessFile(string filePath)
        {
            var now = DateTime.Now;
            
            // Ignore files that existed before FileWatcher started (within 10 seconds grace period)
            if (File.Exists(filePath))
            {
                var fileModifiedTime = File.GetLastWriteTime(filePath);
                var timeSinceWatcherStart = now - _watcherStartTime;
                var timeSinceFileModified = now - fileModifiedTime;
                
                // If file was modified before watcher started, ignore it
                if (fileModifiedTime < _watcherStartTime && timeSinceWatcherStart < TimeSpan.FromSeconds(10))
                {
                    Debug.WriteLine($"⏭️ Ignoring pre-existing file: {Path.GetFileName(filePath)}");
                    return false;
                }
            }
            
            if (_lastProcessedTime.TryGetValue(filePath, out var lastTime))
            {
                if (now - lastTime < _debounceDelay)
                {
                    return false; // Too soon, skip
                }
            }
            
            _lastProcessedTime[filePath] = now;
            return true;
        }

        // Get parent folder ID with enhanced retry mechanism for complex structures
        private async Task<int?> GetParentFolderIdWithRetryAsync(string filePath)
        {
            const int maxRetries = 5; // Increased retries for complex folder structures
            
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                var parentId = await GetParentFolderIdAsync(filePath);
                
                if (parentId != null || attempt == maxRetries)
                {
                    if (parentId != null)
                    {
                        Debug.WriteLine($"✅ Parent folder found on attempt {attempt}: ID={parentId}");
                    }
                    return parentId;
                }
                
                Debug.WriteLine($"🔄 Retry {attempt}/{maxRetries}: Waiting for parent folder to be created...");
                
                // Progressive delay with longer waits for complex structures
                var delay = Math.Min(3000 * attempt, 15000); // Max 15 seconds
                await Task.Delay(delay);
                
                // Force refresh database on later attempts
                if (attempt >= 3)
                {
                    Debug.WriteLine($"🔄 Attempt {attempt}: Forcing database refresh...");
                    // This will be handled by the GetParentFolderIdAsync method
                }
            }
            
            return null;
        }

        // Get parent folder ID from path
        private async Task<int?> GetParentFolderIdAsync(string filePath)
        {
            if (string.IsNullOrEmpty(_syncFolderPath))
                return null;

            var relativePath = Path.GetRelativePath(_syncFolderPath, filePath);
            var parentPath = Path.GetDirectoryName(relativePath);
            
            if (string.IsNullOrEmpty(parentPath) || parentPath == ".")
            {
                Debug.WriteLine($"🏠 Item '{Path.GetFileName(filePath)}' belongs to Root folder");
                return null; // Root folder
            }
            
            Debug.WriteLine($"🔍 FILEWATCHER: Looking for parent folder: '{parentPath}' for item: '{Path.GetFileName(filePath)}'");
            
            // STEP 1: Try to find parent folder in database first
            var fullParentPath = Path.Combine(_syncFolderPath, parentPath);
            var allFiles = await _localDb.GetAllFilesAsync();
            var parentFolder = allFiles.FirstOrDefault(f => 
                f.LocalPath == fullParentPath && f.Type == "folder");
            
            if (parentFolder != null)
            {
                Debug.WriteLine($"✅ FILEWATCHER: Found parent in DB: {parentFolder.Name} (ID: {parentFolder.Id})");
                return parentFolder.Id;
            }
            
            // STEP 2: Check if parent folder exists physically but not in DB
            if (Directory.Exists(fullParentPath))
            {
                Debug.WriteLine($"📁 FILEWATCHER: Parent folder exists physically but not in DB, creating hierarchy: {parentPath}");
                return await CreateParentFolderHierarchyAsync(parentPath);
            }
            
            // STEP 3: Parent doesn't exist at all - this shouldn't happen in normal file operations
            Debug.WriteLine($"❌ FILEWATCHER: Parent folder doesn't exist: {parentPath}");
            Debug.WriteLine($"   This suggests the folder structure is being created out of order");
            Debug.WriteLine($"   Deferring creation until parent exists");
            
            return null;
        }

        // Create parent folder hierarchy if it doesn't exist - ENHANCED for complex structures
        private async Task<int?> CreateParentFolderHierarchyAsync(string relativePath)
        {
            if (string.IsNullOrEmpty(_syncFolderPath))
                return null;

            var pathParts = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            int? currentParentId = null;
            string currentPath = "";

            Debug.WriteLine($"🏗️ ENHANCED HIERARCHY: Creating folder hierarchy for path: {relativePath}");
            Debug.WriteLine($"   Path parts: [{string.Join(", ", pathParts)}]");

            for (int i = 0; i < pathParts.Length; i++)
            {
                var part = pathParts[i];
                currentPath = string.IsNullOrEmpty(currentPath) ? part : Path.Combine(currentPath, part);
                var fullPath = Path.Combine(_syncFolderPath, currentPath);
                
                Debug.WriteLine($"📁 ENHANCED: Processing folder part {i + 1}/{pathParts.Length}: '{part}'");
                Debug.WriteLine($"   Current path: {currentPath}");
                Debug.WriteLine($"   Full path: {fullPath}");
                Debug.WriteLine($"   Expected parent ID: {currentParentId}");
                
                // CRITICAL: Multiple attempts to handle race conditions
                int? folderIdResult = null;
                
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        // Refresh database to get latest folders (important for concurrent operations)
                        var allFiles = await _localDb.GetAllFilesAsync();
                        
                        // Check if folder exists in database with correct parent
                        var existingFolder = allFiles.FirstOrDefault(f => 
                            f.LocalPath == fullPath && 
                            f.Type == "folder" &&
                            f.ParentId == currentParentId);
                        
                        if (existingFolder != null)
                        {
                            Debug.WriteLine($"✅ ENHANCED: Folder exists in DB: {existingFolder.Name} (ID: {existingFolder.Id}, Parent: {existingFolder.ParentId})");
                            folderIdResult = existingFolder.Id;
                            break;
                        }
                        
                        // Check if folder exists with wrong parent (duplicate prevention)
                        var duplicateFolder = allFiles.FirstOrDefault(f => 
                            f.Name.Equals(part, StringComparison.OrdinalIgnoreCase) && 
                            f.Type == "folder" &&
                            f.ParentId == currentParentId);
                        
                        if (duplicateFolder != null)
                        {
                            Debug.WriteLine($"🔄 ENHANCED: Found existing folder with same name and parent: {duplicateFolder.Name} (ID: {duplicateFolder.Id})");
                            Debug.WriteLine($"   Updating local path to match physical structure");
                            
                            // Update the local path to match the physical folder
                            duplicateFolder.LocalPath = fullPath;
                            await _localDb.UpdateFileAsync(duplicateFolder);
                            
                            folderIdResult = duplicateFolder.Id;
                            break;
                        }
                        
                        // Create folder on server with correct parent
                        Debug.WriteLine($"🆕 ENHANCED: Creating folder on server: '{part}' (parent: {currentParentId}) - Attempt {attempt}");
                        
                        var createdFolder = await _apiService.CreateFolderAsync(part, currentParentId);
                        
                        if (createdFolder != null)
                        {
                            // Save to local database
                            var localEntry = LocalFileEntry.FromFileEntry(createdFolder, fullPath);
                            await _localDb.SaveFileAsync(localEntry);
                            
                            folderIdResult = createdFolder.Id;
                            Debug.WriteLine($"✅ ENHANCED: Created and saved folder: {createdFolder.Name} (ID: {createdFolder.Id}, Parent: {createdFolder.ParentId})");
                            
                            // CRITICAL: Wait to ensure database is updated before next folder
                            await Task.Delay(2000); // Increased delay for complex structures
                            break;
                        }
                        else
                        {
                            Debug.WriteLine($"❌ ENHANCED: Failed to create folder: {part} (attempt {attempt})");
                            if (attempt < 3)
                            {
                                await Task.Delay(1000 * attempt); // Progressive delay
                                continue;
                            }
                            return null;
                        }
                    }
                    catch (Exception ex) when (ex.Message.Contains("already exists"))
                    {
                        Debug.WriteLine($"⚠️ ENHANCED: Folder already exists on server: {part} (attempt {attempt})");
                        Debug.WriteLine($"   Attempting to find it in the database...");
                        
                        // Refresh database and try to find the folder again
                        await Task.Delay(1000); // Give more time for other operations to complete
                        var refreshedFiles = await _localDb.GetAllFilesAsync();
                        
                        var foundFolder = refreshedFiles.FirstOrDefault(f => 
                            f.Name.Equals(part, StringComparison.OrdinalIgnoreCase) && 
                            f.Type == "folder" &&
                            f.ParentId == currentParentId);
                        
                        if (foundFolder != null)
                        {
                            folderIdResult = foundFolder.Id;
                            Debug.WriteLine($"✅ ENHANCED: Found existing folder in DB: {foundFolder.Name} (ID: {foundFolder.Id})");
                            break;
                        }
                        else if (attempt < 3)
                        {
                            Debug.WriteLine($"🔄 ENHANCED: Folder not found in DB yet, retrying... (attempt {attempt})");
                            await Task.Delay(2000 * attempt); // Progressive delay
                            continue;
                        }
                        else
                        {
                            Debug.WriteLine($"❌ ENHANCED: Could not resolve folder conflict for: {part}");
                            return null;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"❌ ENHANCED: Error creating folder '{part}' (attempt {attempt}): {ex.Message}");
                        if (attempt < 3)
                        {
                            await Task.Delay(1000 * attempt);
                            continue;
                        }
                        throw;
                    }
                }
                
                if (folderIdResult == null)
                {
                    Debug.WriteLine($"❌ ENHANCED: Failed to create or find folder: {part}");
                    return null;
                }
                
                currentParentId = folderIdResult;
            }
            
            Debug.WriteLine($"🏗️ ENHANCED: Hierarchy creation complete. Final parent ID: {currentParentId}");
            return currentParentId;
        }

        public bool IsWatching => _isWatching;
        
        public string? GetWatchedPath() => _syncFolderPath;
        
        /// <summary>
        /// Test method to verify FileWatcher is working by creating a test file
        /// </summary>
        public async Task<bool> TestFileWatcherAsync()
        {
            if (!_isWatching || string.IsNullOrEmpty(_syncFolderPath))
            {
                Debug.WriteLine("❌ FileWatcher not running - cannot test");
                return false;
            }
            
            try
            {
                var testFile = Path.Combine(_syncFolderPath, ".okcloud_test_" + DateTime.Now.Ticks + ".txt");
                Debug.WriteLine($"🧪 Testing FileWatcher with: {Path.GetFileName(testFile)}");
                
                // Create test file
                await File.WriteAllTextAsync(testFile, "FileWatcher test");
                
                // Wait a moment
                await Task.Delay(500);
                
                // Delete test file
                if (File.Exists(testFile))
                {
                    File.Delete(testFile);
                }
                
                Debug.WriteLine("✅ FileWatcher test completed");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ FileWatcher test failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Scan for existing files that were added before FileWatcher started
        /// DISABLED: This was causing duplicate uploads during sync
        /// </summary>
        private async Task PerformInitialScanAsync()
        {
            try
            {
                Debug.WriteLine("🚫 INITIAL SCAN DISABLED: Preventing duplicate uploads during sync");
                Debug.WriteLine("ℹ️ SyncService handles initial file uploads - FileWatcher only monitors new changes");
                
                // Just log the status, don't upload anything
                if (string.IsNullOrEmpty(_syncFolderPath) || !Directory.Exists(_syncFolderPath))
                {
                    Debug.WriteLine("❌ Cannot perform scan - sync folder not available");
                    return;
                }
                
                var allFiles = Directory.GetFiles(_syncFolderPath, "*", SearchOption.AllDirectories);
                Debug.WriteLine($"📄 Found {allFiles.Length} total files in sync folder (monitoring only)");
                
                Debug.WriteLine("✅ Initial scan completed (monitoring mode)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Initial scan failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle file added directly (bypass debouncing for initial scan)
        /// </summary>
        private async Task HandleFileAddedDirectAsync(string filePath)
        {
            try
            {
                var fileName = Path.GetFileName(filePath);
                Debug.WriteLine($"⬆️ DIRECT: Processing file: {fileName}");
                
                // CRITICAL: Verify workspace hasn't changed
                if (ApiService.CurrentWorkspaceId != _workspaceIdWhenStarted)
                {
                    Debug.WriteLine($"⚠️ ABORT: Workspace changed during initial scan");
                    return;
                }
                
                // Check if file already exists in local database
                var allFiles = await _localDb.GetAllFilesAsync();
                var existingFile = allFiles.FirstOrDefault(f => f.LocalPath == filePath);
                
                if (existingFile != null)
                {
                    Debug.WriteLine($"⏭️ DIRECT: File already exists in database: {fileName}");
                    return;
                }
                
                // Get parent folder ID
                var parentId = await GetParentFolderIdAsync(filePath);
                Debug.WriteLine($"🔍 DIRECT: Parent ID for {fileName}: {parentId}");
                
                // Check for duplicates by name, parent, and size
                var fileInfo = new FileInfo(filePath);
                var duplicateFile = allFiles.FirstOrDefault(f => 
                    f.Name == fileName && 
                    f.ParentId == parentId && 
                    f.FileSize == fileInfo.Length);
                
                if (duplicateFile != null)
                {
                    Debug.WriteLine($"⏭️ DIRECT: Duplicate file detected, updating path: {fileName}");
                    duplicateFile.LocalPath = filePath;
                    await _localDb.UpdateFileAsync(duplicateFile);
                    return;
                }
                
                // Upload the file
                Debug.WriteLine($"🚀 DIRECT: Uploading file: {fileName}");
                var uploadedFile = await _apiService.UploadFileAsync(filePath, parentId);
                
                if (uploadedFile != null)
                {
                    var localEntry = LocalFileEntry.FromFileEntry(uploadedFile, filePath);
                    await _localDb.SaveFileAsync(localEntry);
                    Debug.WriteLine($"✅ DIRECT: File uploaded successfully: {fileName}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ DIRECT: Failed to upload {Path.GetFileName(filePath)}: {ex.Message}");
            }
        }

        private void CleanupOldDebounceEntries()
        {
            try
            {
                var cutoffTime = DateTime.Now.AddMinutes(-10);
                var keysToRemove = new List<string>();
                
                foreach (var kvp in _lastProcessedTime)
                {
                    if (kvp.Value < cutoffTime)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }
                
                foreach (var key in keysToRemove)
                {
                    _lastProcessedTime.Remove(key);
                }
                
                if (keysToRemove.Count > 0)
                {
                    Debug.WriteLine($"🧹 Cleaned up {keysToRemove.Count} old debounce entries");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠️ Error cleaning up debounce entries: {ex.Message}");
            }
        }

        public void Dispose()
        {
            StopWatching();
        }
    }
}
