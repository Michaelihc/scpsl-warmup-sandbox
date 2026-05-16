using System;
using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using MapGeneration;
using UnityEngine;

namespace ScpslPluginStarter;

internal sealed class RoomGraphNavigationService
{
    private const float TemplateNodeVerticalOffset = 0.18f;
    private const float InRoomLinkDistance = 2.35f;
    private const float InRoomMaxVerticalDelta = 1.35f;
    private const float DoorAnchorLinkDistance = 9.5f;
    private const float AdjacentRoomLinkDistance = 14.0f;
    private const float DynamicLinkDistance = 32.0f;
    private const int DynamicLinkCount = 12;
    private const float NodeRoomProbeHeight = 0.45f;
    private const float NodeFloorProbeUp = 3.0f;
    private const float NodeFloorProbeDown = 9.0f;
    private const float BlockedSegmentMatchDistance = 0.9f;
    private const float BlockedSegmentVerticalTolerance = 1.4f;
    private const float DuplicateNodeHorizontalDistance = 0.22f;
    private const float DuplicateNodeVerticalDistance = 0.35f;

    private FacilityWaypointGraph? _graph;

    public bool HasGraph => _graph != null && _graph.Nodes.Count > 0;

    public string Rebuild(BotBehaviorDefinition behavior)
    {
        _graph = BuildGraph(behavior);
        return _graph == null
            ? "Facility room graph unavailable."
            : $"Facility room graph built seed={_graph.Seed} rooms={_graph.Rooms.Count} nodes={_graph.Nodes.Count} links={_graph.LinkCount} templates={_graph.TemplateRoomCount} gridNodes={_graph.TemplateNodeCount} missingTemplates={_graph.MissingTemplateRoomCount} doors={_graph.DoorAnchorCount} rejected={_graph.RejectedNodeCount}.";
    }

    public IReadOnlyList<Vector3> GetDebugNodes(int maxNodes)
    {
        FacilityWaypointGraph? graph = EnsureGraph(null);
        if (graph == null || maxNodes <= 0)
        {
            return Array.Empty<Vector3>();
        }

        if (graph.Nodes.Count <= maxNodes)
        {
            return graph.Nodes.Select(node => node.Position).ToArray();
        }

        int stride = Math.Max(1, Mathf.CeilToInt(graph.Nodes.Count / (float)maxNodes));
        List<Vector3> nodes = new(maxNodes);
        for (int i = 0; i < graph.Nodes.Count && nodes.Count < maxNodes; i += stride)
        {
            nodes.Add(graph.Nodes[i].Position);
        }

        return nodes;
    }

    public bool TryFindPath(
        Vector3 startPosition,
        Vector3 targetPosition,
        BotBehaviorDefinition behavior,
        Func<Vector3, Vector3, bool> isSegmentClear,
        IReadOnlyList<RoomGraphBlockedSegment> blockedSegments,
        int nowTick,
        out List<Vector3> path,
        out string reason)
    {
        path = new List<Vector3>();
        reason = "none";

        FacilityWaypointGraph? graph = EnsureGraph(behavior);
        if (graph == null)
        {
            reason = "room-graph-unavailable";
            return false;
        }

        if (!Room.TryGetRoomAtPosition(startPosition, out Room? startRoom)
            || startRoom == null
            || startRoom.IsDestroyed)
        {
            return TryRunStandardBeamSearchFallback(
                graph,
                startPosition,
                targetPosition,
                behavior,
                blockedSegments,
                nowTick,
                "start-room-missing",
                out path,
                out reason);
        }

        if (!Room.TryGetRoomAtPosition(targetPosition, out Room? targetRoom)
            || targetRoom == null
            || targetRoom.IsDestroyed)
        {
            return TryRunStandardBeamSearchFallback(
                graph,
                startPosition,
                targetPosition,
                behavior,
                blockedSegments,
                nowTick,
                "target-room-missing",
                out path,
                out reason);
        }

        List<Room> roomPath;
        try
        {
            roomPath = Room.FindPath(startRoom, targetRoom);
        }
        catch (Exception ex)
        {
            return TryRunStandardBeamSearchFallback(
                graph,
                startPosition,
                targetPosition,
                behavior,
                blockedSegments,
                nowTick,
                $"room-findpath-exception:{ex.GetBaseException().Message}",
                out path,
                out reason);
        }

        if (roomPath == null || roomPath.Count == 0)
        {
            if (startRoom == targetRoom)
            {
                roomPath = new List<Room> { startRoom };
            }
            else if ((startRoom.AdjacentRooms ?? Array.Empty<Room>()).Contains(targetRoom))
            {
                roomPath = new List<Room> { startRoom, targetRoom };
            }
            else
            {
                return TryRunStandardBeamSearchFallback(
                    graph,
                    startPosition,
                    targetPosition,
                    behavior,
                    blockedSegments,
                    nowTick,
                    "room-findpath-empty",
                    out path,
                    out reason);
            }
        }

        HashSet<Room> allowedRooms = new(roomPath);
        if (!graph.NodesByRoom.TryGetValue(startRoom, out List<int> startRoomNodes)
            || !graph.NodesByRoom.TryGetValue(targetRoom, out List<int> targetRoomNodes))
        {
            return TryRunStandardBeamSearchFallback(
                graph,
                startPosition,
                targetPosition,
                behavior,
                blockedSegments,
                nowTick,
                "room-nodes-missing",
                out path,
                out reason);
        }

        List<int> startLinks = FindDynamicLinks(graph, startRoomNodes, allowedRooms, startPosition, isSegmentClear);
        List<int> targetLinks = FindDynamicLinks(graph, targetRoomNodes, allowedRooms, targetPosition, isSegmentClear);
        if (startLinks.Count == 0 || targetLinks.Count == 0)
        {
            if (TryBuildDoorBridgePath(roomPath, startPosition, targetPosition, behavior, blockedSegments, nowTick, out path))
            {
                reason = $"room-graph-door-bridge seed={graph.Seed} rooms={roomPath.Count} nodes={path.Count} attachment=start:{startLinks.Count},target:{targetLinks.Count}";
                return true;
            }

            return TryRunStandardBeamSearchFallback(
                graph,
                startPosition,
                targetPosition,
                behavior,
                blockedSegments,
                nowTick,
                $"dynamic-links start={startLinks.Count} target={targetLinks.Count}",
                out path,
                out reason);
        }

        bool usedBeamSearch = false;
        bool usedRelaxedClutterFallback = false;
        if (behavior.UseFacilityRoomGraphBeamSearch
            && TryRunBeamSearch(
                graph,
                allowedRooms,
                startPosition,
                targetPosition,
                startLinks,
                targetLinks,
                isSegmentClear,
                blockedSegments,
                nowTick,
                behavior.FacilityRoomGraphBeamWidth,
                behavior.FacilityRoomGraphBeamMaxDepth,
                out path))
        {
            usedBeamSearch = true;
        }
        else if (!TryRunAStar(graph, allowedRooms, startPosition, targetPosition, startLinks, targetLinks, isSegmentClear, blockedSegments, nowTick, out path))
        {
            if (TryBuildDoorBridgePath(roomPath, startPosition, targetPosition, behavior, blockedSegments, nowTick, out path))
            {
                reason = $"room-graph-door-bridge seed={graph.Seed} rooms={roomPath.Count} nodes={path.Count}";
                return true;
            }

            usedRelaxedClutterFallback = true;
            Func<Vector3, Vector3, bool> relaxedSegmentCheck = (start, end) => IsShortRoomGraphSegment(start, end);
            if (behavior.UseFacilityRoomGraphBeamSearch
                && TryRunBeamSearch(
                    graph,
                    allowedRooms,
                    startPosition,
                    targetPosition,
                    startLinks,
                    targetLinks,
                    relaxedSegmentCheck,
                    blockedSegments,
                    nowTick,
                    behavior.FacilityRoomGraphBeamWidth,
                    behavior.FacilityRoomGraphBeamMaxDepth,
                    out path))
            {
                usedBeamSearch = true;
            }
            else if (!TryRunAStar(
                         graph,
                         allowedRooms,
                         startPosition,
                         targetPosition,
                         startLinks,
                         targetLinks,
                         relaxedSegmentCheck,
                         blockedSegments,
                         nowTick,
                         out path))
            {
                return TryRunStandardBeamSearchFallback(
                    graph,
                    startPosition,
                    targetPosition,
                    behavior,
                    blockedSegments,
                    nowTick,
                    $"astar-failed rooms={roomPath.Count}",
                    out path,
                    out reason);
            }
        }

        reason = (usedBeamSearch, usedRelaxedClutterFallback) switch
        {
            (true, true) => $"room-graph-beam-clutter-fallback seed={graph.Seed} rooms={roomPath.Count} nodes={path.Count}",
            (true, false) => $"room-graph-beam seed={graph.Seed} rooms={roomPath.Count} nodes={path.Count}",
            (false, true) => $"room-graph-clutter-fallback seed={graph.Seed} rooms={roomPath.Count} nodes={path.Count}",
            _ => $"room-graph seed={graph.Seed} rooms={roomPath.Count} nodes={path.Count}",
        };
        return true;
    }

