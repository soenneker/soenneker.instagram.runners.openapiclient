using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Soenneker.OpenApi.Converters.Meta.Models;
using Soenneker.OpenApi.Converters.Meta;
using Soenneker.Instagram.Runners.OpenApiClient.Profiles;

namespace Soenneker.Instagram.Runners.OpenApiClient.Tests;

public sealed class FullApiProfileTests
{
    [Test]
    public void InstagramIncludesAllInstagramNodesAndRelatedOperations()
    {
        var doc = Convert().Document;
        Check(doc["paths"]!["/{node-id}/insights"]?["get"] is not null, "Insights retained");
        Check(doc["paths"]!["/{node-id}/new_endpoint"]?["get"] is not null, "New Instagram nodes and endpoints included automatically");
        Check(doc["paths"]!["/{node-id}/owned_instagram_accounts"]?["get"] is not null, "Cross-platform Instagram edge retained");
        Check(doc["paths"]!["/{node-id}/campaigns"] is null, "Unrelated ad operation excluded from Instagram");
        Check(doc["x-meta-source"]!["includedOperationCount"]!.GetValue<int>() == 4, "Complete Instagram source operation coverage");
    }

    private static MetaOpenApiConversionResult Convert()
    {
        var converter = new MetaOpenApiConverter();
        var options = new MetaOpenApiConverterOptions { GraphApiVersion = "v26.0" };
        return InstagramSpecificationProfile.Convert(converter, Specifications(), options, "test");
    }
    private static IEnumerable<JsonObject> Operations(JsonObject doc) => doc["paths"]!.AsObject().SelectMany(path => path.Value!.AsObject().Select(op => op.Value!.AsObject()));
    private static Dictionary<string, string> Specifications() => new()
    {
        ["Page"] = """{"fields":[{"name":"id","type":"string"},{"name":"unusual_field","type":"string"}],"apis":[{"method":"GET","return":"Page","params":[]},{"method":"POST","endpoint":"feed","params":[{"name":"message","type":"string"},{"name":"thumbnail","type":"file"}]}]}""",
        ["User"] = """{"fields":[],"apis":[{"method":"POST","endpoint":"feed","params":[{"name":"link","type":"string"}]}]}""",
        ["AdAccount"] = """{"fields":[],"apis":[{"method":"POST","endpoint":"campaigns","params":[]}]}""",
        ["IGUser"] = """{"fields":[],"apis":[{"method":"GET","endpoint":"insights","return":"IGUser","params":[]}]}""",
        ["IGFutureResource"] = """{"fields":[],"apis":[{"method":"GET","endpoint":"new_endpoint","return":"IGFutureResource","params":[]}]}""",
        ["Business"] = """{"fields":[],"apis":[{"method":"GET","endpoint":"owned_instagram_accounts","return":"IGUser","params":[]}]}"""
    };
    private static void Validate(MetaOpenApiConversionResult result)
    {
        var parsed = OpenApiDocument.Parse(result.ToJson(), "json");
        Check(parsed.Document is not null && parsed.Diagnostic?.Errors.Count == 0, "Valid OpenAPI document");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}