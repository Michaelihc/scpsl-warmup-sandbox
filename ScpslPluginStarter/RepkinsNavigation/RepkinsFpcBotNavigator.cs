using System.Collections.Generic;
using System.Linq;
using LabApi.Features.Wrappers;
using MapGeneration;
using PlayerRoles.FirstPersonControl;
using UnityEngine;

namespace ScpslPluginStarter.RepkinsNavigation;

// Ported from repkins/scpsl-bot-plugin (SCPSLBot.AI.FirstPersonControl.FpcBotNavigator).
internal sealed class RepkinsFpcBotNavigator
{
    private readonly Player _bot;
    private RepkinsTransformCell _currentCell;
    private RepkinsTransformCell? _goalCell;
    private int _currentPathIndex = -1;
    private bool _isGoalOutside;
    private Vector3 _targetCellClosestPositionToGoal;

    public RepkinsFpcBotNavigator(Player bot)
    {
        _bot = bot;
    }

    public List<RepkinsTransformCell> CellsPath { get; } = new();

    public List<Vector3> PointsPath { get; } = new();

    public Vector3 GoalPosition { get; private set; }

    public Vector3 GetPositionTowards(Vector3 goalPosition)
    {
        UpdateNavigationTo(goalPosition);
        if (!IsAtLastCell())
        {
            return GetNextCorner(goalPosition);
        }

        return _goalCell.HasValue && _isGoalOutside
            ? _targetCellClosestPositionToGoal
            : goalPosition;
    }

    public RepkinsTransformCell? GetCellWithin()
    {
        return RepkinsNavigationMesh.GetCellWithin(PlayerPosition);
    }

    private Vector3 PlayerPosition
    {
        get
        {
            if (_bot.ReferenceHub?.roleManager?.CurrentRole is IFpcRole { FpcModule: var fpcModule })
            {
                return fpcModule.transform.position;
            }

            return _bot.Position;
        }
    }

    private void UpdateNavigationTo(Vector3 goalPosition)
    {
        Vector3 playerPosition = PlayerPosition;

        if (!IsAtLastCell())
        {
            bool isEdgeReached;
            do
            {
                RepkinsTransformCell nextTargetCell = CellsPath[_currentPathIndex + 1];
                if (!TryGetConnectionEdge(_currentCell, nextTargetCell, out RepkinsTransformEdge nextTargetCellEdge))
                {
                    break;
                }

                isEdgeReached = RepkinsNavigationMesh.IsAtPositiveEdgeSide(playerPosition, nextTargetCellEdge);
                if (isEdgeReached)
                {
                    _currentCell = CellsPath[++_currentPathIndex];
                }
            }
            while (isEdgeReached && !IsAtLastCell());
        }

        RepkinsTransformCell? withinCell = GetCellWithin();
        RepkinsTransformCell? targetCell = RepkinsNavigationMesh.GetCellWithin(goalPosition);
        if (!targetCell.HasValue)
        {
            if (RoomUtils.TryGetRoom(goalPosition, out RoomIdentifier goalRoom))
            {
                (RepkinsVertex From, RepkinsVertex To)? nearestEdge = RepkinsNavigationMesh.GetNearestEdge(goalPosition, out Vector3 closestPoint, goalRoom);
                if (nearestEdge.HasValue
                    && RepkinsNavigationMesh.LocalMeshesByRoom.TryGetValue(goalRoom.gameObject, out RepkinsNavigationMesh mesh))
                {
                    RepkinsEdge nearestLocalEdge = new(nearestEdge.Value.From, nearestEdge.Value.To);
                    RepkinsCell? localCell = mesh.Cells.FirstOrDefault(cell => cell.Edges.Any(edge => edge == nearestLocalEdge));
                    if (localCell != null)
                    {
                        targetCell = new RepkinsTransformCell(localCell, goalRoom.transform);
                        _targetCellClosestPositionToGoal = closestPoint;
                    }
                }
            }

            _isGoalOutside = true;
        }
        else
        {
            _isGoalOutside = false;
        }

        if (withinCell.HasValue
            && targetCell.HasValue
            && (targetCell.Value != _goalCell || withinCell.Value != _currentCell))
        {
            _currentCell = withinCell.Value;
            _goalCell = targetCell.Value;
            RepkinsNavigationMesh.FindShortestPath(withinCell.Value, targetCell.Value, CellsPath);
            _currentPathIndex = 0;
            GoalPosition = goalPosition;

            PointsPath.Clear();
            PointsPath.Add(playerPosition);
            bool partialPath = false;
            foreach ((RepkinsTransformCell cell, RepkinsTransformCell nextCell) in CellsPath.Zip(CellsPath.Skip(1), (cell, nextCell) => (cell, nextCell)))
            {
                if (!TryGetConnectionEdge(cell, nextCell, out RepkinsTransformEdge edge))
                {
                    partialPath = true;
                    break;
                }

                PointsPath.Add(Vector3.Lerp(edge.From.Position, edge.To.Position, 0.5f));
            }

            if (!partialPath)
            {
                PointsPath.Add(goalPosition);
            }
        }
    }

