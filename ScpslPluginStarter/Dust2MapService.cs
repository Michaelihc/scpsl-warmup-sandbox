using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using LabApi.Features.Wrappers;
using Mirror;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace ScpslPluginStarter;

internal sealed class Dust2MapService
{
    private readonly Dictionary<string, List<Transform>> _markers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Component>> _adminToyComponents = new(StringComparer.OrdinalIgnoreCase);
    private LoadedSchematicMap? _loadedMap;
    private NavMeshDataInstance _runtimeNavMeshInstance;
    private Bounds _runtimeNavMeshBounds;
    private bool _hasRuntimeNavMesh;

    public bool IsLoaded => _loadedMap != null;

    public bool HasRuntimeNavMesh => _hasRuntimeNavMesh;

    public string BuildStatus(Dust2MapConfig config, bool forceLoad = false)
    {
        string schematicPath = GetInstalledSchematicPath(config.SchematicName);
        bool schematicPresent = Directory.Exists(schematicPath);
        return $"enabled={config.Enabled}, forceLoad={forceLoad}, loaded={IsLoaded}, runtimeNavMesh={_hasRuntimeNavMesh}, schematic={config.SchematicName}, installed={schematicPresent}, path={schematicPath}";
    }

    public bool TryLoad(Dust2MapConfig config, out string response, bool forceLoad = false)
    {
        Unload();

        if (!config.Enabled && !forceLoad)
        {
            response = "Dust2 map is disabled.";
            return true;
        }

        string schematicPath = GetInstalledSchematicPath(config.SchematicName);
        if (!Directory.Exists(schematicPath))
        {
            response = $"Schematic '{config.SchematicName}' was not found at '{schematicPath}'. Build the plugin to deploy the copied Dust2 files or install them into ProjectMER's Schematics folder.";
            return false;
        }

        if (!TrySpawnSchematic(
                config.SchematicName,
                config.Origin.ToVector3(),
                Quaternion.Euler(config.Rotation.ToVector3()),
                config.Scale.ToVector3(),
                out LoadedSchematicMap? loadedMap,
                out response))
        {
            return false;
        }

        LoadedSchematicMap concreteMap = loadedMap!;
        _loadedMap = concreteMap;
        IndexMarkers(concreteMap);
        if (config.RemoveWarmupWalls)
        {
            DestroyNamedObjects("Wall");
            DestroyNamedObjects("Walls");
        }

        response = forceLoad && !config.Enabled
            ? $"Loaded '{config.SchematicName}' with {_markers.Count} named marker groups for bomb mode override."
            : $"Loaded '{config.SchematicName}' with {_markers.Count} named marker groups.";
        return true;
    }

    public void Unload()
    {
        RemoveRuntimeNavMesh();
        _markers.Clear();
        _adminToyComponents.Clear();

        if (_loadedMap?.Root != null)
        {
            NetworkServer.Destroy(_loadedMap.Root);
        }

        _loadedMap = null;
    }

