namespace OkCloud.Client.Models
{
    public enum OperationType
    {
        Upload,
        Delete,
        Rename,
        Move,
        CreateFolder
    }

    public class OfflineOperation
    {
        public int Id { get; set; }
        public OperationType Type { get; set; }
        public string? FilePath { get; set; }
        public string? FileName { get; set; }
        public int? FileId { get; set; }
        public string? NewName { get; set; }
        public int? ParentId { get; set; }
        public DateTime Timestamp { get; set; }
        public string? Data { get; set; } // JSON for additional data
    }
}
