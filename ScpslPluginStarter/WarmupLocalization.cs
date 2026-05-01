using System;

namespace ScpslPluginStarter;

internal static class WarmupLocalization
{
    public static string Language { get; private set; } = "en";

    public static bool IsChinese =>
        Language.Equals("cn", StringComparison.OrdinalIgnoreCase)
        || Language.Equals("zh", StringComparison.OrdinalIgnoreCase)
        || Language.Equals("zh-cn", StringComparison.OrdinalIgnoreCase)
        || Language.Equals("zh_cn", StringComparison.OrdinalIgnoreCase);

    public static void SetLanguage(string? language)
    {
        string trimmed = language?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            Language = "en";
            return;
        }

        Language = trimmed;
    }

    public static string T(string english, string chinese)
    {
        return IsChinese ? chinese : english;
    }

    public static bool TryNormalizeLanguage(string rawValue, out string language)
    {
        string value = rawValue.Trim().ToLowerInvariant();
        switch (value)
        {
            case "en":
            case "eng":
            case "english":
                language = "en";
                return true;
            case "cn":
            case "zh":
            case "zh-cn":
            case "zh_cn":
            case "chinese":
            case "中文":
                language = "cn";
                return true;
            default:
                language = "";
                return false;
        }
    }
}
