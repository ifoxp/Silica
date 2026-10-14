namespace Silica.Localization;

public enum PluralRule
{
    /// <summary>No plural forms (Chinese, Japanese, Korean…).</summary>
    None,
    /// <summary>"one" for 1, "other" otherwise (English, German, Spanish…).</summary>
    OneOther,
    /// <summary>"one" for 0 and 1 (French, Portuguese-BR).</summary>
    ZeroOneOther,
    /// <summary>one / few / many (Ukrainian, Russian, Belarusian, Serbian, Croatian).</summary>
    EastSlavic,
    /// <summary>one / few / many (Polish).</summary>
    Polish,
    /// <summary>one / few / other (Czech, Slovak).</summary>
    Czech
}

public static class PluralRules
{
    public static PluralRule Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "none" => PluralRule.None,
        "zero-one-other" or "french" => PluralRule.ZeroOneOther,
        "east-slavic" or "slavic" => PluralRule.EastSlavic,
        "polish" => PluralRule.Polish,
        "czech" => PluralRule.Czech,
        _ => PluralRule.OneOther
    };

    /// <summary>Index of the form to use from a "form0|form1|form2" translation.</summary>
    public static int FormIndex(PluralRule rule, long n)
    {
        n = Math.Abs(n);
        var mod10 = n % 10;
        var mod100 = n % 100;
        return rule switch
        {
            PluralRule.None => 0,
            PluralRule.OneOther => n == 1 ? 0 : 1,
            PluralRule.ZeroOneOther => n <= 1 ? 0 : 1,
            PluralRule.EastSlavic => mod10 == 1 && mod100 != 11 ? 0
                : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? 1
                : 2,
            PluralRule.Polish => n == 1 ? 0
                : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? 1
                : 2,
            PluralRule.Czech => n == 1 ? 0 : n is >= 2 and <= 4 ? 1 : 2,
            _ => 0
        };
    }
}
