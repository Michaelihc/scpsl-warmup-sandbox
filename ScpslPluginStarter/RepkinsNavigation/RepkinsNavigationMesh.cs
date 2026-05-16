using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LabApi.Features.Wrappers;
using MapGeneration;
using UnityEngine;

namespace ScpslPluginStarter.RepkinsNavigation;

// Ported from repkins/scpsl-bot-plugin (SCPSLBot.Navigation.Mesh).
// The upstream repository did not include a license file in the local clone; keep this
// namespace isolated so the provenance stays clear.
internal sealed class RepkinsVertex
{
    public RepkinsVertex(Vector3 position)
    {
        Position = position;
    }

    public Vector3 Position { get; set; }
}

internal struct RepkinsEdge
{
    public RepkinsEdge(RepkinsVertex from, RepkinsVertex to)
    {
        From = from;
        To = to;
    }

    public RepkinsVertex From;

    public RepkinsVertex To;

    public override readonly bool Equals(object? obj)
    {
        return obj is RepkinsEdge edge && (From, To).Equals((edge.From, edge.To));
    }

    public override readonly int GetHashCode()
    {
        return (From, To).GetHashCode();
    }

    public static bool operator ==(RepkinsEdge left, RepkinsEdge right)
    {
        return (left.From, left.To) == (right.From, right.To);
    }

    public static bool operator !=(RepkinsEdge left, RepkinsEdge right)
    {
        return !(left == right);
    }
}

internal sealed class RepkinsCell
{
    public RepkinsCell(IEnumerable<RepkinsVertex> vertices)
    {
        Vertices.AddRange(vertices);
    }

    public List<RepkinsVertex> Vertices { get; } = new();

    public List<RepkinsCell> AdjacentCells { get; } = new();

    public Dictionary<RepkinsCell, RepkinsEdge> AdjacentCellEdges { get; } = new();

    public IEnumerable<RepkinsEdge> Edges => Vertices.Zip(Vertices.Skip(1), (v1, v2) => new RepkinsEdge(v1, v2))
        .Append(new RepkinsEdge(Vertices.Last(), Vertices.First()));

    public Vector3 CenterPosition => Vertices.Aggregate(Vector3.zero, (sum, vertex) => sum + vertex.Position) / Vertices.Count;

    public void AddAdjacentCell(RepkinsCell adjacentCell, RepkinsEdge adjacentEdge)
    {
        AdjacentCells.Add(adjacentCell);
        AdjacentCellEdges[adjacentCell] = adjacentEdge;
    }

    public void RemoveAdjacentCell(RepkinsCell adjacentCell)
    {
        AdjacentCells.Remove(adjacentCell);
        AdjacentCellEdges.Remove(adjacentCell);
    }

    public void AddVertex(RepkinsVertex vertex, RepkinsVertex beforeVertex)
    {
        int index = Vertices.IndexOf(beforeVertex);
        Vertices.Insert(index, vertex);
    }

    public void RemoveVertex(RepkinsVertex vertex)
    {
        Vertices.Remove(vertex);
    }
}

internal readonly record struct RepkinsTransformVertex(RepkinsVertex Local, Transform Transform)
{
    public Vector3 Position => Transform.TransformPoint(Local.Position);
}

internal readonly record struct RepkinsTransformEdge(RepkinsTransformVertex From, RepkinsTransformVertex To, Transform Transform)
{
    public RepkinsEdge Local => new(From.Local, To.Local);

    public RepkinsTransformEdge(RepkinsEdge edge, Transform transform)
        : this(new RepkinsTransformVertex(edge.From, transform), new RepkinsTransformVertex(edge.To, transform), transform)
    {
    }

    public RepkinsTransformEdge(RepkinsVertex from, RepkinsVertex to, Transform transform)
        : this(new RepkinsTransformVertex(from, transform), new RepkinsTransformVertex(to, transform), transform)
    {
    }
}

