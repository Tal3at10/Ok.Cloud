using System.ServiceProcess;
using Microsoft.Extensions.Logging;

namespace OkCloud.Client.Services;

/// <summary>
/// Manages the OkCloud Windows Background Service
/// </summary>
public class WindowsServiceManager
{
    private readonly ILogger<WindowsServiceManager> _logger;
    private const string SERVICE_NAME = "OkCloud Background Sync";

    public WindowsServiceManager(ILogger<WindowsServiceManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Check if the Windows Service is installed
    /// </summary>
    public bool IsServiceInstalled()
    {
        try
        {
            using var service = new ServiceController(SERVICE_NAME);
            var status = service.Status; // This will throw if service doesn't exist
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check if the Windows Service is running
    /// </summary>
    public bool IsServiceRunning()
    {
        try
        {
            using var service = new ServiceController(SERVICE_NAME);
            return service.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get the current status of the Windows Service
    /// </summary>
    public ServiceStatus GetServiceStatus()
    {
        try
        {
            if (!IsServiceInstalled())
            {
                return ServiceStatus.NotInstalled;
            }

            using var service = new ServiceController(SERVICE_NAME);
            return service.Status switch
            {
                ServiceControllerStatus.Running => ServiceStatus.Running,
                ServiceControllerStatus.Stopped => ServiceStatus.Stopped,
                ServiceControllerStatus.StartPending => ServiceStatus.Starting,
                ServiceControllerStatus.StopPending => ServiceStatus.Stopping,
                _ => ServiceStatus.Unknown
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error getting service status");
            return ServiceStatus.Error;
        }
    }

    /// <summary>
    /// Start the Windows Service
    /// </summary>
    public async Task<bool> StartServiceAsync()
    {
        try
        {
            if (!IsServiceInstalled())
            {
                _logger.LogWarning("⚠️ Service not installed");
                return false;
            }

            using var service = new ServiceController(SERVICE_NAME);
            
            if (service.Status == ServiceControllerStatus.Running)
            {
                _logger.LogInformation("✅ Service already running");
                return true;
            }

            _logger.LogInformation("🚀 Starting Windows Service...");
            service.Start();
            
            // Wait for service to start (max 30 seconds)
            await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)));
            
            _logger.LogInformation("✅ Windows Service started successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to start Windows Service");
            return false;
        }
    }

    /// <summary>
    /// Stop the Windows Service
    /// </summary>
    public async Task<bool> StopServiceAsync()
    {
        try
        {
            if (!IsServiceInstalled())
            {
                _logger.LogWarning("⚠️ Service not installed");
                return false;
            }

            using var service = new ServiceController(SERVICE_NAME);
            
            if (service.Status == ServiceControllerStatus.Stopped)
            {
                _logger.LogInformation("✅ Service already stopped");
                return true;
            }

            _logger.LogInformation("🛑 Stopping Windows Service...");
            service.Stop();
            
            // Wait for service to stop (max 30 seconds)
            await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)));
            
            _logger.LogInformation("✅ Windows Service stopped successfully");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to stop Windows Service");
            return false;
        }
    }

    /// <summary>
    /// Restart the Windows Service
    /// </summary>
    public async Task<bool> RestartServiceAsync()
    {
        _logger.LogInformation("🔄 Restarting Windows Service...");
        
        var stopped = await StopServiceAsync();
        if (!stopped)
        {
            return false;
        }

        await Task.Delay(2000); // Wait 2 seconds
        
        return await StartServiceAsync();
    }

    /// <summary>
    /// Install the Windows Service (requires admin privileges)
    /// </summary>
    public async Task<bool> InstallServiceAsync()
    {
        try
        {
            _logger.LogInformation("📦 Installing Windows Service...");
            
            // Get the path to the background service executable
            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            var servicePath = Path.Combine(currentDir, "..", "OkCloud.BackgroundService", "bin", "Release", "net8.0", "OkCloud.BackgroundService.exe");
            
            if (!File.Exists(servicePath))
            {
                _logger.LogError($"❌ Service executable not found: {servicePath}");
                return false;
            }

            // Use sc command to install service
            var processInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sc",
                Arguments = $"create \"{SERVICE_NAME}\" binPath= \"{servicePath}\" start= auto DisplayName= \"OkCloud Background Sync Service\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(processInfo);
            if (process != null)
            {
                await process.WaitForExitAsync();
                
                if (process.ExitCode == 0)
                {
                    _logger.LogInformation("✅ Windows Service installed successfully");
                    
                    // Set service description
                    var descProcess = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "sc",
                        Arguments = $"description \"{SERVICE_NAME}\" \"Provides background file synchronization for OkCloud when the main application is closed\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    
                    using var descProc = System.Diagnostics.Process.Start(descProcess);
                    await descProc?.WaitForExitAsync()!;
                    
                    return true;
                }
                else
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    _logger.LogError($"❌ Failed to install service: {error}");
                    return false;
                }
            }
            
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error installing Windows Service");
            return false;
        }
    }
}

public enum ServiceStatus
{
    NotInstalled,
    Running,
    Stopped,
    Starting,
    Stopping,
    Unknown,
    Error
}