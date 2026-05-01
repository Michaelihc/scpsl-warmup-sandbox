using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: NavTemplateExporter <AssetRipper ExportedProject path> [output path]");
    return 2;
}

var projectRoot = Path.GetFullPath(args[0]);
var outputRoot = args.Length >= 2
    ? Path.GetFullPath(args[1])
    : Path.Combine(Environment.CurrentDirectory, "nav-templates");

var assetsRoot = Path.Combine(projectRoot, "Assets");
var prefabRoot = Path.Combine(assetsRoot, "GameObject");
if (!Directory.Exists(prefabRoot))
{
    Console.Error.WriteLine($"Missing prefab directory: {prefabRoot}");
    return 2;
}

Directory.CreateDirectory(outputRoot);

var metaMap = BuildGuidMap(assetsRoot);
var prefabs = Directory.EnumerateFiles(prefabRoot, "*.prefab", SearchOption.TopDirectoryOnly)
    .Where(IsRoomPrefab)
    .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
    .ToArray();

var meshCache = new Dictionary<string, MeshAsset>(StringComparer.OrdinalIgnoreCase);
var totals = new ExportTotals();

foreach (var prefabPath in prefabs)
{
    var prefab = PrefabAsset.Load(prefabPath);
    var template = ExportPrefab(prefab, metaMap, meshCache);
    if (template.Boxes.Count == 0)
    {
        Console.WriteLine($"skip {template.Name}: no floor cells found");
        totals.Skipped++;
        continue;
    }

    var outPath = Path.Combine(outputRoot, template.Name + ".json");
    WriteTemplate(template, outPath);

    totals.Exported++;
    totals.Boxes += template.Boxes.Count;
    totals.MeshColliders += template.MeshColliders;
    totals.BoxColliders += template.BoxColliders;
    totals.UnresolvedMeshes += template.UnresolvedMeshes;
    Console.WriteLine($"wrote {template.Name}: boxes={template.Boxes.Count}, meshColliders={template.MeshColliders}, boxColliders={template.BoxColliders}, unresolvedMeshes={template.UnresolvedMeshes}");
}

Console.WriteLine($"done exported={totals.Exported}, skipped={totals.Skipped}, boxes={totals.Boxes}, meshColliders={totals.MeshColliders}, boxColliders={totals.BoxColliders}, unresolvedMeshes={totals.UnresolvedMeshes}");
return 0;

static bool IsRoomPrefab(string path)
{
    var name = Path.GetFileNameWithoutExtension(path);
    if (name.Contains("BreakableGlass", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Role", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Role ", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    return name.StartsWith("LCZ_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("HCZ_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("EZ_", StringComparison.OrdinalIgnoreCase);
}

static Dictionary<string, string> BuildGuidMap(string assetsRoot)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var metaPath in Directory.EnumerateFiles(assetsRoot, "*.meta", SearchOption.AllDirectories))
    {
        var guid = File.ReadLines(metaPath)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("guid: ", StringComparison.Ordinal));
        if (guid == null)
        {
            continue;
        }

        var assetPath = metaPath[..^5];
        result[guid["guid: ".Length..].Trim()] = assetPath;
    }

    return result;
}

