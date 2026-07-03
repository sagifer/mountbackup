using System.ComponentModel.Composition;
using System.Windows;

namespace MountBackup.Dockables {

    [Export(typeof(ResourceDictionary))]
    public partial class MountBackupDockableView : ResourceDictionary {

        public MountBackupDockableView() {
            InitializeComponent();
        }
    }
}
