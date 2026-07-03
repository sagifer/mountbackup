using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MountBackup.Services {

    /// <summary>
    /// All file I/O for the position backup file. One CSV line is appended per sample; when the
    /// file grows beyond <see cref="RotationThresholdBytes"/> it is atomically rewritten with only
    /// the last valid line. A crash can only ever tear the final line, which the backward-scanning
    /// loader skips.
    /// </summary>
    public sealed class PositionFileStore : IDisposable {
        private const long RotationThresholdBytes = 256 * 1024;

        private readonly SemaphoreSlim ioLock = new SemaphoreSlim(1, 1);
        private readonly string directory;
        private string filePath;

        public PositionFileStore(Guid profileId) {
            directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA",
                "MountBackup");
            filePath = PathForProfile(profileId);
        }

        public string FilePath => filePath;

        private string PathForProfile(Guid profileId) {
            return Path.Combine(directory, $"position_{profileId:D}.csv");
        }

        public async Task SwitchProfileAsync(Guid profileId) {
            await ioLock.WaitAsync();
            try {
                filePath = PathForProfile(profileId);
            } finally {
                ioLock.Release();
            }
        }

        public async Task AppendAsync(SavedPosition position) {
            await ioLock.WaitAsync();
            try {
                Directory.CreateDirectory(directory);
                CleanupTempFile();

                long length;
                using (var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read)) {
                    var bytes = Encoding.UTF8.GetBytes(position.ToCsvLine() + "\n");
                    await stream.WriteAsync(bytes, 0, bytes.Length);
                    await stream.FlushAsync();
                    length = stream.Length;
                }

                if (length > RotationThresholdBytes) {
                    Rotate(position);
                }
            } finally {
                ioLock.Release();
            }
        }

        private void Rotate(SavedPosition lastPosition) {
            var tmp = filePath + ".tmp";
            File.WriteAllText(tmp, lastPosition.ToCsvLine() + "\n", Encoding.UTF8);
            File.Move(tmp, filePath, overwrite: true);
        }

        public async Task<SavedPosition> LoadLastAsync() {
            await ioLock.WaitAsync();
            try {
                CleanupTempFile();
                if (!File.Exists(filePath)) { return null; }

                var lines = await File.ReadAllLinesAsync(filePath);
                for (var i = lines.Length - 1; i >= 0; i--) {
                    if (SavedPosition.TryParse(lines[i], out var position)) {
                        return position;
                    }
                }
                return null;
            } finally {
                ioLock.Release();
            }
        }

        public async Task DeleteAsync() {
            await ioLock.WaitAsync();
            try {
                CleanupTempFile();
                if (File.Exists(filePath)) {
                    File.Delete(filePath);
                }
            } finally {
                ioLock.Release();
            }
        }

        private void CleanupTempFile() {
            var tmp = filePath + ".tmp";
            if (File.Exists(tmp)) {
                File.Delete(tmp);
            }
        }

        public void Dispose() {
            ioLock.Dispose();
        }
    }
}
