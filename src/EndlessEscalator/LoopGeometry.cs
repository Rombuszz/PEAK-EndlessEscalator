using System;
using UnityEngine;

namespace EndlessEscalator;

internal readonly struct LaneFrame
{
    internal readonly Vector3 Center;
    internal readonly Vector3 Forward;
    internal readonly float HalfLength;
    internal readonly float HalfWidth;
    internal readonly float HalfHeight;
    internal Vector3 Right => Vector3.Cross(Vector3.up, Forward);

    internal LaneFrame(Vector3 center, Vector3 forward, float halfLength, float halfWidth, float halfHeight)
    {
        Center = center;
        Forward = forward;
        HalfLength = halfLength;
        HalfWidth = halfWidth;
        HalfHeight = halfHeight;
    }

    internal static bool TryCreate(Bounds bounds, Vector3 force, out LaneFrame lane)
    {
        lane = default;
        if (!Finite(bounds.center) || !Finite(bounds.extents) || !Finite(force) ||
            force.sqrMagnitude < 0.01f || Math.Abs(force.y) > 0.05f * force.magnitude)
            return false;
        Vector3 forward = new Vector3(force.x, 0, force.z).normalized;
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        if (Math.Max(Math.Abs(forward.x), Math.Abs(forward.z)) < 0.999f) return false;
        float length = Extent(bounds.extents, forward);
        float width = Extent(bounds.extents, right);
        if (length < 3 || length > 80 || width < 0.4f || width > 3 || length < width * 3 ||
            bounds.extents.y < 0.25f || bounds.extents.y > 4) return false;
        lane = new LaneFrame(bounds.center, forward, length, width, bounds.extents.y);
        return true;
    }

    internal float Along(Vector3 point) => Vector3.Dot(point - Center, Forward);
    internal bool ContainsRider(Vector3 point) => Finite(point) &&
        Math.Abs(Along(point)) <= HalfLength + 0.1f &&
        Math.Abs(Vector3.Dot(point - Center, Right)) <= HalfWidth + 0.1f &&
        Math.Abs(point.y - Center.y) <= HalfHeight;

    internal static bool AreNeighbors(LaneFrame a, LaneFrame b)
    {
        if (Vector3.Dot(a.Forward, b.Forward) > -0.999f || Math.Abs(a.Center.y - b.Center.y) > 0.4f)
            return false;
        Vector3 delta = b.Center - a.Center;
        float lateral = Math.Abs(Vector3.Dot(delta, a.Right));
        float axial = Math.Abs(Vector3.Dot(delta, a.Forward));
        return lateral > a.HalfWidth + b.HalfWidth && lateral < a.HalfWidth + b.HalfWidth + 4 &&
            axial < 1.5f && Math.Abs(a.HalfLength - b.HalfLength) < 1.5f;
    }

    internal static Vector3 Destination(LaneFrame target, float riderY, float inset)
    {
        Vector3 point = target.Center + target.Forward * (-target.HalfLength + inset);
        point.y = riderY;
        return point;
    }

    internal static Vector3 Redirect(Vector3 velocity, Vector3 from, Vector3 to)
    {
        float along = Vector3.Dot(velocity, from);
        return velocity - from * along + to * along;
    }

    internal static bool Finite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static float Extent(Vector3 e, Vector3 axis) =>
        Math.Abs(axis.x) * e.x + Math.Abs(axis.y) * e.y + Math.Abs(axis.z) * e.z;
}

internal readonly struct TurnPath
{
    private readonly Vector3 start, centered, arcStart, arcEnd, end, forward, across;
    private readonly float centeringLength, outboundLength, radius, arcLength, inboundLength;
    internal float Length => centeringLength + outboundLength + arcLength + inboundLength;
    internal static float Clearance(float speed) => Math.Max(1f, speed * 0.5f);

    internal TurnPath(LaneFrame source, LaneFrame target, Bounds casing, Vector3 start, float clearance = 1f)
    {
        this.start = start;
        forward = source.Forward;
        Vector3 separation = target.Center - source.Center;
        separation -= forward * Vector3.Dot(separation, forward);
        separation.y = 0;
        across = separation.normalized;
        radius = separation.magnitude * 0.5f;
        float farEnd = Vector3.Dot(casing.center - source.Center, forward) +
            Math.Abs(forward.x) * casing.extents.x + Math.Abs(forward.z) * casing.extents.z;
        centered = source.Center + forward * Math.Max(source.Along(start), source.HalfLength - 2.5f);
        arcStart = source.Center + forward * (farEnd + clearance);
        arcEnd = arcStart + separation;
        end = LaneFrame.Destination(target, start.y, 1.5f);
        centered.y = arcStart.y = arcEnd.y = start.y;
        centeringLength = (centered - start).magnitude;
        outboundLength = (arcStart - centered).magnitude;
        arcLength = (float)Math.PI * radius;
        inboundLength = (end - arcEnd).magnitude;
    }

    internal Vector3 PointAt(float distance)
    {
        distance = Mathf.Clamp(distance, 0, Length);
        if (distance < centeringLength) return Vector3.Lerp(start, centered, distance / centeringLength);
        distance -= centeringLength;
        if (distance < outboundLength) return Vector3.Lerp(centered, arcStart, distance / outboundLength);
        distance -= outboundLength;
        if (distance < arcLength)
        {
            float angle = distance / radius;
            return arcStart + across * (radius * (1f - (float)Math.Cos(angle))) +
                forward * (radius * (float)Math.Sin(angle));
        }
        return Vector3.Lerp(arcEnd, end, (distance - arcLength) / inboundLength);
    }

    internal Vector3 DirectionAt(float distance)
    {
        distance = Mathf.Clamp(distance, 0, Length);
        if (distance < centeringLength) return (centered - start).normalized;
        distance -= centeringLength;
        if (distance < outboundLength) return forward;
        distance -= outboundLength;
        if (distance >= arcLength) return -forward;
        float angle = distance / radius;
        return across * (float)Math.Sin(angle) + forward * (float)Math.Cos(angle);
    }

    internal float ClosestDistance(Vector3 point)
    {
        float bestDistance = 0, bestError = float.PositiveInfinity;
        ClosestSegment(point, start, centered, 0, ref bestDistance, ref bestError);
        ClosestSegment(point, centered, arcStart, centeringLength, ref bestDistance, ref bestError);
        Vector3 offset = point - (arcStart + across * radius);
        float outward = Vector3.Dot(offset, forward), lateral = Vector3.Dot(offset, across);
        float angle = outward > 0 ? (float)Math.Atan2(outward, -lateral) : lateral > 0 ? (float)Math.PI : 0;
        float arcDistance = centeringLength + outboundLength + angle * radius;
        float error = (point - PointAt(arcDistance)).sqrMagnitude;
        if (error < bestError) { bestDistance = arcDistance; bestError = error; }
        ClosestSegment(point, arcEnd, end, centeringLength + outboundLength + arcLength,
            ref bestDistance, ref bestError);
        return bestDistance;
    }

    private static void ClosestSegment(Vector3 point, Vector3 a, Vector3 b, float offset,
        ref float bestDistance, ref float bestError)
    {
        Vector3 delta = b - a;
        float fraction = delta.sqrMagnitude > 0 ? Mathf.Clamp01(Vector3.Dot(point - a, delta) / delta.sqrMagnitude) : 0;
        float error = (point - a - delta * fraction).sqrMagnitude;
        if (error >= bestError) return;
        bestError = error;
        bestDistance = offset + delta.magnitude * fraction;
    }
}
