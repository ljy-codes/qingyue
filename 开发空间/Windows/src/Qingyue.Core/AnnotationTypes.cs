namespace Qingyue.Core;

public enum AnnotationAnchorState
{
    Exact = 0,
    Shifted = 1,
    Uncertain = 2,
    Missing = 3
}

public enum AnnotationColor
{
    Amber = 0,
    Blue = 1,
    Green = 2,
    Rose = 3,
    Violet = 4
}

public sealed record AnnotationDraft(
    Guid DocumentId,
    string? Path,
    int SourceStart,
    int SourceEnd,
    string Quote,
    string ContextBefore,
    string ContextAfter);

public sealed record DocumentAnnotation(
    Guid Id,
    Guid DocumentId,
    string? Path,
    int SourceStart,
    int SourceEnd,
    string Quote,
    string ContextBefore,
    string ContextAfter,
    string? Note,
    IReadOnlyList<string> Tags,
    AnnotationColor Color,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    AnnotationAnchorState AnchorState);

public sealed record AnnotationAnchorRepair(
    Guid Id,
    int SourceStart,
    int SourceEnd,
    AnnotationAnchorState AnchorState);

public sealed record AnnotationSaveResult(DateTimeOffset SavedAt, List<string> Errors);
