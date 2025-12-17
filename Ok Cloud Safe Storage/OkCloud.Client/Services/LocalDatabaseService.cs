using SQLite;
using OkCloud.Client.Models;
using System.Diagnostics;

namespace OkCloud.Client.Services
{
    public class LocalDatabaseService
    {
        private SQLiteAsyncConnection? _database;
        private readonly string _dbPath;

        public LocalDatabaseService()
        {
            _dbPath = Path.Combine(FileSystem.AppDataDirectory, "okcloud.db");
        }

        private async Task InitAsync()
        {
            if (_database != null)
                return;

            _database = new SQLiteAsyncConnection(_dbPath);
            await _database.CreateTableAsync<LocalFileEntry>();
            Debug.WriteLine($"✅ Database initialized at: {_dbPath}");
        }

        // Save or update file
        public async Task SaveFileAsync(LocalFileEntry file)
        {
            await InitAsync();
            var existing = await _database!.Table<LocalFileEntry>().Where(f => f.Id == file.Id).FirstOrDefaultAsync();
            
            if (existing != null)
            {
                await _database.UpdateAsync(file);
            }
            else
            {
                await _database.InsertAsync(file);
            }
        }

        // Save multiple files
        public async Task SaveFilesAsync(List<LocalFileEntry> files)
        {
            await InitAsync();
            await _database!.RunInTransactionAsync(tran =>
            {
                foreach (var file in files)
                {
                    tran.InsertOrReplace(file);
                }
            });
        }

        // Get all files
        public async Task<List<LocalFileEntry>> GetAllFilesAsync()
        {
            await InitAsync();
            return await _database!.Table<LocalFileEntry>().ToListAsync();
        }

        // Get files by parent
        public async Task<List<LocalFileEntry>> GetFilesByParentAsync(int? parentId)
        {
            await InitAsync();
            if (parentId.HasValue)
            {
                return await _database!.Table<LocalFileEntry>()
                    .Where(f => f.ParentId == parentId.Value)
                    .ToListAsync();
            }
            else
            {
                return await _database!.Table<LocalFileEntry>()
                    .Where(f => f.ParentId == null)
                    .ToListAsync();
            }
        }

        // Get starred files
        public async Task<List<LocalFileEntry>> GetStarredFilesAsync()
        {
            await InitAsync();
            return await _database!.Table<LocalFileEntry>()
                .Where(f => f.IsStarred)
                .ToListAsync();
        }

        // Get file by ID
        public async Task<LocalFileEntry?> GetFileByIdAsync(int id)
        {
            await InitAsync();
            return await _database!.Table<LocalFileEntry>()
                .Where(f => f.Id == id)
                .FirstOrDefaultAsync();
        }

        // Delete file
        public async Task DeleteFileAsync(int id)
        {
            await InitAsync();
            await _database!.DeleteAsync<LocalFileEntry>(id);
        }

        // Update file
        public async Task UpdateFileAsync(LocalFileEntry file)
        {
            await InitAsync();
            await _database!.UpdateAsync(file);
        }

        // Search files
        public async Task<List<LocalFileEntry>> SearchFilesAsync(string query)
        {
            await InitAsync();
            return await _database!.Table<LocalFileEntry>()
                .Where(f => f.Name.Contains(query))
                .ToListAsync();
        }

        // Clear all data
        public async Task ClearAllAsync()
        {
            await InitAsync();
            await _database!.DeleteAllAsync<LocalFileEntry>();
        }

        // Delete file by local path
        public async Task DeleteFileByPathAsync(string localPath)
        {
            await InitAsync();
            var file = await _database!.Table<LocalFileEntry>()
                .Where(f => f.LocalPath == localPath)
                .FirstOrDefaultAsync();
            
            if (file != null)
            {
                await _database.DeleteAsync(file);
            }
        }

        // Get database stats
        public async Task<int> GetFileCountAsync()
        {
            await InitAsync();
            return await _database!.Table<LocalFileEntry>().CountAsync();
        }
    }
}
