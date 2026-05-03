using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using LabApi.Features.Wrappers;
using MapGeneration;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace ScpslPluginStarter;

internal sealed class FacilityNavMeshService
{
    private NavMeshDataInstance _runtimeNavMeshInstance;
    private Bounds _runtimeNavMeshBounds;
    private bool _hasRuntimeNavMesh;
    private readonly List<NavMeshLinkInstance> _runtimeOffMeshLinks = new();
    private int _runtimeOffMeshLinkCount;

    public bool HasRuntimeNavMesh => _hasRuntimeNavMesh;

    public bool HasUsableNavMesh(float sampleDistance)
    {
        if (_hasRuntimeNavMesh)
        {
            return true;
        }

        float navSampleDistance = Mathf.Max(0.5f, sampleDistance);
        foreach (Vector3 anchor in BuildDebugAnchors())
        {
            if (NavMesh.SamplePosition(anchor, out _, navSampleDistance, NavMesh.AllAreas))
            {
                return true;
            }
        }

        return false;
    }

    public string BuildStatus(BotBehaviorDefinition behavior)
    {
        return $"enabled={behavior.FacilityRuntimeNavMeshEnabled}, surfaceEnabled={behavior.FacilitySurfaceRuntimeNavMeshEnabled}, loaded={_hasRuntimeNavMesh}, offMeshLinks={_runtimeOffMeshLinkCount}, boundsCenter=({_runtimeNavMeshBounds.center.x:F1},{_runtimeNavMeshBounds.center.y:F1},{_runtimeNavMeshBounds.center.z:F1}), boundsSize=({_runtimeNavMeshBounds.size.x:F1},{_runtimeNavMeshBounds.size.y:F1},{_runtimeNavMeshBounds.size.z:F1})";
    }

    public bool TryBakeRuntimeNavMesh(BotBehaviorDefinition behavior, out string response)
    {
        RemoveRuntimeNavMesh();

        if (!behavior.FacilityRuntimeNavMeshEnabled)
        {
            response = "Facility runtime NavMesh is disabled.";
            return false;
        }

        Bounds bounds = BuildSceneBounds(behavior.FacilityRuntimeNavMeshBoundsPadding);
        return TryBakeRuntimeNavMeshCore(
            behavior,
            bounds,
            null,
            behavior.FacilityRuntimeNavMeshUseRoomTemplates,
            "Facility runtime NavMesh",
            out response);
    }

    public bool TryBakeSurfaceRuntimeNavMesh(BotBehaviorDefinition behavior, out string response)
    {
        RemoveRuntimeNavMesh();

        if (!behavior.FacilitySurfaceRuntimeNavMeshEnabled)
        {
            response = "Surface runtime NavMesh is disabled.";
            return false;
        }

        if (!TryBuildSurfaceBounds(
            behavior.FacilityRuntimeNavMeshBoundsPadding,
            out Bounds bounds,
            out List<Vector3> anchors))
        {
            response = "Surface runtime NavMesh bake found no surface room or door anchors.";
            return false;
        }

        return TryBakeRuntimeNavMeshCore(
            behavior,
            bounds,
            anchors,
            false,
            "Surface runtime NavMesh",
            out response);
    }

