using UnityEngine;
using VRC.SDK3.Components;

public class PickupSetup : MonoBehaviour
{
    public VRCPickup pickup;
    public Transform pickupTarget;

    private void OnValidate()
    {
        pickup = GetComponent<VRCPickup>();
        pickupTarget = FindGripChild(transform);

        if (pickup != null && pickupTarget != null)
        {
            pickup.ExactGrip = pickupTarget;
            pickup.ExactGun = pickupTarget;
        }
    }

    private Transform FindGripChild(Transform parent)
    {
        foreach (Transform child in parent)
        {
            if (child.name == "Grip")
                return child;

            // Recursive search
            Transform found = FindGripChild(child);
            if (found != null)
                return found;
        }

        return null;
    }
}
