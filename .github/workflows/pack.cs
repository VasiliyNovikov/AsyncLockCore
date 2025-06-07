#:property LangVersion preview

using System.Text.Json;
using System.Xml;

Console.WriteLine("Assigning package version...");

Uri uri = new("https://api.nuget.org/v3-flatcontainer/asynclockcore/index.json");

using HttpClient client = new();
string json = await client.GetStringAsync(uri);

var versions = JsonSerializer.Deserialize<NuGetVersions>(json)!.versions;
Console.WriteLine($"Found versions:");
foreach (var version in versions)
    Console.WriteLine($"  {version}");

string projectPath = Path.Combine("AsyncLockCore", "AsyncLockCore.csproj");
XmlDocument doc = new();
doc.Load(projectPath);

var versionNode = doc.SelectSingleNode("//Version");
if (versionNode == null)
{
    Console.WriteLine("Version node not found in project file");
    return;
}

string currentVersion = versionNode.InnerText;
Console.WriteLine($"Current version: {currentVersion}");

var versionParts = currentVersion.Split('.');
string major = versionParts[0];
string minor = versionParts[1];

Console.WriteLine($"Major: {major}, Minor: {minor}");

var latestPach = versions.Where(v => v.StartsWith($"{major}.{minor}."))
                         .Select(v => 
                         {
                             var parts = v.Split('.');
                             return parts.Length >= 3 && int.TryParse(parts[2], out int patch) ? patch : 0;
                         })
                         .Max();
int newPatch = latestPach + 1;
string newVersion = $"{major}.{minor}.{newPatch}";

Console.WriteLine($"Latest patch on NuGet: {latestPach}");
Console.WriteLine($"New version: {newVersion}");

record NuGetVersions(string[] versions);