static NavTemplate ExportPrefab(PrefabAsset prefab, Dictionary<string, string> metaMap, Dictionary<string, MeshAsset> meshCache)
{
    const float cellSize = 1.5f;
    const float cellHeight = 0.12f;
    var template = new NavTemplate(prefab.Name, cellSize);
    var cells = new Dictionary<CellKey, NavBox>();

    foreach (var collider in prefab.MeshColliders)
    {
        if (collider.IsTrigger || string.IsNullOrWhiteSpace(collider.MeshGuid) || collider.MeshGuid.All(c => c == '0'))
        {
            continue;
        }

        if (!metaMap.TryGetValue(collider.MeshGuid, out var meshPath) || !File.Exists(meshPath))
        {
            template.UnresolvedMeshes++;
            continue;
        }

        if (!meshCache.TryGetValue(meshPath, out var mesh))
        {
            mesh = MeshAsset.Load(meshPath);
            meshCache[meshPath] = mesh;
        }

        if (mesh.Vertices.Count == 0 || mesh.Indices.Count < 3)
        {
            continue;
        }

        if (!prefab.TryGetGameObjectMatrix(collider.GameObjectId, out var matrix))
        {
            continue;
        }

        template.MeshColliders++;
        AddMeshFloorCells(mesh, matrix, cellSize, cellHeight, cells);
    }

    foreach (var collider in prefab.BoxColliders)
    {
        if (collider.IsTrigger || !prefab.TryGetGameObjectMatrix(collider.GameObjectId, out var matrix))
        {
            continue;
        }

        template.BoxColliders++;
        AddBoxFloorCells(collider, matrix, cellSize, cellHeight, cells);
    }

    template.Boxes.AddRange(cells.Values.OrderBy(b => b.Center.Y).ThenBy(b => b.Center.X).ThenBy(b => b.Center.Z));
    return template;
}

static void AddMeshFloorCells(MeshAsset mesh, Matrix4x4 matrix, float cellSize, float cellHeight, Dictionary<CellKey, NavBox> cells)
{
    var transformedBounds = Bounds.Empty;
    foreach (var vertex in mesh.Vertices)
    {
        transformedBounds.Include(Vector3.Transform(vertex, matrix));
    }

    if (!transformedBounds.IsValid)
    {
        return;
    }

    var maxFloorY = transformedBounds.Min.Y + MathF.Max(1.2f, MathF.Min(3.0f, transformedBounds.Size.Y * 0.35f));

    for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
    {
        var a = Vector3.Transform(mesh.Vertices[(int)mesh.Indices[i]], matrix);
        var b = Vector3.Transform(mesh.Vertices[(int)mesh.Indices[i + 1]], matrix);
        var c = Vector3.Transform(mesh.Vertices[(int)mesh.Indices[i + 2]], matrix);
        var normal = Vector3.Cross(b - a, c - a);
        var area2 = normal.Length();
        if (area2 < 0.03f)
        {
            continue;
        }

        normal /= area2;
        var centroid = (a + b + c) / 3.0f;
        if (MathF.Abs(normal.Y) < 0.55f || centroid.Y > maxFloorY)
        {
            continue;
        }

        AddTriangleCells(cells, a, b, c, centroid, cellSize, cellHeight);
    }
}

static void AddTriangleCells(Dictionary<CellKey, NavBox> cells, Vector3 a, Vector3 b, Vector3 c, Vector3 fallback, float cellSize, float cellHeight)
{
    var minX = MathF.Min(a.X, MathF.Min(b.X, c.X));
    var maxX = MathF.Max(a.X, MathF.Max(b.X, c.X));
    var minZ = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
    var maxZ = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
    var minIx = (int)MathF.Floor(minX / cellSize);
    var maxIx = (int)MathF.Ceiling(maxX / cellSize);
    var minIz = (int)MathF.Floor(minZ / cellSize);
    var maxIz = (int)MathF.Ceiling(maxZ / cellSize);
    var added = false;

    for (var ix = minIx; ix <= maxIx; ix++)
    {
        for (var iz = minIz; iz <= maxIz; iz++)
        {
            var x = ix * cellSize;
            var z = iz * cellSize;
            if (!TryBarycentricXZ(a, b, c, x, z, out var u, out var v, out var w))
            {
                continue;
            }

            AddCell(cells, new Vector3(x, a.Y * u + b.Y * v + c.Y * w, z), cellSize, cellHeight);
            added = true;
        }
    }

    if (!added)
    {
        AddCell(cells, fallback, cellSize, cellHeight);
    }
}

static bool TryBarycentricXZ(Vector3 a, Vector3 b, Vector3 c, float x, float z, out float u, out float v, out float w)
{
    var v0x = b.X - a.X;
    var v0z = b.Z - a.Z;
    var v1x = c.X - a.X;
    var v1z = c.Z - a.Z;
    var v2x = x - a.X;
    var v2z = z - a.Z;
    var den = v0x * v1z - v1x * v0z;
    if (MathF.Abs(den) < 0.0001f)
    {
        u = v = w = 0;
        return false;
    }

    v = (v2x * v1z - v1x * v2z) / den;
    w = (v0x * v2z - v2x * v0z) / den;
    u = 1.0f - v - w;
    const float epsilon = -0.02f;
    return u >= epsilon && v >= epsilon && w >= epsilon;
}

