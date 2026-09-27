using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Git.Util.Abstract;
using Soenneker.OpenApi.Converters.Meta.Abstract;
using Soenneker.OpenApi.Converters.Meta.Models;
using Soenneker.Instagram.Runners.OpenApiClient.Utils.Abstract;

namespace Soenneker.Instagram.Runners.OpenApiClient.Utils;

public sealed class FileOperationsUtil(
    IConfiguration configuration,
    ILogger<FileOperationsUtil> logger,
    IMetaOpenApiConverter converter,
    IGitUtil git) : IFileOperationsUtil
{
    public async ValueTask Process(CancellationToken cancellationToken = default)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "soenneker-Instagram-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string? specsDirectory = configuration["Instagram:MetaSpecsDirectory"];
            string revision = configuration["Instagram:MetaRef"] ?? "main";
            if (string.IsNullOrWhiteSpace(specsDirectory))
            {
                string upstream = Path.Combine(scratch, "meta");
                await Run("git", ["clone", "--depth", "1", "--branch", revision,
                    "https://github.com/facebook/facebook-business-sdk-codegen.git", upstream], scratch, cancellationToken);
                revision = (await Run("git", ["rev-parse", "HEAD"], upstream, cancellationToken)).Trim();
                specsDirectory = Path.Combine(upstream, "api_specs", "specs");
            }
            else
            {
                specsDirectory = Path.GetFullPath(specsDirectory);
                revision = configuration["Instagram:MetaSourceRevision"] ?? "local";
            }

            string? clientDirectory = configuration["Instagram:ClientDirectory"];
            bool local = !string.IsNullOrWhiteSpace(clientDirectory);
            clientDirectory = local ? Path.GetFullPath(clientDirectory!) :
                await git.CloneToTempDirectory("https://github.com/soenneker/soenneker.instagram.openapiclient", cancellationToken: cancellationToken);
            string projectDirectory = Path.Combine(clientDirectory, "src", Constants.Library);
            string project = Path.Combine(projectDirectory, Constants.Library + ".csproj");
            if (!File.Exists(project))
                throw new InvalidOperationException($"Client project not found: {project}. Set Instagram:ClientDirectory to the scaffolded repository.");

            var specifications = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string path in Directory.EnumerateFiles(specsDirectory, "*.json").Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileNameWithoutExtension(path);
                JsonNode node = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))!;
                if (node is JsonObject obj && obj["apis"] is JsonArray apis)
                {
                    for (int i = apis.Count - 1; i >= 0; i--)
                    {
                        string operation = $"{name} {apis[i]!["method"]!.GetValue<string>()} {apis[i]!["endpoint"]?.GetValue<string>()}".TrimEnd();
                        if (!PublishingProfile.Operations.Contains(operation)) apis.RemoveAt(i);
                    }
                }
                specifications.Add(name, node.ToJsonString());
            }
            PublishingProfile.AdjustSpecifications(specifications);
            var availableOperations = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string nodeName, string json) in specifications)
            {
                if (JsonNode.Parse(json) is not JsonObject node || node["apis"] is not JsonArray apis) continue;
                foreach (JsonNode? api in apis)
                    availableOperations.Add($"{nodeName} {api!["method"]!.GetValue<string>()} {api["endpoint"]?.GetValue<string>()}".TrimEnd());
            }
            string[] missingOperations = PublishingProfile.Operations.Except(availableOperations).ToArray();
            if (missingOperations.Length > 0)
                throw new InvalidOperationException($"Meta specifications are missing required publishing operations: {string.Join(", ", missingOperations)}");
            var options = new MetaOpenApiConverterOptions
            {
                GraphApiVersion = configuration["Instagram:GraphApiVersion"] ?? "v26.0",
                Title = "Instagram Publishing API",
                NodeTypes = PublishingProfile.Nodes,
                ResponseSchemaOverrides = PublishingProfile.Responses
            };
            MetaOpenApiConversionResult result = converter.Convert(specifications, options);
            // Kiota cannot name a root indexer called just {id}; use a descriptive parameter.
            var paths = result.Document["paths"]!.AsObject();
            foreach ((string path, JsonNode? item) in paths.ToArray())
            {
                if (!path.Contains("{id}", StringComparison.Ordinal)) continue;
                paths.Remove(path);
                paths[path.Replace("{id}", "{node-id}", StringComparison.Ordinal)] = item;
                foreach (JsonObject operation in item!.AsObject().Select(x => x.Value).OfType<JsonObject>())
                    if (operation["parameters"] is JsonArray parameters)
                        foreach (JsonObject parameter in parameters.Cast<JsonObject>())
                            if (parameter["in"]?.GetValue<string>() == "path" && parameter["name"]?.GetValue<string>() == "id")
                                parameter["name"] = "node-id";
            }
            PruneSchemas(result.Document);
            result.Document["x-meta-source"] = new JsonObject
            {
                ["repository"] = "https://github.com/facebook/facebook-business-sdk-codegen",
                ["revision"] = revision,
                ["profile"] = "Instagram publishing"
            };
            string documentPath = Path.Combine(clientDirectory, "openapi.json");
            await File.WriteAllTextAsync(documentPath, result.ToJson(), cancellationToken);
            logger.LogInformation("Converted Meta specs: {Schemas} schemas, {Paths} paths, {Diagnostics} diagnostics",
                result.Document["components"]!["schemas"]!.AsObject().Count, result.Document["paths"]!.AsObject().Count, result.Diagnostics.Count);
            await File.WriteAllTextAsync(Path.Combine(clientDirectory, "generation-diagnostics.json"),
                JsonSerializer.Serialize(result.Diagnostics, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

            string manifest = Path.Combine(AppContext.BaseDirectory, ".config", "dotnet-tools.json");
            await Run("dotnet", ["tool", "restore", "--tool-manifest", manifest], AppContext.BaseDirectory, cancellationToken);
            string generated = Path.Combine(scratch, "generated");
            await Run("dotnet", ["tool", "run", "kiota", "--", "generate", "--language", "CSharp",
                "--openapi", documentPath, "--output", generated, "--class-name", "InstagramOpenApiClient",
                "--namespace-name", Constants.Library, "--exclude-backward-compatible", "--additional-data"], AppContext.BaseDirectory, cancellationToken);

            string destination = Path.GetFullPath(Path.Combine(projectDirectory, "Generated"));
            if (Path.GetDirectoryName(destination) != Path.GetFullPath(projectDirectory))
                throw new InvalidOperationException("Generated directory must remain inside the client project.");
            if (Directory.Exists(destination))
            {
                if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("The generated directory cannot be a link.");
                Directory.Delete(destination, true);
            }
            Directory.CreateDirectory(destination);
            foreach (string path in Directory.EnumerateFiles(generated, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, Path.GetRelativePath(generated, path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target);
            }
            await Run("dotnet", ["build", project, "--configuration", "Release", "--verbosity", "minimal"], clientDirectory, cancellationToken);

            if (configuration.GetValue<bool>("Instagram:Push"))
            {
                string token = Environment.GetEnvironmentVariable("GH__TOKEN") ?? throw new InvalidOperationException("GH__TOKEN is required to push.");
                string name = Environment.GetEnvironmentVariable("GIT__NAME") ?? throw new InvalidOperationException("GIT__NAME is required to push.");
                string email = Environment.GetEnvironmentVariable("GIT__EMAIL") ?? throw new InvalidOperationException("GIT__EMAIL is required to push.");
                await git.CommitAndPush(clientDirectory, "Regenerate Instagram publishing client from Meta specifications", token, name, email, cancellationToken);
            }
            logger.LogInformation("Generated and built {Library} in {Directory}", Constants.Library, clientDirectory);
        }
        finally
        {
            string expectedRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(scratch).StartsWith(expectedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                // Git pack files can be read-only on Windows.
                foreach (string file in Directory.EnumerateFiles(scratch, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint
                }))
                    File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                Directory.Delete(scratch, true);
            }
        }
    }

    private static void PruneSchemas(JsonObject document)
    {
        var schemas = document["components"]!["schemas"]!.AsObject();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["$ref"] is JsonValue value && value.TryGetValue<string>(out string? reference) &&
                    reference.StartsWith("#/components/schemas/", StringComparison.Ordinal))
                {
                    string name = reference["#/components/schemas/".Length..];
                    if (keep.Add(name)) pending.Enqueue(name);
                }
                foreach (var property in obj) Visit(property.Value);
            }
            else if (node is JsonArray array)
                foreach (JsonNode? item in array) Visit(item);
        }
        Visit(document["paths"]);
        while (pending.TryDequeue(out string? name))
        {
            if (!schemas.ContainsKey(name)) throw new InvalidOperationException($"Unresolved schema reference: {name}");
            Visit(schemas[name]);
        }
        foreach (string name in schemas.Select(x => x.Key).ToArray())
            if (!keep.Contains(name)) schemas.Remove(name);
    }

    private async Task<string> Run(string command, string[] arguments, string directory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(command) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        string output = await stdout;
        string errors = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{command} failed ({process.ExitCode}): {output}\n{errors}");
        if (!string.IsNullOrWhiteSpace(output)) logger.LogInformation("{Output}", output.Trim());
        if (!string.IsNullOrWhiteSpace(errors)) logger.LogInformation("{Output}", errors.Trim());
        return output;
    }
}
