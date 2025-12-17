using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Maui.Storage;
using OkCloud.Client.Models;
using System.Text.Json;
using System.Net;

namespace OkCloud.Client.Services
{
    public class ApiService
    {
        private readonly HttpClient _httpClient;
        private const string BaseUrl = "https://cloud.oksite.se/api/v1/";
        
        // Store logged-in user email as fallback
        public static string? LoggedInEmail { get; set; }

        public ApiService()
        {
            var handler = new HttpClientHandler
            {
                UseCookies = false, // نتحكم بالكوكيز يدوياً
                AllowAutoRedirect = true, // Allow redirects for downloads
                // CRITICAL: Performance optimizations - increased for faster parallel operations
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                MaxConnectionsPerServer = 50 // Increased from 10 to 50 for faster parallel uploads/downloads (like Google Drive)
            };

            _httpClient = new HttpClient(handler) 
            { 
                BaseAddress = new Uri(BaseUrl),
                Timeout = TimeSpan.FromHours(2), // PERFORMANCE FIX: Increased to 2 hours for large folders and slow networks
                MaxResponseContentBufferSize = 20 * 1024 * 1024 // زيادة إلى 20MB buffer
            };

            // --- إعدادات المحاكاة الكاملة للمتصفح ---
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://cloud.oksite.se/drive");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://cloud.oksite.se");
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            
            // CRITICAL: Performance headers
            _httpClient.DefaultRequestHeaders.ConnectionClose = false; // Keep-Alive
            _httpClient.DefaultRequestHeaders.ExpectContinue = false; // Disable 100-Continue handshake
            
            Debug.WriteLine("HttpClient initialized with browser-like headers and performance optimizations");
        }

        public async Task SetAuthTokenAsync(string token)
        {
            // حفظ التوكن والكوكيز
            if (!string.IsNullOrWhiteSpace(token))
            {
                await SecureStorage.Default.SetAsync("auth_token", token);
            }
            Debug.WriteLine("✅ Auth Data Saved.");
        }

        public async Task<bool> LoadTokenAsync()
        {
            // تحميل الكوكيز للذاكرة
            var cookies = await SecureStorage.Default.GetAsync("auth_cookies");
            if (!string.IsNullOrEmpty(cookies))
            {
                AppBridge.CurrentCookies = cookies;
                return true;
            }
            return false;
        }

        public async Task<bool> ValidateTokenAsync()
        {
            try
            {
                Debug.WriteLine("=== Validating authentication ===");
                
                // Check if we have cookies (preferred method)
                var cookies = await SecureStorage.Default.GetAsync("auth_cookies");
                var hasValidCookies = !string.IsNullOrEmpty(cookies);
                
                Debug.WriteLine($"Has cookies: {hasValidCookies}");
                
                // If no cookies, check if we have a token (fallback)
                if (!hasValidCookies)
                {
                    var token = await SecureStorage.Default.GetAsync("auth_token");
                    if (!string.IsNullOrEmpty(token))
                    {
                        Debug.WriteLine("⚠️ No cookies found, but token exists. This may fail due to API restrictions.");
                        // Return true to allow the attempt, but it might fail later
                        return true;
                    }
                    
                    Debug.WriteLine("❌ No authentication data found");
                    return false;
                }
                
                Debug.WriteLine("✅ Valid cookies found");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Validation error: {ex.Message}");
                return false;
            }
        }
        
        // دالة لفك تشفير XSRF من الكوكيز
        private string? ExtractXsrfToken(string allCookies)
        {
            try
            {
                // نبحث عن XSRF-TOKEN ونفك تشفيره (UrlDecode)
                var match = System.Text.RegularExpressions.Regex.Match(allCookies, "XSRF-TOKEN=([^;]+)");
                if (match.Success)
                {
                    var encodedToken = match.Groups[1].Value;
                    return WebUtility.UrlDecode(encodedToken);
                }
            }
            catch { }
            return null;
        }

        private string GetMimeType(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".txt" => "text/plain",
                ".c" => "text/x-c",
                ".cpp" => "text/x-c++",
                ".cs" => "text/x-csharp",
                ".h" => "text/x-c",
                ".js" => "application/javascript",
                ".json" => "application/json",
                ".html" => "text/html",
                ".css" => "text/css",
                ".xml" => "application/xml",
                ".pdf" => "application/pdf",
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xls" => "application/vnd.ms-excel",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".svg" => "image/svg+xml",
                ".mp3" => "audio/mpeg",
                ".mp4" => "video/mp4",
                ".zip" => "application/zip",
                ".rar" => "application/x-rar-compressed",
                ".dat" => "application/x-game-data", // Game save files
                ".sav" => "application/x-game-save", // Game save files
                ".cfg" => "text/plain", // Configuration files
                _ => "application/octet-stream"
            };
        }

