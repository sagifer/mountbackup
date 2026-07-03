namespace MountBackup.Model {

    public enum RestoreState {
        NotConnected,
        RestorePending,
        WaitingForUnpark,
        Restoring,
        Monitoring
    }
}