static void AddBoxFloorCells(BoxColliderAsset collider, Matrix4x4 matrix, float cellSize, float cellHeight, Dictionary<CellKey, NavBox> cells)
{
    var scaledSize = collider.Size;
    if (scaledSize.X < 0.5f || scaledSize.Z < 0.5f)
    {
        return;
    }

    var localTop = collider.Center + new Vector3(0, collider.Size.Y * 0.5f, 0);
    var center = Vector3.Transform(localTop, matrix);
    var countX = Math.Max(1, (int)MathF.Ceiling(collider.Size.X / cellSize));
    var countZ = Math.Max(1, (int)MathF.Ceiling(collider.Size.Z / cellSize));

    for (var x = 0; x < countX; x++)
    {
        for (var z = 0; z < countZ; z++)
        {
            var offset = new Vector3(
                (x + 0.5f) / countX * collider.Size.X - collider.Size.X * 0.5f,
                0,
                (z + 0.5f) / countZ * collider.Size.Z - collider.Size.Z * 0.5f);
            AddCell(cells, Vector3.Transform(localTop + offset, matrix), cellSize, cellHeight);
        }
    }
}

static void AddCell(Dictionary<CellKey, NavBox> cells, Vector3 point, float cellSize, float cellHeight)
{
    var key = new CellKey(
        (int)MathF.Round(point.X / cellSize),
        (int)MathF.Round(point.Y / 0.25f),
        (int)MathF.Round(point.Z / cellSize));

    if (cells.ContainsKey(key))
    {
        return;
    }

    var center = new Vector3(key.X * cellSize, key.Y * 0.25f - cellHeight * 0.5f, key.Z * cellSize);
    cells[key] = new NavBox(center, new Vector3(cellSize, cellHeight, cellSize));
}

static void WriteTemplate(NavTemplate template, string path)
{
    using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
    writer.WriteLine("{");
    writer.WriteLine($"  \"name\": \"{JsonEscape(template.Name)}\",");
    writer.WriteLine($"  \"cellSize\": {template.CellSize:0.###},");
    writer.WriteLine($"  \"boxes\": [");
    for (var i = 0; i < template.Boxes.Count; i++)
    {
        var box = template.Boxes[i];
        writer.Write("    { ");
        writer.Write($"\"center\": {{ \"x\": {box.Center.X:0.###}, \"y\": {box.Center.Y:0.###}, \"z\": {box.Center.Z:0.###} }}, ");
        writer.Write($"\"size\": {{ \"x\": {box.Size.X:0.###}, \"y\": {box.Size.Y:0.###}, \"z\": {box.Size.Z:0.###} }} ");
        writer.Write(i == template.Boxes.Count - 1 ? "}" : "},");
        writer.WriteLine();
    }
    writer.WriteLine("  ]");
    writer.WriteLine("}");
}

static string JsonEscape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

sealed class PrefabAsset
{
    private readonly Dictionary<long, GameObjectAsset> _gameObjects;
    private readonly Dictionary<long, TransformAsset> _transformsById;
    private readonly Dictionary<long, TransformAsset> _transformsByGameObject;
    private readonly Dictionary<long, Matrix4x4> _matrixCache = new();

    private PrefabAsset(
        string name,
        Dictionary<long, GameObjectAsset> gameObjects,
        Dictionary<long, TransformAsset> transformsById,
        List<MeshColliderAsset> meshColliders,
        List<BoxColliderAsset> boxColliders)
    {
        Name = name;
        _gameObjects = gameObjects;
        _transformsById = transformsById;
        MeshColliders = meshColliders;
        BoxColliders = boxColliders;
        _transformsByGameObject = transformsById.Values.ToDictionary(t => t.GameObjectId, t => t);
    }

