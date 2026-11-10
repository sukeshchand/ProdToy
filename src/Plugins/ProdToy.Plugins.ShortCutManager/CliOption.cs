namespace ProdToy.Plugins.ShortCutManager;

/// <summary>Whether a command-line option takes a value after it.</summary>
enum CliValueKind
{
    /// <summary>A plain switch, e.g. <c>--verbose</c>.</summary>
    None,
    /// <summary><c>--debug [filter]</c>: the value may be left out.</summary>
    Optional,
    /// <summary><c>--model &lt;model&gt;</c>: the value must follow.</summary>
    Required,
}

/// <summary>
/// One command-line option offered by a launch profile: a chip below the args box
/// and/or an item in the options menu. For the plain profiles (npm, dotnet, ...)
/// only <see cref="Token"/> is set and the token is inserted literally.
/// </summary>
sealed record CliOption(
    string Token,
    string Description = "",
    CliValueKind ValueKind = CliValueKind.None,
    string[]? Choices = null)
{
    /// <summary>Other spellings of the same option (<c>-c</c> for <c>--continue</c>).</summary>
    public string[] Aliases { get; init; } = Array.Empty<string>();

    /// <summary>Menu group (Permissions, Session, Model, Tools, Output, Other).</summary>
    public string Group { get; init; } = "Other";

    /// <summary>The value takes several space-separated items (<c>&lt;tools...&gt;</c>).</summary>
    public bool Variadic { get; init; }

    /// <summary>The placeholder from the help text, e.g. <c>&lt;model&gt;</c>.</summary>
    public string ValueHint { get; init; } = "";

    /// <summary>Options that cannot be combined with this one: turning this one on
    /// switches them off.</summary>
    public string[] Excludes { get; init; } = Array.Empty<string>();

    /// <summary>For a chip: the preset value it toggles (<c>--model opus</c>).</summary>
    public string? ChipValue { get; init; }

    public string[] ChoiceList => Choices ?? Array.Empty<string>();

    /// <summary>The text on a chip: the token plus its preset value.</summary>
    public string Label => ChipValue == null ? Token : Token + " " + ChipValue;

    /// <summary>True when <paramref name="name"/> is this option's token or an alias.</summary>
    public bool Matches(string name) =>
        string.Equals(Token, name, StringComparison.Ordinal)
        || Aliases.Any(a => string.Equals(a, name, StringComparison.Ordinal));

    public IEnumerable<string> Names => new[] { Token }.Concat(Aliases);
}
