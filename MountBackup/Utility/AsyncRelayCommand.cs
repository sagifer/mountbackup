using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace MountBackup.Utility {

    public class AsyncRelayCommand : ICommand {
        private readonly Func<Task> execute;
        private bool isExecuting;

        public AsyncRelayCommand(Func<Task> execute) {
            this.execute = execute;
        }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter) => !isExecuting;

        public async void Execute(object parameter) {
            isExecuting = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try {
                await execute();
            } catch (Exception ex) {
                NINA.Core.Utility.Logger.Error(ex);
            } finally {
                isExecuting = false;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
