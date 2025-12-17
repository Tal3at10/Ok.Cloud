using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net;
using Microsoft.Maui.Storage;

namespace OkCloud.Client.Services
{
    public class UploadProgress
    {
        public string FileName { get; set; } = string.Empty;
        public long TotalBytes { get; set; }
        public long UploadedBytes { get; set; }
        public int Percentage => TotalBytes > 0 ? (int)((UploadedBytes * 100) / TotalBytes) : 0;
        public double SpeedMBps { get; set; }
        public TimeSpan EstimatedTimeRemaining { get; set; }
        public bool IsCompleted { get; set; }
        public bool IsCancelled { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class UploadService
    {
        private readonly HttpClient _httpClient;
        private const string BaseUrl = "https://cloud.oksite.se/api/v1/";
        private const int BufferSize = 81920; // 80 KB buffer for better performance
        private CancellationTokenSource? _cancellationTokenSource;

        public event Action<UploadProgress>? OnProgressChanged;

        public UploadService()
        {
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = true,
                // CRITICAL: Enable automatic decompression for better performance
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                // Increase connection limits for faster parallel uploads (like Google Drive)
                MaxConnectionsPerServer = 50
            };

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(BaseUrl),
                Timeout = TimeSpan.FromMinutes(30),
                // Increase max response content buffer size
                MaxResponseContentBufferSize = 10 * 1024 * 1024 // 10MB
            };
            
            // Add performance headers
            _httpClient.DefaultRequestHeaders.ConnectionClose = false; // Keep connection alive
            _httpClient.DefaultRequestHeaders.ExpectContinue = false; // Disable 100-Continue
        }

        public async Task<bool> UploadFileWithProgressAsync(string filePath, int? parentId = null)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var progress = new UploadProgress
            {
                FileName = Path.GetFileName(filePath)
            };

            try
            {
                var fileInfo = new FileInfo(filePath);
                progress.TotalBytes = fileInfo.Length;

                Debug.WriteLine($"🚀 Starting upload with progress: {progress.FileName} ({progress.TotalBytes} bytes)");

                // Log file size for diagnostics
                var fileSizeMB = fileInfo.Length / 1024.0 / 1024.0;
                Debug.WriteLine($"📊 File size: {fileSizeMB:F2} MB");

                // Get auth cookies
                var cookies = await SecureStorage.Default.GetAsync("auth_cookies") ?? "";
                var xsrfToken = ExtractXsrfToken(cookies);

                var stopwatch = Stopwatch.StartNew();

                // Stream the file directly (no memory loading)
                var uploadSuccess = await UploadFileAsync(filePath, progress.FileName, cookies, xsrfToken, parentId, progress, stopwatch);
                
                if (!uploadSuccess)
                {
                    progress.ErrorMessage = "Upload failed";
                    OnProgressChanged?.Invoke(progress);
                    return false;
                }

                progress.IsCompleted = true;
                progress.UploadedBytes = progress.TotalBytes; // This will make Percentage = 100 automatically
                OnProgressChanged?.Invoke(progress);
                Debug.WriteLine($"✅ Upload completed: {progress.FileName}");
                return true;
            }
            catch (OperationCanceledException)
            {
                progress.IsCancelled = true;
                OnProgressChanged?.Invoke(progress);
                Debug.WriteLine($"⚠️ Upload cancelled: {progress.FileName}");
                return false;
            }
            catch (Exception ex)
            {
                var errorMsg = ex.Message;
                var fileSizeMB = progress.TotalBytes / 1024.0 / 1024.0;
                
                // Provide user-friendly error messages
                if (errorMsg.Contains("copying content to a stream") || errorMsg.Contains("Network error") || errorMsg.Contains("forcibly closed"))
                {
                    if (fileSizeMB > 100)
                    {
                        errorMsg = $"Upload failed - server rejected file ({fileSizeMB:F0} MB).\n\n" +
                                  "The server has strict upload limits (typically 100 MB or less).\n\n" +
                                  "Please use the web interface for files over 100 MB.";
                    }
                    else if (fileSizeMB > 50)
                    {
                        errorMsg = $"Upload failed - file may be too large ({fileSizeMB:F0} MB).\n\n" +
                                  "Try using the web interface for better results.";
                    }
                    else
                    {
                        errorMsg = "Upload failed due to network error. Please check your connection and try again.";
                    }
                }
                else if (errorMsg.Contains("timeout") || errorMsg.Contains("timed out"))
                {
                    errorMsg = $"Upload timed out after {progress.UploadedBytes / 1024.0 / 1024.0:F0} MB.\n\n" +
                              "Your connection may be too slow for this file size. Try a smaller file or use a faster connection.";
                }
                else if (errorMsg.Contains("File read error"))
                {
                    errorMsg = "Cannot read the file. It may be in use by another program.";
                }
                
                progress.ErrorMessage = errorMsg;
                OnProgressChanged?.Invoke(progress);
                Debug.WriteLine($"❌ Upload error: {ex.Message}");
                Debug.WriteLine($"   Type: {ex.GetType().Name}");
                Debug.WriteLine($"   File size: {fileSizeMB:F2} MB");
                Debug.WriteLine($"   Uploaded: {progress.UploadedBytes / 1024.0 / 1024.0:F2} MB");
                return false;
            }
        }

