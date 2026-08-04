using System;
using System.ComponentModel;

namespace ZX0ai.Models
{
    public enum ActivityState
    {
        Pending,
        Running,
        Completed,
        Failed,
        Skipped
    }

    public sealed class ActivityEntry : INotifyPropertyChanged
    {
        public Guid Id { get; } = Guid.NewGuid();

        private string _title = string.Empty;
        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(nameof(Title)); }
        }

        private string? _details;
        public string? Details
        {
            get => _details;
            set { _details = value; OnPropertyChanged(nameof(Details)); }
        }

        private ActivityState _state = ActivityState.Pending;
        public ActivityState State
        {
            get => _state;
            set { _state = value; OnPropertyChanged(nameof(State)); }
        }

        private double _progress;
        public double Progress
        {
            get => _progress;
            set { _progress = value; OnPropertyChanged(nameof(Progress)); }
        }

        public DateTime Timestamp { get; } = DateTime.Now;

        public string TimestampText => Timestamp.ToString("HH:mm:ss");

        // Small output buffer for terminal/build streams
        private string _output = string.Empty;
        public string Output
        {
            get => _output;
            set { _output = value; OnPropertyChanged(nameof(Output)); }
        }

        // Icon key (resource name) to pick a vector icon
        public string IconKey { get; set; } = "IconSearch";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
