using System.Text.Json;
using System.Text.Json.Serialization;
using ValveResourceFormat;
using ValveResourceFormat.IO;

// Usage:
//   dotnet run -- kvien/old_table01 --out out --format glb
//
// Downloads:
//   https://services.facepunch.com/sbox/package/get/{author}.{asset}
// then downloads Version.ManifestUrl and all manifest Files[].url.
// Finally converts the primary .vmdl_c to glTF via ValveResourceFormat.

var argsList = args.ToList();
if (argsList.Count == 0 || argsList.Contains("-h") || argsList.Contains("--help"))
{
    Console.WriteLine("Usage: Sbox2Gltf <author/asset> [--out <dir>] [--format glb|gltf]");
    return;
}

var id = argsList[0];
var outDir = GetArg(argsList, "--out") ?? "out";
var format = (GetArg(argsList, "--format") ?? "glb").ToLowerInvariant();

if (!id.Contains('/')) throw new ArgumentException("Expected <author/asset>");
var author = id.Split('/')[0];
var asset = id.Split('/')[1];
var packageKey = $"{author}.{asset}";

Directory.CreateDirectory(outDir);
var packageRoot = Path.Combine(outDir, packageKey);
Directory.CreateDirectory(packageRoot);

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("Sbox2Gltf/0.1 (+https://github.com/<you>/sbox2gltf)");

var packageUrl = $"https://services.facepunch.com/sbox/package/get/{packageKey}";
Console.WriteLine($"Fetching package: {packageUrl}");
var packageJson = await http.GetStringAsync(packageUrl);

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
};

var package = JsonSerializer.Deserialize<PackageResponse>(packageJson, jsonOptions)
    ?? throw new Exception("Failed to parse package response JSON");

var manifestUrl = package.Version?.ManifestUrl
    ?? throw new Exception("Package JSON missing Version.ManifestUrl");

Console.WriteLine($"Fetching manifest: {manifestUrl}");
var manifestJson = await http.GetStringAsync(manifestUrl);
var manifest = JsonSerializer.Deserialize<ManifestResponse>(manifestJson, jsonOptions)
    ?? throw new Exception("Failed to parse manifest JSON");

var files = manifest.Files ?? [];
Console.WriteLine($"Manifest files: {files.Length} (total bytes: {manifest.TotalSize})");

// Download all manifest files to packageRoot/<path>
var downloader = new Downloader(http);
await downloader.DownloadAllAsync(files.Select(f => (f.Url!, Path.Combine(packageRoot, f.Path!))).ToArray());

// Identify primary model.
// Prefer Version.Meta.PrimaryAsset (points to e.g. models/foo.vmdl); compiled is *.vmdl_c.
string? primaryVmdl = null;
try
{
    if (!string.IsNullOrWhiteSpace(package.Version?.Meta))
    {
        using var metaDoc = JsonDocument.Parse(package.Version!.Meta!);
        if (metaDoc.RootElement.TryGetProperty("PrimaryAsset", out var primaryAssetEl))
        {
            primaryVmdl = primaryAssetEl.GetString();
        }
    }
}
catch
{
    // Meta is best-effort; fall back to scanning manifest.
}

var vmdlCRelative =
    primaryVmdl != null
        ? primaryVmdl + "_c" // .vmdl -> .vmdl_c
        : files.Select(f => f.Path).FirstOrDefault(p => p != null && p.EndsWith(".vmdl_c", StringComparison.OrdinalIgnoreCase));

if (vmdlCRelative == null)
{
    throw new Exception("Could not find .vmdl_c in manifest");
}

var vmdlCPath = Path.Combine(packageRoot, vmdlCRelative.Replace('/', Path.DirectorySeparatorChar));
if (!File.Exists(vmdlCPath))
{
    throw new FileNotFoundException($"Primary model not downloaded: {vmdlCPath}");
}

var outExt = format == "gltf" ? ".gltf" : ".glb";
var outPath = Path.Combine(packageRoot, $"{packageKey}{outExt}");

Console.WriteLine($"Converting: {vmdlCRelative} -> {Path.GetFileName(outPath)}");

using var resource = new Resource();
resource.Read(vmdlCPath);

var fileLoader = new LooseFileLoader(packageRoot);
var exporter = new GltfModelExporter(fileLoader)
{
    ProgressReporter = new Progress<string>(msg => Console.WriteLine($"[vrf] {msg}")),
    ExportMaterials = true,
    ExportAnimations = true,
    AdaptTextures = true,
    SatelliteImages = true,
};

exporter.Export(resource, outPath);

Console.WriteLine("Done.");

static string? GetArg(List<string> args, string name)
{
    var i = args.IndexOf(name);
    if (i < 0) return null;
    if (i + 1 >= args.Count) throw new ArgumentException($"Missing value for {name}");
    return args[i + 1];
}

public sealed class Downloader(HttpClient http)
{
    private readonly HttpClient http = http;

    public async Task DownloadAllAsync((string url, string path)[] items, int maxConcurrency = 8)
    {
        using var sem = new SemaphoreSlim(maxConcurrency);
        var tasks = items.Select(async item =>
        {
            await sem.WaitAsync();
            try { await DownloadOneAsync(item.url, item.path); }
            finally { sem.Release(); }
        });

        await Task.WhenAll(tasks);
    }

    private async Task DownloadOneAsync(string url, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) return;

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(path);
        await resp.Content.CopyToAsync(fs);
        Console.WriteLine($"Downloaded: {url} -> {path}");
    }
}

public sealed class LooseFileLoader(string root) : IFileLoader
{
    private readonly string root = root;

    public Resource? LoadFile(string file)
    {
        // VRF callers may pass paths with forward slashes.
        var rel = file.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var full = Path.Combine(root, rel);
        if (!File.Exists(full)) return null;

        var res = new Resource();
        res.Read(full);
        return res;
    }

    public Resource? LoadFileCompiled(string file)
    {
        if (file.EndsWith("_c", StringComparison.OrdinalIgnoreCase))
        {
            return LoadFile(file);
        }
        return LoadFile(file + "_c");
    }

    public ValveResourceFormat.CompiledShader.ShaderCollection? LoadShader(string shaderName) => null;
}

public sealed class PackageResponse
{
    public OrgInfo? Org { get; set; }
    public string? Ident { get; set; }
    public string? Title { get; set; }
    public PackageVersion? Version { get; set; }
}

public sealed class OrgInfo
{
    public string? Ident { get; set; }
    public string? Title { get; set; }
}

public sealed class PackageVersion
{
    public int Id { get; set; }
    public string? ManifestUrl { get; set; }
    public string? Meta { get; set; }
}

public sealed class ManifestResponse
{
    public int Schema { get; set; }
    public int Asset { get; set; }
    public ManifestFile[]? Files { get; set; }
    public long TotalSize { get; set; }
}

public sealed class ManifestFile
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("crc")] public string? Crc { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}