        private async Task<bool> UploadFileAsync(string filePath, string fileName, string cookies, string? xsrfToken, int? parentId, UploadProgress progress, Stopwatch stopwatch)
        {
            FileStream? fileStream = null;
            try
            {
                // Open file stream with larger buffer for better performance
                fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 524288, useAsync: true); // 512KB buffer لتحسين الأداء
                var streamContent = new StreamContent(fileStream, 524288); // Match buffer size
                streamContent.Headers.ContentType = new MediaTypeHeaderValue(GetMimeType(fileName));
                
                // Wrap with progress tracking
                var fileContent = new ProgressStreamContent(streamContent, (sent, total) =>
                {
                    progress.UploadedBytes = sent;
                    
                    var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                    if (elapsedSeconds > 0)
                    {
                        progress.SpeedMBps = (sent / 1024.0 / 1024.0) / elapsedSeconds;
                        var remainingBytes = total - sent;
                        var bytesPerSecond = sent / elapsedSeconds;
                        if (bytesPerSecond > 0)
                        {
                            var remainingSeconds = remainingBytes / bytesPerSecond;
                            progress.EstimatedTimeRemaining = TimeSpan.FromSeconds(remainingSeconds);
                        }
                    }
                    
                    OnProgressChanged?.Invoke(progress);
                });

                using var content = new MultipartFormDataContent();
                content.Add(fileContent, "file", fileName);
                content.Add(new StringContent("bedrive"), "uploadType");
                
                if (parentId.HasValue)
                {
                    content.Add(new StringContent(parentId.Value.ToString()), "parentId");
                }

                var request = new HttpRequestMessage(HttpMethod.Post, "uploads")
                {
                    Content = content
                };

                if (!string.IsNullOrEmpty(cookies))
                {
                    request.Headers.Add("Cookie", cookies);
                }
                if (!string.IsNullOrEmpty(xsrfToken))
                {
                    request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
                }

                Debug.WriteLine($"📤 Sending upload request for {fileName}...");
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cancellationTokenSource!.Token);
                
                var responseContent = await response.Content.ReadAsStringAsync();
                Debug.WriteLine($"📥 Upload response: {response.StatusCode}");
                
                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"❌ Upload failed: {responseContent}");
                }
                
                return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException ex)
            {
                Debug.WriteLine($"❌ HTTP error during upload: {ex.Message}");
                Debug.WriteLine($"   Inner exception: {ex.InnerException?.Message}");
                throw new Exception($"Network error: {ex.InnerException?.Message ?? ex.Message}");
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"❌ IO error during upload: {ex.Message}");
                throw new Exception($"File read error: {ex.Message}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Unexpected upload error: {ex.Message}");
                Debug.WriteLine($"   Stack: {ex.StackTrace}");
                throw;
            }
            finally
            {
                fileStream?.Dispose();
            }
        }

        private string GetMimeType(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".txt" => "text/plain",
                ".pdf" => "application/pdf",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".mp4" => "video/mp4",
                ".mp3" => "audio/mpeg",
                ".zip" => "application/zip",
                _ => "application/octet-stream"
            };
        }

        private string? ExtractXsrfToken(string allCookies)
        {
            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(allCookies, "XSRF-TOKEN=([^;]+)");
                if (match.Success)
                {
                    var encodedToken = match.Groups[1].Value;
                    return System.Net.WebUtility.UrlDecode(encodedToken);
                }
            }
            catch { }
            return null;
        }

        public void CancelUpload()
        {
            _cancellationTokenSource?.Cancel();
        }
    }

    // Helper class to track upload progress
    public class ProgressStreamContent : HttpContent
    {
        private readonly HttpContent _content;
        private readonly Action<long, long> _onProgress;
        private const int BufferSize = 524288; // 512 KB buffer لتحسين الأداء

        public ProgressStreamContent(HttpContent content, Action<long, long> onProgress)
        {
            _content = content;
            _onProgress = onProgress;

            // Copy all headers from the original content
            foreach (var header in content.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[BufferSize];
            long totalBytes = _content.Headers.ContentLength ?? 0;
            long uploadedBytes = 0;
            long lastReportedBytes = 0;
            const long REPORT_INTERVAL = 1024 * 1024; // Report progress every 1MB to reduce overhead وتحسين الأداء

            using var contentStream = await _content.ReadAsStreamAsync();
            
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await stream.WriteAsync(buffer, 0, bytesRead);
                uploadedBytes += bytesRead;
                
                // Only report progress every 512KB to reduce UI overhead
                if (uploadedBytes - lastReportedBytes >= REPORT_INTERVAL || uploadedBytes == totalBytes)
                {
                    _onProgress?.Invoke(uploadedBytes, totalBytes);
                    lastReportedBytes = uploadedBytes;
                }
            }
            
            // Final progress report and flush
            _onProgress?.Invoke(uploadedBytes, totalBytes);
            await stream.FlushAsync();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _content.Headers.ContentLength ?? 0;
            return _content.Headers.ContentLength.HasValue;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _content?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
