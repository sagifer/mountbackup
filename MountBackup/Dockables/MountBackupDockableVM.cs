using MountBackup.Model;
using MountBackup.Services;
using MountBackup.Utility;
using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MountBackup.Dockables {

    [Export(typeof(IDockableVM))]
    public class MountBackupDockableVM : DockableVM {
        private readonly MountBackupService service;
        private readonly DispatcherTimer refreshTimer;

        [ImportingConstructor]
        public MountBackupDockableVM(IProfileService profileService, ITelescopeMediator telescopeMediator)
            : base(profileService) {
            Title = "Mount Backup";

            var dict = new ResourceDictionary {
                Source = new Uri("MountBackup;component/Dockables/MountBackupDockableView.xaml", UriKind.RelativeOrAbsolute)
            };
            ImageGeometry = (GeometryGroup)dict["MountBackup_IconSVG"];
            ImageGeometry.Freeze();

            service = MountBackupService.GetOrCreate(profileService, telescopeMediator);
            foreach (var entry in service.GetRecentLogSnapshot()) {
                LogEntries.Add(entry);
            }
            service.LogEmitted += OnLogEmitted;
            service.StateChanged += OnServiceStateChanged;
            profileService.ProfileChanged += OnProfileChanged;

            ResetCommand = new AsyncRelayCommand(() => service.ResetAsync());
            RestoreNowCommand = new AsyncRelayCommand(() => service.RestoreNowAsync());

            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            refreshTimer.Tick += (s, e) => {
                RaisePropertyChanged(nameof(SavedAgeText));
                RaisePropertyChanged(nameof(CurrentEquivalentRaDecText));
            };
            refreshTimer.Start();
        }

        public ObservableCollection<PluginLogEntry> LogEntries { get; } = new ObservableCollection<PluginLogEntry>();

        public ICommand ResetCommand { get; }

        public ICommand RestoreNowCommand { get; }

        public bool AutoRestoreEnabled {
            get => service.AutoRestoreEnabled;
            set {
                service.AutoRestoreEnabled = value;
                RaisePropertyChanged();
            }
        }

        public string SavedPoseText {
            get {
                var saved = service.LastSaved;
                return saved == null
                    ? "—"
                    : $"HA {AstroUtil.HoursToHMS(saved.HaHours)}  /  Dec {AstroUtil.DegreesToDMS(saved.DecDeg)}  /  {MountBackupService.FormatPier(saved.PierSide)}";
            }
        }

        public string SavedTimestampText {
            get {
                var saved = service.LastSaved;
                return saved == null ? "—" : saved.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            }
        }

        public string SavedAgeText {
            get {
                var saved = service.LastSaved;
                return saved == null ? "—" : MountBackupService.FormatAge(DateTime.UtcNow - saved.TimestampUtc) + " ago";
            }
        }

        public string CurrentEquivalentRaDecText {
            get {
                var saved = service.LastSaved;
                if (saved == null) { return "—"; }
                try {
                    var lst = AstroUtil.GetLocalSiderealTimeNow(saved.SiteLonDeg);
                    var ra = SavedPosition.Wrap24(lst - saved.HaHours);
                    return $"RA {AstroUtil.HoursToHMS(ra)}  /  Dec {AstroUtil.DegreesToDMS(saved.DecDeg)} (JNOW)";
                } catch (Exception ex) {
                    Logger.Error(ex);
                    return "?";
                }
            }
        }

        public string MountStatusText {
            get {
                var info = service.LastTelescopeInfo;
                if (info == null || !info.Connected) { return "not connected"; }
                if (info.AtPark) { return "connected — parked"; }
                if (info.Slewing) { return "connected — slewing"; }
                return info.TrackingEnabled ? "connected — tracking" : "connected — idle";
            }
        }

        public string RestoreStateText {
            get {
                switch (service.State) {
                    case RestoreState.NotConnected: return "waiting for mount";
                    case RestoreState.RestorePending: return "restore pending (saving paused)";
                    case RestoreState.WaitingForUnpark: return "waiting for unpark (saving paused)";
                    case RestoreState.Restoring: return "restoring…";
                    default: return "monitoring — saving position";
                }
            }
        }

        private void OnServiceStateChanged() {
            RaisePropertyChanged(nameof(SavedPoseText));
            RaisePropertyChanged(nameof(SavedTimestampText));
            RaisePropertyChanged(nameof(SavedAgeText));
            RaisePropertyChanged(nameof(MountStatusText));
            RaisePropertyChanged(nameof(RestoreStateText));
            RaisePropertyChanged(nameof(AutoRestoreEnabled));
        }

        private void OnProfileChanged(object sender, EventArgs e) {
            RaisePropertyChanged(nameof(AutoRestoreEnabled));
        }

        private void OnLogEmitted(PluginLogEntry entry) {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) { return; }
            dispatcher.BeginInvoke(new Action(() => {
                LogEntries.Insert(0, entry);
                while (LogEntries.Count > 200) {
                    LogEntries.RemoveAt(LogEntries.Count - 1);
                }
            }));
        }
    }
}
