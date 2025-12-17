using System.Diagnostics;
using Microsoft.Maui.Storage;

namespace OkCloud.Client.Services
{
    /// <summary>
    /// جسر التواصل بين LoginPage و Home.razor
    /// </summary>
    public static class AppBridge
    {
        public static event Action<string>? OnTokenReceived;
        public static event Action? OnSessionAuthenticated;
        public static event Action? OnLogout;
        
        // تخزين مؤقت للكوكيز
        public static string CurrentCookies { get; set; } = string.Empty;

        public static async Task SaveTokenAndNotify(string token, string cookies = "")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    Debug.WriteLine("⚠️ AppBridge: Empty token received");
                    return;
                }

                await SecureStorage.Default.SetAsync("auth_token", token);
                Debug.WriteLine($"✅ Token saved: {token.Substring(0, Math.Min(20, token.Length))}...");

                // حفظ الكوكيز (الأهم!)
                if (!string.IsNullOrWhiteSpace(cookies))
                {
                    await SecureStorage.Default.SetAsync("auth_cookies", cookies);
                    CurrentCookies = cookies;
                    Debug.WriteLine($"✅ Cookies saved: {cookies.Substring(0, Math.Min(50, cookies.Length))}...");
                }

                OnTokenReceived?.Invoke(token);
                Debug.WriteLine("✅ OnTokenReceived event fired");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ AppBridge Error: {ex.Message}");
            }
        }

        public static async Task NotifySessionAuthenticated()
        {
            try
            {
                await SecureStorage.Default.SetAsync("auth_type", "session");
                Debug.WriteLine("✅ Session authentication successful");

                OnSessionAuthenticated?.Invoke();
                Debug.WriteLine("✅ OnSessionAuthenticated event fired");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ AppBridge Error: {ex.Message}");
            }
        }

        public static async Task LogoutAsync()
        {
            try
            {
                Debug.WriteLine("🚪 Logging out...");
                
                // Clear all saved credentials
                SecureStorage.Default.Remove("auth_token");
                SecureStorage.Default.Remove("auth_cookies");
                SecureStorage.Default.Remove("auth_type");
                CurrentCookies = string.Empty;
                
                Debug.WriteLine("✅ Credentials cleared");
                
                // Notify listeners (Home.razor will reset to login screen)
                OnLogout?.Invoke();
                Debug.WriteLine("✅ OnLogout event fired");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Logout Error: {ex.Message}");
            }
        }
    }
}