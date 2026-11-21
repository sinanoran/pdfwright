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

    /// <summary>Padding: every side finite and zero or more.</summary>
    internal static Margins Padding(Margins value, string name) =>
        value.Top.IsFinite && value.Right.IsFinite && value.Bottom.IsFinite && value.Left.IsFinite && value.Top >= Length.Zero && value.Right >= Length.Zero && value.Bottom >= Length.Zero && value.Left >= Length.Zero
            ? value
            : throw new ArgumentOutOfRangeException(name, value, "Padding is finite and not negative.");

    /// <summary>Optional padding that, when there is some, is finite and not negative.</summary>
    internal static Margins? Padding(Margins? value, string name)
    {
        if (value is { } some)
        {
            _ = Padding(some, name);
        }

        return value;
    }

    /// <summary>An optional border line; a default one (width zero) is refused, and one made with its constructor is always valid.</summary>
    internal static BorderLine? Line(BorderLine? value, string name) =>
        value is { } line && !(line.Width > Length.Zero) ? throw new ArgumentOutOfRangeException(name, line.Width, "A border line's width is finite and greater than zero.") : value;

    /// <summary>A line spacing: finite and more than zero.</summary>
    internal static double LineSpacing(double value, string name) =>
        value > 0 && double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, value, "The line spacing is a positive, finite number.");
}