    public string Name { get; }
    public IReadOnlyList<MeshColliderAsset> MeshColliders { get; }
    public IReadOnlyList<BoxColliderAsset> BoxColliders { get; }

    public bool TryGetGameObjectMatrix(long gameObjectId, out Matrix4x4 matrix)
    {
        if (!_transformsByGameObject.TryGetValue(gameObjectId, out var transform))
        {
            matrix = Matrix4x4.Identity;
            return false;
        }

        matrix = GetMatrix(transform.FileId);
        return true;
    }

    private Matrix4x4 GetMatrix(long transformId)
    {
        if (_matrixCache.TryGetValue(transformId, out var cached))
        {
            return cached;
        }

        if (!_transformsById.TryGetValue(transformId, out var transform))
        {
            return Matrix4x4.Identity;
        }

        var local = Matrix4x4.CreateScale(transform.Scale)
            * Matrix4x4.CreateFromQuaternion(transform.Rotation)
            * Matrix4x4.CreateTranslation(transform.Position);

        var matrix = transform.ParentTransformId != 0
            ? local * GetMatrix(transform.ParentTransformId)
            : local;

        _matrixCache[transformId] = matrix;
        return matrix;
    }

    public static PrefabAsset Load(string path)
    {
        var gameObjects = new Dictionary<long, GameObjectAsset>();
        var transforms = new Dictionary<long, TransformAsset>();
        var meshColliders = new List<MeshColliderAsset>();
        var boxColliders = new List<BoxColliderAsset>();

        foreach (var doc in UnityYaml.ReadDocuments(path))
        {
            switch (doc.TypeId)
            {
                case 1:
                    gameObjects[doc.FileId] = new GameObjectAsset(doc.FileId, doc.StringValue("m_Name") ?? "");
                    break;
                case 4:
                    transforms[doc.FileId] = new TransformAsset(
                        doc.FileId,
                        doc.FileIdRef("m_GameObject"),
                        doc.FileIdRef("m_Father"),
                        doc.VectorValue("m_LocalPosition", Vector3.Zero),
                        doc.QuaternionValue("m_LocalRotation", Quaternion.Identity),
                        doc.VectorValue("m_LocalScale", Vector3.One));
                    break;
                case 64:
                    meshColliders.Add(new MeshColliderAsset(
                        doc.FileIdRef("m_GameObject"),
                        doc.IntValue("m_IsTrigger") != 0,
                        doc.GuidRef("m_Mesh")));
                    break;
                case 65:
                    boxColliders.Add(new BoxColliderAsset(
                        doc.FileIdRef("m_GameObject"),
                        doc.IntValue("m_IsTrigger") != 0,
                        doc.VectorValue("m_Center", Vector3.Zero),
                        doc.VectorValue("m_Size", Vector3.Zero)));
                    break;
            }
        }

        var rootName = gameObjects.Values.Where(g => g.Name.StartsWith("LCZ_", StringComparison.OrdinalIgnoreCase)
            || g.Name.StartsWith("HCZ_", StringComparison.OrdinalIgnoreCase)
            || g.Name.StartsWith("EZ_", StringComparison.OrdinalIgnoreCase))
            .Select(g => g.Name)
            .FirstOrDefault();

        return new PrefabAsset(rootName ?? Path.GetFileNameWithoutExtension(path), gameObjects, transforms, meshColliders, boxColliders);
    }
}

sealed class MeshAsset
{
    private MeshAsset(string name, List<Vector3> vertices, List<uint> indices)
    {
        Name = name;
        Vertices = vertices;
        Indices = indices;
    }

    public string Name { get; }
    public IReadOnlyList<Vector3> Vertices { get; }
    public IReadOnlyList<uint> Indices { get; }

