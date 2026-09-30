namespace rt4k_pi;

/// <summary>
/// Just enough semantic versioning to order release tags against the running build. Build
/// metadata (after '+') is ignored, and a prerelease sorts below the release it precedes, so a
/// development build is always offered the matching or any newer release.
/// </summary>
public static class SemVer
{
    public static bool TryParse(string? text, out int[] core, out string[] prerelease)
    {
        core = [];
        prerelease = [];
        if (string.IsNullOrWhiteSpace(text)) { return false; }

        string value = text.Trim().TrimStart('v', 'V');
        int plus = value.IndexOf('+');
        if (plus >= 0) { value = value[..plus]; }

        int dash = value.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..].Split('.');
            if (prerelease.Any(string.IsNullOrEmpty)) { return false; }
            value = value[..dash];
        }

        string[] parts = value.Split('.');
        if (parts.Length != 3) { return false; }

        core = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, null, out core[i])) { return false; }
        }

        return true;
    }

    /// <summary>Compares two versions; unparseable versions sort below everything else.</summary>
    public static int Compare(string? a, string? b)
    {
        bool okA = TryParse(a, out int[] coreA, out string[] preA);
        bool okB = TryParse(b, out int[] coreB, out string[] preB);
        if (!okA || !okB) { return okA.CompareTo(okB); }

        for (int i = 0; i < 3; i++)
        {
            int c = coreA[i].CompareTo(coreB[i]);
            if (c != 0) { return c; }
        }

        if (preA.Length == 0 || preB.Length == 0) { return preB.Length.CompareTo(preA.Length); }

        for (int i = 0; i < Math.Min(preA.Length, preB.Length); i++)
        {
            bool numA = int.TryParse(preA[i], out int nA);
            bool numB = int.TryParse(preB[i], out int nB);
            int c = (numA, numB) switch
            {
                (true, true) => nA.CompareTo(nB),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(preA[i], preB[i])
            };
            if (c != 0) { return c; }
        }

        return preA.Length.CompareTo(preB.Length);
    }
}
