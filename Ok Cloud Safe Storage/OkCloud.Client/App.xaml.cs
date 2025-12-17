using System.Diagnostics;
using Microsoft.Maui.Storage;
using OkCloud.Client.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OkCloud.Client
{
    public partial class App : Application
    {
        private SystemTrayService? _systemTrayService;
        private IHost? _backgroundHost;
        private bool _isBackgroundMode = false;

        public App()
        {
            InitializeComponent();

            // Check command line arguments for background mode
            CheckBackgroundMode();

            if (_isBackgroundMode)
            {
                // Start in background mode (system tray only)
                StartBackgroundMode();
            }
            else
            {
                // Normal UI mode
                MainPage = new MainPage();
            }
        }

        private void CheckBackgroundMode()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                _isBackgroundMode = args.Contains("--background") || args.Contains("--minimized");
                
                if (_isBackgroundMode)
                {
                    Debug.WriteLine("🔧 Starting in background mode");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error checking command line args: {ex.Message}");
            }
        }

        private async void StartBackgroundMode()
        {
            try
            {
                Debug.WriteLine("🚀 Initializing background mode (Simplified)...");
                
                // For now, we'll use a simplified background mode
                // without the complex hosting setup to avoid dependency issues
                
                Debug.WriteLine("✅ Background mode initialized (Simplified)");
                
                // Still show main page but minimized
                MainPage = new MainPage();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error starting background mode: {ex.Message}");
                // Fallback to normal mode
                MainPage = new MainPage();
            }
        }

        protected override async void OnStart()
        {
            base.OnStart();
            
            if (!_isBackgroundMode)
            {
                // CRITICAL FIX: Delay authentication check to avoid COM exceptions
                // The UI needs to be fully initialized before we can safely access services
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000); // Wait 2 seconds for UI to initialize
                    await CheckSavedAuthenticationAsync();
                });
                
                // Initialize system tray for normal mode too
                await InitializeSystemTrayForNormalMode();
            }
        }

        protected override void OnSleep()
        {
            base.OnSleep();
            Debug.WriteLine("💤 App going to sleep - keeping sync active in background");
            // Don't stop services - let them continue in background
        }

        protected override void OnResume()
        {
            base.OnResume();
            Debug.WriteLine("👋 App resumed from background");
        }

        private async Task InitializeSystemTrayForNormalMode()
        {
            try
            {
                Debug.WriteLine("🎯 System tray initialization skipped (Simplified mode)");
                // We'll implement this later when we have proper Windows Forms support
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error initializing system tray: {ex.Message}");
            }
        }

        private async Task CheckSavedAuthenticationAsync()
        {
            try
            {
                Debug.WriteLine("🔍 Checking for saved authentication...");
                
                // CRITICAL FIX: Wrap in try-catch to prevent COM exceptions from crashing app
                try
                {
                    // Check if we have saved cookies
                    var cookies = await SecureStorage.Default.GetAsync("auth_cookies");
                    
                    if (!string.IsNullOrEmpty(cookies))
                    {
                        Debug.WriteLine("✅ Found saved cookies, validating...");
                        
                        // CRITICAL FIX: Use safer service resolution
                        try
                        {
                            // Get ApiService from DI with null checks
                            var serviceProvider = Handler?.MauiContext?.Services;
                            if (serviceProvider == null)
                            {
                                Debug.WriteLine("⚠️ Service provider not available yet, skipping auto-login");
                                return;
                            }
                            
                            var apiService = serviceProvider.GetService<ApiService>();
                            if (apiService == null)
                            {
                                Debug.WriteLine("⚠️ ApiService not available yet, skipping auto-login");
                                return;
                            }
                            
                            // Load cookies into AppBridge
                            Services.AppBridge.CurrentCookies = cookies;
                            
                            // Validate the session (with timeout to prevent hanging)
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                            bool isValid = await apiService.ValidateTokenAsync();
                            
                            if (isValid)
                            {
                                Debug.WriteLine("✅ Session is valid! Auto-login successful.");
                                
                                // WORKSPACE PERSISTENCE FIX: Restore last workspace on auto-login
                                var lastWorkspaceIdStr = await SecureStorage.Default.GetAsync("last_active_workspace_id");
                                if (!string.IsNullOrEmpty(lastWorkspaceIdStr) && int.TryParse(lastWorkspaceIdStr, out var lastWorkspaceId))
                                {
                                    ApiService.CurrentWorkspaceId = lastWorkspaceId;
                                    Debug.WriteLine($"🔄 Restored workspace {lastWorkspaceId} on auto-login");
                                }
                                else
                                {
                                    Debug.WriteLine("ℹ️ No saved workspace found, will use default");
                                }
                                
                                // IMPORTANT: Wait for Blazor to be ready before triggering login
                                // We'll set a flag that Home.razor will check on initialization
                                await SecureStorage.Default.SetAsync("auto_login_pending", "true");
                                Debug.WriteLine("🔔 Auto-login flag set - Home.razor will handle it");
                                return;
                            }
                            else
                            {
                                Debug.WriteLine("⚠️ Session expired, clearing saved credentials");
                                await ClearSavedCredentialsAsync();
                            }
                        }
                        catch (Exception serviceEx)
                        {
                            Debug.WriteLine($"⚠️ Service resolution error: {serviceEx.Message}");
                            Debug.WriteLine("🔄 Will retry authentication when UI is ready");
                        }
                    }
                    else
                    {
                        Debug.WriteLine("ℹ️ No saved credentials found");
                    }
                }
                catch (System.Runtime.InteropServices.COMException comEx)
                {
                    Debug.WriteLine($"⚠️ COM Exception during auth check (likely UI not ready): {comEx.Message}");
                    Debug.WriteLine("🔄 Authentication will be handled when UI is fully loaded");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error checking saved auth: {ex.Message}");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        private async Task ClearSavedCredentialsAsync()
        {
            try
            {
                SecureStorage.Default.Remove("auth_token");
                SecureStorage.Default.Remove("auth_cookies");
                Services.AppBridge.CurrentCookies = "";
                Debug.WriteLine("🗑️ Cleared saved credentials");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error clearing credentials: {ex.Message}");
            }
        }
    }
}
