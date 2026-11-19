using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class Billboard : UdonSharpBehaviour
{
    [Tooltip("Lock rotation around the X axis")]
    public bool xAxisLocked = false;

    [Tooltip("Lock rotation around the Y axis")]
    public bool yAxisLocked = false;

    private Transform selfTransform;
    private Quaternion initialRotation;

    void Start()
    {
        selfTransform = transform;
        initialRotation = transform.rotation;
    }

    void Update()
    {
        VRCPlayerApi localPlayer = Networking.LocalPlayer;
        if (!Utilities.IsValid(localPlayer))
        {
            return;
        }

        VRCPlayerApi.TrackingData head = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
        if (xAxisLocked && yAxisLocked)
        {
            selfTransform.rotation = initialRotation;
            return;
        }
        if (xAxisLocked)
        {
            Vector3 target = head.position;
            target.y = selfTransform.position.y;
            selfTransform.LookAt(target);
        }
        else if (yAxisLocked)
        {
            Vector3 target = head.position;
            target.x = selfTransform.position.x;
            target.z = selfTransform.position.z;
            selfTransform.LookAt(target);
        }
        else
        {
            selfTransform.LookAt(head.position);
        }
        selfTransform.Rotate(0f, 180f, 0f);
    }
}