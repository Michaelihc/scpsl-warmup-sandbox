using System;
using System.IO;
using System.Linq;
using Interactables.Interobjects;
using Interactables.Interobjects.DoorUtils;
using LabApi.Events.Arguments.ServerEvents;
using LabApi.Events.Handlers;
using MapGeneration;
using UnityEngine;

namespace ScpslPluginStarter.RepkinsNavigation;

// Ported from repkins/scpsl-bot-plugin (SCPSLBot.Navigation.NavigationSystem).
internal sealed class RepkinsNavigationSystem
{
    private RepkinsNavigationSystem()
    {
    }

    public static RepkinsNavigationSystem Instance { get; } = new();

    public string MeshFileName { get; } = "navmesh.slnmf";

    public string BaseDir { get; private set; } = string.Empty;

    public bool Initialized { get; private set; }

    public bool MeshesLoaded { get; private set; }

    public void Init(string baseDir)
    {
        if (Initialized)
        {
            return;
        }

        BaseDir = baseDir;
        Directory.CreateDirectory(BaseDir);
        ServerEvents.MapGenerated += OnMapGenerated;
        ServerEvents.RoundRestarted += OnRoundRestarted;
        Initialized = true;

        if (SeedSynchronizer.MapGenerated)
        {
            LoadConnectMeshes();
        }
    }

    public void Terminate()
    {
        if (!Initialized)
        {
            return;
        }

        ServerEvents.MapGenerated -= OnMapGenerated;
        ServerEvents.RoundRestarted -= OnRoundRestarted;
        Initialized = false;
        MeshesLoaded = false;
        RepkinsFpcMovementRegistry.ClearAll();
        RepkinsNavigationMesh.ResetMeshes();
    }

    public bool TryEnsureLoaded(Action<string>? log = null)
    {
        if (MeshesLoaded)
        {
            return true;
        }

        if (!SeedSynchronizer.MapGenerated)
        {
            log?.Invoke("repkins-navmesh-waiting-for-map");
            return false;
        }

        LoadConnectMeshes(log);
        return MeshesLoaded;
    }

    private void OnMapGenerated(MapGeneratedEventArgs args)
    {
        LoadConnectMeshes();
    }

    private void OnRoundRestarted()
    {
        MeshesLoaded = false;
        RepkinsFpcMovementRegistry.ClearAll();
        RepkinsNavigationMesh.ResetMeshes();
    }

    public void LoadConnectMeshes(Action<string>? log = null)
    {
        try
        {
            RepkinsNavigationMesh.ResetMeshes();
            RepkinsNavigationMesh.InitMeshes();
            if (!LoadMeshes(MeshFileName))
            {
                MeshesLoaded = false;
                log?.Invoke($"repkins-navmesh-missing path={Path.Combine(BaseDir, MeshFileName)}");
                return;
            }

            ConnectCellsBetweenRooms();
            ConnectCellsBetweenElevatorDestinations();
            MeshesLoaded = true;
            log?.Invoke("repkins-navmesh-loaded");
        }
        catch (Exception ex)
        {
            MeshesLoaded = false;
            Debug.LogError($"Repkins navigation mesh load failed: {ex}");
            log?.Invoke($"repkins-navmesh-load-failed error={ex.GetType().Name}:{ex.Message}");
        }
    }

    private bool LoadMeshes(string fileName)
    {
        string path = Path.Combine(BaseDir, fileName);
        if (!File.Exists(path))
        {
            return false;
        }

        using FileStream fileStream = File.OpenRead(path);
        using BinaryReader binaryReader = new(fileStream);
        RepkinsNavigationMesh.ReadMeshes(binaryReader);
        return RepkinsNavigationMesh.MeshesByRoomForm.Values.Any(mesh => mesh.Cells.Count > 0);
    }

