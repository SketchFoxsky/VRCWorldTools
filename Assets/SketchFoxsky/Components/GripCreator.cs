using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class GripCreator : MonoBehaviour
{
    [Header("Grip Offsets")]
    public Vector3 localPositionOffset;
    public Vector3 localRotationOffset;

    [Header("Gizmo")]
    public float gizmoSize = 0.05f;

#if UNITY_EDITOR
    public void CreateGrip()
    {
        // Remove existing Grip children
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform child = transform.GetChild(i);
            if (child.name == "Grip")
            {
                DestroyImmediate(child.gameObject);
            }
        }

        // Create new Grip
        GameObject grip = new GameObject("Grip");
        grip.transform.SetParent(transform, false);

        // Apply local offsets
        grip.transform.localPosition = localPositionOffset;
        grip.transform.localRotation = Quaternion.Euler(localRotationOffset);
    }

    private void OnDrawGizmos()
    {
        // Build grip transform in parent space
        Matrix4x4 gripMatrix =
            transform.localToWorldMatrix *
            Matrix4x4.TRS(
                localPositionOffset,
                Quaternion.Euler(localRotationOffset),
                Vector3.one
            );

        Gizmos.matrix = gripMatrix;

        // Draw axes
        Gizmos.color = Color.red;    // X
        Gizmos.DrawLine(Vector3.zero, Vector3.right * gizmoSize);

        Gizmos.color = Color.green;  // Y
        Gizmos.DrawLine(Vector3.zero, Vector3.up * gizmoSize);

        Gizmos.color = Color.blue;   // Z
        Gizmos.DrawLine(Vector3.zero, Vector3.forward * gizmoSize);

        // Reset
        Gizmos.matrix = Matrix4x4.identity;
    }
#endif
}
