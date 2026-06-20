namespace OpenNetLimit.Core;

public static class WildcardMatcher
{
    public static bool IsMatch(string input, string pattern)
    {
        int i = 0, j = 0;
        int starI = -1, starJ = -1;

        while (i < input.Length)
        {
            if (j < pattern.Length && (char.ToLowerInvariant(pattern[j]) == char.ToLowerInvariant(input[i]) || pattern[j] == '?'))
            {
                i++;
                j++;
            }
            else if (j < pattern.Length && pattern[j] == '*')
            {
                starI = i;
                starJ = j++;
            }
            else if (starJ >= 0)
            {
                i = ++starI;
                j = starJ + 1;
            }
            else
            {
                return false;
            }
        }

        while (j < pattern.Length && pattern[j] == '*') j++;
        return j == pattern.Length;
    }
}
