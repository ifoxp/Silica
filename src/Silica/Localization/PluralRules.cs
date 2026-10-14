namespace Silica.Localization;

/// <summary>
/// Plural rules after CLDR. A translation lists its forms separated by '|' in the order given for each rule.
/// </summary>
public enum PluralRule
{
    /// <summary>One form (Chinese, Japanese, Korean, Vietnamese, Thai…).</summary>
    None,
    /// <summary>one | other (English, German, Spanish…).</summary>
    OneOther,
    /// <summary>one (0 and 1) | other (French, Brazilian Portuguese).</summary>
    ZeroOneOther,
    /// <summary>one | few | many (Ukrainian, Belarusian, Serbian, Croatian, Bosnian).</summary>
    EastSlavic,
    /// <summary>one | few | many.</summary>
    Polish,
    /// <summary>one | few | other (Czech, Slovak).</summary>
    Czech,
    /// <summary>one | few | other.</summary>
    Lithuanian,
    /// <summary>zero | one | other.</summary>
    Latvian,
    /// <summary>one | few | other.</summary>
    Romanian,
    /// <summary>one | two | few | other.</summary>
    Slovenian,
    /// <summary>zero | one | two | few | many | other.</summary>
    Arabic,
    /// <summary>one | two | other.</summary>
    Hebrew
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
        "lithuanian" => PluralRule.Lithuanian,
        "latvian" => PluralRule.Latvian,
        "romanian" => PluralRule.Romanian,
        "slovenian" => PluralRule.Slovenian,
        "arabic" => PluralRule.Arabic,
        "hebrew" => PluralRule.Hebrew,
        _ => PluralRule.OneOther
    };

    public static int FormIndex(PluralRule rule, long n)
    {
        n = Math.Abs(n);
        var mod10 = n % 10;
        var mod100 = n % 100;
        var teen = mod100 is >= 11 and <= 19;
        return rule switch
        {
            PluralRule.None => 0,
            PluralRule.OneOther => n == 1 ? 0 : 1,
            PluralRule.ZeroOneOther => n <= 1 ? 0 : 1,
            PluralRule.EastSlavic => mod10 == 1 && mod100 != 11 ? 0
                : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? 1
                : 2,
            PluralRule.Polish => n == 1 ? 0 : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? 1 : 2,
            PluralRule.Czech => n == 1 ? 0 : n is >= 2 and <= 4 ? 1 : 2,
            PluralRule.Lithuanian => mod10 == 1 && !teen ? 0 : mod10 >= 2 && !teen ? 1 : 2,
            PluralRule.Latvian => mod10 == 0 || teen ? 0 : mod10 == 1 && mod100 != 11 ? 1 : 2,
            PluralRule.Romanian => n == 1 ? 0 : n == 0 || mod100 is >= 2 and <= 19 ? 1 : 2,
            PluralRule.Slovenian => mod100 == 1 ? 0 : mod100 == 2 ? 1 : mod100 is 3 or 4 ? 2 : 3,
            PluralRule.Arabic => n == 0 ? 0 : n == 1 ? 1 : n == 2 ? 2 : mod100 is >= 3 and <= 10 ? 3 : mod100 >= 11 ? 4 : 5,
            PluralRule.Hebrew => n == 1 ? 0 : n == 2 ? 1 : 2,
            _ => 0
        };
    }
}
