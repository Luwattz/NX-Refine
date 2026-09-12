using System;
using System.Globalization;
using System.IO;

namespace NXRefine.Core
{
    internal sealed class CleanupSettings
    {
        public CleanupSettings()
        {
            SmallFaceArea = 1.0;
            ShortEdgeLength = 0.5;
            MaxBlendRadius = 3.0;
            MaxHoleDiameter = 8.0;
            SewTolerance = 0.01;
            SharpAngle = 10.0;
            RemoveMarkingsMaxHeightMm = 2.0;
            FillHolesMaxRadius = 4.0;
            ConfirmBeforeRepair = true;
        }

        public double SmallFaceArea { get; set; }
        public double ShortEdgeLength { get; set; }
        public double MaxBlendRadius { get; set; }
        public double MaxHoleDiameter { get; set; }
        public double SewTolerance { get; set; }
        public double SharpAngle { get; set; }
        public double RemoveMarkingsMaxHeightMm { get; set; }
        public double FillHolesMaxRadius { get; set; }
        public bool ConfirmBeforeRepair { get; set; }

        public static string SettingsPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NXRefine", "settings.ini");
            }
        }

        public static CleanupSettings Load()
        {
            var settings = new CleanupSettings();
            if (!File.Exists(SettingsPath)) return settings;

            foreach (string rawLine in File.ReadAllLines(SettingsPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                string[] parts = line.Split(new[] { '=' }, 2);
                if (parts.Length != 2) continue;
                double number;
                bool flag;
                switch (parts[0].Trim())
                {
                    case "SmallFaceArea": if (TryNumber(parts[1], out number)) settings.SmallFaceArea = number; break;
                    case "ShortEdgeLength": if (TryNumber(parts[1], out number)) settings.ShortEdgeLength = number; break;
                    case "MaxBlendRadius": if (TryNumber(parts[1], out number)) settings.MaxBlendRadius = number; break;
                    case "MaxHoleDiameter": if (TryNumber(parts[1], out number)) settings.MaxHoleDiameter = number; break;
                    case "SewTolerance": if (TryNumber(parts[1], out number)) settings.SewTolerance = number; break;
                    case "SharpAngle": if (TryNumber(parts[1], out number)) settings.SharpAngle = number; break;
                    case "RemoveMarkingsMaxHeightMm":
                        if (TryNumber(parts[1], out number) && number > 0.0 && number <= 100000.0)
                            settings.RemoveMarkingsMaxHeightMm = number;
                        break;
                    case "FillHolesMaxRadius":
                        if (TryNumber(parts[1], out number) && number <= 100000.0)
                            settings.FillHolesMaxRadius = number;
                        break;
                    case "ConfirmBeforeRepair": if (bool.TryParse(parts[1], out flag)) settings.ConfirmBeforeRepair = flag; break;
                }
            }
            return settings;
        }

        public void Save()
        {
            string directory = Path.GetDirectoryName(SettingsPath);
            Directory.CreateDirectory(directory);
            File.WriteAllLines(SettingsPath, new[]
            {
                "# NX Refine settings. Geometric thresholds use the current part unit; RemoveMarkingsMaxHeightMm is millimetres.",
                "SmallFaceArea=" + Format(SmallFaceArea),
                "ShortEdgeLength=" + Format(ShortEdgeLength),
                "MaxBlendRadius=" + Format(MaxBlendRadius),
                "MaxHoleDiameter=" + Format(MaxHoleDiameter),
                "SewTolerance=" + Format(SewTolerance),
                "SharpAngle=" + Format(SharpAngle),
                "RemoveMarkingsMaxHeightMm=" + Format(RemoveMarkingsMaxHeightMm),
                "FillHolesMaxRadius=" + Format(FillHolesMaxRadius),
                "ConfirmBeforeRepair=" + ConfirmBeforeRepair
            });
        }

        private static bool TryNumber(string value, out double result)
        {
            return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result) && result >= 0.0;
        }

        private static string Format(double value)
        {
            return value.ToString("0.########", CultureInfo.InvariantCulture);
        }
    }
}
