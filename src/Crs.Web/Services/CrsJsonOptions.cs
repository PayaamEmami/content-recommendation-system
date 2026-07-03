using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crs.Web.Services;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> used by the Web service layer so every
/// API call serializes/deserializes with the same casing and enum conventions.
/// </summary>
public static class CrsJsonOptions
{
    /// <summary>Camel-cased, case-insensitive options with string enum support.</summary>
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Same as <see cref="Default"/> but pretty-printed, for file exports.</summary>
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
}
