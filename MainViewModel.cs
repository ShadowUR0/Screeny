using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ScreenTimeTracker.Models;

namespace ScreenTimeTracker
{
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<AppUsageRecord> Records { get; } = new();
        public ObservableCollection<AppUsageRecord> AggregatedRecords { get; } = new();

        private DateTime _selectedDate = DateTime.Today;
        public DateTime SelectedDate
        {
            get => _selectedDate;
            set
            {
                if (!SetProperty(ref _selectedDate, value)) return;
                OnPropertyChanged(nameof(FormattedSelectedDate));
                OnPropertyChanged(nameof(FriendlySelectedDate));
            }
        }

        private DateTime? _selectedEndDate;
        public DateTime? SelectedEndDate
        {
            get => _selectedEndDate;
            set => SetProperty(ref _selectedEndDate, value);
        }

        private bool _isDateRangeSelected;
        public bool IsDateRangeSelected
        {
            get => _isDateRangeSelected;
            set => SetProperty(ref _isDateRangeSelected, value);
        }

        private TimePeriod _currentTimePeriod = TimePeriod.Daily;
        public TimePeriod CurrentTimePeriod
        {
            get => _currentTimePeriod;
            set => SetProperty(ref _currentTimePeriod, value);
        }

        private ChartViewMode _currentChartViewMode = ChartViewMode.Hourly;
        public ChartViewMode CurrentChartViewMode
        {
            get => _currentChartViewMode;
            set
            {
                if (!SetProperty(ref _currentChartViewMode, value)) return;
                OnPropertyChanged(nameof(ChartViewModeLabel));
            }
        }

        private bool _isTracking;
        public bool IsTracking
        {
            get => _isTracking;
            set
            {
                if (!SetProperty(ref _isTracking, value)) return;
                OnPropertyChanged(nameof(TrackingStatusText));
            }
        }

        private PersistenceHealthStatus _persistenceHealth = PersistenceHealthStatus.Healthy;
        public PersistenceHealthStatus PersistenceHealth
        {
            get => _persistenceHealth;
            set
            {
                if (!SetProperty(ref _persistenceHealth, value)) return;
                OnPropertyChanged(nameof(TrackingStatusText));
            }
        }

        public string TrackingStatusText => PersistenceHealth switch
        {
            PersistenceHealthStatus.FatalIssue => "Data issue",
            PersistenceHealthStatus.RetryableIssue => "Saving delayed",
            _ => IsTracking ? "Active" : "Paused"
        };

        public string ChartViewModeLabel => CurrentChartViewMode == ChartViewMode.Hourly ? "Hourly View" : "Daily View";
        public string FormattedSelectedDate => SelectedDate.ToString("MMM dd, yyyy");
        public string FriendlySelectedDate => SelectedDate.Date == DateTime.Today
            ? "Today"
            : SelectedDate.Date == DateTime.Today.AddDays(-1)
                ? "Yesterday"
                : SelectedDate.ToString("ddd, MMM d");

        public ICommand StartTrackingCommand { get; }
        public ICommand StopTrackingCommand { get; }
        public ICommand ToggleTrackingCommand { get; }
        public ICommand PickDateCommand { get; }
        public ICommand ToggleViewModeCommand { get; }

        public MainViewModel()
        {
            StartTrackingCommand = new RelayCommand(_ => OnStartTrackingRequested?.Invoke(this, EventArgs.Empty));
            StopTrackingCommand = new RelayCommand(_ => OnStopTrackingRequested?.Invoke(this, EventArgs.Empty));
            ToggleTrackingCommand = new RelayCommand(_ => OnToggleTrackingRequested?.Invoke(this, EventArgs.Empty));
            PickDateCommand = new RelayCommand(_ => OnPickDateRequested?.Invoke(this, EventArgs.Empty));
            ToggleViewModeCommand = new RelayCommand(_ => OnToggleViewModeRequested?.Invoke(this, EventArgs.Empty));
        }

        public event EventHandler? OnStartTrackingRequested;
        public event EventHandler? OnStopTrackingRequested;
        public event EventHandler? OnToggleTrackingRequested;
        public event EventHandler? OnPickDateRequested;
        public event EventHandler? OnToggleViewModeRequested;
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private sealed class RelayCommand : ICommand
        {
            private readonly Action<object?> _execute;
            private readonly Func<object?, bool>? _canExecute;

            public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
            {
                _execute = execute;
                _canExecute = canExecute;
            }

            public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
            public void Execute(object? parameter) => _execute(parameter);
            public event EventHandler? CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
