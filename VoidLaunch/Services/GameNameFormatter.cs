using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace VoidLaunch.Services
{
    public static partial class GameNameFormatter
    {
        private static readonly Dictionary<string, string> KnownNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["fivenightsatfreddys"] = "Five Nights at Freddy's",
                ["fivenightsatfreddys2"] = "Five Nights at Freddy's 2",
                ["fivenightsatfreddys3"] = "Five Nights at Freddy's 3",
                ["fivenightsatfreddys4"] = "Five Nights at Freddy's 4",
                ["fivenightsatfreddyssecuritybreach"] = "Five Nights at Freddy's: Security Breach",
                ["fnaf"] = "Five Nights at Freddy's",
                ["fnaf2"] = "Five Nights at Freddy's 2",
                ["fnaf3"] = "Five Nights at Freddy's 3",
                ["fnaf4"] = "Five Nights at Freddy's 4",

                ["halfsword"] = "Half Sword",
                ["cyberpunk2077"] = "Cyberpunk 2077",
                ["reddeadredemption2"] = "Red Dead Redemption 2",
                ["grandtheftauto5"] = "Grand Theft Auto V",
                ["grandtheftauto"] = "Grand Theft Auto",
                ["gta5"] = "Grand Theft Auto V",
                ["gta5enhanced"] = "Grand Theft Auto V",
                ["gta"] = "Grand Theft Auto",

                ["minecraft"] = "Minecraft",
                ["minecraftlauncher"] = "Minecraft",

                ["amongus"] = "Among Us",
                ["phasmophobia"] = "Phasmophobia",
                ["lethalcompany"] = "Lethal Company",
                ["contentwarning"] = "Content Warning",
                ["contentwarninggame"] = "Content Warning",

                ["baldursgate3"] = "Baldur's Gate 3",
                ["eldenring"] = "Elden Ring",
                ["terraria"] = "Terraria",
                ["stardewvalley"] = "Stardew Valley",
                ["ts4"] = "The Sims 4",
                ["ts4x64"] = "The Sims 4",
                ["ts4dx9x64"] = "The Sims 4",
                ["ts4x64fpb"] = "The Sims 4",
                ["dasims4"] = "The Sims 4",
                ["dasims4steam"] = "The Sims 4",
                ["sims4steam"] = "The Sims 4",
                ["left4dead2"] = "Left 4 Dead 2",
                ["portal2"] = "Portal 2",
                ["teamfortress2"] = "Team Fortress 2"
            };

        private static readonly HashSet<string> GenericDirectoryNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Binaries",
                "Win64",
                "Win32",
                "Windows",
                "Game",
                "Games",
                "Build",
                "Builds",
                "Release",
                "Debug",
                "Shipping",
                "Development",
                "Client"
            };

        public static string FromExecutable(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                return "Unknown Game";

            string name =
                Path.GetFileNameWithoutExtension(executablePath);

            return Format(name);
        }

        public static string FromGameDirectory(
            string directory,
            string executablePath)
        {
            string directoryName =
                Path.GetFileName(
                    directory.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar));

            string executableName =
                Path.GetFileNameWithoutExtension(
                    executablePath);

            // Prefer known executable names first.
            string executableFormatted =
                Format(executableName);

            string normalizedExecutable =
                NormalizeForLookup(executableName);

            if (KnownNames.TryGetValue(
                    normalizedExecutable,
                    out string? knownExecutable))
            {
                return knownExecutable;
            }

            // Then try the actual game folder.
            if (!string.IsNullOrWhiteSpace(directoryName) &&
                !GenericDirectoryNames.Contains(directoryName))
            {
                string directoryFormatted =
                    Format(RemoveLinks(directoryName));

                if (!string.IsNullOrWhiteSpace(directoryFormatted) &&
                    !string.Equals(
                        directoryFormatted,
                        "Unknown Game",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return directoryFormatted;
                }
            }

            return executableFormatted;
        }

        public static string CleanDisplayName(
            string value,
            string executablePath)
        {
            string cleaned = Format(RemoveLinks(value));

            if (string.Equals(cleaned, "Unknown Game", StringComparison.OrdinalIgnoreCase) ||
                LooksLikeLink(cleaned))
            {
                return FromExecutable(executablePath);
            }

            return cleaned;
        }

        private static string Format(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Unknown Game";

            string original =
                value.Trim();

            string lookup =
                NormalizeForLookup(original);

            if (KnownNames.TryGetValue(
                    lookup,
                    out string? known))
            {
                return known;
            }

            string name = original;

            // Remove common Unreal / Windows build suffixes.
            name = Regex.Replace(
                name,
                @"(?:[-_ ]?(?:Win64|Win32|Windows|Linux|Mac|Shipping|Development|Debug|Release|Test|Game|Client))+?$",
                string.Empty,
                RegexOptions.IgnoreCase);

            // Remove UE version markers.
            name = Regex.Replace(
                name,
                @"UE\d+(?:\.\d+)*",
                string.Empty,
                RegexOptions.IgnoreCase);

            // Replace separators.
            name = name.Replace("_", " ");
            name = name.Replace("-", " ");

            // Insert spaces in PascalCase/camelCase.
            name = PascalCaseRegex().Replace(
                name,
                "$1 $2");

            // Handle things like "FiveNightsatFreddys".
            name = LowerUpperRegex().Replace(
                name,
                "$1 $2");

            // Cyberpunk2077.
            name = LetterNumberRegex().Replace(
                name,
                "$1 $2");

            // 2077Cyberpunk.
            name = NumberLetterRegex().Replace(
                name,
                "$1 $2");

            // Clean whitespace.
            name = WhitespaceRegex().Replace(
                name,
                " ");

            name = name.Trim();

            if (string.IsNullOrWhiteSpace(name))
                return "Unknown Game";

            // Some common corrections after generic formatting.
            string normalized =
                NormalizeForLookup(name);

            if (KnownNames.TryGetValue(
                    normalized,
                    out string? corrected))
            {
                return corrected;
            }

            return name;
        }

        private static string RemoveLinks(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string cleaned = Regex.Replace(
                value,
                @"(?:https?://|www\.)\S+",
                " ",
                RegexOptions.IgnoreCase);

            cleaned = Regex.Replace(
                cleaned,
                @"\b(?:[a-z0-9-]+\.)+(?:com|net|org|io|gg|co|to|site|xyz|ru|me|info)\b",
                " ",
                RegexOptions.IgnoreCase);

            cleaned = Regex.Replace(
                cleaned,
                @"[\[\(][^\]\)]*(?:\.com|\.net|\.org|www\.|http)[^\]\)]*[\]\)]",
                " ",
                RegexOptions.IgnoreCase);

            cleaned = Regex.Replace(
                cleaned,
                @"(?:[-_. ]*(?:SteamRIP|FitGirl|DODI|ElAmigos|GOG)(?:\.com)?)",
                " ",
                RegexOptions.IgnoreCase);

            return WhitespaceRegex()
                .Replace(cleaned, " ")
                .Trim(' ', '-', '_', '.', '[', ']', '(', ')');
        }

        private static bool LooksLikeLink(string value)
        {
            return Regex.IsMatch(
                value,
                @"https?://|www\.|\.(?:com|net|org|io|gg|co|to|site|xyz|ru|me|info)\b",
                RegexOptions.IgnoreCase);
        }

        private static string NormalizeForLookup(
            string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return Regex.Replace(
                value.ToLowerInvariant(),
                @"[^a-z0-9]",
                string.Empty);
        }

        [GeneratedRegex(
            @"([a-z])([A-Z])",
            RegexOptions.Compiled)]
        private static partial Regex PascalCaseRegex();

        [GeneratedRegex(
            @"([A-Za-z])([0-9])",
            RegexOptions.Compiled)]
        private static partial Regex LetterNumberRegex();

        [GeneratedRegex(
            @"([0-9])([A-Za-z])",
            RegexOptions.Compiled)]
        private static partial Regex NumberLetterRegex();

        [GeneratedRegex(
            @"([a-z])([A-Z])",
            RegexOptions.Compiled)]
        private static partial Regex LowerUpperRegex();

        [GeneratedRegex(
            @"\s+",
            RegexOptions.Compiled)]
        private static partial Regex WhitespaceRegex();
    }
}