    private static void ConnectCellsBetweenRooms()
    {
        foreach (DoorVariant door in DoorVariant.AllDoors)
        {
            if (door == null || door.Rooms == null || door.Rooms.Length != 2)
            {
                continue;
            }

            Vector3 doorCenterPosition = door.transform.position + Vector3.up;
            RepkinsTransformEdge? edgeInFront = RepkinsNavigationMesh.GetNearestEdge(doorCenterPosition, door.Rooms[0]);
            RepkinsTransformEdge? edgeInBack = RepkinsNavigationMesh.GetNearestEdge(doorCenterPosition, door.Rooms[1]);
            if (!edgeInFront.HasValue || !edgeInBack.HasValue)
            {
                continue;
            }

            if (!TryFindCellOwningEdge(door.Rooms[0], edgeInFront.Value.Local, out RepkinsTransformCell cellInFront)
                || !TryFindCellOwningEdge(door.Rooms[1], edgeInBack.Value.Local, out RepkinsTransformCell cellInBack))
            {
                continue;
            }

            AddForeignConnection(cellInFront, cellInBack, edgeInBack.Value);
            AddForeignConnection(cellInBack, cellInFront, edgeInFront.Value);
        }
    }

    private static void ConnectCellsBetweenElevatorDestinations()
    {
        foreach (ElevatorGroup group in Enum.GetValues(typeof(ElevatorGroup)))
        {
            var elevatorDoors = ElevatorDoor.GetDoorsForGroup(group);
            if (elevatorDoors.Count != 2)
            {
                continue;
            }

            RepkinsTransformCell? firstShaftCell = GetCellInElevatorShaft(elevatorDoors[0]);
            RepkinsTransformCell? secondShaftCell = GetCellInElevatorShaft(elevatorDoors[1]);
            if (!firstShaftCell.HasValue || !secondShaftCell.HasValue)
            {
                continue;
            }

            AddForeignConnection(firstShaftCell.Value, secondShaftCell.Value, null);
            AddForeignConnection(secondShaftCell.Value, firstShaftCell.Value, null);
        }
    }

    private static RepkinsTransformCell? GetCellInElevatorShaft(ElevatorDoor elevatorDoor)
    {
        Transform doorTransform = elevatorDoor.transform;
        Vector3 doorPosition = doorTransform.position + Vector3.up;
        Vector3 doorForward = doorTransform.forward;

        for (int i = 1; i <= 3; i++)
        {
            Vector3 probe = doorPosition - (doorForward * i);
            if (!RoomUtils.TryGetRoom(probe, out RoomIdentifier room)
                && RoomUtils.TryGetRoom(doorPosition + doorForward, out room))
            {
                RoomIdentifier.RoomsByCoords[RoomUtils.PositionToCoords(probe)] = room;
                break;
            }
        }

        return RepkinsNavigationMesh.GetCellWithin(doorPosition - doorForward);
    }

    private static bool TryFindCellOwningEdge(RoomIdentifier room, RepkinsEdge edge, out RepkinsTransformCell cell)
    {
        cell = default;
        if (!RepkinsNavigationMesh.LocalMeshesByRoom.TryGetValue(room.gameObject, out RepkinsNavigationMesh mesh))
        {
            return false;
        }

        RepkinsCell? localCell = mesh.Cells.FirstOrDefault(candidate => candidate.Edges.Any(candidateEdge => candidateEdge == edge));
        if (localCell == null)
        {
            return false;
        }

        cell = new RepkinsTransformCell(localCell, room.transform);
        return true;
    }

    private static void AddForeignConnection(
        RepkinsTransformCell from,
        RepkinsTransformCell to,
        RepkinsTransformEdge? throughEdge)
    {
        if (!RepkinsNavigationMesh.ForeignConnectedCells.TryGetValue(from, out var cells))
        {
            cells = new();
            RepkinsNavigationMesh.ForeignConnectedCells[from] = cells;
        }

        if (!cells.Contains(to))
        {
            cells.Add(to);
        }

        if (throughEdge.HasValue)
        {
            if (!RepkinsNavigationMesh.ForeignConnectedCellEdges.TryGetValue(from, out var edges))
            {
                edges = new();
                RepkinsNavigationMesh.ForeignConnectedCellEdges[from] = edges;
            }

            edges[to] = throughEdge.Value;
        }
    }
}
