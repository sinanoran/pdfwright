namespace Pdfwright;

/// <summary>The checks the public types make as a value is set, so that a bad value fails where it is given and names the property it was given to.</summary>
internal static class Check
{
    /// <summary>A value of an enumeration that the enumeration defines.</summary>
    internal static T Defined<T>(T value, string name)
        where T : struct, Enum =>
        Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(name, value, $"{value} is not a value of {typeof(T).Name}.");

    /// <summary>An optional enumeration value that, when there is one, the enumeration defines.</summary>
    internal static T? Defined<T>(T? value, string name)
        where T : struct, Enum
    {
        if (value is { } some)
        {
            _ = Defined(some, name);
        }

        return value;
    }

    /// <summary>An optional space: finite and zero or more.</summary>
    internal static Length? Space(Length? value, string name)
    {
        if (value is { } length && (!length.IsFinite || length < Length.Zero))
        {
            throw new ArgumentOutOfRangeException(name, length, "A space is zero or more and finite.");
        }

        return value;
    }

    /// <summary>An optional font size: finite and more than zero.</summary>
    internal static Length? Size(Length? value, string name)
    {
        if (value is { } length && (!length.IsFinite || !(length > Length.Zero)))
        {
            throw new ArgumentOutOfRangeException(name, length, "A font size is a positive, finite length.");
        }

        return value;
    }

    /// <summary>A line spacing: finite and more than zero.</summary>
    internal static double LineSpacing(double value, string name) =>
        value > 0 && double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, value, "The line spacing is a positive, finite number.");
}
