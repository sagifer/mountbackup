using System;
using System.Globalization;

namespace MountBackup.Services {

    /// <summary>
    /// One saved mount position sample. Alt/Az + site describe the physical pose in a
    /// time-independent way; RA/Dec/epoch/pier side are informational extras for debugging.
    /// </summary>
    public sealed record SavedPosition(
        DateTime TimestampUtc,
        double AltDeg,
        double AzDeg,
        double SiteLatDeg,
        double SiteLonDeg,
        double SiteElevM,
        double RaHours,
        double DecDeg,
        string Epoch,
        string PierSide) {

        private const int Version = 1;
        private const int FieldCount = 11;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public string ToCsvLine() {
            return string.Join(",",
                Version.ToString(Inv),
                TimestampUtc.ToString("o", Inv),
                AltDeg.ToString("R", Inv),
                AzDeg.ToString("R", Inv),
                SiteLatDeg.ToString("R", Inv),
                SiteLonDeg.ToString("R", Inv),
                SiteElevM.ToString("R", Inv),
                RaHours.ToString("R", Inv),
                DecDeg.ToString("R", Inv),
                Epoch ?? "",
                PierSide ?? "");
        }

        public static bool TryParse(string line, out SavedPosition position) {
            position = null;
            if (string.IsNullOrWhiteSpace(line)) { return false; }

            var parts = line.Split(',');
            if (parts.Length != FieldCount) { return false; }
            if (!int.TryParse(parts[0], NumberStyles.Integer, Inv, out var version) || version != Version) { return false; }
            if (!DateTime.TryParse(parts[1], Inv, DateTimeStyles.RoundtripKind, out var timestamp)) { return false; }
            if (!TryParseDouble(parts[2], out var alt) || alt < -90 || alt > 90) { return false; }
            if (!TryParseDouble(parts[3], out var az) || az < 0 || az >= 360) { return false; }
            if (!TryParseDouble(parts[4], out var lat) || lat < -90 || lat > 90) { return false; }
            if (!TryParseDouble(parts[5], out var lon) || lon < -180 || lon > 180) { return false; }
            if (!TryParseDouble(parts[6], out var elev)) { return false; }
            if (!TryParseDouble(parts[7], out var ra)) { return false; }
            if (!TryParseDouble(parts[8], out var dec)) { return false; }

            position = new SavedPosition(timestamp, alt, az, lat, lon, elev, ra, dec, parts[9], parts[10]);
            return true;
        }

        private static bool TryParseDouble(string s, out double value) {
            return double.TryParse(s, NumberStyles.Float, Inv, out value) && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public bool SamePoseAs(SavedPosition other) {
            return other != null
                && AltDeg == other.AltDeg
                && AzDeg == other.AzDeg
                && SiteLatDeg == other.SiteLatDeg
                && SiteLonDeg == other.SiteLonDeg;
        }
    }
}
