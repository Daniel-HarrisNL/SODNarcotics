using System;

namespace Narcotics
{
    // Flavour names for each operation: a street name for the drug, and a case headline built from it. Picked once per
    // operation (Operation.Start) so the drug objects the player frisks, the lab stash, and the case on the board all
    // share one identity - e.g. drug "Velvet" -> case "Velvet on the Street", doses all named "Velvet".
    internal static class NarcNames
    {
        // Evocative, noir-ish invented drug names (not real narcotics). Kept fictional on purpose.
        private static readonly string[] Drugs =
        {
            "Velvet", "Bliss", "Haze", "Nightshade", "Static", "Mirage", "Ember", "Glass", "Riptide", "Lumen",
            "Cobalt", "Ash", "Zephyr", "Halcyon", "Vellum", "Moonrock", "Tangerine", "Slate", "Dusk", "Quartz",
            "Nocturne", "Vapor", "Honey", "Cinder", "Marrow", "Opal", "Pyre", "Frost", "Gossamer", "Sable",
            "Indigo", "Lacquer", "Saffron", "Onyx", "Drift", "Hush", "Amber", "Cardinal", "Velour", "Sugarglass",
        };

        // Case-title templates; {0} = the drug name.
        private static readonly string[] Titles =
        {
            "{0} on the Street",
            "The {0} Ring",
            "Pushing {0}",
            "The {0} Operation",
            "A Taste of {0}",
            "Cut with {0}",
        };

        private static readonly Random _r = new Random();

        internal static string PickDrug()
        {
            try { return Drugs[_r.Next(Drugs.Length)]; }
            catch { return "Mystery"; }
        }

        internal static string CaseTitle(string drug)
        {
            if (string.IsNullOrEmpty(drug)) return "New Drug on the Street";
            try { return string.Format(Titles[_r.Next(Titles.Length)], drug); }
            catch { return drug + " on the Street"; }
        }
    }
}
