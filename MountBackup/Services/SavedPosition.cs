using NINA.Astrometry;
using System;
using System.Globalization;

namespace MountBackup.Services {

    /// <summary>
    /// One saved mount position sample. Hour angle + declination (JNOW) + pier side describe the
    /// mount's physical axis pose in a time-independent way: a powered-off mount keeps its HA/Dec
    /// no matter how much time passes, and — unlike a direction such as Alt/Az — the pair stays
    /// unambiguous even at the celestial pole, because the driver derives RA from the hour axis
    /// encoder. RA/Alt/Az at save time are informational extras for display and debugging.
    /// </summary>
    public sealed record SavedPosition(
        DateTime TimestampUtc,
        double HaHours,
        double DecDeg,
        double SiteLatDeg,
        double SiteLonDeg,
        double SiteElevM,
        double RaAtSaveHours,
        double AltDeg,
        double AzDeg,
        string PierSide) {

        private const int Version = 2;
        private const int FieldCount = 11;
        private const int V1FieldCount = 11;

        // dead-band for the "did the mount move" comparison: below this, LST/RA quantization
        // jitter of the driver would cause pointless writes and watchdog resets
        private const double HaDeadbandHours = 2.0 / 3600.0;   // 2 s of time (≈30" at the equator)
        private const double DecDeadbandDeg = 5.0 / 3600.0;    // 5"

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public string ToCsvLine() {
            return string.Join(",",
                Version.ToString(Inv),
                TimestampUtc.ToString("o", Inv),
                HaHours.ToString("R", Inv),
                DecDeg.ToString("R", Inv),
                SiteLatDeg.ToString("R", Inv),
                SiteLonDeg.ToString("R", Inv),
                SiteElevM.ToString("R", Inv),
                RaAtSaveHours.ToString("R", Inv),
                AltDeg.ToString("R", Inv),
                AzDeg.ToString("R", Inv),
                PierSide ?? "");
        }

        public static bool TryParse(string line, out SavedPosition position) {
            position = null;
            if (string.IsNullOrWhiteSpace(line)) { return false; }

            var parts = line.Split(',');
            if (parts.Length < 1 || !int.TryParse(parts[0], NumberStyles.Integer, Inv, out var version)) { return false; }

            switch (version) {
                case 2: return TryParseV2(parts, out position);
                case 1: return TryParseV1(parts, out position);
                default: return false;
            }
        }

        private static bool TryParseV2(string[] parts, out SavedPosition position) {
            position = null;
            if (parts.Length != FieldCount) { return false; }
            if (!DateTime.TryParse(parts[1], Inv, DateTimeStyles.RoundtripKind, out var timestamp)) { return false; }
            if (!TryParseDouble(parts[2], out var ha)) { return false; }
            if (!TryParseDouble(parts[3], out var dec) || dec < -90 || dec > 90) { return false; }
            if (!TryParseDouble(parts[4], out var lat) || lat < -90 || lat > 90) { return false; }
            if (!TryParseDouble(parts[5], out var lon) || lon < -180 || lon > 180) { return false; }
            if (!TryParseDouble(parts[6], out var elev)) { return false; }
            // informational fields — NaN is acceptable here
            ParseDoubleOrNaN(parts[7], out var ra);
            ParseDoubleOrNaN(parts[8], out var alt);
            ParseDoubleOrNaN(parts[9], out var az);

            position = new SavedPosition(timestamp, Wrap24(ha), dec, lat, lon, elev, ra, alt, az, parts[10]);
            return true;
        }

        /// <summary>
        /// Upgrades a version-1 (Alt/Az based) line. V1 also recorded the driver-reported RA/Dec,
        /// which — being encoder-derived — carries the hour axis information, so the pose converts
        /// losslessly: HA = LST(saved timestamp, saved site) − RA(JNOW).
        /// </summary>
        private static bool TryParseV1(string[] parts, out SavedPosition position) {
            position = null;
            if (parts.Length != V1FieldCount) { return false; }
            if (!DateTime.TryParse(parts[1], Inv, DateTimeStyles.RoundtripKind, out var timestamp)) { return false; }
            if (!TryParseDouble(parts[2], out var alt) || alt < -90 || alt > 90) { return false; }
            if (!TryParseDouble(parts[3], out var az) || az < 0 || az >= 360) { return false; }
            if (!TryParseDouble(parts[4], out var lat) || lat < -90 || lat > 90) { return false; }
            if (!TryParseDouble(parts[5], out var lon) || lon < -180 || lon > 180) { return false; }
            if (!TryParseDouble(parts[6], out var elev)) { return false; }
            if (!TryParseDouble(parts[7], out var ra)) { return false; }
            if (!TryParseDouble(parts[8], out var dec) || dec < -90 || dec > 90) { return false; }

            var raJnow = ra;
            var decJnow = dec;
            if (string.Equals(parts[9], nameof(NINA.Astrometry.Epoch.J2000), StringComparison.OrdinalIgnoreCase)) {
                var coords = new Coordinates(Angle.ByHours(ra), Angle.ByDegree(dec), Epoch.J2000).Transform(Epoch.JNOW);
                raJnow = coords.RA;
                decJnow = coords.Dec;
            }
            var lst = AstroUtil.GetLocalSiderealTime(timestamp, lon);
            var ha = AstroUtil.GetHourAngle(lst, raJnow);

            position = new SavedPosition(timestamp, ha, decJnow, lat, lon, elev, ra, alt, az, parts[10]);
            return true;
        }

        private static bool TryParseDouble(string s, out double value) {
            return double.TryParse(s, NumberStyles.Float, Inv, out value) && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static void ParseDoubleOrNaN(string s, out double value) {
            if (!double.TryParse(s, NumberStyles.Float, Inv, out value) || double.IsInfinity(value)) {
                value = double.NaN;
            }
        }

        /// <summary>Dead-banded comparison in axis space (HA/Dec/pier). Callers compare against
        /// the last WRITTEN pose, so slow real drift accumulates and always crosses the band.</summary>
        public bool SamePoseAs(SavedPosition other) {
            return other != null
                && Math.Abs(WrapPm12(HaHours - other.HaHours)) < HaDeadbandHours
                && Math.Abs(DecDeg - other.DecDeg) < DecDeadbandDeg
                && string.Equals(PierSide, other.PierSide, StringComparison.Ordinal)
                && SiteLatDeg == other.SiteLatDeg
                && SiteLonDeg == other.SiteLonDeg;
        }

        /// <summary>The direction a pose points to. Time-independent: Alt/Az follows from
        /// HA/Dec and the site latitude alone.</summary>
        public static (double AltDeg, double AzDeg) AltAzFromHaDec(double haHours, double decDeg, double latDeg) {
            const double d2r = Math.PI / 180.0;
            var ha = haHours * 15.0 * d2r;
            var dec = decDeg * d2r;
            var lat = latDeg * d2r;
            var sinAlt = Math.Sin(dec) * Math.Sin(lat) + Math.Cos(dec) * Math.Cos(lat) * Math.Cos(ha);
            var altDeg = Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0)) / d2r;
            // azimuth measured from north through east
            var azDeg = Math.Atan2(
                -Math.Sin(ha) * Math.Cos(dec),
                Math.Sin(dec) * Math.Cos(lat) - Math.Cos(dec) * Math.Sin(lat) * Math.Cos(ha)) / d2r;
            if (azDeg < 0) { azDeg += 360; }
            return (altDeg, azDeg);
        }

        public static double Wrap24(double hours) {
            hours %= 24;
            return hours < 0 ? hours + 24 : hours;
        }

        public static double WrapPm12(double hours) {
            var h = Wrap24(hours);
            return h > 12 ? h - 24 : h;
        }
    }
}
