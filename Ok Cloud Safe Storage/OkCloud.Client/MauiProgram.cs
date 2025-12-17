using Microsoft.Extensions.Logging;
using OkCloud.Client.Services;

namespace OkCloud.Client
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();

#if DEBUG
            builder.Services.AddBlazorWebViewDeveloperTools();
            builder.Logging.AddDebug();
#endif

            builder.Services.AddSingleton<ApiService>();
            builder.Services.AddSingleton<LocalDatabaseService>();
            builder.Services.AddSingleton<SyncService>();
            builder.Services.AddSingleton<OfflineManager>();
            builder.Services.AddSingleton<FileWatcherService>();
            builder.Services.AddSingleton<UploadService>();
            builder.Services.AddSingleton<SystemTrayService>();
            builder.Services.AddSingleton<StartupManager>();
            builder.Services.AddSingleton<OkCloudBackgroundService>();
            builder.Services.AddSingleton<WindowsServiceManager>();

            return builder.Build();
        }
    }
}