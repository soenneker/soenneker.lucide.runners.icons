using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Git.Util.Abstract;
using Soenneker.Hashing.XxHash;
using Soenneker.Lucide.Runners.Icons.Utils.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Dotnet.NuGet.Abstract;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.PooledStringBuilders;

namespace Soenneker.Lucide.Runners.Icons.Utils;

///<inheritdoc cref="IFileOperationsUtil"/>
public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private const string HashFileName = "hash.txt";

    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IDotnetNuGetUtil _dotnetNuGetUtil;

    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IFileUtil fileUtil, IDirectoryUtil directoryUtil, IGitUtil gitUtil, IDotnetUtil dotnetUtil,
        IDotnetNuGetUtil dotnetNuGetUtil)
    {
        _logger = logger;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _dotnetNuGetUtil = dotnetNuGetUtil;
    }

    public async ValueTask Process(CancellationToken cancellationToken)
    {
        string upstreamDirectory = await _gitUtil.CloneToTempDirectory(Constants.UpstreamRepositoryUrl, cancellationToken: cancellationToken);
        string targetDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.TargetRepository}", cancellationToken: cancellationToken);

        try
        {
            string upstreamCommit = (await _gitUtil.Run("rev-parse HEAD", upstreamDirectory, log: false, cancellationToken: cancellationToken))[0].Trim();

            string upstreamIconsDirectory = Path.Combine(upstreamDirectory, "icons");
            string targetResourcesDirectory = Path.Combine(targetDirectory, "src", Constants.Library, "Resources");

            await _directoryUtil.DeleteIfExists(targetResourcesDirectory, cancellationToken);
            await _directoryUtil.Create(targetResourcesDirectory, cancellationToken: cancellationToken);

            var svgFiles = await _directoryUtil.GetFilesByExtension(upstreamIconsDirectory, ".svg", cancellationToken: cancellationToken);

            foreach (string svgFile in svgFiles)
            {
                string targetPath = Path.Combine(targetResourcesDirectory, Path.GetFileName(svgFile));
                await CopySvgWithoutDimensions(svgFile, targetPath, cancellationToken);
            }

            _logger.LogInformation("Copied {Count} Lucide SVG resources from upstream commit {UpstreamCommit}", svgFiles.Count, upstreamCommit);

            string newHash = await HashSvgDirectory(targetResourcesDirectory, cancellationToken);
            string hashPath = Path.Combine(targetDirectory, HashFileName);
            string? existingHash = await _fileUtil.TryRead(hashPath, cancellationToken: cancellationToken);

            if (StringComparer.Ordinal.Equals(existingHash?.Trim(), newHash))
            {
                _logger.LogInformation("{Library} hash is already current at upstream commit {UpstreamCommit}", Constants.Library, upstreamCommit);
                return;
            }

            await _fileUtil.Write(hashPath, newHash, cancellationToken: cancellationToken);

            string projectPath = Path.Combine(targetDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

            await RestoreBuildPackAndPush(projectPath, targetDirectory, cancellationToken);

            await CommitAndPush(targetDirectory, upstreamCommit, cancellationToken);

            _logger.LogInformation("Updated {Library} from lucide-icons/lucide commit {UpstreamCommit}", Constants.Library, upstreamCommit);
        }
        finally
        {
            await _directoryUtil.DeleteIfExists(upstreamDirectory, cancellationToken);
            await _directoryUtil.DeleteIfExists(targetDirectory, cancellationToken);
        }
    }

    private async ValueTask CommitAndPush(string targetDirectory, string upstreamCommit, CancellationToken cancellationToken)
    {
        if (!await _gitUtil.HasWorkingTreeChanges(targetDirectory, cancellationToken))
            return;

        string name = GetRequiredEnvironmentVariable("GIT__NAME");
        string email = GetRequiredEnvironmentVariable("GIT__EMAIL");
        string token = GetRequiredEnvironmentVariable("GH__TOKEN");

        await _gitUtil.CommitAndPush(targetDirectory, $"Update Lucide icons from upstream {upstreamCommit[..12]}", token, name, email, cancellationToken);
    }

    private async ValueTask<string> HashSvgDirectory(string directory, CancellationToken cancellationToken)
    {
        var svgFiles = await _directoryUtil.GetFilesByExtension(directory, ".svg", cancellationToken: cancellationToken);
        svgFiles.Sort(StringComparer.Ordinal);
        var manifestParts = new List<KeyValuePair<string, string>>(svgFiles.Count);

        foreach (string svgFile in svgFiles)
        {
            string relativePath = Path.GetRelativePath(directory, svgFile).Replace('\\', '/');
            string? svg = await _fileUtil.TryRead(svgFile, cancellationToken: cancellationToken);

            if (svg is null)
                throw new FileNotFoundException("Could not read SVG file", svgFile);

            manifestParts.Add(new KeyValuePair<string, string>(relativePath, svg));
        }

        using var builder = new PooledStringBuilder();

        foreach (KeyValuePair<string, string> part in manifestParts)
        {
            builder.Append(part.Key);
            builder.Append('\n');
            builder.Append(part.Value);
            builder.Append('\n');
        }

        return XxHash3Util.Hash(builder.ToString());
    }

    private async ValueTask RestoreBuildPackAndPush(string projectPath, string targetDirectory, CancellationToken cancellationToken)
    {
        await _dotnetUtil.Restore(projectPath, verbosity: "minimal", cancellationToken: cancellationToken);

        bool successful = await _dotnetUtil.Build(projectPath, configuration: "Release", restore: false, verbosity: "minimal", cancellationToken: cancellationToken);

        if (!successful)
            throw new InvalidOperationException($"{Constants.Library} build failed");

        string version = GetRequiredEnvironmentVariable("BUILD_VERSION");
        await _dotnetUtil.Pack(projectPath, version, configuration: "Release", build: false, restore: false, output: targetDirectory, verbosity: "minimal",
            cancellationToken: cancellationToken);

        string packagePath = Path.Combine(targetDirectory, $"{Constants.Library}.{version}.nupkg");
        string apiKey = GetRequiredEnvironmentVariable("NUGET__TOKEN");
        await _dotnetNuGetUtil.Push(packagePath, apiKey: apiKey, skipDuplicate: true, cancellationToken: cancellationToken);
    }

    private async ValueTask CopySvgWithoutDimensions(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        string? svg = await _fileUtil.TryRead(sourcePath, cancellationToken: cancellationToken);

        if (svg is null)
            throw new FileNotFoundException("Could not read SVG file", sourcePath);

        await _fileUtil.Write(targetPath, RemoveSvgDimensionAttributes(svg), cancellationToken: cancellationToken);
    }

    private static string RemoveSvgDimensionAttributes(string svg)
    {
        int svgStart = svg.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);

        if (svgStart < 0)
            return svg;

        int svgTagEnd = svg.IndexOf('>', svgStart);

        if (svgTagEnd < 0)
            return svg;

        string openingTag = svg[svgStart..svgTagEnd];
        string normalizedOpeningTag = Regex.Replace(openingTag, "\\s+(width|height)=(\"[^\"]*\"|'[^']*'|[^\\s>]+)", "", RegexOptions.IgnoreCase);

        using var builder = new PooledStringBuilder(svg.Length);
        builder.Append(svg.AsSpan(0, svgStart));
        builder.Append(normalizedOpeningTag);
        builder.Append(svg.AsSpan(svgTagEnd));

        return builder.ToString();
    }

    private static string GetRequiredEnvironmentVariable(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);

        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} is not set");

        return value;
    }

}