    public bool TryBakeRuntimeNavMesh(Dust2MapConfig config, out string response)
    {
        RemoveRuntimeNavMesh();

        if (!config.RuntimeNavMeshEnabled)
        {
            response = "Runtime NavMesh is disabled.";
            return false;
        }

        if (_loadedMap?.Root == null)
        {
            response = "Runtime NavMesh bake skipped because the schematic is not loaded.";
            return false;
        }

        List<NavMeshBuildSource> sources = new();
        List<NavMeshBuildMarkup> markups = new();
        NavMeshBuilder.CollectSources(
            _loadedMap.Root.transform,
            ~0,
            config.RuntimeNavMeshUseRenderMeshes
                ? NavMeshCollectGeometry.RenderMeshes
                : NavMeshCollectGeometry.PhysicsColliders,
            0,
            markups,
            sources);

        if (sources.Count == 0)
        {
            response = config.RuntimeNavMeshUseRenderMeshes
                ? "Runtime NavMesh bake found no render-mesh sources."
                : "Runtime NavMesh bake found no physics-collider sources.";
            return false;
        }

        Bounds bounds = BuildNavMeshBounds(_loadedMap.Root, config.RuntimeNavMeshBoundsPadding);
        NavMeshBuildSettings settings = NavMeshAgentTypeUtility.CreateDefaultBuildSettings();
        settings.agentRadius = Mathf.Max(0.05f, config.RuntimeNavMeshAgentRadius);
        settings.agentHeight = Mathf.Max(0.5f, config.RuntimeNavMeshAgentHeight);
        settings.agentSlope = Mathf.Clamp(config.RuntimeNavMeshAgentMaxSlope, 0f, 89f);
        settings.agentClimb = Mathf.Max(0f, config.RuntimeNavMeshAgentClimb);
        settings.minRegionArea = Mathf.Max(0f, config.RuntimeNavMeshMinRegionArea);

        NavMeshData data;
        try
        {
            data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
        }
        catch (Exception ex)
        {
            response = $"Runtime NavMesh bake failed: {ex.GetBaseException().Message}";
            return false;
        }

        if (data == null)
        {
            response = "Runtime NavMesh bake returned no NavMeshData.";
            return false;
        }

        _runtimeNavMeshInstance = NavMesh.AddNavMeshData(data);
        _hasRuntimeNavMesh = _runtimeNavMeshInstance.valid;
        _runtimeNavMeshBounds = bounds;
        string sourceKind = config.RuntimeNavMeshUseRenderMeshes ? "render-mesh" : "physics-collider";
        response = _hasRuntimeNavMesh
            ? $"Runtime NavMesh baked with {sources.Count} {sourceKind} sources across bounds center=({bounds.center.x:F1},{bounds.center.y:F1},{bounds.center.z:F1}) size=({bounds.size.x:F1},{bounds.size.y:F1},{bounds.size.z:F1})."
            : "Runtime NavMesh bake completed but the NavMeshData instance was not valid.";
        return _hasRuntimeNavMesh;
    }

    public IReadOnlyList<NavMeshDebugEdge> GetRuntimeNavMeshDebugEdges(int maxEdges)
    {
        if (!_hasRuntimeNavMesh || maxEdges <= 0)
        {
            return Array.Empty<NavMeshDebugEdge>();
        }

        NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();
        Vector3[] vertices = triangulation.vertices ?? Array.Empty<Vector3>();
        int[] indices = triangulation.indices ?? Array.Empty<int>();
        if (vertices.Length == 0 || indices.Length < 3)
        {
            return Array.Empty<NavMeshDebugEdge>();
        }

        List<NavMeshDebugEdge> edges = new(Math.Min(maxEdges, indices.Length));
        HashSet<string> seen = new(StringComparer.Ordinal);
        Bounds filterBounds = _runtimeNavMeshBounds;
        filterBounds.Expand(0.5f);

        int triangleCount = indices.Length / 3;
        int stride = maxEdges > 0 && triangleCount * 3 > maxEdges
            ? Mathf.Max(1, Mathf.CeilToInt((triangleCount * 3) / (float)maxEdges))
            : 1;

        for (int triangle = 0; triangle < triangleCount && edges.Count < maxEdges; triangle += stride)
        {
            int i = triangle * 3;
            AddEdge(vertices, indices[i], indices[i + 1], filterBounds, seen, edges, maxEdges);
            AddEdge(vertices, indices[i + 1], indices[i + 2], filterBounds, seen, edges, maxEdges);
            AddEdge(vertices, indices[i + 2], indices[i], filterBounds, seen, edges, maxEdges);
        }

        return edges;
    }

    public bool TryPlaceHuman(Player player, Dust2MapConfig config, System.Random random)
    {
        if (!TryGetHumanSpawnPosition(config, random, out Vector3 position))
        {
            return false;
        }

        player.Position = position;
        return true;
    }

    public bool TryPlaceBot(Player player, Dust2MapConfig config, System.Random random)
    {
        if (!TryGetBotSpawnPosition(config, random, out Vector3 position))
        {
            return false;
        }

        player.Position = position;
        return true;
    }

    public bool TryGetHumanSpawnPosition(Dust2MapConfig config, System.Random random, out Vector3 position)
    {
        return TryGetSpawnPosition(config.HumanSpawnMarkerNames, config.HumanSpawnJitterRadius, random, out position);
    }

