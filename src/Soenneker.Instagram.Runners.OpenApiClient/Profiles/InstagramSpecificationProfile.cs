using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Soenneker.OpenApi.Converters.Meta.Abstract;
using Soenneker.OpenApi.Converters.Meta.Models;

namespace Soenneker.Instagram.Runners.OpenApiClient.Profiles;

internal static class InstagramSpecificationProfile
{
    internal static MetaOpenApiConversionResult Convert(IMetaOpenApiConverter converter,
        IReadOnlyDictionary<string, string> input, MetaOpenApiConverterOptions options, string sourceRevision,
        bool publishing = false, CancellationToken token = default)
        => publishing ? ConvertPublishing(converter, input, options, sourceRevision, token)
            : ConvertFull(converter, input, options, sourceRevision, token);

    private static MetaOpenApiConversionResult ConvertFull(IMetaOpenApiConverter converter,
        IReadOnlyDictionary<string, string> input, MetaOpenApiConverterOptions options, string sourceRevision, CancellationToken token)
    {
        var specifications = new SortedDictionary<string, string>(StringComparer.Ordinal);
        int sourceOperations = 0;
        var included = new JsonArray();
        foreach ((string key, string json) in input.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(key);
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
            JsonNode root = JsonNode.Parse(json)!;
            if (root is JsonObject obj && obj["apis"] is JsonArray apis)
            {
                sourceOperations += apis.Count;
                for (int i = apis.Count - 1; i >= 0; i--)
                {
                    JsonObject api = apis[i]!.AsObject();
                    if (true && !IsInstagramNode(name) && !IsInstagramOperation(name, api)) apis.RemoveAt(i);
                }
                foreach (JsonObject api in apis.OfType<JsonObject>())
                {
                    // SDK root return aliases can name missing classes; the node defines the complete root fields.
                    if (api["method"]?.GetValue<string>() == "GET" && string.IsNullOrEmpty(api["endpoint"]?.GetValue<string>()) && api["basePath"] is null)
                        api["return"] = name;
                    if (api["params"] is JsonArray parameters)
                        foreach (JsonObject parameter in parameters.OfType<JsonObject>())
                        {
                            if (parameter["name"]?.GetValue<string>() == "creation_id") parameter["type"] = "string";
                            if (name == "Page" && parameter["name"]?.GetValue<string>() == "scheduled_publish_time") parameter["type"] = "unsigned int";
                        }
                }
                foreach (JsonNode? api in apis)
                    included.Add((System.Text.Json.Nodes.JsonNode?)new JsonObject { ["node"] = name, ["method"] = api!["method"]!.DeepClone(), ["endpoint"] = api["endpoint"]?.DeepClone() });
            }
            if (!specifications.TryAdd(name, root.ToJsonString())) throw new FormatException($"Duplicate specification '{name}'.");
        }
        int supplementalOperations = specifications.ContainsKey("IGContainer") ? 0 : 1;
        // Container polling is documented but absent from the SDK node corpus.
        if (!specifications.ContainsKey("IGContainer"))
            specifications["IGContainer"] = """{"fields":[{"name":"id","type":"string"},{"name":"status_code","type":"string"},{"name":"status","type":"string"}],"apis":[{"method":"GET","return":"IGContainer","params":[]}]}""";
        var effective = new MetaOpenApiConverterOptions
        {
            GraphApiVersion = options.GraphApiVersion, Title = options.Title, ServerUrl = options.ServerUrl,
            ThrowOnUnknownTypes = options.ThrowOnUnknownTypes, ResponseSchemaOverrides = options.ResponseSchemaOverrides,
            NormalizeForKiota = true, PruneUnusedSchemas = true
        };
        if (options.NodeTypes is not null) throw new ArgumentException("Full API profiles cannot restrict NodeTypes; use the converter directly for custom subsets.", nameof(options));
        MetaOpenApiConversionResult result = converter.Convert(specifications, effective);
        var paths = result.Document["paths"]!.AsObject();
        int emittedOperations = paths.SelectMany(path => path.Value!.AsObject())
            .Sum(operation => operation.Value?["x-meta-operations"]?.AsArray().Count ?? 0);
        if (emittedOperations != included.Count + supplementalOperations)
            throw new InvalidOperationException($"Coverage mismatch: expected {included.Count + supplementalOperations} source operations, emitted {emittedOperations}.");
        result.Document["x-meta-source"] = new JsonObject
        {
            ["repository"] = "https://github.com/facebook/facebook-business-sdk-codegen", ["revision"] = sourceRevision,
            ["profile"] = "Instagram", ["sourceOperationCount"] = sourceOperations,
            ["includedOperationCount"] = included.Count, ["supplementalOperationCount"] = supplementalOperations, ["operations"] = included
        };
        return result;
    }