internal readonly record struct RepkinsTransformCell(RepkinsCell Local, Transform Transform)
{
    public IEnumerable<RepkinsTransformCell> AdjacentCells
    {
        get
        {
            RepkinsCell local = Local;
            Transform transform = Transform;
            return local.AdjacentCells.Select(cell => new RepkinsTransformCell(cell, transform));
        }
    }

    public Vector3 CenterPosition => Transform.TransformPoint(Local.CenterPosition);

    public bool TryGetAdjacentCellEdge(RepkinsTransformCell adjacentCell, out RepkinsTransformEdge edge)
    {
        if (Local.AdjacentCellEdges.TryGetValue(adjacentCell.Local, out RepkinsEdge localEdge))
        {
            edge = new RepkinsTransformEdge(localEdge, Transform);
            return true;
        }

        edge = default;
        return false;
    }
}

internal sealed class RepkinsNavigationMesh
{
    private RepkinsNavigationMesh()
    {
        VertexDeleted += RemoveVertexFromCells;
    }

    public List<RepkinsVertex> Vertices { get; } = new();

    public List<RepkinsCell> Cells { get; } = new();

    public event Action<RepkinsVertex>? VertexCreated;

    public event Action<RepkinsVertex>? VertexDeleted;

    public event Action<RepkinsCell>? CellCreated;

    public event Action<RepkinsCell>? CellDeleted;

    public static Dictionary<string, RepkinsNavigationMesh> MeshesByRoomForm { get; } = new();

    public static Dictionary<string, List<GameObject>> RoomsByForm { get; } = new();

    public static Dictionary<GameObject, RepkinsNavigationMesh> LocalMeshesByRoom { get; } = new();

    public static Dictionary<RepkinsTransformCell, List<RepkinsTransformCell>> ForeignConnectedCells { get; } = new();

    public static Dictionary<RepkinsTransformCell, Dictionary<RepkinsTransformCell, RepkinsTransformEdge>> ForeignConnectedCellEdges { get; } = new();

    public static RepkinsNavigationMesh CreateMesh(string form)
    {
        RepkinsNavigationMesh mesh = new();
        MeshesByRoomForm[form] = mesh;

        if (RoomsByForm.TryGetValue(form, out List<GameObject> rooms))
        {
            foreach (GameObject room in rooms)
            {
                LocalMeshesByRoom[room] = mesh;
            }

            mesh.CellCreated += cell => AddForeignConnectedCellsList(cell, form);
            mesh.CellDeleted += cell => RemoveForeignConnectedCellsList(cell, form);
        }

        return mesh;
    }

    public static void InitMeshes()
    {
        foreach (GameObject room in Room.List.Select(apiRoom => apiRoom.Base.gameObject))
        {
            string roomForm = GetForm(room);
            if (!RoomsByForm.TryGetValue(roomForm, out List<GameObject> rooms))
            {
                rooms = new List<GameObject>();
                RoomsByForm.Add(roomForm, rooms);
            }

            rooms.Add(room);
        }

        foreach (string roomForm in RoomsByForm.Keys)
        {
            if (!MeshesByRoomForm.ContainsKey(roomForm))
            {
                CreateMesh(roomForm);
            }
        }
    }

    public static void ResetMeshes()
    {
        RoomsByForm.Clear();
        MeshesByRoomForm.Clear();
        LocalMeshesByRoom.Clear();
        ForeignConnectedCells.Clear();
        ForeignConnectedCellEdges.Clear();
    }

    public static RepkinsTransformCell? GetCellWithin(Vector3 position)
    {
        return RoomUtils.TryGetRoom(position, out RoomIdentifier room)
            ? GetRoomCellWithin(position, room)
            : null;
    }

    public static RepkinsTransformCell? GetRoomCellWithin(Vector3 position, RoomIdentifier? room = null)
    {
        if (room == null && !RoomUtils.TryGetRoom(position, out room))
        {
            return null;
        }

        if (room == null || !LocalMeshesByRoom.TryGetValue(room.gameObject, out RepkinsNavigationMesh roomMesh))
        {
            return null;
        }

        Vector3 localPosition = room.transform.InverseTransformPoint(position);
        foreach (RepkinsCell localCell in roomMesh.Cells)
        {
            if (IsLocalPointWithinCell(localCell, localPosition))
            {
                return new RepkinsTransformCell(localCell, room.transform);
            }
        }

        return null;
    }

