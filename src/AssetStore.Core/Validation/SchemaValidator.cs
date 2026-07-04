// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace AssetStore.Core.Validation;

/// <summary>Validates JSON documents against a JSON Schema (draft 2020-12).</summary>
public sealed class SchemaValidator
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // JsonSchema.FromText registers each schema by its $id in a process-global registry and refuses
    // to re-register. Cache by absolute path so a schema is built (and registered) at most once, and
    // serialize builds so concurrent first-time loads of distinct schemas don't race the registry.
    private static readonly ConcurrentDictionary<string, Lazy<SchemaValidator>> Cache = new();
    private static readonly object BuildGate = new();

    private readonly JsonSchema _schema;
    private readonly string _name;

    private SchemaValidator(JsonSchema schema, string name)
    {
        _schema = schema;
        _name = name;
    }

    /// <summary>Loads a schema from a <c>.json</c> file (cached per process).</summary>
    public static SchemaValidator FromFile(string schemaPath) =>
        Cache.GetOrAdd(Path.GetFullPath(schemaPath), key => new Lazy<SchemaValidator>(() => Build(key))).Value;

    private static SchemaValidator Build(string fullPath)
    {
        lock (BuildGate)
        {
            return new SchemaValidator(JsonSchema.FromText(File.ReadAllText(fullPath)), Path.GetFileName(fullPath));
        }
    }

    /// <summary>Validates raw JSON text, appending any violation as an error to <paramref name="report"/>.</summary>
    /// <returns>The parsed node when the document is structurally valid JSON, otherwise null.</returns>
    public JsonNode? Validate(string json, ValidationReport report, string codePrefix)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: DocumentOptions);
        }
        catch (JsonException ex)
        {
            report.Error($"{codePrefix}.json", $"Invalid JSON in {_name}: {ex.Message}");
            return null;
        }

        var element = JsonSerializer.SerializeToElement(node);
        var results = _schema.Evaluate(element, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true, // enforce "format": uri / date / date-time instead of annotating only
        });
        if (results.IsValid)
        {
            return node;
        }

        foreach (var detail in results.Details ?? [])
        {
            if (detail is { IsValid: false, Errors: { Count: > 0 } errors })
            {
                foreach (var (keyword, message) in errors)
                {
                    var location = detail.InstanceLocation.ToString();
                    var where = string.IsNullOrEmpty(location) ? "(root)" : location;
                    report.Error($"{codePrefix}.schema", $"{_name} {where}: {message} ({keyword})");
                }
            }
        }

        // Fallback in case the evaluator reported invalid without granular details.
        if (!report.HasErrors)
        {
            report.Error($"{codePrefix}.schema", $"{_name} failed schema validation.");
        }

        return node;
    }
}
