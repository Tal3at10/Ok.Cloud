using System.Diagnostics;

namespace OkCloud.Client
{
    public partial class BatchUploadProgressPage : ContentPage
    {
        private bool _isCancelled = false;
        private TaskCompletionSource<bool>? _completionSource;

        public bool IsCancelled => _isCancelled;

        public BatchUploadProgressPage()
        {
            InitializeComponent();
        }

        public void UpdateProgress(int current, int total, string currentFileName)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ProgressLabel.Text = $"{current} / {total} files";
                CurrentFileLabel.Text = $"Uploading: {currentFileName}";
                
                var percentage = total > 0 ? (double)current / total : 0;
                ProgressBar.Progress = percentage;
                PercentageLabel.Text = $"{(int)(percentage * 100)}%";
            });
        }

        public void ShowSuccess(int successCount, int failCount)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (failCount == 0)
                {
                    StatusLabel.Text = $"✅ All {successCount} files uploaded successfully!";
                    StatusLabel.TextColor = Color.FromArgb("#10b981");
                }
                else
                {
                    StatusLabel.Text = $"✅ {successCount} uploaded, ❌ {failCount} failed";
                    StatusLabel.TextColor = Color.FromArgb("#f59e0b");
                }
                StatusLabel.IsVisible = true;
                
                CancelButton.Text = "Close";
                CancelButton.BackgroundColor = Color.FromArgb("#ff5500");
            });
        }

        private async void OnCancelClicked(object sender, EventArgs e)
        {
            if (CancelButton.Text == "Close")
            {
                await Navigation.PopModalAsync();
            }
            else
            {
                _isCancelled = true;
                CancelButton.IsEnabled = false;
                CancelButton.Text = "Cancelling...";
                StatusLabel.Text = "Cancelling upload...";
                StatusLabel.TextColor = Color.FromArgb("#ef4444");
                StatusLabel.IsVisible = true;
            }
        }

        public Task WaitForCompletionAsync()
        {
            _completionSource = new TaskCompletionSource<bool>();
            return _completionSource.Task;
        }

        public void Complete()
        {
            _completionSource?.SetResult(true);
        }
    }
}
