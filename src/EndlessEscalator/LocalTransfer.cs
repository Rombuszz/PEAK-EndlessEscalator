using System.Collections.Generic;
using UnityEngine;

namespace EndlessEscalator;

internal sealed class LocalTransfer
{
    private readonly List<Rigidbody> bodies = new(24);
    private readonly HashSet<Rigidbody> bodySet = new();
    private readonly List<Joint> joints = new(24);

    internal static bool CanRide(Character c, bool allowStep = false)
    {
        if (!c || c != Character.localCharacter || !c.photonView ||
            !c.photonView.IsMine || Character.observedCharacter != c || c.refs == null ||
            !c.refs.ragdoll || !c.refs.hip || !c.refs.hip.Rig ||
            c.refs.ragdoll.partList == null || c.refs.ragdoll.partList.Count == 0 ||
            !c.data || !c.input || c.warping) return false;

        CharacterData data = c.data;
        return !data.dead && !data.passedOut && !data.fullyPassedOut && !data.shouldPetrify &&
            !data.isKinecmatic && !data.isJumping && !data.isClimbingAnything &&
            !data.currentClimbHandle && !data.isCarried && !data.carrier && !data.carriedPlayer &&
            !data.grabJoint && data.currentRagdollControll >= 0.8f &&
            (data.isGrounded || data.sinceGrounded < (allowStep ? 0.5f : 0.12f)) &&
            (!allowStep || data.avarageVelocity.y > -2f) &&
            !c.input.jumpWasPressed && !c.input.jumpIsPressed &&
            c.input.movementInput.sqrMagnitude <= 0.01f &&
            LaneFrame.Finite(data.avarageVelocity) && LaneFrame.Finite(data.avarageLastFrameVelocity);
    }

    internal bool TryDrive(Character c, Vector3 desiredVelocity, bool allowStep = false)
    {
        if (!CanRide(c, allowStep) || !LaneFrame.Finite(desiredVelocity)) return false;
        bodies.Clear();
        bodySet.Clear();
        Vector3 average = Vector3.zero;
        foreach (Bodypart part in c.refs.ragdoll.partList)
        {
            if (!part || !LaneFrame.Finite(part.forcesToAdd) || !Capture(part.Rig)) return false;
            average += part.Rig.linearVelocity;
        }
        average /= c.refs.ragdoll.partList.Count;
        Item held = c.data.currentItem;
        if (held && (!held.photonView || !held.photonView.IsMine || !Capture(held.rig))) return false;
        if (!InternalJoints(c) || (held && !InternalJoints(held))) return false;

        desiredVelocity.y = 0;
        Vector3 correction = desiredVelocity - new Vector3(average.x, 0, average.z);
        foreach (Rigidbody body in bodies)
        {
            body.linearVelocity += correction;
            body.WakeUp();
        }
        c.data.avarageVelocity = average + correction;
        return true;
    }

    private bool Capture(Rigidbody body)
    {
        if (!body || body.isKinematic || !LaneFrame.Finite(body.linearVelocity)) return false;
        if (bodySet.Add(body)) bodies.Add(body);
        return true;
    }

    private bool InternalJoints(Component owner)
    {
        owner.GetComponentsInChildren(true, joints);
        foreach (Joint joint in joints)
            if (joint && (!joint.connectedBody || !bodySet.Contains(joint.connectedBody))) return false;
        return true;
    }
}