        private string SanitizeFileName(string fileName)
        {
            try
            {
                // Handle Base64 encoded filenames (=?utf-8?B?...?=)
                if (fileName.StartsWith("=?utf-8?B?") && fileName.EndsWith("?="))
                {
                    Debug.WriteLine($"🔧 Decoding Base64 filename: {fileName}");
                    
                    // Extract the Base64 part
                    var base64Part = fileName.Substring(10, fileName.Length - 12); // Remove =?utf-8?B? and ?=
                    
                    try
                    {
                        var decodedBytes = Convert.FromBase64String(base64Part);
                        var decodedName = System.Text.Encoding.UTF8.GetString(decodedBytes);
                        Debug.WriteLine($"🔧 Decoded to: {decodedName}");
                        fileName = decodedName;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"⚠️ Failed to decode Base64 filename: {ex.Message}");
                        // Fallback: use a safe name
                        fileName = $"file_{DateTime.Now:yyyyMMdd_HHmmss}.dat";
                    }
                }
                
                // Remove or replace invalid characters for Windows filenames
                var invalidChars = Path.GetInvalidFileNameChars();
                var sanitized = fileName;
                
                foreach (var invalidChar in invalidChars)
                {
                    sanitized = sanitized.Replace(invalidChar, '_');
                }
                
                // Additional problematic characters
                sanitized = sanitized.Replace(":", "_")
                                   .Replace("*", "_")
                                   .Replace("?", "_")
                                   .Replace("\"", "_")
                                   .Replace("<", "_")
                                   .Replace(">", "_")
                                   .Replace("|", "_");
                
                // Ensure filename isn't too long (Windows limit is 255 characters)
                if (sanitized.Length > 200)
                {
                    var extension = Path.GetExtension(sanitized);
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(sanitized);
                    sanitized = nameWithoutExt.Substring(0, 200 - extension.Length) + extension;
                }
                
                // Ensure filename isn't empty
                if (string.IsNullOrWhiteSpace(sanitized))
                {
                    sanitized = $"file_{DateTime.Now:yyyyMMdd_HHmmss}.dat";
                }
                
                return sanitized;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠️ Error sanitizing filename '{fileName}': {ex.Message}");
                return $"file_{DateTime.Now:yyyyMMdd_HHmmss}.dat";
            }
        }



        public async Task<User?> GetCurrentUserAsync()
        {
            try
            {
                Debug.WriteLine("=== Getting user info ===");
                
                // Get cookies
                string cookies = AppBridge.CurrentCookies;
                if (string.IsNullOrEmpty(cookies))
                {
                    cookies = await SecureStorage.Default.GetAsync("auth_cookies") ?? "";
                }
                
                // Try different user endpoints
                var userEndpoints = new[] { "auth/user", "users/me", "me" };
                
                foreach (var endpoint in userEndpoints)
                {
                    Debug.WriteLine($"Trying: {endpoint}");
                    
                    var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                    
                    // CRITICAL: Add Accept header to get JSON instead of HTML
                    request.Headers.Add("Accept", "application/json");
                    
                    // Add cookies and XSRF token (same as GetFilesAsync)
                    if (!string.IsNullOrEmpty(cookies))
                    {
                        request.Headers.Add("Cookie", cookies);
                        var xsrfToken = ExtractXsrfToken(cookies);
                        if (!string.IsNullOrEmpty(xsrfToken))
                        {
                            request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
                        }
                        // Remove Authorization header to use cookies only
                        _httpClient.DefaultRequestHeaders.Authorization = null;
                        request.Headers.Authorization = null;
                    }
                    
                    var response = await _httpClient.SendAsync(request);
                    var content = await response.Content.ReadAsStringAsync();
                    
                    if (response.IsSuccessStatusCode)
                    {
                        Debug.WriteLine($"✅ User endpoint found: {endpoint}");
                        Debug.WriteLine($"Response: {content.Substring(0, Math.Min(500, content.Length))}");
                        
                        var options = new System.Text.Json.JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                        };
                        
                        var user = System.Text.Json.JsonSerializer.Deserialize<User>(content, options);
                        
                        if (user != null)
                        {
                            Debug.WriteLine($"👤 User parsed: Email={user.Email}, DisplayName={user.DisplayName}");
                        }
                        
                        return user;
                    }
                    else
                    {
                        Debug.WriteLine($"❌ {endpoint}: {response.StatusCode}");
                        Debug.WriteLine($"   Response: {content.Substring(0, Math.Min(200, content.Length))}");
                    }
                }
                
                Debug.WriteLine("All user endpoints failed");
                
                // Fallback: Create a user object with the logged-in email
                if (!string.IsNullOrEmpty(LoggedInEmail))
                {
                    Debug.WriteLine($"📧 Using fallback email: {LoggedInEmail}");
                    return new User
                    {
                        Email = LoggedInEmail,
                        DisplayName = LoggedInEmail.Split('@')[0], // Use part before @ as display name
                        Id = 0
                    };
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception getting user: {ex.Message}");
                
                // Fallback: Try to get saved email
                var savedEmail = await SecureStorage.Default.GetAsync("user_email");
                if (!string.IsNullOrEmpty(savedEmail))
                {
                    Debug.WriteLine($"📧 Using saved email as fallback: {savedEmail}");
                    return new User
                    {
                        Email = savedEmail,
                        DisplayName = savedEmail.Split('@')[0],
                        Id = 0
                    };
                }
                
                return null;
            }
        }

        /// <summary>
        /// Diagnostic method to test API connectivity and discover available endpoints
        /// </summary>
        public async Task<string> DiagnoseApiAsync()
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("=== API DIAGNOSTIC REPORT ===\n");
            
            report.AppendLine($"Base URL: {_httpClient.BaseAddress}");
            report.AppendLine($"Auth Header: {_httpClient.DefaultRequestHeaders.Authorization}\n");
            
            // Test common endpoints
            var testEndpoints = new[]
            {
                "auth/user", "user", "me", "auth/me",
                "drive/file-entries", "mobile/file-entries", "app/file-entries",
                "drive/entries", "files", "drive/files", "file-entries",
                "workspaces", "drive/workspaces"
            };
            
            foreach (var endpoint in testEndpoints)
            {
                try
                {
                    var response = await _httpClient.GetAsync(endpoint);
                    var statusIcon = response.IsSuccessStatusCode ? "✅" : "❌";
                    report.AppendLine($"{statusIcon} {endpoint}: {response.StatusCode}");
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        var preview = content.Length > 100 ? content.Substring(0, 100) + "..." : content;
                        report.AppendLine($"   Response: {preview}");
                    }
                }
                catch (Exception ex)
                {
                    report.AppendLine($"❌ {endpoint}: Exception - {ex.Message}");
                }
                report.AppendLine();
            }
            
            var result = report.ToString();
            Debug.WriteLine(result);
            return result;
        }

        public async Task<string?> LoginWithCredentialsAsync(string email, string password)
        {
            try
            {
                Debug.WriteLine($"=== Logging in with credentials: {email} ===");

                _httpClient.DefaultRequestHeaders.Authorization = null;

                // STEP 1: Get CSRF Cookie first
                Debug.WriteLine("📋 Step 1: Requesting CSRF cookie...");
                var csrfRequest = new HttpRequestMessage(HttpMethod.Get, "https://cloud.oksite.se/sanctum/csrf-cookie");
                var csrfResponse = await _httpClient.SendAsync(csrfRequest);
                
                Debug.WriteLine($"CSRF Response Status: {csrfResponse.StatusCode}");
                
                // Extract cookies from CSRF response
                string allCookies = "";
                if (csrfResponse.Headers.TryGetValues("Set-Cookie", out var csrfCookies))
                {
                    var cookieList = new List<string>();
                    foreach (var cookie in csrfCookies)
                    {
                        var cookiePart = cookie.Split(';')[0].Trim();
                        cookieList.Add(cookiePart);
                        Debug.WriteLine($"  CSRF Cookie: {cookiePart}");
                    }
                    allCookies = string.Join("; ", cookieList);
                }
                
                // Extract XSRF-TOKEN
                var xsrfToken = ExtractXsrfToken(allCookies);
                Debug.WriteLine($"🔐 XSRF-TOKEN extracted: {xsrfToken?.Substring(0, Math.Min(30, xsrfToken?.Length ?? 0))}...");

                // STEP 2: Now send login request with CSRF token
                Debug.WriteLine("📋 Step 2: Sending login request with CSRF token...");
                
                // Save email for later use
                LoggedInEmail = email;
                await SecureStorage.Default.SetAsync("user_email", email);
                
                var loginData = new
                {
                    email = email,
                    password = password,
                    token_name = "OkCloud Desktop Client"
                };

                var loginRequest = new HttpRequestMessage(HttpMethod.Post, "auth/login")
                {
                    Content = JsonContent.Create(loginData)
                };
                
                // Add cookies and XSRF token
                if (!string.IsNullOrEmpty(allCookies))
                {
                    loginRequest.Headers.Add("Cookie", allCookies);
                }
                if (!string.IsNullOrEmpty(xsrfToken))
                {
                    loginRequest.Headers.Add("X-XSRF-TOKEN", xsrfToken);
                }

                var response = await _httpClient.SendAsync(loginRequest);
                var content = await response.Content.ReadAsStringAsync();

                Debug.WriteLine($"Login Response Status: {response.StatusCode}");
                Debug.WriteLine($"Response Content: {content.Substring(0, Math.Min(500, content.Length))}");

                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"❌ Login Failed: {content}");
                    return null;
                }

                // 🔥 CRITICAL: Extract cookies from login response AND combine with CSRF cookies
                var finalCookieList = new List<string>();
                
                // Add CSRF cookies first
                if (!string.IsNullOrEmpty(allCookies))
                {
                    finalCookieList.AddRange(allCookies.Split("; "));
                }
                
                // Add login response cookies
                if (response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
                {
                    Debug.WriteLine("📦 Extracting cookies from login response...");
                    
                    foreach (var setCookie in setCookieHeaders)
                    {
                        var cookiePart = setCookie.Split(';')[0].Trim();
                        Debug.WriteLine($"  Login Cookie: {cookiePart}");
                        
                        // Replace if cookie with same name exists
                        var cookieName = cookiePart.Split('=')[0];
                        finalCookieList.RemoveAll(c => c.StartsWith(cookieName + "="));
                        finalCookieList.Add(cookiePart);
                    }
                }
                
                string cookieString = string.Join("; ", finalCookieList);
                Debug.WriteLine($"✅ Final combined cookies: {cookieString.Substring(0, Math.Min(150, cookieString.Length))}...");
                
                // Save cookies immediately
                await SecureStorage.Default.SetAsync("auth_cookies", cookieString);
                
                // CRITICAL: Also save for Windows Service compatibility
                await SaveForWindowsServiceAsync("auth_cookies", cookieString);
                
                AppBridge.CurrentCookies = cookieString;
                Debug.WriteLine("✅ Cookies saved to SecureStorage");

                // Extract token from JSON response
                using (JsonDocument doc = JsonDocument.Parse(content))
                {
                    JsonElement root = doc.RootElement;
                    string? token = null;

                    // محاولات استخراج التوكن
                    if (root.TryGetProperty("user", out JsonElement userElement) && 
                        userElement.TryGetProperty("access_token", out JsonElement t1))
                    {
                        token = t1.GetString();
                    }
                    else if (root.TryGetProperty("access_token", out JsonElement t2))
                    {
                        token = t2.GetString();
                    }

                    if (!string.IsNullOrEmpty(token))
                    {
                        await SetAuthTokenAsync(token);
                        Debug.WriteLine($"✅ Login successful! Token: {token.Substring(0, Math.Min(20, token.Length))}...");
                        return token;
                    }
                    else
                    {
                        Debug.WriteLine("⚠️ No token found in response, but cookies may be sufficient");
                        // Even without token, if we have cookies, that might be enough
                        return !string.IsNullOrEmpty(cookieString) ? "cookie-auth" : null;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Login error: {ex.Message}");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        public async Task<FileEntry?> UploadFileAsync(string filePath, int? parentId = null)
        {
            const int MAX_RETRIES = 5; // زيادة من 2 إلى 5 للمحاولات
            Exception? lastException = null;
            
            for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
            {
                try
                {
                    if (attempt > 1)
                    {
                        var fileName = Path.GetFileName(filePath);
                        var fileInfo = new FileInfo(filePath);
                        var fileSizeMB = fileInfo.Length / 1024.0 / 1024.0;
                        
                        Debug.WriteLine($"🔄 Retry attempt {attempt}/{MAX_RETRIES} for {fileName} ({fileSizeMB:F1}MB)");
                        
                        // PERFORMANCE FIX: Longer delay for large files (they need more time)
                        // تأخير تدريجي: 2s, 4s, 8s, 16s for small files
                        // تأخير أطول للملفات الكبيرة: 5s, 10s, 20s, 40s
                        var baseDelay = fileSizeMB > 3 ? 5000 : 2000; // 5s for large files, 2s for small
                        var delayMs = Math.Min(baseDelay * (int)Math.Pow(2, attempt - 2), fileSizeMB > 3 ? 40000 : 16000);
                        
                        Debug.WriteLine($"   Waiting {delayMs / 1000.0:F1}s before retry (file size: {fileSizeMB:F1}MB)");
                        await Task.Delay(delayMs);
                    }
                    
                    return await UploadFileInternalAsync(filePath, parentId);
                }
                catch (Exception ex) when (attempt < MAX_RETRIES && IsRetryableError(ex))
                {
                    lastException = ex;
                    Debug.WriteLine($"⚠️ Upload attempt {attempt} failed, will retry: {ex.Message}");
                }
                catch (Exception ex)
                {
                    // Non-retryable error or final attempt
                    Debug.WriteLine($"❌ Upload failed permanently: {ex.Message}");
                    throw;
                }
            }
            
            // All retries failed
            throw lastException ?? new Exception("Upload failed after all retries");
        }

        private bool IsRetryableError(Exception ex)
        {
            // تحسين اكتشاف الأخطاء القابلة للإعادة
            if (ex is HttpRequestException httpEx)
            {
                var message = httpEx.Message.ToLower();
                
                // CRITICAL FIX: UnprocessableEntity (422) can be temporary for large files
                // Server might be overloaded or need more time to process
                if (message.Contains("unprocessableentity") || 
                    message.Contains("unprocessable entity") ||
                    message.Contains("server cannot process"))
                {
                    return true; // Retry - might be temporary server issue
                }
                
                return message.Contains("internal server error") || 
                       message.Contains("timeout") || 
                       message.Contains("timed out") ||
                       message.Contains("network") ||
                       message.Contains("connection") ||
                       message.Contains("connection closed") ||
                       message.Contains("forcibly closed") ||
                       message.Contains("reset") ||
                       message.Contains("broken pipe") ||
                       message.Contains("server error") ||
                       message.Contains("502") || // Bad Gateway
                       message.Contains("503") || // Service Unavailable
                       message.Contains("504");   // Gateway Timeout
            }
            
            // إضافة دعم لأخطاء Socket و IOException
            if (ex is System.Net.Sockets.SocketException || 
                ex is IOException ioEx && (ioEx.Message.ToLower().Contains("network") || ioEx.Message.ToLower().Contains("connection")))
            {
                return true;
            }
            
            // دعم TaskCanceledException (timeout)
            if (ex is TaskCanceledException)
            {
                return true;
            }
            
            return false;
        }

        private async Task<FileEntry?> UploadFileInternalAsync(string filePath, int? parentId = null)
        {
            FileStream? fileStream = null;
            try
            {
                Debug.WriteLine($"=== Uploading file: {filePath} ===");
                
                if (!File.Exists(filePath))
                {
                    Debug.WriteLine("❌ File not found");
                    throw new FileNotFoundException($"File not found: {filePath}");
                }

                var fileName = Path.GetFileName(filePath);
                var originalFileName = fileName;
                
                // Handle files without extension (like game save files)
                if (!Path.HasExtension(fileName))
                {
                    Debug.WriteLine($"📝 File has no extension: {fileName}");
                    
                    // Try different extensions based on file content/name patterns
                    if (fileName.StartsWith("CUP") || fileName.StartsWith("REPLAY") || fileName.StartsWith("EDIT"))
                    {
                        fileName = fileName + ".sav"; // Game save file
                    }
                    else if (fileName.StartsWith("SYSTEM") || fileName.StartsWith("GRAPHICS") || fileName.StartsWith("VERSUS"))
                    {
                        fileName = fileName + ".cfg"; // Configuration file
                    }
                    else
                    {
                        fileName = fileName + ".dat"; // Generic data file
                    }
                    Debug.WriteLine($"📝 Renamed to: {fileName}");
                }

                // ENHANCED DUPLICATE DETECTION: Check if file already exists with multiple checks
                try
                {
                    Debug.WriteLine($"🔍 ENHANCED: Checking for existing file: {fileName} in parent {parentId}");
                    
                    // CRITICAL FIX: Get files from the specific parent folder with retry
                    List<FileEntry> existingFiles = new List<FileEntry>();
                    
                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        try
                        {
                            if (parentId.HasValue && parentId.Value > 0)
                            {
                                // Get files from specific folder
                                existingFiles = await GetFolderContentsAsync(parentId.Value);
                                Debug.WriteLine($"🔍 Attempt {attempt}: Checking in folder {parentId}: found {existingFiles.Count} items");
                            }
                            else
                            {
                                // Get root files
                                existingFiles = await GetFilesAsync();
                                Debug.WriteLine($"🔍 Attempt {attempt}: Checking in root: found {existingFiles.Count} items");
                            }
                            break; // Success, exit retry loop
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"⚠️ Attempt {attempt} failed to get folder contents: {ex.Message}");
                            if (attempt == 2) throw; // Re-throw on final attempt
                            await Task.Delay(200); // Reduced delay for faster retry (200ms instead of 1000ms)
                        }
                    }
                    
                    // CRITICAL: Check for exact filename AND size match in the same parent folder
                    var localFileSize = new FileInfo(filePath).Length;
                    var duplicateFile = existingFiles?.FirstOrDefault(f => 
                        f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase) && 
                        f.Type == "file" &&
                        f.FileSize == localFileSize
                    );
                    
                    if (duplicateFile != null)
                    {
                        Debug.WriteLine($"⚠️ DUPLICATE DETECTED: File '{fileName}' already exists (ID: {duplicateFile.Id}, Parent: {duplicateFile.ParentId}, Size: {duplicateFile.FileSize})");
                        Debug.WriteLine($"✅ Same file name and size ({localFileSize} bytes) - SKIPPING upload to prevent duplicate");
                        Debug.WriteLine($"   Returning existing file: ID={duplicateFile.Id}, Name={duplicateFile.Name}");
                        return duplicateFile;
                    }
                    
                    // ADDITIONAL CHECK: Look for files with similar names (in case of encoding issues)
                    var similarFile = existingFiles?.FirstOrDefault(f => 
                        f.Type == "file" &&
                        f.FileSize == localFileSize &&
                        (f.Name.Contains(Path.GetFileNameWithoutExtension(fileName)) || 
                         Path.GetFileNameWithoutExtension(fileName).Contains(Path.GetFileNameWithoutExtension(f.Name)))
                    );
                    
                    if (similarFile != null)
                    {
                        Debug.WriteLine($"⚠️ SIMILAR FILE DETECTED: '{similarFile.Name}' vs '{fileName}' (same size: {localFileSize})");
                        Debug.WriteLine($"   This might be the same file with encoding differences");
                        Debug.WriteLine($"   Returning existing file to prevent duplicate: ID={similarFile.Id}");
                        return similarFile;
                    }
                    
                    Debug.WriteLine($"✅ No duplicate found in target location (name: {fileName}, size: {localFileSize}), proceeding with upload");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"⚠️ Error checking for duplicates: {ex.Message}");
                    Debug.WriteLine($"🔄 Proceeding with upload anyway...");
                }
                
                var fileInfo = new FileInfo(filePath);
                var fileSizeInBytes = fileInfo.Length;
                var mimeType = GetMimeType(fileName);
                
                Debug.WriteLine($"📦 File: {fileName}, Size: {fileSizeInBytes} bytes, MIME: {mimeType}");
                Debug.WriteLine($"🔍 Upload parentId: {parentId}, Workspace: {CurrentWorkspaceId}");
                
                // NO SIZE LIMITS - Upload any file size
                // The server will handle size limits if needed
                var fileSizeMB = fileSizeInBytes / 1024.0 / 1024.0;
                if (fileSizeMB > 50)
                {
                    Debug.WriteLine($"📦 Large file: {fileName} ({fileSizeMB:F1} MB) - uploading with streaming");
                }
                
                // Check available space before upload
                var spaceUsage = await GetSpaceUsageAsync();
                if (spaceUsage != null)
                {
                    var availableSpace = spaceUsage.Available - spaceUsage.Used;
                    if (fileSizeInBytes > availableSpace)
                    {
                        var availableMB = availableSpace / 1024.0 / 1024.0;
                        var errorMsg = $"Not enough storage space. File size: {fileSizeMB:F2} MB, Available: {availableMB:F2} MB";
                        Debug.WriteLine($"❌ {errorMsg}");
                        throw new InvalidOperationException(errorMsg);
                    }
                }
                
                // Get cookies first
                string cookies = AppBridge.CurrentCookies;
                if (string.IsNullOrEmpty(cookies))
                {
                    cookies = await SecureStorage.Default.GetAsync("auth_cookies") ?? "";
                }

                var xsrfToken = ExtractXsrfToken(cookies);

                // Create multipart form - API expects: file, uploadType, parentId (optional), relativePath (optional)
                using var content = new MultipartFormDataContent();
                
                // CRITICAL FIX: Use streaming instead of loading entire file into memory
                // This prevents memory issues and timeouts for large files
                // زيادة buffer size لتحسين الأداء: 512KB بدلاً من 256KB
                fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 524288, useAsync: true); // 512KB buffer
                var streamContent = new StreamContent(fileStream, 524288); // Match buffer size
                streamContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
                
                // TEMPORARY FIX: Use ASCII-safe filename for upload, but keep original for display
                var uploadFileName = fileName;
                
                // If filename contains Arabic characters, create ASCII-safe version for upload
                if (fileName.Any(c => c > 127))
                {
                    var extension = Path.GetExtension(fileName);
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    uploadFileName = $"arabic_file_{timestamp}{extension}";
                    Debug.WriteLine($"📝 Arabic filename detected: {fileName}");
                    Debug.WriteLine($"📝 Using ASCII-safe upload name: {uploadFileName}");
                }
                
                content.Add(streamContent, "file", uploadFileName);
                
                Debug.WriteLine($"📝 Uploading filename: {uploadFileName}");
                
                // REQUIRED: uploadType must be "bedrive" for BeDrive uploads
                content.Add(new StringContent("bedrive"), "uploadType");
                
                // Optional: parentId (folder to upload into)
                if (parentId.HasValue && parentId.Value > 0)
                {
                    content.Add(new StringContent(parentId.Value.ToString()), "parentId");
                    Debug.WriteLine($"📁 Adding parentId to upload: {parentId.Value}");
                }
                else
                {
                    Debug.WriteLine($"📁 No parentId provided - uploading to root");
                }
                
                // CRITICAL: Add workspace ID to ensure file goes to correct workspace
                content.Add(new StringContent(CurrentWorkspaceId.ToString()), "workspaceId");
                Debug.WriteLine($"🏢 Adding workspace context to upload: workspaceId={CurrentWorkspaceId}");

                // Create request to /uploads endpoint (as per API docs)
                var request = new HttpRequestMessage(HttpMethod.Post, "uploads")
                {
                    Content = content
                };
                
                // Ensure UTF-8 encoding for the request
                request.Headers.AcceptCharset.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("utf-8"));

                if (!string.IsNullOrEmpty(cookies))
                {
                    request.Headers.Add("Cookie", cookies);
                    Debug.WriteLine($"🍪 Cookies added");
                }
                
                if (!string.IsNullOrEmpty(xsrfToken))
                {
                    request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
                    Debug.WriteLine($"🔐 XSRF-TOKEN added");
                }

                Debug.WriteLine($"📤 Uploading to: uploads");
                
                // استخدام HttpCompletionOption.ResponseHeadersRead لتحسين الأداء مع الملفات الكبيرة
                // وإضافة timeout خاص للرفع
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromHours(2)); // PERFORMANCE FIX: Increased timeout to 2 hours
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                var responseContent = await response.Content.ReadAsStringAsync();
                
                Debug.WriteLine($"Upload Response Status: {response.StatusCode}");
                Debug.WriteLine($"Response: {responseContent.Substring(0, Math.Min(200, responseContent.Length))}");

                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"❌ Upload failed: {responseContent}");
                    
                    // Parse error message from server
                    string errorMessage = "Upload failed";
                    try
                    {
                        using (JsonDocument errorDoc = JsonDocument.Parse(responseContent))
                        {
                            if (errorDoc.RootElement.TryGetProperty("message", out JsonElement msgElement))
                            {
                                errorMessage = msgElement.GetString() ?? errorMessage;
                            }
                        }
                    }
                    catch { }
                    
                    // Handle specific error cases
                    if (response.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge)
                    {
                        errorMessage = $"File '{fileName}' is too large for server ({fileSizeInBytes / 1024 / 1024}MB). Try using the web interface.";
                    }
                    else if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
                    {
                        // PERFORMANCE FIX: UnprocessableEntity might be temporary for large files
                        // Check file size - if large, it's likely a temporary server issue
                        if (fileSizeMB > 3) // Files larger than 3MB (fileSizeMB already defined above)
                        {
                            errorMessage = $"Server temporarily cannot process large file '{fileName}' ({fileSizeMB:F1}MB). Retrying...";
                        }
                        else
                        {
                            errorMessage = $"Server cannot process file '{fileName}'. This might be due to file type restrictions or server issues.";
                        }
                    }
                    else if (response.StatusCode == System.Net.HttpStatusCode.InternalServerError)
                    {
                        errorMessage = $"Server error while uploading '{fileName}'. Please try again later.";
                    }
                    
                    throw new HttpRequestException($"{errorMessage} (Status: {response.StatusCode})");
                }

                // Parse response
                using (JsonDocument doc = JsonDocument.Parse(responseContent))
                {
                    if (doc.RootElement.TryGetProperty("fileEntry", out JsonElement fileEntryElement))
                    {
                        var options = new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                        };
                        
                        var fileEntry = JsonSerializer.Deserialize<FileEntry>(fileEntryElement.GetRawText(), options);
                        Debug.WriteLine($"✅ File uploaded successfully: {fileEntry?.Name}");
                        return fileEntry;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Upload error: {ex.Message}");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                throw; // Re-throw to let caller handle it
            }
            finally
            {
                // CRITICAL: Always dispose the file stream to prevent file locks and memory leaks
                fileStream?.Dispose();
                Debug.WriteLine("🧹 File stream disposed");
            }
        }

        public async Task<List<FileEntry>> GetFilesAsync()
        {
            try
            {
                Debug.WriteLine("=== Starting GetFilesAsync (Cookie-based) ===");
                
                // إعداد الطلب مع workspace context
                var endpoint = "drive/file-entries";
                // ALWAYS add workspace ID, even for Default workspace (ID: 0)
                endpoint += $"?workspaceId={CurrentWorkspaceId}";
                Debug.WriteLine($"🏢 Adding workspace context: workspaceId={CurrentWorkspaceId}");
                var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

                // 1. جلب الكوكيز (الأهم)
                string cookies = AppBridge.CurrentCookies;
                if (string.IsNullOrEmpty(cookies))
                {
                    cookies = await SecureStorage.Default.GetAsync("auth_cookies") ?? "";
                }

                if (!string.IsNullOrEmpty(cookies))
                {
                    // إضافة الكوكيز للهيدر
                    request.Headers.Add("Cookie", cookies);
                    Debug.WriteLine($"🍪 Cookies added: {cookies.Substring(0, Math.Min(100, cookies.Length))}...");

                    // 2. استخراج وإضافة CSRF Token (ضروري جداً مع الكوكيز)
                    var xsrfToken = ExtractXsrfToken(cookies);
                    if (!string.IsNullOrEmpty(xsrfToken))
                    {
                        request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
                        Debug.WriteLine($"🔐 XSRF-TOKEN added: {xsrfToken.Substring(0, Math.Min(30, xsrfToken.Length))}...");
                    }

                    // 3. Workspace context is now in query parameter (as per API docs)
                    if (CurrentWorkspaceId > 0)
                    {
                        Debug.WriteLine($"🏢 Workspace context added as query parameter: workspaceId={CurrentWorkspaceId}");
                    }

                    // 🔥 الحركة السحرية: إزالة Authorization Header
                    // هذا يمنع السيرفر من تفعيل middleware "verifyApiAccess"
                    _httpClient.DefaultRequestHeaders.Authorization = null;
                    request.Headers.Authorization = null;
                    Debug.WriteLine("🚀 Sending request using COOKIES ONLY (No Bearer Token)...");
                }
                else
                {
                    // fallback: لو لم نجد كوكيز، نستخدم التوكن (قد يفشل لكنه محاولة أخيرة)
                    var token = await SecureStorage.Default.GetAsync("auth_token");
                    if (!string.IsNullOrEmpty(token))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        Debug.WriteLine("⚠️ No cookies found, falling back to Bearer token...");
                    }
                }

                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                
                Debug.WriteLine($"Status: {response.StatusCode}");
                Debug.WriteLine($"Response preview: {content.Substring(0, Math.Min(200, content.Length))}...");

                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"❌ Error {response.StatusCode}: {content}");
                    
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                        throw new Exception("Session Expired. Please login again.");
                    
                    throw new Exception($"Server Error: {response.StatusCode}");
                }

                var result = await response.Content.ReadFromJsonAsync<FileListResponse>();
                Debug.WriteLine($"✅ Files fetched: {result?.Data?.Count ?? 0}");
                return result?.Data ?? new List<FileEntry>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Exception: {ex.Message}");
                throw;
            }
        }

        public async Task<bool> DeleteFileAsync(int fileId)
        {
            try
            {
                Debug.WriteLine($"=== Deleting file (move to trash): {fileId} ===");

                var request = new HttpRequestMessage(HttpMethod.Post, "file-entries/delete")
                {
                    Content = JsonContent.Create(new { entryIds = new[] { fileId }, deleteForever = false })
                };

                // Add cookies and XSRF token
                string cookies = AppBridge.CurrentCookies;
                if (string.IsNullOrEmpty(cookies))
                {
                    cookies = await SecureStorage.Default.GetAsync("auth_cookies") ?? "";
                }

                if (!string.IsNullOrEmpty(cookies))
                {
                    request.Headers.Add("Cookie", cookies);
                    
                    var xsrfToken = ExtractXsrfToken(cookies);
                    if (!string.IsNullOrEmpty(xsrfToken))
                    {
                        request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
                    }
                }

                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                
                Debug.WriteLine($"Delete Response Status: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    Debug.WriteLine("✅ File deleted successfully");
                    return true;
                }
                
                Debug.WriteLine($"❌ Delete failed: {content}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Delete error: {ex.Message}");
                return false;
            }
        }

        public async Task<FileEntry?> CreateFolderAsync(string folderName, int? parentId = null)
        {
            try
            {
                Debug.WriteLine($"=== Creating folder: {folderName} ===");
                Debug.WriteLine($"🔍 Parent ID: {parentId}, Workspace ID: {CurrentWorkspaceId}");

                // Validate parent ID if provided
                if (parentId.HasValue && parentId.Value <= 0)
                {
                    Debug.WriteLine($"⚠️ Invalid parent ID provided: {parentId}, setting to null");
                    parentId = null;
                }

                // If parentId is provided, verify it exists and is accessible
                if (parentId.HasValue)
                {
                    try
                    {
                        var parentFolder = await GetFolderContentsAsync(parentId.Value);
                        if (parentFolder == null || parentFolder.Count == 0)
                        {
                            // Check if the parent folder itself exists by trying to get its info
                            Debug.WriteLine($"🔍 Verifying parent folder exists: {parentId}");
                            // If we can't access it, it might not exist or we don't have permission
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"⚠️ Cannot verify parent folder {parentId}: {ex.Message}");
                        Debug.WriteLine($"🔄 Proceeding with folder creation anyway...");
                    }
                }

                var requestData = new { 
                    name = folderName, 
                    parentId = parentId,
                    workspaceId = CurrentWorkspaceId  // CRITICAL: Add workspace context
                };

                Debug.WriteLine($"📤 Request data: name='{folderName}', parentId={parentId}, workspaceId={CurrentWorkspaceId}");

                var request = new HttpRequestMessage(HttpMethod.Post, "folders")
                {
                    Content = JsonContent.Create(requestData)
                };
                Debug.WriteLine($"🏢 Adding workspace context to folder creation: workspaceId={CurrentWorkspaceId}");

                // Add cookies and XSRF token
                string cookies = AppBridge.CurrentCookies;
                if (string.IsNullOrEmpty(cookies))
                {
                    cookies = await SecureStorage.Default.GetAsync("auth_cookies") ?? "";
                }

                if (!string.IsNullOrEmpty(cookies))
                {
                    request.Headers.Add("Cookie", cookies);
                    
                    var xsrfToken = ExtractXsrfToken(cookies);
                    if (!string.IsNullOrEmpty(xsrfToken))
                    {
                        request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
                    }
                }

                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                
                Debug.WriteLine($"Create Folder Response Status: {response.StatusCode}");
                Debug.WriteLine($"Response Content: {content.Substring(0, Math.Min(500, content.Length))}");
                
                if (response.IsSuccessStatusCode)
                {
                    using (JsonDocument doc = JsonDocument.Parse(content))
                    {
                        if (doc.RootElement.TryGetProperty("folder", out JsonElement folderElement))
                        {
                            var options = new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true,
                                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                            };
                            
                            var folder = JsonSerializer.Deserialize<FileEntry>(folderElement.GetRawText(), options);
                            Debug.WriteLine($"✅ Folder created successfully: {folder?.Name} (ID: {folder?.Id})");
                            return folder;
                        }
                    }
                }
                else
                {
                    Debug.WriteLine($"❌ Create folder failed: {content}");
                    
                    // Parse error details for better debugging
                    try
                    {
                        using (JsonDocument errorDoc = JsonDocument.Parse(content))
                        {
                            if (errorDoc.RootElement.TryGetProperty("errors", out JsonElement errorsElement))
                            {
                                Debug.WriteLine($"🔍 Validation errors: {errorsElement.GetRawText()}");
                            }
                            if (errorDoc.RootElement.TryGetProperty("message", out JsonElement messageElement))
                            {
                                Debug.WriteLine($"🔍 Error message: {messageElement.GetString()}");
                            }
                        }
                    }
                    catch (Exception parseEx)
                    {
                        Debug.WriteLine($"⚠️ Could not parse error response: {parseEx.Message}");
                    }
                }
                
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Create folder error: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> RenameFileAsync(int fileId, string newName)
        {
            try
            {
                Debug.WriteLine($"=== Renaming file {fileId} to: {newName} ===");

                var request = new HttpRequestMessage(HttpMethod.Put, $"file-entries/{fileId}")
                {
                    Content = JsonContent.Create(new { name = newName })
                };

                // Add cookies and XSRF token
                string cookies = AppBridge.CurrentCookies;
                if (string.IsNullOrEmpty(cookies))
                {
                    cookies = await SecureStorage.Default.GetAsync("auth_cookies") ?? "";
                }

                if (!string.IsNullOrEmpty(cookies))
                {
                    request.Headers.Add("Cookie", cookies);
                    
                    var xsrfToken = ExtractXsrfToken(cookies);
                    if (!string.IsNullOrEmpty(xsrfToken))
                    {
                        request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
                    }
                }

                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                
                Debug.WriteLine($"Rename Response Status: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    Debug.WriteLine("✅ File renamed successfully");
                    return true;
                }
                
                Debug.WriteLine($"❌ Rename failed: {content}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Rename error: {ex.Message}");
                return false;
            }
        }

        // Get files in a specific folder
        public async Task<List<FileEntry>> GetFolderContentsAsync(int folderId)
        {
            Debug.WriteLine($"=== GetFolderContentsAsync: {folderId} ===");
            var endpoint = $"drive/file-entries?section=folder&folderId={folderId}";
            // ALWAYS add workspace ID, even for Default workspace (ID: 0)
            endpoint += $"&workspaceId={CurrentWorkspaceId}";
            Debug.WriteLine($"🏢 Adding workspace context to folder request: workspaceId={CurrentWorkspaceId}");
            return await SendGetRequestAsync<FileListResponse>(endpoint) ?? new List<FileEntry>();
        }

        // Get starred files
        public async Task<List<FileEntry>> GetStarredFilesAsync()
        {
            Debug.WriteLine("=== GetStarredFilesAsync ===");
            // Use the same cookie-based request as GetFilesAsync
            return await GetFilesWithSectionAsync("starred");
        }

        // Get recent files
        public async Task<List<FileEntry>> GetRecentFilesAsync()
        {
            Debug.WriteLine("=== GetRecentFilesAsync ===");
            return await GetFilesWithSectionAsync("recent");
        }

        // Get shared files (shared with me)
        public async Task<List<FileEntry>> GetSharedFilesAsync()
        {
            Debug.WriteLine("=== GetSharedFilesAsync ===");
            return await GetFilesWithSectionAsync("sharedWithMe");
        }

        // Get trash files
        public async Task<List<FileEntry>> GetTrashFilesAsync()
        {
            Debug.WriteLine("=== GetTrashFilesAsync ===");
            return await GetFilesWithSectionAsync("trash");
        }

        // Helper: Get files with section parameter (uses same auth as GetFilesAsync)
        private async Task<List<FileEntry>> GetFilesWithSectionAsync(string section)
        {
            try
            {
                Debug.WriteLine($"📡 Fetching section: {section}");
                
                var endpoint = $"drive/file-entries?section={section}";
                // ALWAYS add workspace ID, even for Default workspace (ID: 0)
                endpoint += $"&workspaceId={CurrentWorkspaceId}";
                Debug.WriteLine($"🏢 Adding workspace context to section request: workspaceId={CurrentWorkspaceId}");
                
                var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

                string cookies = AppBridge.CurrentCookies;
                if (string.IsNullOrEmpty(cookies))
                {
                    cookies = await SecureStorage.Default.GetAsync("auth_cookies") ?? "";
                }

                if (!string.IsNullOrEmpty(cookies))
                {
                    request.Headers.Add("Cookie", cookies);
                    var xsrfToken = ExtractXsrfToken(cookies);
                    if (!string.IsNullOrEmpty(xsrfToken))
                    {
                        request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
                    }
                    _httpClient.DefaultRequestHeaders.Authorization = null;
                    request.Headers.Authorization = null;
                }

                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                
                Debug.WriteLine($"📡 Section {section} - Status: {response.StatusCode}");
                Debug.WriteLine($"📡 Response: {content.Substring(0, Math.Min(300, content.Length))}...");

                if (response.IsSuccessStatusCode)
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                    };
                    var result = JsonSerializer.Deserialize<FileListResponse>(content, options);
                    Debug.WriteLine($"✅ Section {section}: {result?.Data?.Count ?? 0} files");
                    return result?.Data ?? new List<FileEntry>();
                }
                
                Debug.WriteLine($"❌ Section {section} failed: {response.StatusCode}");
                return new List<FileEntry>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Section {section} error: {ex.Message}");
                return new List<FileEntry>();
            }
        }

        // Star a file
        public async Task<bool> StarFileAsync(int fileId)
        {
            try
            {
                Debug.WriteLine($"=== Starring file: {fileId} ===");

                var request = new HttpRequestMessage(HttpMethod.Post, "file-entries/star")
                {
                    Content = JsonContent.Create(new { entryIds = new[] { fileId } })
                };

                // Add cookies and XSRF token
                string cookies = AppBridge.CurrentCookies;
                if (string.IsNullOrEmpty(cookies))
                {
                    cookies = await SecureStorage.Default.GetAsync("auth_cookies") ?? "";
                }

                if (!string.IsNullOrEmpty(cookies))
                {
                    request.Headers.Add("Cookie", cookies);
                    
                    var xsrfToken = ExtractXsrfToken(cookies);
                    if (!string.IsNullOrEmpty(xsrfToken))
                    {
                        request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
                    }
                }

                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                
                Debug.WriteLine($"Star Response Status: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    Debug.WriteLine("✅ File starred successfully");
                    return true;
                }
                
                Debug.WriteLine($"❌ Star failed: {content}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Star error: {ex.Message}");
                return false;
            }
        }

        // Unstar a file
        public async Task<bool> UnstarFileAsync(int fileId)
        {
            try
            {
                Debug.WriteLine($"=== Unstarring file: {fileId} ===");

                var request = new HttpRequestMessage(HttpMethod.Post, "file-entries/unstar")
                {
                    Content = JsonContent.Create(new { entryIds = new[] { fileId } })
                };

                // Add cookies and XSRF token
                string cookies = AppBridge.CurrentCookies;
                if (string.IsNullOrEmpty(cookies))
                {
                    cookies = await SecureStorage.Default.GetAsync("auth_cookies") ?? "";
                }

                if (!string.IsNullOrEmpty(cookies))
                {
                    request.Headers.Add("Cookie", cookies);
                    
                    var xsrfToken = ExtractXsrfToken(cookies);
                    if (!string.IsNullOrEmpty(xsrfToken))
                    {
                        request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
                    }
                }

                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                
                Debug.WriteLine($"Unstar Response Status: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    Debug.WriteLine("✅ File unstarred successfully");
                    return true;
                }
                
                Debug.WriteLine($"❌ Unstar failed: {content}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Unstar error: {ex.Message}");
                return false;
            }
        }

        // Restore file from trash
        public async Task<bool> RestoreFileAsync(int fileId)
        {
            return await SendPostRequestAsync("file-entries/restore", new { entryIds = new[] { fileId } });
        }

        // Permanently delete file
        public async Task<bool> DeleteFilePermanentlyAsync(int fileId)
        {
            return await SendPostRequestAsync("file-entries/delete", new { entryIds = new[] { fileId }, deleteForever = true });
        }

        // Empty trash
        public async Task<bool> EmptyTrashAsync()
        {
            try
            {
                Debug.WriteLine("=== Emptying trash to free up storage space ===");
                
                var request = new HttpRequestMessage(HttpMethod.Post, "file-entries/delete")
                {
                    Content = JsonContent.Create(new { emptyTrash = true })
                };

                // Add cookies and XSRF token
                string cookies = AppBridge.CurrentCookies;
                if (string.IsNullOrEmpty(cookies))
                {
                    cookies = await SecureStorage.Default.GetAsync("auth_cookies") ?? "";
                }

                if (!string.IsNullOrEmpty(cookies))
                {
                    request.Headers.Add("Cookie", cookies);
                    
                    var xsrfToken = ExtractXsrfToken(cookies);
                    if (!string.IsNullOrEmpty(xsrfToken))
                    {
                        request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
                    }
                }

                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                
                Debug.WriteLine($"Empty Trash Response Status: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    Debug.WriteLine("✅ Trash emptied successfully - storage space should be freed");
                    return true;
                }
                
                Debug.WriteLine($"❌ Empty trash failed: {content}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Empty trash error: {ex.Message}");
                return false;
            }
        }
        
        // Delete multiple files permanently (to free up space immediately)
        public async Task<bool> DeleteFilesPermanentlyAsync(int[] fileIds)
        {
            try
            {
                Debug.WriteLine($"=== Permanently deleting {fileIds.Length} file(s) to free up storage space ===");
                
                var request = new HttpRequestMessage(HttpMethod.Post, "file-entries/delete")
                {
                    Content = JsonContent.Create(new { entryIds = fileIds, deleteForever = true })
                };

                // Add cookies and XSRF token
                string cookies = AppBridge.CurrentCookies;
                if (string.IsNullOrEmpty(cookies))
                {
                    cookies = await SecureStorage.Default.GetAsync("auth_cookies") ?? "";
                }

                if (!string.IsNullOrEmpty(cookies))
                {
                    request.Headers.Add("Cookie", cookies);
                    
                    var xsrfToken = ExtractXsrfToken(cookies);
                    if (!string.IsNullOrEmpty(xsrfToken))
                    {
                        request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
                    }
                }

                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                
                Debug.WriteLine($"Delete Permanently Response Status: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"✅ {fileIds.Length} file(s) permanently deleted - storage space freed");
                    return true;
                }
                
                Debug.WriteLine($"❌ Delete permanently failed: {content}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Delete permanently error: {ex.Message}");
                return false;
            }
        }

        // Get space usage
        public async Task<SpaceUsage?> GetSpaceUsageAsync()
        {
            try
            {
                var request = CreateAuthenticatedRequest(HttpMethod.Get, "user/space-usage");
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<SpaceUsage>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ GetSpaceUsage error: {ex.Message}");
            }
            return null;
        }

        // Download file
        public async Task<string?> DownloadFileAsync(FileEntry file, string destinationFolder)
        {
            try
            {
                Debug.WriteLine($"=== Downloading file: {file.Name} ===");
                Debug.WriteLine($"🔍 File ID: {file.Id}, Hash: {file.Hash ?? "null"}");
                
                // Use file ID if hash is not available
                var downloadEndpoint = !string.IsNullOrEmpty(file.Hash) 
                    ? $"file-entries/download/{file.Hash}"
                    : $"file-entries/{file.Id}/download";
                
                Debug.WriteLine($"📡 Download endpoint: {downloadEndpoint}");
                
                // Add workspace context to download URL
                var downloadUrl = downloadEndpoint;
                if (downloadUrl.Contains("?"))
                    downloadUrl += $"&workspaceId={CurrentWorkspaceId}";
                else
                    downloadUrl += $"?workspaceId={CurrentWorkspaceId}";
                
                Debug.WriteLine($"🏢 Download URL with workspace: {downloadUrl}");
                var request = CreateAuthenticatedRequest(HttpMethod.Get, downloadUrl);
                
                // Ensure redirects are followed
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                
                Debug.WriteLine($"Download Response Status: {response.StatusCode}");
                Debug.WriteLine($"Final URL: {response.RequestMessage?.RequestUri}");
                
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    
                    if (bytes.Length == 0)
                    {
                        Debug.WriteLine($"❌ Downloaded file is empty");
                        return null;
                    }
                    
                    // Fix filename encoding issues
                    var safeFileName = SanitizeFileName(file.Name);
                    Debug.WriteLine($"📝 Original filename: {file.Name}");
                    Debug.WriteLine($"📝 Sanitized filename: {safeFileName}");
                    
                    var filePath = Path.Combine(destinationFolder, safeFileName);
                    await File.WriteAllBytesAsync(filePath, bytes);
                    Debug.WriteLine($"✅ Downloaded to: {filePath} ({bytes.Length} bytes)");
                    return filePath;
                }
                
                Debug.WriteLine($"❌ Download failed: {response.StatusCode}");
                var errorContent = await response.Content.ReadAsStringAsync();
                Debug.WriteLine($"Error content: {errorContent.Substring(0, Math.Min(200, errorContent.Length))}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Download error: {ex.Message}");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        // Move files
        public async Task<bool> MoveFilesAsync(int[] fileIds, int? destinationFolderId)
        {
            return await SendPostRequestAsync("file-entries/move", new { entryIds = fileIds, destinationId = destinationFolderId });
        }

        // Helper: Create authenticated request
        private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string endpoint)
        {
            var request = new HttpRequestMessage(method, endpoint);
            
            string cookies = AppBridge.CurrentCookies;
            if (string.IsNullOrEmpty(cookies))
            {
                cookies = SecureStorage.Default.GetAsync("auth_cookies").Result ?? "";
            }

            if (!string.IsNullOrEmpty(cookies))
            {
                request.Headers.Add("Cookie", cookies);
                var xsrfToken = ExtractXsrfToken(cookies);
                if (!string.IsNullOrEmpty(xsrfToken))
                {
                    request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
                }
            }
            
            return request;
        }

        // Helper: Send GET request and parse response
        private async Task<List<FileEntry>?> SendGetRequestAsync<T>(string endpoint) where T : FileListResponse
        {
            try
            {
                Debug.WriteLine($"📡 GET {endpoint}");
                var request = CreateAuthenticatedRequest(HttpMethod.Get, endpoint);
                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                
                Debug.WriteLine($"📡 Response: {response.StatusCode}");
                Debug.WriteLine($"📡 Content: {content.Substring(0, Math.Min(300, content.Length))}...");
                
                if (response.IsSuccessStatusCode)
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                    };
                    var result = JsonSerializer.Deserialize<T>(content, options);
                    Debug.WriteLine($"✅ Parsed {result?.Data?.Count ?? 0} files");
                    return result?.Data ?? new List<FileEntry>();
                }
                
                Debug.WriteLine($"❌ GET {endpoint} failed: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ GET {endpoint} error: {ex.Message}");
            }
            return new List<FileEntry>();
        }

        // Helper: Send POST request
        private async Task<bool> SendPostRequestAsync(string endpoint, object data)
        {
            try
            {
                var request = CreateAuthenticatedRequest(HttpMethod.Post, endpoint);
                request.Content = JsonContent.Create(data);
                
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ POST {endpoint} error: {ex.Message}");
                return false;
            }
        }

        // Clean up duplicate files
        public async Task<int> CleanupDuplicateFilesAsync()
        {
            try
            {
                Debug.WriteLine("🧹 Starting duplicate file cleanup...");
                
                var allFiles = await GetFilesAsync();
                if (allFiles == null || allFiles.Count == 0)
                {
                    Debug.WriteLine("📂 No files found");
                    return 0;
                }
                
                var duplicatesRemoved = 0;
                var fileGroups = allFiles
                    .Where(f => f.Type == "file")
                    .GroupBy(f => new { f.Name, f.ParentId, f.FileSize })
                    .Where(g => g.Count() > 1)
                    .ToList();
                
                Debug.WriteLine($"🔍 Found {fileGroups.Count} groups of duplicate files");
                
                foreach (var group in fileGroups)
                {
                    var files = group.OrderBy(f => f.CreatedAt).ToList();
                    var keepFile = files.First(); // Keep the oldest one
                    var duplicatesToDelete = files.Skip(1).ToList();
                    
                    Debug.WriteLine($"📁 Duplicate group: {group.Key.Name} ({group.Key.FileSize} bytes)");
                    Debug.WriteLine($"   ✅ Keeping: ID {keepFile.Id} (created: {keepFile.CreatedAt})");
                    
                    foreach (var duplicate in duplicatesToDelete)
                    {
                        Debug.WriteLine($"   🗑️ Deleting: ID {duplicate.Id} (created: {duplicate.CreatedAt})");
                        
                        try
                        {
                            var deleted = await DeleteFileAsync(duplicate.Id);
                            if (deleted)
                            {
                                duplicatesRemoved++;
                                Debug.WriteLine($"   ✅ Deleted duplicate file ID {duplicate.Id}");
                            }
                            else
                            {
                                Debug.WriteLine($"   ❌ Failed to delete duplicate file ID {duplicate.Id}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"   ❌ Error deleting duplicate file ID {duplicate.Id}: {ex.Message}");
                        }
                        
                        // Small delay to avoid overwhelming the server
                        await Task.Delay(100);
                    }
                }
                
                Debug.WriteLine($"🧹 Cleanup completed. Removed {duplicatesRemoved} duplicate files");
                return duplicatesRemoved;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error during duplicate cleanup: {ex.Message}");
                return 0;
            }
        }

        // Copy/Duplicate file
        public async Task<FileEntry?> CopyFileAsync(int fileId, int? destinationFolderId = null)
        {
            try
            {
                Debug.WriteLine($"=== Copying file: {fileId} ===");
                
                var request = CreateAuthenticatedRequest(HttpMethod.Post, "file-entries/duplicate");
                request.Content = JsonContent.Create(new { entryIds = new[] { fileId }, destinationId = destinationFolderId });
                
                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                
                if (response.IsSuccessStatusCode)
                {
                    using (JsonDocument doc = JsonDocument.Parse(content))
                    {
                        if (doc.RootElement.TryGetProperty("entries", out JsonElement entriesElement) && 
                            entriesElement.GetArrayLength() > 0)
                        {
                            var options = new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true,
                                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                            };
                            var entry = JsonSerializer.Deserialize<FileEntry>(entriesElement[0].GetRawText(), options);
                            Debug.WriteLine($"✅ File copied successfully");
                            return entry;
                        }
                    }
                }
                
                Debug.WriteLine($"❌ Copy failed: {content}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Copy error: {ex.Message}");
                return null;
            }
        }

        // Get shareable link for a file
        public async Task<ShareableLink?> GetShareableLinkAsync(int fileId)
        {
            try
            {
                var request = CreateAuthenticatedRequest(HttpMethod.Get, $"file-entries/{fileId}/shareable-link");
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    return JsonSerializer.Deserialize<ShareableLink>(content, options);
                }
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ GetShareableLink error: {ex.Message}");
                return null;
            }
        }

        // Create shareable link
        public async Task<ShareableLink?> CreateShareableLinkAsync(int fileId, bool allowDownload = true, bool allowEdit = false)
        {
            try
            {
                var request = CreateAuthenticatedRequest(HttpMethod.Post, $"file-entries/{fileId}/shareable-link");
                request.Content = JsonContent.Create(new { allowDownload, allowEdit });
                
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    return JsonSerializer.Deserialize<ShareableLink>(content, options);
                }
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ CreateShareableLink error: {ex.Message}");
                return null;
            }
        }

        // Delete shareable link
        public async Task<bool> DeleteShareableLinkAsync(int fileId)
        {
            try
            {
                var request = CreateAuthenticatedRequest(HttpMethod.Delete, $"file-entries/{fileId}/shareable-link");
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ DeleteShareableLink error: {ex.Message}");
                return false;
            }
        }

        // Get file shares (users with access)
        public async Task<List<ShareEntry>> GetFileSharesAsync(int fileId)
        {
            try
            {
                var request = CreateAuthenticatedRequest(HttpMethod.Get, $"file-entries/{fileId}");
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    using (JsonDocument doc = JsonDocument.Parse(content))
                    {
                        if (doc.RootElement.TryGetProperty("users", out JsonElement usersElement))
                        {
                            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                            return JsonSerializer.Deserialize<List<ShareEntry>>(usersElement.GetRawText(), options) ?? new List<ShareEntry>();
                        }
                    }
                }
                return new List<ShareEntry>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ GetFileShares error: {ex.Message}");
                return new List<ShareEntry>();
            }
        }

        // Share file with user
        public async Task<bool> ShareFileWithUserAsync(int fileId, string email, Dictionary<string, bool> permissions)
        {
            try
            {
                var request = CreateAuthenticatedRequest(HttpMethod.Post, $"file-entries/{fileId}/share");
                request.Content = JsonContent.Create(new { emails = new[] { email }, permissions });
                
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ ShareFileWithUser error: {ex.Message}");
                return false;
            }
        }

        // Remove user from shares
        public async Task<bool> RemoveShareAsync(int fileId, int userId)
        {
            try
            {
                var request = CreateAuthenticatedRequest(HttpMethod.Post, $"file-entries/{fileId}/unshare");
                request.Content = JsonContent.Create(new { userId });
                
                var response = await _httpClient.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ RemoveShare error: {ex.Message}");
                return false;
            }
        }

        // Search files
        public async Task<List<FileEntry>> SearchFilesAsync(string query, int? folderId = null)
        {
            try
            {
                var endpoint = $"drive/file-entries?query={Uri.EscapeDataString(query)}";
                if (folderId.HasValue)
                {
                    endpoint += $"&folderId={folderId}";
                }
                if (CurrentWorkspaceId > 0)
                {
                    endpoint += $"&workspaceId={CurrentWorkspaceId}";
                    Debug.WriteLine($"🏢 Adding workspace context to search: workspaceId={CurrentWorkspaceId}");
                }
                
                var request = CreateAuthenticatedRequest(HttpMethod.Get, endpoint);
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                    };
                    var result = JsonSerializer.Deserialize<FileListResponse>(content, options);
                    return result?.Data ?? new List<FileEntry>();
                }
                return new List<FileEntry>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ SearchFiles error: {ex.Message}");
                return new List<FileEntry>();
            }
        }

        // Get file preview content
        public async Task<byte[]?> GetFilePreviewAsync(int fileId, string? hash)
        {
            try
            {
                Debug.WriteLine($"=== Getting file preview: ID={fileId}, Hash={hash ?? "null"} ===");
                
                // Use file ID if hash is not available
                var previewEndpoint = !string.IsNullOrEmpty(hash) 
                    ? $"file-entries/download/{hash}"
                    : $"file-entries/{fileId}/download";
                
                // Add workspace context
                var previewUrl = previewEndpoint;
                if (previewUrl.Contains("?"))
                    previewUrl += $"&workspaceId={CurrentWorkspaceId}";
                else
                    previewUrl += $"?workspaceId={CurrentWorkspaceId}";
                
                Debug.WriteLine($"📡 Preview URL: {previewUrl}");
                var request = CreateAuthenticatedRequest(HttpMethod.Get, previewUrl);
                var response = await _httpClient.SendAsync(request);
                
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsByteArrayAsync();
                }
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ GetFilePreview error: {ex.Message}");
                return null;
            }
        }

        // Get folders for move operation (only folders, not files)
        public async Task<List<FileEntry>> GetFoldersForMoveAsync(int? parentId = null)
        {
            try
            {
                Debug.WriteLine($"=== Getting folders for move (parent: {parentId}) ===");
                
                var endpoint = parentId.HasValue 
                    ? $"drive/file-entries?section=folder&folderId={parentId}&type=folder"
                    : "drive/file-entries?section=folder&type=folder";
                
                var request = CreateAuthenticatedRequest(HttpMethod.Get, endpoint);
                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                
                Debug.WriteLine($"GetFoldersForMove Response Status: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                    };
                    var result = JsonSerializer.Deserialize<FileListResponse>(content, options);
                    
                    // Filter to only folders
                    var folders = result?.Data?.Where(f => f.Type == "folder").ToList() ?? new List<FileEntry>();
                    Debug.WriteLine($"✅ Found {folders.Count} folders");
                    return folders;
                }
                
                Debug.WriteLine($"❌ GetFoldersForMove failed: {content}");
                return new List<FileEntry>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ GetFoldersForMove error: {ex.Message}");
                return new List<FileEntry>();
            }
        }

        // Current workspace ID (0 = personal workspace)
        public static int CurrentWorkspaceId { get; set; } = 0;

        // Switch workspace (just stores the ID locally, no API call needed)
        public async Task<bool> SwitchWorkspaceAsync(int workspaceId)
        {
            try
            {
                Debug.WriteLine($"=== Switching to workspace: {workspaceId} ===");
                
                // Store the workspace ID locally
                CurrentWorkspaceId = workspaceId;
                
                // WORKSPACE PERSISTENCE FIX: Save to secure storage for persistence across app restarts
                await SecureStorage.Default.SetAsync("last_active_workspace_id", workspaceId.ToString());
                
                // CRITICAL: Also save for Windows Service compatibility in shared location
                await SaveForWindowsServiceAsync("workspace_id", workspaceId.ToString());
                
                Debug.WriteLine($"💾 Saved workspace {workspaceId} to persistent storage for app restart restoration");
                
                Debug.WriteLine($"✅ Successfully switched to workspace {workspaceId}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Switch workspace error: {ex.Message}");
                return false;
            }
        }

        // Get available workspaces for current user
        public async Task<List<Workspace>> GetWorkspacesAsync()
        {
            try
            {
                Debug.WriteLine("=== Getting user workspaces ===");
                
                // Try multiple possible endpoints
                var endpoints = new[] { "me/workspaces", "workspaces", "user/workspaces", "drive/workspaces" };
                
                foreach (var endpoint in endpoints)
                {
                    Debug.WriteLine($"Trying workspace endpoint: {endpoint}");
                    
                    var request = CreateAuthenticatedRequest(HttpMethod.Get, endpoint);
                    var response = await _httpClient.SendAsync(request);
                    var content = await response.Content.ReadAsStringAsync();
                    
                    Debug.WriteLine($"Endpoint {endpoint} - Status: {response.StatusCode}");
                    Debug.WriteLine($"Content preview: {content.Substring(0, Math.Min(200, content.Length))}");
                    
                    if (response.IsSuccessStatusCode && !content.TrimStart().StartsWith("<"))
                    {
                        var options = new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                        };
                        
                        // Try to parse as direct array or as data property
                        try
                        {
                            // First try parsing as wrapped response (most likely format)
                            using (JsonDocument doc = JsonDocument.Parse(content))
                            {
                                if (doc.RootElement.TryGetProperty("workspaces", out JsonElement workspacesElement))
                                {
                                    var workspaces = JsonSerializer.Deserialize<List<Workspace>>(workspacesElement.GetRawText(), options);
                                    if (workspaces != null)
                                    {
                                        Debug.WriteLine($"✅ Found {workspaces.Count} workspaces in workspaces property from {endpoint}");
                                        return workspaces;
                                    }
                                }
                                else if (doc.RootElement.TryGetProperty("data", out JsonElement dataElement))
                                {
                                    var workspaces = JsonSerializer.Deserialize<List<Workspace>>(dataElement.GetRawText(), options);
                                    if (workspaces != null)
                                    {
                                        Debug.WriteLine($"✅ Found {workspaces.Count} workspaces in data property from {endpoint}");
                                        return workspaces;
                                    }
                                }
                                else
                                {
                                    // Try parsing as direct array
                                    var workspaces = JsonSerializer.Deserialize<List<Workspace>>(content, options);
                                    if (workspaces != null)
                                    {
                                        Debug.WriteLine($"✅ Found {workspaces.Count} workspaces as direct array from {endpoint}");
                                        return workspaces;
                                    }
                                }
                            }
                        }
                        catch (Exception parseEx)
                        {
                            Debug.WriteLine($"❌ Failed to parse response from {endpoint}: {parseEx.Message}");
                        }
                    }
                }
                
                Debug.WriteLine("❌ All workspace endpoints failed or returned no workspaces");
                
                // Return empty list - let the UI handle creating default workspace
                return new List<Workspace>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Get workspaces error: {ex.Message}");
                return new List<Workspace>();
            }
        }

        // Create a new workspace
        public async Task<Workspace?> CreateWorkspaceAsync(string name)
        {
            try
            {
                Debug.WriteLine($"=== Creating workspace: {name} ===");
                
                var request = CreateAuthenticatedRequest(HttpMethod.Post, "workspace");
                request.Content = JsonContent.Create(new { name = name });
                
                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                
                Debug.WriteLine($"Create workspace response: {response.StatusCode}");
                Debug.WriteLine($"Response content: {content}");
                
                if (response.IsSuccessStatusCode)
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                    };
                    
                    // Try to parse workspace from response
                    using (JsonDocument doc = JsonDocument.Parse(content))
                    {
                        if (doc.RootElement.TryGetProperty("workspace", out JsonElement workspaceElement))
                        {
                            return JsonSerializer.Deserialize<Workspace>(workspaceElement.GetRawText(), options);
                        }
                        else
                        {
                            // Try parsing as direct workspace object
                            return JsonSerializer.Deserialize<Workspace>(content, options);
                        }
                    }
                }
                
                Debug.WriteLine($"❌ Failed to create workspace: {content}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Create workspace error: {ex.Message}");
                return null;
            }
        }

        // Rename a workspace
        public async Task<bool> RenameWorkspaceAsync(int workspaceId, string newName)
        {
            try
            {
                Debug.WriteLine($"=== Renaming workspace {workspaceId} to: {newName} ===");
                
                var request = CreateAuthenticatedRequest(HttpMethod.Put, $"workspace/{workspaceId}");
                request.Content = JsonContent.Create(new { name = newName });
                
                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                
                Debug.WriteLine($"Rename workspace response: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"✅ Workspace renamed successfully");
                    return true;
                }
                
                Debug.WriteLine($"❌ Failed to rename workspace: {content}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Rename workspace error: {ex.Message}");
                return false;
            }
        }

        // Delete a workspace
        public async Task<bool> DeleteWorkspaceAsync(int workspaceId)
        {
            try
            {
                Debug.WriteLine($"=== Deleting workspace: {workspaceId} ===");
                
                var request = CreateAuthenticatedRequest(HttpMethod.Delete, $"workspace/{workspaceId}");
                var response = await _httpClient.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                
                Debug.WriteLine($"Delete workspace response: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    Debug.WriteLine($"✅ Workspace deleted successfully");
                    return true;
                }
                
                Debug.WriteLine($"❌ Failed to delete workspace: {content}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Delete workspace error: {ex.Message}");
                return false;
            }
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
    }
}