    private bool TryBakeRuntimeNavMeshCore(
        BotBehaviorDefinition behavior,
        Bounds bounds,
        IReadOnlyList<Vector3>? anchorFloorProbeAnchors,
        bool useRoomTemplates,
        string label,
        out string response)
    {
        List<NavMeshBuildSource> sources = new();
        List<NavMeshBuildMarkup> markups = BuildIgnoredMarkups(behavior);

        NavMeshBuilder.CollectSources(
            bounds,
            ~0,
            behavior.FacilityRuntimeNavMeshUseRenderMeshes
                ? NavMeshCollectGeometry.RenderMeshes
                : NavMeshCollectGeometry.PhysicsColliders,
            0,
            markups,
            sources);

        int collectedSourceCount = sources.Count;
        SourceFilterResult sourceFilter = ReplaceUnreadableMeshSourcesWithFloorFallbacks(sources);
        RoomTemplateSourceResult roomTemplateSources = AddRoomTemplateSources(
            sources,
            useRoomTemplates);
        int anchorFloorFallbacks = AddAnchorFloorProbeSources(
            sources,
            behavior.FacilityRuntimeNavMeshDebugSampleRadius,
            behavior.FacilityRuntimeNavMeshDebugSampleSpacing,
            anchorFloorProbeAnchors);
        int doorBridgeSources = AddDoorBridgeSources(sources, behavior);

        if (sources.Count == 0)
        {
            response = behavior.FacilityRuntimeNavMeshUseRenderMeshes
                ? $"{label} bake found no usable render-mesh sources collected={collectedSourceCount} removedUnreadableMeshes={sourceFilter.RemovedUnreadableMeshes} floorFallbacks={sourceFilter.FloorFallbacks} roomTemplateSources={roomTemplateSources.Sources} roomTemplateRooms={roomTemplateSources.Rooms} missingRoomTemplates={roomTemplateSources.MissingRooms} anchorFloorFallbacks={anchorFloorFallbacks} doorBridgeSources={doorBridgeSources}."
                : $"{label} bake found no usable physics-collider sources collected={collectedSourceCount} removedUnreadableMeshes={sourceFilter.RemovedUnreadableMeshes} floorFallbacks={sourceFilter.FloorFallbacks} roomTemplateSources={roomTemplateSources.Sources} roomTemplateRooms={roomTemplateSources.Rooms} missingRoomTemplates={roomTemplateSources.MissingRooms} anchorFloorFallbacks={anchorFloorFallbacks} doorBridgeSources={doorBridgeSources}.";
            return false;
        }

        NavMeshBuildSettings settings = NavMeshAgentTypeUtility.CreateDefaultBuildSettings();
        settings.agentRadius = Mathf.Max(0.05f, behavior.FacilityRuntimeNavMeshAgentRadius);
        settings.agentHeight = Mathf.Max(0.5f, behavior.FacilityRuntimeNavMeshAgentHeight);
        settings.agentSlope = Mathf.Clamp(behavior.FacilityRuntimeNavMeshAgentMaxSlope, 0f, 89f);
        settings.agentClimb = Mathf.Max(0f, behavior.FacilityRuntimeNavMeshAgentClimb);
        settings.minRegionArea = Mathf.Max(0f, behavior.FacilityRuntimeNavMeshMinRegionArea);

        NavMeshData data;
        try
        {
            bool previousLogEnabled = Debug.unityLogger.logEnabled;
            Debug.unityLogger.logEnabled = false;
            try
            {
                data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
            }
            finally
            {
                Debug.unityLogger.logEnabled = previousLogEnabled;
            }
        }
        catch (Exception ex)
        {
            response = $"{label} bake failed: {ex.GetBaseException().Message}";
            return false;
        }

        if (data == null)
        {
            response = $"{label} bake returned no NavMeshData.";
            return false;
        }

        _runtimeNavMeshInstance = NavMesh.AddNavMeshData(data);
        _hasRuntimeNavMesh = _runtimeNavMeshInstance.valid;
        _runtimeNavMeshBounds = bounds;
        int offMeshLinks = _hasRuntimeNavMesh ? CreateRuntimeOffMeshLinks(behavior) : 0;
        string sourceKind = behavior.FacilityRuntimeNavMeshUseRenderMeshes ? "render-mesh" : "physics-collider";
        string sourceExamples = DescribeSources(sources, 5);
        response = _hasRuntimeNavMesh
            ? $"{label} baked with {sources.Count}/{collectedSourceCount} {sourceKind} sources removedUnreadableMeshes={sourceFilter.RemovedUnreadableMeshes} floorFallbacks={sourceFilter.FloorFallbacks} roomTemplateSources={roomTemplateSources.Sources} roomTemplateRooms={roomTemplateSources.Rooms} missingRoomTemplates={roomTemplateSources.MissingRooms} anchorFloorFallbacks={anchorFloorFallbacks} doorBridgeSources={doorBridgeSources} offMeshLinks={offMeshLinks} bounds center=({bounds.center.x:F1},{bounds.center.y:F1},{bounds.center.z:F1}) size=({bounds.size.x:F1},{bounds.size.y:F1},{bounds.size.z:F1}) settings=(radius={settings.agentRadius:F2},height={settings.agentHeight:F2},slope={settings.agentSlope:F1},climb={settings.agentClimb:F2},minRegion={settings.minRegionArea:F1}) examples=[{sourceExamples}]. {BuildTriangulationStatus()}"
            : $"{label} bake completed but the NavMeshData instance was not valid.";
        return _hasRuntimeNavMesh;
    }