    private static bool TryBuildDoorBridgePath(
        IReadOnlyList<Room> roomPath,
        Vector3 startPosition,
        Vector3 targetPosition,
        BotBehaviorDefinition behavior,
        IReadOnlyList<RoomGraphBlockedSegment> blockedSegments,
        int nowTick,
        out List<Vector3> path)
    {
        path = new List<Vector3>();
        if (roomPath.Count < 2 || roomPath.Count > 4 || Door.List == null)
        {
            return false;
        }

        Room currentRoom = roomPath[0];
        Vector3 cursor = startPosition;
        for (int i = 1; i < roomPath.Count; i++)
        {
            Room nextRoom = roomPath[i];
            if (!TryFindDoorBridgeBetweenRooms(
                    currentRoom,
                    nextRoom,
                    cursor,
                    behavior,
                    blockedSegments,
                    nowTick,
                    out Vector3 approach,
                    out Vector3 center,
                    out Vector3 exit))
            {
                return false;
            }

            AddDistinctPathPoint(path, approach);
            AddDistinctPathPoint(path, center);
            AddDistinctPathPoint(path, exit);
            cursor = exit;
            currentRoom = nextRoom;
        }

        if (IsRoomGraphSegmentBlocked(cursor, targetPosition, blockedSegments, nowTick))
        {
            return false;
        }

        AddDistinctPathPoint(path, targetPosition);
        return path.Count > 0;
    }

    private static bool TryFindDoorBridgeBetweenRooms(
        Room leftRoom,
        Room rightRoom,
        Vector3 fromPosition,
        BotBehaviorDefinition behavior,
        IReadOnlyList<RoomGraphBlockedSegment> blockedSegments,
        int nowTick,
        out Vector3 approach,
        out Vector3 center,
        out Vector3 exit)
    {
        approach = default;
        center = default;
        exit = default;
        bool found = false;
        float bestScore = float.PositiveInfinity;
        foreach (Door door in Door.List)
        {
            if (door == null || door.IsDestroyed || door.Rooms == null)
            {
                continue;
            }

            bool connectsLeft = door.Rooms.Contains(leftRoom);
            bool connectsRight = door.Rooms.Contains(rightRoom);
            if (!connectsLeft || !connectsRight)
            {
                continue;
            }

            foreach ((Vector3 candidateApproach, Vector3 candidateCenter, Vector3 candidateExit) in EnumerateDoorBridgeCandidates(door, leftRoom, rightRoom, behavior))
            {
                if (IsRoomGraphSegmentBlocked(fromPosition, candidateApproach, blockedSegments, nowTick)
                    || IsRoomGraphSegmentBlocked(candidateApproach, candidateCenter, blockedSegments, nowTick)
                    || IsRoomGraphSegmentBlocked(candidateCenter, candidateExit, blockedSegments, nowTick))
                {
                    continue;
                }

                float score = HorizontalDistance(fromPosition, candidateApproach)
                    + HorizontalDistance(candidateApproach, candidateCenter) * 0.35f
                    + HorizontalDistance(candidateCenter, candidateExit) * 0.35f
                    + HorizontalDistance(candidateExit, door.Position) * 0.2f;
                if (score >= bestScore)
                {
                    continue;
                }

                found = true;
                bestScore = score;
                approach = candidateApproach;
                center = candidateCenter;
                exit = candidateExit;
            }
        }

        return found;
    }

