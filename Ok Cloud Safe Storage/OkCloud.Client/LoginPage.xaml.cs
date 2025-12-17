using System.Diagnostics;
using OkCloud.Client.Services;

namespace OkCloud.Client
{
    public partial class LoginPage : ContentPage
    {
        private const string LoginUrl = "https://cloud.oksite.se/login";
        private bool _isChecking = false;

        public LoginPage()
        {
            InitializeComponent();
            LoginWebView.Source = LoginUrl;
            LoginWebView.Navigated += LoginWebView_Navigated;
        }

        private void LoginWebView_Navigating(object? sender, WebNavigatingEventArgs e)
        {
            Debug.WriteLine($"🔄 Navigating to: {e.Url}");
            
            if (LoadingSpinner != null)
            {
                LoadingSpinner.IsVisible = true;
                LoadingSpinner.IsRunning = true;
            }
        }

        private async void LoginWebView_Navigated(object? sender, WebNavigatedEventArgs e)
        {
            Debug.WriteLine($"✅ Navigated to: {e.Url}");
            Debug.WriteLine($"   Result: {e.Result}");

            if (LoadingSpinner != null)
            {
                LoadingSpinner.IsVisible = false;
                LoadingSpinner.IsRunning = false;
            }

            // إظهار زر الإغلاق اليدوي دائماً بعد أي تنقل ناجح
            if (e.Result == WebNavigationResult.Success)
            {
                // انتظر ثانية واحدة ثم أظهر الزر
                await Task.Delay(1000);
                
                if (CloseButton != null)
                {
                    Debug.WriteLine("🟠 Showing manual close button");
                    CloseButton.IsVisible = true;
                }
            }

            // Check if we're on a page where user should be logged in
            if ((e.Url.Contains("drive") || e.Url.Contains("dashboard") || e.Url == "https://cloud.oksite.se/") && !_isChecking)
            {
                _isChecking = true;
                Debug.WriteLine("🎯 Login page detected. Starting Token Hunter...");
                
                // Start polling for token
                await StartTokenPolling();
            }
            else if (!e.Url.Contains("login") && !_isChecking)
            {
                // أي صفحة غير صفحة Login تعني أن المستخدم سجل دخوله
                Debug.WriteLine("🎯 Non-login page detected. User might be logged in. Starting Token Hunter...");
                _isChecking = true;
                await StartTokenPolling();
            }
        }

        private async Task StartTokenPolling()
        {
            int attempts = 0;
            const int maxAttempts = 20;

            while (attempts < maxAttempts)
            {
                attempts++;
                Debug.WriteLine($"Checking for token and cookies (Attempt {attempts}/{maxAttempts})...");

                try
                {
                    // جافاسكريبت يُرجع كائناً يحتوي على التوكن والكوكيز
                    string script = @"
                        (function() {
                            var token = null;
                            
                            // 1. البحث عن التوكن
                            var keys = ['auth_token', 'access_token', 'token', 'api_token'];
                            for (var i = 0; i < keys.length; i++) {
                                var val = localStorage.getItem(keys[i]);
                                if (val && val.length > 20) { 
                                    token = val; 
                                    break; 
                                }
                            }
                            
                            if (!token) {
                                for (var key in localStorage) {
                                    var val = localStorage.getItem(key);
                                    if (val && typeof val === 'string' && val.startsWith('eyJ')) {
                                        token = val;
                                        break;
                                    }
                                }
                            }
                            
                            // 2. إرجاع النتيجة كـ JSON
                            if (token) {
                                return JSON.stringify({ t: token, c: document.cookie });
                            }
                            return null;
                        })();
                    ";

                    var jsonResult = await LoginWebView.EvaluateJavaScriptAsync(script);

                    if (!string.IsNullOrEmpty(jsonResult) && jsonResult != "null")
                    {
                        // فك التشفير البسيط
                        var cleanJson = jsonResult.Replace("\\\"", "\"").Trim('"');
                        
                        // استخراج التوكن والكوكيز يدوياً
                        var tokenStartIndex = cleanJson.IndexOf("\"t\":\"") + 5;
                        var tokenEndIndex = cleanJson.IndexOf("\",\"c\"");
                        
                        if (tokenStartIndex > 4 && tokenEndIndex > tokenStartIndex)
                        {
                            var token = cleanJson.Substring(tokenStartIndex, tokenEndIndex - tokenStartIndex);
                            
                            var cookieStartIndex = cleanJson.IndexOf("\"c\":\"") + 5;
                            var cookies = cleanJson.Substring(cookieStartIndex, cleanJson.LastIndexOf("\"") - cookieStartIndex);

                            Debug.WriteLine("🎉 Token and Cookies Extracted!");
                            Debug.WriteLine($"Token: {token.Substring(0, Math.Min(30, token.Length))}...");
                            Debug.WriteLine($"Cookies: {cookies.Substring(0, Math.Min(100, cookies.Length))}...");

                            // إرسال الاثنين للجسر
                            await AppBridge.SaveTokenAndNotify(token, cookies);

                            await Navigation.PopModalAsync();
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"⚠️ Polling Error: {ex.Message}");
                }

                await Task.Delay(1000);
            }

            Debug.WriteLine("⚠️ Timed out waiting for token in localStorage.");
            Debug.WriteLine("🍪 Attempting to extract cookies directly...");
            
            // Fallback: Just get cookies even without token
            try
            {
                var cookieScript = "document.cookie";
                var cookies = await LoginWebView.EvaluateJavaScriptAsync(cookieScript);
                
                if (!string.IsNullOrEmpty(cookies) && cookies != "null" && cookies.Length > 10)
                {
                    var cleanCookies = cookies.Trim('"');
                    Debug.WriteLine($"✅ Cookies extracted: {cleanCookies.Substring(0, Math.Min(100, cleanCookies.Length))}...");
                    
                    // Save with a dummy token
                    await AppBridge.SaveTokenAndNotify("browser-session", cleanCookies);
                    await Navigation.PopModalAsync();
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Cookie extraction failed: {ex.Message}");
            }
            
            _isChecking = false;
            
            // Show manual continue button
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                LoadingSpinner.IsVisible = false;
                ContinueButton.IsVisible = true;
            });
        }
        
