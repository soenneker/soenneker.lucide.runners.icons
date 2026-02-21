using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Extensions.String;
using Soenneker.Git.Util.Abstract;
using Soenneker.Lucide.Runners.Icons.Utils.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Dotnet.NuGet.Abstract;
using Soenneker.Utils.Environment;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.SHA3.Abstract;

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
    private readonly ISha3Util _sha3Util;

    private string? _newHash;

    private const string _lucideIconsUrl = "https://github.com/lucide-icons/lucide";

    private const bool _overrideHash = false;

    public FileOperationsUtil(IFileUtil fileUtil, ILogger<FileOperationsUtil> logger, IGitUtil gitUtil, IDotnetUtil dotnetUtil,
        IDotnetNuGetUtil dotnetNuGetUtil, IDirectoryUtil directoryUtil, ISha3Util sha3Util)
    {
        _fileUtil = fileUtil;
        _logger = logger;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _dotnetNuGetUtil = dotnetNuGetUtil;
        _directoryUtil = directoryUtil;
        _sha3Util = sha3Util;
    }

    public async ValueTask Process(CancellationToken cancellationToken)
    {
        string gitDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}",
            cancellationToken: cancellationToken);

        string lucideDirectory = await _gitUtil.CloneToTempDirectory(_lucideIconsUrl, cancellationToken: cancellationToken);
        string lucideIconsPath = Path.Combine(lucideDirectory, "icons");
        string resourceDirectory = Path.Combine(gitDirectory, "src", "Resources");

        bool needToUpdate = await CheckForHashDifferences(gitDirectory, lucideIconsPath, cancellationToken);

        if (!needToUpdate)
            return;

        await BuildPackAndPush(gitDirectory, resourceDirectory, lucideIconsPath, cancellationToken);

        await SaveHashToGitRepo(gitDirectory, cancellationToken);
    }

    private async ValueTask BuildPackAndPush(string gitDirectory, string resourceDirectory, string lucideIconsPath, CancellationToken cancellationToken)
    {
        await _directoryUtil.CreateIfDoesNotExist(resourceDirectory, cancellationToken: cancellationToken);

        await _fileUtil.DeleteAll(resourceDirectory, true, cancellationToken);

        await CopyIconsFromLucide(lucideIconsPath, resourceDirectory, cancellationToken);

        string projFilePath = Path.Combine(gitDirectory, "src", $"{Constants.Library}.csproj");

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

    private async ValueTask CopyIconsFromLucide(string lucideIconsPath, string resourceDirectory, CancellationToken cancellationToken)
    {
        List<string> svgFiles = await _directoryUtil.GetFilesByExtension(lucideIconsPath, "svg", true, cancellationToken);
        foreach (string svgPath in svgFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destPath = Path.Combine(resourceDirectory, Path.GetFileName(svgPath));
            await _fileUtil.Copy(svgPath, destPath, true, cancellationToken);
        }

        _logger.LogInformation("Copied {Count} Lucide SVG icons to {Destination}", svgFiles.Count, resourceDirectory);
    }

    private async ValueTask<bool> CheckForHashDifferences(string gitDirectory, string lucideIconsPath, CancellationToken cancellationToken)
    {
        string? oldHash = await _fileUtil.TryRead(Path.Combine(gitDirectory, "hash.txt"), true, cancellationToken);

        if (oldHash == null)
        {
            _logger.LogDebug("Could not read hash from repository, proceeding to update...");
            return true;
        }

        _newHash = await _sha3Util.HashDirectory(lucideIconsPath, true, cancellationToken);

        if (oldHash == _newHash)
        {
            if (_overrideHash)
                _logger.LogWarning("Hashes are equal but override is set, so continuing...");
            else
            {
                _logger.LogInformation("Hashes are equal, no need to update, exiting...");
                return false;
            }
        }

        return true;
    }

    private async ValueTask SaveHashToGitRepo(string gitDirectory, CancellationToken cancellationToken)
    {
        string targetHashFile = Path.Combine(gitDirectory, "hash.txt");

        await _fileUtil.DeleteIfExists(targetHashFile, cancellationToken: cancellationToken);

        await _fileUtil.Write(targetHashFile, _newHash!, true, cancellationToken);

        await _gitUtil.AddIfNotExists(gitDirectory, targetHashFile, cancellationToken);

        if (await _gitUtil.IsRepositoryDirty(gitDirectory, cancellationToken))
        {
            _logger.LogInformation("Changes have been detected in the repository, commiting and pushing...");

            string name = EnvironmentUtil.GetVariableStrict("GIT__NAME");
            string email = EnvironmentUtil.GetVariableStrict("GIT__EMAIL");
            string username = EnvironmentUtil.GetVariableStrict("GH__USERNAME");
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