    private static IEnumerable<(Vector3 Approach, Vector3 Center, Vector3 Exit)> EnumerateDoorBridgeCandidates(
        Door door,
        Room leftRoom,
        Room rightRoom,
        BotBehaviorDefinition behavior)
    {
        GetDoorAxes(door, out Vector3 forward, out Vector3 right);
        float[] sideDistances = { 1.65f, 2.15f, 1.15f, 2.75f };
        Vector3[] lateralOffsets =
        {
            Vector3.zero,
            right * 0.22f,
            -right * 0.22f,
        };

        foreach (float distance in sideDistances)
        {
            foreach (Vector3 lateral in lateralOffsets)
            {
                Vector3 leftProbe = door.Position - forward * distance + lateral;
                Vector3 rightProbe = door.Position + forward * distance + lateral;
                if (!TryResolveNodePosition(new[] { leftRoom }, leftProbe, behavior, out Vector3 leftPoint)
                    || !TryResolveNodePosition(new[] { rightRoom }, rightProbe, behavior, out Vector3 rightPoint))
                {
                    if (!TryResolveNodePosition(new[] { leftRoom }, rightProbe, behavior, out leftPoint)
                        || !TryResolveNodePosition(new[] { rightRoom }, leftProbe, behavior, out rightPoint))
                    {
                        continue;
                    }
                }

                Vector3 centerPoint = (leftPoint + rightPoint) * 0.5f;
                centerPoint.y = Mathf.Lerp(leftPoint.y, rightPoint.y, 0.5f);
                if (TryResolveNodePosition(new[] { leftRoom, rightRoom }, door.Position + lateral, behavior, out Vector3 resolvedCenter))
                {
                    centerPoint = resolvedCenter;
                }

                yield return (leftPoint, centerPoint, rightPoint);
            }
        }
    }

    private static void GetDoorAxes(Door door, out Vector3 forward, out Vector3 right)
    {
        Transform? transform = door.Transform;
        forward = transform != null ? transform.forward : Vector3.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f)
        {
            forward = Vector3.forward;
        }

        forward.Normalize();
        right = transform != null ? transform.right : Vector3.right;
        right.y = 0f;
        if (right.sqrMagnitude < 0.0001f)
        {
            right = Vector3.Cross(Vector3.up, forward);
        }

