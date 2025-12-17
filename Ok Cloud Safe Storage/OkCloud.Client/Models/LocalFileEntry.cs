using SQLite;

namespace OkCloud.Client.Models
{
    [Table("files")]
    public class LocalFileEntry
    {
        [PrimaryKey]
        public int Id { get; set; }
        
        public string Name { get; set; } = string.Empty;
        
        public string? FileName { get; set; }
        
        public string Type { get; set; } = "file";
        
        public long? FileSize { get; set; }
        
        public string? Mime { get; set; }
        
        public string? Hash { get; set; }
        
        public int? ParentId { get; set; }
        
        public string? Path { get; set; }
        
        public string? LocalPath { get; set; }
        
        public DateTime? CreatedAt { get; set; }
        
        public DateTime? UpdatedAt { get; set; }
        
        public bool IsStarred { get; set; }
        
        public bool IsSynced { get; set; }
        
        public DateTime LastSyncedAt { get; set; }

        // Convert from API model
        public static LocalFileEntry FromFileEntry(FileEntry file, string? localPath = null)
        {
            return new LocalFileEntry
            {
                Id = file.Id,
                Name = file.Name,
                FileName = file.FileName,
                Type = file.Type,
                FileSize = file.FileSize,
                Mime = file.Mime,
                Hash = file.Hash,
                ParentId = file.ParentId,
                Path = file.Path,
                LocalPath = localPath,
                CreatedAt = file.CreatedAt,
                UpdatedAt = file.UpdatedAt,
                IsStarred = file.IsStarred,
                IsSynced = true,
                LastSyncedAt = DateTime.UtcNow
            };
        }

        // Convert to API model
        public FileEntry ToFileEntry()
        {
            return new FileEntry
            {
                Id = Id,
                Name = Name,
                FileName = FileName,
                Type = Type,
                FileSize = FileSize,
                Mime = Mime,
                Hash = Hash,
                ParentId = ParentId,
                Path = Path,
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
                IsStarred = IsStarred
            };
        }
    }
}
