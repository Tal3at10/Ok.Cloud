using System.Text.Json;
using System.Text.Json.Serialization;

namespace OkCloud.Client.Models
{
    // Custom converter to handle thumbnail that can be string, object, or null
    public class ThumbnailConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }
            
            if (reader.TokenType == JsonTokenType.String)
            {
                return reader.GetString();
            }
            
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                // Skip the entire object
                using (JsonDocument doc = JsonDocument.ParseValue(ref reader))
                {
                    // Try to extract 'url' property if it exists
                    if (doc.RootElement.TryGetProperty("url", out JsonElement urlElement))
                    {
                        return urlElement.GetString();
                    }
                }
                return null;
            }
            
            return null;
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(value);
            }
        }
    }
    

    public class User
    {
        [JsonPropertyName("id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int Id { get; set; }

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("avatar")]
        public string? AvatarUrl { get; set; }

        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("default_workspace_id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? DefaultWorkspaceId { get; set; }

        [JsonPropertyName("workspaces")]
        public List<Workspace>? Workspaces { get; set; }
    }

    public class Workspace
    {
        [JsonPropertyName("id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("default")]
        public bool IsDefault { get; set; }
    }

    public class LoginResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("user")]
        public User? User { get; set; }
    }

    public class FileEntry
    {
        [JsonPropertyName("id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("file_name")]
        public string? FileName { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = "file";

        [JsonPropertyName("file_size")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? FileSize { get; set; }

        [JsonPropertyName("mime")]
        public string? Mime { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("thumbnail")]
        [JsonConverter(typeof(ThumbnailConverter))]
        public string? Thumbnail { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("parent_id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? ParentId { get; set; }

        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("created_at")]
        public DateTime? CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTime? UpdatedAt { get; set; }

        [JsonPropertyName("deleted_at")]
        public DateTime? DeletedAt { get; set; }

        [JsonPropertyName("hash")]
        public string? Hash { get; set; }

        [JsonPropertyName("starred")]
        public bool IsStarred { get; set; }

        public bool IsFolder => Type == "folder";
    }

    public class FileListResponse
    {
        [JsonPropertyName("data")]
        public List<FileEntry> Data { get; set; } = new();
    }

    public class SpaceUsage
    {
        [JsonPropertyName("used")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long Used { get; set; }

        [JsonPropertyName("available")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long Available { get; set; }

        public double UsedPercentage => Available > 0 ? (double)Used / Available * 100 : 0;
        
        public string UsedFormatted => FormatBytes(Used);
        public string AvailableFormatted => FormatBytes(Available);

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
            return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
        }
    }

    public class ShareableLink
    {
        [JsonPropertyName("hash")]
        public string Hash { get; set; } = string.Empty;

        [JsonPropertyName("link")]
        public string? Link { get; set; }

        [JsonPropertyName("password")]
        public string? Password { get; set; }

        [JsonPropertyName("expires_at")]
        public DateTime? ExpiresAt { get; set; }

        [JsonPropertyName("allow_download")]
        public bool AllowDownload { get; set; }

        [JsonPropertyName("allow_edit")]
        public bool AllowEdit { get; set; }

        public string Url => Link ?? $"https://cloud.oksite.se/drive/shares/{Hash}";
    }

    public class ShareEntry
    {
        [JsonPropertyName("id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int Id { get; set; }

        [JsonPropertyName("user_id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int UserId { get; set; }

        [JsonPropertyName("email")]
        public string UserEmail { get; set; } = string.Empty;

        [JsonPropertyName("display_name")]
        public string UserName { get; set; } = string.Empty;

        [JsonPropertyName("permissions")]
        public string Permissions { get; set; } = string.Empty;
    }

    public class BreadcrumbItem
    {
        public int? FolderId { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
