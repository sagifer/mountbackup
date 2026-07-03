using MountBackup.Services;
using MountBackup.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace MountBackup {

    [Export(typeof(IPluginManifest))]
    public class MountBackupPlugin : PluginBase, INotifyPropertyChanged {
        private readonly MountBackupService service;
        private readonly IProfileService profileService;

        [ImportingConstructor]
        public MountBackupPlugin(IProfileService profileService, ITelescopeMediator telescopeMediator) {
            this.profileService = profileService;
            service = MountBackupService.GetOrCreate(profileService, telescopeMediator);
            service.StateChanged += OnServiceStateChanged;
            profileService.ProfileChanged += OnProfileChanged;
            ResetCommand = new AsyncRelayCommand(() => service.ResetAsync());
        }

        public bool AutoRestoreEnabled {
            get => service.AutoRestoreEnabled;
            set {
                service.AutoRestoreEnabled = value;
                RaisePropertyChanged();
            }
        }

        public double DeviationThresholdDegrees {
            get => service.DeviationThresholdDegrees;
            set {
                service.DeviationThresholdDegrees = value;
                RaisePropertyChanged();
            }
        }

        public bool WatchdogEnabled {
            get => service.WatchdogEnabled;
            set {
                service.WatchdogEnabled = value;
                RaisePropertyChanged();
            }
        }

        public int WatchdogTimeoutSeconds {
            get => service.WatchdogTimeoutSeconds;
            set {
                service.WatchdogTimeoutSeconds = value;
                RaisePropertyChanged();
            }
        }

        public bool LoggingEnabled {
            get => service.LoggingEnabled;
            set {
                service.LoggingEnabled = value;
                RaisePropertyChanged();
            }
        }

        public string PositionFilePath => service.CurrentFilePath;

        public ICommand ResetCommand { get; }

        private void OnServiceStateChanged() {
            RaisePropertyChanged(nameof(PositionFilePath));
        }

        private void OnProfileChanged(object sender, EventArgs e) {
            RaisePropertyChanged(nameof(AutoRestoreEnabled));
            RaisePropertyChanged(nameof(DeviationThresholdDegrees));
            RaisePropertyChanged(nameof(WatchdogEnabled));
            RaisePropertyChanged(nameof(WatchdogTimeoutSeconds));
            RaisePropertyChanged(nameof(LoggingEnabled));
            RaisePropertyChanged(nameof(PositionFilePath));
        }

        public override async Task Teardown() {
            profileService.ProfileChanged -= OnProfileChanged;
            service.StateChanged -= OnServiceStateChanged;
            await MountBackupService.DisposeInstanceAsync();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