        right.Normalize();
    }

    private static void AddDistinctPathPoint(List<Vector3> path, Vector3 point)
    {
        if (path.Count > 0
            && HorizontalDistance(path[path.Count - 1], point) <= 0.35f
            && Mathf.Abs(path[path.Count - 1].y - point.y) <= 0.75f)
        {
            return;
        }

        path.Add(point);
    }

    private FacilityWaypointGraph? EnsureGraph(BotBehaviorDefinition? behavior)
    {
        int seed = LabApi.Features.Wrappers.Map.Seed;
        int roomCount = Room.List?.Count ?? 0;
        if (_graph != null && _graph.Seed == seed && _graph.SourceRoomCount == roomCount)
        {
            return _graph;
        }

        if (behavior == null)
        {
            return _graph;
        }

        _graph = BuildGraph(behavior);
        return _graph;
    }

    private static FacilityWaypointGraph? BuildGraph(BotBehaviorDefinition behavior)
    {
        if (Room.List == null || Room.List.Count == 0)
        {
            return null;
        }

        Dictionary<string, RoomNavTemplate> templates = FacilityNavMeshService.LoadEmbeddedRoomTemplates();
        FacilityWaypointGraph graph = new(LabApi.Features.Wrappers.Map.Seed, Room.List.Count);
        foreach (Room room in Room.List)
        {
            if (room == null || room.IsDestroyed || room.Base == null || room.Zone == FacilityZone.None)
            {
                continue;
            }

            string? templateName = FacilityNavMeshService.ResolveTemplateName(room.Base, templates);
            if (templateName != null && templates.TryGetValue(templateName, out RoomNavTemplate template))
            {
                Matrix4x4 roomMatrix = room.Base.transform.localToWorldMatrix;
                int addedTemplateNodes = AddTemplateGridNodes(graph, room, roomMatrix, template, behavior);

                if (addedTemplateNodes > 0)
                {
                    graph.TemplateRoomCount++;
                    graph.TemplateNodeCount += addedTemplateNodes;
                }
                else if (TryAddNode(graph, room, new[] { room }, room.Position, FacilityWaypointKind.RoomCenter, behavior))
                {
                    graph.MissingTemplateRoomCount++;
                }
            }
            else
            {
                graph.MissingTemplateRoomCount++;
                TryAddNode(graph, room, new[] { room }, room.Position, FacilityWaypointKind.RoomCenter, behavior);
            }
        }

        LinkTemplateNodesWithinRooms(graph);
        AddDoorAnchorNodes(graph, behavior);
        LinkAdjacentRooms(graph);
        graph.FinalizeLinkCount();
        return graph.Nodes.Count == 0 ? null : graph;
    }

    private static int AddTemplateGridNodes(
        FacilityWaypointGraph graph,
        Room room,
        Matrix4x4 roomMatrix,
        RoomNavTemplate template,
        BotBehaviorDefinition behavior)
    {
        int added = 0;
        int maxNodes = behavior.FacilityRoomGraphMaxNodesPerRoom;
        foreach (RoomNavBox box in template.Boxes)
        {
            foreach (Vector3 localPoint in EnumerateTemplateGridPoints(box, behavior))
            {
                if (maxNodes > 0
                    && graph.NodesByRoom.TryGetValue(room, out List<int> existing)
                    && existing.Count >= maxNodes)
                {
                    return added;
                }

                Vector3 rawPosition = roomMatrix.MultiplyPoint3x4(localPoint + (Vector3.up * TemplateNodeVerticalOffset));
                if (TryAddNode(graph, room, new[] { room }, rawPosition, FacilityWaypointKind.Template, behavior))
                {
                    added++;
                }
            }
        }

        return added;
    }

    private static IEnumerable<Vector3> EnumerateTemplateGridPoints(RoomNavBox box, BotBehaviorDefinition behavior)
    {
        yield return box.Center;
        if (!behavior.FacilityRoomGraphDenseGridEnabled)
        {
            yield break;
        }

        float spacing = Mathf.Clamp(behavior.FacilityRoomGraphGridSpacing, 0.45f, 1.5f);
        int xCount = Math.Max(1, Mathf.CeilToInt(Mathf.Max(0.1f, box.Size.x) / spacing));
        int zCount = Math.Max(1, Mathf.CeilToInt(Mathf.Max(0.1f, box.Size.z) / spacing));
        if (xCount == 1 && zCount == 1)
        {
            yield break;
        }

        float stepX = box.Size.x / xCount;
        float stepZ = box.Size.z / zCount;
        float minX = box.Center.x - (box.Size.x * 0.5f);
        float minZ = box.Center.z - (box.Size.z * 0.5f);
        for (int x = 0; x < xCount; x++)
        {
            for (int z = 0; z < zCount; z++)
            {
                Vector3 point = new(
                    minX + stepX * (x + 0.5f),
                    box.Center.y,
                    minZ + stepZ * (z + 0.5f));
                if (HorizontalDistance(point, box.Center) <= DuplicateNodeHorizontalDistance
                    && Mathf.Abs(point.y - box.Center.y) <= DuplicateNodeVerticalDistance)
                {
                    continue;
                }

                yield return point;
            }
        }
    }

    private static void LinkTemplateNodesWithinRooms(FacilityWaypointGraph graph)
    {
        foreach (List<int> nodeIndexes in graph.NodesByRoom.Values)
        {
            Dictionary<(int X, int Y, int Z), List<int>> buckets = new();
            foreach (int nodeIndex in nodeIndexes)
            {
                (int X, int Y, int Z) key = GetLinkBucketKey(graph.Nodes[nodeIndex].Position);
                if (!buckets.TryGetValue(key, out List<int> bucket))
                {
                    bucket = new List<int>();
                    buckets[key] = bucket;
                }

                bucket.Add(nodeIndex);
            }

            foreach (int leftIndex in nodeIndexes)
            {
                FacilityWaypointNode left = graph.Nodes[leftIndex];
                (int X, int Y, int Z) key = GetLinkBucketKey(left.Position);
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (!buckets.TryGetValue((key.X + dx, key.Y + dy, key.Z + dz), out List<int> candidates))
                            {
                                continue;
                            }

                            foreach (int rightIndex in candidates)
                            {
                                if (rightIndex <= leftIndex)
                                {
                                    continue;
                                }

                                FacilityWaypointNode right = graph.Nodes[rightIndex];
                                float horizontal = HorizontalDistance(left.Position, right.Position);
                                if (horizontal > InRoomLinkDistance
                                    || Mathf.Abs(left.Position.y - right.Position.y) > InRoomMaxVerticalDelta)
                                {
                                    continue;
                                }

                                graph.AddUndirectedLink(left.Index, right.Index, horizontal);
                            }
                        }
                    }
                }
            }
        }
    }

    private static (int X, int Y, int Z) GetLinkBucketKey(Vector3 position)
    {
        return (
            Mathf.FloorToInt(position.x / InRoomLinkDistance),
            Mathf.FloorToInt(position.y / InRoomMaxVerticalDelta),
            Mathf.FloorToInt(position.z / InRoomLinkDistance));
    }

    private static void AddDoorAnchorNodes(FacilityWaypointGraph graph, BotBehaviorDefinition behavior)
    {
        foreach (Door door in Door.List)
        {
            if (door == null || door.IsDestroyed || door.Rooms == null || door.Rooms.Length == 0)
            {
                continue;
            }

            List<Room> rooms = door.Rooms
                .Where(room => room != null && !room.IsDestroyed && graph.NodesByRoom.ContainsKey(room))
                .Distinct()
                .ToList();
            if (rooms.Count == 0)
            {
                continue;
            }

            GetDoorAxes(door, out Vector3 forward, out Vector3 right);
            int passageNode = -1;
            if (TryAddNode(
                    graph,
                    rooms[0],
                    rooms,
                    door.Position + (Vector3.up * TemplateNodeVerticalOffset),
                    FacilityWaypointKind.DoorPassage,
                    behavior,
                    out passageNode))
            {
                graph.DoorAnchorCount++;
            }

            List<int> doorSideNodes = new();
            foreach (Room room in rooms)
            {
                if (!TryAddDoorSideNode(graph, door, room, rooms, forward, right, behavior, out int sideNode))
                {
                    continue;
                }

                doorSideNodes.Add(sideNode);
                graph.DoorAnchorCount++;
                if (passageNode >= 0)
                {
                    graph.AddUndirectedLink(sideNode, passageNode, Math.Max(0.5f, HorizontalDistance(graph.Nodes[sideNode].Position, graph.Nodes[passageNode].Position)));
                }

                if (!graph.NodesByRoom.TryGetValue(room, out List<int> nodeIndexes))
                {
                    continue;
                }

                foreach (int candidate in nodeIndexes
                             .Where(index => graph.Nodes[index].Kind == FacilityWaypointKind.Template
                                 || graph.Nodes[index].Kind == FacilityWaypointKind.RoomCenter)
                             .OrderBy(index => HorizontalDistance(graph.Nodes[index].Position, door.Position))
                             .Take(6))
                {
                    float distance = HorizontalDistance(graph.Nodes[candidate].Position, door.Position);
                    if (distance <= DoorAnchorLinkDistance)
                    {
                        graph.AddUndirectedLink(sideNode, candidate, Math.Max(0.5f, distance));
                    }
                }
            }

            for (int i = 0; i < doorSideNodes.Count; i++)
            {
                for (int j = i + 1; j < doorSideNodes.Count; j++)
                {
                    float distance = HorizontalDistance(graph.Nodes[doorSideNodes[i]].Position, graph.Nodes[doorSideNodes[j]].Position);
                    if (distance <= AdjacentRoomLinkDistance)
                    {
                        graph.AddUndirectedLink(doorSideNodes[i], doorSideNodes[j], Math.Max(0.5f, distance));
                    }
                }
            }
        }
    }

    private static bool TryAddDoorSideNode(
        FacilityWaypointGraph graph,
        Door door,
        Room room,
        IReadOnlyCollection<Room> allDoorRooms,
        Vector3 forward,
        Vector3 right,
        BotBehaviorDefinition behavior,
        out int sideNode)
    {
        sideNode = -1;
        float[] distances = { 1.55f, 2.05f, 1.15f, 2.65f, 3.2f };
        Vector3[] lateralOffsets =
        {
            Vector3.zero,
            right * 0.22f,
            -right * 0.22f,
        };

        foreach (float distance in distances)
        {
            foreach (Vector3 lateral in lateralOffsets)
            {
                Vector3 positiveProbe = door.Position + forward * distance + lateral + Vector3.up * TemplateNodeVerticalOffset;
                if (TryAddNode(graph, room, new[] { room }, positiveProbe, FacilityWaypointKind.DoorApproach, behavior, out sideNode))
                {
                    return true;
                }

                Vector3 negativeProbe = door.Position - forward * distance + lateral + Vector3.up * TemplateNodeVerticalOffset;
                if (TryAddNode(graph, room, new[] { room }, negativeProbe, FacilityWaypointKind.DoorApproach, behavior, out sideNode))
                {
                    return true;
                }
            }
        }

        return TryAddNode(
            graph,
            room,
            allDoorRooms,
            door.Position + Vector3.up * TemplateNodeVerticalOffset,
            FacilityWaypointKind.DoorApproach,
            behavior,
            out sideNode);
    }

    private static bool TryAddNode(
        FacilityWaypointGraph graph,
        Room ownerRoom,
        IReadOnlyCollection<Room> acceptedRooms,
        Vector3 rawPosition,
        FacilityWaypointKind kind,
        BotBehaviorDefinition behavior)
    {
        return TryAddNode(graph, ownerRoom, acceptedRooms, rawPosition, kind, behavior, out _);
    }

    private static bool TryAddNode(
        FacilityWaypointGraph graph,
        Room ownerRoom,
        IReadOnlyCollection<Room> acceptedRooms,
        Vector3 rawPosition,
        FacilityWaypointKind kind,
        BotBehaviorDefinition behavior,
        out int nodeIndex)
    {
        nodeIndex = -1;
        if (!TryResolveNodePosition(acceptedRooms, rawPosition, behavior, out Vector3 position))
        {
            graph.RejectedNodeCount++;
            return false;
        }

        if ((kind == FacilityWaypointKind.Template || kind == FacilityWaypointKind.RoomCenter)
            && HasNearbyRoomNode(graph, ownerRoom, position))
        {
            return false;
        }

        nodeIndex = graph.AddNode(ownerRoom, position, kind);
        return true;
    }

    private static bool HasNearbyRoomNode(FacilityWaypointGraph graph, Room room, Vector3 position)
    {
        if (!graph.NodesByRoom.TryGetValue(room, out List<int> nodeIndexes))
        {
            return false;
        }

        foreach (int index in nodeIndexes)
        {
            Vector3 existing = graph.Nodes[index].Position;
            if (HorizontalDistance(existing, position) <= DuplicateNodeHorizontalDistance
                && Mathf.Abs(existing.y - position.y) <= DuplicateNodeVerticalDistance)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveNodePosition(
        IReadOnlyCollection<Room> acceptedRooms,
        Vector3 rawPosition,
        BotBehaviorDefinition behavior,
        out Vector3 position)
    {
        position = rawPosition;
        if (TryProjectToAcceptedRoomFloor(rawPosition, acceptedRooms, out Vector3 floorPosition))
        {
            Vector3 candidate = floorPosition + (Vector3.up * TemplateNodeVerticalOffset);
            if (HasStandingClearance(floorPosition, behavior)
                && IsInsideAcceptedRoom(candidate, acceptedRooms))
            {
                position = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryProjectToAcceptedRoomFloor(
        Vector3 rawPosition,
        IReadOnlyCollection<Room> acceptedRooms,
        out Vector3 floorPosition)
    {
        floorPosition = default;
        Vector3 origin = rawPosition + (Vector3.up * NodeFloorProbeUp);
        RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, NodeFloorProbeDown, ~0, QueryTriggerInteraction.Ignore);
        if (hits == null || hits.Length == 0)
        {
            return false;
        }

        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider == null || hit.normal.y < 0.55f)
            {
                continue;
            }

            if (!IsInsideAcceptedRoom(hit.point + (Vector3.up * NodeRoomProbeHeight), acceptedRooms))
            {
                continue;
            }

            floorPosition = hit.point;
            return true;
        }

        return false;
    }

    private static bool HasStandingClearance(Vector3 floorPosition, BotBehaviorDefinition behavior)
    {
        float radius = Mathf.Clamp(behavior.FacilityRuntimeNavMeshAgentRadius, 0.22f, 0.42f);
        float height = Mathf.Clamp(behavior.FacilityRuntimeNavMeshAgentHeight, 1.45f, 2.2f);
        Vector3 bottom = floorPosition + (Vector3.up * (radius + 0.22f));
        Vector3 top = floorPosition + (Vector3.up * Math.Max(radius + 0.45f, height - radius));
        return !Physics.CheckCapsule(
            bottom,
            top,
            radius,
            ~0,
            QueryTriggerInteraction.Ignore);
    }

    private static bool IsInsideAcceptedRoom(Vector3 position, IReadOnlyCollection<Room> acceptedRooms)
    {
        if (acceptedRooms.Count == 0
            || !Room.TryGetRoomAtPosition(position + (Vector3.up * NodeRoomProbeHeight), out Room? resolved)
            || resolved == null
            || resolved.IsDestroyed)
        {
            return false;
        }

        return acceptedRooms.Contains(resolved);
    }

    private static void LinkAdjacentRooms(FacilityWaypointGraph graph)
    {
        HashSet<string> linkedPairs = new(StringComparer.Ordinal);
        foreach (Room room in graph.Rooms)
        {
            IReadOnlyCollection<Room> adjacentRooms = room.AdjacentRooms ?? Array.Empty<Room>();
            foreach (Room adjacent in adjacentRooms)
            {
                if (adjacent == null || adjacent.IsDestroyed || !graph.NodesByRoom.ContainsKey(adjacent))
                {
                    continue;
                }

                string key = GetRoomPairKey(room, adjacent);
                if (!linkedPairs.Add(key))
                {
                    continue;
                }

                if (!TryFindClosestRoomNodePair(graph, room, adjacent, out int left, out int right, out float distance))
                {
                    continue;
                }

                graph.AddUndirectedLink(left, right, Math.Max(0.5f, distance));
            }
        }
    }

    private static bool TryFindClosestRoomNodePair(
        FacilityWaypointGraph graph,
        Room leftRoom,
        Room rightRoom,
        out int leftNode,
        out int rightNode,
        out float distance)
    {
        leftNode = -1;
        rightNode = -1;
        distance = float.PositiveInfinity;
        if (!graph.NodesByRoom.TryGetValue(leftRoom, out List<int> leftNodes)
            || !graph.NodesByRoom.TryGetValue(rightRoom, out List<int> rightNodes))
        {
            return false;
        }

        foreach (int left in leftNodes)
        {
            foreach (int right in rightNodes)
            {
                float candidateDistance = HorizontalDistance(graph.Nodes[left].Position, graph.Nodes[right].Position);
                if (candidateDistance >= distance || candidateDistance > AdjacentRoomLinkDistance)
                {
                    continue;
                }

                float verticalDelta = Mathf.Abs(graph.Nodes[left].Position.y - graph.Nodes[right].Position.y);
                if (verticalDelta > 4.0f)
                {
                    continue;
                }

                leftNode = left;
                rightNode = right;
                distance = candidateDistance;
            }
        }

        return leftNode >= 0 && rightNode >= 0;
    }

    private static List<int> FindDynamicLinks(
        FacilityWaypointGraph graph,
        IReadOnlyList<int> roomNodes,
        ISet<Room> allowedRooms,
        Vector3 position,
        Func<Vector3, Vector3, bool> isSegmentClear)
    {
        List<int> clearLinks = new();
        List<int> fallbackLinks = new();
        foreach (int index in roomNodes
                     .Where(index => allowedRooms.Contains(graph.Nodes[index].Room))
                     .OrderBy(index => HorizontalDistance(position, graph.Nodes[index].Position))
                     .Take(DynamicLinkCount * 2))
        {
            float distance = HorizontalDistance(position, graph.Nodes[index].Position);
            if (distance > DynamicLinkDistance)
            {
                continue;
            }

            if (Mathf.Abs(position.y - graph.Nodes[index].Position.y) > 5.0f)
            {
                continue;
            }

            if (isSegmentClear(position, graph.Nodes[index].Position))
            {
                clearLinks.Add(index);
                if (clearLinks.Count >= DynamicLinkCount)
                {
                    break;
                }
            }
            else if (fallbackLinks.Count < DynamicLinkCount)
            {
                fallbackLinks.Add(index);
            }
        }

        return clearLinks.Count > 0 ? clearLinks : fallbackLinks.Take(Math.Max(1, DynamicLinkCount / 2)).ToList();
    }

    private static bool TryRunAStar(
        FacilityWaypointGraph graph,
        ISet<Room> allowedRooms,
        Vector3 startPosition,
        Vector3 targetPosition,
        IReadOnlyList<int> startLinks,
        IReadOnlyCollection<int> targetLinks,
        Func<Vector3, Vector3, bool> isSegmentClear,
        IReadOnlyList<RoomGraphBlockedSegment> blockedSegments,
        int nowTick,
        out List<Vector3> path)
    {
        path = new List<Vector3>();
        int nodeCount = graph.Nodes.Count;
        int startIndex = nodeCount;
        int targetIndex = nodeCount + 1;
        int totalCount = nodeCount + 2;
        float[] gScore = Enumerable.Repeat(float.PositiveInfinity, totalCount).ToArray();
        float[] fScore = Enumerable.Repeat(float.PositiveInfinity, totalCount).ToArray();
        int[] cameFrom = Enumerable.Repeat(-1, totalCount).ToArray();
        bool[] closed = new bool[totalCount];
        List<int> open = new() { startIndex };

        gScore[startIndex] = 0f;
        fScore[startIndex] = HorizontalDistance(startPosition, targetPosition);
        HashSet<int> targetLinkSet = new(targetLinks);

        while (open.Count > 0)
        {
            int current = PopBestOpen(open, fScore);
            if (current == targetIndex)
            {
                path = ReconstructPath(graph, cameFrom, current, startIndex, targetIndex, startPosition, targetPosition);
                return path.Count > 0;
            }

            closed[current] = true;
            foreach ((int next, float cost) in EnumerateNeighbors(graph, allowedRooms, startLinks, targetLinkSet, current, startIndex, targetIndex, startPosition, targetPosition, isSegmentClear, blockedSegments, nowTick))
            {
                if (closed[next])
                {
                    continue;
                }

                float tentative = gScore[current] + cost;
                if (tentative >= gScore[next])
                {
                    continue;
                }

                cameFrom[next] = current;
                gScore[next] = tentative;
                fScore[next] = tentative + HorizontalDistance(GetNodePosition(graph, next, startIndex, targetIndex, startPosition, targetPosition), targetPosition);
                if (!open.Contains(next))
                {
                    open.Add(next);
                }
            }
        }

        return false;
    }

    private static bool TryRunBeamSearch(
        FacilityWaypointGraph graph,
        ISet<Room> allowedRooms,
        Vector3 startPosition,
        Vector3 targetPosition,
        IReadOnlyList<int> startLinks,
        IReadOnlyCollection<int> targetLinks,
        Func<Vector3, Vector3, bool> isSegmentClear,
        IReadOnlyList<RoomGraphBlockedSegment> blockedSegments,
        int nowTick,
        int configuredBeamWidth,
        int configuredMaxDepth,
        out List<Vector3> path)
    {
        path = new List<Vector3>();
        int nodeCount = graph.Nodes.Count;
        int startIndex = nodeCount;
        int targetIndex = nodeCount + 1;
        int totalCount = nodeCount + 2;
        int beamWidth = Math.Max(2, Math.Min(64, configuredBeamWidth));
        int maxDepth = Math.Max(4, Math.Min(Math.Max(4, totalCount), configuredMaxDepth));
        float[] bestCost = Enumerable.Repeat(float.PositiveInfinity, totalCount).ToArray();
        int[] cameFrom = Enumerable.Repeat(-1, totalCount).ToArray();
        HashSet<int> targetLinkSet = new(targetLinks);
        List<BeamSearchCandidate> beam = new()
        {
            new BeamSearchCandidate(startIndex, 0f, HorizontalDistance(startPosition, targetPosition)),
        };

        bestCost[startIndex] = 0f;
        for (int depth = 0; depth < maxDepth && beam.Count > 0; depth++)
        {
            List<BeamSearchCandidate> candidates = new();
            foreach (BeamSearchCandidate current in beam)
            {
                foreach ((int next, float cost) in EnumerateNeighbors(
                             graph,
                             allowedRooms,
                             startLinks,
                             targetLinkSet,
                             current.Node,
                             startIndex,
                             targetIndex,
                             startPosition,
                             targetPosition,
                             isSegmentClear,
                             blockedSegments,
                             nowTick))
                {
                    float tentativeCost = current.Cost + cost;
                    if (tentativeCost >= bestCost[next] - 0.05f)
                    {
                        continue;
                    }

                    cameFrom[next] = current.Node;
                    bestCost[next] = tentativeCost;
                    if (next == targetIndex)
                    {
                        path = ReconstructPath(graph, cameFrom, next, startIndex, targetIndex, startPosition, targetPosition);
                        return path.Count > 0;
                    }

                    Vector3 nextPosition = GetNodePosition(graph, next, startIndex, targetIndex, startPosition, targetPosition);
                    float heuristic = HorizontalDistance(nextPosition, targetPosition)
                        + Mathf.Abs(nextPosition.y - targetPosition.y) * 0.15f;
                    candidates.Add(new BeamSearchCandidate(next, tentativeCost, tentativeCost + heuristic));
                }
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            beam = candidates
                .OrderBy(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Cost)
                .Take(beamWidth)
                .ToList();
        }

        return false;
    }

    private static bool TryRunStandardBeamSearchFallback(
        FacilityWaypointGraph graph,
        Vector3 startPosition,
        Vector3 targetPosition,
        BotBehaviorDefinition behavior,
        IReadOnlyList<RoomGraphBlockedSegment> blockedSegments,
        int nowTick,
        string fallbackFrom,
        out List<Vector3> path,
        out string reason)
    {
        path = new List<Vector3>();
        if (!behavior.UseFacilityRoomGraphBeamSearch)
        {
            reason = $"{fallbackFrom};standard-beam-disabled";
            return false;
        }

        HashSet<Room> allowedRooms = new(graph.Rooms);
        if (allowedRooms.Count == 0)
        {
            reason = $"{fallbackFrom};standard-beam-no-rooms";
            return false;
        }

        List<int> startLinks = FindStandardBeamLinks(graph, allowedRooms, startPosition);
        List<int> targetLinks = FindStandardBeamLinks(graph, allowedRooms, targetPosition);
        if (startLinks.Count == 0 || targetLinks.Count == 0)
        {
            reason = $"{fallbackFrom};standard-beam-links start={startLinks.Count} target={targetLinks.Count}";
            return false;
        }

        if (!TryRunBeamSearch(
                graph,
                allowedRooms,
                startPosition,
                targetPosition,
                startLinks,
                targetLinks,
                AllowAnySegment,
                blockedSegments,
                nowTick,
                behavior.FacilityRoomGraphBeamWidth,
                behavior.FacilityRoomGraphBeamMaxDepth,
                out path))
        {
            reason = $"{fallbackFrom};standard-beam-failed start={startLinks.Count} target={targetLinks.Count}";
            return false;
        }

        reason = $"room-graph-beam-standard fallback={fallbackFrom} seed={graph.Seed} rooms={allowedRooms.Count} nodes={path.Count}";
        return true;
    }

    private static List<int> FindStandardBeamLinks(
        FacilityWaypointGraph graph,
        ISet<Room> allowedRooms,
        Vector3 position)
    {
        List<int> allNodeIndexes = Enumerable.Range(0, graph.Nodes.Count).ToList();
        List<int> links = FindDynamicLinks(graph, allNodeIndexes, allowedRooms, position, AllowAnySegment);
        if (links.Count > 0)
        {
            return links;
        }

        links = FindNearestGraphLinks(graph, allowedRooms, position, maxVerticalDelta: 10.0f);
        return links.Count > 0
            ? links
            : FindNearestGraphLinks(graph, allowedRooms, position, maxVerticalDelta: float.PositiveInfinity);
    }

    private static List<int> FindNearestGraphLinks(
        FacilityWaypointGraph graph,
        ISet<Room> allowedRooms,
        Vector3 position,
        float maxVerticalDelta)
    {
        return graph.Nodes
            .Where(node => allowedRooms.Contains(node.Room)
                && Mathf.Abs(position.y - node.Position.y) <= maxVerticalDelta)
            .OrderBy(node => HorizontalDistance(position, node.Position))
            .Take(DynamicLinkCount)
            .Select(node => node.Index)
            .ToList();
    }

    private static IEnumerable<(int next, float cost)> EnumerateNeighbors(
        FacilityWaypointGraph graph,
        ISet<Room> allowedRooms,
        IReadOnlyList<int> startLinks,
        ISet<int> targetLinks,
        int current,
        int startIndex,
        int targetIndex,
        Vector3 startPosition,
        Vector3 targetPosition,
        Func<Vector3, Vector3, bool> isSegmentClear,
        IReadOnlyList<RoomGraphBlockedSegment> blockedSegments,
        int nowTick)
    {
        if (current == startIndex)
        {
            foreach (int node in startLinks)
            {
                Vector3 nextPosition = graph.Nodes[node].Position;
                if (!IsRoomGraphSegmentBlocked(startPosition, nextPosition, blockedSegments, nowTick)
                    && isSegmentClear(startPosition, nextPosition))
                {
                    yield return (node, HorizontalDistance(startPosition, nextPosition));
                }
            }

            yield break;
        }

        if (current == targetIndex)
        {
            yield break;
        }

        FacilityWaypointNode currentNode = graph.Nodes[current];
        if (!allowedRooms.Contains(currentNode.Room))
        {
            yield break;
        }

        if (targetLinks.Contains(current))
        {
            if (!IsRoomGraphSegmentBlocked(currentNode.Position, targetPosition, blockedSegments, nowTick)
                && isSegmentClear(currentNode.Position, targetPosition))
            {
                yield return (targetIndex, HorizontalDistance(currentNode.Position, targetPosition));
            }
        }

        foreach (FacilityWaypointLink link in currentNode.Links)
        {
            FacilityWaypointNode nextNode = graph.Nodes[link.To];
            if (allowedRooms.Contains(nextNode.Room)
                && !IsRoomGraphSegmentBlocked(currentNode.Position, nextNode.Position, blockedSegments, nowTick)
                && (IsDoorTraversalLink(currentNode, nextNode) || isSegmentClear(currentNode.Position, nextNode.Position)))
            {
                yield return (link.To, link.Cost);
            }
        }
    }

    private static bool IsDoorTraversalLink(FacilityWaypointNode left, FacilityWaypointNode right)
    {
        bool leftDoor = left.Kind == FacilityWaypointKind.DoorApproach
            || left.Kind == FacilityWaypointKind.DoorPassage;
        bool rightDoor = right.Kind == FacilityWaypointKind.DoorApproach
            || right.Kind == FacilityWaypointKind.DoorPassage;
        return leftDoor && rightDoor
            && Mathf.Abs(left.Position.y - right.Position.y) <= 1.4f
            && HorizontalDistance(left.Position, right.Position) <= AdjacentRoomLinkDistance;
    }

    private static bool IsShortRoomGraphSegment(Vector3 start, Vector3 end)
    {
        return HorizontalDistance(start, end) <= 1.15f
            && Mathf.Abs(start.y - end.y) <= 0.75f;
    }

    private static bool IsRoomGraphSegmentBlocked(
        Vector3 start,
        Vector3 end,
        IReadOnlyList<RoomGraphBlockedSegment> blockedSegments,
        int nowTick)
    {
        if (blockedSegments.Count == 0)
        {
            return false;
        }

        foreach (RoomGraphBlockedSegment segment in blockedSegments)
        {
            if (segment.IsExpired(nowTick))
            {
                continue;
            }

            if ((IsSameRoomGraphPoint(start, segment.From) && IsSameRoomGraphPoint(end, segment.To))
                || (IsSameRoomGraphPoint(start, segment.To) && IsSameRoomGraphPoint(end, segment.From)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AllowAnySegment(Vector3 start, Vector3 end)
    {
        return true;
    }

    private static bool IsSameRoomGraphPoint(Vector3 left, Vector3 right)
    {
        return HorizontalDistance(left, right) <= BlockedSegmentMatchDistance
            && Mathf.Abs(left.y - right.y) <= BlockedSegmentVerticalTolerance;
    }

    private static int PopBestOpen(List<int> open, IReadOnlyList<float> fScore)
    {
        int bestOpenIndex = 0;
        int bestNode = open[0];
        for (int i = 1; i < open.Count; i++)
        {
            int candidate = open[i];
            if (fScore[candidate] < fScore[bestNode])
            {
                bestOpenIndex = i;
                bestNode = candidate;
            }
        }

        open.RemoveAt(bestOpenIndex);
        return bestNode;
    }

    private static List<Vector3> ReconstructPath(
        FacilityWaypointGraph graph,
        IReadOnlyList<int> cameFrom,
        int current,
        int startIndex,
        int targetIndex,
        Vector3 startPosition,
        Vector3 targetPosition)
    {
        List<Vector3> points = new();
        while (current >= 0)
        {
            if (current != startIndex)
            {
                points.Add(GetNodePosition(graph, current, startIndex, targetIndex, startPosition, targetPosition));
            }

            current = cameFrom[current];
        }

        points.Reverse();
        return points;
    }

    private static Vector3 GetNodePosition(
        FacilityWaypointGraph graph,
        int index,
        int startIndex,
        int targetIndex,
        Vector3 startPosition,
        Vector3 targetPosition)
    {
        if (index == startIndex)
        {
            return startPosition;
        }

        return index == targetIndex ? targetPosition : graph.Nodes[index].Position;
    }

    private static string GetRoomPairKey(Room left, Room right)
    {
        int leftId = left.Base != null ? left.Base.GetInstanceID() : left.GetHashCode();
        int rightId = right.Base != null ? right.Base.GetInstanceID() : right.GetHashCode();
        return leftId <= rightId ? $"{leftId}:{rightId}" : $"{rightId}:{leftId}";
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        left.y = 0f;
        right.y = 0f;
        return Vector3.Distance(left, right);
    }
}

internal enum FacilityWaypointKind
{
    Template,
    RoomCenter,
    DoorApproach,
    DoorPassage,
}

internal readonly struct BeamSearchCandidate
{
    public BeamSearchCandidate(int node, float cost, float score)
    {
        Node = node;
        Cost = cost;
        Score = score;
    }

    public int Node { get; }

    public float Cost { get; }

    public float Score { get; }
}

internal sealed class FacilityWaypointGraph
{
    public FacilityWaypointGraph(int seed, int sourceRoomCount)
    {
        Seed = seed;
        SourceRoomCount = sourceRoomCount;
    }

    public int Seed { get; }

    public int SourceRoomCount { get; }

    public List<FacilityWaypointNode> Nodes { get; } = new();

    public Dictionary<Room, List<int>> NodesByRoom { get; } = new();

    public List<Room> Rooms { get; } = new();

    public int LinkCount { get; private set; }

    public int TemplateRoomCount { get; set; }

    public int TemplateNodeCount { get; set; }

    public int MissingTemplateRoomCount { get; set; }

    public int DoorAnchorCount { get; set; }

    public int RejectedNodeCount { get; set; }

    public int AddNode(Room room, Vector3 position, FacilityWaypointKind kind)
    {
        int index = Nodes.Count;
        Nodes.Add(new FacilityWaypointNode(index, room, position, kind));
        if (!NodesByRoom.TryGetValue(room, out List<int> indexes))
        {
            indexes = new List<int>();
            NodesByRoom[room] = indexes;
            Rooms.Add(room);
        }

        indexes.Add(index);
        return index;
    }

    public void AddUndirectedLink(int left, int right, float cost)
    {
        if (left == right)
        {
            return;
        }

        if (Nodes[left].Links.Any(link => link.To == right))
        {
            return;
        }

        Nodes[left].Links.Add(new FacilityWaypointLink(right, cost));
        Nodes[right].Links.Add(new FacilityWaypointLink(left, cost));
    }

    public void FinalizeLinkCount()
    {
        LinkCount = Nodes.Sum(node => node.Links.Count) / 2;
    }
}

internal sealed class FacilityWaypointNode
{
    public FacilityWaypointNode(int index, Room room, Vector3 position, FacilityWaypointKind kind)
    {
        Index = index;
        Room = room;
        Position = position;
        Kind = kind;
    }

    public int Index { get; }

    public Room Room { get; }

    public Vector3 Position { get; }

    public FacilityWaypointKind Kind { get; }

    public List<FacilityWaypointLink> Links { get; } = new();
}

internal readonly struct FacilityWaypointLink
{
    public FacilityWaypointLink(int to, float cost)
    {
        To = to;
        Cost = cost;
    }

    public int To { get; }

    public float Cost { get; }
}
