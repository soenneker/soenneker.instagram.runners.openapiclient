using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Soenneker.OpenApi.Converters.Meta.Models;
using Soenneker.OpenApi.Converters.Meta;
using Soenneker.Instagram.Runners.OpenApiClient.Profiles;

namespace Soenneker.Instagram.Runners.OpenApiClient.Tests;

public sealed class PublishingProfileTests
{
    [Test]
    public void InstagramProfileCorrectsIdsAndAddsContainerStatus()
    {
        var result = Convert(Instagram(), Options());
        var doc = result.Document;
        var properties = doc["paths"]!["/{node-id}/media_publish"]!["post"]!["requestBody"]!["content"]!["application/x-www-form-urlencoded"]!["schema"]!["properties"]!;
        Check(properties["creation_id"]!["type"]!.GetValue<string>() == "string", "Large container IDs remain strings");
        Check(doc["components"]!["schemas"]!["PublishingNode"]!["properties"]!["status_code"] is not null, "Container status available");
        Check(doc["paths"]!["/{node-id}/ads"] is null, "Non-publishing API excluded");
        Check(doc["paths"]!["/{node-id}/media_publish"]!["post"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>() == "#/components/schemas/PublishingResult", "Publishing result applied");
        Validate(result);
    }

    private static MetaOpenApiConverterOptions Options() => new() { GraphApiVersion = "v26.0" };

    private static MetaOpenApiConversionResult Convert(IReadOnlyDictionary<string, string> specifications,
        MetaOpenApiConverterOptions options)
        => InstagramSpecificationProfile.Convert(new MetaOpenApiConverter(), specifications, options, "test-revision", true);

    private static Dictionary<string, string> Facebook() => new()
    {
        ["Page.json"] = Node("Page", "GET", "GET feed", "POST feed", "GET photos", "POST photos", "GET posts", "GET published_posts", "GET scheduled_posts", "GET ads"),
        ["PagePost"] = Node("PagePost", "GET", "POST", "DELETE"),
        ["Photo"] = Node("Photo", "GET", "DELETE"),
        ["User"] = Node("User", "GET accounts"),
        ["Unrelated"] = Node("Unrelated", "GET ads")
    };

    private static Dictionary<string, string> Instagram() => new()
    {
        ["IGUser.json"] = Node("IGUser", "GET", "GET media", "POST media", "POST media_publish", "GET stories", "GET content_publishing_limit", "GET ads"),
        ["IGMedia"] = Node("IGMedia", "GET", "GET children", "GET comments", "POST comments", "POST", "DELETE"),
        ["IGComment"] = Node("IGComment", "GET", "DELETE")
    };

    private static string Node(string name, params string[] operations)
    {
        var apis = new JsonArray();
        foreach (string operation in operations)
        {
            string[] parts = operation.Split(' ', 2);
            var parameters = new JsonArray();
            if (operation == "POST feed") parameters = JsonNode.Parse("""[{"name":"message","type":"string"},{"name":"attached_media","type":"list<Object>"},{"name":"scheduled_publish_time","type":"datetime"},{"name":"thumbnail","type":"file"}]""")!.AsArray();
            if (operation == "POST media_publish") parameters = JsonNode.Parse("""[{"name":"creation_id","type":"unsigned int","required":true}]""")!.AsArray();
            var api = new JsonObject { ["method"] = parts[0], ["return"] = name, ["params"] = parameters };
            if (parts.Length == 2) api["endpoint"] = parts[1];
            apis.Add(api);
        }
        return new JsonObject { ["fields"] = JsonNode.Parse("""[{"name":"id","type":"string"},{"name":"name","type":"string"}]"""), ["apis"] = apis }.ToJsonString();
    }

    private static void Validate(MetaOpenApiConversionResult result)
    {
        var parsed = OpenApiDocument.Parse(result.ToJson(), "json");
        Check(parsed.Document is not null && parsed.Diagnostic?.Errors.Count == 0, "Valid OpenAPI document");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}