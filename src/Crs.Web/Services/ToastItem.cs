namespace Crs.Web.Services;

public sealed class ToastItem
{
    public required int Id { get; init; }
    public required string Message { get; init; }
    public required ToastVariant Variant { get; init; }
    public bool Leaving { get; set; }
}
