#:package NuGet.Versioning

using System.Text.Json;
using System.Xml;
using NuGet.Versioning;

const string projectName = "AsyncLockCore";

XmlDocument doc = new();
doc.Load(Path.Combine(projectName, $"{projectName}.csproj"));
var baseVersion = SemanticVersion.Parse(doc.SelectSingleNode("//Version")!.InnerText);

using HttpClient client = new();
var versionsJson = await client.GetStringAsync($"https://api.nuget.org/v3-flatcontainer/{projectName.ToLowerInvariant()}/index.json");
var versions = JsonSerializer.Deserialize<NuGetVersions>(versionsJson)!.versions.Select(v => SemanticVersion.Parse(v));

int[] patches = [.. from v in versions where v.Major == baseVersion.Major && v.Minor == baseVersion.Minor select v.Patch];
var newPatch = patches.Any() ? patches.Max() + 1 : 0;
var newVersion = new SemanticVersion(baseVersion.Major, baseVersion.Minor, newPatch);

Console.WriteLine($"New version: {newVersion}");

record NuGetVersions(string[] versions);