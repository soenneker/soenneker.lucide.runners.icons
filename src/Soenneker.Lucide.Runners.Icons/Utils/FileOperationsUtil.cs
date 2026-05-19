using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Git.Util.Abstract;
using Soenneker.Lucide.Runners.Icons.Utils.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.PooledStringBuilders;
using Soenneker.Utils.Process.Abstract;

namespace Soenneker.Lucide.Runners.Icons.Utils;

///<inheritdoc cref="IFileOperationsUtil"/>
public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IGitUtil _gitUtil;
    private readonly IProcessUtil _processUtil;

    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IFileUtil fileUtil, IDirectoryUtil directoryUtil, IGitUtil gitUtil, IProcessUtil processUtil)
    {
        _logger = logger;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
        _gitUtil = gitUtil;
        _processUtil = processUtil;
    }

    public async ValueTask Process(CancellationToken cancellationToken)
    {
        string workingDirectory = await _directoryUtil.CreateTempDirectory(cancellationToken);
        string upstreamDirectory = Path.Combine(workingDirectory, "lucide");
        string targetDirectory = Path.Combine(workingDirectory, Constants.TargetRepository);

        try
        {
            await _gitUtil.Clone(Constants.UpstreamRepositoryUrl, upstreamDirectory, shallow: true, cancellationToken: cancellationToken);
            string upstreamCommit = (await _gitUtil.Run("rev-parse HEAD", upstreamDirectory, cancellationToken: cancellationToken))[0].Trim();

            await _gitUtil.Clone($"https://github.com/soenneker/{Constants.TargetRepository}.git", targetDirectory, shallow: true, cancellationToken: cancellationToken);

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

            if (!await _gitUtil.HasWorkingTreeChanges(targetDirectory, cancellationToken))
            {
                _logger.LogInformation("{Library} is already current at upstream commit {UpstreamCommit}", Constants.Library, upstreamCommit);
                return;
            }

            string projectPath = Path.Combine(targetDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

            await RunProcess("dotnet", BuildArguments("restore", projectPath, "--verbosity", "minimal"), targetDirectory, cancellationToken);
            await RunProcess("dotnet", BuildArguments("build", projectPath, "--configuration", "Release", "--no-restore", "--verbosity", "minimal"), targetDirectory,
                cancellationToken);

            string version = GetRequiredEnvironmentVariable("BUILD_VERSION");
            await RunProcess("dotnet",
                BuildArguments("pack", projectPath, "--configuration", "Release", "--no-build", "--no-restore", "--output", targetDirectory,
                    $"/p:PackageVersion={version}", "--verbosity", "minimal"), targetDirectory, cancellationToken);

            string packagePath = Path.Combine(targetDirectory, $"{Constants.Library}.{version}.nupkg");
            string apiKey = GetRequiredEnvironmentVariable("NUGET__TOKEN");
            await RunProcess("dotnet", BuildArguments("nuget", "push", packagePath, "--api-key", apiKey, "--source", "https://api.nuget.org/v3/index.json", "--skip-duplicate"),
                targetDirectory, cancellationToken);

            await CommitAndPush(targetDirectory, upstreamCommit, cancellationToken);

            _logger.LogInformation("Updated {Library} from lucide-icons/lucide commit {UpstreamCommit}", Constants.Library, upstreamCommit);
        }
        finally
        {
            await _directoryUtil.DeleteIfExists(workingDirectory, cancellationToken);
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

    private ValueTask<string> RunProcess(string fileName, string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        return _processUtil.StartAndGetOutput(fileName, arguments, workingDirectory, cancellationToken: cancellationToken);
    }

    private static string BuildArguments(params string[] arguments)
    {
        using var builder = new PooledStringBuilder();

        foreach (string argument in arguments)
        {
            if (builder.Length > 0)
                builder.Append(' ');

            AppendEscapedArgument(builder, argument);
        }

        return builder.ToString();
    }

    private static void AppendEscapedArgument(PooledStringBuilder builder, string argument)
    {
        if (!RequiresQuotes(argument))
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');

        foreach (char character in argument)
        {
            if (character is '"' or '\\')
                builder.Append('\\');

            builder.Append(character);
        }

        builder.Append('"');
    }

    private static bool RequiresQuotes(string argument)
    {
        foreach (char character in argument)
        {
            if (char.IsWhiteSpace(character) || character is '"')
                return true;
        }

        return argument.Length == 0;
    }
}
