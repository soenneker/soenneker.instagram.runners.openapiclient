using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Git.Util.Abstract;
using Soenneker.Kiota.Util.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.OpenApi.Converters.Meta.Abstract;
using Soenneker.OpenApi.Converters.Meta.Models;
using Soenneker.Instagram.Runners.OpenApiClient.Utils.Abstract;

namespace Soenneker.Instagram.Runners.OpenApiClient.Utils;

public sealed class FileOperationsUtil(
    IConfiguration configuration,
    ILogger<FileOperationsUtil> logger,
    IMetaOpenApiConverter converter,
    IGitUtil git,
    IKiotaUtil kiota,
    IDotnetUtil dotnet) : IFileOperationsUtil
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

            string documentPath = Path.Combine(clientDirectory, "openapi.json");
            MetaOpenApiConversionResult result = await converter.ConvertToFileAsync(specsDirectory, documentPath,
                new MetaOpenApiConverterOptions
                {
                    GraphApiVersion = configuration["Instagram:GraphApiVersion"] ?? "v26.0",
                    Title = "Instagram Publishing API",
                    Profile = MetaOpenApiProfile.InstagramPublishing,
                    SourceRevision = revision
                }, cancellationToken);
            logger.LogInformation("Converted Meta specs: {Schemas} schemas, {Paths} paths, {Diagnostics} diagnostics",
                result.Document["components"]!["schemas"]!.AsObject().Count, result.Document["paths"]!.AsObject().Count, result.Diagnostics.Count);
            await File.WriteAllTextAsync(Path.Combine(clientDirectory, "generation-diagnostics.json"),
                JsonSerializer.Serialize(result.Diagnostics, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

            await kiota.EnsureInstalled(cancellationToken);
            await kiota.Generate(documentPath, "InstagramOpenApiClient", Constants.Library, scratch, cancellationToken);
            string generated = Path.Combine(scratch, "src", Constants.Library);
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
            if (!await dotnet.Restore(project, cancellationToken: cancellationToken))
                throw new InvalidOperationException("The generated client could not be restored.");
            if (!await dotnet.Build(project, true, "Release", false, cancellationToken: cancellationToken))
                throw new InvalidOperationException("The generated client did not build successfully.");

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
