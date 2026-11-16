using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EndlessEscalator;

[BepInPlugin(Guid, Name, Version)]
[DefaultExecutionOrder(100)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "Rombusz.EndlessEscalator";
    public const string Name = "EndlessEscalator";
    public const string Version = "2.0.0";
    private static readonly FieldInfo? zoneForce = typeof(PlayerMoveZone).GetField("Force", BindingFlags.Instance | BindingFlags.NonPublic);
    private readonly List<Lane> lanes = new List<Lane>();
    private readonly LocalTransfer transfer = new LocalTransfer();
    private readonly RaycastHit[] groundHits = new RaycastHit[24];
    private ConfigEntry<bool> enabledSetting = null!;
    private Lane? riding;
    private Character? rider;
    private float ridingSince, nextTransfer, nextScan;
    private Vector3 lastRidePosition;
    private TurnPath? turn;
    private float turnDistance, progressDistance, lastProgressAt;
    private bool faulted;

    private sealed class Lane
    {
        internal readonly PlayerMoveZone Zone;
        internal readonly MeshCollider Surface;
        internal readonly LaneFrame Frame;
        internal readonly Bounds Casing;
        internal readonly RideSpeed Speed = new();
        internal float Force => zoneForce?.GetValue(Zone) is float value ? value : 20f;
        internal Lane? Other;
        internal bool Live => Zone && Zone.isActiveAndEnabled && Surface && Surface.enabled &&
            Surface.gameObject.activeInHierarchy;

        internal Lane(PlayerMoveZone zone, MeshCollider surface, LaneFrame frame, Bounds casing)
        {
            Zone = zone;
            Surface = surface;
            Frame = frame;
            Casing = casing;
        }

        internal bool FloorBelow(Vector3 point, out RaycastHit hit)
        {
            float side = Vector3.Dot(point - Frame.Center, Frame.Right);
            point += Frame.Right * (Mathf.Clamp(side, -Frame.HalfWidth + 0.02f, Frame.HalfWidth - 0.02f) - side);
            return Surface.Raycast(new Ray(point + Vector3.up * 0.1f, Vector3.down), out hit, 2.8f) && hit.normal.y > 0.8f;
        }
    }

    private void Awake()
    {
        enabledSetting = Config.Bind("General", "Enabled", true,
            "Having this enabled automatically loops you around on the airport escalators.");
        SceneManager.sceneLoaded += SceneLoaded;
        ResetScene();
        Logger.LogMessage($"Plugin {Name} {Version} is loaded!");
    }

    private void OnDestroy() => SceneManager.sceneLoaded -= SceneLoaded;
    private void SceneLoaded(Scene scene, LoadSceneMode mode) => ResetScene();

    private void ResetScene()
    {
        lanes.Clear();
        ResetRider();
        faulted = false;
        nextTransfer = 0;
        nextScan = Time.unscaledTime + 1;
    }

    private void ResetRider()
    {
        riding = null;
        rider = null;
        turn = null;
    }

    private void FixedUpdate()
    {
        if (!enabledSetting.Value || faulted) { ResetRider(); return; }
        try
        {
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 5;
                DiscoverLanes();
            }
            if (lanes.Count > 0) Tick();
        }
        catch (Exception error)
        {
            faulted = true;
            ResetRider();
            Logger.LogError($"EndlessEscalator stopped for this scene: {error}");
        }
    }

    private void DiscoverLanes()
    {
        lanes.Clear();
        ResetRider();
        foreach (PlayerMoveZone zone in FindObjectsByType<PlayerMoveZone>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            Lane? lane = CreateLane(zone);
            if (lane != null) lanes.Add(lane);
        }
        foreach (Lane lane in lanes)
        {
            Lane[] matches = lanes.Where(other => other != lane &&
                other.Zone.gameObject.scene == lane.Zone.gameObject.scene &&
                LaneFrame.AreNeighbors(lane.Frame, other.Frame)).Take(2).ToArray();
            lane.Other = matches.Length == 1 ? matches[0] : null;
        }
        lanes.RemoveAll(lane => lane.Other == null || lane.Other.Other != lane);
        if (lanes.Count == 0) return;
        nextScan = float.PositiveInfinity;
        Logger.LogInfo($"Linked {lanes.Count / 2} airport walkway pair(s).");
    }

    private static Lane? CreateLane(PlayerMoveZone zone)
    {
        Transform parent = zone.transform.parent;
        if (!zone.isActiveAndEnabled || !parent || !parent.name.StartsWith("Conveyor", StringComparison.Ordinal) ||
            !HasAirportAncestor(parent)) return null;
        MeshCollider? belt = parent.GetComponentsInChildren<MeshCollider>().FirstOrDefault(collider =>
            collider.gameObject.name == "conveyor" && collider.enabled && !collider.isTrigger &&
            collider.sharedMesh && collider.sharedMesh.name == "Conveyor");
        if (!belt) return null;
        Bounds force = zone.zoneBounds, physical = belt!.bounds;
        if (physical.size.y > 0.3f || Math.Abs(physical.center.x - force.center.x) > 0.25f ||
            Math.Abs(physical.center.z - force.center.z) > 0.25f) return null;

        Vector3 low = Vector3.Max(force.min, physical.min), high = Vector3.Min(force.max, physical.max);
        low.y = force.min.y;
        high.y = force.max.y;
        if (low.x >= high.x || low.z >= high.z) return null;
        Bounds casing = physical;
        foreach (Collider collider in parent.GetComponentsInChildren<Collider>())
            if (collider.enabled && !collider.isTrigger) casing.Encapsulate(collider.bounds);
        float forceValue = zoneForce?.GetValue(zone) is float value ? value : 20f;
        return LaneFrame.TryCreate(new Bounds((low + high) * 0.5f, high - low), zone.forceDirection * (forceValue < 0 ? -1 : 1), out LaneFrame frame)
            ? new Lane(zone, belt, frame, casing) : null;
    }

    private static bool HasAirportAncestor(Transform transform)
    {
        for (Transform current = transform; current; current = current.parent)
            if (current.name == "BL_Airport") return true;
        return false;
    }

    private void Tick()
    {
        Character character = Character.localCharacter;
        if (!character || !character.data) { ResetRider(); return; }
        bool allowStep = turn.HasValue && HasSupport(character.Center, character.data.groundPos.y);
        if (!LocalTransfer.CanRide(character, allowStep)) { ResetRider(); return; }
        Vector3 center = character.Center;
        Lane? current = null;
        foreach (Lane lane in lanes)
        {
            if (!lane.Live || lane.Other == null || !lane.Other.Live) { ResetScene(); return; }
            if (lane.Force != 0 && Vector3.Dot(lane.Frame.Forward,
                lane.Zone.forceDirection * (lane.Force < 0 ? -1 : 1)) < 0)
            { ResetScene(); return; }
        }
        if (turn.HasValue)
        {
            if (rider != character || !allowStep) { ResetRider(); return; }
            FollowTurn(character);
            return;
        }
        foreach (Lane lane in lanes)
        {
            if (lane.Frame.ContainsRider(center) && lane.FloorBelow(center, out RaycastHit hit) &&
                Math.Abs(character.data.groundPos.y - hit.point.y) < 0.22f)
            {
                current = lane;
                break;
            }
        }
        if (current == null) { ResetRider(); return; }
        if (riding != current || rider != character)
        {
            riding = current;
            rider = character;
            ridingSince = Time.fixedTime;
            lastRidePosition = center;
        }
        float elapsed = Time.fixedTime - ridingSince;
        if (elapsed >= 0.3f && current.Frame.Along(center) < current.Frame.HalfLength - 2.5f &&
            Math.Abs(Vector3.Dot(center - current.Frame.Center, current.Frame.Right)) < current.Frame.HalfWidth - 0.3f)
            current.Speed.Observe(Vector3.Dot(center - lastRidePosition, current.Frame.Forward) / Time.fixedDeltaTime,
                current.Force, Time.fixedDeltaTime);
        lastRidePosition = center;
        if (Time.fixedTime < nextTransfer || Time.fixedTime - ridingSince < 0.3f ||
            current.Frame.Along(center) < current.Frame.HalfLength - 2.5f || current.Speed.AtForce(current.Force) <= 0) return;

        Bounds casing = current.Casing;
        casing.Encapsulate(current.Other!.Casing);
        turn = new TurnPath(current.Frame, current.Other.Frame, casing, center,
            TurnPath.Clearance(current.Speed.AtForce(current.Force)));
        turnDistance = progressDistance = 0;
        lastProgressAt = Time.fixedTime;
        FollowTurn(character);
    }

    private void FollowTurn(Character character)
    {
        TurnPath path = turn!.Value;
        float speed = riding!.Speed.AtForce(riding.Force);
        Vector3 center = character.Center;
        turnDistance = Mathf.Max(turnDistance, path.ClosestDistance(center));
        Vector3 nearest = path.PointAt(turnDistance);
        nearest.y = center.y;
        if (speed == 0) lastProgressAt = Time.fixedTime;
        if ((nearest - center).sqrMagnitude > 1.5f * 1.5f || Time.fixedTime - lastProgressAt > 2.5f)
        {
            ResetRider();
            nextTransfer = Time.fixedTime + 1;
            return;
        }
        if (turnDistance > progressDistance + Mathf.Min(0.1f, speed * 0.5f))
        {
            progressDistance = turnDistance;
            lastProgressAt = Time.fixedTime;
        }
        Lane target = riding!.Other!;
        if (turnDistance >= path.Length - 0.2f && target.FloorBelow(center, out _))
        {
            turn = null;
            riding = target;
            ridingSince = Time.fixedTime;
            lastRidePosition = center;
            nextTransfer = Time.fixedTime + 0.75f;
            return;
        }
        Vector3 ahead = path.PointAt(Mathf.Min(path.Length, turnDistance + Mathf.Clamp(speed * Time.fixedDeltaTime * 2, 0.4f, 0.8f)));
        ahead.y = center.y;
        Vector3 offset = ahead - center;
        if (!HasSupport(ahead, character.data.groundPos.y) ||
            !transfer.TryDrive(character, offset.normalized * Mathf.Min(speed, offset.magnitude / Time.fixedDeltaTime), true))
        {
            ResetRider();
            nextTransfer = Time.fixedTime + 1;
        }
    }

    private bool HasSupport(Vector3 center, float floorY)
    {
        int count = Physics.RaycastNonAlloc(center + Vector3.up * 0.1f, Vector3.down,
            groundHits, 2.8f, ~0, QueryTriggerInteraction.Ignore);
        if (count == groundHits.Length) return false;
        for (int i = 0; i < count; i++)
        {
            RaycastHit hit = groundHits[i];
            if (!hit.collider.attachedRigidbody && hit.normal.y > 0.8f &&
                Math.Abs(hit.point.y - floorY) < 0.5f) return true;
        }
        return false;
    }
}
