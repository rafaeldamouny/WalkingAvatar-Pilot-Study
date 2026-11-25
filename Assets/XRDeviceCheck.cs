using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class XRDeviceCheck : MonoBehaviour
{
    void Start()
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevices(devices);

        Debug.Log("=== XR Devices Detected ===");
        foreach (var d in devices)
            Debug.Log($"{d.name} | {d.characteristics}");

        var left = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, left);
        Debug.Log($"LeftHand count: {left.Count}");

        var right = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, right);
        Debug.Log($"RightHand count: {right.Count}");
    }
}