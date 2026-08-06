using System.Globalization;

namespace UAssetEditor.Core.Editing;

public static class SkipEvaluator
{
    private const double FloatEpsilon = 0.000001;

    /// <summary>Returns false (never skip) if <see cref="SkipCondition.Value"/> isn't itself numeric.</summary>
    public static bool ShouldSkipNumeric(SkipCondition skip, double currentValue)
    {
        if (!double.TryParse(skip.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var skipValue))
            return false;

        return skip.Comparison switch
        {
            SkipComparison.Eq => Math.Abs(currentValue - skipValue) < FloatEpsilon,
            SkipComparison.Lt => currentValue < skipValue,
            SkipComparison.Gt => currentValue > skipValue,
            SkipComparison.Lte => currentValue <= skipValue,
            SkipComparison.Gte => currentValue >= skipValue,
            _ => false,
        };
    }

    /// <summary>Non-numeric properties only support equality; any other comparison never skips.</summary>
    public static bool ShouldSkipText(SkipCondition skip, string currentValue) =>
        skip.Comparison == SkipComparison.Eq &&
        string.Equals(currentValue, skip.Value, StringComparison.OrdinalIgnoreCase);
}
