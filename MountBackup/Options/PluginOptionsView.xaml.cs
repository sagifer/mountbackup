using System.ComponentModel.Composition;
using System.Windows;

namespace MountBackup.Options {

    [Export(typeof(ResourceDictionary))]
    public partial class PluginOptionsView : ResourceDictionary {

        public PluginOptionsView() {
            InitializeComponent();
        }
    }
}