    public static MeshAsset Load(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var indexFormat = 0;
        var vertexCount = 0;
        var dataSize = 0;
        var positionOffset = 0;
        var firstChannel = false;
        string? indexHex = null;
        string? vertexHex = null;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith("m_Name: ", StringComparison.Ordinal))
            {
                name = line["m_Name: ".Length..].Trim();
            }
            else if (line.StartsWith("m_IndexFormat: ", StringComparison.Ordinal))
            {
                indexFormat = YamlUtil.ParseInt(line["m_IndexFormat: ".Length..]);
            }
            else if (line.StartsWith("m_IndexBuffer: ", StringComparison.Ordinal))
            {
                indexHex = line["m_IndexBuffer: ".Length..].Trim();
            }
            else if (line.StartsWith("m_VertexCount: ", StringComparison.Ordinal))
            {
                vertexCount = YamlUtil.ParseInt(line["m_VertexCount: ".Length..]);
            }
            else if (line.StartsWith("m_DataSize: ", StringComparison.Ordinal))
            {
                dataSize = YamlUtil.ParseInt(line["m_DataSize: ".Length..]);
            }
            else if (line.StartsWith("- stream: ", StringComparison.Ordinal) && !firstChannel)
            {
                firstChannel = true;
            }
            else if (firstChannel && line.StartsWith("offset: ", StringComparison.Ordinal))
            {
                positionOffset = YamlUtil.ParseInt(line["offset: ".Length..]);
                firstChannel = false;
            }
            else if (line.StartsWith("_typelessdata: ", StringComparison.Ordinal))
            {
                vertexHex = line["_typelessdata: ".Length..].Trim();
            }
        }

        if (vertexCount <= 0 || dataSize <= 0 || string.IsNullOrWhiteSpace(vertexHex))
        {
            return new MeshAsset(name, new List<Vector3>(), new List<uint>());
        }

        var vertexBytes = YamlUtil.HexToBytes(vertexHex);
        var stride = Math.Max(12, dataSize / vertexCount);
        var vertices = new List<Vector3>(vertexCount);
        for (var i = 0; i < vertexCount; i++)
        {
            var offset = i * stride + positionOffset;
            if (offset + 12 > vertexBytes.Length)
            {
                break;
            }

            vertices.Add(new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(vertexBytes.AsSpan(offset, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(vertexBytes.AsSpan(offset + 4, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(vertexBytes.AsSpan(offset + 8, 4))));
        }

        var indices = new List<uint>();
        if (!string.IsNullOrWhiteSpace(indexHex))
        {
            var indexBytes = YamlUtil.HexToBytes(indexHex);
            if (indexFormat == 1)
            {
                for (var i = 0; i + 4 <= indexBytes.Length; i += 4)
                {
                    indices.Add(BinaryPrimitives.ReadUInt32LittleEndian(indexBytes.AsSpan(i, 4)));
                }
            }
            else
            {
                for (var i = 0; i + 2 <= indexBytes.Length; i += 2)
                {
                    indices.Add(BinaryPrimitives.ReadUInt16LittleEndian(indexBytes.AsSpan(i, 2)));
                }
            }
        }

        return new MeshAsset(name, vertices, indices.Where(i => i < vertices.Count).ToList());
    }
}

sealed class UnityYamlDocument
{
    private static readonly Regex HeaderRegex = new(@"^--- !u!(\d+) &(-?\d+)", RegexOptions.Compiled);
    private readonly IReadOnlyList<string> _lines;

    public UnityYamlDocument(int typeId, long fileId, IReadOnlyList<string> lines)
    {
        TypeId = typeId;
        FileId = fileId;
        _lines = lines;
    }

    public int TypeId { get; }
    public long FileId { get; }

    public string? StringValue(string key)
    {
        var prefix = key + ": ";
        foreach (var line in _lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return trimmed[prefix.Length..].Trim();
            }
        }

        return null;
    }

    public int IntValue(string key)
    {
        var value = StringValue(key);
        return value == null ? 0 : YamlUtil.ParseInt(value);
    }

    public long FileIdRef(string key)
    {
        var prefix = key + ": ";
        foreach (var line in _lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var match = Regex.Match(trimmed, @"fileID:\s*(-?\d+)");
            return match.Success ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        return 0;
    }

    public string GuidRef(string key)
    {
        var prefix = key + ": ";
        foreach (var line in _lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var match = Regex.Match(trimmed, @"guid:\s*([0-9a-fA-F]+)");
            return match.Success ? match.Groups[1].Value : "";
        }

        return "";
    }

    public Vector3 VectorValue(string key, Vector3 fallback)
    {
        var value = StringValue(key);
        if (value == null)
        {
            return fallback;
        }

        return new Vector3(YamlUtil.FloatField(value, "x", fallback.X), YamlUtil.FloatField(value, "y", fallback.Y), YamlUtil.FloatField(value, "z", fallback.Z));
    }

    public Quaternion QuaternionValue(string key, Quaternion fallback)
    {
        var value = StringValue(key);
        if (value == null)
        {
            return fallback;
        }

        return new Quaternion(YamlUtil.FloatField(value, "x", fallback.X), YamlUtil.FloatField(value, "y", fallback.Y), YamlUtil.FloatField(value, "z", fallback.Z), YamlUtil.FloatField(value, "w", fallback.W));
    }

    public static bool TryParseHeader(string line, out int typeId, out long fileId)
    {
        var match = HeaderRegex.Match(line);
        if (!match.Success)
        {
            typeId = 0;
            fileId = 0;
            return false;
        }

        typeId = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        fileId = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }
}

static class UnityYaml
{
    public static IEnumerable<UnityYamlDocument> ReadDocuments(string path)
    {
        var typeId = 0;
        long fileId = 0;
        var lines = new List<string>();
        var hasDoc = false;

        foreach (var line in File.ReadLines(path))
        {
            if (UnityYamlDocument.TryParseHeader(line, out var nextType, out var nextFileId))
            {
                if (hasDoc)
                {
                    yield return new UnityYamlDocument(typeId, fileId, lines);
                    lines = new List<string>();
                }

                typeId = nextType;
                fileId = nextFileId;
                hasDoc = true;
                continue;
            }

            if (hasDoc)
            {
                lines.Add(line);
            }
        }

        if (hasDoc)
        {
            yield return new UnityYamlDocument(typeId, fileId, lines);
        }
    }
}

static class YamlUtil
{
    public static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    public static int ParseInt(string value) => int.Parse(value.Trim(), CultureInfo.InvariantCulture);

    public static float FloatField(string value, string field, float fallback)
    {
        var match = Regex.Match(value, field + @":\s*([-+0-9.Ee]+)");
        return match.Success ? float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : fallback;
    }
}

readonly record struct GameObjectAsset(long FileId, string Name);
readonly record struct TransformAsset(long FileId, long GameObjectId, long ParentTransformId, Vector3 Position, Quaternion Rotation, Vector3 Scale);
readonly record struct MeshColliderAsset(long GameObjectId, bool IsTrigger, string MeshGuid);
readonly record struct BoxColliderAsset(long GameObjectId, bool IsTrigger, Vector3 Center, Vector3 Size);
readonly record struct CellKey(int X, int Y, int Z);
readonly record struct NavBox(Vector3 Center, Vector3 Size);

sealed class NavTemplate
{
    public NavTemplate(string name, float cellSize)
    {
        Name = name;
        CellSize = cellSize;
    }

    public string Name { get; }
    public float CellSize { get; }
    public List<NavBox> Boxes { get; } = new();
    public int MeshColliders { get; set; }
    public int BoxColliders { get; set; }
    public int UnresolvedMeshes { get; set; }
}

sealed class ExportTotals
{
    public int Exported { get; set; }
    public int Skipped { get; set; }
    public int Boxes { get; set; }
    public int MeshColliders { get; set; }
    public int BoxColliders { get; set; }
    public int UnresolvedMeshes { get; set; }
}

struct Bounds
{
    public Vector3 Min;
    public Vector3 Max;
    public bool IsValid;

    public Vector3 Size => Max - Min;
    public static Bounds Empty => new() { Min = new Vector3(float.PositiveInfinity), Max = new Vector3(float.NegativeInfinity), IsValid = false };

    public void Include(Vector3 point)
    {
        if (!IsValid)
        {
            Min = point;
            Max = point;
            IsValid = true;
            return;
        }

        Min = Vector3.Min(Min, point);
        Max = Vector3.Max(Max, point);
    }
}