    private Vector3 GetNextCorner(Vector3 goalPosition)
    {
        Vector3 playerPosition = PlayerPosition;
        RepkinsTransformCell nextTargetCell = CellsPath[_currentPathIndex + 1];
        if (!TryGetConnectionEdge(_currentCell, nextTargetCell, out RepkinsTransformEdge targetCellEdge))
        {
            return _currentCell.CenterPosition;
        }

        Vector3 nextTargetEdgeMiddlePosition = Vector3.Lerp(targetCellEdge.From.Position, targetCellEdge.To.Position, 0.5f);
        Vector3 nextTargetPosition = nextTargetEdgeMiddlePosition;
        int aheadPathIndex = _currentPathIndex + 1;

        while (nextTargetEdgeMiddlePosition == nextTargetPosition && aheadPathIndex < CellsPath.Count - 1)
        {
            aheadPathIndex++;
            (Vector3 from, Vector3 to) relTargetEdgePos = (
                targetCellEdge.From.Position - playerPosition,
                targetCellEdge.To.Position - playerPosition);

            RepkinsTransformCell aheadTargetCell = CellsPath[aheadPathIndex];
            if (!TryGetConnectionEdge(nextTargetCell, aheadTargetCell, out RepkinsTransformEdge aheadTargetCellEdge))
            {
                goalPosition = nextTargetCell.CenterPosition;
                break;
            }

            (Vector3 from, Vector3 to) relAheadTargetEdgePos = (
                aheadTargetCellEdge.From.Position - playerPosition,
                aheadTargetCellEdge.To.Position - playerPosition);

            (Vector3 from, Vector3 to) dirToAheadTargetEdgeNormals = (
                Vector3.Cross(relAheadTargetEdgePos.from, Vector3.up),
                Vector3.Cross(relAheadTargetEdgePos.to, Vector3.up));

            if (Vector3.Dot(relTargetEdgePos.from, dirToAheadTargetEdgeNormals.from) < 0)
            {
                targetCellEdge = new RepkinsTransformEdge(aheadTargetCellEdge.From, targetCellEdge.To, targetCellEdge.Transform);
            }

            if (Vector3.Dot(relTargetEdgePos.to, dirToAheadTargetEdgeNormals.to) > 0)
            {
                targetCellEdge = new RepkinsTransformEdge(targetCellEdge.From, aheadTargetCellEdge.To, targetCellEdge.Transform);
            }

            if (Vector3.Dot(relTargetEdgePos.from, dirToAheadTargetEdgeNormals.to) > 0)
            {
                nextTargetPosition = targetCellEdge.From.Position;
            }

            if (Vector3.Dot(relTargetEdgePos.to, dirToAheadTargetEdgeNormals.from) < 0)
            {
                nextTargetPosition = targetCellEdge.To.Position;
            }

            nextTargetCell = aheadTargetCell;
        }

        if (nextTargetPosition == nextTargetEdgeMiddlePosition)
        {
            nextTargetPosition = goalPosition;
            (Vector3 from, Vector3 to) relNextTargetEdgePos = (
                targetCellEdge.From.Position - playerPosition,
                targetCellEdge.To.Position - playerPosition);

            Vector3 relGoalPos = goalPosition - playerPosition;
            Vector3 dirToGoalNormal = Vector3.Cross(relGoalPos, Vector3.up);
            if (Vector3.Dot(relNextTargetEdgePos.from, dirToGoalNormal) > 0)
            {
                nextTargetPosition = targetCellEdge.From.Position;
            }

            if (Vector3.Dot(relNextTargetEdgePos.to, dirToGoalNormal) < 0)
            {
                nextTargetPosition = targetCellEdge.To.Position;
            }
        }

        return nextTargetPosition;
    }

    private static bool TryGetConnectionEdge(
        RepkinsTransformCell from,
        RepkinsTransformCell to,
        out RepkinsTransformEdge edge)
    {
        if (from.TryGetAdjacentCellEdge(to, out edge))
        {
            return true;
        }

        if (RepkinsNavigationMesh.ForeignConnectedCellEdges.TryGetValue(from, out Dictionary<RepkinsTransformCell, RepkinsTransformEdge> foreignEdges)
            && foreignEdges.TryGetValue(to, out edge))
        {
            return true;
        }

        edge = default;
        return false;
    }

    private bool IsAtLastCell()
    {
        return _currentPathIndex >= CellsPath.Count - 1;
    }
}