    public bool TryGetBotSpawnPosition(Dust2MapConfig config, System.Random random, out Vector3 position)
    {
        return TryGetSpawnPosition(config.BotSpawnMarkerNames, config.BotSpawnJitterRadius, random, out position);
    }

    public bool TryGetMarkerTransform(string name, out Transform? transform)
    {
        transform = GetMarkersByName(name).FirstOrDefault();
        return transform != null;
    }

    public IEnumerable<Vector3> GetMarkerPositions(IEnumerable<string> names)
    {
        if (names == null)
        {
            return Enumerable.Empty<Vector3>();
        }

        return names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .SelectMany(GetMarkersByName)
            .Where(transform => transform != null)
            .Select(transform => transform.position)
            .ToList();
    }

    public IEnumerable<Vector3> GetMarkerPositionsByPrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return Enumerable.Empty<Vector3>();
        }

        return _markers
            .Where(entry => entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .SelectMany(entry => entry.Value)
            .Where(transform => transform != null)
            .Select(transform => transform.position)
            .ToList();
    }

    public bool TryGetMarkerCentroid(IEnumerable<string> names, out Vector3 position)
    {
        List<Vector3> positions = GetMarkerPositions(names).ToList();
        if (positions.Count == 0)
        {
            position = Vector3.zero;
            return false;
        }

        Vector3 total = Vector3.zero;
        foreach (Vector3 point in positions)
        {
            total += point;
        }

        position = total / positions.Count;
        return true;
    }

    public bool TryGetAdminToyComponent<T>(string name, out T? component)
        where T : Component
    {
        component = GetAdminToyComponents(name).OfType<T>().FirstOrDefault();
        return component != null;
    }

    public IEnumerable<Component> GetAdminToyComponents(string name)
    {
        return _adminToyComponents.TryGetValue(name, out List<Component>? components)
            ? components.Where(component => component != null)
            : Enumerable.Empty<Component>();
    }

    private bool TryGetSpawnPosition(IReadOnlyCollection<string> markerNames, float jitterRadius, System.Random random, out Vector3 position)
    {
        position = Vector3.zero;

        if (_loadedMap == null || markerNames.Count == 0)
        {
            return false;
        }

        List<Transform> spawnPoints = markerNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .SelectMany(GetMarkersByName)
            .Where(transform => transform != null)
            .ToList();

        if (spawnPoints.Count == 0)
        {
            return false;
        }

        Transform spawn = spawnPoints[random.Next(spawnPoints.Count)];
        position = spawn.position + GetRandomHorizontalOffset(random, jitterRadius);
        return true;
    }

    private IEnumerable<Transform> GetMarkersByName(string name)
    {
        return _markers.TryGetValue(name, out List<Transform>? markers)
            ? markers.Where(marker => marker != null)
            : Enumerable.Empty<Transform>();
    }

    private void IndexMarkers(LoadedSchematicMap loadedMap)
    {
        _markers.Clear();
        _adminToyComponents.Clear();

        foreach (GameObject block in loadedMap.AttachedBlocks.Where(block => block != null))
        {
            AddMarker(block.name, block.transform);
        }

        foreach (Component adminToy in loadedMap.AdminToys.Where(component => component != null))
        {
            AddMarker(adminToy.gameObject.name, adminToy.transform);
            AddAdminToy(adminToy.gameObject.name, adminToy);
        }
    }

    private void AddMarker(string name, Transform transform)
    {
        if (string.IsNullOrWhiteSpace(name) || transform == null)
        {
            return;
        }

        if (!_markers.TryGetValue(name, out List<Transform>? transforms))
        {
            transforms = new List<Transform>();
            _markers[name] = transforms;
        }

        transforms.Add(transform);
    }

    private void AddAdminToy(string name, Component component)
    {
        if (string.IsNullOrWhiteSpace(name) || component == null)
        {
            return;
        }

        if (!_adminToyComponents.TryGetValue(name, out List<Component>? components))
        {
            components = new List<Component>();
            _adminToyComponents[name] = components;
        }

        components.Add(component);
    }

    private void DestroyNamedObjects(string objectName)
    {
        List<Transform> transforms = GetMarkersByName(objectName).ToList();
        foreach (Transform transform in transforms)
        {
            if (transform?.gameObject != null)
            {
                Object.Destroy(transform.gameObject);
            }
        }

        _markers.Remove(objectName);
    }

    private void RemoveRuntimeNavMesh()
    {
        if (!_hasRuntimeNavMesh)
        {
            return;
        }

        NavMesh.RemoveNavMeshData(_runtimeNavMeshInstance);
        _runtimeNavMeshInstance = default;
        _runtimeNavMeshBounds = default;
        _hasRuntimeNavMesh = false;
    }

    private static void AddEdge(
        IReadOnlyList<Vector3> vertices,
        int firstIndex,
        int secondIndex,
        Bounds bounds,
        ISet<string> seen,
        ICollection<NavMeshDebugEdge> edges,
        int maxEdges)
    {
        if (edges.Count >= maxEdges
            || firstIndex < 0
            || secondIndex < 0
            || firstIndex >= vertices.Count
            || secondIndex >= vertices.Count)
        {
            return;
        }

        Vector3 first = vertices[firstIndex];
        Vector3 second = vertices[secondIndex];
        if (!bounds.Contains(first) || !bounds.Contains(second))
        {
            return;
        }

        string firstKey = QuantizeEdgePoint(first);
        string secondKey = QuantizeEdgePoint(second);
        string edgeKey = string.CompareOrdinal(firstKey, secondKey) <= 0
            ? $"{firstKey}|{secondKey}"
            : $"{secondKey}|{firstKey}";
        if (!seen.Add(edgeKey))
        {
            return;
        }

        edges.Add(new NavMeshDebugEdge(first, second));
    }

    private static string QuantizeEdgePoint(Vector3 point)
    {
        return $"{Mathf.RoundToInt(point.x * 20f)}:{Mathf.RoundToInt(point.y * 20f)}:{Mathf.RoundToInt(point.z * 20f)}";
    }

    private static Bounds BuildNavMeshBounds(GameObject root, float padding)
    {
        Bounds? bounds = null;
        foreach (Collider collider in root.GetComponentsInChildren<Collider>(includeInactive: true))
        {
            if (collider == null)
            {
                continue;
            }

            bounds = Encapsulate(bounds, collider.bounds);
        }

        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(includeInactive: true))
        {
            if (renderer == null)
            {
                continue;
            }

            bounds = Encapsulate(bounds, renderer.bounds);
        }

        Bounds finalBounds = bounds ?? new Bounds(root.transform.position, new Vector3(120f, 40f, 120f));
        float safePadding = Mathf.Max(0f, padding);
        finalBounds.Expand(new Vector3(safePadding * 2f, safePadding, safePadding * 2f));
        return finalBounds;
    }

    private static Bounds Encapsulate(Bounds? current, Bounds next)
    {
        if (current == null)
        {
            return next;
        }

        Bounds bounds = current.Value;
        bounds.Encapsulate(next);
        return bounds;
    }

    private static Vector3 GetRandomHorizontalOffset(System.Random random, float radius)
    {
        if (radius <= 0f)
        {
            return Vector3.zero;
        }

        float angle = (float)(random.NextDouble() * Math.PI * 2d);
        float distance = (float)(random.NextDouble() * radius);
        return new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance);
    }

    private static bool TrySpawnSchematic(
        string schematicName,
        Vector3 position,
        Quaternion rotation,
        Vector3 scale,
        out LoadedSchematicMap? loadedMap,
        out string response)
    {
        loadedMap = null;

        Type? objectSpawnerType = Type.GetType("ProjectMER.Features.ObjectSpawner, ProjectMER");
        if (objectSpawnerType == null)
        {
            response = "ProjectMER is not installed or has not been loaded yet. Dust2 can be enabled after ProjectMER is available.";
            return false;
        }

        MethodInfo? trySpawnMethod = objectSpawnerType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method =>
            {
                if (method.Name != "TrySpawnSchematic")
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 5
                    && parameters[0].ParameterType == typeof(string)
                    && parameters[1].ParameterType == typeof(Vector3)
                    && parameters[2].ParameterType == typeof(Quaternion)
                    && parameters[3].ParameterType == typeof(Vector3)
                    && parameters[4].IsOut;
            });

        if (trySpawnMethod == null)
        {
            response = "ProjectMER was found, but its schematic spawn API could not be located.";
            return false;
        }

        object?[] arguments = { schematicName, position, rotation, scale, null };
        bool spawned;
        try
        {
            spawned = (bool)(trySpawnMethod.Invoke(null, arguments) ?? false);
        }
        catch (Exception ex)
        {
            response = $"ProjectMER threw while loading '{schematicName}': {ex.GetBaseException().Message}";
            return false;
        }

        if (!spawned || arguments[4] == null)
        {
            response = $"ProjectMER could not spawn schematic '{schematicName}'.";
            return false;
        }

        object schematicObject = arguments[4]!;
        try
        {
            loadedMap = BuildLoadedMap(schematicName, schematicObject);
            response = $"Loaded '{schematicName}'.";
            return true;
        }
        catch (Exception ex)
        {
            response = $"Schematic '{schematicName}' spawned, but its contents could not be inspected: {ex.GetBaseException().Message}";
            return false;
        }
    }

    private static LoadedSchematicMap BuildLoadedMap(string schematicName, object schematicObject)
    {
        GameObject? root = TryGetGameObject(schematicObject);
        if (root == null)
        {
            throw new InvalidOperationException("Spawned schematic root GameObject was null.");
        }

        List<GameObject> attachedBlocks = ReadMemberValues(schematicObject, "AttachedBlocks")
            .Select(TryGetGameObject)
            .Where(gameObject => gameObject != null)
            .Cast<GameObject>()
            .ToList();

        List<Component> adminToys = ReadMemberValues(schematicObject, "AdminToyBases")
            .Select(TryGetComponent)
            .Where(component => component != null)
            .Cast<Component>()
            .ToList();

        foreach (Component adminToy in adminToys)
        {
            TrySetSyncInterval(adminToy, 0f);
        }

        return new LoadedSchematicMap(schematicName, root, attachedBlocks, adminToys);
    }

    private static IEnumerable<object> ReadMemberValues(object instance, string memberName)
    {
        if (instance == null)
        {
            yield break;
        }

        Type type = instance.GetType();
        object? value = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance)
            ?? type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);

        if (value is not IEnumerable enumerable)
        {
            yield break;
        }

        foreach (object? item in enumerable)
        {
            if (item != null)
            {
                yield return item;
            }
        }
    }

    private static GameObject? TryGetGameObject(object value)
    {
        if (value is GameObject gameObject)
        {
            return gameObject;
        }

        if (value is Component component)
        {
            return component.gameObject;
        }

        Type type = value.GetType();
        object? gameObjectValue = type.GetProperty("gameObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value)
            ?? type.GetField("gameObject", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value);

        return gameObjectValue as GameObject;
    }

    private static Component? TryGetComponent(object value)
    {
        if (value is Component component)
        {
            return component;
        }

        GameObject? gameObject = TryGetGameObject(value);
        return gameObject?.transform;
    }

    private static void TrySetSyncInterval(Component component, float value)
    {
        Type type = component.GetType();
        MemberInfo? member = (MemberInfo?)type.GetProperty("syncInterval", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? type.GetField("syncInterval", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        switch (member)
        {
            case PropertyInfo property when property.CanWrite:
                property.SetValue(component, value);
                break;
            case FieldInfo field:
                field.SetValue(component, value);
                break;
        }
    }

    private static string GetInstalledSchematicPath(string schematicName)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SCP Secret Laboratory",
            "LabAPI",
            "configs",
            "ProjectMER",
            "Schematics",
            schematicName);
    }

    private sealed class LoadedSchematicMap
    {
        public LoadedSchematicMap(
            string schematicName,
            GameObject root,
            List<GameObject> attachedBlocks,
            List<Component> adminToys)
        {
            SchematicName = schematicName;
            Root = root;
            AttachedBlocks = attachedBlocks;
            AdminToys = adminToys;
        }

        public string SchematicName { get; }

        public GameObject Root { get; }

        public List<GameObject> AttachedBlocks { get; }

        public List<Component> AdminToys { get; }
    }
}

internal readonly struct NavMeshDebugEdge
{
    public NavMeshDebugEdge(Vector3 start, Vector3 end)
    {
        Start = start;
        End = end;
    }

    public Vector3 Start { get; }

    public Vector3 End { get; }
}
