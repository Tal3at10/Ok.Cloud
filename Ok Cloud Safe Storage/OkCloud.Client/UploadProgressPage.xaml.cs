using OkCloud.Client.Services;

namespace OkCloud.Client
{
    public partial class UploadProgressPage : ContentPage
    {
        private readonly UploadService _uploadService;
        private readonly string _filePath;
        private readonly string _fileName;

        public UploadProgressPage(UploadService uploadService, string filePath, string fileName)
        {
            InitializeComponent();
            _uploadService = uploadService;
            _filePath = filePath;
            _fileName = fileName;
            _uploadService.OnProgressChanged += OnProgressChanged;
            
            // Set initial file name
            FileNameLabel.Text = fileName;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            
            // Start upload when page appears
            await _uploadService.UploadFileWithProgressAsync(_filePath);
        }

        private void OnProgressChanged(UploadProgress progress)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                FileNameLabel.Text = progress.FileName;
                ProgressLabel.Text = $"{progress.Percentage}%";
                UploadProgressBar.Progress = progress.Percentage / 100.0;
                
                if (progress.SpeedMBps > 0)
                {
                    SpeedLabel.Text = $"{progress.SpeedMBps:F2} MB/s";
                }
                
                if (progress.EstimatedTimeRemaining.TotalSeconds > 0)
                {
                    var time = progress.EstimatedTimeRemaining;
                    if (time.TotalHours >= 1)
                        TimeRemainingLabel.Text = $"{(int)time.TotalHours}h {time.Minutes}m remaining";
                    else if (time.TotalMinutes >= 1)
                        TimeRemainingLabel.Text = $"{(int)time.TotalMinutes}m {time.Seconds}s remaining";
                    else
                        TimeRemainingLabel.Text = $"{time.Seconds}s remaining";
                }

                if (progress.IsCompleted)
                {
                    DisplayAlert("Success", $"'{progress.FileName}' uploaded successfully!", "OK");
                    Navigation.PopModalAsync();
                }
                else if (progress.IsCancelled)
                {
                    DisplayAlert("Cancelled", "Upload was cancelled.", "OK");
                    Navigation.PopModalAsync();
                }
                else if (!string.IsNullOrEmpty(progress.ErrorMessage))
                {
                    DisplayAlert("Upload Failed", progress.ErrorMessage, "OK");
                    Navigation.PopModalAsync();
                }
            });
        }

        private void OnCancelClicked(object sender, EventArgs e)
        {
            _uploadService.CancelUpload();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _uploadService.OnProgressChanged -= OnProgressChanged;
        }
    }
}
