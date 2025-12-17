using Microsoft.Win32;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace OkCloud.Client.Services
{
    /// <summary>
    /// Manages Windows startup integration for background sync
    /// Similar to Google Drive auto-start functionality
    /// </summary>
    public class StartupManager
    {
        private readonly ILogger<StartupManager> _logger;
        private const string REGISTRY_KEY = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME = "OkCloudSync";

        public StartupManager(ILogger<StartupManager> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Enable auto-start with Windows
        /// </summary>
        public async Task<bool> EnableAutoStartAsync()
        {
            try
            {
                _logger.LogInformation("🚀 Enabling auto-start with Windows...");
                
                var appPath = GetApplicationPath();
                if (string.IsNullOrEmpty(appPath))
                {
                    _logger.LogError("❌ Could not determine application path");
                    return false;
                }
                
                using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_KEY, true);
                if (key == null)
                {
                    _logger.LogError("❌ Could not access Windows startup registry key");
                    return false;
                }
                
                // Add startup entry with --background flag
                var startupCommand = $"\"{appPath}\" --background --minimized";
                key.SetValue(APP_NAME, startupCommand);
                
                _logger.LogInformation("✅ Auto-start enabled successfully");
                _logger.LogInformation($"📍 Startup command: {startupCommand}");
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to enable auto-start");
                return false;
            }
        }

        /// <summary>
        /// Disable auto-start with Windows
        /// </summary>
        public async Task<bool> DisableAutoStartAsync()
        {
            try
            {
                _logger.LogInformation("🛑 Disabling auto-start with Windows...");
                
                using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_KEY, true);
                if (key == null)
                {
                    _logger.LogWarning("⚠️ Could not access Windows startup registry key");
                    return false;
                }
                
                // Remove startup entry if it exists
                if (key.GetValue(APP_NAME) != null)
                {
                    key.DeleteValue(APP_NAME);
                    _logger.LogInformation("✅ Auto-start disabled successfully");
                }
                else
                {
                    _logger.LogInformation("ℹ️ Auto-start was not enabled");
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to disable auto-start");
                return false;
            }
        }

        /// <summary>
        /// Check if auto-start is currently enabled
        /// </summary>
        public async Task<bool> IsAutoStartEnabledAsync()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(REGISTRY_KEY, false);
                if (key == null)
                {
                    return false;
                }
                
                var value = key.GetValue(APP_NAME) as string;
                var isEnabled = !string.IsNullOrEmpty(value);
                
                _logger.LogInformation($"🔍 Auto-start status: {(isEnabled ? "Enabled" : "Disabled")}");
                
                return isEnabled;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to check auto-start status");
                return false;
            }
        }

        /// <summary>
        /// Get the current application executable path
        /// </summary>
        private string GetApplicationPath()
        {
            try
            {
                var process = Process.GetCurrentProcess();
                var path = process.MainModule?.FileName;
                
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    return path;
                }
                
                // Fallback to entry assembly location
                var entryAssembly = System.Reflection.Assembly.GetEntryAssembly();
                if (entryAssembly != null)
                {
                    return entryAssembly.Location;
                }
                
                return string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error getting application path");
                return string.Empty;
            }
        }

        /// <summary>
        /// Create a Windows Task Scheduler entry for more reliable startup
        /// Alternative to registry-based startup
        /// </summary>
        public async Task<bool> CreateScheduledTaskAsync()
        {
            try
            {
                _logger.LogInformation("📅 Creating Windows scheduled task...");
                
                var appPath = GetApplicationPath();
                if (string.IsNullOrEmpty(appPath))
                {
                    _logger.LogError("❌ Could not determine application path for scheduled task");
                    return false;
                }
                
                var taskName = "OkCloudBackgroundSync";
                var arguments = "--background --minimized";
                
                // Create scheduled task using schtasks command
                var createTaskCommand = $"schtasks /create /tn \"{taskName}\" /tr \"\\\"{appPath}\\\" {arguments}\" /sc onlogon /rl highest /f";
                
                var processInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {createTaskCommand}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(processInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    
                    if (process.ExitCode == 0)
                    {
                        _logger.LogInformation("✅ Scheduled task created successfully");
                        return true;
                    }
                    else
                    {
                        var error = await process.StandardError.ReadToEndAsync();
                        _logger.LogError($"❌ Failed to create scheduled task: {error}");
                    }
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error creating scheduled task");
                return false;
            }
        }

        /// <summary>
        /// Remove the Windows scheduled task
        /// </summary>
        public async Task<bool> RemoveScheduledTaskAsync()
        {
            try
            {
                _logger.LogInformation("🗑️ Removing Windows scheduled task...");
                
                var taskName = "OkCloudBackgroundSync";
                var deleteTaskCommand = $"schtasks /delete /tn \"{taskName}\" /f";
                
                var processInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {deleteTaskCommand}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(processInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    
                    if (process.ExitCode == 0)
                    {
                        _logger.LogInformation("✅ Scheduled task removed successfully");
                        return true;
                    }
                    else
                    {
                        // Task might not exist, which is fine
                        _logger.LogInformation("ℹ️ Scheduled task was not found or already removed");
                        return true;
                    }
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error removing scheduled task");
                return false;
            }
        }

        /// <summary>
        /// Setup complete background sync integration
        /// </summary>
        public async Task<bool> SetupBackgroundSyncAsync()
        {
            try
            {
                _logger.LogInformation("🔧 Setting up background sync integration...");
                
                // Try scheduled task first (more reliable)
                var taskCreated = await CreateScheduledTaskAsync();
                if (taskCreated)
                {
                    _logger.LogInformation("✅ Background sync setup completed using scheduled task");
                    return true;
                }
                
                // Fallback to registry startup
                var registryEnabled = await EnableAutoStartAsync();
                if (registryEnabled)
                {
                    _logger.LogInformation("✅ Background sync setup completed using registry startup");
                    return true;
                }
                
                _logger.LogError("❌ Failed to setup background sync");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error setting up background sync");
                return false;
            }
        }

        /// <summary>
        /// Remove all background sync integration
        /// </summary>
        public async Task<bool> RemoveBackgroundSyncAsync()
        {
            try
            {
                _logger.LogInformation("🧹 Removing background sync integration...");
                
                var taskRemoved = await RemoveScheduledTaskAsync();
                var registryDisabled = await DisableAutoStartAsync();
                
                if (taskRemoved || registryDisabled)
                {
                    _logger.LogInformation("✅ Background sync integration removed");
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error removing background sync integration");
                return false;
            }
        }
    }
}