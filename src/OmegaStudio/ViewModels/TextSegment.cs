namespace OmegaStudio.ViewModels;

/// <summary>
/// A run of an Outliner row's label, flagged if it matched the search box. Rows are
/// drawn as a sequence of these so a match can be highlighted in place.
/// </summary>
public sealed record TextSegment(string Text, bool IsHighlight);
