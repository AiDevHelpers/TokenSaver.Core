using System.Text.RegularExpressions;

namespace TokenSaver.Utilities.Extensions;

public static class StringExtensions
{
    public static string ReplaceRegex(this string input, string pattern, string replacement)
    {
        return Regex.Replace(input, pattern, replacement);
    }
}
