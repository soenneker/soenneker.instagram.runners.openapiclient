using System;
using System.Text.RegularExpressions;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Git.Util.Abstract;
using Soenneker.Kiota.Util.Abstract;
using Soenneker.OpenApi.Fixer.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.Environment;
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
    IOpenApiFixer fixer,
    IDotnetUtil dotnet,
    IDirectoryUtil directoryUtil,
    IFileUtil fileUtil) : IFileOperationsUtil
{
    public async ValueTask Process(CancellationToken cancellationToken = default)
    {
        string scratch = await directoryUtil.CreateTempDirectory(cancellationToken);
        try
        {
            string? specsDirectory = configuration["Instagram:MetaSpecsDirectory"];
            string revision = configuration["Instagram:MetaRef"] ?? "main";
            if (string.IsNullOrWhiteSpace(specsDirectory))
            {
                string upstream = Path.Combine(scratch, "meta");
                if (!Regex.IsMatch(revision, @"^[A-Za-z0-9][A-Za-z0-9._/-]*$"))
                    throw new ArgumentException(
                        "MetaRef must be a branch, tag, or commit using letters, digits, dots, underscores, slashes, or hyphens.");
                await git.Clone("https://github.com/facebook/facebook-business-sdk-codegen.git", upstream,
                    shallow: true, cancellationToken: cancellationToken);
                await git.Run($"fetch --depth=1 origin {revision}", upstream, cancellationToken: cancellationToken);
                await git.Run("checkout --detach FETCH_HEAD", upstream, cancellationToken: cancellationToken);
                revision = string.Join("",
                    await git.Run("rev-parse HEAD", upstream, cancellationToken: cancellationToken)).Trim();
                specsDirectory = Path.Combine(upstream, "api_specs", "specs");
            }
            else
            {
                specsDirectory = Path.GetFullPath(specsDirectory);
                revision = configuration["Instagram:MetaSourceRevision"] ?? "local";
            }

            string? clientDirectory = configuration["Instagram:ClientDirectory"];
            bool local = !string.IsNullOrWhiteSpace(clientDirectory);
            clientDirectory = local
                ? Path.GetFullPath(clientDirectory!)
                : await git.CloneToTempDirectory("https://github.com/soenneker/soenneker.instagram.openapiclient",
                    cancellationToken: cancellationToken);
            string projectDirectory = Path.Combine(clientDirectory, "src", Constants.Library);
            string project = Path.Combine(projectDirectory, Constants.Library + ".csproj");
            if (!await fileUtil.Exists(project, cancellationToken))
                throw new InvalidOperationException(
                    $"Client project not found: {project}. Set Instagram:ClientDirectory to the scaffolded repository.");

            string documentPath = Path.Combine(clientDirectory, "openapi.json");
            MetaOpenApiConversionResult result = await converter.ConvertToFileAsync(specsDirectory, documentPath,
                new MetaOpenApiConverterOptions
                {
                    GraphApiVersion = configuration["Instagram:GraphApiVersion"] ?? "v26.0",
                    Title = "Instagram Graph API",
                    Profile = MetaOpenApiProfile.Instagram,
                    SourceRevision = revision
                }, cancellationToken);
            logger.LogInformation("Converted Meta specs: {Schemas} schemas, {Paths} paths, {Diagnostics} diagnostics",
                result.Document["components"]!["schemas"]!.AsObject().Count, result.Document["paths"]!.AsObject().Count,
                result.Diagnostics.Count);
            await fileUtil.Write(Path.Combine(clientDirectory, "generation-diagnostics.json"),
                JsonSerializer.Serialize(result.Diagnostics, AotJsonContext.Get<System.Collections.Generic.IReadOnlyList<string>>(new JsonSerializerOptions { WriteIndented = true })),
                cancellationToken: cancellationToken);

            string fixedPath = Path.Combine(clientDirectory, "openapi.fixed.json");
            await fixer.Fix(documentPath, fixedPath, cancellationToken);
            result.ValidateOperationCoverage(await fileUtil.Read(fixedPath, cancellationToken: cancellationToken));
            await kiota.EnsureInstalled(cancellationToken);
            await kiota.Generate(fixedPath, "InstagramOpenApiClient", Constants.Library, scratch, cancellationToken);
            string generated = Path.Combine(scratch, "src", Constants.Library);
            await fixer.SanitizeGeneratedEnumMembers(generated, cancellationToken);
            string destination = Path.GetFullPath(Path.Combine(projectDirectory, "Generated"));
            if (Path.GetDirectoryName(destination) != Path.GetFullPath(projectDirectory))
                throw new InvalidOperationException("Generated directory must remain inside the client project.");
            if (await directoryUtil.Exists(destination, cancellationToken) &&
                new DirectoryInfo(destination).LinkTarget is not null)
                throw new InvalidOperationException("The generated directory cannot be a link.");
            await directoryUtil.DeleteIfExists(destination, cancellationToken);
            await directoryUtil.CopyDirectory(generated, destination, cancellationToken: cancellationToken);
            if (!await dotnet.Restore(project, cancellationToken: cancellationToken))
                throw new InvalidOperationException("The generated client could not be restored.");
            if (!await dotnet.Build(project, true, "Release", false, cancellationToken: cancellationToken))
                throw new InvalidOperationException("The generated client did not build successfully.");

            if (configuration.GetValue<bool>("Instagram:Push"))
            {
                string token = EnvironmentUtil.GetVariableStrict("GH__TOKEN");
                string name = EnvironmentUtil.GetVariableStrict("GIT__NAME");
                string email = EnvironmentUtil.GetVariableStrict("GIT__EMAIL");
                await git.CommitAndPush(clientDirectory,
                    "Regenerate Instagram Graph API client from Meta specifications", token, name, email,
                    cancellationToken);
            }

            logger.LogInformation("Generated and built {Library} in {Directory}", Constants.Library, clientDirectory);
        }
        finally
        {
            string expectedRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) +
                                  Path.DirectorySeparatorChar;
            if (Path.GetFullPath(scratch).StartsWith(expectedRoot,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                await directoryUtil.DeleteIfExists(scratch, CancellationToken.None);
            }
        }
    }
}