// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetStore.Core.Serialization;

/// <summary>Shared JSON settings for reading/writing asset store documents.</summary>
public static class AssetStoreJson
{
    /// <summary>Lenient reader: camelCase, comments and trailing commas tolerated.</summary>
    public static JsonSerializerOptions Read { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Canonical writer: camelCase, indented, nulls omitted.</summary>
    public static JsonSerializerOptions Write { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Deserialize <paramref name="json"/> into <typeparamref name="T"/>.</summary>
    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Read)
        ?? throw new JsonException($"Document deserialized to null for type {typeof(T).Name}.");

    /// <summary>Serialize <paramref name="value"/> using the canonical writer.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Write);
}
