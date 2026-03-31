using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Extensions.String;
using Soenneker.Git.Util.Abstract;
using Soenneker.Hashing.Blake3.Abstract;
using Soenneker.Lucide.Runners.Icons.Utils.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Dotnet.NuGet.Abstract;
using Soenneker.Utils.Environment;
using Soenneker.Utils.File.Abstract;

namespace Soenneker.Lucide.Runners.Icons.Utils;

///<inheritdoc cref="IFileOperationsUtil"/>
public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IDotnetNuGetUtil _dotnetNuGetUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IBlake3Util _blake3Util;

    private const string _lucideIconsUrl = "https://github.com/lucide-icons/lucide";

    public FileOperationsUtil(IFileUtil fileUtil, ILogger<FileOperationsUtil> logger, IGitUtil gitUtil, IDotnetUtil dotnetUtil,
        IDotnetNuGetUtil dotnetNuGetUtil, IDirectoryUtil directoryUtil, IBlake3Util blake3Util)
    {
        _fileUtil = fileUtil;
        _logger = logger;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _dotnetNuGetUtil = dotnetNuGetUtil;
        _directoryUtil = directoryUtil;
        _blake3Util = blake3Util;
    }

    public async ValueTask Process(CancellationToken cancellationToken)
    {
        string gitDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}",
            cancellationToken: cancellationToken);

        string lucideDirectory = await _gitUtil.CloneToTempDirectory(_lucideIconsUrl, cancellationToken: cancellationToken);
        string lucideIconsPath = Path.Combine(lucideDirectory, "icons");
        string resourceDirectory = Path.Combine(gitDirectory, "src", Constants.Library, "Resources");
        string transformedDirectory = await CreateTransformedIconsDirectory(lucideIconsPath, cancellationToken);

        bool needToUpdate = await CheckForOutputDifferences(resourceDirectory, transformedDirectory, cancellationToken);

        if (!needToUpdate)
        {
            await _fileUtil.DeleteAll(transformedDirectory, true, cancellationToken);
            return;
        }

        try
        {
            await BuildPackAndPush(gitDirectory, resourceDirectory, transformedDirectory, cancellationToken);
            await CommitAndPushChanges(gitDirectory, cancellationToken);
        }
        finally
        {
            await _fileUtil.DeleteAll(transformedDirectory, true, cancellationToken);
        }
    }

    private async ValueTask BuildPackAndPush(string gitDirectory, string resourceDirectory, string transformedDirectory, CancellationToken cancellationToken)
    {
        await _directoryUtil.Create(resourceDirectory, cancellationToken: cancellationToken);

        await _fileUtil.DeleteAll(resourceDirectory, true, cancellationToken);

        await CopyIcons(transformedDirectory, resourceDirectory, cancellationToken);

        string projFilePath = Path.Combine(gitDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

        await _dotnetUtil.Restore(projFilePath, cancellationToken: cancellationToken);

        bool successful = await _dotnetUtil.Build(projFilePath, true, "Release", false, cancellationToken: cancellationToken);

        if (!successful)
        {
            _logger.LogError("Build was not successful, exiting...");
            return;
        }

        string version = EnvironmentUtil.GetVariableStrict("BUILD_VERSION");

        await _dotnetUtil.Pack(projFilePath, version, true, "Release", false, false, gitDirectory, cancellationToken: cancellationToken);

        string apiKey = EnvironmentUtil.GetVariableStrict("NUGET__TOKEN");

        string nuGetPackagePath = Path.Combine(gitDirectory, $"{Constants.Library}.{version}.nupkg");

        await _dotnetNuGetUtil.Push(nuGetPackagePath, apiKey: apiKey, cancellationToken: cancellationToken);
    }

    private async ValueTask<string> CreateTransformedIconsDirectory(string lucideIconsPath, CancellationToken cancellationToken)
    {
        string transformedDirectory = Path.Combine(Path.GetTempPath(), $"lucide-transformed-{Guid.NewGuid():N}");

        await _directoryUtil.Create(transformedDirectory, cancellationToken: cancellationToken);
        await CopyIconsFromLucide(lucideIconsPath, transformedDirectory, cancellationToken);

        return transformedDirectory;
    }

    private async ValueTask CopyIconsFromLucide(string lucideIconsPath, string destinationDirectory, CancellationToken cancellationToken)
    {
        List<string> svgFiles = await _directoryUtil.GetFilesByExtension(lucideIconsPath, "svg", true, cancellationToken);
        foreach (string svgPath in svgFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destPath = Path.Combine(destinationDirectory, Path.GetFileName(svgPath));
            string? content = await _fileUtil.TryRead(svgPath, true, cancellationToken);
            if (content == null)
            {
                await _fileUtil.Copy(svgPath, destPath, true, cancellationToken);
                continue;
            }
            string stripped = StripSvgWidthAndHeight(content);
            await _fileUtil.Write(destPath, stripped, true, cancellationToken);
        }

        _logger.LogInformation("Copied {Count} Lucide SVG icons to {Destination}", svgFiles.Count, destinationDirectory);
    }

    private static string StripSvgWidthAndHeight(string svgContent)
    {
        return Regex.Replace(svgContent, @"<svg\b[^>]*>", match =>
        {
            return Regex.Replace(match.Value, @"\s+(?:width|height)\s*=\s*[""']?[^""'\s>]+[""']?", string.Empty);
        }, RegexOptions.IgnoreCase);
    }

    private async ValueTask CopyIcons(string sourceDirectory, string destinationDirectory, CancellationToken cancellationToken)
    {
        List<string> svgFiles = await _directoryUtil.GetFilesByExtension(sourceDirectory, "svg", true, cancellationToken);

        foreach (string svgPath in svgFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string destPath = Path.Combine(destinationDirectory, Path.GetFileName(svgPath));
            await _fileUtil.Copy(svgPath, destPath, true, cancellationToken);
        }
    }

    private async ValueTask<bool> CheckForOutputDifferences(string resourceDirectory, string transformedDirectory, CancellationToken cancellationToken)
    {
        string transformedHash = await _blake3Util.HashDirectoryToAggregateString(transformedDirectory, cancellationToken);

        if (!Directory.Exists(resourceDirectory))
        {
            _logger.LogInformation("Output directory does not exist yet, proceeding to update...");
            return true;
        }

        string currentOutputHash = await _blake3Util.HashDirectoryToAggregateString(resourceDirectory, cancellationToken);

        if (currentOutputHash == transformedHash)
        {
            _logger.LogInformation("Output directories are identical, no need to update, exiting...");
            return false;
        }

        return true;
    }

    private async ValueTask CommitAndPushChanges(string gitDirectory, CancellationToken cancellationToken)
    {
        if (await _gitUtil.IsRepositoryDirty(gitDirectory, cancellationToken))
        {
            _logger.LogInformation("Changes have been detected in the repository, commiting and pushing...");

            string name = EnvironmentUtil.GetVariableStrict("GIT__NAME");
            string email = EnvironmentUtil.GetVariableStrict("GIT__EMAIL");
            string token = EnvironmentUtil.GetVariableStrict("GH__TOKEN");

            await _gitUtil.Commit(gitDirectory, "Updates hash for new version", name, email, cancellationToken);

            await _gitUtil.Push(gitDirectory, token, cancellationToken);
        }
        else
        {
            _logger.LogInformation("There are no changes to commit");
        }
    }
}
