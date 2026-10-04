using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Soenneker.Instagram.Runners.OpenApiClient.Profiles;

internal static class InstagramPublishingProfile
{
    internal static readonly string[] Nodes = ["IGUser", "IGMedia", "IGComment", "IGContainer"];
    internal static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
    {
        "IGUser GET",
        "IGUser GET media",
        "IGUser POST media",
        "IGUser POST media_publish",
        "IGUser GET stories",
        "IGUser GET content_publishing_limit",
        "IGMedia GET",
        "IGMedia GET children",
        "IGMedia GET comments",
        "IGMedia POST comments",
        "IGMedia POST",
        "IGMedia DELETE",
        "IGComment GET",
        "IGComment DELETE",
        "IGContainer GET"
    };
    private static readonly Dictionary<string, string[]> Fields = new(StringComparer.Ordinal)
    {
        ["IGUser"] = ["id", "username", "biography", "media_count", "followers_count", "follows_count", "website", "profile_picture_url"],
        ["IGMedia"] = ["id", "caption", "media_type", "media_product_type", "media_url", "permalink", "thumbnail_url", "timestamp", "username", "owner", "comments_count", "like_count", "is_comment_enabled"],
        ["IGComment"] = ["id", "text", "timestamp", "username", "like_count", "hidden", "parent_id"]
    };
    internal static readonly IReadOnlyDictionary<string, JsonObject> Responses = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
    {
        ["IGUser GET"] = new JsonObject { ["$ref"] = "#/components/schemas/PublishingNode" },
        ["IGMedia GET"] = new JsonObject { ["$ref"] = "#/components/schemas/PublishingNode" },
        ["IGContainer GET"] = new JsonObject { ["$ref"] = "#/components/schemas/PublishingNode" },
        ["IGComment GET"] = new JsonObject { ["$ref"] = "#/components/schemas/PublishingNode" },
        ["IGUser POST media"] = Published(),
        ["IGUser POST media_publish"] = Published(),
        ["IGMedia POST comments"] = Published(),
        ["IGMedia POST"] = Published(),
        ["IGMedia DELETE"] = Published(),
        ["IGComment DELETE"] = Published()
    };

    internal static void AdjustSpecifications(IDictionary<string, string> specifications)
    {
        foreach ((string name, string[] names) in Fields)
        {
            var node = JsonNode.Parse(specifications[name])!.AsObject();
            var fields = node["fields"]!.AsArray();
            for (int i = fields.Count - 1; i >= 0; i--)
                if (!names.Contains(fields[i]!["name"]!.GetValue<string>(), StringComparer.Ordinal)) fields.RemoveAt(i);
            specifications[name] = node.ToJsonString();
        }
        specifications["PublishingResult"] = """{"fields":[{"name":"id","type":"string"},{"name":"post_id","type":"string"},{"name":"success","type":"bool"}]}""";
        var rootFields = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in Nodes)
        {
            if (!specifications.TryGetValue(name, out string? json)) continue;
            foreach (JsonNode? field in JsonNode.Parse(json)!["fields"]!.AsArray())
                if (seen.Add(field!["name"]!.GetValue<string>())) rootFields.Add(field.DeepClone());
        }
        specifications["PublishingNode"] = new JsonObject { ["fields"] = rootFields }.ToJsonString();
        specifications["IGContainer"] = """{"fields":[{"name":"id","type":"string"},{"name":"status_code","type":"string"},{"name":"status","type":"string"}],"apis":[{"name":"#get","method":"GET","return":"IGContainer","params":[]}]}""";
        rootFields.Add((System.Text.Json.Nodes.JsonNode?)new JsonObject { ["name"] = "status_code", ["type"] = "string" });
        rootFields.Add((System.Text.Json.Nodes.JsonNode?)new JsonObject { ["name"] = "status", ["type"] = "string" });
        specifications["PublishingNode"] = new JsonObject { ["fields"] = rootFields.DeepClone() }.ToJsonString();
        var user = JsonNode.Parse(specifications["IGUser"])!.AsObject();
        foreach (JsonObject api in user["apis"]!.AsArray().Cast<JsonObject>())
            if (api["endpoint"]?.GetValue<string>() == "media_publish")
                foreach (JsonObject parameter in api["params"]!.AsArray().Cast<JsonObject>())
                    if (parameter["name"]!.GetValue<string>() == "creation_id") parameter["type"] = "string";
        specifications["IGUser"] = user.ToJsonString();
    }

    private static JsonObject Published() => new() { ["$ref"] = "#/components/schemas/PublishingResult" };
}