        private async void ContinueButton_Clicked(object sender, EventArgs e)
        {
            Debug.WriteLine("Manual continue clicked - extracting cookies...");
            
            try
            {
                // محاولة استخراج الكوكيز من JavaScript
                var cookieScript = "document.cookie";
                var cookies = await LoginWebView.EvaluateJavaScriptAsync(cookieScript);
                
                if (!string.IsNullOrEmpty(cookies) && cookies != "null")
                {
                    var cleanCookies = cookies.Trim('"');
                    Debug.WriteLine($"✅ Cookies from JS: {cleanCookies.Substring(0, Math.Min(100, cleanCookies.Length))}...");
                    
                    // تحذير: الكوكيز من JavaScript قد لا تحتوي على laravel_session
                    // لذلك سنحاول طريقة بديلة
                    
                    // محاولة عمل طلب API مباشرة من الـ WebView لاستخراج الكوكيز الكاملة
                    var testScript = @"
                        fetch('https://cloud.oksite.se/api/v1/auth/user', {
                            credentials: 'include',
                            headers: {
                                'Accept': 'application/json',
                                'X-Requested-With': 'XMLHttpRequest'
                            }
                        })
                        .then(r => r.ok ? 'SUCCESS' : 'FAILED')
                        .catch(e => 'ERROR');
                    ";
                    
                    var testResult = await LoginWebView.EvaluateJavaScriptAsync(testScript);
                    Debug.WriteLine($"🧪 API Test from WebView: {testResult}");
                    
                    if (testResult?.Contains("SUCCESS") == true)
                    {
                        Debug.WriteLine("✅ WebView has valid session! Using alternative approach...");
                        
                        // بدلاً من استخراج الكوكيز، سنخبر المستخدم أن يستخدم Email/Password
                        await DisplayAlert(
                            "تسجيل الدخول ناجح!",
                            "لكن لا يمكن استخراج جلسة المتصفح بسبب قيود الأمان.\n\nالرجاء استخدام \"Sign in with Email/Password\" بدلاً من ذلك.",
                            "حسناً"
                        );
                        await Navigation.PopModalAsync();
                        return;
                    }
                    
                    await AppBridge.SaveTokenAndNotify("browser-session", cleanCookies);
                    await Navigation.PopModalAsync();
                }
                else
                {
                    await DisplayAlert(
                        "خطأ",
                        "لم يتم العثور على كوكيز.\n\nالرجاء استخدام \"Sign in with Email/Password\" للحصول على أفضل النتائج.",
                        "حسناً"
                    );
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ Error: {ex.Message}");
                await DisplayAlert("Error", $"Failed to extract cookies: {ex.Message}", "OK");
            }
        }

        private async void OnBackClicked(object sender, EventArgs e)
        {
            Debug.WriteLine("Back button clicked - closing login page");
            await Navigation.PopModalAsync();
        }
    }
}