    public void RemoveRuntimeNavMesh()
    {
        ClearRuntimeOffMeshLinks();

        if (!_hasRuntimeNavMesh)
        {
            return;
        }

        NavMesh.RemoveNavMeshData(_runtimeNavMeshInstance);
        _runtimeNavMeshInstance = default;
        _runtimeNavMeshBounds = default;
        _hasRuntimeNavMesh = false;
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

    public IReadOnlyList<NavMeshDebugSample> GetRuntimeNavMeshDebugSamples(int maxSamples, float spacing)
    {
        if (!_hasRuntimeNavMesh || maxSamples <= 0)
        {
            return Array.Empty<NavMeshDebugSample>();
        }

        NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();
        Vector3[] vertices = triangulation.vertices ?? Array.Empty<Vector3>();
        int[] indices = triangulation.indices ?? Array.Empty<int>();
        if (vertices.Length == 0 || indices.Length < 3)
        {
            return Array.Empty<NavMeshDebugSample>();
        }

        float cellSize = Mathf.Max(0.25f, spacing);
        Bounds filterBounds = _runtimeNavMeshBounds;
        filterBounds.Expand(0.5f);

        List<NavMeshDebugSample> samples = new(maxSamples);
        HashSet<string> seen = new(StringComparer.Ordinal);
        int triangleCount = indices.Length / 3;
        int stride = triangleCount > maxSamples
            ? Mathf.Max(1, Mathf.CeilToInt(triangleCount / (float)maxSamples))
            : 1;

        for (int triangle = 0; triangle < triangleCount && samples.Count < maxSamples; triangle += stride)
        {
            int i = triangle * 3;
            if (!TryGetTriangleCentroid(vertices, indices[i], indices[i + 1], indices[i + 2], out Vector3 centroid)
                || !filterBounds.Contains(centroid))
            {
                continue;
            }

            string key = QuantizeSamplePoint(centroid, cellSize);
            if (!seen.Add(key))
            {
                continue;
            }

            samples.Add(new NavMeshDebugSample(centroid));
        }

        return samples;
    }

    public IReadOnlyList<NavMeshDebugSample> GetLoadedNavMeshDebugSamples(
        int maxSamples,
        float spacing,
        float radius,
        float sampleDistance)
    {
        if (maxSamples <= 0)
        {
            return Array.Empty<NavMeshDebugSample>();
        }

        float cellSize = Mathf.Max(0.25f, spacing);
        float sampleRadius = Mathf.Max(cellSize, radius);
        float navSampleDistance = Mathf.Max(0.5f, sampleDistance);
        List<Vector3> anchors = BuildDebugAnchors();
        if (anchors.Count == 0)
        {
            return Array.Empty<NavMeshDebugSample>();
        }

        List<NavMeshDebugSample> samples = new(maxSamples);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (Vector3 anchor in anchors)
        {
            for (float x = -sampleRadius; x <= sampleRadius && samples.Count < maxSamples; x += cellSize)
            {
                for (float z = -sampleRadius; z <= sampleRadius && samples.Count < maxSamples; z += cellSize)
                {
                    Vector3 probe = new(anchor.x + x, anchor.y, anchor.z + z);
                    if (!NavMesh.SamplePosition(probe, out NavMeshHit hit, navSampleDistance, NavMesh.AllAreas))
                    {
                        continue;
                    }

                    string key = QuantizeSamplePoint(hit.position, cellSize);
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    samples.Add(new NavMeshDebugSample(hit.position));
                }
            }

            if (samples.Count >= maxSamples)
            {
                break;
            }
        }

        return samples;
    }

    public string BuildTriangulationStatus()
    {
        if (!_hasRuntimeNavMesh)
        {
            return "triangulation=(loaded=false)";
        }

        NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();
        int vertexCount = triangulation.vertices?.Length ?? 0;
        int indexCount = triangulation.indices?.Length ?? 0;
        int triangleCount = indexCount / 3;
        return $"triangulation=(vertices={vertexCount},triangles={triangleCount})";
    }

    private void ClearRuntimeOffMeshLinks()
    {
        foreach (NavMeshLinkInstance link in _runtimeOffMeshLinks)
        {
            if (NavMesh.IsLinkValid(link))
            {
                NavMesh.RemoveLink(link);
            }
        }

        _runtimeOffMeshLinks.Clear();
        _runtimeOffMeshLinkCount = 0;
    }

    private int CreateRuntimeOffMeshLinks(BotBehaviorDefinition behavior)
    {
        ClearRuntimeOffMeshLinks();

        if (!_hasRuntimeNavMesh
            || !behavior.FacilityRuntimeNavMeshCreateOffMeshLinks
            || behavior.FacilityRuntimeNavMeshMaxOffMeshLinks <= 0)
        {
            return 0;
        }

        int maxLinks = Math.Max(0, behavior.FacilityRuntimeNavMeshMaxOffMeshLinks);
        int maxSamples = Math.Min(5000, Math.Max(256, maxLinks * 8));
        float searchRadius = Mathf.Max(0.5f, behavior.FacilityRuntimeNavMeshOffMeshLinkSearchRadius);
        float maxVerticalDelta = Mathf.Max(0.05f, behavior.FacilityRuntimeNavMeshOffMeshLinkMaxVerticalDelta);
        float minHorizontalDistance = Mathf.Max(behavior.FacilityRuntimeNavMeshAgentRadius * 2f, 0.45f);
        float bucketSize = searchRadius;
        IReadOnlyList<NavMeshDebugSample> samples = GetRuntimeNavMeshDebugSamples(
            maxSamples,
            behavior.FacilityRuntimeNavMeshOffMeshLinkSampleSpacing);
        if (samples.Count < 2)
        {
            samples = GetLoadedNavMeshDebugSamples(
                maxSamples,
                behavior.FacilityRuntimeNavMeshOffMeshLinkSampleSpacing,
                behavior.FacilityRuntimeNavMeshDebugSampleRadius,
                behavior.FacilityRuntimeNavMeshOffMeshLinkSampleDistance);
        }

        if (samples.Count < 2)
        {
            return 0;
        }

        Dictionary<string, List<Vector3>> buckets = new(StringComparer.Ordinal);
        HashSet<string> linkKeys = new(StringComparer.Ordinal);
        NavMeshPath path = new();

        foreach (NavMeshDebugSample sample in samples)
        {
            Vector3 current = sample.Position;
            Vector3Int bucket = GetOffMeshLinkBucket(current, bucketSize);

            for (int x = -1; x <= 1 && _runtimeOffMeshLinkCount < maxLinks; x++)
            {
                for (int y = -1; y <= 1 && _runtimeOffMeshLinkCount < maxLinks; y++)
                {
                    for (int z = -1; z <= 1 && _runtimeOffMeshLinkCount < maxLinks; z++)
                    {
                        string neighborKey = FormatOffMeshLinkBucket(bucket.x + x, bucket.y + y, bucket.z + z);
                        if (!buckets.TryGetValue(neighborKey, out List<Vector3> candidates))
                        {
                            continue;
                        }

                        foreach (Vector3 candidate in candidates)
                        {
                            if (_runtimeOffMeshLinkCount >= maxLinks)
                            {
                                break;
                            }

                            if (!ShouldCreateRuntimeOffMeshLink(
                                    candidate,
                                    current,
                                    minHorizontalDistance,
                                    searchRadius,
                                    maxVerticalDelta,
                                    linkKeys,
                                    path))
                            {
                                continue;
                            }

                            NavMeshLinkData linkData = new()
                            {
                                startPosition = candidate,
                                endPosition = current,
                                width = Mathf.Max(0f, behavior.FacilityRuntimeNavMeshOffMeshLinkWidth),
                                costModifier = behavior.FacilityRuntimeNavMeshOffMeshLinkCostModifier,
                                bidirectional = true,
                                area = 0,
                            };

                            NavMeshLinkInstance link = NavMesh.AddLink(linkData, Vector3.zero, Quaternion.identity);
                            if (!NavMesh.IsLinkValid(link))
                            {
                                continue;
                            }

                            _runtimeOffMeshLinks.Add(link);
                            _runtimeOffMeshLinkCount++;
                        }
                    }
                }
            }

            string bucketKey = FormatOffMeshLinkBucket(bucket.x, bucket.y, bucket.z);
            if (!buckets.TryGetValue(bucketKey, out List<Vector3> bucketSamples))
            {
                bucketSamples = new List<Vector3>();
                buckets[bucketKey] = bucketSamples;
            }

            bucketSamples.Add(current);

            if (_runtimeOffMeshLinkCount >= maxLinks)
            {
                break;
            }
        }

        return _runtimeOffMeshLinkCount;
    }

    private static bool ShouldCreateRuntimeOffMeshLink(
        Vector3 first,
        Vector3 second,
        float minHorizontalDistance,
        float maxHorizontalDistance,
        float maxVerticalDelta,
        ISet<string> seen,
        NavMeshPath path)
    {
        float verticalDelta = Mathf.Abs(first.y - second.y);
        if (verticalDelta > maxVerticalDelta)
        {
            return false;
        }

        Vector2 firstHorizontal = new(first.x, first.z);
        Vector2 secondHorizontal = new(second.x, second.z);
        float horizontalDistance = Vector2.Distance(firstHorizontal, secondHorizontal);
        if (horizontalDistance < minHorizontalDistance || horizontalDistance > maxHorizontalDistance)
        {
            return false;
        }

        string key = QuantizeOffMeshLink(first, second, 0.75f);
        if (!seen.Add(key))
        {
            return false;
        }

        path.ClearCorners();
        if (NavMesh.CalculatePath(first, second, NavMesh.AllAreas, path)
            && path.status == NavMeshPathStatus.PathComplete)
        {
            return false;
        }

        return true;
    }

    private static Vector3Int GetOffMeshLinkBucket(Vector3 position, float bucketSize)
    {
        float size = Mathf.Max(0.5f, bucketSize);
        return new Vector3Int(
            Mathf.FloorToInt(position.x / size),
            Mathf.FloorToInt(position.y / size),
            Mathf.FloorToInt(position.z / size));
    }

    private static string FormatOffMeshLinkBucket(int x, int y, int z)
    {
        return $"{x}:{y}:{z}";
    }

    private static string QuantizeOffMeshLink(Vector3 first, Vector3 second, float cellSize)
    {
        string firstKey = QuantizeSamplePoint(first, cellSize);
        string secondKey = QuantizeSamplePoint(second, cellSize);
        return string.CompareOrdinal(firstKey, secondKey) <= 0
            ? $"{firstKey}|{secondKey}"
            : $"{secondKey}|{firstKey}";
    }

    private static List<NavMeshBuildMarkup> BuildIgnoredMarkups(BotBehaviorDefinition behavior)
    {
        List<NavMeshBuildMarkup> markups = new();
        foreach (Player player in Player.List)
        {
            if (player?.ReferenceHub?.transform != null)
            {
                markups.Add(CreateIgnoreMarkup(player.ReferenceHub.transform));
            }
        }

        if (behavior.FacilityRuntimeNavMeshIgnoreDoors)
        {
            foreach (Door door in Door.List)
            {
                Transform? transform = door?.Base?.transform;
                if (transform != null)
                {
                    markups.Add(CreateIgnoreMarkup(transform));
                }
            }
        }

        return markups;
    }

    private static NavMeshBuildMarkup CreateIgnoreMarkup(Transform root)
    {
        return new NavMeshBuildMarkup
        {
            root = root,
            ignoreFromBuild = true,
        };
    }

    private static SourceFilterResult ReplaceUnreadableMeshSourcesWithFloorFallbacks(List<NavMeshBuildSource> sources)
    {
        int removed = 0;
        int floorFallbacks = 0;
        for (int i = sources.Count - 1; i >= 0; i--)
        {
            NavMeshBuildSource source = sources[i];
            if (source.shape != NavMeshBuildSourceShape.Mesh
                || source.sourceObject is not Mesh mesh
                || mesh.isReadable)
            {
                continue;
            }

            if (TryCreateUnreadableFloorFallback(source, mesh, out NavMeshBuildSource fallback))
            {
                sources[i] = fallback;
                floorFallbacks++;
                continue;
            }

            sources.RemoveAt(i);
            removed++;
        }

        return new SourceFilterResult(removed, floorFallbacks);
    }

    private static bool TryCreateUnreadableFloorFallback(NavMeshBuildSource source, Mesh mesh, out NavMeshBuildSource fallback)
    {
        fallback = default;

        Bounds localBounds = mesh.bounds;
        Bounds worldBounds = TransformBounds(source.transform, localBounds);
        Vector3 worldSize = worldBounds.size;
        float horizontalMin = Mathf.Min(Mathf.Abs(worldSize.x), Mathf.Abs(worldSize.z));
        float horizontalMax = Mathf.Max(Mathf.Abs(worldSize.x), Mathf.Abs(worldSize.z));
        float height = Mathf.Abs(worldSize.y);

        if (horizontalMin < 0.75f || horizontalMax < 1.5f)
        {
            return false;
        }

        if (height <= 1.25f)
        {
            fallback = CreateBoxSource(
                source.area,
                source.transform * Matrix4x4.Translate(localBounds.center),
                new Vector3(
                    Mathf.Max(0.05f, localBounds.size.x),
                    Mathf.Max(0.05f, localBounds.size.y),
                    Mathf.Max(0.05f, localBounds.size.z)));
            return true;
        }

        if (horizontalMin < 3.0f || height < 1.5f)
        {
            return false;
        }

        Vector3 bottomCenter = new(
            worldBounds.center.x,
            worldBounds.min.y + 0.05f,
            worldBounds.center.z);
        Vector3 bottomSize = new(
            Mathf.Max(0.05f, worldBounds.size.x),
            0.1f,
            Mathf.Max(0.05f, worldBounds.size.z));

        fallback = CreateBoxSource(source.area, Matrix4x4.Translate(bottomCenter), bottomSize);
        return true;
    }

    private static NavMeshBuildSource CreateBoxSource(int area, Matrix4x4 transform, Vector3 size)
    {
        return new NavMeshBuildSource
        {
            shape = NavMeshBuildSourceShape.Box,
            transform = transform,
            size = size,
            area = area,
        };
    }

    private static RoomTemplateSourceResult AddRoomTemplateSources(List<NavMeshBuildSource> sources, bool enabled)
    {
        if (!enabled)
        {
            return default;
        }

        Dictionary<string, RoomNavTemplate> templates = LoadEmbeddedRoomTemplates();
        if (templates.Count == 0 || RoomIdentifier.AllRoomIdentifiers == null)
        {
            return default;
        }

        int sourceCount = 0;
        int roomCount = 0;
        int missingRooms = 0;
        HashSet<RoomIdentifier> rooms = RoomIdentifier.AllRoomIdentifiers;
        foreach (RoomIdentifier room in rooms)
        {
            if (room == null || room.transform == null)
            {
                continue;
            }

            string? templateName = ResolveTemplateName(room, templates);
            if (templateName == null || !templates.TryGetValue(templateName, out RoomNavTemplate template))
            {
                missingRooms++;
                continue;
            }

            Matrix4x4 roomMatrix = room.transform.localToWorldMatrix;
            foreach (RoomNavBox box in template.Boxes)
            {
                sources.Add(CreateBoxSource(
                    0,
                    roomMatrix * Matrix4x4.Translate(box.Center),
                    box.Size));
                sourceCount++;
            }

            roomCount++;
        }

        return new RoomTemplateSourceResult(sourceCount, roomCount, missingRooms);
    }

    private static Dictionary<string, RoomNavTemplate> LoadEmbeddedRoomTemplates()
    {
        Assembly assembly = typeof(FacilityNavMeshService).Assembly;
        Dictionary<string, RoomNavTemplate> templates = new(StringComparer.OrdinalIgnoreCase);
        foreach (string resourceName in assembly.GetManifestResourceNames())
        {
            if (resourceName.IndexOf(".NavTemplates.", StringComparison.Ordinal) < 0
                || !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                continue;
            }

            using StreamReader reader = new(stream);
            RoomNavTemplate? template = ParseRoomTemplate(reader.ReadToEnd());
            if (template != null && template.Boxes.Count > 0)
            {
                templates[template.Name] = template;
            }
        }

        return templates;
    }

    private static RoomNavTemplate? ParseRoomTemplate(string json)
    {
        Match nameMatch = Regex.Match(json, @"""name""\s*:\s*""([^""]+)""");
        if (!nameMatch.Success)
        {
            return null;
        }

        RoomNavTemplate template = new(nameMatch.Groups[1].Value);
        MatchCollection boxMatches = Regex.Matches(
            json,
            @"""center""\s*:\s*\{\s*""x""\s*:\s*([-+0-9.Ee]+),\s*""y""\s*:\s*([-+0-9.Ee]+),\s*""z""\s*:\s*([-+0-9.Ee]+)\s*\}\s*,\s*""size""\s*:\s*\{\s*""x""\s*:\s*([-+0-9.Ee]+),\s*""y""\s*:\s*([-+0-9.Ee]+),\s*""z""\s*:\s*([-+0-9.Ee]+)\s*\}");

        foreach (Match match in boxMatches)
        {
            template.Boxes.Add(new RoomNavBox(
                new Vector3(ParseFloat(match.Groups[1].Value), ParseFloat(match.Groups[2].Value), ParseFloat(match.Groups[3].Value)),
                new Vector3(ParseFloat(match.Groups[4].Value), ParseFloat(match.Groups[5].Value), ParseFloat(match.Groups[6].Value))));
        }

        return template;
    }

    private static float ParseFloat(string value)
    {
        return float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float result)
            ? result
            : 0f;
    }

    private static string? ResolveTemplateName(RoomIdentifier room, IReadOnlyDictionary<string, RoomNavTemplate> templates)
    {
        string objectName = CleanTemplateName(room.gameObject != null ? room.gameObject.name : "");
        if (!string.IsNullOrWhiteSpace(objectName) && templates.ContainsKey(objectName))
        {
            return objectName;
        }

        string? exact = ResolveExactRoomName(room.Name);
        if (exact != null && templates.ContainsKey(exact))
        {
            return exact;
        }

        string? byShape = ResolveShapeTemplateName(room.Zone, room.Shape);
        return byShape != null && templates.ContainsKey(byShape) ? byShape : null;
    }

    private static string CleanTemplateName(string name)
    {
        int cloneIndex = name.IndexOf("(Clone)", StringComparison.OrdinalIgnoreCase);
        return (cloneIndex >= 0 ? name.Remove(cloneIndex, "(Clone)".Length) : name).Trim();
    }

    private static string? ResolveExactRoomName(RoomName roomName)
    {
        switch (roomName)
        {
            case RoomName.LczClassDSpawn:
                return "LCZ_ClassDSpawn";
            case RoomName.LczComputerRoom:
                return "LCZ_372";
            case RoomName.LczCheckpointA:
                return "LCZ_ChkpA";
            case RoomName.LczCheckpointB:
                return "LCZ_ChkpB";
            case RoomName.LczToilets:
                return "LCZ_Toilets";
            case RoomName.LczArmory:
                return "LCZ_Armory";
            case RoomName.Lcz173:
                return "LCZ_173";
            case RoomName.LczGlassroom:
            case RoomName.LczGreenhouse:
                return "LCZ_Plants";
            case RoomName.Lcz330:
                return "LCZ_330";
            case RoomName.Lcz914:
                return "LCZ_914";
            case RoomName.LczAirlock:
                return "LCZ_Airlock";
            case RoomName.HczCheckpointToEntranceZone:
                return "HCZ_EZ_Checkpoint Part";
            case RoomName.HczCheckpointA:
                return "HCZ_ChkpA";
            case RoomName.HczCheckpointB:
                return "HCZ_ChkpB";
            case RoomName.HczWarhead:
                return "HCZ_Nuke";
            case RoomName.Hcz049:
                return "HCZ_049";
            case RoomName.Hcz079:
                return "HCZ_079";
            case RoomName.Hcz096:
                return "HCZ_096";
            case RoomName.Hcz106:
                return "HCZ_106_Rework";
            case RoomName.Hcz939:
                return "HCZ_939";
            case RoomName.HczMicroHID:
                return "HCZ_MicroHID_New";
            case RoomName.HczArmory:
                return "HCZ_TArmory";
            case RoomName.HczServers:
                return "HCZ_ServerRoom";
            case RoomName.HczTesla:
                return "HCZ_Tesla_Rework";
            case RoomName.HczTestroom:
                return "HCZ_Testroom";
            case RoomName.Hcz127:
                return "HCZ_127";
            case RoomName.HczAcroamaticAbatement:
            case RoomName.HczWaysideIncinerator:
                return "HCZ_IncineratorWayside";
            case RoomName.HczRampTunnel:
                return "HCZ_Intersection_Ramp";
            case RoomName.EzCollapsedTunnel:
                return "EZ_CollapsedTunnel";
            case RoomName.EzGateA:
                return "EZ_GateA";
            case RoomName.EzGateB:
                return "EZ_GateB";
            case RoomName.EzRedroom:
                return "EZ_PCs";
            case RoomName.EzEvacShelter:
                return "EZ_Shelter";
            case RoomName.EzIntercom:
                return "EZ_Intercom";
            case RoomName.EzOfficeStoried:
                return "EZ_upstairs";
            case RoomName.EzOfficeLarge:
                return "EZ_PCs";
            case RoomName.EzOfficeSmall:
                return "EZ_PCs_small";
            default:
                return null;
        }
    }

    private static string? ResolveShapeTemplateName(FacilityZone zone, RoomShape shape)
    {
        switch (zone)
        {
            case FacilityZone.LightContainment:
                return shape switch
                {
                    RoomShape.Straight => "LCZ_Straight",
                    RoomShape.Curve => "LCZ_Curve",
                    RoomShape.TShape => "LCZ_TCross",
                    RoomShape.XShape => "LCZ_Crossing",
                    RoomShape.Endroom => "LCZ_Airlock",
                    _ => null,
                };
            case FacilityZone.HeavyContainment:
                return shape switch
                {
                    RoomShape.Straight => "HCZ_Straight",
                    RoomShape.Curve => "HCZ_Curve",
                    RoomShape.TShape => "HCZ_Intersection",
                    RoomShape.XShape => "HCZ_Crossing",
                    RoomShape.Endroom => "HCZ_Corner_Deep",
                    _ => null,
                };
            case FacilityZone.Entrance:
                return shape switch
                {
                    RoomShape.Straight => "EZ_Straight",
                    RoomShape.Curve => "EZ_Curve",
                    RoomShape.TShape => "EZ_ThreeWay",
                    RoomShape.XShape => "EZ_Crossing",
                    RoomShape.Endroom => "EZ_Endoof",
                    _ => null,
                };
            default:
                return null;
        }
    }

    private static int AddAnchorFloorProbeSources(
        List<NavMeshBuildSource> sources,
        float radius,
        float spacing,
        IReadOnlyList<Vector3>? anchorFloorProbeAnchors = null)
    {
        IReadOnlyList<Vector3> anchors = anchorFloorProbeAnchors ?? BuildDebugAnchors();
        if (anchors.Count == 0)
        {
            return 0;
        }

        const int maxProbeSources = 8000;
        float cellSize = Mathf.Max(1.0f, spacing);
        float probeRadius = Mathf.Clamp(radius, cellSize, 26.0f);
        Vector3 sourceSize = new(cellSize * 0.95f, 0.12f, cellSize * 0.95f);
        HashSet<string> seen = new(StringComparer.Ordinal);
        int added = 0;

        foreach (Vector3 anchor in anchors)
        {
            for (float x = -probeRadius; x <= probeRadius && added < maxProbeSources; x += cellSize)
            {
                for (float z = -probeRadius; z <= probeRadius && added < maxProbeSources; z += cellSize)
                {
                    Vector3 probe = new(anchor.x + x, anchor.y, anchor.z + z);
                    if (!TryFindWalkableFloorNearProbe(probe, out Vector3 floor))
                    {
                        continue;
                    }

                    string key = QuantizeSamplePoint(floor, cellSize);
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    sources.Add(CreateBoxSource(
                        0,
                        Matrix4x4.Translate(floor + (Vector3.down * (sourceSize.y * 0.5f))),
                        sourceSize));
                    added++;
                }
            }

            if (added >= maxProbeSources)
            {
                break;
            }
        }

        return added;
    }

    private static int AddDoorBridgeSources(List<NavMeshBuildSource> sources, BotBehaviorDefinition behavior)
    {
        const int maxDoorBridgeSources = 512;
        float horizontalSize = Mathf.Clamp(behavior.FacilityRuntimeNavMeshAgentRadius * 8f, 2.2f, 4.0f);
        Vector3 sourceSize = new(horizontalSize, 0.12f, horizontalSize);
        HashSet<string> seen = new(StringComparer.Ordinal);
        int added = 0;

        foreach (Door door in Door.List)
        {
            if (added >= maxDoorBridgeSources || door == null || door.IsDestroyed)
            {
                continue;
            }

            Transform? transform = door.Base?.transform;
            Vector3 origin = transform != null ? transform.position : door.Position;
            if (!TryFindWalkableFloorNearProbe(origin, out Vector3 floor))
            {
                continue;
            }

            string key = QuantizeSamplePoint(floor, 1.0f);
            if (!seen.Add(key))
            {
                continue;
            }

            sources.Add(CreateBoxSource(
                0,
                Matrix4x4.Translate(floor + (Vector3.down * (sourceSize.y * 0.5f))),
                sourceSize));
            added++;
        }

        return added;
    }

    private static bool TryFindWalkableFloorNearProbe(Vector3 probe, out Vector3 floor)
    {
        floor = default;
        Vector3 origin = probe + (Vector3.up * 3.0f);
        RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 7.0f, ~0, QueryTriggerInteraction.Ignore);
        if (hits == null || hits.Length == 0)
        {
            return false;
        }

        float bestScore = float.MaxValue;
        bool found = false;
        foreach (RaycastHit hit in hits)
        {
            Collider collider = hit.collider;
            if (collider == null
                || !collider.enabled
                || hit.normal.y < 0.65f
                || hit.point.y > probe.y + 1.25f
                || hit.point.y < probe.y - 3.0f
                || ShouldIgnoreBounds(collider.transform))
            {
                continue;
            }

            float score = Mathf.Abs(hit.point.y - probe.y);
            if (score >= bestScore)
            {
                continue;
            }

            floor = hit.point;
            bestScore = score;
            found = true;
        }

        return found;
    }

    private static Bounds TransformBounds(Matrix4x4 transform, Bounds bounds)
    {
        Vector3 center = bounds.center;
        Vector3 extents = bounds.extents;
        Bounds transformed = new(transform.MultiplyPoint3x4(center), Vector3.zero);

        for (int x = -1; x <= 1; x += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 corner = center + new Vector3(extents.x * x, extents.y * y, extents.z * z);
                    transformed.Encapsulate(transform.MultiplyPoint3x4(corner));
                }
            }
        }

        return transformed;
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

    private static string QuantizeSamplePoint(Vector3 point, float cellSize)
    {
        return $"{Mathf.RoundToInt(point.x / cellSize)}:{Mathf.RoundToInt(point.y / cellSize)}:{Mathf.RoundToInt(point.z / cellSize)}";
    }

    private static List<Vector3> BuildDebugAnchors()
    {
        List<Vector3> anchors = new();
        foreach (Player player in Player.List)
        {
            if (player?.ReferenceHub?.transform != null)
            {
                anchors.Add(player.Position);
            }
        }

        foreach (Door door in Door.List)
        {
            Transform? transform = door?.Base?.transform;
            if (transform != null)
            {
                anchors.Add(transform.position);
            }
        }

        return anchors;
    }

    private static List<Vector3> BuildZoneAnchors(FacilityZone zone)
    {
        List<Vector3> anchors = new();
        HashSet<string> seen = new(StringComparer.Ordinal);

        if (Room.List != null)
        {
            foreach (Room room in Room.List)
            {
                if (room == null || room.IsDestroyed || room.Zone != zone)
                {
                    continue;
                }

                AddAnchor(anchors, seen, room.Position);
            }
        }

        foreach (Door door in Door.List)
        {
            if (door == null || door.IsDestroyed || door.Zone != zone)
            {
                continue;
            }

            AddAnchor(anchors, seen, door.Position);
        }

        if (RoomIdentifier.AllRoomIdentifiers != null)
        {
            foreach (RoomIdentifier room in RoomIdentifier.AllRoomIdentifiers)
            {
                if (room == null || room.transform == null || room.Zone != zone)
                {
                    continue;
                }

                AddAnchor(anchors, seen, room.transform.position);
            }
        }

        return anchors;
    }

    private static void AddAnchor(List<Vector3> anchors, ISet<string> seen, Vector3 position)
    {
        string key = $"{Mathf.RoundToInt(position.x)}:{Mathf.RoundToInt(position.y)}:{Mathf.RoundToInt(position.z)}";
        if (seen.Add(key))
        {
            anchors.Add(position);
        }
    }

    private static bool TryGetTriangleCentroid(
        IReadOnlyList<Vector3> vertices,
        int firstIndex,
        int secondIndex,
        int thirdIndex,
        out Vector3 centroid)
    {
        centroid = default;
        if (firstIndex < 0
            || secondIndex < 0
            || thirdIndex < 0
            || firstIndex >= vertices.Count
            || secondIndex >= vertices.Count
            || thirdIndex >= vertices.Count)
        {
            return false;
        }

        centroid = (vertices[firstIndex] + vertices[secondIndex] + vertices[thirdIndex]) / 3f;
        return true;
    }

    private static string DescribeSources(IReadOnlyList<NavMeshBuildSource> sources, int maxExamples)
    {
        if (sources.Count == 0 || maxExamples <= 0)
        {
            return "";
        }

        int count = Math.Min(maxExamples, sources.Count);
        List<string> examples = new(count);
        for (int i = 0; i < count; i++)
        {
            NavMeshBuildSource source = sources[i];
            string name = source.sourceObject != null ? source.sourceObject.name : "none";
            examples.Add($"{source.shape}:{name}");
        }

        if (sources.Count > count)
        {
            examples.Add($"+{sources.Count - count} more");
        }

        return string.Join(", ", examples);
    }

    private static Bounds BuildSceneBounds(float padding)
    {
        Bounds? bounds = null;
        foreach (Collider collider in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
        {
            if (collider == null || !collider.enabled || ShouldIgnoreBounds(collider.transform))
            {
                continue;
            }

            bounds = Encapsulate(bounds, collider.bounds);
        }

        foreach (Renderer renderer in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (renderer == null || !renderer.enabled || ShouldIgnoreBounds(renderer.transform))
            {
                continue;
            }

            bounds = Encapsulate(bounds, renderer.bounds);
        }

        Bounds finalBounds = bounds ?? new Bounds(Vector3.zero, new Vector3(260f, 380f, 260f));
        float safePadding = Mathf.Max(0f, padding);
        finalBounds.Expand(new Vector3(safePadding * 2f, safePadding, safePadding * 2f));
        return finalBounds;
    }

    private static bool TryBuildSurfaceBounds(float padding, out Bounds bounds, out List<Vector3> anchors)
    {
        anchors = BuildZoneAnchors(FacilityZone.Surface);
        bounds = default;
        if (anchors.Count == 0)
        {
            return false;
        }

        Bounds? surfaceBounds = null;
        foreach (Vector3 anchor in anchors)
        {
            surfaceBounds = Encapsulate(surfaceBounds, new Bounds(anchor, new Vector3(18f, 16f, 18f)));
        }

        foreach (Collider collider in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
        {
            if (collider == null
                || !collider.enabled
                || ShouldIgnoreBounds(collider.transform)
                || !IsNearAnyAnchor(collider.bounds, anchors, 90f, 80f))
            {
                continue;
            }

            surfaceBounds = Encapsulate(surfaceBounds, collider.bounds);
        }

        foreach (Renderer renderer in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (renderer == null
                || !renderer.enabled
                || ShouldIgnoreBounds(renderer.transform)
                || !IsNearAnyAnchor(renderer.bounds, anchors, 90f, 80f))
            {
                continue;
            }

            surfaceBounds = Encapsulate(surfaceBounds, renderer.bounds);
        }

        if (!surfaceBounds.HasValue)
        {
            return false;
        }

        bounds = surfaceBounds.Value;
        float safePadding = Mathf.Max(0f, padding);
        bounds.Expand(new Vector3(safePadding * 2f, Mathf.Max(20f, safePadding * 2f), safePadding * 2f));
        return true;
    }

    private static bool IsNearAnyAnchor(Bounds bounds, IReadOnlyList<Vector3> anchors, float horizontalDistance, float verticalDistance)
    {
        foreach (Vector3 anchor in anchors)
        {
            float dx = DistanceOutsideRange(anchor.x, bounds.min.x, bounds.max.x);
            float dz = DistanceOutsideRange(anchor.z, bounds.min.z, bounds.max.z);
            float dy = DistanceOutsideRange(anchor.y, bounds.min.y, bounds.max.y);
            if (dy <= verticalDistance && Mathf.Sqrt((dx * dx) + (dz * dz)) <= horizontalDistance)
            {
                return true;
            }
        }

        return false;
    }

    private static float DistanceOutsideRange(float value, float min, float max)
    {
        if (value < min)
        {
            return min - value;
        }

        return value > max ? value - max : 0f;
    }

    private static bool ShouldIgnoreBounds(Transform transform)
    {
        if (transform == null)
        {
            return true;
        }

        foreach (Player player in Player.List)
        {
            Transform? root = player?.ReferenceHub?.transform;
            if (root != null && (transform == root || transform.IsChildOf(root)))
            {
                return true;
            }
        }

        return false;
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
}

internal readonly struct SourceFilterResult
{
    public SourceFilterResult(int removedUnreadableMeshes, int floorFallbacks)
    {
        RemovedUnreadableMeshes = removedUnreadableMeshes;
        FloorFallbacks = floorFallbacks;
    }

    public int RemovedUnreadableMeshes { get; }

    public int FloorFallbacks { get; }
}

internal readonly struct RoomTemplateSourceResult
{
    public RoomTemplateSourceResult(int sources, int rooms, int missingRooms)
    {
        Sources = sources;
        Rooms = rooms;
        MissingRooms = missingRooms;
    }

    public int Sources { get; }

    public int Rooms { get; }

    public int MissingRooms { get; }
}

internal sealed class RoomNavTemplate
{
    public RoomNavTemplate(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public List<RoomNavBox> Boxes { get; } = new();
}

internal readonly struct RoomNavBox
{
    public RoomNavBox(Vector3 center, Vector3 size)
    {
        Center = center;
        Size = size;
    }

    public Vector3 Center { get; }

    public Vector3 Size { get; }
}

internal readonly struct NavMeshDebugSample
{
    public NavMeshDebugSample(Vector3 position)
    {
        Position = position;
    }

    public Vector3 Position { get; }
}