    private static MetaOpenApiConversionResult ConvertPublishing(IMetaOpenApiConverter converter,
        IReadOnlyDictionary<string, string> input, MetaOpenApiConverterOptions options, string sourceRevision, CancellationToken token)
    {
        string[] nodes = InstagramPublishingProfile.Nodes;
        HashSet<string> operations = InstagramPublishingProfile.Operations;
        var specifications = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string json) in input)
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(key);
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
            JsonNode root;
            try { root = JsonNode.Parse(json) ?? throw new JsonException("Null specification."); }
            catch (JsonException exception) { throw new FormatException($"Invalid Meta JSON in '{key}'.", exception); }
            if (root is JsonObject obj && obj["apis"] is JsonArray apis)
                for (int i = apis.Count - 1; i >= 0; i--)
                    if (!operations.Contains(Operation(name, apis[i]!))) apis.RemoveAt(i);
            if (!specifications.TryAdd(name, root.ToJsonString())) throw new FormatException($"Duplicate specification '{name}'.");
        }
        foreach (string name in nodes)
            if (name != "IGContainer" && !specifications.ContainsKey(name))
                throw new ArgumentException($"Publishing profile requires the '{name}' specification.", nameof(input));
        InstagramPublishingProfile.AdjustSpecifications(specifications);

        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, string json) in specifications)
        {
            token.ThrowIfCancellationRequested();
            if (JsonNode.Parse(json) is not JsonObject obj || obj["apis"] is not JsonArray apis) continue;
            foreach (JsonNode? api in apis) available.Add(Operation(name, api!));
        }
        string[] missing = operations.Except(available).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"Meta specifications are missing required publishing operations: {string.Join(", ", missing)}");

        var responses = new Dictionary<string, JsonObject>(InstagramPublishingProfile.Responses, StringComparer.Ordinal);
        foreach (var response in options.ResponseSchemaOverrides) responses[response.Key] = response.Value;
        var effective = new MetaOpenApiConverterOptions
        {
            GraphApiVersion = options.GraphApiVersion,
            Title = options.Title,
            ServerUrl = options.ServerUrl,
            NodeTypes = options.NodeTypes ?? nodes,
            ThrowOnUnknownTypes = options.ThrowOnUnknownTypes,
            ResponseSchemaOverrides = responses,
            NormalizeForKiota = true, PruneUnusedSchemas = true
        };
        MetaOpenApiConversionResult result = converter.Convert(specifications, effective);
        result.Document["x-meta-source"] = new JsonObject
        {
            ["repository"] = "https://github.com/facebook/facebook-business-sdk-codegen",
            ["revision"] = sourceRevision,
            ["profile"] = "Instagram publishing"
        };
        return result;
    }

    private static string Operation(string name, JsonNode api) => $"{name} {api["method"]!.GetValue<string>()} {api["endpoint"]?.GetValue<string>()}".TrimEnd();

    internal static bool IsInstagramNode(string name) => name.Contains("Instagram", StringComparison.OrdinalIgnoreCase)
        || name.Contains("IG", StringComparison.Ordinal) || name is "UnifiedThread" or "UnifiedMessage";

    private static bool IsInstagramOperation(string name, JsonObject api)
    {
        string endpoint = api["endpoint"]?.GetValue<string>() ?? "";
        return endpoint.Contains("instagram", StringComparison.OrdinalIgnoreCase)
            || endpoint.StartsWith("ig_", StringComparison.OrdinalIgnoreCase)
            || IsInstagramNode(api["return"]?.GetValue<string>() ?? "")
            || (api["params"]?.ToJsonString().Contains("instagram", StringComparison.OrdinalIgnoreCase) ?? false)
            || (name is "Page" or "User" && (endpoint.Length == 0 || endpoint is "accounts" or "conversations" or "messages" or "subscribed_apps" or "messenger_profile"));
    }
}
