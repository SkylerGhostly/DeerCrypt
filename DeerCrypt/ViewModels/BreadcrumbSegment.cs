namespace DeerCrypt.ViewModels
{
    /// <param name="IsFirst">True for the leftmost segment, which gets no leading chevron.</param>
    public sealed record BreadcrumbSegment( string DirectoryId, string Name, bool IsFirst = false );
}