using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace VoidLaunch.Services
{
    public static class ThemeService
    {
        private static readonly string[] ColorNames =
        {
            "Background", "Sidebar", "Card", "CardHover",
            "Border", "Text", "Secondary", "Accent", "Error"
        };

        public static IReadOnlyList<string> ColorKeys => ColorNames;

        public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Themes { get; } =
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Void Purple"] = Colors("#09090D", "#0D0D13", "#12121A", "#181821", "#242431", "#F5F5F7", "#858592", "#8B5CF6", "#F87171"),
                ["Midnight Blue"] = Colors("#070B14", "#0A1020", "#10192B", "#17243A", "#243552", "#F2F7FF", "#8294AF", "#3B82F6", "#FB7185"),
                ["Crimson"] = Colors("#0D080A", "#140B0F", "#1C1015", "#28161D", "#3D222C", "#FFF4F6", "#A98991", "#E11D48", "#FB923C"),
                ["Emerald"] = Colors("#07100D", "#0A1712", "#10221A", "#173126", "#25483A", "#F0FFF8", "#83A99A", "#10B981", "#F97316"),
                ["Amber"] = Colors("#0D0B07", "#171208", "#211A0D", "#2E2411", "#49391A", "#FFF9ED", "#AE9C78", "#F59E0B", "#EF4444"),
                ["Cherry Blossom"] = Colors("#100A10", "#160D16", "#211321", "#2D192B", "#4A2942", "#FFF4F8", "#B98C9F", "#F472B6", "#FB7185")
            };

        public static IReadOnlyDictionary<string, string> ActiveColors { get; private set; } = Themes["Void Purple"];

        public static string GetCode(string themeName)
        {
            var colors = Themes.TryGetValue(themeName, out var selected) ? selected : ActiveColors;
            return GetCode(colors);
        }

        public static bool IsBuiltIn(string themeName) => Themes.ContainsKey(themeName);

        public static bool TryNormalizeCode(string code, out string normalizedCode, out string error)
        {
            if (!TryParse(code, null, true, out Dictionary<string, string> colors, out error))
            {
                normalizedCode = string.Empty;
                return false;
            }

            normalizedCode = GetCode(colors);
            return true;
        }

        public static bool TryGetColors(
            string code,
            out IReadOnlyDictionary<string, string> colors,
            out string error)
        {
            bool success = TryParse(code, null, true, out Dictionary<string, string> parsed, out error);
            colors = parsed;
            return success;
        }

        public static double GetContrastRatio(string firstColor, string secondColor)
        {
            Color first = (Color)ColorConverter.ConvertFromString(firstColor);
            Color second = (Color)ColorConverter.ConvertFromString(secondColor);
            return ContrastRatio(first, second);
        }

        public static bool TryApply(ResourceDictionary resources, string code, out string error)
        {
            if (!TryParse(code, ActiveColors, false, out Dictionary<string, string> colors, out error))
                return false;

            ActiveColors = colors;
            ApplyTo(resources);
            error = string.Empty;
            return true;
        }

        private static bool TryParse(
            string code,
            IReadOnlyDictionary<string, string>? startingColors,
            bool requireEveryColor,
            out Dictionary<string, string> colors,
            out string error)
        {
            colors = startingColors is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(startingColors, StringComparer.OrdinalIgnoreCase);

            foreach (string rawLine in (code ?? string.Empty).Split(
                         new[] { '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("# "))
                    continue;

                string[] parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
                string? canonicalName = ColorNames.FirstOrDefault(
                    x => x.Equals(parts[0], StringComparison.OrdinalIgnoreCase));

                if (parts.Length != 2 || canonicalName is null)
                {
                    error = $"Unknown theme line: {rawLine}";
                    return false;
                }

                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(parts[1]);
                    colors[canonicalName] = ToHex(color);
                }
                catch
                {
                    error = $"Invalid color in line: {rawLine}";
                    return false;
                }
            }

            if (requireEveryColor)
            {
                Dictionary<string, string> parsedColors = colors;
                string[] missing = ColorNames.Where(name => !parsedColors.ContainsKey(name)).ToArray();
                if (missing.Length > 0)
                {
                    error = $"Missing theme colors: {string.Join(", ", missing)}";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        public static void ApplyTo(ResourceDictionary resources)
        {
            foreach (var color in ActiveColors)
            {
                string key = color.Key + "Brush";
                if (resources[key] is not SolidColorBrush brush)
                    continue;

                Color value = (Color)ColorConverter.ConvertFromString(color.Value);
                if (!brush.IsFrozen)
                    brush.Color = value;
                else
                    resources[key] = new SolidColorBrush(value);
            }

            if (ActiveColors.TryGetValue("Accent", out string? accentHex) &&
                ActiveColors.TryGetValue("Text", out string? textHex) &&
                ActiveColors.TryGetValue("Background", out string? backgroundHex))
            {
                Color accent = (Color)ColorConverter.ConvertFromString(accentHex);
                Color text = (Color)ColorConverter.ConvertFromString(textHex);
                Color background = (Color)ColorConverter.ConvertFromString(backgroundHex);
                Color readable = ContrastRatio(accent, text) >= ContrastRatio(accent, background)
                    ? text
                    : background;

                const string accentTextKey = "AccentTextBrush";
                if (resources[accentTextKey] is SolidColorBrush accentTextBrush && !accentTextBrush.IsFrozen)
                    accentTextBrush.Color = readable;
                else
                    resources[accentTextKey] = new SolidColorBrush(readable);
            }
        }

        private static string GetCode(IReadOnlyDictionary<string, string> colors)
        {
            var builder = new StringBuilder();

            foreach (string name in ColorNames)
                builder.AppendLine($"{name} = {colors[name]}");

            return builder.ToString().TrimEnd();
        }

        private static string ToHex(Color color) => color.A == byte.MaxValue
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

        private static double ContrastRatio(Color first, Color second)
        {
            double firstLuminance = RelativeLuminance(first);
            double secondLuminance = RelativeLuminance(second);
            double lighter = Math.Max(firstLuminance, secondLuminance);
            double darker = Math.Min(firstLuminance, secondLuminance);
            return (lighter + 0.05) / (darker + 0.05);
        }

        private static double RelativeLuminance(Color color)
        {
            static double Channel(byte value)
            {
                double normalized = value / 255d;
                return normalized <= 0.04045
                    ? normalized / 12.92
                    : Math.Pow((normalized + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Channel(color.R)) +
                   (0.7152 * Channel(color.G)) +
                   (0.0722 * Channel(color.B));
        }

        private static IReadOnlyDictionary<string, string> Colors(
            string background, string sidebar, string card, string cardHover,
            string border, string text, string secondary, string accent, string error)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Background"] = background, ["Sidebar"] = sidebar,
                ["Card"] = card, ["CardHover"] = cardHover,
                ["Border"] = border, ["Text"] = text,
                ["Secondary"] = secondary, ["Accent"] = accent,
                ["Error"] = error
            };
        }
    }
}
