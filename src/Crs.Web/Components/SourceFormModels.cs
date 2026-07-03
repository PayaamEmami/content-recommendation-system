using System.ComponentModel.DataAnnotations;
using Crs.Core.Enums;

namespace Crs.Web.Components;

/// <summary>Form model for adding a new source.</summary>
public sealed class AddSourceModel
{
    [Required(ErrorMessage = "Name is required")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "URL is required")]
    [Url(ErrorMessage = "Enter a valid URL")]
    public string Url { get; set; } = string.Empty;

    [Required(ErrorMessage = "Category is required")]
    public ContentType Category { get; set; }

    public string? Description { get; set; }
}

/// <summary>Form model for editing an existing source inline.</summary>
public sealed class EditSourceModel
{
    [Required] public string Name { get; set; } = string.Empty;
    [Required][Url] public string Url { get; set; } = string.Empty;
    [Required] public ContentType Category { get; set; }
    public string? Description { get; set; }
}