    public static bool IsAtPositiveEdgeSide(Vector3 position, RepkinsTransformEdge transformEdge)
    {
        Vector3 localPosition = transformEdge.Transform.InverseTransformPoint(position);
        return IsAtPositiveEdgeSide(localPosition, transformEdge.Local);
    }

    public static RepkinsTransformEdge? GetNearestEdge(Vector3 position, RoomIdentifier? room = null)
    {
        (RepkinsVertex From, RepkinsVertex To)? nearestEdge = GetNearestEdge(position, out _, room);
        return nearestEdge.HasValue && room != null
            ? new RepkinsTransformEdge(nearestEdge.Value.From, nearestEdge.Value.To, room.transform)
            : null;
    }

    public static (RepkinsVertex From, RepkinsVertex To)? GetNearestEdge(Vector3 position, out Vector3 closestPoint, RoomIdentifier? room = null)
    {
        closestPoint = Vector3.zero;
        if (room == null && !RoomUtils.TryGetRoom(position, out room))
        {
            return null;
        }

        if (room == null || !LocalMeshesByRoom.TryGetValue(room.gameObject, out RepkinsNavigationMesh roomMesh))
        {
            return null;
        }

        Vector3 localPosition = room.transform.InverseTransformPoint(position);
        var hit = roomMesh.Cells
            .SelectMany(cell => cell.Edges)
            .Select(edge => (Edge: edge, PlaneDist: GetPointDistToEdgePlane(edge, localPosition, out Vector3 planeClosest), PlaneClosest: planeClosest))
            .Where(candidate => candidate.PlaneDist <= 0f)
            .Select(candidate => (candidate.Edge, Closest: ClampWithinEdgePoints(candidate.Edge, candidate.PlaneClosest)))
            .Select(candidate => (candidate.Edge, Dist: -Vector3.SqrMagnitude(localPosition - candidate.Closest), candidate.Closest))
            .Where(candidate => IsEdgeCenterWithinVertically(candidate.Edge, localPosition))
            .OrderByDescending(candidate => candidate.Dist)
            .Select(candidate => new { candidate.Edge, candidate.Closest })
            .FirstOrDefault();

        if (hit == null)
        {
            return null;
        }

        closestPoint = room.transform.TransformPoint(hit.Closest);
        return (hit.Edge.From, hit.Edge.To);
    }

    public static void FindShortestPath(RepkinsTransformCell startingCell, RepkinsTransformCell endCell, List<RepkinsTransformCell> results)
    {
        Dictionary<RepkinsTransformCell, float> cellsWithPriorityToEvaluate = new();
        Dictionary<RepkinsTransformCell, RepkinsTransformCell> cameFromCells = new();
        Dictionary<RepkinsTransformCell, float> costsTill = new();

        float cost = 0f;
        float heuristic = Vector3.Magnitude(endCell.CenterPosition - startingCell.CenterPosition);
        cellsWithPriorityToEvaluate.Add(startingCell, cost + heuristic);
        costsTill.Add(startingCell, cost);

        RepkinsTransformCell cell = startingCell;
        do
        {
            cell = cellsWithPriorityToEvaluate.Aggregate((left, right) => right.Value < left.Value ? right : left).Key;
            cellsWithPriorityToEvaluate.Remove(cell);

            if (cell == endCell)
            {
                break;
            }

            cost = costsTill[cell];
            foreach (RepkinsTransformCell connectedCell in EnumerateConnectedCells(cell))
            {
                float connectedCost = cost + Vector3.Magnitude(connectedCell.CenterPosition - cell.CenterPosition);
                if (!costsTill.ContainsKey(connectedCell) || connectedCost < costsTill[connectedCell])
                {
                    costsTill[connectedCell] = connectedCost;
                    heuristic = Vector3.Magnitude(endCell.CenterPosition - connectedCell.CenterPosition);
                    cellsWithPriorityToEvaluate[connectedCell] = connectedCost + heuristic;
                    cameFromCells[connectedCell] = cell;
                }
            }
        }
        while (cellsWithPriorityToEvaluate.Any());

        results.Clear();
        if (startingCell == endCell)
        {
            results.Add(startingCell);
            return;
        }

        if (!cameFromCells.ContainsKey(endCell))
        {
            return;
        }

        cell = endCell;
        do
        {
            results.Add(cell);
        }
        while (cameFromCells.TryGetValue(cell, out cell));

        results.Reverse();
    }

