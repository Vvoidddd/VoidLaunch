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

        public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Themes { get; } =
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Void Purple"] = Colors("#09090D", "#0D0D13", "#12121A", "#181821", "#242431", "#F5F5F7", "#858592", "#8B5CF6", "#F87171"),
                ["Midnight Blue"] = Colors("#070B14", "#0A1020", "#10192B", "#17243A", "#243552", "#F2F7FF", "#8294AF", "#3B82F6", "#FB7185"),
                ["Crimson"] = Colors("#0D080A", "#140B0F", "#1C1015", "#28161D", "#3D222C", "#FFF4F6", "#A98991", "#E11D48", "#FB923C"),
                ["Emerald"] = Colors("#07100D", "#0A1712", "#10221A", "#173126", "#25483A", "#F0FFF8", "#83A99A", "#10B981", "#F97316"),
                ["Amber"] = Colors("#0D0B07", "#171208", "#211A0D", "#2E2411", "#49391A", "#FFF9ED", "#AE9C78", "#F59E0B", "#EF4444")
            };

        public static IReadOnlyDictionary<string, string> ActiveColors { get; private set; } = Themes["Void Purple"];

        public static string GetCode(string themeName)
        {
            var colors = Themes.TryGetValue(themeName, out var selected) ? selected : ActiveColors;
            var builder = new StringBuilder();

            foreach (string name in ColorNames)
                builder.AppendLine($"{name} = {colors[name]}");

            return builder.ToString().TrimEnd();
        }

        public static bool TryApply(ResourceDictionary resources, string code, out string error)
        {
            var colors = new Dictionary<string, string>(ActiveColors, StringComparer.OrdinalIgnoreCase);

            foreach (string rawLine in code.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("# "))
                    continue;

                string[] parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
                string? canonicalName = ColorNames.FirstOrDefault(x => x.Equals(parts[0], StringComparison.OrdinalIgnoreCase));

                if (parts.Length != 2 || canonicalName is null)
                {
                    error = $"Unknown theme line: {rawLine}";
                    return false;
                }

                try
                {
                    _ = (Color)ColorConverter.ConvertFromString(parts[1]);
                    colors[canonicalName] = parts[1];
                }
                catch
                {
                    error = $"Invalid color in line: {rawLine}";
                    return false;
                }
            }

            ActiveColors = colors;
            ApplyTo(resources);
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
