using System.Text.RegularExpressions;
using System.Xml.Linq;
using IotStarter.App.Core.Provisioning;
using IotStarter.App.Core.State;

namespace IotStarter.App.Core.Tests;

/// <summary>
/// Every string the UI asks for must exist in Strings.resx, including keys built from enum names.
/// A missing key would show the raw key to users, so this guards the localisation contract.
/// </summary>
public partial class LocalizationKeysTests
{
    [Fact]
    public void Every_ui_key_exists_in_the_resource_file()
    {
        string appDirectory = Path.Combine(RepositoryRoot(), "src", "IotStarter.App");
        HashSet<string> keys = XDocument.Load(Path.Combine(appDirectory, "Resources", "Strings.resx"))
            .Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToHashSet(StringComparer.Ordinal);

        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(appDirectory, "Components"), "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal)))
        {
            foreach (Match match in LiteralKey().Matches(File.ReadAllText(file)))
            {
                string key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (key.Length > 0)
                {
                    required.Add(key);
                }
            }
        }

        foreach (FailureKind kind in Enum.GetValues<FailureKind>())
        {
            required.Add($"Failure.{kind}.Title");
            required.Add($"Failure.{kind}.Advice");
        }

        foreach (BlockReason reason in Enum.GetValues<BlockReason>())
        {
            required.Add($"Blocked.{reason}.Title");
            required.Add($"Blocked.{reason}.Body");
        }

        foreach (NoticeKind notice in Enum.GetValues<NoticeKind>())
        {
            required.Add($"Notice.{notice}");
        }

        foreach (CredentialProblem problem in Enum.GetValues<CredentialProblem>().Where(p => p != CredentialProblem.None))
        {
            required.Add($"Credential.{problem}");
        }

        for (int step = 0; step < 4; step++)
        {
            required.Add($"Wizard.Step{step}");
        }

        string[] missing = [.. required.Where(k => !keys.Contains(k)).Order()];
        Assert.True(missing.Length == 0, "Missing resource keys: " + string.Join(", ", missing));
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "IotStarter.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    // L["Key"] and L["Key", args] in Razor and C#; also the ternary keys inside L[...].
    [GeneratedRegex("""L\[\s*(?:[^\]"]*\?\s*)?"([A-Za-z0-9.]+)"|:\s*"((?:Network|Common|Failure)\.[A-Za-z0-9.]+)"\]""")]
    private static partial Regex LiteralKey();
}