    public static void ReadMeshes(BinaryReader binaryReader)
    {
        byte version = binaryReader.ReadByte();
        if (version < 3)
        {
            Debug.LogError("Repkins navmesh version is older than supported.");
            return;
        }

        int roomFormCount = binaryReader.ReadInt32();
        for (int i = 0; i < roomFormCount; i++)
        {
            string roomForm = binaryReader.ReadString();
            if (!MeshesByRoomForm.TryGetValue(roomForm, out RepkinsNavigationMesh formMesh))
            {
                formMesh = CreateMesh(roomForm);
            }

            formMesh.ReadMesh(binaryReader, version);
        }
    }

    public static string GetForm(GameObject gameObject)
    {
        string? gameObjectName = gameObject == null ? null : gameObject.name;
        return gameObjectName != null && gameObjectName.EndsWith("(Clone)", StringComparison.Ordinal)
            ? gameObjectName.Remove(gameObjectName.LastIndexOf("(Clone)", StringComparison.Ordinal))
            : gameObjectName ?? string.Empty;
    }

    public RepkinsVertex AddVertex(Vector3 position)
    {
        RepkinsVertex newVertex = new(position);
        Vertices.Add(newVertex);
        VertexCreated?.Invoke(newVertex);
        return newVertex;
    }

    public RepkinsCell MakeCell(IEnumerable<RepkinsVertex> vertices)
    {
        RepkinsCell newCell = new(vertices);
        Cells.Add(newCell);
        AddAdjacentCells(newCell);
        CellCreated?.Invoke(newCell);
        return newCell;
    }

    public bool DeleteVertex(RepkinsVertex vertex)
    {
        if (!Vertices.Remove(vertex))
        {
            return false;
        }

        VertexDeleted?.Invoke(vertex);
        return true;
    }

    public bool RemoveCell(RepkinsCell cell)
    {
        if (!Cells.Remove(cell))
        {
            return false;
        }

        RemoveAdjacentCells(cell);
        CellDeleted?.Invoke(cell);
        return true;
    }

    public void ReadMesh(BinaryReader binaryReader, byte version)
    {
        int vertexCount = binaryReader.ReadInt32();
        for (int i = 0; i < vertexCount; i++)
        {
            Vector3 vertexPosition = new()
            {
                x = binaryReader.ReadSingle(),
                y = binaryReader.ReadSingle(),
                z = binaryReader.ReadSingle(),
            };
            AddVertex(vertexPosition);
        }

        int cellsCount = binaryReader.ReadInt32();
        for (int i = 0; i < cellsCount; i++)
        {
            int cellVerticesCount = binaryReader.ReadInt32();
            int[] cellVertices = new int[cellVerticesCount];
            for (int j = 0; j < cellVerticesCount; j++)
            {
                cellVertices[j] = binaryReader.ReadInt32();
            }

            MakeCell(cellVertices.Select(vertexIndex => Vertices[vertexIndex]));

            if (version < 5)
            {
                int connectedCellsCount = binaryReader.ReadInt32();
                for (int j = 0; j < connectedCellsCount; j++)
                {
                    _ = binaryReader.ReadInt32();
                }
            }
        }
    }

    private static IEnumerable<RepkinsTransformCell> EnumerateConnectedCells(RepkinsTransformCell cell)
    {
        foreach (RepkinsTransformCell adjacent in cell.AdjacentCells)
        {
            yield return adjacent;
        }

        if (!ForeignConnectedCells.TryGetValue(cell, out List<RepkinsTransformCell> foreignCells))
        {
            yield break;
        }

        foreach (RepkinsTransformCell foreign in foreignCells)
        {
            yield return foreign;
        }
    }

    private static void AddForeignConnectedCellsList(RepkinsCell cell, string form)
    {
        foreach (GameObject room in RoomsByForm[form])
        {
            RepkinsTransformCell transformCell = new(cell, room.transform);
            ForeignConnectedCells[transformCell] = new List<RepkinsTransformCell>();
            ForeignConnectedCellEdges[transformCell] = new Dictionary<RepkinsTransformCell, RepkinsTransformEdge>();
        }
    }

    private static void RemoveForeignConnectedCellsList(RepkinsCell cell, string form)
    {
        foreach (GameObject room in RoomsByForm[form])
        {
            RepkinsTransformCell transformCell = new(cell, room.transform);
            ForeignConnectedCells.Remove(transformCell);
            ForeignConnectedCellEdges.Remove(transformCell);
        }
    }

    private static bool IsAtPositiveEdgeSide(Vector3 position, RepkinsEdge edge)
    {
        return GetPointDistToEdgePlane(edge, position) > 0f;
    }

    private static Vector3 ClampWithinEdgePoints(RepkinsEdge edge, Vector3 planeClosestPoint)
    {
        Vector3 fromTo = edge.To.Position - edge.From.Position;
        Vector3 fromPoint = planeClosestPoint - edge.From.Position;
        Vector3 toFrom = edge.From.Position - edge.To.Position;
        Vector3 toPoint = planeClosestPoint - edge.To.Position;

        if (Vector3.Dot(fromPoint, fromTo) < 0f)
        {
            return edge.From.Position;
        }

        if (Vector3.Dot(toPoint, toFrom) < 0f)
        {
            return edge.To.Position;
        }

        return planeClosestPoint;
    }

    private static bool IsLocalPointWithinCell(RepkinsCell cell, Vector3 pointLocalPosition)
    {
        bool isAnyVertexWithinVerticalRange = false;
        foreach (RepkinsEdge edge in cell.Edges)
        {
            if (GetPointDistToEdgePlane(edge, pointLocalPosition) <= 0f)
            {
                return false;
            }

            if (!isAnyVertexWithinVerticalRange)
            {
                isAnyVertexWithinVerticalRange =
                    edge.From.Position.y > pointLocalPosition.y - 1f
                    && edge.From.Position.y < pointLocalPosition.y + 1f;
            }
        }

        return isAnyVertexWithinVerticalRange;
    }

    private static float GetPointDistToEdgePlane(RepkinsEdge edge, Vector3 point)
    {
        return GetPointDistToEdgePlane(edge, point, out _);
    }

    private static float GetPointDistToEdgePlane(RepkinsEdge edge, Vector3 point, out Vector3 closestPoint)
    {
        Vector3 edgeDirection = edge.To.Position - edge.From.Position;
        Vector3 pointDirection = point - edge.From.Position;
        Vector3 edgeNormal = Vector3.Cross(edgeDirection.normalized, Vector3.down);
        float distance = Vector3.Dot(edgeNormal, pointDirection);
        closestPoint = point - (edgeNormal * distance);
        return distance;
    }

    private static bool IsEdgeCenterWithinVertically(RepkinsEdge edge, Vector3 localPoint)
    {
        float localPointYLowest = localPoint.y - 1f;
        float localPointYHighest = localPoint.y + 1f;
        Vector3 edgeCenter = Vector3.Lerp(edge.From.Position, edge.To.Position, 0.5f);
        return edgeCenter.y > localPointYLowest && edgeCenter.y < localPointYHighest;
    }

    private void AddAdjacentCells(RepkinsCell newCell)
    {
        foreach (RepkinsCell adjacentCell in Cells)
        {
            if (ReferenceEquals(adjacentCell, newCell))
            {
                continue;
            }

            foreach (RepkinsEdge edge in newCell.Edges)
            {
                RepkinsEdge adjacentEdge = new(edge.To, edge.From);
                if (adjacentCell.Edges.Contains(adjacentEdge))
                {
                    newCell.AddAdjacentCell(adjacentCell, adjacentEdge);
                    adjacentCell.AddAdjacentCell(newCell, edge);
                }
            }
        }
    }

    private void RemoveAdjacentCells(RepkinsCell cell)
    {
        foreach (RepkinsCell otherCell in Cells)
        {
            cell.RemoveAdjacentCell(otherCell);
            otherCell.RemoveAdjacentCell(cell);
        }
    }

    private void RemoveVertexFromCells(RepkinsVertex vertex)
    {
        foreach (RepkinsCell cell in Cells)
        {
            cell.RemoveVertex(vertex);
            RemoveAdjacentCells(cell);
            AddAdjacentCells(cell);
        }
    }
}
